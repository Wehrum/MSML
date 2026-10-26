using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using Mono.Cecil;

namespace MoonSweepMods.ModLoader;

// Shared by the patcher (runs before plugins load) and the plugin (in-game Mods screen). Compiled into both.

internal static class ModPaths
{
    public const string ActiveFolderName = "MoonSweepMods.ModLoader.Active";

    /// <summary>&lt;game&gt;\Mods: where players put mods. Every subfolder (or loose .dll) is one mod.</summary>
    public static string ModsDir => Path.Combine(Paths.GameRootPath, "Mods");

    /// <summary>Which mods the player enabled in the Mods screen. Applied at the next launch.</summary>
    public static string StateFile => Path.Combine(Paths.ConfigPath, "MoonSweepMods.ModLoader.mods.json");

    /// <summary>Copies of the enabled mods, refreshed at startup. BepInEx loads plugins from here.</summary>
    public static string ActiveDir => Path.Combine(Paths.PluginPath, ActiveFolderName);

    /// <summary>What the patcher activated for the current session.</summary>
    public static string SessionFile => Path.Combine(Paths.CachePath, "MoonSweepMods.ModLoader.session.json");
}

/// <summary>Optional mod.json in a mod's folder.</summary>
internal sealed class ModManifest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Preview { get; set; }
    public string? Website { get; set; }
}

internal sealed class ModInfo
{
    /// <summary>Folder name (or file name for a loose .dll) inside Mods. Stable identity for enable state.</summary>
    public string Key { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public bool IsLooseFile { get; init; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string? PreviewPath { get; set; }
    public string Website { get; set; } = "";
    public List<string> Dlls { get; } = new();
    public string? Problem { get; set; }
}

internal static class ModCatalog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private static readonly string[] PreviewNames = { "preview.png", "preview.jpg", "icon.png", "icon.jpg" };

    public static List<ModInfo> Scan()
    {
        var mods = new List<ModInfo>();
        var root = ModPaths.ModsDir;
        if (!Directory.Exists(root)) return mods;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".") || name.StartsWith("_")) continue;
            var mod = new ModInfo { Key = name, SourcePath = dir };
            mod.Dlls.AddRange(Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories));
            ReadMetadata(mod, dir);
            mods.Add(mod);
        }
        foreach (var file in Directory.GetFiles(root, "*.dll"))
        {
            var mod = new ModInfo { Key = Path.GetFileName(file), SourcePath = file, IsLooseFile = true };
            mod.Dlls.Add(file);
            ReadMetadata(mod, null);
            mods.Add(mod);
        }
        return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadMetadata(ModInfo mod, string? dir)
    {
        ModManifest? manifest = null;
        if (dir != null)
        {
            var manifestPath = Path.Combine(dir, "mod.json");
            if (File.Exists(manifestPath))
            {
                try { manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), Json); }
                catch (Exception e) { mod.Problem = "mod.json is invalid: " + e.Message; }
            }
        }

        // Fall back to the [BepInPlugin] attribute for anything mod.json doesn't say.
        var (pluginId, pluginName, pluginVersion) = ReadPluginAttribute(mod.Dlls);
        mod.Id = First(manifest?.Id, pluginId, mod.Key);
        mod.Name = First(manifest?.Name, pluginName, Path.GetFileNameWithoutExtension(mod.Key));
        mod.Version = First(manifest?.Version, pluginVersion, "");
        mod.Author = manifest?.Author ?? "";
        mod.Description = manifest?.Description ?? "";
        mod.Website = manifest?.Website ?? "";

        if (dir != null)
        {
            var candidates = manifest?.Preview != null ? new[] { manifest.Preview } : PreviewNames;
            mod.PreviewPath = candidates.Select(p => Path.Combine(dir, p)).FirstOrDefault(File.Exists);
        }
        if (mod.Dlls.Count == 0) mod.Problem ??= "No .dll files found in this mod's folder.";
        else if (pluginId == null) mod.Problem ??= "No BepInEx plugin found in this mod's DLLs.";
    }

    private static (string? id, string? name, string? version) ReadPluginAttribute(IEnumerable<string> dlls)
    {
        foreach (var dll in dlls)
        {
            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadSymbols = false, InMemory = true });
                foreach (var type in asm.MainModule.Types)
                {
                    var attr = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
                    if (attr == null || attr.ConstructorArguments.Count < 3) continue;
                    return ((string)attr.ConstructorArguments[0].Value, (string)attr.ConstructorArguments[1].Value,
                        (string)attr.ConstructorArguments[2].Value);
                }
            }
            catch (BadImageFormatException) { /* native or non-.NET DLL bundled with the mod */ }
        }
        return (null, null, null);
    }

    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // ---- enable state -----------------------------------------------------------------------------

    private sealed class StateDto
    {
        public List<string> Enabled { get; set; } = new();
    }

    private sealed class SessionDto
    {
        public List<string> Loaded { get; set; } = new();
    }

    public static List<string> LoadEnabled() => ReadJson<StateDto>(ModPaths.StateFile)?.Enabled ?? new List<string>();

    public static void SaveEnabled(IEnumerable<string> keys) =>
        WriteJson(ModPaths.StateFile, new StateDto { Enabled = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });

    public static List<string> LoadSession() => ReadJson<SessionDto>(ModPaths.SessionFile)?.Loaded ?? new List<string>();

    public static void SaveSession(IEnumerable<string> keys) => WriteJson(ModPaths.SessionFile, new SessionDto { Loaded = keys.ToList() });

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (Exception) { return null; }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, true);
    }
}
