using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace MoonSweepMods.DevHarness;

/// <summary>
/// Drives the build tool like a player would, for scripts testing placement: a fake aim and clicks fed through the
/// game's own input reading, so everything after it (aiming, snapping, holograms, building) is the game's.
/// <code>
/// BuildTool.Use(ctx);                          // once: hooks the input (undone when the script ends)
/// yield return BuildTool.Choose(tube.Kind);    // build tool out, card chosen
/// yield return BuildTool.StartBelt(point);     // aim there and click until a belt is started
/// BuildTool.AimAt(other); yield return 0.4f;   // the plan follows (BuildTool.Plan(...) describes it)
/// yield return BuildTool.Click();              // builds it
/// BuildTool.Stop();                            // puts the belt away (also done when the script ends)
/// </code>
/// </summary>
public static class BuildTool
{
    private static Vector3? _aim;
    private static int _place, _back, _reads;
    private static ScriptContext? _owner;

    public static MoonDepotBuilder Builder => Dev.Find<MoonDepotBuilder>() ?? throw new InvalidOperationException("No builder (not in a game?).");

    /// <summary>Times the game read the build input since <see cref="Use"/> (0: the builder isn't taking input).</summary>
    public static int Reads => _reads;

    /// <summary>Hooks the build input for this script. Undone, with the aim given back to the mouse, when it ends.</summary>
    public static void Use(ScriptContext ctx)
    {
        if (_owner == ctx) return;
        Reset();
        _owner = ctx;
        ctx.Harmony.Patch(AccessTools.Method(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ReadInput)),
            postfix: new HarmonyMethod(typeof(BuildTool), nameof(AfterReadInput)));
        ctx.OnUnload(() =>
        {
            try { Stop(); } catch { }
            Reset();
        });
    }

    private static void Reset()
    {
        _aim = null;
        _place = _back = _reads = 0;
        _owner = null;
    }

    private static void AfterReadInput(ref MoonDepotBuilder.BuildInput __result)
    {
        _reads++;
        if (_aim is { } target && Camera.main is { } camera)
        {
            var eye = camera.transform.position;
            __result.aim = new Ray(eye, (target - eye).normalized);
        }
        if (_place > 0) { _place--; __result.place = true; }
        if (_back > 0) { _back--; __result.back = true; }
    }

    /// <summary>Aims the build tool at a world point (from the camera); null gives the aim back to the mouse.</summary>
    public static void AimAt(Vector3? point) => _aim = point;

    /// <summary>Presses place for a frame (<c>yield return BuildTool.Click()</c>).</summary>
    public static IEnumerator Click()
    {
        _place = 1;
        yield return null;
        yield return null;
    }

    /// <summary>Presses back / unselect for a frame.</summary>
    public static IEnumerator Back()
    {
        _back = 1;
        yield return null;
        yield return null;
    }

    /// <summary>Takes out the build tool and chooses a card (a structure or belt kind; belts start drawing mode).</summary>
    public static IEnumerator Choose(int kind)
    {
        var b = Builder;
        b.EndConveyor();
        b.CancelPlacement();
        Dev.Find<MoonToolbelt>()?.SelectSlot(1);
        yield return 0.3f;
        b.SelectStructure(kind);
        b.BeginPlacement();
        yield return 0.4f;
    }

    /// <summary>Aims at a point and clicks until a belt is started there (throws if the game won't, unless told not to: then check Builder.ConveyorHasStart).</summary>
    public static IEnumerator StartBelt(Vector3 at, int tries = 6, bool orThrow = true)
    {
        var b = Builder;
        AimAt(at);
        yield return 0.3f;
        for (var i = 0; i < tries && !b.ConveyorHasStart; i++)
        {
            yield return Click();
            yield return 0.25f;
        }
        if (!b.ConveyorHasStart && orThrow)
            throw new InvalidOperationException($"Couldn't start a belt at {at} (conveyor mode {b.ConveyorMode}, input reads {_reads}).");
    }

    /// <summary>The belt being drawn: its ends (relative to a point), what they snap to, and whether it can be built.</summary>
    public static string Plan(Vector3 relativeTo = default)
    {
        var b = Builder;
        var p = b.conveyorPlan;
        if (p == null || !b.ConveyorHasStart) return "no belt started";
        return $"start {p.start - relativeTo} (belt {p.startBelt}, structure {p.startStructure}) -> end {p.end - relativeTo} " +
               $"(belt {p.endBelt}, structure {p.endStructure}); {b.previewCount} piece(s), {(b.ConveyorPreviewValid ? "valid" : "not valid")}";
    }

    /// <summary>Puts the belt or structure away, gives the aim back to the mouse, and puts the build tool away.</summary>
    public static void Stop()
    {
        _aim = null;
        _place = _back = 0;
        var b = Dev.Find<MoonDepotBuilder>();
        if (b != null) { b.EndConveyor(); b.CancelPlacement(); }
        Dev.Find<MoonToolbelt>()?.SelectSlot(0);
    }
}
