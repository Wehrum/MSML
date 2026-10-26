// Checks the script host end to end: output, values, waiting, Harmony patches, hooks, captured warnings, and (in a
// game) spawning, boarding and driving a vehicle. Run it twice: the second run must replace the first cleanly.
//   .\tools\cs.ps1 tools\scripts\selftest.cs

public static class SelfTest
{
    public static int Frames;

    public static IEnumerator Run(ScriptContext ctx)
    {
        ctx.Log($"vehicles in catalog: {MoonPrototypeCatalog.Vehicles.Length}");

        // Per-frame hook, removed when the script reloads.
        Frames = 0;
        ctx.OnUpdate(() => Frames++);
        yield return 0.5f;
        ctx.Log($"hook ran {Frames} frames in 0.5 s");

        // A Harmony patch through ctx.Harmony is undone on reload.
        // (Patching Heft mid-game crashes the runtime; StorageKg is fine.)
        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.StorageKg)),
            postfix: new HarmonyMethod(typeof(SelfTest), nameof(StoragePostfix)));
        ctx.Log($"StorageKg(0) patched: {MoonPrototypeCatalog.StorageKg(0)}");

        // Warnings logged by anything while the script runs are captured.
        BepInEx.Logging.Logger.CreateLogSource("SelfTest").LogWarning("captured warning");

        if (!InGame) { ctx.Log("not in a game: skipping the vehicle test"); yield break; }

        ctx.Log($"player at {Position}, money {Money:N0}");
        var vehicle = Spawn("Push sweeper");
        yield return 1f;
        yield return Board(vehicle);
        ctx.Log($"driving: {Driving?.Spec?.name}");
        var start = vehicle.transform.position;
        Drive(1, 0, 2);
        yield return 2f;
        ctx.Log($"moved {Vector3.Distance(start, vehicle.transform.position):0.0} m");
        ctx.Log(Shot("selftest"));
        Leave();
    }

    private static void StoragePostfix(ref double __result) => __result = 4242;
}
