using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn.Save;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Lifts the rope anchor off the wall before every way the game saves, so the invisible piton
/// is never written into a savegame. The rope controller puts it back on the next frame.
/// </summary>
internal static class RopeAnchorSavePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeAnchorSave");

    public static void Install()
    {
        var prefix = new HarmonyMethod(typeof(RopeAnchorSavePatch), nameof(BeforeSave));
        var patched = 0;
        foreach (var name in new[] { "Save", "AutoSave", "QuickSave", "SaveAndClose" })
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(SavegameManager)))
            {
                if (method.Name != name) continue;
                try
                {
                    Harmony.Patch(method, prefix: prefix);
                    patched++;
                }
                catch (Exception ex)
                {
                    ModLog.Warning($"[RopeAnchor] Could not guard {name} against the anchor: {ex.Message}");
                }
            }
        ModLog.Debug($"[RopeAnchor] Save guard on {patched} save entry point(s)");
    }

    public static void Uninstall() => Harmony.UnpatchSelf();

    private static void BeforeSave()
    {
        try { RopeAnchor.ReleaseForSave(); }
        catch (Exception exception) { ModLog.SuppressedException("rope.anchor-save", exception); }
    }
}
