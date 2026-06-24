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

/// <summary>Vector index 使用的 embedding provider 类型。</summary>
public static class EmbeddingProviderTypes
{
    public const string DeterministicHash = nameof(DeterministicHash);

    public const string OnnxLocal = nameof(OnnxLocal);

    public const string Disabled = nameof(Disabled);
}

/// <summary>Embedding provider 配置；模型路径应走本地私有配置，不提交模型文件。</summary>
public sealed class EmbeddingProviderOptions
{
    public string ProviderId { get; init; } = "deterministic-hash";

    public string ProviderType { get; init; } = EmbeddingProviderTypes.DeterministicHash;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public string EmbeddingModel { get; init; } = "deterministic-hash-v1";

    public int Dimension { get; init; } = 16;

    public bool Normalize { get; init; } = true;

    public string PoolingStrategy { get; init; } = "Mean";

    public int MaxTokens { get; init; } = 256;

    public int BatchSize { get; init; } = 32;

    public string Device { get; init; } = "cpu";

    public bool Enabled { get; init; } = true;
}

/// <summary>embedding tokenizer 输出；用于 ONNX input tensor 构造。</summary>
public sealed class EmbeddingTokenizationResult
{
    public int BatchSize { get; init; }

    public int SequenceLength { get; init; }

    public long[] InputIds { get; init; } = Array.Empty<long>();

    public long[] AttentionMask { get; init; } = Array.Empty<long>();

    public long[] TokenTypeIds { get; init; } = Array.Empty<long>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector index 查询条件。</summary>
public sealed class VectorIndexQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? ItemKind { get; init; }

    public string? Layer { get; init; }

    public string? EmbeddingModel { get; init; }

    public string? EmbeddingProvider { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 100;

    public bool IncludeVector { get; init; }
}

/// <summary>vector index brute-force 余弦查询条件。</summary>
public sealed class VectorIndexSearchQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    public string? EmbeddingModel { get; init; }

    public string? EmbeddingProvider { get; init; }

    public int? Dimension { get; init; }

    public int TopK { get; init; } = 10;

    public double? MinScore { get; init; }

    public bool IncludeVector { get; init; }
}

/// <summary>vector index brute-force 查询结果。</summary>
public sealed class VectorIndexSearchResult
{
    public VectorIndexEntry Entry { get; init; } = new();

    public double Score { get; init; }

    public int Rank { get; init; }
}

/// <summary>vector index 诊断类型。</summary>
public static class VectorIndexDiagnosticTypes
{
    public const string MissingEmbedding = nameof(MissingEmbedding);

    public const string StaleEmbedding = nameof(StaleEmbedding);

    public const string ContentHashMismatch = nameof(ContentHashMismatch);

    public const string DimensionMismatch = nameof(DimensionMismatch);

    public const string UnsupportedEmbeddingModel = nameof(UnsupportedEmbeddingModel);

    public const string ProviderUnavailable = nameof(ProviderUnavailable);

    public const string DuplicateVectorEntry = nameof(DuplicateVectorEntry);

    public const string OrphanVectorEntry = nameof(OrphanVectorEntry);

    public const string ModelFileMissing = nameof(ModelFileMissing);

    public const string TokenizerUnavailable = nameof(TokenizerUnavailable);

    public const string EmbeddingModelMismatch = nameof(EmbeddingModelMismatch);

    public const string ProviderMismatch = nameof(ProviderMismatch);

    public const string NormalizationMismatch = nameof(NormalizationMismatch);

    public const string UnsupportedPoolingStrategy = nameof(UnsupportedPoolingStrategy);

    public const string OnnxSessionFailed = nameof(OnnxSessionFailed);

    public const string RequiresReindex = nameof(RequiresReindex);

    public const string EmbeddingProviderChanged = nameof(EmbeddingProviderChanged);

    public const string EmbeddingModelChanged = nameof(EmbeddingModelChanged);

    public const string DimensionChanged = nameof(DimensionChanged);
}

/// <summary>单条 vector index 诊断结果。</summary>
public sealed class VectorIndexDiagnostic
{
    public string DiagnosticId { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Severity { get; init; } = "Warning";

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string? EntryId { get; init; }

    public string Message { get; init; } = string.Empty;

    public string SuggestedAction { get; init; } = string.Empty;

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

/// <summary>vector index 只读状态响应。</summary>
public sealed class VectorIndexStatusResponse
{
    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int IndexedCount { get; init; }

    public int StaleCount { get; init; }

    public int MissingCount { get; init; }

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public bool StoreAvailable { get; init; }

    public bool GeneratorAvailable { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
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

/// <summary>vector reindex preview 的单条动作。</summary>
public sealed class VectorReindexPreviewItem
{
    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string CurrentContentHash { get; init; } = string.Empty;

    public string? ExistingContentHash { get; init; }

    public string Reason { get; init; } = string.Empty;
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

/// <summary>vector reindex 计划，不写入 vector index。</summary>
public sealed class VectorReindexPlan
{
    public string PlanId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? LayerFilter { get; init; }

    public string? ItemKindFilter { get; init; }

    public int TotalCandidates { get; init; }

    public int ToCreate { get; init; }

    public int ToUpdate { get; init; }

    public int ToSkip { get; init; }

    public int ToDeleteOrphan { get; init; }

    public int EstimatedEmbeddingCount { get; init; }

    public bool DryRun { get; init; } = true;

    public IReadOnlyList<string> StaleItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DuplicateItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> OrphanItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<VectorReindexPlanItem> Items { get; init; } = Array.Empty<VectorReindexPlanItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>vector reindex 计划中的单条 source / entry 动作。</summary>
public sealed class VectorReindexPlanItem
{
    public string ItemId { get; init; } = string.Empty;

    public string? EntryId { get; init; }

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string CurrentContentHash { get; init; } = string.Empty;

    public string? ExistingContentHash { get; init; }

    public bool NeedsEmbedding { get; init; }

    public bool IsDuplicate { get; init; }

    public bool IsOrphan { get; init; }

    public string Reason { get; init; } = string.Empty;

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

/// <summary>vector reindex 执行摘要。</summary>
public sealed class VectorReindexSummary
{
    public int TotalCandidates { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public int Duplicate { get; init; }

    public int Orphan { get; init; }

    public int EstimatedEmbeddingCount { get; init; }

    public bool DryRun { get; init; } = true;

    public bool Applied { get; init; }
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

/// <summary>vector index 覆盖率分组统计。</summary>
public sealed class VectorIndexCoverageBucket
{
    public string Key { get; init; } = string.Empty;

    public int TotalSourceItems { get; init; }

    public int IndexedItems { get; init; }

    public int MissingItems { get; init; }

    public int StaleItems { get; init; }

    public double CoverageRate { get; init; }
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

/// <summary>向量查询配置标识。</summary>
public static class VectorQueryProfileIds
{
    public const string NormalV1 = "normal-v1";

    public const string CurrentTaskV1 = "current-task-v1";

    public const string AuditV1 = "audit-v1";

    public const string DiagnosticsV1 = "diagnostics-v1";
}

/// <summary>向量预览候选被路由到的只读目标区块。</summary>
public static class VectorQueryTargetSections
{
    public const string NormalContext = "normal_context";

    public const string WorkingContext = "working_context";

    public const string StableContext = "stable_context";

    public const string HistoricalContext = "historical_context";

    public const string AuditContext = "audit_context";

    public const string DiagnosticsOnly = "diagnostics_only";

    public const string Excluded = "excluded";
}

/// <summary>向量查询预览安全配置；只用于预览和影子评估，不接正式检索。</summary>
public sealed class VectorQueryProfile
{
    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public double MinSimilarity { get; init; }

    public IReadOnlyList<string> AllowedLayers { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedItemKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedSourceTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DiagnosticsOnlyItemKinds { get; init; } = Array.Empty<string>();

    public bool RequireKnownLifecycle { get; init; }

    public bool RequireCompleteLifecycleMetadata { get; init; }

    public bool AllowDeprecatedCandidates { get; init; }

    public bool AllowHistoricalCandidates { get; init; }

    public bool AllowRejectedCandidates { get; init; }

    public bool AllowCandidateLifecycle { get; init; }

    public string DefaultTargetSection { get; init; } = VectorQueryTargetSections.NormalContext;

    public string HistoricalTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string DiagnosticsTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;
}

/// <summary>向量预览候选资格状态。</summary>
public static class VectorCandidateEligibilityStatuses
{
    public const string Eligible = nameof(Eligible);

    public const string Blocked = nameof(Blocked);
}

/// <summary>向量预览候选被策略阻断的原因。</summary>
public static class VectorCandidateBlockedReason
{
    public const string UnknownLifecycleBlocked = nameof(UnknownLifecycleBlocked);

    public const string LifecycleMetadataIncompleteBlocked = nameof(LifecycleMetadataIncompleteBlocked);

    public const string ReplacementMetadataMissingBlocked = nameof(ReplacementMetadataMissingBlocked);

    public const string LegacySourceRequiresLifecycleMetadata = nameof(LegacySourceRequiresLifecycleMetadata);

    public const string HistoricalSourceRequiresAuditProfile = nameof(HistoricalSourceRequiresAuditProfile);

    public const string DeprecatedCandidateBlocked = nameof(DeprecatedCandidateBlocked);

    public const string HistoricalCandidateBlocked = nameof(HistoricalCandidateBlocked);

    public const string RejectedCandidateBlocked = nameof(RejectedCandidateBlocked);

    public const string CandidateLifecycleBlocked = nameof(CandidateLifecycleBlocked);

    public const string SimilarityBelowThreshold = nameof(SimilarityBelowThreshold);

    public const string DuplicateVectorEntryBlocked = nameof(DuplicateVectorEntryBlocked);

    public const string OrphanVectorEntryBlocked = nameof(OrphanVectorEntryBlocked);

    public const string DimensionMismatchBlocked = nameof(DimensionMismatchBlocked);

    public const string StaleEmbeddingBlocked = nameof(StaleEmbeddingBlocked);

    public const string UnsupportedLayer = nameof(UnsupportedLayer);

    public const string UnsupportedItemKind = nameof(UnsupportedItemKind);

    public const string DiagnosticsOnlyItemKindBlocked = nameof(DiagnosticsOnlyItemKindBlocked);

    public const string SupersededCandidateBlocked = nameof(SupersededCandidateBlocked);
}

/// <summary>单个向量预览候选的资格结果。</summary>
public sealed class VectorCandidateEligibilityResult
{
    public string CandidateId { get; init; } = string.Empty;

    public string EligibilityStatus { get; init; } = VectorCandidateEligibilityStatuses.Eligible;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string TargetSection { get; init; } = VectorQueryTargetSections.NormalContext;

    public bool RiskIfNormalSelected { get; init; }

    public bool RiskAfterPolicy { get; init; }
}

/// <summary>vector query preview 请求；只读查询 vector index，不接正式 retrieval/package。</summary>
public sealed class VectorQueryPreviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; } = 10;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public double? MinSimilarity { get; init; }

    public bool IncludeVector { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector query preview 响应；候选仅用于观察，不改变正式输出。</summary>
public sealed class VectorQueryPreviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; }

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public double? MinSimilarity { get; init; }

    public IReadOnlyList<VectorQueryPreviewCandidate> Candidates { get; init; } =
        Array.Empty<VectorQueryPreviewCandidate>();

    public VectorQueryPreviewDiagnostics Diagnostics { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>vector query preview 的单条候选。</summary>
public sealed class VectorQueryPreviewCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string EntryId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public int Rank { get; init; }

    public int RawRank { get; init; }

    public double Similarity { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool IsDuplicate { get; init; }

    public bool IsStale { get; init; }

    public bool IsOrphan { get; init; }

    public bool IsLifecycleRisk { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string EligibilityStatus { get; init; } = VectorCandidateEligibilityStatuses.Eligible;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string TargetSection { get; init; } = VectorQueryTargetSections.NormalContext;

    public bool RiskIfNormalSelected { get; init; }

    public bool RiskAfterPolicy { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector query preview 的诊断摘要。</summary>
public sealed class VectorQueryPreviewDiagnostics
{
    public bool StoreAvailable { get; init; }

    public bool GeneratorAvailable { get; init; }

    public bool IndexEmpty { get; init; }

    public int IndexedCount { get; init; }

    public int DuplicateCount { get; init; }

    public int StaleCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int UnsupportedModelCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();
}

/// <summary>vector query shadow eval 推荐结论。</summary>
public static class VectorQueryShadowRecommendations
{
    public const string NeedsMoreIndexedData = nameof(NeedsMoreIndexedData);

    public const string NeedsPolicyTuning = nameof(NeedsPolicyTuning);

    public const string NeedsProfileTuning = nameof(NeedsProfileTuning);

    public const string NeedsRankerFusion = nameof(NeedsRankerFusion);

    public const string NeedsFusionTuning = nameof(NeedsFusionTuning);

    public const string NeedsBetterEmbedding = nameof(NeedsBetterEmbedding);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);

    public const string ReadyForRetrievalShadow = nameof(ReadyForRetrievalShadow);

    public const string BlockedByRisk = nameof(BlockedByRisk);

    public const string NeedsRealEmbeddingProvider = nameof(NeedsRealEmbeddingProvider);

    public const string RequiresReranker = nameof(RequiresReranker);
}

/// <summary>vector recall loss 的离线 miss 原因；只用于 eval/report，不进入资格策略。</summary>
public static class VectorRecallLossMissReasons
{
    public const string NotIndexed = nameof(NotIndexed);

    public const string BelowTopK = nameof(BelowTopK);

    public const string BelowSimilarityThreshold = nameof(BelowSimilarityThreshold);

    public const string BlockedByEligibilityPolicy = nameof(BlockedByEligibilityPolicy);

    public const string LayerFilterExcluded = nameof(LayerFilterExcluded);

    public const string ItemKindFilterExcluded = nameof(ItemKindFilterExcluded);

    public const string NoCandidateGenerated = nameof(NoCandidateGenerated);

    public const string LowSimilaritySeparation = nameof(LowSimilaritySeparation);

    public const string RequiresRankerFusion = nameof(RequiresRankerFusion);
}

/// <summary>单个 mustHit 在 vector shadow 中未召回的离线审计明细。</summary>
public sealed class VectorRecallLossMiss
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public bool WasIndexed { get; init; }

    public bool WasRawCandidate { get; init; }

    public int RawRank { get; init; }

    public double RawSimilarity { get; init; }

    public bool WasEligibleCandidate { get; init; }

    public int EligibleRank { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public string MissReason { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public string ItemKindFilter { get; init; } = string.Empty;
}

/// <summary>按 mode 或 intent 聚合的 vector readiness 结果。</summary>
public sealed class VectorIntentReadinessBucket
{
    public string Key { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public int Samples { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public IReadOnlyDictionary<string, int> TopMissReasons { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>vector profile readiness 分组报告；用于判断是否只停留在 preview/shadow。</summary>
public sealed class VectorIntentReadinessReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string GroupBy { get; init; } = string.Empty;

    public IReadOnlyList<VectorIntentReadinessBucket> Buckets { get; init; } =
        Array.Empty<VectorIntentReadinessBucket>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>vector recall loss 审计报告；只解释 eval shadow 召回损失，不改变正式输出。</summary>
public sealed class VectorRecallLossAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public string ItemKindFilter { get; init; } = string.Empty;

    public int MissedMustHitCount { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public IReadOnlyList<VectorRecallLossMiss> MissedMustHits { get; init; } =
        Array.Empty<VectorRecallLossMiss>();

    public IReadOnlyDictionary<string, int> MissReasonCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public VectorIntentReadinessReport ModeReadiness { get; init; } = new();

    public VectorIntentReadinessReport IntentReadiness { get; init; } = new();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>被 eligibility policy 阻断的 mustHit 离线分类；只用于报告，不改变策略。</summary>
public static class VectorBlockedMustHitClassifications
{
    public const string MetadataRepairNeeded = nameof(MetadataRepairNeeded);

    public const string ProfileTooNarrow = nameof(ProfileTooNarrow);

    public const string LayerFilterTooStrict = nameof(LayerFilterTooStrict);

    public const string HistoricalMustHitRequiresAuditProfile = nameof(HistoricalMustHitRequiresAuditProfile);

    public const string DeprecatedMustHitBlockedCorrectly = nameof(DeprecatedMustHitBlockedCorrectly);

    public const string RequiresRankerFusion = nameof(RequiresRankerFusion);

    public const string RequiresManualReview = nameof(RequiresManualReview);

    public const string ShouldRemainBlocked = nameof(ShouldRemainBlocked);
}

/// <summary>单个被阻断 mustHit 的安全恢复审计明细。</summary>
public sealed class VectorBlockedMustHitAuditRecord
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string ResolvedLifecycle { get; init; } = string.Empty;

    public string MetadataCompleteness { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public bool CanBeSafelyAllowed { get; init; }

    public string MustRemainBlockedReason { get; init; } = string.Empty;

    public string RecommendedRepair { get; init; } = string.Empty;

    public string Classification { get; init; } = VectorBlockedMustHitClassifications.RequiresManualReview;
}

/// <summary>safe recall recovery 的单个 profile/topK/minSimilarity/layer 组合结果。</summary>
public sealed class VectorSafeRecallRecoverySweepResult
{
    public string ConfigurationId { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public int BelowTopKMissCount { get; init; }

    public int RecoveredBelowTopKCount { get; init; }

    public double RecoveryRate { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>safe recall recovery 汇总报告；只用于离线调参，不接正式检索。</summary>
public sealed class VectorSafeRecallRecoveryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public double BaselineRecallAfterPolicy { get; init; }

    public double BaselineMrrAfterPolicy { get; init; }

    public int BaselineRiskAfterPolicy { get; init; }

    public int BelowTopKMissCount { get; init; }

    public int BlockedMustHitCount { get; init; }

    public IReadOnlyList<VectorSafeRecallRecoverySweepResult> SweepResults { get; init; } =
        Array.Empty<VectorSafeRecallRecoverySweepResult>();

    public VectorSafeRecallRecoverySweepResult? BestSafeSweep { get; init; }

    public IReadOnlyList<VectorBlockedMustHitAuditRecord> BlockedMustHitAudit { get; init; } =
        Array.Empty<VectorBlockedMustHitAuditRecord>();

    public IReadOnlyDictionary<string, int> BlockedClassificationCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>V4 retrieval shadow readiness gate 报告；只读冻结闸门。</summary>
public sealed class VectorRetrievalShadowReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public bool Passed { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public int A3RiskAfterPolicy { get; init; }

    public double A3MustNotHitRiskAfterPolicy { get; init; }

    public double A3LifecycleRiskAfterPolicy { get; init; }

    public int A3FormalOutputChanged { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public int ExtendedRiskAfterPolicy { get; init; }

    public double ExtendedMustNotHitRiskAfterPolicy { get; init; }

    public double ExtendedLifecycleRiskAfterPolicy { get; init; }

    public int ExtendedFormalOutputChanged { get; init; }

    public double A3FusionRecallAfterPolicy { get; init; }

    public int A3FusionRiskAfterPolicy { get; init; }

    public double A3FusionLifecycleRiskAfterPolicy { get; init; }

    public int A3FusionNewlyRiskySamples { get; init; }

    public double ExtendedFusionRecallAfterPolicy { get; init; }

    public int ExtendedFusionRiskAfterPolicy { get; init; }

    public double ExtendedFusionLifecycleRiskAfterPolicy { get; init; }

    public int ExtendedFusionNewlyRiskySamples { get; init; }

    public double A3ExpandedRecallAfterPolicy { get; init; }

    public int A3ExpandedRiskAfterPolicy { get; init; }

    public double A3ExpandedMustNotHitRiskAfterPolicy { get; init; }

    public double A3ExpandedLifecycleRiskAfterPolicy { get; init; }

    public double ExtendedExpandedRecallAfterPolicy { get; init; }

    public int ExtendedExpandedRiskAfterPolicy { get; init; }

    public double ExtendedExpandedMustNotHitRiskAfterPolicy { get; init; }

    public double ExtendedExpandedLifecycleRiskAfterPolicy { get; init; }

    public IReadOnlyDictionary<string, bool> Conditions { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FailReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>vector + ranker fusion 的离线 shadow 策略名。</summary>
public static class VectorRankerFusionStrategies
{
    public const string VectorOnly = nameof(VectorOnly);

    public const string RankerOnly = nameof(RankerOnly);

    public const string UnionThenRank = nameof(UnionThenRank);

    public const string VectorBoostedRanker = nameof(VectorBoostedRanker);

    public const string RankerFilteredVector = nameof(RankerFilteredVector);

    public const string LifecycleAwareFusion = nameof(LifecycleAwareFusion);
}

/// <summary>离线 document representation profile；只用于 vector benchmark，不接正式检索。</summary>
public static class DocumentRepresentationProfiles
{
    public const string RawContentV1 = "raw-content-v1";

    public const string TitleContentV1 = "title-content-v1";

    public const string TitleSummaryContentV1 = "title-summary-content-v1";

    public const string AnchorEnrichedV1 = "anchor-enriched-v1";

    public const string MetadataEnrichedV1 = "metadata-enriched-v1";

    public const string CompactRetrievalTextV1 = "compact-retrieval-text-v1";
}

/// <summary>离线 query representation profile；只用于 vector benchmark，不接正式检索。</summary>
public static class QueryRepresentationProfiles
{
    public const string RawQueryV1 = "raw-query-v1";

    public const string IntentQueryV1 = "intent-query-v1";

    public const string AnchorQueryV1 = "anchor-query-v1";

    public const string ModeIntentQueryV1 = "mode-intent-query-v1";

    public const string ExpandedAnchorQueryV1 = "expanded-anchor-query-v1";
}

/// <summary>离线 vector query expansion profile；只组合运行时信号，不接正式检索。</summary>
public static class VectorQueryExpansionProfileIds
{
    public const string RawQueryV1 = "raw-query-v1";

    public const string ModeIntentQueryV1 = "mode-intent-query-v1";

    public const string AnchorQueryV1 = "anchor-query-v1";

    public const string IntentAnchorQueryV1 = "intent-anchor-query-v1";

    public const string PlanningContextQueryV1 = "planning-context-query-v1";

    public const string ConstraintAwareQueryV1 = "constraint-aware-query-v1";
}

/// <summary>vector query expansion profile；只用于离线 shadow，不影响正式 retrieval/package。</summary>
public sealed class VectorQueryExpansionProfile
{
    public string ProfileId { get; init; } = VectorQueryExpansionProfileIds.RawQueryV1;

    public bool IncludeRawQuery { get; init; } = true;

    public bool IncludeMode { get; init; }

    public bool IncludeIntent { get; init; }

    public bool IncludeQueryAnchors { get; init; }

    public bool IncludeWorkingMemoryAnchors { get; init; }

    public bool IncludePlanningContext { get; init; }

    public bool IncludeConstraintHints { get; init; }

    public bool IncludeTaskKind { get; init; }

    public bool IncludeRequestMetadata { get; init; }

    public int MaxSignalCount { get; init; } = 24;
}

/// <summary>vector query expansion 请求；字段必须来自运行时上下文或显式请求元数据。</summary>
public sealed class VectorQueryExpansionRequest
{
    public string QueryText { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string RouterIntent { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public ContextPlanningSnapshot? PlanningSnapshot { get; init; }

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> WorkingMemoryAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstraintHints { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> RequestMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector query expansion 结果；只用于 shadow query preview。</summary>
public sealed class VectorQueryExpansionResult
{
    public string ProfileId { get; init; } = VectorQueryExpansionProfileIds.RawQueryV1;

    public string OriginalQuery { get; init; } = string.Empty;

    public string ExpandedQuery { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> UsedSignals { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单个 query expansion profile 的 shadow 结果。</summary>
public sealed class VectorQueryExpansionShadowResult
{
    public string ExpansionProfile { get; init; } = VectorQueryExpansionProfileIds.RawQueryV1;

    public int Samples { get; init; }

    public double RecallBeforeExpansion { get; init; }

    public double RecallAfterExpansion { get; init; }

    public double MrrBeforeExpansion { get; init; }

    public double MrrAfterExpansion { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int RecoveredMissCount { get; init; }

    public int NewRiskCount { get; init; }

    public int QueryIntentMissingRecovered { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>query expansion shadow 汇总报告；只用于离线 eval，不改变正式输出。</summary>
public sealed class VectorQueryExpansionShadowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string VectorProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public IReadOnlyList<VectorQueryExpansionShadowResult> Results { get; init; } =
        Array.Empty<VectorQueryExpansionShadowResult>();

    public VectorQueryExpansionShadowResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>vector miss-set 的表示诊断类型；只用于 eval/report。</summary>
public static class VectorRepresentationDiagnosisTypes
{
    public const string QueryTooShort = nameof(QueryTooShort);

    public const string QueryIntentMissing = nameof(QueryIntentMissing);

    public const string DocumentTitleMissing = nameof(DocumentTitleMissing);

    public const string DocumentSummaryMissing = nameof(DocumentSummaryMissing);

    public const string AnchorMismatch = nameof(AnchorMismatch);

    public const string RepresentationTooNoisy = nameof(RepresentationTooNoisy);

    public const string MustHitOnlyRecoverableByMetadata = nameof(MustHitOnlyRecoverableByMetadata);

    public const string RequiresQueryExpansion = nameof(RequiresQueryExpansion);

    public const string RequiresDocumentRepresentationRewrite = nameof(RequiresDocumentRepresentationRewrite);

    public const string RequiresBetterEmbeddingModel = nameof(RequiresBetterEmbeddingModel);
}

/// <summary>单个 missed mustHit 的 representation 审计明细。</summary>
public sealed class VectorMissSetRepresentationAuditRecord
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public string MustHitItemId { get; init; } = string.Empty;

    public string DocumentTitle { get; init; } = string.Empty;

    public IReadOnlyList<string> DocumentAnchors { get; init; } = Array.Empty<string>();

    public string DocumentRepresentationProfile { get; init; } = DocumentRepresentationProfiles.RawContentV1;

    public string QueryRepresentationProfile { get; init; } = QueryRepresentationProfiles.RawQueryV1;

    public double RawSimilarity { get; init; }

    public int RawRank { get; init; }

    public int EligibleRank { get; init; }

    public string MissReason { get; init; } = string.Empty;

    public string RepresentationDiagnosis { get; init; } = string.Empty;

    public string RecommendedRepair { get; init; } = string.Empty;
}

/// <summary>A3 / Extended miss-set representation 审计报告。</summary>
public sealed class VectorMissSetRepresentationAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string DocumentRepresentationProfile { get; init; } = DocumentRepresentationProfiles.RawContentV1;

    public string QueryRepresentationProfile { get; init; } = QueryRepresentationProfiles.RawQueryV1;

    public int MissedMustHitCount { get; init; }

    public IReadOnlyList<VectorMissSetRepresentationAuditRecord> MissedMustHits { get; init; } =
        Array.Empty<VectorMissSetRepresentationAuditRecord>();

    public IReadOnlyDictionary<string, int> DiagnosisCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RecommendedRepairCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单个 representation benchmark 组合结果。</summary>
public sealed class VectorRepresentationBenchmarkResult
{
    public string DocumentRepresentationProfile { get; init; } = DocumentRepresentationProfiles.RawContentV1;

    public string QueryRepresentationProfile { get; init; } = QueryRepresentationProfiles.RawQueryV1;

    public string Provider { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public double Recall { get; init; }

    public double Mrr { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRisk { get; init; }

    public double LifecycleRisk { get; init; }

    public int NoCandidateCount { get; init; }

    public int RecoveredMissCount { get; init; }

    public int NewRiskCount { get; init; }

    public double SimilaritySeparation { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>representation benchmark 汇总报告；只使用临时 index，不写正式 vector index。</summary>
public sealed class VectorRepresentationBenchmarkReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public IReadOnlyList<VectorRepresentationBenchmarkResult> Results { get; init; } =
        Array.Empty<VectorRepresentationBenchmarkResult>();

    public VectorRepresentationBenchmarkResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单个 fusion 候选的离线打分明细；只用于 report，不进入正式检索。</summary>
public sealed class VectorRankerFusionCandidate
{
    public string ItemId { get; init; } = string.Empty;

    public int VectorRank { get; init; }

    public int FusionRank { get; init; }

    public double Similarity { get; init; }

    public double RankerScore { get; init; }

    public double FusionScore { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public bool RiskAfterPolicy { get; init; }

    public IReadOnlyList<string> ScoreReasons { get; init; } = Array.Empty<string>();
}

/// <summary>单个 eval 样本的 vector + ranker fusion shadow 差异。</summary>
public sealed class VectorRankerFusionShadowSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string Strategy { get; init; } = VectorRankerFusionStrategies.VectorOnly;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public int VectorCandidateCount { get; init; }

    public int FusionCandidateCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitVectorOnlyHitCount { get; init; }

    public int MustHitFusionHitCount { get; init; }

    public double MustHitMrrVectorOnly { get; init; }

    public double MustHitMrrFusion { get; init; }

    public int MustNotHitVectorOnlyCount { get; init; }

    public int MustNotHitFusionCount { get; init; }

    public int LifecycleRiskFusionCount { get; init; }

    public double RecallGain { get; init; }

    public int RiskDelta { get; init; }

    public bool IsFixed { get; init; }

    public bool IsNewlyRisky { get; init; }

    public IReadOnlyList<string> MustHitGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitLost { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NewlyRiskyItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<VectorRankerFusionCandidate> TopCandidates { get; init; } =
        Array.Empty<VectorRankerFusionCandidate>();
}

/// <summary>单个 fusion 策略的离线 shadow 汇总。</summary>
public sealed class VectorRankerFusionStrategyResult
{
    public string Strategy { get; init; } = VectorRankerFusionStrategies.VectorOnly;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public int Samples { get; init; }

    public int VectorCandidateCount { get; init; }

    public int FusionCandidateCount { get; init; }

    public double MustHitRecallVectorOnly { get; init; }

    public double MustHitRecallFusion { get; init; }

    public double MustHitMrrVectorOnly { get; init; }

    public double MustHitMrrFusion { get; init; }

    public double MustNotHitRiskVectorOnly { get; init; }

    public double MustNotHitRiskFusion { get; init; }

    public double LifecycleRiskFusion { get; init; }

    public double RecallGain { get; init; }

    public double RiskDelta { get; init; }

    public IReadOnlyList<VectorRankerFusionShadowSample> TopFixedSamples { get; init; } =
        Array.Empty<VectorRankerFusionShadowSample>();

    public IReadOnlyList<VectorRankerFusionShadowSample> NewlyRiskySamples { get; init; } =
        Array.Empty<VectorRankerFusionShadowSample>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>vector + ranker fusion 离线 shadow 报告；不改变正式 retrieval/package 输出。</summary>
public sealed class VectorRankerFusionShadowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string ProfileId { get; init; } = VectorQueryProfileIds.NormalV1;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public IReadOnlyList<VectorRankerFusionStrategyResult> Results { get; init; } =
        Array.Empty<VectorRankerFusionStrategyResult>();

    public VectorRankerFusionStrategyResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>vector query shadow eval 汇总报告；不改变正式 retrieval/package 输出。</summary>
public sealed class VectorQueryShadowEvalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int Samples { get; init; }

    public double IndexedCoverage { get; init; }

    public int QueryCount { get; init; }

    public int CandidateCount { get; init; }

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public int RiskBeforePolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustHitRecallBeforePolicy { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustNotHitRiskBeforePolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskBeforePolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public double MustHitRecallAtK { get; init; }

    public double MustNotHitRiskAtK { get; init; }

    public double LifecycleRiskAtK { get; init; }

    public int DeprecatedHitCount { get; init; }

    public int DuplicateHitCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public int NoCandidateCount { get; init; }

    public int LowConfidenceCount { get; init; }

    public IReadOnlyDictionary<string, int> TopNoiseClusters { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.NeedsMoreIndexedData;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<VectorQueryShadowEvalSample> SampleResults { get; init; } =
        Array.Empty<VectorQueryShadowEvalSample>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单个 eval 样本的 vector query shadow 结果。</summary>
public sealed class VectorQueryShadowEvalSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int CandidateCount { get; init; }

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitHitCount { get; init; }

    public int MustHitHitCountBeforePolicy { get; init; }

    public int MustHitHitCountAfterPolicy { get; init; }

    public int MustNotHitCount { get; init; }

    public int MustNotHitHitCount { get; init; }

    public int MustNotHitHitCountBeforePolicy { get; init; }

    public int MustNotHitHitCountAfterPolicy { get; init; }

    public int LifecycleRiskCount { get; init; }

    public int LifecycleRiskBeforePolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int RiskBeforePolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int DeprecatedHitCount { get; init; }

    public int DuplicateHitCount { get; init; }

    public double TopSimilarity { get; init; }

    public bool LowConfidence { get; init; }

    public IReadOnlyList<string> MustHitMatched { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitMatchedBeforePolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitMatchedAfterPolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitMissing { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitMatched { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitMatchedBeforePolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitMatchedAfterPolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItemsBeforePolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItemsAfterPolicy { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorQueryPreviewCandidate> Candidates { get; init; } =
        Array.Empty<VectorQueryPreviewCandidate>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>向量查询 profile sweep 的单个配置结果。</summary>
public sealed class VectorQueryProfileSweepResult
{
    public string ConfigurationId { get; init; } = string.Empty;

    public int Samples { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public double MustHitRecallBeforePolicy { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public double MustNotHitRiskBeforePolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskBeforePolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public int LowConfidenceCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public IReadOnlyDictionary<string, int> TopNoiseClusters { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RiskAfterPolicyByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double RecallLossAfterRepair { get; init; }

    public double SimilarityMarginForRiskCandidates { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>向量查询 profile sweep 汇总报告。</summary>
public sealed class VectorQueryProfileSweepReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int Samples { get; init; }

    public IReadOnlyList<VectorQueryProfileSweepResult> Results { get; init; } =
        Array.Empty<VectorQueryProfileSweepResult>();

    public VectorQueryProfileSweepResult? BestResult { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>ONNX 残余风险审计分类；只用于离线报告，不进入运行时策略。</summary>
public static class VectorResidualRiskTypes
{
    public const string DeprecatedMetadataGap = nameof(DeprecatedMetadataGap);

    public const string LifecycleMetadataGap = nameof(LifecycleMetadataGap);

    public const string SupersededItemAllowed = nameof(SupersededItemAllowed);

    public const string HistoricalItemAllowed = nameof(HistoricalItemAllowed);

    public const string WrongVersionActiveItem = nameof(WrongVersionActiveItem);

    public const string SemanticOvermatch = nameof(SemanticOvermatch);

    public const string SameTopicWrongIntent = nameof(SameTopicWrongIntent);

    public const string LowMarginAmbiguity = nameof(LowMarginAmbiguity);

    public const string SimilarityThresholdTooLoose = nameof(SimilarityThresholdTooLoose);

    public const string ProfileTooBroad = nameof(ProfileTooBroad);

    public const string RequiresReranker = nameof(RequiresReranker);

    public const string RequiresHumanPolicyDecision = nameof(RequiresHumanPolicyDecision);
}

/// <summary>单个 residual vector risk 明细；由 eval shadow 产生，不进入正式检索。</summary>
public sealed class VectorResidualRiskDetail
{
    public string SampleId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string CandidateItemId { get; init; } = string.Empty;

    public double Similarity { get; init; }

    public double SimilarityMargin { get; init; }

    public int RawRank { get; init; }

    public int EligibleRank { get; init; }

    public string TargetSection { get; init; } = string.Empty;

    public string RiskType { get; init; } = string.Empty;

    public string RiskReason { get; init; } = string.Empty;

    public string ItemLifecycle { get; init; } = string.Empty;

    public string ItemLayer { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string SourceRef { get; init; } = string.Empty;

    public string ContentHash { get; init; } = string.Empty;

    public string WhyPolicyAllowed { get; init; } = string.Empty;

    public string ExpectedAction { get; init; } = string.Empty;
}

/// <summary>vector residual risk audit 报告；用于解释 shadow 风险，不改变正式输出。</summary>
public sealed class VectorResidualRiskAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int ResidualRiskCount { get; init; }

    public int BeforeRepairRiskCount { get; init; }

    public int AfterRepairRiskCount { get; init; }

    public int BlockedByLifecycleMetadataGate { get; init; }

    public IReadOnlyDictionary<string, int> RemainingRiskTypes { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorResidualRiskDetail> Risks { get; init; } =
        Array.Empty<VectorResidualRiskDetail>();

    public IReadOnlyDictionary<string, int> RiskAfterPolicyByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double RecallLossAfterRepair { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public double SimilarityMarginForRiskCandidates { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>vector source lifecycle metadata coverage 推荐结论。</summary>
public static class VectorLifecycleMetadataCoverageRecommendations
{
    public const string ReadyForVectorShadowEval = nameof(ReadyForVectorShadowEval);

    public const string NeedsLifecycleMetadataBackfill = nameof(NeedsLifecycleMetadataBackfill);

    public const string BlockedByUnknownLifecycle = nameof(BlockedByUnknownLifecycle);

    public const string BlockedByDiagnostics = nameof(BlockedByDiagnostics);
}

/// <summary>vector source lifecycle metadata coverage 的分组统计。</summary>
public sealed class VectorLifecycleMetadataCoverageBucket
{
    public string Key { get; init; } = string.Empty;

    public int Total { get; init; }

    public int KnownLifecycleCount { get; init; }

    public int UnknownLifecycleCount { get; init; }

    public int MissingReviewStatusCount { get; init; }

    public int MissingReplacementInfoCount { get; init; }

    public int LegacySourceWithoutLifecycleCount { get; init; }

    public int DeprecatedSourceWithoutLifecycleCount { get; init; }

    public double LifecycleCoverageRate { get; init; }
}

/// <summary>vector source lifecycle metadata coverage 报告；只读诊断，不写 index。</summary>
public sealed class VectorLifecycleMetadataCoverageReport
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int TotalVectorSourceItems { get; init; }

    public int KnownLifecycleCount { get; init; }

    public int UnknownLifecycleCount { get; init; }

    public int MissingReviewStatusCount { get; init; }

    public int MissingReplacementInfoCount { get; init; }

    public int LegacySourceWithoutLifecycleCount { get; init; }

    public int DeprecatedSourceWithoutLifecycleCount { get; init; }

    public double LifecycleCoverageRate { get; init; }

    public IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> CoverageByLayer { get; init; } =
        new Dictionary<string, VectorLifecycleMetadataCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> CoverageByItemKind { get; init; } =
        new Dictionary<string, VectorLifecycleMetadataCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> CoverageBySourceType { get; init; } =
        new Dictionary<string, VectorLifecycleMetadataCoverageBucket>(StringComparer.OrdinalIgnoreCase);

    public int DuplicateCount { get; init; }

    public int OrphanCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int ProviderUnavailableCount { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataCoverageRecommendations.NeedsLifecycleMetadataBackfill;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>vector lifecycle metadata backfill 候选动作。</summary>
public static class VectorLifecycleMetadataBackfillActions
{
    public const string AutoResolve = nameof(AutoResolve);

    public const string ManualReviewRequired = nameof(ManualReviewRequired);

    public const string CannotResolve = nameof(CannotResolve);

    public const string AlreadyKnown = nameof(AlreadyKnown);
}

/// <summary>单个 vector lifecycle metadata backfill 候选；只描述可追踪 metadata 修复，不修改正式检索。</summary>
public sealed class VectorLifecycleMetadataBackfillCandidate
{
    public string ItemId { get; init; } = string.Empty;

    public string EntryId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string Action { get; init; } = VectorLifecycleMetadataBackfillActions.ManualReviewRequired;

    public string Reason { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public bool CanApply { get; init; }

    public IReadOnlyList<string> EvidenceMetadataKeys { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> BackfilledMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector lifecycle metadata backfill 计划；默认 dry-run，只允许显式确认后写 vector sidecar metadata。</summary>
public sealed class VectorLifecycleMetadataBackfillPlan
{
    public string PlanId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int TotalVectorSourceItems { get; init; }

    public int UnknownLifecycleBefore { get; init; }

    public int AutoResolvableCount { get; init; }

    public int ManualReviewRequiredCount { get; init; }

    public int CannotResolveCount { get; init; }

    public int ExpectedKnownLifecycleAfter { get; init; }

    public double ExpectedCoverageAfter { get; init; }

    public string RiskImpact { get; init; } = string.Empty;

    public double RecallRecoveryEstimate { get; init; }

    public bool DryRun { get; init; } = true;

    public IReadOnlyList<VectorLifecycleMetadataBackfillCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataBackfillCandidate>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>vector lifecycle metadata backfill 执行结果；不触碰业务源对象。</summary>
public sealed class VectorLifecycleMetadataBackfillResult
{
    public string ResultId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool Applied { get; init; }

    public int UpdatedEntries { get; init; }

    public int SkippedEntries { get; init; }

    public int ManualReviewRequiredCount { get; init; }

    public int CannotResolveCount { get; init; }

    public int FailedCount { get; init; }

    public VectorLifecycleMetadataBackfillPlan Plan { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}

/// <summary>确定性 hash embedding 的离线质量基线报告。</summary>
public sealed class VectorEmbeddingQualityBaselineReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public int PairCount { get; init; }

    public int PositiveHitCount { get; init; }

    public int NegativeHitCount { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public double MustHitRecallAt20 { get; init; }

    public double MustNotHitRiskAt20 { get; init; }

    public string EmbeddingProvider { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Embedding provider 离线质量比较报告；不接正式 retrieval。</summary>
public sealed class VectorEmbeddingProviderComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int IndexedItems { get; init; }

    public int QueryCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public double MustHitRecallAtK { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorEmbeddingProviderComparisonResult> ProviderResults { get; init; } =
        Array.Empty<VectorEmbeddingProviderComparisonResult>();

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>单个 embedding provider 的离线比较结果。</summary>
public sealed class VectorEmbeddingProviderComparisonResult
{
    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int IndexedItems { get; init; }

    public int QueryCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int NoCandidateCount { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        Array.Empty<VectorIndexDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>V3.10 provider 级向量质量比较；只用于 preview/shadow/eval。</summary>
public sealed class VectorProviderComparisonV310Result
{
    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int IndexedEntryCount { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public double A3MrrAfterPolicy { get; init; }

    public int A3RiskAfterPolicy { get; init; }

    public double A3MustNotHitRiskAfterPolicy { get; init; }

    public double A3LifecycleRiskAfterPolicy { get; init; }

    public int A3FormalOutputChanged { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public double ExtendedMrrAfterPolicy { get; init; }

    public int ExtendedRiskAfterPolicy { get; init; }

    public double QueryPreviewTopKOverlap { get; init; }

    public bool PgVectorParityPassed { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class VectorProviderComparisonV310Report
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorProviderComparisonV310Result> Providers { get; init; } =
        Array.Empty<VectorProviderComparisonV310Result>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>Qwen3 embedding provider readiness gate；不改变正式检索开关。</summary>
public sealed class VectorQwen3ReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public bool Passed { get; init; }

    public string ProviderId { get; init; } = "qwen3-embedding-0.6b-onnx";

    public string ProviderType { get; init; } = EmbeddingProviderTypes.OnnxLocal;

    public string ModelId { get; init; } = "qwen3-embedding-0.6b";

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ProviderCompatibilityPassed { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ProjectionMismatchCount { get; init; }

    public bool PgVectorFileSystemParityPassed { get; init; }

    public bool P15GatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}

/// <summary>V3.10.F provider 配置一致性检查；用于防止 qwen3 eval 静默回退到其它 provider。</summary>
public sealed class VectorProviderConfigurationSanityAuditItem
{
    public string ReportKind { get; init; } = string.Empty;

    public string ReportPath { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public bool Passed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();
}

public sealed class VectorProviderConfigurationSanityAuditReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string ProviderComparison { get; init; } = "Inconclusive";

    public string ExpectedProviderType { get; init; } = EmbeddingProviderTypes.OnnxLocal;

    public string ExpectedProviderId { get; init; } = "qwen3-embedding-0.6b-onnx";

    public string ExpectedModelId { get; init; } = "qwen3-embedding-0.6b";

    public string ExpectedPathSegment { get; init; } = "qwen3-embedding-0.6b-onnx";

    public int ExpectedDimension { get; init; } = 1024;

    public bool ExpectedUseForRuntime { get; init; }

    public IReadOnlyList<VectorProviderConfigurationSanityAuditItem> ReportChecks { get; init; } =
        Array.Empty<VectorProviderConfigurationSanityAuditItem>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = "BlockedByProviderConfigurationMismatch";
}

/// <summary>V3.10.F embedding provider promotion 结论；DoNotPromote = 维持当前 preview provider。</summary>
public static class EmbeddingProviderPromotionStatuses
{
    public const string Inconclusive = nameof(Inconclusive);

    public const string DoNotPromote = nameof(DoNotPromote);

    public const string KeepCurrentProvider = nameof(KeepCurrentProvider);

    public const string PromoteCandidate = nameof(PromoteCandidate);
}

/// <summary>V3.10.F embedding provider comparison freeze；不启用 formal retrieval，不切换 preview provider。</summary>
public sealed class EmbeddingProviderComparisonFreezeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string ProviderId { get; init; } = "qwen3-embedding-0.6b-onnx";

    public string ModelId { get; init; } = "qwen3-embedding-0.6b";

    public string ProviderComparison { get; init; } = "Inconclusive";

    public bool ProviderConfigurationSanityPassed { get; init; }

    public string ProviderConfigurationSanityAuditPath { get; init; } = string.Empty;

    public bool ReadinessGatePassed { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public string PromotionStatus { get; init; } = EmbeddingProviderPromotionStatuses.DoNotPromote;

    public bool VectorV4RecheckAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string VectorRetrievalStatus { get; init; } = "PreviewOnly";

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> Allowed { get; init; } = ["preview", "shadow", "eval"];

    public IReadOnlyList<string> Forbidden { get; init; } =
        ["FormalRetrievalSwitch", "PgVectorFormalRetrievalSwitch", "FormalIVectorIndexStoreBinding", "PackingPolicyIntegration", "PackageOutputIntegration"];

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

/// <summary>hybrid retrieval preview 变体标识；dense / dense+lexical / dense+anchor / 全量。</summary>
public static class HybridRetrievalVariant
{
    public const string Dense = nameof(Dense);
    public const string DenseLexical = nameof(DenseLexical);
    public const string DenseAnchor = nameof(DenseAnchor);
    public const string DenseLexicalAnchor = nameof(DenseLexicalAnchor);
}

/// <summary>hybrid retrieval readiness gate 结论常量。</summary>
public static class HybridRetrievalReadinessRecommendations
{
    public const string ReadyForVectorV4Recheck = nameof(ReadyForVectorV4Recheck);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByA3Recall = nameof(BlockedByA3Recall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPolicyViolation = nameof(BlockedByPolicyViolation);
}

/// <summary>hybrid retrieval preview 选项；preview only，UseForRuntime 恒 false，MaxRiskAllowed 恒 0。</summary>
public sealed class HybridVectorLexicalPreviewOptions
{
    public string DenseProviderScope { get; init; } = "current";
    public bool LexicalEnabled { get; init; } = true;
    public bool AnchorEnabled { get; init; } = true;
    public int UnionTopK { get; init; } = 10;
    public int DenseTopK { get; init; } = 10;
    public int LexicalTopK { get; init; } = 10;
    public int AnchorTopK { get; init; } = 10;
    public double MinDenseSimilarity { get; init; } = 0.25;
    public int MaxRiskAllowed { get; init; } = 0;
    public bool UseForRuntime { get; init; } = false;
}

/// <summary>hybrid retrieval 单变体单数据集报告。</summary>
public sealed class HybridRetrievalVariantReport
{
    public string DatasetName { get; init; } = string.Empty;
    public string ProfileName { get; init; } = "normal-v1";
    public string Variant { get; init; } = HybridRetrievalVariant.Dense;
    public int SampleCount { get; init; }
    public int DenseCandidateCount { get; init; }
    public int LexicalCandidateCount { get; init; }
    public int AnchorCandidateCount { get; init; }
    public int UnionCandidateCount { get; init; }
    public double RecallAfterPolicy { get; init; }
    public double MrrAfterPolicy { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public double RecallDeltaVsDense { get; init; }
    public double RiskDeltaVsDense { get; init; }
    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
}

/// <summary>hybrid retrieval 候选来源贡献统计。</summary>
public sealed class HybridSourceContribution
{
    public int DenseOnlyCount { get; init; }
    public int LexicalOnlyCount { get; init; }
    public int AnchorOnlyCount { get; init; }
    public int DenseAndLexicalCount { get; init; }
    public int DenseAndAnchorCount { get; init; }
    public int LexicalAndAnchorCount { get; init; }
    public int AllThreeCount { get; init; }
}

/// <summary>hybrid retrieval preview 总报告；A3+Extended 各 4 变体 = 8 条。</summary>
public sealed class HybridRetrievalPreviewReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public HybridVectorLexicalPreviewOptions Options { get; init; } = new();
    public IReadOnlyList<HybridRetrievalVariantReport> Variants { get; init; } = Array.Empty<HybridRetrievalVariantReport>();
    public HybridSourceContribution ContributionBreakdown { get; init; } = new();
    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>hybrid recall regression audit 结论常量。</summary>
public static class HybridRecallRegressionAuditRecommendations
{
    public const string ReadyForHybridFreeze = nameof(ReadyForHybridFreeze);
    public const string BlockedByDenseBaselineRegression = nameof(BlockedByDenseBaselineRegression);
    public const string BlockedByProviderScopeMismatch = nameof(BlockedByProviderScopeMismatch);
    public const string BlockedByEligibilityMismatch = nameof(BlockedByEligibilityMismatch);
    public const string BlockedByDedupBug = nameof(BlockedByDedupBug);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>hybrid recall regression audit 单 profile 对齐报告。</summary>
public sealed class HybridRecallRegressionAuditProfileResult
{
    public string ProfileName { get; init; } = string.Empty;
    public string DatasetName { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int CandidateCount { get; init; }
    public int EligibleCandidateCount { get; init; }
    public int BlockedCandidateCount { get; init; }
    public double RecallAfterPolicy { get; init; }
    public double MrrAfterPolicy { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int DenseCandidateDroppedCount { get; init; }
    public int EligibilityMismatchCount { get; init; }
}

/// <summary>hybrid recall regression audit 总报告；sanity audit only，不接 formal retrieval。</summary>
public sealed class HybridRetrievalRecallRegressionAuditReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public double LegacyDenseRecallA3 { get; init; }

    public double HybridDenseOnlyRecallA3 { get; init; }

    public double HybridBestRecallA3 { get; init; }

    public double LegacyDenseRecallExtended { get; init; }

    public double HybridDenseOnlyRecallExtended { get; init; }

    public double HybridBestRecallExtended { get; init; }

    public int CandidateLossCount { get; init; }

    public int DenseCandidateDroppedCount { get; init; }

    public int EligibilityMismatchCount { get; init; }

    public int ProviderScopeMismatchCount { get; init; }

    public int TopKConfigMismatchCount { get; init; }

    public int QueryVectorMismatchCount { get; init; }

    public int DedupOverwriteCount { get; init; }

    public bool UseForRuntime { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<HybridRecallRegressionAuditProfileResult> Profiles { get; init; } = Array.Empty<HybridRecallRegressionAuditProfileResult>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = HybridRecallRegressionAuditRecommendations.KeepPreviewOnly;
}

/// <summary>hybrid retrieval preview freeze gate 报告；只冻结 preview 结论，不启用正式检索。</summary>
public sealed class HybridRetrievalPreviewFreezeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string HybridRetrievalStatus { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;

    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;

    public double LegacyDenseRecallA3 { get; init; }

    public double HybridDenseOnlyRecallA3 { get; init; }

    public double HybridBestRecallA3 { get; init; }

    public double LegacyDenseRecallExtended { get; init; }

    public double HybridDenseOnlyRecallExtended { get; init; }

    public double HybridBestRecallExtended { get; init; }

    public int DenseCandidateDroppedCount { get; init; }

    public int EligibilityMismatchCount { get; init; }

    public int DedupOverwriteCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool V4RecheckAllowed { get; init; }

    public IReadOnlyList<string> RequiredBeforeV4 { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>retrieval dataset / query-corpus alignment audit 的诊断分类；只用于离线报告。</summary>
public static class RetrievalDatasetAlignmentIssueTypes
{
    public const string MustHitMissingFromCorpus = nameof(MustHitMissingFromCorpus);
    public const string MustHitMissingFromProviderScope = nameof(MustHitMissingFromProviderScope);
    public const string MustHitBlockedByEligibility = nameof(MustHitBlockedByEligibility);
    public const string MustHitLifecycleFiltered = nameof(MustHitLifecycleFiltered);
    public const string QueryTokenTooSparse = nameof(QueryTokenTooSparse);
    public const string QueryCorpusTokenMismatch = nameof(QueryCorpusTokenMismatch);
    public const string MissingAnchorMetadata = nameof(MissingAnchorMetadata);
    public const string SourceKindMismatch = nameof(SourceKindMismatch);
    public const string ProviderScopeMismatch = nameof(ProviderScopeMismatch);
    public const string CorpusCoverageRegression = nameof(CorpusCoverageRegression);
    public const string Unknown = nameof(Unknown);
}

/// <summary>retrieval dataset / query-corpus alignment audit 推荐结论。</summary>
public static class RetrievalDatasetAlignmentRecommendations
{
    public const string ReadyForRecallSourceRepair = nameof(ReadyForRecallSourceRepair);
    public const string NeedsCorpusBackfill = nameof(NeedsCorpusBackfill);
    public const string NeedsAnchorMetadataBackfill = nameof(NeedsAnchorMetadataBackfill);
    public const string NeedsQueryNormalizationRepair = nameof(NeedsQueryNormalizationRepair);
    public const string NeedsProviderScopeRepair = nameof(NeedsProviderScopeRepair);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>单条 query-corpus alignment 诊断记录；不进入 retrieval policy。</summary>
public sealed class RetrievalDatasetAlignmentIssue
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string IssueType { get; init; } = RetrievalDatasetAlignmentIssueTypes.Unknown;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryTokens { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CorpusOverlapTokens { get; init; } = Array.Empty<string>();

    public string SourceKind { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Notes { get; init; } = string.Empty;
}

/// <summary>retrieval dataset / query-corpus alignment audit 报告；只读评估，不改变正式检索。</summary>
public sealed class RetrievalDatasetAlignmentAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustNotCount { get; init; }

    public int MustHitPresentInCorpusCount { get; init; }

    public int MustHitMissingFromCorpusCount { get; init; }

    public int MustHitPresentInProviderScopeCount { get; init; }

    public int MustHitBlockedByEligibilityCount { get; init; }

    public double QueryTokenCoverageAverage { get; init; }

    public double QueryCorpusTokenOverlapAverage { get; init; }

    public double AnchorCoverageRate { get; init; }

    public double SourceKindCoverageRate { get; init; }

    public int CorpusEntryCount { get; init; }

    public int ProviderScopedEntryCount { get; init; }

    public int AlignmentIssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RetrievalDatasetAlignmentIssue> Issues { get; init; } =
        Array.Empty<RetrievalDatasetAlignmentIssue>();

    public string Recommendation { get; init; } = RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>A3 / Extended retrieval dataset alignment audit 汇总。</summary>
public sealed class RetrievalDatasetAlignmentAuditSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<RetrievalDatasetAlignmentAuditReport> Reports { get; init; } =
        Array.Empty<RetrievalDatasetAlignmentAuditReport>();

    public string Recommendation { get; init; } = RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;

    public int AlignmentIssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>eligibility recall loss triage 分类；只用于离线报告。</summary>
public static class VectorEligibilityRecallLossTriageCategories
{
    public const string CorrectlyBlockedDeprecated = nameof(CorrectlyBlockedDeprecated);
    public const string CorrectlyBlockedHistorical = nameof(CorrectlyBlockedHistorical);
    public const string CorrectlyBlockedSuperseded = nameof(CorrectlyBlockedSuperseded);
    public const string ShouldRouteToHistoricalContext = nameof(ShouldRouteToHistoricalContext);
    public const string ShouldRouteToAuditContext = nameof(ShouldRouteToAuditContext);
    public const string MetadataLifecycleRepairNeeded = nameof(MetadataLifecycleRepairNeeded);
    public const string ReviewStatusRepairNeeded = nameof(ReviewStatusRepairNeeded);
    public const string ReplacementStateRepairNeeded = nameof(ReplacementStateRepairNeeded);
    public const string ProfileTooStrictForAuditMode = nameof(ProfileTooStrictForAuditMode);
    public const string RequiresEvalExpectationReview = nameof(RequiresEvalExpectationReview);
    public const string UnsafeToRecover = nameof(UnsafeToRecover);
}

/// <summary>eligibility recall loss triage 推荐结论。</summary>
public static class VectorEligibilityRecallLossTriageRecommendations
{
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string ReadyForSectionRoutedRecallRepair = nameof(ReadyForSectionRoutedRecallRepair);
    public const string NeedsMetadataRepair = nameof(NeedsMetadataRepair);
    public const string NeedsEvalExpectationReview = nameof(NeedsEvalExpectationReview);
    public const string UnsafeToRecover = nameof(UnsafeToRecover);
}

/// <summary>单条 lifecycle-filtered mustHit 的离线 triage 明细。</summary>
public sealed class VectorEligibilityRecallLossTriageDetail
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string BlockedReason { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string CandidateTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public bool ShouldRemainBlocked { get; init; }

    public bool CanRouteToAuditOrHistorical { get; init; }

    public bool CanRepairMetadata { get; init; }

    public string RecommendedAction { get; init; } = string.Empty;

    public string Rationale { get; init; } = string.Empty;

    public string TriageCategory { get; init; } = VectorEligibilityRecallLossTriageCategories.UnsafeToRecover;
}

/// <summary>单个数据集的 lifecycle-filtered mustHit triage 报告。</summary>
public sealed class VectorEligibilityRecallLossTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public int SampleCount { get; init; }

    public int TotalFilteredMustHit { get; init; }

    public int CorrectlyBlockedCount { get; init; }

    public int RouteToHistoricalCount { get; init; }

    public int RouteToAuditCount { get; init; }

    public int MetadataRepairNeededCount { get; init; }

    public int EvalExpectationReviewNeededCount { get; init; }

    public int UnsafeToRecoverCount { get; init; }

    public int RecoverableWithoutNormalContextCount { get; init; }

    public int RecoverableToNormalContextCount { get; init; }

    public string Recommendation { get; init; } = VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyDictionary<string, int> CategoryBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorEligibilityRecallLossTriageDetail> Details { get; init; } =
        Array.Empty<VectorEligibilityRecallLossTriageDetail>();
}

/// <summary>A3 / Extended lifecycle-filtered mustHit triage 汇总。</summary>
public sealed class VectorEligibilityRecallLossTriageSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<VectorEligibilityRecallLossTriageReport> Reports { get; init; } =
        Array.Empty<VectorEligibilityRecallLossTriageReport>();

    public int TotalFilteredMustHit { get; init; }

    public int CorrectlyBlockedCount { get; init; }

    public int RouteToHistoricalCount { get; init; }

    public int RouteToAuditCount { get; init; }

    public int MetadataRepairNeededCount { get; init; }

    public int EvalExpectationReviewNeededCount { get; init; }

    public int UnsafeToRecoverCount { get; init; }

    public int RecoverableWithoutNormalContextCount { get; init; }

    public int RecoverableToNormalContextCount { get; init; }

    public string Recommendation { get; init; } = VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyDictionary<string, int> CategoryBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>vector lifecycle metadata repair preview 推荐状态。</summary>
public static class VectorLifecycleMetadataRepairPlanRecommendations
{
    public const string ReadyForMetadataRepairPreview = nameof(ReadyForMetadataRepairPreview);

    public const string NeedsHumanReview = nameof(NeedsHumanReview);

    public const string UnsafeToRepair = nameof(UnsafeToRepair);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>单个 lifecycle-filtered mustHit 的 metadata repair preview 候选。</summary>
public sealed class VectorLifecycleMetadataRepairCandidate
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string CurrentReviewStatus { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string ProposedTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool ProvenanceAvailable { get; init; }

    public bool RelationEvidenceAvailable { get; init; }

    public bool ReviewEvidenceAvailable { get; init; }

    public double RepairConfidence { get; init; }

    public string RepairReason { get; init; } = string.Empty;

    public bool CanAutoRepair { get; init; }

    public bool RequiresHumanReview { get; init; }

    public string ForbiddenReason { get; init; } = string.Empty;
}

/// <summary>单个数据集的 vector lifecycle metadata repair preview 报告。</summary>
public sealed class VectorLifecycleMetadataRepairPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public int CandidateCount { get; init; }

    public int AutoRepairableCount { get; init; }

    public int HumanReviewRequiredCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public double EstimatedRecallRecovery { get; init; }

    public int RiskAfterRepairEstimate { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorLifecycleMetadataRepairCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataRepairCandidate>();
}

/// <summary>A3 / Extended vector lifecycle metadata repair preview 汇总。</summary>
public sealed class VectorLifecycleMetadataRepairPlanSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<VectorLifecycleMetadataRepairPlanReport> Reports { get; init; } =
        Array.Empty<VectorLifecycleMetadataRepairPlanReport>();

    public int CandidateCount { get; init; }

    public int AutoRepairableCount { get; init; }

    public int HumanReviewRequiredCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public double EstimatedRecallRecovery { get; init; }

    public int RiskAfterRepairEstimate { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>vector lifecycle metadata review candidate 状态；V3.15 只生成 PendingReview，不提供决策写入。</summary>
public static class VectorLifecycleMetadataReviewCandidateStatuses
{
    public const string PendingReview = nameof(PendingReview);
    public const string NeedsEvidence = nameof(NeedsEvidence);
    public const string Rejected = nameof(Rejected);
    public const string ApprovedForSidecar = nameof(ApprovedForSidecar);
    public const string Superseded = nameof(Superseded);
}

/// <summary>从 lifecycle metadata repair plan 派生的人工 review 候选项；不会直接改变 runtime eligibility。</summary>
public sealed class VectorLifecycleMetadataReviewCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceEvalSet { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string CurrentReviewStatus { get; init; } = string.Empty;

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string ProposedTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;

    public string RepairReason { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool ProvenanceAvailable { get; init; }

    public bool RelationEvidenceAvailable { get; init; }

    public bool ReviewEvidenceAvailable { get; init; }

    public IReadOnlyList<string> RiskIfApproved { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RiskIfRejected { get; init; } = Array.Empty<string>();

    public bool RequiresHumanReview { get; init; }

    public string Status { get; init; } = VectorLifecycleMetadataReviewCandidateStatuses.PendingReview;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>vector lifecycle metadata review candidate 查询条件。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? Status { get; init; }

    public string? Layer { get; init; }

    public string? ItemKind { get; init; }

    public string? MustHitItemId { get; init; }

    public string? SourceEvalSet { get; init; }

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
}

/// <summary>vector lifecycle metadata review candidate 生成请求。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateGenerationRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? RepairPlanReportPath { get; init; }

    public int Limit { get; init; } = 500;
}

/// <summary>vector lifecycle metadata review candidate 生成结果。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateGenerationResult
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SourceReportPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int GeneratedCount { get; init; }

    public int UpsertedCount { get; init; }

    public int SkippedCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public IReadOnlyList<VectorLifecycleMetadataReviewCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewCandidate>();
}

/// <summary>vector lifecycle metadata review candidate explain 响应；只读展示证据与风险。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateExplanation
{
    public string CandidateId { get; init; } = string.Empty;

    public VectorLifecycleMetadataReviewCandidate Candidate { get; init; } = new();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool ProvenanceAvailable { get; init; }

    public bool RelationEvidenceAvailable { get; init; }

    public bool ReviewEvidenceAvailable { get; init; }

    public IReadOnlyList<string> RiskIfApproved { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RiskIfRejected { get; init; } = Array.Empty<string>();

    public string RepairReason { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>vector lifecycle metadata review candidate CLI / ControlRoom 汇总报告。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SourceReportPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int PendingCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public IReadOnlyDictionary<string, int> CountByStatus { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CountByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CountByItemKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorLifecycleMetadataReviewCandidate> RecentCandidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewCandidate>();

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>lifecycle metadata evidence backfill 的外部证据快照；只供 preview/audit 使用。</summary>
public sealed class VectorLifecycleMetadataEvidenceSourceSnapshot
{
    public string ItemId { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string ProvenanceRecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public IReadOnlyList<string> RelationEvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ReviewEvidenceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> OriginalCorpusMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>单个 lifecycle metadata review candidate 的 evidence backfill 状态。</summary>
public sealed class VectorLifecycleMetadataEvidenceBackfillCandidateStatus
{
    public string CandidateId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceEvalSet { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public bool EvidenceFound { get; init; }

    public bool SourceRefFound { get; init; }

    public bool ProvenanceFound { get; init; }

    public bool RelationEvidenceFound { get; init; }

    public bool ReviewEvidenceFound { get; init; }

    public bool ReplacementConflictFound { get; init; }

    public bool CanReclassifyAsAutoRepairable { get; init; }

    public bool StillNeedsHumanReview { get; init; }

    public bool ShouldRemainNeedsEvidence { get; init; }

    public bool ForbiddenToRepair { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string ProvenanceRecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;
}

/// <summary>lifecycle metadata evidence/provenance backfill preview/audit 报告；不写 sidecar，不改变 source item。</summary>
public sealed class VectorLifecycleMetadataEvidenceBackfillReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Mode { get; init; } = "preview";

    public string BatchId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string BatchPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int EvidenceFoundCount { get; init; }

    public int SourceRefFoundCount { get; init; }

    public int ProvenanceFoundCount { get; init; }

    public int AutoRepairableAfterBackfillCount { get; init; }

    public int StillHumanReviewRequiredCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int ReplacementConflictCount { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public bool SidecarWritten { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<VectorLifecycleMetadataEvidenceBackfillCandidateStatus> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataEvidenceBackfillCandidateStatus>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>Retrieval Dataset V2 ingestion metadata contract；只描述数据要求，不生成正式数据。</summary>
public sealed class RetrievalDatasetV2MetadataContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public IReadOnlyList<string> CorpusItemRequiredFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> QuerySampleRequiredFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TargetSectionRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RelationEvidenceRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SplitIsolationRules { get; init; } = Array.Empty<string>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "ReadyForDatasetV2Authoring";
}

/// <summary>Retrieval Dataset V2 validator issue type。</summary>
public static class RetrievalDatasetV2ValidationIssueTypes
{
    public const string MustHitMissingFromCorpus = nameof(MustHitMissingFromCorpus);
    public const string MustNotMissingFromCorpus = nameof(MustNotMissingFromCorpus);
    public const string MustHitMustNotOverlap = nameof(MustHitMustNotOverlap);
    public const string QueryContainsItemId = nameof(QueryContainsItemId);
    public const string MissingSourceRefs = nameof(MissingSourceRefs);
    public const string MissingEvidenceRefs = nameof(MissingEvidenceRefs);
    public const string MissingProvenance = nameof(MissingProvenance);
    public const string LifecycleTargetSectionMismatch = nameof(LifecycleTargetSectionMismatch);
    public const string RelationEvidenceMissing = nameof(RelationEvidenceMissing);
    public const string SplitIsolationViolation = nameof(SplitIsolationViolation);
}

/// <summary>Retrieval Dataset V2 validator recommendation。</summary>
public static class RetrievalDatasetV2ValidationRecommendations
{
    public const string ReadyForDatasetV2Authoring = nameof(ReadyForDatasetV2Authoring);
    public const string NeedsIngestionMetadataBackfill = nameof(NeedsIngestionMetadataBackfill);
    public const string NeedsRelationEvidenceBackfill = nameof(NeedsRelationEvidenceBackfill);
    public const string NeedsQueryLabelHygiene = nameof(NeedsQueryLabelHygiene);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Retrieval Dataset V2 validation 单条问题。</summary>
public sealed class RetrievalDatasetV2ValidationIssue
{
    public string IssueType { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>Retrieval Dataset V2 validation 报告；只读检查，不改变正式检索。</summary>
public sealed class RetrievalDatasetV2ValidationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public int CorpusItemCount { get; init; }

    public int QuerySampleCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustNotCount { get; init; }

    public int MustHitMissingFromCorpusCount { get; init; }

    public int MustNotMissingFromCorpusCount { get; init; }

    public int MustHitMustNotOverlapCount { get; init; }

    public int QueryItemIdLeakCount { get; init; }

    public int MissingSourceRefsCount { get; init; }

    public int MissingEvidenceRefsCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int LifecycleTargetSectionMismatchCount { get; init; }

    public int RelationEvidenceMissingCount { get; init; }

    public int SplitIsolationViolationCount { get; init; }

    public int IssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RetrievalDatasetV2ValidationIssue> Issues { get; init; } =
        Array.Empty<RetrievalDatasetV2ValidationIssue>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ValidationRecommendations.KeepPreviewOnly;
}

/// <summary>旧 retrieval/vector eval corpus 限制报告；说明其不适合作为主 recall repair 目标。</summary>
public sealed class RetrievalDatasetLegacyLimitationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string BatchId { get; init; } = string.Empty;

    public int ReviewCandidateCount { get; init; }

    public int MissingEvidenceSourceProvenanceCandidateCount { get; init; }

    public string EvidenceBackfillRecommendation { get; init; } = string.Empty;

    public bool LegacyDatasetSuitableForPrimaryRecallRepair { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredNextDataWork { get; init; } = Array.Empty<string>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill;
}

/// <summary>Retrieval Dataset V2 离线生成配置；默认禁用且不用于 runtime。</summary>
public sealed class RetrievalDatasetV2GenerationOptions
{
    public bool Enabled { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = "default-workspace";

    public string CollectionId { get; init; } = "default-collection";

    public int TargetCorpusItemCount { get; init; } = 28;

    public int TargetSampleCount { get; init; } = 21;

    public string DifficultyProfile { get; init; } = "balanced-v1";

    public int Seed { get; init; } = 1701;

    public string OutputDirectory { get; init; } = "vector/dataset-v2/generated";

    public bool DryRun { get; init; } = true;

    public bool RequireValidation { get; init; } = true;

    public bool UseForRuntime { get; init; }
}

/// <summary>Retrieval Dataset V2 corpus item provenance。</summary>
public sealed class RetrievalDatasetV2Provenance
{
    public string RecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string IngestionBatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Retrieval Dataset V2 relation evidence。</summary>
public sealed class RetrievalDatasetV2Relation
{
    public string RelationId { get; init; } = string.Empty;

    public string SourceItemId { get; init; } = string.Empty;

    public string TargetItemId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>Retrieval Dataset V2 corpus item；离线生成，不直接进入正式检索。</summary>
public sealed class RetrievalDatasetV2CorpusItem
{
    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public RetrievalDatasetV2Provenance Provenance { get; init; } = new();

    public string SourceFingerprint { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<RetrievalDatasetV2Relation> Relations { get; init; } = Array.Empty<RetrievalDatasetV2Relation>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Anchors { get; init; } = Array.Empty<string>();

    public string Content { get; init; } = string.Empty;

    public string Split { get; init; } = "train";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Retrieval Dataset V2 generated query sample。</summary>
public sealed class RetrievalDatasetV2Sample
{
    public string SampleId { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string Difficulty { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitItemIds { get; init; } = Array.Empty<string>();

    public string Rationale { get; init; } = string.Empty;

    public IReadOnlyList<string> NegativeDistractorIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredRelations { get; init; } = Array.Empty<string>();

    public string ExpectedLifecycleBehavior { get; init; } = string.Empty;

    public string Split { get; init; } = "test";

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public RetrievalDatasetV2Provenance Provenance { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Retrieval Dataset V2 生成结果。</summary>
public sealed class RetrievalDatasetV2GeneratedDataset
{
    public IReadOnlyList<RetrievalDatasetV2CorpusItem> CorpusItems { get; init; } =
        Array.Empty<RetrievalDatasetV2CorpusItem>();

    public IReadOnlyList<RetrievalDatasetV2Sample> Samples { get; init; } =
        Array.Empty<RetrievalDatasetV2Sample>();
}

/// <summary>Retrieval Dataset V2 generator recommendation。</summary>
public static class RetrievalDatasetV2GenerationRecommendations
{
    public const string ReadyForDatasetV2ShadowEval = nameof(ReadyForDatasetV2ShadowEval);
    public const string NeedsGenerationRepair = nameof(NeedsGenerationRepair);
    public const string BlockedByValidationIssues = nameof(BlockedByValidationIssues);
    public const string BlockedByMissingEvidence = nameof(BlockedByMissingEvidence);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string NotConfigured = nameof(NotConfigured);
}

/// <summary>Retrieval Dataset V2 generation report。</summary>
public sealed class RetrievalDatasetV2GenerationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public RetrievalDatasetV2GenerationOptions Options { get; init; } = new();

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int MustNotOverlapCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public int JudgeWarningCount { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2GenerationRecommendations.NotConfigured;

    public IReadOnlyList<string> PromptTemplates { get; init; } = Array.Empty<string>();
}

/// <summary>Retrieval Dataset V2 quality report。</summary>
public sealed class RetrievalDatasetV2QualityReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int MustNotOverlapCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public int JudgeWarningCount { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2GenerationRecommendations.NotConfigured;
}

/// <summary>Retrieval Dataset V2 物化 manifest。</summary>
public sealed class RetrievalDatasetV2Manifest
{
    public string DatasetId { get; init; } = string.Empty;

    public string CorpusPath { get; init; } = string.Empty;

    public string SamplesPath { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = "retrieval-dataset-v2-generator/v1";

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
}

/// <summary>Retrieval Dataset V2 materialization gate recommendation。</summary>
public static class RetrievalDatasetV2MaterializationRecommendations
{
    public const string ReadyForDatasetV2ShadowEval = nameof(ReadyForDatasetV2ShadowEval);
    public const string BlockedByMissingArtifact = nameof(BlockedByMissingArtifact);
    public const string BlockedByValidationIssues = nameof(BlockedByValidationIssues);
    public const string BlockedByQualityGate = nameof(BlockedByQualityGate);
    public const string BlockedByHashInstability = nameof(BlockedByHashInstability);
    public const string BlockedByRuntimeUse = nameof(BlockedByRuntimeUse);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Retrieval Dataset V2 materialization / immutability gate report。</summary>
public sealed class RetrievalDatasetV2MaterializationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string CorpusPath { get; init; } = string.Empty;

    public string SamplesPath { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = "retrieval-dataset-v2-generator/v1";

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public bool CorpusExists { get; init; }

    public bool SamplesExists { get; init; }

    public bool ManifestExists { get; init; }

    public bool ValidatePassed { get; init; }

    public string QualityRecommendation { get; init; } = string.Empty;

    public bool CorpusHashStable { get; init; }

    public bool SamplesHashStable { get; init; }

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2MaterializationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Retrieval Dataset V2 shadow eval recommendation。</summary>
public static class RetrievalDatasetV2ShadowEvalRecommendations
{
    public const string ReadyForDatasetV2RetrievalCandidate = nameof(ReadyForDatasetV2RetrievalCandidate);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRecall = nameof(BlockedByRecall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByMustNotRisk = nameof(BlockedByMustNotRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPgVectorParityMismatch = nameof(BlockedByPgVectorParityMismatch);
    public const string BlockedByDatasetValidation = nameof(BlockedByDatasetValidation);
}

/// <summary>Retrieval Dataset V2 单个 profile 的 shadow eval 报告。</summary>
public sealed class RetrievalDatasetV2ShadowEvalProfileReport
{
    public string DatasetId { get; init; } = string.Empty;

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int CorpusItemCount { get; init; }

    public int CandidateCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int DenseCandidateCount { get; init; }

    public int LexicalCandidateCount { get; init; }

    public int AnchorCandidateCount { get; init; }

    public int UnionCandidateCount { get; init; }

    public int EligibilityBlockedCount { get; init; }

    public int MustHitBlockedByEligibilityCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int TargetSectionMismatchCount { get; init; }

    public double TopKOverlapRate { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;
}

/// <summary>Retrieval Dataset V2 shadow eval 汇总报告。</summary>
public sealed class RetrievalDatasetV2ShadowEvalSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string BestProfileName { get; init; } = string.Empty;

    public double BestRecallAfterPolicy { get; init; }

    public double BestMrrAfterPolicy { get; init; }

    public int BestRiskAfterPolicy { get; init; }

    public bool PgVectorParityPassed { get; init; }

    public double PgVectorTopKOverlapRate { get; init; }

    public int PgVectorOrderingMismatchCount { get; init; }

    public double PgVectorScoreDeltaMax { get; init; }

    public int PgVectorMetadataMismatchCount { get; init; }

    public int PgVectorEligibilityMetadataMismatchCount { get; init; }

    public int PgVectorRiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> Profiles { get; init; } =
        Array.Empty<RetrievalDatasetV2ShadowEvalProfileReport>();
}

/// <summary>Retrieval Dataset V2 readiness gate 报告。</summary>
public sealed class RetrievalDatasetV2ReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public double RecallThreshold { get; init; }

    public double BestRecallAfterPolicy { get; init; }

    public double BestMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PgVectorParityPassed { get; init; }

    public bool MaterializationGatePassed { get; init; }

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Retrieval Dataset V2 stress / holdout / leakage audit 配置；仅用于离线预览。</summary>
public sealed class RetrievalDatasetV2StressOptions
{
    public int TargetCorpusItemCount { get; init; } = 120;

    public int TargetSampleCount { get; init; } = 120;

    public double HoldoutRatio { get; init; } = 0.2;

    public IReadOnlyDictionary<string, int> DifficultyDistribution { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double DistractorRatio { get; init; } = 0.35;

    public bool AnchorAblationEnabled { get; init; } = true;

    public bool LeakageAuditEnabled { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public string WorkspaceId { get; init; } = "default-workspace";

    public string CollectionId { get; init; } = "default-collection";

    public int Seed { get; init; } = 2701;

    public string OutputDirectory { get; init; } = "vector/dataset-v2/stress";

    public bool DryRun { get; init; } = true;
}

/// <summary>Retrieval Dataset V2 stress / leakage / readiness recommendation。</summary>
public static class RetrievalDatasetV2StressRecommendations
{
    public const string ReadyForDatasetV2StressFreeze = nameof(ReadyForDatasetV2StressFreeze);
    public const string NeedsHarderDataset = nameof(NeedsHarderDataset);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string BlockedByAnchorDominance = nameof(BlockedByAnchorDominance);
    public const string BlockedByHoldoutRecall = nameof(BlockedByHoldoutRecall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Retrieval Dataset V2 stress / holdout / leakage audit 报告。</summary>
public sealed class RetrievalDatasetV2StressReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int LeakageIssueCount { get; init; }

    public int UniqueAnchorLeakageCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RationaleLeakageCount { get; init; }

    public int SplitLeakageCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public double AnchorAblationRecallDelta { get; init; }

    public double AnchorShuffleRecallDelta { get; init; }

    public double DenseRecall { get; init; }

    public double LexicalRecall { get; init; }

    public double AnchorRecall { get; init; }

    public double HybridRecall { get; init; }

    public double HoldoutHybridRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2StressRecommendations.KeepPreviewOnly;

    public IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> Profiles { get; init; } =
        Array.Empty<RetrievalDatasetV2ShadowEvalProfileReport>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Retrieval Dataset V2 stress recall failure 分类。</summary>
public static class RetrievalDatasetV2StressFailureReasons
{
    public const string MustHitMissingFromCandidateSet = nameof(MustHitMissingFromCandidateSet);
    public const string MustHitBelowTopK = nameof(MustHitBelowTopK);
    public const string MustHitBlockedByEligibility = nameof(MustHitBlockedByEligibility);
    public const string TargetSectionMismatch = nameof(TargetSectionMismatch);
    public const string DenseSemanticMismatch = nameof(DenseSemanticMismatch);
    public const string LexicalTokenMismatch = nameof(LexicalTokenMismatch);
    public const string AnchorMetadataInsufficient = nameof(AnchorMetadataInsufficient);
    public const string AnchorRankingRegression = nameof(AnchorRankingRegression);
    public const string HybridUnionRankingRegression = nameof(HybridUnionRankingRegression);
    public const string NegativeDistractorOutranksMustHit = nameof(NegativeDistractorOutranksMustHit);
    public const string QueryTooSparse = nameof(QueryTooSparse);
    public const string MultiHopRelationNotRepresented = nameof(MultiHopRelationNotRepresented);
    public const string LifecycleTrapTooAmbiguous = nameof(LifecycleTrapTooAmbiguous);
    public const string Unknown = nameof(Unknown);
}

/// <summary>Retrieval Dataset V2 stress recall failure triage recommendation。</summary>
public static class RetrievalDatasetV2StressFailureTriageRecommendations
{
    public const string ReadyForRankingRepairPreview = nameof(ReadyForRankingRepairPreview);
    public const string NeedsAnchorRankingRepair = nameof(NeedsAnchorRankingRepair);
    public const string NeedsQueryNormalizationRepair = nameof(NeedsQueryNormalizationRepair);
    public const string NeedsRelationAwareCandidateProvider = nameof(NeedsRelationAwareCandidateProvider);
    public const string NeedsHybridUnionScoringRepair = nameof(NeedsHybridUnionScoringRepair);
    public const string NeedsHarderNegativePolicy = nameof(NeedsHarderNegativePolicy);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>单条 stress recall failure detail。</summary>
public sealed class RetrievalDatasetV2StressRecallFailureDetail
{
    public string SampleId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Difficulty { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DenseTopK { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LexicalTopK { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AnchorTopK { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> HybridTopK { get; init; } = Array.Empty<string>();

    public bool MustHitPresentInCorpus { get; init; }

    public bool MustHitPresentInCandidateSet { get; init; }

    public int MustHitRank { get; init; }

    public bool MustHitBlockedByEligibility { get; init; }

    public bool MustHitTargetSectionMismatch { get; init; }

    public string NearestWrongCandidateId { get; init; } = string.Empty;

    public string NearestWrongCandidateKind { get; init; } = string.Empty;

    public string FailureReason { get; init; } = RetrievalDatasetV2StressFailureReasons.Unknown;

    public string RecommendedRepair { get; init; } = string.Empty;
}

/// <summary>Stress failure triage 的 profile 对比统计。</summary>
public sealed class RetrievalDatasetV2StressProfileComparison
{
    public string ComparisonName { get; init; } = string.Empty;

    public string LeftProfileName { get; init; } = string.Empty;

    public string RightProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int LeftHitCount { get; init; }

    public int RightHitCount { get; init; }

    public int LeftOnlyWinCount { get; init; }

    public int RightOnlyWinCount { get; init; }

    public int BothHitCount { get; init; }

    public int BothMissCount { get; init; }

    public double LeftRecall { get; init; }

    public double RightRecall { get; init; }
}

/// <summary>Retrieval Dataset V2 stress recall failure triage / clusters report。</summary>
public sealed class RetrievalDatasetV2StressRecallFailureTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int FailureCount { get; init; }

    public IReadOnlyDictionary<string, int> FailureCountBySplit { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> FailureCountByDifficulty { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> FailureCountByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int HoldoutFailureCount { get; init; }

    public int DenseOnlyWinCount { get; init; }

    public int HybridWinCount { get; init; }

    public int AnchorRegressionCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int MustHitMissingFromCandidateSetCount { get; init; }

    public int EligibilityBlockedCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2StressFailureTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyList<RetrievalDatasetV2StressProfileComparison> ProfileComparisons { get; init; } =
        Array.Empty<RetrievalDatasetV2StressProfileComparison>();

    public IReadOnlyList<RetrievalDatasetV2StressRecallFailureDetail> Failures { get; init; } =
        Array.Empty<RetrievalDatasetV2StressRecallFailureDetail>();
}

/// <summary>Hybrid union scoring repair preview 配置；仅用于离线评估。</summary>
public sealed class HybridUnionScoringRepairOptions
{
    public bool Enabled { get; init; }

    public bool DensePreservationEnabled { get; init; } = true;

    public bool DenseWinnerFloorEnabled { get; init; } = true;

    public bool NegativeDistractorPenaltyEnabled { get; init; } = true;

    public bool AnchorScoreCapEnabled { get; init; } = true;

    public bool ContributionAwareRerankEnabled { get; init; } = true;

    public int MaxRiskAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>Hybrid union scoring repair profile 名称。</summary>
public static class HybridUnionScoringRepairProfiles
{
    public const string BaselineHybridFull = "baseline-hybrid-full";
    public const string DensePreservingUnionV1 = "dense-preserving-union-v1";
    public const string DenseWinnerFloorV1 = "dense-winner-floor-v1";
    public const string NegativeDistractorPenaltyV1 = "negative-distractor-penalty-v1";
    public const string PostScoringRiskGatedV1 = "post-scoring-risk-gated-v1";
    public const string AnchorScoreCappedV1 = "anchor-score-capped-v1";
    public const string ContributionAwareRerankV1 = "contribution-aware-rerank-v1";
    public const string CombinedSafeV1 = "combined-safe-v1";
}

/// <summary>Hybrid union scoring repair recommendation。</summary>
public static class HybridUnionScoringRepairRecommendations
{
    public const string ReadyForDatasetV2StressFreeze = nameof(ReadyForDatasetV2StressFreeze);
    public const string NeedsMoreRankingRepair = nameof(NeedsMoreRankingRepair);
    public const string BlockedByDenseRegression = nameof(BlockedByDenseRegression);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByNegativeDistractor = nameof(BlockedByNegativeDistractor);
    public const string BlockedByAnchorRegression = nameof(BlockedByAnchorRegression);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Hybrid union scoring repair 单个 profile 评估。</summary>
public sealed class HybridUnionScoringRepairProfileReport
{
    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double HoldoutRecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public double DenseOnlyRecall { get; init; }

    public double DenseOnlyHoldoutRecall { get; init; }

    public double RecallDeltaVsDense { get; init; }

    public double HoldoutRecallDeltaVsDense { get; init; }

    public int HybridRegressionCount { get; init; }

    public int DenseWinnerPreservedCount { get; init; }

    public int DenseWinnerLostCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int NegativeDistractorOutranksMustHitCount { get; init; }

    public int AnchorRankingRegressionCount { get; init; }

    public string Recommendation { get; init; } = HybridUnionScoringRepairRecommendations.KeepPreviewOnly;
}

/// <summary>Hybrid union scoring repair preview / gate 报告。</summary>
public sealed class HybridUnionScoringRepairReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string BestProfileName { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = HybridUnionScoringRepairRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<HybridUnionScoringRepairProfileReport> Profiles { get; init; } =
        Array.Empty<HybridUnionScoringRepairProfileReport>();
}

/// <summary>Hybrid scoring risk regression 分类。</summary>
public static class HybridScoringRiskRegressionReasons
{
    public const string BlockedCandidateReintroduced = nameof(BlockedCandidateReintroduced);
    public const string EligibilityPolicyBypassed = nameof(EligibilityPolicyBypassed);
    public const string MustNotCandidatePromoted = nameof(MustNotCandidatePromoted);
    public const string DeprecatedCandidatePromoted = nameof(DeprecatedCandidatePromoted);
    public const string HistoricalCandidatePromoted = nameof(HistoricalCandidatePromoted);
    public const string LifecycleRiskPromoted = nameof(LifecycleRiskPromoted);
    public const string TargetSectionMismatchPromoted = nameof(TargetSectionMismatchPromoted);
    public const string NegativePenaltyOverPromotedWrongCandidate = nameof(NegativePenaltyOverPromotedWrongCandidate);
    public const string ScoreFusionOrderBug = nameof(ScoreFusionOrderBug);
    public const string RiskProjectionMismatch = nameof(RiskProjectionMismatch);
    public const string Unknown = nameof(Unknown);
}

/// <summary>Hybrid scoring risk regression triage recommendation。</summary>
public static class HybridScoringRiskRegressionRecommendations
{
    public const string ReadyForSafeScoringRepair = nameof(ReadyForSafeScoringRepair);
    public const string NeedsPostScoringRiskGate = nameof(NeedsPostScoringRiskGate);
    public const string NeedsScoreCap = nameof(NeedsScoreCap);
    public const string NeedsEligibilityOrderFix = nameof(NeedsEligibilityOrderFix);
    public const string NeedsRiskProjectionFix = nameof(NeedsRiskProjectionFix);
    public const string UnsafeProfileDiscard = nameof(UnsafeProfileDiscard);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Hybrid scoring risk regression 单条风险候选。</summary>
public sealed class HybridScoringRiskRegressionDetail
{
    public string SampleId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Difficulty { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public int CandidateRank { get; init; }

    public string CandidateKind { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string RiskType { get; init; } = string.Empty;

    public string RiskReason { get; init; } = HybridScoringRiskRegressionReasons.Unknown;

    public bool WasEligibleBeforeRepair { get; init; }

    public bool WasBlockedBeforeRepair { get; init; }

    public int DenseRank { get; init; }

    public int LexicalRank { get; init; }

    public int AnchorRank { get; init; }

    public int BaselineHybridRank { get; init; }

    public int RepairedHybridRank { get; init; }

    public double ScoreBeforeRepair { get; init; }

    public double ScoreAfterRepair { get; init; }

    public double ScoreDelta { get; init; }

    public string ContributionSource { get; init; } = string.Empty;

    public int NearestMustHitRank { get; init; }

    public int NearestMustNotRank { get; init; }

    public string RecommendedFix { get; init; } = string.Empty;
}

/// <summary>Hybrid scoring risk regression triage report。</summary>
public sealed class HybridScoringRiskRegressionTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int RiskCandidateCount { get; init; }

    public IReadOnlyDictionary<string, int> RiskByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RiskByDifficulty { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RiskBySplit { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int BlockedCandidateReintroducedCount { get; init; }

    public int MustNotCandidatePromotedCount { get; init; }

    public int LifecycleRiskPromotedCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public int EligibilityBypassCount { get; init; }

    public int RepairableByPostScoringRiskGateCount { get; init; }

    public int RepairableByScoreCapCount { get; init; }

    public int UnsafeProfileCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = HybridScoringRiskRegressionRecommendations.KeepPreviewOnly;

    public IReadOnlyList<HybridScoringRiskRegressionDetail> Details { get; init; } =
        Array.Empty<HybridScoringRiskRegressionDetail>();
}

/// <summary>Dataset V2 stress freeze 的输出状态。</summary>
public static class RetrievalDatasetV2StressFreezeStatuses
{
    public const string ReadyForV4RecheckInput = nameof(ReadyForV4RecheckInput);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Dataset V2 stress freeze recommendation。</summary>
public static class RetrievalDatasetV2StressFreezeRecommendations
{
    public const string ReadyForV4RecheckInput = nameof(ReadyForV4RecheckInput);
    public const string BlockedByMissingReport = nameof(BlockedByMissingReport);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string BlockedByAnchorDominance = nameof(BlockedByAnchorDominance);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByHybridScoringRisk = nameof(BlockedByHybridScoringRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeUse = nameof(BlockedByRuntimeUse);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Dataset V2 stress freeze gate；仅允许作为 V4 复核输入，不允许直接进入正式检索。</summary>
public sealed class RetrievalDatasetV2StressFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public bool FreezePassed { get; init; }

    public string DatasetV2Stress { get; init; } = RetrievalDatasetV2StressFreezeStatuses.KeepPreviewOnly;

    public string BestPreviewProfile { get; init; } = string.Empty;

    public double StressRecall { get; init; }

    public double HoldoutRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int LeakageIssueCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public bool MaterializationGatePassed { get; init; }

    public bool SmallSetReadinessGatePassed { get; init; }

    public string StressReadinessRecommendation { get; init; } = string.Empty;

    public bool StressFailureTriageCompleted { get; init; }

    public bool HybridScoringRepairGatePassed { get; init; }

    public int HybridScoringRiskCandidateCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool V4RecheckAllowed { get; init; }

    public bool ReadyForFormalRetrieval { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2StressFreezeRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Vector V4.R readiness recheck 的推荐结论；通过也只允许进入 guarded formal preview。</summary>
public static class VectorV4ReadinessRecheckRecommendations
{
    public const string ReadyForGuardedFormalPreview = nameof(ReadyForGuardedFormalPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByLegacyRisk = nameof(BlockedByLegacyRisk);
    public const string BlockedByDatasetV2Stress = nameof(BlockedByDatasetV2Stress);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByProviderParity = nameof(BlockedByProviderParity);
}

/// <summary>Vector V4.R readiness recheck；只允许产生 guarded formal preview 输入，不启用正式 retrieval。</summary>
public sealed class VectorV4ReadinessRecheckReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool RecheckPassed { get; init; }

    public string Recommendation { get; init; } = VectorV4ReadinessRecheckRecommendations.KeepPreviewOnly;

    public string LegacyVectorStatus { get; init; } = "Unknown";

    public string DatasetV2SmallStatus { get; init; } = "Unknown";

    public string DatasetV2StressStatus { get; init; } = "Unknown";

    public string PgVectorProviderStatus { get; init; } = "Unknown";

    public string Qwen3ProviderComparisonStatus { get; init; } = "Unknown";

    public string HybridRetrievalStatus { get; init; } = "Unknown";

    public string HybridScoringRepairStatus { get; init; } = "Unknown";

    public string RuntimeChangeGateStatus { get; init; } = "Unknown";

    public string BestPreviewProfile { get; init; } = string.Empty;

    public double DatasetV2StressRecall { get; init; }

    public double DatasetV2HoldoutRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int LeakageIssueCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ReadyForGuardedFormalPreview { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Guarded formal retrieval preview 配置；只允许离线比较，不启用正式检索。</summary>
public sealed class GuardedFormalRetrievalPreviewOptions
{
    public bool Enabled { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireV4RecheckPassed { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool CompareWithCurrentFormal { get; init; } = true;

    public bool FailClosedOnRisk { get; init; } = true;

    public int MaxRiskAllowed { get; init; }
}

/// <summary>Guarded formal retrieval preview recommendation。</summary>
public static class GuardedFormalRetrievalPreviewRecommendations
{
    public const string ReadyForShadowPackageComparison = nameof(ReadyForShadowPackageComparison);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
}

/// <summary>Guarded formal retrieval preview / gate；只输出 would-change 结果，不写正式 package。</summary>
public sealed class GuardedFormalRetrievalPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool V4RecheckPassed { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselineCandidateCount { get; init; }

    public int PreviewVectorCandidateCount { get; init; }

    public int WouldAddCount { get; init; }

    public int WouldRemoveCount { get; init; }

    public int WouldRerankCount { get; init; }

    public int WouldChangeTargetSectionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = GuardedFormalRetrievalPreviewRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Shadow package comparison 配置；只构建离线 shadow package envelope。</summary>
public sealed class VectorShadowPackageComparisonOptions
{
    public bool Enabled { get; init; }

    public bool RequireGuardedFormalPreviewPassed { get; init; } = true;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool BuildShadowPackage { get; init; } = true;

    public bool CompareWithBaseline { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool FailClosedOnRisk { get; init; } = true;

    public int MaxRiskAllowed { get; init; }
}

/// <summary>Shadow package comparison recommendation。</summary>
public static class VectorShadowPackageComparisonRecommendations
{
    public const string ReadyForScopedFormalPreviewOptIn = nameof(ReadyForScopedFormalPreviewOptIn);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByTokenBudgetRegression = nameof(BlockedByTokenBudgetRegression);
    public const string BlockedByConstraintCoverageRegression = nameof(BlockedByConstraintCoverageRegression);
}

/// <summary>Shadow package comparison 报告；不写正式 package，不改变 runtime。</summary>
public sealed class VectorShadowPackageComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ComparisonPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ShadowPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = VectorShadowPackageComparisonRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Scoped formal preview opt-in mode。</summary>
public static class ScopedFormalPreviewOptInModes
{
    public const string Off = nameof(Off);
    public const string PreviewOnly = nameof(PreviewOnly);
}

/// <summary>Scoped formal preview opt-in 配置；只允许显式 scope 的 preview-only 路径。</summary>
public sealed class ScopedFormalPreviewOptInOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.Off;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string SelectedWorkspaceId { get; init; } = string.Empty;

    public string SelectedCollectionId { get; init; } = string.Empty;

    public string SelectedEvalScope { get; init; } = string.Empty;

    public string NonAllowlistedWorkspaceId { get; init; } = string.Empty;

    public string NonAllowlistedCollectionId { get; init; } = string.Empty;

    public string NonAllowlistedEvalScope { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireV4RecheckPassed { get; init; } = true;

    public bool RequireGuardedFormalPreviewPassed { get; init; } = true;

    public bool RequireShadowPackageComparisonPassed { get; init; } = true;

    public bool WriteFormalPackage { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }
}

/// <summary>Scoped formal preview opt-in recommendation。</summary>
public static class ScopedFormalPreviewOptInRecommendations
{
    public const string ReadyForLimitedFormalPreviewObservation = nameof(ReadyForLimitedFormalPreviewObservation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}

/// <summary>Scoped formal preview opt-in report；只写 shadow artifact，不写正式 package。</summary>
public sealed class ScopedFormalPreviewOptInReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public bool SmokePassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.Off;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string SelectedWorkspaceId { get; init; } = string.Empty;

    public string SelectedCollectionId { get; init; } = string.Empty;

    public string SelectedEvalScope { get; init; } = string.Empty;

    public string NonAllowlistedWorkspaceId { get; init; } = string.Empty;

    public string NonAllowlistedCollectionId { get; init; } = string.Empty;

    public string NonAllowlistedEvalScope { get; init; } = string.Empty;

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public string Recommendation { get; init; } = ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> GateDependencySummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Limited formal preview observation 配置；只做多轮 shadow preview 观测。</summary>
public sealed class LimitedFormalPreviewObservationOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.PreviewOnly;

    public int ObservationWindowRuns { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireScopedFormalPreviewOptInPassed { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool FailClosedOnRisk { get; init; } = true;
}

/// <summary>Limited formal preview observation recommendation。</summary>
public static class LimitedFormalPreviewObservationRecommendations
{
    public const string ReadyForFormalPreviewFreeze = nameof(ReadyForFormalPreviewFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Limited formal preview observation report；聚合多轮 preview-only package comparison。</summary>
public sealed class LimitedFormalPreviewObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.PreviewOnly;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MinimumObservationRunCount { get; init; }

    public int ObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int TokenDeltaP95 { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Formal preview freeze 状态。</summary>
public static class VectorFormalPreviewFreezeStatuses
{
    public const string ReadyForScopedOptInPreview = nameof(ReadyForScopedOptInPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Formal preview freeze recommendation。</summary>
public static class VectorFormalPreviewFreezeRecommendations
{
    public const string ReadyForScopedOptInPreview = nameof(ReadyForScopedOptInPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
}

/// <summary>Formal preview freeze gate report；只冻结 preview-only 许可，不启用 runtime。</summary>
public sealed class VectorFormalPreviewFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string VectorFormalPreview { get; init; } = VectorFormalPreviewFreezeStatuses.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ScopedPreviewOnly";

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool V4ReadinessRecheckPassed { get; init; }

    public bool GuardedFormalPreviewGatePassed { get; init; }

    public bool ShadowPackageComparisonGatePassed { get; init; }

    public bool ScopedFormalPreviewOptInGatePassed { get; init; }

    public bool LimitedFormalPreviewObservationGatePassed { get; init; }

    public bool RuntimeChangeReadinessGatePassed { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public IReadOnlyList<string> ForbiddenChanges { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = VectorFormalPreviewFreezeRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Explicit scoped runtime experiment planning mode；只允许计划和 dry-run。</summary>
public static class ExplicitScopedRuntimeExperimentModes
{
    public const string PlanOnly = nameof(PlanOnly);
    public const string DryRun = nameof(DryRun);
}

/// <summary>Explicit scoped runtime experiment planning recommendation。</summary>
public static class ExplicitScopedRuntimeExperimentRecommendations
{
    public const string ReadyForExplicitScopedRuntimeExperimentDryRun = nameof(ReadyForExplicitScopedRuntimeExperimentDryRun);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingFoundationFreeze = nameof(BlockedByMissingFoundationFreeze);
    public const string BlockedByMissingServiceFreeze = nameof(BlockedByMissingServiceFreeze);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Explicit scoped runtime experiment planning 配置；不启用 runtime。</summary>
public sealed class ExplicitScopedRuntimeExperimentPlanOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ExplicitScopedRuntimeExperimentModes.PlanOnly;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireFoundationFreeze { get; init; } = true;

    public bool RequireServiceFoundationFreeze { get; init; } = true;

    public bool RequireVectorFormalPreviewFreeze { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }
}

/// <summary>Explicit scoped runtime experiment planning report；只描述计划和 dry-run 边界。</summary>
public sealed class ExplicitScopedRuntimeExperimentPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = ExplicitScopedRuntimeExperimentRecommendations.KeepPreviewOnly;

    public string Mode { get; init; } = ExplicitScopedRuntimeExperimentModes.PlanOnly;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string RollbackPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ObservationMetrics { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool DryRunSupported { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Scoped runtime experiment dry-run observation mode；只允许 dry-run。</summary>
public static class ScopedRuntimeExperimentDryRunObservationModes
{
    public const string DryRun = nameof(DryRun);
}

/// <summary>Scoped runtime experiment dry-run observation recommendation。</summary>
public static class ScopedRuntimeExperimentDryRunObservationRecommendations
{
    public const string ReadyForScopedRuntimeExperimentDesignFreeze = nameof(ReadyForScopedRuntimeExperimentDesignFreeze);
    public const string NeedsMoreDryRunObservation = nameof(NeedsMoreDryRunObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Scoped runtime experiment dry-run observation 配置；不启用 runtime。</summary>
public sealed class ScopedRuntimeExperimentDryRunObservationOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentDryRunObservationModes.DryRun;

    public int ObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool RequireV45PlanPassed { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool FailClosedOnRisk { get; init; } = true;

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }
}

/// <summary>Scoped runtime experiment dry-run observation report；只聚合 dry-run 观测和边界检查。</summary>
public sealed class ScopedRuntimeExperimentDryRunObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentDryRunObservationModes.DryRun;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int ObservationRunCount { get; init; }

    public int MinimumObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int DryRunPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool RuntimeChangeGateConsistent { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } =
        ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Scoped runtime experiment design freeze status。</summary>
public static class ScopedRuntimeExperimentDesignFreezeStatuses
{
    public const string Frozen = nameof(Frozen);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Scoped runtime experiment design freeze recommendation。</summary>
public static class ScopedRuntimeExperimentDesignFreezeRecommendations
{
    public const string ReadyForRuntimeExperimentProposal = nameof(ReadyForRuntimeExperimentProposal);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDryRunObservation = nameof(BlockedByMissingDryRunObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
}

/// <summary>Scoped runtime experiment design freeze report；冻结设计边界，不启用 runtime。</summary>
public sealed class ScopedRuntimeExperimentDesignFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; } =
        ScopedRuntimeExperimentDesignFreezeRecommendations.KeepPreviewOnly;

    public string DesignStatus { get; init; } = ScopedRuntimeExperimentDesignFreezeStatuses.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ExplicitScopedRuntimeExperimentOnly";

    public int AllowlistedScopeCount { get; init; }

    public int ObservationRunCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool ReadyForRuntimeExperimentProposal { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyIntegrationAllowed { get; init; }

    public bool GlobalDefaultOnAllowed { get; init; }

    public bool FoundationReleaseCandidateGatePassed { get; init; }

    public bool ServiceFoundationFreezeGatePassed { get; init; }

    public bool VectorFormalPreviewFreezeGatePassed { get; init; }

    public bool ScopedRuntimeExperimentGatePassed { get; init; }

    public bool DryRunObservationGatePassed { get; init; }

    public bool RuntimeChangeReadinessGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Explicit scoped runtime experiment proposal mode；只允许 proposal。</summary>
public static class ExplicitScopedRuntimeExperimentProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
}

/// <summary>Explicit scoped runtime experiment proposal recommendation。</summary>
public static class ExplicitScopedRuntimeExperimentProposalRecommendations
{
    public const string ReadyForManualExperimentApproval = nameof(ReadyForManualExperimentApproval);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Explicit scoped runtime experiment proposal 配置；只生成 proposal / config preview。</summary>
public sealed class ExplicitScopedRuntimeExperimentProposalOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string EvalScopeId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public string Mode { get; init; } = ExplicitScopedRuntimeExperimentProposalModes.ProposalOnly;

    public bool RequireV47DesignFreeze { get; init; } = true;

    public bool RequireFoundationFreeze { get; init; } = true;

    public bool RequireServiceFoundationFreeze { get; init; } = true;

    public bool RequireVectorFormalPreviewFreeze { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool RequireManualApproval { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public bool Approved { get; init; }
}

/// <summary>Explicit scoped runtime experiment proposal report；不写 runtime 配置。</summary>
public sealed class ExplicitScopedRuntimeExperimentProposalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public bool ProposalPassed { get; init; }

    public string Recommendation { get; init; } =
        ExplicitScopedRuntimeExperimentProposalRecommendations.KeepPreviewOnly;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string EvalScopeId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ProposedConfigPatch { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ObservationPlan { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool ApprovalRequired { get; init; }

    public bool Approved { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool ConfigPatchWritten { get; init; }

    public bool DiBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Scoped runtime experiment approval mode；V4.9 只允许 no-op harness。</summary>
public static class ScopedRuntimeExperimentApprovalModes
{
    public const string NoOpHarnessOnly = nameof(NoOpHarnessOnly);
    public const string ScopedRuntimeExperiment = nameof(ScopedRuntimeExperiment);
}

/// <summary>Scoped runtime experiment approval/no-op harness recommendation。</summary>
public static class ScopedRuntimeExperimentApprovalRecommendations
{
    public const string ReadyForActivationPreflight = nameof(ReadyForActivationPreflight);
    public const string ReadyForScopedRuntimeExperimentDryRunHarnessFreeze = nameof(ReadyForScopedRuntimeExperimentDryRunHarnessFreeze);
    public const string NeedsManualApproval = nameof(NeedsManualApproval);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByExpiredApproval = nameof(BlockedByExpiredApproval);
    public const string BlockedByRevokedApproval = nameof(BlockedByRevokedApproval);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByWrongApprovalMode = nameof(BlockedByWrongApprovalMode);
    public const string BlockedByMissingAcknowledgement = nameof(BlockedByMissingAcknowledgement);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Scoped runtime experiment manual approval；只授权 no-op harness，不授权 runtime switch。</summary>
public sealed class ScopedRuntimeExperimentApprovalRecord
{
    public string ApprovalId { get; init; } = string.Empty;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovedBy { get; init; } = string.Empty;

    public DateTimeOffset ApprovedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ApprovalScope { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public string Reason { get; init; } = string.Empty;

    public string RiskAcknowledgement { get; init; } = string.Empty;

    public string RollbackAcknowledgement { get; init; } = string.Empty;

    public string KillSwitchAcknowledgement { get; init; } = string.Empty;

    public string ScopeAcknowledgement { get; init; } = string.Empty;

    public string ObservationPlanAcknowledgement { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Revoked { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Scoped runtime experiment approval 配置；不会开启正式 runtime。</summary>
public sealed class ScopedRuntimeExperimentApprovalOptions
{
    public string ProposalId { get; init; } = string.Empty;

    public string ApprovedBy { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public bool RequireExplicitConfirm { get; init; } = true;

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public bool AllowRuntimeSwitch { get; init; }

    public bool AllowFormalRetrieval { get; init; }

    public bool AllowFormalPackageWrite { get; init; }

    public bool AllowPackingPolicyChange { get; init; }

    public string RiskAcknowledgement { get; init; } = string.Empty;

    public string RollbackAcknowledgement { get; init; } = string.Empty;

    public string KillSwitchAcknowledgement { get; init; } = string.Empty;

    public string ScopeAcknowledgement { get; init; } = string.Empty;

    public string ObservationPlanAcknowledgement { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Scoped runtime experiment approval preview/write report。</summary>
public sealed class ScopedRuntimeExperimentApprovalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool ApprovalPassed { get; init; }

    public bool PreviewOnly { get; init; } = true;

    public bool RecordWritten { get; init; }

    public bool Confirmed { get; init; }

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public string ApprovedBy { get; init; } = string.Empty;

    public bool RollbackPlanAvailable { get; init; }

    public bool KillSwitchPlanAvailable { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyChangeAllowed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public ScopedRuntimeExperimentApprovalRecord? ApprovalRecord { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Scoped runtime experiment approval summary；只读取 approval artifact。</summary>
public sealed class ScopedRuntimeExperimentApprovalSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public int ApprovalCount { get; init; }

    public bool ApprovalRecordExists { get; init; }

    public string LatestApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public bool Expired { get; init; }

    public bool Revoked { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>V4.12 runtime approval request preview；只展示审批材料，不写 approval record。</summary>
public sealed class ScopedRuntimeExperimentApprovalRequestPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyList<string> ObservationPlan { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public bool PreviewOnly { get; init; } = true;

    public bool RecordWritten { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval;
}

/// <summary>V4.12 scoped runtime experiment approval gate；不授权 runtime switch。</summary>
public sealed class ScopedRuntimeExperimentApprovalGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string ApprovedBy { get; init; } = string.Empty;

    public bool ApprovalExists { get; init; }

    public bool ApprovalExpired { get; init; }

    public bool ApprovalRevoked { get; init; }

    public bool RequiredAcknowledgementsPresent { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyIntegrationAllowed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>V4.13 activation preflight mode；只允许 preflight + dry-run route。</summary>
public static class ScopedRuntimeExperimentActivationPreflightModes
{
    public const string PreflightAndDryRunRoute = nameof(PreflightAndDryRunRoute);
}

/// <summary>V4.13 activation preflight recommendation。</summary>
public static class ScopedRuntimeExperimentActivationPreflightRecommendations
{
    public const string ReadyForGuardedScopedRuntimeExperiment = nameof(ReadyForGuardedScopedRuntimeExperiment);
    public const string NeedsActivationConfig = nameof(NeedsActivationConfig);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingTraceSink = nameof(BlockedByMissingTraceSink);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>V4.13 activation preflight options；只做 dry-run route，不写正式配置。</summary>
public sealed class ScopedRuntimeExperimentActivationPreflightOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute;

    public bool RequireV411PlanPassed { get; init; } = true;

    public bool RequireV412ApprovalPassed { get; init; } = true;

    public bool RequireFoundationFreeze { get; init; } = true;

    public bool RequireServiceFoundationFreeze { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireTraceSink { get; init; } = true;

    public bool TraceSinkAvailable { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool MutateRuntime { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }
}

/// <summary>V4.13 activation preflight / guarded runtime dry-run route report。</summary>
public sealed class ScopedRuntimeExperimentActivationPreflightReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreflightPassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentActivationPreflightRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public bool KillSwitchAvailable { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool TraceSinkAvailable { get; init; }

    public bool ConfigPatchPreviewed { get; init; }

    public bool ConfigPatchWritten { get; init; }

    public bool RuntimeRouteDryRunExecuted { get; init; }

    public int DryRunRouteHitCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>V4.14 guarded scoped runtime experiment mode；仅允许 scoped shadow runtime experiment。</summary>
public static class GuardedScopedRuntimeExperimentModes
{
    public const string ShadowRuntimeExperiment = nameof(ShadowRuntimeExperiment);
}

/// <summary>V4.14 guarded scoped runtime experiment recommendation。</summary>
public static class GuardedScopedRuntimeExperimentRecommendations
{
    public const string ReadyForScopedRuntimeExperimentObservation = nameof(ReadyForScopedRuntimeExperimentObservation);
    public const string NeedsMoreExperimentRuns = nameof(NeedsMoreExperimentRuns);
    public const string BlockedByMissingActivationGate = nameof(BlockedByMissingActivationGate);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByWrongApprovalMode = nameof(BlockedByWrongApprovalMode);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByRollbackFailure = nameof(BlockedByRollbackFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>V4.14 guarded scoped runtime experiment options；只允许 allowlisted scope 的 shadow route。</summary>
public sealed class GuardedScopedRuntimeExperimentOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MaxRequestCount { get; init; } = 120;

    public int MaxDurationMinutes { get; init; } = 30;

    public int MaxErrorCount { get; init; }

    public bool RequireV413PreflightPassed { get; init; } = true;

    public bool RequireScopedRuntimeExperimentApproval { get; init; } = true;

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireTraceSink { get; init; } = true;

    public bool TraceSinkAvailable { get; init; } = true;

    public bool WriteFormalPackage { get; init; }

    public bool MutateFormalOutput { get; init; }

    public bool MutatePackingPolicy { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public bool RollbackVerified { get; init; } = true;

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ErrorCount { get; init; }
}

/// <summary>V4.14 scoped runtime experiment trace；shadow route 只写观测 trace。</summary>
public sealed class ScopedRuntimeExperimentTrace
{
    public string RequestId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool ScopeMatched { get; init; }

    public bool ExperimentRouteHit { get; init; }

    public string BaselinePackageId { get; init; } = string.Empty;

    public string ExperimentPackagePreviewId { get; init; } = string.Empty;

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public string Error { get; init; } = string.Empty;

    public int DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>V4.14 guarded scoped runtime experiment report；正式 retrieval/package 保持不变。</summary>
public sealed class GuardedScopedRuntimeExperimentReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ExperimentPassed { get; init; }

    public string Recommendation { get; init; } = GuardedScopedRuntimeExperimentRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedRequestCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ExperimentPreviewPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public bool RollbackVerified { get; init; }

    public int ErrorCount { get; init; }

    public int LatencyP50 { get; init; }

    public int LatencyP95 { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<ScopedRuntimeExperimentTrace> Traces { get; init; } = Array.Empty<ScopedRuntimeExperimentTrace>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>V4.15 scoped runtime experiment observation window mode；仅允许 scoped shadow observation。</summary>
public static class ScopedRuntimeExperimentObservationWindowModes
{
    public const string ScopedShadowObservation = nameof(ScopedShadowObservation);
}

/// <summary>V4.15 scoped runtime experiment observation window recommendation。</summary>
public static class ScopedRuntimeExperimentObservationWindowRecommendations
{
    public const string ReadyForScopedRuntimeExperimentObservationFreeze = nameof(ReadyForScopedRuntimeExperimentObservationFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByTraceGap = nameof(BlockedByTraceGap);
    public const string BlockedByLatency = nameof(BlockedByLatency);
    public const string BlockedByRollbackFailure = nameof(BlockedByRollbackFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>V4.15 scoped runtime experiment observation window options；扩展 V4.14 shadow route 的观测窗口。</summary>
public sealed class ScopedRuntimeExperimentObservationWindowOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ObservationWindowId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int MinRequestCount { get; init; } = 360;

    public int ObservationRunCount { get; init; } = 3;

    public int MaxDurationMinutes { get; init; } = 30;

    public int MaxErrorCount { get; init; }

    public int MaxLatencyP95Ms { get; init; } = 1_000;

    public bool RequireV414GatePassed { get; init; } = true;

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireTraceSink { get; init; } = true;

    public bool TraceSinkAvailable { get; init; } = true;

    public bool WriteFormalPackage { get; init; }

    public bool MutateFormalOutput { get; init; }

    public bool MutatePackingPolicy { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool KillSwitchAvailable { get; init; } = true;

    public bool KillSwitchSmokePassed { get; init; } = true;

    public bool RollbackVerified { get; init; } = true;

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ErrorCount { get; init; }

    public int? LatencyP50 { get; init; }

    public int? LatencyP95 { get; init; }

    public double TraceCompleteness { get; init; } = 100;
}

/// <summary>V4.15 scoped runtime experiment observation window report；只写 shadow artifact/trace。</summary>
public sealed class ScopedRuntimeExperimentObservationWindowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ObservationWindowId { get; init; } = string.Empty;

    public bool ObservationPassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int ObservationRunCount { get; init; }

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedRequestCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ExperimentPreviewPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchSmokePassed { get; init; }

    public bool RollbackVerified { get; init; }

    public double TraceCompleteness { get; init; }

    public int ErrorCount { get; init; }

    public int LatencyP50 { get; init; }

    public int LatencyP95 { get; init; }

    public bool StopConditionTriggered { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<ScopedRuntimeExperimentTrace> Traces { get; init; } = Array.Empty<ScopedRuntimeExperimentTrace>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>V4.16 scoped runtime experiment observation freeze / promotion decision。</summary>
public static class ScopedRuntimeExperimentObservationFreezeDecisions
{
    public const string KeepScopedObservation = nameof(KeepScopedObservation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string ReadyForFormalRetrievalIntegrationPlan = nameof(ReadyForFormalRetrievalIntegrationPlan);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByOutputChange = nameof(BlockedByOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByTraceGap = nameof(BlockedByTraceGap);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}

/// <summary>V4.16 observation freeze report；只冻结主线决策，不允许 runtime/formal package 变更。</summary>
public sealed class ScopedRuntimeExperimentObservationFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string PromotionDecision { get; init; } = ScopedRuntimeExperimentObservationFreezeDecisions.KeepPreviewOnly;

    public string Recommendation { get; init; } = ScopedRuntimeExperimentObservationFreezeDecisions.KeepPreviewOnly;

    public string ObservationWindowId { get; init; } = string.Empty;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool V414GatePassed { get; init; }

    public bool V415GatePassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public int ObservationRunCount { get; init; }

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchSmokePassed { get; init; }

    public bool RollbackVerified { get; init; }

    public double TraceCompleteness { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>formal retrieval integration plan mode；本阶段只允许 PlanOnly。</summary>
public static class FormalRetrievalIntegrationPlanModes
{
    public const string PlanOnly = nameof(PlanOnly);
}

/// <summary>formal retrieval integration plan recommendation。</summary>
public static class FormalRetrievalIntegrationPlanRecommendations
{
    public const string ReadyForShadowFormalRetrievalAdapter = nameof(ReadyForShadowFormalRetrievalAdapter);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingPromotionDecision = nameof(BlockedByMissingPromotionDecision);
    public const string BlockedByP15Gate = nameof(BlockedByP15Gate);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByFormalOutputMutation = nameof(BlockedByFormalOutputMutation);
    public const string BlockedByPackageOutputMutation = nameof(BlockedByPackageOutputMutation);
    public const string BlockedByPackingPolicyMutation = nameof(BlockedByPackingPolicyMutation);
    public const string BlockedByVectorBindingMutation = nameof(BlockedByVectorBindingMutation);
}

/// <summary>formal retrieval integration plan；只规划接入点和下一阶段，不执行接入。</summary>
public sealed class FormalRetrievalIntegrationPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = FormalRetrievalIntegrationPlanRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = FormalRetrievalIntegrationPlanModes.PlanOnly;

    public string RequiredNextPhase { get; init; } = "ShadowFormalRetrievalAdapter";

    public bool V416PromotionDecisionPassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public string PromotionDecision { get; init; } = string.Empty;

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public IReadOnlyList<string> IntegrationPoints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string FallbackPathPlan { get; init; } = string.Empty;

    public string ConfigSwitchPlan { get; init; } = string.Empty;

    public string TraceComparisonOutputPlan { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>shadow formal retrieval adapter plan recommendations。</summary>
public static class ShadowFormalRetrievalAdapterPlanRecommendations
{
    public const string ReadyForShadowAdapterDesignFreeze = nameof(ReadyForShadowAdapterDesignFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingProjectStateAudit = nameof(BlockedByMissingProjectStateAudit);
    public const string BlockedByMissingPrerequisiteGate = nameof(BlockedByMissingPrerequisiteGate);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string BlockedByFormalRetrievalAttempt = nameof(BlockedByFormalRetrievalAttempt);
    public const string BlockedByPackageMutation = nameof(BlockedByPackageMutation);
    public const string BlockedByIncompleteAdapterPlan = nameof(BlockedByIncompleteAdapterPlan);
}

/// <summary>shadow formal retrieval adapter plan；只定义影子 adapter 设计，不接入正式检索。</summary>
public sealed class ShadowFormalRetrievalAdapterPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = ShadowFormalRetrievalAdapterPlanRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "PlanOnly";

    public string RequiredNextPhase { get; init; } = "ShadowFormalRetrievalAdapterDesignFreeze";

    public IReadOnlyList<string> AdapterInputs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AdapterOutputs { get; init; } = Array.Empty<string>();

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public IReadOnlyList<string> GateOrder { get; init; } = Array.Empty<string>();

    public string FallbackPath { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string TraceArtifactPlan { get; init; } = string.Empty;

    public string ComparisonArtifactPlan { get; init; } = string.Empty;

    public string LatencyBaselinePlan { get; init; } = string.Empty;

    public string AllocationBaselinePlan { get; init; } = string.Empty;

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public bool V50ProjectStateAuditPassed { get; init; }

    public bool V4FormalPreviewFreezeReadable { get; init; }

    public bool V416PromotionDecisionReadable { get; init; }

    public bool V414GuardedRuntimeExperimentReadable { get; init; }

    public bool V42ShadowPackageComparisonReadable { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>shadow formal retrieval adapter recommendations。</summary>
public static class ShadowFormalRetrievalAdapterRecommendations
{
    public const string ReadyForShadowAdapterFreeze = nameof(ReadyForShadowAdapterFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingPlanGate = nameof(BlockedByMissingPlanGate);
    public const string BlockedByPlanGateNotPassed = nameof(BlockedByPlanGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyShadowOutput = nameof(BlockedByEmptyShadowOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByTargetSectionViolation = nameof(BlockedByTargetSectionViolation);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalSelectedSetChange = nameof(BlockedByFormalSelectedSetChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}

/// <summary>shadow formal retrieval adapter per-sample trace。</summary>
public sealed class ShadowFormalRetrievalAdapterSampleResult
{
    public string SampleId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public int BaselineCandidateCount { get; init; }

    public int ShadowVectorCandidateCount { get; init; }

    public int ShadowGraphCandidateCount { get; init; }

    public int MergedShadowCandidateCount { get; init; }

    public int FilteredCandidateCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public IReadOnlyList<string> BaselineCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowVectorCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowGraphCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MergedShadowCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FilteredCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DropReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExplainNotes { get; init; } = Array.Empty<string>();
}

/// <summary>shadow formal retrieval adapter run report；只产出影子候选与 trace，不接入正式 retrieval。</summary>
public sealed class ShadowFormalRetrievalAdapterReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool AdapterPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = ShadowFormalRetrievalAdapterRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ShadowOnly";

    public string RequiredNextPhase { get; init; } = "ShadowFormalRetrievalAdapterFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public IReadOnlyList<string> AdapterInputs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AdapterOutputs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> GateOrder { get; init; } = Array.Empty<string>();

    public int SampleCount { get; init; }

    public int TotalBaselineCandidateCount { get; init; }

    public int TotalShadowVectorCandidateCount { get; init; }

    public int TotalShadowGraphCandidateCount { get; init; }

    public int TotalMergedShadowCandidateCount { get; init; }

    public int TotalFilteredCandidateCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<ShadowFormalRetrievalAdapterSampleResult> Samples { get; init; }
        = Array.Empty<ShadowFormalRetrievalAdapterSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>formal adapter package shadow comparison recommendations。</summary>
public static class FormalAdapterPackageShadowComparisonRecommendations
{
    public const string ReadyForFormalAdapterPackageShadowFreeze = nameof(ReadyForFormalAdapterPackageShadowFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingAdapterGate = nameof(BlockedByMissingAdapterGate);
    public const string BlockedByAdapterGateNotPassed = nameof(BlockedByAdapterGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyShadowOutput = nameof(BlockedByEmptyShadowOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByTokenBudgetExceeded = nameof(BlockedByTokenBudgetExceeded);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalSelectedSetChange = nameof(BlockedByFormalSelectedSetChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByFormalPackageWritten = nameof(BlockedByFormalPackageWritten);
}

/// <summary>formal adapter package shadow comparison per-sample trace。</summary>
public sealed class FormalAdapterPackageShadowComparisonSampleResult
{
    public string SampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public int BaselinePackageItemCount { get; init; }

    public int ShadowPackageItemCount { get; init; }

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int AddedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int OrderChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public int BaselineTokenCount { get; init; }

    public int ShadowTokenCount { get; init; }

    public int TokenDelta { get; init; }

    public int TokenDeltaAbsolute { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public IReadOnlyList<string> BaselinePackageItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowPackageItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DroppedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SectionChangedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> OrderChangedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> PriorityChangedItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExplainNotes { get; init; } = Array.Empty<string>();
}

/// <summary>formal adapter package shadow comparison report；只生成报告，不写 formal package。</summary>
public sealed class FormalAdapterPackageShadowComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ComparisonPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = FormalAdapterPackageShadowComparisonRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ShadowOnly";

    public string RequiredNextPhase { get; init; } = "FormalAdapterPackageShadowFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int TotalBaselinePackageItemCount { get; init; }

    public int TotalShadowPackageItemCount { get; init; }

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int AddedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int OrderChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public int BaselineTokenTotal { get; init; }

    public int ShadowTokenTotal { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int TokenDeltaAbsoluteTotal { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int TargetSectionViolationCount { get; init; }

    public IReadOnlyDictionary<string, int> BaselineSectionHistogram { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> ShadowSectionHistogram { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public int TokenDeltaBudgetTotal { get; init; }

    public int TokenDeltaBudgetPerSample { get; init; }

    public IReadOnlyList<FormalAdapterPackageShadowComparisonSampleResult> Samples { get; init; }
        = Array.Empty<FormalAdapterPackageShadowComparisonSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>graph + vector retrieval quality audit recommendations。</summary>
public static class GraphVectorRetrievalQualityAuditRecommendations
{
    public const string ReadyForRetrievalQualityFreeze = nameof(ReadyForRetrievalQualityFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingPackageShadowGate = nameof(BlockedByMissingPackageShadowGate);
    public const string BlockedByPackageShadowGateNotPassed = nameof(BlockedByPackageShadowGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyAuditOutput = nameof(BlockedByEmptyAuditOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByGraphNoiseExceedsThreshold = nameof(BlockedByGraphNoiseExceedsThreshold);
    public const string BlockedByRankingRegressionExceedsThreshold = nameof(BlockedByRankingRegressionExceedsThreshold);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}

/// <summary>retrieval quality audit failure cluster IDs。</summary>
public static class GraphVectorRetrievalQualityAuditFailureClusters
{
    public const string MissingCandidate = nameof(MissingCandidate);
    public const string RankingTooLow = nameof(RankingTooLow);
    public const string GraphNoise = nameof(GraphNoise);
    public const string VectorNoise = nameof(VectorNoise);
    public const string SectionMismatch = nameof(SectionMismatch);
    public const string LifecycleMismatch = nameof(LifecycleMismatch);
    public const string MetadataEvidenceGap = nameof(MetadataEvidenceGap);
}

/// <summary>retrieval quality audit failure cluster details。</summary>
public sealed class GraphVectorRetrievalQualityAuditFailureCluster
{
    public string ClusterId { get; init; } = string.Empty;

    public int Count { get; init; }

    public IReadOnlyList<string> SampleIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ItemIds { get; init; } = Array.Empty<string>();

    public string Description { get; init; } = string.Empty;
}

/// <summary>retrieval quality audit per-sample trace。</summary>
public sealed class GraphVectorRetrievalQualityAuditSampleResult
{
    public string SampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public int MustHitCount { get; init; }

    public int MustHitRecalledCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int VectorCandidateCount { get; init; }

    public int GraphCandidateCount { get; init; }

    public int MergedCandidateCount { get; init; }

    public int OverlapCount { get; init; }

    public int VectorOnlyCount { get; init; }

    public int GraphOnlyCount { get; init; }

    public int GraphNoiseCount { get; init; }

    public int VectorNoiseCount { get; init; }

    public int RankingRegressionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public int MetadataEvidenceGapCount { get; init; }

    public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingMustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RankingRegressionItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> GraphNoiseItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> VectorNoiseItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SectionMismatchItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MetadataEvidenceGapItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MergedCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExplainNotes { get; init; } = Array.Empty<string>();
}

/// <summary>graph + vector retrieval quality audit report。</summary>
public sealed class GraphVectorRetrievalQualityAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool AuditPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = GraphVectorRetrievalQualityAuditRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "AuditOnly";

    public string RequiredNextPhase { get; init; } = "RetrievalQualityFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int MustHitTotal { get; init; }

    public int MustHitRecalledTotal { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int VectorContributionCount { get; init; }

    public int GraphContributionCount { get; init; }

    public int OverlapCount { get; init; }

    public int VectorOnlyCount { get; init; }

    public int GraphOnlyCount { get; init; }

    public int GraphNoiseCount { get; init; }

    public int VectorNoiseCount { get; init; }

    public int RankingRegressionCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public int MetadataEvidenceGapCount { get; init; }

    public int TopK { get; init; }

    public int GraphNoiseThreshold { get; init; }

    public int RankingRegressionThreshold { get; init; }

    public int MustHitBelowTopKThreshold { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<GraphVectorRetrievalQualityAuditFailureCluster> FailureClusters { get; init; }
        = Array.Empty<GraphVectorRetrievalQualityAuditFailureCluster>();

    public IReadOnlyList<GraphVectorRetrievalQualityAuditSampleResult> Samples { get; init; }
        = Array.Empty<GraphVectorRetrievalQualityAuditSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>retrieval quality repair preview profile IDs。</summary>
public static class RetrievalQualityRepairProfiles
{
    public const string Baseline = "baseline";
    public const string CandidatePoolExpansion = "candidate-pool-expansion";
    public const string TopKAdjustment = "topk-adjustment";
    public const string SectionAwareBoost = "section-aware-boost";
    public const string MustHitEvidenceBoost = "must-hit-evidence-boost";
    public const string GraphRelationAnchorBoost = "graph-relation-anchor-boost";
    public const string LexicalFallbackBoost = "lexical-fallback-boost";
    public const string Combined = "combined-repair";
}

/// <summary>retrieval quality repair preview recommendations。</summary>
public static class RetrievalQualityRepairPreviewRecommendations
{
    public const string ReadyForRetrievalQualityRepairFreeze = nameof(ReadyForRetrievalQualityRepairFreeze);
    public const string KeepBaselineOnly = nameof(KeepBaselineOnly);
    public const string BlockedByMissingQualityGate = nameof(BlockedByMissingQualityGate);
    public const string BlockedByQualityGateNotPassed = nameof(BlockedByQualityGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyRepairOutput = nameof(BlockedByEmptyRepairOutput);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByGraphNoiseRegression = nameof(BlockedByGraphNoiseRegression);
    public const string BlockedByRankingRegression = nameof(BlockedByRankingRegression);
    public const string BlockedByRecallRegression = nameof(BlockedByRecallRegression);
    public const string BlockedByMrrRegression = nameof(BlockedByMrrRegression);
    public const string BlockedByTokenBudgetExceeded = nameof(BlockedByTokenBudgetExceeded);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
    public const string BlockedByNoRepairProfileImprovement = nameof(BlockedByNoRepairProfileImprovement);
}

/// <summary>retrieval quality repair profile result。</summary>
public sealed class RetrievalQualityRepairProfileResult
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileLabel { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int MustHitTotal { get; init; }

    public int MustHitRecalledTotal { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int VectorContributionCount { get; init; }

    public int GraphContributionCount { get; init; }

    public int OverlapCount { get; init; }

    public int VectorOnlyCount { get; init; }

    public int GraphOnlyCount { get; init; }

    public int GraphNoiseCount { get; init; }

    public int VectorNoiseCount { get; init; }

    public int RankingRegressionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public int BaselineTokenTotal { get; init; }

    public int RepairTokenTotal { get; init; }

    public int TokenDelta { get; init; }

    public int TokenDeltaAbsolute { get; init; }

    public double RecallDelta { get; init; }

    public double PrecisionDelta { get; init; }

    public double MrrDelta { get; init; }

    public int MustHitBelowTopKDelta { get; init; }

    public bool RiskRegressionDetected { get; init; }

    public bool RankingRegressionDetected { get; init; }

    public bool RecallRegressionDetected { get; init; }

    public bool MrrRegressionDetected { get; init; }

    public bool GraphNoiseRegressionDetected { get; init; }

    public bool TokenBudgetExceeded { get; init; }

    public IReadOnlyList<string> AppliedAdjustments { get; init; } = Array.Empty<string>();
}

/// <summary>retrieval quality repair preview report。</summary>
public sealed class RetrievalQualityRepairPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public string RequiredNextPhase { get; init; } = "RetrievalQualityRepairFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int MaxTokenDeltaTotal { get; init; }

    public int MaxTokenDeltaPerSample { get; init; }

    public RetrievalQualityRepairProfileResult Baseline { get; init; }
        = new RetrievalQualityRepairProfileResult();

    public IReadOnlyList<RetrievalQualityRepairProfileResult> Profiles { get; init; }
        = Array.Empty<RetrievalQualityRepairProfileResult>();

    public string BestProfileId { get; init; } = string.Empty;

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>runtime-observable feature classifications。</summary>
public static class RuntimeObservableFeatureClassifications
{
    public const string RuntimeObservable = nameof(RuntimeObservable);
    public const string DerivedAtRuntime = nameof(DerivedAtRuntime);
    public const string EvalOnly = nameof(EvalOnly);
    public const string ForbiddenForScoring = nameof(ForbiddenForScoring);
}

/// <summary>runtime-observable feature usage kinds。</summary>
public static class RuntimeObservableFeatureUsageKinds
{
    public const string Scoring = nameof(Scoring);
    public const string Filtering = nameof(Filtering);
    public const string CandidateExpansion = nameof(CandidateExpansion);
    public const string Knob = nameof(Knob);
}

/// <summary>profile contract statuses。</summary>
public static class RuntimeObservableFeatureContractStatuses
{
    public const string RuntimeSafe = nameof(RuntimeSafe);
    public const string RequiresRuntimeDerivation = nameof(RequiresRuntimeDerivation);
    public const string EvalOnly = nameof(EvalOnly);
    public const string ForbiddenForScoring = nameof(ForbiddenForScoring);
    public const string None = nameof(None);
}

/// <summary>runtime-observable feature contract recommendations。</summary>
public static class RuntimeObservableFeatureContractRecommendations
{
    public const string ReadyForRuntimeObservableFeatureFreeze = nameof(ReadyForRuntimeObservableFeatureFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingRepairGate = nameof(BlockedByMissingRepairGate);
    public const string BlockedByRepairGateNotPassed = nameof(BlockedByRepairGateNotPassed);
    public const string BlockedByEvalOnlyFeatureInScoring = nameof(BlockedByEvalOnlyFeatureInScoring);
    public const string BlockedByForbiddenFeatureInScoring = nameof(BlockedByForbiddenFeatureInScoring);
    public const string BlockedByMissingRuntimeDerivationPath = nameof(BlockedByMissingRuntimeDerivationPath);
    public const string BlockedByBestProfileContractFailure = nameof(BlockedByBestProfileContractFailure);
    public const string BlockedByFixtureSpecialCasing = nameof(BlockedByFixtureSpecialCasing);
    public const string BlockedBySourceScanMissing = nameof(BlockedBySourceScanMissing);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}

/// <summary>single feature usage record。</summary>
public sealed class RuntimeObservableFeatureUsage
{
    public string FeatureId { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string UsageKind { get; init; } = string.Empty;

    public string DerivationPath { get; init; } = string.Empty;

    public string CurrentSource { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> ProfileIds { get; init; } = Array.Empty<string>();
}

/// <summary>per-profile contract record。</summary>
public sealed class RuntimeObservableFeatureContractProfile
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileLabel { get; init; } = string.Empty;

    public string ContractStatus { get; init; } = RuntimeObservableFeatureContractStatuses.None;

    public IReadOnlyList<RuntimeObservableFeatureUsage> Features { get; init; }
        = Array.Empty<RuntimeObservableFeatureUsage>();

    public bool UsesForbiddenForScoring { get; init; }

    public bool UsesEvalOnlyForScoring { get; init; }

    public bool RequiresRuntimeDerivation { get; init; }

    public IReadOnlyList<string> RequiredRuntimeDerivationPaths { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>runner-side source scan summary; populated by EvalCommand at run time.</summary>
public sealed class RuntimeObservableFeatureContractSourceScan
{
    public bool ScanPerformed { get; init; }

    public int ScannedFileCount { get; init; }

    public int FixtureTokenHitCount { get; init; }

    public IReadOnlyList<string> ScannedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FlaggedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FlaggedTokens { get; init; } = Array.Empty<string>();
}

/// <summary>runtime-observable retrieval feature contract report。</summary>
public sealed class RuntimeObservableFeatureContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ContractPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeObservableFeatureContractRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "AuditOnly";

    public string RequiredNextPhase { get; init; } = "RuntimeObservableFeatureFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public string BestProfileId { get; init; } = string.Empty;

    public string BestProfileContractStatus { get; init; } = RuntimeObservableFeatureContractStatuses.None;

    public IReadOnlyList<RuntimeObservableFeatureContractProfile> Profiles { get; init; }
        = Array.Empty<RuntimeObservableFeatureContractProfile>();

    public IReadOnlyList<RuntimeObservableFeatureUsage> Catalog { get; init; }
        = Array.Empty<RuntimeObservableFeatureUsage>();

    public int ScoringFeatureCount { get; init; }

    public int FilteringFeatureCount { get; init; }

    public int CandidateExpansionFeatureCount { get; init; }

    public int RuntimeObservableCount { get; init; }

    public int DerivedAtRuntimeCount { get; init; }

    public int EvalOnlyCount { get; init; }

    public int ForbiddenForScoringCount { get; init; }

    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; }
        = new RuntimeObservableFeatureContractSourceScan();

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>runtime retrieval feature derivation recommendations。</summary>
public static class RuntimeRetrievalFeatureDerivationRecommendations
{
    public const string ReadyForRuntimeFeatureFreeze = nameof(ReadyForRuntimeFeatureFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingContractGate = nameof(BlockedByMissingContractGate);
    public const string BlockedByContractGateNotPassed = nameof(BlockedByContractGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyDerivedEnvelope = nameof(BlockedByEmptyDerivedEnvelope);
    public const string BlockedByForbiddenInputUsed = nameof(BlockedByForbiddenInputUsed);
    public const string BlockedByDerivedRecallRegression = nameof(BlockedByDerivedRecallRegression);
    public const string BlockedByDerivedMrrRegression = nameof(BlockedByDerivedMrrRegression);
    public const string BlockedByDerivedRiskNonZero = nameof(BlockedByDerivedRiskNonZero);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByFixtureSpecialCasing = nameof(BlockedByFixtureSpecialCasing);
    public const string BlockedBySourceScanMissing = nameof(BlockedBySourceScanMissing);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}

/// <summary>runtime-derived retrieval feature envelope。</summary>
public sealed class RuntimeRetrievalFeatureEnvelope
{
    public string SampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceAnchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredRelations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotConstraints { get; init; } = Array.Empty<string>();

    public string TargetSectionDerivationSource { get; init; } = string.Empty;

    public string EvidenceAnchorDerivationSource { get; init; } = string.Empty;

    public string SourceAnchorDerivationSource { get; init; } = string.Empty;

    public string RequiredRelationDerivationSource { get; init; } = string.Empty;

    public string MustNotConstraintDerivationSource { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>per-sample derivation result。</summary>
public sealed class RuntimeRetrievalFeatureDerivationSampleResult
{
    public RuntimeRetrievalFeatureEnvelope Envelope { get; init; }
        = new RuntimeRetrievalFeatureEnvelope();

    public bool TargetSectionMatch { get; init; }

    public int ExpectedRequiredRelationCount { get; init; }

    public int DerivedRequiredRelationCount { get; init; }

    public int RequiredRelationOverlap { get; init; }

    public int ExpectedEvidenceAnchorCount { get; init; }

    public int DerivedEvidenceAnchorCount { get; init; }

    public int EvidenceAnchorOverlap { get; init; }

    public int ExpectedSourceAnchorCount { get; init; }

    public int DerivedSourceAnchorCount { get; init; }

    public int SourceAnchorOverlap { get; init; }

    public int ExpectedMustNotCount { get; init; }

    public int DerivedMustNotCount { get; init; }

    public int MissingDerivationCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitRecalledCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }
}

/// <summary>runtime retrieval feature derivation preview report。</summary>
public sealed class RuntimeRetrievalFeatureDerivationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeRetrievalFeatureDerivationRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public string RequiredNextPhase { get; init; } = "RuntimeFeatureDerivationFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int SeedTopK { get; init; }

    public int SampleCount { get; init; }

    public double TargetSectionMatchRate { get; init; }

    public double RequiredRelationCoverageRate { get; init; }

    public double EvidenceAnchorCoverageRate { get; init; }

    public double SourceAnchorCoverageRate { get; init; }

    public double DerivationCompletenessRate { get; init; }

    public double BaselineRecall { get; init; }

    public double BaselinePrecision { get; init; }

    public double BaselineMeanReciprocalRank { get; init; }

    public int BaselineMustHitBelowTopKCount { get; init; }

    public int BaselineRiskAfterPolicy { get; init; }

    public int BaselineMustNotHitRiskAfterPolicy { get; init; }

    public int BaselineLifecycleRiskAfterPolicy { get; init; }

    public int BaselineSectionMismatchCount { get; init; }

    public double DerivedRecall { get; init; }

    public double DerivedPrecision { get; init; }

    public double DerivedMeanReciprocalRank { get; init; }

    public int DerivedMustHitBelowTopKCount { get; init; }

    public int DerivedRiskAfterPolicy { get; init; }

    public int DerivedMustNotHitRiskAfterPolicy { get; init; }

    public int DerivedLifecycleRiskAfterPolicy { get; init; }

    public int DerivedSectionMismatchCount { get; init; }

    public double EvalDrivenRecall { get; init; }

    public double EvalDrivenPrecision { get; init; }

    public double EvalDrivenMeanReciprocalRank { get; init; }

    public double DerivedRecallDelta { get; init; }

    public double DerivedMrrDelta { get; init; }

    public int ForbiddenSampleAnnotationReadCount { get; init; }

    public double MaxAllowedRecallRegression { get; init; }

    public double MaxAllowedMrrRegression { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; }
        = new RuntimeObservableFeatureContractSourceScan();

    public IReadOnlyList<RuntimeRetrievalFeatureDerivationSampleResult> Samples { get; init; }
        = Array.Empty<RuntimeRetrievalFeatureDerivationSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>runtime feature derivation repair recommendations。</summary>
public static class RuntimeRetrievalFeatureDerivationRepairRecommendations
{
    public const string ReadyForRuntimeFeatureDerivationRepairFreeze = nameof(ReadyForRuntimeFeatureDerivationRepairFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDerivationGate = nameof(BlockedByMissingDerivationGate);
    public const string BlockedByDerivationGateNotPassed = nameof(BlockedByDerivationGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByEmptyRepairEnvelope = nameof(BlockedByEmptyRepairEnvelope);
    public const string BlockedByForbiddenSampleAnnotationRead = nameof(BlockedByForbiddenSampleAnnotationRead);
    public const string BlockedByDerivedRecallNotImproved = nameof(BlockedByDerivedRecallNotImproved);
    public const string BlockedByDerivedMrrNotImproved = nameof(BlockedByDerivedMrrNotImproved);
    public const string BlockedByHoldoutRecallRegression = nameof(BlockedByHoldoutRecallRegression);
    public const string BlockedByHoldoutMrrRegression = nameof(BlockedByHoldoutMrrRegression);
    public const string BlockedByLowRelationCoverage = nameof(BlockedByLowRelationCoverage);
    public const string BlockedByZeroAnchorCoverage = nameof(BlockedByZeroAnchorCoverage);
    public const string BlockedByDerivedRiskNonZero = nameof(BlockedByDerivedRiskNonZero);
    public const string BlockedByMustNotHitRisk = nameof(BlockedByMustNotHitRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByFixtureSpecialCasing = nameof(BlockedByFixtureSpecialCasing);
    public const string BlockedBySourceScanMissing = nameof(BlockedBySourceScanMissing);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingChange = nameof(BlockedByVectorStoreBindingChange);
}

/// <summary>per-sample repair derivation result。</summary>
public sealed class RuntimeRetrievalFeatureDerivationRepairSampleResult
{
    public RuntimeRetrievalFeatureEnvelope Envelope { get; init; }
        = new RuntimeRetrievalFeatureEnvelope();

    public string Split { get; init; } = "train";

    public bool TargetSectionMatch { get; init; }

    public int ExpectedRequiredRelationCount { get; init; }

    public int DerivedRequiredRelationCount { get; init; }

    public int RequiredRelationOverlap { get; init; }

    public int CanonicalRequiredRelationOverlap { get; init; }

    public int ExpectedEvidenceAnchorCount { get; init; }

    public int DerivedEvidenceAnchorCount { get; init; }

    public int CanonicalEvidenceAnchorOverlap { get; init; }

    public int ExpectedSourceAnchorCount { get; init; }

    public int DerivedSourceAnchorCount { get; init; }

    public int CanonicalSourceAnchorOverlap { get; init; }

    public int ExpectedMustNotCount { get; init; }

    public int DerivedMustNotCount { get; init; }

    public int MissingDerivationCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustHitRecalledCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }
}

/// <summary>runtime feature derivation repair preview report。</summary>
public sealed class RuntimeRetrievalFeatureDerivationRepairReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeRetrievalFeatureDerivationRepairRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public string RequiredNextPhase { get; init; } = "RuntimeFeatureDerivationRepairFreeze";

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public int TopK { get; init; }

    public int DenseSeedTopK { get; init; }

    public int AnchorSeedTopK { get; init; }

    public int RelationTopK { get; init; }

    public int SampleCount { get; init; }

    public int TrainSampleCount { get; init; }

    public int HoldoutSampleCount { get; init; }

    public double TrainBaselineRecall { get; init; }

    public double TrainBaselineMrr { get; init; }

    public double TrainDerivedRecall { get; init; }

    public double TrainDerivedMrr { get; init; }

    public double HoldoutBaselineRecall { get; init; }

    public double HoldoutBaselineMrr { get; init; }

    public double HoldoutDerivedRecall { get; init; }

    public double HoldoutDerivedMrr { get; init; }

    public double TargetSectionMatchRate { get; init; }

    public double CanonicalRequiredRelationCoverageRate { get; init; }

    public double CanonicalEvidenceAnchorCoverageRate { get; init; }

    public double CanonicalSourceAnchorCoverageRate { get; init; }

    public int ApplicableEvidenceSampleCount { get; init; }

    public int ApplicableSourceSampleCount { get; init; }

    public int ApplicableRelationSampleCount { get; init; }

    public int ApplicableEvidenceCoveredCount { get; init; }

    public int ApplicableSourceCoveredCount { get; init; }

    public int ApplicableRelationCoveredCount { get; init; }

    public int DerivedRiskAfterPolicy { get; init; }

    public int DerivedMustNotHitRiskAfterPolicy { get; init; }

    public int DerivedLifecycleRiskAfterPolicy { get; init; }

    public int DerivedSectionMismatchCount { get; init; }

    public int ForbiddenSampleAnnotationReadCount { get; init; }

    public double MinRelationCoverageRate { get; init; }

    public double MaxAllowedHoldoutRecallRegression { get; init; }

    public double MaxAllowedHoldoutMrrRegression { get; init; }

    public IReadOnlyList<string> DerivationDiagnostics { get; init; } = Array.Empty<string>();

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; }
        = new RuntimeObservableFeatureContractSourceScan();

    public IReadOnlyList<RuntimeRetrievalFeatureDerivationRepairSampleResult> Samples { get; init; }
        = Array.Empty<RuntimeRetrievalFeatureDerivationRepairSampleResult>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>运行时特征推导失败冻结推荐。</summary>
public static class RuntimeFeatureDerivationFailureFreezeRecommendations
{
    public const string ReadyForGraphHubNoiseControlPreview = nameof(ReadyForGraphHubNoiseControlPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingRepairGate = nameof(BlockedByMissingRepairGate);
    public const string BlockedByRepairGateNotFrozen = nameof(BlockedByRepairGateNotFrozen);
}

/// <summary>运行时特征推导失败冻结报告。</summary>
public sealed class RuntimeFeatureDerivationFailureFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeFeatureDerivationFailureFreezeRecommendations.KeepPreviewOnly;

    public string FrozenStatus { get; init; } = "BlockedByHubRelationNoise";

    public string RepairGateSourcePath { get; init; } = string.Empty;

    public string DerivationGateSourcePath { get; init; } = string.Empty;

    public bool CanonicalAnchorResolverReusable { get; init; } = true;

    public bool RuntimeRelationIntentDeriverReady { get; init; } = false;

    public bool CombinedRepairEvalUpperBoundOnly { get; init; } = true;

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public double V57DerivedRecall { get; init; }

    public double V57DerivedMrr { get; init; }

    public double V58TrainBaselineRecall { get; init; }

    public double V58TrainDerivedRecall { get; init; }

    public double V58TrainBaselineMrr { get; init; }

    public double V58TrainDerivedMrr { get; init; }

    public double V58HoldoutBaselineRecall { get; init; }

    public double V58HoldoutDerivedRecall { get; init; }

    public double V58HoldoutBaselineMrr { get; init; }

    public double V58HoldoutDerivedMrr { get; init; }

    public double CanonicalRelationCoverageRate { get; init; }

    public double CanonicalEvidenceCoverageRate { get; init; }

    public double CanonicalSourceCoverageRate { get; init; }

    public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DisabledCapabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RecommendedNextPhases { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FrozenArtifactPaths { get; init; } = Array.Empty<string>();
}

/// <summary>图枢纽噪声控制推荐。</summary>
public static class GraphHubNoiseControlRecommendations
{
    public const string ReadyForHubNoiseControlFreeze = nameof(ReadyForHubNoiseControlFreeze);
    public const string KeepBaselineOnly = nameof(KeepBaselineOnly);
    public const string BlockedByMissingFreezeGate = nameof(BlockedByMissingFreezeGate);
    public const string BlockedByFreezeGateNotPassed = nameof(BlockedByFreezeGateNotPassed);
    public const string BlockedByMissingDataset = nameof(BlockedByMissingDataset);
    public const string BlockedByRecallRegression = nameof(BlockedByRecallRegression);
    public const string BlockedByMrrRegression = nameof(BlockedByMrrRegression);
    public const string BlockedByHoldoutRecallRegression = nameof(BlockedByHoldoutRecallRegression);
    public const string BlockedByRiskNonZero = nameof(BlockedByRiskNonZero);
}

/// <summary>单样本图枢纽指标。</summary>
public sealed class GraphHubNoiseControlSampleMetrics
{
    public string SampleId { get; init; } = string.Empty;

    public int SeedItemCount { get; init; }

    public int HubItemCount { get; init; }

    public int TotalRelationDegree { get; init; }

    public int MaxRelationDegree { get; init; }

    public int EnvelopeRelationCount { get; init; }

    public double EnvelopeWidthRatio { get; init; }

    public double HubDominanceRatio { get; init; }

    public int HubCandidates { get; init; }
}

/// <summary>单 profile 评分结果。</summary>
public sealed class GraphHubNoiseControlProfileResult
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileLabel { get; init; } = string.Empty;

    public double Recall { get; init; }

    public double Precision { get; init; }

    public double MeanReciprocalRank { get; init; }

    public int MustHitBelowTopKCount { get; init; }
}

/// <summary>图枢纽噪声控制预览报告。</summary>
public sealed class GraphHubNoiseControlReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = GraphHubNoiseControlRecommendations.KeepBaselineOnly;

    public string AllowedMode { get; init; } = "PreviewOnly";

    public int TopK { get; init; }

    public int SampleCount { get; init; }

    public int HubItemCount { get; init; }

    public double AvgEnvelopeWidthRatio { get; init; }

    public double AvgHubDominanceRatio { get; init; }

    public GraphHubNoiseControlProfileResult Baseline { get; init; }
        = new GraphHubNoiseControlProfileResult();

    public GraphHubNoiseControlProfileResult PreviousDerived { get; init; }
        = new GraphHubNoiseControlProfileResult();

    public GraphHubNoiseControlProfileResult HubControlled { get; init; }
        = new GraphHubNoiseControlProfileResult();

    public double HubControlledRecallDelta { get; init; }

    public double HubControlledMrrDelta { get; init; }
}

/// <summary>受控应用合并 dry-run 观察推荐。</summary>
public static class ControlledAppliedMergeDryRunDecisionRecommendations
{
    public const string ReadyForControlledAppliedMergeApproval = nameof(ReadyForControlledAppliedMergeApproval);
    public const string KeepDryRunOnly = nameof(KeepDryRunOnly);
    public const string BlockedByMissingProposalGate = nameof(BlockedByMissingProposalGate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByOutputMutation = nameof(BlockedByOutputMutation);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
}

/// <summary>受控应用合并 dry-run 观察报告。</summary>
public sealed class ControlledAppliedMergeDryRunObservationReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeDryRunDecisionRecommendations.KeepDryRunOnly;

    public string ProposalSourcePath { get; init; } = "";

    public int ObservationRuns { get; init; }

    public int WouldApplyAddCount { get; init; }

    public int WouldApplyRemoveCount { get; init; }

    public int AppliedAddCount { get; init; }

    public int AppliedRemoveCount { get; init; }

    public int MaxAddPerSample { get; init; }

    public int MaxRemovePerSample { get; init; }

    public int TotalTokenDelta { get; init; }

    public int MaxTokenDeltaPerSample { get; init; }

    public int SectionChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public bool RollbackPassed { get; init; }

    public bool KillSwitchTested { get; init; }

    public bool StopConditionsChecked { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>受控应用合并批准推荐。</summary>
public static class ControlledAppliedMergeApprovalRecommendations
{
    public const string ReadyForScopedPreview = nameof(ReadyForScopedPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDryRunDecision = nameof(BlockedByMissingDryRunDecision);
    public const string BlockedByRiskAcknowledgementRequired = nameof(BlockedByRiskAcknowledgementRequired);
    public const string BlockedByRollbackAcknowledgementRequired = nameof(BlockedByRollbackAcknowledgementRequired);
}

/// <summary>受控应用合并批准记录。</summary>
public sealed class ControlledAppliedMergeApprovalReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ApprovalPassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeApprovalRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = "";

    public string ApprovedBy { get; init; } = "";

    public string Reason { get; init; } = "";

    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddDays(7);

    public string ApprovalMode { get; init; } = "ControlledAppliedMergePreviewOnly";

    public string DryRunDecisionSourcePath { get; init; } = "";

    public int WouldApplyAddCount { get; init; }

    public int WouldApplyRemoveCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public bool RollbackPresent { get; init; }

    public bool KillSwitchPresent { get; init; }

    public bool IsRevoked { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>受控应用合并范围预览推荐。</summary>
public static class ControlledAppliedMergeScopedPreviewRecommendations
{
    public const string ReadyForControlledAppliedMergeScopedPreviewGate = nameof(ReadyForControlledAppliedMergeScopedPreviewGate);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByApprovalExpiredOrRevoked = nameof(BlockedByApprovalExpiredOrRevoked);
    public const string BlockedByPreviewSelectedSetUnchanged = nameof(BlockedByPreviewSelectedSetUnchanged);
    public const string BlockedByFormalSelectedSetChanged = nameof(BlockedByFormalSelectedSetChanged);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
}

/// <summary>受控应用合并范围预览报告。</summary>
public sealed class ControlledAppliedMergeScopedPreviewReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeScopedPreviewRecommendations.BlockedByMissingApproval;

    public string ApprovalSourcePath { get; init; } = "";

    public string DryRunDecisionSourcePath { get; init; } = "";

    public bool PreviewSelectedSetChanged { get; init; }

    public int PreviewAddCount { get; init; }

    public int PreviewRemoveCount { get; init; }

    public int AppliedFormalAddCount { get; init; }

    public int AppliedFormalRemoveCount { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public bool RollbackPresent { get; init; }

    public bool KillSwitchPresent { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>受控应用合并预览冻结推荐。</summary>
public static class ControlledAppliedMergePreviewFreezeRecommendations
{
    public const string ReadyForV6MainlineFreeze = nameof(ReadyForV6MainlineFreeze);
    public const string BlockedByMissingScopedPreviewGate = nameof(BlockedByMissingScopedPreviewGate);
}

/// <summary>受控应用合并预览冻结报告。</summary>
public sealed class ControlledAppliedMergePreviewFreezeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergePreviewFreezeRecommendations.ReadyForV6MainlineFreeze;
    public string ScopedPreviewSourcePath { get; init; } = "";
    public int ProposalAddCount { get; init; }
    public int ProposalRemoveCount { get; init; }
    public int DryRunWouldApplyAdd { get; init; }
    public int DryRunWouldApplyRemove { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public bool ApprovalPresent { get; init; }
    public string ApprovedBy { get; init; } = "";
    public int V6PhaseCount { get; init; }
    public IReadOnlyList<string> FrozenArtifacts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>架构清理计划推荐。</summary>
public static class ArchitectureCleanupPlanRecommendations
{
    public const string ReadyForCleanupPlan = nameof(ReadyForCleanupPlan);
    public const string BlockedByMissingV6FFreeze = nameof(BlockedByMissingV6FFreeze);
}

/// <summary>架构清理计划报告。</summary>
public sealed class ArchitectureCleanupPlanReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public string Recommendation { get; init; }
        = ArchitectureCleanupPlanRecommendations.BlockedByMissingV6FFreeze;
    public int CoreRunnerCount { get; init; }
    public int DtoClassCount { get; init; }
    public int EvalCommandLines { get; init; }
    public int ControlRoomServiceLines { get; init; }
    public int RendererLines { get; init; }
    public int SubcommandCount { get; init; }
    public IReadOnlyList<ArchitectureCleanupItem> RecommendedMigrations { get; init; }
        = Array.Empty<ArchitectureCleanupItem>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>单个清理项。</summary>
public sealed class ArchitectureCleanupItem
{
    public string Priority { get; init; } = "nice-to-have";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string CurrentState { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string Risk { get; init; } = "low";
}

/// <summary>Scoped runtime experiment no-op harness mode。</summary>
public static class ScopedRuntimeExperimentNoOpHarnessModes
{
    public const string NoOp = nameof(NoOp);
}

/// <summary>No-op harness 配置；只验证审批和边界，不切 runtime。</summary>
public sealed class ScopedRuntimeExperimentNoOpHarnessOptions
{
    public bool Enabled { get; init; }

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentNoOpHarnessModes.NoOp;

    public bool RequireApprovedProposal { get; init; } = true;

    public string RequireApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool MutateRuntime { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }
}

/// <summary>Scoped runtime experiment no-op harness report；不改变正式 retrieval/package。</summary>
public sealed class ScopedRuntimeExperimentNoOpHarnessReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool HarnessPassed { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentNoOpHarnessModes.NoOp;

    public bool SelectedScopeChecked { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int NoOpTraceCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int PreviewPackageCount { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool DiBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool P15GatePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Scoped runtime experiment dry-run harness freeze recommendation。</summary>
public static class ScopedRuntimeExperimentHarnessFreezeRecommendations
{
    public const string ReadyForGuardedRuntimeExperimentPlanning = nameof(ReadyForGuardedRuntimeExperimentPlanning);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByExpiredApproval = nameof(BlockedByExpiredApproval);
    public const string BlockedByRevokedApproval = nameof(BlockedByRevokedApproval);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByHarnessFailure = nameof(BlockedByHarnessFailure);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>Scoped runtime experiment harness freeze report；冻结 no-op harness 设计边界，不授权 runtime switch。</summary>
public sealed class ScopedRuntimeExperimentHarnessFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentHarnessFreezeRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string HarnessStatus { get; init; } = string.Empty;

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public string AllowedMode { get; init; } = "NoOpHarnessOnly / ExplicitScopedExperimentPlanningOnly";

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string NextAllowedPhase { get; init; } = "GuardedScopedRuntimeExperimentPlan";

    public bool ProposalGatePassed { get; init; }

    public bool ApprovalSummaryPassed { get; init; }

    public bool NoOpHarnessGatePassed { get; init; }

    public bool DesignFreezeGatePassed { get; init; }

    public bool ServiceFoundationFreezeGatePassed { get; init; }

    public bool FoundationReleaseCandidateGatePassed { get; init; }

    public bool RuntimeChangeReadinessGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Guarded scoped runtime experiment plan mode；V4.11 只允许计划模式。</summary>
public static class GuardedScopedRuntimeExperimentPlanModes
{
    public const string PlanOnly = nameof(PlanOnly);
}

/// <summary>Guarded scoped runtime experiment plan recommendation。</summary>
public static class GuardedScopedRuntimeExperimentPlanRecommendations
{
    public const string ReadyForScopedRuntimeExperimentActivationContract = nameof(ReadyForScopedRuntimeExperimentActivationContract);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

/// <summary>V4.11 guarded scoped runtime experiment plan options；只生成 activation contract。</summary>
public sealed class GuardedScopedRuntimeExperimentPlanOptions
{
    public bool Enabled { get; init; }

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentPlanModes.PlanOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MaxRequestCount { get; init; }

    public int MaxDurationMinutes { get; init; }

    public int MaxErrorCount { get; init; }

    public int MaxRiskCount { get; init; }

    public bool RequireKillSwitch { get; init; } = true;

    public bool RequireRollbackPlan { get; init; } = true;

    public bool RequireObservationPlan { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }
}

/// <summary>V4.11 guarded scoped runtime experiment plan；不切 runtime、不写正式 package。</summary>
public sealed class GuardedScopedRuntimeExperimentPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = GuardedScopedRuntimeExperimentPlanRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int MaxRequestCount { get; init; }

    public int MaxDurationMinutes { get; init; }

    public string KillSwitchPlan { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public IReadOnlyList<string> ObservationPlan { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>vector lifecycle metadata review 决策类型。</summary>
public static class VectorLifecycleMetadataReviewDecisions
{
    public const string ApproveForSidecar = nameof(ApproveForSidecar);
    public const string Reject = nameof(Reject);
    public const string NeedsEvidence = nameof(NeedsEvidence);
    public const string Supersede = nameof(Supersede);
}

/// <summary>提交 lifecycle metadata review 决策的请求。</summary>
public sealed class VectorLifecycleMetadataReviewRequest
{
    public string CandidateId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string ProposedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool Confirmed { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>lifecycle metadata review 历史记录。</summary>
public sealed class VectorLifecycleMetadataReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string ResultStatus { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string ProposedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool SidecarWritten { get; init; }

    public bool UnsafeApprovalBlocked { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>sidecar lifecycle metadata override；只写旁路文件，不修改业务 source item。</summary>
public sealed class VectorLifecycleSidecarMetadataEntry
{
    public string ItemId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string LifecycleOverride { get; init; } = string.Empty;

    public string ReviewStatusOverride { get; init; } = string.Empty;

    public string TargetSectionOverride { get; init; } = string.Empty;

    public string SourceReviewId { get; init; } = string.Empty;

    public string SourceCandidateId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string PolicyVersion { get; init; } = "vector-lifecycle-sidecar/v1";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>review 决策执行结果。</summary>
public sealed class VectorLifecycleMetadataReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Succeeded { get; init; }

    public string CandidateId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string CandidateStatus { get; init; } = string.Empty;

    public bool SidecarWritten { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public bool UnsafeApprovalBlocked { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public VectorLifecycleMetadataReviewRecord? Review { get; init; }

    public VectorLifecycleSidecarMetadataEntry? SidecarEntry { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>lifecycle metadata review summary 报告。</summary>
public sealed class VectorLifecycleMetadataReviewSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CandidateCount { get; init; }

    public int PendingCount { get; init; }

    public int ApprovedForSidecarCount { get; init; }

    public int RejectedCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int SupersededCount { get; init; }

    public int SidecarEntryCount { get; init; }

    public int NormalContextApprovalCount { get; init; }

    public int AuditContextApprovalCount { get; init; }

    public int HistoricalContextApprovalCount { get; init; }

    public int DiagnosticsOnlyApprovalCount { get; init; }

    public int UnsafeApprovalBlockedCount { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<VectorLifecycleMetadataReviewRecord> RecentReviews { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewRecord>();
}

/// <summary>lifecycle metadata sidecar preview 报告。</summary>
public sealed class VectorLifecycleMetadataSidecarPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int SidecarEntryCount { get; init; }

    public int NormalContextEntryCount { get; init; }

    public int AuditContextEntryCount { get; init; }

    public int HistoricalContextEntryCount { get; init; }

    public int DiagnosticsOnlyEntryCount { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<VectorLifecycleSidecarMetadataEntry> Entries { get; init; } =
        Array.Empty<VectorLifecycleSidecarMetadataEntry>();
}

/// <summary>sidecar-aware eligibility 预览模式；仅用于 eval/preview，不接正式 retrieval。</summary>
public static class VectorSidecarEligibilityModes
{
    public const string BaseEligibility = nameof(BaseEligibility);

    public const string SidecarAwareEligibility = nameof(SidecarAwareEligibility);
}

/// <summary>sidecar effective metadata 来源。</summary>
public static class VectorLifecycleSidecarResolutionSources
{
    public const string Base = nameof(Base);

    public const string Sidecar = nameof(Sidecar);

    public const string Conflict = nameof(Conflict);

    public const string NotFound = nameof(NotFound);
}

/// <summary>单个 item 的 sidecar-aware lifecycle metadata 解析结果。</summary>
public sealed class VectorLifecycleSidecarResolution
{
    public string ItemId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string BaseLifecycle { get; init; } = string.Empty;

    public string BaseReviewStatus { get; init; } = string.Empty;

    public string BaseTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string EffectiveLifecycle { get; init; } = string.Empty;

    public string EffectiveReviewStatus { get; init; } = string.Empty;

    public string EffectiveTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string Source { get; init; } = VectorLifecycleSidecarResolutionSources.Base;

    public string SidecarReviewId { get; init; } = string.Empty;

    public bool Valid { get; init; } = true;

    public bool Blocked { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public bool SourceItemUnchanged { get; init; } = true;

    public string Explanation { get; init; } = string.Empty;
}

/// <summary>sidecar-aware eligibility preview/recheck/quality 报告；不改变正式检索或 source item。</summary>
public sealed class VectorSidecarEligibilityPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Mode { get; init; } = VectorSidecarEligibilityModes.SidecarAwareEligibility;

    public int CandidateCount { get; init; }

    public int SidecarEntryCount { get; init; }

    public int ApprovedSidecarCount { get; init; }

    public int PendingReviewCount { get; init; }

    public int EffectiveMetadataChangedCount { get; init; }

    public int UnsafeSidecarBlockedCount { get; init; }

    public int ConflictSidecarBlockedCount { get; init; }

    public bool SourceItemUnchanged { get; init; } = true;

    public double RecallBeforeSidecar { get; init; }

    public double RecallAfterSidecar { get; init; }

    public int RiskBeforeSidecar { get; init; }

    public int RiskAfterSidecar { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<VectorLifecycleSidecarResolution> Resolutions { get; init; } =
        Array.Empty<VectorLifecycleSidecarResolution>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>lifecycle metadata 人工 review batch 状态。</summary>
public static class VectorLifecycleMetadataReviewBatchStatuses
{
    public const string Draft = nameof(Draft);

    public const string Exported = nameof(Exported);

    public const string Imported = nameof(Imported);

    public const string Validated = nameof(Validated);

    public const string AppliedPreview = nameof(AppliedPreview);

    public const string Closed = nameof(Closed);
}

/// <summary>lifecycle metadata 人工 review batch；仅组织人工审阅，不自动写 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatch
{
    public string BatchId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public IReadOnlyList<string> CandidateIds { get; init; } = Array.Empty<string>();

    public int CandidateCount { get; init; }

    public string Status { get; init; } = VectorLifecycleMetadataReviewBatchStatuses.Draft;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string CreatedBy { get; init; } = string.Empty;

    public string ReviewInstructions { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>人工 review sheet 单行；ReviewerDecision 为空时表示尚未审阅。</summary>
public sealed class VectorLifecycleMetadataReviewSheetRow
{
    public string CandidateId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string ProposedTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public string RepairReason { get; init; } = string.Empty;

    public string ReviewerDecision { get; init; } = string.Empty;

    public string ReviewerReason { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string TargetSectionOverride { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}

/// <summary>review batch import 结果。</summary>
public sealed class VectorLifecycleMetadataReviewBatchImportResult
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;

    public int RowCount { get; init; }

    public int DecisionCount { get; init; }

    public bool Imported { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>review batch validation 单条问题。</summary>
public sealed class VectorLifecycleMetadataReviewBatchValidationIssue
{
    public string CandidateId { get; init; } = string.Empty;

    public string Severity { get; init; } = "Error";

    public string Reason { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>review batch validation 报告。</summary>
public sealed class VectorLifecycleMetadataReviewBatchValidationReport
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CandidateCount { get; init; }

    public int RowCount { get; init; }

    public int DecisionCount { get; init; }

    public int ApprovalCount { get; init; }

    public int RejectCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int SupersedeCount { get; init; }

    public int ValidationErrorCount { get; init; }

    public int UnsafeDecisionCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingReviewerCount { get; init; }

    public int MissingReviewerReasonCount { get; init; }

    public bool LastWriteWins { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<VectorLifecycleMetadataReviewBatchValidationIssue> Issues { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewBatchValidationIssue>();

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>review batch apply preview；只估算 sidecar 写入，不写真实 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatchApplyPreviewReport
{
    public string BatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CandidateCount { get; init; }

    public int DecisionCount { get; init; }

    public int WouldWriteSidecarEntryCount { get; init; }

    public int UnsafeBlockedCount { get; init; }

    public int NormalContextApprovalCount { get; init; }

    public int AuditContextApprovalCount { get; init; }

    public int HistoricalContextApprovalCount { get; init; }

    public int DiagnosticsOnlyApprovalCount { get; init; }

    public int EffectiveMetadataChangedCount { get; init; }

    public bool RealSidecarWritten { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>review batch import smoke 报告；只验证导入、校验和 preview，不写真实 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatchImportSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool SmokePassed { get; init; }

    public string BatchId { get; init; } = string.Empty;

    public int ImportedRowCount { get; init; }

    public int ValidDecisionCount { get; init; }

    public int InvalidDecisionCount { get; init; }

    public int DuplicateDecisionBlockedCount { get; init; }

    public int UnknownDecisionBlockedCount { get; init; }

    public int MissingReviewerBlockedCount { get; init; }

    public int MissingReasonBlockedCount { get; init; }

    public int MissingEvidenceBlockedCount { get; init; }

    public int UnsafeNormalContextBlockedCount { get; init; }

    public int WouldWriteSidecarCount { get; init; }

    public int ActualSidecarWriteCount { get; init; }

    public bool SourceItemUnchanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string InitialStatus { get; init; } = string.Empty;

    public string ExportedStatus { get; init; } = string.Empty;

    public string ImportedStatus { get; init; } = string.Empty;

    public string ValidatedStatus { get; init; } = string.Empty;

    public string ValidationRecommendation { get; init; } = string.Empty;

    public string ApplyPreviewRecommendation { get; init; } = string.Empty;

    public string Recommendation { get; init; } = "KeepPreviewOnly";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>lifecycle metadata review smoke 报告。</summary>
public sealed class VectorLifecycleMetadataReviewSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ApprovedSidecarWritten { get; init; }

    public bool RejectSkippedSidecar { get; init; }

    public bool NeedsEvidenceSkippedSidecar { get; init; }

    public bool SupersedeSkippedSidecar { get; init; }

    public bool SourceItemUnchanged { get; init; }

    public bool UnsafeNormalContextApprovalBlocked { get; init; }

    public bool CleanupPerformed { get; init; }

    public int SidecarEntryCount { get; init; }

    public string Recommendation { get; init; } = "ReviewSmokePassed";

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
/// <summary>hybrid retrieval readiness gate 报告；FormalRetrievalAllowed 恒 false。</summary>
public sealed class HybridRetrievalReadinessGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Passed { get; init; }
    public double A3RecallAfterPolicy { get; init; }
    public double ExtendedRecallAfterPolicy { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool PolicyViolationFound { get; init; }
    public bool P15GatePassed { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public IReadOnlyList<string> Allowed { get; init; } = ["preview", "shadow", "eval"];
    public IReadOnlyList<string> Forbidden { get; init; } = ["FormalRetrievalSwitch", "FormalIVectorIndexStoreBinding", "PackingPolicyIntegration", "PackageOutputIntegration"];
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
}
/// <summary>embedding provider 本地 smoke test 报告。</summary>
public sealed class EmbeddingProviderSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int ExpectedDimension { get; init; }

    public int ActualDimension { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ProviderEnabled { get; init; }

    public bool ModelPathExists { get; init; }

    public bool TokenizerPathExists { get; init; }

    public bool TokenizationWorks { get; init; }

    public bool OnnxInferenceWorks { get; init; }

    public bool DimensionMatchesConfig { get; init; }

    public bool NormalizationWorks { get; init; }

    public bool BatchEmbeddingWorks { get; init; }

    public bool Succeeded { get; init; }

    public IReadOnlyList<VectorIndexDiagnostic> Diagnostics { get; init; } =
        [];

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>embedding generator 请求。</summary>
public sealed class EmbeddingGeneratorRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public IReadOnlyList<EmbeddingGeneratorInput> Inputs { get; init; } = Array.Empty<EmbeddingGeneratorInput>();
}

/// <summary>embedding generator 单条输入。</summary>
public sealed class EmbeddingGeneratorInput
{
    public string ItemId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>embedding generator 批量结果。</summary>
public sealed class EmbeddingGeneratorResult
{
    public string OperationId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public IReadOnlyList<VectorIndexEntry> Entries { get; init; } = Array.Empty<VectorIndexEntry>();
}

/// <summary>
/// formal adapter 未来唯一允许读取的 runtime 输入 envelope。
/// 不包含 Dataset V2 sample、gold labels、shadow report 或 eval-only metadata。
/// </summary>
public sealed class FormalAdapterRuntimeInputEnvelope
{
    public string ContractVersion { get; init; } = "formal-adapter-input-contract-v1";

    public string RequestId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public FormalAdapterRuntimePackageContext PackageContext { get; init; } = new();

    public IReadOnlyList<FormalAdapterRuntimeCandidateInput> BaselineCandidates { get; init; } =
        Array.Empty<FormalAdapterRuntimeCandidateInput>();

    public IReadOnlyList<FormalAdapterRuntimeCandidateInput> CandidatePool { get; init; } =
        Array.Empty<FormalAdapterRuntimeCandidateInput>();
}

/// <summary>formal adapter 可读取的当前 package 上下文快照。</summary>
public sealed class FormalAdapterRuntimePackageContext
{
    public string BaselinePackageId { get; init; } = string.Empty;

    public IReadOnlyList<string> BaselineCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, int> SectionTokenBudgets { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SectionOccupancy { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int TotalTokenBudget { get; init; }

    public int MaxCandidateCount { get; init; }
}

/// <summary>
/// formal adapter 可读取的候选输入。ItemId 仅作候选身份与稳定 tie-break，不允许作业务特判。
/// </summary>
public sealed class FormalAdapterRuntimeCandidateInput
{
    public string CandidateId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Anchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public FormalAdapterRuntimeProvenanceInput Provenance { get; init; } = new();

    public IReadOnlyList<FormalAdapterRuntimeRelationEvidenceInput> Relations { get; init; } =
        Array.Empty<FormalAdapterRuntimeRelationEvidenceInput>();

    public int EstimatedTokens { get; init; }

    public double Score { get; init; }

    public int DenseRank { get; init; }

    public int LexicalRank { get; init; }

    public int AnchorRank { get; init; }
}

/// <summary>formal adapter 可读取的 provenance 子集。</summary>
public sealed class FormalAdapterRuntimeProvenanceInput
{
    public string RecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string IngestionBatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>formal adapter 可读取的 relation evidence 子集。</summary>
public sealed class FormalAdapterRuntimeRelationEvidenceInput
{
    public string RelationId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public string SourceItemId { get; init; } = string.Empty;

    public string TargetItemId { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}
