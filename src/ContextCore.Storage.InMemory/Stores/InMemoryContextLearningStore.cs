using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的学习记录存储，适用于测试和短生命周期运行。</summary>
public sealed class InMemoryContextLearningStore : IContextLearningStore
{
    private readonly ConcurrentDictionary<string, PromotionFeedbackSignal> _feedback = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ContextLearningRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ContextLearningCase> _cases = new(StringComparer.OrdinalIgnoreCase);

    public Task AddFeedbackAsync(PromotionFeedbackSignal feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = LearningRecordNormalizer.Normalize(feedback);
        _feedback[normalized.FeedbackId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PromotionFeedbackSignal>> QueryFeedbackAsync(
        PromotionFeedbackSignalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _feedback.Values
            .Where(feedback => Matches(feedback, query))
            .OrderByDescending(static feedback => feedback.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(LearningRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<PromotionFeedbackSignal>>(results);
    }

    public Task AddRecordAsync(ContextLearningRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = LearningRecordNormalizer.Normalize(record);
        _records[normalized.RecordId] = normalized;
        return Task.CompletedTask;
    }

    public Task<ContextLearningRecord?> GetRecordAsync(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_records.TryGetValue(recordId, out var record) ? LearningRecordNormalizer.Clone(record) : null);
    }

    public Task<IReadOnlyList<ContextLearningRecord>> QueryRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _records.Values
            .Where(record => Matches(record, query))
            .OrderByDescending(static record => record.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(LearningRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextLearningRecord>>(results);
    }

    public Task<ContextLearningCase> AddCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = LearningRecordNormalizer.Normalize(learningCase);
        _cases[normalized.CaseId] = normalized;
        return Task.FromResult(LearningRecordNormalizer.Clone(normalized));
    }

    public Task<ContextLearningCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_cases.TryGetValue(caseId, out var learningCase) ? LearningRecordNormalizer.Clone(learningCase) : null);
    }

    public Task<IReadOnlyList<ContextLearningCase>> QueryCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _cases.Values
            .Where(learningCase => Matches(learningCase, query))
            .OrderByDescending(static learningCase => learningCase.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(LearningRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextLearningCase>>(results);
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
