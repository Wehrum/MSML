using System.Collections.Generic;
using System.Linq;
using MoonCleaners;
using UnityEngine;

namespace MSML;

/// <summary>Parks a vehicle on clear ground in front of a garage's door (for vehicles too big for its hall).</summary>
internal static class GarageDelivery
{
    private static readonly float[] Ahead = { 14f, 18f, 22f, 26f, 30f, 36f, 42f, 50f };
    private static readonly float[] Sideways = { 0f, -12f, 12f, -24f, 24f };

    public static bool MoveOutside(MoonGarage garage, MoonPrototypeVehicle vehicle)
    {
        var spot = garage.Spot();   // the hall's delivery pose; forward points out of the door
        var own = vehicle.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger && c.enabled).ToList();
        var half = HalfExtents(vehicle, own);
        var up = spot.position.normalized;   // the moon is a sphere centred on the origin

        foreach (var ahead in Ahead)
            foreach (var side in Sideways)
            {
                var ground = Ground(spot.position + spot.forward * ahead + spot.right * side, up);
                if (ground == null || Mathf.Abs(Vector3.Dot(ground.Value - spot.position, up)) > 2.5f) continue;
                var centre = ground.Value + up * (half.y + 0.4f);
                var blocked = Physics.OverlapBox(centre, half, spot.rotation, ~0, QueryTriggerInteraction.Ignore)
                    .Any(c => c != null && !c.name.StartsWith("Terrain") && !own.Any(o => o.Pointer == c.Pointer));
                if (blocked) continue;

                var position = ground.Value + up * 0.8f;
                var body = vehicle.body;
                if (body != null)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.position = position;
                    body.rotation = spot.rotation;
                }
                vehicle.transform.SetPositionAndRotation(position, spot.rotation);
                return true;
            }
        return false;
    }

    /// <summary>Half size of the vehicle's colliders in its own orientation, in world units.</summary>
    private static Vector3 HalfExtents(MoonPrototypeVehicle vehicle, List<Collider> colliders)
    {
        var t = vehicle.transform;
        var local = new Bounds();
        var first = true;
        foreach (var c in colliders)
        {
            var b = c.bounds;
            for (var i = 0; i < 8; i++)
            {
                var corner = t.InverseTransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                if (first) { local = new Bounds(corner, Vector3.zero); first = false; }
                else local.Encapsulate(corner);
            }
        }
        return Vector3.Scale(local.extents, t.lossyScale);
    }

    /// <summary>The terrain or floor below a point (ignoring vehicles and other moving bodies).</summary>
    private static Vector3? Ground(Vector3 point, Vector3 up)
    {
        var best = float.MaxValue;
        Vector3? found = null;
        foreach (var hit in Physics.RaycastAll(point + up * 40f, -up, 120f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || hit.collider.attachedRigidbody != null) continue;
            if (hit.distance < best) { best = hit.distance; found = hit.point; }
        }
        return found;
    }
}
