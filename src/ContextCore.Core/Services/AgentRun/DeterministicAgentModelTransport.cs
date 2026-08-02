using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 子问题 7：DeterministicAgentModelTransport — IAgentModelTransport 的确定性 fallback 实现
//
// 不调用真实 LLM，基于 Task 关键词产生确定性响应：
//   1. 默认返回 IsFinalAnswer=true 的简单文本（适合测试和 fallback）。
//   2. 可配置为模拟 Tool 调用（基于 Task / context 中的关键词匹配）。
//   3. 同一 runId + context 产出相同响应（可复现，便于测试断言）。
//
// 设计决策：
//   - 这是 fallback 实现，生产环境应替换为真实 LLM adapter（OpenAI / Anthropic / ModelGateway）。
//   - 不消耗真实 token；TokensConsumed = context.Length / 4（粗略估算，非精确 tokenizer）。
//   - 线程安全：内部使用 ConcurrentDictionary 跟踪每个 runId 的调用次数，避免无限循环。
//   - 当检测到 context 含 "[Tool]" 观察标记（AgentMessage.Serialize 格式）且无更多 Tool 需求时，产出最终答案。
// ===========================================================================

/// <summary>
/// 子问题 7：IAgentModelTransport 的确定性 fallback 实现。
/// 基于关键词匹配产出确定性响应，不调用真实 LLM。
/// </summary>
/// <remarks>
/// 生产环境应替换为真实 LLM adapter。本实现主要用于：
/// - 单元测试（可复现的模型响应）。
/// - 开发环境（无需 API key 即可跑通 Agent 循环）。
/// - 生产 fallback（真实 adapter 不可用时降级为确定性响应）。
/// </remarks>
public sealed class DeterministicAgentModelTransport : IAgentModelTransport
{
    /// <summary>
    /// 默认 Tool 触发关键词映射（Task / context 含此关键词 → 产出对应 Tool 调用）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultToolTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = "search",
        ["query"] = "search",
        ["lookup"] = "search",
        ["read"] = "read_file",
        ["file"] = "read_file",
        ["calculate"] = "calculator",
        ["compute"] = "calculator"
    };

    private readonly ConcurrentDictionary<string, int> _callCounts = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _toolTriggers;
    private readonly int _maxCallsPerRun;

    /// <summary>
    /// 构造确定性模型传输。
    /// </summary>
    /// <param name="toolTriggers">Tool 触发关键词映射（null = 使用默认映射）。</param>
    /// <param name="maxCallsPerRun">单个 Run 最大模型调用次数（防止无限循环；默认 10）。</param>
    public DeterministicAgentModelTransport(
        IReadOnlyDictionary<string, string>? toolTriggers = null,
        int maxCallsPerRun = 10)
    {
        _toolTriggers = toolTriggers ?? DefaultToolTriggers;
        _maxCallsPerRun = maxCallsPerRun > 0 ? maxCallsPerRun : 10;
    }

    /// <inheritdoc />
    public ValueTask<AgentModelResponse> CallAsync(
        string runId,
        string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(context);

        var callCount = _callCounts.AddOrUpdate(runId, 1, (_, c) => c + 1);

        // 超过单 Run 最大调用次数 → 强制产出最终答案（防止无限循环）
        if (callCount > _maxCallsPerRun)
        {
            return ValueTask.FromResult(BuildFinalAnswerResponse(context, "已达到最大模型调用次数，强制终止。"));
        }

        // 检测 context 中是否已包含 Tool 观察结果。
        // AgentMessage.Serialize 的格式为 "[Tool]:name\ncontent"（冒号在括号外），
        // ModelGateway 提示拼接格式为 "[Tool:name]"（冒号在括号内），两种都识别。
        var hasToolObservation = context.Contains("[Tool]", StringComparison.Ordinal)
            || context.Contains("[Tool:", StringComparison.Ordinal);

        // 匹配 Tool 触发关键词
        var toolCall = MatchToolTrigger(context);
        if (toolCall is not null && !hasToolObservation)
        {
            // 首次匹配到 Tool 关键词且尚未调用过 Tool → 产出 Tool 调用
            return ValueTask.FromResult(BuildToolCallResponse(context, toolCall));
        }

        // 默认：产出最终答案
        var finalContent = hasToolObservation
            ? $"基于已观察的 Tool 结果，任务已完成。原始任务：{ExtractTask(context)}"
            : $"已处理任务：{ExtractTask(context)}";

        return ValueTask.FromResult(BuildFinalAnswerResponse(context, finalContent));
    }

    /// <inheritdoc />
    public ValueTask<AgentModelResponse> CallAsync(
        string runId,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(messages);

        // G1：结构化消息 → 一次性序列化为字符串，委托到旧路径。
        // 确定性 fallback 不需要原生消费 AgentMessage[]（真实 LLM adapter 可直接传 chat completions）。
        var context = AgentMessage.Serialize(messages);
        return CallAsync(runId, context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AgentModelResponse> CallAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 确定性 fallback 不调用真实 LLM，忽略 Tools / ModelArtifactId / DeadlineAt，
        // 委托到 CallAsync(runId, messages) 旧路径（基于关键词匹配产出确定性响应）。
        return CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>匹配 context 中的 Tool 触发关键词。</summary>
    private string? MatchToolTrigger(string context)
    {
        foreach (var (keyword, toolName) in _toolTriggers)
        {
            if (context.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return toolName;
            }
        }
        return null;
    }

    /// <summary>从 context 提取任务描述（首行或全部）。</summary>
    private static string ExtractTask(string context)
    {
        if (string.IsNullOrEmpty(context))
        {
            return "(空任务)";
        }
        var newlineIdx = context.IndexOf('\n');
        return newlineIdx > 0 ? context.Substring(0, newlineIdx) : context;
    }

    /// <summary>构建 Tool 调用响应。</summary>
    private static AgentModelResponse BuildToolCallResponse(string context, string toolName)
    {
        var arguments = JsonSerializer.Serialize(new { query = ExtractTask(context) });
        return new AgentModelResponse
        {
            Content = $"需要调用 Tool: {toolName}",
            ToolCalls = new[]
            {
                new AgentToolCallRequest
                {
                    ToolName = toolName,
                    Arguments = arguments
                }
            },
            IsFinalAnswer = false,
            TokensConsumed = EstimateTokens(context),
            Duration = TimeSpan.FromMilliseconds(10),
            InputTokens = EstimateTokens(context),
            OutputTokens = EstimateTokens($"需要调用 Tool: {toolName}"),
            ModelId = "deterministic-fallback"
        };
    }

    /// <summary>构建最终答案响应。</summary>
    private static AgentModelResponse BuildFinalAnswerResponse(string context, string content)
    {
        return new AgentModelResponse
        {
            Content = content,
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = EstimateTokens(context) + EstimateTokens(content),
            Duration = TimeSpan.FromMilliseconds(5),
            InputTokens = EstimateTokens(context),
            OutputTokens = EstimateTokens(content),
            ModelId = "deterministic-fallback"
        };
    }

    /// <summary>粗略估算 token 数（length / 4，非精确 tokenizer）。</summary>
    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        return Math.Max(1, text.Length / 4);
    }
}
