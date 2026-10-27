using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Which splitters and mergers have which <see cref="JunctionType"/>, keeping that in the save and in step with the
/// crew, and drawing them. Saved in <see cref="MoonData"/> ("msml.junctions": type id -> structure ids). In a crew the
/// host's table is the truth (as for belt types); messages name structures by where they stand, since each machine
/// counts its own ids.
/// </summary>
internal static class JunctionState
{
    private const string DataKey = "msml.junctions";
    private const string SetTopic = "msml.junctions.set";
    private const string TableTopic = "msml.junctions.table";
    private const float MissingGrace = 10f;
    private const float PendingGrace = 5f;
    private const float AwaitGrace = 15f;
    private const float MatchReach = 0.2f;
    private const string PreviewChild = "MSML junction preview";

    private static readonly Dictionary<int, JunctionType> Typed = new();
    private static readonly Dictionary<JunctionType, HashSet<int>> ByType = new();
    private static readonly Dictionary<int, float> MissingSince = new();
    private static readonly Dictionary<int, (JunctionType? Type, float At)> Pending = new();
    private static readonly List<(JunctionType Type, Vector3 At, float Since)> Awaiting = new();
    private static Dictionary<string, List<int>> _unknown = new();

    // Drawn ones: parts, the view they follow, and what the look was drawn for (its routes).
    private static readonly Dictionary<int, (GameObject Parts, GameObject View, string Routes)> Styled = new();
    private static readonly HashSet<IntPtr> HiddenViews = new();
    private static readonly Dictionary<int, GameObject> Views = new();
    private static float _nextViewSearch;

    private static bool _save, _broadcast, _handlersAdded;
    private static float _nextReconcile, _nextBroadcast;

    internal static IEnumerable<(int Structure, JunctionType Type)> All => Typed.Select(t => (t.Key, t.Value));

    internal static JunctionType? TypeOf(int structure) => Typed.TryGetValue(structure, out var t) ? t : null;

    internal static IReadOnlyCollection<int> OfType(JunctionType type) =>
        ByType.TryGetValue(type, out var set) ? set.ToList() : (IReadOnlyCollection<int>)Array.Empty<int>();

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

    internal static void SetType(int structure, JunctionType? type, bool request)
    {
        if (TypeOf(structure) == type) return;
        Assign(structure, type);
        if (CrewMessages.IsHost) { _save = true; _broadcast = true; }
        else if (request)
        {
            Pending[structure] = (type, Time.unscaledTime);
            CrewMessages.SendToHost(SetTopic, EncodeSet(structure, type));
        }
    }

    private static void Assign(int structure, JunctionType? type)
    {
        var old = TypeOf(structure);
        if (old == type) return;
        if (old != null) { Typed.Remove(structure); ByType[old].Remove(structure); }
        if (type != null)
        {
            Typed[structure] = type;
            if (!ByType.TryGetValue(type, out var set)) ByType[type] = set = new HashSet<int>();
            set.Add(structure);
        }
        Restyle(structure);
        Junctions.RaiseTypeChanged(structure, type);
    }

    private static void Forget(int structure)
    {
        MissingSince.Remove(structure);
        DropStyle(structure);
        Views.Remove(structure);
        if (TypeOf(structure) == null) return;
        Assign(structure, null);
        if (CrewMessages.IsHost) { _save = true; _broadcast = true; }
    }

    internal static void Clear()
    {
        Typed.Clear();
        ByType.Clear();
        MissingSince.Clear();
        Pending.Clear();
        Awaiting.Clear();
        foreach (var s in Styled.Keys.ToList()) DropStyle(s);
        HiddenViews.Clear();
        Views.Clear();
        Places.Clear();
        _unknown = new Dictionary<string, List<int>>();
    }

    // ---- views and looks -----------------------------------------------------------------------

    private static bool IsJunction(MoonWorld w, int structure, out int kind)
    {
        kind = w.depots.ContainsKey(structure) ? w.depots[structure].kind : 0;
        return kind == JunctionType.Splitter || kind == JunctionType.Merger;
    }

    internal static GameObject? ViewOf(int structure)
    {
        if (Views.TryGetValue(structure, out var view) && view != null && !view.WasCollected) return view;
        Views.Remove(structure);
        var w = BeltState.World;
        if (w == null || !IsJunction(w, structure, out var kind)) return null;
        view = GameObject.Find((kind == JunctionType.Splitter ? "Splitter " : "Merger ") + structure);
        if (view == null) return null;
        Views[structure] = view;
        return view;
    }

    private static string RoutesKey(int structure) => string.Join(",", Belts.RoutesThrough(structure));

    internal static void Restyle(int structure)
    {
        try
        {
            DropStyle(structure);
            var view = ViewOf(structure);
            if (view == null) return;
            var type = TypeOf(structure);
            HideModel(view, type?.HideModel == true);
            BeltState.SetPassThrough(view, type?.PassThrough == true, type?.ColliderTop);
            if (type is not { Style: { } style }) return;
            var parts = new GameObject($"MSML junction {structure} ({type.Name})");
            Styled[structure] = (parts, view, RoutesKey(structure));
            try { style(structure, parts); }
            catch (Exception e) { Log.Error($"Styling a {type.Name} failed: {e}"); }
            parts.SetActive(view.activeInHierarchy);
        }
        catch (Exception e) { Log.Error($"Restyling structure {structure} failed: {e}"); }
    }

    private static void DropStyle(int structure)
    {
        if (!Styled.TryGetValue(structure, out var s)) return;
        Styled.Remove(structure);
        if (s.Parts != null && !s.Parts.WasCollected) UnityEngine.Object.Destroy(s.Parts);
    }

    private static void HideModel(GameObject view, bool hide)
    {
        if (!hide && !HiddenViews.Remove(view.Pointer)) return;
        if (hide) HiddenViews.Add(view.Pointer);
        foreach (var r in view.GetComponentsInChildren<Renderer>(true)) r.enabled = !hide;
    }

    /// <summary>The parts drawn for the structure whose view this is, when its model is hidden; else null.</summary>
    internal static GameObject? PartsFor(GameObject view)
    {
        foreach (var (structure, s) in Styled)
            if (s.View != null && !s.View.WasCollected && s.View.Pointer == view.Pointer && s.Parts != null && !s.Parts.WasCollected &&
                TypeOf(structure)?.HideModel == true)
                return s.Parts;
        return null;
    }

    /// <summary>The game is taking a view away (removed): its parts go with it, through the dismantle effect.</summary>
    internal static void HandOver(GameObject view)
    {
        foreach (var (structure, s) in Styled.ToList())
        {
            if (s.View == null || s.View.WasCollected || s.View.Pointer != view.Pointer) continue;
            Styled.Remove(structure);
            if (s.Parts == null || s.Parts.WasCollected) continue;
            s.Parts.SetActive(true);
            s.Parts.transform.SetParent(view.transform, true);
        }
    }

    // Looks follow their view (shown and hidden with it, gone with it), the model stays hidden, and a junction whose
    // routes changed (belts joined or left) is drawn again.
    private static void SyncStyles(MoonWorld w, float now)
    {
        var deep = now >= _nextDeep;          // the model's renderers: checked every couple of seconds
        if (deep) _nextDeep = now + 2f;
        var network = BeltNetwork.RoutesVersion;   // routes only change with this
        foreach (var (structure, s) in Styled.ToList())
        {
            if (s.Parts == null || s.Parts.WasCollected || s.View == null || s.View.WasCollected || TypeOf(structure) == null)
            {
                DropStyle(structure);
                continue;
            }
            var shown = s.View.activeInHierarchy;
            if (s.Parts.activeSelf != shown) s.Parts.SetActive(shown);
            if (deep && TypeOf(structure)!.HideModel)
                foreach (var r in s.View.GetComponentsInChildren<Renderer>())
                    if (r.enabled && !r.transform.IsChildOf(s.Parts.transform)) { r.enabled = false; }
            if (network != _routesVersion && RoutesKey(structure) != s.Routes) Restyle(structure);
        }
        _routesVersion = network;
        // Typed ones near the camera without a look yet (their view came later). Finding a view searches the whole
        // scene, so only for ones close enough to have one.
        if (now < _nextViewSearch) return;
        _nextViewSearch = now + 2f;
        var camera = Camera.main;
        if (camera == null) return;
        var eye = camera.transform.position;
        foreach (var structure in Typed.Keys.ToList())
        {
            if (Styled.ContainsKey(structure) || At(w, structure) is not { } at || (at - eye).sqrMagnitude > ViewSearchReach * ViewSearchReach) continue;
            if (ViewOf(structure) != null) Restyle(structure);
        }
    }

    private const float ViewSearchReach = 200f;
    private static float _nextDeep;
    private static int _routesVersion = -1;

    // The placement hologram of a junction type: the game's ghost, hidden, with the type's preview in it.
    private static void SyncGhost()
    {
        var builder = JunctionPatches.Builder;
        if (builder == null || builder.WasCollected) return;
        var ghost = builder.ghost;
        var type = JunctionPatches.Active;
        var wanted = ghost != null && type?.Preview != null && builder.ghostKind == type.BaseKind && !builder.ConveyorMode;
        var existing = _ghostPreview != null && !_ghostPreview.WasCollected && ghost != null && _ghostPreview.parent != null &&
                       _ghostPreview.parent.Pointer == ghost.transform.Pointer ? _ghostPreview : null;
        if (!wanted)
        {
            if (existing == null) { _ghostPreview = null; return; }
            UnityEngine.Object.Destroy(existing.gameObject);
            _ghostPreview = null;
            foreach (var r in ghost!.GetComponentsInChildren<Renderer>(true)) if (!r.transform.IsChildOf(existing)) r.enabled = true;
            return;
        }
        if (existing == null || _ghostPreviewType != type)
        {
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
            var parts = new GameObject(PreviewChild + " " + type!.Id);
            parts.transform.SetParent(ghost!.transform, false);
            parts.layer = ghost.layer;
            var material = builder.ghostMaterial != null ? builder.ghostMaterial : BeltLooks.Glow(Color.cyan);
            try { type!.Preview!(parts, material); }
            catch (Exception e) { Log.Error($"{type!.Name}'s Preview failed: {e}"); }
            foreach (var t in parts.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = ghost.layer;
            existing = _ghostPreview = parts.transform;
            _ghostPreviewType = type;
            _nextGhostCheck = 0f;
        }
        // The game's own model stays hidden (checked a few times a second, not every frame).
        var now = Time.unscaledTime;
        if (now < _nextGhostCheck) return;
        _nextGhostCheck = now + 0.25f;
        foreach (var r in ghost!.GetComponentsInChildren<Renderer>())
            if (r.enabled && !r.transform.IsChildOf(existing)) r.enabled = false;
    }

    private static Transform? _ghostPreview;
    private static JunctionType? _ghostPreviewType;
    private static float _nextGhostCheck;

    // ---- every frame ---------------------------------------------------------------------------

    private static float _nextSync;

    internal static void Tick(MoonWorld world)
    {
        if (Registered == 0) return;
        var now = Time.unscaledTime;
        SyncGhost();
        if (now >= _nextSync)
        {
            _nextSync = now + 0.1f;
            SyncStyles(world, now);
        }
        if (now >= _nextReconcile)
        {
            _nextReconcile = now + 0.5f;
            Reconcile(world, now);
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
            CrewMessages.SendToCrew(TableTopic, EncodeTable(world));
        }
    }

    private static int Registered => Junctions.Types.Count;

    private static void Reconcile(MoonWorld world, float now)
    {
        foreach (var structure in Typed.Keys.ToList())
        {
            if (IsJunction(world, structure, out _)) { MissingSince.Remove(structure); At(world, structure); continue; }
            if (!MissingSince.TryGetValue(structure, out var since))
            {
                MissingSince[structure] = now;
                // Crewmate: the game may put it back under another id (the host's truth); it keeps its type.
                if (!CrewMessages.IsHost && At(world, structure) is { } at) Awaiting.Add((Typed[structure], at, now));
                continue;
            }
            if (now - since > MissingGrace) Forget(structure);
        }
        foreach (var (structure, p) in Pending.ToList())
            if (now - p.At > PendingGrace) Pending.Remove(structure);
        for (var i = Awaiting.Count - 1; i >= 0; i--)
        {
            var (type, at, since) = Awaiting[i];
            var structure = Match(world, 0, type.BaseKind, at);
            if (structure != 0)
            {
                if (CrewMessages.IsHost) SetType(structure, type, false);
                else if (!Pending.ContainsKey(structure)) Assign(structure, type);
                Awaiting.RemoveAt(i);
            }
            else if (now - since > AwaitGrace) Awaiting.RemoveAt(i);
        }
    }

    // Last known place of each typed structure (kept for when it goes missing).
    private static readonly Dictionary<int, Vector3> Places = new();

    private static Vector3? At(MoonWorld w, int structure)
    {
        if (w.depots.ContainsKey(structure))
        {
            var d = w.depots[structure];
            return Places[structure] = new Vector3(d.x, d.y, d.z);
        }
        return Places.TryGetValue(structure, out var p) ? p : null;
    }

    // The splitter or merger of this kind standing here (the same id first), or 0.
    private static int Match(MoonWorld w, int id, int kind, Vector3 at)
    {
        bool Same(int s) => w.depots.ContainsKey(s) && w.depots[s] is { } d && d.kind == kind &&
                            (new Vector3(d.x, d.y, d.z) - at).sqrMagnitude < MatchReach * MatchReach;
        if (id != 0 && Same(id)) return id;
        foreach (var s in w.depots.Keys)
            if (Same(s)) return s;
        return 0;
    }

    // ---- saving and loading --------------------------------------------------------------------

    private static string Serialize()
    {
        var table = new Dictionary<string, List<int>>();
        foreach (var (id, structures) in _unknown) table[id] = new List<int>(structures);
        foreach (var (type, structures) in ByType)
            if (structures.Count > 0) table[type.Id] = structures.OrderBy(s => s).ToList();
        return JsonSerializer.Serialize(table);
    }

    private static void OnLoaded()
    {
        Clear();
        Dictionary<string, List<int>> table;
        try
        {
            var json = MoonData.Get(DataKey);
            table = string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<Dictionary<string, List<int>>>(json!) ?? new();
        }
        catch (Exception e) { Log.Error("The saved junction types couldn't be read: " + e.Message); return; }
        foreach (var (id, structures) in table)
        {
            if (Junctions.TypeById(id) is { } type) foreach (var s in structures) Assign(s, type);
            else
            {
                _unknown[id] = structures;
                Log.Warning($"{structures.Count} splitters/mergers are of type '{id}', whose mod isn't installed; they work as the game's until it's back.");
            }
        }
        if (Typed.Count > 0) Log.Info($"Loaded {Typed.Count} typed splitters and mergers.");
    }

    // ---- crew ----------------------------------------------------------------------------------

    private static void WriteVector(System.IO.BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static Vector3 ReadVector(System.IO.BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static byte[] EncodeSet(int structure, JunctionType? type)
    {
        using var stream = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(stream);
        var world = BeltState.World;
        var kind = 0;
        var at = world != null && IsJunction(world, structure, out kind) ? At(world, structure) ?? default : default;
        w.Write(structure);
        w.Write(kind);
        WriteVector(w, at);
        w.Write(type?.Id ?? "");
        w.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeTable(MoonWorld world)
    {
        using var stream = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(stream);
        var entries = Typed.Where(t => world.depots.ContainsKey(t.Key)).ToList();
        w.Write(entries.Count);
        foreach (var (structure, type) in entries)
        {
            w.Write(structure);
            WriteVector(w, At(world, structure) ?? default);
            w.Write(type.Id);
        }
        w.Flush();
        return stream.ToArray();
    }

    // Host: a crewmate built or changed one.
    private static void OnSetRequest(ulong sender, byte[] data)
    {
        if (!CrewMessages.IsHost || BeltState.World is not { } world) return;
        try
        {
            using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            var id = r.ReadInt32();
            var kind = r.ReadInt32();
            var at = ReadVector(r);
            var typeId = r.ReadString();
            var type = typeId.Length == 0 ? null : Junctions.TypeById(typeId);
            if (typeId.Length > 0 && type == null) { Log.Warning($"A crewmate set a splitter/merger to unknown type '{typeId}'."); return; }
            var structure = Match(world, id, type?.BaseKind ?? kind, at);
            if (structure != 0) SetType(structure, type, false);
            else if (type != null) Awaiting.Add((type, at, Time.unscaledTime));   // not here yet
            _broadcast = true;
        }
        catch (Exception e) { Log.Warning("A crewmate's splitter/merger change couldn't be read: " + e.Message); }
    }

    // Crewmate: the host's table.
    private static void OnTable(ulong sender, byte[] data)
    {
        if (CrewMessages.IsHost || sender != CrewMessages.Host || BeltState.World is not { } world) return;
        var wanted = new Dictionary<int, JunctionType>();
        var now = Time.unscaledTime;
        Awaiting.Clear();
        try
        {
            using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadInt32();
                var at = ReadVector(r);
                var type = Junctions.TypeById(r.ReadString());
                if (type == null) continue;
                var structure = Match(world, id, type.BaseKind, at);
                if (structure != 0) wanted[structure] = type;
                else Awaiting.Add((type, at, now));
            }
        }
        catch (Exception e) { Log.Warning("The host's splitter/merger types couldn't be read: " + e.Message); return; }
        foreach (var (structure, p) in Pending)
        {
            if (now - p.At > PendingGrace) continue;
            if (p.Type == null) wanted.Remove(structure);
            else wanted[structure] = p.Type;
        }
        foreach (var structure in Typed.Keys.ToList())
            if (!wanted.ContainsKey(structure)) Assign(structure, null);
        foreach (var (structure, type) in wanted) Assign(structure, type);
    }
}
