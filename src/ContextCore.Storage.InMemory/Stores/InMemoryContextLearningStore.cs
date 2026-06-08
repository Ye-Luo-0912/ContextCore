using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

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

        var normalized = Normalize(feedback);
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
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<PromotionFeedbackSignal>>(results);
    }

    public Task AddRecordAsync(ContextLearningRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(record);
        _records[normalized.RecordId] = normalized;
        return Task.CompletedTask;
    }

    public Task<ContextLearningRecord?> GetRecordAsync(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_records.TryGetValue(recordId, out var record) ? Clone(record) : null);
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
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextLearningRecord>>(results);
    }

    public Task<ContextLearningCase> AddCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(learningCase);
        _cases[normalized.CaseId] = normalized;
        return Task.FromResult(Clone(normalized));
    }

    public Task<ContextLearningCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_cases.TryGetValue(caseId, out var learningCase) ? Clone(learningCase) : null);
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
            .Select(Clone)
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
}
