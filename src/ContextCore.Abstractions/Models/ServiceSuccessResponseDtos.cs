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

    public bool? AutoMigrate { get; init; }
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

public sealed class PostgresRelationStoreOptions
{
    public bool Enabled { get; init; }

    public string ProviderId { get; init; } = "postgres-relation-store-v1";

    public bool UseForRuntime { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    public int BatchSize { get; init; } = 100;
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

public sealed class PostgresRelationStoreParityReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int FixtureRelationCount { get; init; }

    public bool GetPassed { get; init; }

    public bool ListPassed { get; init; }

    public bool SourceQueryPassed { get; init; }

    public bool TargetQueryPassed { get; init; }

    public bool TypeQueryPassed { get; init; }

    public bool LifecycleQueryPassed { get; init; }

    public bool ReviewStatusQueryPassed { get; init; }

    public bool ReplacementChainQueryPassed { get; init; }

    public bool DeletePassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

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

public sealed class PostgresRelationReviewParityReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int FixtureReviewCount { get; init; }

    public int FixtureDiagnosticsCount { get; init; }

    public bool ReviewListPassed { get; init; }

    public bool LatestReviewPassed { get; init; }

    public bool ReviewStatusFilterPassed { get; init; }

    public bool ReviewerFilterPassed { get; init; }

    public bool OperationIdFilterPassed { get; init; }

    public bool DiagnosticsByRelationPassed { get; init; }

    public bool DiagnosticsByItemPassed { get; init; }

    public bool DiagnosticsKindFilterPassed { get; init; }

    public bool DiagnosticsSeverityFilterPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationGovernanceParityReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool RelationParityPassed { get; init; }

    public bool ReviewParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool GovernanceParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public bool CanDualWrite { get; init; }

    public bool CanShadowRead { get; init; }

    public bool CanRuntimeSwitch { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

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

public sealed class RelationGovernanceDualWriteOptions
{
    public bool Enabled { get; init; }

    public bool WritePostgres { get; init; }

    public bool TraceEnabled { get; init; } = true;

    public bool FallbackOnPostgresFailure { get; init; } = true;

    public bool FailOnMismatch { get; init; }
}

public sealed class RelationGovernanceDualWriteTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string TargetKind { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public bool FileSystemWriteSucceeded { get; init; }

    public bool PostgresWriteSucceeded { get; init; }

    public bool MismatchDetected { get; init; }

    public string MismatchReason { get; init; } = string.Empty;

    public string PostgresError { get; init; } = string.Empty;

    public bool FallbackUsed { get; init; }

    public double DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostgresRelationDualWriteSmokeReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool RelationDualWritePassed { get; init; }

    public bool ReviewDualWritePassed { get; init; }

    public bool DiagnosticsDualWritePassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public int TraceCount { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationDualWriteQualityReport
{
    public int TraceCount { get; init; }

    public int FileSystemWriteSuccessCount { get; init; }

    public int PostgresWriteSuccessCount { get; init; }

    public int PostgresWriteFailureCount { get; init; }

    public int MismatchCount { get; init; }

    public int FallbackCount { get; init; }

    public double AverageDurationMs { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RelationGovernanceShadowReadOptions
{
    public bool Enabled { get; init; }

    public bool TraceEnabled { get; init; } = true;

    public bool ReadPostgres { get; init; }

    public bool CompareResults { get; init; } = true;

    public bool FailOnMismatch { get; init; }

    public int MaxTraceItems { get; init; } = 100;
}

public sealed class RelationGovernanceShadowReadTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ReadKind { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public bool FileSystemReadSucceeded { get; init; }

    public bool PostgresReadSucceeded { get; init; }

    public string FileSystemResultHash { get; init; } = string.Empty;

    public string PostgresResultHash { get; init; } = string.Empty;

    public bool MismatchDetected { get; init; }

    public string MismatchReason { get; init; } = string.Empty;

    public string PostgresError { get; init; } = string.Empty;

    public bool FallbackUsed { get; init; }

    public double FileSystemDurationMs { get; init; }

    public double PostgresDurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostgresRelationShadowReadSmokeReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TraceCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationShadowReadQualityReport
{
    public int TraceCount { get; init; }

    public int FileSystemReadSuccessCount { get; init; }

    public int PostgresReadSuccessCount { get; init; }

    public int PostgresReadFailureCount { get; init; }

    public int MismatchCount { get; init; }

    public int FallbackCount { get; init; }

    public double AverageFileSystemReadMs { get; init; }

    public double AveragePostgresReadMs { get; init; }

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

public sealed class PostgresRelationProviderSwitchSmokeReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool WritePassed { get; init; }

    public bool PostgresPrimaryReadPassed { get; init; }

    public bool FileSystemFallbackPassed { get; init; }

    public bool ComparisonTraceRecorded { get; init; }

    public bool CleanupPerformed { get; init; }

    public int TraceCount { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
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

public sealed class RelationGovernanceCanaryOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireProviderSwitchGate { get; init; } = true;

    public bool RequireRuntimeCanaryPassed { get; init; } = true;
}

public sealed class RelationGovernanceExtendedCanaryOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireScopedServiceModeGate { get; init; } = true;
}

public sealed class RelationGovernanceSelectedWorkspaceCanaryOptions
{
    public bool Enabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireExtendedCanaryPassed { get; init; } = true;

    public int MaxOperations { get; init; } = 100;

    public int ObservationWindowMinutes { get; init; } = 30;
}

public enum RelationGovernanceSelectedNormalWorkspaceCleanupMode
{
    None,
    CanaryOnly,
    ExplicitConfirm
}

public sealed class RelationGovernanceSelectedNormalWorkspaceOptions
{
    public bool Enabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireScopedObservationPassed { get; init; } = true;

    public int ObservationWindowMinutes { get; init; } = 30;

    public int MaxOperations { get; init; } = 100;

    public string CanaryIdPrefix { get; init; } = string.Empty;

    public RelationGovernanceSelectedNormalWorkspaceCleanupMode CleanupMode { get; init; } = RelationGovernanceSelectedNormalWorkspaceCleanupMode.None;
}

public sealed class RelationGovernanceLimitedNormalScopeObservationOptions
{
    public bool Enabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ObservationWindowMinutes { get; init; } = 60;

    public int OperationIntervalSeconds { get; init; } = 1;

    public int MaxOperations { get; init; } = 100;

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireSelectedNormalCanaryPassed { get; init; } = true;

    public string CanaryIdPrefix { get; init; } = string.Empty;

    public RelationGovernanceSelectedNormalWorkspaceCleanupMode CleanupMode { get; init; } = RelationGovernanceSelectedNormalWorkspaceCleanupMode.None;
}

public sealed class RelationGovernanceNormalScopeRule
{
    public string ScopeName { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string RolloutStage { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public RelationGovernanceSelectedNormalWorkspaceCleanupMode CleanupMode { get; init; } = RelationGovernanceSelectedNormalWorkspaceCleanupMode.None;
}

public sealed class RelationGovernanceMultiNormalScopeCanaryOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<RelationGovernanceNormalScopeRule> Scopes { get; init; } = Array.Empty<RelationGovernanceNormalScopeRule>();

    public RelationGovernanceProviderMode Mode { get; init; } = RelationGovernanceProviderMode.GuardedPostgresPrimary;

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool RequireLimitedNormalScopeObservationPassed { get; init; } = true;

    public int ObservationWindowMinutes { get; init; } = 60;

    public int MaxOperationsPerScope { get; init; } = 100;

    public RelationGovernanceSelectedNormalWorkspaceCleanupMode CleanupMode { get; init; } = RelationGovernanceSelectedNormalWorkspaceCleanupMode.None;
}

public sealed class RelationGovernanceScopedExpansionPlan
{
    public string ScopeName { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string GateStatus { get; init; } = string.Empty;

    public string LastCanaryStatus { get; init; } = string.Empty;

    public IReadOnlyList<string> AllowedOperations { get; init; } = Array.Empty<string>();

    public bool FallbackEnabled { get; init; } = true;

    public bool ComparisonTraceEnabled { get; init; } = true;

    public string RollbackInstruction { get; init; } = string.Empty;
}

public sealed class RelationGovernanceScopedExpansionScopeStatus
{
    public string ScopeName { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string RolloutStage { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FallbackCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationScopedExpansionReport
{
    public bool GatePassed { get; init; }

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemScopeReadCount { get; init; }

    public int FallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public double AveragePostgresReadMs { get; init; }

    public double AveragePostgresWriteMs { get; init; }

    public IReadOnlyList<RelationGovernanceScopedExpansionPlan> Plans { get; init; } = Array.Empty<RelationGovernanceScopedExpansionPlan>();

    public IReadOnlyList<RelationGovernanceScopedExpansionScopeStatus> PerScopeStatus { get; init; } = Array.Empty<RelationGovernanceScopedExpansionScopeStatus>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RelationGovernanceScopedObservationOptions
{
    public bool Enabled { get; init; }

    public int ObservationWindowMinutes { get; init; } = 30;

    public int OperationIntervalSeconds { get; init; } = 1;

    public int MaxOperations { get; init; } = 100;

    public IReadOnlyList<RelationGovernanceScopedRule> ScopedRules { get; init; } = Array.Empty<RelationGovernanceScopedRule>();

    public bool FallbackToFileSystem { get; init; } = true;

    public bool ContinueComparisonTrace { get; init; } = true;

    public bool FailClosedOnMismatch { get; init; } = true;

    public bool CleanupAfterRun { get; init; }

    public bool RequireScopedExpansionGate { get; init; } = true;
}

public sealed class PostgresRelationScopedObservationReport
{
    public bool GatePassed { get; init; }

    public int ScopeCount { get; init; }

    public int ObservationWindowMinutes { get; init; }

    public int OperationIntervalSeconds { get; init; }

    public int MaxOperations { get; init; }

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public double AveragePostgresReadMs { get; init; }

    public double P95PostgresReadMs { get; init; }

    public double AveragePostgresWriteMs { get; init; }

    public double P95PostgresWriteMs { get; init; }

    public bool FallbackPathTested { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<RelationGovernanceScopedExpansionScopeStatus> PerScopeStatus { get; init; } = Array.Empty<RelationGovernanceScopedExpansionScopeStatus>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string RollbackInstruction { get; init; } = string.Empty;

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

public sealed class PostgresRelationSelectedWorkspaceCanaryReport
{
    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public double AveragePostgresReadMs { get; init; }

    public double AveragePostgresWriteMs { get; init; }

    public double AverageFileSystemFallbackMs { get; init; }

    public bool GraphExpansionPreviewParityPassed { get; init; }

    public bool ReviewLifecycleParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool ReplacementChainParityPassed { get; init; }

    public bool ControlRoomReadPathPassed { get; init; }

    public bool ClientApiRoundtripPathPassed { get; init; }

    public bool NonSelectedScopeRemainsFileSystem { get; init; }

    public bool CleanupPerformed { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationSelectedNormalWorkspaceCanaryReport
{
    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public double AveragePostgresReadMs { get; init; }

    public double P95PostgresReadMs { get; init; }

    public double AveragePostgresWriteMs { get; init; }

    public double P95PostgresWriteMs { get; init; }

    public bool GraphExpansionPreviewParityPassed { get; init; }

    public bool ReviewLifecycleParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool ReplacementChainParityPassed { get; init; }

    public bool ControlRoomReadPathPassed { get; init; }

    public bool ClientApiRoundtripPathPassed { get; init; }

    public bool NonSelectedNormalScopeRemainsFileSystem { get; init; }

    public bool CleanupPerformed { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationLimitedNormalScopeObservationReport
{
    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ObservationWindowMinutes { get; init; }

    public int OperationIntervalSeconds { get; init; }

    public int MaxOperations { get; init; }

    public string ProviderMode { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public double AveragePostgresReadMs { get; init; }

    public double P95PostgresReadMs { get; init; }

    public double AveragePostgresWriteMs { get; init; }

    public double P95PostgresWriteMs { get; init; }

    public double ErrorRate { get; init; }

    public double FallbackRate { get; init; }

    public bool GraphExpansionPreviewParityPassed { get; init; }

    public bool ReviewLifecycleParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool ReplacementChainParityPassed { get; init; }

    public bool ControlRoomReadPathPassed { get; init; }

    public bool ClientApiRoundtripPathPassed { get; init; }

    public bool NonSelectedNormalScopeRemainsFileSystem { get; init; }

    public bool CleanupPerformed { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RelationGovernanceMultiNormalScopeStatus
{
    public string ScopeName { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string RolloutStage { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationMultiNormalScopeCanaryReport
{
    public bool GatePassed { get; init; }

    public int ScopeCount { get; init; }

    public int EnabledScopeCount { get; init; }

    public int OperationCount { get; init; }

    public IReadOnlyDictionary<string, int> OperationCountByScope { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public double AveragePostgresReadMs { get; init; }

    public double P95PostgresReadMs { get; init; }

    public double AveragePostgresWriteMs { get; init; }

    public double P95PostgresWriteMs { get; init; }

    public IReadOnlyList<RelationGovernanceMultiNormalScopeStatus> PerScopeStatus { get; init; } = Array.Empty<RelationGovernanceMultiNormalScopeStatus>();

    public bool GraphExpansionPreviewParityPassed { get; init; }

    public bool ReviewLifecycleParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool ReplacementChainParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationScopedExtendedCanaryReport
{
    public bool GatePassed { get; init; }

    public string ProviderMode { get; init; } = string.Empty;

    public string CanaryScope { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public bool GraphExpansionPreviewParityPassed { get; init; }

    public bool ReviewLifecycleParityPassed { get; init; }

    public bool DiagnosticsParityPassed { get; init; }

    public bool ReplacementChainParityPassed { get; init; }

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

public sealed class PostgresRelationScopedServiceModeSmokeReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public bool AllowlistedScopeUsedPostgresPrimary { get; init; }

    public bool NonAllowlistedScopeUsedFileSystem { get; init; }

    public bool FallbackTested { get; init; }

    public bool ComparisonTraceRecorded { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationScopedServiceModeGateReport
{
    public bool Passed { get; init; }

    public bool GovernanceReadinessGatePassed { get; init; }

    public bool ProviderSwitchGatePassed { get; init; }

    public bool RuntimeCanaryPassed { get; init; }

    public bool ScopedAllowlistConfigured { get; init; }

    public bool NonAllowlistedScopeRemainsFileSystem { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public bool FallbackTested { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
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

public sealed class PostgresLearningFeedbackDiagnosticsReport
{
    public bool ProviderEnabled { get; init; }

    public bool ConnectionAvailable { get; init; }

    public string SchemaVersion { get; init; } = string.Empty;

    public bool FeedbackTableExists { get; init; }

    public bool ReviewTableExists { get; init; }

    public bool FeatureCandidateTableExists { get; init; }

    public bool RequiredIndexesExist { get; init; }

    public int FeedbackCount { get; init; }

    public int ReviewCount { get; init; }

    public int FeatureCandidateCount { get; init; }

    public bool UseForRuntime { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresLearningFeedbackParityReport
{
    public bool ProviderEnabled { get; init; }

    public bool FeedbackParityPassed { get; init; }

    public bool ReviewParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public bool MetadataRoundtripPassed { get; init; }

    public bool DuplicateFeedbackUpsertPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public int FeedbackCount { get; init; }

    public int ReviewCount { get; init; }

    public int FeatureCandidateCount { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackPostgresReadinessGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string StorageDiagnosticsStatus { get; init; } = string.Empty;

    public string SchemaVersion { get; init; } = string.Empty;

    public bool FeedbackTablesExist { get; init; }

    public bool ReviewTablesExist { get; init; }

    public bool FeatureCandidateTablesExist { get; init; }

    public bool RequiredIndexesExist { get; init; }

    public bool DiagnosticsReadyForParityEval { get; init; }

    public int ParityMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> FailedConditions { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackDualWriteSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int FileSystemWriteSuccessCount { get; init; }

    public int PostgresWriteSuccessCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool DuplicateFeedbackUpsertPassed { get; init; }

    public bool MetadataRoundtripPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackShadowReadSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int OperationCount { get; init; }

    public int FileSystemReadSuccessCount { get; init; }

    public int PostgresReadSuccessCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackProviderQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int TraceCount { get; init; }

    public int FileSystemWriteSuccessCount { get; init; }

    public int PostgresWriteSuccessCount { get; init; }

    public int FileSystemReadSuccessCount { get; init; }

    public int PostgresReadSuccessCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningFeedbackScopedServiceModeSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = LearningFeedbackProviderMode.FileSystemPrimary.ToString();

    public bool ProviderQualityReady { get; init; }

    public bool AllowlistConfigured { get; init; }

    public bool NonAllowlistedScopeRemainsFileSystem { get; init; }

    public bool PostgresPrimaryResultVerified { get; init; }

    public bool FileSystemFallbackResultVerified { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemScopeOperationCount { get; init; }

    public int FallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string RollbackInstruction { get; init; } = "remove learning feedback scoped allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false";

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackScopedServiceModeGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public bool ReadinessGatePassed { get; init; }

    public bool DualWriteSmokePassed { get; init; }

    public bool ShadowReadSmokePassed { get; init; }

    public bool ProviderQualityReady { get; init; }

    public bool ScopedAllowlistConfigured { get; init; }

    public bool NonAllowlistedScopeRemainsFileSystem { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool FallbackTested { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackSelectedNormalScopeCanaryReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = LearningFeedbackProviderMode.GuardedPostgresPrimary.ToString();

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool ReviewSummaryParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string RollbackInstruction { get; init; } =
        "remove selected learning feedback scope allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false";

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackLimitedScopeObservationReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ObservationWindowMinutes { get; init; }

    public string ProviderMode { get; init; } = LearningFeedbackProviderMode.GuardedPostgresPrimary.ToString();

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public double ErrorRate { get; init; }

    public double FallbackRate { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool ReviewSummaryParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public int TrainableCandidateLeakCount { get; init; }

    public int SmokeCandidateExcludedCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string RollbackInstruction { get; init; } =
        "remove limited learning feedback scope allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false";

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackLimitedScopeQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public int OperationCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public double ErrorRate { get; init; }

    public double FallbackRate { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool ReviewSummaryParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public int TrainableCandidateLeakCount { get; init; }

    public int SmokeCandidateExcludedCount { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackPostgresFreezeGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string LearningFeedbackPostgres { get; init; } = "NotReady";

    public string DefaultProvider { get; init; } = "FileSystem";

    public string AllowedMode { get; init; } =
        "GuardedPostgresPrimary only for allowlisted scopes";

    public IReadOnlyList<string> Required { get; init; } =
        ["fallback", "comparison trace"];

    public IReadOnlyList<string> Forbidden { get; init; } =
        ["global default-on", "auto-training", "auto-readiness-change"];

    public bool ReadinessGatePassed { get; init; }

    public bool ProviderQualityReady { get; init; }

    public bool ScopedServiceModeGatePassed { get; init; }

    public bool SelectedNormalScopeCanaryPassed { get; init; }

    public bool LimitedObservationQualityPassed { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public int TrainableCandidateLeakCount { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool ReviewSummaryParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public bool FallbackRequired { get; init; } = true;

    public bool ComparisonTraceRequired { get; init; } = true;

    public bool GlobalDefaultOnForbidden { get; init; } = true;

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueDiagnosticsReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ProviderEnabled { get; init; }

    public bool ConnectionAvailable { get; init; }

    public string SchemaVersion { get; init; } = string.Empty;

    public bool JobTableExists { get; init; }

    public bool RequiredIndexesExist { get; init; }

    public int PendingCount { get; init; }

    public int RunningCount { get; init; }

    public int FailedCount { get; init; }

    public int DeadLetterCount { get; init; }

    public int StaleLeaseCount { get; init; }

    public bool UseForRuntime { get; init; }

    public int JobCount { get; init; }

    public IReadOnlyList<string> MissingIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueParityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int JobCount { get; init; }

    public int OperationCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public bool EnqueueListParityPassed { get; init; }

    public bool DuplicateUpsertParityPassed { get; init; }

    public bool StatusTransitionParityPassed { get; init; }

    public bool RetryCountParityPassed { get; init; }

    public bool CancelParityPassed { get; init; }

    public bool DeadLetterParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueLeaseSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int JobCount { get; init; }

    public int OperationCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int LeaseAcquireCount { get; init; }

    public int LeaseConflictCount { get; init; }

    public int LeaseExpiredReacquireCount { get; init; }

    public bool HeartbeatRenewalPassed { get; init; }

    public bool CompleteTransitionPassed { get; init; }

    public bool RetryTransitionPassed { get; init; }

    public bool DeadLetterTransitionPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class JobQueueDualWriteTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string OperationKind { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public bool FileSystemWriteSucceeded { get; init; }

    public bool PostgresWriteSucceeded { get; init; }

    public bool MismatchDetected { get; init; }

    public string MismatchReason { get; init; } = string.Empty;

    public string PostgresError { get; init; } = string.Empty;

    public bool FallbackUsed { get; init; }

    public double DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class JobQueueShadowReadTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ReadKind { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public bool FileSystemReadSucceeded { get; init; }

    public bool PostgresReadSucceeded { get; init; }

    public string FileSystemResultHash { get; init; } = string.Empty;

    public string PostgresResultHash { get; init; } = string.Empty;

    public bool MismatchDetected { get; init; }

    public string MismatchReason { get; init; } = string.Empty;

    public string PostgresError { get; init; } = string.Empty;

    public bool FallbackUsed { get; init; }

    public double FileSystemDurationMs { get; init; }

    public double PostgresDurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostgresJobQueueDualWriteSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TraceCount { get; init; }

    public int FileSystemWriteSuccessCount { get; init; }

    public int PostgresWriteSuccessCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool LeaseParityPassed { get; init; }

    public bool RetryParityPassed { get; init; }

    public bool DeadLetterParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<JobQueueDualWriteTrace> Traces { get; init; } = Array.Empty<JobQueueDualWriteTrace>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueShadowReadSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TraceCount { get; init; }

    public int FileSystemReadSuccessCount { get; init; }

    public int PostgresReadSuccessCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool CountParityPassed { get; init; }

    public bool LeaseParityPassed { get; init; }

    public bool RetryParityPassed { get; init; }

    public bool DeadLetterParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<JobQueueShadowReadTrace> Traces { get; init; } = Array.Empty<JobQueueShadowReadTrace>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueProviderQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int TraceCount { get; init; }

    public int FileSystemWriteSuccessCount { get; init; }

    public int PostgresWriteSuccessCount { get; init; }

    public int FileSystemReadSuccessCount { get; init; }

    public int PostgresReadSuccessCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool LeaseParityPassed { get; init; }

    public bool RetryParityPassed { get; init; }

    public bool DeadLetterParityPassed { get; init; }

    public bool CountParityPassed { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class JobQueueScopedWorkerCanaryTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Mode { get; init; } = JobQueueWorkerProviderMode.GuardedPostgresPrimary.ToString();

    public string OperationKind { get; init; } = string.Empty;

    public string JobId { get; init; } = string.Empty;

    public string JobKind { get; init; } = string.Empty;

    public string PrimaryProvider { get; init; } = string.Empty;

    public bool PostgresSucceeded { get; init; }

    public bool FileSystemSucceeded { get; init; }

    public bool MismatchDetected { get; init; }

    public string MismatchReason { get; init; } = string.Empty;

    public string PostgresError { get; init; } = string.Empty;

    public bool ScopeLeakDetected { get; init; }

    public bool LeaseConflictObserved { get; init; }

    public bool LeaseExpiredReacquired { get; init; }

    public bool HeartbeatRenewed { get; init; }

    public double DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostgresJobQueueScopedWorkerCanaryReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = JobQueueWorkerProviderMode.GuardedPostgresPrimary.ToString();

    public bool ProviderQualityReady { get; init; }

    public int JobCount { get; init; }

    public int CompletedCount { get; init; }

    public int FailedCount { get; init; }

    public int RetriedCount { get; init; }

    public int DeadLetterCount { get; init; }

    public int LeaseAcquireCount { get; init; }

    public int LeaseConflictCount { get; init; }

    public int LeaseExpiredReacquireCount { get; init; }

    public int HeartbeatCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool NonSelectedScopeRemainsFileSystem { get; init; }

    public bool RuntimeWorkerGlobalProviderUnchanged { get; init; } = true;

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<JobQueueScopedWorkerCanaryTrace> Traces { get; init; } =
        Array.Empty<JobQueueScopedWorkerCanaryTrace>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueScopedWorkerQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public int JobCount { get; init; }

    public int CompletedCount { get; init; }

    public int RetriedCount { get; init; }

    public int DeadLetterCount { get; init; }

    public int LeaseAcquireCount { get; init; }

    public int LeaseConflictCount { get; init; }

    public int LeaseExpiredReacquireCount { get; init; }

    public int HeartbeatCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool NonSelectedScopeRemainsFileSystem { get; init; }

    public bool RuntimeWorkerGlobalProviderUnchanged { get; init; } = true;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class JobQueueLimitedWorkerScopeObservationTrace
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string OperationKind { get; init; } = string.Empty;

    public string JobId { get; init; } = string.Empty;

    public string CanaryJobKind { get; init; } = string.Empty;

    public string PrimaryProvider { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public bool MismatchDetected { get; init; }

    public string MismatchReason { get; init; } = string.Empty;

    public bool LeaseConflictObserved { get; init; }

    public bool LeaseExpiredReacquired { get; init; }

    public bool HeartbeatRenewed { get; init; }

    public bool DuplicateExecutionDetected { get; init; }

    public bool LeaseViolationDetected { get; init; }

    public string ViolationReason { get; init; } = string.Empty;

    public string PostgresError { get; init; } = string.Empty;

    public bool ScopeLeakDetected { get; init; }

    public double DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostgresJobQueueLimitedWorkerScopeObservationReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ObservationWindowSeconds { get; init; }

    public int JobCount { get; init; }

    public int CompletedCount { get; init; }

    public int RetriedCount { get; init; }

    public int DeadLetterCount { get; init; }

    public int CancelledCount { get; init; }

    public int LeaseAcquireCount { get; init; }

    public int LeaseConflictCount { get; init; }

    public int LeaseExpiredReacquireCount { get; init; }

    public int HeartbeatCount { get; init; }

    public int DuplicateExecutionCount { get; init; }

    public int LeaseViolationCount { get; init; }

    public int RetryViolationCount { get; init; }

    public int DeadLetterViolationCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool NonSelectedScopeRemainsFileSystem { get; init; }

    public bool RuntimeWorkerGlobalProviderUnchanged { get; init; } = true;

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Violations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<JobQueueLimitedWorkerScopeObservationTrace> Traces { get; init; } =
        Array.Empty<JobQueueLimitedWorkerScopeObservationTrace>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueLimitedWorkerScopeQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public int ObservationWindowSeconds { get; init; }

    public int JobCount { get; init; }

    public int CompletedCount { get; init; }

    public int RetriedCount { get; init; }

    public int DeadLetterCount { get; init; }

    public int CancelledCount { get; init; }

    public int LeaseAcquireCount { get; init; }

    public int LeaseConflictCount { get; init; }

    public int LeaseExpiredReacquireCount { get; init; }

    public int HeartbeatCount { get; init; }

    public int DuplicateExecutionCount { get; init; }

    public int LeaseViolationCount { get; init; }

    public int RetryViolationCount { get; init; }

    public int DeadLetterViolationCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool NonSelectedScopeRemainsFileSystem { get; init; }

    public bool RuntimeWorkerGlobalProviderUnchanged { get; init; } = true;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class JobQueuePostgresFreezeGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string JobQueuePostgres { get; init; } = "NotReady";

    public string DefaultProvider { get; init; } = "ExistingProvider";

    public string AllowedMode { get; init; } =
        "GuardedPostgresPrimary only for explicit allowlisted worker scopes";

    public IReadOnlyList<string> Required { get; init; } =
        ["lease quality gate", "heartbeat quality gate", "retry quality gate", "dead-letter quality gate"];

    public IReadOnlyList<string> Forbidden { get; init; } =
        ["GlobalWorkerProviderSwitch", "ProductionWorkerLoopSwitchWithoutGate"];

    public bool DiagnosticsReady { get; init; }

    public bool ProviderQualityReady { get; init; }

    public bool ScopedWorkerCanaryPassed { get; init; }

    public bool LimitedWorkerScopeQualityPassed { get; init; }

    public int DuplicateExecutionCount { get; init; }

    public int LeaseViolationCount { get; init; }

    public int RetryViolationCount { get; init; }

    public int DeadLetterViolationCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool NonSelectedScopeRemainsFileSystem { get; init; }

    public bool RuntimeWorkerGlobalProviderUnchanged { get; init; }

    public bool GlobalSwitchAllowed { get; init; }

    public bool ScopedWorkerCanaryAllowed { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
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

public sealed class PostgresVectorDiagnosticsReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ProviderEnabled { get; init; }

    public bool ConnectionAvailable { get; init; }

    public bool PgVectorAvailable { get; init; }

    public string SchemaVersion { get; init; } = string.Empty;

    public bool TableExists { get; init; }

    public bool RequiredIndexesExist { get; init; }

    public int MissingIndexCount { get; init; }

    public IReadOnlyList<string> MissingIndexes { get; init; } = Array.Empty<string>();

    public int SupportedDimensionCount { get; init; }

    public IReadOnlyList<int> SupportedDimensions { get; init; } = Array.Empty<int>();

    public int IndexedEntryCount { get; init; }

    public IReadOnlyList<PostgresVectorProviderDistribution> ProviderModelDistribution { get; init; } =
        Array.Empty<PostgresVectorProviderDistribution>();

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresVectorCompatibilityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string RequestedProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public bool ProviderEnabled { get; init; }

    public bool ConnectionAvailable { get; init; }

    public bool PgVectorAvailable { get; init; }

    public bool TableExists { get; init; }

    public bool TableDimensionCompatible { get; init; }

    public bool ExistingIndexCompatible { get; init; }

    public int ExistingCompatibleEntryCount { get; init; }

    public int StaleProviderModelEntriesCount { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresVectorProviderSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool ProviderEnabled { get; init; }

    public bool ConnectionAvailable { get; init; }

    public bool PgVectorAvailable { get; init; }

    public string SchemaVersion { get; init; } = string.Empty;

    public bool TableExists { get; init; }

    public int MissingIndexCount { get; init; }

    public int SupportedDimensionCount { get; init; }

    public int InsertedCount { get; init; }

    public int UpsertedCount { get; init; }

    public int QueryCount { get; init; }

    public int MismatchCount { get; init; }

    public bool DimensionMismatchBlocked { get; init; }

    public bool ProviderModelMismatchBlocked { get; init; }

    public bool CleanupPerformed { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresVectorIndexParityReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public int OperationCount { get; init; }

    public int FileSystemEntryCount { get; init; }

    public int PostgresEntryCount { get; init; }

    public int InsertedCount { get; init; }

    public int UpsertedCount { get; init; }

    public int DeletedCount { get; init; }

    public int QueryCount { get; init; }

    public int MismatchCount { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public bool DimensionMismatchBlocked { get; init; }

    public bool ProviderModelMismatchBlocked { get; init; }

    public bool NormalizedMismatchWarned { get; init; }

    public bool CleanupPerformed { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresVectorProviderScopedReindexPlanItem
{
    public string SourceId { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string EntryId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string CurrentContentHash { get; init; } = string.Empty;

    public string ExistingContentHash { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PostgresVectorProviderScopedReindexPlan
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public string SourceKindFilter { get; init; } = string.Empty;

    public bool DryRun { get; init; } = true;

    public int CandidateCount { get; init; }

    public int PlannedInsertCount { get; init; }

    public int PlannedUpdateCount { get; init; }

    public int PlannedDeleteCount { get; init; }

    public int PlannedSkipCount { get; init; }

    public int StaleEntryCount { get; init; }

    public int OrphanEntryCount { get; init; }

    public int DuplicateSourceCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int ProviderModelMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<PostgresVectorProviderScopedReindexPlanItem> Items { get; init; } =
        Array.Empty<PostgresVectorProviderScopedReindexPlanItem>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresVectorProviderScopedReindexResult
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public bool Confirmed { get; init; }

    public int CandidateCount { get; init; }

    public int PlannedInsertCount { get; init; }

    public int PlannedUpdateCount { get; init; }

    public int PlannedSkipCount { get; init; }

    public int AppliedInsertCount { get; init; }

    public int AppliedUpdateCount { get; init; }

    public int MetadataRoundtripMismatchCount { get; init; }

    public int IndexedEntryCountAfterApply { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresVectorProviderScopedReindexReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public int PlannedInsertCount { get; init; }

    public int PlannedUpdateCount { get; init; }

    public int PlannedSkipCount { get; init; }

    public int AppliedInsertCount { get; init; }

    public int AppliedUpdateCount { get; init; }

    public int StaleEntryCount { get; init; }

    public int OrphanEntryCount { get; init; }

    public int DuplicateSourceCount { get; init; }

    public int DimensionMismatchCount { get; init; }

    public int ProviderModelMismatchCount { get; init; }

    public int MetadataRoundtripMismatchCount { get; init; }

    public int IndexedEntryCountAfterApply { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
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

public sealed class PostgresVectorQueryPreviewReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public int TopK { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public string Recommendation { get; init; } = string.Empty;

    public int QueryCount { get; init; }

    public int CandidateCount { get; init; }

    public int PgVectorCandidateCount { get; init; }

    public int FileSystemCandidateCount { get; init; }

    public int TopKOverlapCount { get; init; }

    public double TopKOverlapRate { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public bool DimensionMismatchBlocked { get; init; }

    public bool ProviderModelMismatchBlocked { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<PostgresVectorQueryPreviewSample> Samples { get; init; } =
        Array.Empty<PostgresVectorQueryPreviewSample>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresVectorShadowEvalReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int PgVectorCandidateCount { get; init; }

    public int FileSystemCandidateCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public double FileSystemRecallAfterPolicy { get; init; }

    public double RecallDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public double TopKOverlapRate { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<PostgresVectorQueryPreviewSample> Samples { get; init; } =
        Array.Empty<PostgresVectorQueryPreviewSample>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresVectorShadowEvalSummaryReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Recommendation { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<PostgresVectorShadowEvalReport> Reports { get; init; } =
        Array.Empty<PostgresVectorShadowEvalReport>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class VectorPostgresProviderFreezeGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string VectorPostgresProvider { get; init; } = "NotReady";

    public string DefaultVectorStore { get; init; } = "unchanged";

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public IReadOnlyList<string> Allowed { get; init; } =
        ["preview", "shadow", "eval"];

    public IReadOnlyList<string> Required { get; init; } =
        ["V4 readiness gate before formal retrieval"];

    public IReadOnlyList<string> Forbidden { get; init; } =
        ["FormalRetrievalSwitchWithoutV4Gate", "PackingPolicyIntegrationWithoutV4Gate", "PackageOutputIntegrationWithoutV4Gate"];

    public bool DiagnosticsReady { get; init; }

    public bool CompatibilityReady { get; init; }

    public bool ParityPassed { get; init; }

    public bool ReindexQualityPassed { get; init; }

    public bool QueryPreviewPassed { get; init; }

    public bool ShadowEvalPassed { get; init; }

    public double A3RecallDelta { get; init; }

    public double ExtendedRecallDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ProjectionMismatchCount { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}
