using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>关系类型注册表，提供图谱校验所需的第一版 taxonomy。</summary>
public sealed class RelationTypeRegistry
{
    private readonly IReadOnlyDictionary<string, RelationTypeDefinition> _definitions;

    /// <summary>
    /// 关系类型注册表，用于存储和管理不同类型的关系定义。这些关系定义包括它们的名称、权重、是否需要证据支持以及适用的源和目标种类。
    /// 该类在初始化时预定义了一系列的关系类型，并将其存储在一个字典中以便快速查找。
    /// </summary>
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
        return _definitions.GetValueOrDefault(relationType);
    }

    /// <summary>
    /// 创建并返回一个新的关系类型定义。
    /// </summary>
    /// <param name="type">关系类型的名称。</param>
    /// <param name="directional">指示该关系是否具有方向性，默认为true。</param>
    /// <param name="inverse">该关系的逆向关系类型名称，可选参数，默认为null。</param>
    /// <param name="weight">关系的默认权重，默认值为0.5。</param>
    /// <param name="requiresEvidence">指示该关系是否需要证据支持，默认为false。</param>
    /// <param name="auditOnly">指示该关系是否仅用于审核，默认为false。</param>
    /// <param name="allowsNormalExpansion">指示该关系是否允许常规扩展，默认为true。</param>
    /// <param name="sourceKinds">允许作为源节点的种类列表，可选参数，默认为空列表。</param>
    /// <param name="targetKinds">允许作为目标节点的种类列表，可选参数，默认为空列表。</param>
    /// <param name="warnings">与该关系类型相关的警告信息列表，可选参数，默认为空列表。</param>
    /// <returns>根据提供的参数构建的关系类型定义实例。</returns>
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
