using System;
using System.IO;
using BepInEx;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.ModLoader;

/// <summary>
/// The "experimental, use at your own risk" notice. Shown the first time the main menu appears with MSML installed,
/// and before opening MODS until the player accepts. Acceptance is remembered (bump <see cref="Revision"/> to show it
/// again after changing the text).
/// </summary>
internal static class Disclaimer
{
    private const int Revision = 1;

    public const string Title = "Use mods at your own risk";

    public const string Message =
        "MSML and its mods are unofficial and experimental. They aren't made or supported by the developers of Moon Sweepers, " +
        "and they haven't been tested in every situation. They could crash the game, corrupt your saves or your moon, " +
        "or stop working after a game update. Back up your saves before playing with mods " +
        "(%USERPROFILE%\\AppData\\LocalLow\\DragDropGames\\MoonSweepers). By continuing, you accept all risk.";

    private static string StateFile => Path.Combine(Paths.ConfigPath, "MoonSweepMods.ModLoader.disclaimer.txt");

    private static bool? _accepted;
    private static bool _shownThisSession;

    public static bool Accepted
    {
        get
        {
            if (_accepted == null)
            {
                try { _accepted = File.Exists(StateFile) && File.ReadAllText(StateFile).Trim() == Revision.ToString(); }
                catch { _accepted = false; }
            }
            return _accepted.Value;
        }
    }

    private static void Accept()
    {
        _accepted = true;
        try { File.WriteAllText(StateFile, Revision.ToString()); }
        catch (Exception e) { Plugin.Logger.LogWarning("Couldn't save the disclaimer choice: " + e.Message); }
        Plugin.Logger.LogInfo("Mod disclaimer accepted.");
    }

    /// <summary>Shows the notice; <paramref name="then"/> runs after the player accepts. Declining quits the game.</summary>
    public static void Show(MoonSimpleDialog dialog, Action? then)
    {
        _shownThisSession = true;
        dialog.Show(Title, Message, "I UNDERSTAND", Act(() =>
        {
            dialog.Hide();
            Accept();
            then?.Invoke();
        }), "QUIT GAME", Act(() =>
        {
            dialog.Hide();
            Plugin.Logger.LogInfo("Mod disclaimer declined; quitting.");
            Application.Quit();
        }));
    }

    /// <summary>Called every frame: shows the notice once at the main menu after the studio intro.</summary>
    public static void Tick(MoonMainMenu? menu, float menuSeenAt)
    {
        if (Accepted || _shownThisSession || menu == null || menu.WasCollected || !menu) return;
        if (Time.unscaledTime - menuSeenAt < 4f) return;   // let the intro/logo animations finish
        if (menu.current != MoonMainMenu.Panel.None || menu.dialog == null || menu.dialog.Open) return;
        if (menu.home == null || !menu.home.gameObject.activeInHierarchy) return;
        Show(menu.dialog, null);
    }

    private static Il2CppSystem.Action Act(Action a) => Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(a)!;
}
