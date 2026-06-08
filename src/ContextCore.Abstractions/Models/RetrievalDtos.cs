using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>混合检索候选项来源类型。</summary>
public enum ContextRetrievalCandidateKind
{
    ContextItem,
    MemoryItem
}

/// <summary>混合检索请求。</summary>
public sealed class ContextRetrievalRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? QueryText { get; init; }

    public string? RewrittenQueryText { get; init; }

    public IReadOnlyList<string> RequiredTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<float> QueryVector { get; init; } = Array.Empty<float>();

    public string? ModelName { get; init; }

    /// <summary>BGE 等检索模型的 query instruction；为空时不添加前缀。</summary>
    public string QueryInstruction { get; init; } = "为这个句子生成表示以用于检索相关文章：";

    public int TopK { get; init; } = 10;

    public int CandidateTake { get; init; } = 50;

    public int VectorTopK { get; init; } = 20;

    public double? MinVectorScore { get; init; }

    /// <summary>关系扩展允许经过的关系类型；为空表示不限制关系类型。</summary>
    public IReadOnlyList<string> AllowedRelationTypes { get; init; } = Array.Empty<string>();

    /// <summary>关系扩展最大跳数。默认 1 跳，运行时会做上限保护，避免图遍历失控。</summary>
    public int RelationExpansionDepth { get; init; } = 1;

    public int TokenBudget { get; init; } = 4000;

    public bool IncludeKeywordRecall { get; init; } = true;

    public bool IncludeVectorRecall { get; init; } = true;

    public bool IncludeRelationExpansion { get; init; } = true;

    public bool IncludeWorkingMemory { get; init; } = true;

    public bool IncludeStableMemory { get; init; } = true;

    public bool IncludeContent { get; init; } = true;

    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>
    /// 可选的短期锚定召回计划。存在时，HybridContextRetriever 将按计划调整召回优先级和过滤策略。
    /// 为 null 时保持原有行为不变（eval 兼容）。
    /// </summary>
    public RetrievalPlan? Plan { get; init; }
}

/// <summary>混合检索结果。</summary>
public sealed class ContextRetrievalResult
{
    public string OperationId { get; init; } = string.Empty;

    public bool Succeeded { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<ContextRetrievalCandidate> SelectedItems { get; init; } = Array.Empty<ContextRetrievalCandidate>();

    public IReadOnlyList<ContextRetrievalDecision> DroppedItems { get; init; } = Array.Empty<ContextRetrievalDecision>();

    public int EstimatedTokens { get; init; }

    public ContextOperationUsage Usage { get; init; } = new();

    public ContextRetrievalTrace Trace { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Lifecycle-aware ranker shadow debug request. It is diagnostic-only and does not opt in runtime scoring.</summary>
public sealed class LifecycleAwareRankerShadowDebugRequest
{
    public string Query { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Mode { get; init; } = "ChatMode";

    public IReadOnlyList<string> CandidateIds { get; init; } = Array.Empty<string>();

    public bool IncludeLifecycleDetails { get; init; } = true;

    public int TopK { get; init; } = 10;

    public int CandidateTake { get; init; } = 50;

    public int TokenBudget { get; init; } = 4000;
}

/// <summary>Lifecycle-aware ranker shadow debug response. Formal retrieval output is reported but never mutated.</summary>
public sealed class LifecycleAwareRankerShadowDebugResponse
{
    public string OperationId { get; init; } = string.Empty;

    public string RetrievalOperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool RankerShadowEnabled { get; init; }

    public bool DebugEndpointEnabled { get; init; }

    public string RankerShadowProfile { get; init; } = string.Empty;

    public bool FormalOutputChanged { get; init; }

    public bool SelectedSetChanged { get; init; }

    public IReadOnlyList<string> LegacySelectedIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FinalSelectedIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> CandidateScores { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> DeprecatedDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> HistoricalDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> CurrentActivePromotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> VersionConflictFixes { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustHitDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustNotHitPromotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>检索候选项，统一承载原始上下文和记忆条目。</summary>
public sealed class ContextRetrievalCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public ContextRetrievalCandidateKind Kind { get; init; }

    public string Type { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string Content { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>检索候选项的选中或丢弃决策。</summary>
public sealed class ContextRetrievalDecision
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public ContextRetrievalCandidateKind Kind { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>检索流程中的一个阶段摘要。</summary>
public sealed class ContextRetrievalStageTrace
{
    public string Name { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>完整检索 trace，记录候选、阶段和最终选择。</summary>
public sealed class ContextRetrievalTrace
{
    public string RetrievalId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? QueryText { get; init; }

    public string? RewrittenQueryText { get; init; }

    public IReadOnlyList<ContextRetrievalStageTrace> Stages { get; init; } = Array.Empty<ContextRetrievalStageTrace>();

    public IReadOnlyList<ContextRetrievalCandidate> Candidates { get; init; } = Array.Empty<ContextRetrievalCandidate>();

    public IReadOnlyList<ContextRetrievalDecision> SelectedItems { get; init; } = Array.Empty<ContextRetrievalDecision>();

    public IReadOnlyList<ContextRetrievalDecision> DroppedItems { get; init; } = Array.Empty<ContextRetrievalDecision>();

    public IReadOnlyList<ContextAttentionScore> AttentionScores { get; init; } = Array.Empty<ContextAttentionScore>();

    public AttentionShadowReport AttentionShadowReport { get; init; } = new();

    public AttentionProfileExperimentReport AttentionProfileComparison { get; init; } = new();

    public AttentionRerankComparisonReport AttentionRerankComparison { get; init; } = new();

    public LifecycleAwareRankerShadowTrace RankerShadowTrace { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Exportable lifecycle-aware ranker shadow trace record captured from retrieval traces.</summary>
public sealed class LifecycleAwareRankerShadowTraceRecord
{
    public string RetrievalId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> CandidateScores { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> DeprecatedDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> VersionConflictFixes { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustHitDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustNotHitPromotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Supported next-step recommendations for runtime ranker shadow trace quality.</summary>
public static class RankerShadowTraceRecommendedNextSteps
{
    public const string KeepShadowOnly = nameof(KeepShadowOnly);
    public const string ReadyForGuardedOptIn = nameof(ReadyForGuardedOptIn);
    public const string NeedsMoreRealTraces = nameof(NeedsMoreRealTraces);
    public const string BlockedByRisk = nameof(BlockedByRisk);
}

/// <summary>Runtime lifecycle-aware ranker shadow trace quality report.</summary>
public sealed class RankerShadowTraceQualityReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TraceCount { get; init; }

    public int CandidateScoreCount { get; init; }

    public int DeprecatedDemotionCount { get; init; }

    public int HistoricalDemotionCount { get; init; }

    public int VersionConflictFixCount { get; init; }

    public int CurrentVersionPromotionCount { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public int LifecycleViolationCount { get; init; }

    public double AverageScoreDelta { get; init; }

    public double MaxPositiveDelta { get; init; }

    public double MaxNegativeDelta { get; init; }

    public IReadOnlyDictionary<string, RankerShadowTraceQualityBreakdown> ModeBreakdown { get; init; } =
        new Dictionary<string, RankerShadowTraceQualityBreakdown>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RankerShadowTraceQualityBreakdown> IntentBreakdown { get; init; } =
        new Dictionary<string, RankerShadowTraceQualityBreakdown>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RankerShadowTraceRiskSample> RiskSamples { get; init; } =
        Array.Empty<RankerShadowTraceRiskSample>();

    public string RecommendedNextStep { get; init; } = RankerShadowTraceRecommendedNextSteps.NeedsMoreRealTraces;

    public string PolicyVersion { get; init; } = string.Empty;
}

/// <summary>Mode or intent level quality summary for runtime ranker shadow traces.</summary>
public sealed class RankerShadowTraceQualityBreakdown
{
    public string Key { get; init; } = string.Empty;

    public int TraceCount { get; init; }

    public int CandidateScoreCount { get; init; }

    public int DeprecatedDemotionCount { get; init; }

    public int HistoricalDemotionCount { get; init; }

    public int VersionConflictFixCount { get; init; }

    public int CurrentVersionPromotionCount { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public double AverageScoreDelta { get; init; }
}

/// <summary>Risk sample emitted when shadow lifecycle-aware scoring demotes must-hit or promotes must-not-hit items.</summary>
public sealed class RankerShadowTraceRiskSample
{
    public string RetrievalId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string RiskType { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public double ScoreDelta { get; init; }

    public string Reason { get; init; } = string.Empty;
}
