using System.Diagnostics;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ModelGateway.Infrastructure;

namespace ContextCore.ModelGateway;

/// <summary>
/// 支持多端点、超时重试、使用日志和用量计费的可配置模型网关实现。
/// </summary>
public sealed class ConfigurableModelGateway : IModelGateway
{
    private readonly IReadOnlyDictionary<string, IModelAdapter> _adapters;
    private readonly ModelGatewayOptions _options;
    private readonly IReadOnlyDictionary<string, ModelEndpointOptions> _modelOptions;
    private readonly IModelUsageLogStore _usageLogStore;

    public ConfigurableModelGateway(ModelGatewayOptions options)
        : this(options, ModelAdapterFactory.CreateAdapters(options), new InMemoryModelUsageLogStore())
    {
    }

    public ConfigurableModelGateway(
        ModelGatewayOptions options,
        IEnumerable<IModelAdapter> adapters,
        IModelUsageLogStore? usageLogStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        _options = ModelGatewayOptionsMaterializer.Materialize(options);
        _modelOptions = _options.Models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
        _adapters = adapters.ToDictionary(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase);
        _usageLogStore = usageLogStore ?? new InMemoryModelUsageLogStore();
    }

    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = ContextCoreDiagnostics.StartOperation("model.complete", request.OperationId);
        activity?.SetTag("contextcore.model.role", request.Role.ToString());
        SetTagIfPresent(activity, "contextcore.model.task_kind", ReadRequestMetadata(request, "taskKind"));
        SetTagIfPresent(activity, "contextcore.model.thinking_mode", ReadRequestMetadata(request, "thinkingMode"));
        SetTagIfPresent(activity, "contextcore.model.response_format", request.ResponseFormat);

        var resolution = ModelRouteResolver.Resolve(_options, request);
        activity?.SetTag("contextcore.model.route_source", resolution.RouteSource.ToString());
        SetTagIfPresent(activity, "contextcore.model.selected_task_kind", resolution.TaskKind);
        SetTagIfPresent(activity, "contextcore.model.selected_thinking_mode", resolution.ThinkingMode);

        var route = resolution.Route;
        if (route is null)
        {
            const string errorMessage = "未配置可用的模型路由。";
            ContextCoreDiagnostics.SetStatus(activity, succeeded: false, errorMessage);
            return CreateFailure(
                request,
                errorMessage,
                "unavailable",
                requiresReview: false,
                fallbackUsed: false);
        }

        var primaryModelName = resolution.Primary.ModelName;
        if (string.IsNullOrWhiteSpace(primaryModelName))
        {
            const string errorMessage = "没有模型满足当前路由约束。";
            ContextCoreDiagnostics.SetStatus(activity, succeeded: false, errorMessage);
            return CreateFailure(
                request,
                errorMessage,
                "unavailable",
                requiresReview: false,
                fallbackUsed: false);
        }

        activity?.SetTag("contextcore.model.primary", primaryModelName);
        activity?.SetTag("contextcore.model.high_risk_task", route.HighRiskTask);
        activity?.SetTag("contextcore.model.max_retry_count", route.MaxRetryCount);
        SetTagIfPresent(activity, "contextcore.model.primary_provider", resolution.Primary.Provider);
        SetTagIfPresent(activity, "contextcore.model.primary_api_provider", resolution.Primary.ApiProviderName);

        var primary = await ExecuteWithRetryAsync(
            primaryModelName,
            request,
            route.MaxRetryCount,
            fallbackUsed: false,
            fallbackReason: null,
            cancellationToken).ConfigureAwait(false);

        if (primary.Response.Succeeded)
        {
            ContextCoreDiagnostics.SetStatus(activity, succeeded: true);
            return primary.Response;
        }

        var fallbackModelName = resolution.Fallback?.ModelName;
        SetTagIfPresent(activity, "contextcore.model.fallback", fallbackModelName);
        SetTagIfPresent(activity, "contextcore.model.fallback_provider", resolution.Fallback?.Provider);
        SetTagIfPresent(activity, "contextcore.model.fallback_api_provider", resolution.Fallback?.ApiProviderName);

        // 高风险任务失败时不自动降级到备用模型，避免把可能需要人工复核的结果静默交给弱模型。
        if (route.HighRiskTask)
        {
            ContextCoreDiagnostics.SetStatus(
                activity,
                succeeded: false,
                primary.Response.ErrorMessage ?? "高风险任务主模型失败。不能自动回退。");
            return WithMetadata(primary.Response, new Dictionary<string, string>
            {
                ["requiresReview"] = "true",
                ["fallbackBlocked"] = "highRiskTask"
            });
        }

        if (!ShouldFallback(route, fallbackModelName, primary.FailureReason))
        {
            ContextCoreDiagnostics.SetStatus(activity, succeeded: false, primary.Response.ErrorMessage);
            return primary.Response;
        }

        var fallback = await ExecuteAttemptAsync(
            fallbackModelName!,
            request,
            attempt: 1,
            fallbackUsed: true,
            fallbackReason: primary.FailureReason.ToMetadataValue(),
            cancellationToken).ConfigureAwait(false);

        var finalResponse = fallback.Response.Succeeded
            ? WithMetadata(fallback.Response, new Dictionary<string, string>
            {
                ["primaryModelName"] = primaryModelName,
                ["fallbackUsed"] = "true",
                ["fallbackReason"] = primary.FailureReason.ToMetadataValue()
            })
            : WithMetadata(fallback.Response, new Dictionary<string, string>
            {
                ["primaryModelName"] = primaryModelName,
                ["fallbackUsed"] = "true",
                ["fallbackReason"] = primary.FailureReason.ToMetadataValue(),
                ["primaryError"] = primary.Response.ErrorMessage ?? string.Empty
            });
        activity?.SetTag("contextcore.model.fallback_result", fallback.Response.Succeeded ? "succeeded" : "failed");
        ContextCoreDiagnostics.SetStatus(activity, finalResponse.Succeeded, finalResponse.ErrorMessage);
        return finalResponse;
    }

    private async Task<ModelAttemptResult> ExecuteWithRetryAsync(
        string modelName,
        ModelRequest request,
        int maxRetryCount,
        bool fallbackUsed,
        string? fallbackReason,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, maxRetryCount + 1);
        ModelAttemptResult? last = null;

        // MaxRetryCount 表示失败后的额外尝试次数，因此总尝试次数至少为 1。
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            last = await ExecuteAttemptAsync(
                modelName,
                request,
                attempt,
                fallbackUsed,
                fallbackReason,
                cancellationToken).ConfigureAwait(false);

            if (last.Response.Succeeded)
            {
                return last;
            }
        }

        return last!;
    }

    private async Task<ModelAttemptResult> ExecuteAttemptAsync(
        string modelName,
        ModelRequest request,
        int attempt,
        bool fallbackUsed,
        string? fallbackReason,
        CancellationToken cancellationToken)
    {
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;
        using var activity = ContextCoreDiagnostics.StartOperation("model.complete.attempt", operationId);
        activity?.SetTag("contextcore.model.name", modelName);
        activity?.SetTag("contextcore.model.role", request.Role.ToString());
        activity?.SetTag("contextcore.model.attempt", attempt);
        activity?.SetTag("contextcore.model.fallback_used", fallbackUsed);
        SetTagIfPresent(activity, "contextcore.model.fallback_reason", fallbackReason);

        if (!_modelOptions.TryGetValue(modelName, out var modelOptions))
        {
            var missingModelResponse = CreateFailure(
                request,
                $"模型 '{modelName}' 未配置。",
                "unavailable",
                requiresReview: false,
                fallbackUsed);
            SetAttemptResultTags(activity, missingModelResponse, ModelFailureReason.Unavailable, latencyMs: 0);
            return new ModelAttemptResult(missingModelResponse, ModelFailureReason.Unavailable);
        }

        SetModelOptionTags(activity, modelOptions);

        if (!modelOptions.Enabled)
        {
            var disabledModelResponse = CreateFailure(
                request,
                $"模型 '{modelName}' 已禁用。",
                "unavailable",
                requiresReview: false,
                fallbackUsed);
            SetAttemptResultTags(activity, disabledModelResponse, ModelFailureReason.Unavailable, latencyMs: 0);
            return new ModelAttemptResult(disabledModelResponse, ModelFailureReason.Unavailable);
        }

        if (!_adapters.TryGetValue(modelName, out var adapter))
        {
            var missingAdapterResponse = CreateFailure(
                request,
                $"模型适配器 '{modelName}' 不可用。",
                "unavailable",
                requiresReview: false,
                fallbackUsed);
            SetAttemptResultTags(activity, missingAdapterResponse, ModelFailureReason.Unavailable, latencyMs: 0);
            return new ModelAttemptResult(missingAdapterResponse, ModelFailureReason.Unavailable);
        }
        activity?.SetTag("contextcore.model.adapter", adapter.Name);

        var stopwatch = Stopwatch.StartNew();
        ModelResponse response;
        try
        {
            using var timeoutSource = modelOptions.Timeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (timeoutSource is not null)
            {
                timeoutSource.CancelAfter(modelOptions.Timeout);
            }

            var effectiveToken = timeoutSource?.Token ?? cancellationToken;
            var adapterTask = adapter.CompleteAsync(
                CreateAdapterRequest(request, operationId, modelName, attempt, fallbackUsed, fallbackReason),
                effectiveToken);

            response = modelOptions.Timeout > TimeSpan.Zero
                ? await adapterTask.WaitAsync(modelOptions.Timeout, cancellationToken).ConfigureAwait(false)
                : await adapterTask.ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            response = CreateFailure(request, $"模型请求已超时：{ex.Message}", "timeout", requiresReview: false, fallbackUsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            response = CreateFailure(request, "模型请求已超时。", "timeout", requiresReview: false, fallbackUsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            response = CreateFailure(request, ex.Message, "unavailable", requiresReview: false, fallbackUsed);
        }

        stopwatch.Stop();

        var validation = ValidateResponse(request, response);
        var failureReason = validation.FailureReason;
        if (!validation.Succeeded)
        {
            response = WithMetadata(new ModelResponse
            {
                OperationId = response.OperationId,
                Content = response.Content,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                Succeeded = false,
                ErrorMessage = validation.ErrorMessage ?? response.ErrorMessage,
                Metadata = response.Metadata
            }, new Dictionary<string, string>
            {
                ["failureReason"] = failureReason.ToMetadataValue()
            });
        }

        response = WithMetadata(response, new Dictionary<string, string>
        {
            ["modelName"] = modelName,
            ["provider"] = modelOptions.Provider,
            ["attempt"] = attempt.ToString(),
            ["fallbackUsed"] = fallbackUsed ? "true" : "false",
            ["latencyMs"] = stopwatch.ElapsedMilliseconds.ToString()
        });

        SetAttemptResultTags(activity, response, failureReason, stopwatch.ElapsedMilliseconds);

        await _usageLogStore.SaveAsync(new ModelUsageLog
        {
            OperationId = response.OperationId,
            Role = request.Role,
            ModelName = modelName,
            Provider = modelOptions.Provider,
            Succeeded = response.Succeeded,
            FallbackUsed = fallbackUsed,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            ErrorMessage = response.ErrorMessage,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        return new ModelAttemptResult(response, failureReason);
    }

    private static void SetModelOptionTags(Activity? activity, ModelEndpointOptions modelOptions)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("contextcore.model.provider", modelOptions.Provider);
        SetTagIfPresent(activity, "contextcore.model.api_provider", ReadModelMetadata(modelOptions, "apiProviderName"));
        SetTagIfPresent(activity, "contextcore.model.provider_model", ReadModelMetadata(modelOptions, "model"));
        SetTagIfPresent(activity, "contextcore.model.category", ReadModelMetadata(modelOptions, "category"));
        SetTagIfPresent(activity, "contextcore.model.capabilities", ReadModelMetadata(modelOptions, "capabilities"));
    }

    private static void SetAttemptResultTags(
        Activity? activity,
        ModelResponse response,
        ModelFailureReason failureReason,
        long latencyMs)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("contextcore.model.latency_ms", latencyMs);
        activity.SetTag("contextcore.model.succeeded", response.Succeeded);
        activity.SetTag("contextcore.model.input_tokens", response.InputTokens);
        activity.SetTag("contextcore.model.output_tokens", response.OutputTokens);
        activity.SetTag("contextcore.model.failure_reason", failureReason.ToMetadataValue());
        ContextCoreDiagnostics.SetStatus(activity, response.Succeeded, response.ErrorMessage);
    }

    private static string? ReadRequestMetadata(ModelRequest request, string key)
    {
        return request.Metadata.TryGetValue(key, out var value) ? value : null;
    }

    private static string? ReadModelMetadata(ModelEndpointOptions modelOptions, string key)
    {
        return modelOptions.Metadata.TryGetValue(key, out var value) ? value : null;
    }

    private static void SetTagIfPresent(Activity? activity, string key, string? value)
    {
        if (activity is not null && !string.IsNullOrWhiteSpace(value))
        {
            activity.SetTag(key, value);
        }
    }

    private static bool ShouldFallback(
        ModelRoleRoute route,
        string? fallbackModelName,
        ModelFailureReason reason)
    {
        if (!route.EnableFallback || string.IsNullOrWhiteSpace(fallbackModelName))
        {
            return false;
        }

        return reason switch
        {
            ModelFailureReason.Unavailable => true,
            ModelFailureReason.EmptyResponse => true,
            ModelFailureReason.Timeout => route.FallbackOnTimeout,
            ModelFailureReason.RateLimit => route.FallbackOnRateLimit,
            ModelFailureReason.ServerError => route.FallbackOnServerError,
            ModelFailureReason.InvalidJson => route.FallbackOnInvalidJson,
            _ => false
        };
    }

    private static ModelRequest CreateAdapterRequest(
        ModelRequest request,
        string operationId,
        string modelName,
        int attempt,
        bool fallbackUsed,
        string? fallbackReason)
    {
        var metadata = new Dictionary<string, string>(request.Metadata)
        {
            ["modelName"] = modelName,
            ["attempt"] = attempt.ToString(),
            ["fallbackUsed"] = fallbackUsed ? "true" : "false"
        };

        if (!string.IsNullOrWhiteSpace(fallbackReason))
        {
            metadata["fallbackReason"] = fallbackReason!;
        }

        return new ModelRequest
        {
            OperationId = operationId,
            Role = request.Role,
            Prompt = request.Prompt,
            SystemPrompt = request.SystemPrompt,
            ResponseFormat = request.ResponseFormat,
            Metadata = metadata
        };
    }

    private static ModelValidationResult ValidateResponse(ModelRequest request, ModelResponse response)
    {
        if (!response.Succeeded)
        {
            return new ModelValidationResult(false, ClassifyFailure(response), response.ErrorMessage);
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return new ModelValidationResult(false, ModelFailureReason.EmptyResponse, "模型返回了空内容。");
        }

        if (RequestExpectsJson(request.ResponseFormat))
        {
            try
            {
                // 结构化输出在网关层先做一次轻量解析，避免下游拿到不可解析 JSON。
                using var _ = JsonDocument.Parse(response.Content);
            }
            catch (JsonException ex)
            {
                return new ModelValidationResult(false, ModelFailureReason.InvalidJson, $"结构化输出 JSON 解析失败：{ex.Message}");
            }
        }

        return new ModelValidationResult(true, ModelFailureReason.None, null);
    }

    private static ModelFailureReason ClassifyFailure(ModelResponse response)
    {
        if (response.Metadata.TryGetValue("failureReason", out var metadataReason))
        {
            return metadataReason.ToFailureReason();
        }

        if (response.Metadata.TryGetValue("httpStatusCode", out var statusCodeText)
            && int.TryParse(statusCodeText, out var statusCode))
        {
            if (statusCode == 429)
            {
                return ModelFailureReason.RateLimit;
            }

            if (statusCode >= 500)
            {
                return ModelFailureReason.ServerError;
            }
        }

        var error = response.ErrorMessage ?? string.Empty;
        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ModelFailureReason.Timeout;
        }

        if (error.Contains("429", StringComparison.OrdinalIgnoreCase)
            || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return ModelFailureReason.RateLimit;
        }

        if (error.Contains("server", StringComparison.OrdinalIgnoreCase)
            || error.Contains("5", StringComparison.OrdinalIgnoreCase))
        {
            return ModelFailureReason.ServerError;
        }

        if (error.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return ModelFailureReason.InvalidJson;
        }

        return ModelFailureReason.Unavailable;
    }

    private static bool RequestExpectsJson(string? responseFormat)
    {
        return !string.IsNullOrWhiteSpace(responseFormat)
            && responseFormat.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static ModelResponse CreateFailure(
        ModelRequest request,
        string errorMessage,
        string failureReason,
        bool requiresReview,
        bool fallbackUsed)
    {
        var metadata = new Dictionary<string, string>
        {
            ["failureReason"] = failureReason,
            ["fallbackUsed"] = fallbackUsed ? "true" : "false"
        };

        if (requiresReview)
        {
            metadata["requiresReview"] = "true";
        }

        return new ModelResponse
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            Content = string.Empty,
            Succeeded = false,
            ErrorMessage = errorMessage,
            Metadata = metadata
        };
    }

    private static ModelResponse WithMetadata(ModelResponse response, Dictionary<string, string> metadata)
    {
        var merged = new Dictionary<string, string>(response.Metadata);
        foreach (var (key, value) in metadata)
        {
            merged[key] = value;
        }

        return new ModelResponse
        {
            OperationId = response.OperationId,
            Content = response.Content,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            Succeeded = response.Succeeded,
            ErrorMessage = response.ErrorMessage,
            Metadata = merged
        };
    }

    private sealed record ModelAttemptResult(ModelResponse Response, ModelFailureReason FailureReason);

    private sealed record ModelValidationResult(bool Succeeded, ModelFailureReason FailureReason, string? ErrorMessage);
}

/// <summary>网关内部使用的失败分类，用于决定是否重试或触发回退模型。</summary>
internal enum ModelFailureReason
{
    None,
    Unavailable,
    Timeout,
    RateLimit,
    ServerError,
    InvalidJson,
    EmptyResponse
}

/// <summary>模型失败分类与日志元数据字符串之间的转换工具。</summary>
internal static class ModelFailureReasonExtensions
{
    public static string ToMetadataValue(this ModelFailureReason reason)
    {
        return reason switch
        {
            ModelFailureReason.None => "none",
            ModelFailureReason.Unavailable => "unavailable",
            ModelFailureReason.Timeout => "timeout",
            ModelFailureReason.RateLimit => "rate_limit",
            ModelFailureReason.ServerError => "server_error",
            ModelFailureReason.InvalidJson => "invalid_json",
            ModelFailureReason.EmptyResponse => "empty_response",
            _ => "unavailable"
        };
    }

    public static ModelFailureReason ToFailureReason(this string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => ModelFailureReason.None,
            "unavailable" => ModelFailureReason.Unavailable,
            "timeout" => ModelFailureReason.Timeout,
            "rate_limit" => ModelFailureReason.RateLimit,
            "server_error" => ModelFailureReason.ServerError,
            "invalid_json" => ModelFailureReason.InvalidJson,
            "empty_response" => ModelFailureReason.EmptyResponse,
            _ => ModelFailureReason.Unavailable
        };
    }
}
