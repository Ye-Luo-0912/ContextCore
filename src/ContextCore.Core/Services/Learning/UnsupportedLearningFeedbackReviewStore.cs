using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>未实现 provider 的反馈审核存储占位，返回明确配置错误。</summary>
public sealed class UnsupportedLearningFeedbackReviewStore : ILearningFeedbackReviewStore
{
    private readonly string _provider;

    public UnsupportedLearningFeedbackReviewStore(string provider)
    {
        _provider = provider;
    }

    public Task UpsertAsync(
        LearningFeedbackReviewRecord review,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Learning feedback review store is not supported for provider '{_provider}'.");
    }

    public Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Learning feedback review store is not supported for provider '{_provider}'.");
    }
}
