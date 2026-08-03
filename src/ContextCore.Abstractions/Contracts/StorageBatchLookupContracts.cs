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

/// <summary>
/// 可选能力接口：按 ID 批量获取上下文条目的元数据（不加载正文）。
/// 实现此接口的 <see cref="IContextStore"/> 可在召回阶段只返回 Content 为空的
/// <see cref="ContextItem"/>（Metadata 携带 content_hash / content_token_cost 等摄取阶段持久化值），
/// 避免把未选中候选的完整正文 jsonb 读入内存；需要正文时由调用方走
/// <see cref="IContextStoreBatchLookup.BatchGetAsync"/> 二次读取。
/// </summary>
/// <remarks>
/// Provider 在 IncludeContent=false 时优先使用本接口，
/// 仅对 Engine 最终选中的候选由 ISelectedCandidateHydrator 批量 hydrate 正文。
/// 未实现本接口的 store 回退到全量批量读取（正确性不变，仅失去元数据投影的传输/解析节省）。
/// </remarks>
public interface IContextStoreMetadataLookup
{
    /// <summary>按 ID 批量获取上下文条目元数据（Content 恒为空，Metadata/作用域字段齐全）。只返回找到的条目，顺序不保证。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextItem>> BatchGetMetadataAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 可选能力接口：按 ID 批量获取记忆条目的元数据（不加载正文）。
/// 语义与 <see cref="IContextStoreMetadataLookup"/> 一致，面向 <see cref="IMemoryStore"/>。
/// </summary>
public interface IMemoryStoreMetadataLookup
{
    /// <summary>按 ID 批量获取记忆条目元数据（Content 恒为空，Layer/Status/Type/Metadata 等字段齐全）。只返回找到的条目，顺序不保证。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextMemoryItem>> BatchGetMetadataAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

// ===========================================================================
// Late Hydration 契约
//
// 目标：
// 补齐 Recall metadata → Merge/Score/Allocate → Selected IDs → Batch hydrate
// selected content 链路的最后一环。Provider 在 Recall 阶段使用 IncludeContent=false
// 只返回 metadata（避免加载所有候选正文），Engine 选出最终 N 个 SelectedEnvelopes 后，
// 由本接口对 Selected IDs 批量 hydrate 正文，避免对未选中候选做无用 I/O。
//
// 设计原则：
// 1. 接口可选注入：未注入时 Runtime 保持旧行为（直接使用 Provider 已加载的 Material）。
// 2. 接口不修改 Envelope 决策字段，仅填充 WorkingSet.Materials 中 Selected 候选的 Content。
// 3. 复用 IContextStoreBatchLookup / IMemoryStoreBatchLookup，避免 N+1 单条查询。
// 4. 已 hydrate 的 Material（Content 非空）跳过，避免重复 I/O。
// ===========================================================================

/// <summary>
/// Selected 候选正文批量 hydrator。
/// 在 Engine 产出 SelectedEnvelopes 后，对选中的候选批量读取正文，
/// 替换 WorkingSet 中对应 Material 的空 Content（IncludeContent=false 路径产出）。
/// </summary>
/// <remarks>
/// 链路位置：Recall（IncludeContent=false）→ Merge → Score → Allocate（SelectedEnvelopes）
/// → <see cref="HydrateAsync"/>（本接口）→ Projector（消费已 hydrate 的 Material）。
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
    /// 最终 token 预算（用于 hydrate 后的二次预算修复）。&lt;= 0 表示无预算约束，跳过修复。
    /// hydrate 后正文的真实 TokenCost 可能超出 Engine 基于召回估算值做出的预算分配，
    /// 实现须在返回前按 FinalScore 升序裁减低分 Material（mandatory / hard constraint 不裁剪），
    /// 直到 Selected 候选的 TokenCost 总和回到预算内。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// hydrate 结果（修复后的 WorkingSet + 计数 + 预算修复诊断 + 正式修复决策 <see cref="HydrationResult.Repair"/>）；
    /// 未选中候选保持原样。Caller 必须基于 <see cref="HydrationRepairDecision"/> 重建 ContextDecisionResult。
    /// </returns>
    ValueTask<HydrationResult> HydrateAsync(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        CandidateWorkingSet workingSet,
        int tokenBudget = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Late Hydration 执行结果。携带修复后的 WorkingSet、hydrate 计数与预算修复诊断。
/// </summary>
/// <remarks>
/// Caller（DefaultContextDecisionRuntime）将 <see cref="FailedCount"/> / <see cref="BudgetExceeded"/>
/// 合并进 Outcome.Diagnostics；AgentContext 路径对 hard constraint hydrate 失败 fail-closed。
/// <see cref="Repair"/> 携带正式修复决策，Caller 据此重建 ContextDecisionResult
/// （移除 dropped、更新 AllocationDecisions / SelectedCount / EstimatedTokens），不能只修改 WorkingSet。
/// </remarks>
public sealed record HydrationResult
{
    /// <summary>hydrate（+ 预算修复）后的候选工作集。</summary>
    public required CandidateWorkingSet WorkingSet { get; init; }

    /// <summary>成功 hydrate 正文的 Selected 候选数。</summary>
    public required int HydratedCount { get; init; }

    /// <summary>需要 hydrate 但失败的 Selected 候选数（store 未命中 / 读取异常 / 正文为空）。</summary>
    public required int FailedCount { get; init; }

    /// <summary>预算修复后 Selected 候选 TokenCost 总和仍超出预算时为 true。</summary>
    public required bool BudgetExceeded { get; init; }

    /// <summary>预算修复诊断（被裁剪的 Material 列表及原因）；未发生修复时为 null。</summary>
    public IReadOnlyList<string>? BudgetRepairDiagnostics { get; init; }

    /// <summary>
    /// 正式 hydration 修复决策。非空时 Caller 必须基于此重建 ContextDecisionResult，
    /// 不能只替换 WorkingSet（否则 SelectedEnvelopes / AllocationDecisions / Outcome.SelectedCount /
    /// EstimatedTokens 与实际输入不一致）。无 hydrate 发生时（NoHydration 路径）为 null。
    /// </summary>
    public HydrationRepairDecision? Repair { get; init; }
}

/// <summary>
/// Formal hydration repair decision. Returned by hydrator when budget repair is needed.
/// 携带 hydrate 后真实的 selected / dropped 候选 ID、更新的 AllocationDecisions、精确 token 总数与失败明细，
/// 让 Caller（DefaultContextDecisionRuntime）能重建整个 ContextDecisionResult 而非仅替换 WorkingSet。
/// </summary>
/// <remarks>
/// 字段语义：
/// <list type="bullet">
/// <item><see cref="HydratedSelected"/>：hydrate 成功且未被预算修复裁剪的候选 ID（仍保留在 SelectedEnvelopes）。</item>
/// <item><see cref="HydrationDropped"/>：因预算修复裁剪或 hydrate 失败被丢弃的候选 ID（需从 SelectedEnvelopes 移除）。</item>
/// <item><see cref="UpdatedAllocationDecisions"/>：反映 hydrate 后实际结果的分配决策（retained 标 Selected，dropped 标 TokenBudgetExceeded）。</item>
/// <item><see cref="ExactTokenCount"/>：hydrate 后 retained 候选的精确 token 总数（基于真实正文重算，非估算）。</item>
/// <item><see cref="HydrationFailures"/>：hydrate 失败的候选 ID → 错误描述（store 未命中 / 读取异常 / 正文为空）。</item>
/// </list>
/// </remarks>
public sealed record HydrationRepairDecision
{
    /// <summary>Candidates that were successfully hydrated and retained.</summary>
    public required IReadOnlyList<string> HydratedSelected { get; init; }
    /// <summary>Candidates that were dropped due to budget constraints or hydration failure.</summary>
    public required IReadOnlyList<string> HydrationDropped { get; init; }
    /// <summary>Updated allocation decisions reflecting actual hydration results.</summary>
    public required IReadOnlyList<CandidateAllocationDecision> UpdatedAllocationDecisions { get; init; }
    /// <summary>Exact token count after hydration (not estimate).</summary>
    public required int ExactTokenCount { get; init; }
    /// <summary>Failures encountered during hydration (candidate_id -> error).</summary>
    public required IReadOnlyDictionary<string, string> HydrationFailures { get; init; }
}
