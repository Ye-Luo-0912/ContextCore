using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>关系类型注册表，提供图谱校验所需的正式 taxonomy。所有类型常量均引用 ContextRelationTypes。</summary>
public sealed class RelationTypeRegistry
{
    private readonly IReadOnlyDictionary<string, RelationTypeDefinition> _definitions;

    /// <summary>
    /// 关系类型注册表，统一管理所有关系类型定义。所有类型名称均通过 ContextRelationTypes 常量引用，
    /// 确保契约层与实现层对齐。节点种类词表覆盖 GraphNodeKind 全部正式节点（含 Package、Operation）。
    /// </summary>
    public RelationTypeRegistry()
    {
        var definitions = new[]
        {
            Definition(ContextRelationTypes.Contains, inverse: null, weight: 0.7, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.References, inverse: null, weight: 0.5, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.DerivedFrom, inverse: null, weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.EvidenceFor, inverse: null, weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.CandidateMemory), nameof(GraphNodeKind.CandidateConstraint)]),
            Definition(ContextRelationTypes.Supports, inverse: null, weight: 0.6, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.DependsOn, inverse: null, weight: 0.6, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.Requires, inverse: null, weight: 0.7, sourceKinds: ["*"], targetKinds: [nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.CandidateConstraint), nameof(GraphNodeKind.Constraint)]),
            Definition(ContextRelationTypes.Blocks, inverse: null, weight: 0.7, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.ConflictsWith, directional: false, inverse: ContextRelationTypes.ConflictsWith, weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.AppliesTo, inverse: null, weight: 0.9, requiresEvidence: true, sourceKinds: [nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.CandidateConstraint), nameof(GraphNodeKind.Constraint)], targetKinds: ["*"]),
            Definition(ContextRelationTypes.SupersededBy, inverse: ContextRelationTypes.Replaces, weight: 1.0, requiresEvidence: true, sourceKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.GlobalMemory)], targetKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.GlobalMemory)]),
            Definition(ContextRelationTypes.Replaces, inverse: ContextRelationTypes.SupersededBy, weight: 1.0, requiresEvidence: true, sourceKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.GlobalMemory)], targetKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.GlobalMemory)]),
            Definition(ContextRelationTypes.ReplacedBy, inverse: ContextRelationTypes.Replaces, weight: 1.0, requiresEvidence: true, sourceKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.GlobalMemory)], targetKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.DecisionRecord), nameof(GraphNodeKind.GlobalMemory)]),
            Definition(ContextRelationTypes.SameAs, directional: false, inverse: ContextRelationTypes.SameAs, weight: 0.7, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.Contradicts, directional: false, inverse: ContextRelationTypes.Contradicts, weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.Duplicates, directional: false, inverse: ContextRelationTypes.Duplicates, weight: 0.7, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.IncludedInPackage, inverse: null, weight: 0.5, sourceKinds: ["*"], targetKinds: [nameof(GraphNodeKind.Package)]),
            Definition(ContextRelationTypes.GeneratedBy, inverse: null, weight: 0.6, sourceKinds: ["*"], targetKinds: [nameof(GraphNodeKind.Operation)]),
            // 短期晋升流程生成 Candidate 层目标条目（Status=Candidate），因此 PromotedFrom 的 source
            // 同时允许 StableMemory/StableConstraint（稳定晋升路径）和 CandidateMemory/CandidateConstraint（短期晋升路径）。
            Definition(ContextRelationTypes.PromotedFrom, inverse: null, weight: 0.7, sourceKinds: [nameof(GraphNodeKind.StableMemory), nameof(GraphNodeKind.StableConstraint), nameof(GraphNodeKind.CandidateMemory), nameof(GraphNodeKind.CandidateConstraint)], targetKinds: [nameof(GraphNodeKind.CandidateMemory), nameof(GraphNodeKind.CandidateConstraint)]),
            Definition(ContextRelationTypes.Summarizes, inverse: null, weight: 0.7, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.RelatedTo, directional: false, inverse: ContextRelationTypes.RelatedTo, weight: 0.3, sourceKinds: ["*"], targetKinds: ["*"], warnings: ["Weak generic relation; prefer a specific relation type when possible."])
        };

        _definitions = definitions.ToDictionary(item => item.Type, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RelationTypeDefinition> GetAll()
    {
        return _definitions.Values
            .OrderBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RelationTypeDefinition? Find(string relationType)
    {
        return _definitions.GetValueOrDefault(relationType);
    }

    private static RelationTypeDefinition Definition(
        string type,
        bool directional = true,
        string? inverse = null,
        double weight = 0.5,
        bool requiresEvidence = false,
        bool auditOnly = false,
        bool allowsNormalExpansion = true,
        IReadOnlyList<string>? sourceKinds = null,
        IReadOnlyList<string>? targetKinds = null,
        IReadOnlyList<string>? warnings = null)
    {
        return new RelationTypeDefinition
        {
            Type = type,
            IsDirectional = directional,
            InverseType = inverse,
            DefaultWeight = weight,
            RequiresEvidence = requiresEvidence,
            AuditOnly = auditOnly,
            AllowsNormalExpansion = allowsNormalExpansion,
            AllowedSourceKinds = sourceKinds ?? [],
            AllowedTargetKinds = targetKinds ?? [],
            Warnings = warnings ?? []
        };
    }
}
