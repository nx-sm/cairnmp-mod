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
        _resumeStage = 0;
        _resumeFailed = false;
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
                if (!GameOptionsInterop.SetNextGameMode(RulesOf(s), CoopSkipsTutorials,
                        CoopSkipsTutorials, s.AssistEnabled, verbose: false))
                {
                    GameOptionsInterop.SetNextGameSkipOptions(
                        CoopSkipsTutorials, CoopSkipsTutorials, s.AssistEnabled, verbose: false);
                }
                SkipNewGameQuestions();
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
                if (!GameOptionsInterop.SetNextGameMode(rules, CoopSkipsTutorials,
                        CoopSkipsTutorials, start.AssistEnabled))
                {
                    GameOptionsInterop.SetNextGameSkipOptions(
                        CoopSkipsTutorials, CoopSkipsTutorials, start.AssistEnabled);
                }
                _skippedQuestions = false;
                _resumeFailed = false;
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

    /// <summary>A co-op run never plays the tutorials, whatever the host's start message says.</summary>
    private const bool CoopSkipsTutorials = true;

    private bool _skippedQuestions;

    /// <summary>
    /// The host already chose the mode, and with it the difficulty; the start message carries
    /// it to everyone. The game's own New Game flow would still ask each player again —
    /// difficulty, customisation, tutorials — so those screens are passed straight through:
    /// the options are already written, the next step launches with them.
    /// </summary>
    private void SkipNewGameQuestions()
    {
        if (!MainMenuInterop.TryGetCurrentStep(out var step)) return;
        if (step is not (MainMenuInterop.MainMenuStep.DifficultySelect
            or MainMenuInterop.MainMenuStep.DifficultyCustomization
            or MainMenuInterop.MainMenuStep.ActivateTutorials
            or MainMenuInterop.MainMenuStep.LaunchNewStoryGame)) return;

        // A new game is under way, whoever started it: it must land in a slot holding nothing.
        // Cairn writes it into the active slot, and the game cannot see co-op saves to warn.
        if (CoopSaveInterop.ActiveSlotHoldsASave()) CoopSaveInterop.PointAtFreeSlot();

        // Only a launch meant to start a new run skips the questions. One that was continuing
        // a run, and failed, leaves the player in charge of the screens.
        if (_skippedQuestions || _resumeFailed || _resumeStage > 0
            || step == MainMenuInterop.MainMenuStep.LaunchNewStoryGame) return;

        _skippedQuestions = true;
        MainMenuInterop.ForceMainMenuStep(MainMenuInterop.MainMenuStep.LaunchNewStoryGame);
        ModLog.Info($"[StartGame] Skipped the game's {step} screen: the host's settings apply, tutorials off");
    }

    /// <summary>
    /// A rope team continues its own run: once the save screen is up, the remembered slot is
    /// loaded, or a new game is started in a free one. Anything that does not work out leaves
    /// the screen to the player, exactly as before.
    /// </summary>
    private void TickCoopSave(string currentScene)
    {
        TickResumeWatch(currentScene);
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
        CoopSaveInterop.BackupSaves();
        CoopSaveInterop.LogSaves(screen);
        var plan = CoopSavePlan.Decide(_coopSlot(), CoopSaveInterop.HasSave, CoopSaveInterop.FindFreeSlot());
        switch (plan.Action)
        {
            case CoopSaveAction.Resume:
                // Watched either way: a "continue" that throws or quietly does nothing gets the
                // direct load next, never a new game.
                _resumeSlot = plan.Slot;
                _resumeFailed = false;
                if (CoopSaveInterop.Resume(screen, plan.Slot))
                {
                    _resumeStage = 1;
                    _resumeWaited = 0f;
                }
                else ResumeDirectlyOrStop();
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

    /// <summary>How long a load may take to leave the menu before the next way is tried.</summary>
    private const float ResumeStageSeconds = 5f;

    private byte _resumeSlot;
    private int _resumeStage;
    private float _resumeWaited;
    private bool _resumeFailed;

    /// <summary>
    /// Leaving the menu means the load took. Still there after a few seconds: the direct load,
    /// then a plain message. Nothing in here ever starts a new game.
    /// </summary>
    private void TickResumeWatch(string currentScene)
    {
        if (_resumeStage == 0) return;
        if (!SceneRoles.IsMainMenuArea(currentScene) || _state.LocalPlayerState == PlayerState.Loading)
        {
            _resumeStage = 0;
            return;
        }

        _resumeWaited += Time.unscaledDeltaTime;
        if (_resumeWaited < ResumeStageSeconds) return;

        if (_resumeStage == 1)
        {
            ModLog.Warning($"[CoopSave] Still on the menu after continuing slot {CoopSaveInterop.Letter(_resumeSlot)}; loading it directly");
            ResumeDirectlyOrStop();
            return;
        }
        StopResuming();
    }

    private void ResumeDirectlyOrStop()
    {
        if (CoopSaveInterop.ResumeDirectly(_resumeSlot))
        {
            _resumeStage = 2;
            _resumeWaited = 0f;
            return;
        }
        StopResuming();
    }

    /// <summary>
    /// The run could not be loaded. The game is pointed back at a free slot before anything
    /// else can happen: a new game started from here can no longer land on the run.
    /// </summary>
    private void StopResuming()
    {
        _resumeStage = 0;
        _resumeFailed = true;
        CoopSaveInterop.PointAtFreeSlot();
        var letter = CoopSaveInterop.Letter(_resumeSlot);
        ModLog.Warning($"[CoopSave] Could not load the rope team's run in slot {letter}. Its save file is untouched.");
        _panel.SetStatus($"Could not load your co-op save (slot {letter}). It is untouched; quit and send the log.", true);
    }
}
