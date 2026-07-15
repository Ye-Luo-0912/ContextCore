using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的学习记录与案例存储。</summary>
public sealed class FileContextLearningStore : IContextLearningStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileContextLearningStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextLearningStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task AddFeedbackAsync(PromotionFeedbackSignal feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var normalized = LearningRecordNormalizer.Normalize(feedback);

        var path = _paths.GetLearningFeedbackJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<PromotionFeedbackSignal>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.FeedbackId, normalized.FeedbackId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionFeedbackSignal>> QueryFeedbackAsync(
        PromotionFeedbackSignalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<PromotionFeedbackSignal>();
        foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetLearningFeedbackJsonlPath))
        {
            var path = _paths.GetLearningFeedbackJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var feedback = await _jsonLines.ReadAsync<PromotionFeedbackSignal>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(feedback.Where(item => Matches(item, query)));
        }

        return results
            .OrderByDescending(item => item.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(LearningRecordNormalizer.Clone)
            .ToArray();
    }

    public async Task AddRecordAsync(ContextLearningRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = LearningRecordNormalizer.Normalize(record);

        var path = _paths.GetLearningRecordsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<ContextLearningRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.RecordId, normalized.RecordId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningRecord?> GetRecordAsync(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetLearningRecordsJsonlPath))
        {
            var path = _paths.GetLearningRecordsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var records = await _jsonLines.ReadAsync<ContextLearningRecord>(path, cancellationToken).ConfigureAwait(false);
            var match = records.FirstOrDefault(item => string.Equals(item.RecordId, recordId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return LearningRecordNormalizer.Clone(match);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ContextLearningRecord>> QueryRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<ContextLearningRecord>();
        foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetLearningRecordsJsonlPath))
        {
            var path = _paths.GetLearningRecordsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var records = await _jsonLines.ReadAsync<ContextLearningRecord>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(records.Where(record => Matches(record, query)));
        }

        return results
            .OrderByDescending(record => record.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(LearningRecordNormalizer.Clone)
            .ToArray();
    }

    public async Task<ContextLearningCase> AddCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        var normalized = LearningRecordNormalizer.Normalize(learningCase);

        var path = _paths.GetLearningCasesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<ContextLearningCase>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.CaseId, normalized.CaseId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
        return LearningRecordNormalizer.Clone(normalized);
    }

    public async Task<ContextLearningCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetLearningCasesJsonlPath))
        {
            var path = _paths.GetLearningCasesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var cases = await _jsonLines.ReadAsync<ContextLearningCase>(path, cancellationToken).ConfigureAwait(false);
            var match = cases.FirstOrDefault(item => string.Equals(item.CaseId, caseId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return LearningRecordNormalizer.Clone(match);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ContextLearningCase>> QueryCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<ContextLearningCase>();
        foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetLearningCasesJsonlPath))
        {
            var path = _paths.GetLearningCasesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var cases = await _jsonLines.ReadAsync<ContextLearningCase>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(cases.Where(learningCase => Matches(learningCase, query)));
        }

        return results
            .OrderByDescending(learningCase => learningCase.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(LearningRecordNormalizer.Clone)
            .ToArray();
    }

    private IReadOnlyList<ShortTermMemoryScope> ResolveScopes(string? workspaceId, string? collectionId, Func<string, string, string> pathSelector)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        return _scopeCatalog.EnumerateScopes(pathSelector)
            .Where(scope => string.IsNullOrWhiteSpace(workspaceId) || string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(scope => string.IsNullOrWhiteSpace(collectionId) || string.Equals(scope.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
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
}
