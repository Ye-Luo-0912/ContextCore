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
    public Dictionary<string, string> PackageMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> PackageSectionNames { get; init; } = Array.Empty<string>();
    public Dictionary<string, IReadOnlyList<string>> PackageSectionItemRefs { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
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

