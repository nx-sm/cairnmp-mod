using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.UI;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Keeps launch on Cairn's native save-selection path so every player owns their save choice.
/// </summary>
internal sealed class StartGameFlow
{
    private readonly IMultiplayerPanel _panel;
    private readonly Action _restoreMainMenuInput;
    private readonly RuntimeState _state;

    private ServerStartGame? _pendingStart;
    private float _pendingStartRetryTimer;
    private int _pendingStartRetries;

    // Cairn overwrites newGameOptions after the New Game click, so keep the host's flags applied
    // until loading begins.
    private ServerStartGame? _forceNewGameOpts;

    /// <summary>How long the save screen may take to open before the choice is left to the player.</summary>
    private const float CoopSaveTimeoutSeconds = 10f;

    private readonly Func<int> _coopSlot;
    private readonly Action<int> _rememberCoopSlot;
    private bool _coopSavePending;
    private float _coopSaveWaited;

    // Always enter through Cairn's save manager so players can resume an existing save.
    // Choosing an empty slot continues through the native new-game/difficulty flow, where
    // Free Roam is already made available by FreeRoamUnlockPatch.
    internal static MainMenuInterop.MainMenuStep MultiplayerLaunchEntryStep =>
        MainMenuInterop.MainMenuStep.StoryModeManageSave;

    /// <param name="coopSlot">The story slot this machine's rope-team run lives in, or -1.</param>
    /// <param name="rememberCoopSlot">Stores the slot a new rope-team run was started in.</param>
    internal StartGameFlow(
        IMultiplayerPanel panel,
        Action restoreMainMenuInput,
        RuntimeState state,
        Func<int> coopSlot = null,
        Action<int> rememberCoopSlot = null)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _restoreMainMenuInput = restoreMainMenuInput
            ?? throw new ArgumentNullException(nameof(restoreMainMenuInput));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _coopSlot = coopSlot ?? (() => -1);
        _rememberCoopSlot = rememberCoopSlot ?? (_ => { });
    }

    internal bool HasPending => _pendingStart.HasValue;

    private static MultiplayerModeRules RulesOf(ServerStartGame start)
        => MultiplayerModes.Parse(start.Mode);

    internal void Cancel()
    {
        _pendingStart = null;
        _forceNewGameOpts = null;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 0f;
        _coopSavePending = false;
    }

    internal void Begin(ServerStartGame pkt)
    {
        _restoreMainMenuInput();
        _pendingStart = pkt;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 1.0f;
        _panel.SetStatus("Opening save selection...", true);
    }

    internal void Tick()
    {
        var currentScene = _state.CurrentScene;

        if (_forceNewGameOpts.HasValue)
        {
            if (SceneRoles.IsMainMenuArea(currentScene))
            {
                var s = _forceNewGameOpts.Value;
                // The lobby's mode decides the difficulty, so it is re-applied every frame:
                // the native New Game flow rewrites these options when the player walks
                // through it, and the last write before the launch is the one that counts.
                if (!GameOptionsInterop.SetNextGameMode(RulesOf(s), s.SkipTutorials,
                        s.SkipPractice, s.AssistEnabled, verbose: false))
                {
                    GameOptionsInterop.SetNextGameSkipOptions(
                        s.SkipTutorials, s.SkipPractice, s.AssistEnabled, verbose: false);
                }
            }
            else
            {
                _forceNewGameOpts = null;
            }
        }

        if (_pendingStart.HasValue && SceneRoles.IsMainMenuArea(currentScene))
        {
            // ForceStepTransition is consumed by MainMenu.Update, which the panel disables.
            _restoreMainMenuInput();

            _pendingStartRetryTimer += Time.unscaledDeltaTime;

            if (_pendingStartRetries < 3 && _pendingStartRetryTimer >= 0.5f)
            {
                _pendingStartRetryTimer = 0f;
                _pendingStartRetries++;
                MainMenuInterop.ForceMainMenuStep(MainMenuInterop.MainMenuStep.ModeSelect);
                ModLog.Info($"[StartGame] Force ModeSelect (retry #{_pendingStartRetries})");
            }
            else if (_pendingStartRetries == 3 && _pendingStartRetryTimer >= 0.5f)
            {
                var start = _pendingStart.Value;
                var rules = RulesOf(start);
                if (!GameOptionsInterop.SetNextGameMode(rules, start.SkipTutorials,
                        start.SkipPractice, start.AssistEnabled))
                {
                    GameOptionsInterop.SetNextGameSkipOptions(
                        start.SkipTutorials, start.SkipPractice, start.AssistEnabled);
                }
                _state.ModeRules = rules;

                _forceNewGameOpts = start;

                MainMenuInterop.ForceMainMenuStep(MultiplayerLaunchEntryStep);
                ModLog.Info("[StartGame] Opened native save selection — player chooses an existing or new save (options re-applied each frame)");

                _pendingStart = null;
                _pendingStartRetries = 0;
                _pendingStartRetryTimer = 0f;
                _coopSavePending = rules.LeadAndFollow;
                _coopSaveWaited = 0f;
                _panel.Hide();
            }
        }

        TickCoopSave(currentScene);
    }

    /// <summary>
    /// A rope team continues its own run: once the save screen is up, the remembered slot is
    /// loaded, or a new game is started in a free one. Anything that does not work out leaves
    /// the screen to the player, exactly as before.
    /// </summary>
    private void TickCoopSave(string currentScene)
    {
        if (!_coopSavePending) return;
        if (!SceneRoles.IsMainMenuArea(currentScene))
        {
            _coopSavePending = false;
            return;
        }

        _coopSaveWaited += Time.unscaledDeltaTime;
        if (_coopSaveWaited > CoopSaveTimeoutSeconds)
        {
            _coopSavePending = false;
            ModLog.Warning("[CoopSave] The save screen never became ready; choose a save yourself.");
            return;
        }
        if (!CoopSaveInterop.TryGetReadySaveScreen(out var screen)) return;

        _coopSavePending = false;
        var plan = CoopSavePlan.Decide(_coopSlot(), CoopSaveInterop.HasSave, CoopSaveInterop.FindFreeSlot());
        switch (plan.Action)
        {
            case CoopSaveAction.Resume:
                CoopSaveInterop.Resume(screen, plan.Slot);
                break;
            case CoopSaveAction.NewGame:
                if (CoopSaveInterop.StartNewGame(screen, plan.Slot)) _rememberCoopSlot(plan.Slot);
                break;
            default:
                _panel.SetStatus("Every story slot holds a save: pick one for the rope team.", true);
                ModLog.Warning("[CoopSave] No free story slot; leaving the choice to the player.");
                break;
        }
    }
}
