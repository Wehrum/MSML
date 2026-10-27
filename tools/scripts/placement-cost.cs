using MSML;
public static class PlaceCost {
  public static IEnumerator Run(ScriptContext ctx) {
    ctx.Timeout = 60;
    var w = World; var b = Find<MoonDepotBuilder>();
    var tube = Belts.TypeById("msml.betterbelts.tube");
    var ids = System.IO.File.ReadAllText(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "devshots", "tube-scale-ids.txt")).Split(',').Where(x => x.Length > 0).Select(int.Parse).Where(i => w.conveyors.ContainsKey(i)).ToList();
    ctx.Log($"field pieces present: {ids.Count}; typed pipes in world: {Belts.OfType(tube).Count}");
    // A spot 6 m beside the first field line.
    var c0 = w.conveyors[ids[0]]; var up = c0.start.normalized; var f = Vector3.ProjectOnPlane(c0.end - c0.start, up).normalized; var r = Vector3.Cross(up, f);
    for (int k = 0; k < 4; k++) {
      var s = Ground(c0.start - r * (8f + k * 3f) - f * 4f) + up * 0.6f; var e = Ground(s + f * 8f) + up * 0.6f;
      Player.Teleport(Ground(s + r * 2f) + up * 0.2f, true); yield return 0.3f;
      b.SelectStructure(tube.Kind); b.BeginPlacement();
      var plan = new MoonWorld.ConveyorPlan(); plan.start = s; plan.end = e; plan.startDirection = f; plan.endDirection = f; plan.startUp = up; plan.endUp = up;
      var sw = System.Diagnostics.Stopwatch.StartNew();
      int id = 0; var ok = w.CheckConveyor(Player, plan, out var why, out _, out _) && w.TryBuildConveyor(Player, plan, out why, out id);
      var buildMs = sw.Elapsed.TotalMilliseconds;
      float worst = 0; for (int i = 0; i < 15; i++) { yield return null; worst = Math.Max(worst, Time.unscaledDeltaTime * 1000f); }
      var sw2 = System.Diagnostics.Stopwatch.StartNew(); var removed = ok && w.TryRemoveConveyor(Player, id, out _); var removeMs = sw2.Elapsed.TotalMilliseconds;
      float worst2 = 0; for (int i = 0; i < 15; i++) { yield return null; worst2 = Math.Max(worst2, Time.unscaledDeltaTime * 1000f); }
      ctx.Log($"build {(ok ? "ok" : why)}: call {buildMs:F1} ms, worst frame after {worst:F1} ms | remove: call {removeMs:F1} ms, worst frame after {worst2:F1} ms");
    }
    b.CancelPlacement();
  }
}

