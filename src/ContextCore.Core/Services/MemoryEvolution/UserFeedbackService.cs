using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// 用户反馈服务。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. 入口层服务：将 <see cref="UserFeedbackSubmitRequest"/> 映射为 <see cref="UserFeedbackEntry"/>，
/// 通过 <see cref="IUserFeedbackLedger.AppendFeedbackAsync"/> 写入 ledger。
/// 2. 自动生成字段：FeedbackEntryId / GivenAt / IdempotencyKey 由本服务生成（调用方无需提供）。
/// 3. FeedbackValue 推导：ThumbsUp=+1.0 / ThumbsDown=-1.0 / Report=-1.0 / TextFeedback=0.0；
/// ScoreCorrection 时调用方必须显式提供 FeedbackValue（范围 [0.0, 1.0]）。
/// 4. TextFeedback 必填 FeedbackText；其他类型可选。
/// 5. 与 <see cref="LearningFeedbackService"/> 的关系：
/// - LearningFeedbackService 面向运行时反馈审核流程（LearningFeedbackEvent → LearningFeedbackReviewRecord）；
/// - UserFeedbackService 直接关联到 Utility Ledger 条目，作为校准/训练的标签信号源。
/// </remarks>
public sealed class UserFeedbackService
{
    private readonly IUserFeedbackLedger _ledger;
    private readonly TimeProvider? _timeProvider;

    public UserFeedbackService(
        IUserFeedbackLedger ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 提交用户反馈。
    /// </summary>
    /// <param name="request">提交请求（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交结果（含写入后的完整快照与警告）。</returns>
    /// <exception cref="ArgumentException">
    /// - WorkspaceId / CollectionId / DecisionId / CandidateItemId 为空
    /// - Kind = Unknown
    /// - ScoreCorrection 时 FeedbackValue 为空或超出 [0.0, 1.0]
    /// - TextFeedback 时 FeedbackText 为空
    /// </exception>
    public async Task<UserFeedbackSubmitResult> SubmitAsync(
        UserFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();

        ValidateRequest(request);

        var entry = BuildEntry(request, warnings);
        await _ledger.AppendFeedbackAsync(entry, cancellationToken).ConfigureAwait(false);

        return new UserFeedbackSubmitResult
        {
            FeedbackEntryId = entry.FeedbackEntryId,
            // Service 层无法感知 Store 是否做了幂等覆盖（InMemory 不去重，Postgres 做 ON CONFLICT DO UPDATE）；
            // Created=true 是保守表示：本次提交已写入 ledger。需要精确去重语义时调用方应通过
            // IdempotencyKey 自行跟踪（例如：相同 IdempotencyKey 多次提交视为幂等覆盖）。
            Created = true,
            IdempotentReplaced = false,
            Entry = entry,
            Warnings = warnings
        };
    }

    private static void ValidateRequest(UserFeedbackSubmitRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateItemId);

        if (request.Kind == UserFeedbackKind.Unknown)
        {
            throw new ArgumentException(
                "Kind 不能为 Unknown；必须显式指定反馈类型（ThumbsUp / ThumbsDown / ScoreCorrection / TextFeedback / Report）。",
                nameof(request));
        }

        if (request.Kind == UserFeedbackKind.ScoreCorrection)
        {
            if (!request.FeedbackValue.HasValue)
            {
                throw new ArgumentException(
                    "ScoreCorrection 必须提供 FeedbackValue（范围 [0.0, 1.0]）。",
                    nameof(request));
            }
            var v = request.FeedbackValue.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0.0 || v > 1.0)
            {
                throw new ArgumentException(
                    $"ScoreCorrection 的 FeedbackValue 必须在 [0.0, 1.0] 范围内；实际值 = {v}。",
                    nameof(request));
            }
        }

        if (request.Kind == UserFeedbackKind.TextFeedback
            && string.IsNullOrWhiteSpace(request.FeedbackText))
        {
            throw new ArgumentException(
                "TextFeedback 必须提供 FeedbackText。",
                nameof(request));
        }
    }

    private UserFeedbackEntry BuildEntry(UserFeedbackSubmitRequest request, List<string> warnings)
    {
        var feedbackValue = ResolveFeedbackValue(request, warnings);
        var givenAt = _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var feedbackEntryId = "feedback-" + Guid.NewGuid().ToString("N");
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? "fb-idem-" + Guid.NewGuid().ToString("N")
            : request.IdempotencyKey!;

        return new UserFeedbackEntry
        {
            FeedbackEntryId = feedbackEntryId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            DecisionId = request.DecisionId,
            CandidateItemId = request.CandidateItemId,
            Kind = request.Kind,
            FeedbackValue = feedbackValue,
            FeedbackText = request.FeedbackText,
            GivenBy = request.GivenBy,
            GivenAt = givenAt,
            IdempotencyKey = idempotencyKey,
            Metadata = request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static double ResolveFeedbackValue(UserFeedbackSubmitRequest request, List<string> warnings)
    {
        return request.Kind switch
        {
            UserFeedbackKind.ThumbsUp => 1.0,
            UserFeedbackKind.ThumbsDown => -1.0,
            UserFeedbackKind.Report => -1.0,
            UserFeedbackKind.TextFeedback => 0.0,
            UserFeedbackKind.ScoreCorrection => request.FeedbackValue!.Value,
            _ => throw new ArgumentException(
                $"未支持的 Kind: {request.Kind}", nameof(request))
        };
    }
}
