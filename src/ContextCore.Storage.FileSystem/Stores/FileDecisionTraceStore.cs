using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 将统一决策记录（V17.0 decision trace）持久化为按日期分片的 JSONL 文件。
/// 写入按 (workspace_id, collection_id, decision_id) 稳定主键 Upsert：决策提交 outbox
/// 重放（worker 落库后崩溃、未 Ack → 重新领取重投递）不会重复落库，与 InMemory /
/// Postgres 实现的幂等语义保持一致；同一条决策的更新覆盖旧记录，点查与最近列表唯一。
/// 读改写在同一写锁内完成（FileJsonLineStore.UpsertAsync），避免并发写入互相覆盖。
/// </summary>
public sealed class FileDecisionTraceStore : IDecisionTraceStore
{
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;
    private readonly FileTraceJanitor _janitor;
    private Task? _pendingPurge;

    /// <summary>最近一次 MaybePurge 派发的清理 Task。供测试等待清理完成。</summary>
    internal Task? PendingPurge => _pendingPurge;

    public FileDecisionTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer(), options)
    {
    }

    public FileDecisionTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
        : this(paths, serializer, new FileStorageOptions())
    {
    }

    internal FileDecisionTraceStore(FilePathResolver paths, FileFormatSerializer serializer, FileStorageOptions options)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _janitor = new FileTraceJanitor(options);
    }

    public async Task SaveAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = _paths.GetDecisionTraceJsonlPath(record.WorkspaceId, record.CollectionId);

        // 按稳定主键 Upsert（存在则覆盖，不存在则追加）：重放 / 重投递幂等，
        // 保证任意时刻同一条决策在决策记录平面至多一条。
        await _jsonLines.UpsertAsync(
            path, record, r => r.DecisionId ?? string.Empty, cancellationToken).ConfigureAwait(false);

        // retention 移出 Save 热路径——fire-and-forget，不阻塞写入返回。
        _pendingPurge = _janitor.MaybePurge(_paths.GetDecisionTraceDirectory(record.WorkspaceId, record.CollectionId));
    }

    public async Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var paths = EnumerateTraceFiles(workspaceId, collectionId);
        var records = await TraceQueryHelper.ReadRecentAsync<ContextDecisionRecord>(
            paths,
            take,
            _jsonLines,
            r => r.DecisionId ?? string.Empty,
            r => string.Equals(r.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(r.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

        var count = take > 0 ? take : 50;
        return [.. records.OrderByDescending(item => item.CreatedAt).Take(count)];
    }

    public async Task<ContextDecisionRecord?> GetAsync(
        string workspaceId,
        string collectionId,
        string decisionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(decisionId))
        {
            return null;
        }

        // 稳定主键点查（Decision Evidence Plane：Durable / Point Lookup）。
        var paths = EnumerateTraceFiles(workspaceId, collectionId);
        var records = await TraceQueryHelper.ReadRecentAsync<ContextDecisionRecord>(
            paths,
            int.MaxValue,
            _jsonLines,
            r => r.DecisionId ?? string.Empty,
            r => string.Equals(r.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(r.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(r.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

        return records.FirstOrDefault();
    }

    private IReadOnlyList<string> EnumerateTraceFiles(string workspaceId, string collectionId)
    {
        var files = new List<string>();
        var directory = _paths.GetDecisionTraceDirectory(workspaceId, collectionId);
        if (Directory.Exists(directory))
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        var legacyPath = _paths.GetLegacyDecisionTraceJsonlPath(workspaceId, collectionId);
        if (File.Exists(legacyPath))
        {
            files.Add(legacyPath);
        }

        return files;
    }
}
