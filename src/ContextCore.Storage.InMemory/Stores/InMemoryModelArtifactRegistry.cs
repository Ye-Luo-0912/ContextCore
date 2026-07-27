using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// P0-6：IModelArtifactRegistry 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 与 PostgresModelArtifactRegistry 实现同一契约，让 FileSystem / InMemory provider
///      下的 Model Control Plane API 仍可注册与查询模型工件描述符（注册数据在进程重启后丢失）。
///   2. 同一 ModelArtifactId 仅允许注册一次（与 Postgres ON CONFLICT DO NOTHING → 抛异常语义一致）。
///   3. GetLatestAsync / ListByVersionAsync 通过 ModelName 字段过滤；按 RegisteredAt 倒序 / 升序排序。
///   4. 线程安全：使用 ConcurrentDictionary 存储，ConcurrentBag 仅用于列举。
/// </remarks>
public sealed class InMemoryModelArtifactRegistry : IModelArtifactRegistry
{
    private readonly ConcurrentDictionary<string, ModelArtifactDescriptor> _descriptors = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask RegisterAsync(ModelArtifactDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_descriptors.TryAdd(descriptor.ModelArtifactId, descriptor))
        {
            throw new InvalidOperationException(
                $"ModelArtifactId '{descriptor.ModelArtifactId}' 已注册，不可重复注册。" +
                "如需发布新版本，请使用新的 ModelArtifactId（与 FeatureSchema 不可变语义一致）。");
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<ModelArtifactDescriptor?> GetAsync(string modelArtifactId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelArtifactId))
        {
            return default;
        }

        _descriptors.TryGetValue(modelArtifactId, out var descriptor);
        return new ValueTask<ModelArtifactDescriptor?>(descriptor);
    }

    /// <inheritdoc />
    public ValueTask<ModelArtifactDescriptor?> GetLatestAsync(string modelName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return default;
        }

        var latest = _descriptors.Values
            .Where(d => string.Equals(d.ModelName, modelName, StringComparison.Ordinal))
            .OrderByDescending(d => d.RegisteredAt)
            .FirstOrDefault();
        return new ValueTask<ModelArtifactDescriptor?>(latest);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListByVersionAsync(string modelName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new ValueTask<IReadOnlyList<ModelArtifactDescriptor>>(Array.Empty<ModelArtifactDescriptor>());
        }

        var list = _descriptors.Values
            .Where(d => string.Equals(d.ModelName, modelName, StringComparison.Ordinal))
            .OrderBy(d => d.RegisteredAt)
            .ToList();
        return new ValueTask<IReadOnlyList<ModelArtifactDescriptor>>(list);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = _descriptors.Values
            .OrderBy(d => d.RegisteredAt)
            .ToList();
        return new ValueTask<IReadOnlyList<ModelArtifactDescriptor>>(list);
    }
}

/// <summary>
/// P0-6：IModelActivationAuditStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresModelActivationAuditStore 实现同一契约，让 FileSystem / InMemory provider
/// 下的 Model Control Plane API 仍可记录审计日志（数据在进程重启后丢失）。
/// append-only：审计记录一旦写入不可修改。
/// </remarks>
public sealed class InMemoryModelActivationAuditStore : IModelActivationAuditStore
{
    private readonly ConcurrentBag<ModelActivationAuditEntry> _entries = new();

    /// <inheritdoc />
    public ValueTask AppendAsync(ModelActivationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(entry);
        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelActivationAuditEntry>> ListByModelAsync(
        string modelArtifactId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelArtifactId))
        {
            return new ValueTask<IReadOnlyList<ModelActivationAuditEntry>>(Array.Empty<ModelActivationAuditEntry>());
        }

        var list = _entries
            .Where(e => string.Equals(e.ModelArtifactId, modelArtifactId, StringComparison.Ordinal))
            .OrderBy(e => e.Timestamp)
            .ToList();
        return new ValueTask<IReadOnlyList<ModelActivationAuditEntry>>(list);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelActivationAuditEntry>> ListAllAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = take > 0 ? take : 100;
        var list = _entries
            .OrderBy(e => e.Timestamp)
            .Take(limit)
            .ToList();
        return new ValueTask<IReadOnlyList<ModelActivationAuditEntry>>(list);
    }
}
