# MSML: Moon Sweepers Mod Loader

![example of mod loader in-game](docs/images/mod_loader_example.png)

## Install, update or uninstall

Close Moon Sweepers, open PowerShell, and paste:

```powershell
irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1 | iex
```

That's the only command you need. It shows what's installed and asks what to do: **Install**, or **Update /
Reinstall / Uninstall** if MSML is already installed. Run it again after a Moon Sweepers update to refresh MSML.
Your mods and settings are kept, including on uninstall (your `Mods` folder stays).

### Adding mods

MSML doesn't include any mods. To add one, download the mod's `.zip`, then in the main menu choose **MODS > ADD MOD**
and pick it, or extract it into `<game>\Mods\`. Restart the game to load it (**RESTART TO APPLY**).

MSML adds a **MODS** entry to the game's main menu, with a screen where players add, enable and disable mods,
RimWorld-style. Mods are separate downloads that players drop into `<game>\Mods`, or add in-game with **ADD MOD**.
This repo holds the loader, the installer pipeline, and example mods to build from.

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

## Licensing notes

`tools/il2cppinterop/Class_108_0.cs` is derived from [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop),
which is LGPL-3.0, and the file stays under that license.
