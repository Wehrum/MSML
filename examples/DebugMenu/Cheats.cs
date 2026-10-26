using System;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.DebugMenu;

/// <summary>Cheat toggles applied every frame while enabled.</summary>
internal static class Cheats
{
    private const double UnlimitedFloor = 1_000_000;

    public static bool UnlimitedMoney;
    public static bool UnlimitedEnergy;
    public static bool Flying;

    public static double Money => Game.World == null ? 0 : ToDouble(Game.World.WalletDollars(Game.PlayerId));

    public static void AddMoney(double dollars)
    {
        var world = Game.World ?? throw new InvalidOperationException("Not in a game.");
        SetMoney(ToDouble(world.WalletDollars(Game.PlayerId)) + dollars);
    }

    public static void SetMoney(double dollars)
    {
        var world = Game.World ?? throw new InvalidOperationException("Not in a game.");
        world.SetTeamDollars(Dec(dollars));
    }

    public static void SetFlying(bool on)
    {
        var player = Game.Equipment?.Player ?? throw new InvalidOperationException("Not in a game.");
        player.SetFlying(on, false);
        Flying = on;
    }

    public static string SetVehicleDust(bool full)
    {
        var eq = Game.Equipment ?? throw new InvalidOperationException("Not in a game.");
        var vehicle = eq.Vehicle ?? throw new InvalidOperationException("Get in a vehicle first.");
        eq.World.DevSetVehicleDust(vehicle.StorageId, full ? vehicle.DustCapacityKg : 0);
        return full ? "Hopper filled" : "Hopper emptied";
    }

    public static void Tick()
    {
        var eq = Game.Equipment;
        if (eq == null) return;
        if (UnlimitedMoney && Money < UnlimitedFloor) SetMoney(UnlimitedFloor * 10);
        if (UnlimitedEnergy && eq._Energy_k__BackingField < 1f) eq._Energy_k__BackingField = 1f;
    }

    public static double ToDouble(Il2CppSystem.Decimal d) => double.Parse(d.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    public static Il2CppSystem.Decimal Dec(double d) => new(d);
}
