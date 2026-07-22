using ContextCore.Abstractions;

namespace ContextCore.Abstractions;

// ===========================================================================
// R24-2：Agent Task State Store 契约
//
// 目标（对齐 R24 规格）：
//   1. 持久化 AgentTaskState（R23-2 定义），支持跨 turn / 跨请求恢复任务状态。
//   2. 主键 (workspace_id, task_id) + 按 SessionId 查询；
//   3. 失败语义：SaveAsync 幂等（同主键覆盖）；GetAsync 不存在返回 null。
//
// 设计边界：
//   - Store 仅负责持久化；不负责状态机转换（如 Pending→Running→Completed）；
//   - 状态机由调用方维护，Store 仅保存最终状态；
//   - 默认实现使用 ConcurrentDictionary（in-memory）；生产实现应替换为 Postgres store。
//   - P0-6 修复：GetAsync / DeleteAsync 必须传 workspaceId 以保证跨 workspace 隔离。
//     主键为 (workspace_id, task_id)；不允许只按 taskId 查询，避免跨 workspace 误读 / 误删。
// ===========================================================================

/// <summary>
/// R24-2：Agent 任务状态存储。持久化 <see cref="AgentTaskState"/> 以支持跨请求恢复。
/// </summary>
/// <remarks>
/// 适用于需要跨 turn 持久化任务状态的场景（如长任务恢复、断点续传）。
/// Store 不负责状态机转换；仅保存/读取。
/// </remarks>
public interface IAgentTaskStateStore
{
    /// <summary>保存或更新任务状态（同 (workspaceId, taskId) 主键覆盖）。</summary>
    /// <param name="taskState">任务状态（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveAsync(AgentTaskState taskState, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 (workspaceId, taskId) 获取任务状态。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（与主键组合；P0-6 修复）。</param>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务状态；不存在或跨 workspace 不可见时返回 null。</returns>
    Task<AgentTaskState?> GetAsync(
        string workspaceId,
        string taskId,
        CancellationToken cancellationToken = default);

    /// <summary>按 SessionId 列出所有任务状态。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该 session 下的所有任务状态（按 UpdatedAt 倒序）。</returns>
    Task<IReadOnlyList<AgentTaskState>> ListBySessionAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 (workspaceId, taskId) 删除任务状态。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（与主键组合；P0-6 修复）。</param>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否删除成功（true = 存在并已删除；false = 不存在或跨 workspace 不可见）。</returns>
    Task<bool> DeleteAsync(
        string workspaceId,
        string taskId,
        CancellationToken cancellationToken = default);
}
