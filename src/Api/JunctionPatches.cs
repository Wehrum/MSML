using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Hooks the game for <see cref="Junctions"/>: junction type cards (choosing one selects the game's splitter or
/// merger, remembering the type), their details, giving built ones their type, the build HUD, removal, and the
/// per-frame work in <see cref="JunctionState"/>.
/// </summary>
internal static class JunctionPatches
{
    private const string CardPrefix = "msmlj:";
    private static bool _applied;
    private static bool _redirecting;

    /// <summary>The junction type being placed (with the game's splitter or merger), or null.</summary>
    internal static JunctionType? Active { get; private set; }

    internal static MoonDepotBuilder? Builder { get; private set; }

    internal static void Apply()
    {
        if (_applied) return;
        _applied = true;
        new Harmony("msml.api.junctions").PatchAll(typeof(JunctionPatches));
        BuildMenuPatches.Apply();
        JunctionState.AddHandlers();
    }

    internal static void AddCard(JunctionType type)
    {
        var entries = MoonDepotBuilder.baseStructures;
        var game = entries.FirstOrDefault(e => e.kind == type.BaseKind);
        var entry = new MoonDepotBuilder.CatalogEntry(type.Kind, type.Name, type.Category, CardPrefix + type.Id);
        var list = entries.ToList();
        var at = game == null ? list.Count : list.IndexOf(game) + 1 + Junctions.Types.Count(t => t.BaseKind == type.BaseKind && t.Kind < type.Kind);
        list.Insert(Math.Min(at, list.Count), entry);
        MoonDepotBuilder.baseStructures = new Il2CppReferenceArray<MoonDepotBuilder.CatalogEntry>(list.ToArray());
    }

    private static JunctionType? ByKind(int kind) => Junctions.Types.FirstOrDefault(t => t.Kind == kind);

    /// <summary>The junction type card chosen, while the game shows its splitter/merger card as chosen; else null.</summary>
    internal static int? ChosenCard(MoonDepotBuilder builder) =>
        Active != null && builder.selectedBuildId == Active.BaseKind ? Active.Kind : null;

    /// <summary>While a junction type's card is being chosen: its kind, if the game asks about its base kind for it.</summary>
    internal static int? RedirectingCard(int kind) =>
        _redirecting && Active != null && kind == Active.BaseKind ? Active.Kind : null;

    // The type being placed by this builder (the local player), or null.
    private static JunctionType? ActiveFor(MoonWalker? builder)
    {
        if (Active == null || builder == null || Builder == null || Builder.WasCollected) return null;
        if (Builder.walker == null || Builder.walker.Pointer != builder.Pointer || Builder.ConveyorMode) return null;
        return Builder.selectedStructure == Active.BaseKind ? Active : null;
    }

    // ---- build menu ----------------------------------------------------------------------------

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SelectStructure))]
    [HarmonyPrefix]
    private static void BeforeSelect(MoonDepotBuilder __instance, ref int kind)
    {
        Builder = __instance;
        if (_redirecting) return;
        // The game choosing the same splitter or merger again by itself (menu closed, e.g. to place another): the
        // type stays. Choosing a card in the menu changes it.
        if (Active != null && kind == Active.BaseKind && !__instance.IsOpen) return;
        Active = ByKind(kind);
        if (Active == null) return;
        kind = Active.BaseKind;
        _redirecting = true;
    }

    /// <summary>The junction type built last and when (its belts are drawn as its <see cref="JunctionType.RouteType"/>).</summary>
    internal static (JunctionType? Type, float At) LastBuilt { get; private set; }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SelectStructure))]
    [HarmonyFinalizer]
    private static void AfterSelect() => _redirecting = false;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.CanBuild))]
    [HarmonyPrefix]
    private static void BeforeCanBuild(ref int kind)
    {
        if (ByKind(kind) is { } type) kind = type.BaseKind;
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowPrice))]
    [HarmonyPrefix]
    private static void BeforeShowPrice(ref int kind)
    {
        if (ByKind(kind) is { } type) kind = type.BaseKind;
    }

    private static JunctionType? _describing;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowDetails))]
    [HarmonyPrefix]
    private static void BeforeShowDetails(ref int kind)
    {
        _describing = ByKind(kind) ?? (_redirecting && Active != null && kind == Active.BaseKind ? Active : null);
        if (_describing != null) kind = _describing.BaseKind;
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowDetails))]
    [HarmonyPostfix]
    private static void AfterShowDetails(MoonDepotBuilder __instance)
    {
        var type = _describing;
        _describing = null;
        if (type == null) return;
        try
        {
            if (__instance.detailName != null) __instance.detailName.text = type.Name;
            if (__instance.detailNote != null && type.Description.Length > 0) __instance.detailNote.text = type.Description;
            if (__instance.detailArt != null && CardSprite(type) is { } sprite) __instance.detailArt.sprite = sprite;
        }
        catch (Exception e) { Log.Error($"Describing {type.Name} failed: {e}"); }
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.CardArt))]
    [HarmonyPrefix]
    private static bool BeforeCardArt(ref string image, ref Sprite __result)
    {
        if (image == null || !image.StartsWith(CardPrefix, StringComparison.Ordinal)) return true;
        var type = Junctions.TypeById(image[CardPrefix.Length..]);
        if (type != null && CardSprite(type) is { } sprite) { __result = sprite; return false; }
        image = type?.BaseKind == JunctionType.Merger ? "merger" : "splitter";
        return true;
    }

    private static Sprite? CardSprite(JunctionType type)
    {
        if (type.CardSprite != null) return type.CardSprite;
        if (type.CardPath == null || !File.Exists(type.CardPath)) return null;
        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = type.Name + " card", wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(type.CardPath))) return null;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            type.CardSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            type.CardSprite.name = type.Name;
            type.CardSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return type.CardSprite;
        }
        catch (Exception e) { Log.Error($"Loading the card picture for {type.Name} failed: {e.Message}"); return null; }
    }

    // The build HUD names the type being placed, with its picture.
    [HarmonyPatch(typeof(MoonPlayerHud), nameof(MoonPlayerHud.RefreshBuildView))]
    [HarmonyPostfix]
    private static void AfterRefreshBuildView(MoonPlayerHud __instance)
    {
        try
        {
            if (Builder == null || Builder.WasCollected || Builder.ConveyorMode || Active is not { } type || Builder.selectedStructure != type.BaseKind) return;
            var name = type.Name.ToUpperInvariant();
            if (__instance.shownHeading != name)
            {
                __instance.shownHeading = name;
                __instance.LayOutBuildText();
            }
            if (__instance.buildArt != null && CardSprite(type) is { } sprite && __instance.buildArt.sprite != sprite) __instance.buildArt.sprite = sprite;
        }
        catch (Exception e) { Log.Error("Naming the junction type in the build HUD failed: " + e); }
    }

    // ---- building and removing -----------------------------------------------------------------

    // Structure ids aren't handed out in order: the new one is whichever wasn't there before the build. The type is
    // taken before building too: a built splitter or merger switches the builder to drawing its belts straight away.
    // Placed on open ground, the game may only build it a little later, once its site is checked: placements are kept
    // for a while, and the splitter or merger that appears there gets the type.
    private static HashSet<int>? _depotsBefore;
    private static JunctionType? _building;
    private static readonly List<(JunctionType Type, Vector3 At, float Since)> Placing = new();
    private static HashSet<int>? _known;
    private const float PlacingGrace = 15f;

    /// <summary>A junction type placed in the last few seconds and not built yet, or null.</summary>
    internal static JunctionType? Pending => Placing.Count > 0 && Time.unscaledTime - Placing[^1].Since < 5f ? Placing[^1].Type : null;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildStructure))]
    [HarmonyPrefix]
    private static void BeforeBuildStructure(MoonWorld __instance, MoonWalker builder, MoonWorld.StructurePlan plan)
    {
        _depotsBefore = null;
        _building = null;
        try
        {
                if (ActiveFor(builder) is not { } type || plan == null || plan.kind != type.BaseKind) return;
                _building = type;
                LastBuilt = (type, Time.unscaledTime);
                _depotsBefore = new HashSet<int>();
                foreach (var id in __instance.depots.Keys) _depotsBefore.Add(id);
                Placing.RemoveAll(p => (p.At - plan.position).sqrMagnitude < 0.01f);
                Placing.Add((type, plan.position, Time.unscaledTime));
                _known ??= new HashSet<int>(_depotsBefore);
        }
        catch (Exception e) { Log.Error("Preparing to type a splitter/merger failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildStructure))]
    [HarmonyPostfix]
    private static void AfterBuildStructure(MoonWorld __instance, bool __result)
    {
        var before = _depotsBefore;
        var type = _building;
        _depotsBefore = null;
        _building = null;
        try
        {
                if (before == null || type == null) return;
                foreach (var id in __instance.depots.Keys)
                    if (!before.Contains(id)) Built(__instance, id);
        }
        catch (Exception e) { Log.Error("Giving a built splitter/merger its type failed: " + e); }
    }

    // Placed on open ground (not cut into or snapped to a belt), the game builds it as a depot instead.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildDepot))]
    [HarmonyPrefix]
    private static void BeforeBuildDepot(MoonWorld __instance, MoonWalker builder, Vector3 direction, float yaw, int kind)
    {
        _depotsBefore = null;
        _building = null;
        try
        {
                if (ActiveFor(builder) is not { } type || kind != type.BaseKind) return;
                _building = type;
                LastBuilt = (type, Time.unscaledTime);
                _depotsBefore = new HashSet<int>();
                foreach (var id in __instance.depots.Keys) _depotsBefore.Add(id);
                __instance.DepotPose(direction, yaw, out var at, out _, kind);
                Placing.RemoveAll(p => (p.At - at).sqrMagnitude < 0.01f);
                Placing.Add((type, at, Time.unscaledTime));
                _known ??= new HashSet<int>(_depotsBefore);
        }
        catch (Exception e) { Log.Error("Preparing to type a splitter/merger failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildDepot))]
    [HarmonyPostfix]
    private static void AfterBuildDepot(MoonWorld __instance) => AfterBuildStructure(__instance, true);

    // A splitter or merger that wasn't there before: the type placed there, if any.
    private static void Built(MoonWorld w, int id)
    {
        _known?.Add(id);
        var d = w.depots[id];
        var at = new Vector3(d.x, d.y, d.z);
        for (var i = Placing.Count - 1; i >= 0; i--)
        {
                var (type, place, _) = Placing[i];
                if (d.kind != type.BaseKind || (at - place).sqrMagnitude > 2.5f * 2.5f) continue;
                Placing.RemoveAt(i);
                LastBuilt = (type, Time.unscaledTime);
                JunctionState.SetType(id, type, true);
                return;
        }
    }

    // Every frame while placements are waiting: anything new built?
    internal static void WatchPlacements(MoonWorld w)
    {
        if (Placing.Count == 0) return;
        var now = Time.unscaledTime;
        Placing.RemoveAll(p => now - p.Since > PlacingGrace);
        if (_known == null || w.depots.Count == _known.Count) { if (Placing.Count == 0) _known = null; return; }
        var fresh = new List<int>();
        foreach (var id in w.depots.Keys) if (!_known.Contains(id)) fresh.Add(id);
        _known = null;
        if (Placing.Count > 0) { _known = new HashSet<int>(); foreach (var id in w.depots.Keys) _known.Add(id); }
        foreach (var id in fresh) Built(w, id);
    }

    // Removing one: the game's dismantle effect takes the type's parts too.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.DismantleView))]
    [HarmonyPrefix]
    private static void BeforeDismantleView(GameObject view)
    {
        try { if (view != null) JunctionState.HandOver(view); }
        catch (Exception e) { Log.Error("Dismantling a junction type's parts failed: " + e); }
    }

    // Aiming at one to remove it: the red highlight is drawn from the type's parts (the model is hidden).
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SetRemoveTarget))]
    [HarmonyPrefix]
    private static void BeforeRemoveTarget(ref GameObject view)
    {
        if (view != null && JunctionState.PartsFor(view) is { } parts) view = parts;
    }

    // ---- belts to sunk ports (PortSink) --------------------------------------------------------

    // Every sunk port: where the game has it and where its mouth is drawn. Worked out from the world's update (not
    // while the game is aiming or drawing markers: nothing of the game's is called from inside those).
    private static readonly List<(int Structure, Vector3 Port, Vector3 Mouth)> Mouths = new();
    private static float _nextMouths;
    private const float MouthReach = 0.5f;

    private static void RefreshMouths()
    {
        if (Time.unscaledTime < _nextMouths) return;
        _nextMouths = Time.unscaledTime + 0.5f;
        Mouths.Clear();
        foreach (var type in Junctions.Types)
        {
            if (type.PortSink <= 0f) continue;
            foreach (var structure in Junctions.OfType(type))
            {
                if (!Junctions.Pose(structure, out var position, out var rotation)) continue;
                var down = VMath.Mul(VMath.Rotate(rotation, Vector3.up), -type.PortSink);
                foreach (var local in Junctions.InputsOf(type.BaseKind).Concat(Junctions.OutputsOf(type.BaseKind)))
                {
                    var port = VMath.Add(position, VMath.Rotate(rotation, local));
                    Mouths.Add((structure, port, VMath.Add(port, down)));
                }
            }
        }
    }

    // Aiming a belt right at a sunk mouth snaps to its port, as aiming at the port itself would (the game only snaps to
    // ports up in the air). Only there: anywhere else the game's own answer stands, untouched.
    private static bool _reaiming;

    [HarmonyPatch(typeof(MoonDepotBuilder), "Aim")]
    [HarmonyPostfix]
    private static void AfterAim(MoonDepotBuilder __instance, Ray ray, bool hit, RaycastHit aim, MoonConveyorKit kit,
        ref string shape, ref string height, ref MoonDepotBuilder.Anchor __result)
    {
        if (_reaiming || !hit || Mouths.Count == 0 || (__result != null && __result.snapped)) return;
        var point = aim.point;
        var best = MouthReach * MouthReach;
        var chosen = -1;
        for (var i = 0; i < Mouths.Count; i++)
        {
            var d = VMath.SqrDistance(point, Mouths[i].Mouth);
            if (d < best) { best = d; chosen = i; }
        }
        if (chosen < 0) return;
        var (structure, port, _) = Mouths[chosen];
        try
        {
            _reaiming = true;
            var again = aim;
            again.point = port;
            var anchor = __instance.Aim(new Ray(ray.origin, VMath.Normalize(VMath.Sub(port, ray.origin))), true, again, kit, out var s2, out var h2);
            if (anchor != null && anchor.snapped && anchor.structure == structure)
            {
                __result = anchor;
                shape = s2;
                height = h2;
            }
        }
        catch (Exception e) { Log.Error("Snapping to a sunk junction port failed: " + e); }
        finally { _reaiming = false; }
    }

    // Port markers at the sunk mouths.
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowMarker))]
    [HarmonyPrefix]
    private static void BeforeShowMarker(ref Vector3 position)
    {
        foreach (var (_, port, mouth) in Mouths)
            if (VMath.SqrDistance(port, position) < 0.0025f) { position = mouth; return; }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.Update))]
    [HarmonyPrefix]
    private static void BeforeWorldUpdate(MoonWorld __instance)
    {
        try { JunctionState.Tick(__instance); WatchPlacements(__instance); RefreshMouths(); }
        catch (Exception e) { Log.Error("Junction update failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterWorldDestroyed() { JunctionState.Clear(); Mouths.Clear(); }
}
