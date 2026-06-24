using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// lifecycle-filtered mustHit triage；只读审计 section routing 与 metadata 修复方向，不改变 vector eligibility policy。
/// </summary>
public sealed class VectorEligibilityRecallLossTriageRunner
{
    private static readonly string[] SourceReferenceKeys =
    [
        "sourceRefs",
        "sourceRef",
        "sourceReferences",
        "sourceReference"
    ];

    private static readonly string[] EvidenceReferenceKeys =
    [
        "evidenceRefs",
        "evidenceRef",
        "evidenceReferences",
        "evidenceReference"
    ];

    private static readonly string[] ReplacementKeys =
    [
        "replacementState",
        "supersededBy",
        "replacedBy",
        "replacementItemId",
        "replacementId",
        "superseded_by",
        "replaced_by"
    ];

    private readonly VectorQueryProfileRegistry _profileRegistry;
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;
    private readonly VectorSourceLifecycleMetadataResolver _lifecycleResolver;

    public VectorEligibilityRecallLossTriageRunner(
        VectorQueryProfileRegistry? profileRegistry = null,
        VectorCandidateEligibilityPolicy? eligibilityPolicy = null,
        VectorSourceLifecycleMetadataResolver? lifecycleResolver = null)
    {
        _profileRegistry = profileRegistry ?? new VectorQueryProfileRegistry();
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
        _lifecycleResolver = lifecycleResolver ?? new VectorSourceLifecycleMetadataResolver();
    }

    public VectorEligibilityRecallLossTriageReport BuildReport(
        string datasetName,
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> indexedEntries,
        EmbeddingProviderOptions providerOptions,
        string? profileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(indexedEntries);
        ArgumentNullException.ThrowIfNull(providerOptions);

        var profile = _profileRegistry.Resolve(string.IsNullOrWhiteSpace(profileId)
            ? VectorQueryProfileIds.NormalV1
            : profileId);
        var sourceById = sourceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var providerEntriesById = indexedEntries
            .Where(entry => MatchesProviderScope(entry, providerOptions) && !string.IsNullOrWhiteSpace(entry.ItemId))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.UpdatedAt).ToArray(), StringComparer.OrdinalIgnoreCase);
        var details = new List<VectorEligibilityRecallLossTriageDetail>();

        foreach (var sample in samples)
        {
            foreach (var mustHit in sample.MustHit
                         .Where(item => !string.IsNullOrWhiteSpace(item))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var entry = FindEntry(providerEntriesById, mustHit);
                if (entry is null)
                {
                    continue;
                }

                var eligibility = _eligibilityPolicy.Evaluate(profile, entry, similarity: 1.0, diagnostics: Array.Empty<string>());
                if (!eligibility.BlockedReasons.Any(IsLifecycleBlockedReason))
                {
                    continue;
                }

                var source = FindSource(sourceById, mustHit);
                details.Add(BuildDetail(datasetName, sample, mustHit, source, entry, eligibility));
            }
        }

        return BuildReport(datasetName, samples.Count, providerOptions, details);
    }

    public static VectorEligibilityRecallLossTriageSummaryReport BuildSummary(
        IReadOnlyList<VectorEligibilityRecallLossTriageReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var breakdown = reports
            .SelectMany(report => report.CategoryBreakdown)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase);

        return new VectorEligibilityRecallLossTriageSummaryReport
        {
            OperationId = $"vector-eligibility-recall-loss-triage-summary-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Reports = reports.ToArray(),
            TotalFilteredMustHit = reports.Sum(report => report.TotalFilteredMustHit),
            CorrectlyBlockedCount = reports.Sum(report => report.CorrectlyBlockedCount),
            RouteToHistoricalCount = reports.Sum(report => report.RouteToHistoricalCount),
            RouteToAuditCount = reports.Sum(report => report.RouteToAuditCount),
            MetadataRepairNeededCount = reports.Sum(report => report.MetadataRepairNeededCount),
            EvalExpectationReviewNeededCount = reports.Sum(report => report.EvalExpectationReviewNeededCount),
            UnsafeToRecoverCount = reports.Sum(report => report.UnsafeToRecoverCount),
            RecoverableWithoutNormalContextCount = reports.Sum(report => report.RecoverableWithoutNormalContextCount),
            RecoverableToNormalContextCount = reports.Sum(report => report.RecoverableToNormalContextCount),
            Recommendation = RecommendSummary(reports),
            CategoryBreakdown = breakdown,
            FormalRetrievalAllowed = false,
            UseForRuntime = false
        };
    }

    public static string BuildMarkdownReport(VectorEligibilityRecallLossTriageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine($"# Vector Eligibility Recall Loss Triage - {report.DatasetName}");
        builder.AppendLine();
        AppendReport(builder, report);
        return builder.ToString();
    }

    public static string BuildMarkdownSummary(VectorEligibilityRecallLossTriageSummaryReport summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Eligibility Recall Loss Triage Summary");
        builder.AppendLine();
        builder.AppendLine($"Generated: {summary.CreatedAt:O}");
        builder.AppendLine($"- Recommendation: `{summary.Recommendation}`");
        builder.AppendLine($"- TotalFilteredMustHit: `{summary.TotalFilteredMustHit}`");
        builder.AppendLine($"- CorrectlyBlockedCount: `{summary.CorrectlyBlockedCount}`");
        builder.AppendLine($"- RouteToHistoricalCount: `{summary.RouteToHistoricalCount}`");
        builder.AppendLine($"- RouteToAuditCount: `{summary.RouteToAuditCount}`");
        builder.AppendLine($"- MetadataRepairNeededCount: `{summary.MetadataRepairNeededCount}`");
        builder.AppendLine($"- EvalExpectationReviewNeededCount: `{summary.EvalExpectationReviewNeededCount}`");
        builder.AppendLine($"- UnsafeToRecoverCount: `{summary.UnsafeToRecoverCount}`");
        builder.AppendLine($"- RecoverableWithoutNormalContextCount: `{summary.RecoverableWithoutNormalContextCount}`");
        builder.AppendLine($"- RecoverableToNormalContextCount: `{summary.RecoverableToNormalContextCount}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{summary.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{summary.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Filtered | CorrectlyBlocked | Historical | Audit | MetadataRepair | EvalReview | Unsafe | RecoverNoNormal | RecoverNormal | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var report in summary.Reports)
        {
            builder.AppendLine($"| {report.DatasetName} | {report.TotalFilteredMustHit} | {report.CorrectlyBlockedCount} | {report.RouteToHistoricalCount} | {report.RouteToAuditCount} | {report.MetadataRepairNeededCount} | {report.EvalExpectationReviewNeededCount} | {report.UnsafeToRecoverCount} | {report.RecoverableWithoutNormalContextCount} | {report.RecoverableToNormalContextCount} | {report.Recommendation} |");
        }

        builder.AppendLine();
        AppendBreakdown(builder, summary.CategoryBreakdown);
        foreach (var report in summary.Reports)
        {
            builder.AppendLine();
            AppendReport(builder, report);
        }

        return builder.ToString();
    }

    private VectorEligibilityRecallLossTriageDetail BuildDetail(
        string datasetName,
        ContextEvalSample sample,
        string mustHit,
        VectorReindexSourceItem? source,
        VectorIndexEntry entry,
        VectorCandidateEligibilityResult eligibility)
    {
        var metadata = ResolveMetadata(source, entry);
        var lifecycle = _lifecycleResolver.Resolve(entry);
        var classification = Classify(eligibility.BlockedReasons, lifecycle);
        var candidateSection = ResolveCandidateTargetSection(classification.Category, lifecycle);
        var canRoute = IsAuditOrHistoricalSection(candidateSection);
        var canRepair = IsMetadataRepairCategory(classification.Category)
                        || lifecycle.MissingReplacementInfo
                        || !lifecycle.HasReviewStatus;

        return new VectorEligibilityRecallLossTriageDetail
        {
            DatasetName = datasetName,
            SampleId = sample.Id,
            Mode = sample.Mode,
            Intent = ResolveIntent(sample),
            QueryText = sample.Query,
            MustHitItemId = mustHit,
            ItemKind = FirstNonEmpty(entry.ItemKind, source?.ItemKind, lifecycle.ItemKind),
            Layer = FirstNonEmpty(entry.Layer, source?.Layer, lifecycle.Layer),
            Lifecycle = string.IsNullOrWhiteSpace(lifecycle.Lifecycle) ? "Unknown" : lifecycle.Lifecycle,
            ReviewStatus = lifecycle.ReviewStatus,
            ReplacementState = ResolveReplacementState(metadata, lifecycle),
            SourceRefs = ResolveReferences(metadata, SourceReferenceKeys),
            EvidenceRefs = ResolveReferences(metadata, EvidenceReferenceKeys),
            BlockedReason = string.Join(",", eligibility.BlockedReasons),
            BlockedReasons = eligibility.BlockedReasons,
            CurrentTargetSection = eligibility.TargetSection,
            CandidateTargetSection = candidateSection,
            ShouldRemainBlocked = classification.ShouldRemainBlocked,
            CanRouteToAuditOrHistorical = canRoute,
            CanRepairMetadata = canRepair,
            RecommendedAction = classification.RecommendedAction,
            Rationale = classification.Rationale,
            TriageCategory = classification.Category
        };
    }

    private static VectorEligibilityRecallLossTriageReport BuildReport(
        string datasetName,
        int sampleCount,
        EmbeddingProviderOptions providerOptions,
        IReadOnlyList<VectorEligibilityRecallLossTriageDetail> details)
    {
        var breakdown = details
            .GroupBy(item => item.TriageCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var correctlyBlocked = details.Count(static item =>
            string.Equals(item.TriageCategory, VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedDeprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.TriageCategory, VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedHistorical, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.TriageCategory, VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedSuperseded, StringComparison.OrdinalIgnoreCase));
        var routeHistorical = details.Count(static item =>
            string.Equals(item.CandidateTargetSection, VectorQueryTargetSections.HistoricalContext, StringComparison.OrdinalIgnoreCase));
        var routeAudit = details.Count(static item =>
            string.Equals(item.CandidateTargetSection, VectorQueryTargetSections.AuditContext, StringComparison.OrdinalIgnoreCase));
        var metadataRepair = details.Count(static item => item.CanRepairMetadata);
        var evalReview = Count(breakdown, VectorEligibilityRecallLossTriageCategories.RequiresEvalExpectationReview);
        var unsafeCount = Count(breakdown, VectorEligibilityRecallLossTriageCategories.UnsafeToRecover);
        var recoverWithoutNormal = details.Count(static item => item.CanRouteToAuditOrHistorical);
        var recoverNormal = details.Count(static item =>
            !item.ShouldRemainBlocked
            && string.Equals(item.CandidateTargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase));

        return new VectorEligibilityRecallLossTriageReport
        {
            OperationId = $"vector-eligibility-recall-loss-triage-{datasetName.ToLowerInvariant()}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            ProviderId = providerOptions.ProviderId,
            EmbeddingModel = providerOptions.EmbeddingModel,
            Dimension = providerOptions.Dimension,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            SampleCount = sampleCount,
            TotalFilteredMustHit = details.Count,
            CorrectlyBlockedCount = correctlyBlocked,
            RouteToHistoricalCount = routeHistorical,
            RouteToAuditCount = routeAudit,
            MetadataRepairNeededCount = metadataRepair,
            EvalExpectationReviewNeededCount = evalReview,
            UnsafeToRecoverCount = unsafeCount,
            RecoverableWithoutNormalContextCount = recoverWithoutNormal,
            RecoverableToNormalContextCount = recoverNormal,
            Recommendation = Recommend(metadataRepair, evalReview, unsafeCount, recoverWithoutNormal, recoverNormal),
            CategoryBreakdown = breakdown,
            Details = details
                .OrderBy(item => item.TriageCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SampleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.MustHitItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void AppendReport(StringBuilder builder, VectorEligibilityRecallLossTriageReport report)
    {
        builder.AppendLine($"## {report.DatasetName}");
        builder.AppendLine();
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- EmbeddingModel: `{report.EmbeddingModel}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- TotalFilteredMustHit: `{report.TotalFilteredMustHit}`");
        builder.AppendLine($"- CorrectlyBlockedCount: `{report.CorrectlyBlockedCount}`");
        builder.AppendLine($"- RouteToHistoricalCount: `{report.RouteToHistoricalCount}`");
        builder.AppendLine($"- RouteToAuditCount: `{report.RouteToAuditCount}`");
        builder.AppendLine($"- MetadataRepairNeededCount: `{report.MetadataRepairNeededCount}`");
        builder.AppendLine($"- EvalExpectationReviewNeededCount: `{report.EvalExpectationReviewNeededCount}`");
        builder.AppendLine($"- UnsafeToRecoverCount: `{report.UnsafeToRecoverCount}`");
        builder.AppendLine($"- RecoverableWithoutNormalContextCount: `{report.RecoverableWithoutNormalContextCount}`");
        builder.AppendLine($"- RecoverableToNormalContextCount: `{report.RecoverableToNormalContextCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        AppendBreakdown(builder, report.CategoryBreakdown);
        builder.AppendLine();
        builder.AppendLine("| Category | Sample | Mode | Intent | MustHit | Lifecycle | Review | Replacement | CurrentSection | CandidateSection | RemainBlocked | Action | Rationale |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---:|---|---|");
        foreach (var detail in report.Details.Take(120))
        {
            builder.AppendLine($"| {detail.TriageCategory} | {Escape(detail.SampleId)} | {Escape(detail.Mode)} | {Escape(detail.Intent)} | {Escape(detail.MustHitItemId)} | {Escape(detail.Lifecycle)} | {Escape(detail.ReviewStatus)} | {Escape(detail.ReplacementState)} | {Escape(detail.CurrentTargetSection)} | {Escape(detail.CandidateTargetSection)} | {detail.ShouldRemainBlocked} | {Escape(detail.RecommendedAction)} | {Escape(detail.Rationale)} |");
        }
    }

    private static void AppendBreakdown(StringBuilder builder, IReadOnlyDictionary<string, int> breakdown)
    {
        builder.AppendLine("### Category Breakdown");
        builder.AppendLine();
        builder.AppendLine("| Category | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in breakdown.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.Key} | {item.Value} |");
        }
    }

    private static ClassificationResult Classify(
        IReadOnlyList<string> blockedReasons,
        VectorSourceLifecycleMetadata lifecycle)
    {
        if (blockedReasons.Any(IsHardDiagnosticBlock) || lifecycle.IsRejected)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.UnsafeToRecover,
                true,
                "KeepBlockedUnsafe",
                "存在 rejected 或硬诊断阻断，不能通过 section routing 恢复。");
        }

        if (lifecycle.IsSuperseded)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedSuperseded,
                true,
                "RouteToAuditContext",
                "superseded mustHit 不允许进入 normal_context，只能作为 replacement/audit 证据观察。");
        }

        if (lifecycle.IsDeprecated)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedDeprecated,
                true,
                "RouteToAuditContext",
                "deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。");
        }

        if (lifecycle.IsHistorical)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedHistorical,
                true,
                "RouteToHistoricalContext",
                "historical mustHit 不允许进入 normal_context，可进入 historical/audit section。");
        }

        if (!lifecycle.IsKnownLifecycle
            || blockedReasons.Contains(VectorCandidateBlockedReason.UnknownLifecycleBlocked, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.LegacySourceRequiresLifecycleMetadata, StringComparer.OrdinalIgnoreCase))
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.MetadataLifecycleRepairNeeded,
                true,
                "RepairLifecycleMetadata",
                "缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。");
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.ReplacementMetadataMissingBlocked, StringComparer.OrdinalIgnoreCase)
            || lifecycle.MissingReplacementInfo)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.ReplacementStateRepairNeeded,
                true,
                "RepairReplacementState",
                "replacement metadata 不完整；修复前只能保留阻断。");
        }

        if (!lifecycle.HasReviewStatus)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.ReviewStatusRepairNeeded,
                true,
                "RepairReviewStatus",
                "缺少 reviewStatus；修复前不建议恢复。");
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.HistoricalSourceRequiresAuditProfile, StringComparer.OrdinalIgnoreCase)
            || lifecycle.RequiresAuditProfile)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.ProfileTooStrictForAuditMode,
                true,
                "RouteToAuditContext",
                "该候选需要 audit-aware profile 或 audit section，不能直接放入 normal_context。");
        }

        if (IsActiveLike(lifecycle.Lifecycle) && !lifecycle.IsSuperseded)
        {
            return NewClassification(
                VectorEligibilityRecallLossTriageCategories.RequiresEvalExpectationReview,
                false,
                "ReviewEvalExpectation",
                "metadata 显示为 active/current/stable，但仍被阻断；需要复核 eval expectation 与 profile 配置。");
        }

        return NewClassification(
            VectorEligibilityRecallLossTriageCategories.RequiresEvalExpectationReview,
            true,
            "ReviewEvalExpectation",
            "未能仅凭 lifecycle metadata 安全恢复，需要人工复核 eval expectation。");
    }

    private static string ResolveCandidateTargetSection(string category, VectorSourceLifecycleMetadata lifecycle)
    {
        return category switch
        {
            VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedHistorical
                or VectorEligibilityRecallLossTriageCategories.ShouldRouteToHistoricalContext => VectorQueryTargetSections.HistoricalContext,
            VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedDeprecated
                or VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedSuperseded
                or VectorEligibilityRecallLossTriageCategories.ShouldRouteToAuditContext
                or VectorEligibilityRecallLossTriageCategories.ProfileTooStrictForAuditMode => VectorQueryTargetSections.AuditContext,
            VectorEligibilityRecallLossTriageCategories.RequiresEvalExpectationReview when IsActiveLike(lifecycle.Lifecycle) && !lifecycle.IsSuperseded => VectorQueryTargetSections.NormalContext,
            VectorEligibilityRecallLossTriageCategories.MetadataLifecycleRepairNeeded
                or VectorEligibilityRecallLossTriageCategories.ReviewStatusRepairNeeded
                or VectorEligibilityRecallLossTriageCategories.ReplacementStateRepairNeeded => VectorQueryTargetSections.DiagnosticsOnly,
            _ => VectorQueryTargetSections.Excluded
        };
    }

    private static string Recommend(
        int metadataRepair,
        int evalReview,
        int unsafeCount,
        int recoverWithoutNormal,
        int recoverNormal)
    {
        if (unsafeCount > 0)
        {
            return VectorEligibilityRecallLossTriageRecommendations.UnsafeToRecover;
        }

        if (metadataRepair > 0)
        {
            return VectorEligibilityRecallLossTriageRecommendations.NeedsMetadataRepair;
        }

        if (evalReview > 0 && recoverWithoutNormal == 0 && recoverNormal == 0)
        {
            return VectorEligibilityRecallLossTriageRecommendations.NeedsEvalExpectationReview;
        }

        if (recoverWithoutNormal > 0)
        {
            return VectorEligibilityRecallLossTriageRecommendations.ReadyForSectionRoutedRecallRepair;
        }

        return VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;
    }

    private static string RecommendSummary(IReadOnlyList<VectorEligibilityRecallLossTriageReport> reports)
    {
        if (reports.Count == 0)
        {
            return VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;
        }

        var priorities = new[]
        {
            VectorEligibilityRecallLossTriageRecommendations.UnsafeToRecover,
            VectorEligibilityRecallLossTriageRecommendations.NeedsMetadataRepair,
            VectorEligibilityRecallLossTriageRecommendations.NeedsEvalExpectationReview,
            VectorEligibilityRecallLossTriageRecommendations.ReadyForSectionRoutedRecallRepair
        };
        foreach (var priority in priorities)
        {
            if (reports.Any(report => string.Equals(report.Recommendation, priority, StringComparison.OrdinalIgnoreCase)))
            {
                return priority;
            }
        }

        return VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;
    }

    private static VectorReindexSourceItem? FindSource(
        IReadOnlyDictionary<string, VectorReindexSourceItem> sourceById,
        string expected)
    {
        if (sourceById.TryGetValue(expected, out var exact))
        {
            return exact;
        }

        return sourceById
            .Where(pair => IdMatches(expected, pair.Key))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    private static VectorIndexEntry? FindEntry(
        IReadOnlyDictionary<string, VectorIndexEntry[]> entriesById,
        string expected)
    {
        if (entriesById.TryGetValue(expected, out var exact) && exact.Length > 0)
        {
            return exact[0];
        }

        return entriesById
            .Where(pair => IdMatches(expected, pair.Key))
            .SelectMany(pair => pair.Value)
            .OrderByDescending(entry => entry.UpdatedAt)
            .FirstOrDefault();
    }

    private static bool IdMatches(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
               || actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesProviderScope(VectorIndexEntry entry, EmbeddingProviderOptions options)
    {
        return string.Equals(entry.EmbeddingProvider, options.ProviderId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.EmbeddingModel, options.EmbeddingModel, StringComparison.OrdinalIgnoreCase)
               && (options.Dimension <= 0 || entry.Dimension == options.Dimension);
    }

    private static Dictionary<string, string> ResolveMetadata(VectorReindexSourceItem? source, VectorIndexEntry entry)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach (var pair in source.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in entry.Metadata)
        {
            metadata[pair.Key] = pair.Value;
        }

        return metadata;
    }

    private static string ResolveIntent(ContextEvalSample sample)
    {
        return Get(sample.Metadata, "intent",
            "routerIntent",
            "planningIntent",
            "taskIntent");
    }

    private static string ResolveReplacementState(
        IReadOnlyDictionary<string, string> metadata,
        VectorSourceLifecycleMetadata lifecycle)
    {
        var explicitState = Get(metadata, "replacementState");
        if (!string.IsNullOrWhiteSpace(explicitState))
        {
            return explicitState;
        }

        if (ReplacementKeys.Any(key => HasMetadataValue(metadata, key)))
        {
            return "HasReplacement";
        }

        return lifecycle.MissingReplacementInfo
            ? "MissingReplacement"
            : lifecycle.HasReplacementInfo ? "HasReplacement" : "NotRequired";
    }

    private static IReadOnlyList<string> ResolveReferences(
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            var value = Get(metadata, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return SplitRefs(value);
            }
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> SplitRefs(string value)
    {
        return value
            .Trim('[', ']', '"')
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => item.Trim().Trim('"'))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static string Get(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static bool IsLifecycleBlockedReason(string reason)
    {
        return string.Equals(reason, VectorCandidateBlockedReason.UnknownLifecycleBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.LegacySourceRequiresLifecycleMetadata, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.HistoricalSourceRequiresAuditProfile, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.DeprecatedCandidateBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.HistoricalCandidateBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.RejectedCandidateBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.CandidateLifecycleBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.SupersededCandidateBlocked, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardDiagnosticBlock(string reason)
    {
        return string.Equals(reason, VectorCandidateBlockedReason.RejectedCandidateBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.DuplicateVectorEntryBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.OrphanVectorEntryBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.DimensionMismatchBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.StaleEmbeddingBlocked, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveLike(string lifecycle)
    {
        return string.Equals(lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Stable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMetadataRepairCategory(string category)
    {
        return string.Equals(category, VectorEligibilityRecallLossTriageCategories.MetadataLifecycleRepairNeeded, StringComparison.OrdinalIgnoreCase)
               || string.Equals(category, VectorEligibilityRecallLossTriageCategories.ReviewStatusRepairNeeded, StringComparison.OrdinalIgnoreCase)
               || string.Equals(category, VectorEligibilityRecallLossTriageCategories.ReplacementStateRepairNeeded, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuditOrHistoricalSection(string section)
    {
        return string.Equals(section, VectorQueryTargetSections.AuditContext, StringComparison.OrdinalIgnoreCase)
               || string.Equals(section, VectorQueryTargetSections.HistoricalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static int Count(IReadOnlyDictionary<string, int> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : 0;
    }

    private static ClassificationResult NewClassification(
        string category,
        bool shouldRemainBlocked,
        string recommendedAction,
        string rationale)
    {
        return new ClassificationResult(category, shouldRemainBlocked, recommendedAction, rationale);
    }

    private static string Escape(string value)
    {
        return string.IsNullOrEmpty(value) ? "-" : value.Replace("|", "/", StringComparison.Ordinal);
    }

    private sealed record ClassificationResult(
        string Category,
        bool ShouldRemainBlocked,
        string RecommendedAction,
        string Rationale);
}
