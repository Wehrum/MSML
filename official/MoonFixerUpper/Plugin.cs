using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace MSML.MoonFixerUpper;

/// <summary>
/// MoonFixerUpper: fixes for bugs in the game itself. Each fix is its own class with a setting to turn it off, and
/// is listed in the MODS screen and the pause menu (MOD SETTINGS).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "msml.moonfixerupper";
    public const string Name = "MoonFixerUpper";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        var harmony = new Harmony(Guid);

        CapsuleLimitDeadlock.Bind(Config);
        harmony.PatchAll(typeof(CapsuleLimitDeadlock));

        ModSettings.ShowInGame(this);
        Log.LogInfo($"{Name} {Version} loaded.");
    }
}
