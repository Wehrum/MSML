using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.HexHarvester;

/// <summary>
/// Runs every frame: hides the local player's body while they drive a Hex Harvester (the cab is enclosed, and the
/// player model clips through it), and restores it when they get out.
/// </summary>
public class HarvesterRuntime : MonoBehaviour
{
    private readonly List<Renderer> _hidden = new();
    private MoonPrototypeEquipment? _equipment;
    private float _nextLookup;

    public HarvesterRuntime(IntPtr ptr) : base(ptr) { }

    private void LateUpdate()
    {
        try
        {
            var eq = Equipment();
            var vehicle = eq?.Vehicle;
            var driving = vehicle != null && vehicle.Index == Plugin.Index && vehicle.IsDriven;
            if (driving) Hide(eq!.Player.gameObject, vehicle!);
            else Restore();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
            Restore();
        }
    }

    private MoonPrototypeEquipment? Equipment()
    {
        if (_equipment != null && !_equipment.WasCollected && _equipment) return _equipment;
        _equipment = null;
        if (Time.unscaledTime < _nextLookup) return null;
        _nextLookup = Time.unscaledTime + 1f;
        foreach (var o in FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<MoonPrototypeEquipment>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            var eq = o.TryCast<MoonPrototypeEquipment>();
            if (eq != null && eq.Player != null) { _equipment = eq; break; }
        }
        return _equipment;
    }

    private void Hide(GameObject player, MoonPrototypeVehicle vehicle)
    {
        var before = _hidden.Count;
        // Every frame: the game may re-enable parts of the player (tools, first-person arms).
        foreach (var r in player.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled) continue;
            r.enabled = false;
            _hidden.Add(r);
        }
        if (before == 0 && _hidden.Count > 0) Plugin.Logger.LogInfo($"Driver hidden ({_hidden.Count} player renderers).");
    }

    private void Restore()
    {
        if (_hidden.Count == 0) return;
        foreach (var r in _hidden)
            if (r != null && !r.WasCollected && r) r.enabled = true;
        Plugin.Logger.LogInfo($"Driver shown again ({_hidden.Count} player renderers).");
        _hidden.Clear();
    }
}
