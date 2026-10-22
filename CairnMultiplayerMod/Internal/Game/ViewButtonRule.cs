using System;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Where the game's own controller bindings on the View button go, so View can belong to
/// CairnMP's shortcuts alone. Plain binding paths in, plain paths out: testable without the game.
/// </summary>
internal static class ViewButtonRule
{
    internal const string MenuButtonPath = "<Gamepad>/start";

    /// <summary>The View button (Share on PlayStation), whatever controller layout names it.</summary>
    internal static bool IsViewButton(string path)
        => !string.IsNullOrEmpty(path)
           && path.StartsWith("<", StringComparison.Ordinal)
           && path.EndsWith("/select", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMenuButton(string path)
        => !string.IsNullOrEmpty(path)
           && path.StartsWith("<", StringComparison.Ordinal)
           && path.EndsWith("/start", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The new path for a game binding on View: the Menu button, unless that action map
    /// already uses Menu for something else — two actions on one button would fire together,
    /// so the View binding is then simply cleared.
    /// </summary>
    internal static string NewPath(bool mapAlreadyUsesMenu) => mapAlreadyUsesMenu ? "" : MenuButtonPath;
}
