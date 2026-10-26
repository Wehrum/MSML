using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using MoonSweepMods.ModelKit;
using UnityEngine;

namespace MoonSweepMods.HexHarvester;

/// <summary>
/// Puts the Hex Harvester design on a freshly built Mega Vacuum Rover model. The rover supplies the game-side pieces
/// the vehicle code needs (MoonAuthoredVehicleView, wheel transforms, wheel contacts, driver anchor, colliders);
/// all of its meshes are hidden and replaced by the design's geometry. See Design/HexHarvesterDesign.cs.
/// </summary>
internal static class HarvesterModel
{
    private static Model? _design;

    public static void Build(GameObject model, MoonPrototypeModels kit)
    {
        var view = model.GetComponent<MoonAuthoredVehicleView>();
        if (view == null) { Plugin.Logger.LogError("Base vehicle model has no MoonAuthoredVehicleView."); return; }
        _design ??= new HexHarvesterDesign().Build();
        var design = _design;
        var scale = HexHarvesterDesign.PhysicsScale;

        model.name = "HexHarvester";
        // Physics anchors (wheels, contacts, seat, colliders) scale with the root; geometry below is built in metres.
        model.transform.localScale = Vector3.one * scale;

        var hidden = 0;
        foreach (var r in model.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; hidden++; }

        var root = new GameObject("Hex Harvester visuals").transform;
        root.SetParent(model.transform, false);
        root.localScale = Vector3.one / scale;

        var materials = new Dictionary<string, Material?>();
        Material? Mat(string name)
        {
            if (materials.TryGetValue(name, out var m)) return m;
            m = name switch
            {
                "shell" => kit.shell,
                "dark" => kit.dark,
                "orange" => kit.orange,
                "glass" => kit.glass,
                "metal" => kit.metal,
                "cyan" => kit.cyan,
                _ => null,
            };
            if (m == null && design.Palette.TryGetValue(name, out var c)) m = kit.Mat("Hex Harvester " + name, UnityModelBuilder.ToColor(c));
            if (m == null) Plugin.Logger.LogWarning($"No material for '{name}'.");
            materials[name] = m;
            return m;
        }

        // Wheels attach to the game's animated wheel transforms (Wheel_1L ...), which already sit at the design's pivots.
        Transform? ParentFor(ModelGroup g)
        {
            if (!g.Name.StartsWith("Wheel_")) return null;
            var wheel = model.transform.Find(g.Name);
            if (wheel == null) { Plugin.Logger.LogWarning($"Rover has no {g.Name}; wheel attached to the body."); return null; }
            var holder = new GameObject(g.Name + " visual").transform;
            holder.SetParent(wheel, false);
            holder.localScale = Vector3.one / scale;
            return holder;
        }

        var made = UnityModelBuilder.Build(design, root, Mat, ParentFor);

        // Rollers: the game spins view.rollers while cleaning.
        var rollers = new List<Transform>();
        foreach (var (name, t) in made) if (name.StartsWith("Roller ")) rollers.Add(t);
        view.rollers = new Il2CppReferenceArray<Transform>(rollers.ToArray());
        var rest = new Il2CppStructArray<Quaternion>(rollers.Count);
        for (var i = 0; i < rollers.Count; i++) rest[i] = rollers[i].localRotation;
        view.rollerRest = rest;

        view.cleaningWidth = design.Values["cleaningWidth"];
        view.cleaningOffset = design.Values["headZ"] / scale;
        view.wheelRadius = design.Values["wheelRadius"];

        Plugin.Logger.LogInfo($"Built Hex Harvester model: hid {hidden} rover renderers, {made.Count} parts, " +
            $"{rollers.Count} brush rollers, cleaning width {view.cleaningWidth} m, offset {view.cleaningOffset}.");
    }
}
