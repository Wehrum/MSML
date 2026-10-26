using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// Local, offline game-version check. The installer writes BepInEx\MoonSweepMods.install.json with the MSML version,
/// the game builds this MSML release was tested on, and a SHA-256 of GameAssembly.dll. At startup we compare the
/// running game against that to warn when Steam updated the game, or when it's newer than MSML was tested with.
/// Nothing here touches the network.
/// </summary>
internal static class VersionCheck
{
    public const string InstallCommand = "irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1 | iex";

    private static string StampFile => Path.Combine(Paths.BepInExRootPath, "MoonSweepMods.install.json");

    public static string? InstalledVersion { get; private set; }
    /// <summary>The running game's version (as shown in the main menu), e.g. "1.0.4".</summary>
    public static string GameVersion { get; private set; } = "";
    /// <summary>Newest game version this MSML release was tested with.</summary>
    public static string? TestedGameVersion { get; private set; }
    /// <summary>GameAssembly.dll changed since MSML was installed (Steam updated the game).</summary>
    public static bool GameChangedSinceInstall { get; private set; }
    /// <summary>The running game is newer than anything this MSML release was tested with.</summary>
    public static bool GameNewerThanTested =>
        TryParse(GameVersion, out var game) && TryParse(TestedGameVersion, out var tested) && game > tested;

    private static string? _installedGameHash;

    public static void Start()
    {
        GameVersion = Application.version;
        try
        {
            if (File.Exists(StampFile))
            {
                var stamp = JsonDocument.Parse(File.ReadAllText(StampFile)).RootElement;
                InstalledVersion = stamp.GetProperty("packVersion").GetString();
                if (stamp.TryGetProperty("gameAssemblySha256", out var h)) _installedGameHash = h.GetString();
                if (stamp.TryGetProperty("testedGames", out var tested) && tested.ValueKind == JsonValueKind.Array)
                    foreach (var t in tested.EnumerateArray())
                        if (t.TryGetProperty("game", out var g) && g.GetString() is { } v &&
                            (!TryParse(TestedGameVersion, out var best) || (TryParse(v, out var cur) && cur > best)))
                            TestedGameVersion = v;
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug("No install stamp: " + e.Message); }

        if (GameNewerThanTested)
            Plugin.Logger.LogWarning($"Moon Sweepers v{GameVersion} is newer than MSML {InstalledVersion} was tested with (v{TestedGameVersion}). " +
                $"If mods misbehave, update MSML: {InstallCommand}");
        if (_installedGameHash != null) Task.Run(CheckGameUnchanged);
    }

    private static void CheckGameUnchanged()
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var file = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            GameChangedSinceInstall = !string.Equals(Convert.ToHexString(sha.ComputeHash(file)), _installedGameHash, StringComparison.OrdinalIgnoreCase);
            if (GameChangedSinceInstall)
                Plugin.Logger.LogWarning($"Moon Sweepers was updated since MSML was installed. Run the MSML install command again: {InstallCommand}");
            else
                Plugin.Logger.LogInfo($"MSML {InstalledVersion ?? "dev"}, game v{GameVersion} (tested with v{TestedGameVersion ?? "?"}); matches the install.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("Couldn't check the game version: " + e.Message); }
    }

    /// <summary>Short note for the MODS main-menu entry, or "". Mismatches are shown as a dialog instead.</summary>
    public static string MenuNote => "";

    /// <summary>The running game doesn't match what this MSML release was tested with.</summary>
    public static bool Mismatch =>
        GameChangedSinceInstall || (TestedGameVersion != null && GameVersion != TestedGameVersion);

    public static string MismatchMessage =>
        $"This version of MSML ({InstalledVersion ?? "dev"}) was tested with Moon Sweepers v{TestedGameVersion ?? "?"}, " +
        $"but you're running v{GameVersion}{(GameChangedSinceInstall ? " (the game was updated after MSML was installed)" : "")}. " +
        "Mods may not work correctly. Proceed at your own risk, or close the game and run the MSML install command again to update.";

    /// <summary>One line for the Mods screen.</summary>
    public static string StatusLine(string orange)
    {
        var line = $"MSML {InstalledVersion ?? "dev"}  ·  game v{GameVersion}";
        return Mismatch ? $"{line}  <color=#{orange}>·  tested with v{TestedGameVersion ?? "?"}</color>" : line;
    }

    private static bool TryParse(string? s, out Version v)
    {
        v = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var core = s.TrimStart('v').Split('-', '+', ' ')[0];
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out v!);
    }
}
