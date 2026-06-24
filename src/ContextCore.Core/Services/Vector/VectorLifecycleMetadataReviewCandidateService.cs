using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 从 lifecycle metadata repair plan 生成只读人工 review 候选项；本服务不写 sidecar，不改变正式 retrieval。
/// </summary>
public sealed class VectorLifecycleMetadataReviewCandidateService
{
    public const string GeneratedBy = "vector-lifecycle-metadata-review-candidate-service/v1";
    private const string RiskNormalContext = "NormalContextRecoveryRequiresMetadataReview";
    private const string RiskFutureSidecar = "SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval";
    private const string RiskRejected = "RecallRemainsBlockedByLifecycleMetadata";

    private readonly IVectorLifecycleMetadataReviewCandidateStore _store;

    public VectorLifecycleMetadataReviewCandidateService(IVectorLifecycleMetadataReviewCandidateStore store)
    {
        _store = store;
    }

    public async Task<VectorLifecycleMetadataReviewCandidateGenerationResult> GenerateAsync(
        VectorLifecycleMetadataReviewCandidateGenerationRequest request,
        VectorLifecycleMetadataRepairPlanSummaryReport repairPlan,
        string sourceReportPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(repairPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var now = DateTimeOffset.UtcNow;
        var operationId = $"vector-lifecycle-metadata-review-candidates-{Guid.NewGuid():N}";
        var generated = new List<VectorLifecycleMetadataReviewCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var report in repairPlan.Reports)
        {
            foreach (var repair in report.Candidates)
            {
                if (!ShouldGenerate(repair))
                {
                    skipped++;
                    continue;
                }

                var candidateId = BuildCandidateId(
                    request.WorkspaceId,
                    request.CollectionId,
                    repair.MustHitItemId,
                    repair.ProposedLifecycle,
                    repair.ProposedTargetSection,
                    repair.SampleId,
                    FirstNonEmpty(repair.DatasetName, report.DatasetName));
                if (!seen.Add(candidateId))
                {
                    skipped++;
                    continue;
                }

                var existing = await _store.GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
                var dedupeKey = BuildDedupeKey(
                    request.WorkspaceId,
                    request.CollectionId,
                    repair.MustHitItemId,
                    repair.ProposedLifecycle,
                    repair.ProposedTargetSection);
                var candidate = new VectorLifecycleMetadataReviewCandidate
                {
                    CandidateId = candidateId,
                    WorkspaceId = request.WorkspaceId,
                    CollectionId = request.CollectionId,
                    SourceSampleId = repair.SampleId,
                    SourceEvalSet = FirstNonEmpty(repair.DatasetName, report.DatasetName),
                    MustHitItemId = repair.MustHitItemId,
                    ItemKind = repair.ItemKind,
                    Layer = repair.Layer,
                    CurrentLifecycle = repair.CurrentLifecycle,
                    CurrentReviewStatus = repair.CurrentReviewStatus,
                    CurrentTargetSection = repair.CurrentTargetSection,
                    ProposedLifecycle = repair.ProposedLifecycle,
                    ProposedReviewStatus = repair.ProposedReviewStatus,
                    ProposedTargetSection = repair.ProposedTargetSection,
                    RepairReason = repair.RepairReason,
                    EvidenceRefs = repair.EvidenceRefs.ToArray(),
                    SourceRefs = repair.SourceRefs.ToArray(),
                    ProvenanceAvailable = repair.ProvenanceAvailable,
                    RelationEvidenceAvailable = repair.RelationEvidenceAvailable,
                    ReviewEvidenceAvailable = repair.ReviewEvidenceAvailable,
                    RiskIfApproved = BuildRiskIfApproved(repair.ProposedTargetSection),
                    RiskIfRejected = [RiskRejected],
                    RequiresHumanReview = true,
                    Status = string.IsNullOrWhiteSpace(existing?.Status)
                        ? VectorLifecycleMetadataReviewCandidateStatuses.PendingReview
                        : existing!.Status,
                    CreatedAt = existing?.CreatedAt ?? now,
                    Metadata = BuildMetadata(operationId, repairPlan, report, sourceReportPath, dedupeKey)
                };

                await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                generated.Add(candidate);

                if (request.Limit > 0 && generated.Count >= request.Limit)
                {
                    break;
                }
            }

            if (request.Limit > 0 && generated.Count >= request.Limit)
            {
                break;
            }
        }

        return new VectorLifecycleMetadataReviewCandidateGenerationResult
        {
            OperationId = operationId,
            CreatedAt = now,
            SourceReportPath = sourceReportPath,
            CandidateCount = generated.Count,
            GeneratedCount = generated.Count(candidate => candidate.CreatedAt == now),
            UpsertedCount = generated.Count,
            SkippedCount = skipped,
            CorrectlyBlockedSkippedCount = repairPlan.CorrectlyBlockedSkippedCount,
            Candidates = generated
                .OrderByDescending(static item => item.CreatedAt)
                .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
        => _store.QueryAsync(query, cancellationToken);

    public Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _store.GetAsync(candidateId, cancellationToken);

    public async Task<VectorLifecycleMetadataReviewCandidateExplanation?> ExplainAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _store.GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        return new VectorLifecycleMetadataReviewCandidateExplanation
        {
            CandidateId = candidate.CandidateId,
            Candidate = candidate,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            SourceRefs = candidate.SourceRefs.ToArray(),
            ProvenanceAvailable = candidate.ProvenanceAvailable,
            RelationEvidenceAvailable = candidate.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = candidate.ReviewEvidenceAvailable,
            RiskIfApproved = candidate.RiskIfApproved.ToArray(),
            RiskIfRejected = candidate.RiskIfRejected.ToArray(),
            RepairReason = candidate.RepairReason,
            Warnings = candidate.RequiresHumanReview
                ? Array.Empty<string>()
                : ["CandidateDoesNotRequireHumanReview"]
        };
    }

    public static VectorLifecycleMetadataReviewCandidateReport BuildReport(
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        string sourceReportPath,
        int correctlyBlockedSkippedCount)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return new VectorLifecycleMetadataReviewCandidateReport
        {
            OperationId = $"vector-lifecycle-metadata-review-candidates-report-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            SourceReportPath = sourceReportPath,
            CandidateCount = candidates.Count,
            PendingCount = candidates.Count(static item => string.Equals(item.Status, VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, StringComparison.OrdinalIgnoreCase)),
            CorrectlyBlockedSkippedCount = correctlyBlockedSkippedCount,
            CountByStatus = CountBy(candidates, static item => item.Status),
            CountByLayer = CountBy(candidates, static item => FirstNonEmpty(item.Layer, "unknown")),
            CountByItemKind = CountBy(candidates, static item => FirstNonEmpty(item.ItemKind, "unknown")),
            RecentCandidates = candidates
                .OrderByDescending(static item => item.CreatedAt)
                .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray(),
            Recommendation = candidates.Count == 0
                ? VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly
                : VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview,
            FormalRetrievalAllowed = false,
            UseForRuntime = false
        };
    }

    public static string BuildMarkdownReport(VectorLifecycleMetadataReviewCandidateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Candidates");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine($"- SourceReportPath: `{report.SourceReportPath}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- PendingCount: `{report.PendingCount}`");
        builder.AppendLine($"- CorrectlyBlockedSkippedCount: `{report.CorrectlyBlockedSkippedCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        AppendCounts(builder, "Status", report.CountByStatus);
        AppendCounts(builder, "Layer", report.CountByLayer);
        AppendCounts(builder, "ItemKind", report.CountByItemKind);
        builder.AppendLine("## Recent Candidates");
        builder.AppendLine();
        builder.AppendLine("| CandidateId | EvalSet | Sample | MustHit | Layer | ItemKind | CurrentLifecycle | ProposedLifecycle | ProposedSection | Status | RiskIfApproved | RiskIfRejected |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var candidate in report.RecentCandidates)
        {
            builder.AppendLine($"| {Escape(candidate.CandidateId)} | {Escape(candidate.SourceEvalSet)} | {Escape(candidate.SourceSampleId)} | {Escape(candidate.MustHitItemId)} | {Escape(candidate.Layer)} | {Escape(candidate.ItemKind)} | {Escape(candidate.CurrentLifecycle)} | {Escape(candidate.ProposedLifecycle)} | {Escape(candidate.ProposedTargetSection)} | {Escape(candidate.Status)} | {Escape(string.Join(",", candidate.RiskIfApproved))} | {Escape(string.Join(",", candidate.RiskIfRejected))} |");
        }

        return builder.ToString();
    }

    public static bool ShouldGenerate(VectorLifecycleMetadataRepairCandidate repair)
    {
        ArgumentNullException.ThrowIfNull(repair);
        return repair.RequiresHumanReview
            && !repair.CanAutoRepair
            && !IsHardForbidden(repair.ForbiddenReason);
    }

    public static string BuildCandidateId(
        string workspaceId,
        string collectionId,
        string mustHitItemId,
        string proposedLifecycle,
        string proposedTargetSection,
        string? sourceSampleId = null,
        string? sourceEvalSet = null)
    {
        var canonical = string.Join(
            '|',
            BuildDedupeKey(workspaceId, collectionId, mustHitItemId, proposedLifecycle, proposedTargetSection),
            (sourceSampleId ?? string.Empty).Trim().ToLowerInvariant(),
            (sourceEvalSet ?? string.Empty).Trim().ToLowerInvariant());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "vlm-review-" + Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static IReadOnlyList<string> BuildRiskIfApproved(string proposedTargetSection)
    {
        return string.Equals(proposedTargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            ? [RiskNormalContext, RiskFutureSidecar]
            : [RiskFutureSidecar];
    }

    private static bool IsHardForbidden(string? forbiddenReason)
    {
        if (string.IsNullOrWhiteSpace(forbiddenReason))
        {
            return false;
        }

        return forbiddenReason.Equals("UnsafeLifecycle", StringComparison.OrdinalIgnoreCase)
            || forbiddenReason.Equals("ConflictingReplacementRelation", StringComparison.OrdinalIgnoreCase)
            || forbiddenReason.Equals("SourceRejectedOrDeprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildMetadata(
        string operationId,
        VectorLifecycleMetadataRepairPlanSummaryReport summary,
        VectorLifecycleMetadataRepairPlanReport report,
        string sourceReportPath,
        string dedupeKey)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceRepairPlanOperationId"] = summary.OperationId,
            ["sourceRepairPlanDatasetOperationId"] = report.OperationId,
            ["sourceDataset"] = report.DatasetName,
            ["sourceReportPath"] = sourceReportPath,
            ["dedupeKey"] = dedupeKey,
            ["generatedBy"] = GeneratedBy,
            ["generationOperationId"] = operationId,
            ["reviewOnly"] = bool.TrueString,
            ["runtimeEffect"] = bool.FalseString,
            ["sidecarWrite"] = bool.FalseString,
            ["trainingUse"] = "disabled_until_review",
            ["excludedFromTraining"] = bool.TrueString
        };
    }

    private static string BuildDedupeKey(
        string workspaceId,
        string collectionId,
        string mustHitItemId,
        string proposedLifecycle,
        string proposedTargetSection)
        => string.Join(
            '|',
            workspaceId.Trim().ToLowerInvariant(),
            collectionId.Trim().ToLowerInvariant(),
            mustHitItemId.Trim().ToLowerInvariant(),
            proposedLifecycle.Trim().ToLowerInvariant(),
            proposedTargetSection.Trim().ToLowerInvariant());

    private static IReadOnlyDictionary<string, int> CountBy(
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        Func<VectorLifecycleMetadataReviewCandidate, string> selector)
    {
        return candidates
            .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendCounts(
        StringBuilder builder,
        string label,
        IReadOnlyDictionary<string, int> counts)
    {
        builder.AppendLine($"## Count By {label}");
        builder.AppendLine();
        if (counts.Count == 0)
        {
            builder.AppendLine("- none");
            builder.AppendLine();
            return;
        }

        foreach (var item in counts)
        {
            builder.AppendLine($"- {Escape(item.Key)}: `{item.Value.ToString(CultureInfo.InvariantCulture)}`");
        }

        builder.AppendLine();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string Escape(string? value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
