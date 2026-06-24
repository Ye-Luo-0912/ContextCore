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


/// <summary>safe recall recovery 的单个 profile/topK/minSimilarity/layer 组合结果。</summary>
public sealed class VectorSafeRecallRecoverySweepResult
{
    public string ConfigurationId { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public int BelowTopKMissCount { get; init; }

    public int RecoveredBelowTopKCount { get; init; }

    public double RecoveryRate { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}


/// <summary>离线 document representation profile；只用于 vector benchmark，不接正式检索。</summary>
public static class DocumentRepresentationProfiles
{
    public const string RawContentV1 = "raw-content-v1";

    public const string TitleContentV1 = "title-content-v1";

    public const string TitleSummaryContentV1 = "title-summary-content-v1";

    public const string AnchorEnrichedV1 = "anchor-enriched-v1";

    public const string MetadataEnrichedV1 = "metadata-enriched-v1";

    public const string CompactRetrievalTextV1 = "compact-retrieval-text-v1";
}


/// <summary>vector query expansion 请求；字段必须来自运行时上下文或显式请求元数据。</summary>
public sealed class VectorQueryExpansionRequest
{
    public string QueryText { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string RouterIntent { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public ContextPlanningSnapshot? PlanningSnapshot { get; init; }

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WorkingMemoryAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstraintHints { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> RequestMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector query expansion 结果；只用于 shadow query preview。</summary>
public sealed class VectorQueryExpansionResult
{
    public string ProfileId { get; init; } = VectorQueryExpansionProfileIds.RawQueryV1;

    public string OriginalQuery { get; init; } = string.Empty;

    public string ExpandedQuery { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> UsedSignals { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
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


/// <summary>vector miss-set 的表示诊断类型；只用于 eval/report。</summary>
public static class VectorRepresentationDiagnosisTypes
{
    public const string QueryTooShort = nameof(QueryTooShort);

    public const string QueryIntentMissing = nameof(QueryIntentMissing);

    public const string DocumentTitleMissing = nameof(DocumentTitleMissing);

    public const string DocumentSummaryMissing = nameof(DocumentSummaryMissing);

    public const string AnchorMismatch = nameof(AnchorMismatch);

    public const string RepresentationTooNoisy = nameof(RepresentationTooNoisy);

    public const string MustHitOnlyRecoverableByMetadata = nameof(MustHitOnlyRecoverableByMetadata);

    public const string RequiresQueryExpansion = nameof(RequiresQueryExpansion);

    public const string RequiresDocumentRepresentationRewrite = nameof(RequiresDocumentRepresentationRewrite);

    public const string RequiresBetterEmbeddingModel = nameof(RequiresBetterEmbeddingModel);
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


/// <summary>单个 fusion 策略的离线 shadow 汇总。</summary>
public sealed class VectorRankerFusionStrategyResult
{
    public string Strategy { get; init; } = VectorRankerFusionStrategies.VectorOnly;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public int Samples { get; init; }

    public int VectorCandidateCount { get; init; }

    public int FusionCandidateCount { get; init; }

    public double MustHitRecallVectorOnly { get; init; }

    public double MustHitRecallFusion { get; init; }

    public double MustHitMrrVectorOnly { get; init; }

    public double MustHitMrrFusion { get; init; }

    public double MustNotHitRiskVectorOnly { get; init; }

    public double MustNotHitRiskFusion { get; init; }

    public double LifecycleRiskFusion { get; init; }

    public double RecallGain { get; init; }

    public double RiskDelta { get; init; }

    public IReadOnlyList<VectorRankerFusionShadowSample> TopFixedSamples { get; init; } =
        Array.Empty<VectorRankerFusionShadowSample>();

    public IReadOnlyList<VectorRankerFusionShadowSample> NewlyRiskySamples { get; init; } =
        Array.Empty<VectorRankerFusionShadowSample>();

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


/// <summary>ONNX 残余风险审计分类；只用于离线报告，不进入运行时策略。</summary>
public static class VectorResidualRiskTypes
{
    public const string DeprecatedMetadataGap = nameof(DeprecatedMetadataGap);

    public const string LifecycleMetadataGap = nameof(LifecycleMetadataGap);

    public const string SupersededItemAllowed = nameof(SupersededItemAllowed);

    public const string HistoricalItemAllowed = nameof(HistoricalItemAllowed);

    public const string WrongVersionActiveItem = nameof(WrongVersionActiveItem);

    public const string SemanticOvermatch = nameof(SemanticOvermatch);

    public const string SameTopicWrongIntent = nameof(SameTopicWrongIntent);

    public const string LowMarginAmbiguity = nameof(LowMarginAmbiguity);

    public const string SimilarityThresholdTooLoose = nameof(SimilarityThresholdTooLoose);

    public const string ProfileTooBroad = nameof(ProfileTooBroad);

    public const string RequiresReranker = nameof(RequiresReranker);

    public const string RequiresHumanPolicyDecision = nameof(RequiresHumanPolicyDecision);
}


/// <summary>vector lifecycle metadata backfill 执行结果；不触碰业务源对象。</summary>
public sealed class VectorLifecycleMetadataBackfillResult
{
    public string ResultId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool Applied { get; init; }

    public int UpdatedEntries { get; init; }

    public int SkippedEntries { get; init; }

    public int ManualReviewRequiredCount { get; init; }

    public int CannotResolveCount { get; init; }

    public int FailedCount { get; init; }

    public VectorLifecycleMetadataBackfillPlan Plan { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}


/// <summary>单个 embedding provider 的离线比较结果。</summary>
public sealed class VectorEmbeddingProviderComparisonResult
{
    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int IndexedItems { get; init; }

    public int QueryCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>V3.10 provider 级向量质量比较；只用于 preview/shadow/eval。</summary>
public sealed class VectorProviderComparisonV310Result
{
    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int IndexedEntryCount { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public double A3MrrAfterPolicy { get; init; }

    public int A3RiskAfterPolicy { get; init; }

    public double A3MustNotHitRiskAfterPolicy { get; init; }

    public double A3LifecycleRiskAfterPolicy { get; init; }

    public int A3FormalOutputChanged { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public double ExtendedMrrAfterPolicy { get; init; }

    public int ExtendedRiskAfterPolicy { get; init; }

    public double QueryPreviewTopKOverlap { get; init; }

    public bool PgVectorParityPassed { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class VectorProviderComparisonV310Report
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorProviderComparisonV310Result> Providers { get; init; } =
        Array.Empty<VectorProviderComparisonV310Result>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V3.10.F provider 配置一致性检查；用于防止 qwen3 eval 静默回退到其它 provider。</summary>
public sealed class VectorProviderConfigurationSanityAuditItem
{
    public string ReportKind { get; init; } = string.Empty;

    public string ReportPath { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public bool Passed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();
}

public sealed class VectorProviderConfigurationSanityAuditReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string ProviderComparison { get; init; } = "Inconclusive";

    public string ExpectedProviderType { get; init; } = EmbeddingProviderTypes.OnnxLocal;

    public string ExpectedProviderId { get; init; } = "qwen3-embedding-0.6b-onnx";

    public string ExpectedModelId { get; init; } = "qwen3-embedding-0.6b";

    public string ExpectedPathSegment { get; init; } = "qwen3-embedding-0.6b-onnx";

    public int ExpectedDimension { get; init; } = 1024;

    public bool ExpectedUseForRuntime { get; init; }

    public IReadOnlyList<VectorProviderConfigurationSanityAuditItem> ReportChecks { get; init; } =
        Array.Empty<VectorProviderConfigurationSanityAuditItem>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = "BlockedByProviderConfigurationMismatch";
}


/// <summary>V3.10.F embedding provider promotion 结论；DoNotPromote = 维持当前 preview provider。</summary>
public static class EmbeddingProviderPromotionStatuses
{
    public const string Inconclusive = nameof(Inconclusive);

    public const string DoNotPromote = nameof(DoNotPromote);

    public const string KeepCurrentProvider = nameof(KeepCurrentProvider);

    public const string PromoteCandidate = nameof(PromoteCandidate);
}


/// <summary>hybrid retrieval preview 选项；preview only，UseForRuntime 恒 false，MaxRiskAllowed 恒 0。</summary>
public sealed class HybridVectorLexicalPreviewOptions
{
    public string DenseProviderScope { get; init; } = "current";
    public bool LexicalEnabled { get; init; } = true;
    public bool AnchorEnabled { get; init; } = true;
    public int UnionTopK { get; init; } = 10;
    public int DenseTopK { get; init; } = 10;
    public int LexicalTopK { get; init; } = 10;
    public int AnchorTopK { get; init; } = 10;
    public double MinDenseSimilarity { get; init; } = 0.25;
    public int MaxRiskAllowed { get; init; } = 0;
    public bool UseForRuntime { get; init; } = false;
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


/// <summary>单个 lifecycle metadata review candidate 的 evidence backfill 状态。</summary>
public sealed class VectorLifecycleMetadataEvidenceBackfillCandidateStatus
{
    public string CandidateId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceEvalSet { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public bool EvidenceFound { get; init; }

    public bool SourceRefFound { get; init; }

    public bool ProvenanceFound { get; init; }

    public bool RelationEvidenceFound { get; init; }

    public bool ReviewEvidenceFound { get; init; }

    public bool ReplacementConflictFound { get; init; }

    public bool CanReclassifyAsAutoRepairable { get; init; }

    public bool StillNeedsHumanReview { get; init; }

    public bool ShouldRemainNeedsEvidence { get; init; }

    public bool ForbiddenToRepair { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string ProvenanceRecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;
}


/// <summary>Retrieval Dataset V2 validator issue type。</summary>
public static class RetrievalDatasetV2ValidationIssueTypes
{
    public const string MustHitMissingFromCorpus = nameof(MustHitMissingFromCorpus);
    public const string MustNotMissingFromCorpus = nameof(MustNotMissingFromCorpus);
    public const string MustHitMustNotOverlap = nameof(MustHitMustNotOverlap);
    public const string QueryContainsItemId = nameof(QueryContainsItemId);
    public const string MissingSourceRefs = nameof(MissingSourceRefs);
    public const string MissingEvidenceRefs = nameof(MissingEvidenceRefs);
    public const string MissingProvenance = nameof(MissingProvenance);
    public const string LifecycleTargetSectionMismatch = nameof(LifecycleTargetSectionMismatch);
    public const string RelationEvidenceMissing = nameof(RelationEvidenceMissing);
    public const string SplitIsolationViolation = nameof(SplitIsolationViolation);
}


/// <summary>Retrieval Dataset V2 离线生成配置；默认禁用且不用于 runtime。</summary>
public sealed class RetrievalDatasetV2GenerationOptions
{
    public bool Enabled { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = "default-workspace";

    public string CollectionId { get; init; } = "default-collection";

    public int TargetCorpusItemCount { get; init; } = 28;

    public int TargetSampleCount { get; init; } = 21;

    public string DifficultyProfile { get; init; } = "balanced-v1";

    public int Seed { get; init; } = 1701;

    public string OutputDirectory { get; init; } = "vector/dataset-v2/generated";

    public bool DryRun { get; init; } = true;

    public bool RequireValidation { get; init; } = true;

    public bool UseForRuntime { get; init; }
}


/// <summary>Retrieval Dataset V2 stress / holdout / leakage audit 配置；仅用于离线预览。</summary>
public sealed class RetrievalDatasetV2StressOptions
{
    public int TargetCorpusItemCount { get; init; } = 120;

    public int TargetSampleCount { get; init; } = 120;

    public double HoldoutRatio { get; init; } = 0.2;

    public IReadOnlyDictionary<string, int> DifficultyDistribution { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double DistractorRatio { get; init; } = 0.35;

    public bool AnchorAblationEnabled { get; init; } = true;

    public bool LeakageAuditEnabled { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public string WorkspaceId { get; init; } = "default-workspace";

    public string CollectionId { get; init; } = "default-collection";

    public int Seed { get; init; } = 2701;

    public string OutputDirectory { get; init; } = "vector/dataset-v2/stress";

    public bool DryRun { get; init; } = true;
}


/// <summary>Hybrid union scoring repair preview 配置；仅用于离线评估。</summary>
public sealed class HybridUnionScoringRepairOptions
{
    public bool Enabled { get; init; }

    public bool DensePreservationEnabled { get; init; } = true;

    public bool DenseWinnerFloorEnabled { get; init; } = true;

    public bool NegativeDistractorPenaltyEnabled { get; init; } = true;

    public bool AnchorScoreCapEnabled { get; init; } = true;

    public bool ContributionAwareRerankEnabled { get; init; } = true;

    public int MaxRiskAllowed { get; init; }

    public bool UseForRuntime { get; init; }
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


/// <summary>Guarded formal retrieval preview 配置；只允许离线比较，不启用正式检索。</summary>
public sealed class GuardedFormalRetrievalPreviewOptions
{
    public bool Enabled { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireV4RecheckPassed { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool CompareWithCurrentFormal { get; init; } = true;

    public bool FailClosedOnRisk { get; init; } = true;

    public int MaxRiskAllowed { get; init; }
}


/// <summary>Shadow package comparison 配置；只构建离线 shadow package envelope。</summary>
public sealed class VectorShadowPackageComparisonOptions
{
    public bool Enabled { get; init; }

    public bool RequireGuardedFormalPreviewPassed { get; init; } = true;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool BuildShadowPackage { get; init; } = true;

    public bool CompareWithBaseline { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool FailClosedOnRisk { get; init; } = true;

    public int MaxRiskAllowed { get; init; }
}


/// <summary>Scoped formal preview opt-in 配置；只允许显式 scope 的 preview-only 路径。</summary>
public sealed class ScopedFormalPreviewOptInOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.Off;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string SelectedWorkspaceId { get; init; } = string.Empty;

    public string SelectedCollectionId { get; init; } = string.Empty;

    public string SelectedEvalScope { get; init; } = string.Empty;

    public string NonAllowlistedWorkspaceId { get; init; } = string.Empty;

    public string NonAllowlistedCollectionId { get; init; } = string.Empty;

    public string NonAllowlistedEvalScope { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireV4RecheckPassed { get; init; } = true;

    public bool RequireGuardedFormalPreviewPassed { get; init; } = true;

    public bool RequireShadowPackageComparisonPassed { get; init; } = true;

    public bool WriteFormalPackage { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }
}


/// <summary>Limited formal preview observation 配置；只做多轮 shadow preview 观测。</summary>
public sealed class LimitedFormalPreviewObservationOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.PreviewOnly;

    public int ObservationWindowRuns { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireScopedFormalPreviewOptInPassed { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool FailClosedOnRisk { get; init; } = true;
}


/// <summary>Explicit scoped runtime experiment planning 配置；不启用 runtime。</summary>
public sealed class ExplicitScopedRuntimeExperimentPlanOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ExplicitScopedRuntimeExperimentModes.PlanOnly;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireFoundationFreeze { get; init; } = true;

    public bool RequireServiceFoundationFreeze { get; init; } = true;

    public bool RequireVectorFormalPreviewFreeze { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }
}


/// <summary>Scoped runtime experiment dry-run observation 配置；不启用 runtime。</summary>
public sealed class ScopedRuntimeExperimentDryRunObservationOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentDryRunObservationModes.DryRun;

    public int ObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireV45PlanPassed { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool FailClosedOnRisk { get; init; } = true;

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }
}


/// <summary>Explicit scoped runtime experiment proposal 配置；只生成 proposal / config preview。</summary>
public sealed class ExplicitScopedRuntimeExperimentProposalOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string EvalScopeId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public string Mode { get; init; } = ExplicitScopedRuntimeExperimentProposalModes.ProposalOnly;

    public bool RequireV47DesignFreeze { get; init; } = true;

    public bool RequireFoundationFreeze { get; init; } = true;

    public bool RequireServiceFoundationFreeze { get; init; } = true;

    public bool RequireVectorFormalPreviewFreeze { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool RequireManualApproval { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public bool Approved { get; init; }
}


/// <summary>Scoped runtime experiment approval 配置；不会开启正式 runtime。</summary>
public sealed class ScopedRuntimeExperimentApprovalOptions
{
    public string ProposalId { get; init; } = string.Empty;

    public string ApprovedBy { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public bool RequireExplicitConfirm { get; init; } = true;

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public bool AllowRuntimeSwitch { get; init; }

    public bool AllowFormalRetrieval { get; init; }

    public bool AllowFormalPackageWrite { get; init; }

    public bool AllowPackingPolicyChange { get; init; }

    public string RiskAcknowledgement { get; init; } = string.Empty;

    public string RollbackAcknowledgement { get; init; } = string.Empty;

    public string KillSwitchAcknowledgement { get; init; } = string.Empty;

    public string ScopeAcknowledgement { get; init; } = string.Empty;

    public string ObservationPlanAcknowledgement { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }
}


/// <summary>V4.13 activation preflight options；只做 dry-run route，不写正式配置。</summary>
public sealed class ScopedRuntimeExperimentActivationPreflightOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute;

    public bool RequireV411PlanPassed { get; init; } = true;

    public bool RequireV412ApprovalPassed { get; init; } = true;

    public bool RequireFoundationFreeze { get; init; } = true;

    public bool RequireServiceFoundationFreeze { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireTraceSink { get; init; } = true;

    public bool TraceSinkAvailable { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool MutateRuntime { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }
}


/// <summary>V4.14 guarded scoped runtime experiment options；只允许 allowlisted scope 的 shadow route。</summary>
public sealed class GuardedScopedRuntimeExperimentOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MaxRequestCount { get; init; } = 120;

    public int MaxDurationMinutes { get; init; } = 30;

    public int MaxErrorCount { get; init; }

    public bool RequireV413PreflightPassed { get; init; } = true;

    public bool RequireScopedRuntimeExperimentApproval { get; init; } = true;

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireTraceSink { get; init; } = true;

    public bool TraceSinkAvailable { get; init; } = true;

    public bool WriteFormalPackage { get; init; }

    public bool MutateFormalOutput { get; init; }

    public bool MutatePackingPolicy { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public bool RollbackVerified { get; init; } = true;

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ErrorCount { get; init; }
}


/// <summary>V4.15 scoped runtime experiment observation window options；扩展 V4.14 shadow route 的观测窗口。</summary>
public sealed class ScopedRuntimeExperimentObservationWindowOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ObservationWindowId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int MinRequestCount { get; init; } = 360;

    public int ObservationRunCount { get; init; } = 3;

    public int MaxDurationMinutes { get; init; } = 30;

    public int MaxErrorCount { get; init; }

    public int MaxLatencyP95Ms { get; init; } = 1_000;

    public bool RequireV414GatePassed { get; init; } = true;

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireTraceSink { get; init; } = true;

    public bool TraceSinkAvailable { get; init; } = true;

    public bool WriteFormalPackage { get; init; }

    public bool MutateFormalOutput { get; init; }

    public bool MutatePackingPolicy { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool KillSwitchAvailable { get; init; } = true;

    public bool KillSwitchSmokePassed { get; init; } = true;

    public bool RollbackVerified { get; init; } = true;

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ErrorCount { get; init; }

    public int? LatencyP50 { get; init; }

    public int? LatencyP95 { get; init; }

    public double TraceCompleteness { get; init; } = 100;
}


/// <summary>shadow formal retrieval adapter per-sample trace。</summary>
public sealed class ShadowFormalRetrievalAdapterSampleResult
{
    public string SampleId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public int BaselineCandidateCount { get; init; }

    public int ShadowVectorCandidateCount { get; init; }

    public int ShadowGraphCandidateCount { get; init; }

    public int MergedShadowCandidateCount { get; init; }

    public int FilteredCandidateCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public IReadOnlyList<string> BaselineCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowVectorCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowGraphCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MergedShadowCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FilteredCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DropReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExplainNotes { get; init; } = Array.Empty<string>();
}


/// <summary>formal adapter package shadow comparison per-sample trace。</summary>
public sealed class FormalAdapterPackageShadowComparisonSampleResult
{
    public string SampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public int BaselinePackageItemCount { get; init; }

    public int ShadowPackageItemCount { get; init; }

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int AddedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int OrderChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public int BaselineTokenCount { get; init; }

    public int ShadowTokenCount { get; init; }

    public int TokenDelta { get; init; }

    public int TokenDeltaAbsolute { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public IReadOnlyList<string> BaselinePackageItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowPackageItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DroppedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SectionChangedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> OrderChangedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> PriorityChangedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExplainNotes { get; init; } = Array.Empty<string>();
}


/// <summary>retrieval quality audit per-sample trace。</summary>
public sealed class GraphVectorRetrievalQualityAuditSampleResult
{
    public string SampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public int MustHitCount { get; init; }

    public int MustHitRecalledCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int VectorCandidateCount { get; init; }

    public int GraphCandidateCount { get; init; }

    public int MergedCandidateCount { get; init; }

    public int OverlapCount { get; init; }

    public int VectorOnlyCount { get; init; }

    public int GraphOnlyCount { get; init; }

    public int GraphNoiseCount { get; init; }

    public int VectorNoiseCount { get; init; }

    public int RankingRegressionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public int MetadataEvidenceGapCount { get; init; }

    public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingMustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RankingRegressionItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> GraphNoiseItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> VectorNoiseItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SectionMismatchItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MetadataEvidenceGapItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MergedCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExplainNotes { get; init; } = Array.Empty<string>();
}


/// <summary>retrieval quality repair preview profile IDs。</summary>
public static class RetrievalQualityRepairProfiles
{
    public const string Baseline = "baseline";
    public const string CandidatePoolExpansion = "candidate-pool-expansion";
    public const string TopKAdjustment = "topk-adjustment";
    public const string SectionAwareBoost = "section-aware-boost";
    public const string MustHitEvidenceBoost = "must-hit-evidence-boost";
    public const string GraphRelationAnchorBoost = "graph-relation-anchor-boost";
    public const string LexicalFallbackBoost = "lexical-fallback-boost";
    public const string Combined = "combined-repair";
}


/// <summary>profile contract statuses。</summary>
public static class RuntimeObservableFeatureContractStatuses
{
    public const string RuntimeSafe = nameof(RuntimeSafe);
    public const string RequiresRuntimeDerivation = nameof(RequiresRuntimeDerivation);
    public const string EvalOnly = nameof(EvalOnly);
    public const string ForbiddenForScoring = nameof(ForbiddenForScoring);
    public const string None = nameof(None);
}


/// <summary>runtime-observable feature contract recommendations。</summary>
public static class RuntimeObservableFeatureContractRecommendations
{
    public const string ReadyForRuntimeObservableFeatureFreeze = nameof(ReadyForRuntimeObservableFeatureFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingRepairGate = nameof(BlockedByMissingRepairGate);
    public const string BlockedByRepairGateNotPassed = nameof(BlockedByRepairGateNotPassed);
    public const string BlockedByEvalOnlyFeatureInScoring = nameof(BlockedByEvalOnlyFeatureInScoring);
    public const string BlockedByForbiddenFeatureInScoring = nameof(BlockedByForbiddenFeatureInScoring);
    public const string BlockedByMissingRuntimeDerivationPath = nameof(BlockedByMissingRuntimeDerivationPath);
    public const string BlockedByBestProfileContractFailure = nameof(BlockedByBestProfileContractFailure);
    public const string BlockedByFixtureSpecialCasing = nameof(BlockedByFixtureSpecialCasing);
    public const string BlockedBySourceScanMissing = nameof(BlockedBySourceScanMissing);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}


/// <summary>per-profile contract record。</summary>
public sealed class RuntimeObservableFeatureContractProfile
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileLabel { get; init; } = string.Empty;

    public string ContractStatus { get; init; } = RuntimeObservableFeatureContractStatuses.None;

    public IReadOnlyList<RuntimeObservableFeatureUsage> Features { get; init; }
        = Array.Empty<RuntimeObservableFeatureUsage>();

    public bool UsesForbiddenForScoring { get; init; }

    public bool UsesEvalOnlyForScoring { get; init; }

    public bool RequiresRuntimeDerivation { get; init; }

    public IReadOnlyList<string> RequiredRuntimeDerivationPaths { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}


/// <summary>runner-side source scan summary; populated by EvalCommand at run time.</summary>
public sealed class RuntimeObservableFeatureContractSourceScan
{
    public bool ScanPerformed { get; init; }

    public int ScannedFileCount { get; init; }

    public int FixtureTokenHitCount { get; init; }

    public IReadOnlyList<string> ScannedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FlaggedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FlaggedTokens { get; init; } = Array.Empty<string>();
}


/// <summary>runtime-derived retrieval feature envelope。</summary>
public sealed class RuntimeRetrievalFeatureEnvelope
{
    public string SampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredRelations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotConstraints { get; init; } = Array.Empty<string>();

    public string TargetSectionDerivationSource { get; init; } = string.Empty;

    public string EvidenceAnchorDerivationSource { get; init; } = string.Empty;

    public string SourceAnchorDerivationSource { get; init; } = string.Empty;

    public string RequiredRelationDerivationSource { get; init; } = string.Empty;

    public string MustNotConstraintDerivationSource { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>per-sample derivation result。</summary>
public sealed class RuntimeRetrievalFeatureDerivationSampleResult
{
    public RuntimeRetrievalFeatureEnvelope Envelope { get; init; }
        = new RuntimeRetrievalFeatureEnvelope();

    public bool TargetSectionMatch { get; init; }

    public int ExpectedRequiredRelationCount { get; init; }

    public int DerivedRequiredRelationCount { get; init; }

    public int RequiredRelationOverlap { get; init; }

    public int ExpectedEvidenceAnchorCount { get; init; }

    public int DerivedEvidenceAnchorCount { get; init; }

    public int EvidenceAnchorOverlap { get; init; }

    public int ExpectedSourceAnchorCount { get; init; }

    public int DerivedSourceAnchorCount { get; init; }

    public int SourceAnchorOverlap { get; init; }

    public int ExpectedMustNotCount { get; init; }

    public int DerivedMustNotCount { get; init; }

    public int MissingDerivationCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitRecalledCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }
}


/// <summary>per-sample repair derivation result。</summary>
public sealed class RuntimeRetrievalFeatureDerivationRepairSampleResult
{
    public RuntimeRetrievalFeatureEnvelope Envelope { get; init; }
        = new RuntimeRetrievalFeatureEnvelope();

    public string Split { get; init; } = "train";

    public bool TargetSectionMatch { get; init; }

    public int ExpectedRequiredRelationCount { get; init; }

    public int DerivedRequiredRelationCount { get; init; }

    public int RequiredRelationOverlap { get; init; }

    public int CanonicalRequiredRelationOverlap { get; init; }

    public int ExpectedEvidenceAnchorCount { get; init; }

    public int DerivedEvidenceAnchorCount { get; init; }

    public int CanonicalEvidenceAnchorOverlap { get; init; }

    public int ExpectedSourceAnchorCount { get; init; }

    public int DerivedSourceAnchorCount { get; init; }

    public int CanonicalSourceAnchorOverlap { get; init; }

    public int ExpectedMustNotCount { get; init; }

    public int DerivedMustNotCount { get; init; }

    public int MissingDerivationCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitRecalledCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }
}


/// <summary>No-op harness 配置；只验证审批和边界，不切 runtime。</summary>
public sealed class ScopedRuntimeExperimentNoOpHarnessOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentNoOpHarnessModes.NoOp;

    public bool RequireApprovedProposal { get; init; } = true;

    public string RequireApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool MutateRuntime { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }
}


/// <summary>V4.11 guarded scoped runtime experiment plan options；只生成 activation contract。</summary>
public sealed class GuardedScopedRuntimeExperimentPlanOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentPlanModes.PlanOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MaxRequestCount { get; init; }

    public int MaxDurationMinutes { get; init; }

    public int MaxErrorCount { get; init; }

    public int MaxRiskCount { get; init; }

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireObservationPlan { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }
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


/// <summary>lifecycle metadata 人工 review batch 状态。</summary>
public static class VectorLifecycleMetadataReviewBatchStatuses
{
    public const string Draft = nameof(Draft);

    public const string Exported = nameof(Exported);

    public const string Imported = nameof(Imported);

    public const string Validated = nameof(Validated);

    public const string AppliedPreview = nameof(AppliedPreview);

    public const string Closed = nameof(Closed);
}


/// <summary>review batch import 结果。</summary>
public sealed class VectorLifecycleMetadataReviewBatchImportResult
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;

    public int RowCount { get; init; }

    public int DecisionCount { get; init; }

    public bool Imported { get; init; }

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

/// <summary>
/// formal adapter 未来唯一允许读取的 runtime 输入 envelope。
/// 不包含 Dataset V2 sample、gold labels、shadow report 或 eval-only metadata。
/// </summary>
public sealed class FormalAdapterRuntimeInputEnvelope
{
    public string ContractVersion { get; init; } = "formal-adapter-input-contract-v1";

    public string RequestId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public FormalAdapterRuntimePackageContext PackageContext { get; init; } = new();

    public IReadOnlyList<FormalAdapterRuntimeCandidateInput> BaselineCandidates { get; init; } =
        Array.Empty<FormalAdapterRuntimeCandidateInput>();

    public IReadOnlyList<FormalAdapterRuntimeCandidateInput> CandidatePool { get; init; } =
        Array.Empty<FormalAdapterRuntimeCandidateInput>();
}
