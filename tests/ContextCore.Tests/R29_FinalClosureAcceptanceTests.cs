using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;

namespace ContextCore.Tests;

// ===========================================================================
// R29 Final Closure — 30 项硬验收测试
//
// 覆盖六条工作流（A-F）的核心契约：
//   A. 持久化投递（Durable Delivery）：Outbox + Transport 的 FIFO / 计数 / 往返契约
//   B. Tool 副作用安全（Tool Effect Safety）：Journal 状态机 + expected-state CAS
//   C. Canary 真相（Canary Truth）：外部 ground-truth 指标采集器
//   D. 模型激活（Model Activation）：ActivationManager 代理行为 + 失败路径
//   E. Agent 智能（Agent Intelligence）：状态机 + 哈希链 + CAS Run Store
//   F. 性能真相（Performance Truth）：组件健康注册表 + benchmark 脚本门控
//
// 设计原则：
//   - 全部使用 InMemory 实现（无 Postgres / 真实 ONNX 依赖）
//   - 需要 Postgres / 真实 ONNX 的场景用 InMemory 模拟或 Assert.Inconclusive
//   - 复用同 assembly 的 internal 测试辅助：InMemoryModelArtifactRegistry /
//     MockOnnxInferenceSession / MockSessionFactory / FailingSessionFactory
//   - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// 工作流 A：Durable Delivery — Outbox + Transport 投递契约（5 项）
// ===========================================================================

[TestClass]
[TestCategory("R29-Closure")]
[TestCategory("Workflow-A")]
public sealed class WorkflowA_DurableDeliveryAcceptanceTests
{
    [TestMethod]
    public async Task Outbox_EnqueueAndDequeue_PreservesResultBody()
    {
        // 验证：EnqueueAsync 写入的结果能通过 DequeueAsync FIFO 读出，字段保持一致
        var outbox = new InMemoryKernelResultOutbox(capacity: 16);
        var result = BuildResult("instr-A1", output: "output-A1");

        await outbox.EnqueueAsync(result);
        var dequeued = await outbox.DequeueAsync();

        Assert.IsNotNull(dequeued, "Outbox 应能读出刚写入的结果。");
        Assert.AreEqual(result.InstructionId, dequeued!.InstructionId);
        Assert.AreEqual(result.Output, dequeued.Output);
    }

    [TestMethod]
    public async Task Outbox_PendingCount_ReflectsEnqueueAndDequeue()
    {
        // 验证：PendingCount 同步反映 Enqueue/Dequeue 操作后的积压数量
        var outbox = new InMemoryKernelResultOutbox(capacity: 16);

        Assert.AreEqual(0, outbox.PendingCount, "初始 PendingCount 应为 0。");

        await outbox.EnqueueAsync(BuildResult("instr-A2-1"));
        await outbox.EnqueueAsync(BuildResult("instr-A2-2"));
        Assert.AreEqual(2, outbox.PendingCount, "Enqueue 2 条后 PendingCount 应为 2。");

        await outbox.DequeueAsync();
        Assert.AreEqual(1, outbox.PendingCount, "Dequeue 1 条后 PendingCount 应为 1。");
    }

    [TestMethod]
    public async Task Outbox_GetPendingCountAsync_MatchesSyncCounter()
    {
        // 验证：异步 GetPendingCountAsync 与同步 PendingCount 返回一致值（P2 异步化兼容）
        var outbox = new InMemoryKernelResultOutbox(capacity: 16);
        await outbox.EnqueueAsync(BuildResult("instr-A3-1"));
        await outbox.EnqueueAsync(BuildResult("instr-A3-2"));
        await outbox.EnqueueAsync(BuildResult("instr-A3-3"));

        var syncCount = outbox.PendingCount;
        var asyncCount = await outbox.GetPendingCountAsync();

        Assert.AreEqual(syncCount, asyncCount, "同步与异步 PendingCount 必须一致。");
        Assert.AreEqual(3, asyncCount, "3 条 Enqueue 后异步计数应为 3。");
    }

    [TestMethod]
    public async Task Transport_SubmitAndReceive_PreservesInstruction()
    {
        // 验证：InProcessTransport.SubmitAsync 写入的指令能通过 ReceiveAsync 读出
        var transport = new InProcessTransport(capacity: 16);
        var instruction = new AgentKernelInstruction
        {
            InstructionId = "instr-A4",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "execute-payload-A4"
        };

        await transport.SubmitAsync(instruction);
        var received = await transport.ReceiveAsync();

        Assert.IsNotNull(received, "Transport 应能读出刚提交的指令。");
        Assert.AreEqual(instruction.InstructionId, received!.InstructionId);
        Assert.AreEqual(instruction.Kind, received.Kind);
        Assert.AreEqual(instruction.Payload, received.Payload);
    }

    [TestMethod]
    public async Task Transport_SendAndReceiveResult_PreservesResult()
    {
        // 验证：Transport.SendResultAsync 写入的结果能通过 ReceiveResultAsync 读出
        var transport = new InProcessTransport(capacity: 16);
        var result = BuildResult("instr-A5", output: "result-A5");

        await transport.SendResultAsync(result);
        var received = await transport.ReceiveResultAsync();

        Assert.IsNotNull(received, "Transport outbox 应能读出刚发送的结果。");
        Assert.AreEqual(result.InstructionId, received!.InstructionId);
        Assert.AreEqual(result.Output, received.Output);
    }

    private static AgentKernelResult BuildResult(string instructionId, string? output = null) => new()
    {
        InstructionId = instructionId,
        Succeeded = true,
        Output = output ?? ("output-" + instructionId)
    };
}

// ===========================================================================
// 工作流 B：Tool Effect Safety — Journal 状态机 + expected-state CAS（5 项）
// ===========================================================================

[TestClass]
[TestCategory("R29-Closure")]
[TestCategory("Workflow-B")]
public sealed class WorkflowB_ToolEffectSafetyAcceptanceTests
{
    [TestMethod]
    public async Task Journal_PrepareAsync_StoresEntryInPreparedState()
    {
        // 验证：PrepareAsync 写入 Prepared 条目，GetEntryAsync 能读出
        var journal = new InMemoryToolDispatchJournal();
        var entry = BuildEntry("req-B1", ToolDispatchState.Prepared);

        await journal.PrepareAsync(entry);

        var stored = await journal.GetEntryAsync("req-B1");
        Assert.IsNotNull(stored, "PrepareAsync 后应能读出条目。");
        Assert.AreEqual(ToolDispatchState.Prepared, stored!.State);
        Assert.AreEqual(entry.ToolName, stored.ToolName);
    }

    [TestMethod]
    public async Task Journal_FullStateMachine_AdvancesThroughAllStates()
    {
        // 验证：完整状态机推进 Prepared → Dispatched → Committed → ResultDelivered
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-B2";
        await journal.PrepareAsync(BuildEntry(requestId, ToolDispatchState.Prepared));

        await journal.MarkDispatchedAsync(requestId, externalOperationId: "ext-op-B2");
        var afterDispatch = await journal.GetEntryAsync(requestId);
        Assert.AreEqual(ToolDispatchState.Dispatched, afterDispatch!.State);
        Assert.AreEqual("ext-op-B2", afterDispatch.ExternalOperationId);

        await journal.MarkCommittedAsync(requestId);
        var afterCommit = await journal.GetEntryAsync(requestId);
        Assert.AreEqual(ToolDispatchState.Committed, afterCommit!.State);

        await journal.MarkResultDeliveredAsync(requestId);
        var afterDelivered = await journal.GetEntryAsync(requestId);
        Assert.AreEqual(ToolDispatchState.ResultDelivered, afterDelivered!.State);
    }

    [TestMethod]
    public async Task Journal_MarkDispatchedAsync_ThrowsWhenMissingPrepare()
    {
        // 验证 P0-3 expected-state CAS：缺失 Prepared 前驱时抛 InvalidOperationException（不 auto-create stub）
        var journal = new InMemoryToolDispatchJournal();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.MarkDispatchedAsync("req-B3-no-prepare").AsTask());
    }

    [TestMethod]
    public async Task Journal_MarkResultDeliveredAsync_ThrowsWhenMissingPrepare()
    {
        // 验证 P0-3 expected-state CAS：MarkResultDeliveredAsync 在缺失前驱记录时抛 InvalidOperationException
        // （与 MarkDispatchedAsync 对称，保证审计链完整——不存在 → ResultDelivered 这样的跳跃不再可能）
        var journal = new InMemoryToolDispatchJournal();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.MarkResultDeliveredAsync("req-B4-no-prepare").AsTask());
    }

    [TestMethod]
    public async Task Journal_StateTransition_ThrowsOnRegression()
    {
        // 验证：状态机不可逆退（Dispatched → Prepared 应抛异常）
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-B5";
        await journal.PrepareAsync(BuildEntry(requestId, ToolDispatchState.Prepared));
        await journal.MarkDispatchedAsync(requestId);

        // 已是 Dispatched，再次 MarkDispatchedAsync（target=Dispatched, current=Dispatched）
        // (int)target <= (int)current → 应抛逆退异常
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.MarkDispatchedAsync(requestId).AsTask());
    }

    private static ToolDispatchJournalEntry BuildEntry(string requestId, ToolDispatchState state) => new()
    {
        RequestId = requestId,
        ToolName = "echo",
        State = state,
        IdempotencyKey = "idem-" + requestId,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}

// ===========================================================================
// 工作流 C：Canary Truth — 外部 ground-truth 指标采集器（5 项）
// ===========================================================================

[TestClass]
[TestCategory("R29-Closure")]
[TestCategory("Workflow-C")]
public sealed class WorkflowC_CanaryTruthAcceptanceTests
{
    [TestMethod]
    public async Task ExternalMetrics_RegisterTaskResult_ComputesSuccessRate()
    {
        // 验证：注册 4 次任务结果（3 成功 + 1 失败）→ TaskSuccessRate = 0.75
        var source = new DefaultCanaryExternalMetricsSource();
        var runId = "run-C1";
        var windowStart = DateTimeOffset.UtcNow;

        source.RegisterTaskResult(runId, succeeded: true);
        source.RegisterTaskResult(runId, succeeded: true);
        source.RegisterTaskResult(runId, succeeded: true);
        source.RegisterTaskResult(runId, succeeded: false);

        var metrics = await source.CollectAsync(runId, windowStart, DateTimeOffset.UtcNow);

        Assert.IsNotNull(metrics.TaskSuccessRate, "TaskSuccessRate 不应为 null。");
        Assert.AreEqual(0.75, metrics.TaskSuccessRate!.Value, 0.0001, "3/4 成功 → 0.75。");
        Assert.AreEqual(4, metrics.SampleCount, "SampleCount 应为 4。");
    }

    [TestMethod]
    public async Task ExternalMetrics_RegisterToolResultAsync_ComputesSuccessRate()
    {
        // 验证：注册 2 次 Tool 成功 + 1 次 Tool 失败 → ToolSuccessRate = 2/3
        var source = new DefaultCanaryExternalMetricsSource();
        var runId = "run-C2";
        var windowStart = DateTimeOffset.UtcNow;

        await source.RegisterToolResultAsync(runId, "req-C2-1", succeeded: true);
        await source.RegisterToolResultAsync(runId, "req-C2-2", succeeded: true);
        await source.RegisterToolResultAsync(runId, "req-C2-3", succeeded: false);

        var metrics = await source.CollectAsync(runId, windowStart, DateTimeOffset.UtcNow);

        Assert.IsNotNull(metrics.ToolSuccessRate, "ToolSuccessRate 不应为 null。");
        Assert.AreEqual(2.0 / 3.0, metrics.ToolSuccessRate!.Value, 0.0001, "2/3 成功。");
    }

    [TestMethod]
    public async Task ExternalMetrics_RegisterSafetyEvent_ComputesViolationRate()
    {
        // 验证：注册 5 次安全事件（1 次违规）→ SafetyViolationRate = 0.2
        var source = new DefaultCanaryExternalMetricsSource();
        var runId = "run-C3";
        var windowStart = DateTimeOffset.UtcNow;

        for (int i = 0; i < 4; i++)
        {
            source.RegisterSafetyEvent(runId, violation: false);
        }
        source.RegisterSafetyEvent(runId, violation: true);

        var metrics = await source.CollectAsync(runId, windowStart, DateTimeOffset.UtcNow);

        Assert.IsNotNull(metrics.SafetyViolationRate, "SafetyViolationRate 不应为 null。");
        Assert.AreEqual(0.2, metrics.SafetyViolationRate!.Value, 0.0001, "1/5 违规 → 0.2。");
    }

    [TestMethod]
    public async Task ExternalMetrics_RegisterUserFeedback_ComputesAcceptanceAndQuality()
    {
        // 验证：注册用户反馈 → UserAcceptance + AnswerQuality 均值都正确计算
        var source = new DefaultCanaryExternalMetricsSource();
        var runId = "run-C4";
        var windowStart = DateTimeOffset.UtcNow;

        source.RegisterUserFeedback(runId, accepted: true, qualityScore: 0.8);
        source.RegisterUserFeedback(runId, accepted: false, qualityScore: 0.6);
        source.RegisterUserFeedback(runId, accepted: true, qualityScore: null); // 不计入 AnswerQuality

        var metrics = await source.CollectAsync(runId, windowStart, DateTimeOffset.UtcNow);

        Assert.IsNotNull(metrics.UserAcceptance, "UserAcceptance 不应为 null。");
        Assert.AreEqual(2.0 / 3.0, metrics.UserAcceptance!.Value, 0.0001, "2/3 接受。");
        Assert.IsNotNull(metrics.AnswerQuality, "AnswerQuality 不应为 null。");
        Assert.AreEqual(0.7, metrics.AnswerQuality!.Value, 0.0001, "(0.8+0.6)/2 = 0.7。");
    }

    [TestMethod]
    public async Task ExternalMetrics_CollectAsync_ReturnsEmpty_WhenNoDataRegistered()
    {
        // 验证：未注册任何样本时返回空指标（所有比率为 null, SampleCount=0）—— 优雅降级
        var source = new DefaultCanaryExternalMetricsSource();
        var windowStart = DateTimeOffset.UtcNow;

        var metrics = await source.CollectAsync("run-C5-empty", windowStart, DateTimeOffset.UtcNow);

        Assert.IsNull(metrics.TaskSuccessRate, "无数据时 TaskSuccessRate 应为 null。");
        Assert.IsNull(metrics.ToolSuccessRate, "无数据时 ToolSuccessRate 应为 null。");
        Assert.IsNull(metrics.SafetyViolationRate, "无数据时 SafetyViolationRate 应为 null。");
        Assert.AreEqual(0, metrics.SampleCount, "无数据时 SampleCount 应为 0。");
    }
}

// ===========================================================================
// 工作流 D：Model Activation — ActivationManager 代理 + 失败路径（5 项）
// ===========================================================================

[TestClass]
[TestCategory("R29-Closure")]
[TestCategory("Workflow-D")]
public sealed class WorkflowD_ModelActivationAcceptanceTests
{
    private const string SchemaVersion = "r29-closure-schema-v1";
    private const string ModelArtifactId = "r29-closure-model-v1";
    private const string ModelName = "r29-closure-test-model";
    private const string CalibrationVersion = "r29-closure-cal-v1";

    [TestMethod]
    public async Task ModelActivation_BeforeActivation_DelegatesToFallback()
    {
        // 验证：未激活时推理委托给 fallback（DeterministicBatchInferenceEngine），Kind 为 DeterministicReplay
        var manager = BuildManager();

        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = SchemaVersion,
                    Values = new Dictionary<string, object> { ["lexical_score"] = 0.5 }
                }
            }
        };

        var result = await manager.InferAsync(request);

        Assert.IsTrue(result.Succeeded, "fallback 推理应成功。");
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind);
        Assert.IsNull(manager.ActiveEngine, "未激活时 ActiveEngine 应为 null。");
        Assert.IsNull(manager.ActiveDescriptor, "未激活时 ActiveDescriptor 应为 null。");
    }

    [TestMethod]
    public async Task ModelActivation_ActivateAsync_UnknownArtifact_ReturnsFailure()
    {
        // 验证：激活未知 artifactId 时返回失败，且 manager 仍使用 fallback
        var manager = BuildManager();
        var options = BuildOptions();

        var result = await manager.ActivateAsync("nonexistent-artifact-id", options);

        Assert.IsFalse(result.Success, "未知 artifactId 激活应失败。");
        Assert.IsNotNull(result.Error, "失败时应提供 Error 信息。");
        Assert.IsNull(result.Descriptor, "失败时 Descriptor 应为 null。");
        Assert.IsNull(result.Engine, "失败时 Engine 应为 null。");
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind, "失败后仍使用 fallback。");
    }

    [TestMethod]
    public async Task ModelActivation_ActivateAsync_SchemaNotRegistered_RejectsActivation()
    {
        // 验证 P0-8：descriptor 引用未注册的 schema 版本 → 激活被拒绝
        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = "bad-schema-model",
            ModelName = "bad-schema-model",
            ModelVersion = "1.0.0",
            FeatureSchemaVersion = "unregistered-schema-v999",
            CalibrationVersion = CalibrationVersion,
            EngineKind = InferenceEngineKind.RealModel,
            ContentHash = "sha256:test",
            ArtifactPath = "/path/to/model.onnx",
            RegisteredAt = DateTimeOffset.UtcNow
        });

        var featureRegistry = new DefaultFeatureRegistry();
        // 故意不注册 "unregistered-schema-v999"
        var mockSession = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var factory = new MockSessionFactory(mockSession);
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();

        // P0-8 fail-closed：提供有效校准参数以便流程越过校准检查到达 schema 验证步骤
        var cal = new PlattCalibrationService();
        cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: "bad-schema-model", version: CalibrationVersion);

        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, cal);

        var result = await manager.ActivateAsync("bad-schema-model", BuildOptions());

        Assert.IsFalse(result.Success, "未注册 schema 应拒绝激活。");
        Assert.IsNotNull(result.SchemaValidationError, "应提供 schema 校验错误。");
        Assert.IsTrue(result.SchemaValidationError!.Contains("unregistered-schema-v999"),
            $"错误消息应包含未注册的 schema 版本号：{result.SchemaValidationError}");
        Assert.IsNull(manager.ActiveEngine, "拒绝后 ActiveEngine 应为 null。");
    }

    [TestMethod]
    public async Task ModelActivation_ActivateAsync_SessionCreationFails_ReturnsFailure()
    {
        // 验证：ONNX session 创建失败时激活返回失败（fail-safe 不切换引擎）
        var manager = BuildManagerWithFailingFactory();
        var options = BuildOptions();

        var result = await manager.ActivateAsync(ModelArtifactId, options);

        Assert.IsFalse(result.Success, "session 创建失败时激活应失败。");
        Assert.IsNull(result.Engine, "失败时 Engine 应为 null。");
        Assert.IsNull(manager.ActiveEngine, "失败后 manager 仍使用 fallback。");
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind);
    }

    [TestMethod]
    public async Task ModelActivation_RealOnnxModel_E2E_InconclusiveWhenMissing()
    {
        // 验证：真实 ONNX E2E 激活路径；CI 未下载模型时 Assert.Inconclusive 跳过
        // 对齐 P0-6 设计：当 ONNX 文件不存在时跳过，避免误报
        var repoRoot = FindRepoRoot();
        var bgePath = Path.Combine(repoRoot, "src", "ContextCore.Embedding", "Models",
            "bge-small-zh-v1.5", "onnx", "model_quantized.onnx");
        var miniLmPath = Path.Combine(repoRoot, "src", "ContextCore.Embedding", "Models",
            "all-MiniLM-L6-v2", "onnx", "model_quantized.onnx");

        if (!File.Exists(bgePath) && !File.Exists(miniLmPath))
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 激活测试。");
            return;
        }

        // 模型存在时验证 factory 能加载真实文件创建 session（读取真实 metadata）
        var modelPath = File.Exists(bgePath) ? bgePath : miniLmPath;
        var factory = new OnnxRuntimeInferenceSessionFactory();
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input_ids",
            ScoreOutputName = "last_hidden_state",
            ModelPath = modelPath,
            ApplySigmoid = false
        };

        var session = await factory.CreateAsync(options);

        Assert.IsNotNull(session, "真实 ONNX 文件应能创建 session。");
        await session.DisposeAsync();
    }

    private static ModelActivationManager BuildManager()
    {
        var registry = BuildRegistry();
        var featureRegistry = BuildFeatureRegistry();
        var factory = new MockSessionFactory(
            new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>()));
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        return new ModelActivationManager(registry, calValidator, featureRegistry, factory, fallback);
    }

    private static ModelActivationManager BuildManagerWithFailingFactory()
    {
        var registry = BuildRegistry();
        var featureRegistry = BuildFeatureRegistry();
        var factory = new FailingSessionFactory();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        // P0-8 fail-closed：提供有效校准参数以便流程越过校准检查到达 session 创建步骤
        var cal = new PlattCalibrationService();
        cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: ModelArtifactId, version: CalibrationVersion);
        return new ModelActivationManager(registry, calValidator, featureRegistry, factory, fallback, cal);
    }

    private static InMemoryModelArtifactRegistry BuildRegistry()
    {
        var registry = new InMemoryModelArtifactRegistry();
        registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = ModelArtifactId,
            ModelName = ModelName,
            ModelVersion = "1.0.0",
            FeatureSchemaVersion = SchemaVersion,
            CalibrationVersion = CalibrationVersion,
            EngineKind = InferenceEngineKind.RealModel,
            ContentHash = "sha256:test",
            ArtifactPath = "/path/to/model.onnx",
            RegisteredAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();
        return registry;
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
                new FeatureDefinition
                {
                    Name = "lexical_score",
                    Type = FeatureType.Numeric,
                    IsRequired = false,
                    DefaultValue = "0"
                },
                new FeatureDefinition
                {
                    Name = "semantic_score",
                    Type = FeatureType.Numeric,
                    IsRequired = false,
                    DefaultValue = "0"
                }
            }
        });
        return registry;
    }

    private static OnnxInferenceEngineOptions BuildOptions() => new()
    {
        InputTensorName = "input",
        ScoreOutputName = "logits"
    };

    private static string FindRepoRoot()
    {
        // 测试 assembly 位于 <repo>/tests/ContextCore.Tests/bin/<config>/<tfm>/
        // 向上查找直到找到 src 目录
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return AppContext.BaseDirectory;
    }
}

// ===========================================================================
// 工作流 E：Agent Intelligence — 状态机 + 哈希链 + CAS Run Store（5 项）
// ===========================================================================

[TestClass]
[TestCategory("R29-Closure")]
[TestCategory("Workflow-E")]
public sealed class WorkflowE_AgentIntelligenceAcceptanceTests
{
    [TestMethod]
    public void StateMachine_ValidTransition_CreatedToContextBuilding_Succeeds()
    {
        // 验证：合法前向推进 Created → ContextBuilding 不抛异常
        AgentRunStateMachine.ValidateTransition(
            AgentRunState.Created, AgentRunState.ContextBuilding);
    }

    [TestMethod]
    public void StateMachine_InvalidTransition_TerminalStateToNonTerminal_Throws()
    {
        // 验证：终态（Completed）不可流转到非终态（ContextBuilding）
        Assert.ThrowsException<InvalidOperationException>(() =>
            AgentRunStateMachine.ValidateTransition(
                AgentRunState.Completed, AgentRunState.ContextBuilding));
    }

    [TestMethod]
    public void StateMachine_AnyState_CanTransitionToFailedOrCancelled()
    {
        // 验证：任意非终态可跳转到 Failed 或 Cancelled（异常 / 取消短路）
        foreach (AgentRunState from in Enum.GetValues<AgentRunState>())
        {
            if (AgentRunStateMachine.IsTerminalState(from)) continue;

            AgentRunStateMachine.ValidateTransition(from, AgentRunState.Failed);
            AgentRunStateMachine.ValidateTransition(from, AgentRunState.Cancelled);
        }
    }

    [TestMethod]
    public async Task EventChain_BuildEvent_ComputesAndVerifiesContentHash()
    {
        // 验证：AgentRunEventChain.BuildEvent 自动计算 ContentHash；
        //       VerifyContentHash 重算后与存储值一致
        var evt = AgentRunEventChain.BuildEvent(
            runId: "run-E4",
            workspaceId: "ws-E4",
            sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: "{\"task\":\"test\"}",
            prevChainHash: null);

        Assert.IsFalse(string.IsNullOrEmpty(evt.ContentHash), "ContentHash 应被自动计算。");
        Assert.AreEqual(64, evt.ContentHash!.Length, "SHA-256 hex 应为 64 字符。");
        Assert.IsNull(evt.PrevChainHash, "链头事件 PrevChainHash 应为 null。");
        Assert.IsTrue(AgentRunEventChain.VerifyContentHash(evt), "VerifyContentHash 应通过。");
    }

    [TestMethod]
    public async Task AgentRunStore_TransitionStateAsync_CAS_FailsOnStateMismatch()
    {
        // 验证：expected-state CAS——期望当前状态不匹配时抛 InvalidOperationException
        var store = new InMemoryAgentRunStore();
        var runId = "run-E5";
        var workspaceId = "ws-E5";
        var now = DateTimeOffset.UtcNow;

        await store.CreateAsync(new AgentRun
        {
            RunId = runId,
            WorkspaceId = workspaceId,
            SessionId = "session-E5",
            Task = "test task",
            State = AgentRunState.Created,
            Turn = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        // 期望当前状态为 ModelCalling，实际为 Created → CAS 失败
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => store.TransitionStateAsync(
                workspaceId, runId,
                expectedCurrentState: AgentRunState.ModelCalling,
                newState: AgentRunState.AwaitingApproval).AsTask());

        // 期望正确时 CAS 成功
        await store.TransitionStateAsync(
            workspaceId, runId,
            expectedCurrentState: AgentRunState.Created,
            newState: AgentRunState.ContextBuilding);

        var updated = await store.GetAsync(workspaceId, runId);
        Assert.AreEqual(AgentRunState.ContextBuilding, updated!.State, "CAS 成功后状态应推进。");
    }
}

// ===========================================================================
// 工作流 F：Performance Truth — 组件健康 + benchmark 门控（5 项）
// ===========================================================================

[TestClass]
[TestCategory("R29-Closure")]
[TestCategory("Workflow-F")]
public sealed class WorkflowF_PerformanceTruthAcceptanceTests
{
    [TestMethod]
    public void ComponentRegistry_NewComponent_ReturnsHealthyByDefault()
    {
        // 验证：未记录任何样本时组件状态为 Healthy（冷启动不触发回退）
        var registry = new DefaultComponentHealthRegistry();

        var health = registry.GetComponentHealth(ComponentKind.Inference, "scope-F1");

        Assert.AreEqual(ComponentHealthState.Healthy, health, "新组件默认应为 Healthy。");
        Assert.IsFalse(registry.ShouldFallbackComponent(ComponentKind.Inference, "scope-F1"),
            "无样本时不应触发回退。");
    }

    [TestMethod]
    public void ComponentRegistry_ConsecutiveFailures_TriggersFallback()
    {
        // 验证：连续失败累积到 MinSamplesBeforeFallback 时触发 FallbackActive
        var registry = new DefaultComponentHealthRegistry();
        var scope = "scope-F2";

        // 默认 MinSamplesBeforeFallback = 3，连续 3 次失败应触发回退
        registry.RecordComponentTime(ComponentKind.Inference, durationMs: 10, succeeded: false, scopeKey: scope);
        registry.RecordComponentTime(ComponentKind.Inference, durationMs: 10, succeeded: false, scopeKey: scope);
        Assert.IsFalse(registry.ShouldFallbackComponent(ComponentKind.Inference, scope),
            "2 次失败不足 MinSamplesBeforeFallback，不应触发回退。");

        registry.RecordComponentTime(ComponentKind.Inference, durationMs: 10, succeeded: false, scopeKey: scope);
        Assert.IsTrue(registry.ShouldFallbackComponent(ComponentKind.Inference, scope),
            "3 次连续失败应触发 FallbackActive。");
        Assert.AreEqual(ComponentHealthState.FallbackActive,
            registry.GetComponentHealth(ComponentKind.Inference, scope));
    }

    [TestMethod]
    public void ComponentRegistry_SelfHeals_AfterRecoverySamples()
    {
        // 验证：FallbackActive 后累积 RecoverySamplesRequired 个低于阈值样本 → 自愈为 Healthy
        var registry = new DefaultComponentHealthRegistry();
        var scope = "scope-F3";

        // 触发回退
        for (int i = 0; i < 3; i++)
        {
            registry.RecordComponentTime(ComponentKind.Inference, durationMs: 10, succeeded: false, scopeKey: scope);
        }
        Assert.IsTrue(registry.ShouldFallbackComponent(ComponentKind.Inference, scope));

        // 累积足够低于阈值的成功样本 → 自愈
        var policy = registry.Options.GetPolicy(ComponentKind.Inference);
        for (int i = 0; i < policy.RecoverySamplesRequired; i++)
        {
            registry.RecordComponentTime(ComponentKind.Inference, durationMs: 1, succeeded: true, scopeKey: scope);
        }

        Assert.IsFalse(registry.ShouldFallbackComponent(ComponentKind.Inference, scope),
            "累积 RecoverySamplesRequired 个低阈值样本后应自愈。");
    }

    [TestMethod]
    public void ComponentRegistry_ScopeIsolation_PreventsCrossScopeLeakage()
    {
        // 验证：scope 隔离——一个 scope 的组件触发回退不影响另一 scope
        var registry = new DefaultComponentHealthRegistry();
        var scopeA = "scope-F4-A";
        var scopeB = "scope-F4-B";

        // scope-A 连续失败触发回退
        for (int i = 0; i < 3; i++)
        {
            registry.RecordComponentTime(ComponentKind.Inference, durationMs: 10, succeeded: false, scopeKey: scopeA);
        }

        // scope-B 始终健康
        registry.RecordComponentTime(ComponentKind.Inference, durationMs: 5, succeeded: true, scopeKey: scopeB);

        Assert.IsTrue(registry.ShouldFallbackComponent(ComponentKind.Inference, scopeA),
            "scope-A 应触发回退。");
        Assert.IsFalse(registry.ShouldFallbackComponent(ComponentKind.Inference, scopeB),
            "scope-B 不应受 scope-A 影响（scope 隔离）。");
    }

    [TestMethod]
    public void BenchmarkCompareScript_ContainsFalsePositiveSuppressionParameters()
    {
        // 验证 P0-9：benchmark-compare.sh 包含四层假阳性抑制参数
        // （NOISE_FLOOR_PCT / MIN_SAMPLE_COUNT / CONFIDENCE_SIGMA / IO_BOUND_THRESHOLD_PCT）
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "benchmark-compare.sh");

        if (!File.Exists(scriptPath))
        {
            Assert.Inconclusive("未找到 scripts/benchmark-compare.sh，跳过脚本门控测试。");
            return;
        }

        var content = File.ReadAllText(scriptPath);

        // 四层假阳性抑制参数必须存在
        Assert.IsTrue(content.Contains("NOISE_FLOOR_PCT"),
            "脚本必须包含 NOISE_FLOOR_PCT（噪声底抑制）。");
        Assert.IsTrue(content.Contains("MIN_SAMPLE_COUNT"),
            "脚本必须包含 MIN_SAMPLE_COUNT（样本不足跳过）。");
        Assert.IsTrue(content.Contains("CONFIDENCE_SIGMA"),
            "脚本必须包含 CONFIDENCE_SIGMA（置信区间检查）。");
        Assert.IsTrue(content.Contains("IO_BOUND_THRESHOLD_PCT"),
            "脚本必须包含 IO_BOUND_THRESHOLD_PCT（I/O 宽松阈值）。");

        // 退出码语义必须存在
        Assert.IsTrue(content.Contains("regression_found=false"),
            "脚本必须输出 regression_found=false 标记（无回归）。");
        Assert.IsTrue(content.Contains("regression_found=true"),
            "脚本必须输出 regression_found=true 标记（检测到回归）。");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "scripts")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
