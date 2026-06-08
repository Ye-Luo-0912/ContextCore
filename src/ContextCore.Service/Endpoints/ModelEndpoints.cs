using ContextCore.Abstractions;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>模型网关观测端点，用于查看已启用模型的健康状态。</summary>
internal static class ModelEndpoints
{
	public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/model/status", async Task<IResult> (
			IModelHealthService? health,
			ModelGatewayOptions options,
			ApiKeyResolver apiKeyResolver,
			CancellationToken ct) =>
		{
			var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
			var configurationStatus = ModelGatewayConfigurationInspector.Inspect(effectiveOptions, apiKeyResolver);
			var configurationByName = configurationStatus
				.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
			var results = new List<ContextCoreModelHealthStatusResponse>();

			foreach (var model in effectiveOptions.Models)
			{
				configurationByName.TryGetValue(model.Name, out var modelConfiguration);
				if (health is null)
				{
					results.Add(new ContextCoreModelHealthStatusResponse
					{
						Name = model.Name,
						Provider = model.Provider,
						Enabled = model.Enabled,
						ApiProviderName = ReadMetadata(model, "apiProviderName"),
						ProviderModel = ReadMetadata(model, "model"),
						Category = ReadMetadata(model, "category"),
						Capabilities = ReadMetadataList(model, "capabilities"),
						Roles = ReadMetadataList(model, "roles"),
						TaskKinds = ReadMetadataList(model, "taskKinds"),
						ThinkingModes = ReadMetadataList(model, "thinkingModes"),
						EndpointConfigured = modelConfiguration?.EndpointConfigured ?? false,
						ApiKeyRequired = modelConfiguration?.ApiKeyRequired ?? false,
						ApiKeyConfigured = modelConfiguration?.ApiKeyConfigured ?? false,
						ApiKeySource = modelConfiguration?.ApiKeySource ?? string.Empty,
						ApiKeyEnvironmentVariable = modelConfiguration?.ApiKeyEnvironmentVariable,
						ConfigurationError = modelConfiguration?.ConfigurationError,
						Availability = TranslateAvailability(ModelAvailability.Unavailable),
						LastError = "模型健康检查服务未配置。"
					});
					continue;
				}

				var result = model.Enabled
					? await health.CheckAsync(model.Name, ct)
					: new ModelHealthResult
					{
						ModelName = model.Name,
						Availability = ModelAvailability.Unavailable,
						LastError = "模型已禁用。",
						CheckedAt = DateTimeOffset.UtcNow
					};
				results.Add(new ContextCoreModelHealthStatusResponse
				{
					Name = model.Name,
					Provider = model.Provider,
					Enabled = model.Enabled,
					ApiProviderName = ReadMetadata(model, "apiProviderName"),
					ProviderModel = ReadMetadata(model, "model"),
					Category = ReadMetadata(model, "category"),
					Capabilities = ReadMetadataList(model, "capabilities"),
					Roles = ReadMetadataList(model, "roles"),
					TaskKinds = ReadMetadataList(model, "taskKinds"),
					ThinkingModes = ReadMetadataList(model, "thinkingModes"),
					EndpointConfigured = modelConfiguration?.EndpointConfigured ?? false,
					ApiKeyRequired = modelConfiguration?.ApiKeyRequired ?? false,
					ApiKeyConfigured = modelConfiguration?.ApiKeyConfigured ?? false,
					ApiKeySource = modelConfiguration?.ApiKeySource ?? string.Empty,
					ApiKeyEnvironmentVariable = modelConfiguration?.ApiKeyEnvironmentVariable,
					ConfigurationError = modelConfiguration?.ConfigurationError,
					Availability = TranslateAvailability(result.Availability),
					LatencyMs = result.LatencyMs,
					LastError = result.LastError,
					CheckedAt = result.CheckedAt
				});
			}

			return Results.Ok(new ContextCoreModelStatusResponse
			{
				ApiProviders = effectiveOptions.ApiProviders.Select(provider =>
					DescribeApiProvider(provider, apiKeyResolver)).ToArray(),
				ModelProfiles = effectiveOptions.ModelProfiles.Select(DescribeModelProfile).ToArray(),
				Models = results,
				Routes = effectiveOptions.Routes.Select(route =>
					DescribeRoute(effectiveOptions, route)).ToArray()
			});
		})
		.WithTags("Model")
		.WithName("GetModelStatus")
		.WithSummary("获取模型健康状态");

		app.MapPost("/api/model/route/resolve", (ModelRouteResolveRequest request, ModelGatewayOptions options, HttpContext httpContext) =>
		{
			if (!TryParseModelRole(request.Role, out var role))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"model.route.resolve",
					$"未知模型角色 '{request.Role}'。",
					field: "role");
			}

			var modelRequest = CreateModelRequest(role, request);
			var resolution = ModelRouteResolver.Resolve(options, modelRequest);
			return Results.Ok(DescribeResolution(resolution));
		})
		.WithTags("Model")
		.WithName("ResolveModelRoute")
		.WithSummary("预览一次模型路由解析结果");

		return app;
	}

	private static ContextCoreModelApiProviderStatusResponse DescribeApiProvider(
		ModelApiProviderOptions provider,
		ApiKeyResolver apiKeyResolver)
	{
		var endpointLikeModel = new ModelEndpointOptions
		{
			Name = provider.Name,
			Provider = provider.Provider,
			Endpoint = provider.Endpoint,
			ApiKey = provider.ApiKey,
			Enabled = provider.Enabled,
			Metadata = provider.Metadata
		};
		var apiKey = apiKeyResolver.Resolve(endpointLikeModel);

		return new ContextCoreModelApiProviderStatusResponse
		{
			Name = provider.Name,
			Provider = provider.Provider,
			Enabled = provider.Enabled,
			EndpointConfigured = provider.Provider.Equals("mock", StringComparison.OrdinalIgnoreCase)
				|| !string.IsNullOrWhiteSpace(provider.Endpoint),
			TimeoutSeconds = provider.Timeout.TotalSeconds,
			ApiKeyRequired = apiKey.Required,
			ApiKeyConfigured = apiKey.Configured,
			ApiKeySource = apiKey.Source,
			ApiKeyEnvironmentVariable = apiKey.EnvironmentVariableName,
			ApiKeyError = apiKey.Error
		};
	}

	private static ContextCoreModelProfileStatusResponse DescribeModelProfile(ModelProfileOptions profile)
	{
		return new ContextCoreModelProfileStatusResponse
		{
			Name = profile.Name,
			ApiProviderName = profile.ApiProviderName,
			ProviderModel = profile.Model,
			Category = profile.Category,
			Capabilities = profile.Capabilities,
			Roles = profile.Roles,
			TaskKinds = profile.TaskKinds,
			ThinkingModes = profile.ThinkingModes,
			SupportsJsonResponseFormat = profile.SupportsJsonResponseFormat,
			TimeoutSeconds = profile.Timeout?.TotalSeconds,
			Enabled = profile.Enabled
		};
	}

	private static ContextCoreModelRouteStatusResponse DescribeRoute(
		ModelGatewayOptions options,
		ModelRoleRoute route)
	{
		var request = CreateRoutePreviewRequest(route);
		var primary = ModelRouteResolver.ResolveModel(
			options,
			route.PrimaryModelName,
			route.PrimaryModelCategory,
			route.RequiredCapabilities,
			request);
		var fallback = string.IsNullOrWhiteSpace(route.FallbackModelName)
			&& string.IsNullOrWhiteSpace(route.FallbackModelCategory)
				? null
				: ModelRouteResolver.ResolveModel(
					options,
					route.FallbackModelName,
					route.FallbackModelCategory,
					route.RequiredCapabilities,
					request);

		return new ContextCoreModelRouteStatusResponse
		{
			Role = route.Role.ToString(),
			TaskKind = route.TaskKind,
			ThinkingMode = route.ThinkingMode,
			Priority = route.Priority,
			PrimaryModelName = route.PrimaryModelName,
			PrimaryModelCategory = route.PrimaryModelCategory,
			RequiredCapabilities = route.RequiredCapabilities,
			FallbackModelName = route.FallbackModelName,
			FallbackModelCategory = route.FallbackModelCategory,
			MaxRetryCount = route.MaxRetryCount,
			EnableFallback = route.EnableFallback,
			FallbackOnTimeout = route.FallbackOnTimeout,
			FallbackOnRateLimit = route.FallbackOnRateLimit,
			FallbackOnServerError = route.FallbackOnServerError,
			FallbackOnInvalidJson = route.FallbackOnInvalidJson,
			HighRiskTask = route.HighRiskTask,
			Primary = DescribeSelection(primary),
			Fallback = fallback is null ? null : DescribeSelection(fallback)
		};
	}

	private static ContextCoreModelRouteResolveResponse DescribeResolution(ModelRouteResolution resolution)
	{
		return new ContextCoreModelRouteResolveResponse
		{
			Role = resolution.Role.ToString(),
			TaskKind = resolution.TaskKind,
			ThinkingMode = resolution.ThinkingMode,
			RouteSource = TranslateRouteSource(resolution.RouteSource),
			Route = resolution.Route is null
				? null
				: new ContextCoreModelRouteDescriptor
				{
					Role = resolution.Route.Role.ToString(),
					TaskKind = resolution.Route.TaskKind,
					ThinkingMode = resolution.Route.ThinkingMode,
					Priority = resolution.Route.Priority,
					PrimaryModelName = resolution.Route.PrimaryModelName,
					PrimaryModelCategory = resolution.Route.PrimaryModelCategory,
					RequiredCapabilities = resolution.Route.RequiredCapabilities,
					FallbackModelName = resolution.Route.FallbackModelName,
					FallbackModelCategory = resolution.Route.FallbackModelCategory,
					MaxRetryCount = resolution.Route.MaxRetryCount,
					EnableFallback = resolution.Route.EnableFallback,
					HighRiskTask = resolution.Route.HighRiskTask
				},
			Primary = DescribeSelection(resolution.Primary),
			Fallback = resolution.Fallback is null ? null : DescribeSelection(resolution.Fallback)
		};
	}

	private static ContextCoreModelSelectionResponse DescribeSelection(ModelRouteModelSelection selection)
	{
		return new ContextCoreModelSelectionResponse
		{
			RequestedModelName = selection.RequestedModelName,
			RequestedCategory = selection.RequestedCategory,
			RequiredCapabilities = selection.RequiredCapabilities,
			ModelName = selection.ModelName,
			Provider = selection.Provider,
			ApiProviderName = selection.ApiProviderName,
			ProviderModel = selection.ProviderModel,
			Category = selection.Category,
			Capabilities = selection.Capabilities,
			Roles = selection.Roles,
			TaskKinds = selection.TaskKinds,
			ThinkingModes = selection.ThinkingModes,
			Found = selection.Found,
			Enabled = selection.Enabled,
			Score = selection.Score ?? 0d,
			Reason = selection.Reason,
			Candidates = selection.Candidates.Select(candidate => new ContextCoreModelSelectionCandidateResponse
			{
				Name = candidate.Name,
				Provider = candidate.Provider,
				ApiProviderName = candidate.ApiProviderName,
				ProviderModel = candidate.ProviderModel,
				Category = candidate.Category,
				Capabilities = candidate.Capabilities,
				Roles = candidate.Roles,
				TaskKinds = candidate.TaskKinds,
				ThinkingModes = candidate.ThinkingModes,
				Score = candidate.Score
			}).ToArray()
		};
	}

	private static ModelRequest CreateRoutePreviewRequest(ModelRoleRoute route)
	{
		var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		AddMetadata(metadata, "taskKind", route.TaskKind);
		AddMetadata(metadata, "compressionTask", route.TaskKind);
		AddMetadata(metadata, "thinkingMode", route.ThinkingMode);

		return new ModelRequest
		{
			Role = route.Role,
			Prompt = string.Empty,
			Metadata = metadata
		};
	}

	private static ModelRequest CreateModelRequest(
		ModelRole role,
		ModelRouteResolveRequest request)
	{
		var metadata = request.Metadata is null
			? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase);
		AddMetadata(metadata, "taskKind", request.TaskKind);
		AddMetadata(metadata, "compressionTask", request.TaskKind);
		AddMetadata(metadata, "thinkingMode", request.ThinkingMode);
		if (request.RequiredCapabilities is { Count: > 0 })
		{
			metadata["requiredCapabilities"] = string.Join(",", request.RequiredCapabilities);
		}

		return new ModelRequest
		{
			Role = role,
			Prompt = request.Prompt ?? string.Empty,
			ResponseFormat = request.ResponseFormat,
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

	private static bool TryParseModelRole(
		string? roleValue,
		out ModelRole role)
	{
		if (string.IsNullOrWhiteSpace(roleValue))
		{
			role = ModelRole.Fallback;
			return true;
		}

		return Enum.TryParse(roleValue, ignoreCase: true, out role);
	}

	private static string? ReadMetadata(
		ModelEndpointOptions model,
		string key)
	{
		return model.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
			? value
			: null;
	}

	private static IReadOnlyList<string> ReadMetadataList(
		ModelEndpointOptions model,
		string key)
	{
		return model.Metadata.TryGetValue(key, out var value)
			? value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: Array.Empty<string>();
	}

	private static string TranslateAvailability(ModelAvailability availability)
	{
		return availability switch
		{
			ModelAvailability.Available => "可用",
			ModelAvailability.Unavailable => "不可用",
			_ => "未知"
		};
	}

	private static string TranslateRouteSource(ModelRouteSource source)
	{
		return source switch
		{
			ModelRouteSource.ExactRole => "角色精确匹配",
			ModelRouteSource.FallbackRole => "兜底角色匹配",
			ModelRouteSource.FirstEnabledModel => "首个启用模型",
			_ => "未命中"
		};
	}
}

public sealed class ModelRouteResolveRequest
{
	public string? Role { get; init; }

	public string? TaskKind { get; init; }

	public string? ThinkingMode { get; init; }

	public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

	public string? Prompt { get; init; }

	public string? ResponseFormat { get; init; }

	public Dictionary<string, string>? Metadata { get; init; }
}
