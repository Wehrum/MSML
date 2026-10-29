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
    private static float _nextSolve, _nextSettings, _nextCrewSolve;
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
        Lengths.Clear();
        Middles.Clear();
        Outlets.Clear();
        foreach (var booster in LastWhoosh.Keys.ToList()) if (!Belts.Exists(booster)) { LastWhoosh.Remove(booster); Inside.Remove(booster); }
    }

    // Each piece's length and each booster's middle, kept until the network changes (asking the game each solve was a
    // call per piece).
    private static readonly Dictionary<int, float> Lengths = new();
    private static readonly Dictionary<int, Vector3> Middles = new();
    private static int _settingsKey;

    private static float LengthOf(int belt)
    {
        if (!Lengths.TryGetValue(belt, out var l)) Lengths[belt] = l = Belts.PathOf(belt)?.Length ?? 0f;
        return l;
    }

    // The rings only need recolouring when the pressure they show changed.
    private static int _colorKey;

    /// <summary>Pressure (0-1) at a distance from a booster.</summary>
    private static float At(float distance) =>
        distance <= Full ? 1f : distance >= Full + Fade ? 0f : 1f - (distance - Full) / Fade;

    private static void Tick()
    {
        var now = Time.unscaledTime;
        // Solved again when the network changes (Dirty), and when a booster's power or the settings change: checked
        // once a second (power comes and goes with the sun and batteries). Solving every second regardless cost a few
        // milliseconds a second with thousands of tubes.
        if (!_dirty && now >= _nextSolve)
        {
            _nextSolve = now + 1f;
            // In a crew, belts also arrive from the network: worked out again now and then regardless, so nothing
            // missed stands for long.
            if (CrewMessages.InCrew && now >= _nextCrewSolve) { _nextCrewSolve = now + 5f; _dirty = true; }
            try
            {
                UpdatePowerUsers();
                var settings = HashCode.Combine(Speed, Full, Fade, Kw, Settings.ColorBlind.Value);
                if (settings != _settingsKey) { _settingsKey = settings; _dirty = true; }
            }
            catch (Exception e) { Plugin.Logger.LogError("Checking the boosters' power failed: " + e); }
        }
        if (_dirty)
        {
            try { UpdatePowerUsers(); }
            catch (Exception e) { Plugin.Logger.LogError("Checking the boosters' power failed: " + e); }
            _dirty = false;
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

        // Nearest-booster distances (Dijkstra over tubes; a booster restarts the count, so only seed from boosters).
        StartDistance.Clear();
        var queue = new PriorityQueue<int, float>();
        float Length(int belt) => LengthOf(belt);
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
        key = HashCode.Combine(key, Palette);
        if (key != _colorKey) { _colorKey = key; RecolorAll(); }
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
            if (!Middles.TryGetValue(booster, out var middle))
            {
                var path = Belts.PathOf(booster);
                if (path == null) continue;
                Middles[booster] = middle = path.Point(path.Length / 2f);
            }
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

    // Every ring (tubes', boosters', junctions') shares one material coloured by a palette of the pressure colours, each
    // ring's u coordinate picking its colour: so they're all drawn together. A material (property block) and texture
    // per tube made the GPU set up and draw each tube's rings on their own: over 3 ms a frame with ~600 tubes in view.
    private const int PaletteSize = 64;
    private static Texture2D? _palette;
    private static Material? _ringMaterial;
    private static int _paletteKey = int.MinValue;

    /// <summary>The material every ring is drawn with (recolour rings with <see cref="RingU"/>).</summary>
    public static Material RingMaterial()
    {
        EnsurePalette();
        return _ringMaterial!;
    }

    private static void EnsurePalette()
    {
        if (_palette == null || _palette.WasCollected || _ringMaterial == null || _ringMaterial.WasCollected)
        {
            _palette = new Texture2D(PaletteSize, 1, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "MSML pressure colours", hideFlags = HideFlags.DontUnloadUnusedAsset };
            _ringMaterial = PaletteGlow(_palette, Intensity);
            _paletteKey = int.MinValue;
        }
        var key = HashCode.Combine(Settings.ColorBlind.Value, _bright);
        if (key == _paletteKey) return;
        _paletteKey = key;
        var colors = new Color[PaletteSize];
        for (var i = 0; i < PaletteSize; i++) colors[i] = ColorFor(i / (PaletteSize - 1f));
        _palette.SetPixels(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color>(colors));
        _palette.Apply(false);
        _ringMaterial.SetColor("_EmissionColor", Color.white * Intensity);
    }

    /// <summary>The u coordinate that shows a pressure's colour with <see cref="RingMaterial"/>.</summary>
    public static float RingU(float pressure) =>
        PaletteU(pressure >= 0.999f ? PaletteSize - 1 : pressure <= 0.001f ? 0 : Mathf.Clamp(Mathf.RoundToInt(pressure * (PaletteSize - 1)), 1, PaletteSize - 2));

    // (Kept here rather than using the MSML API's copies, so this works with older MSML releases too.)
    private static float PaletteU(int pixel) => (pixel + 0.5f) / PaletteSize;

    private static Material PaletteGlow(Texture2D palette, float intensity)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MSML pressure rings" };
        m.SetColor("_BaseColor", Color.white);
        m.SetTexture("_BaseMap", palette);
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        m.SetTexture("_EmissionMap", palette);
        m.SetColor("_EmissionColor", Color.white * intensity);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return m;
    }

    // Sets a mesh's u coordinates: vertices in runs of 'run', run i getting u[i] (one run: all the same).
    internal static void SetU(Mesh mesh, IReadOnlyList<float> u, int run)
    {
        var uv = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2>(mesh.vertexCount);
        var span = uv.AsSpan();
        for (var i = 0; i < span.Length; i++) span[i] = new Vector2(u[Math.Min(u.Count - 1, i / run)], 0.5f);
        mesh.uv = uv;
    }

    // A ring renderer's mesh, recoloured as a whole.
    private static void SetRing(Renderer ring, float pressure)
    {
        if (ring == null || ring.WasCollected) return;
        var mesh = ring.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh != null) SetU(mesh, new[] { RingU(pressure) }, int.MaxValue);
    }

    // What each piece's rings (and air) and each junction's ring last showed: only those whose pressure (or the
    // colours: ranges, colour-blind, brightness) changed are recoloured. Recolouring every ring in the world on each
    // change was thousands of calls into the game.
    private static readonly Dictionary<int, int> Shown = new();
    private static readonly Dictionary<int, int> ShownJunction = new();

    // What the rings' pressures depend on besides each piece's own state (the colours and brightness are the shared
    // material's: changing them recolours nothing).
    private static int Palette => HashCode.Combine(Full, Fade);

    private static int ShowKey(int belt) => HashCode.Combine(Palette,
        StartDistance.TryGetValue(belt, out var d) ? (int)(d * 4f) : int.MinValue,
        BoosterPower.TryGetValue(belt, out var p) ? (int)(p * 50f) : int.MinValue);

    private static int JunctionKey(int structure) =>
        HashCode.Combine(Palette, JunctionPressure.TryGetValue(structure, out var p) ? (int)(p * 50f) : int.MinValue);

    public static void Recolor(int belt)
    {
        EnsurePalette();
        Shown[belt] = ShowKey(belt);
        var boosterPower = BoosterPower.TryGetValue(belt, out var bp) ? bp : 0f;
        if (Tubes.Air.TryGetValue(belt, out var air))
            foreach (var (puffs, rate) in air) puffs.Rate = rate * boosterPower;
        if (Tubes.Hiss.TryGetValue(belt, out var hiss) && hiss != null && !hiss.WasCollected) hiss.volume = 0.13f * boosterPower;
        var hasStart = StartDistance.TryGetValue(belt, out var start);
        if (Tubes.RingStrips.TryGetValue(belt, out var strip) && strip.Mesh != null && !strip.Mesh.WasCollected)
        {
            var u = new float[strip.At.Length];
            for (var i = 0; i < u.Length; i++) u[i] = RingU(hasStart ? At(start + strip.At[i]) : 0f);
            SetU(strip.Mesh, u, strip.PerRing);
        }
        if (!Tubes.Rings.TryGetValue(belt, out var rings)) return;
        var isBooster = Belts.TypeOf(belt) == Tubes.Booster;
        foreach (var (ring, at) in rings)
        {
            float pressure;
            if (isBooster) pressure = BoosterPower.TryGetValue(belt, out var p) ? p : 0f;
            else pressure = hasStart ? At(start + at) : 0f;
            SetRing(ring, pressure);
        }
    }

    /// <summary>Colours a tube splitter's or merger's ring by the pressure through it.</summary>
    public static void RecolorJunction(int structure)
    {
        EnsurePalette();
        ShownJunction[structure] = JunctionKey(structure);
        if (!Tubes.JunctionRings.TryGetValue(structure, out var rings)) return;
        var pressure = JunctionPressure.TryGetValue(structure, out var p) ? p : 0f;
        foreach (var ring in rings) SetRing(ring, pressure);
    }

    private static void RecolorAll()
    {
        EnsurePalette();
        foreach (var structure in Tubes.JunctionRings.Keys.ToList())
        {
            if (Junctions.TypeOf(structure) is null) { Tubes.JunctionRings.Remove(structure); ShownJunction.Remove(structure); continue; }
            if (!ShownJunction.TryGetValue(structure, out var shown) || shown != JunctionKey(structure)) RecolorJunction(structure);
        }
        // Pieces no longer tubes or boosters (removed ones lose their type) are forgotten.
        foreach (var belt in Tubes.Air.Keys.ToList()) if (Belts.TypeOf(belt) is null) Tubes.Air.Remove(belt);
        foreach (var belt in Tubes.Rings.Keys.Concat(Tubes.RingStrips.Keys).Concat(Tubes.Air.Keys).Distinct().ToList())
        {
            if (Belts.TypeOf(belt) is null) { Tubes.Rings.Remove(belt); Tubes.DropRingStrip(belt); Shown.Remove(belt); continue; }
            if (!Shown.TryGetValue(belt, out var shown) || shown != ShowKey(belt)) Recolor(belt);
        }
    }
}
