using System.Linq;
using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>Tying in, and which pitons the game may refuse.</summary>
[Collection(nameof(RopeLinkState))]
public sealed class RopeRolesTests
{
    private static readonly Vector3 Here = new(0, 10, 0), Far = new(0, 30, 0);

    [Fact]
    public void ARopeIsNotThrownAcrossTheMountain()
    {
        Assert.False(RopeOrder.WithinReach(Here, Far));
        // Within the host's margin: positions arrive late and smoothed.
        Assert.True(RopeOrder.WithinReach(Here, Here + new Vector3(4.5f, 0, 0)));
    }

    [Fact]
    public void AnUnknownPositionNeverRefusesARope()
    {
        Assert.True(RopeOrder.WithinReach(null, Far));
        Assert.True(RopeOrder.WithinReach(Here, null));
    }

    [Fact]
    public void AnUnclipNamesThePairInEitherOrder()
    {
        RopeLinkState.Clear();
        try
        {
            RopeLinkState.Apply(5, 3, clip: true);
            Assert.Equal((5, 3), Assert.Single(RopeLinkState.Links()));

            RopeLinkState.Apply(3, 5, clip: false);
            Assert.Empty(RopeLinkState.Links());
        }
        finally { RopeLinkState.Clear(); }
    }

    [Fact]
    public void ALatecomerGetsTheSameRopesFromTheSnapshot()
    {
        RopeLinkState.Clear();
        try
        {
            RopeLinkState.Apply(2, 9, clip: true);
            var snapshot = RopeLinkState.Links().ToArray();

            RopeLinkState.Clear();
            foreach (var (a, b) in snapshot) RopeLinkState.Apply(a, b, clip: true);

            Assert.True(RopeLinkState.IsLinked(9, 2));
        }
        finally { RopeLinkState.Clear(); }
    }

    [Fact]
    public void WithoutTheClimbotItsAbsenceRefusesNoPiton()
    {
        var allowed = PitonPlacementRule.Decide(false, PitonPlacementRule.ClimbotUnavailableOrMissing,
            withoutClimbot: true, out var reasons);

        Assert.True(allowed);
        Assert.Equal(PitonPlacementRule.Usable, reasons);
    }

    [Fact]
    public void AnyOtherRefusalStands()
    {
        const int noneInInventory = 2;
        var allowed = PitonPlacementRule.Decide(false,
            noneInInventory | PitonPlacementRule.ClimbotUnavailableOrMissing,
            withoutClimbot: true, out var reasons);

        Assert.False(allowed);
        Assert.Equal(noneInInventory, reasons);
    }

    [Fact]
    public void OutsideTheRulesTheGameDecides()
    {
        Assert.True(PitonPlacementRule.Decide(true, 0, false, out _));
        Assert.False(PitonPlacementRule.Decide(false, PitonPlacementRule.ClimbotUnavailableOrMissing, false, out _));
    }
}
