using ContextCore.Abstractions;

namespace ContextCore.Abstractions;

// ===========================================================================
// Agent Runtime Registry 契约
//
// 目标：
// 1. 支持同一 process 内注册多个 AgentRuntime（GenericTool / Codex / Claude），
// 按 RuntimeKind 解析对应 adapter。
// 2. Registry 仅负责查找；不持有 session 状态（状态由各 IAgentRuntime 自身管理）。
// 3. Registry 写操作（Register/Unregister）线程安全；读操作（Resolve/GetAll）非阻塞。
//
// 设计边界：
// - 不引入 session 路由逻辑（由 CompositeAgentWorkspaceContextProvider 完成）；
// - 不依赖 DI 容器（可在任意 host 中使用；DI 扩展由外部扩展提供）；
// - 默认实现使用 ConcurrentDictionary；同名 RuntimeKind 后注册覆盖先注册。
// ===========================================================================

/// <summary>
/// Agent Runtime 注册表。支持按 <see cref="AgentRuntimeKind"/> 解析对应 <see cref="IAgentRuntime"/>。
/// </summary>
/// <remarks>
/// 适用于同一 process 内同时使用多个 Agent Runtime（如 GenericTool 用于本地工具型 + Codex 用于生产）的场景。
/// Registry 仅负责查找；不持有 session 状态。
///
/// <b>线程安全</b>：所有方法线程安全。
/// </remarks>
public interface IAgentRuntimeRegistry
{
    /// <summary>注册一个 <see cref="IAgentRuntime"/>。同名 RuntimeKind 后注册覆盖先注册。</summary>
    /// <param name="runtime">要注册的 runtime。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否注册成功（true = 新增；false = 覆盖既有）。</returns>
    Task<bool> RegisterAsync(IAgentRuntime runtime, CancellationToken cancellationToken = default);

    /// <summary>按 <see cref="AgentRuntimeKind"/> 注销 runtime。</summary>
    /// <param name="kind">要注销的 runtime kind。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否注销成功（true = 存在并已移除；false = 不存在）。</returns>
    Task<bool> UnregisterAsync(AgentRuntimeKind kind, CancellationToken cancellationToken = default);

    /// <summary>按 <see cref="AgentRuntimeKind"/> 解析 runtime。</summary>
    /// <param name="kind">要查找的 runtime kind。</param>
    /// <returns>对应的 runtime；不存在返回 null。</returns>
    IAgentRuntime? Resolve(AgentRuntimeKind kind);

    /// <summary>获取所有已注册的 runtime（按 RuntimeKind 排序）。</summary>
    IReadOnlyList<IAgentRuntime> GetAll();

    /// <summary>当前已注册的 runtime 数量。</summary>
    int Count { get; }
}
