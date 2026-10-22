using System;
using System.IO;
using System.Linq;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTheGameBakers.Cairn.Save;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// Drives the game's own story save screen: continuing a slot, or starting a new game in a
/// chosen one.
///
/// Whether a slot holds a save is read from the disk, not from the game's save list. A story
/// run is written as a Summit save (SAVEGAMES/…/SUMMIT/SLOT_A.CPK), and on a machine where the
/// game's save manifest is missing its list comes back empty — every slot looked free, and a
/// launch started a new game over the run it should have continued. A slot with a save file
/// is never treated as free.
/// </summary>
internal static class CoopSaveInterop
{
    private static readonly SaveType[] Channels = { SaveType.Story, SaveType.Summit };
    private const int SlotCount = 12; // A to L, as the game's own slot check lists them

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

    internal static bool HasSave(byte slot) => SaveFile(slot) != null || ListedChannelOf(slot).HasValue;

    /// <summary>The highest slot with no save at all. Starting from the top keeps a co-op run
    /// clear of slot A, where the game puts a solo story by default.</summary>
    internal static byte? FindFreeSlot()
    {
        for (var slot = SlotCount - 1; slot >= 0; slot--)
            if (!HasSave((byte)slot)) return (byte)slot;
        return null;
    }

    /// <summary>
    /// The save file for a slot, if one exists: Saved Games\TheGameBakers\Cairn_*\SAVEGAMES\
    /// &lt;build&gt;\{SUMMIT|STORY}\SLOT_X.CPK, whichever build of the game wrote it.
    /// </summary>
    internal static FileInfo SaveFile(byte slot)
    {
        try
        {
            var root = SavesRoot();
            if (root == null) return null;
            var name = $"SLOT_{Letter(slot)}.CPK";
            foreach (var game in root.EnumerateDirectories("Cairn_*"))
            {
                var saves = new DirectoryInfo(Path.Combine(game.FullName, "SAVEGAMES"));
                if (!saves.Exists) continue;
                foreach (var build in saves.EnumerateDirectories())
                    foreach (var channel in new[] { "SUMMIT", "STORY" })
                    {
                        var file = new FileInfo(Path.Combine(build.FullName, channel, name));
                        if (file.Exists && file.Length > 0) return file;
                    }
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.find-file", exception);
        }
        return null;
    }

    private static DirectoryInfo SavesRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = new DirectoryInfo(Path.Combine(profile, "Saved Games", "TheGameBakers"));
        return root.Exists ? root : null;
    }

    private static SaveType? ListedChannelOf(byte slot)
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

    /// <summary>Names every save on disk and in the game's list, so a decision can be checked.</summary>
    internal static void LogSaves(MainMenuSaveManagementElement screen)
    {
        var onDisk = Enumerable.Range(0, SlotCount).Select(slot => SaveFile((byte)slot))
            .Where(file => file != null)
            .Select(file => $"{file.Directory?.Name}/{file.Name} ({file.LastWriteTime:yyyy-MM-dd HH:mm})")
            .ToArray();
        ModLog.Info($"[CoopSave] Save files on disk: {(onDisk.Length == 0 ? "none" : string.Join(", ", onDisk))}");

        try
        {
            var manager = SavegameManager.Instance;
            if (manager != null)
                foreach (var channel in Channels)
                {
                    var saves = manager.GetSavesOfType(channel);
                    var letters = saves == null ? "" : string.Join(" ", saves.Select(info => Letter(info.slot)));
                    ModLog.Info($"[CoopSave] The game lists {channel} saves: {(letters.Length == 0 ? "none" : letters)}");
                }
            if (screen != null) ModLog.Info($"[CoopSave] The save screen works on the {screen.saveChannel} channel");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.log-saves", exception);
        }
    }

    /// <summary>First try: the save screen's own "continue", on the remembered slot.</summary>
    internal static bool Resume(MainMenuSaveManagementElement screen, byte slot)
    {
        try
        {
            foreach (var channel in Channels)
                SavegameManager.Instance.ChangeActiveSlot(channel, slot);
            screen.OnLoadGame(slot);
            ModLog.Info($"[CoopSave] Continuing the rope team's run in slot {Letter(slot)}");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[CoopSave] The save screen could not continue slot {Letter(slot)}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Second try, when the screen did not start loading: what its "continue" does underneath —
    /// make the slot active and load it — on the channel its file was written to, then walk the
    /// menu into the saved game.
    /// </summary>
    internal static bool ResumeDirectly(byte slot)
    {
        try
        {
            var channel = SaveFile(slot)?.Directory?.Name == "STORY" ? SaveType.Story : SaveType.Summit;
            foreach (var c in Channels)
                SavegameManager.Instance.ChangeActiveSlot(c, slot);
            SavegameManager.Instance.Load(channel, null, false, null);
            MainMenuInterop.ForceMainMenuStep(MainMenuInterop.MainMenuStep.LaunchSavedGame);
            ModLog.Info($"[CoopSave] Loading slot {Letter(slot)} directly ({channel} save)");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[CoopSave] Could not load slot {Letter(slot)} directly: {exception.Message}");
            return false;
        }
    }

    /// <summary>The game starts a new story in the active slot, and writes it through the Summit
    /// channel's active slot: both must point at the free one first.</summary>
    internal static bool StartNewGame(MainMenuSaveManagementElement screen, byte slot)
    {
        if (SaveFile(slot) != null)
        {
            // Never: whatever decided this, a slot with a save file is not free.
            ModLog.Warning($"[CoopSave] Refused to start a new game over the save in slot {Letter(slot)}");
            return false;
        }
        try
        {
            foreach (var channel in Channels)
                SavegameManager.Instance.ChangeActiveSlot(channel, slot);
            screen.OnNewGame();
            ModLog.Info($"[CoopSave] Starting the rope team's run in free slot {Letter(slot)}");
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
