using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// Decision Evidence Contract 单元测试：验证证据 DTO 结构和 EvidenceAuditStatus enum 语义。
/// NullDecisionEvidenceProvider 已删除（R18-十），未接入 provider 时审计报告标记 NotConfigured。
/// </summary>
[TestClass]
[TestCategory("Decision")]
public sealed class DecisionEvidenceContractTests
{
    [TestMethod]
    public void DecisionEvidence_Dto_HasExpectedFields()
    {
        var evidence = new DecisionEvidence
        {
            ItemId = "item-1",
            PrimaryRationale = "score-below-threshold",
            SecondaryRationales = new[] { "token-budget-exceeded" },
            AlternativesConsidered = new[]
            {
                new DecisionAlternative { ItemId = "alt-1", Reason = "lower-score", Score = 0.3 }
            },
            Confidence = 0.85,
            EvidenceRefs = new[] { "trace-001", "build-abc" },
            Provenance = "retrieval-trace"
        };

        Assert.AreEqual("item-1", evidence.ItemId);
        Assert.AreEqual("score-below-threshold", evidence.PrimaryRationale);
        Assert.AreEqual(1, evidence.SecondaryRationales.Count);
        Assert.AreEqual(1, evidence.AlternativesConsidered.Count);
        Assert.AreEqual(0.85, evidence.Confidence);
        Assert.AreEqual(2, evidence.EvidenceRefs.Count);
        Assert.AreEqual("retrieval-trace", evidence.Provenance);
    }

    [TestMethod]
    public void DecisionEvidenceResult_Defaults_AreSafe()
    {
        var result = new DecisionEvidenceResult();

        Assert.AreEqual(string.Empty, result.DecisionId);
        Assert.AreEqual(0, result.Evidence.Count);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, result.MissingItemIds.Count);
    }

    [TestMethod]
    public void EvidenceAuditStatus_HasExpectedValues()
    {
        // 验证 enum 值顺序与语义：NotConfigured < Incomplete < Complete < Failed
        Assert.AreEqual(0, (int)EvidenceAuditStatus.NotConfigured);
        Assert.AreEqual(1, (int)EvidenceAuditStatus.Incomplete);
        Assert.AreEqual(2, (int)EvidenceAuditStatus.Complete);
        Assert.AreEqual(3, (int)EvidenceAuditStatus.Failed);
    }

    [TestMethod]
    public void ContextDecisionAuditReport_HasEvidenceStatusField()
    {
        var report = new ContextDecisionAuditReport();

        // 默认值应为 NotConfigured（enum 默认值 0）
        Assert.AreEqual(EvidenceAuditStatus.NotConfigured, report.EvidenceStatus);
        Assert.IsFalse(report.EvidenceComplete);
    }

    [TestMethod]
    public void ContextDecisionAuditSample_HasEvidenceStatusField()
    {
        var sample = new ContextDecisionAuditSample();

        Assert.AreEqual(EvidenceAuditStatus.NotConfigured, sample.EvidenceStatus);
        Assert.IsFalse(sample.EvidenceComplete);
    }
}
