using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版反馈审核记录存储；按 feedbackId upsert，避免重复审核噪声。</summary>
public sealed class FileLearningFeedbackReviewStore : ILearningFeedbackReviewStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileLearningFeedbackReviewStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileLearningFeedbackReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task UpsertAsync(
        LearningFeedbackReviewRecord review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (string.IsNullOrWhiteSpace(review.FeedbackId))
        {
            throw new ArgumentException("feedbackId is required.", nameof(review));
        }

        var workspaceId = ResolveMetadata(review, "workspaceId", "default");
        var collectionId = ResolveMetadata(review, "collectionId", "test");
        var path = GetReviewsPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                    path,
                    review,
                    static item => item.FeedbackId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var limit = query.Limit > 0 ? query.Limit : 100;
            var offset = Math.Max(0, query.Offset);
            var rows = new List<LearningFeedbackReviewRecord>();
            foreach (var path in EnumerateReviewPaths())
            {
                rows.AddRange(await _jsonLines.ReadAsync<LearningFeedbackReviewRecord>(path, cancellationToken)
                    .ConfigureAwait(false));
            }

            return [.. rows
                .Where(item => Matches(query.FeedbackId, item.FeedbackId))
                .Where(item => query.ReviewStatus is null || item.ReviewStatus == query.ReviewStatus)
                .Where(item => Matches(query.Reviewer, item.Reviewer))
                .OrderByDescending(item => item.ReviewedAt)
                .Skip(offset)
                .Take(limit)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> EnumerateReviewPaths()
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            yield break;
        }

        foreach (var workspaceDirectory in Directory.EnumerateDirectories(workspacesRoot))
        {
            var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
            if (!Directory.Exists(collectionsRoot))
            {
                continue;
            }

            foreach (var collectionDirectory in Directory.EnumerateDirectories(collectionsRoot))
            {
                var path = Path.Combine(collectionDirectory, "learning", "runtime-feedback-reviews.jsonl");
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private string GetReviewsPath(string workspaceId, string collectionId)
    {
        return Path.Combine(
            _paths.GetLearningDirectory(workspaceId, collectionId),
            "runtime-feedback-reviews.jsonl");
    }

    private static string ResolveMetadata(
        LearningFeedbackReviewRecord review,
        string key,
        string fallback)
    {
        return review.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static bool Matches(string? expected, string actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }
}
