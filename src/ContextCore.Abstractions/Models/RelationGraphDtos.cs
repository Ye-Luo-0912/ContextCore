namespace ContextCore.Abstractions.Models;

/// <summary>关系类型定义，用于图谱基础层校验和只读展示。</summary>
public sealed class RelationTypeDefinition
{
    public string Type { get; init; } = string.Empty;

    public bool IsDirectional { get; init; } = true;

    public string? InverseType { get; init; }

    public double DefaultWeight { get; init; } = 0.5;

    public bool RequiresEvidence { get; init; }

    public bool AuditOnly { get; init; }

    public bool AllowsNormalExpansion { get; init; } = true;

    public IReadOnlyList<string> AllowedSourceKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedTargetKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>关系图谱诊断报告。</summary>
public sealed class RelationGraphDiagnosticsReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? ItemId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int RelationCount { get; init; }

    public int DiagnosticCount { get; init; }

    public IReadOnlyList<RelationGraphDiagnostic> Diagnostics { get; init; } = Array.Empty<RelationGraphDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单条关系图谱诊断。</summary>
public sealed class RelationGraphDiagnostic
{
    public string DiagnosticId { get; init; } = string.Empty;

    public string DiagnosticType { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? RelationId { get; init; }

    public string? RelationType { get; init; }

    public string? SourceId { get; init; }

    public string? TargetId { get; init; }

    public IReadOnlyList<string> RelatedRelationIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RelatedItemIds { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>关系证据引用，用于 relation explain 和离线诊断。</summary>
public sealed class RelationEvidence
{
    public string EvidenceId { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string? SourceOperationId { get; init; }

    public string? SourceItemId { get; init; }

    public string EvidenceText { get; init; } = string.Empty;

    public string EvidenceKind { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>关系解释中的端点条目摘要。</summary>
public sealed class RelationItemReference
{
    public string ItemId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Summary { get; init; } = string.Empty;

    public bool Missing { get; init; }
}

/// <summary>单条关系的只读解释结果。</summary>
public sealed class RelationExplainResponse
{
    public string RelationId { get; init; } = string.Empty;

    public ContextRelation? Relation { get; init; }

    public RelationTypeDefinition? TypeDefinition { get; init; }

    public RelationItemReference? SourceItem { get; init; }

    public RelationItemReference? TargetItem { get; init; }

    public ContextRelation? InverseRelation { get; init; }

    public IReadOnlyList<RelationEvidence> Evidence { get; init; } = Array.Empty<RelationEvidence>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public double Confidence { get; init; }

    public string ConfidenceReason { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public IReadOnlyList<RelationGraphDiagnostic> Diagnostics { get; init; } = Array.Empty<RelationGraphDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>关系 review / lifecycle 人工操作请求。</summary>
public sealed class RelationReviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>关系 review / lifecycle 人工操作结果。</summary>
public sealed class RelationReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string FromLifecycle { get; init; } = string.Empty;

    public string ToLifecycle { get; init; } = string.Empty;

    public string FromReviewStatus { get; init; } = string.Empty;

    public string ToReviewStatus { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public ContextRelation Relation { get; init; } = new();

    public RelationReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>关系 review / lifecycle 人工操作审计记录。</summary>
public sealed class RelationReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string FromLifecycle { get; init; } = string.Empty;

    public string ToLifecycle { get; init; } = string.Empty;

    public string FromReviewStatus { get; init; } = string.Empty;

    public string ToReviewStatus { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public static class RelationReviewActions
{
    public const string Review = nameof(Review);

    public const string Reject = nameof(Reject);

    public const string Deprecate = nameof(Deprecate);

    public const string MarkNeedsEvidence = nameof(MarkNeedsEvidence);
}

public static class RelationReviewStatuses
{
    public const string Reviewed = nameof(Reviewed);

    public const string Rejected = nameof(Rejected);

    public const string NeedsEvidence = nameof(NeedsEvidence);
}

/// <summary>关系扩展治理 profile；仅用于 preview / shadow，不改变正式扩展路径。</summary>
public sealed class RelationExpansionProfile
{
    public string ProfileId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public int MaxDepth { get; init; } = 1;

    public int MaxFanout { get; init; } = 8;

    public IReadOnlyList<string> AllowedRelationTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedRelationTypes { get; init; } = Array.Empty<string>();

    public double MinConfidence { get; init; } = 0.5;

    public bool AllowCandidateRelations { get; init; }

    public bool AllowDeprecatedRelations { get; init; }

    public bool AllowRejectedRelations { get; init; }

    public bool RequireEvidence { get; init; } = true;

    public IReadOnlyList<string> AuditOnlyTypes { get; init; } = Array.Empty<string>();

    public Dictionary<string, double> WeightByRelationType { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string LifecyclePolicy { get; init; } = string.Empty;

    public IReadOnlyList<RelationTraversalPolicy> TraversalPolicies { get; init; } = Array.Empty<RelationTraversalPolicy>();
}

/// <summary>图扩展 shadow trace 采集选项；默认关闭且不影响正式输出。</summary>
public sealed class GraphExpansionShadowOptions
{
    public bool Enabled { get; init; }

    public bool TraceCollectionEnabled { get; init; }

    public IReadOnlyList<string> Profiles { get; init; } = ["audit-v1", "conflict-v1"];

    public int MaxRelationsPerTrace { get; init; } = 50;
}

/// <summary>图扩展 guarded apply 选项；默认关闭，且不允许写入 normal_context。</summary>
public sealed class GraphExpansionApplyOptions
{
    public const string OffMode = "Off";

    public const string ShadowMode = "Shadow";

    public const string ApplyGuardedMode = "ApplyGuarded";

    public const string ProfileScopedApplyMode = "ProfileScoped";

    public string Mode { get; init; } = OffMode;

    public string ApplyMode { get; init; } = ProfileScopedApplyMode;

    public IReadOnlyList<string> OptInProfiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedTargetSections { get; init; } =
    [
        GraphExpansionTargetSection.AuditContext,
        GraphExpansionTargetSection.ConflictEvidence,
        GraphExpansionTargetSection.HistoricalContext,
        GraphExpansionTargetSection.DiagnosticsOnly
    ];

    public bool DisallowNormalContextInjection { get; init; } = true;

    public bool FallbackOnRisk { get; init; } = true;

    public int MaxAddedItemsPerPackage { get; init; } = 20;

    public bool EmitComparisonTrace { get; init; } = true;
}

/// <summary>图扩展 guarded apply 的风险计数，任一硬风险出现时必须回退。</summary>
public sealed class GraphExpansionApplyRiskChecks
{
    public int RiskAfterRoutingCount { get; init; }

    public int WrongSectionRiskCount { get; init; }

    public int MustNotHitRiskCount { get; init; }

    public int LifecycleRiskCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public bool HasRisk =>
        RiskAfterRoutingCount > 0
        || WrongSectionRiskCount > 0
        || MustNotHitRiskCount > 0
        || LifecycleRiskCount > 0
        || MissingEvidenceCount > 0;
}

/// <summary>图扩展 guarded apply 计划追加到辅助 section 的单个条目。</summary>
public sealed class GraphExpansionSectionContributionItem
{
    public string ItemId { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string SectionReason { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ItemRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>图扩展 guarded apply 对 package 的辅助 section 贡献；不会进入 normal selected set。</summary>
public sealed class GraphExpansionSectionContribution
{
    public string Mode { get; init; } = GraphExpansionApplyOptions.OffMode;

    public bool Applied { get; init; }

    public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<GraphExpansionSectionContributionItem> AddedItems { get; init; } =
        Array.Empty<GraphExpansionSectionContributionItem>();

    public IReadOnlyList<string> TargetSections { get; init; } = Array.Empty<string>();

    public bool FallbackUsed { get; init; }

    public string FallbackReason { get; init; } = string.Empty;

    public GraphExpansionApplyRiskChecks RiskChecks { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>关系遍历方向与目标生命周期治理策略；仅用于 preview / shadow。</summary>
public sealed class RelationTraversalPolicy
{
    public string RelationType { get; init; } = string.Empty;

    public IReadOnlyList<string> AllowedDirections { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedTargetLifecycle { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedTargetLifecycle { get; init; } = Array.Empty<string>();

    public bool AllowHistoricalTarget { get; init; }

    public string TargetSection { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

/// <summary>关系扩展 preview 请求；不执行正式 retrieval。</summary>
public sealed class RelationExpansionPreviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string ItemId { get; init; } = string.Empty;

    public string ProfileId { get; init; } = "normal-v1";
}

/// <summary>单条 relation expansion preview 结果。</summary>
public sealed class RelationExpansionPreviewRelation
{
    public string RelationId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public string TraversalDirection { get; init; } = string.Empty;

    public int Depth { get; init; }

    public double Confidence { get; init; }

    public double Weight { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string TargetLifecycle { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string SectionReason { get; init; } = string.Empty;

    public bool RiskIfNormalSelected { get; init; }

    public bool RiskAfterSectionRouting { get; init; }

    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>关系扩展 preview 响应。</summary>
public sealed class RelationExpansionPreviewResponse
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string ItemId { get; init; } = string.Empty;

    public RelationExpansionProfile Profile { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }

    public int AcceptedCount { get; init; }

    public int BlockedCount { get; init; }

    public IReadOnlyList<RelationExpansionPreviewRelation> AcceptedRelations { get; init; } = Array.Empty<RelationExpansionPreviewRelation>();

    public IReadOnlyList<RelationExpansionPreviewRelation> BlockedRelations { get; init; } = Array.Empty<RelationExpansionPreviewRelation>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>关系扩展 profile validator 单条结果。</summary>
public sealed class RelationExpansionPolicyValidationResult
{
    public bool Accepted { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string TraversalDirection { get; init; } = string.Empty;

    public string TargetLifecycle { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string SectionReason { get; init; } = string.Empty;

    public bool RiskIfNormalSelected { get; init; }

    public bool RiskAfterSectionRouting { get; init; }
}

/// <summary>关系扩展 profile shadow report。</summary>
public sealed class RelationExpansionProfileShadowReport
{
    public DateTimeOffset CreatedAt { get; init; }

    public int ProfileCount { get; init; }

    public int SampleCount { get; init; }

    public int AcceptedRelationCount { get; init; }

    public int BlockedRelationCount { get; init; }

    public IReadOnlyList<RelationExpansionProfileShadowProfileSummary> Profiles { get; init; } = Array.Empty<RelationExpansionProfileShadowProfileSummary>();

    public IReadOnlyList<RelationExpansionProfileShadowSample> Samples { get; init; } = Array.Empty<RelationExpansionProfileShadowSample>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class RelationExpansionProfileShadowProfileSummary
{
    public string ProfileId { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int AcceptedRelationCount { get; init; }

    public int BlockedRelationCount { get; init; }

    public Dictionary<string, int> BlockReasonCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int BlockedByBackwardReplacementTraversal { get; init; }

    public int BlockedByDeprecatedTarget { get; init; }

    public int BlockedByHistoricalTarget { get; init; }

    public int AllowedTowardLatest { get; init; }

    public int BlockedTowardHistorical { get; init; }

    public int HistoricalAllowedOnlyInAudit { get; init; }

    public int AcceptedToNormalContext { get; init; }

    public int AcceptedToHistoricalContext { get; init; }

    public int AcceptedToAuditContext { get; init; }

    public int AcceptedToConflictEvidence { get; init; }

    public int AcceptedToDiagnosticsOnly { get; init; }

    public int RiskIfNormalSelected { get; init; }

    public int RiskAfterSectionRouting { get; init; }

    public int HistoricalAuditExpansion { get; init; }

    public int ConflictEvidenceExpansion { get; init; }

    public int WrongSectionRisk { get; init; }
}

public sealed class RelationExpansionProfileShadowSample
{
    public string ItemId { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int AcceptedCount { get; init; }

    public int BlockedCount { get; init; }

    public IReadOnlyList<string> TopBlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int BlockedByBackwardReplacementTraversal { get; init; }

    public int BlockedByDeprecatedTarget { get; init; }

    public int BlockedByHistoricalTarget { get; init; }

    public int AllowedTowardLatest { get; init; }

    public int BlockedTowardHistorical { get; init; }

    public int HistoricalAllowedOnlyInAudit { get; init; }

    public int AcceptedToNormalContext { get; init; }

    public int AcceptedToHistoricalContext { get; init; }

    public int AcceptedToAuditContext { get; init; }

    public int AcceptedToConflictEvidence { get; init; }

    public int AcceptedToDiagnosticsOnly { get; init; }

    public int RiskIfNormalSelected { get; init; }

    public int RiskAfterSectionRouting { get; init; }

    public int HistoricalAuditExpansion { get; init; }

    public int ConflictEvidenceExpansion { get; init; }

    public int WrongSectionRisk { get; init; }
}

/// <summary>评测关系语料卫生报告，用于 legacy type 标准化和元数据回填准备度检查。</summary>
public sealed class RelationCorpusHygieneReport
{
    public DateTimeOffset CreatedAt { get; init; }

    public string ContextsRootPath { get; init; } = string.Empty;

    public int CorpusFileCount { get; init; }

    public int RelationCount { get; init; }

    public Dictionary<string, int> UnknownRelationTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RelationCorpusLegacyTypeSummary> LegacyRelationTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RelationCorpusHygieneFinding> MissingEvidenceRelations { get; init; } = Array.Empty<RelationCorpusHygieneFinding>();

    public IReadOnlyList<RelationCorpusHygieneFinding> MissingConfidenceRelations { get; init; } = Array.Empty<RelationCorpusHygieneFinding>();

    public IReadOnlyList<RelationCorpusHygieneFinding> MissingLifecycleRelations { get; init; } = Array.Empty<RelationCorpusHygieneFinding>();

    public IReadOnlyList<RelationCorpusHygieneFinding> MissingReviewStatusRelations { get; init; } = Array.Empty<RelationCorpusHygieneFinding>();

    public IReadOnlyList<RelationCorpusMigrationCandidate> MigrationCandidates { get; init; } = Array.Empty<RelationCorpusMigrationCandidate>();

    public IReadOnlyList<RelationCorpusBackfillCandidate> BackfillCandidates { get; init; } = Array.Empty<RelationCorpusBackfillCandidate>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class RelationCorpusLegacyTypeSummary
{
    public string LegacyType { get; init; } = string.Empty;

    public string NormalizedType { get; init; } = string.Empty;

    public int Count { get; init; }
}

public sealed class RelationCorpusHygieneFinding
{
    public string Category { get; init; } = string.Empty;

    public string CorpusFile { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public string NormalizedType { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;
}

public sealed class RelationCorpusMigrationCandidate
{
    public string Category { get; init; } = string.Empty;

    public string CorpusFile { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public string LegacyType { get; init; } = string.Empty;

    public string NormalizedType { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;
}

public sealed class RelationCorpusBackfillCandidate
{
    public string Category { get; init; } = string.Empty;

    public string CorpusFile { get; init; } = string.Empty;

    public string RelationId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public string NormalizedType { get; init; } = string.Empty;

    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();

    public bool CanBackfillEvidence { get; init; }

    public string BackfillPolicy { get; init; } = string.Empty;

    public string Suggestion { get; init; } = string.Empty;
}

/// <summary>评测样本上的 relation expansion profile shadow 报告；不改变 retrieval/package 输出。</summary>
public sealed class RelationExpansionShadowEvalReport
{
    public DateTimeOffset CreatedAt { get; init; }

    public bool IncludeSeedBatches { get; init; }

    public int TotalEvalSamples { get; init; }

    public int SampleCount { get; init; }

    public int ProfileCount { get; init; }

    public int FormalOutputChanged { get; init; }

    public int SelectedSetChanged { get; init; }

    public IReadOnlyList<RelationExpansionShadowProfileSummary> Profiles { get; init; } = Array.Empty<RelationExpansionShadowProfileSummary>();

    public IReadOnlyList<RelationExpansionShadowSample> Samples { get; init; } = Array.Empty<RelationExpansionShadowSample>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class RelationExpansionShadowProfileSummary
{
    public string ProfileId { get; init; } = string.Empty;

    public int Samples { get; init; }

    public int AcceptedRelations { get; init; }

    public int BlockedRelations { get; init; }

    public int WouldAddCandidates { get; init; }

    public int MustHitGain { get; init; }

    public int MustNotHitRisk { get; init; }

    public int LifecycleRisk { get; init; }

    public int BlockedByType { get; init; }

    public int BlockedByLifecycle { get; init; }

    public int BlockedByConfidence { get; init; }

    public int BlockedByMissingEvidence { get; init; }

    public int FanoutTrimmed { get; init; }

    public int DepthTrimmed { get; init; }

    public int BlockedByBackwardReplacementTraversal { get; init; }

    public int BlockedByDeprecatedTarget { get; init; }

    public int BlockedByHistoricalTarget { get; init; }

    public int AllowedTowardLatest { get; init; }

    public int BlockedTowardHistorical { get; init; }

    public int HistoricalAllowedOnlyInAudit { get; init; }

    public int AcceptedToNormalContext { get; init; }

    public int AcceptedToHistoricalContext { get; init; }

    public int AcceptedToAuditContext { get; init; }

    public int AcceptedToConflictEvidence { get; init; }

    public int AcceptedToDiagnosticsOnly { get; init; }

    public int RiskIfNormalSelected { get; init; }

    public int RiskAfterSectionRouting { get; init; }

    public int HistoricalAuditExpansion { get; init; }

    public int ConflictEvidenceExpansion { get; init; }

    public int WrongSectionRisk { get; init; }

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RelationExpansionShadowSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public IReadOnlyList<string> SeedItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<RelationExpansionPreviewRelation> ExpandedRelations { get; init; } = Array.Empty<RelationExpansionPreviewRelation>();

    public IReadOnlyList<RelationExpansionPreviewRelation> AcceptedRelations { get; init; } = Array.Empty<RelationExpansionPreviewRelation>();

    public IReadOnlyList<RelationExpansionPreviewRelation> BlockedRelations { get; init; } = Array.Empty<RelationExpansionPreviewRelation>();

    public IReadOnlyList<string> WouldAddCandidates { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WouldAddMustHit { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WouldAddMustNotHit { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WouldAddLifecycleRisk { get; init; } = Array.Empty<string>();

    public int RiskIfNormalSelected { get; init; }

    public int RiskAfterSectionRouting { get; init; }

    public int HistoricalAuditExpansion { get; init; }

    public int ConflictEvidenceExpansion { get; init; }

    public int WrongSectionRisk { get; init; }

    public Dictionary<string, int> BlockedReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int FanoutTrimmed { get; init; }

    public int DepthTrimmed { get; init; }

    public string Recommendation { get; init; } = string.Empty;
}

public static class RelationExpansionValidationReasons
{
    public const string UnknownRelationType = nameof(UnknownRelationType);

    public const string BlockedRelationType = nameof(BlockedRelationType);

    public const string RelationTypeNotAllowed = nameof(RelationTypeNotAllowed);

    public const string ConfidenceTooLow = nameof(ConfidenceTooLow);

    public const string MissingEvidence = nameof(MissingEvidence);

    public const string InvalidLifecycle = nameof(InvalidLifecycle);

    public const string AuditOnlyRelationInNormalProfile = nameof(AuditOnlyRelationInNormalProfile);

    public const string FanoutExceeded = nameof(FanoutExceeded);

    public const string DepthExceeded = nameof(DepthExceeded);

    public const string BackwardReplacementTraversalBlocked = nameof(BackwardReplacementTraversalBlocked);

    public const string DeprecatedTargetBlocked = nameof(DeprecatedTargetBlocked);

    public const string HistoricalTargetBlocked = nameof(HistoricalTargetBlocked);

    public const string AuditOnlyHistoricalTraversal = nameof(AuditOnlyHistoricalTraversal);

    public const string ReplacementTargetInactive = nameof(ReplacementTargetInactive);

    public const string ReplacementTargetRejected = nameof(ReplacementTargetRejected);

    public const string ReplacementTargetMissing = nameof(ReplacementTargetMissing);

    public const string HistoricalAllowedOnlyInAudit = nameof(HistoricalAllowedOnlyInAudit);

    public const string BlockedByWrongSectionRisk = nameof(BlockedByWrongSectionRisk);
}

public static class RelationTraversalDirections
{
    public const string Any = nameof(Any);

    public const string Both = nameof(Both);

    public const string TowardLatest = nameof(TowardLatest);

    public const string TowardHistorical = nameof(TowardHistorical);
}

public static class GraphExpansionTargetSection
{
    public const string NormalContext = "normal_context";

    public const string WorkingContext = "working_context";

    public const string StableContext = "stable_context";

    public const string HistoricalContext = "historical_context";

    public const string AuditContext = "audit_context";

    public const string ConflictEvidence = "conflict_evidence";

    public const string DiagnosticsOnly = "diagnostics_only";

    public const string Excluded = "excluded";
}

public static class RelationExpansionTargetSections
{
    public const string Normal = GraphExpansionTargetSection.NormalContext;

    public const string Constraints = "constraints";

    public const string AuditHistorical = GraphExpansionTargetSection.AuditContext;

    public const string HistoricalContext = GraphExpansionTargetSection.HistoricalContext;

    public const string AuditContext = GraphExpansionTargetSection.AuditContext;

    public const string ConflictEvidence = GraphExpansionTargetSection.ConflictEvidence;

    public const string DiagnosticsOnly = GraphExpansionTargetSection.DiagnosticsOnly;

    public const string Excluded = GraphExpansionTargetSection.Excluded;
}

public static class RelationExpansionShadowRecommendations
{
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);

    public const string ReadyForShadowInRetrieval = nameof(ReadyForShadowInRetrieval);

    public const string NeedsPolicyTuning = nameof(NeedsPolicyTuning);

    public const string BlockedByRisk = nameof(BlockedByRisk);

    public const string NeedsMoreRelations = nameof(NeedsMoreRelations);

    public const string ReadyForAuditShadow = nameof(ReadyForAuditShadow);

    public const string ReadyForConflictShadow = nameof(ReadyForConflictShadow);

    public const string ReadyForSectionAwareShadow = nameof(ReadyForSectionAwareShadow);

    public const string BlockedByWrongSectionRisk = nameof(BlockedByWrongSectionRisk);
}

/// <summary>graph expansion guarded opt-in 对比报告中的 warning 分类。</summary>
public static class GraphExpansionComparisonWarningKind
{
    public const string AuxiliaryGraphSectionAdded = nameof(AuxiliaryGraphSectionAdded);

    public const string ExpectedAuditContextAdded = nameof(ExpectedAuditContextAdded);

    public const string ExpectedConflictEvidenceAdded = nameof(ExpectedConflictEvidenceAdded);

    public const string GraphContributionDeduplicated = nameof(GraphContributionDeduplicated);

    public const string UnexpectedPackageWarningDelta = nameof(UnexpectedPackageWarningDelta);

    public const string NormalSelectedSetChanged = nameof(NormalSelectedSetChanged);

    public const string DisallowedNormalContextInjection = nameof(DisallowedNormalContextInjection);

    public const string RiskFallbackTriggered = nameof(RiskFallbackTriggered);

    public const string MissingEvidenceDetected = nameof(MissingEvidenceDetected);

    public const string LifecycleRiskDetected = nameof(LifecycleRiskDetected);

    public const string WrongSectionRiskDetected = nameof(WrongSectionRiskDetected);
}

/// <summary>graph expansion guarded opt-in 冻结闸门状态。</summary>
public static class GraphExpansionGuardStatus
{
    public const string Passed = nameof(Passed);

    public const string Failed = nameof(Failed);
}

/// <summary>graph expansion guarded opt-in 与 baseline package 的对比报告。</summary>
public sealed class GraphExpansionOptInComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string Scope { get; init; } = string.Empty;

    public int TotalSamples { get; init; }

    public int NormalSelectedSetChanged { get; init; }

    public int AuxiliaryGraphSectionChanged { get; init; }

    public int GraphExpansionAppliedCount { get; init; }

    public int AddedAuditContextItems { get; init; }

    public int AddedConflictEvidenceItems { get; init; }

    public int RiskAfterRoutingCount { get; init; }

    public int WrongSectionRiskCount { get; init; }

    public int MustNotHitRiskCount { get; init; }

    public int LifecycleRiskCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int FallbackCount { get; init; }

    public double PassRateDelta { get; init; }

    public int WarningDelta { get; init; }

    public int ExpectedWarningDelta { get; init; }

    public int UnexpectedWarningDelta { get; init; }

    public IReadOnlyDictionary<string, int> WarningDeltaByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int DisallowedNormalContextInjection { get; init; }

    public string GuardStatus { get; init; } = GraphExpansionGuardStatus.Passed;

    public IReadOnlyList<GraphExpansionOptInComparisonSample> Samples { get; init; } =
        Array.Empty<GraphExpansionOptInComparisonSample>();
}

/// <summary>单个 eval 样本的 graph expansion guarded opt-in 对比结果。</summary>
public sealed class GraphExpansionOptInComparisonSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool NormalSelectedSetChanged { get; init; }

    public bool AuxiliaryGraphSectionChanged { get; init; }

    public bool GraphExpansionApplied { get; init; }

    public string GraphExpansionMode { get; init; } = string.Empty;

    public bool GraphExpansionFallbackUsed { get; init; }

    public string GraphExpansionFallbackReason { get; init; } = string.Empty;

    public string GraphExpansionWarnings { get; init; } = string.Empty;

    public IReadOnlyList<string> BaselineSelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ApplySelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedGraphItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TargetSections { get; init; } = Array.Empty<string>();

    public int AddedAuditContextItems { get; init; }

    public int AddedConflictEvidenceItems { get; init; }

    public GraphExpansionApplyRiskChecks RiskChecks { get; init; } = new();

    public int WarningDelta { get; init; }

    public int ExpectedWarningDelta { get; init; }

    public int UnexpectedWarningDelta { get; init; }

    public IReadOnlyList<string> WarningKinds { get; init; } = Array.Empty<string>();

    public bool DisallowedNormalContextInjection { get; init; }

    public string GuardStatus { get; init; } = GraphExpansionGuardStatus.Passed;
}

/// <summary>graph expansion guarded opt-in 冻结闸门报告。</summary>
public sealed class GraphExpansionGuardedOptInGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public bool Passed { get; init; }

    public IReadOnlyList<GraphExpansionGuardedOptInGateScopeResult> Scopes { get; init; } =
        Array.Empty<GraphExpansionGuardedOptInGateScopeResult>();

    public IReadOnlyList<string> FailedConditions { get; init; } = Array.Empty<string>();
}

/// <summary>单个 scope 的 graph expansion guarded opt-in 冻结闸门结果。</summary>
public sealed class GraphExpansionGuardedOptInGateScopeResult
{
    public string Scope { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public string GuardStatus { get; init; } = GraphExpansionGuardStatus.Passed;

    public int NormalSelectedSetChanged { get; init; }

    public int RiskAfterRoutingCount { get; init; }

    public int WrongSectionRiskCount { get; init; }

    public int MustNotHitRiskCount { get; init; }

    public int LifecycleRiskCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int UnexpectedWarningDelta { get; init; }

    public int DisallowedNormalContextInjection { get; init; }

    public IReadOnlyList<string> FailedConditions { get; init; } = Array.Empty<string>();
}

public static class RelationGraphDiagnosticTypes
{
    public const string LegacyRelationType = nameof(LegacyRelationType);

    public const string UnknownRelationType = nameof(UnknownRelationType);

    public const string MissingInverseRelation = nameof(MissingInverseRelation);

    public const string BrokenSource = nameof(BrokenSource);

    public const string BrokenTarget = nameof(BrokenTarget);

    public const string MissingEvidence = nameof(MissingEvidence);

    public const string EvidenceBackfillRequired = nameof(EvidenceBackfillRequired);

    public const string InvalidDirection = nameof(InvalidDirection);

    public const string InvalidSourceKind = nameof(InvalidSourceKind);

    public const string InvalidTargetKind = nameof(InvalidTargetKind);

    public const string DuplicateRelation = nameof(DuplicateRelation);

    public const string ConflictingRelation = nameof(ConflictingRelation);

    public const string SupersedeCycle = nameof(SupersedeCycle);

    public const string WeakRelatedToOveruse = nameof(WeakRelatedToOveruse);

    public const string AuditOnlyRelationInNormalPath = nameof(AuditOnlyRelationInNormalPath);

    public const string LowConfidence = nameof(LowConfidence);

    public const string UnreviewedHighImpactRelation = nameof(UnreviewedHighImpactRelation);

    public const string RejectedRelationStillActive = nameof(RejectedRelationStillActive);

    public const string DeprecatedRelationUsedInNormalPath = nameof(DeprecatedRelationUsedInNormalPath);

    public const string CandidateRelationUsedInNormalPath = nameof(CandidateRelationUsedInNormalPath);

    public const string RelationConfidenceMissing = nameof(RelationConfidenceMissing);

    public const string RelationEvidenceBroken = nameof(RelationEvidenceBroken);

    public const string RelationLifecycleMismatch = nameof(RelationLifecycleMismatch);

    public const string RejectedRelationHasActiveInverse = nameof(RejectedRelationHasActiveInverse);

    public const string DeprecatedRelationUsedByActiveChain = nameof(DeprecatedRelationUsedByActiveChain);

    public const string NeedsEvidenceHighImpactRelation = nameof(NeedsEvidenceHighImpactRelation);

    public const string ReviewedRelationMissingReviewer = nameof(ReviewedRelationMissingReviewer);

    public const string ConfidenceChangedWithoutReview = nameof(ConfidenceChangedWithoutReview);

    public const string RelationReviewHistoryMissing = nameof(RelationReviewHistoryMissing);
}
