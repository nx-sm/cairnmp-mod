using MelonLoader;

namespace CairnMultiplayerMod.Internal;

internal static class ModConfig
{
    private static readonly string CategoryPlayer = "Player";
    private static readonly string CategoryRoom = "Room";
    private static readonly string CategoryKeybinds = "Keybinds";
    private static readonly string CategoryDebug = "Debug";
    private static readonly string CategoryDirect = "DirectConnect";

    public static MelonPreferences_Entry<string> PlayerName;

    public static MelonPreferences_Entry<string> RoomCode;
    public static MelonPreferences_Entry<int> MaxPlayers;

    public static MelonPreferences_Entry<int> DirectPort;
    public static MelonPreferences_Entry<string> DirectPassword;
    public static MelonPreferences_Entry<int> DirectReconnectSeconds;

    public static MelonPreferences_Entry<int> CoopSaveSlot;

    public static MelonPreferences_Entry<bool> FreeViewButton;

    public static MelonPreferences_Entry<string> ConnectKey;
    public static MelonPreferences_Entry<string> DisconnectKey;

    public static MelonPreferences_Entry<bool> VerboseLogging;
    public static MelonPreferences_Entry<bool> PerformanceDiagnostics;
    public static MelonPreferences_Entry<bool> NativeProfiling;
    public static MelonPreferences_Entry<int> JobWorkerCount;
    public static MelonPreferences_Entry<string> PerformanceScenario;

    public static void Register()
    {
        var player = MelonPreferences.CreateCategory(CategoryPlayer);
        PlayerName = player.CreateEntry("Name", "Player");

        var room = MelonPreferences.CreateCategory(CategoryRoom);
        RoomCode = room.CreateEntry("Code", "");
        MaxPlayers = room.CreateEntry("MaxPlayers", 8);

        // Direct connect (no Steam): the host opens this UDP port; guests type "address:port".
        var direct = MelonPreferences.CreateCategory(CategoryDirect);
        DirectPort = direct.CreateEntry("Port", 27420);
        // Empty: anyone who can reach the port may join. Both sides must set the same value.
        DirectPassword = direct.CreateEntry("Password", "");
        // A connection lost without a goodbye: guests redial, hosts keep the seat, this long.
        DirectReconnectSeconds = direct.CreateEntry("ReconnectSeconds", 300);

        // The story slot this machine's rope-team run lives in, so every launch from a lobby
        // continues it. -1: none yet — the next launch starts one in a free slot.
        var coop = MelonPreferences.CreateCategory("CoopSave");
        CoopSaveSlot = coop.CreateEntry("Slot", -1);

        // Moves the game's own View-button actions to the Menu button, so View + button
        // shortcuts no longer open a game menu too. false: leave the game's bindings alone.
        var controller = MelonPreferences.CreateCategory("Controller");
        FreeViewButton = controller.CreateEntry("FreeViewButton", true);

        var keybinds = MelonPreferences.CreateCategory(CategoryKeybinds);
        ConnectKey = keybinds.CreateEntry("ConnectKey", "F5");
        DisconnectKey = keybinds.CreateEntry("DisconnectKey", "F6");

        var debug = MelonPreferences.CreateCategory(CategoryDebug);
        VerboseLogging = debug.CreateEntry("VerboseLogging", false);
        PerformanceDiagnostics = debug.CreateEntry("PerformanceDiagnostics", false);
        // Times the native methods flagged by the reverse-engineering audit. Adds Harmony
        // hooks to per-frame game code, so it stays off unless someone is measuring.
        NativeProfiling = debug.CreateEntry("NativeProfiling", false);
        // Unity sizes its job worker pool from the core count. On machines with many threads
        // the wake-up traffic can cost more than the parallelism returns — 0 leaves the engine
        // alone, any other value is an experiment measured by the [GameTuning] frame report.
        JobWorkerCount = debug.CreateEntry("JobWorkerCount", 0);
        PerformanceScenario = debug.CreateEntry("PerformanceScenario", "unspecified - set route, save, run number and voice/rope state");
    }
}
