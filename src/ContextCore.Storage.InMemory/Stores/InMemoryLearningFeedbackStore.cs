using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>内存版运行时反馈事件存储，用于测试和本地临时采集。</summary>
public sealed class InMemoryLearningFeedbackStore : ILearningFeedbackStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LearningFeedbackEvent> _events = new(StringComparer.OrdinalIgnoreCase);

    public Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(feedbackId))
        {
            return Task.FromResult<LearningFeedbackEvent?>(null);
        }

        lock (_gate)
        {
            return Task.FromResult(_events.GetValueOrDefault(feedbackId.Trim()));
        }
    }

    public Task UpsertAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedbackEvent);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(feedbackEvent.FeedbackId))
        {
            throw new ArgumentException("feedbackId is required.", nameof(feedbackEvent));
        }

        lock (_gate)
        {
            _events[feedbackEvent.FeedbackId] = feedbackEvent;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var limit = query.Limit > 0 ? query.Limit : 100;
        var offset = Math.Max(0, query.Offset);
        lock (_gate)
        {
            var rows = _events.Values
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
                .Take(limit)
                .ToArray();
            return Task.FromResult<IReadOnlyList<LearningFeedbackEvent>>(rows);
        }
    }

    private static bool Matches(string? expected, string? actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }
}
