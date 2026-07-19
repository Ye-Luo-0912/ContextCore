using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的约束缺口候选项存储。</summary>
public sealed class FileConstraintGapCandidateStore : IConstraintGapCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileConstraintGapCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileConstraintGapCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task<ConstraintGapCandidate> SaveAsync(
        ConstraintGapCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = CandidateRecordNormalizer.Normalize(candidate);
        var path = _paths.GetConstraintGapCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        ConstraintGapCandidate? duplicate = null;

        await _jsonLines.UpdateAsync<ConstraintGapCandidate>(
            path,
            existing =>
            {
                duplicate = existing.FirstOrDefault(item => HasSameDedupeKey(item, normalized));
                if (duplicate is not null)
                {
                    return existing;
                }

                return existing
                    .Where(item => !string.Equals(item.GapId, normalized.GapId, StringComparison.OrdinalIgnoreCase))
                    .Append(normalized)
                    .OrderByDescending(static item => item.CreatedAt)
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);

        return duplicate is not null ? CandidateRecordNormalizer.Clone(duplicate) : CandidateRecordNormalizer.Clone(normalized);
    }

    public async Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);

        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetConstraintGapCandidatesJsonlPath))
        {
            var path = _paths.GetConstraintGapCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<ConstraintGapCandidate>(path, cancellationToken).ConfigureAwait(false);
            var match = items.FirstOrDefault(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return CandidateRecordNormalizer.Clone(match);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<ConstraintGapCandidate>();
        foreach (var scope in _scopeCatalog.ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetConstraintGapCandidatesJsonlPath))
        {
            var path = _paths.GetConstraintGapCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<ConstraintGapCandidate>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => Matches(item, query)));
        }

        return results
            .OrderByDescending(static item => item.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(CandidateRecordNormalizer.Clone)
            .ToArray();
    }

    public async Task<ConstraintGapCandidate?> UpdateStatusAsync(
        string gapId,
        string status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetConstraintGapCandidatesJsonlPath))
            {
                var path = _paths.GetConstraintGapCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                ConstraintGapCandidate? updatedGap = null;

                // P1-1: TryUpdateAsync 在跨进程锁内 RMW——未匹配到 gapId 时返回 null 跳过写入，
                // 避免对未创建/未变更的 candidates.jsonl 创建空文件。
                var written = await _jsonLines.TryUpdateAsync<ConstraintGapCandidate>(
                    path,
                    existing =>
                    {
                        var match = existing.FirstOrDefault(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase));
                        if (match is null)
                        {
                            return null;
                        }

                        var metadata = new Dictionary<string, string>(match.Metadata, StringComparer.OrdinalIgnoreCase)
                        {
                            ["lastReviewStatus"] = status,
                            ["lastReviewedAt"] = DateTimeOffset.UtcNow.ToString("O")
                        };
                        if (!string.IsNullOrWhiteSpace(reviewer))
                        {
                            metadata["lastReviewer"] = reviewer.Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            metadata["lastReviewReason"] = reason.Trim();
                        }

                        updatedGap = CandidateRecordNormalizer.Normalize(new ConstraintGapCandidate
                        {
                            GapId = match.GapId,
                            WorkspaceId = match.WorkspaceId,
                            CollectionId = match.CollectionId,
                            SessionId = match.SessionId,
                            Source = match.Source,
                            SourceSampleId = match.SourceSampleId,
                            SourceOperationId = match.SourceOperationId,
                            ExpectedConstraintText = match.ExpectedConstraintText,
                            MatchedConstraintIds = match.MatchedConstraintIds.ToArray(),
                            SuggestedConstraintTitle = match.SuggestedConstraintTitle,
                            SuggestedConstraintScope = match.SuggestedConstraintScope,
                            SuggestedConstraintType = match.SuggestedConstraintType,
                            Severity = match.Severity,
                            Reason = match.Reason,
                            EvidenceRefs = match.EvidenceRefs.ToArray(),
                            Status = status.Trim(),
                            CreatedAt = match.CreatedAt,
                            Metadata = metadata
                        });
                        return existing
                            .Where(item => !string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase))
                            .Append(updatedGap)
                            .OrderByDescending(static item => item.CreatedAt)
                            .ToArray();
                    },
                    cancellationToken).ConfigureAwait(false);

                if (written && updatedGap is not null)
                {
                    return CandidateRecordNormalizer.Clone(updatedGap);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendReviewAsync(
        ConstraintGapReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);
        var path = _paths.GetConstraintGapReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);

        await _jsonLines.UpdateAsync<ConstraintGapReviewRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);

        var results = new List<ConstraintGapReviewRecord>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetConstraintGapCandidatesJsonlPath))
        {
            var path = _paths.GetConstraintGapReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<ConstraintGapReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase)));
        }

        return results
            .OrderByDescending(static item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();
    }

    private static bool Matches(ConstraintGapCandidate item, ConstraintGapCandidateQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Source) || string.Equals(item.Source, query.Source, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SourceSampleId) || string.Equals(item.SourceSampleId, query.SourceSampleId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(item.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Severity) || string.Equals(item.Severity, query.Severity, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSameDedupeKey(ConstraintGapCandidate left, ConstraintGapCandidate right)
    {
        return string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.CollectionId, right.CollectionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SourceSampleId, right.SourceSampleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeText(left.ExpectedConstraintText), NormalizeText(right.ExpectedConstraintText), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
