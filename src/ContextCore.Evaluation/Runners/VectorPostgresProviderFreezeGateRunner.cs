using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>DB5.F pgvector provider freeze gate；只读取报告，不启用正式向量检索。</summary>
public sealed class VectorPostgresProviderFreezeGateRunner
{
    private const double FloatTolerance = 0.000000001d;

    public VectorPostgresProviderFreezeGateReport BuildFreezeGateReport(
        PostgresVectorDiagnosticsReport? diagnostics,
        PostgresVectorCompatibilityReport? compatibility,
        PostgresVectorIndexParityReport? parity,
        PostgresVectorProviderScopedReindexReport? reindexQuality,
        PostgresVectorQueryPreviewReport? queryPreview,
        PostgresVectorShadowEvalSummaryReport? shadowSummary,
        bool p15GatePassed)
    {
        var blocked = new List<string>();
        var diagnosticsReady = string.Equals(diagnostics?.Recommendation, "ReadyForVectorParityEval", StringComparison.OrdinalIgnoreCase);
        var compatibilityReady = string.Equals(compatibility?.Recommendation, "ReadyForVectorParityEval", StringComparison.OrdinalIgnoreCase);
        var parityPassed = parity is not null
                           && string.Equals(parity.Recommendation, "ReadyForProviderScopedReindex", StringComparison.OrdinalIgnoreCase)
                           && parity.MismatchCount == 0
                           && parity.OrderingMismatchCount == 0
                           && parity.MetadataMismatchCount == 0
                           && parity.DimensionMismatchBlocked
                           && parity.ProviderModelMismatchBlocked;
        var reindexPassed = reindexQuality is not null
                            && string.Equals(reindexQuality.Recommendation, "ReadyForPgVectorQueryPreview", StringComparison.OrdinalIgnoreCase)
                            && reindexQuality.MetadataRoundtripMismatchCount == 0
                            && reindexQuality.DimensionMismatchCount == 0
                            && reindexQuality.ProviderModelMismatchCount == 0
                            && !reindexQuality.UseForRuntime;
        var queryPreviewPassed = queryPreview is not null
                                 && string.Equals(queryPreview.Recommendation, "ReadyForPgVectorShadowEval", StringComparison.OrdinalIgnoreCase)
                                 && queryPreview.OrderingMismatchCount == 0
                                 && queryPreview.MetadataMismatchCount == 0
                                 && queryPreview.EligibilityMetadataMismatchCount == 0
                                 && queryPreview.RiskProjectionMismatchCount == 0
                                 && queryPreview.DimensionMismatchBlocked
                                 && queryPreview.ProviderModelMismatchBlocked
                                 && !queryPreview.UseForRuntime;
        var shadowEvalPassed = shadowSummary is not null
                               && string.Equals(shadowSummary.Recommendation, "ReadyForVectorPostgresFreeze", StringComparison.OrdinalIgnoreCase)
                               && !shadowSummary.UseForRuntime;

        var a3 = FindReport(shadowSummary, "A3");
        var extended = FindReport(shadowSummary, "Extended");
        var allReports = shadowSummary?.Reports ?? Array.Empty<PostgresVectorShadowEvalReport>();
        var a3RecallDelta = a3?.RecallDelta ?? double.NaN;
        var extendedRecallDelta = extended?.RecallDelta ?? double.NaN;
        var riskAfterPolicy = allReports.Sum(static report => report.RiskAfterPolicy);
        var mustNotRisk = allReports.Sum(static report => report.MustNotHitRiskAfterPolicy);
        var lifecycleRisk = allReports.Sum(static report => report.LifecycleRiskAfterPolicy);
        var formalOutputChanged = allReports.Sum(static report => report.FormalOutputChanged);
        var projectionMismatch = allReports.Sum(static report =>
            report.MetadataMismatchCount + report.EligibilityMetadataMismatchCount + report.RiskProjectionMismatchCount);
        var runtimeEnabled = (reindexQuality?.UseForRuntime ?? false)
                             || (queryPreview?.UseForRuntime ?? false)
                             || (shadowSummary?.UseForRuntime ?? false)
                             || allReports.Any(static report => report.UseForRuntime);

        AddReasonIfFalse(blocked, diagnosticsReady, "DiagnosticsNotReadyForVectorParityEval");
        AddReasonIfFalse(blocked, compatibilityReady, "CompatibilityNotReadyForVectorParityEval");
        AddReasonIfFalse(blocked, parityPassed, "VectorParityNotReadyForProviderScopedReindex");
        AddReasonIfFalse(blocked, reindexPassed, "ProviderScopedReindexQualityNotReadyForQueryPreview");
        AddReasonIfFalse(blocked, queryPreviewPassed, "QueryPreviewNotReadyForShadowEval");
        AddReasonIfFalse(blocked, shadowEvalPassed, "ShadowEvalNotReadyForFreeze");
        AddReasonIfFalse(blocked, a3 is not null, "A3ShadowEvalMissing");
        AddReasonIfFalse(blocked, extended is not null, "ExtendedShadowEvalMissing");
        AddReasonIfFalse(blocked, IsZero(a3RecallDelta), "A3RecallDeltaNonZero");
        AddReasonIfFalse(blocked, IsZero(extendedRecallDelta), "ExtendedRecallDeltaNonZero");
        AddReasonIfFalse(blocked, riskAfterPolicy == 0, "RiskAfterPolicyIncreased");
        AddReasonIfFalse(blocked, mustNotRisk <= FloatTolerance, "MustNotHitRiskAfterPolicyIncreased");
        AddReasonIfFalse(blocked, lifecycleRisk <= FloatTolerance, "LifecycleRiskAfterPolicyIncreased");
        AddReasonIfFalse(blocked, formalOutputChanged == 0, "FormalOutputChangedNonZero");
        AddReasonIfFalse(blocked, projectionMismatch == 0, "ProjectionMismatchNonZero");
        AddReasonIfFalse(blocked, !runtimeEnabled, "UseForRuntimeTrue");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        var passed = blocked.Count == 0;
        return new VectorPostgresProviderFreezeGateReport
        {
            Passed = passed,
            VectorPostgresProvider = passed ? "ReadyForPreviewShadowStorage" : "NotReady",
            DefaultVectorStore = "unchanged",
            UseForRuntime = runtimeEnabled,
            FormalRetrievalAllowed = false,
            DiagnosticsReady = diagnosticsReady,
            CompatibilityReady = compatibilityReady,
            ParityPassed = parityPassed,
            ReindexQualityPassed = reindexPassed,
            QueryPreviewPassed = queryPreviewPassed,
            ShadowEvalPassed = shadowEvalPassed,
            A3RecallDelta = double.IsNaN(a3RecallDelta) ? 0 : a3RecallDelta,
            ExtendedRecallDelta = double.IsNaN(extendedRecallDelta) ? 0 : extendedRecallDelta,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            ProjectionMismatchCount = projectionMismatch,
            P15GatePassed = p15GatePassed,
            BlockedReasons = blocked,
            Diagnostics = passed
                ? ["UseForRuntime=false", "FormalRetrievalAllowed=false", "PreviewShadowEvalOnly", "VectorRetrievalStillBlockedByA3Recall"]
                : ["VectorPostgresFreezeGateBlocked"],
            Recommendation = passed ? "ReadyForPreviewShadowStorage" : BuildRecommendation(blocked)
        };
    }

    public static string BuildMarkdown(VectorPostgresProviderFreezeGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector Postgres Provider Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- VectorPostgresProvider: `{report.VectorPostgresProvider}`");
        builder.AppendLine($"- DefaultVectorStore: `{report.DefaultVectorStore}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- DiagnosticsReady: `{report.DiagnosticsReady}`");
        builder.AppendLine($"- CompatibilityReady: `{report.CompatibilityReady}`");
        builder.AppendLine($"- ParityPassed: `{report.ParityPassed}`");
        builder.AppendLine($"- ReindexQualityPassed: `{report.ReindexQualityPassed}`");
        builder.AppendLine($"- QueryPreviewPassed: `{report.QueryPreviewPassed}`");
        builder.AppendLine($"- ShadowEvalPassed: `{report.ShadowEvalPassed}`");
        builder.AppendLine($"- A3RecallDelta: `{report.A3RecallDelta:0.########}`");
        builder.AppendLine($"- ExtendedRecallDelta: `{report.ExtendedRecallDelta:0.########}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy:0.########}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy:0.########}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- ProjectionMismatchCount: `{report.ProjectionMismatchCount}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Allowed", report.Allowed);
        AppendList(builder, "Required", report.Required);
        AppendList(builder, "Forbidden", report.Forbidden);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static PostgresVectorShadowEvalReport? FindReport(
        PostgresVectorShadowEvalSummaryReport? summary,
        string datasetName)
        => summary?.Reports.FirstOrDefault(report =>
            string.Equals(report.DatasetName, datasetName, StringComparison.OrdinalIgnoreCase));

    private static bool IsZero(double value)
        => !double.IsNaN(value) && Math.Abs(value) <= FloatTolerance;

    private static void AddReasonIfFalse(ICollection<string> reasons, bool condition, string reason)
    {
        if (!condition)
        {
            reasons.Add(reason);
        }
    }

    private static string BuildRecommendation(IReadOnlyCollection<string> blocked)
    {
        if (blocked.Contains("UseForRuntimeTrue", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("FormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByFormalOutputChange";
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByRiskRegression";
        }

        if (blocked.Any(static reason => reason.Contains("Projection", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByProjectionMismatch";
        }

        if (blocked.Any(static reason => reason.Contains("RecallDelta", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByRecallRegression";
        }

        return "KeepPreviewOnly";
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
