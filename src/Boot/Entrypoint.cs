using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Doorstop;

/// <summary>
/// What Doorstop starts (instead of BepInEx directly). MSML generates the interop assemblies from the game's own
/// GameAssembly.dll when it's installed; after Steam updates the game they no longer match it, and starting BepInEx
/// with them crashes the game. So before starting BepInEx this checks the game is the one MSML was set up for. If it
/// isn't, the player can update MSML (the latest release is downloaded and its installer rebuilds the interop
/// assemblies, then the game starts again) or play without mods this time.
/// </summary>
public static class Entrypoint
{
    private const string Repo = "Wehrum/MSML";
    private const string ReleaseInfoUrl = "https://github.com/" + Repo + "/releases/latest/download/release.json";
    private const string InstallCommand = "irm https://github.com/" + Repo + "/releases/latest/download/get.ps1 | iex";
    private const int SteamAppId = 5335510;
    private const string StampFile = "MoonSweepMods.install.json";
    private const uint Topmost = 0x40000, Foreground = 0x10000;

    private static string _log = "";

    public static void Start()
    {
        var core = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location)!;
        var bepinex = Path.GetDirectoryName(core)!;
        var game = Path.GetDirectoryName(bepinex)!;
        _log = Path.Combine(bepinex, "MSML.Boot.log");
        bool changed;
        try
        {
            File.WriteAllText(_log, $"MSML.Boot {DateTime.Now:s}\r\n");
            changed = GameChanged(game, bepinex, out var why);
            if (changed) Log("The game changed since MSML was set up: " + why);
        }
        catch (Exception e)
        {
            // Never stop the game over this check: start BepInEx as before.
            Log("The game update check failed: " + e);
            changed = false;
        }
        if (!changed)
        {
            StartBepInEx(core);
            return;
        }

        const uint yesNo = 0x4, question = 0x20;
        var answer = MessageBoxW(IntPtr.Zero,
            "Moon Sweepers was updated by Steam, so MSML has to set itself up again for this version before mods can load " +
            "(otherwise the game would crash).\n\n" +
            "Yes: update MSML now. It downloads the latest MSML and sets it up (about a minute, in a PowerShell window), " +
            "then starts the game again.\n\n" +
            "No: play without mods this time.",
            "MSML: Moon Sweepers was updated", yesNo | question | Topmost | Foreground);
        if (answer != 6)
        {
            Log("Starting without mods.");
            return;
        }
        try
        {
            Update(game, bepinex);
            Log("Updating MSML: closing the game.");
            Environment.Exit(0);
        }
        catch (Exception e)
        {
            Log("Updating MSML failed: " + e);
            MessageBoxW(IntPtr.Zero,
                $"MSML couldn't update itself: {e.Message}\n\nThe game will start without mods. To update MSML, close the game " +
                $"and run the install command in PowerShell:\n\n{InstallCommand}",
                "MSML", 0x30 | Topmost | Foreground);
        }
    }

    // BepInEx's own Doorstop entry point (it works out its folders from the process and BepInEx\core).
    private static void StartBepInEx(string core)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(core, "BepInEx.Unity.IL2CPP.dll"));
        var entry = assembly.GetType("Doorstop.Entrypoint", true)!.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        entry.Invoke(null, null);
    }

    // Whether GameAssembly.dll differs from the one the interop assemblies were made from (as the installer noted).
    private static bool GameChanged(string game, string bepinex, out string why)
    {
        why = "";
        var stampPath = Path.Combine(bepinex, StampFile);
        var assemblyPath = Path.Combine(game, "GameAssembly.dll");
        if (!File.Exists(stampPath) || !File.Exists(assemblyPath)) return false;
        var expected = (string?)JsonNode.Parse(File.ReadAllText(stampPath))?["gameAssemblySha256"];
        if (string.IsNullOrEmpty(expected)) return false;

        // Hashing GameAssembly.dll takes a moment: kept with its size and date, and only done again when they change.
        var info = new FileInfo(assemblyPath);
        var cachePath = Path.Combine(bepinex, "cache", "msml-boot.json");
        string? hash = null;
        try
        {
            var cache = File.Exists(cachePath) ? JsonNode.Parse(File.ReadAllText(cachePath)) : null;
            if (cache != null && (long?)cache["size"] == info.Length && (long?)cache["modified"] == info.LastWriteTimeUtc.Ticks)
                hash = (string?)cache["sha256"];
        }
        catch { }
        if (hash == null)
        {
            using (var stream = File.OpenRead(assemblyPath))
            using (var sha = SHA256.Create())
                hash = Convert.ToHexString(sha.ComputeHash(stream));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllText(cachePath, new JsonObject { ["size"] = info.Length, ["modified"] = info.LastWriteTimeUtc.Ticks, ["sha256"] = hash }.ToJsonString());
            }
            catch { }
        }
        if (hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        why = $"GameAssembly.dll is {hash}, MSML was set up for {expected}";
        return true;
    }

    // Downloads the latest release's installer package (checked against its SHA-256) and starts a PowerShell window
    // that waits for the game to close, runs the package's own install.ps1, and starts the game again. Nothing is
    // downloaded and run as a script (Windows Security flags that).
    private static void Update(string game, string bepinex)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MSML-boot");
        var release = JsonDocument.Parse(http.GetStringAsync(ReleaseInfoUrl).GetAwaiter().GetResult()).RootElement;
        var package = release.GetProperty("installer");
        var name = package.GetProperty("file").GetString() ?? throw new InvalidOperationException("the release has no installer");
        var sha = package.TryGetProperty("sha256", out var s) ? s.GetString() : null;

        var work = Path.Combine(game, ".msml-update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var bytes = http.GetByteArrayAsync($"https://github.com/{Repo}/releases/latest/download/{name}").GetAwaiter().GetResult();
        if (sha != null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the download is corrupt (checksum mismatch); try again");
        var zip = Path.Combine(work, name);
        File.WriteAllBytes(zip, bytes);
        var pack = Path.Combine(work, "pack");
        ZipFile.ExtractToDirectory(zip, pack);
        System.Threading.Thread.Sleep(500);
        // Everything in the package must have arrived (antivirus removes files as they're written).
        using (var archive = ZipFile.OpenRead(zip))
            foreach (var entry in archive.Entries)
                if (entry.Name.Length > 0 && !File.Exists(Path.Combine(pack, entry.FullName)))
                    throw new InvalidOperationException($"{entry.Name} was removed after downloading (by antivirus?). Add an exclusion for the game folder in Windows Security and try again.");
        File.Delete(zip);
        var installer = Directory.GetFiles(pack, "install.ps1", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("the update package has no installer");

        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        // Done when the installer succeeded and noted this GameAssembly.dll (the interop assemblies match it).
        var stamp = Path.Combine(bepinex, StampFile);
        var script = $@"
$Host.UI.RawUI.WindowTitle = 'Updating MSML for the new Moon Sweepers version'
Write-Host 'Waiting for Moon Sweepers to close...'
Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$ok = $false
try {{
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(installer)} -GameDir {Quote(game)}
    if ($LASTEXITCODE -eq 0) {{
        $hash = (Get-FileHash {Quote(Path.Combine(game, "GameAssembly.dll"))} -Algorithm SHA256).Hash
        try {{ $ok = (Get-Content {Quote(stamp)} -Raw | ConvertFrom-Json).gameAssemblySha256 -eq $hash }} catch {{ }}
    }}
}} catch {{ Write-Host $_.Exception.Message -ForegroundColor Red }}
if ($ok) {{
    Write-Host 'MSML is set up for this game version. Starting Moon Sweepers...' -ForegroundColor Green
    Write-Host 'Check MODS in the main menu for mod updates.'
    Set-Location {Quote(game)}
    Remove-Item -Recurse -Force {Quote(work)} -ErrorAction SilentlyContinue
    Start-Process 'steam://rungameid/{SteamAppId}'
    Start-Sleep -Seconds 4
}} else {{
    Write-Host ''
    Write-Host 'MSML could not be set up for this game version automatically. You can try with:' -ForegroundColor Yellow
    Write-Host '  {InstallCommand}'
    Read-Host 'Press Enter to start Moon Sweepers'
    Start-Process 'steam://rungameid/{SteamAppId}'
}}
";
        var path = Path.Combine(work, "update.ps1");
        File.WriteAllText(path, script);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"") { UseShellExecute = true });
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(_log, line + "\r\n"); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
