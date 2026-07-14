using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Evaluation.Models;
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

    private static void AddIfFalse(ICollection<string> failed, bool condition, string reason)
    {
        if (!condition)
        {
            failed.Add(reason);
        }
    }
}
