using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Applies <see cref="PitonPlacementRule"/> to the game's two checks before a piton goes in:
/// the climbing module's and the piton item's. The game's refusal message follows the reason
/// it is handed, so a follower hears the game's own "not the rope head" answer.
/// </summary>
internal static class PitonRulesPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.PitonRules");
    private static bool _installed;
    private static bool _withoutClimbot;
    private static bool _follower;

    /// <summary>Set every frame by the rope controller.</summary>
    internal static void Update(bool withoutClimbot, bool follower)
    {
        _withoutClimbot = withoutClimbot;
        _follower = follower;
    }

    public static void Install()
    {
        if (_installed) return;
        try
        {
            Harmony.Patch(
                AccessTools.Method(typeof(ClimbingV2PawnControllerModulePitonExecution),
                    nameof(ClimbingV2PawnControllerModulePitonExecution.CanAddPiton)),
                postfix: new HarmonyMethod(typeof(PitonRulesPatch), nameof(AfterCanAddPiton)));
            Harmony.Patch(
                AccessTools.Method(typeof(ClimbingGearItem), nameof(ClimbingGearItem.CanUsePiton)),
                postfix: new HarmonyMethod(typeof(PitonRulesPatch), nameof(AfterCanUsePiton)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[PitonRules] Piton rules unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _withoutClimbot = false;
        _follower = false;
    }

    private static void AfterCanAddPiton(
        ref ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason reason, ref bool __result)
        => Apply(ref reason, ref __result);

    private static void AfterCanUsePiton(
        ref ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason reason, ref bool __result)
        => Apply(ref reason, ref __result);

    private static void Apply(
        ref ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason reason, ref bool allowed)
    {
        if (!_withoutClimbot && !_follower) return;
        try
        {
            allowed = PitonPlacementRule.Decide(allowed, (int)reason, _withoutClimbot, _follower, out var adjusted);
            reason = (ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason)adjusted;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-rules", exception);
        }
    }
}
