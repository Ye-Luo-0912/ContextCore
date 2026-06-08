using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的学习记录与案例存储。</summary>
public sealed class FileContextLearningStore : IContextLearningStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileContextLearningStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextLearningStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task AddFeedbackAsync(PromotionFeedbackSignal feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var normalized = Normalize(feedback);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetLearningFeedbackJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<PromotionFeedbackSignal>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.FeedbackId, normalized.FeedbackId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PromotionFeedbackSignal>> QueryFeedbackAsync(
        PromotionFeedbackSignalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<PromotionFeedbackSignal>();
            foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId, LearningScopeKind.Feedback))
            {
                var path = _paths.GetLearningFeedbackJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var feedback = await _jsonLines.ReadAsync<PromotionFeedbackSignal>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(feedback.Where(item => Matches(item, query)));
            }

            return results
                .OrderByDescending(item => item.CreatedAt)
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

    public async Task AddRecordAsync(ContextLearningRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetLearningRecordsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<ContextLearningRecord>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.RecordId, normalized.RecordId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextLearningRecord?> GetRecordAsync(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateLearningScopes(LearningScopeKind.Record))
            {
                var path = _paths.GetLearningRecordsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var records = await _jsonLines.ReadAsync<ContextLearningRecord>(path, cancellationToken).ConfigureAwait(false);
                var match = records.FirstOrDefault(item => string.Equals(item.RecordId, recordId, StringComparison.OrdinalIgnoreCase));
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

    public async Task<IReadOnlyList<ContextLearningRecord>> QueryRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ContextLearningRecord>();
            foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId, LearningScopeKind.Record))
            {
                var path = _paths.GetLearningRecordsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var records = await _jsonLines.ReadAsync<ContextLearningRecord>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(records.Where(record => Matches(record, query)));
            }

            return results
                .OrderByDescending(record => record.CreatedAt)
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

    public async Task<ContextLearningCase> AddCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        var normalized = Normalize(learningCase);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetLearningCasesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<ContextLearningCase>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.CaseId, normalized.CaseId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextLearningCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateLearningScopes(LearningScopeKind.Case))
            {
                var path = _paths.GetLearningCasesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var cases = await _jsonLines.ReadAsync<ContextLearningCase>(path, cancellationToken).ConfigureAwait(false);
                var match = cases.FirstOrDefault(item => string.Equals(item.CaseId, caseId, StringComparison.OrdinalIgnoreCase));
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

    public async Task<IReadOnlyList<ContextLearningCase>> QueryCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ContextLearningCase>();
            foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId, LearningScopeKind.Case))
            {
                var path = _paths.GetLearningCasesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var cases = await _jsonLines.ReadAsync<ContextLearningCase>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(cases.Where(learningCase => Matches(learningCase, query)));
            }

            return results
                .OrderByDescending(learningCase => learningCase.CreatedAt)
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

    private IReadOnlyList<ShortTermMemoryScope> ResolveScopes(string? workspaceId, string? collectionId, LearningScopeKind scopeKind)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        return EnumerateLearningScopes(scopeKind)
            .Where(scope => string.IsNullOrWhiteSpace(workspaceId) || string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(scope => string.IsNullOrWhiteSpace(collectionId) || string.Equals(scope.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateLearningScopes(LearningScopeKind scopeKind)
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
                    .Where(item => File.Exists(ResolvePath(item.WorkspaceId, item.CollectionId!, scopeKind)))
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

    private string ResolvePath(string workspaceId, string collectionId, LearningScopeKind scopeKind)
    {
        return scopeKind switch
        {
            LearningScopeKind.Feedback => _paths.GetLearningFeedbackJsonlPath(workspaceId, collectionId),
            LearningScopeKind.Record => _paths.GetLearningRecordsJsonlPath(workspaceId, collectionId),
            _ => _paths.GetLearningCasesJsonlPath(workspaceId, collectionId)
        };
    }

    private static bool Matches(ContextLearningRecord record, ContextLearningRecordQuery query)
    {
        return (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.Equals(record.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(record.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(record.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (query.Signal is null || record.Signal == query.Signal.Value)
            && (query.FailureType is null || record.FailureType == query.FailureType.Value)
            && (string.IsNullOrWhiteSpace(query.SourceKind) || string.Equals(record.SourceKind, query.SourceKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SourceId) || string.Equals(record.SourceId, query.SourceId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(PromotionFeedbackSignal feedback, PromotionFeedbackSignalQuery query)
    {
        return (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.Equals(feedback.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(feedback.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(feedback.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.CandidateId) || string.Equals(feedback.CandidateId, query.CandidateId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Action) || string.Equals(feedback.Action, query.Action, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(ContextLearningCase learningCase, ContextLearningCaseQuery query)
    {
        return (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.Equals(learningCase.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(learningCase.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(learningCase.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (query.Signal is null || learningCase.Signal == query.Signal.Value)
            && (query.FailureType is null || learningCase.FailureType == query.FailureType.Value)
            && (query.Status is null || learningCase.Status == query.Status.Value)
            && (string.IsNullOrWhiteSpace(query.CaseKind) || string.Equals(learningCase.CaseKind, query.CaseKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SourceRecordId) || string.Equals(learningCase.SourceRecordId, query.SourceRecordId, StringComparison.OrdinalIgnoreCase));
    }

    private static ContextLearningRecord Normalize(ContextLearningRecord record)
    {
        return new ContextLearningRecord
        {
            RecordId = string.IsNullOrWhiteSpace(record.RecordId) ? Guid.NewGuid().ToString("N") : record.RecordId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SessionId = record.SessionId,
            SourceKind = record.SourceKind,
            SourceId = record.SourceId,
            CandidateId = record.CandidateId,
            ReviewId = record.ReviewId,
            EventKind = record.EventKind,
            Signal = record.Signal,
            FailureType = record.FailureType,
            Reason = record.Reason,
            Confidence = record.Confidence,
            Importance = record.Importance,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static PromotionFeedbackSignal Normalize(PromotionFeedbackSignal feedback)
    {
        return new PromotionFeedbackSignal
        {
            FeedbackId = string.IsNullOrWhiteSpace(feedback.FeedbackId) ? Guid.NewGuid().ToString("N") : feedback.FeedbackId,
            CandidateId = feedback.CandidateId,
            WorkspaceId = feedback.WorkspaceId,
            CollectionId = feedback.CollectionId,
            SessionId = feedback.SessionId,
            Action = feedback.Action,
            Reviewer = feedback.Reviewer,
            Reason = feedback.Reason,
            SourceWorkingItemId = feedback.SourceWorkingItemId,
            CreatedTargetItemId = feedback.CreatedTargetItemId,
            SuggestedTargetLayer = feedback.SuggestedTargetLayer,
            ActualTargetLayer = feedback.ActualTargetLayer,
            Confidence = feedback.Confidence,
            Importance = feedback.Importance,
            EvidenceRefs = feedback.EvidenceRefs.ToArray(),
            CreatedAt = feedback.CreatedAt == default ? DateTimeOffset.UtcNow : feedback.CreatedAt,
            Metadata = new Dictionary<string, string>(feedback.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ContextLearningCase Normalize(ContextLearningCase learningCase)
    {
        return new ContextLearningCase
        {
            CaseId = string.IsNullOrWhiteSpace(learningCase.CaseId) ? Guid.NewGuid().ToString("N") : learningCase.CaseId,
            SourceType = learningCase.SourceType,
            WorkspaceId = learningCase.WorkspaceId,
            CollectionId = learningCase.CollectionId,
            SessionId = learningCase.SessionId,
            SourceRecordId = learningCase.SourceRecordId,
            SourceKind = learningCase.SourceKind,
            SourceId = learningCase.SourceId,
            CaseKind = learningCase.CaseKind,
            Title = learningCase.Title,
            Summary = learningCase.Summary,
            InputSummary = learningCase.InputSummary,
            ExpectedBehavior = learningCase.ExpectedBehavior,
            Signal = learningCase.Signal,
            FailureType = learningCase.FailureType,
            CorrectionReason = learningCase.CorrectionReason,
            Status = learningCase.Status,
            EvidenceRefs = learningCase.EvidenceRefs.ToArray(),
            PositiveRefs = learningCase.PositiveRefs.ToArray(),
            NegativeRefs = learningCase.NegativeRefs.ToArray(),
            CreatedAt = learningCase.CreatedAt == default ? DateTimeOffset.UtcNow : learningCase.CreatedAt,
            Metadata = new Dictionary<string, string>(learningCase.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static PromotionFeedbackSignal Clone(PromotionFeedbackSignal feedback) => Normalize(feedback);

    private static ContextLearningRecord Clone(ContextLearningRecord record) => Normalize(record);

    private static ContextLearningCase Clone(ContextLearningCase learningCase) => Normalize(learningCase);

    private enum LearningScopeKind
    {
        Feedback,
        Record,
        Case
    }
}
