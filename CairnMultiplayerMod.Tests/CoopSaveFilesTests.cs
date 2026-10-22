using System;
using System.IO;
using System.Linq;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>The co-op run keeps its own slot, L, and never touches a single-player save.</summary>
public sealed class CoopSaveFilesTests : IDisposable
{
    private readonly DirectoryInfo _root;
    private readonly DirectoryInfo _build;
    private readonly string _backup;

    public CoopSaveFilesTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairnmp-saves-" + Guid.NewGuid().ToString("N")));
        _build = Directory.CreateDirectory(Path.Combine(_root.FullName, "Cairn_RETAIL", "SAVEGAMES", "RETAIL"));
        _backup = Path.Combine(_root.FullName, "CairnMP-backups", "test");
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string Path_(string channel, char slot) => Path.Combine(_build.FullName, channel, $"SLOT_{slot}.CPK");

    private void Save(string channel, char slot, string content, DateTime written)
    {
        var path = Path_(channel, slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, written);
    }

    private string Read(string channel, char slot) => File.Exists(Path_(channel, slot)) ? File.ReadAllText(Path_(channel, slot)) : null;

    private static readonly DateTime Earlier = new(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Earlier.AddHours(1);

    [Fact]
    public void AnOldCoopRunInSlotAMovesToLAndFreesAForSinglePlayer()
    {
        Save("SUMMIT", 'A', "coop run", Earlier);
        Save("STORY", 'A', "coop run", Earlier);
        Save("SUMMIT", 'L', "stale fresh start", Later);

        var slot = CoopSaveFiles.PrepareAtStartup(_root, rememberedSlot: 0, _backup, _ => { });

        Assert.Equal(CoopSaveFiles.Slot, slot);
        Assert.Equal("coop run", Read("STORY", 'L'));
        Assert.Equal("coop run", Read("SUMMIT", 'L'));
        Assert.Null(Read("STORY", 'A'));
        Assert.Null(Read("SUMMIT", 'A'));
        // Nothing is ever lost: the replaced L and the moved A are both in the backup folder.
        var kept = Directory.GetFiles(_backup, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        Assert.Contains("stale fresh start", kept);
        Assert.Contains("coop run", kept);
    }

    [Fact]
    public void TheNewestCheckpointIsTheOneTheGameWillList()
    {
        Save("STORY", 'L', "older checkpoint", Earlier);
        Save("SUMMIT", 'L', "newer checkpoint", Later);

        CoopSaveFiles.PrepareAtStartup(_root, CoopSaveFiles.Slot, _backup, _ => { });

        Assert.Equal("newer checkpoint", Read("STORY", 'L'));
        Assert.Equal("newer checkpoint", Read("SUMMIT", 'L'));
    }

    [Fact]
    public void ACoopRunOnlyInSummitAppearsInStoryToo()
    {
        Save("SUMMIT", 'L', "new run", Later);

        CoopSaveFiles.PrepareAtStartup(_root, CoopSaveFiles.Slot, _backup, _ => { });

        Assert.Equal("new run", Read("STORY", 'L'));
    }

    [Fact]
    public void SinglePlayerSlotsAreNeverTouched()
    {
        Save("STORY", 'A', "solo story", Earlier);
        Save("STORY", 'C', "another solo story", Earlier);
        Save("SUMMIT", 'L', "coop run", Later);

        CoopSaveFiles.PrepareAtStartup(_root, CoopSaveFiles.Slot, _backup, _ => { });

        Assert.Equal("solo story", Read("STORY", 'A'));
        Assert.Equal("another solo story", Read("STORY", 'C'));
    }

    [Fact]
    public void ArchivingTheCoopSlotKeepsThePreviousRunAndLeavesSoloAlone()
    {
        Save("STORY", 'A', "solo story", Earlier);
        Save("STORY", 'L', "previous coop run", Earlier);
        Save("SUMMIT", 'L', "previous coop run", Earlier);

        CoopSaveFiles.Archive(_build, CoopSaveFiles.Slot, _backup, _ => { });

        Assert.False(CoopSaveFiles.SlotHasSave(_root, CoopSaveFiles.Slot));
        Assert.Equal("solo story", Read("STORY", 'A'));
        Assert.Equal(2, Directory.GetFiles(_backup, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void NoSavesAtAllIsNotAnError()
        => Assert.Equal(CoopSaveFiles.Slot, CoopSaveFiles.PrepareAtStartup(_root, -1, _backup, _ => { }));
}
