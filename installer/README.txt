MOON SWEEPERS MOD LOADER
========================

This adds mod support to Moon Sweepers: a MODS option in the main menu where you add, enable and disable mods.
It doesn't change the game by itself.

Requirements
------------
- Windows 10 or 11 (64-bit)
- Moon Sweepers installed through Steam
- An internet connection the first time you install (it downloads a few MB of Unity files)
Nothing else is needed. You don't need .NET, git, or any developer tools.

Install
-------
1. Close Moon Sweepers.
2. Extract this zip anywhere, for example your Downloads folder.
3. Double-click Install.bat.
   - If Windows shows a security warning, click "Run" (or "More info" > "Run anyway").
   - The installer finds the game through Steam. If it can't, it asks you for the game folder
     (in Steam: right-click Moon Sweepers > Manage > Browse local files).
   - The first install takes under a minute while it reads your copy of the game.
4. Launch Moon Sweepers from Steam as usual. A console window opens next to the game; that's the mod loader.

Adding and managing mods
------------------------
In the main menu, choose MODS.
- ADD MOD: pick a mod's .zip (or .dll) file. It's copied into the Mods folder and enabled.
- Click a mod to see its description and picture. Double-click it, or use the arrow, to move it between
  DISABLED and ENABLED.
- Changes apply when the game restarts. Use RESTART TO APPLY, or just restart the game later.
- OPEN MODS FOLDER: mods live in <game folder>\Mods. You can also extract mods there yourself (one folder per mod).
  New mods start disabled; enable them in the MODS screen.

Only install mods from people you trust. Mods are programs that run with the game.

After a game update
-------------------
Run Install.bat again. The mod loader is built against your exact game version, so it needs refreshing after each
update. Re-running is safe and quick when nothing has changed, and your mods and settings are kept.

Multiplayer
-----------
Everyone in a session should have the same mods enabled.

Uninstall
---------
Close the game and double-click Uninstall.bat. This removes the mod loader and the game goes back to normal.
Your Mods folder is kept; delete it if you don't need it. (Steam's "Verify integrity of game files" does NOT remove
the mod loader.)

Troubleshooting
---------------
- Mod loader log:  <game folder>\BepInEx\LogOutput.log
- Installer log:   <game folder>\BepInEx\MoonSweepMods-install.log
- If a mod shows "FAILED TO LOAD" in the MODS screen, its error is in the mod loader log.
- Some antivirus programs flag winhttp.dll in the game folder. That file is how BepInEx loads into the game.
  You may need to allow it.

See THIRD-PARTY-NOTICES.txt for the open-source components included in this pack.
