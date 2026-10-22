using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Frees the controller's View button for CairnMP. Cairn binds it to one of its own menus,
/// and the game reacts on the press itself — before the mod can hold its actions back — so
/// every View + button shortcut opened that menu too. The game's View bindings move to the
/// Menu button instead, through Unity's own binding overrides: nothing in the game's files
/// changes, and the overrides last only while the game runs.
/// </summary>
internal static class ViewButtonRebind
{
    private static IntPtr _appliedTo;

    /// <summary>Applies once per input asset; cheap to call every frame.</summary>
    internal static void Ensure(bool enabled)
    {
        if (!enabled) return;
        try
        {
            var asset = InputInterop.FindInputManager()?.InputActions?.asset;
            if (asset == null || asset.Pointer == _appliedTo) return;
            _appliedTo = asset.Pointer;
            Apply(asset);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("input.view-rebind", exception);
        }
    }

    private static void Apply(InputActionAsset asset)
    {
        var maps = asset.actionMaps;
        var moved = 0;
        for (var m = 0; m < maps.Count; m++)
        {
            var map = maps[m];
            if (map == null) continue;

            var bindings = map.bindings;
            var usesMenu = false;
            var onView = new List<(string Action, string Path)>();
            for (var b = 0; b < bindings.Count; b++)
            {
                var binding = bindings[b];
                var path = binding.path;
                if (ViewButtonRule.IsMenuButton(path)) usesMenu = true;
                else if (ViewButtonRule.IsViewButton(path)) onView.Add((binding.action, path));
            }

            foreach (var (actionName, path) in onView)
            {
                var action = map.FindAction(actionName);
                if (action == null) continue;
                var newPath = ViewButtonRule.NewPath(usesMenu);
                action.ApplyBindingOverride(newPath, null, path);
                moved++;
                ModLog.Info(newPath.Length == 0
                    ? $"[Input] Freed View for CairnMP: '{map.name}/{action.name}' no longer on {path} (Menu already used in that map)"
                    : $"[Input] Freed View for CairnMP: '{map.name}/{action.name}' moved from {path} to {newPath}");
            }
        }
        if (moved == 0) ModLog.Info("[Input] No game action uses the View button; nothing to move.");
    }
}
