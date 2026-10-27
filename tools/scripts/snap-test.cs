// Belt snapping, driven through the build tool like a player (BuildTool): drawing freely on open ground, carrying on
// from a tube's end, starting and ending at a sunk tube splitter's / merger's mouth (JunctionType.PortSink), and
// ending at a machine's input. Builds nothing; prints PASS/FAIL per check. Finds its own spots near the player.
//   .\tools\cs.ps1 tools\scripts\snap-test.cs

using MSML;

public static class SnapTest
{
    static int _pass, _fail;
    static ScriptContext _ctx;

    static void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        _ctx.Log($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    // A belt end or port with nothing on it.
    static bool FreeOutput(MoonWorld w, Vector3 point) { w.Takers(point, out var b, out var s); return b == 0 && s == 0; }
    static bool FreeInput(MoonWorld w, Vector3 point) { w.Feeders(point, out var b, out var s); return b == 0 && s == 0; }

    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 240;
        _ctx = ctx; _pass = _fail = 0;
        var w = World;
        var builtBefore = w.nextConveyorId;
        var home = Position;
        var tube = Belts.TypeById("msml.betterbelts.tube");
        var kind = tube?.Kind ?? MoonDepotBuilder.ConveyorKind;
        BuildTool.Use(ctx);

        // 1. Free: the end follows the aim over open ground.
        {
            var up = Up; var f = Forward; var r = Vector3.Cross(up, f);
            var start = Ahead(3f);
            yield return BuildTool.Choose(kind);
            yield return BuildTool.StartBelt(start);
            foreach (var (along, side) in new[] { (7f, 0f), (10f, 1.5f), (12f, -1.5f) })
            {
                var aim = Ground(start + f * along + r * side);
                BuildTool.AimAt(aim); yield return 0.4f;
                var p = BuildTool.Builder.conveyorPlan;
                var off = Vector3.ProjectOnPlane(p.end - aim, up).magnitude;
                Check(off < 0.6f && p.endBelt == 0 && p.endStructure == 0, $"free end follows the aim ({off:F2} m off) | {BuildTool.Plan(start)}");
            }
        }

        // 2. Carrying on from a tube's free end.
        var ends = Belts.OfType(tube).Select(k => (k, p: Belts.PathOf(k))).Where(x => x.p != null)
            .Select(x => (x.k, x.p, end: x.p.Point(x.p.Length))).Where(x => (x.end - home).magnitude < 300f && FreeOutput(w, x.end))
            .OrderBy(x => (x.end - home).sqrMagnitude).ToList();
        if (ends.Count == 0) ctx.Log("skip: no tube with a free end nearby");
        else
        {
            var (id, path, end) = ends[0];
            BeltLooks.Frame(path, path.Length, out _, out var fw, out var rt, out var up);
            Teleport(Ground(end - fw * 5f + rt * 3f) + up * 0.3f); yield return 1.5f;
            yield return BuildTool.Choose(kind);
            yield return BuildTool.StartBelt(end);
            Check(BuildTool.Builder.conveyorPlan.startBelt == id, $"starts on tube {id}'s end | {BuildTool.Plan(end)}");
            var aim = Ground(end + fw * 7f + rt * 1.5f);
            BuildTool.AimAt(aim); yield return 0.4f;
            var off = Vector3.ProjectOnPlane(BuildTool.Builder.conveyorPlan.end - aim, up).magnitude;
            Check(off < 0.6f, $"then its end follows the aim ({off:F2} m off)");
        }

        // 3. Sunk junction mouths: starting at a free output, ending at a free input.
        var sunk = Junctions.Types.Where(t => t.PortSink > 0f).SelectMany(t => Junctions.OfType(t).Select(s => (t, s))).ToList();
        if (sunk.Count == 0) ctx.Log("skip: no sunk-port junctions (Better Belts' tube splitter / merger) built");
        (JunctionType T, int S, Vector3 Port, Vector3 Mouth, Quaternion Rot)? Find(bool output)
        {
            foreach (var (t, s) in sunk.OrderBy(x => Junctions.Pose(x.s, out var p, out _) ? (p - home).sqrMagnitude : float.MaxValue))
            {
                Junctions.Pose(s, out var pos, out var rot);
                foreach (var local in output ? Junctions.OutputsOf(t.BaseKind) : Junctions.InputsOf(t.BaseKind))
                {
                    var port = pos + rot * local;
                    if (output ? FreeOutput(w, port) : FreeInput(w, port)) return (t, s, port, port - rot * Vector3.up * t.PortSink, rot);
                }
            }
            return null;
        }
        if (sunk.Count > 0)
        {
            if (Find(true) is { } o)
            {
                var up = o.Rot * Vector3.up; var f = o.Rot * Vector3.forward; var r = o.Rot * Vector3.right;
                Teleport(Ground(o.Port + f * 6f + r * 2f) + up * 0.3f); yield return 1.5f;
                yield return BuildTool.Choose(kind);
                yield return BuildTool.StartBelt(o.Mouth);
                Check(BuildTool.Builder.conveyorPlan.startStructure == o.S, $"aiming at {o.T.Name} {o.S}'s output mouth starts there | {BuildTool.Plan(o.Port)}");
                var aim = Ground(o.Port + f * 5f + r);
                BuildTool.AimAt(aim); yield return 0.4f;
                var off = Vector3.ProjectOnPlane(BuildTool.Builder.conveyorPlan.end - aim, up).magnitude;
                Check(off < 0.6f, $"then its end follows the aim ({off:F2} m off)");
            }
            else ctx.Log("skip: no free output on a sunk junction");

            if (Find(false) is { } i)
            {
                var up = i.Rot * Vector3.up; var f = i.Rot * Vector3.forward; var r = i.Rot * Vector3.right;
                Teleport(Ground(i.Port - f * 7f + r * 3f) + up * 0.3f); yield return 1.5f;
                yield return BuildTool.Choose(kind);
                yield return BuildTool.StartBelt(Ground(i.Port - f * 6f + r * 1.5f));
                BuildTool.AimAt(i.Mouth); yield return 0.5f;
                Check(BuildTool.Builder.conveyorPlan.endStructure == i.S, $"aiming at {i.T.Name} {i.S}'s input mouth ends there | {BuildTool.Plan(i.Port)}");
                var away = Ground(i.Port - f * 3f + r * 3f);
                BuildTool.AimAt(away); yield return 0.4f;
                Check(BuildTool.Builder.conveyorPlan.endStructure == 0, $"aiming away lets go of it | {BuildTool.Plan(i.Port)}");
            }
            else ctx.Log("skip: no free input on a sunk junction");
        }

        // 4. A plain machine's free input (the game's own snapping, untouched).
        var ports = new Il2CppSystem.Collections.Generic.List<MoonWorld.ConveyorPort>();
        w.ConveyorPorts(home, 120f, ports);
        var machine = Items<MoonWorld.ConveyorPort>(ports)
            .Where(q => !q.output && q.structure != 0 && Junctions.TypeOf(q.structure) == null && FreeInput(w, q.position))
            .OrderBy(q => (q.position - home).sqrMagnitude).FirstOrDefault();
        if (machine == null) ctx.Log("skip: no free machine input nearby");
        else
        {
            var up = machine.position.normalized;
            var away = Vector3.ProjectOnPlane(home - machine.position, up).normalized;
            Teleport(Ground(machine.position + away * 8f) + up * 0.3f); yield return 1.5f;
            yield return BuildTool.Choose(kind);
            yield return BuildTool.StartBelt(Ground(machine.position + away * 6f));
            BuildTool.AimAt(machine.position); yield return 0.5f;
            Check(BuildTool.Builder.conveyorPlan.endStructure == machine.structure, $"aiming at machine {machine.structure}'s input ends there | {BuildTool.Plan(machine.position)}");
        }

        BuildTool.Stop();
        Teleport(home); yield return 0.5f;
        Check(w.nextConveyorId == builtBefore, $"nothing was built ({w.nextConveyorId - builtBefore} belts)");
        ctx.Log($"{_pass} passed, {_fail} failed");
    }
}
