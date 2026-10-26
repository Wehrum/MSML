using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.HelloWorld;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "moonsweepmods.helloworld";
    public const string Name = "Hello World";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        Log.LogInfo("Hello, Moon Sweepers!");

        // Injects a custom MonoBehaviour into the game (exercises Il2CppInterop class injection).
        AddComponent<HelloOverlay>();

        // Hooks game code (exercises Harmony patching of IL2CPP methods).
        new Harmony(Guid).PatchAll(typeof(MainMenuPatches));
    }
}

public class HelloOverlay : MonoBehaviour
{
    private float _shownUntil;
    private bool _loggedFirstDraw;

    // Il2CppInterop requires this constructor on injected types.
    public HelloOverlay(IntPtr ptr) : base(ptr) { }

    private void Start()
    {
        _shownUntil = Time.realtimeSinceStartup + 30f;
        Plugin.Logger.LogInfo("HelloOverlay component is running.");
    }

    private void OnGUI()
    {
        if (Time.realtimeSinceStartup >= _shownUntil)
            return;

        GUI.Label(new Rect(12, 12, 600, 30), $"{Plugin.Name} mod v{Plugin.Version} loaded");
        if (!_loggedFirstDraw)
        {
            _loggedFirstDraw = true;
            Plugin.Logger.LogInfo("On-screen label drawn.");
        }
    }
}

[HarmonyPatch(typeof(MoonMainMenu), "OnEnable")]
internal static class MainMenuPatches
{
    [HarmonyPostfix]
    private static void Postfix(MoonMainMenu __instance)
    {
        Plugin.Logger.LogInfo($"Main menu opened ({__instance.name}).");
    }
}
