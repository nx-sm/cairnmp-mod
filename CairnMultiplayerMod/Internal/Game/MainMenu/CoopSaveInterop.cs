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
    private const SaveType Channel = SaveType.Story;

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

    internal static bool HasSave(byte slot)
    {
        try
        {
            var saves = SavegameManager.Instance?.GetSavesOfType(Channel);
            if (saves == null) return false;
            foreach (var info in saves)
                if (info.slot == slot) return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.has-save", exception);
        }
        return false;
    }

    internal static byte? FindFreeSlot()
    {
        try
        {
            var manager = SavegameManager.Instance;
            if (manager != null && manager.FindNextAvailableSlot(Channel, out var free) && free.IsValid)
                return free.slot;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("coop-save.free-slot", exception);
        }
        return null;
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

    /// <summary>The game starts a new story in the active slot: make it the free one first.</summary>
    internal static bool StartNewGame(MainMenuSaveManagementElement screen, byte slot)
    {
        try
        {
            SavegameManager.Instance.ChangeActiveSlot(Channel, slot);
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
