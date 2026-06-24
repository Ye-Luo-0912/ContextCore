using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ModelGateway.Infrastructure;

namespace ContextCore.ModelGateway.Adapters;

/// <summary>适配 OpenAI 兼容 HTTP Chat Completion API 的模型适配器。</summary>
public sealed class OpenAiCompatibleModelAdapter : HttpChatCompletionAdapterBase
{
    public OpenAiCompatibleModelAdapter(ModelEndpointOptions options)
        : base(options, "openai-compatible", null, null)
    {
    }

    public OpenAiCompatibleModelAdapter(ModelEndpointOptions options, ApiKeyResolver apiKeyResolver)
        : base(options, "openai-compatible", null, apiKeyResolver)
    {
    }

    public OpenAiCompatibleModelAdapter(ModelEndpointOptions options, HttpClient httpClient)
        : base(options, "openai-compatible", httpClient, null)
    {
    }

    public OpenAiCompatibleModelAdapter(
        ModelEndpointOptions options,
        HttpClient httpClient,
        ApiKeyResolver apiKeyResolver)
        : base(options, "openai-compatible", httpClient, apiKeyResolver)
    {
    }
}

/// <summary>适配本地 HTTP 接口模型的模型适配器。</summary>
public sealed class LocalHttpModelAdapter : HttpChatCompletionAdapterBase
{
    public LocalHttpModelAdapter(ModelEndpointOptions options)
        : base(options, "local-http", null, null)
    {
    }

    public LocalHttpModelAdapter(ModelEndpointOptions options, ApiKeyResolver apiKeyResolver)
        : base(options, "local-http", null, apiKeyResolver)
    {
    }

    public LocalHttpModelAdapter(ModelEndpointOptions options, HttpClient httpClient)
        : base(options, "local-http", httpClient, null)
    {
    }

    public LocalHttpModelAdapter(
        ModelEndpointOptions options,
        HttpClient httpClient,
        ApiKeyResolver apiKeyResolver)
        : base(options, "local-http", httpClient, apiKeyResolver)
    {
    }
}

/// <summary>基于 HTTP 的 Chat Completion 适配器基类，封装认证、请求构建、响应解析等公共逻辑。</summary>
public abstract class HttpChatCompletionAdapterBase : IModelAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly ModelEndpointOptions _options;
    private readonly ApiKeyResolver _apiKeyResolver;
    private readonly string _provider;

    protected HttpChatCompletionAdapterBase(
        ModelEndpointOptions options,
        string provider,
        HttpClient? httpClient,
        ApiKeyResolver? apiKeyResolver)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _apiKeyResolver = apiKeyResolver ?? new ApiKeyResolver();
        _provider = string.IsNullOrWhiteSpace(options.Provider) ? provider : options.Provider;
        _httpClient = httpClient ?? new HttpClient();
        Name = string.IsNullOrWhiteSpace(options.Name) ? options.Metadata.GetValueOrDefault("model", provider) : options.Name;
    }

    public string Name { get; }

    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;

        if (!_options.Enabled)
        {
            return Failure(operationId, "模型端点已禁用。", "unavailable");
        }

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return Failure(operationId, "模型端点未配置。", "unavailable");
        }

        var apiKey = _apiKeyResolver.Resolve(_options);
        if (apiKey.Required && !apiKey.Configured)
        {
            var source = string.IsNullOrWhiteSpace(apiKey.EnvironmentVariableName)
                ? "API 密钥"
                : $"环境变量 '{apiKey.EnvironmentVariableName}'";
            return Failure(operationId, $"{source} 未配置。", "unavailable");
        }

        using var timeoutSource = _options.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutSource is not null)
        {
            timeoutSource.CancelAfter(_options.Timeout);
        }

        // Endpoint 可配置为服务根地址或完整 /chat/completions 地址，适配器统一补齐最终路径。
        var effectiveToken = timeoutSource?.Token ?? cancellationToken;
        var endpoint = BuildChatCompletionsUri(_options.Endpoint);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(CreateRequestPayload(request), JsonOptions),
            Encoding.UTF8,
            "application/json");

        if (!string.IsNullOrWhiteSpace(apiKey.Value))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Value);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, effectiveToken).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(effectiveToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    operationId,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(responseText, 240)}",
                    ClassifyHttpStatus(response.StatusCode),
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            return ParseCompletionResponse(operationId, responseText, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(operationId, "模型请求已超时。", "timeout", latencyMs: stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return Failure(
                operationId,
                ex.Message,
                ex.StatusCode is null ? "unavailable" : ClassifyHttpStatus(ex.StatusCode.Value),
                ex.StatusCode is null ? null : (int)ex.StatusCode.Value,
                stopwatch.ElapsedMilliseconds);
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            return Failure(operationId, ex.Message, "invalid_json", latencyMs: stopwatch.ElapsedMilliseconds);
        }
    }

    private object CreateRequestPayload(ModelRequest request)
    {
        var messages = new List<Dictionary<string, string>>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt!
            });
        }

        messages.Add(new Dictionary<string, string>
        {
            ["role"] = "user",
            ["content"] = request.Prompt
        });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = ResolveModelName(_options),
            ["messages"] = messages
        };

        if (RequestExpectsJson(request.ResponseFormat) && SupportsJsonResponseFormat(_options))
        {
            // 目前只使用 OpenAI 兼容的 json_object 约束；更复杂 schema 后续可从 ResponseFormat 扩展。
            payload["response_format"] = new Dictionary<string, string>
            {
                ["type"] = "json_object"
            };
        }

        return payload;
    }

    private ModelResponse ParseCompletionResponse(string operationId, string responseText, long latencyMs)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        var content = string.Empty;
        string? finishReason = null;
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var messageContent))
            {
                content = ReadString(messageContent);
            }
            else if (firstChoice.TryGetProperty("text", out var text))
            {
                content = ReadString(text);
            }

            if (firstChoice.TryGetProperty("finish_reason", out var finishReasonValue))
            {
                finishReason = ReadString(finishReasonValue);
            }
        }

        var inputTokens = 0;
        var outputTokens = 0;
        var totalTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = ReadFirstInt(usage, "prompt_tokens", "promptTokens", "input_tokens", "inputTokens");
            outputTokens = ReadFirstInt(usage, "completion_tokens", "completionTokens", "output_tokens", "outputTokens");
            totalTokens = ReadFirstInt(usage, "total_tokens", "totalTokens");
        }

        var metadata = new Dictionary<string, string>
        {
            ["modelName"] = Name,
            ["provider"] = _provider,
            ["latencyMs"] = latencyMs.ToString(),
            ["failureReason"] = string.IsNullOrWhiteSpace(content) ? "empty_response" : "none"
        };

        if (root.TryGetProperty("model", out var responseModel))
        {
            metadata["responseModel"] = ReadString(responseModel);
        }

        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            metadata["finishReason"] = finishReason!;
        }

        if (totalTokens > 0)
        {
            metadata["totalTokens"] = totalTokens.ToString();
        }

        return new ModelResponse
        {
            OperationId = operationId,
            Content = content,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Succeeded = !string.IsNullOrWhiteSpace(content),
            ErrorMessage = string.IsNullOrWhiteSpace(content) ? "模型返回了空内容。" : null,
            Metadata = metadata
        };
    }

    private ModelResponse Failure(
        string operationId,
        string errorMessage,
        string failureReason,
        int? statusCode = null,
        long latencyMs = 0)
    {
        var metadata = new Dictionary<string, string>
        {
            ["modelName"] = Name,
            ["provider"] = _provider,
            ["failureReason"] = failureReason,
            ["latencyMs"] = latencyMs.ToString()
        };

        if (statusCode is not null)
        {
            metadata["httpStatusCode"] = statusCode.Value.ToString();
        }

        return new ModelResponse
        {
            OperationId = operationId,
            Content = string.Empty,
            Succeeded = false,
            ErrorMessage = errorMessage,
            Metadata = metadata
        };
    }

    private static string BuildChatCompletionsUri(string endpoint)
    {
        endpoint = endpoint.TrimEnd('/');
        return endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"{endpoint}/chat/completions";
    }

    private static string ResolveModelName(ModelEndpointOptions options)
    {
        return options.Metadata.TryGetValue("model", out var model)
            && !string.IsNullOrWhiteSpace(model)
                ? model
                : options.Name;
    }

    private static bool RequestExpectsJson(string? responseFormat)
    {
        return !string.IsNullOrWhiteSpace(responseFormat)
            && responseFormat.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsJsonResponseFormat(ModelEndpointOptions options)
    {
        return !options.Metadata.TryGetValue("supportsJsonResponseFormat", out var configured)
            || !bool.TryParse(configured, out var supports)
            || supports;
    }

    private static int ReadFirstInt(JsonElement parent, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (parent.TryGetProperty(propertyName, out var value)
                && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string ReadString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static string ClassifyHttpStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code == 429)
        {
            return "rate_limit";
        }

        if (code >= 500)
        {
            return "server_error";
        }

        return "unavailable";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

}
