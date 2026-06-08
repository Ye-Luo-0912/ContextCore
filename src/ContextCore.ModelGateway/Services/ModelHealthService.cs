using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.ModelGateway.Infrastructure;

namespace ContextCore.ModelGateway;

/// <summary>通过发送探针请求检测模型端点健康状态的服务。</summary>
public sealed class ModelHealthService : IModelHealthService
{
    private readonly IReadOnlyDictionary<string, IModelAdapter> _adapters;
    private readonly ApiKeyResolver _apiKeyResolver;
    private readonly IReadOnlyDictionary<string, ModelEndpointOptions> _models;

    public ModelHealthService(ModelGatewayOptions options, IEnumerable<IModelAdapter> adapters)
        : this(options, adapters, new ApiKeyResolver())
    {
    }

    public ModelHealthService(
        ModelGatewayOptions options,
        IEnumerable<IModelAdapter> adapters,
        ApiKeyResolver apiKeyResolver)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(apiKeyResolver);

        _apiKeyResolver = apiKeyResolver;
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

        if (!_models.TryGetValue(modelName, out var options))
        {
            return Unavailable(modelName, 0, "模型未配置。");
        }

        if (!options.Enabled)
        {
            return Unavailable(modelName, 0, "模型已禁用。");
        }

        var apiKey = _apiKeyResolver.Resolve(options);
        if (apiKey.Required && !apiKey.Configured)
        {
            var source = string.IsNullOrWhiteSpace(apiKey.EnvironmentVariableName)
                ? "API 密钥"
                : $"环境变量 '{apiKey.EnvironmentVariableName}'";
            return Unavailable(modelName, 0, $"{source} 未配置。");
        }

        if (!_adapters.TryGetValue(modelName, out var adapter))
        {
            return Unavailable(modelName, 0, "模型适配器不可用。");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await adapter.CompleteAsync(new ModelRequest
            {
                OperationId = $"health-{Guid.NewGuid():N}",
                Role = ModelRole.Fallback,
                Prompt = "请返回 pong。",
                Metadata = new Dictionary<string, string>
                {
                    ["healthCheck"] = "true"
                }
            }, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            return response.Succeeded && !string.IsNullOrWhiteSpace(response.Content)
                ? new ModelHealthResult
                {
                    ModelName = modelName,
                    Availability = ModelAvailability.Available,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    CheckedAt = DateTimeOffset.UtcNow
                }
                : Unavailable(modelName, stopwatch.ElapsedMilliseconds, response.ErrorMessage ?? "模型返回了失败响应。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Unavailable(modelName, stopwatch.ElapsedMilliseconds, ex.Message);
        }
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
