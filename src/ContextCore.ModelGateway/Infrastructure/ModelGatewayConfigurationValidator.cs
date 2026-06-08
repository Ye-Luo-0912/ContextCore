using ContextCore.Abstractions;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>检查模型网关配置是否足以启动和调用已启用模型。</summary>
public static class ModelGatewayConfigurationValidator
{
    public static IReadOnlyList<ModelGatewayConfigurationIssue> Validate(
        ModelGatewayOptions options,
        ApiKeyResolver? apiKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolver = apiKeyResolver ?? new ApiKeyResolver();
        var issues = new List<ModelGatewayConfigurationIssue>();
        ValidateApiProviders(options, issues);
        ValidateModelProfiles(options, issues);

        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        var modelNames = effectiveOptions.Models
            .Select(model => model.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateRoutes(effectiveOptions, modelNames, issues);

        foreach (var model in effectiveOptions.Models.Where(model => model.Enabled))
        {
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = model.Name,
                    Code = "ModelNameRequired",
                    Message = "启用的模型端点必须配置 Name。"
                });
            }

            if (RequiresEndpoint(model) && string.IsNullOrWhiteSpace(model.Endpoint))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = model.Name,
                    Code = "EndpointRequired",
                    Message = $"启用的模型 '{model.Name}' 必须配置 Endpoint。"
                });
            }

            var apiKey = resolver.Resolve(model);
            if (apiKey.Required && !apiKey.Configured)
            {
                var source = string.IsNullOrWhiteSpace(apiKey.EnvironmentVariableName)
                    ? "ApiKey"
                    : $"环境变量 '{apiKey.EnvironmentVariableName}'";
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = model.Name,
                    Code = "ApiKeyRequired",
                    Message = $"启用的模型 '{model.Name}' 需要 API 密钥。请在启动前配置 {source}。"
                });
            }
        }

        return issues;
    }

    public static void ThrowIfInvalid(
        ModelGatewayOptions options,
        ApiKeyResolver? apiKeyResolver = null)
    {
        var issues = Validate(options, apiKeyResolver);
        if (issues.Count == 0)
        {
            return;
        }

        var message = string.Join(
            Environment.NewLine,
            issues.Select(issue => $"- [{issue.Code}] {issue.Message}"));
        throw new InvalidOperationException($"ModelGateway 配置无效：{Environment.NewLine}{message}");
    }

    private static bool RequiresEndpoint(ModelEndpointOptions model)
    {
        return !model.Provider.Equals("mock", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateApiProviders(
        ModelGatewayOptions options,
        List<ModelGatewayConfigurationIssue> issues)
    {
        foreach (var provider in options.ApiProviders.Where(provider => provider.Enabled))
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = provider.Name,
                    Code = "ApiProviderNameRequired",
                    Message = "启用的 API 平台必须配置 Name。"
                });
            }

            if (!provider.Provider.Equals("mock", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = provider.Name,
                    Code = "ApiProviderEndpointRequired",
                    Message = $"启用的 API 平台 '{provider.Name}' 必须配置 Endpoint。"
                });
            }
        }
    }

    private static void ValidateModelProfiles(
        ModelGatewayOptions options,
        List<ModelGatewayConfigurationIssue> issues)
    {
        if (options.ModelProfiles.Count == 0)
        {
            return;
        }

        var providerNames = options.ApiProviders
            .Select(provider => provider.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in options.ModelProfiles.Where(profile => profile.Enabled))
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = profile.Name,
                    Code = "ModelProfileNameRequired",
                    Message = "启用的模型 Profile 必须配置 Name。"
                });
            }

            if (string.IsNullOrWhiteSpace(profile.ApiProviderName)
                || !providerNames.Contains(profile.ApiProviderName))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = profile.Name,
                    Code = "ApiProviderMissing",
                    Message = $"模型 Profile '{profile.Name}' 引用了不存在的 API 平台 '{profile.ApiProviderName}'。"
                });
            }

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = profile.Name,
                    Code = "ProfileModelRequired",
                    Message = $"模型 Profile '{profile.Name}' 必须指定提供商侧模型 ID。"
                });
            }
        }
    }

    private static void ValidateRoutes(
        ModelGatewayOptions options,
        HashSet<string> modelNames,
        List<ModelGatewayConfigurationIssue> issues)
    {
        foreach (var route in options.Routes)
        {
            if (!string.IsNullOrWhiteSpace(route.PrimaryModelName)
                && !modelNames.Contains(route.PrimaryModelName))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = route.PrimaryModelName,
                    Code = "RoutePrimaryModelMissing",
                    Message = $"角色 '{route.Role}' 的路由引用了不存在的主模型 '{route.PrimaryModelName}'。"
                });
            }

            if (!string.IsNullOrWhiteSpace(route.FallbackModelName)
                && !modelNames.Contains(route.FallbackModelName))
            {
                issues.Add(new ModelGatewayConfigurationIssue
                {
                    ModelName = route.FallbackModelName!,
                    Code = "RouteFallbackModelMissing",
                    Message = $"角色 '{route.Role}' 的路由引用了不存在的备用模型 '{route.FallbackModelName}'。"
                });
            }
        }
    }
}

/// <summary>模型网关配置问题。</summary>
public sealed class ModelGatewayConfigurationIssue
{
    public string ModelName { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
