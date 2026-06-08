namespace ContextCore.Abstractions;

/// <summary>模型在网关中承担的业务角色。</summary>
public enum ModelRole
{
    /// <summary>路由决策模型。</summary>
    Router,
    /// <summary>短文本摘要模型。</summary>
    ShortSummary,
    /// <summary>向量嵌入模型。</summary>
    Embedding,
    /// <summary>重排序模型。</summary>
    Reranker,
    /// <summary>通用压缩模型。</summary>
    GeneralCompression,
    /// <summary>强推理模型，适合复杂任务。</summary>
    StrongReasoning,
    /// <summary>验证与校验模型。</summary>
    Validator,
    /// <summary>回退兜底模型。</summary>
    Fallback
}

/// <summary>模型的当前可用性状态。</summary>
public enum ModelAvailability
{
    /// <summary>可用。</summary>
    Available,
    /// <summary>不可用。</summary>
    Unavailable
}

/// <summary>单个模型端点的连接与行为配置。</summary>
public sealed class ModelEndpointOptions
{
    /// <summary>模型名称，用于路由匹配。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>提供商名称（如 "openai"、"azure"）。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>API 端点 URL。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>API 密钥（可选，建议通过环境变量注入）。</summary>
    public string? ApiKey { get; init; }

    /// <summary>请求超时时长，默认 30 秒。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>是否启用此端点。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>一个可复用的模型 API 平台配置，例如 DeepSeek、OpenAI 兼容网关或本地服务。</summary>
public sealed class ModelApiProviderOptions
{
    /// <summary>API 平台名称，供模型配置引用。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>提供商名称（如 "deepseek"、"openai-compatible"、"local-http"）。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>API 根端点 URL。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>API 密钥（可选，建议使用 env:NAME）。</summary>
    public string? ApiKey { get; init; }

    /// <summary>默认请求超时时长。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>是否启用该 API 平台。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>API 平台级附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>某个 API 平台下的具体模型 profile，描述模型名称、分类和能力标签。</summary>
public sealed class ModelProfileOptions
{
    /// <summary>模型 profile 名称，也是路由引用的逻辑模型名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>所属 API 平台名称。</summary>
    public string ApiProviderName { get; init; } = string.Empty;

    /// <summary>真实发送给 API 的模型 ID。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>模型分类，如 fast、balanced、deep、audit。</summary>
    public string? Category { get; init; }

    /// <summary>能力标签，如 compression、reasoning、json-response-format。</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>适合承担的角色名称。</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>适合处理的任务类型。</summary>
    public IReadOnlyList<string> TaskKinds { get; init; } = Array.Empty<string>();

    /// <summary>适合处理的思考模式。</summary>
    public IReadOnlyList<string> ThinkingModes { get; init; } = Array.Empty<string>();

    /// <summary>是否支持 OpenAI 兼容 response_format 字段；为空时按支持处理。</summary>
    public bool? SupportsJsonResponseFormat { get; init; }

    /// <summary>模型级超时时长；为空时继承 API 平台默认值。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>是否启用该模型。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>模型级附加元数据，会覆盖同名 API 平台元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>模型角色与端点的路由规则，支持主备回退。</summary>
public sealed class ModelRoleRoute
{
    /// <summary>该规则对应的模型角色。</summary>
    public ModelRole Role { get; init; } = ModelRole.Fallback;

    /// <summary>可选任务类型过滤器；为空时匹配该角色下所有任务。</summary>
    public string? TaskKind { get; init; }

    /// <summary>可选思考模式过滤器，如 fast、balanced、deep、audit。</summary>
    public string? ThinkingMode { get; init; }

    /// <summary>同等匹配条件下的优先级，数值越大越先选。</summary>
    public int Priority { get; init; }

    /// <summary>主要模型名称。</summary>
    public string PrimaryModelName { get; init; } = string.Empty;

    /// <summary>主要模型分类；未指定 PrimaryModelName 时按分类与能力自动选择。</summary>
    public string? PrimaryModelCategory { get; init; }

    /// <summary>路由要求模型具备的能力标签。</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>备用模型名称（可选）。</summary>
    public string? FallbackModelName { get; init; }

    /// <summary>备用模型分类；未指定 FallbackModelName 时按分类与能力自动选择。</summary>
    public string? FallbackModelCategory { get; init; }

    /// <summary>最大重试次数。</summary>
    public int MaxRetryCount { get; init; }

    /// <summary>是否启用回退逻辑。</summary>
    public bool EnableFallback { get; init; }

    /// <summary>超时时触发回退。</summary>
    public bool FallbackOnTimeout { get; init; }

    /// <summary>限流时触发回退。</summary>
    public bool FallbackOnRateLimit { get; init; }

    /// <summary>服务端错误时触发回退。</summary>
    public bool FallbackOnServerError { get; init; }

    /// <summary>响应非法 JSON 时触发回退。</summary>
    public bool FallbackOnInvalidJson { get; init; }

    /// <summary>是否为高风险任务（影响日志与监控级别）。</summary>
    public bool HighRiskTask { get; init; }
}

/// <summary>模型网关的全局配置，包含所有模型端点与路由规则。</summary>
public sealed class ModelGatewayOptions
{
    /// <summary>可复用的 API 平台配置列表。</summary>
    public IReadOnlyList<ModelApiProviderOptions> ApiProviders { get; init; } = Array.Empty<ModelApiProviderOptions>();

    /// <summary>API 平台下的具体模型 profile 列表。</summary>
    public IReadOnlyList<ModelProfileOptions> ModelProfiles { get; init; } = Array.Empty<ModelProfileOptions>();

    /// <summary>已注册的模型端点列表。</summary>
    public IReadOnlyList<ModelEndpointOptions> Models { get; init; } = Array.Empty<ModelEndpointOptions>();

    /// <summary>角色路由规则列表。</summary>
    public IReadOnlyList<ModelRoleRoute> Routes { get; init; } = Array.Empty<ModelRoleRoute>();
}

/// <summary>向模型网关发送的推理请求。</summary>
public sealed class ModelRequest
{
    /// <summary>请求唯一标识符。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>使用的模型角色。</summary>
    public ModelRole Role { get; init; } = ModelRole.Fallback;

    /// <summary>用户提示词。</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>系统提示词（可选）。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>期望的响应格式（可选，如 "json"）。</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>模型网关返回的推理响应。</summary>
public sealed class ModelResponse
{
    /// <summary>对应请求的操作 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>模型生成的文本内容。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>输入 Token 数量。</summary>
    public int InputTokens { get; init; }

    /// <summary>输出 Token 数量。</summary>
    public int OutputTokens { get; init; }

    /// <summary>是否成功完成。</summary>
    public bool Succeeded { get; init; }

    /// <summary>失败时的错误信息（可选）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>单次模型健康检查的结果。</summary>
public sealed class ModelHealthResult
{
    /// <summary>被检查的模型名称。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>当前可用性状态。</summary>
    public ModelAvailability Availability { get; init; } = ModelAvailability.Unavailable;

    /// <summary>本次检查延迟（毫秒）。</summary>
    public long LatencyMs { get; init; }

    /// <summary>最近一次错误信息（可选）。</summary>
    public string? LastError { get; init; }

    /// <summary>检查时间（UTC）。</summary>
    public DateTimeOffset CheckedAt { get; init; }
}

/// <summary>记录一次模型调用的用量与结果。</summary>
public sealed class ModelUsageLog
{
    /// <summary>对应的操作 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>使用的模型角色。</summary>
    public ModelRole Role { get; init; } = ModelRole.Fallback;

    /// <summary>实际调用的模型名称。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>提供商名称。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>是否调用成功。</summary>
    public bool Succeeded { get; init; }

    /// <summary>是否使用了回退模型。</summary>
    public bool FallbackUsed { get; init; }

    /// <summary>调用延迟（毫秒）。</summary>
    public long LatencyMs { get; init; }

    /// <summary>输入 Token 数量。</summary>
    public int InputTokens { get; init; }

    /// <summary>输出 Token 数量。</summary>
    public int OutputTokens { get; init; }

    /// <summary>失败时的错误信息（可选）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>记录时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>模型适配器接口，封装对特定模型提供商的 HTTP 调用。</summary>
public interface IModelAdapter
{
    /// <summary>适配器名称，与 <see cref="ModelEndpointOptions.Name"/> 对应。</summary>
    string Name { get; }

    /// <summary>向模型发送推理请求并返回响应。</summary>
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>模型网关接口，负责按角色路由请求到合适的模型适配器。</summary>
public interface IModelGateway
{
    /// <summary>按角色路由请求并完成推理。</summary>
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>模型健康检测服务接口。</summary>
public interface IModelHealthService
{
    /// <summary>检测指定模型的可用性与延迟。</summary>
    Task<ModelHealthResult> CheckAsync(
        string modelName,
        CancellationToken cancellationToken = default);
}

/// <summary>模型调用用量日志存储接口。</summary>
public interface IModelUsageLogStore
{
    /// <summary>保存一条用量日志。</summary>
    Task SaveAsync(
        ModelUsageLog log,
        CancellationToken cancellationToken = default);

    /// <summary>查询最近的用量日志。</summary>
    Task<IReadOnlyList<ModelUsageLog>> QueryRecentAsync(
        int take,
        CancellationToken cancellationToken = default);
}
