# Moon Sweepers Mod Loader

A mod loader for **Moon Sweepers** (Unity 6000.6.0f1, IL2CPP metadata v108), built on BepInEx 6.

Players run a one-click installer. It adds a **MODS** entry to the game's main menu, with a screen where they add,
enable and disable mods, RimWorld-style. Mods are separate downloads that players drop into `<game>\Mods`, or add
in-game with **ADD MOD**. This repo holds the loader, the installer pipeline, and example mods to build from.

## How it works

| Piece | Where | What it does |
|---|---|---|
| Installer | [`installer/`](installer/), built by [`tools/build-installer.ps1`](tools/build-installer.ps1) | Installs BepInEx (patched for this game) and the mod loader, and generates the game's interop assemblies on the player's machine |
| Startup patcher | [`src/ModLoader.Patcher`](src/ModLoader.Patcher) → `BepInEx\patchers` | Runs before any plugin loads. It mirrors the mods enabled in-game from `<game>\Mods` into `BepInEx\plugins\MoonSweepMods.ModLoader.Active`, so BepInEx loads exactly those |
| Mods screen | [`src/ModLoader`](src/ModLoader) → `BepInEx\plugins` | Adds MODS to the main menu and the Mods screen, built from the game's own UI kit (`MoonSimpleKit`) |
| Shared code | [`src/ModLoader.Shared`](src/ModLoader.Shared) | Mod discovery (`mod.json`, `[BepInPlugin]`), enable state, paths. Compiled into both projects |
| Example mods | [`examples/`](examples/) | Mods built on the loader. Each is packaged as its own zip (see [Example mods](#example-mods)) |
| ModelKit | [`shared/ModelKit`](shared/ModelKit) | Engine-independent procedural 3D models, shared by mods and the offline previewer (see [Making models](#making-models)) |

The Mods screen:

- **Lists:** DISABLED and ENABLED columns. Click a mod to select it; double-click it or use its arrow to move it.
- **Details:** a picture, name, version, author, description, and load status (running / failed to load / changes
  pending).
- **Buttons:** ADD MOD (a Windows file picker for `.zip`/`.dll`), OPEN MODS FOLDER, and RESTART TO APPLY (the game's
  own confirmation dialog, then a relaunch through Steam).
- **Changes need a restart:** BepInEx can't unload plugins. The screen and the main-menu entry say when changes are
  waiting.
- **Main menu only:** that's the only place mods can be changed.

State lives in `BepInEx\config\MoonSweepMods.ModLoader.mods.json`, and new mods start disabled. Copying enabled mods
at startup (instead of loading them straight from `Mods\`) keeps `Mods\` unlocked while the game runs, so players
can add or update mods at any time.

### Why the patched BepInEx

Stock BepInEx doesn't work with this game yet:

1. BepInEx's bundled Cpp2IL only reads IL2CPP metadata up to v106; Moon Sweepers uses v108.
2. Il2CppInterop has no struct definition for Unity 6000.6's native `Il2CppClass` layout, so it crashes on startup
   (`Base class Il2CppSystem.Object is value type and can't be inherited from`).

[`tools/build-tools.ps1`](tools/build-tools.ps1) builds a current Cpp2IL and a patched Il2CppInterop that includes
[`tools/il2cppinterop/Class_108_0.cs`](tools/il2cppinterop/Class_108_0.cs). That class layout was recovered from the
game's exported `il2cpp_class_*` functions and checked against live game data.

## Developer setup (once, and again after every game update)

Requirements: Windows, git, and Moon Sweepers installed through Steam. The .NET SDKs the tools need are installed into
`.tools/` if missing, so nothing is installed system-wide.

```powershell
# Close the game first. The game folder is found through Steam; pass -GameDir to override.
powershell -ExecutionPolicy Bypass -File .\tools\setup-modding.ps1
dotnet build -c Release
```

`setup-modding.ps1` builds the tools under `.tools/` (git-ignored). Then it runs the same installer players use
([`installer/scripts/install.ps1`](installer/scripts/install.ps1)), which:

- installs BepInEx Bleeding Edge build 791 (IL2CPP x64)
- copies the patched Il2CppInterop into `BepInEx\core`
- generates the interop assemblies into `BepInEx\interop`. This only happens when `GameAssembly.dll` or the tools
  changed; pass `-ForceRegenerate` to force it.
- sets `UpdateInteropAssemblies = false` (BepInEx's own generator fails on this game)

`dotnet build` then deploys the loader and the example mods into the game. After a game update, re-run setup with
`-SkipBuild`.

## Building mods

Each project sets `IsModProject` and a `ModKind`, which decides where `dotnet build` deploys it
(see [`Directory.Build.props`](Directory.Build.props)):

| `ModKind` | Deploys to | Used by |
|---|---|---|
| `Mod` (default) | `<game>\Mods\<ProjectName>\` | Mods, for example [`examples/HelloWorld`](examples/HelloWorld) |
| `LoaderPlugin` | `<game>\BepInEx\plugins\<AssemblyName>\` | `src/ModLoader` |
| `LoaderPatcher` | `<game>\BepInEx\patchers\<AssemblyName>\` | `src/ModLoader.Patcher` |
| `DevPlugin` | `<game>\BepInEx\plugins\<AssemblyName>\` | `tools/DevHarness` (never shipped) |

Pass `-p:DeployToGame=false` to build without deploying, or `-p:GameDir="D:\...\Moon Sweepers"` for another game
folder. The log is at `<game>\BepInEx\LogOutput.log`.

### Creating a mod

1. Copy `examples/HelloWorld` to `examples/<YourMod>` (or anywhere in the repo). Rename the `.csproj`, and change
   `AssemblyName` and the plugin `Guid`/`Name`.
2. Edit `package/mod.json` and replace `package/preview.png`. Everything in `package/` ships next to the DLL.
3. `dotnet sln add examples/<YourMod>/<YourMod>.csproj`, then `dotnet build -c Release`.
4. In the game, open MODS, enable it, and restart.

A mod is a BepInEx 6 IL2CPP plugin (`BasePlugin` with `[BepInPlugin]`). Its folder in `Mods\` can contain:

```text
Mods\YourMod\
  YourMod.dll        one or more DLLs (BepInEx finds plugins in any of them)
  mod.json           optional: name, version, author, description, preview, website, id
  preview.png        optional: picture shown in the Mods screen (16:9 looks best; preview.jpg / icon.png also work)
```

```json
{
  "name": "Your Mod",
  "version": "1.0.0",
  "author": "You",
  "description": "What it does. Newlines (\\n) are kept."
}
```

Without `mod.json`, the name and version come from the `[BepInPlugin]` attribute.

### Example mods

| Mod | What it shows |
|---|---|
| [`HelloWorld`](examples/HelloWorld) | The minimal mod: a plugin, a custom `MonoBehaviour`, and a Harmony hook |
| [`DebugMenu`](examples/DebugMenu) | A uGUI panel in the pause menu: unlimited money, +$10k/+$1M, unlimited suit energy, fly mode, empty/fill the hopper, and spawn any catalog vehicle (including mod-added ones). It only reports a spawn as successful once the vehicle is visible and on the ground |
| [`HexHarvester`](examples/HexHarvester) | A brand-new vehicle. It's added as a new entry in the game's vehicle catalog and runs on the game's own vehicle code (driving, cleaning, hopper, unloading, lights, audio). It has an original ModelKit model and a 25 m (one hex) cleaning head. It isn't sold in the garage yet, so spawn it from the Debug Menu |

How the Hex Harvester plugs into the game, as a pattern for future vehicles:

- **Catalog entry:** a new `MoonPrototypeCatalog.Item` is appended to `MoonPrototypeCatalog.Vehicles`, so it gets its own index.
- **Base vehicle:** per-index lookups (`AuthoredPrefab`, `StorageKg`, `Heft`, `MoonVehicleAudio.Voice`) are patched to reuse a stock vehicle for physics anchors and sounds.
- **Model:** `MoonPrototypeModels.Vehicle` is patched so the stock model's renderers are hidden and the ModelKit model is attached in their place. Wheels go on the game's animated wheel transforms, and brushes become `view.rollers`.

### Making models

Models are code, built with [`shared/ModelKit`](shared/ModelKit) (boxes, tapered blocks, prisms and hex columns, cylinders, rods, spheres, radial patterns), for example [`HexHarvesterDesign.cs`](examples/HexHarvester/Design/HexHarvesterDesign.cs). The same design renders in the game, through `UnityModelBuilder`, and offline without the game:

```powershell
.\tools\preview-model.ps1 HexHarvester                                   # artifacts\previews\HexHarvester.png, 6 views, about 1 s
.\tools\preview-model.ps1 HexHarvester -View "front 3/4" -Size 1280x720  # one clean view, for example a mod's preview.png
```

The previewer culls back faces like Unity does, draws a 1 m/5 m ground grid with the cleaning width marked in orange, and adds shadows and part outlines. Put designs in `examples/<Mod>/Design/*.cs`, implementing `IModelDesign`, and the previewer picks them up automatically.

### Browsing the game's code

Open `<game>\BepInEx\interop\Assembly-CSharp.dll` in [ILSpy](https://github.com/icsharpcode/ILSpy) or
[dnSpyEx](https://github.com/dnSpyEx/dnSpy) to see the game's classes, fields, and method signatures. Most gameplay
code is in the `MoonCleaners` namespace (for example `MoonMainMenu`, `MoonSimpleKit` and `MoonSteamLobby`). Method
bodies are native code, so the interop DLLs only show signatures. To understand what a method does, hook it with
Harmony and log, or poke at it live with the dev harness.

### Dev harness (testing in the running game)

[`tools/DevHarness`](tools/DevHarness) is a dev-only plugin that lets scripts drive the game:

```powershell
.\tools\dev-relaunch.ps1                     # close the game, build + deploy everything, relaunch, wait for the menu
.\tools\dev-relaunch.ps1 -LoadSlot 1         # ...and load save slot 1
.\tools\dev.ps1 "dump menu.home 2"           # UI hierarchy with positions, colors, text
.\tools\dev.ps1 "click menu.home.MODS" "shot C:\temp\mods.png"
.\tools\dev.ps1 "get menu.kit.Orange" "call menu.Back"
.\tools\dev.ps1 "get find:MoonPrototypeVehicle.CleaningWidth"   # find:<Type>[#n] finds live game objects
```

Expressions start at `menu` (the `MoonMainMenu`), `find:Type`, `/Path/To/GameObject`, or `static:Full.Type.Name:Member`. `set` assigns values. See the comment at the top of [`Harness.cs`](tools/DevHarness/Harness.cs).

Logging: the installer sets BepInEx to log every level, including Unity's own log, to `<game>\BepInEx\LogOutput.log`.

## Releasing

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-installer.ps1 -Version 0.2.0   # -SkipBuild reuses built tools
```

This writes:

- **`artifacts\MoonSweepMods-<Version>.zip` (about 80 MB):** the installer. Players extract it and double-click
  `Install.bat`. They don't need .NET, git, or this repo. Player instructions are in
  [`installer/README.txt`](installer/README.txt).
- **`artifacts\mods\<Mod>-<version>.zip`:** one drop-in zip per project in `examples/`. Players add it with ADD MOD.

The installer:

- finds the game through Steam (or asks for the folder)
- installs BepInEx, the patched Il2CppInterop runtime and the mod loader, and creates `<game>\Mods`
- downloads Unity's base libraries and generates the interop assemblies from the player's own copy of the game,
  using self-contained Cpp2IL and Il2CppInterop exes bundled in the zip (about 20 seconds)

Re-running `Install.bat` is safe and quick. It only regenerates when the game or the tools changed, so players re-run
it after game updates. `Uninstall.bat` restores the vanilla game and keeps the player's `Mods` folder.

| Path in the installer zip | Contents |
|---|---|
| `Install.bat`, `Uninstall.bat`, `scripts\` | Installer (PowerShell, launched with `-ExecutionPolicy Bypass`) |
| `payload\BepInEx.zip` | Pinned BepInEx build |
| `payload\core\` | Patched Il2CppInterop runtime |
| `payload\tools\` | Self-contained `Cpp2IL.exe` and `Il2CppInterop.CLI.exe` (trimmed) |
| `payload\plugins\`, `payload\patchers\` | The mod loader |
| `THIRD-PARTY-NOTICES.txt`, `licenses\` | License notices (Il2CppInterop is LGPL, so the patch source ships with it) |

Game files and anything generated from them (`BepInEx\interop`, dummy assemblies, Unity libraries) are never put in
the zips, and must never be redistributed. Releases can't be built in CI, because compiling needs interop assemblies
generated from a local copy of the game.

For multiplayer, everyone in a session should have the same mods enabled.

## Known limitations

- **Restart to apply:** BepInEx can't unload plugins, so enabling or disabling mods takes effect after a restart.
- **Load order:** BepInEx orders plugins by their `[BepInDependency]` declarations; the Mods screen doesn't reorder.
- **Interface injection:** Unity 6000.6 merged `implementedInterfaces` into the interface-offsets table, so
  `ClassInjector` can't yet register injected types that implement Il2Cpp interfaces. Plain `MonoBehaviour`s,
  delegates, and Harmony patches work. For UI hover and click, use the game's `MoonWorkshopHover` component or
  `Button.onClick`, as the Mods screen does.
- **Stripped Unity APIs:** some Unity methods are stripped from the game. Examples are `ColorUtility.ToHtmlStringRGBA`,
  `ScreenCapture.CaptureScreenshot(string)`, and all of IMGUI's controls (`GUI.Button`, `GUI.Box`, ...). They throw
  `Method unstripping failed` or `MissingMethodException`. Use uGUI (`Canvas`, `Button`, TextMeshPro) for UI.
- **Spawned vehicle models:** `MoonPrototypeModels.Vehicle()` returns the model inactive. Activate it before
  `MoonPrototypeVehicle.Initialize`, or the vehicle has physics but no visible body or wheel contacts.
- **Log warnings:** `Class::Init signatures have been exhausted, using a substitute!` and "Interop assemblies are
  possibly out of date" appear in the log. Both are harmless.
- **Pinned versions:** BepInEx build 791, Cpp2IL `b5ad444` and Il2CppInterop `81a6f78`. Newer upstream releases may
  make parts of this unnecessary.

## Uninstalling (developer)

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\uninstall-bepinex.ps1   # -Force skips the confirmation
```

This removes BepInEx and the mod loader, keeping `<game>\Mods`.

## Licensing notes

`tools/il2cppinterop/Class_108_0.cs` is derived from [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop),
which is LGPL-3.0, and the file stays under that license.
