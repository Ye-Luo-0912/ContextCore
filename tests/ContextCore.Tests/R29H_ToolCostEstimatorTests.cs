using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

/// <summary>
/// 服务端 Tool 成本估算器测试：静态成本表优先、参数启发式、最小基数、费用换算。
/// </summary>
[TestClass]
[TestCategory("Tool-Validation")]
public sealed class R29H_ToolCostEstimatorTests
{
    [TestMethod]
    public void Estimate_StaticCostTable_PreferredOverHeuristic()
    {
        var estimator = new DefaultToolCostEstimator(
            staticCosts: new Dictionary<string, ToolCostEstimate>(StringComparer.OrdinalIgnoreCase)
            {
                ["expensive_tool"] = new() { Tokens = 100_000, CostUsd = 5.0 }
            });

        var estimate = estimator.Estimate("expensive_tool", new AgentToolCallRequest
        {
            ToolName = "expensive_tool",
            Arguments = "{}"
        });

        Assert.AreEqual(100_000, estimate.Tokens, "静态成本表应优先于启发式。");
        Assert.AreEqual(5.0, estimate.CostUsd, 1e-9, "静态成本表应返回配置的费用。");
    }

    [TestMethod]
    public void Estimate_Heuristic_ScalesWithArgumentSize()
    {
        var estimator = new DefaultToolCostEstimator();

        var small = estimator.Estimate("echo", new AgentToolCallRequest
        {
            ToolName = "echo",
            Arguments = "{\"text\":\"hi\"}" // 约 13 字符 → 最小基数 32
        });
        Assert.AreEqual(32, small.Tokens, "短参数应回落到最小基数。");
        Assert.AreEqual(32 / 1000.0 * 0.002, small.CostUsd, 1e-9, "费用应按 token × 单价换算。");

        var largeArgs = new string('x', 4000); // {"text":" + 4000 x + "} = 4008 字符 → 1002 tokens
        var large = estimator.Estimate("echo", new AgentToolCallRequest
        {
            ToolName = "echo",
            Arguments = "{\"text\":\"" + largeArgs + "\"}"
        });
        Assert.AreEqual(1002, large.Tokens, "长参数应按约 4 字符/token 线性估算。");
    }

    [TestMethod]
    public void Estimate_EmptyArguments_ReturnsMinimumBase()
    {
        var estimator = new DefaultToolCostEstimator();

        var estimate = estimator.Estimate("echo", new AgentToolCallRequest
        {
            ToolName = "echo",
            Arguments = string.Empty
        });

        Assert.AreEqual(32, estimate.Tokens, "空参数不应低于最小基数。");
        Assert.IsTrue(estimate.CostUsd > 0, "最小基数的费用应为正。");
    }
}
