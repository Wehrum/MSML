// Better Belts at scale: lays a field of boosted tube lines (each a booster and nine 10 m tubes), feeds capsules into
// them, and measures frames (with the field in view), garbage collections, and the cost of working out pressure and of
// drawing pieces. RUN IT IN A THROWAWAY SAVE: it builds thousands of pieces, and the game may save while they exist.
//   .\tools\cs.ps1 tools\scripts\tube-scale-test.cs build [lines=200] [keep]   build, measure, then remove (keep: leave it)
//   .\tools\cs.ps1 tools\scripts\tube-scale-test.cs remove                     remove a field left by "keep"
//   .\tools\cs.ps1 tools\scripts\tube-scale-test.cs convert [every=8] [keep]  every plain belt in the save becomes a
//                                                                             tube (every Nth a booster), with the capsules
//                                                                             already on them; measured, then put back
//   .\tools\cs.ps1 tools\scripts\tube-scale-test.cs revert                     put back a base left by "convert ... keep"
// The test boosters get full power while the script is loaded (with "keep" until "remove"), wherever they are.
// With "keep", run tools\scripts\perf.cs next for a per-method breakdown.

using System.Reflection;
using MSML;

public static class TubeScaleTest
{
    const int TubesPerLine = 9;
    const float TubeLength = 10f;
    const float LineGap = 2.4f;       // between parallel lines
    const float RowGap = 20f;         // between the end of one row of lines and the start of the next
    static readonly string IdsFile = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots", "tube-scale-ids.txt");
    static readonly string ProgressFile = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots", "tube-scale-progress.txt");

    // A line in the progress file (readable while the script runs: Get-Content -Wait).
    static void Progress(string line, bool restart = false)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        if (restart) System.IO.File.WriteAllText(ProgressFile, text); else System.IO.File.AppendAllText(ProgressFile, text);
    }

    static readonly HashSet<int> Boosters = new();
    static PropertyInfo _power, _grid;
    static System.Collections.IDictionary _users;

    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 1800;
        var w = World;
        if (ctx.Args.Length == 0) { ctx.Log("usage: build [lines] [keep] | remove | convert [every] [keep] | revert  (in a throwaway save)"); yield break; }
        // Frame times as they are, not rounded up to the screen's refresh by vsync.
        var vsync = QualitySettings.vSyncCount; var fps = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
        ctx.OnUnload(() => { QualitySettings.vSyncCount = vsync; Application.targetFrameRate = fps; });
        if (ctx.Args[0] == "remove") { yield return Remove(ctx, ReadIds()); yield break; }
        if (ctx.Args[0] == "convert") { yield return Convert(ctx, ctx.Args.Length > 1 && int.TryParse(ctx.Args[1], out var every) ? every : 8, ctx.Args.Contains("keep")); yield break; }
        if (ctx.Args[0] == "revert") { Revert(ctx, ReadIds(ConvertFile)); yield break; }
        if (ctx.Args[0] != "build") { ctx.Log("usage: build [lines] [keep] | remove"); yield break; }
        var lines = ctx.Args.Length > 1 && int.TryParse(ctx.Args[1], out var n) ? n : 200;
        var keep = ctx.Args.Contains("keep");
        if (ReadIds().Count > 0) { ctx.Log("A field from an earlier run is still there: run 'remove' first."); yield break; }

        var tube = Belts.TypeById("msml.betterbelts.tube");
        var booster = Belts.TypeById("msml.betterbelts.booster");
        var builder = Find<MoonDepotBuilder>();
        var money = Money;
        Money = 1e9;
        ctx.OnUnload(() => { try { Money = money; } catch { } });
        PowerTestBoosters(ctx);

        // The field: rows of parallel lines, laid out about as wide as deep, in the flattest direction from the player
        // (tubes can't go up slopes over 45 degrees, so a hill stops a line).
        var up = Up;
        var lineLength = 2.4f + TubesPerLine * TubeLength;
        var perRow = Math.Max(10, Math.Min(lines, (int)Math.Ceiling(Math.Sqrt(lines * (lineLength + RowGap) / LineGap))));
        var depth = ((lines + perRow - 1) / perRow) * (lineLength + RowGap);
        var width = perRow * LineGap;
        var (f, flatness) = FlattestDirection(Position, up, width, depth);
        var r = Vector3.Cross(up, f);
        var origin = Ground(Position + f * 15f);
        ctx.Log($"layout: {perRow} lines per row, {width:F0} m wide x {depth:F0} m deep; flattest direction: steepest step {flatness:F0} deg");
        Vector3 LineStart(int i) => Ground(origin + f * ((i / perRow) * (lineLength + RowGap)) + r * ((i % perRow - perRow / 2f) * LineGap));
        var viewFrom = Ground(origin - f * 6f) + up * 4f;
        var viewAt = origin + f * Math.Min(60f, depth / 2f);

        ctx.Log($"field: {lines} lines x ({1 + TubesPerLine} pieces) = {lines * (1 + TubesPerLine)} pieces, {lines * lineLength / 1000f:F1} km of tube");
        var quick = ctx.Args.Contains("quick");   // just build (and remove unless keep): no measuring
        var baseline = new Stats();
        if (!quick)
        {
            yield return Look(viewFrom, viewAt);
            yield return baseline.Measure(10f);
            ctx.Log("before:  " + baseline);
        }

        // Build row by row: go there first and let the ground load (far ground isn't there to measure until then), then
        // each line: its booster and its tubes, a few pieces a frame. Progress goes to a file as it goes.
        var ids = new List<int>();
        var failed = new Dictionary<string, int>();
        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        builder.SetOpen(true);
        var rows = (lines + perRow - 1) / perRow;
        Progress($"building {lines} lines in {rows} rows", true);
        var built = 0;
        for (var row = 0; row < rows; row++)
        {
            var rowCentre = origin + f * (row * (lineLength + RowGap) + lineLength / 2f);
            Player.Teleport(rowCentre + up * 3f, true);
            yield return 1f;
            var rowBuilt = 0;
            for (var i = row * perRow; i < Math.Min(lines, (row + 1) * perRow); i++)
            {
                var s = LineStart(i) + up * 0.6f;
                if (!Build(w, Plan(s, Ground(s + f * 2.4f) + up * 0.6f, f, 0), booster.Kind, out var last, failed)) continue;
                ids.Add(last); Boosters.Add(last); rowBuilt++;
                for (var t = 0; t < TubesPerLine; t++)
                {
                    var from = w.conveyors[last].end;
                    var e = Ground(from + f * TubeLength) + up * 0.6f;
                    if (!Build(w, Plan(from, e, f, last), tube.Kind, out var id, failed)) break;
                    ids.Add(id); last = id; rowBuilt++;
                    if (++built % 8 == 0) yield return null;
                }
            }
            WriteIds(ids);
            Progress($"row {row + 1}/{rows}: {rowBuilt} pieces; {ids.Count} so far; refused: " + string.Join(", ", failed.Select(x => $"{x.Key} x{x.Value}")));
        }
        builder.SelectStructure(MoonDepotBuilder.ConveyorKind);
        builder.SetOpen(false);
        buildWatch.Stop();
        ctx.Log($"built {ids.Count} pieces ({Boosters.Count} boosters) in {buildWatch.Elapsed.TotalSeconds:F0} s" +
                (failed.Count > 0 ? "; not built: " + string.Join(", ", failed.Select(x => $"{x.Key} x{x.Value}")) : ""));

        yield return Look(viewFrom, viewAt);
        if (quick)
        {
            yield return 2f;
            ctx.Log($"{ids.Count(id => Belts.SpeedOf(id) > 1.01f)} of {ids.Count} pieces sped up, {ids.Count(id => Belts.TypeOf(id) == null)} untyped");
            ctx.Log(Shot("tube-scale"));
            if (keep) { ctx.KeepLoaded = true; ctx.Log($"kept {ids.Count} pieces"); }
            else yield return Remove(ctx, ids);
            yield break;
        }
        yield return 4f;   // pressure and styles settle
        var pressure = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "MSML.BetterBelts").GetTypes().First(t => t.Name == "Pressure");
        var solve = pressure.GetMethod("Solve", BindingFlags.NonPublic | BindingFlags.Static);
        var solveMs = new List<double>();
        for (var k = 0; k < 5; k++) { var sw = System.Diagnostics.Stopwatch.StartNew(); solve.Invoke(null, null); solveMs.Add(sw.Elapsed.TotalMilliseconds); yield return null; }
        var sped = ids.Count(id => Belts.SpeedOf(id) > 1.01f);
        ctx.Log($"pressure: solving takes {solveMs.Skip(1).Average():F2} ms (worst {solveMs.Max():F2}); {sped} of {ids.Count} pieces sped up");
        var restyle = new List<double>();
        foreach (var id in ids.Where(x => !Boosters.Contains(x)).Take(50)) { var sw = System.Diagnostics.Stopwatch.StartNew(); Belts.Restyle(id); restyle.Add(sw.Elapsed.TotalMilliseconds); }
        if (restyle.Count > 0) ctx.Log($"drawing a 10 m tube: {restyle.Average():F2} ms (worst {restyle.Max():F2})");
        var empty = new Stats(); yield return empty.Measure(15f);
        ctx.Log("built, no capsules:  " + empty);
        ctx.Log(Shot("tube-scale"));

        // Capsules: loose ones, and new ones packed from dust, fed onto the boosters as they make room.
        var capsules = Items<int>(w.canisters.Keys).Where(c => !w.IsCanisterRiding(c) && !w.IsCanisterCarried(c) && !w.IsCanisterFlying(c)).ToList();
        var packed = 0;
        for (var k = 0; k < 3000 && capsules.Count < 4000; k++)
        {
            var at = Ground(origin - f * 3f + r * ((k % 40) * 0.8f - 16f));
            if (!w.PackDustCanister(PlayerId, at + up * 0.3f, up, out var made) || made == 0) break;
            capsules.Add(made); packed++;
            if (k % 50 == 49) yield return null;
        }
        ctx.Log($"capsules: {capsules.Count} ({packed} packed from dust)");
        var queue = new Queue<int>(capsules);
        var riding = 0;
        var feedUntil = Time.unscaledTime + 30f;
        while (Time.unscaledTime < feedUntil && queue.Count > 0)
        {
            foreach (var b in Boosters)
            {
                if (queue.Count == 0) break;
                var c = queue.Peek();
                if (!w.canisters.ContainsKey(c)) { queue.Dequeue(); continue; }
                if (w.Enter(b, c)) { queue.Dequeue(); riding++; }
            }
            yield return 0.25f;
        }
        ctx.Log($"{riding} capsules fed in");
        yield return Look(viewFrom, viewAt);
        var full = new Stats(); yield return full.Measure(20f);
        ctx.Log($"with {riding} capsules riding:  " + full);
        ctx.Log(Shot("tube-scale-capsules"));

        if (keep) { ctx.KeepLoaded = true; ctx.Log($"kept: {ids.Count} pieces (ids in {IdsFile}); the test boosters stay powered until 'remove'"); }
        else yield return Remove(ctx, ids);
    }

    // ---- where to build ------------------------------------------------------------------------

    // The heading (of 12 around) whose field footprint has the gentlest steepest 10 m step along the lines, and that
    // step in degrees. Ground that isn't loaded counts as unusable.
    static (Vector3 Direction, float Steepest) FlattestDirection(Vector3 at, Vector3 up, float width, float depth)
    {
        var flat = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
        if (flat.sqrMagnitude < 0.5f) flat = Vector3.ProjectOnPlane(Vector3.right, up).normalized;
        var best = (Direction: Forward, Steepest: float.MaxValue);
        for (var h = 0; h < 12; h++)
        {
            var f = Quaternion.AngleAxis(h * 30f, up) * flat;
            var r = Vector3.Cross(up, f);
            var steepest = 0f;
            for (var x = -width / 2f; x <= width / 2f + 0.1f && steepest < best.Steepest; x += Math.Max(10f, width / 4f))
            {
                float? last = null;
                for (var d = 15f; d <= 15f + depth && steepest < best.Steepest; d += 10f)
                {
                    var p = at + f * d + r * x;
                    if (!Physics.Raycast(p + up * 300f, -up, out var hit, 600f)) { steepest = float.MaxValue; break; }
                    var height = Vector3.Dot(hit.point - at, up);
                    if (last is { } l) steepest = Math.Max(steepest, Mathf.Atan(Math.Abs(height - l) / 10f) * Mathf.Rad2Deg);
                    last = height;
                }
            }
            if (steepest < best.Steepest) best = (f, steepest);
        }
        return best;
    }

    // ---- the whole base as tubes ---------------------------------------------------------------

    static readonly string ConvertFile = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots", "tube-scale-converted.txt");

    // Every plain belt in the save becomes a tube (every Nth a booster, powered), with the capsules already riding them:
    // a big base switched over to tubes. Measured standing in the middle of it, before and after.
    static IEnumerator Convert(ScriptContext ctx, int boosterEvery, bool keep)
    {
        var w = World;
        if (ReadIds(ConvertFile).Count > 0) { ctx.Log("Already converted: run 'revert' first."); yield break; }
        var tube = Belts.TypeById("msml.betterbelts.tube");
        var booster = Belts.TypeById("msml.betterbelts.booster");
        var plain = Items<int>(w.conveyors.Keys).Where(k => k > 0 && Belts.TypeOf(k) == null).ToList();
        var riders = Items<int>(w.beltQueues.Keys).Where(k => k > 0).Sum(k => Belts.Riders(k).Count);
        PowerTestBoosters(ctx);

        // Stand where most belts are.
        var mids = plain.Select(k => Belts.PathOf(k)).Where(p => p != null).Select(p => p.Point(p.Length / 2f)).ToList();
        var centre = mids.Aggregate(Vector3.zero, (a, b) => a + b) / Math.Max(1, mids.Count);
        var stand = mids.OrderBy(m => (m - centre).sqrMagnitude).First();
        var up = stand.normalized;
        var from = Ground(stand + Vector3.Cross(up, Vector3.forward).normalized * 8f) + up * 0.3f;
        yield return Look(from, stand);
        ctx.Log($"base: {plain.Count} plain belts, {riders} capsules riding belts; standing at {from}");
        var before = new Stats(); yield return before.Measure(20f);
        ctx.Log("plain belts:  " + before);

        var converted = new List<int>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var worst = 0.0;
        for (var i = 0; i < plain.Count; i++)
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            var isBooster = i % boosterEvery == 0;
            Belts.SetType(plain[i], isBooster ? booster : tube);
            if (isBooster) Boosters.Add(plain[i]);
            converted.Add(plain[i]);
            worst = Math.Max(worst, one.Elapsed.TotalMilliseconds);
            if (i % 50 == 49) { WriteIds(converted, ConvertFile); yield return null; }
        }
        WriteIds(converted, ConvertFile);
        ctx.Log($"converted {converted.Count} belts ({Boosters.Count} boosters) in {watch.Elapsed.TotalMilliseconds:F0} ms of work, worst one {worst:F1} ms");
        yield return 5f;   // styles, joins and pressure settle

        var pressure = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "MSML.BetterBelts").GetTypes().First(t => t.Name == "Pressure");
        var solve = pressure.GetMethod("Solve", BindingFlags.NonPublic | BindingFlags.Static);
        var solveMs = new List<double>();
        for (var k = 0; k < 5; k++) { var sw = System.Diagnostics.Stopwatch.StartNew(); solve.Invoke(null, null); solveMs.Add(sw.Elapsed.TotalMilliseconds); yield return null; }
        var sped = converted.Count(id => Belts.SpeedOf(id) > 1.01f);
        ctx.Log($"pressure: solving takes {solveMs.Skip(1).Average():F2} ms (worst {solveMs.Max():F2}); {sped} of {converted.Count} pieces sped up");

        yield return Look(from, stand);
        var after = new Stats(); yield return after.Measure(20f);
        ctx.Log("all tubes:  " + after);
        ctx.Log(Shot("tube-scale-base"));

        if (keep) { ctx.KeepLoaded = true; ctx.Log("kept converted (run 'revert' to put the belts back); the boosters stay powered until then"); }
        else Revert(ctx, converted);
    }

    static void Revert(ScriptContext ctx, List<int> ids)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var n = 0;
        foreach (var id in ids) if (Belts.Exists(id) && Belts.TypeOf(id) != null) { Belts.SetType(id, null); n++; }
        Boosters.Clear();
        WriteIds(new List<int>(), ConvertFile);
        ctx.Log($"reverted {n} belts in {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    // ---- building ------------------------------------------------------------------------------

    static MoonWorld.ConveyorPlan Plan(Vector3 start, Vector3 end, Vector3 f, int after)
    {
        var up = start.normalized;
        var dir = Vector3.ProjectOnPlane(end - start, up).normalized;
        var plan = new MoonWorld.ConveyorPlan();
        plan.start = start; plan.end = end;
        plan.startDirection = dir; plan.endDirection = Vector3.ProjectOnPlane(end - start, end.normalized).normalized;
        plan.startUp = up; plan.endUp = end.normalized;
        plan.startBelt = after;
        return plan;
    }

    static bool Build(MoonWorld w, MoonWorld.ConveyorPlan plan, int kind, out int id, Dictionary<string, int> failed)
    {
        id = 0;
        // Built within the builder's reach: stand beside it (teleporting puts the card away: choose it again).
        var mid = (plan.start + plan.end) / 2f;
        Player.Teleport(Ground(mid + Vector3.Cross(mid.normalized, plan.startDirection) * 3f) + mid.normalized * 0.2f, true);
        var builder = Find<MoonDepotBuilder>();
        builder.SelectStructure(kind);
        builder.BeginPlacement();
        if (w.CheckConveyor(Player, plan, out var reason, out _, out _) && w.TryBuildConveyor(Player, plan, out reason, out id) && id != 0) return true;
        var key = reason ?? "?";
        if (key.Length > 60) key = key.Substring(0, 60);
        failed[key] = failed.TryGetValue(key, out var k) ? k + 1 : 1;
        return false;
    }

    static IEnumerator Remove(ScriptContext ctx, List<int> ids)
    {
        var w = World;
        var removed = 0;
        foreach (var id in ids.AsEnumerable().Reverse())
        {
            if (!w.conveyors.ContainsKey(id)) continue;
            var c = w.conveyors[id];
            var mid = (c.start + c.end) / 2f;
            Player.Teleport(Ground(mid + mid.normalized * 0f) + mid.normalized * 1f, true);
            if (w.TryRemoveConveyor(Player, id, out var reason)) removed++;
            if (removed % 10 == 9) yield return null;
            if (removed % 200 == 199) Progress($"removed {removed + 1} of {ids.Count}");
        }
        Boosters.Clear();
        WriteIds(new List<int>());
        ctx.Log($"removed {removed} of {ids.Count} pieces");
    }

    static List<int> ReadIds(string file = null) =>
        System.IO.File.Exists(file ??= IdsFile) ? System.IO.File.ReadAllText(file).Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList() : new List<int>();

    static void WriteIds(List<int> ids, string file = null)
    {
        file ??= IdsFile;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
        System.IO.File.WriteAllText(file, string.Join(",", ids));
    }

    // ---- full power for the test boosters ------------------------------------------------------

    // After the game (and MSML Power) work out the grids each frame: the test boosters get full power.
    static void PowerTestBoosters(ScriptContext ctx)
    {
        var bb = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "MSML.BetterBelts");
        _users = (System.Collections.IDictionary)bb.GetTypes().First(t => t.Name == "Pressure").GetField("Users", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        _power = typeof(PowerUser).GetProperty("Power");
        _grid = typeof(PowerUser).GetProperty("Grid");
        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonWorld), nameof(MoonWorld.SolvePower)),
            postfix: new HarmonyMethod(typeof(TubeScaleTest), nameof(AfterSolvePower)) { priority = Priority.Last });
        ctx.OnUnload(() => Boosters.Clear());
    }

    public static void AfterSolvePower()
    {
        foreach (var b in Boosters)
        {
            if (!_users.Contains(b)) continue;
            var user = _users[b];
            _power.SetValue(user, 1f);
            if ((int)_grid.GetValue(user) == 0) _grid.SetValue(user, 1);
        }
    }

    // ---- measuring -----------------------------------------------------------------------------

    static IEnumerator Look(Vector3 from, Vector3 at)
    {
        Player.Teleport(from, true);
        yield return 0.5f;
        var up = from.normalized;
        var to = at - Camera.main.transform.position;
        Player.heading = Vector3.ProjectOnPlane(to, up).normalized;
        Player.pitch = Vector3.SignedAngle(Vector3.ProjectOnPlane(to, up), to, Vector3.Cross(to, up).normalized);
        yield return 1f;
    }

    sealed class Stats
    {
        readonly List<float> _frames = new();
        int _gc0, _gc2; long _il;
        public IEnumerator Measure(float seconds)
        {
            _frames.Clear();
            _gc0 = GC.CollectionCount(0); _gc2 = GC.CollectionCount(2); _il = Il();
            var end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end) { yield return null; _frames.Add(Time.unscaledDeltaTime * 1000f); }
            _gc0 = GC.CollectionCount(0) - _gc0; _gc2 = GC.CollectionCount(2) - _gc2; _il = Il() - _il;
        }
        static long Il() { try { return Il2CppSystem.GC.CollectionCount(0); } catch { return -1; } }
        public override string ToString()
        {
            if (_frames.Count == 0) return "no frames";
            var sorted = _frames.OrderBy(x => x).ToList();
            float P(float q) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
            return $"{_frames.Count} frames, avg {_frames.Average():F1} ms ({1000f / _frames.Average():F0} fps), p95 {P(0.95f):F1}, p99 {P(0.99f):F1}, max {sorted[^1]:F1}, " +
                   $"over 33 ms: {_frames.Count(x => x > 33f)}; GC: .NET gen0 {_gc0} gen2 {_gc2}, game {_il}";
        }
    }
}
