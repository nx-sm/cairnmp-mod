using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2Cpp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Draws every rope in the session, the same way on every screen: a line hanging between the
/// two harnesses, with no physics at all.
///
/// The rope used to be a native physics rope each player simulated on their own, pinned to a
/// remote climber's harness that only ever moves by teleport. Two simulations disagreed, one end
/// could fly off to an invalid position, and each player saw only their own rope. A drawn line
/// cannot do any of that: everyone sees every link the host announced, and nothing pulls on
/// anybody. Catching a fall is the rope controller's business, not this one's.
/// </summary>
internal static class RopeVisuals
{
    private const int Segments = 16;
    private const float Width = 0.035f;

    /// <summary>How far the middle of the rope hangs below the straight line, per metre of span.</summary>
    private const float SagPerMeter = 0.12f;

    private const float MaxSagMeters = 2.5f;

    private static readonly Dictionary<long, LineRenderer> _lines = new();
    private static readonly HashSet<long> _seen = new();
    private static readonly List<long> _gone = new();
    private static readonly Vector3[] _points = new Vector3[Segments + 1];
    private static Material _material;
    private static Harness _localHarness;
    private static int _lastLocalHarnessSearchFrame;

    /// <summary>Redraws every link. Call once per frame while in game.</summary>
    internal static void Update(int localPlayerId)
    {
        _seen.Clear();
        foreach (var (a, b) in RopeLinkState.Links())
        {
            var key = Key(a, b);
            _seen.Add(key);
            if (TryGetAnchor(a, localPlayerId, out var from) && TryGetAnchor(b, localPlayerId, out var to))
                Draw(key, from, to);
            else
                Hide(key);
        }

        _gone.Clear();
        foreach (var key in _lines.Keys)
            if (!_seen.Contains(key)) _gone.Add(key);
        foreach (var key in _gone) Destroy(key);
    }

    /// <summary>Hides every rope without forgetting the links: ghosts are about to go away.</summary>
    internal static void HideAll()
    {
        foreach (var key in _lines.Keys) Hide(key);
    }

    /// <summary>Removes every drawn rope, and forgets the local harness of the old scene.</summary>
    internal static void Clear()
    {
        foreach (var line in _lines.Values)
            if (line != null) Object.Destroy(line.gameObject);
        _lines.Clear();
        _localHarness = null;
        _lastLocalHarnessSearchFrame = 0;
    }

    private static bool TryGetAnchor(int playerId, int localPlayerId, out Vector3 position)
    {
        position = default;
        if (playerId == localPlayerId)
        {
            var harness = ResolveLocalHarness();
            return harness != null && RopeInterop.TryGetHarnessAttachPosition(harness, out position);
        }
        return RemotePlayerManager.TryGetGhostHarnessAttachPosition(playerId, out position);
    }

    private static Harness ResolveLocalHarness()
    {
        if (_localHarness != null) return _localHarness;
        if (_lastLocalHarnessSearchFrame != 0 && Time.frameCount - _lastLocalHarnessSearchFrame < 30) return null;
        _lastLocalHarnessSearchFrame = Time.frameCount;
        try { _localHarness = PawnManager.Instance?.ClimbingPawnController?.harness; }
        catch (Exception exception) { ModLog.SuppressedException("rope.visual-local-harness", exception); }
        return _localHarness;
    }

    private static void Draw(long key, Vector3 from, Vector3 to)
    {
        var line = LineFor(key);
        if (line == null) return;

        var span = Vector3.Distance(from, to);
        var sag = Mathf.Min(MaxSagMeters, span * SagPerMeter);
        for (var i = 0; i <= Segments; i++)
        {
            var t = i / (float)Segments;
            // A parabola is close enough to a hanging rope at these lengths.
            _points[i] = Vector3.Lerp(from, to, t) + Vector3.down * (sag * 4f * t * (1f - t));
        }
        line.positionCount = _points.Length;
        line.SetPositions(_points);
        if (!line.enabled) line.enabled = true;
    }

    private static void Hide(long key)
    {
        if (_lines.TryGetValue(key, out var line) && line != null && line.enabled) line.enabled = false;
    }

    private static void Destroy(long key)
    {
        if (_lines.TryGetValue(key, out var line) && line != null) Object.Destroy(line.gameObject);
        _lines.Remove(key);
    }

    private static LineRenderer LineFor(long key)
    {
        if (_lines.TryGetValue(key, out var existing) && existing != null) return existing;
        try
        {
            var go = new GameObject("CairnMP.SharedRope");
            Object.DontDestroyOnLoad(go);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.widthMultiplier = Width;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = ResolveMaterial();
            line.startColor = line.endColor = new Color(0.85f, 0.35f, 0.15f);
            _lines[key] = line;
            return line;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.visual-create", exception);
            return null;
        }
    }

    /// <summary>The look of the game's own ropes when one is around, a plain colour otherwise.</summary>
    private static Material ResolveMaterial()
    {
        if (_material != null) return _material;
        try
        {
            var ropes = Object.FindObjectsOfType<LogicalRope>();
            if (ropes != null)
                foreach (var rope in ropes)
                {
                    var source = rope?.GetComponent<LineRenderer>()
                                 ?? rope?.ropePartPrefab?.GetComponent<LineRenderer>();
                    if (source?.sharedMaterial == null) continue;
                    return _material = source.sharedMaterial;
                }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.visual-material", exception);
        }

        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        return _material = shader != null ? new Material(shader) : null;
    }

    private static long Key(int a, int b)
    {
        uint lo = (uint)Math.Min(a, b);
        uint hi = (uint)Math.Max(a, b);
        return ((long)lo << 32) | hi;
    }
}
