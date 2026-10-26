using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>See <see cref="BeltType.RiderPose"/>.</summary>
public delegate void RiderPose(int belt, float distance, MoonDroppedCanister capsule, ref Vector3 position, ref Quaternion rotation);

/// <summary>
/// A kind of conveyor belt added by a mod, built with its own card in the build menu. Belts of a type are ordinary
/// belts to the game (so they connect to belts, splitters, mergers and machines, and removing the mod turns them back
/// into plain belts); the type adds a speed, a price, a look and anything else the mod does with them.
/// Register it with <see cref="Belts.Register"/> from your plugin's <c>Load()</c>.
/// </summary>
public sealed class BeltType
{
    /// <param name="id">Stable id, saved with the moon: "yourmod.belt". Never change it.</param>
    /// <param name="name">Name on the build card, e.g. "Pneumatic tube".</param>
    public BeltType(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A belt type needs an id.", nameof(id));
        Id = id;
        Name = name;
    }

    /// <summary>Stable id saved with the moon.</summary>
    public string Id { get; }

    /// <summary>Name on the build card.</summary>
    public string Name { get; }

    /// <summary>Text shown on the build card's details.</summary>
    public string Description { get; set; } = "";

    /// <summary>Picture for the build card (a PNG; relative paths are from your mod's folder). Null uses the belt's.</summary>
    public string? Card { get; set; }

    /// <summary>Build menu section: "Logistics" by default.</summary>
    public string Category { get; set; } = "Logistics";

    /// <summary>Price compared to a plain belt of the same length (refunds follow it).</summary>
    public double PriceScale { get; set; } = 1;

    /// <summary>A fixed price for each piece whatever its length (instead of <see cref="PriceScale"/>), e.g. for a machine-like piece.</summary>
    public double? PricePerPiece { get; set; }

    /// <summary>
    /// Longest belt of this type that can be built in one piece, in metres (null: the game's 30 m). Can be more than
    /// the game's limit.
    /// </summary>
    public float? MaxLength { get; set; }

    /// <summary>
    /// Every piece is exactly this long, straight on from where it starts (the player only chooses where it starts and
    /// which way it points), e.g. for a machine-like piece.
    /// </summary>
    public float? FixedLength { get; set; }

    /// <summary>Pieces of this type can't be joined directly to each other (something else has to be between them).</summary>
    public bool NoChaining { get; set; }

    /// <summary>
    /// With <see cref="FixedLength"/>: aiming at a belt of one of these types builds the piece into it, like placing
    /// a splitter on a belt: the belt is cut there and the piece goes in between its two halves (which keep their
    /// type, at no cost). E.g. a booster built into a tube line.
    /// </summary>
    public HashSet<BeltType>? InsertsInto { get; set; }

    /// <summary>
    /// Belts of this type can only be built along the ground (or foundations): no higher above it than
    /// <see cref="MaxGroundGap"/> anywhere except near their ends, where they may rise to meet machines.
    /// </summary>
    public bool OnGround { get; set; }

    /// <summary>With <see cref="OnGround"/>: the most the belt may be above the ground, in metres.</summary>
    public float MaxGroundGap { get; set; } = 1.4f;

    /// <summary>With <see cref="OnGround"/>: how far from each end the belt may rise to meet machines, in metres.</summary>
    public float GroundRamp { get; set; } = 1.6f;

    /// <summary>
    /// Vehicles and players go over belts of this type as if they were ground (no bumping into them, no being carried
    /// along); the build tool can still pick them. E.g. for belts sunk into the ground.
    /// </summary>
    public bool PassThrough { get; set; }

    /// <summary>When a belt of this type is removed, the capsules on it are deleted instead of dropped.</summary>
    public bool DeleteRidersOnRemove { get; set; }

    /// <summary>
    /// With <see cref="PassThrough"/>: lowers the belt's (invisible) colliders so their tops are this many metres above
    /// the ground below them, so players walk over the belt's look instead of above the old belt height.
    /// </summary>
    public float? ColliderTop { get; set; }

    /// <summary>Speed compared to a plain belt (1.1 m/s). At least 1. <see cref="Belts.SetSpeed"/> overrides it per belt.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>
    /// Called with the belt id and an empty object whenever a belt of this type gets a view: when it's built or
    /// loaded, given this type, or comes back into view. Add your parts (in world positions) as children of that
    /// object. It's kept apart from the game's belt (so the game's animations never touch it), shown and hidden with
    /// it, and removed with it (also when the belt's type changes).
    /// </summary>
    public Action<int, GameObject>? Style { get; set; }

    /// <summary>
    /// Hide the game's own belt (chassis, tread, legs) on belts of this type, so <see cref="Style"/> can draw something
    /// else entirely (add your own supports, e.g. with <see cref="BeltLooks.GroundBelow"/>). Its colliders stay. The
    /// game's build and removal animations of the belt are skipped too.
    /// </summary>
    public bool HideBelt { get; set; }

    /// <summary>
    /// Draws the placement hologram while building belts of this type, instead of the plain belt's: called with the
    /// planned path, an empty object to add parts to, and the game's hologram material (its colour shows whether the
    /// plan can be built). Give every part that material.
    /// </summary>
    public Action<MoonConveyorPath, GameObject, Material>? Preview { get; set; }

    /// <summary>
    /// Changes how capsules riding belts of this type are drawn (only how they look; where the game keeps them is
    /// unchanged). Called with the belt, the capsule's distance along it, the capsule, and the position and rotation
    /// the game would show it at (the capsule's bottom, upright), which you can change.
    /// </summary>
    public RiderPose? RiderPose { get; set; }

    /// <summary>Name of the mod that registered it.</summary>
    public string ModName { get; internal set; } = "";

    /// <summary>Build menu kind used for its card (assigned when registered).</summary>
    public int Kind { get; internal set; } = -1;

    internal string? CardPath { get; set; }
    internal Sprite? CardSprite { get; set; }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// Belt types, belt speeds and the belt network. Belt ids are the game's conveyor ids; routes inside machines
/// (splitters, mergers...) have negative ids.
/// </summary>
public static class Belts
{
    private static readonly List<BeltType> Registered = new();

    /// <summary>A plain belt's speed in metres per second.</summary>
    public static float BaseSpeed => MoonWorld.BeltSpeed;

    /// <summary>First build-menu kind used for belt type cards.</summary>
    public const int FirstKind = 7000;

    /// <summary>Every registered belt type.</summary>
    public static IReadOnlyList<BeltType> Types => Registered;

    /// <summary>
    /// Structure kinds capsules pass straight through, followed by <see cref="Downstream"/> and <see cref="Upstream"/>:
    /// splitters (11) and mergers (12).
    /// </summary>
    public static HashSet<int> JunctionKinds { get; } = new() { 11, 12 };

    /// <summary>Adds the type and its card to the build menu. Call from <c>Load()</c>.</summary>
    public static BeltType Register(BeltType type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (Registered.Any(t => t.Id.Equals(type.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A belt type with id '{type.Id}' is already registered.");
        if (type.Speed < 1f) throw new ArgumentOutOfRangeException(nameof(type), "Belt types can't be slower than a plain belt.");

        var caller = Assembly.GetCallingAssembly();
        type.ModName = Vehicles.ModNameOf(caller) ?? type.Name;
        if (type.Card != null)
        {
            var folder = Path.GetDirectoryName(caller.Location) ?? "";
            type.CardPath = Path.IsPathRooted(type.Card) ? type.Card : Path.Combine(folder, type.Card);
        }
        type.Kind = FirstKind + Registered.Count;
        Registered.Add(type);
        BeltPatches.Apply();
        BeltPatches.AddCard(type);
        Log.Info($"Registered belt type {type} (card kind {type.Kind}, x{type.Speed} speed, x{type.PriceScale} price)");
        return type;
    }

    /// <summary>The registered type with this id, or null.</summary>
    public static BeltType? TypeById(string id) =>
        Registered.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ---- belts ---------------------------------------------------------------------------------

    /// <summary>Raised when the belt network changes: belts built, removed or given a type, or a moon loaded.</summary>
    public static event Action? Changed;

    /// <summary>Raised when a belt's type changes (null: back to a plain belt).</summary>
    public static event Action<int, BeltType?>? TypeChanged;

    /// <summary>The type of a belt, or null for plain belts.</summary>
    public static BeltType? TypeOf(int belt) => BeltState.TypeOf(belt);

    /// <summary>Belts of a type.</summary>
    public static IReadOnlyCollection<int> OfType(BeltType type) => BeltState.OfType(type);

    /// <summary>
    /// Gives a belt a type (null: plain belt). In a crew, crewmates ask the host, which tells everyone; the change
    /// shows right away on this machine.
    /// </summary>
    public static void SetType(int belt, BeltType? type) => BeltState.SetType(belt, type, true);

    /// <summary>Every built belt (not machine routes).</summary>
    public static IEnumerable<int> All => World is { } w ? BeltState.Keys(w.conveyors) : Enumerable.Empty<int>();

    /// <summary>True if the belt (or machine route) exists.</summary>
    public static bool Exists(int belt) => World is { } w && (w.conveyors.ContainsKey(belt) || w.machinePaths.ContainsKey(belt));

    /// <summary>Builds a belt's look again (e.g. after something it depends on changed, like its neighbours).</summary>
    public static void Restyle(int belt) => BeltState.Restyle(belt);

    /// <summary>The capsules riding a belt (or machine route): (distance along it, capsule id), front first.</summary>
    public static List<(float Distance, int Id)> Riders(int belt)
    {
        var w = World;
        if (w == null || !w.beltQueues.ContainsKey(belt)) return new List<(float, int)>();
        var queue = w.beltQueues[belt];
        if (queue == null || queue.Count == 0) return new List<(float, int)>();
        var riders = BeltState.ReadQueue(queue);
        riders.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        return riders;
    }

    /// <summary>The belt's path (length, points along it), or null.</summary>
    public static MoonConveyorPath? PathOf(int belt)
    {
        var w = World;
        if (w == null || !Exists(belt)) return null;
        try { return w.BeltPath(belt); } catch { return null; }
    }

    /// <summary>The belt's view in the world, or null when it has none right now (far away, or not built yet).</summary>
    public static GameObject? ViewOf(int belt)
    {
        var views = World?.ConveyorViews;
        if (views == null) return null;
        var view = views.ViewOf(belt);
        return view == null ? null : view;
    }

    // ---- speed ---------------------------------------------------------------------------------

    /// <summary>
    /// Sets a belt's (or machine route's) speed compared to a plain belt (at least 1), overriding its type's speed.
    /// Capsules can't go faster than the belt ahead lets them in, so the slowest part of a line sets its throughput.
    /// </summary>
    public static void SetSpeed(int belt, float multiplier) => BeltState.SetSpeed(belt, Math.Max(1f, multiplier));

    /// <summary>Goes back to the speed of the belt's type. Changing a belt's type also clears its speed.</summary>
    public static void ClearSpeed(int belt) => BeltState.ClearSpeed(belt);

    /// <summary>The belt's current speed compared to a plain belt.</summary>
    public static float SpeedOf(int belt) => BeltState.SpeedOf(belt);

    // ---- network -------------------------------------------------------------------------------

    /// <summary>
    /// Belts that capsules from this belt's end go onto next: the belt starting there, or, through a splitter or
    /// merger (<see cref="JunctionKinds"/>), the belts leaving it. Empty when the belt ends at a machine or nothing.
    /// </summary>
    public static List<int> Downstream(int belt) => BeltNetwork.Next(belt, true);

    /// <summary>Belts feeding this belt's start, directly or through a splitter or merger.</summary>
    public static List<int> Upstream(int belt) => BeltNetwork.Next(belt, false);

    /// <summary>The structure at this belt's end (0 if it goes onto another belt or nothing), and its kind.</summary>
    public static (int Structure, int Kind) EndsAt(int belt) => BeltNetwork.StructureAt(belt, true);

    /// <summary>The structure at this belt's start (0 if it comes from another belt or nothing), and its kind.</summary>
    public static (int Structure, int Kind) StartsAt(int belt) => BeltNetwork.StructureAt(belt, false);

    /// <summary>Routes capsules take inside a structure (e.g. a splitter's two), as belt ids usable with <see cref="SetSpeed"/>.</summary>
    public static List<int> RoutesThrough(int structure) => BeltNetwork.Routes(structure);

    internal static MoonWorld? World => BeltState.World;

    internal static void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)handler)(); }
            catch (Exception e) { Log.Error("A Belts.Changed handler failed: " + e); }
        }
    }

    internal static void RaiseTypeChanged(int belt, BeltType? type)
    {
        foreach (var handler in TypeChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<int, BeltType?>)handler)(belt, type); }
            catch (Exception e) { Log.Error("A Belts.TypeChanged handler failed: " + e); }
        }
    }
}
