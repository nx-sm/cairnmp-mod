using CairnMultiplayerMod.Internal.UI;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class JoinCodeTextTests
{
    [Theory]
    [InlineData("100.64.1.2:27420", "100.64.1.2:27420")]
    [InlineData("  100.64.1.2:27420\r\n", "100.64.1.2:27420")]
    [InlineData("join me: 100.64.1.2:27420", "100.64.1.2:27420")]
    [InlineData("Join me at \"100.64.1.2:27420\".", "100.64.1.2:27420")]
    [InlineData("[fd7a:115c::1]:27420", "[fd7a:115c::1]:27420")]
    [InlineData("ABCD-EFGH", "ABCD-EFGH")]
    [InlineData("code: ABCD-EFGH", "ABCD-EFGH")]
    public void PicksTheAddressOrCodeOutOfWhatWasCopied(string clipboard, string expected)
        => Assert.Equal(expected, JoinCodeText.FromClipboard(clipboard, 64));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void AnEmptyClipboardPastesNothing(string clipboard)
        => Assert.Equal("", JoinCodeText.FromClipboard(clipboard, 64));

    [Fact]
    public void ThePasteFitsTheField()
        => Assert.Equal("100.64", JoinCodeText.FromClipboard("100.64.1.2:27420", 6));
}
