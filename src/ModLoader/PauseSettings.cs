using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MoonCleaners;
using MSML;
using TMPro;
using UnityEngine;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// MOD SETTINGS in the pause menu (below SETTINGS): the settings of every mod that called
/// <see cref="ModSettings.ShowInGame(BepInEx.Unity.IL2CPP.BasePlugin, string?)"/>, so they can be changed mid-game.
/// The entry only appears when at least one mod registered settings.
/// </summary>
internal static class PauseSettings
{
    private const string EntryName = "MOD SETTINGS";
    private const float Edge = 1250f;   // sidebar width while open, like the game's Settings page
    private const float Left = 64f;
    private const float Top = 196f;
    private const float Width = 1100f;
    private const float Bottom = 950f;
    private const float FootY = 990f;

    private static MoonPauseMenu? _pause;
    private static RectTransform? _root;
    private static RectTransform? _content;
    private static MoonSimpleKit.Chip? _back;

    public static bool IsOpen { get; private set; }

    private static List<(string title, BepInEx.Configuration.ConfigFile config)> Pages() =>
        ModSettings.InGame.Select(p => (p.Title, p.Config)).Where(p => SettingsView.Visible(p.Config).Count > 0).ToList();

    // ---- pause menu entry ------------------------------------------------------------------------

    private static void AddEntry(MoonPauseMenu pause)
    {
        _pause = pause;
        _root = null;
        IsOpen = false;
        if (Pages().Count == 0) return;

        var rows = pause.homeRows;
        if (rows == null || rows.Count == 0) return;
        for (var i = 0; i < rows.Count; i++) if (rows[i].Item1.rect.name == EntryName) return;

        // Below SETTINGS (or above the last row if the game renames it); later rows move down one slot.
        var insertAt = rows.Count - 1;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Item1.rect.name == "SETTINGS") { insertAt = i + 1; break; }
        var slot = MoonPauseMenu.RowH + MoonPauseMenu.RowGap;
        var at = rows[insertAt].Item1;
        var entry = pause.kit.NewRow(pause.home, EntryName, EntryName, at.y, MoonPauseMenu.RowH, at.edge, Act(Open), 34, false);
        entry.rect.SetSiblingIndex(at.rect.GetSiblingIndex());
        for (var i = insertAt; i < rows.Count; i++)
        {
            var row = rows[i].Item1;
            MoonSimpleKit.MoveRow(row, row.y + slot, row.height);
        }
        InsertRow(rows, insertAt, entry, Act(Open));

        // Hovering a row selects it by index; the rows were built with their old indices.
        for (var i = insertAt; i < rows.Count; i++)
        {
            var index = i;
            var hover = rows[i].Item1.hover;
            if (hover == null) continue;
            var original = i == insertAt ? null : hover.enter;
            hover.enter = Act(() =>
            {
                original?.Invoke();
                if (Alive(_pause)) _pause!.choice = index;
            });
        }
    }

    /// <summary>
    /// homeRows.Insert(index, (choice, action)). The interop's List&lt;ValueTuple&gt;.Insert passes the tuple as an object
    /// pointer where IL2CPP expects the struct's bytes, storing garbage that crashes the game later; so call the
    /// native method directly with the unboxed tuple.
    /// </summary>
    private static unsafe void InsertRow(Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<MoonSimpleKit.Choice, Il2CppSystem.Action>> rows,
        int index, MoonSimpleKit.Choice choice, Il2CppSystem.Action action)
    {
        var tuple = new Il2CppSystem.ValueTuple<MoonSimpleKit.Choice, Il2CppSystem.Action>(choice, action);
        if (tuple.Item1?.Pointer != choice.Pointer || tuple.Item2?.Pointer != action.Pointer)
            throw new InvalidOperationException("Couldn't build the menu row tuple.");
        var method = IL2CPP.il2cpp_class_get_method_from_name(IL2CPP.il2cpp_object_get_class(rows.Pointer), "Insert", 2);
        if (method == IntPtr.Zero) throw new MissingMethodException("List.Insert");
        var args = stackalloc IntPtr[2];
        args[0] = (IntPtr)(&index);
        args[1] = IL2CPP.il2cpp_object_unbox(tuple.Pointer);
        var exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(method, rows.Pointer, (void**)args, ref exception);
        Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exception);
    }

    // ---- open / close ----------------------------------------------------------------------------

    private static void Open()
    {
        if (!Alive(_pause) || IsOpen || !_pause!.IsOpen) return;
        EnsureBuilt();
        _pause.home.gameObject.SetActive(false);
        _root!.gameObject.SetActive(true);
        _pause.kit.SidebarTo(Edge, false);
        IsOpen = true;
        MoonSimpleKit.Size(_back!);
        Refresh();
    }

    public static void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (Alive(_root)) _root!.gameObject.SetActive(false);
        if (!Alive(_pause)) return;
        _pause!.home.gameObject.SetActive(true);
        _pause.kit.SidebarTo(MoonSimpleKit.EdgeNarrow, false);
    }

    /// <summary>Called every frame: tidies up if the pause menu closed some other way (e.g. a controller).</summary>
    public static void Tick()
    {
        if (IsOpen && (!Alive(_pause) || !_pause!.IsOpen)) Close();
    }

    private static void EnsureBuilt()
    {
        if (Alive(_root)) return;
        var kit = _pause!.kit;
        _root = kit.Rect(_pause.frame, "Mod settings screen", 0, 0, 1920, 1080);
        _root.SetSiblingIndex(_pause.home.GetSiblingIndex() + 1);
        kit.Title(_root, EntryName, 64);
        kit.Text(_root, "Settings from your mods. Changes are saved right away.", Left, 136, Width, 30, 20, MoonSimpleKit.Faint, false, TextAlignmentOptions.MidlineLeft);
        _content = SettingsView.NewScrollArea(kit, _root, Left, Top, Width, Bottom - Top);
        _back = kit.NewChip(_root, "BACK", Left, FootY, 46, 18, Act(Close), null!, "Esc", false);
    }

    private static void Refresh()
    {
        if (!Alive(_content)) return;
        SettingsView.Fill(_pause!.kit, _content!, Pages(), headings: true, "No mod settings to show.");
    }

    // ---- hooks -----------------------------------------------------------------------------------

    internal static class Hooks
    {
        [HarmonyPatch(typeof(MoonPauseMenu), nameof(MoonPauseMenu.Build))]
        [HarmonyPostfix]
        private static void AfterBuild(MoonPauseMenu __instance)
        {
            try { AddEntry(__instance); }
            catch (Exception e) { Plugin.Logger.LogError("Couldn't add MOD SETTINGS to the pause menu: " + e); }
        }

        // Keyboard / controller steering of the (hidden) home rows; Enter would trigger them.
        [HarmonyPatch(typeof(MoonPauseMenu), nameof(MoonPauseMenu.UpdateUI))]
        [HarmonyPrefix]
        private static bool BeforeUpdateUI() => !IsOpen;

        [HarmonyPatch(typeof(MoonPauseMenu), nameof(MoonPauseMenu.HandleEscape))]
        [HarmonyPrefix]
        private static bool BeforeHandleEscape() => !CloseInstead();

        [HarmonyPatch(typeof(MoonPauseMenu), nameof(MoonPauseMenu.Back))]
        [HarmonyPrefix]
        private static bool BeforeBack() => !CloseInstead();

        [HarmonyPatch(typeof(MoonPauseMenu), nameof(MoonPauseMenu.Resume))]
        [HarmonyPrefix]
        private static void BeforeResume() => Close();

        private static bool CloseInstead()
        {
            if (!IsOpen) return false;
            if (Alive(_pause) && _pause!.dialog != null && _pause.dialog.Open) return true;
            Close();
            return true;
        }
    }

    private static bool Alive(UnityEngine.Object? o) => o != null && !o.WasCollected && o;

    private static Il2CppSystem.Action Act(Action action) => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action)!;
}
