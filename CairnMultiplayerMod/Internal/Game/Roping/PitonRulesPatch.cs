using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Applies <see cref="PitonPlacementRule"/> to the game's two checks before a piton goes in:
/// the climbing module's and the piton item's.
/// </summary>
internal static class PitonRulesPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.PitonRules");
    private static bool _installed;

    /// <summary>Set every frame by the rope controller.</summary>
    internal static bool WithoutClimbot { get; set; }

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
        WithoutClimbot = false;
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
        if (!WithoutClimbot) return;
        try
        {
            allowed = PitonPlacementRule.Decide(allowed, (int)reason, WithoutClimbot, out var adjusted);
            reason = (ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason)adjusted;
            ReportRefusal(allowed, adjusted);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-rules", exception);
        }
    }

    private static int _lastReportedRefusal = -1;

    /// <summary>The game asks every frame; only a new reason is worth a log line.</summary>
    private static void ReportRefusal(bool allowed, int reasons)
    {
        var current = allowed ? PitonPlacementRule.Usable : reasons;
        if (current == _lastReportedRefusal) return;
        _lastReportedRefusal = current;
        if (!allowed)
            ModLog.Info("[PitonRules] The game refuses a piton: "
                + (ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason)reasons);
    }
}
