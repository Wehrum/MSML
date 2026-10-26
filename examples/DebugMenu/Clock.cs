using System;
using MoonCleaners;

namespace MoonSweepMods.DebugMenu;

/// <summary>
/// The lunar day clock. The game derives the time of day from a global clock:
/// LunarTime = frac((Clock + clockOffset) / lunarDaySeconds), where Clock advances at clockSpeed. So we move time by
/// shifting clockOffset, and keep the clock continuous when changing speed. "Hours" split one lunar day into 24.
/// </summary>
internal static class Clock
{
    private static MoonSolarSystem? _sky;

    private static MoonSolarSystem Sky
    {
        get
        {
            if (_sky != null && !_sky.WasCollected && _sky) return _sky;
            _sky = Game.Find<MoonSolarSystem>() ?? throw new InvalidOperationException("No sky in this scene.");
            return _sky;
        }
    }

    private static double DaySeconds => Sky.lunarDaySeconds > 0 ? Sky.lunarDaySeconds : 3600;

    public static string Describe()
    {
        try
        {
            var hours = Sky.LunarTime * 24;
            var speed = MoonSolarSystem.clockSpeed;
            var speedText = speed == 0 ? "paused" : $"x{speed:0.#}";
            return $"{(int)hours:00}:{(int)(hours % 1 * 60):00}  sun {Sky.SunHeight:0} deg  {speedText}";
        }
        catch (Exception) { return "-"; }
    }

    /// <summary>Moves the clock so LunarTime becomes <paramref name="fraction"/> (0..1).</summary>
    private static void SetFraction(double fraction)
    {
        var delta = fraction - Sky.LunarTime;
        delta -= Math.Floor(delta);                       // always forward, 0..1
        MoonSolarSystem.clockOffset += delta * DaySeconds;
    }

    public static string Nudge(float hours)
    {
        MoonSolarSystem.clockOffset += hours / 24.0 * DaySeconds;
        return $"{(hours > 0 ? "+" : "")}{hours} h -> {Describe()}";
    }

    /// <summary>
    /// Dawn, noon, dusk or night where the player is: samples the game's own sun height (degrees above the local
    /// horizon) across the lunar day and picks the matching moment. The sky updates SunHeight once per frame, so the
    /// scan takes one frame per sample (call <see cref="Tick"/> every frame); the result arrives via TakeStatus.
    /// </summary>
    public static string Preset(string which)
    {
        _scanWhich = which;
        _scanStart = MoonSolarSystem.clockOffset;
        _scanIndex = -1;
        _scanHeights = new float[ScanSteps];
        return $"Finding {which}...";
    }

    private const int ScanSteps = 48;
    private static string? _scanWhich;
    private static double _scanStart;
    private static int _scanIndex;
    private static float[] _scanHeights = Array.Empty<float>();
    private static string? _status;

    public static string? TakeStatus() { var s = _status; _status = null; return s; }

    public static void Tick()
    {
        if (_scanWhich == null) return;
        // Read the height the sky computed for the previous sample, then move to the next one.
        if (_scanIndex >= 0) _scanHeights[_scanIndex] = Sky.SunHeight;
        _scanIndex++;
        if (_scanIndex < ScanSteps)
        {
            MoonSolarSystem.clockOffset = _scanStart;
            SetFraction(_scanIndex / (double)ScanSteps);
            return;
        }

        var heights = _scanHeights;
        var which = _scanWhich;
        _scanWhich = null;
        MoonSolarSystem.clockOffset = _scanStart;

        var best = 0;
        switch (which)
        {
            case "noon":
                for (var i = 1; i < ScanSteps; i++) if (heights[i] > heights[best]) best = i;
                break;
            case "midnight":
                for (var i = 1; i < ScanSteps; i++) if (heights[i] < heights[best]) best = i;
                break;
            default:
            {
                // Dawn: sun rising through ~5 degrees. Dusk: setting through ~5 degrees.
                var rising = which == "dawn";
                for (var i = 0; i < ScanSteps; i++)
                {
                    var a = heights[i]; var b = heights[(i + 1) % ScanSteps];
                    if (rising ? a < 5 && b >= 5 : a >= 5 && b < 5) { best = (i + 1) % ScanSteps; break; }
                }
                break;
            }
        }
        SetFraction(best / (double)ScanSteps);
        Plugin.Logger.LogDebug($"Clock preset {which}: sun heights {string.Join(",", Array.ConvertAll(heights, h => h.ToString("0")))}; picked {best}/{ScanSteps}");
        _status = $"{char.ToUpper(which[0])}{which[1..]}: sun at {heights[best]:0} deg ({best * 24.0 / ScanSteps:0.#} h)";
    }

    public static string Speed(float speed)
    {
        // Changing the speed rescales elapsed time; compensate so the time of day doesn't jump.
        var before = Sky.LunarTime;
        MoonSolarSystem.clockSpeed = speed;
        SetFraction(before);
        return speed == 0 ? "Clock paused" : $"Clock speed x{speed:0.#}";
    }
}
