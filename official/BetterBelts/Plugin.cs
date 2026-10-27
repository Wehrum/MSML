using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace MSML.BetterBelts;

/// <summary>
/// Better Belts: pneumatic tubes (glass belts that carry capsules up to 3x as fast) and tube boosters that pressurise
/// them. Tubes and boosters are belt types (<see cref="Belts"/>), so they're belts to the game: they connect to belts,
/// splitters, mergers and machines, and become plain belts if the mod is removed. See <see cref="Pressure"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "msml.betterbelts";
    public const string Name = "Better Belts";
    public const string Version = "0.2.0";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        Settings.Bind(Config);
        Tubes.Register();
        Pressure.Start();
        ModSettings.ShowInGame(this);
        Log.LogInfo($"{Name} {Version} loaded.");
    }
}
