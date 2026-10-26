using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using MoonCleaners;
using Steamworks;
using UnityEngine;

namespace MSML;

/// <summary>
/// Multiplayer support for mod content everyone in a crew needs: modded vehicles and belt types. Crewmates refer to
/// vehicles by catalog index, so everyone in a crew needs the same vehicle mods (same ids at the same indexes), and
/// the same belt types (or crewmates' belts would move at different speeds).
/// <list type="bullet">
/// <item><b>Joining:</b> a host with modded vehicles advertises its version as "1.0.4 + mods: Hex Harvester" and its
/// vehicles in the "msml.vehicles" lobby field. Players without the same vehicles are turned away by the game's own
/// version check, with a message saying which mods to add or turn off (unmodded players see the mod list in the
/// game's message); players with them see the plain version and join.
/// A modded player can still join an unmodded crew: their modded vehicles just aren't sold there.</item>
/// <item><b>Garage terms:</b> the host charges for purchases, so crewmates' garages show the host's prices, and don't
/// offer modded vehicles the host doesn't have.</item>
/// <item><b>Safety:</b> crewmate vehicles and owned vehicles with an index this game doesn't know are skipped instead
/// of throwing every frame.</item>
/// <item><b>Crewmates' vehicles:</b> <see cref="ModVehicle.RemoteUpdate"/> runs for them, and <see cref="ModVehicle.HideDriver"/>
/// hides crewmates driving one.</item>
/// </list>
/// </summary>
internal static class Crew
{
    private const string VersionKey = "version";
    private const string VehiclesKey = "msml.vehicles";

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Mod { get; set; }
        public int Index { get; set; }
        public double? Price { get; set; }
        public double? UpgradePriceScale { get; set; }
    }

    private static List<Entry> Local() =>
        Vehicles.All.OrderBy(v => v.Index).Select(v => new Entry
        {
            Id = v.Id, Name = v.Name, Mod = v.ModName, Index = v.Index, Price = v.Price, UpgradePriceScale = v.UpgradePriceScale,
        }).Concat(Belts.Types.OrderBy(t => t.Kind).Select(t => new Entry
        {
            Id = t.Id, Name = t.Name, Mod = t.ModName, Index = -1,
        })).ToList();

    private static bool HasCrewContent => Vehicles.All.Count > 0 || Belts.Types.Count > 0;

    private static bool _applied;

    /// <summary>Applies the crew patches (once), for mods that add things everyone in a crew needs.</summary>
    internal static void Apply()
    {
        if (_applied) return;
        _applied = true;
        new Harmony("msml.api.crew").PatchAll(typeof(Crew));
        JoinCheck.Apply();
    }

    /// <summary>"1.0.4 + mods: Hex Harvester" (mod names) with crew content registered, else the plain game version.</summary>
    internal static string AdvertisedVersion()
    {
        if (!HasCrewContent) return Application.version;
        return Application.version + " + mods: " + string.Join(", ", Local().Select(e => e.Mod ?? e.Name).Distinct());
    }

    /// <summary>Same vehicle ids at the same indexes (prices may differ: the host's are used).</summary>
    private static bool SameVehicles(IEnumerable<Entry> theirs) =>
        Signature(theirs) == Signature(Local());

    private static string Signature(IEnumerable<Entry> entries) =>
        string.Join("|", entries.OrderBy(e => e.Index).Select(e => $"{e.Id}@{e.Index}"));

    private static List<Entry>? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<List<Entry>>(json); }
        catch { return null; }
    }

    // ---- host: advertise ---------------------------------------------------------------------------

    private static string _published = "";

    /// <summary>Writes the version and vehicle list to the lobby (host only; again when prices change).</summary>
    internal static void PublishIfHost()
    {
        if (!MoonSteamLobby.InLobby || !MoonSteamLobby.IsHost || MoonSteamLobby.lobby == 0) return;
        var json = JsonSerializer.Serialize(Local());
        if (json == _published) return;
        var lobby = new CSteamID(MoonSteamLobby.lobby);
        SteamMatchmaking.SetLobbyData(lobby, VersionKey, AdvertisedVersion());
        SteamMatchmaking.SetLobbyData(lobby, VehiclesKey, HasCrewContent ? json : "");
        _published = json;
    }

    [HarmonyPatch(typeof(MoonSteamLobby), nameof(MoonSteamLobby.OnCreated))]
    [HarmonyPostfix]
    private static void AfterCreated()
    {
        try
        {
            ClearCrewTerms();
            _published = "";
            PublishIfHost();
            if (HasCrewContent) Log.Info($"Crew created; advertised as '{AdvertisedVersion()}'.");
        }
        catch (Exception e) { Log.Error("Advertising modded vehicles to the crew failed: " + e); }
    }

    // ---- joining: the game's version check sees a match only with the same vehicles ----------------

    private static bool _reading;

    /// <summary>
    /// The join check. Applied by the mod loader even without vehicle mods, so a player missing a crew's mods is
    /// told which ones to add.
    /// </summary>
    internal static class JoinCheck
    {
        private static bool _applied;

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;
            new Harmony("msml.api.crewjoin").PatchAll(typeof(JoinCheck));
        }

        [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.GetLobbyData))]
        [HarmonyPostfix]
        private static void AfterGetLobbyData(CSteamID steamIDLobby, string pchKey, ref string __result)
        {
            if (_reading || pchKey != VersionKey || string.IsNullOrEmpty(__result)) return;
            try
            {
                _reading = true;
                var theirs = Parse(SteamMatchmaking.GetLobbyData(steamIDLobby, VehiclesKey)) ?? new List<Entry>();
                var plainHost = !__result.Contains(" + mods: ");
                // Unmodded host: compatible (our vehicles just aren't sold there). Modded host: only with the same vehicles.
                if (plainHost ? __result == Application.version : SameVehicles(theirs) && __result.StartsWith(Application.version + " + mods: "))
                {
                    __result = Application.version;
                    _refusal = null;
                }
                else if (!plainHost)
                {
                    _refusal = DescribeMismatch(__result, theirs);
                    _refusedVersion = __result;
                }
            }
            catch (Exception e) { Log.Error("Checking a crew's mods failed: " + e); }
            finally { _reading = false; }
        }

        // The game turns a mismatch away with "That crew plays version {0}. You have {1}. Update the game on Steam...";
        // for a modded crew that's replaced with what to install or turn off.
        private static string? _refusal;
        private static string _refusedVersion = "";

        private static string DescribeMismatch(string hostVersion, List<Entry> theirs)
        {
            var hostGame = hostVersion.Substring(0, hostVersion.IndexOf(" + mods: ", StringComparison.Ordinal));
            string ModOf(Entry e) => string.IsNullOrWhiteSpace(e.Mod) ? e.Name : e.Mod!;
            string List(IEnumerable<string> names) => string.Join(", ", names.Distinct());

            var mine = Local();
            var missing = theirs.Where(t => mine.All(m => m.Id != t.Id)).Select(ModOf).ToList();
            var extra = mine.Where(m => theirs.All(t => t.Id != m.Id)).Select(ModOf).ToList();
            var lines = new List<string>();
            if (hostGame != Application.version)
                lines.Add($"That crew plays version {hostGame}. You have {Application.version}. Update the game on Steam to play together.");
            if (missing.Count > 0)
                lines.Add($"This crew uses mods you don't have: {List(missing)}. Add them in MODS, then join again.");
            if (extra.Count > 0)
                lines.Add($"This crew doesn't use these mods of yours: {List(extra)}. Turn them off in MODS, then join again.");
            if (missing.Count == 0 && extra.Count == 0 && hostGame == Application.version)
                lines.Add($"Your mods don't match this crew's ({List(theirs.Select(ModOf))}). Use the same mods and versions as the host, then join again.");
            return string.Join(" ", lines);
        }

        [HarmonyPatch(typeof(MoonSteamLobby), nameof(MoonSteamLobby.SetStatus))]
        [HarmonyPrefix]
        private static void BeforeSetStatus(ref string message, bool error)
        {
            try
            {
                if (!error || _refusal == null || message == null || !message.Contains(_refusedVersion)) return;
                Log.Info("Turned away from a modded crew: " + _refusal);
                message = _refusal;
                _refusal = null;
            }
            catch (Exception e) { Log.Error("Explaining a crew's mods failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(MoonSteamLobby), nameof(MoonSteamLobby.OnEntered))]
    [HarmonyPostfix]
    private static void AfterEntered() => ReadHostTerms();

    [HarmonyPatch(typeof(MoonSteamLobby), nameof(MoonSteamLobby.OnDataChanged))]
    [HarmonyPostfix]
    private static void AfterDataChanged() => ReadHostTerms();

    [HarmonyPatch(typeof(MoonSteamLobby), nameof(MoonSteamLobby.Leave))]
    [HarmonyPostfix]
    private static void AfterLeave() => ClearCrewTerms();

    [HarmonyPatch(typeof(MoonSteamLobby), nameof(MoonSteamLobby.ResetState))]
    [HarmonyPostfix]
    private static void AfterReset() => ClearCrewTerms();

    /// <summary>In someone else's crew: use the host's prices, and don't sell vehicles the host doesn't have.</summary>
    private static void ReadHostTerms()
    {
        try
        {
            if (!MoonSteamLobby.InLobby || MoonSteamLobby.IsHost || MoonSteamLobby.lobby == 0) { ClearCrewTerms(); return; }
            List<Entry> host;
            _reading = true;
            try { host = Parse(SteamMatchmaking.GetLobbyData(new CSteamID(MoonSteamLobby.lobby), VehiclesKey)) ?? new List<Entry>(); }
            finally { _reading = false; }
            foreach (var v in Vehicles.All)
            {
                var entry = host.FirstOrDefault(e => e.Id == v.Id && e.Index == v.Index);
                v.CrewOverride = true;
                v.CrewPrice = entry?.Price;
                v.CrewUpgradePriceScale = entry?.UpgradePriceScale;
            }
        }
        catch (Exception e) { Log.Error("Reading the host's garage terms failed: " + e); }
    }

    private static void ClearCrewTerms()
    {
        foreach (var v in Vehicles.All)
        {
            v.CrewOverride = false;
            v.CrewPrice = null;
            v.CrewUpgradePriceScale = null;
        }
    }

    // ---- safety: indexes this game doesn't know ----------------------------------------------------

    private static readonly HashSet<int> Reported = new();

    private static bool Known(int index, string what)
    {
        if (index >= 0 && index < MoonPrototypeCatalog.Vehicles.Length) return true;
        if (Reported.Add(index)) Log.Warning($"A crewmate's {what} uses vehicle #{index}, which this game doesn't have (a mod is missing); it's not shown.");
        return false;
    }

    [HarmonyPatch(typeof(MoonRemoteVehicle), nameof(MoonRemoteVehicle.Spawn))]
    [HarmonyPrefix]
    private static bool BeforeRemoteSpawn(int index, ref MoonRemoteVehicle __result)
    {
        if (Known(index, "vehicle")) return true;
        __result = null!;
        return false;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ApplyCrewVehicles))]
    [HarmonyPostfix]
    private static void AfterApplyCrewVehicles(MoonWorld __instance)
    {
        try
        {
            var owned = __instance.ownedVehicles;
            if (owned == null) return;
            var unknown = new List<string>();
            foreach (var pair in owned)
                if (pair.Value != null && !Known(pair.Value.vehicle, "owned vehicle")) unknown.Add(pair.Key);
            foreach (var key in unknown) owned.Remove(key);
        }
        catch (Exception e) { Log.Error("Filtering crew vehicles failed: " + e); }
    }

    // ---- crewmates' vehicles -------------------------------------------------------------------------

    private static readonly Dictionary<IntPtr, List<Renderer>> HiddenDrivers = new();
    private static float _remoteErrorAt = -999f;

    [HarmonyPatch(typeof(MoonRemoteVehicle), nameof(MoonRemoteVehicle.ShowAt))]
    [HarmonyPostfix]
    private static void AfterRemoteShow(MoonRemoteVehicle __instance)
    {
        var v = Vehicles.ByIndex(__instance.Index);
        if (v == null) return;
        try
        {
            v.RemoteUpdate?.Invoke(__instance);
            HideRemoteDriver(__instance, v.HideDriver && __instance.Driven);
        }
        catch (Exception e)
        {
            if (Time.unscaledTime - _remoteErrorAt < 10f) return;
            _remoteErrorAt = Time.unscaledTime;
            Log.Error($"Updating a crewmate's {v} failed: {e}");
        }
    }

    private static void HideRemoteDriver(MoonRemoteVehicle vehicle, bool hide)
    {
        HiddenDrivers.TryGetValue(vehicle.Pointer, out var hidden);
        var driver = vehicle.Crewmate;
        if (hide && driver != null)
        {
            hidden ??= HiddenDrivers[vehicle.Pointer] = new List<Renderer>();
            foreach (var r in driver.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                r.enabled = false;
                hidden.Add(r);
            }
        }
        else if (hidden != null)
        {
            foreach (var r in hidden)
                if (r != null && !r.WasCollected && r) r.enabled = true;
            HiddenDrivers.Remove(vehicle.Pointer);
        }
    }
}
