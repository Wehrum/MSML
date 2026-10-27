using System;
using System.Collections.Generic;
using System.Linq;
using MoonCleaners;
using UnityEngine;

namespace MSML.BetterBelts;

/// <summary>
/// The two belt types and their looks. The game's belt is hidden on both and they're built along the ground: a tube
/// is a glass pipe sunk into the ground (its bottom hidden by the ground, a dark track inside
/// where capsules slide), with pressure rings; a booster is a fixed-length housing with a compressor on top. Where a
/// tube meets something that isn't a tube (a machine, splitter or belt) it rises out of the ground to meet it.
/// Capsules in them lie on their side on the track (stone blocks stay flat).
/// </summary>
internal static class Tubes
{
    public static BeltType Tube = null!;
    public static BeltType Booster = null!;
    public static JunctionType Splitter = null!;
    public static JunctionType Merger = null!;

    // Sizes in metres. Capsules are about 0.41 wide and 0.6 long (pivot at the bottom); stone blocks 0.5 x 0.28 x 0.5.
    private const float TubeRadius = 0.36f;
    private const float BuriedCentre = 0.14f;      // pipe centre above the ground, buried
    private const float TrackBelow = 0.14f;        // the track (floor) below the pipe centre: level with the ground
    private const float RaisedCentre = 0.26f;      // pipe centre above the belt's surface, where it meets a machine
    private const float CapsuleHalf = 0.21f;       // a lying capsule's half height (it rests on the track)
    private const float CapsuleLength = 0.6f;
    private const float StoneHalfHeight = 0.14f;
    private const float Ramp = 1.6f;               // rising out of the ground to meet a machine
    private const float RingSpacing = 2f;
    private const float RingWidth = 0.06f;
    private const float BoosterRadius = 0.46f;
    public const float BoosterLength = 2.4f;
    private const float HousingDrop = 0.06f;        // the booster's housing sits lower than the tube through it
    private const float DrumRadius = 0.15f;         // the booster's compressor, lying beside it on the ground
    private const float DrumSide = BoosterRadius + 0.2f;
    private const float DrumLift = 0.08f;           // its centre above the ground (mostly sunk)
    private const float ProfileStep = 0.5f;
    private const float MaxGap = 2.5f;             // the belt's most above the ground (the tube drops to the ground)
    private const float MaxRise = 2.5f;            // the ground's most above the belt (the tube rides up over it)
    private const float MaxDrop = MaxGap + 0.2f;

    /// <summary>Each booster's air: (puffs, rate at full power).</summary>
    public static readonly Dictionary<int, List<(Puffs Puffs, float Rate)>> Air = new();

    /// <summary>Each booster's hiss.</summary>
    public static readonly Dictionary<int, AudioSource> Hiss = new();

    /// <summary>Each tube's (or booster's) glowing rings and how far along the belt they sit.</summary>
    public static readonly Dictionary<int, List<(Renderer Ring, float At)>> Rings = new();

    /// <summary>Each tube splitter's and merger's pressure ring.</summary>
    public static readonly Dictionary<int, List<Renderer>> JunctionRings = new();

    // Per belt: whether the pieces before / after are pipes too (no rising between them), and the ground below it.
    private static readonly Dictionary<int, (bool From, bool To)> Joined = new();
    private static readonly Dictionary<int, float[]> Ground = new();

    public static void Register()
    {
        Tube = Belts.Register(new BeltType("msml.betterbelts.tube", "Pneumatic tube")
        {
            Description = "A glass tube sunk into the ground that shoots capsules along. Put a Tube booster in the line for full speed " +
                          "(5x a belt); without one it runs at belt speed. Up to 90 m long. Rings show the pressure: green full, yellow fading, red none.",
            Card = "tube.png",
            Section = "Tubes",
            HideBelt = true,
            OnGround = true,
            MaxGroundGap = MaxGap,
            MaxGroundRise = MaxRise,
            PassThrough = true,
            ColliderTop = 0.12f,
            DeleteRidersOnRemove = true,
            MaxLength = 90f,
            Style = StyleTube,
            Preview = PreviewTube,
            RiderPose = Pose,
        });
        Booster = Belts.Register(new BeltType("msml.betterbelts.booster", "Tube booster")
        {
            Description = "A powered piece that pressurises the tubes after it: full speed for about 80 m, then fading. " +
                          "Aim at a tube to build it in, or put it at a tube's end (never straight after another booster); it's always the same size. Runs on solar power like a machine.",
            Card = "booster.png",
            Section = "Tubes",
            FixedLength = BoosterLength,
            HideBelt = true,
            OnGround = true,
            MaxGroundGap = MaxGap,
            MaxGroundRise = MaxRise,
            PassThrough = true,
            ColliderTop = 0.12f,
            DeleteRidersOnRemove = true,
            NoChaining = true,
            Style = StyleBooster,
            Preview = PreviewBooster,
            RiderPose = Pose,
        });
        Booster.InsertsInto = new HashSet<BeltType> { Tube };
        Splitter = Junctions.Register(new JunctionType("msml.betterbelts.splitter", "Tube splitter", JunctionType.Splitter)
        {
            Description = "A splitter sunk into the ground for tube lines: tubes join it flush, and capsules keep their pressure through it. " +
                          "Aim at a tube to build it in, like the game's splitter. Its ring shows the pressure through it.",
            Card = "splitter.png",
            Section = "Tubes",
            HideModel = true,
            PassThrough = true,
            ColliderTop = 0.12f,
            Style = StyleJunction,
            Preview = (parts, hologram) => PreviewJunction(JunctionType.Splitter, parts, hologram),
            RouteType = Tube,
            PortSink = PortHeight - BuriedCentre,
        });
        Merger = Junctions.Register(new JunctionType("msml.betterbelts.merger", "Tube merger", JunctionType.Merger)
        {
            Description = "A merger sunk into the ground for tube lines: tubes join it flush, and capsules keep their pressure through it. " +
                          "Aim at a tube to build it in, like the game's merger. Its ring shows the pressure through it.",
            Card = "merger.png",
            Section = "Tubes",
            HideModel = true,
            PassThrough = true,
            ColliderTop = 0.12f,
            Style = StyleJunction,
            Preview = (parts, hologram) => PreviewJunction(JunctionType.Merger, parts, hologram),
            RouteType = Tube,
            PortSink = PortHeight - BuriedCentre,
        });
        ApplyPrices();
        Belts.TypeChanged += (belt, type) =>
        {
            if (type != Tube && type != Booster) Rings.Remove(belt);
            if (type != Booster) { Air.Remove(belt); Hiss.Remove(belt); }
            Ground.Remove(belt);
        };
        Belts.Changed += RejoinPipes;
        Junctions.TypeChanged += (structure, type) =>
        {
            if (type != Splitter && type != Merger) JunctionRings.Remove(structure);
            foreach (var route in Ground.Keys.Where(k => k < 0).ToList()) Ground.Remove(route);
            RejoinPipes();
        };
    }

    // A pipe rises out of the ground where it meets something that isn't a pipe: when the network changes, pipes
    // whose neighbours changed are drawn again.
    private static void RejoinPipes()
    {
        foreach (var belt in Belts.OfType(Tube).Concat(Belts.OfType(Booster)).ToList())
        {
            var now = Neighbours(belt);
            if (Joined.TryGetValue(belt, out var before) && before == now) continue;
            var known = Joined.ContainsKey(belt);
            Joined[belt] = now;
            if (known) Belts.Restyle(belt);
        }
    }

    public static void ApplyPrices()
    {
        Tube.PriceScale = Settings.TubePriceScale.Value;
        Booster.PricePerPiece = Settings.BoosterPrice.Value;
    }

    private static bool IsPipe(int belt) => Belts.TypeOf(belt) is { } t && (t == Tube || t == Booster);

    private static bool IsPipeJunction(int structure) => Junctions.TypeOf(structure) is { } t && (t == Splitter || t == Merger);

    // Joined straight to another pipe at each end, or to a tube splitter or merger (a machine, belt, or the game's
    // splitter or merger, the pipe rises to meet).
    private static (bool From, bool To) Neighbours(int belt)
    {
        var start = Belts.StartsAt(belt).Structure;
        var end = Belts.EndsAt(belt).Structure;
        return (start == 0 ? Belts.Upstream(belt).Exists(IsPipe) : IsPipeJunction(start),
                end == 0 ? Belts.Downstream(belt).Exists(IsPipe) : IsPipeJunction(end));
    }

    // ---- shape along the belt ------------------------------------------------------------------

    private static (bool From, bool To) JoinedOf(int belt)
    {
        if (!Joined.TryGetValue(belt, out var joined))
            Joined[belt] = joined = Neighbours(belt);
        return joined;
    }

    private static float[] GroundOf(int belt, MoonConveyorPath path)
    {
        if (!Ground.TryGetValue(belt, out var profile)) Ground[belt] = profile = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f, MaxRise + 0.3f);
        return profile;
    }

    // How far below the belt's surface the ground is (far-off or missing ground: as if built at the usual height).
    private static float DropAt(float[] ground, float distance)
    {
        var drop = BeltLooks.AtDistance(ground, ProfileStep, distance);
        return drop > MaxDrop ? 0.4f : Math.Max(-MaxRise - 0.3f, drop);
    }

    // 1 where the pipe is sunk into the ground, easing to 0 at ends that meet something that isn't a pipe.
    private static float Buried((bool From, bool To) joined, float length, float distance)
    {
        var t = 1f;
        if (!joined.From) t = Math.Min(t, distance / Ramp);
        if (!joined.To) t = Math.Min(t, (length - distance) / Ramp);
        return VMath.SmoothStep(0f, 1f, t);
    }

    /// <summary>The pipe's centre above the belt's surface at a distance along it, and how buried it is there.</summary>
    private static (float Centre, float Buried) Shape(int belt, MoonConveyorPath path, float distance)
    {
        // A route through a tube splitter or merger: in its tubes, at its floor (the route runs at its ports' height).
        if (belt < 0) return (-DropAt(GroundOf(belt, path), distance) + BuriedCentre, 1f);
        // Boosters are too short to rise: they always sit in the ground (tubes rise to meet machines).
        var buried = Belts.TypeOf(belt) == Booster ? 1f : Buried(JoinedOf(belt), path.Length, distance);
        var centre = VMath.Lerp(RaisedCentre, -DropAt(GroundOf(belt, path), distance) + BuriedCentre, buried);
        return (centre, buried);
    }

    // ---- capsules ------------------------------------------------------------------------------

    private static void Pose(int belt, float distance, MoonDroppedCanister capsule, ref Vector3 position, ref Quaternion rotation)
    {
        var path = Belts.PathOf(belt);
        if (path == null || !BeltLooks.Frame(path, distance, out _, out var forward, out _, out var up)) return;
        var (centre, buried) = Shape(belt, path, distance);
        // On the track: the capsule's centre a little above the pipe's centre when buried, on it when raised.
        var capsuleCentre = centre + (CapsuleHalf - TrackBelow) * buried;
        // Plain C# maths (see VMath): this runs for every capsule shown, every frame.
        var lying = VMath.SmoothStep(0f, 1f, Math.Min(buried * 2f, 1f));
        if (capsule.cargo == MoonCargo.Stone)
        {
            position = VMath.Mad(position, up, capsuleCentre - StoneHalfHeight);
            return;
        }
        // On its side, along the pipe, centred on the rider's point (the capsule's pivot is its bottom end); tipping
        // up where the pipe rises to meet a machine.
        var lyingPosition = VMath.Mad(position, up, capsuleCentre, forward, -CapsuleLength / 2f);
        var upright = VMath.Mad(position, up, Math.Min(capsuleCentre, 0f));
        position = VMath.Lerp(upright, lyingPosition, lying);
        rotation = VMath.Slerp(rotation, VMath.LookRotation(up, forward), lying);
    }

    // ---- tube ----------------------------------------------------------------------------------

    private static void StyleTube(int belt, GameObject parts)
    {
        var path = Belts.PathOf(belt);
        if (path == null) return;
        Ground.Remove(belt);
        var t = parts.transform;
        var length = path.Length;
        var metal = BeltLooks.GameMaterial("MC - Brushed joint hardware") ?? BeltLooks.Glow(Color.gray, 0f);
        float CentreAt(float d) => Shape(belt, path, d).Centre;

        var glass = BeltLooks.Tube(path, t, TubeRadius, 0f, sides: 22, liftAt: CentreAt);
        if (glass != null) BeltLooks.Show(t, "Glass", glass, BeltLooks.Glass(new Color(0.62f, 0.84f, 1f, 0.3f)), shadows: false);
        AddTrack(belt, path, t, TubeRadius);

        // A thin metal spine along the top gives the glass an outline.
        var spine = BeltLooks.Tube(path, t, 0.016f, 0f, sides: 5, step: 0.5f, liftAt: d => CentreAt(d) + TubeRadius);
        if (spine != null) BeltLooks.Show(t, "Spine", spine, metal, shadows: false);

        // Metal collars where the tube rises to meet something that isn't a pipe (boosters have their own flanges).
        var joined = JoinedOf(belt);
        foreach (var (from, to, open) in new[] { (0f, 0.12f, !joined.From), (length - 0.12f, length, !joined.To) })
        {
            if (!open) continue;
            var collar = BeltLooks.Tube(path, t, TubeRadius + 0.035f, 0f, from, to, sides: 22, liftAt: CentreAt);
            if (collar != null) BeltLooks.Show(t, "Collar", collar, metal);
        }
        AddRampPosts(belt, path, t, TubeRadius, metal);
        AddRings(belt, path, t, length, TubeRadius + 0.012f);
        // Dug in: soil heaped along both sides and loose rocks, where it's sunk into the ground.
        var (buriedFrom, buriedTo) = (joined.From ? 0f : Ramp, joined.To ? length : length - Ramp);
        float GroundAt(float d) => CentreAt(d) - BuriedCentre;
        AddSpoil(t, path, buriedFrom, buriedTo, GroundAt, belt, !joined.From, !joined.To, -1f, 1f);
    }

    // A plain dark floor (the game's belt surface has stripes).
    private static Material? TrackMaterial() =>
        BeltLooks.GameMaterial("MC - Graphite chassis") ?? BeltLooks.Glow(new Color(0.08f, 0.09f, 0.1f), 0f);

    // ---- dug-in ground -------------------------------------------------------------------------

    // The soil heaped beside a pipe: across (from the pipe's centre) and height above the ground, inner to outer edge.
    private static readonly (float Across, float Height)[] Mound =
        { (TubeRadius - 0.06f, 0.02f), (TubeRadius + 0.04f, 0.075f), (TubeRadius + 0.16f, 0.065f), (TubeRadius + 0.3f, 0.035f), (TubeRadius + 0.46f, 0.012f), (TubeRadius + 0.62f, -0.012f) };
    private const float MoundStep = 0.25f;
    private const float RockStep = 0.3f;

    private static Material Soil() =>
        BeltLooks.SceneMaterial("Deposit intake - dust heap") ?? BeltLooks.Glow(new Color(0.55f, 0.56f, 0.6f), 0f);

    private static Material Rock() =>
        BeltLooks.SceneMaterial("MoonRock") ?? BeltLooks.Glow(new Color(0.42f, 0.42f, 0.45f), 0f);

    // Something that looks random but is the same every time (so redrawing doesn't move the lumps).
    private static float Hash(int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)(a * 73856093 ^ b * 19349663 ^ c * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xffff) / 65535f;
        }
    }

    // Soil heaped along the given sides (-1 left, 1 right) from 'from' to 'to' metres along the path - low and lumpy,
    // fading into the ground outwards and at free ends - with loose rocks scattered on and beside it.
    // groundAt: the ground's height above the path.
    private static void AddSpoil(Transform parent, MoonConveyorPath path, float from, float to, Func<float, float> groundAt, int seed,
        bool taperStart, bool taperEnd, params float[] sides)
    {
        if (to - from < 0.3f) return;
        var rows = Math.Max(2, (int)Math.Ceiling((to - from) / MoundStep) + 1);
        var columns = Mound.Length;
        var frames = new (Vector3 Position, Vector3 Right, Vector3 Up, float Ground, float Taper)[rows];
        for (var i = 0; i < rows; i++)
        {
            var d = from + (to - from) * i / (rows - 1);
            path.Frame(d, out var position, out _, out var right, out var up);
            var edge = Math.Min(taperStart ? d - from : 1f, taperEnd ? to - d : 1f);
            frames[i] = (position, right, up, groundAt(d), VMath.SmoothStep(0f, 1f, edge / 0.6f));
        }
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        foreach (var side in sides)
        {
            var first = vertices.Count;
            for (var i = 0; i < rows; i++)
            {
                var (position, right, up, ground, taper) = frames[i];
                var d = from + (to - from) * i / (rows - 1);
                for (var c = 0; c < columns; c++)
                {
                    var (across, height) = Mound[c];
                    // Lumpy on top, a ragged outer edge.
                    var inner = c > 0 && c < columns - 1;
                    if (inner) height += (Hash(seed, i, c * 2 + (side > 0 ? 1 : 0)) - 0.5f) * 0.035f;
                    if (c >= columns - 2) across += (Hash(seed, i, 50 + c + (side > 0 ? 9 : 0)) - 0.5f) * 0.14f;
                    height = height * taper - 0.03f * (1f - taper);
                    vertices.Add(VMath.Mad(VMath.Mad(position, up, ground + height), right, side * across));
                    uvs.Add(new Vector2(across * 0.7f, d * 0.7f));
                }
            }
            for (var i = 0; i < rows - 1; i++)
                for (var c = 0; c < columns - 1; c++)
                {
                    int a = first + i * columns + c, b = a + 1, e = a + columns, f = e + 1;
                    AddUp(a, e, b, frames[i].Up);
                    AddUp(b, e, f, frames[i].Up);
                }
        }
        // Each triangle facing up, whatever the side and the frame's handedness.
        void AddUp(int a, int b, int c, Vector3 up)
        {
            var normal = VMath.Cross(VMath.Sub(vertices[b], vertices[a]), VMath.Sub(vertices[c], vertices[a]));
            if (VMath.Dot(normal, up) < 0f) (b, c) = (c, b);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }
        if (triangles.Count > 0)
            BeltLooks.Show(parent, "Soil", BeltLooks.WorldMesh(parent, vertices, null, triangles, "Soil", uvs), Soil(), shadows: false);
        AddRocks(parent, path, from, to, groundAt, seed, sides);
    }

    private static void AddRocks(Transform parent, MoonConveyorPath path, float from, float to, Func<float, float> groundAt, int seed, float[] sides)
    {
        // Rocks: a few low, chunky stones per metre, the same each time it's drawn.
        var random = new System.Random(seed);
        float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        for (var d = from + 0.15f; d < to - 0.15f; d += RockStep)
            foreach (var side in sides)
            {
                if (random.NextDouble() > 0.55) continue;
                var at = d + R(-0.12f, 0.12f);
                path.Frame(VMath.Clamp(at, 0f, path.Length), out var position, out var forward, out var right, out var up);
                var size = R(0.03f, 0.085f);
                if (random.NextDouble() < 0.12) size *= 1.7f;
                var across = TubeRadius + R(0.02f, 0.75f);
                // On the soil (its height there), partly sunk.
                var onSoil = across < Mound[2].Across ? Mound[1].Height : across < Mound[4].Across ? Mound[3].Height : 0f;
                var centre = VMath.Mad(VMath.Mad(position, up, groundAt(at) + onSoil + size * 0.2f), right, side * across);
                AddRock(vertices, normals, triangles, centre, forward, right, up, size, random);
            }
        if (vertices.Count == 0) return;
        var mesh = BeltLooks.WorldMesh(parent, vertices, normals, triangles, "Rocks");
        BeltLooks.Show(parent, "Rocks", mesh, Rock(), shadows: false);
    }

    // A chunky, flat-shaded stone: an octahedron with its points pulled about.
    private static readonly Vector3[] RockPoints = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
    private static readonly int[] RockFaces = { 0, 2, 4, 4, 2, 1, 1, 2, 5, 5, 2, 0, 4, 3, 0, 1, 3, 4, 5, 3, 1, 0, 3, 5 };

    private static void AddRock(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, Vector3 centre,
        Vector3 forward, Vector3 right, Vector3 up, float size, System.Random random)
    {
        float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
        // Flattened (stones lie on their wide side), stretched one way, turned at random.
        var scale = new Vector3(R(0.8f, 1.5f), R(0.45f, 0.75f), R(0.8f, 1.3f));
        var turn = R(0f, MathF.PI * 2f);
        var (c, s) = (MathF.Cos(turn), MathF.Sin(turn));
        var points = new Vector3[RockPoints.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = RockPoints[i];
            var x = p.x * scale.x * R(0.75f, 1.25f);
            var y = p.y * scale.y * R(0.75f, 1.25f);
            var z = p.z * scale.z * R(0.75f, 1.25f);
            var local = VMath.Mad(VMath.Mad(VMath.Mul(right, (x * c - z * s) * size), up, y * size), forward, (x * s + z * c) * size);
            points[i] = VMath.Add(centre, local);
        }
        for (var f = 0; f < RockFaces.Length; f += 3)
        {
            var a = points[RockFaces[f]];
            var b = points[RockFaces[f + 1]];
            var e = points[RockFaces[f + 2]];
            var normal = VMath.Normalize(VMath.Cross(VMath.Sub(b, a), VMath.Sub(e, a)));
            // Face outwards from the stone's centre, whatever the frame's handedness.
            if (VMath.Dot(normal, VMath.Sub(a, centre)) < 0f) { (b, e) = (e, b); normal = VMath.Mul(normal, -1f); }
            var first = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(e);
            normals.Add(normal); normals.Add(normal); normals.Add(normal);
            triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
        }
    }

    // The track: a dark floor across the inside of the pipe, level with the ground where it's buried, so capsules
    // slide on something and the ground doesn't show through the glass.
    private static void AddTrack(int belt, MoonConveyorPath path, Transform parent, float radius)
    {
        const float below = TrackBelow - 0.004f;
        var halfWidth = Mathf.Sqrt(radius * radius - below * below) - 0.01f;
        var floor = BeltLooks.Strip(path, parent, halfWidth, d => Shape(belt, path, d).Centre - below);
        var dark = TrackMaterial();
        if (floor != null && dark != null) BeltLooks.Show(parent, "Track", floor, dark, shadows: false);
    }

    // A post under each end that rises to meet a machine (the buried parts need none).
    private static void AddRampPosts(int belt, MoonConveyorPath path, Transform parent, float radius, Material metal)
    {
        var joined = JoinedOf(belt);
        var length = path.Length;
        foreach (var (at, open) in new[] { (Mathf.Min(0.45f, length / 2f), !joined.From), (Mathf.Max(length - 0.45f, length / 2f), !joined.To) })
        {
            if (!open || !BeltLooks.Frame(path, at, out var p, out var forward, out _, out var up)) continue;
            var drop = DropAt(GroundOf(belt, path), at);
            var bottom = Shape(belt, path, at).Centre - radius;
            var height = bottom + drop;
            if (height < 0.08f) continue;
            var post = Primitive(PrimitiveType.Cylinder, parent, "Post", metal);
            post.position = p + up * (bottom - height / 2f);
            post.rotation = Quaternion.LookRotation(forward, up);
            post.localScale = new Vector3(0.07f, height / 2f, 0.07f);
            var foot = Primitive(PrimitiveType.Cylinder, parent, "Foot", metal);
            foot.position = p + up * (-drop + 0.02f);
            foot.rotation = post.rotation;
            foot.localScale = new Vector3(0.24f, 0.02f, 0.24f);
        }
    }

    private static Transform Primitive(PrimitiveType shape, Transform parent, string name, Material material)
    {
        var go = GameObject.CreatePrimitive(shape);
        UnityEngine.Object.Destroy(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go.transform;
    }

    private static void AddRings(int belt, MoonConveyorPath path, Transform parent, float length, float radius)
    {
        var rings = new List<(Renderer, float)>();
        var count = Mathf.Max(1, Mathf.FloorToInt(length / RingSpacing));
        var first = (length - (count - 1) * RingSpacing) / 2f;
        var material = BeltLooks.Glow(Color.white);
        for (var i = 0; i < count; i++)
        {
            var at = first + i * RingSpacing;
            var mesh = BeltLooks.Tube(path, parent, radius, 0f, at - RingWidth / 2f, at + RingWidth / 2f, sides: 22, step: RingWidth,
                liftAt: d => Shape(belt, path, d).Centre);
            if (mesh == null) continue;
            rings.Add((BeltLooks.Show(parent, "Ring", mesh, material, shadows: false), at));
        }
        Rings[belt] = rings;
        Pressure.Recolor(belt);
    }

    // ---- placement holograms -------------------------------------------------------------------

    // The plan isn't a belt yet: show it sunk into the ground the whole way, as it will mostly look.
    private static float PreviewCentre(MoonConveyorPath path, float[] ground, float d) => -DropAt(ground, d) + BuriedCentre;

    private static void PreviewTube(MoonConveyorPath path, GameObject parts, Material hologram)
    {
        var drops = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f, MaxRise + 0.3f);
        var glass = BeltLooks.Tube(path, parts.transform, TubeRadius, 0f, sides: 18, step: 0.5f, liftAt: d => PreviewCentre(path, drops, d));
        if (glass != null) BeltLooks.Show(parts.transform, "Tube", glass, hologram, shadows: false);
    }

    private static void PreviewBooster(MoonConveyorPath path, GameObject parts, Material hologram)
    {
        var drops = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f, MaxRise + 0.3f);
        float Centre(float d) => -DropAt(drops, d) + BuriedCentre;
        var length = path.Length;
        var housing = BeltLooks.Tube(path, parts.transform, BoosterRadius, 0f, 0.1f, length - 0.1f, sides: 24, capped: true, liftAt: d => Centre(d) - HousingDrop);
        if (housing != null) BeltLooks.Show(parts.transform, "Housing", housing, hologram, shadows: false);
        var drum = BeltLooks.Tube(path, parts.transform, DrumRadius, 0f, 0.35f, length - 0.35f, sides: 16, capped: true, side: DrumSide,
            liftAt: d => Centre(d) - BuriedCentre + DrumLift);
        if (drum != null) BeltLooks.Show(parts.transform, "Compressor", drum, hologram, shadows: false);
    }

    // ---- tube splitter and merger --------------------------------------------------------------

    private const float PortHeight = 0.85f;   // the game's splitter/merger ports above its floor

    // A tube from each input port to each output port, in world space (the game's routes take the same curves), with
    // the across position (local x) of its port at the two-port end: which side is the junction's outside.
    private static List<(MoonConveyorPath Path, float OuterX)> JunctionPaths(int baseKind, Vector3 position, Quaternion rotation)
    {
        var paths = new List<(MoonConveyorPath, float)>();
        if (Moon.World is not { } w) return paths;
        var forward = rotation * Vector3.forward;
        var up = rotation * Vector3.up;
        foreach (var a in Junctions.InputsOf(baseKind))
            foreach (var b in Junctions.OutputsOf(baseKind))
                paths.Add((w.ConveyorPath(position + rotation * a, forward, position + rotation * b, forward, up, up),
                    baseKind == JunctionType.Splitter ? b.x : a.x));
        return paths;
    }

    // Glass tubes sunk into the floor along each route, with collars at the ports, the track inside, and coloured bands:
    // splitters in orange (one in, two out), mergers in cyan (two in, one out). The hologram also has arrows; a placed
    // one (structure) has a pressure ring on its single tube, and is dug in like the tubes.
    private static void DrawJunction(Transform t, int baseKind, Vector3 position, Quaternion rotation, Material glass, Material? metal, Material? dark,
        Material? hologram = null, int structure = 0)
    {
        var colour = JunctionColour(baseKind);
        var trim = hologram ?? BeltLooks.Glow(colour, 0.6f);
        var arrows = hologram ?? BeltLooks.Glow(colour, 2.2f);
        var up = rotation * Vector3.up;
        var across = rotation * Vector3.right;
        var rings = new List<Renderer>();
        var index = 0;
        foreach (var (path, outerX) in JunctionPaths(baseKind, position, rotation))
        {
            var first = index++ == 0;
            var length = path.Length;
            // Sunk into the ground like the tubes joining it (so they meet on slopes and bumps too).
            var drops = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f, MaxRise + 0.3f);
            float Centre(float d) => -DropAt(drops, d) + BuriedCentre;
            var tube = BeltLooks.Tube(path, t, TubeRadius, 0f, sides: 22, step: 0.2f, liftAt: Centre);
            if (tube != null) BeltLooks.Show(t, "Glass", tube, glass, shadows: false);
            // Collars at the ports in metal, a coloured band in the middle, and a wide one where the single tube is
            // (a splitter's input, a merger's output).
            var single = baseKind == JunctionType.Splitter ? (0.14f, 0.5f) : (length - 0.5f, length - 0.14f);
            foreach (var (from, to, band) in new[] { (0f, 0.12f, false), (length - 0.12f, length, false), (length / 2f - 0.07f, length / 2f + 0.07f, true), (single.Item1, single.Item2, true) })
            {
                var material = band ? trim : metal ?? hologram;
                if (material == null) continue;
                var collar = BeltLooks.Tube(path, t, TubeRadius + (band ? 0.03f : 0.035f), 0f, from, to, sides: 22, liftAt: Centre);
                if (collar != null) BeltLooks.Show(t, band ? "Band" : "Collar", collar, material);
            }
            // Arrows showing the way capsules go: only on the placement hologram (the placed piece keeps its colours).
            if (hologram != null)
                foreach (var at in new[] { 0.55f, 1.25f, length - 1.25f, length - 0.55f })
                {
                    if (!BeltLooks.Frame(path, at, out var p, out var forward, out _, out _)) continue;
                    var arrow = BeltLooks.Chevron(t, p + up * (Centre(at) + TubeRadius + 0.02f), forward, up, 0.42f, 0.42f);
                    BeltLooks.Show(t, "Arrow", arrow, arrows, shadows: false);
                }
            if (dark != null)
            {
                const float below = TrackBelow - 0.004f;
                var halfWidth = Mathf.Sqrt(TubeRadius * TubeRadius - below * below) - 0.01f;
                var floor = BeltLooks.Strip(path, t, halfWidth, d => Centre(d) - below);
                if (floor != null) BeltLooks.Show(t, "Track", floor, dark, shadows: false);
            }
            if (structure == 0) continue;
            // The pressure ring: one, on the single tube just past its wide band.
            if (first)
            {
                var at = baseKind == JunctionType.Splitter ? 0.62f : length - 0.62f;
                var ring = BeltLooks.Tube(path, t, TubeRadius + 0.02f, 0f, at - RingWidth / 2f, at + RingWidth / 2f, sides: 22, step: RingWidth, liftAt: Centre);
                if (ring != null) rings.Add(BeltLooks.Show(t, "Ring", ring, BeltLooks.Glow(Color.white), shadows: false));
            }
            // Dug in on the outside of each route (the side away from the other one).
            BeltLooks.Frame(path, length / 2f, out _, out _, out var right, out _);
            var outside = Math.Sign(VMath.Dot(right, across)) * Math.Sign(outerX);
            if (outside != 0) AddSpoil(t, path, 0f, length, d => Centre(d) - BuriedCentre, structure * 7 + index, false, false, outside);
        }
        if (structure == 0) return;
        JunctionRings[structure] = rings;
        Pressure.RecolorJunction(structure);
    }

    private static Color JunctionColour(int baseKind) =>
        baseKind == JunctionType.Splitter ? new Color(1f, 0.45f, 0.05f) : new Color(0.1f, 0.85f, 1f);

    private static void StyleJunction(int structure, GameObject parts)
    {
        if (Junctions.TypeOf(structure) is not { } type || !Junctions.Pose(structure, out var position, out var rotation)) return;
        var metal = BeltLooks.GameMaterial("MC - Brushed joint hardware") ?? BeltLooks.Glow(Color.gray, 0f);
        var dark = TrackMaterial();
        var tint = Color.Lerp(new Color(0.62f, 0.84f, 1f), JunctionColour(type.BaseKind), 0.35f);
        DrawJunction(parts.transform, type.BaseKind, position, rotation, BeltLooks.Glass(new Color(tint.r, tint.g, tint.b, 0.36f)), metal, dark, structure: structure);
    }

    // The hologram in the game's colour (it shows whether it can be built), with the arrows and bands glowing in the
    // junction's own colour (the game recolours its hologram material) so splitter and merger can be told apart
    // before placing.
    private static void PreviewJunction(int baseKind, GameObject parts, Material hologram) =>
        DrawJunction(parts.transform, baseKind, parts.transform.position, parts.transform.rotation, hologram, null, null, BeltLooks.Glow(JunctionColour(baseKind), 2.2f));

    // ---- booster -------------------------------------------------------------------------------

    private static void StyleBooster(int belt, GameObject parts)
    {
        var path = Belts.PathOf(belt);
        if (path == null) return;
        Ground.Remove(belt);
        var t = parts.transform;
        var length = path.Length;
        var body = BeltLooks.GameMaterial("MC - Graphite chassis") ?? BeltLooks.Glow(Color.gray, 0f);
        var trim = BeltLooks.GameMaterial("MC - Tangerine safety trim") ?? BeltLooks.Glow(new Color(1f, 0.5f, 0.1f), 0f);
        var metal = BeltLooks.GameMaterial("MC - Brushed joint hardware") ?? body;
        float CentreAt(float d) => Shape(belt, path, d).Centre;

        // Housing: a capped drum around the pipe, sunk like the tubes, with metal flanges and orange bands.
        var housing = BeltLooks.Tube(path, t, BoosterRadius, 0f, 0.1f, length - 0.1f, sides: 26, capped: true, liftAt: d => CentreAt(d) - HousingDrop);
        if (housing != null) BeltLooks.Show(t, "Housing", housing, body);
        foreach (var (from, to) in new[] { (0f, 0.1f), (length - 0.1f, length) })
        {
            var flange = BeltLooks.Tube(path, t, TubeRadius + 0.06f, 0f, from, to, sides: 22, capped: true, liftAt: CentreAt);
            if (flange != null) BeltLooks.Show(t, "Flange", flange, metal);
        }
        foreach (var at in new[] { 0.26f, length - 0.26f })
        {
            var band = BeltLooks.Tube(path, t, BoosterRadius + 0.015f, 0f, at - 0.05f, at + 0.05f, sides: 26, liftAt: d => CentreAt(d) - HousingDrop);
            if (band != null) BeltLooks.Show(t, "Band", band, trim);
        }

        // Compressor beside it, low on the ground: a drum lying along the housing, piped into it, with a glowing
        // intake at the front.
        float DrumAt(float d) => CentreAt(d) - BuriedCentre + DrumLift;
        const float drumFrom = 0.4f;
        var drumTo = length - 0.4f;
        var drum = BeltLooks.Tube(path, t, DrumRadius, 0f, drumFrom, drumTo, sides: 16, capped: true, side: DrumSide, liftAt: DrumAt);
        if (drum != null) BeltLooks.Show(t, "Compressor", drum, metal);
        var intake = BeltLooks.Tube(path, t, 0.11f, 0f, drumFrom - 0.03f, drumFrom, sides: 16, capped: true, side: DrumSide, liftAt: DrumAt);
        if (intake != null) BeltLooks.Show(t, "Intake", intake, BeltLooks.GameMaterial("MC - Cyan frosted diffuser") ?? metal, shadows: false);
        foreach (var at in new[] { drumFrom + 0.15f, drumTo - 0.15f })
        {
            if (!BeltLooks.Frame(path, at, out var p, out var forward, out var right, out var up)) continue;
            var pipe = Primitive(PrimitiveType.Cylinder, t, "Pipe", trim);
            pipe.position = p + up * DrumAt(at) + right * ((BoosterRadius + DrumSide - DrumRadius) / 2f);
            pipe.rotation = Quaternion.LookRotation(forward, right);
            pipe.localScale = new Vector3(0.07f, (DrumSide - DrumRadius - BoosterRadius) / 2f + 0.04f, 0.07f);
        }

        // Air: a stream shot from the booster into the tube (seen through the glass), and the compressor's vents.
        var air = new List<(Puffs, float)>();
        if (BeltLooks.Frame(path, length, out var outlet, out var ahead, out _, out var outletUp))
            air.Add((Puffs.Create(t, outlet + outletUp * (CentreAt(length) + 0.05f), ahead, new Color(1f, 1f, 1f, 0.35f),
                rate: 14f, speed: 3.2f, size: 0.16f, lifetime: 0.55f), 14f));
        if (BeltLooks.Frame(path, drumTo, out var vent, out var back, out var side, out var ventUp))
            foreach (var s in new[] { -1f, 1f })
                air.Add((Puffs.Create(t, vent + ventUp * (DrumAt(drumTo) + DrumRadius) + side * (DrumSide + 0.08f * s), ventUp + side * (0.4f * s) - back * 0.3f,
                    new Color(0.92f, 0.96f, 1f, 0.45f), rate: 5f, speed: 0.9f, size: 0.2f, lifetime: 1.1f), 5f));
        Air[belt] = air;

        // A quiet hiss while powered (its volume follows the power, see Pressure.Recolor).
        if (BeltLooks.Frame(path, length / 2f, out var middle, out _, out _, out var middleUp))
        {
            var hiss = GameSounds.Loop(t, middle + middleUp * CentreAt(length / 2f), "garage-flow-loop", 0f, 16f);
            if (hiss != null) { hiss.pitch = 1.35f; Hiss[belt] = hiss; }
        }

        // A glowing ring around the middle shows its own state (green: running, red: no power).
        var status = BeltLooks.Tube(path, t, BoosterRadius + 0.02f, 0f, length / 2f - 0.08f, length / 2f + 0.08f, sides: 26, liftAt: d => CentreAt(d) - HousingDrop);
        var rings = new List<(Renderer, float)>();
        if (status != null) rings.Add((BeltLooks.Show(t, "Status", status, BeltLooks.Glow(Color.white), shadows: false), -1f));
        Rings[belt] = rings;
        Pressure.Recolor(belt);
    }
}
