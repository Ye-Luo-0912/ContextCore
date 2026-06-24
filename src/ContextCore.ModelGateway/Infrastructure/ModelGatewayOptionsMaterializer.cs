using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>将 API 平台 + 模型 profile 的声明式配置展开为运行时模型端点。</summary>
public static class ModelGatewayOptionsMaterializer
{
    public static ModelGatewayOptions Materialize(ModelGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ApiProviders.Count == 0 && options.ModelProfiles.Count == 0)
        {
            return options;
        }

        var apiProviders = options.ApiProviders
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Name))
            .ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
        var models = options.Models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in options.ModelProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name)
                || string.IsNullOrWhiteSpace(profile.ApiProviderName)
                || !apiProviders.TryGetValue(profile.ApiProviderName, out var apiProvider))
            {
                continue;
            }

            // 旧式 Models 中同名端点优先，便于局部覆盖 materialized profile。
            if (models.ContainsKey(profile.Name))
            {
                continue;
            }

            models[profile.Name] = CreateEndpoint(apiProvider, profile);
        }

        return new ModelGatewayOptions
        {
            ApiProviders = options.ApiProviders,
            ModelProfiles = options.ModelProfiles,
            Models = models.Values.ToArray(),
            Routes = options.Routes
        };
    }

    private static ModelEndpointOptions CreateEndpoint(
        ModelApiProviderOptions apiProvider,
        ModelProfileOptions profile)
    {
        var metadata = new Dictionary<string, string>(apiProvider.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["apiProviderName"] = apiProvider.Name,
            ["model"] = string.IsNullOrWhiteSpace(profile.Model) ? profile.Name : profile.Model
        };

        AddMetadata(metadata, "category", profile.Category);
        AddMetadata(metadata, "capabilities", profile.Capabilities);
        AddMetadata(metadata, "roles", profile.Roles);
        AddMetadata(metadata, "taskKinds", profile.TaskKinds);
        AddMetadata(metadata, "thinkingModes", profile.ThinkingModes);
        if (profile.SupportsJsonResponseFormat is not null)
        {
            metadata["supportsJsonResponseFormat"] = profile.SupportsJsonResponseFormat.Value ? "true" : "false";
        }

        foreach (var (key, value) in profile.Metadata)
        {
            metadata[key] = value;
        }

        return new ModelEndpointOptions
        {
            Name = profile.Name,
            Provider = apiProvider.Provider,
            Endpoint = apiProvider.Endpoint,
            ApiKey = apiProvider.ApiKey,
            Timeout = profile.Timeout ?? apiProvider.Timeout,
            Enabled = apiProvider.Enabled && profile.Enabled,
            Metadata = metadata
        };
    }

    private static void AddMetadata(
        Dictionary<string, string> metadata,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value.Trim();
        }
    }

    private static void AddMetadata(
        Dictionary<string, string> metadata,
        string key,
        IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            metadata[key] = string.Join(",", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }
}
