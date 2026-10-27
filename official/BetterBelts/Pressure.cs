using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MSML.BetterBelts;

/// <summary>
/// Air pressure in the tube network, and what it does.
/// <para>
/// A powered booster gives full pressure to the tubes after it. Pressure travels downstream through tubes, splitters
/// and mergers (<see cref="Belts.Downstream"/>), not through belts or machines, and stays full for
/// <see cref="Settings.FullRange"/> metres of tube, then fades to nothing over <see cref="Settings.FadeRange"/>. A tube's
/// speed follows the pressure along it (1x to <see cref="Settings.TubeSpeed"/>); more boosters don't add up, the
/// nearest one counts. Low power shortens a booster's reach.
/// </para>
/// <para>
/// Power: a booster takes power from the grid of a solar panel it's in range of, like a machine: it uses spare
/// solar power first, then the grid's batteries; when neither is enough its reach shrinks with the shortfall.
/// </para>
/// <para>
/// In a crew everyone computes the same pressure from the host's settings (speed, ranges, power) and the host's power
/// for each booster (each machine's own solar grid simulation can drift), so capsules move alike for everyone.
/// </para>
/// </summary>
internal static class Pressure
{
    private const string SettingsTopic = "msml.betterbelts.settings";

    // Distance (metres of tube) from the nearest powered booster to the start of each pressurised tube.
    private static readonly Dictionary<int, float> StartDistance = new();
    // Each booster on the solar grid (MSML Power): how much power it gets (0-1).
    private static readonly Dictionary<int, PowerUser> Users = new();
    private static readonly Dictionary<int, float> BoosterPower = new();
    private static readonly HashSet<int> SpedRoutes = new();
    // Each tube splitter's or merger's pressure (0-1) at its middle, for its ring.
    private static readonly Dictionary<int, float> JunctionPressure = new();
    private static bool _dirty = true;
    private static float _nextSolve, _nextSettings;
    private static bool _bright;

    // The host's settings while in someone else's crew.
    private static float? _hostSpeed, _hostFull, _hostFade, _hostPower;

    private static float Speed => _hostSpeed ?? Settings.TubeSpeed.Value;
    private static float Full => _hostFull ?? Settings.FullRange.Value;
    private static float Fade => _hostFade ?? Settings.FadeRange.Value;
    private static float Kw => _hostPower ?? Settings.BoosterPower.Value;

    public static void Start()
    {
        Belts.Changed += Dirty;
        Moon.Update += Tick;
        CrewMessages.On(SettingsTopic, OnHostSettings);
        CrewMessages.On(PowerTopic, OnHostPower);
        // A crewmate who just loaded gets the host's settings and boosters' power now.
        CrewMessages.CrewmateReady += _ => { _nextSettings = 0f; _nextPower = 0f; _lastPowerSent = -100f; };
    }

    public static void Dirty()
    {
        _dirty = true;
        _recolor = true;
        Outlets.Clear();
        foreach (var booster in LastWhoosh.Keys.ToList()) if (!Belts.Exists(booster)) { LastWhoosh.Remove(booster); Inside.Remove(booster); }
    }

    // The rings only need recolouring when the pressure they show changed.
    private static bool _recolor = true;
    private static int _colorKey;

    /// <summary>Pressure (0-1) at a distance from a booster.</summary>
    private static float At(float distance) =>
        distance <= Full ? 1f : distance >= Full + Fade ? 0f : 1f - (distance - Full) / Fade;

    private static void Tick()
    {
        var now = Time.unscaledTime;
        if (_dirty || now >= _nextSolve)
        {
            _dirty = false;
            _nextSolve = now + 1f;
            try { Solve(); }
            catch (Exception e) { Plugin.Logger.LogError("Working out tube pressure failed: " + e); }
        }
        Whooshes();
        var bright = Settings.AlwaysBright.Value || Building();
        if (bright != _bright) { _bright = bright; RecolorAll(); }
        if (!CrewMessages.InCrew) { _hostSpeed = _hostFull = _hostFade = _hostPower = null; HostBoosterPower.Clear(); }
        else if (CrewMessages.IsHost && now >= _nextSettings)
        {
            _nextSettings = now + 10f;
            CrewMessages.SendToCrew(SettingsTopic, Encoding.UTF8.GetBytes(
                FormattableString.Invariant($"{Settings.TubeSpeed.Value};{Settings.FullRange.Value};{Settings.FadeRange.Value};{Settings.BoosterPower.Value}")));
        }
        if (CrewMessages.InCrew && CrewMessages.IsHost && now >= _nextPower)
        {
            _nextPower = now + 1f;
            SendPower(now);
        }
    }

    // ---- the host's boosters' power -------------------------------------------------------------

    // Each booster's power comes from this machine's own simulation of the solar grid and its batteries, which can
    // drift from the host's; capsules move on the host's say, so crewmates use the host's power for each booster
    // (named by its ends: belt ids differ between machines) and work out the same pressure and speeds from it.
    private const string PowerTopic = "msml.betterbelts.power";
    private const float HostPowerGrace = 12f;   // how long a crewmate keeps using the host's last word
    private static readonly Dictionary<int, (float Power, float At)> HostBoosterPower = new();
    private static float _nextPower, _lastPowerSent = -100f;
    private static int _sentPowerKey;

    // Within a second of a change, and every few seconds anyway (a crewmate may have missed it, or just joined).
    private static void SendPower(float now)
    {
        var key = 0;
        foreach (var (belt, p) in BoosterPower) key ^= HashCode.Combine(belt, (int)(p * 50f));
        if (key == _sentPowerKey && now - _lastPowerSent < 9f) return;
        _sentPowerKey = key;
        _lastPowerSent = now;
        CrewMessages.SendToCrew(PowerTopic, EncodePower());
    }

    // Each booster's ends and power.
    private static byte[] EncodePower()
    {
        using var stream = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(stream);
        var known = BoosterPower.Where(b => Belts.EndsOf(b.Key, out _, out _)).ToList();
        w.Write(known.Count);
        foreach (var (belt, power) in known)
        {
            Belts.EndsOf(belt, out var start, out var end);
            w.Write(start.x); w.Write(start.y); w.Write(start.z);
            w.Write(end.x); w.Write(end.y); w.Write(end.z);
            w.Write(power);
        }
        w.Flush();
        return stream.ToArray();
    }

    private static void OnHostPower(ulong sender, byte[] data)
    {
        if (CrewMessages.IsHost || sender != CrewMessages.Host) return;
        try { ReadPower(data); }
        catch (Exception e) { Plugin.Logger.LogWarning("The host's booster power couldn't be read: " + e.Message); }
    }

    // The host's power for each booster, found here by its ends. Returns how many were found.
    private static int ReadPower(byte[] data)
    {
        using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        var count = r.ReadInt32();
        var now = Time.unscaledTime;
        var found = 0;
        for (var i = 0; i < count; i++)
        {
            var start = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var end = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var power = r.ReadSingle();
            var belt = Belts.FindByEnds(start, end);
            if (belt == 0) continue;
            found++;
            var old = HostBoosterPower.TryGetValue(belt, out var o) ? o.Power : -1f;
            HostBoosterPower[belt] = (power, now);
            if (Math.Abs(old - power) > 0.01f) _dirty = true;
        }
        return found;
    }

    // A crewmate's power for a booster: the host's recent word for it, else its own.
    private static float CrewPower(int booster, float own) =>
        !CrewMessages.IsHost && HostBoosterPower.TryGetValue(booster, out var host) && Time.unscaledTime - host.At < HostPowerGrace ? host.Power : own;

    private static void OnHostSettings(ulong sender, byte[] data)
    {
        if (CrewMessages.IsHost || sender != CrewMessages.Host) return;
        var parts = Encoding.UTF8.GetString(data).Split(';');
        if (parts.Length < 4) return;
        float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        _hostSpeed = F(parts[0]); _hostFull = F(parts[1]); _hostFade = F(parts[2]); _hostPower = F(parts[3]);
        _dirty = true;
    }

    private static MoonDepotBuilder? _builder;
    private static float _nextBuilderSearch;

    private static bool Building()
    {
        if ((_builder == null || _builder.WasCollected) && Time.unscaledTime >= _nextBuilderSearch)
        {
            _nextBuilderSearch = Time.unscaledTime + 2f;
            _builder = Moon.Find<MoonDepotBuilder>();
        }
        return _builder != null && !_builder.WasCollected && (_builder.IsOpen || _builder.ConveyorMode || _builder.placing);
    }

    // ---- solving -------------------------------------------------------------------------------

    private static void Solve()
    {
        var w = Moon.World;
        if (w == null) return;
        UpdatePowerUsers();

        // Nearest-booster distances (Dijkstra over tubes; a booster restarts the count, so only seed from boosters).
        StartDistance.Clear();
        var queue = new PriorityQueue<int, float>();
        var lengths = new Dictionary<int, float>();
        float Length(int belt)
        {
            if (!lengths.TryGetValue(belt, out var l)) lengths[belt] = l = Belts.PathOf(belt)?.Length ?? 0f;
            return l;
        }
        foreach (var booster in Belts.OfType(Tubes.Booster))
        {
            var power = BoosterPower.TryGetValue(booster, out var p) ? p : 0f;
            if (power <= 0f) continue;
            // Low power shortens the reach: start further along, as if it had already used some of its range.
            var start = (1f - power) * (Full + Fade);
            foreach (var next in Belts.Downstream(booster)) Relax(next, start + ThroughLength(booster));
        }
        void Relax(int belt, float distance)
        {
            if (Belts.TypeOf(belt) != Tubes.Tube) return;
            if (StartDistance.TryGetValue(belt, out var best) && best <= distance) return;
            StartDistance[belt] = distance;
            queue.Enqueue(belt, distance);
        }
        // Splitters and mergers at the belt's end (several when joined straight to each other) count like a stretch of
        // tube: their routes' length uses up range.
        float RouteLength(int structure)
        {
            var longest = 0f;
            foreach (var route in Belts.RoutesThrough(structure)) longest = Math.Max(longest, Length(route));
            return longest;
        }
        float ThroughLength(int belt)
        {
            var total = 0f;
            foreach (var structure in Belts.JunctionsAfter(belt)) total += RouteLength(structure);
            return total;
        }
        while (queue.TryDequeue(out var belt, out var distance))
        {
            if (StartDistance[belt] < distance) continue;
            var end = distance + Length(belt) + ThroughLength(belt);
            if (end >= Full + Fade) continue;
            foreach (var next in Belts.Downstream(belt)) Relax(next, end);
        }

        // Speeds: tubes by the pressure along them, boosters by their power, junction routes by what feeds them.
        var speed = Speed;
        var routes = new Dictionary<int, float>();
        JunctionPressure.Clear();
        // The splitters and mergers after a pipe, from 'from' metres of range on: each route like a tube (the average
        // pressure along it), and each junction's pressure at its middle (its ring).
        void Through(int pipe, float from)
        {
            foreach (var structure in Belts.JunctionsAfter(pipe))
            {
                var through = RouteLength(structure);
                foreach (var route in Belts.RoutesThrough(structure))
                {
                    var rl = Length(route);
                    var ra = (At(from) + At(from + rl * 0.5f) + At(from + rl)) / 3f;
                    routes[route] = Math.Max(routes.TryGetValue(route, out var r) ? r : 1f, 1f + (speed - 1f) * ra);
                }
                var middle = At(from + through * 0.5f);
                JunctionPressure[structure] = Math.Max(JunctionPressure.TryGetValue(structure, out var jp) ? jp : 0f, middle);
                from += through;
            }
        }
        foreach (var tube in Belts.OfType(Tubes.Tube))
        {
            var s = 1f;
            if (StartDistance.TryGetValue(tube, out var d))
            {
                var l = Length(tube);
                // Average pressure along the tube (it's linear in the fade, so sample a few points).
                var avg = (At(d) + At(d + l * 0.25f) + At(d + l * 0.5f) + At(d + l * 0.75f) + At(d + l)) / 5f;
                s = 1f + (speed - 1f) * avg;
                Through(tube, d + l);
            }
            if (s > 1.001f) Belts.SetSpeed(tube, s); else Belts.ClearSpeed(tube);
        }
        foreach (var booster in Belts.OfType(Tubes.Booster))
        {
            var p = BoosterPower.TryGetValue(booster, out var power) ? power : 0f;
            if (p > 0f) Belts.SetSpeed(booster, 1f + (speed - 1f) * p); else Belts.ClearSpeed(booster);
            if (p > 0f) Through(booster, (1f - p) * (Full + Fade));
        }
        foreach (var route in SpedRoutes) if (!routes.ContainsKey(route)) Belts.ClearSpeed(route);
        SpedRoutes.Clear();
        foreach (var (route, s) in routes) { Belts.SetSpeed(route, s); SpedRoutes.Add(route); }

        // What the rings show (order-independent): recolour only when it changed.
        var key = 0;
        foreach (var (belt, d) in StartDistance) key ^= HashCode.Combine(belt, (int)(d * 4f));
        foreach (var (belt, p) in BoosterPower) key ^= HashCode.Combine(belt, -1, (int)(p * 50f));
        foreach (var (structure, p) in JunctionPressure) key ^= HashCode.Combine(structure, -2, (int)(p * 50f));
        key = HashCode.Combine(key, Full, Fade, Settings.ColorBlind.Value);
        if (key != _colorKey || _recolor) { _colorKey = key; _recolor = false; RecolorAll(); }
    }

    // ---- power ---------------------------------------------------------------------------------

    // Each booster is a power user at its middle (MSML Power: on the nearest panel's grid, beam and all).
    private static void UpdatePowerUsers()
    {
        var boosters = new HashSet<int>(Belts.OfType(Tubes.Booster));
        foreach (var (belt, user) in Users.ToList())
            if (!boosters.Contains(belt)) { Power.Remove(user); Users.Remove(belt); BoosterPower.Remove(belt); }
        foreach (var booster in boosters)
        {
            var path = Belts.PathOf(booster);
            if (path == null) continue;
            var middle = path.Point(path.Length / 2f);
            if (!Users.TryGetValue(booster, out var user)) Users[booster] = user = Power.Add(middle, Kw);
            user.Position = middle;
            user.Kw = Kw;
            var old = BoosterPower.TryGetValue(booster, out var o) ? o : -1f;
            // Free boosters (0 kW) run whenever they're on a grid.
            var power = CrewPower(booster, Kw <= 0f ? (user.Grid != 0 ? 1f : 0f) : user.Power);
            BoosterPower[booster] = power;
            if (Math.Abs(old - power) > 0.05f) _dirty = true;
        }
    }

    // ---- sounds --------------------------------------------------------------------------------

    private static readonly Dictionary<int, HashSet<int>> Inside = new();
    private static readonly Dictionary<int, float> LastWhoosh = new();
    private static float _nextListen;

    // A whoosh as a capsule is shot out of a powered booster nearby (only boosters within earshot are looked at).
    private static void Whooshes()
    {
        var now = Time.unscaledTime;
        if (now < _nextListen) return;
        _nextListen = now + 0.1f;
        var eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        foreach (var booster in Belts.OfType(Tubes.Booster))
        {
            if (!Outlets.TryGetValue(booster, out var outlet))
            {
                var path = Belts.PathOf(booster);
                if (path == null) continue;
                Outlets[booster] = outlet = path.Point(path.Length);
            }
            var power = BoosterPower.TryGetValue(booster, out var p) ? p : 0f;
            if (power <= 0.2f || (outlet - eye).sqrMagnitude > 40f * 40f) { Inside.Remove(booster); continue; }
            var riders = new HashSet<int>(Belts.Riders(booster).Select(r => r.Id));
            if (Inside.TryGetValue(booster, out var before))
                foreach (var capsule in before)
                {
                    if (riders.Contains(capsule)) continue;
                    if (LastWhoosh.TryGetValue(booster, out var last) && now - last < 0.12f) break;
                    LastWhoosh[booster] = now;
                    GameSounds.PlayAt("vehicle-boost", outlet, 0.22f * power, 22f, UnityEngine.Random.Range(1.25f, 1.5f));
                    break;
                }
            Inside[booster] = riders;
        }
    }

    // Each booster's outlet (cleared when the network changes).
    private static readonly Dictionary<int, Vector3> Outlets = new();
    // ---- colours -------------------------------------------------------------------------------

    private static Color ColorFor(float pressure)
    {
        // Saturated colours and moderate glow: brighter glows wash out towards white/cyan in the game's tone mapping.
        var cb = Settings.ColorBlind.Value;
        Color full = cb ? new Color(0.1f, 0.4f, 1f) : new Color(0.12f, 1f, 0.08f);
        Color fading = cb ? new Color(1f, 0.5f, 0.02f) : new Color(1f, 0.78f, 0.02f);
        Color none = cb ? new Color(0.5f, 0.5f, 0.5f) : new Color(1f, 0.1f, 0.06f);
        if (pressure >= 0.999f) return full;
        if (pressure <= 0.001f) return none;
        return Color.Lerp(fading, full, pressure * pressure * 0.5f);
    }

    private static float Intensity => _bright ? 1.4f : 0.5f;

    public static void Recolor(int belt)
    {
        var boosterPower = BoosterPower.TryGetValue(belt, out var bp) ? bp : 0f;
        if (Tubes.Air.TryGetValue(belt, out var air))
            foreach (var (puffs, rate) in air) puffs.Rate = rate * boosterPower;
        if (Tubes.Hiss.TryGetValue(belt, out var hiss) && hiss != null && !hiss.WasCollected) hiss.volume = 0.13f * boosterPower;
        if (!Tubes.Rings.TryGetValue(belt, out var rings)) return;
        var isBooster = Belts.TypeOf(belt) == Tubes.Booster;
        var hasStart = StartDistance.TryGetValue(belt, out var start);
        foreach (var (ring, at) in rings)
        {
            if (ring == null || ring.WasCollected) continue;
            float pressure;
            if (isBooster) pressure = BoosterPower.TryGetValue(belt, out var p) ? p : 0f;
            else pressure = hasStart ? At(start + at) : 0f;
            BeltLooks.SetGlow(ring, ColorFor(pressure), Intensity);
        }
    }

    /// <summary>Colours a tube splitter's or merger's ring by the pressure through it.</summary>
    public static void RecolorJunction(int structure)
    {
        if (!Tubes.JunctionRings.TryGetValue(structure, out var rings)) return;
        var pressure = JunctionPressure.TryGetValue(structure, out var p) ? p : 0f;
        foreach (var ring in rings)
            if (ring != null && !ring.WasCollected) BeltLooks.SetGlow(ring, ColorFor(pressure), Intensity);
    }

    private static void RecolorAll()
    {
        foreach (var structure in Tubes.JunctionRings.Keys.ToList())
        {
            if (Junctions.TypeOf(structure) is null) { Tubes.JunctionRings.Remove(structure); continue; }
            RecolorJunction(structure);
        }
        foreach (var belt in Tubes.Air.Keys.ToList()) if (!Belts.Exists(belt)) Tubes.Air.Remove(belt);
        foreach (var belt in Tubes.Rings.Keys.Concat(Tubes.Air.Keys).Distinct().ToList())
        {
            if (!Belts.Exists(belt)) { Tubes.Rings.Remove(belt); continue; }
            Recolor(belt);
        }
    }
}
