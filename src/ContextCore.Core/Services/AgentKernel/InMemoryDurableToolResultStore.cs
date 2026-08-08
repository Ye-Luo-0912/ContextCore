using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

/// <summary>
/// 进程内 Durable Tool Result 缓存（开发/测试用）。
/// 维护 toolCallId → <see cref="DurableToolResult"/> 的进程内映射，按 toolCallId 幂等覆盖。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后缓存丢失。
/// 生产部署应注入持久化实现（如 <see cref="ContextCore.Storage.Postgres.Stores.PostgresDurableToolResultStore"/>）。
/// </remarks>
public sealed class InMemoryDurableToolResultStore : IDurableToolResultStore
{
    /// <summary>复合身份键（工作区 + Run + RequestId），与 Postgres 复合主键对齐。</summary>
    private static string Key(TenantRunKey key, string requestId)
        => $"{key.WorkspaceId}\u001f{key.RunId}\u001f{requestId}";

    private readonly ConcurrentDictionary<string, DurableToolResult> _results = new(StringComparer.Ordinal);
    // 按 (workspace_id, run_id, request_id) 复合键索引的结果缓存，作为新主键路径的内存实现。
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
        // 同步写入复合键索引，保持两条索引一致（键从 result 负载提取双键）。
        if (!string.IsNullOrWhiteSpace(result.RequestId))
        {
            _resultsByRequestId[Key(ResultTenantKey(result), result.RequestId)] = result;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DurableToolResult?> GetByRequestIdAsync(TenantRunKey key, string requestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Task.FromResult<DurableToolResult?>(null);
        }

        _resultsByRequestId.TryGetValue(Key(key, requestId), out var result);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task SaveByRequestIdAsync(TenantRunKey key, DurableToolResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.RequestId);
        _resultsByRequestId[Key(key, result.RequestId)] = result;
        // 同步写入 tool_call_id 索引，保持两条索引一致（供旧 GetAsync 路径查询）。
        if (!string.IsNullOrWhiteSpace(result.ToolCallId))
        {
            _results[result.ToolCallId] = result;
        }
        return Task.CompletedTask;
    }

    /// <summary>从结果负载提取复合身份键（旧路径 result 自带双键）。</summary>
    private static TenantRunKey ResultTenantKey(DurableToolResult result)
        => new(result.WorkspaceId ?? string.Empty, result.RunId ?? string.Empty);
}
