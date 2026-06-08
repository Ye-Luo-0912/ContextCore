using ContextCore.Abstractions;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>
/// 统一解析模型端点 API Key，支持直接值、<c>env:NAME</c> 和 <c>&lt;from-env&gt;</c> 占位符。
/// </summary>
public sealed class ApiKeyResolver
{
    public ApiKeyResolution Resolve(ModelEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var required = RequiresApiKey(options);
        var configuredValue = options.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return new ApiKeyResolution
            {
                Required = required,
                Configured = false,
                Source = required ? "not-configured" : "not-required"
            };
        }

        if (configuredValue.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envName = configuredValue[4..].Trim();
            if (string.IsNullOrWhiteSpace(envName))
            {
                return new ApiKeyResolution
                {
                    Required = required,
                    Configured = false,
                    Source = "environment",
                    Error = "环境变量名称为空。"
                };
            }

            var value = Environment.GetEnvironmentVariable(envName);
            return new ApiKeyResolution
            {
                Required = required,
                Configured = !string.IsNullOrWhiteSpace(value),
                Source = "environment",
                EnvironmentVariableName = envName,
                Value = value
            };
        }

        if (string.Equals(configuredValue, "<from-env>", StringComparison.OrdinalIgnoreCase))
        {
            var envName = ResolveEnvironmentVariableName(options);
            var value = Environment.GetEnvironmentVariable(envName);
            return new ApiKeyResolution
            {
                Required = required,
                Configured = !string.IsNullOrWhiteSpace(value),
                Source = options.Metadata.ContainsKey("apiKeyEnv") ? "metadata-environment" : "default-environment",
                EnvironmentVariableName = envName,
                Value = value
            };
        }

        return new ApiKeyResolution
        {
            Required = required,
            Configured = true,
            Source = "inline",
            Value = configuredValue
        };
    }

    public bool RequiresApiKey(ModelEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Metadata.TryGetValue("requiresApiKey", out var configured)
            && bool.TryParse(configured, out var requiresApiKey))
        {
            return requiresApiKey;
        }

        return options.Provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
            || options.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            || options.Provider.Equals("bigmodel", StringComparison.OrdinalIgnoreCase)
            || options.Provider.Equals("glm", StringComparison.OrdinalIgnoreCase)
            || options.Provider.Equals("zhipu", StringComparison.OrdinalIgnoreCase)
            || options.Provider.Equals("zhipuai", StringComparison.OrdinalIgnoreCase)
            || options.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEnvironmentVariableName(ModelEndpointOptions options)
    {
        if (options.Metadata.TryGetValue("apiKeyEnv", out var envName)
            && !string.IsNullOrWhiteSpace(envName))
        {
            return envName.Trim();
        }

        if (ContainsProviderOrName(options, "deepseek"))
        {
            return "DEEPSEEK_API_KEY";
        }

        if (ContainsProviderOrName(options, "bigmodel")
            || ContainsProviderOrName(options, "glm"))
        {
            return "BIGMODEL_API_KEY";
        }

        var normalizedName = options.Name
            .ToUpperInvariant()
            .Replace('-', '_')
            .Replace('.', '_')
            .Replace(' ', '_');
        return $"CONTEXTCORE_MODEL_{normalizedName}_API_KEY";
    }

    private static bool ContainsProviderOrName(ModelEndpointOptions options, string value)
    {
        return options.Provider.Contains(value, StringComparison.OrdinalIgnoreCase)
            || options.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
            || options.Metadata.TryGetValue("model", out var model)
                && model.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>API Key 解析结果。<see cref="Value"/> 只供调用适配器使用，不应显示到日志或 UI。</summary>
public sealed class ApiKeyResolution
{
    public bool Required { get; init; }

    public bool Configured { get; init; }

    public string Source { get; init; } = string.Empty;

    public string? EnvironmentVariableName { get; init; }

    public string? Value { get; init; }

    public string? Error { get; init; }
}
