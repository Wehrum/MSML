using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Makes the game treat modded vehicles like stock ones. Per-vehicle lookups by catalog index either answer for the
/// modded vehicle or are redirected to its base vehicle; the base vehicle's model is built and then handed to the
/// mod; garage offers and icons are added; the driver is hidden where asked. Saving is in <see cref="VehicleSaves"/>.
/// </summary>
internal static class VehiclePatches
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;
        var harmony = new Harmony("msml.api.vehicles");
        harmony.PatchAll(typeof(VehiclePatches));
        harmony.PatchAll(typeof(VehicleSaves));
        Crew.Apply();
    }

    private static ModVehicle? Modded(int index) => Vehicles.ByIndex(index);

    // ---- catalog lookups -----------------------------------------------------------------------------

    [HarmonyPatch(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.AuthoredPrefab))]
    [HarmonyPrefix]
    private static void AuthoredPrefab(ref int index)
    {
        if (Modded(index) is { } v) index = v.BaseVehicle;
    }

    [HarmonyPatch(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.StorageKg))]
    [HarmonyPrefix]
    private static bool StorageKg(int index, ref double __result)
    {
        if (Modded(index) is not { } v) return true;
        __result = v.HopperKg;
        return false;
    }

    [HarmonyPatch(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.Heft))]
    [HarmonyPrefix]
    private static bool Heft(ref int index, ref float __result)
    {
        if (Modded(index) is not { } v) return true;
        if (v.Heft is not { } heft) { index = v.BaseVehicle; return true; }
        __result = heft;
        return false;
    }

    // ---- model: build the base vehicle's, then let the mod change it ---------------------------------

    [HarmonyPatch(typeof(MoonPrototypeModels), nameof(MoonPrototypeModels.Vehicle))]
    [HarmonyPrefix]
    private static void ModelPrefix(ref int index, out ModVehicle? __state)
    {
        __state = Modded(index);
        if (__state != null) index = __state.BaseVehicle;
    }

    [HarmonyPatch(typeof(MoonPrototypeModels), nameof(MoonPrototypeModels.Vehicle))]
    [HarmonyPostfix]
    private static void ModelPostfix(MoonPrototypeModels __instance, GameObject __result, ModVehicle? __state)
    {
        if (__state?.BuildModel == null || __result == null) return;
        try { __state.BuildModel(__result, __instance); }
        catch (Exception e) { Log.Error($"Building the model of {__state} failed: {e}"); }
    }

    // ---- audio: the base vehicle's sounds ------------------------------------------------------------

    [HarmonyPatch(typeof(MoonVehicleAudio), nameof(MoonVehicleAudio.Voice))]
    [HarmonyPrefix]
    private static void Voice(ref int vehicleIndex)
    {
        if (Modded(vehicleIndex) is { } v) vehicleIndex = v.BaseVehicle;
    }

    // The engine mix looks up per-vehicle levels by index in native code (and gets silence for a new index), so
    // present the vehicle as its base while the audio updates.
    [HarmonyPatch(typeof(MoonVehicleAudio), nameof(MoonVehicleAudio.LateUpdate))]
    [HarmonyPrefix]
    private static void AudioBefore(MoonVehicleAudio __instance, out int __state)
    {
        __state = -1;
        var vehicle = __instance.vehicle;
        if (vehicle == null || Modded(vehicle._Index_k__BackingField) is not { } v) return;
        __state = vehicle._Index_k__BackingField;
        vehicle._Index_k__BackingField = v.BaseVehicle;
    }

    [HarmonyPatch(typeof(MoonVehicleAudio), nameof(MoonVehicleAudio.LateUpdate))]
    [HarmonyPostfix]
    private static void AudioAfter(MoonVehicleAudio __instance, int __state)
    {
        if (__state >= 0 && __instance.vehicle != null) __instance.vehicle._Index_k__BackingField = __state;
    }

    // ---- setup in the world --------------------------------------------------------------------------

    [HarmonyPatch(typeof(MoonPrototypeVehicle), nameof(MoonPrototypeVehicle.Initialize))]
    [HarmonyPostfix]
    private static void Initialized(MoonPrototypeVehicle __instance, int index, string storageId)
    {
        if (Modded(index) is not { } v) return;
        try
        {
            if (__instance.authored == null)
            {
                var view = __instance.GetComponentInChildren<MoonAuthoredVehicleView>(true);
                if (view != null) { __instance.authored = view; view.vehicle = __instance; }
            }
            var world = __instance.World;
            if (world != null && world.VehicleDustCapacity(storageId) < v.HopperKg) world.RaiseVehicleCapacity(storageId, v.HopperKg);
            v.Ready?.Invoke(__instance);
        }
        catch (Exception e) { Log.Error($"Setting up {v} failed: {e}"); }
    }

    // ---- per-frame hook --------------------------------------------------------------------------------

    private static float _updateErrorAt = -999f;

    [HarmonyPatch(typeof(MoonPrototypeVehicle), nameof(MoonPrototypeVehicle.Update))]
    [HarmonyPostfix]
    private static void AfterVehicleUpdate(MoonPrototypeVehicle __instance)
    {
        if (Modded(__instance.Index) is not { Update: { } update } v) return;
        try { update(__instance); }
        catch (Exception e)
        {
            // Every frame would flood the log; report at most every 10 s.
            if (Time.unscaledTime - _updateErrorAt < 10f) return;
            _updateErrorAt = Time.unscaledTime;
            Log.Error($"{v}'s Update failed: {e}");
        }
    }

    // ---- garage: offers and card pictures ------------------------------------------------------------

    /// <summary>Adds (or re-prices) the garage offers of modded vehicles that have a price.</summary>
    internal static void EnsureOffers()
    {
        var offers = MoonWorld.GarageOffers;
        if (offers == null) return;
        var list = new List<MoonWorld.GarageOffer>(offers);
        var changed = false;
        foreach (var v in Vehicles.All)
        {
            var existing = list.FindIndex(o => o != null && o.vehicle == v.Index);
            if (v.EffectivePrice is not { } price)
            {
                if (existing >= 0) { list.RemoveAt(existing); changed = true; }
                continue;
            }
            if (existing >= 0)
            {
                var offer = list[existing];
                if (Math.Abs(double.Parse(offer.price.ToString(), System.Globalization.CultureInfo.InvariantCulture) - price) > 0.005 || offer.summary != v.Summary)
                {
                    offer.price = new Il2CppSystem.Decimal(price);
                    offer.summary = v.Summary;
                }
                continue;
            }
            list.Add(new MoonWorld.GarageOffer { vehicle = v.Index, price = new Il2CppSystem.Decimal(price), summary = v.Summary });
            changed = true;
        }
        if (changed) MoonWorld.GarageOffers = new Il2CppReferenceArray<MoonWorld.GarageOffer>(list.ToArray());
        Crew.PublishIfHost();   // the host's prices are the crew's; republish if they changed
    }

    [HarmonyPatch(typeof(MoonGarageMenu), nameof(MoonGarageMenu.Build))]
    [HarmonyPrefix]
    private static void BeforeGarageBuild() => SafeEnsureOffers();

    [HarmonyPatch(typeof(MoonGarageMenu), nameof(MoonGarageMenu.Refresh))]
    [HarmonyPrefix]
    private static void BeforeGarageRefresh() => SafeEnsureOffers();

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.GarageOfferFor))]
    [HarmonyPrefix]
    private static void BeforeOfferFor() => SafeEnsureOffers();

    private static void SafeEnsureOffers()
    {
        try { EnsureOffers(); }
        catch (Exception e) { Log.Error("Adding modded vehicles to the garage failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonGarageMenu), nameof(MoonGarageMenu.Render))]
    [HarmonyPrefix]
    private static bool Render(int vehicle, ref Sprite __result)
    {
        if (Modded(vehicle) is not { } v) return true;
        __result = Icon(v)!;
        return false;
    }

    // The game inlined Render where it builds the vehicle cards, the upgrades model strip and the details picture,
    // so set modded vehicles' pictures after those.
    [HarmonyPatch(typeof(MoonGarageMenu), nameof(MoonGarageMenu.Row))]
    [HarmonyPostfix]
    private static void AfterRow(MoonGarageMenu __instance)
    {
        try
        {
            foreach (var card in __instance.cards)
                if (card != null && Modded(card.vehicle) is { } v) SetPicture(card.art, v);
        }
        catch (Exception e) { Log.Error("Setting garage card pictures failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonGarageMenu), nameof(MoonGarageMenu.BuildUpgradesPage))]
    [HarmonyPostfix]
    private static void AfterUpgradesPage(MoonGarageMenu __instance)
    {
        try
        {
            foreach (var card in __instance.strip)
                if (card != null && Modded(card.vehicle) is { } v) SetPicture(card.art, v);
        }
        catch (Exception e) { Log.Error("Setting upgrade strip pictures failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonGarageMenu), nameof(MoonGarageMenu.RefreshDetails))]
    [HarmonyPostfix]
    private static void AfterDetails(MoonGarageMenu __instance)
    {
        try
        {
            var card = __instance.shown ?? __instance.selected;
            if (card != null && Modded(card.vehicle) is { } v) SetPicture(__instance.picture, v);
        }
        catch (Exception e) { Log.Error("Setting the garage details picture failed: " + e); }
    }

    private static void SetPicture(UnityEngine.UI.Image? image, ModVehicle v)
    {
        if (image == null || Icon(v) is not { } sprite) return;
        image.sprite = sprite;
        image.enabled = true;
    }

    // ---- garage delivery for vehicles too big for the hall -------------------------------------------

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.BringOut))]
    [HarmonyPostfix]
    private static void AfterBringOut(MoonGarage view, int vehicle, MoonPrototypeVehicle __result)
    {
        if (__result == null || view == null || Modded(vehicle) is not { DeliverOutsideGarage: true } v) return;
        try
        {
            if (!GarageDelivery.MoveOutside(view, __result)) Log.Warning($"No clear ground in front of the garage for {v}; it stays in the hall.");
        }
        catch (Exception e) { Log.Error($"Moving {v} out of the garage failed: {e}"); }
    }

    private static Sprite? Icon(ModVehicle v)
    {
        if (v.IconSprite != null && !v.IconSprite.WasCollected && v.IconSprite) return v.IconSprite;
        if (v.IconPath == null || !File.Exists(v.IconPath)) return null;
        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = v.Name + " icon", wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(v.IconPath))) return null;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            v.IconSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            v.IconSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return v.IconSprite;
        }
        catch (Exception e)
        {
            Log.Warning($"Couldn't load the icon of {v} from {v.IconPath}: {e.Message}");
            return null;
        }
    }

    // ---- upgrade prices -------------------------------------------------------------------------------

    [HarmonyPatch(typeof(MoonVehicleUpgrades), nameof(MoonVehicleUpgrades.PriceScale))]
    [HarmonyPrefix]
    private static bool UpgradePriceScale(ref int vehicle, ref Il2CppSystem.Decimal __result)
    {
        if (Modded(vehicle) is not { } v) return true;
        if (v.EffectiveUpgradePriceScale is not { } scale) { vehicle = v.BaseVehicle; return true; }
        __result = new Il2CppSystem.Decimal(scale);
        return false;
    }

    [HarmonyPatch(typeof(MoonVehicleUpgrades), nameof(MoonVehicleUpgrades.Price))]
    [HarmonyPrefix]
    private static bool UpgradePrice(MoonVehicleUpgrades.Def def, int vehicle, int level, ref Il2CppSystem.Decimal __result)
    {
        if (def == null || Modded(vehicle) is not { } v) return true;
        if (!v.UpgradePrices.TryGetValue(def.kind, out var prices) || level < 0 || level >= prices.Length) return true;
        __result = new Il2CppSystem.Decimal((double)prices[level]);
        return false;
    }

    // ---- hide the driver in closed cabs --------------------------------------------------------------

    private static readonly List<Renderer> Hidden = new();

    [HarmonyPatch(typeof(MoonWalker), nameof(MoonWalker.LateUpdate))]
    [HarmonyPostfix]
    private static void AfterWalkerLateUpdate(MoonWalker __instance)
    {
        try
        {
            var vehicle = __instance.mountedVehicle;
            if (vehicle != null && vehicle.IsDriven && Modded(vehicle.Index) is { HideDriver: true })
            {
                // Every frame: the game may switch parts of the player (tools, arms) back on.
                foreach (var r in __instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    r.enabled = false;
                    Hidden.Add(r);
                }
            }
            else if (Hidden.Count > 0)
            {
                foreach (var r in Hidden)
                    if (r != null && !r.WasCollected && r) r.enabled = true;
                Hidden.Clear();
            }
        }
        catch (Exception e)
        {
            Log.Error("Hiding the driver failed: " + e);
            Hidden.Clear();
        }
    }
}
