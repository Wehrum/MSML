// Walks through the garage screens for a modded vehicle and screenshots each (default: the Hex Harvester).
//   .\tools\cs.ps1 tools\scripts\garage-screens.cs

public static class GarageScreens
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var index = VehicleIndex(ctx.Args.Length > 0 ? ctx.Args[0].Replace('_', ' ') : "Hex harvester");
        var menu = Find<MoonGarageMenu>();
        menu.SetOpen(false);
        yield return 0.3f;
        if (!menu.Open(1)) { ctx.Log("garage didn't open"); yield break; }
        yield return 0.5f;

        var card = Items<MoonGarageMenu.Card>(menu.cards).First(c => c.vehicle == index);
        menu.Pick(card);
        yield return 0.5f;
        ctx.Log("vehicles: " + Shot("garage-1-vehicles"));
        yield return null;

        menu.ShowPage(MoonGarageMenu.Page.Upgrades, false);
        yield return 0.3f;
        menu.PickModel(index);
        yield return 0.5f;
        ctx.Log("upgrades: " + Shot("garage-2-upgrades"));
        yield return null;

        menu.ShowPage(MoonGarageMenu.Page.Fleet, false);
        yield return 0.5f;
        ctx.Log("fleet: " + Shot("garage-3-fleet"));
        yield return null;
        menu.ShowPage(MoonGarageMenu.Page.Vehicles, false);
    }
}
