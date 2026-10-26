# MSML: Moon Sweepers Mod Loader

![example of mod loader in-game](docs/images/mod_loader_example.png)

## Install, update or uninstall

> [!CAUTION]
> DO NOT TRUST RANDOM SCRIPTS ON THE INTERNET! While I know there is nothing nefarious here, you DON'T! Please do your due diligence and review any scripts before running them on your machine.

Close Moon Sweepers, open PowerShell, and paste:

```powershell
irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1 | iex
```

> [!WARNING]
> MSML and its mods are unofficial, experimental and only lightly tested. They aren't made or supported by the developers of Moon Sweepers. They could crash the game, corrupt your saves or your moon, or stop working after a game update. **Back up your saves** (`%USERPROFILE%\AppData\LocalLow\DragDropGames\MoonSweepers`) before using them. You use MSML and any mods at your own risk; we aren't responsible for lost or damaged saves.

### Adding mods

MSML doesn't include any mods. Ready-made ones are in [`mods/`](mods). To add one:
- Download the mod's `.zip` (from [`mods/`](mods) or anywhere else)
- In the main menu choose `MODS > ADD MOD`
- Select the mod

Or you can extract it into `<game>\Mods\`. Restart the game to load it.

### Official mods

| Mod | Description | |
|---|---|---|
| **MoonSweeper+** | Quality-of-life fixes, each with its own setting. So far: the vehicle camera stays where you point it instead of snapping back behind the vehicle (auto recenter can be turned back on, with your own delay). | [`mods/`](mods) |
| **Hex Harvester** | A new vehicle, sold in the garage for $25,000 (adjustable in SETTINGS): a six-wheeled harvester with a 25 m sweeper head and a 9,000 kg silo that clears a whole hex in one pass. It's delivered in front of the garage, since it doesn't fit in the hall. | [`mods/`](mods) |

Mod settings can be changed in the MODS screen (select the mod, then **SETTINGS**) and, for mods that support it, in the pause menu under **MOD SETTINGS**.

Modded vehicles are saved so that removing the mod never breaks a save: a bought Hex Harvester just disappears until the mod is back. In multiplayer, everyone in the crew needs the same vehicle mods.

### Example mods

| Mod | Version | Description | |
|---|---|---|---|
| **Debug Menu** | 0.1.0 | Testing tools. In-game, open the pause menu (Esc) and a DEBUG panel appears on the right (F7 hides or shows it). | [Download](https://github.com/Wehrum/MSML/raw/main/mods/DebugMenu-0.1.0.zip) |
| **Hello World** | 0.1.0 | A tiny example mod. It shows a "Hello World mod loaded" label in the top-left corner for 30 seconds after the game starts, and logs a message whenever the main menu opens. | [Download](https://github.com/Wehrum/MSML/raw/main/mods/HelloWorld-0.1.0.zip) |

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

### Mod settings (MSML API)

Declare settings the normal BepInEx way. Every loaded mod's settings show up in the MODS screen (select the mod, then SETTINGS) with no extra code: OFF / ON for `bool`, - / + for numbers (bounded by an `AcceptableValueRange`), and a choice per option for enums and `AcceptableValueList`. Changes save to the mod's `.cfg` straight away and raise `SettingChanged`.

To use the API, add `<UseMsmlApi>true</UseMsmlApi>` to the `.csproj`. `MSML.Api.dll` ships with the loader, so it's never copied into your mod. With it you can:

```csharp
using MSML;

public override void Load()
{
    _delay = Config.Bind("Camera", "RecenterDelay", 5f, new ConfigDescription(
        "Seconds before the camera swings back.",
        new AcceptableValueRange<float>(1.2f, 30f),
        ModSettings.Step(0.5)));                   // - / + change it by 0.5
    Config.Bind("Debug", "Internal", 0, new ConfigDescription("Not for players.", null, ModSettings.Hidden));

    ModSettings.ShowInGame(this);                  // also list them in the pause menu (MOD SETTINGS)
}
```

Only use `ShowInGame` for settings that take effect immediately. [`official/MoonSweeperPlus`](official/MoonSweeperPlus) is a complete example.

### New vehicles (MSML API)

`Vehicles.Register` adds a vehicle to the game's catalog. It runs on the game's own vehicle code (driving, cleaning, hopper, unloading, lights, sounds), built on a stock base vehicle whose prefab, physics anchors and sounds it uses. Give it a price and it's sold in the garage, with your picture on its card:

```csharp
Vehicles.Register(new ModVehicle("yourmod.bigsweeper", "Big sweeper", MoonPrototypeCatalog.MegaVacuumRover)
{
    CleaningWidth = 8f,          // metres (defaults are the base vehicle's)
    HopperKg = 5000,
    Price = 12000,               // sold in the garage; null = not sold
    Summary = "One line for the garage card.",
    Icon = "garage.png",         // 512x512 PNG with a transparent background, next to your DLL
    BuildModel = (model, kit) => { /* change or replace the base vehicle's model */ },
});
```

Bought modded vehicles are saved by id in records the unmodded game ignores, so saves never break when a mod is removed. Also available: `Heft`, `Speed`, `Turn`, `Cargo`, `Ready` (called when one is set up in the world), `HideDriver` (for closed cabs) and `DeliverOutsideGarage` (for vehicles too big for the garage hall). [`official/HexHarvester`](official/HexHarvester) is a complete example, and [`tools/scripts/render-icon.cs`](tools/scripts/render-icon.cs) renders a garage picture from the in-game model.

### Testing without restarting

[`tools\cs.ps1`](tools/cs.ps1) runs C# inside the running game (compiled on the fly), so you can poke at game code, spawn and drive vehicles, or try a Harmony patch in seconds. See [`tools/scripts`](tools/scripts).

### Making models

Models are code, built with [`shared/ModelKit`](shared/ModelKit) (boxes, tapered blocks, prisms and hex columns, cylinders, rods, spheres, radial patterns), for example [`HexHarvesterDesign.cs`](official/HexHarvester/Design/HexHarvesterDesign.cs). The same design renders in the game, through `UnityModelBuilder`, and offline without the game:

```powershell
.\tools\preview-model.ps1 HexHarvester                                   # artifacts\previews\HexHarvester.png, 6 views, about 1 s
.\tools\preview-model.ps1 HexHarvester -View "front 3/4" -Size 1280x720  # one clean view, for example a mod's preview.png
```

The previewer culls back faces like Unity does, draws a 1 m/5 m ground grid with the cleaning width marked in orange, and adds shadows and part outlines. Put designs in `official/<Mod>/Design/*.cs` or `examples/<Mod>/Design/*.cs`, implementing `IModelDesign`, and the previewer picks them up automatically.

## Licensing notes

`tools/il2cppinterop/Class_108_0.cs` is derived from [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop),
which is LGPL-3.0, and the file stays under that license.
