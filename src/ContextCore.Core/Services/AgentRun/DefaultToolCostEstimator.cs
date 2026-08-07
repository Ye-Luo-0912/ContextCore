using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// 服务端 Tool 成本估算器默认实现。
/// 估算不依赖模型在 <see cref="AgentToolCallRequest.EstimatedCostUsd"/> 中的填写：
/// 静态成本表（运维显式声明的每工具成本）优先，未配置工具按参数大小启发式估算
/// （约 4 字符/token，含最小基数；费用 = token × 每千 token 单价）。
/// </summary>
public sealed class DefaultToolCostEstimator : IToolCostEstimator
{
    /// <summary>未配置工具的 token 估算最小基数（空参数/极小参数也不低于此值）。</summary>
    private const long MinHeuristicTokens = 32;

    /// <summary>参数文本到 token 的粗略换算（约 4 字符/token）。</summary>
    private const int CharsPerToken = 4;

    private readonly IReadOnlyDictionary<string, ToolCostEstimate> _staticCosts;
    private readonly double _pricePerKTokenUsd;

    /// <summary>
    /// 初始化默认估算器。
    /// </summary>
    /// <param name="staticCosts">按 Tool 名称的静态成本表（null/空 = 全部走启发式）。</param>
    /// <param name="pricePerKTokenUsd">启发式费用换算单价（USD / 千 token）。</param>
    public DefaultToolCostEstimator(
        IReadOnlyDictionary<string, ToolCostEstimate>? staticCosts = null,
        double pricePerKTokenUsd = 0.002)
    {
        _staticCosts = staticCosts ?? new Dictionary<string, ToolCostEstimate>(StringComparer.OrdinalIgnoreCase);
        _pricePerKTokenUsd = pricePerKTokenUsd > 0 ? pricePerKTokenUsd : 0.002;
    }

    /// <inheritdoc />
    public ToolCostEstimate Estimate(string toolName, AgentToolCallRequest toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (_staticCosts.TryGetValue(toolName, out var configured))
        {
            return configured;
        }

        var textLength = (toolCall.Arguments?.Length ?? 0) + (toolCall.IdempotencyKey?.Length ?? 0);
        var tokens = Math.Max(MinHeuristicTokens, textLength / CharsPerToken);
        return new ToolCostEstimate
        {
            Tokens = tokens,
            CostUsd = tokens / 1000.0 * _pricePerKTokenUsd
        };
    }
}
