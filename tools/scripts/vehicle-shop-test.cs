// End-to-end test of a modded vehicle in the garage (default: the Hex Harvester).
//   .\tools\cs.ps1 tools\scripts\vehicle-shop-test.cs buy     money, buy it in garage 1, save, screenshot the garage
//   .\tools\cs.ps1 tools\scripts\vehicle-shop-test.cs check   is it owned? (after a reload)
//   .\tools\cs.ps1 tools\scripts\vehicle-shop-test.cs drive   call it out of the garage, get in, drive, screenshot

public static class VehicleShopTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Timeout = 90;
        var mode = ctx.Args.FirstOrDefault() ?? "check";
        var name = ctx.Args.Length > 1 ? ctx.Args[1].Replace('_', ' ') : "Hex harvester";
        var index = VehicleIndex(name);
        const int garage = 1;

        var offer = MoonWorld.GarageOfferFor(index);
        ctx.Log($"#{index} {name}: offer {(offer == null ? "none" : $"${offer.price} '{offer.summary}'")}, icon {(MoonGarageMenu.Render(index) == null ? "missing" : "ok")}");
        var owned = World.OwnedVehicle(garage, index, PlayerId);
        ctx.Log($"owned: {(owned == null ? "no" : $"yes (storage {owned.Storage}, in world {owned.inWorld})")}");

        if (mode == "buy")
        {
            if (owned == null)
            {
                Money = Math.Max(Money, 100000);
                var before = Money;
                var bought = World.TryBuyVehicle(PlayerId, garage, index, out var message);
                ctx.Log($"TryBuyVehicle: {bought} '{message}', money {before:N0} -> {Money:N0}");
                yield return 1f;
                owned = World.OwnedVehicle(garage, index, PlayerId);
                ctx.Log($"owned now: {owned?.Storage}");
            }
            var save = World.SaveProgress(false);
            yield return WaitFor(() => save.IsCompleted, 20, "the save");
            ctx.Log($"saved: faulted {save.IsFaulted}");
            var menu = Find<MoonGarageMenu>();
            if (menu != null && menu.Open(garage))
            {
                yield return 1f;
                ctx.Log(Shot("garage-bought"));
                yield return null;
            }
        }
        else if (mode == "drive")
        {
            var vehicle = FindAll<MoonPrototypeVehicle>().FirstOrDefault(v => v.Index == index && v.StorageId == owned?.Storage);
            if (vehicle == null && owned != null)
            {
                var recalled = World.RecallGarageVehicle(garage, index, MoonGarage.Find(World, garage), out var message);
                ctx.Log($"recall: {recalled} '{message}'");
                yield return 2f;
                vehicle = FindAll<MoonPrototypeVehicle>().FirstOrDefault(v => v.Index == index);
            }
            if (vehicle == null) { ctx.Log("no vehicle in the world"); yield break; }
            yield return Board(vehicle);
            ctx.Log($"driving {Driving?.Spec?.name}, hopper {Driving?.DustCapacityKg} kg, width {Driving?.CleaningWidth} m");
            Drive(1, 0, 3);
            yield return 3f;
            ctx.Log(Shot("driving"));
            yield return null;
        }
    }
}
