// Rendering experiments: measures the frame (uncapped) under different settings, a few seconds each, and puts
// everything back. Shows what each candidate performance fix would save before building it.
//   .\tools\cs.ps1 tools\scripts\render-experiments.cs [seconds] [culling] [parts] [save]
//     culling  only the culling phases; parts: each MSML look part hidden in turn
//     save     keep the player's place and view as the viewpoint every run goes back to (else the saved one is used)

using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class RenderExperiments
{
    static ProfilerRecorder Rec(ProfilerCategory c, string name) => ProfilerRecorder.StartNew(c, name, 1, ProfilerRecorderOptions.Default);

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Select(a => float.TryParse(a, out var v) ? v : 0f).FirstOrDefault(v => v > 0f);
        if (seconds <= 0f) seconds = 5f;
        ctx.Timeout = seconds * 10 + 90f;
        var urp = GraphicsSettings.currentRenderPipeline!.TryCast<UniversalRenderPipelineAsset>()!;
        var shadowDistance = urp.shadowDistance; var cascades = urp.shadowCascadeCount;
        var only = ctx.Args.Contains("culling");   // only the culling phases
        var level0 = Math.Clamp(MoonUserSettings.Current.objectDetail, 0, MoonGraphicsQuality.CullRatios.Length - 1);
        var ratio0 = MoonGraphicsQuality.CullRatios[level0]; var near0 = MoonGraphicsQuality.CullNear[level0];
        var off = MoonFramePacing.Off; var vsync = QualitySettings.vSyncCount; var fps = Application.targetFrameRate;
        MoonFramePacing.Off = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;

        var main = Rec(ProfilerCategory.Render, "CPU Main Thread Frame Time");
        var render = Rec(ProfilerCategory.Render, "CPU Render Thread Frame Time");
        var gpu = Rec(ProfilerCategory.Render, "GPU Frame Time");
        var tris = Rec(ProfilerCategory.Render, "Triangles Count");
        var draws = Rec(ProfilerCategory.Render, "SRP Batcher Draw Calls Count");
        var std = Rec(ProfilerCategory.Render, "Standard Draw Calls Count");
        var casters = Rec(ProfilerCategory.Render, "Shadow Casters Count");
        var renderLoop = Rec(ProfilerCategory.Render, "RenderLoop");

        // Every mesh renderer, with where it is and how big (they don't move: worked out once).
        var renderers = FindAll<Renderer>().Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy && r.GetIl2CppType().Name is "MeshRenderer" or "SkinnedMeshRenderer").ToList();
        var info = renderers.Select(r => { var b = r.bounds; return (R: r, Centre: b.center, Size: b.size.magnitude, Belt: r.name.StartsWith("Conveyor belt"), Shadow: r.shadowCastingMode); }).ToList();
        ctx.Log($"{renderers.Count} mesh renderers; shadows {shadowDistance} m, {cascades} cascades");

        // A fixed viewpoint, so runs compare: "view save" keeps the player's place and view, later runs go back to it
        // before each phase.
        var viewFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "msml-perf-view.txt");
        if (ctx.Args.Contains("save"))
        {
            var p = Player.transform.position; var h = Player.heading;
            System.IO.File.WriteAllText(viewFile, FormattableString.Invariant($"{p.x} {p.y} {p.z} {h.x} {h.y} {h.z} {Player.pitch}"));
            ctx.Log($"viewpoint saved: {p}, heading {h}, pitch {Player.pitch}");
        }
        float[]? view = System.IO.File.Exists(viewFile) ? System.IO.File.ReadAllText(viewFile).Split(' ').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
        IEnumerator Pin()
        {
            if (view == null) yield break;
            var at = new Vector3(view[0], view[1], view[2]);
            if ((Player.transform.position - at).magnitude > 1f) { Player.Teleport(at, true); yield return 1.5f; }
            Player.heading = new Vector3(view[3], view[4], view[5]);
            Player.pitch = view[6];
            yield return 0.5f;
        }
        if (view == null) ctx.Log("no saved viewpoint (run with 'save' to keep this one): phases may see different things");

        IEnumerator Measure(string name)
        {
            yield return Pin();
            yield return 1f;
            var m = new List<double>(); var r = new List<double>(); var g = new List<double>(); var t = new List<double>(); var d = new List<double>(); var c = new List<double>(); var rl = new List<double>(); var frames = new List<float>();
            var end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
                frames.Add(Time.unscaledDeltaTime * 1000f);
                m.Add(main.LastValueAsDouble / 1e6); r.Add(render.LastValueAsDouble / 1e6); g.Add(gpu.LastValueAsDouble / 1e6);
                t.Add(tris.LastValue); d.Add(draws.LastValue + std.LastValue); c.Add(casters.LastValue); rl.Add(renderLoop.LastValue / 1e6);
            }
            ctx.Log($"{name,-34} frame {frames.Average(),5:F1} ms ({1000 / frames.Average(),3:F0} fps) | main {m.Average(),5:F1} (render loop {rl.Average(),4:F1}) | render thread {r.Average(),5:F1} | GPU {g.Average(),5:F1} | tris {t.Average() / 1e6,5:F1} M | draws {d.Average(),6:N0} | casters {c.Average(),5:N0}");
        }

        try
        {
            yield return Measure("baseline");

            if (!only)
            {
            foreach (var x in info.Where(x => x.Belt && x.R != null)) x.R.shadowCastingMode = ShadowCastingMode.Off;
            yield return Measure("belts cast no shadows");
            foreach (var x in info.Where(x => x.Belt && x.R != null)) x.R.shadowCastingMode = x.Shadow;

            urp.shadowDistance = 120f;
            yield return Measure("shadow distance 120 m");
            urp.shadowDistance = shadowDistance;

            urp.shadowCascadeCount = 2;
            yield return Measure("2 shadow cascades");
            urp.shadowCascadeCount = cascades;

            foreach (var reach in new[] { 300f, 200f, 120f })
            {
                var cam = Camera.main.transform.position;
                var hidden = 0;
                foreach (var x in info)
                {
                    if (x.R == null) continue;
                    var far = x.Size < 30f && (x.Centre - cam).magnitude > reach;
                    x.R.forceRenderingOff = far;
                    if (far) hidden++;
                }
                yield return Measure($"small objects hidden past {reach} m ({hidden})");
            }
            foreach (var x in info) if (x.R != null) x.R.forceRenderingOff = false;
            }

            // The game's own detail culling (off at the top two object detail settings), at Low's and twice Low's ratio.
            var level = Math.Clamp(MoonUserSettings.Current.objectDetail, 0, MoonGraphicsQuality.CullRatios.Length - 1);
            var ratio = MoonGraphicsQuality.CullRatios[level]; var near = MoonGraphicsQuality.CullNear[level];
            foreach (var (r, n) in new[] { (0.006f, 30f), (0.012f, 30f) })
            {
                MoonGraphicsQuality.CullRatios[level] = r; MoonGraphicsQuality.CullNear[level] = n;
                yield return 1f;
                var hc = FindAll<MoonHorizonCulling>().FirstOrDefault();
                yield return Measure($"game detail culling {r}/{n} m (hides {hc?.SmallHidden})");
            }
            MoonGraphicsQuality.CullRatios[level] = ratio; MoonGraphicsQuality.CullNear[level] = near;

            // MSML's belt and junction looks (not tracked by the game's culling) hidden far away.
            foreach (var reach in new[] { 300f, 150f })
            {
                var cam = Camera.main.transform.position;
                var hidden = 0;
                foreach (var x in info)
                {
                    if (x.R == null) continue;
                    var far = x.R.transform.root.name.StartsWith("MSML") && (x.Centre - cam).magnitude > reach;
                    x.R.forceRenderingOff = far;
                    if (far) hidden++;
                }
                yield return Measure($"MSML looks hidden past {reach} m ({hidden})");
            }
            foreach (var x in info) if (x.R != null) x.R.forceRenderingOff = false;

            // Each kind of MSML look part hidden everywhere: what each costs.
            if (ctx.Args.Contains("parts"))
            {
                var parts = info.Where(x => x.R != null && x.R.transform.root.name.StartsWith("MSML")).GroupBy(x => x.R.name).OrderByDescending(g => g.Count()).Take(10).ToList();
                foreach (var g in parts)
                {
                    foreach (var x in g) if (x.R != null) x.R.forceRenderingOff = true;
                    yield return Measure($"MSML '{g.Key}' hidden ({g.Count()})");
                    foreach (var x in g) if (x.R != null) x.R.forceRenderingOff = false;
                }
            }

            yield return Measure("baseline again");
        }
        finally
        {
            foreach (var x in info) { if (x.R == null) continue; x.R.forceRenderingOff = false; x.R.shadowCastingMode = x.Shadow; }
            urp.shadowDistance = shadowDistance; urp.shadowCascadeCount = cascades;
            MoonGraphicsQuality.CullRatios[level0] = ratio0; MoonGraphicsQuality.CullNear[level0] = near0;
            MoonFramePacing.Off = off; QualitySettings.vSyncCount = vsync; Application.targetFrameRate = fps;
            main.Dispose(); render.Dispose(); gpu.Dispose(); tris.Dispose(); draws.Dispose(); std.Dispose(); casters.Dispose(); renderLoop.Dispose();
        }
    }
}
