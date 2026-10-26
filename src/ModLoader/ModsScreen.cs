using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// The MODS main-menu entry and the Mods screen: Disabled and Enabled columns (click to select, double-click or the
/// arrow to move), a details pane with the mod's picture and description, and Add mod / Open folder / Restart.
/// Built from the game's own menu kit (MoonSimpleKit) so it matches the other menu screens. Coordinates are in the
/// menu's 1920x1080 frame, measured from the top-left, like the game's own screens.
/// </summary>
internal static class ModsScreen
{
    private const string EntryName = "MODS";
    private const float Edge = 1500f;          // sidebar width while the screen is open (Settings uses 1250)
    private const float Left = 64f;
    private const float ColW = 390f;
    private const float Col2X = Left + ColW + 24f;
    private const float DetX = Col2X + ColW + 42f;
    private const float DetW = 1400f - DetX;
    private const float HeaderY = 196f;
    private const float ListTop = 240f;
    private const float ListBottom = 950f;
    private const float RowH = 56f;
    private const float RowGap = 6f;
    private const float FootY = 990f;

    private static MoonMainMenu? _menu;
    private static MoonSimpleKit.Choice? _entry;
    private static RectTransform? _root;
    private static TMP_Text? _status;
    private static TMP_Text? _disabledHeader;
    private static TMP_Text? _enabledHeader;
    private static RectTransform? _disabledContent;
    private static RectTransform? _enabledContent;
    private static RectTransform? _details;
    private static MoonSimpleKit.Chip? _restartChip;

    private static List<ModInfo> _mods = new();
    private static List<string> _enabled = new();
    private static HashSet<string>? _session;
    private static HashSet<string>? _loaded;
    private static readonly HashSet<string> Changed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> Previews = new(StringComparer.OrdinalIgnoreCase);
    private static string? _selected;
    private static string? _lastClickKey;
    private static float _lastClickTime;

    public static bool IsOpen { get; private set; }
    public static bool DialogOpen => _menu != null && _menu.dialog != null && _menu.dialog.Open;

    // ---- main menu entry -------------------------------------------------------------------------

    public static void AddMenuEntry(MoonMainMenu menu)
    {
        _menu = menu;
        var rows = menu.homeRows;
        if (rows == null || rows.Length == 0) return;
        for (var i = 0; i < rows.Length; i++) if (rows[i].rect.name == EntryName) return;

        // Insert above the last row (QUIT GAME).
        var insertAt = rows.Length - 1;
        var entry = menu.HomeRow(EntryName, Act(Open), insertAt);
        var list = new List<MoonSimpleKit.Choice>();
        for (var i = 0; i < rows.Length; i++) list.Add(rows[i]);
        list.Insert(insertAt, entry);
        menu.homeRows = new Il2CppReferenceArray<MoonSimpleKit.Choice>(list.ToArray());

        // Rows after ours were built with their old index for mouse hover; point them at the new one.
        for (var i = insertAt + 1; i < list.Count; i++)
        {
            var index = i;
            var hover = list[i].hover;
            if (hover == null) continue;
            var original = hover.enter;
            hover.enter = Act(() =>
            {
                original?.Invoke();
                _menu?.Hover(index);
            });
        }
        entry.rect.SetSiblingIndex(list[insertAt + 1].rect.GetSiblingIndex());
        _entry = entry;
        UpdateEntryDetail();
        menu.LayoutHome();
    }

    public static void Forget(MoonMainMenu menu)
    {
        if (_menu == null || _menu.Pointer != menu.Pointer) return;
        _menu = null;
        _root = null;
        _entry = null;
        IsOpen = false;
    }

    private static void UpdateEntryDetail()
    {
        if (_entry?.detail == null) return;
        LoadState();
        var pending = HasPendingChanges();
        _entry.detail.text = pending ? "Restart to apply mod changes" : VersionCheck.MenuNote;
        _entry.detail.gameObject.SetActive(_entry.detail.text.Length > 0);
    }

    // ---- open / close ----------------------------------------------------------------------------

    private static bool _mismatchAccepted;

    public static void Open()
    {
        if (_menu == null || IsOpen || _menu.current != MoonMainMenu.Panel.None || DialogOpen) return;
        if (VersionCheck.Mismatch && !_mismatchAccepted)
        {
            // Once per session: warn before opening the Mods screen.
            Ask("Version mismatch", VersionCheck.MismatchMessage, "PROCEED", () => { _mismatchAccepted = true; OpenScreen(); }, "BACK");
            return;
        }
        OpenScreen();
    }

    private static void OpenScreen()
    {
        if (_menu == null || IsOpen || _menu.current != MoonMainMenu.Panel.None) return;
        EnsureBuilt();
        LoadState();
        _selected ??= _enabled.FirstOrDefault(k => Find(k) != null) ?? _mods.FirstOrDefault()?.Key;

        // Activate first: text can only measure itself (for chip sizes) while active.
        _menu.home.gameObject.SetActive(false);
        _root!.gameObject.SetActive(true);
        SetLinksVisible(false);
        _menu.kit.SidebarTo(Edge, false);
        if (_menu.stage != null) _menu.stage._PanelOpen_k__BackingField = true;
        IsOpen = true;
        LayoutFooter();
        Refresh();
    }

    public static void Close()
    {
        if (!IsOpen || _menu == null) return;
        IsOpen = false;
        if (Alive(_root)) _root!.gameObject.SetActive(false);
        _menu.home.gameObject.SetActive(true);
        SetLinksVisible(true);
        _menu.kit.SidebarTo(MoonSimpleKit.EdgeNarrow, false);
        if (_menu.stage != null) _menu.stage._PanelOpen_k__BackingField = false;
        UpdateEntryDetail();
        _menu.LayoutHome();
    }

    /// <summary>The Discord / Steam review buttons sit over the right of the screen; the game hides them on its panels too.</summary>
    private static void SetLinksVisible(bool visible)
    {
        foreach (var chip in new[] { _menu!.discordChip, _menu.reviewChip })
            if (chip?.rect != null) chip.rect.gameObject.SetActive(visible);
    }

    // ---- state -----------------------------------------------------------------------------------

    private static void LoadState()
    {
        _mods = ModCatalog.Scan();
        _enabled = ModCatalog.LoadEnabled().Where(k => Find(k) != null).ToList();
        _session ??= new HashSet<string>(ModCatalog.LoadSession(), StringComparer.OrdinalIgnoreCase);
        _loaded ??= LoadedModKeys();
        if (_selected != null && Find(_selected) == null) _selected = null;
    }

    /// <summary>Mods (by key) that BepInEx actually loaded a plugin from this session.</summary>
    private static HashSet<string> LoadedModKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = Path.GetFullPath(ModPaths.ActiveDir) + Path.DirectorySeparatorChar;
        foreach (var plugin in IL2CPPChainloader.Instance.Plugins.Values)
        {
            var location = plugin.Location == null ? "" : Path.GetFullPath(plugin.Location);
            if (!location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            keys.Add(location[prefix.Length..].Split(Path.DirectorySeparatorChar)[0]);
        }
        return keys;
    }

    private static ModInfo? Find(string key) => _mods.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static bool IsEnabled(string key) => _enabled.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool HasPendingChanges() =>
        !new HashSet<string>(_enabled, StringComparer.OrdinalIgnoreCase).SetEquals(_session!.Where(k => Find(k) != null))
        || Changed.Any(k => IsEnabled(k) || _session!.Contains(k));

    private static void Toggle(string key)
    {
        if (IsEnabled(key)) _enabled.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        else _enabled.Add(key);
        ModCatalog.SaveEnabled(_enabled);
        _selected = key;
        Refresh();
    }

    private static void Click(string key)
    {
        var now = Time.unscaledTime;
        if (_lastClickKey == key && now - _lastClickTime < 0.4f)
        {
            _lastClickKey = null;
            Toggle(key);
            return;
        }
        _lastClickKey = key;
        _lastClickTime = now;
        _selected = key;
        Refresh();
    }

    // ---- building --------------------------------------------------------------------------------

    private static void EnsureBuilt()
    {
        if (Alive(_root)) return;
        var kit = _menu!.kit;
        _root = kit.Rect(_menu.frame, "Mods screen", 0, 0, 1920, 1080);
        if (_menu.lookScreen != null) _root.SetSiblingIndex(_menu.lookScreen.GetSiblingIndex() + 1);
        _root.gameObject.SetActive(false);

        kit.Title(_root, EntryName, 64);
        _status = kit.Text(_root, "", Left, 136, 1336, 30, 20, MoonSimpleKit.Faint, false, TextAlignmentOptions.MidlineLeft);

        _disabledHeader = kit.Text(_root, "", Left, HeaderY, ColW, 33, 22, MoonSimpleKit.Cream, true, TextAlignmentOptions.MidlineLeft);
        _enabledHeader = kit.Text(_root, "", Col2X, HeaderY, ColW, 33, 22, MoonSimpleKit.Cream, true, TextAlignmentOptions.MidlineLeft);
        _disabledContent = MakeList("Disabled mods", Left);
        _enabledContent = MakeList("Enabled mods", Col2X);
        _details = kit.Rect(_root, "Details", DetX, HeaderY, DetW, ListBottom - HeaderY);

        _footer.Clear();
        _footer.Add(kit.NewChip(_root, "BACK", Left, FootY, 46, 18, Act(Close), null, "Esc", false));
        _footer.Add(kit.NewChip(_root, "ADD MOD", Left, FootY, 46, 18, Act(AddMod), null, null, false));
        _footer.Add(kit.NewChip(_root, "OPEN MODS FOLDER", Left, FootY, 46, 18, Act(ModActions.OpenModsFolder), null, null, false));
        _restartChip = kit.NewChip(_root, "RESTART TO APPLY", 0, FootY, 46, 18, Act(ConfirmRestart), null, null, true);
        _version = kit.Text(_root, "", DetX, 136, DetW, 30, 16, MoonSimpleKit.Faint, false, TextAlignmentOptions.MidlineRight);
    }

    private static TMP_Text? _version;

    private static readonly List<MoonSimpleKit.Chip> _footer = new();

    /// <summary>Sizes chips to their labels (only works while the screen is active) and lays out the footer.</summary>
    private static void LayoutFooter()
    {
        var kit = _menu!.kit;
        var x = Left;
        foreach (var chip in _footer)
        {
            MoonSimpleKit.Size(chip);
            MoonSimpleKit.Place(chip.rect, x, FootY, chip.rect.sizeDelta.x, 46);
            x += chip.rect.sizeDelta.x + 14;
        }
        MoonSimpleKit.Size(_restartChip!);
        var w = _restartChip!.rect.sizeDelta.x;
        MoonSimpleKit.Place(_restartChip.rect, DetX + DetW - w, FootY, w, 46);
    }

    private static RectTransform MakeList(string name, float x)
    {
        var kit = _menu!.kit;
        var view = kit.Rect(_root, name, x, ListTop, ColW, ListBottom - ListTop);
        MoonSimpleKit.Sprite9(view, "Background", MoonSimpleKit.Rounded(false), new Color(0.02f, 0.025f, 0.035f, 0.35f));
        view.gameObject.AddComponent<RectMask2D>();
        var content = kit.Rect(view, "Content", 0, 0, ColW, 0);
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

    private static void Refresh()
    {
        if (_menu == null || !Alive(_root)) return;
        var kit = _menu.kit;
        var disabled = _mods.Where(m => !IsEnabled(m.Key)).ToList();
        var enabled = _enabled.Select(Find).Where(m => m != null).Select(m => m!).ToList();

        _disabledHeader!.text = $"DISABLED  <color=#{Hex(MoonSimpleKit.Faint)}>{disabled.Count}</color>";
        _enabledHeader!.text = $"ENABLED  <color=#{Hex(MoonSimpleKit.Faint)}>{enabled.Count}</color>";
        FillList(_disabledContent!, disabled, false,
            _mods.Count == 0 ? "Your Mods folder is empty.\nUse ADD MOD, or put mods in the folder." : "Every mod is enabled.");
        FillList(_enabledContent!, enabled, true, "No mods enabled.\nDouble-click a mod to enable it.");

        var pending = HasPendingChanges();
        _status!.text = pending
            ? "You have changes that apply after a restart."
            : "Choose which mods load when the game starts. Changes apply after a restart.";
        _status.color = pending ? MoonSimpleKit.Orange : MoonSimpleKit.Faint;
        _restartChip!.rect.gameObject.SetActive(pending);
        _version!.text = VersionCheck.StatusLine(Hex(MoonSimpleKit.Orange));
        FillDetails();
    }

    private static void FillList(RectTransform content, List<ModInfo> mods, bool enabledColumn, string emptyText)
    {
        var kit = _menu!.kit;
        Clear(content);
        var width = ColW - 16;
        var y = 8f;
        foreach (var mod in mods)
        {
            var key = mod.Key;
            var selected = key.Equals(_selected, StringComparison.OrdinalIgnoreCase);
            var row = kit.Rect(content, key, 8, y, width, RowH);
            var fill = MoonSimpleKit.Sprite9(row, "Fill", MoonSimpleKit.Rounded(false), selected ? MoonSimpleKit.PillFill : MoonSimpleKit.ChipFill);
            if (selected) kit.Line(row, 0, 10, 4, RowH - 20, MoonSimpleKit.Orange);

            var nameText = kit.Text(row, mod.Name, 18, 0, width - 18 - 96, RowH, 21, selected ? MoonSimpleKit.Cream : MoonSimpleKit.Soft, false, TextAlignmentOptions.MidlineLeft);
            nameText.overflowMode = TextOverflowModes.Ellipsis;
            nameText.raycastTarget = false;
            var badge = mod.Problem != null ? "!" : Pending(key) ? "*" : "";
            var tag = kit.Text(row, badge, width - 96, 0, 40, RowH, 22, mod.Problem != null ? MoonSimpleKit.Red : MoonSimpleKit.Orange, true, TextAlignmentOptions.Center);
            tag.raycastTarget = false;

            var normal = selected ? MoonSimpleKit.PillFill : MoonSimpleKit.ChipFill;
            var hover = fill.gameObject.AddComponent<MoonWorkshopHover>();
            hover.enter = Act(() => { if (Alive(fill)) fill.color = MoonSimpleKit.PillFill; });
            hover.exit = Act(() => { if (Alive(fill)) fill.color = normal; });
            OnClick(fill.gameObject, () => Click(key));

            // Move arrow on the right of each row.
            var move = kit.Rect(row, "Move", width - 56, 0, 56, RowH);
            var moveHit = move.gameObject.AddComponent<Image>();
            moveHit.color = new Color(0, 0, 0, 0);
            var arrow = kit.Text(move, enabledColumn ? "<" : ">", 0, 0, 56, RowH, 26, MoonSimpleKit.Faint, true, TextAlignmentOptions.Center);
            arrow.raycastTarget = false;
            var moveHover = move.gameObject.AddComponent<MoonWorkshopHover>();
            moveHover.enter = Act(() => { if (Alive(arrow)) arrow.color = MoonSimpleKit.Orange; });
            moveHover.exit = Act(() => { if (Alive(arrow)) arrow.color = MoonSimpleKit.Faint; });
            OnClick(move.gameObject, () => Toggle(key));

            y += RowH + RowGap;
        }
        if (mods.Count == 0)
        {
            var empty = kit.Text(content, emptyText, 20, 20, ColW - 40, 80, 18, MoonSimpleKit.Dimmed, false, TextAlignmentOptions.TopLeft);
            Wrap(empty);
            y = 120;
        }
        content.sizeDelta = new Vector2(content.sizeDelta.x, y + 2);
    }

    private static bool Pending(string key) => IsEnabled(key) != _session!.Contains(key) || Changed.Contains(key);

    private static void FillDetails()
    {
        var kit = _menu!.kit;
        Clear(_details!);
        var mod = _selected == null ? null : Find(_selected);
        if (mod == null)
        {
            Wrap(kit.Text(_details, _mods.Count == 0 ? "No mods installed yet." : "Select a mod to see its details.",
                0, 44, DetW, 60, 20, MoonSimpleKit.Dimmed, false, TextAlignmentOptions.TopLeft));
            return;
        }

        // Picture (16:9, cropped to fill) or the mod's initials.
        var ph = Mathf.Round(DetW * 9f / 16f);
        var frame = kit.Rect(_details, "Preview", 0, 44, DetW, ph);
        var maskImage = frame.gameObject.AddComponent<Image>();
        maskImage.sprite = MoonSimpleKit.Rounded(false);
        maskImage.type = Image.Type.Sliced;
        maskImage.color = MoonSimpleKit.ChipFill;
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var texture = LoadPreview(mod);
        if (texture != null)
        {
            var image = kit.Rect(frame, "Picture", 0, 0, DetW, ph).gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.uvRect = CoverRect(texture.width, texture.height, DetW, ph);
        }
        else
        {
            kit.Text(frame, Initials(mod.Name), 0, 0, DetW, ph, 96, MoonSimpleKit.Dimmed, true, TextAlignmentOptions.Center);
        }

        var y = 44 + ph + 18;
        var name = kit.Text(_details, mod.Name, 0, y, DetW, 44, 34, MoonSimpleKit.Cream, true, TextAlignmentOptions.MidlineLeft);
        name.overflowMode = TextOverflowModes.Ellipsis;
        y += 46;

        var meta = new List<string>();
        if (mod.Version.Length > 0) meta.Add("Version " + mod.Version);
        if (mod.Author.Length > 0) meta.Add("by " + mod.Author);
        if (meta.Count > 0)
        {
            kit.Text(_details, string.Join("  ·  ", meta), 0, y, DetW, 26, 18, MoonSimpleKit.Faint, false, TextAlignmentOptions.MidlineLeft);
            y += 30;
        }

        var (status, color) = StatusOf(mod);
        kit.Text(_details, status, 0, y, DetW, 26, 16, color, true, TextAlignmentOptions.MidlineLeft);
        y += 34;
        if (mod.Problem != null)
        {
            var problem = kit.Text(_details, mod.Problem, 0, y, DetW, 48, 17, MoonSimpleKit.Red, false, TextAlignmentOptions.TopLeft);
            Wrap(problem);
            y += 52;
        }

        var toggleY = ListBottom - HeaderY - 52;
        var description = mod.Description.Length > 0 ? mod.Description : "No description.";
        var desc = kit.Text(_details, description, 0, y, DetW, toggleY - 16 - y, 20, mod.Description.Length > 0 ? MoonSimpleKit.Soft : MoonSimpleKit.Dimmed, false, TextAlignmentOptions.TopLeft);
        Wrap(desc);

        var key = mod.Key;
        if (mod.Dlls.Count == 0 && !IsEnabled(key)) return;
        var toggle = kit.NewChip(_details, IsEnabled(key) ? "DISABLE" : "ENABLE", 0, toggleY, 52, 20, Act(() => Toggle(key)), null, null, !IsEnabled(key));
        MoonSimpleKit.Size(toggle);
    }

    private static (string, Color) StatusOf(ModInfo mod)
    {
        var kit = _menu!.kit;
        var now = _session!.Contains(mod.Key);
        var next = IsEnabled(mod.Key);
        if (now && next && Changed.Contains(mod.Key)) return ("UPDATED · RESTART TO LOAD THE NEW VERSION", MoonSimpleKit.Orange);
        if (!now && next) return ("WILL BE ENABLED AFTER A RESTART", MoonSimpleKit.Orange);
        if (now && !next) return ("WILL BE DISABLED AFTER A RESTART", MoonSimpleKit.Orange);
        if (now && _loaded!.Contains(mod.Key)) return ("ENABLED · RUNNING", MoonSimpleKit.Cream);
        if (now) return ("ENABLED · FAILED TO LOAD (SEE BepInEx\\LogOutput.log)", MoonSimpleKit.Red);
        return ("DISABLED", MoonSimpleKit.Faint);
    }

    // ---- actions ---------------------------------------------------------------------------------

    private static void AddMod()
    {
        ModActions.PickAndImport((imported, errors) =>
        {
            foreach (var result in imported) Changed.Add(result.Key);
            LoadState();
            if (imported.Count > 0) _selected = imported[0].Key;
            Refresh();

            if (errors.Count > 0)
            {
                Ask("Couldn't add " + (errors.Count == 1 ? "that mod" : "some mods"), string.Join("\n", errors), "OK", () => { }, null);
            }
            else if (imported.Count > 0)
            {
                var what = imported.Count == 1 ? $"{imported[0].Name} was {(imported[0].Replaced ? "updated" : "added")}" : $"{imported.Count} mods were added";
                Ask("Restart now?", $"{what} and enabled. Restart the game to load it.", "RESTART", ModActions.Restart, "LATER");
            }
        });
    }

    private static void ConfirmRestart() =>
        Ask("Restart Moon Sweepers?", "Your mod changes apply when the game starts again.", "RESTART", ModActions.Restart, "CANCEL");

    private static void Ask(string question, string meaning, string accept, Action yes, string? cancel)
    {
        var dialog = _menu?.dialog;
        if (dialog == null) return;
        // The game's dialog stays up until a callback hides it.
        dialog.Show(question, meaning, accept, Act(() => { dialog.Hide(); yes(); }), cancel!,
            cancel == null ? null! : Act(() => dialog.Hide()));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Texture2D? LoadPreview(ModInfo mod)
    {
        if (mod.PreviewPath == null) return null;
        var cacheKey = mod.PreviewPath + "|" + File.GetLastWriteTimeUtc(mod.PreviewPath).Ticks;
        if (Previews.TryGetValue(cacheKey, out var cached) && Alive(cached)) return cached;
        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Mod preview " + mod.Key };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(mod.PreviewPath))) return null;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Previews[cacheKey] = texture;
            return texture;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't load preview {mod.PreviewPath}: {e.Message}");
            return null;
        }
    }

    private static Rect CoverRect(float texW, float texH, float boxW, float boxH)
    {
        var texAspect = texW / texH;
        var boxAspect = boxW / boxH;
        if (texAspect > boxAspect)
        {
            var w = boxAspect / texAspect;
            return new Rect((1 - w) / 2, 0, w, 1);
        }
        var h = texAspect / boxAspect;
        return new Rect(0, (1 - h) / 2, 1, h);
    }

    private static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    private static void Wrap(TMP_Text text)
    {
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    private static void OnClick(GameObject target, Action action)
    {
        var button = target.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(action));
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

    private static string Hex(Color c) => $"{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";

    private static bool Alive(UnityEngine.Object? o) => o != null && !o.WasCollected;

    private static Il2CppSystem.Action Act(Action action) => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action)!;
}
