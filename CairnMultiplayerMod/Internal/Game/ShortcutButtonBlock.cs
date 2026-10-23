using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// While View is held, the game's own actions on the shortcut buttons are switched off, one
/// action at a time. Cairn's input switch (<c>disableInputs</c>) does not reach all of them:
/// the Climbot whistle on Y, rest, rewind and the Eagle Eye buttons kept firing alongside the
/// mod's shortcuts. The sticks are not touched, so the climber keeps moving and holding on.
///
/// An action already held when View goes down (hang mode on RT, say) is left alone, so
/// pressing View never lets go of anything. The check runs every frame while View is held,
/// because the game enables other action maps as the climber walks, climbs or opens a view.
/// On release every action switched off here is switched back on; the game's input context
/// is rebuilt right after, which settles which maps belong on.
/// </summary>
internal static class ShortcutButtonBlock
{
    private static readonly Dictionary<IntPtr, bool> _onShortcutButton = new();
    private static readonly List<InputAction> _disabled = new();
    private static readonly HashSet<IntPtr> _seen = new();
    private static bool _active;
    private static bool _logged;

    internal static void Apply(bool modifierHeld)
    {
        if (modifierHeld)
        {
            if (!_active)
            {
                _active = true;
                // Bindings can change between holds (control settings): work them out afresh.
                _onShortcutButton.Clear();
            }
            BlockEnabledActions();
        }
        else if (_active)
        {
            _active = false;
            Restore();
        }
    }

    private static void BlockEnabledActions()
    {
        try
        {
            _seen.Clear();
            var asset = InputInterop.FindInputManager()?.InputActions?.asset;
            if (asset != null)
            {
                var maps = asset.actionMaps;
                for (var m = 0; m < maps.Count; m++)
                {
                    var map = maps[m];
                    if (map == null || !map.enabled) continue;
                    var actions = map.actions;
                    for (var a = 0; a < actions.Count; a++) Block(actions[a]);
                }
            }

            // Anything else the game has enabled: menus and photo mode bring their own actions.
            var enabledActions = InputSystem.ListEnabledActions();
            if (enabledActions != null)
                for (var i = 0; i < enabledActions.Count; i++) Block(enabledActions[i]);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("input.shortcut-block", exception);
        }
    }

    private static void Block(InputAction action)
    {
        if (action == null || !_seen.Add(action.Pointer) || !action.enabled) return;
        if (!IsOnShortcutButton(action)) return;
        // Held before View went down: let it finish rather than drop it.
        if (action.phase != InputActionPhase.Waiting) return;

        action.Disable();
        _disabled.Add(action);
        if (!_logged)
        {
            _logged = true;
            ModLog.Info($"[Input] View held: the game's '{action.actionMap?.name}/{action.name}' and other actions " +
                        "on the shortcut buttons are off until it is released");
        }
    }

    private static bool IsOnShortcutButton(InputAction action)
    {
        if (_onShortcutButton.TryGetValue(action.Pointer, out var known)) return known;
        var found = false;
        var bindings = action.bindings;
        for (var b = 0; b < bindings.Count && !found; b++)
            found = ShortcutButtonRule.IsShortcutButton(bindings[b].effectivePath);
        _onShortcutButton[action.Pointer] = found;
        return found;
    }

    private static void Restore()
    {
        foreach (var action in _disabled)
        {
            try
            {
                action?.Enable();
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("input.shortcut-restore", exception);
            }
        }
        _disabled.Clear();
    }
}
