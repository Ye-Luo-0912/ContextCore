using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

/// <summary>
/// P0-3：进程内 Durable Tool Result 缓存（开发/测试用）。
/// 维护 toolCallId → <see cref="DurableToolResult"/> 的进程内映射，按 toolCallId 幂等覆盖。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后缓存丢失。
/// 生产部署应注入持久化实现（如 <see cref="ContextCore.Storage.Postgres.Stores.PostgresDurableToolResultStore"/>）。
/// </remarks>
public sealed class InMemoryDurableToolResultStore : IDurableToolResultStore
{
    private readonly ConcurrentDictionary<string, DurableToolResult> _results = new(StringComparer.Ordinal);
    // P0-4：按 request_id（稳定调用身份）索引的结果缓存，作为新主键路径的内存实现。
    private readonly ConcurrentDictionary<string, DurableToolResult> _resultsByRequestId = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<DurableToolResult?> GetAsync(string toolCallId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return Task.FromResult<DurableToolResult?>(null);
        }

        _results.TryGetValue(toolCallId, out var result);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task SaveAsync(string toolCallId, DurableToolResult result, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(result);
        _results[toolCallId] = result;
        // P0-4：同步写入 request_id 索引，保持两条索引一致。
        if (!string.IsNullOrWhiteSpace(result.RequestId))
        {
            _resultsByRequestId[result.RequestId] = result;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DurableToolResult?> GetByRequestIdAsync(string requestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Task.FromResult<DurableToolResult?>(null);
        }

        _resultsByRequestId.TryGetValue(requestId, out var result);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task SaveByRequestIdAsync(DurableToolResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.RequestId);
        _resultsByRequestId[result.RequestId] = result;
        // P0-4：同步写入 tool_call_id 索引，保持两条索引一致（供旧 GetAsync 路径查询）。
        if (!string.IsNullOrWhiteSpace(result.ToolCallId))
        {
            _results[result.ToolCallId] = result;
        }
        return Task.CompletedTask;
    }
}
