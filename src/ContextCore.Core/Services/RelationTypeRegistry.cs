using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>关系类型注册表，提供图谱校验所需的第一版 taxonomy。</summary>
public sealed class RelationTypeRegistry
{
    private readonly IReadOnlyDictionary<string, RelationTypeDefinition> _definitions;

    public RelationTypeRegistry()
    {
        var definitions = new[]
        {
            Definition("contains", inverse: null, weight: 0.7, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition("references", inverse: null, weight: 0.5, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.DerivedFrom, inverse: null, weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.EvidenceFor, inverse: null, weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "CandidateMemory", "CandidateConstraint"]),
            Definition("supports", inverse: null, weight: 0.6, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.DependsOn, inverse: null, weight: 0.6, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition("requires", inverse: null, weight: 0.7, sourceKinds: ["*"], targetKinds: ["StableConstraint", "CandidateConstraint", "Constraint"]),
            Definition("blocks", inverse: null, weight: 0.7, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition("conflicts_with", directional: false, inverse: "conflicts_with", weight: 0.8, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.AppliesTo, inverse: null, weight: 0.9, requiresEvidence: true, sourceKinds: ["StableConstraint", "CandidateConstraint", "Constraint"], targetKinds: ["*"]),
            Definition(ContextRelationTypes.SupersededBy, inverse: ContextRelationTypes.Replaces, weight: 1.0, requiresEvidence: true, sourceKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "GlobalMemory"], targetKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "GlobalMemory"]),
            Definition(ContextRelationTypes.Replaces, inverse: ContextRelationTypes.SupersededBy, weight: 1.0, requiresEvidence: true, sourceKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "GlobalMemory"], targetKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "GlobalMemory"]),
            Definition("replaced_by", inverse: ContextRelationTypes.Replaces, weight: 1.0, requiresEvidence: true, sourceKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "GlobalMemory"], targetKinds: ["StableMemory", "StableConstraint", "DecisionRecord", "GlobalMemory"]),
            Definition("same_as", directional: false, inverse: "same_as", weight: 0.7, requiresEvidence: true, sourceKinds: ["*"], targetKinds: ["*"]),
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
        return _definitions.TryGetValue(relationType, out var definition) ? definition : null;
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
            AllowedSourceKinds = sourceKinds ?? Array.Empty<string>(),
            AllowedTargetKinds = targetKinds ?? Array.Empty<string>(),
            Warnings = warnings ?? Array.Empty<string>()
        };
    }
}
