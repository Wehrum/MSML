// Debug Menu's "Open garage menu": from the pause menu (real garage), then the browse-only path (no garage).
//   .\tools\cs.ps1 tools\scripts\garage-button-test.cs

public static class GarageButtonTest
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        Leave();
        yield return 0.5f;
        var pause = Find<MoonPauseMenu>();
        pause.Open();
        yield return 1f;
        var button = FindAll<UnityEngine.UI.Button>().FirstOrDefault(b => b.GetComponentInChildren<TMPro.TMP_Text>()?.text == "Open garage menu");
        if (button == null) { ctx.Log("button not found in the Debug panel"); yield break; }
        ctx.Log("click: " + Click(button));
        var menu = Find<MoonGarageMenu>();
        yield return WaitFor(() => menu.root != null && menu.root.activeInHierarchy, 3, "the garage menu");
        ctx.Log($"pause open {pause.IsOpen}, garage menu open {menu.root.activeInHierarchy}");
        yield return 0.5f;
        ctx.Log(Shot("garage-from-debug"));
        yield return null;
        menu.SetOpen(false);
        yield return 0.5f;

        // Browse-only: pretend there's no garage by opening with the made-up id the way GarageOpener does.
        var opener = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MoonSweepMods.DebugMenu.GarageOpener")).First(t => t != null);
        var opening = opener.GetField("_opening", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        opening.SetValue(null, true);
        var opened = menu.Open(9999);
        opening.SetValue(null, false);
        ctx.Log($"browse-only open: {opened}");
        yield return 0.5f;
        var bought = World.TryBuyVehicle(PlayerId, 9999, 1, out var message);
        ctx.Log($"buy in browse-only garage: {bought} '{message}'");
        ctx.Log(Shot("garage-browse-only"));
        yield return null;
        menu.SetOpen(false);
    }
}
