using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// 可选能力接口：支持按 ID 批量查询上下文条目。
/// 实现此接口的 <see cref="IContextStore"/> 可在一次调用中返回多个条目，
/// 避免 retrieval 通道中的 N+1 单条查询。返回列表只包含找到的条目，顺序不保证。
/// </summary>
public interface IContextStoreBatchLookup
{
    /// <summary>按 ID 批量获取上下文条目。只返回找到的条目。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 可选能力接口：支持按 ID 批量查询记忆条目。
/// 实现此接口的 <see cref="IMemoryStore"/> 可在一次调用中返回多个条目，
/// 避免 retrieval 通道中的 N+1 单条查询。返回列表只包含找到的条目，顺序不保证。
/// </summary>
public interface IMemoryStoreBatchLookup
{
    /// <summary>按 ID 批量获取记忆条目。只返回找到的条目。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextMemoryItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

// ===========================================================================
// Perf-1：Late Hydration 契约
//
// 目标：
//   补齐 Recall metadata → Merge/Score/Allocate → Selected IDs → Batch hydrate
//   selected content 链路的最后一环。Provider 在 Recall 阶段使用 IncludeContent=false
//   只返回 metadata（避免加载所有候选正文），Engine 选出最终 N 个 SelectedEnvelopes 后，
//   由本接口对 Selected IDs 批量 hydrate 正文，避免对未选中候选做无用 I/O。
//
// 设计原则：
//   1. 接口可选注入：未注入时 Runtime 保持旧行为（直接使用 Provider 已加载的 Material）。
//   2. 接口不修改 Envelope 决策字段，仅填充 WorkingSet.Materials 中 Selected 候选的 Content。
//   3. 复用 IContextStoreBatchLookup / IMemoryStoreBatchLookup，避免 N+1 单条查询。
//   4. 已 hydrate 的 Material（Content 非空）跳过，避免重复 I/O。
// ===========================================================================

/// <summary>
/// Perf-1：Selected 候选正文批量 hydrator。
/// 在 Engine 产出 SelectedEnvelopes 后，对选中的候选批量读取正文，
/// 替换 WorkingSet 中对应 Material 的空 Content（IncludeContent=false 路径产出）。
/// </summary>
/// <remarks>
/// 链路位置：Recall（IncludeContent=false）→ Merge → Score → Allocate（SelectedEnvelopes）
///   → <see cref="HydrateAsync"/>（本接口）→ Projector（消费已 hydrate 的 Material）。
/// 未注入时 Runtime 退化为旧行为：Provider 在 Recall 阶段加载所有正文（IncludeContent=true）。
/// </remarks>
public interface ISelectedCandidateHydrator
{
    /// <summary>
    /// 对 SelectedEnvelopes 对应的 Material 批量 hydrate 正文。
    /// </summary>
    /// <param name="selectedEnvelopes">Engine 选中的候选 envelope 集合（仅对这些候选 hydrate 正文）。</param>
    /// <param name="workingSet">当前候选工作集（含 Provider 产出的 Materials，可能 Content 为空）。</param>
    /// <param name="tokenBudget">
    /// P1-1：最终 token 预算（用于 hydrate 后的二次预算修复）。&lt;= 0 表示无预算约束，跳过修复。
    /// hydrate 后正文的真实 TokenCost 可能超出 Engine 基于召回估算值做出的预算分配，
    /// 实现须在返回前按 FinalScore 升序裁减低分 Material（mandatory / hard constraint 不裁剪），
    /// 直到 Selected 候选的 TokenCost 总和回到预算内。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>hydrate 结果（修复后的 WorkingSet + 计数 + 预算修复诊断）；未选中候选保持原样。</returns>
    ValueTask<HydrationResult> HydrateAsync(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        CandidateWorkingSet workingSet,
        int tokenBudget = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// P1-1：Late Hydration 执行结果。携带修复后的 WorkingSet、hydrate 计数与预算修复诊断。
/// </summary>
/// <remarks>
/// Caller（DefaultContextDecisionRuntime）将 <see cref="FailedCount"/> / <see cref="BudgetExceeded"/>
/// 合并进 Outcome.Diagnostics；AgentContext 路径对 hard constraint hydrate 失败 fail-closed。
/// </remarks>
public sealed record HydrationResult
{
    /// <summary>hydrate（+ 预算修复）后的候选工作集。</summary>
    public required CandidateWorkingSet WorkingSet { get; init; }

    /// <summary>成功 hydrate 正文的 Selected 候选数。</summary>
    public required int HydratedCount { get; init; }

    /// <summary>需要 hydrate 但失败的 Selected 候选数（store 未命中 / 读取异常 / 正文为空）。</summary>
    public required int FailedCount { get; init; }

    /// <summary>P1-1：预算修复后 Selected 候选 TokenCost 总和仍超出预算时为 true。</summary>
    public required bool BudgetExceeded { get; init; }

    /// <summary>P1-1：预算修复诊断（被裁剪的 Material 列表及原因）；未发生修复时为 null。</summary>
    public IReadOnlyList<string>? BudgetRepairDiagnostics { get; init; }
}
