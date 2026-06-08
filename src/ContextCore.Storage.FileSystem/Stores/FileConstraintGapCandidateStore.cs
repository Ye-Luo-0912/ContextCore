using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的约束缺口候选项存储。</summary>
public sealed class FileConstraintGapCandidateStore : IConstraintGapCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileConstraintGapCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileConstraintGapCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task<ConstraintGapCandidate> SaveAsync(
        ConstraintGapCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetConstraintGapCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<ConstraintGapCandidate>(path, cancellationToken).ConfigureAwait(false);
            var duplicate = existing.FirstOrDefault(item => HasSameDedupeKey(item, normalized));
            if (duplicate is not null)
            {
                return Clone(duplicate);
            }

            var updated = existing
                .Where(item => !string.Equals(item.GapId, normalized.GapId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetConstraintGapCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<ConstraintGapCandidate>(path, cancellationToken).ConfigureAwait(false);
                var match = items.FirstOrDefault(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return Clone(match);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ConstraintGapCandidate>();
            foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId))
            {
                var path = _paths.GetConstraintGapCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<ConstraintGapCandidate>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => Matches(item, query)));
            }

            return results
                .OrderByDescending(static item => item.CreatedAt)
                .Skip(Math.Max(0, query.Offset))
                .Take(query.Limit > 0 ? query.Limit : 20)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
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
            foreach (var scope in EnumerateScopes())
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

                var updatedGap = Normalize(new ConstraintGapCandidate
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
                return Clone(updatedGap);
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
        var normalized = Normalize(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetConstraintGapReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<ConstraintGapReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ConstraintGapReviewRecord>();
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetConstraintGapReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<ConstraintGapReviewRecord>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase)));
            }

            return results
                .OrderByDescending(static item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<ShortTermMemoryScope> ResolveScopes(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        return EnumerateScopes()
            .Where(scope => string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateScopes()
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return Array.Empty<ShortTermMemoryScope>();
        }

        return Directory.EnumerateDirectories(workspacesRoot)
            .SelectMany(workspaceDirectory =>
            {
                var workspaceId = Path.GetFileName(workspaceDirectory);
                if (string.IsNullOrWhiteSpace(workspaceId))
                {
                    return Array.Empty<ShortTermMemoryScope>();
                }

                var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
                if (!Directory.Exists(collectionsRoot))
                {
                    return Array.Empty<ShortTermMemoryScope>();
                }

                return Directory.EnumerateDirectories(collectionsRoot)
                    .Select(collectionDirectory => new
                    {
                        WorkspaceId = workspaceId!,
                        CollectionId = Path.GetFileName(collectionDirectory)
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                    .Where(item => File.Exists(_paths.GetConstraintGapCandidatesJsonlPath(item.WorkspaceId, item.CollectionId!)))
                    .Select(item => new ShortTermMemoryScope
                    {
                        WorkspaceId = item.WorkspaceId,
                        CollectionId = item.CollectionId!
                    })
                    .ToArray();
            })
            .DistinctBy(scope => $"{scope.WorkspaceId}\u001f{scope.CollectionId}", StringComparer.OrdinalIgnoreCase)
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

    private static ConstraintGapCandidate Normalize(ConstraintGapCandidate candidate)
    {
        var expectedText = candidate.ExpectedConstraintText.Trim();
        return new ConstraintGapCandidate
        {
            GapId = string.IsNullOrWhiteSpace(candidate.GapId)
                ? BuildGapId(candidate.WorkspaceId, candidate.CollectionId, expectedText, candidate.SourceSampleId)
                : candidate.GapId.Trim(),
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            Source = candidate.Source,
            SourceSampleId = candidate.SourceSampleId,
            SourceOperationId = candidate.SourceOperationId,
            ExpectedConstraintText = expectedText,
            MatchedConstraintIds = candidate.MatchedConstraintIds.ToArray(),
            SuggestedConstraintTitle = candidate.SuggestedConstraintTitle,
            SuggestedConstraintScope = string.IsNullOrWhiteSpace(candidate.SuggestedConstraintScope)
                ? "Collection"
                : candidate.SuggestedConstraintScope,
            SuggestedConstraintType = string.IsNullOrWhiteSpace(candidate.SuggestedConstraintType)
                ? "Hard"
                : candidate.SuggestedConstraintType,
            Severity = string.IsNullOrWhiteSpace(candidate.Severity) ? ConstraintGapSeverity.High : candidate.Severity,
            Reason = candidate.Reason,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            Status = string.IsNullOrWhiteSpace(candidate.Status) ? ConstraintGapStatus.Pending : candidate.Status,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ConstraintGapCandidate Clone(ConstraintGapCandidate candidate) => Normalize(candidate);

    private static ConstraintGapReviewRecord Normalize(ConstraintGapReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new ConstraintGapReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            GapId = record.GapId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SessionId = record.SessionId,
            Action = record.Action,
            FromStatus = string.IsNullOrWhiteSpace(record.FromStatus) ? ConstraintGapStatus.Pending : record.FromStatus,
            ToStatus = string.IsNullOrWhiteSpace(record.ToStatus) ? ConstraintGapStatus.Pending : record.ToStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            CreatedConstraintId = record.CreatedConstraintId,
            TargetItemKind = record.TargetItemKind,
            TargetLayer = record.TargetLayer,
            SourceSampleId = record.SourceSampleId,
            SourceOperationId = record.SourceOperationId,
            ExpectedConstraintText = record.ExpectedConstraintText,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = record.Warnings.ToArray(),
            Errors = record.Errors.ToArray()
        };
    }

    private static ConstraintGapReviewRecord Clone(ConstraintGapReviewRecord record) => Normalize(record);

    private static bool HasSameDedupeKey(ConstraintGapCandidate left, ConstraintGapCandidate right)
    {
        return string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.CollectionId, right.CollectionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SourceSampleId, right.SourceSampleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeText(left.ExpectedConstraintText), NormalizeText(right.ExpectedConstraintText), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGapId(
        string workspaceId,
        string collectionId,
        string expectedConstraintText,
        string sourceSampleId)
    {
        var key = string.Join('\u001f', workspaceId, collectionId, NormalizeText(expectedConstraintText), sourceSampleId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"constraint-gap-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
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
