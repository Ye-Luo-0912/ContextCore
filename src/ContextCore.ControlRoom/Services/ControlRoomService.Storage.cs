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
                var readinessGate = TryLoadVectorReadinessGateReport();
                var providerComparison = TryLoadVectorProviderComparisonReport();
                var qwen3ReadinessGate = TryLoadVectorQwen3ReadinessGateReport();
                var providerComparisonFreeze = TryLoadEmbeddingProviderComparisonFreezeReport();
                var hybridPreview = TryLoadVectorHybridPreviewReport();
                var hybridGate = TryLoadVectorHybridReadinessGateReport();
                var hybridAudit = TryLoadVectorHybridRecallRegressionAuditReport();
                var hybridFreeze = TryLoadVectorHybridFreezeReport();
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
                    OperationalReports = BuildOperationalReports(),
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
                    V4GateSatisfied = readinessGate?.Report.Passed ?? false
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
            var readinessGate = TryLoadVectorReadinessGateReport();
            var providerComparison = TryLoadVectorProviderComparisonReport();
            var qwen3ReadinessGate = TryLoadVectorQwen3ReadinessGateReport();
            var providerComparisonFreeze = TryLoadEmbeddingProviderComparisonFreezeReport();
            var hybridPreview = TryLoadVectorHybridPreviewReport();
            var hybridGate = TryLoadVectorHybridReadinessGateReport();
            var hybridAudit = TryLoadVectorHybridRecallRegressionAuditReport();
            var hybridFreeze = TryLoadVectorHybridFreezeReport();
            return new ServiceVectorShadowQualitySummary
            {
                Available = true,
                SourcePath = residualOnly.Value.SourcePath,
                CurrentRecommendation = residualOnly.Value.Report.Recommendation,
                OperationalReports = BuildOperationalReports(),
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
                V4GateSatisfied = readinessGate?.Report.Passed ?? false
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

    private static (ArchitectureCleanupFreezeReport Report, string SourcePath)? TryLoadArchitectureCleanupFreezeSummary()
        => TryLoadFromDescriptor<ArchitectureCleanupFreezeReport>(ReportSummaryRegistry.OPTArchitectureCleanupFreeze);

    private static (ArchitectureCleanupFreezeGateReport Report, string SourcePath)? TryLoadArchitectureCleanupFreezeGateSummary()
        => TryLoadFromDescriptor<ArchitectureCleanupFreezeGateReport>(ReportSummaryRegistry.OPTArchitectureCleanupFreezeGate);

    private static string VectorReportPath(string phase, string fileName)
    {
        return Path.Combine("vector", phase, fileName);
    }

    private static OperationalReportSnapshot TryLoadOperationalReport(
        string reportKey, string title, params string[] candidatePaths)
    {
        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return new OperationalReportSnapshot
                {
                    ReportKey = reportKey,
                    DisplayTitle = title,
                    SourcePath = path,
                    Available = true,
                    Passed = VectorShadowQualitySnapshotReader.GetBool(root, "Passed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "FreezePassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "AuditPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "PreviewPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "RecheckPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "ReportPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "ShadowPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "ContractPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "DecisionPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "PlanPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "AdapterPassed")
                             || VectorShadowQualitySnapshotReader.GetBool(root, "ComparisonPassed"),
                    GatePassed = VectorShadowQualitySnapshotReader.GetBool(root, "GatePassed"),
                    Recommendation = VectorShadowQualitySnapshotReader.GetString(root, "Recommendation"),
                    BlockedReasons = VectorShadowQualitySnapshotReader.GetStringArray(root, "BlockedReasons"),
                    KeyMetrics = ExtractKeyMetrics(root)
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }

        return new OperationalReportSnapshot { ReportKey = reportKey, DisplayTitle = title };
    }

    private static IReadOnlyDictionary<string, string> ExtractKeyMetrics(JsonElement root)
    {
        var metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Passed", "GatePassed", "FreezePassed", "AuditPassed", "PreviewPassed",
            "RecheckPassed", "ReportPassed", "ShadowPassed", "ContractPassed",
            "DecisionPassed", "PlanPassed", "AdapterPassed", "ComparisonPassed",
            "Recommendation", "BlockedReasons", "Status", "SourcePath",
            "ProfileName", "BestProfileId", "BestProfileName", "SelectedProfileId",
            "SelectedProfile", "AllowedMode", "NextAllowedPhase", "RequiredNextPhase",
            "IntegrationDecision", "IntegrationPoints", "ForbiddenActions",
            "DatasetId", "CorpusHash", "SamplesHash", "BatchId",
            "ProtocolVersion", "ContractVersion", "PromotionStatus",
            "HybridRetrievalStatus", "DatasetV2Stress", "LegacyVectorStatus",
            "BestPreviewProfile", "ProfileComparisons", "FailureClusters",
            "Risks", "Reports", "Profiles", "Variants", "RecentCandidates",
            "DifficultyBreakdown", "SplitBreakdown", "IssueBreakdown",
            "CountByLayer", "CountByItemKind", "RiskAfterPolicyByType",
            "IntentReadiness", "MissReasonCounts", "BlockedClassificationCounts",
            "NewlyRiskySamples", "FlaggedTokens", "FrozenArtifactPaths",
            "SourceScan", "Baseline", "Protocol", "BestResult", "BestSafeSweep"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (metrics.Count >= 8)
            {
                break;
            }

            var name = property.Name;
            if (excluded.Contains(name))
            {
                continue;
            }

            var value = property.Value;
            string? metricValue = value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number when value.TryGetInt32(out var i) => i.ToString(),
                JsonValueKind.Number => value.GetDouble().ToString("F4"),
                JsonValueKind.String => value.GetString(),
                _ => null
            };

            if (!string.IsNullOrEmpty(metricValue))
            {
                metrics[name] = metricValue;
            }
        }

        return metrics;
    }

    private static IReadOnlyList<OperationalReportSnapshot> BuildOperationalReports()
    {
        var reports = new List<OperationalReportSnapshot>
        {
            TryLoadOperationalReport(
                "V4.LifecycleBackfillPlan",
                "V4 Lifecycle Metadata Backfill Plan",
                Path.Combine("eval", "vector-lifecycle-metadata-backfill-plan.json")),
            TryLoadOperationalReport(
                "V4.RecallLoss",
                "V4 Recall Loss / Intent Readiness",
                Path.Combine("eval", "vector-recall-loss-audit-a3.json"),
                Path.Combine("eval", "vector-recall-loss-audit-extended.json")),
            TryLoadOperationalReport(
                "V4.SafeRecallRecovery",
                "V4 Safe Recall Recovery",
                Path.Combine("eval", "vector-safe-recall-recovery-a3.json"),
                Path.Combine("eval", "vector-safe-recall-recovery-extended.json")),
            TryLoadOperationalReport(
                "V4.FusionShadow",
                "V4 Ranker Fusion Shadow",
                Path.Combine("eval", "vector-ranker-fusion-shadow-a3.json"),
                Path.Combine("eval", "vector-ranker-fusion-shadow-extended.json")),
            TryLoadOperationalReport(
                "V4.RepresentationBenchmark",
                "V4 Representation Benchmark",
                Path.Combine("eval", "vector-representation-benchmark-a3.json"),
                Path.Combine("eval", "vector-representation-benchmark-extended.json")),
            TryLoadOperationalReport(
                "V4.QueryExpansionShadow",
                "V4 Query Expansion Shadow",
                Path.Combine("eval", "vector-query-expansion-shadow-a3.json"),
                Path.Combine("eval", "vector-query-expansion-shadow-extended.json")),
            TryLoadOperationalReport(
                "V5.DatasetAlignmentAudit",
                "Dataset Alignment Audit Summary",
                Path.Combine("vector", "alignment", "vector-retrieval-dataset-alignment-audit-summary.json")),
            TryLoadOperationalReport(
                "V5.EligibilityRecallLossTriage",
                "Eligibility Recall Loss Triage Summary",
                Path.Combine("vector", "eligibility", "vector-eligibility-recall-loss-triage-summary.json")),
            TryLoadOperationalReport(
                "V5.LifecycleMetadataRepairPlan",
                "Lifecycle Metadata Repair Plan Summary",
                Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-repair-plan-summary.json")),
            TryLoadOperationalReport(
                "V5.LifecycleMetadataReviewCandidates",
                "Lifecycle Metadata Review Candidates",
                Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-review-candidates.json")),
            TryLoadOperationalReport(
                "V5.LifecycleMetadataReviewSummary",
                "Lifecycle Metadata Review Summary",
                Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-review-summary.json")),
            TryLoadOperationalReport(
                "V5.LifecycleMetadataSidecarPreview",
                "Lifecycle Metadata Sidecar Preview",
                Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-sidecar-preview.json")),
            TryLoadOperationalReport(
                "V5.SidecarEligibilityQuality",
                "Sidecar-aware Eligibility Preview",
                Path.Combine("vector", "eligibility", "vector-sidecar-eligibility-quality.json"),
                Path.Combine("vector", "eligibility", "vector-sidecar-eligibility-preview.json")),
            TryLoadOperationalReport(
                "V5.LifecycleMetadataEvidenceBackfill",
                "Lifecycle Metadata Evidence Backfill",
                Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-evidence-backfill-audit.json"),
                Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-evidence-backfill-preview.json")),
            TryLoadOperationalReport(
                "V5.LifecycleMetadataReviewBatch",
                "Lifecycle Metadata Review Batch",
                Path.Combine("vector", "eligibility", "review-batches", "batch.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2Generation",
                "Retrieval Dataset V2 Generation",
                Path.Combine("vector", "dataset-v2", "generated", "quality-report.json"),
                Path.Combine("vector", "dataset-v2", "generated", "generation-report.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2Materialization",
                "Retrieval Dataset V2 Materialization",
                Path.Combine("vector", "dataset-v2", "generated", "materialization-gate.json"),
                Path.Combine("vector", "dataset-v2", "generated", "materialization-report.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2ShadowEval",
                "Retrieval Dataset V2 Shadow Eval",
                Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-shadow-eval-summary.json"),
                Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-readiness-gate.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2Stress",
                "Retrieval Dataset V2 Stress",
                Path.Combine("vector", "dataset-v2", "stress", "stress-readiness-gate.json"),
                Path.Combine("vector", "dataset-v2", "stress", "stress-shadow-eval.json"),
                Path.Combine("vector", "dataset-v2", "stress", "leakage-audit.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2StressFailureTriage",
                "Retrieval Dataset V2 Stress Failure Triage",
                Path.Combine("vector", "dataset-v2", "stress", "stress-failure-triage.json"),
                Path.Combine("vector", "dataset-v2", "stress", "stress-failure-clusters.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2HybridScoringRepair",
                "Retrieval Dataset V2 Hybrid Scoring Repair",
                Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-gate.json"),
                Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-shadow-eval.json"),
                Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-preview.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2HybridScoringRiskTriage",
                "Retrieval Dataset V2 Hybrid Scoring Risk Triage",
                Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage.json"),
                Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage-holdout.json")),
            TryLoadOperationalReport(
                "V5.RetrievalDatasetV2StressFreeze",
                "Retrieval Dataset V2 Stress Freeze",
                Path.Combine("vector", "dataset-v2", "stress", "stress-freeze-gate.json")),
            TryLoadOperationalReport(
                "V5.VectorV4ReadinessRecheck",
                "Vector V4 Readiness Recheck",
                Path.Combine("vector", "v4", "vector-v4-readiness-recheck.json")),
            TryLoadOperationalReport(
                "V5.VectorShadowPackageComparison",
                "Vector Shadow Package Comparison",
                Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json"),
                Path.Combine("vector", "v4", "vector-shadow-package-comparison.json")),
            TryLoadOperationalReport(
                "V5.FormalRetrievalIntegrationPlan",
                "Formal Retrieval Integration Plan",
                Path.Combine("vector", "v5", "formal-retrieval-integration-plan-gate.json"),
                Path.Combine("vector", "v5", "formal-retrieval-integration-plan.json")),
            TryLoadOperationalReport(
                "V5.FormalRetrievalIntegrationDecision",
                "Formal Retrieval Integration Decision",
                Path.Combine("vector", "v5", "formal-retrieval-integration-decision-gate.json"),
                Path.Combine("vector", "v5", "formal-retrieval-integration-decision.json")),
            TryLoadOperationalReport(
                "V5.ShadowFormalRetrievalAdapterPlan",
                "Shadow Formal Retrieval Adapter Plan",
                Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan-gate.json"),
                Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan.json")),
            TryLoadOperationalReport(
                "V5.ShadowFormalRetrievalAdapter",
                "Shadow Formal Retrieval Adapter",
                Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-gate.json"),
                Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter.json")),
            TryLoadOperationalReport(
                "V5.FormalAdapterPackageShadowComparison",
                "Formal Adapter Package Shadow Comparison",
                Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison-gate.json"),
                Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison.json")),
            TryLoadOperationalReport(
                "V5.GraphVectorRetrievalQualityAudit",
                "Graph Vector Retrieval Quality Audit",
                Path.Combine("vector", "v5", "graph-vector-retrieval-quality-gate.json"),
                Path.Combine("vector", "v5", "graph-vector-retrieval-quality-audit.json")),
            TryLoadOperationalReport(
                "V5.RetrievalQualityRepairPreview",
                "Retrieval Quality Repair Preview",
                Path.Combine("vector", "v5", "retrieval-quality-repair-gate.json"),
                Path.Combine("vector", "v5", "retrieval-quality-repair-preview.json")),
            TryLoadOperationalReport(
                "V5.RuntimeObservableFeatureContract",
                "Runtime Observable Feature Contract",
                Path.Combine("vector", "v5", "runtime-observable-feature-contract-gate.json"),
                Path.Combine("vector", "v5", "runtime-observable-feature-contract.json")),
            TryLoadOperationalReport(
                "V5.RuntimeRetrievalFeatureDerivation",
                "Runtime Retrieval Feature Derivation",
                Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json"),
                Path.Combine("vector", "v5", "runtime-feature-derivation-preview.json")),
            TryLoadOperationalReport(
                "V5.RuntimeRetrievalFeatureDerivationRepair",
                "Runtime Retrieval Feature Derivation Repair",
                Path.Combine("vector", "v5", "runtime-feature-derivation-repair-gate.json"),
                Path.Combine("vector", "v5", "runtime-feature-derivation-repair.json")),
            TryLoadOperationalReport(
                "V5.FeatureDerivationFailureFreeze",
                "Feature Derivation Failure Freeze",
                VectorReportPath("v5", "runtime-feature-derivation-failure-freeze.json")),
            TryLoadOperationalReport(
                "V5.GraphHubNoiseControl",
                "Graph Hub Noise Control",
                Path.Combine("vector", "v5", "graph-hub-noise-control-gate.json"),
                Path.Combine("vector", "v5", "graph-hub-noise-control-preview.json")),
            TryLoadOperationalReport(
                "V5.RetrievalEvalProtocol",
                "Retrieval Eval Protocol Gate",
                VectorReportPath("v5", "retrieval-eval-protocol-gate.json"),
                VectorReportPath("v5", "candidate-source-discriminability-audit.json")),
            TryLoadOperationalReport(
                "V5.InputMetadataEnrichment",
                "Input Metadata Enrichment Preview",
                VectorReportPath("v5", "input-metadata-enrichment-gate.json"),
                VectorReportPath("v5", "input-metadata-enrichment-preview.json")),
            TryLoadOperationalReport(
                "V5.EnrichedCandidateSourceRepairRecheck",
                "Enriched Candidate Source Repair Recheck",
                VectorReportPath("v5", "enriched-candidate-source-repair-recheck-gate.json"),
                VectorReportPath("v5", "enriched-candidate-source-repair-recheck.json")),
            TryLoadOperationalReport(
                "V5.SourceAwareRankingRepair",
                "Source Aware Ranking Repair",
                VectorReportPath("v5", "source-aware-ranking-repair-gate.json"),
                VectorReportPath("v5", "source-aware-ranking-repair.json")),
            TryLoadOperationalReport(
                "V5.OutputTokenPriorityShadow",
                "Output Token Priority Shadow",
                VectorReportPath("v5", "output-token-priority-shadow-gate.json"),
                VectorReportPath("v5", "output-token-priority-shadow.json")),
            TryLoadOperationalReport(
                "V5.FormalAdapterInputContract",
                "Formal Adapter Input Contract",
                VectorReportPath("v5", "formal-adapter-input-contract-gate.json"),
                VectorReportPath("v5", "formal-adapter-input-contract.json")),
            TryLoadOperationalReport(
                "V5.FormalRetrievalIntegrationFreeze",
                "Formal Retrieval Integration Freeze",
                VectorReportPath("v5", "formal-retrieval-integration-freeze-gate.json"),
                VectorReportPath("v5", "formal-retrieval-integration-freeze.json")),
            TryLoadOperationalReport(
                "V5.ArchitectureCleanupFreeze",
                "Architecture Cleanup Freeze",
                ReportSummaryRegistry.OPTArchitectureCleanupFreeze.AllPaths().ToArray()),
            TryLoadOperationalReport(
                "V5.ArchitectureCleanupFreezeGate",
                "Architecture Cleanup Freeze Gate",
                ReportSummaryRegistry.OPTArchitectureCleanupFreezeGate.AllPaths().ToArray())
        };

        return reports;
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
