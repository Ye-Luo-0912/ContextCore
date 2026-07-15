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
                var items = await _jsonLines.ReadAsync<ConstraintGapCandidate>(path, cancellationToken).ConfigureAwait(false);
                var existing = items.FirstOrDefault(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    continue;
                }

                var metadata = new Dictionary<string, string>(existing.Metadata, StringComparer.OrdinalIgnoreCase)
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

                var updatedGap = CandidateRecordNormalizer.Normalize(new ConstraintGapCandidate
                {
                    GapId = existing.GapId,
                    WorkspaceId = existing.WorkspaceId,
                    CollectionId = existing.CollectionId,
                    SessionId = existing.SessionId,
                    Source = existing.Source,
                    SourceSampleId = existing.SourceSampleId,
                    SourceOperationId = existing.SourceOperationId,
                    ExpectedConstraintText = existing.ExpectedConstraintText,
                    MatchedConstraintIds = existing.MatchedConstraintIds.ToArray(),
                    SuggestedConstraintTitle = existing.SuggestedConstraintTitle,
                    SuggestedConstraintScope = existing.SuggestedConstraintScope,
                    SuggestedConstraintType = existing.SuggestedConstraintType,
                    Severity = existing.Severity,
                    Reason = existing.Reason,
                    EvidenceRefs = existing.EvidenceRefs.ToArray(),
                    Status = status.Trim(),
                    CreatedAt = existing.CreatedAt,
                    Metadata = metadata
                });
                var updatedItems = items
                    .Where(item => !string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase))
                    .Append(updatedGap)
                    .OrderByDescending(static item => item.CreatedAt)
                    .ToArray();
                await _jsonLines.WriteAsync(path, updatedItems, cancellationToken).ConfigureAwait(false);
                return CandidateRecordNormalizer.Clone(updatedGap);
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
