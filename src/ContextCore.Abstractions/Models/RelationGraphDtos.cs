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

/// <summary>relation explain 中的端点条目摘要。</summary>
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

public static class RelationGraphDiagnosticTypes
{
    public const string UnknownRelationType = nameof(UnknownRelationType);

    public const string MissingInverseRelation = nameof(MissingInverseRelation);

    public const string BrokenSource = nameof(BrokenSource);

    public const string BrokenTarget = nameof(BrokenTarget);

    public const string MissingEvidence = nameof(MissingEvidence);

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
}
