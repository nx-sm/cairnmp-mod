using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// Keeps the co-op run apart from single-player saves, on every player's machine.
///
/// Co-op owns one save slot, L, and nothing else; single-player keeps A to K. Cairn writes a
/// co-op run as a Summit save (…\SUMMIT\SLOT_L.CPK) but only lists Story saves
/// (…\STORY\SLOT_L.CPK) on its save screen, and it reads which saves exist once, at start-up.
/// So before the game looks, the newer of the two copies is copied over the other: the run
/// the game lists is always the latest checkpoint, whichever folder it was written to.
///
/// Nothing here touches the game: it works on a "Saved Games\TheGameBakers" folder, so it is
/// tested on a temporary one. Every file it replaces or moves is copied to the backup folder
/// first.
/// </summary>
internal static class CoopSaveFiles
{
    /// <summary>The co-op slot: L, the last one, out of the way of a solo story's slot A.</summary>
    internal const byte Slot = 11;

    internal static readonly string[] Channels = { "STORY", "SUMMIT" };

    internal static string FileName(byte slot) => $"SLOT_{(char)('A' + slot)}.CPK";

    /// <summary>Every SAVEGAMES\&lt;build&gt; folder under the game's saves root.</summary>
    internal static IEnumerable<DirectoryInfo> BuildFolders(DirectoryInfo root)
    {
        if (root == null || !root.Exists) yield break;
        foreach (var game in root.EnumerateDirectories("Cairn_*"))
        {
            var saves = new DirectoryInfo(Path.Combine(game.FullName, "SAVEGAMES"));
            if (!saves.Exists) continue;
            foreach (var build in saves.EnumerateDirectories()) yield return build;
        }
    }

    /// <summary>The slot's file in each channel folder of one build, existing or not.</summary>
    internal static IEnumerable<FileInfo> SlotFiles(DirectoryInfo build, byte slot)
        => Channels.Select(channel => new FileInfo(Path.Combine(build.FullName, channel, FileName(slot))));

    internal static bool SlotHasSave(DirectoryInfo root, byte slot)
        => BuildFolders(root).SelectMany(build => SlotFiles(build, slot)).Any(file => file.Exists && file.Length > 0);

    /// <summary>
    /// Run once at start-up, before the game reads its saves.
    /// A co-op run remembered in another slot (from before co-op had its own) moves to L,
    /// whatever L held being archived first; then L's Story and Summit copies are made the same,
    /// the newer winning.
    /// </summary>
    /// <returns>The slot to remember from now on: always <see cref="Slot"/>.</returns>
    internal static int PrepareAtStartup(DirectoryInfo root, int rememberedSlot, string backupFolder,
        Action<string> log)
    {
        foreach (var build in BuildFolders(root).ToArray())
        {
            if (rememberedSlot is >= 0 and < Slot)
                MoveSlot(build, (byte)rememberedSlot, Slot, backupFolder, log);
            SyncNewest(build, Slot, log);
        }
        return Slot;
    }

    /// <summary>Moves a run between slots, keeping both channels; the target's old files are archived.</summary>
    internal static void MoveSlot(DirectoryInfo build, byte from, byte to, string backupFolder, Action<string> log)
    {
        var sources = SlotFiles(build, from).Where(file => file.Exists).ToArray();
        if (sources.Length == 0) return;

        Archive(build, to, backupFolder, log);
        foreach (var source in sources)
        {
            var target = Path.Combine(source.DirectoryName!, FileName(to));
            source.CopyTo(target, overwrite: false);
            Directory.CreateDirectory(Path.Combine(backupFolder, "moved-out"));
            source.MoveTo(Path.Combine(backupFolder, "moved-out", $"{source.Directory!.Name}_{source.Name}"), overwrite: true);
        }
        log?.Invoke($"[CoopSave] Moved the co-op run from slot {(char)('A' + from)} to slot {(char)('A' + to)}; " +
                    $"slot {(char)('A' + from)} is free for single-player again");
    }

    /// <summary>Moves a slot's files out of the game's folders into the backup folder.</summary>
    internal static void Archive(DirectoryInfo build, byte slot, string backupFolder, Action<string> log)
    {
        var archived = 0;
        foreach (var file in SlotFiles(build, slot).Where(file => file.Exists))
        {
            var folder = Path.Combine(backupFolder, "replaced");
            Directory.CreateDirectory(folder);
            file.MoveTo(Path.Combine(folder, $"{file.Directory!.Name}_{file.Name}"), overwrite: true);
            archived++;
        }
        if (archived > 0)
            log?.Invoke($"[CoopSave] Archived the previous co-op run of slot {(char)('A' + slot)} to {backupFolder}");
    }

    /// <summary>Makes the slot's Story and Summit copies the same file: the newer one.</summary>
    internal static void SyncNewest(DirectoryInfo build, byte slot, Action<string> log)
    {
        var files = SlotFiles(build, slot).ToArray();
        var existing = files.Where(file => file.Exists && file.Length > 0).ToArray();
        if (existing.Length == 0) return;

        var newest = existing.OrderByDescending(file => file.LastWriteTimeUtc).First();
        foreach (var file in files)
        {
            if (file.FullName == newest.FullName) continue;
            if (file.Exists && file.LastWriteTimeUtc == newest.LastWriteTimeUtc && file.Length == newest.Length)
                continue;
            Directory.CreateDirectory(file.DirectoryName!);
            newest.CopyTo(file.FullName, overwrite: true);
            File.SetLastWriteTimeUtc(file.FullName, newest.LastWriteTimeUtc);
            log?.Invoke($"[CoopSave] Slot {(char)('A' + slot)}: {file.Directory!.Name} copy updated from the newer " +
                        $"{newest.Directory!.Name} save ({newest.LastWriteTime:yyyy-MM-dd HH:mm})");
        }
    }
}
