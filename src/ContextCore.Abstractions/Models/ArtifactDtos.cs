namespace ContextCore.Abstractions.Models;

/// <summary>文件系统 artifact 的标准分类，用于统一 report、trace 和治理数据的路由。</summary>
public enum ArtifactKind
{
    MemoryShort,
    MemoryCandidate,
    MemoryStable,
    MemoryShortTermRawEvent,
    MemoryShortTermWorkingItem,
    MemoryShortTermArchive,
    MemoryShortTermCompactionRun,
    MemoryTemporalItem,
    MemoryTemporalArchive,
    MemoryTemporalDiagnostics,
    MemoryCandidateItem,
    MemoryCandidateReview,
    MemoryCandidateDiagnostics,
    MemoryCandidateEvidence,
    MemoryStableItem,
    MemoryStableLifecycleReview,
    MemoryStableReplacementChain,
    MemoryStableProvenance,
    MemoryStableDiagnostics,
    Relation,
    Constraint,
    Vector,
    VectorLifecycleMetadataReviewCandidate,
    VectorLifecycleMetadataReviewCandidateReport,
    LearningFeedback,
    Router,
    Ranker,
    Graph,
    Eval,
    Trace,
    TraceRetrieval,
    TracePlanning,
    TraceToolCall,
    TraceRouterShadow,
    TraceRankerShadow,
    TraceVectorShadow,
    TraceGraphShadow,
    TraceRelationDualWrite,
    TraceRelationShadowRead,
    TraceRelationProviderSwitch,
    TraceLearningFeedbackDualWrite,
    TraceLearningFeedbackShadowRead,
    TraceLearningFeedbackProviderSwitch,
    TraceJobQueueDualWrite,
    TraceJobQueueShadowRead,
    TraceJobQueueScopedWorkerCanary,
    TraceJobQueueLimitedWorkerScopeObservation,
    TracePackageBuild,
    TraceModelCall,
    TraceError,
    Job,
    Report
}

/// <summary>存储责任分类，用于区分 artifact plane、运行状态、索引状态和迁移建议。</summary>
public enum StorageResponsibilityKind
{
    ArtifactOnly,
    OperationalState,
    IndexState,
    TraceOnly,
    ExportOnly,
    SnapshotOnly,
    MigrationCandidate,
    DatabaseRecommended,
    FileSystemPreferred
}

/// <summary>单个 artifact/store 的存储责任声明。</summary>
public sealed record StorageResponsibilityEntry
{
    public string SubjectId { get; init; } = string.Empty;

    public string SubjectType { get; init; } = "ArtifactKind";

    public ArtifactKind? ArtifactKind { get; init; }

    public string? StoreKind { get; init; }

    public StorageResponsibilityKind Responsibility { get; init; } = StorageResponsibilityKind.ArtifactOnly;

    public StorageResponsibilityKind PreferredProvider { get; init; } = StorageResponsibilityKind.FileSystemPreferred;

    public string CurrentProvider { get; init; } = "FileSystem";

    public string MigrationPriority { get; init; } = "None";

    public string MigrationRisk { get; init; } = "Low";

    public IReadOnlyList<StorageResponsibilityKind> Tags { get; init; } = Array.Empty<StorageResponsibilityKind>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Notes { get; init; } = string.Empty;
}

/// <summary>artifact plane 与 operational/index store 边界报告。</summary>
public sealed record StorageBoundaryReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int TotalArtifactKinds { get; init; }

    public int ArtifactOnlyCount { get; init; }

    public int OperationalStateCount { get; init; }

    public int IndexStateCount { get; init; }

    public int DatabaseRecommendedCount { get; init; }

    public int FileSystemPreferredCount { get; init; }

    public IReadOnlyList<StorageResponsibilityEntry> MigrationCandidates { get; init; } =
        Array.Empty<StorageResponsibilityEntry>();

    public IReadOnlyList<StorageResponsibilityEntry> HighPriorityMigrationCandidates { get; init; } =
        Array.Empty<StorageResponsibilityEntry>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RecommendedNextPhases { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<StorageResponsibilityEntry> Entries { get; init; } =
        Array.Empty<StorageResponsibilityEntry>();
}

/// <summary>用于解析 artifact 标准路径的描述符。</summary>
public sealed record ArtifactDescriptor
{
    public ArtifactKind Kind { get; init; } = ArtifactKind.Report;

    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? MemoryLayer { get; init; }

    public string? CapabilityId { get; init; }

    public string? ProviderId { get; init; }

    public string? OperationId { get; init; }

    public string? ReportId { get; init; }

    public string? DateShard { get; init; }

    public string Extension { get; init; } = ".json";
}

/// <summary>artifact manifest 记录；同一个 descriptor 重复写入时只更新同一条记录。</summary>
public sealed record ArtifactManifestEntry
{
    public string ArtifactId { get; init; } = string.Empty;

    public ArtifactKind ArtifactKind { get; init; } = ArtifactKind.Report;

    public ArtifactDescriptor Descriptor { get; init; } = new();

    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string? LegacyPath { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public string Extension { get; init; } = ".json";

    public string? ReportId { get; init; }

    public string? CapabilityId { get; init; }

    public string? ProviderId { get; init; }

    public string? PolicyVersion { get; init; }

    public string SchemaVersion { get; init; } = "artifact-manifest/v2";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public long SizeBytes { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public bool IsLatest { get; init; }

    public bool IsSnapshot { get; init; } = true;

    public string? SourceCommand { get; init; }
}

/// <summary>文件布局状态摘要，供 ControlRoom 展示。</summary>
public sealed record FileLayoutStatus
{
    public string DataRoot { get; init; } = string.Empty;

    public IReadOnlyList<string> ArtifactCategories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ArtifactManifestEntry> ResolvedPathSamples { get; init; } = Array.Empty<ArtifactManifestEntry>();

    public int ManifestCount { get; init; }

    public int ReportCount { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>Memory layer 文件布局诊断摘要，供 ControlRoom 和迁移测试确认路径治理状态。</summary>
public sealed record MemoryLayoutDiagnostics
{
    public string DataRoot { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> MemoryLayerPaths { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int ShortTermArtifactCount { get; init; }

    public int CandidateArtifactCount { get; init; }

    public int StableArtifactCount { get; init; }

    public bool TemporalPlaceholderReady { get; init; }

    public int LegacyFallbackCount { get; init; }

    public int MissingDirectoryCount { get; init; }

    public int ManifestCount { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>Trace artifact 布局诊断摘要，供 ControlRoom 展示 trace 分片和 legacy fallback 状态。</summary>
public sealed record TraceLayoutDiagnostics
{
    public string DataRoot { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string TraceRoot { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> TraceCategoryPaths { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int RetrievalTraceCount { get; init; }

    public int RouterShadowTraceCount { get; init; }

    public int RankerShadowTraceCount { get; init; }

    public int GraphShadowTraceCount { get; init; }

    public int VectorShadowTraceCount { get; init; }

    public bool ToolCallPlaceholderReady { get; init; }

    public int LegacyFallbackCount { get; init; }

    public int ManifestCount { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>Report artifact 布局诊断摘要，用于确认 legacy mirror、latest alias 和 manifest 完整性。</summary>
public sealed record ReportLayoutDiagnostics
{
    public string DataRoot { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, int> ReportCountByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int LatestReportCount { get; init; }

    public int LegacyMirroredCount { get; init; }

    public int MissingStandardArtifactCount { get; init; }

    public int MissingLegacyArtifactCount { get; init; }

    public int DuplicateContentHashCount { get; init; }

    public IReadOnlyList<ArtifactManifestEntry> LargestReports { get; init; } = Array.Empty<ArtifactManifestEntry>();

    public int ManifestCount { get; init; }

    public IReadOnlyList<ArtifactManifestEntry> ResolvedPathSamples { get; init; } = Array.Empty<ArtifactManifestEntry>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
