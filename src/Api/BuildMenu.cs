using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MoonCleaners;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MSML;

/// <summary>
/// The build menu, for mods adding cards to it (<see cref="BeltType"/>, <see cref="JunctionType"/>...).
/// <para>
/// Sub-tabs: cards can be put in a section of their tab (<see cref="SetSection"/>, or a type's <c>Section</c>). A tab
/// with sections gets a row of sub-tabs at the top: ALL, the game's own cards (<see cref="MainSection"/>), then each
/// section, so a crowded tab stays easy to read.
/// </para>
/// </summary>
public static class BuildMenu
{
    private static readonly Dictionary<int, string> Sections = new();

    /// <summary>Name of the sub-tab for the cards of a tab that aren't in a section (the game's own).</summary>
    public static string MainSection { get; set; } = "Standard";

    /// <summary>Name of the sub-tab showing every card of the tab.</summary>
    public static string AllSection { get; set; } = "All";

    /// <summary>Puts a build card (by its kind, e.g. <see cref="BeltType.Kind"/>) in a section of its tab; null takes it out.</summary>
    public static void SetSection(int kind, string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) Sections.Remove(kind);
        else Sections[kind] = section!.Trim();
        BuildMenuPatches.Apply();
        BuildMenuPatches.Relayout();
    }

    /// <summary>The section a build card is in, or null.</summary>
    public static string? SectionOf(int kind) => Sections.TryGetValue(kind, out var s) ? s : null;
}

/// <summary>
/// Hooks for <see cref="BuildMenu"/>: sub-tabs within tabs, and highlighting a mod's card while it's being built (the
/// game only knows the kind the card stands in for, e.g. the belt for a belt type).
/// </summary>
internal static class BuildMenuPatches
{
    private static bool _applied;
    private static readonly Color Selected = new(0.949f, 0.416f, 0.106f, 1f);
    private static readonly Color Idle = new(0.173f, 0.192f, 0.224f, 1f);
    private static readonly Color IdleText = new(0.949f, 0.929f, 0.894f, 1f);
    private const float ChipHeight = 44f, ChipGap = 10f, Pad = 16f;

    internal static void Apply()
    {
        if (_applied) return;
        _applied = true;
        new Harmony("msml.api.buildmenu").PatchAll(typeof(BuildMenuPatches));
    }

    // ---- which card is being built -------------------------------------------------------------

    // The mod card the player chose, when the game shows the card it stands in for as chosen.
    private static int? ChosenCard(MoonDepotBuilder builder) =>
        BeltPatches.ChosenCard(builder) ?? JunctionPatches.ChosenCard(builder);

    /// <summary>
    /// The mod card whose details are shown (hovered, or just chosen). The game only knows the kind it stands in for,
    /// so after its details are shown the builder's shown kind is set to the card's own: the game then lights up that
    /// card, and hovering the stand-in's card (e.g. the plain belt) counts as hovering something else.
    /// </summary>
    internal static int? Described { get; private set; }

    // Sees the card's own kind, before the belt and junction hooks change it to the game's.
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowDetails))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void NoteDetails(int kind)
    {
        if (Belts.Types.Any(t => t.Kind == kind) || Junctions.Types.Any(t => t.Kind == kind)) Described = kind;
        // Shown while a mod card is being chosen (the game asks for its stand-in's details).
        else Described = BeltPatches.RedirectingCard(kind) ?? JunctionPatches.RedirectingCard(kind);
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowDetails))]
    [HarmonyPostfix]
    private static void AfterDetails(MoonDepotBuilder __instance)
    {
        try { if (Described is { } card) __instance.shownStructure = card; }
        catch (Exception e) { Log.Error("Noting a mod card's details failed: " + e.Message); }
    }

    private static (int BuildId, int Structure)? _swappedFrom;
    private static int _depth;

    // While the cards are animated (their highlight; only then: building needs the game's kind), the chosen card is
    // the mod's.
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.AnimateCatalog))]
    [HarmonyPrefix]
    private static void BeforeAnimate(MoonDepotBuilder __instance) => SwapIn(__instance);

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.AnimateCatalog))]
    [HarmonyFinalizer]
    private static void AfterAnimate(MoonDepotBuilder __instance) => SwapOut(__instance);

    // Nested calls swap once, and the outermost puts it back.
    private static void SwapIn(MoonDepotBuilder builder)
    {
        if (_depth++ > 0) return;
        _swappedFrom = null;
        try
        {
            if (ChosenCard(builder) is not { } card) return;
            _swappedFrom = (builder.selectedBuildId, builder.selectedStructure);
            builder.selectedBuildId = card;
            builder.selectedStructure = card;
        }
        catch (Exception e) { Log.Error("Highlighting a mod's build card failed: " + e.Message); }
    }

    private static void SwapOut(MoonDepotBuilder builder)
    {
        if (--_depth > 0) return;
        _depth = 0;
        if (_swappedFrom is not { } was) return;
        _swappedFrom = null;
        try
        {
            builder.selectedBuildId = was.BuildId;
            builder.selectedStructure = was.Structure;
        }
        catch (Exception e) { Log.Error("Putting the build selection back failed: " + e.Message); }
    }

    // ---- sub-tabs ------------------------------------------------------------------------------

    private static MoonDepotBuilder? _builder;
    // The sub-tab chosen in each tab (by tab index).
    private static readonly Dictionary<int, string> Chosen = new();
    private static readonly List<(GameObject Chip, string Section)> Chips = new();
    private static bool _laying;

    internal static void Relayout()
    {
        try { if (_builder != null && !_builder.WasCollected && _builder.IsOpen) _builder.LayOutCatalog(); }
        catch (Exception e) { Log.Error("Laying out the build menu failed: " + e.Message); }
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.LayOutCatalog))]
    [HarmonyPostfix]
    private static void AfterLayOut(MoonDepotBuilder __instance, ref Il2CppSystem.Collections.Generic.List<MoonDepotBuilder.Card> __result)
    {
        if (_laying) return;
        _builder = __instance;
        try
        {
            _laying = true;
            var tab = __instance.selectedCategory;
            var shown = __result?.ToArray().ToList() ?? new List<MoonDepotBuilder.Card>();
            var sections = shown.Select(c => BuildMenu.SectionOf(c.entry.kind)).Where(s => s != null).Distinct().ToList();
            if (tab == 0 || tab == MoonDepotBuilder.StructuresTab || sections.Count == 0 || shown.Count == 0)
            {
                HideChips();
                return;
            }
            var names = new List<string> { BuildMenu.AllSection };
            if (shown.Any(c => BuildMenu.SectionOf(c.entry.kind) == null)) names.Add(BuildMenu.MainSection);
            names.AddRange(sections!);
            if (!Chosen.TryGetValue(tab, out var chosen) || !names.Contains(chosen)) Chosen[tab] = chosen = BuildMenu.AllSection;

            var parent = shown[0].rect.parent;
            var x = Pad;
            ShowChips(__instance, parent, names, chosen, ref x);

            bool InChosen(MoonDepotBuilder.Card c) =>
                chosen == BuildMenu.AllSection || (BuildMenu.SectionOf(c.entry.kind) ?? BuildMenu.MainSection) == chosen;
            var keep = new Il2CppSystem.Collections.Generic.List<MoonDepotBuilder.Card>();
            foreach (var card in shown)
            {
                var visible = InChosen(card);
                card.rect.gameObject.SetActive(visible);
                if (visible) keep.Add(card);
            }
            var placed = new Il2CppSystem.Collections.Generic.List<MoonDepotBuilder.Card>();
            var bottom = __instance.PlaceCards(keep, Pad + ChipHeight + Pad, placed);
            var content = __instance.catalogContent;
            if (content != null) content.sizeDelta = new Vector2(content.sizeDelta.x, Math.Max(bottom + Pad, 0f));
            __result = placed;
        }
        catch (Exception e) { Log.Error("Showing the build menu's sub-tabs failed: " + e); }
        finally { _laying = false; }
    }

    private static void HideChips()
    {
        foreach (var (chip, _) in Chips) if (chip != null && !chip.WasCollected) chip.SetActive(false);
    }

    private static void ShowChips(MoonDepotBuilder builder, Transform parent, List<string> names, string chosen, ref float x)
    {
        // Made from the menu's own tab buttons, so they look like the game's.
        if (Chips.Count > 0 && (Chips[0].Chip == null || Chips[0].Chip.WasCollected || Chips[0].Chip.transform.parent != parent)) Chips.Clear();
        var template = builder.tabViews != null && builder.tabViews.Count > 0 ? builder.tabViews[0] : null;
        if (template == null) return;
        for (var i = 0; i < Math.Max(names.Count, Chips.Count); i++)
        {
            if (i >= names.Count) { Chips[i].Chip.SetActive(false); continue; }
            if (i >= Chips.Count) Chips.Add((MakeChip(template, parent), ""));
            var chip = Chips[i].Chip;
            var name = names[i];
            Chips[i] = (chip, name);
            chip.SetActive(true);
            var text = chip.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.text = name.ToUpperInvariant();
                text.color = name == chosen ? Color.white : IdleText;
            }
            var body = chip.GetComponent<Image>();
            if (body != null) body.color = name == chosen ? Selected : Idle;
            var width = (text != null ? text.GetPreferredValues(text.text).x : 80f) + 40f;
            var rect = chip.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(width, ChipHeight);
            rect.anchoredPosition = new Vector2(x, -Pad);
            rect.localScale = Vector3.one;
            rect.SetAsLastSibling();
            x += width + ChipGap;
        }
    }

    private static GameObject MakeChip(MoonWorkshopKit.TabView template, Transform parent)
    {
        var chip = UnityEngine.Object.Instantiate(template.rect.gameObject, parent, false);
        chip.name = "MSML sub-tab";
        // Only the background and the label: no hover effects or other parts copied from the tab.
        foreach (var hover in chip.GetComponentsInChildren<MoonWorkshopHover>(true)) UnityEngine.Object.Destroy(hover);
        var button = chip.GetComponent<Button>() ?? chip.AddComponent<Button>();
        button.onClick.RemoveAllListeners();
        var go = chip;
        button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(() => Choose(go))));
        return chip;
    }

    private static void Choose(GameObject chip)
    {
        var builder = _builder;
        if (builder == null || builder.WasCollected) return;
        foreach (var (c, section) in Chips)
            if (c != null && c.Pointer == chip.Pointer) { Chosen[builder.selectedCategory] = section; break; }
        Relayout();
        if (builder.catalogScroll != null) builder.catalogScroll.verticalNormalizedPosition = 1f;
    }
}
