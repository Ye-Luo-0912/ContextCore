using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Evaluation.Services;

/// <summary>
/// 历史产物解析报告构建器；依赖 <see cref="FoundationStatusService"/> 提供只读状态聚合，
/// 构建契约与冻结报告。仅服务于 Evaluation 项目的 EvalCommand.Service。
/// </summary>
public sealed class FoundationReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    private readonly FoundationStatusService _statusService;
    private readonly string _rootDirectory;

    public FoundationReportBuilder(FoundationStatusService statusService, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _statusService = statusService;
        _rootDirectory = rootDirectory;
    }

    public static FoundationApiSecurityDiagnosticsReport BuildSecurityDiagnostics(
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

    public async Task<FoundationApiContractReport> BuildContractReportAsync(
        FoundationApiSecurityDiagnosticsReport securityDiagnostics,
        bool productionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityDiagnostics);

        var status = await _statusService.GetStatusAsync("foundation/status", cancellationToken).ConfigureAwait(false);
        var navigation = await _statusService.GetReportNavigationEnvelopeAsync(cancellationToken).ConfigureAwait(false);
        var missingProbeRoot = Path.Combine(_rootDirectory, ".foundation-contract-missing-report-probe");
        var missingProbe = await new FoundationStatusService(missingProbeRoot)
            .GetStatusEnvelopeAsync("foundation/status", cancellationToken)
            .ConfigureAwait(false);

        return BuildContractReport(status, navigation, missingProbe, securityDiagnostics, productionMode);
    }

    private FoundationApiContractReport BuildContractReport(
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
        var endpointContractStable = EndpointContracts.Count == 3
            && EndpointContracts.All(static endpoint => endpoint.ReadOnly && endpoint.UsesEnvelope);
        var clientContractStable = ClientMethodContracts.Count == 3
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
        var reportNavigationSchemaStable = navigation.SchemaVersion == FoundationStatusService.EnvelopeSchemaVersion
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
            EnvelopeSchemaVersion = FoundationStatusService.EnvelopeSchemaVersion,
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

    public async Task<ServiceFoundationFreezeReport> BuildServiceFoundationFreezeReportAsync(
        CancellationToken cancellationToken = default)
    {
        var serviceStatus = await ReadJsonAsync<ServiceFoundationStatusSmokeReport>(
                FoundationStatusService.ServiceFoundationStatusSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var serviceReadiness = await ReadJsonAsync<ServiceFoundationStatusSmokeReport>(
                FoundationStatusService.ServiceReadinessApiSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var security = await ReadJsonAsync<FoundationApiSecurityDiagnosticsReport>(
                FoundationStatusService.ServiceApiSecurityDiagnosticsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var navigation = await ReadJsonAsync<ServiceReportNavigationSmokeReport>(
                FoundationStatusService.ServiceReportNavigationSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var contract = await ReadJsonAsync<FoundationApiContractReport>(
                FoundationStatusService.ServiceApiContractFreezeGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var deployment = await ReadJsonAsync<FoundationServiceDeploymentProfileGateReport>(
                FoundationStatusService.ServiceDeploymentProfileGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var drift = await ReadJsonAsync<FoundationOpenApiContractReport>(
                FoundationStatusService.ServiceApiContractDriftGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var hosted = await ReadJsonAsync<HostedServiceSmokeReport>(
                FoundationStatusService.ServiceHostedDeploymentSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var readonlyRuntime = await ReadJsonAsync<HostedServiceSmokeReport>(
                FoundationStatusService.ServiceReadonlyRuntimeSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var hostedContract = await ReadJsonAsync<HostedServiceSmokeReport>(
                FoundationStatusService.ServiceHostedApiContractSmokePath,
                cancellationToken)
            .ConfigureAwait(false);
        var foundation = await ReadJsonAsync<ContextCoreFoundationFreezeReport>(
                FoundationStatusService.FoundationReleaseCandidatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var reproducibility = await ReadJsonAsync<FoundationReproducibilityReport>(
                FoundationStatusService.FoundationReproducibilityPath,
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonAsync<LearningRuntimeChangeReadinessGateReport>(
                FoundationStatusService.RuntimeChangeGatePath,
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

    private ServiceFoundationFreezeReport BuildServiceFoundationFreezeReport(
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

    private static int ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static void AddIfFalse(ICollection<string> failed, bool condition, string reason)
    {
        if (!condition)
        {
            failed.Add(reason);
        }
    }
}
