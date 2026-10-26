using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace MSML;

/// <summary>
/// Settings players can change in-game.
/// <para>
/// Declare settings the normal BepInEx way, with <c>Config.Bind(...)</c> in your plugin. Every loaded mod's settings
/// show up in the MODS screen (select the mod, then SETTINGS) with no extra code. Toggles are used for
/// <c>bool</c>, - / + buttons for numbers (bounded by an <see cref="AcceptableValueRange{T}"/>), and choices for
/// enums and <see cref="AcceptableValueList{T}"/>. Changes are saved to the mod's .cfg file immediately; listen to
/// <see cref="ConfigEntry{T}.SettingChanged"/> (or read <c>.Value</c> when you need it) to react.
/// </para>
/// <para>Call <see cref="ShowInGame(BasePlugin, string?)"/> to also list them in the pause menu (MOD SETTINGS).</para>
/// </summary>
public static class ModSettings
{
    private static readonly List<SettingsPage> InGamePages = new();

    /// <summary>Add to a <see cref="ConfigDescription"/>'s tags to keep a setting out of the in-game settings UI.</summary>
    public static readonly object Hidden = new HiddenTag();

    /// <summary>
    /// Add to a <see cref="ConfigDescription"/>'s tags to set how much the - / + buttons change a number setting.
    /// Without it, ints step by 1 and decimals by a twentieth of their range.
    /// </summary>
    public static object Step(double amount) => new StepTag(amount);

    /// <summary>
    /// Also list this mod's settings in the pause menu (MOD SETTINGS), so players can change them while playing.
    /// Only do this for settings that take effect immediately.
    /// </summary>
    /// <param name="plugin">Your plugin (<c>this</c> in <c>Load()</c>).</param>
    /// <param name="title">Heading shown above the settings; defaults to the plugin's name.</param>
    public static void ShowInGame(BasePlugin plugin, string? title = null)
    {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        ShowInGame(plugin.Config, title ?? MetadataHelper.GetMetadata(plugin)?.Name ?? plugin.GetType().Name);
    }

    /// <summary>Lists a config file's settings in the pause menu (MOD SETTINGS) under <paramref name="title"/>.</summary>
    public static void ShowInGame(ConfigFile config, string title)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        lock (InGamePages)
        {
            if (InGamePages.Any(p => ReferenceEquals(p.Config, config))) return;
            InGamePages.Add(new SettingsPage(title, config));
        }
    }

    /// <summary>The settings registered with <see cref="ShowInGame(ConfigFile, string)"/>, in registration order.</summary>
    public static IReadOnlyList<SettingsPage> InGame
    {
        get { lock (InGamePages) return InGamePages.ToArray(); }
    }

    /// <summary>True if the setting is tagged <see cref="Hidden"/>.</summary>
    public static bool IsHidden(ConfigEntryBase entry) => Tags(entry).Any(t => t is HiddenTag);

    /// <summary>The step set with <see cref="Step(double)"/>, if any.</summary>
    public static double? StepOf(ConfigEntryBase entry) => Tags(entry).OfType<StepTag>().FirstOrDefault()?.Amount;

    private static IEnumerable<object> Tags(ConfigEntryBase entry) => entry.Description?.Tags ?? Array.Empty<object>();

    private sealed class HiddenTag
    {
        public override string ToString() => "MSML: hidden";
    }

    private sealed class StepTag
    {
        public StepTag(double amount) => Amount = amount;
        public double Amount { get; }
        public override string ToString() => "MSML: step " + Amount;
    }
}

/// <summary>A titled group of settings (one config file) shown in the settings UI.</summary>
public sealed class SettingsPage
{
    internal SettingsPage(string title, ConfigFile config)
    {
        Title = title;
        Config = config;
    }

    /// <summary>Heading shown above the settings.</summary>
    public string Title { get; }

    /// <summary>The config file holding the settings.</summary>
    public ConfigFile Config { get; }
}
