using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版反馈特征候选存储；按 candidateId upsert，避免重复导出噪声。</summary>
public sealed class FileLearningFeatureCandidateStore : ILearningFeatureCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileLearningFeatureCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileLearningFeatureCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task UpsertAsync(
        FeedbackFeatureCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            throw new ArgumentException("candidateId is required.", nameof(candidate));
        }

        var workspaceId = ResolveMetadata(candidate, "workspaceId", "default");
        var collectionId = ResolveMetadata(candidate, "collectionId", "test");
        var path = GetCandidatesPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                    path,
                    candidate,
                    static item => item.CandidateId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryAsync(
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var limit = query.Limit > 0 ? query.Limit : 100;
            var offset = Math.Max(0, query.Offset);
            var rows = new List<FeedbackFeatureCandidate>();
            foreach (var path in EnumerateCandidatePaths())
            {
                rows.AddRange(await _jsonLines.ReadAsync<FeedbackFeatureCandidate>(path, cancellationToken)
                    .ConfigureAwait(false));
            }

            return [.. rows
                .Where(item => Matches(query.CandidateId, item.CandidateId))
                .Where(item => Matches(query.SourceFeedbackId, item.SourceFeedbackId))
                .Where(item => Matches(query.CapabilityId, item.CapabilityId))
                .Where(item => Matches(query.TargetType, item.TargetType))
                .Where(item => Matches(query.LabelKind, item.LabelKind))
                .Where(item => Matches(query.TrainingUse, item.TrainingUse))
                .OrderBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Skip(offset)
                .Take(limit)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> EnumerateCandidatePaths()
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
                var path = Path.Combine(collectionDirectory, "learning", "runtime-feedback-feature-candidates.jsonl");
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private string GetCandidatesPath(string workspaceId, string collectionId)
    {
        return Path.Combine(
            _paths.GetLearningDirectory(workspaceId, collectionId),
            "runtime-feedback-feature-candidates.jsonl");
    }

    private static string ResolveMetadata(
        FeedbackFeatureCandidate candidate,
        string key,
        string fallback)
    {
        return candidate.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static bool Matches(string? expected, string actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }
}
