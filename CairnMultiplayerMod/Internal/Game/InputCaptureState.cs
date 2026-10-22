namespace CairnMultiplayerMod.Internal.Game;

internal static class InputCaptureState
{
    internal static bool IsKeyboardCaptured { get; set; }
    internal static bool IsControllerShortcutCaptured { get; set; }
    internal static bool WantsGameplayBlocked
        => IsKeyboardCaptured || IsControllerShortcutCaptured;

    /// <summary>
    /// Only typing may stop the input system from taking events at all. A held View button
    /// must not: its own release is an input event, and swallowing it left the modifier held
    /// forever — the game froze until a window switch reset the controller.
    /// </summary>
    internal static bool WantsInputEventsIgnored => IsKeyboardCaptured;
}
