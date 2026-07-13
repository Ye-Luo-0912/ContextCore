namespace ContextCore.Abstractions.Models;


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


/// <summary>架构清理计划推荐。</summary>
public static class ArchitectureCleanupPlanRecommendations
{
    public const string ReadyForCleanupPlan = nameof(ReadyForCleanupPlan);
    public const string BlockedByMissingV6FFreeze = nameof(BlockedByMissingV6FFreeze);
}


/// <summary>架构清理冻结推荐。</summary>
public static class ArchitectureCleanupFreezeRecommendations
{
    public const string CleanupFrozen = nameof(CleanupFrozen);
    public const string BlockedByMissingReports = nameof(BlockedByMissingReports);
    public const string BlockedByPlanNotPassed = nameof(BlockedByPlanNotPassed);
    public const string BlockedByDtoSplitFailed = nameof(BlockedByDtoSplitFailed);
    public const string BlockedByHygieneGateFailed = nameof(BlockedByHygieneGateFailed);
    public const string DeferredCleanupNotCompleted = nameof(DeferredCleanupNotCompleted);
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


/// <summary>Scoped runtime experiment dry-run observation mode；只允许 dry-run。</summary>
public static class ScopedRuntimeExperimentDryRunObservationModes
{
    public const string DryRun = nameof(DryRun);
}


/// <summary>Explicit scoped runtime experiment proposal mode；只允许 proposal。</summary>
public static class ExplicitScopedRuntimeExperimentProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
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


/// <summary>V4.13 activation preflight mode；只允许 preflight + dry-run route。</summary>
public static class ScopedRuntimeExperimentActivationPreflightModes
{
    public const string PreflightAndDryRunRoute = nameof(PreflightAndDryRunRoute);
}


/// <summary>V4.14 guarded scoped runtime experiment mode；仅允许 scoped shadow runtime experiment。</summary>
public static class GuardedScopedRuntimeExperimentModes
{
    public const string ShadowRuntimeExperiment = nameof(ShadowRuntimeExperiment);
}


/// <summary>V4.15 scoped runtime experiment observation window mode；仅允许 scoped shadow observation。</summary>
public static class ScopedRuntimeExperimentObservationWindowModes
{
    public const string ScopedShadowObservation = nameof(ScopedShadowObservation);
}


/// <summary>Scoped runtime experiment no-op harness mode。</summary>
public static class ScopedRuntimeExperimentNoOpHarnessModes
{
    public const string NoOp = nameof(NoOp);
}


/// <summary>Guarded scoped runtime experiment plan mode；V4.11 只允许计划模式。</summary>
public static class GuardedScopedRuntimeExperimentPlanModes
{
    public const string PlanOnly = nameof(PlanOnly);
}


/// <summary>Scoped formal preview opt-in mode。</summary>
public static class ScopedFormalPreviewOptInModes
{
    public const string Off = nameof(Off);
    public const string PreviewOnly = nameof(PreviewOnly);
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

