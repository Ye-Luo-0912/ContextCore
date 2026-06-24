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
        new() { Method = "GET", Route = "/api/admin/foundation/release-candidate", CapabilityId = "foundation.readonly.status", ResponseType = "FoundationServiceStatusResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/reproducibility", CapabilityId = "foundation.readonly.status", ResponseType = "FoundationServiceStatusResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/runtime-change-gate", CapabilityId = "foundation.readonly.status", ResponseType = "FoundationServiceStatusResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/vector-formal-preview", CapabilityId = "foundation.readonly.status", ResponseType = "FoundationServiceStatusResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/postgres-freeze-status", CapabilityId = "foundation.readonly.status", ResponseType = "FoundationServiceStatusResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/reports", CapabilityId = "foundation.report.navigation", ResponseType = "FoundationReportNavigationResponse" },
        new() { Method = "GET", Route = "/api/admin/foundation/reports/{reportId}", CapabilityId = "foundation.report.navigation", ResponseType = "FoundationReportNavigationEntry" }
    ];

    private static readonly IReadOnlyList<FoundationApiClientMethodContract> ClientMethodContracts =
    [
        new() { MethodName = "GetFoundationStatusAsync", Route = "/api/admin/foundation/status", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationReleaseCandidateAsync", Route = "/api/admin/foundation/release-candidate", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationReproducibilityAsync", Route = "/api/admin/foundation/reproducibility", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetRuntimeChangeGateAsync", Route = "/api/admin/foundation/runtime-change-gate", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetVectorFormalPreviewStatusAsync", Route = "/api/admin/foundation/vector-formal-preview", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetPostgresFreezeStatusAsync", Route = "/api/admin/foundation/postgres-freeze-status", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationReportsAsync", Route = "/api/admin/foundation/reports", ResponseType = "FoundationReportNavigationResponse" },
        new() { MethodName = "GetFoundationReportAsync", Route = "/api/admin/foundation/reports/{reportId}", ResponseType = "FoundationReportNavigationEntry" }
    ];

    private static readonly IReadOnlyList<FoundationApiClientMethodContract> ClientAliasMethodContracts =
    [
        new() { MethodName = "GetFoundationReleaseCandidateStatusAsync", Route = "/api/admin/foundation/release-candidate", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationReproducibilityStatusAsync", Route = "/api/admin/foundation/reproducibility", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationRuntimeChangeGateStatusAsync", Route = "/api/admin/foundation/runtime-change-gate", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationVectorFormalPreviewStatusAsync", Route = "/api/admin/foundation/vector-formal-preview", ResponseType = "FoundationServiceStatusResponse" },
        new() { MethodName = "GetFoundationPostgresFreezeStatusAsync", Route = "/api/admin/foundation/postgres-freeze-status", ResponseType = "FoundationServiceStatusResponse" }
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

    public FoundationApiSecurityDiagnosticsReport BuildSecurityDiagnostics(
        bool requireApiKey,
        bool apiKeyConfigured,
        bool developmentMode,
        IEnumerable<string>? serializedResponses = null,
        string? secretProbe = null)
    {
        var diagnostics = new List<string>();
        var authConfigured = requireApiKey && apiKeyConfigured;
        if (!requireApiKey)
        {
            diagnostics.Add("DevelopmentOnlyAuthDisabled");
        }
        else if (!apiKeyConfigured)
        {
            diagnostics.Add("ApiKeyRequiredButMissing");
        }

        var payload = string.Join('\n', serializedResponses ?? Array.Empty<string>());
        var secretLeak = !string.IsNullOrWhiteSpace(secretProbe)
            && payload.Contains(secretProbe, StringComparison.Ordinal);
        var absolutePathLeak = ContainsAbsolutePathLeak(payload);
        if (secretLeak)
        {
            diagnostics.Add("SecretLeakDetected");
        }

        if (absolutePathLeak)
        {
            diagnostics.Add("AbsolutePathLeakDetected");
        }

        var recommendation = authConfigured && !secretLeak && !absolutePathLeak
            ? "ReadyForReadOnlyServiceExposure"
            : !requireApiKey && !secretLeak && !absolutePathLeak
                ? "DevelopmentOnly"
                : "NotConfigured";

        return new FoundationApiSecurityDiagnosticsReport
        {
            OperationId = $"service-api-security-diagnostics-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            AuthConfigured = authConfigured,
            ApiKeyConfigured = apiKeyConfigured,
            DevelopmentMode = developmentMode || !requireApiKey,
            SecretLeakDetected = secretLeak,
            AbsolutePathLeakDetected = absolutePathLeak,
            Recommendation = recommendation,
            Diagnostics = diagnostics
        };
    }

    public FoundationServiceAuthDiagnosticsReport BuildAuthDiagnostics(
        FoundationServiceAuthOptions options,
        bool apiKeyConfigured,
        IEnumerable<string>? serializedResponses = null,
        string? secretProbe = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = string.Join('\n', serializedResponses ?? Array.Empty<string>());
        var secretLeak = !string.IsNullOrWhiteSpace(secretProbe)
            && payload.Contains(secretProbe, StringComparison.Ordinal);
        var absolutePathLeak = ContainsAbsolutePathLeak(payload) || ContainsSecretPathLeak(payload);
        var diagnostics = new List<string>();
        var developmentNoAuthAllowed = options.DeploymentProfile == ServiceDeploymentProfile.Development
            && options.AllowDevelopmentNoAuth
            && !options.RequireApiKey;
        var authConfigured = options.RequireApiKey && apiKeyConfigured;

        if (!options.Enabled)
        {
            diagnostics.Add("AuthOptionsDisabled");
        }

        if (developmentNoAuthAllowed)
        {
            diagnostics.Add("DevelopmentOnlyAuthDisabled");
        }

        if (options.DeploymentProfile != ServiceDeploymentProfile.Development
            && options.RequireApiKey
            && !apiKeyConfigured)
        {
            diagnostics.Add("ApiKeyRequiredButMissing");
        }

        if (options.DeploymentProfile == ServiceDeploymentProfile.Production && !authConfigured)
        {
            diagnostics.Add("ProductionAuthNotConfigured");
        }

        if (secretLeak)
        {
            diagnostics.Add("SecretLeakDetected");
        }

        if (absolutePathLeak)
        {
            diagnostics.Add("AbsolutePathLeakDetected");
        }

        var blocked = diagnostics
            .Where(item => !string.Equals(item, "DevelopmentOnlyAuthDisabled", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recommendation = BuildAuthRecommendation(options, authConfigured, developmentNoAuthAllowed, blocked);

        return new FoundationServiceAuthDiagnosticsReport
        {
            OperationId = $"service-auth-diagnostics-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DeploymentProfile = options.DeploymentProfile,
            AuthConfigured = authConfigured,
            ApiKeyConfigured = apiKeyConfigured,
            RequireApiKey = options.RequireApiKey,
            ApiKeyHeaderName = string.IsNullOrWhiteSpace(options.ApiKeyHeaderName)
                ? "X-ContextCore-Key"
                : options.ApiKeyHeaderName,
            DevelopmentNoAuthAllowed = developmentNoAuthAllowed,
            SecretLeakDetected = secretLeak,
            AbsolutePathLeakDetected = absolutePathLeak,
            RuntimeMutated = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            Recommendation = recommendation,
            Diagnostics = diagnostics,
            BlockedReasons = blocked
        };
    }

    public FoundationServiceAuthEnforcementSmokeReport BuildAuthEnforcementSmokeReport(
        FoundationServiceAuthDiagnosticsReport developmentDiagnostics,
        FoundationServiceAuthDiagnosticsReport serviceMissingDiagnostics,
        FoundationServiceAuthDiagnosticsReport serviceConfiguredDiagnostics,
        FoundationServiceAuthDiagnosticsReport productionMissingDiagnostics,
        bool wrongApiKeyUnauthorized,
        bool correctApiKeyAvailable)
    {
        ArgumentNullException.ThrowIfNull(developmentDiagnostics);
        ArgumentNullException.ThrowIfNull(serviceMissingDiagnostics);
        ArgumentNullException.ThrowIfNull(serviceConfiguredDiagnostics);
        ArgumentNullException.ThrowIfNull(productionMissingDiagnostics);

        var blocked = new List<string>();
        AddIfFalse(blocked, developmentDiagnostics.DevelopmentNoAuthAllowed, "DevelopmentNoAuthNotAllowed");
        AddIfFalse(blocked, serviceMissingDiagnostics.BlockedReasons.Contains("ApiKeyRequiredButMissing", StringComparer.OrdinalIgnoreCase), "ServiceMissingApiKeyNotBlocked");
        AddIfFalse(blocked, serviceConfiguredDiagnostics.AuthConfigured, "ServiceConfiguredApiKeyNotPassed");
        AddIfFalse(blocked, productionMissingDiagnostics.BlockedReasons.Contains("ProductionAuthNotConfigured", StringComparer.OrdinalIgnoreCase), "ProductionMissingAuthNotBlocked");
        AddIfFalse(blocked, wrongApiKeyUnauthorized, "WrongApiKeyNotUnauthorized");
        AddIfFalse(blocked, correctApiKeyAvailable, "CorrectApiKeyNotAvailable");
        AddIfFalse(blocked, !developmentDiagnostics.RuntimeMutated
            && !serviceMissingDiagnostics.RuntimeMutated
            && !serviceConfiguredDiagnostics.RuntimeMutated
            && !productionMissingDiagnostics.RuntimeMutated, "RuntimeMutated");
        AddIfFalse(blocked, !developmentDiagnostics.SecretLeakDetected
            && !serviceMissingDiagnostics.SecretLeakDetected
            && !serviceConfiguredDiagnostics.SecretLeakDetected
            && !productionMissingDiagnostics.SecretLeakDetected, "SecretLeakDetected");
        AddIfFalse(blocked, !developmentDiagnostics.AbsolutePathLeakDetected
            && !serviceMissingDiagnostics.AbsolutePathLeakDetected
            && !serviceConfiguredDiagnostics.AbsolutePathLeakDetected
            && !productionMissingDiagnostics.AbsolutePathLeakDetected, "AbsolutePathLeakDetected");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FoundationServiceAuthEnforcementSmokeReport
        {
            OperationId = $"service-auth-enforcement-smoke-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            SmokePassed = distinctBlocked.Length == 0,
            DevelopmentNoAuthAllowed = developmentDiagnostics.DevelopmentNoAuthAllowed,
            ServiceMissingApiKeyBlocked = serviceMissingDiagnostics.BlockedReasons.Contains("ApiKeyRequiredButMissing", StringComparer.OrdinalIgnoreCase),
            ServiceConfiguredApiKeyPassed = serviceConfiguredDiagnostics.AuthConfigured,
            ProductionMissingAuthBlocked = productionMissingDiagnostics.BlockedReasons.Contains("ProductionAuthNotConfigured", StringComparer.OrdinalIgnoreCase),
            WrongApiKeyUnauthorized = wrongApiKeyUnauthorized,
            CorrectApiKeyAvailable = correctApiKeyAvailable,
            RuntimeMutated = false,
            SecretLeakDetected = false,
            AbsolutePathLeakDetected = false,
            Recommendation = distinctBlocked.Length == 0 ? "ReadyForDeploymentProfileGate" : "BlockedByAuthEnforcementBug",
            BlockedReasons = distinctBlocked
        };
    }

    public FoundationServiceDeploymentProfileGateReport BuildDeploymentProfileGateReport(
        FoundationServiceAuthDiagnosticsReport diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var blocked = diagnostics.BlockedReasons
            .Concat(diagnostics.RuntimeMutated ? ["RuntimeMutated"] : Array.Empty<string>())
            .Concat(diagnostics.FormalRetrievalAllowed ? ["FormalRetrievalAllowed"] : Array.Empty<string>())
            .Concat(diagnostics.RuntimeSwitchAllowed ? ["RuntimeSwitchAllowed"] : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = blocked.Length == 0;

        return new FoundationServiceDeploymentProfileGateReport
        {
            OperationId = $"service-deployment-profile-gate-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            GatePassed = passed,
            DeploymentProfile = diagnostics.DeploymentProfile,
            AuthConfigured = diagnostics.AuthConfigured,
            ApiKeyConfigured = diagnostics.ApiKeyConfigured,
            RequireApiKey = diagnostics.RequireApiKey,
            DevelopmentNoAuthAllowed = diagnostics.DevelopmentNoAuthAllowed,
            SecretLeakDetected = diagnostics.SecretLeakDetected,
            AbsolutePathLeakDetected = diagnostics.AbsolutePathLeakDetected,
            RuntimeMutated = diagnostics.RuntimeMutated,
            FormalRetrievalAllowed = diagnostics.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = diagnostics.RuntimeSwitchAllowed,
            Recommendation = passed ? "ReadyForServiceDeploymentProfile" : diagnostics.Recommendation,
            BlockedReasons = blocked
        };
    }

    public ServiceReportNavigationSmokeReport BuildReportNavigationSmokeReport(
        FoundationApiResponseEnvelope<FoundationReportNavigationResponse> navigation,
        FoundationApiResponseEnvelope<FoundationReportNavigationEntry> firstEntry)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(firstEntry);

        var serialized = JsonSerializer.Serialize(new { navigation, firstEntry }, JsonOptions);
        var blocked = new List<string>();
        AddIfFalse(blocked, navigation.Data is not null, "NavigationDataMissing");
        AddIfFalse(blocked, navigation.SchemaVersion == "foundation-api-envelope-v1", "EnvelopeSchemaMismatch");
        AddIfFalse(blocked, !ContainsAbsolutePathLeak(serialized), "AbsolutePathLeakDetected");
        AddIfFalse(blocked, !ContainsSecretPathLeak(serialized), "SecretPathLeakDetected");
        AddIfFalse(blocked, navigation.Data?.Reports.All(static item => item.SafeToExpose) == true, "UnsafeReportPath");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ServiceReportNavigationSmokeReport
        {
            OperationId = $"service-report-navigation-smoke-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            SmokePassed = distinctBlocked.Length == 0,
            ReportCount = navigation.Data?.ReportCount ?? 0,
            DegradedReportCount = navigation.Data?.DegradedReportCount ?? 0,
            AbsolutePathLeakDetected = distinctBlocked.Contains("AbsolutePathLeakDetected", StringComparer.OrdinalIgnoreCase),
            SecretLeakDetected = distinctBlocked.Contains("SecretPathLeakDetected", StringComparer.OrdinalIgnoreCase),
            EnvelopeSchemaStable = navigation.SchemaVersion == "foundation-api-envelope-v1",
            Recommendation = distinctBlocked.Length == 0 ? "ReadyForReadOnlyReportNavigation" : "BlockedByReportNavigationLeak",
            BlockedReasons = distinctBlocked
        };
    }

    public async Task<FoundationApiContractReport> BuildContractReportAsync(
        FoundationApiSecurityDiagnosticsReport securityDiagnostics,
        bool productionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityDiagnostics);

        var status = await GetStatusAsync("foundation/status", cancellationToken).ConfigureAwait(false);
        var navigation = await GetReportNavigationEnvelopeAsync(cancellationToken).ConfigureAwait(false);
        var missingProbeRoot = Path.Combine(_rootDirectory, ".foundation-contract-missing-report-probe");
        var missingProbe = await new FoundationStatusService(missingProbeRoot)
            .GetStatusEnvelopeAsync("foundation/status", cancellationToken)
            .ConfigureAwait(false);

        return BuildContractReport(status, navigation, missingProbe, securityDiagnostics, productionMode);
    }

    public FoundationApiContractReport BuildContractReport(
        FoundationServiceStatusResponse status,
        FoundationApiResponseEnvelope<FoundationReportNavigationResponse> navigation,
        FoundationApiResponseEnvelope<FoundationServiceStatusResponse> missingReportProbe,
        FoundationApiSecurityDiagnosticsReport securityDiagnostics,
        bool productionMode)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(missingReportProbe);
        ArgumentNullException.ThrowIfNull(securityDiagnostics);

        var serializedContract = JsonSerializer.Serialize(new
        {
            navigation,
            missingReportProbe,
            securityDiagnostics
        }, JsonOptions);
        var forbiddenActionsExposed = RequiredForbiddenActions.All(required =>
            status.Capabilities.Any(capability =>
                capability.ForbiddenModes.Any(mode => string.Equals(mode, required, StringComparison.OrdinalIgnoreCase)))
            || RequiredForbiddenActions.Any(action => string.Equals(action, required, StringComparison.OrdinalIgnoreCase)));
        var missingReportReturnsDegraded = string.Equals(missingReportProbe.Status, "Degraded", StringComparison.OrdinalIgnoreCase)
            && string.Equals(missingReportProbe.Recommendation, "RegenerateReport", StringComparison.OrdinalIgnoreCase)
            && missingReportProbe.Diagnostics.TryGetValue("MissingReportIds", out var missing)
            && missing.Count > 0;
        var endpointContractStable = EndpointContracts.Count == 8
            && EndpointContracts.All(static endpoint => endpoint.ReadOnly && endpoint.UsesEnvelope);
        var clientContractStable = ClientMethodContracts.Count == 8
            && ClientMethodContracts.All(static method => method.DeserializesEnvelope);
        var envelopeSchemaStable = EnvelopeSchemaFields.SequenceEqual(
            [
                "Success",
                "CapabilityId",
                "Status",
                "Recommendation",
                "Data",
                "Diagnostics",
                "GeneratedAt",
                "SchemaVersion"
            ]);
        var reportNavigationSchemaStable = navigation.SchemaVersion == EnvelopeSchemaVersion
            && navigation.Data is not null
            && navigation.Data.Reports.All(static report => report.SafeToExpose)
            && ReportNavigationSchemaFields.Count == 8;
        var absolutePathLeak = securityDiagnostics.AbsolutePathLeakDetected || ContainsAbsolutePathLeak(serializedContract);
        var secretLeak = securityDiagnostics.SecretLeakDetected || ContainsSecretPathLeak(serializedContract);
        var productionAuthConfigured = !productionMode || securityDiagnostics.AuthConfigured;

        var blocked = new List<string>();
        AddIfFalse(blocked, endpointContractStable, "EndpointContractMismatch");
        AddIfFalse(blocked, clientContractStable, "ClientContractMismatch");
        AddIfFalse(blocked, envelopeSchemaStable, "EnvelopeSchemaMismatch");
        AddIfFalse(blocked, reportNavigationSchemaStable, "ReportNavigationSchemaMismatch");
        AddIfFalse(blocked, missingReportReturnsDegraded, "DegradedBehaviorMismatch");
        AddIfFalse(blocked, forbiddenActionsExposed, "ForbiddenActionsNotExposed");
        AddIfFalse(blocked, !secretLeak, "SecretLeakDetected");
        AddIfFalse(blocked, !absolutePathLeak, "AbsolutePathLeakDetected");
        AddIfFalse(blocked, productionAuthConfigured, "ProductionAuthNotConfigured");
        AddIfFalse(blocked, !status.RuntimeSwitchAllowed, "RuntimeSwitchAllowed");
        AddIfFalse(blocked, !status.FormalRetrievalAllowed, "FormalRetrievalAllowed");
        AddIfFalse(blocked, !status.ReadyForRuntimeSwitch, "ReadyForRuntimeSwitch");
        AddIfFalse(blocked, !status.FormalPackageWritten, "FormalPackageWritten");
        AddIfFalse(blocked, !status.PackingPolicyChanged, "PackingPolicyChanged");
        AddIfFalse(blocked, !status.PackageOutputChanged, "PackageOutputChanged");
        AddIfFalse(blocked, !status.RuntimeMutated, "RuntimeMutated");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;

        return new FoundationApiContractReport
        {
            OperationId = $"service-api-contract-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            ContractPassed = passed,
            FreezePassed = passed,
            Recommendation = BuildContractRecommendation(distinctBlocked),
            EndpointCount = EndpointContracts.Count,
            ClientMethodCount = ClientMethodContracts.Count,
            EnvelopeSchemaVersion = EnvelopeSchemaVersion,
            EnvelopeSchemaFields = EnvelopeSchemaFields,
            Endpoints = EndpointContracts,
            ClientMethods = ClientMethodContracts,
            AuthMode = securityDiagnostics.AuthConfigured
                ? "ApiKey"
                : securityDiagnostics.DevelopmentMode ? "DevelopmentOnly" : "NotConfigured",
            AuthConfigured = securityDiagnostics.AuthConfigured,
            ApiKeyConfigured = securityDiagnostics.ApiKeyConfigured,
            DevelopmentMode = securityDiagnostics.DevelopmentMode,
            ProductionMode = productionMode,
            ProductionAuthRequired = productionMode,
            ProductionAuthConfigured = productionAuthConfigured,
            DegradedBehaviorStable = missingReportReturnsDegraded,
            MissingReportReturnsDegraded = missingReportReturnsDegraded,
            ReportNavigationSchemaStable = reportNavigationSchemaStable,
            ReportNavigationSchemaFields = ReportNavigationSchemaFields,
            ForbiddenActionsExposed = forbiddenActionsExposed,
            ForbiddenActions = RequiredForbiddenActions,
            SecretLeakDetected = secretLeak,
            AbsolutePathLeakDetected = absolutePathLeak,
            RuntimeSwitchAllowed = status.RuntimeSwitchAllowed,
            FormalRetrievalAllowed = status.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = status.ReadyForRuntimeSwitch,
            FormalPackageWritten = status.FormalPackageWritten,
            PackingPolicyChanged = status.PackingPolicyChanged,
            PackageOutputChanged = status.PackageOutputChanged,
            RuntimeMutated = status.RuntimeMutated,
            BlockedReasons = distinctBlocked
        };
    }

    public JsonObject BuildOpenApiDocument(FoundationServiceAuthDiagnosticsReport? authDiagnostics = null)
    {
        var apiKeyHeaderName = string.IsNullOrWhiteSpace(authDiagnostics?.ApiKeyHeaderName)
            ? "X-ContextCore-Key"
            : authDiagnostics.ApiKeyHeaderName;
        var paths = new JsonObject();
        foreach (var endpoint in EndpointContracts)
        {
            var pathItem = new JsonObject();
            var operation = new JsonObject
            {
                ["operationId"] = ToOperationId(endpoint.Route),
                ["summary"] = $"{endpoint.Method} {endpoint.Route}",
                ["x-capabilityId"] = endpoint.CapabilityId,
                ["x-readOnly"] = endpoint.ReadOnly,
                ["x-runtimeMutationAllowed"] = false,
                ["x-formalRetrievalAllowed"] = false,
                ["x-formalPackageWriteAllowed"] = false,
                ["x-packingPolicyMutationAllowed"] = false,
                ["x-packageOutputMutationAllowed"] = false
            };
            if (endpoint.Route.Contains("{reportId}", StringComparison.Ordinal))
            {
                operation["parameters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "reportId",
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "string" }
                    }
                };
            }

            operation["security"] = new JsonArray { new JsonObject { ["ApiKeyAuth"] = new JsonArray() } };
            operation["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "Read-only foundation status response.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["$ref"] = endpoint.ResponseType.Equals("FoundationReportNavigationEntry", StringComparison.Ordinal)
                                    ? "#/components/schemas/FoundationReportEntryEnvelope"
                                    : endpoint.ResponseType.Equals("FoundationReportNavigationResponse", StringComparison.Ordinal)
                                        ? "#/components/schemas/FoundationReportNavigationEnvelope"
                                        : "#/components/schemas/FoundationStatusEnvelope"
                            }
                        }
                    }
                },
                ["401"] = new JsonObject { ["description"] = "Unauthorized API key." },
                ["503"] = new JsonObject
                {
                    ["description"] = "Degraded report coverage response.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/DegradedFoundationEnvelope" }
                        }
                    }
                }
            };
            pathItem[endpoint.Method.ToLowerInvariant()] = operation;
            paths[endpoint.Route] = pathItem;
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "ContextCore Foundation Read-only API",
                ["version"] = "foundation-api-contract-v1"
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["ApiKeyAuth"] = new JsonObject
                    {
                        ["type"] = "apiKey",
                        ["in"] = "header",
                        ["name"] = apiKeyHeaderName,
                        ["description"] = "Header name only; secret values are never serialized."
                    }
                },
                ["schemas"] = BuildOpenApiSchemas()
            },
            ["x-envelopeSchemaVersion"] = EnvelopeSchemaVersion,
            ["x-forbiddenRuntimeActions"] = ToJsonArray(RequiredForbiddenActions),
            ["x-useForRuntime"] = false,
            ["x-formalRetrievalAllowed"] = false,
            ["x-readyForRuntimeSwitch"] = false
        };
    }

    public FoundationApiContractSnapshot BuildApiContractSnapshot(
        FoundationServiceAuthDiagnosticsReport? authDiagnostics = null)
        => new()
        {
            SnapshotId = $"foundation-api-contract-snapshot-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            SchemaVersion = EnvelopeSchemaVersion,
            EnvelopeSchemaFields = EnvelopeSchemaFields,
            Endpoints = EndpointContracts,
            CapabilityStatusSchemaFields = CapabilityStatusSchemaFields,
            ReportNavigationSchemaFields = ReportNavigationSchemaFields,
            ForbiddenActions = RequiredForbiddenActions,
            AuthScheme = "ApiKeyAuth",
            ApiKeyHeaderName = string.IsNullOrWhiteSpace(authDiagnostics?.ApiKeyHeaderName)
                ? "X-ContextCore-Key"
                : authDiagnostics.ApiKeyHeaderName,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false
        };

    public FoundationClientContractSnapshot BuildClientContractSnapshot()
        => new()
        {
            SnapshotId = $"foundation-client-contract-snapshot-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            SchemaVersion = EnvelopeSchemaVersion,
            Methods = ClientMethodContracts,
            AliasMethods = ClientAliasMethodContracts
        };

    public IReadOnlyList<FoundationApiEndpointContract> GetFoundationEndpointContracts()
        => EndpointContracts;

    public FoundationOpenApiContractReport BuildOpenApiContractReport(
        JsonObject openApiDocument,
        FoundationApiContractSnapshot apiSnapshot,
        FoundationClientContractSnapshot clientSnapshot)
    {
        ArgumentNullException.ThrowIfNull(openApiDocument);
        ArgumentNullException.ThrowIfNull(apiSnapshot);
        ArgumentNullException.ThrowIfNull(clientSnapshot);

        var serialized = JsonSerializer.Serialize(new
        {
            openApiDocument,
            apiSnapshot,
            clientSnapshot
        }, JsonOptions);
        var openApiPaths = openApiDocument["paths"]?.AsObject();
        var openApiSchemas = openApiDocument["components"]?["schemas"]?.AsObject();
        var openApiSecuritySchemes = openApiDocument["components"]?["securitySchemes"]?.AsObject();
        var endpointIds = apiSnapshot.Endpoints
            .Select(static endpoint => $"{endpoint.Method} {endpoint.Route}")
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var clientMethodCount = clientSnapshot.Methods.Count + clientSnapshot.AliasMethods.Count;
        var missingEndpoints = EndpointContracts
            .Where(endpoint => openApiPaths is null || !openApiPaths.ContainsKey(endpoint.Route))
            .Select(static endpoint => endpoint.Route)
            .ToArray();
        var missingClientMethods = ClientMethodContracts
            .Where(method => !clientSnapshot.Methods.Any(snapshot =>
                string.Equals(snapshot.MethodName, method.MethodName, StringComparison.Ordinal)))
            .Select(static method => method.MethodName)
            .ToArray();
        var blocked = new List<string>();
        AddIfFalse(blocked, missingEndpoints.Length == 0, "EndpointDeleted");
        AddIfFalse(blocked, apiSnapshot.Endpoints.Count == EndpointContracts.Count, "EndpointContractMismatch");
        AddIfFalse(blocked, apiSnapshot.EnvelopeSchemaFields.SequenceEqual(EnvelopeSchemaFields), "EnvelopeSchemaMismatch");
        AddIfFalse(blocked, string.Equals(apiSnapshot.SchemaVersion, EnvelopeSchemaVersion, StringComparison.Ordinal), "EnvelopeSchemaVersionMismatch");
        AddIfFalse(blocked, missingClientMethods.Length == 0, "ClientMethodDeleted");
        AddIfFalse(blocked, clientSnapshot.Methods.All(static method => method.DeserializesEnvelope), "ClientEnvelopeMismatch");
        AddIfFalse(blocked, openApiSchemas?.ContainsKey("FoundationApiResponseEnvelope") == true, "EnvelopeSchemaMissing");
        AddIfFalse(blocked, openApiSchemas?.ContainsKey("CapabilityStatus") == true, "CapabilityStatusSchemaMissing");
        AddIfFalse(blocked, openApiSchemas?.ContainsKey("FoundationReportNavigationEntry") == true, "ReportNavigationSchemaMissing");
        AddIfFalse(blocked, string.Equals(apiSnapshot.AuthScheme, "ApiKeyAuth", StringComparison.Ordinal), "AuthSchemeDowngrade");
        AddIfFalse(blocked, openApiSecuritySchemes?.ContainsKey("ApiKeyAuth") == true, "AuthSchemeMissing");
        AddIfFalse(blocked, RequiredForbiddenActions.All(action =>
            apiSnapshot.ForbiddenActions.Contains(action, StringComparer.OrdinalIgnoreCase)), "ForbiddenActionsMissing");
        AddIfFalse(blocked, !ContainsAbsolutePathLeak(serialized), "AbsolutePathLeakDetected");
        AddIfFalse(blocked, !ContainsSecretPathLeak(serialized), "SecretLeakDetected");
        AddIfFalse(blocked, apiSnapshot.ReadOnly && clientSnapshot.ReadOnly, "ReadOnlyContractBroken");
        AddIfFalse(blocked, !apiSnapshot.FormalRetrievalAllowed, "FormalRetrievalAllowed");
        AddIfFalse(blocked, !apiSnapshot.RuntimeSwitchAllowed, "RuntimeSwitchAllowed");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FoundationOpenApiContractReport
        {
            OperationId = $"service-openapi-contract-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            EndpointCount = apiSnapshot.Endpoints.Count,
            EndpointIds = endpointIds,
            EnvelopeSchemaVersion = apiSnapshot.SchemaVersion,
            AuthScheme = apiSnapshot.AuthScheme,
            ApiKeyHeaderName = apiSnapshot.ApiKeyHeaderName,
            ClientMethodCount = clientMethodCount,
            RequestSchemaCount = 0,
            ResponseSchemaCount = openApiSchemas?.Count ?? 0,
            ForbiddenActionCount = apiSnapshot.ForbiddenActions.Count,
            BreakingChangeDetected = distinctBlocked.Length > 0,
            SecretLeakDetected = distinctBlocked.Contains("SecretLeakDetected", StringComparer.OrdinalIgnoreCase),
            AbsolutePathLeakDetected = distinctBlocked.Contains("AbsolutePathLeakDetected", StringComparer.OrdinalIgnoreCase),
            Recommendation = BuildOpenApiContractRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked
        };
    }

    public async Task<ServiceFoundationFreezeReport> BuildServiceFoundationFreezeReportAsync(
        CancellationToken cancellationToken = default)
    {
        var serviceStatus = await ReadJsonAsync<ServiceFoundationStatusSmokeReport>(
                ServiceFoundationStatusSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var serviceReadiness = await ReadJsonAsync<ServiceFoundationStatusSmokeReport>(
                ServiceReadinessApiSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var security = await ReadJsonAsync<FoundationApiSecurityDiagnosticsReport>(
                ServiceApiSecurityDiagnosticsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var navigation = await ReadJsonAsync<ServiceReportNavigationSmokeReport>(
                ServiceReportNavigationSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var contract = await ReadJsonAsync<FoundationApiContractReport>(
                ServiceApiContractFreezeGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var deployment = await ReadJsonAsync<FoundationServiceDeploymentProfileGateReport>(
                ServiceDeploymentProfileGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var drift = await ReadJsonAsync<FoundationOpenApiContractReport>(
                ServiceApiContractDriftGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var hosted = await ReadJsonAsync<HostedServiceSmokeReport>(
                ServiceHostedDeploymentSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var readonlyRuntime = await ReadJsonAsync<HostedServiceSmokeReport>(
                ServiceReadonlyRuntimeSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var hostedContract = await ReadJsonAsync<HostedServiceSmokeReport>(
                ServiceHostedApiContractSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var foundation = await ReadJsonAsync<ContextCoreFoundationFreezeReport>(
                FoundationReleaseCandidatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var reproducibility = await ReadJsonAsync<FoundationReproducibilityReport>(
                FoundationReproducibilityPath,
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonAsync<LearningRuntimeChangeReadinessGateReport>(
                RuntimeChangeGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var p15A3 = await ReadP15StatusAsync(ResolvePath("eval/eval-report-p15-a3.json"), cancellationToken)
            .ConfigureAwait(false);
        var p15Extended = await ReadP15StatusAsync(ResolvePath("eval/eval-report-p15-extended.json"), cancellationToken)
            .ConfigureAwait(false);

        return BuildServiceFoundationFreezeReport(
            serviceStatus,
            serviceReadiness,
            security,
            navigation,
            contract,
            deployment,
            drift,
            hosted,
            readonlyRuntime,
            hostedContract,
            foundation,
            reproducibility,
            runtimeGate,
            p15A3.Passed && p15Extended.Passed);
    }

    public ServiceFoundationFreezeReport BuildServiceFoundationFreezeReport(
        ServiceFoundationStatusSmokeReport? serviceStatus,
        ServiceFoundationStatusSmokeReport? serviceReadiness,
        FoundationApiSecurityDiagnosticsReport? security,
        ServiceReportNavigationSmokeReport? navigation,
        FoundationApiContractReport? contract,
        FoundationServiceDeploymentProfileGateReport? deployment,
        FoundationOpenApiContractReport? drift,
        HostedServiceSmokeReport? hosted,
        HostedServiceSmokeReport? readonlyRuntime,
        HostedServiceSmokeReport? hostedContract,
        ContextCoreFoundationFreezeReport? foundation,
        FoundationReproducibilityReport? reproducibility,
        LearningRuntimeChangeReadinessGateReport? runtimeGate,
        bool p15Passed)
    {
        var blocked = new List<string>();

        var svc1Passed = serviceStatus?.SmokePassed == true
            && serviceReadiness?.SmokePassed == true;
        AddIfFalse(blocked, serviceStatus is not null, "MissingServiceFoundationStatusSmoke");
        AddIfFalse(blocked, serviceReadiness is not null, "MissingServiceReadinessApiSmoke");
        AddIfFalse(blocked, svc1Passed, "Svc1ReadOnlyFoundationApiNotPassed");

        var svc2Passed = security is not null
            && navigation?.SmokePassed == true
            && !security.SecretLeakDetected
            && !security.AbsolutePathLeakDetected;
        AddIfFalse(blocked, security is not null, "MissingServiceApiSecurityDiagnostics");
        AddIfFalse(blocked, navigation is not null, "MissingServiceReportNavigationSmoke");
        AddIfFalse(blocked, svc2Passed, "Svc2ServiceHardeningNotPassed");

        var svc3Passed = contract?.FreezePassed == true;
        AddIfFalse(blocked, contract is not null, "MissingServiceApiContractFreezeGate");
        AddIfFalse(blocked, svc3Passed, "Svc3ApiContractFreezeNotPassed");

        var svc4Passed = deployment?.GatePassed == true;
        AddIfFalse(blocked, deployment is not null, "MissingServiceDeploymentProfileGate");
        AddIfFalse(blocked, svc4Passed, "Svc4AuthDeploymentProfileNotPassed");

        var svc5Passed = drift is not null
            && !drift.BreakingChangeDetected
            && !drift.SecretLeakDetected
            && !drift.AbsolutePathLeakDetected
            && string.Equals(drift.Recommendation, "ReadyForOpenApiContractFreeze", StringComparison.OrdinalIgnoreCase);
        AddIfFalse(blocked, drift is not null, "MissingServiceApiContractDriftGate");
        AddIfFalse(blocked, svc5Passed, "Svc5OpenApiContractSnapshotNotPassed");

        var svc6Passed = hosted?.SmokePassed == true
            && readonlyRuntime?.SmokePassed == true
            && hostedContract?.SmokePassed == true;
        AddIfFalse(blocked, hosted is not null, "MissingHostedDeploymentSmoke");
        AddIfFalse(blocked, readonlyRuntime is not null, "MissingReadonlyRuntimeSmoke");
        AddIfFalse(blocked, hostedContract is not null, "MissingHostedApiContractSmoke");
        AddIfFalse(blocked, svc6Passed, "Svc6HostedReadOnlySmokeNotPassed");

        var foundationPassed = foundation?.FreezePassed == true;
        AddIfFalse(blocked, foundationPassed, "FoundationReleaseCandidateGateNotPassed");

        var reproducibilityPassed = reproducibility?.ReproducibilityPassed == true;
        AddIfFalse(blocked, reproducibilityPassed, "FoundationReproducibilityCheckNotPassed");

        var runtimeGatePassed = runtimeGate?.Passed == true;
        AddIfFalse(blocked, runtimeGatePassed, "RuntimeChangeGateNotPassed");

        AddIfFalse(blocked, p15Passed, "P15GateNotPassed");

        var runtimeMutated = serviceStatus?.RuntimeMutated == true
            || serviceReadiness?.RuntimeMutated == true
            || contract?.RuntimeMutated == true
            || deployment?.RuntimeMutated == true
            || hosted?.RuntimeMutated == true
            || readonlyRuntime?.RuntimeMutated == true
            || hostedContract?.RuntimeMutated == true;
        AddIfFalse(blocked, !runtimeMutated, "RuntimeMutationDetected");

        var formalRetrievalAllowed = serviceStatus?.FormalRetrievalAllowed == true
            || serviceReadiness?.FormalRetrievalAllowed == true
            || contract?.FormalRetrievalAllowed == true
            || deployment?.FormalRetrievalAllowed == true
            || hosted?.FormalRetrievalAllowed == true
            || readonlyRuntime?.FormalRetrievalAllowed == true
            || hostedContract?.FormalRetrievalAllowed == true
            || foundation?.FormalRetrievalAllowed == true;
        AddIfFalse(blocked, !formalRetrievalAllowed, "FormalRetrievalAllowed");

        var runtimeSwitchAllowed = serviceStatus?.RuntimeSwitchAllowed == true
            || serviceReadiness?.RuntimeSwitchAllowed == true
            || contract?.RuntimeSwitchAllowed == true
            || deployment?.RuntimeSwitchAllowed == true
            || hosted?.RuntimeSwitchAllowed == true
            || readonlyRuntime?.RuntimeSwitchAllowed == true
            || hostedContract?.RuntimeSwitchAllowed == true
            || foundation?.RuntimeSwitchAllowed == true;
        AddIfFalse(blocked, !runtimeSwitchAllowed, "RuntimeSwitchAllowed");

        var readyForRuntimeSwitch = contract?.ReadyForRuntimeSwitch == true
            || hosted?.ReadyForRuntimeSwitch == true
            || readonlyRuntime?.ReadyForRuntimeSwitch == true
            || hostedContract?.ReadyForRuntimeSwitch == true
            || foundation?.ReadyForRuntimeSwitch == true;
        AddIfFalse(blocked, !readyForRuntimeSwitch, "ReadyForRuntimeSwitch");

        var packingPolicyChanged = serviceStatus?.PackingPolicyChanged == true
            || serviceReadiness?.PackingPolicyChanged == true
            || contract?.PackingPolicyChanged == true
            || hosted?.PackingPolicyChanged == true
            || readonlyRuntime?.PackingPolicyChanged == true
            || hostedContract?.PackingPolicyChanged == true
            || foundation?.PackingPolicyChanged == true;
        AddIfFalse(blocked, !packingPolicyChanged, "PackingPolicyChanged");

        var packageOutputChanged = serviceStatus?.PackageOutputChanged == true
            || serviceReadiness?.PackageOutputChanged == true
            || contract?.PackageOutputChanged == true
            || hosted?.PackageOutputChanged == true
            || readonlyRuntime?.PackageOutputChanged == true
            || hostedContract?.PackageOutputChanged == true
            || foundation?.PackageOutputChanged == true;
        AddIfFalse(blocked, !packageOutputChanged, "PackageOutputChanged");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var freezePassed = distinctBlocked.Length == 0;

        return new ServiceFoundationFreezeReport
        {
            OperationId = $"service-foundation-freeze-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            Recommendation = BuildServiceFoundationFreezeRecommendation(distinctBlocked),
            ServiceFoundation = freezePassed ? "Frozen" : "NotFrozen",
            FoundationApi = freezePassed ? "ReadyForHostedReadOnlyService" : "NotFrozen",
            OpenApiContract = svc5Passed ? "Frozen" : "NotFrozen",
            AuthDeploymentProfile = svc4Passed ? "Ready" : "NotReady",
            RuntimeMutationAllowed = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            Svc1ReadOnlyFoundationApiPassed = svc1Passed,
            Svc2ServiceHardeningPassed = svc2Passed,
            Svc3ApiContractFreezePassed = svc3Passed,
            Svc4AuthDeploymentProfilePassed = svc4Passed,
            Svc5OpenApiContractSnapshotPassed = svc5Passed,
            Svc6HostedReadOnlySmokePassed = svc6Passed,
            FoundationReleaseCandidateGatePassed = foundationPassed,
            FoundationReproducibilityCheckPassed = reproducibilityPassed,
            RuntimeChangeGatePassed = runtimeGatePassed,
            P15GatePassed = p15Passed,
            HostedSmokeRecommendation = hosted?.Recommendation ?? "MissingReport",
            AuthDeploymentRecommendation = deployment?.Recommendation ?? "MissingReport",
            ContractDriftRecommendation = drift?.Recommendation ?? "MissingReport",
            NextAllowedPhase = freezePassed
                ? "V4.5 Explicit Scoped Runtime Experiment Planning"
                : "ResolveServiceFoundationFreezeBlockers",
            PhaseStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SVC1 Read-only foundation API"] = svc1Passed ? "Passed" : "MissingOrFailed",
                ["SVC2 Service hardening"] = svc2Passed ? "Passed" : "MissingOrFailed",
                ["SVC3 API contract freeze"] = svc3Passed ? "Passed" : "MissingOrFailed",
                ["SVC4 Auth deployment profile"] = svc4Passed ? "Passed" : "MissingOrFailed",
                ["SVC5 OpenAPI/client snapshot"] = svc5Passed ? "Passed" : "MissingOrFailed",
                ["SVC6 Hosted read-only smoke"] = svc6Passed ? "Passed" : "MissingOrFailed",
                ["Foundation release candidate gate"] = foundationPassed ? "Passed" : "MissingOrFailed",
                ["Foundation reproducibility check"] = reproducibilityPassed ? "Passed" : "MissingOrFailed",
                ["Runtime change gate"] = runtimeGatePassed ? "Passed" : "MissingOrFailed",
                ["P15 gate"] = p15Passed ? "Passed" : "MissingOrFailed"
            },
            BlockedReasons = distinctBlocked
        };
    }

    public HostedServiceSmokeReport BuildHostedServiceSmokeReport(
        HostedServiceSmokeOptions options,
        IReadOnlyList<HostedServiceEndpointProbeResult> endpointResults,
        bool authPassed,
        bool unauthorizedCheckPassed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpointResults);

        var configured = options.Enabled && !string.IsNullOrWhiteSpace(options.BaseUrl);
        var endpointCount = EndpointContracts.Count;
        var successful = endpointResults.Count(static item => item.Success);
        var failed = configured ? endpointCount - successful : endpointCount;
        var envelopeMatched = configured && endpointResults.Count == endpointCount
            && endpointResults.All(static item => item.EnvelopeSchemaMatched);
        var runtimeMutated = endpointResults.Any(static item => item.RuntimeMutated);
        var formalRetrievalAllowed = endpointResults.Any(static item => item.FormalRetrievalAllowed);
        var runtimeSwitchAllowed = endpointResults.Any(static item => item.RuntimeSwitchAllowed);
        var readyForRuntimeSwitch = endpointResults.Any(static item => item.ReadyForRuntimeSwitch);
        var packingPolicyChanged = endpointResults.Any(static item => item.PackingPolicyChanged);
        var packageOutputChanged = endpointResults.Any(static item => item.PackageOutputChanged);
        var secretLeak = endpointResults.Any(static item => item.SecretLeakDetected);
        var absolutePathLeak = endpointResults.Any(static item => item.AbsolutePathLeakDetected);

        var blocked = new List<string>();
        AddIfFalse(blocked, configured, "HostedServiceNotConfigured");
        AddIfFalse(blocked, successful == endpointCount, "HostedEndpointFailure");
        AddIfFalse(blocked, authPassed, "AuthFailure");
        AddIfFalse(blocked, unauthorizedCheckPassed, "UnauthorizedCheckFailed");
        AddIfFalse(blocked, envelopeMatched, "EnvelopeSchemaMismatch");
        AddIfFalse(blocked, !runtimeMutated, "RuntimeMutated");
        AddIfFalse(blocked, !formalRetrievalAllowed, "FormalRetrievalAllowed");
        AddIfFalse(blocked, !runtimeSwitchAllowed, "RuntimeSwitchAllowed");
        AddIfFalse(blocked, !readyForRuntimeSwitch, "ReadyForRuntimeSwitch");
        AddIfFalse(blocked, !packingPolicyChanged, "PackingPolicyChanged");
        AddIfFalse(blocked, !packageOutputChanged, "PackageOutputChanged");
        AddIfFalse(blocked, !secretLeak, "SecretLeakDetected");
        AddIfFalse(blocked, !absolutePathLeak, "AbsolutePathLeakDetected");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new HostedServiceSmokeReport
        {
            OperationId = $"service-hosted-smoke-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            SmokePassed = distinctBlocked.Length == 0,
            BaseUrl = NormalizeHostedBaseUrlForReport(options.BaseUrl),
            DeploymentProfile = options.DeploymentProfile,
            EndpointCount = endpointCount,
            SuccessfulEndpointCount = successful,
            FailedEndpointCount = failed,
            AuthPassed = authPassed,
            UnauthorizedCheckPassed = unauthorizedCheckPassed,
            EnvelopeSchemaMatched = envelopeMatched,
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            SecretLeakDetected = secretLeak,
            AbsolutePathLeakDetected = absolutePathLeak,
            Recommendation = BuildHostedSmokeRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked,
            Endpoints = endpointResults
        };
    }

    public ServiceFoundationStatusSmokeReport BuildSmokeReport(
        FoundationServiceStatusResponse status,
        FoundationServiceStatusResponse releaseCandidate,
        FoundationServiceStatusResponse reproducibility,
        FoundationServiceStatusResponse runtimeChangeGate,
        FoundationServiceStatusResponse vectorFormalPreview,
        FoundationServiceStatusResponse postgresFreeze)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(releaseCandidate);
        ArgumentNullException.ThrowIfNull(reproducibility);
        ArgumentNullException.ThrowIfNull(runtimeChangeGate);
        ArgumentNullException.ThrowIfNull(vectorFormalPreview);
        ArgumentNullException.ThrowIfNull(postgresFreeze);

        var failed = new List<string>();
        AddIfFalse(failed, status.FoundationGateStatus == "Passed", "FoundationStatusNotPassed");
        AddIfFalse(failed, releaseCandidate.FoundationGateStatus == "Passed", "ReleaseCandidateNotPassed");
        AddIfFalse(failed, reproducibility.ReproducibilityStatus == "Passed", "ReproducibilityNotPassed");
        AddIfFalse(failed, runtimeChangeGate.RuntimeChangeGateStatus == "Passed", "RuntimeChangeGateNotPassed");
        AddIfFalse(failed, vectorFormalPreview.VectorFormalPreviewStatus == "Passed", "VectorFormalPreviewNotPassed");
        AddIfFalse(failed, postgresFreeze.PostgresFreezeStatus == "Passed", "PostgresFreezeNotPassed");
        var responses = new[]
        {
            status,
            releaseCandidate,
            reproducibility,
            runtimeChangeGate,
            vectorFormalPreview,
            postgresFreeze
        };
        var runtimeMutated = responses.Any(static item => item.RuntimeMutated);
        var formalRetrievalAllowed = responses.Any(static item => item.FormalRetrievalAllowed);
        var runtimeSwitchAllowed = responses.Any(static item => item.RuntimeSwitchAllowed);
        var packingPolicyChanged = responses.Any(static item => item.PackingPolicyChanged);
        var packageOutputChanged = responses.Any(static item => item.PackageOutputChanged);
        AddIfFalse(failed, !runtimeMutated, "RuntimeMutated");
        AddIfFalse(failed, !formalRetrievalAllowed, "FormalRetrievalAllowed");
        AddIfFalse(failed, !runtimeSwitchAllowed, "RuntimeSwitchAllowed");
        AddIfFalse(failed, !packingPolicyChanged, "PackingPolicyChanged");
        AddIfFalse(failed, !packageOutputChanged, "PackageOutputChanged");

        var blocked = failed
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = blocked.Length == 0;

        return new ServiceFoundationStatusSmokeReport
        {
            OperationId = $"service-foundation-status-smoke-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            SmokePassed = passed,
            Recommendation = passed ? "ReadyForReadOnlyServiceStatus" : "BlockedByReadOnlyStatusMismatch",
            EndpointCount = 6,
            CapabilityCount = status.Capabilities.Count,
            FoundationStatusPassed = status.FoundationGateStatus == "Passed",
            ReleaseCandidatePassed = releaseCandidate.FoundationGateStatus == "Passed",
            ReproducibilityPassed = reproducibility.ReproducibilityStatus == "Passed",
            RuntimeChangeGatePassed = runtimeChangeGate.RuntimeChangeGateStatus == "Passed",
            VectorFormalPreviewPassed = vectorFormalPreview.VectorFormalPreviewStatus == "Passed",
            PostgresFreezePassed = postgresFreeze.PostgresFreezeStatus == "Passed",
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            BlockedReasons = blocked
        };
    }

    public static string BuildSmokeMarkdown(ServiceFoundationStatusSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Foundation Read-only Status Smoke");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- EndpointCount: `{report.EndpointCount}`");
        builder.AppendLine($"- CapabilityCount: `{report.CapabilityCount}`");
        builder.AppendLine($"- FoundationStatusPassed: `{report.FoundationStatusPassed}`");
        builder.AppendLine($"- ReleaseCandidatePassed: `{report.ReleaseCandidatePassed}`");
        builder.AppendLine($"- ReproducibilityPassed: `{report.ReproducibilityPassed}`");
        builder.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- VectorFormalPreviewPassed: `{report.VectorFormalPreviewPassed}`");
        builder.AppendLine($"- PostgresFreezePassed: `{report.PostgresFreezePassed}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        if (report.BlockedReasons.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var reason in report.BlockedReasons)
            {
                builder.AppendLine($"- `{reason}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Boundary");
        builder.AppendLine();
        builder.AppendLine("- Read-only service/API smoke 不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`。");
        builder.AppendLine("- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。");
        return builder.ToString();
    }

    public static string BuildSecurityDiagnosticsMarkdown(FoundationApiSecurityDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service API Security Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- AuthConfigured: `{report.AuthConfigured}`");
        builder.AppendLine($"- ApiKeyConfigured: `{report.ApiKeyConfigured}`");
        builder.AppendLine($"- DevelopmentMode: `{report.DevelopmentMode}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        AppendList(builder, report.Diagnostics);
        builder.AppendLine();
        builder.AppendLine("API key value is never serialized in this report.");
        return builder.ToString();
    }

    public static string BuildReportNavigationSmokeMarkdown(ServiceReportNavigationSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Report Navigation Smoke");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ReportCount: `{report.ReportCount}`");
        builder.AppendLine($"- DegradedReportCount: `{report.DegradedReportCount}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- EnvelopeSchemaStable: `{report.EnvelopeSchemaStable}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        return builder.ToString();
    }

    public static string BuildContractMarkdown(FoundationApiContractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service API Contract Freeze");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ContractPassed: `{report.ContractPassed}`");
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- EndpointCount: `{report.EndpointCount}`");
        builder.AppendLine($"- ClientMethodCount: `{report.ClientMethodCount}`");
        builder.AppendLine($"- EnvelopeSchemaVersion: `{report.EnvelopeSchemaVersion}`");
        builder.AppendLine($"- AuthMode: `{report.AuthMode}`");
        builder.AppendLine($"- AuthConfigured: `{report.AuthConfigured}`");
        builder.AppendLine($"- ProductionMode: `{report.ProductionMode}`");
        builder.AppendLine($"- DegradedBehaviorStable: `{report.DegradedBehaviorStable}`");
        builder.AppendLine($"- ReportNavigationSchemaStable: `{report.ReportNavigationSchemaStable}`");
        builder.AppendLine($"- ForbiddenActionsExposed: `{report.ForbiddenActionsExposed}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine();
        builder.AppendLine("## Endpoints");
        foreach (var endpoint in report.Endpoints)
        {
            builder.AppendLine($"- `{endpoint.Method} {endpoint.Route}` -> `{endpoint.ResponseType}` envelope=`{endpoint.UsesEnvelope}` readOnly=`{endpoint.ReadOnly}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Client Methods");
        foreach (var method in report.ClientMethods)
        {
            builder.AppendLine($"- `{method.MethodName}` -> `{method.Route}` response=`{method.ResponseType}` envelope=`{method.DeserializesEnvelope}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Envelope Schema");
        AppendList(builder, report.EnvelopeSchemaFields);
        builder.AppendLine();
        builder.AppendLine("## Report Navigation Schema");
        AppendList(builder, report.ReportNavigationSchemaFields);
        builder.AppendLine();
        builder.AppendLine("## Forbidden Actions");
        AppendList(builder, report.ForbiddenActions);
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("This contract is read-only and does not allow runtime switch, formal retrieval, formal package write, PackingPolicy integration, or package output mutation.");
        return builder.ToString();
    }

    public static string BuildOpenApiContractMarkdown(FoundationOpenApiContractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service OpenAPI / Client Contract Snapshot");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- EndpointCount: `{report.EndpointCount}`");
        builder.AppendLine($"- ClientMethodCount: `{report.ClientMethodCount}`");
        builder.AppendLine($"- EnvelopeSchemaVersion: `{report.EnvelopeSchemaVersion}`");
        builder.AppendLine($"- AuthScheme: `{report.AuthScheme}`");
        builder.AppendLine($"- ApiKeyHeaderName: `{report.ApiKeyHeaderName}`");
        builder.AppendLine($"- RequestSchemaCount: `{report.RequestSchemaCount}`");
        builder.AppendLine($"- ResponseSchemaCount: `{report.ResponseSchemaCount}`");
        builder.AppendLine($"- ForbiddenActionCount: `{report.ForbiddenActionCount}`");
        builder.AppendLine($"- BreakingChangeDetected: `{report.BreakingChangeDetected}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Endpoints");
        AppendList(builder, report.EndpointIds);
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("OpenAPI/snapshot artifacts are read-only contracts. They do not enable runtime switch, formal retrieval, formal package write, PackingPolicy integration, or package output mutation.");
        return builder.ToString();
    }

    public static string BuildHostedServiceSmokeMarkdown(HostedServiceSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Hosted Service Deployment Smoke");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        builder.AppendLine($"- BaseUrl: `{report.BaseUrl}`");
        builder.AppendLine($"- DeploymentProfile: `{report.DeploymentProfile}`");
        builder.AppendLine($"- EndpointCount: `{report.EndpointCount}`");
        builder.AppendLine($"- SuccessfulEndpointCount: `{report.SuccessfulEndpointCount}`");
        builder.AppendLine($"- FailedEndpointCount: `{report.FailedEndpointCount}`");
        builder.AppendLine($"- AuthPassed: `{report.AuthPassed}`");
        builder.AppendLine($"- UnauthorizedCheckPassed: `{report.UnauthorizedCheckPassed}`");
        builder.AppendLine($"- EnvelopeSchemaMatched: `{report.EnvelopeSchemaMatched}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Endpoints");
        if (report.Endpoints.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var endpoint in report.Endpoints)
            {
                builder.AppendLine($"- `{endpoint.Method} {endpoint.Route}` status=`{endpoint.StatusCode}` success=`{endpoint.Success}` envelope=`{endpoint.EnvelopeSchemaMatched}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("Hosted smoke is read-only. It does not enable formal retrieval, runtime switch, formal package write, PackingPolicy integration, or package output mutation.");
        return builder.ToString();
    }

    public static string BuildServiceFoundationFreezeMarkdown(ServiceFoundationFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Foundation Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ServiceFoundation: `{report.ServiceFoundation}`");
        builder.AppendLine($"- FoundationApi: `{report.FoundationApi}`");
        builder.AppendLine($"- OpenApiContract: `{report.OpenApiContract}`");
        builder.AppendLine($"- AuthDeploymentProfile: `{report.AuthDeploymentProfile}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine();
        builder.AppendLine("## Phase Gates");
        builder.AppendLine();
        foreach (var phase in report.PhaseStatuses)
        {
            builder.AppendLine($"- {phase.Key}: `{phase.Value}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Boundary");
        builder.AppendLine();
        builder.AppendLine($"- RuntimeMutationAllowed: `{report.RuntimeMutationAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine();
        builder.AppendLine("## Service Signals");
        builder.AppendLine();
        builder.AppendLine($"- HostedSmokeRecommendation: `{report.HostedSmokeRecommendation}`");
        builder.AppendLine($"- AuthDeploymentRecommendation: `{report.AuthDeploymentRecommendation}`");
        builder.AppendLine($"- ContractDriftRecommendation: `{report.ContractDriftRecommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("Service Foundation freeze is still read-only: it does not enable formal retrieval, runtime switch, formal package write, PackingPolicy integration, or package output mutation.");
        return builder.ToString();
    }

    public static string BuildAuthDiagnosticsMarkdown(FoundationServiceAuthDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Auth Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- DeploymentProfile: `{report.DeploymentProfile}`");
        builder.AppendLine($"- AuthConfigured: `{report.AuthConfigured}`");
        builder.AppendLine($"- ApiKeyConfigured: `{report.ApiKeyConfigured}`");
        builder.AppendLine($"- RequireApiKey: `{report.RequireApiKey}`");
        builder.AppendLine($"- ApiKeyHeaderName: `{report.ApiKeyHeaderName}`");
        builder.AppendLine($"- DevelopmentNoAuthAllowed: `{report.DevelopmentNoAuthAllowed}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        AppendList(builder, report.Diagnostics);
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("API key header name may be shown; API key values are never serialized.");
        return builder.ToString();
    }

    public static string BuildAuthEnforcementSmokeMarkdown(FoundationServiceAuthEnforcementSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Auth Enforcement Smoke");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        builder.AppendLine($"- DevelopmentNoAuthAllowed: `{report.DevelopmentNoAuthAllowed}`");
        builder.AppendLine($"- ServiceMissingApiKeyBlocked: `{report.ServiceMissingApiKeyBlocked}`");
        builder.AppendLine($"- ServiceConfiguredApiKeyPassed: `{report.ServiceConfiguredApiKeyPassed}`");
        builder.AppendLine($"- ProductionMissingAuthBlocked: `{report.ProductionMissingAuthBlocked}`");
        builder.AppendLine($"- WrongApiKeyUnauthorized: `{report.WrongApiKeyUnauthorized}`");
        builder.AppendLine($"- CorrectApiKeyAvailable: `{report.CorrectApiKeyAvailable}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        return builder.ToString();
    }

    public static string BuildDeploymentProfileGateMarkdown(FoundationServiceDeploymentProfileGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Deployment Profile Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- DeploymentProfile: `{report.DeploymentProfile}`");
        builder.AppendLine($"- AuthConfigured: `{report.AuthConfigured}`");
        builder.AppendLine($"- ApiKeyConfigured: `{report.ApiKeyConfigured}`");
        builder.AppendLine($"- RequireApiKey: `{report.RequireApiKey}`");
        builder.AppendLine($"- DevelopmentNoAuthAllowed: `{report.DevelopmentNoAuthAllowed}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        return builder.ToString();
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

    private static async Task<P15ReportStatus> ReadP15StatusAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new P15ReportStatus(false, 0, 0, 0, "MissingReport");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var total = ReadInt(root, "TotalSamples");
            var failed = ReadInt(root, "FailedSamples");
            var invalid = ReadInt(root, "InvalidSamples");
            return new P15ReportStatus(total > 0 && failed == 0 && invalid == 0, total, failed, invalid, "Loaded");
        }
        catch (JsonException)
        {
            return new P15ReportStatus(false, 0, 0, 0, "InvalidReport");
        }
        catch (IOException)
        {
            return new P15ReportStatus(false, 0, 0, 0, "UnreadableReport");
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

    private static JsonObject BuildOpenApiSchemas()
        => new()
        {
            ["FoundationApiResponseEnvelope"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = ToJsonArray(EnvelopeSchemaFields),
                ["properties"] = new JsonObject
                {
                    ["Success"] = new JsonObject { ["type"] = "boolean" },
                    ["CapabilityId"] = new JsonObject { ["type"] = "string" },
                    ["Status"] = new JsonObject { ["type"] = "string" },
                    ["Recommendation"] = new JsonObject { ["type"] = "string" },
                    ["Data"] = new JsonObject(),
                    ["Diagnostics"] = new JsonObject { ["type"] = "object" },
                    ["GeneratedAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                    ["SchemaVersion"] = new JsonObject { ["type"] = "string", ["const"] = EnvelopeSchemaVersion }
                }
            },
            ["DegradedFoundationEnvelope"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    new JsonObject { ["$ref"] = "#/components/schemas/FoundationApiResponseEnvelope" }
                },
                ["properties"] = new JsonObject
                {
                    ["Status"] = new JsonObject { ["const"] = "Degraded" },
                    ["Recommendation"] = new JsonObject { ["const"] = "RegenerateReport" },
                    ["Diagnostics"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["MissingReportIds"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            }
                        }
                    }
                }
            },
            ["FoundationStatusEnvelope"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    new JsonObject { ["$ref"] = "#/components/schemas/FoundationApiResponseEnvelope" }
                },
                ["properties"] = new JsonObject
                {
                    ["Data"] = new JsonObject { ["$ref"] = "#/components/schemas/FoundationServiceStatusResponse" }
                }
            },
            ["FoundationReportNavigationEnvelope"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    new JsonObject { ["$ref"] = "#/components/schemas/FoundationApiResponseEnvelope" }
                },
                ["properties"] = new JsonObject
                {
                    ["Data"] = new JsonObject { ["$ref"] = "#/components/schemas/FoundationReportNavigationResponse" }
                }
            },
            ["FoundationReportEntryEnvelope"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    new JsonObject { ["$ref"] = "#/components/schemas/FoundationApiResponseEnvelope" }
                },
                ["properties"] = new JsonObject
                {
                    ["Data"] = new JsonObject { ["$ref"] = "#/components/schemas/FoundationReportNavigationEntry" }
                }
            },
            ["FoundationServiceStatusResponse"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["Capabilities"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/CapabilityStatus" }
                    },
                    ["FormalRetrievalAllowed"] = new JsonObject { ["type"] = "boolean", ["const"] = false },
                    ["RuntimeSwitchAllowed"] = new JsonObject { ["type"] = "boolean", ["const"] = false },
                    ["ReadyForRuntimeSwitch"] = new JsonObject { ["type"] = "boolean", ["const"] = false }
                }
            },
            ["CapabilityStatus"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = ToJsonArray(CapabilityStatusSchemaFields),
                ["properties"] = ToStringPropertyMap(CapabilityStatusSchemaFields)
            },
            ["FoundationReportNavigationResponse"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["Reports"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/FoundationReportNavigationEntry" }
                    },
                    ["MissingReportIds"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" }
                    }
                }
            },
            ["FoundationReportNavigationEntry"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = ToJsonArray(ReportNavigationSchemaFields),
                ["properties"] = ToStringPropertyMap(ReportNavigationSchemaFields)
            }
        };

    private static JsonObject ToStringPropertyMap(IEnumerable<string> fields)
    {
        var properties = new JsonObject();
        foreach (var field in fields)
        {
            properties[field] = new JsonObject { ["type"] = "string" };
        }

        return properties;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string ToOperationId(string route)
    {
        var normalized = route
            .Replace("/api/admin/foundation/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{reportId}", "by-id", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "-", StringComparison.Ordinal)
            .Replace("_", "-", StringComparison.Ordinal)
            .Trim('-');
        return $"get-foundation-{normalized}";
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

    private static string BuildContractRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Count == 0)
        {
            return "ReadyForServiceApiContractFreeze";
        }

        if (blockedReasons.Any(static item => item.Contains("Auth", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByAuthNotConfigured";
        }

        if (blockedReasons.Contains("SecretLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedBySecretLeak";
        }

        if (blockedReasons.Contains("AbsolutePathLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByAbsolutePathLeak";
        }

        if (blockedReasons.Any(static item => item.Contains("Schema", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByEnvelopeSchemaMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("Client", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByClientContractMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Formal", StringComparison.OrdinalIgnoreCase)
                || item.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
                || item.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByForbiddenActionExposure";
        }

        if (blockedReasons.Any(static item => item.Contains("Degraded", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByDegradedBehaviorMismatch";
        }

        return "KeepReadOnlyOnly";
    }

    private static string BuildOpenApiContractRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Count == 0)
        {
            return "ReadyForOpenApiContractFreeze";
        }

        if (blockedReasons.Contains("SecretLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedBySecretLeak";
        }

        if (blockedReasons.Contains("AbsolutePathLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByAbsolutePathLeak";
        }

        if (blockedReasons.Any(static item => item.Contains("AuthScheme", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByAuthDowngrade";
        }

        if (blockedReasons.Any(static item => item.Contains("Client", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByClientContractMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("Schema", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByEnvelopeSchemaMismatch";
        }

        return "BlockedByBreakingChange";
    }

    private static string BuildHostedSmokeRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Count == 0)
        {
            return "ReadyForHostedReadOnlyService";
        }

        if (blockedReasons.Contains("HostedServiceNotConfigured", StringComparer.OrdinalIgnoreCase))
        {
            return "NeedsHostedServiceConfig";
        }

        if (blockedReasons.Contains("SecretLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedBySecretLeak";
        }

        if (blockedReasons.Contains("AbsolutePathLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByAbsolutePathLeak";
        }

        if (blockedReasons.Any(static item => item.Contains("Auth", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByAuthFailure";
        }

        if (blockedReasons.Any(static item => item.Contains("Envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByContractMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Formal", StringComparison.OrdinalIgnoreCase)
                || item.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
                || item.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)
                || item.Contains("ReadyForRuntimeSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByRuntimeMutation";
        }

        return "NeedsHostedServiceConfig";
    }

    private static string BuildServiceFoundationFreezeRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Count == 0)
        {
            return "ReadyForV45ExplicitScopedRuntimeExperimentPlanning";
        }

        if (blockedReasons.Any(static item => item.Contains("RuntimeMutation", StringComparison.OrdinalIgnoreCase)
                || item.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
                || item.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByRuntimeMutation";
        }

        if (blockedReasons.Contains("FormalRetrievalAllowed", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByFormalRetrieval";
        }

        if (blockedReasons.Any(static item => item.Contains("RuntimeSwitch", StringComparison.OrdinalIgnoreCase)
                || item.Contains("ReadyForRuntimeSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByRuntimeSwitch";
        }

        if (blockedReasons.Contains("P15GateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByP15";
        }

        if (blockedReasons.Any(static item => item.Contains("Hosted", StringComparison.OrdinalIgnoreCase)
                || item.Contains("ReadonlyRuntime", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Svc6", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByHostedSmoke";
        }

        if (blockedReasons.Any(static item => item.Contains("Drift", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Svc5", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByContractDrift";
        }

        if (blockedReasons.Any(static item => item.Contains("Deployment", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Auth", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Svc4", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByAuthDeployment";
        }

        return "BlockedByMissingServiceGate";
    }

    private static string NormalizeHostedBaseUrlForReport(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "invalid";
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string BuildAuthRecommendation(
        FoundationServiceAuthOptions options,
        bool authConfigured,
        bool developmentNoAuthAllowed,
        IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Contains("SecretLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedBySecretLeak";
        }

        if (blockedReasons.Contains("AbsolutePathLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByAbsolutePathLeak";
        }

        if (blockedReasons.Contains("ProductionAuthNotConfigured", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByProductionAuthMissing";
        }

        if (blockedReasons.Contains("ApiKeyRequiredButMissing", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByMissingApiKey";
        }

        if (developmentNoAuthAllowed)
        {
            return "DevelopmentOnly";
        }

        if (authConfigured)
        {
            return options.DeploymentProfile == ServiceDeploymentProfile.Production
                ? "ReadyForProductionServiceProfile"
                : "ReadyForServiceProfile";
        }

        return "NotConfigured";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), out var value)
                ? value
                : null;

    private static void AppendList(StringBuilder builder, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static string ToPassedStatus(bool passed)
        => passed ? "Passed" : "MissingOrFailed";

    private static void AddIfFalse(ICollection<string> failed, bool condition, string reason)
    {
        if (!condition)
        {
            failed.Add(reason);
        }
    }

    private sealed record ReportDefinition(
        string ReportId,
        string CapabilityId,
        string RelativePath);
}
