using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DungeonLurkerCoop;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.doubtfulhermit.dungeonlurker.coop";
    public const string Name = "DungeonLurkerCoop";
    public const string Version = "0.1.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> CoopEnabled;
    internal static ConfigEntry<bool> SpawnWithoutGamepad;
    internal static ConfigEntry<bool> TintP2;
    internal static ConfigEntry<float> P2HueShift;
    internal static ConfigEntry<bool> DebugAutoDrive;
    internal static ConfigEntry<bool> DebugAutoStart;
    internal static ConfigEntry<float> DebugScreenshotInterval;
    internal static ConfigEntry<string> DebugScreenshotDir;
    internal static ConfigEntry<bool> DebugDeathTest;
    internal static ConfigEntry<bool> BibleDump;
    internal static ConfigEntry<string> BibleDumpDir;

    private void Awake()
    {
        Log = Logger;
        CoopEnabled = Config.Bind("General", "Enabled", true,
            "Enable couch co-op. P2 spawns when a free gamepad is present.");
        SpawnWithoutGamepad = Config.Bind("General", "SpawnWithoutGamepad", false,
            "Spawn P2 even when no gamepad is connected (for testing).");
        TintP2 = Config.Bind("General", "TintP2", true,
            "Recolor player 2 via palette hue rotation so the players are distinguishable.");
        P2HueShift = Config.Bind("General", "P2HueShift", 150f,
            "Degrees to rotate P2's palette hues (0-360). 150 turns the blue knight warm red/orange.");
        DebugAutoDrive = Config.Bind("Debug", "AutoDrive", false,
            "Debug: drive both players with in-engine input for testing.");
        DebugAutoStart = Config.Bind("Debug", "AutoStart", false,
            "Debug: automatically progress the splash menu into a run.");
        DebugScreenshotInterval = Config.Bind("Debug", "ScreenshotInterval", 0f,
            "Debug: capture a screenshot every N seconds (0 = off).");
        DebugScreenshotDir = Config.Bind("Debug", "ScreenshotDir", "",
            "Debug: directory for periodic screenshots.");
        DebugDeathTest = Config.Bind("Debug", "DeathTest", false,
            "Debug: scripted kill/revive/game-over verification sequence.");
        BibleDump = Config.Bind("Bible", "Dump", false,
            "Dump all game data (boons, spells, items, enemies) to JSON + icons.");
        BibleDumpDir = Config.Bind("Bible", "DumpDir", "Z:/home/ttanurhan/projects/DungeonLurkerCoop/bible/dump",
            "Output directory for the bible dump.");

        var harmony = new Harmony(Guid);
        harmony.PatchAll();

        CoopManager.Bootstrap();
        TestHarness.Bootstrap();
        BibleDumper.Bootstrap();
        Log.LogInfo($"{Name} {Version} loaded. Co-op enabled: {CoopEnabled.Value}");
    }
}
