using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Hooks the game for <see cref="Belts"/>: belt type cards in the build menu (choosing one enters the game's own belt
/// mode, remembering the type), their price and length, giving built belts their type, refunds, keeping the type when
/// a splitter or merger cuts a belt, styling views, and the per-frame work in <see cref="BeltState"/>.
/// </summary>
internal static class BeltPatches
{
    private const string CardPrefix = "msml:";
    private static bool _applied;

    // The belt type being built with, and the local player's builder.
    private static BeltType? _active;
    private static MoonDepotBuilder? _builder;
    private static bool _redirecting;

    internal static void Apply()
    {
        if (_applied) return;
        _applied = true;
        new Harmony("msml.api.belts").PatchAll(typeof(BeltPatches));
        MoonData.Apply();
        CrewMessages.Patches.Apply();
        Crew.Apply();
        BeltState.AddHandlers();
    }

    internal static void AddCard(BeltType type)
    {
        var entries = MoonDepotBuilder.baseStructures;
        var conveyor = entries.FirstOrDefault(e => e.kind == MoonDepotBuilder.ConveyorKind);
        var entry = new MoonDepotBuilder.CatalogEntry(type.Kind, type.Name, type.Category, CardPrefix + type.Id);
        var list = entries.ToList();
        var at = conveyor == null ? list.Count : list.IndexOf(conveyor) + 1 + Belts.Types.Count(t => t.Kind < type.Kind);
        list.Insert(Math.Min(at, list.Count), entry);
        MoonDepotBuilder.baseStructures = new Il2CppReferenceArray<MoonDepotBuilder.CatalogEntry>(list.ToArray());
    }

    private static BeltType? ByKind(int kind) => Belts.Types.FirstOrDefault(t => t.Kind == kind);

    // The game's own longest belt (its conveyor kit's), raised while building a type allowed to be longer.
    private static MoonConveyorKit? _kit;
    private static float _kitMax = -1f;

    internal static void ApplyKitLength()
    {
        if (_kit == null || _kit.WasCollected)
        {
            _kit = Resources.FindObjectsOfTypeAll<MoonConveyorKit>().FirstOrDefault();
            if (_kit == null) return;
            _kitMax = _kit.maxLength;
        }
        var type = _builder != null && !_builder.WasCollected && _builder.ConveyorMode ? _active : null;
        var wanted = type?.MaxLength is { } max && max > _kitMax ? max : _kitMax;
        if (Math.Abs(_kit.maxLength - wanted) > 0.001f) _kit.maxLength = wanted;
    }

    private static BeltType? ActiveFor(MoonWalker? builder)
    {
        if (_inserting != null) return _inserting;
        if (_active == null || builder == null || _builder == null || _builder.WasCollected) return null;
        if (!_builder.ConveyorMode || _builder.walker == null || _builder.walker.Pointer != builder.Pointer) return null;
        return _active;
    }

    // ---- building a piece into a belt (InsertsInto) --------------------------------------------

    // While inserting: the type being built (the piece, or the cut belt's halves), and that it's free.
    private static BeltType? _inserting;
    private static bool _insertFree;
    // The belt the current plan would be built into, and where along it (start, end distance).
    private static (int Belt, float From, float To)? _insertTarget;
    private const float InsertReach = 1.6f;

    // The plan's start is aimed at (or beside) a belt the type goes into: lay the piece along that belt there.
    private static bool PlanInsert(MoonWorld w, BeltType type, float length, MoonWorld.ConveyorPlan plan)
    {
        _insertTarget = null;
        if (type.InsertsInto == null || type.InsertsInto.Count == 0) return false;
        var aim = plan.start;
        int best = 0; float bestDistance = InsertReach, along = 0f;
        foreach (var into in type.InsertsInto)
            foreach (var belt in Belts.OfType(into))
            {
                var path = Belts.PathOf(belt);
                if (path == null || path.Length < length) continue;
                var s = MoonWorld.NearestDistance(path, aim);
                var d = (path.Point(s) - aim).magnitude;
                if (d < bestDistance) { best = belt; bestDistance = d; along = s; }
            }
        if (best == 0) return false;
        var target = Belts.PathOf(best)!;
        var min = MinLength();
        // Centred on the aimed point; snapped to an end when what's left there would be too short for a belt.
        var from = Mathf.Clamp(along - length / 2f, 0f, target.Length - length);
        if (from < min) from = 0f;
        if (target.Length - (from + length) < min) from = target.Length - length;
        if (from > 0f && from < min) return false;   // too short for a piece on both sides
        var to = from + length;
        target.Frame(from, out var a, out var aForward, out _, out var aUp);
        target.Frame(to, out var b, out var bForward, out _, out var bUp);
        plan.start = a; plan.startDirection = aForward; plan.startUp = aUp;
        plan.end = b; plan.endDirection = bForward; plan.endUp = bUp;
        plan.startBelt = 0; plan.startStructure = 0; plan.endBelt = 0; plan.endStructure = 0;
        _insertTarget = (best, from, to);
        return true;
    }

    private static float MinLength()
    {
        var kit = _kit ?? Resources.FindObjectsOfTypeAll<MoonConveyorKit>().FirstOrDefault();
        return kit != null ? kit.minLength : 0.75f;
    }

    /// <summary>Cuts the target belt and builds the piece between its halves. Returns the piece's id, or 0.</summary>
    private static int Insert(MoonWorld w, MoonWalker builder, BeltType type, (int Belt, float From, float To) target, out string reason)
    {
        reason = "";
        var old = w.conveyors.ContainsKey(target.Belt) ? w.conveyors[target.Belt] : null;
        var path = Belts.PathOf(target.Belt);
        var halves = Belts.TypeOf(target.Belt);
        if (old == null || path == null || halves == null) { reason = "That belt is gone."; return 0; }
        var oldStart = (old.start, old.startDirection, path.Up(old.start));
        var oldEnd = (old.end, old.endDirection, path.Up(old.end));
        w.Feeders(old.start, out var feedBelt, out var feedStructure);
        w.Takers(old.end, out var takeBelt, out var takeStructure);

        MoonWorld.ConveyorPlan Plan(float from, float to, int startBelt, int startStructure, int endBelt, int endStructure)
        {
            path.Frame(from, out var a, out var af, out _, out var au);
            path.Frame(to, out var b, out var bf, out _, out var bu);
            var p = new MoonWorld.ConveyorPlan();
            p.start = from <= 0f ? oldStart.Item1 : a; p.startDirection = from <= 0f ? oldStart.Item2 : af; p.startUp = au;
            p.end = to >= path.Length ? oldEnd.Item1 : b; p.endDirection = to >= path.Length ? oldEnd.Item2 : bf; p.endUp = bu;
            p.startBelt = startBelt; p.startStructure = startStructure; p.endBelt = endBelt; p.endStructure = endStructure;
            return p;
        }

        var length = path.Length;
        var built = new List<int>();
        try
        {
            _insertFree = true;
            if (!w.TryRemoveConveyor(builder, target.Belt, out reason)) return 0;
            int before = 0, piece = 0, after;
            if (target.From > 0f)
            {
                _inserting = halves;
                if (!w.TryBuildConveyor(builder, Plan(0f, target.From, feedBelt, feedStructure, 0, 0), out reason, out before)) throw new InvalidOperationException(reason);
                built.Add(before);
            }
            _inserting = type;
            _insertFree = false;
            if (!w.TryBuildConveyor(builder, Plan(target.From, target.To, before != 0 ? before : feedBelt, before != 0 ? 0 : feedStructure, 0, 0), out reason, out piece))
                throw new InvalidOperationException(reason);
            built.Add(piece);
            _insertFree = true;
            if (target.To < length)
            {
                _inserting = halves;
                if (!w.TryBuildConveyor(builder, Plan(target.To, length, piece, 0, takeBelt, takeStructure), out reason, out after)) throw new InvalidOperationException(reason);
                built.Add(after);
            }
            return piece;
        }
        catch (InvalidOperationException)
        {
            // Put the belt back as it was.
            foreach (var id in built) w.TryRemoveConveyor(builder, id, out _);
            _inserting = halves;
            _insertFree = true;
            var whole = Plan(0f, length, feedBelt, feedStructure, takeBelt, takeStructure);
            if (!w.TryBuildConveyor(builder, whole, out var again, out _)) Log.Error($"Putting a {halves.Name} back after a failed insert failed: {again}");
            return 0;
        }
        finally
        {
            _inserting = null;
            _insertFree = false;
        }
    }

    // ---- build menu ----------------------------------------------------------------------------

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SelectStructure))]
    [HarmonyPrefix]
    private static void BeforeSelect(MoonDepotBuilder __instance, ref int kind)
    {
        _builder = __instance;
        if (_redirecting) return;
        _active = ByKind(kind);
        if (_active == null) return;
        kind = MoonDepotBuilder.ConveyorKind;
        _redirecting = true;
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SelectStructure))]
    [HarmonyFinalizer]
    private static void AfterSelect() => _redirecting = false;

    // Belt mode also starts without choosing a card (the Belts key, or belts after placing a machine): plain belts.
    private static bool _placing;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.BeginPlacement))]
    [HarmonyPrefix]
    private static void BeforePlacement() => _placing = true;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.BeginPlacement))]
    [HarmonyFinalizer]
    private static void AfterPlacement() => _placing = false;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.BeginConveyor))]
    [HarmonyPrefix]
    private static void BeforeBeginConveyor()
    {
        if (!_placing) _active = null;
    }

    // The game checks and describes the card's kind; a belt type is a belt to it.
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.CanBuild))]
    [HarmonyPrefix]
    private static void BeforeCanBuild(ref int kind)
    {
        if (ByKind(kind) != null) kind = MoonDepotBuilder.ConveyorKind;
    }

    // Price tags (on cards and details): the type's price.
    private static BeltType? _pricing;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowPrice))]
    [HarmonyPrefix]
    private static void BeforeShowPrice(ref int kind)
    {
        _pricing = ByKind(kind);
        if (_pricing != null) kind = MoonDepotBuilder.ConveyorKind;
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowPrice))]
    [HarmonyPostfix]
    private static void AfterShowPrice(TMP_Text text)
    {
        var type = _pricing;
        _pricing = null;
        if (type == null || text == null) return;
        text.text = type.PricePerPiece is { } piece ? $"${piece:N0} each" : $"${PricePerMetre() * type.PriceScale:0.00} / m";
    }

    // Details of a belt type's card (hovered, or chosen): the belt's, with the type's name, picture, price and text.
    private static BeltType? _describing;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowDetails))]
    [HarmonyPrefix]
    private static void BeforeShowDetails(ref int kind)
    {
        _describing = ByKind(kind) ?? (_redirecting && kind == MoonDepotBuilder.ConveyorKind ? _active : null);
        if (_describing != null) kind = MoonDepotBuilder.ConveyorKind;
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
            if (__instance.detailNote != null) __instance.detailNote.text = type.Description;
            var price = type.PricePerPiece is { } piece ? $"${piece:N0} each" : $"${PricePerMetre() * type.PriceScale:0.00} / m";
            if (__instance.detailPrice != null) __instance.detailPrice.text = price;
            if (__instance.detailTagText != null) __instance.detailTagText.text = price;
            if (type.MaxLength is { } max && __instance.detailFootprint != null) __instance.detailFootprint.text = $"Up to {max:0.#} m long";
            if (__instance.detailArt != null && CardSprite(type) is { } sprite) __instance.detailArt.sprite = sprite;
        }
        catch (Exception e) { Log.Error($"Describing {type.Name} failed: {e}"); }
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.CardArt))]
    [HarmonyPrefix]
    private static bool BeforeCardArt(ref string image, ref Sprite __result)
    {
        if (image == null || !image.StartsWith(CardPrefix, StringComparison.Ordinal)) return true;
        var type = Belts.TypeById(image[CardPrefix.Length..]);
        if (type != null && CardSprite(type) is { } sprite) { __result = sprite; return false; }
        image = "conveyor_belt";
        return true;
    }

    private static Sprite? CardSprite(BeltType type)
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

    // ---- price, length, building ---------------------------------------------------------------

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CheckConveyor), new[] { typeof(MoonWalker), typeof(MoonWorld.ConveyorPlan), typeof(string), typeof(MoonConveyorPath), typeof(Il2CppSystem.Decimal), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPrefix]
    private static void BeforeCheck(MoonWalker builder, MoonWorld.ConveyorPlan plan)
    {
        if (_inserting == null) _insertTarget = null;
        // A fixed-length piece is laid straight from whichever end is anchored: on from its start, or back from its
        // end when only the end is snapped to something (a machine's input, a belt).
        if (ActiveFor(builder) is not { FixedLength: { } length } type || plan == null) return;
        if (_inserting == null && Moon.World is { } world && PlanInsert(world, type, length, plan)) return;
        var startSnapped = plan.startBelt != 0 || plan.startStructure != 0;
        var endSnapped = plan.endBelt != 0 || plan.endStructure != 0;
        if (endSnapped && !startSnapped)
        {
            var back = plan.endDirection.sqrMagnitude > 1e-4f ? plan.endDirection.normalized : (plan.end - plan.start).normalized;
            if (back.sqrMagnitude < 0.5f) return;
            plan.start = plan.end - back * length;
            plan.startDirection = back;
            plan.startUp = plan.endUp;
            return;
        }
        var direction = plan.startDirection.sqrMagnitude > 1e-4f ? plan.startDirection : plan.end - plan.start;
        if (direction.sqrMagnitude < 1e-6f) return;
        direction.Normalize();
        plan.end = plan.start + direction * length;
        plan.endDirection = direction;
        plan.endUp = plan.startUp;
        plan.endBelt = 0;
        plan.endStructure = 0;
    }

    // Fixed-length pieces are never split into a route of several pieces (each would be a whole piece).
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.RouteConveyor))]
    [HarmonyPrefix]
    private static bool BeforeRoute(MoonWalker builder, ref bool __result)
    {
        if (ActiveFor(builder)?.FixedLength == null) return true;
        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyorRoute))]
    [HarmonyPrefix]
    private static bool BeforeBuildRouteFixed(MoonWalker builder, Il2CppSystem.Collections.Generic.IList<MoonWorld.ConveyorPlan> plans, ref string reason, ref bool __result)
    {
        if (ActiveFor(builder) is not { FixedLength: { } length } type || plans == null || plans.Cast<Il2CppSystem.Collections.Generic.ICollection<MoonWorld.ConveyorPlan>>().Count <= 1) return true;
        reason = $"A {type.Name} is {length:0.#} m long: start it where there's room straight ahead.";
        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CheckConveyor), new[] { typeof(MoonWalker), typeof(MoonWorld.ConveyorPlan), typeof(string), typeof(MoonConveyorPath), typeof(Il2CppSystem.Decimal), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPostfix]
    private static void AfterCheck(MoonWorld __instance, MoonWalker builder, MoonWorld.ConveyorPlan plan, ref string reason, MoonConveyorPath path, ref Il2CppSystem.Decimal price, ref bool __result)
    {
        try
        {
            if (ActiveFor(builder) is not { } type) return;
            if (_inserting == null && _insertTarget is { } into)
            {
                // Built into a belt: it overlaps that belt by design; the cut makes room.
                __result = true;
                reason = $"Builds into the {Belts.TypeOf(into.Belt)?.Name ?? "belt"}";
            }
            if (_insertFree) { price = new Il2CppSystem.Decimal(0); return; }
            if (__result && type.NoChaining && plan != null && Chained(__instance, type, plan))
            {
                __result = false;
                reason = $"Two {type.Name}s can't be joined directly: put something between them.";
            }
            if (__result && type.MaxLength is { } max && path != null && path.Length > max + 0.01f)
            {
                __result = false;
                reason = $"{type.Name} pieces can be at most {max:0.#} m long.";
            }
            if (__result && type.FixedLength is { } fixedLength && path != null && Math.Abs(path.Length - fixedLength) > 0.05f)
            {
                __result = false;
                reason = $"A {type.Name} is {fixedLength:0.#} m long: start it where there's room straight ahead.";
            }
            if (__result && type.OnGround && path != null && !NearGround(type, path))
            {
                __result = false;
                reason = $"{type.Name}s run along the ground: keep it within {type.MaxGroundGap:0.#} m of the ground.";
            }
            if (type.PricePerPiece is { } piece) price = new Il2CppSystem.Decimal(Math.Round(piece, 2));
            else if (Math.Abs(type.PriceScale - 1) > 1e-9) price = Scale(price, type.PriceScale);
        }
        catch (Exception e) { Log.Error("Checking a belt type's plan failed: " + e); }
    }

    // The planned piece would start at the end of, or end at the start of, a piece of the same type.
    private static bool Chained(MoonWorld world, BeltType type, MoonWorld.ConveyorPlan plan)
    {
        if (plan.startBelt != 0 && Belts.TypeOf(plan.startBelt) == type) return true;
        if (plan.endBelt != 0 && Belts.TypeOf(plan.endBelt) == type) return true;
        world.Feeders(plan.start, out var before, out _);
        world.Takers(plan.end, out var after, out _);
        return (before != 0 && Belts.TypeOf(before) == type) || (after != 0 && Belts.TypeOf(after) == type);
    }

    // Within the type's ground gap everywhere except near the ends (which may rise to machines' ports).
    private static bool NearGround(BeltType type, MoonConveyorPath path)
    {
        var length = path.Length;
        for (var d = Math.Min(type.GroundRamp, length / 2f); d <= length - Math.Min(type.GroundRamp, length / 2f) + 0.001f; d += 1f)
            if (!BeltLooks.GroundBelow(path, d, 0.05f, out var drop, type.MaxGroundGap + 0.1f) || drop - 0.05f > type.MaxGroundGap) return false;
        return true;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyor))]
    [HarmonyPostfix]
    private static void AfterBuild(MoonWalker builder, int id, bool __result)
    {
        if (__result && id != 0 && ActiveFor(builder) is { } type) BeltState.SetType(id, type, true);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyorRoute))]
    [HarmonyPostfix]
    private static void AfterBuildRoute(MoonWalker builder, Il2CppSystem.Collections.Generic.List<int> ids, bool __result)
    {
        if (!__result || ids == null || ActiveFor(builder) is not { } type) return;
        foreach (var id in ids) if (id != 0) BeltState.SetType(id, type, true);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ConveyorRefund))]
    [HarmonyPostfix]
    private static void AfterRefund(int id, ref Il2CppSystem.Decimal __result)
    {
        if (_insertFree) { __result = new Il2CppSystem.Decimal(0); return; }   // cut for an insert: rebuilt for free
        if (Belts.TypeOf(id) is not { } type) return;
        if (type.PricePerPiece is { } piece)
        {
            // The game refunds part of a belt's price; refund the same part of the piece's.
            var length = Belts.PathOf(id)?.Length ?? 0f;
            var plain = length * PricePerMetre();
            var part = plain > 0 ? Math.Clamp(ToDouble(__result) / plain, 0, 1) : 0.75;
            __result = new Il2CppSystem.Decimal(Math.Round(piece * part, 2));
        }
        else if (Math.Abs(type.PriceScale - 1) > 1e-9) __result = Scale(__result, type.PriceScale);
    }

    private static float _pricePerMetre;

    private static float PricePerMetre()
    {
        if (_pricePerMetre > 0) return _pricePerMetre;
        foreach (var kit in Resources.FindObjectsOfTypeAll<MoonConveyorKit>()) { _pricePerMetre = kit.pricePerMetre; break; }
        return _pricePerMetre > 0 ? _pricePerMetre : 0.5f;
    }

    private static double ToDouble(Il2CppSystem.Decimal value) =>
        double.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryRemoveConveyor))]
    [HarmonyPostfix]
    private static void AfterRemove(MoonWorld __instance, int id, bool __result)
    {
        var riders = _ridersOfRemoved;
        _ridersOfRemoved = null;
        if (!__result) return;
        BeltState.Forget(id);
        if (riders == null) return;
        foreach (var capsule in riders)
            if (__instance.canisters.ContainsKey(capsule)) __instance.RemoveCanister(capsule);
    }

    private static Il2CppSystem.Decimal Scale(Il2CppSystem.Decimal value, double scale) =>
        new(Math.Round(ToDouble(value) * scale, 2));

    // ---- a splitter or merger cutting a belt: both halves keep its type ------------------------

    private static List<(int Id, BeltType Type, Vector3 Start, Vector3 End)>? _cutting;
    private static HashSet<int>? _before;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CutBelt))]
    [HarmonyPrefix]
    private static void BeforeCut(MoonWorld __instance)
    {
        try
        {
            _cutting = new();
            _before = new HashSet<int>(BeltState.Keys(__instance.conveyors));
            foreach (var type in Belts.Types)
                foreach (var id in Belts.OfType(type))
                    if (__instance.conveyors.ContainsKey(id))
                    {
                        var c = __instance.conveyors[id];
                        _cutting.Add((id, type, c.start, c.end));
                    }
        }
        catch (Exception e) { Log.Error("Preparing for a belt cut failed: " + e); _cutting = null; }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CutBelt))]
    [HarmonyPostfix]
    private static void AfterCut(MoonWorld __instance)
    {
        try
        {
            if (_cutting == null || _before == null || _cutting.Count == 0) return;
            foreach (var id in BeltState.Keys(__instance.conveyors))
            {
                if (_before.Contains(id)) continue;
                var c = __instance.conveyors[id];
                foreach (var old in _cutting)
                {
                    if (__instance.conveyors.ContainsKey(old.Id)) continue;
                    if ((c.start - old.Start).sqrMagnitude < 0.01f || (c.end - old.End).sqrMagnitude < 0.01f)
                    {
                        BeltState.SetType(id, old.Type, true);
                        break;
                    }
                }
            }
        }
        catch (Exception e) { Log.Error("Keeping belt types after a cut failed: " + e); }
        finally { _cutting = null; _before = null; }
    }

    // ---- views and per-frame work --------------------------------------------------------------

    [HarmonyPatch(typeof(MoonConveyorNetwork), nameof(MoonConveyorNetwork.Spawn))]
    [HarmonyPostfix]
    private static void AfterSpawn(MoonConveyorSnapshot belt, bool __result)
    {
        // Also for plain belts: a view the game reuses may still have a hidden belt from a typed one.
        if (__result && belt != null) BeltState.Restyle(belt.id);
    }

    // Removing a belt: the game's dismantle effect (and its sound) plays on the type's parts too, and a hidden belt
    // stays hidden.
    private static int _dismantling;

    [HarmonyPatch(typeof(MoonConveyorNetwork), nameof(MoonConveyorNetwork.Dismantled))]
    [HarmonyPrefix]
    private static void BeforeDismantled(MoonConveyorSnapshot belt) => _dismantling = belt?.id ?? 0;

    [HarmonyPatch(typeof(MoonConveyorNetwork), nameof(MoonConveyorNetwork.Dismantled))]
    [HarmonyPostfix]
    private static void AfterDismantled(GameObject __result)
    {
        var belt = _dismantling;
        _dismantling = 0;
        if (belt == 0) return;
        try
        {
            if (__result == null) { BeltState.DropStyle(belt); return; }
            if (Belts.TypeOf(belt)?.HideBelt == true)
                foreach (var r in __result.GetComponents<Renderer>()) r.enabled = false;
            BeltState.HandOverStyle(belt, __result);
        }
        catch (Exception e) { Log.Error("Dismantling a belt type's parts failed: " + e); BeltState.DropStyle(belt); }
    }

    // Aiming at a belt to remove it: the red highlight is drawn from the type's parts (the belt itself is hidden).
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SetRemoveTarget))]
    [HarmonyPrefix]
    private static void BeforeRemoveTarget(ref GameObject view)
    {
        if (view != null && BeltState.PartsFor(view) is { } parts) view = parts;
    }

    // Standing on a pass-through belt doesn't carry you along.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.BeltSurfaceVelocity))]
    [HarmonyPrefix]
    private static bool BeforeSurfaceVelocity(MoonWorld __instance, Collider collider, ref Vector3 velocity, ref bool __result)
    {
        var belt = collider == null ? 0 : __instance.ConveyorViews?.BeltAt(collider) ?? 0;
        if (belt == 0 || Belts.TypeOf(belt)?.PassThrough != true) return true;
        velocity = Vector3.zero;
        __result = false;
        return false;
    }

    // The build HUD names the belt type being built, with its picture.
    [HarmonyPatch(typeof(MoonPlayerHud), nameof(MoonPlayerHud.RefreshBuildView))]
    [HarmonyPostfix]
    private static void AfterRefreshBuildView(MoonPlayerHud __instance)
    {
        try
        {
            if (_builder == null || _builder.WasCollected || !_builder.ConveyorMode || _active is not { } type) return;
            var name = type.Name.ToUpperInvariant();
            if (__instance.shownHeading != name)
            {
                __instance.shownHeading = name;
                __instance.LayOutBuildText();
            }
            if (__instance.buildArt != null && CardSprite(type) is { } sprite && __instance.buildArt.sprite != sprite) __instance.buildArt.sprite = sprite;
        }
        catch (Exception e) { Log.Error("Naming the belt type in the build HUD failed: " + e); }
    }

    // Capsules on a removed belt of a type that deletes them.
    private static List<int>? _ridersOfRemoved;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryRemoveConveyor))]
    [HarmonyPrefix]
    private static void BeforeRemove(int id)
    {
        _ridersOfRemoved = Belts.TypeOf(id)?.DeleteRidersOnRemove == true ? Belts.Riders(id).Select(r => r.Id).ToList() : null;
    }

    // Building one: no reveal animation of the (hidden) belt.
    private static bool _buildingHidden;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyor))]
    [HarmonyPrefix]
    private static void BeforeBuild(MoonWalker builder) => _buildingHidden = ActiveFor(builder)?.HideBelt == true;

    // Building a piece aimed at a belt it goes into: cut the belt and build it in between.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyor))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool BeforeBuildInsert(MoonWorld __instance, MoonWalker builder, MoonWorld.ConveyorPlan plan, ref string reason, ref int id, ref bool __result)
    {
        if (_inserting != null || ActiveFor(builder) is not { FixedLength: { } length, InsertsInto: { Count: > 0 } } type || plan == null) return true;
        if (!PlanInsert(__instance, type, length, plan) || _insertTarget is not { } target) return true;
        // Check the piece itself (price, what it's next to) before cutting anything.
        if (!__instance.CheckConveyor(builder, plan, out reason, out _, out _))
        {
            _insertTarget = null;
            id = 0;
            __result = false;
            return false;
        }
        _insertTarget = null;
        id = Insert(__instance, builder, type, target, out reason);
        __result = id != 0;
        if (__result) reason = $"{type.Name} built in.";
        return false;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyorRoute))]
    [HarmonyPrefix]
    private static void BeforeBuildRoute(MoonWalker builder) => _buildingHidden = ActiveFor(builder)?.HideBelt == true;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.RevealBuilt))]
    [HarmonyPrefix]
    private static bool BeforeReveal(int belt) => !(_buildingHidden || Belts.TypeOf(belt)?.HideBelt == true);

    // The path a piece would take built into the belt its preview starts on (or null).
    private static MoonConveyorPath? InsertPreviewPath(BeltType type, MoonConveyorPath preview)
    {
        if (type.FixedLength is not { } length || type.InsertsInto is not { Count: > 0 } || Moon.World is not { } w) return null;
        var saved = _insertTarget;
        try
        {
            var plan = new MoonWorld.ConveyorPlan();
            plan.start = preview.Point(0f);
            if (!PlanInsert(w, type, length, plan)) return null;
            return w.ConveyorPath(plan.start, plan.startDirection, plan.end, plan.endDirection, plan.startUp, plan.endUp);
        }
        catch { return null; }
        finally { _insertTarget = saved; }
    }

    // ---- placement hologram --------------------------------------------------------------------

    private const string PreviewChild = "MSML preview";

    // A splitter or merger placed on a typed belt previews the belt's two new pieces: in that type's look.
    private static BeltType? _reshaping;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.PreviewReshapedBelt))]
    [HarmonyPrefix]
    private static void BeforeReshapePreview(MoonWorld.StructurePlan plan)
    {
        _reshaping = null;
        if (plan == null) return;
        _reshaping = Belts.TypeOf(plan.cutBelt) ?? Belts.TypeOf(plan.shiftBelt);
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.PreviewReshapedBelt))]
    [HarmonyFinalizer]
    private static void AfterReshapePreview() => _reshaping = null;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.BuildPreview))]
    [HarmonyPostfix]
    private static void AfterBuildPreview(MoonDepotBuilder __instance, int index, MoonConveyorPath path)
    {
        try
        {
            var slots = __instance.previews;
            if (slots == null || index < 0 || index >= slots.Length || slots[index]?.root == null) return;
            var slot = slots[index];
            var old = slot.root.transform.Find(PreviewChild);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
            var type = _reshaping ?? (__instance.ConveyorMode ? _active : null);
            if (type?.Preview == null || path == null)
            {
                if (slot.renderer != null) slot.renderer.enabled = true;
                return;
            }
            if (slot.renderer != null) slot.renderer.enabled = false;
            var parts = new GameObject(PreviewChild);
            parts.transform.SetParent(slot.root.transform, false);
            parts.layer = slot.root.layer;
            // Aimed at a belt it goes into: show it built into that belt there.
            if (_reshaping == null && InsertPreviewPath(type, path) is { } inserted) path = inserted;
            type.Preview(path, parts, slot.renderer != null ? slot.renderer.sharedMaterial : BeltLooks.Glow(Color.cyan));
            foreach (var t in parts.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = slot.root.layer;
        }
        catch (Exception e) { Log.Error("Drawing a belt type's placement hologram failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.Update))]
    [HarmonyPrefix]
    private static void BeforeWorldUpdate(MoonWorld __instance)
    {
        try { BeltState.Tick(__instance); ApplyKitLength(); }
        catch (Exception e) { Log.Error("Belt update failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepConveyors))]
    [HarmonyPostfix]
    private static void AfterStepConveyors(MoonWorld __instance)
    {
        try { BeltState.MapRiders(__instance); }
        catch (Exception e) { Log.Error("Mapping capsules on typed belts failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonDustCanisterView), nameof(MoonDustCanisterView.Show))]
    [HarmonyPrefix]
    private static void BeforeCapsuleShown(MoonDroppedCanister c, ref Vector3 point, ref Quaternion rotation) =>
        BeltState.Pose(c, ref point, ref rotation);

    // Far capsules are drawn in batches worked out from the capsule's record: while that's worked out, the record
    // holds the posed position and rotation (put back straight after).
    private static (MoonDroppedCanister C, Vector3 Position, Quaternion Rotation)? _swapped;

    [HarmonyPatch(typeof(MoonCanisterStreaming), nameof(MoonCanisterStreaming.WorkOut))]
    [HarmonyPrefix]
    private static void BeforeWorkOut(MoonDroppedCanister c)
    {
        _swapped = null;
        if (c == null || !BeltState.Poses(c.id)) return;
        var position = c.position;
        var rotation = c.rotation;
        BeltState.Pose(c, ref position, ref rotation);
        _swapped = (c, c.position, c.rotation);
        c.position = position;
        c.rotation = rotation;
    }

    [HarmonyPatch(typeof(MoonCanisterStreaming), nameof(MoonCanisterStreaming.WorkOut))]
    [HarmonyFinalizer]
    private static void AfterWorkOut()
    {
        if (_swapped is not { } s) return;
        _swapped = null;
        s.C.position = s.Position;
        s.C.rotation = s.Rotation;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterWorldDestroyed() => BeltState.Clear();
}
