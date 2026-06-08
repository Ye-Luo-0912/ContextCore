using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>覆盖 A2 Promotion Eval 指标计算。</summary>
[TestClass]
public sealed class ContextCorePromotionEvalRunnerTests
{
    [TestMethod]
    public void PromotionEvalRunner_ShouldReportPromotionMetrics()
    {
        var runner = new PromotionEvalRunner(new BasicPromotionPolicyEvaluator());
        var report = runner.Run(
        [
            Sample(
                "working-architecture",
                "新的架构原则：Promotion Eval 只评估，不写入存储。",
                PromotionEvaluationDecision.PromoteToWorkingMemory,
                ContextMemoryLayer.Working),
            Sample(
                "stable-preference",
                "用户明确长期偏好：输出、日志和提示信息保持中文。",
                PromotionEvaluationDecision.PromoteToStableMemory,
                ContextMemoryLayer.Stable),
            Sample(
                "no-greeting",
                "你好，谢谢，辛苦了。",
                PromotionEvaluationDecision.DoNotPromote,
                null),
            Sample(
                "no-oneoff",
                "一次性日志片段，仅这次排查使用。",
                PromotionEvaluationDecision.DoNotPromote,
                null),
            Sample(
                "review-unclear",
                "这段内容可能有点价值，但没有明确后续使用方式。",
                PromotionEvaluationDecision.NeedsReview,
                null)
        ]);

        Assert.AreEqual(5, report.TotalSamples);
        Assert.AreEqual(2, report.ExpectedPromotionSamples);
        Assert.AreEqual(2, report.CorrectPromotionCount);
        Assert.AreEqual(0, report.ErroneousPromotionCount);
        Assert.AreEqual(0, report.MissedPromotionCount);
        Assert.AreEqual(0, report.StableLayerPollutionCount);
        Assert.AreEqual(1, report.NeedsReviewCount);
        Assert.AreEqual(1.0, report.CorrectPromotionRate);
        Assert.AreEqual(0.0, report.ErroneousPromotionRate);
        Assert.AreEqual(0.2, report.NeedsReviewRate);
    }

    private static PromotionEvalSample Sample(
        string id,
        string content,
        PromotionEvaluationDecision decision,
        ContextMemoryLayer? targetLayer)
    {
        return new PromotionEvalSample
        {
            Id = id,
            Content = content,
            ExpectedDecision = decision,
            ExpectedTargetLayer = targetLayer,
            Tags = ["promotion-eval"]
        };
    }
}
