// Checks the message shown when joining a crew whose mods don't match. Makes an invite-only crew, writes a pretend
// host's version and vehicle list into it, then runs the game's version check (GetLobbyData) and failure message
// (SetStatus) the way joining does, and logs what the player would see.
//   .\tools\cs.ps1 tools\scripts\crew-mismatch.cs

public static class CrewMismatch
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 60;
        var created = false;
        if (!MoonSteamLobby.InLobby)
        {
            MoonSteamLobby.Create(MoonSteamLobby.Privacy.InviteOnly, 4);
            yield return WaitFor(() => MoonSteamLobby.InLobby, 20, "the crew to be created");
            yield return 1f;
            created = true;
        }
        var lobby = new Steamworks.CSteamID(MoonSteamLobby.lobby);
        var game = Application.version;
        var ours = Steamworks.SteamMatchmaking.GetLobbyData(lobby, "msml.vehicles");
        ctx.Log($"advertised vehicles: {ours}");

        var mine = string.IsNullOrEmpty(ours) ? "" : ours.Trim('[', ']');
        const string other = "{\"Id\":\"other.bigtruck\",\"Name\":\"Big truck\",\"Mod\":\"Big Truck Mod\",\"Index\":40}";
        const string oldHost = "{\"Id\":\"other.digger\",\"Name\":\"Digger\",\"Index\":41}";
        var cases = new (string Label, string Version, string Vehicles)[]
        {
            ("same mods", $"{game} + mods: Hex Harvester", $"[{mine}]"),
            ("host has an extra mod", $"{game} + mods: Hex Harvester, Big Truck Mod", $"[{mine},{other}]"),
            ("host lacks our mod", $"{game} + mods: Big Truck Mod", $"[{other}]"),
            ("older API host (no mod names)", $"{game} + mods: Digger", $"[{oldHost}]"),
            ("other game version", $"1.0.3 + mods: Hex Harvester", $"[{mine}]"),
            ("unmodded host", game, ""),
        };
        foreach (var c in cases)
        {
            Steamworks.SteamMatchmaking.SetLobbyData(lobby, "version", c.Version);
            Steamworks.SteamMatchmaking.SetLobbyData(lobby, "msml.vehicles", c.Vehicles);
            var seen = Steamworks.SteamMatchmaking.GetLobbyData(lobby, "version");
            if (seen == game) { ctx.Log($"{c.Label}: joins"); continue; }
            MoonSteamLobby.SetStatus($"That crew plays version {seen}. You have {game}. Update the game on Steam to play together.", true);
            ctx.Log($"{c.Label}: turned away -> \"{MoonSteamLobby.Status}\"");
        }
        MoonSteamLobby.SetStatus("", false);
        if (created) MoonSteamLobby.Leave();
    }
}
