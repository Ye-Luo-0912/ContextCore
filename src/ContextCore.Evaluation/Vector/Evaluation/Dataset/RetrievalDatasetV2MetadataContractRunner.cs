using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Retrieval Dataset V2 ingestion metadata contract / validator；仅生成离线报告，不生成正式数据。
/// </summary>
public sealed class RetrievalDatasetV2MetadataContractRunner
{
    public RetrievalDatasetV2MetadataContractReport BuildContractReport()
    {
        return new RetrievalDatasetV2MetadataContractReport
        {
            OperationId = $"retrieval-dataset-v2-contract-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CorpusItemRequiredFields =
            [
                "itemId",
                "itemKind",
                "layer/sourceKind",
                "text",
                "sourceRefs",
                "evidenceRefs",
                "provenance.recordId",
                "provenance.sourceFingerprint",
                "lifecycle",
                "reviewStatus",
                "replacementState",
                "targetSection",
                "split"
            ],
            QuerySampleRequiredFields =
            [
                "sampleId",
                "queryText",
                "mode/intent",
                "mustHit",
                "mustNotHit",
                "split",
                "sourceRefs",
                "evidenceRefs",
                "provenance.recordId"
            ],
            LifecycleRules =
            [
                "normal_context requires lifecycle Active/Current/Stable and non-superseded replacementState.",
                "deprecated/historical/superseded items must not be expected in normal_context.",
                "reviewStatus must be compatible with lifecycle and targetSection."
            ],
            TargetSectionRules =
            [
                "normal_context is for active/current/stable data only.",
                "audit_context and historical_context are allowed for deprecated/historical review paths.",
                "diagnostics_only is allowed for evidence gaps and unsafe recovery candidates."
            ],
            RelationEvidenceRules =
            [
                "replacement/deprecation/supersedes relations must have sourceRefs or evidenceRefs.",
                "mustHit repair candidates should carry relation evidence when lifecycle depends on graph state.",
                "relation review status must not contradict item lifecycle metadata."
            ],
            SplitIsolationRules =
            [
                "corpus items and query samples must declare split.",
                "train/dev/test splits must not share sample ids.",
                "evaluation query text must not contain target item ids or label-only leakage."
            ],
            GeneratesFormalDataset = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = RetrievalDatasetV2ValidationRecommendations.ReadyForDatasetV2Authoring
        };
    }

    public RetrievalDatasetV2ValidationReport Validate(
        IReadOnlyList<VectorReindexSourceItem> corpusItems,
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<ContextRelation> relations)
    {
        ArgumentNullException.ThrowIfNull(corpusItems);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(relations);

        var corpusById = corpusItems
            .Where(static item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var relationByItem = relations
            .SelectMany(relation => new[]
            {
                new KeyValuePair<string, ContextRelation>(relation.SourceId, relation),
                new KeyValuePair<string, ContextRelation>(relation.TargetId, relation)
            })
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Select(static pair => pair.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        var issues = new List<RetrievalDatasetV2ValidationIssue>();
        AddDuplicateIssues(corpusItems, samples, issues);

        foreach (var item in corpusItems)
        {
            ValidateCorpusItem(item, relationByItem.GetValueOrDefault(item.ItemId) ?? Array.Empty<ContextRelation>(), issues);
        }

        foreach (var sample in samples)
        {
            ValidateSample(sample, corpusById, issues);
        }

        var issueBreakdown = issues
            .GroupBy(static issue => issue.IssueType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new RetrievalDatasetV2ValidationReport
        {
            OperationId = $"retrieval-dataset-v2-validator-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CorpusItemCount = corpusItems.Count,
            QuerySampleCount = samples.Count,
            MustHitCount = samples.Sum(static sample => sample.MustHit.Count(static item => !string.IsNullOrWhiteSpace(item))),
            MustNotCount = samples.Sum(static sample => sample.MustNotHit.Count(static item => !string.IsNullOrWhiteSpace(item))),
            MustHitMissingFromCorpusCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MustHitMissingFromCorpus),
            MustNotMissingFromCorpusCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MustNotMissingFromCorpus),
            MustHitMustNotOverlapCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MustHitMustNotOverlap),
            QueryItemIdLeakCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.QueryContainsItemId),
            MissingSourceRefsCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MissingSourceRefs),
            MissingEvidenceRefsCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MissingEvidenceRefs),
            MissingProvenanceCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MissingProvenance),
            LifecycleTargetSectionMismatchCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.LifecycleTargetSectionMismatch),
            RelationEvidenceMissingCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.RelationEvidenceMissing),
            SplitIsolationViolationCount = Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation),
            IssueCount = issues.Count,
            IssueBreakdown = issueBreakdown,
            Issues = issues
                .OrderBy(static issue => issue.IssueType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static issue => issue.SampleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static issue => issue.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .ToArray(),
            GeneratesFormalDataset = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = ResolveRecommendation(issueBreakdown)
        };
    }

    public RetrievalDatasetLegacyLimitationReport BuildLegacyLimitationReport(
        VectorLifecycleMetadataEvidenceBackfillReport? evidenceBackfill,
        VectorLifecycleMetadataReviewCandidateReport? reviewCandidates)
    {
        var candidateCount = evidenceBackfill?.CandidateCount ?? reviewCandidates?.CandidateCount ?? 0;
        var missingCount = evidenceBackfill?.NeedsEvidenceCount ?? candidateCount;
        return new RetrievalDatasetLegacyLimitationReport
        {
            OperationId = $"retrieval-dataset-legacy-limitation-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            BatchId = evidenceBackfill?.BatchId ?? string.Empty,
            ReviewCandidateCount = candidateCount,
            MissingEvidenceSourceProvenanceCandidateCount = missingCount,
            EvidenceBackfillRecommendation = evidenceBackfill?.Recommendation ?? string.Empty,
            LegacyDatasetSuitableForPrimaryRecallRepair = false,
            Limitations =
            [
                $"{missingCount} lifecycle metadata review candidates lack evidence/source/provenance required by Dataset V2.",
                "Legacy eval corpus can explain recall loss but cannot safely justify lifecycle repair decisions.",
                "Recall repair should move to Dataset V2 ingestion metadata rather than manual repair of legacy labels."
            ],
            RequiredNextDataWork =
            [
                "Backfill sourceRefs/evidenceRefs/provenance at ingestion time.",
                "Add lifecycle/reviewStatus/replacementState metadata to corpus items.",
                "Attach relation evidence for deprecation and replacement state.",
                "Validate split isolation and query label hygiene before using a dataset for recall repair."
            ],
            GeneratesFormalDataset = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill
        };
    }

    public static string BuildContractMarkdown(RetrievalDatasetV2MetadataContractReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Metadata Contract");
        builder.AppendLine();
        builder.AppendLine($"- ContractVersion: `{report.ContractVersion}`");
        builder.AppendLine($"- GeneratesFormalDataset: `{report.GeneratesFormalDataset}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Corpus Item Required Fields", report.CorpusItemRequiredFields);
        AppendList(builder, "Query Sample Required Fields", report.QuerySampleRequiredFields);
        AppendList(builder, "Lifecycle Rules", report.LifecycleRules);
        AppendList(builder, "Target Section Rules", report.TargetSectionRules);
        AppendList(builder, "Relation Evidence Rules", report.RelationEvidenceRules);
        AppendList(builder, "Split Isolation Rules", report.SplitIsolationRules);
        return builder.ToString();
    }

    public static string BuildValidationMarkdown(RetrievalDatasetV2ValidationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Validation Report");
        builder.AppendLine();
        builder.AppendLine($"- CorpusItemCount: `{report.CorpusItemCount}`");
        builder.AppendLine($"- QuerySampleCount: `{report.QuerySampleCount}`");
        builder.AppendLine($"- MustHitCount: `{report.MustHitCount}`");
        builder.AppendLine($"- MustNotCount: `{report.MustNotCount}`");
        builder.AppendLine($"- MissingSourceRefsCount: `{report.MissingSourceRefsCount}`");
        builder.AppendLine($"- MissingEvidenceRefsCount: `{report.MissingEvidenceRefsCount}`");
        builder.AppendLine($"- MissingProvenanceCount: `{report.MissingProvenanceCount}`");
        builder.AppendLine($"- IssueCount: `{report.IssueCount}`");
        builder.AppendLine($"- GeneratesFormalDataset: `{report.GeneratesFormalDataset}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendBreakdown(builder, report.IssueBreakdown);
        builder.AppendLine();
        builder.AppendLine("| Issue | Sample | Item | Split | Message |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var issue in report.Issues.Take(120))
        {
            builder.AppendLine($"| {issue.IssueType} | {Escape(issue.SampleId)} | {Escape(issue.ItemId)} | {Escape(issue.Split)} | {Escape(issue.Message)} |");
        }

        return builder.ToString();
    }

    public static string BuildLegacyLimitationMarkdown(RetrievalDatasetLegacyLimitationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Legacy Retrieval Dataset Limitation Report");
        builder.AppendLine();
        builder.AppendLine($"- BatchId: `{report.BatchId}`");
        builder.AppendLine($"- ReviewCandidateCount: `{report.ReviewCandidateCount}`");
        builder.AppendLine($"- MissingEvidenceSourceProvenanceCandidateCount: `{report.MissingEvidenceSourceProvenanceCandidateCount}`");
        builder.AppendLine($"- EvidenceBackfillRecommendation: `{report.EvidenceBackfillRecommendation}`");
        builder.AppendLine($"- LegacyDatasetSuitableForPrimaryRecallRepair: `{report.LegacyDatasetSuitableForPrimaryRecallRepair}`");
        builder.AppendLine($"- GeneratesFormalDataset: `{report.GeneratesFormalDataset}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Limitations", report.Limitations);
        AppendList(builder, "Required Next Data Work", report.RequiredNextDataWork);
        return builder.ToString();
    }

    private static void ValidateCorpusItem(
        VectorReindexSourceItem item,
        IReadOnlyList<ContextRelation> relations,
        ICollection<RetrievalDatasetV2ValidationIssue> issues)
    {
        var split = Metadata(item, "split");
        if (string.IsNullOrWhiteSpace(split))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation, string.Empty, item.ItemId, split, "corpus item must declare split.");
        }

        if (!HasAnyRef(item.Metadata, "sourceRefs", "sourceRef"))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MissingSourceRefs, string.Empty, item.ItemId, split, "corpus item must include sourceRefs.");
        }

        if (!HasAnyRef(item.Metadata, "evidenceRefs", "evidenceRef"))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MissingEvidenceRefs, string.Empty, item.ItemId, split, "corpus item must include evidenceRefs.");
        }

        if (string.IsNullOrWhiteSpace(Metadata(item, "provenanceRecordId", "provenanceId", "sourceFingerprint", "fingerprint", "contentHash")))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MissingProvenance, string.Empty, item.ItemId, split, "corpus item must include provenance record or source fingerprint.");
        }

        var lifecycle = Metadata(item, "lifecycle", "status");
        var targetSection = Metadata(item, "targetSection");
        var replacementState = Metadata(item, "replacementState", "supersededBy");
        if (string.Equals(targetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (!IsNormalLifecycle(lifecycle) || IsReplacementConflict(replacementState)))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.LifecycleTargetSectionMismatch, string.Empty, item.ItemId, split, "normal_context requires Active/Current/Stable lifecycle and non-superseded replacement state.");
        }

        if (relations.Any(IsLifecycleRelationWithoutEvidence))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.RelationEvidenceMissing, string.Empty, item.ItemId, split, "lifecycle/replacement relation must include evidenceRefs or sourceRefs.");
        }
    }

    private static void ValidateSample(
        ContextEvalSample sample,
        IReadOnlyDictionary<string, VectorReindexSourceItem> corpusById,
        ICollection<RetrievalDatasetV2ValidationIssue> issues)
    {
        var split = Metadata(sample, "split");
        if (string.IsNullOrWhiteSpace(split))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation, sample.Id, string.Empty, split, "query sample must declare split.");
        }

        if (!HasAnyRef(sample.Metadata, "sourceRefs", "sourceRef"))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MissingSourceRefs, sample.Id, string.Empty, split, "query sample must include sourceRefs.");
        }

        if (!HasAnyRef(sample.Metadata, "evidenceRefs", "evidenceRef"))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MissingEvidenceRefs, sample.Id, string.Empty, split, "query sample must include evidenceRefs.");
        }

        if (string.IsNullOrWhiteSpace(Metadata(sample, "provenanceRecordId", "provenanceId", "sourceFingerprint", "fingerprint")))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MissingProvenance, sample.Id, string.Empty, split, "query sample must include provenance.");
        }

        var mustHit = sample.MustHit
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mustNot = sample.MustNotHit
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var item in mustHit)
        {
            if (!corpusById.ContainsKey(item))
            {
                AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MustHitMissingFromCorpus, sample.Id, item, split, "mustHit item must exist in corpus.");
            }
            else
            {
                ValidateSampleCorpusSplit(sample.Id, item, split, corpusById[item], issues);
            }

            if (ContainsToken(sample.Query, item))
            {
                AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.QueryContainsItemId, sample.Id, item, split, "query text must not contain target item id.");
            }
        }

        foreach (var item in mustNot)
        {
            if (!corpusById.ContainsKey(item))
            {
                AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MustNotMissingFromCorpus, sample.Id, item, split, "mustNot item must exist in corpus.");
            }
            else
            {
                ValidateSampleCorpusSplit(sample.Id, item, split, corpusById[item], issues);
            }
        }

        foreach (var item in mustHit.Intersect(mustNot, StringComparer.OrdinalIgnoreCase))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.MustHitMustNotOverlap, sample.Id, item, split, "mustHit and mustNot must not overlap.");
        }
    }

    private static string ResolveRecommendation(IReadOnlyDictionary<string, int> issueBreakdown)
    {
        if (issueBreakdown.Count == 0)
        {
            return RetrievalDatasetV2ValidationRecommendations.ReadyForDatasetV2Authoring;
        }

        if (Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.QueryContainsItemId) > 0
            || Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MustHitMustNotOverlap) > 0)
        {
            return RetrievalDatasetV2ValidationRecommendations.NeedsQueryLabelHygiene;
        }

        if (Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.RelationEvidenceMissing) > 0)
        {
            return RetrievalDatasetV2ValidationRecommendations.NeedsRelationEvidenceBackfill;
        }

        if (Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MissingSourceRefs) > 0
            || Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MissingEvidenceRefs) > 0
            || Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.MissingProvenance) > 0
            || Count(issueBreakdown, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation) > 0)
        {
            return RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill;
        }

        return RetrievalDatasetV2ValidationRecommendations.KeepPreviewOnly;
    }

    private static void AddDuplicateIssues(
        IReadOnlyList<VectorReindexSourceItem> corpusItems,
        IReadOnlyList<ContextEvalSample> samples,
        ICollection<RetrievalDatasetV2ValidationIssue> issues)
    {
        foreach (var group in corpusItems
                     .Where(static item => !string.IsNullOrWhiteSpace(item.ItemId))
                     .GroupBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation, string.Empty, group.Key, string.Empty, "corpus item id must be unique across splits.");
        }

        foreach (var group in samples
                     .Where(static sample => !string.IsNullOrWhiteSpace(sample.Id))
                     .GroupBy(static sample => sample.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation, group.Key, string.Empty, string.Empty, "query sample id must be unique across splits.");
        }
    }

    private static void ValidateSampleCorpusSplit(
        string sampleId,
        string itemId,
        string sampleSplit,
        VectorReindexSourceItem corpusItem,
        ICollection<RetrievalDatasetV2ValidationIssue> issues)
    {
        var corpusSplit = Metadata(corpusItem, "split");
        if (string.IsNullOrWhiteSpace(sampleSplit)
            || string.IsNullOrWhiteSpace(corpusSplit)
            || string.Equals(corpusSplit, "shared", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sampleSplit, corpusSplit, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddIssue(issues, RetrievalDatasetV2ValidationIssueTypes.SplitIsolationViolation, sampleId, itemId, sampleSplit, "query sample must not reference corpus items from a different non-shared split.");
    }

    private static bool IsLifecycleRelationWithoutEvidence(ContextRelation relation)
    {
        if (!ContainsAny(relation.RelationType, "replace", "supersede", "deprecat", "historical"))
        {
            return false;
        }

        return relation.SourceRefs.Count == 0
               && !HasAnyRef(relation.Metadata, "sourceRefs", "sourceRef", "evidenceRefs", "evidenceRef");
    }

    private static void AddIssue(
        ICollection<RetrievalDatasetV2ValidationIssue> issues,
        string issueType,
        string sampleId,
        string itemId,
        string split,
        string message)
    {
        issues.Add(new RetrievalDatasetV2ValidationIssue
        {
            IssueType = issueType,
            SampleId = sampleId,
            ItemId = itemId,
            Split = split,
            Message = message
        });
    }

    private static int Count(IReadOnlyDictionary<string, int> values, string key)
        => values.TryGetValue(key, out var value) ? value : 0;

    private static bool HasAnyRef(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        return keys.Any(key => metadata.TryGetValue(key, out var value)
                               && !string.IsNullOrWhiteSpace(value)
                               && value.Split(',', ';', '|').Any(static item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string Metadata(VectorReindexSourceItem item, params string[] keys)
        => Metadata(item.Metadata, keys);

    private static string Metadata(ContextEvalSample sample, params string[] keys)
        => Metadata(sample.Metadata, keys);

    private static string Metadata(IReadOnlyDictionary<string, string> metadata, params string[] keys)
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

    private static bool ContainsToken(string text, string itemId)
    {
        return !string.IsNullOrWhiteSpace(text)
               && !string.IsNullOrWhiteSpace(itemId)
               && text.Contains(itemId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNormalLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Stable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReplacementConflict(string value)
        => ContainsAny(value, "superseded", "replaced", "deprecated", "conflict");

    private static bool ContainsAny(string value, params string[] tokens)
    {
        return !string.IsNullOrWhiteSpace(value)
               && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendBreakdown(StringBuilder builder, IReadOnlyDictionary<string, int> breakdown)
    {
        builder.AppendLine();
        builder.AppendLine("## Issue Breakdown");
        builder.AppendLine();
        builder.AppendLine("| Issue | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in breakdown.OrderByDescending(static item => item.Value).ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.Key} | {item.Value} |");
        }
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static string Escape(string value)
        => value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
