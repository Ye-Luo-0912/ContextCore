using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// DefaultAgentRuntimeRegistry — Agent Runtime 注册表默认实现。
//
// 实现 IAgentRuntimeRegistry 契约：
//   - ConcurrentDictionary<AgentRuntimeKind, IAgentRuntime> 后端存储
//   - Register 后注册覆盖先注册（TryUpdate 语义）
//   - Resolve / GetAll 非阻塞读取
//   - 不持有 session 状态
// ===========================================================================

/// <summary>
/// <see cref="IAgentRuntimeRegistry"/> 的默认实现。
/// </summary>
/// <remarks>
/// 使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 后端；线程安全。
/// 后注册覆盖先注册（同 RuntimeKind）。
/// </remarks>
public sealed class DefaultAgentRuntimeRegistry : IAgentRuntimeRegistry
{
    private readonly ConcurrentDictionary<AgentRuntimeKind, IAgentRuntime> _runtimes = new();

    /// <inheritdoc />
    public Task<bool> RegisterAsync(IAgentRuntime runtime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        cancellationToken.ThrowIfCancellationRequested();

        // 后注册覆盖先注册：AddOrUpdate 原子操作
        var wasNew = !_runtimes.ContainsKey(runtime.RuntimeKind);
        _runtimes[runtime.RuntimeKind] = runtime;
        return Task.FromResult(wasNew);
    }

    /// <inheritdoc />
    public Task<bool> UnregisterAsync(AgentRuntimeKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_runtimes.TryRemove(kind, out _));
    }

    /// <inheritdoc />
    public IAgentRuntime? Resolve(AgentRuntimeKind kind)
    {
        return _runtimes.TryGetValue(kind, out var runtime) ? runtime : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentRuntime> GetAll()
    {
        // 按 RuntimeKind 排序保证返回顺序稳定
        return _runtimes.Values
            .OrderBy(r => (byte)r.RuntimeKind)
            .ToList();
    }

    /// <inheritdoc />
    public int Count => _runtimes.Count;
}
