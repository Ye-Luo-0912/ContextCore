using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>渲染 Service 模式下的 jobs / model / admin-runtime 页面。</summary>
public static class ServiceOperationalRenderer
{
    public static string RenderJobs(ServiceJobsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Jobs");
        builder.AppendLine("============");
        builder.AppendLine($"时间   : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务   : {snapshot.BaseUrl}");
        builder.AppendLine($"作业数 : {snapshot.Jobs.Count}");
        builder.AppendLine();

        foreach (var job in snapshot.Jobs)
        {
            var payload = TryParsePayload(job.PayloadJson);
            builder.AppendLine($"- {job.JobId} [{job.Kind}/{job.State}]");
            builder.AppendLine($"  OperationId : {payload.OperationId ?? job.JobId}");
            builder.AppendLine($"  CreatedAt   : {job.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  UpdatedAt   : {(job.CompletedAt ?? job.StartedAt ?? job.CreatedAt):yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  RetryCount  : {job.RetryCount}/{job.MaxRetryCount}");
            builder.AppendLine($"  Warnings    : {(string.IsNullOrWhiteSpace(job.ErrorMessage) ? "无" : job.ErrorMessage)}");
            if (payload.Metadata.Count > 0)
            {
                builder.AppendLine($"  Metadata    : {string.Join(", ", payload.Metadata.Select(pair => $"{pair.Key}={pair.Value}"))}");
            }
        }

        return builder.ToString();
    }

    public static string RenderJobDetail(ContextJob job)
    {
        var payload = TryParsePayload(job.PayloadJson);
        var builder = new StringBuilder();
        builder.AppendLine("Service Job Detail");
        builder.AppendLine("==================");
        builder.AppendLine($"JobId       : {job.JobId}");
        builder.AppendLine($"Kind        : {job.Kind}");
        builder.AppendLine($"Status      : {job.State}");
        builder.AppendLine($"OperationId : {payload.OperationId ?? job.JobId}");
        builder.AppendLine($"CreatedAt   : {job.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"UpdatedAt   : {(job.CompletedAt ?? job.StartedAt ?? job.CreatedAt):yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"RetryCount  : {job.RetryCount}/{job.MaxRetryCount}");
        builder.AppendLine($"Warnings    : {(string.IsNullOrWhiteSpace(job.ErrorMessage) ? "无" : job.ErrorMessage)}");
        if (payload.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in payload.Metadata)
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderModel(ServiceModelSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Model Status");
        builder.AppendLine("====================");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine();
        builder.AppendLine("Providers");
        foreach (var provider in snapshot.ModelStatus.ApiProviders)
        {
            builder.AppendLine($"- {provider.Name} [{provider.Provider}] enabled={(provider.Enabled ? "yes" : "no")} endpoint={(provider.EndpointConfigured ? "configured" : "missing")}");
        }

        builder.AppendLine();
        builder.AppendLine("Routes");
        foreach (var route in snapshot.ModelStatus.Routes.Take(10))
        {
            builder.AppendLine($"- role={route.Role} task={route.TaskKind ?? "-"} mode={route.ThinkingMode ?? "-"} primary={route.Primary?.ModelName ?? route.PrimaryModelName ?? "-"} fallback={route.Fallback?.ModelName ?? route.FallbackModelName ?? "-"}");
        }

        if (snapshot.RouteResolution is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Route Resolve");
            builder.AppendLine($"- role={snapshot.RouteResolution.Role}");
            builder.AppendLine($"- selected={snapshot.RouteResolution.Primary?.ModelName ?? "未命中"}");
            builder.AppendLine($"- fallback={snapshot.RouteResolution.Fallback?.ModelName ?? "无"}");
            builder.AppendLine($"- reason={snapshot.RouteResolution.RouteSource}");
        }

        return builder.ToString();
    }

    public static string RenderAdminRuntime(ServiceAdminRuntimeSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Admin / Runtime");
        builder.AppendLine("=======================");
        builder.AppendLine($"时间          : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务          : {snapshot.BaseUrl}");
        builder.AppendLine($"RuntimeStatus : {snapshot.Runtime.Status.Status}/{snapshot.Runtime.Readiness.Status}");
        builder.AppendLine($"Storage       : {snapshot.AdminStatus.Storage.Provider}");
        builder.AppendLine($"RootPath      : {snapshot.AdminStatus.Storage.RootPath ?? "未返回"}");
        builder.AppendLine($"Retrieval     : {snapshot.AdminStatus.RetrievalBaseline}");
        builder.AppendLine($"BackupRoot    : {snapshot.BackupStatus.Root ?? "无"}");
        builder.AppendLine($"BackupExists  : {snapshot.BackupStatus.Exists}");
        builder.AppendLine($"BackupHealthy : {snapshot.BackupValidate.Healthy}");
        builder.AppendLine($"BackupMessage : {snapshot.BackupValidate.Message ?? "无"}");
        builder.AppendLine();
        builder.AppendLine("File Layout Status");
        builder.AppendLine($"DataRoot      : {snapshot.FileLayoutStatus.DataRoot}");
        builder.AppendLine($"Categories    : {snapshot.FileLayoutStatus.ArtifactCategories.Count}");
        builder.AppendLine($"ManifestCount : {snapshot.FileLayoutStatus.ManifestCount}");
        builder.AppendLine($"ReportCount   : {snapshot.FileLayoutStatus.ReportCount}");
        foreach (var sample in snapshot.FileLayoutStatus.ResolvedPathSamples.Take(4))
        {
            builder.AppendLine($"- {sample.Descriptor.Kind}/{sample.Descriptor.CapabilityId}: {sample.RelativePath}");
        }

        if (snapshot.FileLayoutStatus.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", snapshot.FileLayoutStatus.Diagnostics)}");
        }

        builder.AppendLine();
        builder.AppendLine("Memory Layout Status");
        AppendMemoryLayoutStatus(builder, snapshot.MemoryLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Trace Layout Status");
        AppendTraceLayoutStatus(builder, snapshot.TraceLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Report Layout Status");
        AppendReportLayoutStatus(builder, snapshot.ReportLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Storage Boundary Status");
        AppendStorageBoundaryStatus(builder, snapshot.StorageBoundaryReport);

        builder.AppendLine();
        builder.AppendLine("Postgres Operational Store Status");
        AppendPostgresOperationalStoreStatus(builder, snapshot.PostgresOperationalStoreDiagnostics);

        builder.AppendLine();
        builder.AppendLine("RelationStore Provider Status");
        AppendPostgresRelationStoreStatus(
            builder,
            snapshot.PostgresRelationStoreDiagnostics,
            snapshot.PostgresRelationReviewProviderDiagnostics,
            snapshot.PostgresRelationReviewParityReport,
            snapshot.PostgresRelationGovernanceParityReport,
            snapshot.PostgresRelationGovernanceReadinessGateReport,
            snapshot.PostgresRelationDualWriteQualityReport,
            snapshot.PostgresRelationShadowReadQualityReport,
            snapshot.PostgresRelationProviderSwitchSmokeReport,
            snapshot.PostgresRelationProviderSwitchGateReport,
            snapshot.PostgresRelationRuntimeCanaryReport,
            snapshot.PostgresRelationScopedServiceModeSmokeReport,
            snapshot.PostgresRelationScopedServiceModeGateReport,
            snapshot.PostgresRelationScopedExtendedCanaryReport,
            snapshot.PostgresRelationSelectedWorkspaceCanaryReport,
            snapshot.PostgresRelationScopedExpansionReport,
            snapshot.PostgresRelationScopedObservationReport,
            snapshot.PostgresRelationSelectedNormalWorkspaceCanaryReport,
            snapshot.PostgresRelationLimitedNormalScopeObservationReport,
            snapshot.PostgresRelationMultiNormalScopeCanaryReport,
            snapshot.PostgresLearningFeedbackDiagnosticsReport,
            snapshot.PostgresLearningFeedbackParityReport,
            snapshot.PostgresLearningFeedbackReadinessGateReport,
            snapshot.PostgresLearningFeedbackDualWriteSmokeReport,
            snapshot.PostgresLearningFeedbackShadowReadSmokeReport,
            snapshot.PostgresLearningFeedbackProviderQualityReport,
            snapshot.PostgresLearningFeedbackScopedServiceModeSmokeReport,
            snapshot.PostgresLearningFeedbackScopedServiceModeGateReport,
            snapshot.PostgresLearningFeedbackSelectedNormalScopeCanaryReport,
            snapshot.PostgresLearningFeedbackLimitedScopeObservationReport,
            snapshot.PostgresLearningFeedbackLimitedScopeQualityReport,
            snapshot.PostgresLearningFeedbackFreezeGateReport,
            snapshot.PostgresJobQueueDiagnosticsReport,
            snapshot.PostgresJobQueueParityReport,
            snapshot.PostgresJobQueueLeaseSmokeReport,
            snapshot.PostgresJobQueueDualWriteSmokeReport,
            snapshot.PostgresJobQueueShadowReadSmokeReport,
            snapshot.PostgresJobQueueProviderQualityReport,
            snapshot.PostgresJobQueueScopedWorkerCanaryReport,
            snapshot.PostgresJobQueueScopedWorkerQualityReport,
            snapshot.PostgresJobQueueLimitedWorkerScopeObservationReport,
            snapshot.PostgresJobQueueLimitedWorkerScopeQualityReport,
            snapshot.PostgresJobQueueFreezeGateReport);

        builder.AppendLine();
        builder.AppendLine("Vector Index Provider Status");
        AppendPostgresVectorIndexStatus(
            builder,
            snapshot.PostgresVectorDiagnosticsReport,
            snapshot.PostgresVectorCompatibilityReport,
            snapshot.PostgresVectorProviderSmokeReport,
            snapshot.PostgresVectorIndexParityReport,
            snapshot.PostgresVectorProviderScopedReindexPlan,
            snapshot.PostgresVectorProviderScopedReindexResult,
            snapshot.PostgresVectorProviderScopedReindexReport,
            snapshot.PostgresVectorQueryPreviewReport,
            snapshot.PostgresVectorShadowEvalA3Report,
            snapshot.PostgresVectorShadowEvalExtendedReport,
            snapshot.PostgresVectorShadowEvalSummaryReport,
            snapshot.PostgresVectorFreezeGateReport);

        return builder.ToString();
    }

    public static string RenderMemory(ServiceMemorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Memory");
        builder.AppendLine("==============");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Working : {snapshot.Working.Count}");
        builder.AppendLine($"Candidate: {snapshot.Candidates.Count}");
        builder.AppendLine($"Stable  : {snapshot.Stable.Count}");
        builder.AppendLine($"Global  : {snapshot.Global.Count}");
        builder.AppendLine();
        builder.AppendLine("Memory Layout Status");
        AppendMemoryLayoutStatus(builder, snapshot.MemoryLayoutDiagnostics);
        return builder.ToString();
    }

    private static void AppendMemoryLayoutStatus(StringBuilder builder, MemoryLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"DataRoot      : {diagnostics.DataRoot}");
        builder.AppendLine($"ShortTerm     : {diagnostics.ShortTermArtifactCount}");
        builder.AppendLine($"Candidate     : {diagnostics.CandidateArtifactCount}");
        builder.AppendLine($"Stable        : {diagnostics.StableArtifactCount}");
        builder.AppendLine($"TemporalReady : {diagnostics.TemporalPlaceholderReady}");
        builder.AppendLine($"LegacyFallback: {diagnostics.LegacyFallbackCount}");
        builder.AppendLine($"MissingDirs   : {diagnostics.MissingDirectoryCount}");
        foreach (var path in diagnostics.MemoryLayerPaths.Take(6))
        {
            builder.AppendLine($"- {path.Key}: {path.Value}");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendTraceLayoutStatus(StringBuilder builder, TraceLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"TraceRoot     : {diagnostics.TraceRoot}");
        builder.AppendLine($"Retrieval     : {diagnostics.RetrievalTraceCount}");
        builder.AppendLine($"RouterShadow  : {diagnostics.RouterShadowTraceCount}");
        builder.AppendLine($"RankerShadow  : {diagnostics.RankerShadowTraceCount}");
        builder.AppendLine($"GraphShadow   : {diagnostics.GraphShadowTraceCount}");
        builder.AppendLine($"VectorShadow  : {diagnostics.VectorShadowTraceCount}");
        builder.AppendLine($"ToolCallReady : {diagnostics.ToolCallPlaceholderReady}");
        builder.AppendLine($"LegacyFallback: {diagnostics.LegacyFallbackCount}");
        foreach (var path in diagnostics.TraceCategoryPaths.Take(6))
        {
            builder.AppendLine($"- {path.Key}: {path.Value}");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendReportLayoutStatus(StringBuilder builder, ReportLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"DataRoot       : {diagnostics.DataRoot}");
        builder.AppendLine($"ManifestCount  : {diagnostics.ManifestCount}");
        builder.AppendLine($"LatestReports  : {diagnostics.LatestReportCount}");
        builder.AppendLine($"LegacyMirrored : {diagnostics.LegacyMirroredCount}");
        builder.AppendLine($"MissingStandard: {diagnostics.MissingStandardArtifactCount}");
        builder.AppendLine($"MissingLegacy  : {diagnostics.MissingLegacyArtifactCount}");
        builder.AppendLine($"DuplicateHash  : {diagnostics.DuplicateContentHashCount}");
        foreach (var count in diagnostics.ReportCountByKind.OrderBy(item => item.Key).Take(8))
        {
            builder.AppendLine($"- {count.Key}: {count.Value}");
        }

        foreach (var sample in diagnostics.ResolvedPathSamples.Take(4))
        {
            builder.AppendLine($"sample {sample.ArtifactKind}/{sample.CapabilityId}: {sample.RelativePath}");
        }

        if (diagnostics.LargestReports.Count > 0)
        {
            builder.AppendLine("LargestReports");
            foreach (var report in diagnostics.LargestReports.Take(3))
            {
                builder.AppendLine($"- {report.RelativePath} ({report.SizeBytes} bytes)");
            }
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics    : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendStorageBoundaryStatus(StringBuilder builder, StorageBoundaryReport report)
    {
        builder.AppendLine($"ArtifactKinds : {report.TotalArtifactKinds}");
        builder.AppendLine($"ArtifactOnly  : {report.ArtifactOnlyCount}");
        builder.AppendLine($"Operational   : {report.OperationalStateCount}");
        builder.AppendLine($"IndexState    : {report.IndexStateCount}");
        builder.AppendLine($"DbRecommended : {report.DatabaseRecommendedCount}");
        builder.AppendLine($"FsPreferred   : {report.FileSystemPreferredCount}");
        builder.AppendLine($"Migrations    : {report.MigrationCandidates.Count}");
        builder.AppendLine($"HighPriority  : {report.HighPriorityMigrationCandidates.Count}");
        foreach (var candidate in report.HighPriorityMigrationCandidates.Take(6))
        {
            builder.AppendLine(
                $"- {candidate.SubjectId}: {candidate.Responsibility}/{candidate.PreferredProvider}, risk={candidate.MigrationRisk}");
        }

        if (report.RecommendedNextPhases.Count > 0)
        {
            builder.AppendLine("NextPhases");
            foreach (var phase in report.RecommendedNextPhases.Take(4))
            {
                builder.AppendLine($"- {phase}");
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", report.Diagnostics)}");
        }
    }

    private static void AppendPostgresOperationalStoreStatus(
        StringBuilder builder,
        PostgresOperationalStoreDiagnostics diagnostics)
    {
        builder.AppendLine($"Enabled       : {diagnostics.ProviderEnabled}");
        builder.AppendLine($"ProviderId    : {diagnostics.ProviderId}");
        builder.AppendLine($"Status        : {diagnostics.Status}");
        builder.AppendLine($"Connection    : {diagnostics.ConnectionAvailable}");
        builder.AppendLine($"SchemaVersion : {diagnostics.CurrentSchemaVersion ?? "未应用"}");
        builder.AppendLine($"Pending       : {diagnostics.PendingMigrations}");
        builder.AppendLine($"TableCount    : {diagnostics.TableCount}");
        builder.AppendLine($"MissingTables : {diagnostics.RequiredTableMissingCount}");
        builder.AppendLine($"Capability    : {diagnostics.ProviderCapabilityStatus}");
        if (diagnostics.SchemaVerification is not null)
        {
            var verification = diagnostics.SchemaVerification;
            builder.AppendLine("Schema Verification");
            builder.AppendLine($"- SchemaName     : {(string.IsNullOrWhiteSpace(verification.SchemaName) ? "default" : verification.SchemaName)}");
            builder.AppendLine($"- SchemaVersion  : {verification.CurrentSchemaVersion ?? "未应用"}");
            builder.AppendLine($"- RequiredTables : {verification.RequiredTableCount}");
            builder.AppendLine($"- MissingTables  : {verification.MissingRequiredTableCount}");
            builder.AppendLine($"- RequiredIndexes: {verification.RequiredIndexCount}");
            builder.AppendLine($"- MissingIndexes : {verification.MissingIndexCount}");
            builder.AppendLine($"- Recommendation : {verification.Recommendation}");
        }

        if (diagnostics.MissingRequiredTables.Count > 0)
        {
            builder.AppendLine("MissingRequiredTables");
            foreach (var table in diagnostics.MissingRequiredTables.Take(6))
            {
                builder.AppendLine($"- {table}");
            }
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendPostgresRelationStoreStatus(
        StringBuilder builder,
        PostgresRelationStoreDiagnostics diagnostics,
        PostgresRelationReviewProviderDiagnostics reviewDiagnostics,
        PostgresRelationReviewParityReport reviewParity,
        PostgresRelationGovernanceParityReport governanceParity,
        PostgresRelationGovernanceReadinessGateReport governanceGate,
        PostgresRelationDualWriteQualityReport dualWriteQuality,
        PostgresRelationShadowReadQualityReport shadowReadQuality,
        PostgresRelationProviderSwitchSmokeReport switchSmoke,
        PostgresRelationProviderSwitchGateReport switchGate,
        PostgresRelationRuntimeCanaryReport runtimeCanary,
        PostgresRelationScopedServiceModeSmokeReport scopedSmoke,
        PostgresRelationScopedServiceModeGateReport scopedGate,
        PostgresRelationScopedExtendedCanaryReport extendedCanary,
        PostgresRelationSelectedWorkspaceCanaryReport selectedCanary,
        PostgresRelationScopedExpansionReport scopedExpansion,
        PostgresRelationScopedObservationReport scopedObservation,
        PostgresRelationSelectedNormalWorkspaceCanaryReport selectedNormalCanary,
        PostgresRelationLimitedNormalScopeObservationReport limitedNormalObservation,
        PostgresRelationMultiNormalScopeCanaryReport multiNormalScopeCanary,
        PostgresLearningFeedbackDiagnosticsReport learningFeedbackDiagnostics,
        PostgresLearningFeedbackParityReport learningFeedbackParity,
        LearningFeedbackPostgresReadinessGateReport learningFeedbackReadinessGate,
        LearningFeedbackDualWriteSmokeReport learningFeedbackDualWriteSmoke,
        LearningFeedbackShadowReadSmokeReport learningFeedbackShadowReadSmoke,
        LearningFeedbackProviderQualityReport learningFeedbackProviderQuality,
        LearningFeedbackScopedServiceModeSmokeReport learningFeedbackScopedSmoke,
        LearningFeedbackScopedServiceModeGateReport learningFeedbackScopedGate,
        LearningFeedbackSelectedNormalScopeCanaryReport learningFeedbackSelectedNormalCanary,
        LearningFeedbackLimitedScopeObservationReport learningFeedbackLimitedObservation,
        LearningFeedbackLimitedScopeQualityReport learningFeedbackLimitedQuality,
        LearningFeedbackPostgresFreezeGateReport learningFeedbackFreezeGate,
        PostgresJobQueueDiagnosticsReport jobQueueDiagnostics,
        PostgresJobQueueParityReport jobQueueParity,
        PostgresJobQueueLeaseSmokeReport jobQueueLeaseSmoke,
        PostgresJobQueueDualWriteSmokeReport jobQueueDualWriteSmoke,
        PostgresJobQueueShadowReadSmokeReport jobQueueShadowReadSmoke,
        PostgresJobQueueProviderQualityReport jobQueueProviderQuality,
        PostgresJobQueueScopedWorkerCanaryReport jobQueueScopedWorkerCanary,
        PostgresJobQueueScopedWorkerQualityReport jobQueueScopedWorkerQuality,
        PostgresJobQueueLimitedWorkerScopeObservationReport jobQueueLimitedWorkerObservation,
        PostgresJobQueueLimitedWorkerScopeQualityReport jobQueueLimitedWorkerQuality,
        JobQueuePostgresFreezeGateReport jobQueueFreezeGate)
    {
        builder.AppendLine($"ActiveRuntime : {diagnostics.ActiveRuntimeProvider}");
        builder.AppendLine($"PostgresOn    : {diagnostics.ProviderEnabled}");
        builder.AppendLine($"UseForRuntime : {diagnostics.UseForRuntime}");
        builder.AppendLine($"Connection    : {diagnostics.ConnectionAvailable}");
        builder.AppendLine($"SchemaVersion : {diagnostics.SchemaVersion ?? "未应用"}");
        builder.AppendLine($"RelationTable : {diagnostics.RelationTableExists}");
        builder.AppendLine($"Relations     : {diagnostics.RelationCount}");
        builder.AppendLine($"Reviews       : {diagnostics.ReviewCount}");
        builder.AppendLine($"MissingIndexes: {diagnostics.MissingRequiredIndexes.Count}");
        builder.AppendLine($"Recommendation: {diagnostics.Recommendation}");
        builder.AppendLine("Review Provider");
        builder.AppendLine($"- ReviewsTable     : {reviewDiagnostics.RelationReviewsTableExists}");
        builder.AppendLine($"- DiagnosticsTable : {reviewDiagnostics.RelationDiagnosticsTableExists}");
        builder.AppendLine($"- ReviewCount      : {reviewDiagnostics.ReviewCount}");
        builder.AppendLine($"- DiagnosticsCount : {reviewDiagnostics.DiagnosticsCount}");
        builder.AppendLine($"- MissingIndexes   : {reviewDiagnostics.MissingRequiredIndexes.Count}");
        builder.AppendLine($"- Recommendation   : {reviewDiagnostics.Recommendation}");
        builder.AppendLine("Review Parity");
        builder.AppendLine($"- Recommendation   : {reviewParity.Recommendation}");
        builder.AppendLine($"- Mismatches       : {reviewParity.Mismatches.Count}");
        builder.AppendLine($"- CleanupPerformed : {reviewParity.CleanupPerformed}");
        builder.AppendLine("Governance Readiness");
        builder.AppendLine($"- EdgeParity       : {governanceGate.RelationStoreParityPassed}");
        builder.AppendLine($"- ReviewParity     : {governanceGate.RelationReviewParityPassed}");
        builder.AppendLine($"- DiagnosticsParity: {governanceGate.DiagnosticsParityPassed}");
        builder.AppendLine($"- GovernanceParity : {governanceGate.GovernanceParityPassed}");
        builder.AppendLine($"- GatePassed       : {governanceGate.Passed}");
        builder.AppendLine($"- CanDualWrite     : {governanceGate.CanDualWrite}");
        builder.AppendLine($"- CanShadowRead    : {governanceGate.CanShadowRead}");
        builder.AppendLine($"- CanRuntimeSwitch : {governanceGate.CanRuntimeSwitch}");
        builder.AppendLine($"- Recommendation   : {governanceGate.Recommendation}");
        builder.AppendLine("Governance Parity");
        builder.AppendLine($"- Recommendation   : {governanceParity.Recommendation}");
        builder.AppendLine($"- Mismatches       : {governanceParity.Mismatches.Count}");
        builder.AppendLine($"- CleanupPerformed : {governanceParity.CleanupPerformed}");
        builder.AppendLine("Dual-write Status");
        builder.AppendLine("- Enabled          : false by default");
        builder.AppendLine("- WriteTarget      : Postgres explicit eval only");
        builder.AppendLine($"- TraceCount       : {dualWriteQuality.TraceCount}");
        builder.AppendLine($"- MismatchCount    : {dualWriteQuality.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {dualWriteQuality.PostgresWriteFailureCount}");
        builder.AppendLine($"- FallbackCount    : {dualWriteQuality.FallbackCount}");
        builder.AppendLine($"- AvgDurationMs    : {dualWriteQuality.AverageDurationMs:0.00}");
        builder.AppendLine($"- Recommendation   : {dualWriteQuality.Recommendation}");
        builder.AppendLine("Shadow-read Status");
        builder.AppendLine("- Enabled          : false by default");
        builder.AppendLine($"- TraceCount       : {shadowReadQuality.TraceCount}");
        builder.AppendLine($"- MismatchCount    : {shadowReadQuality.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {shadowReadQuality.PostgresReadFailureCount}");
        builder.AppendLine($"- AvgFileSystemMs  : {shadowReadQuality.AverageFileSystemReadMs:0.00}");
        builder.AppendLine($"- AvgPostgresMs    : {shadowReadQuality.AveragePostgresReadMs:0.00}");
        builder.AppendLine($"- Recommendation   : {shadowReadQuality.Recommendation}");
        builder.AppendLine("Provider Switch Status");
        builder.AppendLine($"- CurrentMode      : {RelationGovernanceProviderMode.FileSystemPrimary}");
        builder.AppendLine($"- SmokeMode        : {(string.IsNullOrWhiteSpace(switchSmoke.Mode) ? "-" : switchSmoke.Mode)}");
        builder.AppendLine($"- AllowedWorkspace : {(string.IsNullOrWhiteSpace(switchSmoke.WorkspaceId) ? "-" : switchSmoke.WorkspaceId)}");
        builder.AppendLine($"- AllowedCollection: {(string.IsNullOrWhiteSpace(switchSmoke.CollectionId) ? "-" : switchSmoke.CollectionId)}");
        builder.AppendLine("- PrimaryProvider  : FileSystem runtime / Postgres smoke only");
        builder.AppendLine("- FallbackEnabled  : true");
        builder.AppendLine($"- ReadinessGate    : {governanceGate.Passed}");
        builder.AppendLine($"- SwitchGate       : {switchGate.Passed}");
        builder.AppendLine($"- SwitchTraces     : {switchSmoke.TraceCount}");
        builder.AppendLine($"- Recommendation   : {switchGate.Recommendation}");
        builder.AppendLine("- Rollback         : set mode FileSystemPrimary or disable RelationGovernanceProviderSwitchOptions");
        builder.AppendLine("Runtime Canary Status");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- CanaryScope      : {(string.IsNullOrWhiteSpace(runtimeCanary.CanaryScope) ? "-" : runtimeCanary.CanaryScope)}");
        builder.AppendLine($"- ProviderMode     : {(string.IsNullOrWhiteSpace(runtimeCanary.ProviderMode) ? "-" : runtimeCanary.ProviderMode)}");
        builder.AppendLine($"- GatePassed       : {runtimeCanary.GatePassed}");
        builder.AppendLine($"- PrimaryReads     : {runtimeCanary.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {runtimeCanary.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {runtimeCanary.FallbackCount}");
        builder.AppendLine($"- MismatchCount    : {runtimeCanary.MismatchCount}");
        builder.AppendLine($"- Recommendation   : {runtimeCanary.Recommendation}");
        builder.AppendLine("- Rollback         : remove canary scope from allowlist or set FileSystemPrimary");
        builder.AppendLine("Scoped Service Mode");
        builder.AppendLine("- DefaultGlobalOn  : false");
        builder.AppendLine($"- Mode             : {(string.IsNullOrWhiteSpace(scopedSmoke.ProviderMode) ? RelationGovernanceProviderMode.FileSystemPrimary : scopedSmoke.ProviderMode)}");
        builder.AppendLine($"- ActiveProvider   : {(scopedGate.Passed ? "Postgres scoped allowlist / FileSystem elsewhere" : "FileSystemRelationStore")}");
        builder.AppendLine($"- Allowlist        : {(string.IsNullOrWhiteSpace(scopedSmoke.WorkspaceId) ? "-" : scopedSmoke.WorkspaceId)} / {(string.IsNullOrWhiteSpace(scopedSmoke.CollectionId) ? "-" : scopedSmoke.CollectionId)}");
        builder.AppendLine("- FallbackEnabled  : true");
        builder.AppendLine("- ComparisonTrace  : true");
        builder.AppendLine($"- GatePassed       : {scopedGate.Passed}");
        builder.AppendLine($"- CanaryPassed     : {scopedGate.RuntimeCanaryPassed}");
        builder.AppendLine($"- NonAllowlistedFS : {scopedGate.NonAllowlistedScopeRemainsFileSystem}");
        builder.AppendLine($"- MismatchCount    : {scopedGate.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {scopedGate.PostgresFailureCount}");
        builder.AppendLine($"- Recommendation   : {scopedGate.Recommendation}");
        builder.AppendLine("- Rollback         : remove scoped allowlist or set RelationGovernanceProviderSwitchOptions.Enabled=false");
        builder.AppendLine("Extended Canary Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- CanaryScope      : {(string.IsNullOrWhiteSpace(extendedCanary.CanaryScope) ? "-" : extendedCanary.CanaryScope)}");
        builder.AppendLine($"- ProviderMode     : {(string.IsNullOrWhiteSpace(extendedCanary.ProviderMode) ? "-" : extendedCanary.ProviderMode)}");
        builder.AppendLine($"- OperationCount   : {extendedCanary.OperationCount}");
        builder.AppendLine($"- MismatchCount    : {extendedCanary.MismatchCount}");
        builder.AppendLine($"- FallbackCount    : {extendedCanary.FileSystemFallbackCount}");
        builder.AppendLine($"- GraphPreview     : {extendedCanary.GraphExpansionPreviewParityPassed}");
        builder.AppendLine($"- ReviewLifecycle  : {extendedCanary.ReviewLifecycleParityPassed}");
        builder.AppendLine($"- Diagnostics      : {extendedCanary.DiagnosticsParityPassed}");
        builder.AppendLine($"- ReplacementChain : {extendedCanary.ReplacementChainParityPassed}");
        builder.AppendLine($"- Recommendation   : {extendedCanary.Recommendation}");
        builder.AppendLine("Selected Workspace Canary Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- Workspace        : {(string.IsNullOrWhiteSpace(selectedCanary.WorkspaceId) ? "-" : selectedCanary.WorkspaceId)}");
        builder.AppendLine($"- Collection       : {(string.IsNullOrWhiteSpace(selectedCanary.CollectionId) ? "-" : selectedCanary.CollectionId)}");
        builder.AppendLine($"- Mode             : {(string.IsNullOrWhiteSpace(selectedCanary.ProviderMode) ? "-" : selectedCanary.ProviderMode)}");
        builder.AppendLine($"- OperationCount   : {selectedCanary.OperationCount}");
        builder.AppendLine($"- MismatchCount    : {selectedCanary.MismatchCount}");
        builder.AppendLine($"- FallbackCount    : {selectedCanary.FileSystemFallbackCount}");
        builder.AppendLine($"- AvgPostgresRead  : {selectedCanary.AveragePostgresReadMs:0.00}ms");
        builder.AppendLine($"- AvgPostgresWrite : {selectedCanary.AveragePostgresWriteMs:0.00}ms");
        builder.AppendLine($"- Recommendation   : {selectedCanary.Recommendation}");
        builder.AppendLine($"- Rollback         : {(string.IsNullOrWhiteSpace(selectedCanary.RollbackInstruction) ? "remove selected scope allowlist or set FileSystemPrimary" : selectedCanary.RollbackInstruction)}");
        builder.AppendLine("Scoped Expansion Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- ScopeCount       : {scopedExpansion.ScopeCount}");
        builder.AppendLine($"- AllowlistedScopes: {scopedExpansion.AllowlistedScopeCount}");
        builder.AppendLine($"- NonAllowlistedFS : {scopedExpansion.NonAllowlistedScopeChecked}");
        builder.AppendLine($"- OperationCount   : {scopedExpansion.OperationCount}");
        builder.AppendLine($"- MismatchCount    : {scopedExpansion.MismatchCount}");
        builder.AppendLine($"- FallbackCount    : {scopedExpansion.FallbackCount}");
        builder.AppendLine($"- PostgresFailures : {scopedExpansion.PostgresFailureCount}");
        builder.AppendLine($"- AvgPostgresRead  : {scopedExpansion.AveragePostgresReadMs:0.00}ms");
        builder.AppendLine($"- AvgPostgresWrite : {scopedExpansion.AveragePostgresWriteMs:0.00}ms");
        builder.AppendLine($"- Recommendation   : {scopedExpansion.Recommendation}");
        foreach (var scope in scopedExpansion.PerScopeStatus.Take(5))
        {
            builder.AppendLine($"  - {scope.ScopeName}: mode={scope.Mode} stage={scope.RolloutStage} mismatch={scope.MismatchCount} fallback={scope.FallbackCount} failure={scope.PostgresFailureCount} next={scope.Recommendation}");
        }

        builder.AppendLine("- Rollback         : disable affected scope rule or set RelationGovernanceProviderSwitchOptions.Enabled=false");
        builder.AppendLine("Scoped Observation Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- WindowMinutes    : {scopedObservation.ObservationWindowMinutes}");
        builder.AppendLine($"- ScopeCount       : {scopedObservation.ScopeCount}");
        builder.AppendLine($"- OperationCount   : {scopedObservation.OperationCount}");
        builder.AppendLine($"- PrimaryReads     : {scopedObservation.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {scopedObservation.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {scopedObservation.FileSystemFallbackCount}");
        builder.AppendLine($"- ComparisonTraces : {scopedObservation.ComparisonTraceCount}");
        builder.AppendLine($"- MismatchCount    : {scopedObservation.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {scopedObservation.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeaks       : {scopedObservation.NonAllowlistedScopeLeakCount}");
        builder.AppendLine($"- AvgPostgresRead  : {scopedObservation.AveragePostgresReadMs:0.00}ms");
        builder.AppendLine($"- P95PostgresRead  : {scopedObservation.P95PostgresReadMs:0.00}ms");
        builder.AppendLine($"- AvgPostgresWrite : {scopedObservation.AveragePostgresWriteMs:0.00}ms");
        builder.AppendLine($"- P95PostgresWrite : {scopedObservation.P95PostgresWriteMs:0.00}ms");
        builder.AppendLine($"- Recommendation   : {scopedObservation.Recommendation}");
        foreach (var scope in scopedObservation.PerScopeStatus.Take(5))
        {
            builder.AppendLine($"  - {scope.ScopeName}: mode={scope.Mode} mismatch={scope.MismatchCount} fallback={scope.FallbackCount} failure={scope.PostgresFailureCount} next={scope.Recommendation}");
        }

        builder.AppendLine($"- Rollback         : {(string.IsNullOrWhiteSpace(scopedObservation.RollbackInstruction) ? "disable affected scope rule or set FileSystemPrimary" : scopedObservation.RollbackInstruction)}");
        builder.AppendLine("Selected Normal Workspace Canary Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- Workspace        : {(string.IsNullOrWhiteSpace(selectedNormalCanary.WorkspaceId) ? "-" : selectedNormalCanary.WorkspaceId)}");
        builder.AppendLine($"- Collection       : {(string.IsNullOrWhiteSpace(selectedNormalCanary.CollectionId) ? "-" : selectedNormalCanary.CollectionId)}");
        builder.AppendLine($"- Mode             : {(string.IsNullOrWhiteSpace(selectedNormalCanary.ProviderMode) ? "-" : selectedNormalCanary.ProviderMode)}");
        builder.AppendLine($"- OperationCount   : {selectedNormalCanary.OperationCount}");
        builder.AppendLine($"- PrimaryReads     : {selectedNormalCanary.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {selectedNormalCanary.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {selectedNormalCanary.FileSystemFallbackCount}");
        builder.AppendLine($"- ComparisonTraces : {selectedNormalCanary.ComparisonTraceCount}");
        builder.AppendLine($"- MismatchCount    : {selectedNormalCanary.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {selectedNormalCanary.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeaks       : {selectedNormalCanary.ScopeLeakCount}");
        builder.AppendLine($"- AvgPostgresRead  : {selectedNormalCanary.AveragePostgresReadMs:0.00}ms");
        builder.AppendLine($"- P95PostgresRead  : {selectedNormalCanary.P95PostgresReadMs:0.00}ms");
        builder.AppendLine($"- AvgPostgresWrite : {selectedNormalCanary.AveragePostgresWriteMs:0.00}ms");
        builder.AppendLine($"- P95PostgresWrite : {selectedNormalCanary.P95PostgresWriteMs:0.00}ms");
        builder.AppendLine($"- GraphPreview     : {selectedNormalCanary.GraphExpansionPreviewParityPassed}");
        builder.AppendLine($"- ReviewLifecycle  : {selectedNormalCanary.ReviewLifecycleParityPassed}");
        builder.AppendLine($"- Diagnostics      : {selectedNormalCanary.DiagnosticsParityPassed}");
        builder.AppendLine($"- ReplacementChain : {selectedNormalCanary.ReplacementChainParityPassed}");
        builder.AppendLine($"- Recommendation   : {selectedNormalCanary.Recommendation}");
        builder.AppendLine($"- Rollback         : {(string.IsNullOrWhiteSpace(selectedNormalCanary.RollbackInstruction) ? "remove selected normal scope allowlist or set FileSystemPrimary" : selectedNormalCanary.RollbackInstruction)}");
        builder.AppendLine("Limited Normal Scope Observation Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- Workspace        : {(string.IsNullOrWhiteSpace(limitedNormalObservation.WorkspaceId) ? "-" : limitedNormalObservation.WorkspaceId)}");
        builder.AppendLine($"- Collection       : {(string.IsNullOrWhiteSpace(limitedNormalObservation.CollectionId) ? "-" : limitedNormalObservation.CollectionId)}");
        builder.AppendLine($"- WindowMinutes    : {limitedNormalObservation.ObservationWindowMinutes}");
        builder.AppendLine($"- OperationCount   : {limitedNormalObservation.OperationCount}");
        builder.AppendLine($"- PrimaryReads     : {limitedNormalObservation.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {limitedNormalObservation.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {limitedNormalObservation.FileSystemFallbackCount}");
        builder.AppendLine($"- FallbackRate     : {limitedNormalObservation.FallbackRate:0.####}");
        builder.AppendLine($"- ComparisonTraces : {limitedNormalObservation.ComparisonTraceCount}");
        builder.AppendLine($"- MismatchCount    : {limitedNormalObservation.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {limitedNormalObservation.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeaks       : {limitedNormalObservation.ScopeLeakCount}");
        builder.AppendLine($"- AvgPostgresRead  : {limitedNormalObservation.AveragePostgresReadMs:0.00}ms");
        builder.AppendLine($"- P95PostgresRead  : {limitedNormalObservation.P95PostgresReadMs:0.00}ms");
        builder.AppendLine($"- AvgPostgresWrite : {limitedNormalObservation.AveragePostgresWriteMs:0.00}ms");
        builder.AppendLine($"- P95PostgresWrite : {limitedNormalObservation.P95PostgresWriteMs:0.00}ms");
        builder.AppendLine($"- GraphPreview     : {limitedNormalObservation.GraphExpansionPreviewParityPassed}");
        builder.AppendLine($"- ReviewLifecycle  : {limitedNormalObservation.ReviewLifecycleParityPassed}");
        builder.AppendLine($"- Diagnostics      : {limitedNormalObservation.DiagnosticsParityPassed}");
        builder.AppendLine($"- ReplacementChain : {limitedNormalObservation.ReplacementChainParityPassed}");
        builder.AppendLine($"- Recommendation   : {limitedNormalObservation.Recommendation}");
        builder.AppendLine($"- Rollback         : {(string.IsNullOrWhiteSpace(limitedNormalObservation.RollbackInstruction) ? "remove limited normal scope allowlist or set FileSystemPrimary" : limitedNormalObservation.RollbackInstruction)}");
        builder.AppendLine("Multi Normal Scope Canary Summary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- ScopeCount       : {multiNormalScopeCanary.ScopeCount}");
        builder.AppendLine($"- EnabledScopes    : {multiNormalScopeCanary.EnabledScopeCount}");
        builder.AppendLine($"- OperationCount   : {multiNormalScopeCanary.OperationCount}");
        builder.AppendLine($"- PrimaryReads     : {multiNormalScopeCanary.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {multiNormalScopeCanary.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {multiNormalScopeCanary.FileSystemFallbackCount}");
        builder.AppendLine($"- ComparisonTraces : {multiNormalScopeCanary.ComparisonTraceCount}");
        builder.AppendLine($"- MismatchCount    : {multiNormalScopeCanary.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {multiNormalScopeCanary.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeaks       : {multiNormalScopeCanary.ScopeLeakCount}");
        builder.AppendLine($"- NonAllowlistedOk : {multiNormalScopeCanary.NonAllowlistedScopeChecked}");
        builder.AppendLine($"- AvgPostgresRead  : {multiNormalScopeCanary.AveragePostgresReadMs:0.00}ms");
        builder.AppendLine($"- P95PostgresRead  : {multiNormalScopeCanary.P95PostgresReadMs:0.00}ms");
        builder.AppendLine($"- AvgPostgresWrite : {multiNormalScopeCanary.AveragePostgresWriteMs:0.00}ms");
        builder.AppendLine($"- P95PostgresWrite : {multiNormalScopeCanary.P95PostgresWriteMs:0.00}ms");
        builder.AppendLine($"- GraphPreview     : {multiNormalScopeCanary.GraphExpansionPreviewParityPassed}");
        builder.AppendLine($"- ReviewLifecycle  : {multiNormalScopeCanary.ReviewLifecycleParityPassed}");
        builder.AppendLine($"- Diagnostics      : {multiNormalScopeCanary.DiagnosticsParityPassed}");
        builder.AppendLine($"- ReplacementChain : {multiNormalScopeCanary.ReplacementChainParityPassed}");
        builder.AppendLine($"- Recommendation   : {multiNormalScopeCanary.Recommendation}");
        builder.AppendLine($"- Rollback         : {(string.IsNullOrWhiteSpace(multiNormalScopeCanary.RollbackInstruction) ? "remove affected multi-normal scope rule or set FileSystemPrimary" : multiNormalScopeCanary.RollbackInstruction)}");
        foreach (var scope in multiNormalScopeCanary.PerScopeStatus.Take(4))
        {
            builder.AppendLine($"  - {scope.ScopeName}: ops={scope.OperationCount}, mismatch={scope.MismatchCount}, failures={scope.PostgresFailureCount}, leaks={scope.ScopeLeakCount}, rec={scope.Recommendation}");
        }

        builder.AppendLine("Warning       : scoped mode 仅限显式 allowlist；未配置全局 default on。");
        builder.AppendLine("Learning Feedback Provider Status");
        builder.AppendLine("- RuntimeProvider  : FileSystem source of truth");
        builder.AppendLine($"- ProviderEnabled  : {learningFeedbackDiagnostics.ProviderEnabled}");
        builder.AppendLine($"- Connection       : {learningFeedbackDiagnostics.ConnectionAvailable}");
        builder.AppendLine($"- SchemaVersion    : {learningFeedbackDiagnostics.SchemaVersion}");
        builder.AppendLine($"- FeedbackTable    : {learningFeedbackDiagnostics.FeedbackTableExists}");
        builder.AppendLine($"- ReviewTable      : {learningFeedbackDiagnostics.ReviewTableExists}");
        builder.AppendLine($"- CandidateTable   : {learningFeedbackDiagnostics.FeatureCandidateTableExists}");
        builder.AppendLine($"- RequiredIndexes  : {learningFeedbackDiagnostics.RequiredIndexesExist}");
        builder.AppendLine($"- FeedbackCount    : {learningFeedbackDiagnostics.FeedbackCount}");
        builder.AppendLine($"- ReviewCount      : {learningFeedbackDiagnostics.ReviewCount}");
        builder.AppendLine($"- CandidateCount   : {learningFeedbackDiagnostics.FeatureCandidateCount}");
        builder.AppendLine($"- UseForRuntime    : {learningFeedbackDiagnostics.UseForRuntime}");
        builder.AppendLine($"- Status           : {learningFeedbackDiagnostics.Status}");
        builder.AppendLine("Learning Feedback Parity");
        builder.AppendLine($"- Recommendation   : {learningFeedbackParity.Recommendation}");
        builder.AppendLine($"- FeedbackParity   : {learningFeedbackParity.FeedbackParityPassed}");
        builder.AppendLine($"- ReviewParity     : {learningFeedbackParity.ReviewParityPassed}");
        builder.AppendLine($"- CandidateParity  : {learningFeedbackParity.FeatureCandidateParityPassed}");
        builder.AppendLine($"- MetadataRoundtrip: {learningFeedbackParity.MetadataRoundtripPassed}");
        builder.AppendLine($"- DuplicateUpsert  : {learningFeedbackParity.DuplicateFeedbackUpsertPassed}");
        builder.AppendLine($"- Mismatches       : {learningFeedbackParity.Mismatches.Count}");
        builder.AppendLine($"- CleanupPerformed : {learningFeedbackParity.CleanupPerformed}");
        builder.AppendLine("Learning Feedback Readiness / Smoke");
        builder.AppendLine($"- GatePassed       : {learningFeedbackReadinessGate.GatePassed}");
        builder.AppendLine($"- GateRecommendation: {learningFeedbackReadinessGate.Recommendation}");
        builder.AppendLine($"- DualWriteRec     : {learningFeedbackDualWriteSmoke.Recommendation}");
        builder.AppendLine($"- DualWriteMismatch: {learningFeedbackDualWriteSmoke.MismatchCount}");
        builder.AppendLine($"- DualWriteFailures: {learningFeedbackDualWriteSmoke.PostgresFailureCount}");
        builder.AppendLine($"- ShadowReadRec    : {learningFeedbackShadowReadSmoke.Recommendation}");
        builder.AppendLine($"- ShadowMismatch   : {learningFeedbackShadowReadSmoke.MismatchCount}");
        builder.AppendLine($"- ShadowFailures   : {learningFeedbackShadowReadSmoke.PostgresFailureCount}");
        builder.AppendLine($"- QualityTraceCount: {learningFeedbackProviderQuality.TraceCount}");
        builder.AppendLine($"- QualityRec       : {learningFeedbackProviderQuality.Recommendation}");
        builder.AppendLine("- RuntimeProvider  : still FileSystem");
        builder.AppendLine("Learning Feedback Scoped Service Mode");
        builder.AppendLine($"- CurrentMode      : {(string.IsNullOrWhiteSpace(learningFeedbackScopedSmoke.ProviderMode) ? LearningFeedbackProviderMode.FileSystemPrimary : learningFeedbackScopedSmoke.ProviderMode)}");
        builder.AppendLine($"- Allowlist        : {(string.IsNullOrWhiteSpace(learningFeedbackScopedSmoke.WorkspaceId) ? "-" : learningFeedbackScopedSmoke.WorkspaceId)} / {(string.IsNullOrWhiteSpace(learningFeedbackScopedSmoke.CollectionId) ? "-" : learningFeedbackScopedSmoke.CollectionId)}");
        builder.AppendLine($"- PrimaryProvider  : {(learningFeedbackScopedGate.Passed ? "Postgres scoped allowlist / FileSystem elsewhere" : "FileSystem")}");
        builder.AppendLine("- FallbackEnabled  : true");
        builder.AppendLine("- ComparisonTrace  : true");
        builder.AppendLine($"- ScopedGatePassed : {learningFeedbackScopedGate.Passed}");
        builder.AppendLine($"- NonAllowlistedFS : {learningFeedbackScopedGate.NonAllowlistedScopeRemainsFileSystem}");
        builder.AppendLine($"- MismatchCount    : {learningFeedbackScopedGate.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {learningFeedbackScopedGate.PostgresFailureCount}");
        builder.AppendLine($"- FallbackCount    : {learningFeedbackScopedGate.FallbackCount}");
        builder.AppendLine($"- ExportParity     : {learningFeedbackScopedGate.ExportProjectionParityPassed}");
        builder.AppendLine($"- SummaryParity    : {learningFeedbackScopedGate.SummaryParityPassed}");
        builder.AppendLine($"- Recommendation   : {learningFeedbackScopedGate.Recommendation}");
        builder.AppendLine($"- Rollback         : {learningFeedbackScopedSmoke.RollbackInstruction}");
        builder.AppendLine("Learning Feedback Selected Normal Scope Canary");
        builder.AppendLine($"- Workspace        : {(string.IsNullOrWhiteSpace(learningFeedbackSelectedNormalCanary.WorkspaceId) ? "-" : learningFeedbackSelectedNormalCanary.WorkspaceId)}");
        builder.AppendLine($"- Collection       : {(string.IsNullOrWhiteSpace(learningFeedbackSelectedNormalCanary.CollectionId) ? "-" : learningFeedbackSelectedNormalCanary.CollectionId)}");
        builder.AppendLine($"- ProviderMode     : {(string.IsNullOrWhiteSpace(learningFeedbackSelectedNormalCanary.ProviderMode) ? "-" : learningFeedbackSelectedNormalCanary.ProviderMode)}");
        builder.AppendLine($"- OperationCount   : {learningFeedbackSelectedNormalCanary.OperationCount}");
        builder.AppendLine($"- PrimaryReads     : {learningFeedbackSelectedNormalCanary.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {learningFeedbackSelectedNormalCanary.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {learningFeedbackSelectedNormalCanary.FileSystemFallbackCount}");
        builder.AppendLine($"- MismatchCount    : {learningFeedbackSelectedNormalCanary.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {learningFeedbackSelectedNormalCanary.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeaks       : {learningFeedbackSelectedNormalCanary.ScopeLeakCount}");
        builder.AppendLine($"- ExportParity     : {learningFeedbackSelectedNormalCanary.ExportProjectionParityPassed}");
        builder.AppendLine($"- SummaryParity    : {learningFeedbackSelectedNormalCanary.SummaryParityPassed}");
        builder.AppendLine($"- ReviewSummary    : {learningFeedbackSelectedNormalCanary.ReviewSummaryParityPassed}");
        builder.AppendLine($"- CandidateParity  : {learningFeedbackSelectedNormalCanary.FeatureCandidateParityPassed}");
        builder.AppendLine($"- Recommendation   : {learningFeedbackSelectedNormalCanary.Recommendation}");
        builder.AppendLine($"- Rollback         : {learningFeedbackSelectedNormalCanary.RollbackInstruction}");
        builder.AppendLine("Learning Feedback Limited Scope Observation");
        builder.AppendLine($"- Workspace        : {(string.IsNullOrWhiteSpace(learningFeedbackLimitedObservation.WorkspaceId) ? "-" : learningFeedbackLimitedObservation.WorkspaceId)}");
        builder.AppendLine($"- Collection       : {(string.IsNullOrWhiteSpace(learningFeedbackLimitedObservation.CollectionId) ? "-" : learningFeedbackLimitedObservation.CollectionId)}");
        builder.AppendLine($"- WindowMinutes    : {learningFeedbackLimitedObservation.ObservationWindowMinutes}");
        builder.AppendLine($"- OperationCount   : {learningFeedbackLimitedObservation.OperationCount}");
        builder.AppendLine($"- PrimaryReads     : {learningFeedbackLimitedObservation.PostgresPrimaryReadCount}");
        builder.AppendLine($"- PrimaryWrites    : {learningFeedbackLimitedObservation.PostgresPrimaryWriteCount}");
        builder.AppendLine($"- FallbackCount    : {learningFeedbackLimitedObservation.FileSystemFallbackCount}");
        builder.AppendLine($"- FallbackRate     : {learningFeedbackLimitedObservation.FallbackRate:0.####}");
        builder.AppendLine($"- MismatchCount    : {learningFeedbackLimitedObservation.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {learningFeedbackLimitedObservation.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeaks       : {learningFeedbackLimitedObservation.ScopeLeakCount}");
        builder.AppendLine($"- TrainableLeaks   : {learningFeedbackLimitedObservation.TrainableCandidateLeakCount}");
        builder.AppendLine($"- SmokeExcluded    : {learningFeedbackLimitedObservation.SmokeCandidateExcludedCount}");
        builder.AppendLine($"- ExportParity     : {learningFeedbackLimitedObservation.ExportProjectionParityPassed}");
        builder.AppendLine($"- SummaryParity    : {learningFeedbackLimitedObservation.SummaryParityPassed}");
        builder.AppendLine($"- ReviewSummary    : {learningFeedbackLimitedObservation.ReviewSummaryParityPassed}");
        builder.AppendLine($"- CandidateParity  : {learningFeedbackLimitedObservation.FeatureCandidateParityPassed}");
        builder.AppendLine($"- Recommendation   : {learningFeedbackLimitedObservation.Recommendation}");
        builder.AppendLine("Learning Feedback Freeze Status");
        builder.AppendLine($"- Passed           : {learningFeedbackFreezeGate.Passed}");
        builder.AppendLine($"- Status           : {learningFeedbackFreezeGate.LearningFeedbackPostgres}");
        builder.AppendLine($"- DefaultProvider  : {learningFeedbackFreezeGate.DefaultProvider}");
        builder.AppendLine($"- AllowedMode      : {learningFeedbackFreezeGate.AllowedMode}");
        builder.AppendLine($"- LimitedQuality   : {learningFeedbackLimitedQuality.Passed} / {learningFeedbackLimitedQuality.Recommendation}");
        builder.AppendLine($"- Required         : {string.Join(", ", learningFeedbackFreezeGate.Required)}");
        builder.AppendLine($"- Forbidden        : {string.Join(", ", learningFeedbackFreezeGate.Forbidden)}");
        builder.AppendLine($"- Recommendation   : {learningFeedbackFreezeGate.Recommendation}");
        builder.AppendLine("Job Queue Provider Status");
        builder.AppendLine("- RuntimeProvider  : unchanged / FileSystem or InMemory source of truth");
        builder.AppendLine($"- ProviderEnabled  : {jobQueueDiagnostics.ProviderEnabled}");
        builder.AppendLine($"- Connection       : {jobQueueDiagnostics.ConnectionAvailable}");
        builder.AppendLine($"- SchemaVersion    : {jobQueueDiagnostics.SchemaVersion}");
        builder.AppendLine($"- JobTable         : {jobQueueDiagnostics.JobTableExists}");
        builder.AppendLine($"- RequiredIndexes  : {jobQueueDiagnostics.RequiredIndexesExist}");
        builder.AppendLine($"- PendingCount     : {jobQueueDiagnostics.PendingCount}");
        builder.AppendLine($"- RunningCount     : {jobQueueDiagnostics.RunningCount}");
        builder.AppendLine($"- FailedCount      : {jobQueueDiagnostics.FailedCount}");
        builder.AppendLine($"- DeadLetterCount  : {jobQueueDiagnostics.DeadLetterCount}");
        builder.AppendLine($"- StaleLeaseCount  : {jobQueueDiagnostics.StaleLeaseCount}");
        builder.AppendLine($"- UseForRuntime    : {jobQueueDiagnostics.UseForRuntime}");
        builder.AppendLine($"- DiagnosticsRec   : {jobQueueDiagnostics.Recommendation}");
        builder.AppendLine($"- ParityRec        : {jobQueueParity.Recommendation}");
        builder.AppendLine($"- ParityMismatch   : {jobQueueParity.MismatchCount}");
        builder.AppendLine($"- LeaseSmokeRec    : {jobQueueLeaseSmoke.Recommendation}");
        builder.AppendLine($"- LeaseAcquire     : {jobQueueLeaseSmoke.LeaseAcquireCount}");
        builder.AppendLine($"- LeaseConflict    : {jobQueueLeaseSmoke.LeaseConflictCount}");
        builder.AppendLine($"- ExpiredReacquire : {jobQueueLeaseSmoke.LeaseExpiredReacquireCount}");
        builder.AppendLine($"- RetryTransition  : {jobQueueLeaseSmoke.RetryTransitionPassed}");
        builder.AppendLine($"- DeadLetter       : {jobQueueLeaseSmoke.DeadLetterTransitionPassed}");
        builder.AppendLine("Job Queue Dual-write / Shadow-read Status");
        builder.AppendLine("- DualWriteEnabled : false by default / explicit eval only");
        builder.AppendLine($"- DualWriteRec     : {jobQueueDualWriteSmoke.Recommendation}");
        builder.AppendLine($"- DualWriteTraces  : {jobQueueDualWriteSmoke.TraceCount}");
        builder.AppendLine($"- DualWriteMismatch: {jobQueueDualWriteSmoke.MismatchCount}");
        builder.AppendLine($"- DualWriteFailures: {jobQueueDualWriteSmoke.PostgresFailureCount}");
        builder.AppendLine($"- DualWriteFallback: {jobQueueDualWriteSmoke.FallbackCount}");
        builder.AppendLine("- ShadowReadEnabled: false by default / explicit eval only");
        builder.AppendLine($"- ShadowReadRec    : {jobQueueShadowReadSmoke.Recommendation}");
        builder.AppendLine($"- ShadowReadTraces : {jobQueueShadowReadSmoke.TraceCount}");
        builder.AppendLine($"- ShadowMismatch   : {jobQueueShadowReadSmoke.MismatchCount}");
        builder.AppendLine($"- ShadowFailures   : {jobQueueShadowReadSmoke.PostgresFailureCount}");
        builder.AppendLine($"- ShadowFallback   : {jobQueueShadowReadSmoke.FallbackCount}");
        builder.AppendLine($"- QualityRec       : {jobQueueProviderQuality.Recommendation}");
        builder.AppendLine($"- QualityTraces    : {jobQueueProviderQuality.TraceCount}");
        builder.AppendLine($"- LeaseParity      : {jobQueueProviderQuality.LeaseParityPassed}");
        builder.AppendLine($"- RetryParity      : {jobQueueProviderQuality.RetryParityPassed}");
        builder.AppendLine($"- DeadLetterParity : {jobQueueProviderQuality.DeadLetterParityPassed}");
        builder.AppendLine($"- CountParity      : {jobQueueProviderQuality.CountParityPassed}");
        builder.AppendLine("- RuntimeWorker    : unchanged");
        builder.AppendLine("Job Queue Scoped Worker Canary");
        builder.AppendLine("- Enabled          : false by default / explicit eval only");
        builder.AppendLine($"- SelectedScope    : {(string.IsNullOrWhiteSpace(jobQueueScopedWorkerCanary.WorkspaceId) ? "-" : jobQueueScopedWorkerCanary.WorkspaceId)} / {(string.IsNullOrWhiteSpace(jobQueueScopedWorkerCanary.CollectionId) ? "-" : jobQueueScopedWorkerCanary.CollectionId)}");
        builder.AppendLine($"- ProviderMode     : {jobQueueScopedWorkerCanary.ProviderMode}");
        builder.AppendLine($"- JobCount         : {jobQueueScopedWorkerCanary.JobCount}");
        builder.AppendLine($"- Completed        : {jobQueueScopedWorkerCanary.CompletedCount}");
        builder.AppendLine($"- Retried          : {jobQueueScopedWorkerCanary.RetriedCount}");
        builder.AppendLine($"- DeadLetter       : {jobQueueScopedWorkerCanary.DeadLetterCount}");
        builder.AppendLine($"- LeaseConflicts   : {jobQueueScopedWorkerCanary.LeaseConflictCount}");
        builder.AppendLine($"- Heartbeats       : {jobQueueScopedWorkerCanary.HeartbeatCount}");
        builder.AppendLine($"- MismatchCount    : {jobQueueScopedWorkerCanary.MismatchCount}");
        builder.AppendLine($"- PostgresFailures : {jobQueueScopedWorkerCanary.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeakCount   : {jobQueueScopedWorkerCanary.ScopeLeakCount}");
        builder.AppendLine($"- NonSelectedFS    : {jobQueueScopedWorkerCanary.NonSelectedScopeRemainsFileSystem}");
        builder.AppendLine($"- Recommendation   : {jobQueueScopedWorkerCanary.Recommendation}");
        builder.AppendLine("Job Queue Scoped Worker Quality");
        builder.AppendLine($"- Passed           : {jobQueueScopedWorkerQuality.Passed}");
        builder.AppendLine($"- Recommendation   : {jobQueueScopedWorkerQuality.Recommendation}");
        builder.AppendLine($"- RuntimeWorker    : {(jobQueueScopedWorkerQuality.RuntimeWorkerGlobalProviderUnchanged ? "global provider unchanged" : "changed")}");
        builder.AppendLine("Job Queue Limited Worker Scope Observation");
        builder.AppendLine($"- SelectedScope    : {(string.IsNullOrWhiteSpace(jobQueueLimitedWorkerObservation.WorkspaceId) ? "-" : jobQueueLimitedWorkerObservation.WorkspaceId)} / {(string.IsNullOrWhiteSpace(jobQueueLimitedWorkerObservation.CollectionId) ? "-" : jobQueueLimitedWorkerObservation.CollectionId)}");
        builder.AppendLine($"- WindowSeconds    : {jobQueueLimitedWorkerObservation.ObservationWindowSeconds}");
        builder.AppendLine($"- JobCount         : {jobQueueLimitedWorkerObservation.JobCount}");
        builder.AppendLine($"- Completed        : {jobQueueLimitedWorkerObservation.CompletedCount}");
        builder.AppendLine($"- Retried          : {jobQueueLimitedWorkerObservation.RetriedCount}");
        builder.AppendLine($"- DeadLetter       : {jobQueueLimitedWorkerObservation.DeadLetterCount}");
        builder.AppendLine($"- Cancelled        : {jobQueueLimitedWorkerObservation.CancelledCount}");
        builder.AppendLine($"- LeaseConflict    : {jobQueueLimitedWorkerObservation.LeaseConflictCount}");
        builder.AppendLine($"- ExpiredReacquire : {jobQueueLimitedWorkerObservation.LeaseExpiredReacquireCount}");
        builder.AppendLine($"- Heartbeats       : {jobQueueLimitedWorkerObservation.HeartbeatCount}");
        builder.AppendLine($"- DuplicateExec    : {jobQueueLimitedWorkerObservation.DuplicateExecutionCount}");
        builder.AppendLine($"- LeaseViolations  : {jobQueueLimitedWorkerObservation.LeaseViolationCount}");
        builder.AppendLine($"- PostgresFailures : {jobQueueLimitedWorkerObservation.PostgresFailureCount}");
        builder.AppendLine($"- ScopeLeakCount   : {jobQueueLimitedWorkerObservation.ScopeLeakCount}");
        builder.AppendLine($"- NonSelectedFS    : {jobQueueLimitedWorkerObservation.NonSelectedScopeRemainsFileSystem}");
        builder.AppendLine($"- Recommendation   : {jobQueueLimitedWorkerObservation.Recommendation}");
        builder.AppendLine("Job Queue Limited Worker Scope Quality");
        builder.AppendLine($"- Passed           : {jobQueueLimitedWorkerQuality.Passed}");
        builder.AppendLine($"- Recommendation   : {jobQueueLimitedWorkerQuality.Recommendation}");
        builder.AppendLine($"- RuntimeWorker    : {(jobQueueLimitedWorkerQuality.RuntimeWorkerGlobalProviderUnchanged ? "global provider unchanged" : "changed")}");
        builder.AppendLine("Job Queue Freeze Status");
        builder.AppendLine($"- Passed           : {jobQueueFreezeGate.Passed}");
        builder.AppendLine($"- FreezeState      : {jobQueueFreezeGate.JobQueuePostgres}");
        builder.AppendLine($"- DefaultProvider  : {jobQueueFreezeGate.DefaultProvider}");
        builder.AppendLine($"- AllowedMode      : {jobQueueFreezeGate.AllowedMode}");
        builder.AppendLine($"- Required         : {string.Join(", ", jobQueueFreezeGate.Required)}");
        builder.AppendLine($"- Forbidden        : {string.Join(", ", jobQueueFreezeGate.Forbidden)}");
        builder.AppendLine($"- LastQuality      : {jobQueueLimitedWorkerQuality.Recommendation}");
        builder.AppendLine($"- Rollback         : keep ExistingProvider / remove scoped allowlist");
        builder.AppendLine($"- Recommendation   : {jobQueueFreezeGate.Recommendation}");

        var allDiagnostics = diagnostics.Diagnostics
            .Concat(reviewDiagnostics.Diagnostics)
            .Concat(reviewParity.Diagnostics)
            .Concat(governanceParity.Diagnostics)
            .Concat(governanceGate.Diagnostics)
            .Concat(governanceGate.BlockedReasons)
            .Concat(dualWriteQuality.Diagnostics)
            .Concat(shadowReadQuality.Diagnostics)
            .Concat(switchSmoke.Diagnostics)
            .Concat(switchGate.Diagnostics)
            .Concat(switchGate.BlockedReasons)
            .Concat(runtimeCanary.Diagnostics)
            .Concat(runtimeCanary.BlockedReasons)
            .Concat(scopedSmoke.Diagnostics)
            .Concat(scopedSmoke.BlockedReasons)
            .Concat(scopedGate.Diagnostics)
            .Concat(scopedGate.BlockedReasons)
            .Concat(extendedCanary.Diagnostics)
            .Concat(extendedCanary.BlockedReasons)
            .Concat(selectedCanary.Diagnostics)
            .Concat(selectedCanary.BlockedReasons)
            .Concat(scopedExpansion.Diagnostics)
            .Concat(scopedExpansion.BlockedReasons)
            .Concat(scopedObservation.Diagnostics)
            .Concat(scopedObservation.BlockedReasons)
            .Concat(selectedNormalCanary.Diagnostics)
            .Concat(selectedNormalCanary.BlockedReasons)
            .Concat(limitedNormalObservation.Diagnostics)
            .Concat(limitedNormalObservation.BlockedReasons)
            .Concat(multiNormalScopeCanary.Diagnostics)
            .Concat(multiNormalScopeCanary.BlockedReasons)
            .Concat(learningFeedbackDiagnostics.Diagnostics)
            .Concat(learningFeedbackParity.Diagnostics)
            .Concat(learningFeedbackParity.Mismatches)
            .Concat(learningFeedbackReadinessGate.FailedConditions)
            .Concat(learningFeedbackDualWriteSmoke.Mismatches)
            .Concat(learningFeedbackShadowReadSmoke.Mismatches)
            .Concat(learningFeedbackProviderQuality.Diagnostics)
            .Concat(learningFeedbackScopedSmoke.Diagnostics)
            .Concat(learningFeedbackScopedSmoke.Mismatches)
            .Concat(learningFeedbackScopedGate.Diagnostics)
            .Concat(learningFeedbackScopedGate.BlockedReasons)
            .Concat(learningFeedbackSelectedNormalCanary.Diagnostics)
            .Concat(learningFeedbackSelectedNormalCanary.BlockedReasons)
            .Concat(learningFeedbackSelectedNormalCanary.Mismatches)
            .Concat(learningFeedbackLimitedObservation.Diagnostics)
            .Concat(learningFeedbackLimitedObservation.BlockedReasons)
            .Concat(learningFeedbackLimitedObservation.Mismatches)
            .Concat(learningFeedbackLimitedQuality.Diagnostics)
            .Concat(learningFeedbackLimitedQuality.BlockedReasons)
            .Concat(learningFeedbackFreezeGate.Diagnostics)
            .Concat(learningFeedbackFreezeGate.BlockedReasons)
            .Concat(jobQueueDiagnostics.Diagnostics)
            .Concat(jobQueueDiagnostics.MissingIndexes)
            .Concat(jobQueueParity.Diagnostics)
            .Concat(jobQueueParity.Mismatches)
            .Concat(jobQueueLeaseSmoke.Diagnostics)
            .Concat(jobQueueLeaseSmoke.Mismatches)
            .Concat(jobQueueDualWriteSmoke.Diagnostics)
            .Concat(jobQueueDualWriteSmoke.Mismatches)
            .Concat(jobQueueShadowReadSmoke.Diagnostics)
            .Concat(jobQueueShadowReadSmoke.Mismatches)
            .Concat(jobQueueProviderQuality.Diagnostics)
            .Concat(jobQueueScopedWorkerCanary.Diagnostics)
            .Concat(jobQueueScopedWorkerCanary.Mismatches)
            .Concat(jobQueueScopedWorkerQuality.Diagnostics)
            .Concat(jobQueueScopedWorkerQuality.BlockedReasons)
            .Concat(jobQueueLimitedWorkerObservation.Diagnostics)
            .Concat(jobQueueLimitedWorkerObservation.BlockedReasons)
            .Concat(jobQueueLimitedWorkerObservation.Violations)
            .Concat(jobQueueLimitedWorkerQuality.Diagnostics)
            .Concat(jobQueueLimitedWorkerQuality.BlockedReasons)
            .Concat(jobQueueFreezeGate.Diagnostics)
            .Concat(jobQueueFreezeGate.BlockedReasons)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allDiagnostics.Length > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", allDiagnostics)}");
        }
    }

    private static void AppendPostgresVectorIndexStatus(
        StringBuilder builder,
        PostgresVectorDiagnosticsReport diagnostics,
        PostgresVectorCompatibilityReport compatibility,
        PostgresVectorProviderSmokeReport smoke,
        PostgresVectorIndexParityReport parity,
        PostgresVectorProviderScopedReindexPlan reindexPlan,
        PostgresVectorProviderScopedReindexResult reindexResult,
        PostgresVectorProviderScopedReindexReport reindexQuality,
        PostgresVectorQueryPreviewReport queryPreview,
        PostgresVectorShadowEvalReport shadowA3,
        PostgresVectorShadowEvalReport shadowExtended,
        PostgresVectorShadowEvalSummaryReport shadowSummary,
        VectorPostgresProviderFreezeGateReport freezeGate)
    {
        builder.AppendLine("- RuntimeProvider  : disabled / formal retrieval unchanged");
        builder.AppendLine($"- PgVector         : {diagnostics.PgVectorAvailable}");
        builder.AppendLine($"- SchemaVersion    : {(string.IsNullOrWhiteSpace(diagnostics.SchemaVersion) ? "-" : diagnostics.SchemaVersion)}");
        builder.AppendLine($"- TableExists      : {diagnostics.TableExists}");
        builder.AppendLine($"- MissingIndexes   : {diagnostics.MissingIndexCount}");
        builder.AppendLine($"- IndexedEntries   : {diagnostics.IndexedEntryCount}");
        builder.AppendLine($"- Dimensions       : {diagnostics.SupportedDimensionCount}");
        builder.AppendLine($"- Diagnostics      : {diagnostics.Recommendation}");
        builder.AppendLine("Compatibility");
        builder.AppendLine($"- Provider/Model   : {(string.IsNullOrWhiteSpace(compatibility.RequestedProviderId) ? "-" : compatibility.RequestedProviderId)} / {(string.IsNullOrWhiteSpace(compatibility.ModelId) ? "-" : compatibility.ModelId)}");
        builder.AppendLine($"- Dimension        : {compatibility.Dimension}");
        builder.AppendLine($"- TableCompatible  : {compatibility.TableDimensionCompatible}");
        builder.AppendLine($"- ExistingCompatible: {compatibility.ExistingIndexCompatible}");
        builder.AppendLine($"- StaleEntries     : {compatibility.StaleProviderModelEntriesCount}");
        builder.AppendLine($"- Recommendation   : {compatibility.Recommendation}");
        builder.AppendLine("Smoke");
        builder.AppendLine($"- Inserted         : {smoke.InsertedCount}");
        builder.AppendLine($"- Upserted         : {smoke.UpsertedCount}");
        builder.AppendLine($"- QueryCount       : {smoke.QueryCount}");
        builder.AppendLine($"- MismatchCount    : {smoke.MismatchCount}");
        builder.AppendLine($"- DimensionBlocked : {smoke.DimensionMismatchBlocked}");
        builder.AppendLine($"- ProviderBlocked  : {smoke.ProviderModelMismatchBlocked}");
        builder.AppendLine($"- CleanupPerformed : {smoke.CleanupPerformed}");
        builder.AppendLine($"- Recommendation   : {smoke.Recommendation}");
        builder.AppendLine("Parity");
        builder.AppendLine($"- Operations       : {parity.OperationCount}");
        builder.AppendLine($"- FS/PostgresCount : {parity.FileSystemEntryCount} / {parity.PostgresEntryCount}");
        builder.AppendLine($"- Inserted/Upserted: {parity.InsertedCount} / {parity.UpsertedCount}");
        builder.AppendLine($"- Deleted          : {parity.DeletedCount}");
        builder.AppendLine($"- QueryCount       : {parity.QueryCount}");
        builder.AppendLine($"- MismatchCount    : {parity.MismatchCount}");
        builder.AppendLine($"- OrderingMismatch : {parity.OrderingMismatchCount}");
        builder.AppendLine($"- ScoreDeltaMax    : {parity.ScoreDeltaMax:0.########}");
        builder.AppendLine($"- MetadataMismatch : {parity.MetadataMismatchCount}");
        builder.AppendLine($"- DimensionBlocked : {parity.DimensionMismatchBlocked}");
        builder.AppendLine($"- ProviderBlocked  : {parity.ProviderModelMismatchBlocked}");
        builder.AppendLine($"- CleanupPerformed : {parity.CleanupPerformed}");
        builder.AppendLine($"- Recommendation   : {parity.Recommendation}");
        builder.AppendLine("Provider-scoped Reindex");
        builder.AppendLine($"- Provider/Model   : {(string.IsNullOrWhiteSpace(reindexQuality.ProviderId) ? reindexPlan.ProviderId : reindexQuality.ProviderId)} / {(string.IsNullOrWhiteSpace(reindexQuality.ModelId) ? reindexPlan.ModelId : reindexQuality.ModelId)}");
        builder.AppendLine($"- Dimension        : {(reindexQuality.Dimension > 0 ? reindexQuality.Dimension : reindexPlan.Dimension)}");
        builder.AppendLine($"- Normalized       : {(reindexQuality.Dimension > 0 ? reindexQuality.Normalized : reindexPlan.Normalized)}");
        builder.AppendLine($"- Candidates       : {(reindexQuality.CandidateCount > 0 ? reindexQuality.CandidateCount : reindexPlan.CandidateCount)}");
        builder.AppendLine($"- Plan I/U/S       : {reindexPlan.PlannedInsertCount} / {reindexPlan.PlannedUpdateCount} / {reindexPlan.PlannedSkipCount}");
        builder.AppendLine($"- Applied I/U      : {reindexResult.AppliedInsertCount} / {reindexResult.AppliedUpdateCount}");
        builder.AppendLine($"- Stale/Orphan/Dup : {reindexQuality.StaleEntryCount} / {reindexQuality.OrphanEntryCount} / {reindexQuality.DuplicateSourceCount}");
        builder.AppendLine($"- MetadataMismatch : {reindexQuality.MetadataRoundtripMismatchCount}");
        builder.AppendLine($"- IndexedAfterApply: {reindexQuality.IndexedEntryCountAfterApply}");
        builder.AppendLine($"- UseForRuntime    : {reindexQuality.UseForRuntime}");
        builder.AppendLine($"- Recommendation   : {reindexQuality.Recommendation}");
        builder.AppendLine("PgVector Query Preview");
        builder.AppendLine($"- Queries          : {queryPreview.QueryCount}");
        builder.AppendLine($"- Pg/FS Candidates : {queryPreview.PgVectorCandidateCount} / {queryPreview.FileSystemCandidateCount}");
        builder.AppendLine($"- TopKOverlap      : {queryPreview.TopKOverlapCount} ({queryPreview.TopKOverlapRate:P1})");
        builder.AppendLine($"- OrderingMismatch : {queryPreview.OrderingMismatchCount}");
        builder.AppendLine($"- ScoreDeltaMax    : {queryPreview.ScoreDeltaMax:0.########}");
        builder.AppendLine($"- MetadataMismatch : {queryPreview.MetadataMismatchCount}");
        builder.AppendLine($"- EligibilityMismatch: {queryPreview.EligibilityMetadataMismatchCount}");
        builder.AppendLine($"- RiskProjectionMismatch: {queryPreview.RiskProjectionMismatchCount}");
        builder.AppendLine($"- DimensionBlocked : {queryPreview.DimensionMismatchBlocked}");
        builder.AppendLine($"- ProviderBlocked  : {queryPreview.ProviderModelMismatchBlocked}");
        builder.AppendLine($"- UseForRuntime    : {queryPreview.UseForRuntime}");
        builder.AppendLine($"- Recommendation   : {queryPreview.Recommendation}");
        builder.AppendLine("PgVector Shadow Eval");
        builder.AppendLine($"- Summary          : {shadowSummary.Recommendation}");
        builder.AppendLine($"- A3 Recall/Risk   : {shadowA3.RecallAfterPolicy:P1} / {shadowA3.RiskAfterPolicy}");
        builder.AppendLine($"- A3 FormalChanged : {shadowA3.FormalOutputChanged}");
        builder.AppendLine($"- A3 Overlap/Order : {shadowA3.TopKOverlapRate:P1} / {shadowA3.OrderingMismatchCount}");
        builder.AppendLine($"- A3 ScoreDeltaMax : {shadowA3.ScoreDeltaMax:0.########}");
        builder.AppendLine($"- Extended Recall/Risk: {shadowExtended.RecallAfterPolicy:P1} / {shadowExtended.RiskAfterPolicy}");
        builder.AppendLine($"- Extended FormalChanged: {shadowExtended.FormalOutputChanged}");
        builder.AppendLine($"- Extended Overlap/Order: {shadowExtended.TopKOverlapRate:P1} / {shadowExtended.OrderingMismatchCount}");
        builder.AppendLine($"- Extended ScoreDeltaMax: {shadowExtended.ScoreDeltaMax:0.########}");
        builder.AppendLine($"- ProjectionMismatch: {shadowA3.MetadataMismatchCount + shadowA3.EligibilityMetadataMismatchCount + shadowA3.RiskProjectionMismatchCount + shadowExtended.MetadataMismatchCount + shadowExtended.EligibilityMetadataMismatchCount + shadowExtended.RiskProjectionMismatchCount}");
        builder.AppendLine($"- RuntimeDisabled  : {!shadowSummary.UseForRuntime}");
        builder.AppendLine("Vector Postgres Freeze");
        builder.AppendLine($"- FreezeState      : {freezeGate.VectorPostgresProvider}");
        builder.AppendLine($"- GatePassed       : {freezeGate.Passed}");
        builder.AppendLine($"- DefaultStore     : {freezeGate.DefaultVectorStore}");
        builder.AppendLine($"- UseForRuntime    : {freezeGate.UseForRuntime}");
        builder.AppendLine($"- FormalRetrieval  : {freezeGate.FormalRetrievalAllowed}");
        builder.AppendLine($"- A3/Ext Delta     : {freezeGate.A3RecallDelta:0.########} / {freezeGate.ExtendedRecallDelta:0.########}");
        builder.AppendLine($"- Risk/FormalChange: {freezeGate.RiskAfterPolicy} / {freezeGate.FormalOutputChanged}");
        builder.AppendLine($"- ProjectionMismatch: {freezeGate.ProjectionMismatchCount}");
        builder.AppendLine($"- Required         : {string.Join(", ", freezeGate.Required)}");
        builder.AppendLine($"- Recommendation   : {freezeGate.Recommendation}");
    }

    public static string RenderMemoryDetail(ContextMemoryItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Memory Detail");
        builder.AppendLine("=====================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Layer      : {item.Layer}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"Type       : {item.Type}");
        builder.AppendLine($"Tags       : {string.Join(',', item.Tags)}");
        builder.AppendLine($"Refs       : {string.Join(',', item.RelationRefs)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Importance : {item.Importance:0.00}");
        builder.AppendLine($"UpdatedAt  : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderCandidateMemory(ServiceCandidateMemorySnapshot snapshot)
    {
        var view = snapshot.Snapshot;
        var diagnostics = snapshot.Diagnostics;
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Memory");
        builder.AppendLine("========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {view.WorkspaceId}");
        builder.AppendLine($"Collection : {view.CollectionId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Snapshot");
        builder.AppendLine($"- CandidateMemoryCount        : {view.CandidateMemoryCount}");
        builder.AppendLine($"- CandidateConstraintCount    : {view.CandidateConstraintCount}");
        builder.AppendLine($"- CandidateDecisionCount      : {view.CandidateDecisionCount}");
        builder.AppendLine($"- PendingReviewCount          : {view.PendingReviewCount}");
        builder.AppendLine($"- AcceptedFromPromotionCount  : {view.AcceptedFromPromotionCount}");
        builder.AppendLine($"- ExpiredCandidateCount       : {view.ExpiredCandidateCount}");
        builder.AppendLine($"- DuplicateCandidateCount     : {view.DuplicateCandidateCount}");
        builder.AppendLine($"- ConflictCandidateCount      : {view.ConflictCandidateCount}");
        builder.AppendLine();
        builder.AppendLine("Recent Candidates");
        foreach (var candidate in view.RecentCandidates.Take(20))
        {
            builder.AppendLine($"- {candidate.Id} [{candidate.CandidateKind}/{candidate.Status}/{candidate.Lifecycle}] type={candidate.Type}");
            builder.AppendLine($"  title    : {candidate.Title}");
            builder.AppendLine($"  evidence : {(candidate.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", candidate.EvidenceRefs))}");
            builder.AppendLine($"  source   : promotion={candidate.PromotionCandidateId ?? "-"} stable={candidate.StableReviewCandidateId ?? "-"} gap={candidate.ConstraintGapId ?? "-"}");
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- Total                 : {diagnostics.DiagnosticCount}");
        builder.AppendLine($"- Duplicate             : {diagnostics.DuplicateCandidateCount}");
        builder.AppendLine($"- Stale                 : {diagnostics.StaleCandidateCount}");
        builder.AppendLine($"- WithoutEvidence       : {diagnostics.CandidateWithoutEvidenceCount}");
        builder.AppendLine($"- RejectedSource        : {diagnostics.CandidateWithRejectedSourceCount}");
        builder.AppendLine($"- StableConflict        : {diagnostics.StableConflictCount}");
        builder.AppendLine($"- Superseded            : {diagnostics.SupersededCandidateCount}");
        foreach (var item in diagnostics.Diagnostics.Take(20))
        {
            builder.AppendLine($"  - {item.CandidateId} [{item.DiagnosticType}/{item.Severity}] {item.Reason}");
            builder.AppendLine($"    suggested: {item.SuggestedAction}");
            if (item.RelatedCandidateIds.Count > 0)
            {
                builder.AppendLine($"    related: {string.Join(", ", item.RelatedCandidateIds)}");
            }
        }

        if (view.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in view.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryDetail(CandidateMemoryRecord candidate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Memory Detail");
        builder.AppendLine("===============================");
        builder.AppendLine($"Id          : {candidate.Id}");
        builder.AppendLine($"Kind        : {candidate.CandidateKind}");
        builder.AppendLine($"Type        : {candidate.Type}");
        builder.AppendLine($"Status      : {candidate.Status}");
        builder.AppendLine($"Lifecycle   : {candidate.Lifecycle}");
        builder.AppendLine($"Importance  : {candidate.Importance:0.00}");
        builder.AppendLine($"Confidence  : {candidate.Confidence:0.00}");
        builder.AppendLine($"PromotionId : {candidate.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"StableId    : {candidate.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"GapId       : {candidate.ConstraintGapId ?? "-"}");
        builder.AppendLine($"FeedbackId  : {candidate.FeedbackId ?? "-"}");
        builder.AppendLine($"LearningId  : {candidate.LearningCaseId ?? "-"}");
        builder.AppendLine($"Evidence    : {(candidate.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", candidate.EvidenceRefs))}");
        builder.AppendLine($"SourceRefs  : {(candidate.SourceRefs.Count == 0 ? "-" : string.Join(", ", candidate.SourceRefs))}");
        builder.AppendLine($"UpdatedAt   : {candidate.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content     : {candidate.Content}");
        return builder.ToString();
    }

    public static string RenderCandidateMemoryExplanation(CandidateMemoryExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Memory Explain");
        builder.AppendLine("================================");
        builder.AppendLine($"Candidate : {explanation.CandidateId}");
        builder.AppendLine($"Kind      : {explanation.Candidate.CandidateKind}");
        builder.AppendLine($"RiskFlags : {(explanation.RiskFlags.Count == 0 ? "-" : string.Join(", ", explanation.RiskFlags))}");
        builder.AppendLine($"Evidence  : {(explanation.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", explanation.EvidenceRefs))}");
        builder.AppendLine();
        builder.AppendLine("Sources");
        builder.AppendLine($"- Promotion    : {explanation.SourcePromotionCandidate?.CandidateId ?? "-"}");
        builder.AppendLine($"- StableReview : {explanation.SourceStableReviewCandidate?.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"- ConstraintGap: {explanation.SourceConstraintGap?.GapId ?? "-"}");
        builder.AppendLine($"- Feedback     : {explanation.SourceFeedbackSignal?.FeedbackId ?? "-"}");
        builder.AppendLine($"- LearningCase : {explanation.SourceLearningCase?.CaseId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Review History");
        builder.AppendLine($"- Promotion reviews       : {explanation.PromotionReviewHistory.Count}");
        builder.AppendLine($"- Stable reviews          : {explanation.StableReviewHistory.Count}");
        builder.AppendLine($"- Constraint gap reviews  : {explanation.ConstraintGapReviewHistory.Count}");
        builder.AppendLine($"- Candidate constraint reviews: {explanation.CandidateConstraintReviewHistory.Count}");
        builder.AppendLine($"- Candidate memory reviews    : {explanation.CandidateMemoryReviewHistory.Count}");
        builder.AppendLine();
        builder.AppendLine("Provenance Chain");
        foreach (var link in explanation.ProvenanceChain)
        {
            builder.AppendLine($"- {link.SourceType}:{link.SourceId} relation={link.Relation} status={link.Status}");
        }

        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryReviewResult(CandidateMemoryReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Memory Review Result");
        builder.AppendLine("==============================");
        builder.AppendLine($"OperationId : {result.OperationId}");
        builder.AppendLine($"CandidateId : {result.CandidateId}");
        builder.AppendLine($"Kind        : {result.CandidateKind}");
        builder.AppendLine($"Action      : {result.Action}");
        builder.AppendLine($"Status      : {result.FromStatus} -> {result.ToStatus}");
        builder.AppendLine($"ReviewId    : {result.ReviewId}");
        builder.AppendLine($"Reviewer    : {result.Reviewer}");
        builder.AppendLine($"Reason      : {result.Reason}");
        builder.AppendLine($"ReviewedAt  : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Supersedes  : {result.SupersedeTargetCandidateId ?? "-"}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryReviews(IReadOnlyList<CandidateMemoryReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Memory Review History");
        builder.AppendLine("===============================");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(50))
        {
            builder.AppendLine($"- {review.ReviewId} {review.Action} {review.FromStatus}->{review.ToStatus}");
            builder.AppendLine($"  reviewer={review.Reviewer} reason={review.Reason} reviewedAt={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(review.SupersedeTargetCandidateId))
            {
                builder.AppendLine($"  supersedeTarget={review.SupersedeTargetCandidateId}");
            }

            if (review.Warnings.Count > 0)
            {
                builder.AppendLine($"  warnings={string.Join("; ", review.Warnings)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableMemory(ServiceStableMemorySnapshot snapshot)
    {
        var view = snapshot.Snapshot;
        var diagnostics = snapshot.Diagnostics;
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Memory");
        builder.AppendLine("=====================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {view.WorkspaceId}");
        builder.AppendLine($"Collection : {view.CollectionId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Snapshot");
        builder.AppendLine($"- StableMemoryCount        : {view.StableMemoryCount}");
        builder.AppendLine($"- StableConstraintCount    : {view.StableConstraintCount}");
        builder.AppendLine($"- DecisionRecordCount      : {view.DecisionRecordCount}");
        builder.AppendLine($"- GlobalMemoryCount        : {view.GlobalMemoryCount}");
        builder.AppendLine($"- ActiveCount              : {view.ActiveCount}");
        builder.AppendLine($"- SupersededCount          : {view.SupersededCount}");
        builder.AppendLine($"- DeprecatedCount          : {view.DeprecatedCount}");
        builder.AppendLine($"- RejectedCount            : {view.RejectedCount}");
        builder.AppendLine($"- MissingProvenanceCount   : {view.MissingProvenanceCount}");
        builder.AppendLine($"- DuplicateCandidateCount  : {view.DuplicateCandidateCount}");
        builder.AppendLine($"- ConflictCandidateCount   : {view.ConflictCandidateCount}");
        builder.AppendLine($"- WeakEvidenceCount        : {view.WeakEvidenceCount}");
        builder.AppendLine();
        builder.AppendLine("Recent Stable Items");
        foreach (var item in view.RecentStableItems.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.StableKind}/{item.Status}/{item.Lifecycle}] type={item.Type}");
            builder.AppendLine($"  title    : {item.Title}");
            builder.AppendLine($"  evidence : {(item.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", item.EvidenceRefs))}");
            builder.AppendLine($"  source   : stableReview={item.StableReviewCandidateId ?? "-"} promotion={item.PromotionCandidateId ?? "-"} learning={item.LearningCaseId ?? "-"}");
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- Total                         : {diagnostics.DiagnosticCount}");
        builder.AppendLine($"- DuplicateStableMemory         : {diagnostics.DuplicateStableMemoryCount}");
        builder.AppendLine($"- PossibleConflict              : {diagnostics.PossibleConflictCount}");
        builder.AppendLine($"- MissingProvenance             : {diagnostics.MissingProvenanceCount}");
        builder.AppendLine($"- MissingEvidenceRefs           : {diagnostics.MissingEvidenceRefsCount}");
        builder.AppendLine($"- StableWithoutReviewSource     : {diagnostics.StableWithoutReviewSourceCount}");
        builder.AppendLine($"- StableConstraintWithoutScope  : {diagnostics.StableConstraintWithoutScopeCount}");
        builder.AppendLine($"- DecisionRecordWithoutSource   : {diagnostics.DecisionRecordWithoutSourceCount}");
        builder.AppendLine($"- DeprecatedStillActive         : {diagnostics.DeprecatedStillActiveCount}");
        builder.AppendLine($"- SupersededWithoutReplacement  : {diagnostics.SupersededWithoutReplacementCount}");
        builder.AppendLine($"- GlobalMemoryScopeRisk         : {diagnostics.GlobalMemoryScopeRiskCount}");
        builder.AppendLine($"- SupersededWithoutRelation     : {diagnostics.SupersededWithoutRelationCount}");
        builder.AppendLine($"- MetadataRelationMismatch      : {diagnostics.MetadataRelationMismatchCount}");
        builder.AppendLine($"- BrokenReplacementLink         : {diagnostics.BrokenReplacementLinkCount}");
        builder.AppendLine($"- ReplacementTargetMissing      : {diagnostics.ReplacementTargetMissingCount}");
        builder.AppendLine($"- ReplacementTargetInactive     : {diagnostics.ReplacementTargetInactiveCount}");
        builder.AppendLine($"- ReplacementCycle              : {diagnostics.ReplacementCycleCount}");
        builder.AppendLine($"- MultipleActiveReplacements    : {diagnostics.MultipleActiveReplacementsCount}");
        builder.AppendLine($"- ScopeMismatchInReplacement    : {diagnostics.ScopeMismatchInReplacementCount}");
        foreach (var item in diagnostics.Diagnostics.Take(20))
        {
            builder.AppendLine($"  - {item.StableItemId} [{item.StableKind}/{item.DiagnosticType}/{item.Severity}] {item.Reason}");
            if (item.RelatedStableItemIds.Count > 0)
            {
                builder.AppendLine($"    related: {string.Join(", ", item.RelatedStableItemIds)}");
            }
        }

        if (view.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in view.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableReplacementChain(StableReplacementChainResponse chain)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Replacement Chain");
        builder.AppendLine("========================");
        builder.AppendLine($"Item       : {chain.ItemId}");
        builder.AppendLine($"Current    : {chain.CurrentItem.Id} [{chain.CurrentItem.Status}/{chain.CurrentItem.Lifecycle}]");
        builder.AppendLine($"Root       : {chain.RootItem?.Id ?? "-"}");
        builder.AppendLine($"Latest     : {chain.LatestItem?.Id ?? "-"} [{chain.LatestItem?.Status.ToString() ?? "-"} / {chain.LatestItem?.Lifecycle ?? "-"}]");
        builder.AppendLine();
        builder.AppendLine("Previous Items");
        if (chain.PreviousItems.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var item in chain.PreviousItems)
            {
                builder.AppendLine($"- {item.Id} [{item.StableKind}/{item.Status}/{item.Lifecycle}] {item.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Next Items");
        if (chain.NextItems.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var item in chain.NextItems)
            {
                builder.AppendLine($"- {item.Id} [{item.StableKind}/{item.Status}/{item.Lifecycle}] {item.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Relations");
        if (chain.Relations.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var relation in chain.Relations)
            {
                builder.AppendLine($"- {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} confidence={relation.Confidence:0.00}");
                builder.AppendLine($"  reviewId={relation.Metadata.GetValueOrDefault("reviewId", "-")} lifecycle={relation.Metadata.GetValueOrDefault("lifecycle", "-")} source={relation.Metadata.GetValueOrDefault("source", "-")}");
            }
        }

        if (chain.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in chain.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableMemoryDetail(StableMemoryRecord item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Memory Detail");
        builder.AppendLine("============================");
        builder.AppendLine($"Id          : {item.Id}");
        builder.AppendLine($"Kind        : {item.StableKind}");
        builder.AppendLine($"Type        : {item.Type}");
        builder.AppendLine($"Status      : {item.Status}");
        builder.AppendLine($"Lifecycle   : {item.Lifecycle}");
        builder.AppendLine($"Scope       : {item.Scope?.ToString() ?? "-"}");
        builder.AppendLine($"Level       : {item.ConstraintLevel?.ToString() ?? "-"}");
        builder.AppendLine($"Importance  : {item.Importance:0.00}");
        builder.AppendLine($"Confidence  : {item.Confidence:0.00}");
        builder.AppendLine($"StableId    : {item.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"PromotionId : {item.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"FeedbackId  : {item.FeedbackId ?? "-"}");
        builder.AppendLine($"LearningId  : {item.LearningCaseId ?? "-"}");
        builder.AppendLine($"WorkingId   : {item.WorkingItemId ?? "-"}");
        builder.AppendLine($"Evidence    : {(item.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", item.EvidenceRefs))}");
        builder.AppendLine($"SourceRefs  : {(item.SourceRefs.Count == 0 ? "-" : string.Join(", ", item.SourceRefs))}");
        builder.AppendLine($"UpdatedAt   : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content     : {item.Content}");
        return builder.ToString();
    }

    public static string RenderStableMemoryExplanation(StableMemoryExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Memory Explain");
        builder.AppendLine("=============================");
        builder.AppendLine($"StableItem : {explanation.StableItemId}");
        builder.AppendLine($"Kind       : {explanation.StableItem.StableKind}");
        builder.AppendLine($"Evidence   : {(explanation.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", explanation.EvidenceRefs))}");
        builder.AppendLine();
        builder.AppendLine("Source Refs");
        builder.AppendLine($"- StableReview : {explanation.StableItem.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"- Promotion    : {explanation.StableItem.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"- Feedback     : {explanation.StableItem.FeedbackId ?? "-"}");
        builder.AppendLine($"- LearningCase : {explanation.StableItem.LearningCaseId ?? "-"}");
        builder.AppendLine($"- WorkingItem  : {explanation.StableItem.WorkingItemId ?? "-"}");
        if (explanation.Provenance is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Provenance");
            builder.AppendLine($"- targetKind={explanation.Provenance.TargetItemKind}");
            builder.AppendLine($"- stableReview={explanation.Provenance.StableReviewCandidate?.StableReviewCandidateId ?? "-"}");
            builder.AppendLine($"- promotion={explanation.Provenance.PromotionCandidate?.CandidateId ?? "-"}");
            builder.AppendLine($"- feedback={explanation.Provenance.FeedbackSignal?.FeedbackId ?? "-"}");
            builder.AppendLine($"- learningCase={explanation.Provenance.LearningCase?.CaseId ?? "-"}");
            builder.AppendLine($"- sourceWorkingItem={explanation.Provenance.SourceWorkingItem?.ItemId ?? "-"}");
            builder.AppendLine($"- missingLinks={(explanation.Provenance.MissingLinks.Count == 0 ? "-" : string.Join(", ", explanation.Provenance.MissingLinks))}");
        }

        if (explanation.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in explanation.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] {diagnostic.Reason}");
            }
        }

        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableLifecycleReviewResult(StableLifecycleReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Lifecycle Review Result");
        builder.AppendLine("==============================");
        builder.AppendLine($"OperationId : {result.OperationId}");
        builder.AppendLine($"StableItem  : {result.StableItemId}");
        builder.AppendLine($"Kind        : {result.StableKind}");
        builder.AppendLine($"Action      : {result.Action}");
        builder.AppendLine($"Status      : {result.FromStatus} -> {result.ToStatus}");
        builder.AppendLine($"Lifecycle   : {result.FromLifecycle} -> {result.ToLifecycle}");
        builder.AppendLine($"ReviewId    : {result.ReviewId}");
        builder.AppendLine($"Reviewer    : {result.Reviewer}");
        builder.AppendLine($"Reason      : {result.Reason}");
        builder.AppendLine($"Replacement : {result.ReplacementItemId ?? "-"}");
        builder.AppendLine($"ReviewedAt  : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableLifecycleReviews(IReadOnlyList<StableLifecycleReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Lifecycle Review History");
        builder.AppendLine("===============================");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(50))
        {
            builder.AppendLine($"- {review.ReviewId} {review.Action} {review.FromStatus}->{review.ToStatus} {review.FromLifecycle}->{review.ToLifecycle}");
            builder.AppendLine($"  reviewer={review.Reviewer} reason={review.Reason} reviewedAt={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  replacement={review.ReplacementItemId ?? "-"}");
            if (review.Warnings.Count > 0)
            {
                builder.AppendLine($"  warnings={string.Join("; ", review.Warnings)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderGlobalMemoryDetail(ContextGlobalItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Global Context Detail");
        builder.AppendLine("=============================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Type       : {item.Type}");
        builder.AppendLine($"Tags       : {string.Join(',', item.Tags)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Importance : {item.Importance:0.00}");
        builder.AppendLine($"UpdatedAt  : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderConstraints(ServiceConstraintsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraints");
        builder.AppendLine("===================");
        builder.AppendLine($"Count: {snapshot.Constraints.Count}");
        foreach (var item in snapshot.Constraints.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}] scope={item.Scope} appliesTo={string.Join(',', item.AppliesToRefs)}");
        }
        return builder.ToString();
    }

    public static string RenderConstraintDetail(ContextConstraint item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraint Detail");
        builder.AppendLine("=========================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Type       : {item.Level}");
        builder.AppendLine($"Severity   : {item.Level}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"AppliesTo  : {string.Join(',', item.AppliesToRefs)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderConstraintGaps(ServiceConstraintGapsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraint Gaps");
        builder.AppendLine("=======================");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Count   : {snapshot.Gaps.Count}");
        builder.AppendLine($"Filter  : status={snapshot.Status ?? "-"} severity={snapshot.Severity ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        foreach (var gap in snapshot.Gaps.Take(20))
        {
            builder.AppendLine($"- {gap.GapId} [{gap.Status}/{gap.Severity}] sample={gap.SourceSampleId} source={gap.Source}");
            builder.AppendLine($"  expected : {gap.ExpectedConstraintText}");
            builder.AppendLine($"  suggest  : scope={gap.SuggestedConstraintScope} type={gap.SuggestedConstraintType} title={gap.SuggestedConstraintTitle}");
            builder.AppendLine($"  evidence : {string.Join(", ", gap.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapDetail(ConstraintGapCandidate gap)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraint Gap Detail");
        builder.AppendLine("=============================");
        builder.AppendLine($"GapId                  : {gap.GapId}");
        builder.AppendLine($"Status                 : {gap.Status}");
        builder.AppendLine($"Severity               : {gap.Severity}");
        builder.AppendLine($"Source                 : {gap.Source}");
        builder.AppendLine($"SourceSampleId         : {gap.SourceSampleId}");
        builder.AppendLine($"SourceOperationId      : {gap.SourceOperationId}");
        builder.AppendLine($"ExpectedConstraintText : {gap.ExpectedConstraintText}");
        builder.AppendLine($"MatchedConstraintIds   : {(gap.MatchedConstraintIds.Count == 0 ? "-" : string.Join(", ", gap.MatchedConstraintIds))}");
        builder.AppendLine($"SuggestedTitle         : {gap.SuggestedConstraintTitle}");
        builder.AppendLine($"SuggestedScope         : {gap.SuggestedConstraintScope}");
        builder.AppendLine($"SuggestedType          : {gap.SuggestedConstraintType}");
        builder.AppendLine($"Reason                 : {gap.Reason}");
        builder.AppendLine($"EvidenceRefs           : {string.Join(", ", gap.EvidenceRefs)}");
        if (gap.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in gap.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapReviewResult(ConstraintGapReviewResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Constraint Gap Review Result");
        builder.AppendLine("============================");
        builder.AppendLine($"OperationId         : {response.OperationId}");
        builder.AppendLine($"GapId               : {response.GapId}");
        builder.AppendLine($"Action              : {response.Action}");
        builder.AppendLine($"Status              : {response.Status}");
        builder.AppendLine($"ReviewId            : {response.ReviewId}");
        builder.AppendLine($"Reviewer            : {response.Reviewer}");
        builder.AppendLine($"Reason              : {response.Reason}");
        builder.AppendLine($"ReviewedAt          : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"CreatedConstraintId : {response.CreatedConstraintId ?? response.TargetItemId ?? "-"}");
        builder.AppendLine($"TargetKind          : {response.TargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer         : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapReviews(IReadOnlyList<ConstraintGapReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Constraint Gap Review History");
        builder.AppendLine("=============================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer            : {review.Reviewer}");
            builder.AppendLine($"  reason              : {review.Reason}");
            builder.AppendLine($"  createdConstraintId : {review.CreatedConstraintId ?? "-"}");
            builder.AppendLine($"  source              : sample={review.SourceSampleId} operation={review.SourceOperationId}");
            builder.AppendLine($"  expected            : {review.ExpectedConstraintText}");
            builder.AppendLine($"  evidenceRefs        : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt          : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraints(ServiceCandidateConstraintsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Constraints");
        builder.AppendLine("=============================");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Count   : {snapshot.Constraints.Count}");
        builder.AppendLine($"Filter  : status={snapshot.Status?.ToString() ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        foreach (var item in snapshot.Constraints.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}] scope={item.Scope}");
            builder.AppendLine($"  source   : gap={ReadMetadata(item, "sourceConstraintGapId")} sample={ReadMetadata(item, "sourceSampleId")}");
            builder.AppendLine($"  evidence : {ReadMetadata(item, "evidenceRefs")}");
            builder.AppendLine($"  content  : {item.Content}");
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintDetail(ContextConstraint item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Constraint Detail");
        builder.AppendLine("===================================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Level      : {item.Level}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"Confidence : {item.Confidence:0.###}");
        builder.AppendLine($"SourceRefs : {string.Join(", ", item.SourceRefs)}");
        builder.AppendLine($"Content    : {item.Content}");
        if (item.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in item.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintReviewResult(CandidateConstraintReviewResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Constraint Review Result");
        builder.AppendLine("==================================");
        builder.AppendLine($"OperationId           : {response.OperationId}");
        builder.AppendLine($"ConstraintId          : {response.ConstraintId}");
        builder.AppendLine($"Action                : {response.Action}");
        builder.AppendLine($"Status                : {response.Status}");
        builder.AppendLine($"ReviewId              : {response.ReviewId}");
        builder.AppendLine($"Reviewer              : {response.Reviewer}");
        builder.AppendLine($"Reason                : {response.Reason}");
        builder.AppendLine($"ReviewedAt            : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"ActivatedConstraintId : {response.ActivatedConstraintId ?? "-"}");
        builder.AppendLine($"TargetLayer           : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintReviews(IReadOnlyList<CandidateConstraintReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Constraint Review History");
        builder.AppendLine("===================================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer            : {review.Reviewer}");
            builder.AppendLine($"  reason              : {review.Reason}");
            builder.AppendLine($"  activatedConstraint : {review.ActivatedConstraintId ?? "-"}");
            builder.AppendLine($"  source              : gap={review.SourceConstraintGapId} sample={review.SourceSampleId} operation={review.SourceOperationId}");
            builder.AppendLine($"  evidenceRefs        : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt          : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderProvenance(ContextProvenanceResponse provenance)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Provenance");
        builder.AppendLine("==================");
        builder.AppendLine($"ItemId     : {provenance.ItemId}");
        builder.AppendLine($"TargetKind : {(string.IsNullOrWhiteSpace(provenance.TargetItemKind) ? "-" : provenance.TargetItemKind)}");
        if (provenance.TargetMemoryItem is not null)
        {
            builder.AppendLine("Target Memory");
            builder.AppendLine($"- {provenance.TargetMemoryItem.Id} [{provenance.TargetMemoryItem.Layer}/{provenance.TargetMemoryItem.Status}/{provenance.TargetMemoryItem.Type}]");
            builder.AppendLine($"  sourceRefs : {string.Join(", ", provenance.TargetMemoryItem.SourceRefs)}");
        }

        if (provenance.TargetConstraint is not null)
        {
            builder.AppendLine("Target Constraint");
            builder.AppendLine($"- {provenance.TargetConstraint.Id} [{provenance.TargetConstraint.Level}/{provenance.TargetConstraint.Status}]");
            builder.AppendLine($"  sourceRefs : {string.Join(", ", provenance.TargetConstraint.SourceRefs)}");
        }

        if (provenance.StableReviewCandidate is not null)
        {
            builder.AppendLine("Stable Review Candidate");
            builder.AppendLine($"- {provenance.StableReviewCandidate.StableReviewCandidateId} [{provenance.StableReviewCandidate.Status}/{provenance.StableReviewCandidate.ValidationStatus}]");
            builder.AppendLine($"  source     : promotion={provenance.StableReviewCandidate.SourceCandidateId} target={provenance.StableReviewCandidate.SourceTargetItemId} learningCase={provenance.StableReviewCandidate.SourceLearningCaseId ?? "-"}");
        }

        if (provenance.PromotionCandidate is not null)
        {
            builder.AppendLine("Promotion Candidate");
            builder.AppendLine($"- {provenance.PromotionCandidate.CandidateId} [{provenance.PromotionCandidate.Kind}/{provenance.PromotionCandidate.Status}] target={provenance.PromotionCandidate.SuggestedTargetLayer}");
            builder.AppendLine($"  workingItem: {provenance.PromotionCandidate.SourceWorkingItemId}");
        }

        if (provenance.FeedbackSignal is not null)
        {
            builder.AppendLine("Feedback Signal");
            builder.AppendLine($"- {provenance.FeedbackSignal.FeedbackId} [{provenance.FeedbackSignal.Action}] reviewer={provenance.FeedbackSignal.Reviewer}");
        }

        if (provenance.LearningCase is not null)
        {
            builder.AppendLine("Learning Case");
            builder.AppendLine($"- {provenance.LearningCase.CaseId} [{provenance.LearningCase.CaseKind}/{provenance.LearningCase.Signal}/{provenance.LearningCase.Status}]");
        }

        if (provenance.SourceWorkingItem is not null)
        {
            builder.AppendLine("Source Working Item");
            builder.AppendLine($"- {provenance.SourceWorkingItem.ItemId} [{provenance.SourceWorkingItem.Kind}/{provenance.SourceWorkingItem.Status}] {provenance.SourceWorkingItem.Summary}");
        }

        builder.AppendLine($"EvidenceRefs : {(provenance.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", provenance.EvidenceRefs))}");
        builder.AppendLine($"StableReviews: {provenance.StableReviewHistory.Count}");
        foreach (var review in provenance.StableReviewHistory.Take(5))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus}->{review.ToStatus} target={review.StableTargetItemId ?? "-"}");
        }

        builder.AppendLine($"PromotionReviews: {provenance.PromotionReviewHistory.Count}");
        foreach (var review in provenance.PromotionReviewHistory.Take(5))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus}->{review.ToStatus} target={review.TargetItemId ?? "-"}");
        }

        if (provenance.Diagnostics.Count > 0)
        {
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in provenance.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic.Code} [{diagnostic.Severity}] {diagnostic.Message}");
            }
        }

        if (provenance.MissingLinks.Count > 0)
        {
            builder.AppendLine($"MissingLinks: {string.Join(", ", provenance.MissingLinks)}");
        }

        if (provenance.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in provenance.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderRelations(ServiceRelationsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relations");
        builder.AppendLine("=================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine();
        builder.AppendLine("Relation Types");
        builder.AppendLine($"Count: {snapshot.RelationTypes.Count}");
        foreach (var type in snapshot.RelationTypes.Take(20))
        {
            builder.AppendLine($"- {type.Type} directional={type.IsDirectional} inverse={type.InverseType ?? "-"} weight={type.DefaultWeight:0.00} evidence={(type.RequiresEvidence ? "yes" : "no")} normalExpansion={(type.AllowsNormalExpansion ? "yes" : "no")}");
        }

        AppendRelationDiagnostics(builder, "Global Relation Diagnostics", snapshot.Diagnostics);
        AppendGraphExpansionShadowTraceQualitySummary(builder, snapshot.GraphShadowTraceQualitySummary);
        AppendRecentGraphExpansionShadowTraces(builder, snapshot.RecentGraphShadowTraces);

        if (!string.IsNullOrWhiteSpace(snapshot.ItemId))
        {
            builder.AppendLine();
            builder.AppendLine("Item Relations");
            builder.AppendLine($"ItemId   : {snapshot.ItemId}");
            builder.AppendLine($"Outgoing : {snapshot.Relations.Outgoing.Count}");
            foreach (var relation in snapshot.Relations.Outgoing)
            {
                builder.AppendLine($"- OUT {relation.SourceId} -> {relation.TargetId} type={relation.RelationType} weight={relation.Weight:0.00} confidence={relation.Confidence:0.00}");
            }

            builder.AppendLine($"Incoming : {snapshot.Relations.Incoming.Count}");
            foreach (var relation in snapshot.Relations.Incoming)
            {
                builder.AppendLine($"- IN  {relation.SourceId} -> {relation.TargetId} type={relation.RelationType} weight={relation.Weight:0.00} confidence={relation.Confidence:0.00}");
            }

            if (snapshot.ItemDiagnostics is not null)
            {
                AppendRelationDiagnostics(builder, "Item Relation Diagnostics", snapshot.ItemDiagnostics);
            }
        }

        return builder.ToString();
    }

    private static void AppendGraphExpansionShadowTraceQualitySummary(
        StringBuilder builder,
        GraphExpansionShadowTraceQualityReport report)
    {
        builder.AppendLine();
        builder.AppendLine("Graph Shadow Trace Quality Summary");
        builder.AppendLine("----------------------------------");
        builder.AppendLine($"- traces={report.TraceCount} accepted={report.AcceptedRelationCount} blocked={report.BlockedRelationCount} audit={report.AuditContextCount} conflict={report.ConflictEvidenceCount}");
        builder.AppendLine($"- risks afterRouting={report.RiskAfterRoutingCount} wrongSection={report.WrongSectionRiskCount} mustNotHit={report.MustNotHitRiskCount} lifecycle={report.LifecycleRiskCount} missingEvidence={report.MissingEvidenceCount}");
        builder.AppendLine($"- next={BlankDash(report.Recommendation)}");
    }

    private static void AppendRecentGraphExpansionShadowTraces(
        StringBuilder builder,
        IReadOnlyList<GraphExpansionShadowTraceRecord> traces)
    {
        builder.AppendLine();
        builder.AppendLine("Recent Graph Shadow Traces");
        builder.AppendLine("--------------------------");
        if (traces.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var trace in traces.Take(5))
        {
            builder.AppendLine($"- {trace.RetrievalId} {trace.CreatedAt:yyyy-MM-dd HH:mm:ss} profiles={string.Join(",", trace.Profiles.DefaultIfEmpty("-"))} accepted={trace.AcceptedRelations.Count} blocked={trace.BlockedRelations.Count}");
            builder.AppendLine($"  sections: {FormatTargetSections(trace.TargetSections)}");
            builder.AppendLine($"  risks: normal={trace.RiskIfNormal} afterRouting={trace.RiskAfterRouting} wrongSection={trace.WrongSectionRisk}");
            builder.AppendLine($"  query: {Compact(trace.Query, 140)}");
        }
    }

    private static string FormatTargetSections(IReadOnlyDictionary<string, int> sections)
    {
        return sections.Count == 0
            ? "-"
            : string.Join(", ", sections
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static item => $"{item.Key}={item.Value}"));
    }

    public static string RenderRelationExplain(RelationExplainResponse explain)
    {
        var relation = explain.Relation;
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Explain");
        builder.AppendLine("========================");
        builder.AppendLine($"RelationId : {explain.RelationId}");
        builder.AppendLine($"Type       : {relation?.RelationType ?? explain.TypeDefinition?.Type ?? "-"}");
        builder.AppendLine($"Source     : {relation?.SourceId ?? "-"} ({explain.SourceItem?.Kind ?? "unknown"}, lifecycle={explain.SourceItem?.Lifecycle ?? "-"})");
        builder.AppendLine($"Target     : {relation?.TargetId ?? "-"} ({explain.TargetItem?.Kind ?? "unknown"}, lifecycle={explain.TargetItem?.Lifecycle ?? "-"})");
        builder.AppendLine($"Inverse    : {explain.InverseRelation?.Id ?? "-"}");
        builder.AppendLine($"Confidence : {explain.Confidence:0.00} reason={BlankDash(explain.ConfidenceReason)}");
        builder.AppendLine($"Lifecycle  : {BlankDash(explain.Lifecycle)}");
        builder.AppendLine($"Review     : {BlankDash(explain.ReviewStatus)}");
        builder.AppendLine();
        builder.AppendLine("Evidence");
        builder.AppendLine($"EvidenceRefs: {string.Join(", ", explain.EvidenceRefs.DefaultIfEmpty("-"))}");
        builder.AppendLine($"SourceRefs  : {string.Join(", ", explain.SourceRefs.DefaultIfEmpty("-"))}");
        foreach (var evidence in explain.Evidence.Take(10))
        {
            builder.AppendLine($"- {evidence.EvidenceId} kind={BlankDash(evidence.EvidenceKind)} sourceOperation={BlankDash(evidence.SourceOperationId)} sourceItem={BlankDash(evidence.SourceItemId)}");
            if (!string.IsNullOrWhiteSpace(evidence.EvidenceText))
            {
                builder.AppendLine($"  text: {evidence.EvidenceText}");
            }
        }

        if (explain.TypeDefinition is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Type Definition");
            builder.AppendLine($"- directional={explain.TypeDefinition.IsDirectional} inverse={explain.TypeDefinition.InverseType ?? "-"} requiresEvidence={explain.TypeDefinition.RequiresEvidence} normalExpansion={explain.TypeDefinition.AllowsNormalExpansion}");
        }

        if (explain.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in explain.Diagnostics.Take(20))
            {
                builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] {diagnostic.Reason}");
            }
        }

        if (explain.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in explain.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderRelationExpansionProfiles(IReadOnlyList<RelationExpansionProfile> profiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Expansion Profiles");
        builder.AppendLine("===================================");
        builder.AppendLine($"Count: {profiles.Count}");
        foreach (var profile in profiles.OrderBy(item => item.ProfileId, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {profile.ProfileId} mode={profile.Mode} intent={profile.Intent} depth={profile.MaxDepth} fanout={profile.MaxFanout} minConfidence={profile.MinConfidence:0.00}");
            builder.AppendLine($"  allowed={string.Join(", ", profile.AllowedRelationTypes.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  blocked={string.Join(", ", profile.BlockedRelationTypes.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  lifecycle={BlankDash(profile.LifecyclePolicy)} candidate={profile.AllowCandidateRelations} deprecated={profile.AllowDeprecatedRelations} rejected={profile.AllowRejectedRelations} requireEvidence={profile.RequireEvidence}");
        }

        return builder.ToString();
    }

    public static string RenderRelationExpansionPreview(RelationExpansionPreviewResponse preview)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Expansion Preview");
        builder.AppendLine("==================================");
        builder.AppendLine($"Operation : {preview.OperationId}");
        builder.AppendLine($"ItemId    : {preview.ItemId}");
        builder.AppendLine($"Profile   : {preview.Profile.ProfileId} ({preview.Profile.Mode}/{preview.Profile.Intent})");
        builder.AppendLine($"Accepted  : {preview.AcceptedCount}");
        builder.AppendLine($"Blocked   : {preview.BlockedCount}");
        builder.AppendLine();
        builder.AppendLine("Accepted Relations");
        foreach (var relation in preview.AcceptedRelations.Take(20))
        {
            builder.AppendLine($"- {relation.RelationId} depth={relation.Depth} {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} confidence={relation.Confidence:0.00} weight={relation.Weight:0.00}");
            builder.AppendLine($"  section={BlankDash(relation.TargetSection)} reason={BlankDash(relation.SectionReason)} riskNormal={relation.RiskIfNormalSelected} riskAfterRouting={relation.RiskAfterSectionRouting}");
        }

        if (preview.AcceptedRelations.Count == 0)
        {
            builder.AppendLine("- none");
        }

        builder.AppendLine();
        builder.AppendLine("Blocked Relations");
        foreach (var relation in preview.BlockedRelations.Take(30))
        {
            builder.AppendLine($"- {relation.RelationId} depth={relation.Depth} {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} reasons={string.Join(",", relation.Reasons.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  section={BlankDash(relation.TargetSection)} reason={BlankDash(relation.SectionReason)} riskNormal={relation.RiskIfNormalSelected} riskAfterRouting={relation.RiskAfterSectionRouting}");
        }

        if (preview.BlockedRelations.Count == 0)
        {
            builder.AppendLine("- none");
        }

        if (preview.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in preview.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderRelationReviewResult(RelationReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Review Result");
        builder.AppendLine("==============================");
        builder.AppendLine($"Operation  : {result.OperationId}");
        builder.AppendLine($"RelationId : {result.RelationId}");
        builder.AppendLine($"Action     : {result.Action}");
        builder.AppendLine($"Lifecycle  : {BlankDash(result.FromLifecycle)} -> {BlankDash(result.ToLifecycle)}");
        builder.AppendLine($"Review     : {BlankDash(result.FromReviewStatus)} -> {BlankDash(result.ToReviewStatus)}");
        builder.AppendLine($"Reviewer   : {BlankDash(result.Reviewer)}");
        builder.AppendLine($"Reason     : {BlankDash(result.Reason)}");
        builder.AppendLine($"ReviewedAt : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Relation   : {result.Relation.SourceId} --{result.Relation.RelationType}--> {result.Relation.TargetId}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderRelationReviews(IReadOnlyList<RelationReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Review History");
        builder.AppendLine("===============================");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(20))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {BlankDash(review.FromLifecycle)}->{BlankDash(review.ToLifecycle)} review={BlankDash(review.FromReviewStatus)}->{BlankDash(review.ToReviewStatus)}");
            builder.AppendLine($"  relation={review.RelationId} {review.SourceId} --{review.RelationType}--> {review.TargetId}");
            builder.AppendLine($"  reviewer={BlankDash(review.Reviewer)} reason={Compact(review.Reason, 160)} at={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    private static void AppendRelationDiagnostics(
        StringBuilder builder,
        string title,
        RelationGraphDiagnosticsReport report)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine($"Relations={report.RelationCount} Diagnostics={report.DiagnosticCount}");
        foreach (var diagnostic in report.Diagnostics.Take(20))
        {
            builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] relation={diagnostic.RelationId ?? "-"} {diagnostic.SourceId ?? "-"} --{diagnostic.RelationType ?? "-"}--> {diagnostic.TargetId ?? "-"}");
            builder.AppendLine($"  reason: {diagnostic.Reason}");
            if (diagnostic.RelatedItemIds.Count > 0)
            {
                builder.AppendLine($"  items : {string.Join(", ", diagnostic.RelatedItemIds)}");
            }
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }
    }

    private static void AppendStatusLine(StringBuilder builder, string value)
    {
        AppendLabeledLine(builder, "status", value);
    }

    private static void AppendMetricLine(StringBuilder builder, string label, string value)
    {
        AppendLabeledLine(builder, label, value);
    }

    private static void AppendBooleanInvariantLine(StringBuilder builder, string label, bool value)
    {
        AppendLabeledLine(builder, label, value.ToString());
    }

    private static void AppendRecommendationLine(StringBuilder builder, string? value)
    {
        AppendLabeledLine(builder, "recommendation", BlankDash(value));
    }

    private static void AppendBlockedLine(StringBuilder builder, IReadOnlyList<string> blockedReasons, string label = "blocked")
    {
        AppendLabeledLine(
            builder,
            label,
            blockedReasons.Count == 0 ? "-" : string.Join(", ", blockedReasons));
    }

    private static void AppendMissingSummaryState(StringBuilder builder, string status, string action)
    {
        AppendStatusLine(builder, status);
        AppendMetricLine(builder, "action", action);
    }

    private static void AppendLabeledLine(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"- {label,-14}: {value}");
    }

    private static string BlankDash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "-" : string.Join(", ", values);
    }

    private static string FormatMap(IReadOnlyDictionary<string, string> values, int maxItems = 6)
    {
        if (values.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", values
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    public static string RenderPolicy(ServicePolicySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Policy");
        builder.AppendLine("==============");
        builder.AppendLine($"PersistedPolicies : {snapshot.Policies.Count}");
        builder.AppendLine($"DefaultPolicy     : {snapshot.DefaultPolicy.Name}");
        builder.AppendLine($"TokenBudget       : {snapshot.DefaultPolicy.TokenBudget}");
        builder.AppendLine($"SectionPriorities : {(snapshot.DefaultPolicy.SectionPriorities.Count == 0 ? "(default)" : string.Join(',', snapshot.DefaultPolicy.SectionPriorities.Select(p => $"{p.Key}={p.Value}")))}");
        builder.AppendLine("LifecyclePolicy");
        foreach (var note in snapshot.LifecycleNotes)
        {
            builder.AppendLine($"- {note}");
        }
        builder.AppendLine("ProviderCapabilities");
        foreach (var capability in snapshot.ProviderCapabilities)
        {
            builder.AppendLine($"- {capability.Name} [{capability.State}] active={(capability.Active ? "yes" : "no")}");
        }
        return builder.ToString();
    }

    public static string RenderShortTermMemory(ServiceShortTermMemorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Short-Term Memory");
        builder.AppendLine("=========================");
        builder.AppendLine($"RawEventCount    : {snapshot.Summary.RawEventCount}");
        builder.AppendLine($"WorkingItemCount : {snapshot.Summary.WorkingItemCount}");
        builder.AppendLine($"ActiveTasks      : {snapshot.Summary.ActiveTaskCount}");
        builder.AppendLine($"RecentDecisions  : {snapshot.Summary.RecentDecisionCount}");
        builder.AppendLine($"OpenQuestions    : {snapshot.Summary.OpenQuestionCount}");
        builder.AppendLine($"KnownIssues      : {snapshot.Summary.KnownIssueCount}");
        builder.AppendLine($"RecentWarnings   : {snapshot.Summary.RecentWarningCount}");
        AppendMaintenanceSection(builder, snapshot.Maintenance);
        AppendWorkingSection(builder, "ActiveTasks", snapshot.Summary.ActiveTasks);
        AppendWorkingSection(builder, "RecentDecisions", snapshot.Summary.RecentDecisions);
        AppendWorkingSection(builder, "OpenQuestions", snapshot.Summary.OpenQuestions);
        AppendWorkingSection(builder, "KnownIssues", snapshot.Summary.KnownIssues);
        AppendWorkingSection(builder, "RecentWarnings", snapshot.Summary.RecentWarnings);
        builder.AppendLine("LatestRawEvents");
        foreach (var item in snapshot.RawEvents)
        {
            builder.AppendLine($"- {item.EventId} [{item.EventKind}] seq={item.SequenceId} source={item.Source} tags={string.Join(',', item.Tags)}");
        }
        builder.AppendLine();
        builder.AppendLine(RenderShortTermArchiveSummary(snapshot.ArchiveSummary));
        builder.AppendLine();
        builder.AppendLine(RenderShortTermArchiveItems(snapshot.ArchiveItems));
        builder.AppendLine();
        builder.AppendLine(RenderShortTermCompactionRuns(snapshot.RecentRuns));
        return builder.ToString();
    }

    public static string RenderShortTermCompactionResult(ShortTermMemoryCompactionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Compaction Result");
        builder.AppendLine("===========================");
        builder.AppendLine($"Scope                  : {result.WorkspaceId}/{result.CollectionId} session={result.SessionId ?? "-"}");
        builder.AppendLine($"ActiveRawEvents        : {result.ActiveRawEventCountBefore} -> {result.ActiveRawEventCountAfter}");
        builder.AppendLine($"ActiveWorkingItems     : {result.ActiveWorkingItemCountBefore} -> {result.ActiveWorkingItemCountAfter}");
        builder.AppendLine($"MergedWorkingItems     : {result.MergedWorkingItems}");
        builder.AppendLine($"MergedByWorkingKey     : {result.MergedByWorkingKeyGroups}");
        builder.AppendLine($"MergedByTitle          : {result.MergedByTitleGroups}");
        builder.AppendLine($"ArchivedRawEvents      : {result.ArchivedRawEventCount}");
        builder.AppendLine($"ArchivedWorkingItems   : {result.ArchivedWorkingItemCount}");
        builder.AppendLine($"ArchivedResolvedItems  : {result.ArchivedResolvedWorkingItemCount}");
        builder.AppendLine($"EvidenceRefsTrimmed    : {result.EvidenceRefsTrimmed}");
        builder.AppendLine($"CompletedAt            : {result.CompletedAt:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString();
    }

    public static string RenderShortTermArchiveSummary(ShortTermArchiveSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Archive Summary");
        builder.AppendLine("==========================");
        builder.AppendLine($"Scope                   : {summary.WorkspaceId}/{summary.CollectionId ?? "-"} session={summary.SessionId ?? "-"}");
        builder.AppendLine($"ArchivedRawEvents       : {summary.ArchivedRawEventCount}");
        builder.AppendLine($"ArchivedWorkingItems    : {summary.ArchivedWorkingItemCount}");
        builder.AppendLine($"ArchivedResolvedItems   : {summary.ArchivedResolvedWorkingItemCount}");
        builder.AppendLine($"ArchivedActiveTasks     : {summary.ArchivedActiveTaskCount}");
        builder.AppendLine($"ArchivedDecisions       : {summary.ArchivedRecentDecisionCount}");
        builder.AppendLine($"ArchivedOpenQuestions   : {summary.ArchivedOpenQuestionCount}");
        builder.AppendLine($"ArchivedKnownIssues     : {summary.ArchivedKnownIssueCount}");
        builder.AppendLine($"ArchivedRecentWarnings  : {summary.ArchivedRecentWarningCount}");
        builder.AppendLine($"LatestArchivedAt        : {(summary.LatestArchivedAt is null ? "-" : summary.LatestArchivedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"))}");
        return builder.ToString();
    }

    public static string RenderShortTermArchiveItems(ShortTermArchiveItemsResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Archive Items");
        builder.AppendLine("========================");
        builder.AppendLine($"ArchivedRawCount        : {response.RawEvents.Count}");
        foreach (var item in response.RawEvents)
        {
            builder.AppendLine($"- RAW {item.EventId} [{item.EventKind}] {item.Source}");
        }

        builder.AppendLine($"ArchivedWorkingCount    : {response.WorkingItems.Count}");
        foreach (var item in response.WorkingItems)
        {
            builder.AppendLine($"- WORK {item.ItemId} [{item.Kind}/{item.Status}] {item.Summary}");
        }

        return builder.ToString();
    }

    public static string RenderShortTermCompactionRuns(IReadOnlyList<ShortTermCompactionRun> runs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Compaction Runs");
        builder.AppendLine("==========================");
        if (runs.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var run in runs)
        {
            builder.AppendLine($"- {run.RunId} [{run.Trigger}] {run.StartedAt:yyyy-MM-dd HH:mm:ss} dup={run.RemovedDuplicates} archiveRaw={run.ArchivedRawEvents} archiveWorking={run.ArchivedWorkingItems}");
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidates(ServicePromotionCandidatesSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Promotion Candidates");
        builder.AppendLine("============================");
        builder.AppendLine($"时间        : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务        : {snapshot.BaseUrl}");
        builder.AppendLine($"Candidates  : {snapshot.Candidates.Count}");
        builder.AppendLine($"Filters     : status={snapshot.Status?.ToString() ?? "-"} kind={snapshot.Kind ?? "-"} target={snapshot.SuggestedTargetLayer ?? "-"} minConf={snapshot.MinConfidence?.ToString("0.00") ?? "-"} minImp={snapshot.MinImportance?.ToString("0.00") ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var candidate in snapshot.Candidates)
        {
            builder.AppendLine($"- {candidate.CandidateId} [{candidate.Kind}/{candidate.Status}]");
            builder.AppendLine($"  title        : {candidate.Title}");
            builder.AppendLine($"  target       : {candidate.SuggestedTargetLayer}");
            builder.AppendLine($"  confidence   : {candidate.Confidence:0.00}");
            builder.AppendLine($"  importance   : {candidate.Importance:0.00}");
            builder.AppendLine($"  reason       : {candidate.Reason}");
            builder.AppendLine($"  evidenceRefs : {string.Join(", ", candidate.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateDetail(ShortTermPromotionCandidate candidate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Detail");
        builder.AppendLine("==========================");
        builder.AppendLine($"CandidateId      : {candidate.CandidateId}");
        builder.AppendLine($"SourceWorkingId  : {candidate.SourceWorkingItemId}");
        builder.AppendLine($"Kind             : {candidate.Kind}");
        builder.AppendLine($"Title            : {candidate.Title}");
        builder.AppendLine($"TargetLayer      : {candidate.SuggestedTargetLayer}");
        builder.AppendLine($"Status           : {candidate.Status}");
        builder.AppendLine($"Confidence       : {candidate.Confidence:0.00}");
        builder.AppendLine($"Importance       : {candidate.Importance:0.00}");
        builder.AppendLine($"Reason           : {candidate.Reason}");
        builder.AppendLine($"DedupeKey        : {candidate.DedupeKey}");
        builder.AppendLine($"SourceFingerprint: {candidate.SourceFingerprint}");
        builder.AppendLine($"GeneratedBy      : {candidate.GeneratedBy}");
        builder.AppendLine($"PolicyVersion    : {candidate.PolicyVersion}");
        builder.AppendLine($"Rule             : {candidate.RuleName} ({candidate.RuleVersion})");
        builder.AppendLine($"EvidenceRefs     : {string.Join(", ", candidate.EvidenceRefs)}");
        return builder.ToString();
    }

    public static string RenderPromotionCandidateExplanation(ShortTermPromotionCandidateExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Explain");
        builder.AppendLine("===========================");
        builder.AppendLine($"CandidateId      : {explanation.CandidateId}");
        builder.AppendLine($"TargetLayer      : {explanation.SuggestedTargetLayer}");
        builder.AppendLine($"Confidence       : {explanation.Confidence:0.00}");
        builder.AppendLine($"Importance       : {explanation.Importance:0.00}");
        builder.AppendLine($"Reason           : {explanation.Reason}");
        builder.AppendLine($"Rule             : {explanation.RuleName} ({explanation.RuleVersion})");
        builder.AppendLine($"PolicyVersion    : {explanation.PolicyVersion}");
        builder.AppendLine($"GeneratedBy      : {explanation.GeneratedBy}");
        builder.AppendLine($"DedupeKey        : {explanation.DedupeKey}");
        builder.AppendLine($"SourceFingerprint: {explanation.SourceFingerprint}");
        builder.AppendLine("SourceWorkingItem");
        builder.AppendLine($"- {explanation.SourceWorkingItem.ItemId} [{explanation.SourceWorkingItem.Kind}/{explanation.SourceWorkingItem.Status}] {explanation.SourceWorkingItem.Summary}");
        builder.AppendLine($"EvidenceRefs     : {string.Join(", ", explanation.EvidenceRefs)}");
        builder.AppendLine($"SourceRawEvents  : {explanation.SourceRawEvents.Count}");
        foreach (var item in explanation.SourceRawEvents)
        {
            builder.AppendLine($"- {item.EventId} [{item.EventKind}] {item.Source}");
        }
        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateReviewResult(PromotionCandidateReviewResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Review Result");
        builder.AppendLine("=================================");
        builder.AppendLine($"OperationId : {response.OperationId}");
        builder.AppendLine($"CandidateId : {response.CandidateId}");
        builder.AppendLine($"Action      : {response.Action}");
        builder.AppendLine($"Status      : {response.Status}");
        builder.AppendLine($"ReviewId    : {response.ReviewId}");
        builder.AppendLine($"Reviewer    : {response.Reviewer}");
        builder.AppendLine($"Reason      : {response.Reason}");
        builder.AppendLine($"ReviewedAt  : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"TargetId    : {response.CreatedTargetItemId ?? response.TargetItemId ?? "-"}");
        builder.AppendLine($"TargetKind  : {response.TargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateReviews(IReadOnlyList<PromotionCandidateReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Review History");
        builder.AppendLine("==================================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer    : {review.Reviewer}");
            builder.AppendLine($"  reason      : {review.Reason}");
            builder.AppendLine($"  target      : {review.TargetItemKind ?? "-"} {review.TargetItemId ?? "-"} layer={review.TargetLayer ?? "-"}");
            builder.AppendLine($"  evidenceRefs: {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt  : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidates(ServiceStableReviewCandidatesSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Review Candidates");
        builder.AppendLine("================================");
        builder.AppendLine($"时间        : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务        : {snapshot.BaseUrl}");
        builder.AppendLine($"Candidates  : {snapshot.Candidates.Count}");
        builder.AppendLine($"Filters     : status={snapshot.Status ?? "-"} validation={snapshot.ValidationStatus ?? "-"} kind={snapshot.Kind ?? "-"} target={snapshot.SuggestedStableTarget ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var candidate in snapshot.Candidates)
        {
            builder.AppendLine($"- {candidate.StableReviewCandidateId} [{candidate.Kind}/{candidate.Status}/{candidate.ValidationStatus}]");
            builder.AppendLine($"  title        : {candidate.Title}");
            builder.AppendLine($"  stableTarget : {candidate.SuggestedStableTarget}");
            builder.AppendLine($"  source       : candidate={candidate.SourceCandidateId} target={candidate.SourceTargetItemId} learningCase={candidate.SourceLearningCaseId ?? "-"}");
            builder.AppendLine($"  confidence   : {candidate.Confidence:0.00}");
            builder.AppendLine($"  importance   : {candidate.Importance:0.00}");
            builder.AppendLine($"  riskFlags    : {(candidate.RiskFlags.Count == 0 ? "-" : string.Join(", ", candidate.RiskFlags))}");
            builder.AppendLine($"  evidenceRefs : {string.Join(", ", candidate.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidateDetail(StableReviewCandidate candidate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Candidate Detail");
        builder.AppendLine("==============================");
        builder.AppendLine($"StableReviewCandidateId : {candidate.StableReviewCandidateId}");
        builder.AppendLine($"SourceCandidateId       : {candidate.SourceCandidateId}");
        builder.AppendLine($"SourceTargetItemId      : {candidate.SourceTargetItemId}");
        builder.AppendLine($"SourceLearningCaseId    : {candidate.SourceLearningCaseId ?? "-"}");
        builder.AppendLine($"Kind                    : {candidate.Kind}");
        builder.AppendLine($"SuggestedStableTarget   : {candidate.SuggestedStableTarget}");
        builder.AppendLine($"Status                  : {candidate.Status}");
        builder.AppendLine($"ValidationStatus        : {candidate.ValidationStatus}");
        builder.AppendLine($"RiskFlags               : {(candidate.RiskFlags.Count == 0 ? "-" : string.Join(", ", candidate.RiskFlags))}");
        builder.AppendLine($"Confidence              : {candidate.Confidence:0.00}");
        builder.AppendLine($"Importance              : {candidate.Importance:0.00}");
        builder.AppendLine($"Reason                  : {candidate.Reason}");
        builder.AppendLine($"EvidenceRefs            : {string.Join(", ", candidate.EvidenceRefs)}");
        return builder.ToString();
    }

    public static string RenderStableReviewCandidateExplanation(StableReviewCandidateExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Candidate Explain");
        builder.AppendLine("===============================");
        builder.AppendLine($"StableReviewCandidateId : {explanation.StableReviewCandidateId}");
        builder.AppendLine($"ValidationStatus        : {explanation.ValidationStatus}");
        builder.AppendLine($"RiskFlags               : {(explanation.RiskFlags.Count == 0 ? "-" : string.Join(", ", explanation.RiskFlags))}");
        builder.AppendLine($"Reason                  : {explanation.Reason}");
        builder.AppendLine("Source Promotion Candidate");
        builder.AppendLine($"- {explanation.SourceCandidate.CandidateId} [{explanation.SourceCandidate.Kind}/{explanation.SourceCandidate.Status}] target={explanation.SourceCandidate.SuggestedTargetLayer}");
        builder.AppendLine($"  title    : {explanation.SourceCandidate.Title}");
        builder.AppendLine($"  evidence : {string.Join(", ", explanation.SourceCandidate.EvidenceRefs)}");
        if (explanation.SourceLearningCase is not null)
        {
            builder.AppendLine("Source Learning Case");
            builder.AppendLine($"- {explanation.SourceLearningCase.CaseId} [{explanation.SourceLearningCase.CaseKind}/{explanation.SourceLearningCase.Status}]");
            builder.AppendLine($"  evidence : {string.Join(", ", explanation.SourceLearningCase.EvidenceRefs)}");
        }

        if (explanation.SourceMemoryTarget is not null)
        {
            builder.AppendLine("Source Target Memory");
            builder.AppendLine($"- {explanation.SourceMemoryTarget.Id} [{explanation.SourceMemoryTarget.Layer}/{explanation.SourceMemoryTarget.Status}/{explanation.SourceMemoryTarget.Type}]");
            builder.AppendLine($"  sourceRefs: {string.Join(", ", explanation.SourceMemoryTarget.SourceRefs)}");
        }

        if (explanation.SourceConstraintTarget is not null)
        {
            builder.AppendLine("Source Target Constraint");
            builder.AppendLine($"- {explanation.SourceConstraintTarget.Id} [{explanation.SourceConstraintTarget.Level}/{explanation.SourceConstraintTarget.Status}]");
            builder.AppendLine($"  sourceRefs: {string.Join(", ", explanation.SourceConstraintTarget.SourceRefs)}");
        }

        builder.AppendLine($"EvidenceRefs            : {string.Join(", ", explanation.EvidenceRefs)}");
        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableReviewDecisionResult(StableReviewDecisionResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Decision Result");
        builder.AppendLine("=============================");
        builder.AppendLine($"OperationId             : {response.OperationId}");
        builder.AppendLine($"StableReviewCandidateId : {response.StableReviewCandidateId}");
        builder.AppendLine($"Action                  : {response.Action}");
        builder.AppendLine($"Status                  : {response.Status}");
        builder.AppendLine($"ValidationStatus        : {response.ValidationStatus}");
        builder.AppendLine($"ReviewId                : {response.ReviewId}");
        builder.AppendLine($"Reviewer                : {response.Reviewer}");
        builder.AppendLine($"Reason                  : {response.Reason}");
        builder.AppendLine($"ReviewedAt              : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"StableTargetId          : {response.CreatedStableTargetItemId ?? response.CreatedTargetItemId ?? "-"}");
        builder.AppendLine($"StableTargetKind        : {response.StableTargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer             : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidateReviews(IReadOnlyList<StableReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Decision History");
        builder.AppendLine("==============================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer       : {review.Reviewer}");
            builder.AppendLine($"  reason         : {review.Reason}");
            builder.AppendLine($"  validation     : {review.ValidationStatus}");
            builder.AppendLine($"  riskFlags      : {(review.RiskFlags.Count == 0 ? "-" : string.Join(", ", review.RiskFlags))}");
            builder.AppendLine($"  stableTarget   : {review.StableTargetItemKind ?? "-"} {review.StableTargetItemId ?? "-"} layer={review.TargetLayer ?? "-"}");
            builder.AppendLine($"  source         : promotion={review.SourcePromotionCandidateId} target={review.SourceTargetItemId} learningCase={review.SourceLearningCaseId ?? "-"}");
            builder.AppendLine($"  evidenceRefs   : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt     : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderLearning(ServiceLearningSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Context Learning");
        builder.AppendLine("========================");
        builder.AppendLine($"时间     : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务     : {snapshot.BaseUrl}");
        builder.AppendLine($"Feedback : {snapshot.FeedbackSignals.Count}");
        builder.AppendLine($"Records  : {snapshot.Records.Count}");
        builder.AppendLine($"Cases    : {snapshot.Cases.Count}");
        builder.AppendLine($"Signals  : positive={snapshot.PositiveCount} negative={snapshot.NegativeCount} stale={snapshot.StaleCount}");
        if (snapshot.Summary is not null)
        {
            builder.AppendLine($"Summary  : records={snapshot.Summary.RecordCount} cases={snapshot.Summary.CaseCount}");
            builder.AppendLine($"Statuses : draft={snapshot.Summary.DraftCaseCount} candidate={snapshot.Summary.CandidateCaseCount} activeRegression={snapshot.Summary.ActiveRegressionCaseCount} archived={snapshot.Summary.ArchivedCaseCount} rejected={snapshot.Summary.RejectedCaseCount}");
        }

        if (snapshot.LastGeneration is not null)
        {
            builder.AppendLine($"Generation: scanned={snapshot.LastGeneration.RecordsScanned} created={snapshot.LastGeneration.Created} existing={snapshot.LastGeneration.Existing}");
        }

        if (snapshot.LastStatusUpdate is not null)
        {
            builder.AppendLine($"LastUpdate: {snapshot.LastStatusUpdate.CaseId} -> {snapshot.LastStatusUpdate.Status} op={snapshot.LastStatusUpdate.OperationId}");
        }

        builder.AppendLine();
        builder.AppendLine("Failure Types");
        var failureTypes = snapshot.Summary?.FailureTypeCounts ?? snapshot.FailureTypeSummary;
        if (failureTypes.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in failureTypes.OrderBy(pair => pair.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Case Kinds");
        if (snapshot.Summary is null || snapshot.Summary.CaseKindCounts.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in snapshot.Summary.CaseKindCounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Active Regression Cases");
        if (snapshot.RegressionCases.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var learningCase in snapshot.RegressionCases.Take(10))
            {
                builder.AppendLine($"- {learningCase.CaseId} [{learningCase.CaseKind}] {learningCase.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Promotion Feedback Signals");
        if (snapshot.FeedbackSignals.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var feedback in snapshot.FeedbackSignals.Take(20))
            {
                builder.AppendLine($"- {feedback.FeedbackId} [{feedback.Action}] candidate={feedback.CandidateId}");
                builder.AppendLine($"  reviewer : {feedback.Reviewer}");
                builder.AppendLine($"  target   : suggested={feedback.SuggestedTargetLayer} actual={feedback.ActualTargetLayer ?? "-"} created={feedback.CreatedTargetItemId ?? "-"}");
                builder.AppendLine($"  reason   : {feedback.Reason}");
                builder.AppendLine($"  evidence : {string.Join(", ", feedback.EvidenceRefs)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Feedback");
        if (snapshot.Records.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var record in snapshot.Records.Take(20))
            {
                builder.AppendLine($"- {record.RecordId} [{record.Signal}/{record.FailureType}] {record.EventKind}");
                builder.AppendLine($"  source   : {record.SourceKind}/{record.SourceId}");
                builder.AppendLine($"  candidate: {record.CandidateId ?? "-"} review={record.ReviewId ?? "-"}");
                builder.AppendLine($"  reason   : {record.Reason}");
                builder.AppendLine($"  evidence : {string.Join(", ", record.EvidenceRefs)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Learning Cases");
        if (snapshot.Cases.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var learningCase in snapshot.Cases.Take(20))
            {
                builder.AppendLine($"- {learningCase.CaseId} [{learningCase.CaseKind}/{learningCase.Signal}/{learningCase.FailureType}/{learningCase.Status}]");
                builder.AppendLine($"  title    : {learningCase.Title}");
                builder.AppendLine($"  source   : {learningCase.SourceKind}/{learningCase.SourceId} record={learningCase.SourceRecordId}");
                builder.AppendLine($"  evidence : {string.Join(", ", learningCase.EvidenceRefs)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPolicyFeedbackDataset(ServicePolicyFeedbackDatasetSnapshot snapshot)
    {
        var dataset = snapshot.Dataset;
        var builder = new StringBuilder();
        builder.AppendLine("Service Policy Feedback Dataset");
        builder.AppendLine("================================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Dataset    : {dataset.DatasetId}");
        builder.AppendLine($"Name       : {dataset.Name}");
        builder.AppendLine($"Scope      : {dataset.Scope}");
        builder.AppendLine($"Policy     : {dataset.PolicyVersion}");
        builder.AppendLine($"Baseline   : {dataset.EvalBaselineRef}");
        builder.AppendLine($"Page       : offset={snapshot.Offset} limit={snapshot.Limit} records={dataset.Records.Count}");
        builder.AppendLine($"Labels     : positive={dataset.PositiveCount} negative={dataset.NegativeCount} neutral={dataset.NeutralCount}");

        builder.AppendLine();
        builder.AppendLine("Source Types");
        if (dataset.SourceTypes.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in dataset.SourceTypes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Policy Feedback Records");
        if (dataset.Records.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var record in dataset.Records.Take(20))
            {
                builder.AppendLine($"- {record.FeedbackRecordId} [{record.Label}/{record.Action}] {record.SourceType}/{record.SourceId}");
                builder.AppendLine($"  workspace : {record.WorkspaceId} collection={record.CollectionId} session={record.SessionId ?? "-"}");
                builder.AppendLine($"  reviewer  : {record.Reviewer}");
                builder.AppendLine($"  target    : {record.TargetLayer}");
                builder.AppendLine($"  reason    : {record.Reason}");
                builder.AppendLine($"  positive  : {string.Join(", ", record.PositiveRefs)}");
                builder.AppendLine($"  negative  : {string.Join(", ", record.NegativeRefs)}");
                builder.AppendLine($"  evidence  : {string.Join(", ", record.EvidenceRefs)}");
                builder.AppendLine($"  createdAt : {record.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            }
        }

        return builder.ToString();
    }

    public static string RenderLearningFeatures(ServiceLearningFeaturesSnapshot snapshot)
    {
        var dataset = snapshot.Dataset;
        var builder = new StringBuilder();
        builder.AppendLine("Service Learning Features");
        builder.AppendLine("=========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Dataset    : {dataset.DatasetId}");
        builder.AppendLine($"Policy     : {dataset.PolicyVersion}");
        builder.AppendLine($"Page       : offset={snapshot.Offset} limit={snapshot.Limit} records={dataset.FeatureExamples.Count}");
        builder.AppendLine($"Counts     : features={dataset.FeatureCount} rankingPairs={dataset.RankingPairCount} routerIntent={dataset.RouterIntentExampleCount}");
        builder.AppendLine($"LatestExport: {(string.IsNullOrWhiteSpace(dataset.LatestExportPath) ? "-" : dataset.LatestExportPath)}");

        var quality = snapshot.QualityReport;
        builder.AppendLine();
        builder.AppendLine("Dataset Quality");
        builder.AppendLine($"- counts : policy={quality.PolicyFeedbackFeatureCount} rankingPairs={quality.RankingPairCount} routerIntent={quality.RouterIntentExampleCount}");
        builder.AppendLine($"- labels : positive={quality.PositiveCount} negative={quality.NegativeCount} neutral={quality.NeutralCount}");
        builder.AppendLine($"- risks  : {(quality.DataRisks.Count == 0 ? "-" : string.Join(", ", quality.DataRisks))}");
        builder.AppendLine($"- next   : {(string.IsNullOrWhiteSpace(quality.RecommendedNextAction) ? "-" : quality.RecommendedNextAction)}");

        var feedback = snapshot.LearningFeedbackSummary;
        builder.AppendLine();
        builder.AppendLine("Runtime Feedback");
        builder.AppendLine($"- count  : {feedback.FeedbackCount}");
        builder.AppendLine($"- metadataOnly: {feedback.MetadataOnlyCount}");
        builder.AppendLine($"- trainingDisabled: {feedback.TrainingUseDisabledCount}");
        builder.AppendLine($"- export : {(string.IsNullOrWhiteSpace(feedback.ExportPath) ? "-" : feedback.ExportPath)}");
        builder.AppendLine($"- cap    : {FormatDictionaryCompact(feedback.FeedbackByCapability)}");
        builder.AppendLine($"- kind   : {FormatDictionaryCompact(feedback.FeedbackByKind)}");
        builder.AppendLine($"- target : {FormatDictionaryCompact(feedback.FeedbackByTargetType)}");
        var reviewSummary = snapshot.LearningFeedbackReviewSummary;
        builder.AppendLine($"- review : pending={reviewSummary.PendingReviewCount} approved={reviewSummary.ApprovedCount} rejected={reviewSummary.RejectedCount} needsRedaction={reviewSummary.NeedsRedactionCount} needsEvidence={reviewSummary.NeedsMoreEvidenceCount}");
        var candidateReport = snapshot.LearningFeedbackFeatureCandidateReport;
        if (candidateReport is null)
        {
            builder.AppendLine("- candidates: not generated; run eval learning-feedback-feature-candidates");
        }
        else
        {
            builder.AppendLine($"- candidates: generated={candidateReport.GeneratedCandidateCount} scanned={candidateReport.FeedbackScanned} byCap={FormatDictionaryCompact(candidateReport.CandidatesByCapability)}");
        }

        var qualityReport = snapshot.LearningFeedbackQualityReport;
        if (qualityReport is null)
        {
            builder.AppendLine("- quality: not generated; run eval learning-feedback-quality");
        }
        else
        {
            builder.AppendLine($"- quality: review={qualityReport.ReviewCoverageRate:P2} redaction={qualityReport.RedactionCoverageRate:P2} rec={qualityReport.Recommendation}");
            foreach (var readiness in qualityReport.ApprovedDatasetReadiness.Take(6))
            {
                builder.AppendLine($"  - {readiness.CapabilityId}: ready={readiness.Ready} candidates={readiness.ApprovedCandidateCount} blocked={(readiness.BlockedReasons.Count == 0 ? "-" : string.Join(", ", readiness.BlockedReasons))}");
            }
        }

        var approvedGate = snapshot.LearningApprovedFeedbackDatasetGateReport;
        if (approvedGate is null)
        {
            builder.AppendLine("- approved gate: not generated; run eval learning-approved-feedback-dataset-gate");
        }
        else
        {
            builder.AppendLine($"- approved gate: passed={approvedGate.Passed} trainable={approvedGate.TrainableCandidateCount} smokeExcluded={approvedGate.SmokeExcludedCount} rec={approvedGate.Recommendation}");
            if (approvedGate.FailureReasons.Count > 0)
            {
                builder.AppendLine($"  - fail: {string.Join(", ", approvedGate.FailureReasons.Take(5))}");
            }
        }

        if (feedback.RecentFeedback.Count == 0)
        {
            builder.AppendLine("- recent : (empty)");
        }
        else
        {
            foreach (var item in feedback.RecentFeedback.Take(5))
            {
                builder.AppendLine($"- recent : {item.FeedbackId} {item.CapabilityId}/{item.FeedbackKind} target={FormatEmpty(item.TargetType)}:{FormatEmpty(item.TargetId)}");
            }
        }

        builder.AppendLine("Task Readiness");
        if (quality.TaskReadiness.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in quality.TaskReadiness.Values.OrderBy(item => item.TaskName, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {item.TaskName}: {item.Status} ready={(item.Ready ? "yes" : "no")}");
                builder.AppendLine($"  next    : {item.RecommendedNextAction}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Learning Readiness Dashboard");
        var readinessRegistry = snapshot.LearningReadinessRegistry;
        var runtimeGate = snapshot.LearningRuntimeChangeReadinessGateReport;
        if (readinessRegistry is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(LearningReadinessFreezeRunner.DefaultOutputDirectory, LearningReadinessFreezeRunner.FreezeReportFileName)}");
            builder.AppendLine("- action : run eval learning-readiness-freeze-report");
        }
        else
        {
            builder.AppendLine($"- ready  : {readinessRegistry.ReadyCount}");
            builder.AppendLine($"- blocked: {readinessRegistry.BlockedCount}");
            builder.AppendLine($"- rec    : {readinessRegistry.OverallRecommendation}");
            foreach (var capability in readinessRegistry.Capabilities.OrderBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {capability.CapabilityId}: status={capability.Status} gate={capability.GatePassed} rec={capability.Recommendation}");
                builder.AppendLine($"  blocked : {(capability.BlockedReasons.Count == 0 ? "-" : string.Join(", ", capability.BlockedReasons))}");
                builder.AppendLine($"  allowed : {(capability.AllowedRuntimeModes.Count == 0 ? "-" : string.Join(", ", capability.AllowedRuntimeModes))}");
                builder.AppendLine($"  forbidden: {(capability.ForbiddenRuntimeModes.Count == 0 ? "-" : string.Join(", ", capability.ForbiddenRuntimeModes))}");
                builder.AppendLine($"  report  : {(string.IsNullOrWhiteSpace(capability.LastEvalReportPath) ? "-" : capability.LastEvalReportPath)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Foundation Freeze Summary");
        var foundation = snapshot.FoundationFreezeReport;
        if (foundation is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(ContextCoreFoundationFreezeRunner.DefaultOutputDirectory, "foundation-release-candidate-gate.json")}");
            builder.AppendLine("- action : run eval foundation-release-candidate-gate");
        }
        else
        {
            builder.AppendLine($"- freeze : {foundation.FreezePassed}");
            builder.AppendLine($"- rec    : {foundation.Recommendation}");
            builder.AppendLine($"- foundation/storage/vector: {foundation.ContextCoreFoundation} / {foundation.StorageFoundation} / {foundation.VectorFoundation}");
            builder.AppendLine($"- next   : {foundation.NextAllowedPhase}");
            builder.AppendLine($"- relation/feedback/job/vector: {foundation.RelationGovernanceStatus} / {foundation.LearningFeedbackStatus} / {foundation.JobQueueStatus} / {foundation.VectorPostgresProviderStatus}");
            builder.AppendLine($"- formal preview: {foundation.VectorFormalPreviewStatus}");
            builder.AppendLine($"- runtime gate/P15: {foundation.RuntimeChangeGateStatus} / {foundation.P15GateStatus}");
            builder.AppendLine($"- runtime/formal allowed: {foundation.RuntimeSwitchAllowed} / {foundation.FormalRetrievalAllowed}");
            builder.AppendLine($"- package/policy changed: {foundation.PackageOutputChanged} / {foundation.PackingPolicyChanged}");
            builder.AppendLine($"- missing reports/docs: {foundation.MissingReportCount} / {foundation.MissingDocCount}");
            builder.AppendLine($"- blocked: {(foundation.BlockedReasons.Count == 0 ? "-" : string.Join(", ", foundation.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Unified Foundation / Runtime Gate / Vector Formal Preview / Storage Freeze Summary");
        var foundationStatus = snapshot.FoundationServiceStatus;
        if (foundationStatus is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine("- action : run eval foundation-reproducibility-check or call /api/admin/foundation/status");
        }
        else
        {
            builder.AppendLine($"- read-only: {foundationStatus.ReadOnly}");
            builder.AppendLine($"- foundation/runtime/repro: {foundationStatus.FoundationGateStatus} / {foundationStatus.RuntimeChangeGateStatus} / {foundationStatus.ReproducibilityStatus}");
            builder.AppendLine($"- vector formal/postgres freeze: {foundationStatus.VectorFormalPreviewStatus} / {foundationStatus.PostgresFreezeStatus}");
            builder.AppendLine($"- runtime/formal/ready-switch: {foundationStatus.RuntimeSwitchAllowed} / {foundationStatus.FormalRetrievalAllowed} / {foundationStatus.ReadyForRuntimeSwitch}");
            builder.AppendLine($"- package/policy/runtime mutated: {foundationStatus.PackageOutputChanged} / {foundationStatus.PackingPolicyChanged} / {foundationStatus.RuntimeMutated}");
            builder.AppendLine($"- capabilities: {foundationStatus.Capabilities.Count}");
            foreach (var capability in foundationStatus.Capabilities.OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"  - {capability.Category}/{capability.CapabilityId}: gate={capability.GatePassed} state={capability.State} rec={capability.Recommendation}");
            }
            builder.AppendLine($"- blocked: {(foundationStatus.BlockedReasons.Count == 0 ? "-" : string.Join(", ", foundationStatus.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Service API Hardening Summary");
        var securityDiagnostics = snapshot.FoundationApiSecurityDiagnostics;
        var reportNavigation = snapshot.FoundationReportNavigation;
        if (securityDiagnostics is null)
        {
            builder.AppendLine("- auth configured : unknown");
            builder.AppendLine("- action          : run eval service-api-security-diagnostics");
        }
        else
        {
            builder.AppendLine($"- auth configured : {securityDiagnostics.AuthConfigured}");
            builder.AppendLine($"- api key configured: {securityDiagnostics.ApiKeyConfigured}");
            builder.AppendLine($"- development mode: {securityDiagnostics.DevelopmentMode}");
            builder.AppendLine($"- secret leak     : {securityDiagnostics.SecretLeakDetected}");
            builder.AppendLine($"- absolute path leak: {securityDiagnostics.AbsolutePathLeakDetected}");
            builder.AppendLine($"- security rec    : {securityDiagnostics.Recommendation}");
        }

        if (reportNavigation is null)
        {
            builder.AppendLine("- reports         : unknown");
            builder.AppendLine("- action          : run eval service-report-navigation-smoke or call /api/admin/foundation/reports");
        }
        else
        {
            builder.AppendLine($"- reports         : {reportNavigation.ExistingReportCount}/{reportNavigation.ReportCount}");
            builder.AppendLine($"- degraded reports: {reportNavigation.DegradedReportCount}");
            builder.AppendLine($"- navigation rec  : {reportNavigation.Recommendation}");
            foreach (var report in reportNavigation.Reports.Take(5))
            {
                builder.AppendLine($"  - {report.ReportId}: exists={report.Exists} safe={report.SafeToExpose} path={report.RelativePath}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Service API Contract Freeze Summary");
        var contractReport = snapshot.FoundationApiContractReport;
        if (contractReport is null)
        {
            builder.AppendLine("- status          : not generated");
            builder.AppendLine("- action          : run eval service-api-contract-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- freeze passed   : {contractReport.FreezePassed}");
            builder.AppendLine($"- endpoints       : {contractReport.EndpointCount}");
            builder.AppendLine($"- client methods  : {contractReport.ClientMethodCount}");
            builder.AppendLine($"- schema version  : {contractReport.EnvelopeSchemaVersion}");
            builder.AppendLine($"- auth mode       : {contractReport.AuthMode}");
            builder.AppendLine($"- degraded stable : {contractReport.DegradedBehaviorStable}");
            builder.AppendLine($"- forbidden exposed: {contractReport.ForbiddenActionsExposed}");
            builder.AppendLine($"- runtime/formal  : {contractReport.RuntimeSwitchAllowed} / {contractReport.FormalRetrievalAllowed}");
            builder.AppendLine($"- recommendation  : {contractReport.Recommendation}");
            builder.AppendLine($"- blocked         : {(contractReport.BlockedReasons.Count == 0 ? "-" : string.Join(", ", contractReport.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Service Auth / Deployment Profile Summary");
        var authDiagnostics = snapshot.FoundationServiceAuthDiagnostics;
        var deploymentGate = snapshot.FoundationServiceDeploymentProfileGate;
        if (authDiagnostics is null)
        {
            builder.AppendLine("- auth diagnostics: not generated");
            builder.AppendLine("- action          : run eval service-auth-diagnostics");
        }
        else
        {
            builder.AppendLine($"- profile         : {authDiagnostics.DeploymentProfile}");
            builder.AppendLine($"- auth configured : {authDiagnostics.AuthConfigured}");
            builder.AppendLine($"- api key configured: {authDiagnostics.ApiKeyConfigured}");
            builder.AppendLine($"- require api key : {authDiagnostics.RequireApiKey}");
            builder.AppendLine($"- dev no-auth     : {authDiagnostics.DevelopmentNoAuthAllowed}");
            builder.AppendLine($"- secret leak     : {authDiagnostics.SecretLeakDetected}");
            builder.AppendLine($"- absolute path leak: {authDiagnostics.AbsolutePathLeakDetected}");
            builder.AppendLine($"- auth rec        : {authDiagnostics.Recommendation}");
        }

        if (deploymentGate is null)
        {
            builder.AppendLine("- deployment gate : not generated");
            builder.AppendLine("- action          : run eval service-deployment-profile-gate");
        }
        else
        {
            builder.AppendLine($"- deployment gate : {deploymentGate.GatePassed}");
            builder.AppendLine($"- production status: profile={deploymentGate.DeploymentProfile} rec={deploymentGate.Recommendation}");
            builder.AppendLine($"- blocked         : {(deploymentGate.BlockedReasons.Count == 0 ? "-" : string.Join(", ", deploymentGate.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Service OpenAPI / Client Contract Snapshot Summary");
        var openApiContract = snapshot.FoundationOpenApiContractReport;
        if (openApiContract is null)
        {
            builder.AppendLine("- status          : not generated");
            builder.AppendLine("- action          : run eval service-api-contract-drift-gate");
        }
        else
        {
            builder.AppendLine($"- endpoints       : {openApiContract.EndpointCount}");
            builder.AppendLine($"- client methods  : {openApiContract.ClientMethodCount}");
            builder.AppendLine($"- schema version  : {openApiContract.EnvelopeSchemaVersion}");
            builder.AppendLine($"- auth scheme     : {openApiContract.AuthScheme}");
            builder.AppendLine($"- drift detected  : {openApiContract.BreakingChangeDetected}");
            builder.AppendLine($"- secret/path leak: {openApiContract.SecretLeakDetected} / {openApiContract.AbsolutePathLeakDetected}");
            builder.AppendLine($"- recommendation  : {openApiContract.Recommendation}");
            builder.AppendLine($"- blocked         : {(openApiContract.BlockedReasons.Count == 0 ? "-" : string.Join(", ", openApiContract.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Hosted Service Smoke Summary");
        var hostedSmoke = snapshot.HostedServiceSmokeReport;
        if (hostedSmoke is null)
        {
            builder.AppendLine("- status          : not generated");
            builder.AppendLine("- action          : run eval service-hosted-deployment-smoke");
        }
        else
        {
            builder.AppendLine($"- base url        : {hostedSmoke.BaseUrl}");
            builder.AppendLine($"- profile         : {hostedSmoke.DeploymentProfile}");
            builder.AppendLine($"- endpoints       : {hostedSmoke.SuccessfulEndpointCount}/{hostedSmoke.EndpointCount}");
            builder.AppendLine($"- auth            : {hostedSmoke.AuthPassed} unauthorized={hostedSmoke.UnauthorizedCheckPassed}");
            builder.AppendLine($"- envelope        : {hostedSmoke.EnvelopeSchemaMatched}");
            builder.AppendLine($"- runtime mutated : {hostedSmoke.RuntimeMutated}");
            builder.AppendLine($"- formal/runtime  : {hostedSmoke.FormalRetrievalAllowed} / {hostedSmoke.RuntimeSwitchAllowed}");
            builder.AppendLine($"- recommendation  : {hostedSmoke.Recommendation}");
            builder.AppendLine($"- blocked         : {(hostedSmoke.BlockedReasons.Count == 0 ? "-" : string.Join(", ", hostedSmoke.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Service Foundation Freeze Status");
        var serviceFreeze = snapshot.ServiceFoundationFreezeReport;
        if (serviceFreeze is null)
        {
            builder.AppendLine("- status          : not generated");
            builder.AppendLine("- action          : run eval service-foundation-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- freeze passed   : {serviceFreeze.FreezePassed}");
            builder.AppendLine($"- service foundation: {serviceFreeze.ServiceFoundation}");
            builder.AppendLine($"- foundation api  : {serviceFreeze.FoundationApi}");
            builder.AppendLine($"- openapi contract: {serviceFreeze.OpenApiContract}");
            builder.AppendLine($"- auth profile    : {serviceFreeze.AuthDeploymentProfile}");
            builder.AppendLine($"- svc1-svc6       : {serviceFreeze.Svc1ReadOnlyFoundationApiPassed}/{serviceFreeze.Svc2ServiceHardeningPassed}/{serviceFreeze.Svc3ApiContractFreezePassed}/{serviceFreeze.Svc4AuthDeploymentProfilePassed}/{serviceFreeze.Svc5OpenApiContractSnapshotPassed}/{serviceFreeze.Svc6HostedReadOnlySmokePassed}");
            builder.AppendLine($"- hosted smoke    : {serviceFreeze.HostedSmokeRecommendation}");
            builder.AppendLine($"- contract drift  : {serviceFreeze.ContractDriftRecommendation}");
            builder.AppendLine($"- runtime mutation: {serviceFreeze.RuntimeMutationAllowed}");
            builder.AppendLine($"- formal/runtime  : {serviceFreeze.FormalRetrievalAllowed} / {serviceFreeze.RuntimeSwitchAllowed}");
            builder.AppendLine($"- recommendation  : {serviceFreeze.Recommendation}");
            builder.AppendLine($"- next phase      : {serviceFreeze.NextAllowedPhase}");
            builder.AppendLine($"- blocked         : {(serviceFreeze.BlockedReasons.Count == 0 ? "-" : string.Join(", ", serviceFreeze.BlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Learning Runtime Change Gate");
        if (runtimeGate is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(LearningReadinessFreezeRunner.DefaultOutputDirectory, LearningReadinessFreezeRunner.RuntimeGateFileName)}");
            builder.AppendLine("- action : run eval learning-runtime-change-readiness-gate");
        }
        else
        {
            builder.AppendLine($"- passed : {runtimeGate.Passed}");
            builder.AppendLine($"- rec    : {runtimeGate.Recommendation}");
            builder.AppendLine($"- failed : {(runtimeGate.FailedConditions.Count == 0 ? "-" : string.Join(", ", runtimeGate.FailedConditions))}");
        }

        builder.AppendLine();
        builder.AppendLine("Router Intent Baseline");
        var routerReport = snapshot.RouterIntentBaselineReport;
        if (routerReport is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(RouterIntentEvaluationRunner.DefaultOutputDirectory, RouterIntentEvaluationRunner.ReportFileName)}");
        }
        else
        {
            builder.AppendLine($"- status : {routerReport.Status}");
            builder.AppendLine($"- samples: {routerReport.SampleCount}");
            builder.AppendLine($"- best   : {(string.IsNullOrWhiteSpace(routerReport.BestBaseline) ? "-" : routerReport.BestBaseline)}");
            builder.AppendLine($"- rec    : {routerReport.Recommendation}");
            foreach (var baseline in routerReport.Baselines.OrderBy(item => item.BaselineName, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {baseline.BaselineName}: accuracy={baseline.Accuracy:P2} macroF1={baseline.MacroF1:0.####} lowConfidence={baseline.LowConfidenceCount} abstain={baseline.AbstainCount}");
                builder.AppendLine($"  recalls : current={baseline.CurrentTaskRecall:P2} fuzzy={baseline.FuzzyQuestionRecall:P2} coding={baseline.CodingTaskRecall:P2} novel={baseline.NovelGenerationRecall:P2} automation={baseline.AutomationRecoveryRecall:P2}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Router Shadow Summary");
        var routerShadow = snapshot.RouterShadowTraceQualityReport;
        if (routerShadow is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(RouterIntentShadowReportBuilder.DefaultOutputDirectory, RouterIntentShadowReportBuilder.TraceQualityReportFileName)}");
            builder.AppendLine("- action : run eval router-shadow-trace-quality");
        }
        else
        {
            builder.AppendLine($"- traces : {routerShadow.TraceCount}");
            builder.AppendLine($"- agree  : {routerShadow.AgreementRate:P2}");
            builder.AppendLine($"- disagree: {routerShadow.DisagreementRate:P2}");
            builder.AppendLine($"- lowConf: {routerShadow.LowConfidenceCount}");
            builder.AppendLine($"- abstain: {routerShadow.AbstainCount}");
            builder.AppendLine($"- rec    : {routerShadow.Recommendation}");
            foreach (var pair in routerShadow.TopConfusionPairs
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- confusion: {pair.Key} = {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Router Disagreement Triage Summary");
        var routerTriageA3 = snapshot.RouterDisagreementTriageA3Report;
        var routerTriageExtended = snapshot.RouterDisagreementTriageExtendedReport;
        if (routerTriageA3 is null && routerTriageExtended is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(RouterDisagreementTriageRunner.DefaultOutputDirectory, RouterDisagreementTriageRunner.A3ReportFileName)}");
            builder.AppendLine("- action : run eval router-disagreement-triage");
        }
        else
        {
            if (routerTriageA3 is not null)
            {
                builder.AppendLine($"- A3     : disagreements={routerTriageA3.DisagreementCount} fixes={routerTriageA3.ShadowFixesRuntime} breaks={routerTriageA3.ShadowBreaksRuntime} rec={routerTriageA3.Recommendation}");
            }

            if (routerTriageExtended is not null)
            {
                builder.AppendLine($"- Ext    : disagreements={routerTriageExtended.DisagreementCount} fixes={routerTriageExtended.ShadowFixesRuntime} breaks={routerTriageExtended.ShadowBreaksRuntime} rec={routerTriageExtended.Recommendation}");
            }

            builder.AppendLine($"- hard negatives: {snapshot.RouterHardNegativeCount}");
            var confusionPairs = (routerTriageA3?.TopConfusionPairs ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
                .Concat(routerTriageExtended?.TopConfusionPairs ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Sum(item => item.Value)))
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5);
            foreach (var pair in confusionPairs)
            {
                builder.AppendLine($"- confusion: {pair.Key} = {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Router Opt-in Readiness Summary");
        var routerGate = snapshot.RouterGuardedOptInReadinessGateReport;
        if (routerGate is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(RouterGuardedOptInReadinessGateRunner.DefaultOutputDirectory, RouterGuardedOptInReadinessGateRunner.ReportFileName)}");
            builder.AppendLine("- action : run eval router-guarded-optin-readiness-gate");
        }
        else
        {
            builder.AppendLine($"- passed : {routerGate.Passed}");
            builder.AppendLine($"- fixes  : {routerGate.ShadowFixesRuntime}");
            builder.AppendLine($"- breaks : {routerGate.ShadowBreaksRuntime}");
            builder.AppendLine($"- netGain: {routerGate.NetGain}");
            builder.AppendLine($"- agree  : {routerGate.AgreementRate:P2}");
            builder.AppendLine($"- rec    : {routerGate.Recommendation}");
            builder.AppendLine($"- blocked: {(routerGate.FailureReasons.Count == 0 ? "-" : string.Join(", ", routerGate.FailureReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Candidate Feature Completeness / Eligibility Guard Summary");
        var featureA3 = snapshot.CandidateRerankerFeatureCompletenessA3Report;
        var featureExtended = snapshot.CandidateRerankerFeatureCompletenessExtendedReport;
        if (featureA3 is null && featureExtended is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(CandidateRerankerFeatureCompletenessRunner.DefaultOutputDirectory, CandidateRerankerFeatureCompletenessRunner.A3ReportFileName)}");
            builder.AppendLine("- action : run eval candidate-reranker-feature-completeness");
        }
        else
        {
            if (featureA3 is not null)
            {
                builder.AppendLine($"- A3     : completeness={featureA3.FeatureCompletenessRate:P2} missing={featureA3.MissingFeatureMetadataCount} blockedBeforeRerank={featureA3.RiskCandidateBlockedBeforeRerank} guard={featureA3.EligibilityGuardStatus} rec={featureA3.Recommendation}");
            }

            if (featureExtended is not null)
            {
                builder.AppendLine($"- Ext    : completeness={featureExtended.FeatureCompletenessRate:P2} missing={featureExtended.MissingFeatureMetadataCount} blockedBeforeRerank={featureExtended.RiskCandidateBlockedBeforeRerank} guard={featureExtended.EligibilityGuardStatus} rec={featureExtended.Recommendation}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Candidate Reranker Shadow Summary");
        var rerankerA3 = snapshot.CandidateRerankerShadowEvalA3Report;
        var rerankerExtended = snapshot.CandidateRerankerShadowEvalExtendedReport;
        var rerankerTraceQuality = snapshot.CandidateRerankerShadowTraceQualityReport;
        if (rerankerA3 is null && rerankerExtended is null && rerankerTraceQuality is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- eval   : {Path.Combine(CandidateRerankerShadowEvalRunner.DefaultOutputDirectory, CandidateRerankerShadowEvalRunner.A3ReportFileName)}");
            builder.AppendLine($"- traces : {Path.Combine(CandidateRerankerShadowTraceQualityReportBuilder.DefaultOutputDirectory, CandidateRerankerShadowTraceQualityReportBuilder.ReportFileName)}");
            builder.AppendLine("- action : run eval candidate-reranker-shadow-eval and eval candidate-reranker-shadow-trace-quality");
        }
        else
        {
            if (rerankerTraceQuality is not null)
            {
                builder.AppendLine($"- traces : {rerankerTraceQuality.TraceCount}");
                builder.AppendLine($"- traceRec: {rerankerTraceQuality.Recommendation}");
            }

            if (rerankerA3 is not null)
            {
                var risk = rerankerA3.LifecycleRiskCount + rerankerA3.DeprecatedRiskCount + rerankerA3.MustNotRiskCount;
                builder.AppendLine($"- A3     : netGain={rerankerA3.NetGain} improve={rerankerA3.WouldImproveCount} regress={rerankerA3.WouldRegressCount} risk={risk} rec={rerankerA3.Recommendation}");
            }

            if (rerankerExtended is not null)
            {
                var risk = rerankerExtended.LifecycleRiskCount + rerankerExtended.DeprecatedRiskCount + rerankerExtended.MustNotRiskCount;
                builder.AppendLine($"- Ext    : netGain={rerankerExtended.NetGain} improve={rerankerExtended.WouldImproveCount} regress={rerankerExtended.WouldRegressCount} risk={risk} rec={rerankerExtended.Recommendation}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Candidate Reranker Failure Audit Summary");
        var rerankerAuditA3 = snapshot.CandidateRerankerShadowFailureAuditA3Report;
        var rerankerAuditExtended = snapshot.CandidateRerankerShadowFailureAuditExtendedReport;
        if (rerankerAuditA3 is null && rerankerAuditExtended is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(CandidateRerankerShadowFailureAuditRunner.DefaultOutputDirectory, CandidateRerankerShadowFailureAuditRunner.A3ReportFileName)}");
            builder.AppendLine("- action : run eval candidate-reranker-shadow-failure-audit");
        }
        else
        {
            if (rerankerAuditA3 is not null)
            {
                builder.AppendLine($"- A3     : regressions={rerankerAuditA3.RegressionCount} scoreContract={rerankerAuditA3.ScoreContractStatus} riskTopK={rerankerAuditA3.RiskCandidateInShadowTopK}");
                builder.AppendLine($"  next   : {rerankerAuditA3.RecommendedNextAction}");
            }

            if (rerankerAuditExtended is not null)
            {
                builder.AppendLine($"- Ext    : regressions={rerankerAuditExtended.RegressionCount} scoreContract={rerankerAuditExtended.ScoreContractStatus} riskTopK={rerankerAuditExtended.RiskCandidateInShadowTopK}");
                builder.AppendLine($"  next   : {rerankerAuditExtended.RecommendedNextAction}");
            }

            var reasonSummary = (rerankerAuditA3?.RegressionReasonSummary ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
                .Concat(rerankerAuditExtended?.RegressionReasonSummary ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
                .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new KeyValuePair<string, int>(group.Key, group.Sum(static item => item.Value)))
                .OrderByDescending(static item => item.Value)
                .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5);
            foreach (var pair in reasonSummary)
            {
                builder.AppendLine($"- reason : {pair.Key} = {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Ranker Calibration Summary");
        var scoreDistributionA3 = snapshot.CandidateRerankerScoreDistributionA3Report;
        var scoreDistributionExtended = snapshot.CandidateRerankerScoreDistributionExtendedReport;
        var listwiseA3 = snapshot.CandidateRerankerListwiseCalibrationA3Report;
        var listwiseExtended = snapshot.CandidateRerankerListwiseCalibrationExtendedReport;
        if (scoreDistributionA3 is null && scoreDistributionExtended is null && listwiseA3 is null && listwiseExtended is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- score  : {Path.Combine(CandidateRerankerScoreDistributionRunner.DefaultOutputDirectory, CandidateRerankerScoreDistributionRunner.A3ReportFileName)}");
            builder.AppendLine($"- list   : {Path.Combine(CandidateRerankerListwiseCalibrationRunner.DefaultOutputDirectory, CandidateRerankerListwiseCalibrationRunner.A3ReportFileName)}");
            builder.AppendLine("- action : run eval candidate-reranker-score-distribution and eval candidate-reranker-listwise-calibration");
        }
        else
        {
            if (scoreDistributionA3 is not null)
            {
                builder.AppendLine($"- A3 score : mean={scoreDistributionA3.ScoreMean:0.####} std={scoreDistributionA3.ScoreStdDev:0.####} lowMargin={scoreDistributionA3.LowMarginDecisionCount} rec={scoreDistributionA3.Recommendation}");
            }

            if (scoreDistributionExtended is not null)
            {
                builder.AppendLine($"- Ext score: mean={scoreDistributionExtended.ScoreMean:0.####} std={scoreDistributionExtended.ScoreStdDev:0.####} lowMargin={scoreDistributionExtended.LowMarginDecisionCount} rec={scoreDistributionExtended.Recommendation}");
            }

            if (listwiseA3 is not null)
            {
                builder.AppendLine($"- A3 list  : regressions={listwiseA3.RegressionCount} lowMargin={listwiseA3.LowMarginDecisionCount} formalPriorityMismatch={listwiseA3.FormalPriorityMismatchCount} rec={listwiseA3.Recommendation}");
            }

            if (listwiseExtended is not null)
            {
                builder.AppendLine($"- Ext list : regressions={listwiseExtended.RegressionCount} lowMargin={listwiseExtended.LowMarginDecisionCount} formalPriorityMismatch={listwiseExtended.FormalPriorityMismatchCount} rec={listwiseExtended.Recommendation}");
            }

            var calibrationIssues = (listwiseA3?.CalibrationIssueCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
                .Concat(listwiseExtended?.CalibrationIssueCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
                .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new KeyValuePair<string, int>(group.Key, group.Sum(static item => item.Value)))
                .OrderByDescending(static item => item.Value)
                .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5);
            foreach (var pair in calibrationIssues)
            {
                builder.AppendLine($"- issue  : {pair.Key} = {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Formal Priority Alignment Summary");
        var formalPriorityA3 = snapshot.CandidateRerankerFormalPriorityAlignmentA3Report;
        var formalPriorityExtended = snapshot.CandidateRerankerFormalPriorityAlignmentExtendedReport;
        if (formalPriorityA3 is null && formalPriorityExtended is null)
        {
            builder.AppendLine("- status : not generated");
            builder.AppendLine($"- path   : {Path.Combine(CandidateRerankerFormalPriorityAlignmentRunner.DefaultOutputDirectory, CandidateRerankerFormalPriorityAlignmentRunner.A3ReportFileName)}");
            builder.AppendLine("- action : run eval candidate-reranker-formal-priority-alignment");
        }
        else
        {
            if (formalPriorityA3 is not null)
            {
                builder.AppendLine($"- A3     : recovered={formalPriorityA3.RecoveredCount} unexplained={formalPriorityA3.UnexplainedMismatchCount} abstain={formalPriorityA3.AbstainCount} netAfterAbstain={formalPriorityA3.NetGainAfterAbstain} rec={formalPriorityA3.Recommendation}");
                builder.AppendLine($"  by     : layer={formalPriorityA3.RecoveredByLayerPriority} source={formalPriorityA3.RecoveredBySourcePriority} task={formalPriorityA3.RecoveredByCurrentTaskBoost} constraint={formalPriorityA3.RecoveredByConstraintRelevance} stable={formalPriorityA3.RecoveredByStableMemoryBias}");
            }

            if (formalPriorityExtended is not null)
            {
                builder.AppendLine($"- Ext    : recovered={formalPriorityExtended.RecoveredCount} unexplained={formalPriorityExtended.UnexplainedMismatchCount} abstain={formalPriorityExtended.AbstainCount} netAfterAbstain={formalPriorityExtended.NetGainAfterAbstain} rec={formalPriorityExtended.Recommendation}");
                builder.AppendLine($"  by     : layer={formalPriorityExtended.RecoveredByLayerPriority} source={formalPriorityExtended.RecoveredBySourcePriority} task={formalPriorityExtended.RecoveredByCurrentTaskBoost} constraint={formalPriorityExtended.RecoveredByConstraintRelevance} stable={formalPriorityExtended.RecoveredByStableMemoryBias}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Label Distribution");
        if (dataset.LabelDistribution.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in dataset.LabelDistribution.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Source Type Distribution");
        if (dataset.SourceTypeDistribution.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in dataset.SourceTypeDistribution.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Feature Examples");
        if (dataset.FeatureExamples.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var example in dataset.FeatureExamples.Take(20))
            {
                builder.AppendLine($"- {example.ExampleId} [{example.TaskKind}/{example.Label}] {example.SourceType}/{example.SourceId}");
                builder.AppendLine($"  candidate : {example.CandidateId} kind={example.CandidateKind} layer={example.CandidateLayer} status={example.CandidateStatus}");
                builder.AppendLine($"  accepted  : {example.Accepted} rejected={example.Rejected} selected={example.Selected}");
                builder.AppendLine($"  evidence  : {string.Join(", ", example.EvidenceRefs)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorIndex(ServiceVectorIndexSnapshot snapshot)
    {
        var status = snapshot.Status;
        var diagnostics = snapshot.Diagnostics;
        var preview = snapshot.ReindexPreview;
        var builder = new StringBuilder();
        builder.AppendLine("Service Vector Index");
        builder.AppendLine("====================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {status.WorkspaceId}");
        builder.AppendLine($"Collection : {status.CollectionId}");
        builder.AppendLine($"Provider   : {(string.IsNullOrWhiteSpace(status.Provider) ? "-" : status.Provider)}");
        builder.AppendLine($"Model      : {(string.IsNullOrWhiteSpace(status.Model) ? "-" : status.Model)}");
        builder.AppendLine($"Dimension  : {status.Dimension}");
        builder.AppendLine($"Available  : store={(status.StoreAvailable ? "yes" : "no")} generator={(status.GeneratorAvailable ? "yes" : "no")}");
        builder.AppendLine($"Counts     : indexed={status.IndexedCount} stale={status.StaleCount} missing={status.MissingCount} duplicate={status.DuplicateCount} orphan={status.OrphanCount}");
        builder.AppendLine();
        builder.AppendLine("Coverage Summary");
        builder.AppendLine($"- source items : {snapshot.Coverage.TotalSourceItems}");
        builder.AppendLine($"- indexed      : {snapshot.Coverage.IndexedItems}");
        builder.AppendLine($"- coverage     : {snapshot.Coverage.CoverageRate:P2}");
        builder.AppendLine($"- missing      : {snapshot.Coverage.MissingByLayer.Values.Sum()}");
        builder.AppendLine($"- stale        : {snapshot.Coverage.StaleByLayer.Values.Sum()}");
        builder.AppendLine($"- duplicate    : {snapshot.Coverage.DuplicateCount}");
        builder.AppendLine($"- orphan       : {snapshot.Coverage.OrphanCount}");
        builder.AppendLine($"- recommendation: {snapshot.Coverage.Recommendation}");
        builder.AppendLine();
        builder.AppendLine("Shadow Quality Summary");
        if (!snapshot.ShadowQuality.Available)
        {
            builder.AppendLine($"- status        : {snapshot.ShadowQuality.CurrentRecommendation}");
            builder.AppendLine("- action        : run eval vector-query-profile-sweep");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.SourcePath}");
            builder.AppendLine($"- recommendation: {snapshot.ShadowQuality.CurrentRecommendation}");
            builder.AppendLine($"- best profile  : {snapshot.ShadowQuality.BestProfile}");
            builder.AppendLine($"- best topK     : {snapshot.ShadowQuality.BestTopK}");
            builder.AppendLine($"- best minSim   : {snapshot.ShadowQuality.BestMinSimilarity:F2}");
            builder.AppendLine($"- riskAfter     : {snapshot.ShadowQuality.RiskAfterPolicy}");
            builder.AppendLine($"- separation    : {snapshot.ShadowQuality.SimilaritySeparation:F4}");
        }

        builder.AppendLine();
        builder.AppendLine("Residual Risk Summary");
        if (snapshot.ShadowQuality.ResidualRiskCount == 0)
        {
            builder.AppendLine("- residualRisk  : 0");
        }
        else
        {
            builder.AppendLine($"- source        : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ResidualRiskSourcePath) ? "-" : snapshot.ShadowQuality.ResidualRiskSourcePath)}");
            builder.AppendLine($"- residualRisk  : {snapshot.ShadowQuality.ResidualRiskCount}");
            foreach (var pair in snapshot.ShadowQuality.TopResidualRiskTypes
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- riskType      : {pair.Key} = {pair.Value}");
            }

            foreach (var reason in snapshot.ShadowQuality.TopWhyPolicyAllowed.Take(3))
            {
                builder.AppendLine($"- whyAllowed    : {reason}");
            }

            foreach (var action in snapshot.ShadowQuality.TopExpectedActions.Take(3))
            {
                builder.AppendLine($"- expectedAction: {action}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Lifecycle Metadata Coverage");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataCoverageSourcePath))
        {
            builder.AppendLine("- status        : NoCoverageReport");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-coverage");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataCoverageSourcePath}");
            builder.AppendLine($"- coverage      : {snapshot.ShadowQuality.LifecycleMetadataCoverageRate:P2}");
            builder.AppendLine($"- unknown       : {snapshot.ShadowQuality.UnknownLifecycleCount}");
            builder.AppendLine($"- missingReview : {snapshot.ShadowQuality.MissingReviewStatusCount}");
            builder.AppendLine($"- missingReplace: {snapshot.ShadowQuality.MissingReplacementInfoCount}");
            builder.AppendLine($"- blockedByGate : {snapshot.ShadowQuality.BlockedByLifecycleMetadataGate}");
        }

        builder.AppendLine();
        builder.AppendLine("Lifecycle Metadata Backfill Plan");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleBackfillPlanSourcePath))
        {
            builder.AppendLine("- status        : NoBackfillPlan");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-backfill-plan");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleBackfillPlanSourcePath}");
            builder.AppendLine($"- unknownBefore : {snapshot.ShadowQuality.BackfillUnknownLifecycleBefore}");
            builder.AppendLine($"- autoResolvable: {snapshot.ShadowQuality.BackfillAutoResolvableCount}");
            builder.AppendLine($"- manualReview  : {snapshot.ShadowQuality.BackfillManualReviewRequiredCount}");
            builder.AppendLine($"- coverageAfter : {snapshot.ShadowQuality.BackfillExpectedCoverageAfter:P2}");
        }

        builder.AppendLine();
        builder.AppendLine("Recall Loss / Intent Readiness Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RecallLossA3SourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RecallLossExtendedSourcePath))
        {
            builder.AppendLine("- status        : NoRecallLossAudit");
            builder.AppendLine("- action        : run eval vector-recall-loss-audit");
        }
        else
        {
            builder.AppendLine($"- a3 source     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RecallLossA3SourcePath) ? "-" : snapshot.ShadowQuality.RecallLossA3SourcePath)}");
            builder.AppendLine($"- extended source: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RecallLossExtendedSourcePath) ? "-" : snapshot.ShadowQuality.RecallLossExtendedSourcePath)}");
            builder.AppendLine($"- a3 recall     : {snapshot.ShadowQuality.A3RecallAfterPolicy:P2} ({(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.A3RecallRecommendation) ? "-" : snapshot.ShadowQuality.A3RecallRecommendation)})");
            builder.AppendLine($"- extended recall: {snapshot.ShadowQuality.ExtendedRecallAfterPolicy:P2} ({(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ExtendedRecallRecommendation) ? "-" : snapshot.ShadowQuality.ExtendedRecallRecommendation)})");
            builder.AppendLine($"- v4 gate       : {(snapshot.ShadowQuality.V4GateSatisfied ? "satisfied" : "not-satisfied")}");
            foreach (var reason in snapshot.ShadowQuality.TopRecallMissReasons
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- missReason    : {reason.Key} = {reason.Value}");
            }

            foreach (var readiness in snapshot.ShadowQuality.IntentReadinessRecommendations.Take(8))
            {
                builder.AppendLine($"- intentReady   : {readiness}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Safe Recall Recovery / V4 Readiness Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SafeRecallRecoveryA3SourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SafeRecallRecoveryExtendedSourcePath))
        {
            builder.AppendLine("- status        : NoSafeRecallRecoveryReport");
            builder.AppendLine("- action        : run eval vector-safe-recall-recovery");
        }
        else
        {
            builder.AppendLine($"- a3 source     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SafeRecallRecoveryA3SourcePath) ? "-" : snapshot.ShadowQuality.SafeRecallRecoveryA3SourcePath)}");
            builder.AppendLine($"- extended source: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SafeRecallRecoveryExtendedSourcePath) ? "-" : snapshot.ShadowQuality.SafeRecallRecoveryExtendedSourcePath)}");
            builder.AppendLine($"- a3 best recall: {snapshot.ShadowQuality.SafeRecoveryA3RecallAfterPolicy:P2}");
            builder.AppendLine($"- extended best : {snapshot.ShadowQuality.SafeRecoveryExtendedRecallAfterPolicy:P2}");
            builder.AppendLine($"- a3 best config: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SafeRecoveryA3BestConfiguration) ? "-" : snapshot.ShadowQuality.SafeRecoveryA3BestConfiguration)}");
            builder.AppendLine($"- ext best config: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SafeRecoveryExtendedBestConfiguration) ? "-" : snapshot.ShadowQuality.SafeRecoveryExtendedBestConfiguration)}");
            builder.AppendLine($"- belowTopK recovered: a3={snapshot.ShadowQuality.SafeRecoveryA3RecoveredBelowTopK} extended={snapshot.ShadowQuality.SafeRecoveryExtendedRecoveredBelowTopK}");
            foreach (var pair in snapshot.ShadowQuality.BlockedMustHitClassificationCounts
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(6))
            {
                builder.AppendLine($"- blockedClass  : {pair.Key} = {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Fusion Shadow Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FusionShadowA3SourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FusionShadowExtendedSourcePath))
        {
            builder.AppendLine("- status        : NoFusionShadowReport");
            builder.AppendLine("- action        : run eval vector-ranker-fusion-shadow");
        }
        else
        {
            builder.AppendLine($"- a3 source     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FusionShadowA3SourcePath) ? "-" : snapshot.ShadowQuality.FusionShadowA3SourcePath)}");
            builder.AppendLine($"- extended source: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FusionShadowExtendedSourcePath) ? "-" : snapshot.ShadowQuality.FusionShadowExtendedSourcePath)}");
            builder.AppendLine($"- best strategy : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FusionBestStrategy) ? "-" : snapshot.ShadowQuality.FusionBestStrategy)}");
            builder.AppendLine($"- a3 fusion recall: {snapshot.ShadowQuality.FusionA3RecallAfterPolicy:P2}");
            builder.AppendLine($"- extended fusion: {snapshot.ShadowQuality.FusionExtendedRecallAfterPolicy:P2}");
            builder.AppendLine($"- fusion risk   : {snapshot.ShadowQuality.FusionRiskAfterPolicy}");
            builder.AppendLine($"- recall gain   : {snapshot.ShadowQuality.FusionRecallGain:P2}");
            builder.AppendLine($"- fusion gate   : {(snapshot.ShadowQuality.FusionReadinessGateSatisfied ? "satisfied" : "not-satisfied")}");
        }

        builder.AppendLine();
        builder.AppendLine("Representation Benchmark Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RepresentationBenchmarkA3SourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RepresentationBenchmarkExtendedSourcePath))
        {
            builder.AppendLine("- status        : NoRepresentationBenchmark");
            builder.AppendLine("- action        : run eval vector-representation-benchmark");
        }
        else
        {
            builder.AppendLine($"- a3 source     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RepresentationBenchmarkA3SourcePath) ? "-" : snapshot.ShadowQuality.RepresentationBenchmarkA3SourcePath)}");
            builder.AppendLine($"- extended source: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RepresentationBenchmarkExtendedSourcePath) ? "-" : snapshot.ShadowQuality.RepresentationBenchmarkExtendedSourcePath)}");
            builder.AppendLine($"- best document : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RepresentationBestDocumentProfile) ? "-" : snapshot.ShadowQuality.RepresentationBestDocumentProfile)}");
            builder.AppendLine($"- best query    : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RepresentationBestQueryProfile) ? "-" : snapshot.ShadowQuality.RepresentationBestQueryProfile)}");
            builder.AppendLine($"- a3 recall     : {snapshot.ShadowQuality.RepresentationA3RecallAfterPolicy:P2}");
            builder.AppendLine($"- extended recall: {snapshot.ShadowQuality.RepresentationExtendedRecallAfterPolicy:P2}");
            builder.AppendLine($"- risk          : {snapshot.ShadowQuality.RepresentationRiskAfterPolicy}");
            builder.AppendLine($"- recovered miss: {snapshot.ShadowQuality.RepresentationRecoveredMissCount}");
            builder.AppendLine($"- representation gate: {(snapshot.ShadowQuality.RepresentationV4GateSatisfied ? "satisfied" : "not-satisfied")}");
        }

        builder.AppendLine();
        builder.AppendLine("Query Expansion Shadow Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.QueryExpansionShadowA3SourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.QueryExpansionShadowExtendedSourcePath))
        {
            builder.AppendLine("- status        : NoQueryExpansionShadow");
            builder.AppendLine("- action        : run eval vector-query-expansion-shadow");
        }
        else
        {
            builder.AppendLine($"- a3 source     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.QueryExpansionShadowA3SourcePath) ? "-" : snapshot.ShadowQuality.QueryExpansionShadowA3SourcePath)}");
            builder.AppendLine($"- extended source: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.QueryExpansionShadowExtendedSourcePath) ? "-" : snapshot.ShadowQuality.QueryExpansionShadowExtendedSourcePath)}");
            builder.AppendLine($"- best profile  : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.QueryExpansionBestProfile) ? "-" : snapshot.ShadowQuality.QueryExpansionBestProfile)}");
            builder.AppendLine($"- a3 recall     : {snapshot.ShadowQuality.QueryExpansionA3RecallBefore:P2} -> {snapshot.ShadowQuality.QueryExpansionA3RecallAfter:P2}");
            builder.AppendLine($"- extended recall: {snapshot.ShadowQuality.QueryExpansionExtendedRecallBefore:P2} -> {snapshot.ShadowQuality.QueryExpansionExtendedRecallAfter:P2}");
            builder.AppendLine($"- risk          : {snapshot.ShadowQuality.QueryExpansionRiskAfterPolicy}");
            builder.AppendLine($"- recovered miss: {snapshot.ShadowQuality.QueryExpansionRecoveredMissCount}");
            builder.AppendLine($"- expansion gate: {(snapshot.ShadowQuality.QueryExpansionV4GateSatisfied ? "satisfied" : "not-satisfied")}");
        }

        builder.AppendLine();
        builder.AppendLine("Provider Comparison Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderComparisonSourcePath))
        {
            builder.AppendLine("- status        : NoProviderComparisonReport");
            builder.AppendLine("- action        : run eval vector-provider-comparison --providers current,qwen3");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ProviderComparisonSourcePath}");
            foreach (var provider in snapshot.ShadowQuality.ProviderComparisonResults.Take(4))
            {
                builder.AppendLine(
                    $"- provider      : {provider.ProviderId} dim={provider.Dimension} indexed={provider.IndexedEntryCount} a3={provider.A3RecallAfterPolicy:P2}/{provider.A3MrrAfterPolicy:F4} extended={provider.ExtendedRecallAfterPolicy:P2}/{provider.ExtendedMrrAfterPolicy:F4} risk={provider.A3RiskAfterPolicy + provider.ExtendedRiskAfterPolicy} pgParity={provider.PgVectorParityPassed} rec={provider.Recommendation}");
            }

            builder.AppendLine($"- qwen3 gate    : {snapshot.ShadowQuality.Qwen3ReadinessGatePassed}");
            builder.AppendLine($"- qwen3 rec     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.Qwen3Recommendation) ? "-" : snapshot.ShadowQuality.Qwen3Recommendation)}");
            foreach (var reason in snapshot.ShadowQuality.Qwen3BlockedReasons.Take(5))
            {
                builder.AppendLine($"- qwen3 blocked : {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Provider Promotion Status");
        builder.AppendLine($"- current       : KeepCurrentPreviewProvider");
        builder.AppendLine($"- qwen3         : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderPromotionStatus) ? "NoFreezeReport" : snapshot.ShadowQuality.ProviderPromotionStatus)}");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderComparisonFreezeSourcePath))
        {
            builder.AppendLine("- freeze action : run eval vector-provider-comparison-freeze");
        }
        else
        {
            builder.AppendLine($"- freeze source : {snapshot.ShadowQuality.ProviderComparisonFreezeSourcePath}");
            builder.AppendLine($"- comparison    : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderComparisonStatus) ? "-" : snapshot.ShadowQuality.ProviderComparisonStatus)}");
            builder.AppendLine($"- sanity passed : {snapshot.ShadowQuality.ProviderConfigurationSanityPassed}");
            builder.AppendLine($"- v4 recheck    : {snapshot.ShadowQuality.VectorV4RecheckAllowed}");
            foreach (var reason in snapshot.ShadowQuality.ProviderPromotionBlockedReasons.Take(5))
            {
                builder.AppendLine($"- promotion blocked : {reason}");
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.V4ReadinessGateSourcePath))
        {
            builder.AppendLine("- gate          : NoReadinessGateReport");
            builder.AppendLine("- gate action   : run eval vector-retrieval-shadow-readiness-gate");
        }
        else
        {
            builder.AppendLine($"- gate source   : {snapshot.ShadowQuality.V4ReadinessGateSourcePath}");
            builder.AppendLine($"- gate passed   : {snapshot.ShadowQuality.V4ReadinessGatePassed}");
            foreach (var reason in snapshot.ShadowQuality.V4ReadinessGateFailReasons.Take(6))
            {
                builder.AppendLine($"- gate fail     : {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Retrieval Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridPreviewSourcePath))
        {
            builder.AppendLine("- status        : NoHybridPreviewReport");
            builder.AppendLine("- action        : run eval vector-hybrid-preview");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.HybridPreviewSourcePath}");
            builder.AppendLine($"- a3 recall     : {snapshot.ShadowQuality.HybridFullA3Recall}");
            builder.AppendLine($"- ext recall    : {snapshot.ShadowQuality.HybridFullExtendedRecall}");
            builder.AppendLine($"- risk          : {snapshot.ShadowQuality.HybridFullRiskAfterPolicy}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridReadinessRecommendation) ? "-" : snapshot.ShadowQuality.HybridReadinessRecommendation)}");
            builder.AppendLine($"- gate passed   : {snapshot.ShadowQuality.HybridReadinessGatePassed}");
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Recall Regression Audit Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridAuditSourcePath))
        {
            builder.AppendLine("- status        : NoAuditReport");
            builder.AppendLine("- action        : run eval vector-hybrid-recall-regression-audit");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.HybridAuditSourcePath}");
            builder.AppendLine($"- passed        : {snapshot.ShadowQuality.HybridAuditPassed}");
            builder.AppendLine($"- dense dropped : {snapshot.ShadowQuality.HybridAuditDenseDroppedCount}");
            builder.AppendLine($"- elig mismatch : {snapshot.ShadowQuality.HybridAuditEligibilityMismatchCount}");
            builder.AppendLine($"- dedup overwrite: {snapshot.ShadowQuality.HybridAuditDedupOverwriteCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridAuditRecommendation) ? "-" : snapshot.ShadowQuality.HybridAuditRecommendation)}");
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Retrieval Freeze Status");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoFreezeReport");
            builder.AppendLine("- action        : run eval vector-hybrid-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.HybridFreezeSourcePath}");
            builder.AppendLine($"- freeze passed : {snapshot.ShadowQuality.HybridFreezePassed}");
            builder.AppendLine($"- status        : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridFreezeStatus) ? "-" : snapshot.ShadowQuality.HybridFreezeStatus)}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridFreezeRecommendation) ? "-" : snapshot.ShadowQuality.HybridFreezeRecommendation)}");
            builder.AppendLine($"- v4 recheck    : {snapshot.ShadowQuality.HybridV4RecheckAllowed}");
            foreach (var reason in snapshot.ShadowQuality.HybridFreezeBlockedReasons.Take(5))
            {
                builder.AppendLine($"- freeze blocked: {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Dataset Alignment Audit Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.DatasetAlignmentAuditSourcePath))
        {
            builder.AppendLine("- status        : NoAlignmentAuditReport");
            builder.AppendLine("- action        : run eval vector-retrieval-dataset-alignment-audit");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.DatasetAlignmentAuditSourcePath}");
            builder.AppendLine($"- mustHit corpus: A3={snapshot.ShadowQuality.DatasetAlignmentA3MustHitCorpusCoverage:P2} Extended={snapshot.ShadowQuality.DatasetAlignmentExtendedMustHitCorpusCoverage:P2}");
            builder.AppendLine($"- provider scope: A3={snapshot.ShadowQuality.DatasetAlignmentA3ProviderScopeCoverage:P2} Extended={snapshot.ShadowQuality.DatasetAlignmentExtendedProviderScopeCoverage:P2}");
            builder.AppendLine($"- eligibility blocks: {snapshot.ShadowQuality.DatasetAlignmentEligibilityBlockCount}");
            builder.AppendLine($"- anchor coverage: {snapshot.ShadowQuality.DatasetAlignmentAnchorCoverageRate:P2}");
            builder.AppendLine($"- issue count   : {snapshot.ShadowQuality.DatasetAlignmentIssueCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.DatasetAlignmentRecommendation) ? "-" : snapshot.ShadowQuality.DatasetAlignmentRecommendation)}");
            foreach (var issue in snapshot.ShadowQuality.DatasetAlignmentTopIssues
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- issue         : {issue.Key}={issue.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Eligibility Recall Loss Triage Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.EligibilityRecallLossTriageSourcePath))
        {
            builder.AppendLine("- status        : NoEligibilityTriageReport");
            builder.AppendLine("- action        : run eval vector-eligibility-recall-loss-triage");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.EligibilityRecallLossTriageSourcePath}");
            builder.AppendLine($"- filtered mustHit: {snapshot.ShadowQuality.EligibilityFilteredMustHitCount}");
            builder.AppendLine($"- correctly blocked: {snapshot.ShadowQuality.EligibilityCorrectlyBlockedCount}");
            builder.AppendLine($"- route historical : {snapshot.ShadowQuality.EligibilityRouteToHistoricalCount}");
            builder.AppendLine($"- route audit      : {snapshot.ShadowQuality.EligibilityRouteToAuditCount}");
            builder.AppendLine($"- metadata repair  : {snapshot.ShadowQuality.EligibilityMetadataRepairNeededCount}");
            builder.AppendLine($"- eval review      : {snapshot.ShadowQuality.EligibilityEvalExpectationReviewNeededCount}");
            builder.AppendLine($"- unsafe           : {snapshot.ShadowQuality.EligibilityUnsafeToRecoverCount}");
            builder.AppendLine($"- recommendation   : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.EligibilityRecallLossRecommendation) ? "-" : snapshot.ShadowQuality.EligibilityRecallLossRecommendation)}");
        }

        builder.AppendLine();
        builder.AppendLine("Lifecycle Metadata Repair Plan Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataRepairPlanSourcePath))
        {
            builder.AppendLine("- status        : NoLifecycleMetadataRepairPlanReport");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-repair-plan");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataRepairPlanSourcePath}");
            builder.AppendLine($"- candidates    : {snapshot.ShadowQuality.LifecycleMetadataRepairCandidateCount}");
            builder.AppendLine($"- auto repair   : {snapshot.ShadowQuality.LifecycleMetadataRepairAutoRepairableCount}");
            builder.AppendLine($"- human review  : {snapshot.ShadowQuality.LifecycleMetadataRepairHumanReviewRequiredCount}");
            builder.AppendLine($"- forbidden     : {snapshot.ShadowQuality.LifecycleMetadataRepairForbiddenCount}");
            builder.AppendLine($"- recall estimate: {snapshot.ShadowQuality.LifecycleMetadataRepairEstimatedRecallRecovery:F2}");
            builder.AppendLine($"- risk estimate : {snapshot.ShadowQuality.LifecycleMetadataRepairRiskEstimate}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataRepairRecommendation) ? "-" : snapshot.ShadowQuality.LifecycleMetadataRepairRecommendation)}");
        }

        builder.AppendLine();
        builder.AppendLine("Lifecycle Metadata Review Candidates");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataReviewCandidatesSourcePath))
        {
            builder.AppendLine("- status        : NoLifecycleMetadataReviewCandidateReport");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-review-candidates-generate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataReviewCandidatesSourcePath}");
            builder.AppendLine($"- candidates    : {snapshot.ShadowQuality.LifecycleMetadataReviewCandidateCount}");
            builder.AppendLine($"- pending       : {snapshot.ShadowQuality.LifecycleMetadataReviewPendingCount}");
            builder.AppendLine($"- skipped blocked: {snapshot.ShadowQuality.LifecycleMetadataReviewCorrectlyBlockedSkippedCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataReviewRecommendation) ? "-" : snapshot.ShadowQuality.LifecycleMetadataReviewRecommendation)}");
            foreach (var pair in snapshot.ShadowQuality.LifecycleMetadataReviewCountByLayer
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- layer         : {pair.Key}={pair.Value}");
            }

            foreach (var pair in snapshot.ShadowQuality.LifecycleMetadataReviewCountByItemKind
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- itemKind      : {pair.Key}={pair.Value}");
            }

            foreach (var candidate in snapshot.ShadowQuality.LifecycleMetadataReviewRecentCandidates.Take(5))
            {
                builder.AppendLine($"- recent        : {candidate.CandidateId} mustHit={candidate.MustHitItemId} status={candidate.Status} riskApproved={string.Join(",", candidate.RiskIfApproved)} riskRejected={string.Join(",", candidate.RiskIfRejected)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Lifecycle Metadata Review / Sidecar Apply");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataReviewSummarySourcePath))
        {
            builder.AppendLine("- status        : NoLifecycleMetadataReviewSummary");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-review-summary");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataReviewSummarySourcePath}");
            builder.AppendLine($"- approved      : {snapshot.ShadowQuality.LifecycleMetadataReviewApprovedForSidecarCount}");
            builder.AppendLine($"- rejected      : {snapshot.ShadowQuality.LifecycleMetadataReviewRejectedCount}");
            builder.AppendLine($"- needs evidence: {snapshot.ShadowQuality.LifecycleMetadataReviewNeedsEvidenceCount}");
            builder.AppendLine($"- superseded    : {snapshot.ShadowQuality.LifecycleMetadataReviewSupersededCount}");
            builder.AppendLine($"- sidecar       : {snapshot.ShadowQuality.LifecycleMetadataReviewSidecarEntryCount}");
            builder.AppendLine($"- unsafe blocked: {snapshot.ShadowQuality.LifecycleMetadataReviewUnsafeApprovalBlockedCount}");
            builder.AppendLine($"- normal/audit/historical/diag: {snapshot.ShadowQuality.LifecycleMetadataReviewNormalContextApprovalCount}/{snapshot.ShadowQuality.LifecycleMetadataReviewAuditContextApprovalCount}/{snapshot.ShadowQuality.LifecycleMetadataReviewHistoricalContextApprovalCount}/{snapshot.ShadowQuality.LifecycleMetadataReviewDiagnosticsOnlyApprovalCount}");
            builder.AppendLine("- actions       : A ApproveForSidecar / R Reject / N NeedsEvidence / S Supersede / H ReviewHistory; approve requires YES confirmation");
            builder.AppendLine("- formal retrieval: disabled");
        }

        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataReviewSidecarPreviewSourcePath))
        {
            builder.AppendLine("- sidecar preview: run eval vector-lifecycle-metadata-sidecar-preview");
        }
        else
        {
            builder.AppendLine($"- sidecar preview: {snapshot.ShadowQuality.LifecycleMetadataReviewSidecarPreviewSourcePath}");
        }

        builder.AppendLine();
        builder.AppendLine("Sidecar-aware Eligibility Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SidecarEligibilityPreviewSourcePath))
        {
            builder.AppendLine("- status        : NoSidecarEligibilityPreview");
            builder.AppendLine("- action        : run eval vector-sidecar-eligibility-preview");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.SidecarEligibilityPreviewSourcePath}");
            builder.AppendLine($"- candidates    : {snapshot.ShadowQuality.SidecarEligibilityCandidateCount}");
            builder.AppendLine($"- sidecar       : {snapshot.ShadowQuality.SidecarEligibilitySidecarEntryCount}");
            builder.AppendLine($"- approved sidecar: {snapshot.ShadowQuality.SidecarEligibilityApprovedSidecarCount}");
            builder.AppendLine($"- pending review: {snapshot.ShadowQuality.SidecarEligibilityPendingReviewCount}");
            builder.AppendLine($"- metadata changes: {snapshot.ShadowQuality.SidecarEligibilityEffectiveMetadataChangedCount}");
            builder.AppendLine($"- unsafe/conflict blocked: {snapshot.ShadowQuality.SidecarEligibilityUnsafeBlockedCount}/{snapshot.ShadowQuality.SidecarEligibilityConflictBlockedCount}");
            builder.AppendLine($"- source unchanged: {snapshot.ShadowQuality.SidecarEligibilitySourceItemUnchanged}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SidecarEligibilityRecommendation) ? "-" : snapshot.ShadowQuality.SidecarEligibilityRecommendation)}");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Human Review Batch Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataReviewBatchSourcePath))
        {
            builder.AppendLine("- status        : NoReviewBatch");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-review-batch-create");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataReviewBatchSourcePath}");
            builder.AppendLine($"- batch id      : {snapshot.ShadowQuality.LifecycleMetadataReviewBatchId}");
            builder.AppendLine($"- status        : {snapshot.ShadowQuality.LifecycleMetadataReviewBatchStatus}");
            builder.AppendLine($"- pending candidates: {snapshot.ShadowQuality.LifecycleMetadataReviewPendingCount}");
            builder.AppendLine($"- batch candidates: {snapshot.ShadowQuality.LifecycleMetadataReviewBatchCandidateCount}");
            builder.AppendLine($"- validation errors: {snapshot.ShadowQuality.LifecycleMetadataReviewBatchValidationErrorCount}");
            builder.AppendLine($"- would write sidecar: {snapshot.ShadowQuality.LifecycleMetadataReviewBatchWouldWriteSidecarCount}");
            builder.AppendLine($"- unsafe blocked: {snapshot.ShadowQuality.LifecycleMetadataReviewBatchUnsafeBlockedCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataReviewBatchRecommendation) ? "-" : snapshot.ShadowQuality.LifecycleMetadataReviewBatchRecommendation)}");
            builder.AppendLine("- warning       : no auto-approve; apply preview does not write real sidecar");
        }

        builder.AppendLine();
        builder.AppendLine("Evidence Backfill Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataEvidenceBackfillSourcePath))
        {
            builder.AppendLine("- status        : NoEvidenceBackfillPreview");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-evidence-backfill-preview");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataEvidenceBackfillSourcePath}");
            builder.AppendLine($"- candidates    : {snapshot.ShadowQuality.LifecycleMetadataEvidenceBackfillCandidateCount}");
            builder.AppendLine($"- evidence/source/provenance: {snapshot.ShadowQuality.LifecycleMetadataEvidenceFoundCount}/{snapshot.ShadowQuality.LifecycleMetadataSourceRefFoundCount}/{snapshot.ShadowQuality.LifecycleMetadataProvenanceFoundCount}");
            builder.AppendLine($"- auto repairable after backfill: {snapshot.ShadowQuality.LifecycleMetadataAutoRepairableAfterBackfillCount}");
            builder.AppendLine($"- needs evidence after backfill : {snapshot.ShadowQuality.LifecycleMetadataNeedsEvidenceAfterBackfillCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataEvidenceBackfillRecommendation) ? "-" : snapshot.ShadowQuality.LifecycleMetadataEvidenceBackfillRecommendation)}");
            builder.AppendLine("- sidecar/write : disabled");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Dataset V2 Generation Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2GenerationSourcePath))
        {
            builder.AppendLine("- status        : NoDatasetV2GenerationReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-generate --dry-run");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2GenerationSourcePath}");
            builder.AppendLine($"- corpus/sample : {snapshot.ShadowQuality.RetrievalDatasetV2CorpusItemCount}/{snapshot.ShadowQuality.RetrievalDatasetV2SampleCount}");
            builder.AppendLine($"- validation issues: {snapshot.ShadowQuality.RetrievalDatasetV2ValidationIssueCount}");
            builder.AppendLine($"- missing evidence/provenance: {snapshot.ShadowQuality.RetrievalDatasetV2MissingEvidenceCount}/{snapshot.ShadowQuality.RetrievalDatasetV2MissingProvenanceCount}");
            builder.AppendLine($"- difficulty    : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2DifficultyBreakdown)}");
            builder.AppendLine($"- split         : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2SplitBreakdown)}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2Recommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2Recommendation)}");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Dataset V2 Materialization Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2MaterializationSourcePath))
        {
            builder.AppendLine("- status        : NoDatasetV2MaterializationReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-materialization-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2MaterializationSourcePath}");
            builder.AppendLine($"- datasetId     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2DatasetId) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2DatasetId)}");
            builder.AppendLine($"- corpus hash   : {TrimHash(snapshot.ShadowQuality.RetrievalDatasetV2CorpusHash)}");
            builder.AppendLine($"- samples hash  : {TrimHash(snapshot.ShadowQuality.RetrievalDatasetV2SamplesHash)}");
            builder.AppendLine($"- gate passed   : {snapshot.ShadowQuality.RetrievalDatasetV2MaterializationGatePassed}");
            builder.AppendLine($"- hash stable   : {snapshot.ShadowQuality.RetrievalDatasetV2MaterializationCorpusHashStable}/{snapshot.ShadowQuality.RetrievalDatasetV2MaterializationSamplesHashStable}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2MaterializationRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2MaterializationRecommendation)}");
            builder.AppendLine("- use/runtime   : false");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Dataset V2 Shadow Eval Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalSourcePath))
        {
            builder.AppendLine("- status        : NoDatasetV2ShadowEvalReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-shadow-eval");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalSourcePath}");
            builder.AppendLine($"- datasetId     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalDatasetId) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalDatasetId)}");
            builder.AppendLine($"- best profile  : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalBestProfileName) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalBestProfileName)}");
            builder.AppendLine($"- recall / mrr  : {snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalBestRecallAfterPolicy:P2} / {snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalBestMrrAfterPolicy:F4}");
            builder.AppendLine($"- risk          : {snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalBestRiskAfterPolicy}");
            builder.AppendLine($"- pgvector parity: {snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalPgVectorParityPassed}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2ShadowEvalRecommendation)}");
            builder.AppendLine("- use/runtime   : false");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Dataset V2 Stress / Leakage Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressSourcePath))
        {
            builder.AppendLine("- status        : NoDatasetV2StressReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-stress-shadow-eval");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2StressSourcePath}");
            builder.AppendLine($"- datasetId     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressDatasetId) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressDatasetId)}");
            builder.AppendLine($"- corpus/sample : {snapshot.ShadowQuality.RetrievalDatasetV2StressCorpusItemCount}/{snapshot.ShadowQuality.RetrievalDatasetV2StressSampleCount}");
            builder.AppendLine($"- split         : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2StressSplitBreakdown)}");
            builder.AppendLine($"- difficulty    : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2StressDifficultyBreakdown)}");
            builder.AppendLine($"- leakage       : {snapshot.ShadowQuality.RetrievalDatasetV2StressLeakageIssueCount}");
            builder.AppendLine($"- anchor dominance: {snapshot.ShadowQuality.RetrievalDatasetV2StressAnchorDominanceScore:F4}");
            builder.AppendLine($"- dense/lex/anchor: {snapshot.ShadowQuality.RetrievalDatasetV2StressDenseRecall:P2} / {snapshot.ShadowQuality.RetrievalDatasetV2StressLexicalRecall:P2} / {snapshot.ShadowQuality.RetrievalDatasetV2StressAnchorRecall:P2}");
            builder.AppendLine($"- hybrid/holdout: {snapshot.ShadowQuality.RetrievalDatasetV2StressHybridRecall:P2} / {snapshot.ShadowQuality.RetrievalDatasetV2StressHoldoutHybridRecall:P2}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressRecommendation)}");
            builder.AppendLine("- use/runtime   : false");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Dataset V2 Stress Failure Triage Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressTriageSourcePath))
        {
            builder.AppendLine("- status        : NoDatasetV2StressFailureTriageReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-stress-failure-triage");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2StressTriageSourcePath}");
            builder.AppendLine($"- failures      : {snapshot.ShadowQuality.RetrievalDatasetV2StressFailureCount}");
            builder.AppendLine($"- holdout failures: {snapshot.ShadowQuality.RetrievalDatasetV2StressHoldoutFailureCount}");
            builder.AppendLine($"- by split      : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2StressFailureCountBySplit)}");
            builder.AppendLine($"- by difficulty : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2StressFailureCountByDifficulty)}");
            builder.AppendLine($"- by reason     : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2StressFailureCountByReason)}");
            builder.AppendLine($"- dense/hybrid wins: {snapshot.ShadowQuality.RetrievalDatasetV2StressDenseOnlyWinCount}/{snapshot.ShadowQuality.RetrievalDatasetV2StressHybridWinCount}");
            builder.AppendLine($"- anchor regression: {snapshot.ShadowQuality.RetrievalDatasetV2StressAnchorRegressionCount}");
            builder.AppendLine($"- profile compare: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressProfileComparisonSummary) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressProfileComparisonSummary)}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressTriageRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressTriageRecommendation)}");
            builder.AppendLine("- use/runtime   : false");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Scoring Repair Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairSourcePath))
        {
            builder.AppendLine("- status        : NoHybridScoringRepairReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-hybrid-scoring-repair-preview");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairSourcePath}");
            builder.AppendLine($"- best profile  : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairBestProfileName) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairBestProfileName)}");
            builder.AppendLine($"- recall/holdout: {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairRecallAfterPolicy:P2} / {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairHoldoutRecallAfterPolicy:P2}");
            builder.AppendLine($"- dense lost    : {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairDenseWinnerLostCount}");
            builder.AppendLine($"- below topK    : {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairMustHitBelowTopKCount}");
            builder.AppendLine($"- negative/risk : {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairNegativeDistractorCount} / {snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairRiskAfterPolicy}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2HybridRepairRecommendation)}");
            builder.AppendLine("- use/runtime   : false");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Scoring Risk Triage Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskTriageSourcePath))
        {
            builder.AppendLine("- status        : NoHybridScoringRiskTriageReport");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-hybrid-scoring-risk-triage");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskTriageSourcePath}");
            builder.AppendLine($"- profile       : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskTriageProfileName) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskTriageProfileName)}");
            builder.AppendLine($"- risk count    : {snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskCandidateCount}");
            builder.AppendLine($"- risk by type  : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskByType)}");
            builder.AppendLine($"- risk by split : {FormatDictionaryCompact(snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskBySplit)}");
            builder.AppendLine($"- must-not / eligibility bypass: {snapshot.ShadowQuality.RetrievalDatasetV2HybridMustNotPromotedCount} / {snapshot.ShadowQuality.RetrievalDatasetV2HybridEligibilityBypassCount}");
            builder.AppendLine($"- projection mismatch: {snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskProjectionMismatchCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskTriageRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2HybridRiskTriageRecommendation)}");
            builder.AppendLine("- use/runtime   : false");
            builder.AppendLine("- formal retrieval: disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Dataset V2 Stress Freeze Status");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoDatasetV2StressFreezeGate");
            builder.AppendLine("- action        : run eval retrieval-dataset-v2-stress-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeSourcePath}");
            builder.AppendLine($"- freeze passed : {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezePassed}");
            builder.AppendLine($"- status        : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeStatus) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeStatus)}");
            builder.AppendLine($"- best profile  : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeBestProfile) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeBestProfile)}");
            builder.AppendLine($"- stress/holdout: {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeStressRecall:P2} / {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeHoldoutRecall:P2}");
            builder.AppendLine($"- risk/must-not/lifecycle: {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeLifecycleRiskAfterPolicy}");
            builder.AppendLine($"- formal output changed: {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeFormalOutputChanged}");
            builder.AppendLine($"- leakage / anchor dominance: {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeLeakageIssueCount} / {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeAnchorDominanceScore:F4}");
            builder.AppendLine($"- V4 input / formal ready: {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeV4RecheckAllowed} / {snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeReadyForFormalRetrieval}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeRecommendation) ? "-" : snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeBlockedReasons))}");
            builder.AppendLine($"- formal retrieval: {(snapshot.ShadowQuality.RetrievalDatasetV2StressFreezeFormalRetrievalAllowed ? "enabled" : "disabled")}");
        }

        builder.AppendLine();
        builder.AppendLine("V4 Readiness Recheck Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.VectorV4ReadinessRecheckSourcePath))
        {
            builder.AppendLine("- status        : NoVectorV4ReadinessRecheckReport");
            builder.AppendLine("- action        : run eval vector-v4-readiness-recheck");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.VectorV4ReadinessRecheckSourcePath}");
            builder.AppendLine($"- recheck passed: {snapshot.ShadowQuality.VectorV4ReadinessRecheckPassed}");
            builder.AppendLine($"- legacy/small/stress: {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessLegacyStatus)} / {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessSmallStatus)} / {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessStressStatus)}");
            builder.AppendLine($"- pgvector/runtime gate: {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessPgVectorStatus)} / {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessRuntimeGateStatus)}");
            builder.AppendLine($"- best profile  : {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessBestProfile)}");
            builder.AppendLine($"- stress/holdout: {snapshot.ShadowQuality.VectorV4ReadinessStressRecall:P2} / {snapshot.ShadowQuality.VectorV4ReadinessHoldoutRecall:P2}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.VectorV4ReadinessRiskAfterPolicy} / {snapshot.ShadowQuality.VectorV4ReadinessFormalOutputChanged}");
            builder.AppendLine($"- guarded preview/runtime switch: {snapshot.ShadowQuality.VectorV4ReadinessReadyForGuardedFormalPreview} / {snapshot.ShadowQuality.VectorV4ReadinessReadyForRuntimeSwitch}");
            builder.AppendLine($"- formal retrieval: {(snapshot.ShadowQuality.VectorV4ReadinessFormalRetrievalAllowed ? "enabled" : "disabled")}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.VectorV4ReadinessRecheckRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.VectorV4ReadinessBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.VectorV4ReadinessBlockedReasons))}");
            builder.AppendLine("- next allowed  : guarded formal preview only; runtime switch remains disabled");
        }

        builder.AppendLine();
        builder.AppendLine("Guarded Formal Retrieval Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.GuardedFormalRetrievalPreviewSourcePath))
        {
            builder.AppendLine("- status        : NoGuardedFormalRetrievalPreviewReport");
            builder.AppendLine("- action        : run eval vector-guarded-formal-retrieval-preview-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewSourcePath}");
            builder.AppendLine($"- V4.R / gate   : {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewV4RecheckPassed} / {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewGatePassed}");
            builder.AppendLine($"- profile       : {BlankDash(snapshot.ShadowQuality.GuardedFormalRetrievalPreviewProfileName)}");
            builder.AppendLine($"- would add/remove/rerank: {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewWouldAddCount}/{snapshot.ShadowQuality.GuardedFormalRetrievalPreviewWouldRemoveCount}/{snapshot.ShadowQuality.GuardedFormalRetrievalPreviewWouldRerankCount}");
            builder.AppendLine($"- would route section: {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewWouldChangeTargetSectionCount}");
            builder.AppendLine($"- risk/must-not/lifecycle: {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewRiskAfterPolicy}/{snapshot.ShadowQuality.GuardedFormalRetrievalPreviewMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.GuardedFormalRetrievalPreviewLifecycleRiskAfterPolicy}");
            builder.AppendLine($"- formal output : {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewFormalOutputChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewPackingPolicyChanged} / {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewPackageOutputChanged}");
            builder.AppendLine($"- runtime switch/formal retrieval: {snapshot.ShadowQuality.GuardedFormalRetrievalPreviewReadyForRuntimeSwitch} / {(snapshot.ShadowQuality.GuardedFormalRetrievalPreviewFormalRetrievalAllowed ? "enabled" : "disabled")}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.GuardedFormalRetrievalPreviewRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.GuardedFormalRetrievalPreviewBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedFormalRetrievalPreviewBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Shadow Package Comparison Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.VectorShadowPackageComparisonSourcePath))
        {
            builder.AppendLine("- status        : NoVectorShadowPackageComparisonReport");
            builder.AppendLine("- action        : run eval vector-shadow-package-comparison-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.VectorShadowPackageComparisonSourcePath}");
            builder.AppendLine($"- gate          : {snapshot.ShadowQuality.VectorShadowPackageComparisonGatePassed}");
            builder.AppendLine($"- profile       : {BlankDash(snapshot.ShadowQuality.VectorShadowPackageComparisonProfileName)}");
            builder.AppendLine($"- add/remove/unchanged: {snapshot.ShadowQuality.VectorShadowPackageCandidateAddCount}/{snapshot.ShadowQuality.VectorShadowPackageCandidateRemoveCount}/{snapshot.ShadowQuality.VectorShadowPackageCandidateUnchangedCount}");
            builder.AppendLine($"- section changes: {snapshot.ShadowQuality.VectorShadowPackageSectionChangedCount}");
            builder.AppendLine($"- token delta total/max: {snapshot.ShadowQuality.VectorShadowPackageTokenDeltaTotal}/{snapshot.ShadowQuality.VectorShadowPackageTokenDeltaMax}");
            builder.AppendLine($"- coverage delta constraint/relation: {snapshot.ShadowQuality.VectorShadowPackageConstraintCoverageDelta:F4}/{snapshot.ShadowQuality.VectorShadowPackageRelationCoverageDelta:F4}");
            builder.AppendLine($"- risk/must-not/lifecycle: {snapshot.ShadowQuality.VectorShadowPackageRiskAfterPolicy}/{snapshot.ShadowQuality.VectorShadowPackageMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.VectorShadowPackageLifecycleRiskAfterPolicy}");
            builder.AppendLine($"- formal output : {snapshot.ShadowQuality.VectorShadowPackageFormalOutputChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.VectorShadowPackagePackingPolicyChanged} / {snapshot.ShadowQuality.VectorShadowPackagePackageOutputChanged}");
            builder.AppendLine($"- runtime mutation/switch: {snapshot.ShadowQuality.VectorShadowPackageRuntimeMutated} / {snapshot.ShadowQuality.VectorShadowPackageReadyForRuntimeSwitch}");
            builder.AppendLine($"- formal retrieval: {(snapshot.ShadowQuality.VectorShadowPackageFormalRetrievalAllowed ? "enabled" : "disabled")}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.VectorShadowPackageComparisonRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.VectorShadowPackageBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.VectorShadowPackageBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Formal Preview Opt-in Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedFormalPreviewOptInSourcePath))
        {
            builder.AppendLine("- status        : NoScopedFormalPreviewOptInReport");
            builder.AppendLine("- action        : run eval vector-scoped-formal-preview-optin-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedFormalPreviewOptInSourcePath}");
            builder.AppendLine($"- gate          : {snapshot.ShadowQuality.ScopedFormalPreviewOptInGatePassed}");
            builder.AppendLine($"- mode/profile  : {BlankDash(snapshot.ShadowQuality.ScopedFormalPreviewOptInMode)} / {BlankDash(snapshot.ShadowQuality.ScopedFormalPreviewOptInProfileName)}");
            builder.AppendLine($"- workspace allowlist: {FormatList(snapshot.ShadowQuality.ScopedFormalPreviewOptInWorkspaceAllowlist)}");
            builder.AppendLine($"- collection allowlist: {FormatList(snapshot.ShadowQuality.ScopedFormalPreviewOptInCollectionAllowlist)}");
            builder.AppendLine($"- eval scope allowlist: {FormatList(snapshot.ShadowQuality.ScopedFormalPreviewOptInEvalScopeAllowlist)}");
            builder.AppendLine($"- preview/baseline packages: {snapshot.ShadowQuality.ScopedFormalPreviewOptInPreviewPackageCount}/{snapshot.ShadowQuality.ScopedFormalPreviewOptInBaselinePackageCount}");
            builder.AppendLine($"- non-allowlisted checked/leaks: {snapshot.ShadowQuality.ScopedFormalPreviewOptInNonAllowlistedScopeChecked}/{snapshot.ShadowQuality.ScopedFormalPreviewOptInNonAllowlistedScopeLeakCount}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.ScopedFormalPreviewOptInRiskAfterPolicy}/{snapshot.ShadowQuality.ScopedFormalPreviewOptInFormalOutputChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.ScopedFormalPreviewOptInPackingPolicyChanged} / {snapshot.ShadowQuality.ScopedFormalPreviewOptInPackageOutputChanged}");
            builder.AppendLine($"- formal package/runtime mutation: {snapshot.ShadowQuality.ScopedFormalPreviewOptInFormalPackageWritten} / {snapshot.ShadowQuality.ScopedFormalPreviewOptInRuntimeMutated}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedFormalPreviewOptInRecommendation)}");
            builder.AppendLine($"- rollback      : {BlankDash(snapshot.ShadowQuality.ScopedFormalPreviewOptInRollbackInstruction)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedFormalPreviewOptInBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedFormalPreviewOptInBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Limited Formal Preview Observation Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LimitedFormalPreviewObservationSourcePath))
        {
            builder.AppendLine("- status        : NoLimitedFormalPreviewObservationReport");
            builder.AppendLine("- action        : run eval vector-limited-formal-preview-observation-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LimitedFormalPreviewObservationSourcePath}");
            builder.AppendLine($"- gate          : {snapshot.ShadowQuality.LimitedFormalPreviewObservationGatePassed}");
            builder.AppendLine($"- mode/profile  : {BlankDash(snapshot.ShadowQuality.LimitedFormalPreviewObservationMode)} / {BlankDash(snapshot.ShadowQuality.LimitedFormalPreviewObservationProfileName)}");
            builder.AppendLine($"- observation runs: {snapshot.ShadowQuality.LimitedFormalPreviewObservationRunCount}");
            builder.AppendLine($"- preview/baseline packages: {snapshot.ShadowQuality.LimitedFormalPreviewObservationPreviewPackageCount}/{snapshot.ShadowQuality.LimitedFormalPreviewObservationBaselinePackageCount}");
            builder.AppendLine($"- add/remove/section: {snapshot.ShadowQuality.LimitedFormalPreviewObservationCandidateAddCount}/{snapshot.ShadowQuality.LimitedFormalPreviewObservationCandidateRemoveCount}/{snapshot.ShadowQuality.LimitedFormalPreviewObservationSectionChangedCount}");
            builder.AppendLine($"- token delta total/max/p95: {snapshot.ShadowQuality.LimitedFormalPreviewObservationTokenDeltaTotal}/{snapshot.ShadowQuality.LimitedFormalPreviewObservationTokenDeltaMax}/{snapshot.ShadowQuality.LimitedFormalPreviewObservationTokenDeltaP95}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.LimitedFormalPreviewObservationRiskAfterPolicy}/{snapshot.ShadowQuality.LimitedFormalPreviewObservationFormalOutputChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.LimitedFormalPreviewObservationPackingPolicyChanged} / {snapshot.ShadowQuality.LimitedFormalPreviewObservationPackageOutputChanged}");
            builder.AppendLine($"- formal package/runtime mutation: {snapshot.ShadowQuality.LimitedFormalPreviewObservationFormalPackageWritten} / {snapshot.ShadowQuality.LimitedFormalPreviewObservationRuntimeMutated}");
            builder.AppendLine($"- scope leaks    : {snapshot.ShadowQuality.LimitedFormalPreviewObservationNonAllowlistedScopeLeakCount}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.LimitedFormalPreviewObservationRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.LimitedFormalPreviewObservationBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.LimitedFormalPreviewObservationBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Formal Preview Freeze Status");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.VectorFormalPreviewFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoVectorFormalPreviewFreezeReport");
            builder.AppendLine("- action        : run eval vector-formal-preview-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.VectorFormalPreviewFreezeSourcePath}");
            builder.AppendLine($"- freeze/status : {snapshot.ShadowQuality.VectorFormalPreviewFreezePassed} / {BlankDash(snapshot.ShadowQuality.VectorFormalPreviewFreezeStatus)}");
            builder.AppendLine($"- allowed mode  : {BlankDash(snapshot.ShadowQuality.VectorFormalPreviewAllowedMode)}");
            builder.AppendLine($"- formal/runtime/use: {snapshot.ShadowQuality.VectorFormalPreviewFormalRetrievalAllowed} / {snapshot.ShadowQuality.VectorFormalPreviewReadyForRuntimeSwitch} / {snapshot.ShadowQuality.VectorFormalPreviewUseForRuntime}");
            builder.AppendLine($"- runtime switch allowed: {snapshot.ShadowQuality.VectorFormalPreviewRuntimeSwitchAllowed}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.VectorFormalPreviewRiskAfterPolicy}/{snapshot.ShadowQuality.VectorFormalPreviewFormalOutputChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.VectorFormalPreviewPackingPolicyChanged} / {snapshot.ShadowQuality.VectorFormalPreviewPackageOutputChanged}");
            builder.AppendLine($"- formal package/runtime mutation: {snapshot.ShadowQuality.VectorFormalPreviewFormalPackageWritten} / {snapshot.ShadowQuality.VectorFormalPreviewRuntimeMutated}");
            builder.AppendLine($"- scope leaks    : {snapshot.ShadowQuality.VectorFormalPreviewScopeLeakCount}");
            builder.AppendLine($"- forbidden     : {(snapshot.ShadowQuality.VectorFormalPreviewForbiddenChanges.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.VectorFormalPreviewForbiddenChanges))}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.VectorFormalPreviewFreezeRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.VectorFormalPreviewBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.VectorFormalPreviewBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Explicit Scoped Runtime Experiment Plan Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentSourcePath))
        {
            builder.AppendLine("- status        : NoExplicitScopedRuntimeExperimentPlanReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentSourcePath}");
            builder.AppendLine($"- passed        : {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentPlanPassed}");
            builder.AppendLine($"- mode/profile  : {BlankDash(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentMode)} / {BlankDash(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentProfileName)}");
            builder.AppendLine($"- workspace allowlist: {FormatList(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentWorkspaceAllowlist)}");
            builder.AppendLine($"- collection allowlist: {FormatList(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentCollectionAllowlist)}");
            builder.AppendLine($"- eval scope allowlist: {FormatList(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentEvalScopeAllowlist)}");
            builder.AppendLine($"- dry-run supported: {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentDryRunSupported}");
            builder.AppendLine($"- runtime/formal/ready: {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentRuntimeSwitchAllowed} / {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentFormalRetrievalAllowed} / {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentReadyForRuntimeSwitch}");
            builder.AppendLine($"- use/runtime mutation: {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentUseForRuntime} / {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentRuntimeMutated}");
            builder.AppendLine($"- formal package/packing/package: {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentFormalPackageWritten} / {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentPackingPolicyChanged} / {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentPackageOutputChanged}");
            builder.AppendLine($"- scope leaks    : {snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentScopeLeakCount}");
            builder.AppendLine($"- required gates : {FormatMap(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentRequiredGateSummary, maxItems: 4)}");
            builder.AppendLine($"- allowed       : {(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentAllowedActions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentAllowedActions.Take(4)))}");
            builder.AppendLine($"- forbidden     : {(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentForbiddenActions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentForbiddenActions.Take(4)))}");
            builder.AppendLine($"- rollback      : {BlankDash(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentRollbackPlan)}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ExplicitScopedRuntimeExperimentBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Dry-run Observation Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentDryRunObservationReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-dry-run-observation-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationSourcePath}");
            builder.AppendLine($"- gate          : {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationGatePassed}");
            builder.AppendLine($"- mode/profile  : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationMode)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationProfileName)}");
            builder.AppendLine($"- observation runs: {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationRunCount}");
            builder.AppendLine($"- workspace allowlist: {FormatList(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationWorkspaceAllowlist)}");
            builder.AppendLine($"- collection allowlist: {FormatList(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationCollectionAllowlist)}");
            builder.AppendLine($"- eval scope allowlist: {FormatList(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationEvalScopeAllowlist)}");
            builder.AppendLine($"- dry-run/baseline packages: {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationDryRunPackageCount}/{snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationBaselinePackageCount}");
            builder.AppendLine($"- add/remove    : {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationCandidateAddCount}/{snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationCandidateRemoveCount}");
            builder.AppendLine($"- token delta total/max: {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationTokenDeltaTotal}/{snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationTokenDeltaMax}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationRiskAfterPolicy}/{snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationFormalOutputChanged}");
            builder.AppendLine($"- formal package/runtime/vector binding: {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationFormalPackageWritten} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationRuntimeMutated} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationVectorStoreBindingChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationPackingPolicyChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationPackageOutputChanged}");
            builder.AppendLine($"- scope leaks    : {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationNonAllowlistedScopeLeakCount}");
            builder.AppendLine($"- rollback plan : {snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationRollbackPlanAvailable}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentDryRunObservationBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Design Freeze Status");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentDesignFreezeReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-design-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeSourcePath}");
            builder.AppendLine($"- freeze/status : {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezePassed} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeStatus)}");
            builder.AppendLine($"- allowed mode  : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeAllowedMode)}");
            builder.AppendLine($"- scopes/runs   : {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeAllowlistedScopeCount}/{snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeObservationRunCount}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeRiskAfterPolicy}/{snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeFormalOutputChanged}");
            builder.AppendLine($"- formal package/runtime/vector binding: {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeFormalPackageWritten} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeRuntimeMutated} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeVectorStoreBindingChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezePackingPolicyChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezePackageOutputChanged}");
            builder.AppendLine($"- scope leaks    : {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeScopeLeakCount}");
            builder.AppendLine($"- rollback/proposal: {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeRollbackPlanAvailable} / {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeReadyForRuntimeExperimentProposal}");
            builder.AppendLine($"- runtime switch : {snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeReadyForRuntimeSwitch}");
            builder.AppendLine($"- forbidden     : {(snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeForbiddenActions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeForbiddenActions.Take(5)))}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentDesignFreezeBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Explicit Scoped Runtime Experiment Proposal Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentProposalReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-proposal-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalSourcePath}");
            builder.AppendLine($"- proposal/pass : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalId)} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalPassed}");
            builder.AppendLine($"- selected scope: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalWorkspaceId)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalCollectionId)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalEvalScopeId)}");
            builder.AppendLine($"- profile       : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalProfileName)}");
            builder.AppendLine($"- approval      : required={snapshot.ShadowQuality.ScopedRuntimeExperimentProposalApprovalRequired}, approved={snapshot.ShadowQuality.ScopedRuntimeExperimentProposalApproved}");
            builder.AppendLine($"- runtime/formal/ready: {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalRuntimeSwitchAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalFormalRetrievalAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalReadyForRuntimeSwitch}");
            builder.AppendLine($"- use/package/config/di: {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalUseForRuntime} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalWriteFormalPackage} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalConfigPatchWritten} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalDiBindingChanged}");
            builder.AppendLine($"- packing/package changed: {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalPackingPolicyChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentProposalPackageOutputChanged}");
            builder.AppendLine($"- rollback     : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalRollbackPlan)}");
            builder.AppendLine($"- kill switch  : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalKillSwitchPlan)}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentProposalBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentProposalBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Approval / No-op Harness Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalSummarySourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentApprovalReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-approval-summary and eval vector-scoped-runtime-experiment-noop-harness-gate");
        }
        else
        {
            builder.AppendLine($"- approval source: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalSummarySourcePath)}");
            builder.AppendLine($"- proposal/approval: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalProposalId)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalId)}");
            builder.AppendLine($"- approval mode : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalMode)}");
            builder.AppendLine($"- approval count: {snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalCount}, exists={snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalRecordExists}");
            builder.AppendLine($"- expired/revoked: {snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalExpired} / {snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalRevoked}");
            builder.AppendLine($"- approval recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalRecommendation)}");
            builder.AppendLine($"- harness source: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessSourcePath)}");
            builder.AppendLine($"- harness/traces: {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessPassed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessTraceCount}");
            builder.AppendLine($"- runtime/vector binding: {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessRuntimeMutated} / {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessVectorStoreBindingChanged}");
            builder.AppendLine($"- formal package/packing/package: {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessFormalPackageWritten} / {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessPackingPolicyChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessPackageOutputChanged}");
            builder.AppendLine($"- formal/runtime/ready: {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessFormalRetrievalAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessRuntimeSwitchAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessReadyForRuntimeSwitch}");
            builder.AppendLine("- forbidden     : runtime switch, formal retrieval, formal package write, DI/vector binding change, PackingPolicy/package mutation");
            builder.AppendLine($"- harness recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessRecommendation)}");
            var blocked = snapshot.ShadowQuality.ScopedRuntimeExperimentNoOpHarnessBlockedReasons
                .Concat(snapshot.ShadowQuality.ScopedRuntimeExperimentApprovalBlockedReasons)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            builder.AppendLine($"- blocked       : {(blocked.Length == 0 ? "-" : string.Join(", ", blocked))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Harness Freeze Status");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentHarnessFreezeReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-harness-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeSourcePath}");
            builder.AppendLine($"- freeze        : {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezePassed}");
            builder.AppendLine($"- proposal/approval: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeProposalId)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeApprovalId)}");
            builder.AppendLine($"- approval mode : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeApprovalMode)}");
            builder.AppendLine($"- harness status: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeHarnessStatus)}");
            builder.AppendLine($"- allowed mode  : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeAllowedMode)}");
            builder.AppendLine($"- next phase    : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeNextAllowedPhase)}");
            builder.AppendLine($"- runtime/vector binding: {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeRuntimeMutated} / {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeVectorStoreBindingChanged}");
            builder.AppendLine($"- formal package/packing/package: {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeFormalPackageWritten} / {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezePackingPolicyChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezePackageOutputChanged}");
            builder.AppendLine($"- formal/runtime/ready: {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeFormalRetrievalAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeRuntimeSwitchAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeReadyForRuntimeSwitch}");
            builder.AppendLine($"- forbidden     : {(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeForbiddenActions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeForbiddenActions.Take(6)))}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentHarnessFreezeBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Guarded Scoped Runtime Experiment Plan Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentPlanSourcePath))
        {
            builder.AppendLine("- status        : NoGuardedScopedRuntimeExperimentPlanReport");
            builder.AppendLine("- action        : run eval vector-guarded-scoped-runtime-experiment-plan-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentPlanSourcePath}");
            builder.AppendLine($"- plan passed   : {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentPlanPassed}");
            builder.AppendLine($"- proposal      : {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentProposalId)}");
            builder.AppendLine($"- approval mode : {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRequiredApprovalMode)}");
            builder.AppendLine($"- scopes        : {FormatList(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentSelectedScopes)}");
            builder.AppendLine($"- max request/duration: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentMaxRequestCount}/{snapshot.ShadowQuality.GuardedScopedRuntimeExperimentMaxDurationMinutes}");
            builder.AppendLine($"- kill switch   : {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentKillSwitchPlan)}");
            builder.AppendLine($"- rollback      : {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRollbackPlan)}");
            builder.AppendLine($"- stop conditions: {(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentStopConditions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedScopedRuntimeExperimentStopConditions.Take(5)))}");
            builder.AppendLine($"- observation   : {(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentObservationPlan.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedScopedRuntimeExperimentObservationPlan.Take(5)))}");
            builder.AppendLine($"- forbidden     : {(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentForbiddenActions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedScopedRuntimeExperimentForbiddenActions.Take(5)))}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentPlanRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedScopedRuntimeExperimentBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Approval Gate Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentRuntimeApprovalReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-approval-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalSourcePath}");
            builder.AppendLine($"- gate passed   : {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalGatePassed}");
            builder.AppendLine($"- proposal/approval: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalProposalId)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalId)}");
            builder.AppendLine($"- approval mode : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalMode)}");
            builder.AppendLine($"- approved by   : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalApprovedBy)}");
            builder.AppendLine($"- exists/expired/revoked: {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalExists} / {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalExpired} / {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalRevoked}");
            builder.AppendLine($"- acknowledgements: {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalAcknowledgementsPresent}");
            builder.AppendLine($"- runtime/formal/ready/use: {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalRuntimeSwitchAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalFormalRetrievalAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalReadyForRuntimeSwitch} / {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalUseForRuntime}");
            builder.AppendLine($"- formal package/packing integration: {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalFormalPackageWriteAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalPackingPolicyIntegrationAllowed}");
            builder.AppendLine("- next phase    : V4.13 activation preflight only");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentRuntimeApprovalBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Activation Preflight / Dry-run Route Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationPreflightSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentActivationPreflightReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-activation-preflight and eval vector-scoped-runtime-experiment-dry-run-route");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationPreflightSourcePath}");
            builder.AppendLine($"- preflight passed: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationPreflightPassed}");
            builder.AppendLine($"- proposal/approval: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationProposalId)} / {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationApprovalId)}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationMode)}");
            builder.AppendLine($"- scopes        : {(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationSelectedScopes.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentActivationSelectedScopes))}");
            builder.AppendLine($"- kill/rollback/trace: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationKillSwitchAvailable} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationRollbackPlanAvailable} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationTraceSinkAvailable}");
            builder.AppendLine($"- config preview/written: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationConfigPatchPreviewed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationConfigPatchWritten}");
            builder.AppendLine($"- dry-run route : executed={snapshot.ShadowQuality.ScopedRuntimeExperimentActivationDryRunRouteExecuted}; hits={snapshot.ShadowQuality.ScopedRuntimeExperimentActivationDryRunRouteHitCount}");
            builder.AppendLine($"- non-allow leak: checked={snapshot.ShadowQuality.ScopedRuntimeExperimentActivationNonAllowlistedScopeChecked}; leak={snapshot.ShadowQuality.ScopedRuntimeExperimentActivationScopeLeakCount}");
            builder.AppendLine($"- runtime/vector/formal package: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationRuntimeMutated} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationVectorStoreBindingChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationFormalPackageWritten}");
            builder.AppendLine($"- packing/package: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationPackingPolicyChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationPackageOutputChanged}");
            builder.AppendLine($"- formal/runtime/ready: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationFormalRetrievalAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationRuntimeSwitchAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationReadyForRuntimeSwitch}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationRiskAfterPolicy} / {snapshot.ShadowQuality.ScopedRuntimeExperimentActivationFormalOutputChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationPreflightRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentActivationBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentActivationBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Guarded Scoped Runtime Experiment Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunSourcePath))
        {
            builder.AppendLine("- status        : NoGuardedScopedRuntimeExperimentReport");
            builder.AppendLine("- action        : run eval vector-guarded-scoped-runtime-experiment and eval vector-guarded-scoped-runtime-experiment-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunSourcePath}");
            builder.AppendLine($"- experiment passed: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunPassed}");
            builder.AppendLine($"- proposal/approval: {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunProposalId)} / {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunApprovalId)}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunMode)}");
            builder.AppendLine($"- scopes        : {(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunSelectedScopes.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunSelectedScopes))}");
            builder.AppendLine($"- requests/hits : {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunRequestCount} / {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunRouteHitCount}");
            builder.AppendLine($"- non-allow leak: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunNonAllowlistedLeakCount}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunRiskAfterPolicy} / {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunFormalOutputChanged}");
            builder.AppendLine($"- package/packing: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunPackageOutputChanged} / {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunPackingPolicyChanged}");
            builder.AppendLine($"- runtime/vector/formal package: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunRuntimeMutated} / {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunVectorStoreBindingChanged} / {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunFormalPackageWritten}");
            builder.AppendLine($"- kill switch   : available={snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunKillSwitchAvailable}; triggered={snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunKillSwitchTriggered}");
            builder.AppendLine($"- rollback/errors: {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunRollbackVerified} / {snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunErrorCount}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GuardedScopedRuntimeExperimentRunBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Observation Window Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentObservationWindowReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-observation-window-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowSourcePath}");
            builder.AppendLine($"- window        : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowId)}");
            builder.AppendLine($"- passed        : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowPassed}");
            builder.AppendLine($"- runs/requests : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRunCount} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRequestCount}");
            builder.AppendLine($"- route hits    : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRouteHitCount}");
            builder.AppendLine($"- scope leak    : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowScopeLeakCount}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRiskAfterPolicy} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowFormalOutputChanged}");
            builder.AppendLine($"- package/packing: {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowPackageOutputChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowPackingPolicyChanged}");
            builder.AppendLine($"- runtime/vector/formal package: {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRuntimeMutated} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowVectorStoreBindingChanged} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowFormalPackageWritten}");
            builder.AppendLine($"- kill/rollback : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowKillSwitchAvailable}/{snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowKillSwitchSmokePassed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRollbackVerified}");
            builder.AppendLine($"- trace/errors/latencyP95: {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowTraceCompleteness} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowErrorCount} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowLatencyP95}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentObservationWindowBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("Scoped Runtime Experiment Observation Freeze / Promotion Decision Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoScopedRuntimeExperimentObservationFreezeReport");
            builder.AppendLine("- action        : run eval vector-scoped-runtime-experiment-promotion-decision");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeSourcePath}");
            builder.AppendLine($"- window        : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeWindowId)}");
            builder.AppendLine($"- freeze passed : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezePassed}");
            builder.AppendLine($"- decision      : {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentPromotionDecision)}");
            builder.AppendLine($"- requests/hits : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeRequestCount} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeRouteHitCount}");
            builder.AppendLine($"- risk/formal output: {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeRiskAfterPolicy} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeFormalOutputChanged}");
            builder.AppendLine($"- trace         : {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeTraceCompleteness}");
            builder.AppendLine($"- formal/runtime: {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeFormalRetrievalAllowed} / {snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeRuntimeSwitchAllowed}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ScopedRuntimeExperimentObservationFreezeBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Formal Retrieval Integration Plan Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FormalRetrievalIntegrationPlanSourcePath))
        {
            builder.AppendLine("- status        : NoFormalRetrievalIntegrationPlanReport");
            builder.AppendLine("- action        : run eval vector-formal-retrieval-integration-plan-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.FormalRetrievalIntegrationPlanSourcePath}");
            builder.AppendLine($"- passed        : {snapshot.ShadowQuality.FormalRetrievalIntegrationPlanPassed}");
            builder.AppendLine($"- mode/next     : {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationPlanAllowedMode)} / {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationPlanRequiredNextPhase)}");
            builder.AppendLine($"- formal/runtime/ready: {snapshot.ShadowQuality.FormalRetrievalIntegrationPlanFormalRetrievalAllowed} / {snapshot.ShadowQuality.FormalRetrievalIntegrationPlanRuntimeSwitchAllowed} / {snapshot.ShadowQuality.FormalRetrievalIntegrationPlanReadyForRuntimeSwitch}");
            builder.AppendLine($"- integration   : {(snapshot.ShadowQuality.FormalRetrievalIntegrationPlanIntegrationPoints.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.FormalRetrievalIntegrationPlanIntegrationPoints.Take(5)))}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationPlanRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.FormalRetrievalIntegrationPlanBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.FormalRetrievalIntegrationPlanBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Formal Retrieval Integration Decision Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionSourcePath))
        {
            builder.AppendLine("- status        : NoFormalRetrievalIntegrationDecisionReport");
            builder.AppendLine("- action        : run eval vector-formal-retrieval-integration-decision-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionSourcePath}");
            builder.AppendLine($"- decision/gate : {snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionPassed} / {snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionGatePassed}");
            builder.AppendLine($"- decision      : {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionValue)}");
            builder.AppendLine($"- next          : {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionNextAllowedPhase)}");
            builder.AppendLine($"- ready         : freeze={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionReadyForFreeze} noopBindingPlan={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionReadyForNoOpBindingPlan}");
            builder.AppendLine($"- risk/output   : risk={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionRiskAfterPolicy} formalOutputChanged={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionFormalOutputChanged}");
            builder.AppendLine($"- invariants    : formalRetrieval={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionFormalRetrievalAllowed} runtimeSwitch={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionRuntimeSwitchAllowed} readyRuntime={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionReadyForRuntimeSwitch} packageOutput={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionPackageOutputChanged} packingPolicy={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionPackingPolicyChanged} runtimeMutated={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionRuntimeMutated} vectorBinding={snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.FormalRetrievalIntegrationDecisionBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Shadow Formal Retrieval Adapter Plan Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanSourcePath))
        {
            builder.AppendLine("- status        : NoShadowFormalRetrievalAdapterPlanReport");
            builder.AppendLine("- action        : run eval vector-shadow-formal-retrieval-adapter-plan-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanSourcePath}");
            builder.AppendLine($"- passed        : {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanPassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanAllowedMode)}");
            builder.AppendLine($"- vector source : {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanVectorProviderSource)}");
            builder.AppendLine($"- graph source  : {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanGraphCandidateSource)}");
            builder.AppendLine($"- formal/runtime: {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanFormalRetrievalAllowed} / {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanRuntimeSwitchAllowed}");
            builder.AppendLine($"- forbidden     : {(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanForbiddenActions.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanForbiddenActions.Take(5)))}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPlanBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Shadow Formal Retrieval Adapter Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterSourcePath))
        {
            builder.AppendLine("- status        : NoShadowFormalRetrievalAdapterReport");
            builder.AppendLine("- action        : run eval vector-shadow-formal-retrieval-adapter-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPassed} / {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterAllowedMode)}");
            builder.AppendLine($"- samples       : {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterSampleCount}");
            builder.AppendLine($"- risk/mustNot/lifecycle: {snapshot.ShadowQuality.ShadowFormalRetrievalAdapterRiskAfterPolicy}/{snapshot.ShadowQuality.ShadowFormalRetrievalAdapterMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.ShadowFormalRetrievalAdapterLifecycleRiskAfterPolicy}");
            builder.AppendLine($"- vector source : {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterVectorProviderSource)}");
            builder.AppendLine($"- graph source  : {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterGraphCandidateSource)}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.ShadowFormalRetrievalAdapterFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.ShadowFormalRetrievalAdapterFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.ShadowFormalRetrievalAdapterPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.ShadowFormalRetrievalAdapterRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.ShadowFormalRetrievalAdapterVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.ShadowFormalRetrievalAdapterBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ShadowFormalRetrievalAdapterBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Package Shadow Comparison Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonSourcePath))
        {
            builder.AppendLine("- status        : NoFormalAdapterPackageShadowComparisonReport");
            builder.AppendLine("- action        : run eval vector-formal-adapter-package-shadow-comparison-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonPassed} / {snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonAllowedMode)}");
            builder.AppendLine($"- samples       : {snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonSampleCount}");
            builder.AppendLine($"- token delta   : total={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonTokenDeltaTotal} max={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonTokenDeltaMax} (budget total={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonTokenDeltaBudgetTotal}, per-sample={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonTokenDeltaBudgetPerSample})");
            builder.AppendLine($"- risk/mustNot/lifecycle: {snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonRiskAfterPolicy}/{snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonLifecycleRiskAfterPolicy}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.FormalAdapterPackageShadowComparisonBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Retrieval Quality Audit Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditSourcePath))
        {
            builder.AppendLine("- status        : NoGraphVectorRetrievalQualityAuditReport");
            builder.AppendLine("- action        : run eval vector-graph-retrieval-quality-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditPassed} / {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditAllowedMode)}");
            builder.AppendLine($"- samples       : {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditSampleCount}");
            builder.AppendLine($"- recall/precision/mrr: {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditRecall:F4}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditPrecision:F4}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditMrr:F4}");
            builder.AppendLine($"- graphNoise/vectorNoise/rankingRegression/mustHitBelowTopK: {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditGraphNoiseCount}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditVectorNoiseCount}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditRankingRegressionCount}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditMustHitBelowTopKCount}");
            builder.AppendLine($"- risk/mustNot/lifecycle/sectionMismatch/metadataGap: {snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditRiskAfterPolicy}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditLifecycleRiskAfterPolicy}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditSectionMismatchCount}/{snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditMetadataEvidenceGapCount}");
            builder.AppendLine($"- clusters      : {(snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditFailureClusterIds.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditFailureClusterIds))}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.GraphVectorRetrievalQualityAuditBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Retrieval Quality Repair Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalQualityRepairPreviewSourcePath))
        {
            builder.AppendLine("- status        : NoRetrievalQualityRepairPreviewReport");
            builder.AppendLine("- action        : run eval vector-retrieval-quality-repair-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RetrievalQualityRepairPreviewSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.RetrievalQualityRepairPreviewPassed} / {snapshot.ShadowQuality.RetrievalQualityRepairPreviewGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.RetrievalQualityRepairPreviewAllowedMode)}");
            builder.AppendLine($"- best profile  : {BlankDash(snapshot.ShadowQuality.RetrievalQualityRepairPreviewBestProfileId)}");
            builder.AppendLine($"- baseline R/P/MRR  : {snapshot.ShadowQuality.RetrievalQualityRepairPreviewBaselineRecall:F4}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewBaselinePrecision:F4}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewBaselineMrr:F4}");
            builder.AppendLine($"- best     R/P/MRR  : {snapshot.ShadowQuality.RetrievalQualityRepairPreviewBestRecall:F4}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewBestPrecision:F4}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewBestMrr:F4}");
            builder.AppendLine($"- delta    R/MRR    : {snapshot.ShadowQuality.RetrievalQualityRepairPreviewRecallDelta:+0.0000;-0.0000;0.0000}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewMrrDelta:+0.0000;-0.0000;0.0000}");
            builder.AppendLine($"- mustHitBelowTopK base/best: {snapshot.ShadowQuality.RetrievalQualityRepairPreviewMustHitBelowTopKBaseline}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewMustHitBelowTopKBest}");
            builder.AppendLine($"- profiles evaluated: {snapshot.ShadowQuality.RetrievalQualityRepairPreviewProfileEvaluatedCount}");
            builder.AppendLine($"- risk/mustNot/lifecycle/section/graphNoise/rankingRegression: {snapshot.ShadowQuality.RetrievalQualityRepairPreviewRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewLifecycleRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewSectionMismatchCount}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewGraphNoiseCount}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewRankingRegressionCount}");
            builder.AppendLine($"- token delta total/max: {snapshot.ShadowQuality.RetrievalQualityRepairPreviewTokenDeltaTotal}/{snapshot.ShadowQuality.RetrievalQualityRepairPreviewTokenDeltaMax}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.RetrievalQualityRepairPreviewFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.RetrievalQualityRepairPreviewFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.RetrievalQualityRepairPreviewPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.RetrievalQualityRepairPreviewPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.RetrievalQualityRepairPreviewRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.RetrievalQualityRepairPreviewVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.RetrievalQualityRepairPreviewRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.RetrievalQualityRepairPreviewBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.RetrievalQualityRepairPreviewBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Runtime-observable Feature Contract Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RuntimeObservableFeatureContractSourcePath))
        {
            builder.AppendLine("- status        : NoRuntimeObservableFeatureContractReport");
            builder.AppendLine("- action        : run eval vector-runtime-observable-feature-contract-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RuntimeObservableFeatureContractSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.RuntimeObservableFeatureContractPassed} / {snapshot.ShadowQuality.RuntimeObservableFeatureContractGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.RuntimeObservableFeatureContractAllowedMode)}");
            builder.AppendLine($"- best profile  : {BlankDash(snapshot.ShadowQuality.RuntimeObservableFeatureContractBestProfileId)} (status={BlankDash(snapshot.ShadowQuality.RuntimeObservableFeatureContractBestProfileContractStatus)})");
            builder.AppendLine($"- feature counts: scoring={snapshot.ShadowQuality.RuntimeObservableFeatureContractScoringFeatureCount} filtering={snapshot.ShadowQuality.RuntimeObservableFeatureContractFilteringFeatureCount} candidateExpansion={snapshot.ShadowQuality.RuntimeObservableFeatureContractCandidateExpansionFeatureCount}");
            builder.AppendLine($"- classification: runtimeObservable={snapshot.ShadowQuality.RuntimeObservableFeatureContractRuntimeObservableCount} derivedAtRuntime={snapshot.ShadowQuality.RuntimeObservableFeatureContractDerivedAtRuntimeCount} evalOnly={snapshot.ShadowQuality.RuntimeObservableFeatureContractEvalOnlyCount} forbiddenForScoring={snapshot.ShadowQuality.RuntimeObservableFeatureContractForbiddenForScoringCount}");
            builder.AppendLine($"- source scan   : files={snapshot.ShadowQuality.RuntimeObservableFeatureContractSourceScanFiles} fixtureHits={snapshot.ShadowQuality.RuntimeObservableFeatureContractFixtureTokenHitCount} tokens={(snapshot.ShadowQuality.RuntimeObservableFeatureContractFlaggedTokens.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.RuntimeObservableFeatureContractFlaggedTokens))}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.RuntimeObservableFeatureContractFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.RuntimeObservableFeatureContractFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.RuntimeObservableFeatureContractPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.RuntimeObservableFeatureContractPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.RuntimeObservableFeatureContractRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.RuntimeObservableFeatureContractVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.RuntimeObservableFeatureContractRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.RuntimeObservableFeatureContractBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.RuntimeObservableFeatureContractBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Runtime Feature Derivation Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationSourcePath))
        {
            builder.AppendLine("- status        : NoRuntimeRetrievalFeatureDerivationReport");
            builder.AppendLine("- action        : run eval vector-runtime-feature-derivation-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationPassed} / {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationAllowedMode)}");
            builder.AppendLine($"- samples       : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationSampleCount}");
            builder.AppendLine($"- coverage      : target={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationTargetSectionMatchRate:F4} relation={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRequiredRelationCoverageRate:F4} evidence={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationEvidenceAnchorCoverageRate:F4} source={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationSourceAnchorCoverageRate:F4} completeness={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivationCompletenessRate:F4}");
            builder.AppendLine($"- recall/MRR    : baseline={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationBaselineRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationBaselineMrr:F4} derived={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedMrr:F4} eval-driven={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationEvalDrivenRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationEvalDrivenMrr:F4}");
            builder.AppendLine($"- delta R/MRR   : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedRecallDelta:+0.0000;-0.0000;0.0000}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedMrrDelta:+0.0000;-0.0000;0.0000}");
            builder.AppendLine($"- risk/mustNot/lifecycle/section: {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedRiskAfterPolicy}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedLifecycleRiskAfterPolicy}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationDerivedSectionMismatchCount}");
            builder.AppendLine($"- forbiddenReads: {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationForbiddenSampleAnnotationReadCount}");
            builder.AppendLine($"- source scan   : files={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationSourceScanFiles} fixtureHits={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationFixtureTokenHitCount}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Runtime Feature Derivation Repair Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairSourcePath))
        {
            builder.AppendLine("- status        : NoRuntimeRetrievalFeatureDerivationRepairReport");
            builder.AppendLine("- action        : run eval vector-runtime-feature-derivation-repair-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairPassed} / {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairGatePassed}");
            builder.AppendLine($"- mode          : {BlankDash(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairAllowedMode)}");
            builder.AppendLine($"- train/holdout : {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairTrainSampleCount} / {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairHoldoutSampleCount}");
            builder.AppendLine($"- train recall/mrr : baseline={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairTrainBaselineRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairTrainBaselineMrr:F4} derived={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairTrainDerivedRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairTrainDerivedMrr:F4}");
            builder.AppendLine($"- holdout recall/mrr: baseline={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineMrr:F4} derived={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedRecall:F4}/{snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedMrr:F4}");
            builder.AppendLine($"- canonical coverage: relation={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairCanonicalRelationCoverageRate:F4} evidence={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairCanonicalEvidenceCoverageRate:F4} source={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairCanonicalSourceCoverageRate:F4}");
            builder.AppendLine($"- risk/forbiddenReads/sourceScan: {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairDerivedRiskAfterPolicy} / {snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairForbiddenSampleAnnotationReadCount} / files={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairSourceScanFiles} fixtureHits={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairFixtureTokenHitCount}");
            builder.AppendLine($"- invariants    : formalOutputChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairFormalOutputChanged}, formalSelectedSetChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairFormalSelectedSetChanged}, packageOutputChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairPackageOutputChanged}, packingPolicyChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairPackingPolicyChanged}, runtimeMutated={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairRuntimeMutated}, vectorStoreBindingChanged={snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairVectorStoreBindingChanged}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairRecommendation)}");
            builder.AppendLine($"- blocked       : {(snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairBlockedReasons.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.RuntimeRetrievalFeatureDerivationRepairBlockedReasons))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Formal Integration Freeze Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FormalRetrievalIntegrationFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoFormalRetrievalIntegrationFreezeReport");
            builder.AppendLine("- action        : run eval vector-formal-retrieval-integration-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.FormalRetrievalIntegrationFreezeSourcePath}");
            builder.AppendLine($"- freezePassed  : {snapshot.ShadowQuality.FormalRetrievalIntegrationFreezePassed}");
            builder.AppendLine($"- profile       : {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationFreezeSelectedProfile)}");
            builder.AppendLine($"- rec           : {BlankDash(snapshot.ShadowQuality.FormalRetrievalIntegrationFreezeRecommendation)}");
            builder.AppendLine($"- artifacts     : {snapshot.ShadowQuality.FormalRetrievalIntegrationFreezeFrozenArtifactCount}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Runtime Feature Derivation Failure Freeze Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FeatureDerivationFailureFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoRuntimeFeatureDerivationFailureFreezeReport");
            builder.AppendLine("- action        : run eval vector-runtime-feature-derivation-failure-freeze");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.FeatureDerivationFailureFreezeSourcePath}");
            builder.AppendLine($"- freezePassed  : {snapshot.ShadowQuality.FeatureDerivationFailureFreezePassed}");
            builder.AppendLine($"- status        : {snapshot.ShadowQuality.FeatureDerivationFailureFreezeStatus}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.FeatureDerivationFailureFreezeRecommendation)}");
            builder.AppendLine($"- resolver reusable : {snapshot.ShadowQuality.FeatureDerivationFailureFreezeCanonicalResolverReusable}");
            builder.AppendLine($"- deriver ready     : {snapshot.ShadowQuality.FeatureDerivationFailureFreezeRelationDeriverReady}");
            builder.AppendLine($"- disabled caps    : {(snapshot.ShadowQuality.FeatureDerivationFailureFreezeDisabledCapabilities.Count == 0 ? "-" : string.Join("; ", snapshot.ShadowQuality.FeatureDerivationFailureFreezeDisabledCapabilities.Take(3)))}");
            builder.AppendLine($"- next phases      : {(snapshot.ShadowQuality.FeatureDerivationFailureFreezeRecommendedNextPhases.Count == 0 ? "-" : string.Join("; ", snapshot.ShadowQuality.FeatureDerivationFailureFreezeRecommendedNextPhases.Take(3)))}");
        }

        builder.AppendLine();
        builder.AppendLine("V5 Graph Hub Noise Control Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.GraphHubNoiseControlSourcePath))
        {
            builder.AppendLine("- status        : NoGraphHubNoiseControlReport");
            builder.AppendLine("- action        : run eval vector-graph-hub-noise-control-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.GraphHubNoiseControlSourcePath}");
            builder.AppendLine($"- passed/gate   : {snapshot.ShadowQuality.GraphHubNoiseControlPassed} / {snapshot.ShadowQuality.GraphHubNoiseControlGatePassed}");
            builder.AppendLine($"- hubItems      : {snapshot.ShadowQuality.GraphHubNoiseControlHubItemCount}");
            builder.AppendLine($"- avgDominance  : {snapshot.ShadowQuality.GraphHubNoiseControlAvgDominance:F4}");
            builder.AppendLine($"- baseline/hub-ctrl recall: {snapshot.ShadowQuality.GraphHubNoiseControlBaselineRecall:F4}/{snapshot.ShadowQuality.GraphHubNoiseControlHubCtrlRecall:F4}");
            builder.AppendLine($"- recall delta  : {snapshot.ShadowQuality.GraphHubNoiseControlRecallDelta:+0.0000;-0.0000;0.0000}");
            builder.AppendLine($"- recommendation: {BlankDash(snapshot.ShadowQuality.GraphHubNoiseControlRecommendation)}");
        }

        builder.AppendLine();
        builder.AppendLine("V5.11 Retrieval Eval Protocol / Source Discriminability Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalEvalProtocolGateSourcePath)
            && string.IsNullOrWhiteSpace(snapshot.ShadowQuality.RetrievalEvalProtocolSourceAuditPath))
        {
            AppendMissingSummaryState(builder, "NoRetrievalEvalProtocolAuditReport", "run eval vector-retrieval-eval-protocol-gate");
        }
        else
        {
            AppendMetricLine(builder, "gate source", BlankDash(snapshot.ShadowQuality.RetrievalEvalProtocolGateSourcePath));
            AppendMetricLine(builder, "audit source", BlankDash(snapshot.ShadowQuality.RetrievalEvalProtocolSourceAuditPath));
            AppendBooleanInvariantLine(builder, "gate passed", snapshot.ShadowQuality.RetrievalEvalProtocolGatePassed);
            AppendMetricLine(builder, "protocol", $"{BlankDash(snapshot.ShadowQuality.RetrievalEvalProtocolVersion)} topK vector/merged/final={snapshot.ShadowQuality.RetrievalEvalProtocolVectorTopK}/{snapshot.ShadowQuality.RetrievalEvalProtocolMergedTopK}/{snapshot.ShadowQuality.RetrievalEvalProtocolFinalTopK}");
            AppendMetricLine(builder, "reproducible", $"tieBreak={snapshot.ShadowQuality.RetrievalEvalProtocolTieBreakDeterministic} hashOrderSensitivity={snapshot.ShadowQuality.RetrievalEvalProtocolHashOrderSensitivityCount}");
            AppendMetricLine(builder, "recall", $"baseline={snapshot.ShadowQuality.RetrievalEvalProtocolBaselineRecall:F4} merged={snapshot.ShadowQuality.RetrievalEvalProtocolMergedRecall:F4}");
            AppendMetricLine(builder, "source shape", $"nonDiscriminative={snapshot.ShadowQuality.RetrievalEvalProtocolSourceNonDiscriminativeDetected} count={snapshot.ShadowQuality.RetrievalEvalProtocolNonDiscriminativeSourceCount} templateHomogeneity={snapshot.ShadowQuality.RetrievalEvalProtocolTemplateHomogeneityScore:F4} detected={snapshot.ShadowQuality.RetrievalEvalProtocolTemplateHomogeneityDetected}");
            AppendMetricLine(builder, "risk/mustNot/lifecycle", $"{snapshot.ShadowQuality.RetrievalEvalProtocolRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalEvalProtocolMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.RetrievalEvalProtocolLifecycleRiskAfterPolicy}");
            AppendBooleanInvariantLine(builder, "runtime gate", snapshot.ShadowQuality.RetrievalEvalProtocolRuntimeChangeGatePassed);
            AppendRecommendationLine(builder, snapshot.ShadowQuality.RetrievalEvalProtocolRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.RetrievalEvalProtocolBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("V5.12 Input Metadata Enrichment Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.InputMetadataEnrichmentSourcePath))
        {
            AppendMissingSummaryState(builder, "NoInputMetadataEnrichmentReport", "run eval vector-input-metadata-enrichment-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.InputMetadataEnrichmentSourcePath));
            AppendMetricLine(builder, "preview/gate", $"{snapshot.ShadowQuality.InputMetadataEnrichmentPreviewPassed} / {snapshot.ShadowQuality.InputMetadataEnrichmentGatePassed}");
            AppendMetricLine(builder, "coverage delta", snapshot.ShadowQuality.InputMetadataEnrichmentCoverageDelta.ToString());
            AppendMetricLine(builder, "recall", $"{snapshot.ShadowQuality.InputMetadataEnrichmentBeforeRecall:F4} -> {snapshot.ShadowQuality.InputMetadataEnrichmentAfterRecall:F4}");
            AppendMetricLine(builder, "non-dense independent sources", snapshot.ShadowQuality.InputMetadataEnrichmentIndependentNonDenseSourceCount.ToString());
            AppendMetricLine(builder, "risk/mustNot/lifecycle", $"{snapshot.ShadowQuality.InputMetadataEnrichmentRiskAfterPolicy}/{snapshot.ShadowQuality.InputMetadataEnrichmentMustNotHitRiskAfterPolicy}/{snapshot.ShadowQuality.InputMetadataEnrichmentLifecycleRiskAfterPolicy}");
            AppendMetricLine(builder, "package/policy/runtime/vectorBinding", $"{snapshot.ShadowQuality.InputMetadataEnrichmentPackageOutputChanged}/{snapshot.ShadowQuality.InputMetadataEnrichmentPackingPolicyChanged}/{snapshot.ShadowQuality.InputMetadataEnrichmentRuntimeMutated}/{snapshot.ShadowQuality.InputMetadataEnrichmentVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.InputMetadataEnrichmentRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.InputMetadataEnrichmentBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("V5.13 Enriched Candidate Source Repair Recheck Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.EnrichedCandidateSourceRepairRecheckSourcePath))
        {
            AppendMissingSummaryState(builder, "NoEnrichedCandidateSourceRepairRecheckReport", "run eval vector-enriched-candidate-source-repair-recheck-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.EnrichedCandidateSourceRepairRecheckSourcePath));
            AppendMetricLine(builder, "recheck/gate", $"{snapshot.ShadowQuality.EnrichedCandidateSourceRepairRecheckPassed} / {snapshot.ShadowQuality.EnrichedCandidateSourceRepairRecheckGatePassed}");
            AppendBooleanInvariantLine(builder, "quality lift", snapshot.ShadowQuality.EnrichedCandidateSourceRepairQualityImproved);
            AppendMetricLine(builder, "train recall delta", $"{snapshot.ShadowQuality.EnrichedCandidateSourceRepairTrainRecallDelta:+0.0000;-0.0000;0.0000}");
            AppendMetricLine(builder, "holdout recall delta", $"{snapshot.ShadowQuality.EnrichedCandidateSourceRepairHoldoutRecallDelta:+0.0000;-0.0000;0.0000}");
            AppendMetricLine(builder, "below-topK delta", snapshot.ShadowQuality.EnrichedCandidateSourceRepairMustHitBelowTopKDelta.ToString());
            AppendMetricLine(builder, "risk/package/policy/runtime/vectorBinding", $"{snapshot.ShadowQuality.EnrichedCandidateSourceRepairRiskAfterPolicy}/{snapshot.ShadowQuality.EnrichedCandidateSourceRepairPackageOutputChanged}/{snapshot.ShadowQuality.EnrichedCandidateSourceRepairPackingPolicyChanged}/{snapshot.ShadowQuality.EnrichedCandidateSourceRepairRuntimeMutated}/{snapshot.ShadowQuality.EnrichedCandidateSourceRepairVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.EnrichedCandidateSourceRepairRecheckRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.EnrichedCandidateSourceRepairBlockedReasons);
            AppendBlockedLine(builder, snapshot.ShadowQuality.EnrichedCandidateSourceRepairQualityBlockedReasons, "quality block");
        }

        builder.AppendLine();
        builder.AppendLine("V5.14 Source-aware Ranking Repair Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SourceAwareRankingRepairSourcePath))
        {
            AppendMissingSummaryState(builder, "NoSourceAwareRankingRepairReport", "run eval vector-source-aware-ranking-repair-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.SourceAwareRankingRepairSourcePath));
            AppendMetricLine(builder, "repair/gate", $"{snapshot.ShadowQuality.SourceAwareRankingRepairPassed} / {snapshot.ShadowQuality.SourceAwareRankingRepairGatePassed}");
            AppendMetricLine(builder, "profile", BlankDash(snapshot.ShadowQuality.SourceAwareRankingRepairSelectedProfileId));
            AppendMetricLine(builder, "recall deltas", $"trainDev={snapshot.ShadowQuality.SourceAwareRankingRepairTrainDevRecallDelta:+0.0000;-0.0000;0.0000} test={snapshot.ShadowQuality.SourceAwareRankingRepairTestRecallDelta:+0.0000;-0.0000;0.0000} holdout={snapshot.ShadowQuality.SourceAwareRankingRepairHoldoutRecallDelta:+0.0000;-0.0000;0.0000} blind={snapshot.ShadowQuality.SourceAwareRankingRepairBlindHoldoutRecallDelta:+0.0000;-0.0000;0.0000}");
            AppendMetricLine(builder, "source shape", $"denseLost={snapshot.ShadowQuality.SourceAwareRankingRepairDenseWinnerLostCount} uniqueRecovery={snapshot.ShadowQuality.SourceAwareRankingRepairUniqueSourceRecoveryCount} sourceNoise={snapshot.ShadowQuality.SourceAwareRankingRepairSourceNoiseCount} fallback={snapshot.ShadowQuality.SourceAwareRankingRepairFallbackRate:F4}");
            AppendMetricLine(builder, "risk/package/policy/runtime/vectorBinding", $"{snapshot.ShadowQuality.SourceAwareRankingRepairRiskAfterPolicy}/{snapshot.ShadowQuality.SourceAwareRankingRepairPackageOutputChanged}/{snapshot.ShadowQuality.SourceAwareRankingRepairPackingPolicyChanged}/{snapshot.ShadowQuality.SourceAwareRankingRepairRuntimeMutated}/{snapshot.ShadowQuality.SourceAwareRankingRepairVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.SourceAwareRankingRepairRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.SourceAwareRankingRepairBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("V5.15 Output Token Priority Shadow Gate Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.OutputTokenPriorityShadowSourcePath))
        {
            AppendMissingSummaryState(builder, "NoOutputTokenPriorityShadowReport", "run eval vector-output-token-priority-shadow-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.OutputTokenPriorityShadowSourcePath));
            AppendMetricLine(builder, "shadow/gate", $"{snapshot.ShadowQuality.OutputTokenPriorityShadowPassed} / {snapshot.ShadowQuality.OutputTokenPriorityShadowGatePassed}");
            AppendMetricLine(builder, "profile", BlankDash(snapshot.ShadowQuality.OutputTokenPriorityShadowProfileName));
            AppendMetricLine(builder, "token delta", $"total={snapshot.ShadowQuality.OutputTokenPriorityShadowTokenDeltaTotal} max={snapshot.ShadowQuality.OutputTokenPriorityShadowTokenDeltaMax} p95={snapshot.ShadowQuality.OutputTokenPriorityShadowTokenDeltaP95} budgetExceeded={snapshot.ShadowQuality.OutputTokenPriorityShadowTokenBudgetExceededCount}");
            AppendMetricLine(builder, "policy checks", $"priorityInversion={snapshot.ShadowQuality.OutputTokenPriorityShadowPriorityInversionCount} droppedRequired={snapshot.ShadowQuality.OutputTokenPriorityShadowDroppedRequiredCandidateCount} sectionMismatch={snapshot.ShadowQuality.OutputTokenPriorityShadowSectionMismatchCount}");
            AppendMetricLine(builder, "risk/package/policy/runtime/vectorBinding", $"{snapshot.ShadowQuality.OutputTokenPriorityShadowRiskAfterPolicy}/{snapshot.ShadowQuality.OutputTokenPriorityShadowPackageOutputChanged}/{snapshot.ShadowQuality.OutputTokenPriorityShadowPackingPolicyChanged}/{snapshot.ShadowQuality.OutputTokenPriorityShadowRuntimeMutated}/{snapshot.ShadowQuality.OutputTokenPriorityShadowVectorStoreBindingChanged}");
            AppendBooleanInvariantLine(builder, "formal selected set changed", snapshot.ShadowQuality.OutputTokenPriorityShadowFormalSelectedSetChanged);
            AppendRecommendationLine(builder, snapshot.ShadowQuality.OutputTokenPriorityShadowRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.OutputTokenPriorityShadowBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("Formal Adapter Input Contract Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.FormalAdapterInputContractSourcePath))
        {
            AppendMissingSummaryState(builder, "NoFormalAdapterInputContractReport", "run eval vector-formal-adapter-input-contract-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.FormalAdapterInputContractSourcePath));
            AppendMetricLine(builder, "contract/gate", $"{snapshot.ShadowQuality.FormalAdapterInputContractPassed} / {snapshot.ShadowQuality.FormalAdapterInputContractGatePassed}");
            AppendMetricLine(builder, "version", BlankDash(snapshot.ShadowQuality.FormalAdapterInputContractVersion));
            AppendMetricLine(builder, "fields", $"runtime={snapshot.ShadowQuality.FormalAdapterInputContractRuntimeInputFieldCount} denied={snapshot.ShadowQuality.FormalAdapterInputContractDeniedFieldCount} forbiddenProps={snapshot.ShadowQuality.FormalAdapterInputContractForbiddenPropertyCount}");
            AppendMetricLine(builder, "forbidden read", $"formal={snapshot.ShadowQuality.FormalAdapterInputContractFormalSourceForbiddenReadCount} evalOnly={snapshot.ShadowQuality.FormalAdapterInputContractEvalOnlyForbiddenReadCount}");
            AppendMetricLine(builder, "blocked cats", $"dataset={snapshot.ShadowQuality.FormalAdapterInputContractDatasetEvalFieldsBlocked} gold={snapshot.ShadowQuality.FormalAdapterInputContractGoldLabelsBlocked} sampleMeta={snapshot.ShadowQuality.FormalAdapterInputContractSampleMetadataBlocked} shadow={snapshot.ShadowQuality.FormalAdapterInputContractShadowArtifactFieldsBlocked}");
            AppendMetricLine(builder, "invariants", $"formalRetrieval={snapshot.ShadowQuality.FormalAdapterInputContractFormalRetrievalAllowed} runtimeSwitch={snapshot.ShadowQuality.FormalAdapterInputContractRuntimeSwitchAllowed} packageOutputChanged={snapshot.ShadowQuality.FormalAdapterInputContractPackageOutputChanged} packingPolicyChanged={snapshot.ShadowQuality.FormalAdapterInputContractPackingPolicyChanged} runtimeMutated={snapshot.ShadowQuality.FormalAdapterInputContractRuntimeMutated} vectorBinding={snapshot.ShadowQuality.FormalAdapterInputContractVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.FormalAdapterInputContractRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.FormalAdapterInputContractBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("V6.6 Source-diverse Shadow Adapter Validation Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationSourcePath))
        {
            AppendMissingSummaryState(builder, "NoSourceDiverseShadowAdapterValidationReport", "run eval vector-source-diverse-shadow-adapter-validation-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationSourcePath));
            AppendMetricLine(builder, "validation/gate", $"{snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationPassed} / {snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationGatePassed}");
            AppendMetricLine(builder, "set/scope", $"sourceDiverse={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationSetSourceDiverse} scopeMetadata={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationScopeMetadataPresent} samples={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationSampleCount}");
            AppendMetricLine(builder, "delta", $"overlap={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationOverlapRate:P2} shadowOnly={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationShadowOnlyCount} hypoAdd={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationHypotheticalAddCount} hypoRemove={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationHypotheticalRemoveCount} applied={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationAppliedAddCount}/{snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationAppliedRemoveCount}");
            AppendMetricLine(builder, "recovery/risk", $"uniqueRecovery={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationUniqueSourceRecoveryCount} risk={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationRiskAfterPolicy}");
            AppendMetricLine(builder, "token/section", $"total={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationTokenDeltaTotal} max={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationTokenDeltaMax} sectionDelta={snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationSectionDeltaCount}");
            AppendMetricLine(builder, "package/policy/runtime/vectorBinding", $"{snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationPackageOutputChanged}/{snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationPackingPolicyChanged}/{snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationRuntimeMutated}/{snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.SourceDiverseShadowAdapterValidationBlockedReasons);
        }
        builder.AppendLine();
        builder.AppendLine("V6.7 Shadow Candidate Merge Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ShadowCandidateMergePreviewSourcePath))
        {
            AppendMissingSummaryState(builder, "NoShadowCandidateMergePreviewReport", "run eval vector-shadow-candidate-merge-preview-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ShadowCandidateMergePreviewSourcePath));
            AppendMetricLine(builder, "preview/gate", $"{snapshot.ShadowQuality.ShadowCandidateMergePreviewPassed} / {snapshot.ShadowQuality.ShadowCandidateMergePreviewGatePassed}");
            AppendMetricLine(builder, "candidates", $"baseline={snapshot.ShadowQuality.ShadowCandidateMergePreviewBaselineCandidateCount} shadow={snapshot.ShadowQuality.ShadowCandidateMergePreviewShadowAdapterCandidateCount} merged={snapshot.ShadowQuality.ShadowCandidateMergePreviewMergedPreviewCandidateCount} samples={snapshot.ShadowQuality.ShadowCandidateMergePreviewSampleCount}");
            AppendMetricLine(builder, "delta", $"previewAdd={snapshot.ShadowQuality.ShadowCandidateMergePreviewPreviewAddCount} previewRemove={snapshot.ShadowQuality.ShadowCandidateMergePreviewPreviewRemoveCount} applied={snapshot.ShadowQuality.ShadowCandidateMergePreviewAppliedAddCount}/{snapshot.ShadowQuality.ShadowCandidateMergePreviewAppliedRemoveCount}");
            AppendMetricLine(builder, "token/order", $"total={snapshot.ShadowQuality.ShadowCandidateMergePreviewTokenDeltaTotal} max={snapshot.ShadowQuality.ShadowCandidateMergePreviewTokenDeltaMax} orderDelta={snapshot.ShadowQuality.ShadowCandidateMergePreviewPriorityOrderDeltaCount} inversion={snapshot.ShadowQuality.ShadowCandidateMergePreviewPriorityInversionCount}");
            AppendMetricLine(builder, "section/risk", $"sectionMismatch={snapshot.ShadowQuality.ShadowCandidateMergePreviewSectionMismatchCount} droppedRequired={snapshot.ShadowQuality.ShadowCandidateMergePreviewDroppedRequiredCandidateCount} risk={snapshot.ShadowQuality.ShadowCandidateMergePreviewRiskAfterPolicy}");
            AppendMetricLine(builder, "formal/package/policy/runtime/vectorBinding", $"{snapshot.ShadowQuality.ShadowCandidateMergePreviewFormalSelectedSetChanged}/{snapshot.ShadowQuality.ShadowCandidateMergePreviewPackageOutputChanged}/{snapshot.ShadowQuality.ShadowCandidateMergePreviewPackingPolicyChanged}/{snapshot.ShadowQuality.ShadowCandidateMergePreviewRuntimeMutated}/{snapshot.ShadowQuality.ShadowCandidateMergePreviewVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ShadowCandidateMergePreviewRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ShadowCandidateMergePreviewBlockedReasons);
        }
        builder.AppendLine();
        builder.AppendLine("V6.7 Shadow Candidate Merge Observation Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationSourcePath))
        {
            AppendMissingSummaryState(builder, "NoShadowCandidateMergePreviewObservationReport", "run eval vector-shadow-candidate-merge-preview-observation-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationSourcePath));
            AppendMetricLine(builder, "observation/gate", $"{snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPassed} / {snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationGatePassed}");
            AppendMetricLine(builder, "runs/samples", $"runs={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationRunCount} sampleObs={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationSampleCount} stable={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationDeterministicStable} addRemoveStable={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPreviewAddRemoveStable}");
            AppendMetricLine(builder, "add/remove", $"add={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPreviewAddCountMin}-{snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPreviewAddCountMax} remove={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPreviewRemoveCountMin}-{snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPreviewRemoveCountMax} appliedMax={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationAppliedAddCountMax}/{snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationAppliedRemoveCountMax}");
            AppendMetricLine(builder, "token/order", $"totalMax={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationTokenDeltaTotalMax} maxMax={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationTokenDeltaMaxMax} inversion={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPriorityInversionCountTotal}");
            AppendMetricLine(builder, "section/risk", $"sectionMismatch={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationSectionMismatchCountTotal} riskMax={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationRiskAfterPolicyMax}");
            AppendMetricLine(builder, "formal/package/policy/runtime/vectorBinding", $"outputChangedMax={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationFormalOutputChangedMax} package={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPackageOutputChanged} policy={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationPackingPolicyChanged} runtime={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationRuntimeMutated} vectorBinding={snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ShadowCandidateMergePreviewObservationBlockedReasons);
        }
        builder.AppendLine();
        builder.AppendLine("V6.7 Shadow Merge Stability Freeze / Promotion Decision");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ShadowMergeStabilityFreezeSourcePath) &&
            string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ShadowMergePromotionDecisionSourcePath))
        {
            AppendMissingSummaryState(builder, "NoShadowMergeStabilityFreezeReport", "run eval vector-shadow-merge-stability-freeze and eval vector-shadow-merge-promotion-decision");
        }
        else
        {
            AppendMetricLine(builder, "freezeSource", BlankDash(snapshot.ShadowQuality.ShadowMergeStabilityFreezeSourcePath));
            AppendMetricLine(builder, "decisionSource", BlankDash(snapshot.ShadowQuality.ShadowMergePromotionDecisionSourcePath));
            AppendMetricLine(builder, "freeze/promo", $"{snapshot.ShadowQuality.ShadowMergeStabilityFreezePassed} / {snapshot.ShadowQuality.ShadowMergePromotionDecisionPassed}");
            AppendMetricLine(builder, "decision", $"{BlankDash(snapshot.ShadowQuality.ShadowMergePromotionDecision)} next={BlankDash(snapshot.ShadowQuality.ShadowMergeNextAllowedPhase)}");
            AppendMetricLine(builder, "runs/samples", $"runs={snapshot.ShadowQuality.ShadowMergeObservationRunCount} sampleObs={snapshot.ShadowQuality.ShadowMergeSampleObservationCount} stable={snapshot.ShadowQuality.ShadowMergeDeterministicPreviewStable}");
            AppendMetricLine(builder, "add/remove", $"add={snapshot.ShadowQuality.ShadowMergePreviewAddCountMin}-{snapshot.ShadowQuality.ShadowMergePreviewAddCountMax} remove={snapshot.ShadowQuality.ShadowMergePreviewRemoveCountMin}-{snapshot.ShadowQuality.ShadowMergePreviewRemoveCountMax} appliedMax={snapshot.ShadowQuality.ShadowMergeAppliedAddCountMax}/{snapshot.ShadowQuality.ShadowMergeAppliedRemoveCountMax}");
            AppendMetricLine(builder, "token/order", $"totalMax={snapshot.ShadowQuality.ShadowMergeTokenDeltaTotalMax} inversion={snapshot.ShadowQuality.ShadowMergePriorityInversionCountTotal}");
            AppendMetricLine(builder, "section/risk", $"sectionMismatch={snapshot.ShadowQuality.ShadowMergeSectionMismatchCountTotal} riskMax={snapshot.ShadowQuality.ShadowMergeRiskAfterPolicyMax}");
            AppendMetricLine(builder, "formal/package/policy/runtime/vectorBinding", $"outputChangedMax={snapshot.ShadowQuality.ShadowMergeFormalOutputChangedMax} package={snapshot.ShadowQuality.ShadowMergePackageOutputChanged} policy={snapshot.ShadowQuality.ShadowMergePackingPolicyChanged} runtime={snapshot.ShadowQuality.ShadowMergeRuntimeMutated} vectorBinding={snapshot.ShadowQuality.ShadowMergeVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ShadowMergeStabilityFreezeRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ShadowMergeBlockedReasons);
        }
        builder.AppendLine();
        builder.AppendLine("V6.8 Controlled Shadow Merge Proposal");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ControlledShadowMergeProposalSourcePath))
        {
            AppendMissingSummaryState(builder, "NoControlledShadowMergeProposalReport", "run eval vector-controlled-shadow-merge-proposal and eval vector-controlled-shadow-merge-proposal-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ControlledShadowMergeProposalSourcePath));
            AppendMetricLine(builder, "proposal/gate", $"{snapshot.ShadowQuality.ControlledShadowMergeProposalPassed} / {snapshot.ShadowQuality.ControlledShadowMergeProposalGatePassed}");
            AppendMetricLine(builder, "proposalId", BlankDash(snapshot.ShadowQuality.ControlledShadowMergeProposalId));
            AppendMetricLine(builder, "scopes", $"count={snapshot.ShadowQuality.ControlledShadowMergeProposalScopeCount} selected={(snapshot.ShadowQuality.ControlledShadowMergeProposalSelectedScopes.Count == 0 ? "-" : string.Join("; ", snapshot.ShadowQuality.ControlledShadowMergeProposalSelectedScopes))}");
            AppendMetricLine(builder, "limits", $"requests={snapshot.ShadowQuality.ControlledShadowMergeProposalMaxRequestCount} durationMin={snapshot.ShadowQuality.ControlledShadowMergeProposalMaxDurationMinutes} add/remove={snapshot.ShadowQuality.ControlledShadowMergeProposalMaxPreviewAddCount}/{snapshot.ShadowQuality.ControlledShadowMergeProposalMaxPreviewRemoveCount}");
            AppendMetricLine(builder, "guardrails", $"rollback={snapshot.ShadowQuality.ControlledShadowMergeProposalRollbackPlanPresent} killSwitch={snapshot.ShadowQuality.ControlledShadowMergeProposalKillSwitchPlanPresent} observation={snapshot.ShadowQuality.ControlledShadowMergeProposalObservationConditionCount} stop={snapshot.ShadowQuality.ControlledShadowMergeProposalStopConditionCount}");
            AppendMetricLine(builder, "runtime gates", $"formalRetrieval={snapshot.ShadowQuality.ControlledShadowMergeProposalFormalRetrievalAllowed} runtimeSwitch={snapshot.ShadowQuality.ControlledShadowMergeProposalRuntimeSwitchAllowed} runtimeMutated={snapshot.ShadowQuality.ControlledShadowMergeProposalRuntimeMutated}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ControlledShadowMergeProposalRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ControlledShadowMergeProposalBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("V6.10 Controlled Shadow Merge Dry-run Gate");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ControlledShadowMergeDryRunSourcePath))
        {
            AppendMissingSummaryState(builder, "NoControlledShadowMergeDryRunReport", "run eval vector-controlled-shadow-merge-dry-run and eval vector-controlled-shadow-merge-dry-run-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ControlledShadowMergeDryRunSourcePath));
            AppendMetricLine(builder, "dryRun/gate", $"{snapshot.ShadowQuality.ControlledShadowMergeDryRunPassed} / {snapshot.ShadowQuality.ControlledShadowMergeDryRunGatePassed}");
            AppendMetricLine(builder, "constraints", $"proposal={snapshot.ShadowQuality.ControlledShadowMergeDryRunProposalConstraintsApplied} addRemove={snapshot.ShadowQuality.ControlledShadowMergeDryRunAddRemoveLimitEnforced} tokenSectionPriority={snapshot.ShadowQuality.ControlledShadowMergeDryRunTokenSectionPriorityGatePassed}");
            AppendMetricLine(builder, "rollback/kill", $"rollback={snapshot.ShadowQuality.ControlledShadowMergeDryRunRollbackVerified} killSwitch={snapshot.ShadowQuality.ControlledShadowMergeDryRunKillSwitchVerified}");
            AppendMetricLine(builder, "add/remove", $"preview={snapshot.ShadowQuality.ControlledShadowMergeDryRunPreviewAddCount}/{snapshot.ShadowQuality.ControlledShadowMergeDryRunPreviewRemoveCount} applied={snapshot.ShadowQuality.ControlledShadowMergeDryRunAppliedAddCount}/{snapshot.ShadowQuality.ControlledShadowMergeDryRunAppliedRemoveCount}");
            AppendMetricLine(builder, "token/order", $"total={snapshot.ShadowQuality.ControlledShadowMergeDryRunTokenDeltaTotal} max={snapshot.ShadowQuality.ControlledShadowMergeDryRunTokenDeltaMax} inversion={snapshot.ShadowQuality.ControlledShadowMergeDryRunPriorityInversionCount}");
            AppendMetricLine(builder, "section/formal", $"sectionMismatch={snapshot.ShadowQuality.ControlledShadowMergeDryRunSectionMismatchCount} formalChanged={snapshot.ShadowQuality.ControlledShadowMergeDryRunFormalOutputChanged}");
            AppendMetricLine(builder, "package/policy/runtime/vectorBinding", $"package={snapshot.ShadowQuality.ControlledShadowMergeDryRunPackageOutputChanged} policy={snapshot.ShadowQuality.ControlledShadowMergeDryRunPackingPolicyChanged} runtime={snapshot.ShadowQuality.ControlledShadowMergeDryRunRuntimeMutated} vectorBinding={snapshot.ShadowQuality.ControlledShadowMergeDryRunVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ControlledShadowMergeDryRunRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ControlledShadowMergeDryRunBlockedReasons);
        }

        builder.AppendLine();
        builder.AppendLine("V6.11 Controlled Shadow Merge Observation Window");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ControlledShadowMergeObservationWindowSourcePath))
        {
            AppendMissingSummaryState(builder, "NoControlledShadowMergeObservationWindowReport", "run eval vector-controlled-shadow-merge-observation-window and eval vector-controlled-shadow-merge-observation-window-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ControlledShadowMergeObservationWindowSourcePath));
            AppendMetricLine(builder, "observation/gate", $"{snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPassed} / {snapshot.ShadowQuality.ControlledShadowMergeObservationWindowGatePassed}");
            AppendMetricLine(builder, "constraints", $"proposal={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowProposalConstraintsApplied} runs={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowRunCount} requests={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowRequestCountTotal}/{snapshot.ShadowQuality.ControlledShadowMergeObservationWindowMaxRequestCount}");
            AppendMetricLine(builder, "add/remove", $"previewMinMax={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPreviewAddCountMin}-{snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPreviewAddCountMax}/{snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPreviewRemoveCountMin}-{snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPreviewRemoveCountMax} appliedMax={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowAppliedAddCountMax}/{snapshot.ShadowQuality.ControlledShadowMergeObservationWindowAppliedRemoveCountMax}");
            AppendMetricLine(builder, "risk/token", $"riskMax={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowRiskAfterPolicyMax} tokenTotalMax={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowTokenDeltaTotalMax} tokenMax={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowTokenDeltaMaxMax}");
            AppendMetricLine(builder, "order/section", $"inversionTotal={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPriorityInversionCountTotal} sectionMismatchTotal={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowSectionMismatchCountTotal}");
            AppendMetricLine(builder, "formal/runtime", $"formalChangedMax={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowFormalOutputChangedMax} package={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPackageOutputChanged} policy={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowPackingPolicyChanged} runtime={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowRuntimeMutated} vectorBinding={snapshot.ShadowQuality.ControlledShadowMergeObservationWindowVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ControlledShadowMergeObservationWindowRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ControlledShadowMergeObservationWindowBlockedReasons);
        }
        builder.AppendLine();
        builder.AppendLine("V6.13 Controlled Shadow Merge Freeze / Promotion Decision");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ControlledShadowMergeFreezeSourcePath))
        {
            AppendMissingSummaryState(builder, "NoControlledShadowMergeFreezeReport", "run eval vector-controlled-shadow-merge-freeze and eval vector-controlled-shadow-merge-promotion-decision");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ControlledShadowMergeFreezeSourcePath));
            AppendMetricLine(builder, "freeze/promo", $"{snapshot.ShadowQuality.ControlledShadowMergeFreezePassed} / {snapshot.ShadowQuality.ControlledShadowMergePromotionDecisionPassed}");
            AppendMetricLine(builder, "proposal", BlankDash(snapshot.ShadowQuality.ControlledShadowMergeFreezeProposalId));
            AppendMetricLine(builder, "decision", $"{BlankDash(snapshot.ShadowQuality.ControlledShadowMergePromotionDecision)} next={BlankDash(snapshot.ShadowQuality.ControlledShadowMergeNextAllowedPhase)}");
            AppendMetricLine(builder, "runs/requests", $"runs={snapshot.ShadowQuality.ControlledShadowMergeFreezeObservationRunCount} requests={snapshot.ShadowQuality.ControlledShadowMergeFreezeRequestCountTotal}");
            AppendMetricLine(builder, "add/remove", $"preview={snapshot.ShadowQuality.ControlledShadowMergeFreezePreviewAddCountMin}-{snapshot.ShadowQuality.ControlledShadowMergeFreezePreviewAddCountMax}/{snapshot.ShadowQuality.ControlledShadowMergeFreezePreviewRemoveCountMin}-{snapshot.ShadowQuality.ControlledShadowMergeFreezePreviewRemoveCountMax} appliedMax={snapshot.ShadowQuality.ControlledShadowMergeFreezeAppliedAddCountMax}/{snapshot.ShadowQuality.ControlledShadowMergeFreezeAppliedRemoveCountMax}");
            AppendMetricLine(builder, "risk/formal", $"riskMax={snapshot.ShadowQuality.ControlledShadowMergeFreezeRiskAfterPolicyMax} formalChangedMax={snapshot.ShadowQuality.ControlledShadowMergeFreezeFormalOutputChangedMax} formalPackage={snapshot.ShadowQuality.ControlledShadowMergeFreezeFormalPackageWritten}");
            AppendMetricLine(builder, "package/policy/runtime/vectorBinding", $"package={snapshot.ShadowQuality.ControlledShadowMergeFreezePackageOutputChanged} policy={snapshot.ShadowQuality.ControlledShadowMergeFreezePackingPolicyChanged} runtime={snapshot.ShadowQuality.ControlledShadowMergeFreezeRuntimeMutated} vectorBinding={snapshot.ShadowQuality.ControlledShadowMergeFreezeVectorStoreBindingChanged}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ControlledShadowMergeFreezeRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ControlledShadowMergeFreezeBlockedReasons);
        }
        builder.AppendLine();
        builder.AppendLine("V6.14 Controlled Applied Merge Proposal");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ControlledAppliedMergeProposalSourcePath))
        {
            AppendMissingSummaryState(builder, "NoControlledAppliedMergeProposalReport", "run eval vector-controlled-applied-merge-proposal and eval vector-controlled-applied-merge-proposal-gate");
        }
        else
        {
            AppendMetricLine(builder, "source", BlankDash(snapshot.ShadowQuality.ControlledAppliedMergeProposalSourcePath));
            AppendMetricLine(builder, "proposal/gate", $"{snapshot.ShadowQuality.ControlledAppliedMergeProposalPassed} / {snapshot.ShadowQuality.ControlledAppliedMergeProposalGatePassed}");
            AppendMetricLine(builder, "proposal", $"{BlankDash(snapshot.ShadowQuality.ControlledAppliedMergeProposalId)} approval={BlankDash(snapshot.ShadowQuality.ControlledAppliedMergeProposalApprovalMode)} next={BlankDash(snapshot.ShadowQuality.ControlledAppliedMergeProposalNextAllowedPhase)}");
            AppendMetricLine(builder, "scopes", $"count={snapshot.ShadowQuality.ControlledAppliedMergeProposalScopeCount} selected={(snapshot.ShadowQuality.ControlledAppliedMergeProposalSelectedScopes.Count == 0 ? "-" : string.Join(", ", snapshot.ShadowQuality.ControlledAppliedMergeProposalSelectedScopes.Take(3)))}");
            AppendMetricLine(builder, "add/remove", $"stablePreview={snapshot.ShadowQuality.ControlledAppliedMergeProposalStablePreviewAddCount}/{snapshot.ShadowQuality.ControlledAppliedMergeProposalStablePreviewRemoveCount} maxApplied={snapshot.ShadowQuality.ControlledAppliedMergeProposalMaxAppliedAddCount}/{snapshot.ShadowQuality.ControlledAppliedMergeProposalMaxAppliedRemoveCount} applied={snapshot.ShadowQuality.ControlledAppliedMergeProposalAppliedAddCount}/{snapshot.ShadowQuality.ControlledAppliedMergeProposalAppliedRemoveCount}");
            AppendMetricLine(builder, "approval/rollback/killSwitch", $"{snapshot.ShadowQuality.ControlledAppliedMergeProposalApprovalPlanPresent} / {snapshot.ShadowQuality.ControlledAppliedMergeProposalRollbackPlanPresent} / {snapshot.ShadowQuality.ControlledAppliedMergeProposalKillSwitchPlanPresent}");
            AppendMetricLine(builder, "risk/formal", $"risk={snapshot.ShadowQuality.ControlledAppliedMergeProposalRiskAfterPolicy} formalChanged={snapshot.ShadowQuality.ControlledAppliedMergeProposalFormalOutputChanged} formalPackage={snapshot.ShadowQuality.ControlledAppliedMergeProposalFormalPackageWritten}");
            AppendMetricLine(builder, "package/policy/runtime/vectorBinding/appliedAllowed", $"package={snapshot.ShadowQuality.ControlledAppliedMergeProposalPackageOutputChanged} policy={snapshot.ShadowQuality.ControlledAppliedMergeProposalPackingPolicyChanged} runtime={snapshot.ShadowQuality.ControlledAppliedMergeProposalRuntimeMutated} vectorBinding={snapshot.ShadowQuality.ControlledAppliedMergeProposalVectorStoreBindingChanged} appliedAllowed={snapshot.ShadowQuality.ControlledAppliedMergeProposalAppliedMergeAllowed}");
            AppendRecommendationLine(builder, snapshot.ShadowQuality.ControlledAppliedMergeProposalRecommendation);
            AppendBlockedLine(builder, snapshot.ShadowQuality.ControlledAppliedMergeProposalBlockedReasons);
        }
        if (status.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in status.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- total          : {diagnostics.Diagnostics.Count}");
        builder.AppendLine($"- dimensionMismatch: {diagnostics.DimensionMismatchCount}");
        builder.AppendLine($"- unsupportedModel : {diagnostics.UnsupportedModelCount}");
        builder.AppendLine($"- providerUnavailable: {diagnostics.ProviderUnavailableCount}");
        if (diagnostics.CountsByType.Count > 0)
        {
            foreach (var pair in diagnostics.CountsByType.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Diagnostics");
        if (diagnostics.Diagnostics.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in diagnostics.Diagnostics.Take(20))
            {
                builder.AppendLine($"- {item.Type} [{item.Severity}] item={item.ItemId} entry={item.EntryId ?? "-"}");
                builder.AppendLine($"  message : {item.Message}");
                builder.AppendLine($"  action  : {item.SuggestedAction}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Reindex Preview");
        builder.AppendLine($"- sources : {preview.SourceItemCount}");
        builder.AppendLine($"- create  : {preview.WouldCreateCount}");
        builder.AppendLine($"- update  : {preview.WouldUpdateCount}");
        builder.AppendLine($"- current : {preview.AlreadyCurrentCount}");
        builder.AppendLine($"- orphan  : {preview.WouldDeleteOrphanCount}");
        builder.AppendLine();
        builder.AppendLine("Actions");
        builder.AppendLine("- P Reindex Plan");
        builder.AppendLine("- A Apply Reindex (requires YES)");
        builder.AppendLine("- R Reindex Reports");
        builder.AppendLine("- Q Query Preview");
        builder.AppendLine("- D Diagnostics");
        if (preview.Warnings.Count > 0)
        {
            foreach (var warning in preview.Warnings)
            {
                builder.AppendLine($"- warning : {warning}");
            }
        }

        foreach (var item in preview.Items.Take(20))
        {
            builder.AppendLine($"- {item.Action,-12} {item.ItemId} kind={item.ItemKind} layer={item.Layer}");
            builder.AppendLine($"  reason : {item.Reason}");
        }

        return builder.ToString();
    }

    public static string RenderVectorQueryPreview(VectorQueryPreviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Query Preview");
        builder.AppendLine("====================");
        builder.AppendLine($"Operation  : {result.OperationId}");
        builder.AppendLine($"Workspace  : {result.WorkspaceId}");
        builder.AppendLine($"Collection : {result.CollectionId}");
        builder.AppendLine($"Query      : {result.QueryText}");
        builder.AppendLine($"TopK       : {result.TopK}");
        builder.AppendLine($"Profile    : {result.ProfileId}");
        builder.AppendLine($"Layer      : {result.Layer ?? "-"}");
        builder.AppendLine($"ItemKind   : {result.ItemKind ?? "-"}");
        builder.AppendLine($"MinSim     : {result.MinSimilarity?.ToString("F3") ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- indexed={result.Diagnostics.IndexedCount} duplicate={result.Diagnostics.DuplicateCount} stale={result.Diagnostics.StaleCount} orphan={result.Diagnostics.OrphanCount}");
        builder.AppendLine($"- store={result.Diagnostics.StoreAvailable} generator={result.Diagnostics.GeneratorAvailable} indexEmpty={result.Diagnostics.IndexEmpty}");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Candidates");
        if (result.Candidates.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return builder.ToString();
        }

        foreach (var candidate in result.Candidates.Take(30))
        {
            var flags = new List<string>();
            if (candidate.IsDuplicate) flags.Add("duplicate");
            if (candidate.IsStale) flags.Add("stale");
            if (candidate.IsOrphan) flags.Add("orphan");
            if (candidate.IsLifecycleRisk) flags.Add("lifecycle-risk");
            builder.AppendLine($"- #{candidate.Rank} raw=#{candidate.RawRank} {candidate.ItemId} sim={candidate.Similarity:F4} status={candidate.EligibilityStatus} target={candidate.TargetSection}");
            builder.AppendLine($"  kind={candidate.ItemKind} layer={candidate.Layer} riskBefore={candidate.RiskIfNormalSelected} riskAfter={candidate.RiskAfterPolicy}");
            builder.AppendLine($"  entry={candidate.EntryId} model={candidate.EmbeddingModel} provider={candidate.EmbeddingProvider}");
            if (flags.Count > 0)
            {
                builder.AppendLine($"  flags={string.Join(",", flags)}");
            }

            if (candidate.BlockedReasons.Count > 0)
            {
                builder.AppendLine($"  blocked={string.Join(",", candidate.BlockedReasons)}");
            }

            if (candidate.Diagnostics.Count > 0)
            {
                builder.AppendLine($"  diagnostics={string.Join(",", candidate.Diagnostics)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorReindexPlan(VectorReindexPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Plan");
        builder.AppendLine("===================");
        builder.AppendLine($"PlanId     : {plan.PlanId}");
        builder.AppendLine($"Workspace  : {plan.WorkspaceId}");
        builder.AppendLine($"Collection : {plan.CollectionId}");
        builder.AppendLine($"DryRun     : {plan.DryRun}");
        builder.AppendLine($"Candidates : total={plan.TotalCandidates} create={plan.ToCreate} update={plan.ToUpdate} skip={plan.ToSkip} orphan={plan.ToDeleteOrphan}");
        builder.AppendLine($"Signals    : stale={plan.StaleItems.Count} missing={plan.MissingItems.Count} duplicate={plan.DuplicateItems.Count} orphan={plan.OrphanItems.Count} estimatedEmbedding={plan.EstimatedEmbeddingCount}");

        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in plan.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Plan Items");
        if (plan.Items.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in plan.Items.Take(30))
            {
                builder.AppendLine($"- {item.Action,-12} {item.ItemId} kind={item.ItemKind} layer={item.Layer}");
                builder.AppendLine($"  reason : {item.Reason}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorReindexSubmit(VectorReindexSubmitResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Submit");
        builder.AppendLine("=====================");
        builder.AppendLine($"JobId      : {response.Job.JobId}");
        builder.AppendLine($"State      : {response.Job.State}");
        builder.AppendLine($"Kind       : {response.Job.Kind}");
        builder.AppendLine($"Workspace  : {response.Job.WorkspaceId}");
        builder.AppendLine($"Collection : {response.Job.CollectionId}");
        builder.AppendLine();
        builder.AppendLine($"Plan       : create={response.Plan.ToCreate} update={response.Plan.ToUpdate} skip={response.Plan.ToSkip} orphan={response.Plan.ToDeleteOrphan} duplicate={response.Plan.DuplicateItems.Count}");
        builder.AppendLine("Apply 已提交为后台 job；正式 retrieval/package 输出不会被 vector reindex 修改。");
        return builder.ToString();
    }

    public static string RenderVectorReindexReports(VectorReindexReportQueryResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Reports");
        builder.AppendLine("======================");
        builder.AppendLine($"Count: {response.Count}");
        if (response.Reports.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return builder.ToString();
        }

        foreach (var report in response.Reports.Take(20))
        {
            builder.AppendLine($"- {report.ReportId} op={report.OperationId} job={report.JobId ?? "-"} dryRun={report.Summary.DryRun} applied={report.Summary.Applied}");
            builder.AppendLine($"  summary: create={report.Summary.Created} update={report.Summary.Updated} skip={report.Summary.Skipped} failed={report.Summary.Failed} duplicate={report.Summary.Duplicate} orphan={report.Summary.Orphan}");
        }

        return builder.ToString();
    }

    public static string RenderPlanningSnapshot(ServicePlanningSnapshot snapshot)
    {
        var planning = snapshot.Snapshot;
        var builder = new StringBuilder();
        builder.AppendLine("Service Planning Snapshot");
        builder.AppendLine("=========================");
        builder.AppendLine($"时间      : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务      : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace : {planning.WorkspaceId}");
        builder.AppendLine($"Collection: {planning.CollectionId ?? "-"}");
        builder.AppendLine($"Session   : {planning.SessionId ?? "-"}");
        builder.AppendLine($"Policy    : {planning.PolicyVersion}");
        builder.AppendLine($"CreatedAt : {planning.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine($"Counts    : tasks={planning.ActiveTasks.Count} decisions={planning.RecentDecisions.Count} questions={planning.OpenQuestions.Count} issues={planning.KnownIssues.Count} constraints={planning.StableConstraints.Count} preferences={planning.StablePreferences.Count} decisionRecords={planning.DecisionRecords.Count}");

        builder.AppendLine();
        AppendWorkingItems(builder, "Active Tasks", planning.ActiveTasks);
        AppendWorkingItems(builder, "Recent Decisions", planning.RecentDecisions);
        AppendWorkingItems(builder, "Open Questions", planning.OpenQuestions);
        AppendWorkingItems(builder, "Known Issues", planning.KnownIssues);
        AppendConstraints(builder, "Stable Constraints", planning.StableConstraints);
        AppendMemoryItems(builder, "Stable Preferences", planning.StablePreferences);
        AppendMemoryItems(builder, "Decision Records", planning.DecisionRecords);

        builder.AppendLine();
        builder.AppendLine("Learning Signals Summary");
        builder.AppendLine("------------------------");
        builder.AppendLine($"records={planning.LearningSignalsSummary.RecordCount} cases={planning.LearningSignalsSummary.CaseCount} positive={planning.LearningSignalsSummary.PositiveCount} negative={planning.LearningSignalsSummary.NegativeCount} stale={planning.LearningSignalsSummary.StaleCount}");
        builder.AppendLine($"caseStatus draft={planning.LearningSignalsSummary.DraftCaseCount} candidate={planning.LearningSignalsSummary.CandidateCaseCount} activeRegression={planning.LearningSignalsSummary.ActiveRegressionCaseCount} archived={planning.LearningSignalsSummary.ArchivedCaseCount} rejected={planning.LearningSignalsSummary.RejectedCaseCount}");
        if (planning.LearningSignalsSummary.FailureTypeCounts.Count > 0)
        {
            builder.AppendLine("failureTypes");
            foreach (var pair in planning.LearningSignalsSummary.FailureTypeCounts.OrderBy(pair => pair.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPlanningProposal(ServicePlanningProposalSnapshot snapshot)
    {
        var proposal = snapshot.Proposal;
        var builder = new StringBuilder();
        builder.AppendLine("Service Planning Proposal");
        builder.AppendLine("=========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Input      : {Compact(snapshot.CurrentInput, 180)}");
        builder.AppendLine($"Operation  : {proposal.OperationId}");
        builder.AppendLine($"Workspace  : {proposal.WorkspaceId}");
        builder.AppendLine($"Collection : {proposal.CollectionId ?? "-"}");
        builder.AppendLine($"Intent     : {proposal.Intent}");
        builder.AppendLine($"Mode       : {proposal.Mode}");
        builder.AppendLine($"Confidence : {proposal.Confidence:0.00}");
        builder.AppendLine($"AuditMode  : {proposal.AuditMode}");
        builder.AppendLine($"Conflict   : {proposal.ConflictMode}");
        builder.AppendLine();
        builder.AppendLine("Channels");
        builder.AppendLine("--------");
        builder.AppendLine($"Exact={proposal.UseExact} Keyword={proposal.UseKeyword} ShortTerm={proposal.UseShortTermMemory} Working={proposal.UseWorkingMemory} Stable={proposal.UseStableMemory} Relations={proposal.UseRelations} Vector={proposal.UseVector}");
        builder.AppendLine();
        builder.AppendLine("TopK");
        builder.AppendLine("----");
        builder.AppendLine($"Keyword={proposal.KeywordTopK} Memory={proposal.MemoryTopK} Relation={proposal.RelationTopK} Vector={proposal.VectorTopK} Final={proposal.FinalTopK}");
        AppendStringList(builder, "Reasons", proposal.Reasons);
        AppendStringList(builder, "Warnings", proposal.Warnings);

        return builder.ToString();
    }

    public static string RenderRankerShadowDebug(ServiceRankerShadowDebugSnapshot snapshot)
    {
        var response = snapshot.Response;
        var builder = new StringBuilder();
        builder.AppendLine("Service Ranker Shadow Debug");
        builder.AppendLine("===========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Query      : {Compact(response.Query, 180)}");
        builder.AppendLine($"Operation  : {response.OperationId}");
        builder.AppendLine($"Retrieval  : {response.RetrievalOperationId}");
        builder.AppendLine($"Workspace  : {response.WorkspaceId}");
        builder.AppendLine($"Collection : {response.CollectionId}");
        builder.AppendLine($"Mode       : {response.Mode}");
        builder.AppendLine($"Profile    : {response.RankerShadowProfile}");
        builder.AppendLine($"DebugOnly  : {response.Metadata.GetValueOrDefault("debugOnly", "true")}");
        builder.AppendLine($"FormalChanged : {response.FormalOutputChanged}");
        builder.AppendLine($"SelectedChanged: {response.SelectedSetChanged}");
        builder.AppendLine($"Selected   : {string.Join(", ", response.LegacySelectedIds.Take(12))}");
        builder.AppendLine();
        builder.AppendLine("Candidate Score Comparison");
        builder.AppendLine("--------------------------");
        if (response.CandidateScores.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in response.CandidateScores
                .OrderBy(static item => item.LegacyRank)
                .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Take(30))
            {
                builder.AppendLine(
                    $"- {item.CandidateId} selected={item.Selected} rank={item.LegacyRank}->{item.ShadowRank} legacy={item.LegacyScore:0.00} lifecycle={item.LifecycleAwareScore:0.00} delta={item.ScoreDelta:+0.00;-0.00;0.00}");
                builder.AppendLine($"  kind={item.Kind}/{item.Type} section={item.SectionName} reason={item.Reason}");
                var features = item.LifecycleFeatures;
                if (features.IsDeprecated || features.IsSuperseded || features.IsHistorical || features.IsRejected || features.IsCurrentVersion)
                {
                    builder.AppendLine($"  lifecycle deprecated={features.IsDeprecated} superseded={features.IsSuperseded} historical={features.IsHistorical} rejected={features.IsRejected} current={features.IsCurrentVersion} confidence={features.LifecycleConfidence:0.00}");
                }
            }
        }

        AppendShadowScoreList(builder, "Deprecated / Historical Demotions", response.DeprecatedDemotions.Concat(response.HistoricalDemotions).DistinctBy(static item => item.CandidateId).ToArray());
        AppendShadowScoreList(builder, "Current / Active Promotions", response.CurrentActivePromotions);
        AppendShadowScoreList(builder, "Version Conflict Fixes", response.VersionConflictFixes);
        AppendShadowScoreList(builder, "Must-hit Demotions", response.MustHitDemotions);
        AppendShadowScoreList(builder, "Must-not-hit Promotions", response.MustNotHitPromotions);
        AppendRankerShadowTraceQualitySummary(builder, snapshot.TraceQualitySummary);
        AppendRecentRankerShadowTraces(builder, snapshot.RecentShadowTraces);

        return builder.ToString();
    }

    public static string RenderError(ContextCoreApiException exception)
    {
        return ServiceOperationRenderer.RenderError(exception);
    }

    private static void AppendShadowScoreList(
        StringBuilder builder,
        string title,
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> items)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items.Take(12))
        {
            builder.AppendLine($"- {item.CandidateId} delta={item.ScoreDelta:+0.00;-0.00;0.00} rank={item.LegacyRank}->{item.ShadowRank} reason={item.Reason}");
        }
    }

    private static void AppendRankerShadowTraceQualitySummary(
        StringBuilder builder,
        RankerShadowTraceQualityReport report)
    {
        builder.AppendLine();
        builder.AppendLine("Trace Quality Summary");
        builder.AppendLine("---------------------");
        builder.AppendLine($"- traces={report.TraceCount} candidates={report.CandidateScoreCount} deprecated={report.DeprecatedDemotionCount} historical={report.HistoricalDemotionCount} versionFixes={report.VersionConflictFixCount}");
        builder.AppendLine($"- currentPromotions={report.CurrentVersionPromotionCount} avgDelta={report.AverageScoreDelta:0.00} maxPositive={report.MaxPositiveDelta:0.00} maxNegative={report.MaxNegativeDelta:0.00}");
        builder.AppendLine($"- risks mustHitDemoted={report.MustHitDemotedCount} mustNotHitPromoted={report.MustNotHitPromotedCount}");
        builder.AppendLine($"- next={report.RecommendedNextStep}");
    }

    private static void AppendRecentRankerShadowTraces(
        StringBuilder builder,
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> traces)
    {
        builder.AppendLine();
        builder.AppendLine("Recent Shadow Traces");
        builder.AppendLine("--------------------");
        if (traces.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var trace in traces.Take(5))
        {
            builder.AppendLine($"- {trace.RetrievalId} {trace.CreatedAt:yyyy-MM-dd HH:mm:ss} profile={trace.Profile} candidates={trace.CandidateScores.Count} demotions={trace.DeprecatedDemotions.Count}");
            builder.AppendLine($"  query: {Compact(trace.Query, 140)}");
        }
    }

    private static void AppendStringList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values.Take(20))
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static void AppendWorkingItems(StringBuilder builder, string title, IReadOnlyList<ShortTermWorkingItem> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.ItemId} [{item.Kind}/{item.Status}/{item.Lifecycle}] importance={item.Importance:0.00}");
            builder.AppendLine($"  title   : {item.Title}");
            builder.AppendLine($"  summary : {Compact(item.Summary, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))}");
        }

        builder.AppendLine();
    }

    private static void AppendConstraints(StringBuilder builder, string title, IReadOnlyList<ContextConstraint> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}/{item.Scope}] confidence={item.Confidence:0.00}");
            builder.AppendLine($"  content : {Compact(item.Content, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Take(8))}");
        }

        builder.AppendLine();
    }

    private static void AppendMemoryItems(StringBuilder builder, string title, IReadOnlyList<ContextMemoryItem> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Type}/{item.Status}] importance={item.Importance:0.00}");
            builder.AppendLine($"  content : {Compact(item.Content, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Take(8))}");
        }

        builder.AppendLine();
    }

    private static string Compact(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static JobPayloadInfo TryParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new JobPayloadInfo();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? operationId = null;

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("OperationId") || property.NameEquals("operationId"))
                    {
                        operationId = property.Value.GetString();
                    }

                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        metadata[property.Name] = property.Value.ToString();
                    }
                }
            }

            return new JobPayloadInfo
            {
                OperationId = operationId,
                Metadata = metadata
            };
        }
        catch
        {
            return new JobPayloadInfo();
        }
    }

    private sealed class JobPayloadInfo
    {
        public string? OperationId { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    private static void AppendWorkingSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<ShortTermWorkingItem> items)
    {
        builder.AppendLine(title);
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- {item.ItemId} [{item.Kind}/{item.Status}] {item.Summary}");
        }
    }

    private static void AppendMaintenanceSection(
        StringBuilder builder,
        ShortTermMaintenanceStatusResponse? maintenance)
    {
        builder.AppendLine("Maintenance");
        if (maintenance is null)
        {
            builder.AppendLine("- (unavailable)");
            return;
        }

        builder.AppendLine($"- Enabled       : {maintenance.Enabled}");
        builder.AppendLine($"- Running       : {maintenance.IsRunning}");
        builder.AppendLine($"- RunOnStartup  : {maintenance.RunOnStartup}");
        builder.AppendLine($"- IntervalSec   : {maintenance.IntervalSeconds}");
        builder.AppendLine($"- LastError     : {maintenance.LastError ?? "none"}");
        builder.AppendLine($"- LastRun       : {maintenance.LastRun?.RunId ?? "none"}");
    }

    private static string FormatDictionaryCompact(IReadOnlyDictionary<string, int> values)
    {
        return values.Count == 0
            ? "-"
            : string.Join(", ", values
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static string TrimHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Length <= 16 ? value : value[..16];
    }

    private static string FormatEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string ReadMetadata(ContextConstraint item, string key)
    {
        return item.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "-";
    }
}
