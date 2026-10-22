using CairnMultiplayerMod.Internal.Game.MainMenu;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class CoopSavePlanTests
{
    [Fact]
    public void TheRopeTeamContinuesTheRunItStarted()
    {
        var plan = CoopSavePlan.Decide(2, slot => slot == 2, freeSlot: 5);

        Assert.Equal(CoopSaveAction.Resume, plan.Action);
        Assert.Equal(2, plan.Slot);
    }

    [Fact]
    public void AFirstRunStartsInAFreeSlotNeverOverAnExistingSave()
    {
        var plan = CoopSavePlan.Decide(-1, slot => slot == 0, freeSlot: 1);

        Assert.Equal(CoopSaveAction.NewGame, plan.Action);
        Assert.Equal(1, plan.Slot);
    }

    [Fact]
    public void ARememberedSlotThatWasErasedStartsAgainInAFreeOne()
    {
        var plan = CoopSavePlan.Decide(3, _ => false, freeSlot: 0);

        Assert.Equal(CoopSaveAction.NewGame, plan.Action);
        Assert.Equal(0, plan.Slot);
    }

    [Fact]
    public void WithEverySlotTakenThePlayerChooses()
    {
        var plan = CoopSavePlan.Decide(-1, _ => true, freeSlot: null);

        Assert.Equal(CoopSaveAction.Manual, plan.Action);
    }

    [Fact]
    public void AnOutOfRangeSlotIsIgnored()
    {
        var plan = CoopSavePlan.Decide(300, _ => true, freeSlot: 4);

        Assert.Equal(CoopSaveAction.NewGame, plan.Action);
        Assert.Equal(4, plan.Slot);
    }
}
