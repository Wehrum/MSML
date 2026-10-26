using BepInEx.Configuration;

namespace MSML.BetterBelts;

internal static class Settings
{
    public static ConfigEntry<float> TubeSpeed = null!;
    public static ConfigEntry<float> FullRange = null!;
    public static ConfigEntry<float> FadeRange = null!;
    public static ConfigEntry<float> TubePriceScale = null!;
    public static ConfigEntry<int> BoosterPrice = null!;
    public static ConfigEntry<float> BoosterPower = null!;
    public static ConfigEntry<bool> ColorBlind = null!;
    public static ConfigEntry<bool> AlwaysBright = null!;

    public static void Bind(ConfigFile config)
    {
        TubeSpeed = config.Bind("Tubes", "Speed", 5f,
            new ConfigDescription("How fast capsules go in a fully pressurised tube, compared to a conveyor belt.", new AcceptableValueRange<float>(1.5f, 10f)));
        TubePriceScale = config.Bind("Tubes", "PriceScale", 10f,
            new ConfigDescription("A tube's price compared to a conveyor belt of the same length.", new AcceptableValueRange<float>(1f, 40f)));
        FullRange = config.Bind("Boosters", "FullRange", 80f,
            new ConfigDescription("How far (metres of tube) a booster keeps tubes at full speed.", new AcceptableValueRange<float>(20f, 200f)));
        FadeRange = config.Bind("Boosters", "FadeRange", 40f,
            new ConfigDescription("Over how many metres after that the pressure fades to belt speed.", new AcceptableValueRange<float>(10f, 100f)));
        BoosterPrice = config.Bind("Boosters", "Price", 250,
            new ConfigDescription("Price of a tube booster.", new AcceptableValueRange<int>(0, 5000)));
        BoosterPower = config.Bind("Boosters", "PowerKw", 3f,
            new ConfigDescription("Power a booster draws from the grid (kW). Without enough power, tubes after it run slower.", new AcceptableValueRange<float>(0f, 20f)));
        ColorBlind = config.Bind("Look", "ColorBlindColors", false,
            "Show pressure as blue (full), orange (fading) and grey (belt speed) instead of green, yellow and red.");
        AlwaysBright = config.Bind("Look", "AlwaysBright", false,
            "Keep the pressure rings bright all the time, not just while building.");

        TubeSpeed.SettingChanged += (_, _) => Pressure.Dirty();
        FullRange.SettingChanged += (_, _) => Pressure.Dirty();
        FadeRange.SettingChanged += (_, _) => Pressure.Dirty();
        BoosterPower.SettingChanged += (_, _) => Pressure.Dirty();
        ColorBlind.SettingChanged += (_, _) => Pressure.Dirty();
        TubePriceScale.SettingChanged += (_, _) => Tubes.ApplyPrices();
        BoosterPrice.SettingChanged += (_, _) => Tubes.ApplyPrices();
    }
}
