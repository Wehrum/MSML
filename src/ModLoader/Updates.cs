using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// In-game updates (MODS > CHECK FOR UPDATES). MSML's latest release comes from its GitHub release.json; official
/// mods from the repo's mods\index.json, and other mods from the "updates" link in their mod.json (same format).
/// Updating a mod downloads its zip, checks its SHA-256 and replaces its folder (applied at the next start, like
/// enabling a mod). Updating MSML closes the game, runs the normal installer (get.ps1) in update mode, and starts
/// the game again.
/// </summary>
internal static class Updates
{
    private const string Repo = "Wehrum/MSML";
    private const string ReleaseInfoUrl = "https://github.com/" + Repo + "/releases/latest/download/release.json";
    private const string GetScriptUrl = "https://github.com/" + Repo + "/releases/latest/download/get.ps1";
    private const string OfficialIndexUrl = "https://raw.githubusercontent.com/" + Repo + "/main/mods/index.json";
    private const int SteamAppId = 5335510;

    public sealed record ModUpdate(string Key, string Name, string Installed, string Version, string Url, string? Sha256);

    public enum State { Idle, Checking, Done, Failed }

    public static State Status { get; private set; } = State.Idle;
    public static string? Error { get; private set; }
    /// <summary>The newest MSML release, when it's newer than the installed one.</summary>
    public static string? MsmlUpdate { get; private set; }
    /// <summary>Mods with a newer version available, by mod folder.</summary>
    public static Dictionary<string, ModUpdate> ModUpdates { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Mods being downloaded right now.</summary>
    public static HashSet<string> Downloading { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the main thread when a check or a download finishes (to redraw the screen).</summary>
    public static event Action? Changed;

    private static readonly ConcurrentQueue<Action> MainThread = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static Updates()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MSML-update-check");
    }

    /// <summary>Called every frame on the main thread.</summary>
    public static void Poll()
    {
        while (MainThread.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Plugin.Logger.LogError("Update step failed: " + e); }
        }
    }

    // ---- checking -------------------------------------------------------------------------------

    /// <summary>Looks for MSML and mod updates in the background.</summary>
    public static void Check()
    {
        if (Status == State.Checking) return;
        Status = State.Checking;
        Error = null;
        var mods = ModCatalog.Scan();
        var installed = VersionCheck.InstalledVersion;
        Task.Run(async () =>
        {
            string? msml = null;
            var updates = new Dictionary<string, ModUpdate>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var answered = 0;
            try
            {
                var release = JsonDocument.Parse(await Http.GetStringAsync(ReleaseInfoUrl)).RootElement;
                var latest = release.GetProperty("version").GetString();
                if (latest != null && Newer(latest, installed)) msml = latest;
                answered++;
            }
            catch (Exception e) { errors.Add("MSML: " + e.Message); }

            // Every index once: the official one, and the mods' own links.
            var indexes = new List<string> { OfficialIndexUrl };
            indexes.AddRange(mods.Select(m => m.Updates).Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).Distinct());
            foreach (var url in indexes)
            {
                try
                {
                    var index = JsonDocument.Parse(await Http.GetStringAsync(url)).RootElement;
                    answered++;
                    foreach (var entry in index.GetProperty("mods").EnumerateArray())
                    {
                        var id = Text(entry, "id");
                        var version = Text(entry, "version");
                        var link = Text(entry, "url");
                        if (id == null || version == null || link == null) continue;
                        foreach (var mod in mods.Where(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Official list: only for mods that don't name their own.
                            if (url == OfficialIndexUrl && mod.Updates.Length > 0) continue;
                            if (!Newer(version, mod.Version)) continue;
                            if (updates.TryGetValue(mod.Key, out var known) && !Newer(version, known.Version)) continue;
                            updates[mod.Key] = new ModUpdate(mod.Key, Text(entry, "name") ?? mod.Name, mod.Version, version, link, Text(entry, "sha256"));
                        }
                    }
                }
                catch (Exception e) { errors.Add($"{url}: {e.Message}"); }
            }

            MainThread.Enqueue(() =>
            {
                MsmlUpdate = msml;
                ModUpdates = updates;
                Status = answered == 0 ? State.Failed : State.Done;
                Error = errors.Count > 0 ? string.Join("\n", errors) : null;
                foreach (var e in errors) Plugin.Logger.LogWarning("Update check: " + e);
                Plugin.Logger.LogInfo($"Update check: MSML {(msml ?? "up to date")}, {updates.Count} mod update(s).");
                Changed?.Invoke();
            });
        });
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>True if <paramref name="candidate"/> is a newer version than <paramref name="current"/>.</summary>
    public static bool Newer(string candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;   // dev builds and unknown versions aren't offered updates
        if (TryParse(candidate, out var a) && TryParse(current, out var b)) return a > b;
        return false;
    }

    private static bool TryParse(string s, out Version v)
    {
        v = new Version(0, 0);
        var core = s.Trim().TrimStart('v').Split('-', '+', ' ')[0];
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out v!);
    }

    // ---- updating mods --------------------------------------------------------------------------

    /// <summary>Downloads and installs one mod update; <paramref name="done"/> gets an error, or null.</summary>
    public static void UpdateMod(string key, bool keepEnabled, Action<string?> done)
    {
        if (!ModUpdates.TryGetValue(key, out var update) || !Downloading.Add(key)) return;
        Changed?.Invoke();
        Task.Run(async () =>
        {
            string? error = null;
            var file = Path.Combine(Path.GetTempPath(), $"MSML-{Guid.NewGuid():N}.zip");
            try
            {
                var bytes = await Http.GetByteArrayAsync(update.Url);
                if (update.Sha256 != null)
                {
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("the download is corrupt (checksum mismatch); try again");
                }
                await File.WriteAllBytesAsync(file, bytes);
            }
            catch (Exception e) { error = e.Message; }

            MainThread.Enqueue(() =>
            {
                try
                {
                    if (error == null)
                    {
                        ModActions.Import(file, enable: keepEnabled, folder: key);
                        ModUpdates.Remove(key);
                        Plugin.Logger.LogInfo($"Updated {update.Name} {update.Installed} -> {update.Version}.");
                    }
                }
                catch (Exception e) { error = e.Message; }
                finally
                {
                    try { File.Delete(file); } catch { }
                    Downloading.Remove(key);
                }
                if (error != null) Plugin.Logger.LogWarning($"Updating {update.Name} failed: {error}");
                done(error);
                Changed?.Invoke();
            });
        });
    }

    // ---- updating MSML --------------------------------------------------------------------------

    /// <summary>
    /// Closes the game, updates MSML with its installer in a PowerShell window (Windows may ask for administrator
    /// rights), and starts the game again.
    /// </summary>
    public static void UpdateMsml()
    {
        var target = MsmlUpdate ?? "";
        var pid = Environment.ProcessId;
        var gameDir = Paths.GameRootPath.Replace("'", "''");
        var stamp = Path.Combine(Paths.BepInExRootPath, "MoonSweepMods.install.json").Replace("'", "''");
        var script = $@"
$Host.UI.RawUI.WindowTitle = 'Updating MSML'
Write-Host 'Waiting for Moon Sweepers to close...'
Wait-Process -Id {pid} -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$ok = $false
try {{
    & ([scriptblock]::Create((Invoke-RestMethod '{GetScriptUrl}'))) -Action Update -GameDir '{gameDir}'
    # The installer may finish in a separate administrator window: wait for the new version to be installed.
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {{
        try {{ if ((Get-Content '{stamp}' -Raw | ConvertFrom-Json).packVersion -eq '{target}') {{ $ok = $true; break }} }} catch {{ }}
        Start-Sleep -Seconds 2
    }}
}} catch {{ Write-Host $_.Exception.Message -ForegroundColor Red }}
if ($ok) {{
    Write-Host 'MSML is up to date. Starting Moon Sweepers...' -ForegroundColor Green
    Start-Process 'steam://rungameid/{SteamAppId}'
    Start-Sleep -Seconds 3
}} else {{
    Write-Host ''
    Write-Host 'MSML could not be updated automatically. You can update it with:' -ForegroundColor Yellow
    Write-Host '  {VersionCheck.InstallCommand}'
    Read-Host 'Press Enter to start Moon Sweepers'
    Start-Process 'steam://rungameid/{SteamAppId}'
}}
";
        var path = Path.Combine(Path.GetTempPath(), "MSML-update.ps1");
        File.WriteAllText(path, script);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"") { UseShellExecute = true });
        Plugin.Logger.LogInfo($"Updating MSML to {target}: closing the game.");
        Application.Quit();
    }
}
