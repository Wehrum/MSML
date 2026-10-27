using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace MSML.HexHarvester;

/// <summary>
/// Per-vehicle setup the game only allows after it has assembled the vehicle:
/// <list type="bullet">
/// <item>While driving, the game rolls a "Sprung visual assembly" with the suspension (body sway). It moves the base
/// vehicle's body, the rollers, the hoses and the lights into it, but not the harvester's own body, so the intake
/// duct swayed while its clamps stood still. The harvester's visuals go into it too, so everything sways together.</item>
/// <item>The game builds head and tail lamps from the base vehicle's body outline, which puts the headlights inside
/// the harvester's cab. They're moved to the design's lamp housings.</item>
/// </list>
/// Works for your own vehicles and crewmates' copies (which have no sprung assembly).
/// </summary>
internal static class HarvesterRig
{
    private const string Sprung = "Sprung visual assembly";
    private const string Visuals = "Hex Harvester visuals";
    private const string Lights = "Vehicle lights";
    private const string Placed = " (placed)";

    // Per model: when it was last checked. The work is done once; it's checked again every second in case the game
    // rebuilt the sway assembly or lights (walking the model by name every frame was costly).
    private static readonly Dictionary<IntPtr, float> Checked = new();

    /// <param name="model">The vehicle's model root (its MoonAuthoredVehicleView), scaled by the physics scale.</param>
    public static void Update(Transform? model)
    {
        if (model == null) return;
        var now = Time.unscaledTime;
        if (Checked.TryGetValue(model.Pointer, out var next) && now < next) return;
        if (Checked.Count > 64) Checked.Clear();
        Checked[model.Pointer] = now + 1f;
        var sprung = Find(model, Sprung);
        var visuals = Find(model, Visuals);
        // Same frame as the root at rest (the assembly is at the origin), so the local pose carries over unchanged.
        if (sprung != null && visuals != null && visuals.parent != sprung) visuals.SetParent(sprung, false);

        var lights = Find(model, Lights);
        if (lights != null) PlaceLights(lights, sprung != null && lights.IsChildOf(sprung) ? sprung : model);
    }

    /// <param name="frame">The frame the design's positions are in: the model root, or the sprung assembly (the
    /// same frame at rest) when the lights sway with it.</param>
    private static void PlaceLights(Transform lights, Transform frame)
    {
        for (var i = 0; i < lights.childCount; i++)
        {
            var child = lights.GetChild(i);
            if (child.name.EndsWith(Placed)) continue;
            var isHead = child.name.StartsWith("Headlight");
            var isTail = child.name.StartsWith("Tail");
            if (!isHead && !isTail) continue;
            var side = frame.InverseTransformPoint(child.position).x >= 0 ? 1f : -1f;
            var metres = isHead ? HexHarvesterDesign.Headlight : HexHarvesterDesign.TailLight;
            var lamp = child.GetComponent<Light>() == null;
            // Lamp panes sit on the housing face; the light sources just in front of them.
            var ahead = isHead ? (lamp ? 0f : 0.06f) : (lamp ? 0f : -0.04f);
            var target = new Vector3(side * metres.X, metres.Y, metres.Z + ahead) / HexHarvesterDesign.PhysicsScale;
            child.position = frame.TransformPoint(target);
            child.name += Placed;
        }
    }

    private static Transform? Find(Transform parent, string name)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name) return child;
        }
        for (var i = 0; i < parent.childCount; i++)
        {
            var found = Find(parent.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
