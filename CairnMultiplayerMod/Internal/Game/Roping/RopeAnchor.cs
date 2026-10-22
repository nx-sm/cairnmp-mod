using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The partner on the other end of the rope, as Cairn understands an anchor: an invisible,
/// unbreakable piton on the local climber's own Lifeline, kept at the partner's harness.
///
/// Cairn only knows how to hold a fall from a piton. Telling it "you are secured" without one
/// sent the climber into a secured fall with nothing to hang from, then dropped them
/// unsecured to their death; rewinding the death afterwards did not bring them back either.
/// With a real piton where the partner stands, the game's own secured fall does the rest —
/// the climber swings and hangs on the rope exactly as below one of their own pitons.
///
/// The piton follows the partner while the local climber is on the wall and stays put while
/// they fall. It is never synced to anyone (each player keeps their own, at the other's
/// harness), cannot be taken out by hand, and is lifted off before every save so it never
/// ends up in a savegame.
/// </summary>
internal static class RopeAnchor
{
    /// <summary>How far the partner moves before the anchor is moved after them.</summary>
    private const float FollowMeters = 1f;

    /// <summary>Far beyond anything a fall takes out of a piton: it never breaks.</summary>
    private const int UnbreakableHp = 1_000_000;

    private static IntPtr _anchor;
    private static Vector3 _anchorAt;
    private static int? _pitonItemId;
    private static readonly HashSet<IntPtr> _before = new();
    private static bool _loggedPlaced;

    internal static bool IsAnchor(IntPtr piton) => piton != IntPtr.Zero && piton == _anchor;

    /// <param name="active">Tied in to a partner who is on the wall, in a rope-team session.</param>
    /// <param name="partnerHarness">Where the partner's harness is, if known.</param>
    /// <param name="localFalling">The local climber is coming off: the anchor stays put.</param>
    internal static void Tick(bool active, Vector3? partnerHarness, bool localFalling)
    {
        if (!active || !partnerHarness.HasValue)
        {
            Remove();
            return;
        }
        if (localFalling) return;

        var target = partnerHarness.Value;
        if (_anchor != IntPtr.Zero && StillPlaced() && (target - _anchorAt).sqrMagnitude < FollowMeters * FollowMeters)
            return;

        Remove();
        Place(target);
    }

    /// <summary>Called right before the game saves: an anchor must never be written out.</summary>
    internal static void ReleaseForSave() => Remove();

    private static void Place(Vector3 at)
    {
        var lifeline = RopeInterop.TryGetLifeline();
        var controller = RopeInterop.ResolveLocalClimbController();
        var itemId = PitonItemId(lifeline);
        if (lifeline == null || controller == null || !itemId.HasValue) return;

        try
        {
            if (!Collect(lifeline, _before)) return;
            lifeline.AddPiton(at, Quaternion.identity, Il2Cpp.PitonExecutionQuality.Perfect,
                UnbreakableHp, (Il2Cpp.InventoryItemStringId)itemId.Value, controller);

            var placed = lifeline.PlacedPitons;
            for (var i = 0; placed != null && i < placed.Count; i++)
            {
                var piton = placed[i]?.Piton;
                if (piton == null || piton.Pointer == IntPtr.Zero || _before.Contains(piton.Pointer)) continue;
                _anchor = piton.Pointer;
                _anchorAt = at;
                Hide(piton);
                RopeInterop.IgnorePiton(piton.Pointer);
                break;
            }
            if (_anchor != IntPtr.Zero && !_loggedPlaced)
            {
                _loggedPlaced = true;
                ModLog.Info("[RopeAnchor] The partner now holds the rope like a piton (invisible anchor placed)");
            }
        }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeAnchor] Could not place the anchor: " + exception.Message);
        }
    }

    private static void Remove()
    {
        if (_anchor == IntPtr.Zero) return;
        var anchor = _anchor;
        _anchor = IntPtr.Zero;
        try
        {
            var lifeline = RopeInterop.TryGetLifeline();
            if (lifeline == null || !Collect(lifeline, _before) || !_before.Contains(anchor)) return;

            var itemId = new Il2Cpp.Piton(anchor).ItemId.value;
            var carried = InventoryAdapter.CountInBag(itemId);
            lifeline.DetachPiton(new Il2Cpp.Piton(anchor));
            // Taking a piton off the wall can hand it back to the bag: this one was never the
            // climber's to keep.
            if (InventoryAdapter.CountInBag(itemId) > carried) InventoryAdapter.TryTakeOne(itemId);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.anchor-remove", exception);
        }
    }

    private static bool StillPlaced()
    {
        var lifeline = RopeInterop.TryGetLifeline();
        return lifeline != null && Collect(lifeline, _before) && _before.Contains(_anchor);
    }

    private static int? PitonItemId(Il2Cpp.Lifeline lifeline)
    {
        if (_pitonItemId.HasValue) return _pitonItemId;
        _pitonItemId = InventoryAdapter.FindPitonItemId();
        if (_pitonItemId.HasValue || lifeline == null) return _pitonItemId;
        try
        {
            var placed = lifeline.PlacedPitons;
            for (var i = 0; placed != null && i < placed.Count; i++)
            {
                var piton = placed[i]?.Piton;
                if (piton != null && piton.Pointer != IntPtr.Zero) return _pitonItemId = piton.ItemId.value;
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.anchor-item", exception);
        }
        return null;
    }

    private static void Hide(Il2Cpp.Piton piton)
    {
        try
        {
            foreach (var renderer in piton.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;
            piton.canBePickedUp = false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.anchor-hide", exception);
        }
    }

    private static bool Collect(Il2Cpp.Lifeline lifeline, HashSet<IntPtr> pointers)
    {
        pointers.Clear();
        var placed = lifeline?.PlacedPitons;
        if (placed == null) return false;
        for (var i = 0; i < placed.Count; i++)
        {
            var piton = placed[i]?.Piton;
            if (piton != null && piton.Pointer != IntPtr.Zero) pointers.Add(piton.Pointer);
        }
        return true;
    }

    /// <summary>Leaving the session or the scene: the old Lifeline's pointers mean nothing.</summary>
    internal static void Forget()
    {
        _anchor = IntPtr.Zero;
        _pitonItemId = null;
    }
}
