// Watches the factory for a while: capsules, belts moving, processors stuck on "Could not pack a capsule".
//   .\tools\cs.ps1 tools\scripts\capsule-watch.cs [seconds]

public static class CapsuleWatch
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var seconds = ctx.Args.Length > 0 ? float.Parse(ctx.Args[0]) : 60f;
        ctx.Timeout = seconds + 30;
        for (var t = 0f; t <= seconds; t += 15f)
        {
            var w = World;
            var riders = Items<MoonWorld.Rider>(w.riders.Values);
            var processors = Items<MoonProductionState>(w.production.Values).Where(p => w.BuildDefinition(p.Kind)?.name is "stone_press" or "pressure_chamber").ToList();
            ctx.Log($"{t:0} s: capsules {w.canisters.Count}/{w.settings.maxDroppedCanisters}, on belts {riders.Count} ({riders.Count(r => r.moving)} moving), " +
                    $"processors {processors.Count}: running {processors.Count(p => p.Running)}, showing pack error {processors.Count(p => p.flowStatus == "Could not pack a capsule")}, cycles {processors.Sum(p => p.Cycles)}");
            if (t < seconds) yield return 15f;
        }
    }
}
