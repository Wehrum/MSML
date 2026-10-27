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
    private static readonly System.Random Random = new();
    private static float Rand() => (float)Random.NextDouble() * 2f - 1f;

    private readonly Transform _parent;
    private readonly Vector3 _localPosition, _localDirection;
    // Live puffs (world space), drawn as camera-facing quads in one mesh: one object and draw call per stream, and
    // arrays reused every frame (a GameObject and mesh per puff, recoloured every frame, filled the game's heap).
    private readonly List<(Vector3 Position, Vector3 Velocity, float Born)> _live = new();
    private float _owed;
    private GameObject? _object;
    private Mesh? _mesh;
    private MeshRenderer? _renderer;
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>? _vertices;
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color>? _colors;
    private int _capacity;

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
        if (All.Count == 0) return;
        var camera = Camera.main;
        var dt = Time.deltaTime;
        Vector3 eye = default, right = Vector3.right, up = Vector3.up;
        if (camera != null)
        {
            var t = camera.transform;
            eye = t.position; right = t.right; up = t.up;
        }
        var now = Time.time;
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var p = All[i];
            if (p._parent == null || p._parent.WasCollected) { p.Dispose(); All.RemoveAt(i); continue; }
            p.Tick(camera != null, eye, right, up, dt, now);
        }
    }

    // Where the stream comes from and whether its parent is shown: looked up a few times a second (parents don't
    // move; every call into the game leaves a little garbage on its heap).
    private Vector3 _origin, _direction;
    private bool _parentShown, _shown;
    private float _nextLook = -1f;

    private void Tick(bool hasCamera, Vector3 eye, Vector3 right, Vector3 up, float dt, float now)
    {
        if (now >= _nextLook || _nextLook < 0f)
        {
            _nextLook = now + 0.25f;
            _origin = _parent.TransformPoint(_localPosition);
            _direction = _parent.TransformDirection(_localDirection);
            _parentShown = _parent.gameObject.activeInHierarchy;
        }
        var origin = _origin;
        var near = hasCamera && VMath.SqrDistance(eye, origin) < VisibleDistance * VisibleDistance;
        if (!near || !_parentShown)
        {
            if (_shown) Hide();
            _owed = 0f;
            return;
        }

        var capacity = Math.Max(4, (int)Math.Ceiling(Rate * Lifetime * 1.3f) + 2);
        if (_mesh == null || _mesh.WasCollected || capacity > _capacity) Build(capacity, origin);

        _owed += Rate * dt;
        while (_owed >= 1f)
        {
            _owed -= 1f;
            if (_live.Count >= _capacity) continue;
            var spread = new Vector3(Rand(), Rand(), Rand());
            var velocity = VMath.Mul(VMath.Normalize(VMath.Mad(_direction, spread, 0.25f)), Speed * (0.6f + 0.4f * (float)Random.NextDouble()));
            _live.Add((origin, velocity, now));
        }

        var vertices = _vertices!.AsSpan();
        var colors = _colors!.AsSpan();
        var n = 0;
        for (var i = _live.Count - 1; i >= 0; i--)
        {
            var (position, velocity, born) = _live[i];
            var age = (now - born) / Lifetime;
            if (age >= 1f) { _live.RemoveAt(i); continue; }
            position = VMath.Mad(position, velocity, dt * (1f - age * 0.6f));
            _live[i] = (position, velocity, born);
            var half = Size * (0.5f + 1.1f * age) * 0.5f;
            var alpha = Color.a * (age < 0.15f ? age / 0.15f : 1f - (age - 0.15f) / 0.85f);
            var colour = new Color(Color.r, Color.g, Color.b, alpha);
            var v = n * 4;
            vertices[v] = VMath.Mad(position, right, -half, up, -half); vertices[v + 1] = VMath.Mad(position, right, half, up, -half);
            vertices[v + 2] = VMath.Mad(position, right, -half, up, half); vertices[v + 3] = VMath.Mad(position, right, half, up, half);
            colors[v] = colour; colors[v + 1] = colour; colors[v + 2] = colour; colors[v + 3] = colour;
            n++;
        }
        // Unused quads: nothing to draw.
        for (var v = n * 4; v < vertices.Length; v++) { vertices[v] = origin; colors[v] = default; }
        _mesh!.vertices = _vertices;
        _mesh.colors = _colors;
        if (!_shown) { _shown = true; _renderer!.enabled = true; }
    }
    // The stream's object (in world space, so the quads are written in world coordinates) and its fixed mesh layout.
    private void Build(int capacity, Vector3 origin)
    {
        if (_object == null || _object.WasCollected)
        {
            _object = new GameObject("MSML puffs");
            _object.AddComponent<MeshFilter>();
            _renderer = _object.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = Material();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }
        if (_mesh != null && !_mesh.WasCollected) UnityEngine.Object.Destroy(_mesh);
        _capacity = capacity;
        _mesh = new Mesh { name = "MSML puffs" };
        _mesh.MarkDynamic();
        _vertices = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(capacity * 4);
        _colors = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color>(capacity * 4);
        var uvs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector2>(capacity * 4);
        var triangles = new int[capacity * 6];
        for (var q = 0; q < capacity; q++)
        {
            uvs[q * 4] = new Vector2(0f, 0f); uvs[q * 4 + 1] = new Vector2(1f, 0f);
            uvs[q * 4 + 2] = new Vector2(0f, 1f); uvs[q * 4 + 3] = new Vector2(1f, 1f);
            var v = q * 4;
            triangles[q * 6] = v; triangles[q * 6 + 1] = v + 2; triangles[q * 6 + 2] = v + 1;
            triangles[q * 6 + 3] = v + 1; triangles[q * 6 + 4] = v + 2; triangles[q * 6 + 5] = v + 3;
        }
        _mesh.vertices = _vertices;
        _mesh.uv = uvs;
        _mesh.colors = _colors;
        _mesh.SetTriangles(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(triangles), 0);
        // Fixed bounds around the outlet, as far as a puff can travel and grow.
        var reach = Speed * Lifetime + Size * 2f + 0.5f;
        _mesh.bounds = new Bounds(origin, Vector3.one * reach * 2f);
        _object.GetComponent<MeshFilter>().sharedMesh = _mesh;
    }

    private void Hide()
    {
        _live.Clear();
        _shown = false;
        if (_renderer != null && !_renderer.WasCollected) _renderer.enabled = false;
    }

    private void Dispose()
    {
        _live.Clear();
        if (_mesh != null && !_mesh.WasCollected) UnityEngine.Object.Destroy(_mesh);
        if (_object != null && !_object.WasCollected) UnityEngine.Object.Destroy(_object);
        _mesh = null;
        _object = null;
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
