using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace MSML.HexHarvester;

/// <summary>
/// Shows the hopper's fill in the glass silo: the dust column (Design: "Silo dust", pivot at the bottom of the glass)
/// is scaled to the fill level, easing towards it like the garage silo does.
/// </summary>
internal static class SiloFill
{
    private const float Rate = 0.5f;   // fill shown per second, at most

    private sealed class State
    {
        public Transform? Dust;
        public float Shown = -1;
        public bool GarageLook;
    }

    private static readonly Dictionary<IntPtr, State> States = new();

    /// <summary>Your own vehicle: the hopper's kilograms over its capacity.</summary>
    public static void Update(MoonPrototypeVehicle vehicle)
    {
        var capacity = vehicle.DustCapacityKg;
        var target = capacity > 0 ? Mathf.Clamp01((float)(vehicle.DustKilograms / capacity)) : 0f;
        Show(vehicle.Pointer, vehicle.authored == null ? null : vehicle.authored.transform, target);
    }

    /// <summary>A crewmate's vehicle: the fill level the game receives for it (0-1).</summary>
    public static void Update(MoonRemoteVehicle vehicle) =>
        Show(vehicle.Pointer, vehicle.AuthoredView == null ? null : vehicle.AuthoredView.transform, Mathf.Clamp01(vehicle.fill));

    private static void Show(IntPtr key, Transform? model, float target)
    {
        if (!States.TryGetValue(key, out var state))
        {
            if (States.Count > 64) Prune();
            States[key] = state = new State();
        }
        if (state.Dust == null || state.Dust.WasCollected)
        {
            state.Dust = model == null ? null : FindDeep(model, HexHarvesterDesign.SiloDustGroup);
            if (state.Dust == null) return;
            state.GarageLook = false;
        }
        // Models built while a save loads come before the garages; use the garage silo's dust once one exists.
        if (!state.GarageLook && HarvesterModel.GarageMaterial("Garage - Container dust") is { } garageDust)
        {
            foreach (var r in state.Dust.GetComponentsInChildren<Renderer>(true)) r.sharedMaterial = garageDust;
            state.GarageLook = true;
        }

        state.Shown = state.Shown < 0 ? target : Mathf.MoveTowards(state.Shown, target, Rate * Time.deltaTime);
        var visible = state.Shown > 0.002f;
        if (state.Dust.gameObject.activeSelf != visible) state.Dust.gameObject.SetActive(visible);
        if (visible) state.Dust.localScale = new Vector3(1f, state.Shown, 1f);
    }

    private static Transform? FindDeep(Transform parent, string name)
    {
        foreach (var t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private static void Prune()
    {
        var dead = new List<IntPtr>();
        foreach (var (key, state) in States)
            if (state.Dust == null || state.Dust.WasCollected) dead.Add(key);
        foreach (var key in dead) States.Remove(key);
    }
}
