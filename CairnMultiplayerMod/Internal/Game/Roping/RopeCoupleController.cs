using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Life;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal sealed class RopeCoupleController
{
    private const float RopeClipRangeMeters = 3f;

    /// <summary>How long a fresh death counts as undecided: the game rules on the death screen
    /// a moment after the body goes limp, and the rope must not be cut before it has.</summary>
    private const float DeathDecisionGraceSeconds = 2f;

    internal const string ToggleKeyName = "L";
    private static readonly Key ToggleKey = System.Enum.Parse<Key>(ToggleKeyName);

    internal const string TakeUpPitonKeyName = "T";
    private static readonly Key TakeUpPitonKey = System.Enum.Parse<Key>(TakeUpPitonKeyName);

    /// <summary>Used when the game's netplay tweakables are not loaded yet.</summary>
    private const float FallbackTakeUpReachMeters = 2.5f;

    private const float NoticeSeconds = 3f;

    private readonly NetworkManager _network;
    private readonly RuntimeState _state;
    private readonly System.Action<string> _notify;

    // Hard teardown is broadcast once per episode to avoid reliable-packet spam.
    private bool _ropeHardTornDown;
    private float _deadSince = -1f;

    internal RopeCoupleController(NetworkManager network, RuntimeState state, System.Action<string> notify = null)
    {
        _network = network;
        _state = state ?? throw new System.ArgumentNullException(nameof(state));
        _notify = notify ?? (_ => { });
    }

    private bool LeadAndFollow => _state.ModeRules.LeadAndFollow;

    internal void Tick()
    {
        if (_network == null)
            return;

        ApplyModeRules();

        // Teardown must remain reachable outside InGame.
        if (!TickRopeSafety()) return;

        if (_state.LocalPlayerState != PlayerState.InGame)
            return;

        if (!InputCaptureState.IsKeyboardCaptured)
        {
            HandleRopeClipInput();
            HandlePitonTakeUpInput();
        }
        TickRopeTeam();
    }

    /// <summary>
    /// The piton rules follow the session: playing without the Climbot is no reason to refuse
    /// a piton, and a follower leaves the pitons to the leader. Outside a session the game
    /// decides alone.
    /// </summary>
    private void ApplyModeRules()
    {
        var inSession = _network.IsConnected && _network.IsHandshakeComplete;
        var rules = _state.ModeRules;
        var withoutClimbot = inSession
            && (rules.ExtraConstraints & CairnMultiplayer.Shared.ClimbConstraints.NoClimbot) != 0;
        var follower = inSession && rules.LeadAndFollow && RopeLinkState.IsLast(_network.LocalPlayerId);
        PitonRulesPatch.Update(withoutClimbot, follower);
        RopeInterop.RestoreOwnPitonsAfterReload = inSession && rules.LeadAndFollow;
    }

    /// <summary>
    /// The climbers carry the gear now: the last one on the rope takes the pitons out by hand.
    /// A climber on their own does both jobs, so they may take out any piton within reach.
    /// </summary>
    private void HandlePitonTakeUpInput()
    {
        if (!LeadAndFollow) return;
        var keyboard = Keyboard.current;
        var pressed = keyboard != null && keyboard[TakeUpPitonKey].wasPressedThisFrame;
        if (!pressed && !ModControllerInput.WasPressed(ControllerShortcut.TakeUpPiton))
            return;

        if (RopeLinkState.IsLeader(_network.LocalPlayerId))
        {
            _notify("You lead the rope: the last climber takes the pitons out.");
            return;
        }
        if (!LocalPlayerInterop.TryGetPose(out var localPos, out _)) return;

        var refusal = RopeInterop.TryTakeUpClosestPiton(localPos,
            RopeInterop.PitonTakeUpReachMeters(FallbackTakeUpReachMeters));
        _notify(refusal ?? "Piton taken out and stowed.");
    }

    /// <summary>
    /// Hard exits break the logical link; transient loading and bivouacs release only native
    /// anchors so the link can survive scene streaming.
    /// </summary>
    private bool TickRopeSafety()
    {
        bool connected = _network.IsConnected && _network.IsHandshakeComplete;
        bool inGame = _state.LocalPlayerState == PlayerState.InGame;
        bool dead = PawnCaptureInterop.GetLocalPawnState() == NetFrame.PawnStateType.Dead;
        bool gameOver = GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _)
                        && lifecycle == CairnGameLifecycleState.GameOver;
        bool atMainMenu = SceneRoles.IsMainMenuArea(_state.CurrentScene);

        if (!dead) _deadSince = -1f;
        else if (_deadSince < 0f) _deadSince = Time.unscaledTime;
        // A climber down and waiting for help has not left the rope: Cairn's own rope teams
        // carry a corpse, and the partner pays for it in endurance. Only a death that plays
        // out breaks the link.
        bool downed = dead && (DeathScreenPatch.IsHoldingDeath
                               || Time.unscaledTime - _deadSince < DeathDecisionGraceSeconds);

        bool hardUnsafe = !connected || (dead && !downed) || gameOver || atMainMenu;

        if (hardUnsafe)
        {
            if (RopeInterop.HasRopeTeamAnchors)
                RopeInterop.ReleaseAllAnchors();

            if (!_ropeHardTornDown)
            {
                _ropeHardTornDown = true;
                string reason = !connected ? "disconnected"
                    : dead ? "local player died"
                    : gameOver ? "game over"
                    : "returned to main menu";
                TearDownAllLocalRopeLinks(reason);
            }
            return false;
        }

        _ropeHardTornDown = false;

        // The body keeps its link but not its anchors: a dead pawn is no harness to hang
        // the rope from. The partner's side still holds it.
        if ((downed || !inGame) && RopeInterop.HasRopeTeamAnchors)
            RopeInterop.ReleaseAllAnchors();
        return inGame && !downed;
    }

    private void TearDownAllLocalRopeLinks(string reason)
    {
        int self = _network.LocalPlayerId;

        var partners = new List<int>();
        foreach (var (a, b) in RopeLinkState.Links())
        {
            if (a == self) partners.Add(b);
            else if (b == self) partners.Add(a);
        }
        if (partners.Count == 0)
            return;

        bool canNotify = _network.IsConnected && _network.IsHandshakeComplete;
        foreach (var partner in partners)
        {
            if (canNotify)
                _network.SendRopeClip(partner, false);
            RopeLinkState.Apply(self, partner, false);
        }

        ModLog.Info($"[RopeCouple] Auto-unclipped {partners.Count} link(s): {reason}");
    }

    private void HandleRopeClipInput()
    {
        var keyboard = Keyboard.current;
        var keyboardPressed = keyboard != null && keyboard[ToggleKey].wasPressedThisFrame;
        if (!keyboardPressed && !ModControllerInput.WasPressed(ControllerShortcut.ToggleRope))
            return;

        // An existing rope must always be releasable, including at zero slack.
        int self = _network.LocalPlayerId;
        int linkedPartner = RopeLinkState.PartnerOf(self);
        if (linkedPartner >= 0)
        {
            _network.SendRopeClip(linkedPartner, false);
            RopeLinkState.Apply(self, linkedPartner, false);
            RopeInterop.ReleaseAllAnchors();
            return;
        }

        if (!LocalPlayerInterop.TryGetPose(out var localPos, out _))
            return;

        // The debug mirror has a negative id, so a separate flag avoids sentinel collisions.
        bool found = false;
        int bestId = 0;
        float bestDist = RopeClipRangeMeters;
        foreach (var kv in _network.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || rp.State != PlayerState.InGame) continue;
            var d = Vector3.Distance(localPos, new Vector3(rp.X, rp.Y, rp.Z));
            if (d < bestDist)
            {
                bestDist = d;
                bestId = kv.Key;
                found = true;
            }
        }

        if (!found)
        {
            int total = _network.RemotePlayers.Count;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _network.RemotePlayers)
            {
                var rp = kv.Value;
                if (rp == null) { sb.Append($" [{kv.Key}:null]"); continue; }
                float d = Vector3.Distance(localPos, new Vector3(rp.X, rp.Y, rp.Z));
                sb.Append($" [{kv.Key} state={rp.State} dist={d:F1}m hasFrame={rp.HasPlayerFrame}]");
            }
            ModLog.Info($"[RopeCouple] {ToggleKey} pressed — no eligible ghost within {RopeClipRangeMeters:F0}m " +
                $"(remote players: {total}):{(total == 0 ? " none" : sb.ToString())}");
            return;
        }

        if (RopeLinkState.IsLinked(self, bestId))
        {
            _network.SendRopeClip(bestId, false);
            ModLog.Info($"[RopeCouple] Unclip from player {bestId}");
            return;
        }

        int current = RopeLinkState.PartnerOf(self);
        if (current >= 0 && current != bestId)
            _network.SendRopeClip(current, false);
        _network.SendRopeClip(bestId, true);
        ModLog.Info($"[RopeCouple] Clip to player {bestId} at {bestDist:F1}m");
    }

    private void TickRopeTeam()
    {
        int self = _network.LocalPlayerId;
        int partner = RopeLinkState.PartnerOf(self);

        if (partner < 0 || !_network.RemotePlayers.TryGetValue(partner, out var remote)
            || remote == null || remote.State != PlayerState.InGame
            || !RemotePlayerManager.TryGetGhostHarness(partner, out var partnerAnchor))
        {
            if (RopeInterop.HasRopeTeamAnchors) RopeInterop.ReleaseAllAnchors();
            return;
        }

        if (!RopeInterop.UpdateRopeTeamAnchor(partner, partnerAnchor))
        {
            _network.SendRopeClip(partner, false);
            RopeLinkState.Apply(self, partner, false);
            RopeInterop.ReleaseAllAnchors();
            ModLog.Warning("[RopeCouple] Rope attachment was refused; the link has been released.");
        }
    }

    internal void ClearLinks()
    {
        RopeLinkState.Clear();
        RopeInterop.ReleaseAllAnchors();
        _ropeHardTornDown = false;
    }

    /// <summary>Logical links intentionally survive scene streaming and clear only on disconnect.</summary>
    internal void Reset() { }
}
