// Builds a Better Belts line next to a solar panel and reports what happens: a booster, then tubes long enough to
// show the pressure fading, fed with loose capsules. Uses the build menu's own path (open the builder, choose the
// card, build), so belt types, prices and lengths are checked like a player would get them.
//   .\tools\cs.ps1 tools\scripts\tubes-test.cs [keep]     keep: leave the line built (default: remove it after)

using MSML;

public static class TubesTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 120;
        var keep = ctx.Args.Contains("keep");
        var w = World;
        var tube = Belts.TypeById("msml.betterbelts.tube");
        var booster = Belts.TypeById("msml.betterbelts.booster");
        if (tube == null || booster == null) { ctx.Log("Better Belts isn't loaded."); yield break; }
        var builder = Find<MoonDepotBuilder>();
        Money = Math.Max(Money, 100000);

        // A clear straight run near a solar panel (shorter runs if a long one doesn't fit; the booster's range is
        // shrunk for the test so the pressure still fades within the run).
        var panels = Items<MoonWorld.PowerNode>(w.powerNodes).Where(n => n.panel).ToList();
        ctx.Log($"{panels.Count} solar panels");
        var up = Player.transform.up;
        // Scan with the tube card chosen, so tubes' own rules (along the ground) apply.
        builder.SetOpen(true);
        builder.SelectStructure(tube.Kind);
        builder.BeginPlacement();
        float[] lengths = null; Vector3 start = default, dir = default; bool found = false;
        foreach (var run in new[] { new[] { 2.4f, 25f, 25f, 25f, 25f, 25f }, new[] { 2.4f, 25f, 25f, 25f, 25f }, new[] { 2.4f, 25f, 25f, 25f }, new[] { 2.4f, 25f, 25f }, new[] { 2.4f, 20f, 20f } })
        {
            lengths = run;
            foreach (var panel in panels)
            {
                for (var off = 4f; off < panel.spec.range - 2f && !found; off += 4f)
                    for (var a = 0; a < 24 && !found; a++)
                    {
                        var d = Quaternion.AngleAxis(a * 15f, up) * Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                        var s = Ground(panel.position + d * off) + up * 0.6f;
                        if (Clear(s, d, lengths.Sum())) { start = s; dir = d; found = true; }
                    }
                if (found) { ctx.Log($"next to panel {panel.record.id} (range {panel.spec.range} m), run {lengths.Sum()} m"); break; }
            }
            if (found) break;
        }
        if (!found) { ctx.Log("No clear run near a panel: " + string.Join(" | ", Reasons.Select(kv => kv.Key + " x" + kv.Value))); yield break; }
        var cfg = ((BepInEx.Unity.IL2CPP.BasePlugin)BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins["msml.betterbelts"].Instance).Config;
        var full = (BepInEx.Configuration.ConfigEntry<float>)cfg[new BepInEx.Configuration.ConfigDefinition("Boosters", "FullRange")];
        var fade = (BepInEx.Configuration.ConfigEntry<float>)cfg[new BepInEx.Configuration.ConfigDefinition("Boosters", "FadeRange")];
        float oldFull = full.Value, oldFade = fade.Value;
        var tubesLength = lengths.Sum() - lengths[0];
        full.Value = Math.Max(20f, tubesLength * 0.45f);
        fade.Value = Math.Max(10f, tubesLength * 0.3f);
        ctx.OnUnload(() => { full.Value = oldFull; fade.Value = oldFade; });
        ctx.Log($"range for the test: full {full.Value} m, fade {fade.Value} m");
        Teleport(start - dir * 4f + up * 1f);
        yield return 1f;

        // Build through the builder: open it, choose the card, build each piece joined to the last.
        builder.SetOpen(true);
        yield return 0.5f;
        var ids = new List<int>();
        var at = start;
        for (var i = 0; i < lengths.Length; i++)
        {
            var plan = Plan(at, dir, lengths[i], ids.Count > 0 ? ids[^1] : 0);
            Near(plan);
            yield return null;
            builder.SelectStructure(i == 0 ? booster.Kind : tube.Kind);
            builder.BeginPlacement();
            string reason; MoonConveyorPath path; Il2CppSystem.Decimal price;
            var ok = w.CheckConveyor(Player, plan, out reason, out path, out price);
            int id = 0;
            var before = Money;
            if (ok) ok = w.TryBuildConveyor(Player, plan, out reason, out id);
            ctx.Log($"{(i == 0 ? "booster" : "tube")} {lengths[i]} m: {(ok ? "built " + id : "failed")} ({reason}), price ${ToDouble(price)}, paid ${before - Money:F2}, type {Belts.TypeOf(id)?.Name ?? "plain"}, conveyor mode {builder.ConveyorMode}");
            if (!ok) break;
            ids.Add(id);
            at = w.conveyors[id].end;
            yield return 0.2f;
        }
        // A booster is always the same length, whatever is aimed at.
        builder.SelectStructure(booster.Kind);
        builder.BeginPlacement();
        {
            string reason; MoonConveyorPath path; Il2CppSystem.Decimal price;
            var plan = Plan(at, dir, 6f, ids.Count > 0 ? ids[^1] : 0);
            Near(plan);
            var ok = w.CheckConveyor(Player, plan, out reason, out path, out price);
            ctx.Log($"booster aimed 6 m out: {ok} ({reason}), length {(path == null ? 0 : path.Length):F2} m");
        }
        builder.SelectStructure(MoonDepotBuilder.ConveyorKind);
        builder.SetOpen(false);
        if (ids.Count < 2) { Cleanup(ctx, ids); yield break; }
        Teleport(start + Vector3.Cross(up, dir) * 4f + dir * 6f + up * 1.5f);
        Player.transform.rotation = Quaternion.LookRotation(dir + Vector3.Cross(dir, up) * 0.6f, up);

        yield return 2.5f;   // let power and pressure settle
        foreach (var id in ids)
        {
            var down = Belts.Downstream(id);
            ctx.Log($"belt {id} {Belts.TypeOf(id)?.Name}: length {Belts.PathOf(id).Length:F1} speed x{Belts.SpeedOf(id):F2} downstream [{string.Join(",", down)}] view {(Belts.ViewOf(id) != null)}");
        }

        // Feed loose capsules onto the booster and time them.
        var loose = Items<int>(w.canisters.Keys).Where(c => !w.IsCanisterRiding(c) && !w.IsCanisterCarried(c) && !w.IsCanisterFlying(c)).Take(6).ToList();
        var entered = 0;
        foreach (var c in loose) { if (w.Enter(ids[0], c)) entered++; yield return 0.8f; }
        ctx.Log($"{entered} capsules entered the booster");
        Shot("tubes-test");
        var t0 = Time.time;
        var seen = new Dictionary<int, (int Belt, float At)>();
        while (Time.time - t0 < 25f)
        {
            foreach (var c in loose)
                for (var i = 0; i < ids.Count; i++)
                    if (w.beltQueues.ContainsKey(ids[i]) && Spike.Queue(w.beltQueues[ids[i]]).Any(r => r.id == c))
                    {
                        if (!seen.TryGetValue(c, out var s) || s.Belt != i) { seen[c] = (i, Time.time - t0); if (c == loose[0]) ctx.Log($"capsule {c} on piece {i} at {Time.time - t0:F1} s"); }
                    }
            yield return 0.25f;
        }
        if (!keep) Cleanup(ctx, ids);
        full.Value = oldFull;
        fade.Value = oldFade;
    }

    static MoonWorld.ConveyorPlan Plan(Vector3 start, Vector3 dir, float length, int after)
    {
        var up = Player.transform.up;
        var plan = new MoonWorld.ConveyorPlan();
        plan.start = start; plan.end = start + dir * length;
        plan.startDirection = dir; plan.endDirection = dir; plan.startUp = up; plan.endUp = up;
        plan.startBelt = after;
        return plan;
    }

    // Belts can only be built within reach (40 m) of the builder: stand beside the piece.
    static void Near(MoonWorld.ConveyorPlan plan)
    {
        var up = Player.transform.up;
        var mid = (plan.start + plan.end) / 2f;
        var side = Vector3.Cross(up, (plan.end - plan.start).normalized);
        Player.Teleport(Ground(mid + side * 3f) + up * 0.2f, true);
    }

    public static Dictionary<string, int> Reasons = new();

    static bool Clear(Vector3 start, Vector3 dir, float length)
    {
        string reason; MoonConveyorPath path; Il2CppSystem.Decimal price;
        // Belts are at most 30 m (a straight 30 m piece measures a bit over): check the run in 25 m pieces.
        for (var at = 0f; at < length; at += 25f)
        {
            var piece = Plan(start + dir * at, dir, Math.Min(25f, length - at), 0);
            Near(piece);
            if (!World.CheckConveyor(Player, piece, out reason, out path, out price)) { var key = reason.StartsWith("Too long") ? $"{reason} ({path?.Length:F0} m for {Vector3.Distance(piece.start, piece.end):F0} m)" : reason; Reasons[key] = Reasons.TryGetValue(key, out var n) ? n + 1 : 1; return false; }
        }
        return true;
    }

    static void Cleanup(ScriptContext ctx, List<int> ids)
    {
        var removed = 0;
        foreach (var id in ids.AsEnumerable().Reverse())
        {
            if (!World.conveyors.ContainsKey(id)) continue;
            var c = World.conveyors[id];
            var plan = new MoonWorld.ConveyorPlan(); plan.start = c.start; plan.end = c.end;
            Near(plan);
            string reason;
            if (World.TryRemoveConveyor(Player, id, out reason)) removed++;
            else ctx.Log($"couldn't remove {id}: {reason}");
        }
        ctx.Log($"removed {removed} of {ids.Count} pieces");
    }
}

// Reads a belt's riders (distance, capsule id); the interop garbles List<(float, int)>.
public static class Spike
{
    static int _items = -1, _size;
    public static List<(float d, int id)> Queue(Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>> q)
    {
        var lp = q.Pointer;
        if (_items < 0)
        {
            var k = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(lp);
            _items = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(k, "_items"));
            _size = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(k, "_size"));
        }
        var items = System.Runtime.InteropServices.Marshal.ReadIntPtr(lp + _items);
        var n = System.Runtime.InteropServices.Marshal.ReadInt32(lp + _size);
        var r = new List<(float, int)>();
        for (int i = 0; i < n; i++)
        {
            var e = items + 0x20 + i * 8;
            r.Add((BitConverter.Int32BitsToSingle(System.Runtime.InteropServices.Marshal.ReadInt32(e)), System.Runtime.InteropServices.Marshal.ReadInt32(e + 4)));
        }
        return r;
    }
}
