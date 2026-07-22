using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Policy;
using System.Linq;

namespace ContextCore.Tests;

/// <summary>
/// R19-2：默认策略包注册表 + 默认 bundle 工厂验证。
///
/// 验证目标：
///   1. DefaultPolicyBundleFactory.Create 返回非 null bundle 且各字段填充正确
///   2. 默认 bundle 的 Policies 字段对齐 ContextDecisionPolicyVersions 5 个常量
///   3. 默认 bundle 的 Safety/Budget/Routing profile 默认值合理
///   4. 默认 bundle 的 SectionRatios 包含 5 个 section 且比例之和 = 1.0
///   5. DefaultPolicyRegistry 未激活时返回默认 bundle
///   6. DefaultPolicyRegistry Register + Activate + GetActiveBundleAsync 往返一致
///   7. ListBundlesAsync 默认过滤 superseded bundle
///   8. GetActivationAsync 未激活返回 null
///   9. 线程安全（并发 Register 不报错）
///  10. DefaultPolicyBundleFactory.Create 幂等：多次调用创建独立实例
/// </summary>
[TestClass]
[TestCategory("R19")]
public sealed class DefaultPolicyRegistryTests
{
    // =========================================================================
    // 1. DefaultPolicyBundleFactory.Create
    // =========================================================================

    [TestMethod]
    public void Create_ReturnsBundleWithDefaultIdAndVersion()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        Assert.AreEqual(DefaultPolicyBundleFactory.DefaultBundleId, bundle.BundleId);
        Assert.AreEqual(DefaultPolicyBundleFactory.DefaultBundleVersion, bundle.Version);
        Assert.IsFalse(bundle.IsSuperseded);
        Assert.IsNull(bundle.SupersededByBundleId);
    }

    [TestMethod]
    public void Create_DefaultPolicies_AlignWithContextDecisionPolicyVersions()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        Assert.AreEqual(ContextDecisionPolicyVersions.DecisionSchemaV2_0, bundle.Policies.DecisionSchemaVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.PackagePolicyV3_1, bundle.Policies.PackagePolicyVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.RetrievalPolicyV4_0, bundle.Policies.RetrievalPolicyVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.RelationProfileV2_0, bundle.Policies.RelationProfileVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.QualityContractV1_0, bundle.Policies.QualityContractVersion);
    }

    // =========================================================================
    // 2. 默认 Safety / Budget / Routing profile
    // =========================================================================

    [TestMethod]
    public void Create_DefaultSafetyProfile_AllowsDeprecatedButNotDuplicates()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        Assert.IsTrue(bundle.Safety.AllowDeprecatedUsedByActiveChain);
        Assert.IsFalse(bundle.Safety.AllowDuplicateReference);
        Assert.AreEqual(0, bundle.Safety.RequiredTags.Count);
        Assert.AreEqual(0, bundle.Safety.ForbiddenTags.Count);
    }

    [TestMethod]
    public void Create_DefaultBudgetProfile_Has8000TokenAnd50TopK()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        Assert.AreEqual(8000, bundle.Budget.DefaultTokenBudget);
        Assert.AreEqual(50, bundle.Budget.DefaultTopK);
        Assert.IsTrue(bundle.Budget.StrictBudgetEnforcement);
    }

    [TestMethod]
    public void Create_DefaultRoutingProfile_IsDeterministicOnly()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        Assert.IsFalse(bundle.Routing.EnableModelScoring);
        Assert.IsNull(bundle.Routing.ModelArtifactId);
        Assert.AreEqual(1.0, bundle.Routing.DeterministicWeight);
        Assert.AreEqual(0.0, bundle.Routing.ModelWeight);
        Assert.AreEqual(0.70, bundle.Routing.ModelConfidenceThreshold);
        Assert.AreEqual(0, bundle.Routing.EnabledExperts.Count);
    }

    [TestMethod]
    public void Create_DefaultBundle_HasNoModelArtifacts()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        Assert.AreEqual(0, bundle.ModelArtifacts.Count);
        Assert.IsNull(bundle.Rollout);
    }

    // =========================================================================
    // 3. SectionRatios 比例之和 = 1.0
    // =========================================================================

    [TestMethod]
    public void Create_DefaultSectionRatios_SumToOne()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        var ratios = bundle.Budget.SectionRatios;
        Assert.AreEqual(5, ratios.Count);

        var sum = ratios.Values.Sum();
        Assert.AreEqual(1.0, sum, 0.0001); // 容差 0.0001
    }

    [TestMethod]
    public void Create_DefaultSectionRatios_ContainsAll5Sections()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        var ratios = bundle.Budget.SectionRatios;
        Assert.IsTrue(ratios.ContainsKey("working_memory"));
        Assert.IsTrue(ratios.ContainsKey("recent_context"));
        Assert.IsTrue(ratios.ContainsKey("related_context"));
        Assert.IsTrue(ratios.ContainsKey("stable_memory"));
        Assert.IsTrue(ratios.ContainsKey("global_context"));
    }

    [TestMethod]
    public void Create_DefaultSectionRatios_WorkingMemoryHasHighestRatio()
    {
        var bundle = DefaultPolicyBundleFactory.Create();

        var ratios = bundle.Budget.SectionRatios;
        var maxRatio = ratios.Values.Max();
        Assert.AreEqual(ratios["working_memory"], maxRatio);
        Assert.AreEqual(0.30, ratios["working_memory"]);
    }

    // =========================================================================
    // 4. DefaultPolicyRegistry — 未激活返回默认 bundle
    // =========================================================================

    [TestMethod]
    public async Task GetActiveBundleAsync_Unmapped_ReturnsDefaultBundle()
    {
        var registry = new DefaultPolicyRegistry();

        var bundle = await registry.GetActiveBundleAsync("ws-unmapped", "col-unmapped");

        Assert.IsNotNull(bundle);
        Assert.AreEqual(DefaultPolicyBundleFactory.DefaultBundleId, bundle.BundleId);
    }

    // =========================================================================
    // 5. Register + Activate + GetActiveBundleAsync 往返
    // =========================================================================

    [TestMethod]
    public async Task RegisterActivateGet_RoundTripReturnsRegisteredBundle()
    {
        var registry = new DefaultPolicyRegistry();
        var customBundle = new ContextPolicyBundle
        {
            BundleId = "bundle-custom-1",
            Version = "1.0.0"
        };

        await registry.RegisterBundleAsync(customBundle);

        var activation = new PolicyActivation
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            BundleId = "bundle-custom-1",
            BundleVersion = "1.0.0",
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow
        };
        Assert.IsTrue(await registry.TryActivateAsync(activation, expectedEpoch: 0));

        var retrievedBundle = await registry.GetActiveBundleAsync("ws-1", "col-1");
        Assert.AreEqual("bundle-custom-1", retrievedBundle.BundleId);

        var retrievedActivation = await registry.GetActivationAsync("ws-1", "col-1");
        Assert.IsNotNull(retrievedActivation);
        Assert.AreEqual("bundle-custom-1", retrievedActivation.BundleId);
    }

    [TestMethod]
    public async Task TryActivateAsync_WithBundleOverride_PreservesOverride()
    {
        var registry = new DefaultPolicyRegistry();
        await registry.RegisterBundleAsync(new ContextPolicyBundle
        {
            BundleId = "bundle-override-test",
            Version = "1.0.0"
        });

        var activation = new PolicyActivation
        {
            WorkspaceId = "ws-2",
            CollectionId = "col-2",
            BundleId = "bundle-override-test",
            BundleVersion = "1.0.0",
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow,
            // P1-4：override 使用受限类型 RequestBudgetOverride / RequestRoutingOverride。
            BudgetOverride = new RequestBudgetOverride
            {
                TokenBudget = 2000,
                TopK = 10
            },
            RoutingOverride = new RequestRoutingOverride
            {
                EnableModelScoring = true
            }
        };
        Assert.IsTrue(await registry.TryActivateAsync(activation, expectedEpoch: 0));

        var retrievedActivation = await registry.GetActivationAsync("ws-2", "col-2");
        Assert.IsNotNull(retrievedActivation);
        Assert.IsNotNull(retrievedActivation.BudgetOverride);
        Assert.AreEqual(2000, retrievedActivation.BudgetOverride.TokenBudget);
        Assert.IsNotNull(retrievedActivation.RoutingOverride);
        Assert.IsTrue(retrievedActivation.RoutingOverride.EnableModelScoring == true);
    }

    // =========================================================================
    // 6. ListBundlesAsync 过滤 superseded
    // =========================================================================

    [TestMethod]
    public async Task ListBundlesAsync_DefaultExcludesSuperseded()
    {
        var registry = new DefaultPolicyRegistry();
        await registry.RegisterBundleAsync(new ContextPolicyBundle
        {
            BundleId = "bundle-old",
            Version = "1.0.0",
            SupersededAt = DateTimeOffset.UtcNow,
            SupersededByBundleId = "bundle-new"
        });
        await registry.RegisterBundleAsync(new ContextPolicyBundle
        {
            BundleId = "bundle-new",
            Version = "2.0.0"
        });

        var activeOnly = await registry.ListBundlesAsync(includeSuperseded: false);
        var all = await registry.ListBundlesAsync(includeSuperseded: true);

        Assert.AreEqual(1, activeOnly.Count);
        Assert.AreEqual("bundle-new", activeOnly[0].BundleId);
        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public async Task ListBundlesAsync_ReturnsBundlesSortedById()
    {
        var registry = new DefaultPolicyRegistry();
        await registry.RegisterBundleAsync(new ContextPolicyBundle { BundleId = "z-bundle", Version = "1.0" });
        await registry.RegisterBundleAsync(new ContextPolicyBundle { BundleId = "a-bundle", Version = "1.0" });
        await registry.RegisterBundleAsync(new ContextPolicyBundle { BundleId = "m-bundle", Version = "1.0" });

        var bundles = await registry.ListBundlesAsync();

        Assert.AreEqual(3, bundles.Count);
        Assert.AreEqual("a-bundle", bundles[0].BundleId);
        Assert.AreEqual("m-bundle", bundles[1].BundleId);
        Assert.AreEqual("z-bundle", bundles[2].BundleId);
    }

    // =========================================================================
    // 7. GetActivationAsync 未激活返回 null
    // =========================================================================

    [TestMethod]
    public async Task GetActivationAsync_Unmapped_ReturnsNull()
    {
        var registry = new DefaultPolicyRegistry();

        var activation = await registry.GetActivationAsync("ws-unmapped", "col-unmapped");

        Assert.IsNull(activation);
    }

    // =========================================================================
    // 8. RegisterBundleAsync — P0-4：Bundle 全局不可变（相同 BundleId+Version 重复注册抛异常）
    // =========================================================================

    [TestMethod]
    public async Task RegisterBundleAsync_SameIdAndVersionTwice_Throws()
    {
        // P0-4 修复：insert-if-absent 语义。相同 (BundleId, Version) 已存在时抛 InvalidOperationException，
        // 不再静默覆盖。Bundle 全局不可变；supersede 通过新建 bundle 实现。
        var registry = new DefaultPolicyRegistry();
        var original = new ContextPolicyBundle { BundleId = "bundle-1", Version = "1.0.0" };
        var duplicate = new ContextPolicyBundle { BundleId = "bundle-1", Version = "1.0.0" };

        await registry.RegisterBundleAsync(original);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => registry.RegisterBundleAsync(duplicate));

        // 原始 bundle 未被覆盖
        var bundles = await registry.ListBundlesAsync();
        Assert.AreEqual(1, bundles.Count);
        Assert.AreEqual("1.0.0", bundles[0].Version);
    }

    [TestMethod]
    public async Task RegisterBundleAsync_SameIdDifferentVersion_RegistersBoth()
    {
        // P0-4：(BundleId, Version) 为复合主键。同一 BundleId 不同 Version 视为不同 bundle，
        // 都允许注册（用于支持 supersede：新版本注册后激活，旧版本保留为历史）。
        var registry = new DefaultPolicyRegistry();
        var v1 = new ContextPolicyBundle { BundleId = "bundle-1", Version = "1.0.0" };
        var v2 = new ContextPolicyBundle { BundleId = "bundle-1", Version = "2.0.0" };

        await registry.RegisterBundleAsync(v1);
        await registry.RegisterBundleAsync(v2);

        var bundles = await registry.ListBundlesAsync(includeSuperseded: true);
        Assert.AreEqual(2, bundles.Count);
        CollectionAssert.AreEquivalent(
            new[] { "1.0.0", "2.0.0" },
            bundles.Select(b => b.Version).ToArray());
    }

    // =========================================================================
    // 9. Create 幂等：多次调用创建独立实例
    // =========================================================================

    [TestMethod]
    public void Create_CalledMultipleTimes_ReturnsIndependentInstances()
    {
        var bundle1 = DefaultPolicyBundleFactory.Create();
        var bundle2 = DefaultPolicyBundleFactory.Create();

        Assert.AreNotSame(bundle1, bundle2);
        Assert.AreEqual(bundle1.BundleId, bundle2.BundleId);
        Assert.AreEqual(bundle1.Version, bundle2.Version);
    }

    [TestMethod]
    public void Create_EachCallProducesNewSectionRatiosDictionary()
    {
        var bundle1 = DefaultPolicyBundleFactory.Create();
        var bundle2 = DefaultPolicyBundleFactory.Create();

        Assert.AreNotSame(bundle1.Budget.SectionRatios, bundle2.Budget.SectionRatios);
    }

    // =========================================================================
    // 10. 线程安全（并发 Register 不报错）
    // =========================================================================

    [TestMethod]
    public async Task RegisterBundleAsync_ConcurrentRegisters_AllSucceed()
    {
        var registry = new DefaultPolicyRegistry();
        var tasks = Enumerable.Range(0, 20).Select(i => registry.RegisterBundleAsync(
            new ContextPolicyBundle { BundleId = $"bundle-concurrent-{i}", Version = "1.0" }));

        await Task.WhenAll(tasks);

        var bundles = await registry.ListBundlesAsync();
        Assert.AreEqual(20, bundles.Count);
    }

    [TestMethod]
    public async Task TryActivateAsync_ConcurrentActivations_AllSucceed()
    {
        var registry = new DefaultPolicyRegistry();
        // 先注册 bundle
        await registry.RegisterBundleAsync(new ContextPolicyBundle
        {
            BundleId = "bundle-concurrent-activation",
            Version = "1.0"
        });

        // 并发激活到不同 ws/col（首次激活，expectedEpoch: 0）
        var tasks = Enumerable.Range(0, 10).Select(i => registry.TryActivateAsync(
            new PolicyActivation
            {
                WorkspaceId = $"ws-{i}",
                CollectionId = $"col-{i}",
                BundleId = "bundle-concurrent-activation",
                BundleVersion = "1.0",
                BundleContentHash = "sha256:test",
                ActivatedAt = DateTimeOffset.UtcNow
            }, expectedEpoch: 0));

        var results = await Task.WhenAll(tasks);
        Assert.IsTrue(results.All(r => r), "All concurrent first-time activations should succeed via CAS.");

        // 验证每个 ws/col 都能查到
        for (var i = 0; i < 10; i++)
        {
            var activation = await registry.GetActivationAsync($"ws-{i}", $"col-{i}");
            Assert.IsNotNull(activation);
            Assert.AreEqual("bundle-concurrent-activation", activation.BundleId);
        }
    }

    // =========================================================================
    // 11. GetDefaultBundle（internal 方法）返回与 Create 相同的 bundle
    // =========================================================================

    [TestMethod]
    public void GetDefaultBundle_ReturnsBundleWithDefaultId()
    {
        var registry = new DefaultPolicyRegistry();

        var defaultBundle = registry.GetDefaultBundle();

        Assert.AreEqual(DefaultPolicyBundleFactory.DefaultBundleId, defaultBundle.BundleId);
    }

    // =========================================================================
    // 12. 未激活的 ws/col 同时使用默认 bundle + 自定义 bundle 隔离
    // =========================================================================

    [TestMethod]
    public async Task GetActiveBundleAsync_IsolatedByWorkspaceCollection()
    {
        var registry = new DefaultPolicyRegistry();
        await registry.RegisterBundleAsync(new ContextPolicyBundle
        {
            BundleId = "bundle-ws1-col1",
            Version = "1.0"
        });
        Assert.IsTrue(await registry.TryActivateAsync(new PolicyActivation
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            BundleId = "bundle-ws1-col1",
            BundleVersion = "1.0",
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow
        }, expectedEpoch: 0));

        var ws1Bundle = await registry.GetActiveBundleAsync("ws-1", "col-1");
        var ws2Bundle = await registry.GetActiveBundleAsync("ws-2", "col-2");

        Assert.AreEqual("bundle-ws1-col1", ws1Bundle.BundleId);
        // ws-2/col-2 未激活 → 返回默认 bundle
        Assert.AreEqual(DefaultPolicyBundleFactory.DefaultBundleId, ws2Bundle.BundleId);
    }
}
