namespace ContextCore.Abstractions.Models;

/// <summary>图节点的正式种类枚举，用于关系端点类型治理。</summary>
public enum GraphNodeKind
{
    /// <summary>未知或未指定。</summary>
    Unknown,
    /// <summary>通用上下文条目。</summary>
    ContextItem,
    /// <summary>稳定记忆。</summary>
    StableMemory,
    /// <summary>稳定约束。</summary>
    StableConstraint,
    /// <summary>候选记忆。</summary>
    CandidateMemory,
    /// <summary>候选约束。</summary>
    CandidateConstraint,
    /// <summary>约束（通用）。</summary>
    Constraint,
    /// <summary>全局记忆。</summary>
    GlobalMemory,
    /// <summary>决策记录。</summary>
    DecisionRecord,
    /// <summary>上下文包（正式图节点）。</summary>
    Package,
    /// <summary>操作（如压缩、摄取等操作的唯一标识，正式图节点）。</summary>
    Operation
}

/// <summary>关系生命周期状态常量。</summary>
public static class RelationLifecycles
{
    /// <summary>活跃（默认）。</summary>
    public const string Active = "active";

    /// <summary>已废弃。</summary>
    public const string Deprecated = "deprecated";

    /// <summary>已被替代。</summary>
    public const string Superseded = "superseded";
}

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

    /// <summary>
    /// 每跳衰减因子（0, 1]。默认 1.0 = 不衰减。
    /// childScore = parentScore * DecayFactor * weightFactor * confidenceFactor。
    /// 设为 0.7 时，2 跳路径的分数为 seed.Score * 0.7^2 * weight * confidence。
    /// </summary>
    public double DecayFactor { get; init; } = 1.0;

    /// <summary>
    /// 是否启用 weight/confidence 传播到 child score。默认 true。
    /// false 时 childScore = parentScore * DecayFactor（仅路径衰减，无边质量传播），
    /// 保持与旧版完全等价的排序语义。
    /// </summary>
    public bool EnableScorePropagation { get; init; } = true;

    public string LifecyclePolicy { get; init; } = string.Empty;

    public IReadOnlyList<RelationTraversalPolicy> TraversalPolicies { get; init; } = Array.Empty<RelationTraversalPolicy>();
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

/// <summary>Projector 输出诊断记录。</summary>
public sealed record RelationProjectorOutputDiagnostic(
    string Severity,
    string DiagnosticType,
    string RelationId,
    string Message);

/// <summary>
/// IRelationProjectionWriter.WriteAsync 的返回结果：包含本次写入的 provenance、
/// 请求/写入/跳过计数、是否通过 High 级诊断验证，以及诊断和被跳过的 relation id 列表。
/// </summary>
public sealed class RelationProjectionWriteResult
{
    public string Provenance { get; init; } = string.Empty;
    public int WrittenCount { get; init; }
    public int SkippedCount { get; init; }
    public int RequestedCount { get; init; }
    public bool IsValid { get; init; }
    public IReadOnlyList<RelationProjectorOutputDiagnostic> Diagnostics { get; init; } = Array.Empty<RelationProjectorOutputDiagnostic>();
    public IReadOnlyList<string> SkippedRelationIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Supersede 投影请求，解构 StableLifecycleReviewService 的私有 StableSource 以便跨层传递给 IRelationProjector。
/// </summary>
public sealed record SupersedeProjectionRequest(
    string WorkspaceId,
    string CollectionId,
    string SourceId,
    string ReplacementId,
    string SourceStableKind,
    string ReplacementStableKind,
    string ReviewId,
    string OperationId,
    string Reviewer,
    string Reason,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyDictionary<string, string> RequestMetadata,
    DateTimeOffset Now);

/// <summary>统一关系遍历请求。</summary>
public sealed class RelationTraversalRequest
{
    public required string WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public required IReadOnlyList<RelationTraversalSeed> Seeds { get; init; }

    public required RelationExpansionProfile Profile { get; init; }

    public RelationDirection Direction { get; init; } = RelationDirection.Outgoing;

    public int? MaxNodesOverride { get; init; }

    public int? MaxRelationsOverride { get; init; }
}

public sealed record RelationTraversalSeed(string ItemId, double Score = 1.0);

/// <summary>统一关系遍历结果。</summary>
public sealed class RelationTraversalResult
{
    public required IReadOnlyList<RelationTraversalEdge> Edges { get; init; }

    public int MaxDepthReached { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record RelationTraversalEdge(
    ContextRelation Relation,
    int Depth,
    double SourceScore,
    string Path,
    string NeighborId,
    double TargetScore);

/// <summary>关系子图 DTO，包含节点和边的快照，用于可视化与分析。</summary>
public sealed class RelationSubgraph
{
    public string RootItemId { get; init; } = string.Empty;

    public IReadOnlyList<RelationSubgraphNode> Nodes { get; init; } = Array.Empty<RelationSubgraphNode>();

    public IReadOnlyList<RelationSubgraphEdge> Edges { get; init; } = Array.Empty<RelationSubgraphEdge>();

    public int MaxDepthReached { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class RelationSubgraphNode
{
    public string ItemId { get; init; } = string.Empty;

    public int Depth { get; init; }

    public string? NodeKind { get; init; }

    /// <summary> 节点标题（条目标题或摘要首行），供 UI 紧凑展示。</summary>
    public string? Title { get; init; }

    /// <summary> 节点摘要，供 UI 紧凑展示。</summary>
    public string? Summary { get; init; }

    /// <summary> 节点生命周期（Active/Deprecated/Superseded）。</summary>
    public string? Lifecycle { get; init; }

    /// <summary> 节点审核状态（Reviewed/Rejected/NeedsEvidence）。</summary>
    public string? ReviewStatus { get; init; }
}

public sealed class RelationSubgraphEdge
{
    public string RelationId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public double Weight { get; init; }

    public double Confidence { get; init; }

    public string? Lifecycle { get; init; }

    public string? ReviewStatus { get; init; }

    public int Depth { get; init; }
}

/// <summary>
/// 统一邻居查询 DTO。携带方向、类型、置信度、生命周期、分页和扫描上限，
/// 代替多个 QueryBy 方法。Postgres 在 SQL 中过滤和 Limit；File/InMemory 在内存中过滤。
/// </summary>
public sealed class RelationNeighborQuery
{
    /// <summary>所属工作空间 ID（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>筛选指定集合（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>查询邻居的种子条目 ID（必填）。</summary>
    public required string ItemId { get; init; }

    /// <summary>方向过滤：出边、入边或双向。</summary>
    public RelationDirection Direction { get; init; } = RelationDirection.Both;

    /// <summary>筛选指定关系类型（可选）。为空时允许所有类型。</summary>
    public string? RelationType { get; init; }

    /// <summary>
    /// 筛选多个关系类型（可选）。非空时优先于 <see cref="RelationType"/>，
    /// 存储层在 Take/排序前按列表过滤，避免高权重非允许边把合法边挤出窗口。
    /// </summary>
    public IReadOnlyList<string> AllowedRelationTypes { get; init; } = Array.Empty<string>();

    /// <summary>最低置信度阈值（可选）。默认 0，不过滤。</summary>
    public double MinConfidence { get; init; }

    /// <summary>排除的生命周期值列表（可选）。匹配的关系将被过滤掉。</summary>
    public IReadOnlyList<string> ExcludedLifecycles { get; init; } = Array.Empty<string>();

    /// <summary>排除的 ReviewStatus 值列表（可选）。</summary>
    public IReadOnlyList<string> ExcludedReviewStatuses { get; init; } = Array.Empty<string>();

    /// <summary>返回记录数上限（分页 Take）。默认 100。</summary>
    public int Take { get; init; } = 100;

    /// <summary>跳过的记录数（分页 Skip）。默认 0。</summary>
    public int Skip { get; init; }

    /// <summary>扫描上限：从数据源读取的最大行数，防止全表扫描。默认 1000。</summary>
    public int MaxScan { get; init; } = 1000;
}

/// <summary>
/// Graph 查询全局硬上限。所有图查询路径（store / traversal engine）必须遵守，
/// 防止病态查询（超大种子集 × 高扇出）导致全局扫描或结果集爆炸。
/// </summary>
/// <remarks>
/// 语义：
///   - <see cref="MaxSeeds"/>：单次图查询的最大种子数，超出部分直接截断（保留原序）。
///   - <see cref="MaxEdgesPerSeed"/>：单种子返回的最大边数（per-seed Take 硬上限）。
///   - <see cref="MaxTotalEdges"/>：单次图查询全局读取/返回的最大边数，超出即截断并标记 Truncated。
/// 存储层通过查询参数（<see cref="RelationNeighborBatchQuery.GlobalEdgeLimit"/>、MaxScan、Take）强制这些上限，
/// 本类常量作为默认值与天花板（存储层对 GlobalEdgeLimit clamp 到 [1, MaxTotalEdges]）。
/// </remarks>
public static class GraphQueryLimits
{
    /// <summary>单次图查询的最大种子数（硬上限）。</summary>
    public const int MaxSeeds = 50;

    /// <summary>单种子返回的最大边数（per-seed Take 硬上限）。</summary>
    public const int MaxEdgesPerSeed = 100;

    /// <summary>单次图查询全局读取/返回的最大边数（硬上限）。</summary>
    public const int MaxTotalEdges = 5000;

    /// <summary>图扩展可引入的最大新节点数（不含种子）。防止 BFS 爆炸式扩展。</summary>
    public const int MaxExpandedNodes = 500;

    /// <summary>图候选 hydration 的最大条目数。防止对过多邻居节点进行正文批量获取。</summary>
    public const int MaxHydrationItems = 200;

    /// <summary>图候选的最大总 Token 预算。防止图扩展结果超出模型上下文窗口。</summary>
    public const int MaxGraphTokens = 8192;
}

/// <summary>
/// 批量邻居查询 DTO：一次查询多个种子节点的邻居，消除 BFS 逐节点往返。
/// 字段语义与 <see cref="RelationNeighborQuery"/> 一致，区别仅在于 <see cref="ItemIds"/>（多个种子）。
/// Take/Skip/MaxScan 仍为 per-seed 语义：每个种子独立排序、独立扫描上限、独立分页；
/// <see cref="GlobalEdgeLimit"/> 为所有种子合计的全局上限：存储层在 SQL/遍历中强制，命中即截断并标记 Truncated。
/// </summary>
public sealed class RelationNeighborBatchQuery
{
    /// <summary>所属工作空间 ID（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>筛选指定集合（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>查询邻居的种子条目 ID 列表（必填，至少 1 个）。重复 ID 在存储层去重。</summary>
    public required IReadOnlyList<string> ItemIds { get; init; }

    /// <summary>方向过滤：出边、入边或双向（默认 Both）。
    /// Both 方向下，一条同时连接两个种子的边会出现在两个种子的结果集中。</summary>
    public RelationDirection Direction { get; init; } = RelationDirection.Both;

    /// <summary>筛选指定关系类型（可选）。为空时允许所有类型。</summary>
    public string? RelationType { get; init; }

    /// <summary>筛选多个关系类型（可选）。非空时优先于 <see cref="RelationType"/>。</summary>
    public IReadOnlyList<string> AllowedRelationTypes { get; init; } = Array.Empty<string>();

    /// <summary>最低置信度阈值（可选）。默认 0，不过滤。</summary>
    public double MinConfidence { get; init; }

    /// <summary>排除的生命周期值列表（可选）。</summary>
    public IReadOnlyList<string> ExcludedLifecycles { get; init; } = Array.Empty<string>();

    /// <summary>排除的 ReviewStatus 值列表（可选）。</summary>
    public IReadOnlyList<string> ExcludedReviewStatuses { get; init; } = Array.Empty<string>();

    /// <summary>per-seed 返回记录数上限（分页 Take）。默认 100。</summary>
    public int Take { get; init; } = 100;

    /// <summary>per-seed 跳过的记录数（分页 Skip）。默认 0。</summary>
    public int Skip { get; init; }

    /// <summary>per-seed 扫描上限：从数据源读取的最大行数。默认 1000。</summary>
    public int MaxScan { get; init; } = 1000;

    /// <summary>
    /// 单次查询所有种子合计的全局返回边数上限（硬上限，默认 <see cref="GraphQueryLimits.MaxTotalEdges"/>）。
    /// 存储层在 SQL/遍历中强制：累计返回边数达到上限即停止读取并截断，命中种子标记
    /// <see cref="RelationNeighborBatchResult.Truncated"/>。调用方（BFS 引擎 / Provider）可传入自身剩余预算。
    /// </summary>
    public int GlobalEdgeLimit { get; init; } = GraphQueryLimits.MaxTotalEdges;
}

/// <summary>
/// 单个种子的批量邻居查询结果。
/// 引擎按 <see cref="ItemId"/> 索引结果以扩展 BFS frontier。
/// 新增 <see cref="Truncated"/> 信号，标记存储层是否因 <c>MaxScan</c>（或 Postgres 全局 LIMIT）
/// 截断了该种子的候选集。true 表示返回的 <see cref="Relations"/> 可能不完整，
/// 调用方（如 BFS 引擎）应据此设置自身的 Truncated 标记并向用户告警。
/// </summary>
public sealed class RelationNeighborBatchResult
{
    /// <summary>对应的种子条目 ID（来自查询的 ItemIds）。</summary>
    public required string ItemId { get; init; }

    /// <summary>该种子的邻居关系列表（已按 Weight/Confidence/CreatedAt 排序并应用 Skip/Take）。</summary>
    public required IReadOnlyList<ContextRelation> Relations { get; init; }

    /// <summary>
    /// true 表示存储层对该种子的候选集进行了截断，<see cref="Relations"/> 可能不完整。
    /// 触发条件（任一即置 true）：
    /// <list type="bullet">
    /// <item>InMemory/FileSystem：该种子过滤后的候选数大于 <c>MaxScan</c>。</item>
    /// <item>Postgres：该种子的桶大小大于 <c>MaxScan</c>，或 SQL 全局 LIMIT 命中（保守标记所有非空桶）。</item>
    /// </list>
    /// 默认 false（未截断）。
    /// </summary>
    public bool Truncated { get; init; }
}

/// <summary>关系旧数据迁移报告。</summary>
public sealed class RelationMigrationReport
{
    /// <summary>扫描的关系总数。</summary>
    public int TotalRelations { get; init; }

    /// <summary>实际更新的关系数。</summary>
    public int UpdatedRelations { get; set; }

    /// <summary>回填 NodeKind 的次数（每条关系最多 2 次：source + target）。</summary>
    public int NodeKindBackfilled { get; set; }

    /// <summary>回填 Lifecycle 的次数。</summary>
    public int LifecycleBackfilled { get; set; }

    /// <summary>回填 ReviewStatus 的次数。</summary>
    public int ReviewStatusBackfilled { get; set; }

    /// <summary>回填 Provenance 的次数。</summary>
    public int ProvenanceBackfilled { get; set; }

    /// <summary>已是最新、无需变更的关系数。</summary>
    public int SkippedRelations { get; set; }

    /// <summary>是否为 dry-run（未实际写入）。</summary>
    public bool DryRun { get; init; }
}

/// <summary>P3.1-d：关系迁移选项。</summary>
public sealed class RelationMigrationOptions
{
    /// <summary>限定迁移的集合范围（null = 工作空间内所有集合）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>是否实际写入变更。默认 false（dry-run，仅报告将变更的内容）。</summary>
    public bool Apply { get; init; }
}
