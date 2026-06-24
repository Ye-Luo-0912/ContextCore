using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版运行时反馈事件存储；按 feedbackId upsert，避免重复采集噪声。</summary>
public sealed class FileLearningFeedbackStore : ILearningFeedbackStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileLearningFeedbackStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileLearningFeedbackStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedbackId))
        {
            return null;
        }

        var rows = await ReadRowsAsync(null, null, cancellationToken)
            .ConfigureAwait(false);
        return rows.FirstOrDefault(item =>
            string.Equals(item.FeedbackId, feedbackId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedbackEvent);
        if (string.IsNullOrWhiteSpace(feedbackEvent.FeedbackId))
        {
            throw new ArgumentException("feedbackId is required.", nameof(feedbackEvent));
        }

        if (string.IsNullOrWhiteSpace(feedbackEvent.WorkspaceId)
            || string.IsNullOrWhiteSpace(feedbackEvent.CollectionId))
        {
            throw new ArgumentException("workspaceId and collectionId are required.", nameof(feedbackEvent));
        }

        var path = _paths.GetRuntimeLearningFeedbackJsonlPath(
            feedbackEvent.WorkspaceId,
            feedbackEvent.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                    path,
                    feedbackEvent,
                    static item => item.FeedbackId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var limit = query.Limit > 0 ? query.Limit : 100;
            var offset = Math.Max(0, query.Offset);
            var rows = await ReadRowsUnsafeAsync(query.WorkspaceId, query.CollectionId, cancellationToken)
                .ConfigureAwait(false);
            return [.. rows
                .Where(item => Matches(query.WorkspaceId, item.WorkspaceId))
                .Where(item => Matches(query.CollectionId, item.CollectionId))
                .Where(item => Matches(query.Source, item.Source))
                .Where(item => Matches(query.SourceOperationId, item.SourceOperationId))
                .Where(item => Matches(query.CapabilityId, item.CapabilityId))
                .Where(item => Matches(query.TargetId, item.TargetId))
                .Where(item => Matches(query.TargetType, item.TargetType))
                .Where(item => Matches(query.FeedbackKind, item.FeedbackKind))
                .OrderByDescending(item => item.CreatedAt)
                .Skip(offset)
                .Take(limit)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<LearningFeedbackEvent>> ReadRowsAsync(
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadRowsUnsafeAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<LearningFeedbackEvent>> ReadRowsUnsafeAsync(
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var rows = new List<LearningFeedbackEvent>();
        foreach (var path in EnumerateFeedbackPaths(workspaceId, collectionId))
        {
            rows.AddRange(await _jsonLines.ReadAsync<LearningFeedbackEvent>(path, cancellationToken)
                .ConfigureAwait(false));
        }

        return rows;
    }

    private IEnumerable<string> EnumerateFeedbackPaths(string? workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(collectionId))
        {
            yield return _paths.GetRuntimeLearningFeedbackJsonlPath(workspaceId, collectionId);
            yield break;
        }

        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            yield break;
        }

        foreach (var workspaceDirectory in Directory.EnumerateDirectories(workspacesRoot))
        {
            var currentWorkspaceId = Path.GetFileName(workspaceDirectory);
            if (!Matches(workspaceId, currentWorkspaceId))
            {
                continue;
            }

            var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
            if (!Directory.Exists(collectionsRoot))
            {
                continue;
            }

            foreach (var collectionDirectory in Directory.EnumerateDirectories(collectionsRoot))
            {
                var currentCollectionId = Path.GetFileName(collectionDirectory);
                if (!Matches(collectionId, currentCollectionId))
                {
                    continue;
                }

                var path = Path.Combine(collectionDirectory, "learning", "runtime-feedback-events.jsonl");
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static bool Matches(string? expected, string? actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }
}
