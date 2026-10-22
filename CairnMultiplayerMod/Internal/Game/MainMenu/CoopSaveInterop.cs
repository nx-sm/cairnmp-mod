using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTheGameBakers.Cairn.Save;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// Drives the game's own story save screen: continuing a slot, or starting a new game in a
/// chosen one. Exactly what its buttons do — the screen changes the active slot, then loads
/// or walks into the new-game flow — so the game keeps every save rule of its own.
/// </summary>
internal static class CoopSaveInterop
{
    /// <summary>
    /// A story run is started on the Story channel but written to disk as a Summit save
    /// (SAVEGAMES/…/SUMMIT/SLOT_A.CPK). Looking at Story alone found no save at all, so the
    /// remembered slot looked empty and a new game was started over the run. A slot counts
    /// as taken when either channel holds it.
    /// </summary>
    private static readonly SaveType[] Channels = { SaveType.Story, SaveType.Summit };

    /// <summary>The save screen, once it is open and not busy loading previews.</summary>
    internal static bool TryGetReadySaveScreen(out MainMenuSaveManagementElement screen)
    {
        screen = null;
        try
        {
            screen = UnityEngine.Object.FindObjectOfType<MainMenuSaveManagementElement>();
            return screen != null && screen.IsShown && !screen.IsBusy;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.find-screen", exception);
            return false;
        }
    }

    internal static bool HasSave(byte slot) => SaveChannelOf(slot).HasValue;

    /// <summary>The channel holding a save in that slot, if any.</summary>
    private static SaveType? SaveChannelOf(byte slot)
    {
        try
        {
            var manager = SavegameManager.Instance;
            if (manager == null) return null;
            foreach (var channel in Channels)
            {
                var saves = manager.GetSavesOfType(channel);
                if (saves == null) continue;
                foreach (var info in saves)
                    if (info.slot == slot) return channel;
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.has-save", exception);
        }
        return null;
    }

    /// <summary>The highest slot with no save on either channel. Starting from the top keeps a
    /// co-op run clear of slot A, where the game puts a solo story by default.</summary>
    internal static byte? FindFreeSlot()
    {
        const byte SlotCount = 12; // A to L, as the game's own slot check lists them
        try
        {
            if (SavegameManager.Instance == null) return null;
            for (var slot = (int)SlotCount - 1; slot >= 0; slot--)
                if (!HasSave((byte)slot)) return (byte)slot;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.free-slot", exception);
        }
        return null;
    }

    /// <summary>Names every save the game sees, so a wrong decision can be told from the log.</summary>
    internal static void LogSaves()
    {
        try
        {
            var manager = SavegameManager.Instance;
            if (manager == null) return;
            foreach (var channel in Channels)
            {
                var saves = manager.GetSavesOfType(channel);
                var letters = new System.Text.StringBuilder();
                if (saves != null)
                    foreach (var info in saves) letters.Append(Letter(info.slot)).Append(' ');
                ModLog.Info($"[CoopSave] {channel} saves: {(letters.Length == 0 ? "none" : letters.ToString().Trim())}");
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.log-saves", exception);
        }
    }

    internal static bool Resume(MainMenuSaveManagementElement screen, byte slot)
    {
        try
        {
            screen.OnLoadGame(slot);
            ModLog.Info($"[CoopSave] Continuing the rope team's run in story slot {Letter(slot)}");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[CoopSave] Could not continue slot {Letter(slot)}: {exception.Message}");
            return false;
        }
    }

    /// <summary>The game starts a new story in the active slot, and writes it through the Summit
    /// channel's active slot: both must point at the free one first.</summary>
    internal static bool StartNewGame(MainMenuSaveManagementElement screen, byte slot)
    {
        try
        {
            foreach (var channel in Channels)
                SavegameManager.Instance.ChangeActiveSlot(channel, slot);
            screen.OnNewGame();
            ModLog.Info($"[CoopSave] Starting the rope team's run in free story slot {Letter(slot)}");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[CoopSave] Could not start a new game in slot {Letter(slot)}: {exception.Message}");
            return false;
        }
    }

    internal static string Letter(byte slot) => slot < 26 ? ((char)('A' + slot)).ToString() : slot.ToString();
}
