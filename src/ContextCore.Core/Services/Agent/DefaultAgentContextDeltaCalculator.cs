using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// R23-6：DefaultAgentContextDeltaCalculator — 默认 delta 计算器实现。
//
// 实现 IAgentContextDeltaCalculator：
//   - Section 比较：基于 SectionName（key）；Content 字符串不同 = Modified；
//     仅在 ToSnapshot 中 = Added；仅在 FromSnapshot 中 = Removed。
//   - Decision/Constraint ID：集合差集。
//   - ToolCallRefs：ToSnapshot 中的新 key = AddedToolCallRefs。
//   - TokenDelta：ToSnapshot.ActualTokens - FromSnapshot.ActualTokens。
//   - 纯函数；无状态。
// ===========================================================================

/// <summary>
/// R23-6：<see cref="IAgentContextDeltaCalculator"/> 的默认实现。
/// </summary>
/// <remarks>
/// 纯函数；线程安全；无状态。
/// </remarks>
public sealed class DefaultAgentContextDeltaCalculator : IAgentContextDeltaCalculator
{
    /// <inheritdoc />
    public AgentContextDelta Calculate(
        AgentContextSnapshot fromSnapshot,
        AgentContextSnapshot toSnapshot,
        string? deltaId = null,
        string source = "")
    {
        ArgumentNullException.ThrowIfNull(fromSnapshot);
        ArgumentNullException.ThrowIfNull(toSnapshot);

        // 校验：两次 snapshot 必须属于同一 session
        if (!string.Equals(fromSnapshot.Session.Value, toSnapshot.Session.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Session mismatch：from={fromSnapshot.Session.Value}，to={toSnapshot.Session.Value}",
                nameof(toSnapshot));
        }

        // ===== Section 差异 =====
        var fromSections = fromSnapshot.Sections
            .ToDictionary(s => s.SectionName, s => s, StringComparer.Ordinal);
        var toSections = toSnapshot.Sections
            .ToDictionary(s => s.SectionName, s => s, StringComparer.Ordinal);

        var addedSections = toSections.Keys.Except(fromSections.Keys, StringComparer.Ordinal).ToList();
        var removedSections = fromSections.Keys.Except(toSections.Keys, StringComparer.Ordinal).ToList();
        var modifiedSections = toSections.Keys
            .Intersect(fromSections.Keys, StringComparer.Ordinal)
            .Where(name => !string.Equals(
                fromSections[name].Content,
                toSections[name].Content,
                StringComparison.Ordinal))
            .ToList();

        // ===== Decision ID 差异 =====
        var addedDecisionIds = toSnapshot.DecisionRequestIds
            .Except(fromSnapshot.DecisionRequestIds, StringComparer.Ordinal)
            .ToList();
        var removedDecisionIds = fromSnapshot.DecisionRequestIds
            .Except(toSnapshot.DecisionRequestIds, StringComparer.Ordinal)
            .ToList();

        // ===== Constraint ID 差异 =====
        var addedConstraintIds = toSnapshot.ConstraintIds
            .Except(fromSnapshot.ConstraintIds, StringComparer.Ordinal)
            .ToList();
        var removedConstraintIds = fromSnapshot.ConstraintIds
            .Except(toSnapshot.ConstraintIds, StringComparer.Ordinal)
            .ToList();

        // ===== ToolCallRefs 差异（仅 Added， Removed 由 ToolResults 重建） =====
        var addedToolCallRefs = toSnapshot.ToolCallRefs
            .Where(kv => !fromSnapshot.ToolCallRefs.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // ===== Token delta =====
        var tokenDelta = toSnapshot.ActualTokens - fromSnapshot.ActualTokens;

        return new AgentContextDelta
        {
            DeltaId = deltaId ?? $"delta-{Guid.NewGuid():N}",
            Session = toSnapshot.Session,
            FromSnapshotId = fromSnapshot.SnapshotId,
            ToSnapshotId = toSnapshot.SnapshotId,
            CreatedAt = DateTimeOffset.UtcNow,
            AddedSections = addedSections,
            ModifiedSections = modifiedSections,
            RemovedSections = removedSections,
            AddedDecisionIds = addedDecisionIds,
            RemovedDecisionIds = removedDecisionIds,
            AddedConstraintIds = addedConstraintIds,
            RemovedConstraintIds = removedConstraintIds,
            AddedToolCallRefs = addedToolCallRefs,
            TokenDelta = tokenDelta,
            Source = source
        };
    }
}
