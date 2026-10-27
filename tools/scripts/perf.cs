// Frame-time profiler: times the game's per-frame code (every Update / LateUpdate / FixedUpdate, plus the heavy
// world steps) and every mod hook (methods with Harmony attributes in the MSML / mod assemblies, and their per-frame
// ticks), then reports where the frame goes, the frame-time spread, and what ran in the spikes.
//   .\tools\cs.ps1 tools\scripts\perf.cs [seconds] [ab] [base]
//     seconds  how long each phase measures (default 10)
//     ab       also measure with the mods' belt/junction looks hidden, and with capsule posing off, then normal again
//     base     teleport to the middle of the moon's belts first
//     novsync  turn vsync off while measuring (frame times are then the real cost, not rounded up to the screen's
//              refresh), put back after
// Inclusive times: a method's time includes what it calls (e.g. MoonWorld.Update includes the belt hooks it runs).

using MSML;
using System.Diagnostics;
using System.Reflection;

public static class Perf
{
    sealed class Stat { public string Name = ""; public bool Mod; public long Frame, Total, Worst; public int FrameCalls, Calls; }

    static readonly Dictionary<MethodBase, Stat> Stats = new();
    static readonly List<Stat> All = new();
    static readonly List<float> Frames = new();
    static readonly List<(float Ms, string Top)> Spikes = new();
    static bool _measuring;
    static bool _noPoses;
    static int _lastNet; static long _lastIl; static float _median = 1000f;

    public static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void Post(MethodBase __originalMethod, long __state)
    {
        if (!_measuring || !Stats.TryGetValue(__originalMethod, out var s)) return;
        s.Frame += Stopwatch.GetTimestamp() - __state;
        s.FrameCalls++;
    }

    public static bool SkipPose() => !_noPoses;

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    static readonly HashSet<string> TickNames = new()
    {
        "Tick", "Solve", "UpdatePowerUsers", "Whooshes", "RecolorAll", "RejoinPipes", "SyncStyles", "Reconcile", "MapRiders",
        "ApplySpeeds", "LetMoversThrough", "RehideAll", "SyncGhost", "WatchPlacements", "Pose", "PutBackTick", "ApplyKitLength",
        "Poll", "Restyle", "StyleTube", "StyleBooster", "StyleJunction", "MatchAwaiting", "Serialize", "RaiseChanged", "RaiseUpdate",
    };

    static readonly string[] GameExtras =
    {
        "MoonWorld.StepConveyors", "MoonWorld.ProcessToolStrokes", "MoonWorld.UpdateDustBanks", "MoonWorld.UpdateProgress",
        "MoonCanisterStreaming.WorkOut", "MoonDustCanisterView.Show", "MoonConveyorNetwork.RebuildAt", "MoonConveyorNetwork.RebuildJoined",
        "MoonConveyorNetwork.Spawn", "MoonWorld.BoardCanisters", "MoonWorld.UpdatePower", "MoonWorld.StepMachines",
    };

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Select(a => float.TryParse(a, out var v) ? v : 0f).FirstOrDefault(v => v > 0f);
        if (seconds <= 0f) seconds = 10f;
        var ab = ctx.Args.Contains("ab");
        ctx.Timeout = seconds * (ab ? 4 : 1) + 90f;

        if (ctx.Args.Contains("base"))
        {
            var starts = Items<int>(World.conveyors.Keys).Where(k => k > 0).Select(k => World.conveyors[k].start).ToList();
            var mid = new Vector3(Median(starts.Select(p => p.x)), Median(starts.Select(p => p.y)), Median(starts.Select(p => p.z)));
            Player.Teleport(Ground(mid) + (mid - World.transform.position).normalized * 0.5f, true);
            yield return 3f;
        }

        // ---- patch ----
        var sw = Stopwatch.StartNew();
        var pre = new HarmonyLib.HarmonyMethod(typeof(Perf).GetMethod(nameof(Pre))) { priority = HarmonyLib.Priority.First };
        var post = new HarmonyLib.HarmonyMethod(typeof(Perf).GetMethod(nameof(Post))) { priority = HarmonyLib.Priority.Last };
        int failed = 0;
        void Hook(MethodBase m, string name, bool mod)
        {
            if (Stats.ContainsKey(m)) return;
            try
            {
                ctx.Harmony.Patch(m, prefix: pre, postfix: post);
                var s = new Stat { Name = name, Mod = mod };
                Stats[m] = s;
                All.Add(s);
            }
            catch { failed++; }
        }
        var game = typeof(MoonWorld).Assembly;
        foreach (var t in game.GetTypes())
        {
            if (t.IsGenericTypeDefinition || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
            foreach (var n in new[] { "Update", "LateUpdate", "FixedUpdate" })
                if (t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) is { } m)
                    Hook(m, $"{t.Name}.{n}", false);
        }
        foreach (var e in GameExtras)
        {
            var parts = e.Split('.');
            var t = game.GetTypes().FirstOrDefault(x => x.Name == parts[0]);
            foreach (var m in t?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(x => x.Name == parts[1]) ?? Enumerable.Empty<MethodInfo>())
                Hook(m, e, false);
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is { } n && (n.StartsWith("MSML") || n.StartsWith("MoonSweepMods.ModLoader") || n.StartsWith("MoonSweepMods.DebugMenu"))))
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition || t.IsInterface || t.Name.Contains('<')) continue;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName || m.Name.Contains('<')) continue;
                    var harmony = m.GetCustomAttributes(false).Any(a => a.GetType().Namespace == "HarmonyLib" && !a.GetType().Name.Contains("Transpiler"));
                    if (!harmony && !TickNames.Contains(m.Name)) continue;
                    Hook(m, $"{asm.GetName().Name!.Replace("MSML.", "").Replace("MoonSweepMods.", "")}:{t.Name}.{m.Name}", true);
                }
            }
        }
        // Capsule posing can be switched off for a phase.
        var poseMethod = typeof(Belts).Assembly.GetType("MSML.BeltState")!.GetMethod("Pose", BindingFlags.Static | BindingFlags.NonPublic);
        if (poseMethod != null) ctx.Harmony.Patch(poseMethod, prefix: new HarmonyLib.HarmonyMethod(typeof(Perf).GetMethod(nameof(SkipPose))) { priority = HarmonyLib.Priority.First });
        ctx.Log($"timing {All.Count} methods ({All.Count(s => s.Mod)} mod, {All.Count(s => !s.Mod)} game; {failed} couldn't be patched) - patched in {sw.ElapsedMilliseconds} ms");
        ctx.Log($"at {Player.transform.position}; belts {World.conveyors.Count}, typed {Belts.Types.Sum(t => Belts.OfType(t).Count)}, riding capsules {World.RidingCanisterCount}, structures {World.depots.Count}; quality {QualitySettings.GetQualityLevel()} vsync {QualitySettings.vSyncCount} target fps {Application.targetFrameRate}");

        var vsync = QualitySettings.vSyncCount; var fps = Application.targetFrameRate;
        if (ctx.Args.Contains("novsync")) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
        var phases = ab ? new[] { "normal", "looks hidden", "posing off", "normal again" } : new[] { "normal" };
        ctx.OnUpdate(EndFrame);
        try
        {
            foreach (var phase in phases)
            {
                SetLooksHidden(phase == "looks hidden");
                _noPoses = phase == "posing off";
                yield return 1.5f;
                Reset();
                var gc0 = GC.CollectionCount(0); var gc2 = GC.CollectionCount(2);
                long il0 = IlGc();
                _measuring = true;
                var until = Time.unscaledTime + seconds;
                while (Time.unscaledTime < until) yield return null;
                _measuring = false;
                Report(ctx, phase, GC.CollectionCount(0) - gc0, GC.CollectionCount(2) - gc2, IlGc() - il0);
            }
        }
        finally
        {
            _measuring = false;
            _noPoses = false;
            SetLooksHidden(false);
            QualitySettings.vSyncCount = vsync; Application.targetFrameRate = fps;
            ctx.Harmony.UnpatchSelf();
        }
    }

    static long IlGc() { try { return Il2CppSystem.GC.CollectionCount(0); } catch { return -1; } }

    static float Median(IEnumerable<float> values) { var l = values.OrderBy(v => v).ToList(); return l.Count == 0 ? 0 : l[l.Count / 2]; }

    static readonly List<Renderer> Hidden = new();
    static void SetLooksHidden(bool hide)
    {
        if (!hide)
        {
            foreach (var r in Hidden) if (r != null && !r.WasCollected) r.enabled = true;
            Hidden.Clear();
            return;
        }
        foreach (var t in FindAll<Transform>())
        {
            if (t.parent != null || !(t.name.StartsWith("MSML belt ") || t.name.StartsWith("MSML junction "))) continue;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; Hidden.Add(r); }
        }
    }

    static void Reset()
    {
        Frames.Clear(); Spikes.Clear();
        _median = 1000f; _lastNet = GC.CollectionCount(0); _lastIl = IlGc();
        foreach (var s in All) { s.Frame = s.Total = s.Worst = 0; s.FrameCalls = s.Calls = 0; }
    }

    static void EndFrame()
    {
        if (!_measuring) { foreach (var s in All) { s.Frame = 0; s.FrameCalls = 0; } return; }
        var ms = Time.unscaledDeltaTime * 1000f;
        Frames.Add(ms);
        foreach (var s in All)
        {
            s.Total += s.Frame; s.Calls += s.FrameCalls;
            if (s.Frame > s.Worst) s.Worst = s.Frame;
        }
        // A garbage collection this frame (.NET: the mods' own memory; game: the game's heap, which calls from mods
        // into the game also fill).
        var net = GC.CollectionCount(0);
        var il = IlGc();
        var gc = (net != _lastNet ? "[.NET GC] " : "") + (il != _lastIl ? "[game GC] " : "");
        _lastNet = net;
        _lastIl = il;
        // The spike threshold comes from the first second's median (sorting every frame would itself allocate).
        if (Frames.Count == 60) { var sorted = Frames.OrderBy(f => f).ToList(); _median = sorted[30]; }
        if (Frames.Count > 60 && ms > Math.Max(_median * 1.8f, _median + 12f))
            Spikes.Add((ms, gc + string.Join(", ", All.Where(s => Ms(s.Frame) > 0.5).OrderByDescending(s => s.Frame).Take(6).Select(s => $"{s.Name} {Ms(s.Frame):F1}"))));
        foreach (var s in All) { s.Frame = 0; s.FrameCalls = 0; }
    }

    static void Report(ScriptContext ctx, string phase, int gc0, int gc2, long ilgc)
    {
        var n = Math.Max(1, Frames.Count);
        var sorted = Frames.OrderBy(f => f).ToList();
        float P(float q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
        ctx.Log($"\n=== {phase}: {Frames.Count} frames, avg {Frames.DefaultIfEmpty().Average():F1} ms ({1000f / Math.Max(0.01f, Frames.DefaultIfEmpty().Average()):F0} fps), median {P(0.5f):F1}, p95 {P(0.95f):F1}, p99 {P(0.99f):F1}, max {P(1f):F1}; spikes {Spikes.Count}; GC: .NET gen0 {gc0} gen2 {gc2}, game {ilgc}");
        var gameTop = All.Where(s => !s.Mod && s.Calls > 0).Sum(s => 0L);
        ctx.Log($"{"method",-58} {"ms/frame",8} {"worst",7} {"calls/f",8}");
        foreach (var s in All.Where(s => s.Calls > 0).OrderByDescending(s => s.Total).Take(32))
            ctx.Log($"{(s.Mod ? "* " : "  ") + s.Name,-58} {Ms(s.Total) / n,8:F3} {Ms(s.Worst),7:F2} {(double)s.Calls / n,8:F1}");
        var modTotal = All.Where(s => s.Mod && s.Name.Contains(":") && IsTopLevelHook(s.Name)).Sum(s => Ms(s.Total)) / n;
        ctx.Log($"mod hooks (top-level, * above): {modTotal:F2} ms/frame");
        foreach (var (ms, top) in Spikes.OrderByDescending(s => s.Ms).Take(8)) ctx.Log($"  spike {ms:F0} ms: {top}");
    }

    // Harmony callbacks and event handlers the game calls directly (not helpers they call, which are counted inside them).
    static bool IsTopLevelHook(string name) => name.Contains(".Before") || name.Contains(".After") || name.Contains(".Note") || name.EndsWith(".Tick") && name.Contains("Pressure");
}
