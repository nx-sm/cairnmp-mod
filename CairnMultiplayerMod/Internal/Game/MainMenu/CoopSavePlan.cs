using System;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

internal enum CoopSaveAction
{
    /// <summary>Leave the choice to the player on the game's own save screen.</summary>
    Manual,

    /// <summary>Continue the co-op run in <see cref="CoopSavePlan.Slot"/>.</summary>
    Resume,

    /// <summary>Start the co-op run as a new game in the free <see cref="CoopSavePlan.Slot"/>.</summary>
    NewGame,
}

/// <summary>
/// Where a rope team's run lives on this machine. Every player keeps their own copy: the
/// slot the run was started in is remembered, and each launch from a lobby continues it.
///
/// A new co-op run never goes into a slot that already holds a save. Cairn starts a new game
/// in whichever slot is active — usually the solo story — and that is how co-op launches kept
/// overwriting the progress they were meant to continue.
/// </summary>
internal readonly struct CoopSavePlan
{
    private CoopSavePlan(CoopSaveAction action, byte slot)
    {
        Action = action;
        Slot = slot;
    }

    internal CoopSaveAction Action { get; }
    internal byte Slot { get; }

    /// <param name="rememberedSlot">The co-op slot from an earlier run, or a negative value.</param>
    /// <param name="hasSave">Whether a slot holds a save.</param>
    /// <param name="freeSlot">A slot holding nothing, if there is one.</param>
    internal static CoopSavePlan Decide(int rememberedSlot, Func<byte, bool> hasSave, byte? freeSlot)
    {
        if (hasSave == null) throw new ArgumentNullException(nameof(hasSave));
        if (rememberedSlot is >= 0 and <= byte.MaxValue && hasSave((byte)rememberedSlot))
            return new CoopSavePlan(CoopSaveAction.Resume, (byte)rememberedSlot);
        // Every slot taken: picking one would destroy somebody's save. The player chooses.
        return freeSlot.HasValue
            ? new CoopSavePlan(CoopSaveAction.NewGame, freeSlot.Value)
            : new CoopSavePlan(CoopSaveAction.Manual, 0);
    }
}
