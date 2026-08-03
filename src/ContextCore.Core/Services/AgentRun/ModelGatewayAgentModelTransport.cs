using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// ModelGatewayAgentModelTransport — IAgentModelTransport 的真实 LLM 实现
//
// 目标（修复）：
// 替代 DeterministicAgentModelTransport 作为生产环境的 IAgentModelTransport 实现。
// 通过 IModelGateway.ChatWithToolsAsync 调用真实 LLM，传入原生 messages + Tool JSON Schema，
// 解析模型返回的结构化 Tool Call（ToolCallId / ToolName / Arguments JSON）。
//
// 设计原则：
// 1. 真实调用：通过 IModelGateway.ChatWithToolsAsync 调用真实模型（function calling），
// 不再硬编码 ToolCalls=[] IsFinalAnswer=true。
// 2. 正确的 finish reason：
// - 模型返回 tool_calls → IsFinalAnswer=false, ToolCalls=解析结果（进入 Tool Validation → Approval → Dispatch → Observation 循环）。
// - 模型返回 stop → IsFinalAnswer=true, ToolCalls=[]（产出最终答案，循环终止）。
// 3. 模型选择：使用 request.ModelArtifactId 选择模型（如果非 null），否则用 Gateway Fallback（ModelRole.Fallback）。
// 4. 失败语义：IModelGateway 未注册或调用失败时抛出异常（不包装成最终文本），让 Agent Run 进入 Failed。
// 这是 关键修复点——旧实现将错误包装为 IsFinalAnswer=true 的文本响应，
// 导致模型网关失败时 Run 错误地"成功完成"而非 Failed。
// 5. Token / 费用核算：从 ModelChatResponse 读取 InputTokens / OutputTokens / CachedInputTokens /
// EstimatedCost / BilledCost，填充 AgentModelResponse 供 cost budget 校验使用。
// ===========================================================================

/// <summary>
/// IAgentModelTransport 的真实 LLM 实现，通过 IModelGateway.ChatWithToolsAsync 调用真实模型。
/// </summary>
/// <remarks>
/// 生产环境（Profile=ProductionHA 或 AgentModelMode=RealModel）应使用本实现替代
/// <see cref="DeterministicAgentModelTransport"/>。本类依赖 <see cref="IModelGateway"/>，
/// 若未注册或调用失败则抛出异常（让 Agent Run 进入 Failed，而非包装为最终文本误导调用方）。
/// </remarks>
public sealed class ModelGatewayAgentModelTransport : IAgentModelTransport
{
    private readonly IModelGateway? _modelGateway;
    private readonly ILogger<ModelGatewayAgentModelTransport>? _logger;
    private readonly ModelRole _modelRole;

    /// <summary>
    /// 构造 ModelGatewayAgentModelTransport。
    /// </summary>
    /// <param name="modelGateway">模型网关（null 时所有调用抛异常，让 Run 进入 Failed）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    /// <param name="modelRole">模型角色（默认 <see cref="ModelRole.Fallback"/>；当 ModelArtifactId 为 null 时生效）。</param>
    public ModelGatewayAgentModelTransport(
        IModelGateway? modelGateway,
        ILogger<ModelGatewayAgentModelTransport>? logger = null,
        ModelRole modelRole = ModelRole.Fallback)
    {
        _modelGateway = modelGateway;
        _logger = logger;
        _modelRole = modelRole;
    }

    /// <inheritdoc />
    public async ValueTask<AgentModelResponse> CallAsync(
        string runId,
        string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(context);

        // string context 旧路径：包装为单条 User 消息走 AgentModelRequest 重载（无 Tool 声明）。
        // 真实 LLM 在无 Tool 时模型应直接产出 stop（IsFinalAnswer=true）。
        var messages = new[]
        {
            new AgentMessage { Role = AgentMessageRole.User, Content = context }
        };
        var request = new AgentModelRequest
        {
            RunId = runId,
            ModelArtifactId = null,
            Messages = messages,
            Tools = Array.Empty<AgentToolDefinition>(),
            DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        return await CallAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AgentModelResponse> CallAsync(
        string runId,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(messages);

        // 结构化消息旧路径：委托到 AgentModelRequest 重载（无 Tool 声明、无 ModelArtifactId、默认 5 分钟截止）。
        // 真实 LLM 在无 Tool 时模型应直接产出 stop（IsFinalAnswer=true）。
        var request = new AgentModelRequest
        {
            RunId = runId,
            ModelArtifactId = null,
            Messages = messages,
            Tools = Array.Empty<AgentToolDefinition>(),
            DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        return await CallAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AgentModelResponse> CallAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentNullException.ThrowIfNull(request.Messages);
        ArgumentNullException.ThrowIfNull(request.Tools);

        // 关键修复：IModelGateway 未注册 → 抛异常（让 Run 进入 Failed），不再包装为最终文本。
        if (_modelGateway is null)
        {
            _logger?.LogError(
                "ModelGatewayAgentModelTransport 调用失败：IModelGateway 未注册。runId={RunId}",
                request.RunId);
            throw new InvalidOperationException(
                "ModelGatewayAgentModelTransport 要求 IModelGateway 已注册——生产环境需调用 AddContextModelGateway 配置模型网关。");
        }

        var sw = Stopwatch.StartNew();
        var chatRequest = BuildChatRequest(request);
        ModelChatResponse chatResponse;

        try
        {
            chatResponse = await _modelGateway.ChatWithToolsAsync(chatRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 关键修复：模型网关调用异常 → 抛异常（让 Run 进入 Failed），不包装为最终文本。
            _logger?.LogError(ex,
                "ModelGateway ChatWithTools 调用异常。runId={RunId} modelArtifactId={ModelArtifactId}",
                request.RunId, request.ModelArtifactId);
            throw new InvalidOperationException(
                $"ModelGateway ChatWithTools 调用异常：{ex.Message}", ex);
        }
        sw.Stop();

        // 模型网关返回失败 → 抛异常（让 Run 进入 Failed）
        if (!chatResponse.Succeeded)
        {
            _logger?.LogWarning(
                "ModelGateway ChatWithTools 返回失败：{Error}。runId={RunId} modelArtifactId={ModelArtifactId}",
                chatResponse.ErrorMessage ?? "未知错误", request.RunId, request.ModelArtifactId);
            throw new InvalidOperationException(
                $"ModelGateway ChatWithTools 调用失败：{chatResponse.ErrorMessage ?? "未知错误"}");
        }

        return BuildAgentResponse(chatResponse, sw.Elapsed);
    }

    /// <summary>
    /// 将 <see cref="AgentModelRequest"/> 转换为 <see cref="ModelChatRequest"/>。
    /// </summary>
    /// <remarks>
    /// 传入原生 messages（不拼接字符串）+ Tool JSON Schema（从 AgentToolDefinition 转换），
    /// 按 ModelArtifactId 选择模型（非 null 时透传，否则用 Gateway Fallback/ModelRole）。
    /// </remarks>
    private ModelChatRequest BuildChatRequest(AgentModelRequest request)
    {
        var chatMessages = new ModelChatMessage[request.Messages.Count];
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var msg = request.Messages[i];
            chatMessages[i] = new ModelChatMessage
            {
                Role = ToModelChatRole(msg.Role),
                Content = msg.Content,
                ToolName = msg.ToolName,
                ToolCallId = msg.ToolCallId,
                ToolCalls = msg.ToolCalls is not null && msg.ToolCalls.Count > 0
                    ? msg.ToolCalls.Select(tc => new ModelToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Name,
                        ArgumentsJson = tc.Arguments ?? "{}"
                    }).ToList()
                    : null
            };
        }

        var tools = new ModelToolDefinition[request.Tools.Count];
        for (var i = 0; i < request.Tools.Count; i++)
        {
            var tool = request.Tools[i];
            tools[i] = new ModelToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersJsonSchema = tool.ParametersJsonSchema
            };
        }

        return new ModelChatRequest
        {
            OperationId = $"agent-{request.RunId}-{Guid.NewGuid():N}",
            ModelArtifactId = request.ModelArtifactId,
            Role = _modelRole,
            Messages = chatMessages,
            Tools = tools,
            DeadlineAt = request.DeadlineAt
        };
    }

    /// <summary>
    /// 将 <see cref="ModelChatResponse"/> 转换为 <see cref="AgentModelResponse"/>。
    /// </summary>
    /// <remarks>
    /// 按 finish reason 设置 IsFinalAnswer 与 ToolCalls：
    /// <list type="bullet">
    /// <item><see cref="ModelChatFinishReason.ToolCalls"/> → IsFinalAnswer=false, ToolCalls=解析结果。</item>
    /// <item><see cref="ModelChatFinishReason.Stop"/> → IsFinalAnswer=true, ToolCalls=[]。</item>
    /// <item>其他 finish reason（Length / ContentFilter / Error）→ IsFinalAnswer=true（保守终止循环）。</item>
    /// </list>
    /// </remarks>
    private static AgentModelResponse BuildAgentResponse(ModelChatResponse chatResponse, TimeSpan duration)
    {
        var isToolCalls = chatResponse.FinishReason == ModelChatFinishReason.ToolCalls
            && chatResponse.ToolCalls.Count > 0;

        var toolCalls = isToolCalls
            ? BuildToolCalls(chatResponse.ToolCalls)
            : Array.Empty<AgentToolCallRequest>();

        return new AgentModelResponse
        {
            Content = chatResponse.Content,
            ToolCalls = toolCalls,
            IsFinalAnswer = !isToolCalls,
            TokensConsumed = chatResponse.InputTokens + chatResponse.OutputTokens,
            Duration = duration,
            InputTokens = chatResponse.InputTokens,
            OutputTokens = chatResponse.OutputTokens,
            CachedInputTokens = chatResponse.CachedInputTokens,
            ModelArtifactId = chatResponse.ModelId,
            ModelId = chatResponse.ModelId,
            EstimatedCost = chatResponse.EstimatedCost,
            BilledCost = chatResponse.BilledCost,
            RawOutput = chatResponse.Content
        };
    }

    /// <summary>将 <see cref="ModelToolCall"/> 列表转换为 <see cref="AgentToolCallRequest"/> 列表。</summary>
    private static AgentToolCallRequest[] BuildToolCalls(IReadOnlyList<ModelToolCall> toolCalls)
    {
        var result = new AgentToolCallRequest[toolCalls.Count];
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            result[i] = new AgentToolCallRequest
            {
                ToolName = call.Name,
                Arguments = call.ArgumentsJson,
                ToolCallId = call.Id
            };
        }
        return result;
    }

    private static ModelChatRole ToModelChatRole(AgentMessageRole role) => role switch
    {
        AgentMessageRole.System => ModelChatRole.System,
        AgentMessageRole.User => ModelChatRole.User,
        AgentMessageRole.Assistant => ModelChatRole.Assistant,
        AgentMessageRole.Tool => ModelChatRole.Tool,
        _ => ModelChatRole.User
    };
}
