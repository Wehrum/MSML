using System;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.DebugMenu;

/// <summary>Spawns any catalog vehicle in front of the player, using the game's own model and vehicle code.</summary>
public static class VehicleSpawner
{
    private static MoonPrototypeVehicle? _tracked;
    private static float _trackUntil;
    private static float _nextTrack;

    public static string Spawn(int index)
    {
        var eq = Game.Equipment ?? throw new InvalidOperationException("Not in a game.");
        var world = eq.World;
        var vehicles = MoonPrototypeCatalog.Vehicles;
        if (index < 0 || index >= vehicles.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var spec = vehicles[index];

        // The moon is a sphere centred on the origin: "up" is away from the centre.
        var playerPos = eq.Player.transform.position;
        var up = playerPos.normalized;
        var look = Camera.main != null ? Camera.main.transform.forward : eq.Player.transform.forward;
        var forward = Vector3.ProjectOnPlane(look, up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(eq.Player.transform.forward, up).normalized;
        var distance = 8f + Mathf.Min(spec.width, 30f) * 0.8f;
        var target = playerPos + forward * distance;
        var position = GroundBelow(target, up, out var groundNote) + up * 0.6f;
        var rotation = Quaternion.LookRotation(forward, up);
        Plugin.Logger.LogInfo($"Spawning {spec.name} (#{index}): player {playerPos}, target {target}, ground {groundNote}, final {position}");

        var root = new GameObject($"Vehicle {spec.name} (debug)");
        if (eq.RuntimeRoot != null) root.transform.SetParent(eq.RuntimeRoot, true);
        root.transform.SetPositionAndRotation(position, rotation);

        var model = eq.Models.Vehicle(index, root.transform);
        if (model == null) Plugin.Logger.LogWarning($"Models.Vehicle({index}) returned no model.");
        // Models are built inactive; activate before Initialize so the view (wheels, contact colliders) wakes up.
        else model.SetActive(true);

        var vehicle = root.GetComponentInChildren<MoonPrototypeVehicle>(true);
        if (vehicle == null) vehicle = root.AddComponent<MoonPrototypeVehicle>();
        var storage = $"debug-{index}-{Guid.NewGuid():N}";
        world.RegisterVehicleStorage(storage, MoonPrototypeCatalog.StorageKg(index));
        vehicle.Initialize(eq, index, storage);
        Plugin.Logger.LogDebug($"Spawned hierarchy:\n{Game.Describe(root, 2)}");
        Plugin.Logger.LogInfo($"Spawned {spec.name}: width {vehicle.CleaningWidth} m, hopper {vehicle.DustCapacityKg} kg, mass {vehicle.body?.mass}, wheels {vehicle.WheelCount}");

        _tracked = vehicle;
        _trackStart = Time.unscaledTime;
        _trackUntil = Time.unscaledTime + 8f;
        _nextTrack = 0;
        _verified = false;
        return $"Spawning {spec.name} {distance:0} m ahead...";
    }

    private static float _trackStart;
    private static bool _verified;

    /// <summary>A result message once the last spawn has been checked (visible + on the ground), or null.</summary>
    public static string? TakeStatus()
    {
        var s = _pendingStatus;
        _pendingStatus = null;
        return s;
    }

    private static string? _pendingStatus;

    private static void Verify()
    {
        if (_verified || _tracked == null || Time.unscaledTime - _trackStart < 2.5f) return;
        _verified = true;
        var name = _tracked.Spec?.name ?? "Vehicle";
        var visible = 0;
        foreach (var r in _tracked.GetComponentsInChildren<Renderer>(false)) if (r.enabled && r.isVisible) visible++;
        var grounded = _tracked.WheelsOnGround;
        var distance = Vector3.Distance(_tracked.transform.position, Game.Equipment?.Player?.transform.position ?? _tracked.transform.position);
        if (!_tracked.gameObject.activeInHierarchy || grounded == 0 || visible == 0)
        {
            _pendingStatus = $"{name} spawned but something's wrong: active {_tracked.gameObject.activeInHierarchy}, " +
                $"{grounded}/{_tracked.WheelCount} wheels on the ground, {visible} parts on screen. See LogOutput.log.";
            Plugin.Logger.LogWarning(_pendingStatus);
        }
        else
        {
            _pendingStatus = $"{name} is on the ground {distance:0} m away ({grounded}/{_tracked.WheelCount} wheels down).";
        }
    }

    /// <summary>Raycasts down onto the terrain; falls back to the target height if nothing is hit.</summary>
    private static Vector3 GroundBelow(Vector3 target, Vector3 up, out string note)
    {
        var from = target + up * 40f;
        var hits = Physics.RaycastAll(from, -up, 120f, ~0, QueryTriggerInteraction.Ignore);
        var best = float.MaxValue;
        Vector3? point = null;
        var name = "";
        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.collider.attachedRigidbody != null) continue;
            if (hit.distance < best) { best = hit.distance; point = hit.point; name = hit.collider.name; }
        }
        note = point.HasValue ? $"hit '{name}' at {point.Value} ({hits.Length} hits)" : $"no ground hit ({hits.Length} hits)";
        return point ?? target;
    }

    /// <summary>Logs the last spawned vehicle for a few seconds so we can see if it falls, flies off or settles.</summary>
    public static void Tick()
    {
        if (_tracked != null && !_tracked.WasCollected && _tracked) Verify();
        if (_tracked == null || Time.unscaledTime > _trackUntil || Time.unscaledTime < _nextTrack) return;
        _nextTrack = Time.unscaledTime + 0.5f;
        if (_tracked.WasCollected || !_tracked) { Plugin.Logger.LogWarning("Spawned vehicle was destroyed."); _tracked = null; return; }
        var p = _tracked.transform.position;
        var player = Game.Equipment?.Player?.transform.position ?? Vector3.zero;
        var v = _tracked.body != null ? _tracked.body.velocity : Vector3.zero;
        Plugin.Logger.LogInfo($"[track] {_tracked.Spec?.name}: altitude {p.magnitude:0.00} (player {player.magnitude:0.00}), " +
            $"distance {Vector3.Distance(p, player):0.0} m, speed {v.magnitude:0.0} m/s, grounded {_tracked.Grounded}, " +
            $"wheels on ground {_tracked.WheelsOnGround}/{_tracked.WheelCount}, active {_tracked.gameObject.activeInHierarchy}");
    }
}
