// Real capsules through tube splitters and mergers: takes the busiest plain splitters and mergers in the save (and
// the belts in and out of them), measures capsules through them, turns them into tubes, boosters (where capsules come
// in) and tube splitters / mergers, and measures again: throughput, speeds, capsules that stop moving, and capsules
// drawn away from where they ride (popping out of the tube). Puts everything back after. Prints PASS/FAIL.
// RUN IT IN A THROWAWAY SAVE (it changes the base while it runs).
//   .\tools\cs.ps1 tools\scripts\capsule-test.cs [junctions=12] [seconds=25]

using System.Reflection;
using MSML;

public static class CapsuleTest
{
    // Capsules jumping where they're drawn (popping up or out of the tube): between two frames in a row, a capsule
    // drawn further from where it was than it can travel. Only capsules riding the belts and routes being watched.
    static readonly Dictionary<int, (Vector3 Drawn, int Frame)> Drawn = new();
    static readonly HashSet<int> Watching = new();
    static int _draws, _jumps; static float _worstJump;
    const float JumpLimit = 0.45f;  // metres in one frame (x10 is about 0.2 m a frame; popping up out of a tube is more)

    public static void AfterShow(MoonDroppedCanister c, Vector3 point)
    {
        if (c == null || !Watching.Contains(c.id)) return;
        var frame = Time.frameCount;
        if (Drawn.TryGetValue(c.id, out var last) && last.Frame == frame - 1)
        {
            _draws++;
            var jump = Vector3.Distance(point, last.Drawn);
            if (jump > JumpLimit) { _jumps++; _worstJump = Math.Max(_worstJump, jump); }
        }
        Drawn[c.id] = (point, frame);
    }

    public static IEnumerator Run(ScriptContext ctx)
    {
        var count = ctx.Args.Length > 0 && int.TryParse(ctx.Args[0], out var n) ? n : 12;
        var seconds = ctx.Args.Length > 1 && float.TryParse(ctx.Args[1], out var s) ? s : 25f;
        ctx.Timeout = 120 + seconds * 3;
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) pass++; else fail++; ctx.Log($"{(ok ? "PASS" : "FAIL")} {what}"); }

        var w = World;
        var tube = Belts.TypeById("msml.betterbelts.tube");
        var booster = Belts.TypeById("msml.betterbelts.booster");
        var splitter = Junctions.TypeById("msml.betterbelts.splitter");
        var merger = Junctions.TypeById("msml.betterbelts.merger");

        // The busiest plain splitters and mergers, near the busiest one (so their capsules are in view).
        var belts = Items<int>(w.conveyors.Keys).Where(k => k > 0 && Belts.TypeOf(k) == null).ToList();
        var traffic = new Dictionary<int, int>();
        foreach (var b in belts)
        {
            var riders = Belts.Riders(b).Count;
            if (riders == 0) continue;
            foreach (var (structure, kind) in new[] { Belts.EndsAt(b), Belts.StartsAt(b) })
                if (structure != 0 && Belts.JunctionKinds.Contains(kind) && Junctions.TypeOf(structure) == null)
                    traffic[structure] = (traffic.TryGetValue(structure, out var t) ? t : 0) + riders;
        }
        if (traffic.Count == 0) { ctx.Log("No plain splitters or mergers with capsules near them."); yield break; }
        Junctions.Pose(traffic.OrderByDescending(x => x.Value).First().Key, out var centre, out _);
        // As many splitters as mergers, and any joined straight to another (a merger into a splitter...).
        var near = traffic.Where(x => Junctions.Pose(x.Key, out var p, out _) && (p - centre).magnitude < 150f).OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
        var chosen = near.Where(j => w.depots[j].kind == 11).Take(count / 2).Concat(near.Where(j => w.depots[j].kind == 12).Take(count / 2)).ToList();
        foreach (var b in belts)
        {
            var (end, _) = Belts.EndsAt(b);
            if (end == 0 || !chosen.Contains(end)) continue;
            foreach (var j in Belts.JunctionsAfter(b)) if (!chosen.Contains(j) && Junctions.TypeOf(j) == null) chosen.Add(j);
        }

        // Their belts, one more belt out each way; the belts capsules come in on become boosters.
        var region = new HashSet<int>();
        foreach (var j in chosen)
            foreach (var b in belts)
            {
                if (Belts.EndsAt(b).Structure == j) { region.Add(b); foreach (var u in Belts.Upstream(b)) if (Belts.TypeOf(u) == null) region.Add(u); }
                if (Belts.StartsAt(b).Structure == j) { region.Add(b); foreach (var d in Belts.Downstream(b)) if (Belts.TypeOf(d) == null) region.Add(d); }
            }
        var entries = region.Where(b => !Belts.Upstream(b).Any(region.Contains)).ToHashSet();
        var routes = chosen.SelectMany(j => Belts.RoutesThrough(j)).ToList();
        var chains = chosen.Count(j => region.Any(b => Belts.EndsAt(b).Structure == j && Belts.JunctionsAfter(b).Count > 1));
        ctx.Log($"{chosen.Count} junctions ({chosen.Count(j => w.depots[j].kind == 11)} splitters, {chosen.Count(j => w.depots[j].kind == 12)} mergers; " +
                $"{chains} joined straight to another), {region.Count} belts ({entries.Count} entries), {routes.Count} routes, " +
                $"{region.Sum(b => Belts.Riders(b).Count)} capsules on them");

        // Stand among them; capsule views are drawn near the camera.
        Teleport(Ground(centre + Up * 0f) + centre.normalized * 2f);
        yield return 2f;
        var show = typeof(MoonDustCanisterView).GetMethods().First(m => m.Name == "Show" && m.GetParameters().Length == 3);
        ctx.Harmony.Patch(show, postfix: new HarmonyMethod(typeof(CapsuleTest).GetMethod(nameof(AfterShow))) { priority = Priority.Last });

        var before = new Watch(region, routes); yield return before.Run(seconds);
        ctx.Log("plain:  " + before);

        // Tubes.
        var typed = new List<int>();
        try
        {
            foreach (var b in region) { Belts.SetType(b, entries.Contains(b) ? booster : tube); typed.Add(b); }
            foreach (var j in chosen) Junctions.SetType(j, w.depots[j].kind == 11 ? splitter : merger);
            ctx.OnUnload(() => Revert(region, chosen));
        }
        catch (Exception e) { ctx.Log("converting failed: " + e.Message); }
        // The boosters run on the solar grid; in a test they may be far from one: full power while it runs.
        var pressure = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "MSML.BetterBelts").GetTypes().First(t => t.Name == "Pressure");
        _users = (System.Collections.IDictionary)pressure.GetField("Users", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        _boosters = entries;
        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonWorld), nameof(MoonWorld.SolvePower)), postfix: new HarmonyMethod(typeof(CapsuleTest).GetMethod(nameof(AfterSolvePower))) { priority = Priority.Last });
        yield return 4f;
        var sped = region.Count(b => Belts.SpeedOf(b) > 1.01f);
        var spedRoutes = routes.Count(r => Belts.SpeedOf(r) > 1.01f);
        ctx.Log($"sped up: {sped} of {region.Count} belts, {spedRoutes} of {routes.Count} routes (x{routes.Select(Belts.SpeedOf).DefaultIfEmpty(1f).Max():F1} at most)");

        Drawn.Clear();
        var after = new Watch(region, routes); yield return after.Run(seconds);
        ctx.Log("tubes:  " + after);

        Check(spedRoutes == routes.Count, $"every route through the junctions is sped up ({spedRoutes} of {routes.Count})");
        // Belts backed up behind busy machines pass what the machines take, tubes or not: within 15% is the same.
        Check(after.Passed >= before.Passed * 0.85f, $"as many capsules through the junctions as on plain belts ({after.Passed} vs {before.Passed} in {seconds:F0} s)");
        Check(after.Stuck <= Math.Max(2, before.Stuck * 1.5f), $"no more capsules stopped than on plain belts ({after.Stuck} vs {before.Stuck})");
        Check(after.Jumps <= before.Jumps, $"no capsules jumping where they're drawn ({after.Jumps} of {after.Draws} draws jumped over {JumpLimit} m, worst {after.WorstJump:F2} m; plain belts: {before.Jumps})");

        Revert(region, chosen);
        ctx.Log($"put back: {region.Count} belts, {chosen.Count} junctions");
        ctx.Log($"{pass} passed, {fail} failed");
    }

    static System.Collections.IDictionary _users;
    static HashSet<int> _boosters = new();
    static PropertyInfo _power = typeof(PowerUser).GetProperty("Power"), _grid = typeof(PowerUser).GetProperty("Grid");

    public static void AfterSolvePower()
    {
        if (_users == null) return;
        foreach (var b in _boosters)
        {
            if (!_users.Contains(b)) continue;
            var user = _users[b];
            _power.SetValue(user, 1f);
            if ((int)_grid.GetValue(user) == 0) _grid.SetValue(user, 1);
        }
    }

    static void Revert(HashSet<int> region, List<int> junctions)
    {
        foreach (var b in region) if (Belts.TypeOf(b) != null) Belts.SetType(b, null);
        foreach (var j in junctions) if (Junctions.TypeOf(j) != null) Junctions.SetType(j, null);
        _boosters = new HashSet<int>();
    }

    // Capsules through the junctions (new ones seen on their routes), capsules that didn't move for 8 s, and capsules
    // drawn more than a metre from where they ride.
    sealed class Watch
    {
        readonly HashSet<int> _belts; readonly List<int> _routes;
        public int Passed, Stuck, Jumps, Draws; public float WorstJump;
        public Watch(HashSet<int> belts, List<int> routes) { _belts = belts; _routes = routes; }

        public IEnumerator Run(float seconds)
        {
            var seenOnRoutes = new HashSet<int>();
            var last = new Dictionary<int, (int Belt, float At, float Since)>();
            var stuck = new HashSet<int>();
            var end = Time.unscaledTime + seconds;
            var frame = 0;
            Drawn.Clear(); Watching.Clear(); _draws = _jumps = 0; _worstJump = 0f;
            while (Time.unscaledTime < end)
            {
                yield return null;
                frame++;
                foreach (var r in _routes) foreach (var (_, id) in Belts.Riders(r)) seenOnRoutes.Add(id);
                if (frame % 5 != 0) continue;
                var now = Time.unscaledTime;
                foreach (var b in _belts.Concat(_routes))
                    foreach (var (at, id) in Belts.Riders(b))
                    {
                        Watching.Add(id);
                        if (!last.TryGetValue(id, out var was) || was.Belt != b || Math.Abs(was.At - at) > 0.05f) last[id] = (b, at, now);
                        else if (now - was.Since > 8f) stuck.Add(id);
                    }
            }
            Passed = seenOnRoutes.Count;
            Stuck = stuck.Count;
            Jumps = _jumps; Draws = _draws; WorstJump = _worstJump;
        }

        public override string ToString() => $"{Passed} capsules through the junctions, {Stuck} stopped for 8 s+, {Jumps} of {Draws} draws jumped (worst {WorstJump:F2} m)";
    }
}
