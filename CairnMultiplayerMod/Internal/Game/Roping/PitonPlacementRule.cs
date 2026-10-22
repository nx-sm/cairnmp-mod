namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Who may drive a piton in, decided on top of the game's own answer. The reasons are
/// Cairn's <c>PitonUnusabilityReason</c> flags, carried as plain ints so the rule can be
/// tested without the game.
/// </summary>
internal static class PitonPlacementRule
{
    internal const int Usable = 0;
    internal const int ClimbotUnavailableOrMissing = 4;
    internal const int NetplayNotRopeHead = 16;

    /// <param name="nativeAllowed">The game's own verdict.</param>
    /// <param name="reasons">The game's reasons for refusing, as flags.</param>
    /// <param name="withoutClimbot">The session plays without the robot on purpose, so its
    /// absence is no reason to refuse.</param>
    /// <param name="follower">The local climber is tied in behind a leader, who alone
    /// places the pitons — the game's own rule for its netplay rope.</param>
    /// <param name="adjusted">The reasons to report back to the game.</param>
    internal static bool Decide(bool nativeAllowed, int reasons, bool withoutClimbot, bool follower,
        out int adjusted)
    {
        adjusted = reasons;
        if (withoutClimbot) adjusted &= ~ClimbotUnavailableOrMissing;
        if (follower) adjusted |= NetplayNotRopeHead;
        // Nothing of ours applied: the game's answer stands, whatever it was based on.
        if (adjusted == reasons) return nativeAllowed;
        return adjusted == Usable;
    }
}
