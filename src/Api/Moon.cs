using System;
using HarmonyLib;
using MoonCleaners;

namespace MSML;

/// <summary>The moon being played.</summary>
public static class Moon
{
    private static MoonWorld? _world;
    private static Action? _update;
    private static bool _patched;
    private static float _errorAt = -999f;

    /// <summary>The moon's world while on a moon, else null.</summary>
    public static MoonWorld? World => _world != null && !_world.WasCollected ? _world : null;

    /// <summary>
    /// The first active object of a type in the scene, or null (Unity's <c>FindObjectOfType</c> is stripped from the
    /// game). It searches the whole scene, so cache the result.
    /// </summary>
    public static T? Find<T>() where T : UnityEngine.Object
    {
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), UnityEngine.FindObjectsInactive.Exclude, UnityEngine.FindObjectsSortMode.None))
            if (o != null && o.TryCast<T>() is { } found) return found;
        return null;
    }

    /// <summary>Raised every frame while on a moon, on the main thread, before the world's own update.</summary>
    public static event Action? Update
    {
        add { _update += value; Apply(); }
        remove => _update -= value;
    }

    internal static void Apply()
    {
        if (_patched) return;
        _patched = true;
        new Harmony("msml.api.moon").PatchAll(typeof(Moon));
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.Update))]
    [HarmonyPrefix]
    private static void BeforeWorldUpdate(MoonWorld __instance)
    {
        _world = __instance;
        if (_update == null) return;
        foreach (var handler in _update.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception e)
            {
                if (UnityEngine.Time.unscaledTime - _errorAt < 10f) continue;
                _errorAt = UnityEngine.Time.unscaledTime;
                Log.Error("A Moon.Update handler failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterWorldDestroyed(MoonWorld __instance)
    {
        if (_world != null && _world.Pointer == __instance.Pointer) _world = null;
    }
}
