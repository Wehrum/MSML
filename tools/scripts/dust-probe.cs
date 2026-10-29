// Dust and multiplayer probe, read-only (patches nothing, so it's safe in a live crew session): every frame it samples
// the game's own cleaning, dust and crew-sync counters and Unity's frame counters, then reports averages, the worst
// frames and what was going on in them. Run it while clearing dust (yourself or crewmates).
//   .\tools\cs.ps1 tools\scripts\dust-probe.cs [seconds] [caps=2,4,6]
//     caps  measure once per cap on MSML's raised cleaning budgets (CleaningBudget; patches only MSML's own code)

using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

public static class DustProbe
{
    sealed class Sample
    {
        public float Frame, Main, Render, Gpu, Scripts, Late, Finish, Gc, Cleaning, Maintenance, EdgeMs, Press, Loop, TargetWait, PresentWait, LastPresent, RenderWait;
        public int EdgeVerts, Strokes, Held, PagesQueued, PagesTaken, PagesSkipped, TruthWaiting, Joined, Capsules, PendingStrokes, PendingBanks, PendingCollisions;
        public long Upload;
    }

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Select(a => float.TryParse(a, out var v) ? v : 0f).FirstOrDefault(v => v > 0f);
        if (seconds <= 0f) seconds = 15f;
        // caps=2,4,6: measure once per cap on MSML's raised cleaning budgets (patches only MSML's own CleaningBudget).
        var caps = ctx.Args.FirstOrDefault(a => a.StartsWith("caps="))?.Substring(5).Split(',').Select(c => float.Parse(c, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        // try=Name=Value,Name=Value: measure as it is, then with those MoonWorld statics set (put back after).
        var trial = ctx.Args.FirstOrDefault(a => a.StartsWith("try="))?.Substring(4).Split(',').Select(p => p.Split('=')).ToList();
        if (trial != null)
        {
            ctx.Timeout = (seconds + 5f) * 2 + 60f;
            yield return Measure(ctx, seconds, null);
            var saved = new List<(System.Reflection.PropertyInfo, object)>();
            try
            {
                foreach (var kv in trial)
                {
                    var p = typeof(MoonWorld).GetProperty(kv[0], System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
                    saved.Add((p, p.GetValue(null)!));
                    p.SetValue(null, Convert.ChangeType(kv[1], p.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                }
                ctx.Log($"\n==== with {string.Join(", ", trial.Select(kv => kv[0] + "=" + kv[1]))}");
                yield return 3f;
                yield return Measure(ctx, seconds, null);
            }
            finally { foreach (var (p, v) in saved) p.SetValue(null, v); }
            yield break;
        }
        if (caps == null) { yield return Measure(ctx, seconds, null); yield break; }
        ctx.Timeout = (seconds + 5f) * caps.Count + 60f;
        var tick = typeof(MSML.Belts).Assembly.GetType("MSML.CleaningBudget")!.GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        ctx.Harmony.Patch(tick, postfix: new HarmonyLib.HarmonyMethod(typeof(DustProbe).GetMethod(nameof(CapBudget))));
        try
        {
            foreach (var cap in caps)
            {
                _cap = cap;
                yield return 3f;
                yield return Measure(ctx, seconds, cap);
            }
        }
        finally { _cap = null; ctx.Harmony.UnpatchSelf(); }
    }

    static float? _cap;

    public static void CapBudget()
    {
        if (_cap is not { } cap || MSML.Moon.World?.settings is not { } s) return;
        if (s.cleaningWorkBudgetMs > cap) s.cleaningWorkBudgetMs = cap;
        if (s.dustVisualBudgetMs > cap) s.dustVisualBudgetMs = cap;
    }

    static IEnumerator Measure(ScriptContext ctx, float seconds, float? cap)
    {
        ctx.Timeout = Math.Max(ctx.Timeout, seconds + 60f);
        var w = World;

        var handles = new Il2CppSystem.Collections.Generic.List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        ProfilerRecorder? Find(string name)
        {
            for (var i = 0; i < handles.Count; i++)
            {
                var d = ProfilerRecorderHandle.GetDescription(handles[i]);
                if (d.Name == name) return ProfilerRecorder.StartNew(d.Category, d.Name, 1, ProfilerRecorderOptions.Default);
            }
            return null;
        }
        var main = Find("CPU Main Thread Frame Time"); var render = Find("CPU Render Thread Frame Time"); var gpu = Find("GPU Frame Time");
        var scripts = Find("Update.ScriptRunBehaviourUpdate"); var late = Find("PreLateUpdate.ScriptRunBehaviourLateUpdate");
        var finish = Find("PostLateUpdate.FinishFrameRendering"); var gc = Find("GC.Collect"); var gcInc = Find("GarbageCollector.CollectIncremental");
        var upload = Find("Vertex Buffer Upload In Frame Bytes");
        var loop = Find("PlayerLoop"); var targetWait = Find("WaitForTargetFPS"); var presentWait = Find("Gfx.WaitForPresentOnGfxThread"); var lastPresent = Find("WaitForLastPresentation"); var renderWait = Find("Gfx.WaitForRenderThread");
        double Ms(ProfilerRecorder? r, bool dbl = false) => r is { } x && x.Valid ? (dbl ? x.LastValueAsDouble : x.LastValue) / 1e6 : 0;

        ctx.Log($"\n==== {(cap is { } c ? $"MSML cleaning budgets capped at {c} ms" : "as it is")}");
        ctx.Log($"crew {MSML.CrewMessages.InCrew}, host {MSML.CrewMessages.IsHost}; budgets: cleaning {w.settings.cleaningWorkBudgetMs} ms, dust visuals {w.settings.dustVisualBudgetMs} ms; " +
                $"crew take budget {MoonWorld.CrewTakeBudgetMs} ms, strokes a frame {MoonWorld.MaxStrokesAFrame}, dust pages waiting max {MoonWorld.MaxDustPagesWaiting}; vsync {QualitySettings.vSyncCount}, pacing '{MoonFramePacing.State}'");
        var samples = new List<Sample>();
        int taken0 = w.CrewDustPagesTaken, skipped0 = w.CrewDustPagesSkipped, joined0 = w.JoinedCrewStrokes;
        var end = Time.unscaledTime + seconds;
        while (Time.unscaledTime < end)
        {
            FrameTimingManager.CaptureFrameTimings();
            yield return null;
            samples.Add(new Sample
            {
                Frame = Time.unscaledDeltaTime * 1000f, Main = (float)Ms(main, true), Render = (float)Ms(render, true), Gpu = (float)Ms(gpu, true),
                Scripts = (float)Ms(scripts), Late = (float)Ms(late), Finish = (float)Ms(finish), Gc = (float)(Ms(gc) + Ms(gcInc)),
                Cleaning = w.LastQueuedCleaningMs, Maintenance = w.LastCleaningMaintenanceMs, EdgeMs = w.LastDustEdgeUploadMs, EdgeVerts = w.LastDustEdgeUploadVertices,
                Press = w.LastPressMs, Strokes = w.strokeCount, Held = w.HeldCrewStrokes, PagesQueued = w.CrewDustPagesQueued,
                PagesTaken = w.CrewDustPagesTaken, PagesSkipped = w.CrewDustPagesSkipped, TruthWaiting = w.dustTruthWaitingCount, Joined = w.JoinedCrewStrokes,
                Loop = (float)Ms(loop), TargetWait = (float)Ms(targetWait), PresentWait = (float)Ms(presentWait), LastPresent = (float)Ms(lastPresent), RenderWait = (float)Ms(renderWait),
                PendingStrokes = w.PendingToolStrokes, PendingBanks = w.PendingDustBanks, PendingCollisions = w.PendingCleaningCollisions,
                Capsules = w.canisters.Count, Upload = upload is { } u && u.Valid ? u.LastValue : 0,
            });
        }
        foreach (var r in new[] { main, render, gpu, scripts, late, finish, gc, gcInc, upload, loop, targetWait, presentWait, lastPresent, renderWait }) r?.Dispose();

        float Avg(Func<Sample, float> f) => samples.Average(f);
        float Pct(Func<Sample, float> f, float q) { var s = samples.Select(f).OrderBy(v => v).ToList(); return s[Math.Min(s.Count - 1, (int)(s.Count * q))]; }
        void Line(string name, Func<Sample, float> f) => ctx.Log($"{name,-34} avg {Avg(f),7:F2}   p95 {Pct(f, 0.95f),7:F2}   max {samples.Max(f),7:F2}");
        var avg = Avg(s => s.Frame);
        ctx.Log($"{samples.Count} frames: avg {avg:F1} ms ({1000 / avg:F0} fps), p95 {Pct(s => s.Frame, 0.95f):F1}, max {samples.Max(s => s.Frame):F1}");
        ctx.Log("---- frame (ms)");
        Line("CPU main thread", s => s.Main); Line("CPU render thread", s => s.Render); Line("GPU", s => s.Gpu);
        Line("scripts Update", s => s.Scripts); Line("scripts LateUpdate", s => s.Late); Line("finish rendering", s => s.Finish); Line("garbage collection", s => s.Gc);
        Line("vertex upload (KB)", s => s.Upload / 1024f);
        ctx.Log("---- the game's own dust work");
        Line("cleaning (queued strokes, ms)", s => s.Cleaning); Line("cleaning maintenance (ms)", s => s.Maintenance);
        Line("dust edge upload (ms)", s => s.EdgeMs); Line("dust edge upload (vertices)", s => s.EdgeVerts); Line("stone press (ms)", s => s.Press);
        Line("PENDING tool strokes", s => s.PendingStrokes); Line("PENDING dust banks (redraw)", s => s.PendingBanks); Line("PENDING cleaning collisions", s => s.PendingCollisions);
        Line("strokes waiting", s => s.Strokes); Line("crew strokes held", s => s.Held);
        Line("crew dust pages queued", s => s.PagesQueued); Line("dust truth waiting", s => s.TruthWaiting);
        ctx.Log($"crew dust pages: taken {w.CrewDustPagesTaken - taken0}, skipped {w.CrewDustPagesSkipped - skipped0}; crew strokes joined {w.JoinedCrewStrokes - joined0}; capsules {samples[^1].Capsules}");
        ctx.Log("---- slowest frames");
        foreach (var s in samples.OrderByDescending(s => s.Frame).Take(10))
            ctx.Log($"  {s.Frame,5:F1} ms: player loop {s.Loop:F1}, waits: frame cap {s.TargetWait:F1}, present {s.PresentWait:F1}/{s.LastPresent:F1}, render thread {s.RenderWait:F1} | main {s.Main:F1} (Update {s.Scripts:F1}, Late {s.Late:F1}, finish {s.Finish:F1}, GC {s.Gc:F1}) render {s.Render:F1} GPU {s.Gpu:F1} | cleaning {s.Cleaning:F1} maint {s.Maintenance:F1} edges {s.EdgeMs:F1} ({s.EdgeVerts} v) | pending strokes {s.PendingStrokes} banks {s.PendingBanks} | strokes {s.Strokes} held {s.Held} pages {s.PagesQueued} truth {s.TruthWaiting}");
    }
}
