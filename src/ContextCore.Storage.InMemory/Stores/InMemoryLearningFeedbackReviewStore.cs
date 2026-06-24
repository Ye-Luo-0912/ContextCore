using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>内存版反馈审核记录存储；按 feedbackId 覆盖，避免重复审核噪声。</summary>
public sealed class InMemoryLearningFeedbackReviewStore : ILearningFeedbackReviewStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LearningFeedbackReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertAsync(
        LearningFeedbackReviewRecord review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(review.FeedbackId))
        {
            throw new ArgumentException("feedbackId is required.", nameof(review));
        }

        lock (_gate)
        {
            _reviews[review.FeedbackId.Trim()] = review;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var limit = query.Limit > 0 ? query.Limit : 100;
        var offset = Math.Max(0, query.Offset);

        lock (_gate)
        {
            var rows = _reviews.Values
                .Where(item => Matches(query.FeedbackId, item.FeedbackId))
                .Where(item => query.ReviewStatus is null || item.ReviewStatus == query.ReviewStatus)
                .Where(item => Matches(query.Reviewer, item.Reviewer))
                .OrderByDescending(item => item.ReviewedAt)
                .Skip(offset)
                .Take(limit)
                .ToArray();
            return Task.FromResult<IReadOnlyList<LearningFeedbackReviewRecord>>(rows);
        }
    }

    private static bool Matches(string? expected, string actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }
}
