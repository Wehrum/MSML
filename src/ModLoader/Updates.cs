using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
/// enabling a mod). Updating MSML downloads the release into the game folder, closes the game, runs the installer
/// inside it, and starts the game again. Downloads go into the game folder, so one antivirus exclusion covers them.
/// </summary>
internal static class Updates
{
    private const string Repo = "Wehrum/MSML";
    private const string ReleaseInfoUrl = "https://github.com/" + Repo + "/releases/latest/download/release.json";
    private const string OfficialIndexUrl = "https://raw.githubusercontent.com/" + Repo + "/main/mods/index.json";
    private const int SteamAppId = 5335510;

    public sealed record ModUpdate(string Key, string Name, string Installed, string Version, string Url, string? Sha256);

    /// <summary>An official mod that isn't installed (from the repo's mods\index.json).</summary>
    public sealed record AvailableMod(string Id, string Name, string Version, string Folder, string Url, string? Sha256, string Author, string Description, long Bytes);

    /// <summary>Official mods that aren't installed, from the last check.</summary>
    public static List<AvailableMod> AvailableMods { get; private set; } = new();

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
            var available = new List<AvailableMod>();
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
                    var index = JsonDocument.Parse(url == OfficialIndexUrl ? await OfficialIndex() : await Http.GetStringAsync(url)).RootElement;
                    answered++;
                    foreach (var entry in index.GetProperty("mods").EnumerateArray())
                    {
                        var id = Text(entry, "id");
                        var version = Text(entry, "version");
                        var link = Text(entry, "url");
                        if (id == null || version == null || link == null) continue;
                        // Official mods not installed (by id, or by folder for mods without one): offered to install.
                        var folder = Text(entry, "folder");
                        if (url == OfficialIndexUrl && folder != null &&
                            !mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || m.Key.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                            available.Add(new AvailableMod(id, Text(entry, "name") ?? folder, version, folder, link, Text(entry, "sha256"),
                                Text(entry, "author") ?? "", Text(entry, "description") ?? "",
                                entry.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var n) ? n : 0));
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
                AvailableMods = available.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
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

    // The official mods list as it is now. raw.githubusercontent.com keeps a copy of a file for 5 minutes (whatever
    // the request says), so right after mods are updated the list read from main can still be the old one, with links
    // to zips that are gone. Read at main's newest commit instead (a new address for every commit, so never an old
    // copy); the plain address if GitHub can't say which commit that is.
    private static async Task<string> OfficialIndex()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/commits/main");
            request.Headers.Accept.ParseAdd("application/vnd.github.sha");
            using var response = await Http.SendAsync(request);
            var sha = response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync()).Trim() : "";
            if (sha.Length == 40 && sha.All(Uri.IsHexDigit))
                return await Http.GetStringAsync($"https://raw.githubusercontent.com/{Repo}/{sha}/mods/index.json");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("Update check: couldn't get the newest mods list, using the usual one: " + e.Message); }
        return await Http.GetStringAsync(OfficialIndexUrl);
    }

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

    // ---- updating and installing mods -------------------------------------------------------------

    /// <summary>Downloads and installs one mod update; <paramref name="done"/> gets an error, or null.</summary>
    public static void UpdateMod(string key, bool keepEnabled, Action<string?> done)
    {
        if (!ModUpdates.TryGetValue(key, out var update)) return;
        Download(key, update.Url, update.Sha256, keepEnabled, done, () =>
        {
            ModUpdates.Remove(key);
            Plugin.Logger.LogInfo($"Updated {update.Name} {update.Installed} -> {update.Version}.");
        }, $"Updating {update.Name}");
    }

    /// <summary>Downloads and installs (and enables) an official mod that isn't installed; <paramref name="done"/> gets an error, or null.</summary>
    public static void InstallMod(AvailableMod mod, Action<string?> done) =>
        Download(mod.Folder, mod.Url, mod.Sha256, true, done, () =>
        {
            AvailableMods.Remove(mod);
            Plugin.Logger.LogInfo($"Installed {mod.Name} {mod.Version}.");
        }, $"Installing {mod.Name}");

    // Downloads a mod's zip into the Mods folder (checking its SHA-256) and installs it as the mod's folder. Done by the
    // game rather than a browser, the file isn't marked as downloaded from the internet, which Windows Security is
    // much stricter about (and an exclusion for the game folder covers it).
    private static void Download(string key, string url, string? sha256, bool enable, Action<string?> done, Action installed, string what)
    {
        if (!Downloading.Add(key)) return;
        Changed?.Invoke();
        Task.Run(async () =>
        {
            string? error = null;
            var downloads = Path.Combine(ModPaths.ModsDir, ".downloads");
            var file = Path.Combine(downloads, $"{key}-{Guid.NewGuid():N}.zip");
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                if (sha256 != null)
                {
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("the download is corrupt (checksum mismatch); try again");
                }
                Directory.CreateDirectory(downloads);
                try { await File.WriteAllBytesAsync(file, bytes); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"The download couldn't be saved. {ModActions.AntivirusHint}", e); }
                // Antivirus removes a file it doesn't like straight after it's written.
                await Task.Delay(300);
                if (!File.Exists(file) || new FileInfo(file).Length != bytes.Length)
                    throw new InvalidOperationException($"The download was removed after saving. {ModActions.AntivirusHint}");
            }
            catch (Exception e) { error = e.Message; }

            MainThread.Enqueue(() =>
            {
                try
                {
                    if (error == null)
                    {
                        ModActions.Import(file, enable: enable, folder: key);
                        installed();
                    }
                }
                catch (Exception e) { error = e.Message; }
                finally
                {
                    try { File.Delete(file); } catch { }
                    Downloading.Remove(key);
                }
                if (error != null) Plugin.Logger.LogWarning($"{what} failed: {error}");
                done(error);
                Changed?.Invoke();
            });
        });
    }

    // ---- updating MSML --------------------------------------------------------------------------

    /// <summary>True while the MSML update is downloading (before the game closes to install it).</summary>
    public static bool MsmlDownloading { get; private set; }

    /// <summary>
    /// Downloads the MSML release into the game folder (checking its SHA-256), then closes the game, installs it with
    /// the installer inside it in a PowerShell window, and starts the game again. <paramref name="failed"/> gets an
    /// error if it couldn't get that far (the game keeps running).
    /// </summary>
    /// <remarks>
    /// Nothing is downloaded and run as a script: PowerShell only runs the release's own install.ps1 from the
    /// downloaded (checked) package. Downloading a script and running it straight away is what malware does, and
    /// Windows Security flags it.
    /// </remarks>
    public static void UpdateMsml(Action<string> failed)
    {
        if (MsmlDownloading) return;
        var target = MsmlUpdate ?? "";
        var pid = Environment.ProcessId;
        var gameDir = Paths.GameRootPath;
        var work = Path.Combine(gameDir, ".msml-update");
        MsmlDownloading = true;
        Changed?.Invoke();
        Task.Run(async () =>
        {
            string? error = null, installer = null;
            try
            {
                var release = JsonDocument.Parse(await Http.GetStringAsync(ReleaseInfoUrl)).RootElement;
                var package = release.GetProperty("installer");
                var name = Text(package, "file") ?? throw new InvalidOperationException("the release has no installer");
                var sha = Text(package, "sha256");
                try
                {
                    if (Directory.Exists(work)) Directory.Delete(work, true);
                    Directory.CreateDirectory(work);
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("Windows won't let MSML write to the game folder. Update with the install command instead " +
                        "(it asks for administrator rights): " + VersionCheck.InstallCommand);
                }
                var zip = Path.Combine(work, name);
                using (var big = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    big.DefaultRequestHeaders.UserAgent.ParseAdd("MSML-update");
                    var bytes = await big.GetByteArrayAsync($"https://github.com/{Repo}/releases/latest/download/{name}");
                    if (sha != null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("the download is corrupt (checksum mismatch); try again");
                    await File.WriteAllBytesAsync(zip, bytes);
                }
                var pack = Path.Combine(work, "pack");
                try { ZipFile.ExtractToDirectory(zip, pack); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"the update couldn't be unpacked. {ModActions.AntivirusHint}", e); }
                await Task.Delay(500);
                // Everything in the package must have arrived (antivirus removes files as they're written).
                using (var archive = ZipFile.OpenRead(zip))
                    foreach (var entry in archive.Entries)
                        if (entry.Name.Length > 0 && !File.Exists(Path.Combine(pack, entry.FullName)))
                            throw new InvalidOperationException($"{entry.Name} was removed after downloading. {ModActions.AntivirusHint}");
                File.Delete(zip);
                installer = Directory.GetFiles(pack, "install.ps1", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException("the update package has no installer");
            }
            catch (Exception e) { error = e.Message; }

            MainThread.Enqueue(() =>
            {
                MsmlDownloading = false;
                Changed?.Invoke();
                if (error != null || installer == null)
                {
                    Plugin.Logger.LogWarning("Updating MSML failed: " + error);
                    try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
                    failed(error ?? "the update couldn't be prepared");
                    return;
                }
                RunInstaller(installer, target, pid, gameDir, work);
            });
        });
    }

    // Closes the game and runs the downloaded installer once it has closed, then starts the game again.
    private static void RunInstaller(string installer, string target, int pid, string gameDir, string work)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var stamp = Path.Combine(Paths.BepInExRootPath, "MoonSweepMods.install.json");
        var script = $@"
$Host.UI.RawUI.WindowTitle = 'Updating MSML'
Write-Host 'Waiting for Moon Sweepers to close...'
Wait-Process -Id {pid} -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$ok = $false
try {{
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(installer)} -GameDir {Quote(gameDir)}
    try {{ $ok = (Get-Content {Quote(stamp)} -Raw | ConvertFrom-Json).packVersion -eq {Quote(target)} }} catch {{ }}
}} catch {{ Write-Host $_.Exception.Message -ForegroundColor Red }}
if ($ok) {{
    Write-Host 'MSML is up to date. Starting Moon Sweepers...' -ForegroundColor Green
    Set-Location {Quote(gameDir)}
    Remove-Item -Recurse -Force {Quote(work)} -ErrorAction SilentlyContinue
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
        var path = Path.Combine(work, "update.ps1");
        File.WriteAllText(path, script);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"") { UseShellExecute = true });
        Plugin.Logger.LogInfo($"Updating MSML to {target}: closing the game.");
        Application.Quit();
    }
}
