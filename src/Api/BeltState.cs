using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Which belts have which type, belt speeds, and keeping both in the save and in step with the crew.
/// <para>
/// Types are saved in <see cref="MoonData"/> ("msml.belts": type id -> belt ids). In a crew the host's table is the
/// truth: crewmates ask the host to change a belt, and the host sends the whole table to everyone when it changes
/// (and every few seconds, so a lost message heals itself). Crew messages name belts by their ends as well as their
/// ids, since each machine counts its own belt ids.
/// </para>
/// <para>
/// Speed: belts far from the camera are only stepped every few frames, with the time they missed kept in
/// <c>MoonWorld.beltOwed</c>. Adding (speed - 1) x frame time to a belt's owed time each frame makes the game move its
/// capsules that much further, with its own spacing and hand-off rules.
/// </para>
/// </summary>
internal static class BeltState
{
    private const string DataKey = "msml.belts";
    // Crew messages carry each belt's ends: belt ids are counted on each machine, so a crewmate's ids needn't match
    // the host's. Belts are matched by where they start and end.
    private const string SetTopic = "msml.belts.set2";
    private const string TableTopic = "msml.belts.table2";
    private const float MissingGrace = 10f;   // a typed belt may arrive after its type (crew); keep the type that long
    private const float PendingGrace = 5f;    // a crewmate's own change survives tables sent before the host saw it
    private const float AwaitGrace = 15f;     // a typed belt not built here yet: keep looking for it that long
    private const float MatchReach = 0.15f;   // ends this close are the same belt's
    private const float MaxOwed = 1f;

    private static readonly Dictionary<int, BeltType> Typed = new();
    private static readonly Dictionary<BeltType, HashSet<int>> ByType = new();
    private static readonly Dictionary<int, float> Speeds = new();
    private static readonly Dictionary<int, float> MissingSince = new();
    private static readonly Dictionary<int, (BeltType? Type, float At)> Pending = new();
    // Crew: typed belts heard of whose belt isn't here yet (by its ends).
    private static readonly List<(BeltType? Type, Vector3 Start, Vector3 End, int Id, float Since)> Awaiting = new();
    private static readonly Dictionary<int, (Vector3 Start, Vector3 End)> KnownEnds = new();
    private static Dictionary<string, List<int>> _unknown = new();

    private static MoonWorld? _world;
    private static bool _changed, _save, _broadcast, _restyleAll;
    private static float _nextReconcile, _nextBroadcast;
    private static int _beltCount = -1;
    private static bool _handlersAdded;

    internal static MoonWorld? World => _world != null && !_world.WasCollected ? _world : null;

    internal static IEnumerable<int> Keys(Il2CppSystem.Collections.Generic.Dictionary<int, MoonConveyorSnapshot> d)
    {
        var keys = new List<int>(d.Count);
        foreach (var k in d.Keys) keys.Add(k);
        return keys;
    }

    internal static BeltType? TypeOf(int belt) => Typed.TryGetValue(belt, out var t) ? t : null;

    internal static IReadOnlyCollection<int> OfType(BeltType type) =>
        ByType.TryGetValue(type, out var set) ? set.ToList() : (IReadOnlyCollection<int>)Array.Empty<int>();

    internal static float SpeedOf(int belt) =>
        Speeds.TryGetValue(belt, out var s) ? s : TypeOf(belt)?.Speed ?? 1f;

    internal static void SetSpeed(int belt, float multiplier) => Speeds[belt] = multiplier;

    internal static void ClearSpeed(int belt) => Speeds.Remove(belt);

    internal static void AddHandlers()
    {
        if (_handlersAdded) return;
        _handlersAdded = true;
        MoonData.Loaded += OnLoaded;
        CrewMessages.On(SetTopic, OnSetRequest);
        CrewMessages.On(TableTopic, OnTable);
        CrewMessages.CrewmateReady += _ => _broadcast = true;
    }

    // ---- types ---------------------------------------------------------------------------------

    /// <param name="request">A change made on this machine (asked of the host when in someone else's crew).</param>
    internal static void SetType(int belt, BeltType? type, bool request)
    {
        if (TypeOf(belt) == type) return;
        Assign(belt, type);
        if (CrewMessages.IsHost) { _save = true; _broadcast = true; }
        else if (request)
        {
            Pending[belt] = (type, Time.unscaledTime);
            CrewMessages.SendToHost(SetTopic, EncodeSet(belt, type));
        }
    }

    private static void Assign(int belt, BeltType? type)
    {
        var old = TypeOf(belt);
        if (old == type) return;
        if (old != null) { Typed.Remove(belt); ByType[old].Remove(belt); }
        if (type != null)
        {
            Typed[belt] = type;
            if (!ByType.TryGetValue(type, out var set)) ByType[type] = set = new HashSet<int>();
            set.Add(belt);
            if (EndsOf(belt, out var start, out var end)) KnownEnds[belt] = (start, end);
        }
        else KnownEnds.Remove(belt);
        // Speeds set for the old type don't carry over.
        Speeds.Remove(belt);
        _changed = true;
        Restyle(belt);
        Belts.RaiseTypeChanged(belt, type);
    }

    /// <summary>Drops a removed belt's type and speed.</summary>
    internal static void Forget(int belt)
    {
        Speeds.Remove(belt);
        MissingSince.Remove(belt);
        DropStyle(belt);
        if (TypeOf(belt) == null) return;
        Assign(belt, null);
        if (CrewMessages.IsHost) { _save = true; _broadcast = true; }
    }

    internal static void Clear()
    {
        Typed.Clear();
        ByType.Clear();
        Speeds.Clear();
        MissingSince.Clear();
        Pending.Clear();
        Awaiting.Clear();
        KnownEnds.Clear();
        Riders.Clear();
        Queues.Clear();
        Missing.Clear();
        ViewSeen.Clear();
        StyleQueue.Clear();
        NextFarSync.Clear();
        Queued.Clear();
        BeltNetwork.Invalidate();
        foreach (var belt in Styled.Keys.ToList()) DropStyle(belt);
        HiddenViews.Clear();
        PassColliders.Clear();
        _unknown = new Dictionary<string, List<int>>();
        _world = null;
        _beltCount = -1;
    }

    // ---- views ---------------------------------------------------------------------------------

    // Each styled belt's parts, kept apart from the game's view (its build/removal animations take over everything
    // under the view) and kept in step with it in Reconcile.
    private static readonly Dictionary<int, GameObject> Styled = new();

    internal static void Restyle(int belt)
    {
        try
        {
            DropStyle(belt);
            var view = Belts.ViewOf(belt);
            if (view == null) return;
            var type = TypeOf(belt);
            HideGameBelt(belt, view, type?.HideBelt == true);
            SetPassThrough(view, type?.PassThrough == true, type?.ColliderTop);
            if (type is not { Style: { } style }) return;
            var parts = new GameObject($"MSML belt {belt} ({type.Name})");
            Styled[belt] = parts;
            try { style(belt, parts); }
            catch (Exception e) { Log.Error($"Styling a {type.Name} failed: {e}"); }
            parts.SetActive(view.activeInHierarchy);
        }
        catch (Exception e) { Log.Error($"Restyling belt {belt} failed: {e}"); }
    }

    internal static void DropStyle(int belt)
    {
        if (!Styled.TryGetValue(belt, out var parts)) return;
        Styled.Remove(belt);
        if (parts != null && !parts.WasCollected) UnityEngine.Object.Destroy(parts);
    }

    // Views the game spawns (streaming in as the player moves) get their look over the next frames, a couple of
    // milliseconds' worth each frame: building the looks of many tubes in one frame was a hitch. Their game belt is
    // hidden straight away (no plain belt flashes up).
    private static readonly List<int> StyleQueue = new();
    private static readonly HashSet<int> Queued = new();
    private const double StyleBudgetMs = 2.0;

    internal static void RestyleSoon(int belt)
    {
        if (TypeOf(belt) is not { Style: not null } type) { Restyle(belt); return; }
        try
        {
            DropStyle(belt);
            var view = Belts.ViewOf(belt);
            if (view == null) return;
            HideGameBelt(belt, view, type.HideBelt);
            SetPassThrough(view, type.PassThrough, type.ColliderTop);
        }
        catch (Exception e) { Log.Error($"Restyling belt {belt} failed: {e}"); return; }
        if (Queued.Add(belt)) StyleQueue.Add(belt);
    }

    private static void StyleQueued()
    {
        if (StyleQueue.Count == 0) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var done = 0;
        while (done < StyleQueue.Count && (done == 0 || watch.Elapsed.TotalMilliseconds < StyleBudgetMs))
        {
            var belt = StyleQueue[done++];
            Queued.Remove(belt);
            if (!Styled.ContainsKey(belt)) Restyle(belt);   // unless restyled meanwhile
        }
        StyleQueue.RemoveRange(0, done);
    }

    /// <summary>Gives a removed belt's parts to the game's dismantle effect (which destroys them when done).</summary>
    internal static void HandOverStyle(int belt, GameObject dismantled)
    {
        if (!Styled.TryGetValue(belt, out var parts)) return;
        Styled.Remove(belt);
        if (parts == null || parts.WasCollected) return;
        parts.SetActive(true);
        parts.transform.SetParent(dismantled.transform, true);
    }

    /// <summary>The parts drawn for the belt whose view this is, when the game's belt is hidden; else null.</summary>
    internal static GameObject? PartsFor(GameObject view)
    {
        foreach (var (belt, parts) in Styled)
        {
            if (parts == null || parts.WasCollected || TypeOf(belt)?.HideBelt != true) continue;
            var v = Belts.ViewOf(belt);
            if (v != null && v.Pointer == view.Pointer) return parts;
        }
        return null;
    }

    // The game's belt is the view's own renderer (chassis, guards, legs) plus its tread, drawn by the network. Only
    // views hidden here are shown again (the game reuses views; it may hide belts for its own reasons).
    private static readonly HashSet<IntPtr> HiddenViews = new();

    private static void HideGameBelt(int belt, GameObject view, bool hide)
    {
        if (!hide && !HiddenViews.Remove(view.Pointer)) return;
        if (hide) HiddenViews.Add(view.Pointer);
        foreach (var r in view.GetComponents<Renderer>()) r.enabled = !hide;
        World?.ConveyorViews?.SetTreadHidden(belt, hide);
    }

    private static bool _keepingHidden;

    /// <summary>After the game rebuilds a belt's look: a hidden belt (and its tread) stays hidden.</summary>
    internal static void KeepHidden(int belt)
    {
        if (_keepingHidden || TypeOf(belt)?.HideBelt != true || Belts.ViewOf(belt) is not { } view) return;
        try { _keepingHidden = true; HideGameBelt(belt, view, true); }
        catch (Exception e) { Log.Error($"Keeping belt {belt} hidden failed: {e.Message}"); }
        finally { _keepingHidden = false; }
    }

    internal static void KeepAllHidden() => _rehideAll = true;

    private static bool _rehideAll;

    private static void RehideAll()
    {
        if (!_rehideAll) return;
        _rehideAll = false;
        foreach (var (type, belts) in ByType)
            if (type.HideBelt)
                foreach (var belt in belts.ToList()) KeepHidden(belt);
    }

    // The game hides a belt while previewing changes to it (a splitter cutting it, an end being moved).
    private static bool GameHidden(int belt)
    {
        var views = World?.ConveyorViews?.views;
        return views != null && views.ContainsKey(belt) && views[belt]?.hidden == true;
    }

    // Styles follow their belt's view: hidden with it, gone with it; the game's belt stays hidden. The game's view is
    // only gone through (renderers, colliders) when it's a new one for the belt, plus a few belts each time as a
    // safety net: fetching every view's components every time left a lot of garbage on the game's heap.
    private static readonly Dictionary<int, IntPtr> ViewSeen = new();
    private static int _deepCursor;
    private const int DeepPerSync = 12;
    private static List<int> _syncBelts = new();
    private static int _syncCursor;
    private const float FarSync = 200f;
    private static readonly Dictionary<int, float> NextFarSync = new();

    // Keeps each look in step with its belt's view, a slice of the belts each frame (all of them every 'period'
    // seconds): doing them all at once was a few calls into the game per belt in one frame, a hitch with thousands.
    private static void SyncStyles(bool building, float period)
    {
        if (_syncCursor >= _syncBelts.Count)
        {
            // A new round.
            _syncBelts = Styled.Keys.ToList();
            _syncCursor = 0;
            _deepCursor = _syncBelts.Count == 0 ? 0 : (_deepCursor + DeepPerSync) % _syncBelts.Count;
            if (!building) SyncPassThrough();
            if (_syncBelts.Count == 0) return;
        }
        var count = _syncBelts.Count;
        var slice = Math.Max(32, (int)Math.Ceiling(count * Math.Min(1f, Time.unscaledDeltaTime / period)));
        var end = Math.Min(count, _syncCursor + slice);
        // Belts far from the camera are checked less often (a few calls into the game each, every round, for
        // thousands of belts left a lot of garbage behind).
        var now = Time.unscaledTime;
        var camera = Camera.main;
        var eye = camera != null ? camera.transform.position : default;
        for (var i = _syncCursor; i < end; i++)
        {
            var belt = _syncBelts[i];
            if (!Styled.TryGetValue(belt, out var parts)) continue;
            if (camera != null && !building && BeltNetwork.TryEnds(belt, out var a, out var b) &&
                VMath.SqrDistance(VMath.Lerp(a, b, 0.5f), eye) > FarSync * FarSync)
            {
                if (NextFarSync.TryGetValue(belt, out var next) && now < next) continue;
                NextFarSync[belt] = now + 2f;
            }
            var view = Belts.ViewOf(belt);
            var type = TypeOf(belt);
            if (parts == null || parts.WasCollected || view == null || type?.Style == null) { DropStyle(belt); ViewSeen.Remove(belt); continue; }
            // The game hides a belt while previewing a change to it: only while building.
            var shown = view.activeInHierarchy && !(building && GameHidden(belt));
            if (parts.activeSelf != shown) parts.SetActive(shown);
            var deep = (i - _deepCursor + count) % count < DeepPerSync;
            if (!deep && ViewSeen.TryGetValue(belt, out var seen) && seen == view.Pointer) continue;
            ViewSeen[belt] = view.Pointer;
            if (type.HideBelt)
                foreach (var r in view.GetComponents<Renderer>())
                    if (r.enabled) { HideGameBelt(belt, view, true); break; }
            if (type.PassThrough) SetPassThrough(view, true, type.ColliderTop);
        }
        _syncCursor = end;
    }

    // Pass-through belts without a look of their own.
    private static void SyncPassThrough()
    {
        foreach (var (type, typed) in ByType)
        {
            if (!type.PassThrough || type.Style != null) continue;
            foreach (var belt in typed)
                if (Belts.ViewOf(belt) is { } view && (!ViewSeen.TryGetValue(belt, out var seen) || seen != view.Pointer))
                {
                    ViewSeen[belt] = view.Pointer;
                    SetPassThrough(view, true, type.ColliderTop);
                }
        }
    }

    // PassThrough belts: their colliders go on a layer that collides with nothing (vehicles and players pass over),
    // but raycasts (the build tool's) still hit it. Only colliders moved here are put back (views are reused).
    internal const int PassLayer = 29;
    private static readonly Dictionary<IntPtr, (int Layer, Vector3 Position)> PassColliders = new();
    private static bool _passLayerReady;

    internal static void SetPassThrough(GameObject view, bool pass, float? top = null)
    {
        if (pass && !_passLayerReady)
        {
            _passLayerReady = true;
            for (var layer = 0; layer < 32; layer++) Physics.IgnoreLayerCollision(PassLayer, layer, true);
        }
        foreach (var collider in view.GetComponentsInChildren<Collider>(true))
        {
            var go = collider.gameObject;
            if (pass)
            {
                if (go.layer == PassLayer) continue;
                PassColliders[go.Pointer] = (go.layer, go.transform.localPosition);
                go.layer = PassLayer;
                if (top is { } t) Lower(collider, t);
            }
            else if (PassColliders.TryGetValue(go.Pointer, out var was))
            {
                PassColliders.Remove(go.Pointer);
                go.layer = was.Layer;
                go.transform.localPosition = was.Position;
            }
        }
    }

    // Moves a collider down so its top is 'top' metres above the solid ground below it (belts skipped).
    private static void Lower(Collider collider, float top)
    {
        var up = collider.transform.up;
        var centre = collider.bounds.center;
        var above = centre + up * 3f;
        if (!collider.Raycast(new Ray(above, -up), out var surface, 6f)) return;
        var views = World?.ConveyorViews;
        var ground = float.MaxValue;
        foreach (var hit in Physics.RaycastAll(above, -up, 40f, ~(1 << PassLayer), QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || (views != null && views.BeltAt(hit.collider) != 0)) continue;
            if (hit.collider.GetComponentInParent<MoonDustCanisterView>() != null) continue;
            if (hit.distance > surface.distance && hit.distance < ground) ground = hit.distance;
        }
        if (ground == float.MaxValue) return;
        var drop = ground - surface.distance - top;
        if (drop > 0f) collider.transform.position -= up * drop;
    }

    // ---- capsules on typed belts (for RiderPose) -----------------------------------------------

    private static readonly Dictionary<int, (int Belt, float Distance, BeltType Type)> Riders = new();
    private static int _itemsOffset = -1, _sizeOffset;

    /// <summary>After the belts moved: which capsules ride belts whose type poses them, and where.</summary>
    internal static void MapRiders(MoonWorld world)
    {
        Riders.Clear();
        foreach (var (type, belts) in ByType)
        {
            if (type.RiderPose == null) continue;
            foreach (var belt in belts)
                if (QueueOf(world, belt) is { } queue) MapQueue(queue.Pointer, belt, type);
        }
        // Capsules passing through splitters and mergers whose type draws them as on a belt type.
        foreach (var (structure, junction) in JunctionState.All)
        {
            if (junction.RouteType is not { RiderPose: not null } type) continue;
            foreach (var route in BeltNetwork.Routes(structure))
                if (QueueOf(world, route) is { } queue) MapQueue(queue.Pointer, route, type);
        }
    }

    private static void MapQueue(IntPtr list, int belt, BeltType type)
    {
        EnsureOffsets(list);
        var count = System.Runtime.InteropServices.Marshal.ReadInt32(list + _sizeOffset);
        if (count <= 0) return;
        var items = System.Runtime.InteropServices.Marshal.ReadIntPtr(list + _itemsOffset);
        for (var i = 0; i < count; i++)
        {
            var entry = items + 0x20 + i * 8;
            Riders[System.Runtime.InteropServices.Marshal.ReadInt32(entry + 4)] =
                (belt, BitConverter.Int32BitsToSingle(System.Runtime.InteropServices.Marshal.ReadInt32(entry)), type);
        }
    }

    // Each belt's capsule queue, looked up from the game at most once a second (the list object stays the same while
    // the belt exists; holding it keeps it alive, so a replaced one is only stale until the next refresh). Read
    // directly from memory every frame: asking the game every frame left garbage on its heap. The game only makes a
    // queue when the first capsule arrives, so belts without one are asked about again whenever the number of queues
    // changes (else the first capsules through a new tube, splitter or merger weren't posed, popping up out of the
    // tube); asking about every empty belt every frame cost a lot with thousands of them.
    private static readonly Dictionary<int, Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>>> Queues = new();
    private static readonly HashSet<int> Missing = new();
    private static float _queuesUntil;
    private static int _queuesVersion = -1, _queueCountFrame = -1, _queueCount = -1;

    private static Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>>? QueueOf(MoonWorld world, int belt)
    {
        var now = Time.unscaledTime;
        if (now >= _queuesUntil || _queuesVersion != BeltNetwork.Version)
        {
            Queues.Clear();
            Missing.Clear();
            _queuesUntil = now + 1f;
            _queuesVersion = BeltNetwork.Version;
        }
        var queues = world.beltQueues;
        var frame = Time.frameCount;
        if (frame != _queueCountFrame)
        {
            _queueCountFrame = frame;
            var count = queues.Count;
            if (count != _queueCount) { _queueCount = count; Missing.Clear(); }
        }
        if (Queues.TryGetValue(belt, out var queue)) return queue;
        if (Missing.Contains(belt)) return null;
        if (!queues.ContainsKey(belt)) { Missing.Add(belt); return null; }
        return Queues[belt] = queues[belt];
    }

    // How many capsules are on a queue (read directly).
    private static int CountOf(Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>> queue)
    {
        EnsureOffsets(queue.Pointer);
        return System.Runtime.InteropServices.Marshal.ReadInt32(queue.Pointer + _sizeOffset);
    }

    private static void EnsureOffsets(IntPtr list)
    {
        if (_itemsOffset >= 0) return;
        var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(list);
        _itemsOffset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "_items"));
        _sizeOffset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "_size"));
    }

    /// <summary>True if the capsule rides a belt whose type poses it.</summary>
    internal static bool Poses(int capsule) => Riders.ContainsKey(capsule);

    /// <summary>Lets the belt's type change how a riding capsule is drawn.</summary>
    internal static void Pose(MoonDroppedCanister capsule, ref Vector3 position, ref Quaternion rotation)
    {
        if (capsule == null || !Riders.TryGetValue(capsule.id, out var rider)) return;
        try { rider.Type.RiderPose!(rider.Belt, rider.Distance, capsule, ref position, ref rotation); }
        catch (Exception e)
        {
            Riders.Clear();
            Log.Error($"{rider.Type.Name}'s RiderPose failed: {e}");
        }
    }

    /// <summary>Moves a rider to a distance along its belt (written straight into the belt's queue).</summary>
    internal static bool SetRiderDistance(Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>> queue, int id, float distance)
    {
        ReadQueue(queue);   // makes sure the field offsets are known
        var list = queue.Pointer;
        var items = System.Runtime.InteropServices.Marshal.ReadIntPtr(list + _itemsOffset);
        var count = System.Runtime.InteropServices.Marshal.ReadInt32(list + _sizeOffset);
        for (var i = 0; i < count; i++)
        {
            var entry = items + 0x20 + i * 8;
            if (System.Runtime.InteropServices.Marshal.ReadInt32(entry + 4) != id) continue;
            System.Runtime.InteropServices.Marshal.WriteInt32(entry, BitConverter.SingleToInt32Bits(distance));
            return true;
        }
        return false;
    }

    // The interop garbles List<(float, int)>, so read the list's array directly: (distance, capsule id) pairs.
    internal static List<(float Distance, int Id)> ReadQueue(Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>> queue)
    {
        var list = queue.Pointer;
        EnsureOffsets(list);
        var items = System.Runtime.InteropServices.Marshal.ReadIntPtr(list + _itemsOffset);
        var count = System.Runtime.InteropServices.Marshal.ReadInt32(list + _sizeOffset);
        var result = new List<(float, int)>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = items + 0x20 + i * 8;   // array header, then 8-byte (float, int) entries
            result.Add((BitConverter.Int32BitsToSingle(System.Runtime.InteropServices.Marshal.ReadInt32(entry)),
                        System.Runtime.InteropServices.Marshal.ReadInt32(entry + 4)));
        }
        return result;
    }

    // ---- every frame ---------------------------------------------------------------------------

    internal static void Tick(MoonWorld world)
    {
        if (_world == null || _world.Pointer != world.Pointer) { _world = world; _beltCount = -1; }
        ApplySpeeds(world);
        RehideAll();
        var now = Time.unscaledTime;

        SyncStyles(BeltPatches.Building, BeltPatches.Building ? 0.1f : 0.3f);
        StyleQueued();
        if (now >= _nextReconcile)
        {
            _nextReconcile = now + 0.5f;
            Reconcile(world, now);
        }
        if (_restyleAll)
        {
            _restyleAll = false;
            foreach (var belt in Typed.Keys.ToList()) Restyle(belt);
        }
        if (_save && CrewMessages.IsHost)
        {
            _save = false;
            MoonData.Set(DataKey, Serialize());
        }
        if (CrewMessages.InCrew && CrewMessages.IsHost && (_broadcast || (now >= _nextBroadcast && Typed.Count > 0)))
        {
            _broadcast = false;
            _nextBroadcast = now + 10f;
            CrewMessages.SendToCrew(TableTopic, EncodeTable());
        }
        if (_changed)
        {
            _changed = false;
            Belts.RaiseChanged();
        }
    }

    private static void ApplySpeeds(MoonWorld world)
    {
        if (Typed.Count == 0 && Speeds.Count == 0) return;
        var dt = Time.deltaTime;
        if (dt <= 0f) return;
        var owed = world.beltOwed;
        void Boost(int belt, float speed)
        {
            // Empty belts are skipped by reading their queue directly (no call into the game).
            if (speed <= 1.001f || QueueOf(world, belt) is not { } queue || CountOf(queue) == 0) return;
            owed.TryGetValue(belt, out var current);
            owed[belt] = Math.Min(current + (speed - 1f) * dt, MaxOwed);
        }
        foreach (var (belt, type) in Typed)
            if (!Speeds.ContainsKey(belt)) Boost(belt, type.Speed);
        foreach (var (belt, speed) in Speeds) Boost(belt, speed);
    }

    private static void Reconcile(MoonWorld world, float now)
    {
        var count = world.conveyors.Count;
        if (count != _beltCount) { _beltCount = count; _changed = true; }

        foreach (var belt in Typed.Keys.ToList())
        {
            if (BeltNetwork.Has(belt)) { MissingSince.Remove(belt); continue; }
            if (!MissingSince.TryGetValue(belt, out var since))
            {
                MissingSince[belt] = now;
                // Crewmate: the game may put the belt back under another id (the host's truth); it keeps its type.
                if (!CrewMessages.IsHost && KnownEnds.TryGetValue(belt, out var ends))
                    Awaiting.Add((Typed[belt], ends.Start, ends.End, belt, now));
                continue;
            }
            if (now - since > MissingGrace) Forget(belt);
        }
        foreach (var belt in Speeds.Keys.ToList())
            if (!BeltNetwork.Has(belt)) Speeds.Remove(belt);
        foreach (var (belt, p) in Pending.ToList())
            if (now - p.At > PendingGrace) Pending.Remove(belt);
        MatchAwaiting(world, now);
    }

    // ---- saving and loading --------------------------------------------------------------------

    private static string Serialize()
    {
        var table = new Dictionary<string, List<int>>();
        foreach (var (id, belts) in _unknown) table[id] = new List<int>(belts);
        foreach (var (type, belts) in ByType)
            if (belts.Count > 0) table[type.Id] = belts.OrderBy(b => b).ToList();
        return JsonSerializer.Serialize(table);
    }

    private static void Load(string? json, bool fromHost)
    {
        Dictionary<string, List<int>> table;
        try { table = string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<Dictionary<string, List<int>>>(json!) ?? new(); }
        catch (Exception e) { Log.Error("The saved belt types couldn't be read: " + e.Message); return; }

        var wanted = new Dictionary<int, BeltType>();
        _unknown = new Dictionary<string, List<int>>();
        foreach (var (id, belts) in table)
        {
            if (Belts.TypeById(id) is { } type) foreach (var b in belts) wanted[b] = type;
            else
            {
                _unknown[id] = belts;
                if (!fromHost) Log.Warning($"{belts.Count} belts are of type '{id}', whose mod isn't installed; they work as plain belts until it's back.");
            }
        }
        // A crewmate's own recent change stands until the host's table includes it.
        var now = Time.unscaledTime;
        foreach (var (belt, p) in Pending)
        {
            if (now - p.At > PendingGrace) continue;
            if (p.Type == null) wanted.Remove(belt);
            else wanted[belt] = p.Type;
        }
        foreach (var belt in Typed.Keys.ToList())
            if (!wanted.ContainsKey(belt)) Assign(belt, null);
        foreach (var (belt, type) in wanted) Assign(belt, type);
        _changed = true;
        _restyleAll = true;
    }

    private static void OnLoaded()
    {
        Typed.Clear();
        ByType.Clear();
        Speeds.Clear();
        MissingSince.Clear();
        Pending.Clear();
        Load(MoonData.Get(DataKey), false);
        if (Typed.Count > 0) Log.Info($"Loaded {Typed.Count} typed belts.");
    }

    // ---- crew ----------------------------------------------------------------------------------

    // The ends index for FindByEnds, rebuilt when the network changes.
    private static Dictionary<Vector3Int, List<(int Belt, Vector3 Start, Vector3 End)>>? _endsIndex;
    private static int _endsIndexVersion = -1;

    /// <summary>The belt here with these ends, or 0 (belt ids differ between crewmates' machines; ends don't).</summary>
    internal static int FindByEnds(Vector3 start, Vector3 end)
    {
        var w = World;
        if (w == null) return 0;
        if (_endsIndexVersion != BeltNetwork.Version) { _endsIndex = null; _endsIndexVersion = BeltNetwork.Version; }
        return Match(w, 0, start, end, ref _endsIndex);
    }

    internal static bool Ends(int belt, out Vector3 start, out Vector3 end) => EndsOf(belt, out start, out end);

    private static bool EndsOf(int belt, out Vector3 start, out Vector3 end)
    {
        start = end = default;
        var w = World;
        if (w == null || !w.conveyors.ContainsKey(belt)) return false;
        var c = w.conveyors[belt];
        if (c == null) return false;
        start = c.start;
        end = c.end;
        return true;
    }

    // The belt here with these ends (the same id first), or 0.
    private static int Match(MoonWorld w, int id, Vector3 start, Vector3 end, ref Dictionary<Vector3Int, List<(int Belt, Vector3 Start, Vector3 End)>>? index)
    {
        static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < MatchReach * MatchReach;
        if (w.conveyors.ContainsKey(id) && w.conveyors[id] is { } same && Near(same.start, start) && Near(same.end, end)) return id;
        index ??= IndexEnds(w);
        var cell = Vector3Int.FloorToInt(start);
        for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                    if (index.TryGetValue(cell + new Vector3Int(x, y, z), out var list))
                        foreach (var b in list)
                            if (Near(b.Start, start) && Near(b.End, end)) return b.Belt;
        return 0;
    }

    // Every belt by the metre cell its start is in.
    private static Dictionary<Vector3Int, List<(int Belt, Vector3 Start, Vector3 End)>> IndexEnds(MoonWorld w)
    {
        var index = new Dictionary<Vector3Int, List<(int, Vector3, Vector3)>>();
        foreach (var belt in Keys(w.conveyors))
        {
            var c = w.conveyors[belt];
            if (c == null) continue;
            var cell = Vector3Int.FloorToInt(c.start);
            if (!index.TryGetValue(cell, out var list)) index[cell] = list = new List<(int, Vector3, Vector3)>();
            list.Add((belt, c.start, c.end));
        }
        return index;
    }

    private static void WriteVector(System.IO.BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static Vector3 ReadVector(System.IO.BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    // Set: belt id, its ends, type id ("" = plain).
    private static byte[] EncodeSet(int belt, BeltType? type)
    {
        using var stream = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(stream);
        EndsOf(belt, out var start, out var end);
        w.Write(belt);
        WriteVector(w, start);
        WriteVector(w, end);
        w.Write(type?.Id ?? "");
        w.Flush();
        return stream.ToArray();
    }

    // Table: per type, its id and its belts (id and ends).
    private static byte[] EncodeTable()
    {
        using var stream = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(stream);
        var types = ByType.Where(t => t.Value.Count > 0).ToList();
        w.Write(types.Count);
        foreach (var (type, belts) in types)
        {
            var known = belts.Where(b => EndsOf(b, out _, out _)).ToList();
            w.Write(type.Id);
            w.Write(known.Count);
            foreach (var belt in known)
            {
                EndsOf(belt, out var start, out var end);
                w.Write(belt);
                WriteVector(w, start);
                WriteVector(w, end);
            }
        }
        w.Flush();
        return stream.ToArray();
    }

    // Host: a crewmate built or changed a belt.
    private static void OnSetRequest(ulong sender, byte[] data)
    {
        if (!CrewMessages.IsHost || World is not { } world) return;
        Dictionary<Vector3Int, List<(int Belt, Vector3 Start, Vector3 End)>>? index = null;
        try
        {
            using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            var id = r.ReadInt32();
            var start = ReadVector(r);
            var end = ReadVector(r);
            var typeId = r.ReadString();
            var type = typeId.Length == 0 ? null : Belts.TypeById(typeId);
            if (typeId.Length > 0 && type == null) { Log.Warning($"A crewmate set a belt to unknown type '{typeId}'."); return; }
            var belt = Match(world, id, start, end, ref index);
            if (belt != 0) SetType(belt, type, false);
            else if (type != null) Awaiting.Add((type, start, end, id, Time.unscaledTime));   // its belt hasn't reached us yet
            _broadcast = true;
        }
        catch (Exception e) { Log.Warning("A crewmate's belt change couldn't be read: " + e.Message); }
    }

    // Crewmate: the host's table.
    private static void OnTable(ulong sender, byte[] data)
    {
        if (CrewMessages.IsHost || sender != CrewMessages.Host || World is not { } world) return;
        var wanted = new Dictionary<int, BeltType>();
        var now = Time.unscaledTime;
        Dictionary<Vector3Int, List<(int Belt, Vector3 Start, Vector3 End)>>? index = null;
        Awaiting.Clear();
        try
        {
            using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            var types = r.ReadInt32();
            for (var t = 0; t < types; t++)
            {
                var type = Belts.TypeById(r.ReadString());
                var count = r.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var id = r.ReadInt32();
                    var start = ReadVector(r);
                    var end = ReadVector(r);
                    if (type == null) continue;
                    var belt = Match(world, id, start, end, ref index);
                    if (belt != 0) wanted[belt] = type;
                    else Awaiting.Add((type, start, end, id, now));
                }
            }
        }
        catch (Exception e) { Log.Warning("The host's belt types couldn't be read: " + e.Message); return; }
        // A crewmate's own recent change stands until the host's table includes it.
        foreach (var (belt, p) in Pending)
        {
            if (now - p.At > PendingGrace) continue;
            if (p.Type == null) wanted.Remove(belt);
            else wanted[belt] = p.Type;
        }
        foreach (var belt in Typed.Keys.ToList())
            if (!wanted.ContainsKey(belt)) Assign(belt, null);
        foreach (var (belt, type) in wanted) Assign(belt, type);
    }

    // Typed belts that weren't here when heard of: type them when they arrive.
    private static void MatchAwaiting(MoonWorld world, float now)
    {
        Dictionary<Vector3Int, List<(int Belt, Vector3 Start, Vector3 End)>>? index = null;
        for (var i = Awaiting.Count - 1; i >= 0; i--)
        {
            var (type, start, end, id, since) = Awaiting[i];
            var belt = Match(world, id, start, end, ref index);
            if (belt != 0)
            {
                if (CrewMessages.IsHost) { SetType(belt, type, false); _broadcast = true; }
                else if (!Pending.ContainsKey(belt)) Assign(belt, type);
                Awaiting.RemoveAt(i);
            }
            else if (now - since > AwaitGrace) Awaiting.RemoveAt(i);
        }
    }
}
