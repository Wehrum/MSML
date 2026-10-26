using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>Following capsules from belt to belt, through splitters and mergers.</summary>
internal static class BeltNetwork
{
    private const float PortSearch = 8f;
    private static readonly Il2CppSystem.Collections.Generic.List<MoonWorld.ConveyorPort> Ports = new();

    internal static List<int> Next(int belt, bool downstream)
    {
        var result = new List<int>();
        var w = Belts.World;
        if (w == null || !w.conveyors.ContainsKey(belt)) return result;
        var c = w.conveyors[belt];
        var (onBelt, structure) = At(w, downstream ? c.end : c.start, downstream);
        if (onBelt != 0) { result.Add(onBelt); return result; }
        if (structure == 0 || !Belts.JunctionKinds.Contains(KindOf(w, structure))) return result;

        // Through the junction: belts on its other side.
        var d = w.depots[structure];
        Ports.Clear();
        w.ConveyorPorts(new Vector3(d.x, d.y, d.z), PortSearch, Ports);
        foreach (var port in Ports)
        {
            if (port.structure != structure || port.output != downstream) continue;
            var (other, _) = At(w, port.position, downstream);
            if (other != 0 && other != belt && !result.Contains(other)) result.Add(other);
        }
        return result;
    }

    internal static (int Structure, int Kind) StructureAt(int belt, bool end)
    {
        var w = Belts.World;
        if (w == null || !w.conveyors.ContainsKey(belt)) return (0, 0);
        var c = w.conveyors[belt];
        var (_, structure) = At(w, end ? c.end : c.start, end);
        return structure == 0 ? (0, 0) : (structure, KindOf(w, structure));
    }

    internal static List<int> Routes(int structure)
    {
        var result = new List<int>();
        var w = Belts.World;
        if (w == null) return result;
        foreach (var key in w.machinePaths.Keys)
        {
            var route = w.machinePaths[key];
            if (route.Item1 != null && route.Item1.id == structure) result.Add(key);
        }
        return result;
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
