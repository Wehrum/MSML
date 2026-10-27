// Capsules on tubes that wouldn't be drawn in the tube (missing from BeltState's rider map), and belts whose cached
// capsule queue is no longer the game's, counted every frame. With "crew", the host's own belt update is also run back
// through ApplyCrewBelts every 0.1 s, as a crewmate gets it. Run on a throwaway save.
//   .\tools\cs.ps1 tools\scripts\crew-queues-test.cs [crew] [seconds]

using System.Reflection;
using MSML;

public static class CrewQueuesTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var w = World;
        var crew = ctx.Args.Contains("crew") || ctx.Args.Any(a => a.StartsWith("crewmate"));
        // "crewmate": the game is told it isn't the host while it takes the update; "crewmate-all": the whole time.
        var asCrewmate = ctx.Args.Any(a => a.StartsWith("crewmate"));
        var wholeTime = ctx.Args.Contains("crewmate-all");
        var seconds = ctx.Args.Select(a => float.TryParse(a, out var s) ? s : 0f).FirstOrDefault(s => s > 0f);
        if (seconds <= 0f) seconds = 8f;
        ctx.Timeout = seconds + 30;
        var state = typeof(Belts).Assembly.GetType("MSML.BeltState");
        const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Static;
        var cache = (System.Collections.IDictionary)state.GetField("Queues", any).GetValue(null);
        var riders = (System.Collections.IDictionary)state.GetField("Riders", any).GetValue(null);
        var read = state.GetMethod("ReadQueue", any);
        var tubes = Belts.Types.Where(t => t.RiderPose != null).SelectMany(t => Belts.OfType(t)).ToHashSet();
        ctx.Log($"{tubes.Count} belts whose type draws capsules in them; crew updates: {crew}");

        int frames = 0, staleFrames = 0, stale = 0, unposedFrames = 0, unposed = 0, replaced = 0, applies = 0, unposedApplies = 0, unposedAfterApply = 0;
        var afterApply = false;
        var pointers = new Dictionary<int, IntPtr>();
        var next = 0f;
        // The host's update arrives late (as over the network): capsules that moved on to the next belt since are put back.
        Il2CppSystem.IO.MemoryStream? late = null;
        var lateAt = 0f;
        var end = Time.unscaledTime + seconds;
        while (Time.unscaledTime < end)
        {
            yield return null;
            frames++;
            if (crew && Time.unscaledTime >= next)
            {
                next = Time.unscaledTime + 0.1f;
                if (late != null)
                {
                    late.Position = 0;
                    if (asCrewmate) MoonSteamLobby.IsHost = false;
                    w.ApplyCrewBelts(new Il2CppSystem.IO.BinaryReader(late.Cast<Il2CppSystem.IO.Stream>()), Time.unscaledTime - lateAt);
                    if (asCrewmate && !wholeTime) MoonSteamLobby.IsHost = true;
                    afterApply = true;
                }
                if (wholeTime) MoonSteamLobby.IsHost = true;
                late = new Il2CppSystem.IO.MemoryStream();
                var writer = new Il2CppSystem.IO.BinaryWriter(late.Cast<Il2CppSystem.IO.Stream>());
                w.WriteCrewBelts(writer, Camera.main.transform.position, 150f);
                writer.Flush();
                if (wholeTime) MoonSteamLobby.IsHost = false;
                lateAt = Time.unscaledTime;
            }
            int s = 0, u = 0;
            foreach (var e in w.beltQueues)
            {
                if (e.Value == null) continue;
                var ptr = e.Value.Pointer;
                if (pointers.TryGetValue(e.Key, out var was) && was != ptr) replaced++;
                pointers[e.Key] = ptr;
                if (cache.Contains(e.Key) && ((Il2CppSystem.Object)cache[e.Key]).Pointer != ptr) s++;
                if (!tubes.Contains(e.Key)) continue;
                foreach (var (_, id) in (List<(float, int)>)read.Invoke(null, new object[] { e.Value }))
                    if (!riders.Contains(id)) u++;
            }
            if (s > 0) { staleFrames++; stale += s; }
            if (u > 0) { unposedFrames++; unposed += u; }
            if (afterApply) { applies++; if (u > 0) { unposedApplies++; unposedAfterApply += u; } afterApply = false; }
        }
        if (asCrewmate) MoonSteamLobby.IsHost = true;
        ctx.Log($"{frames} frames: queues replaced by the game {replaced}; frames with stale cached queues {staleFrames} ({stale} belt-frames); " +
            $"frames with capsules on tubes not posed {unposedFrames} ({unposed} capsule-frames)");
        if (crew) ctx.Log($"straight after a late crew update: {unposedApplies} of {applies} left capsules on tubes not posed ({unposedAfterApply} capsules)");
    }
}
