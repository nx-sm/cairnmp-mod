using System.Linq;
using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>Who leads a rope, who places the pitons, and who takes them out.</summary>
[Collection(nameof(RopeLinkState))]
public sealed class RopeRolesTests
{
    private static readonly Vector3 Low = new(0, 10, 0), High = new(1, 12, 0), Far = new(0, 30, 0);

    [Fact]
    public void TheHigherClimberLeads()
    {
        var order = new RopeOrder();

        Assert.True(order.TryOrder(requester: 1, target: 2, High, Low, out var leader, out var last));
        Assert.Equal((1, 2), (leader, last));

        Assert.True(order.TryOrder(requester: 1, target: 2, Low, High, out leader, out last));
        Assert.Equal((2, 1), (leader, last));
    }

    [Fact]
    public void WithoutPositionsTheClimberTiedOntoLeads()
    {
        var order = new RopeOrder();

        Assert.True(order.TryOrder(requester: 1, target: 2, null, null, out var leader, out var last));

        Assert.Equal((2, 1), (leader, last));
    }

    [Fact]
    public void ASurvivorLeadsWhateverTheHeights()
    {
        var order = new RopeOrder();
        order.YieldLead(1);

        // The climber back from the checkpoint is even standing higher: still not the leader.
        Assert.True(order.TryOrder(requester: 1, target: 2, High, Low, out var leader, out var last));
        Assert.Equal((2, 1), (leader, last));

        Assert.True(order.TryOrder(requester: 2, target: 1, Low, High, out leader, out last));
        Assert.Equal((2, 1), (leader, last));
    }

    [Fact]
    public void TyingInSettlesAnEarlierDeath()
    {
        var order = new RopeOrder();
        order.YieldLead(1);

        order.Linked(2, 1);

        Assert.False(order.YieldsLead(1));
        Assert.True(order.TryOrder(requester: 2, target: 1, Low, High, out var leader, out _));
        Assert.Equal(1, leader);
    }

    [Fact]
    public void ARopeIsNotThrownAcrossTheMountain()
    {
        var order = new RopeOrder();

        Assert.False(order.TryOrder(1, 2, Low, Far, out _, out _));
        // Within the host's margin: positions arrive late and smoothed.
        Assert.True(order.TryOrder(1, 2, Low, Low + new Vector3(4.5f, 0, 0), out _, out _));
    }

    [Fact]
    public void ALeaverOwesNothingWhenTheyComeBack()
    {
        var order = new RopeOrder();
        order.YieldLead(1);

        order.Forget(1);

        Assert.False(order.YieldsLead(1));
    }

    [Fact]
    public void EveryClientKnowsWhoLeadsFromTheClip()
    {
        RopeLinkState.Clear();
        try
        {
            RopeLinkState.Apply(5, 3, clip: true);

            Assert.True(RopeLinkState.IsLeader(5));
            Assert.True(RopeLinkState.IsLast(3));
            Assert.False(RopeLinkState.IsLast(5));
            Assert.False(RopeLinkState.IsLeader(3));
            Assert.Equal((5, 3), Assert.Single(RopeLinkState.Links()));

            // An unclip names the pair in either order.
            RopeLinkState.Apply(3, 5, clip: false);
            Assert.Empty(RopeLinkState.Links());
            Assert.False(RopeLinkState.IsLeader(5));
            Assert.False(RopeLinkState.IsLast(3));
        }
        finally { RopeLinkState.Clear(); }
    }

    [Fact]
    public void AClimberOnTheirOwnIsNeitherLeaderNorLast()
    {
        RopeLinkState.Clear();
        Assert.False(RopeLinkState.IsLeader(1));
        Assert.False(RopeLinkState.IsLast(1));
    }

    [Fact]
    public void ALatecomerLearnsTheOrderFromTheSnapshot()
    {
        RopeLinkState.Clear();
        try
        {
            RopeLinkState.Apply(2, 9, clip: true);
            var snapshot = RopeLinkState.Links().ToArray();

            RopeLinkState.Clear();
            foreach (var (a, b) in snapshot) RopeLinkState.Apply(a, b, clip: true);

            Assert.True(RopeLinkState.IsLeader(2));
            Assert.True(RopeLinkState.IsLast(9));
        }
        finally { RopeLinkState.Clear(); }
    }

    [Fact]
    public void WithoutTheClimbotItsAbsenceRefusesNoPiton()
    {
        var allowed = PitonPlacementRule.Decide(false, PitonPlacementRule.ClimbotUnavailableOrMissing,
            withoutClimbot: true, follower: false, out var reasons);

        Assert.True(allowed);
        Assert.Equal(PitonPlacementRule.Usable, reasons);
    }

    [Fact]
    public void AnyOtherRefusalStands()
    {
        const int noneInInventory = 2;
        var allowed = PitonPlacementRule.Decide(false,
            noneInInventory | PitonPlacementRule.ClimbotUnavailableOrMissing,
            withoutClimbot: true, follower: false, out var reasons);

        Assert.False(allowed);
        Assert.Equal(noneInInventory, reasons);
    }

    [Fact]
    public void AFollowerLeavesThePitonsToTheLeader()
    {
        var allowed = PitonPlacementRule.Decide(true, PitonPlacementRule.Usable,
            withoutClimbot: true, follower: true, out var reasons);

        Assert.False(allowed);
        Assert.Equal(PitonPlacementRule.NetplayNotRopeHead, reasons);
    }

    [Fact]
    public void OutsideTheRulesTheGameDecides()
    {
        Assert.True(PitonPlacementRule.Decide(true, 0, false, false, out _));
        Assert.False(PitonPlacementRule.Decide(false, 0, true, false, out _));
    }
}
