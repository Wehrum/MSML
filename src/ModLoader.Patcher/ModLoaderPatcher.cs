using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Preloader.Core.Patching;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// Runs in BepInEx's preloader, before any plugin is discovered. Mirrors the mods enabled in the in-game Mods screen
/// from &lt;game&gt;\Mods into BepInEx\plugins\MoonSweepMods.ModLoader.Active, so BepInEx loads exactly those.
/// Copying (rather than loading from Mods directly) keeps Mods\ unlocked while the game runs, so players can add,
/// update or remove mods at any time.
/// </summary>
[PatcherPluginInfo("moonsweepmods.modloader.patcher", "Mod Loader (startup)", "0.2.0")]
public class ModLoaderPatcher : BasePatcher
{
    public override void Initialize()
    {
        try
        {
            Directory.CreateDirectory(ModPaths.ModsDir);
            var mods = ModCatalog.Scan().ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
            var enabled = ModCatalog.LoadEnabled();
            var active = new List<string>();

            Directory.CreateDirectory(ModPaths.ActiveDir);
            foreach (var key in enabled)
            {
                if (!mods.TryGetValue(key, out var mod))
                {
                    Log.LogWarning($"Enabled mod '{key}' is no longer in the Mods folder; skipping.");
                    continue;
                }
                var target = Path.Combine(ModPaths.ActiveDir, mod.Key);
                if (mod.IsLooseFile)
                {
                    Directory.CreateDirectory(target);
                    MirrorFiles(new[] { (mod.SourcePath, Path.GetFileName(mod.SourcePath)) }, target);
                }
                else
                {
                    MirrorFiles(Directory.GetFiles(mod.SourcePath, "*", SearchOption.AllDirectories)
                        .Select(f => (f, Path.GetRelativePath(mod.SourcePath, f))), target);
                }
                active.Add(mod.Key);
                Log.LogInfo($"Enabled: {mod.Name} {mod.Version} ({mod.Key})");
            }

            foreach (var dir in Directory.GetDirectories(ModPaths.ActiveDir))
            {
                if (!active.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) Directory.Delete(dir, true);
            }

            ModCatalog.SaveSession(active);
            Log.LogInfo($"{active.Count} of {mods.Count} mods enabled.");
        }
        catch (Exception e)
        {
            Log.LogError("Failed to prepare mods: " + e);
        }
    }

    /// <summary>Makes <paramref name="target"/> contain exactly the given files, copying only what changed.</summary>
    private static void MirrorFiles(IEnumerable<(string source, string relative)> files, string target)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, relative) in files)
        {
            var dest = Path.Combine(target, relative);
            wanted.Add(Path.GetFullPath(dest));
            var src = new FileInfo(source);
            var dst = new FileInfo(dest);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(dst.DirectoryName!);
            src.CopyTo(dest, true);
        }
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            if (!wanted.Contains(Path.GetFullPath(file))) File.Delete(file);
        }
        foreach (var dir in Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
    }
}
