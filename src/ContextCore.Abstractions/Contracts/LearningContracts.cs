using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>Router intent shadow trace 存储；用于旁路观测，不参与正式输出。</summary>
public interface IRouterIntentShadowTraceStore
{
    Task SaveAsync(
        RouterIntentShadowTrace trace,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RouterIntentShadowTrace>> QueryAsync(
        RouterIntentShadowTraceQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>运行时反馈事件存储；只用于收集和离线分析，不驱动正式策略。</summary>
public interface ILearningFeedbackStore
{
    Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>运行时反馈审核记录存储；只用于离线数据集候选治理。</summary>
public interface ILearningFeedbackReviewStore
{
    Task UpsertAsync(
        LearningFeedbackReviewRecord review,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>已审核反馈特征候选存储；只服务离线数据集候选治理。</summary>
public interface ILearningFeatureCandidateStore
{
    Task UpsertAsync(
        FeedbackFeatureCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryAsync(
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken = default);
}
