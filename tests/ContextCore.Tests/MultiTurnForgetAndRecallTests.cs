using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// 多轮「忘掉再搜回」端到端夹具（本阶段产品验收）：
// 1. 库里有两条笔记：keep-budget（长正文，占预算）与 AmberCompass-17（短标题实体）。
// 2. 第一轮任务同时命中两条；分配器预算只够 keep-budget，AmberCompass-17 被裁掉。
//    断言短的不在下一轮种子，其实体词进入下一轮找回问句。
// 3. 第二轮找回问句自动带上 AmberCompass-17，搜索再次把它召回并选中。
// 4. 失败工具 id:gone 后，第三轮排除集与种子都不再出现 gone。
// 全链路用 InMemory store + 确定性模型 + Echo 工具，不调真实 LLM、不用 embedding。

[TestClass]
[TestCategory("Agent-Run-Full-Loop")]
public sealed class MultiTurnForgetAndRecallTests
{
    [TestMethod]
    public async Task ForgetThenRecall_EndToEnd()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "keep-budget",
            WorkspaceId = "ws-recall",
            CollectionId = "col-recall",
            Type = "note",
            Title = "keep-budget",
            // 长正文 + 持久化内容长度：候选层（IncludeContent=false）按元数据估算 TokenCost，
            // 让第一轮预算只够它一条，分配器把短条目裁掉。
            Content = string.Concat(Enumerable.Repeat("budget allocation strategy planning note ", 2000)),
            Metadata = new Dictionary<string, string>
            {
                [ContentMetadataKeys.TsRank] = "99",
                [ContentMetadataKeys.ContentLength] = "40000"
            }
        });
        await store.SaveAsync(new ContextItem
        {
            Id = "AmberCompass-17",
            WorkspaceId = "ws-recall",
            CollectionId = "col-recall",
            Type = "note",
            // 标题词元含「手册」：任务 "keep-budget 手册" 能命中它，但任务不含 AmberCompass-17 整词，
            // 这样被裁后的找回问句不会被任务整句的覆盖检查挡掉。
            Title = "AmberCompass-17 手册",
            Content = "short body"
        });

        var runtime = new RecordingRealRuntime(BuildRuntime(store));
        var planner = new AdaptiveRetrievalPlanner(
            new DefaultAgentRetrievalQueryPlanner(), new InMemoryRetrievalPlanFeedbackStore());

        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-recall-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-recall",
            CollectionId = "col-recall",
            SessionId = "session-recall",
            Task = "keep-budget 手册",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 8, TurnsUsed = 0, MaxModelCalls = 5 }
        };
        await runStore.CreateAsync(run);

        var actor = new AgentRunActor(
            runStore, eventStore, new ScriptedModelTransport(),
            new DefaultAgentLoopPolicy(), new ScriptedToolDispatcher(),
            decisionRuntime: runtime, adaptivePlanner: planner,
            modelContextProjector: new DefaultAgentModelContextProjector());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsTrue(runtime.Requests.Count >= 3, "应跑满三轮上下文构建（第一轮选中、第二轮找回、第三轮排除）。");

        // ── 第一轮：选中长的、裁掉短的 ──
        var first = runtime.Executions[0].Decision;
        Assert.IsTrue(
            first.SelectedEnvelopes.Any(e => e.CanonicalKey.EntityId == "keep-budget"),
            "第一轮应选中长正文条目 keep-budget。");
        Assert.IsFalse(
            first.SelectedEnvelopes.Any(e => e.CanonicalKey.EntityId == "AmberCompass-17"),
            "第一轮不应选中短条目 AmberCompass-17。");
        Assert.IsTrue(
            first.DroppedEnvelopes.Any(e => e.CanonicalKey.EntityId == "AmberCompass-17"),
            "第一轮分配器应裁掉 AmberCompass-17（预算不够）。");
        Assert.AreEqual(0, (runtime.Requests[0].SeedWorkingSet?.Envelopes ?? Array.Empty<ContextCandidateEnvelope>()).Count,
            "首轮没有上一轮种子。");

        // ── 第二轮：找回问句自动带上被裁条目，搜索再次召回并选中 ──
        Assert.IsFalse(
            (runtime.Requests[1].SeedWorkingSet?.Envelopes ?? Array.Empty<ContextCandidateEnvelope>())
                .Any(e => e.CanonicalKey.EntityId == "AmberCompass-17"),
            "被裁掉的短条目不应进入下一轮种子。");
        CollectionAssert.Contains(
            runtime.Requests[1].RetrievalInput!.QueryTexts.ToList(),
            "AmberCompass-17",
            "找回问句应把被裁条目的实体词写进第二轮分条检索。");
        Assert.IsTrue(
            runtime.Executions[1].Decision.SelectedEnvelopes.Any(e => e.CanonicalKey.EntityId == "AmberCompass-17"),
            "第二轮搜索应把 AmberCompass-17 再次找回并选中。");

        // ── 第三轮：失败工具 id:gone 后，排除集与种子都不再出现 gone ──
        CollectionAssert.Contains(
            runtime.Requests[2].RetrievalInput!.ExcludedIds.ToList(),
            "gone",
            "失败工具观察里的 id:gone 应进入第三轮排除集。");
        Assert.IsFalse(
            (runtime.Requests[2].SeedWorkingSet?.Envelopes ?? Array.Empty<ContextCandidateEnvelope>())
                .Any(e => e.CanonicalKey.EntityId == "gone"),
            "第三轮种子不应包含 gone。");
        Assert.IsFalse(
            runtime.Executions[2].Decision.SelectedEnvelopes.Any(e => e.CanonicalKey.EntityId == "gone"),
            "第三轮决策不应选中 gone。");
    }

    private static DefaultContextDecisionRuntime BuildRuntime(InMemoryContextStore store)
    {
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator());

        return new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: new DefaultResolvedPolicyProvider(),
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: new ICandidateProvider[]
            {
                new LexicalCandidateProvider(store, new DefaultContextTokenizerResolver())
            },
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()));
    }

    // ── 记录真实请求与执行结果的运行时包装 ────────────────────────────

    private sealed class RecordingRealRuntime : IContextDecisionRuntime
    {
        private readonly IContextDecisionRuntime _inner;

        public RecordingRealRuntime(IContextDecisionRuntime inner) => _inner = inner;

        public List<ContextDecisionRuntimeRequest> Requests { get; } = new();

        public List<ContextDecisionExecutionResult> Executions { get; } = new();

        public ValueTask<ContextDecisionResult> ExecuteAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var result = _inner.ExecuteAsync(request, cancellationToken).AsTask().GetAwaiter().GetResult();
            return ValueTask.FromResult(result);
        }

        public async ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var execution = await _inner.ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);
            Executions.Add(execution);
            return execution;
        }
    }

    // ── 脚本化模型：前两次调 Echo 工具，第三次给最终答案 ───────────────

    private sealed class ScriptedModelTransport : IAgentModelTransport
    {
        private int _calls;

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, string context, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                return ValueTask.FromResult(MakeToolCall("echo", "AmberCompass-17"));
            }
            if (_calls == 2)
            {
                return ValueTask.FromResult(MakeToolCall("echo", "gone"));
            }
            return ValueTask.FromResult(new AgentModelResponse
            {
                Content = "任务完成。",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 10,
                Duration = TimeSpan.FromMilliseconds(5),
                InputTokens = 5,
                OutputTokens = 5,
                ModelId = "scripted"
            });
        }

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => CallAsync(runId, AgentMessage.Serialize(messages), cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(
            AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);

        private static AgentModelResponse MakeToolCall(string toolName, string query) => new()
        {
            Content = $"需要调用 Tool: {toolName}",
            ToolCalls = new[]
            {
                new AgentToolCallRequest
                {
                    ToolName = toolName,
                    Arguments = JsonSerializer.Serialize(new { query })
                }
            },
            IsFinalAnswer = false,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(10),
            InputTokens = 5,
            OutputTokens = 5,
            ModelId = "scripted"
        };
    }

    // ── 脚本化工具：第一次成功（找到 AmberCompass-17），第二次失败（id:gone）──

    private sealed class ScriptedToolDispatcher : IToolDispatcher
    {
        private int _calls;

        public IReadOnlySet<string> SupportedTools { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "echo" };

        public ToolDescriptor? GetDescriptor(string toolName) => null;

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls++;
            if (_calls == 1)
            {
                return ValueTask.FromResult(new ToolDispatchResult
                {
                    Succeeded = true,
                    Result = "found AmberCompass-17",
                    Duration = TimeSpan.Zero,
                    SideEffect = ToolSideEffect.None
                });
            }
            return ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = false,
                Result = string.Empty,
                Error = "未找到 id:gone",
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
        }
    }
}
