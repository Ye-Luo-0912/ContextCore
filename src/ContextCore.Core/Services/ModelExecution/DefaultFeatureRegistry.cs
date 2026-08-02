using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// Feature Registry 默认实现
//
// 目标：
//   把 IFeatureRegistry 契约落到 Core 层，提供可立即注入 DI 的 in-memory 实现。
//
// 设计原则：
//   1. 线程安全：所有读写操作通过 ConcurrentDictionary 保护，key 为 schema 版本号。
//   2. Schema 全局不可变：Register 对已存在的版本号抛 ArgumentException，
//      新版本通过新版本号注册实现（不覆盖、不修改）。
//   3. GetLatest 通过比较 CreatedAt 返回最新注册的 schema；
//      并发注册时通过 snapshot 排序保证返回结果稳定。
//   4. ListAll 返回按 CreatedAt 升序排列的快照（不暴露内部字典引用）。
// ===========================================================================

/// <summary>
/// 默认 Feature Registry（in-memory，线程安全）。
/// </summary>
/// <remarks>
/// 生产部署应替换为持久化实现；契约不变。
/// </remarks>
public sealed class DefaultFeatureRegistry : IFeatureRegistry
{
    // 主键：schema 版本号（区分大小写、按序数字符串比较）。
    private readonly ConcurrentDictionary<string, FeatureSchema> _schemas = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Register(FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (string.IsNullOrWhiteSpace(schema.Version))
        {
            throw new ArgumentException("Schema Version 不能为空。", nameof(schema));
        }

        // insert-if-absent：相同版本号已存在时抛异常，保证不可变语义。
        if (!_schemas.TryAdd(schema.Version, schema))
        {
            throw new InvalidOperationException(
                $"Feature schema 版本 '{schema.Version}' 已注册，不可重复注册。");
        }
    }

    /// <inheritdoc />
    public FeatureSchema? Get(string schemaVersion)
    {
        if (string.IsNullOrEmpty(schemaVersion))
        {
            return null;
        }
        return _schemas.TryGetValue(schemaVersion, out var schema) ? schema : null;
    }

    /// <inheritdoc />
    public FeatureSchema? GetLatest()
    {
        // 无并发写入时单次 snapshot；并发场景下读取到的是某一时刻的一致视图。
        if (_schemas.IsEmpty)
        {
            return null;
        }

        FeatureSchema? latest = null;
        foreach (var kv in _schemas)
        {
            var current = kv.Value;
            if (latest is null || current.CreatedAt > latest.CreatedAt)
            {
                latest = current;
            }
        }
        return latest;
    }

    /// <inheritdoc />
    public IReadOnlyList<FeatureSchema> ListAll()
    {
        // 按 CreatedAt 升序返回快照，避免暴露内部字典引用。
        if (_schemas.IsEmpty)
        {
            return Array.Empty<FeatureSchema>();
        }

        var snapshot = new List<FeatureSchema>(_schemas.Count);
        foreach (var kv in _schemas)
        {
            snapshot.Add(kv.Value);
        }
        snapshot.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return snapshot;
    }
}
