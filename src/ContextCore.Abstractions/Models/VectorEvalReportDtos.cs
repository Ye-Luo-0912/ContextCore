namespace ContextCore.Abstractions.Models;


/// <summary>独立 vector index 的单条索引记录；V1 只用于基础设施与诊断，不接正式 retrieval。</summary>
public sealed class VectorIndexEntry
{
    public string EntryId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ContentHash { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector index 诊断汇总。</summary>
public sealed class VectorIndexDiagnosticsReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int IndexedCount { get; init; }

    public int MissingCount { get; init; }

    public int StaleCount { get; init; }

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int UnsupportedModelCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public IReadOnlyDictionary<string, int> CountsByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();
}


/// <summary>vector reindex preview 请求；只计算预期动作，不写入 index。</summary>
public sealed class VectorReindexPreviewRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? Layer { get; init; }

    public int Take { get; init; } = 200;

    public bool IncludeMemoryItems { get; init; } = true;

    public bool IncludeContextItems { get; init; } = true;
}


/// <summary>vector reindex preview 响应。</summary>
public sealed class VectorReindexPreviewResponse
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int SourceItemCount { get; init; }

    public int WouldCreateCount { get; init; }

    public int WouldUpdateCount { get; init; }

    public int AlreadyCurrentCount { get; init; }

    public int WouldDeleteOrphanCount { get; init; }

    public IReadOnlyList<VectorReindexPreviewItem> Items { get; init; } =
        Array.Empty<VectorReindexPreviewItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>vector reindex 的外部源项；用于 eval corpus 等只读数据源，不要求先写入 context/memory store。</summary>
public sealed class VectorReindexSourceItem
{
    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector reindex 计划、提交与执行的统一请求。</summary>
public sealed class VectorReindexRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();

    public bool DryRun { get; init; } = true;

    public bool Apply { get; init; }

    public bool ConfirmApply { get; init; }

    public bool Force { get; init; }

    public int BatchSize { get; init; } = 50;

    public int MaxItems { get; init; } = 200;

    public bool IncludeContextItems { get; init; } = true;

    public bool IncludeMemoryItems { get; init; } = true;

    public IReadOnlyList<VectorReindexSourceItem> SourceItems { get; init; } =
        Array.Empty<VectorReindexSourceItem>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector reindex 执行结果。</summary>
public sealed class VectorReindexResult
{
    public string ReportId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string? JobId { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public VectorReindexPlan Plan { get; init; } = new();

    public VectorReindexSummary Summary { get; init; } = new();

    public IReadOnlyList<VectorReindexPlanItem> ProcessedItems { get; init; } = Array.Empty<VectorReindexPlanItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}


/// <summary>vector reindex submit 响应，返回已入队 job 和预计算计划。</summary>
public sealed class VectorReindexSubmitResponse
{
    public ContextJob Job { get; init; } = new();

    public VectorReindexPlan Plan { get; init; } = new();
}


/// <summary>vector reindex report 查询响应。</summary>
public sealed class VectorReindexReportQueryResponse
{
    public IReadOnlyList<VectorReindexResult> Reports { get; init; } = Array.Empty<VectorReindexResult>();

    public int Count { get; init; }
}


/// <summary>vector index 覆盖率报告推荐结论。</summary>
public static class VectorIndexCoverageRecommendations
{
    public const string NeedsInitialIndexing = nameof(NeedsInitialIndexing);

    public const string NeedsReindex = nameof(NeedsReindex);

    public const string ReadyForVectorShadowEval = nameof(ReadyForVectorShadowEval);

    public const string BlockedByDiagnostics = nameof(BlockedByDiagnostics);
}


/// <summary>vector index coverage report；只读评估索引覆盖，不写入 index。</summary>
public sealed class VectorIndexCoverageReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TotalSourceItems { get; init; }

    public int IndexedItems { get; init; }

    public double CoverageRate { get; init; }

    public IReadOnlyDictionary<string, VectorIndexCoverageBucket> CoverageByLayer { get; init; } =
        new Dictionary<string, VectorIndexCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VectorIndexCoverageBucket> CoverageByItemKind { get; init; } =
        new Dictionary<string, VectorIndexCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> MissingByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> StaleByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public string Recommendation { get; init; } = VectorIndexCoverageRecommendations.NeedsInitialIndexing;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}


/// <summary>向量查询配置标识。</summary>
public static class VectorQueryProfileIds
{
    public const string NormalV1 = "normal-v1";

    public const string CurrentTaskV1 = "current-task-v1";

    public const string AuditV1 = "audit-v1";

    public const string DiagnosticsV1 = "diagnostics-v1";
}


/// <summary>向量查询预览安全配置；只用于预览和影子评估，不接正式检索。</summary>
public sealed class VectorQueryProfile
{
    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public double MinSimilarity { get; init; }

    public IReadOnlyList<string> AllowedLayers { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedItemKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedSourceTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DiagnosticsOnlyItemKinds { get; init; } = Array.Empty<string>();

    public bool RequireKnownLifecycle { get; init; }

    public bool RequireCompleteLifecycleMetadata { get; init; }

    public bool AllowDeprecatedCandidates { get; init; }

    public bool AllowHistoricalCandidates { get; init; }

    public bool AllowRejectedCandidates { get; init; }

    public bool AllowCandidateLifecycle { get; init; }

    public string DefaultTargetSection { get; init; } = VectorQueryTargetSections.NormalContext;

    public string HistoricalTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string DiagnosticsTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;
}


/// <summary>vector query preview 的诊断摘要。</summary>
public sealed class VectorQueryPreviewDiagnostics
{
    public bool StoreAvailable { get; init; }

    public bool GeneratorAvailable { get; init; }

    public bool IndexEmpty { get; init; }

    public int IndexedCount { get; init; }

    public int DuplicateCount { get; init; }

    public int StaleCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int UnsupportedModelCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();
}


/// <summary>vector recall loss 的离线 miss 原因；只用于 eval/report，不进入资格策略。</summary>
public static class VectorRecallLossMissReasons
{
    public const string NotIndexed = nameof(NotIndexed);

    public const string BelowTopK = nameof(BelowTopK);

    public const string BelowSimilarityThreshold = nameof(BelowSimilarityThreshold);

    public const string BlockedByEligibilityPolicy = nameof(BlockedByEligibilityPolicy);

    public const string LayerFilterExcluded = nameof(LayerFilterExcluded);

    public const string ItemKindFilterExcluded = nameof(ItemKindFilterExcluded);

    public const string NoCandidateGenerated = nameof(NoCandidateGenerated);

    public const string LowSimilaritySeparation = nameof(LowSimilaritySeparation);

    public const string RequiresRankerFusion = nameof(RequiresRankerFusion);
}


/// <summary>vector profile readiness 分组报告；用于判断是否只停留在 preview/shadow。</summary>
public sealed class VectorIntentReadinessReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string GroupBy { get; init; } = string.Empty;

    public IReadOnlyList<VectorIntentReadinessBucket> Buckets { get; init; } =
        Array.Empty<VectorIntentReadinessBucket>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}


/// <summary>vector recall loss 审计报告；只解释 eval shadow 召回损失，不改变正式输出。</summary>
public sealed class VectorRecallLossAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public string ItemKindFilter { get; init; } = string.Empty;

    public int MissedMustHitCount { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public IReadOnlyList<VectorRecallLossMiss> MissedMustHits { get; init; } =
        Array.Empty<VectorRecallLossMiss>();

    public IReadOnlyDictionary<string, int> MissReasonCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public VectorIntentReadinessReport ModeReadiness { get; init; } = new();

    public VectorIntentReadinessReport IntentReadiness { get; init; } = new();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>safe recall recovery 汇总报告；只用于离线调参，不接正式检索。</summary>
public sealed class VectorSafeRecallRecoveryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public double BaselineRecallAfterPolicy { get; init; }

    public double BaselineMrrAfterPolicy { get; init; }

    public int BaselineRiskAfterPolicy { get; init; }

    public int BelowTopKMissCount { get; init; }

    public int BlockedMustHitCount { get; init; }

    public IReadOnlyList<VectorSafeRecallRecoverySweepResult> SweepResults { get; init; } =
        Array.Empty<VectorSafeRecallRecoverySweepResult>();

    public VectorSafeRecallRecoverySweepResult? BestSafeSweep { get; init; }

    public IReadOnlyList<VectorBlockedMustHitAuditRecord> BlockedMustHitAudit { get; init; } =
        Array.Empty<VectorBlockedMustHitAuditRecord>();

    public IReadOnlyDictionary<string, int> BlockedClassificationCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>离线 query representation profile；只用于 vector benchmark，不接正式检索。</summary>
public static class QueryRepresentationProfiles
{
    public const string RawQueryV1 = "raw-query-v1";

    public const string IntentQueryV1 = "intent-query-v1";

    public const string AnchorQueryV1 = "anchor-query-v1";

    public const string ModeIntentQueryV1 = "mode-intent-query-v1";

    public const string ExpandedAnchorQueryV1 = "expanded-anchor-query-v1";
}


/// <summary>离线 vector query expansion profile；只组合运行时信号，不接正式检索。</summary>
public static class VectorQueryExpansionProfileIds
{
    public const string RawQueryV1 = "raw-query-v1";

    public const string ModeIntentQueryV1 = "mode-intent-query-v1";

    public const string AnchorQueryV1 = "anchor-query-v1";

    public const string IntentAnchorQueryV1 = "intent-anchor-query-v1";

    public const string PlanningContextQueryV1 = "planning-context-query-v1";

    public const string ConstraintAwareQueryV1 = "constraint-aware-query-v1";
}


/// <summary>vector query expansion profile；只用于离线 shadow，不影响正式 retrieval/package。</summary>
public sealed class VectorQueryExpansionProfile
{
    public string ProfileId { get; init; } = VectorQueryExpansionProfileIds.RawQueryV1;

    public bool IncludeRawQuery { get; init; } = true;

    public bool IncludeMode { get; init; }

    public bool IncludeIntent { get; init; }

    public bool IncludeQueryAnchors { get; init; }

    public bool IncludeWorkingMemoryAnchors { get; init; }

    public bool IncludePlanningContext { get; init; }

    public bool IncludeConstraintHints { get; init; }

    public bool IncludeTaskKind { get; init; }

    public bool IncludeRequestMetadata { get; init; }

    public int MaxSignalCount { get; init; } = 24;
}


/// <summary>query expansion shadow 汇总报告；只用于离线 eval，不改变正式输出。</summary>
public sealed class VectorQueryExpansionShadowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string VectorProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public IReadOnlyList<VectorQueryExpansionShadowResult> Results { get; init; } =
        Array.Empty<VectorQueryExpansionShadowResult>();

    public VectorQueryExpansionShadowResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>A3 / Extended miss-set representation 审计报告。</summary>
public sealed class VectorMissSetRepresentationAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string DocumentRepresentationProfile { get; init; } = DocumentRepresentationProfiles.RawContentV1;

    public string QueryRepresentationProfile { get; init; } = QueryRepresentationProfiles.RawQueryV1;

    public int MissedMustHitCount { get; init; }

    public IReadOnlyList<VectorMissSetRepresentationAuditRecord> MissedMustHits { get; init; } =
        Array.Empty<VectorMissSetRepresentationAuditRecord>();

    public IReadOnlyDictionary<string, int> DiagnosisCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RecommendedRepairCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>representation benchmark 汇总报告；只使用临时 index，不写正式 vector index。</summary>
public sealed class VectorRepresentationBenchmarkReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public IReadOnlyList<VectorRepresentationBenchmarkResult> Results { get; init; } =
        Array.Empty<VectorRepresentationBenchmarkResult>();

    public VectorRepresentationBenchmarkResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>单个 fusion 候选的离线打分明细；只用于 report，不进入正式检索。</summary>
public sealed class VectorRankerFusionCandidate
{
    public string ItemId { get; init; } = string.Empty;

    public int VectorRank { get; init; }

    public int FusionRank { get; init; }

    public double Similarity { get; init; }

    public double RankerScore { get; init; }

    public double FusionScore { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public bool RiskAfterPolicy { get; init; }

    public IReadOnlyList<string> ScoreReasons { get; init; } = Array.Empty<string>();
}


/// <summary>单个 eval 样本的 vector + ranker fusion shadow 差异。</summary>
public sealed class VectorRankerFusionShadowSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string Strategy { get; init; } = VectorRankerFusionStrategies.VectorOnly;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public int VectorCandidateCount { get; init; }

    public int FusionCandidateCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitVectorOnlyHitCount { get; init; }

    public int MustHitFusionHitCount { get; init; }

    public double MustHitMrrVectorOnly { get; init; }

    public double MustHitMrrFusion { get; init; }

    public int MustNotHitVectorOnlyCount { get; init; }

    public int MustNotHitFusionCount { get; init; }

    public int LifecycleRiskFusionCount { get; init; }

    public double RecallGain { get; init; }

    public int RiskDelta { get; init; }

    public bool IsFixed { get; init; }

    public bool IsNewlyRisky { get; init; }

    public IReadOnlyList<string> MustHitGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitLost { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NewlyRiskyItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<VectorRankerFusionCandidate> TopCandidates { get; init; } =
        Array.Empty<VectorRankerFusionCandidate>();
}


/// <summary>vector + ranker fusion 离线 shadow 报告；不改变正式 retrieval/package 输出。</summary>
public sealed class VectorRankerFusionShadowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public IReadOnlyList<VectorRankerFusionStrategyResult> Results { get; init; } =
        Array.Empty<VectorRankerFusionStrategyResult>();

    public VectorRankerFusionStrategyResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>vector query shadow eval 汇总报告；不改变正式 retrieval/package 输出。</summary>
public sealed class VectorQueryShadowEvalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int Samples { get; init; }

    public double IndexedCoverage { get; init; }

    public int QueryCount { get; init; }

    public int CandidateCount { get; init; }

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public int RiskBeforePolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustHitRecallBeforePolicy { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustNotHitRiskBeforePolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskBeforePolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public double MustHitRecallAtK { get; init; }

    public double MustNotHitRiskAtK { get; init; }

    public double LifecycleRiskAtK { get; init; }

    public int DeprecatedHitCount { get; init; }

    public int DuplicateHitCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public int NoCandidateCount { get; init; }

    public int LowConfidenceCount { get; init; }

    public IReadOnlyDictionary<string, int> TopNoiseClusters { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.NeedsMoreIndexedData;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<VectorQueryShadowEvalSample> SampleResults { get; init; } =
        Array.Empty<VectorQueryShadowEvalSample>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>向量查询 profile sweep 的单个配置结果。</summary>
public sealed class VectorQueryProfileSweepResult
{
    public string ConfigurationId { get; init; } = string.Empty;

    public int Samples { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public double MustHitRecallBeforePolicy { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public double MustNotHitRiskBeforePolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskBeforePolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public int LowConfidenceCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public IReadOnlyDictionary<string, int> TopNoiseClusters { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RiskAfterPolicyByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double RecallLossAfterRepair { get; init; }

    public double SimilarityMarginForRiskCandidates { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}


/// <summary>向量查询 profile sweep 汇总报告。</summary>
public sealed class VectorQueryProfileSweepReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int Samples { get; init; }

    public IReadOnlyList<VectorQueryProfileSweepResult> Results { get; init; } =
        Array.Empty<VectorQueryProfileSweepResult>();

    public VectorQueryProfileSweepResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>单个 residual vector risk 明细；由 eval shadow 产生，不进入正式检索。</summary>
public sealed class VectorResidualRiskDetail
{
    public string SampleId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string CandidateItemId { get; init; } = string.Empty;

    public double Similarity { get; init; }

    public double SimilarityMargin { get; init; }

    public int RawRank { get; init; }

    public int EligibleRank { get; init; }

    public string TargetSection { get; init; } = string.Empty;

    public string RiskType { get; init; } = string.Empty;

    public string RiskReason { get; init; } = string.Empty;

    public string ItemLifecycle { get; init; } = string.Empty;

    public string ItemLayer { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string SourceRef { get; init; } = string.Empty;

    public string ContentHash { get; init; } = string.Empty;

    public string WhyPolicyAllowed { get; init; } = string.Empty;

    public string ExpectedAction { get; init; } = string.Empty;
}


/// <summary>vector residual risk audit 报告；用于解释 shadow 风险，不改变正式输出。</summary>
public sealed class VectorResidualRiskAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int ResidualRiskCount { get; init; }

    public int BeforeRepairRiskCount { get; init; }

    public int AfterRepairRiskCount { get; init; }

    public int BlockedByLifecycleMetadataGate { get; init; }

    public IReadOnlyDictionary<string, int> RemainingRiskTypes { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorResidualRiskDetail> Risks { get; init; } =
        Array.Empty<VectorResidualRiskDetail>();

    public IReadOnlyDictionary<string, int> RiskAfterPolicyByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double RecallLossAfterRepair { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public double SimilarityMarginForRiskCandidates { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>vector source lifecycle metadata coverage 推荐结论。</summary>
public static class VectorLifecycleMetadataCoverageRecommendations
{
    public const string ReadyForVectorShadowEval = nameof(ReadyForVectorShadowEval);

    public const string NeedsLifecycleMetadataBackfill = nameof(NeedsLifecycleMetadataBackfill);

    public const string BlockedByUnknownLifecycle = nameof(BlockedByUnknownLifecycle);

    public const string BlockedByDiagnostics = nameof(BlockedByDiagnostics);
}


/// <summary>vector source lifecycle metadata coverage 报告；只读诊断，不写 index。</summary>
public sealed class VectorLifecycleMetadataCoverageReport
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int TotalVectorSourceItems { get; init; }

    public int KnownLifecycleCount { get; init; }

    public int UnknownLifecycleCount { get; init; }

    public int MissingReviewStatusCount { get; init; }

    public int MissingReplacementInfoCount { get; init; }

    public int LegacySourceWithoutLifecycleCount { get; init; }

    public int DeprecatedSourceWithoutLifecycleCount { get; init; }

    public double LifecycleCoverageRate { get; init; }

    public IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> CoverageByLayer { get; init; } =
        new Dictionary<string, VectorLifecycleMetadataCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> CoverageByItemKind { get; init; } =
        new Dictionary<string, VectorLifecycleMetadataCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> CoverageBySourceType { get; init; } =
        new Dictionary<string, VectorLifecycleMetadataCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataCoverageRecommendations.NeedsLifecycleMetadataBackfill;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}


/// <summary>确定性 hash embedding 的离线质量基线报告。</summary>
public sealed class VectorEmbeddingQualityBaselineReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public int PairCount { get; init; }

    public int PositiveHitCount { get; init; }

    public int NegativeHitCount { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public double MustHitRecallAt20 { get; init; }

    public double MustNotHitRiskAt20 { get; init; }

    public string EmbeddingProvider { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>Embedding provider 离线质量比较报告；不接正式 retrieval。</summary>
public sealed class VectorEmbeddingProviderComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

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

    public double MustHitRecallAtK { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorEmbeddingProviderComparisonResult> ProviderResults { get; init; } =
        Array.Empty<VectorEmbeddingProviderComparisonResult>();

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>hybrid retrieval preview 变体标识；dense / dense+lexical / dense+anchor / 全量。</summary>
public static class HybridRetrievalVariant
{
    public const string Dense = nameof(Dense);
    public const string DenseLexical = nameof(DenseLexical);
    public const string DenseAnchor = nameof(DenseAnchor);
    public const string DenseLexicalAnchor = nameof(DenseLexicalAnchor);
}


/// <summary>hybrid retrieval readiness gate 结论常量。</summary>
public static class HybridRetrievalReadinessRecommendations
{
    public const string ReadyForVectorV4Recheck = nameof(ReadyForVectorV4Recheck);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByA3Recall = nameof(BlockedByA3Recall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPolicyViolation = nameof(BlockedByPolicyViolation);
}


/// <summary>hybrid retrieval 单变体单数据集报告。</summary>
public sealed class HybridRetrievalVariantReport
{
    public string DatasetName { get; init; } = string.Empty;
    public string ProfileName { get; init; } = "normal-v1";
    public string Variant { get; init; } = HybridRetrievalVariant.Dense;
    public int SampleCount { get; init; }
    public int DenseCandidateCount { get; init; }
    public int LexicalCandidateCount { get; init; }
    public int AnchorCandidateCount { get; init; }
    public int UnionCandidateCount { get; init; }
    public double RecallAfterPolicy { get; init; }
    public double MrrAfterPolicy { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public double RecallDeltaVsDense { get; init; }
    public double RiskDeltaVsDense { get; init; }
    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
}


/// <summary>hybrid retrieval 候选来源贡献统计。</summary>
public sealed class HybridSourceContribution
{
    public int DenseOnlyCount { get; init; }
    public int LexicalOnlyCount { get; init; }
    public int AnchorOnlyCount { get; init; }
    public int DenseAndLexicalCount { get; init; }
    public int DenseAndAnchorCount { get; init; }
    public int LexicalAndAnchorCount { get; init; }
    public int AllThreeCount { get; init; }
}


/// <summary>hybrid retrieval preview 总报告；A3+Extended 各 4 变体 = 8 条。</summary>
public sealed class HybridRetrievalPreviewReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public HybridVectorLexicalPreviewOptions Options { get; init; } = new();
    public IReadOnlyList<HybridRetrievalVariantReport> Variants { get; init; } = Array.Empty<HybridRetrievalVariantReport>();
    public HybridSourceContribution ContributionBreakdown { get; init; } = new();
    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>hybrid recall regression audit 结论常量。</summary>
public static class HybridRecallRegressionAuditRecommendations
{
    public const string ReadyForHybridFreeze = nameof(ReadyForHybridFreeze);
    public const string BlockedByDenseBaselineRegression = nameof(BlockedByDenseBaselineRegression);
    public const string BlockedByProviderScopeMismatch = nameof(BlockedByProviderScopeMismatch);
    public const string BlockedByEligibilityMismatch = nameof(BlockedByEligibilityMismatch);
    public const string BlockedByDedupBug = nameof(BlockedByDedupBug);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>hybrid recall regression audit 单 profile 对齐报告。</summary>
public sealed class HybridRecallRegressionAuditProfileResult
{
    public string ProfileName { get; init; } = string.Empty;
    public string DatasetName { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int CandidateCount { get; init; }
    public int EligibleCandidateCount { get; init; }
    public int BlockedCandidateCount { get; init; }
    public double RecallAfterPolicy { get; init; }
    public double MrrAfterPolicy { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int DenseCandidateDroppedCount { get; init; }
    public int EligibilityMismatchCount { get; init; }
}


/// <summary>hybrid recall regression audit 总报告；sanity audit only，不接 formal retrieval。</summary>
public sealed class HybridRetrievalRecallRegressionAuditReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public double LegacyDenseRecallA3 { get; init; }

    public double HybridDenseOnlyRecallA3 { get; init; }

    public double HybridBestRecallA3 { get; init; }

    public double LegacyDenseRecallExtended { get; init; }

    public double HybridDenseOnlyRecallExtended { get; init; }

    public double HybridBestRecallExtended { get; init; }

    public int CandidateLossCount { get; init; }

    public int DenseCandidateDroppedCount { get; init; }

    public int EligibilityMismatchCount { get; init; }

    public int ProviderScopeMismatchCount { get; init; }

    public int TopKConfigMismatchCount { get; init; }

    public int QueryVectorMismatchCount { get; init; }

    public int DedupOverwriteCount { get; init; }

    public bool UseForRuntime { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<HybridRecallRegressionAuditProfileResult> Profiles { get; init; } = Array.Empty<HybridRecallRegressionAuditProfileResult>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = HybridRecallRegressionAuditRecommendations.KeepPreviewOnly;
}


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


/// <summary>eligibility recall loss triage 推荐结论。</summary>
public static class VectorEligibilityRecallLossTriageRecommendations
{
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string ReadyForSectionRoutedRecallRepair = nameof(ReadyForSectionRoutedRecallRepair);
    public const string NeedsMetadataRepair = nameof(NeedsMetadataRepair);
    public const string NeedsEvalExpectationReview = nameof(NeedsEvalExpectationReview);
    public const string UnsafeToRecover = nameof(UnsafeToRecover);
}


/// <summary>单个数据集的 lifecycle-filtered mustHit triage 报告。</summary>
public sealed class VectorEligibilityRecallLossTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public int SampleCount { get; init; }

    public int TotalFilteredMustHit { get; init; }

    public int CorrectlyBlockedCount { get; init; }

    public int RouteToHistoricalCount { get; init; }

    public int RouteToAuditCount { get; init; }

    public int MetadataRepairNeededCount { get; init; }

    public int EvalExpectationReviewNeededCount { get; init; }

    public int UnsafeToRecoverCount { get; init; }

    public int RecoverableWithoutNormalContextCount { get; init; }

    public int RecoverableToNormalContextCount { get; init; }

    public string Recommendation { get; init; } = VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyDictionary<string, int> CategoryBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorEligibilityRecallLossTriageDetail> Details { get; init; } =
        Array.Empty<VectorEligibilityRecallLossTriageDetail>();
}


/// <summary>vector lifecycle metadata repair preview 推荐状态。</summary>
public static class VectorLifecycleMetadataRepairPlanRecommendations
{
    public const string ReadyForMetadataRepairPreview = nameof(ReadyForMetadataRepairPreview);

    public const string NeedsHumanReview = nameof(NeedsHumanReview);

    public const string UnsafeToRepair = nameof(UnsafeToRepair);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>vector lifecycle metadata review candidate CLI / ControlRoom 汇总报告。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SourceReportPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int PendingCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public IReadOnlyDictionary<string, int> CountByStatus { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CountByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CountByItemKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorLifecycleMetadataReviewCandidate> RecentCandidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewCandidate>();

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}


/// <summary>lifecycle metadata evidence/provenance backfill preview/audit 报告；不写 sidecar，不改变 source item。</summary>
public sealed class VectorLifecycleMetadataEvidenceBackfillReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Mode { get; init; } = "preview";

    public string BatchId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string BatchPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int EvidenceFoundCount { get; init; }

    public int SourceRefFoundCount { get; init; }

    public int ProvenanceFoundCount { get; init; }

    public int AutoRepairableAfterBackfillCount { get; init; }

    public int StillHumanReviewRequiredCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int ReplacementConflictCount { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public bool SidecarWritten { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<VectorLifecycleMetadataEvidenceBackfillCandidateStatus> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataEvidenceBackfillCandidateStatus>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>Retrieval Dataset V2 ingestion metadata contract；只描述数据要求，不生成正式数据。</summary>
public sealed class RetrievalDatasetV2MetadataContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public IReadOnlyList<string> CorpusItemRequiredFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> QuerySampleRequiredFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TargetSectionRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RelationEvidenceRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SplitIsolationRules { get; init; } = Array.Empty<string>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "ReadyForDatasetV2Authoring";
}


/// <summary>Retrieval Dataset V2 validator recommendation。</summary>
public static class RetrievalDatasetV2ValidationRecommendations
{
    public const string ReadyForDatasetV2Authoring = nameof(ReadyForDatasetV2Authoring);
    public const string NeedsIngestionMetadataBackfill = nameof(NeedsIngestionMetadataBackfill);
    public const string NeedsRelationEvidenceBackfill = nameof(NeedsRelationEvidenceBackfill);
    public const string NeedsQueryLabelHygiene = nameof(NeedsQueryLabelHygiene);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Retrieval Dataset V2 validation 单条问题。</summary>
public sealed class RetrievalDatasetV2ValidationIssue
{
    public string IssueType { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}


/// <summary>Retrieval Dataset V2 validation 报告；只读检查，不改变正式检索。</summary>
public sealed class RetrievalDatasetV2ValidationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public int CorpusItemCount { get; init; }

    public int QuerySampleCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustNotCount { get; init; }

    public int MustHitMissingFromCorpusCount { get; init; }

    public int MustNotMissingFromCorpusCount { get; init; }

    public int MustHitMustNotOverlapCount { get; init; }

    public int QueryItemIdLeakCount { get; init; }

    public int MissingSourceRefsCount { get; init; }

    public int MissingEvidenceRefsCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int LifecycleTargetSectionMismatchCount { get; init; }

    public int RelationEvidenceMissingCount { get; init; }

    public int SplitIsolationViolationCount { get; init; }

    public int IssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RetrievalDatasetV2ValidationIssue> Issues { get; init; } =
        Array.Empty<RetrievalDatasetV2ValidationIssue>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ValidationRecommendations.KeepPreviewOnly;
}


/// <summary>旧 retrieval/vector eval corpus 限制报告；说明其不适合作为主 recall repair 目标。</summary>
public sealed class RetrievalDatasetLegacyLimitationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string BatchId { get; init; } = string.Empty;

    public int ReviewCandidateCount { get; init; }

    public int MissingEvidenceSourceProvenanceCandidateCount { get; init; }

    public string EvidenceBackfillRecommendation { get; init; } = string.Empty;

    public bool LegacyDatasetSuitableForPrimaryRecallRepair { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredNextDataWork { get; init; } = Array.Empty<string>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill;
}


/// <summary>Retrieval Dataset V2 corpus item provenance。</summary>
public sealed class RetrievalDatasetV2Provenance
{
    public string RecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string IngestionBatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}


/// <summary>Retrieval Dataset V2 relation evidence。</summary>
public sealed class RetrievalDatasetV2Relation
{
    public string RelationId { get; init; } = string.Empty;

    public string SourceItemId { get; init; } = string.Empty;

    public string TargetItemId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}


/// <summary>Retrieval Dataset V2 corpus item；离线生成，不直接进入正式检索。</summary>
public sealed class RetrievalDatasetV2CorpusItem
{
    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public RetrievalDatasetV2Provenance Provenance { get; init; } = new();

    public string SourceFingerprint { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<RetrievalDatasetV2Relation> Relations { get; init; } = Array.Empty<RetrievalDatasetV2Relation>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Anchors { get; init; } = Array.Empty<string>();

    public string Content { get; init; } = string.Empty;

    public string Split { get; init; } = "train";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Retrieval Dataset V2 generated query sample。</summary>
public sealed class RetrievalDatasetV2Sample
{
    public string SampleId { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string Difficulty { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitItemIds { get; init; } = Array.Empty<string>();

    public string Rationale { get; init; } = string.Empty;

    public IReadOnlyList<string> NegativeDistractorIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredRelations { get; init; } = Array.Empty<string>();

    public string ExpectedLifecycleBehavior { get; init; } = string.Empty;

    public string Split { get; init; } = "test";

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public RetrievalDatasetV2Provenance Provenance { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Retrieval Dataset V2 生成结果。</summary>
public sealed class RetrievalDatasetV2GeneratedDataset
{
    public IReadOnlyList<RetrievalDatasetV2CorpusItem> CorpusItems { get; init; } =
        Array.Empty<RetrievalDatasetV2CorpusItem>();

    public IReadOnlyList<RetrievalDatasetV2Sample> Samples { get; init; } =
        Array.Empty<RetrievalDatasetV2Sample>();
}


/// <summary>Retrieval Dataset V2 generator recommendation。</summary>
public static class RetrievalDatasetV2GenerationRecommendations
{
    public const string ReadyForDatasetV2ShadowEval = nameof(ReadyForDatasetV2ShadowEval);
    public const string NeedsGenerationRepair = nameof(NeedsGenerationRepair);
    public const string BlockedByValidationIssues = nameof(BlockedByValidationIssues);
    public const string BlockedByMissingEvidence = nameof(BlockedByMissingEvidence);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string NotConfigured = nameof(NotConfigured);
}


/// <summary>Retrieval Dataset V2 generation report。</summary>
public sealed class RetrievalDatasetV2GenerationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public RetrievalDatasetV2GenerationOptions Options { get; init; } = new();

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int MustNotOverlapCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public int JudgeWarningCount { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2GenerationRecommendations.NotConfigured;

    public IReadOnlyList<string> PromptTemplates { get; init; } = Array.Empty<string>();
}


/// <summary>Retrieval Dataset V2 quality report。</summary>
public sealed class RetrievalDatasetV2QualityReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int MustNotOverlapCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public int JudgeWarningCount { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2GenerationRecommendations.NotConfigured;
}


/// <summary>Retrieval Dataset V2 物化 manifest。</summary>
public sealed class RetrievalDatasetV2Manifest
{
    public string DatasetId { get; init; } = string.Empty;

    public string CorpusPath { get; init; } = string.Empty;

    public string SamplesPath { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = "retrieval-dataset-v2-generator/v1";

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
}


/// <summary>Retrieval Dataset V2 materialization gate recommendation。</summary>
public static class RetrievalDatasetV2MaterializationRecommendations
{
    public const string ReadyForDatasetV2ShadowEval = nameof(ReadyForDatasetV2ShadowEval);
    public const string BlockedByMissingArtifact = nameof(BlockedByMissingArtifact);
    public const string BlockedByValidationIssues = nameof(BlockedByValidationIssues);
    public const string BlockedByQualityGate = nameof(BlockedByQualityGate);
    public const string BlockedByHashInstability = nameof(BlockedByHashInstability);
    public const string BlockedByRuntimeUse = nameof(BlockedByRuntimeUse);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Retrieval Dataset V2 materialization / immutability gate report。</summary>
public sealed class RetrievalDatasetV2MaterializationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string CorpusPath { get; init; } = string.Empty;

    public string SamplesPath { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = "retrieval-dataset-v2-generator/v1";

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public bool CorpusExists { get; init; }

    public bool SamplesExists { get; init; }

    public bool ManifestExists { get; init; }

    public bool ValidatePassed { get; init; }

    public string QualityRecommendation { get; init; } = string.Empty;

    public bool CorpusHashStable { get; init; }

    public bool SamplesHashStable { get; init; }

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2MaterializationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>Retrieval Dataset V2 shadow eval recommendation。</summary>
public static class RetrievalDatasetV2ShadowEvalRecommendations
{
    public const string ReadyForDatasetV2RetrievalCandidate = nameof(ReadyForDatasetV2RetrievalCandidate);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRecall = nameof(BlockedByRecall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByMustNotRisk = nameof(BlockedByMustNotRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPgVectorParityMismatch = nameof(BlockedByPgVectorParityMismatch);
    public const string BlockedByDatasetValidation = nameof(BlockedByDatasetValidation);
}


/// <summary>Retrieval Dataset V2 单个 profile 的 shadow eval 报告。</summary>
public sealed class RetrievalDatasetV2ShadowEvalProfileReport
{
    public string DatasetId { get; init; } = string.Empty;

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int CorpusItemCount { get; init; }

    public int CandidateCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int DenseCandidateCount { get; init; }

    public int LexicalCandidateCount { get; init; }

    public int AnchorCandidateCount { get; init; }

    public int UnionCandidateCount { get; init; }

    public int EligibilityBlockedCount { get; init; }

    public int MustHitBlockedByEligibilityCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int TargetSectionMismatchCount { get; init; }

    public double TopKOverlapRate { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;
}


/// <summary>Retrieval Dataset V2 stress / leakage / readiness recommendation。</summary>
public static class RetrievalDatasetV2StressRecommendations
{
    public const string ReadyForDatasetV2StressFreeze = nameof(ReadyForDatasetV2StressFreeze);
    public const string NeedsHarderDataset = nameof(NeedsHarderDataset);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string BlockedByAnchorDominance = nameof(BlockedByAnchorDominance);
    public const string BlockedByHoldoutRecall = nameof(BlockedByHoldoutRecall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Retrieval Dataset V2 stress / holdout / leakage audit 报告。</summary>
public sealed class RetrievalDatasetV2StressReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int LeakageIssueCount { get; init; }

    public int UniqueAnchorLeakageCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RationaleLeakageCount { get; init; }

    public int SplitLeakageCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public double AnchorAblationRecallDelta { get; init; }

    public double AnchorShuffleRecallDelta { get; init; }

    public double DenseRecall { get; init; }

    public double LexicalRecall { get; init; }

    public double AnchorRecall { get; init; }

    public double HybridRecall { get; init; }

    public double HoldoutHybridRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2StressRecommendations.KeepPreviewOnly;

    public IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> Profiles { get; init; } =
        Array.Empty<RetrievalDatasetV2ShadowEvalProfileReport>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>Retrieval Dataset V2 stress recall failure 分类。</summary>
public static class RetrievalDatasetV2StressFailureReasons
{
    public const string MustHitMissingFromCandidateSet = nameof(MustHitMissingFromCandidateSet);
    public const string MustHitBelowTopK = nameof(MustHitBelowTopK);
    public const string MustHitBlockedByEligibility = nameof(MustHitBlockedByEligibility);
    public const string TargetSectionMismatch = nameof(TargetSectionMismatch);
    public const string DenseSemanticMismatch = nameof(DenseSemanticMismatch);
    public const string LexicalTokenMismatch = nameof(LexicalTokenMismatch);
    public const string AnchorMetadataInsufficient = nameof(AnchorMetadataInsufficient);
    public const string AnchorRankingRegression = nameof(AnchorRankingRegression);
    public const string HybridUnionRankingRegression = nameof(HybridUnionRankingRegression);
    public const string NegativeDistractorOutranksMustHit = nameof(NegativeDistractorOutranksMustHit);
    public const string QueryTooSparse = nameof(QueryTooSparse);
    public const string MultiHopRelationNotRepresented = nameof(MultiHopRelationNotRepresented);
    public const string LifecycleTrapTooAmbiguous = nameof(LifecycleTrapTooAmbiguous);
    public const string Unknown = nameof(Unknown);
}


/// <summary>Retrieval Dataset V2 stress recall failure triage recommendation。</summary>
public static class RetrievalDatasetV2StressFailureTriageRecommendations
{
    public const string ReadyForRankingRepairPreview = nameof(ReadyForRankingRepairPreview);
    public const string NeedsAnchorRankingRepair = nameof(NeedsAnchorRankingRepair);
    public const string NeedsQueryNormalizationRepair = nameof(NeedsQueryNormalizationRepair);
    public const string NeedsRelationAwareCandidateProvider = nameof(NeedsRelationAwareCandidateProvider);
    public const string NeedsHybridUnionScoringRepair = nameof(NeedsHybridUnionScoringRepair);
    public const string NeedsHarderNegativePolicy = nameof(NeedsHarderNegativePolicy);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Retrieval Dataset V2 stress recall failure triage / clusters report。</summary>
public sealed class RetrievalDatasetV2StressRecallFailureTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int FailureCount { get; init; }

    public IReadOnlyDictionary<string, int> FailureCountBySplit { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> FailureCountByDifficulty { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> FailureCountByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int HoldoutFailureCount { get; init; }

    public int DenseOnlyWinCount { get; init; }

    public int HybridWinCount { get; init; }

    public int AnchorRegressionCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int MustHitMissingFromCandidateSetCount { get; init; }

    public int EligibilityBlockedCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2StressFailureTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyList<RetrievalDatasetV2StressProfileComparison> ProfileComparisons { get; init; } =
        Array.Empty<RetrievalDatasetV2StressProfileComparison>();

    public IReadOnlyList<RetrievalDatasetV2StressRecallFailureDetail> Failures { get; init; } =
        Array.Empty<RetrievalDatasetV2StressRecallFailureDetail>();
}


/// <summary>Hybrid union scoring repair recommendation。</summary>
public static class HybridUnionScoringRepairRecommendations
{
    public const string ReadyForDatasetV2StressFreeze = nameof(ReadyForDatasetV2StressFreeze);
    public const string NeedsMoreRankingRepair = nameof(NeedsMoreRankingRepair);
    public const string BlockedByDenseRegression = nameof(BlockedByDenseRegression);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByNegativeDistractor = nameof(BlockedByNegativeDistractor);
    public const string BlockedByAnchorRegression = nameof(BlockedByAnchorRegression);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Hybrid union scoring repair 单个 profile 评估。</summary>
public sealed class HybridUnionScoringRepairProfileReport
{
    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double HoldoutRecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public double DenseOnlyRecall { get; init; }

    public double DenseOnlyHoldoutRecall { get; init; }

    public double RecallDeltaVsDense { get; init; }

    public double HoldoutRecallDeltaVsDense { get; init; }

    public int HybridRegressionCount { get; init; }

    public int DenseWinnerPreservedCount { get; init; }

    public int DenseWinnerLostCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int NegativeDistractorOutranksMustHitCount { get; init; }

    public int AnchorRankingRegressionCount { get; init; }

    public string Recommendation { get; init; } = HybridUnionScoringRepairRecommendations.KeepPreviewOnly;
}


/// <summary>Hybrid union scoring repair preview / gate 报告。</summary>
public sealed class HybridUnionScoringRepairReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string BestProfileName { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = HybridUnionScoringRepairRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<HybridUnionScoringRepairProfileReport> Profiles { get; init; } =
        Array.Empty<HybridUnionScoringRepairProfileReport>();
}


/// <summary>Hybrid scoring risk regression triage recommendation。</summary>
public static class HybridScoringRiskRegressionRecommendations
{
    public const string ReadyForSafeScoringRepair = nameof(ReadyForSafeScoringRepair);
    public const string NeedsPostScoringRiskGate = nameof(NeedsPostScoringRiskGate);
    public const string NeedsScoreCap = nameof(NeedsScoreCap);
    public const string NeedsEligibilityOrderFix = nameof(NeedsEligibilityOrderFix);
    public const string NeedsRiskProjectionFix = nameof(NeedsRiskProjectionFix);
    public const string UnsafeProfileDiscard = nameof(UnsafeProfileDiscard);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Hybrid scoring risk regression triage report。</summary>
public sealed class HybridScoringRiskRegressionTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int RiskCandidateCount { get; init; }

    public IReadOnlyDictionary<string, int> RiskByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RiskByDifficulty { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RiskBySplit { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int BlockedCandidateReintroducedCount { get; init; }

    public int MustNotCandidatePromotedCount { get; init; }

    public int LifecycleRiskPromotedCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public int EligibilityBypassCount { get; init; }

    public int RepairableByPostScoringRiskGateCount { get; init; }

    public int RepairableByScoreCapCount { get; init; }

    public int UnsafeProfileCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = HybridScoringRiskRegressionRecommendations.KeepPreviewOnly;

    public IReadOnlyList<HybridScoringRiskRegressionDetail> Details { get; init; } =
        Array.Empty<HybridScoringRiskRegressionDetail>();
}


/// <summary>Vector V4.R readiness recheck 的推荐结论；通过也只允许进入 guarded formal preview。</summary>
public static class VectorV4ReadinessRecheckRecommendations
{
    public const string ReadyForGuardedFormalPreview = nameof(ReadyForGuardedFormalPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByLegacyRisk = nameof(BlockedByLegacyRisk);
    public const string BlockedByDatasetV2Stress = nameof(BlockedByDatasetV2Stress);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByProviderParity = nameof(BlockedByProviderParity);
}


/// <summary>Vector V4.R readiness recheck；只允许产生 guarded formal preview 输入，不启用正式 retrieval。</summary>
public sealed class VectorV4ReadinessRecheckReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool RecheckPassed { get; init; }

    public string Recommendation { get; init; } = VectorV4ReadinessRecheckRecommendations.KeepPreviewOnly;

    public string LegacyVectorStatus { get; init; } = "Unknown";

    public string DatasetV2SmallStatus { get; init; } = "Unknown";

    public string DatasetV2StressStatus { get; init; } = "Unknown";

    public string PgVectorProviderStatus { get; init; } = "Unknown";

    public string Qwen3ProviderComparisonStatus { get; init; } = "Unknown";

    public string HybridRetrievalStatus { get; init; } = "Unknown";

    public string HybridScoringRepairStatus { get; init; } = "Unknown";

    public string RuntimeChangeGateStatus { get; init; } = "Unknown";

    public string BestPreviewProfile { get; init; } = string.Empty;

    public double DatasetV2StressRecall { get; init; }

    public double DatasetV2HoldoutRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int LeakageIssueCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ReadyForGuardedFormalPreview { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Guarded formal retrieval preview recommendation。</summary>
public static class GuardedFormalRetrievalPreviewRecommendations
{
    public const string ReadyForShadowPackageComparison = nameof(ReadyForShadowPackageComparison);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
}


/// <summary>Guarded formal retrieval preview / gate；只输出 would-change 结果，不写正式 package。</summary>
public sealed class GuardedFormalRetrievalPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool V4RecheckPassed { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselineCandidateCount { get; init; }

    public int PreviewVectorCandidateCount { get; init; }

    public int WouldAddCount { get; init; }

    public int WouldRemoveCount { get; init; }

    public int WouldRerankCount { get; init; }

    public int WouldChangeTargetSectionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = GuardedFormalRetrievalPreviewRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Shadow package comparison recommendation。</summary>
public static class VectorShadowPackageComparisonRecommendations
{
    public const string ReadyForScopedFormalPreviewOptIn = nameof(ReadyForScopedFormalPreviewOptIn);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByTokenBudgetRegression = nameof(BlockedByTokenBudgetRegression);
    public const string BlockedByConstraintCoverageRegression = nameof(BlockedByConstraintCoverageRegression);
}


/// <summary>Shadow package comparison 报告；不写正式 package，不改变 runtime。</summary>
public sealed class VectorShadowPackageComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ComparisonPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ShadowPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = VectorShadowPackageComparisonRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Scoped formal preview opt-in mode。</summary>
public static class ScopedFormalPreviewOptInModes
{
    public const string Off = nameof(Off);
    public const string PreviewOnly = nameof(PreviewOnly);
}


/// <summary>Scoped formal preview opt-in recommendation。</summary>
public static class ScopedFormalPreviewOptInRecommendations
{
    public const string ReadyForLimitedFormalPreviewObservation = nameof(ReadyForLimitedFormalPreviewObservation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}


/// <summary>Scoped formal preview opt-in report；只写 shadow artifact，不写正式 package。</summary>
public sealed class ScopedFormalPreviewOptInReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public bool SmokePassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.Off;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string SelectedWorkspaceId { get; init; } = string.Empty;

    public string SelectedCollectionId { get; init; } = string.Empty;

    public string SelectedEvalScope { get; init; } = string.Empty;

    public string NonAllowlistedWorkspaceId { get; init; } = string.Empty;

    public string NonAllowlistedCollectionId { get; init; } = string.Empty;

    public string NonAllowlistedEvalScope { get; init; } = string.Empty;

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public string Recommendation { get; init; } = ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> GateDependencySummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Limited formal preview observation recommendation。</summary>
public static class LimitedFormalPreviewObservationRecommendations
{
    public const string ReadyForFormalPreviewFreeze = nameof(ReadyForFormalPreviewFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Limited formal preview observation report；聚合多轮 preview-only package comparison。</summary>
public sealed class LimitedFormalPreviewObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.PreviewOnly;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MinimumObservationRunCount { get; init; }

    public int ObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int TokenDeltaP95 { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Explicit scoped runtime experiment planning mode；只允许计划和 dry-run。</summary>
public static class ExplicitScopedRuntimeExperimentModes
{
    public const string PlanOnly = nameof(PlanOnly);
    public const string DryRun = nameof(DryRun);
}


/// <summary>Explicit scoped runtime experiment planning recommendation。</summary>
public static class ExplicitScopedRuntimeExperimentRecommendations
{
    public const string ReadyForExplicitScopedRuntimeExperimentDryRun = nameof(ReadyForExplicitScopedRuntimeExperimentDryRun);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingFoundationFreeze = nameof(BlockedByMissingFoundationFreeze);
    public const string BlockedByMissingServiceFreeze = nameof(BlockedByMissingServiceFreeze);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Scoped runtime experiment dry-run observation mode；只允许 dry-run。</summary>
public static class ScopedRuntimeExperimentDryRunObservationModes
{
    public const string DryRun = nameof(DryRun);
}


/// <summary>Scoped runtime experiment dry-run observation recommendation。</summary>
public static class ScopedRuntimeExperimentDryRunObservationRecommendations
{
    public const string ReadyForScopedRuntimeExperimentDesignFreeze = nameof(ReadyForScopedRuntimeExperimentDesignFreeze);
    public const string NeedsMoreDryRunObservation = nameof(NeedsMoreDryRunObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Scoped runtime experiment dry-run observation report；只聚合 dry-run 观测和边界检查。</summary>
public sealed class ScopedRuntimeExperimentDryRunObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentDryRunObservationModes.DryRun;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int ObservationRunCount { get; init; }

    public int MinimumObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int DryRunPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool RuntimeChangeGateConsistent { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } =
        ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Explicit scoped runtime experiment proposal mode；只允许 proposal。</summary>
public static class ExplicitScopedRuntimeExperimentProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
}


/// <summary>Explicit scoped runtime experiment proposal recommendation。</summary>
public static class ExplicitScopedRuntimeExperimentProposalRecommendations
{
    public const string ReadyForManualExperimentApproval = nameof(ReadyForManualExperimentApproval);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Explicit scoped runtime experiment proposal report；不写 runtime 配置。</summary>
public sealed class ExplicitScopedRuntimeExperimentProposalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public bool ProposalPassed { get; init; }

    public string Recommendation { get; init; } =
        ExplicitScopedRuntimeExperimentProposalRecommendations.KeepPreviewOnly;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string EvalScopeId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ProposedConfigPatch { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ObservationPlan { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool ApprovalRequired { get; init; }

    public bool Approved { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool ConfigPatchWritten { get; init; }

    public bool DiBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Scoped runtime experiment approval mode；V4.9 只允许 no-op harness。</summary>
public static class ScopedRuntimeExperimentApprovalModes
{
    public const string NoOpHarnessOnly = nameof(NoOpHarnessOnly);
    public const string ScopedRuntimeExperiment = nameof(ScopedRuntimeExperiment);
}


/// <summary>Scoped runtime experiment approval/no-op harness recommendation。</summary>
public static class ScopedRuntimeExperimentApprovalRecommendations
{
    public const string ReadyForActivationPreflight = nameof(ReadyForActivationPreflight);
    public const string ReadyForScopedRuntimeExperimentDryRunHarnessFreeze = nameof(ReadyForScopedRuntimeExperimentDryRunHarnessFreeze);
    public const string NeedsManualApproval = nameof(NeedsManualApproval);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByExpiredApproval = nameof(BlockedByExpiredApproval);
    public const string BlockedByRevokedApproval = nameof(BlockedByRevokedApproval);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByWrongApprovalMode = nameof(BlockedByWrongApprovalMode);
    public const string BlockedByMissingAcknowledgement = nameof(BlockedByMissingAcknowledgement);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Scoped runtime experiment approval preview/write report。</summary>
public sealed class ScopedRuntimeExperimentApprovalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool ApprovalPassed { get; init; }

    public bool PreviewOnly { get; init; } = true;

    public bool RecordWritten { get; init; }

    public bool Confirmed { get; init; }

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public string ApprovedBy { get; init; } = string.Empty;

    public bool RollbackPlanAvailable { get; init; }

    public bool KillSwitchPlanAvailable { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyChangeAllowed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public ScopedRuntimeExperimentApprovalRecord? ApprovalRecord { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.12 runtime approval request preview；只展示审批材料，不写 approval record。</summary>
public sealed class ScopedRuntimeExperimentApprovalRequestPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyList<string> ObservationPlan { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public bool PreviewOnly { get; init; } = true;

    public bool RecordWritten { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval;
}


/// <summary>V4.13 activation preflight mode；只允许 preflight + dry-run route。</summary>
public static class ScopedRuntimeExperimentActivationPreflightModes
{
    public const string PreflightAndDryRunRoute = nameof(PreflightAndDryRunRoute);
}


/// <summary>V4.13 activation preflight recommendation。</summary>
public static class ScopedRuntimeExperimentActivationPreflightRecommendations
{
    public const string ReadyForGuardedScopedRuntimeExperiment = nameof(ReadyForGuardedScopedRuntimeExperiment);
    public const string NeedsActivationConfig = nameof(NeedsActivationConfig);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingTraceSink = nameof(BlockedByMissingTraceSink);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V4.13 activation preflight / guarded runtime dry-run route report。</summary>
public sealed class ScopedRuntimeExperimentActivationPreflightReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreflightPassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentActivationPreflightRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public bool KillSwitchAvailable { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool TraceSinkAvailable { get; init; }

    public bool ConfigPatchPreviewed { get; init; }

    public bool ConfigPatchWritten { get; init; }

    public bool RuntimeRouteDryRunExecuted { get; init; }

    public int DryRunRouteHitCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.14 guarded scoped runtime experiment mode；仅允许 scoped shadow runtime experiment。</summary>
public static class GuardedScopedRuntimeExperimentModes
{
    public const string ShadowRuntimeExperiment = nameof(ShadowRuntimeExperiment);
}


/// <summary>V4.14 guarded scoped runtime experiment recommendation。</summary>
public static class GuardedScopedRuntimeExperimentRecommendations
{
    public const string ReadyForScopedRuntimeExperimentObservation = nameof(ReadyForScopedRuntimeExperimentObservation);
    public const string NeedsMoreExperimentRuns = nameof(NeedsMoreExperimentRuns);
    public const string BlockedByMissingActivationGate = nameof(BlockedByMissingActivationGate);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByWrongApprovalMode = nameof(BlockedByWrongApprovalMode);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByRollbackFailure = nameof(BlockedByRollbackFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V4.14 guarded scoped runtime experiment report；正式 retrieval/package 保持不变。</summary>
public sealed class GuardedScopedRuntimeExperimentReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ExperimentPassed { get; init; }

    public string Recommendation { get; init; } = GuardedScopedRuntimeExperimentRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedRequestCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ExperimentPreviewPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public bool RollbackVerified { get; init; }

    public int ErrorCount { get; init; }

    public int LatencyP50 { get; init; }

    public int LatencyP95 { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<ScopedRuntimeExperimentTrace> Traces { get; init; } = Array.Empty<ScopedRuntimeExperimentTrace>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.15 scoped runtime experiment observation window mode；仅允许 scoped shadow observation。</summary>
public static class ScopedRuntimeExperimentObservationWindowModes
{
    public const string ScopedShadowObservation = nameof(ScopedShadowObservation);
}


/// <summary>V4.15 scoped runtime experiment observation window recommendation。</summary>
public static class ScopedRuntimeExperimentObservationWindowRecommendations
{
    public const string ReadyForScopedRuntimeExperimentObservationFreeze = nameof(ReadyForScopedRuntimeExperimentObservationFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByTraceGap = nameof(BlockedByTraceGap);
    public const string BlockedByLatency = nameof(BlockedByLatency);
    public const string BlockedByRollbackFailure = nameof(BlockedByRollbackFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V4.15 scoped runtime experiment observation window report；只写 shadow artifact/trace。</summary>
public sealed class ScopedRuntimeExperimentObservationWindowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ObservationWindowId { get; init; } = string.Empty;

    public bool ObservationPassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int ObservationRunCount { get; init; }

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedRequestCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ExperimentPreviewPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchSmokePassed { get; init; }

    public bool RollbackVerified { get; init; }

    public double TraceCompleteness { get; init; }

    public int ErrorCount { get; init; }

    public int LatencyP50 { get; init; }

    public int LatencyP95 { get; init; }

    public bool StopConditionTriggered { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<ScopedRuntimeExperimentTrace> Traces { get; init; } = Array.Empty<ScopedRuntimeExperimentTrace>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>formal retrieval integration plan mode；本阶段只允许 PlanOnly。</summary>
public static class FormalRetrievalIntegrationPlanModes
{
    public const string PlanOnly = nameof(PlanOnly);
}


/// <summary>formal retrieval integration plan recommendation。</summary>
public static class FormalRetrievalIntegrationPlanRecommendations
{
    public const string ReadyForShadowFormalRetrievalAdapter = nameof(ReadyForShadowFormalRetrievalAdapter);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingPromotionDecision = nameof(BlockedByMissingPromotionDecision);
    public const string BlockedByP15Gate = nameof(BlockedByP15Gate);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByFormalOutputMutation = nameof(BlockedByFormalOutputMutation);
    public const string BlockedByPackageOutputMutation = nameof(BlockedByPackageOutputMutation);
    public const string BlockedByPackingPolicyMutation = nameof(BlockedByPackingPolicyMutation);
    public const string BlockedByVectorBindingMutation = nameof(BlockedByVectorBindingMutation);
}


/// <summary>shadow formal retrieval adapter plan recommendations。</summary>
public static class ShadowFormalRetrievalAdapterPlanRecommendations
{
    public const string ReadyForShadowAdapterDesignFreeze = nameof(ReadyForShadowAdapterDesignFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingProjectStateAudit = nameof(BlockedByMissingProjectStateAudit);
    public const string BlockedByMissingPrerequisiteGate = nameof(BlockedByMissingPrerequisiteGate);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string BlockedByFormalRetrievalAttempt = nameof(BlockedByFormalRetrievalAttempt);
    public const string BlockedByPackageMutation = nameof(BlockedByPackageMutation);
    public const string BlockedByIncompleteAdapterPlan = nameof(BlockedByIncompleteAdapterPlan);
}


/// <summary>shadow formal retrieval adapter recommendations。</summary>
public static class ShadowFormalRetrievalAdapterRecommendations
{
    public const string ReadyForShadowAdapterFreeze = nameof(ReadyForShadowAdapterFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingPlanGate = nameof(BlockedByMissingPlanGate);
    public const string BlockedByPlanGateNotPassed = nameof(BlockedByPlanGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyShadowOutput = nameof(BlockedByEmptyShadowOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByTargetSectionViolation = nameof(BlockedByTargetSectionViolation);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalSelectedSetChange = nameof(BlockedByFormalSelectedSetChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}


/// <summary>shadow formal retrieval adapter run report；只产出影子候选与 trace，不接入正式 retrieval。</summary>
public sealed class ShadowFormalRetrievalAdapterReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool AdapterPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = ShadowFormalRetrievalAdapterRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ShadowOnly";

    public string RequiredNextPhase { get; init; } = "ShadowFormalRetrievalAdapterFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public IReadOnlyList<string> AdapterInputs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AdapterOutputs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> GateOrder { get; init; } = Array.Empty<string>();

    public int SampleCount { get; init; }

    public int TotalBaselineCandidateCount { get; init; }

    public int TotalShadowVectorCandidateCount { get; init; }

    public int TotalShadowGraphCandidateCount { get; init; }

    public int TotalMergedShadowCandidateCount { get; init; }

    public int TotalFilteredCandidateCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<ShadowFormalRetrievalAdapterSampleResult> Samples { get; init; }
        = Array.Empty<ShadowFormalRetrievalAdapterSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>formal adapter package shadow comparison recommendations。</summary>
public static class FormalAdapterPackageShadowComparisonRecommendations
{
    public const string ReadyForFormalAdapterPackageShadowFreeze = nameof(ReadyForFormalAdapterPackageShadowFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingAdapterGate = nameof(BlockedByMissingAdapterGate);
    public const string BlockedByAdapterGateNotPassed = nameof(BlockedByAdapterGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyShadowOutput = nameof(BlockedByEmptyShadowOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByTokenBudgetExceeded = nameof(BlockedByTokenBudgetExceeded);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalSelectedSetChange = nameof(BlockedByFormalSelectedSetChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByFormalPackageWritten = nameof(BlockedByFormalPackageWritten);
}


/// <summary>formal adapter package shadow comparison report；只生成报告，不写 formal package。</summary>
public sealed class FormalAdapterPackageShadowComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ComparisonPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = FormalAdapterPackageShadowComparisonRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ShadowOnly";

    public string RequiredNextPhase { get; init; } = "FormalAdapterPackageShadowFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int TotalBaselinePackageItemCount { get; init; }

    public int TotalShadowPackageItemCount { get; init; }

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int AddedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int OrderChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public int BaselineTokenTotal { get; init; }

    public int ShadowTokenTotal { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int TokenDeltaAbsoluteTotal { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public IReadOnlyDictionary<string, int> BaselineSectionHistogram { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> ShadowSectionHistogram { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public int TokenDeltaBudgetTotal { get; init; }

    public int TokenDeltaBudgetPerSample { get; init; }

    public IReadOnlyList<FormalAdapterPackageShadowComparisonSampleResult> Samples { get; init; }
        = Array.Empty<FormalAdapterPackageShadowComparisonSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>graph + vector retrieval quality audit recommendations。</summary>
public static class GraphVectorRetrievalQualityAuditRecommendations
{
    public const string ReadyForRetrievalQualityFreeze = nameof(ReadyForRetrievalQualityFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingPackageShadowGate = nameof(BlockedByMissingPackageShadowGate);
    public const string BlockedByPackageShadowGateNotPassed = nameof(BlockedByPackageShadowGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyAuditOutput = nameof(BlockedByEmptyAuditOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByGraphNoiseExceedsThreshold = nameof(BlockedByGraphNoiseExceedsThreshold);
    public const string BlockedByRankingRegressionExceedsThreshold = nameof(BlockedByRankingRegressionExceedsThreshold);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}


/// <summary>retrieval quality audit failure cluster IDs。</summary>
public static class GraphVectorRetrievalQualityAuditFailureClusters
{
    public const string MissingCandidate = nameof(MissingCandidate);
    public const string RankingTooLow = nameof(RankingTooLow);
    public const string GraphNoise = nameof(GraphNoise);
    public const string VectorNoise = nameof(VectorNoise);
    public const string SectionMismatch = nameof(SectionMismatch);
    public const string LifecycleMismatch = nameof(LifecycleMismatch);
    public const string MetadataEvidenceGap = nameof(MetadataEvidenceGap);
}


/// <summary>retrieval quality audit failure cluster details。</summary>
public sealed class GraphVectorRetrievalQualityAuditFailureCluster
{
    public string ClusterId { get; init; } = string.Empty;

    public int Count { get; init; }

    public IReadOnlyList<string> SampleIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ItemIds { get; init; } = Array.Empty<string>();

    public string Description { get; init; } = string.Empty;
}


/// <summary>graph + vector retrieval quality audit report。</summary>
public sealed class GraphVectorRetrievalQualityAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool AuditPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = GraphVectorRetrievalQualityAuditRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "AuditOnly";

    public string RequiredNextPhase { get; init; } = "RetrievalQualityFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int MustHitTotal { get; init; }

    public int MustHitRecalledTotal { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int VectorContributionCount { get; init; }

    public int GraphContributionCount { get; init; }

    public int OverlapCount { get; init; }

    public int VectorOnlyCount { get; init; }

    public int GraphOnlyCount { get; init; }

    public int GraphNoiseCount { get; init; }

    public int VectorNoiseCount { get; init; }

    public int RankingRegressionCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public int MetadataEvidenceGapCount { get; init; }

    public int TopK { get; init; }

    public int GraphNoiseThreshold { get; init; }

    public int RankingRegressionThreshold { get; init; }

    public int MustHitBelowTopKThreshold { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<GraphVectorRetrievalQualityAuditFailureCluster> FailureClusters { get; init; }
        = Array.Empty<GraphVectorRetrievalQualityAuditFailureCluster>();

    public IReadOnlyList<GraphVectorRetrievalQualityAuditSampleResult> Samples { get; init; }
        = Array.Empty<GraphVectorRetrievalQualityAuditSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>retrieval quality repair preview recommendations。</summary>
public static class RetrievalQualityRepairPreviewRecommendations
{
    public const string ReadyForRetrievalQualityRepairFreeze = nameof(ReadyForRetrievalQualityRepairFreeze);
    public const string KeepBaselineOnly = nameof(KeepBaselineOnly);
    public const string BlockedByMissingQualityGate = nameof(BlockedByMissingQualityGate);
    public const string BlockedByQualityGateNotPassed = nameof(BlockedByQualityGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyRepairOutput = nameof(BlockedByEmptyRepairOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByGraphNoiseRegression = nameof(BlockedByGraphNoiseRegression);
    public const string BlockedByRankingRegression = nameof(BlockedByRankingRegression);
    public const string BlockedByRecallRegression = nameof(BlockedByRecallRegression);
    public const string BlockedByMrrRegression = nameof(BlockedByMrrRegression);
    public const string BlockedByTokenBudgetExceeded = nameof(BlockedByTokenBudgetExceeded);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
    public const string BlockedByNoRepairProfileImprovement = nameof(BlockedByNoRepairProfileImprovement);
}


/// <summary>retrieval quality repair profile result。</summary>
public sealed class RetrievalQualityRepairProfileResult
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileLabel { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int MustHitTotal { get; init; }

    public int MustHitRecalledTotal { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int VectorContributionCount { get; init; }

    public int GraphContributionCount { get; init; }

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

    public int BaselineTokenTotal { get; init; }

    public int RepairTokenTotal { get; init; }

    public int TokenDelta { get; init; }

    public int TokenDeltaAbsolute { get; init; }

    public double RecallDelta { get; init; }

    public double PrecisionDelta { get; init; }

    public double MrrDelta { get; init; }

    public int MustHitBelowTopKDelta { get; init; }

    public bool RiskRegressionDetected { get; init; }

    public bool RankingRegressionDetected { get; init; }

    public bool RecallRegressionDetected { get; init; }

    public bool MrrRegressionDetected { get; init; }

    public bool GraphNoiseRegressionDetected { get; init; }

    public bool TokenBudgetExceeded { get; init; }

    public IReadOnlyList<string> AppliedAdjustments { get; init; } = Array.Empty<string>();
}


/// <summary>retrieval quality repair preview report。</summary>
public sealed class RetrievalQualityRepairPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public string RequiredNextPhase { get; init; } = "RetrievalQualityRepairFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int MaxTokenDeltaTotal { get; init; }

    public int MaxTokenDeltaPerSample { get; init; }

    public RetrievalQualityRepairProfileResult Baseline { get; init; }
        = new RetrievalQualityRepairProfileResult();

    public IReadOnlyList<RetrievalQualityRepairProfileResult> Profiles { get; init; }
        = Array.Empty<RetrievalQualityRepairProfileResult>();

    public string BestProfileId { get; init; } = string.Empty;

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>runtime-observable retrieval feature contract report。</summary>
public sealed class RuntimeObservableFeatureContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ContractPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeObservableFeatureContractRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "AuditOnly";

    public string RequiredNextPhase { get; init; } = "RuntimeObservableFeatureFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public string BestProfileId { get; init; } = string.Empty;

    public string BestProfileContractStatus { get; init; } = RuntimeObservableFeatureContractStatuses.None;

    public IReadOnlyList<RuntimeObservableFeatureContractProfile> Profiles { get; init; }
        = Array.Empty<RuntimeObservableFeatureContractProfile>();

    public IReadOnlyList<RuntimeObservableFeatureUsage> Catalog { get; init; }
        = Array.Empty<RuntimeObservableFeatureUsage>();

    public int ScoringFeatureCount { get; init; }

    public int FilteringFeatureCount { get; init; }

    public int CandidateExpansionFeatureCount { get; init; }

    public int RuntimeObservableCount { get; init; }

    public int DerivedAtRuntimeCount { get; init; }

    public int EvalOnlyCount { get; init; }

    public int ForbiddenForScoringCount { get; init; }

    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; }
        = new RuntimeObservableFeatureContractSourceScan();

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>runtime retrieval feature derivation recommendations。</summary>
public static class RuntimeRetrievalFeatureDerivationRecommendations
{
    public const string ReadyForRuntimeFeatureFreeze = nameof(ReadyForRuntimeFeatureFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingContractGate = nameof(BlockedByMissingContractGate);
    public const string BlockedByContractGateNotPassed = nameof(BlockedByContractGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyDerivedEnvelope = nameof(BlockedByEmptyDerivedEnvelope);
    public const string BlockedByForbiddenInputUsed = nameof(BlockedByForbiddenInputUsed);
    public const string BlockedByDerivedRecallRegression = nameof(BlockedByDerivedRecallRegression);
    public const string BlockedByDerivedMrrRegression = nameof(BlockedByDerivedMrrRegression);
    public const string BlockedByDerivedRiskNonZero = nameof(BlockedByDerivedRiskNonZero);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByFixtureSpecialCasing = nameof(BlockedByFixtureSpecialCasing);
    public const string BlockedBySourceScanMissing = nameof(BlockedBySourceScanMissing);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}


/// <summary>runtime retrieval feature derivation preview report。</summary>
public sealed class RuntimeRetrievalFeatureDerivationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeRetrievalFeatureDerivationRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public string RequiredNextPhase { get; init; } = "RuntimeFeatureDerivationFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int SeedTopK { get; init; }

    public int SampleCount { get; init; }

    public double TargetSectionMatchRate { get; init; }

    public double RequiredRelationCoverageRate { get; init; }

    public double EvidenceAnchorCoverageRate { get; init; }

    public double SourceAnchorCoverageRate { get; init; }

    public double DerivationCompletenessRate { get; init; }

    public double BaselineRecall { get; init; }

    public double BaselinePrecision { get; init; }

    public double BaselineMeanReciprocalRank { get; init; }

    public int BaselineMustHitBelowTopKCount { get; init; }

    public int BaselineRiskAfterPolicy { get; init; }

    public int BaselineMustNotHitRiskAfterPolicy { get; init; }

    public int BaselineLifecycleRiskAfterPolicy { get; init; }

    public int BaselineSectionMismatchCount { get; init; }

    public double DerivedRecall { get; init; }

    public double DerivedPrecision { get; init; }

    public double DerivedMeanReciprocalRank { get; init; }

    public int DerivedMustHitBelowTopKCount { get; init; }

    public int DerivedRiskAfterPolicy { get; init; }

    public int DerivedMustNotHitRiskAfterPolicy { get; init; }

    public int DerivedLifecycleRiskAfterPolicy { get; init; }

    public int DerivedSectionMismatchCount { get; init; }

    public double EvalDrivenRecall { get; init; }

    public double EvalDrivenPrecision { get; init; }

    public double EvalDrivenMeanReciprocalRank { get; init; }

    public double DerivedRecallDelta { get; init; }

    public double DerivedMrrDelta { get; init; }

    public int ForbiddenSampleAnnotationReadCount { get; init; }

    public double MaxAllowedRecallRegression { get; init; }

    public double MaxAllowedMrrRegression { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; }
        = new RuntimeObservableFeatureContractSourceScan();

    public IReadOnlyList<RuntimeRetrievalFeatureDerivationSampleResult> Samples { get; init; }
        = Array.Empty<RuntimeRetrievalFeatureDerivationSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>runtime feature derivation repair recommendations。</summary>
public static class RuntimeRetrievalFeatureDerivationRepairRecommendations
{
    public const string ReadyForRuntimeFeatureDerivationRepairFreeze = nameof(ReadyForRuntimeFeatureDerivationRepairFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDerivationGate = nameof(BlockedByMissingDerivationGate);
    public const string BlockedByDerivationGateNotPassed = nameof(BlockedByDerivationGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyRepairEnvelope = nameof(BlockedByEmptyRepairEnvelope);
    public const string BlockedByForbiddenSampleAnnotationRead = nameof(BlockedByForbiddenSampleAnnotationRead);
    public const string BlockedByDerivedRecallNotImproved = nameof(BlockedByDerivedRecallNotImproved);
    public const string BlockedByDerivedMrrNotImproved = nameof(BlockedByDerivedMrrNotImproved);
    public const string BlockedByHoldoutRecallRegression = nameof(BlockedByHoldoutRecallRegression);
    public const string BlockedByHoldoutMrrRegression = nameof(BlockedByHoldoutMrrRegression);
    public const string BlockedByLowRelationCoverage = nameof(BlockedByLowRelationCoverage);
    public const string BlockedByZeroAnchorCoverage = nameof(BlockedByZeroAnchorCoverage);
    public const string BlockedByDerivedRiskNonZero = nameof(BlockedByDerivedRiskNonZero);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByFixtureSpecialCasing = nameof(BlockedByFixtureSpecialCasing);
    public const string BlockedBySourceScanMissing = nameof(BlockedBySourceScanMissing);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}


/// <summary>runtime feature derivation repair preview report。</summary>
public sealed class RuntimeRetrievalFeatureDerivationRepairReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeRetrievalFeatureDerivationRepairRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public string RequiredNextPhase { get; init; } = "RuntimeFeatureDerivationRepairFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int DenseSeedTopK { get; init; }

    public int AnchorSeedTopK { get; init; }

    public int RelationTopK { get; init; }

    public int SampleCount { get; init; }

    public int TrainSampleCount { get; init; }

    public int HoldoutSampleCount { get; init; }

    public double TrainBaselineRecall { get; init; }

    public double TrainBaselineMrr { get; init; }

    public double TrainDerivedRecall { get; init; }

    public double TrainDerivedMrr { get; init; }

    public double HoldoutBaselineRecall { get; init; }

    public double HoldoutBaselineMrr { get; init; }

    public double HoldoutDerivedRecall { get; init; }

    public double HoldoutDerivedMrr { get; init; }

    public double TargetSectionMatchRate { get; init; }

    public double CanonicalRequiredRelationCoverageRate { get; init; }

    public double CanonicalEvidenceAnchorCoverageRate { get; init; }

    public double CanonicalSourceAnchorCoverageRate { get; init; }

    public int ApplicableEvidenceSampleCount { get; init; }

    public int ApplicableSourceSampleCount { get; init; }

    public int ApplicableRelationSampleCount { get; init; }

    public int ApplicableEvidenceCoveredCount { get; init; }

    public int ApplicableSourceCoveredCount { get; init; }

    public int ApplicableRelationCoveredCount { get; init; }

    public int DerivedRiskAfterPolicy { get; init; }

    public int DerivedMustNotHitRiskAfterPolicy { get; init; }

    public int DerivedLifecycleRiskAfterPolicy { get; init; }

    public int DerivedSectionMismatchCount { get; init; }

    public int ForbiddenSampleAnnotationReadCount { get; init; }

    public double MinRelationCoverageRate { get; init; }

    public double MaxAllowedHoldoutRecallRegression { get; init; }

    public double MaxAllowedHoldoutMrrRegression { get; init; }

    public IReadOnlyList<string> DerivationDiagnostics { get; init; } = Array.Empty<string>();

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; }
        = new RuntimeObservableFeatureContractSourceScan();

    public IReadOnlyList<RuntimeRetrievalFeatureDerivationRepairSampleResult> Samples { get; init; }
        = Array.Empty<RuntimeRetrievalFeatureDerivationRepairSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>图枢纽噪声控制推荐。</summary>
public static class GraphHubNoiseControlRecommendations
{
    public const string ReadyForHubNoiseControlFreeze = nameof(ReadyForHubNoiseControlFreeze);
    public const string KeepBaselineOnly = nameof(KeepBaselineOnly);
    public const string BlockedByMissingFreezeGate = nameof(BlockedByMissingFreezeGate);
    public const string BlockedByFreezeGateNotPassed = nameof(BlockedByFreezeGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByRecallRegression = nameof(BlockedByRecallRegression);
    public const string BlockedByMrrRegression = nameof(BlockedByMrrRegression);
    public const string BlockedByHoldoutRecallRegression = nameof(BlockedByHoldoutRecallRegression);
    public const string BlockedByRiskNonZero = nameof(BlockedByRiskNonZero);
}


/// <summary>单样本图枢纽指标。</summary>
public sealed class GraphHubNoiseControlSampleMetrics
{
    public string SampleId { get; init; } = string.Empty;

    public int SeedItemCount { get; init; }

    public int HubItemCount { get; init; }

    public int TotalRelationDegree { get; init; }

    public int MaxRelationDegree { get; init; }

    public int EnvelopeRelationCount { get; init; }

    public double EnvelopeWidthRatio { get; init; }

    public double HubDominanceRatio { get; init; }

    public int HubCandidates { get; init; }
}


/// <summary>单 profile 评分结果。</summary>
public sealed class GraphHubNoiseControlProfileResult
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileLabel { get; init; } = string.Empty;

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int MustHitBelowTopKCount { get; init; }
}


/// <summary>图枢纽噪声控制预览报告。</summary>
public sealed class GraphHubNoiseControlReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = GraphHubNoiseControlRecommendations.KeepBaselineOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public int TopK { get; init; }

    public int SampleCount { get; init; }

    public int HubItemCount { get; init; }

    public double AvgEnvelopeWidthRatio { get; init; }

    public double AvgHubDominanceRatio { get; init; }

    public GraphHubNoiseControlProfileResult Baseline { get; init; }
        = new GraphHubNoiseControlProfileResult();

    public GraphHubNoiseControlProfileResult PreviousDerived { get; init; }
        = new GraphHubNoiseControlProfileResult();

    public GraphHubNoiseControlProfileResult HubControlled { get; init; }
        = new GraphHubNoiseControlProfileResult();

    public double HubControlledRecallDelta { get; init; }

    public double HubControlledMrrDelta { get; init; }
}


/// <summary>受控应用合并 dry-run 观察报告。</summary>
public sealed class ControlledAppliedMergeDryRunObservationReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeDryRunDecisionRecommendations.KeepDryRunOnly;

    public string ProposalSourcePath { get; init; } = "";

    public int ObservationRuns { get; init; }

    public int WouldApplyAddCount { get; init; }

    public int WouldApplyRemoveCount { get; init; }

    public int AppliedAddCount { get; init; }

    public int AppliedRemoveCount { get; init; }

    public int MaxAddPerSample { get; init; }

    public int MaxRemovePerSample { get; init; }

    public int TotalTokenDelta { get; init; }

    public int MaxTokenDeltaPerSample { get; init; }

    public int SectionChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public bool RollbackPassed { get; init; }

    public bool KillSwitchTested { get; init; }

    public bool StopConditionsChecked { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>受控应用合并批准推荐。</summary>
public static class ControlledAppliedMergeApprovalRecommendations
{
    public const string ReadyForScopedPreview = nameof(ReadyForScopedPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDryRunDecision = nameof(BlockedByMissingDryRunDecision);
    public const string BlockedByRiskAcknowledgementRequired = nameof(BlockedByRiskAcknowledgementRequired);
    public const string BlockedByRollbackAcknowledgementRequired = nameof(BlockedByRollbackAcknowledgementRequired);
}


/// <summary>受控应用合并批准记录。</summary>
public sealed class ControlledAppliedMergeApprovalReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ApprovalPassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeApprovalRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = "";

    public string ApprovedBy { get; init; } = "";

    public string Reason { get; init; } = "";

    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddDays(7);

    public string ApprovalMode { get; init; } = "ControlledAppliedMergePreviewOnly";

    public string DryRunDecisionSourcePath { get; init; } = "";

    public int WouldApplyAddCount { get; init; }

    public int WouldApplyRemoveCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public bool RollbackPresent { get; init; }

    public bool KillSwitchPresent { get; init; }

    public bool IsRevoked { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>受控应用合并范围预览推荐。</summary>
public static class ControlledAppliedMergeScopedPreviewRecommendations
{
    public const string ReadyForControlledAppliedMergeScopedPreviewGate = nameof(ReadyForControlledAppliedMergeScopedPreviewGate);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByApprovalExpiredOrRevoked = nameof(BlockedByApprovalExpiredOrRevoked);
    public const string BlockedByPreviewSelectedSetUnchanged = nameof(BlockedByPreviewSelectedSetUnchanged);
    public const string BlockedByFormalSelectedSetChanged = nameof(BlockedByFormalSelectedSetChanged);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
}


/// <summary>受控应用合并范围预览报告。</summary>
public sealed class ControlledAppliedMergeScopedPreviewReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeScopedPreviewRecommendations.BlockedByMissingApproval;

    public string ApprovalSourcePath { get; init; } = "";

    public string DryRunDecisionSourcePath { get; init; } = "";

    public bool PreviewSelectedSetChanged { get; init; }

    public int PreviewAddCount { get; init; }

    public int PreviewRemoveCount { get; init; }

    public int AppliedFormalAddCount { get; init; }

    public int AppliedFormalRemoveCount { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public bool RollbackPresent { get; init; }

    public bool KillSwitchPresent { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>架构清理计划推荐。</summary>
public static class ArchitectureCleanupPlanRecommendations
{
    public const string ReadyForCleanupPlan = nameof(ReadyForCleanupPlan);
    public const string BlockedByMissingV6FFreeze = nameof(BlockedByMissingV6FFreeze);
}


/// <summary>Scoped runtime experiment no-op harness mode。</summary>
public static class ScopedRuntimeExperimentNoOpHarnessModes
{
    public const string NoOp = nameof(NoOp);
}


/// <summary>Scoped runtime experiment no-op harness report；不改变正式 retrieval/package。</summary>
public sealed class ScopedRuntimeExperimentNoOpHarnessReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool HarnessPassed { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentNoOpHarnessModes.NoOp;

    public bool SelectedScopeChecked { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int NoOpTraceCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int PreviewPackageCount { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool DiBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool P15GatePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>Guarded scoped runtime experiment plan mode；V4.11 只允许计划模式。</summary>
public static class GuardedScopedRuntimeExperimentPlanModes
{
    public const string PlanOnly = nameof(PlanOnly);
}


/// <summary>Guarded scoped runtime experiment plan recommendation。</summary>
public static class GuardedScopedRuntimeExperimentPlanRecommendations
{
    public const string ReadyForScopedRuntimeExperimentActivationContract = nameof(ReadyForScopedRuntimeExperimentActivationContract);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>lifecycle metadata sidecar preview 报告。</summary>
public sealed class VectorLifecycleMetadataSidecarPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int SidecarEntryCount { get; init; }

    public int NormalContextEntryCount { get; init; }

    public int AuditContextEntryCount { get; init; }

    public int HistoricalContextEntryCount { get; init; }

    public int DiagnosticsOnlyEntryCount { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<VectorLifecycleSidecarMetadataEntry> Entries { get; init; } =
        Array.Empty<VectorLifecycleSidecarMetadataEntry>();
}


/// <summary>sidecar-aware eligibility 预览模式；仅用于 eval/preview，不接正式 retrieval。</summary>
public static class VectorSidecarEligibilityModes
{
    public const string BaseEligibility = nameof(BaseEligibility);

    public const string SidecarAwareEligibility = nameof(SidecarAwareEligibility);
}


/// <summary>sidecar-aware eligibility preview/recheck/quality 报告；不改变正式检索或 source item。</summary>
public sealed class VectorSidecarEligibilityPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Mode { get; init; } = VectorSidecarEligibilityModes.SidecarAwareEligibility;

    public int CandidateCount { get; init; }

    public int SidecarEntryCount { get; init; }

    public int ApprovedSidecarCount { get; init; }

    public int PendingReviewCount { get; init; }

    public int EffectiveMetadataChangedCount { get; init; }

    public int UnsafeSidecarBlockedCount { get; init; }

    public int ConflictSidecarBlockedCount { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public double RecallBeforeSidecar { get; init; }

    public double RecallAfterSidecar { get; init; }

    public int RiskBeforeSidecar { get; init; }

    public int RiskAfterSidecar { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<VectorLifecycleSidecarResolution> Resolutions { get; init; } =
        Array.Empty<VectorLifecycleSidecarResolution>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>review batch validation 报告。</summary>
public sealed class VectorLifecycleMetadataReviewBatchValidationReport
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CandidateCount { get; init; }

    public int RowCount { get; init; }

    public int DecisionCount { get; init; }

    public int ApprovalCount { get; init; }

    public int RejectCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int SupersedeCount { get; init; }

    public int ValidationErrorCount { get; init; }

    public int UnsafeDecisionCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingReviewerCount { get; init; }

    public int MissingReviewerReasonCount { get; init; }

    public bool LastWriteWins { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<VectorLifecycleMetadataReviewBatchValidationIssue> Issues { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewBatchValidationIssue>();

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}


/// <summary>review batch apply preview；只估算 sidecar 写入，不写真实 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatchApplyPreviewReport
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CandidateCount { get; init; }

    public int DecisionCount { get; init; }

    public int WouldWriteSidecarEntryCount { get; init; }

    public int UnsafeBlockedCount { get; init; }

    public int NormalContextApprovalCount { get; init; }

    public int AuditContextApprovalCount { get; init; }

    public int HistoricalContextApprovalCount { get; init; }

    public int DiagnosticsOnlyApprovalCount { get; init; }

    public int EffectiveMetadataChangedCount { get; init; }

    public bool RealSidecarWritten { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>review batch import smoke 报告；只验证导入、校验和 preview，不写真实 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatchImportSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool SmokePassed { get; init; }

    public string BatchId { get; init; } = string.Empty;

    public int ImportedRowCount { get; init; }

    public int ValidDecisionCount { get; init; }

    public int InvalidDecisionCount { get; init; }

    public int DuplicateDecisionBlockedCount { get; init; }

    public int UnknownDecisionBlockedCount { get; init; }

    public int MissingReviewerBlockedCount { get; init; }

    public int MissingReasonBlockedCount { get; init; }

    public int MissingEvidenceBlockedCount { get; init; }

    public int UnsafeNormalContextBlockedCount { get; init; }

    public int WouldWriteSidecarCount { get; init; }

    public int ActualSidecarWriteCount { get; init; }

    public bool SourceItemUnchanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string InitialStatus { get; init; } = string.Empty;

    public string ExportedStatus { get; init; } = string.Empty;

    public string ImportedStatus { get; init; } = string.Empty;

    public string ValidatedStatus { get; init; } = string.Empty;

    public string ValidationRecommendation { get; init; } = string.Empty;

    public string ApplyPreviewRecommendation { get; init; } = string.Empty;

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>lifecycle metadata review smoke 报告。</summary>
public sealed class VectorLifecycleMetadataReviewSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ApprovedSidecarWritten { get; init; }

    public bool RejectSkippedSidecar { get; init; }

    public bool NeedsEvidenceSkippedSidecar { get; init; }

    public bool SupersedeSkippedSidecar { get; init; }

    public bool SourceItemUnchanged { get; init; }

    public bool UnsafeNormalContextApprovalBlocked { get; init; }

    public bool CleanupPerformed { get; init; }

    public int SidecarEntryCount { get; init; }

    public string Recommendation { get; init; } = "ReviewSmokePassed";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>embedding provider 本地 smoke test 报告。</summary>
public sealed class EmbeddingProviderSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int ExpectedDimension { get; init; }

    public int ActualDimension { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ProviderEnabled { get; init; }

    public bool ModelPathExists { get; init; }

    public bool TokenizerPathExists { get; init; }

    public bool TokenizationWorks { get; init; }

    public bool OnnxInferenceWorks { get; init; }

    public bool DimensionMatchesConfig { get; init; }

    public bool NormalizationWorks { get; init; }

    public bool BatchEmbeddingWorks { get; init; }

    public bool Succeeded { get; init; }

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        [];

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
