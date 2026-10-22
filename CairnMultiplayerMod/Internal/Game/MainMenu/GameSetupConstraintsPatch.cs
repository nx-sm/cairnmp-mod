using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// Makes a lobby's extra constraints stick on every difficulty.
///
/// Cairn only reads the customized constraints of a difficulty that allows customization
/// (Free roam). Every other one — Alpinist included — answers with its own fixed defaults,
/// so the constraints written into the new-game options were silently dropped and a Rope team
/// run still got its Climbot. Adding them where the game computes a run's constraints lets
/// the game apply them itself: its robot spawner already removes the Climbot under
/// <c>NoClimbot</c>.
/// </summary>
internal static class GameSetupConstraintsPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.GameSetupConstraints");
    private static bool _installed;

    /// <summary>The constraints the current lobby adds, or none outside a lobby.</summary>
    internal static Func<ClimbConstraints> Extra { get; set; } = () => ClimbConstraints.None;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            Harmony.Patch(AccessTools.Method(typeof(GameSetup), nameof(GameSetup.GetConstraints)),
                postfix: new HarmonyMethod(typeof(GameSetupConstraintsPatch), nameof(AddLobbyConstraints)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[GameOptions] Lobby constraints cannot be enforced: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
    }

    private static void AddLobbyConstraints(ref GamemodeConstraints __result)
    {
        try
        {
            var extra = (GamemodeConstraints)(int)Extra();
            if (extra == 0 || (__result & extra) == extra) return;
            __result |= extra;
            ModLog.Info($"[GameOptions] Lobby constraints added to the run: {__result}");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("game-options.add-constraints", exception);
        }
    }
}
