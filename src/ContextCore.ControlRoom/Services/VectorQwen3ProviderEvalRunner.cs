using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>V3.10 Qwen3 provider 对比与 readiness gate；仅用于 preview/shadow/eval。</summary>
public sealed class VectorQwen3ProviderEvalRunner
{
    public VectorProviderComparisonV310Report BuildComparison(
        EmbeddingProviderSmokeReport? qwenSmoke,
        VectorQueryShadowEvalReport? currentA3,
        VectorQueryShadowEvalReport? currentExtended,
        VectorQueryShadowEvalReport? qwenA3,
        VectorQueryShadowEvalReport? qwenExtended,
        int indexedEntryCount,
        bool currentPgVectorParityPassed,
        bool qwenPgVectorParityPassed)
    {
        var providers = new List<VectorProviderComparisonV310Result>
        {
            BuildResult(
                "current",
                string.Empty,
                "current",
                "current",
                null,
                null,
                0,
                false,
                indexedEntryCount,
                currentA3,
                currentExtended,
                currentPgVectorParityPassed,
                []),
            BuildResult(
                "qwen3",
                qwenSmoke?.ProviderType ?? EmbeddingProviderTypes.OnnxLocal,
                qwenSmoke?.ProviderId ?? "qwen3-embedding-0.6b-onnx",
                qwenSmoke?.EmbeddingModel ?? "qwen3-embedding-0.6b",
                qwenSmoke?.ModelPath,
                qwenSmoke?.TokenizerPath,
                qwenSmoke?.ExpectedDimension ?? 1024,
                qwenSmoke?.UseForRuntime ?? false,
                indexedEntryCount,
                qwenA3,
                qwenExtended,
                qwenPgVectorParityPassed,
                BuildSmokeDiagnostics(qwenSmoke))
        };

        var diagnostics = new List<string>();
        if (qwenSmoke is null)
        {
            diagnostics.Add("Qwen3EmbeddingProviderSmokeMissing");
        }

        if (qwenA3 is null || qwenExtended is null)
        {
            diagnostics.Add("Qwen3ShadowEvalReportMissing");
        }

        var qwenResult = providers[1];
        return new VectorProviderComparisonV310Report
        {
            OperationId = $"vector-provider-comparison-v310-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Providers = providers,
            Diagnostics = diagnostics,
            Recommendation = qwenResult.Recommendation
        };
    }

    public VectorQwen3ReadinessGateReport BuildReadinessGate(
        EmbeddingProviderSmokeReport? smoke,
        VectorQueryShadowEvalReport? a3,
        VectorQueryShadowEvalReport? extended,
        bool pgVectorParityPassed,
        bool p15GatePassed,
        int projectionMismatchCount = 0)
    {
        var blocked = new List<string>();
        var providerCompatibilityPassed = smoke?.Succeeded == true;
        AddReasonIfFalse(blocked, providerCompatibilityPassed, "ProviderCompatibilityNotPassed");
        AddReasonIfFalse(blocked, a3 is not null, "A3ShadowEvalMissing");
        AddReasonIfFalse(blocked, extended is not null, "ExtendedShadowEvalMissing");

        var a3Recall = a3?.MustHitRecallAfterPolicy ?? 0;
        var extendedRecall = extended?.MustHitRecallAfterPolicy ?? 0;
        var riskAfterPolicy = (a3?.RiskAfterPolicy ?? 0) + (extended?.RiskAfterPolicy ?? 0);
        var mustNotHitRisk = Math.Max(a3?.MustNotHitRiskAfterPolicy ?? 0, extended?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Math.Max(a3?.LifecycleRiskAfterPolicy ?? 0, extended?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = (a3?.FormalOutputChanged ?? 0) + (extended?.FormalOutputChanged ?? 0);

        AddReasonIfFalse(blocked, a3Recall >= 0.80, "A3RecallBelow80Percent");
        AddReasonIfFalse(blocked, extendedRecall >= 0.80, "ExtendedRecallBelow80Percent");
        AddReasonIfFalse(blocked, riskAfterPolicy == 0, "RiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, mustNotHitRisk <= 0, "MustNotHitRiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, lifecycleRisk <= 0, "LifecycleRiskAfterPolicyNonZero");
        AddReasonIfFalse(blocked, formalOutputChanged == 0, "FormalOutputChangedNonZero");
        AddReasonIfFalse(blocked, projectionMismatchCount == 0, "ProjectionMismatchNonZero");
        AddReasonIfFalse(blocked, pgVectorParityPassed, "PgVectorFileSystemParityNotPassed");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        return new VectorQwen3ReadinessGateReport
        {
            OperationId = $"vector-qwen3-readiness-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Passed = blocked.Count == 0,
            ProviderId = smoke?.ProviderId ?? "qwen3-embedding-0.6b-onnx",
            ProviderType = smoke?.ProviderType ?? EmbeddingProviderTypes.OnnxLocal,
            ModelId = smoke?.EmbeddingModel ?? "qwen3-embedding-0.6b",
            ModelPath = smoke?.ModelPath,
            TokenizerPath = smoke?.TokenizerPath,
            Dimension = smoke?.ExpectedDimension ?? 1024,
            UseForRuntime = smoke?.UseForRuntime ?? false,
            ProviderCompatibilityPassed = providerCompatibilityPassed,
            A3RecallAfterPolicy = a3Recall,
            ExtendedRecallAfterPolicy = extendedRecall,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotHitRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            ProjectionMismatchCount = projectionMismatchCount,
            PgVectorFileSystemParityPassed = pgVectorParityPassed,
            P15GatePassed = p15GatePassed,
            FormalRetrievalAllowed = false,
            BlockedReasons = blocked,
            Diagnostics = BuildSmokeDiagnostics(smoke),
            Recommendation = BuildGateRecommendation(blocked)
        };
    }

    public static string BuildComparisonMarkdown(VectorProviderComparisonV310Report report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Provider Comparison V3.10");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Provider | Type | Model | Dim | Runtime | Indexed | A3 Recall | A3 MRR | A3 Risk | Extended Recall | Extended MRR | Extended Risk | PgVector Parity | Recommendation |");
        builder.AppendLine("|---|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---|---|");
        foreach (var provider in report.Providers)
        {
            builder.AppendLine(
                $"| {provider.ProviderId} | {provider.ProviderType} | {provider.ModelId} | {provider.Dimension} | {provider.UseForRuntime} | {provider.IndexedEntryCount} | {provider.A3RecallAfterPolicy:P2} | {provider.A3MrrAfterPolicy:F4} | {provider.A3RiskAfterPolicy} | {provider.ExtendedRecallAfterPolicy:P2} | {provider.ExtendedMrrAfterPolicy:F4} | {provider.ExtendedRiskAfterPolicy} | {provider.PgVectorParityPassed} | {provider.Recommendation} |");
        }

        AppendDiagnostics(builder, report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildReadinessGateMarkdown(VectorQwen3ReadinessGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Qwen3 Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ProviderType: `{report.ProviderType}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- ModelPath: `{report.ModelPath ?? "-"}`");
        builder.AppendLine($"- TokenizerPath: `{report.TokenizerPath ?? "-"}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- ProviderCompatibilityPassed: `{report.ProviderCompatibilityPassed}`");
        builder.AppendLine($"- A3RecallAfterPolicy: `{report.A3RecallAfterPolicy:P2}`");
        builder.AppendLine($"- ExtendedRecallAfterPolicy: `{report.ExtendedRecallAfterPolicy:P2}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy:P2}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy:P2}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- ProjectionMismatchCount: `{report.ProjectionMismatchCount}`");
        builder.AppendLine($"- PgVectorFileSystemParityPassed: `{report.PgVectorFileSystemParityPassed}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendDiagnostics(builder, report.BlockedReasons, "Blocked Reasons");
        AppendDiagnostics(builder, report.Diagnostics, "Diagnostics");
        return builder.ToString();
    }

    public static string BuildShadowMarkdown(string datasetName, VectorQueryShadowEvalReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Vector Qwen3 Shadow Eval - {datasetName}");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ProviderType: `{report.ProviderType}`");
        builder.AppendLine($"- EmbeddingModel: `{report.EmbeddingModel}`");
        builder.AppendLine($"- ModelPath: `{report.ModelPath ?? "-"}`");
        builder.AppendLine($"- TokenizerPath: `{report.TokenizerPath ?? "-"}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- RecallAfterPolicy: `{report.MustHitRecallAfterPolicy:P2}`");
        builder.AppendLine($"- MrrAfterPolicy: `{CalculateMrr(report):F4}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy:P2}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy:P2}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendDiagnostics(builder, report.Warnings, "Warnings");
        return builder.ToString();
    }

    private static VectorProviderComparisonV310Result BuildResult(
        string resultName,
        string providerType,
        string providerId,
        string modelId,
        string? modelPath,
        string? tokenizerPath,
        int dimension,
        bool useForRuntime,
        int indexedEntryCount,
        VectorQueryShadowEvalReport? a3,
        VectorQueryShadowEvalReport? extended,
        bool pgVectorParityPassed,
        IReadOnlyList<string> diagnostics)
    {
        var recommendation = BuildProviderRecommendation(a3, extended, pgVectorParityPassed, diagnostics);
        return new VectorProviderComparisonV310Result
        {
            ProviderId = string.Equals(resultName, "current", StringComparison.OrdinalIgnoreCase)
                ? providerId
                : providerId,
            ProviderType = providerType,
            ModelId = modelId,
            ModelPath = modelPath,
            TokenizerPath = tokenizerPath,
            Dimension = dimension,
            UseForRuntime = useForRuntime,
            IndexedEntryCount = indexedEntryCount,
            A3RecallAfterPolicy = a3?.MustHitRecallAfterPolicy ?? 0,
            A3MrrAfterPolicy = a3 is null ? 0 : CalculateMrr(a3),
            A3RiskAfterPolicy = a3?.RiskAfterPolicy ?? 0,
            A3MustNotHitRiskAfterPolicy = a3?.MustNotHitRiskAfterPolicy ?? 0,
            A3LifecycleRiskAfterPolicy = a3?.LifecycleRiskAfterPolicy ?? 0,
            A3FormalOutputChanged = a3?.FormalOutputChanged ?? 0,
            ExtendedRecallAfterPolicy = extended?.MustHitRecallAfterPolicy ?? 0,
            ExtendedMrrAfterPolicy = extended is null ? 0 : CalculateMrr(extended),
            ExtendedRiskAfterPolicy = extended?.RiskAfterPolicy ?? 0,
            QueryPreviewTopKOverlap = pgVectorParityPassed ? 1.0 : 0,
            PgVectorParityPassed = pgVectorParityPassed,
            Recommendation = recommendation,
            Diagnostics = diagnostics
        };
    }

    private static string BuildProviderRecommendation(
        VectorQueryShadowEvalReport? a3,
        VectorQueryShadowEvalReport? extended,
        bool pgVectorParityPassed,
        IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count > 0)
        {
            return "BlockedByProviderCompatibility";
        }

        if (a3 is null || extended is null)
        {
            return VectorQueryShadowRecommendations.KeepPreviewOnly;
        }

        if (a3.FormalOutputChanged + extended.FormalOutputChanged > 0)
        {
            return "BlockedByFormalOutputChange";
        }

        if (a3.RiskAfterPolicy + extended.RiskAfterPolicy > 0
            || a3.MustNotHitRiskAfterPolicy > 0
            || extended.MustNotHitRiskAfterPolicy > 0
            || a3.LifecycleRiskAfterPolicy > 0
            || extended.LifecycleRiskAfterPolicy > 0)
        {
            return "BlockedByRisk";
        }

        if (!pgVectorParityPassed)
        {
            return "BlockedByProjectionMismatch";
        }

        return a3.MustHitRecallAfterPolicy >= 0.80 && extended.MustHitRecallAfterPolicy >= 0.80
            ? "ReadyForVectorV4Recheck"
            : "BlockedByA3Recall";
    }

    private static string BuildGateRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Count == 0)
        {
            return "ReadyForVectorV4Recheck";
        }

        if (blocked.Contains("ProviderCompatibilityNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByProviderCompatibility";
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByRisk";
        }

        if (blocked.Contains("FormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByFormalOutputChange";
        }

        if (blocked.Contains("ProjectionMismatchNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("PgVectorFileSystemParityNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByProjectionMismatch";
        }

        return blocked.Contains("A3RecallBelow80Percent", StringComparer.OrdinalIgnoreCase)
            ? "BlockedByA3Recall"
            : VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static double CalculateMrr(VectorQueryShadowEvalReport shadow)
    {
        if (shadow.SampleResults.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var sample in shadow.SampleResults)
        {
            var eligible = sample.Candidates
                .Where(candidate => string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Rank)
                .ToArray();
            var rank = eligible
                .Select((candidate, index) => new { candidate, index })
                .FirstOrDefault(item => sample.MustHitMatchedAfterPolicy.Any(expected => EvalIdMatches(expected, item.candidate.ItemId)))
                ?.index + 1;
            total += rank is null ? 0 : 1.0 / rank.Value;
        }

        return total / shadow.SampleResults.Count;
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildSmokeDiagnostics(EmbeddingProviderSmokeReport? smoke)
    {
        if (smoke is null)
        {
            return ["EmbeddingProviderSmokeMissing"];
        }

        var diagnostics = smoke.Diagnostics
            .Select(item => $"{item.Type}: {item.Message}")
            .ToList();
        diagnostics.AddRange(smoke.Warnings);
        return diagnostics;
    }

    private static void AddReasonIfFalse(List<string> reasons, bool condition, string reason)
    {
        if (!condition)
        {
            reasons.Add(reason);
        }
    }

    private static void AppendDiagnostics(StringBuilder builder, IReadOnlyList<string> diagnostics, string title = "Diagnostics")
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (diagnostics.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var diagnostic in diagnostics)
        {
            builder.AppendLine($"- {diagnostic}");
        }
    }
}
