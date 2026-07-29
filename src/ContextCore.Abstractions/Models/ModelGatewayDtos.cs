namespace ContextCore.Abstractions.Models;

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

/// <summary>模型网关弹性策略配置：重试退避、健康检查缓存等。</summary>
public sealed class ModelGatewayResilienceOptions
{
    /// <summary>重试基础延迟（指数退避的基数），默认 1 秒。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>重试最大延迟上限，默认 30 秒。</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>健康检查结果缓存 TTL，默认 30 秒。设为 Zero 表示不缓存。</summary>
    public TimeSpan HealthCheckCacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>健康检查探针请求的超时时长，默认 15 秒。</summary>
    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(15);
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

    /// <summary>弹性策略配置；为 null 时使用默认值。</summary>
    public ModelGatewayResilienceOptions? Resilience { get; init; }
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

/// <summary>
/// 支持原生 function calling 的模型适配器接口。
/// 扩展 <see cref="IModelAdapter"/>，增加原生 <see cref="ChatWithToolsAsync"/> 方法，
/// 直接向 OpenAI / Anthropic 兼容 API 传入 tools 参数并解析结构化 tool_calls 响应。
/// </summary>
public interface IChatCompletionAdapter : IModelAdapter
{
    /// <summary>
    /// 带 Tool 定义的原生结构化对话调用（OpenAI / Anthropic function calling）。
    /// </summary>
    /// <param name="request">结构化对话请求（原生 messages + tool 定义）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构化对话响应（含文本 / Tool 调用 / finish reason / token 用量）。</returns>
    Task<ModelChatResponse> ChatWithToolsAsync(
        ModelChatRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>模型网关接口，负责按角色路由请求到合适的模型适配器。</summary>
public interface IModelGateway
{
    /// <summary>按角色路由请求并完成推理。</summary>
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-1：带 Tool 定义的结构化对话调用（function calling）。
    /// </summary>
    /// <param name="request">结构化对话请求（原生 messages + tool 定义 + 模型工件）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构化对话响应（含文本 / Tool 调用 / finish reason / token 用量 / 计费）。</returns>
    /// <remarks>
    /// <b>引入背景</b>：<see cref="CompleteAsync"/> 仅接受拼接的 <see cref="ModelRequest.Prompt"/> 字符串，
    /// 不支持原生 chat completions 消息序列与 OpenAI / Anthropic function calling。
    /// 本方法让 Agent 模型 transport 能传入原生 messages + Tool JSON Schema，
    /// 并接收结构化 Tool 调用结果（ToolCallId / ToolName / Arguments JSON）。
    ///
    /// <b>降级策略</b>：实现若底层适配器不支持 function calling，可在响应中：
    /// <list type="bullet">
    ///   <item>将 Tools 拼接到 SystemPrompt 中作为提示。</item>
    ///   <item>尝试从 <see cref="ModelChatResponse.Content"/> 解析 JSON 格式的 Tool 调用。</item>
    ///   <item>解析失败时返回 <see cref="ModelChatFinishReason.Stop"/> + 原始文本。</item>
    /// </list>
    /// </remarks>
    Task<ModelChatResponse> ChatWithToolsAsync(
        ModelChatRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>P0-1：结构化对话消息角色（与 OpenAI / Anthropic chat completions 对齐）。</summary>
public enum ModelChatRole : byte
{
    /// <summary>系统指令。</summary>
    System = 0,
    /// <summary>用户输入。</summary>
    User = 1,
    /// <summary>模型输出（assistant 文本或 Tool 调用）。</summary>
    Assistant = 2,
    /// <summary>Tool 观察结果（function/tool response）。</summary>
    Tool = 3
}

/// <summary>P0-1：结构化对话消息（chat completions 单条消息）。</summary>
public sealed record ModelChatMessage
{
    /// <summary>消息角色。</summary>
    public required ModelChatRole Role { get; init; }

    /// <summary>消息内容（System/User/Assistant 文本，或 Tool 观察结果）。</summary>
    public required string Content { get; init; }

    /// <summary>Tool 名称（仅 Role=Tool 时填充；用于审计与 ToolCall 关联）。</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool 调用 ID（仅 Role=Tool 时填充；与引发本次观察的 ModelToolCall.Id 对应）。</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>P0-1：向模型声明的 Tool 定义（OpenAI / Anthropic function calling 兼容）。</summary>
public sealed record ModelToolDefinition
{
    /// <summary>Tool 名称。</summary>
    public required string Name { get; init; }

    /// <summary>Tool 描述（向模型说明何时调用此 Tool）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Tool 参数的 JSON Schema 字符串（OpenAI / Anthropic function calling 兼容）。
    /// 例如：<c>{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}</c>。
    /// </summary>
    public required string ParametersJsonSchema { get; init; }
}

/// <summary>P0-1：模型返回的结构化 Tool 调用。</summary>
public sealed record ModelToolCall
{
    /// <summary>
    /// Tool 调用 ID（由模型分配，如 OpenAI 的 tool_call_id）。
    /// 调用方在后续 Tool 观察消息中回填此 ID 以关联调用与结果。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Tool 名称（与 <see cref="ModelToolDefinition.Name"/> 对应）。</summary>
    public required string Name { get; init; }

    /// <summary>Tool 参数（JSON 字符串；语义由 Tool 实现约定）。</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>P0-1：模型对话 finish reason（与 OpenAI / Anthropic 对齐）。</summary>
public enum ModelChatFinishReason : byte
{
    /// <summary>模型自然停止（产出最终答案；无 Tool 调用）。</summary>
    Stop = 0,
    /// <summary>模型请求调用 Tool（finish_reason=tool_calls）。</summary>
    ToolCalls = 1,
    /// <summary>达到最大 token 上限。</summary>
    Length = 2,
    /// <summary>被内容过滤器终止。</summary>
    ContentFilter = 3,
    /// <summary>调用失败（参见 <see cref="ModelChatResponse.ErrorMessage"/>）。</summary>
    Error = 4
}

/// <summary>P0-1：带 Tool 定义的结构化对话请求。</summary>
public sealed record ModelChatRequest
{
    /// <summary>请求唯一标识符。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>
    /// 模型工件 ID（指定具体模型；null = 由网关按 <see cref="Role"/> 路由）。
    /// </summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>使用的模型角色（ModelArtifactId 为 null 时生效）。</summary>
    public ModelRole Role { get; init; } = ModelRole.Fallback;

    /// <summary>结构化消息列表（按时间顺序）。</summary>
    public required IReadOnlyList<ModelChatMessage> Messages { get; init; }

    /// <summary>本次调用可见的 Tool 定义集合（空 = 无 function calling）。</summary>
    public IReadOnlyList<ModelToolDefinition> Tools { get; init; } = Array.Empty<ModelToolDefinition>();

    /// <summary>期望的响应格式（可选，如 "json"）。</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>调用截止时间（UTC）；网关应在此时间前完成调用。</summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>P0-1：带 Tool 定义的结构化对话响应。</summary>
public sealed record ModelChatResponse
{
    /// <summary>对应请求的操作 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>模型生成的文本内容（可能为空，当模型直接产出 Tool 调用时）。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>模型请求的 Tool 调用列表（空 = 无 Tool 调用）。</summary>
    public IReadOnlyList<ModelToolCall> ToolCalls { get; init; } = Array.Empty<ModelToolCall>();

    /// <summary>finish reason（stop / tool_calls / length / content_filter / error）。</summary>
    public ModelChatFinishReason FinishReason { get; init; } = ModelChatFinishReason.Stop;

    /// <summary>输入 Token 数量。</summary>
    public int InputTokens { get; init; }

    /// <summary>输出 Token 数量。</summary>
    public int OutputTokens { get; init; }

    /// <summary>命中缓存的输入 Token 数量（prompt caching）。</summary>
    public int CachedInputTokens { get; init; }

    /// <summary>是否成功完成。</summary>
    public bool Succeeded { get; init; }

    /// <summary>失败时的错误信息（可选）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>实际调用的模型标识（如 "gpt-4o-2024-08-06"）。</summary>
    public string? ModelId { get; init; }

    /// <summary>估算费用（美元）。</summary>
    public double EstimatedCost { get; init; }

    /// <summary>实际计费费用（美元；考虑缓存折扣）。</summary>
    public double BilledCost { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
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
