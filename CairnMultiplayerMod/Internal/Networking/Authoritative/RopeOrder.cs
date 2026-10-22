#nullable enable
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>
/// The host's check before tying two climbers together. Who leads is not decided here: it is
/// whoever is ahead at any moment, and changes as often as the climb does.
/// </summary>
internal static class RopeOrder
{
    /// <summary>
    /// How far apart two climbers may tie in, as the host sees them. Clients only offer the
    /// rope within 3 m; positions reach the host late and smoothed, so its margin is wider —
    /// it only has to refuse a rope thrown across the mountain.
    /// </summary>
    internal const float ClipReachMeters = 5f;

    /// <summary>Unknown positions pass: a rope is never refused for want of a fresh position,
    /// only for one plainly too far.</summary>
    internal static bool WithinReach(Vector3? a, Vector3? b)
        => !a.HasValue || !b.HasValue
           || Vector3.DistanceSquared(a.Value, b.Value) <= ClipReachMeters * ClipReachMeters;
}
