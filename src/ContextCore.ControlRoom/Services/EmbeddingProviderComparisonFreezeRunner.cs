using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>V3.10.F embedding provider comparison freeze；只读取报告，不启用正式向量检索，不切换 preview provider。</summary>
public sealed class EmbeddingProviderComparisonFreezeRunner
{
    private const double RecallThreshold = 0.80d;

    public EmbeddingProviderComparisonFreezeReport BuildFreezeReport(
        VectorQwen3ReadinessGateReport? qwen3Gate,
        VectorProviderComparisonV310Report? comparison,
        bool p15GatePassed,
        VectorProviderConfigurationSanityAuditReport? sanityAudit = null,
        string sanityAuditPath = "")
    {
        var blocked = new List<string>();
        var sanityPassed = sanityAudit?.Passed == true;
        var readinessGatePassed = qwen3Gate?.Passed ?? false;
        var a3Recall = qwen3Gate?.A3RecallAfterPolicy ?? 0;
        var extendedRecall = qwen3Gate?.ExtendedRecallAfterPolicy ?? 0;
        var riskAfterPolicy = qwen3Gate?.RiskAfterPolicy ?? 0;
        var formalOutputChanged = qwen3Gate?.FormalOutputChanged ?? 0;

        AddReasonIfFalse(blocked, sanityPassed, "ProviderConfigurationSanityAuditNotPassed");
        AddReasonIfFalse(blocked, readinessGatePassed, "ReadinessGateNotPassed");
        AddReasonIfFalse(blocked, riskAfterPolicy == 0, "RiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, a3Recall >= RecallThreshold, "A3RecallBelow80Percent");
        AddReasonIfFalse(blocked, extendedRecall >= RecallThreshold, "ExtendedRecallBelow80Percent");
        AddReasonIfFalse(blocked, formalOutputChanged == 0, "FormalOutputChangedNonZero");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        if (sanityAudit is not null)
        {
            blocked.AddRange(sanityAudit.BlockedReasons);
        }

        var passed = blocked.Count == 0;
        var configurationMismatch = !sanityPassed;
        return new EmbeddingProviderComparisonFreezeReport
        {
            Passed = passed,
            ProviderId = qwen3Gate?.ProviderId ?? "qwen3-embedding-0.6b-onnx",
            ModelId = qwen3Gate?.ModelId ?? "qwen3-embedding-0.6b",
            ProviderComparison = configurationMismatch ? "Inconclusive" : "Conclusive",
            ProviderConfigurationSanityPassed = sanityPassed,
            ProviderConfigurationSanityAuditPath = sanityAuditPath,
            ReadinessGatePassed = readinessGatePassed,
            A3RecallAfterPolicy = a3Recall,
            ExtendedRecallAfterPolicy = extendedRecall,
            RiskAfterPolicy = riskAfterPolicy,
            FormalOutputChanged = formalOutputChanged,
            PromotionStatus = configurationMismatch
                ? EmbeddingProviderPromotionStatuses.Inconclusive
                : passed
                ? EmbeddingProviderPromotionStatuses.PromoteCandidate
                : EmbeddingProviderPromotionStatuses.DoNotPromote,
            VectorV4RecheckAllowed = passed && !configurationMismatch,
            FormalRetrievalAllowed = false,
            VectorRetrievalStatus = configurationMismatch ? "PreviewOnly / BlockedByA3Recall" : "PreviewOnly",
            P15GatePassed = p15GatePassed,
            BlockedReasons = blocked,
            Diagnostics = configurationMismatch
                ? ["ProviderComparison=Inconclusive", "FormalRetrievalAllowed=false", "VectorV4RecheckAllowed=false"]
                : passed
                ? ["UseForRuntime=false", "FormalRetrievalAllowed=false", "PreviewShadowEvalOnly", "VectorRetrievalStillBlockedByA3Recall"]
                : ["EmbeddingProviderComparisonFreezeBlocked"],
            Recommendation = configurationMismatch
                ? "BlockedByProviderConfigurationMismatch"
                : passed
                ? EmbeddingProviderPromotionStatuses.PromoteCandidate
                : BuildRecommendation(blocked)
        };
    }

    public static string BuildMarkdown(EmbeddingProviderComparisonFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Embedding Provider Comparison Freeze (V3.10.F)");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- ProviderComparison: `{report.ProviderComparison}`");
        builder.AppendLine($"- ProviderConfigurationSanityPassed: `{report.ProviderConfigurationSanityPassed}`");
        builder.AppendLine($"- ProviderConfigurationSanityAuditPath: `{report.ProviderConfigurationSanityAuditPath}`");
        builder.AppendLine($"- ReadinessGatePassed: `{report.ReadinessGatePassed}`");
        builder.AppendLine($"- A3RecallAfterPolicy: `{report.A3RecallAfterPolicy:P2}`");
        builder.AppendLine($"- ExtendedRecallAfterPolicy: `{report.ExtendedRecallAfterPolicy:P2}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PromotionStatus: `{report.PromotionStatus}`");
        builder.AppendLine($"- VectorV4RecheckAllowed: `{report.VectorV4RecheckAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- VectorRetrievalStatus: `{report.VectorRetrievalStatus}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
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
            return "BlockedByFormalOutputChange";
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByRisk";
        }

        if (blocked.Contains("A3RecallBelow80Percent", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ExtendedRecallBelow80Percent", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByRecall";
        }

        if (blocked.Contains("ReadinessGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByReadinessGate";
        }

        return EmbeddingProviderPromotionStatuses.KeepCurrentProvider;
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
