using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ModelGateway.Infrastructure;

namespace ContextCore.ModelGateway;

/// <summary>通过发送探针请求检测模型端点健康状态的服务，支持 TTL 缓存避免频繁探活。</summary>
public sealed class ModelHealthService : IModelHealthService
{
    private readonly IReadOnlyDictionary<string, IModelAdapter> _adapters;
    private readonly ApiKeyResolver _apiKeyResolver;
    private readonly IReadOnlyDictionary<string, ModelEndpointOptions> _models;
    private readonly ModelGatewayResilienceOptions _resilience;
    private readonly Dictionary<string, ModelHealthResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _cacheGate = new();

    public ModelHealthService(ModelGatewayOptions options, IEnumerable<IModelAdapter> adapters)
        : this(options, adapters, new ApiKeyResolver())
    {
    }

    public ModelHealthService(
        ModelGatewayOptions options,
        IEnumerable<IModelAdapter> adapters,
        ApiKeyResolver apiKeyResolver)
        : this(options, adapters, apiKeyResolver, options.Resilience ?? new ModelGatewayResilienceOptions())
    {
    }

    public ModelHealthService(
        ModelGatewayOptions options,
        IEnumerable<IModelAdapter> adapters,
        ApiKeyResolver apiKeyResolver,
        ModelGatewayResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        ArgumentNullException.ThrowIfNull(resilience);

        _apiKeyResolver = apiKeyResolver;
        _resilience = resilience;
        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        _models = effectiveOptions.Models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
        _adapters = adapters.ToDictionary(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ModelHealthResult> CheckAsync(
        string modelName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Unavailable(modelName, 0, "模型名称不能为空。");
        }

        // TTL 缓存：在缓存有效期内直接返回上次结果，避免频繁探活消耗 API 调用。
        if (_resilience.HealthCheckCacheTtl > TimeSpan.Zero)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(modelName, out var cached)
                    && DateTimeOffset.UtcNow - cached.CheckedAt < _resilience.HealthCheckCacheTtl)
                {
                    return cached;
                }
            }
        }

        if (!_models.TryGetValue(modelName, out var options))
        {
            return CacheAndReturn(modelName, Unavailable(modelName, 0, "模型未配置。"));
        }

        if (!options.Enabled)
        {
            return CacheAndReturn(modelName, Unavailable(modelName, 0, "模型已禁用。"));
        }

        var apiKey = _apiKeyResolver.Resolve(options);
        if (apiKey.Required && !apiKey.Configured)
        {
            var source = string.IsNullOrWhiteSpace(apiKey.EnvironmentVariableName)
                ? "API 密钥"
                : $"环境变量 '{apiKey.EnvironmentVariableName}'";
            return CacheAndReturn(modelName, Unavailable(modelName, 0, $"{source} 未配置。"));
        }

        if (!_adapters.TryGetValue(modelName, out var adapter))
        {
            return CacheAndReturn(modelName, Unavailable(modelName, 0, "模型适配器不可用。"));
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // 健康检查使用独立的超时，避免高超时模型（如 90s）导致状态查询挂起。
            using var timeoutSource = _resilience.HealthCheckTimeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (timeoutSource is not null)
            {
                timeoutSource.CancelAfter(_resilience.HealthCheckTimeout);
            }

            var effectiveToken = timeoutSource?.Token ?? cancellationToken;
            var response = await adapter.CompleteAsync(new ModelRequest
            {
                OperationId = $"health-{Guid.NewGuid():N}",
                Role = ModelRole.Fallback,
                Prompt = "请返回 pong。",
                Metadata = new Dictionary<string, string>
                {
                    ["healthCheck"] = "true"
                }
            }, effectiveToken).ConfigureAwait(false);

            stopwatch.Stop();

            var result = response.Succeeded && !string.IsNullOrWhiteSpace(response.Content)
                ? new ModelHealthResult
                {
                    ModelName = modelName,
                    Availability = ModelAvailability.Available,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    CheckedAt = DateTimeOffset.UtcNow
                }
                : Unavailable(modelName, stopwatch.ElapsedMilliseconds, response.ErrorMessage ?? "模型返回了失败响应。");

            return CacheAndReturn(modelName, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return CacheAndReturn(modelName, Unavailable(modelName, stopwatch.ElapsedMilliseconds, ex.Message));
        }
    }

    /// <summary>清除所有缓存的健康检查结果，强制下次检查发起真实请求。</summary>
    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
        }
    }

    private ModelHealthResult CacheAndReturn(string modelName, ModelHealthResult result)
    {
        if (_resilience.HealthCheckCacheTtl > TimeSpan.Zero && !string.IsNullOrWhiteSpace(modelName))
        {
            lock (_cacheGate)
            {
                _cache[modelName] = result;
            }
        }

        return result;
    }

    private static ModelHealthResult Unavailable(string modelName, long latencyMs, string lastError)
    {
        return new ModelHealthResult
        {
            ModelName = modelName,
            Availability = ModelAvailability.Unavailable,
            LatencyMs = latencyMs,
            LastError = lastError,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }
}
