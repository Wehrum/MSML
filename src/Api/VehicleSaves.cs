using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Keeps bought modded vehicles out of the game's own owned-vehicle list in saves, so a save never refers to a
/// catalog index that may not exist (the mod removed, or another mod changing the order).
/// <para>
/// Saving: just before the vehicle-storage section is written, modded vehicles are taken out of the snapshot's
/// owned-vehicle list and written as extra storage records (ids <c>msml:000:</c> + part of a base64 JSON list, by
/// vehicle <see cref="ModVehicle.Id"/>). The unmodded game loads and keeps that record like any other storage entry.
/// The vehicles are put back once the owned-vehicle section is written.
/// </para>
/// <para>
/// Loading: after the owned-vehicle section is read, the record is decoded and every vehicle whose mod is installed
/// is added back with its current index. Vehicles of missing mods are kept and written again on the next save, so
/// they come back when the mod does.
/// </para>
/// </summary>
internal static class VehicleSaves
{
    // The game rejects storage ids over 128 characters and duplicate ids, so the data is split into numbered records:
    // "msml:000:<base64 part>", "msml:001:...".
    private const string RecordPrefix = "msml:";
    private const int PartLength = 110;

    // Vehicles of mods that aren't installed, from the last save loaded; written back on save.
    private static List<SavedVehicle> _unknown = new();

    // The snapshot being saved and the modded vehicles taken out of it (one save runs at a time).
    private static IntPtr _saving;
    private static readonly List<MoonOwnedVehicle> Removed = new();
    private static readonly List<MoonVehicleStorageSnapshot> Records = new();

    [HarmonyPatch(typeof(MoonVehicleStorageSave), nameof(MoonVehicleStorageSave.Write))]
    [HarmonyPrefix]
    private static void BeforeStorageWrite(MoonLooseDustSnapshot snapshot)
    {
        try
        {
            Restore();
            var owned = snapshot.ownedVehicles;
            var saved = new List<SavedVehicle>(_unknown);
            if (owned != null)
            {
                for (var i = owned.Count - 1; i >= 0; i--)
                {
                    var o = owned[i];
                    if (o == null || Vehicles.ByIndex(o.vehicle) is not { } v) continue;
                    saved.Add(SavedVehicle.From(o, v));
                    Removed.Add(o);
                    owned.RemoveAt(i);
                }
            }
            RemoveRecords(snapshot);
            if (saved.Count == 0) return;
            var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(saved)));
            snapshot.vehicles ??= new Il2CppSystem.Collections.Generic.List<MoonVehicleStorageSnapshot>();
            for (int part = 0, at = 0; at < data.Length; part++, at += PartLength)
            {
                var record = new MoonVehicleStorageSnapshot
                {
                    id = $"{RecordPrefix}{part:D3}:{data.Substring(at, Math.Min(PartLength, data.Length - at))}",
                    kilograms = 0,
                    capacity = 1,
                };
                snapshot.vehicles.Add(record);
                Records.Add(record);
            }
            _saving = snapshot.Pointer;
        }
        catch (Exception e)
        {
            Log.Error("Saving modded vehicles failed; they won't be in this save: " + e);
            Restore();
        }
    }

    [HarmonyPatch(typeof(MoonOwnedVehicleSave), nameof(MoonOwnedVehicleSave.Write))]
    [HarmonyPostfix]
    private static void AfterOwnedWrite(MoonLooseDustSnapshot state)
    {
        if (state != null && state.Pointer == _saving) Restore(state);
    }

    /// <summary>Puts the vehicles taken out for saving back, and removes the record.</summary>
    private static void Restore(MoonLooseDustSnapshot? state = null)
    {
        try
        {
            if (state != null)
            {
                foreach (var o in Removed) state.ownedVehicles?.Add(o);
                foreach (var record in Records) state.vehicles?.Remove(record);
            }
        }
        catch (Exception e) { Log.Error("Restoring modded vehicles after saving failed: " + e); }
        Removed.Clear();
        Records.Clear();
        _saving = IntPtr.Zero;
    }

    [HarmonyPatch(typeof(MoonOwnedVehicleSave), nameof(MoonOwnedVehicleSave.Read))]
    [HarmonyPostfix]
    private static void AfterOwnedRead(MoonLooseDustSnapshot state)
    {
        try
        {
            _unknown = new List<SavedVehicle>();
            var records = RemoveRecords(state);
            if (records.Count == 0) return;
            state.ownedVehicles ??= new Il2CppSystem.Collections.Generic.List<MoonOwnedVehicle>();
            foreach (var saved in records)
            {
                if (Vehicles.ById(saved.Id) is not { } v)
                {
                    _unknown.Add(saved);
                    Log.Warning($"Saved vehicle '{saved.Id}' ({saved.Name}) belongs to a mod that isn't installed; it's kept for when it's back.");
                    continue;
                }
                var owned = saved.ToOwned(v.Index);
                // The hopper's storage id contains the catalog index; follow the vehicle if the index changed.
                if (saved.Index != v.Index && state.vehicles != null)
                {
                    var oldStorage = saved.ToOwned(saved.Index).Storage;
                    foreach (var s in state.vehicles)
                        if (s != null && s.id == oldStorage) s.id = owned.Storage;
                }
                state.ownedVehicles.Add(owned);
                Log.Info($"Loaded {v.Name} (garage {saved.Garage}, {saved.OwnerName}).");
            }
        }
        catch (Exception e)
        {
            Log.Error("Loading modded vehicles failed: " + e);
        }
    }

    /// <summary>Takes the msml records out of a snapshot's storage list and decodes them.</summary>
    private static List<SavedVehicle> RemoveRecords(MoonLooseDustSnapshot snapshot)
    {
        var parts = new SortedDictionary<int, string>();
        var storage = snapshot.vehicles;
        if (storage == null) return new List<SavedVehicle>();
        for (var i = storage.Count - 1; i >= 0; i--)
        {
            var id = storage[i]?.id;
            if (id == null || !id.StartsWith(RecordPrefix, StringComparison.Ordinal)) continue;
            storage.RemoveAt(i);
            var colon = id.IndexOf(':', RecordPrefix.Length);
            if (colon > 0 && int.TryParse(id[RecordPrefix.Length..colon], out var part)) parts[part] = id[(colon + 1)..];
        }
        if (parts.Count == 0) return new List<SavedVehicle>();
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(parts.Values)));
            return JsonSerializer.Deserialize<List<SavedVehicle>>(json) ?? new List<SavedVehicle>();
        }
        catch (Exception e)
        {
            Log.Error("The saved modded-vehicle records couldn't be read: " + e.Message);
            return new List<SavedVehicle>();
        }
    }

    /// <summary>One bought modded vehicle, as saved.</summary>
    internal sealed class SavedVehicle
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Index { get; set; }
        public int Garage { get; set; }
        public string? Owner { get; set; }
        public string? OwnerName { get; set; }
        public bool InWorld { get; set; }
        public float[] Position { get; set; } = new float[3];
        public float[] Rotation { get; set; } = { 0, 0, 0, 1 };
        public string? Holder { get; set; }
        public double Odometer { get; set; }
        public string? Upgrades { get; set; }

        public static SavedVehicle From(MoonOwnedVehicle o, ModVehicle v) => new()
        {
            Id = v.Id,
            Name = v.Name,
            Index = o.vehicle,
            Garage = o.garage,
            Owner = o.owner,
            OwnerName = o.ownerName,
            InWorld = o.inWorld,
            Position = new[] { o.position.x, o.position.y, o.position.z },
            Rotation = new[] { o.rotation.x, o.rotation.y, o.rotation.z, o.rotation.w },
            Holder = o.holder,
            Odometer = o.odometer,
            Upgrades = o.upgrades == null ? null : Convert.ToBase64String(o.upgrades.ToArray()),
        };

        public MoonOwnedVehicle ToOwned(int index)
        {
            var o = new MoonOwnedVehicle
            {
                garage = Garage,
                vehicle = index,
                owner = Owner,
                ownerName = OwnerName,
                inWorld = InWorld,
                position = new Vector3(Position[0], Position[1], Position[2]),
                rotation = new Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3]),
                holder = Holder,
                odometer = Odometer,
            };
            if (Upgrades != null) o.upgrades = new Il2CppStructArray<byte>(Convert.FromBase64String(Upgrades));
            return o;
        }
    }
}
