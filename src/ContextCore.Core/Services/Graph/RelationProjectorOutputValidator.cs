using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// GRAPH-09：投影输出验证器 — 在 projector 输出写入 store 前通过 registry/validation 检查，
/// 确保零 High 级诊断。仅检查 projector 可控字段（类型、NodeKind、方向、inverse 完整性），
/// 不检查需要 item store 上下文的诊断（BrokenSource/BrokenTarget）。
/// </summary>
public sealed class RelationProjectorOutputValidator
{
    private readonly RelationTypeRegistry _registry;
    private readonly RelationTypeNormalizer _normalizer;

    public RelationProjectorOutputValidator(
        RelationTypeRegistry registry,
        RelationTypeNormalizer normalizer)
    {
        _registry = registry;
        _normalizer = normalizer;
    }

    /// <summary>
    /// 验证 projector 输出，返回诊断列表。零 High 级诊断表示通过。
    /// </summary>
    public IReadOnlyList<RelationProjectorOutputDiagnostic> Validate(
        IReadOnlyList<ContextRelation> relations,
        string provenance)
    {
        var diagnostics = new List<RelationProjectorOutputDiagnostic>();

        for (var i = 0; i < relations.Count; i++)
        {
            var relation = relations[i];
            var normalizedType = _normalizer.Normalize(relation.RelationType);
            var definition = _registry.Find(normalizedType);

            if (definition is null)
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "High",
                    RelationGraphDiagnosticTypes.UnknownRelationType,
                    relation.Id,
                    $"Projector {provenance} produced unknown relation type: {relation.RelationType}"));
                continue;
            }

            // GRAPH-09：projector 必须填充正式 NodeKind 字段
            if (string.IsNullOrWhiteSpace(relation.SourceNodeKind))
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "High",
                    "MissingSourceNodeKind",
                    relation.Id,
                    $"Projector {provenance} produced relation with empty SourceNodeKind."));
            }
            else if (!KindAllowed(relation.SourceNodeKind, definition.AllowedSourceKinds))
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "High",
                    RelationGraphDiagnosticTypes.InvalidSourceKind,
                    relation.Id,
                    $"Projector {provenance} produced SourceNodeKind={relation.SourceNodeKind} not allowed for type {definition.Type}. Allowed: [{string.Join(", ", definition.AllowedSourceKinds)}]"));
            }

            if (string.IsNullOrWhiteSpace(relation.TargetNodeKind))
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "High",
                    "MissingTargetNodeKind",
                    relation.Id,
                    $"Projector {provenance} produced relation with empty TargetNodeKind."));
            }
            else if (!KindAllowed(relation.TargetNodeKind, definition.AllowedTargetKinds))
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "High",
                    RelationGraphDiagnosticTypes.InvalidTargetKind,
                    relation.Id,
                    $"Projector {provenance} produced TargetNodeKind={relation.TargetNodeKind} not allowed for type {definition.Type}. Allowed: [{string.Join(", ", definition.AllowedTargetKinds)}]"));
            }

            // GRAPH-09：无向边必须按规范顺序存储（source < target）
            if (!definition.IsDirectional
                && !string.IsNullOrWhiteSpace(relation.SourceId)
                && !string.IsNullOrWhiteSpace(relation.TargetId)
                && string.Compare(relation.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase) > 0)
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "Low",
                    RelationGraphDiagnosticTypes.InvalidDirection,
                    relation.Id,
                    $"Projector {provenance} produced undirected relation in non-canonical order: source={relation.SourceId} > target={relation.TargetId}"));
            }
        }

        // GRAPH-09：directional inverse 完整性检查（如 superseded_by ↔ replaces）
        foreach (var relation in relations)
        {
            var normalizedType = _normalizer.Normalize(relation.RelationType);
            var definition = _registry.Find(normalizedType);
            if (definition?.InverseType is null || definition.IsDirectional == false)
            {
                continue;
            }

            // 自反 inverse（inverse == type）跳过
            if (string.Equals(definition.InverseType, normalizedType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasInverse = relations.Any(other =>
                string.Equals(_normalizer.Normalize(other.RelationType), definition.InverseType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.TargetId, relation.SourceId, StringComparison.OrdinalIgnoreCase));

            if (!hasInverse)
            {
                diagnostics.Add(new RelationProjectorOutputDiagnostic(
                    "High",
                    RelationGraphDiagnosticTypes.MissingInverseRelation,
                    relation.Id,
                    $"Projector {provenance} produced {normalizedType} without inverse {definition.InverseType}."));
            }
        }

        return diagnostics;
    }

    /// <summary>验证 projector 输出是否通过（零 High 级诊断）。</summary>
    public bool IsValid(IReadOnlyList<ContextRelation> relations, string provenance)
    {
        return Validate(relations, provenance).All(static d => d.Severity != "High");
    }

    private static bool KindAllowed(string kind, IReadOnlyList<string> allowedKinds)
    {
        if (allowedKinds.Count == 0)
        {
            return true;
        }

        foreach (var allowed in allowedKinds)
        {
            if (string.Equals(allowed, "*", StringComparison.OrdinalIgnoreCase)
                || string.Equals(allowed, kind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Projector 输出诊断记录。</summary>
public sealed record RelationProjectorOutputDiagnostic(
    string Severity,
    string DiagnosticType,
    string RelationId,
    string Message);
