using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace MoonSweepMods.ModLoader;

internal static class ModActions
{
    private const int SteamAppId = 5335510;
    private static readonly ConcurrentQueue<List<string>> PickedFiles = new();
    private static bool _pickerOpen;

    public sealed record ImportResult(string Key, string Name, bool Replaced);

    // ---- Add mod -----------------------------------------------------------------------------------

    public static void PickAndImport(Action<List<ImportResult>, List<string>> done)
    {
        if (_pickerOpen) return;
        _pickerOpen = true;
        _onPicked = done;
        FilePicker.Show("Add mods to Moon Sweepers", "Mods (*.zip, *.dll)\0*.zip;*.dll\0All files\0*.*\0\0",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { } home ? Path.Combine(home, "Downloads") : null,
            files => PickedFiles.Enqueue(files));
    }

    private static Action<List<ImportResult>, List<string>>? _onPicked;

    /// <summary>Called every frame on the main thread; finishes an import once the picker closes.</summary>
    public static void PollFilePicker()
    {
        if (!PickedFiles.TryDequeue(out var files)) return;
        _pickerOpen = false;
        var imported = new List<ImportResult>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            try { imported.Add(Import(file)); }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Couldn't add {file}: {e}");
                errors.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        if (files.Count > 0) _onPicked?.Invoke(imported, errors);
    }

    /// <summary>Copies a .zip or .dll into Mods\ (replacing an older copy of the same mod) and enables it.</summary>
    public static ImportResult Import(string path)
    {
        var mods = ModPaths.ModsDir;
        Directory.CreateDirectory(mods);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string key;
        var staging = Path.Combine(mods, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (ext == ".dll")
            {
                key = SafeName(Path.GetFileNameWithoutExtension(path));
                Directory.CreateDirectory(staging);
                File.Copy(path, Path.Combine(staging, Path.GetFileName(path)));
            }
            else if (ext == ".zip")
            {
                ZipFile.ExtractToDirectory(path, staging);
                // A zip with a single top-level folder: that folder is the mod.
                var entries = Directory.GetFileSystemEntries(staging);
                if (entries.Length == 1 && Directory.Exists(entries[0]))
                {
                    var inner = entries[0];
                    key = SafeName(Path.GetFileName(inner));
                    var flattened = staging + "-root";
                    Directory.Move(inner, flattened);
                    Directory.Delete(staging, true);
                    staging = flattened;
                }
                else
                {
                    key = SafeName(Path.GetFileNameWithoutExtension(path));
                }
            }
            else
            {
                throw new InvalidOperationException("Mods must be .zip or .dll files.");
            }

            if (!Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("It doesn't contain any mod DLLs.");

            var target = Path.Combine(mods, key);
            var replaced = Directory.Exists(target);
            if (replaced) Directory.Delete(target, true);
            Directory.Move(staging, target);

            var enabled = ModCatalog.LoadEnabled();
            if (!enabled.Contains(key, StringComparer.OrdinalIgnoreCase)) enabled.Add(key);
            ModCatalog.SaveEnabled(enabled);

            var name = ModCatalog.Scan().FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Name ?? key;
            Plugin.Logger.LogInfo($"{(replaced ? "Updated" : "Added")} mod {name} ({key}) from {path}");
            return new ImportResult(key, name, replaced);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.StartsWith("_")) clean = clean.TrimStart('_');
        return clean.Length == 0 ? "Mod" : clean;
    }

    // ---- Folder / restart --------------------------------------------------------------------------

    public static void OpenModsFolder()
    {
        Directory.CreateDirectory(ModPaths.ModsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModPaths.ModsDir}\"") { UseShellExecute = true });
    }

    /// <summary>Quits, then relaunches through Steam once this process has exited.</summary>
    public static void Restart()
    {
        var pid = Environment.ProcessId;
        var script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process 'steam://rungameid/{SteamAppId}'";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Plugin.Logger.LogInfo("Restarting the game to apply mod changes.");
        Application.Quit();
    }
}
