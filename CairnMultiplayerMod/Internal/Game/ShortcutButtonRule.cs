using System;
using System.Collections.Generic;
using System.Linq;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Which of the game's controller bindings sit on a button CairnMP uses with View. While View
/// is held those game actions are switched off, so View + Y ties the rope without the game
/// also whistling for the Climbot. Plain binding paths in: testable without the game.
/// </summary>
internal static class ShortcutButtonRule
{
    /// <summary>Unity's control names for each button, aliases included (a/b/x/y, PlayStation names).</summary>
    private static string[] ControlNames(ControllerButton button) => button switch
    {
        ControllerButton.South => new[] { "buttonSouth", "a", "cross" },
        ControllerButton.East => new[] { "buttonEast", "b", "circle" },
        ControllerButton.West => new[] { "buttonWest", "x", "square" },
        ControllerButton.North => new[] { "buttonNorth", "y", "triangle" },
        ControllerButton.LeftShoulder => new[] { "leftShoulder", "L1" },
        ControllerButton.RightShoulder => new[] { "rightShoulder", "R1" },
        ControllerButton.LeftTrigger => new[] { "leftTrigger", "L2" },
        ControllerButton.RightTrigger => new[] { "rightTrigger", "R2" },
        // The whole pad, one axis of it, or one direction: all of it moves with the D-pad.
        ControllerButton.DpadUp => new[] { "dpad", "dpad/up", "dpad/y" },
        ControllerButton.DpadDown => new[] { "dpad", "dpad/down", "dpad/y" },
        ControllerButton.DpadLeft => new[] { "dpad", "dpad/left", "dpad/x" },
        ControllerButton.DpadRight => new[] { "dpad", "dpad/right", "dpad/x" },
        ControllerButton.Menu => new[] { "start", "options" },
        ControllerButton.RightStick => new[] { "rightStickPress", "R3" },
        _ => Array.Empty<string>(),
    };

    private static readonly HashSet<string> Controls = new(
        Enum.GetValues(typeof(ControllerShortcut)).Cast<ControllerShortcut>()
            .Select(ControllerShortcutBindings.ButtonFor)
            .SelectMany(ControlNames),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] NotAController = { "<Keyboard>", "<Mouse>", "<Pointer>", "<Touchscreen>", "<Pen>" };

    /// <summary>A controller binding on a button some View + button shortcut uses.</summary>
    internal static bool IsShortcutButton(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("<", StringComparison.Ordinal)) return false;
        if (NotAController.Any(device => path.StartsWith(device, StringComparison.OrdinalIgnoreCase))) return false;
        var slash = path.IndexOf('/');
        return slash > 0 && Controls.Contains(path.Substring(slash + 1));
    }
}
