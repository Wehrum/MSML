// Stall probe, read-only (patches nothing, changes nothing): finds what the game is doing when it freezes for a
// moment. A watcher thread of its own (it never touches the game) notices when the main thread stops finishing frames
// and measures, from Windows, whether the main thread was busy (computing) or waiting meanwhile and which of the
// game's threads used the CPU. Each frame it also keeps Unity's timers, garbage collections (the game's and .NET's)
// and the dust/crew counters, and it watches for files being written. Then it reports every freeze.
//   .\tools\cs.ps1 tools\scripts\stall-probe.cs [seconds] [threshold=150]

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

public static class StallProbe
{
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    static long ThreadCpu(IntPtr h) => GetThreadTimes(h, out _, out _, out var k, out var u) ? k + u : 0;   // 100 ns units

    // Written by the main thread every frame, read by the watcher.
    static volatile int _frame;
    static long _frameAt;   // Stopwatch ticks of the last frame end

    sealed class Stall
    {
        public double At, Ms, MainCpuMs, ProcessCpuMs;
        public int Frame;
        public List<(int Id, double CpuMs)> Threads = new();
    }

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Select(a => float.TryParse(a, out var v) ? v : 0f).FirstOrDefault(v => v > 0f);
        if (seconds <= 0f) seconds = 30f;
        var threshold = ctx.Args.FirstOrDefault(a => a.StartsWith("threshold=")) is { } t ? float.Parse(t.Substring(10)) : 150f;
        ctx.Timeout = seconds + 60f;
        var w = World;
        var mainId = GetCurrentThreadId();
        var mainHandle = OpenThread(0x0040 /* QUERY_LIMITED_INFORMATION */, false, mainId);
        var process = Process.GetCurrentProcess();
        var start = Stopwatch.GetTimestamp();
        double Now() => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        // Files written meanwhile (saves, caches, logs).
        var files = new System.Collections.Concurrent.ConcurrentQueue<(double At, string What)>();
        var watchers = new List<System.IO.FileSystemWatcher>();
        foreach (var dir in new[] { w.SaveDirectory, System.IO.Path.GetDirectoryName(w.SaveDirectory), BepInEx.Paths.BepInExRootPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low\\DragDropGames\\MoonSweepers" }.Distinct())
        {
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;
            var fw = new System.IO.FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.FileName };
            fw.Changed += (_, e) => { if (!e.FullPath.EndsWith("LogOutput.log") && !e.FullPath.EndsWith("devharness.out")) files.Enqueue((Now(), e.FullPath)); };
            fw.Created += (_, e) => files.Enqueue((Now(), "created " + e.FullPath));
            fw.EnableRaisingEvents = true;
            watchers.Add(fw);
        }

        // The watcher: while the main thread hasn't finished a frame for longer than the threshold, measure it.
        var stalls = new System.Collections.Concurrent.ConcurrentQueue<Stall>();
        var stop = false;
        var watcher = new Thread(() =>
        {
            while (!stop)
            {
                Thread.Sleep(5);
                var since = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _frameAt)) * 1000.0 / Stopwatch.Frequency;
                if (since < threshold * 0.5) continue;
                // Stalled for a while: sample from here to its end.
                var frame = _frame;
                var t0 = Stopwatch.GetTimestamp();
                var main0 = ThreadCpu(mainHandle);
                var proc0 = process.TotalProcessorTime;
                var threads0 = new Dictionary<int, TimeSpan>();
                try { process.Refresh(); foreach (ProcessThread t in process.Threads) { try { threads0[t.Id] = t.TotalProcessorTime; } catch { } } } catch { }
                while (!stop && _frame == frame) Thread.Sleep(2);
                var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency + threshold * 0.5;
                var stall = new Stall { At = Now(), Ms = ms, Frame = frame, MainCpuMs = (ThreadCpu(mainHandle) - main0) / 1e4, ProcessCpuMs = (process.TotalProcessorTime - proc0).TotalMilliseconds };
                try
                {
                    process.Refresh();
                    foreach (ProcessThread t in process.Threads)
                        try { if (threads0.TryGetValue(t.Id, out var before)) stall.Threads.Add((t.Id, (t.TotalProcessorTime - before).TotalMilliseconds)); } catch { }
                }
                catch { }
                stall.Threads = stall.Threads.OrderByDescending(t => t.CpuMs).Take(5).ToList();
                if (ms >= threshold) stalls.Enqueue(stall);
            }
        }) { IsBackground = true, Name = "MSML stall probe" };

        // Every Unity timer, per frame.
        var handles = new Il2CppSystem.Collections.Generic.List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        var recs = new List<(string Name, bool Time, bool Double, ProfilerRecorder R)>();
        for (var i = 0; i < handles.Count; i++)
        {
            var d = ProfilerRecorderHandle.GetDescription(handles[i]);
            if (d.Name.StartsWith("<") || d.Category.Name == "Memory" && d.Name != "GC Used Memory" && d.Name != "Total Used Memory") continue;
            recs.Add((d.Name, d.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds, d.DataType.ToString() == "Double", ProfilerRecorder.StartNew(d.Category, d.Name, 1, ProfilerRecorderOptions.Default)));
        }
        var frames = new List<(double At, float Ms, double[] Values, int NetGc0, int NetGc2, long IlGc, int Pages, int Truth, int Strokes, int Held, int Capsules, float Cleaning, float Maint)>();
        long IlGc() { try { return Il2CppSystem.GC.CollectionCount(0); } catch { return -1; } }

        Interlocked.Exchange(ref _frameAt, Stopwatch.GetTimestamp());
        watcher.Start();
        ctx.Log($"watching for {seconds} s: freezes over {threshold} ms; main thread {mainId}; crew {MSML.CrewMessages.InCrew}, host {MSML.CrewMessages.IsHost}");
        try
        {
            var end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end)
            {
                yield return null;
                Interlocked.Exchange(ref _frameAt, Stopwatch.GetTimestamp());
                _frame++;
                var values = new double[recs.Count];
                for (var i = 0; i < recs.Count; i++)
                {
                    var r = recs[i].R;
                    if (!r.Valid) continue;
                    var v = recs[i].Double ? r.LastValueAsDouble : r.LastValue;
                    values[i] = recs[i].Time ? v / 1e6 : v;
                }
                frames.Add((Now(), Time.unscaledDeltaTime * 1000f, values, GC.CollectionCount(0), GC.CollectionCount(2), IlGc(),
                    w.CrewDustPagesTaken, w.dustTruthWaitingCount, w.strokeCount, w.HeldCrewStrokes, w.canisters.Count, w.LastQueuedCleaningMs, w.LastCleaningMaintenanceMs));
            }
        }
        finally
        {
            stop = true;
            foreach (var fw in watchers) fw.Dispose();
            foreach (var r in recs) r.R.Dispose();
            CloseHandle(mainHandle);
        }

        var sorted = frames.Select(f => f.Ms).OrderBy(v => v).ToList();
        ctx.Log($"{frames.Count} frames: avg {frames.Average(f => f.Ms):F1} ms, p95 {sorted[(int)(sorted.Count * 0.95)]:F1}, max {sorted[^1]:F1}; " +
                $".NET GCs {frames[^1].NetGc0 - frames[0].NetGc0} (gen2 {frames[^1].NetGc2 - frames[0].NetGc2}), game GCs {frames[^1].IlGc - frames[0].IlGc}; {stalls.Count} freezes over {threshold} ms");
        var fileList = files.ToList();
        foreach (var s in stalls)
        {
            ctx.Log($"\nfreeze {s.Ms:F0} ms at {s.At / 1000:F1} s (frame {s.Frame}): main thread used {s.MainCpuMs:F0} ms of CPU ({(s.MainCpuMs > s.Ms * 0.6 ? "BUSY: computing" : "WAITING: blocked on something")}), whole game {s.ProcessCpuMs:F0} ms");
            ctx.Log("  busiest threads meanwhile: " + string.Join(", ", s.Threads.Select(t => $"{(t.Id == mainId ? "MAIN" : t.Id.ToString())} {t.CpuMs:F0} ms")));
            // The frame that took the time is the one after the last finished frame.
            foreach (var index in new[] { s.Frame, s.Frame + 1 })
            {
                if (index <= 0 || index >= frames.Count) continue;
                var f = frames[index]; var p = frames[index - 1];
                var big = recs.Select((r, i) => (r, v: f.Values[i])).Where(x => x.r.Time && x.v > 2).OrderByDescending(x => x.v).Take(8).Select(x => $"{x.r.Name} {x.v:F1}");
                ctx.Log($"  frame {index} ({f.Ms:F0} ms): {string.Join(", ", big)} | GCs .NET {f.NetGc0 - p.NetGc0} (gen2 {f.NetGc2 - p.NetGc2}) game {f.IlGc - p.IlGc} | cleaning {f.Cleaning:F1} maint {f.Maint:F1} | pages +{f.Pages - p.Pages}, truth {f.Truth}, strokes {f.Strokes}, held {f.Held}, capsules {f.Capsules - p.Capsules:+0;-0;0}");
            }
            foreach (var (at, what) in fileList.Where(x => Math.Abs(x.At - s.At) < s.Ms + 1500).Take(6)) ctx.Log($"  file {at / 1000:F1} s: {what}");
        }
        ctx.Log($"\nfiles written while watching: {fileList.Count}" + (fileList.Count > 0 ? ": " + string.Join(", ", fileList.Select(f => System.IO.Path.GetFileName(f.What)).GroupBy(n => n).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} x{g.Count()}")) : ""));
    }
}
