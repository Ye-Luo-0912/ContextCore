using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>内存版反馈特征候选存储，用于测试和 parity 基准。</summary>
public sealed class InMemoryLearningFeatureCandidateStore : ILearningFeatureCandidateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FeedbackFeatureCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertAsync(
        FeedbackFeatureCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            throw new ArgumentException("candidateId is required.", nameof(candidate));
        }

        lock (_gate)
        {
            _candidates[candidate.CandidateId.Trim()] = candidate;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryAsync(
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var limit = query.Limit > 0 ? query.Limit : 100;
        var offset = Math.Max(0, query.Offset);

        lock (_gate)
        {
            var rows = _candidates.Values
                .Where(item => Matches(query.CandidateId, item.CandidateId))
                .Where(item => Matches(query.SourceFeedbackId, item.SourceFeedbackId))
                .Where(item => Matches(query.CapabilityId, item.CapabilityId))
                .Where(item => Matches(query.TargetType, item.TargetType))
                .Where(item => Matches(query.LabelKind, item.LabelKind))
                .Where(item => Matches(query.TrainingUse, item.TrainingUse))
                .OrderBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Skip(offset)
                .Take(limit)
                .ToArray();
            return Task.FromResult<IReadOnlyList<FeedbackFeatureCandidate>>(rows);
        }
    }

    private static bool Matches(string? expected, string actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }
}
