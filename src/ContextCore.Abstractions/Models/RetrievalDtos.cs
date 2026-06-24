namespace ContextCore.Abstractions.Models
{
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
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> HistoricalDemotions { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> CurrentActivePromotions { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> VersionConflictFixes { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustHitDemotions { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustNotHitPromotions { get; init; } =
            [];

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

        public GraphExpansionShadowTrace GraphExpansionShadowTrace { get; init; } = new();

        public Dictionary<string, string> Metadata { get; init; } = new();

        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>图扩展 shadow trace 块；只记录观测结果，不改变正式检索输出。</summary>
    public sealed class GraphExpansionShadowTrace
    {
        public bool GraphExpansionShadowEnabled { get; init; }

        public IReadOnlyList<string> GraphExpansionProfiles { get; init; } = Array.Empty<string>();

        public IReadOnlyList<RelationExpansionPreviewRelation> AcceptedRelations { get; init; } =
            [];

        public IReadOnlyList<RelationExpansionPreviewRelation> BlockedRelations { get; init; } =
            [];

        public Dictionary<string, int> TargetSections { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public int RiskIfNormal { get; init; }

        public int RiskAfterRouting { get; init; }

        public int HistoricalAuditCount { get; init; }

        public int ConflictEvidenceCount { get; init; }

        public int WrongSectionRisk { get; init; }

        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>可导出的图扩展 shadow trace 记录。</summary>
    public sealed class GraphExpansionShadowTraceRecord
    {
        public string RetrievalId { get; init; } = string.Empty;

        public string WorkspaceId { get; init; } = string.Empty;

        public string CollectionId { get; init; } = string.Empty;

        public string Query { get; init; } = string.Empty;

        public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();

        public DateTimeOffset CreatedAt { get; init; }

        public IReadOnlyList<RelationExpansionPreviewRelation> AcceptedRelations { get; init; } =
            [];

        public IReadOnlyList<RelationExpansionPreviewRelation> BlockedRelations { get; init; } =
            [];

        public Dictionary<string, int> TargetSections { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public int RiskIfNormal { get; init; }

        public int RiskAfterRouting { get; init; }

        public int HistoricalAuditCount { get; init; }

        public int ConflictEvidenceCount { get; init; }

        public int WrongSectionRisk { get; init; }

        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class GraphExpansionShadowTraceRecommendations
    {
        public const string NeedsMoreRealTraces = nameof(NeedsMoreRealTraces);

        public const string ReadyForAuditShadowOnly = nameof(ReadyForAuditShadowOnly);

        public const string ReadyForConflictShadowOnly = nameof(ReadyForConflictShadowOnly);

        public const string ReadyForGuardedOptIn = nameof(ReadyForGuardedOptIn);

        public const string BlockedByRisk = nameof(BlockedByRisk);
    }

    /// <summary>图扩展 shadow trace 质量报告。</summary>
    public sealed class GraphExpansionShadowTraceQualityReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string WorkspaceId { get; init; } = string.Empty;

        public string CollectionId { get; init; } = string.Empty;

        public int TraceCount { get; init; }

        public int AcceptedRelationCount { get; init; }

        public int BlockedRelationCount { get; init; }

        public int AuditContextCount { get; init; }

        public int ConflictEvidenceCount { get; init; }

        public int RiskAfterRoutingCount { get; init; }

        public int WrongSectionRiskCount { get; init; }

        public int MustNotHitRiskCount { get; init; }

        public int LifecycleRiskCount { get; init; }

        public int MissingEvidenceCount { get; init; }

        public Dictionary<string, int> TopRelationTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> TopBlockedReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string Recommendation { get; init; } = GraphExpansionShadowTraceRecommendations.NeedsMoreRealTraces;

        public string PolicyVersion { get; init; } = string.Empty;
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
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> DeprecatedDemotions { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> VersionConflictFixes { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustHitDemotions { get; init; } =
            [];

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> MustNotHitPromotions { get; init; } =
            [];

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
            [];

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

    /// <summary>Candidate reranker shadow 采集配置；默认关闭，只做旁路观测。</summary>
    public sealed class CandidateRerankerShadowOptions
    {
        public bool Enabled { get; init; }

        public bool TraceCollectionEnabled { get; init; }

        public string ShadowRanker { get; init; } = "LifecycleAwareFeatureBaseline";

        public string ShadowProfile { get; init; } = CandidateRerankerShadowProfiles.BaselineLifecycleAware;

        public int MaxCandidatesPerTrace { get; init; } = 50;

        public int RecordTopK { get; init; } = 10;

        public bool RecordWouldChange { get; init; } = true;
    }

    public static class CandidateRerankerShadowProfiles
    {
        public const string BaselineLifecycleAware = "baseline-lifecycle-aware";
        public const string FormalPriorityAwareV1 = "formal-priority-aware-v1";
        public const string FormalPriorityAwareWithAbstainV1 = "formal-priority-aware-with-abstain-v1";
    }

    /// <summary>Candidate reranker shadow 的候选摘要；只用于报告，不改变正式排序。</summary>
    public sealed class CandidateRerankerShadowCandidateRef
    {
        public string CandidateId { get; init; } = string.Empty;

        public int Rank { get; init; }

        public double Score { get; init; }

        public bool Selected { get; init; }

        public bool IsMustHit { get; init; }

        public bool IsMustNotHit { get; init; }

        public string SectionName { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker shadow trace；formalOutputChanged 固定为 false。</summary>
    public sealed class CandidateRerankerShadowTrace
    {
        public string RequestId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public string QueryText { get; init; } = string.Empty;

        public int CandidateCount { get; init; }

        public IReadOnlyList<CandidateRerankerShadowCandidateRef> FormalTopCandidates { get; init; } =
            [];

        public IReadOnlyList<CandidateRerankerShadowCandidateRef> ShadowTopCandidates { get; init; } =
            [];

        public bool WouldChangeTop1 { get; init; }

        public bool WouldChangeTopK { get; init; }

        public int LifecycleRiskCount { get; init; }

        public int DeprecatedCandidateCount { get; init; }

        public int MustNotRiskCount { get; init; }

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> ScoreBreakdown { get; init; } =
            [];

        public IReadOnlyList<RankerCandidateEligibilityDecision> EligibilityDecisions { get; init; } =
            [];

        public bool FormalOutputChanged { get; init; }
    }

    /// <summary>Formal priority 的 shadow-only 特征；只用于离线对齐和报告。</summary>
    public sealed class CandidateRerankerFormalPriorityFeatureSet
    {
        public double LayerPriority { get; init; }

        public double SourcePriority { get; init; }

        public double CurrentTaskBoost { get; init; }

        public double ConstraintRelevance { get; init; }

        public double RelationEvidenceBoost { get; init; }

        public double StableMemoryBias { get; init; }

        public double WorkingMemoryBias { get; init; }

        public double CandidateMemoryPenalty { get; init; }

        public double FreshnessPriority { get; init; }

        public double PackagePolicyPriority { get; init; }

        public double LifecyclePriority { get; init; }

        public double Total { get; init; }

        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    }

    public static class CandidateRerankerShadowRecommendations
    {
        public const string NeedsMoreRealTraces = nameof(NeedsMoreRealTraces);
        public const string KeepFormalRanking = nameof(KeepFormalRanking);
        public const string ReadyForRankerShadow = nameof(ReadyForRankerShadow);
        public const string NeedsFeatureTuning = nameof(NeedsFeatureTuning);
        public const string BlockedByRisk = nameof(BlockedByRisk);
        public const string ReadyForGuardedOptIn = nameof(ReadyForGuardedOptIn);
    }

    public static class CandidateRerankerScoreContractStatuses
    {
        public const string Unknown = nameof(Unknown);
        public const string Passed = nameof(Passed);
        public const string NeedsAudit = nameof(NeedsAudit);
        public const string Failed = nameof(Failed);
    }

    public static class CandidateRerankerRegressionReasons
    {
        public const string ScoreDirectionMismatch = nameof(ScoreDirectionMismatch);
        public const string ScoreScaleMismatch = nameof(ScoreScaleMismatch);
        public const string MissingFeatureMetadata = nameof(MissingFeatureMetadata);
        public const string LifecyclePenaltyNotApplied = nameof(LifecyclePenaltyNotApplied);
        public const string DeprecatedPenaltyNotApplied = nameof(DeprecatedPenaltyNotApplied);
        public const string RiskCandidateAllowed = nameof(RiskCandidateAllowed);
        public const string PairwiseToListwiseMismatch = nameof(PairwiseToListwiseMismatch);
        public const string CandidateScopeMismatch = nameof(CandidateScopeMismatch);
        public const string ComparisonMetricMismatch = nameof(ComparisonMetricMismatch);
        public const string RankerFeatureTooWeak = nameof(RankerFeatureTooWeak);
        public const string RequiresFeatureTuning = nameof(RequiresFeatureTuning);
    }

    public static class CandidateRerankerCalibrationIssues
    {
        public const string PairwiseToListwiseMismatch = nameof(PairwiseToListwiseMismatch);
        public const string ScoreScaleTooFlat = nameof(ScoreScaleTooFlat);
        public const string ScoreScaleTooSharp = nameof(ScoreScaleTooSharp);
        public const string LowMarginAmbiguity = nameof(LowMarginAmbiguity);
        public const string DominantPenaltyOverpowersRelevance = nameof(DominantPenaltyOverpowersRelevance);
        public const string MissingIntentFeature = nameof(MissingIntentFeature);
        public const string MissingModeFeature = nameof(MissingModeFeature);
        public const string FormalRankingHasImplicitPriority = nameof(FormalRankingHasImplicitPriority);
        public const string RequiresFeatureCalibration = nameof(RequiresFeatureCalibration);
        public const string KeepFormalRanking = nameof(KeepFormalRanking);
    }

    public static class CandidateRerankerEligibilityStatuses
    {
        public const string Rankable = nameof(Rankable);
        public const string Blocked = nameof(Blocked);
        public const string AuditOnly = nameof(AuditOnly);
        public const string DiagnosticsOnly = nameof(DiagnosticsOnly);
        public const string MetadataIncomplete = nameof(MetadataIncomplete);
        public const string RiskCandidateAllowed = nameof(RiskCandidateAllowed);
    }

    public static class CandidateRerankerBlockedReasons
    {
        public const string MissingLifecycleMetadata = nameof(MissingLifecycleMetadata);
        public const string DeprecatedCandidateBlocked = nameof(DeprecatedCandidateBlocked);
        public const string HistoricalCandidateBlocked = nameof(HistoricalCandidateBlocked);
        public const string SupersededCandidateBlocked = nameof(SupersededCandidateBlocked);
        public const string MissingReplacementMetadata = nameof(MissingReplacementMetadata);
        public const string MissingReviewStatus = nameof(MissingReviewStatus);
        public const string MissingProvenance = nameof(MissingProvenance);
        public const string RiskCandidateBlocked = nameof(RiskCandidateBlocked);
        public const string IncompleteFeatureEnvelope = nameof(IncompleteFeatureEnvelope);
    }

    public static class CandidateRerankerEligibilityGuardStatuses
    {
        public const string Unknown = nameof(Unknown);
        public const string Passed = nameof(Passed);
        public const string Guarded = nameof(Guarded);
        public const string BlockedByRisk = nameof(BlockedByRisk);
    }

    /// <summary>Candidate reranker 使用的特征包络；只承载运行时元数据和诊断，不读 eval label。</summary>
    public sealed class CandidateFeatureEnvelope
    {
        public string CandidateId { get; init; } = string.Empty;

        public string ItemId { get; init; } = string.Empty;

        public string Layer { get; init; } = string.Empty;

        public string ItemKind { get; init; } = string.Empty;

        public string Lifecycle { get; init; } = string.Empty;

        public string ReviewStatus { get; init; } = string.Empty;

        public string SourceRef { get; init; } = string.Empty;

        public IReadOnlyList<string> Provenance { get; init; } = Array.Empty<string>();

        public string ReplacementState { get; init; } = string.Empty;

        public bool IsDeprecated { get; init; }

        public bool IsHistorical { get; init; }

        public bool IsSuperseded { get; init; }

        public bool HasActiveReplacement { get; init; }

        public bool ConstraintRelevance { get; init; }

        public bool RelationEvidence { get; init; }

        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

        public double FeatureCompleteness { get; init; }
    }

    /// <summary>Candidate reranker eligibility guard 的单候选判定。</summary>
    public sealed class RankerCandidateEligibilityDecision
    {
        public string CandidateId { get; init; } = string.Empty;

        public string Status { get; init; } = CandidateRerankerEligibilityStatuses.Rankable;

        public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

        public CandidateFeatureEnvelope Envelope { get; init; } = new();
    }

    /// <summary>Candidate feature completeness 单样本摘要。</summary>
    public sealed class CandidateRerankerFeatureCompletenessSample
    {
        public string SampleId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public int RawCandidateCount { get; init; }

        public int RankableCandidateCount { get; init; }

        public int BlockedCandidateCount { get; init; }

        public int AuditOnlyCandidateCount { get; init; }

        public int DiagnosticsOnlyCandidateCount { get; init; }

        public double FeatureCompletenessRate { get; init; }

        public int MissingFeatureMetadataCount { get; init; }

        public int RiskCandidateBlockedBeforeRerank { get; init; }

        public IReadOnlyList<RankerCandidateEligibilityDecision> Decisions { get; init; } =
            [];
    }

    /// <summary>Candidate feature completeness 与 eligibility guard 离线报告。</summary>
    public sealed class CandidateRerankerFeatureCompletenessReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string DatasetName { get; init; } = string.Empty;

        public int Samples { get; init; }

        public int RawCandidateCount { get; init; }

        public int RankableCandidateCount { get; init; }

        public int BlockedCandidateCount { get; init; }

        public int AuditOnlyCandidateCount { get; init; }

        public int DiagnosticsOnlyCandidateCount { get; init; }

        public double FeatureCompletenessRate { get; init; }

        public int MissingFeatureMetadataCount { get; init; }

        public int MissingLifecycleMetadataCount { get; init; }

        public int MissingReviewStatusCount { get; init; }

        public int MissingProvenanceCount { get; init; }

        public int MissingReplacementMetadataCount { get; init; }

        public int RiskCandidateBlockedBeforeRerank { get; init; }

        public Dictionary<string, int> BlockedReasonCounts { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string EligibilityGuardStatus { get; init; } = CandidateRerankerEligibilityGuardStatuses.Unknown;

        public string Recommendation { get; init; } = string.Empty;

        public IReadOnlyList<CandidateRerankerFeatureCompletenessSample> SampleResults { get; init; } =
            [];

        public string PolicyVersion { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker shadow eval 的单样本结果。</summary>
    public sealed class CandidateRerankerShadowEvalSample
    {
        public string ShadowProfile { get; init; } = CandidateRerankerShadowProfiles.BaselineLifecycleAware;

        public string SampleId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public int CandidateCount { get; init; }

        public int RawCandidateCount { get; init; }

        public int RankableCandidateCount { get; init; }

        public int BlockedCandidateCount { get; init; }

        public double FeatureCompletenessRate { get; init; }

        public int RiskCandidateInRawTopK { get; init; }

        public int RiskCandidateInShadowTopK { get; init; }

        public int RiskCandidateBlockedBeforeRerank { get; init; }

        public int MissingFeatureMetadataCount { get; init; }

        public string EligibilityGuardStatus { get; init; } = CandidateRerankerEligibilityGuardStatuses.Unknown;

        public string FormalTopCandidateId { get; init; } = string.Empty;

        public string ShadowTopCandidateId { get; init; } = string.Empty;

        public bool FormalTop1Correct { get; init; }

        public bool ShadowTop1Correct { get; init; }

        public double FormalMrr { get; init; }

        public double ShadowMrr { get; init; }

        public bool WouldChangeTop1 { get; init; }

        public bool WouldChangeTopK { get; init; }

        public bool WouldImprove { get; init; }

        public bool WouldRegress { get; init; }

        public bool WouldApply { get; init; }

        public bool Abstained { get; init; }

        public bool WouldImproveAfterAbstain { get; init; }

        public bool WouldRegressAfterAbstain { get; init; }

        public bool FormalPriorityRecovered { get; init; }

        public bool UnexplainedRegression { get; init; }

        public double Top1Margin { get; init; }

        public int LifecycleRiskCount { get; init; }

        public int DeprecatedRiskCount { get; init; }

        public int MustNotRiskCount { get; init; }

        public CandidateRerankerShadowTrace Trace { get; init; } = new();
    }

    /// <summary>Candidate reranker shadow eval 汇总；不改变正式输出。</summary>
    public sealed class CandidateRerankerShadowEvalReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string DatasetName { get; init; } = string.Empty;

        public string ShadowProfile { get; init; } = CandidateRerankerShadowProfiles.BaselineLifecycleAware;

        public int Samples { get; init; }

        public int CandidateCount { get; init; }

        public int RawCandidateCount { get; init; }

        public double FormalTop1Accuracy { get; init; }

        public double ShadowTop1Accuracy { get; init; }

        public double FormalMRR { get; init; }

        public double ShadowMRR { get; init; }

        public int WouldChangeTop1Count { get; init; }

        public int WouldImproveCount { get; init; }

        public int WouldRegressCount { get; init; }

        public int WouldApplyCount { get; init; }

        public int AbstainCount { get; init; }

        public int NetGainAfterAbstain { get; init; }

        public int FormalPriorityRecoveredCount { get; init; }

        public int UnexplainedRegressionCount { get; init; }

        public int LifecycleRiskCount { get; init; }

        public int DeprecatedRiskCount { get; init; }

        public int MustNotRiskCount { get; init; }

        public int NetGain { get; init; }

        public string ScoreContractStatus { get; init; } = CandidateRerankerScoreContractStatuses.Unknown;

        public int RankableCandidateCount { get; init; }

        public int BlockedCandidateCount { get; init; }

        public int RiskCandidateInShadowTopK { get; init; }

        public int RiskCandidateInRawTopK { get; init; }

        public int RiskCandidateBlockedBeforeRerank { get; init; }

        public int MissingFeatureMetadataCount { get; init; }

        public double FeatureCompletenessRate { get; init; }

        public string EligibilityGuardStatus { get; init; } = CandidateRerankerEligibilityGuardStatuses.Unknown;

        public Dictionary<string, int> RegressionReasonSummary { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string Recommendation { get; init; } = CandidateRerankerShadowRecommendations.KeepFormalRanking;

        public IReadOnlyList<CandidateRerankerShadowEvalSample> SampleResults { get; init; } =
            [];

        public string PolicyVersion { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker shadow 回归样本审计明细。</summary>
    public sealed class CandidateRerankerShadowFailureAuditRecord
    {
        public string SampleId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public string QueryText { get; init; } = string.Empty;

        public string FormalTop1 { get; init; } = string.Empty;

        public string ShadowTop1 { get; init; } = string.Empty;

        public IReadOnlyList<string> ExpectedMustHit { get; init; } = Array.Empty<string>();

        public bool FormalHit { get; init; }

        public bool ShadowHit { get; init; }

        public int FormalCandidateRank { get; init; }

        public int ShadowCandidateRank { get; init; }

        public int CandidateCount { get; init; }

        public int RiskCandidatesInShadowTopK { get; init; }

        public string ScoreDirection { get; init; } = string.Empty;

        public IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> ScoreBreakdown { get; init; } =
            [];

        public bool LifecycleMetadataPresent { get; init; }

        public bool ReplacementMetadataPresent { get; init; }

        public bool DeprecatedMetadataPresent { get; init; }

        public string EligibilityStatus { get; init; } = CandidateRerankerEligibilityStatuses.Rankable;

        public string WhyShadowPromoted { get; init; } = string.Empty;

        public string RegressionReason { get; init; } = CandidateRerankerRegressionReasons.RequiresFeatureTuning;

        public string RecommendedAction { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker shadow failure audit 报告；仅解释离线回归，不改变正式输出。</summary>
    public sealed class CandidateRerankerShadowFailureAuditReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string DatasetName { get; init; } = string.Empty;

        public int Samples { get; init; }

        public int RegressionCount { get; init; }

        public string ScoreContractStatus { get; init; } = CandidateRerankerScoreContractStatuses.Unknown;

        public int RankableCandidateCount { get; init; }

        public int BlockedCandidateCount { get; init; }

        public int RiskCandidateInShadowTopK { get; init; }

        public int MissingLifecycleMetadataCount { get; init; }

        public int MissingReplacementMetadataCount { get; init; }

        public int DeprecatedMetadataPresentCount { get; init; }

        public Dictionary<string, int> RegressionReasonSummary { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<CandidateRerankerShadowFailureAuditRecord> Regressions { get; init; } =
            Array.Empty<CandidateRerankerShadowFailureAuditRecord>();

        public string RecommendedNextAction { get; init; } = string.Empty;

        public string PolicyVersion { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker score 分布的单样本摘要；只用于离线校准报告。</summary>
    public sealed class CandidateRerankerScoreDistributionSample
    {
        public string SampleId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public int CandidateCount { get; init; }

        public double ScoreMean { get; init; }

        public double ScoreStdDev { get; init; }

        public double ScoreMin { get; init; }

        public double ScoreMax { get; init; }

        public double Top1Margin { get; init; }

        public bool LowMarginDecision { get; init; }

        public bool WouldImprove { get; init; }

        public bool WouldRegress { get; init; }

        public string DominantFeature { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker score distribution 离线报告；不改变正式排序。</summary>
    public sealed class CandidateRerankerScoreDistributionReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string DatasetName { get; init; } = string.Empty;

        public int Samples { get; init; }

        public int CandidateCount { get; init; }

        public double ScoreMean { get; init; }

        public double ScoreStdDev { get; init; }

        public double ScoreMin { get; init; }

        public double ScoreMax { get; init; }

        public double Top1MarginAverage { get; init; }

        public double Top1MarginForRegressions { get; init; }

        public double Top1MarginForImprovements { get; init; }

        public double ScoreOverlapMustHitVsNonHit { get; init; }

        public Dictionary<string, double> FeatureContributionByType { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int DominantFeatureCount { get; init; }

        public int LowMarginDecisionCount { get; init; }

        public double LowMarginThreshold { get; init; }

        public string Recommendation { get; init; } = CandidateRerankerShadowRecommendations.KeepFormalRanking;

        public IReadOnlyList<CandidateRerankerScoreDistributionSample> SampleResults { get; init; } =
            Array.Empty<CandidateRerankerScoreDistributionSample>();

        public string PolicyVersion { get; init; } = string.Empty;
    }

    /// <summary>正式排序隐含优先级观测；只解释 formal 与 shadow 的差异。</summary>
    public sealed class CandidateRerankerFormalPriorityComparison
    {
        public string LayerPriority { get; init; } = string.Empty;

        public string SourcePriority { get; init; } = string.Empty;

        public string ConstraintRelevance { get; init; } = string.Empty;

        public string CurrentTaskBoost { get; init; } = string.Empty;

        public string WorkingStableCandidateBias { get; init; } = string.Empty;

        public string Freshness { get; init; } = string.Empty;

        public string PackagePolicyPriority { get; init; } = string.Empty;

        public string RelationPriority { get; init; } = string.Empty;

        public bool HasMismatch { get; init; }

        public CandidateRerankerFormalPriorityFeatureSet FormalTopFeatures { get; init; } = new();

        public CandidateRerankerFormalPriorityFeatureSet ShadowTopFeatures { get; init; } = new();
    }

    /// <summary>Formal priority alignment 单样本记录；只用于 shadow-only listwise repair 报告。</summary>
    public sealed class CandidateRerankerFormalPriorityAlignmentSample
    {
        public string SampleId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public string BaselineShadowTop1 { get; init; } = string.Empty;

        public string FormalPriorityShadowTop1 { get; init; } = string.Empty;

        public string FormalTop1 { get; init; } = string.Empty;

        public bool BaselineRegressed { get; init; }

        public bool Recovered { get; init; }

        public bool Abstained { get; init; }

        public bool UnexplainedMismatch { get; init; }

        public double BaselineShadowMrr { get; init; }

        public double FormalPriorityShadowMrr { get; init; }

        public double FormalMrr { get; init; }

        public CandidateRerankerFormalPriorityFeatureSet RecoveredFeatureSet { get; init; } = new();

        public CandidateRerankerFormalPriorityComparison FormalPriorityComparison { get; init; } = new();

        public string RecommendedAction { get; init; } = string.Empty;
    }

    /// <summary>Formal priority alignment 离线报告；不改变正式 scorer 或 package 输出。</summary>
    public sealed class CandidateRerankerFormalPriorityAlignmentReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string DatasetName { get; init; } = string.Empty;

        public int Samples { get; init; }

        public int RegressionCount { get; init; }

        public int FormalPriorityMismatchCount { get; init; }

        public int RecoveredCount { get; init; }

        public int RecoveredByLayerPriority { get; init; }

        public int RecoveredBySourcePriority { get; init; }

        public int RecoveredByCurrentTaskBoost { get; init; }

        public int RecoveredByConstraintRelevance { get; init; }

        public int RecoveredByStableMemoryBias { get; init; }

        public int UnexplainedMismatchCount { get; init; }

        public int AbstainCount { get; init; }

        public int NetGainAfterAbstain { get; init; }

        public string Recommendation { get; init; } = CandidateRerankerShadowRecommendations.KeepFormalRanking;

        public IReadOnlyList<CandidateRerankerFormalPriorityAlignmentSample> SampleResults { get; init; } =
            [];

        public string PolicyVersion { get; init; } = string.Empty;
    }

    /// <summary>Candidate reranker listwise calibration 单样本记录。</summary>
    public sealed class CandidateRerankerListwiseCalibrationSample
    {
        public string SampleId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string Intent { get; init; } = string.Empty;

        public int CandidateCount { get; init; }

        public int MustHitCandidateCount { get; init; }

        public string FormalTop1 { get; init; } = string.Empty;

        public string ShadowTop1 { get; init; } = string.Empty;

        public int MustHitBestRankFormal { get; init; }

        public int MustHitBestRankShadow { get; init; }

        public double Top1Margin { get; init; }

        public double TopKOverlap { get; init; }

        public string RegressionReason { get; init; } = string.Empty;

        public string CalibrationIssue { get; init; } = CandidateRerankerCalibrationIssues.KeepFormalRanking;

        public string RecommendedAction { get; init; } = string.Empty;

        public CandidateRerankerFormalPriorityComparison FormalPriorityComparison { get; init; } = new();
    }

    /// <summary>Candidate reranker listwise calibration 离线报告；不改变正式输出。</summary>
    public sealed class CandidateRerankerListwiseCalibrationReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string DatasetName { get; init; } = string.Empty;

        public int Samples { get; init; }

        public int CandidateCount { get; init; }

        public int MustHitCandidateCount { get; init; }

        public int RegressionCount { get; init; }

        public int LowMarginDecisionCount { get; init; }

        public int FormalPriorityMismatchCount { get; init; }

        public double AverageTop1Margin { get; init; }

        public double AverageTopKOverlap { get; init; }

        public bool FormalOutputChanged { get; init; }

        public Dictionary<string, int> CalibrationIssueCounts { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> RegressionReasonSummary { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string Recommendation { get; init; } = CandidateRerankerShadowRecommendations.KeepFormalRanking;

        public IReadOnlyList<CandidateRerankerListwiseCalibrationSample> SampleResults { get; init; } =
            Array.Empty<CandidateRerankerListwiseCalibrationSample>();

        public string PolicyVersion { get; init; } = string.Empty;
    }

    /// <summary>Runtime candidate reranker shadow trace 质量报告。</summary>
    public sealed class CandidateRerankerShadowTraceQualityReport
    {
        public string OperationId { get; init; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; init; }

        public string WorkspaceId { get; init; } = string.Empty;

        public string CollectionId { get; init; } = string.Empty;

        public int TraceCount { get; init; }

        public int CandidateCount { get; init; }

        public int WouldChangeTop1Count { get; init; }

        public int WouldChangeTopKCount { get; init; }

        public int LifecycleRiskCount { get; init; }

        public int DeprecatedRiskCount { get; init; }

        public int MustNotRiskCount { get; init; }

        public int NetGain { get; init; }

        public string Recommendation { get; init; } = CandidateRerankerShadowRecommendations.NeedsMoreRealTraces;

        public IReadOnlyList<CandidateRerankerShadowTrace> Traces { get; init; } =
            Array.Empty<CandidateRerankerShadowTrace>();

        public string PolicyVersion { get; init; } = string.Empty;
    }
}

