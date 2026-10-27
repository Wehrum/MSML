// Teleports away and back to where the player stands and reports the worst frames while belts stream back in.
//   .\tools\cs.ps1 tools\scripts\stream-in.cs
public static class StreamIn {
  public static IEnumerator Run(ScriptContext ctx) {
    ctx.Timeout = 60;
    var vs = QualitySettings.vSyncCount; QualitySettings.vSyncCount = 0; ctx.OnUnload(() => QualitySettings.vSyncCount = vs);
    var home = Position; var up = Up;
    for (int k = 0; k < 2; k++) {
      Teleport(Ground(home + Vector3.Cross(up, Forward) * 700f) + up * 1f); yield return 4f;
      Teleport(home); var fr = new List<float>(); var end = Time.unscaledTime + 4f;
      while (Time.unscaledTime < end) { yield return null; fr.Add(Time.unscaledDeltaTime * 1000f); }
      var s = fr.OrderByDescending(x => x).ToList();
      ctx.Log($"back at the field: {fr.Count} frames, worst {s[0]:F0} {s[1]:F0} {s[2]:F0} {s[3]:F0} ms, over 33 ms: {fr.Count(x => x > 33f)}, avg {fr.Average():F1}");
    }
  }
}
