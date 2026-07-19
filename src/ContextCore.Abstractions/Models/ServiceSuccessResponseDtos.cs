namespace ContextCore.Abstractions.Models;

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

    /// <summary>已弃用——历史遗留字段，恒为 true。新代码应使用 <see cref="AutoBootstrap"/>。</summary>
    public bool? AutoMigrate { get; init; }

    /// <summary>
    /// P0-6：服务启动时是否自动应用 baseline migration。反映 <c>Storage:AutoBootstrap</c> 配置（默认 true）。
    /// </summary>
    public bool? AutoBootstrap { get; init; }
}

public sealed class PostgresOperationalStoreDiagnostics
{
    public bool ProviderEnabled { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public bool ConnectionAvailable { get; init; }

    public bool SchemaExists { get; init; }

    public string? CurrentSchemaVersion { get; init; }

    public int PendingMigrations { get; init; }

    public int TableCount { get; init; }

    public int RequiredTableMissingCount { get; init; }

    public string ProviderCapabilityStatus { get; init; } = string.Empty;

    public string RedactedConnectionString { get; init; } = string.Empty;

    public bool AutoMigrate { get; init; }

    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingRequiredTables { get; init; } = Array.Empty<string>();

    public PostgresSchemaVerificationReport? SchemaVerification { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed record PostgresSchemaVerificationReport
{
    public bool ProviderEnabled { get; init; }

    public bool ConnectionAvailable { get; init; }

    public string SchemaName { get; init; } = string.Empty;

    public string? CurrentSchemaVersion { get; init; }

    public int AppliedMigrationCount { get; init; }

    public int RequiredTableCount { get; init; }

    public int MissingRequiredTableCount { get; init; }

    public int RequiredIndexCount { get; init; }

    public int MissingIndexCount { get; init; }

    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingRequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresStorageStatusResponse
{
    public bool Enabled { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public bool ConnectionAvailable { get; init; }

    public string? CurrentSchemaVersion { get; init; }

    public int PendingMigrations { get; init; }

    public int RequiredTableMissingCount { get; init; }

    public string CapabilityStatus { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresMigrationRequest
{
    public bool Confirm { get; init; }
}

public sealed class PostgresMigrationPlanResponse
{
    public bool DryRun { get; init; } = true;

    public bool ProviderEnabled { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string? CurrentSchemaVersion { get; init; }

    public IReadOnlyList<string> PendingMigrations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingRequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresMigrationApplyResponse
{
    public bool Applied { get; init; }

    public bool ConfirmRequired { get; init; }

    public string? SchemaVersion { get; init; }

    public IReadOnlyList<string> AppliedMigrations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresRelationStoreDiagnostics
{
    public bool ProviderEnabled { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public bool UseForRuntime { get; init; }

    public string ActiveRuntimeProvider { get; init; } = "FileSystemRelationStore";

    public bool ConnectionAvailable { get; init; }

    public string? SchemaVersion { get; init; }

    public bool RelationTableExists { get; init; }

    public bool RelationReviewsTableExists { get; init; }

    public IReadOnlyList<string> RequiredIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingRequiredIndexes { get; init; } = Array.Empty<string>();

    public int RelationCount { get; init; }

    public int ReviewCount { get; init; }

    public string RedactedConnectionString { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RelationDiagnosticsSnapshot
{
    public string DiagnosticId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string RelationId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string DiagnosticKind { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PostgresRelationReviewProviderDiagnostics
{
    public bool ProviderEnabled { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public bool UseForRuntime { get; init; }

    public string ActiveRuntimeProvider { get; init; } = "FileSystemRelationStore";

    public bool ConnectionAvailable { get; init; }

    public string? SchemaVersion { get; init; }

    public bool RelationReviewsTableExists { get; init; }

    public bool RelationDiagnosticsTableExists { get; init; }

    public IReadOnlyList<string> RequiredIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingRequiredIndexes { get; init; } = Array.Empty<string>();

    public int ReviewCount { get; init; }

    public int DiagnosticsCount { get; init; }

    public string RedactedConnectionString { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationGovernanceReadinessGateReport
{
    public bool ProviderEnabled { get; init; }

    public bool Passed { get; init; }

    public bool StorageReady { get; init; }

    public string? SchemaVersion { get; init; }

    public bool SchemaVersionReady { get; init; }

    public bool RelationTableExists { get; init; }

    public bool RelationReviewsTableExists { get; init; }

    public bool RelationDiagnosticsTableExists { get; init; }

    public int MissingRequiredIndexCount { get; init; }

    public bool RelationStoreParityPassed { get; init; }

    public bool RelationReviewParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool GovernanceParityPassed { get; init; }

    public int MismatchCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool P15GateExpected { get; init; } = true;

    public bool CanDualWrite { get; init; }

    public bool CanShadowRead { get; init; }

    public bool CanRuntimeSwitch { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public enum RelationGovernanceProviderMode
{
    FileSystemPrimary,
    DualWriteOnly,
    ShadowRead,
    GuardedPostgresPrimary
}

public sealed class RelationGovernanceScopedRule
{
    public string ScopeName { get; init; } = string.Empty;

    public string ScopeDescription { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public string RolloutStage { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}

public sealed class RelationGovernanceProviderSwitchOptions
{
    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.FileSystemPrimary;

    public bool Enabled { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedWorkspaces { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedCollections { get; init; } = Array.Empty<string>();

    public IReadOnlyList<RelationGovernanceScopedRule> ScopedRules { get; init; } = Array.Empty<RelationGovernanceScopedRule>();

    public string ScopeName { get; init; } = string.Empty;

    public string ScopeDescription { get; init; } = string.Empty;

    public string RolloutStage { get; init; } = string.Empty;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireReadinessGate { get; init; } = true;

    public bool RequireRuntimeCanaryPassed { get; init; } = true;

    public string ProviderId { get; init; } = "postgres-relation-governance-v1";
}

public sealed class RelationGovernanceProviderSwitchTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string OperationKind { get; init; } = string.Empty;

    public string PrimaryProvider { get; init; } = string.Empty;

    public bool FallbackUsed { get; init; }

    public bool MismatchDetected { get; init; }

    public string PostgresError { get; init; } = string.Empty;

    public string ReadinessGateVersion { get; init; } = string.Empty;

    public double DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostgresRelationProviderSwitchGateReport
{
    public bool Passed { get; init; }

    public bool GovernanceReadinessGatePassed { get; init; }

    public bool DualWriteQualityReady { get; init; }

    public bool ShadowReadQualityReady { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresReadFailureCount { get; init; }

    public int PostgresWriteFailureCount { get; init; }

    public bool FallbackPathTested { get; init; }

    public bool AllowlistScopeConfigured { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationRuntimeCanaryReport
{
    public string CanaryScope { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FallbackCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationScopedServiceModeStatusResponse
{
    public string CurrentMode { get; init; } = RelationGovernanceProviderMode.FileSystemPrimary.ToString();

    public string ActiveRuntimeProvider { get; init; } = "FileSystemRelationStore";

    public IReadOnlyList<string> AllowlistedWorkspaces { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowlistedCollections { get; init; } = Array.Empty<string>();

    public bool FallbackEnabled { get; init; }

    public bool ComparisonTraceEnabled { get; init; }

    public bool GovernanceReadinessGatePassed { get; init; }

    public bool ProviderSwitchGatePassed { get; init; }

    public bool RuntimeCanaryPassed { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class ContextCoreHealthLiveResponse
{
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset Utc { get; init; }
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

/// <summary>/api/status 中的作业统计摘要。</summary>
public sealed class ContextCoreServiceJobQueueResponse
{
    public int Queued { get; init; }

    public int Running { get; init; }
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

public sealed class PostgresVectorProviderDistribution
{
    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public int Count { get; init; }
}

public sealed class PostgresVectorQueryPreviewSample
{
    public string SampleId { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public int PgVectorCandidateCount { get; init; }

    public int FileSystemCandidateCount { get; init; }

    public IReadOnlyList<string> PgVectorTopKIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FileSystemTopKIds { get; init; } = Array.Empty<string>();

    public int TopKOverlapCount { get; init; }

    public bool OrderingMatched { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();
}
