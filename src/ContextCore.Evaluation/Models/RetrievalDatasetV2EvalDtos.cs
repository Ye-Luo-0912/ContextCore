using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

/// <summary>retrieval dataset / query-corpus alignment audit 推荐结论。</summary>
public static class RetrievalDatasetAlignmentRecommendations
{
    public const string ReadyForRecallSourceRepair = nameof(ReadyForRecallSourceRepair);
    public const string NeedsCorpusBackfill = nameof(NeedsCorpusBackfill);
    public const string NeedsAnchorMetadataBackfill = nameof(NeedsAnchorMetadataBackfill);
    public const string NeedsQueryNormalizationRepair = nameof(NeedsQueryNormalizationRepair);
    public const string NeedsProviderScopeRepair = nameof(NeedsProviderScopeRepair);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>单条 query-corpus alignment 诊断记录；不进入 retrieval policy。</summary>
public sealed class RetrievalDatasetAlignmentIssue
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string IssueType { get; init; } = RetrievalDatasetAlignmentIssueTypes.Unknown;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryTokens { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CorpusOverlapTokens { get; init; } = Array.Empty<string>();

    public string SourceKind { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Notes { get; init; } = string.Empty;
}


/// <summary>retrieval dataset / query-corpus alignment audit 报告；只读评估，不改变正式检索。</summary>
public sealed class RetrievalDatasetAlignmentAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustNotCount { get; init; }

    public int MustHitPresentInCorpusCount { get; init; }

    public int MustHitMissingFromCorpusCount { get; init; }

    public int MustHitPresentInProviderScopeCount { get; init; }

    public int MustHitBlockedByEligibilityCount { get; init; }

    public double QueryTokenCoverageAverage { get; init; }

    public double QueryCorpusTokenOverlapAverage { get; init; }

    public double AnchorCoverageRate { get; init; }

    public double SourceKindCoverageRate { get; init; }

    public int CorpusEntryCount { get; init; }

    public int ProviderScopedEntryCount { get; init; }

    public int AlignmentIssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RetrievalDatasetAlignmentIssue> Issues { get; init; } =
        Array.Empty<RetrievalDatasetAlignmentIssue>();

    public string Recommendation { get; init; } = RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
