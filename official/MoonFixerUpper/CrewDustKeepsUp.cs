using BepInEx.Configuration;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MSML.MoonFixerUpper;

/// <summary>
/// Crewmates: the dust behind your own vehicle keeps up when several players are cleaning.
/// <para>
/// A crewmate's game cleans everyone's strokes, its own and every other player's (sent by the host), in one queue of
/// at most 4,096 strokes. A wide head is cleaned as a stroke per ~2 m strip every frame (a 25 m head: ~13 strokes a
/// frame), so with a few players cleaning the queue fills: your own strokes wait behind everyone else's, or are
/// dropped when it's full (the game drops strokes then), and the dust behind you clears seconds late, if at all until
/// the host's dust sync arrives. Measured in a crew: over 2,800 strokes waiting even with 12 ms a frame for cleaning.
/// </para>
/// <para>
/// While the queue is backed up, strokes far from you (other players' work you can't see up close) aren't queued: the
/// host's dust sync brings that dust up to date anyway. Strokes near you, your own and anyone cleaning beside you,
/// always are. Only crewmates do this: the host cleans every stroke (its moon is the one that counts).
/// </para>
/// </summary>
internal static class CrewDustKeepsUp
{
    private static ConfigEntry<bool> _enabled = null!;
    private static ConfigEntry<float> _reach = null!;
    private const int Backed = 48;   // strokes waiting: one frame's worth (the game cleans up to 48 a frame)

    private static Vector3 _me;
    private static bool _active;
    private static int _skipped;
    private static float _nextReport;

    public static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Fixes", "CrewDustKeepsUp", true,
            "Crewmates: when several players are cleaning, the dust behind your own vehicle keeps up. While your game's " +
            "cleaning queue is backed up, other players' strokes far from you are left to the host's dust sync instead of " +
            "being cleaned on your machine too, so yours aren't stuck behind them. The host is never affected.");
        _reach = config.Bind("Fixes", "CrewDustReach", 60f, new ConfigDescription(
            "Crewmates: strokes this close to you (metres) are always cleaned on your machine, however busy it is.",
            new AcceptableValueRange<float>(20f, 300f), ModSettings.Step(10)));
        Moon.Update += Tick;
    }

    private static void Tick()
    {
        var world = Moon.World;
        _active = _enabled.Value && world != null && CrewMessages.InCrew && !CrewMessages.IsHost && world.PendingToolStrokes > Backed;
        if (!_active) return;
        var camera = Camera.main;
        if (camera != null) _me = camera.transform.position;
        if (_skipped > 0 && Time.unscaledTime >= _nextReport)
        {
            _nextReport = Time.unscaledTime + 30f;
            Plugin.Logger.LogInfo($"Busy cleaning queue: {_skipped} far strokes left to the host's dust sync so far.");
        }
    }

    // Every stroke on its way into the queue.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.Enqueue))]
    [HarmonyPrefix]
    private static bool BeforeEnqueue(Vector3 from, ref bool __result)
    {
        if (!_active) return true;
        var dx = from.x - _me.x; var dy = from.y - _me.y; var dz = from.z - _me.z;
        var reach = _reach.Value;
        if (dx * dx + dy * dy + dz * dz <= reach * reach) return true;
        _skipped++;
        __result = false;
        return false;
    }
}
