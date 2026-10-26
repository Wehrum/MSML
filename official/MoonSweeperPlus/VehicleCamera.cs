using System;
using BepInEx.Configuration;
using HarmonyLib;
using MoonCleaners;

namespace MSML.MoonSweeperPlus;

/// <summary>
/// Stops the vehicle camera snapping back behind the vehicle after you look around while driving.
/// <para>
/// The game (MoonWalker.LateUpdate) swings the camera back once the vehicle is moving faster than 1.5 m/s and 1.2 s
/// have passed since <c>vehicleCameraTurnedAt</c>, which it sets whenever you move the mouse. Those numbers are
/// compiled in, so instead this holds that timestamp: forever (auto recenter off) or for the chosen delay.
/// </para>
/// </summary>
internal static class VehicleCamera
{
    private const float GameDelay = 1.2f;
    private const float Never = 1_000_000f;

    private static ConfigEntry<bool> _autoRecenter = null!;
    private static ConfigEntry<float> _delay = null!;

    // When the player last turned the camera (the game's own timestamp), and the value written over it, so the
    // game's writes (a new look, or mounting a vehicle) can be told apart from ours.
    private static float _lookedAt = -99f;
    private static float _written = float.NaN;

    public static void Bind(ConfigFile config)
    {
        _autoRecenter = config.Bind("Vehicle camera", "AutoRecenter", false,
            "Swing the camera back behind the vehicle after you look around while driving. Off keeps the camera where you point it.");
        _delay = config.Bind("Vehicle camera", "RecenterDelay", 5f, new ConfigDescription(
            "Seconds after you stop moving the mouse before the camera swings back (when auto recenter is on). The game's own delay is 1.2.",
            new AcceptableValueRange<float>(GameDelay, 30f), ModSettings.Step(0.5)));
    }

    [HarmonyPatch(typeof(MoonWalker), nameof(MoonWalker.LateUpdate))]
    [HarmonyPrefix]
    private static void BeforeLateUpdate(MoonWalker __instance)
    {
        try
        {
            if (__instance.mountedVehicle == null)
            {
                _written = float.NaN;
                return;
            }
            var turnedAt = __instance.vehicleCameraTurnedAt;
            if (turnedAt != _written) _lookedAt = turnedAt;

            // The game recenters when (now - turnedAt) > 1.2 s, so moving turnedAt forward delays that.
            var hold = _autoRecenter.Value ? Math.Max(0f, _delay.Value - GameDelay) : Never;
            _written = _lookedAt + hold;
            __instance.vehicleCameraTurnedAt = _written;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }
}
