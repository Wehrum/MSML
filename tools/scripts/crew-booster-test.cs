// A crewmate building a booster and tubes, as the host sees it: the belts arrive plain (the host's own build hooks don't
// run for them), and their types arrive as the crewmate's messages (BeltState's set requests), in the order a crewmate
// builds: booster first, then the tubes after it. The host must then work out pressure for those tubes.
//   .\tools\cs.ps1 tools\scripts\crew-booster-test.cs

using System.Reflection;
using MSML;

public static class CrewBoosterTest
{
    public static bool Skip() => false;

    // A set request as BeltState writes it: belt id, its ends, type id.
    static byte[] BuildSet(int id, Vector3 start, Vector3 end, string type)
    {
        using var stream = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(stream);
        w.Write(id); w.Write(start.x); w.Write(start.y); w.Write(start.z); w.Write(end.x); w.Write(end.y); w.Write(end.z); w.Write(type);
        w.Flush();
        return stream.ToArray();
    }

    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 120;
        var w = World; var b = Find<MoonDepotBuilder>();
        var tube = Belts.TypeById("msml.betterbelts.tube"); var booster = Belts.TypeById("msml.betterbelts.booster");
        var state = typeof(Belts).Assembly.GetType("MSML.BeltState");
        var net = typeof(Belts).Assembly.GetType("MSML.BeltNetwork");
        const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var encode = state.GetMethod("EncodeSet", any); var onSet = state.GetMethod("OnSetRequest", any);
        void CrewmateSays(int belt, BeltType type) => onSet.Invoke(null, new object[] { 123UL, (byte[])encode.Invoke(null, new object[] { belt, type }) });

        // Somewhere clear, in reach of a solar panel (the booster needs power).
        var up = Up; MoonWorld.ConveyorPlan P(Vector3 s, Vector3 e, int after) { var d = (e - s).normalized; var p = new MoonWorld.ConveyorPlan(); p.start = s; p.end = e; p.startDirection = d; p.endDirection = d; p.startUp = s.normalized; p.endUp = e.normalized; p.startBelt = after; return p; }
        Vector3 start = default, dir = default; var found = false;
        foreach (var node in Items<MoonWorld.PowerNode>(w.powerNodes).Where(n => n.panel))
        {
            for (var a = 0; a < 12 && !found; a++)
            {
                var d = Quaternion.AngleAxis(a * 30f, node.position.normalized) * Vector3.ProjectOnPlane(Vector3.forward, node.position.normalized).normalized;
                var s = Ground(node.position + d * 8f) + node.position.normalized * 0.6f;
                Teleport(Ground(s + Vector3.Cross(s.normalized, d) * 3f) + s.normalized * 0.3f);
                b.SelectStructure(MoonDepotBuilder.ConveyorKind); b.BeginPlacement();
                if (w.CheckConveyor(Player, P(s, Ground(s + d * 22.4f) + s.normalized * 0.6f, 0), out _, out _, out _)) { start = s; dir = d; found = true; }
            }
            if (found) break;
        }
        if (!found) { ctx.Log("no clear spot near a panel"); yield break; }

        // The host's own build hooks off: belts arrive as from a crewmate.
        foreach (var name in new[] { "Changed", "Invalidate" })
            ctx.Harmony.Patch(net.GetMethod(name, any), prefix: new HarmonyMethod(typeof(CrewBoosterTest).GetMethod(nameof(Skip))));
        var ids = new List<int>();
        int Build(Vector3 s, Vector3 e, int after)
        {
            Teleport(Ground((s + e) / 2f + Vector3.Cross(s.normalized, dir) * 3f) + s.normalized * 0.3f);
            b.SelectStructure(MoonDepotBuilder.ConveyorKind); b.BeginPlacement();
            return w.TryBuildConveyor(Player, P(s, e, after), out var why, out var id) ? id : 0;
        }
        var typeFirst = ctx.Args.Contains("typefirst");   // the crewmate's message before the belt reaches the host
        ctx.Log(typeFirst ? "order: type message, then the belt" : "order: belt, then its type message");
        // The booster, then the tubes after it, one by one.
        var last = 0;
        for (var i = 0; i < 3; i++)
        {
            var s = i == 0 ? start : w.conveyors[last].end;
            var e = i == 0 ? start + dir * 2.4f : Ground(s + dir * 10f) + s.normalized * 0.6f;
            var type = i == 0 ? booster : tube;
            int id;
            if (typeFirst)
            {
                // As the crewmate's message names it: its ends (and the crewmate's own id, which differs).
                onSet.Invoke(null, new object[] { 123UL, BuildSet(9_000_000 + i, s, e, type.Id) });
                yield return 0.7f;
                id = Build(s, e, last);
            }
            else
            {
                id = Build(s, e, last);
                yield return 1f;
                CrewmateSays(id, type);
            }
            ids.Add(id);
            yield return 1.5f;
            last = id;
        }
        b.CancelPlacement();
        yield return 3f;   // pressure is checked at least once a second
        ctx.Log($"pieces: {string.Join(", ", ids.Select(id => $"{id} {Belts.TypeOf(id)?.Name ?? "plain"} x{Belts.SpeedOf(id):F1} down[{string.Join(",", Belts.Downstream(id))}]"))}");
        var ok = ids.Skip(1).All(id => Belts.SpeedOf(id) > 1.01f);
        ctx.Log(ok ? "PASS the tubes after a crewmate's booster are pressurised on the host" : "FAIL the tubes after a crewmate's booster have no pressure on the host");
        ctx.Harmony.UnpatchSelf();
        foreach (var id in ids.AsEnumerable().Reverse()) { Teleport(Ground(w.conveyors[id].start) + up * 1f); w.TryRemoveConveyor(Player, id, out _); }
    }
}
