using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

/// <summary>
/// R20-2：DefaultRetrievalRouter 测试。
///
/// 验证目标：
///   1. 默认路由（mask=AllEnabled，无 bundle）→ 8 个 Expert 全部启用
///   2. Mandatory / Constraint 永远 Enabled=true（即使 mask=MandatoryOnly）
///   3. Budget 兜底（request.TokenBudget=0 时使用 bundle.Budget.DefaultTokenBudget）
///   4. Request 显式值覆盖 bundle 默认
///   5. Budget-Aware TopK 平均分配（V1 简化版）
///   6. PolicyBundle.Routing.EnabledExperts 过滤 mask
///   7. PolicyOverride.RoutingOverride 仅合并 EnableModelScoring（不替换 EnabledExperts）
///   8. PolicyOverride.BudgetOverride 仅合并 TokenBudget/TopK（不替换 ProfileId）
///   9. DisabledExpert 的 TopK/TokenBudget=0
///  10. ReasonCode 区分 mandatory / default / ablation-disabled / policy-disabled
///  11. RouterId / RouterVersion 默认值
///  12. TotalTokenBudget 反映解析后的总预算
///  13. Metadata 包含 budget 分配明细
///  14. 无 bundle 时使用 hardcoded defaults
///  15. 幂等性：相同输入产生相同输出
///  16. null request 抛出 ArgumentNullException
///  17. 契约无存储 I/O（反射验证）
/// </summary>
[TestClass]
[TestCategory("R20")]
public sealed class DefaultRetrievalRouterTests
{
    // =========================================================================
    // 1. 默认路由（mask=AllEnabled，无 bundle）
    // =========================================================================

    [TestMethod]
    public void Route_AllEnabledMaskNoBundle_EnablesAll8Experts()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 8000, topK: 50);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        Assert.AreEqual(8, decisionSet.Decisions.Count);
        foreach (var expert in new[]
        {
            RetrievalExpert.Mandatory, RetrievalExpert.Constraint,
            RetrievalExpert.Lexical, RetrievalExpert.Semantic,
            RetrievalExpert.WorkingMemory, RetrievalExpert.StableMemory,
            RetrievalExpert.Graph, RetrievalExpert.Recency
        })
        {
            Assert.IsTrue(decisionSet.IsExpertEnabled(expert),
                $"{expert} should be enabled with AllEnabled mask");
        }
    }

    [TestMethod]
    public void Route_AllEnabledMaskNoBundle_UsesHardcodedDefaults()
    {
        var router = new DefaultRetrievalRouter();
        // request.TokenBudget=0 且无 bundle → 使用 hardcoded 8000
        var request = MakeRequest(tokenBudget: 0, topK: 0);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        Assert.AreEqual(8000, decisionSet.TotalTokenBudget);
        // 非 Mandatory 启用 Expert = 6 (Lexical/Semantic/WorkingMemory/StableMemory/Graph/Recency)
        // perExpertTokenBudget = 8000 / 6 = 1333
        var lexical = decisionSet.GetDecision(RetrievalExpert.Lexical)!;
        Assert.AreEqual(1333, lexical.TokenBudget);
        // perExpertTopK = 50 / 6 = 8 (整数除法)
        Assert.AreEqual(8, lexical.TopK);
    }

    // =========================================================================
    // 2. Mandatory / Constraint 永远 Enabled=true
    // =========================================================================

    [TestMethod]
    public void Route_MandatoryOnlyMask_KeepsMandatoryAndConstraintEnabled()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 8000, topK: 50);
        var mask = RetrievalExpertMask.MandatoryOnly;

        var decisionSet = router.Route(request, mask, bundle: null);

        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Constraint));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Lexical));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.WorkingMemory));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.StableMemory));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Graph));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Recency));

        // Mandatory 候选的 ReasonCode 应为 "mandatory-always-enabled"
        var mandatory = decisionSet.GetDecision(RetrievalExpert.Mandatory)!;
        Assert.AreEqual("mandatory-always-enabled", mandatory.ReasonCode);
        Assert.IsNull(mandatory.DisabledReason);
    }

    [TestMethod]
    public void Route_DisablingMandatoryViaMask_HasNoEffect()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 8000, topK: 50);
        // 尝试禁用 Mandatory（应被忽略）
        var mask = RetrievalExpertMask.AllEnabled
            .With(RetrievalExpert.Mandatory, enabled: false)
            .With(RetrievalExpert.Constraint, enabled: false);

        var decisionSet = router.Route(request, mask, bundle: null);

        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Constraint));
    }

    // =========================================================================
    // 3. Budget 兜底（request.TokenBudget=0 时使用 bundle.Budget.DefaultTokenBudget）
    // =========================================================================

    [TestMethod]
    public void Route_ZeroRequestTokenBudget_FallsBackToBundleBudget()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 0, topK: 0);
        var bundle = MakeBundle(defaultTokenBudget: 4000, defaultTopK: 20);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle);

        Assert.AreEqual(4000, decisionSet.TotalTokenBudget);
        // 非 Mandatory 启用 Expert = 6 → perExpertTokenBudget = 4000/6 = 666
        var lexical = decisionSet.GetDecision(RetrievalExpert.Lexical)!;
        Assert.AreEqual(666, lexical.TokenBudget);
        Assert.AreEqual(3, lexical.TopK); // 20/6 = 3
    }

    // =========================================================================
    // 4. Request 显式值覆盖 bundle 默认
    // =========================================================================

    [TestMethod]
    public void Route_RequestExplicitValues_OverrideBundleDefaults()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 2000, topK: 10);
        var bundle = MakeBundle(defaultTokenBudget: 4000, defaultTopK: 20);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle);

        Assert.AreEqual(2000, decisionSet.TotalTokenBudget);
        // perExpertTokenBudget = 2000/6 = 333
        var lexical = decisionSet.GetDecision(RetrievalExpert.Lexical)!;
        Assert.AreEqual(333, lexical.TokenBudget);
        Assert.AreEqual(1, lexical.TopK); // 10/6 = 1
    }

    // =========================================================================
    // 5. Budget-Aware TopK 平均分配（V1 简化版）
    // =========================================================================

    [TestMethod]
    public void Route_AverageBudgetDistribution_AmongNonMandatoryExperts()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        // 非 Mandatory 启用 Expert = 6
        // perExpertTokenBudget = 6000/6 = 1000
        // perExpertTopK = 60/6 = 10
        foreach (var expert in new[]
        {
            RetrievalExpert.Lexical, RetrievalExpert.Semantic,
            RetrievalExpert.WorkingMemory, RetrievalExpert.StableMemory,
            RetrievalExpert.Graph, RetrievalExpert.Recency
        })
        {
            var decision = decisionSet.GetDecision(expert)!;
            Assert.AreEqual(1000, decision.TokenBudget,
                $"{expert} should get 1000 tokens (6000/6)");
            Assert.AreEqual(10, decision.TopK,
                $"{expert} should get TopK=10 (60/6)");
        }
    }

    [TestMethod]
    public void Route_MandatoryExperts_GetFullBudgetNotShared()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        // Mandatory / Constraint 不参与 budget 分配，独立占用 totalTokenBudget
        var mandatory = decisionSet.GetDecision(RetrievalExpert.Mandatory)!;
        Assert.AreEqual(6000, mandatory.TokenBudget);
        Assert.AreEqual(60, mandatory.TopK);

        var constraint = decisionSet.GetDecision(RetrievalExpert.Constraint)!;
        Assert.AreEqual(6000, constraint.TokenBudget);
        Assert.AreEqual(60, constraint.TopK);
    }

    [TestMethod]
    public void Route_FewerEnabledExperts_GetsLargerBudgetPerExpert()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // 仅启用 Lexical + Mandatory/Constraint → 非 Mandatory 启用 = 1
        var mask = RetrievalExpertMask.MandatoryOnly
            .With(RetrievalExpert.Lexical, enabled: true);

        var decisionSet = router.Route(request, mask, bundle: null);

        var lexical = decisionSet.GetDecision(RetrievalExpert.Lexical)!;
        Assert.AreEqual(6000, lexical.TokenBudget); // 全部预算归 Lexical
        Assert.AreEqual(60, lexical.TopK);

        // 其他 Expert 应被禁用
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Graph));
    }

    // =========================================================================
    // 6. PolicyBundle.Routing.EnabledExperts 过滤 mask
    // =========================================================================

    [TestMethod]
    public void Route_BundleRoutingEnabledExperts_FiltersMask()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // bundle 只允许 Lexical + Semantic（其他 Expert 应被禁用）
        var bundle = MakeBundle(
            defaultTokenBudget: 6000,
            defaultTopK: 60,
            enabledExperts: new[] { "Lexical", "Semantic" });
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle);

        // Mandatory / Constraint 永远启用
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Mandatory));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Constraint));
        // Lexical / Semantic 被 bundle 允许
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Lexical));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
        // 其他被 bundle 禁用
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.WorkingMemory));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.StableMemory));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Graph));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Recency));

        // ReasonCode 应为 "policy-disabled"
        var workingMemory = decisionSet.GetDecision(RetrievalExpert.WorkingMemory)!;
        Assert.AreEqual("policy-disabled", workingMemory.ReasonCode);
        Assert.IsNotNull(workingMemory.DisabledReason);
        Assert.IsTrue(workingMemory.DisabledReason.Contains("PolicyBundle.Routing.EnabledExperts"));
    }

    [TestMethod]
    public void Route_BundleRoutingEmptyEnabledExperts_KeepsMaskAsIs()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // bundle.EnabledExperts 为空 = 全部启用（mask 保持原样）
        var bundle = MakeBundle(
            defaultTokenBudget: 6000,
            defaultTopK: 60,
            enabledExperts: Array.Empty<string>());
        // mask 只启用 Lexical（除 Mandatory/Constraint 外）
        var mask = RetrievalExpertMask.MandatoryOnly
            .With(RetrievalExpert.Lexical, enabled: true);

        var decisionSet = router.Route(request, mask, bundle);

        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Lexical));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Graph));
    }

    [TestMethod]
    public void Route_BundleRoutingEnabledExpertsCaseInsensitive_ParsesCorrectly()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // 大小写不敏感解析
        var bundle = MakeBundle(
            defaultTokenBudget: 6000,
            defaultTopK: 60,
            enabledExperts: new[] { "lexical", "SEMANTIC" });

        var mask = RetrievalExpertMask.AllEnabled;
        var decisionSet = router.Route(request, mask, bundle);

        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Lexical));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
    }

    [TestMethod]
    public void Route_BundleRoutingInvalidExpertNames_AreIgnored()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // 包含无效名称 → 解析时忽略，不抛异常
        var bundle = MakeBundle(
            defaultTokenBudget: 6000,
            defaultTopK: 60,
            enabledExperts: new[] { "Lexical", "InvalidExpert", "Semantic" });

        var mask = RetrievalExpertMask.AllEnabled;
        var decisionSet = router.Route(request, mask, bundle);

        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Lexical));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
        // 其他被禁用（未在列表中）
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Graph));
    }

    // =========================================================================
    // 7. PolicyOverride.RoutingOverride 仅合并 EnableModelScoring（不替换 EnabledExperts）
    // =========================================================================

    [TestMethod]
    public void Route_PolicyOverrideRoutingOverride_OnlyMergesEnableModelScoring()
    {
        // P0-3 修复：RoutingOverride 不再完整替换 RoutingProfile。
        // - RequestRoutingOverride 仅暴露 EnableModelScoring 字段；
        // - EnabledExperts / ModelArtifactId 等字段保留 bundle 默认。
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(
            tokenBudget: 6000,
            topK: 60,
            routingOverride: new RequestRoutingOverride
            {
                EnableModelScoring = true
            });
        // bundle 仅允许 Lexical + Semantic（PolicyOverride 不应改变此过滤）
        var bundle = MakeBundle(
            defaultTokenBudget: 6000,
            defaultTopK: 60,
            enabledExperts: new[] { "Lexical", "Semantic" });

        var mask = RetrievalExpertMask.AllEnabled;
        var decisionSet = router.Route(request, mask, bundle);

        // bundle 的 EnabledExperts 仍生效：Lexical/Semantic 启用，Graph 禁用
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Lexical));
        Assert.IsTrue(decisionSet.IsExpertEnabled(RetrievalExpert.Semantic));
        Assert.IsFalse(decisionSet.IsExpertEnabled(RetrievalExpert.Graph));
    }

    // =========================================================================
    // 8. PolicyOverride.BudgetOverride 仅合并 TokenBudget/TopK（不替换 ProfileId）
    // =========================================================================

    [TestMethod]
    public void Route_PolicyOverrideBudgetOverride_MergesTokenBudgetAndTopK()
    {
        // P0-3 修复：BudgetOverride 不再完整替换 BudgetProfile。
        // - RequestBudgetOverride 仅暴露 TokenBudget / TopK / SectionRatios；
        // - ProfileId / StrictBudgetEnforcement 等字段保留 bundle 默认。
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(
            tokenBudget: 0,
            topK: 0,
            budgetOverride: new RequestBudgetOverride
            {
                TokenBudget = 12000,
                TopK = 100
            });
        // bundle 默认 4000/20（应被 override 的 12000/100 覆盖）
        var bundle = MakeBundle(defaultTokenBudget: 4000, defaultTopK: 20);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle);

        Assert.AreEqual(12000, decisionSet.TotalTokenBudget);
        // perExpertTokenBudget = 12000/6 = 2000
        var lexical = decisionSet.GetDecision(RetrievalExpert.Lexical)!;
        Assert.AreEqual(2000, lexical.TokenBudget);
        Assert.AreEqual(16, lexical.TopK); // 100/6 = 16
    }

    // =========================================================================
    // 9. DisabledExpert 的 TopK/TokenBudget=0
    // =========================================================================

    [TestMethod]
    public void Route_DisabledExpert_HasZeroTopKAndTokenBudget()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // 禁用 Semantic
        var mask = RetrievalExpertMask.AllEnabled
            .With(RetrievalExpert.Semantic, enabled: false);

        var decisionSet = router.Route(request, mask, bundle: null);

        var semantic = decisionSet.GetDecision(RetrievalExpert.Semantic)!;
        Assert.IsFalse(semantic.Enabled);
        Assert.AreEqual(0, semantic.TokenBudget);
        Assert.AreEqual(0, semantic.TopK);
        Assert.AreEqual("ablation-disabled", semantic.ReasonCode);
        Assert.IsNotNull(semantic.DisabledReason);
        Assert.IsTrue(semantic.DisabledReason.Contains("mask"));
    }

    // =========================================================================
    // 10. ReasonCode 区分
    // =========================================================================

    [TestMethod]
    public void Route_ReasonCode_DistinguishesMandatoryVsDefaultVsAblationDisabled()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // 仅启用 Lexical + Mandatory/Constraint
        var mask = RetrievalExpertMask.MandatoryOnly
            .With(RetrievalExpert.Lexical, enabled: true);

        var decisionSet = router.Route(request, mask, bundle: null);

        // Mandatory → "mandatory-always-enabled"
        Assert.AreEqual("mandatory-always-enabled",
            decisionSet.GetDecision(RetrievalExpert.Mandatory)!.ReasonCode);
        // Constraint → "mandatory-always-enabled"
        Assert.AreEqual("mandatory-always-enabled",
            decisionSet.GetDecision(RetrievalExpert.Constraint)!.ReasonCode);
        // Lexical 启用 → "default"
        Assert.AreEqual("default",
            decisionSet.GetDecision(RetrievalExpert.Lexical)!.ReasonCode);
        // Semantic 禁用 → "ablation-disabled"
        var semantic = decisionSet.GetDecision(RetrievalExpert.Semantic)!;
        Assert.AreEqual("ablation-disabled", semantic.ReasonCode);
    }

    [TestMethod]
    public void Route_ReasonCode_DistinguishesPolicyDisabled()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        // bundle 限制 EnabledExperts = ["Lexical"]，但 mask=AllEnabled
        // Semantic 应被标记为 "policy-disabled"
        var bundle = MakeBundle(
            defaultTokenBudget: 6000,
            defaultTopK: 60,
            enabledExperts: new[] { "Lexical" });
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle);

        var semantic = decisionSet.GetDecision(RetrievalExpert.Semantic)!;
        Assert.AreEqual("policy-disabled", semantic.ReasonCode);
    }

    // =========================================================================
    // 11. RouterId / RouterVersion 默认值
    // =========================================================================

    [TestMethod]
    public void Route_DefaultRouterIdAndVersion()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        Assert.AreEqual(DefaultRetrievalRouter.DefaultRouterId, decisionSet.RouterId);
        Assert.AreEqual(DefaultRetrievalRouter.DefaultRouterVersion, decisionSet.RouterVersion);
    }

    // =========================================================================
    // 12. TotalTokenBudget 反映解析后的总预算
    // =========================================================================

    [TestMethod]
    public void Route_TotalTokenBudget_ReflectsResolvedValue()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 12345, topK: 50);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        Assert.AreEqual(12345, decisionSet.TotalTokenBudget);
    }

    // =========================================================================
    // 13. Metadata 包含 budget 分配明细
    // =========================================================================

    [TestMethod]
    public void Route_Metadata_ContainsBudgetAllocationDetails()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        var lexical = decisionSet.GetDecision(RetrievalExpert.Lexical)!;
        Assert.IsTrue(lexical.Metadata.ContainsKey("totalTokenBudget"));
        Assert.IsTrue(lexical.Metadata.ContainsKey("totalTopK"));
        Assert.IsTrue(lexical.Metadata.ContainsKey("nonMandatoryEnabledCount"));
        Assert.IsTrue(lexical.Metadata.ContainsKey("perExpertTokenBudget"));
        Assert.IsTrue(lexical.Metadata.ContainsKey("perExpertTopK"));

        Assert.AreEqual("6000", lexical.Metadata["totalTokenBudget"]);
        Assert.AreEqual("60", lexical.Metadata["totalTopK"]);
        Assert.AreEqual("6", lexical.Metadata["nonMandatoryEnabledCount"]);
        Assert.AreEqual("1000", lexical.Metadata["perExpertTokenBudget"]);
        Assert.AreEqual("10", lexical.Metadata["perExpertTopK"]);
    }

    // =========================================================================
    // 14. 无 bundle 时使用 hardcoded defaults
    // =========================================================================

    [TestMethod]
    public void Route_NoBundleAndZeroRequest_UsesHardcodedDefaults()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 0, topK: 0);
        var mask = RetrievalExpertMask.AllEnabled;

        var decisionSet = router.Route(request, mask, bundle: null);

        // hardcoded 默认 8000 / 50
        Assert.AreEqual(8000, decisionSet.TotalTokenBudget);
        var mandatory = decisionSet.GetDecision(RetrievalExpert.Mandatory)!;
        Assert.AreEqual(50, mandatory.TopK);
    }

    // =========================================================================
    // 15. 幂等性：相同输入产生相同输出
    // =========================================================================

    [TestMethod]
    public void Route_SameInput_ProducesSameDecisions()
    {
        var router = new DefaultRetrievalRouter();
        var request = MakeRequest(tokenBudget: 6000, topK: 60);
        var mask = RetrievalExpertMask.AllEnabled
            .With(RetrievalExpert.Graph, enabled: false);

        var decisionSet1 = router.Route(request, mask, bundle: null);
        var decisionSet2 = router.Route(request, mask, bundle: null);

        Assert.AreEqual(decisionSet1.TotalTokenBudget, decisionSet2.TotalTokenBudget);
        Assert.AreEqual(decisionSet1.Decisions.Count, decisionSet2.Decisions.Count);
        for (var i = 0; i < decisionSet1.Decisions.Count; i++)
        {
            var d1 = decisionSet1.Decisions[i];
            var d2 = decisionSet2.Decisions[i];
            Assert.AreEqual(d1.Expert, d2.Expert);
            Assert.AreEqual(d1.Enabled, d2.Enabled);
            Assert.AreEqual(d1.TopK, d2.TopK);
            Assert.AreEqual(d1.TokenBudget, d2.TokenBudget);
            Assert.AreEqual(d1.ReasonCode, d2.ReasonCode);
        }
    }

    // =========================================================================
    // 16. null request 抛出 ArgumentNullException
    // =========================================================================

    [TestMethod]
    public void Route_NullRequest_ThrowsArgumentNullException()
    {
        var router = new DefaultRetrievalRouter();
        var mask = RetrievalExpertMask.AllEnabled;

        Assert.ThrowsException<ArgumentNullException>(() =>
            router.Route(null!, mask, bundle: null));
    }

    // =========================================================================
    // 17. 契约无存储 I/O（反射验证）
    // =========================================================================

    [TestMethod]
    public void IRetrievalRouter_HasNoStorageIO_MethodsAreSyncOrReturnRoutingDecisionSet()
    {
        var routerType = typeof(IRetrievalRouter);
        var routeMethod = routerType.GetMethod(nameof(IRetrievalRouter.Route));

        Assert.IsNotNull(routeMethod);
        // 返回类型为 ExpertRoutingDecisionSet（不返回 Task）→ 同步方法，无 I/O
        Assert.AreEqual(typeof(ExpertRoutingDecisionSet), routeMethod!.ReturnType);
        // 参数：request, mask, bundle, cancellationToken
        var parameters = routeMethod.GetParameters();
        Assert.AreEqual(4, parameters.Length);
        Assert.AreEqual(typeof(ContextDecisionRequest), parameters[0].ParameterType);
        Assert.AreEqual(typeof(RetrievalExpertMask), parameters[1].ParameterType);
        Assert.AreEqual(typeof(ContextPolicyBundle), parameters[2].ParameterType);
        Assert.AreEqual(typeof(CancellationToken), parameters[3].ParameterType);
    }

    [TestMethod]
    public void IRetrievalRouter_IsInterface()
    {
        Assert.IsTrue(typeof(IRetrievalRouter).IsInterface);
    }

    [TestMethod]
    public void DefaultRetrievalRouter_ImplementsIRetrievalRouter()
    {
        var router = new DefaultRetrievalRouter();
        Assert.IsInstanceOfType(router, typeof(IRetrievalRouter));
    }

    [TestMethod]
    public void DefaultRetrievalRouter_IsSealed()
    {
        Assert.IsTrue(typeof(DefaultRetrievalRouter).IsSealed);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static ContextDecisionRequest MakeRequest(
        int tokenBudget = 8000,
        int topK = 50,
        RequestBudgetOverride? budgetOverride = null,
        RequestRoutingOverride? routingOverride = null)
    {
        ContextPolicyOverride? policyOverride = null;
        if (budgetOverride is not null || routingOverride is not null)
        {
            policyOverride = new ContextPolicyOverride
            {
                BudgetOverride = budgetOverride,
                RoutingOverride = routingOverride
            };
        }

        return new ContextDecisionRequest
        {
            RequestId = "req-test-" + Guid.NewGuid().ToString("N"),
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Candidates = Array.Empty<ContextCandidateEnvelope>(),
            TokenBudget = tokenBudget,
            TopK = topK,
            PolicyOverride = policyOverride
        };
    }

    private static ContextPolicyBundle MakeBundle(
        int defaultTokenBudget = 8000,
        int defaultTopK = 50,
        string[]? enabledExperts = null)
    {
        return new ContextPolicyBundle
        {
            BundleId = "bundle-test-" + Guid.NewGuid().ToString("N"),
            Version = "2026-07/test",
            Policies = new ContextPolicySet(),
            Safety = new SafetyProfile
            {
                ProfileId = "safety-test",
                AllowDeprecatedUsedByActiveChain = true,
                AllowDuplicateReference = false
            },
            Budget = new BudgetProfile
            {
                ProfileId = "budget-test",
                DefaultTokenBudget = defaultTokenBudget,
                DefaultTopK = defaultTopK
            },
            Routing = new RoutingProfile
            {
                ProfileId = "routing-test",
                EnableModelScoring = false,
                EnabledExperts = enabledExperts ?? Array.Empty<string>()
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
