// Where do jammed conveyor capsules stop? For every belt carrying capsules, follow the chain to its last belt and
// report what that belt feeds (next belt / machine input / station / nothing) and how full the machine is.
//   .\tools\cs.ps1 tools\scripts\capsule-jam.cs

public static class CapsuleJam
{
    public static object Run(ScriptContext ctx)
    {
        var w = World;
        var riders = Items<MoonWorld.Rider>(w.riders.Values);
        var perBelt = riders.GroupBy(r => r.belt).ToDictionary(g => g.Key, g => g.Count());
        var stoppedPerBelt = riders.Where(r => !r.moving).GroupBy(r => r.belt).ToDictionary(g => g.Key, g => g.Count());
        ctx.Log($"capsules {w.canisters.Count}/{w.settings.maxDroppedCanisters}, on belts {riders.Count} ({riders.Count(r => !r.moving)} stopped), belts {w.conveyors.Count}, machines {w.production.Count}");

        // Follow each loaded belt to its terminal belt.
        var terminals = new Dictionary<int, (int capsules, int stopped)>();
        foreach (var start in perBelt.Keys)
        {
            var id = start; var guard = 0;
            while (guard++ < 500)
            {
                if (!w.conveyors.ContainsKey(id)) break;
                var next = w.BeltStartingAt(w.conveyors[id].end);
                if (next == null || next.id == id) break;
                id = next.id;
            }
            terminals.TryGetValue(id, out var t);
            terminals[id] = (t.capsules + perBelt[start], t.stopped + (stoppedPerBelt.TryGetValue(start, out var s) ? s : 0));
        }

        var kinds = new Dictionary<string, (int belts, int capsules)>();
        var details = new List<string>();
        foreach (var (id, load) in terminals.OrderByDescending(t => t.Value.capsules))
        {
            if (!w.conveyors.ContainsKey(id)) { Add(kinds, "belt missing", load.capsules); continue; }
            var end = w.conveyors[id].end;
            string what;
            if (w.MachineInputAt(end, out var machine, out var input))
            {
                var state = w.production.ContainsKey(machine.id) ? w.production[machine.id] : null;
                var stored = state == null ? "?" : $"{state.Stored(input):0.#}/{state.Capacity(input):0.#}";
                what = $"machine kind {machine.kind} #{machine.id} input {input} stored {stored} running {state?.Running} power {state?.Power:0.##} flow '{state?.flowStatus}' powerStatus '{state?.powerStatus}'";
                Add(kinds, $"machine kind {machine.kind}", load.capsules);
            }
            else if (w.FeedStation(end, -1)) { what = "station (accepts)"; Add(kinds, "station", load.capsules); }
            else { what = "nothing connected"; Add(kinds, "dead end", load.capsules); }
            details.Add($"  belt {id}: {load.capsules} capsules upstream ({load.stopped} stopped) -> {what}");
        }
        ctx.Log("chain ends by kind: " + string.Join("; ", kinds.Select(k => $"{k.Key}: {k.Value.belts} chains, {k.Value.capsules} capsules")));
        foreach (var d in details.Take(25)) ctx.Log(d);
        return null;
    }

    private static void Add(Dictionary<string, (int belts, int capsules)> kinds, string key, int capsules)
    {
        kinds.TryGetValue(key, out var k);
        kinds[key] = (k.belts + 1, k.capsules + capsules);
    }
}
