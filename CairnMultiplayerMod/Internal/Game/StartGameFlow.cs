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
    private const float CoopSaveTimeoutSeconds = 30f;

    private bool _coopSavePending;
    private float _coopSaveWaited;

    // Always enter through Cairn's save manager so players can resume an existing save.
    // Choosing an empty slot continues through the native new-game/difficulty flow, where
    // Free Roam is already made available by FreeRoamUnlockPatch.
    internal static MainMenuInterop.MainMenuStep MultiplayerLaunchEntryStep =>
        MainMenuInterop.MainMenuStep.StoryModeManageSave;

    /// <param name="isHost">The local player hosts: they choose the settings on the game's own
    /// screens, and the others receive them.</param>
    /// <param name="announceLaunch">Host only: once its own game is launching, starts everyone
    /// else's with the host's final choices.</param>
    internal StartGameFlow(
        IMultiplayerPanel panel,
        Action restoreMainMenuInput,
        RuntimeState state,
        Func<bool> isHost = null,
        Action<ServerStartGame, ServerHostGameSettings> announceLaunch = null)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _restoreMainMenuInput = restoreMainMenuInput
            ?? throw new ArgumentNullException(nameof(restoreMainMenuInput));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _isHost = isHost ?? (() => false);
        _announceLaunch = announceLaunch ?? ((_, _) => { });
    }

    /// <summary>How long a joiner waits for the host's settings before deciding without them.</summary>
    private const float HostSettingsWaitSeconds = 20f;

    private readonly Func<bool> _isHost;
    private readonly Action<ServerStartGame, ServerHostGameSettings> _announceLaunch;
    private ServerHostGameSettings? _hostSettings;
    private ServerStartGame _hostStart;
    private bool _hostAnnounced;
    private bool _waitingNoticeShown;

    /// <summary>A joining player receives what the host chose; their own new game uses it.</summary>
    internal void OnHostSettings(ServerHostGameSettings settings)
    {
        if (_isHost()) return;
        _hostSettings = settings;
        GameOptionsInterop.ApplyHostOptions(settings, verbose: true);
    }

    /// <summary>
    /// The host pressed Start: only its own game goes to the save and New Game screens. The
    /// others stay in the lobby until the host's game launches and announces the start.
    /// </summary>
    internal void BeginAsHost(ServerStartGame start)
    {
        _hostStart = start;
        _hostAnnounced = false;
        BeginLaunch(start);
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

    /// <summary>
    /// The start signal from the lobby. A joiner launches on it; the host has already launched
    /// and only hears its own announcement coming back.
    /// </summary>
    internal void Begin(ServerStartGame pkt)
    {
        if (_isHost()) return;
        BeginLaunch(pkt);
    }

    private void BeginLaunch(ServerStartGame pkt)
    {
        _restoreMainMenuInput();
        _pendingStart = pkt;
        // A joiner's settings may already be here: the host sends them with the start.
        if (_isHost()) _hostSettings = null;
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
                if (_isHost())
                {
                    // The host chooses on the game's own screens: only the tutorials are ours
                    // to switch off. What they pick goes to everyone once the game launches.
                    GameOptionsInterop.ForceSkipTutorials();
                    AnnounceNewGameOnLaunch();
                }
                // A joiner plays what the host chose, re-applied every frame because the native
                // New Game flow rewrites these options; until it arrives, the mode's defaults.
                else if (_hostSettings.HasValue)
                {
                    GameOptionsInterop.ApplyHostOptions(_hostSettings.Value);
                }
                else if (!GameOptionsInterop.SetNextGameMode(RulesOf(s), CoopSkipsTutorials,
                             CoopSkipsTutorials, s.AssistEnabled, verbose: false))
                {
                    GameOptionsInterop.SetNextGameSkipOptions(
                        CoopSkipsTutorials, CoopSkipsTutorials, s.AssistEnabled, verbose: false);
                }
                SkipNewGameQuestions();
            }
            else
            {
                // Left the menu without a new game: the host continued its saved run.
                if (_isHost()) Announce(newGame: false);
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
                _waitingNoticeShown = false;
                // Every co-op launch, host or joiner, any mode, uses the co-op slot and only it.
                _coopSavePending = true;
                _coopSaveWaited = 0f;
                _panel.Hide();
            }
        }

        TickCoopSave(currentScene);
    }

    /// <summary>
    /// The host's new game is launching: what its screens left in the options is final, and
    /// goes to every joined player once.
    /// </summary>
    private void AnnounceNewGameOnLaunch()
    {
        if (_hostAnnounced || !MainMenuInterop.TryGetCurrentStep(out var step)
            || step != MainMenuInterop.MainMenuStep.LaunchNewStoryGame) return;
        Announce(newGame: true);
    }

    /// <summary>Starts everyone else's game, once, with what the host's own game launched with.</summary>
    private void Announce(bool newGame)
    {
        if (_hostAnnounced) return;
        _hostAnnounced = true;
        GameOptionsInterop.TryReadNextGameOptions(out var settings);
        settings.NewGame = newGame;
        _announceLaunch(_hostStart, settings);
        ModLog.Info(newGame
            ? "[StartGame] The host's new game is launching: starting everyone else's with its settings"
            : "[StartGame] The host continued its run: everyone else continues theirs");
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

        // Only a joiner's launch meant to start a new run skips the questions: the host
        // answers them, and one that was continuing a run, and failed, leaves the player in
        // charge of the screens.
        if (_isHost() || _skippedQuestions || _resumeFailed || _resumeStage > 0
            || step == MainMenuInterop.MainMenuStep.LaunchNewStoryGame) return;

        // Not before the host has chosen: the joiner waits on the difficulty screen.
        if (!_hostSettings.HasValue)
        {
            if (!_waitingNoticeShown)
            {
                _waitingNoticeShown = true;
                _panel.SetStatus("Waiting for the host to choose the difficulty...", true);
                ModLog.Info("[StartGame] Waiting for the host's settings before starting the new game");
            }
            return;
        }

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

        // A joiner follows the host: a new run if the host started one, its own copy of the
        // run if the host continued. The host's word travels with the start; give it a moment.
        if (!_isHost() && !_hostSettings.HasValue && _coopSaveWaited < HostSettingsWaitSeconds)
        {
            if (!_waitingNoticeShown)
            {
                _waitingNoticeShown = true;
                _panel.SetStatus("Waiting for the host's game to start...", true);
            }
            return;
        }

        _coopSavePending = false;
        CoopSaveInterop.RememberSoloActiveSlots();
        CoopSaveInterop.BackupSaves();
        CoopSaveInterop.LogSaves(screen);
        var plan = PlanForThisLaunch();
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
                // The co-op slot only ever holds the co-op run: the previous one is archived,
                // single-player slots are never touched.
                if (CoopSaveInterop.HasSave(plan.Slot)) CoopSaveInterop.ArchiveCoopSlot();
                if (!CoopSaveInterop.StartNewGame(screen, plan.Slot))
                    _panel.SetStatus("Could not start the co-op run in its own slot. Quit and send the log.", true);
                break;
            default:
                _panel.SetStatus("Pick the save to play on the save screen.", true);
                ModLog.Warning("[CoopSave] No save chosen automatically; leaving the choice to the player.");
                break;
        }
    }

    /// <summary>
    /// What this launch does with the saves. The host's own rope-team launch continues its run
    /// or starts one. A joiner starts a new run in a free slot when the host did, continues its
    /// rope team's run when the host continued, and otherwise leaves the save screen to them.
    /// </summary>
    private CoopSavePlan PlanForThisLaunch()
    {
        const byte coopSlot = CoopSaveFiles.Slot;
        var hostStartedANewRun = !_isHost() && _hostSettings?.NewGame == true;
        if (!hostStartedANewRun && CoopSaveInterop.HasSave(coopSlot))
            return CoopSavePlan.Decide(coopSlot, _ => true, null);
        return CoopSavePlan.Decide(-1, _ => false, coopSlot);
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
