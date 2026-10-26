// Isolates runtime Harmony patching: "install" patches StorageKg (prefix + postfix), "call" calls it.
//   .\tools\cs.ps1 tools\scripts\patchtest.cs install
//   .\tools\cs.ps1 -Code "MoonPrototypeCatalog.StorageKg(0)"

public static class PatchTest
{
    public static object Run(ScriptContext ctx)
    {
        var mode = ctx.Args.FirstOrDefault() ?? "install";
        var method = AccessTools.Method(typeof(MoonPrototypeCatalog), nameof(MoonPrototypeCatalog.StorageKg));
        if (mode == "install")
        {
            ctx.Harmony.Patch(method, postfix: new HarmonyMethod(typeof(PatchTest), nameof(Postfix)));
            return "installed prefix";
        }
        return $"StorageKg(0) = {MoonPrototypeCatalog.StorageKg(0)}, prefix ran {Calls}x";
    }

    public static int Calls;

    private static void Postfix(ref double __result) => __result = 4242;

    private static void Prefix(int index) { Calls++; BepInEx.Logging.Logger.CreateLogSource("PatchTest").LogWarning("prefix ran for " + index); }
}
