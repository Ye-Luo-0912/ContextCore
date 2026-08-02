using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// 默认 <see cref="IAgentObservationSource"/>：内存指标源，
/// 支持外部通过 <see cref="RecordMetricsAsync"/> 写入指标，供 <see cref="DefaultContextEvolutionAgent"/> 在测试与离线诊断中读取。
/// </summary>
/// <remarks>
/// 不依赖任何运行时 telemetry sink；生产部署时可替换为基于 OpenTelemetry / metrics registry 的实现。
/// 线程安全：使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 保护内部状态。
/// </remarks>
public sealed class DefaultAgentObservationSource : IAgentObservationSource
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, double>> _metricsByWorkspace = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _collectionByWorkspace = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造默认 observation source。
    /// </summary>
    /// <param name="sourceId">观察源标识（如 "telemetry:default"、"benchmark:package-build"）。</param>
    public DefaultAgentObservationSource(string sourceId = "telemetry:default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceId = sourceId;
    }

    /// <inheritdoc />
    public string SourceId { get; }

    /// <summary>
    /// 写入指定 workspace 的指标快照；后续 <see cref="ObserveAsync"/> 返回最近一次写入。
    /// </summary>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="collectionId">集合 ID（可选；非空时同时记录，便于审计）。</param>
    /// <param name="metrics">指标字典（metric_name → value）。</param>
    /// <param name="cancellationToken">取消令牌（未使用，保留契约兼容）。</param>
    public Task RecordMetricsAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyDictionary<string, double> metrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(metrics);
        cancellationToken.ThrowIfCancellationRequested();
        _metricsByWorkspace[workspaceId] = metrics;
        if (collectionId is not null)
        {
            _collectionByWorkspace[workspaceId] = collectionId;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, double>> ObserveAsync(
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();
        if (_metricsByWorkspace.TryGetValue(workspaceId, out var metrics))
        {
            return Task.FromResult(metrics);
        }
        return Task.FromResult<IReadOnlyDictionary<string, double>>(EmptyMetrics);
    }

    private static readonly IReadOnlyDictionary<string, double> EmptyMetrics =
        new Dictionary<string, double>(StringComparer.Ordinal).AsReadOnly();
}
