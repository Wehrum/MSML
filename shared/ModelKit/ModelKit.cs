using System;
using System.Collections.Generic;

namespace MoonSweepMods.ModelKit;

/// <summary>A model design. tools/ModelPreview renders every design it's compiled with.</summary>
public interface IModelDesign
{
    string Id { get; }
    Model Build();
}

// Engine-independent procedural modelling: designs build triangle meshes here, then the mod turns them into Unity
// meshes and tools/ModelPreview renders them offline. Coordinates match Unity model space: x right, y up, z forward,
// metres.

public readonly struct V3
{
    public readonly float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static readonly V3 Zero = new(0, 0, 0);
    public static readonly V3 Up = new(0, 1, 0);
    public static readonly V3 Right = new(1, 0, 0);
    public static readonly V3 Forward = new(0, 0, 1);
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static V3 operator *(float s, V3 a) => a * s;
    public static V3 operator /(V3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);
    public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public V3 Normalized { get { var l = Length; return l > 1e-8f ? this / l : Zero; } }
    public static V3 Lerp(V3 a, V3 b, float t) => a + (b - a) * t;
    public V3 MirrorX => new(-X, Y, Z);
    public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

/// <summary>Rotation matrix (rows are the rotated basis applied to local vectors).</summary>
public readonly struct M3
{
    public readonly V3 Ax, Ay, Az; // images of local x, y, z
    public M3(V3 ax, V3 ay, V3 az) { Ax = ax; Ay = ay; Az = az; }
    public static readonly M3 Identity = new(V3.Right, V3.Up, V3.Forward);
    public V3 Apply(V3 v) => Ax * v.X + Ay * v.Y + Az * v.Z;

    /// <summary>Unity order: Z, then X, then Y (degrees).</summary>
    public static M3 Euler(float x, float y, float z) => RotY(y) * RotX(x) * RotZ(z);
    public static M3 RotX(float deg) { var r = deg * MathF.PI / 180; float c = MathF.Cos(r), s = MathF.Sin(r); return new(V3.Right, new V3(0, c, s), new V3(0, -s, c)); }
    public static M3 RotY(float deg) { var r = deg * MathF.PI / 180; float c = MathF.Cos(r), s = MathF.Sin(r); return new(new V3(c, 0, -s), V3.Up, new V3(s, 0, c)); }
    public static M3 RotZ(float deg) { var r = deg * MathF.PI / 180; float c = MathF.Cos(r), s = MathF.Sin(r); return new(new V3(c, s, 0), new V3(-s, c, 0), V3.Forward); }
    public static M3 operator *(M3 a, M3 b) => new(a.Apply(b.Ax), a.Apply(b.Ay), a.Apply(b.Az));

    /// <summary>A frame whose local y points along <paramref name="axis"/>.</summary>
    public static M3 AlongY(V3 axis)
    {
        var y = axis.Normalized;
        var helper = MathF.Abs(y.Y) < 0.95f ? V3.Up : V3.Forward;
        var x = V3.Cross(helper, y).Normalized;
        var z = V3.Cross(x, y).Normalized;
        return new(x, y, z);
    }
}

public sealed class MeshData
{
    public readonly List<V3> Vertices = new();
    public readonly List<V3> Normals = new();
    public readonly List<int> Triangles = new();

    public void Quad(V3 a, V3 b, V3 c, V3 d) // counter-clockwise when viewed from outside (Unity: clockwise front faces handled at export)
    {
        var n = V3.Cross(b - a, c - a).Normalized;
        var i = Vertices.Count;
        Vertices.Add(a); Vertices.Add(b); Vertices.Add(c); Vertices.Add(d);
        Normals.Add(n); Normals.Add(n); Normals.Add(n); Normals.Add(n);
        Triangles.Add(i); Triangles.Add(i + 1); Triangles.Add(i + 2);
        Triangles.Add(i); Triangles.Add(i + 2); Triangles.Add(i + 3);
    }

    public void Tri(V3 a, V3 b, V3 c)
    {
        var n = V3.Cross(b - a, c - a).Normalized;
        var i = Vertices.Count;
        Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
        Normals.Add(n); Normals.Add(n); Normals.Add(n);
        Triangles.Add(i); Triangles.Add(i + 1); Triangles.Add(i + 2);
    }

    /// <summary>Smooth-shaded strip quad with explicit per-vertex normals.</summary>
    public void SmoothQuad(V3 a, V3 b, V3 c, V3 d, V3 na, V3 nb, V3 nc, V3 nd)
    {
        var i = Vertices.Count;
        Vertices.Add(a); Vertices.Add(b); Vertices.Add(c); Vertices.Add(d);
        Normals.Add(na); Normals.Add(nb); Normals.Add(nc); Normals.Add(nd);
        Triangles.Add(i); Triangles.Add(i + 1); Triangles.Add(i + 2);
        Triangles.Add(i); Triangles.Add(i + 2); Triangles.Add(i + 3);
    }
}

/// <summary>A named node: either static geometry, or something the game animates (wheels, rollers).</summary>
public sealed class ModelGroup
{
    public string Name { get; }
    /// <summary>Pivot in model space. Geometry is stored relative to it.</summary>
    public V3 Pivot { get; }
    public readonly Dictionary<string, MeshData> ByMaterial = new();

    public ModelGroup(string name, V3 pivot) { Name = name; Pivot = pivot; }

    public MeshData Mesh(string material)
    {
        if (!ByMaterial.TryGetValue(material, out var m)) ByMaterial[material] = m = new MeshData();
        return m;
    }

    public int TriangleCount { get { var n = 0; foreach (var m in ByMaterial.Values) n += m.Triangles.Count / 3; return n; } }
}

public sealed class Model
{
    public string Name { get; }
    public readonly List<ModelGroup> Groups = new();
    public readonly Dictionary<string, Rgba> Palette = new();
    public readonly Dictionary<string, V3> Points = new();   // named anchors (driver seat, head, ...)
    public readonly Dictionary<string, float> Values = new(); // named numbers (wheel radius, cleaning width, ...)

    public Model(string name) { Name = name; }

    public ModelGroup Group(string name, V3 pivot)
    {
        var g = new ModelGroup(name, pivot);
        Groups.Add(g);
        return g;
    }

    public ModelGroup? Find(string name) => Groups.Find(g => g.Name == name);
}

public readonly struct Rgba
{
    public readonly float R, G, B, A;
    public readonly float Metallic, Smoothness, Emission;
    public Rgba(float r, float g, float b, float a = 1, float metallic = 0, float smoothness = 0.3f, float emission = 0)
    { R = r; G = g; B = b; A = a; Metallic = metallic; Smoothness = smoothness; Emission = emission; }
    public static Rgba Hex(string hex, float a = 1, float metallic = 0, float smoothness = 0.3f, float emission = 0)
    {
        hex = hex.TrimStart('#');
        return new(Convert.ToInt32(hex[..2], 16) / 255f, Convert.ToInt32(hex[2..4], 16) / 255f, Convert.ToInt32(hex[4..6], 16) / 255f, a, metallic, smoothness, emission);
    }
}

/// <summary>Primitive builders. Everything is added to a group's mesh for one material, in the group's local space.</summary>
public sealed class Shapes
{
    private readonly ModelGroup _group;
    private readonly V3 _origin;
    private readonly M3 _frame;

    public Shapes(ModelGroup group) : this(group, V3.Zero, M3.Identity) { }
    private Shapes(ModelGroup group, V3 origin, M3 frame) { _group = group; _origin = origin; _frame = frame; }

    /// <summary>Nested coordinate frame: positions/rotations are relative to this one.</summary>
    public Shapes At(V3 position, M3? rotation = null) => new(_group, P(position), rotation.HasValue ? _frame * rotation.Value : _frame);

    private V3 P(V3 local) => _origin + _frame.Apply(local);   // model space, relative to the group pivot
    private V3 D(V3 local) => _frame.Apply(local);

    public Shapes Box(string mat, V3 center, V3 size, M3? rot = null)
    {
        var r = rot ?? M3.Identity;
        var h = size / 2;
        V3 C(float x, float y, float z) => P(center + r.Apply(new V3(x * h.X, y * h.Y, z * h.Z)));
        Hexahedron(mat, C(-1, -1, -1), C(1, -1, -1), C(1, 1, -1), C(-1, 1, -1), C(-1, -1, 1), C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1));
        return this;
    }

    /// <summary>Box whose top face is narrower/shorter: a wedge or hood. Taper is the top size (x, z).</summary>
    public Shapes Tapered(string mat, V3 center, V3 size, float topX, float topZ, float topShiftZ = 0, M3? rot = null)
    {
        var r = rot ?? M3.Identity;
        var h = size / 2;
        V3 B(float x, float z) => P(center + r.Apply(new V3(x * h.X, -h.Y, z * h.Z)));
        V3 T(float x, float z) => P(center + r.Apply(new V3(x * topX / 2, h.Y, z * topZ / 2 + topShiftZ)));
        Hexahedron(mat, B(-1, -1), B(1, -1), T(1, -1), T(-1, -1), B(-1, 1), B(1, 1), T(1, 1), T(-1, 1));
        return this;
    }

    /// <summary>Generic 8-corner solid. Corners: back face (z-) bl, br, tr, tl, then front face (z+) bl, br, tr, tl.</summary>
    public void Hexahedron(string mat, V3 b0, V3 b1, V3 b2, V3 b3, V3 f0, V3 f1, V3 f2, V3 f3)
    {
        var m = _group.Mesh(mat);
        m.Quad(b0, b3, b2, b1); // back
        m.Quad(f0, f1, f2, f3); // front
        m.Quad(b0, f0, f3, b3); // left
        m.Quad(b1, b2, f2, f1); // right
        m.Quad(b3, f3, f2, b2); // top
        m.Quad(b0, b1, f1, f0); // bottom
    }

    /// <summary>Prism around an axis: sides=6 gives a hex prism, 24+ a cylinder. Axis goes from a to b.</summary>
    public Shapes Prism(string mat, V3 a, V3 b, float radius, int sides, bool smooth = false, float phaseDeg = 0, float radiusB = -1, bool caps = true)
    {
        var m = _group.Mesh(mat);
        var pa = P(a); var pb = P(b);
        var f = M3.AlongY(D(b - a));
        var rb = radiusB < 0 ? radius : radiusB;
        var ringA = new V3[sides]; var ringB = new V3[sides]; var nrm = new V3[sides];
        for (var i = 0; i < sides; i++)
        {
            var ang = (phaseDeg * MathF.PI / 180) + i * 2 * MathF.PI / sides;
            var dir = f.Ax * MathF.Cos(ang) + f.Az * MathF.Sin(ang);
            ringA[i] = pa + dir * radius;
            ringB[i] = pb + dir * rb;
            nrm[i] = dir;
        }
        for (var i = 0; i < sides; i++)
        {
            var j = (i + 1) % sides;
            if (smooth) m.SmoothQuad(ringA[i], ringB[i], ringB[j], ringA[j], nrm[i], nrm[i], nrm[j], nrm[j]);
            else m.Quad(ringA[i], ringB[i], ringB[j], ringA[j]);
        }
        if (caps)
        {
            for (var i = 1; i < sides - 1; i++)
            {
                if (radius > 0) m.Tri(ringA[0], ringA[i], ringA[i + 1]);
                if (rb > 0) m.Tri(ringB[0], ringB[i + 1], ringB[i]);
            }
        }
        return this;
    }

    public Shapes Cylinder(string mat, V3 a, V3 b, float radius, int sides = 24) => Prism(mat, a, b, radius, sides, true);

    /// <summary>Hex prism standing on its base: center of the bottom face, radius to corners.</summary>
    public Shapes HexColumn(string mat, V3 baseCenter, float radius, float height, float phaseDeg = 30, float topRadius = -1) =>
        Prism(mat, baseCenter, baseCenter + V3.Up * height, radius, 6, false, phaseDeg, topRadius);

    public Shapes Rod(string mat, V3 a, V3 b, float radius, int sides = 8) => Prism(mat, a, b, radius, sides, true);

    /// <summary>Low-poly sphere.</summary>
    public Shapes Sphere(string mat, V3 center, float radius, int rings = 8, int segments = 12)
    {
        var m = _group.Mesh(mat);
        var c = P(center);
        V3 Pt(int r, int s)
        {
            var phi = MathF.PI * r / rings;
            var th = 2 * MathF.PI * s / segments;
            return new V3(MathF.Sin(phi) * MathF.Cos(th), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(th));
        }
        for (var r = 0; r < rings; r++)
        for (var s = 0; s < segments; s++)
        {
            V3 a = Pt(r, s), b = Pt(r + 1, s), cc = Pt(r + 1, s + 1), d = Pt(r, s + 1);
            m.SmoothQuad(c + D(a) * radius, c + D(d) * radius, c + D(cc) * radius, c + D(b) * radius, D(a), D(d), D(cc), D(b));
        }
        return this;
    }

    /// <summary>Ring of boxes around an axis (tyre treads, bristles, bolts).</summary>
    public Shapes Radial(string mat, V3 center, V3 axis, int count, float radius, V3 blockSize, float phaseDeg = 0)
    {
        var f = M3.AlongY(axis);
        for (var i = 0; i < count; i++)
        {
            var ang = phaseDeg + i * 360f / count;
            var rad = ang * MathF.PI / 180;
            var dir = f.Ax * MathF.Cos(rad) + f.Az * MathF.Sin(rad);
            var tangent = V3.Cross(dir, f.Ay);
            var rot = new M3(tangent, dir, f.Ay); // right-handed: block x along tangent, y radial, z along axis
            Box(mat, center + dir * radius, blockSize, rot);
        }
        return this;
    }
}
