namespace ContextCore.Abstractions;

/// <summary>短期记忆存储接口，负责保存短期原始事件、工作项并提供只读摘要。</summary>
public interface IShortTermMemoryStore
{
    Task AppendRawEventAsync(ShortTermRawEvent rawEvent, CancellationToken cancellationToken = default);

    Task SaveWorkingItemAsync(ShortTermWorkingItem item, CancellationToken cancellationToken = default);

    Task ReplaceRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default);

    Task ReplaceWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default);

    Task AppendArchivedRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default);

    Task AppendArchivedWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default);

    Task<ShortTermWorkingItem?> GetWorkingItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsAsync(
        ShortTermRawEventQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsAsync(
        ShortTermWorkingItemQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermRawEvent>> QueryArchivedRawEventsAsync(
        ShortTermRawEventQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermWorkingItem>> QueryArchivedWorkingItemsAsync(
        ShortTermWorkingItemQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermMemoryScope>> QueryScopesAsync(
        CancellationToken cancellationToken = default);

    Task<ShortTermMemorySummary> GetSummaryAsync(
        ShortTermSummaryQuery query,
        CancellationToken cancellationToken = default);

    Task<ShortTermArchiveSummary> GetArchiveSummaryAsync(
        ShortTermArchiveSummaryQuery query,
        CancellationToken cancellationToken = default);

    Task AppendCompactionRunAsync(
        ShortTermCompactionRun run,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermCompactionRun>> QueryCompactionRunsAsync(
        ShortTermCompactionRunQuery query,
        CancellationToken cancellationToken = default);

    Task<ShortTermCompactionRun?> GetCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

/// <summary>短期记忆晋升候选项存储接口。</summary>
public interface IShortTermPromotionCandidateStore
{
    Task SaveAsync(
        ShortTermPromotionCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<ShortTermPromotionCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default);

    Task AppendReviewAsync(
        PromotionCandidateReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default);
}

/// <summary>中期 CandidateMemory 治理存储；只登记候选层视图，不改变正式检索路径。</summary>
public interface ICandidateMemoryStore
{
    Task SaveAsync(
        CandidateMemoryItem item,
        CancellationToken cancellationToken = default);

    Task<CandidateMemoryItem?> GetAsync(
        string candidateMemoryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateMemoryItem>> QueryAsync(
        CandidateMemoryQuery query,
        CancellationToken cancellationToken = default);

    Task<CandidateMemorySummary> GetSummaryAsync(
        CandidateMemoryQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>中期 CandidateMemory 人工 review / cleanup 审核历史存储。</summary>
public interface ICandidateMemoryReviewStore
{
    Task AppendReviewAsync(
        CandidateMemoryReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateMemoryReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable review 候选项存储接口。</summary>
public interface IStableReviewCandidateStore
{
    Task SaveAsync(
        StableReviewCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default);

    Task AppendReviewAsync(
        StableReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default);
}

/// <summary>上下文学习记录与案例存储接口。</summary>
public interface IContextLearningStore
{
    Task AddFeedbackAsync(
        PromotionFeedbackSignal feedback,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromotionFeedbackSignal>> QueryFeedbackAsync(
        PromotionFeedbackSignalQuery query,
        CancellationToken cancellationToken = default);

    Task AddRecordAsync(
        ContextLearningRecord record,
        CancellationToken cancellationToken = default);

    Task<ContextLearningRecord?> GetRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextLearningRecord>> QueryRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default);

    Task<ContextLearningCase> AddCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default);

    Task<ContextLearningCase?> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextLearningCase>> QueryCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default);
}
