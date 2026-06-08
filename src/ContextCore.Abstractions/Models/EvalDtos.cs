using ContextCore.Abstractions;

namespace ContextCore.Abstractions.Models;

/// <summary>上下文评测样本，描述一次 query 的期望命中、排除、实体、约束和不确定性。</summary>
public sealed class ContextEvalSample
{
    /// <summary>样本唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>用户或系统发起的查询。</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>评测模式：ChatMode、NovelMode、AutomationMode、CodingMode 或 ProjectMode。</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>期望必须命中的上下文 ID、记忆 ID 或来源引用。</summary>
    public IReadOnlyList<string> MustHit { get; init; } = Array.Empty<string>();

    /// <summary>期望不得进入结果的上下文 ID、记忆 ID 或来源引用。</summary>
    public IReadOnlyList<string> MustNotHit { get; init; } = Array.Empty<string>();

    /// <summary>期望覆盖的作用域，如 workspace、collection、task、session。</summary>
    public IReadOnlyList<string> ExpectedScopes { get; init; } = Array.Empty<string>();

    /// <summary>期望识别或保留的实体。</summary>
    public IReadOnlyList<string> ExpectedEntities { get; init; } = Array.Empty<string>();

    /// <summary>期望注入的约束。</summary>
    public IReadOnlyList<string> ExpectedConstraints { get; init; } = Array.Empty<string>();

    /// <summary>期望报告的不确定性。</summary>
    public IReadOnlyList<string> ExpectedUncertainties { get; init; } = Array.Empty<string>();

    /// <summary>人工金标说明。</summary>
    public string GoldenNotes { get; init; } = string.Empty;

    /// <summary>附加元数据，用于记录语料来源、版本或标签。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>上下文评测样本加载结果。</summary>
public sealed class ContextEvalSampleLoadResult
{
    /// <summary>成功读取的样本。</summary>
    public IReadOnlyList<ContextEvalSample> Samples { get; init; } = Array.Empty<ContextEvalSample>();

    /// <summary>按 mode 聚合的样本数量。</summary>
    public Dictionary<string, int> ModeCounts { get; init; } = new();

    /// <summary>读取过的样本文件路径。</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

/// <summary>上下文评测静态语料库数据，供 InMemory 模式一键加载。</summary>
public sealed class ContextEvalCorpus
{
    public IReadOnlyList<ContextItem> Contexts { get; init; } = Array.Empty<ContextItem>();
    public IReadOnlyList<ContextMemoryItem> Memories { get; init; } = Array.Empty<ContextMemoryItem>();
    public IReadOnlyList<ContextRelation> Relations { get; init; } = Array.Empty<ContextRelation>();
    public IReadOnlyList<ContextConstraint> Constraints { get; init; } = Array.Empty<ContextConstraint>();

    /// <summary>
    /// 评测专用的约束缺口激活 fixture。加载时必须经过 ConstraintGap accept 与 CandidateConstraint activate 正式链路。
    /// </summary>
    public IReadOnlyList<ConstraintGapCandidate> ActivatedConstraintGaps { get; init; } = Array.Empty<ConstraintGapCandidate>();
}

/// <summary>单条样本的评测详细结果。</summary>
public sealed class ContextEvalResult
{
    public string SampleId { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    
    /// <summary>测评状态：Passed, PassedWithWarnings, Failed, InvalidSample</summary>
    public string Status { get; init; } = "Passed";

    // Retrieval Metrics
    public double RetrievalRecall3 { get; init; }
    public double RetrievalRecall5 { get; init; }
    public double RetrievalRecall10 { get; init; }

    /// <summary>所有 mustHit 中排名最高（物理位置最小）的那个的倒数排名。主评测指标。</summary>
    public double RetrievalMrrAnyMustHit { get; init; }

    /// <summary>第一个 mustHit（按样本顺序）在 selectedIds 中的倒数排名。传统 MRR 语义。</summary>
    public double PrimaryMustHitMrr { get; init; }

    /// <summary>向后兼容别名，等于 RetrievalMrrAnyMustHit。</summary>
    public double RetrievalMrr => RetrievalMrrAnyMustHit;

    public double RetrievalNoiseViolationRatio { get; init; }
    public int MustHitCount { get; init; }
    public int MustHitRecalledCount { get; init; }
    public int MustNotHitCount { get; init; }
    public int MustNotHitRecalledCount { get; init; }

    // Attention Shadow Metrics
    public double AttentionMrr { get; init; }
    public double AttentionRecall3 { get; init; }
    public double AttentionRecall5 { get; init; }
    public bool AttentionImproved { get; init; }
    public bool AttentionRegressed { get; init; }
    public bool AttentionWouldChangeSelectedSet { get; init; }
    public int MustNotHitPromotedCount { get; init; }
    public double AttentionSelectedSetChangeRatio { get; init; }
    public IReadOnlyList<ContextEvalAttentionProfileResult> AttentionProfiles { get; init; } = Array.Empty<ContextEvalAttentionProfileResult>();

    public AttentionRerankComparisonReport AttentionRerankComparison { get; init; } = new();

    // Package Metrics
    public double PackageTokenWasteRatio { get; init; }
    public double UnusedBudgetRatio { get; init; }
    public double MustHitTokenShare { get; init; }
    public bool PackageHasAllConstraints { get; init; }
    public bool PackageHasAllEntities { get; init; }
    public bool PackageHasAllUncertainties { get; init; }

    // Detail Counts
    public int AnchorsCount { get; init; }
    public int RawSearchTokensCount { get; init; }
    public int SemanticAnchorsCount { get; init; }
    public string RawSearchTokens { get; init; } = string.Empty;
    public string SemanticAnchors { get; init; } = string.Empty;
    
    public int CandidatesCount { get; init; }
    public int SelectedCount { get; init; }
    public int ExcludedCount { get; init; }
    public int TokenBudget { get; init; }
    public IReadOnlyList<string> SelectedIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedIds { get; init; } = Array.Empty<string>();
    public string PackageBuildTrace { get; init; } = string.Empty;
    
    public IReadOnlyList<string> MustHit { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MustNotHit { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedConstraints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedEntities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedUncertainties { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WarningReasons { get; init; } = Array.Empty<string>();

    public ContextEvalBudgetPressureBreakdown BudgetPressureBreakdown { get; init; } = new();

    public IReadOnlyList<ContextEvalItemDiagnostic> SelectedItemDiagnostics { get; init; } = Array.Empty<ContextEvalItemDiagnostic>();

    public IReadOnlyList<ContextEvalItemDiagnostic> DroppedItemDiagnostics { get; init; } = Array.Empty<ContextEvalItemDiagnostic>();

    public string ErrorMessage { get; init; } = string.Empty;
    public string GoldenNotes { get; init; } = string.Empty;
}

/// <summary>评测期的 token 预算压力拆解，仅用于诊断，不参与排序或打包决策。</summary>
public sealed class ContextEvalBudgetPressureBreakdown
{
    public int MandatoryTokens { get; init; }

    public int ConstraintsTokens { get; init; }

    public int WorkingTokens { get; init; }

    public int StableTokens { get; init; }

    public int EvidenceTokens { get; init; }

    public int DiagnosticsTokens { get; init; }

    public int HistoricalTokens { get; init; }

    public int DroppedMustHitTokens { get; init; }

    public int DroppedLowPriorityTokens { get; init; }
}

/// <summary>评测报告中的 selected/dropped item 诊断快照。</summary>
public sealed class ContextEvalItemDiagnostic
{
    public string ItemId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string SectionName { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public int Rank { get; init; }

    public bool IsMustHit { get; init; }

    public bool IsMustNotHit { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>Lifecycle-aware ranker shadow scoring options. Disabled by default outside explicit eval commands.</summary>
public sealed class LifecycleAwareRankerShadowOptions
{
    public bool Enabled { get; init; }

    public bool DebugEndpointEnabled { get; init; } = true;

    public bool TraceCollectionEnabled { get; init; }

    public int MaxCandidatesPerTrace { get; init; } = 50;

    public string Profile { get; init; } = "lifecycle-aware-v1";
}

/// <summary>Shadow score snapshot for one candidate. It is diagnostic-only and never mutates retrieval output.</summary>
public sealed class LifecycleAwareRankerShadowCandidateScore
{
    public string CandidateId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string SectionName { get; init; } = string.Empty;

    public bool Selected { get; init; }

    public bool IsMustHit { get; init; }

    public bool IsMustNotHit { get; init; }

    public int LegacyRank { get; init; }

    public int ShadowRank { get; init; }

    public int RankDelta { get; init; }

    public double LegacyScore { get; init; }

    public double LifecycleAwareScore { get; init; }

    public double ScoreDelta { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> DemotionReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> PromotionReasons { get; init; } = Array.Empty<string>();

    public LifecycleAwareFeatureSet LifecycleFeatures { get; init; } = new();
}

/// <summary>Trace block emitted by lifecycle-aware ranker shadow evaluation.</summary>
public sealed class LifecycleAwareRankerShadowTrace
{
    public bool RankerShadowEnabled { get; init; }

    public string RankerShadowProfile { get; init; } = string.Empty;

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> CandidateShadowScores { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> DeprecatedDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> VersionConflictFixes { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustHitDemotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();

    public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustNotHitPromotions { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowCandidateScore>();
}

/// <summary>Sample-level lifecycle-aware ranker shadow evaluation result.</summary>
public sealed class LifecycleAwareRankerShadowSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool FormalOutputChanged { get; init; }

    public bool SelectedSetChanged { get; init; }

    public IReadOnlyList<string> LegacySelectedIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowSelectedIds { get; init; } = Array.Empty<string>();

    public int DeprecatedNoiseDemotedCount { get; init; }

    public int VersionConflictFixedCount { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public int LifecycleViolationCount { get; init; }

    public double LegacyMRR { get; init; }

    public double ShadowPotentialMRR { get; init; }

    public double PotentialMRRDelta { get; init; }

    public double PotentialPairwiseWinRate { get; init; }

    public LifecycleAwareRankerShadowTrace Trace { get; init; } = new();
}

/// <summary>Lifecycle-aware ranker shadow report. Formal output and selected set must remain unchanged.</summary>
public sealed class LifecycleAwareRankerShadowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public string Profile { get; init; } = string.Empty;

    public int TotalSamples { get; init; }

    public bool IncludeSeedBatches { get; init; }

    public int FormalOutputChanged { get; init; }

    public int SelectedSetChanged { get; init; }

    public int DeprecatedNoiseDemotedCount { get; init; }

    public int VersionConflictFixedCount { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public int LifecycleViolationCount { get; init; }

    public double PotentialMRRDelta { get; init; }

    public double PotentialPairwiseWinRate { get; init; }

    public IReadOnlyList<LifecycleAwareRankerShadowSample> Samples { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowSample>();

    public string PolicyVersion { get; init; } = string.Empty;
}

/// <summary>上下文评测汇总报告。</summary>
public sealed class ContextEvalReport
{
    public int TotalSamples { get; init; }
    public int PassedSamples { get; init; }
    public int PassedWithWarningsSamples { get; init; }
    public int FailedSamples { get; init; }
    public int InvalidSamples { get; init; }
    public double PassRate { get; init; }

    // Averages
    public double AvgRetrievalRecall3 { get; init; }
    public double AvgRetrievalRecall5 { get; init; }
    public double AvgRetrievalRecall10 { get; init; }

    /// <summary>所有样本的 MRRAnyMustHit 平均值（主指标）</summary>
    public double AvgRetrievalMrrAnyMustHit { get; init; }

    /// <summary>所有样本的 PrimaryMustHitMrr 平均值</summary>
    public double AvgPrimaryMustHitMrr { get; init; }

    /// <summary>向后兼容别名，等于 AvgRetrievalMrrAnyMustHit。</summary>
    public double AvgRetrievalMrr => AvgRetrievalMrrAnyMustHit;

    public double AvgRetrievalNoiseViolationRatio { get; init; }

    public double AvgAttentionMrr { get; init; }
    public double AvgAttentionRecall3 { get; init; }
    public double AvgAttentionRecall5 { get; init; }
    public int AttentionImprovedSamples { get; init; }
    public int AttentionRegressedSamples { get; init; }
    public int MustNotHitPromotedCount { get; init; }
    public double SelectedSetChangeRatio { get; init; }

    public double AvgPackageWasteRatio { get; init; }
    public double AvgUnusedBudgetRatio { get; init; }
    public double AvgMustHitTokenShare { get; init; }
    public double PackageConstraintHitRate { get; init; }
    public double PackageEntityHitRate { get; init; }
    public double PackageUncertaintyHitRate { get; init; }

    // Average Counts
    public double AvgAnchorsCount { get; init; }
    public double AvgRawSearchTokensCount { get; init; }
    public double AvgSemanticAnchorsCount { get; init; }
    public double AvgCandidatesCount { get; init; }
    public double AvgSelectedCount { get; init; }
    public double AvgExcludedCount { get; init; }

    public Dictionary<string, int> WarningSources { get; init; } = new();

    /// <summary>按 Chat/Project/Novel/Automation/Coding 等场景聚合的质量指标。</summary>
    public IReadOnlyList<ContextEvalModeSummary> ModeSummaries { get; init; } = Array.Empty<ContextEvalModeSummary>();

    public IReadOnlyList<ContextEvalAttentionProfileSummary> AttentionProfileSummaries { get; init; } = Array.Empty<ContextEvalAttentionProfileSummary>();

    public ContextEvalAttentionDiagnostics AttentionDiagnostics { get; init; } = new();

    public IReadOnlyList<ContextEvalResult> Results { get; init; } = Array.Empty<ContextEvalResult>();
}

/// <summary>Extended eval 失败归因报告。</summary>
public sealed class ExtendedFailureTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int TotalSamples { get; init; }

    public int FailedSamples { get; init; }

    public Dictionary<string, int> CategoryCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> ModeCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ExtendedFailureFixPlanItem> FixPlan { get; init; } = Array.Empty<ExtendedFailureFixPlanItem>();

    public IReadOnlyList<ExtendedFailureTriageSample> Samples { get; init; } = Array.Empty<ExtendedFailureTriageSample>();
}

/// <summary>单个 failed 样本的 package quality triage 记录。</summary>
public sealed class ExtendedFailureTriageSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string FailedReason { get; init; } = string.Empty;

    public IReadOnlyList<string> FailureCategories { get; init; } = Array.Empty<string>();

    public int SelectedCount { get; init; }

    public int TokenBudget { get; init; }

    public bool BudgetPressure { get; init; }

    public ContextEvalBudgetPressureBreakdown BudgetPressureBreakdown { get; init; } = new();

    public IReadOnlyList<ExtendedFailureMustHitStatus> MustHitStatuses { get; init; } = Array.Empty<ExtendedFailureMustHitStatus>();

    public ExtendedFailureExpectationStatus ConstraintStatus { get; init; } = new();

    public ExtendedFailureExpectationStatus EntityStatus { get; init; } = new();

    public ExtendedFailureExpectationStatus UncertaintyStatus { get; init; } = new();

    public IReadOnlyList<string> UncertaintyFailureTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ContextEvalItemDiagnostic> TopDroppedImportantItems { get; init; } = Array.Empty<ContextEvalItemDiagnostic>();

    public string SuspectedRootCause { get; init; } = string.Empty;

    public string SuggestedFixType { get; init; } = string.Empty;
}

/// <summary>Failed sample 的修复计划条目。</summary>
public sealed class ExtendedFailureFixPlanItem
{
    public string SampleId { get; init; } = string.Empty;

    public string FailureType { get; init; } = string.Empty;

    public string SuspectedRootCause { get; init; } = string.Empty;

    public string FixType { get; init; } = string.Empty;

    public string ExpectedRegressionTest { get; init; } = string.Empty;
}

/// <summary>Failed 样本中 must-hit 的 selected/dropped/rank 状态。</summary>
public sealed class ExtendedFailureMustHitStatus
{
    public string ItemId { get; init; } = string.Empty;

    public bool Selected { get; init; }

    public bool Dropped { get; init; }

    public int SelectedRank { get; init; }

    public string DroppedReason { get; init; } = string.Empty;

    public int EstimatedTokens { get; init; }
}

/// <summary>Failed 样本中 constraint/entity/uncertainty 的满足状态。</summary>
public sealed class ExtendedFailureExpectationStatus
{
    public bool Satisfied { get; init; }

    public IReadOnlyList<string> Expected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
}

/// <summary>单个评测模式的聚合质量指标，用于让报告直接比较不同场景的上下文质量。</summary>
public sealed class ContextEvalModeSummary
{
    /// <summary>评测模式名称。</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>该模式下参与评测的样本总数。</summary>
    public int TotalSamples { get; init; }

    public int PassedSamples { get; init; }
    public int PassedWithWarningsSamples { get; init; }
    public int FailedSamples { get; init; }
    public int InvalidSamples { get; init; }

    /// <summary>该模式下成功样本比例，Passed 与 PassedWithWarnings 都计入成功。</summary>
    public double PassRate { get; init; }

    public double AvgRetrievalRecall3 { get; init; }
    public double AvgRetrievalRecall5 { get; init; }
    public double AvgRetrievalRecall10 { get; init; }
    public double AvgRetrievalMrrAnyMustHit { get; init; }
    public double AvgPrimaryMustHitMrr { get; init; }

    /// <summary>向后兼容别名，等于 AvgRetrievalMrrAnyMustHit。</summary>
    public double AvgRetrievalMrr => AvgRetrievalMrrAnyMustHit;

    public double AvgRetrievalNoiseViolationRatio { get; init; }
    public double AvgAttentionMrr { get; init; }
    public double AvgAttentionRecall3 { get; init; }
    public double AvgAttentionRecall5 { get; init; }
    public int AttentionImprovedSamples { get; init; }
    public int AttentionRegressedSamples { get; init; }
    public int MustNotHitPromotedCount { get; init; }
    public double SelectedSetChangeRatio { get; init; }
    public double AvgPackageWasteRatio { get; init; }
    public double AvgUnusedBudgetRatio { get; init; }
    public double AvgMustHitTokenShare { get; init; }
    public double PackageConstraintHitRate { get; init; }
    public double PackageEntityHitRate { get; init; }
    public double PackageUncertaintyHitRate { get; init; }
    public double AvgCandidatesCount { get; init; }
    public double AvgSelectedCount { get; init; }
    public double AvgExcludedCount { get; init; }

    /// <summary>该模式下的警告来源统计。</summary>
    public Dictionary<string, int> WarningSources { get; init; } = new();
}

/// <summary>单条样本在某个 attention profile 下的 shadow 指标。</summary>
public sealed class ContextEvalAttentionProfileResult
{
    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public double CurrentMrr { get; init; }

    public double AttentionMrr { get; init; }

    public double AttentionRecall3 { get; init; }

    public double AttentionRecall5 { get; init; }

    public bool Improved { get; init; }

    public bool Regressed { get; init; }

    public bool WouldChangeSelectedSet { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public int MustNotHitWouldBeSelectedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<ContextEvalAttentionCandidateDiagnostic> CandidateDiagnostics { get; init; } = Array.Empty<ContextEvalAttentionCandidateDiagnostic>();
}

/// <summary>某个 attention profile 的评测汇总。</summary>
public sealed class ContextEvalAttentionProfileSummary
{
    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public double AvgAttentionMrr { get; init; }

    public double AvgAttentionRecall3 { get; init; }

    public double AvgAttentionRecall5 { get; init; }

    public int ImprovedSamples { get; init; }

    public int RegressedSamples { get; init; }

    public int CurrentMrrOneRegressionCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<ContextEvalAttentionProfileCategorySummary> CategoryBreakdown { get; init; } = Array.Empty<ContextEvalAttentionProfileCategorySummary>();
}

/// <summary>某个 profile 在单个评测 mode/category 下的 shadow 汇总。</summary>
public sealed class ContextEvalAttentionProfileCategorySummary
{
    public string Category { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public double AvgAttentionMrr { get; init; }

    public double AvgAttentionRecall3 { get; init; }

    public double AvgAttentionRecall5 { get; init; }

    public int ImprovedSamples { get; init; }

    public int RegressedSamples { get; init; }

    public int CurrentMrrOneRegressionCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }
}

/// <summary>Attention profile 回归与风险诊断摘要。</summary>
public sealed class ContextEvalAttentionDiagnostics
{
    public IReadOnlyList<ContextEvalAttentionDiagnosticSample> TopRegressedSamples { get; init; } = Array.Empty<ContextEvalAttentionDiagnosticSample>();

    public IReadOnlyList<ContextEvalAttentionDiagnosticSample> MustHitDemotedSamples { get; init; } = Array.Empty<ContextEvalAttentionDiagnosticSample>();

    public IReadOnlyList<ContextEvalAttentionDiagnosticSample> MustNotHitPromotedSamples { get; init; } = Array.Empty<ContextEvalAttentionDiagnosticSample>();

    public IReadOnlyList<ContextEvalAttentionDiagnosticSample> SelectedSetChangedSamples { get; init; } = Array.Empty<ContextEvalAttentionDiagnosticSample>();
}

/// <summary>Attention profile 诊断中的单条样本记录。</summary>
public sealed class ContextEvalAttentionDiagnosticSample
{
    public string ProfileId { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public double CurrentMrr { get; init; }

    public double AttentionMrr { get; init; }

    public double MrrDelta { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<ContextEvalAttentionCandidateDiagnostic> CandidateBreakdown { get; init; } = Array.Empty<ContextEvalAttentionCandidateDiagnostic>();
}

/// <summary>Attention diagnostics 的候选级 breakdown。</summary>
public sealed class ContextEvalAttentionCandidateDiagnostic
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public int CurrentRank { get; init; }

    public int AttentionRank { get; init; }

    public int RankDelta { get; init; }

    public double CurrentScore { get; init; }

    public double AttentionScore { get; init; }

    public bool SelectedByCurrentPolicy { get; init; }

    public bool WouldBeSelectedByAttention { get; init; }

    public bool IsMustHit { get; init; }

    public bool IsMustNotHit { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RelationPaths { get; init; } = Array.Empty<string>();

    public string ScoreBreakdown { get; init; } = string.Empty;

    public Dictionary<string, double> AttentionScoreBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

/// <summary>Guarded attention rerank 实验的评测汇总报告。</summary>
public sealed class GuardedAttentionRerankEvalReport
{
    public string OperationId { get; init; } = string.Empty;

    public string Mode { get; init; } = "SelectedSetPreserving";

    public string ProfileId { get; init; } = "old-score-anchored-v1";

    public int TotalSamples { get; init; }

    public int AppliedSamples { get; init; }

    public int SkippedSamples { get; init; }

    public int BlockedSamples { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int OrderChanges { get; init; }

    public int SectionChanges { get; init; }

    public int MustHitRankDeltaCount { get; init; }

    public int MustNotHitRankDeltaCount { get; init; }

    public int SelectedSetChangeCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public Dictionary<string, int> BlockedReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> SkippedReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GuardedAttentionRerankEvalSample> Samples { get; init; } = Array.Empty<GuardedAttentionRerankEvalSample>();
}

/// <summary>Guarded attention rerank 实验的单样本汇总。</summary>
public sealed class GuardedAttentionRerankEvalSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public bool Applied { get; init; }

    public bool Skipped { get; init; }

    public bool Blocked { get; init; }

    public string SkippedReason { get; init; } = string.Empty;

    public string BlockedReason { get; init; } = string.Empty;

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int OrderChanges { get; init; }

    public int SectionChanges { get; init; }

    public int MustHitRankDeltaCount { get; init; }

    public int MustNotHitRankDeltaCount { get; init; }

    public int SelectedSetChangeCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<AttentionRerankItemChange> TopOrderChanges { get; init; } = Array.Empty<AttentionRerankItemChange>();
}

/// <summary>Selected order 质量指标。rank 越小表示越靠前。</summary>
public sealed class SelectedOrderQualityMetrics
{
    public double SelectedOrderMRR { get; init; }

    public double FirstMustHitSelectedRank { get; init; }

    public double MustHitAverageSelectedRank { get; init; }

    public double ConstraintAverageRank { get; init; }

    public double LifecycleRiskAverageRank { get; init; }

    public double AttentionOrderDelta { get; init; }

    public int MovedUpMustHitCount { get; init; }

    public int MovedDownMustHitCount { get; init; }
}

/// <summary>Selected order 质量闸门结果。</summary>
public sealed class SelectedOrderQualityGateResult
{
    public string Name { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public double Actual { get; init; }

    public double Threshold { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>Guarded attention rerank selected order 质量对比报告。</summary>
public sealed class GuardedAttentionOrderQualityReport
{
    public string OperationId { get; init; } = string.Empty;

    public string Mode { get; init; } = "SelectedSetPreserving";

    public string ProfileId { get; init; } = "old-score-anchored-v1";

    public int TotalSamples { get; init; }

    public int AppliedSamples { get; init; }

    public int SkippedSamples { get; init; }

    public int BlockedSamples { get; init; }

    public int SelectedSetDiffCount { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int HardConstraintMissingCount { get; init; }

    public SelectedOrderQualityMetrics Baseline { get; init; } = new();

    public SelectedOrderQualityMetrics Reranked { get; init; } = new();

    public SelectedOrderQualityMetrics Delta { get; init; } = new();

    public IReadOnlyList<SelectedOrderQualityGateResult> SafetyGates { get; init; } = Array.Empty<SelectedOrderQualityGateResult>();

    public IReadOnlyList<SelectedOrderQualityGateResult> SortingGates { get; init; } = Array.Empty<SelectedOrderQualityGateResult>();

    public IReadOnlyList<GuardedAttentionOrderQualitySample> Samples { get; init; } = Array.Empty<GuardedAttentionOrderQualitySample>();
}

/// <summary>单样本 selected order 质量对比。</summary>
public sealed class GuardedAttentionOrderQualitySample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public bool Applied { get; init; }

    public bool Skipped { get; init; }

    public bool Blocked { get; init; }

    public string SkippedReason { get; init; } = string.Empty;

    public string BlockedReason { get; init; } = string.Empty;

    public SelectedOrderQualityMetrics Baseline { get; init; } = new();

    public SelectedOrderQualityMetrics Reranked { get; init; } = new();

    public SelectedOrderQualityMetrics Delta { get; init; } = new();

    public int SelectedSetDiffCount { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int HardConstraintMissingCount { get; init; }

    public IReadOnlyList<AttentionRerankOrderItem> OldSelectedOrder { get; init; } = Array.Empty<AttentionRerankOrderItem>();

    public IReadOnlyList<AttentionRerankOrderItem> NewSelectedOrder { get; init; } = Array.Empty<AttentionRerankOrderItem>();

    public IReadOnlyList<AttentionRerankItemChange> MovedUpItems { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankItemChange> MovedDownItems { get; init; } = Array.Empty<AttentionRerankItemChange>();
}

/// <summary>Guarded attention profile sweep 的 selected-order 汇总报告。</summary>
public sealed class GuardedAttentionProfileSweepReport
{
    public string OperationId { get; init; } = string.Empty;

    public string Mode { get; init; } = "SelectedSetPreserving";

    public int TotalSamples { get; init; }

    public bool IncludeSeedBatches { get; init; }

    public IReadOnlyList<GuardedAttentionProfileSweepProfile> Profiles { get; init; } = Array.Empty<GuardedAttentionProfileSweepProfile>();
}

/// <summary>单个 guarded attention profile 的 sweep 指标。</summary>
public sealed class GuardedAttentionProfileSweepProfile
{
    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public Dictionary<string, double> Weights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalSamples { get; init; }

    public int AppliedSamples { get; init; }

    public int SkippedSamples { get; init; }

    public int BlockedSamples { get; init; }

    public int SelectedSetDiffCount { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int HardConstraintMissingCount { get; init; }

    public double SelectedOrderMRR { get; init; }

    public double FirstMustHitSelectedRank { get; init; }

    public double MustHitAverageSelectedRank { get; init; }

    public double ConstraintAverageRank { get; init; }

    public double LifecycleRiskAverageRank { get; init; }

    public double AttentionOrderDelta { get; init; }

    public int MovedUpMustHitCount { get; init; }

    public int MovedDownMustHitCount { get; init; }

    public bool SafetyGatePassed { get; init; }

    public bool SortingGatePassed { get; init; }
}
