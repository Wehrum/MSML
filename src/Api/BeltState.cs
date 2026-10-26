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
/// (and every few seconds, so a lost message heals itself).
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
    private const string SetTopic = "msml.belts.set";
    private const string TableTopic = "msml.belts.table";
    private const float MissingGrace = 10f;   // a typed belt may arrive after its type (crew); keep the type that long
    private const float PendingGrace = 5f;    // a crewmate's own change survives tables sent before the host saw it
    private const float MaxOwed = 1f;

    private static readonly Dictionary<int, BeltType> Typed = new();
    private static readonly Dictionary<BeltType, HashSet<int>> ByType = new();
    private static readonly Dictionary<int, float> Speeds = new();
    private static readonly Dictionary<int, float> MissingSince = new();
    private static readonly Dictionary<int, (BeltType? Type, float At)> Pending = new();
    private static Dictionary<string, List<int>> _unknown = new();

    private static MoonWorld? _world;
    private static bool _changed, _save, _broadcast, _restyleAll;
    private static float _nextReconcile, _nextBroadcast, _nextSync;
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
        }
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
        Riders.Clear();
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

    // The game hides a belt while previewing changes to it (a splitter cutting it, an end being moved).
    private static bool GameHidden(int belt)
    {
        var views = World?.ConveyorViews?.views;
        return views != null && views.ContainsKey(belt) && views[belt]?.hidden == true;
    }

    // Styles follow their belt's view: hidden with it, gone with it; the game's belt stays hidden.
    private static void SyncStyles()
    {
        foreach (var (belt, parts) in Styled.ToList())
        {
            var view = Belts.ViewOf(belt);
            if (parts == null || parts.WasCollected || view == null || TypeOf(belt)?.Style == null) { DropStyle(belt); continue; }
            var shown = view.activeInHierarchy && !GameHidden(belt);
            if (parts.activeSelf != shown) parts.SetActive(shown);
            if (TypeOf(belt)?.HideBelt == true)
                foreach (var r in view.GetComponents<Renderer>())
                    if (r.enabled) HideGameBelt(belt, view, true);
        }
        LetMoversThrough();
    }

    // PassThrough belts: their colliders go on a layer that collides with nothing (vehicles and players pass over),
    // but raycasts (the build tool's) still hit it. Only colliders moved here are put back (views are reused).
    internal const int PassLayer = 29;
    private static readonly Dictionary<IntPtr, (int Layer, Vector3 Position)> PassColliders = new();
    private static bool _passLayerReady;

    private static void LetMoversThrough()
    {
        foreach (var (type, belts) in ByType)
        {
            if (!type.PassThrough) continue;
            foreach (var belt in belts)
                if (Belts.ViewOf(belt) is { } view) SetPassThrough(view, true, type.ColliderTop);
        }
    }

    private static void SetPassThrough(GameObject view, bool pass, float? top = null)
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
            {
                if (!world.beltQueues.ContainsKey(belt)) continue;
                var queue = world.beltQueues[belt];
                if (queue == null || queue.Count == 0) continue;
                foreach (var (distance, id) in ReadQueue(queue)) Riders[id] = (belt, distance, type);
            }
        }
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

    // The interop garbles List<(float, int)>, so read the list's array directly: (distance, capsule id) pairs.
    internal static List<(float Distance, int Id)> ReadQueue(Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>> queue)
    {
        var list = queue.Pointer;
        if (_itemsOffset < 0)
        {
            var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(list);
            _itemsOffset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "_items"));
            _sizeOffset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "_size"));
        }
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
        var now = Time.unscaledTime;

        if (now >= _nextSync)
        {
            _nextSync = now + 0.1f;
            SyncStyles();
        }
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
            CrewMessages.SendToCrew(TableTopic, Encoding.UTF8.GetBytes(Serialize()));
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
        var queues = world.beltQueues;
        var owed = world.beltOwed;
        void Boost(int belt, float speed)
        {
            if (speed <= 1.001f || !queues.ContainsKey(belt)) return;
            var queue = queues[belt];
            if (queue == null || queue.Count == 0) return;
            var current = owed.ContainsKey(belt) ? owed[belt] : 0f;
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
            if (world.conveyors.ContainsKey(belt)) { MissingSince.Remove(belt); continue; }
            if (!MissingSince.TryGetValue(belt, out var since)) { MissingSince[belt] = now; continue; }
            if (now - since > MissingGrace) Forget(belt);
        }
        foreach (var belt in Speeds.Keys.ToList())
            if (!Belts.Exists(belt)) Speeds.Remove(belt);
        foreach (var (belt, p) in Pending.ToList())
            if (now - p.At > PendingGrace) Pending.Remove(belt);
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

    private static byte[] EncodeSet(int belt, BeltType? type)
    {
        var id = Encoding.UTF8.GetBytes(type?.Id ?? "");
        var bytes = new byte[4 + id.Length];
        BitConverter.GetBytes(belt).CopyTo(bytes, 0);
        id.CopyTo(bytes, 4);
        return bytes;
    }

    // Host: a crewmate built or changed a belt.
    private static void OnSetRequest(ulong sender, byte[] data)
    {
        if (!CrewMessages.IsHost || data.Length < 4) return;
        var belt = BitConverter.ToInt32(data, 0);
        var id = Encoding.UTF8.GetString(data, 4, data.Length - 4);
        var type = id.Length == 0 ? null : Belts.TypeById(id);
        if (id.Length > 0 && type == null) { Log.Warning($"A crewmate set belt {belt} to unknown type '{id}'."); return; }
        SetType(belt, type, false);
        _broadcast = true;
    }

    // Crewmate: the host's table.
    private static void OnTable(ulong sender, byte[] data)
    {
        if (CrewMessages.IsHost || sender != CrewMessages.Host) return;
        Load(Encoding.UTF8.GetString(data), true);
    }
}
