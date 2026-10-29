using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Building blocks for belt looks (<see cref="BeltType.Style"/>): meshes that follow a belt's path, glass and
/// glowing materials, and the game's own conveyor materials.
/// </summary>
public static class BeltLooks
{
    private static readonly Dictionary<Color, Material> GlassCache = new();
    private static readonly Dictionary<Color, Material> GlowCache = new();
    private static readonly Dictionary<string, Material> GameCache = new();
    private static MaterialPropertyBlock? _block;

    /// <summary>
    /// A tube following the belt's path from <paramref name="from"/> to <paramref name="to"/> metres along it, centred
    /// <paramref name="lift"/> metres above the belt's surface, in the local space of <paramref name="space"/>.
    /// </summary>
    /// <param name="capped">Close the ends (for solid parts; glass tubes stay open).</param>
    /// <param name="side">Moves the tube sideways (metres to the belt's right), e.g. for rails along a bigger tube.</param>
    /// <param name="liftAt">Height of the centre at each distance along the belt, instead of <paramref name="lift"/> (e.g. to follow the ground).</param>
    public static Mesh? Tube(int belt, Transform space, float radius, float lift, float from = 0f, float to = float.MaxValue,
        int sides = 16, float step = 0.25f, bool capped = false, float side = 0f, Func<float, float>? liftAt = null)
    {
        var path = Belts.PathOf(belt);
        return path == null ? null : Tube(path, space, radius, lift, from, to, sides, step, capped, side, belt, liftAt);
    }

    /// <summary>The same along a path, e.g. a planned belt's in <see cref="BeltType.Preview"/>.</summary>
    public static Mesh? Tube(MoonConveyorPath path, Transform space, float radius, float lift, float from = 0f, float to = float.MaxValue,
        int sides = 16, float step = 0.25f, bool capped = false, float side = 0f, Func<float, float>? liftAt = null) =>
        Tube(path, space, radius, lift, from, to, sides, step, capped, side, 0, liftAt);

    private static Mesh? Tube(MoonConveyorPath path, Transform space, float radius, float lift, float from, float to,
        int sides, float step, bool capped, float side, int belt, Func<float, float>? liftAt)
    {
        to = Math.Min(to, path.Length);
        from = Math.Max(0f, from);
        if (to - from < 0.01f) return null;

        var rings = Math.Max(2, (int)Math.Ceiling((to - from) / step) + 1);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        // Into the parent's space with its matrix, in plain C# (see VMath: per-vertex calls into the game were slow).
        var toLocal = space.worldToLocalMatrix;
        for (var i = 0; i < rings; i++)
        {
            var d = from + (to - from) * i / (rings - 1);
            path.Frame(d, out var position, out _, out var right, out var up);
            var centre = VMath.Mad(position, up, liftAt?.Invoke(d) ?? lift, right, side);
            for (var s = 0; s <= sides; s++)
            {
                var a = s * MathF.PI * 2f / sides;
                var outward = VMath.Mad(default, right, MathF.Cos(a), up, MathF.Sin(a));
                vertices.Add(VMath.Point(toLocal, VMath.Mad(centre, outward, radius)));
                normals.Add(VMath.Direction(toLocal, outward));
                uvs.Add(new Vector2((float)s / sides, d));
            }
        }
        var stride = sides + 1;
        for (var i = 0; i < rings - 1; i++)
            for (var s = 0; s < sides; s++)
            {
                int a = i * stride + s, b = a + 1, c = a + stride, e = c + 1;
                triangles.AddRange(new[] { a, b, c, b, e, c });
            }
        FaceOut(vertices, normals, triangles, 0);
        if (capped)
        {
            var first = triangles.Count;
            AddCap(path, from, -1f);
            FaceOut(vertices, normals, triangles, first);
            first = triangles.Count;
            AddCap(path, to, 1f);
            FaceOut(vertices, normals, triangles, first);
        }
        void AddCap(MoonConveyorPath p, float d, float facing)
        {
            p.Frame(d, out var position, out var forward, out var right, out var up);
            var centre = VMath.Mad(position, up, liftAt?.Invoke(d) ?? lift, right, side);
            var first = vertices.Count;
            var normal = VMath.Direction(toLocal, VMath.Mul(forward, facing));
            vertices.Add(VMath.Point(toLocal, centre));
            normals.Add(normal);
            uvs.Add(new Vector2(0.5f, 0.5f));
            for (var s = 0; s <= sides; s++)
            {
                var a = s * MathF.PI * 2f / sides;
                vertices.Add(VMath.Point(toLocal, VMath.Mad(centre, right, MathF.Cos(a) * radius, up, MathF.Sin(a) * radius)));
                normals.Add(normal);
                uvs.Add(new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)));
            }
            for (var s = 0; s < sides; s++)
            {
                if (facing > 0) triangles.AddRange(new[] { first, first + 2 + s, first + 1 + s });
                else triangles.AddRange(new[] { first, first + 1 + s, first + 2 + s });
            }
        }

        var mesh = new Mesh { name = $"Belt {belt} tube" };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = ToIl2Cpp(vertices);
        mesh.normals = ToIl2Cpp(normals);
        mesh.uv = ToIl2Cpp(uvs);
        mesh.SetTriangles(new Il2CppStructArray<int>(triangles.ToArray()), 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// A flat strip facing up, following the path, <paramref name="halfWidth"/> to each side, at
    /// <paramref name="liftAt"/>(distance) above the belt's surface. E.g. a floor inside a tube.
    /// </summary>
    public static Mesh? Strip(MoonConveyorPath path, Transform space, float halfWidth, Func<float, float> liftAt,
        float from = 0f, float to = float.MaxValue, float step = 0.25f)
    {
        to = Math.Min(to, path.Length);
        from = Math.Max(0f, from);
        if (to - from < 0.01f) return null;
        var rows = Math.Max(2, (int)Math.Ceiling((to - from) / step) + 1);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        var toLocal = space.worldToLocalMatrix;
        for (var i = 0; i < rows; i++)
        {
            var d = from + (to - from) * i / (rows - 1);
            path.Frame(d, out var position, out _, out var right, out var up);
            var centre = VMath.Mad(position, up, liftAt(d));
            var normal = VMath.Direction(toLocal, up);
            for (var s = -1f; s <= 1f; s += 2f)
            {
                vertices.Add(VMath.Point(toLocal, VMath.Mad(centre, right, halfWidth * s)));
                normals.Add(normal);
                uvs.Add(new Vector2((s + 1f) / 2f, d));
            }
            if (i == 0) continue;
            int a = (i - 1) * 2, b = a + 1, c = a + 2, e = a + 3;
            triangles.AddRange(new[] { a, c, b, b, c, e });
        }
        var mesh = new Mesh { name = "Belt strip" };
        mesh.vertices = ToIl2Cpp(vertices);
        mesh.normals = ToIl2Cpp(normals);
        mesh.uv = ToIl2Cpp(uvs);
        mesh.SetTriangles(new Il2CppStructArray<int>(triangles.ToArray()), 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Which way a triangle faces depends on the belt's frame (its right/up/forward handedness), so turn each group of
    // triangles (from 'first' on) to face where its normals point: outwards, so single-sided materials show.
    private static void FaceOut(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, int first)
    {
        var score = 0f;
        for (var t = first; t + 2 < triangles.Count; t += 3)
        {
            var a = vertices[triangles[t]];
            var facing = VMath.Cross(VMath.Sub(vertices[triangles[t + 1]], a), VMath.Sub(vertices[triangles[t + 2]], a));
            score += VMath.Dot(facing, VMath.Add(normals[triangles[t]], VMath.Add(normals[triangles[t + 1]], normals[triangles[t + 2]])));
        }
        if (score >= 0f) return;
        for (var t = first; t + 2 < triangles.Count; t += 3) (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
    }

    /// <summary>Adds a child object showing <paramref name="mesh"/> with <paramref name="material"/>.</summary>
    public static MeshRenderer Show(Transform parent, string name, Mesh mesh, Material material, bool shadows = true)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        return renderer;
    }

    /// <summary>
    /// A flat chevron (an arrow head, "&gt;" lying down) at <paramref name="position"/> pointing along
    /// <paramref name="forward"/>, facing <paramref name="up"/>, <paramref name="size"/> metres long; in the local
    /// space of <paramref name="space"/>. Seen from both sides. E.g. to show which way things flow.
    /// </summary>
    public static Mesh Chevron(Transform space, Vector3 position, Vector3 forward, Vector3 up, float size, float thickness = 0.25f)
    {
        forward = Vector3.ProjectOnPlane(forward, up).normalized;
        var right = Vector3.Cross(up, forward).normalized;
        var tip = position + forward * (size * 0.5f);
        var back = position - forward * (size * 0.5f);
        var half = size * 0.45f;
        var bar = size * thickness;
        // Two bars from the tip back to each side (outer and inner edges), drawn on both faces.
        var points = new[]
        {
            tip, tip - forward * bar,
            back + right * half, back + right * half - forward * bar,
            back - right * half, back - right * half - forward * bar,
        };
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        foreach (var side in new[] { up, -up })
            foreach (var p in points)
            {
                vertices.Add(space.InverseTransformPoint(p + side * 0.002f));
                normals.Add(space.InverseTransformDirection(side));
            }
        var triangles = new List<int> { 0, 1, 2, 1, 3, 2, 0, 4, 1, 1, 4, 5 };
        FaceOut(vertices, normals, triangles, 0);
        var under = new List<int>();
        foreach (var i in triangles) under.Add(i + 6);
        for (var t = 0; t + 2 < under.Count; t += 3) (under[t + 1], under[t + 2]) = (under[t + 2], under[t + 1]);
        triangles.AddRange(under);
        var mesh = new Mesh { name = "Chevron" };
        mesh.vertices = ToIl2Cpp(vertices);
        mesh.normals = ToIl2Cpp(normals);
        mesh.SetTriangles(new Il2CppStructArray<int>(triangles.ToArray()), 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// How far below a point on the belt (<paramref name="lift"/> metres above its surface, at
    /// <paramref name="distance"/> along it) the first solid thing is: ground, a foundation, a building. Belts and
    /// capsules are skipped. Use it to stand your own supports on (with <see cref="BeltType.HideBelt"/>).
    /// With <paramref name="maxRise"/>, ground up to that far above the point (a belt cutting through a slope) is
    /// found too, as a negative drop.
    /// </summary>
    public static bool GroundBelow(int belt, float distance, float lift, out float drop, float maxDrop, float maxRise)
    {
        drop = 0f;
        var path = Belts.PathOf(belt);
        return path != null && GroundBelow(path, distance, lift, out drop, maxDrop, maxRise);
    }

    /// <inheritdoc cref="GroundBelow(int, float, float, out float, float, float)"/>
    public static bool GroundBelow(int belt, float distance, float lift, out float drop, float maxDrop = 30f) =>
        GroundBelow(belt, distance, lift, out drop, maxDrop, 0f);

    /// <summary>The same along a path (e.g. a planned belt).</summary>
    public static bool GroundBelow(MoonConveyorPath path, float distance, float lift, out float drop, float maxDrop = 30f) =>
        GroundBelow(path, distance, lift, out drop, maxDrop, 0f);

    /// <summary>The same along a path (e.g. a planned belt).</summary>
    public static bool GroundBelow(MoonConveyorPath path, float distance, float lift, out float drop, float maxDrop, float maxRise)
    {
        drop = 0f;
        if (!Frame(path, distance, out var position, out _, out _, out var up)) return false;
        maxRise = Math.Max(0f, maxRise);
        var from = VMath.Mad(position, up, lift + maxRise);
        var views = Belts.World?.ConveyorViews;
        var best = float.MaxValue;
        _hits ??= new Il2CppStructArray<RaycastHit>(32);
        if (_skipVersion != BeltNetwork.Version || Skip.Count > 8192) { Skip.Clear(); _skipVersion = BeltNetwork.Version; }
        var count = Physics.RaycastNonAlloc(from, VMath.Mul(up, -1f), _hits, maxRise + maxDrop, ~0, QueryTriggerInteraction.Ignore);
        var hits = _hits.AsSpan();
        for (var i = 0; i < count; i++)
        {
            var hit = hits[i];
            // What each collider is (ground or not) is worked out once: asking the game about every hit of every
            // sample was slow and left garbage on its heap.
            var id = hit.m_Collider.m_rawData;
            if (!Skip.TryGetValue(id, out var skip))
            {
                var collider = hit.collider;
                if (collider == null) continue;
                var capsule = collider.GetComponentInParent<MoonDustCanisterView>() != null;
                skip = capsule ||
                       collider.gameObject.layer == BeltState.PassLayer ||                    // walked over: sunk into the ground
                       IsJunction(collider) ||                                               // splitters and mergers stand on the ground too
                       (views != null && views.BeltAt(collider) != 0);
                if (!capsule) Skip[id] = skip;   // capsules move about: never remembered
            }
            if (skip) continue;
            // The surface nearest the point (looking from above, something overhead could be hit first).
            var d = hit.distance - maxRise;
            if (Math.Abs(d) < Math.Abs(best)) best = d;
        }
        if (best == float.MaxValue) return false;
        drop = best;
        return true;
    }

    private static Il2CppStructArray<RaycastHit>? _hits;
    private static readonly Dictionary<ulong, bool> Skip = new();
    private static int _skipVersion = -1;

    // Part of a splitter's or merger's view (their roots are named "Splitter <id>" / "Merger <id>").
    private static bool IsJunction(Collider collider)
    {
        var name = collider.transform.root.name;
        return name.StartsWith("Splitter ", StringComparison.Ordinal) || name.StartsWith("Merger ", StringComparison.Ordinal);
    }

    /// <summary>
    /// How far the ground is below the belt's surface every <paramref name="step"/> metres along it (index i is at
    /// i x step; the last sample is at the end). Where nothing is found within <paramref name="maxDrop"/>, the value
    /// is <paramref name="maxDrop"/>. Sample with <see cref="AtDistance"/>.
    /// </summary>
    public static float[] GroundProfile(MoonConveyorPath path, float step = 0.5f, float maxDrop = 30f) =>
        GroundProfile(path, step, maxDrop, 0f);

    /// <summary>
    /// How far the ground is below the belt's surface every <paramref name="step"/> metres along it (index i is at
    /// i x step; the last sample is at the end); negative where the ground is above it (up to
    /// <paramref name="maxRise"/>). Where nothing is found, the value is <paramref name="maxDrop"/>. Sample with
    /// <see cref="AtDistance"/>.
    /// </summary>
    public static float[] GroundProfile(MoonConveyorPath path, float step, float maxDrop, float maxRise)
    {
        var count = Math.Max(2, (int)Math.Ceiling(path.Length / step) + 1);
        var drops = new float[count];
        for (var i = 0; i < count; i++)
            drops[i] = GroundBelow(path, Math.Min(i * step, path.Length), 0.05f, out var d, maxDrop, maxRise) ? d - 0.05f : maxDrop;
        return drops;
    }

    /// <summary>A value from a <see cref="GroundProfile"/> at a distance along the belt, interpolated.</summary>
    public static float AtDistance(float[] profile, float step, float distance)
    {
        if (profile.Length == 0) return 0f;
        var f = VMath.Clamp(distance / step, 0f, profile.Length - 1);
        var i = Math.Min((int)f, profile.Length - 2);
        return i < 0 ? profile[0] : VMath.Lerp(profile[i], profile[i + 1], f - i);
    }

    /// <summary>The belt's frame at a distance along it: position on the belt's surface, and its directions.</summary>
    public static bool Frame(int belt, float distance, out Vector3 position, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        position = forward = right = up = default;
        var path = Belts.PathOf(belt);
        if (path == null) return false;
        return Frame(path, distance, out position, out forward, out right, out up);
    }

    /// <summary>The same along a path.</summary>
    public static bool Frame(MoonConveyorPath path, float distance, out Vector3 position, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        path.Frame(VMath.Clamp(distance, 0f, path.Length), out position, out forward, out right, out up);
        return true;
    }

    /// <summary>Clear, double-sided glass (URP Lit, transparent) with a tint; alpha sets how see-through it is.</summary>
    public static Material Glass(Color tint)
    {
        if (GlassCache.TryGetValue(tint, out var cached) && cached != null && !cached.WasCollected) return cached;
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MSML glass" };
        m.SetFloat("_Surface", 1);
        m.SetFloat("_Blend", 0);
        m.SetFloat("_Cull", 0);
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.SetFloat("_Smoothness", 0.95f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.SetColor("_BaseColor", tint);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return GlassCache[tint] = m;
    }

    /// <summary>A glowing material. Recolour single renderers with <see cref="SetGlow"/>.</summary>
    public static Material Glow(Color color, float intensity = 2f)
    {
        var key = color * intensity;
        if (GlowCache.TryGetValue(key, out var cached) && cached != null && !cached.WasCollected) return cached;
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MSML glow" };
        m.SetColor("_BaseColor", color);
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        m.SetColor("_EmissionColor", color * intensity);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return GlowCache[key] = m;
    }

    /// <summary>Recolours one renderer using a <see cref="Glow"/> material, without new materials.</summary>
    public static void SetGlow(Renderer renderer, Color color, float intensity = 2f)
    {
        _block ??= new MaterialPropertyBlock();
        if (_baseColorId == 0) { _baseColorId = Shader.PropertyToID("_BaseColor"); _emissionId = Shader.PropertyToID("_EmissionColor"); }
        _block.Clear();
        _block.SetColor(_baseColorId, color);
        _block.SetColor(_emissionId, color * intensity);
        renderer.SetPropertyBlock(_block);
    }

    // Property ids (passing names marshals a new string into the game every call).
    private static int _baseColorId, _emissionId, _baseMapId, _emissionMapId;

    /// <summary>
    /// A glowing material coloured by a palette: a texture 1 pixel high (point-filtered), each vertex's u coordinate
    /// picking its colour. Many renderers share it (so they're drawn together) while showing different colours:
    /// recolour one by changing its mesh's u coordinates (<see cref="SetPaletteU"/>), not its material. Per-renderer
    /// material changes (property blocks) keep renderers from being drawn together, which costs a lot with thousands.
    /// </summary>
    public static Material PaletteGlow(Texture2D palette, float intensity = 2f)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MSML palette glow" };
        m.SetColor("_BaseColor", Color.white);
        m.SetTexture("_BaseMap", palette);
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        m.SetTexture("_EmissionMap", palette);
        m.SetColor("_EmissionColor", Color.white * intensity);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return m;
    }

    /// <summary>The u coordinate of a palette's pixel (see <see cref="PaletteGlow"/>).</summary>
    public static float PaletteU(int pixel, int width) => (pixel + 0.5f) / width;

    /// <summary>Gives every vertex of the mesh the same u (one palette colour; see <see cref="PaletteGlow"/>).</summary>
    public static void SetPaletteU(Mesh mesh, float u)
    {
        var uv = new Il2CppStructArray<Vector2>(mesh.vertexCount);
        var span = uv.AsSpan();
        for (var i = 0; i < span.Length; i++) span[i] = new Vector2(u, 0.5f);
        mesh.uv = uv;
    }

    /// <summary>Sets a mesh's u coordinates: vertices in consecutive runs of <paramref name="run"/>, run i getting u[i].</summary>
    public static void SetPaletteU(Mesh mesh, IReadOnlyList<float> u, int run)
    {
        var uv = new Il2CppStructArray<Vector2>(mesh.vertexCount);
        var span = uv.AsSpan();
        for (var i = 0; i < span.Length; i++) span[i] = new Vector2(u[Math.Min(u.Count - 1, i / run)], 0.5f);
        mesh.uv = uv;
    }

    /// <summary>
    /// Colours parts of one renderer using a <see cref="Glow"/> material, each its own colour: the mesh's u coordinate
    /// picks a pixel of <paramref name="strip"/> (a texture <paramref name="colors"/> wide and 1 high, point-filtered),
    /// which is set to the colours. E.g. many rings in one mesh, each showing its own colour, instead of a renderer each.
    /// Prefer <see cref="PaletteGlow"/> for many renderers: this gives each renderer its own material settings.
    /// </summary>
    public static void SetGlowColors(Renderer renderer, Texture2D strip, Color[] colors, float intensity = 2f)
    {
        _block ??= new MaterialPropertyBlock();
        if (_baseColorId == 0) { _baseColorId = Shader.PropertyToID("_BaseColor"); _emissionId = Shader.PropertyToID("_EmissionColor"); }
        if (_baseMapId == 0) { _baseMapId = Shader.PropertyToID("_BaseMap"); _emissionMapId = Shader.PropertyToID("_EmissionMap"); }
        strip.SetPixels(new Il2CppStructArray<Color>(colors));
        strip.Apply(false);
        _block.Clear();
        _block.SetColor(_baseColorId, Color.white);
        _block.SetColor(_emissionId, Color.white * intensity);
        _block.SetTexture(_baseMapId, strip);
        _block.SetTexture(_emissionMapId, strip);
        renderer.SetPropertyBlock(_block);
    }

    /// <summary>
    /// One of the game's conveyor materials by name, e.g. "MC - Graphite chassis", "MC - Brushed joint hardware",
    /// "MC - Tangerine safety trim", "MC - Porcelain ivory polymer", "MC - Cyan frosted diffuser". Null if not found.
    /// </summary>
    public static Material? GameMaterial(string name)
    {
        if (GameCache.TryGetValue(name, out var cached) && cached != null && !cached.WasCollected) return cached;
        foreach (var kit in Resources.FindObjectsOfTypeAll<MoonConveyorKit>())
            foreach (var m in kit.materials)
                if (m != null && m.name == name) return GameCache[name] = m;
        return null;
    }

    /// <summary>
    /// Any loaded material by name, e.g. the ground's ("MoonSurface") or rocks' ("MoonRock"). Null if not found.
    /// Searches everything loaded the first time, so look up once (e.g. when styling) rather than every frame.
    /// </summary>
    public static Material? SceneMaterial(string name)
    {
        if (GameCache.TryGetValue("scene:" + name, out var cached) && cached != null && !cached.WasCollected) return cached;
        if (SceneMiss.TryGetValue(name, out var retry) && Time.unscaledTime < retry) return null;
        foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            if (m != null && m.name == name) return GameCache["scene:" + name] = m;
        SceneMiss[name] = Time.unscaledTime + 10f;
        return null;
    }

    private static readonly Dictionary<string, float> SceneMiss = new();

    /// <summary>
    /// A mesh from world-space vertices, in the local space of <paramref name="space"/>. Without normals they're worked
    /// out from the triangles (smooth).
    /// </summary>
    public static Mesh WorldMesh(Transform space, IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector3>? normals, IReadOnlyList<int> triangles,
        string name = "MSML mesh", IReadOnlyList<Vector2>? uvs = null)
    {
        var toLocal = space.worldToLocalMatrix;
        var local = new List<Vector3>(vertices.Count);
        for (var i = 0; i < vertices.Count; i++) local.Add(VMath.Point(toLocal, vertices[i]));
        var mesh = new Mesh { name = name };
        if (local.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = ToIl2Cpp(local);
        if (uvs != null) mesh.uv = ToIl2Cpp(new List<Vector2>(uvs));
        var tris = new Il2CppStructArray<int>(triangles.Count);
        var span = tris.AsSpan();
        for (var i = 0; i < triangles.Count; i++) span[i] = triangles[i];
        mesh.SetTriangles(tris, 0);
        if (normals != null)
        {
            var localNormals = new List<Vector3>(normals.Count);
            for (var i = 0; i < normals.Count; i++) localNormals.Add(VMath.Direction(toLocal, normals[i]));
            mesh.normals = ToIl2Cpp(localNormals);
        }
        else mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // One copy into a game array (adding items to a game list one by one was a call into the game per vertex).
    private static Il2CppStructArray<T> ToIl2Cpp<T>(List<T> list) where T : unmanaged
    {
        var result = new Il2CppStructArray<T>(list.Count);
        var span = result.AsSpan();
        for (var i = 0; i < list.Count; i++) span[i] = list[i];
        return result;
    }
}
