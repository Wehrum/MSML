using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using MoonCleaners;

namespace MSML.HexHarvester;

/// <summary>
/// The Hex Harvester: a new vehicle sold in the garage, built with the MSML vehicle API. It runs on the game's own
/// vehicle code using the Mega Vacuum Rover as its base (physics anchors, sounds); the mod supplies its stats, its
/// model (Design/HexHarvesterDesign.cs, built by HarvesterModel) and its garage offer.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "msml.hexharvester";
    public const string Name = "Hex Harvester";
    public const string Version = "0.3.0";

    internal static ManualLogSource Logger = null!;

    /// <summary>The registered vehicle (its catalog index is <c>Vehicle.Index</c>).</summary>
    public static ModVehicle Vehicle { get; private set; } = null!;

    public override void Load()
    {
        Logger = Log;
        var price = Config.Bind("Garage", "Price", 100000, new ConfigDescription(
            "Price in the garage, in dollars. The Mega Vacuum Rover costs 6,000.",
            new AcceptableValueRange<int>(1000, 250000), ModSettings.Step(1000)));
        var upgrades = Config.Bind("Garage", "UpgradePriceMultiplier", 3f, new ConfigDescription(
            "Upgrade prices are each upgrade's base price times this. The Mega Vacuum Rover's is 3, the Push Sweeper's 0.5.",
            new AcceptableValueRange<float>(0.5f, 20f), ModSettings.Step(0.5)));

        Vehicle = Vehicles.Register(new ModVehicle("msml.hexharvester", "Hex harvester", MoonPrototypeCatalog.MegaVacuumRover)
        {
            Speed = 4.5f,
            Turn = 40f,
            Cargo = 8,
            CleaningWidth = 25f,   // one hex
            HopperKg = 9000,       // Mega Vacuum Rover: 3,000
            Heft = 1.1f,           // Mega Vacuum Rover: 0.55
            Price = price.Value,
            UpgradePriceScale = upgrades.Value,
            Summary = "Clears a whole hex in one pass: a 25 m sweeper head and a 9,000 kg silo.",
            Icon = "garage.png",
            HideDriver = true,     // the cab is closed and the player model clips through it
            DeliverOutsideGarage = true,   // too big to drive out of the garage hall
            BuildModel = HarvesterModel.Build,
            Update = SiloFill.Update,      // the dust in the glass silo follows the hopper
        });
        price.SettingChanged += (_, _) => Vehicle.Price = price.Value;
        upgrades.SettingChanged += (_, _) => Vehicle.UpgradePriceScale = upgrades.Value;
    }
}
