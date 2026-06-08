using ContextCore.Abstractions;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>按角色、任务类型、思考模式和模型能力解析一次模型路由。</summary>
public static class ModelRouteResolver
{
    public static ModelRouteResolution Resolve(
        ModelGatewayOptions options,
        ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        var modelOptions = effectiveOptions.Models
            .ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
        var routeMatch = ResolveRoute(effectiveOptions, request);
        if (routeMatch.Route is null)
        {
            return new ModelRouteResolution
            {
                Role = request.Role,
                TaskKind = ReadTaskKind(request),
                ThinkingMode = ReadThinkingMode(request),
                RouteSource = ModelRouteSource.None,
                Primary = ModelRouteModelSelection.NotFound("未配置可用的模型路由。")
            };
        }

        var route = routeMatch.Route;
        var effectiveRequiredCapabilities = MergeRequiredCapabilities(
            route.RequiredCapabilities,
            ReadRequiredCapabilities(request));
        var primary = ResolveModel(
            modelOptions,
            route.PrimaryModelName,
            route.PrimaryModelCategory,
            effectiveRequiredCapabilities,
            request);
        var fallback = string.IsNullOrWhiteSpace(route.FallbackModelName)
            && string.IsNullOrWhiteSpace(route.FallbackModelCategory)
                ? null
                : ResolveModel(
                    modelOptions,
                    route.FallbackModelName,
                    route.FallbackModelCategory,
                    effectiveRequiredCapabilities,
                    request);

        return new ModelRouteResolution
        {
            Role = request.Role,
            TaskKind = ReadTaskKind(request),
            ThinkingMode = ReadThinkingMode(request),
            RouteSource = routeMatch.Source,
            Route = route,
            Primary = primary,
            Fallback = fallback
        };
    }

    public static ModelRouteModelSelection ResolveModel(
        ModelGatewayOptions options,
        string? explicitModelName,
        string? category,
        IReadOnlyList<string> requiredCapabilities,
        ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(request);

        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        var modelOptions = effectiveOptions.Models
            .ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
        return ResolveModel(
            modelOptions,
            explicitModelName,
            category,
            requiredCapabilities,
            request);
    }

    private static ResolvedRouteMatch ResolveRoute(
        ModelGatewayOptions options,
        ModelRequest request)
    {
        var exact = SelectBestRoute(
            options.Routes.Where(route => route.Role == request.Role),
            request);
        if (exact is not null)
        {
            return new ResolvedRouteMatch(exact, ModelRouteSource.ExactRole);
        }

        var fallback = SelectBestRoute(
            options.Routes.Where(route => route.Role == ModelRole.Fallback),
            request);
        if (fallback is not null)
        {
            return new ResolvedRouteMatch(fallback, ModelRouteSource.FallbackRole);
        }

        var firstEnabledModel = options.Models.FirstOrDefault(model => model.Enabled);
        return firstEnabledModel is null
            ? new ResolvedRouteMatch(null, ModelRouteSource.None)
            : new ResolvedRouteMatch(new ModelRoleRoute
            {
                Role = request.Role,
                PrimaryModelName = firstEnabledModel.Name
            }, ModelRouteSource.FirstEnabledModel);
    }

    private static ModelRoleRoute? SelectBestRoute(
        IEnumerable<ModelRoleRoute> routes,
        ModelRequest request)
    {
        return routes
            .Select((route, index) => new
            {
                Route = route,
                Score = CalculateRouteScore(route, request),
                Index = index
            })
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Route)
            .FirstOrDefault();
    }

    private static int CalculateRouteScore(
        ModelRoleRoute route,
        ModelRequest request)
    {
        var score = route.Priority;
        if (!RouteFilterMatches(route.TaskKind, ReadTaskKind(request)))
        {
            return -1;
        }

        if (!RouteFilterMatches(route.ThinkingMode, ReadThinkingMode(request)))
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(route.TaskKind))
        {
            score += 100;
        }

        if (!string.IsNullOrWhiteSpace(route.ThinkingMode))
        {
            score += 200;
        }

        return score;
    }

    private static ModelRouteModelSelection ResolveModel(
        IReadOnlyDictionary<string, ModelEndpointOptions> modelOptions,
        string? explicitModelName,
        string? category,
        IReadOnlyList<string> requiredCapabilities,
        ModelRequest request)
    {
        if (!string.IsNullOrWhiteSpace(explicitModelName))
        {
            if (modelOptions.TryGetValue(explicitModelName, out var explicitModel))
            {
                return ModelRouteModelSelection.FromModel(
                    explicitModel,
                    requestedModelName: explicitModelName,
                    requestedCategory: category,
                    requiredCapabilities: requiredCapabilities,
                    score: null,
                    reason: explicitModel.Enabled
                        ? "命中显式模型名称"
                        : "显式模型已配置但处于禁用状态");
            }

            return ModelRouteModelSelection.NotFound(
                $"显式模型 '{explicitModelName}' 未配置。",
                requestedModelName: explicitModelName,
                requestedCategory: category,
                requiredCapabilities: requiredCapabilities);
        }

        var candidates = modelOptions.Values
            .Select(model => new
            {
                Model = model,
                Score = model.Enabled
                    ? CalculateModelProfileScore(model, category, requiredCapabilities, request)
                    : -1
            })
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateModels = candidates
            .Select(candidate => ModelRouteModelCandidate.FromModel(candidate.Model, candidate.Score))
            .ToArray();
        var selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            return ModelRouteModelSelection.NotFound(
                "没有启用的模型满足当前路由约束。",
                requestedModelName: null,
                requestedCategory: category,
                requiredCapabilities: requiredCapabilities);
        }

        return ModelRouteModelSelection.FromModel(
            selected.Model,
            requestedModelName: null,
            requestedCategory: category,
            requiredCapabilities: requiredCapabilities,
            score: selected.Score,
            reason: "类别与能力标签匹配",
            candidates: candidateModels);
    }

    private static int CalculateModelProfileScore(
        ModelEndpointOptions model,
        string? category,
        IReadOnlyList<string> requiredCapabilities,
        ModelRequest request)
    {
        var score = ReadMetadataInt(model, "priority");
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!MetadataValueMatches(model, "category", category))
            {
                return -1;
            }

            score += 60;
        }

        foreach (var capability in requiredCapabilities.Where(capability => !string.IsNullOrWhiteSpace(capability)))
        {
            if (!MetadataListContains(model, "capabilities", capability))
            {
                return -1;
            }

            score += 20;
        }

        score += ScoreOptionalMetadataMatch(model, "roles", request.Role.ToString(), 25);
        score += ScoreOptionalMetadataMatch(model, "taskKinds", ReadTaskKind(request), 20);
        score += ScoreOptionalMetadataMatch(model, "thinkingModes", ReadThinkingMode(request), 20);
        return score;
    }

    private static int ScoreOptionalMetadataMatch(
        ModelEndpointOptions model,
        string metadataKey,
        string? requestValue,
        int score)
    {
        var configured = ReadMetadataList(model, metadataKey);
        if (configured.Count == 0)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(requestValue))
        {
            return 0;
        }

        return configured.Any(value => string.Equals(
                NormalizeRouteValue(value),
                NormalizeRouteValue(requestValue),
                StringComparison.OrdinalIgnoreCase))
                ? score
                : -1000;
    }

    private static bool MetadataValueMatches(
        ModelEndpointOptions model,
        string metadataKey,
        string expected)
    {
        return model.Metadata.TryGetValue(metadataKey, out var value)
            && string.Equals(
                NormalizeRouteValue(value),
                NormalizeRouteValue(expected),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool MetadataListContains(
        ModelEndpointOptions model,
        string metadataKey,
        string expected)
    {
        return ReadMetadataList(model, metadataKey)
            .Any(value => string.Equals(
                NormalizeRouteValue(value),
                NormalizeRouteValue(expected),
                StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> ReadMetadataList(
        ModelEndpointOptions model,
        string metadataKey)
    {
        return model.Metadata.TryGetValue(metadataKey, out var value)
            ? value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
    }

    private static int ReadMetadataInt(
        ModelEndpointOptions model,
        string metadataKey)
    {
        return model.Metadata.TryGetValue(metadataKey, out var value)
            && int.TryParse(value, out var parsed)
                ? parsed
                : 0;
    }

    private static bool RouteFilterMatches(
        string? routeValue,
        string? requestValue)
    {
        if (string.IsNullOrWhiteSpace(routeValue))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestValue))
        {
            return false;
        }

        var normalizedRequest = NormalizeRouteValue(requestValue);
        return routeValue.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(
                NormalizeRouteValue(value),
                normalizedRequest,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadTaskKind(ModelRequest request)
    {
        return ReadMetadataValue(
            request,
            "compressionTask",
            "compressionTaskKind",
            "taskKind",
            "task");
    }

    private static string? ReadThinkingMode(ModelRequest request)
    {
        return ReadMetadataValue(request, "thinkingMode", "reasoningMode");
    }

    private static IReadOnlyList<string> ReadRequiredCapabilities(ModelRequest request)
    {
        var capabilities = new List<string>();
        foreach (var key in new[] { "requiredCapabilities", "requiredCapability" })
        {
            var value = ReadMetadataValue(request, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            capabilities.AddRange(value.Split(
                [',', ';', '|'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return capabilities
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> MergeRequiredCapabilities(
        IReadOnlyList<string> routeCapabilities,
        IReadOnlyList<string> requestCapabilities)
    {
        return routeCapabilities
            .Concat(requestCapabilities)
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadMetadataValue(
        ModelRequest request,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = request.Metadata.FirstOrDefault(entry =>
                string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string NormalizeRouteValue(string value)
    {
        return value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private sealed record ResolvedRouteMatch(ModelRoleRoute? Route, ModelRouteSource Source);
}

/// <summary>模型路由匹配来源，用于诊断命中的规则来自角色、兜底角色还是默认启用模型。</summary>
public enum ModelRouteSource
{
    None,
    ExactRole,
    FallbackRole,
    FirstEnabledModel
}

/// <summary>一次模型路由解析的完整、脱敏诊断结果。</summary>
public sealed class ModelRouteResolution
{
    public ModelRole Role { get; init; } = ModelRole.Fallback;

    public string? TaskKind { get; init; }

    public string? ThinkingMode { get; init; }

    public ModelRouteSource RouteSource { get; init; } = ModelRouteSource.None;

    public ModelRoleRoute? Route { get; init; }

    public ModelRouteModelSelection Primary { get; init; } = ModelRouteModelSelection.NotFound("未解析到主模型。");

    public ModelRouteModelSelection? Fallback { get; init; }
}

/// <summary>模型路由中某一侧（主模型或备用模型）的脱敏解析结果。</summary>
public sealed class ModelRouteModelSelection
{
    public string? RequestedModelName { get; init; }

    public string? RequestedCategory { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    public string? ModelName { get; init; }

    public string? Provider { get; init; }

    public string? ApiProviderName { get; init; }

    public string? ProviderModel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    public bool Found { get; init; }

    public bool Enabled { get; init; }

    public int? Score { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<ModelRouteModelCandidate> Candidates { get; init; } = Array.Empty<ModelRouteModelCandidate>();

    internal static ModelRouteModelSelection FromModel(
        ModelEndpointOptions model,
        string? requestedModelName,
        string? requestedCategory,
        IReadOnlyList<string> requiredCapabilities,
        int? score,
        string reason,
        IReadOnlyList<ModelRouteModelCandidate>? candidates = null)
    {
        return new ModelRouteModelSelection
        {
            RequestedModelName = requestedModelName,
            RequestedCategory = requestedCategory,
            RequiredCapabilities = requiredCapabilities.ToArray(),
            ModelName = model.Name,
            Provider = model.Provider,
            ApiProviderName = ReadMetadata(model, "apiProviderName"),
            ProviderModel = ReadMetadata(model, "model"),
            Category = ReadMetadata(model, "category"),
            Capabilities = ModelRouteResolver.ReadMetadataList(model, "capabilities"),
            Roles = ModelRouteResolver.ReadMetadataList(model, "roles"),
            TaskKinds = ModelRouteResolver.ReadMetadataList(model, "taskKinds"),
            ThinkingModes = ModelRouteResolver.ReadMetadataList(model, "thinkingModes"),
            Found = true,
            Enabled = model.Enabled,
            Score = score,
            Reason = reason,
            Candidates = candidates ?? Array.Empty<ModelRouteModelCandidate>()
        };
    }

    internal static ModelRouteModelSelection NotFound(
        string reason,
        string? requestedModelName = null,
        string? requestedCategory = null,
        IReadOnlyList<string>? requiredCapabilities = null)
    {
        return new ModelRouteModelSelection
        {
            RequestedModelName = requestedModelName,
            RequestedCategory = requestedCategory,
            RequiredCapabilities = requiredCapabilities?.ToArray() ?? Array.Empty<string>(),
            Found = false,
            Enabled = false,
            Reason = reason
        };
    }

    private static string? ReadMetadata(
        ModelEndpointOptions model,
        string key)
    {
        return model.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}

/// <summary>参与某次自动模型选择的候选模型摘要，不包含端点和密钥。</summary>
public sealed class ModelRouteModelCandidate
{
    public string Name { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string? ApiProviderName { get; init; }

    public string? ProviderModel { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    public int Score { get; init; }

    internal static ModelRouteModelCandidate FromModel(
        ModelEndpointOptions model,
        int score)
    {
        return new ModelRouteModelCandidate
        {
            Name = model.Name,
            Provider = model.Provider,
            ApiProviderName = ReadMetadata(model, "apiProviderName"),
            ProviderModel = ReadMetadata(model, "model"),
            Category = ReadMetadata(model, "category"),
            Capabilities = ModelRouteResolver.ReadMetadataList(model, "capabilities"),
            Roles = ModelRouteResolver.ReadMetadataList(model, "roles"),
            TaskKinds = ModelRouteResolver.ReadMetadataList(model, "taskKinds"),
            ThinkingModes = ModelRouteResolver.ReadMetadataList(model, "thinkingModes"),
            Score = score
        };
    }

    private static string? ReadMetadata(
        ModelEndpointOptions model,
        string key)
    {
        return model.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}
