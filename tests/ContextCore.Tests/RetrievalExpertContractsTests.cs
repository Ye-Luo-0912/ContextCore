using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// R20-1：Multi-Expert Retrieval Routing 契约测试。
///
/// 验证目标：
///   1. RetrievalExpert 枚举 8 值（Unknown + 8 Expert）与 ContextCandidateSource 对齐
///   2. Mandatory / Constraint 永远启用（Mask 无法关闭）
///   3. RetrievalExpertMask 位运算正确（With / IsEnabled）
///   4. AllEnabled / MandatoryOnly 默认值合理
///   5. GetEnabledExperts 按枚举顺序返回
///   6. ExpertRoutingDecision 必填字段 + 默认值
///   7. ExpertRoutingDecisionSet.IsExpertEnabled 强制 Mandatory/Constraint=true
///   8. ExpertRoutingDecisionSet.GetDecision 未找到返回 null
///   9. 5 channel 对齐：ChannelToExperts 映射 5 channel 到 Expert
///  10. ExpertToChannels 映射 Expert 到 channel
///  11. ShouldExecuteChannel 在所有 Expert 禁用时返回 false
///  12. ShouldExecuteChannel MandatoryRecallChannel 永远 true
///  13. HasDedicatedChannel Recency/Constraint 返回 false
///  14. 契约无存储 I/O（反射验证）
/// </summary>
[TestClass]
[TestCategory("R20")]
public sealed class RetrievalExpertContractsTests
{
    // =========================================================================
    // 1. RetrievalExpert 枚举
    // =========================================================================

    [TestMethod]
    public void RetrievalExpert_Has9Values_IncludingUnknown()
    {
        var values = Enum.GetValues<RetrievalExpert>();
        Assert.AreEqual(9, values.Length);
        Assert.IsTrue(values.Contains(RetrievalExpert.Unknown));
        Assert.IsTrue(values.Contains(RetrievalExpert.Mandatory));
        Assert.IsTrue(values.Contains(RetrievalExpert.Constraint));
        Assert.IsTrue(values.Contains(RetrievalExpert.Lexical));
        Assert.IsTrue(values.Contains(RetrievalExpert.Semantic));
        Assert.IsTrue(values.Contains(RetrievalExpert.WorkingMemory));
        Assert.IsTrue(values.Contains(RetrievalExpert.StableMemory));
        Assert.IsTrue(values.Contains(RetrievalExpert.Graph));
        Assert.IsTrue(values.Contains(RetrievalExpert.Recency));
    }

    [TestMethod]
    public void RetrievalExpert_ValuesAreUnique()
    {
        var values = Enum.GetValues<RetrievalExpert>().Select(v => (byte)v).ToList();
        var uniqueCount = values.Distinct().Count();
        Assert.AreEqual(values.Count, uniqueCount, "枚举值必须唯一");
    }

    // =========================================================================
    // 2. Mandatory / Constraint 永远启用
    // =========================================================================

    [TestMethod]
    public void RetrievalExpertMask_AllEnabled_MandatoryAndConstraintAlwaysTrue()
    {
        var mask = RetrievalExpertMask.AllEnabled;

        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Constraint));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Lexical));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Semantic));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.WorkingMemory));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.StableMemory));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Graph));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Recency));
    }

    [TestMethod]
    public void RetrievalExpertMask_MandatoryOnly_OnlyMandatoryAndConstraintEnabled()
    {
        var mask = RetrievalExpertMask.MandatoryOnly;

        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Constraint));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Lexical));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Semantic));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.WorkingMemory));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.StableMemory));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Graph));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Recency));
    }

    [TestMethod]
    public void RetrievalExpertMask_ZeroMask_StillEnablesMandatoryAndConstraint()
    {
        // 即使传入 0，Mandatory / Constraint 仍强制启用
        var mask = new RetrievalExpertMask(0);

        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Constraint));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Lexical));
    }

    [TestMethod]
    public void RetrievalExpertMask_With_DisablingMandatory_HasNoEffect()
    {
        var mask = RetrievalExpertMask.AllEnabled.With(RetrievalExpert.Mandatory, enabled: false);

        // Mandatory 仍然启用（无法关闭）
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Mandatory));
    }

    [TestMethod]
    public void RetrievalExpertMask_With_DisablingConstraint_HasNoEffect()
    {
        var mask = RetrievalExpertMask.AllEnabled.With(RetrievalExpert.Constraint, enabled: false);

        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Constraint));
    }

    // =========================================================================
    // 3. RetrievalExpertMask 位运算
    // =========================================================================

    [TestMethod]
    public void RetrievalExpertMask_With_EnablingAndDisabling_NonMandatory()
    {
        var mask = RetrievalExpertMask.MandatoryOnly;

        // 启用 Lexical
        mask = mask.With(RetrievalExpert.Lexical, enabled: true);
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Lexical));

        // 禁用 Lexical
        mask = mask.With(RetrievalExpert.Lexical, enabled: false);
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Lexical));

        // 启用多个
        mask = mask.With(RetrievalExpert.Semantic, enabled: true)
                  .With(RetrievalExpert.Graph, enabled: true);
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Semantic));
        Assert.IsTrue(mask.IsEnabled(RetrievalExpert.Graph));
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Lexical));
    }

    [TestMethod]
    public void RetrievalExpertMask_With_UnknownExpert_ReturnsSelf()
    {
        var mask = RetrievalExpertMask.AllEnabled;
        var newMask = mask.With(RetrievalExpert.Unknown, enabled: false);

        Assert.AreEqual(mask.Mask, newMask.Mask);
    }

    [TestMethod]
    public void RetrievalExpertMask_IsEnabled_Unknown_ReturnsFalse()
    {
        var mask = RetrievalExpertMask.AllEnabled;
        Assert.IsFalse(mask.IsEnabled(RetrievalExpert.Unknown));
    }

    // =========================================================================
    // 4/5. GetEnabledExperts + EnabledCount
    // =========================================================================

    [TestMethod]
    public void RetrievalExpertMask_GetEnabledExperts_AllEnabled_Returns8Experts()
    {
        var mask = RetrievalExpertMask.AllEnabled;
        var experts = mask.GetEnabledExperts();

        Assert.AreEqual(8, experts.Count);
        Assert.AreEqual(8, mask.EnabledCount);
        // 按枚举顺序：Mandatory → Constraint → Lexical → Semantic → WorkingMemory → StableMemory → Graph → Recency
        Assert.AreEqual(RetrievalExpert.Mandatory, experts[0]);
        Assert.AreEqual(RetrievalExpert.Constraint, experts[1]);
        Assert.AreEqual(RetrievalExpert.Lexical, experts[2]);
        Assert.AreEqual(RetrievalExpert.Semantic, experts[3]);
        Assert.AreEqual(RetrievalExpert.WorkingMemory, experts[4]);
        Assert.AreEqual(RetrievalExpert.StableMemory, experts[5]);
        Assert.AreEqual(RetrievalExpert.Graph, experts[6]);
        Assert.AreEqual(RetrievalExpert.Recency, experts[7]);
    }

    [TestMethod]
    public void RetrievalExpertMask_GetEnabledExperts_MandatoryOnly_Returns2Experts()
    {
        var mask = RetrievalExpertMask.MandatoryOnly;
        var experts = mask.GetEnabledExperts();

        Assert.AreEqual(2, experts.Count);
        Assert.AreEqual(RetrievalExpert.Mandatory, experts[0]);
        Assert.AreEqual(RetrievalExpert.Constraint, experts[1]);
    }

    // =========================================================================
    // 6. ExpertRoutingDecision
    // =========================================================================

    [TestMethod]
    public void ExpertRoutingDecision_Defaults_EnabledTrue_TopK50_Budget1000()
    {
        var decision = new ExpertRoutingDecision { Expert = RetrievalExpert.Lexical };

        Assert.IsTrue(decision.Enabled);
        Assert.AreEqual(50, decision.TopK);
        Assert.AreEqual(1000, decision.TokenBudget);
        Assert.AreEqual(1.0, decision.Weight);
        Assert.AreEqual("default", decision.ReasonCode);
        Assert.IsNull(decision.DisabledReason);
        Assert.AreEqual(0, decision.Metadata.Count);
    }

    [TestMethod]
    public void ExpertRoutingDecision_WithExpression_PreservesExpert()
    {
        var decision = new ExpertRoutingDecision { Expert = RetrievalExpert.Semantic };
        var updated = decision with { TopK = 10, TokenBudget = 200, Weight = 0.5 };

        Assert.AreEqual(RetrievalExpert.Semantic, updated.Expert);
        Assert.AreEqual(10, updated.TopK);
        Assert.AreEqual(200, updated.TokenBudget);
        Assert.AreEqual(0.5, updated.Weight);
    }

    [TestMethod]
    public void ExpertRoutingDecision_Disabled_WithDisabledReason()
    {
        var decision = new ExpertRoutingDecision
        {
            Expert = RetrievalExpert.Graph,
            Enabled = false,
            ReasonCode = "ablation-disabled",
            DisabledReason = "graph expert disabled for ablation study"
        };

        Assert.IsFalse(decision.Enabled);
        Assert.AreEqual("ablation-disabled", decision.ReasonCode);
        Assert.AreEqual("graph expert disabled for ablation study", decision.DisabledReason);
    }

    // =========================================================================
    // 7/8. ExpertRoutingDecisionSet
    // =========================================================================

    [TestMethod]
    public void DecisionSet_IsExpertEnabled_MandatoryAndConstraint_AlwaysTrue()
    {
        var set = new ExpertRoutingDecisionSet
        {
            Decisions = new[]
            {
                new ExpertRoutingDecision { Expert = RetrievalExpert.Mandatory, Enabled = false },  // 即使 false
                new ExpertRoutingDecision { Expert = RetrievalExpert.Constraint, Enabled = false }, // 即使 false
                new ExpertRoutingDecision { Expert = RetrievalExpert.Lexical, Enabled = true }
            }
        };

        // Mandatory / Constraint 永远返回 true（忽略 Enabled=false）
        Assert.IsTrue(set.IsExpertEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(set.IsExpertEnabled(RetrievalExpert.Constraint));
        // 其他 Expert 尊重 Enabled 字段
        Assert.IsTrue(set.IsExpertEnabled(RetrievalExpert.Lexical));
    }

    [TestMethod]
    public void DecisionSet_IsExpertEnabled_NotInDecisions_ReturnsFalse()
    {
        var set = new ExpertRoutingDecisionSet
        {
            Decisions = new[]
            {
                new ExpertRoutingDecision { Expert = RetrievalExpert.Lexical }
            }
        };

        // Semantic 不在 Decisions 中
        Assert.IsFalse(set.IsExpertEnabled(RetrievalExpert.Semantic));
        // 但 Mandatory/Constraint 仍永远 true
        Assert.IsTrue(set.IsExpertEnabled(RetrievalExpert.Mandatory));
    }

    [TestMethod]
    public void DecisionSet_GetDecision_ReturnsMatchingDecision()
    {
        var lexical = new ExpertRoutingDecision { Expert = RetrievalExpert.Lexical, TopK = 10 };
        var set = new ExpertRoutingDecisionSet
        {
            Decisions = new[] { lexical }
        };

        var result = set.GetDecision(RetrievalExpert.Lexical);
        Assert.IsNotNull(result);
        Assert.AreEqual(10, result.TopK);
    }

    [TestMethod]
    public void DecisionSet_GetDecision_NotFound_ReturnsNull()
    {
        var set = new ExpertRoutingDecisionSet
        {
            Decisions = Array.Empty<ExpertRoutingDecision>()
        };

        var result = set.GetDecision(RetrievalExpert.Semantic);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DecisionSet_Defaults_RouterIdAndVersion()
    {
        var set = new ExpertRoutingDecisionSet
        {
            Decisions = Array.Empty<ExpertRoutingDecision>()
        };

        Assert.AreEqual("default-router", set.RouterId);
        Assert.AreEqual("v1", set.RouterVersion);
    }

    // =========================================================================
    // 9/10. 5 channel 对齐
    // =========================================================================

    [TestMethod]
    public void ChannelToExperts_Has5Channels()
    {
        Assert.AreEqual(5, RetrievalExpertChannels.AllChannels.Count);
        Assert.IsTrue(RetrievalExpertChannels.ChannelToExperts.ContainsKey(RetrievalExpertChannels.MandatoryRecallChannel));
        Assert.IsTrue(RetrievalExpertChannels.ChannelToExperts.ContainsKey(RetrievalExpertChannels.ContextRecallChannel));
        Assert.IsTrue(RetrievalExpertChannels.ChannelToExperts.ContainsKey(RetrievalExpertChannels.VectorRecallChannel));
        Assert.IsTrue(RetrievalExpertChannels.ChannelToExperts.ContainsKey(RetrievalExpertChannels.MemoryRecallChannel));
        Assert.IsTrue(RetrievalExpertChannels.ChannelToExperts.ContainsKey(RetrievalExpertChannels.RelationRecallChannel));
    }

    [TestMethod]
    public void ChannelToExperts_MapsMandatoryRecallToMandatoryExpert()
    {
        var experts = RetrievalExpertChannels.ChannelToExperts[RetrievalExpertChannels.MandatoryRecallChannel];
        Assert.AreEqual(1, experts.Count);
        Assert.AreEqual(RetrievalExpert.Mandatory, experts[0]);
    }

    [TestMethod]
    public void ChannelToExperts_MapsContextRecallToLexicalExpert()
    {
        var experts = RetrievalExpertChannels.ChannelToExperts[RetrievalExpertChannels.ContextRecallChannel];
        Assert.AreEqual(1, experts.Count);
        Assert.AreEqual(RetrievalExpert.Lexical, experts[0]);
    }

    [TestMethod]
    public void ChannelToExperts_MapsVectorRecallToSemanticExpert()
    {
        var experts = RetrievalExpertChannels.ChannelToExperts[RetrievalExpertChannels.VectorRecallChannel];
        Assert.AreEqual(1, experts.Count);
        Assert.AreEqual(RetrievalExpert.Semantic, experts[0]);
    }

    [TestMethod]
    public void ChannelToExperts_MapsMemoryRecallToWorkingMemoryAndStableMemory()
    {
        var experts = RetrievalExpertChannels.ChannelToExperts[RetrievalExpertChannels.MemoryRecallChannel];
        Assert.AreEqual(2, experts.Count);
        Assert.IsTrue(experts.Contains(RetrievalExpert.WorkingMemory));
        Assert.IsTrue(experts.Contains(RetrievalExpert.StableMemory));
    }

    [TestMethod]
    public void ChannelToExperts_MapsRelationRecallToGraphExpert()
    {
        var experts = RetrievalExpertChannels.ChannelToExperts[RetrievalExpertChannels.RelationRecallChannel];
        Assert.AreEqual(1, experts.Count);
        Assert.AreEqual(RetrievalExpert.Graph, experts[0]);
    }

    // =========================================================================
    // 11/12/13. ShouldExecuteChannel + HasDedicatedChannel
    // =========================================================================

    [TestMethod]
    public void ShouldExecuteChannel_MandatoryRecall_AlwaysTrue()
    {
        // 即使 MandatoryOnly mask，MandatoryRecallChannel 仍执行（Mandatory Expert 永远启用）
        var mask = RetrievalExpertMask.MandatoryOnly;
        Assert.IsTrue(RetrievalExpertChannels.ShouldExecuteChannel(
            RetrievalExpertChannels.MandatoryRecallChannel, mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_ContextRecall_DisabledWhenMaskMandatoryOnly()
    {
        var mask = RetrievalExpertMask.MandatoryOnly;
        Assert.IsFalse(RetrievalExpertChannels.ShouldExecuteChannel(
            RetrievalExpertChannels.ContextRecallChannel, mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_ContextRecall_EnabledWhenLexicalEnabled()
    {
        var mask = RetrievalExpertMask.MandatoryOnly.With(RetrievalExpert.Lexical, enabled: true);
        Assert.IsTrue(RetrievalExpertChannels.ShouldExecuteChannel(
            RetrievalExpertChannels.ContextRecallChannel, mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_MemoryRecall_DisabledWhenBothMemoryExpertsOff()
    {
        var mask = RetrievalExpertMask.MandatoryOnly;
        // WorkingMemory + StableMemory 都关闭 → MemoryRecallChannel 不执行
        Assert.IsFalse(RetrievalExpertChannels.ShouldExecuteChannel(
            RetrievalExpertChannels.MemoryRecallChannel, mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_MemoryRecall_EnabledWhenWorkingMemoryOn()
    {
        var mask = RetrievalExpertMask.MandatoryOnly.With(RetrievalExpert.WorkingMemory, enabled: true);
        Assert.IsTrue(RetrievalExpertChannels.ShouldExecuteChannel(
            RetrievalExpertChannels.MemoryRecallChannel, mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_MemoryRecall_EnabledWhenStableMemoryOn()
    {
        var mask = RetrievalExpertMask.MandatoryOnly.With(RetrievalExpert.StableMemory, enabled: true);
        Assert.IsTrue(RetrievalExpertChannels.ShouldExecuteChannel(
            RetrievalExpertChannels.MemoryRecallChannel, mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_UnknownChannel_ReturnsFalse()
    {
        var mask = RetrievalExpertMask.AllEnabled;
        Assert.IsFalse(RetrievalExpertChannels.ShouldExecuteChannel("unknown_channel", mask));
    }

    [TestMethod]
    public void ShouldExecuteChannel_NullOrEmptyChannel_Throws()
    {
        var mask = RetrievalExpertMask.AllEnabled;
        Assert.ThrowsException<ArgumentException>(() =>
            RetrievalExpertChannels.ShouldExecuteChannel("", mask));
        // null 触发 ArgumentNullException（ArgumentException 子类）
        Assert.ThrowsException<ArgumentNullException>(() =>
            RetrievalExpertChannels.ShouldExecuteChannel(null!, mask));
    }

    [TestMethod]
    public void HasDedicatedChannel_Recency_ReturnsFalse()
    {
        // Recency 无独立 channel（由 WorkingMemory 副产品或 TaskState 信号提供）
        Assert.IsFalse(RetrievalExpertChannels.HasDedicatedChannel(RetrievalExpert.Recency));
    }

    [TestMethod]
    public void HasDedicatedChannel_Constraint_ReturnsFalse()
    {
        // Constraint 无独立 channel（由 Mandatory 副产品或独立 ConstraintStore 查询）
        Assert.IsFalse(RetrievalExpertChannels.HasDedicatedChannel(RetrievalExpert.Constraint));
    }

    [TestMethod]
    public void HasDedicatedChannel_Lexical_ReturnsTrue()
    {
        Assert.IsTrue(RetrievalExpertChannels.HasDedicatedChannel(RetrievalExpert.Lexical));
    }

    // =========================================================================
    // 14. 无存储 I/O（反射验证）
    // =========================================================================

    [TestMethod]
    public void Contracts_DoNotExposeStorageIO()
    {
        // 验证契约类型不继承任何 IStore 接口
        var assembly = typeof(ContextMemoryLayer).Assembly;
        var contractTypes = new[]
        {
            typeof(RetrievalExpert),
            typeof(ExpertRoutingDecision),
            typeof(ExpertRoutingDecisionSet),
            typeof(RetrievalExpertMask),
            typeof(RetrievalExpertChannels)
        };

        foreach (var type in contractTypes)
        {
            // 检查所有接口，不包含 IStore / IAsyncDisposable 等
            foreach (var iface in type.GetInterfaces())
            {
                var name = iface.Name;
                Assert.IsFalse(name.Contains("Store"),
                    $"{type.Name} 不应实现存储接口 {name}");
                Assert.IsFalse(name.Contains("Repository"),
                    $"{type.Name} 不应实现仓储接口 {name}");
            }
        }
    }

    [TestMethod]
    public void RetrievalExpertMask_IsStruct()
    {
        Assert.IsTrue(typeof(RetrievalExpertMask).IsValueType,
            "RetrievalExpertMask 应为 struct（值类型）以保证性能");
    }

    [TestMethod]
    public void ExpertRoutingDecision_IsSealedRecord()
    {
        Assert.IsTrue(typeof(ExpertRoutingDecision).IsSealed,
            "ExpertRoutingDecision 应为 sealed record 防止继承");
    }
}
