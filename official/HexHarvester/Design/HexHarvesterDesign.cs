using System;
using System.Collections.Generic;
using MoonSweepMods.ModelKit;

namespace MSML.HexHarvester;

/// <summary>
/// The Hex Harvester: a six-wheeled lunar harvester with a hex-glass cab, a hex silo tower, and a 25 m truss-mounted
/// sweeper head with five brush pods, wide enough to clear a whole hex in one pass.
///
/// Model space is metres with the origin on the ground under the vehicle centre (x right, y up, z forward). Wheel
/// pivots and the driver seat line up with the game's vehicle physics, which are the Mega Vacuum Rover's anchors
/// scaled 1.45x.
/// </summary>
public sealed class HexHarvesterDesign : IModelDesign
{
    public string Id => "HexHarvester";

    // Physics anchors (Mega Vacuum Rover x1.45).
    public const float PhysicsScale = 1.45f;
    public const float WheelRadius = 0.66f * PhysicsScale;          // 0.957
    public const float WheelX = 1.22f * PhysicsScale;               // 1.77
    public static readonly float[] WheelZ = { 1.15f * PhysicsScale, -0.75f * PhysicsScale, -2.65f * PhysicsScale };
    public static readonly V3 DriverAnchor = new(0, 1.75f * PhysicsScale, 1.5f * PhysicsScale);

    public const float HeadWidth = 25f;
    public const float HeadZ = 6.4f;         // brush axis, metres ahead of the vehicle origin
    public const float BrushRadius = 0.44f;
    public const int Pods = 5;

    private const string Shell = "shell", Dark = "dark", Orange = "orange", Glass = "glass", Metal = "metal", Cyan = "cyan";
    private const string Rubber = "rubber", Red = "red", Hazard = "hazard", Bristle = "bristle", Steel = "steel";

    public Model Build()
    {
        var m = new Model("Hex Harvester");
        m.Palette[Shell] = Rgba.Hex("E9E6DF", smoothness: 0.45f);
        m.Palette[Dark] = Rgba.Hex("2B2D34", smoothness: 0.35f);
        m.Palette[Orange] = Rgba.Hex("FF7A2E", smoothness: 0.45f);
        m.Palette[Glass] = Rgba.Hex("6FA7C4", a: 0.45f, smoothness: 0.9f);
        m.Palette[Metal] = Rgba.Hex("9AA0A8", metallic: 0.8f, smoothness: 0.55f);
        m.Palette[Cyan] = Rgba.Hex("39D6E6", emission: 1.2f);
        m.Palette[Rubber] = Rgba.Hex("1A1B1F", smoothness: 0.15f);
        m.Palette[Red] = Rgba.Hex("E2342B", emission: 0.8f);
        m.Palette[Hazard] = Rgba.Hex("F2C230", smoothness: 0.4f);
        m.Palette[Bristle] = Rgba.Hex("D9822B", smoothness: 0.2f);
        m.Palette[Steel] = Rgba.Hex("5E636B", metallic: 0.6f, smoothness: 0.45f);

        m.Values["wheelRadius"] = WheelRadius;
        m.Values["cleaningWidth"] = HeadWidth;
        m.Values["headZ"] = HeadZ;
        m.Points["driver"] = DriverAnchor;

        var body = new Shapes(m.Group("Body", V3.Zero));
        Chassis(body);
        Cab(body);
        Silo(body);
        Rear(body);
        PushFrame(body);
        HeadFrame(body);
        Ducting(m, body);

        for (var axle = 0; axle < 3; axle++)
        foreach (var side in new[] { 1, -1 })
        {
            // The game's wheel transforms are named Wheel_<axle><L|R>, with L on +x.
            var name = $"Wheel_{axle + 1}{(side > 0 ? "L" : "R")}";
            Wheel(new Shapes(m.Group(name, new V3(side * WheelX, WheelRadius, WheelZ[axle]))), side);
        }

        var podWidth = HeadWidth / Pods;
        for (var i = 0; i < Pods; i++)
        {
            var x = -HeadWidth / 2 + podWidth * (i + 0.5f);
            Brush(new Shapes(m.Group($"Roller {i + 1}", new V3(x, BrushRadius, HeadZ))), podWidth - 0.5f);
        }
        return m;
    }

    // ---- chassis --------------------------------------------------------------------------------

    private static void Chassis(Shapes s)
    {
        // Ladder frame.
        foreach (var x in new[] { -0.95f, 0.95f })
            s.Box(Dark, new V3(x, 1.2f, -0.6f), new V3(0.32f, 0.42f, 9.4f));
        for (var z = -4.6f; z <= 3.8f; z += 2.1f)
            s.Box(Steel, new V3(0, 1.2f, z), new V3(1.6f, 0.26f, 0.22f));

        // Deck: an orange hex-plated floor with a dark lip.
        s.Box(Dark, new V3(0, 1.5f, -0.7f), new V3(3.15f, 0.18f, 9.2f));
        s.Box(Orange, new V3(0, 1.63f, -0.7f), new V3(2.95f, 0.08f, 9.0f));
        for (var z = -4.6f; z < 3.6f; z += 1.1f)
        for (var x = -1.05f; x <= 1.06f; x += 0.7f)
            s.HexColumn(Dark, new V3(x + ((int)MathF.Round(z * 10) % 2 == 0 ? 0 : 0.35f), 1.665f, z), 0.24f, 0.02f);

        // Angular fenders over each wheel, with hazard edges.
        foreach (var side in new[] { -1f, 1f })
        foreach (var wz in WheelZ)
        {
            var x = side * WheelX;
            s.Tapered(Orange, new V3(x, 2.13f, wz), new V3(0.86f, 0.22f, 2.35f), 0.7f, 1.7f);
            s.Box(Hazard, new V3(x + side * 0.42f, 2.04f, wz), new V3(0.04f, 0.08f, 2.3f));
            s.Box(Dark, new V3(x, 1.86f, wz - 1.1f), new V3(0.8f, 0.5f, 0.08f));
        }

        // Toolboxes between the wheels.
        foreach (var side in new[] { -1f, 1f })
        foreach (var (z0, z1) in new[] { (WheelZ[1] + 1.15f, WheelZ[0] - 1.15f), (WheelZ[2] + 1.15f, WheelZ[1] - 1.15f) })
        {
            var len = z0 > z1 ? 0 : z1 - z0;
            if (len < 0.2f) continue;
            var c = new V3(side * 1.62f, 1.15f, (z0 + z1) / 2);
            s.Box(Dark, c, new V3(0.32f, 0.55f, len));
            s.Box(Metal, c + new V3(side * 0.17f, 0.12f, 0), new V3(0.02f, 0.05f, len * 0.7f));
        }
    }

    // ---- cab --------------------------------------------------------------------------------------

    private static void Cab(Shapes s)
    {
        var z0 = 1.55f; var z1 = 4.15f;
        var mid = (z0 + z1) / 2;
        // Base tub.
        s.Tapered(Shell, new V3(0, 1.95f, mid), new V3(2.7f, 0.55f, z1 - z0), 2.6f, z1 - z0 - 0.1f);
        s.Box(Dark, new V3(0, 2.25f, mid), new V3(2.72f, 0.06f, z1 - z0 + 0.02f));

        // Hex glass canopy lying along z, flat side down.
        var cy = 3.05f;
        s.Prism(Glass, new V3(0, cy, z0 + 0.15f), new V3(0, cy, z1 - 0.25f), 1.0f, 6, false, 0);
        // Frame rings and spine.
        foreach (var z in new[] { z0 + 0.12f, mid, z1 - 0.22f })
            s.Prism(Dark, new V3(0, cy, z - 0.05f), new V3(0, cy, z + 0.05f), 1.04f, 6, false, 0);
        s.Box(Orange, new V3(0, cy + 0.9f, mid), new V3(1.15f, 0.08f, z1 - z0 - 0.3f));
        // Nose cone (front hex face tapers into the visor).
        s.Prism(Dark, new V3(0, cy, z1 - 0.22f), new V3(0, cy - 0.05f, z1 + 0.15f), 1.04f, 6, false, 0, 0.75f);
        s.Prism(Cyan, new V3(0, cy - 0.05f, z1 + 0.15f), new V3(0, cy - 0.05f, z1 + 0.18f), 0.6f, 6, false, 0, 0.6f);

        // Headlight bar and lamps.
        s.Box(Dark, new V3(0, 2.35f, z1 + 0.05f), new V3(2.4f, 0.16f, 0.12f));
        foreach (var x in new[] { -0.95f, -0.6f, 0.6f, 0.95f })
            s.Box(Cyan, new V3(x, 2.35f, z1 + 0.12f), new V3(0.26f, 0.08f, 0.03f));

        // Mirrors.
        foreach (var side in new[] { -1f, 1f })
        {
            s.Rod(Metal, new V3(side * 1.0f, 2.9f, z1 - 0.4f), new V3(side * 1.55f, 3.1f, z1 - 0.2f), 0.03f);
            s.Box(Dark, new V3(side * 1.6f, 3.0f, z1 - 0.2f), new V3(0.08f, 0.32f, 0.2f));
        }

        // Roof mast and beacon.
        s.Rod(Metal, new V3(0.45f, cy + 0.9f, z0 + 0.5f), new V3(0.45f, cy + 1.6f, z0 + 0.5f), 0.025f);
        s.Sphere(Cyan, new V3(0.45f, cy + 1.62f, z0 + 0.5f), 0.07f);

        // Seat back and steering column for the driver.
        s.Box(Dark, new V3(0, DriverAnchor.Y + 0.25f, DriverAnchor.Z - 0.35f), new V3(0.6f, 0.75f, 0.12f));
        s.Rod(Steel, new V3(0, 2.3f, DriverAnchor.Z + 0.55f), new V3(0, 2.75f, DriverAnchor.Z + 0.35f), 0.04f);
    }

    // ---- silo -------------------------------------------------------------------------------------

    private static void Silo(Shapes s)
    {
        var c = new V3(0, 1.67f, -1.75f);
        // Hex tower: plinth, white body, orange band, cyan fill window, dark tapered cap.
        s.HexColumn(Dark, c, 1.62f, 0.25f);
        s.HexColumn(Shell, c + V3.Up * 0.25f, 1.52f, 2.2f);
        s.HexColumn(Orange, c + V3.Up * 1.25f, 1.56f, 0.32f);
        s.HexColumn(Cyan, c + V3.Up * 0.55f, 1.56f, 0.08f);
        s.HexColumn(Dark, c + V3.Up * 2.45f, 1.6f, 0.45f, 30, 1.05f);
        s.HexColumn(Metal, c + V3.Up * 2.9f, 0.62f, 0.18f);
        s.HexColumn(Orange, c + V3.Up * 3.08f, 0.5f, 0.06f);

        // Vertical ribs at the hex corners.
        for (var i = 0; i < 6; i++)
        {
            var a = (30 + 60 * i) * MathF.PI / 180;
            var p = c + new V3(MathF.Cos(a) * 1.55f, 0, MathF.Sin(a) * 1.55f);
            s.Box(Dark, p + V3.Up * 1.35f, new V3(0.1f, 2.2f, 0.1f));
        }

        // Beacons on the cap.
        foreach (var side in new[] { -1f, 1f })
        {
            s.Box(Dark, c + new V3(side * 0.85f, 2.95f, 0), new V3(0.16f, 0.12f, 0.16f));
            s.Sphere(Orange, c + new V3(side * 0.85f, 3.08f, 0), 0.1f);
        }

        // Ladder up the back.
        var lz = c.Z - 1.42f;
        foreach (var x in new[] { -0.28f, 0.28f })
            s.Rod(Metal, new V3(x, 1.7f, lz), new V3(x, 4.4f, lz), 0.03f);
        for (var y = 1.9f; y < 4.4f; y += 0.32f)
            s.Rod(Metal, new V3(-0.28f, y, lz), new V3(0.28f, y, lz), 0.022f);
    }

    // ---- rear ---------------------------------------------------------------------------------------

    private static void Rear(Shapes s)
    {
        var zb = -4.75f;
        // Engine pod behind the silo.
        s.Tapered(Shell, new V3(0, 2.1f, -4.0f), new V3(2.7f, 0.9f, 1.4f), 2.3f, 1.1f, -0.1f);
        // Radiator fins.
        for (var x = -1.0f; x <= 1.01f; x += 0.2f)
            s.Box(Dark, new V3(x, 2.15f, zb + 0.05f), new V3(0.08f, 0.7f, 0.12f));
        // Exhaust stacks.
        foreach (var x in new[] { -1.1f, 1.1f })
        {
            s.Cylinder(Metal, new V3(x, 2.4f, -3.55f), new V3(x, 3.65f, -3.55f), 0.11f, 14);
            s.Cylinder(Dark, new V3(x, 3.65f, -3.55f), new V3(x, 3.8f, -3.55f), 0.14f, 14);
        }
        // Bumper with hazard stripes and tail lights.
        s.Box(Dark, new V3(0, 1.05f, zb - 0.1f), new V3(3.0f, 0.36f, 0.25f));
        s.HazardStripes(Hazard, new V3(0, 1.05f, zb - 0.225f), M3.RotY(180), 2.8f, 0.3f);
        foreach (var x in new[] { -1.25f, 1.25f })
            s.Box(Red, new V3(x, 1.5f, zb - 0.05f), new V3(0.4f, 0.14f, 0.05f));
    }

    // ---- push frame and head --------------------------------------------------------------------------

    private const float TopChordY = 2.05f, BottomChordY = 1.25f, ChordZ = HeadZ - 0.55f;

    private static void PushFrame(Shapes s)
    {
        // Nose block and a hydraulic A-frame out to the truss.
        s.Box(Dark, new V3(0, 1.3f, 4.55f), new V3(1.8f, 0.6f, 0.7f));
        foreach (var side in new[] { -1f, 1f })
        {
            s.Rod(Metal, new V3(side * 0.7f, 1.3f, 4.8f), new V3(side * 3.2f, BottomChordY, ChordZ), 0.09f);
            s.Rod(Metal, new V3(side * 0.5f, 1.55f, 4.8f), new V3(side * 1.6f, TopChordY, ChordZ), 0.08f);
            // Lift ram: cylinder plus chrome rod.
            s.Cylinder(Dark, new V3(side * 0.35f, 2.15f, 4.15f), new V3(side * 0.6f, 2.1f, 5.0f), 0.12f, 12);
            s.Cylinder(Metal, new V3(side * 0.6f, 2.1f, 5.0f), new V3(side * 0.8f, TopChordY, ChordZ), 0.06f, 10);
        }
    }

    private static void HeadFrame(Shapes s)
    {
        var half = HeadWidth / 2 + 0.25f;
        // Truss: two chords with a zigzag web.
        s.Box(Dark, new V3(0, TopChordY, ChordZ), new V3(half * 2, 0.24f, 0.3f));
        s.Box(Dark, new V3(0, BottomChordY, ChordZ), new V3(half * 2, 0.2f, 0.26f));
        var bays = 20;
        for (var i = 0; i < bays; i++)
        {
            var x0 = -half + 2 * half * i / bays;
            var x1 = -half + 2 * half * (i + 1) / bays;
            s.Rod(Steel, new V3(x0, BottomChordY, ChordZ), new V3(x1, TopChordY, ChordZ), 0.05f, 6);
            s.Rod(Steel, new V3(x1, BottomChordY, ChordZ), new V3(x1, TopChordY, ChordZ), 0.035f, 6);
        }
        // Hazard stripes along the top chord's face.
        for (var i = 0; i < 52; i++)
            s.Box(i % 2 == 0 ? Hazard : Dark, new V3(-half + 0.25f + i * (2 * half - 0.5f) / 51, TopChordY, ChordZ + 0.16f), new V3(0.46f, 0.2f, 0.02f));

        // Brush pods: an orange hood, white cheeks, and a hanger to the bottom chord.
        var podWidth = HeadWidth / Pods;
        for (var i = 0; i < Pods; i++)
        {
            var x = -HeadWidth / 2 + podWidth * (i + 0.5f);
            var w = podWidth - 0.3f;
            s.Tapered(Orange, new V3(x, BrushRadius * 2 + 0.12f, HeadZ - 0.05f), new V3(w, 0.32f, 1.25f), w - 0.2f, 0.75f, -0.15f);
            s.Box(Dark, new V3(x, BrushRadius + 0.2f, HeadZ - 0.62f), new V3(w, BrushRadius * 1.6f, 0.08f));
            foreach (var side in new[] { -1f, 1f })
                s.Box(Shell, new V3(x + side * (w / 2 - 0.05f), BrushRadius + 0.12f, HeadZ), new V3(0.1f, BrushRadius * 2 + 0.1f, 1.15f));
            s.Box(Dark, new V3(x, (BottomChordY + BrushRadius * 2 + 0.3f) / 2, ChordZ), new V3(0.5f, BottomChordY - BrushRadius * 2 - 0.2f, 0.22f));
            // Hex intake on top of each hood.
            s.HexColumn(Dark, new V3(x, BrushRadius * 2 + 0.28f, HeadZ - 0.25f), 0.32f, 0.22f);
        }

        // Wing tips: end plates, caster wheels and beacons.
        foreach (var side in new[] { -1f, 1f })
        {
            var x = side * (HeadWidth / 2 + 0.3f);
            s.Box(Dark, new V3(x, 1.1f, HeadZ - 0.2f), new V3(0.14f, 1.9f, 1.6f));
            s.HazardStripes(Hazard, new V3(x + side * 0.07f, 1.1f, HeadZ - 0.2f), M3.RotY(side * 90), 1.5f, 1.8f, 0.2f, 0.2f, 0.3f);
            s.Cylinder(Rubber, new V3(x - side * 0.1f, 0.28f, HeadZ - 0.9f), new V3(x + side * 0.1f, 0.28f, HeadZ - 0.9f), 0.28f, 18);
            s.Cylinder(Metal, new V3(x - side * 0.12f, 0.28f, HeadZ - 0.9f), new V3(x + side * 0.12f, 0.28f, HeadZ - 0.9f), 0.12f, 12);
            s.Sphere(Cyan, new V3(x, 2.15f, HeadZ - 0.2f), 0.13f);
            s.Rod(Metal, new V3(x, 2.05f, HeadZ - 0.2f), new V3(x, TopChordY, ChordZ), 0.05f);
        }

        // Centre badge: a cyan hex on a dark plate.
        s.Box(Dark, new V3(0, (TopChordY + BottomChordY) / 2, ChordZ + 0.17f), new V3(1.1f, 0.95f, 0.06f));
        s.Prism(Cyan, new V3(0, (TopChordY + BottomChordY) / 2, ChordZ + 0.2f), new V3(0, (TopChordY + BottomChordY) / 2, ChordZ + 0.24f), 0.4f, 6, false, 30);
    }

    private const string DuctGroup = "Intake duct";
    private static readonly V3 SiloInlet = new(0, 4.81f, -1.75f);
    private const float DuctRadius = 0.27f;

    private static void Ducting(Model m, Shapes s)
    {
        // Collector manifold along the truss, with a riser from each brush pod.
        var my = TopChordY + 0.35f;
        var mz = ChordZ - 0.05f;
        s.Cylinder(Shell, new V3(-HeadWidth / 2 + 1.0f, my, mz), new V3(HeadWidth / 2 - 1.0f, my, mz), 0.2f, 16);
        for (var i = 0; i < Pods; i++)
        {
            var x = -HeadWidth / 2 + HeadWidth / Pods * (i + 0.5f);
            s.Cylinder(Dark, new V3(x, BrushRadius * 2 + 0.45f, HeadZ - 0.25f), new V3(x, my, mz), 0.13f, 12);
            s.Cylinder(Orange, new V3(x - 0.24f, my, mz), new V3(x + 0.24f, my, mz), 0.24f, 16);
        }
        // Junction box where the main duct leaves the manifold.
        s.Box(Dark, new V3(0, my + 0.05f, mz), new V3(0.9f, 0.62f, 0.62f));

        // Main intake duct: one continuous tube from the junction, up over the cab and down into the silo's top.
        // The game animates it as a hose (suction pulses travelling to the silo), so it's its own group.
        var duct = new Shapes(m.Group(DuctGroup, V3.Zero));
        var controls = new List<V3>
        {
            new(0, my + 0.3f, mz),
            new(0, 3.3f, 5.55f),
            new(0, 4.45f, 4.75f),
            new(0, 5.05f, 3.2f),
            new(0, 5.15f, 1.0f),
            new(0, 5.35f, -0.9f),
            new(0, 5.35f, -1.6f),
            new(0, 5.15f, -1.75f),
            SiloInlet,
        };
        var path = duct.Tube(Dark, controls, DuctRadius, 18, 0.04f);

        // Clamps (static) at fixed fractions; the hose can't bulge under them.
        var clampAt = new[] { 0.16f, 0.38f, 0.58f, 0.78f };
        var envelope = new List<float>();
        for (var i = 0; i < path.Count; i++)
        {
            var f = i / (float)(path.Count - 1);
            var e = 1f;
            e = MathF.Min(e, Ramp(i, 8));                    // fixed at the junction
            e = MathF.Min(e, Ramp(path.Count - 1 - i, 8));   // fixed at the silo collar
            foreach (var c in clampAt) e = MathF.Min(e, Ramp(MathF.Abs(f - c) * (path.Count - 1), 5));
            envelope.Add(e);
        }
        m.Hoses[DuctGroup] = (path, envelope);
        foreach (var c in clampAt)
        {
            var i = (int)(c * (path.Count - 1));
            var dir = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).Normalized;
            s.Cylinder(Orange, path[i] - dir * 0.07f, path[i] + dir * 0.07f, DuctRadius + 0.06f, 18);
        }

        // Collars at both ends.
        s.Cylinder(Metal, SiloInlet - V3.Up * 0.02f, SiloInlet + V3.Up * 0.2f, DuctRadius + 0.08f, 18);
        s.Cylinder(Orange, SiloInlet + V3.Up * 0.2f, SiloInlet + V3.Up * 0.26f, DuctRadius + 0.05f, 18);

        // Struts holding the duct over the cab, from the cab roof spine and the silo cap.
        s.Rod(Steel, new V3(0.45f, 3.98f, 3.6f), path[(int)(path.Count * 0.33f)] + new V3(0.2f, -0.2f, 0), 0.05f);
        s.Rod(Steel, new V3(-0.45f, 3.98f, 2.1f), path[(int)(path.Count * 0.5f)] + new V3(-0.2f, -0.2f, 0), 0.05f);
        s.Rod(Steel, new V3(0.9f, 4.5f, -1.0f), path[(int)(path.Count * 0.72f)] + new V3(0.2f, -0.15f, 0), 0.05f);
        s.Rod(Steel, new V3(-0.9f, 4.5f, -1.0f), path[(int)(path.Count * 0.72f)] + new V3(-0.2f, -0.15f, 0), 0.05f);
    }

    /// <summary>0 at distance 0, rising smoothly to 1 at <paramref name="width"/> samples.</summary>
    private static float Ramp(float distance, float width)
    {
        var t = Math.Clamp(distance / width, 0, 1);
        return t * t * (3 - 2 * t);
    }
    // ---- moving parts -----------------------------------------------------------------------------------

    private static void Wheel(Shapes s, int side)
    {
        const float width = 0.66f;
        var a = new V3(-width / 2, 0, 0);
        var b = new V3(width / 2, 0, 0);
        s.Cylinder(Rubber, a, b, WheelRadius - 0.06f, 28);
        s.Radial(Rubber, V3.Zero, V3.Right, 22, WheelRadius - 0.04f, new V3(0.16f, 0.1f, width - 0.04f));
        s.Cylinder(Metal, a * 1.05f, b * 1.05f, 0.58f, 24);
        var outer = new V3(side * (width / 2 + 0.04f), 0, 0);
        s.Prism(Orange, V3.Zero, outer, 0.42f, 6, false, 0);
        s.Prism(Dark, outer, outer + new V3(side * 0.08f, 0, 0), 0.2f, 6, false, 30);
        s.Radial(Steel, outer, V3.Right, 6, 0.3f, new V3(0.07f, 0.07f, 0.06f), 30);
    }

    private static void Brush(Shapes s, float width)
    {
        var a = new V3(-width / 2, 0, 0);
        var b = new V3(width / 2, 0, 0);
        s.Cylinder(Dark, a, b, 0.2f, 16);
        // Spiral-ish bristle bands.
        var bands = (int)(width / 0.55f);
        for (var i = 0; i < bands; i++)
        {
            var x = -width / 2 + (i + 0.5f) * width / bands;
            s.Radial(Bristle, new V3(x, 0, 0), V3.Right, 10, BrushRadius - 0.1f, new V3(0.08f, 0.2f, width / bands - 0.06f), i * 18);
        }
        s.Cylinder(Metal, a - new V3(0.05f, 0, 0), a + new V3(0.08f, 0, 0), 0.26f, 14);
        s.Cylinder(Metal, b - new V3(0.08f, 0, 0), b + new V3(0.05f, 0, 0), 0.26f, 14);
    }
}
