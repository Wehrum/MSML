using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using MoonCleaners;
using MSML;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// Draws BepInEx config entries as rows in the game's menu style: OFF / ON for bools, - value + for numbers, and a
/// choice per option for enums and value lists. Used by the MODS screen (one mod) and the pause menu (every mod that
/// called <see cref="ModSettings.ShowInGame(BepInEx.Unity.IL2CPP.BasePlugin, string?)"/>). Changes are written to the
/// entry straight away, which saves the mod's .cfg file and raises its SettingChanged event.
/// </summary>
internal static class SettingsView
{
    private const float RowH = 100f;
    private const float RowGap = 8f;
    private const float ControlW = 330f;
    private const float ChipH = 44f;
    private const float ChipText = 18f;

    /// <summary>A scrolling area for settings at the given place; fill it with <see cref="Fill"/>.</summary>
    public static RectTransform NewScrollArea(MoonSimpleKit kit, Transform parent, float x, float y, float w, float h)
    {
        var view = kit.Rect(parent, "Settings", x, y, w, h);
        MoonSimpleKit.Sprite9(view, "Background", MoonSimpleKit.Rounded(false), new Color(0.02f, 0.025f, 0.035f, 0.35f));
        view.gameObject.AddComponent<RectMask2D>();
        var content = kit.Rect(view, "Content", 0, 0, w, 0);
        var scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = view;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = 40;
        return content;
    }

    /// <summary>Settings a player can see in a config file (not tagged <see cref="ModSettings.Hidden"/>).</summary>
    public static List<ConfigEntryBase> Visible(ConfigFile config) =>
        config.Keys.Select(k => config[k]).Where(e => e != null && !ModSettings.IsHidden(e)).ToList();

    /// <summary>
    /// Replaces the content of a scroll area with the given pages. <paramref name="headings"/> shows each page's
    /// title (for lists of several mods). Clicking a control changes the setting and redraws.
    /// </summary>
    public static void Fill(MoonSimpleKit kit, RectTransform content, IReadOnlyList<(string title, ConfigFile config)> pages, bool headings, string emptyText)
    {
        Clear(content);
        var width = content.sizeDelta.x;
        var inner = width - 32;
        var y = 16f;
        void Redraw() => Fill(kit, content, pages, headings, emptyText);

        foreach (var (title, config) in pages)
        {
            var entries = Visible(config);
            if (entries.Count == 0) continue;
            if (headings)
            {
                kit.Text(content, title.ToUpperInvariant(), 16, y, inner, 40, 26, MoonSimpleKit.Cream, true, TextAlignmentOptions.MidlineLeft);
                y += 48;
            }

            var sections = entries.Select(e => e.Definition.Section).Distinct().ToList();
            foreach (var section in sections)
            {
                if (sections.Count > 1)
                {
                    kit.Text(content, Pretty(section).ToUpperInvariant(), 16, y, inner, 28, 16, MoonSimpleKit.Faint, true, TextAlignmentOptions.MidlineLeft);
                    y += 32;
                }
                foreach (var entry in entries.Where(e => e.Definition.Section == section))
                {
                    Row(kit, content, entry, 16, y, inner, Redraw);
                    y += RowH + RowGap;
                }
            }
            y += 16;
        }

        if (y <= 16f)
        {
            var empty = kit.Text(content, emptyText, 16, 16, inner, 60, 20, MoonSimpleKit.Dimmed, false, TextAlignmentOptions.TopLeft);
            empty.textWrappingMode = TextWrappingModes.Normal;
            y = 80;
        }
        content.sizeDelta = new Vector2(width, y);
    }

    private static void Row(MoonSimpleKit kit, RectTransform parent, ConfigEntryBase entry, float x, float y, float w, Action redraw)
    {
        var row = kit.Rect(parent, entry.Definition.Key, x, y, w, RowH);
        MoonSimpleKit.Sprite9(row, "Fill", MoonSimpleKit.Rounded(false), MoonSimpleKit.ChipFill);

        var textW = w - ControlW - 48;
        var label = kit.Text(row, Pretty(entry.Definition.Key), 20, 12, textW, 30, 21, MoonSimpleKit.Cream, true, TextAlignmentOptions.MidlineLeft);
        label.overflowMode = TextOverflowModes.Ellipsis;
        var description = entry.Description?.Description ?? "";
        var defaultText = Format(entry, entry.DefaultValue);
        if (defaultText != null && !Equals(entry.BoxedValue, entry.DefaultValue))
            description = (description.Length > 0 ? description.TrimEnd() + " " : "") + $"Default: {defaultText}.";
        var desc = kit.Text(row, description, 20, 44, textW, RowH - 50, 16, MoonSimpleKit.Faint, false, TextAlignmentOptions.TopLeft);
        desc.textWrappingMode = TextWrappingModes.Normal;
        desc.overflowMode = TextOverflowModes.Ellipsis;

        var right = w - 20;
        var top = (RowH - ChipH) / 2;
        var type = entry.SettingType;
        var options = Options(entry);

        if (type == typeof(bool))
        {
            var on = (bool)entry.BoxedValue;
            Choices(kit, row, new[] { "OFF", "ON" }, on ? 1 : 0, right, top, i => Set(entry, i == 1, redraw));
        }
        else if (options != null && options.Count <= 3)
        {
            var current = options.FindIndex(o => Equals(o, entry.BoxedValue));
            Choices(kit, row, options.Select(o => Format(entry, o) ?? "?").ToArray(), current, right, top, i => Set(entry, options[i], redraw));
        }
        else if (options != null)
        {
            var current = Math.Max(0, options.FindIndex(o => Equals(o, entry.BoxedValue)));
            Stepper(kit, row, Format(entry, entry.BoxedValue) ?? "?", right, top,
                () => Set(entry, options[(current - 1 + options.Count) % options.Count], redraw),
                () => Set(entry, options[(current + 1) % options.Count], redraw));
        }
        else if (IsNumber(type))
        {
            Stepper(kit, row, Format(entry, entry.BoxedValue) ?? "?", right, top,
                () => Set(entry, StepNumber(entry, -1), redraw),
                () => Set(entry, StepNumber(entry, +1), redraw));
        }
        else
        {
            var value = kit.Text(row, (Format(entry, entry.BoxedValue) ?? "") + "\n<size=14>Change it in the mod's .cfg file</size>",
                right - ControlW, 0, ControlW, RowH, 18, MoonSimpleKit.Soft, false, TextAlignmentOptions.MidlineRight);
            value.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    // ---- controls ----------------------------------------------------------------------------------

    /// <summary>One chip per option, right-aligned; the current one is highlighted.</summary>
    private static void Choices(MoonSimpleKit kit, RectTransform row, string[] labels, int current, float right, float top, Action<int> pick)
    {
        var chips = new List<MoonSimpleKit.Chip>();
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var chip = kit.NewChip(row, labels[i], 0, top, ChipH, ChipText, Act(() => { if (index != current) pick(index); }), null!, null!, i == current);
            MoonSimpleKit.Size(chip);
            chip.rect.sizeDelta = new Vector2(Math.Max(chip.rect.sizeDelta.x, 76), ChipH);
            chips.Add(chip);
        }
        var x = right;
        for (var i = chips.Count - 1; i >= 0; i--)
        {
            x -= chips[i].rect.sizeDelta.x;
            MoonSimpleKit.Place(chips[i].rect, x, top, chips[i].rect.sizeDelta.x, ChipH);
            x -= 8;
        }
    }

    /// <summary>[-]  value  [+], right-aligned.</summary>
    private static void Stepper(MoonSimpleKit kit, RectTransform row, string value, float right, float top, Action down, Action up)
    {
        const float valueW = 150f;
        var plus = kit.NewChip(row, "+", 0, top, ChipH, 22, Act(up), null!, null!, false);
        var minus = kit.NewChip(row, "-", 0, top, ChipH, 22, Act(down), null!, null!, false);
        MoonSimpleKit.Place(plus.rect, right - ChipH, top, ChipH, ChipH);
        MoonSimpleKit.Place(minus.rect, right - ChipH * 2 - valueW, top, ChipH, ChipH);
        var text = kit.Text(row, value, right - ChipH - valueW, top, valueW, ChipH, 20, MoonSimpleKit.Cream, true, TextAlignmentOptions.Center);
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    // ---- values ------------------------------------------------------------------------------------

    private static void Set(ConfigEntryBase entry, object value, Action redraw)
    {
        try
        {
            entry.BoxedValue = value;
            entry.ConfigFile.Save();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't change setting {entry.Definition}: {e.Message}");
        }
        redraw();
    }

    /// <summary>The allowed values of an enum or <see cref="AcceptableValueList{T}"/> setting, else null.</summary>
    private static List<object>? Options(ConfigEntryBase entry)
    {
        var acceptable = entry.Description?.AcceptableValues;
        if (acceptable != null && acceptable.GetType().IsGenericType && acceptable.GetType().GetGenericTypeDefinition() == typeof(AcceptableValueList<>))
        {
            if (acceptable.GetType().GetProperty("AcceptableValues")?.GetValue(acceptable) is Array values)
                return values.Cast<object>().ToList();
        }
        if (entry.SettingType.IsEnum && !entry.SettingType.IsDefined(typeof(FlagsAttribute), false))
            return Enum.GetValues(entry.SettingType).Cast<object>().ToList();
        return null;
    }

    private static bool IsNumber(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    private static bool IsWhole(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte);

    private static object StepNumber(ConfigEntryBase entry, int direction)
    {
        var type = entry.SettingType;
        var value = System.Convert.ToDouble(entry.BoxedValue, CultureInfo.InvariantCulture);
        var (min, max) = Range(entry);
        var step = ModSettings.StepOf(entry)
                   ?? (IsWhole(type) ? 1 : min.HasValue && max.HasValue ? NiceStep((max.Value - min.Value) / 20) : 0.1);
        // Snap to multiples of the step so repeated presses give round numbers, then clamp to the range.
        var next = Math.Round(value / step + direction) * step;
        if (min.HasValue) next = Math.Max(min.Value, next);
        if (max.HasValue) next = Math.Min(max.Value, next);
        next = Math.Round(next, 6);
        return System.Convert.ChangeType(IsWhole(type) ? Math.Round(next) : next, type, CultureInfo.InvariantCulture);
    }

    private static (double? min, double? max) Range(ConfigEntryBase entry)
    {
        var acceptable = entry.Description?.AcceptableValues;
        if (acceptable == null || !acceptable.GetType().IsGenericType || acceptable.GetType().GetGenericTypeDefinition() != typeof(AcceptableValueRange<>))
            return (null, null);
        var t = acceptable.GetType();
        return (System.Convert.ToDouble(t.GetProperty("MinValue")!.GetValue(acceptable), CultureInfo.InvariantCulture),
                System.Convert.ToDouble(t.GetProperty("MaxValue")!.GetValue(acceptable), CultureInfo.InvariantCulture));
    }

    /// <summary>Rounds a step to 1, 2 or 5 times a power of ten.</summary>
    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 0.1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / power;
        return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * power;
    }

    private static string? Format(ConfigEntryBase entry, object? value)
    {
        switch (value)
        {
            case null: return null;
            case bool b: return b ? "On" : "Off";
            case float f: return f.ToString("0.##", CultureInfo.InvariantCulture);
            case double d: return d.ToString("0.##", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString("0.##", CultureInfo.InvariantCulture);
            case Enum e: return Pretty(e.ToString());
            default:
                try { return TomlTypeConverter.ConvertToString(value, entry.SettingType); }
                catch { return value.ToString(); }
        }
    }

    /// <summary>"AutoRecenter" or "auto_recenter" to "Auto recenter".</summary>
    internal static string Pretty(string name)
    {
        var spaced = Regex.Replace(name.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
        if (spaced.Length == 0) return name;
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Keep acronyms (FOV, HUD); lower-case ordinary words after the first.
        for (var i = 1; i < words.Length; i++)
            if (!words[i].All(char.IsUpper)) words[i] = words[i].ToLowerInvariant();
        words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(" ", words);
    }

    private static void Clear(Transform parent)
    {
        for (var i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
    }

    private static Il2CppSystem.Action Act(Action action) => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action)!;
}
