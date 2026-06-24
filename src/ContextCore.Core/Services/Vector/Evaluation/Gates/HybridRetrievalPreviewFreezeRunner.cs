using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// hybrid retrieval preview freeze gate；只冻结 preview 结论，不改变正式 retrieval / scoring / PackingPolicy / package output。
/// </summary>
public sealed class HybridRetrievalPreviewFreezeRunner
{
    private const double RecallThreshold = 0.80d;

    public HybridRetrievalPreviewFreezeReport BuildFreezeReport(
        HybridRetrievalReadinessGateReport? readinessGate,
        HybridRetrievalRecallRegressionAuditReport? regressionAudit,
        bool p15GatePassed)
    {
        var blocked = new List<string>();
        if (readinessGate is null)
        {
            blocked.Add("HybridReadinessGateMissing");
        }
        else if (!readinessGate.Passed && readinessGate.BlockedReasons.Count == 0)
        {
            blocked.Add("HybridReadinessGateBlockedWithoutReason");
        }

        if (regressionAudit is null)
        {
            blocked.Add("HybridRecallRegressionAuditMissing");
        }
        else
        {
            AddReasonIfFalse(blocked, regressionAudit.Passed, "HybridRecallRegressionAuditNotPassed");
            AddReasonIfFalse(blocked, regressionAudit.DenseCandidateDroppedCount == 0, "DenseCandidateDropped");
            AddReasonIfFalse(blocked, regressionAudit.EligibilityMismatchCount == 0, "EligibilityMismatch");
            AddReasonIfFalse(blocked, regressionAudit.DedupOverwriteCount == 0, "DedupOverwriteDetected");
            AddReasonIfFalse(blocked, !regressionAudit.UseForRuntime, "UseForRuntimeEnabled");
            AddReasonIfFalse(blocked, regressionAudit.FormalOutputChanged == 0, "AuditFormalOutputChangedNonZero");
            blocked.AddRange(regressionAudit.BlockedReasons);
        }

        var risk = readinessGate?.RiskAfterPolicy ?? 0;
        var formalOutputChanged = Math.Max(readinessGate?.FormalOutputChanged ?? 0, regressionAudit?.FormalOutputChanged ?? 0);
        var formalRetrievalAllowed = readinessGate?.FormalRetrievalAllowed ?? false;
        var a3Recall = regressionAudit?.HybridBestRecallA3 ?? readinessGate?.A3RecallAfterPolicy ?? 0;
        var extendedRecall = regressionAudit?.HybridBestRecallExtended ?? readinessGate?.ExtendedRecallAfterPolicy ?? 0;

        AddReasonIfFalse(blocked, risk == 0, "RiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, formalOutputChanged == 0, "FormalOutputChangedNonZero");
        AddReasonIfFalse(blocked, !formalRetrievalAllowed, "FormalRetrievalAllowed");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        if (readinessGate?.BlockedReasons is { Count: > 0 })
        {
            blocked.AddRange(readinessGate.BlockedReasons);
        }

        var safetyBlocked = blocked.Any(IsFreezeBlockingReason);
        var recallReady = a3Recall >= RecallThreshold && extendedRecall >= RecallThreshold;
        var freezePassed = !safetyBlocked;
        var v4RecheckAllowed = freezePassed
                               && recallReady
                               && readinessGate?.Passed == true
                               && risk == 0
                               && formalOutputChanged == 0
                               && !formalRetrievalAllowed;

        return new HybridRetrievalPreviewFreezeReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            HybridRetrievalStatus = HybridRetrievalReadinessRecommendations.KeepPreviewOnly,
            Recommendation = ResolveRecommendation(blocked, a3Recall, extendedRecall, freezePassed),
            LegacyDenseRecallA3 = regressionAudit?.LegacyDenseRecallA3 ?? 0,
            HybridDenseOnlyRecallA3 = regressionAudit?.HybridDenseOnlyRecallA3 ?? 0,
            HybridBestRecallA3 = a3Recall,
            LegacyDenseRecallExtended = regressionAudit?.LegacyDenseRecallExtended ?? 0,
            HybridDenseOnlyRecallExtended = regressionAudit?.HybridDenseOnlyRecallExtended ?? 0,
            HybridBestRecallExtended = extendedRecall,
            DenseCandidateDroppedCount = regressionAudit?.DenseCandidateDroppedCount ?? 0,
            EligibilityMismatchCount = regressionAudit?.EligibilityMismatchCount ?? 0,
            DedupOverwriteCount = regressionAudit?.DedupOverwriteCount ?? 0,
            RiskAfterPolicy = risk,
            FormalOutputChanged = formalOutputChanged,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            UseForRuntime = false,
            V4RecheckAllowed = v4RecheckAllowed,
            RequiredBeforeV4 = recallReady
                ? Array.Empty<string>()
                : ["RecallImprovementSourceRequired", "A3AndExtendedRecallAtLeast80Percent"],
            Notes =
            [
                "Current hybrid framework valid but ineffective for recall.",
                "Dense-only recall aligns with legacy dense baseline.",
                "Lexical / anchor union did not improve recall.",
                "Formal retrieval remains disabled."
            ],
            BlockedReasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public static string BuildMarkdown(HybridRetrievalPreviewFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Hybrid Retrieval Preview Freeze");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- HybridRetrievalStatus: `{report.HybridRetrievalStatus}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- LegacyDenseRecallA3: `{report.LegacyDenseRecallA3:P2}`");
        builder.AppendLine($"- HybridDenseOnlyRecallA3: `{report.HybridDenseOnlyRecallA3:P2}`");
        builder.AppendLine($"- HybridBestRecallA3: `{report.HybridBestRecallA3:P2}`");
        builder.AppendLine($"- LegacyDenseRecallExtended: `{report.LegacyDenseRecallExtended:P2}`");
        builder.AppendLine($"- HybridDenseOnlyRecallExtended: `{report.HybridDenseOnlyRecallExtended:P2}`");
        builder.AppendLine($"- HybridBestRecallExtended: `{report.HybridBestRecallExtended:P2}`");
        builder.AppendLine($"- DenseCandidateDroppedCount: `{report.DenseCandidateDroppedCount}`");
        builder.AppendLine($"- EligibilityMismatchCount: `{report.EligibilityMismatchCount}`");
        builder.AppendLine($"- DedupOverwriteCount: `{report.DedupOverwriteCount}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- V4RecheckAllowed: `{report.V4RecheckAllowed}`");
        AppendList(builder, "RequiredBeforeV4", report.RequiredBeforeV4);
        AppendList(builder, "Notes", report.Notes);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        return builder.ToString();
    }

    private static void AddReasonIfFalse(ICollection<string> reasons, bool condition, string reason)
    {
        if (!condition)
        {
            reasons.Add(reason);
        }
    }

    private static bool IsFreezeBlockingReason(string reason)
    {
        return reason is not ("A3RecallBelow80Percent" or "ExtendedRecallBelow80Percent");
    }

    private static string ResolveRecommendation(
        IReadOnlyCollection<string> blocked,
        double a3Recall,
        double extendedRecall,
        bool freezePassed)
    {
        if (!freezePassed)
        {
            if (blocked.Contains("FormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase)
                || blocked.Contains("AuditFormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase))
            {
                return HybridRetrievalReadinessRecommendations.BlockedByFormalOutputChange;
            }

            if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
            {
                return HybridRetrievalReadinessRecommendations.BlockedByRisk;
            }

            return HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
        }

        return a3Recall < RecallThreshold || extendedRecall < RecallThreshold
            ? HybridRetrievalReadinessRecommendations.BlockedByA3Recall
            : HybridRetrievalReadinessRecommendations.ReadyForVectorV4Recheck;
    }

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {label}");
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }
}
