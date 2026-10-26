using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>
/// Something of a mod's that runs on the solar grid like a machine (see <see cref="Power.Add"/>).
/// </summary>
public sealed class PowerUser
{
    internal PowerUser(Vector3 position, float kw) { Position = position; Kw = kw; }

    /// <summary>Where it is (the blue power beam ends here). Can be moved.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Power it draws when running, in kW. Can be changed.</summary>
    public float Kw { get; set; }

    /// <summary>How much of its power it gets, 0-1 (0 outside every panel's range, or with no power left).</summary>
    public float Power { get; internal set; }

    /// <summary>The grid it's on (a panel's id), or 0.</summary>
    public int Grid { get; internal set; }

    internal int Panel { get; set; }
    internal Vector3 PanelPoint { get; set; }
    internal bool Removed { get; set; }
}

/// <summary>
/// Lets mods' things run on the solar grid like the game's machines. A power user is on the grid of the nearest solar
/// panel in range (like a machine), counts as one of the grid's machines, draws spare solar power first and then the
/// grid's batteries, and shows the blue power beam from its panel (when looking at the grid, and nearby). Its
/// <see cref="PowerUser.Power"/> says how much it got.
/// </summary>
public static class Power
{
    private static readonly List<PowerUser> Users = new();
    private static bool _patched;

    /// <summary>Adds a power user at a position, drawing <paramref name="kw"/> when running.</summary>
    public static PowerUser Add(Vector3 position, float kw)
    {
        var user = new PowerUser(position, kw);
        Users.Add(user);
        if (!_patched) { _patched = true; new Harmony("msml.api.power").PatchAll(typeof(Power)); }
        return user;
    }

    /// <summary>Removes a power user (it stops drawing and loses its beam).</summary>
    public static void Remove(PowerUser? user)
    {
        if (user == null) return;
        user.Removed = true;
        user.Power = 0f;
        Users.Remove(user);
    }

    // ---- sharing out power ---------------------------------------------------------------------

    private static float _nextJoin;

    // Which panel's grid each user is on (the nearest panel in range), refreshed every second.
    private static void Join(MoonWorld w)
    {
        if (Time.unscaledTime < _nextJoin) return;
        _nextJoin = Time.unscaledTime + 1f;
        var panels = new List<(int Id, Vector3 Position, float Range, int Grid)>();
        foreach (var node in w.powerNodes)
            if (node != null && node.panel && node.state != null && node.record != null)
                panels.Add((node.record.id, node.position, node.spec.range, node.state.Grid));
        foreach (var user in Users)
        {
            var best = panels.Where(p => (p.Position - user.Position).sqrMagnitude <= p.Range * p.Range)
                             .OrderBy(p => (p.Position - user.Position).sqrMagnitude).FirstOrDefault();
            user.Grid = best.Grid;
            user.Panel = best.Id;
            if (best.Id == 0) user.Power = 0f;
            else if (w.PowerAnchor(best.Id, out var anchor)) user.PanelPoint = anchor;
            else user.PanelPoint = best.Position;
        }
    }

    // After the game shares out each grid's power: users take theirs, spare solar first, then the batteries.
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.SolvePower))]
    [HarmonyPostfix]
    private static void AfterSolvePower(MoonWorld __instance, float dt)
    {
        try
        {
            if (Users.Count == 0) return;
            Join(__instance);
            foreach (var group in Users.Where(u => u.Grid != 0).GroupBy(u => u.Grid))
            {
                if (!__instance.powerGrids.ContainsKey(group.Key)) { foreach (var u in group) u.Power = 0f; continue; }
                var grid = __instance.powerGrids[group.Key];
                var demand = group.Sum(u => Math.Max(0f, u.Kw));
                var power = 1f;
                if (demand > 0f)
                {
                    var spare = Math.Max(0f, grid.supplyKw - grid.usedKw);
                    var fromBattery = Math.Max(0f, demand - spare);
                    if (fromBattery > 0f)
                    {
                        var drawn = DrainBatteries(__instance, group.Key, fromBattery * dt / 3600.0);
                        var shortKw = fromBattery - (float)(drawn * 3600.0 / Math.Max(dt, 1e-4f));
                        power = Mathf.Clamp01(1f - shortKw / demand);
                    }
                    grid.demandKw += demand;
                    grid.usedKw += demand * power;
                }
                grid.machines += group.Count();
                __instance.powerGrids[group.Key] = grid;
                foreach (var u in group) u.Power = power;
                SupplyFromPanels(__instance, group.Key, demand * power);
            }
        }
        catch (Exception e) { Log.Error("Powering mod machines failed: " + e); }
    }

    /// <summary>Takes up to <paramref name="kwh"/> from the grid's panel batteries; returns what it got.</summary>
    private static double DrainBatteries(MoonWorld w, int grid, double kwh)
    {
        var got = 0.0;
        foreach (var node in w.powerNodes)
        {
            if (got >= kwh) break;
            if (node == null || !node.panel || node.state == null || node.state.Grid != grid) continue;
            var take = Math.Min(node.state.ChargeKwh, kwh - got);
            if (take <= 0) continue;
            node.state.ChargeKwh -= take;
            got += take;
        }
        return got;
    }

    // The panels' "to machines" readout includes what mod machines take, shared over the grid's panels.
    private static void SupplyFromPanels(MoonWorld w, int grid, float kw)
    {
        if (kw <= 0f) return;
        var panels = new List<MoonProductionState>();
        foreach (var node in w.powerNodes)
            if (node != null && node.panel && node.state != null && node.state.Grid == grid) panels.Add(node.state);
        foreach (var state in panels) state.SuppliedKw += kw / panels.Count;
    }

    // ---- power beams ---------------------------------------------------------------------------

    private const float AmbientDistance = 70f;
    private static bool _ambient;
    private static int _grid;

    [HarmonyPatch(typeof(MoonPowerLinks), nameof(MoonPowerLinks.ShowAmbient))]
    [HarmonyPrefix]
    private static void BeforeAmbient() { _ambient = true; _grid = 0; }

    [HarmonyPatch(typeof(MoonPowerLinks), nameof(MoonPowerLinks.ShowAmbient))]
    [HarmonyFinalizer]
    private static void AfterAmbient() => _ambient = false;

    [HarmonyPatch(typeof(MoonPowerLinks), nameof(MoonPowerLinks.ShowGrid))]
    [HarmonyPrefix]
    private static void BeforeGrid(MoonPowerLinks __instance, int id)
    {
        _ambient = false;
        _grid = id;
        try { _grid = __instance.world?.ProductionOf(id)?.Grid ?? id; } catch { }
    }

    [HarmonyPatch(typeof(MoonPowerLinks), nameof(MoonPowerLinks.ShowGrid))]
    [HarmonyFinalizer]
    private static void AfterGrid() => _grid = 0;

    // Just before the game finishes drawing its beams, add ours (same look as the game's machine links).
    [HarmonyPatch(typeof(MoonPowerLinks), nameof(MoonPowerLinks.End))]
    [HarmonyPrefix]
    private static void BeforeEnd(MoonPowerLinks __instance)
    {
        if (Users.Count == 0 || (!_ambient && _grid == 0)) return;
        try
        {
            var eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var color = __instance.lines != null && __instance.lines.Count > 0 && __instance.lines[0] != null
                ? __instance.lines[0].startColor : new Color(0.3f, 0.85f, 1f, 0.9f);
            foreach (var user in Users)
            {
                if (user.Panel == 0 || user.Power <= 0.01f) continue;
                if (_grid != 0 && user.Grid != _grid) continue;
                if (_ambient && (user.Position - eye).sqrMagnitude > AmbientDistance * AmbientDistance) continue;
                __instance.Arc(user.PanelPoint, user.Position, 0.07f + 0.05f * user.Power, color);
            }
        }
        catch (Exception e) { Log.Error("Drawing mod machines' power beams failed: " + e); }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterWorldDestroyed()
    {
        foreach (var user in Users) { user.Grid = 0; user.Panel = 0; user.Power = 0f; }
    }
}
