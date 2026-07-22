using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Tests;

/// <summary>
/// R19-3：Pipeline 集成 — Engine.DecideAsync 读取 PolicyBundle 测试。
///
/// 验证目标：
///   1. 无 IPolicyRegistry 时向后兼容（hardcoded defaults，与 R18-2 行为一致）
///   2. 有 IPolicyRegistry 时 Engine 从 bundle 读取 SafetyProfile（IsDuplicate / IsDeprecated 阻断）
///   3. 有 IPolicyRegistry 时 Engine 从 bundle 读取 BudgetProfile（DefaultTokenBudget / DefaultTopK 作为 request 字段为空时的兜底）
///   4. 有 IPolicyRegistry 时 Engine 从 bundle 读取 RoutingProfile（EnableModelScoring 控制 enableModel）
///   5. per-request PolicyOverride.BudgetOverride 受限调整 Budget
///   6. per-request PolicyOverride.RoutingOverride 受限调整 Routing.EnableModelScoring
///   7. ModelConfidence 低于 bundle.Routing.ModelConfidenceThreshold 时回退到 DeterministicScore（验收标准 #6）
///   8. PolicyVersion 来自 bundle.Policies.DecisionSchemaVersion
///   9. ModelVersion 来自 bundle.Routing.ModelArtifactId（当 model 启用时）
///  10. GetActiveBundleAsync 未激活时返回默认 bundle（DefaultPolicyBundleFactory.Create）
///  11. SafetyProfile 不允许 per-request override（ContextPolicyOverride 不包含 SafetyOverride 字段）
///  12. 已 blocked 候选的 BlockReasonCode 在重写时保留原值（不覆盖 adapter 预设）
///  13. PolicyBundleId 显式提供时不调用 registry（caller 已有 bundle 引用）
///  14. 默认 bundle 的 RoutingProfile.EnableModelScoring=false → 模型路径禁用
/// </summary>
[TestClass]
[TestCategory("R19")]
public sealed class DefaultContextDecisionEnginePolicyIntegrationTests
{
    // =========================================================================
    // 1. 无 IPolicyRegistry → 向后兼容（hardcoded defaults）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_NoRegistry_PreservesR18_2Behavior()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 200),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.5, tokens: 200)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(2, result.SelectedEnvelopes.Count);
        Assert.AreEqual(0, result.DroppedEnvelopes.Count);
        Assert.AreEqual(ContextDecisionPolicyVersions.DecisionSchemaV2_0, result.PolicyVersion);
        // 无 bundle → routing=null → enableModel=request.EnableModel=true
        // 但候选无 ModelScore → ModelEnabled=false
        Assert.IsFalse(result.ModelEnabled);
    }

    [TestMethod]
    public async Task DecideAsync_NoRegistry_WithModelScore_KeepsModelEnabled()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85,
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:router-v1"
                })
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true);

        var result = await engine.DecideAsync(request);

        Assert.IsTrue(result.ModelEnabled);
        Assert.AreEqual("model:router-v1", result.ModelVersion);
    }

    // =========================================================================
    // 2. 有 IPolicyRegistry → 读取 SafetyProfile
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_BundleSafetyBlocksDuplicate()
    {
        // bundle.Safety.AllowDuplicateReference=false → IsDuplicate=true 的候选被阻断
        var bundle = MakeBundle(safety: new SafetyProfile
        {
            ProfileId = "safety-no-dup",
            AllowDuplicateReference = false,
            AllowDeprecatedUsedByActiveChain = true
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("dup", ContextCandidateSource.Lexical, score: 0.9, tokens: 100,
                safety: new CandidateSafetyState { IsDuplicate = true }),
            MakeEnvelope("ok", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual("ok", result.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual(1, result.DroppedEnvelopes.Count);
        Assert.AreEqual("dup", result.DroppedEnvelopes[0].CandidateId);
        Assert.AreEqual(
            CandidateDecisionReasonCode.DuplicateSuppressed,
            result.DroppedEnvelopes[0].Safety.BlockReasonCode);
        Assert.AreEqual(1, result.Outcome.SafetyGateBlockedCount);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_BundleSafetyAllowsDuplicateWhenFlagSet()
    {
        // bundle.Safety.AllowDuplicateReference=true → IsDuplicate=true 的候选仍参与评分
        var bundle = MakeBundle(safety: new SafetyProfile
        {
            ProfileId = "safety-allow-dup",
            AllowDuplicateReference = true,
            AllowDeprecatedUsedByActiveChain = true
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("dup", ContextCandidateSource.Lexical, score: 0.9, tokens: 100,
                safety: new CandidateSafetyState { IsDuplicate = true }),
            MakeEnvelope("ok", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        // 候选保留（bundle 允许 duplicate）
        Assert.AreEqual(2, result.SelectedEnvelopes.Count);
        Assert.AreEqual(0, result.Outcome.SafetyGateBlockedCount);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_BundleSafetyBlocksDeprecatedWhenFlagFalse()
    {
        var bundle = MakeBundle(safety: new SafetyProfile
        {
            ProfileId = "safety-no-deprecated",
            AllowDeprecatedUsedByActiveChain = false,
            AllowDuplicateReference = true
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("dep", ContextCandidateSource.Lexical, score: 0.9, tokens: 100,
                safety: new CandidateSafetyState { IsDeprecatedUsedByActiveChain = true }),
            MakeEnvelope("ok", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual("ok", result.SelectedEnvelopes[0].CandidateId);
        var dropped = result.DroppedEnvelopes.Single(e => e.CandidateId == "dep");
        Assert.AreEqual(
            CandidateDecisionReasonCode.DeprecatedBlocked,
            dropped.Safety.BlockReasonCode);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_SupersededAlwaysBlockedRegardlessOfBundle()
    {
        // IsSuperseded 永远阻断（不受 bundle Allow* 字段控制）
        var bundle = MakeBundle(safety: new SafetyProfile
        {
            ProfileId = "safety-permissive",
            AllowDeprecatedUsedByActiveChain = true,
            AllowDuplicateReference = true
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("sup", ContextCandidateSource.Lexical, score: 0.9, tokens: 100,
                safety: new CandidateSafetyState { IsSuperseded = true }),
            MakeEnvelope("ok", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual("ok", result.SelectedEnvelopes[0].CandidateId);
        var dropped = result.DroppedEnvelopes.Single(e => e.CandidateId == "sup");
        Assert.AreEqual(
            CandidateDecisionReasonCode.SupersededByCurrentVersion,
            dropped.Safety.BlockReasonCode);
    }

    // =========================================================================
    // 3. 有 IPolicyRegistry → 读取 BudgetProfile（兜底）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_BundleBudgetFallbackWhenRequestTokenBudgetZero()
    {
        // bundle.Budget.DefaultTokenBudget=200，request.TokenBudget=0 → 使用 bundle 默认值
        var bundle = MakeBundle(budget: new BudgetProfile
        {
            ProfileId = "budget-200",
            DefaultTokenBudget = 200,
            DefaultTopK = 10
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 100),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.7, tokens: 100),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, score: 0.5, tokens: 100)
        };
        // TokenBudget=0 → 使用 bundle.Budget.DefaultTokenBudget=200
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 0);

        var result = await engine.DecideAsync(request);

        // 只能选 2 个（200 token）
        Assert.AreEqual(2, result.SelectedEnvelopes.Count);
        Assert.AreEqual("c1", result.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual("c2", result.SelectedEnvelopes[1].CandidateId);
        // c3 因 token 超限被丢弃
        Assert.AreEqual(1, result.Outcome.BudgetExceededCount);
        Assert.AreEqual(200, result.Outcome.TokenBudget);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_BundleTopKFallbackWhenRequestTopKZero()
    {
        // bundle.Budget.DefaultTopK=2，request.TopK=0 → 使用 bundle 默认值
        var bundle = MakeBundle(budget: new BudgetProfile
        {
            ProfileId = "budget-topk-2",
            DefaultTokenBudget = 1000,
            DefaultTopK = 2
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 100),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.7, tokens: 100),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, score: 0.5, tokens: 100)
        };
        // TopK=0 → 使用 bundle.Budget.DefaultTopK=2
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 1000);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(2, result.SelectedEnvelopes.Count);
        Assert.AreEqual(1, result.Outcome.BudgetExceededCount);
        Assert.AreEqual(
            CandidateDecisionReasonCode.SectionQuotaExceeded,
            result.DroppedEnvelopes[0].Safety.BlockReasonCode);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_RequestTokenBudgetOverridesBundle()
    {
        // bundle.Budget.DefaultTokenBudget=2000，但 request.TokenBudget=300 显式指定 → 使用 300
        var bundle = MakeBundle(budget: new BudgetProfile
        {
            ProfileId = "budget-2000",
            DefaultTokenBudget = 2000,
            DefaultTopK = 10
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 200),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.5, tokens: 200)
        };
        // request.TokenBudget=300 → 覆盖 bundle 默认值 2000
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 300);

        var result = await engine.DecideAsync(request);

        // 只能选 1 个（200 token）
        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual(300, result.Outcome.TokenBudget);
    }

    // =========================================================================
    // 4. 有 IPolicyRegistry → 读取 RoutingProfile（EnableModelScoring）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_RoutingEnableModelFalse_DisablesModel()
    {
        // bundle.Routing.EnableModelScoring=false → 即使 request.EnableModel=true 也禁用
        var bundle = MakeBundle(routing: new RoutingProfile
        {
            ProfileId = "routing-no-model",
            EnableModelScoring = false,
            ModelArtifactId = null,
            ModelConfidenceThreshold = 0.70
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85,
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:v1"
                })
        };
        // request.EnableModel=true，但 bundle.Routing.EnableModelScoring=false
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true);

        var result = await engine.DecideAsync(request);

        Assert.IsFalse(result.ModelEnabled);
        Assert.IsNull(result.ModelVersion);
        // 候选回退到 deterministic
        Assert.AreEqual(0.8, result.SelectedEnvelopes[0].Utility.FinalScore);
        Assert.IsNull(result.SelectedEnvelopes[0].Utility.ModelScore);
        Assert.AreEqual(0, result.SelectedEnvelopes[0].Utility.ModelConfidence);
        Assert.AreEqual("fallback-to-deterministic", result.SelectedEnvelopes[0].Utility.ReasonCode);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_RoutingEnableModelTrue_KeepsModelScore()
    {
        // bundle.Routing.EnableModelScoring=true + ModelConfidence 高于阈值 → 保留 ModelScore
        var bundle = MakeBundle(routing: new RoutingProfile
        {
            ProfileId = "routing-with-model",
            EnableModelScoring = true,
            ModelArtifactId = "router-v2",
            ModelConfidenceThreshold = 0.70,
            DeterministicWeight = 1.0,
            ModelWeight = 0.0
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85, // > 0.70 阈值
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:v1"
                })
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true);

        var result = await engine.DecideAsync(request);

        Assert.IsTrue(result.ModelEnabled);
        // ModelVersion 来自 bundle.Routing.ModelArtifactId
        Assert.AreEqual("router-v2", result.ModelVersion);
        // 候选保留 ModelScore（FinalScore 不被重写）
        Assert.AreEqual(0.875, result.SelectedEnvelopes[0].Utility.FinalScore);
    }

    // =========================================================================
    // 5. ModelConfidence 低于阈值 → 回退到 DeterministicScore（验收标准 #6）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_LowModelConfidence_FallsBackToDeterministic()
    {
        // bundle.Routing.ModelConfidenceThreshold=0.90，候选 ModelConfidence=0.85 → 回退
        var bundle = MakeBundle(routing: new RoutingProfile
        {
            ProfileId = "routing-strict",
            EnableModelScoring = true,
            ModelArtifactId = "router-v2",
            ModelConfidenceThreshold = 0.90
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85, // < 0.90 阈值 → 回退
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:v1"
                })
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true);

        var result = await engine.DecideAsync(request);

        // 模型未启用（候选已回退）
        Assert.IsFalse(result.ModelEnabled);
        Assert.IsNull(result.ModelVersion);
        // 候选 FinalScore=DeterministicScore，ModelScore=null
        Assert.AreEqual(0.8, result.SelectedEnvelopes[0].Utility.FinalScore);
        Assert.IsNull(result.SelectedEnvelopes[0].Utility.ModelScore);
        Assert.AreEqual(0, result.SelectedEnvelopes[0].Utility.ModelConfidence);
        Assert.AreEqual("fallback-to-deterministic", result.SelectedEnvelopes[0].Utility.ReasonCode);
    }

    // =========================================================================
    // 6. per-request PolicyOverride → BudgetOverride
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_PolicyOverride_BudgetOverrideTakesPrecedence()
    {
        // bundle.Budget.DefaultTokenBudget=2000，但 PolicyOverride.BudgetOverride.DefaultTokenBudget=300
        var bundle = MakeBundle(budget: new BudgetProfile
        {
            ProfileId = "budget-bundle",
            DefaultTokenBudget = 2000,
            DefaultTopK = 10
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 200),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.7, tokens: 200)
        };
        // request.TokenBudget=0 → 用 override 的 300（不是 bundle 的 2000）
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 0,
            policyOverride: new ContextPolicyOverride
            {
                BundleId = bundle.BundleId,
                // P0-3 修复：使用 RequestBudgetOverride（仅允许 TokenBudget/TopK/SectionRatios）
                BudgetOverride = new RequestBudgetOverride
                {
                    TokenBudget = 300,
                    TopK = 10
                }
            });

        var result = await engine.DecideAsync(request);

        // 只能选 1 个（300 token）
        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual(300, result.Outcome.TokenBudget);
    }

    // =========================================================================
    // 7. per-request PolicyOverride → RoutingOverride.EnableModelScoring
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_PolicyOverride_RoutingOverrideEnablesModel()
    {
        // bundle.Routing.EnableModelScoring=false，但 PolicyOverride.RoutingOverride.EnableModelScoring=true
        var bundle = MakeBundle(routing: new RoutingProfile
        {
            ProfileId = "routing-bundle-no-model",
            EnableModelScoring = false,
            ModelArtifactId = "router-v2",
            ModelConfidenceThreshold = 0.70
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85,
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:v1"
                })
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true,
            policyOverride: new ContextPolicyOverride
            {
                BundleId = bundle.BundleId,
                // P0-3 修复：使用 RequestRoutingOverride（仅允许 EnableModelScoring）
                // bundle.Routing.ModelArtifactId 等字段不可被 Request 替换
                RoutingOverride = new RequestRoutingOverride { EnableModelScoring = true }
            });

        var result = await engine.DecideAsync(request);

        // 模型启用（被 override）
        Assert.IsTrue(result.ModelEnabled);
        Assert.AreEqual("router-v2", result.ModelVersion);
    }

    // =========================================================================
    // 8. PolicyVersion 来自 bundle.Policies.DecisionSchemaVersion
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_PolicyVersionFromBundle()
    {
        var bundle = MakeBundle(policies: new ContextPolicySet
        {
            DecisionSchemaVersion = "test-schema/9.9",
            PackagePolicyVersion = ContextDecisionPolicyVersions.PackagePolicyV3_1,
            RetrievalPolicyVersion = ContextDecisionPolicyVersions.RetrievalPolicyV4_0,
            RelationProfileVersion = ContextDecisionPolicyVersions.RelationProfileV2_0,
            QualityContractVersion = ContextDecisionPolicyVersions.QualityContractV1_0
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual("test-schema/9.9", result.PolicyVersion);
    }

    // =========================================================================
    // 9. ModelVersion 来自 bundle.Routing.ModelArtifactId
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_ModelVersionFromBundleRouting()
    {
        var bundle = MakeBundle(routing: new RoutingProfile
        {
            ProfileId = "routing-with-artifact",
            EnableModelScoring = true,
            ModelArtifactId = "router-bundle-artifact",
            ModelConfidenceThreshold = 0.70
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85,
                    ReasonCode = "model-weighted",
                    // 候选自己的 ModelArtifactRef，bundle.Routing.ModelArtifactId 优先
                    ModelArtifactRef = "model:candidate-artifact"
                })
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true);

        var result = await engine.DecideAsync(request);

        Assert.IsTrue(result.ModelEnabled);
        // ModelVersion 来自 bundle.Routing.ModelArtifactId（优先于候选 ModelArtifactRef）
        Assert.AreEqual("router-bundle-artifact", result.ModelVersion);
    }

    // =========================================================================
    // 10. GetActiveBundleAsync 未激活时返回默认 bundle
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithDefaultRegistry_NoActivation_UsesDefaultBundle()
    {
        // 未注册任何 bundle，未激活 → DefaultPolicyRegistry 返回 DefaultPolicyBundleFactory.Create()
        var registry = new DefaultPolicyRegistry();

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 100),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.5, tokens: 100)
        };
        // request.TokenBudget=0, TopK=0 → 使用默认 bundle 的 DefaultTokenBudget=8000, DefaultTopK=50
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 0);

        var result = await engine.DecideAsync(request);

        // 默认 bundle 默认 TokenBudget=8000，足够容纳 2 个候选
        Assert.AreEqual(2, result.SelectedEnvelopes.Count);
        // 默认 bundle RoutingProfile.EnableModelScoring=false → 模型路径禁用
        Assert.IsFalse(result.ModelEnabled);
        // 默认 bundle Policies 字段对齐 ContextDecisionPolicyVersions
        Assert.AreEqual(ContextDecisionPolicyVersions.DecisionSchemaV2_0, result.PolicyVersion);
        Assert.AreEqual(8000, result.Outcome.TokenBudget);
    }

    // =========================================================================
    // 11. PolicyBundleId 显式提供时不调用 GetActiveBundleAsync（P0-2：改走 GetBundleAsync）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_WithRegistry_PolicyBundleIdProvided_SkipsRegistry()
    {
        // 自定义计数 registry：GetActiveBundleAsync 调用计数
        // P0-2 修复：PolicyBundleId 显式提供时，Engine 通过 GetBundleAsync 精确加载
        // （不再静默回退默认 Bundle）。
        var bundle = MakeBundle(bundleId: "bundle-explicit-id");
        var countingRegistry = new CountingPolicyRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(countingRegistry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        // request.PolicyBundleId 显式提供 → 跳过 GetActiveBundleAsync，改调 GetBundleAsync
        var request = new ContextDecisionRequest
        {
            RequestId = "req-test-explicit",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Candidates = candidates,
            TokenBudget = 1000,
            EnableModel = true,
            PolicyBundleId = "bundle-explicit-id"
        };

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(0, countingRegistry.GetActiveBundleCallCount,
            "GetActiveBundleAsync 不应被调用");
        // P0-2：显式 BundleId 解析成功 → 使用该 bundle（而非 hardcoded defaults）
        Assert.AreEqual(ContextDecisionPolicyVersions.DecisionSchemaV2_0, result.PolicyVersion);
    }

    [TestMethod]
    public async Task DecideAsync_WithRegistry_PolicyBundleIdProvided_ButNotFound_FailsClosed()
    {
        // P0-2 fail-closed 契约：当 PolicyBundleId 显式提供但 GetBundleAsync 返回 null 时，
        // Engine 不允许静默回退默认 Bundle，必须抛 InvalidOperationException。
        var countingRegistry = new CountingPolicyRegistry(MakeBundle(bundleId: "bundle-real"));
        var engine = new DefaultContextDecisionEngine(countingRegistry);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = new ContextDecisionRequest
        {
            RequestId = "req-test-missing",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Candidates = candidates,
            TokenBudget = 1000,
            EnableModel = true,
            PolicyBundleId = "bundle-nonexistent"
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => engine.DecideAsync(request));
    }

    // =========================================================================
    // 12. 已 blocked 候选保留 adapter 预设的 BlockReasonCode（不被 bundle 覆盖）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_PreBlockedCandidate_KeepsAdapterReasonCode()
    {
        // 候选 PassesSafetyGate=false + 自定义 BlockReasonCode → Engine 信任之
        var bundle = MakeBundle(safety: new SafetyProfile
        {
            ProfileId = "safety-test",
            AllowDuplicateReference = true,
            AllowDeprecatedUsedByActiveChain = true
        });
        var registry = MakeRegistry(bundle);

        var engine = new DefaultContextDecisionEngine(registry);
        var candidates = new[]
        {
            MakeEnvelope("blocked", ContextCandidateSource.Lexical, score: 0.9, tokens: 100,
                safety: new CandidateSafetyState
                {
                    PassesSafetyGate = false,
                    BlockReasonCode = CandidateDecisionReasonCode.LifecycleBlocked,
                    BlockReasonDetail = "frozen by admin"
                }),
            MakeEnvelope("ok", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        var dropped = result.DroppedEnvelopes.Single(e => e.CandidateId == "blocked");
        // 保留 adapter 预设的 BlockReasonCode（不被 bundle 改写为 Unknown 或其他）
        Assert.AreEqual(
            CandidateDecisionReasonCode.LifecycleBlocked,
            dropped.Safety.BlockReasonCode);
        Assert.AreEqual("frozen by admin", dropped.Safety.BlockReasonDetail);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static ContextPolicyBundle MakeBundle(
        string bundleId = "bundle-test",
        SafetyProfile? safety = null,
        BudgetProfile? budget = null,
        RoutingProfile? routing = null,
        ContextPolicySet? policies = null)
    {
        return new ContextPolicyBundle
        {
            BundleId = bundleId,
            Version = "2026-07/test",
            Policies = policies ?? new ContextPolicySet(),
            Safety = safety ?? new SafetyProfile
            {
                ProfileId = "safety-test",
                AllowDeprecatedUsedByActiveChain = true,
                AllowDuplicateReference = true
            },
            Budget = budget ?? new BudgetProfile
            {
                ProfileId = "budget-test",
                DefaultTokenBudget = 1000,
                DefaultTopK = 10
            },
            Routing = routing ?? new RoutingProfile
            {
                ProfileId = "routing-test",
                EnableModelScoring = false,
                ModelConfidenceThreshold = 0.70
            },
            CreatedAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)
        };
    }

    private static DefaultPolicyRegistry MakeRegistry(ContextPolicyBundle bundle)
    {
        var registry = new DefaultPolicyRegistry();
        registry.RegisterBundleAsync(bundle).Wait();
        registry.TryActivateAsync(new PolicyActivation
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            BundleId = bundle.BundleId,
            BundleVersion = bundle.Version,
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivatedBy = "test"
        }, expectedEpoch: 0).Wait();
        return registry;
    }

    private static ContextDecisionRequest MakeRequest(
        ContextDecisionSource source,
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int tokenBudget = 1000,
        int topK = 0,
        bool enableModel = true,
        ContextPolicyOverride? policyOverride = null,
        string workspaceId = "ws-test",
        string collectionId = "col-test")
    {
        return new ContextDecisionRequest
        {
            RequestId = "req-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            DecisionSource = source,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Candidates = candidates,
            TokenBudget = tokenBudget,
            TopK = topK,
            EnableModel = enableModel,
            PolicyOverride = policyOverride
        };
    }

    private static ContextCandidateEnvelope MakeEnvelope(
        string candidateId,
        ContextCandidateSource source,
        double score,
        int tokens,
        CandidateSafetyState? safety = null,
        CandidateUtilityScore? utility = null)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            Source = source,
            EstimatedTokens = tokens,
            Safety = safety ?? new CandidateSafetyState(),
            Utility = utility ?? new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ReasonCode = "deterministic-only"
            }
        };
    }

    /// <summary>计数 PolicyRegistry：用于验证 GetActiveBundleAsync 调用次数。</summary>
    private sealed class CountingPolicyRegistry : IPolicyRegistry
    {
        private readonly ContextPolicyBundle _bundle;
        private int _getActiveBundleCallCount;

        public CountingPolicyRegistry(ContextPolicyBundle bundle)
        {
            _bundle = bundle;
        }

        public int GetActiveBundleCallCount => _getActiveBundleCallCount;

        public Task<ContextPolicyBundle> GetActiveBundleAsync(
            string workspaceId, string collectionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getActiveBundleCallCount);
            return Task.FromResult(_bundle);
        }

        public Task<PolicyActivation?> GetActivationAsync(
            string workspaceId, string collectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<PolicyActivation?>(null);

        public Task<IReadOnlyList<ContextPolicyBundle>> ListBundlesAsync(
            bool includeSuperseded = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextPolicyBundle>>(new[] { _bundle });

        public Task RegisterBundleAsync(
            ContextPolicyBundle bundle, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ActivateAsync(
            PolicyActivation activation, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // P0-2：精确加载 bundle；此 stub 未追踪多版本 bundle，故仅在 BundleId 匹配时返回。
        public Task<ContextPolicyBundle?> GetBundleAsync(
            string bundleId, string? version, CancellationToken cancellationToken = default)
            => Task.FromResult<ContextPolicyBundle?>(
                string.Equals(_bundle.BundleId, bundleId, StringComparison.Ordinal) ? _bundle : null);

        // P0-4：CAS 激活；此 stub 不维护 epoch，无竞争场景下总是成功。
        public Task<bool> TryActivateAsync(
            PolicyActivation next, long expectedEpoch, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
