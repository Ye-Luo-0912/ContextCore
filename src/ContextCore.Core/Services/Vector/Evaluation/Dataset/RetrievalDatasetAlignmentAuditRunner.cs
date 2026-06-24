using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Retrieval dataset / query-corpus alignment audit；只读检查 eval 样本、当前 indexed corpus 与 provider scope 的对齐情况。
/// 不接 formal retrieval，不改变 retrieval/planning/scoring/PackingPolicy/package output。
/// </summary>
public sealed class RetrievalDatasetAlignmentAuditRunner
{
    private static readonly string[] TextMetadataKeys =
    [
        "indexedText",
        "title",
        "summary",
        "sourceTags",
        "sourceKind",
        "itemKind"
    ];

    private readonly VectorQueryProfileRegistry _profileRegistry;
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;

    public RetrievalDatasetAlignmentAuditRunner(
        VectorQueryProfileRegistry? profileRegistry = null,
        VectorCandidateEligibilityPolicy? eligibilityPolicy = null)
    {
        _profileRegistry = profileRegistry ?? new VectorQueryProfileRegistry();
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
    }

    public RetrievalDatasetAlignmentAuditReport BuildReport(
        string datasetName,
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> indexedEntries,
        EmbeddingProviderOptions providerOptions,
        string? profileId = null,
        IReadOnlyList<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(indexedEntries);
        ArgumentNullException.ThrowIfNull(providerOptions);

        var resolvedProfile = _profileRegistry.Resolve(string.IsNullOrWhiteSpace(profileId)
            ? VectorQueryProfileIds.NormalV1
            : profileId);
        var sourceById = sourceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var entriesById = indexedEntries
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var providerScopedEntries = indexedEntries
            .Where(entry => MatchesProviderScope(entry, providerOptions))
            .ToArray();
        var providerEntriesById = providerScopedEntries
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var corpusTokens = BuildCorpusTokens(sourceItems, indexedEntries);
        var issues = new List<RetrievalDatasetAlignmentIssue>();

        var mustHitCount = 0;
        var mustNotCount = 0;
        var presentInCorpus = 0;
        var presentInProviderScope = 0;
        var blockedByEligibility = 0;
        var anchorCovered = 0;
        var sourceKindCovered = 0;
        var tokenCoverageSum = 0.0;
        var overlapSum = 0.0;

        foreach (var sample in samples)
        {
            var queryTokens = Tokenize(sample.Query);
            var overlapTokens = queryTokens
                .Where(corpusTokens.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            tokenCoverageSum += queryTokens.Count == 0 ? 0 : 1;
            overlapSum += queryTokens.Count == 0 ? 0 : (double)overlapTokens.Length / queryTokens.Count;

            if (queryTokens.Count == 0)
            {
                issues.Add(NewIssue(datasetName, sample, string.Empty, RetrievalDatasetAlignmentIssueTypes.QueryTokenTooSparse, queryTokens, overlapTokens, null, null, null, "query text 没有可用于通用 token/anchor 对齐的 token。"));
            }
            else if (overlapTokens.Length == 0)
            {
                issues.Add(NewIssue(datasetName, sample, string.Empty, RetrievalDatasetAlignmentIssueTypes.QueryCorpusTokenMismatch, queryTokens, overlapTokens, null, null, null, "query token 与当前 indexed corpus token 没有重叠。"));
            }

            mustHitCount += sample.MustHit.Count(item => !string.IsNullOrWhiteSpace(item));
            mustNotCount += sample.MustNotHit.Count(item => !string.IsNullOrWhiteSpace(item));
            foreach (var mustHit in sample.MustHit
                         .Where(item => !string.IsNullOrWhiteSpace(item))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var source = FindSource(sourceById, mustHit);
                var entry = FindEntry(entriesById, mustHit);
                var providerEntry = FindEntry(providerEntriesById, mustHit);
                var metadata = ResolveMetadata(source, entry, providerEntry);
                var itemKind = ResolveItemKind(source, entry, providerEntry);
                var sourceKind = ResolveSourceKind(source, entry, providerEntry);
                var sourceTags = SplitCsv(GetMetadata(metadata, "sourceTags"));
                var mustHitQueryOverlap = queryTokens
                    .Where(token => BuildItemTokens(source, entry, providerEntry).Contains(token))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (entry is not null)
                {
                    presentInCorpus++;
                }
                else
                {
                    issues.Add(NewIssue(datasetName, sample, mustHit, RetrievalDatasetAlignmentIssueTypes.MustHitMissingFromCorpus, queryTokens, mustHitQueryOverlap, source, entry, providerEntry, "mustHit 不存在于当前 indexed corpus。"));
                }

                if (providerEntry is not null)
                {
                    presentInProviderScope++;
                    var eligibility = _eligibilityPolicy.Evaluate(resolvedProfile, providerEntry, similarity: 1.0, diagnostics: Array.Empty<string>());
                    if (!string.Equals(eligibility.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
                    {
                        blockedByEligibility++;
                        var issueType = eligibility.BlockedReasons.Any(IsLifecycleBlockedReason)
                            ? RetrievalDatasetAlignmentIssueTypes.MustHitLifecycleFiltered
                            : RetrievalDatasetAlignmentIssueTypes.MustHitBlockedByEligibility;
                        issues.Add(NewIssue(datasetName, sample, mustHit, issueType, queryTokens, mustHitQueryOverlap, source, entry, providerEntry, "mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。", eligibility.BlockedReasons));
                    }
                }
                else
                {
                    if (entry is not null)
                    {
                        issues.Add(NewIssue(datasetName, sample, mustHit, RetrievalDatasetAlignmentIssueTypes.MustHitMissingFromProviderScope, queryTokens, mustHitQueryOverlap, source, entry, null, "mustHit 已索引，但不在当前 provider/model/dimension scope 中。"));
                        issues.Add(NewIssue(datasetName, sample, mustHit, RetrievalDatasetAlignmentIssueTypes.ProviderScopeMismatch, queryTokens, mustHitQueryOverlap, source, entry, null, "当前 provider scope 与 mustHit indexed entry 不对齐。"));
                    }
                }

                if (!string.IsNullOrWhiteSpace(itemKind) || sourceTags.Count > 0)
                {
                    anchorCovered++;
                }
                else
                {
                    issues.Add(NewIssue(datasetName, sample, mustHit, RetrievalDatasetAlignmentIssueTypes.MissingAnchorMetadata, queryTokens, mustHitQueryOverlap, source, entry, providerEntry, "mustHit 缺少 sourceTags / itemKind anchor metadata。"));
                }

                if (!string.IsNullOrWhiteSpace(sourceKind))
                {
                    sourceKindCovered++;
                }
                else
                {
                    issues.Add(NewIssue(datasetName, sample, mustHit, RetrievalDatasetAlignmentIssueTypes.SourceKindMismatch, queryTokens, mustHitQueryOverlap, source, entry, providerEntry, "mustHit 缺少 sourceKind 或等价 layer metadata。"));
                }
            }
        }

        var corpusCoverageGap = Math.Max(0, sourceItems.Count - indexedEntries.Select(entry => entry.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (corpusCoverageGap > 0)
        {
            for (var i = 0; i < corpusCoverageGap; i++)
            {
                issues.Add(new RetrievalDatasetAlignmentIssue
                {
                    DatasetName = datasetName,
                    IssueType = RetrievalDatasetAlignmentIssueTypes.CorpusCoverageRegression,
                    Notes = "当前 indexed corpus entry count 少于 eval corpus source item count。"
                });
            }
        }

        var issueBreakdown = issues
            .GroupBy(issue => string.IsNullOrWhiteSpace(issue.IssueType) ? RetrievalDatasetAlignmentIssueTypes.Unknown : issue.IssueType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new RetrievalDatasetAlignmentAuditReport
        {
            OperationId = $"vector-retrieval-dataset-alignment-audit-{datasetName.ToLowerInvariant()}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            ProviderId = providerOptions.ProviderId,
            EmbeddingModel = providerOptions.EmbeddingModel,
            Dimension = providerOptions.Dimension,
            UseForRuntime = false,
            SampleCount = samples.Count,
            QueryCount = samples.Count,
            MustHitCount = mustHitCount,
            MustNotCount = mustNotCount,
            MustHitPresentInCorpusCount = presentInCorpus,
            MustHitMissingFromCorpusCount = Math.Max(0, mustHitCount - presentInCorpus),
            MustHitPresentInProviderScopeCount = presentInProviderScope,
            MustHitBlockedByEligibilityCount = blockedByEligibility,
            QueryTokenCoverageAverage = samples.Count == 0 ? 0 : tokenCoverageSum / samples.Count,
            QueryCorpusTokenOverlapAverage = samples.Count == 0 ? 0 : overlapSum / samples.Count,
            AnchorCoverageRate = mustHitCount == 0 ? 1 : (double)anchorCovered / mustHitCount,
            SourceKindCoverageRate = mustHitCount == 0 ? 1 : (double)sourceKindCovered / mustHitCount,
            CorpusEntryCount = indexedEntries.Count,
            ProviderScopedEntryCount = providerScopedEntries.Length,
            AlignmentIssueCount = issues.Count,
            IssueBreakdown = issueBreakdown,
            Issues = issues
                .OrderBy(issue => issue.IssueType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(issue => issue.SampleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(issue => issue.MustHitItemId, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToArray(),
            Recommendation = Recommend(issueBreakdown, mustHitCount, presentInCorpus, presentInProviderScope),
            FormalOutputChanged = 0,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }

    public static RetrievalDatasetAlignmentAuditSummaryReport BuildSummary(
        IReadOnlyList<RetrievalDatasetAlignmentAuditReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var issueBreakdown = reports
            .SelectMany(report => report.IssueBreakdown)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase);

        return new RetrievalDatasetAlignmentAuditSummaryReport
        {
            OperationId = $"vector-retrieval-dataset-alignment-audit-summary-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Reports = reports.ToArray(),
            Recommendation = RecommendSummary(reports),
            AlignmentIssueCount = reports.Sum(report => report.AlignmentIssueCount),
            IssueBreakdown = issueBreakdown,
            FormalRetrievalAllowed = false,
            UseForRuntime = false
        };
    }

    public static string BuildMarkdownReport(RetrievalDatasetAlignmentAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine($"# Vector Retrieval Dataset Alignment Audit - {report.DatasetName}");
        builder.AppendLine();
        AppendReport(builder, report);
        return builder.ToString();
    }

    public static string BuildMarkdownSummary(RetrievalDatasetAlignmentAuditSummaryReport summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Retrieval Dataset Alignment Audit Summary");
        builder.AppendLine();
        builder.AppendLine($"Generated: {summary.CreatedAt:O}");
        builder.AppendLine($"- Recommendation: `{summary.Recommendation}`");
        builder.AppendLine($"- AlignmentIssueCount: `{summary.AlignmentIssueCount}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{summary.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{summary.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Samples | MustHit | Corpus Coverage | Provider Scope | Eligibility Blocks | Query Tokens | Token Overlap | Anchor Coverage | SourceKind Coverage | Issues | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var report in summary.Reports)
        {
            var corpusCoverage = report.MustHitCount == 0 ? 1 : (double)report.MustHitPresentInCorpusCount / report.MustHitCount;
            var providerCoverage = report.MustHitCount == 0 ? 1 : (double)report.MustHitPresentInProviderScopeCount / report.MustHitCount;
            builder.AppendLine($"| {report.DatasetName} | {report.SampleCount} | {report.MustHitCount} | {corpusCoverage:P2} | {providerCoverage:P2} | {report.MustHitBlockedByEligibilityCount} | {report.QueryTokenCoverageAverage:P2} | {report.QueryCorpusTokenOverlapAverage:P2} | {report.AnchorCoverageRate:P2} | {report.SourceKindCoverageRate:P2} | {report.AlignmentIssueCount} | {report.Recommendation} |");
        }

        builder.AppendLine();
        AppendBreakdown(builder, summary.IssueBreakdown);
        foreach (var report in summary.Reports)
        {
            builder.AppendLine();
            AppendReport(builder, report);
        }

        return builder.ToString();
    }

    private static void AppendReport(StringBuilder builder, RetrievalDatasetAlignmentAuditReport report)
    {
        builder.AppendLine($"## {report.DatasetName}");
        builder.AppendLine();
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- EmbeddingModel: `{report.EmbeddingModel}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- MustHitCount: `{report.MustHitCount}`");
        builder.AppendLine($"- MustNotCount: `{report.MustNotCount}`");
        builder.AppendLine($"- MustHitPresentInCorpusCount: `{report.MustHitPresentInCorpusCount}`");
        builder.AppendLine($"- MustHitMissingFromCorpusCount: `{report.MustHitMissingFromCorpusCount}`");
        builder.AppendLine($"- MustHitPresentInProviderScopeCount: `{report.MustHitPresentInProviderScopeCount}`");
        builder.AppendLine($"- MustHitBlockedByEligibilityCount: `{report.MustHitBlockedByEligibilityCount}`");
        builder.AppendLine($"- QueryTokenCoverageAverage: `{report.QueryTokenCoverageAverage:P2}`");
        builder.AppendLine($"- QueryCorpusTokenOverlapAverage: `{report.QueryCorpusTokenOverlapAverage:P2}`");
        builder.AppendLine($"- AnchorCoverageRate: `{report.AnchorCoverageRate:P2}`");
        builder.AppendLine($"- SourceKindCoverageRate: `{report.SourceKindCoverageRate:P2}`");
        builder.AppendLine($"- CorpusEntryCount: `{report.CorpusEntryCount}`");
        builder.AppendLine($"- ProviderScopedEntryCount: `{report.ProviderScopedEntryCount}`");
        builder.AppendLine($"- AlignmentIssueCount: `{report.AlignmentIssueCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        AppendBreakdown(builder, report.IssueBreakdown);
        builder.AppendLine();
        builder.AppendLine("| Issue | Sample | MustHit | QueryOverlap | SourceKind | ItemKind | Tags | Notes |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var issue in report.Issues.Take(100))
        {
            builder.AppendLine($"| {issue.IssueType} | {Escape(issue.SampleId)} | {Escape(issue.MustHitItemId)} | {Escape(string.Join(",", issue.CorpusOverlapTokens.Take(8)))} | {Escape(issue.SourceKind)} | {Escape(issue.ItemKind)} | {Escape(string.Join(",", issue.SourceTags.Take(8)))} | {Escape(issue.Notes)} |");
        }
    }

    private static void AppendBreakdown(StringBuilder builder, IReadOnlyDictionary<string, int> breakdown)
    {
        builder.AppendLine("### Issue Breakdown");
        builder.AppendLine();
        builder.AppendLine("| Issue | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in breakdown.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.Key} | {item.Value} |");
        }
    }

    private static string Recommend(
        IReadOnlyDictionary<string, int> issueBreakdown,
        int mustHitCount,
        int presentInCorpus,
        int presentInProviderScope)
    {
        if (Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.MustHitMissingFromCorpus) > 0
            || Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.CorpusCoverageRegression) > 0)
        {
            return RetrievalDatasetAlignmentRecommendations.NeedsCorpusBackfill;
        }

        if (Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.ProviderScopeMismatch) > 0
            || Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.MustHitMissingFromProviderScope) > 0
            || presentInProviderScope < presentInCorpus)
        {
            return RetrievalDatasetAlignmentRecommendations.NeedsProviderScopeRepair;
        }

        if (Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.MissingAnchorMetadata) > 0)
        {
            return RetrievalDatasetAlignmentRecommendations.NeedsAnchorMetadataBackfill;
        }

        if (Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.QueryTokenTooSparse) > 0
            || Count(issueBreakdown, RetrievalDatasetAlignmentIssueTypes.QueryCorpusTokenMismatch) > 0)
        {
            return RetrievalDatasetAlignmentRecommendations.NeedsQueryNormalizationRepair;
        }

        if (issueBreakdown.Count > 0 || presentInCorpus < mustHitCount)
        {
            return RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;
        }

        return RetrievalDatasetAlignmentRecommendations.ReadyForRecallSourceRepair;
    }

    private static string RecommendSummary(IReadOnlyList<RetrievalDatasetAlignmentAuditReport> reports)
    {
        if (reports.Count == 0)
        {
            return RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;
        }

        var priorities = new[]
        {
            RetrievalDatasetAlignmentRecommendations.NeedsCorpusBackfill,
            RetrievalDatasetAlignmentRecommendations.NeedsProviderScopeRepair,
            RetrievalDatasetAlignmentRecommendations.NeedsAnchorMetadataBackfill,
            RetrievalDatasetAlignmentRecommendations.NeedsQueryNormalizationRepair,
            RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly
        };
        foreach (var priority in priorities)
        {
            if (reports.Any(report => string.Equals(report.Recommendation, priority, StringComparison.OrdinalIgnoreCase)))
            {
                return priority;
            }
        }

        return RetrievalDatasetAlignmentRecommendations.ReadyForRecallSourceRepair;
    }

    private static RetrievalDatasetAlignmentIssue NewIssue(
        string datasetName,
        ContextEvalSample sample,
        string mustHit,
        string issueType,
        IReadOnlyList<string> queryTokens,
        IReadOnlyList<string> overlapTokens,
        VectorReindexSourceItem? source,
        VectorIndexEntry? entry,
        VectorIndexEntry? providerEntry,
        string notes,
        IReadOnlyList<string>? blockedReasons = null)
    {
        var metadata = ResolveMetadata(source, entry, providerEntry);
        return new RetrievalDatasetAlignmentIssue
        {
            DatasetName = datasetName,
            SampleId = sample.Id,
            Mode = sample.Mode,
            MustHitItemId = mustHit,
            IssueType = issueType,
            QueryText = sample.Query,
            QueryTokens = queryTokens,
            CorpusOverlapTokens = overlapTokens,
            SourceKind = ResolveSourceKind(source, entry, providerEntry),
            ItemKind = ResolveItemKind(source, entry, providerEntry),
            SourceTags = SplitCsv(GetMetadata(metadata, "sourceTags")),
            BlockedReasons = blockedReasons ?? Array.Empty<string>(),
            Notes = notes
        };
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
            return exact.OrderByDescending(entry => entry.UpdatedAt).First();
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

    private static HashSet<string> BuildCorpusTokens(
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> indexedEntries)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceItems)
        {
            AddTokens(tokens, source.Text);
            AddTokens(tokens, source.ItemKind);
            AddTokens(tokens, source.Layer);
            AddTokens(tokens, GetMetadata(source.Metadata, "sourceTags"));
            AddTokens(tokens, GetMetadata(source.Metadata, "sourceKind"));
        }

        foreach (var entry in indexedEntries)
        {
            AddTokens(tokens, entry.ItemKind);
            AddTokens(tokens, entry.Layer);
            foreach (var key in TextMetadataKeys)
            {
                AddTokens(tokens, GetMetadata(entry.Metadata, key));
            }
        }

        return tokens;
    }

    private static HashSet<string> BuildItemTokens(
        VectorReindexSourceItem? source,
        VectorIndexEntry? entry,
        VectorIndexEntry? providerEntry)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            AddTokens(tokens, source.Text);
            AddTokens(tokens, source.ItemKind);
            AddTokens(tokens, source.Layer);
            AddTokens(tokens, GetMetadata(source.Metadata, "sourceTags"));
            AddTokens(tokens, GetMetadata(source.Metadata, "sourceKind"));
        }

        foreach (var item in new[] { entry, providerEntry }.Where(item => item is not null))
        {
            AddTokens(tokens, item!.ItemKind);
            AddTokens(tokens, item.Layer);
            foreach (var key in TextMetadataKeys)
            {
                AddTokens(tokens, GetMetadata(item.Metadata, key));
            }
        }

        return tokens;
    }

    private static void AddTokens(ISet<string> target, string? text)
    {
        foreach (var token in Tokenize(text))
        {
            target.Add(token);
        }
    }

    private static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ascii = new StringBuilder();
        var cjk = new List<char>();

        foreach (var ch in text)
        {
            if (IsAsciiLetterOrDigit(ch))
            {
                if (cjk.Count > 0)
                {
                    AddCjkBigrams(tokens, cjk);
                    cjk.Clear();
                }

                ascii.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushAscii(tokens, ascii);
            if (IsCjk(ch))
            {
                cjk.Add(ch);
                tokens.Add(ch.ToString());
            }
            else if (!char.IsWhiteSpace(ch) && cjk.Count > 0)
            {
                AddCjkBigrams(tokens, cjk);
                cjk.Clear();
            }
        }

        FlushAscii(tokens, ascii);
        AddCjkBigrams(tokens, cjk);
        return tokens
            .Where(token => token.Length > 0)
            .OrderBy(token => token, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void FlushAscii(ISet<string> tokens, StringBuilder builder)
    {
        if (builder.Length >= 2)
        {
            tokens.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static void AddCjkBigrams(ISet<string> tokens, IReadOnlyList<char> chars)
    {
        if (chars.Count < 2)
        {
            return;
        }

        for (var i = 0; i + 1 < chars.Count; i++)
        {
            tokens.Add(new string([chars[i], chars[i + 1]]));
        }
    }

    private static bool IsAsciiLetterOrDigit(char ch)
    {
        return ch is >= 'a' and <= 'z'
               || ch is >= 'A' and <= 'Z'
               || ch is >= '0' and <= '9';
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u4E00' and <= '\u9FFF'
               || ch is >= '\u3400' and <= '\u4DBF'
               || ch is >= '\uF900' and <= '\uFAFF';
    }

    private static Dictionary<string, string> ResolveMetadata(
        VectorReindexSourceItem? source,
        VectorIndexEntry? entry,
        VectorIndexEntry? providerEntry)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach (var pair in source.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        foreach (var item in new[] { entry, providerEntry }.Where(item => item is not null))
        {
            foreach (var pair in item!.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        return metadata;
    }

    private static string ResolveItemKind(
        VectorReindexSourceItem? source,
        VectorIndexEntry? entry,
        VectorIndexEntry? providerEntry)
    {
        return FirstNonEmpty(source?.ItemKind, providerEntry?.ItemKind, entry?.ItemKind, GetMetadata(providerEntry?.Metadata, "itemKind"), GetMetadata(entry?.Metadata, "itemKind"));
    }

    private static string ResolveSourceKind(
        VectorReindexSourceItem? source,
        VectorIndexEntry? entry,
        VectorIndexEntry? providerEntry)
    {
        return FirstNonEmpty(
            GetMetadata(source?.Metadata, "sourceKind"),
            GetMetadata(providerEntry?.Metadata, "sourceKind"),
            GetMetadata(entry?.Metadata, "sourceKind"),
            providerEntry?.Layer,
            entry?.Layer,
            source?.Layer);
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

    private static string GetMetadata(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        return metadata is not null && metadata.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static IReadOnlyList<string> SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static int Count(IReadOnlyDictionary<string, int> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : 0;
    }

    private static string Escape(string value)
    {
        return string.IsNullOrEmpty(value) ? "-" : value.Replace("|", "/", StringComparison.Ordinal);
    }
}
