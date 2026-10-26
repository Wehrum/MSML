using System;
using System.Collections.Generic;
using UnityEngine;

namespace MSML;

/// <summary>
/// A soft stream of puffs (air, steam, dust) blowing from a point, e.g. a vent on a machine. Made with
/// <see cref="Create"/>; it follows its parent, stops when the parent is gone or hidden, and is only animated near
/// the camera. (The game's particle system can't be set up from mods: most of its settings are stripped.)
/// </summary>
public sealed class Puffs
{
    private const float VisibleDistance = 120f;
    private static readonly List<Puffs> All = new();
    private static Material? _material;
    private static bool _ticking;

    private readonly Transform _parent;
    private readonly Vector3 _localPosition, _localDirection;
    private readonly List<(Transform Puff, MeshRenderer Renderer, Vector3 Velocity, float Born)> _live = new();
    private float _owed;

    /// <summary>Puffs per second (0 stops it; puffs already out fade away).</summary>
    public float Rate { get; set; }

    /// <summary>Colour and opacity of the puffs.</summary>
    public Color Color { get; set; }

    /// <summary>Speed (m/s), size (m) and lifetime (s) of each puff.</summary>
    public float Speed { get; set; }
    public float Size { get; set; }
    public float Lifetime { get; set; }

    private Puffs(Transform parent, Vector3 position, Vector3 direction)
    {
        _parent = parent;
        _localPosition = parent.InverseTransformPoint(position);
        _localDirection = parent.InverseTransformDirection(direction.normalized);
    }

    /// <summary>A stream from <paramref name="position"/> towards <paramref name="direction"/> (world space), following <paramref name="parent"/>.</summary>
    public static Puffs Create(Transform parent, Vector3 position, Vector3 direction, Color color,
        float rate = 8f, float speed = 1.4f, float size = 0.22f, float lifetime = 0.9f)
    {
        var puffs = new Puffs(parent, position, direction) { Color = color, Rate = rate, Speed = speed, Size = size, Lifetime = lifetime };
        All.Add(puffs);
        if (!_ticking) { _ticking = true; Moon.Update += TickAll; }
        return puffs;
    }

    private static void TickAll()
    {
        var camera = Camera.main;
        var dt = Time.deltaTime;
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var p = All[i];
            if (p._parent == null || p._parent.WasCollected) { p.Clear(); All.RemoveAt(i); continue; }
            p.Tick(camera, dt);
        }
    }

    private void Tick(Camera? camera, float dt)
    {
        var shown = _parent.gameObject.activeInHierarchy;
        var origin = _parent.TransformPoint(_localPosition);
        var near = camera != null && (camera.transform.position - origin).sqrMagnitude < VisibleDistance * VisibleDistance;
        if (!shown || !near) { Clear(); _owed = 0f; return; }

        _owed += Rate * dt;
        var direction = _parent.TransformDirection(_localDirection);
        while (_owed >= 1f)
        {
            _owed -= 1f;
            var spread = UnityEngine.Random.insideUnitSphere * 0.25f;
            var velocity = (direction + spread).normalized * Speed * UnityEngine.Random.Range(0.6f, 1f);
            _live.Add(Spawn(origin, velocity));
        }

        var now = Time.time;
        for (var i = _live.Count - 1; i >= 0; i--)
        {
            var (puff, renderer, velocity, born) = _live[i];
            var age = (now - born) / Lifetime;
            if (age >= 1f || puff == null || puff.WasCollected)
            {
                if (puff != null && !puff.WasCollected) Destroy(puff);
                _live.RemoveAt(i);
                continue;
            }
            puff.position += velocity * dt * (1f - age * 0.6f);
            puff.localScale = Vector3.one * Size * Mathf.Lerp(0.5f, 1.6f, age);
            if (camera != null) puff.rotation = Quaternion.LookRotation(puff.position - camera.transform.position, camera.transform.up);
            var alpha = Color.a * (age < 0.15f ? age / 0.15f : 1f - (age - 0.15f) / 0.85f);
            Tint(puff, new Color(Color.r, Color.g, Color.b, alpha));
        }
    }

    // Each puff has its own small quad; its vertex colours carry the colour and fade (the game's puff shader uses them).
    private static void Tint(Transform puff, Color color)
    {
        var mesh = puff.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null) return;
        var colors = new Il2CppSystem.Collections.Generic.List<Color>(4);
        for (var i = 0; i < 4; i++) colors.Add(color);
        mesh.SetColors(colors);
    }

    private (Transform, MeshRenderer, Vector3, float) Spawn(Vector3 at, Vector3 velocity)
    {
        var go = new GameObject("Puff");
        go.transform.position = at;
        go.AddComponent<MeshFilter>().sharedMesh = NewQuad();
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = Material();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return (go.transform, renderer, velocity, Time.time);
    }

    private static void Destroy(Transform puff)
    {
        var mesh = puff.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh != null) UnityEngine.Object.Destroy(mesh);
        UnityEngine.Object.Destroy(puff.gameObject);
    }

    private void Clear()
    {
        foreach (var (puff, _, _, _) in _live)
            if (puff != null && !puff.WasCollected) Destroy(puff);
        _live.Clear();
    }

    private static Mesh NewQuad()
    {
        var mesh = new Mesh { name = "MSML puff" };
        var vertices = new Il2CppSystem.Collections.Generic.List<Vector3>(4);
        vertices.Add(new Vector3(-0.5f, -0.5f, 0f)); vertices.Add(new Vector3(0.5f, -0.5f, 0f));
        vertices.Add(new Vector3(-0.5f, 0.5f, 0f)); vertices.Add(new Vector3(0.5f, 0.5f, 0f));
        var uvs = new Il2CppSystem.Collections.Generic.List<Vector2>(4);
        uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f)); uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(1f, 1f));
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(new[] { 0, 2, 1, 1, 2, 3 }), 0);
        return mesh;
    }

    // The game's own dust puff (soft and round, coloured by vertex colours); else unlit and transparent with a soft round texture.
    private static Material Material()
    {
        if (_material != null && !_material.WasCollected) return _material;
        foreach (var game in Resources.FindObjectsOfTypeAll<Material>())
            if (game != null && game.name == "BrushDust") return _material = game;
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "MSML puff" };
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "MSML puff", wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x - size / 2f + 0.5f, y - size / 2f + 0.5f).magnitude / (size / 2f);
                var a = Mathf.Clamp01(1f - d);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
            }
        tex.SetPixels32(pixels);
        tex.Apply();
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        m.SetTexture("_BaseMap", tex);
        m.SetTexture("_MainTex", tex);
        m.mainTexture = tex;
        m.SetFloat("_Surface", 1);
        m.SetFloat("_Blend", 0);
        m.SetFloat("_Cull", 0);
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _material = m;
    }
}
