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
        for (var i = 0; i < rings; i++)
        {
            var d = from + (to - from) * i / (rings - 1);
            path.Frame(d, out var position, out _, out var right, out var up);
            var centre = position + up * (liftAt?.Invoke(d) ?? lift) + right * side;
            for (var s = 0; s <= sides; s++)
            {
                var a = s * Mathf.PI * 2f / sides;
                var outward = right * Mathf.Cos(a) + up * Mathf.Sin(a);
                vertices.Add(space.InverseTransformPoint(centre + outward * radius));
                normals.Add(space.InverseTransformDirection(outward));
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
            var centre = position + up * (liftAt?.Invoke(d) ?? lift) + right * side;
            var first = vertices.Count;
            var normal = space.InverseTransformDirection(forward * facing);
            vertices.Add(space.InverseTransformPoint(centre));
            normals.Add(normal);
            uvs.Add(new Vector2(0.5f, 0.5f));
            for (var s = 0; s <= sides; s++)
            {
                var a = s * Mathf.PI * 2f / sides;
                vertices.Add(space.InverseTransformPoint(centre + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius));
                normals.Add(normal);
                uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
            }
            for (var s = 0; s < sides; s++)
            {
                if (facing > 0) triangles.AddRange(new[] { first, first + 2 + s, first + 1 + s });
                else triangles.AddRange(new[] { first, first + 1 + s, first + 2 + s });
            }
        }

        var mesh = new Mesh { name = $"Belt {belt} tube" };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(ToIl2Cpp(vertices));
        mesh.SetNormals(ToIl2Cpp(normals));
        mesh.SetUVs(0, ToIl2Cpp(uvs));
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
        for (var i = 0; i < rows; i++)
        {
            var d = from + (to - from) * i / (rows - 1);
            path.Frame(d, out var position, out _, out var right, out var up);
            var centre = position + up * liftAt(d);
            foreach (var s in new[] { -1f, 1f })
            {
                vertices.Add(space.InverseTransformPoint(centre + right * (halfWidth * s)));
                normals.Add(space.InverseTransformDirection(up));
                uvs.Add(new Vector2((s + 1f) / 2f, d));
            }
            if (i == 0) continue;
            int a = (i - 1) * 2, b = a + 1, c = a + 2, e = a + 3;
            triangles.AddRange(new[] { a, c, b, b, c, e });
        }
        var mesh = new Mesh { name = "Belt strip" };
        mesh.SetVertices(ToIl2Cpp(vertices));
        mesh.SetNormals(ToIl2Cpp(normals));
        mesh.SetUVs(0, ToIl2Cpp(uvs));
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
            var facing = Vector3.Cross(vertices[triangles[t + 1]] - a, vertices[triangles[t + 2]] - a);
            score += Vector3.Dot(facing, normals[triangles[t]] + normals[triangles[t + 1]] + normals[triangles[t + 2]]);
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
    /// How far below a point on the belt (<paramref name="lift"/> metres above its surface, at
    /// <paramref name="distance"/> along it) the first solid thing is: ground, a foundation, a building. Belts and
    /// capsules are skipped. Use it to stand your own supports on (with <see cref="BeltType.HideBelt"/>).
    /// </summary>
    public static bool GroundBelow(int belt, float distance, float lift, out float drop, float maxDrop = 30f)
    {
        drop = 0f;
        var path = Belts.PathOf(belt);
        return path != null && GroundBelow(path, distance, lift, out drop, maxDrop);
    }

    /// <summary>The same along a path (e.g. a planned belt).</summary>
    public static bool GroundBelow(MoonConveyorPath path, float distance, float lift, out float drop, float maxDrop = 30f)
    {
        drop = 0f;
        if (!Frame(path, distance, out var position, out _, out _, out var up)) return false;
        var from = position + up * lift;
        var views = Belts.World?.ConveyorViews;
        var best = float.MaxValue;
        foreach (var hit in Physics.RaycastAll(from, -up, maxDrop, ~0, QueryTriggerInteraction.Ignore))
        {
            var collider = hit.collider;
            if (collider == null || hit.distance >= best) continue;
            if (views != null && views.BeltAt(collider) != 0) continue;
            if (collider.GetComponentInParent<MoonDustCanisterView>() != null) continue;
            best = hit.distance;
        }
        if (best == float.MaxValue) return false;
        drop = best;
        return true;
    }

    /// <summary>
    /// How far the ground is below the belt's surface every <paramref name="step"/> metres along it (index i is at
    /// i x step; the last sample is at the end). Where nothing is found within <paramref name="maxDrop"/>, the value
    /// is <paramref name="maxDrop"/>. Sample with <see cref="AtDistance"/>.
    /// </summary>
    public static float[] GroundProfile(MoonConveyorPath path, float step = 0.5f, float maxDrop = 30f)
    {
        var count = Math.Max(2, (int)Math.Ceiling(path.Length / step) + 1);
        var drops = new float[count];
        for (var i = 0; i < count; i++)
            drops[i] = GroundBelow(path, Math.Min(i * step, path.Length), 0.05f, out var d, maxDrop) ? d - 0.05f : maxDrop;
        return drops;
    }

    /// <summary>A value from a <see cref="GroundProfile"/> at a distance along the belt, interpolated.</summary>
    public static float AtDistance(float[] profile, float step, float distance)
    {
        if (profile.Length == 0) return 0f;
        var f = Mathf.Clamp(distance / step, 0f, profile.Length - 1);
        var i = Math.Min((int)f, profile.Length - 2);
        return i < 0 ? profile[0] : Mathf.Lerp(profile[i], profile[i + 1], f - i);
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
        path.Frame(Mathf.Clamp(distance, 0f, path.Length), out position, out forward, out right, out up);
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
        _block.Clear();
        _block.SetColor("_BaseColor", color);
        _block.SetColor("_EmissionColor", color * intensity);
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

    private static Il2CppSystem.Collections.Generic.List<T> ToIl2Cpp<T>(List<T> list) where T : unmanaged
    {
        var result = new Il2CppSystem.Collections.Generic.List<T>(list.Count);
        foreach (var item in list) result.Add(item);
        return result;
    }
}
