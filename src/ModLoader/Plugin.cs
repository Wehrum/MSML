using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MoonSweepMods.ModLoader;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "moonsweepmods.modloader";
    public const string Name = "Mod Loader";
    public const string Version = "0.2.0";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = Log;
        AddComponent<ModLoaderBehaviour>();
        new Harmony(Guid).PatchAll(typeof(MenuHooks));
        new Harmony(Guid + ".pause").PatchAll(typeof(PauseSettings.Hooks));
        MSML.Crew.JoinCheck.Apply();
        VersionCheck.Start();
        Log.LogInfo($"Mod Loader {Version}: mods folder is {ModPaths.ModsDir}");
    }
}

/// <summary>Per-frame work that has to happen on Unity's main thread.</summary>
public class ModLoaderBehaviour : MonoBehaviour
{
    public ModLoaderBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        try
        {
            ModActions.PollFilePicker();
            Disclaimer.Tick(ModsScreen.Menu, ModsScreen.MenuSeenAt);
            if (ModsScreen.IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && !ModsScreen.DialogOpen)
                ModsScreen.Back();
            PauseSettings.Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }
}

internal static class MenuHooks
{
    [HarmonyPatch(typeof(MoonMainMenu), nameof(MoonMainMenu.BuildHome))]
    [HarmonyPostfix]
    private static void AfterBuildHome(MoonMainMenu __instance)
    {
        try { ModsScreen.AddMenuEntry(__instance); }
        catch (Exception e) { Plugin.Logger.LogError("Couldn't add MODS to the main menu: " + e); }
    }

    // While the Mods screen is open the home rows are hidden, but the menu still steers them with the keyboard.
    [HarmonyPatch(typeof(MoonMainMenu), nameof(MoonMainMenu.SteerHome))]
    [HarmonyPrefix]
    private static bool BeforeSteerHome() => !ModsScreen.IsOpen;

    [HarmonyPatch(typeof(MoonMainMenu), nameof(MoonMainMenu.Back))]
    [HarmonyPrefix]
    private static bool BeforeBack()
    {
        if (!ModsScreen.IsOpen) return true;
        if (!ModsScreen.DialogOpen) ModsScreen.Back();
        return false;
    }

    [HarmonyPatch(typeof(MoonMainMenu), nameof(MoonMainMenu.OnDestroy))]
    [HarmonyPostfix]
    private static void AfterDestroy(MoonMainMenu __instance) => ModsScreen.Forget(__instance);
}
