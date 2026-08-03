using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// 策略包契约可实施性验证。
///
/// 验证目标：
/// 1. ContextPolicySet 默认值对齐 ContextDecisionPolicyVersions 5 个常量
/// 2. ModelArtifactReference 复用 LearningLoopContracts.ModelArtifactStatus（不重复定义）
/// 3. 3 个 Profile（Safety/Budget/Routing）可独立构造 + with 表达式
/// 4. RolloutPolicy 复用 EvolutionContracts.RollbackCondition
/// 5. ContextPolicyBundle 不可变性 + IsSuperseded 判定
/// 6. PolicyActivation 按 workspace/collection 作用域
/// 7. ContextPolicyOverride.IsCompliant 基础校验
/// 8. IPolicyRegistry 接口契约（方法签名 + 返回类型）
/// 9. 不引入存储 I/O（反射检查无 SaveAsync / PersistAsync 等存储方法）
/// 10. 5 个版本字段不允许 per-request override（用户澄清）
/// </summary>
[TestClass]
[TestCategory("R19")]
public sealed class PolicyBundleContractsTests
{
    // =========================================================================
    // 1. ContextPolicySet 默认值对齐
    // =========================================================================

    [TestMethod]
    public void ContextPolicySet_DefaultValues_AlignWithContextDecisionPolicyVersions()
    {
        var set = new ContextPolicySet();

        Assert.AreEqual(ContextDecisionPolicyVersions.DecisionSchemaV2_0, set.DecisionSchemaVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.PackagePolicyV3_1, set.PackagePolicyVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.RetrievalPolicyV4_0, set.RetrievalPolicyVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.RelationProfileV2_0, set.RelationProfileVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.QualityContractV1_0, set.QualityContractVersion);
    }

    [TestMethod]
    public void ContextPolicySet_CanOverrideVersionsViaWithExpression()
    {
        var original = new ContextPolicySet();
        var overridden = original with
        {
            DecisionSchemaVersion = "decision-schema/3.0",
            PackagePolicyVersion = "package-policy/4.0"
        };

        Assert.AreEqual("decision-schema/3.0", overridden.DecisionSchemaVersion);
        Assert.AreEqual("package-policy/4.0", overridden.PackagePolicyVersion);
        Assert.AreEqual(ContextDecisionPolicyVersions.RetrievalPolicyV4_0, overridden.RetrievalPolicyVersion);
        Assert.AreNotSame(original, overridden);
    }

    // =========================================================================
    // 2. ModelArtifactReference
    // =========================================================================

    [TestMethod]
    public void ModelArtifactReference_DefaultStatus_IsDraft()
    {
        var artifact = new ModelArtifactReference
        {
            ArtifactId = "router-v1",
            ModelType = "router",
            Version = "1.0.0"
        };

        Assert.AreEqual(ModelArtifactStatus.Draft, artifact.Status);
        Assert.IsNull(artifact.StorageUri);
    }

    [TestMethod]
    public void ModelArtifactReference_CanSetStatus_ToActive()
    {
        var artifact = new ModelArtifactReference
        {
            ArtifactId = "router-v1",
            ModelType = "router",
            Version = "1.0.0",
            Status = ModelArtifactStatus.Active
        };

        Assert.AreEqual(ModelArtifactStatus.Active, artifact.Status);
    }

    // =========================================================================
    // 3. SafetyProfile
    // =========================================================================

    [TestMethod]
    public void SafetyProfile_Defaults_AllowDeprecatedButNotDuplicates()
    {
        var profile = new SafetyProfile { ProfileId = "safety-default-v1" };

        Assert.IsTrue(profile.AllowDeprecatedUsedByActiveChain);
        Assert.IsFalse(profile.AllowDuplicateReference);
        Assert.AreEqual(0, profile.RequiredTags.Count);
        Assert.AreEqual(0, profile.ForbiddenTags.Count);
    }

    [TestMethod]
    public void SafetyProfile_CanConfigureRequiredAndForbiddenTags()
    {
        var profile = new SafetyProfile
        {
            ProfileId = "safety-strict",
            RequiredTags = new[] { "long-term", "verified" },
            ForbiddenTags = new[] { "deprecated", "sensitive" },
            AllowDeprecatedUsedByActiveChain = false,
            AllowDuplicateReference = false
        };

        Assert.AreEqual(2, profile.RequiredTags.Count);
        Assert.AreEqual("long-term", profile.RequiredTags[0]);
        Assert.AreEqual("verified", profile.RequiredTags[1]);
        Assert.AreEqual(2, profile.ForbiddenTags.Count);
        Assert.IsFalse(profile.AllowDeprecatedUsedByActiveChain);
    }

    // =========================================================================
    // 4. BudgetProfile
    // =========================================================================

    [TestMethod]
    public void BudgetProfile_Defaults_TokenBudget8000_TopK50()
    {
        var profile = new BudgetProfile { ProfileId = "budget-default-v1" };

        Assert.AreEqual(8000, profile.DefaultTokenBudget);
        Assert.AreEqual(50, profile.DefaultTopK);
        Assert.IsTrue(profile.StrictBudgetEnforcement);
        Assert.AreEqual(0, profile.SectionRatios.Count);
    }

    [TestMethod]
    public void BudgetProfile_CanConfigureSectionRatios()
    {
        var profile = new BudgetProfile
        {
            ProfileId = "budget-sections",
            DefaultTokenBudget = 4000,
            DefaultTopK = 20,
            SectionRatios = new Dictionary<string, double>
            {
                ["working_memory"] = 0.4,
                ["recent_context"] = 0.3,
                ["related_context"] = 0.3
            },
            StrictBudgetEnforcement = false
        };

        Assert.AreEqual(4000, profile.DefaultTokenBudget);
        Assert.AreEqual(20, profile.DefaultTopK);
        Assert.AreEqual(3, profile.SectionRatios.Count);
        Assert.AreEqual(0.4, profile.SectionRatios["working_memory"]);
        Assert.IsFalse(profile.StrictBudgetEnforcement);
    }

    // =========================================================================
    // 5. RoutingProfile
    // =========================================================================

    [TestMethod]
    public void RoutingProfile_Defaults_DeterministicOnly_NoModel()
    {
        var profile = new RoutingProfile { ProfileId = "routing-default-v1" };

        Assert.IsFalse(profile.EnableModelScoring);
        Assert.IsNull(profile.ModelArtifactId);
        Assert.AreEqual(1.0, profile.DeterministicWeight);
        Assert.AreEqual(0.0, profile.ModelWeight);
        Assert.AreEqual(0.70, profile.ModelConfidenceThreshold);
        Assert.AreEqual(0, profile.EnabledExperts.Count);
    }

    [TestMethod]
    public void RoutingProfile_CanEnableModelScoring_WithArtifact()
    {
        var profile = new RoutingProfile
        {
            ProfileId = "routing-model-v1",
            EnableModelScoring = true,
            ModelArtifactId = "router-v1",
            DeterministicWeight = 0.6,
            ModelWeight = 0.4,
            ModelConfidenceThreshold = 0.85,
            EnabledExperts = new[] { "Lexical", "Semantic", "WorkingMemory" }
        };

        Assert.IsTrue(profile.EnableModelScoring);
        Assert.AreEqual("router-v1", profile.ModelArtifactId);
        Assert.AreEqual(0.6, profile.DeterministicWeight);
        Assert.AreEqual(0.4, profile.ModelWeight);
        Assert.AreEqual(0.85, profile.ModelConfidenceThreshold);
        Assert.AreEqual(3, profile.EnabledExperts.Count);
    }

    // =========================================================================
    // 6. RolloutPolicy + RollbackCondition
    // =========================================================================

    [TestMethod]
    public void RolloutPolicy_Defaults_ShadowStrategy_NoConditions()
    {
        var policy = new RolloutPolicy { PolicyId = "rollout-shadow-1" };

        Assert.AreEqual(PolicyRolloutStrategy.Shadow, policy.Strategy);
        Assert.AreEqual(0, policy.ScopedWorkspaceIds.Count);
        Assert.AreEqual(0, policy.ScopedCollectionIds.Count);
        Assert.AreEqual(0, policy.RollbackConditions.Count);
        Assert.IsNull(policy.StartAt);
        Assert.IsNull(policy.EndAt);
    }

    [TestMethod]
    public void RolloutPolicy_CanConfigureScopedCanary_WithRollbackConditions()
    {
        var policy = new RolloutPolicy
        {
            PolicyId = "rollout-canary-1",
            Strategy = PolicyRolloutStrategy.ScopedCanary,
            ScopedWorkspaceIds = new[] { "ws-1", "ws-2" },
            ScopedCollectionIds = new[] { "col-1" },
            StartAt = DateTimeOffset.UtcNow,
            EndAt = DateTimeOffset.UtcNow.AddDays(7),
            RollbackConditions = new[]
            {
                new RollbackCondition("recall", ComparisonOperator.LessThan, 0.80, "recall below 80%"),
                new RollbackCondition("latency_ms", ComparisonOperator.GreaterThan, 100.0, "latency > 100ms")
            }
        };

        Assert.AreEqual(PolicyRolloutStrategy.ScopedCanary, policy.Strategy);
        Assert.AreEqual(2, policy.ScopedWorkspaceIds.Count);
        Assert.AreEqual(1, policy.ScopedCollectionIds.Count);
        Assert.AreEqual(2, policy.RollbackConditions.Count);
        Assert.IsNotNull(policy.StartAt);
        Assert.IsNotNull(policy.EndAt);
    }

    [TestMethod]
    public void RolloutPolicy_RollbackConditions_CanTriggerOnMetricValue()
    {
        var condition = new RollbackCondition("recall", ComparisonOperator.LessThan, 0.80, "recall below 80%");

        Assert.IsTrue(condition.IsTriggered(0.70));  // 0.70 < 0.80 → triggered
        Assert.IsFalse(condition.IsTriggered(0.85)); // 0.85 >= 0.80 → not triggered
    }

    // =========================================================================
    // 7. ContextPolicyBundle
    // =========================================================================

    [TestMethod]
    public void ContextPolicyBundle_Defaults_AllProfilesInitialized()
    {
        var bundle = new ContextPolicyBundle
        {
            BundleId = "bundle-test-1",
            Version = "2026-07/v1"
        };

        Assert.IsNotNull(bundle.Policies);
        Assert.IsNotNull(bundle.Safety);
        Assert.IsNotNull(bundle.Budget);
        Assert.IsNotNull(bundle.Routing);
        Assert.AreEqual(0, bundle.ModelArtifacts.Count);
        Assert.IsNull(bundle.Rollout);
        Assert.IsFalse(bundle.IsSuperseded);
        Assert.AreEqual(DateTimeOffset.MinValue, bundle.SupersededAt);
        Assert.IsNull(bundle.SupersededByBundleId);
    }

    [TestMethod]
    public void ContextPolicyBundle_WithModelArtifacts_CanReferenceMultipleArtifacts()
    {
        var bundle = new ContextPolicyBundle
        {
            BundleId = "bundle-multi-model",
            Version = "1.0.0",
            ModelArtifacts = new[]
            {
                new ModelArtifactReference
                {
                    ArtifactId = "router-v1",
                    ModelType = "router",
                    Version = "1.0.0",
                    Status = ModelArtifactStatus.Active
                },
                new ModelArtifactReference
                {
                    ArtifactId = "reranker-v1",
                    ModelType = "reranker",
                    Version = "0.5.0",
                    Status = ModelArtifactStatus.Staged
                }
            }
        };

        Assert.AreEqual(2, bundle.ModelArtifacts.Count);
        Assert.AreEqual(ModelArtifactStatus.Active, bundle.ModelArtifacts[0].Status);
        Assert.AreEqual(ModelArtifactStatus.Staged, bundle.ModelArtifacts[1].Status);
    }

    [TestMethod]
    public void ContextPolicyBundle_IsSuperseded_TrueWhenSupersededAtSet()
    {
        var original = new ContextPolicyBundle
        {
            BundleId = "bundle-old",
            Version = "1.0.0"
        };
        var superseded = original with
        {
            SupersededAt = DateTimeOffset.UtcNow,
            SupersededByBundleId = "bundle-new"
        };

        Assert.IsFalse(original.IsSuperseded);
        Assert.IsTrue(superseded.IsSuperseded);
        Assert.AreEqual("bundle-new", superseded.SupersededByBundleId);
    }

    // =========================================================================
    // 8. PolicyActivation
    // =========================================================================

    [TestMethod]
    public void PolicyActivation_Defaults_RolloutStatusPromoted_NoOverride()
    {
        var activation = new PolicyActivation
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            BundleId = "bundle-1",
            BundleVersion = "1.0.0",
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual("system", activation.ActivatedBy);
        Assert.AreEqual(PolicyRolloutStrategy.Promoted, activation.RolloutStatus);
        Assert.IsNull(activation.BudgetOverride);
        Assert.IsNull(activation.RoutingOverride);
    }

    [TestMethod]
    public void PolicyActivation_CanOverride_BudgetAndRouting()
    {
        var activation = new PolicyActivation
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            BundleId = "bundle-1",
            BundleVersion = "1.0.0",
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivatedBy = "agent",
            RolloutStatus = PolicyRolloutStrategy.ScopedCanary,
            // override 使用受限类型 RequestBudgetOverride / RequestRoutingOverride。
            BudgetOverride = new RequestBudgetOverride { TokenBudget = 2000 },
            RoutingOverride = new RequestRoutingOverride { EnableModelScoring = true }
        };

        Assert.AreEqual("agent", activation.ActivatedBy);
        Assert.AreEqual(PolicyRolloutStrategy.ScopedCanary, activation.RolloutStatus);
        Assert.IsNotNull(activation.BudgetOverride);
        Assert.AreEqual(2000, activation.BudgetOverride.TokenBudget);
        Assert.IsNotNull(activation.RoutingOverride);
        Assert.IsTrue(activation.RoutingOverride.EnableModelScoring == true);
    }

    // =========================================================================
    // 9. ContextPolicyOverride
    // =========================================================================

    [TestMethod]
    public void ContextPolicyOverride_Empty_NotCompliant()
    {
        var override_ = new ContextPolicyOverride();

        Assert.IsFalse(override_.IsCompliant());
    }

    [TestMethod]
    public void ContextPolicyOverride_WithBundleId_IsCompliant()
    {
        var override_ = new ContextPolicyOverride { BundleId = "bundle-explicit" };

        Assert.IsTrue(override_.IsCompliant());
    }

    [TestMethod]
    public void ContextPolicyOverride_WithBudgetOverride_IsCompliant()
    {
        // 修复：BudgetOverride 现使用 RequestBudgetOverride（仅允许 TokenBudget/TopK/SectionRatios）
        var override_ = new ContextPolicyOverride
        {
            BudgetOverride = new RequestBudgetOverride { TokenBudget = 1000, TopK = 20 }
        };

        Assert.IsTrue(override_.IsCompliant());
    }

    // =========================================================================
    // 10. IPolicyRegistry 接口契约
    // =========================================================================

    [TestMethod]
    public void IPolicyRegistry_Interface_DefinesRequiredMethods()
    {
        // 编译期验证：所有方法签名存在且参数类型正确
        Type type = typeof(IPolicyRegistry);

        Assert.IsNotNull(type.GetMethod(nameof(IPolicyRegistry.GetActiveBundleAsync)));
        Assert.IsNotNull(type.GetMethod(nameof(IPolicyRegistry.GetActivationAsync)));
        Assert.IsNotNull(type.GetMethod(nameof(IPolicyRegistry.ListBundlesAsync)));
        Assert.IsNotNull(type.GetMethod(nameof(IPolicyRegistry.RegisterBundleAsync)));
        Assert.IsNotNull(type.GetMethod(nameof(IPolicyRegistry.TryActivateAsync)));
        // ActivateAsync 已彻底删除，仅保留 TryActivateAsync CAS 路径
        Assert.IsNull(type.GetMethod("ActivateAsync"));
    }

    [TestMethod]
    public async Task IPolicyRegistry_GetActiveBundleAsync_ReturnsContextPolicyBundle()
    {
        // 通过 mock 验证返回类型
        IPolicyRegistry registry = new InMemoryPolicyRegistry();
        var bundle = await registry.GetActiveBundleAsync("ws-default", "col-default");

        Assert.IsNotNull(bundle);
        Assert.IsInstanceOfType(bundle, typeof(ContextPolicyBundle));
    }

    [TestMethod]
    public async Task IPolicyRegistry_GetActivationAsync_UnmappedReturnsNull()
    {
        IPolicyRegistry registry = new InMemoryPolicyRegistry();
        var activation = await registry.GetActivationAsync("ws-unmapped", "col-unmapped");

        Assert.IsNull(activation);
    }

    [TestMethod]
    public async Task IPolicyRegistry_RegisterAndActivate_RoundTrip()
    {
        IPolicyRegistry registry = new InMemoryPolicyRegistry();
        var bundle = new ContextPolicyBundle
        {
            BundleId = "bundle-test",
            Version = "1.0.0"
        };
        await registry.RegisterBundleAsync(bundle);

        var activation = new PolicyActivation
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            BundleId = "bundle-test",
            BundleVersion = "1.0.0",
            BundleContentHash = "sha256:test",
            ActivatedAt = DateTimeOffset.UtcNow
        };
        Assert.IsTrue(await registry.TryActivateAsync(activation, expectedEpoch: 0));

        var retrieved = await registry.GetActiveBundleAsync("ws-1", "col-1");
        Assert.AreEqual("bundle-test", retrieved.BundleId);

        var retrievedActivation = await registry.GetActivationAsync("ws-1", "col-1");
        Assert.IsNotNull(retrievedActivation);
        Assert.AreEqual("bundle-test", retrievedActivation.BundleId);
    }

    [TestMethod]
    public async Task IPolicyRegistry_ListBundles_FiltersSupersededByDefault()
    {
        IPolicyRegistry registry = new InMemoryPolicyRegistry();
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

    // =========================================================================
    // 11. 反射检查：不引入存储 I/O
    // =========================================================================

    [TestMethod]
    public void ContextPolicyBundle_NoStorageMethods()
    {
        Type type = typeof(ContextPolicyBundle);

        // 不应有 SaveAsync / PersistAsync / LoadAsync 等存储方法
        Assert.IsNull(type.GetMethod("SaveAsync"));
        Assert.IsNull(type.GetMethod("PersistAsync"));
        Assert.IsNull(type.GetMethod("LoadAsync"));
        Assert.IsNull(type.GetMethod("StoreAsync"));
    }

    [TestMethod]
    public void IPolicyRegistry_DoesNotInheritIDisposable()
    {
        Type type = typeof(IPolicyRegistry);

        // 不应继承 IDisposable（接口契约允许实现层选择 InMemory / Postgres）
        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(type));
    }

    // =========================================================================
    // 12. 5 个版本字段不允许 per-request override（用户澄清）
    // =========================================================================

    [TestMethod]
    public void ContextPolicyOverride_DoesNotExposePolicySetOverride()
    {
        Type type = typeof(ContextPolicyOverride);

        // 显式反射检查：override 类型不应包含 PolicySet / Policies / Safety 字段
        Assert.IsNull(type.GetProperty("PolicySet"));
        Assert.IsNull(type.GetProperty("Policies"));
        Assert.IsNull(type.GetProperty("SafetyOverride"));
        Assert.IsNull(type.GetProperty("Safety"));

        // 应仅包含 BudgetOverride / RoutingOverride / BundleId
        Assert.IsNotNull(type.GetProperty(nameof(ContextPolicyOverride.BundleId)));
        Assert.IsNotNull(type.GetProperty(nameof(ContextPolicyOverride.BudgetOverride)));
        Assert.IsNotNull(type.GetProperty(nameof(ContextPolicyOverride.RoutingOverride)));
    }

    // =========================================================================
    // 辅助：本地 InMemoryPolicyRegistry（用于测试）
    // =========================================================================

    private sealed class InMemoryPolicyRegistry : IPolicyRegistry
    {
        private readonly Dictionary<string, ContextPolicyBundle> _bundles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PolicyActivation> _activations = new(StringComparer.Ordinal);

        public Task<ContextPolicyBundle> GetActiveBundleAsync(
            string workspaceId, string collectionId, CancellationToken cancellationToken = default)
        {
            var key = $"{workspaceId}/{collectionId}";
            if (_activations.TryGetValue(key, out var activation)
                && _bundles.TryGetValue(activation.BundleId, out var bundle))
            {
                return Task.FromResult(bundle);
            }
            // 返回全局默认 bundle
            return Task.FromResult(new ContextPolicyBundle
            {
                BundleId = "bundle-default",
                Version = "default"
            });
        }

        public Task<PolicyActivation?> GetActivationAsync(
            string workspaceId, string collectionId, CancellationToken cancellationToken = default)
        {
            var key = $"{workspaceId}/{collectionId}";
            _activations.TryGetValue(key, out var activation);
            return Task.FromResult(activation);
        }

        public Task<IReadOnlyList<ContextPolicyBundle>> ListBundlesAsync(
            bool includeSuperseded = false, CancellationToken cancellationToken = default)
        {
            var bundles = _bundles.Values
                .Where(b => includeSuperseded || !b.IsSuperseded)
                .ToList();
            return Task.FromResult<IReadOnlyList<ContextPolicyBundle>>(bundles);
        }

        public Task RegisterBundleAsync(
            ContextPolicyBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[bundle.BundleId] = bundle;
            return Task.CompletedTask;
        }

        // 精确加载 bundle；此 stub 仅按 BundleId 索引（忽略 version 精确匹配）。
        // 若 _bundles 中无对应 BundleId，返回 null（fail-closed，不静默回退默认 bundle）。
        public Task<ContextPolicyBundle?> GetBundleAsync(
            string bundleId, string? version, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue(bundleId, out var bundle);
            return Task.FromResult<ContextPolicyBundle?>(bundle);
        }

        // CAS 原子激活。
        // expectedEpoch=0 表示首次激活（当前无 activation 记录）；
        // 非零表示仅当当前 activation.Epoch == expectedEpoch 时才激活。
        // CAS 失败（epoch 不匹配）返回 false，调用方可重试。
        public Task<bool> TryActivateAsync(
            PolicyActivation next, long expectedEpoch, CancellationToken cancellationToken = default)
        {
            var key = $"{next.WorkspaceId}/{next.CollectionId}";
            lock (_activations)
            {
                if (!_activations.TryGetValue(key, out var current))
                {
                    if (expectedEpoch != 0)
                    {
                        return Task.FromResult(false);
                    }
                    _activations[key] = next with { Epoch = 1 };
                    return Task.FromResult(true);
                }

                if (current.Epoch != expectedEpoch)
                {
                    return Task.FromResult(false);
                }

                _activations[key] = next with { Epoch = current.Epoch + 1 };
                return Task.FromResult(true);
            }
        }
    }
}
