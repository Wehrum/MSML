// Does a vehicle drive outside? Teleports next to the parked push sweeper, spawns one (default Hex harvester),
// boards it and drives forward for a few seconds, logging speed.
//   .\tools\cs.ps1 tools\scripts\drive-outside.cs [Vehicle_name]

public static class DriveOutside
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var name = ctx.Args.Length > 0 ? ctx.Args[0].Replace('_', ' ') : "Hex harvester";
        Leave();
        yield return 0.5f;
        var parked = Items<MoonOwnedVehicle>(World.ownedVehicles.Values).FirstOrDefault(o => o.vehicle == 0);
        if (parked != null) Teleport(Ground(parked.position + parked.position.normalized * 2f + Vector3.Cross(parked.position.normalized, Vector3.right).normalized * 6f));
        yield return 1f;
        var v = Spawn(name, 15f);
        yield return 1.5f;
        yield return Board(v);
        var start = v.transform.position;
        Drive(1, 0, 4);
        for (var i = 0; i < 8; i++)
        {
            yield return 0.5f;
            ctx.Log($"t={i * 0.5f + 0.5f:0.0}s speed {v.Speed:0.0} moved {Vector3.Distance(start, v.transform.position):0.0} m wheels {v.WheelsOnGround}/{v.WheelCount}");
        }
        ctx.Log(Shot("drive-outside"));
        yield return null;
    }
}
