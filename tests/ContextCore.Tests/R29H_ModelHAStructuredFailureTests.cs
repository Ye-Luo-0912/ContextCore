using System.Net;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Adapters;
using ContextCore.Service.Hosting;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Tests;

// ===========================================================================
// Model HA 与推理租约 —— 验收测试
//
// 覆盖范围：
//   Model State Reconciler：Desired State 先写、节点后 reconcile；
//      全新节点首次启动立即应用 Champion 模型（不等待 Revision 变更）；
//      期望状态更新（Revision CAS）后节点自动切换模型。
//   Engine Lease 释放：OnnxInferenceEngine 实现 IAsyncDisposable 且幂等；
//      ModelActivationManager 停用时释放 native ONNX session（修复泄漏）。
//   结构化失败：length / content_filter / empty_choices 不再落入瞬态
//      Unavailable 分类——网关不重试、不触发回退，失败原因精确传播。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Model-HA")]
public sealed class R29H_ModelHAStructuredFailureTests
{
    private const string SchemaVersion = "s4-schema-v1";
    private const string CalibrationVersion = "s4-cal-v1";

    // ===========================================================================
    // Model State Reconciler：首次启动立即应用 + Desired State 先写
    // ===========================================================================

    [TestMethod]
    public async Task Reconciler_FirstStart_ImmediatelyActivatesChampionModel()
    {
        const string modelId = "s4-champion-v1";
        const string contentHash = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelId, contentHash, ClusterModelSlotDesiredStatus.Active, "control-plane");

        var (manager, factory) = BuildActivationManager(modelId);
        using var worker = CreateWorker(slotStore, manager);

        var startedAt = DateTimeOffset.UtcNow;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var descriptor = await WaitForActiveAsync(manager, modelId, "全新节点首次同步应立即激活 Champion 模型。");
            Assert.AreEqual(modelId, descriptor.ModelArtifactId);

            // 等待覆盖至少一个额外轮询周期，确认同进程不重复激活
            await Task.Delay(300);
            Assert.AreEqual(1, factory.CreateCallCount, "同 ContentHash 的模型不应被重复激活。");
            Assert.IsTrue(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(5),
                "首次激活应在启动后立即发生，而非等待 Revision 变更。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Reconciler_FirstStart_InactiveSlot_DoesNotCreateSession()
    {
        const string modelId = "s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, null, null, ClusterModelSlotDesiredStatus.Inactive, "control-plane");

        var (manager, factory) = BuildActivationManager(modelId);
        using var worker = CreateWorker(slotStore, manager);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(400); // 覆盖至少一个轮询周期
            Assert.IsNull(manager.ActiveDescriptor, "Inactive 期望状态下不应激活模型。");
            Assert.AreEqual(0, factory.CreateCallCount, "Inactive 期望状态不应创建任何 ONNX session。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Reconciler_DesiredStateUpdate_HigherRevision_SwitchesModel()
    {
        const string modelA = "s4-champion-v1";
        const string modelB = "s4-champion-v2";
        const string hashA = "sha256:s4-champion-v1";
        const string hashB = "sha256:s4-champion-v2";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelA, hashA, ClusterModelSlotDesiredStatus.Active, "control-plane");

        var (manager, factory) = BuildActivationManager(modelA, modelB);
        using var worker = CreateWorker(slotStore, manager);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var d1 = await WaitForActiveAsync(manager, modelA, "应首先激活模型 A。");
            Assert.AreEqual(modelA, d1.ModelArtifactId);

            // Desired State 先写（Revision CAS 1 → 2），节点随后 reconcile 切换
            var updated = await slotStore.TryUpdateAsync("primary", expectedRevision: 1, modelB, hashB, ClusterModelSlotDesiredStatus.Active, "control-plane");
            Assert.IsNotNull(updated, "期望状态 CAS 更新应成功。");

            var d2 = await WaitForActiveAsync(manager, modelB, "期望状态更新后节点应切换至模型 B。");
            Assert.AreEqual(modelB, d2.ModelArtifactId);
            Assert.AreEqual(2, factory.CreateCallCount, "两次激活应恰好创建两个 session。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 验证：本地引擎代次（LocalEngineGeneration）不得作为集群槽位 Revision 的下界——
    /// 节点已本地激活模型（ActiveGeneration ≥ 集群 Revision）时，仍必须应用集群期望状态
    /// （两套计数空间独立，混用会导致期望模型从未被加载）。
    /// </summary>
    [TestMethod]
    public async Task Reconciler_ExistingLocalActivation_DoesNotBlockLowerSlotRevision()
    {
        const string modelA = "s4-champion-v1";
        const string modelB = "s4-champion-v2";
        const string hashA = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        // 集群期望 Revision = 1（DesiredStatus=Active，目标模型 A）
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelA, hashA, ClusterModelSlotDesiredStatus.Active, "control-plane");

        var (manager, factory) = BuildActivationManager(modelA, modelB);
        // 节点先本地激活模型 B（ActiveGeneration=1，本地引擎代次 ≥ 集群 Revision）
        var local = await manager.ActivateAsync(modelB, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score"
        });
        Assert.IsTrue(local.Success, $"本地激活应成功：{local.Error}");

        using var worker = CreateWorker(slotStore, manager);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // 关键断言：本地代次不高于集群期望 → 节点必须切换到集群期望的模型 A
            var descriptor = await WaitForActiveAsync(manager, modelA, "本地已有激活也不应跳过集群期望状态。");
            Assert.AreEqual(modelA, descriptor.ModelArtifactId);
            Assert.AreEqual(2, factory.CreateCallCount,
                "本地激活（B）+ 集群期望切换（A）应各创建一个 session。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ===========================================================================
    // Model Node Applied State：节点已应用状态记录 + 冷启动/热引擎恢复语义
    // ===========================================================================

    [TestMethod]
    public async Task Reconciler_RecordsAppliedState_AfterActivation()
    {
        const string modelId = "s4-champion-v1";
        const string contentHash = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelId, contentHash, ClusterModelSlotDesiredStatus.Active, "control-plane");

        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        var (manager, _) = BuildActivationManager(modelId);
        using var worker = CreateWorker(slotStore, manager, appliedStore);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForActiveAsync(manager, modelId, "节点应应用集群期望状态并记录已应用状态。");
            var applied = await WaitForAppliedAsync(appliedStore, Environment.MachineName, "primary",
                "激活后节点应写入已应用状态记录。");

            Assert.AreEqual(1, applied.AppliedRevision);
            Assert.AreEqual(modelId, applied.ModelArtifactId);
            Assert.AreEqual(contentHash, applied.ContentHash);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 验证：节点已应用状态不得成为冷启动跳过集群期望的依据——
    /// 进程重启后本地引擎为空，即使已应用状态声称"本节点已应用过该 Revision"，
    /// 也必须重新应用当前期望状态（与 P0-7 的计数空间隔离原则一致）。
    /// </summary>
    [TestMethod]
    public async Task Reconciler_ColdEngine_AppliesDesiredDespiteAppliedState()
    {
        const string modelA = "s4-champion-v1";
        const string hashA = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelA, hashA, ClusterModelSlotDesiredStatus.Active, "control-plane");

        // 预置节点已应用状态（Revision=1，模型 A），但本地引擎为空（冷启动）。
        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        await appliedStore.UpsertAsync(new ModelNodeAppliedState
        {
            NodeId = Environment.MachineName,
            SlotName = "primary",
            AppliedRevision = 1,
            ModelArtifactId = modelA,
            ContentHash = hashA,
            AppliedAt = DateTimeOffset.UtcNow
        });

        var (manager, factory) = BuildActivationManager(modelA);
        using var worker = CreateWorker(slotStore, manager, appliedStore);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var descriptor = await WaitForActiveAsync(manager, modelA, "冷启动必须重新应用集群期望状态。");
            Assert.AreEqual(modelA, descriptor.ModelArtifactId);
            Assert.AreEqual(1, factory.CreateCallCount,
                "冷启动（引擎为空）时已应用状态不得跳过集群期望的激活。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 验证：本地引擎与节点已应用状态一致（同宿主内 Reconciler 重建）时，
    /// 从 AppliedRevision 继续收敛，不重复激活已生效的模型。
    /// </summary>
    [TestMethod]
    public async Task Reconciler_WarmEngineMatchingAppliedState_DoesNotReapply()
    {
        const string modelA = "s4-champion-v1";
        const string hashA = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelA, hashA, ClusterModelSlotDesiredStatus.Active, "control-plane");

        // 本地引擎先激活模型 A（引擎热），节点已应用状态与之匹配（Revision=1，模型 A）。
        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        await appliedStore.UpsertAsync(new ModelNodeAppliedState
        {
            NodeId = Environment.MachineName,
            SlotName = "primary",
            AppliedRevision = 1,
            ModelArtifactId = modelA,
            ContentHash = hashA,
            AppliedAt = DateTimeOffset.UtcNow
        });

        var (manager, factory) = BuildActivationManager(modelA);
        var local = await manager.ActivateAsync(modelA, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score"
        });
        Assert.IsTrue(local.Success, $"本地激活应成功：{local.Error}");

        using var worker = CreateWorker(slotStore, manager, appliedStore);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(400); // 覆盖至少一个轮询周期
            Assert.AreEqual(modelA, manager.ActiveDescriptor?.ModelArtifactId);
            Assert.AreEqual(1, factory.CreateCallCount,
                "引擎已与节点已应用状态一致，不应重复激活。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ===========================================================================
    // PromoteStaged 发布前 Descriptor 校验（fail-closed）
    // ===========================================================================

    [TestMethod]
    public async Task PromoteStaged_MatchingDescriptor_Publishes()
    {
        const string modelA = "s4-champion-v1";
        const string hashA = "sha256:s4-champion-v1";
        var (manager, _) = BuildActivationManager(modelA);

        var staged = await manager.LoadAndWarmupAsync(modelA, CreateDefaultOnnxOptions());
        Assert.IsTrue(staged.Success, $"Warmup 应成功：{staged.Error}");
        Assert.IsNotNull(staged.Descriptor);

        var result = await manager.PromoteStagedAsync(staged.HandleId, modelA, hashA);

        Assert.IsTrue(result.Success, $"期望匹配时应发布成功：{result.Error}");
        Assert.AreEqual(modelA, manager.ActiveDescriptor?.ModelArtifactId);
        Assert.AreEqual(hashA, manager.ContentHash);
    }

    [TestMethod]
    public async Task PromoteStaged_WrongModelArtifactId_FailsClosed()
    {
        const string modelA = "s4-champion-v1";
        var (manager, factory) = BuildActivationManager(modelA);

        var staged = await manager.LoadAndWarmupAsync(modelA, CreateDefaultOnnxOptions());
        Assert.IsTrue(staged.Success, $"Warmup 应成功：{staged.Error}");

        // 期望模型 Id 与实际 Staged Descriptor 不匹配 → 拒绝发布（fail-closed）。
        var result = await manager.PromoteStagedAsync(staged.HandleId, "s4-other-model", "sha256:other");

        Assert.IsFalse(result.Success, "期望模型 Id 不匹配时不得发布。");
        StringAssert.Contains(result.Error, "不匹配");
        Assert.IsNull(manager.ActiveDescriptor, "发布被拒绝时不得切换 active 引擎。");
        Assert.AreEqual(1, factory.CreateCallCount, "失败路径不得创建额外 session。");
    }

    [TestMethod]
    public async Task PromoteStaged_WrongContentHash_FailsClosed()
    {
        const string modelA = "s4-champion-v1";
        var (manager, factory) = BuildActivationManager(modelA);

        var staged = await manager.LoadAndWarmupAsync(modelA, CreateDefaultOnnxOptions());
        Assert.IsTrue(staged.Success, $"Warmup 应成功：{staged.Error}");

        // 期望内容哈希与实际 Staged Descriptor 不匹配 → 拒绝发布（fail-closed）。
        var result = await manager.PromoteStagedAsync(staged.HandleId, modelA, "sha256:wrong-hash");

        Assert.IsFalse(result.Success, "期望内容哈希不匹配时不得发布。");
        StringAssert.Contains(result.Error, "不匹配");
        Assert.IsNull(manager.ActiveDescriptor, "发布被拒绝时不得切换 active 引擎。");
        Assert.AreEqual(1, factory.CreateCallCount, "失败路径不得创建额外 session。");
    }

    [TestMethod]
    public async Task PromoteStaged_NullExpectations_Publishes()
    {
        const string modelA = "s4-champion-v1";
        var (manager, _) = BuildActivationManager(modelA);

        var staged = await manager.LoadAndWarmupAsync(modelA, CreateDefaultOnnxOptions());
        Assert.IsTrue(staged.Success, $"Warmup 应成功：{staged.Error}");

        // 期望值均为 null（未指定）→ 跳过校验，保持向后兼容发布语义。
        var result = await manager.PromoteStagedAsync(staged.HandleId);

        Assert.IsTrue(result.Success, $"未指定期望值时按原语义发布：{result.Error}");
        Assert.AreEqual(modelA, manager.ActiveDescriptor?.ModelArtifactId);
    }

    // ===========================================================================
    // Engine Lease 释放：DisposeAsync 幂等 + 停用释放 native session
    // ===========================================================================

    [TestMethod]
    public async Task OnnxInferenceEngine_DisposeAsync_DisposesSessionOnce_AndIsIdempotent()
    {
        var session = new MockOnnxInferenceSession("s4-model", "1.0.0", "sha256:s4", Array.Empty<InferenceOutput>());
        var engine = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score"
        });

        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.AreEqual(1, session.DisposeCallCount, "重复 Dispose 应幂等：native session 只释放一次。");
    }

    [TestMethod]
    public async Task ModelActivationManager_Dispose_DisposesNativeOnnxSession()
    {
        const string modelId = "s4-champion-v1";
        var session = new MockOnnxInferenceSession(
            modelId, "1.0.0", "sha256:" + modelId,
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });
        var (manager, _) = BuildActivationManager(modelId, session);

        var activate = await manager.ActivateAsync(modelId, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score"
        });
        Assert.IsTrue(activate.Success, $"激活应成功：{activate.Error}");
        Assert.AreEqual(0, session.DisposeCallCount, "激活阶段不应释放 session。");

        // Dispose 路径（SafeDisposeEngineAsync）必须释放 native ONNX session——
        // 修复前 OnnxInferenceEngine 未实现 IAsyncDisposable，Dispose 静默漏掉会话。
        await manager.DisposeAsync();

        Assert.IsNull(manager.ActiveDescriptor, "Dispose 后不应有活跃模型。");
        Assert.AreEqual(1, session.DisposeCallCount, "Dispose 必须释放 native ONNX session（IAsyncDisposable 路径）。");
    }

    // ===========================================================================
    // 结构化失败：length / content_filter / empty_choices 非瞬态
    // ===========================================================================

    [TestMethod]
    public async Task ModelGateway_LengthTruncated_NoRetryNoFallback()
        => await AssertStructuredFailure("length_truncated");

    [TestMethod]
    public async Task ModelGateway_ContentFilter_NoRetryNoFallback()
        => await AssertStructuredFailure("content_filter");

    [TestMethod]
    public async Task ModelGateway_EmptyChoices_NoRetryNoFallback()
        => await AssertStructuredFailure("empty_choices");

    [TestMethod]
    public async Task ChatAdapter_EmptyChoices_ReturnsStructuredFailure()
    {
        var handler = StubHttpMessageHandler.Json("""
        {
          "choices": [],
          "usage": { "prompt_tokens": 1, "completion_tokens": 0 }
        }
        """);
        var adapter = CreateAdapter(handler);

        var response = await adapter.ChatWithToolsAsync(CreateChatRequest());

        Assert.IsFalse(response.Succeeded, "空 choices 不得当作成功响应。");
        Assert.AreEqual("empty_choices", response.Metadata["failureReason"]);
        Assert.AreEqual(ModelChatFinishReason.Error, response.FinishReason);
    }

    [TestMethod]
    public async Task ChatAdapter_LengthTruncated_ReturnsStructuredFailure()
    {
        var handler = StubHttpMessageHandler.Json("""
        {
          "choices": [
            {
              "message": { "role": "assistant", "content": "部分输出" },
              "finish_reason": "length"
            }
          ],
          "usage": { "prompt_tokens": 5, "completion_tokens": 200 }
        }
        """);
        var adapter = CreateAdapter(handler);

        var response = await adapter.ChatWithToolsAsync(CreateChatRequest());

        Assert.IsFalse(response.Succeeded, "finish_reason=length 不得当作正常最终答案。");
        Assert.AreEqual("length_truncated", response.Metadata["failureReason"]);
        Assert.AreEqual(ModelChatFinishReason.Length, response.FinishReason);
    }

    [TestMethod]
    public async Task ChatAdapter_ContentFilter_ReturnsStructuredFailure()
    {
        var handler = StubHttpMessageHandler.Json("""
        {
          "choices": [
            {
              "message": { "role": "assistant", "content": "无法显示" },
              "finish_reason": "content_filter"
            }
          ],
          "usage": { "prompt_tokens": 5, "completion_tokens": 10 }
        }
        """);
        var adapter = CreateAdapter(handler);

        var response = await adapter.ChatWithToolsAsync(CreateChatRequest());

        Assert.IsFalse(response.Succeeded, "finish_reason=content_filter 不得当作正常最终答案。");
        Assert.AreEqual("content_filter", response.Metadata["failureReason"]);
        Assert.AreEqual(ModelChatFinishReason.ContentFilter, response.FinishReason);
    }

    private static async Task AssertStructuredFailure(string failureReason)
    {
        var primary = FuncModelAdapter.Failing("primary-model", failureReason);
        var fallback = FuncModelAdapter.Success("fallback-model", "fallback content");
        var gateway = new ConfigurableModelGateway(
            CreateGatewayOptions(maxRetryCount: 3, enableFallback: true),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "s4-structured-" + failureReason,
            Role = ModelRole.Router,
            Prompt = "test"
        });

        Assert.IsFalse(response.Succeeded, $"{failureReason} 应保持失败。");
        Assert.AreEqual(failureReason, response.Metadata["failureReason"], "结构化失败原因应精确传播到响应。");
        Assert.AreEqual(1, primary.CallCount, $"{failureReason} 非瞬态，不应重试（配置了 3 次重试）。");
        Assert.AreEqual(0, fallback.CallCount, $"{failureReason} 非瞬态，不应触发回退模型。");
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static async Task<ModelArtifactDescriptor> WaitForActiveAsync(
        IModelActivationManager manager,
        string modelId,
        string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (manager.ActiveDescriptor is { } d && d.ModelArtifactId == modelId)
            {
                return d;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"{message}（等待模型 {modelId} 激活超时）。");
        return null!;
    }

    private static async Task<ModelNodeAppliedState> WaitForAppliedAsync(
        IModelNodeAppliedStateStore store,
        string nodeId,
        string slotName,
        string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var applied = await store.GetAsync(nodeId, slotName);
            if (applied is not null)
            {
                return applied;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"{message}（等待节点已应用状态写入超时）。");
        return null!;
    }

    private static OnnxInferenceEngineOptions CreateDefaultOnnxOptions() => new()
    {
        InputTensorName = "input",
        ScoreOutputName = "score"
    };

    private static ModelStateReconcilerWorker CreateWorker(
        IClusterModelSlotStore slotStore,
        IModelActivationManager manager,
        IModelNodeAppliedStateStore? appliedStateStore = null)
    {
        var options = new ModelStateReconcilerOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(100)
        };
        return new ModelStateReconcilerWorker(
            slotStore,
            manager,
            new TestOptionsMonitor<ModelStateReconcilerOptions>(options),
            new ConfigurationBuilder().Build(),
            NullLogger<ModelStateReconcilerWorker>.Instance,
            appliedStateStore);
    }

    private static (ModelActivationManager Manager, TrackingSessionFactory Factory) BuildActivationManager(
        params string[] modelIds)
    {
        var registry = new InMemoryModelArtifactRegistry();
        var cal = new PlattCalibrationService();
        foreach (var id in modelIds)
        {
            registry.RegisterAsync(MakeDescriptor(id)).GetAwaiter().GetResult();
            cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: id, version: CalibrationVersion);
        }

        var session = new MockOnnxInferenceSession(
            modelIds[0], "1.0.0", "sha256:" + modelIds[0],
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });
        var factory = new TrackingSessionFactory(session);
        var fallback = new DeterministicBatchInferenceEngine();
        var manager = new ModelActivationManager(
            registry,
            new DefaultCalibrationValidator(),
            BuildFeatureRegistry(),
            factory,
            fallback,
            cal);
        return (manager, factory);
    }

    private static (ModelActivationManager Manager, TrackingSessionFactory Factory) BuildActivationManager(
        string modelId,
        MockOnnxInferenceSession session)
    {
        var registry = new InMemoryModelArtifactRegistry();
        registry.RegisterAsync(MakeDescriptor(modelId)).GetAwaiter().GetResult();
        var cal = new PlattCalibrationService();
        cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: modelId, version: CalibrationVersion);

        var factory = new TrackingSessionFactory(session);
        var fallback = new DeterministicBatchInferenceEngine();
        var manager = new ModelActivationManager(
            registry,
            new DefaultCalibrationValidator(),
            BuildFeatureRegistry(),
            factory,
            fallback,
            cal);
        return (manager, factory);
    }

    private static DefaultFeatureRegistry BuildFeatureRegistry()
    {
        var registry = new DefaultFeatureRegistry();
        registry.Register(new FeatureSchema
        {
            Version = SchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" }
            }
        });
        return registry;
    }

    private static ModelArtifactDescriptor MakeDescriptor(string artifactId) => new()
    {
        ModelArtifactId = artifactId,
        ModelName = artifactId,
        ModelVersion = "1.0.0",
        FeatureSchemaVersion = SchemaVersion,
        CalibrationVersion = CalibrationVersion,
        EngineKind = InferenceEngineKind.RealModel,
        ContentHash = "sha256:" + artifactId,
        ArtifactPath = "/path/to/" + artifactId + ".onnx",
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private static ModelGatewayOptions CreateGatewayOptions(int maxRetryCount, bool enableFallback) => new()
    {
        Models = new[]
        {
            new ModelEndpointOptions
            {
                Name = "primary-model",
                Provider = "mock",
                Endpoint = "mock://primary",
                Enabled = true,
                Timeout = TimeSpan.FromSeconds(1),
                Metadata = new Dictionary<string, string> { ["model"] = "primary-model" }
            },
            new ModelEndpointOptions
            {
                Name = "fallback-model",
                Provider = "mock",
                Endpoint = "mock://fallback",
                Enabled = true,
                Timeout = TimeSpan.FromSeconds(1),
                Metadata = new Dictionary<string, string> { ["model"] = "fallback-model" }
            }
        },
        Routes = new[]
        {
            new ModelRoleRoute
            {
                Role = ModelRole.Router,
                PrimaryModelName = "primary-model",
                FallbackModelName = "fallback-model",
                MaxRetryCount = maxRetryCount,
                EnableFallback = enableFallback,
                FallbackOnTimeout = true,
                FallbackOnRateLimit = true,
                FallbackOnServerError = true,
                FallbackOnInvalidJson = true
            }
        }
    };

    private static OpenAiCompatibleModelAdapter CreateAdapter(StubHttpMessageHandler handler) => new(
        new ModelEndpointOptions
        {
            Name = "s4-gpt",
            Provider = "openai-compatible",
            Endpoint = "https://example.com/v1",
            ApiKey = "s4-secret",
            Enabled = true,
            Timeout = TimeSpan.FromSeconds(5),
            Metadata = new Dictionary<string, string> { ["model"] = "s4-gpt" }
        },
        new HttpClient(handler));

    private static ModelChatRequest CreateChatRequest() => new()
    {
        OperationId = "s4-chat",
        Role = ModelRole.Router,
        Messages = new[]
        {
            new ModelChatMessage { Role = ModelChatRole.User, Content = "hi" }
        }
    };

    // ===========================================================================
    // 测试辅助
    // ===========================================================================

    private sealed class TrackingSessionFactory : IOnnxInferenceSessionFactory
    {
        private readonly IOnnxInferenceSession _session;

        public TrackingSessionFactory(IOnnxInferenceSession session)
        {
            _session = session;
        }

        public int CreateCallCount { get; private set; }

        public ValueTask<IOnnxInferenceSession> CreateAsync(
            OnnxInferenceEngineOptions options,
            ModelArtifactDescriptor? descriptor = null,
            CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            return ValueTask.FromResult(_session);
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _json;

        private StubHttpMessageHandler(string json)
        {
            _json = json;
        }

        public static StubHttpMessageHandler Json(string json) => new(json);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                ReasonPhrase = "OK"
            });
        }
    }

    private sealed class FuncModelAdapter : IModelAdapter
    {
        private readonly Func<ModelRequest, ModelResponse> _handler;

        private FuncModelAdapter(string name, Func<ModelRequest, ModelResponse> handler)
        {
            Name = name;
            _handler = handler;
        }

        public string Name { get; }

        public int CallCount { get; private set; }

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_handler(request));
        }

        public static FuncModelAdapter Success(string name, string content) => new(name, request => new ModelResponse
        {
            OperationId = request.OperationId,
            Content = content,
            Succeeded = true,
            Metadata = new Dictionary<string, string> { ["modelName"] = name }
        });

        public static FuncModelAdapter Failing(string name, string failureReason) => new(name, request => new ModelResponse
        {
            OperationId = request.OperationId,
            Content = string.Empty,
            Succeeded = false,
            ErrorMessage = $"{failureReason} 结构化失败。",
            Metadata = new Dictionary<string, string> { ["failureReason"] = failureReason }
        });
    }
}
