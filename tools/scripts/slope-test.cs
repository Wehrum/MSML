// Tubes down steep hills: finds ground steeper than belts may go (45 degrees) near the player, and checks a tube along
// it is allowed (Better Belts: up to 75) while a plain belt still isn't, then builds the tube and removes it.
//   .\tools\cs.ps1 tools\scripts\slope-test.cs
using MSML;
public static class SlopeTest {
  // Nothing but ground within 5 m (belts and structures in the way would refuse it for other reasons).
  public static Dictionary<string, int> Near = new();
  static bool Clear(Vector3 at) { var cols = Physics.OverlapSphere(at, 5f); foreach (var col in cols) if (!col.name.StartsWith("Terrain")) Near[col.name] = Near.TryGetValue(col.name, out var k) ? k + 1 : 1; return cols.All(col => col.name.StartsWith("Terrain")); }
  public static IEnumerator Run(ScriptContext ctx) {
    ctx.Timeout = 120;
    var w = World; var b = Find<MoonDepotBuilder>(); var tube = Belts.TypeById("msml.betterbelts.tube");
    var start = Position; var up0 = Up;
    var flat0 = Vector3.ProjectOnPlane(Vector3.forward, up0).normalized;
    // Empty steep ground: around here, else further out (bases spread over the hills nearby).
    var origins = new List<Vector3> { start };
    foreach (var dist in new[] { 600f, 1000f }) for (var a = 0; a < 8; a++) origins.Add(Ground(start + Quaternion.AngleAxis(a * 45f, up0) * flat0 * dist));
    var found = new List<(Vector3 S, Vector3 E, float Angle)>();
    foreach (var home in origins) {
    if (found.Count > 0) break;
    var hu = home.normalized;
    var flat = Vector3.ProjectOnPlane(Vector3.forward, hu).normalized;
    if (home != start) { Teleport(home + hu * 1f); yield return 1.5f; }
    // The steepest 6 m step around (a ring of points out to 150 m).
    for (var r = 10f; r <= 150f; r += 10f)
      for (var a = 0; a < 36; a++) {
        var dir = Quaternion.AngleAxis(a * 10f, hu) * flat;
        var p = Ground(home + dir * r);
        for (var h = 0; h < 8; h++) {
          var f = Quaternion.AngleAxis(h * 45f, hu) * flat;
          var q = Ground(p + f * 6f);
          var rise = Vector3.Dot(q - p, hu); var run = Vector3.ProjectOnPlane(q - p, hu).magnitude;
          var angle = Mathf.Atan2(Math.Abs(rise), run) * Mathf.Rad2Deg;
          if (angle > 46f && angle < 70f && run > 3f && Clear((p + q) / 2f)) found.Add((p, q, angle));
        }
      }
    }
    var up = Up;
    ctx.Log($"{found.Count} steps steeper than 45 degrees nearby; in the way elsewhere: " + string.Join(", ", Near.OrderByDescending(x => x.Value).Take(8).Select(x => x.Key + " x" + x.Value)));
    if (found.Count == 0) yield break;
    MoonWorld.ConveyorPlan plan = null; string asTube = "", asBelt = ""; float angleUsed = 0; var reasons = new Dictionary<string, int>();
    string Try(int kind) { b.SelectStructure(kind); b.BeginPlacement(); return w.CheckConveyor(Player, plan, out var why, out _, out _) ? "allowed" : "refused: " + why; }
    // The steepest one with nothing else in the way.
    foreach (var (fs, fe, fa) in found.OrderByDescending(x => x.Angle).Take(40)) {
      var s = fs + up * 0.6f; var e = fe + up * 0.6f; var dirPlan = (e - s).normalized;
      plan = new MoonWorld.ConveyorPlan(); plan.start = s; plan.end = e; plan.startDirection = dirPlan; plan.endDirection = dirPlan; plan.startUp = up; plan.endUp = up;
      Teleport(Ground((s + e) / 2f + Vector3.Cross(up, dirPlan) * 3f) + up * 0.3f); yield return 0.3f;
      asTube = Try(tube.Kind);
      reasons[asTube] = reasons.TryGetValue(asTube, out var rn) ? rn + 1 : 1;
      if (asTube == "allowed") { asBelt = Try(MoonDepotBuilder.ConveyorKind); angleUsed = fa; break; }
    }
    ctx.Log($"step: {angleUsed:F0} degrees; tube answers: " + string.Join(" | ", reasons.Select(x => x.Key + " x" + x.Value)));
    ctx.Log($"tube: {asTube}"); ctx.Log($"plain belt: {asBelt}");
    var slopes = string.Join(",", Resources.FindObjectsOfTypeAll<MoonConveyorKit>().Select(k => k.maxSlope.ToString("F0")).Distinct());
    ctx.Log($"conveyor kits' slope limit after the checks: {slopes}");
    b.SelectStructure(tube.Kind); b.BeginPlacement();
    int id = 0; var built = w.CheckConveyor(Player, plan, out var r2, out _, out _) && w.TryBuildConveyor(Player, plan, out r2, out id);
    ctx.Log($"built: {built} {(built ? Belts.TypeOf(id)?.Name : r2)}");
    if (built) { ctx.Log(Shot("slope-tube")); yield return 1f; w.TryRemoveConveyor(Player, id, out _); }
    b.CancelPlacement();
    ctx.Log(asTube == "allowed" && asBelt.StartsWith("refused: Too steep") && built ? "PASS" : "FAIL");
  }
}
