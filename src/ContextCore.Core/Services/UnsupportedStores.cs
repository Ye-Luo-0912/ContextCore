using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>未实现持久化后端时的显式占位存储集合，避免运行时静默丢弃数据。</summary>
// 以下每个类对应一个 Postgres provider 暂未实现的存储契约，构造时传入 provider 名称，
// 所有方法抛出 NotSupportedException，便于在调用方显式感知能力缺失。

/// <summary>短期记忆存储的占位实现。</summary>
public sealed class UnsupportedShortTermMemoryStore : IShortTermMemoryStore
{
    private readonly string _provider;

    public UnsupportedShortTermMemoryStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task AppendRawEventAsync(ShortTermRawEvent rawEvent, CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task SaveWorkingItemAsync(ShortTermWorkingItem item, CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task ReplaceRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task ReplaceWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AppendArchivedRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AppendArchivedWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ShortTermWorkingItem?> GetWorkingItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsAsync(
        ShortTermRawEventQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsAsync(
        ShortTermWorkingItemQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermRawEvent>> QueryArchivedRawEventsAsync(
        ShortTermRawEventQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermWorkingItem>> QueryArchivedWorkingItemsAsync(
        ShortTermWorkingItemQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermMemoryScope>> QueryScopesAsync(
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ShortTermMemorySummary> GetSummaryAsync(
        ShortTermSummaryQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ShortTermArchiveSummary> GetArchiveSummaryAsync(
        ShortTermArchiveSummaryQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AppendCompactionRunAsync(
        ShortTermCompactionRun run,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermCompactionRun>> QueryCompactionRunsAsync(
        ShortTermCompactionRunQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ShortTermCompactionRun?> GetCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Short term memory store is not implemented for storage provider '{_provider}'.");
}

/// <summary>短期记忆晋升候选项存储的占位实现。</summary>
public sealed class UnsupportedShortTermPromotionCandidateStore : IShortTermPromotionCandidateStore
{
    private readonly string _provider;

    public UnsupportedShortTermPromotionCandidateStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        ShortTermPromotionCandidate candidate,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ShortTermPromotionCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AppendReviewAsync(
        PromotionCandidateReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Short term promotion candidate store is not implemented for storage provider '{_provider}'.");
}

/// <summary>CandidateMemory review 存储的占位实现。</summary>
public sealed class UnsupportedCandidateMemoryReviewStore : ICandidateMemoryReviewStore
{
    private readonly string _provider;

    public UnsupportedCandidateMemoryReviewStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task AppendReviewAsync(
        CandidateMemoryReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<CandidateMemoryReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Candidate memory review store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Stable review 候选项存储的占位实现。</summary>
public sealed class UnsupportedStableReviewCandidateStore : IStableReviewCandidateStore
{
    private readonly string _provider;

    public UnsupportedStableReviewCandidateStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        StableReviewCandidate candidate,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AppendReviewAsync(
        StableReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Stable review candidate store is not implemented for storage provider '{_provider}'.");
}

/// <summary>上下文学习记录存储的占位实现。</summary>
public sealed class UnsupportedContextLearningStore : IContextLearningStore
{
    private readonly string _provider;

    public UnsupportedContextLearningStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task AddFeedbackAsync(
        PromotionFeedbackSignal feedback,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<PromotionFeedbackSignal>> QueryFeedbackAsync(
        PromotionFeedbackSignalQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AddRecordAsync(
        ContextLearningRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ContextLearningRecord?> GetRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ContextLearningRecord>> QueryRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ContextLearningCase> AddCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ContextLearningCase?> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ContextLearningCase>> QueryCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Context learning store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Router intent shadow trace 存储的占位实现。</summary>
public sealed class UnsupportedRouterIntentShadowTraceStore : IRouterIntentShadowTraceStore
{
    private readonly string _provider;

    public UnsupportedRouterIntentShadowTraceStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        RouterIntentShadowTrace trace,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<RouterIntentShadowTrace>> QueryAsync(
        RouterIntentShadowTraceQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Router intent shadow trace store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Vector reindex 报告存储的占位实现。</summary>
public sealed class UnsupportedVectorReindexReportStore : IVectorReindexReportStore
{
    private readonly string _provider;

    public UnsupportedVectorReindexReportStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        VectorReindexResult result,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<VectorReindexResult>> QueryAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<VectorReindexResult?> GetAsync(
        string reportId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Vector reindex report store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Vector lifecycle metadata review candidate 存储的占位实现。</summary>
public sealed class UnsupportedVectorLifecycleMetadataReviewCandidateStore : IVectorLifecycleMetadataReviewCandidateStore
{
    private readonly string _provider;

    public UnsupportedVectorLifecycleMetadataReviewCandidateStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        VectorLifecycleMetadataReviewCandidate candidate,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Vector lifecycle metadata review candidate store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Vector lifecycle metadata review 存储的占位实现。</summary>
public sealed class UnsupportedVectorLifecycleMetadataReviewStore : IVectorLifecycleMetadataReviewStore
{
    private readonly string _provider;

    public UnsupportedVectorLifecycleMetadataReviewStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        VectorLifecycleMetadataReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Vector lifecycle metadata review store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Vector lifecycle sidecar metadata 存储的占位实现。</summary>
public sealed class UnsupportedVectorLifecycleSidecarMetadataStore : IVectorLifecycleSidecarMetadataStore
{
    private readonly string _provider;

    public UnsupportedVectorLifecycleSidecarMetadataStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        VectorLifecycleSidecarMetadataEntry entry,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Vector lifecycle sidecar metadata store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Artifact 存储的占位实现。</summary>
public sealed class UnsupportedArtifactStore : IArtifactStore
{
    private readonly string _provider;

    public UnsupportedArtifactStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task<string> WriteJsonAsync<T>(
        ArtifactDescriptor descriptor,
        T value,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<string> WriteMarkdownAsync(
        ArtifactDescriptor descriptor,
        string markdown,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<string> AppendJsonLineAsync<T>(
        ArtifactDescriptor descriptor,
        T value,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<T?> ReadJsonAsync<T>(
        ArtifactDescriptor descriptor,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ArtifactManifestEntry>> ListAsync(
        ArtifactKind? kind = null,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Artifact store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Stable lifecycle review 存储的占位实现。</summary>
public sealed class UnsupportedStableLifecycleReviewStore : IStableLifecycleReviewStore
{
    private readonly string _provider;

    public UnsupportedStableLifecycleReviewStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task AppendReviewAsync(
        StableLifecycleReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Stable lifecycle review store is not implemented for storage provider '{_provider}'.");
}

/// <summary>Candidate constraint review 存储的占位实现。</summary>
public sealed class UnsupportedCandidateConstraintReviewStore : ICandidateConstraintReviewStore
{
    private readonly string _provider;

    public UnsupportedCandidateConstraintReviewStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task AppendReviewAsync(
        CandidateConstraintReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Candidate constraint review store is not implemented for storage provider '{_provider}'.");
}

/// <summary>约束语料缺口候选项存储的占位实现。</summary>
public sealed class UnsupportedConstraintGapCandidateStore : IConstraintGapCandidateStore
{
    private readonly string _provider;

    public UnsupportedConstraintGapCandidateStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task<ConstraintGapCandidate> SaveAsync(
        ConstraintGapCandidate candidate,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<ConstraintGapCandidate?> UpdateStatusAsync(
        string gapId,
        string status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task AppendReviewAsync(
        ConstraintGapReviewRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
        => new($"Constraint gap candidate store is not implemented for storage provider '{_provider}'.");
}
