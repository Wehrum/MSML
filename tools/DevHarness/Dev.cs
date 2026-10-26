using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoonSweepMods.DevHarness;

/// <summary>
/// Helpers for dev scripts (available unqualified in every script: <c>Player</c>, <c>Spawn("Push sweeper")</c>, ...).
/// Iterator helpers (Board, WaitFor) are used with <c>yield return</c>.
/// </summary>
public static class Dev
{
    // ---- the local player and world --------------------------------------------------------------

    private static MoonPrototypeEquipment? _equipment;

    /// <summary>The local player's equipment (tools, energy, current vehicle), or null outside a game.</summary>
    public static MoonPrototypeEquipment? Equipment
    {
        get
        {
            if (_equipment != null && !_equipment.WasCollected && _equipment) return _equipment;
            _equipment = null;
            foreach (var o in Object.FindObjectsByType(Il2CppType.Of<MoonPrototypeEquipment>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var eq = o.TryCast<MoonPrototypeEquipment>();
                if (eq != null && eq.Player != null) { _equipment = eq; break; }
            }
            return _equipment;
        }
    }

    public static bool InGame => Equipment != null;
    public static MoonWalker Player => Equipment?.Player ?? throw new InvalidOperationException("Not in a game.");
    public static MoonWorld World => Equipment?.World ?? throw new InvalidOperationException("Not in a game.");
    public static string PlayerId => Equipment?.Inventory?.playerId ?? "local";
    public static Vector3 Position => Player.transform.position;
    /// <summary>"Up" at the player: the moon is a sphere centred on the origin.</summary>
    public static Vector3 Up => Position.normalized;

    /// <summary>The vehicle the player is driving, or null.</summary>
    public static MoonPrototypeVehicle? Driving => Equipment?.Vehicle;

    // ---- finding things ----------------------------------------------------------------------------

    public static T? Find<T>() where T : Object => FindAll<T>().FirstOrDefault();

    public static List<T> FindAll<T>() where T : Object =>
        Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Select(o => o.TryCast<T>()).Where(o => o != null).Select(o => o!).ToList();

    /// <summary>Evaluates a dev.ps1 expression: <c>Get("find:MoonPauseMenu.home")</c>, <c>Get("static:Type:Member")</c>.</summary>
    public static object? Get(string expression) => Harness.Resolve(expression);

    /// <summary>A transform hierarchy with components, rects and text.</summary>
    public static string Dump(object target, int depth = 2)
    {
        var sb = new StringBuilder();
        Harness.Dump(Harness.TransformOf(target), sb, 0, depth);
        return sb.ToString();
    }

    /// <summary>One-line description of any value (fields and properties of objects, items of lists).</summary>
    public static string Describe(object? value) => Harness.Describe(value);

    // ---- money -------------------------------------------------------------------------------------

    public static double Money
    {
        get => ToDouble(World.WalletDollars(PlayerId));
        set => World.SetTeamDollars(Dec(value));
    }

    public static double ToDouble(Il2CppSystem.Decimal d) => double.Parse(d.ToString(), CultureInfo.InvariantCulture);
    public static Il2CppSystem.Decimal Dec(double d) => new(d);

    // ---- moving around -----------------------------------------------------------------------------

    public static void Teleport(Vector3 position) => Player.Teleport(position, true);

    /// <summary>The ground below a point (raycast down from above), or the point itself if nothing is hit.</summary>
    public static Vector3 Ground(Vector3 point)
    {
        var up = point.normalized;
        var best = float.MaxValue;
        Vector3? found = null;
        foreach (var hit in Physics.RaycastAll(point + up * 40f, -up, 120f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || hit.collider.attachedRigidbody != null) continue;
            if (hit.distance < best) { best = hit.distance; found = hit.point; }
        }
        return found ?? point;
    }

    /// <summary>Flat direction the camera (or player) faces.</summary>
    public static Vector3 Forward
    {
        get
        {
            var look = Camera.main != null ? Camera.main.transform.forward : Player.transform.forward;
            var f = Vector3.ProjectOnPlane(look, Up).normalized;
            return f.sqrMagnitude > 0.01f ? f : Vector3.ProjectOnPlane(Player.transform.forward, Up).normalized;
        }
    }

    /// <summary>The ground <paramref name="metres"/> in front of the player.</summary>
    public static Vector3 Ahead(float metres) => Ground(Position + Forward * metres);

    // ---- vehicles ----------------------------------------------------------------------------------

    /// <summary>Catalog index of a vehicle by (case-insensitive, partial) name.</summary>
    public static int VehicleIndex(string name)
    {
        var vehicles = MoonPrototypeCatalog.Vehicles;
        for (var i = 0; i < vehicles.Length; i++) if (vehicles[i].name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < vehicles.Length; i++) if (vehicles[i].name.Contains(name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new ArgumentException($"No vehicle named '{name}'. Vehicles: {string.Join(", ", Enumerable.Range(0, vehicles.Length).Select(i => $"#{i} {vehicles[i].name}"))}");
    }

    public static MoonPrototypeVehicle Spawn(string name, float? metres = null) => Spawn(VehicleIndex(name), metres);

    /// <summary>
    /// Spawns a catalog vehicle in front of the player with the game's own model and vehicle code (not owned: it isn't
    /// saved). Give it a couple of frames to settle before boarding.
    /// </summary>
    public static MoonPrototypeVehicle Spawn(int index, float? metres = null)
    {
        var eq = Equipment ?? throw new InvalidOperationException("Not in a game.");
        var spec = MoonPrototypeCatalog.Vehicles[index];
        var distance = metres ?? 8f + Mathf.Min(spec.width, 30f) * 0.8f;
        var forward = Forward;
        var position = Ground(Position + forward * distance) + Up * 0.6f;

        var root = new GameObject($"Vehicle {spec.name} (dev)");
        if (eq.RuntimeRoot != null) root.transform.SetParent(eq.RuntimeRoot, true);
        root.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Up));

        var model = eq.Models.Vehicle(index, root.transform);
        if (model != null) model.SetActive(true);   // built inactive; the view must be awake before Initialize
        var vehicle = root.GetComponentInChildren<MoonPrototypeVehicle>(true) ?? root.AddComponent<MoonPrototypeVehicle>();
        var view = model != null ? model.GetComponent<MoonAuthoredVehicleView>() : null;
        if (view != null) { vehicle.authored = view; view.vehicle = vehicle; }
        var storage = $"dev-{index}-{Guid.NewGuid():N}";
        World.RegisterVehicleStorage(storage, MoonPrototypeCatalog.StorageKg(index));
        vehicle.Initialize(eq, index, storage);
        return vehicle;
    }

    /// <summary>Walks up to a vehicle and gets in (<c>yield return Board(v)</c>). Throws if the game refuses.</summary>
    public static IEnumerator Board(MoonPrototypeVehicle vehicle)
    {
        if (Driving != null && Driving.Pointer == vehicle.Pointer) yield break;
        if (Driving != null) { Driving.Exit(); yield return 0.5f; }
        Teleport(vehicle.ExitSpot());
        yield return 0.3f;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (vehicle.Enter()) { yield return 0.5f; yield break; }
            yield return 0.3f;
        }
        throw new InvalidOperationException($"Couldn't get into {vehicle.Spec?.name}.");
    }

    /// <summary>Gets out of the current vehicle.</summary>
    public static bool Leave() => Driving?.Exit() ?? false;

    /// <summary>Holds the driving controls (throttle and steer -1..1) for some seconds.</summary>
    public static void Drive(float throttle, float steer, float seconds)
    {
        DrivePatch.Throttle = throttle;
        DrivePatch.Steer = steer;
        DrivePatch.Until = Time.unscaledTime + seconds;
    }

    // ---- UI and waiting ----------------------------------------------------------------------------

    /// <summary>Clicks like the mouse (UI raycast at the object's centre). Takes an expression or a component.</summary>
    public static string Click(string expression) => Click((Component)Harness.TransformOf(Harness.Resolve(expression))!);

    public static string Click(Component target) => Harness.RayClick(target.transform.Cast<RectTransform>());

    /// <summary>Screenshot saved at the end of this frame to BepInEx\devshots\&lt;name&gt;.png; returns the path.</summary>
    public static string Shot(string name = "shot")
    {
        var path = Path.Combine(Paths.BepInExRootPath, "devshots", name.EndsWith(".png") ? name : name + ".png");
        Harness.Screenshot(path);
        return path;
    }

    /// <summary>Waits until <paramref name="condition"/> is true (<c>yield return WaitFor(...)</c>); throws after the timeout.</summary>
    public static IEnumerator WaitFor(Func<bool> condition, float timeout = 10f, string? what = null)
    {
        var end = Time.unscaledTime + timeout;
        while (!condition())
        {
            if (Time.unscaledTime > end) throw new TimeoutException($"Timed out after {timeout} s waiting for {what ?? "condition"}.");
            yield return null;
        }
    }

    public static float Now => Time.unscaledTime;

    /// <summary>
    /// The items of an Il2Cpp collection (List, Dictionary, KeyCollection, HashSet...) as a normal list, so LINQ works:
    /// <c>Items&lt;string&gt;(World.vehicleStorage.Keys)</c>.
    /// </summary>
    public static List<T> Items<T>(object collection)
    {
        var list = new List<T>();
        var enumerator = collection.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)!.Invoke(collection, null)!;
        var moveNext = enumerator.GetType().GetMethod("MoveNext")!;
        var current = enumerator.GetType().GetProperty("Current")!;
        while ((bool)moveNext.Invoke(enumerator, null)!) list.Add((T)current.GetValue(enumerator)!);
        return list;
    }
}
