namespace ContextCore.Abstractions.Models;

/// <summary>短期原始事件，记录进入输入层后的短期上下文痕迹。</summary>
public sealed class ShortTermRawEvent
{
    public string EventId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Source { get; init; } = string.Empty;

    public string EventKind { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public DateTimeOffset CreatedAt { get; init; }

    public long SequenceId { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>短期工作项，表示从原始事件中提炼出的会话级工作记忆。</summary>
public sealed class ShortTermWorkingItem
{
    public string ItemId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public double Importance { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>短期记忆的基础策略，控制 TTL、数量上限与第一版写时裁剪行为。</summary>
public sealed class ShortTermMemoryPolicy
{
    public int MaxRawEvents { get; init; } = 200;

    public int MaxWorkingItems { get; init; } = 50;

    public TimeSpan RawEventTtl { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan WorkingItemTtl { get; init; } = TimeSpan.FromHours(24);

    public bool EnableCompaction { get; init; } = true;

    public bool EnablePromotionCandidate { get; init; }

    public bool EnableWorkingItemExtraction { get; init; } = true;

    public bool EnableExplicitWorkingMetadata { get; init; } = true;

    public bool MergeByWorkingKey { get; init; } = true;

    public TimeSpan DefaultWorkingItemTtl { get; init; } = TimeSpan.FromHours(24);
}

public sealed class ShortTermRawEventQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Source { get; init; }

    public string? EventKind { get; init; }

    public int Take { get; init; } = 100;
}

public sealed class ShortTermWorkingItemQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Kind { get; init; }

    public string? Status { get; init; }

    public int Take { get; init; } = 100;
}

public sealed class ShortTermSummaryQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public int LatestRawTake { get; init; } = 10;
}

public sealed class ShortTermMemorySummary
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public int RawEventCount { get; init; }

    public int WorkingItemCount { get; init; }

    public int ActiveTaskCount { get; init; }

    public int RecentDecisionCount { get; init; }

    public int OpenQuestionCount { get; init; }

    public int KnownIssueCount { get; init; }

    public int RecentWarningCount { get; init; }

    public IReadOnlyList<ShortTermWorkingItem> ActiveTasks { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> RecentDecisions { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> OpenQuestions { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> KnownIssues { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> RecentWarnings { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermRawEvent> LatestRawEvents { get; init; } = Array.Empty<ShortTermRawEvent>();

    public ShortTermMemoryPolicy Policy { get; init; } = new();
}

/// <summary>短期记忆压缩与归档策略，控制显式 compact/archive 行为。</summary>
public sealed class ShortTermMemoryCompactionPolicy
{
    public bool EnableCompaction { get; init; } = true;

    public bool EnableArchive { get; init; } = true;

    public TimeSpan ArchiveRawEventsAfter { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan ArchiveWorkingItemsAfter { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan ArchiveResolvedItemsAfter { get; init; } = TimeSpan.FromHours(6);

    public int MaxEvidenceRefsPerWorkingItem { get; init; } = 20;
}

/// <summary>短期记忆压缩请求，按 workspace/collection 可选收敛到单个 session。</summary>
public sealed class ShortTermMemoryCompactionRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }
}

/// <summary>短期记忆压缩结果，描述合并与归档的影响范围。</summary>
public sealed class ShortTermMemoryCompactionResult
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    public int ActiveRawEventCountBefore { get; init; }

    public int ActiveRawEventCountAfter { get; init; }

    public int ActiveWorkingItemCountBefore { get; init; }

    public int ActiveWorkingItemCountAfter { get; init; }

    public int MergedWorkingItems { get; init; }

    public int MergedByWorkingKeyGroups { get; init; }

    public int MergedByTitleGroups { get; init; }

    public int ArchivedRawEventCount { get; init; }

    public int ArchivedWorkingItemCount { get; init; }

    public int ArchivedResolvedWorkingItemCount { get; init; }

    public int EvidenceRefsTrimmed { get; init; }

    public ShortTermArchiveSummary ArchiveSummary { get; init; } = new();

    public ShortTermCompactionRun? Run { get; init; }
}

/// <summary>短期记忆 scope，用于维护任务枚举当前存在的数据分区。</summary>
public sealed class ShortTermMemoryScope
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;
}

/// <summary>短期记忆归档摘要查询条件。</summary>
public sealed class ShortTermArchiveSummaryQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }
}

/// <summary>短期记忆归档摘要，仅描述 archive 中的保留量，不含 active 数据。</summary>
public sealed class ShortTermArchiveSummary
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public int ArchivedRawEventCount { get; init; }

    public int ArchivedWorkingItemCount { get; init; }

    public int ArchivedResolvedWorkingItemCount { get; init; }

    public int ArchivedActiveTaskCount { get; init; }

    public int ArchivedRecentDecisionCount { get; init; }

    public int ArchivedOpenQuestionCount { get; init; }

    public int ArchivedKnownIssueCount { get; init; }

    public int ArchivedRecentWarningCount { get; init; }

    public DateTimeOffset? LatestArchivedAt { get; init; }
}

/// <summary>归档明细查询条件。kind 仅作用于 archived working items。</summary>
public sealed class ShortTermArchiveItemsQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Kind { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>归档明细响应，分 raw events 与 working items 返回。</summary>
public sealed class ShortTermArchiveItemsResponse
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Kind { get; init; }

    public IReadOnlyList<ShortTermRawEvent> RawEvents { get; init; } = Array.Empty<ShortTermRawEvent>();

    public IReadOnlyList<ShortTermWorkingItem> WorkingItems { get; init; } = Array.Empty<ShortTermWorkingItem>();
}

/// <summary>短期记忆压缩运行记录，用于手动或定时维护历史查询。</summary>
public sealed class ShortTermCompactionRun
{
    public string RunId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Trigger { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    public double DurationMs { get; init; }

    public int CompactedRawEvents { get; init; }

    public int CompactedWorkingItems { get; init; }

    public int ArchivedRawEvents { get; init; }

    public int ArchivedWorkingItems { get; init; }

    public int RemovedDuplicates { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>压缩运行记录查询条件。</summary>
public sealed class ShortTermCompactionRunQuery
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Trigger { get; init; }

    public int Take { get; init; } = 20;
}

/// <summary>短期记忆维护状态，供 runtime status/readiness 和 ControlRoom 统一展示。</summary>
public sealed class ShortTermMaintenanceStatusResponse
{
    public bool Enabled { get; init; }

    public bool IsRunning { get; init; }

    public bool RunOnStartup { get; init; }

    public int IntervalSeconds { get; init; }

    public string? LastError { get; init; }

    public ShortTermCompactionRun? LastRun { get; init; }
}
