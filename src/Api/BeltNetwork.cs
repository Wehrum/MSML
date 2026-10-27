using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Following capsules from belt to belt, through splitters and mergers. Answers are cached until the network changes
/// (belts or structures built, removed or moved, here or by the crew): asking the game is slow (a spatial search per
/// belt end) and leaves garbage on its heap, which makes it stop for collections.
/// </summary>
internal static class BeltNetwork
{
    private const float PortSearch = 8f;
    private const float CheckEvery = 0.25f;   // how often the cheap "did the network change?" check runs
    private static readonly Il2CppSystem.Collections.Generic.List<MoonWorld.ConveyorPort> Ports = new();

    private static readonly Dictionary<(int Belt, bool Down), List<int>> NextCache = new();
    private static readonly Dictionary<(int Belt, bool Down), List<int>> ThroughCache = new();
    private static readonly Dictionary<(int Belt, bool End), (int Structure, int Kind)> StructureCache = new();
    private static readonly Dictionary<int, MoonConveyorPath> PathCache = new();
    private static Dictionary<int, List<int>>? _routes;
    private static readonly Dictionary<int, List<int>> JunctionRoutes = new();
    private static (int Belts, int Structures, int NextBelt, int NextStructure) _signature;
    private static int _routeCount = -1;
    private static float _nextCheck;

    /// <summary>Bumped whenever the network changes (for callers keeping their own caches).</summary>
    internal static int Version { get; private set; }

    /// <summary>Bumped whenever the machine routes change.</summary>
    internal static int RoutesVersion { get; private set; }

    /// <summary>Forgets everything (something was built, removed or moved).</summary>
    internal static void Invalidate()
    {
        NextCache.Clear();
        ThroughCache.Clear();
        StructureCache.Clear();
        PathCache.Clear();
        JunctionRoutes.Clear();
        _routes = null;
        _nextCheck = 0f;
        Version++;
        RoutesVersion++;
    }

    /// <summary>Checks for changes at the next question (rather than up to a quarter second later).</summary>
    internal static void CheckSoon() => _nextCheck = 0f;

    private static MoonWorld? Fresh()
    {
        var w = Belts.World;
        if (w == null) return null;
        var now = Time.unscaledTime;
        if (now < _nextCheck) return w;
        _nextCheck = now + CheckEvery;
        // Machine routes come and go on their own (the game adds them as machines work): only the routes are redone then.
        var routeCount = w.machinePaths.Count;
        if (routeCount != _routeCount) { _routeCount = routeCount; _routes = null; RoutesVersion++; }
        var signature = (w.conveyors.Count, w.depots.Count, w.nextConveyorId, w.nextDepotId);
        if (signature != _signature)
        {
            _signature = signature;
            Invalidate();
            _nextCheck = now + CheckEvery;
        }
        return w;
    }

    internal static List<int> Next(int belt, bool downstream)
    {
        var w = Fresh();
        if (w == null) return new List<int>();
        if (!NextCache.TryGetValue((belt, downstream), out var cached))
            NextCache[(belt, downstream)] = cached = Find(w, belt, downstream);
        return new List<int>(cached);
    }

    private static List<int> Find(MoonWorld w, int belt, bool downstream)
    {
        var result = new List<int>();
        if (!w.conveyors.ContainsKey(belt)) return result;
        var c = w.conveyors[belt];
        var through = new List<int>();
        ThroughCache[(belt, downstream)] = through;
        var (onBelt, structure) = At(w, downstream ? c.end : c.start, downstream);
        if (onBelt != 0) { result.Add(onBelt); return result; }
        if (structure == 0 || !Belts.JunctionKinds.Contains(KindOf(w, structure))) return result;

        // Through the junction: belts on its other side, and on through junctions joined straight to it.
        var pending = new Queue<int>();
        pending.Enqueue(structure);
        through.Add(structure);
        while (pending.Count > 0)
        {
            var junction = pending.Dequeue();
            var d = w.depots[junction];
            Ports.Clear();
            w.ConveyorPorts(new Vector3(d.x, d.y, d.z), PortSearch, Ports);
            foreach (var port in Ports)
            {
                if (port.structure != junction || port.output != downstream) continue;
                var (other, next) = At(w, port.position, downstream);
                if (other != 0) { if (other != belt && !result.Contains(other)) result.Add(other); }
                else if (next != 0 && next != junction && !through.Contains(next) && w.depots.ContainsKey(next) &&
                         Belts.JunctionKinds.Contains(KindOf(w, next)))
                {
                    through.Add(next);
                    pending.Enqueue(next);
                }
            }
        }
        return result;
    }

    /// <summary>The splitters and mergers between a belt's end (or start) and the belts <see cref="Next"/> gives, nearest first.</summary>
    internal static List<int> Through(int belt, bool downstream)
    {
        Next(belt, downstream);
        return ThroughCache.TryGetValue((belt, downstream), out var through) ? new List<int>(through) : new List<int>();
    }

    internal static (int Structure, int Kind) StructureAt(int belt, bool end)
    {
        var w = Fresh();
        if (w == null) return (0, 0);
        if (StructureCache.TryGetValue((belt, end), out var cached)) return cached;
        var result = (0, 0);
        if (w.conveyors.ContainsKey(belt))
        {
            var c = w.conveyors[belt];
            var (_, structure) = At(w, end ? c.end : c.start, end);
            if (structure != 0) result = (structure, KindOf(w, structure));
        }
        return StructureCache[(belt, end)] = result;
    }

    internal static List<int> Routes(int structure)
    {
        var w = Fresh();
        if (w == null) return new List<int>();
        // A splitter's or merger's routes are only added to the game's list when a capsule first takes them: ask for
        // them by their ports (splitter: in 0, outs 1 and 2; merger: ins 0 and 1, out 2), which adds them.
        if (w.depots.ContainsKey(structure) && KindOf(w, structure) is var kind && Belts.JunctionKinds.Contains(kind))
        {
            if (JunctionRoutes.TryGetValue(structure, out var known)) return new List<int>(known);
            var list = new List<int>();
            foreach (var (input, output) in kind == 11 ? new[] { (0, 1), (0, 2) } : new[] { (0, 2), (1, 2) })
            {
                var route = MoonWorld.MachineRoute(structure, input, output);
                try { if (w.MachinePath(route) != null) list.Add(route); } catch { }
            }
            if (list.Count > 0) JunctionRoutes[structure] = list;
            return new List<int>(list);
        }
        if (_routes == null)
        {
            // One pass over every machine route, by the structure it belongs to.
            _routes = new Dictionary<int, List<int>>();
            foreach (var key in w.machinePaths.Keys)
            {
                var route = w.machinePaths[key];
                if (route.Item1 == null) continue;
                var owner = route.Item1.id;
                if (!_routes.TryGetValue(owner, out var list)) _routes[owner] = list = new List<int>();
                list.Add(key);
            }
        }
        return _routes.TryGetValue(structure, out var routes) ? new List<int>(routes) : new List<int>();
    }

    /// <summary>The belt's (or route's) path, or null. The game keeps one path object per belt, so it's cached too.</summary>
    internal static MoonConveyorPath? PathOf(int belt)
    {
        var w = Fresh();
        if (w == null) return null;
        if (PathCache.TryGetValue(belt, out var path) && path != null && !path.WasCollected) return path;
        if (!w.conveyors.ContainsKey(belt) && !w.machinePaths.ContainsKey(belt)) return null;
        try { path = w.BeltPath(belt); } catch { return null; }
        if (path != null) PathCache[belt] = path;
        return path;
    }

    // What takes capsules from a point (going downstream) or feeds it (going upstream).
    private static (int Belt, int Structure) At(MoonWorld w, Vector3 point, bool downstream)
    {
        int belt, structure;
        if (downstream) w.Takers(point, out belt, out structure);
        else w.Feeders(point, out belt, out structure);
        return (belt, structure);
    }

    private static int KindOf(MoonWorld w, int structure) =>
        w.depots.ContainsKey(structure) ? w.depots[structure].kind : 0;
}
