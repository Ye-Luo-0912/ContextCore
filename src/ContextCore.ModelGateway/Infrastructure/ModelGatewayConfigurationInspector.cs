using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>生成不包含明文凭据的模型端点配置状态。</summary>
public static class ModelGatewayConfigurationInspector
{
    public static IReadOnlyList<ModelEndpointConfigurationStatus> Inspect(
        ModelGatewayOptions options,
        ApiKeyResolver? apiKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolver = apiKeyResolver ?? new ApiKeyResolver();
        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        return [.. effectiveOptions.Models.Select(model => Inspect(model, resolver))];
    }

    public static ModelEndpointConfigurationStatus Inspect(
        ModelEndpointOptions model,
        ApiKeyResolver? apiKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var resolver = apiKeyResolver ?? new ApiKeyResolver();
        var apiKey = resolver.Resolve(model);
        var endpointConfigured = model.Provider.Equals("mock", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(model.Endpoint);
        var configurationError = ResolveConfigurationError(model, apiKey, endpointConfigured);

        return new ModelEndpointConfigurationStatus
        {
            Name = model.Name,
            Provider = model.Provider,
            Enabled = model.Enabled,
            EndpointConfigured = endpointConfigured,
            ApiKeyRequired = apiKey.Required,
            ApiKeyConfigured = apiKey.Configured,
            ApiKeySource = apiKey.Source,
            ApiKeyEnvironmentVariable = apiKey.EnvironmentVariableName,
            ConfigurationError = configurationError
        };
    }

    private static string? ResolveConfigurationError(
        ModelEndpointOptions model,
        ApiKeyResolution apiKey,
        bool endpointConfigured)
    {
        if (!model.Enabled)
        {
            return null;
        }

        if (!endpointConfigured)
        {
            return "端点未配置";
        }

        if (apiKey.Error is not null)
        {
            return apiKey.Error;
        }

        if (apiKey.Required && !apiKey.Configured)
        {
            return string.IsNullOrWhiteSpace(apiKey.EnvironmentVariableName)
                ? "API 密钥未配置"
                : $"环境变量 '{apiKey.EnvironmentVariableName}' 未配置";
        }

        return null;
    }
}

/// <summary>不含明文 API Key 的模型端点配置状态。</summary>
public sealed class ModelEndpointConfigurationStatus
{
    public string Name { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool EndpointConfigured { get; init; }

    public bool ApiKeyRequired { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public string ApiKeySource { get; init; } = string.Empty;

    public string? ApiKeyEnvironmentVariable { get; init; }

    public string? ConfigurationError { get; init; }
}
