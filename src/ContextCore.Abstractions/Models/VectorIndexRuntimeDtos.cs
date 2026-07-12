namespace ContextCore.Abstractions.Models;


/// <summary>独立 vector index 的单条索引记录；V1 只用于基础设施与诊断，不接正式 retrieval。</summary>
public sealed class VectorIndexEntry
{
    public string EntryId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ContentHash { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector index 诊断汇总。</summary>
public sealed class VectorIndexDiagnosticsReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int IndexedCount { get; init; }

    public int MissingCount { get; init; }

    public int StaleCount { get; init; }

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int UnsupportedModelCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public IReadOnlyDictionary<string, int> CountsByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();
}


/// <summary>vector reindex preview 请求；只计算预期动作，不写入 index。</summary>
public sealed class VectorReindexPreviewRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? Layer { get; init; }

    public int Take { get; init; } = 200;

    public bool IncludeMemoryItems { get; init; } = true;

    public bool IncludeContextItems { get; init; } = true;
}


/// <summary>vector reindex preview 响应。</summary>
public sealed class VectorReindexPreviewResponse
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int SourceItemCount { get; init; }

    public int WouldCreateCount { get; init; }

    public int WouldUpdateCount { get; init; }

    public int AlreadyCurrentCount { get; init; }

    public int WouldDeleteOrphanCount { get; init; }

    public IReadOnlyList<VectorReindexPreviewItem> Items { get; init; } =
        Array.Empty<VectorReindexPreviewItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>vector reindex 的外部源项；用于 eval corpus 等只读数据源，不要求先写入 context/memory store。</summary>
public sealed class VectorReindexSourceItem
{
    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector reindex 计划、提交与执行的统一请求。</summary>
public sealed class VectorReindexRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();

    public bool DryRun { get; init; } = true;

    public bool Apply { get; init; }

    public bool ConfirmApply { get; init; }

    public bool Force { get; init; }

    public int BatchSize { get; init; } = 50;

    public int MaxItems { get; init; } = 200;

    public bool IncludeContextItems { get; init; } = true;

    public bool IncludeMemoryItems { get; init; } = true;

    public IReadOnlyList<VectorReindexSourceItem> SourceItems { get; init; } =
        Array.Empty<VectorReindexSourceItem>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector reindex 执行结果。</summary>
public sealed class VectorReindexResult
{
    public string ReportId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string? JobId { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public VectorReindexPlan Plan { get; init; } = new();

    public VectorReindexSummary Summary { get; init; } = new();

    public IReadOnlyList<VectorReindexPlanItem> ProcessedItems { get; init; } = Array.Empty<VectorReindexPlanItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}


/// <summary>vector reindex submit 响应，返回已入队 job 和预计算计划。</summary>
public sealed class VectorReindexSubmitResponse
{
    public ContextJob Job { get; init; } = new();

    public VectorReindexPlan Plan { get; init; } = new();
}


/// <summary>vector reindex report 查询响应。</summary>
public sealed class VectorReindexReportQueryResponse
{
    public IReadOnlyList<VectorReindexResult> Reports { get; init; } = Array.Empty<VectorReindexResult>();

    public int Count { get; init; }
}


/// <summary>vector index 覆盖率报告推荐结论。</summary>
public static class VectorIndexCoverageRecommendations
{
    public const string NeedsInitialIndexing = nameof(NeedsInitialIndexing);

    public const string NeedsReindex = nameof(NeedsReindex);

    public const string ReadyForVectorShadowEval = nameof(ReadyForVectorShadowEval);

    public const string BlockedByDiagnostics = nameof(BlockedByDiagnostics);
}


/// <summary>vector index coverage report；只读评估索引覆盖，不写入 index。</summary>
public sealed class VectorIndexCoverageReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TotalSourceItems { get; init; }

    public int IndexedItems { get; init; }

    public double CoverageRate { get; init; }

    public IReadOnlyDictionary<string, VectorIndexCoverageBucket> CoverageByLayer { get; init; } =
        new Dictionary<string, VectorIndexCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VectorIndexCoverageBucket> CoverageByItemKind { get; init; } =
        new Dictionary<string, VectorIndexCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> MissingByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> StaleByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public string Recommendation { get; init; } = VectorIndexCoverageRecommendations.NeedsInitialIndexing;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}
