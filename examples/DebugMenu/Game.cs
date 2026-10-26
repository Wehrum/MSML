using System;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.DebugMenu;

/// <summary>Finds the local player's objects in the running game.</summary>
internal static class Game
{
    private static MoonPrototypeEquipment? _equipment;

    public static MoonPrototypeEquipment? Equipment
    {
        get
        {
            if (_equipment != null && !_equipment.WasCollected && _equipment) return _equipment;
            _equipment = null;
            foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MoonPrototypeEquipment>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var eq = o.TryCast<MoonPrototypeEquipment>();
                if (eq != null && eq.Player != null) { _equipment = eq; break; }
            }
            return _equipment;
        }
    }

    public static MoonWorld? World => Equipment?.World;

    public static string PlayerId => Equipment?.Inventory?.playerId ?? "local";

    public static T? Find<T>() where T : UnityEngine.Object
    {
        var found = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        return found.Length > 0 ? found[0].TryCast<T>() : null;
    }

    public static string Describe(GameObject go, int depth = 2)
    {
        var sb = new StringBuilder();
        Walk(go.transform, sb, 0, depth);
        return sb.ToString();
    }

    private static void Walk(Transform t, StringBuilder sb, int indent, int depth)
    {
        var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform"));
        sb.AppendLine($"{new string(' ', indent * 2)}{t.name} [{comps}] scale={t.localScale}");
        if (indent >= depth) return;
        for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), sb, indent + 1, depth);
    }
}
