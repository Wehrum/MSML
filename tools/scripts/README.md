# Dev scripts

C# scripts that run **inside the running game**, compiled on the fly by the DevHarness (Roslyn), so trying something
takes seconds instead of a rebuild and restart. Use them to explore game code, reproduce bugs and test mods.

```powershell
.\tools\cs.ps1 tools\scripts\selftest.cs            # run a script (starts the game into save slot 1 if needed)
.\tools\cs.ps1 tools\scripts\patchtest.cs install   # extra words go to ctx.Args
.\tools\cs.ps1 -Code "MoonPrototypeCatalog.Vehicles.Length"   # an expression
.\tools\cs.ps1 -Code "Money = 1e6; return Money;"             # statements
```

Needs the DevHarness plugin in the game (`tools\dev-relaunch.ps1` builds and deploys it with everything else).

## Writing a script

Any class with a static `Run` method. It can return a value (printed), or be an iterator that runs over several
frames: `yield return null` waits a frame, `yield return 1.5f` waits seconds, `yield return (Func<bool>)` waits until
true, and yielding another iterator (`Board(v)`, `WaitFor(...)`) runs it first.

```csharp
public static class Script
{
    public static IEnumerator Run(ScriptContext ctx)
    {
        var v = Spawn("Push sweeper");
        yield return 1f;
        yield return Board(v);
        Drive(1, 0, 2);                       // throttle, steer, seconds
        yield return 2f;
        ctx.Log($"driving {Driving?.Spec?.name}");
        ctx.Log(Shot("after-drive"));         // BepInEx\devshots\after-drive.png
    }
}
```

Usings for `System`, `System.Linq`, collections, `UnityEngine`, `MoonCleaners`, `HarmonyLib` and the helpers are
already there. Everything in the game, its interop assemblies and loaded mods can be used.

- **Output:** `ctx.Log(...)`, plus the return value. Warnings and errors logged by anything while the script runs
  are included, so game-side failures show up.
- **Lifetime:** `ctx.Harmony` patches, `ctx.OnUpdate(...)` hooks and `ctx.OnUnload(...)` cleanups are undone when the
  same script runs again (or `dev.ps1 "unload name"`), so scripts can be edited and rerun freely.
- **Helpers** ([Dev.cs](../DevHarness/Dev.cs)): `Player`, `World`, `Equipment`, `Position`, `Money`,
  `Find<T>()`, `FindAll<T>()`, `Items<T>(il2cppCollection)` (so LINQ works on game lists and dictionaries),
  `Get("find:Type.member")`, `Dump(obj)`, `Describe(obj)`, `Teleport`, `Ahead(m)`, `Ground(p)`, `VehicleIndex(name)`,
  `Spawn(name)`, `Board(v)`, `Leave()`, `Driving`, `Drive(t, s, secs)`, `Click("find:...")`, `Shot(name)`,
  `WaitFor(cond)`.
- **Code from PowerShell:** quotes inside `-Code` get mangled by nested `powershell -File` calls; pipe the code in
  instead: `'Money = 1e6; return Money;' | powershell -File tools\cs.ps1 -Stdin`.

## Scripts here

| Script | What it does |
|---|---|
| `selftest.cs` | Checks the script host end to end (hooks, patches, log capture, spawn/board/drive). |
| `patchtest.cs` | Installs a runtime Harmony patch (`install`), for checking patching on its own. |
| `garage-probe.cs` | Lists garage offers, prices, the catalog and owned vehicles. |
| `vehicle-shop-test.cs` | Modded vehicle in the garage: `buy` (money, buy, save, screenshot), `check`, `drive`. |
| `garage-screens.cs` | Screenshots the garage's Vehicles, Upgrades and Fleet pages for a vehicle. |
| `garage-recall.cs` | Stores a bought vehicle and calls it back out: where it lands, and that it drives. |
| `drive-outside.cs` | Spawns a vehicle next to the parked push sweeper and drives it, logging speed. |
| `render-icon.cs` | Renders a vehicle's in-game model to a transparent 512x512 garage picture. |
| `harvester-shots.cs` | Close-up renders of the Hex Harvester (front, side, rear, cab, nose) and checks for wheel height, body sway and lamp positions. |
| `capsule-jam.cs` | Where conveyor capsules are stuck: follows every loaded belt to its end and reports what it feeds and how full it is. |
| `capsule-watch.cs` | Watches capsules, moving belts and processors stuck on "Could not pack a capsule" for a while. |
| `join-sim.cs` | Saves the loaded moon, then packs and unpacks it the way a crew join does, to check that the save survives the trip. |
| `join-host-sim.cs` | Runs the host's real join send (`SendWorld`) for a fake crewmate in an invite-only lobby, counting saves and messages. |
| `crew-mismatch.cs` | Shows the message a player gets when joining a crew with different mods, for several pretend hosts. |
| `tubes-test.cs` | Builds a Better Belts line (booster + tubes) next to a solar panel through the build menu's own path, then reports types, prices, power, speeds and capsule timings along it. `keep` leaves it built. |
| `tube-cards.cs` | Renders Better Belts' build-card pictures (512x512, transparent) from real pieces built out on open ground. |
| `crew-test.cs` | Multiplayer as far as one player can test it: a crewmate's harvester (lamps, silo), unknown vehicles skipped, and with `lobby` an invite-only crew to check what the join check sees. |
| `silo-test.cs` | Renders the Hex Harvester's glass silo at several fill levels (own camera and light, works at night) and lists its upgrade prices. |

## Known limits

- Occasionally the first script after a launch crashes the game's .NET runtime when it spawns or patches things.
  The harness runs a harmless warm-up script a few seconds after startup, which avoids most of it; if a script
  crashes right after a relaunch, run it again (or run `-Code "1+1"` first). Runtime Harmony patches, including on
  methods mods already patch, work otherwise.
- Injected Unity classes (`ClassInjector`, custom `MonoBehaviour`s) can't be redefined once registered; use
  `ctx.OnUpdate` instead.
- Changing the DevHarness itself still needs a restart (`tools\dev-relaunch.ps1`).
