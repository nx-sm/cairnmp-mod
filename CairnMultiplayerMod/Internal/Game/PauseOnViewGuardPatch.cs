using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// The last line behind <see cref="ViewButtonRebind"/>: a pause that the View button set off
/// is ignored, whatever binding brought it there. Unity tells the handler which physical
/// control fired, so the Menu button (and a keyboard's Escape) still pause exactly as before.
/// The rebinding alone left a gap — Cairn restores its bindings after start-up, and a press in
/// the moment before they were moved again paused the game.
/// </summary>
internal static class PauseOnViewGuardPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.PauseOnViewGuard");
    private static bool _loggedOnce;

    /// <summary>Follows <c>[Controller] FreeViewButton</c>.</summary>
    internal static Func<bool> Enabled { get; set; } = () => true;

    public static void Install()
    {
        var prefix = new HarmonyMethod(typeof(PauseOnViewGuardPatch), nameof(BeforePause));
        try
        {
            Harmony.Patch(AccessTools.Method(typeof(Il2CppCairn.UI.PauseMenu),
                nameof(Il2CppCairn.UI.PauseMenu.PauseAction_performed)), prefix: prefix);
            Harmony.Patch(AccessTools.Method(typeof(Il2CppTheGameBakers.Cairn.UI.CharacterMenu),
                nameof(Il2CppTheGameBakers.Cairn.UI.CharacterMenu.PauseAction_performed)), prefix: prefix);
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[Input] View-button pause guard unavailable: " + ex.Message);
        }
    }

    public static void Uninstall() => Harmony.UnpatchSelf();

    /// <returns>False to swallow a pause that came from the View button.</returns>
    private static bool BeforePause(InputAction.CallbackContext obj)
    {
        try
        {
            if (!Enabled()) return true;
            var path = obj.control?.path;
            if (string.IsNullOrEmpty(path) || !path.EndsWith("/select", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                ModLog.Info($"[Input] Ignored a pause from the View button ({path}); Menu still pauses.");
            }
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("input.pause-view-guard", exception);
            return true;
        }
    }
}
