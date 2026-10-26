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
/// </summary>
internal static class HarvesterRig
{
    private const string Sprung = "Sprung visual assembly";
    private const string Visuals = "Hex Harvester visuals";
    private const string Placed = " (placed)";

    public static void Update(MoonPrototypeVehicle vehicle)
    {
        var root = vehicle.authored != null ? vehicle.authored.transform : vehicle.transform;
        var sprung = Find(root, Sprung);
        if (sprung == null) return;

        var visuals = Find(root, Visuals);
        // Same frame as the root at rest (the assembly is at the origin), so the local pose carries over unchanged.
        if (visuals != null && visuals.parent != sprung) visuals.SetParent(sprung, false);

        var lights = sprung.Find("Vehicle lights");
        if (lights != null) PlaceLights(lights);
    }

    private static void PlaceLights(Transform lights)
    {
        // Light positions are in the assembly's frame (model units = metres / scale); the lights' parent may be offset.
        var parentFromAssembly = Matrix4x4.TRS(lights.localPosition, lights.localRotation, lights.localScale).inverse;
        for (var i = 0; i < lights.childCount; i++)
        {
            var child = lights.GetChild(i);
            if (child.name.EndsWith(Placed)) continue;
            var isHead = child.name.StartsWith("Headlight");
            var isTail = child.name.StartsWith("Tail");
            if (!isHead && !isTail) continue;
            var side = child.localPosition.x >= 0 ? 1f : -1f;
            var metres = isHead ? HexHarvesterDesign.Headlight : HexHarvesterDesign.TailLight;
            var lamp = child.GetComponent<Light>() == null;
            // Lamp panes sit on the housing face; the light sources just in front of them.
            var ahead = isHead ? (lamp ? 0f : 0.06f) : (lamp ? 0f : -0.04f);
            var target = new Vector3(side * metres.X, metres.Y, metres.Z + ahead) / HexHarvesterDesign.PhysicsScale;
            child.localPosition = parentFromAssembly.MultiplyPoint3x4(target);
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
