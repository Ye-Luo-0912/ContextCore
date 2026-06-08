using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

public sealed class ContextCoreStorageInfo
{
    public string Provider { get; init; } = string.Empty;

    public string? RootPath { get; init; }
}

/// <summary>运行时能力摘要，描述当前 provider 或运行时组件的能力状态。</summary>
public sealed class ProviderCapabilityResponse
{
    public string Name { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public bool Active { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>运行时单项探针结果，统一用于 status / ready / deep 输出。</summary>
public sealed class RuntimeProbeCheckResponse
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public bool HasSideEffect { get; init; }

    public double DurationMs { get; init; }

    public string? Warning { get; init; }

    public string? Detail { get; init; }
}

/// <summary>运行时 readiness / deep probe 统一响应。</summary>
public sealed class RuntimeReadinessResponse
{
    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CheckedAt { get; init; }

    public string StorageProvider { get; init; } = string.Empty;

    public bool ProductionReady { get; init; }

    public string ProviderState { get; init; } = string.Empty;

    public string RetrievalBaseline { get; init; } = string.Empty;

    public bool FromCache { get; init; }

    public int CacheTtlSeconds { get; init; }

    public string? ProbeScope { get; init; }

    public IReadOnlyList<ProviderCapabilityResponse> Capabilities { get; init; } = Array.Empty<ProviderCapabilityResponse>();

    public IReadOnlyList<RuntimeProbeCheckResponse> Checks { get; init; } = Array.Empty<RuntimeProbeCheckResponse>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public ShortTermMaintenanceStatusResponse? ShortTermMaintenance { get; init; }
}

/// <summary>/api/status 的稳定运行时状态响应。</summary>
public sealed class RuntimeStatusResponse
{
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset Utc { get; init; }

    public ContextCoreStorageInfo Storage { get; init; } = new();

    public ContextCoreServiceJobQueueResponse Jobs { get; init; } = new();

    public string RetrievalBaseline { get; init; } = string.Empty;

    public IReadOnlyList<ProviderCapabilityResponse> Capabilities { get; init; } = Array.Empty<ProviderCapabilityResponse>();

    public RuntimeReadinessResponse Readiness { get; init; } = new();

    public ShortTermMaintenanceStatusResponse? ShortTermMaintenance { get; init; }
}

/// <summary>聚合 status / readiness / optional deep status 的运行时快照，供 ControlRoom 等上层调用方一次性消费。</summary>
public sealed class RuntimeSnapshotResponse
{
    public RuntimeStatusResponse Status { get; init; } = new();

    public RuntimeReadinessResponse Readiness { get; init; } = new();

    public RuntimeReadinessResponse? DeepStatus { get; init; }
}

public sealed class ContextCoreAdminStatusResponse
{
    public ContextCoreStorageInfo Storage { get; init; } = new();

    public string? Workspace { get; init; }

    public string? Collection { get; init; }

    public string RetrievalBaseline { get; init; } = string.Empty;
}

public sealed class ContextCoreBackupStatusResponse
{
    public string Provider { get; init; } = string.Empty;

    public string? Root { get; init; }

    public bool? Exists { get; init; }

    public int? FileCount { get; init; }

    public int? JsonlFileCount { get; init; }

    public long? TotalSizeBytes { get; init; }

    public double? TotalSizeMb { get; init; }

    public string? SchemaVersion { get; init; }

    public string? Note { get; init; }
}

public sealed class ContextCoreBackupCreateResponse
{
    public string BackupPath { get; init; } = string.Empty;

    public long BackupSizeBytes { get; init; }

    public double BackupSizeMb { get; init; }

    public string SourceRoot { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ContextCoreBackupValidateResponse
{
    public bool Healthy { get; init; }

    public string? Message { get; init; }

    public int ScannedFiles { get; init; }

    public int CorruptFiles { get; init; }

    public IReadOnlyList<ContextCoreBackupValidateFile> Files { get; init; } = Array.Empty<ContextCoreBackupValidateFile>();
}

public sealed class ContextCoreBackupValidateFile
{
    public string File { get; init; } = string.Empty;

    public int TotalLines { get; init; }

    public int ValidLines { get; init; }

    public int CorruptLines { get; init; }

    public IReadOnlyList<ContextCoreBackupValidateIssue> Issues { get; init; } = Array.Empty<ContextCoreBackupValidateIssue>();
}

public sealed class ContextCoreBackupValidateIssue
{
    public int Line { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Preview { get; init; } = string.Empty;
}

public sealed class ContextCoreSchemaVersionResponse
{
    public string Provider { get; init; } = string.Empty;

    public string? SchemaVersion { get; init; }

    public string? Note { get; init; }

    public string? CodeVersion { get; init; }

    public string? AppliedVersion { get; init; }

    public bool? UpToDate { get; init; }

    public bool? AutoMigrate { get; init; }
}

public sealed class ContextCoreHealthLiveResponse
{
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset Utc { get; init; }
}

public sealed class ContextCoreHealthReadyResponse
{
    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CheckedAt { get; init; }

    public string StorageProvider { get; init; } = string.Empty;

    public bool ProductionReady { get; init; }

    public string ProviderState { get; init; } = string.Empty;

    public string RetrievalBaseline { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreServiceCapabilityResponse> Capabilities { get; init; } = Array.Empty<ContextCoreServiceCapabilityResponse>();

    public IReadOnlyList<ContextCoreHealthCheckResponse> Checks { get; init; } = Array.Empty<ContextCoreHealthCheckResponse>();
}

public sealed class ContextCoreHealthCheckResponse
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public bool HasSideEffect { get; init; }

    public double DurationMs { get; init; }

    public string? Warning { get; init; }

    public string? Detail { get; init; }
}

public sealed class ContextCoreJobStatsResponse
{
    public int Pending { get; init; }

    public int Running { get; init; }

    public int Succeeded { get; init; }

    public int Failed { get; init; }

    public int Cancelled { get; init; }

    public long TotalRetries { get; init; }

    public double? AvgDurationMs { get; init; }

    public ContextCoreJobErrorSummary? LastError { get; init; }

    public DateTimeOffset? LastSuccessTime { get; init; }

    public int SampledTotal { get; init; }
}

public sealed class ContextCoreJobErrorSummary
{
    public string JobId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public DateTimeOffset? Time { get; init; }
}

public sealed class ContextCoreDeadLetterJobsResponse
{
    public int Count { get; init; }

    public IReadOnlyList<ContextJob> Items { get; init; } = Array.Empty<ContextJob>();
}

/// <summary>/api/status 的显式成功响应，供 Service 和测试共享。</summary>
public sealed class ContextCoreServiceStatusResponse
{
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset Utc { get; init; }

    public ContextCoreStorageInfo Storage { get; init; } = new();

    public ContextCoreServiceReadinessResponse Readiness { get; init; } = new();

    public ContextCoreServiceJobQueueResponse Jobs { get; init; } = new();

    public string RetrievalBaseline { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreServiceCapabilityResponse> Capabilities { get; init; } = Array.Empty<ContextCoreServiceCapabilityResponse>();
}

/// <summary>/api/status 中的 readiness 结果。</summary>
public sealed class ContextCoreServiceReadinessResponse
{
    public string State { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool ProductionReady { get; init; }

    public string ProviderState { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreServiceReadinessCheckResponse> Checks { get; init; } = Array.Empty<ContextCoreServiceReadinessCheckResponse>();
}

/// <summary>/api/status readiness 检查项。</summary>
public sealed class ContextCoreServiceReadinessCheckResponse
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public bool HasSideEffect { get; init; }

    public double DurationMs { get; init; }

    public string? Warning { get; init; }

    public string? Detail { get; init; }
}

/// <summary>/api/status 中的作业统计摘要。</summary>
public sealed class ContextCoreServiceJobQueueResponse
{
    public int Queued { get; init; }

    public int Running { get; init; }
}

/// <summary>Service Alpha 运行时能力摘要，供 status/ready 端点稳定输出。</summary>
public sealed class ContextCoreServiceCapabilityResponse
{
    public string Name { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public bool Active { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>/api/status/deep 的显式成功响应。</summary>
public sealed class ContextCoreDeepStatusResponse
{
    public string State { get; init; } = string.Empty;

    public DateTimeOffset CheckedAt { get; init; }

    public string ProbeId { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreDeepStoreCheckResponse> Checks { get; init; } = Array.Empty<ContextCoreDeepStoreCheckResponse>();
}

/// <summary>/api/status/deep 的单项探针结果。</summary>
public sealed class ContextCoreDeepStoreCheckResponse
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public long ElapsedMs { get; init; }

    public string Severity { get; init; } = string.Empty;

    public bool HasSideEffect { get; init; }

    public double DurationMs { get; init; }

    public string? Warning { get; init; }

    public string? Detail { get; init; }
}

public sealed class ContextCoreRequeueJobResponse
{
    public string OriginalJobId { get; init; } = string.Empty;

    public string NewJobId { get; init; } = string.Empty;

    public ContextJob Job { get; init; } = new();
}

public sealed class ContextCoreModelRouteResolveResponse
{
    public string Role { get; init; } = string.Empty;

    public string? TaskKind { get; init; }

    public string? ThinkingMode { get; init; }

    public string RouteSource { get; init; } = string.Empty;

    public ContextCoreModelRouteDescriptor? Route { get; init; }

    public ContextCoreModelSelectionResponse? Primary { get; init; }

    public ContextCoreModelSelectionResponse? Fallback { get; init; }
}

public sealed class ContextCoreModelRouteResolveRequest
{
    public string? Role { get; init; }

    public string? TaskKind { get; init; }

    public string? ThinkingMode { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    public string? Prompt { get; init; }

    public string? ResponseFormat { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class ContextCoreModelRouteDescriptor
{
    public string Role { get; init; } = string.Empty;

    public string? TaskKind { get; init; }

    public string? ThinkingMode { get; init; }

    public int Priority { get; init; }

    public string? PrimaryModelName { get; init; }

    public string? PrimaryModelCategory { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    public string? FallbackModelName { get; init; }

    public string? FallbackModelCategory { get; init; }

    public int MaxRetryCount { get; init; }

    public bool EnableFallback { get; init; }

    public bool HighRiskTask { get; init; }
}

public sealed class ContextCoreModelSelectionResponse
{
    public string? RequestedModelName { get; init; }

    public string? RequestedCategory { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    public string? ModelName { get; init; }

    public string? Provider { get; init; }

    public string? ApiProviderName { get; init; }

    public string? ProviderModel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    public bool Found { get; init; }

    public bool Enabled { get; init; }

    public double Score { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<ContextCoreModelSelectionCandidateResponse> Candidates { get; init; } = Array.Empty<ContextCoreModelSelectionCandidateResponse>();
}

public sealed class ContextCoreModelSelectionCandidateResponse
{
    public string Name { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string? ApiProviderName { get; init; }

    public string? ProviderModel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    public double Score { get; init; }
}

/// <summary>/api/relations/* 的显式成功响应。</summary>
public sealed class ContextCoreRelationLookupResponse
{
    public string ItemId { get; init; } = string.Empty;

    public IReadOnlyList<ContextRelation> Outgoing { get; init; } = Array.Empty<ContextRelation>();

    public IReadOnlyList<ContextRelation> Incoming { get; init; } = Array.Empty<ContextRelation>();
}

/// <summary>/api/model/status 的显式成功响应。</summary>
public sealed class ContextCoreModelStatusResponse
{
    public IReadOnlyList<ContextCoreModelApiProviderStatusResponse> ApiProviders { get; init; } = Array.Empty<ContextCoreModelApiProviderStatusResponse>();

    public IReadOnlyList<ContextCoreModelProfileStatusResponse> ModelProfiles { get; init; } = Array.Empty<ContextCoreModelProfileStatusResponse>();

    public IReadOnlyList<ContextCoreModelHealthStatusResponse> Models { get; init; } = Array.Empty<ContextCoreModelHealthStatusResponse>();

    public IReadOnlyList<ContextCoreModelRouteStatusResponse> Routes { get; init; } = Array.Empty<ContextCoreModelRouteStatusResponse>();
}

public sealed class ContextCoreModelApiProviderStatusResponse
{
    public string Name { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool EndpointConfigured { get; init; }

    public double TimeoutSeconds { get; init; }

    public bool ApiKeyRequired { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public string ApiKeySource { get; init; } = string.Empty;

    public string? ApiKeyEnvironmentVariable { get; init; }

    public string? ApiKeyError { get; init; }
}

public sealed class ContextCoreModelProfileStatusResponse
{
    public string Name { get; init; } = string.Empty;

    public string? ApiProviderName { get; init; }

    public string? ProviderModel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    public bool? SupportsJsonResponseFormat { get; init; }

    public double? TimeoutSeconds { get; init; }

    public bool Enabled { get; init; }
}

public sealed class ContextCoreModelHealthStatusResponse
{
    public string Name { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public string? ApiProviderName { get; init; }

    public string? ProviderModel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    public bool EndpointConfigured { get; init; }

    public bool ApiKeyRequired { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public string ApiKeySource { get; init; } = string.Empty;

    public string? ApiKeyEnvironmentVariable { get; init; }

    public string? ConfigurationError { get; init; }

    public string Availability { get; init; } = string.Empty;

    public long? LatencyMs { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }
}

public sealed class ContextCoreModelRouteStatusResponse
{
    public string Role { get; init; } = string.Empty;

    public string? TaskKind { get; init; }

    public string? ThinkingMode { get; init; }

    public int Priority { get; init; }

    public string? PrimaryModelName { get; init; }

    public string? PrimaryModelCategory { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    public string? FallbackModelName { get; init; }

    public string? FallbackModelCategory { get; init; }

    public int MaxRetryCount { get; init; }

    public bool EnableFallback { get; init; }

    public bool FallbackOnTimeout { get; init; }

    public bool FallbackOnRateLimit { get; init; }

    public bool FallbackOnServerError { get; init; }

    public bool FallbackOnInvalidJson { get; init; }

    public bool HighRiskTask { get; init; }

    public ContextCoreModelSelectionResponse? Primary { get; init; }

    public ContextCoreModelSelectionResponse? Fallback { get; init; }
}
