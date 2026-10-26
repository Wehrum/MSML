// Runs the host's real join routine (MoonCrewSession.SendWorld) on the loaded moon for a fake joiner, inside an
// invite-only crew, capturing what it would send instead of sending it, and counting saves.
//   .\tools\cs.ps1 tools\scripts\join-host-sim.cs [seconds]

public static class JoinHostSim
{
    public const ulong Joiner = 76561190000000001UL;
    public static int Saves, Sends;
    public static long Bytes;
    public static Dictionary<int, int> Kinds = new();

    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Length > 0 ? float.Parse(ctx.Args[0]) : 30f;
        ctx.Timeout = seconds + 60;
        Saves = Sends = 0; Bytes = 0; Kinds.Clear();

        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonWorld), nameof(MoonWorld.SaveProgress)), postfix: new HarmonyMethod(typeof(JoinHostSim), nameof(Saved)));
        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonCrewSession), nameof(MoonCrewSession.SendTo)), prefix: new HarmonyMethod(typeof(JoinHostSim), nameof(Capture)));
        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonSteamLobby), nameof(MoonSteamLobby.IsMember)), prefix: new HarmonyMethod(typeof(JoinHostSim), nameof(Member)));

        if (!MoonSteamLobby.InLobby)
        {
            MoonSteamLobby.Create(MoonSteamLobby.Privacy.InviteOnly, 4);
            yield return WaitFor(() => MoonSteamLobby.InLobby, 20, "the crew to be created");
            yield return 2f;
        }
        var session = Find<MoonCrewSession>();
        ctx.Log($"host {MoonSteamLobby.IsHost}, session {session?.name}, capsules {World.canisters.Count}");
        if (session == null) yield break;

        session.StartCoroutine(session.SendWorld(Joiner));
        for (var t = 5f; t <= seconds; t += 5f)
        {
            yield return 5f;
            var stream = session.streams.ContainsKey(Joiner) ? session.streams[Joiner] : null;
            ctx.Log($"{t:0} s: saves {Saves}, messages {Sends} ({Bytes / 1e6:0.0} MB) kinds [{string.Join(", ", Kinds.Select(k => $"{k.Key}x{k.Value}"))}], stream {(stream == null ? "none" : Describe(stream))}");
        }
        MoonSteamLobby.Leave();
    }

    private static void Saved() => Saves++;

    private static bool Capture(ulong to, Il2CppSystem.IO.MemoryStream message, ref bool __result)
    {
        if (to != Joiner) return true;
        Sends++;
        Bytes += message.Length;
        var kind = message.Length > 0 ? message.GetBuffer()[0] : -1;
        Kinds[kind] = Kinds.TryGetValue(kind, out var n) ? n + 1 : 1;
        __result = true;
        return false;
    }

    private static bool Member(ulong id, ref bool __result)
    {
        if (id != Joiner) return true;
        __result = true;
        return false;
    }
}
