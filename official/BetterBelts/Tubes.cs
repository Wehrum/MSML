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
    private const float ProfileStep = 0.5f;
    private const float MaxDrop = 1.6f;

    /// <summary>Each booster's air: (puffs, rate at full power).</summary>
    public static readonly Dictionary<int, List<(Puffs Puffs, float Rate)>> Air = new();

    /// <summary>Each booster's hiss.</summary>
    public static readonly Dictionary<int, AudioSource> Hiss = new();

    /// <summary>Each tube's (or booster's) glowing rings and how far along the belt they sit.</summary>
    public static readonly Dictionary<int, List<(Renderer Ring, float At)>> Rings = new();

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
            HideBelt = true,
            OnGround = true,
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
            FixedLength = BoosterLength,
            HideBelt = true,
            OnGround = true,
            PassThrough = true,
            ColliderTop = 0.12f,
            DeleteRidersOnRemove = true,
            NoChaining = true,
            Style = StyleBooster,
            Preview = PreviewBooster,
            RiderPose = Pose,
        });
        Booster.InsertsInto = new HashSet<BeltType> { Tube };
        ApplyPrices();
        Belts.TypeChanged += (belt, type) =>
        {
            if (type != Tube && type != Booster) Rings.Remove(belt);
            if (type != Booster) { Air.Remove(belt); Hiss.Remove(belt); }
            Ground.Remove(belt);
        };
        Belts.Changed += RejoinPipes;
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

    // Joined straight to another pipe at each end (through a splitter, merger or machine the pipe rises to meet it).
    private static (bool From, bool To) Neighbours(int belt) =>
        (Belts.StartsAt(belt).Structure == 0 && Belts.Upstream(belt).Exists(IsPipe),
         Belts.EndsAt(belt).Structure == 0 && Belts.Downstream(belt).Exists(IsPipe));

    // ---- shape along the belt ------------------------------------------------------------------

    private static (bool From, bool To) JoinedOf(int belt)
    {
        if (!Joined.TryGetValue(belt, out var joined))
            Joined[belt] = joined = Neighbours(belt);
        return joined;
    }

    private static float[] GroundOf(int belt, MoonConveyorPath path)
    {
        if (!Ground.TryGetValue(belt, out var profile)) Ground[belt] = profile = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f);
        return profile;
    }

    // How far below the belt's surface the ground is (far-off or missing ground: as if built at the usual height).
    private static float DropAt(float[] ground, float distance)
    {
        var drop = BeltLooks.AtDistance(ground, ProfileStep, distance);
        return drop > MaxDrop ? 0.4f : Mathf.Max(0f, drop);
    }

    // 1 where the pipe is sunk into the ground, easing to 0 at ends that meet something that isn't a pipe.
    private static float Buried((bool From, bool To) joined, float length, float distance)
    {
        var t = 1f;
        if (!joined.From) t = Mathf.Min(t, distance / Ramp);
        if (!joined.To) t = Mathf.Min(t, (length - distance) / Ramp);
        return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
    }

    /// <summary>The pipe's centre above the belt's surface at a distance along it, and how buried it is there.</summary>
    private static (float Centre, float Buried) Shape(int belt, MoonConveyorPath path, float distance)
    {
        // Boosters are too short to rise: they always sit in the ground (tubes rise to meet machines).
        var buried = Belts.TypeOf(belt) == Booster ? 1f : Buried(JoinedOf(belt), path.Length, distance);
        var centre = Mathf.Lerp(RaisedCentre, -DropAt(GroundOf(belt, path), distance) + BuriedCentre, buried);
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
        var lying = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(buried * 2f, 1f)));
        if (capsule.cargo == MoonCargo.Stone)
        {
            position += up * (capsuleCentre - StoneHalfHeight);
            return;
        }
        // On its side, along the pipe, centred on the rider's point (the capsule's pivot is its bottom end); tipping
        // up where the pipe rises to meet a machine.
        var lyingPosition = position + up * capsuleCentre - forward * (CapsuleLength / 2f);
        var upright = position + up * Mathf.Min(capsuleCentre, 0f);
        position = Vector3.Lerp(upright, lyingPosition, lying);
        rotation = Quaternion.Slerp(rotation, Quaternion.LookRotation(up, forward), lying);
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
    }

    // The track: a dark floor across the inside of the pipe, level with the ground where it's buried, so capsules
    // slide on something and the ground doesn't show through the glass.
    private static void AddTrack(int belt, MoonConveyorPath path, Transform parent, float radius)
    {
        const float below = TrackBelow - 0.004f;
        var halfWidth = Mathf.Sqrt(radius * radius - below * below) - 0.01f;
        var floor = BeltLooks.Strip(path, parent, halfWidth, d => Shape(belt, path, d).Centre - below);
        var dark = BeltLooks.GameMaterial("MC - Higgsfield molded belt") ?? BeltLooks.GameMaterial("MC - Graphite chassis");
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
        Object.Destroy(go.GetComponent<Collider>());
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

    // The plan isn't a belt yet: show it sunk into the ground along its middle.
    private static float PreviewCentre(MoonConveyorPath path, float[] ground, float d)
    {
        var buried = Buried((false, false), path.Length, d);
        return Mathf.Lerp(RaisedCentre, -DropAt(ground, d) + BuriedCentre, buried);
    }

    private static void PreviewTube(MoonConveyorPath path, GameObject parts, Material hologram)
    {
        var drops = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f);
        var glass = BeltLooks.Tube(path, parts.transform, TubeRadius, 0f, sides: 18, step: 0.5f, liftAt: d => PreviewCentre(path, drops, d));
        if (glass != null) BeltLooks.Show(parts.transform, "Tube", glass, hologram, shadows: false);
    }

    private static void PreviewBooster(MoonConveyorPath path, GameObject parts, Material hologram)
    {
        var drops = BeltLooks.GroundProfile(path, ProfileStep, MaxDrop + 0.5f);
        float Centre(float d) => -DropAt(drops, d) + BuriedCentre;
        var length = path.Length;
        var housing = BeltLooks.Tube(path, parts.transform, BoosterRadius, 0f, 0.1f, length - 0.1f, sides: 24, capped: true, liftAt: Centre);
        if (housing != null) BeltLooks.Show(parts.transform, "Housing", housing, hologram, shadows: false);
        var drum = BeltLooks.Tube(path, parts.transform, 0.15f, 0f, 0.35f, length - 0.35f, sides: 16, capped: true,
            liftAt: d => Centre(d) + BoosterRadius + 0.17f);
        if (drum != null) BeltLooks.Show(parts.transform, "Compressor", drum, hologram, shadows: false);
    }

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
        var housing = BeltLooks.Tube(path, t, BoosterRadius, 0f, 0.1f, length - 0.1f, sides: 26, capped: true, liftAt: CentreAt);
        if (housing != null) BeltLooks.Show(t, "Housing", housing, body);
        foreach (var (from, to) in new[] { (0f, 0.1f), (length - 0.1f, length) })
        {
            var flange = BeltLooks.Tube(path, t, TubeRadius + 0.06f, 0f, from, to, sides: 22, capped: true, liftAt: CentreAt);
            if (flange != null) BeltLooks.Show(t, "Flange", flange, metal);
        }
        foreach (var at in new[] { 0.26f, length - 0.26f })
        {
            var band = BeltLooks.Tube(path, t, BoosterRadius + 0.015f, 0f, at - 0.05f, at + 0.05f, sides: 26, liftAt: CentreAt);
            if (band != null) BeltLooks.Show(t, "Band", band, trim);
        }

        // Compressor on top: a drum lying along the housing, piped into it, with a glowing intake at the front.
        float DrumAt(float d) => CentreAt(d) + BoosterRadius + 0.17f;
        const float drumFrom = 0.4f;
        var drumTo = length - 0.4f;
        var drum = BeltLooks.Tube(path, t, 0.15f, 0f, drumFrom, drumTo, sides: 16, capped: true, liftAt: DrumAt);
        if (drum != null) BeltLooks.Show(t, "Compressor", drum, metal);
        var intake = BeltLooks.Tube(path, t, 0.11f, 0f, drumFrom - 0.03f, drumFrom, sides: 16, capped: true, liftAt: DrumAt);
        if (intake != null) BeltLooks.Show(t, "Intake", intake, BeltLooks.GameMaterial("MC - Cyan frosted diffuser") ?? metal, shadows: false);
        foreach (var at in new[] { drumFrom + 0.15f, drumTo - 0.15f })
        {
            if (!BeltLooks.Frame(path, at, out var p, out var forward, out _, out var up)) continue;
            var pipe = Primitive(PrimitiveType.Cylinder, t, "Pipe", trim);
            pipe.position = p + up * (CentreAt(at) + BoosterRadius + 0.05f);
            pipe.rotation = Quaternion.LookRotation(forward, up);
            pipe.localScale = new Vector3(0.07f, 0.08f, 0.07f);
        }

        // Air: a stream shot from the booster into the tube (seen through the glass), and the compressor's vents.
        var air = new List<(Puffs, float)>();
        if (BeltLooks.Frame(path, length, out var outlet, out var ahead, out _, out var outletUp))
            air.Add((Puffs.Create(t, outlet + outletUp * (CentreAt(length) + 0.05f), ahead, new Color(1f, 1f, 1f, 0.35f),
                rate: 14f, speed: 3.2f, size: 0.16f, lifetime: 0.55f), 14f));
        if (BeltLooks.Frame(path, drumTo, out var vent, out var back, out var side, out var ventUp))
            foreach (var s in new[] { -1f, 1f })
                air.Add((Puffs.Create(t, vent + ventUp * DrumAt(drumTo) + side * (0.12f * s), side * s + ventUp * 0.6f - back * 0.3f,
                    new Color(0.92f, 0.96f, 1f, 0.45f), rate: 5f, speed: 0.9f, size: 0.2f, lifetime: 1.1f), 5f));
        Air[belt] = air;

        // A quiet hiss while powered (its volume follows the power, see Pressure.Recolor).
        if (BeltLooks.Frame(path, length / 2f, out var middle, out _, out _, out var middleUp))
        {
            var hiss = GameSounds.Loop(t, middle + middleUp * CentreAt(length / 2f), "garage-flow-loop", 0f, 16f);
            if (hiss != null) { hiss.pitch = 1.35f; Hiss[belt] = hiss; }
        }

        // A glowing ring around the middle shows its own state (green: running, red: no power).
        var status = BeltLooks.Tube(path, t, BoosterRadius + 0.02f, 0f, length / 2f - 0.08f, length / 2f + 0.08f, sides: 26, liftAt: CentreAt);
        var rings = new List<(Renderer, float)>();
        if (status != null) rings.Add((BeltLooks.Show(t, "Status", status, BeltLooks.Glow(Color.white), shadows: false), -1f));
        Rings[belt] = rings;
        Pressure.Recolor(belt);
    }
}
