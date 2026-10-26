// Sends a bought vehicle (default Hex harvester) back to garage 1 and calls it out again: where does it end up,
// and does it drive?   .\tools\cs.ps1 tools\scripts\garage-recall.cs [Vehicle_name]

public static class GarageRecall
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var index = VehicleIndex(ctx.Args.Length > 0 ? ctx.Args[0].Replace('_', ' ') : "Hex harvester");
        var owned = World.OwnedVehicle(1, index, PlayerId);
        if (owned == null) { ctx.Log("not owned"); yield break; }
        Leave();
        yield return 0.5f;
        ctx.Log($"store: {World.RecallToGarage(owned.Storage, out var stored)} '{stored}'");
        yield return 1f;
        var garage = MoonGarage.Find(World, 1);
        ctx.Log($"call back: {World.RecallGarageVehicle(1, index, garage, out var called)} '{called}'");
        yield return 1.5f;
        var v = FindAll<MoonPrototypeVehicle>().FirstOrDefault(x => x.StorageId == owned.Storage);
        if (v == null) { ctx.Log("not in the world after calling back"); yield break; }
        ctx.Log($"at {v.transform.position}, in hall {MoonGarage.InHall(v.transform.position)}, {Vector3.Distance(v.transform.position, garage.Spot().position):0} m from the hall spot, wheels {v.WheelsOnGround}/{v.WheelCount}, cleaning width {v.CleaningWidth} m");
        yield return Board(v);
        var start = v.transform.position;
        Drive(1, 0, 2);
        yield return 2f;
        ctx.Log($"drove {Vector3.Distance(start, v.transform.position):0.0} m");
    }
}
