namespace ContextCore.Abstractions;

/// <summary>后台作业的类型。</summary>
public enum ContextJobKind
{
    /// <summary>压缩作业。</summary>
    Compression,
    /// <summary>索引构建作业。</summary>
    IndexBuild,
    /// <summary>Embedding 生成作业。</summary>
    Embedding,
    /// <summary>包刷新作业。</summary>
    PackageRefresh,
    /// <summary>自定义作业。</summary>
    Custom
}

/// <summary>后台作业的执行状态。</summary>
public enum ContextJobState
{
    /// <summary>已入队，等待执行。</summary>
    Queued,
    /// <summary>执行中。</summary>
    Running,
    /// <summary>等待重试。</summary>
    WaitingRetry,
    /// <summary>已成功完成。</summary>
    Succeeded,
    /// <summary>已失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Cancelled,
    /// <summary>需要人工审核。</summary>
    RequiresReview
}

/// <summary>表示一个后台处理作业。</summary>
public sealed class ContextJob
{
    /// <summary>作业唯一标识符。</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>作业类型。</summary>
    public ContextJobKind Kind { get; init; } = ContextJobKind.Custom;

    /// <summary>作业载荷（JSON 格式）。</summary>
    public string PayloadJson { get; init; } = string.Empty;

    /// <summary>当前状态。</summary>
    public ContextJobState State { get; init; } = ContextJobState.Queued;

    /// <summary>优先级（值越大越优先）。</summary>
    public int Priority { get; init; }

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; init; }

    /// <summary>最大重试次数。</summary>
    public int MaxRetryCount { get; init; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>开始执行时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>失败时的错误信息。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>作业查询条件。</summary>
public sealed class ContextJobQuery
{
    /// <summary>筛选指定工作空间的作业。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>筛选指定集合的作业。</summary>
    public string? CollectionId { get; init; }

    /// <summary>筛选指定状态的作业。</summary>
    public ContextJobState? State { get; init; }

    /// <summary>最多返回的记录数，默认 100。</summary>
    public int Take { get; init; } = 100;
}

/// <summary>提供作业队列的入队、出队及确认操作。</summary>
public interface IContextJobQueue
{
    /// <summary>将作业加入队列。</summary>
    Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default);

    /// <summary>取出下一个待处理的作业。</summary>
    Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>确认作业已成功处理。</summary>
    Task AckAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>标记作业处理失败并附加原因。</summary>
    Task NackAsync(
        string jobId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>提供作业的查询功能。</summary>
public interface IContextJobQueryStore
{
    /// <summary>按条件查询作业列表。</summary>
    Task<IReadOnlyList<ContextJob>> QueryAsync(
        ContextJobQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>处理指定类型后台作业的执行器。</summary>
public interface IContextJobProcessor
{
    /// <summary>此处理器支持的作业类型。</summary>
    ContextJobKind Kind { get; }

    /// <summary>执行作业。</summary>
    Task ProcessAsync(ContextJob job, CancellationToken cancellationToken = default);
}

/// <summary>按作业类型分发到对应处理器。</summary>
public interface IContextJobDispatcher
{
    /// <summary>分发并执行作业。</summary>
    Task DispatchAsync(ContextJob job, CancellationToken cancellationToken = default);
}
