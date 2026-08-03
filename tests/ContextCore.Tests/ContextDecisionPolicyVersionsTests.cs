using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// 验收测试：验证 ContextDecisionPolicyVersions 按能力独立演进，
/// 不再绑定全项目阶段编号（如 /）。V17_0/V18_0 保留为 alias 供历史消费者平滑迁移。
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class ContextDecisionPolicyVersionsTests
{
    /// <summary> 5 个能力作用域的版本常量字符串格式为 "capability-name/version"。</summary>
    [TestMethod]
    public void PolicyVersions_CapabilityScopedConstants_FollowSlashVersionFormat()
    {
        Assert.AreEqual("decision-schema/2.0", ContextDecisionPolicyVersions.DecisionSchemaV2_0);
        Assert.AreEqual("package-policy/3.1", ContextDecisionPolicyVersions.PackagePolicyV3_1);
        Assert.AreEqual("retrieval-policy/4.0", ContextDecisionPolicyVersions.RetrievalPolicyV4_0);
        Assert.AreEqual("relation-profile/2.0", ContextDecisionPolicyVersions.RelationProfileV2_0);
        Assert.AreEqual("quality-contract/1.0", ContextDecisionPolicyVersions.QualityContractV1_0);
    }

    /// <summary> 历史别名 V17_0 等价于新常量 DecisionSchemaV2_0（向后兼容）。</summary>
    [TestMethod]
    public void PolicyVersions_V17_0_AliasEqualsDecisionSchemaV2_0()
    {
        Assert.AreEqual(
            ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ContextDecisionPolicyVersions.V17_0,
            "V17_0 必须是 DecisionSchemaV2_0 的别名，历史消费者透明迁移");
        Assert.AreEqual("decision-schema/2.0", ContextDecisionPolicyVersions.V17_0);
    }

    /// <summary> 历史别名 V18_0 等价于新常量 QualityContractV1_0（向后兼容）。</summary>
    [TestMethod]
    public void PolicyVersions_V18_0_AliasEqualsQualityContractV1_0()
    {
        Assert.AreEqual(
            ContextDecisionPolicyVersions.QualityContractV1_0,
            ContextDecisionPolicyVersions.V18_0,
            "V18_0 必须是 QualityContractV1_0 的别名，历史消费者透明迁移");
        Assert.AreEqual("quality-contract/1.0", ContextDecisionPolicyVersions.V18_0);
    }

    /// <summary> 历史别名不再包含全项目阶段编号字符串（v17.0/v18.0 等）。</summary>
    [TestMethod]
    public void PolicyVersions_Aliases_DoNotContainStageNumberSuffixes()
    {
        // 历史值 "context-decision-foundation/v17.0" / "context-decision-evidence/v18.0" 不应再出现
        Assert.IsFalse(ContextDecisionPolicyVersions.V17_0.Contains("v17.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ContextDecisionPolicyVersions.V18_0.Contains("v18.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ContextDecisionPolicyVersions.V17_0.Contains("context-decision-foundation", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ContextDecisionPolicyVersions.V18_0.Contains("context-decision-evidence", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary> ContextDecisionRecord 默认 PolicyVersion 为 DecisionSchemaV2_0（按能力命名）。</summary>
    [TestMethod]
    public void PolicyVersions_ContextDecisionRecord_DefaultUsesDecisionSchemaV2_0()
    {
        var record = new ContextDecisionRecord();
        Assert.AreEqual(
            ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            record.PolicyVersion,
            "ContextDecisionRecord 默认 PolicyVersion 必须为 DecisionSchemaV2_0");
    }

    /// <summary> ContextDecisionAuditReport 默认 PolicyVersion 为 DecisionSchemaV2_0。</summary>
    [TestMethod]
    public void PolicyVersions_ContextDecisionAuditReport_DefaultUsesDecisionSchemaV2_0()
    {
        var report = new ContextDecisionAuditReport();
        Assert.AreEqual(
            ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            report.PolicyVersion);
    }

    /// <summary> PackageQualityReport 默认 PolicyVersion 为 QualityContractV1_0。</summary>
    [TestMethod]
    public void PolicyVersions_PackageQualityReport_DefaultUsesQualityContractV1_0()
    {
        var report = new PackageQualityReport();
        Assert.AreEqual(
            ContextDecisionPolicyVersions.QualityContractV1_0,
            report.PolicyVersion);
    }

    /// <summary> DecisionEvidenceV2Result 默认 PolicyVersion 为 QualityContractV1_0。</summary>
    [TestMethod]
    public void PolicyVersions_DecisionEvidenceV2Result_DefaultUsesQualityContractV1_0()
    {
        // 注：DecisionEvidenceV2.PolicyVersion 由消费者填充（默认 string.Empty），
        // V2 解析结果包装类 DecisionEvidenceV2Result 才使用 QualityContractV1_0 作为默认策略版本。
        var result = new DecisionEvidenceV2Result();
        Assert.AreEqual(
            ContextDecisionPolicyVersions.QualityContractV1_0,
            result.PolicyVersion);
    }
}
