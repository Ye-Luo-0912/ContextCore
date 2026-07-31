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
    private readonly ModelGatewayResilienceOptions _resilience;

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
        _resilience = options.Resilience ?? new ModelGatewayResilienceOptions();
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

        var fallback = await ExecuteWithRetryAsync(
            fallbackModelName!,
            request,
            route.MaxRetryCount,
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

    /// <inheritdoc />
    public async Task<ModelChatResponse> ChatWithToolsAsync(
        ModelChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 路由解析：将 ModelChatRequest 转换为 ModelRequest 用于路由匹配
        var routeRequest = new ModelRequest
        {
            OperationId = request.OperationId,
            ModelArtifactId = request.ModelArtifactId,
            Role = request.Role,
            Prompt = string.Empty,
            ResponseFormat = request.ResponseFormat,
            Metadata = request.Metadata
        };
        var resolution = ModelRouteResolver.Resolve(_options, routeRequest);
        var route = resolution.Route;

        if (route is null || string.IsNullOrWhiteSpace(resolution.Primary.ModelName))
        {
            // 无可用路由 → 降级到 fallback helper（走 CompleteAsync 可能也无路由，但保持一致语义）
            return await ChatWithToolsFallbackHelper.ExecuteViaCompleteAsync(this, request, cancellationToken).ConfigureAwait(false);
        }

        var primaryModelName = resolution.Primary.ModelName;
        var maxRetry = route.MaxRetryCount;

        // 尝试原生 function calling（如果适配器支持 IChatCompletionAdapter）
        var nativeResult = await TryNativeChatWithToolsAsync(
            primaryModelName, request, maxRetry, fallbackUsed: false, fallbackReason: null, cancellationToken).ConfigureAwait(false);

        if (nativeResult is not null && nativeResult.Succeeded)
        {
            return nativeResult;
        }

        // 主模型原生调用失败 → 尝试备用模型（如果配置了且支持原生）
        if (route.EnableFallback
            && resolution.Fallback is { ModelName: not null } fallback
            && !string.IsNullOrWhiteSpace(fallback.ModelName))
        {
            var fallbackResult = await TryNativeChatWithToolsAsync(
                fallback.ModelName, request, maxRetry, fallbackUsed: true,
                fallbackReason: nativeResult?.ErrorMessage ?? "primary native call failed",
                cancellationToken).ConfigureAwait(false);

            if (fallbackResult is not null && fallbackResult.Succeeded)
            {
                return fallbackResult;
            }
        }

        // 原生路径不可用或全部失败 → 降级到 JSON prompt fallback
        return await ChatWithToolsFallbackHelper.ExecuteViaCompleteAsync(this, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 尝试通过 IChatCompletionAdapter 原生 function calling 调用。
    /// 如果适配器不支持原生 function calling，返回 null（调用方应降级）。
    /// </summary>
    private async Task<ModelChatResponse?> TryNativeChatWithToolsAsync(
        string modelName,
        ModelChatRequest request,
        int maxRetryCount,
        bool fallbackUsed,
        string? fallbackReason,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(modelName, out var adapter))
        {
            return null;
        }

        if (adapter is not IChatCompletionAdapter chatAdapter)
        {
            return null;
        }

        var attempts = Math.Max(1, maxRetryCount + 1);
        ModelChatResponse? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                last = await chatAdapter.ChatWithToolsAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = new ModelChatResponse
                {
                    OperationId = request.OperationId,
                    Succeeded = false,
                    ErrorMessage = ex.Message,
                    FinishReason = ModelChatFinishReason.Error,
                    ModelId = modelName
                };
            }

            if (last.Succeeded)
            {
                return last;
            }

            // 仅对瞬态故障重试（通过 metadata 中的 failureReason 判断）
            var failureReason = last.Metadata.TryGetValue("failureReason", out var fr) ? fr : "unknown";
            if (!IsTransientChatFailure(failureReason))
            {
                return last;
            }

            if (attempt < attempts)
            {
                await ApplyChatRetryDelayAsync(last, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return last;
    }

    /// <summary>判断 chat 路径的失败是否为瞬态故障。</summary>
    private static bool IsTransientChatFailure(string failureReason)
    {
        return failureReason is "timeout" or "rate_limit" or "server_error" or "unavailable";
    }

    /// <summary>应用 chat 路径的重试延迟。</summary>
    private async Task ApplyChatRetryDelayAsync(ModelChatResponse last, int attempt, CancellationToken cancellationToken)
    {
        if (last.Metadata.TryGetValue("retryAfterMs", out var retryAfterText)
            && int.TryParse(retryAfterText, out var retryAfterMs)
            && retryAfterMs > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(retryAfterMs), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var baseMs = _resilience.RetryBaseDelay.TotalMilliseconds;
        var maxMs = _resilience.RetryMaxDelay.TotalMilliseconds;
        if (baseMs <= 0)
        {
            return;
        }

        var exponentialMs = baseMs * Math.Pow(2, attempt - 1);
        var cappedMs = Math.Min(exponentialMs, maxMs);
        var jitter = cappedMs * (0.75 + Random.Shared.NextDouble() * 0.5);
        var delayMs = Math.Min(jitter, maxMs);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期行为
        }
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

            // 原因感知重试：仅对瞬态故障重试，确定性故障（InvalidJson/EmptyResponse）直接返回。
            if (!IsTransientFailure(last.FailureReason))
            {
                return last;
            }

            // 最后一次尝试后不再等待。
            if (attempt < attempts)
            {
                await ApplyRetryDelayAsync(last, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return last!;
    }

    /// <summary>判断失败原因是否为瞬态故障（值得重试）。</summary>
    private static bool IsTransientFailure(ModelFailureReason reason)
    {
        return reason is ModelFailureReason.Timeout
            or ModelFailureReason.RateLimit
            or ModelFailureReason.ServerError
            or ModelFailureReason.Unavailable;
    }

    /// <summary>应用重试延迟：优先使用 Retry-After，否则指数退避 + 抖动。</summary>
    private async Task ApplyRetryDelayAsync(ModelAttemptResult last, int attempt, CancellationToken cancellationToken)
    {
        // 如果适配器返回了 retryAfterMs 元数据（来自 429 响应头），优先使用。
        if (last.Response.Metadata.TryGetValue("retryAfterMs", out var retryAfterText)
            && int.TryParse(retryAfterText, out var retryAfterMs)
            && retryAfterMs > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(retryAfterMs), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // 指数退避：baseDelay * 2^(attempt-1)，加 ±25% 抖动。
        var baseMs = _resilience.RetryBaseDelay.TotalMilliseconds;
        var maxMs = _resilience.RetryMaxDelay.TotalMilliseconds;
        if (baseMs <= 0)
        {
            return;
        }

        var exponentialMs = baseMs * Math.Pow(2, attempt - 1);
        var cappedMs = Math.Min(exponentialMs, maxMs);
        var jitter = cappedMs * (0.75 + Random.Shared.NextDouble() * 0.5);
        var delayMs = Math.Min(jitter, maxMs);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时静默返回，让外层循环处理取消。
        }
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
            // 网关层通过 linked token + CancelAfter 传递超时取消信号（供非 HTTP 适配器使用）。
            // HTTP 适配器（HttpChatCompletionAdapterBase）内部也创建自己的 linked token，
            // 两者使用相同的 modelOptions.Timeout 值，不构成双重超时——只是冗余的取消信号源。
            // 移除了之前的 WaitAsync 调用，避免 WaitAsync 掩盖 adapter 层的取消原因。
            using var timeoutSource = modelOptions.Timeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (timeoutSource is not null)
            {
                timeoutSource.CancelAfter(modelOptions.Timeout);
            }

            var effectiveToken = timeoutSource?.Token ?? cancellationToken;
            response = await adapter.CompleteAsync(
                CreateAdapterRequest(request, operationId, modelName, attempt, fallbackUsed, fallbackReason),
                effectiveToken).ConfigureAwait(false);
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

        if (error.Contains("server", StringComparison.OrdinalIgnoreCase))
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
