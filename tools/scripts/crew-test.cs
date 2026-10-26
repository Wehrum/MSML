// Multiplayer support for modded vehicles, as far as one player can test it:
//   1. a crewmate's harvester (MoonRemoteVehicle): model, lamps, silo fill
//   2. a crewmate's vehicle with an unknown index is skipped instead of throwing
//   3. lobby: what a modded host advertises, and the version the game's join check sees for matching and
//      mismatching mod lists
//   .\tools\cs.ps1 tools\scripts\crew-test.cs [lobby]

public static class CrewTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 60;
        var index = VehicleIndex("Hex harvester");

        // 1. A crewmate's harvester, parked in front of us.
        var remote = MoonRemoteVehicle.Spawn(World, index, "crewmate");
        ctx.OnUnload(() => { if (remote != null && remote) Object.Destroy(remote.gameObject); });
        var spot = Ground(Position + Forward * 18f) + Up * 0.1f;
        // Feed it network states like a crewmate driving it with a 60% full hopper and its lights on.
        var rotation = Quaternion.LookRotation(Vector3.Cross(Forward, Up), Up);
        for (var i = 0; i < 90; i++)
        {
            remote.Receive(new MoonRemoteVehicle.State { position = spot, rotation = rotation, fill = 0.6f, driven = true, lights = true });
            remote.ShowAt(0.1f, 0.2f);
            yield return null;
        }
        var lamps = remote.GetComponentsInChildren<Transform>(true).Where(t => t.parent != null && t.parent.name == "Vehicle lights").ToList();
        var dust = remote.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Silo dust");
        var scale = remote.AuthoredView.transform;
        ctx.Log($"remote harvester: view {remote.AuthoredView?.name}, lamps placed {lamps.Count(l => l.name.EndsWith("(placed)"))}/{lamps.Count}, " +
                $"head lamp at {(scale.InverseTransformPoint(lamps.First(l => l.name.StartsWith("Headlight")).position) * 1.45f)}, silo dust {(dust != null && dust.gameObject.activeSelf ? $"{dust.localScale.y:0.00}" : "hidden")} (fill 0.6, remote fill {remote.fill:0.00})");

        // 2. Unknown index.
        var unknown = MoonRemoteVehicle.Spawn(World, 99, "crewmate");
        ctx.Log($"remote vehicle #99: {(unknown == null ? "skipped" : "SPAWNED?!")}");
        Object.Destroy(remote.gameObject);

        if (!ctx.Args.Contains("lobby")) yield break;

        // 3. Lobby: create an invite-only crew, read what's advertised, then leave.
        MoonSteamLobby.Create(MoonSteamLobby.Privacy.InviteOnly, 4);
        yield return WaitFor(() => MoonSteamLobby.InLobby, 20, "the crew to be created");
        var lobby = new Steamworks.CSteamID(MoonSteamLobby.lobby);
        var vehicles = Steamworks.SteamMatchmaking.GetLobbyData(lobby, "msml.vehicles");
        var seenVersion = Steamworks.SteamMatchmaking.GetLobbyData(lobby, "version");
        ctx.Log($"host advertises vehicles: {vehicles}");
        ctx.Log($"join check sees (same mods): '{seenVersion}' (game version '{Application.version}')");

        // Pretend the host has a different vehicle list: the join check must now see the modded version (no match).
        Steamworks.SteamMatchmaking.SetLobbyData(lobby, "msml.vehicles", "[{\"Id\":\"other.mod\",\"Name\":\"Other\",\"Index\":22}]");
        yield return 0.5f;
        ctx.Log($"join check sees (different mods): '{Steamworks.SteamMatchmaking.GetLobbyData(lobby, "version")}'");
        MoonSteamLobby.Leave();
        yield return 1f;
        ctx.Log($"left the crew: in lobby {MoonSteamLobby.InLobby}");
    }
}
