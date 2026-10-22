using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Global state of active rope links (playerId pairs), shared between the network
/// (updated from ServerRopeClip) and rendering (RopeLinkRenderer). Each link is
/// stored once, under a normalized (min/max) key so that (a,b) == (b,a), together with
/// the climber who asked for it, so a latecomer's snapshot replays the clip as it was sent.
/// </summary>
internal static class RopeLinkState
{
    private static readonly Dictionary<long, int> _links = new();

    private static long Key(int a, int b)
    {
        uint lo = (uint)Math.Min(a, b);
        uint hi = (uint)Math.Max(a, b);
        return ((long)lo << 32) | hi;
    }

    private static int High(long k) => (int)(uint)(k >> 32);
    private static int Low(long k) => (int)(uint)(k & 0xFFFFFFFFL);

    public static int Count => _links.Count;

    /// <summary>On a clip, <paramref name="a"/> is the climber who asked. An unclip may name
    /// the pair in either order.</summary>
    public static void Apply(int a, int b, bool clip)
    {
        if (a == b) return;
        var k = Key(a, b);
        if (clip)
        {
            if (PartnerOf(a) >= 0 || PartnerOf(b) >= 0) return;
            _links[k] = a;
        }
        else _links.Remove(k);
    }

    /// <summary>Every link, the climber who asked first.</summary>
    public static IEnumerable<(int a, int b)> Links()
    {
        foreach (var pair in _links)
        {
            var asker = pair.Value;
            var other = High(pair.Key) == asker ? Low(pair.Key) : High(pair.Key);
            yield return (asker, other);
        }
    }

    public static bool IsLinked(int a, int b) => a != b && _links.ContainsKey(Key(a, b));

    public static int PartnerOf(int playerId)
    {
        foreach (var k in _links.Keys)
        {
            int hi = High(k), lo = Low(k);
            if (hi == playerId) return lo;
            if (lo == playerId) return hi;
        }
        return -1;
    }

    public static void RemovePlayer(int playerId)
    {
        var gone = new List<long>();
        foreach (var k in _links.Keys)
            if (High(k) == playerId || Low(k) == playerId) gone.Add(k);
        foreach (var k in gone) _links.Remove(k);
    }

    public static void Clear() => _links.Clear();
}
