namespace ContextCore.Abstractions.Models;

/// <summary>向量查询配置标识。</summary>
public static class VectorQueryProfileIds
{
    public const string NormalV1 = "normal-v1";

    public const string CurrentTaskV1 = "current-task-v1";

    public const string AuditV1 = "audit-v1";

    public const string DiagnosticsV1 = "diagnostics-v1";
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

/// <summary>vector lifecycle metadata repair preview 推荐状态。</summary>
public static class VectorLifecycleMetadataRepairPlanRecommendations
{
    public const string ReadyForMetadataRepairPreview = nameof(ReadyForMetadataRepairPreview);

    public const string NeedsHumanReview = nameof(NeedsHumanReview);

    public const string UnsafeToRepair = nameof(UnsafeToRepair);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
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

/// <summary>Scoped formal preview opt-in mode。</summary>
public static class ScopedFormalPreviewOptInModes
{
    public const string Off = nameof(Off);
    public const string PreviewOnly = nameof(PreviewOnly);
}
