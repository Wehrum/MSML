// Read-only, for a crewmate: which capsules the game draws without them being laid in a tube (not posed), and where
// the game has them (a belt's queue, the host's rider list, neither). Run while watching capsules pop out of tubes.
//   .\tools\cs.ps1 tools\scripts\crew-capsule-probe.cs [seconds]

using System.Reflection;
using MSML;

public static class CrewCapsuleProbe
{
    static System.Collections.IDictionary Riders = null!;
    public static readonly Dictionary<int, int> Unposed = new();     // capsule -> frames shown unposed
    public static readonly Dictionary<int, Vector3> UnposedAt = new();
    public static int Shown, ShownPosed;

    static Vector3 _original;

    // Each capsule's last drawing: frame, where the game wanted it, where it ended up, whether the mod posed it, and
    // the belt and distance the mod had it on.
    static readonly Dictionary<int, (int Frame, Vector3 Original, Vector3 Final, bool Posed, string Rider)> Last = new();
    public static readonly List<string> Jumps = new();
    public static int GameJumps, PoseJumps, Twice;

    static string RiderOf(int id)
    {
        if (!Riders.Contains(id)) return "not mapped";
        var r = Riders[id];
        var t = r.GetType();
        var belt = (int)t.GetField("Item1").GetValue(r);
        var distance = (float)t.GetField("Item2").GetValue(r);
        var length = Belts.PathOf(belt)?.Length ?? -1f;
        return $"{belt} ({Belts.TypeOf(belt)?.Name ?? (belt < 0 ? "route" : "plain")}, {length:0.00} m) at {distance:0.00}";
    }

    // Before the mod's pose (first) and after it (last): moved means posed.
    public static void First(Vector3 point) => _original = point;

    public static void BeforeShow(MoonDroppedCanister c, Vector3 point)
    {
        if (c == null) return;
        Shown++;
        var frame = Time.frameCount;
        var posed = Riders.Contains(c.id) || (point - _original).sqrMagnitude > 1e-6f;
        var rider = RiderOf(c.id);
        if (Last.TryGetValue(c.id, out var last))
        {
            if (last.Frame == frame) Twice++;
            else if (last.Frame >= frame - 3 && (point - last.Final).magnitude > 0.2f)
            {
                var up = point.normalized;
                var game = (_original - last.Original).magnitude > 0.2f;
                if (game) GameJumps++;
                else
                {
                    PoseJumps++;
                    if (Jumps.Count < 40)
                        Jumps.Add($"f{frame} capsule {c.id}: drawn moved {(point - last.Final).magnitude:0.00} m (up {Vector3.Dot(point - last.Final, up):+0.00;-0.00}), " +
                                  $"game's point {(_original - last.Original).magnitude:0.00} m; posed {last.Posed} -> {posed}; mapped {last.Rider} -> {rider}");
                }
            }
        }
        Last[c.id] = (frame, _original, point, posed, rider);
        if (posed) { ShownPosed++; return; }
        Unposed[c.id] = Unposed.GetValueOrDefault(c.id) + 1;
        UnposedAt[c.id] = point;
    }

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Length > 0 ? float.Parse(ctx.Args[0]) : 6f;
        ctx.Timeout = seconds + 30;
        var w = World;
        var state = typeof(Belts).Assembly.GetType("MSML.BeltState");
        const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Static;
        Riders = (System.Collections.IDictionary)state.GetField("Riders", any).GetValue(null);
        var read = state.GetMethod("ReadQueue", any);
        Unposed.Clear(); UnposedAt.Clear(); Shown = ShownPosed = 0;
        Last.Clear(); Jumps.Clear(); GameJumps = PoseJumps = Twice = 0;
        ctx.Log($"in crew {CrewMessages.InCrew}, host {CrewMessages.IsHost}; crew riders {w.crewRiders.Count}, missing {w.crewRiderMissing.Count}, seen {w.crewRidersSeen.Count}");
        var show = AccessTools.Method(typeof(MoonDustCanisterView), nameof(MoonDustCanisterView.Show));
        ctx.Harmony.Patch(show, prefix: new HarmonyMethod(typeof(CrewCapsuleProbe), nameof(First)) { priority = Priority.First + 1 });
        ctx.Harmony.Patch(show, prefix: new HarmonyMethod(typeof(CrewCapsuleProbe), nameof(BeforeShow)) { priority = Priority.Last });

        // Where each unposed capsule was, sampled while it's unposed.
        var where = new Dictionary<int, List<string>>();
        var end = Time.unscaledTime + seconds;
        var frames = 0;
        while (Time.unscaledTime < end)
        {
            yield return null;
            frames++;
            if (frames % 10 != 0 || Unposed.Count == 0) continue;
            var wanted = Unposed.Keys.Where(id => !where.ContainsKey(id) || where[id].Count < 4).ToHashSet();
            if (wanted.Count == 0) continue;
            var inQueue = new Dictionary<int, (int Belt, float Distance)>();
            foreach (var e in w.beltQueues)
            {
                if (e.Value == null) continue;
                foreach (var (d, id) in (List<(float, int)>)read.Invoke(null, new object[] { e.Value }))
                    if (wanted.Contains(id)) inQueue[id] = (e.Key, d);
            }
            var inCrew = new Dictionary<int, MoonWorld.CrewRider>();
            for (var i = 0; i < w.crewRiders.Count; i++) { var r = w.crewRiders[i]; if (wanted.Contains(r.id)) inCrew[r.id] = r; }
            foreach (var id in wanted)
            {
                var s = $"f{frames}: ";
                s += inQueue.TryGetValue(id, out var q) ? $"queue {q.Belt} ({Belts.TypeOf(q.Belt)?.Name ?? "plain"}) at {q.Distance:0.00}" : "no queue";
                s += inCrew.TryGetValue(id, out var r) ? $"; crew rider belt {r.belt} ({Belts.TypeOf(r.belt)?.Name ?? (r.belt < 0 ? "route" : "plain")}) at {r.distance:0.00} place {r.place} moving {r.moving}" : "; not a crew rider";
                s += w.crewRiderMissing.ContainsKey(id) ? "; crew-missing" : "";
                s += w.crewRidersSeen.Contains(id) ? "; seen" : "";
                if (w.canisters.ContainsKey(id)) { var c = w.canisters[id]; s += $"; record {(c.position - UnposedAt[id]).magnitude:0.00} m from drawn, physical {c.physicalPose}"; }
                else s += "; no record";
                if (!where.TryGetValue(id, out var list)) where[id] = list = new List<string>();
                list.Add(s);
            }
        }
        ctx.Log($"{frames} frames: {Shown} capsules drawn, {ShownPosed} posed; {Unposed.Count} different capsules drawn unposed");
        ctx.Log($"jumps between frames (over 0.25 m): {GameJumps} the game moved the capsule, {PoseJumps} only our pose changed; drawn twice in a frame {Twice}");
        foreach (var s in Jumps) ctx.Log("  " + s);
        // The ones the game has on a tube (or the host does) matter: listed first.
        bool Tube(int id) => where.TryGetValue(id, out var l) && l.Any(s => s.Contains("tube") || s.Contains("booster") || s.Contains("route"));
        ctx.Log($"  of those, on a tube by the game's queue or the host's list: {Unposed.Keys.Count(Tube)}");
        foreach (var (id, count) in Unposed.OrderByDescending(p => Tube(p.Key)).ThenByDescending(p => p.Value).Take(25))
        {
            ctx.Log($"  capsule {id}: unposed {count} frames");
            if (where.TryGetValue(id, out var list)) foreach (var s in list) ctx.Log("    " + s);
        }
    }
}
