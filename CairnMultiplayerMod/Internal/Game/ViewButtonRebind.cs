using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Frees the controller's View button for CairnMP. Cairn binds its pause menu to it, and the
/// game reacts on the press itself — before the mod can hold its actions back — so every
/// View + button shortcut paused the game too. The game's View bindings move to the Menu
/// button instead (or are dropped where Menu already does the same job), through Unity's own
/// binding overrides: nothing in the game's files changes.
///
/// Applying this once was not enough. Cairn reloads its control settings — and with them its
/// binding overrides — after start-up and from its settings menu, which quietly put pause
/// back on View. So every action is checked again every second, Cairn's own set and anything
/// else enabled at the time, and a binding that is back on View is moved again.
///
/// Controllers are read through Unity's input system only — no Steam Input or Steam controller
/// profile is involved — so an Xbox pad behaves the same on a GOG copy as on Steam.
/// </summary>
internal static class ViewButtonRebind
{
    private const float RecheckSeconds = 1f;

    private static float _nextCheckAt;
    private static readonly HashSet<IntPtr> _seen = new();
    private static readonly HashSet<string> _logged = new();

    /// <summary>Cheap to call every frame: the actual check runs once a second.</summary>
    internal static void Ensure(bool enabled)
    {
        if (!enabled || Time.unscaledTime < _nextCheckAt) return;
        _nextCheckAt = Time.unscaledTime + RecheckSeconds;
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
                    if (map == null) continue;
                    var actions = map.actions;
                    for (var a = 0; a < actions.Count; a++) Free(actions[a]);
                }
            }

            // Anything else the game has enabled right now: menus can bring their own actions.
            var enabledActions = InputSystem.ListEnabledActions();
            if (enabledActions != null)
                for (var i = 0; i < enabledActions.Count; i++) Free(enabledActions[i]);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("input.view-rebind", exception);
        }
    }

    private static void Free(InputAction action)
    {
        if (action == null || !_seen.Add(action.Pointer)) return;

        var bindings = action.bindings;
        List<string> onView = null;
        for (var b = 0; b < bindings.Count; b++)
        {
            var binding = bindings[b];
            if (ViewButtonRule.IsViewButton(binding.effectivePath))
                (onView ??= new List<string>()).Add(binding.path);
        }
        if (onView == null) return;

        var newPath = ViewButtonRule.NewPath(MapUsesMenu(action.actionMap));
        foreach (var path in onView)
        {
            action.ApplyBindingOverride(newPath, null, path);
            var name = $"{action.actionMap?.name}/{action.name}";
            if (_logged.Add(name))
                ModLog.Info(newPath.Length == 0
                    ? $"[Input] Freed View for CairnMP: '{name}' no longer on {path} (Menu already does it)"
                    : $"[Input] Freed View for CairnMP: '{name}' moved from {path} to {newPath}");
            else
                ModLog.Info($"[Input] '{name}' was put back on View by the game; moved it off again");
        }
    }

    private static bool MapUsesMenu(InputActionMap map)
    {
        if (map == null) return false;
        var bindings = map.bindings;
        for (var b = 0; b < bindings.Count; b++)
            if (ViewButtonRule.IsMenuButton(bindings[b].effectivePath)) return true;
        return false;
    }
}
