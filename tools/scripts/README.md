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
  `Find<T>()`, `FindAll<T>()`, `Get("find:Type.member")`, `Dump(obj)`, `Describe(obj)`, `Teleport`, `Ahead(m)`,
  `Ground(p)`, `VehicleIndex(name)`, `Spawn(name)`, `Board(v)`, `Leave()`, `Driving`, `Drive(t, s, secs)`,
  `Click("find:...")`, `Shot(name)`, `WaitFor(cond)`.

## Known limits

- Patching `MoonPrototypeCatalog.Heft` mid-game crashes the runtime (patching it at startup, as mods do, is fine).
  Other methods tested so far patch and unpatch cleanly.
- Injected Unity classes (`ClassInjector`, custom `MonoBehaviour`s) can't be redefined once registered; use
  `ctx.OnUpdate` instead.
- Changing the DevHarness itself still needs a restart (`tools\dev-relaunch.ps1`).
