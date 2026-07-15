namespace ContextCore.Abstractions.Models;

/// <summary>Vector index 使用的 embedding provider 类型。</summary>
public static class EmbeddingProviderTypes
{
    public const string DeterministicHash = nameof(DeterministicHash);

    public const string OnnxLocal = nameof(OnnxLocal);

    public const string Disabled = nameof(Disabled);
}

/// <summary>Embedding provider 配置；模型路径应走本地私有配置，不提交模型文件。</summary>
public sealed class EmbeddingProviderOptions
{
    public string ProviderId { get; init; } = "deterministic-hash";

    public string ProviderType { get; init; } = EmbeddingProviderTypes.DeterministicHash;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public string EmbeddingModel { get; init; } = "deterministic-hash-v1";

    public int Dimension { get; init; } = 16;

    public bool Normalize { get; init; } = true;

    public string PoolingStrategy { get; init; } = "Mean";

    public int MaxTokens { get; init; } = 256;

    public int BatchSize { get; init; } = 32;

    public string Device { get; init; } = "cpu";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 标记该 provider 是否提供真正的语义检索能力。
    /// DeterministicHash 仅为可重复基础设施测试 provider，不产生语义向量，应设为 false。
    /// OnnxLocal / Mock / External 等真正模型 provider 应设为 true。
    /// </summary>
    public bool IsSemanticRetrieval { get; init; } = false;

    /// <summary>
    /// Embedding 缓存最大条目数，超过后按 LRU 策略淘汰最久未访问的条目。
    /// 设为 0 表示不缓存，设为负数表示无上限（不推荐生产使用）。
    /// </summary>
    public int CacheMaxEntries { get; init; } = 10000;
}

/// <summary>embedding tokenizer 输出；用于 ONNX input tensor 构造。</summary>
public sealed class EmbeddingTokenizationResult
{
    public int BatchSize { get; init; }

    public int SequenceLength { get; init; }

    public long[] InputIds { get; init; } = Array.Empty<long>();

    public long[] AttentionMask { get; init; } = Array.Empty<long>();

    public long[] TokenTypeIds { get; init; } = Array.Empty<long>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector index 查询条件。</summary>
public sealed class VectorIndexQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? ItemKind { get; init; }

    public string? Layer { get; init; }

    public string? EmbeddingModel { get; init; }

    public string? EmbeddingProvider { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 100;

    public bool IncludeVector { get; init; }
}

/// <summary>vector index brute-force 余弦查询条件。</summary>
public sealed class VectorIndexSearchQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    public string? EmbeddingModel { get; init; }

    public string? EmbeddingProvider { get; init; }

    public int? Dimension { get; init; }

    public int TopK { get; init; } = 10;

    public double? MinScore { get; init; }

    public bool IncludeVector { get; init; }
}

/// <summary>vector index brute-force 查询结果。</summary>
public sealed class VectorIndexSearchResult
{
    public VectorIndexEntry Entry { get; init; } = new();

    public double Score { get; init; }

    public int Rank { get; init; }
}

/// <summary>vector index 诊断类型。</summary>
public static class VectorIndexDiagnosticTypes
{
    public const string MissingEmbedding = nameof(MissingEmbedding);

    public const string StaleEmbedding = nameof(StaleEmbedding);

    public const string ContentHashMismatch = nameof(ContentHashMismatch);

    public const string DimensionMismatch = nameof(DimensionMismatch);

    public const string UnsupportedEmbeddingModel = nameof(UnsupportedEmbeddingModel);

    public const string ProviderUnavailable = nameof(ProviderUnavailable);

    public const string DuplicateVectorEntry = nameof(DuplicateVectorEntry);

    public const string OrphanVectorEntry = nameof(OrphanVectorEntry);

    public const string ModelFileMissing = nameof(ModelFileMissing);

    public const string TokenizerUnavailable = nameof(TokenizerUnavailable);

    public const string EmbeddingModelMismatch = nameof(EmbeddingModelMismatch);

    public const string ProviderMismatch = nameof(ProviderMismatch);

    public const string NormalizationMismatch = nameof(NormalizationMismatch);

    public const string UnsupportedPoolingStrategy = nameof(UnsupportedPoolingStrategy);

    public const string OnnxSessionFailed = nameof(OnnxSessionFailed);

    public const string RequiresReindex = nameof(RequiresReindex);

    public const string EmbeddingProviderChanged = nameof(EmbeddingProviderChanged);

    public const string EmbeddingModelChanged = nameof(EmbeddingModelChanged);

    public const string DimensionChanged = nameof(DimensionChanged);
}

/// <summary>vector index 只读状态响应。</summary>
public sealed class VectorIndexStatusResponse
{
    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int IndexedCount { get; init; }

    public int StaleCount { get; init; }

    public int MissingCount { get; init; }

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public bool StoreAvailable { get; init; }

    public bool GeneratorAvailable { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>向量预览候选被路由到的只读目标区块。</summary>
public static class VectorQueryTargetSections
{
    public const string NormalContext = "normal_context";

    public const string WorkingContext = "working_context";

    public const string StableContext = "stable_context";

    public const string HistoricalContext = "historical_context";

    public const string AuditContext = "audit_context";

    public const string DiagnosticsOnly = "diagnostics_only";

    public const string Excluded = "excluded";
}

/// <summary>向量预览候选资格状态。</summary>
public static class VectorCandidateEligibilityStatuses
{
    public const string Eligible = nameof(Eligible);

    public const string Blocked = nameof(Blocked);
}

/// <summary>单个向量预览候选的资格结果。</summary>
public sealed class VectorCandidateEligibilityResult
{
    public string CandidateId { get; init; } = string.Empty;

    public string EligibilityStatus { get; init; } = VectorCandidateEligibilityStatuses.Eligible;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string TargetSection { get; init; } = VectorQueryTargetSections.NormalContext;

    public bool RiskIfNormalSelected { get; init; }

    public bool RiskAfterPolicy { get; init; }
}

/// <summary>vector query preview 请求；只读查询 vector index，不接正式 retrieval/package。</summary>
public sealed class VectorQueryPreviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; } = 10;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public double? MinSimilarity { get; init; }

    public bool IncludeVector { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector query preview 响应；候选仅用于观察，不改变正式输出。</summary>
public sealed class VectorQueryPreviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; }

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public double? MinSimilarity { get; init; }

    public IReadOnlyList<VectorQueryPreviewCandidate> Candidates { get; init; } =
        Array.Empty<VectorQueryPreviewCandidate>();

    public VectorQueryPreviewDiagnostics Diagnostics { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>vector query preview 的单条候选。</summary>
public sealed class VectorQueryPreviewCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string EntryId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public int Rank { get; init; }

    public int RawRank { get; init; }

    public double Similarity { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool IsDuplicate { get; init; }

    public bool IsStale { get; init; }

    public bool IsOrphan { get; init; }

    public bool IsLifecycleRisk { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string EligibilityStatus { get; init; } = VectorCandidateEligibilityStatuses.Eligible;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string TargetSection { get; init; } = VectorQueryTargetSections.NormalContext;

    public bool RiskIfNormalSelected { get; init; }

    public bool RiskAfterPolicy { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector query shadow eval 推荐结论。</summary>
public static class VectorQueryShadowRecommendations
{
    public const string NeedsMoreIndexedData = nameof(NeedsMoreIndexedData);

    public const string NeedsPolicyTuning = nameof(NeedsPolicyTuning);

    public const string NeedsProfileTuning = nameof(NeedsProfileTuning);

    public const string NeedsRankerFusion = nameof(NeedsRankerFusion);

    public const string NeedsFusionTuning = nameof(NeedsFusionTuning);

    public const string NeedsBetterEmbedding = nameof(NeedsBetterEmbedding);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);

    public const string ReadyForRetrievalShadow = nameof(ReadyForRetrievalShadow);

    public const string BlockedByRisk = nameof(BlockedByRisk);

    public const string NeedsRealEmbeddingProvider = nameof(NeedsRealEmbeddingProvider);

    public const string RequiresReranker = nameof(RequiresReranker);
}

public static class DocumentRepresentationProfiles
{
    public const string RawContentV1 = "raw-content-v1";

    public const string TitleContentV1 = "title-content-v1";

    public const string TitleSummaryContentV1 = "title-summary-content-v1";

    public const string AnchorEnrichedV1 = "anchor-enriched-v1";

    public const string MetadataEnrichedV1 = "metadata-enriched-v1";

    public const string CompactRetrievalTextV1 = "compact-retrieval-text-v1";
}

public static class QueryRepresentationProfiles
{
    public const string RawQueryV1 = "raw-query-v1";

    public const string IntentQueryV1 = "intent-query-v1";

    public const string AnchorQueryV1 = "anchor-query-v1";

    public const string ModeIntentQueryV1 = "mode-intent-query-v1";

    public const string ExpandedAnchorQueryV1 = "expanded-anchor-query-v1";
}

public static class VectorQueryExpansionProfileIds
{
    public const string RawQueryV1 = "raw-query-v1";

    public const string ModeIntentQueryV1 = "mode-intent-query-v1";

    public const string AnchorQueryV1 = "anchor-query-v1";

    public const string IntentAnchorQueryV1 = "intent-anchor-query-v1";

    public const string PlanningContextQueryV1 = "planning-context-query-v1";

    public const string ConstraintAwareQueryV1 = "constraint-aware-query-v1";
}

/// <summary>单个 query expansion profile 的 shadow 结果。</summary>
public sealed class VectorQueryExpansionShadowResult
{
    public string ExpansionProfile { get; init; } = VectorQueryExpansionProfileIds.RawQueryV1;

    public int Samples { get; init; }

    public double RecallBeforeExpansion { get; init; }

    public double RecallAfterExpansion { get; init; }

    public double MrrBeforeExpansion { get; init; }

    public double MrrAfterExpansion { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int RecoveredMissCount { get; init; }

    public int NewRiskCount { get; init; }

    public int QueryIntentMissingRecovered { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>单个 representation benchmark 组合结果。</summary>
public sealed class VectorRepresentationBenchmarkResult
{
    public string DocumentRepresentationProfile { get; init; } = DocumentRepresentationProfiles.RawContentV1;

    public string QueryRepresentationProfile { get; init; } = QueryRepresentationProfiles.RawQueryV1;

    public string Provider { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public double Recall { get; init; }

    public double Mrr { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRisk { get; init; }

    public double LifecycleRisk { get; init; }

    public int NoCandidateCount { get; init; }

    public int RecoveredMissCount { get; init; }

    public int NewRiskCount { get; init; }

    public double SimilaritySeparation { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>单个 eval 样本的 vector query shadow 结果。</summary>
public sealed class VectorQueryShadowEvalSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int CandidateCount { get; init; }

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitHitCount { get; init; }

    public int MustHitHitCountBeforePolicy { get; init; }

    public int MustHitHitCountAfterPolicy { get; init; }

    public int MustNotHitCount { get; init; }

    public int MustNotHitHitCount { get; init; }

    public int MustNotHitHitCountBeforePolicy { get; init; }

    public int MustNotHitHitCountAfterPolicy { get; init; }

    public int LifecycleRiskCount { get; init; }

    public int LifecycleRiskBeforePolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int RiskBeforePolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int DeprecatedHitCount { get; init; }

    public int DuplicateHitCount { get; init; }

    public double TopSimilarity { get; init; }

    public bool LowConfidence { get; init; }

    public IReadOnlyList<string> MustHitMatched { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitMatchedBeforePolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitMatchedAfterPolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitMissing { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitMatched { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitMatchedBeforePolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitMatchedAfterPolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItemsBeforePolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItemsAfterPolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorQueryPreviewCandidate> Candidates { get; init; } =
        Array.Empty<VectorQueryPreviewCandidate>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>retrieval dataset / query-corpus alignment audit 的诊断分类；只用于离线报告。</summary>
public static class RetrievalDatasetAlignmentIssueTypes
{
    public const string MustHitMissingFromCorpus = nameof(MustHitMissingFromCorpus);
    public const string MustHitMissingFromProviderScope = nameof(MustHitMissingFromProviderScope);
    public const string MustHitBlockedByEligibility = nameof(MustHitBlockedByEligibility);
    public const string MustHitLifecycleFiltered = nameof(MustHitLifecycleFiltered);
    public const string QueryTokenTooSparse = nameof(QueryTokenTooSparse);
    public const string QueryCorpusTokenMismatch = nameof(QueryCorpusTokenMismatch);
    public const string MissingAnchorMetadata = nameof(MissingAnchorMetadata);
    public const string SourceKindMismatch = nameof(SourceKindMismatch);
    public const string ProviderScopeMismatch = nameof(ProviderScopeMismatch);
    public const string CorpusCoverageRegression = nameof(CorpusCoverageRegression);
    public const string Unknown = nameof(Unknown);
}

/// <summary>vector lifecycle metadata review candidate 状态；V3.15 只生成 PendingReview，不提供决策写入。</summary>
public static class VectorLifecycleMetadataReviewCandidateStatuses
{
    public const string PendingReview = nameof(PendingReview);
    public const string NeedsEvidence = nameof(NeedsEvidence);
    public const string Rejected = nameof(Rejected);
    public const string ApprovedForSidecar = nameof(ApprovedForSidecar);
    public const string Superseded = nameof(Superseded);
}

/// <summary>vector lifecycle metadata review candidate 查询条件。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? Status { get; init; }

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public string? MustHitItemId { get; init; }

    public string? SourceEvalSet { get; init; }

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
}

/// <summary>vector lifecycle metadata review candidate 生成请求。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateGenerationRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? RepairPlanReportPath { get; init; }

    public int Limit { get; init; } = 500;
}

/// <summary>vector lifecycle metadata review candidate 生成结果。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateGenerationResult
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SourceReportPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int GeneratedCount { get; init; }

    public int UpsertedCount { get; init; }

    public int SkippedCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public IReadOnlyList<VectorLifecycleMetadataReviewCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewCandidate>();
}

/// <summary>Hybrid union scoring repair profile 名称。</summary>
public static class HybridUnionScoringRepairProfiles
{
    public const string BaselineHybridFull = "baseline-hybrid-full";
    public const string DensePreservingUnionV1 = "dense-preserving-union-v1";
    public const string DenseWinnerFloorV1 = "dense-winner-floor-v1";
    public const string NegativeDistractorPenaltyV1 = "negative-distractor-penalty-v1";
    public const string PostScoringRiskGatedV1 = "post-scoring-risk-gated-v1";
    public const string AnchorScoreCappedV1 = "anchor-score-capped-v1";
    public const string ContributionAwareRerankV1 = "contribution-aware-rerank-v1";
    public const string CombinedSafeV1 = "combined-safe-v1";
}

/// <summary>提交 lifecycle metadata review 决策的请求。</summary>
public sealed class VectorLifecycleMetadataReviewRequest
{
    public string CandidateId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string ProposedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool Confirmed { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>review 决策执行结果。</summary>
public sealed class VectorLifecycleMetadataReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Succeeded { get; init; }

    public string CandidateId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string CandidateStatus { get; init; } = string.Empty;

    public bool SidecarWritten { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public bool UnsafeApprovalBlocked { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public VectorLifecycleMetadataReviewRecord? Review { get; init; }

    public VectorLifecycleSidecarMetadataEntry? SidecarEntry { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>embedding generator 请求。</summary>
public sealed class EmbeddingGeneratorRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public IReadOnlyList<EmbeddingGeneratorInput> Inputs { get; init; } = Array.Empty<EmbeddingGeneratorInput>();
}

/// <summary>embedding generator 批量结果。</summary>
public sealed class EmbeddingGeneratorResult
{
    public string OperationId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public IReadOnlyList<VectorIndexEntry> Entries { get; init; } = Array.Empty<VectorIndexEntry>();
}
