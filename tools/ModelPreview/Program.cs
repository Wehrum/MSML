using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MoonSweepMods.ModelKit;

namespace MoonSweepMods.ModelPreview;

/// <summary>
/// Renders ModelKit designs to a PNG contact sheet (four views, ground grid, shadows, outlines) without the game.
///   dotnet run --project tools/ModelPreview -- [DesignId|all] [--out dir] [--size 1920x1080] [--view name]
/// Faces are back-face culled exactly like Unity, so inside-out geometry shows up here too.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var designs = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IModelDesign).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (IModelDesign)Activator.CreateInstance(t)!)
            .ToList();
        var id = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "all";
        var outDir = Arg(args, "--out") ?? Path.Combine(RepoRoot(), "artifacts", "previews");
        var size = (Arg(args, "--size") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        var only = Arg(args, "--view");
        Directory.CreateDirectory(outDir);

        var chosen = id == "all" ? designs : designs.Where(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0)
        {
            Console.Error.WriteLine($"No design '{id}'. Available: {string.Join(", ", designs.Select(d => d.Id))}");
            return 1;
        }
        foreach (var design in chosen)
        {
            var sw = Stopwatch.StartNew();
            var model = design.Build();
            var tris = model.Groups.Sum(g => g.TriangleCount);
            var views = Views.Standard(model);
            if (only != null) views = views.Where(v => v.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            var suffix = only != null ? "-" + string.Concat(only.Select(c => char.IsLetterOrDigit(c) ? c : '-')) : "";
            var path = Path.Combine(outDir, $"{design.Id}{suffix}.png");
            Sheet.Render(model, views, size[0], size[1], path, $"{model.Name}  ·  {tris:N0} triangles  ·  {model.Groups.Count} parts");
            Console.WriteLine($"{design.Id}: {tris:N0} triangles, {model.Groups.Count} groups -> {path} ({sw.ElapsedMilliseconds} ms)");
        }
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MoonSweepMods.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }
}

public sealed record View(string Name, V3 Direction, float Fov, bool Ortho = false, float Zoom = 1);

public static class Views
{
    public static List<View> Standard(Model m) => new()
    {
        new("front 3/4", new V3(0.75f, 0.42f, 1.0f), 28),
        new("rear 3/4", new V3(-0.85f, 0.5f, -1.0f), 28),
        new("side", new V3(1, 0.08f, 0.0f), 22, Ortho: true),
        new("top", new V3(0.0f, 1, 0.001f), 22, Ortho: true),
        new("close-up", new V3(0.9f, 0.35f, 0.75f), 28, Zoom: 2.4f),
        new("front", new V3(0.0f, 0.12f, 1), 22, Ortho: true),
    };
}

internal struct Tri
{
    public V3 A, B, C, NA, NB, NC;
    public int Mat;
    public int Id;
}

public static class Sheet
{
    public static void Render(Model model, List<View> views, int width, int height, string path, string caption)
    {
        var mats = model.Palette.Keys.ToList();
        var tris = new List<Tri>();
        var partId = 0;
        foreach (var g in model.Groups)
        {
            foreach (var (matName, data) in g.ByMaterial)
            {
                var mi = mats.IndexOf(matName);
                if (mi < 0) { mats.Add(matName); mi = mats.Count - 1; }
                partId++;
                for (var t = 0; t < data.Triangles.Count; t += 3)
                {
                    int a = data.Triangles[t], b = data.Triangles[t + 1], c = data.Triangles[t + 2];
                    tris.Add(new Tri
                    {
                        A = g.Pivot + data.Vertices[a], B = g.Pivot + data.Vertices[b], C = g.Pivot + data.Vertices[c],
                        NA = data.Normals[a], NB = data.Normals[b], NC = data.Normals[c], Mat = mi, Id = partId,
                    });
                }
            }
        }
        var palette = mats.Select(n => model.Palette.TryGetValue(n, out var c) ? c : new Rgba(1, 0, 1)).ToArray();

        var cols = views.Count <= 1 ? 1 : views.Count <= 4 ? 2 : 3;
        var rows = (views.Count + cols - 1) / cols;
        var header = views.Count == 1 ? 0 : 56;
        var cellW = width / cols;
        var cellH = (height - header) / Math.Max(1, rows);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(18, 20, 26));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI Semibold", 20, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 15, GraphicsUnit.Pixel);
            if (header > 0) g.DrawString(caption, font, Brushes.WhiteSmoke, 16, 14);
            for (var i = 0; i < views.Count; i++)
            {
                var pixels = Raster.Render(tris, palette, model, views[i], cellW, cellH);
                using var cell = ToBitmap(pixels, cellW, cellH);
                var x = (i % cols) * cellW;
                var y = header + (i / cols) * cellH;
                g.DrawImageUnscaled(cell, x, y);
                if (views.Count == 1) continue; // single view: a clean picture (used for mod previews)
                g.DrawString(views[i].Name.ToUpperInvariant(), small, Brushes.Gainsboro, x + 12, y + 8);
                g.DrawRectangle(new Pen(Color.FromArgb(40, 44, 54)), x, y, cellW - 1, cellH - 1);
            }
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private static Bitmap ToBitmap(int[] argb, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(argb, 0, data.Scan0, argb.Length);
        bmp.UnlockBits(data);
        return bmp;
    }
}

internal static class Raster
{
    private const int SS = 2; // supersampling

    public static int[] Render(List<Tri> tris, Rgba[] palette, Model model, View view, int outW, int outH)
    {
        int w = outW * SS, h = outH * SS;
        var color = new float[w * h * 3];
        var depth = new float[w * h];
        var ids = new int[w * h];
        Array.Fill(depth, float.MaxValue);

        // Frame the model.
        var lo = new V3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new V3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var t in tris)
        foreach (var p in new[] { t.A, t.B, t.C })
        {
            lo = new V3(MathF.Min(lo.X, p.X), MathF.Min(lo.Y, p.Y), MathF.Min(lo.Z, p.Z));
            hi = new V3(MathF.Max(hi.X, p.X), MathF.Max(hi.Y, p.Y), MathF.Max(hi.Z, p.Z));
        }
        var center = (lo + hi) / 2;
        var radius = (hi - lo).Length / 2;
        if (view.Zoom > 1)
        {
            // Close-up of the vehicle body rather than the whole head.
            center = new V3(0, 2.2f, -0.2f);
            radius = 6.5f;
        }
        else radius /= view.Zoom;
        var fwd = (-view.Direction).Normalized;
        var right = V3.Cross(MathF.Abs(fwd.Y) > 0.99f ? V3.Forward : V3.Up, fwd).Normalized;
        var up = V3.Cross(fwd, right);
        var fovRad = view.Fov * MathF.PI / 180;
        var aspect = (float)w / h;
        var dist = radius / MathF.Sin(fovRad / 2) * (aspect < 1 ? 1 / aspect : 1) * 1.02f;
        var cam = center - fwd * dist;
        var focal = (h / 2f) / MathF.Tan(fovRad / 2);
        var orthoScale = (h / 2f) / (radius * 1.04f);

        (float x, float y, float z) Project(V3 p)
        {
            var d = p - cam;
            var cx = V3.Dot(d, right); var cy = V3.Dot(d, up); var cz = V3.Dot(d, fwd);
            if (view.Ortho) return (w / 2f + cx * orthoScale, h / 2f - cy * orthoScale, cz);
            return (w / 2f + cx * focal / cz, h / 2f - cy * focal / cz, cz);
        }

        V3 RayDir(float sx, float sy) => view.Ortho ? fwd : (right * ((sx - w / 2f) / focal) - up * ((sy - h / 2f) / focal) + fwd).Normalized;
        V3 RayOrigin(float sx, float sy) => view.Ortho ? cam + right * ((sx - w / 2f) / orthoScale) - up * ((sy - h / 2f) / orthoScale) : cam;

        var light = new V3(-0.45f, 0.85f, 0.35f).Normalized;
        var fill = new V3(0.6f, 0.3f, -0.5f).Normalized;

        // Ground: moon dust with a 1 m / 5 m grid, a 25 m swath marker, and the model's planar shadow.
        var shadow = new bool[w * h];
        foreach (var t in tris)
        {
            if (palette[t.Mat].A < 0.99f) continue;
            V3 S(V3 p) => p - light * (p.Y / light.Y);
            RasterTri(Project(S(t.A)), Project(S(t.B)), Project(S(t.C)), w, h, (x, y, _) => shadow[y * w + x] = true, cullCw: false);
        }
        var cleaning = model.Values.TryGetValue("cleaningWidth", out var cw) ? cw : 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = RayOrigin(x + 0.5f, y + 0.5f);
            var d = RayDir(x + 0.5f, y + 0.5f);
            var i = (y * w + x) * 3;
            if (d.Y < -1e-4f)
            {
                var tHit = -o.Y / d.Y;
                var p = o + d * tHit;
                var c = new V3(0.42f, 0.42f, 0.44f);
                var g1 = Grid(p.X, 1f, 0.025f) || Grid(p.Z, 1f, 0.025f);
                var g5 = Grid(p.X, 5f, 0.05f) || Grid(p.Z, 5f, 0.05f);
                if (g5) c = c * 0.78f; else if (g1) c = c * 0.9f;
                if (cleaning > 0 && MathF.Abs(MathF.Abs(p.X) - cleaning / 2) < 0.08f) c = new V3(1f, 0.55f, 0.2f);
                if (shadow[y * w + x]) c = c * 0.45f;
                var fade = MathF.Min(1, tHit / (dist * 4));
                c = V3.Lerp(c, new V3(0.07f, 0.08f, 0.1f), fade);
                color[i] = c.X; color[i + 1] = c.Y; color[i + 2] = c.Z;
            }
            else
            {
                var sky = V3.Lerp(new V3(0.09f, 0.1f, 0.14f), new V3(0.03f, 0.035f, 0.05f), MathF.Min(1, d.Y * 2));
                color[i] = sky.X; color[i + 1] = sky.Y; color[i + 2] = sky.Z;
            }
        }

        // Opaque, then transparent (no depth write, back to front).
        void Draw(Tri t, bool transparent)
        {
            var m = palette[t.Mat];
            var faceN = V3.Cross(t.B - t.A, t.C - t.A);
            var toCam = view.Ortho ? -fwd : cam - t.A;
            if (V3.Dot(faceN, toCam) <= 0) return; // Unity culls back faces
            var pa = Project(t.A); var pb = Project(t.B); var pc = Project(t.C);
            if (pa.z < 0.05f || pb.z < 0.05f || pc.z < 0.05f) return;
            RasterTri(pa, pb, pc, w, h, (x, y, bc) =>
            {
                var k = y * w + x;
                var z = pa.z * bc.a + pb.z * bc.b + pc.z * bc.c;
                if (z >= depth[k]) return;
                var n = (t.NA * bc.a + t.NB * bc.b + t.NC * bc.c).Normalized;
                var p = t.A * bc.a + t.B * bc.b + t.C * bc.c;
                var v = (cam - p).Normalized;
                if (view.Ortho) v = -fwd;
                var c = Shade(m, n, v, light, fill);
                var i = k * 3;
                if (transparent)
                {
                    color[i] = color[i] * (1 - m.A) + c.X * m.A;
                    color[i + 1] = color[i + 1] * (1 - m.A) + c.Y * m.A;
                    color[i + 2] = color[i + 2] * (1 - m.A) + c.Z * m.A;
                    return;
                }
                depth[k] = z;
                ids[k] = t.Id;
                color[i] = c.X; color[i + 1] = c.Y; color[i + 2] = c.Z;
            }, cullCw: false);
        }
        foreach (var t in tris) if (palette[t.Mat].A >= 0.99f) Draw(t, false);
        foreach (var t in tris.Where(t => palette[t.Mat].A < 0.99f)
                     .OrderByDescending(t => V3.Dot((t.A + t.B + t.C) / 3 - cam, fwd)))
            Draw(t, true);

        // Outlines where parts or depth change.
        for (var y = 1; y < h - 1; y++)
        for (var x = 1; x < w - 1; x++)
        {
            var k = y * w + x;
            if (ids[k] == 0) continue;
            var edge = false;
            foreach (var n in new[] { k + 1, k - 1, k + w, k - w })
            {
                if (ids[n] != ids[k] && (ids[n] == 0 || MathF.Abs(depth[n] - depth[k]) > 0.02f * depth[k])) { edge = true; break; }
            }
            if (!edge) continue;
            var i = k * 3;
            color[i] *= 0.35f; color[i + 1] *= 0.35f; color[i + 2] *= 0.35f;
        }

        // Downsample + tonemap.
        var outPx = new int[outW * outH];
        for (var y = 0; y < outH; y++)
        for (var x = 0; x < outW; x++)
        {
            float r = 0, g = 0, b = 0;
            for (var sy = 0; sy < SS; sy++)
            for (var sx = 0; sx < SS; sx++)
            {
                var i = ((y * SS + sy) * w + x * SS + sx) * 3;
                r += color[i]; g += color[i + 1]; b += color[i + 2];
            }
            var inv = 1f / (SS * SS);
            outPx[y * outW + x] = unchecked((int)0xFF000000) | (Tone(r * inv) << 16) | (Tone(g * inv) << 8) | Tone(b * inv);
        }
        return outPx;
    }

    private static bool Grid(float v, float step, float thick)
    {
        var f = MathF.Abs(v / step - MathF.Round(v / step)) * step;
        return f < thick;
    }

    private static int Tone(float v)
    {
        v = v / (1 + v * 0.15f);
        v = MathF.Pow(Math.Clamp(v, 0, 1), 1 / 2.2f);
        return (int)(v * 255 + 0.5f);
    }

    private static V3 Shade(Rgba m, V3 n, V3 v, V3 light, V3 fill)
    {
        var albedo = new V3(Lin(m.R), Lin(m.G), Lin(m.B));
        var sky = 0.22f + 0.18f * (n.Y * 0.5f + 0.5f);
        var diff = MathF.Max(0, V3.Dot(n, light)) * 1.15f + MathF.Max(0, V3.Dot(n, fill)) * 0.25f;
        var c = albedo * (sky + diff * (1 - m.Metallic * 0.6f));
        var hlf = (light + v).Normalized;
        var shin = 8 + m.Smoothness * 120;
        var spec = MathF.Pow(MathF.Max(0, V3.Dot(n, hlf)), shin) * (0.06f + m.Smoothness * 0.5f + m.Metallic * 0.4f);
        var specColor = V3.Lerp(new V3(1, 1, 1), albedo, m.Metallic);
        var rim = MathF.Pow(1 - MathF.Max(0, V3.Dot(n, v)), 3) * 0.12f;
        c = c + specColor * spec + new V3(rim, rim, rim);
        if (m.Emission > 0) c = c + albedo * m.Emission;
        return c;
    }

    private static float Lin(float c) => MathF.Pow(c, 2.2f);

    private delegate void Pixel(int x, int y, (float a, float b, float c) bary);

    private static void RasterTri((float x, float y, float z) a, (float x, float y, float z) b, (float x, float y, float z) c, int w, int h, Pixel plot, bool cullCw)
    {
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.x, MathF.Min(b.x, c.x))));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.x, MathF.Max(b.x, c.x))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.y, MathF.Min(b.y, c.y))));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.y, MathF.Max(b.y, c.y))));
        if (minX > maxX || minY > maxY) return;
        var area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (MathF.Abs(area) < 1e-6f) return;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            var w0 = ((b.x - px) * (c.y - py) - (b.y - py) * (c.x - px)) / area;
            var w1 = ((c.x - px) * (a.y - py) - (c.y - py) * (a.x - px)) / area;
            var w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            plot(x, y, (w0, w1, w2));
        }
    }
}
