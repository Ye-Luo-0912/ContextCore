using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed partial class ControlRoomService
{

    private async Task<PostgresOperationalStoreDiagnostics> GetPostgresStorageDiagnosticsSafeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetServiceClient()
                .GetPostgresStorageDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ContextCoreApiException or InvalidOperationException)
        {
            return new PostgresOperationalStoreDiagnostics
            {
                Status = "Unavailable",
                ProviderCapabilityStatus = "Unavailable",
                Diagnostics = [$"PostgresDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static FileLayoutStatus BuildFileLayoutStatus(string rootPath)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new FileArtifactStore(options).BuildStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new FileLayoutStatus
            {
                DataRoot = rootPath,
                Diagnostics = [$"FileLayoutStatusUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static PostgresRelationStoreDiagnostics BuildPostgresRelationStoreDiagnostics(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, "storage", "postgres", "postgres-relation-store-diagnostics.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "storage",
                    "postgres",
                    "postgres-relation-store-diagnostics.json");
            }

            if (!File.Exists(path))
            {
                return new PostgresRelationStoreDiagnostics
                {
                    ActiveRuntimeProvider = "FileSystemRelationStore",
                    Diagnostics = ["RelationStoreDiagnosticsReportMissing"],
                    Recommendation = "RunEvalPostgresRelationStoreDiagnostics"
                };
            }

            var report = JsonSerializer.Deserialize<PostgresRelationStoreDiagnostics>(
                File.ReadAllText(path),
                JsonOptions);
            return report ?? new PostgresRelationStoreDiagnostics
            {
                ActiveRuntimeProvider = "FileSystemRelationStore",
                Diagnostics = ["RelationStoreDiagnosticsReportInvalid"],
                Recommendation = "RunEvalPostgresRelationStoreDiagnostics"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PostgresRelationStoreDiagnostics
            {
                ActiveRuntimeProvider = "FileSystemRelationStore",
                Diagnostics = [$"RelationStoreDiagnosticsUnavailable:{ex.GetType().Name}"],
                Recommendation = "RunEvalPostgresRelationStoreDiagnostics"
            };
        }
    }

    private static PostgresRelationReviewProviderDiagnostics BuildPostgresRelationReviewProviderDiagnostics(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-review-diagnostics.json",
            new PostgresRelationReviewProviderDiagnostics { ActiveRuntimeProvider = "FileSystemRelationStore", Diagnostics = ["RelationReviewDiagnosticsReportMissing"], Recommendation = "RunEvalPostgresRelationReviewDiagnostics" });

    private static PostgresRelationReviewParityReport BuildPostgresRelationReviewParityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-review-parity-report.json",
            new PostgresRelationReviewParityReport { Diagnostics = ["RelationReviewParityReportMissing"], Recommendation = "RunEvalPostgresRelationReviewParity" });

    private static PostgresRelationGovernanceParityReport BuildPostgresRelationGovernanceParityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-governance-parity-report.json",
            new PostgresRelationGovernanceParityReport { Diagnostics = ["RelationGovernanceParityReportMissing"], Recommendation = "RunEvalPostgresRelationGovernanceParity" });

    private static PostgresRelationGovernanceReadinessGateReport BuildPostgresRelationGovernanceReadinessGateReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-governance-readiness-gate.json",
            new PostgresRelationGovernanceReadinessGateReport { BlockedReasons = ["RelationGovernanceReadinessGateReportMissing"], Recommendation = "RunEvalPostgresRelationGovernanceReadinessGate" });

    private static PostgresRelationDualWriteQualityReport BuildPostgresRelationDualWriteQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-dual-write-quality-report.json",
            new PostgresRelationDualWriteQualityReport { Diagnostics = ["RelationDualWriteQualityReportMissing"], Recommendation = "RunEvalPostgresRelationDualWriteQuality" });

    private static PostgresRelationShadowReadQualityReport BuildPostgresRelationShadowReadQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-shadow-read-quality-report.json",
            new PostgresRelationShadowReadQualityReport { Diagnostics = ["RelationShadowReadQualityReportMissing"], Recommendation = "RunEvalPostgresRelationShadowReadQuality" });

    private static PostgresRelationProviderSwitchSmokeReport BuildPostgresRelationProviderSwitchSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-provider-switch-smoke-report.json",
            new PostgresRelationProviderSwitchSmokeReport { Diagnostics = ["RelationProviderSwitchSmokeReportMissing"], Recommendation = "RunEvalPostgresRelationProviderSwitchSmoke" });

    private static PostgresRelationProviderSwitchGateReport BuildPostgresRelationProviderSwitchGateReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-provider-switch-gate.json",
            new PostgresRelationProviderSwitchGateReport { BlockedReasons = ["RelationProviderSwitchGateReportMissing"], Recommendation = "RunEvalPostgresRelationProviderSwitchGate" });

    private static PostgresRelationRuntimeCanaryReport BuildPostgresRelationRuntimeCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-runtime-canary-report.json",
            new PostgresRelationRuntimeCanaryReport { Diagnostics = ["RelationRuntimeCanaryReportMissing"], Recommendation = "RunEvalPostgresRelationRuntimeCanary" });

    private static PostgresRelationScopedExtendedCanaryReport BuildPostgresRelationScopedExtendedCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-scoped-extended-canary-report.json",
            new PostgresRelationScopedExtendedCanaryReport { Diagnostics = ["RelationScopedExtendedCanaryReportMissing"], Recommendation = "RunEvalPostgresRelationScopedExtendedCanary" });

    private static PostgresRelationSelectedWorkspaceCanaryReport BuildPostgresRelationSelectedWorkspaceCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-selected-workspace-canary-report.json",
            new PostgresRelationSelectedWorkspaceCanaryReport { Diagnostics = ["RelationSelectedWorkspaceCanaryReportMissing"], Recommendation = "RunEvalPostgresRelationSelectedWorkspaceCanary" });

    private static PostgresRelationScopedExpansionReport BuildPostgresRelationScopedExpansionReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-scoped-expansion-smoke-report.json",
            new PostgresRelationScopedExpansionReport { Diagnostics = ["RelationScopedExpansionReportMissing"], Recommendation = "RunEvalPostgresRelationScopedExpansionSmoke" });

    private static PostgresRelationScopedObservationReport BuildPostgresRelationScopedObservationReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-scoped-observation-quality-report.json",
            new PostgresRelationScopedObservationReport { Diagnostics = ["RelationScopedObservationReportMissing"], Recommendation = "RunEvalPostgresRelationScopedObservationQuality" });

    private static PostgresRelationSelectedNormalWorkspaceCanaryReport BuildPostgresRelationSelectedNormalWorkspaceCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-selected-normal-workspace-canary-report.json",
            new PostgresRelationSelectedNormalWorkspaceCanaryReport { Diagnostics = ["RelationSelectedNormalWorkspaceCanaryReportMissing"], Recommendation = "RunEvalPostgresRelationSelectedNormalWorkspaceCanary" });

    private static PostgresRelationLimitedNormalScopeObservationReport BuildPostgresRelationLimitedNormalScopeObservationReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-limited-normal-scope-quality-report.json",
            new PostgresRelationLimitedNormalScopeObservationReport { Diagnostics = ["RelationLimitedNormalScopeObservationReportMissing"], Recommendation = "RunEvalPostgresRelationLimitedNormalScopeObservation" });

    private static PostgresRelationMultiNormalScopeCanaryReport BuildPostgresRelationMultiNormalScopeCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-relation-multi-normal-scope-quality-report.json",
            new PostgresRelationMultiNormalScopeCanaryReport { Diagnostics = ["RelationMultiNormalScopeCanaryReportMissing"], Recommendation = "RunEvalPostgresRelationMultiNormalScopeCanary" });

    private static PostgresLearningFeedbackDiagnosticsReport BuildPostgresLearningFeedbackDiagnosticsReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-diagnostics.json",
            new PostgresLearningFeedbackDiagnosticsReport { Diagnostics = ["PostgresLearningFeedbackDiagnosticsReportMissing"], Status = "RunEvalPostgresLearningFeedbackDiagnostics" });

    private static PostgresLearningFeedbackParityReport BuildPostgresLearningFeedbackParityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-parity-report.json",
            new PostgresLearningFeedbackParityReport { Diagnostics = ["PostgresLearningFeedbackParityReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackParity" });

    private static LearningFeedbackPostgresReadinessGateReport BuildPostgresLearningFeedbackReadinessGateReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-readiness-gate.json",
            new LearningFeedbackPostgresReadinessGateReport { FailedConditions = ["PostgresLearningFeedbackReadinessGateMissing"], Recommendation = "RunEvalPostgresLearningFeedbackReadinessGate" });

    private static LearningFeedbackDualWriteSmokeReport BuildPostgresLearningFeedbackDualWriteSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-dual-write-smoke-report.json",
            new LearningFeedbackDualWriteSmokeReport { Mismatches = ["PostgresLearningFeedbackDualWriteSmokeReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackDualWriteSmoke" });

    private static LearningFeedbackShadowReadSmokeReport BuildPostgresLearningFeedbackShadowReadSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-shadow-read-smoke-report.json",
            new LearningFeedbackShadowReadSmokeReport { Mismatches = ["PostgresLearningFeedbackShadowReadSmokeReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackShadowReadSmoke" });

    private static LearningFeedbackProviderQualityReport BuildPostgresLearningFeedbackProviderQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-provider-quality-report.json",
            new LearningFeedbackProviderQualityReport { Diagnostics = ["PostgresLearningFeedbackProviderQualityReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackProviderQuality" });

    private static LearningFeedbackSelectedNormalScopeCanaryReport BuildPostgresLearningFeedbackSelectedNormalScopeCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-selected-normal-scope-canary-report.json",
            new LearningFeedbackSelectedNormalScopeCanaryReport { BlockedReasons = ["PostgresLearningFeedbackSelectedNormalScopeCanaryReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackSelectedNormalScopeCanary" });

    private static LearningFeedbackLimitedScopeObservationReport BuildPostgresLearningFeedbackLimitedScopeObservationReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-limited-scope-observation-report.json",
            new LearningFeedbackLimitedScopeObservationReport { BlockedReasons = ["PostgresLearningFeedbackLimitedScopeObservationReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackLimitedScopeObservation" });

    private static LearningFeedbackLimitedScopeQualityReport BuildPostgresLearningFeedbackLimitedScopeQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-limited-scope-quality-report.json",
            new LearningFeedbackLimitedScopeQualityReport { BlockedReasons = ["PostgresLearningFeedbackLimitedScopeQualityReportMissing"], Recommendation = "RunEvalPostgresLearningFeedbackLimitedScopeQuality" });

    private static LearningFeedbackPostgresFreezeGateReport BuildPostgresLearningFeedbackFreezeGateReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-learning-feedback-freeze-gate.json",
            new LearningFeedbackPostgresFreezeGateReport { BlockedReasons = ["PostgresLearningFeedbackFreezeGateMissing"], Recommendation = "RunEvalPostgresLearningFeedbackFreezeGate" });

    private static PostgresJobQueueDiagnosticsReport BuildPostgresJobQueueDiagnosticsReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-diagnostics.json",
            new PostgresJobQueueDiagnosticsReport { Diagnostics = ["PostgresJobQueueDiagnosticsMissing"], Recommendation = "RunEvalPostgresJobQueueDiagnostics" });

    private static PostgresJobQueueParityReport BuildPostgresJobQueueParityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-parity-report.json",
            new PostgresJobQueueParityReport { Diagnostics = ["PostgresJobQueueParityMissing"], Recommendation = "RunEvalPostgresJobQueueParity" });

    private static PostgresJobQueueLeaseSmokeReport BuildPostgresJobQueueLeaseSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-lease-smoke-report.json",
            new PostgresJobQueueLeaseSmokeReport { Diagnostics = ["PostgresJobQueueLeaseSmokeMissing"], Recommendation = "RunEvalPostgresJobQueueLeaseSmoke" });

    private static PostgresJobQueueDualWriteSmokeReport BuildPostgresJobQueueDualWriteSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-dual-write-smoke-report.json",
            new PostgresJobQueueDualWriteSmokeReport { Diagnostics = ["PostgresJobQueueDualWriteSmokeMissing"], Recommendation = "RunEvalPostgresJobQueueDualWriteSmoke" });

    private static PostgresJobQueueShadowReadSmokeReport BuildPostgresJobQueueShadowReadSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-shadow-read-smoke-report.json",
            new PostgresJobQueueShadowReadSmokeReport { Diagnostics = ["PostgresJobQueueShadowReadSmokeMissing"], Recommendation = "RunEvalPostgresJobQueueShadowReadSmoke" });

    private static PostgresJobQueueProviderQualityReport BuildPostgresJobQueueProviderQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-provider-quality-report.json",
            new PostgresJobQueueProviderQualityReport { Diagnostics = ["PostgresJobQueueProviderQualityMissing"], Recommendation = "RunEvalPostgresJobQueueProviderQuality" });

    private static PostgresJobQueueScopedWorkerCanaryReport BuildPostgresJobQueueScopedWorkerCanaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-scoped-worker-canary-report.json",
            new PostgresJobQueueScopedWorkerCanaryReport { Diagnostics = ["PostgresJobQueueScopedWorkerCanaryMissing"], Recommendation = "RunEvalPostgresJobQueueScopedWorkerCanary" });

    private static PostgresJobQueueScopedWorkerQualityReport BuildPostgresJobQueueScopedWorkerQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-scoped-worker-quality-report.json",
            new PostgresJobQueueScopedWorkerQualityReport { Diagnostics = ["PostgresJobQueueScopedWorkerQualityMissing"], Recommendation = "RunEvalPostgresJobQueueScopedWorkerQuality" });

    private static PostgresJobQueueLimitedWorkerScopeObservationReport BuildPostgresJobQueueLimitedWorkerScopeObservationReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-limited-worker-scope-observation-report.json",
            new PostgresJobQueueLimitedWorkerScopeObservationReport { Diagnostics = ["PostgresJobQueueLimitedWorkerScopeObservationMissing"], Recommendation = "RunEvalPostgresJobQueueLimitedWorkerScopeObservation" });

    private static PostgresJobQueueLimitedWorkerScopeQualityReport BuildPostgresJobQueueLimitedWorkerScopeQualityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-limited-worker-scope-quality-report.json",
            new PostgresJobQueueLimitedWorkerScopeQualityReport { Diagnostics = ["PostgresJobQueueLimitedWorkerScopeQualityMissing"], Recommendation = "RunEvalPostgresJobQueueLimitedWorkerScopeQuality" });

    private static JobQueuePostgresFreezeGateReport BuildPostgresJobQueueFreezeGateReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-job-queue-freeze-gate.json",
            new JobQueuePostgresFreezeGateReport { BlockedReasons = ["PostgresJobQueueFreezeGateMissing"], Recommendation = "RunEvalPostgresJobQueueFreezeGate" });

    private static PostgresVectorDiagnosticsReport BuildPostgresVectorDiagnosticsReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-diagnostics.json",
            new PostgresVectorDiagnosticsReport { Diagnostics = ["PostgresVectorDiagnosticsMissing"], Recommendation = "RunEvalPostgresVectorDiagnostics" });

    private static PostgresVectorCompatibilityReport BuildPostgresVectorCompatibilityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-compatibility.json",
            new PostgresVectorCompatibilityReport { Diagnostics = ["PostgresVectorCompatibilityMissing"], Recommendation = "RunEvalPostgresVectorCompatibility" });

    private static PostgresVectorProviderSmokeReport BuildPostgresVectorProviderSmokeReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-provider-smoke-report.json",
            new PostgresVectorProviderSmokeReport { Diagnostics = ["PostgresVectorProviderSmokeMissing"], Recommendation = "RunEvalPostgresVectorProviderSmoke" });

    private static PostgresVectorIndexParityReport BuildPostgresVectorIndexParityReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-parity-report.json",
            new PostgresVectorIndexParityReport { Diagnostics = ["PostgresVectorParityMissing"], Recommendation = "RunEvalPostgresVectorParity" });

    private static PostgresVectorProviderScopedReindexPlan BuildPostgresVectorProviderScopedReindexPlan(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-provider-scoped-reindex-plan.json",
            new PostgresVectorProviderScopedReindexPlan { Diagnostics = ["PostgresVectorProviderScopedReindexPlanMissing"], Recommendation = "RunEvalPostgresVectorProviderScopedReindexPlan" });

    private static PostgresVectorProviderScopedReindexResult BuildPostgresVectorProviderScopedReindexResult(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-provider-scoped-reindex-apply-report.json",
            new PostgresVectorProviderScopedReindexResult { Diagnostics = ["PostgresVectorProviderScopedReindexApplyMissing"], Recommendation = "RunEvalPostgresVectorProviderScopedReindexApply" });

    private static PostgresVectorProviderScopedReindexReport BuildPostgresVectorProviderScopedReindexReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-provider-scoped-reindex-quality-report.json",
            new PostgresVectorProviderScopedReindexReport { Diagnostics = ["PostgresVectorProviderScopedReindexQualityMissing"], Recommendation = "RunEvalPostgresVectorProviderScopedReindexQuality" });

    private static PostgresVectorQueryPreviewReport BuildPostgresVectorQueryPreviewReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-query-preview-report.json",
            new PostgresVectorQueryPreviewReport { Diagnostics = ["PostgresVectorQueryPreviewMissing"], Recommendation = "RunEvalPostgresVectorQueryPreview" });

    private static PostgresVectorShadowEvalReport BuildPostgresVectorShadowEvalReport(
        string rootPath,
        string fileName,
        string datasetName) =>
        ReadPostgresReport(rootPath, fileName,
            new PostgresVectorShadowEvalReport { DatasetName = datasetName, Diagnostics = ["PostgresVectorShadowEvalMissing"], Recommendation = "RunEvalPostgresVectorShadowEval" });

    private static PostgresVectorShadowEvalSummaryReport BuildPostgresVectorShadowEvalSummaryReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-shadow-eval-summary.json",
            new PostgresVectorShadowEvalSummaryReport { Diagnostics = ["PostgresVectorShadowEvalSummaryMissing"], Recommendation = "RunEvalPostgresVectorShadowEval" });

    private static VectorPostgresProviderFreezeGateReport BuildPostgresVectorFreezeGateReport(string rootPath) =>
        ReadPostgresReport(rootPath, "postgres-vector-freeze-gate.json",
            new VectorPostgresProviderFreezeGateReport { BlockedReasons = ["PostgresVectorFreezeGateMissing"], Recommendation = "RunEvalPostgresVectorFreezeGate" });

    private static T ReadPostgresReport<T>(string rootPath, string fileName, T fallback)
    {
        try
        {
            var path = Path.Combine(rootPath, "storage", "postgres", fileName);
            if (!File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), "storage", "postgres", fileName);
            }

            if (!File.Exists(path))
            {
                return fallback;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    private static MemoryLayoutDiagnostics BuildMemoryLayoutDiagnostics(
        string rootPath,
        string workspaceId,
        string collectionId)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildMemoryLayoutDiagnostics(workspaceId, collectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new MemoryLayoutDiagnostics
            {
                DataRoot = rootPath,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = [$"MemoryLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static TraceLayoutDiagnostics BuildTraceLayoutDiagnostics(
        string rootPath,
        string workspaceId,
        string collectionId)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildTraceLayoutDiagnostics(workspaceId, collectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new TraceLayoutDiagnostics
            {
                DataRoot = rootPath,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = [$"TraceLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static ReportLayoutDiagnostics BuildReportLayoutDiagnostics(string rootPath)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildReportLayoutDiagnostics();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ReportLayoutDiagnostics
            {
                DataRoot = rootPath,
                Diagnostics = [$"ReportLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static StorageBoundaryReport BuildStorageBoundaryReport()
    {
        try
        {
            return StorageResponsibilityRegistry.BuildReport();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new StorageBoundaryReport
            {
                Diagnostics = [$"StorageBoundaryReportUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static async Task<RouterIntentClassifierBaselineReport?> ReadRouterIntentBaselineReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RouterOutputDirectory,
            EvalReportPaths.RouterIntentBaselineReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterIntentClassifierBaselineReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningFeedbackFeatureCandidateReport?> ReadLearningFeedbackFeatureCandidateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "learning",
            "feedback",
            "learning-feedback-feature-candidates-report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningFeedbackFeatureCandidateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningFeedbackQualityReport?> ReadLearningFeedbackQualityReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "learning",
            "feedback",
            "learning-feedback-quality-report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningFeedbackQualityReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningApprovedFeedbackDatasetGateReport?> ReadLearningApprovedFeedbackDatasetGateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "learning",
            "feedback",
            "learning-approved-feedback-dataset-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningApprovedFeedbackDatasetGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<RouterShadowTraceQualityReport?> ReadRouterShadowTraceQualityReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RouterOutputDirectory,
            EvalReportPaths.RouterShadowTraceQualityReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterShadowTraceQualityReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<RouterDisagreementTriageReport?> ReadRouterDisagreementTriageReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RouterOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterDisagreementTriageReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<int> ReadRouterHardNegativeCountAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RouterOutputDirectory,
            EvalReportPaths.RouterHardNegativesFileName);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            var count = 0;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    count++;
                }
            }

            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static async Task<RouterGuardedOptInReadinessGateReport?> ReadRouterGuardedOptInReadinessGateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RouterOutputDirectory,
            EvalReportPaths.RouterGuardedOptInReadinessGateReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterGuardedOptInReadinessGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerShadowEvalReport?> ReadCandidateRerankerShadowEvalReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerShadowEvalReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerFeatureCompletenessReport?> ReadCandidateRerankerFeatureCompletenessReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerFeatureCompletenessReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerShadowFailureAuditReport?> ReadCandidateRerankerShadowFailureAuditReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerShadowFailureAuditReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerScoreDistributionReport?> ReadCandidateRerankerScoreDistributionReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerScoreDistributionReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerListwiseCalibrationReport?> ReadCandidateRerankerListwiseCalibrationReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerListwiseCalibrationReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerFormalPriorityAlignmentReport?> ReadCandidateRerankerFormalPriorityAlignmentReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerFormalPriorityAlignmentReport>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerShadowTraceQualityReport?> ReadCandidateRerankerShadowTraceQualityReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.RankerOutputDirectory,
            EvalReportPaths.RankerShadowTraceQualityReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerShadowTraceQualityReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningReadinessRegistry?> ReadLearningReadinessRegistryAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.ReadinessOutputDirectory,
            EvalReportPaths.LearningReadinessFreezeReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningReadinessRegistry>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningRuntimeChangeReadinessGateReport?> ReadLearningRuntimeChangeReadinessGateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            EvalReportPaths.ReadinessOutputDirectory,
            EvalReportPaths.LearningRuntimeChangeReadinessGateFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningRuntimeChangeReadinessGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ServiceVectorShadowQualitySummary LoadVectorShadowQualitySummary()
    {
        var candidates = new[]
        {
            Path.Combine("eval", "vector-query-profile-sweep-extended.json"),
            Path.Combine("eval", "vector-query-profile-sweep-a3.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var report = JsonSerializer.Deserialize<VectorQueryProfileSweepReport>(
                    File.ReadAllText(path),
                    JsonOptions);
                var best = report?.BestResult;
                var residual = TryLoadVectorResidualRiskReport();
                var lifecycleCoverage = TryLoadVectorLifecycleMetadataCoverageReport();
                var lifecycleBackfill = TryLoadVectorLifecycleMetadataBackfillPlan();
                var recallLoss = TryLoadVectorRecallLossReports();
                var safeRecovery = TryLoadVectorSafeRecallRecoveryReports();
                var fusionShadow = TryLoadVectorRankerFusionShadowReports();
                var representation = TryLoadVectorRepresentationBenchmarkReports();
                var queryExpansion = TryLoadVectorQueryExpansionShadowReports();
                var readinessGate = TryLoadVectorReadinessGateReport();
                var providerComparison = TryLoadVectorProviderComparisonReport();
                var qwen3ReadinessGate = TryLoadVectorQwen3ReadinessGateReport();
                var providerComparisonFreeze = TryLoadEmbeddingProviderComparisonFreezeReport();
                var hybridPreview = TryLoadVectorHybridPreviewReport();
                var hybridGate = TryLoadVectorHybridReadinessGateReport();
                var hybridAudit = TryLoadVectorHybridRecallRegressionAuditReport();
                var hybridFreeze = TryLoadVectorHybridFreezeReport();
                var alignmentAudit = TryLoadVectorRetrievalDatasetAlignmentAuditSummaryReport();
                var eligibilityTriage = TryLoadVectorEligibilityRecallLossTriageSummaryReport();
                var lifecycleRepairPlan = TryLoadVectorLifecycleMetadataRepairPlanSummaryReport();
                var lifecycleReviewCandidates = TryLoadVectorLifecycleMetadataReviewCandidateReport();
                var lifecycleReviewSummary = TryLoadVectorLifecycleMetadataReviewSummaryReport();
                var lifecycleSidecarPreview = TryLoadVectorLifecycleMetadataSidecarPreviewReport();
                var sidecarEligibility = TryLoadVectorSidecarEligibilityQualityReport();
                var reviewBatch = TryLoadVectorLifecycleMetadataReviewBatchSummary();
                var evidenceBackfill = TryLoadVectorLifecycleMetadataEvidenceBackfillReport();
                var datasetV2Generation = TryLoadRetrievalDatasetV2GenerationSummary();
                var datasetV2Materialization = TryLoadRetrievalDatasetV2MaterializationSummary();
                var datasetV2ShadowEval = TryLoadRetrievalDatasetV2ShadowEvalSummary();
                var datasetV2Stress = TryLoadRetrievalDatasetV2StressSummary();
                var datasetV2StressTriage = TryLoadRetrievalDatasetV2StressFailureTriageSummary();
                var datasetV2HybridRepair = TryLoadRetrievalDatasetV2HybridScoringRepairSummary();
                var datasetV2HybridRiskTriage = TryLoadRetrievalDatasetV2HybridScoringRiskTriageSummary();
                var datasetV2StressFreeze = TryLoadRetrievalDatasetV2StressFreezeSummary();
                var vectorV4Recheck = TryLoadVectorV4ReadinessRecheckSummary();
                var shadowPackageComparison = TryLoadVectorShadowPackageComparisonSummary();
                var formalRetrievalIntegrationPlan = TryLoadFormalRetrievalIntegrationPlanSummary();
                var formalRetrievalIntegrationDecision = TryLoadFormalRetrievalIntegrationDecisionSummary();
                var shadowFormalRetrievalAdapterPlan = TryLoadShadowFormalRetrievalAdapterPlanSummary();
                var shadowFormalRetrievalAdapter = TryLoadShadowFormalRetrievalAdapterSummary();
                var formalAdapterPackageShadowComparison = TryLoadFormalAdapterPackageShadowComparisonSummary();
                var graphVectorRetrievalQualityAudit = TryLoadGraphVectorRetrievalQualityAuditSummary();
                var retrievalQualityRepairPreview = TryLoadRetrievalQualityRepairPreviewSummary();
                var runtimeObservableFeatureContract = TryLoadRuntimeObservableFeatureContractSummary();
                var runtimeRetrievalFeatureDerivation = TryLoadRuntimeRetrievalFeatureDerivationSummary();
                var runtimeRetrievalFeatureDerivationRepair = TryLoadRuntimeRetrievalFeatureDerivationRepairSummary();
                var featureDerivationFailureFreeze = TryLoadRuntimeFeatureDerivationFailureFreezeSummary();
                var graphHubNoiseControl = TryLoadGraphHubNoiseControlSummary();
                var retrievalEvalProtocol = TryLoadRetrievalEvalProtocolSummary();
                var inputMetadataEnrichment = TryLoadInputMetadataEnrichmentSummary();
                var enrichedCandidateSourceRepairRecheck = TryLoadEnrichedCandidateSourceRepairRecheckSummary();
                var sourceAwareRankingRepair = TryLoadSourceAwareRankingRepairSummary();
                var outputTokenPriorityShadow = TryLoadOutputTokenPriorityShadowSummary();
                var formalAdapterInputContract = TryLoadFormalAdapterInputContractSummary();
                var formalRetrievalIntegrationFreeze = TryLoadFormalRetrievalIntegrationFreezeSummary();
                if (report is null || best is null)
                {
                    continue;
                }

                return new ServiceVectorShadowQualitySummary
                {
                    Available = true,
                    SourcePath = path,
                    CurrentRecommendation = report.Recommendation,
                    BestProfile = best.ProfileId,
                    BestTopK = best.TopK,
                    BestMinSimilarity = best.MinSimilarity,
                    RiskAfterPolicy = best.RiskAfterPolicy,
                    SimilaritySeparation = best.SimilaritySeparation,
                    ResidualRiskSourcePath = residual?.SourcePath ?? string.Empty,
                    ResidualRiskCount = residual?.Report.ResidualRiskCount ?? 0,
                    TopResidualRiskTypes = residual?.Report.RiskAfterPolicyByType ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    TopWhyPolicyAllowed = residual?.Report.Risks
                        .Select(item => item.WhyPolicyAllowed)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray() ?? Array.Empty<string>(),
                    TopExpectedActions = residual?.Report.Risks
                        .Select(item => item.ExpectedAction)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray() ?? Array.Empty<string>(),
                    LifecycleMetadataCoverageSourcePath = lifecycleCoverage?.SourcePath ?? string.Empty,
                    LifecycleMetadataCoverageRate = lifecycleCoverage?.Report.LifecycleCoverageRate ?? 0,
                    UnknownLifecycleCount = lifecycleCoverage?.Report.UnknownLifecycleCount ?? 0,
                    MissingReviewStatusCount = lifecycleCoverage?.Report.MissingReviewStatusCount ?? 0,
                    MissingReplacementInfoCount = lifecycleCoverage?.Report.MissingReplacementInfoCount ?? 0,
                    BlockedByLifecycleMetadataGate = residual?.Report.BlockedByLifecycleMetadataGate ?? 0,
                    LifecycleBackfillPlanSourcePath = lifecycleBackfill?.SourcePath ?? string.Empty,
                    BackfillUnknownLifecycleBefore = lifecycleBackfill?.Plan.UnknownLifecycleBefore ?? 0,
                    BackfillAutoResolvableCount = lifecycleBackfill?.Plan.AutoResolvableCount ?? 0,
                    BackfillManualReviewRequiredCount = lifecycleBackfill?.Plan.ManualReviewRequiredCount ?? 0,
                    BackfillExpectedCoverageAfter = lifecycleBackfill?.Plan.ExpectedCoverageAfter ?? 0,
                    RecallLossA3SourcePath = recallLoss.A3SourcePath,
                    RecallLossExtendedSourcePath = recallLoss.ExtendedSourcePath,
                    A3RecallAfterPolicy = recallLoss.A3?.MustHitRecallAfterPolicy ?? 0,
                    ExtendedRecallAfterPolicy = recallLoss.Extended?.MustHitRecallAfterPolicy ?? 0,
                    A3RecallRecommendation = recallLoss.A3?.Recommendation ?? string.Empty,
                    ExtendedRecallRecommendation = recallLoss.Extended?.Recommendation ?? string.Empty,
                    TopRecallMissReasons = MergeMissReasons(recallLoss.A3, recallLoss.Extended),
                    IntentReadinessRecommendations = BuildIntentReadinessSummary(recallLoss.A3, recallLoss.Extended),
                    SafeRecallRecoveryA3SourcePath = safeRecovery.A3SourcePath,
                    SafeRecallRecoveryExtendedSourcePath = safeRecovery.ExtendedSourcePath,
                    SafeRecoveryA3RecallAfterPolicy = safeRecovery.A3?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                    SafeRecoveryExtendedRecallAfterPolicy = safeRecovery.Extended?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                    SafeRecoveryA3BestConfiguration = safeRecovery.A3?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                    SafeRecoveryExtendedBestConfiguration = safeRecovery.Extended?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                    SafeRecoveryA3RecoveredBelowTopK = safeRecovery.A3?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                    SafeRecoveryExtendedRecoveredBelowTopK = safeRecovery.Extended?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                    BlockedMustHitClassificationCounts = MergeBlockedMustHitClassifications(safeRecovery.A3, safeRecovery.Extended),
                    FusionShadowA3SourcePath = fusionShadow.A3SourcePath,
                    FusionShadowExtendedSourcePath = fusionShadow.ExtendedSourcePath,
                    FusionBestStrategy = SelectFusionBestStrategy(fusionShadow.A3, fusionShadow.Extended),
                    FusionA3RecallAfterPolicy = fusionShadow.A3?.BestResult?.MustHitRecallFusion ?? 0,
                    FusionExtendedRecallAfterPolicy = fusionShadow.Extended?.BestResult?.MustHitRecallFusion ?? 0,
                    FusionRiskAfterPolicy = BuildFusionRiskSummary(fusionShadow.A3, fusionShadow.Extended),
                    FusionRecallGain = BuildFusionRecallGainSummary(fusionShadow.A3, fusionShadow.Extended),
                    FusionReadinessGateSatisfied = IsFusionReadinessSatisfied(fusionShadow.A3, fusionShadow.Extended),
                    RepresentationBenchmarkA3SourcePath = representation.A3SourcePath,
                    RepresentationBenchmarkExtendedSourcePath = representation.ExtendedSourcePath,
                    RepresentationBestDocumentProfile = SelectRepresentationBestDocumentProfile(representation.A3, representation.Extended),
                    RepresentationBestQueryProfile = SelectRepresentationBestQueryProfile(representation.A3, representation.Extended),
                    RepresentationA3RecallAfterPolicy = representation.A3?.BestResult?.Recall ?? 0,
                    RepresentationExtendedRecallAfterPolicy = representation.Extended?.BestResult?.Recall ?? 0,
                    RepresentationRiskAfterPolicy = BuildRepresentationRiskSummary(representation.A3, representation.Extended),
                    RepresentationRecoveredMissCount = BuildRepresentationRecoveredMissSummary(representation.A3, representation.Extended),
                    RepresentationV4GateSatisfied = IsRepresentationReadinessSatisfied(representation.A3, representation.Extended),
                    QueryExpansionShadowA3SourcePath = queryExpansion.A3SourcePath,
                    QueryExpansionShadowExtendedSourcePath = queryExpansion.ExtendedSourcePath,
                    QueryExpansionBestProfile = SelectQueryExpansionBestProfile(queryExpansion.A3, queryExpansion.Extended),
                    QueryExpansionA3RecallBefore = queryExpansion.A3?.BestResult?.RecallBeforeExpansion ?? 0,
                    QueryExpansionA3RecallAfter = queryExpansion.A3?.BestResult?.RecallAfterExpansion ?? 0,
                    QueryExpansionExtendedRecallBefore = queryExpansion.Extended?.BestResult?.RecallBeforeExpansion ?? 0,
                    QueryExpansionExtendedRecallAfter = queryExpansion.Extended?.BestResult?.RecallAfterExpansion ?? 0,
                    QueryExpansionRecoveredMissCount = BuildQueryExpansionRecoveredMissSummary(queryExpansion.A3, queryExpansion.Extended),
                    QueryExpansionRiskAfterPolicy = BuildQueryExpansionRiskSummary(queryExpansion.A3, queryExpansion.Extended),
                    QueryExpansionV4GateSatisfied = IsQueryExpansionReadinessSatisfied(queryExpansion.A3, queryExpansion.Extended),
                    V4ReadinessGateSourcePath = readinessGate?.SourcePath ?? string.Empty,
                    V4ReadinessGatePassed = readinessGate?.Report.Passed ?? false,
                                        V4ReadinessGateFailReasons = readinessGate?.Report.FailReasons ?? Array.Empty<string>(),
                    ProviderComparisonSourcePath = providerComparison?.SourcePath ?? string.Empty,
                    ProviderComparisonResults = providerComparison?.Report.Providers ?? Array.Empty<VectorProviderComparisonV310Result>(),
                    Qwen3ReadinessGateSourcePath = qwen3ReadinessGate?.SourcePath ?? string.Empty,
                    Qwen3ReadinessGatePassed = qwen3ReadinessGate?.Report.Passed ?? false,
                    Qwen3Recommendation = qwen3ReadinessGate?.Report.Recommendation ?? string.Empty,
                    Qwen3BlockedReasons = qwen3ReadinessGate?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ProviderComparisonFreezeSourcePath = providerComparisonFreeze?.SourcePath ?? string.Empty,
                    ProviderPromotionStatus = providerComparisonFreeze?.Report.PromotionStatus ?? string.Empty,
                    ProviderConfigurationSanityPassed = false,
                    ProviderComparisonStatus = (providerComparisonFreeze?.Report.Passed ?? false) ? "Conclusive" : (providerComparisonFreeze is not null ? "Inconclusive" : string.Empty),
                    VectorV4RecheckAllowed = providerComparisonFreeze?.Report.VectorV4RecheckAllowed ?? false,
                    ProviderPromotionBlockedReasons = providerComparisonFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    HybridPreviewSourcePath = hybridPreview?.SourcePath ?? string.Empty,
                    HybridFullA3Recall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                    HybridFullExtendedRecall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                    HybridFullRiskAfterPolicy = Math.Max(hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0, hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0),
                    HybridReadinessRecommendation = hybridPreview?.Report.Recommendation ?? string.Empty,
                    HybridReadinessGatePassed = hybridGate?.Report.Passed ?? false,
                    HybridAuditSourcePath = hybridAudit?.SourcePath ?? string.Empty,
                    HybridAuditPassed = hybridAudit?.Report.Passed ?? false,
                    HybridAuditRecommendation = hybridAudit?.Report.Recommendation ?? string.Empty,
                    HybridAuditDenseDroppedCount = hybridAudit?.Report.DenseCandidateDroppedCount ?? 0,
                    HybridAuditEligibilityMismatchCount = hybridAudit?.Report.EligibilityMismatchCount ?? 0,
                    HybridAuditDedupOverwriteCount = hybridAudit?.Report.DedupOverwriteCount ?? 0,
                    HybridFreezeSourcePath = hybridFreeze?.SourcePath ?? string.Empty,
                    HybridFreezePassed = hybridFreeze?.Report.FreezePassed ?? false,
                    HybridFreezeStatus = hybridFreeze?.Report.HybridRetrievalStatus ?? string.Empty,
                    HybridFreezeRecommendation = hybridFreeze?.Report.Recommendation ?? string.Empty,
                    HybridV4RecheckAllowed = hybridFreeze?.Report.V4RecheckAllowed ?? false,
                    HybridFreezeBlockedReasons = hybridFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    DatasetAlignmentAuditSourcePath = alignmentAudit?.SourcePath ?? string.Empty,
                    DatasetAlignmentRecommendation = alignmentAudit?.Report.Recommendation ?? string.Empty,
                    DatasetAlignmentIssueCount = alignmentAudit?.Report.AlignmentIssueCount ?? 0,
                    DatasetAlignmentA3MustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: false),
                    DatasetAlignmentExtendedMustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: false),
                    DatasetAlignmentA3ProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: true),
                    DatasetAlignmentExtendedProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: true),
                    DatasetAlignmentEligibilityBlockCount = alignmentAudit?.Report.Reports.Sum(item => item.MustHitBlockedByEligibilityCount) ?? 0,
                    DatasetAlignmentAnchorCoverageRate = ResolveAlignmentAnchorCoverage(alignmentAudit?.Report),
                    DatasetAlignmentTopIssues = alignmentAudit?.Report.IssueBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    EligibilityRecallLossTriageSourcePath = eligibilityTriage?.SourcePath ?? string.Empty,
                    EligibilityFilteredMustHitCount = eligibilityTriage?.Report.TotalFilteredMustHit ?? 0,
                    EligibilityCorrectlyBlockedCount = eligibilityTriage?.Report.CorrectlyBlockedCount ?? 0,
                    EligibilityRouteToHistoricalCount = eligibilityTriage?.Report.RouteToHistoricalCount ?? 0,
                    EligibilityRouteToAuditCount = eligibilityTriage?.Report.RouteToAuditCount ?? 0,
                    EligibilityMetadataRepairNeededCount = eligibilityTriage?.Report.MetadataRepairNeededCount ?? 0,
                    EligibilityEvalExpectationReviewNeededCount = eligibilityTriage?.Report.EvalExpectationReviewNeededCount ?? 0,
                    EligibilityUnsafeToRecoverCount = eligibilityTriage?.Report.UnsafeToRecoverCount ?? 0,
                    EligibilityRecallLossRecommendation = eligibilityTriage?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataRepairPlanSourcePath = lifecycleRepairPlan?.SourcePath ?? string.Empty,
                    LifecycleMetadataRepairCandidateCount = lifecycleRepairPlan?.Report.CandidateCount ?? 0,
                    LifecycleMetadataRepairAutoRepairableCount = lifecycleRepairPlan?.Report.AutoRepairableCount ?? 0,
                    LifecycleMetadataRepairHumanReviewRequiredCount = lifecycleRepairPlan?.Report.HumanReviewRequiredCount ?? 0,
                    LifecycleMetadataRepairForbiddenCount = lifecycleRepairPlan?.Report.ForbiddenRepairCount ?? 0,
                    LifecycleMetadataRepairEstimatedRecallRecovery = lifecycleRepairPlan?.Report.EstimatedRecallRecovery ?? 0,
                    LifecycleMetadataRepairRiskEstimate = lifecycleRepairPlan?.Report.RiskAfterRepairEstimate ?? 0,
                    LifecycleMetadataRepairRecommendation = lifecycleRepairPlan?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataReviewCandidatesSourcePath = lifecycleReviewCandidates?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewCandidateCount = lifecycleReviewCandidates?.Report.CandidateCount ?? 0,
                    LifecycleMetadataReviewPendingCount = lifecycleReviewCandidates?.Report.PendingCount ?? 0,
                    LifecycleMetadataReviewCorrectlyBlockedSkippedCount = lifecycleReviewCandidates?.Report.CorrectlyBlockedSkippedCount ?? 0,
                    LifecycleMetadataReviewCountByLayer = lifecycleReviewCandidates?.Report.CountByLayer ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    LifecycleMetadataReviewCountByItemKind = lifecycleReviewCandidates?.Report.CountByItemKind ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    LifecycleMetadataReviewRecentCandidates = lifecycleReviewCandidates?.Report.RecentCandidates ?? Array.Empty<VectorLifecycleMetadataReviewCandidate>(),
                    LifecycleMetadataReviewRecommendation = lifecycleReviewCandidates?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataReviewSummarySourcePath = lifecycleReviewSummary?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewApprovedForSidecarCount = lifecycleReviewSummary?.Report.ApprovedForSidecarCount ?? 0,
                    LifecycleMetadataReviewRejectedCount = lifecycleReviewSummary?.Report.RejectedCount ?? 0,
                    LifecycleMetadataReviewNeedsEvidenceCount = lifecycleReviewSummary?.Report.NeedsEvidenceCount ?? 0,
                    LifecycleMetadataReviewSupersededCount = lifecycleReviewSummary?.Report.SupersededCount ?? 0,
                    LifecycleMetadataReviewSidecarEntryCount = lifecycleReviewSummary?.Report.SidecarEntryCount ?? lifecycleSidecarPreview?.Report.SidecarEntryCount ?? 0,
                    LifecycleMetadataReviewUnsafeApprovalBlockedCount = lifecycleReviewSummary?.Report.UnsafeApprovalBlockedCount ?? 0,
                    LifecycleMetadataReviewSidecarPreviewSourcePath = lifecycleSidecarPreview?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewNormalContextApprovalCount = lifecycleReviewSummary?.Report.NormalContextApprovalCount ?? lifecycleSidecarPreview?.Report.NormalContextEntryCount ?? 0,
                    LifecycleMetadataReviewAuditContextApprovalCount = lifecycleReviewSummary?.Report.AuditContextApprovalCount ?? lifecycleSidecarPreview?.Report.AuditContextEntryCount ?? 0,
                    LifecycleMetadataReviewHistoricalContextApprovalCount = lifecycleReviewSummary?.Report.HistoricalContextApprovalCount ?? lifecycleSidecarPreview?.Report.HistoricalContextEntryCount ?? 0,
                    LifecycleMetadataReviewDiagnosticsOnlyApprovalCount = lifecycleReviewSummary?.Report.DiagnosticsOnlyApprovalCount ?? lifecycleSidecarPreview?.Report.DiagnosticsOnlyEntryCount ?? 0,
                    SidecarEligibilityPreviewSourcePath = sidecarEligibility?.SourcePath ?? string.Empty,
                    SidecarEligibilityCandidateCount = sidecarEligibility?.Report.CandidateCount ?? 0,
                    SidecarEligibilitySidecarEntryCount = sidecarEligibility?.Report.SidecarEntryCount ?? 0,
                    SidecarEligibilityApprovedSidecarCount = sidecarEligibility?.Report.ApprovedSidecarCount ?? 0,
                    SidecarEligibilityPendingReviewCount = sidecarEligibility?.Report.PendingReviewCount ?? 0,
                    SidecarEligibilityEffectiveMetadataChangedCount = sidecarEligibility?.Report.EffectiveMetadataChangedCount ?? 0,
                    SidecarEligibilityUnsafeBlockedCount = sidecarEligibility?.Report.UnsafeSidecarBlockedCount ?? 0,
                    SidecarEligibilityConflictBlockedCount = sidecarEligibility?.Report.ConflictSidecarBlockedCount ?? 0,
                    SidecarEligibilitySourceItemUnchanged = sidecarEligibility?.Report.SourceItemUnchanged ?? true,
                    SidecarEligibilityRecommendation = sidecarEligibility?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataReviewBatchSourcePath = reviewBatch?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewBatchId = reviewBatch?.Batch.BatchId ?? string.Empty,
                    LifecycleMetadataReviewBatchStatus = reviewBatch?.Batch.Status ?? string.Empty,
                    LifecycleMetadataReviewBatchCandidateCount = reviewBatch?.Batch.CandidateCount ?? 0,
                    LifecycleMetadataReviewBatchValidationErrorCount = reviewBatch?.Validation?.ValidationErrorCount ?? 0,
                    LifecycleMetadataReviewBatchWouldWriteSidecarCount = reviewBatch?.ApplyPreview?.WouldWriteSidecarEntryCount ?? 0,
                    LifecycleMetadataReviewBatchUnsafeBlockedCount = reviewBatch?.ApplyPreview?.UnsafeBlockedCount ?? reviewBatch?.Validation?.UnsafeDecisionCount ?? 0,
                    LifecycleMetadataReviewBatchRecommendation = reviewBatch?.ApplyPreview?.Recommendation ?? reviewBatch?.Validation?.Recommendation ?? (reviewBatch is null ? string.Empty : "ReadyForManualReview"),
                    LifecycleMetadataEvidenceBackfillSourcePath = evidenceBackfill?.SourcePath ?? string.Empty,
                    LifecycleMetadataEvidenceBackfillCandidateCount = evidenceBackfill?.Report.CandidateCount ?? 0,
                    LifecycleMetadataEvidenceFoundCount = evidenceBackfill?.Report.EvidenceFoundCount ?? 0,
                    LifecycleMetadataSourceRefFoundCount = evidenceBackfill?.Report.SourceRefFoundCount ?? 0,
                    LifecycleMetadataProvenanceFoundCount = evidenceBackfill?.Report.ProvenanceFoundCount ?? 0,
                    LifecycleMetadataAutoRepairableAfterBackfillCount = evidenceBackfill?.Report.AutoRepairableAfterBackfillCount ?? 0,
                    LifecycleMetadataNeedsEvidenceAfterBackfillCount = evidenceBackfill?.Report.NeedsEvidenceCount ?? 0,
                    LifecycleMetadataEvidenceBackfillRecommendation = evidenceBackfill?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2GenerationSourcePath = datasetV2Generation?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2CorpusItemCount = datasetV2Generation?.CorpusItemCount ?? 0,
                    RetrievalDatasetV2SampleCount = datasetV2Generation?.SampleCount ?? 0,
                    RetrievalDatasetV2ValidationIssueCount = datasetV2Generation?.ValidationIssueCount ?? 0,
                    RetrievalDatasetV2MissingEvidenceCount = datasetV2Generation?.MissingEvidenceCount ?? 0,
                    RetrievalDatasetV2MissingProvenanceCount = datasetV2Generation?.MissingProvenanceCount ?? 0,
                    RetrievalDatasetV2DifficultyBreakdown = datasetV2Generation?.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2SplitBreakdown = datasetV2Generation?.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2Recommendation = datasetV2Generation?.Recommendation ?? string.Empty,
                    RetrievalDatasetV2MaterializationSourcePath = datasetV2Materialization?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2DatasetId = datasetV2Materialization?.Report.DatasetId ?? string.Empty,
                    RetrievalDatasetV2CorpusHash = datasetV2Materialization?.Report.CorpusHash ?? string.Empty,
                    RetrievalDatasetV2SamplesHash = datasetV2Materialization?.Report.SamplesHash ?? string.Empty,
                    RetrievalDatasetV2MaterializationGatePassed = datasetV2Materialization?.Report.GatePassed ?? false,
                    RetrievalDatasetV2MaterializationCorpusHashStable = datasetV2Materialization?.Report.CorpusHashStable ?? false,
                    RetrievalDatasetV2MaterializationSamplesHashStable = datasetV2Materialization?.Report.SamplesHashStable ?? false,
                    RetrievalDatasetV2MaterializationRecommendation = datasetV2Materialization?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalSourcePath = datasetV2ShadowEval?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalDatasetId = datasetV2ShadowEval?.Summary.DatasetId ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalBestProfileName = datasetV2ShadowEval?.Summary.BestProfileName ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalBestRecallAfterPolicy = datasetV2ShadowEval?.Summary.BestRecallAfterPolicy ?? 0,
                    RetrievalDatasetV2ShadowEvalBestMrrAfterPolicy = datasetV2ShadowEval?.Summary.BestMrrAfterPolicy ?? 0,
                    RetrievalDatasetV2ShadowEvalBestRiskAfterPolicy = datasetV2ShadowEval?.Summary.BestRiskAfterPolicy ?? 0,
                    RetrievalDatasetV2ShadowEvalPgVectorParityPassed = datasetV2ShadowEval?.Summary.PgVectorParityPassed ?? false,
                    RetrievalDatasetV2ShadowEvalRecommendation = datasetV2ShadowEval?.Gate?.Recommendation ?? datasetV2ShadowEval?.Summary.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressSourcePath = datasetV2Stress?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2StressDatasetId = datasetV2Stress?.Report.DatasetId ?? string.Empty,
                    RetrievalDatasetV2StressCorpusItemCount = datasetV2Stress?.Report.CorpusItemCount ?? 0,
                    RetrievalDatasetV2StressSampleCount = datasetV2Stress?.Report.SampleCount ?? 0,
                    RetrievalDatasetV2StressSplitBreakdown = datasetV2Stress?.Report.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressDifficultyBreakdown = datasetV2Stress?.Report.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressLeakageIssueCount = datasetV2Stress?.Report.LeakageIssueCount ?? 0,
                    RetrievalDatasetV2StressAnchorDominanceScore = datasetV2Stress?.Report.AnchorDominanceScore ?? 0,
                    RetrievalDatasetV2StressDenseRecall = datasetV2Stress?.Report.DenseRecall ?? 0,
                    RetrievalDatasetV2StressLexicalRecall = datasetV2Stress?.Report.LexicalRecall ?? 0,
                    RetrievalDatasetV2StressAnchorRecall = datasetV2Stress?.Report.AnchorRecall ?? 0,
                    RetrievalDatasetV2StressHybridRecall = datasetV2Stress?.Report.HybridRecall ?? 0,
                    RetrievalDatasetV2StressHoldoutHybridRecall = datasetV2Stress?.Report.HoldoutHybridRecall ?? 0,
                    RetrievalDatasetV2StressRecommendation = datasetV2Stress?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressTriageSourcePath = datasetV2StressTriage?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2StressFailureCount = datasetV2StressTriage?.Report.FailureCount ?? 0,
                    RetrievalDatasetV2StressHoldoutFailureCount = datasetV2StressTriage?.Report.HoldoutFailureCount ?? 0,
                    RetrievalDatasetV2StressFailureCountBySplit = datasetV2StressTriage?.Report.FailureCountBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressFailureCountByDifficulty = datasetV2StressTriage?.Report.FailureCountByDifficulty ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressFailureCountByReason = datasetV2StressTriage?.Report.FailureCountByReason ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressDenseOnlyWinCount = datasetV2StressTriage?.Report.DenseOnlyWinCount ?? 0,
                    RetrievalDatasetV2StressHybridWinCount = datasetV2StressTriage?.Report.HybridWinCount ?? 0,
                    RetrievalDatasetV2StressAnchorRegressionCount = datasetV2StressTriage?.Report.AnchorRegressionCount ?? 0,
                    RetrievalDatasetV2StressProfileComparisonSummary = FormatDatasetV2StressProfileComparisons(datasetV2StressTriage?.Report),
                    RetrievalDatasetV2StressTriageRecommendation = datasetV2StressTriage?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2HybridRepairSourcePath = datasetV2HybridRepair?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2HybridRepairBestProfileName = datasetV2HybridRepair?.BestProfile?.ProfileName ?? datasetV2HybridRepair?.Report.BestProfileName ?? string.Empty,
                    RetrievalDatasetV2HybridRepairRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.RecallAfterPolicy ?? 0,
                    RetrievalDatasetV2HybridRepairHoldoutRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.HoldoutRecallAfterPolicy ?? 0,
                    RetrievalDatasetV2HybridRepairDenseWinnerLostCount = datasetV2HybridRepair?.BestProfile?.DenseWinnerLostCount ?? 0,
                    RetrievalDatasetV2HybridRepairMustHitBelowTopKCount = datasetV2HybridRepair?.BestProfile?.MustHitBelowTopKCount ?? 0,
                    RetrievalDatasetV2HybridRepairNegativeDistractorCount = datasetV2HybridRepair?.BestProfile?.NegativeDistractorOutranksMustHitCount ?? 0,
                    RetrievalDatasetV2HybridRepairRiskAfterPolicy = datasetV2HybridRepair?.BestProfile?.RiskAfterPolicy ?? 0,
                    RetrievalDatasetV2HybridRepairRecommendation = datasetV2HybridRepair?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2HybridRiskTriageSourcePath = datasetV2HybridRiskTriage?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2HybridRiskTriageProfileName = datasetV2HybridRiskTriage?.Report.ProfileName ?? string.Empty,
                    RetrievalDatasetV2HybridRiskCandidateCount = datasetV2HybridRiskTriage?.Report.RiskCandidateCount ?? 0,
                    RetrievalDatasetV2HybridRiskByType = datasetV2HybridRiskTriage?.Report.RiskByType ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2HybridRiskBySplit = datasetV2HybridRiskTriage?.Report.RiskBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2HybridMustNotPromotedCount = datasetV2HybridRiskTriage?.Report.MustNotCandidatePromotedCount ?? 0,
                    RetrievalDatasetV2HybridEligibilityBypassCount = datasetV2HybridRiskTriage?.Report.EligibilityBypassCount ?? 0,
                    RetrievalDatasetV2HybridRiskProjectionMismatchCount = datasetV2HybridRiskTriage?.Report.RiskProjectionMismatchCount ?? 0,
                    RetrievalDatasetV2HybridRiskTriageRecommendation = datasetV2HybridRiskTriage?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressFreezeSourcePath = datasetV2StressFreeze?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2StressFreezePassed = datasetV2StressFreeze?.Report.FreezePassed ?? false,
                    RetrievalDatasetV2StressFreezeStatus = datasetV2StressFreeze?.Report.DatasetV2Stress ?? string.Empty,
                    RetrievalDatasetV2StressFreezeRecommendation = datasetV2StressFreeze?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressFreezeBestProfile = datasetV2StressFreeze?.Report.BestPreviewProfile ?? string.Empty,
                    RetrievalDatasetV2StressFreezeStressRecall = datasetV2StressFreeze?.Report.StressRecall ?? 0,
                    RetrievalDatasetV2StressFreezeHoldoutRecall = datasetV2StressFreeze?.Report.HoldoutRecall ?? 0,
                    RetrievalDatasetV2StressFreezeRiskAfterPolicy = datasetV2StressFreeze?.Report.RiskAfterPolicy ?? 0,
                    RetrievalDatasetV2StressFreezeMustNotHitRiskAfterPolicy = datasetV2StressFreeze?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    RetrievalDatasetV2StressFreezeLifecycleRiskAfterPolicy = datasetV2StressFreeze?.Report.LifecycleRiskAfterPolicy ?? 0,
                    RetrievalDatasetV2StressFreezeFormalOutputChanged = datasetV2StressFreeze?.Report.FormalOutputChanged ?? 0,
                    RetrievalDatasetV2StressFreezeLeakageIssueCount = datasetV2StressFreeze?.Report.LeakageIssueCount ?? 0,
                    RetrievalDatasetV2StressFreezeAnchorDominanceScore = datasetV2StressFreeze?.Report.AnchorDominanceScore ?? 0,
                    RetrievalDatasetV2StressFreezeV4RecheckAllowed = datasetV2StressFreeze?.Report.V4RecheckAllowed ?? false,
                    RetrievalDatasetV2StressFreezeReadyForFormalRetrieval = datasetV2StressFreeze?.Report.ReadyForFormalRetrieval ?? false,
                    RetrievalDatasetV2StressFreezeFormalRetrievalAllowed = datasetV2StressFreeze?.Report.FormalRetrievalAllowed ?? false,
                    RetrievalDatasetV2StressFreezeBlockedReasons = datasetV2StressFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    VectorV4ReadinessRecheckSourcePath = vectorV4Recheck?.SourcePath ?? string.Empty,
                    VectorV4ReadinessRecheckPassed = vectorV4Recheck?.Report.RecheckPassed ?? false,
                    VectorV4ReadinessRecheckRecommendation = vectorV4Recheck?.Report.Recommendation ?? string.Empty,
                    VectorV4ReadinessLegacyStatus = vectorV4Recheck?.Report.LegacyVectorStatus ?? string.Empty,
                    VectorV4ReadinessSmallStatus = vectorV4Recheck?.Report.DatasetV2SmallStatus ?? string.Empty,
                    VectorV4ReadinessStressStatus = vectorV4Recheck?.Report.DatasetV2StressStatus ?? string.Empty,
                    VectorV4ReadinessPgVectorStatus = vectorV4Recheck?.Report.PgVectorProviderStatus ?? string.Empty,
                    VectorV4ReadinessHybridScoringStatus = vectorV4Recheck?.Report.HybridScoringRepairStatus ?? string.Empty,
                    VectorV4ReadinessRuntimeGateStatus = vectorV4Recheck?.Report.RuntimeChangeGateStatus ?? string.Empty,
                    VectorV4ReadinessBestProfile = vectorV4Recheck?.Report.BestPreviewProfile ?? string.Empty,
                    VectorV4ReadinessStressRecall = vectorV4Recheck?.Report.DatasetV2StressRecall ?? 0,
                    VectorV4ReadinessHoldoutRecall = vectorV4Recheck?.Report.DatasetV2HoldoutRecall ?? 0,
                    VectorV4ReadinessRiskAfterPolicy = vectorV4Recheck?.Report.RiskAfterPolicy ?? 0,
                    VectorV4ReadinessFormalOutputChanged = vectorV4Recheck?.Report.FormalOutputChanged ?? 0,
                    VectorV4ReadinessReadyForGuardedFormalPreview = vectorV4Recheck?.Report.ReadyForGuardedFormalPreview ?? false,
                    VectorV4ReadinessReadyForRuntimeSwitch = vectorV4Recheck?.Report.ReadyForRuntimeSwitch ?? false,
                    VectorV4ReadinessFormalRetrievalAllowed = vectorV4Recheck?.Report.FormalRetrievalAllowed ?? false,
                    VectorV4ReadinessBlockedReasons = vectorV4Recheck?.Report.BlockedReasons ?? Array.Empty<string>(),
                    VectorShadowPackageComparisonSourcePath = shadowPackageComparison?.SourcePath ?? string.Empty,
                    VectorShadowPackageComparisonGatePassed = shadowPackageComparison?.Report.GatePassed ?? false,
                    VectorShadowPackageComparisonRecommendation = shadowPackageComparison?.Report.Recommendation ?? string.Empty,
                    VectorShadowPackageComparisonProfileName = shadowPackageComparison?.Report.ProfileName ?? string.Empty,
                    VectorShadowPackageCandidateAddCount = shadowPackageComparison?.Report.CandidateAddCount ?? 0,
                    VectorShadowPackageCandidateRemoveCount = shadowPackageComparison?.Report.CandidateRemoveCount ?? 0,
                    VectorShadowPackageCandidateUnchangedCount = shadowPackageComparison?.Report.CandidateUnchangedCount ?? 0,
                    VectorShadowPackageSectionChangedCount = shadowPackageComparison?.Report.SectionChangedCount ?? 0,
                    VectorShadowPackageTokenDeltaTotal = shadowPackageComparison?.Report.TokenDeltaTotal ?? 0,
                    VectorShadowPackageTokenDeltaMax = shadowPackageComparison?.Report.TokenDeltaMax ?? 0,
                    VectorShadowPackageConstraintCoverageDelta = shadowPackageComparison?.Report.ConstraintCoverageDelta ?? 0,
                    VectorShadowPackageRelationCoverageDelta = shadowPackageComparison?.Report.RelationCoverageDelta ?? 0,
                    VectorShadowPackageRiskAfterPolicy = shadowPackageComparison?.Report.RiskAfterPolicy ?? 0,
                    VectorShadowPackageMustNotHitRiskAfterPolicy = shadowPackageComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    VectorShadowPackageLifecycleRiskAfterPolicy = shadowPackageComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                    VectorShadowPackageFormalOutputChanged = shadowPackageComparison?.Report.FormalOutputChanged ?? 0,
                    VectorShadowPackagePackageOutputChanged = shadowPackageComparison?.Report.PackageOutputChanged ?? false,
                    VectorShadowPackagePackingPolicyChanged = shadowPackageComparison?.Report.PackingPolicyChanged ?? false,
                    VectorShadowPackageRuntimeMutated = shadowPackageComparison?.Report.RuntimeMutated ?? false,
                    VectorShadowPackageReadyForRuntimeSwitch = shadowPackageComparison?.Report.ReadyForRuntimeSwitch ?? false,
                    VectorShadowPackageFormalRetrievalAllowed = shadowPackageComparison?.Report.FormalRetrievalAllowed ?? false,
                    VectorShadowPackageBlockedReasons = shadowPackageComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationPlanSourcePath = formalRetrievalIntegrationPlan?.SourcePath ?? string.Empty,
                    FormalRetrievalIntegrationPlanPassed = formalRetrievalIntegrationPlan?.Report.PlanPassed ?? false,
                    FormalRetrievalIntegrationPlanRecommendation = formalRetrievalIntegrationPlan?.Report.Recommendation ?? string.Empty,
                    FormalRetrievalIntegrationPlanAllowedMode = formalRetrievalIntegrationPlan?.Report.AllowedMode ?? string.Empty,
                    FormalRetrievalIntegrationPlanRequiredNextPhase = formalRetrievalIntegrationPlan?.Report.RequiredNextPhase ?? string.Empty,
                    FormalRetrievalIntegrationPlanFormalRetrievalAllowed = formalRetrievalIntegrationPlan?.Report.FormalRetrievalAllowed ?? false,
                    FormalRetrievalIntegrationPlanRuntimeSwitchAllowed = formalRetrievalIntegrationPlan?.Report.RuntimeSwitchAllowed ?? false,
                    FormalRetrievalIntegrationPlanReadyForRuntimeSwitch = formalRetrievalIntegrationPlan?.Report.ReadyForRuntimeSwitch ?? false,
                    FormalRetrievalIntegrationPlanIntegrationPoints = formalRetrievalIntegrationPlan?.Report.IntegrationPoints ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationPlanBlockedReasons = formalRetrievalIntegrationPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationDecisionSourcePath = formalRetrievalIntegrationDecision?.SourcePath ?? string.Empty,
                    FormalRetrievalIntegrationDecisionPassed = formalRetrievalIntegrationDecision?.Snapshot.DecisionPassed ?? false,
                    FormalRetrievalIntegrationDecisionGatePassed = formalRetrievalIntegrationDecision?.Snapshot.GatePassed ?? false,
                    FormalRetrievalIntegrationDecisionRecommendation = formalRetrievalIntegrationDecision?.Snapshot.Recommendation ?? string.Empty,
                    FormalRetrievalIntegrationDecisionValue = formalRetrievalIntegrationDecision?.Snapshot.IntegrationDecision ?? string.Empty,
                    FormalRetrievalIntegrationDecisionNextAllowedPhase = formalRetrievalIntegrationDecision?.Snapshot.NextAllowedPhase ?? string.Empty,
                    FormalRetrievalIntegrationDecisionReadyForFreeze = formalRetrievalIntegrationDecision?.Snapshot.ReadyForFormalRetrievalIntegrationFreeze ?? false,
                    FormalRetrievalIntegrationDecisionReadyForNoOpBindingPlan = formalRetrievalIntegrationDecision?.Snapshot.ReadyForAdapterNoOpBindingPlan ?? false,
                    FormalRetrievalIntegrationDecisionFormalRetrievalAllowed = formalRetrievalIntegrationDecision?.Snapshot.FormalRetrievalAllowed ?? false,
                    FormalRetrievalIntegrationDecisionRuntimeSwitchAllowed = formalRetrievalIntegrationDecision?.Snapshot.RuntimeSwitchAllowed ?? false,
                    FormalRetrievalIntegrationDecisionReadyForRuntimeSwitch = formalRetrievalIntegrationDecision?.Snapshot.ReadyForRuntimeSwitch ?? false,
                    FormalRetrievalIntegrationDecisionRiskAfterPolicy = formalRetrievalIntegrationDecision?.Snapshot.RiskAfterPolicy ?? 0,
                    FormalRetrievalIntegrationDecisionFormalOutputChanged = formalRetrievalIntegrationDecision?.Snapshot.FormalOutputChanged ?? 0,
                    FormalRetrievalIntegrationDecisionPackageOutputChanged = formalRetrievalIntegrationDecision?.Snapshot.PackageOutputChanged ?? false,
                    FormalRetrievalIntegrationDecisionPackingPolicyChanged = formalRetrievalIntegrationDecision?.Snapshot.PackingPolicyChanged ?? false,
                    FormalRetrievalIntegrationDecisionRuntimeMutated = formalRetrievalIntegrationDecision?.Snapshot.RuntimeMutated ?? false,
                    FormalRetrievalIntegrationDecisionVectorStoreBindingChanged = formalRetrievalIntegrationDecision?.Snapshot.VectorStoreBindingChanged ?? false,
                    FormalRetrievalIntegrationDecisionBlockedReasons = formalRetrievalIntegrationDecision?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                    ShadowFormalRetrievalAdapterPlanSourcePath = shadowFormalRetrievalAdapterPlan?.SourcePath ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanPassed = shadowFormalRetrievalAdapterPlan?.Report.PlanPassed ?? false,
                    ShadowFormalRetrievalAdapterPlanRecommendation = shadowFormalRetrievalAdapterPlan?.Report.Recommendation ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanAllowedMode = shadowFormalRetrievalAdapterPlan?.Report.AllowedMode ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanVectorProviderSource = shadowFormalRetrievalAdapterPlan?.Report.VectorProviderSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanGraphCandidateSource = shadowFormalRetrievalAdapterPlan?.Report.GraphCandidateSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanFormalRetrievalAllowed = shadowFormalRetrievalAdapterPlan?.Report.FormalRetrievalAllowed ?? false,
                    ShadowFormalRetrievalAdapterPlanRuntimeSwitchAllowed = shadowFormalRetrievalAdapterPlan?.Report.RuntimeSwitchAllowed ?? false,
                    ShadowFormalRetrievalAdapterPlanForbiddenActions = shadowFormalRetrievalAdapterPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                    ShadowFormalRetrievalAdapterPlanBlockedReasons = shadowFormalRetrievalAdapterPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowFormalRetrievalAdapterSourcePath = shadowFormalRetrievalAdapter?.SourcePath ?? string.Empty,
                    ShadowFormalRetrievalAdapterPassed = shadowFormalRetrievalAdapter?.Report.AdapterPassed ?? false,
                    ShadowFormalRetrievalAdapterGatePassed = shadowFormalRetrievalAdapter?.Report.GatePassed ?? false,
                    ShadowFormalRetrievalAdapterRecommendation = shadowFormalRetrievalAdapter?.Report.Recommendation ?? string.Empty,
                    ShadowFormalRetrievalAdapterAllowedMode = shadowFormalRetrievalAdapter?.Report.AllowedMode ?? string.Empty,
                    ShadowFormalRetrievalAdapterVectorProviderSource = shadowFormalRetrievalAdapter?.Report.VectorProviderSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterGraphCandidateSource = shadowFormalRetrievalAdapter?.Report.GraphCandidateSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterSampleCount = shadowFormalRetrievalAdapter?.Report.SampleCount ?? 0,
                    ShadowFormalRetrievalAdapterRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.RiskAfterPolicy ?? 0,
                    ShadowFormalRetrievalAdapterMustNotHitRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    ShadowFormalRetrievalAdapterLifecycleRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.LifecycleRiskAfterPolicy ?? 0,
                    ShadowFormalRetrievalAdapterFormalOutputChanged = shadowFormalRetrievalAdapter?.Report.FormalOutputChanged ?? 0,
                    ShadowFormalRetrievalAdapterFormalSelectedSetChanged = shadowFormalRetrievalAdapter?.Report.FormalSelectedSetChanged ?? false,
                    ShadowFormalRetrievalAdapterPackageOutputChanged = shadowFormalRetrievalAdapter?.Report.PackageOutputChanged ?? false,
                    ShadowFormalRetrievalAdapterPackingPolicyChanged = shadowFormalRetrievalAdapter?.Report.PackingPolicyChanged ?? false,
                    ShadowFormalRetrievalAdapterRuntimeMutated = shadowFormalRetrievalAdapter?.Report.RuntimeMutated ?? false,
                    ShadowFormalRetrievalAdapterVectorStoreBindingChanged = shadowFormalRetrievalAdapter?.Report.VectorStoreBindingChanged ?? false,
                    ShadowFormalRetrievalAdapterBlockedReasons = shadowFormalRetrievalAdapter?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalAdapterPackageShadowComparisonSourcePath = formalAdapterPackageShadowComparison?.SourcePath ?? string.Empty,
                    FormalAdapterPackageShadowComparisonPassed = formalAdapterPackageShadowComparison?.Report.ComparisonPassed ?? false,
                    FormalAdapterPackageShadowComparisonGatePassed = formalAdapterPackageShadowComparison?.Report.GatePassed ?? false,
                    FormalAdapterPackageShadowComparisonRecommendation = formalAdapterPackageShadowComparison?.Report.Recommendation ?? string.Empty,
                    FormalAdapterPackageShadowComparisonAllowedMode = formalAdapterPackageShadowComparison?.Report.AllowedMode ?? string.Empty,
                    FormalAdapterPackageShadowComparisonSampleCount = formalAdapterPackageShadowComparison?.Report.SampleCount ?? 0,
                    FormalAdapterPackageShadowComparisonRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.RiskAfterPolicy ?? 0,
                    FormalAdapterPackageShadowComparisonMustNotHitRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    FormalAdapterPackageShadowComparisonLifecycleRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaTotal ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaMax = formalAdapterPackageShadowComparison?.Report.TokenDeltaMax ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaBudgetTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetTotal ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaBudgetPerSample = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetPerSample ?? 0,
                    FormalAdapterPackageShadowComparisonFormalOutputChanged = formalAdapterPackageShadowComparison?.Report.FormalOutputChanged ?? 0,
                    FormalAdapterPackageShadowComparisonFormalSelectedSetChanged = formalAdapterPackageShadowComparison?.Report.FormalSelectedSetChanged ?? false,
                    FormalAdapterPackageShadowComparisonPackageOutputChanged = formalAdapterPackageShadowComparison?.Report.PackageOutputChanged ?? false,
                    FormalAdapterPackageShadowComparisonPackingPolicyChanged = formalAdapterPackageShadowComparison?.Report.PackingPolicyChanged ?? false,
                    FormalAdapterPackageShadowComparisonRuntimeMutated = formalAdapterPackageShadowComparison?.Report.RuntimeMutated ?? false,
                    FormalAdapterPackageShadowComparisonVectorStoreBindingChanged = formalAdapterPackageShadowComparison?.Report.VectorStoreBindingChanged ?? false,
                    FormalAdapterPackageShadowComparisonBlockedReasons = formalAdapterPackageShadowComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                    GraphVectorRetrievalQualityAuditSourcePath = graphVectorRetrievalQualityAudit?.SourcePath ?? string.Empty,
                    GraphVectorRetrievalQualityAuditPassed = graphVectorRetrievalQualityAudit?.Report.AuditPassed ?? false,
                    GraphVectorRetrievalQualityAuditGatePassed = graphVectorRetrievalQualityAudit?.Report.GatePassed ?? false,
                    GraphVectorRetrievalQualityAuditRecommendation = graphVectorRetrievalQualityAudit?.Report.Recommendation ?? string.Empty,
                    GraphVectorRetrievalQualityAuditAllowedMode = graphVectorRetrievalQualityAudit?.Report.AllowedMode ?? string.Empty,
                    GraphVectorRetrievalQualityAuditSampleCount = graphVectorRetrievalQualityAudit?.Report.SampleCount ?? 0,
                    GraphVectorRetrievalQualityAuditRecall = graphVectorRetrievalQualityAudit?.Report.Recall ?? 0,
                    GraphVectorRetrievalQualityAuditPrecision = graphVectorRetrievalQualityAudit?.Report.Precision ?? 0,
                    GraphVectorRetrievalQualityAuditMrr = graphVectorRetrievalQualityAudit?.Report.MeanReciprocalRank ?? 0,
                    GraphVectorRetrievalQualityAuditGraphNoiseCount = graphVectorRetrievalQualityAudit?.Report.GraphNoiseCount ?? 0,
                    GraphVectorRetrievalQualityAuditVectorNoiseCount = graphVectorRetrievalQualityAudit?.Report.VectorNoiseCount ?? 0,
                    GraphVectorRetrievalQualityAuditRankingRegressionCount = graphVectorRetrievalQualityAudit?.Report.RankingRegressionCount ?? 0,
                    GraphVectorRetrievalQualityAuditMustHitBelowTopKCount = graphVectorRetrievalQualityAudit?.Report.MustHitBelowTopKCount ?? 0,
                    GraphVectorRetrievalQualityAuditRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.RiskAfterPolicy ?? 0,
                    GraphVectorRetrievalQualityAuditMustNotHitRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    GraphVectorRetrievalQualityAuditLifecycleRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.LifecycleRiskAfterPolicy ?? 0,
                    GraphVectorRetrievalQualityAuditSectionMismatchCount = graphVectorRetrievalQualityAudit?.Report.SectionMismatchCount ?? 0,
                    GraphVectorRetrievalQualityAuditMetadataEvidenceGapCount = graphVectorRetrievalQualityAudit?.Report.MetadataEvidenceGapCount ?? 0,
                    GraphVectorRetrievalQualityAuditFailureClusterIds = graphVectorRetrievalQualityAudit?.Report.FailureClusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>(),
                    GraphVectorRetrievalQualityAuditFormalOutputChanged = graphVectorRetrievalQualityAudit?.Report.FormalOutputChanged ?? 0,
                    GraphVectorRetrievalQualityAuditFormalSelectedSetChanged = graphVectorRetrievalQualityAudit?.Report.FormalSelectedSetChanged ?? false,
                    GraphVectorRetrievalQualityAuditPackageOutputChanged = graphVectorRetrievalQualityAudit?.Report.PackageOutputChanged ?? false,
                    GraphVectorRetrievalQualityAuditPackingPolicyChanged = graphVectorRetrievalQualityAudit?.Report.PackingPolicyChanged ?? false,
                    GraphVectorRetrievalQualityAuditRuntimeMutated = graphVectorRetrievalQualityAudit?.Report.RuntimeMutated ?? false,
                    GraphVectorRetrievalQualityAuditVectorStoreBindingChanged = graphVectorRetrievalQualityAudit?.Report.VectorStoreBindingChanged ?? false,
                    GraphVectorRetrievalQualityAuditBlockedReasons = graphVectorRetrievalQualityAudit?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RetrievalQualityRepairPreviewSourcePath = retrievalQualityRepairPreview?.SourcePath ?? string.Empty,
                    RetrievalQualityRepairPreviewPassed = retrievalQualityRepairPreview?.Report.PreviewPassed ?? false,
                    RetrievalQualityRepairPreviewGatePassed = retrievalQualityRepairPreview?.Report.GatePassed ?? false,
                    RetrievalQualityRepairPreviewRecommendation = retrievalQualityRepairPreview?.Report.Recommendation ?? string.Empty,
                    RetrievalQualityRepairPreviewAllowedMode = retrievalQualityRepairPreview?.Report.AllowedMode ?? string.Empty,
                    RetrievalQualityRepairPreviewBestProfileId = retrievalQualityRepairPreview?.Report.BestProfileId ?? string.Empty,
                    RetrievalQualityRepairPreviewBaselineRecall = retrievalQualityRepairPreview?.Report.Baseline.Recall ?? 0d,
                    RetrievalQualityRepairPreviewBaselinePrecision = retrievalQualityRepairPreview?.Report.Baseline.Precision ?? 0d,
                    RetrievalQualityRepairPreviewBaselineMrr = retrievalQualityRepairPreview?.Report.Baseline.MeanReciprocalRank ?? 0d,
                    RetrievalQualityRepairPreviewBestRecall = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Recall ?? 0d,
                    RetrievalQualityRepairPreviewBestPrecision = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Precision ?? 0d,
                    RetrievalQualityRepairPreviewBestMrr = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MeanReciprocalRank ?? 0d,
                    RetrievalQualityRepairPreviewRecallDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.RecallDelta ?? 0d,
                    RetrievalQualityRepairPreviewMrrDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MrrDelta ?? 0d,
                    RetrievalQualityRepairPreviewMustHitBelowTopKBaseline = retrievalQualityRepairPreview?.Report.Baseline.MustHitBelowTopKCount ?? 0,
                    RetrievalQualityRepairPreviewMustHitBelowTopKBest = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MustHitBelowTopKCount ?? 0,
                    RetrievalQualityRepairPreviewProfileEvaluatedCount = retrievalQualityRepairPreview?.Report.Profiles.Count ?? 0,
                    RetrievalQualityRepairPreviewRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.RiskAfterPolicy ?? 0,
                    RetrievalQualityRepairPreviewMustNotHitRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.MustNotHitRiskAfterPolicy ?? 0,
                    RetrievalQualityRepairPreviewLifecycleRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.LifecycleRiskAfterPolicy ?? 0,
                    RetrievalQualityRepairPreviewSectionMismatchCount = retrievalQualityRepairPreview?.Report.Baseline.SectionMismatchCount ?? 0,
                    RetrievalQualityRepairPreviewGraphNoiseCount = retrievalQualityRepairPreview?.Report.Baseline.GraphNoiseCount ?? 0,
                    RetrievalQualityRepairPreviewRankingRegressionCount = retrievalQualityRepairPreview?.Report.Baseline.RankingRegressionCount ?? 0,
                    RetrievalQualityRepairPreviewTokenDeltaTotal = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDelta ?? 0,
                    RetrievalQualityRepairPreviewTokenDeltaMax = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDeltaAbsolute ?? 0,
                    RetrievalQualityRepairPreviewFormalOutputChanged = retrievalQualityRepairPreview?.Report.FormalOutputChanged ?? 0,
                    RetrievalQualityRepairPreviewFormalSelectedSetChanged = retrievalQualityRepairPreview?.Report.FormalSelectedSetChanged ?? false,
                    RetrievalQualityRepairPreviewPackageOutputChanged = retrievalQualityRepairPreview?.Report.PackageOutputChanged ?? false,
                    RetrievalQualityRepairPreviewPackingPolicyChanged = retrievalQualityRepairPreview?.Report.PackingPolicyChanged ?? false,
                    RetrievalQualityRepairPreviewRuntimeMutated = retrievalQualityRepairPreview?.Report.RuntimeMutated ?? false,
                    RetrievalQualityRepairPreviewVectorStoreBindingChanged = retrievalQualityRepairPreview?.Report.VectorStoreBindingChanged ?? false,
                    RetrievalQualityRepairPreviewBlockedReasons = retrievalQualityRepairPreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RuntimeObservableFeatureContractSourcePath = runtimeObservableFeatureContract?.SourcePath ?? string.Empty,
                    RuntimeObservableFeatureContractPassed = runtimeObservableFeatureContract?.Report.ContractPassed ?? false,
                    RuntimeObservableFeatureContractGatePassed = runtimeObservableFeatureContract?.Report.GatePassed ?? false,
                    RuntimeObservableFeatureContractRecommendation = runtimeObservableFeatureContract?.Report.Recommendation ?? string.Empty,
                    RuntimeObservableFeatureContractAllowedMode = runtimeObservableFeatureContract?.Report.AllowedMode ?? string.Empty,
                    RuntimeObservableFeatureContractBestProfileId = runtimeObservableFeatureContract?.Report.BestProfileId ?? string.Empty,
                    RuntimeObservableFeatureContractBestProfileContractStatus = runtimeObservableFeatureContract?.Report.BestProfileContractStatus ?? string.Empty,
                    RuntimeObservableFeatureContractForbiddenForScoringCount = runtimeObservableFeatureContract?.Report.ForbiddenForScoringCount ?? 0,
                    RuntimeObservableFeatureContractEvalOnlyCount = runtimeObservableFeatureContract?.Report.EvalOnlyCount ?? 0,
                    RuntimeObservableFeatureContractDerivedAtRuntimeCount = runtimeObservableFeatureContract?.Report.DerivedAtRuntimeCount ?? 0,
                    RuntimeObservableFeatureContractRuntimeObservableCount = runtimeObservableFeatureContract?.Report.RuntimeObservableCount ?? 0,
                    RuntimeObservableFeatureContractScoringFeatureCount = runtimeObservableFeatureContract?.Report.ScoringFeatureCount ?? 0,
                    RuntimeObservableFeatureContractFilteringFeatureCount = runtimeObservableFeatureContract?.Report.FilteringFeatureCount ?? 0,
                    RuntimeObservableFeatureContractCandidateExpansionFeatureCount = runtimeObservableFeatureContract?.Report.CandidateExpansionFeatureCount ?? 0,
                    RuntimeObservableFeatureContractSourceScanFiles = runtimeObservableFeatureContract?.Report.SourceScan.ScannedFileCount ?? 0,
                    RuntimeObservableFeatureContractFixtureTokenHitCount = runtimeObservableFeatureContract?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                    RuntimeObservableFeatureContractFlaggedTokens = runtimeObservableFeatureContract?.Report.SourceScan.FlaggedTokens ?? Array.Empty<string>(),
                    RuntimeObservableFeatureContractFormalOutputChanged = runtimeObservableFeatureContract?.Report.FormalOutputChanged ?? 0,
                    RuntimeObservableFeatureContractFormalSelectedSetChanged = runtimeObservableFeatureContract?.Report.FormalSelectedSetChanged ?? false,
                    RuntimeObservableFeatureContractPackageOutputChanged = runtimeObservableFeatureContract?.Report.PackageOutputChanged ?? false,
                    RuntimeObservableFeatureContractPackingPolicyChanged = runtimeObservableFeatureContract?.Report.PackingPolicyChanged ?? false,
                    RuntimeObservableFeatureContractRuntimeMutated = runtimeObservableFeatureContract?.Report.RuntimeMutated ?? false,
                    RuntimeObservableFeatureContractVectorStoreBindingChanged = runtimeObservableFeatureContract?.Report.VectorStoreBindingChanged ?? false,
                    RuntimeObservableFeatureContractBlockedReasons = runtimeObservableFeatureContract?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RuntimeRetrievalFeatureDerivationSourcePath = runtimeRetrievalFeatureDerivation?.SourcePath ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationPassed = runtimeRetrievalFeatureDerivation?.Report.PreviewPassed ?? false,
                    RuntimeRetrievalFeatureDerivationGatePassed = runtimeRetrievalFeatureDerivation?.Report.GatePassed ?? false,
                    RuntimeRetrievalFeatureDerivationRecommendation = runtimeRetrievalFeatureDerivation?.Report.Recommendation ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationAllowedMode = runtimeRetrievalFeatureDerivation?.Report.AllowedMode ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationSampleCount = runtimeRetrievalFeatureDerivation?.Report.SampleCount ?? 0,
                    RuntimeRetrievalFeatureDerivationTargetSectionMatchRate = runtimeRetrievalFeatureDerivation?.Report.TargetSectionMatchRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRequiredRelationCoverageRate = runtimeRetrievalFeatureDerivation?.Report.RequiredRelationCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationEvidenceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.EvidenceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationSourceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.SourceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivationCompletenessRate = runtimeRetrievalFeatureDerivation?.Report.DerivationCompletenessRate ?? 0,
                    RuntimeRetrievalFeatureDerivationBaselineRecall = runtimeRetrievalFeatureDerivation?.Report.BaselineRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationBaselineMrr = runtimeRetrievalFeatureDerivation?.Report.BaselineMeanReciprocalRank ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedRecall = runtimeRetrievalFeatureDerivation?.Report.DerivedRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedMrr = runtimeRetrievalFeatureDerivation?.Report.DerivedMeanReciprocalRank ?? 0,
                    RuntimeRetrievalFeatureDerivationEvalDrivenRecall = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationEvalDrivenMrr = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenMeanReciprocalRank ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedRecallDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedRecallDelta ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedMrrDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedMrrDelta ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedMustNotHitRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedMustNotHitRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedLifecycleRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedLifecycleRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedSectionMismatchCount = runtimeRetrievalFeatureDerivation?.Report.DerivedSectionMismatchCount ?? 0,
                    RuntimeRetrievalFeatureDerivationForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivation?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                    RuntimeRetrievalFeatureDerivationSourceScanFiles = runtimeRetrievalFeatureDerivation?.Report.SourceScan.ScannedFileCount ?? 0,
                    RuntimeRetrievalFeatureDerivationFixtureTokenHitCount = runtimeRetrievalFeatureDerivation?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                    RuntimeRetrievalFeatureDerivationFormalOutputChanged = runtimeRetrievalFeatureDerivation?.Report.FormalOutputChanged ?? 0,
                    RuntimeRetrievalFeatureDerivationFormalSelectedSetChanged = runtimeRetrievalFeatureDerivation?.Report.FormalSelectedSetChanged ?? false,
                    RuntimeRetrievalFeatureDerivationPackageOutputChanged = runtimeRetrievalFeatureDerivation?.Report.PackageOutputChanged ?? false,
                    RuntimeRetrievalFeatureDerivationPackingPolicyChanged = runtimeRetrievalFeatureDerivation?.Report.PackingPolicyChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRuntimeMutated = runtimeRetrievalFeatureDerivation?.Report.RuntimeMutated ?? false,
                    RuntimeRetrievalFeatureDerivationVectorStoreBindingChanged = runtimeRetrievalFeatureDerivation?.Report.VectorStoreBindingChanged ?? false,
                    RuntimeRetrievalFeatureDerivationBlockedReasons = runtimeRetrievalFeatureDerivation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RuntimeRetrievalFeatureDerivationRepairSourcePath = runtimeRetrievalFeatureDerivationRepair?.SourcePath ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationRepairPassed = runtimeRetrievalFeatureDerivationRepair?.Report.PreviewPassed ?? false,
                    RuntimeRetrievalFeatureDerivationRepairGatePassed = runtimeRetrievalFeatureDerivationRepair?.Report.GatePassed ?? false,
                    RuntimeRetrievalFeatureDerivationRepairRecommendation = runtimeRetrievalFeatureDerivationRepair?.Report.Recommendation ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationRepairAllowedMode = runtimeRetrievalFeatureDerivationRepair?.Report.AllowedMode ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationRepairTrainSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.TrainSampleCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutSampleCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairCanonicalRelationCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalRequiredRelationCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairCanonicalEvidenceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalEvidenceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairCanonicalSourceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalSourceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivationRepair?.Report.DerivedRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivationRepair?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairSourceScanFiles = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.ScannedFileCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairFixtureTokenHitCount = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairFormalOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalOutputChanged ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairFormalSelectedSetChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalSelectedSetChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairPackageOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackageOutputChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairPackingPolicyChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackingPolicyChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairRuntimeMutated = runtimeRetrievalFeatureDerivationRepair?.Report.RuntimeMutated ?? false,
                    RuntimeRetrievalFeatureDerivationRepairVectorStoreBindingChanged = runtimeRetrievalFeatureDerivationRepair?.Report.VectorStoreBindingChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairBlockedReasons = runtimeRetrievalFeatureDerivationRepair?.Report.BlockedReasons ?? Array.Empty<string>(),
                    InputMetadataEnrichmentSourcePath = inputMetadataEnrichment?.SourcePath ?? string.Empty,
                    InputMetadataEnrichmentPreviewPassed = inputMetadataEnrichment?.Snapshot.PreviewPassed ?? false,
                    InputMetadataEnrichmentGatePassed = inputMetadataEnrichment?.Snapshot.GatePassed ?? false,
                    InputMetadataEnrichmentRecommendation = inputMetadataEnrichment?.Snapshot.Recommendation ?? string.Empty,
                    InputMetadataEnrichmentCoverageDelta = inputMetadataEnrichment?.Snapshot.MetadataCoverageDelta ?? 0,
                    InputMetadataEnrichmentBeforeRecall = inputMetadataEnrichment?.Snapshot.BeforeRecall ?? 0,
                    InputMetadataEnrichmentAfterRecall = inputMetadataEnrichment?.Snapshot.AfterRecall ?? 0,
                    InputMetadataEnrichmentIndependentNonDenseSourceCount = inputMetadataEnrichment?.Snapshot.IndependentNonDenseSourceCount ?? 0,
                    InputMetadataEnrichmentRiskAfterPolicy = inputMetadataEnrichment?.Snapshot.RiskAfterPolicy ?? 0,
                    InputMetadataEnrichmentMustNotHitRiskAfterPolicy = inputMetadataEnrichment?.Snapshot.MustNotHitRiskAfterPolicy ?? 0,
                    InputMetadataEnrichmentLifecycleRiskAfterPolicy = inputMetadataEnrichment?.Snapshot.LifecycleRiskAfterPolicy ?? 0,
                    InputMetadataEnrichmentPackageOutputChanged = inputMetadataEnrichment?.Snapshot.PackageOutputChanged ?? false,
                    InputMetadataEnrichmentPackingPolicyChanged = inputMetadataEnrichment?.Snapshot.PackingPolicyChanged ?? false,
                    InputMetadataEnrichmentRuntimeMutated = inputMetadataEnrichment?.Snapshot.RuntimeMutated ?? false,
                    InputMetadataEnrichmentVectorStoreBindingChanged = inputMetadataEnrichment?.Snapshot.VectorStoreBindingChanged ?? false,
                    InputMetadataEnrichmentBlockedReasons = inputMetadataEnrichment?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                    EnrichedCandidateSourceRepairRecheckSourcePath = enrichedCandidateSourceRepairRecheck?.SourcePath ?? string.Empty,
                    EnrichedCandidateSourceRepairRecheckPassed = enrichedCandidateSourceRepairRecheck?.Snapshot.RecheckPassed ?? false,
                    EnrichedCandidateSourceRepairRecheckGatePassed = enrichedCandidateSourceRepairRecheck?.Snapshot.GatePassed ?? false,
                    EnrichedCandidateSourceRepairRecheckRecommendation = enrichedCandidateSourceRepairRecheck?.Snapshot.Recommendation ?? string.Empty,
                    EnrichedCandidateSourceRepairQualityImproved = enrichedCandidateSourceRepairRecheck?.Snapshot.QualityImproved ?? false,
                    EnrichedCandidateSourceRepairTrainRecallDelta = enrichedCandidateSourceRepairRecheck?.Snapshot.TrainDerivedRecallDelta ?? 0,
                    EnrichedCandidateSourceRepairHoldoutRecallDelta = enrichedCandidateSourceRepairRecheck?.Snapshot.HoldoutDerivedRecallDelta ?? 0,
                    EnrichedCandidateSourceRepairMustHitBelowTopKDelta = enrichedCandidateSourceRepairRecheck?.Snapshot.MustHitBelowTopKDelta ?? 0,
                    EnrichedCandidateSourceRepairRiskAfterPolicy = enrichedCandidateSourceRepairRecheck?.Snapshot.RiskAfterPolicy ?? 0,
                    EnrichedCandidateSourceRepairPackageOutputChanged = enrichedCandidateSourceRepairRecheck?.Snapshot.PackageOutputChanged ?? false,
                    EnrichedCandidateSourceRepairPackingPolicyChanged = enrichedCandidateSourceRepairRecheck?.Snapshot.PackingPolicyChanged ?? false,
                    EnrichedCandidateSourceRepairRuntimeMutated = enrichedCandidateSourceRepairRecheck?.Snapshot.RuntimeMutated ?? false,
                    EnrichedCandidateSourceRepairVectorStoreBindingChanged = enrichedCandidateSourceRepairRecheck?.Snapshot.VectorStoreBindingChanged ?? false,
                    EnrichedCandidateSourceRepairBlockedReasons = enrichedCandidateSourceRepairRecheck?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                    EnrichedCandidateSourceRepairQualityBlockedReasons = enrichedCandidateSourceRepairRecheck?.Snapshot.QualityBlockedReasons ?? Array.Empty<string>(),
                    SourceAwareRankingRepairSourcePath = sourceAwareRankingRepair?.SourcePath ?? string.Empty,
                    SourceAwareRankingRepairPassed = sourceAwareRankingRepair?.Snapshot.ReportPassed ?? false,
                    SourceAwareRankingRepairGatePassed = sourceAwareRankingRepair?.Snapshot.GatePassed ?? false,
                    SourceAwareRankingRepairRecommendation = sourceAwareRankingRepair?.Snapshot.Recommendation ?? string.Empty,
                    SourceAwareRankingRepairSelectedProfileId = sourceAwareRankingRepair?.Snapshot.SelectedProfileId ?? string.Empty,
                    SourceAwareRankingRepairTrainDevRecallDelta = sourceAwareRankingRepair?.Snapshot.TrainDevRecallDelta ?? 0,
                    SourceAwareRankingRepairTestRecallDelta = sourceAwareRankingRepair?.Snapshot.TestRecallDelta ?? 0,
                    SourceAwareRankingRepairHoldoutRecallDelta = sourceAwareRankingRepair?.Snapshot.HoldoutRecallDelta ?? 0,
                    SourceAwareRankingRepairBlindHoldoutRecallDelta = sourceAwareRankingRepair?.Snapshot.BlindHoldoutRecallDelta ?? 0,
                    SourceAwareRankingRepairDenseWinnerLostCount = sourceAwareRankingRepair?.Snapshot.DenseWinnerLostCount ?? 0,
                    SourceAwareRankingRepairUniqueSourceRecoveryCount = sourceAwareRankingRepair?.Snapshot.UniqueSourceRecoveryCount ?? 0,
                    SourceAwareRankingRepairSourceNoiseCount = sourceAwareRankingRepair?.Snapshot.SourceNoiseCount ?? 0,
                    SourceAwareRankingRepairFallbackRate = sourceAwareRankingRepair?.Snapshot.FallbackRate ?? 0,
                    SourceAwareRankingRepairRiskAfterPolicy = sourceAwareRankingRepair?.Snapshot.RiskAfterPolicy ?? 0,
                    SourceAwareRankingRepairPackageOutputChanged = sourceAwareRankingRepair?.Snapshot.PackageOutputChanged ?? false,
                    SourceAwareRankingRepairPackingPolicyChanged = sourceAwareRankingRepair?.Snapshot.PackingPolicyChanged ?? false,
                    SourceAwareRankingRepairRuntimeMutated = sourceAwareRankingRepair?.Snapshot.RuntimeMutated ?? false,
                    SourceAwareRankingRepairVectorStoreBindingChanged = sourceAwareRankingRepair?.Snapshot.VectorStoreBindingChanged ?? false,
                    SourceAwareRankingRepairBlockedReasons = sourceAwareRankingRepair?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                    OutputTokenPriorityShadowSourcePath = outputTokenPriorityShadow?.SourcePath ?? string.Empty,
                    OutputTokenPriorityShadowPassed = outputTokenPriorityShadow?.Snapshot.ShadowPassed ?? false,
                    OutputTokenPriorityShadowGatePassed = outputTokenPriorityShadow?.Snapshot.GatePassed ?? false,
                    OutputTokenPriorityShadowRecommendation = outputTokenPriorityShadow?.Snapshot.Recommendation ?? string.Empty,
                    OutputTokenPriorityShadowProfileName = outputTokenPriorityShadow?.Snapshot.ProfileName ?? string.Empty,
                    OutputTokenPriorityShadowTokenDeltaTotal = outputTokenPriorityShadow?.Snapshot.TokenDeltaTotal ?? 0,
                    OutputTokenPriorityShadowTokenDeltaMax = outputTokenPriorityShadow?.Snapshot.TokenDeltaMax ?? 0,
                    OutputTokenPriorityShadowTokenDeltaP95 = outputTokenPriorityShadow?.Snapshot.TokenDeltaP95 ?? 0,
                    OutputTokenPriorityShadowTokenBudgetExceededCount = outputTokenPriorityShadow?.Snapshot.TokenBudgetExceededCount ?? 0,
                    OutputTokenPriorityShadowPriorityInversionCount = outputTokenPriorityShadow?.Snapshot.PriorityInversionCount ?? 0,
                    OutputTokenPriorityShadowDroppedRequiredCandidateCount = outputTokenPriorityShadow?.Snapshot.DroppedRequiredCandidateCount ?? 0,
                    OutputTokenPriorityShadowSectionMismatchCount = outputTokenPriorityShadow?.Snapshot.SectionMismatchCount ?? 0,
                    OutputTokenPriorityShadowRiskAfterPolicy = outputTokenPriorityShadow?.Snapshot.RiskAfterPolicy ?? 0,
                    OutputTokenPriorityShadowFormalSelectedSetChanged = outputTokenPriorityShadow?.Snapshot.FormalSelectedSetChanged ?? false,
                    OutputTokenPriorityShadowPackageOutputChanged = outputTokenPriorityShadow?.Snapshot.PackageOutputChanged ?? false,
                    OutputTokenPriorityShadowPackingPolicyChanged = outputTokenPriorityShadow?.Snapshot.PackingPolicyChanged ?? false,
                    OutputTokenPriorityShadowRuntimeMutated = outputTokenPriorityShadow?.Snapshot.RuntimeMutated ?? false,
                    OutputTokenPriorityShadowVectorStoreBindingChanged = outputTokenPriorityShadow?.Snapshot.VectorStoreBindingChanged ?? false,
                    OutputTokenPriorityShadowBlockedReasons = outputTokenPriorityShadow?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                    FormalAdapterInputContractSourcePath = formalAdapterInputContract?.SourcePath ?? string.Empty,
                    FormalAdapterInputContractPassed = formalAdapterInputContract?.Snapshot.ContractPassed ?? false,
                    FormalAdapterInputContractGatePassed = formalAdapterInputContract?.Snapshot.GatePassed ?? false,
                    FormalAdapterInputContractRecommendation = formalAdapterInputContract?.Snapshot.Recommendation ?? string.Empty,
                    FormalAdapterInputContractVersion = formalAdapterInputContract?.Snapshot.ContractVersion ?? string.Empty,
                    FormalAdapterInputContractRuntimeInputFieldCount = formalAdapterInputContract?.Snapshot.RuntimeInputFieldCount ?? 0,
                    FormalAdapterInputContractDeniedFieldCount = formalAdapterInputContract?.Snapshot.DeniedFieldCount ?? 0,
                    FormalAdapterInputContractForbiddenPropertyCount = formalAdapterInputContract?.Snapshot.ContractForbiddenPropertyCount ?? 0,
                    FormalAdapterInputContractFormalSourceForbiddenReadCount = formalAdapterInputContract?.Snapshot.FormalSourceForbiddenReadCount ?? 0,
                    FormalAdapterInputContractEvalOnlyForbiddenReadCount = formalAdapterInputContract?.Snapshot.EvalOnlyForbiddenReadCount ?? 0,
                    FormalAdapterInputContractDatasetEvalFieldsBlocked = formalAdapterInputContract?.Snapshot.DatasetEvalFieldsBlocked ?? false,
                    FormalAdapterInputContractGoldLabelsBlocked = formalAdapterInputContract?.Snapshot.GoldLabelsBlocked ?? false,
                    FormalAdapterInputContractSampleMetadataBlocked = formalAdapterInputContract?.Snapshot.SampleMetadataBlocked ?? false,
                    FormalAdapterInputContractShadowArtifactFieldsBlocked = formalAdapterInputContract?.Snapshot.ShadowArtifactFieldsBlocked ?? false,
                    FormalAdapterInputContractFormalRetrievalAllowed = formalAdapterInputContract?.Snapshot.FormalRetrievalAllowed ?? false,
                    FormalAdapterInputContractRuntimeSwitchAllowed = formalAdapterInputContract?.Snapshot.RuntimeSwitchAllowed ?? false,
                    FormalAdapterInputContractRuntimeMutated = formalAdapterInputContract?.Snapshot.RuntimeMutated ?? false,
                    FormalAdapterInputContractPackageOutputChanged = formalAdapterInputContract?.Snapshot.PackageOutputChanged ?? false,
                    FormalAdapterInputContractPackingPolicyChanged = formalAdapterInputContract?.Snapshot.PackingPolicyChanged ?? false,
                    FormalAdapterInputContractVectorStoreBindingChanged = formalAdapterInputContract?.Snapshot.VectorStoreBindingChanged ?? false,
                    FormalAdapterInputContractBlockedReasons = formalAdapterInputContract?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                    RetrievalEvalProtocolGateSourcePath = retrievalEvalProtocol?.GateSourcePath ?? string.Empty,
                    RetrievalEvalProtocolSourceAuditPath = retrievalEvalProtocol?.SourceAuditPath ?? string.Empty,
                    RetrievalEvalProtocolGatePassed = retrievalEvalProtocol?.Gate?.GatePassed ?? false,
                    RetrievalEvalProtocolRecommendation = retrievalEvalProtocol?.Gate?.Recommendation ?? string.Empty,
                    RetrievalEvalProtocolVersion = retrievalEvalProtocol?.Gate?.ProtocolVersion ?? string.Empty,
                    RetrievalEvalProtocolVectorTopK = retrievalEvalProtocol?.Gate?.VectorTopK ?? 0,
                    RetrievalEvalProtocolMergedTopK = retrievalEvalProtocol?.Gate?.MergedTopK ?? 0,
                    RetrievalEvalProtocolFinalTopK = retrievalEvalProtocol?.Gate?.FinalTopK ?? 0,
                    RetrievalEvalProtocolHashOrderSensitivityCount = retrievalEvalProtocol?.Gate?.HashOrderSensitivityCount ?? 0,
                    RetrievalEvalProtocolTieBreakDeterministic = retrievalEvalProtocol?.Gate?.TieBreakDeterministic ?? false,
                    RetrievalEvalProtocolSourceNonDiscriminativeDetected = retrievalEvalProtocol?.Gate?.SourceNonDiscriminativeDetected ?? false,
                    RetrievalEvalProtocolTemplateHomogeneityDetected = retrievalEvalProtocol?.Gate?.TemplateHomogeneityDetected ?? false,
                    RetrievalEvalProtocolRuntimeChangeGatePassed = retrievalEvalProtocol?.Gate?.RuntimeChangeGatePassed ?? false,
                    RetrievalEvalProtocolRiskAfterPolicy = retrievalEvalProtocol?.Gate?.RiskAfterPolicy ?? 0,
                    RetrievalEvalProtocolMustNotHitRiskAfterPolicy = retrievalEvalProtocol?.Gate?.MustNotHitRiskAfterPolicy ?? 0,
                    RetrievalEvalProtocolLifecycleRiskAfterPolicy = retrievalEvalProtocol?.Gate?.LifecycleRiskAfterPolicy ?? 0,
                    RetrievalEvalProtocolNonDiscriminativeSourceCount = retrievalEvalProtocol?.SourceAudit?.NonDiscriminativeSourceCount ?? 0,
                    RetrievalEvalProtocolTemplateHomogeneityScore = retrievalEvalProtocol?.SourceAudit?.TemplateHomogeneityScore ?? 0,
                    RetrievalEvalProtocolBaselineRecall = retrievalEvalProtocol?.SourceAudit?.BaselineRecall ?? 0,
                    RetrievalEvalProtocolMergedRecall = retrievalEvalProtocol?.SourceAudit?.MergedRecall ?? 0,
                    RetrievalEvalProtocolBlockedReasons = retrievalEvalProtocol?.Gate?.BlockedReasons ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationFreezeSourcePath = formalRetrievalIntegrationFreeze?.SourcePath ?? string.Empty,
                    FormalRetrievalIntegrationFreezePassed = formalRetrievalIntegrationFreeze?.Snapshot.FreezePassed ?? false,
                    FormalRetrievalIntegrationFreezeRecommendation = formalRetrievalIntegrationFreeze?.Snapshot.Recommendation ?? string.Empty,
                    FormalRetrievalIntegrationFreezeSelectedProfile = formalRetrievalIntegrationFreeze?.Snapshot.SelectedProfile ?? string.Empty,
                    FormalRetrievalIntegrationFreezeFrozenArtifactCount = formalRetrievalIntegrationFreeze?.Snapshot.FrozenArtifactCount ?? 0,
                    V4GateSatisfied = readinessGate?.Report.Passed ?? IsVectorV4GateSatisfied(recallLoss.A3, recallLoss.Extended)
                };
            }
            catch (JsonException)
            {
                return new ServiceVectorShadowQualitySummary
                {
                    Available = false,
                    SourcePath = path,
                    CurrentRecommendation = "InvalidReport"
                };
            }
        }

        var residualOnly = TryLoadVectorResidualRiskReport();
        if (residualOnly is not null)
        {
            var lifecycleCoverage = TryLoadVectorLifecycleMetadataCoverageReport();
            var lifecycleBackfill = TryLoadVectorLifecycleMetadataBackfillPlan();
            var recallLoss = TryLoadVectorRecallLossReports();
            var safeRecovery = TryLoadVectorSafeRecallRecoveryReports();
            var fusionShadow = TryLoadVectorRankerFusionShadowReports();
            var representation = TryLoadVectorRepresentationBenchmarkReports();
            var queryExpansion = TryLoadVectorQueryExpansionShadowReports();
            var readinessGate = TryLoadVectorReadinessGateReport();
            var providerComparison = TryLoadVectorProviderComparisonReport();
            var qwen3ReadinessGate = TryLoadVectorQwen3ReadinessGateReport();
            var providerComparisonFreeze = TryLoadEmbeddingProviderComparisonFreezeReport();
            var hybridPreview = TryLoadVectorHybridPreviewReport();
            var hybridGate = TryLoadVectorHybridReadinessGateReport();
            var hybridAudit = TryLoadVectorHybridRecallRegressionAuditReport();
            var hybridFreeze = TryLoadVectorHybridFreezeReport();
            var alignmentAudit = TryLoadVectorRetrievalDatasetAlignmentAuditSummaryReport();
            var eligibilityTriage = TryLoadVectorEligibilityRecallLossTriageSummaryReport();
            var lifecycleRepairPlan = TryLoadVectorLifecycleMetadataRepairPlanSummaryReport();
            var lifecycleReviewCandidates = TryLoadVectorLifecycleMetadataReviewCandidateReport();
            var lifecycleReviewSummary = TryLoadVectorLifecycleMetadataReviewSummaryReport();
            var lifecycleSidecarPreview = TryLoadVectorLifecycleMetadataSidecarPreviewReport();
            var sidecarEligibility = TryLoadVectorSidecarEligibilityQualityReport();
            var reviewBatch = TryLoadVectorLifecycleMetadataReviewBatchSummary();
            var evidenceBackfill = TryLoadVectorLifecycleMetadataEvidenceBackfillReport();
            var datasetV2Generation = TryLoadRetrievalDatasetV2GenerationSummary();
            var datasetV2Materialization = TryLoadRetrievalDatasetV2MaterializationSummary();
            var datasetV2ShadowEval = TryLoadRetrievalDatasetV2ShadowEvalSummary();
            var datasetV2Stress = TryLoadRetrievalDatasetV2StressSummary();
            var datasetV2StressTriage = TryLoadRetrievalDatasetV2StressFailureTriageSummary();
            var datasetV2HybridRepair = TryLoadRetrievalDatasetV2HybridScoringRepairSummary();
            var datasetV2HybridRiskTriage = TryLoadRetrievalDatasetV2HybridScoringRiskTriageSummary();
            var datasetV2StressFreeze = TryLoadRetrievalDatasetV2StressFreezeSummary();
            var vectorV4Recheck = TryLoadVectorV4ReadinessRecheckSummary();
            var shadowPackageComparison = TryLoadVectorShadowPackageComparisonSummary();
                var formalRetrievalIntegrationPlan = TryLoadFormalRetrievalIntegrationPlanSummary();
                var formalRetrievalIntegrationDecision = TryLoadFormalRetrievalIntegrationDecisionSummary();
                var shadowFormalRetrievalAdapterPlan = TryLoadShadowFormalRetrievalAdapterPlanSummary();
            var shadowFormalRetrievalAdapter = TryLoadShadowFormalRetrievalAdapterSummary();
            var formalAdapterPackageShadowComparison = TryLoadFormalAdapterPackageShadowComparisonSummary();
            var graphVectorRetrievalQualityAudit = TryLoadGraphVectorRetrievalQualityAuditSummary();
            var retrievalQualityRepairPreview = TryLoadRetrievalQualityRepairPreviewSummary();
            var runtimeObservableFeatureContract = TryLoadRuntimeObservableFeatureContractSummary();
            var runtimeRetrievalFeatureDerivation = TryLoadRuntimeRetrievalFeatureDerivationSummary();
            var runtimeRetrievalFeatureDerivationRepair = TryLoadRuntimeRetrievalFeatureDerivationRepairSummary();
            var featureDerivationFailureFreeze = TryLoadRuntimeFeatureDerivationFailureFreezeSummary();
            var graphHubNoiseControl = TryLoadGraphHubNoiseControlSummary();
            var retrievalEvalProtocol = TryLoadRetrievalEvalProtocolSummary();
            var inputMetadataEnrichment = TryLoadInputMetadataEnrichmentSummary();
            var enrichedCandidateSourceRepairRecheck = TryLoadEnrichedCandidateSourceRepairRecheckSummary();
            var sourceAwareRankingRepair = TryLoadSourceAwareRankingRepairSummary();
            var outputTokenPriorityShadow = TryLoadOutputTokenPriorityShadowSummary();
            var formalAdapterInputContract = TryLoadFormalAdapterInputContractSummary();
                var formalRetrievalIntegrationFreeze = TryLoadFormalRetrievalIntegrationFreezeSummary();
            return new ServiceVectorShadowQualitySummary
            {
                Available = true,
                SourcePath = residualOnly.Value.SourcePath,
                CurrentRecommendation = residualOnly.Value.Report.Recommendation,
                ResidualRiskSourcePath = residualOnly.Value.SourcePath,
                ResidualRiskCount = residualOnly.Value.Report.ResidualRiskCount,
                TopResidualRiskTypes = residualOnly.Value.Report.RiskAfterPolicyByType,
                TopWhyPolicyAllowed = residualOnly.Value.Report.Risks
                    .Select(item => item.WhyPolicyAllowed)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray(),
                TopExpectedActions = residualOnly.Value.Report.Risks
                    .Select(item => item.ExpectedAction)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray(),
                LifecycleMetadataCoverageSourcePath = lifecycleCoverage?.SourcePath ?? string.Empty,
                LifecycleMetadataCoverageRate = lifecycleCoverage?.Report.LifecycleCoverageRate ?? 0,
                UnknownLifecycleCount = lifecycleCoverage?.Report.UnknownLifecycleCount ?? 0,
                MissingReviewStatusCount = lifecycleCoverage?.Report.MissingReviewStatusCount ?? 0,
                MissingReplacementInfoCount = lifecycleCoverage?.Report.MissingReplacementInfoCount ?? 0,
                BlockedByLifecycleMetadataGate = residualOnly.Value.Report.BlockedByLifecycleMetadataGate,
                LifecycleBackfillPlanSourcePath = lifecycleBackfill?.SourcePath ?? string.Empty,
                BackfillUnknownLifecycleBefore = lifecycleBackfill?.Plan.UnknownLifecycleBefore ?? 0,
                BackfillAutoResolvableCount = lifecycleBackfill?.Plan.AutoResolvableCount ?? 0,
                BackfillManualReviewRequiredCount = lifecycleBackfill?.Plan.ManualReviewRequiredCount ?? 0,
                BackfillExpectedCoverageAfter = lifecycleBackfill?.Plan.ExpectedCoverageAfter ?? 0,
                RecallLossA3SourcePath = recallLoss.A3SourcePath,
                RecallLossExtendedSourcePath = recallLoss.ExtendedSourcePath,
                A3RecallAfterPolicy = recallLoss.A3?.MustHitRecallAfterPolicy ?? 0,
                ExtendedRecallAfterPolicy = recallLoss.Extended?.MustHitRecallAfterPolicy ?? 0,
                A3RecallRecommendation = recallLoss.A3?.Recommendation ?? string.Empty,
                ExtendedRecallRecommendation = recallLoss.Extended?.Recommendation ?? string.Empty,
                TopRecallMissReasons = MergeMissReasons(recallLoss.A3, recallLoss.Extended),
                IntentReadinessRecommendations = BuildIntentReadinessSummary(recallLoss.A3, recallLoss.Extended),
                SafeRecallRecoveryA3SourcePath = safeRecovery.A3SourcePath,
                SafeRecallRecoveryExtendedSourcePath = safeRecovery.ExtendedSourcePath,
                SafeRecoveryA3RecallAfterPolicy = safeRecovery.A3?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                SafeRecoveryExtendedRecallAfterPolicy = safeRecovery.Extended?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                SafeRecoveryA3BestConfiguration = safeRecovery.A3?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                SafeRecoveryExtendedBestConfiguration = safeRecovery.Extended?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                SafeRecoveryA3RecoveredBelowTopK = safeRecovery.A3?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                SafeRecoveryExtendedRecoveredBelowTopK = safeRecovery.Extended?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                BlockedMustHitClassificationCounts = MergeBlockedMustHitClassifications(safeRecovery.A3, safeRecovery.Extended),
                FusionShadowA3SourcePath = fusionShadow.A3SourcePath,
                FusionShadowExtendedSourcePath = fusionShadow.ExtendedSourcePath,
                FusionBestStrategy = SelectFusionBestStrategy(fusionShadow.A3, fusionShadow.Extended),
                FusionA3RecallAfterPolicy = fusionShadow.A3?.BestResult?.MustHitRecallFusion ?? 0,
                FusionExtendedRecallAfterPolicy = fusionShadow.Extended?.BestResult?.MustHitRecallFusion ?? 0,
                FusionRiskAfterPolicy = BuildFusionRiskSummary(fusionShadow.A3, fusionShadow.Extended),
                FusionRecallGain = BuildFusionRecallGainSummary(fusionShadow.A3, fusionShadow.Extended),
                FusionReadinessGateSatisfied = IsFusionReadinessSatisfied(fusionShadow.A3, fusionShadow.Extended),
                RepresentationBenchmarkA3SourcePath = representation.A3SourcePath,
                RepresentationBenchmarkExtendedSourcePath = representation.ExtendedSourcePath,
                RepresentationBestDocumentProfile = SelectRepresentationBestDocumentProfile(representation.A3, representation.Extended),
                RepresentationBestQueryProfile = SelectRepresentationBestQueryProfile(representation.A3, representation.Extended),
                RepresentationA3RecallAfterPolicy = representation.A3?.BestResult?.Recall ?? 0,
                RepresentationExtendedRecallAfterPolicy = representation.Extended?.BestResult?.Recall ?? 0,
                RepresentationRiskAfterPolicy = BuildRepresentationRiskSummary(representation.A3, representation.Extended),
                RepresentationRecoveredMissCount = BuildRepresentationRecoveredMissSummary(representation.A3, representation.Extended),
                RepresentationV4GateSatisfied = IsRepresentationReadinessSatisfied(representation.A3, representation.Extended),
                QueryExpansionShadowA3SourcePath = queryExpansion.A3SourcePath,
                QueryExpansionShadowExtendedSourcePath = queryExpansion.ExtendedSourcePath,
                QueryExpansionBestProfile = SelectQueryExpansionBestProfile(queryExpansion.A3, queryExpansion.Extended),
                QueryExpansionA3RecallBefore = queryExpansion.A3?.BestResult?.RecallBeforeExpansion ?? 0,
                QueryExpansionA3RecallAfter = queryExpansion.A3?.BestResult?.RecallAfterExpansion ?? 0,
                QueryExpansionExtendedRecallBefore = queryExpansion.Extended?.BestResult?.RecallBeforeExpansion ?? 0,
                QueryExpansionExtendedRecallAfter = queryExpansion.Extended?.BestResult?.RecallAfterExpansion ?? 0,
                QueryExpansionRecoveredMissCount = BuildQueryExpansionRecoveredMissSummary(queryExpansion.A3, queryExpansion.Extended),
                QueryExpansionRiskAfterPolicy = BuildQueryExpansionRiskSummary(queryExpansion.A3, queryExpansion.Extended),
                QueryExpansionV4GateSatisfied = IsQueryExpansionReadinessSatisfied(queryExpansion.A3, queryExpansion.Extended),
                V4ReadinessGateSourcePath = readinessGate?.SourcePath ?? string.Empty,
                V4ReadinessGatePassed = readinessGate?.Report.Passed ?? false,
                                V4ReadinessGateFailReasons = readinessGate?.Report.FailReasons ?? Array.Empty<string>(),
                ProviderComparisonSourcePath = providerComparison?.SourcePath ?? string.Empty,
                ProviderComparisonResults = providerComparison?.Report.Providers ?? Array.Empty<VectorProviderComparisonV310Result>(),
                Qwen3ReadinessGateSourcePath = qwen3ReadinessGate?.SourcePath ?? string.Empty,
                Qwen3ReadinessGatePassed = qwen3ReadinessGate?.Report.Passed ?? false,
                Qwen3Recommendation = qwen3ReadinessGate?.Report.Recommendation ?? string.Empty,
                Qwen3BlockedReasons = qwen3ReadinessGate?.Report.BlockedReasons ?? Array.Empty<string>(),
                ProviderComparisonFreezeSourcePath = providerComparisonFreeze?.SourcePath ?? string.Empty,
                ProviderPromotionStatus = providerComparisonFreeze?.Report.PromotionStatus ?? string.Empty,
                ProviderConfigurationSanityPassed = false,
                ProviderComparisonStatus = (providerComparisonFreeze?.Report.Passed ?? false) ? "Conclusive" : (providerComparisonFreeze is not null ? "Inconclusive" : string.Empty),
                VectorV4RecheckAllowed = providerComparisonFreeze?.Report.VectorV4RecheckAllowed ?? false,
                ProviderPromotionBlockedReasons = providerComparisonFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                HybridPreviewSourcePath = hybridPreview?.SourcePath ?? string.Empty,
                HybridFullA3Recall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                HybridFullExtendedRecall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                HybridFullRiskAfterPolicy = Math.Max(hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0, hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0),
                HybridReadinessRecommendation = hybridPreview?.Report.Recommendation ?? string.Empty,
                HybridReadinessGatePassed = hybridGate?.Report.Passed ?? false,
                HybridAuditSourcePath = hybridAudit?.SourcePath ?? string.Empty,
                HybridAuditPassed = hybridAudit?.Report.Passed ?? false,
                HybridAuditRecommendation = hybridAudit?.Report.Recommendation ?? string.Empty,
                HybridAuditDenseDroppedCount = hybridAudit?.Report.DenseCandidateDroppedCount ?? 0,
                HybridAuditEligibilityMismatchCount = hybridAudit?.Report.EligibilityMismatchCount ?? 0,
                HybridAuditDedupOverwriteCount = hybridAudit?.Report.DedupOverwriteCount ?? 0,
                HybridFreezeSourcePath = hybridFreeze?.SourcePath ?? string.Empty,
                HybridFreezePassed = hybridFreeze?.Report.FreezePassed ?? false,
                HybridFreezeStatus = hybridFreeze?.Report.HybridRetrievalStatus ?? string.Empty,
                HybridFreezeRecommendation = hybridFreeze?.Report.Recommendation ?? string.Empty,
                HybridV4RecheckAllowed = hybridFreeze?.Report.V4RecheckAllowed ?? false,
                HybridFreezeBlockedReasons = hybridFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                DatasetAlignmentAuditSourcePath = alignmentAudit?.SourcePath ?? string.Empty,
                DatasetAlignmentRecommendation = alignmentAudit?.Report.Recommendation ?? string.Empty,
                DatasetAlignmentIssueCount = alignmentAudit?.Report.AlignmentIssueCount ?? 0,
                DatasetAlignmentA3MustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: false),
                DatasetAlignmentExtendedMustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: false),
                DatasetAlignmentA3ProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: true),
                DatasetAlignmentExtendedProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: true),
                DatasetAlignmentEligibilityBlockCount = alignmentAudit?.Report.Reports.Sum(item => item.MustHitBlockedByEligibilityCount) ?? 0,
                DatasetAlignmentAnchorCoverageRate = ResolveAlignmentAnchorCoverage(alignmentAudit?.Report),
                DatasetAlignmentTopIssues = alignmentAudit?.Report.IssueBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                EligibilityRecallLossTriageSourcePath = eligibilityTriage?.SourcePath ?? string.Empty,
                EligibilityFilteredMustHitCount = eligibilityTriage?.Report.TotalFilteredMustHit ?? 0,
                EligibilityCorrectlyBlockedCount = eligibilityTriage?.Report.CorrectlyBlockedCount ?? 0,
                EligibilityRouteToHistoricalCount = eligibilityTriage?.Report.RouteToHistoricalCount ?? 0,
                EligibilityRouteToAuditCount = eligibilityTriage?.Report.RouteToAuditCount ?? 0,
                EligibilityMetadataRepairNeededCount = eligibilityTriage?.Report.MetadataRepairNeededCount ?? 0,
                EligibilityEvalExpectationReviewNeededCount = eligibilityTriage?.Report.EvalExpectationReviewNeededCount ?? 0,
                EligibilityUnsafeToRecoverCount = eligibilityTriage?.Report.UnsafeToRecoverCount ?? 0,
                EligibilityRecallLossRecommendation = eligibilityTriage?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataRepairPlanSourcePath = lifecycleRepairPlan?.SourcePath ?? string.Empty,
                LifecycleMetadataRepairCandidateCount = lifecycleRepairPlan?.Report.CandidateCount ?? 0,
                LifecycleMetadataRepairAutoRepairableCount = lifecycleRepairPlan?.Report.AutoRepairableCount ?? 0,
                LifecycleMetadataRepairHumanReviewRequiredCount = lifecycleRepairPlan?.Report.HumanReviewRequiredCount ?? 0,
                LifecycleMetadataRepairForbiddenCount = lifecycleRepairPlan?.Report.ForbiddenRepairCount ?? 0,
                LifecycleMetadataRepairEstimatedRecallRecovery = lifecycleRepairPlan?.Report.EstimatedRecallRecovery ?? 0,
                LifecycleMetadataRepairRiskEstimate = lifecycleRepairPlan?.Report.RiskAfterRepairEstimate ?? 0,
                LifecycleMetadataRepairRecommendation = lifecycleRepairPlan?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataReviewCandidatesSourcePath = lifecycleReviewCandidates?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewCandidateCount = lifecycleReviewCandidates?.Report.CandidateCount ?? 0,
                LifecycleMetadataReviewPendingCount = lifecycleReviewCandidates?.Report.PendingCount ?? 0,
                LifecycleMetadataReviewCorrectlyBlockedSkippedCount = lifecycleReviewCandidates?.Report.CorrectlyBlockedSkippedCount ?? 0,
                LifecycleMetadataReviewCountByLayer = lifecycleReviewCandidates?.Report.CountByLayer ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                LifecycleMetadataReviewCountByItemKind = lifecycleReviewCandidates?.Report.CountByItemKind ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                LifecycleMetadataReviewRecentCandidates = lifecycleReviewCandidates?.Report.RecentCandidates ?? Array.Empty<VectorLifecycleMetadataReviewCandidate>(),
                LifecycleMetadataReviewRecommendation = lifecycleReviewCandidates?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataReviewSummarySourcePath = lifecycleReviewSummary?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewApprovedForSidecarCount = lifecycleReviewSummary?.Report.ApprovedForSidecarCount ?? 0,
                LifecycleMetadataReviewRejectedCount = lifecycleReviewSummary?.Report.RejectedCount ?? 0,
                LifecycleMetadataReviewNeedsEvidenceCount = lifecycleReviewSummary?.Report.NeedsEvidenceCount ?? 0,
                LifecycleMetadataReviewSupersededCount = lifecycleReviewSummary?.Report.SupersededCount ?? 0,
                LifecycleMetadataReviewSidecarEntryCount = lifecycleReviewSummary?.Report.SidecarEntryCount ?? lifecycleSidecarPreview?.Report.SidecarEntryCount ?? 0,
                LifecycleMetadataReviewUnsafeApprovalBlockedCount = lifecycleReviewSummary?.Report.UnsafeApprovalBlockedCount ?? 0,
                LifecycleMetadataReviewSidecarPreviewSourcePath = lifecycleSidecarPreview?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewNormalContextApprovalCount = lifecycleReviewSummary?.Report.NormalContextApprovalCount ?? lifecycleSidecarPreview?.Report.NormalContextEntryCount ?? 0,
                LifecycleMetadataReviewAuditContextApprovalCount = lifecycleReviewSummary?.Report.AuditContextApprovalCount ?? lifecycleSidecarPreview?.Report.AuditContextEntryCount ?? 0,
                LifecycleMetadataReviewHistoricalContextApprovalCount = lifecycleReviewSummary?.Report.HistoricalContextApprovalCount ?? lifecycleSidecarPreview?.Report.HistoricalContextEntryCount ?? 0,
                LifecycleMetadataReviewDiagnosticsOnlyApprovalCount = lifecycleReviewSummary?.Report.DiagnosticsOnlyApprovalCount ?? lifecycleSidecarPreview?.Report.DiagnosticsOnlyEntryCount ?? 0,
                SidecarEligibilityPreviewSourcePath = sidecarEligibility?.SourcePath ?? string.Empty,
                SidecarEligibilityCandidateCount = sidecarEligibility?.Report.CandidateCount ?? 0,
                SidecarEligibilitySidecarEntryCount = sidecarEligibility?.Report.SidecarEntryCount ?? 0,
                SidecarEligibilityApprovedSidecarCount = sidecarEligibility?.Report.ApprovedSidecarCount ?? 0,
                SidecarEligibilityPendingReviewCount = sidecarEligibility?.Report.PendingReviewCount ?? 0,
                SidecarEligibilityEffectiveMetadataChangedCount = sidecarEligibility?.Report.EffectiveMetadataChangedCount ?? 0,
                SidecarEligibilityUnsafeBlockedCount = sidecarEligibility?.Report.UnsafeSidecarBlockedCount ?? 0,
                SidecarEligibilityConflictBlockedCount = sidecarEligibility?.Report.ConflictSidecarBlockedCount ?? 0,
                SidecarEligibilitySourceItemUnchanged = sidecarEligibility?.Report.SourceItemUnchanged ?? true,
                SidecarEligibilityRecommendation = sidecarEligibility?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataReviewBatchSourcePath = reviewBatch?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewBatchId = reviewBatch?.Batch.BatchId ?? string.Empty,
                LifecycleMetadataReviewBatchStatus = reviewBatch?.Batch.Status ?? string.Empty,
                LifecycleMetadataReviewBatchCandidateCount = reviewBatch?.Batch.CandidateCount ?? 0,
                LifecycleMetadataReviewBatchValidationErrorCount = reviewBatch?.Validation?.ValidationErrorCount ?? 0,
                LifecycleMetadataReviewBatchWouldWriteSidecarCount = reviewBatch?.ApplyPreview?.WouldWriteSidecarEntryCount ?? 0,
                LifecycleMetadataReviewBatchUnsafeBlockedCount = reviewBatch?.ApplyPreview?.UnsafeBlockedCount ?? reviewBatch?.Validation?.UnsafeDecisionCount ?? 0,
                LifecycleMetadataReviewBatchRecommendation = reviewBatch?.ApplyPreview?.Recommendation ?? reviewBatch?.Validation?.Recommendation ?? (reviewBatch is null ? string.Empty : "ReadyForManualReview"),
                LifecycleMetadataEvidenceBackfillSourcePath = evidenceBackfill?.SourcePath ?? string.Empty,
                LifecycleMetadataEvidenceBackfillCandidateCount = evidenceBackfill?.Report.CandidateCount ?? 0,
                LifecycleMetadataEvidenceFoundCount = evidenceBackfill?.Report.EvidenceFoundCount ?? 0,
                LifecycleMetadataSourceRefFoundCount = evidenceBackfill?.Report.SourceRefFoundCount ?? 0,
                LifecycleMetadataProvenanceFoundCount = evidenceBackfill?.Report.ProvenanceFoundCount ?? 0,
                LifecycleMetadataAutoRepairableAfterBackfillCount = evidenceBackfill?.Report.AutoRepairableAfterBackfillCount ?? 0,
                LifecycleMetadataNeedsEvidenceAfterBackfillCount = evidenceBackfill?.Report.NeedsEvidenceCount ?? 0,
                LifecycleMetadataEvidenceBackfillRecommendation = evidenceBackfill?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2GenerationSourcePath = datasetV2Generation?.SourcePath ?? string.Empty,
                RetrievalDatasetV2CorpusItemCount = datasetV2Generation?.CorpusItemCount ?? 0,
                RetrievalDatasetV2SampleCount = datasetV2Generation?.SampleCount ?? 0,
                RetrievalDatasetV2ValidationIssueCount = datasetV2Generation?.ValidationIssueCount ?? 0,
                RetrievalDatasetV2MissingEvidenceCount = datasetV2Generation?.MissingEvidenceCount ?? 0,
                RetrievalDatasetV2MissingProvenanceCount = datasetV2Generation?.MissingProvenanceCount ?? 0,
                RetrievalDatasetV2DifficultyBreakdown = datasetV2Generation?.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2SplitBreakdown = datasetV2Generation?.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2Recommendation = datasetV2Generation?.Recommendation ?? string.Empty,
                RetrievalDatasetV2MaterializationSourcePath = datasetV2Materialization?.SourcePath ?? string.Empty,
                RetrievalDatasetV2DatasetId = datasetV2Materialization?.Report.DatasetId ?? string.Empty,
                RetrievalDatasetV2CorpusHash = datasetV2Materialization?.Report.CorpusHash ?? string.Empty,
                RetrievalDatasetV2SamplesHash = datasetV2Materialization?.Report.SamplesHash ?? string.Empty,
                RetrievalDatasetV2MaterializationGatePassed = datasetV2Materialization?.Report.GatePassed ?? false,
                RetrievalDatasetV2MaterializationCorpusHashStable = datasetV2Materialization?.Report.CorpusHashStable ?? false,
                RetrievalDatasetV2MaterializationSamplesHashStable = datasetV2Materialization?.Report.SamplesHashStable ?? false,
                RetrievalDatasetV2MaterializationRecommendation = datasetV2Materialization?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2ShadowEvalSourcePath = datasetV2ShadowEval?.SourcePath ?? string.Empty,
                RetrievalDatasetV2ShadowEvalDatasetId = datasetV2ShadowEval?.Summary.DatasetId ?? string.Empty,
                RetrievalDatasetV2ShadowEvalBestProfileName = datasetV2ShadowEval?.Summary.BestProfileName ?? string.Empty,
                RetrievalDatasetV2ShadowEvalBestRecallAfterPolicy = datasetV2ShadowEval?.Summary.BestRecallAfterPolicy ?? 0,
                RetrievalDatasetV2ShadowEvalBestMrrAfterPolicy = datasetV2ShadowEval?.Summary.BestMrrAfterPolicy ?? 0,
                RetrievalDatasetV2ShadowEvalBestRiskAfterPolicy = datasetV2ShadowEval?.Summary.BestRiskAfterPolicy ?? 0,
                RetrievalDatasetV2ShadowEvalPgVectorParityPassed = datasetV2ShadowEval?.Summary.PgVectorParityPassed ?? false,
                RetrievalDatasetV2ShadowEvalRecommendation = datasetV2ShadowEval?.Gate?.Recommendation ?? datasetV2ShadowEval?.Summary.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressSourcePath = datasetV2Stress?.SourcePath ?? string.Empty,
                RetrievalDatasetV2StressDatasetId = datasetV2Stress?.Report.DatasetId ?? string.Empty,
                RetrievalDatasetV2StressCorpusItemCount = datasetV2Stress?.Report.CorpusItemCount ?? 0,
                RetrievalDatasetV2StressSampleCount = datasetV2Stress?.Report.SampleCount ?? 0,
                RetrievalDatasetV2StressSplitBreakdown = datasetV2Stress?.Report.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressDifficultyBreakdown = datasetV2Stress?.Report.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressLeakageIssueCount = datasetV2Stress?.Report.LeakageIssueCount ?? 0,
                RetrievalDatasetV2StressAnchorDominanceScore = datasetV2Stress?.Report.AnchorDominanceScore ?? 0,
                RetrievalDatasetV2StressDenseRecall = datasetV2Stress?.Report.DenseRecall ?? 0,
                RetrievalDatasetV2StressLexicalRecall = datasetV2Stress?.Report.LexicalRecall ?? 0,
                RetrievalDatasetV2StressAnchorRecall = datasetV2Stress?.Report.AnchorRecall ?? 0,
                RetrievalDatasetV2StressHybridRecall = datasetV2Stress?.Report.HybridRecall ?? 0,
                RetrievalDatasetV2StressHoldoutHybridRecall = datasetV2Stress?.Report.HoldoutHybridRecall ?? 0,
                RetrievalDatasetV2StressRecommendation = datasetV2Stress?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressTriageSourcePath = datasetV2StressTriage?.SourcePath ?? string.Empty,
                RetrievalDatasetV2StressFailureCount = datasetV2StressTriage?.Report.FailureCount ?? 0,
                RetrievalDatasetV2StressHoldoutFailureCount = datasetV2StressTriage?.Report.HoldoutFailureCount ?? 0,
                RetrievalDatasetV2StressFailureCountBySplit = datasetV2StressTriage?.Report.FailureCountBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressFailureCountByDifficulty = datasetV2StressTriage?.Report.FailureCountByDifficulty ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressFailureCountByReason = datasetV2StressTriage?.Report.FailureCountByReason ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressDenseOnlyWinCount = datasetV2StressTriage?.Report.DenseOnlyWinCount ?? 0,
                RetrievalDatasetV2StressHybridWinCount = datasetV2StressTriage?.Report.HybridWinCount ?? 0,
                RetrievalDatasetV2StressAnchorRegressionCount = datasetV2StressTriage?.Report.AnchorRegressionCount ?? 0,
                RetrievalDatasetV2StressProfileComparisonSummary = FormatDatasetV2StressProfileComparisons(datasetV2StressTriage?.Report),
                RetrievalDatasetV2StressTriageRecommendation = datasetV2StressTriage?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2HybridRepairSourcePath = datasetV2HybridRepair?.SourcePath ?? string.Empty,
                RetrievalDatasetV2HybridRepairBestProfileName = datasetV2HybridRepair?.BestProfile?.ProfileName ?? datasetV2HybridRepair?.Report.BestProfileName ?? string.Empty,
                RetrievalDatasetV2HybridRepairRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.RecallAfterPolicy ?? 0,
                RetrievalDatasetV2HybridRepairHoldoutRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.HoldoutRecallAfterPolicy ?? 0,
                RetrievalDatasetV2HybridRepairDenseWinnerLostCount = datasetV2HybridRepair?.BestProfile?.DenseWinnerLostCount ?? 0,
                RetrievalDatasetV2HybridRepairMustHitBelowTopKCount = datasetV2HybridRepair?.BestProfile?.MustHitBelowTopKCount ?? 0,
                RetrievalDatasetV2HybridRepairNegativeDistractorCount = datasetV2HybridRepair?.BestProfile?.NegativeDistractorOutranksMustHitCount ?? 0,
                RetrievalDatasetV2HybridRepairRiskAfterPolicy = datasetV2HybridRepair?.BestProfile?.RiskAfterPolicy ?? 0,
                RetrievalDatasetV2HybridRepairRecommendation = datasetV2HybridRepair?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2HybridRiskTriageSourcePath = datasetV2HybridRiskTriage?.SourcePath ?? string.Empty,
                RetrievalDatasetV2HybridRiskTriageProfileName = datasetV2HybridRiskTriage?.Report.ProfileName ?? string.Empty,
                RetrievalDatasetV2HybridRiskCandidateCount = datasetV2HybridRiskTriage?.Report.RiskCandidateCount ?? 0,
                RetrievalDatasetV2HybridRiskByType = datasetV2HybridRiskTriage?.Report.RiskByType ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2HybridRiskBySplit = datasetV2HybridRiskTriage?.Report.RiskBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2HybridMustNotPromotedCount = datasetV2HybridRiskTriage?.Report.MustNotCandidatePromotedCount ?? 0,
                RetrievalDatasetV2HybridEligibilityBypassCount = datasetV2HybridRiskTriage?.Report.EligibilityBypassCount ?? 0,
                RetrievalDatasetV2HybridRiskProjectionMismatchCount = datasetV2HybridRiskTriage?.Report.RiskProjectionMismatchCount ?? 0,
                RetrievalDatasetV2HybridRiskTriageRecommendation = datasetV2HybridRiskTriage?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressFreezeSourcePath = datasetV2StressFreeze?.SourcePath ?? string.Empty,
                RetrievalDatasetV2StressFreezePassed = datasetV2StressFreeze?.Report.FreezePassed ?? false,
                RetrievalDatasetV2StressFreezeStatus = datasetV2StressFreeze?.Report.DatasetV2Stress ?? string.Empty,
                RetrievalDatasetV2StressFreezeRecommendation = datasetV2StressFreeze?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressFreezeBestProfile = datasetV2StressFreeze?.Report.BestPreviewProfile ?? string.Empty,
                RetrievalDatasetV2StressFreezeStressRecall = datasetV2StressFreeze?.Report.StressRecall ?? 0,
                RetrievalDatasetV2StressFreezeHoldoutRecall = datasetV2StressFreeze?.Report.HoldoutRecall ?? 0,
                RetrievalDatasetV2StressFreezeRiskAfterPolicy = datasetV2StressFreeze?.Report.RiskAfterPolicy ?? 0,
                RetrievalDatasetV2StressFreezeMustNotHitRiskAfterPolicy = datasetV2StressFreeze?.Report.MustNotHitRiskAfterPolicy ?? 0,
                RetrievalDatasetV2StressFreezeLifecycleRiskAfterPolicy = datasetV2StressFreeze?.Report.LifecycleRiskAfterPolicy ?? 0,
                RetrievalDatasetV2StressFreezeFormalOutputChanged = datasetV2StressFreeze?.Report.FormalOutputChanged ?? 0,
                RetrievalDatasetV2StressFreezeLeakageIssueCount = datasetV2StressFreeze?.Report.LeakageIssueCount ?? 0,
                RetrievalDatasetV2StressFreezeAnchorDominanceScore = datasetV2StressFreeze?.Report.AnchorDominanceScore ?? 0,
                RetrievalDatasetV2StressFreezeV4RecheckAllowed = datasetV2StressFreeze?.Report.V4RecheckAllowed ?? false,
                RetrievalDatasetV2StressFreezeReadyForFormalRetrieval = datasetV2StressFreeze?.Report.ReadyForFormalRetrieval ?? false,
                RetrievalDatasetV2StressFreezeFormalRetrievalAllowed = datasetV2StressFreeze?.Report.FormalRetrievalAllowed ?? false,
                RetrievalDatasetV2StressFreezeBlockedReasons = datasetV2StressFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                VectorV4ReadinessRecheckSourcePath = vectorV4Recheck?.SourcePath ?? string.Empty,
                VectorV4ReadinessRecheckPassed = vectorV4Recheck?.Report.RecheckPassed ?? false,
                VectorV4ReadinessRecheckRecommendation = vectorV4Recheck?.Report.Recommendation ?? string.Empty,
                VectorV4ReadinessLegacyStatus = vectorV4Recheck?.Report.LegacyVectorStatus ?? string.Empty,
                VectorV4ReadinessSmallStatus = vectorV4Recheck?.Report.DatasetV2SmallStatus ?? string.Empty,
                VectorV4ReadinessStressStatus = vectorV4Recheck?.Report.DatasetV2StressStatus ?? string.Empty,
                VectorV4ReadinessPgVectorStatus = vectorV4Recheck?.Report.PgVectorProviderStatus ?? string.Empty,
                VectorV4ReadinessHybridScoringStatus = vectorV4Recheck?.Report.HybridScoringRepairStatus ?? string.Empty,
                VectorV4ReadinessRuntimeGateStatus = vectorV4Recheck?.Report.RuntimeChangeGateStatus ?? string.Empty,
                VectorV4ReadinessBestProfile = vectorV4Recheck?.Report.BestPreviewProfile ?? string.Empty,
                VectorV4ReadinessStressRecall = vectorV4Recheck?.Report.DatasetV2StressRecall ?? 0,
                VectorV4ReadinessHoldoutRecall = vectorV4Recheck?.Report.DatasetV2HoldoutRecall ?? 0,
                VectorV4ReadinessRiskAfterPolicy = vectorV4Recheck?.Report.RiskAfterPolicy ?? 0,
                VectorV4ReadinessFormalOutputChanged = vectorV4Recheck?.Report.FormalOutputChanged ?? 0,
                VectorV4ReadinessReadyForGuardedFormalPreview = vectorV4Recheck?.Report.ReadyForGuardedFormalPreview ?? false,
                VectorV4ReadinessReadyForRuntimeSwitch = vectorV4Recheck?.Report.ReadyForRuntimeSwitch ?? false,
                VectorV4ReadinessFormalRetrievalAllowed = vectorV4Recheck?.Report.FormalRetrievalAllowed ?? false,
                VectorV4ReadinessBlockedReasons = vectorV4Recheck?.Report.BlockedReasons ?? Array.Empty<string>(),
                VectorShadowPackageComparisonSourcePath = shadowPackageComparison?.SourcePath ?? string.Empty,
                VectorShadowPackageComparisonGatePassed = shadowPackageComparison?.Report.GatePassed ?? false,
                VectorShadowPackageComparisonRecommendation = shadowPackageComparison?.Report.Recommendation ?? string.Empty,
                VectorShadowPackageComparisonProfileName = shadowPackageComparison?.Report.ProfileName ?? string.Empty,
                VectorShadowPackageCandidateAddCount = shadowPackageComparison?.Report.CandidateAddCount ?? 0,
                VectorShadowPackageCandidateRemoveCount = shadowPackageComparison?.Report.CandidateRemoveCount ?? 0,
                VectorShadowPackageCandidateUnchangedCount = shadowPackageComparison?.Report.CandidateUnchangedCount ?? 0,
                VectorShadowPackageSectionChangedCount = shadowPackageComparison?.Report.SectionChangedCount ?? 0,
                VectorShadowPackageTokenDeltaTotal = shadowPackageComparison?.Report.TokenDeltaTotal ?? 0,
                VectorShadowPackageTokenDeltaMax = shadowPackageComparison?.Report.TokenDeltaMax ?? 0,
                VectorShadowPackageConstraintCoverageDelta = shadowPackageComparison?.Report.ConstraintCoverageDelta ?? 0,
                VectorShadowPackageRelationCoverageDelta = shadowPackageComparison?.Report.RelationCoverageDelta ?? 0,
                VectorShadowPackageRiskAfterPolicy = shadowPackageComparison?.Report.RiskAfterPolicy ?? 0,
                VectorShadowPackageMustNotHitRiskAfterPolicy = shadowPackageComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                VectorShadowPackageLifecycleRiskAfterPolicy = shadowPackageComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                VectorShadowPackageFormalOutputChanged = shadowPackageComparison?.Report.FormalOutputChanged ?? 0,
                VectorShadowPackagePackageOutputChanged = shadowPackageComparison?.Report.PackageOutputChanged ?? false,
                VectorShadowPackagePackingPolicyChanged = shadowPackageComparison?.Report.PackingPolicyChanged ?? false,
                VectorShadowPackageRuntimeMutated = shadowPackageComparison?.Report.RuntimeMutated ?? false,
                VectorShadowPackageReadyForRuntimeSwitch = shadowPackageComparison?.Report.ReadyForRuntimeSwitch ?? false,
                VectorShadowPackageFormalRetrievalAllowed = shadowPackageComparison?.Report.FormalRetrievalAllowed ?? false,
                VectorShadowPackageBlockedReasons = shadowPackageComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalRetrievalIntegrationPlanSourcePath = formalRetrievalIntegrationPlan?.SourcePath ?? string.Empty,
                FormalRetrievalIntegrationPlanPassed = formalRetrievalIntegrationPlan?.Report.PlanPassed ?? false,
                FormalRetrievalIntegrationPlanRecommendation = formalRetrievalIntegrationPlan?.Report.Recommendation ?? string.Empty,
                FormalRetrievalIntegrationPlanAllowedMode = formalRetrievalIntegrationPlan?.Report.AllowedMode ?? string.Empty,
                FormalRetrievalIntegrationPlanRequiredNextPhase = formalRetrievalIntegrationPlan?.Report.RequiredNextPhase ?? string.Empty,
                FormalRetrievalIntegrationPlanFormalRetrievalAllowed = formalRetrievalIntegrationPlan?.Report.FormalRetrievalAllowed ?? false,
                FormalRetrievalIntegrationPlanRuntimeSwitchAllowed = formalRetrievalIntegrationPlan?.Report.RuntimeSwitchAllowed ?? false,
                FormalRetrievalIntegrationPlanReadyForRuntimeSwitch = formalRetrievalIntegrationPlan?.Report.ReadyForRuntimeSwitch ?? false,
                FormalRetrievalIntegrationPlanIntegrationPoints = formalRetrievalIntegrationPlan?.Report.IntegrationPoints ?? Array.Empty<string>(),
                FormalRetrievalIntegrationPlanBlockedReasons = formalRetrievalIntegrationPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalRetrievalIntegrationDecisionSourcePath = formalRetrievalIntegrationDecision?.SourcePath ?? string.Empty,
                FormalRetrievalIntegrationDecisionPassed = formalRetrievalIntegrationDecision?.Snapshot.DecisionPassed ?? false,
                FormalRetrievalIntegrationDecisionGatePassed = formalRetrievalIntegrationDecision?.Snapshot.GatePassed ?? false,
                FormalRetrievalIntegrationDecisionRecommendation = formalRetrievalIntegrationDecision?.Snapshot.Recommendation ?? string.Empty,
                FormalRetrievalIntegrationDecisionValue = formalRetrievalIntegrationDecision?.Snapshot.IntegrationDecision ?? string.Empty,
                FormalRetrievalIntegrationDecisionNextAllowedPhase = formalRetrievalIntegrationDecision?.Snapshot.NextAllowedPhase ?? string.Empty,
                FormalRetrievalIntegrationDecisionReadyForFreeze = formalRetrievalIntegrationDecision?.Snapshot.ReadyForFormalRetrievalIntegrationFreeze ?? false,
                FormalRetrievalIntegrationDecisionReadyForNoOpBindingPlan = formalRetrievalIntegrationDecision?.Snapshot.ReadyForAdapterNoOpBindingPlan ?? false,
                FormalRetrievalIntegrationDecisionFormalRetrievalAllowed = formalRetrievalIntegrationDecision?.Snapshot.FormalRetrievalAllowed ?? false,
                FormalRetrievalIntegrationDecisionRuntimeSwitchAllowed = formalRetrievalIntegrationDecision?.Snapshot.RuntimeSwitchAllowed ?? false,
                FormalRetrievalIntegrationDecisionReadyForRuntimeSwitch = formalRetrievalIntegrationDecision?.Snapshot.ReadyForRuntimeSwitch ?? false,
                FormalRetrievalIntegrationDecisionRiskAfterPolicy = formalRetrievalIntegrationDecision?.Snapshot.RiskAfterPolicy ?? 0,
                FormalRetrievalIntegrationDecisionFormalOutputChanged = formalRetrievalIntegrationDecision?.Snapshot.FormalOutputChanged ?? 0,
                FormalRetrievalIntegrationDecisionPackageOutputChanged = formalRetrievalIntegrationDecision?.Snapshot.PackageOutputChanged ?? false,
                FormalRetrievalIntegrationDecisionPackingPolicyChanged = formalRetrievalIntegrationDecision?.Snapshot.PackingPolicyChanged ?? false,
                FormalRetrievalIntegrationDecisionRuntimeMutated = formalRetrievalIntegrationDecision?.Snapshot.RuntimeMutated ?? false,
                FormalRetrievalIntegrationDecisionVectorStoreBindingChanged = formalRetrievalIntegrationDecision?.Snapshot.VectorStoreBindingChanged ?? false,
                FormalRetrievalIntegrationDecisionBlockedReasons = formalRetrievalIntegrationDecision?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                ShadowFormalRetrievalAdapterPlanSourcePath = shadowFormalRetrievalAdapterPlan?.SourcePath ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanPassed = shadowFormalRetrievalAdapterPlan?.Report.PlanPassed ?? false,
                ShadowFormalRetrievalAdapterPlanRecommendation = shadowFormalRetrievalAdapterPlan?.Report.Recommendation ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanAllowedMode = shadowFormalRetrievalAdapterPlan?.Report.AllowedMode ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanVectorProviderSource = shadowFormalRetrievalAdapterPlan?.Report.VectorProviderSource ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanGraphCandidateSource = shadowFormalRetrievalAdapterPlan?.Report.GraphCandidateSource ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanFormalRetrievalAllowed = shadowFormalRetrievalAdapterPlan?.Report.FormalRetrievalAllowed ?? false,
                ShadowFormalRetrievalAdapterPlanRuntimeSwitchAllowed = shadowFormalRetrievalAdapterPlan?.Report.RuntimeSwitchAllowed ?? false,
                ShadowFormalRetrievalAdapterPlanForbiddenActions = shadowFormalRetrievalAdapterPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                ShadowFormalRetrievalAdapterPlanBlockedReasons = shadowFormalRetrievalAdapterPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                ShadowFormalRetrievalAdapterSourcePath = shadowFormalRetrievalAdapter?.SourcePath ?? string.Empty,
                ShadowFormalRetrievalAdapterPassed = shadowFormalRetrievalAdapter?.Report.AdapterPassed ?? false,
                ShadowFormalRetrievalAdapterGatePassed = shadowFormalRetrievalAdapter?.Report.GatePassed ?? false,
                ShadowFormalRetrievalAdapterRecommendation = shadowFormalRetrievalAdapter?.Report.Recommendation ?? string.Empty,
                ShadowFormalRetrievalAdapterAllowedMode = shadowFormalRetrievalAdapter?.Report.AllowedMode ?? string.Empty,
                ShadowFormalRetrievalAdapterVectorProviderSource = shadowFormalRetrievalAdapter?.Report.VectorProviderSource ?? string.Empty,
                ShadowFormalRetrievalAdapterGraphCandidateSource = shadowFormalRetrievalAdapter?.Report.GraphCandidateSource ?? string.Empty,
                ShadowFormalRetrievalAdapterSampleCount = shadowFormalRetrievalAdapter?.Report.SampleCount ?? 0,
                ShadowFormalRetrievalAdapterRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.RiskAfterPolicy ?? 0,
                ShadowFormalRetrievalAdapterMustNotHitRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.MustNotHitRiskAfterPolicy ?? 0,
                ShadowFormalRetrievalAdapterLifecycleRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.LifecycleRiskAfterPolicy ?? 0,
                ShadowFormalRetrievalAdapterFormalOutputChanged = shadowFormalRetrievalAdapter?.Report.FormalOutputChanged ?? 0,
                ShadowFormalRetrievalAdapterFormalSelectedSetChanged = shadowFormalRetrievalAdapter?.Report.FormalSelectedSetChanged ?? false,
                ShadowFormalRetrievalAdapterPackageOutputChanged = shadowFormalRetrievalAdapter?.Report.PackageOutputChanged ?? false,
                ShadowFormalRetrievalAdapterPackingPolicyChanged = shadowFormalRetrievalAdapter?.Report.PackingPolicyChanged ?? false,
                ShadowFormalRetrievalAdapterRuntimeMutated = shadowFormalRetrievalAdapter?.Report.RuntimeMutated ?? false,
                ShadowFormalRetrievalAdapterVectorStoreBindingChanged = shadowFormalRetrievalAdapter?.Report.VectorStoreBindingChanged ?? false,
                ShadowFormalRetrievalAdapterBlockedReasons = shadowFormalRetrievalAdapter?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalAdapterPackageShadowComparisonSourcePath = formalAdapterPackageShadowComparison?.SourcePath ?? string.Empty,
                FormalAdapterPackageShadowComparisonPassed = formalAdapterPackageShadowComparison?.Report.ComparisonPassed ?? false,
                FormalAdapterPackageShadowComparisonGatePassed = formalAdapterPackageShadowComparison?.Report.GatePassed ?? false,
                FormalAdapterPackageShadowComparisonRecommendation = formalAdapterPackageShadowComparison?.Report.Recommendation ?? string.Empty,
                FormalAdapterPackageShadowComparisonAllowedMode = formalAdapterPackageShadowComparison?.Report.AllowedMode ?? string.Empty,
                FormalAdapterPackageShadowComparisonSampleCount = formalAdapterPackageShadowComparison?.Report.SampleCount ?? 0,
                FormalAdapterPackageShadowComparisonRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.RiskAfterPolicy ?? 0,
                FormalAdapterPackageShadowComparisonMustNotHitRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                FormalAdapterPackageShadowComparisonLifecycleRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaTotal ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaMax = formalAdapterPackageShadowComparison?.Report.TokenDeltaMax ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaBudgetTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetTotal ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaBudgetPerSample = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetPerSample ?? 0,
                FormalAdapterPackageShadowComparisonFormalOutputChanged = formalAdapterPackageShadowComparison?.Report.FormalOutputChanged ?? 0,
                FormalAdapterPackageShadowComparisonFormalSelectedSetChanged = formalAdapterPackageShadowComparison?.Report.FormalSelectedSetChanged ?? false,
                FormalAdapterPackageShadowComparisonPackageOutputChanged = formalAdapterPackageShadowComparison?.Report.PackageOutputChanged ?? false,
                FormalAdapterPackageShadowComparisonPackingPolicyChanged = formalAdapterPackageShadowComparison?.Report.PackingPolicyChanged ?? false,
                FormalAdapterPackageShadowComparisonRuntimeMutated = formalAdapterPackageShadowComparison?.Report.RuntimeMutated ?? false,
                FormalAdapterPackageShadowComparisonVectorStoreBindingChanged = formalAdapterPackageShadowComparison?.Report.VectorStoreBindingChanged ?? false,
                FormalAdapterPackageShadowComparisonBlockedReasons = formalAdapterPackageShadowComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                GraphVectorRetrievalQualityAuditSourcePath = graphVectorRetrievalQualityAudit?.SourcePath ?? string.Empty,
                GraphVectorRetrievalQualityAuditPassed = graphVectorRetrievalQualityAudit?.Report.AuditPassed ?? false,
                GraphVectorRetrievalQualityAuditGatePassed = graphVectorRetrievalQualityAudit?.Report.GatePassed ?? false,
                GraphVectorRetrievalQualityAuditRecommendation = graphVectorRetrievalQualityAudit?.Report.Recommendation ?? string.Empty,
                GraphVectorRetrievalQualityAuditAllowedMode = graphVectorRetrievalQualityAudit?.Report.AllowedMode ?? string.Empty,
                GraphVectorRetrievalQualityAuditSampleCount = graphVectorRetrievalQualityAudit?.Report.SampleCount ?? 0,
                GraphVectorRetrievalQualityAuditRecall = graphVectorRetrievalQualityAudit?.Report.Recall ?? 0,
                GraphVectorRetrievalQualityAuditPrecision = graphVectorRetrievalQualityAudit?.Report.Precision ?? 0,
                GraphVectorRetrievalQualityAuditMrr = graphVectorRetrievalQualityAudit?.Report.MeanReciprocalRank ?? 0,
                GraphVectorRetrievalQualityAuditGraphNoiseCount = graphVectorRetrievalQualityAudit?.Report.GraphNoiseCount ?? 0,
                GraphVectorRetrievalQualityAuditVectorNoiseCount = graphVectorRetrievalQualityAudit?.Report.VectorNoiseCount ?? 0,
                GraphVectorRetrievalQualityAuditRankingRegressionCount = graphVectorRetrievalQualityAudit?.Report.RankingRegressionCount ?? 0,
                GraphVectorRetrievalQualityAuditMustHitBelowTopKCount = graphVectorRetrievalQualityAudit?.Report.MustHitBelowTopKCount ?? 0,
                GraphVectorRetrievalQualityAuditRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.RiskAfterPolicy ?? 0,
                GraphVectorRetrievalQualityAuditMustNotHitRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.MustNotHitRiskAfterPolicy ?? 0,
                GraphVectorRetrievalQualityAuditLifecycleRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.LifecycleRiskAfterPolicy ?? 0,
                GraphVectorRetrievalQualityAuditSectionMismatchCount = graphVectorRetrievalQualityAudit?.Report.SectionMismatchCount ?? 0,
                GraphVectorRetrievalQualityAuditMetadataEvidenceGapCount = graphVectorRetrievalQualityAudit?.Report.MetadataEvidenceGapCount ?? 0,
                GraphVectorRetrievalQualityAuditFailureClusterIds = graphVectorRetrievalQualityAudit?.Report.FailureClusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>(),
                GraphVectorRetrievalQualityAuditFormalOutputChanged = graphVectorRetrievalQualityAudit?.Report.FormalOutputChanged ?? 0,
                GraphVectorRetrievalQualityAuditFormalSelectedSetChanged = graphVectorRetrievalQualityAudit?.Report.FormalSelectedSetChanged ?? false,
                GraphVectorRetrievalQualityAuditPackageOutputChanged = graphVectorRetrievalQualityAudit?.Report.PackageOutputChanged ?? false,
                GraphVectorRetrievalQualityAuditPackingPolicyChanged = graphVectorRetrievalQualityAudit?.Report.PackingPolicyChanged ?? false,
                GraphVectorRetrievalQualityAuditRuntimeMutated = graphVectorRetrievalQualityAudit?.Report.RuntimeMutated ?? false,
                GraphVectorRetrievalQualityAuditVectorStoreBindingChanged = graphVectorRetrievalQualityAudit?.Report.VectorStoreBindingChanged ?? false,
                GraphVectorRetrievalQualityAuditBlockedReasons = graphVectorRetrievalQualityAudit?.Report.BlockedReasons ?? Array.Empty<string>(),
                RetrievalQualityRepairPreviewSourcePath = retrievalQualityRepairPreview?.SourcePath ?? string.Empty,
                RetrievalQualityRepairPreviewPassed = retrievalQualityRepairPreview?.Report.PreviewPassed ?? false,
                RetrievalQualityRepairPreviewGatePassed = retrievalQualityRepairPreview?.Report.GatePassed ?? false,
                RetrievalQualityRepairPreviewRecommendation = retrievalQualityRepairPreview?.Report.Recommendation ?? string.Empty,
                RetrievalQualityRepairPreviewAllowedMode = retrievalQualityRepairPreview?.Report.AllowedMode ?? string.Empty,
                RetrievalQualityRepairPreviewBestProfileId = retrievalQualityRepairPreview?.Report.BestProfileId ?? string.Empty,
                RetrievalQualityRepairPreviewBaselineRecall = retrievalQualityRepairPreview?.Report.Baseline.Recall ?? 0d,
                RetrievalQualityRepairPreviewBaselinePrecision = retrievalQualityRepairPreview?.Report.Baseline.Precision ?? 0d,
                RetrievalQualityRepairPreviewBaselineMrr = retrievalQualityRepairPreview?.Report.Baseline.MeanReciprocalRank ?? 0d,
                RetrievalQualityRepairPreviewBestRecall = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Recall ?? 0d,
                RetrievalQualityRepairPreviewBestPrecision = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Precision ?? 0d,
                RetrievalQualityRepairPreviewBestMrr = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MeanReciprocalRank ?? 0d,
                RetrievalQualityRepairPreviewRecallDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.RecallDelta ?? 0d,
                RetrievalQualityRepairPreviewMrrDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MrrDelta ?? 0d,
                RetrievalQualityRepairPreviewMustHitBelowTopKBaseline = retrievalQualityRepairPreview?.Report.Baseline.MustHitBelowTopKCount ?? 0,
                RetrievalQualityRepairPreviewMustHitBelowTopKBest = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MustHitBelowTopKCount ?? 0,
                RetrievalQualityRepairPreviewProfileEvaluatedCount = retrievalQualityRepairPreview?.Report.Profiles.Count ?? 0,
                RetrievalQualityRepairPreviewRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.RiskAfterPolicy ?? 0,
                RetrievalQualityRepairPreviewMustNotHitRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.MustNotHitRiskAfterPolicy ?? 0,
                RetrievalQualityRepairPreviewLifecycleRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.LifecycleRiskAfterPolicy ?? 0,
                RetrievalQualityRepairPreviewSectionMismatchCount = retrievalQualityRepairPreview?.Report.Baseline.SectionMismatchCount ?? 0,
                RetrievalQualityRepairPreviewGraphNoiseCount = retrievalQualityRepairPreview?.Report.Baseline.GraphNoiseCount ?? 0,
                RetrievalQualityRepairPreviewRankingRegressionCount = retrievalQualityRepairPreview?.Report.Baseline.RankingRegressionCount ?? 0,
                RetrievalQualityRepairPreviewTokenDeltaTotal = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDelta ?? 0,
                RetrievalQualityRepairPreviewTokenDeltaMax = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDeltaAbsolute ?? 0,
                RetrievalQualityRepairPreviewFormalOutputChanged = retrievalQualityRepairPreview?.Report.FormalOutputChanged ?? 0,
                RetrievalQualityRepairPreviewFormalSelectedSetChanged = retrievalQualityRepairPreview?.Report.FormalSelectedSetChanged ?? false,
                RetrievalQualityRepairPreviewPackageOutputChanged = retrievalQualityRepairPreview?.Report.PackageOutputChanged ?? false,
                RetrievalQualityRepairPreviewPackingPolicyChanged = retrievalQualityRepairPreview?.Report.PackingPolicyChanged ?? false,
                RetrievalQualityRepairPreviewRuntimeMutated = retrievalQualityRepairPreview?.Report.RuntimeMutated ?? false,
                RetrievalQualityRepairPreviewVectorStoreBindingChanged = retrievalQualityRepairPreview?.Report.VectorStoreBindingChanged ?? false,
                RetrievalQualityRepairPreviewBlockedReasons = retrievalQualityRepairPreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                RuntimeObservableFeatureContractSourcePath = runtimeObservableFeatureContract?.SourcePath ?? string.Empty,
                RuntimeObservableFeatureContractPassed = runtimeObservableFeatureContract?.Report.ContractPassed ?? false,
                RuntimeObservableFeatureContractGatePassed = runtimeObservableFeatureContract?.Report.GatePassed ?? false,
                RuntimeObservableFeatureContractRecommendation = runtimeObservableFeatureContract?.Report.Recommendation ?? string.Empty,
                RuntimeObservableFeatureContractAllowedMode = runtimeObservableFeatureContract?.Report.AllowedMode ?? string.Empty,
                RuntimeObservableFeatureContractBestProfileId = runtimeObservableFeatureContract?.Report.BestProfileId ?? string.Empty,
                RuntimeObservableFeatureContractBestProfileContractStatus = runtimeObservableFeatureContract?.Report.BestProfileContractStatus ?? string.Empty,
                RuntimeObservableFeatureContractForbiddenForScoringCount = runtimeObservableFeatureContract?.Report.ForbiddenForScoringCount ?? 0,
                RuntimeObservableFeatureContractEvalOnlyCount = runtimeObservableFeatureContract?.Report.EvalOnlyCount ?? 0,
                RuntimeObservableFeatureContractDerivedAtRuntimeCount = runtimeObservableFeatureContract?.Report.DerivedAtRuntimeCount ?? 0,
                RuntimeObservableFeatureContractRuntimeObservableCount = runtimeObservableFeatureContract?.Report.RuntimeObservableCount ?? 0,
                RuntimeObservableFeatureContractScoringFeatureCount = runtimeObservableFeatureContract?.Report.ScoringFeatureCount ?? 0,
                RuntimeObservableFeatureContractFilteringFeatureCount = runtimeObservableFeatureContract?.Report.FilteringFeatureCount ?? 0,
                RuntimeObservableFeatureContractCandidateExpansionFeatureCount = runtimeObservableFeatureContract?.Report.CandidateExpansionFeatureCount ?? 0,
                RuntimeObservableFeatureContractSourceScanFiles = runtimeObservableFeatureContract?.Report.SourceScan.ScannedFileCount ?? 0,
                RuntimeObservableFeatureContractFixtureTokenHitCount = runtimeObservableFeatureContract?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                RuntimeObservableFeatureContractFlaggedTokens = runtimeObservableFeatureContract?.Report.SourceScan.FlaggedTokens ?? Array.Empty<string>(),
                RuntimeObservableFeatureContractFormalOutputChanged = runtimeObservableFeatureContract?.Report.FormalOutputChanged ?? 0,
                RuntimeObservableFeatureContractFormalSelectedSetChanged = runtimeObservableFeatureContract?.Report.FormalSelectedSetChanged ?? false,
                RuntimeObservableFeatureContractPackageOutputChanged = runtimeObservableFeatureContract?.Report.PackageOutputChanged ?? false,
                RuntimeObservableFeatureContractPackingPolicyChanged = runtimeObservableFeatureContract?.Report.PackingPolicyChanged ?? false,
                RuntimeObservableFeatureContractRuntimeMutated = runtimeObservableFeatureContract?.Report.RuntimeMutated ?? false,
                RuntimeObservableFeatureContractVectorStoreBindingChanged = runtimeObservableFeatureContract?.Report.VectorStoreBindingChanged ?? false,
                RuntimeObservableFeatureContractBlockedReasons = runtimeObservableFeatureContract?.Report.BlockedReasons ?? Array.Empty<string>(),
                RuntimeRetrievalFeatureDerivationSourcePath = runtimeRetrievalFeatureDerivation?.SourcePath ?? string.Empty,
                RuntimeRetrievalFeatureDerivationPassed = runtimeRetrievalFeatureDerivation?.Report.PreviewPassed ?? false,
                RuntimeRetrievalFeatureDerivationGatePassed = runtimeRetrievalFeatureDerivation?.Report.GatePassed ?? false,
                RuntimeRetrievalFeatureDerivationRecommendation = runtimeRetrievalFeatureDerivation?.Report.Recommendation ?? string.Empty,
                RuntimeRetrievalFeatureDerivationAllowedMode = runtimeRetrievalFeatureDerivation?.Report.AllowedMode ?? string.Empty,
                RuntimeRetrievalFeatureDerivationSampleCount = runtimeRetrievalFeatureDerivation?.Report.SampleCount ?? 0,
                RuntimeRetrievalFeatureDerivationTargetSectionMatchRate = runtimeRetrievalFeatureDerivation?.Report.TargetSectionMatchRate ?? 0,
                RuntimeRetrievalFeatureDerivationRequiredRelationCoverageRate = runtimeRetrievalFeatureDerivation?.Report.RequiredRelationCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationEvidenceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.EvidenceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationSourceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.SourceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationDerivationCompletenessRate = runtimeRetrievalFeatureDerivation?.Report.DerivationCompletenessRate ?? 0,
                RuntimeRetrievalFeatureDerivationBaselineRecall = runtimeRetrievalFeatureDerivation?.Report.BaselineRecall ?? 0,
                RuntimeRetrievalFeatureDerivationBaselineMrr = runtimeRetrievalFeatureDerivation?.Report.BaselineMeanReciprocalRank ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedRecall = runtimeRetrievalFeatureDerivation?.Report.DerivedRecall ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedMrr = runtimeRetrievalFeatureDerivation?.Report.DerivedMeanReciprocalRank ?? 0,
                RuntimeRetrievalFeatureDerivationEvalDrivenRecall = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenRecall ?? 0,
                RuntimeRetrievalFeatureDerivationEvalDrivenMrr = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenMeanReciprocalRank ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedRecallDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedRecallDelta ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedMrrDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedMrrDelta ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedMustNotHitRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedMustNotHitRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedLifecycleRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedLifecycleRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedSectionMismatchCount = runtimeRetrievalFeatureDerivation?.Report.DerivedSectionMismatchCount ?? 0,
                RuntimeRetrievalFeatureDerivationForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivation?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                RuntimeRetrievalFeatureDerivationSourceScanFiles = runtimeRetrievalFeatureDerivation?.Report.SourceScan.ScannedFileCount ?? 0,
                RuntimeRetrievalFeatureDerivationFixtureTokenHitCount = runtimeRetrievalFeatureDerivation?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                RuntimeRetrievalFeatureDerivationFormalOutputChanged = runtimeRetrievalFeatureDerivation?.Report.FormalOutputChanged ?? 0,
                RuntimeRetrievalFeatureDerivationFormalSelectedSetChanged = runtimeRetrievalFeatureDerivation?.Report.FormalSelectedSetChanged ?? false,
                RuntimeRetrievalFeatureDerivationPackageOutputChanged = runtimeRetrievalFeatureDerivation?.Report.PackageOutputChanged ?? false,
                RuntimeRetrievalFeatureDerivationPackingPolicyChanged = runtimeRetrievalFeatureDerivation?.Report.PackingPolicyChanged ?? false,
                RuntimeRetrievalFeatureDerivationRuntimeMutated = runtimeRetrievalFeatureDerivation?.Report.RuntimeMutated ?? false,
                RuntimeRetrievalFeatureDerivationVectorStoreBindingChanged = runtimeRetrievalFeatureDerivation?.Report.VectorStoreBindingChanged ?? false,
                RuntimeRetrievalFeatureDerivationBlockedReasons = runtimeRetrievalFeatureDerivation?.Report.BlockedReasons ?? Array.Empty<string>(),
                RuntimeRetrievalFeatureDerivationRepairSourcePath = runtimeRetrievalFeatureDerivationRepair?.SourcePath ?? string.Empty,
                RuntimeRetrievalFeatureDerivationRepairPassed = runtimeRetrievalFeatureDerivationRepair?.Report.PreviewPassed ?? false,
                RuntimeRetrievalFeatureDerivationRepairGatePassed = runtimeRetrievalFeatureDerivationRepair?.Report.GatePassed ?? false,
                RuntimeRetrievalFeatureDerivationRepairRecommendation = runtimeRetrievalFeatureDerivationRepair?.Report.Recommendation ?? string.Empty,
                RuntimeRetrievalFeatureDerivationRepairAllowedMode = runtimeRetrievalFeatureDerivationRepair?.Report.AllowedMode ?? string.Empty,
                RuntimeRetrievalFeatureDerivationRepairTrainSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.TrainSampleCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutSampleCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairCanonicalRelationCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalRequiredRelationCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationRepairCanonicalEvidenceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalEvidenceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationRepairCanonicalSourceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalSourceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationRepairDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivationRepair?.Report.DerivedRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationRepairForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivationRepair?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairSourceScanFiles = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.ScannedFileCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairFixtureTokenHitCount = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairFormalOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalOutputChanged ?? 0,
                RuntimeRetrievalFeatureDerivationRepairFormalSelectedSetChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalSelectedSetChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairPackageOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackageOutputChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairPackingPolicyChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackingPolicyChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairRuntimeMutated = runtimeRetrievalFeatureDerivationRepair?.Report.RuntimeMutated ?? false,
                RuntimeRetrievalFeatureDerivationRepairVectorStoreBindingChanged = runtimeRetrievalFeatureDerivationRepair?.Report.VectorStoreBindingChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairBlockedReasons = runtimeRetrievalFeatureDerivationRepair?.Report.BlockedReasons ?? Array.Empty<string>(),
                FeatureDerivationFailureFreezeSourcePath = featureDerivationFailureFreeze?.SourcePath ?? string.Empty,
                FeatureDerivationFailureFreezePassed = featureDerivationFailureFreeze?.Report.FreezePassed ?? false,
                FeatureDerivationFailureFreezeStatus = featureDerivationFailureFreeze?.Report.FrozenStatus ?? string.Empty,
                FeatureDerivationFailureFreezeRecommendation = featureDerivationFailureFreeze?.Report.Recommendation ?? string.Empty,
                FeatureDerivationFailureFreezeCanonicalResolverReusable = featureDerivationFailureFreeze?.Report.CanonicalAnchorResolverReusable ?? false,
                FeatureDerivationFailureFreezeRelationDeriverReady = featureDerivationFailureFreeze?.Report.RuntimeRelationIntentDeriverReady ?? false,
                FeatureDerivationFailureFreezeDisabledCapabilities = featureDerivationFailureFreeze?.Report.DisabledCapabilities ?? Array.Empty<string>(),
                FeatureDerivationFailureFreezeRecommendedNextPhases = featureDerivationFailureFreeze?.Report.RecommendedNextPhases ?? Array.Empty<string>(),
                GraphHubNoiseControlSourcePath = graphHubNoiseControl?.SourcePath ?? string.Empty,
                GraphHubNoiseControlPassed = graphHubNoiseControl?.Report.PreviewPassed ?? false,
                GraphHubNoiseControlGatePassed = graphHubNoiseControl?.Report.GatePassed ?? false,
                GraphHubNoiseControlRecommendation = graphHubNoiseControl?.Report.Recommendation ?? string.Empty,
                GraphHubNoiseControlHubItemCount = graphHubNoiseControl?.Report.HubItemCount ?? 0,
                GraphHubNoiseControlAvgDominance = graphHubNoiseControl?.Report.AvgHubDominanceRatio ?? 0,
                GraphHubNoiseControlBaselineRecall = graphHubNoiseControl?.Report.Baseline.Recall ?? 0,
                GraphHubNoiseControlHubCtrlRecall = graphHubNoiseControl?.Report.HubControlled.Recall ?? 0,
                GraphHubNoiseControlRecallDelta = graphHubNoiseControl?.Report.HubControlledRecallDelta ?? 0,
                InputMetadataEnrichmentSourcePath = inputMetadataEnrichment?.SourcePath ?? string.Empty,
                InputMetadataEnrichmentPreviewPassed = inputMetadataEnrichment?.Snapshot.PreviewPassed ?? false,
                InputMetadataEnrichmentGatePassed = inputMetadataEnrichment?.Snapshot.GatePassed ?? false,
                InputMetadataEnrichmentRecommendation = inputMetadataEnrichment?.Snapshot.Recommendation ?? string.Empty,
                InputMetadataEnrichmentCoverageDelta = inputMetadataEnrichment?.Snapshot.MetadataCoverageDelta ?? 0,
                InputMetadataEnrichmentBeforeRecall = inputMetadataEnrichment?.Snapshot.BeforeRecall ?? 0,
                InputMetadataEnrichmentAfterRecall = inputMetadataEnrichment?.Snapshot.AfterRecall ?? 0,
                InputMetadataEnrichmentIndependentNonDenseSourceCount = inputMetadataEnrichment?.Snapshot.IndependentNonDenseSourceCount ?? 0,
                InputMetadataEnrichmentRiskAfterPolicy = inputMetadataEnrichment?.Snapshot.RiskAfterPolicy ?? 0,
                InputMetadataEnrichmentMustNotHitRiskAfterPolicy = inputMetadataEnrichment?.Snapshot.MustNotHitRiskAfterPolicy ?? 0,
                InputMetadataEnrichmentLifecycleRiskAfterPolicy = inputMetadataEnrichment?.Snapshot.LifecycleRiskAfterPolicy ?? 0,
                InputMetadataEnrichmentPackageOutputChanged = inputMetadataEnrichment?.Snapshot.PackageOutputChanged ?? false,
                InputMetadataEnrichmentPackingPolicyChanged = inputMetadataEnrichment?.Snapshot.PackingPolicyChanged ?? false,
                InputMetadataEnrichmentRuntimeMutated = inputMetadataEnrichment?.Snapshot.RuntimeMutated ?? false,
                InputMetadataEnrichmentVectorStoreBindingChanged = inputMetadataEnrichment?.Snapshot.VectorStoreBindingChanged ?? false,
                InputMetadataEnrichmentBlockedReasons = inputMetadataEnrichment?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                EnrichedCandidateSourceRepairRecheckSourcePath = enrichedCandidateSourceRepairRecheck?.SourcePath ?? string.Empty,
                EnrichedCandidateSourceRepairRecheckPassed = enrichedCandidateSourceRepairRecheck?.Snapshot.RecheckPassed ?? false,
                EnrichedCandidateSourceRepairRecheckGatePassed = enrichedCandidateSourceRepairRecheck?.Snapshot.GatePassed ?? false,
                EnrichedCandidateSourceRepairRecheckRecommendation = enrichedCandidateSourceRepairRecheck?.Snapshot.Recommendation ?? string.Empty,
                EnrichedCandidateSourceRepairQualityImproved = enrichedCandidateSourceRepairRecheck?.Snapshot.QualityImproved ?? false,
                EnrichedCandidateSourceRepairTrainRecallDelta = enrichedCandidateSourceRepairRecheck?.Snapshot.TrainDerivedRecallDelta ?? 0,
                EnrichedCandidateSourceRepairHoldoutRecallDelta = enrichedCandidateSourceRepairRecheck?.Snapshot.HoldoutDerivedRecallDelta ?? 0,
                EnrichedCandidateSourceRepairMustHitBelowTopKDelta = enrichedCandidateSourceRepairRecheck?.Snapshot.MustHitBelowTopKDelta ?? 0,
                EnrichedCandidateSourceRepairRiskAfterPolicy = enrichedCandidateSourceRepairRecheck?.Snapshot.RiskAfterPolicy ?? 0,
                EnrichedCandidateSourceRepairPackageOutputChanged = enrichedCandidateSourceRepairRecheck?.Snapshot.PackageOutputChanged ?? false,
                EnrichedCandidateSourceRepairPackingPolicyChanged = enrichedCandidateSourceRepairRecheck?.Snapshot.PackingPolicyChanged ?? false,
                EnrichedCandidateSourceRepairRuntimeMutated = enrichedCandidateSourceRepairRecheck?.Snapshot.RuntimeMutated ?? false,
                EnrichedCandidateSourceRepairVectorStoreBindingChanged = enrichedCandidateSourceRepairRecheck?.Snapshot.VectorStoreBindingChanged ?? false,
                EnrichedCandidateSourceRepairBlockedReasons = enrichedCandidateSourceRepairRecheck?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                EnrichedCandidateSourceRepairQualityBlockedReasons = enrichedCandidateSourceRepairRecheck?.Snapshot.QualityBlockedReasons ?? Array.Empty<string>(),
                SourceAwareRankingRepairSourcePath = sourceAwareRankingRepair?.SourcePath ?? string.Empty,
                SourceAwareRankingRepairPassed = sourceAwareRankingRepair?.Snapshot.ReportPassed ?? false,
                SourceAwareRankingRepairGatePassed = sourceAwareRankingRepair?.Snapshot.GatePassed ?? false,
                SourceAwareRankingRepairRecommendation = sourceAwareRankingRepair?.Snapshot.Recommendation ?? string.Empty,
                SourceAwareRankingRepairSelectedProfileId = sourceAwareRankingRepair?.Snapshot.SelectedProfileId ?? string.Empty,
                SourceAwareRankingRepairTrainDevRecallDelta = sourceAwareRankingRepair?.Snapshot.TrainDevRecallDelta ?? 0,
                SourceAwareRankingRepairTestRecallDelta = sourceAwareRankingRepair?.Snapshot.TestRecallDelta ?? 0,
                SourceAwareRankingRepairHoldoutRecallDelta = sourceAwareRankingRepair?.Snapshot.HoldoutRecallDelta ?? 0,
                SourceAwareRankingRepairBlindHoldoutRecallDelta = sourceAwareRankingRepair?.Snapshot.BlindHoldoutRecallDelta ?? 0,
                SourceAwareRankingRepairDenseWinnerLostCount = sourceAwareRankingRepair?.Snapshot.DenseWinnerLostCount ?? 0,
                SourceAwareRankingRepairUniqueSourceRecoveryCount = sourceAwareRankingRepair?.Snapshot.UniqueSourceRecoveryCount ?? 0,
                SourceAwareRankingRepairSourceNoiseCount = sourceAwareRankingRepair?.Snapshot.SourceNoiseCount ?? 0,
                SourceAwareRankingRepairFallbackRate = sourceAwareRankingRepair?.Snapshot.FallbackRate ?? 0,
                SourceAwareRankingRepairRiskAfterPolicy = sourceAwareRankingRepair?.Snapshot.RiskAfterPolicy ?? 0,
                SourceAwareRankingRepairPackageOutputChanged = sourceAwareRankingRepair?.Snapshot.PackageOutputChanged ?? false,
                SourceAwareRankingRepairPackingPolicyChanged = sourceAwareRankingRepair?.Snapshot.PackingPolicyChanged ?? false,
                SourceAwareRankingRepairRuntimeMutated = sourceAwareRankingRepair?.Snapshot.RuntimeMutated ?? false,
                SourceAwareRankingRepairVectorStoreBindingChanged = sourceAwareRankingRepair?.Snapshot.VectorStoreBindingChanged ?? false,
                SourceAwareRankingRepairBlockedReasons = sourceAwareRankingRepair?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                OutputTokenPriorityShadowSourcePath = outputTokenPriorityShadow?.SourcePath ?? string.Empty,
                OutputTokenPriorityShadowPassed = outputTokenPriorityShadow?.Snapshot.ShadowPassed ?? false,
                OutputTokenPriorityShadowGatePassed = outputTokenPriorityShadow?.Snapshot.GatePassed ?? false,
                OutputTokenPriorityShadowRecommendation = outputTokenPriorityShadow?.Snapshot.Recommendation ?? string.Empty,
                OutputTokenPriorityShadowProfileName = outputTokenPriorityShadow?.Snapshot.ProfileName ?? string.Empty,
                OutputTokenPriorityShadowTokenDeltaTotal = outputTokenPriorityShadow?.Snapshot.TokenDeltaTotal ?? 0,
                OutputTokenPriorityShadowTokenDeltaMax = outputTokenPriorityShadow?.Snapshot.TokenDeltaMax ?? 0,
                OutputTokenPriorityShadowTokenDeltaP95 = outputTokenPriorityShadow?.Snapshot.TokenDeltaP95 ?? 0,
                OutputTokenPriorityShadowTokenBudgetExceededCount = outputTokenPriorityShadow?.Snapshot.TokenBudgetExceededCount ?? 0,
                OutputTokenPriorityShadowPriorityInversionCount = outputTokenPriorityShadow?.Snapshot.PriorityInversionCount ?? 0,
                OutputTokenPriorityShadowDroppedRequiredCandidateCount = outputTokenPriorityShadow?.Snapshot.DroppedRequiredCandidateCount ?? 0,
                OutputTokenPriorityShadowSectionMismatchCount = outputTokenPriorityShadow?.Snapshot.SectionMismatchCount ?? 0,
                OutputTokenPriorityShadowRiskAfterPolicy = outputTokenPriorityShadow?.Snapshot.RiskAfterPolicy ?? 0,
                OutputTokenPriorityShadowFormalSelectedSetChanged = outputTokenPriorityShadow?.Snapshot.FormalSelectedSetChanged ?? false,
                OutputTokenPriorityShadowPackageOutputChanged = outputTokenPriorityShadow?.Snapshot.PackageOutputChanged ?? false,
                OutputTokenPriorityShadowPackingPolicyChanged = outputTokenPriorityShadow?.Snapshot.PackingPolicyChanged ?? false,
                OutputTokenPriorityShadowRuntimeMutated = outputTokenPriorityShadow?.Snapshot.RuntimeMutated ?? false,
                OutputTokenPriorityShadowVectorStoreBindingChanged = outputTokenPriorityShadow?.Snapshot.VectorStoreBindingChanged ?? false,
                OutputTokenPriorityShadowBlockedReasons = outputTokenPriorityShadow?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                FormalAdapterInputContractSourcePath = formalAdapterInputContract?.SourcePath ?? string.Empty,
                FormalAdapterInputContractPassed = formalAdapterInputContract?.Snapshot.ContractPassed ?? false,
                FormalAdapterInputContractGatePassed = formalAdapterInputContract?.Snapshot.GatePassed ?? false,
                FormalAdapterInputContractRecommendation = formalAdapterInputContract?.Snapshot.Recommendation ?? string.Empty,
                FormalAdapterInputContractVersion = formalAdapterInputContract?.Snapshot.ContractVersion ?? string.Empty,
                FormalAdapterInputContractRuntimeInputFieldCount = formalAdapterInputContract?.Snapshot.RuntimeInputFieldCount ?? 0,
                FormalAdapterInputContractDeniedFieldCount = formalAdapterInputContract?.Snapshot.DeniedFieldCount ?? 0,
                FormalAdapterInputContractForbiddenPropertyCount = formalAdapterInputContract?.Snapshot.ContractForbiddenPropertyCount ?? 0,
                FormalAdapterInputContractFormalSourceForbiddenReadCount = formalAdapterInputContract?.Snapshot.FormalSourceForbiddenReadCount ?? 0,
                FormalAdapterInputContractEvalOnlyForbiddenReadCount = formalAdapterInputContract?.Snapshot.EvalOnlyForbiddenReadCount ?? 0,
                FormalAdapterInputContractDatasetEvalFieldsBlocked = formalAdapterInputContract?.Snapshot.DatasetEvalFieldsBlocked ?? false,
                FormalAdapterInputContractGoldLabelsBlocked = formalAdapterInputContract?.Snapshot.GoldLabelsBlocked ?? false,
                FormalAdapterInputContractSampleMetadataBlocked = formalAdapterInputContract?.Snapshot.SampleMetadataBlocked ?? false,
                FormalAdapterInputContractShadowArtifactFieldsBlocked = formalAdapterInputContract?.Snapshot.ShadowArtifactFieldsBlocked ?? false,
                FormalAdapterInputContractFormalRetrievalAllowed = formalAdapterInputContract?.Snapshot.FormalRetrievalAllowed ?? false,
                FormalAdapterInputContractRuntimeSwitchAllowed = formalAdapterInputContract?.Snapshot.RuntimeSwitchAllowed ?? false,
                FormalAdapterInputContractRuntimeMutated = formalAdapterInputContract?.Snapshot.RuntimeMutated ?? false,
                FormalAdapterInputContractPackageOutputChanged = formalAdapterInputContract?.Snapshot.PackageOutputChanged ?? false,
                FormalAdapterInputContractPackingPolicyChanged = formalAdapterInputContract?.Snapshot.PackingPolicyChanged ?? false,
                FormalAdapterInputContractVectorStoreBindingChanged = formalAdapterInputContract?.Snapshot.VectorStoreBindingChanged ?? false,
                FormalAdapterInputContractBlockedReasons = formalAdapterInputContract?.Snapshot.BlockedReasons ?? Array.Empty<string>(),
                RetrievalEvalProtocolGateSourcePath = retrievalEvalProtocol?.GateSourcePath ?? string.Empty,
                RetrievalEvalProtocolSourceAuditPath = retrievalEvalProtocol?.SourceAuditPath ?? string.Empty,
                RetrievalEvalProtocolGatePassed = retrievalEvalProtocol?.Gate?.GatePassed ?? false,
                RetrievalEvalProtocolRecommendation = retrievalEvalProtocol?.Gate?.Recommendation ?? string.Empty,
                RetrievalEvalProtocolVersion = retrievalEvalProtocol?.Gate?.ProtocolVersion ?? string.Empty,
                RetrievalEvalProtocolVectorTopK = retrievalEvalProtocol?.Gate?.VectorTopK ?? 0,
                RetrievalEvalProtocolMergedTopK = retrievalEvalProtocol?.Gate?.MergedTopK ?? 0,
                RetrievalEvalProtocolFinalTopK = retrievalEvalProtocol?.Gate?.FinalTopK ?? 0,
                RetrievalEvalProtocolHashOrderSensitivityCount = retrievalEvalProtocol?.Gate?.HashOrderSensitivityCount ?? 0,
                RetrievalEvalProtocolTieBreakDeterministic = retrievalEvalProtocol?.Gate?.TieBreakDeterministic ?? false,
                RetrievalEvalProtocolSourceNonDiscriminativeDetected = retrievalEvalProtocol?.Gate?.SourceNonDiscriminativeDetected ?? false,
                RetrievalEvalProtocolTemplateHomogeneityDetected = retrievalEvalProtocol?.Gate?.TemplateHomogeneityDetected ?? false,
                RetrievalEvalProtocolRuntimeChangeGatePassed = retrievalEvalProtocol?.Gate?.RuntimeChangeGatePassed ?? false,
                RetrievalEvalProtocolRiskAfterPolicy = retrievalEvalProtocol?.Gate?.RiskAfterPolicy ?? 0,
                RetrievalEvalProtocolMustNotHitRiskAfterPolicy = retrievalEvalProtocol?.Gate?.MustNotHitRiskAfterPolicy ?? 0,
                RetrievalEvalProtocolLifecycleRiskAfterPolicy = retrievalEvalProtocol?.Gate?.LifecycleRiskAfterPolicy ?? 0,
                RetrievalEvalProtocolNonDiscriminativeSourceCount = retrievalEvalProtocol?.SourceAudit?.NonDiscriminativeSourceCount ?? 0,
                RetrievalEvalProtocolTemplateHomogeneityScore = retrievalEvalProtocol?.SourceAudit?.TemplateHomogeneityScore ?? 0,
                RetrievalEvalProtocolBaselineRecall = retrievalEvalProtocol?.SourceAudit?.BaselineRecall ?? 0,
                RetrievalEvalProtocolMergedRecall = retrievalEvalProtocol?.SourceAudit?.MergedRecall ?? 0,
                RetrievalEvalProtocolBlockedReasons = retrievalEvalProtocol?.Gate?.BlockedReasons ?? Array.Empty<string>(),
                FormalRetrievalIntegrationFreezeSourcePath = formalRetrievalIntegrationFreeze?.SourcePath ?? string.Empty,
                FormalRetrievalIntegrationFreezePassed = formalRetrievalIntegrationFreeze?.Snapshot.FreezePassed ?? false,
                FormalRetrievalIntegrationFreezeRecommendation = formalRetrievalIntegrationFreeze?.Snapshot.Recommendation ?? string.Empty,
                FormalRetrievalIntegrationFreezeSelectedProfile = formalRetrievalIntegrationFreeze?.Snapshot.SelectedProfile ?? string.Empty,
                FormalRetrievalIntegrationFreezeFrozenArtifactCount = formalRetrievalIntegrationFreeze?.Snapshot.FrozenArtifactCount ?? 0,
                V4GateSatisfied = readinessGate?.Report.Passed ?? IsVectorV4GateSatisfied(recallLoss.A3, recallLoss.Extended)
            };
        }

        return new ServiceVectorShadowQualitySummary
        {
            Available = false,
            CurrentRecommendation = "NoSweepReport"
        };
    }

    private static (VectorResidualRiskAuditReport Report, string SourcePath)? TryLoadVectorResidualRiskReport()
    {
        var candidates = new[]
        {
            Path.Combine("eval", "vector-residual-risk-audit-extended.json"),
            Path.Combine("eval", "vector-residual-risk-audit-a3.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var report = JsonSerializer.Deserialize<VectorResidualRiskAuditReport>(
                    File.ReadAllText(path),
                    JsonOptions);
                if (report is not null)
                {
                    return (report, path);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static (VectorProviderComparisonV310Report Report, string SourcePath)? TryLoadVectorProviderComparisonReport()
    {
        var path = Path.Combine("vector", "providers", "qwen3", "vector-provider-comparison.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorProviderComparisonV310Report>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorQwen3ReadinessGateReport Report, string SourcePath)? TryLoadVectorQwen3ReadinessGateReport()
    {
        var path = Path.Combine("vector", "providers", "qwen3", "vector-qwen3-readiness-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorQwen3ReadinessGateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (EmbeddingProviderComparisonFreezeReport Report, string SourcePath)? TryLoadEmbeddingProviderComparisonFreezeReport()
    {
        var path = Path.Combine("vector", "providers", "qwen3", "vector-provider-comparison-freeze.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<EmbeddingProviderComparisonFreezeReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (HybridRetrievalPreviewReport Report, string SourcePath)? TryLoadVectorHybridPreviewReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-preview.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalPreviewReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (HybridRetrievalReadinessGateReport Report, string SourcePath)? TryLoadVectorHybridReadinessGateReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-readiness-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalReadinessGateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (HybridRetrievalRecallRegressionAuditReport Report, string SourcePath)? TryLoadVectorHybridRecallRegressionAuditReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-recall-regression-audit.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalRecallRegressionAuditReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ContextCoreFoundationFreezeReport?> ReadFoundationFreezeReportAsync(
        CancellationToken cancellationToken)
    {
        foreach (var fileName in new[]
        {
            "foundation-release-candidate-gate.json",
            "foundation-freeze-report.json"
        })
        {
            var path = Path.Combine(
                Directory.GetCurrentDirectory(),
                EvalReportPaths.FoundationOutputDirectory,
                fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var report = JsonSerializer.Deserialize<ContextCoreFoundationFreezeReport>(json, JsonOptions);
                if (report is not null)
                {
                    return report;
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<FoundationReportNavigationResponse?> ReadFoundationReportNavigationAsync(
        CancellationToken cancellationToken)
    {
        if (_state.IsServiceMode)
        {
            try
            {
                return await GetServiceClient()
                    .GetFoundationReportsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ContextCoreApiException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        try
        {
            return await new FoundationStatusService(Directory.GetCurrentDirectory())
                .GetReportNavigationAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationApiSecurityDiagnosticsReport?> ReadFoundationApiSecurityDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-api-security-diagnostics.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationApiSecurityDiagnosticsReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationApiContractReport?> ReadFoundationApiContractReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-api-contract-freeze-gate.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("service", "service-api-contract-report.json");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationApiContractReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationOpenApiContractReport?> ReadFoundationOpenApiContractReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "openapi", "service-api-contract-drift-gate.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("service", "openapi", "service-openapi-contract-report.json");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationOpenApiContractReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static (HybridRetrievalPreviewFreezeReport Report, string SourcePath)? TryLoadVectorHybridFreezeReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-freeze-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalPreviewFreezeReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (RetrievalDatasetAlignmentAuditSummaryReport Report, string SourcePath)? TryLoadVectorRetrievalDatasetAlignmentAuditSummaryReport()
    {
        var path = Path.Combine("vector", "alignment", "vector-retrieval-dataset-alignment-audit-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<RetrievalDatasetAlignmentAuditSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorEligibilityRecallLossTriageSummaryReport Report, string SourcePath)? TryLoadVectorEligibilityRecallLossTriageSummaryReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-eligibility-recall-loss-triage-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorEligibilityRecallLossTriageSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataRepairPlanSummaryReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataRepairPlanSummaryReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-repair-plan-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataRepairPlanSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataReviewCandidateReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataReviewCandidateReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-review-candidates.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewCandidateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataReviewSummaryReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataReviewSummaryReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-review-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataSidecarPreviewReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataSidecarPreviewReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-sidecar-preview.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataSidecarPreviewReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorSidecarEligibilityPreviewReport Report, string SourcePath)? TryLoadVectorSidecarEligibilityQualityReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-sidecar-eligibility-quality.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("vector", "eligibility", "vector-sidecar-eligibility-preview.json");
            if (!File.Exists(path))
            {
                return null;
            }
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorSidecarEligibilityPreviewReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataEvidenceBackfillReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataEvidenceBackfillReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-evidence-backfill-audit.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-evidence-backfill-preview.json");
            if (!File.Exists(path))
            {
                return null;
            }
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataEvidenceBackfillReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int CorpusItemCount, int SampleCount, int ValidationIssueCount, int MissingEvidenceCount, int MissingProvenanceCount, IReadOnlyDictionary<string, int> DifficultyBreakdown, IReadOnlyDictionary<string, int> SplitBreakdown, string Recommendation, string SourcePath)? TryLoadRetrievalDatasetV2GenerationSummary()
    {
        var qualityPath = Path.Combine("vector", "dataset-v2", "generated", "quality-report.json");
        var generationPath = Path.Combine("vector", "dataset-v2", "generated", "generation-report.json");
        var quality = TryReadJson<RetrievalDatasetV2QualityReport>(qualityPath);
        if (quality is not null)
        {
            return (
                quality.CorpusItemCount,
                quality.SampleCount,
                quality.ValidationIssueCount,
                quality.MissingEvidenceCount,
                quality.MissingProvenanceCount,
                quality.DifficultyBreakdown,
                quality.SplitBreakdown,
                quality.Recommendation,
                qualityPath);
        }

        var generation = TryReadJson<RetrievalDatasetV2GenerationReport>(generationPath);
        if (generation is null)
        {
            return null;
        }

        return (
            generation.CorpusItemCount,
            generation.SampleCount,
            generation.ValidationIssueCount,
            generation.MissingEvidenceCount,
            generation.MissingProvenanceCount,
            generation.DifficultyBreakdown,
            generation.SplitBreakdown,
            generation.Recommendation,
            generationPath);
    }

    private static (RetrievalDatasetV2MaterializationReport Report, string SourcePath)? TryLoadRetrievalDatasetV2MaterializationSummary()
    {
        var gatePath = Path.Combine("vector", "dataset-v2", "generated", "materialization-gate.json");
        var report = TryReadJson<RetrievalDatasetV2MaterializationReport>(gatePath);
        if (report is not null)
        {
            return (report, gatePath);
        }

        var materializationPath = Path.Combine("vector", "dataset-v2", "generated", "materialization-report.json");
        report = TryReadJson<RetrievalDatasetV2MaterializationReport>(materializationPath);
        return report is null ? null : (report, materializationPath);
    }

    private static (RetrievalDatasetV2ShadowEvalSummaryReport Summary, RetrievalDatasetV2ReadinessGateReport? Gate, string SourcePath)? TryLoadRetrievalDatasetV2ShadowEvalSummary()
    {
        var summaryPath = Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-shadow-eval-summary.json");
        var summary = TryReadJson<RetrievalDatasetV2ShadowEvalSummaryReport>(summaryPath);
        if (summary is null)
        {
            return null;
        }

        var gatePath = Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-readiness-gate.json");
        var gate = TryReadJson<RetrievalDatasetV2ReadinessGateReport>(gatePath);
        return (summary, gate, gate is null ? summaryPath : gatePath);
    }

    private static (RetrievalDatasetV2StressReport Report, string SourcePath)? TryLoadRetrievalDatasetV2StressSummary()
    {
        var gatePath = Path.Combine("vector", "dataset-v2", "stress", "stress-readiness-gate.json");
        var report = TryReadJson<RetrievalDatasetV2StressReport>(gatePath);
        if (report is not null)
        {
            return (report, gatePath);
        }

        var shadowPath = Path.Combine("vector", "dataset-v2", "stress", "stress-shadow-eval.json");
        report = TryReadJson<RetrievalDatasetV2StressReport>(shadowPath);
        if (report is not null)
        {
            return (report, shadowPath);
        }

        var leakagePath = Path.Combine("vector", "dataset-v2", "stress", "leakage-audit.json");
        report = TryReadJson<RetrievalDatasetV2StressReport>(leakagePath);
        return report is null ? null : (report, leakagePath);
    }

    private static (RetrievalDatasetV2StressRecallFailureTriageReport Report, string SourcePath)? TryLoadRetrievalDatasetV2StressFailureTriageSummary()
    {
        var triagePath = Path.Combine("vector", "dataset-v2", "stress", "stress-failure-triage.json");
        var report = TryReadJson<RetrievalDatasetV2StressRecallFailureTriageReport>(triagePath);
        if (report is not null)
        {
            return (report, triagePath);
        }

        var clustersPath = Path.Combine("vector", "dataset-v2", "stress", "stress-failure-clusters.json");
        report = TryReadJson<RetrievalDatasetV2StressRecallFailureTriageReport>(clustersPath);
        return report is null ? null : (report, clustersPath);
    }

    private static (HybridUnionScoringRepairReport Report, HybridUnionScoringRepairProfileReport? BestProfile, string SourcePath)? TryLoadRetrievalDatasetV2HybridScoringRepairSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-gate.json"),
            Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-shadow-eval.json"),
            Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-preview.json")
        };

        foreach (var path in candidates)
        {
            var report = TryReadJson<HybridUnionScoringRepairReport>(path);
            if (report is null)
            {
                continue;
            }

            var best = report.Profiles
                .FirstOrDefault(profile => string.Equals(profile.ProfileName, report.BestProfileName, StringComparison.OrdinalIgnoreCase));
            if (best is null && report.Profiles.Count > 0)
            {
                best = report.Profiles
                    .Where(static profile => !string.Equals(profile.ProfileName, HybridUnionScoringRepairProfiles.BaselineHybridFull, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static profile => profile.RecallAfterPolicy)
                    .ThenByDescending(static profile => profile.HoldoutRecallAfterPolicy)
                    .ThenBy(static profile => profile.RiskAfterPolicy)
                    .FirstOrDefault();
            }

            return (report, best, path);
        }

        return null;
    }

    private static (HybridScoringRiskRegressionTriageReport Report, string SourcePath)? TryLoadRetrievalDatasetV2HybridScoringRiskTriageSummary()
    {
        var triagePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage.json");
        var report = TryReadJson<HybridScoringRiskRegressionTriageReport>(triagePath);
        if (report is not null)
        {
            return (report, triagePath);
        }

        var holdoutPath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage-holdout.json");
        report = TryReadJson<HybridScoringRiskRegressionTriageReport>(holdoutPath);
        return report is null ? null : (report, holdoutPath);
    }

    private static (RetrievalDatasetV2StressFreezeReport Report, string SourcePath)? TryLoadRetrievalDatasetV2StressFreezeSummary()
    {
        var path = Path.Combine("vector", "dataset-v2", "stress", "stress-freeze-gate.json");
        var report = TryReadJson<RetrievalDatasetV2StressFreezeReport>(path);
        return report is null ? null : (report, path);
    }

    private static (VectorV4ReadinessRecheckReport Report, string SourcePath)? TryLoadVectorV4ReadinessRecheckSummary()
    {
        var path = Path.Combine("vector", "v4", "vector-v4-readiness-recheck.json");
        var report = TryReadJson<VectorV4ReadinessRecheckReport>(path);
        return report is null ? null : (report, path);
    }

    private static (VectorShadowPackageComparisonReport Report, string SourcePath)? TryLoadVectorShadowPackageComparisonSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json"),
            Path.Combine("vector", "v4", "vector-shadow-package-comparison.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<VectorShadowPackageComparisonReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (FormalRetrievalIntegrationPlanReport Report, string SourcePath)? TryLoadFormalRetrievalIntegrationPlanSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "formal-retrieval-integration-plan-gate.json"),
            Path.Combine("vector", "v5", "formal-retrieval-integration-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<FormalRetrievalIntegrationPlanReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (FormalRetrievalIntegrationDecisionSnapshot Snapshot, string SourcePath)? TryLoadFormalRetrievalIntegrationDecisionSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "formal-retrieval-integration-decision-gate.json"),
            Path.Combine("vector", "v5", "formal-retrieval-integration-decision.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new FormalRetrievalIntegrationDecisionSnapshot
                {
                    DecisionPassed = VectorShadowQualitySnapshotReader.GetBool(root, "DecisionPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    ReadyForFormalRetrievalIntegrationFreeze = VectorShadowQualitySnapshotReader.GetBool(root, "ReadyForFormalRetrievalIntegrationFreeze"),
                    ReadyForAdapterNoOpBindingPlan = VectorShadowQualitySnapshotReader.GetBool(root, "ReadyForAdapterNoOpBindingPlan"),
                    FormalRetrievalAllowed = VectorShadowQualitySnapshotReader.GetBool(root, "FormalRetrievalAllowed"),
                    RuntimeSwitchAllowed = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeSwitchAllowed"),
                    ReadyForRuntimeSwitch = VectorShadowQualitySnapshotReader.GetBool(root, "ReadyForRuntimeSwitch"),
                    PackageOutputChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackageOutputChanged"),
                    PackingPolicyChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackingPolicyChanged"),
                    RuntimeMutated = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeMutated"),
                    VectorStoreBindingChanged = VectorShadowQualitySnapshotReader.GetBool(root, "VectorStoreBindingChanged"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    IntegrationDecision = VectorShadowQualitySnapshotReader.GetString(root, "IntegrationDecision"),
                    NextAllowedPhase = VectorShadowQualitySnapshotReader.GetString(root, "NextAllowedPhase"),
                    RiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "RiskAfterPolicy"),
                    FormalOutputChanged = VectorShadowQualitySnapshotReader.GetInt32(root, "FormalOutputChanged"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }

        return null;
    }

    private static (ShadowFormalRetrievalAdapterPlanReport Report, string SourcePath)? TryLoadShadowFormalRetrievalAdapterPlanSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan-gate.json"),
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ShadowFormalRetrievalAdapterPlanReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ShadowFormalRetrievalAdapterReport Report, string SourcePath)? TryLoadShadowFormalRetrievalAdapterSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-gate.json"),
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ShadowFormalRetrievalAdapterReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (FormalAdapterPackageShadowComparisonReport Report, string SourcePath)? TryLoadFormalAdapterPackageShadowComparisonSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison-gate.json"),
            Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<FormalAdapterPackageShadowComparisonReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (GraphVectorRetrievalQualityAuditReport Report, string SourcePath)? TryLoadGraphVectorRetrievalQualityAuditSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "graph-vector-retrieval-quality-gate.json"),
            Path.Combine("vector", "v5", "graph-vector-retrieval-quality-audit.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GraphVectorRetrievalQualityAuditReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RetrievalQualityRepairPreviewReport Report, string SourcePath)? TryLoadRetrievalQualityRepairPreviewSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "retrieval-quality-repair-gate.json"),
            Path.Combine("vector", "v5", "retrieval-quality-repair-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RetrievalQualityRepairPreviewReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RuntimeObservableFeatureContractReport Report, string SourcePath)? TryLoadRuntimeObservableFeatureContractSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "runtime-observable-feature-contract-gate.json"),
            Path.Combine("vector", "v5", "runtime-observable-feature-contract.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RuntimeObservableFeatureContractReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RuntimeRetrievalFeatureDerivationReport Report, string SourcePath)? TryLoadRuntimeRetrievalFeatureDerivationSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json"),
            Path.Combine("vector", "v5", "runtime-feature-derivation-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RuntimeRetrievalFeatureDerivationReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RuntimeFeatureDerivationFailureFreezeReport Report, string SourcePath)? TryLoadRuntimeFeatureDerivationFailureFreezeSummary()
    {
        return TryLoadSummaryReport<RuntimeFeatureDerivationFailureFreezeReport>(
            VectorReportPath("v5", "runtime-feature-derivation-failure-freeze.json"));
    }

    private static (GraphHubNoiseControlReport Report, string SourcePath)? TryLoadGraphHubNoiseControlSummary()
    {
        var candidates = new[] {
            Path.Combine("vector", "v5", "graph-hub-noise-control-gate.json"),
            Path.Combine("vector", "v5", "graph-hub-noise-control-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GraphHubNoiseControlReport>(path);
            if (report is not null) return (report, path);
        }
        return null;
    }

    private static (RetrievalEvalProtocolGateSnapshot? Gate, CandidateSourceDiscriminabilityAuditSnapshot? SourceAudit, string GateSourcePath, string SourceAuditPath)? TryLoadRetrievalEvalProtocolSummary()
    {
        var gatePath = VectorReportPath("v5", "retrieval-eval-protocol-gate.json");
        var auditPath = VectorReportPath("v5", "candidate-source-discriminability-audit.json");
        RetrievalEvalProtocolGateSnapshot? gate = null;
        CandidateSourceDiscriminabilityAuditSnapshot? audit = null;
        if (File.Exists(gatePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(gatePath));
                var root = doc.RootElement;
                gate = new RetrievalEvalProtocolGateSnapshot
                {
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    TieBreakDeterministic = VectorShadowQualitySnapshotReader.GetBool(root, "TieBreakDeterministic"),
                    SourceNonDiscriminativeDetected = VectorShadowQualitySnapshotReader.GetBool(root, "SourceNonDiscriminativeDetected"),
                    TemplateHomogeneityDetected = VectorShadowQualitySnapshotReader.GetBool(root, "TemplateHomogeneityDetected"),
                    RuntimeChangeGatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeChangeGatePassed"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    ProtocolVersion = VectorShadowQualitySnapshotReader.GetNestedString(root, "Protocol", "ProtocolVersion"),
                    VectorTopK = VectorShadowQualitySnapshotReader.GetNestedInt32(root, "Protocol", "VectorTopK"),
                    MergedTopK = VectorShadowQualitySnapshotReader.GetNestedInt32(root, "Protocol", "MergedTopK"),
                    FinalTopK = VectorShadowQualitySnapshotReader.GetNestedInt32(root, "Protocol", "FinalTopK"),
                    HashOrderSensitivityCount = VectorShadowQualitySnapshotReader.GetInt32(root, "HashOrderSensitivityCount"),
                    RiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "RiskAfterPolicy"),
                    MustNotHitRiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "MustNotHitRiskAfterPolicy"),
                    LifecycleRiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "LifecycleRiskAfterPolicy"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons")
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (File.Exists(auditPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(auditPath));
                var root = doc.RootElement;
                audit = new CandidateSourceDiscriminabilityAuditSnapshot
                {
                    NonDiscriminativeSourceCount = VectorShadowQualitySnapshotReader.GetInt32(root, "NonDiscriminativeSourceCount"),
                    TemplateHomogeneityScore = VectorShadowQualitySnapshotReader.GetDouble(root, "TemplateHomogeneityScore"),
                    BaselineRecall = VectorShadowQualitySnapshotReader.GetDouble(root, "BaselineRecall"),
                    MergedRecall = VectorShadowQualitySnapshotReader.GetDouble(root, "MergedRecall")
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (gate is null && audit is null)
        {
            return null;
        }
        return (gate, audit, gate is null ? string.Empty : gatePath, audit is null ? string.Empty : auditPath);
    }

    private static (InputMetadataEnrichmentPreviewSnapshot Snapshot, string SourcePath)? TryLoadInputMetadataEnrichmentSummary()
    {
        var candidates = new[]
        {
            VectorReportPath("v5", "input-metadata-enrichment-gate.json"),
            VectorReportPath("v5", "input-metadata-enrichment-preview.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new InputMetadataEnrichmentPreviewSnapshot
                {
                    PreviewPassed = VectorShadowQualitySnapshotReader.GetBool(root, "PreviewPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    PackageOutputChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackageOutputChanged"),
                    PackingPolicyChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackingPolicyChanged"),
                    RuntimeMutated = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeMutated"),
                    VectorStoreBindingChanged = VectorShadowQualitySnapshotReader.GetBool(root, "VectorStoreBindingChanged"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    MetadataCoverageDelta = VectorShadowQualitySnapshotReader.GetInt32(root, "MetadataCoverageDelta"),
                    IndependentNonDenseSourceCount = VectorShadowQualitySnapshotReader.GetInt32(root, "IndependentNonDenseSourceCount"),
                    RiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "RiskAfterPolicy"),
                    MustNotHitRiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "MustNotHitRiskAfterPolicy"),
                    LifecycleRiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "LifecycleRiskAfterPolicy"),
                    BeforeRecall = VectorShadowQualitySnapshotReader.GetDouble(root, "BeforeRecall"),
                    AfterRecall = VectorShadowQualitySnapshotReader.GetDouble(root, "AfterRecall"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }
        return null;
    }

    private static (EnrichedCandidateSourceRepairRecheckSnapshot Snapshot, string SourcePath)? TryLoadEnrichedCandidateSourceRepairRecheckSummary()
    {
        var candidates = new[]
        {
            VectorReportPath("v5", "enriched-candidate-source-repair-recheck-gate.json"),
            VectorReportPath("v5", "enriched-candidate-source-repair-recheck.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new EnrichedCandidateSourceRepairRecheckSnapshot
                {
                    RecheckPassed = VectorShadowQualitySnapshotReader.GetBool(root, "RecheckPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    QualityImproved = VectorShadowQualitySnapshotReader.GetBool(root, "QualityImproved"),
                    PackageOutputChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackageOutputChanged"),
                    PackingPolicyChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackingPolicyChanged"),
                    RuntimeMutated = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeMutated"),
                    VectorStoreBindingChanged = VectorShadowQualitySnapshotReader.GetBool(root, "VectorStoreBindingChanged"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    MustHitBelowTopKDelta = VectorShadowQualitySnapshotReader.GetInt32(root, "MustHitBelowTopKDelta"),
                    RiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "RiskAfterPolicy"),
                    TrainDerivedRecallDelta = VectorShadowQualitySnapshotReader.GetDouble(root, "TrainDerivedRecallDelta"),
                    HoldoutDerivedRecallDelta = VectorShadowQualitySnapshotReader.GetDouble(root, "HoldoutDerivedRecallDelta"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons"),
                    QualityBlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "QualityBlockedReasons")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }
        return null;
    }

    private static (SourceAwareRankingRepairSnapshot Snapshot, string SourcePath)? TryLoadSourceAwareRankingRepairSummary()
    {
        var candidates = new[]
        {
            VectorReportPath("v5", "source-aware-ranking-repair-gate.json"),
            VectorReportPath("v5", "source-aware-ranking-repair.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new SourceAwareRankingRepairSnapshot
                {
                    ReportPassed = VectorShadowQualitySnapshotReader.GetBool(root, "ReportPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    PackageOutputChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackageOutputChanged"),
                    PackingPolicyChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackingPolicyChanged"),
                    RuntimeMutated = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeMutated"),
                    VectorStoreBindingChanged = VectorShadowQualitySnapshotReader.GetBool(root, "VectorStoreBindingChanged"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    SelectedProfileId = VectorShadowQualitySnapshotReader.GetString(root, "SelectedProfileId"),
                    DenseWinnerLostCount = VectorShadowQualitySnapshotReader.GetInt32(root, "DenseWinnerLostCount"),
                    UniqueSourceRecoveryCount = VectorShadowQualitySnapshotReader.GetInt32(root, "UniqueSourceRecoveryCount"),
                    SourceNoiseCount = VectorShadowQualitySnapshotReader.GetInt32(root, "SourceNoiseCount"),
                    RiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "RiskAfterPolicy"),
                    TrainDevRecallDelta = VectorShadowQualitySnapshotReader.GetDouble(root, "TrainDevRecallDelta"),
                    TestRecallDelta = VectorShadowQualitySnapshotReader.GetDouble(root, "TestRecallDelta"),
                    HoldoutRecallDelta = VectorShadowQualitySnapshotReader.GetDouble(root, "HoldoutRecallDelta"),
                    BlindHoldoutRecallDelta = VectorShadowQualitySnapshotReader.GetDouble(root, "BlindHoldoutRecallDelta"),
                    FallbackRate = VectorShadowQualitySnapshotReader.GetDouble(root, "FallbackRate"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }
        return null;
    }

    private static (OutputTokenPriorityShadowSnapshot Snapshot, string SourcePath)? TryLoadOutputTokenPriorityShadowSummary()
    {
        var candidates = new[]
        {
            VectorReportPath("v5", "output-token-priority-shadow-gate.json"),
            VectorReportPath("v5", "output-token-priority-shadow.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new OutputTokenPriorityShadowSnapshot
                {
                    ShadowPassed = VectorShadowQualitySnapshotReader.GetBool(root, "ShadowPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    FormalSelectedSetChanged = VectorShadowQualitySnapshotReader.GetBool(root, "FormalSelectedSetChanged"),
                    PackageOutputChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackageOutputChanged"),
                    PackingPolicyChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackingPolicyChanged"),
                    RuntimeMutated = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeMutated"),
                    VectorStoreBindingChanged = VectorShadowQualitySnapshotReader.GetBool(root, "VectorStoreBindingChanged"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    ProfileName = VectorShadowQualitySnapshotReader.GetString(root, "ProfileName"),
                    TokenDeltaTotal = VectorShadowQualitySnapshotReader.GetInt32(root, "TokenDeltaTotal"),
                    TokenDeltaMax = VectorShadowQualitySnapshotReader.GetInt32(root, "TokenDeltaMax"),
                    TokenDeltaP95 = VectorShadowQualitySnapshotReader.GetInt32(root, "TokenDeltaP95"),
                    TokenBudgetExceededCount = VectorShadowQualitySnapshotReader.GetInt32(root, "TokenBudgetExceededCount"),
                    PriorityInversionCount = VectorShadowQualitySnapshotReader.GetInt32(root, "PriorityInversionCount"),
                    DroppedRequiredCandidateCount = VectorShadowQualitySnapshotReader.GetInt32(root, "DroppedRequiredCandidateCount"),
                    SectionMismatchCount = VectorShadowQualitySnapshotReader.GetInt32(root, "SectionMismatchCount"),
                    RiskAfterPolicy = VectorShadowQualitySnapshotReader.GetInt32(root, "RiskAfterPolicy"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }
        return null;
    }

    private static (FormalAdapterInputContractSnapshot Snapshot, string SourcePath)? TryLoadFormalAdapterInputContractSummary()
    {
        var candidates = new[]
        {
            VectorReportPath("v5", "formal-adapter-input-contract-gate.json"),
            VectorReportPath("v5", "formal-adapter-input-contract.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new FormalAdapterInputContractSnapshot
                {
                    ContractPassed = VectorShadowQualitySnapshotReader.GetBool(root, "ContractPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    DatasetEvalFieldsBlocked = VectorShadowQualitySnapshotReader.GetBool(root, "DatasetEvalFieldsBlocked"),
                    GoldLabelsBlocked = VectorShadowQualitySnapshotReader.GetBool(root, "GoldLabelsBlocked"),
                    SampleMetadataBlocked = VectorShadowQualitySnapshotReader.GetBool(root, "SampleMetadataBlocked"),
                    ShadowArtifactFieldsBlocked = VectorShadowQualitySnapshotReader.GetBool(root, "ShadowArtifactFieldsBlocked"),
                    FormalRetrievalAllowed = VectorShadowQualitySnapshotReader.GetBool(root, "FormalRetrievalAllowed"),
                    RuntimeSwitchAllowed = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeSwitchAllowed"),
                    RuntimeMutated = VectorShadowQualitySnapshotReader.GetBool(root, "RuntimeMutated"),
                    PackageOutputChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackageOutputChanged"),
                    PackingPolicyChanged = VectorShadowQualitySnapshotReader.GetBool(root, "PackingPolicyChanged"),
                    VectorStoreBindingChanged = VectorShadowQualitySnapshotReader.GetBool(root, "VectorStoreBindingChanged"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    ContractVersion = VectorShadowQualitySnapshotReader.GetString(root, "ContractVersion"),
                    RuntimeInputFieldCount = VectorShadowQualitySnapshotReader.GetInt32(root, "RuntimeInputFieldCount"),
                    DeniedFieldCount = VectorShadowQualitySnapshotReader.GetInt32(root, "DeniedFieldCount"),
                    ContractForbiddenPropertyCount = VectorShadowQualitySnapshotReader.GetInt32(root, "ContractForbiddenPropertyCount"),
                    FormalSourceForbiddenReadCount = VectorShadowQualitySnapshotReader.GetInt32(root, "FormalSourceForbiddenReadCount"),
                    EvalOnlyForbiddenReadCount = VectorShadowQualitySnapshotReader.GetInt32(root, "EvalOnlyForbiddenReadCount"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }
        return null;
    }

    private static (FormalRetrievalIntegrationFreezeSnapshot Snapshot, string SourcePath)? TryLoadFormalRetrievalIntegrationFreezeSummary()
    {
        var candidates = new[]
        {
            VectorReportPath("v5", "formal-retrieval-integration-freeze-gate.json"),
            VectorReportPath("v5", "formal-retrieval-integration-freeze.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (new FormalRetrievalIntegrationFreezeSnapshot
                {
                    FreezePassed = VectorShadowQualitySnapshotReader.GetBool(root, "FreezePassed"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    SelectedProfile = VectorShadowQualitySnapshotReader.GetString(root, "SelectedProfile"),
                    FrozenArtifactCount = VectorShadowQualitySnapshotReader.GetArrayLength(root, "FrozenArtifactPaths")
                }, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
        }
        return null;
    }

    private static (ArchitectureCleanupFreezeReport Report, string SourcePath)? TryLoadArchitectureCleanupFreezeSummary()
        => TryLoadFromDescriptor<ArchitectureCleanupFreezeReport>(ReportSummaryRegistry.OPTArchitectureCleanupFreeze);

    private static (ArchitectureCleanupFreezeGateReport Report, string SourcePath)? TryLoadArchitectureCleanupFreezeGateSummary()
        => TryLoadFromDescriptor<ArchitectureCleanupFreezeGateReport>(ReportSummaryRegistry.OPTArchitectureCleanupFreezeGate);

    private static string VectorReportPath(string phase, string fileName)
    {
        return Path.Combine("vector", phase, fileName);
    }

    private static (T Report, string SourcePath)? TryLoadSummaryReport<T>(params string[] candidatePaths)
    {
        foreach (var path in candidatePaths)
        {
            var report = TryReadJson<T>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (T Report, string SourcePath)? TryLoadFromDescriptor<T>(ControlRoomReportDescriptor descriptor) where T : class
    {
        return TryLoadSummaryReport<T>(descriptor.AllPaths());
    }

    private static (TPrimary? Primary, TSecondary? Secondary, string PrimarySourcePath, string SecondarySourcePath)? TryLoadSummaryPair<TPrimary, TSecondary>(
        string primaryPath,
        string secondaryPath)
        where TPrimary : class
        where TSecondary : class
    {
        var primary = TryReadJson<TPrimary>(primaryPath);
        var secondary = TryReadJson<TSecondary>(secondaryPath);
        if (primary is null && secondary is null)
        {
            return null;
        }

        return (
            primary,
            secondary,
            primary is null ? string.Empty : primaryPath,
            secondary is null ? string.Empty : secondaryPath);
    }

    private static (RuntimeRetrievalFeatureDerivationRepairReport Report, string SourcePath)? TryLoadRuntimeRetrievalFeatureDerivationRepairSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "runtime-feature-derivation-repair-gate.json"),
            Path.Combine("vector", "v5", "runtime-feature-derivation-repair.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RuntimeRetrievalFeatureDerivationRepairReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static RetrievalQualityRepairProfileResult? SelectBestProfile(RetrievalQualityRepairPreviewReport? report)
    {
        if (report is null || string.IsNullOrEmpty(report.BestProfileId))
        {
            return null;
        }

        return report.Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, report.BestProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDatasetV2StressProfileComparisons(RetrievalDatasetV2StressRecallFailureTriageReport? report)
    {
        if (report?.ProfileComparisons is null || report.ProfileComparisons.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", report.ProfileComparisons
            .Take(4)
            .Select(static comparison =>
                $"{comparison.LeftProfileName}:{comparison.LeftRecall:P2}->{comparison.RightProfileName}:{comparison.RightRecall:P2}"));
    }

    private static (VectorLifecycleMetadataReviewBatch Batch, VectorLifecycleMetadataReviewBatchValidationReport? Validation, VectorLifecycleMetadataReviewBatchApplyPreviewReport? ApplyPreview, string SourcePath)? TryLoadVectorLifecycleMetadataReviewBatchSummary()
    {
        var root = Path.Combine("vector", "eligibility", "review-batches");
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var batches = Directory.EnumerateFiles(root, "batch.json", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        var batch = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewBatch>(
                            File.ReadAllText(path),
                            JsonOptions);
                        return batch is null ? null : (Batch: batch, Path: path);
                    }
                    catch (JsonException)
                    {
                        return ((VectorLifecycleMetadataReviewBatch Batch, string Path)?)null;
                    }
                })
                .Where(static item => item is not null)
                .OrderByDescending(static item => item!.Value.Batch.CreatedAt)
                .ToArray();
            var latest = batches.FirstOrDefault();
            if (latest is null)
            {
                return null;
            }

            var directory = Path.GetDirectoryName(latest.Value.Path) ?? root;
            var validation = TryReadJson<VectorLifecycleMetadataReviewBatchValidationReport>(
                Path.Combine(directory, "validation-report.json"));
            var applyPreview = TryReadJson<VectorLifecycleMetadataReviewBatchApplyPreviewReport>(
                Path.Combine(directory, "apply-preview.json"));
            return (latest.Value.Batch, validation, applyPreview, latest.Value.Path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static T? TryReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static double ResolveAlignmentCoverage(
        RetrievalDatasetAlignmentAuditSummaryReport? summary,
        string datasetName,
        bool providerScope)
    {
        var report = summary?.Reports.FirstOrDefault(item =>
            string.Equals(item.DatasetName, datasetName, StringComparison.OrdinalIgnoreCase));
        if (report is null)
        {
            return 0;
        }

        if (report.MustHitCount == 0)
        {
            return 1;
        }

        var covered = providerScope
            ? report.MustHitPresentInProviderScopeCount
            : report.MustHitPresentInCorpusCount;
        return covered / (double)report.MustHitCount;
    }

    private static double ResolveAlignmentAnchorCoverage(RetrievalDatasetAlignmentAuditSummaryReport? summary)
    {
        var reports = summary?.Reports ?? Array.Empty<RetrievalDatasetAlignmentAuditReport>();
        var mustHitCount = reports.Sum(report => report.MustHitCount);
        if (mustHitCount == 0)
        {
            return 0;
        }

        return reports.Sum(report => report.AnchorCoverageRate * report.MustHitCount) / mustHitCount;
    }

    private static (VectorLifecycleMetadataCoverageReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataCoverageReport()
    {
        var path = Path.Combine("eval", "vector-lifecycle-metadata-coverage.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataCoverageReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataBackfillPlan Plan, string SourcePath)? TryLoadVectorLifecycleMetadataBackfillPlan()
    {
        var path = Path.Combine("eval", "vector-lifecycle-metadata-backfill-plan.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<VectorLifecycleMetadataBackfillPlan>(
                File.ReadAllText(path),
                JsonOptions);
            return plan is null ? null : (plan, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorRecallLossAuditReport? A3, string A3SourcePath, VectorRecallLossAuditReport? Extended, string ExtendedSourcePath) TryLoadVectorRecallLossReports()
    {
        var a3Path = Path.Combine("eval", "vector-recall-loss-audit-a3.json");
        var extendedPath = Path.Combine("eval", "vector-recall-loss-audit-extended.json");
        return (
            TryLoadVectorRecallLossReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorRecallLossReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorRecallLossAuditReport? TryLoadVectorRecallLossReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRecallLossAuditReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorSafeRecallRecoveryReport? A3, string A3SourcePath, VectorSafeRecallRecoveryReport? Extended, string ExtendedSourcePath) TryLoadVectorSafeRecallRecoveryReports()
    {
        var a3Path = Path.Combine("eval", "vector-safe-recall-recovery-a3.json");
        var extendedPath = Path.Combine("eval", "vector-safe-recall-recovery-extended.json");
        return (
            TryLoadVectorSafeRecallRecoveryReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorSafeRecallRecoveryReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorSafeRecallRecoveryReport? TryLoadVectorSafeRecallRecoveryReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorSafeRecallRecoveryReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorRankerFusionShadowReport? A3, string A3SourcePath, VectorRankerFusionShadowReport? Extended, string ExtendedSourcePath) TryLoadVectorRankerFusionShadowReports()
    {
        var a3Path = Path.Combine("eval", "vector-ranker-fusion-shadow-a3.json");
        var extendedPath = Path.Combine("eval", "vector-ranker-fusion-shadow-extended.json");
        return (
            TryLoadVectorRankerFusionShadowReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorRankerFusionShadowReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorRankerFusionShadowReport? TryLoadVectorRankerFusionShadowReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRankerFusionShadowReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorRepresentationBenchmarkReport? A3, string A3SourcePath, VectorRepresentationBenchmarkReport? Extended, string ExtendedSourcePath) TryLoadVectorRepresentationBenchmarkReports()
    {
        var a3Path = Path.Combine("eval", "vector-representation-benchmark-a3.json");
        var extendedPath = Path.Combine("eval", "vector-representation-benchmark-extended.json");
        return (
            TryLoadVectorRepresentationBenchmarkReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorRepresentationBenchmarkReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorRepresentationBenchmarkReport? TryLoadVectorRepresentationBenchmarkReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRepresentationBenchmarkReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorQueryExpansionShadowReport? A3, string A3SourcePath, VectorQueryExpansionShadowReport? Extended, string ExtendedSourcePath) TryLoadVectorQueryExpansionShadowReports()
    {
        var a3Path = Path.Combine("eval", "vector-query-expansion-shadow-a3.json");
        var extendedPath = Path.Combine("eval", "vector-query-expansion-shadow-extended.json");
        return (
            TryLoadVectorQueryExpansionShadowReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorQueryExpansionShadowReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorQueryExpansionShadowReport? TryLoadVectorQueryExpansionShadowReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorQueryExpansionShadowReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SelectQueryExpansionBestProfile(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return SelectBestQueryExpansionResult(a3, extended)?.ExpansionProfile ?? string.Empty;
    }

    private static int BuildQueryExpansionRiskSummary(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorQueryExpansionShadowResult>()
            .Sum(item => item.RiskAfterPolicy > 0
                         || item.MustNotHitRiskAfterPolicy > 0
                         || item.LifecycleRiskAfterPolicy > 0
                         || item.NewRiskCount > 0 ? 1 : 0);
    }

    private static int BuildQueryExpansionRecoveredMissSummary(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorQueryExpansionShadowResult>()
            .Sum(item => item.RecoveredMissCount);
    }

    private static bool IsQueryExpansionReadinessSatisfied(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        var a3Best = a3?.BestResult;
        var extendedBest = extended?.BestResult;
        return a3Best is not null
               && extendedBest is not null
               && a3Best.RecallAfterExpansion >= 0.80
               && extendedBest.RecallAfterExpansion >= 0.80
               && a3Best.RiskAfterPolicy == 0
               && extendedBest.RiskAfterPolicy == 0
               && a3Best.MustNotHitRiskAfterPolicy == 0
               && extendedBest.MustNotHitRiskAfterPolicy == 0
               && a3Best.LifecycleRiskAfterPolicy == 0
               && extendedBest.LifecycleRiskAfterPolicy == 0
               && a3Best.NewRiskCount == 0
               && extendedBest.NewRiskCount == 0
               && (a3?.FormalOutputChanged ?? 0) == 0
               && (extended?.FormalOutputChanged ?? 0) == 0;
    }

    private static string SelectRepresentationBestDocumentProfile(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return SelectBestRepresentationResult(a3, extended)?.DocumentRepresentationProfile ?? string.Empty;
    }

    private static string SelectRepresentationBestQueryProfile(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return SelectBestRepresentationResult(a3, extended)?.QueryRepresentationProfile ?? string.Empty;
    }

    private static int BuildRepresentationRiskSummary(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRepresentationBenchmarkResult>()
            .Sum(item => item.RiskAfterPolicy > 0 || item.MustNotHitRisk > 0 || item.LifecycleRisk > 0 || item.NewRiskCount > 0 ? 1 : 0);
    }

    private static int BuildRepresentationRecoveredMissSummary(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRepresentationBenchmarkResult>()
            .Sum(item => item.RecoveredMissCount);
    }

    private static bool IsRepresentationReadinessSatisfied(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        var a3Best = a3?.BestResult;
        var extendedBest = extended?.BestResult;
        return a3Best is not null
               && extendedBest is not null
               && a3Best.Recall >= 0.80
               && extendedBest.Recall >= 0.80
               && a3Best.RiskAfterPolicy == 0
               && extendedBest.RiskAfterPolicy == 0
               && a3Best.MustNotHitRisk == 0
               && extendedBest.MustNotHitRisk == 0
               && a3Best.LifecycleRisk == 0
               && extendedBest.LifecycleRisk == 0
               && a3Best.NewRiskCount == 0
               && extendedBest.NewRiskCount == 0
               && (a3?.FormalOutputChanged ?? 0) == 0
               && (extended?.FormalOutputChanged ?? 0) == 0;
    }

    private static string SelectFusionBestStrategy(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        var candidates = new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRankerFusionStrategyResult>()
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.MustNotHitRiskFusion)
            .ThenBy(item => item.LifecycleRiskFusion)
            .ThenByDescending(item => item.MustHitRecallFusion)
            .ThenByDescending(item => item.MustHitMrrFusion)
            .ToArray();
        return candidates.FirstOrDefault()?.Strategy ?? string.Empty;
    }

    private static int BuildFusionRiskSummary(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRankerFusionStrategyResult>()
            .Sum(item => item.MustNotHitRiskFusion > 0 || item.LifecycleRiskFusion > 0 || item.NewlyRiskySamples.Count > 0 ? 1 : 0);
    }

    private static double BuildFusionRecallGainSummary(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        var gains = new[] { a3?.BestResult?.RecallGain, extended?.BestResult?.RecallGain }
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return gains.Length == 0 ? 0 : gains.Average();
    }

    private static bool IsFusionReadinessSatisfied(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        var a3Best = a3?.BestResult;
        var extendedBest = extended?.BestResult;
        return a3Best is not null
               && extendedBest is not null
               && a3Best.MustHitRecallFusion >= 0.80
               && extendedBest.MustHitRecallFusion >= 0.80
               && a3Best.MustNotHitRiskFusion == 0
               && extendedBest.MustNotHitRiskFusion == 0
               && a3Best.LifecycleRiskFusion == 0
               && extendedBest.LifecycleRiskFusion == 0
               && a3Best.NewlyRiskySamples.Count == 0
               && extendedBest.NewlyRiskySamples.Count == 0
               && (a3?.FormalOutputChanged ?? 0) == 0
               && (extended?.FormalOutputChanged ?? 0) == 0;
    }

    private static (VectorRetrievalShadowReadinessGateReport Report, string SourcePath)? TryLoadVectorReadinessGateReport()
    {
        var path = Path.Combine("eval", "vector-retrieval-shadow-readiness-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorRetrievalShadowReadinessGateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, int> MergeMissReasons(
        VectorRecallLossAuditReport? a3,
        VectorRecallLossAuditReport? extended)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in new[] { a3, extended }.Where(item => item is not null))
        {
            foreach (var pair in report!.MissReasonCounts)
            {
                merged[pair.Key] = merged.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        return merged
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildIntentReadinessSummary(
        VectorRecallLossAuditReport? a3,
        VectorRecallLossAuditReport? extended)
    {
        return new[] { ("A3", a3), ("Extended", extended) }
            .Where(item => item.Item2 is not null)
            .SelectMany(item => item.Item2!.IntentReadiness.Buckets
                .OrderBy(bucket => bucket.Key, StringComparer.OrdinalIgnoreCase)
                .Select(bucket => $"{item.Item1}:{bucket.Key}={bucket.Recommendation}"))
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, int> MergeBlockedMustHitClassifications(
        VectorSafeRecallRecoveryReport? a3,
        VectorSafeRecallRecoveryReport? extended)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in new[] { a3, extended }.Where(item => item is not null))
        {
            foreach (var pair in report!.BlockedClassificationCounts)
            {
                merged[pair.Key] = merged.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        return merged
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsVectorV4GateSatisfied(
        VectorRecallLossAuditReport? a3,
        VectorRecallLossAuditReport? extended)
    {
        return a3 is not null
               && extended is not null
               && a3.RiskAfterPolicy == 0
               && extended.RiskAfterPolicy == 0
               && string.Equals(a3.Recommendation, VectorQueryShadowRecommendations.ReadyForRetrievalShadow, StringComparison.OrdinalIgnoreCase)
               && string.Equals(extended.Recommendation, VectorQueryShadowRecommendations.ReadyForRetrievalShadow, StringComparison.OrdinalIgnoreCase);
    }
}
