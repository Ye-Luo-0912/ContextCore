using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Runners;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Evaluation.Commands;

public static partial class EvalCommand
{
    private static async Task ExecuteServiceApiContractAsync(
        string subcommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath("service");
        Directory.CreateDirectory(outputDirectory);

        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var security = ReadServiceSecurityConfigurationSnapshot();
        var statusEnvelope = await service.GetStatusEnvelopeAsync("foundation/status", cancellationToken)
            .ConfigureAwait(false);
        var reportsEnvelope = await service.GetReportNavigationEnvelopeAsync(cancellationToken)
            .ConfigureAwait(false);
        var securityDiagnostics = service.BuildSecurityDiagnostics(
            security.RequireApiKey,
            security.ApiKeyConfigured,
            security.DevelopmentMode,
            [
                JsonSerializer.Serialize(statusEnvelope, JsonOptions),
                JsonSerializer.Serialize(reportsEnvelope, JsonOptions)
            ],
            security.SecretProbe);
        var productionMode = CommandHelpers.HasFlag(args, "--production");
        var report = await service.BuildContractReportAsync(securityDiagnostics, productionMode, cancellationToken)
            .ConfigureAwait(false);

        var fileName = string.Equals(subcommand, "service-api-contract-freeze-gate", StringComparison.OrdinalIgnoreCase)
            ? "service-api-contract-freeze-gate"
            : "service-api-contract-report";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(FoundationStatusService.BuildContractMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Service API contract written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.FreezePassed}; recommendation={report.Recommendation}; endpoints={report.EndpointCount}; clientMethods={report.ClientMethodCount}; schema={report.EnvelopeSchemaVersion}; auth={report.AuthMode}; degraded={report.DegradedBehaviorStable}");
    }


    private static async Task ExecuteServiceFoundationFreezeGateAsync(CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath("service");
        Directory.CreateDirectory(outputDirectory);

        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = await service.BuildServiceFoundationFreezeReportAsync(cancellationToken)
            .ConfigureAwait(false);

        var jsonPath = Path.Combine(outputDirectory, "service-foundation-freeze-gate.json");
        var markdownPath = Path.Combine(outputDirectory, "service-foundation-freeze-gate.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(FoundationStatusService.BuildServiceFoundationFreezeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Service foundation freeze gate written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.FreezePassed}; recommendation={report.Recommendation}; serviceFoundation={report.ServiceFoundation}; hosted={report.Svc6HostedReadOnlySmokePassed}; runtimeMutationAllowed={report.RuntimeMutationAllowed}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}");
    }

    private static HostedServiceSmokeOptions BuildHostedServiceSmokeOptions(
        ServiceSecurityConfigurationSnapshot security,
        IReadOnlyList<string> args)
    {
        var baseUrl = CommandHelpers.GetOption(args, "--base-url")
            ?? Environment.GetEnvironmentVariable("CONTEXTCORE_SERVICE_BASE_URL")
            ?? Environment.GetEnvironmentVariable("FoundationHostedService__BaseUrl")
            ?? string.Empty;
        var profile = ParseServiceDeploymentProfile(
            CommandHelpers.GetOption(args, "--profile")
            ?? Environment.GetEnvironmentVariable("CONTEXTCORE_SERVICE_DEPLOYMENT_PROFILE")
            ?? Environment.GetEnvironmentVariable("FoundationServiceAuth__DeploymentProfile")
            ?? (security.DevelopmentMode ? "Development" : "Development"));
        var requireApiKey = CommandHelpers.HasFlag(args, "--require-api-key")
            || profile != ServiceDeploymentProfile.Development && security.RequireApiKey;
        var timeoutSeconds = CommandHelpers.GetIntOption(args, "--timeout-seconds", 15);
        return new HostedServiceSmokeOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            BaseUrl = baseUrl,
            DeploymentProfile = profile,
            RequireApiKey = requireApiKey,
            ApiKeyHeaderName = security.ApiKeyHeaderName,
            TimeoutSeconds = timeoutSeconds,
            VerifyReadOnly = true,
            VerifyNoRuntimeMutation = true
        };
    }

    private static async Task<bool> ProbeWrongApiKeyAsync(
        HttpClient http,
        HostedServiceSmokeOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.RequireApiKey)
        {
            return true;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/admin/foundation/status");
        request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, "contextcore-invalid-api-key");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<HostedServiceEndpointProbeResult> ProbeHostedEndpointAsync(
        HttpClient http,
        FoundationApiEndpointContract endpoint,
        HostedServiceSmokeOptions options,
        string? apiKey,
        string? secretProbe,
        CancellationToken cancellationToken)
    {
        var route = endpoint.Route.Replace("{reportId}", "foundation-release-candidate-gate", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, route.TrimStart('/'));
        if (options.RequireApiKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, apiKey);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = TryParseHostedEnvelope(content);
            var success = response.IsSuccessStatusCode && parsed.EnvelopeSchemaMatched;
            return new HostedServiceEndpointProbeResult
            {
                Method = endpoint.Method,
                Route = endpoint.Route,
                StatusCode = (int)response.StatusCode,
                Success = success,
                EnvelopeSchemaMatched = parsed.EnvelopeSchemaMatched,
                SecretLeakDetected = ContainsHostedSecretLeak(content, secretProbe),
                AbsolutePathLeakDetected = ContainsHostedAbsolutePathLeak(content),
                RuntimeMutated = parsed.RuntimeMutated,
                FormalRetrievalAllowed = parsed.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = parsed.RuntimeSwitchAllowed,
                ReadyForRuntimeSwitch = parsed.ReadyForRuntimeSwitch,
                PackingPolicyChanged = parsed.PackingPolicyChanged,
                PackageOutputChanged = parsed.PackageOutputChanged,
                Error = success ? string.Empty : $"HTTP {(int)response.StatusCode}"
            };
        }
        catch (HttpRequestException ex)
        {
            return BuildHostedProbeError(endpoint, ex.Message);
        }
        catch (JsonException ex)
        {
            return BuildHostedProbeError(endpoint, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildHostedProbeError(endpoint, ex.Message);
        }
    }

    private static HostedServiceEndpointProbeResult BuildHostedProbeError(
        FoundationApiEndpointContract endpoint,
        string error)
        => new()
        {
            Method = endpoint.Method,
            Route = endpoint.Route,
            StatusCode = 0,
            Success = false,
            EnvelopeSchemaMatched = false,
            Error = error
        };

    private static HostedEnvelopeProbe TryParseHostedEnvelope(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new HostedEnvelopeProbe();
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new HostedEnvelopeProbe();
        }

        var schemaMatched = TryGetHostedProperty(root, "SchemaVersion", out var schema)
            && schema.ValueKind == JsonValueKind.String
            && string.Equals(schema.GetString(), FoundationStatusService.EnvelopeSchemaVersion, StringComparison.Ordinal);
        var data = TryGetHostedProperty(root, "Data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : default;

        return new HostedEnvelopeProbe(
            schemaMatched,
            TryGetHostedBool(data, "RuntimeMutated"),
            TryGetHostedBool(data, "FormalRetrievalAllowed"),
            TryGetHostedBool(data, "RuntimeSwitchAllowed"),
            TryGetHostedBool(data, "ReadyForRuntimeSwitch"),
            TryGetHostedBool(data, "PackingPolicyChanged"),
            TryGetHostedBool(data, "PackageOutputChanged"));
    }

    private static bool TryGetHostedBool(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && TryGetHostedProperty(element, propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();

    private static bool TryGetHostedProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool ContainsHostedAbsolutePathLeak(string value)
        => !string.IsNullOrWhiteSpace(value)
            && (value.Contains(@":\", StringComparison.Ordinal)
                || value.Contains("/home/", StringComparison.OrdinalIgnoreCase)
                || value.Contains("\"C:", StringComparison.OrdinalIgnoreCase)
                || value.Contains("\"D:", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsHostedSecretLeak(string value, string? secretProbe)
        => !string.IsNullOrWhiteSpace(value)
            && (value.Contains(".contextcore", StringComparison.OrdinalIgnoreCase)
                || value.Contains("secrets.json", StringComparison.OrdinalIgnoreCase)
                || value.Contains("model_int8.onnx", StringComparison.OrdinalIgnoreCase)
                || value.Contains(".onnx", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(secretProbe)
                    && value.Contains(secretProbe, StringComparison.Ordinal)));

    private readonly record struct HostedEnvelopeProbe(
        bool EnvelopeSchemaMatched = false,
        bool RuntimeMutated = false,
        bool FormalRetrievalAllowed = false,
        bool RuntimeSwitchAllowed = false,
        bool ReadyForRuntimeSwitch = false,
        bool PackingPolicyChanged = false,
        bool PackageOutputChanged = false);

    private static async Task<IReadOnlyList<string>> BuildFoundationAuthDiagnosticPayloadsAsync(
        FoundationStatusService service,
        CancellationToken cancellationToken)
    {
        var statusEnvelope = await service.GetStatusEnvelopeAsync("foundation/status", cancellationToken)
            .ConfigureAwait(false);
        var reportsEnvelope = await service.GetReportNavigationEnvelopeAsync(cancellationToken)
            .ConfigureAwait(false);
        return
        [
            JsonSerializer.Serialize(statusEnvelope, JsonOptions),
            JsonSerializer.Serialize(reportsEnvelope, JsonOptions)
        ];
    }

    private static FoundationServiceAuthOptions BuildFoundationServiceAuthOptions(
        ServiceSecurityConfigurationSnapshot security,
        IReadOnlyList<string> args)
    {
        var profile = ParseServiceDeploymentProfile(
            CommandHelpers.GetOption(args, "--profile")
            ?? Environment.GetEnvironmentVariable("CONTEXTCORE_SERVICE_DEPLOYMENT_PROFILE")
            ?? Environment.GetEnvironmentVariable("FoundationServiceAuth__DeploymentProfile")
            ?? (security.DevelopmentMode ? "Development" : "Development"));
        return new FoundationServiceAuthOptions
        {
            Enabled = true,
            DeploymentProfile = profile,
            RequireApiKey = profile != ServiceDeploymentProfile.Development,
            ApiKeyHeaderName = security.ApiKeyHeaderName,
            AllowDevelopmentNoAuth = true,
            RedactSecrets = true,
            FailOnSecretLeak = true
        };
    }

    private static ServiceDeploymentProfile ParseServiceDeploymentProfile(string? value)
    {
        if (Enum.TryParse<ServiceDeploymentProfile>(value, ignoreCase: true, out var profile))
        {
            return profile;
        }

        return ServiceDeploymentProfile.Development;
    }

    private static ServiceSecurityConfigurationSnapshot ReadServiceSecurityConfigurationSnapshot()
    {
        var requireApiKey = true;
        var apiKeyConfigured = false;
        var apiKeyHeaderName = "X-ContextCore-Key";
        var developmentMode = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);
        string? secretProbe = null;

        ReadSecurityJson(Path.Combine("src", "ContextCore.Service", "appsettings.json"), ref requireApiKey, ref apiKeyConfigured, ref apiKeyHeaderName, ref secretProbe);
        if (developmentMode)
        {
            ReadSecurityJson(Path.Combine("src", "ContextCore.Service", "appsettings.Development.json"), ref requireApiKey, ref apiKeyConfigured, ref apiKeyHeaderName, ref secretProbe);
        }

        var userSecretsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".contextcore",
            "secrets.json");
        ReadSecurityJson(userSecretsPath, ref requireApiKey, ref apiKeyConfigured, ref apiKeyHeaderName, ref secretProbe);

        var envApiKey = Environment.GetEnvironmentVariable("CONTEXTCORE_API_KEY")
            ?? Environment.GetEnvironmentVariable("Security__ApiKey");
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            apiKeyConfigured = true;
            secretProbe ??= envApiKey;
        }

        return new ServiceSecurityConfigurationSnapshot(requireApiKey, apiKeyConfigured, apiKeyHeaderName, developmentMode || !requireApiKey, secretProbe);
    }

    private static void ReadSecurityJson(
        string path,
        ref bool requireApiKey,
        ref bool apiKeyConfigured,
        ref string apiKeyHeaderName,
        ref string? secretProbe)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Security", out var security)
                || security.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (security.TryGetProperty("RequireApiKey", out var require)
                && (require.ValueKind == JsonValueKind.True || require.ValueKind == JsonValueKind.False))
            {
                requireApiKey = require.GetBoolean();
            }

            if (security.TryGetProperty("ApiKey", out var apiKey)
                && apiKey.ValueKind == JsonValueKind.String)
            {
                var value = apiKey.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    apiKeyConfigured = true;
                    secretProbe ??= value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
                        ? Environment.GetEnvironmentVariable(value[4..])
                        : value;
                }
            }

            if (security.TryGetProperty("ApiKeyHeaderName", out var headerName)
                && headerName.ValueKind == JsonValueKind.String)
            {
                var value = headerName.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    apiKeyHeaderName = value;
                }
            }
        }
        catch (JsonException)
        {
            // 配置诊断保持 best-effort；解析失败会自然表现为未配置。
        }
        catch (IOException)
        {
            // 配置诊断保持 best-effort；读取失败会自然表现为未配置。
        }
    }

    private sealed record ServiceSecurityConfigurationSnapshot(
        bool RequireApiKey,
        bool ApiKeyConfigured,
        string ApiKeyHeaderName,
        bool DevelopmentMode,
        string? SecretProbe);

    private static async Task<int?> TryReadHybridScoringRepairRiskCountAsync(
        string outputDirectory,
        string profileName,
        CancellationToken cancellationToken)
    {
        foreach (var fileName in new[]
        {
            "hybrid-scoring-repair-shadow-eval.json",
            "hybrid-scoring-repair-preview.json",
            "hybrid-scoring-repair-gate.json"
        })
        {
            var path = Path.Combine(outputDirectory, fileName);
            var report = await ReadJsonFileAsync<HybridUnionScoringRepairReport>(path, cancellationToken)
                .ConfigureAwait(false);
            var profile = report?.Profiles.FirstOrDefault(item =>
                string.Equals(item.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
            {
                return profile.RiskAfterPolicy;
            }
        }

        return null;
    }

    private static async Task<RetrievalDatasetV2MaterializationReport> BuildCurrentRetrievalDatasetV2MaterializationGateAsync(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2Manifest? existingManifest,
        string corpusPath,
        string samplesPath,
        string validationReportPath,
        string qualityReportPath,
        CancellationToken cancellationToken)
    {
        var generator = new RetrievalDatasetV2Generator();
        var validationReport = await ReadJsonFileAsync<RetrievalDatasetV2ValidationReport>(validationReportPath, cancellationToken)
            .ConfigureAwait(false);
        if (validationReport is null && dataset.CorpusItems.Count > 0 && dataset.Samples.Count > 0)
        {
            validationReport = generator.Validate(dataset);
        }

        var qualityReport = await ReadJsonFileAsync<RetrievalDatasetV2QualityReport>(qualityReportPath, cancellationToken)
            .ConfigureAwait(false);
        if (qualityReport is null && validationReport is not null && dataset.CorpusItems.Count > 0 && dataset.Samples.Count > 0)
        {
            qualityReport = generator.BuildQualityReport(dataset, validationReport, generator.Judge(dataset));
        }

        var corpusExists = File.Exists(corpusPath);
        var samplesExists = File.Exists(samplesPath);
        var corpusHash = corpusExists ? RetrievalDatasetV2MaterializationRunner.ComputeFileHash(corpusPath) : string.Empty;
        var samplesHash = samplesExists ? RetrievalDatasetV2MaterializationRunner.ComputeFileHash(samplesPath) : string.Empty;
        var materializationRunner = new RetrievalDatasetV2MaterializationRunner();
        var currentManifest = materializationRunner.BuildManifest(
            corpusPath,
            samplesPath,
            dataset.CorpusItems.Count,
            dataset.Samples.Count,
            corpusHash,
            samplesHash);
        if (existingManifest is not null)
        {
            currentManifest = new RetrievalDatasetV2Manifest
            {
                DatasetId = existingManifest.DatasetId,
                CorpusPath = currentManifest.CorpusPath,
                SamplesPath = currentManifest.SamplesPath,
                CorpusItemCount = currentManifest.CorpusItemCount,
                SampleCount = currentManifest.SampleCount,
                CorpusHash = currentManifest.CorpusHash,
                SamplesHash = currentManifest.SamplesHash,
                GeneratorVersion = existingManifest.GeneratorVersion,
                ContractVersion = existingManifest.ContractVersion,
                CreatedAt = existingManifest.CreatedAt,
                UseForRuntime = false,
                FormalRetrievalAllowed = false
            };
        }

        return materializationRunner.BuildReport(
            currentManifest,
            validationReport,
            qualityReport,
            existingManifest,
            corpusExists,
            samplesExists,
            requireExistingManifest: true);
    }
}
