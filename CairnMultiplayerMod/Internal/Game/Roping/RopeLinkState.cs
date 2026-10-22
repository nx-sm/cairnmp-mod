using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Global state of active rope links (playerId pairs), shared between the network
/// (updated from ServerRopeClip) and rendering (RopeLinkRenderer). Each link is
/// stored once, under a normalized (min/max) key so that (a,b) == (b,a), together with
/// the climber who leads it: the host broadcasts a clip leader first.
/// </summary>
internal static class RopeLinkState
{
    private static readonly Dictionary<long, int> _leaders = new();

    private static long Key(int a, int b)
    {
        uint lo = (uint)Math.Min(a, b);
        uint hi = (uint)Math.Max(a, b);
        return ((long)lo << 32) | hi;
    }

    private static int High(long k) => (int)(uint)(k >> 32);
    private static int Low(long k) => (int)(uint)(k & 0xFFFFFFFFL);

    public static int Count => _leaders.Count;

    /// <summary>On a clip, <paramref name="a"/> leads. An unclip may name the pair in
    /// either order.</summary>
    public static void Apply(int a, int b, bool clip)
    {
        if (a == b) return;
        var k = Key(a, b);
        if (clip)
        {
            if (PartnerOf(a) >= 0 || PartnerOf(b) >= 0) return;
            _leaders[k] = a;
        }
        else _leaders.Remove(k);
    }

    /// <summary>Every link, leader first.</summary>
    public static IEnumerable<(int a, int b)> Links()
    {
        foreach (var pair in _leaders)
        {
            var leader = pair.Value;
            var other = High(pair.Key) == leader ? Low(pair.Key) : High(pair.Key);
            yield return (leader, other);
        }
    }

    public static bool IsLinked(int a, int b) => a != b && _leaders.ContainsKey(Key(a, b));

    public static int PartnerOf(int playerId)
    {
        foreach (var k in _leaders.Keys)
        {
            int hi = High(k), lo = Low(k);
            if (hi == playerId) return lo;
            if (lo == playerId) return hi;
        }
        return -1;
    }

    /// <summary>True when that climber is tied in and leads the rope.</summary>
    public static bool IsLeader(int playerId)
    {
        foreach (var leader in _leaders.Values)
            if (leader == playerId) return true;
        return false;
    }

    /// <summary>True when that climber is tied in and follows: the last one on the rope.</summary>
    public static bool IsLast(int playerId) => PartnerOf(playerId) >= 0 && !IsLeader(playerId);

    public static void RemovePlayer(int playerId)
    {
        var gone = new List<long>();
        foreach (var k in _leaders.Keys)
            if (High(k) == playerId || Low(k) == playerId) gone.Add(k);
        foreach (var k in gone) _leaders.Remove(k);
    }

    public static void Clear() => _leaders.Clear();
}
