using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.UI;
using Il2Cpp;
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
        _continuedRunSettings = null;
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
        _continuedRunSettings = null;
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
                // Left the menu without a new game: the host continued its saved run. Its
                // difficulty is the save's, readable once the run is loaded.
                if (_isHost() && !_hostAnnounced)
                {
                    GameOptionsInterop.TryReadNextGameOptions(out var menuSettings);
                    _continuedRunSettings = menuSettings;
                }
                _forceNewGameOpts = null;
            }
        }

        TickContinuedRunAnnounce();

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
                _state.ModeRules = rules;

                _forceNewGameOpts = start;

                MainMenuInterop.ForceMainMenuStep(MultiplayerLaunchEntryStep);
                ModLog.Info("[StartGame] Opened native save selection — player chooses an existing or new save (options re-applied each frame)");

                _pendingStart = null;
                _pendingStartRetries = 0;
                _pendingStartRetryTimer = 0f;
                _waitingNoticeShown = false;
                // Every player picks their own slot, once the save screen is up.
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

    // The host continued a run: what its menu held, until the loaded run's own setup replaces it.
    private ServerHostGameSettings? _continuedRunSettings;

    /// <summary>
    /// The host continued a saved run: once it is in the game, the run's own difficulty goes to
    /// everyone, so a joiner starting a new slot plays the host's run's difficulty, not the
    /// mode's default. The joiners stay in the lobby until then.
    /// </summary>
    private void TickContinuedRunAnnounce()
    {
        if (!_continuedRunSettings.HasValue) return;
        if (_hostAnnounced || !_isHost())
        {
            _continuedRunSettings = null;
            return;
        }
        if (_state.LocalPlayerState != PlayerState.InGame) return;

        var settings = _continuedRunSettings.Value;
        _continuedRunSettings = null;
        if (GameOptionsInterop.TryReadCurrentRunSettings(ref settings))
            ModLog.Info($"[StartGame] The host's run is {(DifficultyTweakables.SelectedDifficulty)settings.Difficulty}: " +
                        "that difficulty goes to everyone");
        else
            ModLog.Warning("[StartGame] Could not read the host's run difficulty; joiners' new runs use the mode's");
        Announce(newGame: false, settings);
    }

    /// <summary>Starts everyone else's game, once, with what the host's own game launched with.</summary>
    private void Announce(bool newGame) => Announce(newGame,
        GameOptionsInterop.TryReadNextGameOptions(out var settings) ? settings : default);

    private void Announce(bool newGame, ServerHostGameSettings settings)
    {
        if (_hostAnnounced) return;
        _hostAnnounced = true;
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

        // Only a joiner's new run skips the questions: the host answers them.
        if (_isHost() || _skippedQuestions
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
    /// Every player chooses their own slot on the save screen; slots are not synced, so the
    /// rope team agrees on them. A loaded slot plays as saved. A new slot is the host's to set
    /// up: a joiner's skips the difficulty screens and launches with the host's settings.
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

        // The host's settings travel just ahead of the start; give them a moment.
        if (!_isHost() && !_hostSettings.HasValue && _coopSaveWaited < HostSettingsWaitSeconds)
        {
            if (!_waitingNoticeShown)
            {
                _waitingNoticeShown = true;
                _panel.SetStatus("Waiting for the host's settings...", true);
            }
            return;
        }

        _coopSavePending = false;
        CoopSaveInterop.RememberSoloActiveSlots();
        CoopSaveInterop.BackupSaves();
        CoopSaveInterop.LogSaves(screen);
        _panel.SetStatus(_isHost()
            ? "Choose a save to continue, or an empty slot for a new run."
            : "Choose your save to continue, or an empty slot: the host's difficulty applies.", true);
        ModLog.Info("[CoopSave] Save screen left to the player");
    }
}
