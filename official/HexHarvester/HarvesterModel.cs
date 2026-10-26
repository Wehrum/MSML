using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using MoonSweepMods.ModelKit;
using UnityEngine;

namespace MSML.HexHarvester;

/// <summary>
/// Puts the Hex Harvester design on a freshly built Mega Vacuum Rover model. The rover supplies the game-side pieces
/// the vehicle code needs (MoonAuthoredVehicleView, wheel transforms, wheel contacts, driver anchor, colliders);
/// all of its meshes are hidden and replaced by the design's geometry. See Design/HexHarvesterDesign.cs.
/// </summary>
internal static class HarvesterModel
{
    private static Model? _design;
    private static readonly Dictionary<string, Material> GarageMaterials = new();
    private static Material? _siloGlass;

    /// <summary>Clear glass for the silo panes (URP Lit, transparent), so the dust inside shows.</summary>
    private static Material SiloGlass()
    {
        if (_siloGlass != null && !_siloGlass.WasCollected) return _siloGlass;
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Hex Harvester silo glass" };
        m.SetFloat("_Surface", 1);
        m.SetFloat("_Blend", 0);
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.SetFloat("_Smoothness", 0.95f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.SetColor("_BaseColor", new Color(0.75f, 0.88f, 0.95f, 0.18f));
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _siloGlass = m;
    }

    /// <summary>
    /// A material from a garage in the world (its silo's dust), so the harvester's silo looks like the garage's.
    /// Null when no garage is loaded yet (the design's palette colour is used, and <see cref="SiloFill"/> swaps it in
    /// later).
    /// </summary>
    internal static Material? GarageMaterial(string name)
    {
        if (GarageMaterials.TryGetValue(name, out var cached) && cached != null && !cached.WasCollected) return cached;
        foreach (var garage in MoonGarage.all)
        {
            if (garage == null) continue;
            foreach (var r in garage.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name == name) return GarageMaterials[name] = m;
        }
        return null;
    }

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
        var roverMaterials = new Dictionary<string, Material>();
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in r.sharedMaterials)
                if (mat != null && !roverMaterials.ContainsKey(mat.name)) roverMaterials[mat.name] = mat;
            // The rover's wheels are detailed game meshes that already fit (radius 0.66 x 1.45 = our tyres): keep them.
            if (r.transform.parent != null && r.transform.parent.name.StartsWith("Wheel_")) continue;
            r.enabled = false;
            hidden++;
        }
        // The rover's own paints (the game's "Station finish" shader), so the harvester is shaded like stock vehicles.
        Material? Rover(string name) => roverMaterials.TryGetValue(name, out var found) ? found : null;

        var root = new GameObject("Hex Harvester visuals").transform;
        root.SetParent(model.transform, false);
        root.localScale = Vector3.one / scale;

        var materials = new Dictionary<string, Material?>();
        Material? Mat(string name)
        {
            if (materials.TryGetValue(name, out var m)) return m;
            m = name switch
            {
                "shell" => Rover("009") ?? kit.shell,
                "dark" => Rover("002") ?? kit.dark,
                "orange" => Rover("003") ?? kit.orange,
                "glass" => kit.glass,
                "metal" => Rover("001") ?? kit.metal,
                "steel" => Rover("004"),
                "rubber" => Rover("011"),
                "hazard" => Rover("007"),
                "cyan" => kit.cyan,
                HexHarvesterDesign.SiloGlass => SiloGlass(),
                HexHarvesterDesign.SiloDust => GarageMaterial("Garage - Container dust"),
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

        // The design's own wheels are for the offline preview; in the game the rover's wheels are used.
        foreach (var (name, t) in made)
            if (name.StartsWith("Wheel_") && t.parent != null && t.parent.name.EndsWith(" visual")) UnityEngine.Object.Destroy(t.parent.gameObject);

        AddLogos(root, Rover("016"));

        // Rollers: the game spins view.rollers while cleaning.
        var rollers = new List<Transform>();
        foreach (var (name, t) in made) if (name.StartsWith("Roller ")) rollers.Add(t);
        view.rollers = new Il2CppReferenceArray<Transform>(rollers.ToArray());
        var rest = new Il2CppStructArray<Quaternion>(rollers.Count);
        for (var i = 0; i < rollers.Count; i++) rest[i] = rollers[i].localRotation;
        view.rollerRest = rest;

        // The game scales the cleaned strip by the model's scale, like the offset below.
        view.cleaningWidth = design.Values["cleaningWidth"] / scale;
        view.cleaningOffset = design.Values["headZ"] / scale;
        // Like the width: the game places the wheel visuals at contact + wheelRadius x the model's scale.
        view.wheelRadius = design.Values["wheelRadius"] / scale;

        // Hoses: the game pulses these meshes along their paths while sucking, and plays the hose sound.
        var hoses = new List<MoonAuthoredVehicleView.Hose>();
        foreach (var (groupName, (path, envelope)) in design.Hoses)
        {
            if (!made.TryGetValue(groupName, out var part)) continue;
            var hose = new MoonAuthoredVehicleView.Hose { part = part };
            var p = new Il2CppStructArray<Vector3>(path.Count);
            var e = new Il2CppStructArray<float>(path.Count);
            for (var i = 0; i < path.Count; i++) { p[i] = UnityModelBuilder.ToUnity(path[i]); e[i] = envelope[i]; }
            hose.path = p;
            hose.envelope = e;
            hoses.Add(hose);
        }
        view.hoses = new Il2CppReferenceArray<MoonAuthoredVehicleView.Hose>(hoses.ToArray());

        Plugin.Logger.LogInfo($"Built Hex Harvester model: hid {hidden} rover renderers, {made.Count} parts, " +
            $"{rollers.Count} brush rollers, {hoses.Count} hoses, cleaning width {view.cleaningWidth} m, offset {view.cleaningOffset}.");
    }

    /// <summary>
    /// The rover's "LSD" logo decal (a game texture) on the design's logo spots: quads, since the design's meshes have
    /// no texture coordinates.
    /// </summary>
    private static void AddLogos(Transform root, Material? logo)
    {
        if (logo == null) return;
        foreach (var (position, normal, width) in HexHarvesterDesign.Logos)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.name = "LSD logo";
            quad.transform.SetParent(root, false);
            quad.transform.localPosition = UnityModelBuilder.ToUnity(position);
            // A quad faces -Z; turn it so its front faces along the surface normal.
            quad.transform.localRotation = Quaternion.LookRotation(-UnityModelBuilder.ToUnity(normal), Vector3.up);
            var texture = logo.mainTexture;
            var aspect = texture != null && texture.width > 0 ? (float)texture.height / texture.width : 0.25f;
            quad.transform.localScale = new Vector3(width, width * aspect, 1);
            quad.GetComponent<Renderer>().sharedMaterial = logo;
        }
    }
}
