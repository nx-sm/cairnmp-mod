using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ShortcutButtonRuleTests
{
    [Theory]
    // Cairn's own bindings that fired alongside View shortcuts.
    [InlineData("<Gamepad>/buttonNorth", true)]      // Climbot whistle, rest
    [InlineData("<Gamepad>/rightShoulder", true)]    // Eagle Eye path select
    [InlineData("<Gamepad>/leftShoulder", true)]     // observation
    [InlineData("<Gamepad>/leftTrigger", true)]
    [InlineData("<Gamepad>/rightTrigger", true)]
    [InlineData("<Gamepad>/rightStickPress", true)]  // rewind
    [InlineData("<Gamepad>/start", true)]
    [InlineData("<Gamepad>/dpad", true)]
    [InlineData("<Gamepad>/dpad/x", true)]
    [InlineData("<Gamepad>/dpad/down", true)]
    [InlineData("<XInputController>/y", true)]
    [InlineData("<DualShockGamepad>/triangle", true)]
    // Movement, the modifier itself and buttons no shortcut uses stay with the game.
    [InlineData("<Gamepad>/leftStick", false)]
    [InlineData("<Gamepad>/rightStick", false)]
    [InlineData("<Gamepad>/leftStick/up", false)]
    [InlineData("<Gamepad>/rightStick/up", false)]
    [InlineData("<Gamepad>/leftStickPress", false)]
    [InlineData("<Gamepad>/select", false)]
    [InlineData("<Keyboard>/y", false)]
    [InlineData("<Keyboard>/b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BlocksOnlyTheGamesBindingsOnShortcutButtons(string path, bool expected)
        => Assert.Equal(expected, ShortcutButtonRule.IsShortcutButton(path));
}
