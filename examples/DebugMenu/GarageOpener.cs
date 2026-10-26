using System;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.DebugMenu;

/// <summary>
/// Opens the garage menu from anywhere. Uses a garage on the moon if there is one (buying and calling vehicles back
/// work as usual); without one it opens a browse-only view: the game is told the made-up garage id exists only while
/// the menu opens, so buying fails with the game's own "unavailable" message instead of creating a vehicle nowhere.
/// </summary>
internal static class GarageOpener
{
    private const int BrowseOnlyId = 9999;

    private static bool _requested;
    private static int _waitFrames;
    private static bool _opening;
    private static string? _status;

    public static void Patch(Harmony harmony) => harmony.PatchAll(typeof(GarageOpener));

    /// <summary>Asks for the garage menu; it opens once the pause menu has closed.</summary>
    public static string Request()
    {
        if (Game.Equipment == null) return "Not in a game.";
        if (Game.Equipment.Vehicle != null) return "Get out of the vehicle first.";
        var pause = Game.Find<MoonPauseMenu>();
        if (pause != null && pause.IsOpen) pause.Resume();
        _requested = true;
        _waitFrames = 3;   // the pause menu releases the UI a frame or two after it closes
        return "Opening the garage...";
    }

    public static string? TakeStatus()
    {
        var s = _status;
        _status = null;
        return s;
    }

    public static void Tick()
    {
        if (!_requested || --_waitFrames > 0) return;
        _requested = false;
        try { _status = Open(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
            _status = "Couldn't open the garage: " + e.Message;
        }
    }

    private static string Open()
    {
        var menu = Game.Find<MoonGarageMenu>();
        if (menu == null) return "No garage menu found.";
        if (MoonGarage.all.Count > 0 && MoonGarage.all[0] != null)
        {
            var garage = MoonGarage.all[0];
            var id = garage.Id;
            return menu.Open(id) ? $"Garage {id} open." : "The game wouldn't open the garage (is another menu open?).";
        }
        _opening = true;
        try
        {
            return menu.Open(BrowseOnlyId)
                ? "No garage on this moon: browsing only (buying is disabled until you build one)."
                : "The game wouldn't open the garage (is another menu open?).";
        }
        finally { _opening = false; }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.IsGarage))]
    [HarmonyPrefix]
    private static bool IsGarage(int id, ref bool __result)
    {
        if (!_opening || id != BrowseOnlyId) return true;
        __result = true;
        return false;
    }
}
