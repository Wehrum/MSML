using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MoonCleaners;

namespace MSML;

/// <summary>
/// Data a mod keeps with a moon, saved inside the moon's own save (so it travels with it, including to crewmates
/// joining the host). Store small things: settings of the moon, which belts are tubes, and so on.
/// <para>
/// The unmodded game loads and keeps the data like any other save entry, so removing a mod never breaks a save, and
/// data of mods that aren't installed is kept for when they're back.
/// </para>
/// </summary>
public static class MoonData
{
    // Saved as extra storage records "msmld:000:<base64 part>", "msmld:001:..." (the game rejects ids over 128
    // characters and duplicate ids) holding a JSON object of key -> value.
    private const string RecordPrefix = "msmld:";
    private const int PartLength = 110;

    private static readonly object Lock = new();
    private static Dictionary<string, string> _data = new();
    private static bool _patched;

    // Read from the last save file; applied when the world restores from that snapshot.
    private static IntPtr _readSnapshot;
    private static Dictionary<string, string>? _read;

    /// <summary>Raised on the main thread once a saved moon's data is loaded. A new moon starts with no data.</summary>
    public static event Action? Loaded;

    /// <summary>The value stored under <paramref name="key"/> for the current moon, or null.</summary>
    public static string? Get(string key)
    {
        lock (Lock) return _data.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>Stores a value for the current moon (null removes it). Saved with the moon's next save.</summary>
    public static void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A key is needed.", nameof(key));
        lock (Lock)
        {
            if (value == null) _data.Remove(key);
            else _data[key] = value;
        }
    }

    /// <summary>The value under <paramref name="key"/> read as JSON, or default.</summary>
    public static T? Get<T>(string key)
    {
        var json = Get(key);
        if (json == null) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (Exception e) { Log.Warning($"Moon data '{key}' couldn't be read: {e.Message}"); return default; }
    }

    /// <summary>Stores <paramref name="value"/> as JSON.</summary>
    public static void Set<T>(string key, T value) => Set(key, value == null ? null : JsonSerializer.Serialize(value));

    internal static void Apply()
    {
        if (_patched) return;
        _patched = true;
        new Harmony("msml.api.moondata").PatchAll(typeof(MoonData));
    }

    // ---- saving --------------------------------------------------------------------------------

    private static readonly List<MoonVehicleStorageSnapshot> Written = new();

    [HarmonyPatch(typeof(MoonVehicleStorageSave), nameof(MoonVehicleStorageSave.Write))]
    [HarmonyPrefix]
    private static void BeforeStorageWrite(MoonLooseDustSnapshot snapshot)
    {
        try
        {
            Written.Clear();
            RemoveRecords(snapshot);
            string json;
            lock (Lock)
            {
                if (_data.Count == 0) return;
                json = JsonSerializer.Serialize(_data);
            }
            var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
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
                Written.Add(record);
            }
        }
        catch (Exception e) { Log.Error("Saving mod moon data failed; it won't be in this save: " + e); }
    }

    [HarmonyPatch(typeof(MoonVehicleStorageSave), nameof(MoonVehicleStorageSave.Write))]
    [HarmonyPostfix]
    private static void AfterStorageWrite(MoonLooseDustSnapshot snapshot)
    {
        try { foreach (var r in Written) snapshot.vehicles?.Remove(r); }
        catch (Exception e) { Log.Error("Cleaning up after saving mod moon data failed: " + e); }
        Written.Clear();
    }

    // ---- loading -------------------------------------------------------------------------------

    [HarmonyPatch(typeof(MoonVehicleStorageSave), nameof(MoonVehicleStorageSave.Read))]
    [HarmonyPostfix]
    private static void AfterStorageRead(MoonLooseDustSnapshot snapshot)
    {
        try
        {
            _read = RemoveRecords(snapshot);
            _readSnapshot = snapshot.Pointer;
        }
        catch (Exception e) { Log.Error("Reading mod moon data failed: " + e); }
    }

    // The save is also read without loading it (e.g. the host packing the moon for a joiner), so the data only
    // becomes the moon's when the world restores from that snapshot.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.RestoreConveyors))]
    [HarmonyPostfix]
    private static void AfterRestore(MoonLooseDustSnapshot snapshot)
    {
        var data = snapshot != null && snapshot.Pointer == _readSnapshot && _read != null ? _read : new Dictionary<string, string>();
        _read = null;
        _readSnapshot = IntPtr.Zero;
        lock (Lock) _data = data;
        if (data.Count > 0) Log.Info($"Loaded mod moon data: {string.Join(", ", data.Keys)}.");
        RaiseLoaded();
    }

    // Leaving a moon: the next one (new or loaded) starts empty.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterWorldDestroyed()
    {
        lock (Lock) _data = new Dictionary<string, string>();
    }

    private static void RaiseLoaded()
    {
        foreach (var handler in Loaded?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)handler)(); }
            catch (Exception e) { Log.Error("A MoonData.Loaded handler failed: " + e); }
        }
    }

    private static Dictionary<string, string> RemoveRecords(MoonLooseDustSnapshot snapshot)
    {
        var parts = new SortedDictionary<int, string>();
        var storage = snapshot?.vehicles;
        if (storage == null) return new Dictionary<string, string>();
        for (var i = storage.Count - 1; i >= 0; i--)
        {
            var id = storage[i]?.id;
            if (id == null || !id.StartsWith(RecordPrefix, StringComparison.Ordinal)) continue;
            storage.RemoveAt(i);
            var colon = id.IndexOf(':', RecordPrefix.Length);
            if (colon > 0 && int.TryParse(id[RecordPrefix.Length..colon], out var part)) parts[part] = id[(colon + 1)..];
        }
        if (parts.Count == 0) return new Dictionary<string, string>();
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(parts.Values)));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception e)
        {
            Log.Error("The saved mod moon data couldn't be read: " + e.Message);
            return new Dictionary<string, string>();
        }
    }
}
