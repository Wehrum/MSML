// What the garage sells: offers (vehicle, price, summary), the vehicle mask, and owned vehicles.
//   .\tools\cs.ps1 tools\scripts\garage-probe.cs

public static class GarageProbe
{
    public static void Run(ScriptContext ctx)
    {
        var vehicles = MoonPrototypeCatalog.Vehicles;
        ctx.Log($"GarageKind {MoonWorld.GarageKind}, GarageVehicleMask 0x{MoonWorld.GarageVehicleMask:X} ({Convert.ToString(MoonWorld.GarageVehicleMask, 2)}), note '{MoonWorld.GarageNote}'");
        foreach (var o in MoonWorld.GarageOffers)
            ctx.Log($"offer: #{o.vehicle} {vehicles[o.vehicle].name}  ${o.price}  '{o.summary}'");
        for (var i = 0; i < vehicles.Length; i++)
        {
            var v = vehicles[i];
            ctx.Log($"#{i} {v.name}: work {v.work}, speed {v.speed}, turn {v.turn}, cargo {v.capacity}, width {v.width}, prefab {MoonPrototypeCatalog.AuthoredPrefab(i)}, storage {MoonPrototypeCatalog.StorageKg(i)} kg, inMask {(MoonWorld.GarageVehicleMask >> i & 1) == 1}");
        }
        if (!InGame) return;
        foreach (var kv in World.ownedVehicles)
        {
            var o = kv.Value;
            ctx.Log($"owned: garage {o.garage} vehicle #{o.vehicle} {vehicles[o.vehicle].name} owner {o.ownerName} inWorld {o.inWorld} storage {o.Storage}");
        }
        ctx.Log($"garages: {string.Join(", ", MoonGarage.all.ToArray().Select(g => g.name))}");
    }
}
