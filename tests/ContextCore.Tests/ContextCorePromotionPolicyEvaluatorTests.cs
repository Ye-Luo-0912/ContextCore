using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>覆盖 A2 Promotion 条件的轻量规则评估，确保默认只返回建议、不执行写入。</summary>
[TestClass]
public sealed class ContextCorePromotionPolicyEvaluatorTests
{
    [TestMethod]
    public void PromotionEvaluator_ShouldClassifyWorkingMemoryConditions()
    {
        var evaluator = new BasicPromotionPolicyEvaluator();
        var samples = new Dictionary<string, string>
        {
            ["新的架构原则"] = "新的架构原则：ContextCore 的文件访问保持读写分离。",
            ["阶段性结论"] = "阶段性结论：A2 先做可解释规则，不直接调用 LLM。",
            ["任务状态变化"] = "任务状态变化：Promotion MVP 已完成，后续进入 Review。",
            ["方案被否决"] = "方案被否决：不采用隐式全量向量化方案。",
            ["约束新增或变更"] = "约束新增：报告文件写入 docs 目录。",
            ["当前项目路线更新"] = "路线图更新：下一步推进 promotion candidate 审核。",
            ["自动化流程状态变化"] = "自动化流程进入阻塞，需要人工处理密钥配置。",
            ["小说状态变化"] = "小说剧情线发生变化，人物状态需要记录。"
        };

        foreach (var sample in samples)
        {
            var result = evaluator.Evaluate(Request(sample.Value));

            Assert.AreEqual(PromotionEvaluationDecision.PromoteToWorkingMemory, result.Decision, sample.Key);
            Assert.AreEqual(ContextMemoryLayer.Working, result.TargetLayer, sample.Key);
            CollectionAssert.Contains(result.MatchedRules.ToArray(), sample.Key);
            Assert.IsTrue(result.ShouldPromote, sample.Key);
        }
    }

    [TestMethod]
    public void PromotionEvaluator_ShouldClassifyStableMemoryConditions()
    {
        var evaluator = new BasicPromotionPolicyEvaluator();
        var samples = new Dictionary<string, string>
        {
            ["用户明确长期偏好"] = "用户明确长期偏好：以后都使用中文输出。",
            ["项目长期定位"] = "项目长期定位：ContextCore 是上下文基础设施服务。",
            ["长期稳定约束"] = "长期稳定约束：不得把明文 API Key 写入项目文件。",
            ["跨场景通用规则"] = "跨场景通用规则：涉及密钥的配置放在用户目录私有配置。",
            ["多次重复稳定模式"] = "多次重复稳定模式：所有项目都需要执行密钥扫描。"
        };

        foreach (var sample in samples)
        {
            var result = evaluator.Evaluate(Request(sample.Value));

            Assert.AreEqual(PromotionEvaluationDecision.PromoteToStableMemory, result.Decision, sample.Key);
            Assert.AreEqual(ContextMemoryLayer.Stable, result.TargetLayer, sample.Key);
            CollectionAssert.Contains(result.MatchedRules.ToArray(), sample.Key);
            Assert.IsTrue(result.ShouldPromote, sample.Key);
        }
    }

    [TestMethod]
    public void PromotionEvaluator_ShouldRejectNoPromotionConditionsBeforePromotionRules()
    {
        var evaluator = new BasicPromotionPolicyEvaluator();
        var samples = new Dictionary<string, string>
        {
            ["普通寒暄"] = "你好，谢谢，辛苦了。",
            ["临时情绪"] = "临时情绪：今天有点烦，这条不需要后续。",
            ["重复解释"] = "重复解释：前面说过的内容再解释一遍。",
            ["无后续价值支线"] = "无后续价值支线，不用继续。",
            ["已被后续覆盖的表达修正"] = "表达修正：更正前面的临时说法，已被后续覆盖。",
            ["明显一次性上下文"] = "一次性日志片段，仅这次排查使用。",
            ["未验证或临时猜测"] = "临时猜测：可能是网络问题，待确认。"
        };

        foreach (var sample in samples)
        {
            var result = evaluator.Evaluate(Request(sample.Value));

            Assert.AreEqual(PromotionEvaluationDecision.DoNotPromote, result.Decision, sample.Key);
            Assert.IsNull(result.TargetLayer, sample.Key);
            CollectionAssert.Contains(result.MatchedRules.ToArray(), sample.Key);
            Assert.IsFalse(result.ShouldPromote, sample.Key);
        }
    }

    [TestMethod]
    public void PromotionEvaluator_ShouldUseExplicitMetadataTarget()
    {
        var evaluator = new BasicPromotionPolicyEvaluator();

        var stable = evaluator.Evaluate(Request(
            "这条内容由上层流程确认可长期保留。",
            metadata: new Dictionary<string, string> { ["promotionTarget"] = "stable" }));
        var denied = evaluator.Evaluate(Request(
            "即使内容里提到长期偏好，也被元数据禁止提升。",
            metadata: new Dictionary<string, string> { ["promotion"] = "never" }));

        Assert.AreEqual(PromotionEvaluationDecision.PromoteToStableMemory, stable.Decision);
        Assert.AreEqual(ContextMemoryLayer.Stable, stable.TargetLayer);
        Assert.AreEqual(PromotionEvaluationDecision.DoNotPromote, denied.Decision);
        Assert.IsNull(denied.TargetLayer);
    }

    private static PromotionEvaluationRequest Request(
        string content,
        Dictionary<string, string>? metadata = null)
    {
        return new PromotionEvaluationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-test",
            Type = "note",
            Content = content,
            Tags = ["promotion"],
            Confidence = 0.8,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }
}
