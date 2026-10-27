using System;
using System.Text;
using BepInEx.Configuration;
using MoonCleaners;
using UnityEngine;

namespace MSML.MoonSweeperPlus;

/// <summary>
/// Raises how many capsules a moon may hold.
/// <para>
/// The game stops making capsules (packing dust, stone presses, pressure chambers...) once the moon holds
/// <c>MoonSettings.maxDroppedCanisters</c> of them, 10,000, counting every capsule riding a belt; it never goes past
/// 100,000 whatever that setting says. This sets it to the chosen limit. In a crew the host's moon is the one that
/// counts: crewmates use the host's limit (sent to them), so everyone agrees on when a capsule can be made.
/// </para>
/// </summary>
internal static class CapsuleLimit
{
    private const int GameLimit = 10_000;
    private const int GameCeiling = 100_000;   // the game caps at min(100,000, the setting)
    private const string Topic = "msml.moonsweeperplus.capsulelimit";

    private static ConfigEntry<int> _limit = null!;
    private static int? _hostLimit;
    private static float _nextCheck, _nextSend;
    private static int _applied = -1;

    public static void Bind(ConfigFile config)
    {
        _limit = config.Bind("Capsules", "CapsuleLimit", GameLimit, new ConfigDescription(
            "The most capsules a moon may hold, counting every capsule on belts (the game's limit is 10,000; it can't go " +
            "past 100,000). At the limit nothing can pack capsules until some are used up. More capsules cost frame rate: " +
            "raise it gradually. In a crew the host's limit is used.",
            new AcceptableValueRange<int>(GameLimit, GameCeiling), ModSettings.Step(1000)));
        _limit.SettingChanged += (_, _) => { _nextCheck = 0f; _nextSend = 0f; };
        Moon.Update += Tick;
        CrewMessages.On(Topic, OnHostLimit);
    }

    /// <summary>The limit in force: the host's in someone else's crew, else this player's.</summary>
    private static int Limit => CrewMessages.InCrew && !CrewMessages.IsHost && _hostLimit is { } host ? host : _limit.Value;

    private static void Tick()
    {
        var now = Time.unscaledTime;
        if (!CrewMessages.InCrew) _hostLimit = null;
        // Host: tell the crew now and then (a crewmate may have just joined).
        if (CrewMessages.InCrew && CrewMessages.IsHost && now >= _nextSend)
        {
            _nextSend = now + 5f;
            CrewMessages.SendToCrew(Topic, Encoding.UTF8.GetBytes(_limit.Value.ToString()));
        }
        if (now < _nextCheck) return;
        _nextCheck = now + 1f;
        try
        {
            // The settings are shared by every moon (and may be reset when one loads): kept at the limit.
            var settings = Moon.World?.settings;
            if (settings == null) return;
            var limit = Math.Clamp(Limit, GameLimit, GameCeiling);
            if (settings.maxDroppedCanisters == limit) return;
            settings.maxDroppedCanisters = limit;
            if (limit != _applied) Plugin.Logger.LogInfo($"Capsule limit: {limit:N0}{(CrewMessages.InCrew && !CrewMessages.IsHost ? " (the host's)" : "")}.");
            _applied = limit;
        }
        catch (Exception e) { Plugin.Logger.LogError("Setting the capsule limit failed: " + e.Message); }
    }

    private static void OnHostLimit(ulong sender, byte[] data)
    {
        if (CrewMessages.IsHost || sender != CrewMessages.Host) return;
        if (!int.TryParse(Encoding.UTF8.GetString(data), out var limit)) return;
        if (_hostLimit != limit) { _hostLimit = limit; _nextCheck = 0f; }
    }
}
