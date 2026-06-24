using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>未实现持久化后端时的显式占位存储，避免运行时静默丢弃反馈。</summary>
public sealed class UnsupportedLearningFeedbackStore : ILearningFeedbackStore
{
    private readonly string _provider;

    public UnsupportedLearningFeedbackStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task UpsertAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
    {
        return new NotSupportedException(
            $"Learning feedback store is not implemented for storage provider '{_provider}'.");
    }
}
