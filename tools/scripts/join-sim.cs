// Runs the host's side of a crew join on the loaded moon, then the joiner's unpack, without a second player:
//   save (MoonWorld.SaveProgress) -> read the save back (ReadWorld) -> pack (PackWorld) -> unpack (UnpackWorld)
// and compares what arrives with the save on disk.
//   .\tools\cs.ps1 tools\scripts\join-sim.cs

public static class JoinSim
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 300;
        var w = World;
        var dir = w.SaveDirectory;
        ctx.Log($"save directory: {dir}");
        ctx.Log($"on disk: {System.IO.Directory.GetFiles(dir).Length} files, {System.IO.Directory.GetFiles(dir).Sum(f => new System.IO.FileInfo(f).Length) / 1e6:0.0} MB");

        // 1. The host saves first (twice, to see whether a save leaves work for the next one).
        for (var i = 1; i <= 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var save = w.SaveProgress(false);
            yield return WaitFor(() => save.IsCompleted, 120, "the save");
            ctx.Log($"save {i}: {sw.ElapsedMilliseconds} ms, faulted {save.IsFaulted}{(save.IsFaulted ? ": " + save.Exception?.GetBaseException().Message : "")}");
        }

        // 2. Read the save back the way SendWorld does, then pack it.
        var first = new Il2CppSystem.Collections.Generic.List<string>();
        var rest = new Il2CppSystem.Collections.Generic.List<string>();
        Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>> files = null;
        var swRead = System.Diagnostics.Stopwatch.StartNew();
        try { files = MoonCrewSession.ReadWorld(w, "steam-0", first, rest); }
        catch (Exception e) { ctx.Log("ReadWorld threw: " + e); yield break; }
        long total = 0;
        foreach (var kv in files) total += kv.Value?.Length ?? 0;
        ctx.Log($"ReadWorld: {files.Count} files, {total / 1e6:0.0} MB in {swRead.ElapsedMilliseconds} ms; first {first.Count} ({string.Join(", ", Items<string>(first).Take(6))}...), rest {rest.Count}");
        var onDisk = System.IO.Directory.GetFiles(dir).Select(System.IO.Path.GetFileName).ToHashSet();
        var sent = Items<string>(files.Keys).Select(System.IO.Path.GetFileName).ToHashSet();
        ctx.Log($"on disk but not sent: {string.Join(", ", onDisk.Except(sent).Take(10))}");
        ctx.Log($"sent but not on disk: {string.Join(", ", sent.Except(onDisk).Take(10))}");

        var blob = MoonCrewSession.PackWorld(files, first, rest);
        ctx.Log($"PackWorld: {blob.Length / 1e6:0.0} MB");

        // 3. The joiner's side: unpack into the temp cache.
        string unpacked = null;
        try { unpacked = MoonCrewSession.UnpackWorld(blob, 123456789UL); }
        catch (Exception e) { ctx.Log("UnpackWorld threw: " + e); }
        ctx.Log($"UnpackWorld returned: '{unpacked}'");
        var cache = System.IO.Path.Combine(Application.temporaryCachePath, "MoonCrew");
        if (System.IO.Directory.Exists(cache))
            foreach (var d in System.IO.Directory.GetDirectories(cache))
                ctx.Log($"unpacked into {d}: {System.IO.Directory.GetFiles(d).Length} files, {System.IO.Directory.GetFiles(d).Sum(f => new System.IO.FileInfo(f).Length) / 1e6:0.0} MB");
    }
}
