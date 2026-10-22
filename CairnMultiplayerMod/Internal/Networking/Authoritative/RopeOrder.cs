#nullable enable
using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>
/// Who leads a rope the host is about to tie. Held by the host only: every client learns the
/// order from the clip the host broadcasts, leader first.
///
/// The higher climber leads, because that is who is already ahead on the face. One rule comes
/// before height: a climber whose death played out gives the lead to whoever survived, so
/// coming back up from the checkpoint and tying in again never hands the lead back to them.
/// </summary>
internal sealed class RopeOrder
{
    /// <summary>
    /// How far apart two climbers may tie in, as the host sees them. Clients only offer the
    /// rope within 3 m; positions reach the host late and smoothed, so its margin is wider —
    /// it only has to refuse a rope thrown across the mountain.
    /// </summary>
    internal const float ClipReachMeters = 5f;

    private readonly HashSet<int> _yieldsLead = new();

    /// <summary>That climber's death played out: their next rope is led by the other.</summary>
    internal void YieldLead(int playerId)
    {
        if (playerId > 0) _yieldsLead.Add(playerId);
    }

    internal bool YieldsLead(int playerId) => _yieldsLead.Contains(playerId);

    internal void Forget(int playerId) => _yieldsLead.Remove(playerId);

    internal void Clear() => _yieldsLead.Clear();

    /// <summary>
    /// Orders a new rope between <paramref name="requester"/> and <paramref name="target"/>.
    /// Unknown positions pass the reach check: a rope is never refused for want of a fresh
    /// position, only for one plainly too far.
    /// </summary>
    internal bool TryOrder(int requester, int target, Vector3? requesterPosition, Vector3? targetPosition,
        out int leader, out int last)
    {
        leader = target;
        last = requester;
        if (requesterPosition.HasValue && targetPosition.HasValue
            && Vector3.DistanceSquared(requesterPosition.Value, targetPosition.Value)
               > ClipReachMeters * ClipReachMeters)
            return false;

        var requesterYields = _yieldsLead.Contains(requester);
        var targetYields = _yieldsLead.Contains(target);
        if (requesterYields != targetYields)
        {
            if (targetYields) (leader, last) = (requester, target);
        }
        else if (requesterPosition.HasValue && targetPosition.HasValue
                 && requesterPosition.Value.Y > targetPosition.Value.Y)
        {
            (leader, last) = (requester, target);
        }
        // Otherwise the climber being tied onto leads: pressing the rope key is how the one
        // behind asks to follow.
        return true;
    }

    /// <summary>Tied in: whatever either climber owed from an earlier death is settled.</summary>
    internal void Linked(int leader, int last)
    {
        _yieldsLead.Remove(leader);
        _yieldsLead.Remove(last);
    }
}
