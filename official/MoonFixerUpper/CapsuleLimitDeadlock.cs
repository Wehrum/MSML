using System;
using BepInEx.Configuration;
using HarmonyLib;
using MoonCleaners;

namespace MSML.MoonFixerUpper;

/// <summary>
/// Fixes "Could not pack a capsule" freezing whole factories.
/// <para>
/// The game limits how many capsules a moon can hold (<c>MoonSettings.maxDroppedCanisters</c>, 10,000), counting
/// every capsule riding a belt. A big belt network reaches it. Then stone presses and pressure chambers can't pack
/// their product (MoonWorld.EmitProduct -> EmitCargo fails with "Could not pack a capsule"), so they stop taking
/// capsules in, their input belts back up through the splitters and mergers, and nothing removes capsules any more:
/// the moon stays at the limit and every processor stays stuck, permanently.
/// </para>
/// <para>
/// Processors only turn capsules they already took in (and removed from the moon) into new ones, so letting them
/// pack past the limit can't make the capsule count run away. While a processor packs its product, the limit is
/// lifted; everything else that makes capsules keeps the game's limit.
/// </para>
/// </summary>
internal static class CapsuleLimitDeadlock
{
    private const int GameHardLimit = 100_000;   // EmitCargo also caps at min(100,000, the setting)

    private static ConfigEntry<bool> _enabled = null!;
    private static int _saved = -1;
    private static bool _reported;

    public static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Fixes", "ProcessorsIgnoreCapsuleLimit", true,
            "Fixes 'Could not pack a capsule': stone presses and pressure chambers may pack their product even when the " +
            "moon holds the most capsules allowed (10,000, including capsules on belts). Without it, reaching the limit " +
            "freezes every processor and backs up all belts for good.");
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitProduct))]
    [HarmonyPrefix]
    private static void BeforeEmitProduct(MoonWorld __instance)
    {
        var settings = __instance.settings;
        if (!_enabled.Value || settings == null) return;
        _saved = settings.maxDroppedCanisters;
        if (!_reported && __instance.canisters != null && __instance.canisters.Count >= _saved)
        {
            _reported = true;
            Plugin.Logger.LogInfo($"The moon holds {__instance.canisters.Count} capsules (limit {_saved}); processors may pack past it so the belts keep moving.");
        }
        settings.maxDroppedCanisters = GameHardLimit;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitProduct))]
    [HarmonyFinalizer]
    private static Exception? AfterEmitProduct(MoonWorld __instance, Exception? __exception)
    {
        if (_saved >= 0 && __instance.settings != null) __instance.settings.maxDroppedCanisters = _saved;
        _saved = -1;
        return __exception;
    }
}
