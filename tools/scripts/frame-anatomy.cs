// Frame anatomy: where each frame goes, from Unity's own counters (they work in the shipped game): CPU main thread,
// render thread and GPU time, each player-loop phase (scripts, physics, rendering, particles, GC...), the render
// counts (draw calls, SetPass calls, triangles, shadow casters) and memory. The game's frame cap (30 fps when its
// window isn't focused, vsync) is lifted while measuring, so the times are the real cost, and put back after.
//   .\tools\cs.ps1 tools\scripts\frame-anatomy.cs [seconds] [capped]
//     capped   keep the game's frame pacing (measure what the player gets)

using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

public static class FrameAnatomy
{
    sealed class Rec
    {
        public string Category = "", Name = "";
        public bool Time, Double;
        public ProfilerRecorder Recorder;
        public readonly List<double> Values = new();
    }

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Select(a => float.TryParse(a, out var v) ? v : 0f).FirstOrDefault(v => v > 0f);
        if (seconds <= 0f) seconds = 10f;
        var capped = ctx.Args.Contains("capped");
        ctx.Timeout = seconds + 60f;

        var handles = new Il2CppSystem.Collections.Generic.List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        var recs = new List<Rec>();
        for (var i = 0; i < handles.Count; i++)
        {
            var d = ProfilerRecorderHandle.GetDescription(handles[i]);
            var category = d.Category.Name;
            if (category is not ("PlayerLoop" or "Render" or "GC" or "Particles" or "VSync" or "Internal")) continue;
            if (d.Name.StartsWith("<")) continue;
            var rec = new Rec { Category = category, Name = d.Name, Time = d.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds, Double = d.DataType.ToString() == "Double" };
            rec.Recorder = ProfilerRecorder.StartNew(d.Category, d.Name, 1, ProfilerRecorderOptions.Default);
            recs.Add(rec);
        }
        long Memory(string name)
        {
            var r = ProfilerRecorder.StartNew(ProfilerCategory.Memory, name, 1, ProfilerRecorderOptions.Default);
            var v = r.CurrentValue;
            r.Dispose();
            return v;
        }

        var off = MoonFramePacing.Off; var vsync = QualitySettings.vSyncCount; var fps = Application.targetFrameRate;
        if (!capped) { MoonFramePacing.Off = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
        var frames = new List<float>();
        try
        {
            yield return 1f;
            var gcUsed0 = Memory("GC Used Memory");
            var end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
                frames.Add(Time.unscaledDeltaTime * 1000f);
                foreach (var r in recs)
                {
                    if (!r.Recorder.Valid) continue;
                    var v = r.Double ? r.Recorder.LastValueAsDouble : r.Recorder.LastValue;
                    r.Values.Add(r.Time ? v / 1e6 : v);
                }
            }
            var gcUsed1 = Memory("GC Used Memory");

            var sorted = frames.OrderBy(f => f).ToList();
            float P(float q) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
            ctx.Log($"{(capped ? "capped (as played)" : "uncapped")}: {frames.Count} frames, avg {frames.Average():F1} ms ({1000f / frames.Average():F0} fps), median {P(0.5f):F1}, p95 {P(0.95f):F1}, p99 {P(0.99f):F1}, max {P(1f):F1}");
            ctx.Log($"at {Player.transform.position}; belts {World.conveyors.Count}, riding capsules {World.RidingCanisterCount}, capsules {World.canisters.Count}, structures {World.depots.Count}; " +
                    $"quality {QualitySettings.GetQualityLevel()}, screen {Screen.width}x{Screen.height}; game heap {gcUsed0 / 1048576} -> {gcUsed1 / 1048576} MB; " +
                    $"memory: total {Memory("Total Used Memory") / 1048576} MB, video {Memory("Video Used Memory") / 1048576} MB");

            double Avg(Rec r) => r.Values.Count == 0 ? 0 : r.Values.Average();
            double Pct(Rec r, double q) { if (r.Values.Count == 0) return 0; var s = r.Values.OrderBy(v => v).ToList(); return s[Math.Min(s.Count - 1, (int)(s.Count * q))]; }
            ctx.Log($"\n{"time (ms)",-52} {"avg",7} {"p95",7} {"max",7}");
            foreach (var r in recs.Where(r => r.Time && r.Values.Count > 0 && Avg(r) > 0.005).OrderByDescending(Avg))
                ctx.Log($"{r.Category + " | " + r.Name,-52} {Avg(r),7:F2} {Pct(r, 0.95),7:F2} {r.Values.Max(),7:F2}");
            ctx.Log($"\n{"counts per frame",-52} {"avg",12} {"max",12}");
            foreach (var r in recs.Where(r => !r.Time && r.Values.Count > 0 && r.Values.Max() > 0).OrderBy(r => r.Name))
                ctx.Log($"{r.Name,-52} {Avg(r),12:N0} {r.Values.Max(),12:N0}");

            // The slowest frames: which phases took the time.
            var main = recs.FirstOrDefault(r => r.Name == "PlayerLoop");
            if (main != null && main.Values.Count == frames.Count)
            {
                ctx.Log("\nslowest frames (player loop ms: phases over 1 ms)");
                var phases = recs.Where(r => r.Time && r.Values.Count == frames.Count && r.Name != "PlayerLoop" && r.Name != "Main Thread" && !r.Name.StartsWith("CPU") && r.Name != "FrameTime.GPU" && r.Name != "GPU Frame Time").ToList();
                foreach (var i in Enumerable.Range(0, frames.Count).OrderByDescending(i => frames[i]).Take(6))
                    ctx.Log($"  {frames[i]:F1} ms: " + string.Join(", ", phases.Where(p => p.Values[i] > 1).OrderByDescending(p => p.Values[i]).Select(p => $"{p.Name} {p.Values[i]:F1}")));
            }
        }
        finally
        {
            foreach (var r in recs) r.Recorder.Dispose();
            MoonFramePacing.Off = off; QualitySettings.vSyncCount = vsync; Application.targetFrameRate = fps;
        }
    }
}
