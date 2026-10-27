using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// A kind of splitter or merger added by a mod, built with its own card in the build menu. To the game it's its own
/// splitter or merger (so it connects and routes capsules the same way, and removing the mod turns it back into the
/// game's); the type gives it a look, a placement hologram, and how capsules passing through it are drawn.
/// Register it with <see cref="Junctions.Register"/> from your plugin's <c>Load()</c>.
/// </summary>
public sealed class JunctionType
{
    /// <summary>The game's splitter kind.</summary>
    public const int Splitter = 11;

    /// <summary>The game's merger kind.</summary>
    public const int Merger = 12;

    /// <param name="id">Stable id, saved with the moon: "yourmod.splitter". Never change it.</param>
    /// <param name="name">Name on the build card, e.g. "Tube splitter".</param>
    /// <param name="baseKind"><see cref="Splitter"/> or <see cref="Merger"/>: what it is to the game.</param>
    public JunctionType(string id, string name, int baseKind)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A junction type needs an id.", nameof(id));
        if (baseKind != Splitter && baseKind != Merger) throw new ArgumentOutOfRangeException(nameof(baseKind), "A junction type is a splitter (11) or a merger (12).");
        Id = id;
        Name = name;
        BaseKind = baseKind;
    }

    /// <summary>Stable id saved with the moon.</summary>
    public string Id { get; }

    /// <summary>Name on the build card.</summary>
    public string Name { get; }

    /// <summary>The game's structure kind it is: <see cref="Splitter"/> or <see cref="Merger"/>.</summary>
    public int BaseKind { get; }

    /// <summary>Text shown on the build card's details.</summary>
    public string Description { get; set; } = "";

    /// <summary>Picture for the build card (a PNG; relative paths are from your mod's folder). Null uses the game's.</summary>
    public string? Card { get; set; }

    /// <summary>Build menu section: "Logistics" by default.</summary>
    public string Category { get; set; } = "Logistics";

    /// <summary>Sub-tab of its tab to put the card in (see <see cref="BuildMenu"/>), e.g. "Tubes"; null: with the game's cards.</summary>
    public string? Section { get; set; }

    /// <summary>The game's model isn't drawn (draw your own with <see cref="Style"/>).</summary>
    public bool HideModel { get; set; }

    /// <summary>Players and vehicles go over it as if it were ground (see <see cref="BeltType.PassThrough"/>).</summary>
    public bool PassThrough { get; set; }

    /// <summary>With <see cref="PassThrough"/>: how far above the ground its colliders reach (null: unchanged).</summary>
    public float? ColliderTop { get; set; }

    /// <summary>
    /// Draws one: called with the structure's id and an empty object (in world space) to add parts to. The object
    /// follows the game's view (shown, hidden and removed with it). <see cref="Junctions.Pose"/> gives where it stands.
    /// </summary>
    public Action<int, GameObject>? Style { get; set; }

    /// <summary>
    /// Draws the placement hologram instead of the game's: called with an empty object at the planned pose (local
    /// space: the structure's floor at y = 0, capsules entering along +z) and the game's hologram material.
    /// </summary>
    public Action<GameObject, Material>? Preview { get; set; }

    /// <summary>Capsules passing through are drawn as on belts of this type (its <see cref="BeltType.RiderPose"/>).</summary>
    public BeltType? RouteType { get; set; }

    /// <summary>
    /// How far below the game's ports (0.85 m above its floor) its own model's mouths are, e.g. when drawn sunk into
    /// the ground. Aiming a belt at a mouth there snaps to its port, and the port markers are shown there. 0: at the ports.
    /// </summary>
    public float PortSink { get; set; }

    /// <summary>Name of the mod that registered it.</summary>
    public string ModName { get; internal set; } = "";

    /// <summary>Build menu kind used for its card (assigned when registered).</summary>
    public int Kind { get; internal set; } = -1;

    internal string? CardPath { get; set; }
    internal Sprite? CardSprite { get; set; }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>Splitter and merger types (<see cref="JunctionType"/>). Structure ids are the game's.</summary>
public static class Junctions
{
    private static readonly List<JunctionType> Registered = new();

    /// <summary>First build-menu kind used for junction type cards.</summary>
    public const int FirstKind = 7500;

    /// <summary>Where a splitter's or merger's ports are, in its own space (floor at y = 0, capsules flowing along +z).</summary>
    public static IReadOnlyList<Vector3> InputsOf(int baseKind) => baseKind == JunctionType.Splitter ? SplitterIn : MergerIn;

    /// <inheritdoc cref="InputsOf"/>
    public static IReadOnlyList<Vector3> OutputsOf(int baseKind) => baseKind == JunctionType.Splitter ? SplitterOut : MergerOut;

    private static readonly Vector3[] SplitterIn = { new(0f, 0.85f, -1.6f) };
    private static readonly Vector3[] SplitterOut = { new(-0.7f, 0.85f, 1.6f), new(0.7f, 0.85f, 1.6f) };
    private static readonly Vector3[] MergerIn = { new(-0.7f, 0.85f, -1.6f), new(0.7f, 0.85f, -1.6f) };
    private static readonly Vector3[] MergerOut = { new(0f, 0.85f, 1.6f) };

    /// <summary>Every registered junction type.</summary>
    public static IReadOnlyList<JunctionType> Types => Registered;

    /// <summary>Adds the type and its card to the build menu. Call from <c>Load()</c>.</summary>
    public static JunctionType Register(JunctionType type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (Registered.Any(t => t.Id.Equals(type.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A junction type with id '{type.Id}' is already registered.");
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
        JunctionPatches.Apply();
        JunctionPatches.AddCard(type);
        if (type.Section != null) BuildMenu.SetSection(type.Kind, type.Section);
        Log.Info($"Registered junction type {type} (card kind {type.Kind}, a {(type.BaseKind == JunctionType.Splitter ? "splitter" : "merger")} to the game)");
        return type;
    }

    /// <summary>The registered type with this id, or null.</summary>
    public static JunctionType? TypeById(string id) =>
        Registered.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised when a structure's junction type changes (null: back to the game's).</summary>
    public static event Action<int, JunctionType?>? TypeChanged;

    internal static void RaiseTypeChanged(int structure, JunctionType? type)
    {
        try { TypeChanged?.Invoke(structure, type); }
        catch (Exception e) { Log.Error("A junction TypeChanged handler failed: " + e); }
    }

    /// <summary>The type of a splitter or merger, or null for the game's own (and anything else).</summary>
    public static JunctionType? TypeOf(int structure) => JunctionState.TypeOf(structure);

    /// <summary>Splitters and mergers of a type.</summary>
    public static IReadOnlyCollection<int> OfType(JunctionType type) => JunctionState.OfType(type);

    /// <summary>
    /// Gives a splitter or merger a type (null: the game's). In a crew, crewmates ask the host, which tells everyone.
    /// </summary>
    public static void SetType(int structure, JunctionType? type) => JunctionState.SetType(structure, type, true);

    /// <summary>Draws one again (e.g. after something its look depends on changed).</summary>
    public static void Restyle(int structure) => JunctionState.Restyle(structure);

    /// <summary>Where a structure stands: its floor's centre and its rotation (capsules flow along its +z).</summary>
    public static bool Pose(int structure, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        var w = Belts.World;
        if (w == null || !w.depots.ContainsKey(structure)) return false;
        w.StructurePose(w.depots[structure], out position, out rotation);
        return true;
    }

    /// <summary>The structure's view in the world, or null when it has none right now.</summary>
    public static GameObject? ViewOf(int structure) => JunctionState.ViewOf(structure);
}
