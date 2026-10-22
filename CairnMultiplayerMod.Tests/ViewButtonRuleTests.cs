using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ViewButtonRuleTests
{
    [Theory]
    [InlineData("<Gamepad>/select", true)]
    [InlineData("<XInputController>/select", true)]
    [InlineData("<DualShockGamepad>/select", true)]
    [InlineData("<Gamepad>/start", false)]
    [InlineData("<Keyboard>/select", true)]
    [InlineData("<Gamepad>/buttonSouth", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesTheViewButton(string path, bool expected)
        => Assert.Equal(expected, ViewButtonRule.IsViewButton(path));

    [Fact]
    public void TheGamesViewActionMovesToTheMenuButton()
        => Assert.Equal("<Gamepad>/start", ViewButtonRule.NewPath(mapAlreadyUsesMenu: false));

    [Fact]
    public void AMenuButtonAlreadyInUseIsNotDoubledUp()
        => Assert.Equal("", ViewButtonRule.NewPath(mapAlreadyUsesMenu: true));
}
