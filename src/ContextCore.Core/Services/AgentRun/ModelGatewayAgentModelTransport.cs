using System.Diagnostics;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// ModelGatewayAgentModelTransport — IAgentModelTransport 的真实 LLM 实现
//
// 目标：
//   替代 DeterministicAgentModelTransport 作为生产环境的 IAgentModelTransport 实现。
//   通过 IModelGateway 调用真实 LLM（OpenAI / Anthropic / Mock 等，由 ModelGateway 配置决定），
//   将 AgentMessage 列表转换为 ModelRequest，并将 ModelResponse 转换为 AgentModelResponse。
//
// 设计原则：
//   1. 真实调用：通过 IModelGateway.CompleteAsync 调用真实模型，不使用关键词匹配。
//   2. 优雅降级：IModelGateway 未注册或调用失败时返回错误响应（IsFinalAnswer=true + 错误内容），
//      不抛异常，让 Agent 循环能安全终止。
//   3. Token 精确核算：从 ModelResponse 读取 InputTokens / OutputTokens，填充 AgentModelResponse，
//      供 cost budget 校验使用（DeterministicAgentModelTransport 仅粗略估算）。
//   4. 不解析 Tool 调用：本实现将模型输出作为最终答案返回（IsFinalAnswer=true）。
//      真实 Tool 调用解析需 LLM 返回结构化 function_call / tool_call JSON，
//      由调用方按自身协议解析——本 transport 不强制特定 Tool 调用格式，
//      避免与 OpenAI / Anthropic 不同 function calling schema 耦合。
//      后续可扩展为支持 OpenAI function calling 格式的子类。
// ===========================================================================

/// <summary>
/// IAgentModelTransport 的真实 LLM 实现，通过 IModelGateway 调用真实模型。
/// </summary>
/// <remarks>
/// 生产环境（Profile=ProductionHA 或 AgentModelMode=RealModel）应使用本实现替代
/// <see cref="DeterministicAgentModelTransport"/>。本类依赖 <see cref="IModelGateway"/>，
/// 若未注册则返回错误响应（不抛异常），让 Agent 循环安全终止。
/// </remarks>
public sealed class ModelGatewayAgentModelTransport : IAgentModelTransport
{
    private readonly IModelGateway? _modelGateway;
    private readonly ILogger<ModelGatewayAgentModelTransport>? _logger;
    private readonly ModelRole _modelRole;

    /// <summary>
    /// 构造 ModelGatewayAgentModelTransport。
    /// </summary>
    /// <param name="modelGateway">模型网关（null 时所有调用返回错误响应）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    /// <param name="modelRole">模型角色（默认 <see cref="ModelRole.Fallback"/>）。</param>
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

        // IModelGateway 未注册 → 返回错误响应（不抛异常，让 Agent 循环安全终止）
        if (_modelGateway is null)
        {
            _logger?.LogError(
                "ModelGatewayAgentModelTransport 调用失败：IModelGateway 未注册。runId={RunId}",
                runId);
            return BuildErrorResponse(context, "IModelGateway 未注册——生产环境需调用 AddContextModelGateway 配置模型网关。");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var request = new ModelRequest
            {
                OperationId = $"agent-{runId}-{Guid.NewGuid():N}",
                Role = _modelRole,
                Prompt = context
            };

            var response = await _modelGateway.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (!response.Succeeded)
            {
                _logger?.LogWarning(
                    "ModelGateway 调用失败：{Error}。runId={RunId}",
                    response.ErrorMessage ?? "未知错误", runId);
                return BuildErrorResponse(context, $"ModelGateway 调用失败：{response.ErrorMessage ?? "未知错误"}");
            }

            return new AgentModelResponse
            {
                Content = response.Content,
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = response.InputTokens + response.OutputTokens,
                Duration = sw.Elapsed,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                ModelArtifactId = response.OperationId,
                RawOutput = response.Content
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "ModelGateway 调用异常。runId={RunId}", runId);
            return BuildErrorResponse(context, $"ModelGateway 调用异常：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentModelResponse> CallAsync(
        string runId,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(messages);

        // 结构化消息 → 序列化为字符串，委托到 string context 重载。
        // 真实 LLM adapter 可直接消费 AgentMessage[] 作为 chat completions，
        // 但 ModelGateway 当前仅接受 string Prompt，故统一序列化。
        var context = AgentMessage.Serialize(messages);
        return await CallAsync(runId, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>构建错误响应（IsFinalAnswer=true，让 Agent 循环安全终止）。</summary>
    private static AgentModelResponse BuildErrorResponse(string context, string errorMessage)
    {
        return new AgentModelResponse
        {
            Content = $"[ModelGateway Error] {errorMessage}",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 0,
            Duration = TimeSpan.Zero,
            InputTokens = 0,
            OutputTokens = 0,
            ModelArtifactId = "model-gateway-error",
            RawOutput = errorMessage
        };
    }
}
