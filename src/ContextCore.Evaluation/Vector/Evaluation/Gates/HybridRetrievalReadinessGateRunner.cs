using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// hybrid retrieval readiness gate；对 shadow eval 结果套用硬规则。
/// FormalRetrievalAllowed 恒 false；任一条件不满足即 Passed=false。
/// </summary>
public sealed class HybridRetrievalReadinessGateRunner
{
    private const double RecallThreshold = 0.80d;

    /// <summary>基于 shadow eval 报告构建 readiness gate 结果。</summary>
    public HybridRetrievalReadinessGateReport BuildGateReport(
        HybridRetrievalPreviewReport? preview,
        bool policyViolationFound,
        bool p15GatePassed)
    {
        var blocked = new List<string>();
        var a3Full = preview?.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor);
        var extendedFull = preview?.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor);

        var a3Recall = a3Full?.RecallAfterPolicy ?? 0;
        var extendedRecall = extendedFull?.RecallAfterPolicy ?? 0;
        var risk = Math.Max(a3Full?.RiskAfterPolicy ?? 0, extendedFull?.RiskAfterPolicy ?? 0);
        var mustNotHitRisk = Math.Max(a3Full?.MustNotHitRiskAfterPolicy ?? 0, extendedFull?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Math.Max(a3Full?.LifecycleRiskAfterPolicy ?? 0, extendedFull?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = Math.Max(a3Full?.FormalOutputChanged ?? 0, extendedFull?.FormalOutputChanged ?? 0);

        AddReasonIfFalse(blocked, a3Recall >= RecallThreshold, "A3RecallBelow80Percent");
        AddReasonIfFalse(blocked, extendedRecall >= RecallThreshold, "ExtendedRecallBelow80Percent");
        AddReasonIfFalse(blocked, risk == 0, "RiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, mustNotHitRisk == 0, "MustNotHitRiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, lifecycleRisk == 0, "LifecycleRiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, formalOutputChanged == 0, "FormalOutputChangedNonZero");
        AddReasonIfFalse(blocked, !policyViolationFound, "PolicyViolationDetected");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        var passed = blocked.Count == 0;
        return new HybridRetrievalReadinessGateReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = passed,
            A3RecallAfterPolicy = a3Recall,
            ExtendedRecallAfterPolicy = extendedRecall,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = mustNotHitRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            PolicyViolationFound = policyViolationFound,
            P15GatePassed = p15GatePassed,
            FormalRetrievalAllowed = false,
            BlockedReasons = blocked,
            Diagnostics = passed
                ? ["UseForRuntime=false", "FormalRetrievalAllowed=false", "PreviewShadowEvalOnly"]
                : ["HybridRetrievalReadinessGateBlocked"],
            Recommendation = passed
                ? HybridRetrievalReadinessRecommendations.ReadyForVectorV4Recheck
                : BuildRecommendation(blocked)
        };
    }

    /// <summary>生成 readiness gate markdown 报告。</summary>
    public static string BuildMarkdown(HybridRetrievalReadinessGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Hybrid Retrieval Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- A3RecallAfterPolicy: `{report.A3RecallAfterPolicy:P2}`");
        builder.AppendLine($"- ExtendedRecallAfterPolicy: `{report.ExtendedRecallAfterPolicy:P2}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PolicyViolationFound: `{report.PolicyViolationFound}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Allowed", report.Allowed);
        AppendList(builder, "Forbidden", report.Forbidden);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static void AddReasonIfFalse(ICollection<string> reasons, bool condition, string reason)
    {
        if (!condition)
        {
            reasons.Add(reason);
        }
    }

    private static string BuildRecommendation(IReadOnlyCollection<string> blocked)
    {
        if (blocked.Contains("FormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRetrievalReadinessRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Contains("PolicyViolationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRetrievalReadinessRecommendations.BlockedByPolicyViolation;
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRetrievalReadinessRecommendations.BlockedByRisk;
        }

        if (blocked.Contains("A3RecallBelow80Percent", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ExtendedRecallBelow80Percent", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRetrievalReadinessRecommendations.BlockedByA3Recall;
        }

        return HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
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
