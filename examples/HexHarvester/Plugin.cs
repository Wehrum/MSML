using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.HexHarvester;

/// <summary>
/// Adds the Hex Harvester as a new entry in the game's vehicle catalog. Everything else (driving, physics, cleaning,
/// hopper, unloading, lights, audio) is the game's own vehicle code; the mod only supplies the catalog entry, a model
/// built from the Mega Vacuum Rover's parts, and maps the game's per-vehicle lookups to sensible values.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "moonsweepmods.hexharvester";
    public const string Name = "Hex Harvester";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger = null!;

    /// <summary>Index of the Hex Harvester in MoonPrototypeCatalog.Vehicles, or -1 before registration.</summary>
    public static int Index { get; private set; } = -1;

    /// <summary>Stock vehicle whose prefab, sounds and per-vehicle data the harvester builds on.</summary>
    public static int BaseIndex => MoonPrototypeCatalog.MegaVacuumRover;

    public const float CleaningWidth = 25f;     // one hex (MoonSettings.regionDiameter)
    public const double HopperKg = 9000;         // Mega Vacuum Rover: 3000
    public const float Heft = 1.1f;              // Mega Vacuum Rover: 0.55

    public override void Load()
    {
        Logger = Log;
        Register();
        new Harmony(Guid).PatchAll(typeof(CatalogPatches));
        AddComponent<HarvesterRuntime>();
    }

    private static void Register()
    {
        var vehicles = MoonPrototypeCatalog.Vehicles;
        for (var i = 0; i < vehicles.Length; i++)
        {
            if (vehicles[i].name == "Hex harvester") { Index = i; return; }
        }
        var basis = vehicles[BaseIndex];
        var item = MoonPrototypeCatalog.V("Hex harvester", MoonPrototypeCatalog.Work.Clean, 4.5f, 40f, 8, CleaningWidth);
        item.hint = basis.hint;
        item.depth = basis.depth;
        item.reach = basis.reach;

        var extended = new Il2CppReferenceArray<MoonPrototypeCatalog.Item>(vehicles.Length + 1);
        for (var i = 0; i < vehicles.Length; i++) extended[i] = vehicles[i];
        extended[vehicles.Length] = item;
        MoonPrototypeCatalog.Vehicles = extended;
        Index = vehicles.Length;
        Logger.LogInfo($"Registered '{item.name}' as vehicle #{Index} (width {item.width} m, speed {item.speed}, cargo {item.capacity}).");
    }
}

/// <summary>Per-vehicle lookups the game does by catalog index.</summary>
[HarmonyPatch]
internal static class CatalogPatches
{
    private static bool Ours(int index) => index == Plugin.Index && Plugin.Index >= 0;

    [HarmonyPatch(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.AuthoredPrefab))]
    [HarmonyPrefix]
    private static void AuthoredPrefab(ref int index) { if (Ours(index)) index = Plugin.BaseIndex; }

    [HarmonyPatch(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.StorageKg))]
    [HarmonyPrefix]
    private static bool StorageKg(int index, ref double __result)
    {
        if (!Ours(index)) return true;
        __result = Plugin.HopperKg;
        return false;
    }

    [HarmonyPatch(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.Heft))]
    [HarmonyPrefix]
    private static bool Heft(int index, ref float __result)
    {
        if (!Ours(index)) return true;
        __result = Plugin.Heft;
        return false;
    }

    // Build the Mega Vacuum Rover's model, then turn it into the harvester.
    [HarmonyPatch(typeof(MoonPrototypeModels), nameof(MoonPrototypeModels.Vehicle))]
    [HarmonyPrefix]
    private static void VehiclePrefix(ref int index, out bool __state)
    {
        __state = Ours(index);
        if (__state) index = Plugin.BaseIndex;
    }

    [HarmonyPatch(typeof(MoonPrototypeModels), nameof(MoonPrototypeModels.Vehicle))]
    [HarmonyPostfix]
    private static void VehiclePostfix(MoonPrototypeModels __instance, GameObject __result, bool __state)
    {
        if (!__state || __result == null) return;
        try { HarvesterModel.Build(__result, __instance); }
        catch (Exception e) { Plugin.Logger.LogError("Building the Hex Harvester model failed: " + e); }
    }

    // Same engine, suction and brush sounds as the Mega Vacuum Rover.
    [HarmonyPatch(typeof(MoonVehicleAudio), nameof(MoonVehicleAudio.Voice))]
    [HarmonyPrefix]
    private static void Voice(ref int vehicleIndex) { if (Ours(vehicleIndex)) vehicleIndex = Plugin.BaseIndex; }

    // The engine/drive mix looks up per-vehicle levels by catalog index in native code and silently gets 0 for a new
    // index. While the audio updates, present the harvester as its base vehicle.
    [HarmonyPatch(typeof(MoonVehicleAudio), nameof(MoonVehicleAudio.LateUpdate))]
    [HarmonyPrefix]
    private static void AudioBefore(MoonVehicleAudio __instance, out bool __state)
    {
        var v = __instance.vehicle;
        __state = v != null && Ours(v._Index_k__BackingField);
        if (__state) v!._Index_k__BackingField = Plugin.BaseIndex;
    }

    [HarmonyPatch(typeof(MoonVehicleAudio), nameof(MoonVehicleAudio.LateUpdate))]
    [HarmonyPostfix]
    private static void AudioAfter(MoonVehicleAudio __instance, bool __state)
    {
        if (__state && __instance.vehicle != null) __instance.vehicle._Index_k__BackingField = Plugin.Index;
    }

    // Make sure the hopper and model data are right even if the game inlined a lookup we patched.
    [HarmonyPatch(typeof(MoonPrototypeVehicle), nameof(MoonPrototypeVehicle.Initialize))]
    [HarmonyPostfix]
    private static void Initialized(MoonPrototypeVehicle __instance, int index, string storageId)
    {
        if (!Ours(index)) return;
        try
        {
            if (__instance.authored == null)
            {
                var view = __instance.GetComponentInChildren<MoonAuthoredVehicleView>(true);
                if (view != null) { __instance.authored = view; view.vehicle = __instance; Plugin.Logger.LogInfo("Linked Hex Harvester to its model view."); }
            }
            var world = __instance.World;
            if (world != null && world.VehicleDustCapacity(storageId) < Plugin.HopperKg) world.RaiseVehicleCapacity(storageId, Plugin.HopperKg);
            Plugin.Logger.LogInfo($"Hex Harvester ready: storage {storageId}, cleaning width {__instance.CleaningWidth} m, " +
                $"hopper {__instance.DustCapacityKg} kg, mass {__instance.body?.mass}, top speed {__instance.MaximumSpeed}, wheels {__instance.WheelCount}.");
        }
        catch (Exception e) { Plugin.Logger.LogError(e); }
    }
}
