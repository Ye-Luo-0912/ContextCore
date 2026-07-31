using System.Net;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.IntegrationTests.TestFixtures;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Adapters;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// R29-Hard-Gate P5：Production Evidence 真实 HTTP Adapter 完整循环 E2E 测试
//
// 目标：补齐 WP-5 关键缺口——验证真实 HTTP Adapter（OpenAiCompatibleModelAdapter）
// 的完整 Agent 循环（User → Assistant tool_call → Tool result → Assistant final），
// 不使用 ScriptedModelTransport 或 DeterministicAgentModelTransport。
//
// 与 R29H_ProductionEvidenceE2ETests 的区别：
//   - 现有测试使用 ScriptedModelTransport（直接返回预设 AgentModelResponse，绕过 HTTP 层）。
//   - 本测试使用真实 ModelGatewayAgentModelTransport → ConfigurableModelGateway →
//     OpenAiCompatibleModelAdapter → HttpClient(StubHttpMessageHandler) 完整链路，
//     验证：HTTP 请求构造（tools 参数 + messages + Authorization）→ HTTP 响应解析
//     （tool_calls + finish_reason + usage + cost）→ AgentRunActor 循环 → Tool 分派 →
//     第二轮 HTTP 调用 → 最终答案。
//
// 设计原则：
//   - 使用 PostgresE2EFixture 共享 PG 容器（真实持久化 Run/Event/Journal）。
//   - 使用 StubHttpMessageHandler（队列模式）模拟 LLM HTTP 响应：第一次返回 tool_calls，
//     第二次返回 stop。
//   - 使用真实 RealToolDispatcher + RecordingToolHandler（真实 Tool 执行）。
//   - 使用真实 ConfigurableModelGateway + OpenAiCompatibleModelAdapter（真实 HTTP 请求构造与响应解析）。
//   - 配置 InputTokenPricePerMillionUsd / OutputTokenPricePerMillionUsd 验证成本计算。
//   - Docker/Postgres 不可用时 Assert.Inconclusive 跳过（不证明生产证据通过）。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
[TestCategory("RealHttpE2E")]
public sealed class R29H_ProductionEvidenceRealHttpE2ETests : IAsyncDisposable
{
    private readonly PostgresE2EFixture _pg = new();

    [TestInitialize]
    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _pg.DisposeAsync();
    }

    private static bool ShouldSkip(PostgresE2EFixture pg) => pg.ShouldSkip;

    // =======================================================================
    // 测试：真实 HTTP Adapter 完整 Agent 循环
    //   User → Assistant tool_call → Tool result → Assistant final
    // =======================================================================

    [TestMethod]
    public async Task E2E_RealHttpAdapter_FullAgentLoop_ToolCallThenFinalAnswer()
    {
        if (ShouldSkip(_pg)) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        // ── 1. 构建真实 PG stores ──
        var (factory, migrationRunner, serializer) = _pg.CreateInfrastructure("realhttp_");
        try
        {
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // ── 2. 构建真实 Tool 执行链 ──
            var toolHandler = new RecordingToolHandler("search", "搜索结果：找到 3 篇相关文档。");
            var dispatcher = new RealToolDispatcher(new[] { (IToolHandler)toolHandler });
            dispatcher.Freeze();
            var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);

            // ── 3. 构建 StubHttpMessageHandler：第一次返回 tool_calls，第二次返回 stop ──
            // OpenAI 兼容响应格式：choices[0].message.tool_calls + finish_reason="tool_calls"
            var httpHandler = new StubHttpMessageHandler(new[]
            {
                StubHttpMessageHandler.Json(BuildToolCallHttpResponse(
                    toolCallId: "call_test_001",
                    toolName: "search",
                    argumentsJson: """{"query":"查找文档"}""",
                    assistantContent: "需要搜索文档",
                    inputTokens: 50,
                    outputTokens: 20)),
                StubHttpMessageHandler.Json(BuildFinalAnswerHttpResponse(
                    finalContent: "基于搜索结果，已找到 3 篇相关文档。任务完成。",
                    inputTokens: 80,
                    outputTokens: 30))
            });

            // ── 4. 构建真实 HTTP Adapter 链路 ──
            // ModelEndpointOptions：配置 openai-compatible provider + token 单价（验证成本计算）
            var modelOptions = new ModelEndpointOptions
            {
                Name = "test-realhttp-model",
                Provider = "openai-compatible",
                Endpoint = "https://stub-llm.test/v1",
                ApiKey = "test-api-key-realhttp",
                Enabled = true,
                Timeout = TimeSpan.FromSeconds(30),
                // WP-0 需求 7：配置 token 单价，验证 ParseChatResponse 的成本计算
                InputTokenPricePerMillionUsd = 1.0,
                OutputTokenPricePerMillionUsd = 2.0,
                Metadata = new Dictionary<string, string>
                {
                    ["model"] = "test-llm-model"
                }
            };

            // OpenAiCompatibleModelAdapter：注入 HttpClient(StubHttpMessageHandler)
            using var httpClient = new HttpClient(httpHandler);
            var adapter = new OpenAiCompatibleModelAdapter(modelOptions, httpClient);

            // ConfigurableModelGateway：真实路由 + 真实 adapter
            var gatewayOptions = new ModelGatewayOptions
            {
                Models = new[] { modelOptions }
            };
            var gateway = new ConfigurableModelGateway(gatewayOptions, new[] { adapter });

            // ModelGatewayAgentModelTransport：真实 IAgentModelTransport（非 Scripted/Deterministic）
            var transport = new ModelGatewayAgentModelTransport(gateway);

            // ── 5. 构建 Run ──
            var run = BuildRun("search 查找文档内容", turnBudget: new AgentTurnBudget
            {
                MaxTurns = 10,
                TurnsUsed = 0,
                MaxModelCalls = 5
            });
            await runStore.CreateAsync(run);

            // ── 6. 构建 Actor 并执行 ──
            var actor = new AgentRunActor(
                runStore, eventStore, transport,
                new DefaultAgentLoopPolicy(),
                dispatcher,
                durableToolExecutor: durableExecutor);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await actor.ExecuteAsync(run, cts.Token);

            // ── 断言 1：Run 进入 Completed 终态 ──
            var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(finalRun, "应能从 store 取回 Run。");
            Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
                $"Run 应进入 Completed 终态，实际 {finalRun.State}。");

            // ── 断言 2：最终答案已持久化且包含搜索结果摘要 ──
            Assert.IsFalse(string.IsNullOrEmpty(finalRun.FinalAnswer),
                "Completed 状态的 Run 应有最终答案。");
            Assert.IsTrue(finalRun.FinalAnswer!.Contains("3 篇相关文档"),
                $"最终答案应包含搜索结果摘要，实际：{finalRun.FinalAnswer}");

            // ── 断言 3：Tool 被真实执行了一次 ──
            Assert.AreEqual(1, toolHandler.InvocationCount,
                $"RecordingToolHandler 应被调用 1 次（真实 Tool 执行），实际 {toolHandler.InvocationCount}。");

            // ── 断言 4：真实 HTTP 请求被发送了两次（tool_call + final answer 两轮）──
            Assert.AreEqual(2, httpHandler.CapturedRequests.Count,
                $"StubHttpMessageHandler 应捕获 2 次 HTTP 请求（tool_call + final），实际 {httpHandler.CapturedRequests.Count}。");

            // ── 断言 5：HTTP 请求 URI 为 /chat/completions（验证 BuildChatCompletionsUri）──
            var firstRequest = httpHandler.CapturedRequests[0];
            Assert.IsNotNull(firstRequest.RequestUri, "第一次请求应有 URI。");
            Assert.AreEqual("https://stub-llm.test/v1/chat/completions", firstRequest.RequestUri!.ToString(),
                $"请求 URI 应为 https://stub-llm.test/v1/chat/completions，实际 {firstRequest.RequestUri}。");
            Assert.AreEqual(HttpMethod.Post, firstRequest.Method,
                "请求方法应为 POST。");

            // ── 断言 6：Authorization 头为 Bearer test-api-key-realhttp ──
            Assert.IsNotNull(firstRequest.Headers.Authorization, "请求应有 Authorization 头。");
            Assert.AreEqual("Bearer", firstRequest.Headers.Authorization!.Scheme,
                "Authorization scheme 应为 Bearer。");
            Assert.AreEqual("test-api-key-realhttp", firstRequest.Headers.Authorization.Parameter,
                "Authorization parameter 应为配置的 ApiKey。");

            // ── 断言 7：第一次请求体包含 tools 参数（原生 function calling）──
            var firstBody = await ReadRequestBodyAsync(firstRequest);
            using var firstPayload = JsonDocument.Parse(firstBody);
            Assert.IsTrue(firstPayload.RootElement.TryGetProperty("tools", out var toolsEl),
                "第一次请求体应包含 tools 参数（原生 function calling）。");
            Assert.AreEqual(JsonValueKind.Array, toolsEl.ValueKind, "tools 应为数组。");
            Assert.AreEqual(1, toolsEl.GetArrayLength(), "tools 数组应包含 1 个 tool 定义。");
            var firstTool = toolsEl[0];
            Assert.AreEqual("function", firstTool.GetProperty("type").GetString(),
                "tool.type 应为 function。");
            Assert.AreEqual("search", firstTool.GetProperty("function").GetProperty("name").GetString(),
                "tool.function.name 应为 search。");

            // ── 断言 8：第二次请求体包含 tool 角色消息（Tool 观察结果回传）──
            var secondRequest = httpHandler.CapturedRequests[1];
            var secondBody = await ReadRequestBodyAsync(secondRequest);
            using var secondPayload = JsonDocument.Parse(secondBody);
            var messages = secondPayload.RootElement.GetProperty("messages");
            // 第二次请求消息至少包含：User, Assistant(tool_calls), Tool(result)（System 可选）
            Assert.IsTrue(messages.GetArrayLength() >= 3,
                $"第二次请求应至少包含 3 条消息（User/Assistant+tool_calls/Tool），实际 {messages.GetArrayLength()}。");

            // 验证存在 Tool 角色消息
            var hasToolMessage = false;
            for (var i = 0; i < messages.GetArrayLength(); i++)
            {
                if (messages[i].GetProperty("role").GetString() == "tool")
                {
                    hasToolMessage = true;
                    Assert.IsTrue(messages[i].TryGetProperty("tool_call_id", out var tcid),
                        "Tool 消息应包含 tool_call_id 字段。");
                    Assert.AreEqual("call_test_001", tcid.GetString(),
                        $"tool_call_id 应为 call_test_001，实际 {tcid.GetString()}。");
                    break;
                }
            }
            Assert.IsTrue(hasToolMessage, "第二次请求应包含 Tool 角色消息（Tool 观察结果回传）。");

            // ── 断言 9：ModelCallsUsed = 2（一次 Tool 调用 + 一次最终答案）──
            Assert.AreEqual(2, finalRun.ModelCallsUsed,
                $"ModelCallsUsed 应为 2（Tool 调用 + 最终答案），实际 {finalRun.ModelCallsUsed}。");

            // ── 断言 10：事件流完整且哈希链无断裂 ──
            var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 10000);
            Assert.IsTrue(events.Count > 0, "事件流应非空。");
            Assert.IsNull(events[0].PrevChainHash, "链头事件的 PrevChainHash 应为 null。");
            for (var i = 1; i < events.Count; i++)
            {
                Assert.AreEqual(events[i - 1].ContentHash, events[i].PrevChainHash,
                    $"事件 {i} 的 PrevChainHash 应指向前一事件的 ContentHash（哈希链断裂）。");
            }

            var eventTypes = events.Select(e => e.EventType).ToList();
            CollectionAssert.Contains(eventTypes, AgentRunEventType.RunCreated, "应有 RunCreated 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ModelCallStarted, "应有 ModelCallStarted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ModelCallCompleted, "应有 ModelCallCompleted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ToolCallStarted, "应有 ToolCallStarted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ToolCallCompleted, "应有 ToolCallCompleted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ObservationAppended, "应有 ObservationAppended 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.RunCompleted, "应有 RunCompleted 事件。");

            // ── 断言 11：成本被正确计算（WP-0 需求 7）──
            // 第一次调用：input=50, output=20, cost = (50*1.0 + 20*2.0)/1_000_000 = 90/1_000_000
            // 第二次调用：input=80, output=30, cost = (80*1.0 + 30*2.0)/1_000_000 = 140/1_000_000
            // 总成本 = 230/1_000_000 ≈ 0.00023
            Assert.IsNotNull(finalRun.CostBudget, "CostBudget 不应为 null（BuildRun 已注入）。");
            var costBudget = finalRun.CostBudget!;
            Assert.IsTrue(costBudget.CostUsedUsd > 0,
                $"CostUsedUsd 应 > 0（成本被计算），实际 {costBudget.CostUsedUsd}。");
            const double expectedCost = (50 * 1.0 + 20 * 2.0 + 80 * 1.0 + 30 * 2.0) / 1_000_000.0;
            Assert.AreEqual(expectedCost, costBudget.CostUsedUsd, expectedCost * 0.01,
                $"CostUsedUsd 应约为 {expectedCost}（两轮调用的 token 单价累积），实际 {costBudget.CostUsedUsd}。");

            // ── 断言 12：Token 消耗正确累积 ──
            // 第一次：input=50, output=20 → 70 tokens
            // 第二次：input=80, output=30 → 110 tokens
            // 总计：180 tokens
            Assert.AreEqual(180, costBudget.TokensUsed,
                $"TokensUsed 应为 180（两轮调用的 input+output 累积），实际 {costBudget.TokensUsed}。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private static AgentRun BuildRun(
        string task,
        AgentTurnBudget? turnBudget = null,
        AgentCostBudget? costBudget = null) => new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-realhttp-prodevidence",
            SessionId = "session-realhttp-prodevidence",
            Task = task,
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = turnBudget,
            CostBudget = costBudget ?? new AgentCostBudget
            {
                MaxTokens = 10000,
                TokensUsed = 0,
                MaxCostUsd = 10.0,
                CostUsedUsd = 0.0
            }
        };

    /// <summary>
    /// 构建 OpenAI 兼容的 tool_calls HTTP 响应（finish_reason="tool_calls"）。
    /// </summary>
    private static object BuildToolCallHttpResponse(
        string toolCallId,
        string toolName,
        string argumentsJson,
        string assistantContent,
        int inputTokens,
        int outputTokens) => new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = assistantContent,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = toolCallId,
                                type = "function",
                                function = new
                                {
                                    name = toolName,
                                    arguments = argumentsJson
                                }
                            }
                        }
                    },
                    finish_reason = "tool_calls"
                }
            },
            usage = new
            {
                prompt_tokens = inputTokens,
                completion_tokens = outputTokens,
                total_tokens = inputTokens + outputTokens
            }
        };

    /// <summary>
    /// 构建 OpenAI 兼容的 stop HTTP 响应（finish_reason="stop"，最终答案）。
    /// </summary>
    private static object BuildFinalAnswerHttpResponse(
        string finalContent,
        int inputTokens,
        int outputTokens) => new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = finalContent
                    },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = inputTokens,
                completion_tokens = outputTokens,
                total_tokens = inputTokens + outputTokens
            }
        };

    /// <summary>读取 HttpRequestMessage 的请求体字符串。</summary>
    private static async Task<string> ReadRequestBodyAsync(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return string.Empty;
        }
        return await request.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // ── 测试 stub ─────────────────────────────────────────────────────────

    /// <summary>
    /// 录制 Tool 调用的 IToolHandler 实现。
    /// 记录调用次数，返回预设的成功结果。用于验证 Tool 被真实执行（而非 mock stub）。
    /// </summary>
    private sealed class RecordingToolHandler : IToolHandler
    {
        private readonly string _resultContent;
        private int _invocationCount;

        public string ToolName { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public RecordingToolHandler(string toolName, string resultContent)
        {
            ToolName = toolName;
            _resultContent = resultContent;
        }

        public ValueTask<ToolHandlerResult> HandleAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = _resultContent,
                SideEffect = ToolSideEffect.None
            });
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync();
    }
}
