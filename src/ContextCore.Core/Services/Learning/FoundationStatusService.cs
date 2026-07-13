using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Frozen foundation 的只读状态聚合器；只读取报告文件，不改变 runtime/provider/package 状态。
/// </summary>
public sealed class FoundationStatusService
{
    public const string EnvelopeSchemaVersion = "foundation-api-envelope-v1";
    public const string FoundationReleaseCandidatePath = "foundation/foundation-release-candidate-gate.json";
    public const string FoundationReproducibilityPath = "foundation/foundation-reproducibility-check.json";
    public const string RuntimeChangeGatePath = "learning/readiness/learning-runtime-change-readiness-gate.json";
    public const string VectorFormalPreviewFreezePath = "vector/v4/vector-formal-preview-freeze-gate.json";
    public const string RelationGovernanceFreezePath = "storage/postgres/postgres-relation-multi-normal-scope-quality-report.json";
    public const string LearningFeedbackFreezePath = "storage/postgres/postgres-learning-feedback-freeze-gate.json";
    public const string JobQueueFreezePath = "storage/postgres/postgres-job-queue-freeze-gate.json";
    public const string VectorPostgresFreezePath = "storage/postgres/postgres-vector-freeze-gate.json";
    public const string ServiceFoundationStatusSmokePath = "foundation/service-foundation-status-smoke.json";
    public const string ServiceReadinessApiSmokePath = "foundation/service-readiness-api-smoke.json";
    public const string ServiceApiSecurityDiagnosticsPath = "service/service-api-security-diagnostics.json";
    public const string ServiceReportNavigationSmokePath = "service/service-report-navigation-smoke.json";
    public const string ServiceApiContractFreezeGatePath = "service/service-api-contract-freeze-gate.json";
    public const string ServiceDeploymentProfileGatePath = "service/service-deployment-profile-gate.json";
    public const string ServiceApiContractDriftGatePath = "service/openapi/service-api-contract-drift-gate.json";
    public const string ServiceHostedDeploymentSmokePath = "service/hosted/service-hosted-deployment-smoke.json";
    public const string ServiceReadonlyRuntimeSmokePath = "service/hosted/service-readonly-runtime-smoke.json";
    public const string ServiceHostedApiContractSmokePath = "service/hosted/service-hosted-api-contract-smoke.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyList<ReportDefinition> ReportDefinitions =
    [
        new("foundation-release-candidate-gate", "ContextCoreFoundation", FoundationReleaseCandidatePath),
        new("foundation-reproducibility-check", "FoundationReproducibility", FoundationReproducibilityPath),
        new("learning-runtime-change-readiness-gate", "RuntimeChangeGate", RuntimeChangeGatePath),
        new("vector-formal-preview-freeze-gate", ShadowCapabilityIds.VectorFormalPreviewFreeze, VectorFormalPreviewFreezePath),
        new("postgres-relation-freeze", ShadowCapabilityIds.RelationGovernance, RelationGovernanceFreezePath),
        new("postgres-learning-feedback-freeze-gate", "LearningFeedbackPostgres", LearningFeedbackFreezePath),
        new("postgres-job-queue-freeze-gate", ShadowCapabilityIds.JobQueuePostgres, JobQueueFreezePath),
        new("postgres-vector-freeze-gate", ShadowCapabilityIds.VectorPostgresProvider, VectorPostgresFreezePath)
    ];

    private static readonly IReadOnlyList<string> EnvelopeSchemaFields =
    [
        "Success",
        "CapabilityId",
        "Status",
        "Recommendation",
        "Data",
        "Diagnostics",
        "GeneratedAt",
        "SchemaVersion"
    ];

    private static readonly IReadOnlyList<string> ReportNavigationSchemaFields =
    [
        "ReportId",
        "CapabilityId",
        "RelativePath",
        "Exists",
        "GeneratedAt",
        "ContentType",
        "Summary",
        "SafeToExpose"
    ];

    private static readonly IReadOnlyList<FoundationApiEndpointContract> EndpointContracts =
    [
        new() { Method = "GET", Route = "/api/admin/foundation/status", CapabilityId = "foundation.readonly.status", ResponseType = "FoundationServiceStatusResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/reports", CapabilityId = "foundation.report.navigation", ResponseType = "FoundationReportNavigationResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/reports/{reportId}", CapabilityId = "foundation.report.navigation", ResponseType = "FoundationReportNavigationEntry" }
    ];

    private static readonly IReadOnlyList<FoundationApiClientMethodContract> ClientMethodContracts =
    [
        new() { MethodName = "GetFoundationStatusAsync", Route = "/api/admin/foundation/status", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationReportsAsync", Route = "/api/admin/foundation/reports", ResponseType = "FoundationReportNavigationResponse" },
        new() { MethodName = "GetFoundationReportAsync", Route = "/api/admin/foundation/reports/{reportId}", ResponseType = "FoundationReportNavigationEntry" }
    ];

    private static readonly IReadOnlyList<FoundationApiClientMethodContract> ClientAliasMethodContracts =
    [
    ];

    private static readonly IReadOnlyList<string> CapabilityStatusSchemaFields =
    [
        "CapabilityId",
        "DisplayName",
        "Category",
        "State",
        "Recommendation",
        "GatePassed",
        "UseForRuntime",
        "FormalRetrievalAllowed",
        "RuntimeSwitchAllowed",
        "ReadyForRuntimeSwitch",
        "PackingPolicyChanged",
        "PackageOutputChanged",
        "SourceReportPath",
        "AllowedModes",
        "ForbiddenModes",
        "BlockedReasons"
    ];

    private static readonly IReadOnlyList<string> RequiredForbiddenActions =
    [
        "RuntimeSwitch",
        "FormalRetrieval",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutation",
        "NonAllowlistedScopeUse"
    ];

    private readonly string _rootDirectory;

    public FoundationStatusService(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
    }

    public async Task<FoundationApiResponseEnvelope<FoundationServiceStatusResponse>> GetStatusEnvelopeAsync(
        string statusKind = "foundation/status",
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(statusKind, cancellationToken).ConfigureAwait(false);
        return BuildEnvelope(
            capabilityId: "foundation.readonly.status",
            data: status,
            recommendationWhenReady: "ReadOnlyStatusAvailable",
            missingReportIds: GetMissingReportIds(status.ReportCoverage));
    }

    public async Task<FoundationServiceStatusResponse> GetStatusAsync(
        string statusKind = "foundation/status",
        CancellationToken cancellationToken = default)
    {
        var foundation = await ReadJsonAsync<ContextCoreFoundationFreezeReport>(FoundationReleaseCandidatePath, cancellationToken)
            .ConfigureAwait(false);
        var reproducibility = await ReadJsonAsync<FoundationReproducibilityReport>(FoundationReproducibilityPath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonAsync<LearningRuntimeChangeReadinessGateReport>(RuntimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var vectorFormal = await ReadJsonAsync<VectorFormalPreviewFreezeReport>(VectorFormalPreviewFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var relation = await ReadJsonAsync<PostgresRelationMultiNormalScopeCanaryReport>(RelationGovernanceFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var learningFeedback = await ReadJsonAsync<LearningFeedbackPostgresFreezeGateReport>(LearningFeedbackFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var jobQueue = await ReadJsonAsync<JobQueuePostgresFreezeGateReport>(JobQueueFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var vectorPostgres = await ReadJsonAsync<VectorPostgresProviderFreezeGateReport>(VectorPostgresFreezePath, cancellationToken)
            .ConfigureAwait(false);

        var capabilities = new List<CapabilityStatus>
        {
            BuildFoundationCapability(foundation),
            BuildReproducibilityCapability(reproducibility),
            BuildRuntimeGateCapability(runtimeGate),
            BuildVectorFormalCapability(vectorFormal),
            BuildRelationCapability(relation),
            BuildLearningFeedbackCapability(learningFeedback),
            BuildJobQueueCapability(jobQueue),
            BuildVectorPostgresCapability(vectorPostgres)
        };

        var formalRetrievalAllowed = capabilities.Any(static item => item.FormalRetrievalAllowed);
        var runtimeSwitchAllowed = capabilities.Any(static item => item.RuntimeSwitchAllowed);
        var readyForRuntimeSwitch = capabilities.Any(static item => item.ReadyForRuntimeSwitch);
        var packingPolicyChanged = capabilities.Any(static item => item.PackingPolicyChanged);
        var packageOutputChanged = capabilities.Any(static item => item.PackageOutputChanged);
        var runtimeMutated = vectorFormal?.RuntimeMutated == true;
        var formalPackageWritten = vectorFormal?.FormalPackageWritten == true;
        var blocked = capabilities
            .SelectMany(static item => item.BlockedReasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FoundationServiceStatusResponse
        {
            OperationId = $"service-foundation-status-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            StatusKind = statusKind,
            FoundationGateStatus = ToPassedStatus(foundation?.FreezePassed == true),
            RuntimeChangeGateStatus = ToPassedStatus(runtimeGate?.Passed == true),
            ReproducibilityStatus = ToPassedStatus(reproducibility?.ReproducibilityPassed == true),
            VectorFormalPreviewStatus = ToPassedStatus(vectorFormal?.FreezePassed == true),
            PostgresFreezeStatus = ToPassedStatus(
                relation?.GatePassed == true
                && learningFeedback?.Passed == true
                && jobQueue?.Passed == true
                && vectorPostgres?.Passed == true),
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            RuntimeMutated = runtimeMutated,
            FormalPackageWritten = formalPackageWritten,
            Capabilities = capabilities,
            ReportCoverage = BuildReportCoverage(),
            BlockedReasons = blocked
        };
    }

    public async Task<FoundationReportNavigationResponse> GetReportNavigationAsync(
        CancellationToken cancellationToken = default)
    {
        var reports = new List<FoundationReportNavigationEntry>(ReportDefinitions.Count);
        foreach (var definition in ReportDefinitions)
        {
            reports.Add(await BuildReportNavigationEntryAsync(definition, cancellationToken).ConfigureAwait(false));
        }

        var missing = reports
            .Where(static item => !item.Exists)
            .Select(static item => item.ReportId)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FoundationReportNavigationResponse
        {
            OperationId = $"service-report-navigation-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            ReportCount = reports.Count,
            ExistingReportCount = reports.Count(static item => item.Exists),
            DegradedReportCount = missing.Length,
            MissingReportIds = missing,
            Reports = reports,
            Recommendation = missing.Length == 0 ? "ReadyForReadOnlyReportNavigation" : "RegenerateReport"
        };
    }

    public async Task<FoundationApiResponseEnvelope<FoundationReportNavigationResponse>> GetReportNavigationEnvelopeAsync(
        CancellationToken cancellationToken = default)
    {
        var navigation = await GetReportNavigationAsync(cancellationToken).ConfigureAwait(false);
        return BuildEnvelope(
            capabilityId: "foundation.report.navigation",
            data: navigation,
            recommendationWhenReady: navigation.Recommendation,
            missingReportIds: navigation.MissingReportIds);
    }

    public async Task<FoundationApiResponseEnvelope<FoundationReportNavigationEntry>> GetReportNavigationEntryEnvelopeAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reportId))
        {
            return BuildEnvelope<FoundationReportNavigationEntry>(
                capabilityId: "foundation.report.navigation",
                data: null,
                recommendationWhenReady: "ReportIdRequired",
                missingReportIds: ["ReportIdRequired"],
                success: false,
                explicitStatus: "NotFound");
        }

        var definition = ReportDefinitions.FirstOrDefault(item =>
            string.Equals(item.ReportId, reportId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return BuildEnvelope<FoundationReportNavigationEntry>(
                capabilityId: "foundation.report.navigation",
                data: null,
                recommendationWhenReady: "UnknownReportId",
                missingReportIds: [reportId],
                success: false,
                explicitStatus: "NotFound");
        }

        var entry = await BuildReportNavigationEntryAsync(definition, cancellationToken).ConfigureAwait(false);
        return BuildEnvelope(
            capabilityId: entry.CapabilityId,
            data: entry,
            recommendationWhenReady: entry.Exists ? "ReadyForReadOnlyReportNavigation" : "RegenerateReport",
            missingReportIds: entry.Exists ? [] : [entry.ReportId]);
    }

    private CapabilityStatus BuildFoundationCapability(ContextCoreFoundationFreezeReport? report)
        => new()
        {
            CapabilityId = "ContextCoreFoundation",
            DisplayName = "ContextCore Foundation Release Candidate",
            Category = "foundation",
            State = report?.ContextCoreFoundation ?? "MissingReport",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.FreezePassed == true,
            UseForRuntime = false,
            FormalRetrievalAllowed = report?.FormalRetrievalAllowed == true,
            RuntimeSwitchAllowed = report?.RuntimeSwitchAllowed == true,
            ReadyForRuntimeSwitch = report?.ReadyForRuntimeSwitch == true,
            PackingPolicyChanged = report?.PackingPolicyChanged == true,
            PackageOutputChanged = report?.PackageOutputChanged == true,
            SourceReportPath = FoundationReleaseCandidatePath,
            AllowedModes = ["ReadOnlyStatus"],
            ForbiddenModes = ["RuntimeSwitch", "FormalRetrieval", "FormalPackageWrite", "PackingPolicyMutation"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingFoundationReleaseCandidateGate"]
        };

    private CapabilityStatus BuildReproducibilityCapability(FoundationReproducibilityReport? report)
        => new()
        {
            CapabilityId = "FoundationReproducibility",
            DisplayName = "Foundation Reproducibility Check",
            Category = "foundation",
            State = report?.ReproducibilityPassed == true ? "Passed" : "MissingOrFailed",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.ReproducibilityPassed == true,
            UseForRuntime = false,
            SourceReportPath = FoundationReproducibilityPath,
            AllowedModes = ["ReadOnlyStatus"],
            ForbiddenModes = ["RuntimeSwitch", "FormalRetrieval"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingFoundationReproducibilityCheck"]
        };

    private CapabilityStatus BuildRuntimeGateCapability(LearningRuntimeChangeReadinessGateReport? report)
        => new()
        {
            CapabilityId = "RuntimeChangeGate",
            DisplayName = "Learning Runtime Change Gate",
            Category = "runtime-gate",
            State = report?.Passed == true ? "Passed" : "MissingOrFailed",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.Passed == true,
            UseForRuntime = false,
            SourceReportPath = RuntimeChangeGatePath,
            AllowedModes = ["ReadOnlyStatus"],
            ForbiddenModes = ["RuntimeSwitch", "GlobalDefaultOn", "FormalRetrieval"],
            BlockedReasons = report?.FailedConditions ?? ["MissingRuntimeChangeGate"]
        };

    private CapabilityStatus BuildVectorFormalCapability(VectorFormalPreviewFreezeReport? report)
        => new()
        {
            CapabilityId = ShadowCapabilityIds.VectorFormalPreviewFreeze,
            DisplayName = "Vector Formal Preview Freeze",
            Category = "vector-formal-preview",
            State = report?.VectorFormalPreview ?? "MissingReport",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.FreezePassed == true,
            UseForRuntime = report?.UseForRuntime == true,
            FormalRetrievalAllowed = report?.FormalRetrievalAllowed == true,
            RuntimeSwitchAllowed = report?.RuntimeSwitchAllowed == true,
            ReadyForRuntimeSwitch = report?.ReadyForRuntimeSwitch == true,
            PackingPolicyChanged = report?.PackingPolicyChanged == true,
            PackageOutputChanged = report?.PackageOutputChanged == true,
            SourceReportPath = VectorFormalPreviewFreezePath,
            AllowedModes = ["ScopedPreviewOnly", "ReadOnlyStatus"],
            ForbiddenModes = ["RuntimeSwitch", "FormalRetrieval", "FormalPackageWrite", "PackingPolicyMutation"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingVectorFormalPreviewFreeze"]
        };

    private CapabilityStatus BuildRelationCapability(PostgresRelationMultiNormalScopeCanaryReport? report)
        => new()
        {
            CapabilityId = ShadowCapabilityIds.RelationGovernance,
            DisplayName = "Relation Governance Postgres Freeze",
            Category = "storage-freeze",
            State = report?.Recommendation ?? "MissingReport",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.GatePassed == true,
            UseForRuntime = false,
            SourceReportPath = RelationGovernanceFreezePath,
            AllowedModes = ["GuardedPostgresPrimaryForAllowlistedScopes", "ReadOnlyStatus"],
            ForbiddenModes = ["GlobalDefaultOn"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingRelationGovernanceFreeze"]
        };

    private CapabilityStatus BuildLearningFeedbackCapability(LearningFeedbackPostgresFreezeGateReport? report)
        => new()
        {
            CapabilityId = "LearningFeedbackPostgres",
            DisplayName = "Learning Feedback Postgres Freeze",
            Category = "storage-freeze",
            State = report?.LearningFeedbackPostgres ?? "MissingReport",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.Passed == true,
            UseForRuntime = false,
            SourceReportPath = LearningFeedbackFreezePath,
            AllowedModes = ["GuardedPostgresPrimaryForAllowlistedScopes", "ReadOnlyStatus"],
            ForbiddenModes = ["GlobalDefaultOn", "AutoTraining", "AutoReadinessChange"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingLearningFeedbackFreeze"]
        };

    private CapabilityStatus BuildJobQueueCapability(JobQueuePostgresFreezeGateReport? report)
        => new()
        {
            CapabilityId = ShadowCapabilityIds.JobQueuePostgres,
            DisplayName = "Job Queue Postgres Freeze",
            Category = "storage-freeze",
            State = report?.JobQueuePostgres ?? "MissingReport",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.Passed == true,
            UseForRuntime = false,
            SourceReportPath = JobQueueFreezePath,
            AllowedModes = ["GuardedPostgresPrimaryForAllowlistedWorkerScopes", "ReadOnlyStatus"],
            ForbiddenModes = ["GlobalWorkerProviderSwitch", "ProductionWorkerLoopSwitchWithoutGate"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingJobQueueFreeze"]
        };

    private CapabilityStatus BuildVectorPostgresCapability(VectorPostgresProviderFreezeGateReport? report)
        => new()
        {
            CapabilityId = ShadowCapabilityIds.VectorPostgresProvider,
            DisplayName = "Vector Postgres Provider Freeze",
            Category = "storage-freeze",
            State = report?.VectorPostgresProvider ?? "MissingReport",
            Recommendation = report?.Recommendation ?? "MissingReport",
            GatePassed = report?.Passed == true,
            UseForRuntime = report?.UseForRuntime == true,
            FormalRetrievalAllowed = report?.FormalRetrievalAllowed == true,
            SourceReportPath = VectorPostgresFreezePath,
            AllowedModes = ["Preview", "Shadow", "Eval", "ReadOnlyStatus"],
            ForbiddenModes = ["FormalRetrievalSwitch", "PackingPolicyIntegrationWithoutV4Gate"],
            BlockedReasons = report?.BlockedReasons ?? ["MissingVectorPostgresFreeze"]
        };

    private IReadOnlyDictionary<string, bool> BuildReportCoverage()
        => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [FoundationReleaseCandidatePath] = File.Exists(ResolvePath(FoundationReleaseCandidatePath)),
            [FoundationReproducibilityPath] = File.Exists(ResolvePath(FoundationReproducibilityPath)),
            [RuntimeChangeGatePath] = File.Exists(ResolvePath(RuntimeChangeGatePath)),
            [VectorFormalPreviewFreezePath] = File.Exists(ResolvePath(VectorFormalPreviewFreezePath)),
            [RelationGovernanceFreezePath] = File.Exists(ResolvePath(RelationGovernanceFreezePath)),
            [LearningFeedbackFreezePath] = File.Exists(ResolvePath(LearningFeedbackFreezePath)),
            [JobQueueFreezePath] = File.Exists(ResolvePath(JobQueueFreezePath)),
            [VectorPostgresFreezePath] = File.Exists(ResolvePath(VectorPostgresFreezePath))
        };

    private async Task<FoundationReportNavigationEntry> BuildReportNavigationEntryAsync(
        ReportDefinition definition,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeRelativePath(definition.RelativePath);
        var fullPath = ResolvePath(relativePath);
        var exists = File.Exists(fullPath);
        var contentType = relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? "text/markdown"
            : "application/json";

        string summary;
        DateTimeOffset? generatedAt = null;
        if (exists)
        {
            (summary, generatedAt) = await ReadReportSummaryAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            summary = "Missing report; regenerate the corresponding eval artifact.";
        }

        return new FoundationReportNavigationEntry
        {
            ReportId = definition.ReportId,
            CapabilityId = definition.CapabilityId,
            RelativePath = relativePath,
            Exists = exists,
            GeneratedAt = generatedAt,
            ContentType = contentType,
            Summary = SanitizeSummary(summary),
            SafeToExpose = IsSafeRelativeReportPath(relativePath)
        };
    }

    private async Task<(string Summary, DateTimeOffset? GeneratedAt)> ReadReportSummaryAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(fullPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var generatedAt = TryGetDateTimeOffset(root, "generatedAt")
                    ?? TryGetDateTimeOffset(root, "GeneratedAt");
                var recommendation = TryGetString(root, "recommendation")
                    ?? TryGetString(root, "Recommendation")
                    ?? "Report available";
                var status = TryGetString(root, "status")
                    ?? TryGetString(root, "Status")
                    ?? TryGetString(root, "ContextCoreFoundation")
                    ?? TryGetString(root, "VectorFormalPreview")
                    ?? TryGetString(root, "JobQueuePostgres")
                    ?? TryGetString(root, "VectorPostgresProvider");
                return (string.IsNullOrWhiteSpace(status)
                    ? recommendation
                    : $"{status}; {recommendation}", generatedAt);
            }

            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var firstLine = lines.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line)) ?? "Report available";
            return (firstLine.TrimStart('#', ' '), File.GetLastWriteTimeUtc(fullPath));
        }
        catch (JsonException)
        {
            return ("Report exists but JSON summary could not be parsed.", File.GetLastWriteTimeUtc(fullPath));
        }
        catch (IOException)
        {
            return ("Report exists but could not be read.", null);
        }
    }

    private FoundationApiResponseEnvelope<T> BuildEnvelope<T>(
        string capabilityId,
        T? data,
        string recommendationWhenReady,
        IReadOnlyList<string> missingReportIds,
        bool success = true,
        string? explicitStatus = null)
    {
        var diagnostics = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (missingReportIds.Count > 0)
        {
            diagnostics["MissingReportIds"] = missingReportIds;
        }

        var status = explicitStatus
            ?? (missingReportIds.Count > 0 ? "Degraded" : "Ready");
        var recommendation = status.Equals("Degraded", StringComparison.OrdinalIgnoreCase)
            ? "RegenerateReport"
            : recommendationWhenReady;

        return new FoundationApiResponseEnvelope<T>
        {
            Success = success,
            CapabilityId = capabilityId,
            Status = status,
            Recommendation = recommendation,
            Data = data,
            Diagnostics = diagnostics,
            GeneratedAt = DateTimeOffset.UtcNow,
            SchemaVersion = EnvelopeSchemaVersion
        };
    }

    private async Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        var path = ResolvePath(relativePath);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private string ResolvePath(string relativePath)
        => Path.Combine(_rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<string> GetMissingReportIds(IReadOnlyDictionary<string, bool> coverage)
        => coverage
            .Where(static item => !item.Value)
            .Select(item => ReportDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.RelativePath, item.Key, StringComparison.OrdinalIgnoreCase))?.ReportId ?? item.Key)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsSafeRelativeReportPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains("..", StringComparison.Ordinal)
            || ContainsAbsolutePathLeak(path)
            || ContainsSecretPathLeak(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var sanitized = summary.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (ContainsAbsolutePathLeak(sanitized) || ContainsSecretPathLeak(sanitized))
        {
            return "Summary redacted because it contained a local path.";
        }

        return sanitized.Length <= 220 ? sanitized : sanitized[..220];
    }

    private static bool ContainsAbsolutePathLeak(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains(@":\", StringComparison.Ordinal)
            || value.Contains(":/", StringComparison.Ordinal)
            || value.Contains("/home/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/", StringComparison.Ordinal);
    }

    private static bool ContainsSecretPathLeak(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains(".contextcore", StringComparison.OrdinalIgnoreCase)
            || value.Contains("secrets.json", StringComparison.OrdinalIgnoreCase)
            || value.Contains("model_int8.onnx", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".onnx", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), out var value)
                ? value
                : null;

    private static string ToPassedStatus(bool passed)
        => passed ? "Passed" : "MissingOrFailed";

    private sealed record ReportDefinition(
        string ReportId,
        string CapabilityId,
        string RelativePath);
}
