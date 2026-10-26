using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// A new vehicle added by a mod. It runs on the game's own vehicle code (driving, physics, cleaning, hopper,
/// unloading, lights, audio) using a stock <see cref="BaseVehicle"/> for its prefab, physics anchors and sounds.
/// Change its stats and model, and set a <see cref="Price"/> to sell it in the garage. Register it with
/// <see cref="Vehicles.Register"/> from your plugin's <c>Load()</c>.
/// <para>
/// Bought modded vehicles are kept out of the game's own owned-vehicle list in saves (they're stored by
/// <see cref="Id"/> in a record the unmodded game ignores), so removing the mod never breaks a save: the vehicle just
/// disappears until the mod is back.
/// </para>
/// </summary>
public sealed class ModVehicle
{
    /// <param name="id">Stable id, saved with the player's vehicles: "yourmod.vehicle". Never change it.</param>
    /// <param name="name">Name shown in the game, e.g. "Hex harvester".</param>
    /// <param name="baseVehicle">Stock vehicle to build on (an index into MoonPrototypeCatalog.Vehicles, e.g.
    /// <c>MoonPrototypeCatalog.MegaVacuumRover</c>). Its stats are the defaults below.</param>
    public ModVehicle(string id, string name, int baseVehicle)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A vehicle needs an id.", nameof(id));
        var vehicles = MoonPrototypeCatalog.Vehicles;
        if (baseVehicle < 0 || baseVehicle >= vehicles.Length) throw new ArgumentOutOfRangeException(nameof(baseVehicle));
        Id = id;
        Name = name;
        BaseVehicle = baseVehicle;
        var basis = vehicles[baseVehicle];
        Work = basis.work;
        Speed = basis.speed;
        Turn = basis.turn;
        Cargo = basis.capacity;
        CleaningWidth = basis.width;
        HopperKg = MoonPrototypeCatalog.StorageKg(baseVehicle);
    }

    /// <summary>Stable id saved with the player's vehicles.</summary>
    public string Id { get; }

    /// <summary>Name shown in the game.</summary>
    public string Name { get; }

    /// <summary>Stock vehicle whose prefab, physics anchors and sounds this one uses.</summary>
    public int BaseVehicle { get; }

    /// <summary>Index in MoonPrototypeCatalog.Vehicles for this session (-1 before registering). Don't save it.</summary>
    public int Index { get; internal set; } = -1;

    // ---- stats (set before registering) ------------------------------------------------------------

    public MoonPrototypeCatalog.Work Work { get; set; }

    /// <summary>Top speed (the catalog's speed value).</summary>
    public float Speed { get; set; }

    public float Turn { get; set; }

    /// <summary>Capsule cargo slots.</summary>
    public int Cargo { get; set; }

    /// <summary>Width of the cleaned strip, in metres. A hex is 25 m across.</summary>
    public float CleaningWidth { get; set; }

    /// <summary>Hopper capacity in kilograms.</summary>
    public double HopperKg { get; set; }

    /// <summary>How heavy it feels to drive (the base vehicle's if null).</summary>
    public float? Heft { get; set; }

    // ---- garage --------------------------------------------------------------------------------------

    /// <summary>Price in the garage, in dollars. Null: not sold. Can be changed while the game runs.</summary>
    public double? Price { get; set; }

    /// <summary>One-line description on the garage card.</summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// Multiplier for the garage's upgrade prices (each upgrade's base prices × this). Null: the base vehicle's
    /// (Push sweeper 0.5, Ride-on 1, Crew transport 2, Lunar sweeper 2.5, Mega vacuum rover 3). Can change at runtime.
    /// </summary>
    public double? UpgradePriceScale { get; set; }

    /// <summary>
    /// Exact upgrade prices in dollars, per upgrade and level (index 0 = level I), replacing the scaled price:
    /// <c>UpgradePrices[MoonVehicleUpgrades.Kind.Speed] = new[] { 1000, 2500, 6000 }</c>. Levels not listed use the scale.
    /// </summary>
    public Dictionary<MoonVehicleUpgrades.Kind, int[]> UpgradePrices { get; } = new();

    /// <summary>
    /// Picture for the garage card: a PNG (512x512 with a transparent background, like the game's). A relative path is
    /// resolved from the registering mod's folder. Without one the card has no picture.
    /// </summary>
    public string? Icon { get; set; }

    // ---- behaviour -----------------------------------------------------------------------------------

    /// <summary>
    /// Called after the base vehicle's model is built for this vehicle (in the world, in the garage, for previews):
    /// hide or replace its renderers, move parts, set view.rollers, ... The GameObject has a MoonAuthoredVehicleView.
    /// </summary>
    public Action<GameObject, MoonPrototypeModels>? BuildModel { get; set; }

    /// <summary>Called when a vehicle of this type is set up in the world.</summary>
    public Action<MoonPrototypeVehicle>? Ready { get; set; }

    /// <summary>
    /// Called every frame for each vehicle of this type in the world (after the game's own update): animate parts,
    /// show the hopper level, ... Keep it cheap.
    /// </summary>
    public Action<MoonPrototypeVehicle>? Update { get; set; }

    /// <summary>Hide the local player's body while they drive it (for closed cabs the player model clips through).</summary>
    public bool HideDriver { get; set; }

    /// <summary>
    /// For vehicles too big to drive out of the garage hall: when the garage brings it out (bought or called back),
    /// park it on clear ground in front of the garage instead.
    /// </summary>
    public bool DeliverOutsideGarage { get; set; }

    internal string? IconPath;
    internal Sprite? IconSprite;

    public override string ToString() => $"{Name} ({Id}, #{Index})";
}

/// <summary>Registers modded vehicles. See <see cref="ModVehicle"/>.</summary>
public static class Vehicles
{
    private static readonly List<ModVehicle> Registered = new();
    private static readonly Dictionary<int, ModVehicle> ByCatalogIndex = new();

    /// <summary>Every registered modded vehicle.</summary>
    public static IReadOnlyList<ModVehicle> All => Registered;

    /// <summary>Adds the vehicle to the game's catalog (and the garage, if it has a price). Call from <c>Load()</c>.</summary>
    public static ModVehicle Register(ModVehicle vehicle)
    {
        if (vehicle == null) throw new ArgumentNullException(nameof(vehicle));
        if (Registered.Any(v => v.Id.Equals(vehicle.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A vehicle with id '{vehicle.Id}' is already registered.");

        if (vehicle.Icon != null)
        {
            var folder = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location) ?? "";
            vehicle.IconPath = Path.IsPathRooted(vehicle.Icon) ? vehicle.Icon : Path.Combine(folder, vehicle.Icon);
        }

        var vehicles = MoonPrototypeCatalog.Vehicles;
        var basis = vehicles[vehicle.BaseVehicle];
        var item = MoonPrototypeCatalog.V(vehicle.Name, vehicle.Work, vehicle.Speed, vehicle.Turn, vehicle.Cargo, vehicle.CleaningWidth);
        item.hint = basis.hint;
        item.depth = basis.depth;
        item.reach = basis.reach;

        var extended = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MoonPrototypeCatalog.Item>(vehicles.Length + 1);
        for (var i = 0; i < vehicles.Length; i++) extended[i] = vehicles[i];
        extended[vehicles.Length] = item;
        MoonPrototypeCatalog.Vehicles = extended;

        vehicle.Index = vehicles.Length;
        Registered.Add(vehicle);
        ByCatalogIndex[vehicle.Index] = vehicle;
        VehiclePatches.Apply();
        Log.Info($"Registered vehicle {vehicle} (base #{vehicle.BaseVehicle}, width {vehicle.CleaningWidth} m, hopper {vehicle.HopperKg} kg" +
                 (vehicle.Price.HasValue ? $", ${vehicle.Price:N0} in the garage)" : ")"));
        return vehicle;
    }

    /// <summary>The modded vehicle with this catalog index, or null for stock vehicles.</summary>
    public static ModVehicle? ByIndex(int index) => ByCatalogIndex.TryGetValue(index, out var v) ? v : null;

    /// <summary>The registered vehicle with this id, or null.</summary>
    public static ModVehicle? ById(string id) =>
        Registered.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

internal static class Log
{
    private static readonly BepInEx.Logging.ManualLogSource Source = BepInEx.Logging.Logger.CreateLogSource("MSML API");
    public static void Info(string message) => Source.LogInfo(message);
    public static void Warning(string message) => Source.LogWarning(message);
    public static void Error(string message) => Source.LogError(message);
}
