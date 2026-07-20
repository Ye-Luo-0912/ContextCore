using ContextCore.Abstractions;

namespace ContextCore.Abstractions;

// ===========================================================================
// R23-6：Agent Context Delta Calculator 契约
//
// 目标（对齐 R23 规格）：
//   1. 计算两次 AgentContextSnapshot 之间的 AgentContextDelta（增量变更）。
//   2. 增量类型：Added/Modified/Removed Sections + Added/Removed DecisionIds +
//      Added/Removed ConstraintIds + Added ToolCallRefs + TokenDelta。
//   3. 纯函数：不修改输入 snapshot；输出 deterministic delta。
//
// 设计边界：
//   - Section 比较基于 SectionName（key）；内容差异用 Content 字符串比较；
//   - Decision/Constraint ID 比较基于字符串集合差集；
//   - TokenDelta = ToSnapshot.ActualTokens - FromSnapshot.ActualTokens（可为负）；
//   - 不引入存储 I/O；calculator 无状态。
// ===========================================================================

/// <summary>
/// R23-6：Agent Context 增量计算器。计算两次 <see cref="AgentContextSnapshot"/> 之间的 <see cref="AgentContextDelta"/>。
/// </summary>
/// <remarks>
/// 纯函数；不修改输入；输出 deterministic。
/// </remarks>
public interface IAgentContextDeltaCalculator
{
    /// <summary>计算两次 snapshot 之间的增量。</summary>
    /// <param name="fromSnapshot">起始 snapshot（较早）。</param>
    /// <param name="toSnapshot">目标 snapshot（较新）。</param>
    /// <param name="deltaId">可选 delta ID（null = 自动生成）。</param>
    /// <param name="source">可选 delta 来源标识。</param>
    /// <returns>描述两次 snapshot 差异的 <see cref="AgentContextDelta"/>。</returns>
    AgentContextDelta Calculate(
        AgentContextSnapshot fromSnapshot,
        AgentContextSnapshot toSnapshot,
        string? deltaId = null,
        string source = "");
}
