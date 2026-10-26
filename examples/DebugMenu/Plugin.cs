using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using MoonCleaners;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MoonSweepMods.DebugMenu;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "moonsweepmods.debugmenu";
    public const string Name = "Debug Menu";
    public const string Version = "0.3.0";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        GarageOpener.Patch(new HarmonyLib.Harmony(Guid));
        AddComponent<DebugPanel>();
    }
}

/// <summary>
/// The DEBUG panel, shown on the right while the pause menu is open (F7 hides or shows it). Built with uGUI:
/// this game strips the Unity code that IMGUI buttons and toggles need.
/// </summary>
public class DebugPanel : MonoBehaviour
{
    private const float Width = 400;
    private const float Height = 1000;
    private const float Pad = 18;
    private const float RowH = 44;

    private static readonly Color Panel = new(0.06f, 0.065f, 0.08f, 0.95f);
    private static readonly Color ButtonNormal = new(0.165f, 0.161f, 0.192f, 1);
    private static readonly Color ButtonHover = new(0.27f, 0.26f, 0.31f, 1);
    private static readonly Color Orange = new(1f, 0.478f, 0.18f, 1);
    private static readonly Color Cream = new(0.949f, 0.929f, 0.894f, 1);
    private static readonly Color Faint = new(0.627f, 0.612f, 0.588f, 1);

    private bool _visible = true;
    private GameObject? _canvas;
    private TMP_Text? _money;
    private TMP_Text? _status;
    private TMP_Text? _moneyToggle;
    private TMP_Text? _energyToggle;
    private TMP_Text? _flyToggle;
    private TMP_Text? _clock;
    private RectTransform? _list;
    private int _listCount = -1;
    private float _statusUntil;
    private float _nextRefresh;
    private MoonPauseMenu? _pause;
    private float _nextPauseLookup;
    private TMP_FontAsset? _bold;
    private TMP_FontAsset? _regular;

    public DebugPanel(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        try
        {
            // In someone else's crew the host owns the money, clock and vehicles, so the Debug Menu is host-only.
            if (Crewmate())
            {
                if (_canvas != null && !_canvas.WasCollected && _canvas && _canvas.activeSelf) _canvas.SetActive(false);
                return;
            }

            if (Keyboard.current != null && Keyboard.current.f7Key.wasPressedThisFrame) _visible = !_visible;
            Cheats.Tick();
            VehicleSpawner.Tick();
            if (VehicleSpawner.TakeStatus() is { } spawned) Show(spawned);
            Clock.Tick();
            if (Clock.TakeStatus() is { } clock) Show(clock);
            GarageOpener.Tick();
            if (GarageOpener.TakeStatus() is { } garage) Show(garage);

            var show = _visible && Game.Equipment != null && PauseOpen();
            if (show && (_canvas == null || _canvas.WasCollected || !_canvas)) Build();
            if (_canvas != null && !_canvas.WasCollected && _canvas && _canvas.activeSelf != show) _canvas.SetActive(show);
            if (show && Time.unscaledTime >= _nextRefresh) Refresh();
        }
        catch (Exception e) { Plugin.Logger.LogError(e); }
    }

    private bool _wasCrewmate;

    /// <summary>True while in a crew someone else hosts. Turns the cheats off once on joining.</summary>
    private bool Crewmate()
    {
        var crewmate = MoonSteamLobby.InLobby && !MoonSteamLobby.IsHost;
        if (crewmate && !_wasCrewmate)
        {
            Cheats.UnlimitedMoney = false;
            Cheats.UnlimitedEnergy = false;
            if (Cheats.Flying && Game.Equipment != null) Cheats.SetFlying(false);
            Plugin.Logger.LogInfo("In someone else's crew: the Debug Menu is off (host only).");
        }
        _wasCrewmate = crewmate;
        return crewmate;
    }

    private bool PauseOpen()
    {
        if ((_pause == null || _pause.WasCollected || !_pause) && Time.unscaledTime >= _nextPauseLookup)
        {
            _pause = Game.Find<MoonPauseMenu>();
            _nextPauseLookup = Time.unscaledTime + 1f;
        }
        return _pause != null && !_pause.WasCollected && _pause && _pause.IsOpen;
    }

    // ---- building ------------------------------------------------------------------------------

    private void Build()
    {
        FindFonts();
        EnsureEventSystem();

        _canvas = new GameObject("Debug Menu canvas");
        DontDestroyOnLoad(_canvas);
        var canvas = _canvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000;
        var scaler = _canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1;
        _canvas.AddComponent<GraphicRaycaster>();

        var panel = Rect(_canvas.transform, "Panel", 0, 0, Width, Height);
        panel.anchorMin = panel.anchorMax = new Vector2(1, 1);
        panel.pivot = new Vector2(1, 1);
        panel.anchoredPosition = new Vector2(-32, -40);
        panel.gameObject.AddComponent<Image>().color = Panel;
        var accent = Rect(panel, "Accent", 0, 0, 5, Height).gameObject.AddComponent<Image>();
        accent.color = Orange;

        var inner = Width - Pad * 2;
        var y = Pad;
        Text(panel, "DEBUG", Pad, y, inner, 40, 30, true, Cream, TextAlignmentOptions.MidlineLeft);
        Text(panel, "F7 hides", Pad, y, inner, 40, 15, false, Faint, TextAlignmentOptions.MidlineRight);
        y += 46;
        _money = Text(panel, "", Pad, y, inner, 30, 20, false, Cream, TextAlignmentOptions.MidlineLeft);
        y += 38;

        _moneyToggle = Button(panel, "", Pad, y, inner, RowH, () => { Cheats.UnlimitedMoney = !Cheats.UnlimitedMoney; return Cheats.UnlimitedMoney ? "Unlimited money on" : "Unlimited money off"; });
        y += RowH + 8;
        var half = (inner - 8) / 2;
        Button(panel, "+ $10,000", Pad, y, half, RowH, () => { Cheats.AddMoney(10_000); return "+$10,000"; });
        Button(panel, "+ $1,000,000", Pad + half + 8, y, half, RowH, () => { Cheats.AddMoney(1_000_000); return "+$1,000,000"; });
        y += RowH + 8;
        _energyToggle = Button(panel, "", Pad, y, inner, RowH, () => { Cheats.UnlimitedEnergy = !Cheats.UnlimitedEnergy; return Cheats.UnlimitedEnergy ? "Unlimited energy on" : "Unlimited energy off"; });
        y += RowH + 8;
        _flyToggle = Button(panel, "", Pad, y, inner, RowH, () => { Cheats.SetFlying(!Cheats.Flying); return Cheats.Flying ? "Flying" : "Walking"; });
        y += RowH + 8;
        Button(panel, "Empty hopper", Pad, y, half, RowH, () => Cheats.SetVehicleDust(false));
        Button(panel, "Fill hopper", Pad + half + 8, y, half, RowH, () => Cheats.SetVehicleDust(true));
        y += RowH + 8;
        Button(panel, "Open garage menu", Pad, y, inner, RowH, GarageOpener.Request);
        y += RowH + 18;

        Text(panel, "CLOCK", Pad, y, inner, 26, 17, true, Cream, TextAlignmentOptions.MidlineLeft);
        _clock = Text(panel, "", Pad, y, inner, 26, 16, false, Faint, TextAlignmentOptions.MidlineRight);
        y += 30;
        var quarter = (inner - 24) / 4;
        var presets = new[] { ("Dawn", "dawn"), ("Noon", "noon"), ("Dusk", "dusk"), ("Night", "midnight") };
        for (var i = 0; i < presets.Length; i++)
        {
            var preset = presets[i].Item2;
            Button(panel, presets[i].Item1, Pad + i * (quarter + 8), y, quarter, RowH - 6, () => Clock.Preset(preset));
        }
        y += RowH + 2;
        var speeds = new[] { ("-1 h", (Func<string>)(() => Clock.Nudge(-1))), ("+1 h", () => Clock.Nudge(1)), ("Pause", () => Clock.Speed(0)), ("x1", () => Clock.Speed(1)) };
        for (var i = 0; i < speeds.Length; i++) Button(panel, speeds[i].Item1, Pad + i * (quarter + 8), y, quarter, RowH - 6, speeds[i].Item2);
        y += RowH + 2;
        Button(panel, "x10", Pad, y, quarter, RowH - 6, () => Clock.Speed(10));
        Button(panel, "x60", Pad + quarter + 8, y, quarter, RowH - 6, () => Clock.Speed(60));
        y += RowH + 12;

        Text(panel, "SPAWN VEHICLE", Pad, y, inner, 26, 17, true, Cream, TextAlignmentOptions.MidlineLeft);
        y += 30;
        var statusH = 54f;
        var listH = Height - y - statusH - Pad;
        _list = ScrollList(panel, Pad, y, inner, listH);
        y += listH + 8;
        _status = Text(panel, "", Pad, y, inner, statusH - 8, 16, false, Orange, TextAlignmentOptions.TopLeft);
        _status.textWrappingMode = TextWrappingModes.Normal;

        Plugin.Logger.LogInfo("Debug panel built.");
        Refresh();
    }

    private void Refresh()
    {
        _nextRefresh = Time.unscaledTime + 0.25f;
        if (_money != null) _money.text = $"Money  <b>${Cheats.Money:N0}</b>";
        if (_moneyToggle != null) _moneyToggle.text = $"Unlimited money   {OnOff(Cheats.UnlimitedMoney)}";
        if (_energyToggle != null) _energyToggle.text = $"Unlimited suit energy   {OnOff(Cheats.UnlimitedEnergy)}";
        if (_flyToggle != null) _flyToggle.text = $"Fly mode   {OnOff(Cheats.Flying)}";
        if (_clock != null) _clock.text = Clock.Describe();
        if (_status != null && Time.unscaledTime >= _statusUntil) _status.text = "";

        var vehicles = MoonPrototypeCatalog.Vehicles;
        if (_list != null && vehicles.Length != _listCount) FillVehicles(vehicles);
    }

    private static string OnOff(bool on) => on ? "<color=#FF7A2E><b>ON</b></color>" : "<color=#A09C96>OFF</color>";

    private void FillVehicles(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MoonPrototypeCatalog.Item> vehicles)
    {
        _listCount = vehicles.Length;
        for (var i = _list!.childCount - 1; i >= 0; i--) Destroy(_list.GetChild(i).gameObject);
        var w = _list.sizeDelta.x;
        var row = 0;
        // Newest first: vehicles added by mods are at the end of the catalog.
        for (var i = vehicles.Length - 1; i >= 0; i--, row++)
        {
            var index = i;
            var item = vehicles[i];
            Button(_list, $"{item.name}  <size=13><color=#A09C96>#{i} · {item.work} · {item.width:0.#} m</color></size>", 0, row * (RowH - 2), w - 12, RowH - 6,
                () => VehicleSpawner.Spawn(index), TextAlignmentOptions.MidlineLeft);
        }
        _list.sizeDelta = new Vector2(w, row * (RowH - 2));
    }

    // ---- uGUI helpers -------------------------------------------------------------------------

    private static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    private TMP_Text Text(Transform parent, string value, float x, float y, float w, float h, float size, bool bold, Color color, TextAlignmentOptions align)
    {
        var rt = Rect(parent, "Text", x, y, w, h);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = bold ? _bold : _regular;
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.richText = true;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.text = value;
        return t;
    }

    private TMP_Text Button(Transform parent, string label, float x, float y, float w, float h, Func<string> action,
        TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        var rt = Rect(parent, "Button", x, y, w, h);
        var image = rt.gameObject.AddComponent<Image>();
        image.color = Color.white;
        var button = rt.gameObject.AddComponent<UnityEngine.UI.Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = ButtonNormal;
        colors.highlightedColor = ButtonHover;
        colors.selectedColor = ButtonNormal;
        colors.pressedColor = Orange;
        colors.colorMultiplier = 1;
        colors.fadeDuration = 0.05f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Run(action))));
        var text = Text(rt, label, 14, 0, w - 28, h, 18, true, Cream, align);
        return text;
    }

    private RectTransform ScrollList(Transform parent, float x, float y, float w, float h)
    {
        var view = Rect(parent, "Vehicles", x, y, w, h);
        view.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.25f);
        view.gameObject.AddComponent<RectMask2D>();
        var content = Rect(view, "Content", 0, 0, w, 0);
        var scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = view;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40;
        scroll.inertia = false;
        return content;
    }

    private void FindFonts()
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
        {
            var f = o.TryCast<TMP_FontAsset>();
            if (f == null) continue;
            if (f.name == "Outfit Bold SDF") _bold = f;
            else if (f.name == "Outfit SDF") _regular = f;
        }
        _regular ??= _bold;
        _bold ??= _regular;
        Plugin.Logger.LogDebug($"Fonts: bold={_bold?.name ?? "default"}, regular={_regular?.name ?? "default"}");
    }

    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("Debug Menu EventSystem");
        DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        Plugin.Logger.LogWarning("No EventSystem in the scene; created one for the debug panel.");
    }

    private void Run(Func<string> action)
    {
        try { Show(action()); }
        catch (Exception e) { Show("Failed: " + e.Message); Plugin.Logger.LogError(e); }
        Refresh();
    }

    private void Show(string message)
    {
        if (_status != null) _status.text = message;
        _statusUntil = Time.unscaledTime + 5f;
        Plugin.Logger.LogInfo(message);
    }
}
