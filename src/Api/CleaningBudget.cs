using System;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Keeps up with wide cleaning heads. The host cleans for everyone within a fixed time each frame
/// (<c>MoonSettings.cleaningWorkBudgetMs</c> for the cleaning, <c>dustVisualBudgetMs</c> for redrawing the dust),
/// sized for the stock vehicles. A much wider head (like the Hex Harvester's 25 m) makes far more work per metre, so
/// on a busy moon the dust falls seconds behind the vehicle. While strokes wider than a stock head are being cleaned,
/// both limits are raised in proportion to the width (they're limits: the extra time is only used while there's a
/// backlog), and put back a few seconds after.
/// </summary>
internal static class CleaningBudget
{
    private const float StockWidth = 4f;          // the widest stock head is about 3.7 m
    private const float MaxScale = 6f;
    private const float MaxBudgetMs = 12f;
    private const float Linger = 3f;

    private static bool _applied;
    private static MoonSettings? _settings;
    private static float _baseWork = -1f, _baseVisual = -1f;
    private static float _scale = 1f, _until;

    internal static void Apply()
    {
        if (_applied) return;
        _applied = true;
        new Harmony("msml.api.cleaningbudget").PatchAll(typeof(CleaningBudget));
        Moon.Update += Tick;
    }

    private static void Wide(float width)
    {
        if (width <= StockWidth) return;
        var scale = Mathf.Min(MaxScale, width / StockWidth);
        _scale = Time.unscaledTime > _until ? scale : Mathf.Max(_scale, scale);
        _until = Time.unscaledTime + Linger;
    }

    // Every vehicle (and crewmate's, on the host) stroke goes through these.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.QueueToolStroke))]
    [HarmonyPrefix]
    private static void BeforeQueue(float width) => Wide(width);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.Enqueue))]
    [HarmonyPrefix]
    private static void BeforeEnqueue(float width) => Wide(width);

    private static void Tick()
    {
        var settings = Moon.World?.settings;
        if (settings == null) return;
        if (_settings == null || _settings.WasCollected || _settings.Pointer != settings.Pointer)
        {
            _settings = settings;
            _baseWork = settings.cleaningWorkBudgetMs;
            _baseVisual = settings.dustVisualBudgetMs;
        }
        var scale = Time.unscaledTime <= _until ? _scale : 1f;
        if (scale <= 1f) _scale = 1f;
        var work = Mathf.Min(MaxBudgetMs, _baseWork * scale);
        var visual = Mathf.Min(MaxBudgetMs, _baseVisual * scale);
        if (Math.Abs(settings.cleaningWorkBudgetMs - work) > 0.01f) settings.cleaningWorkBudgetMs = work;
        if (Math.Abs(settings.dustVisualBudgetMs - visual) > 0.01f) settings.dustVisualBudgetMs = visual;
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
        _scale = 1f;
        _until = 0f;
    }
}
