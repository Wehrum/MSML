using System;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Keeps the dust up with the cleaning. The game cleans within a fixed time each frame
/// (<c>MoonSettings.cleaningWorkBudgetMs</c>, 2 ms) and redraws the dust within another (<c>dustVisualBudgetMs</c>,
/// 2 ms), sized for a few stock vehicles. Wide heads (the Hex Harvester's 25 m) and several players cleaning at once
/// (a crewmate's game cleans everyone's strokes too) queue far more strokes than that: measured in a crew, thousands
/// waited, and the dust cleared seconds behind the vehicles. The time allowed now follows the queues: the game's own
/// when they're short, up to <see cref="MaxBudgetMs"/> as strokes (or dust redraws) pile up, easing back as they clear.
/// </summary>
internal static class CleaningBudget
{
    private const float MaxBudgetMs = 12f;
    private const int QuietStrokes = 16;      // the game's own budget keeps up with this many waiting
    private const int FullStrokes = 96;       // this many waiting (two frames' worth): the most time. At 400, a 25 m
                                              // head's own strokes waited ~0.5 s (up to 1.4 s) before being cleaned
    private const int QuietBanks = 2;
    private const int FullBanks = 16;
    private const float EaseBackPerSecond = 4f;   // ms the budget drops per second once the queue has cleared

    private static bool _applied;
    private static MoonSettings? _settings;
    private static float _baseWork = -1f, _baseVisual = -1f, _work, _visual;

    internal static void Apply()
    {
        if (_applied) return;
        _applied = true;
        new Harmony("msml.api.cleaningbudget").PatchAll(typeof(CleaningBudget));
        Moon.Update += Tick;
    }

    // The budget for a queue this long: the game's own up to quiet, the most from full.
    private static float Wanted(float baseMs, int waiting, int quiet, int full)
    {
        if (waiting <= quiet) return baseMs;
        var t = Mathf.Clamp01((waiting - quiet) / (float)(full - quiet));
        return Mathf.Lerp(baseMs, Mathf.Max(baseMs, MaxBudgetMs), t);
    }

    // Rises straight away, eases back slowly (a queue that just emptied usually fills again).
    private static float Follow(float current, float wanted) =>
        wanted >= current ? wanted : Mathf.Max(wanted, current - EaseBackPerSecond * Time.unscaledDeltaTime);

    private static void Tick()
    {
        var world = Moon.World;
        var settings = world?.settings;
        if (world == null || settings == null) return;
        if (_settings == null || _settings.WasCollected || _settings.Pointer != settings.Pointer)
        {
            _settings = settings;
            _baseWork = _work = settings.cleaningWorkBudgetMs;
            _baseVisual = _visual = settings.dustVisualBudgetMs;
        }
        _work = Follow(_work, Wanted(_baseWork, world.PendingToolStrokes, QuietStrokes, FullStrokes));
        _visual = Follow(_visual, Wanted(_baseVisual, world.PendingDustBanks, QuietBanks, FullBanks));
        if (Math.Abs(settings.cleaningWorkBudgetMs - _work) > 0.01f) settings.cleaningWorkBudgetMs = _work;
        if (Math.Abs(settings.dustVisualBudgetMs - _visual) > 0.01f) settings.dustVisualBudgetMs = _visual;
    }

    // Leaving a moon: the settings asset outlives it, so put its own values back.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterWorldDestroyed()
    {
        if (_settings != null && !_settings.WasCollected && _baseWork > 0f)
        {
            _settings.cleaningWorkBudgetMs = _baseWork;
            _settings.dustVisualBudgetMs = _baseVisual;
        }
        _settings = null;
    }
}
