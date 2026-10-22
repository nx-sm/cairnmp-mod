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

    internal const string TakeUpPitonKeyName = "P";
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
        var canAct = TickRopeSafety();

        // Drawn on every screen, a downed partner's rope included.
        if (_state.LocalPlayerState == PlayerState.InGame) RopeVisuals.Update(_network.LocalPlayerId);
        else RopeVisuals.HideAll();

        if (!canAct) return;

        if (!InputCaptureState.IsKeyboardCaptured)
        {
            HandleRopeClipInput();
            HandlePitonTakeUpInput();
        }
    }

    /// <summary>
    /// The piton rules follow the session: playing without the Climbot is no reason to refuse
    /// a piton. Outside a session the game decides alone.
    /// </summary>
    private void ApplyModeRules()
    {
        var inSession = _network.IsConnected && _network.IsHandshakeComplete;
        var rules = _state.ModeRules;
        PitonRulesPatch.WithoutClimbot = inSession
            && (rules.ExtraConstraints & CairnMultiplayer.Shared.ClimbConstraints.NoClimbot) != 0;
        RopeInterop.RestoreOwnPitonsAfterReload = inSession && rules.LeadAndFollow;
    }

    /// <summary>
    /// The climbers carry the gear now that there is no Climbot: anyone takes a piton out by
    /// hand, whoever placed it and whoever is ahead.
    /// </summary>
    private void HandlePitonTakeUpInput()
    {
        if (!LeadAndFollow) return;
        var keyboard = Keyboard.current;
        var pressed = keyboard != null && keyboard[TakeUpPitonKey].wasPressedThisFrame;
        if (!pressed && !ModControllerInput.WasPressed(ControllerShortcut.TakeUpPiton))
            return;
        if (!LocalPlayerInterop.TryGetPose(out var localPos, out _)) return;

        var refusal = RopeInterop.TryTakeUpClosestPiton(localPos,
            RopeInterop.PitonTakeUpReachMeters(FallbackTakeUpReachMeters));
        if (refusal != null) ModLog.Info("[Piton] Not taken out: " + refusal);
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

    internal void ClearLinks()
    {
        RopeLinkState.Clear();
        RopeVisuals.Clear();
        _ropeHardTornDown = false;
    }

    /// <summary>Logical links intentionally survive scene streaming and clear only on disconnect.</summary>
    internal void Reset() { }
}
