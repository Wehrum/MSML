using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace MSML.MoonSweeperPlus;

/// <summary>
/// MoonSweeper+: small fixes and quality-of-life changes. Each fix is its own class that binds its settings and
/// patches the game; all settings can be changed from the MODS screen or the pause menu (MOD SETTINGS).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "msml.moonsweeperplus";
    public const string Name = "MoonSweeper+";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        var harmony = new Harmony(Guid);

        VehicleCamera.Bind(Config);
        harmony.PatchAll(typeof(VehicleCamera));

        ModSettings.ShowInGame(this);
        Log.LogInfo($"{Name} {Version} loaded.");
    }
}
