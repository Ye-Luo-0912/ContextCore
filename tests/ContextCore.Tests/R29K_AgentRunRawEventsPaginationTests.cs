using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Client;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ContextCore.Tests;

/// <summary>
/// 管理员 raw 事件游标分页 + SSE 终态补读。
/// 验证 /api/agents/runs/{id}/events/raw 的分页契约
/// （Items / NextSequence / HasMore、服务端页大小上限 clamp，替代旧 take=int.MaxValue 无界读取），
/// 以及 SSE 事件流在 Run 进入终态后补读全部尾部事件再关闭连接（不丢最终事件）。
/// </summary>
[TestClass]
[TestCategory("Smoke")]
[TestCategory("Agent")]
public sealed class R29K_AgentRunRawEventsPaginationTests
{
    private const string WorkspaceId = "default";

    // ===========================================================================
    // 1. RawEvents_FirstPage_HasMore_NextSequence
    // 验证：limit 分页逐页游标续取（after=NextSequence），末页 HasMore=false。
    // ===========================================================================
    [TestMethod]
    public async Task RawEvents_FirstPage_HasMore_NextSequence()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new R29KServiceFactory(rootPath);
            using var http = factory.CreateClient();
            var runId = await CreateRunAsync(factory, AgentRunState.Created);
            await AppendEventsAsync(factory, runId, startSequence: 0, count: 7);

            // 第 1 页：limit=3 → 0..2，HasMore=true，NextSequence=2
            var page1 = await GetPageAsync(http, runId, limit: 3, after: null);
            Assert.AreEqual(3, page1.Items.Count, "第 1 页应返回 3 条。");
            Assert.AreEqual(0, page1.Items[0].Sequence);
            Assert.AreEqual(2, page1.Items[^1].Sequence, "第 1 页末条应为 sequence 2。");
            Assert.IsTrue(page1.HasMore, "还有剩余事件时 HasMore 应为 true。");
            Assert.AreEqual(2, page1.NextSequence, "NextSequence 应为当前页末条 sequence。");

            // 第 2 页：after=2&limit=3 → 3..5，HasMore=true，NextSequence=5
            var page2 = await GetPageAsync(http, runId, limit: 3, after: 2);
            Assert.AreEqual(3, page2.Items.Count, "第 2 页应返回 3 条。");
            Assert.AreEqual(3, page2.Items[0].Sequence);
            Assert.AreEqual(5, page2.Items[^1].Sequence);
            Assert.IsTrue(page2.HasMore);
            Assert.AreEqual(5, page2.NextSequence);

            // 第 3 页：after=5&limit=3 → 仅 6，HasMore=false，NextSequence=-1
            var page3 = await GetPageAsync(http, runId, limit: 3, after: 5);
            Assert.AreEqual(1, page3.Items.Count, "第 3 页应返回剩余 1 条。");
            Assert.AreEqual(6, page3.Items[0].Sequence);
            Assert.IsFalse(page3.HasMore, "已到流末尾时 HasMore 应为 false。");
            Assert.AreEqual(-1, page3.NextSequence, "无下一页时 NextSequence 应为 -1。");

            // 分页完整性：三页合并应恰好覆盖 0..6 全部事件（无重复、无遗漏）
            var allSequences = page1.Items.Concat(page2.Items).Concat(page3.Items)
                .Select(e => e.Sequence).OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, 7).ToArray(), allSequences,
                "逐页游标续取应恰好覆盖全部事件。");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ===========================================================================
    // 2. RawEvents_Limit_ClampedToServerMax
    // 验证：服务端把用户 limit clamp 到页大小上限（500），不做无界读取。
    // ===========================================================================
    [TestMethod]
    public async Task RawEvents_Limit_ClampedToServerMax()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new R29KServiceFactory(rootPath);
            using var http = factory.CreateClient();
            var runId = await CreateRunAsync(factory, AgentRunState.Created);
            await AppendEventsAsync(factory, runId, startSequence: 0, count: 501);

            // limit=100000 被 clamp 到 500：返回 500 条 + HasMore=true
            var page = await GetPageAsync(http, runId, limit: 100000, after: null);
            Assert.AreEqual(500, page.Items.Count, "服务端页大小上限为 500，limit=100000 应被 clamp。");
            Assert.AreEqual(0, page.Items[0].Sequence);
            Assert.AreEqual(499, page.Items[^1].Sequence);
            Assert.IsTrue(page.HasMore, "仍有剩余事件时 HasMore 应为 true。");
            Assert.AreEqual(499, page.NextSequence);

            // 续取最后一页：仅 1 条
            var tail = await GetPageAsync(http, runId, limit: 100000, after: 499);
            Assert.AreEqual(1, tail.Items.Count);
            Assert.AreEqual(500, tail.Items[0].Sequence);
            Assert.IsFalse(tail.HasMore);
            Assert.AreEqual(-1, tail.NextSequence);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ===========================================================================
    // 3. RawEvents_NoParams_DefaultsFromStart
    // 验证：不带 after/limit 时从头读取，返回全部（少于默认页大小 200）。
    // ===========================================================================
    [TestMethod]
    public async Task RawEvents_NoParams_DefaultsFromStart()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new R29KServiceFactory(rootPath);
            using var http = factory.CreateClient();
            var runId = await CreateRunAsync(factory, AgentRunState.Created);
            await AppendEventsAsync(factory, runId, startSequence: 0, count: 7);

            var page = await GetPageAsync(http, runId, limit: null, after: null);
            Assert.AreEqual(7, page.Items.Count, "事件少于默认页大小时应全部返回。");
            Assert.AreEqual(0, page.Items[0].Sequence);
            Assert.AreEqual(6, page.Items[^1].Sequence);
            Assert.IsFalse(page.HasMore);
            Assert.AreEqual(-1, page.NextSequence);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ===========================================================================
    // 4. RawEvents_AfterNegative_TreatedAsStart
    // 验证：after < -1 视为从头读取（防御非法游标）。
    // ===========================================================================
    [TestMethod]
    public async Task RawEvents_AfterNegative_TreatedAsStart()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new R29KServiceFactory(rootPath);
            using var http = factory.CreateClient();
            var runId = await CreateRunAsync(factory, AgentRunState.Created);
            await AppendEventsAsync(factory, runId, startSequence: 0, count: 3);

            var page = await GetPageAsync(http, runId, limit: 10, after: -5);
            Assert.AreEqual(3, page.Items.Count, "after=-5 应视为从头读取。");
            Assert.AreEqual(0, page.Items[0].Sequence);
            Assert.IsFalse(page.HasMore);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ===========================================================================
    // 5. RawEvents_NotFound_Returns404
    // 验证：不存在的 Run 返回 404。
    // ===========================================================================
    [TestMethod]
    public async Task RawEvents_NotFound_Returns404()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new R29KServiceFactory(rootPath);
            using var http = factory.CreateClient();

            using var response = await http.GetAsync("/api/agents/runs/missing-run/events/raw");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode,
                "不存在的 Run 应返回 404。");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ===========================================================================
    // 6. Sse_TerminalRun_DrainsAllTrailingEventsBeforeClose
    // 验证：Run 已进入终态时，SSE 补读循环把全部尾部事件
    // （超过单轮 100 条上限的部分）送达后再推送 run.terminal 并关闭连接。
    // ===========================================================================
    [TestMethod]
    public async Task Sse_TerminalRun_DrainsAllTrailingEventsBeforeClose()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new R29KServiceFactory(rootPath);
            using var http = factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            // 终态 Run（Completed）+ 130 条尾部事件（超过单轮补读上限 100，
            // 模拟"状态 CAS 已提交但尾部事件随后落库"的竞态窗口）。
            var runId = await CreateRunAsync(factory, AgentRunState.Completed);
            await AppendEventsAsync(factory, runId, startSequence: 0, count: 130);

            using var sseRequest = new HttpRequestMessage(
                HttpMethod.Get, $"/api/agents/runs/{runId}/events");
            using var sseResponse = await http.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, sseResponse.StatusCode,
                $"SSE 端点应返回 200 OK，实际 {sseResponse.StatusCode}。");
            Assert.AreEqual("text/event-stream", sseResponse.Content.Headers.ContentType?.MediaType,
                "SSE Content-Type 应为 text/event-stream。");

            using var stream = await sseResponse.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

            var received = new List<int>();
            var sawTerminal = false;
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal)
                    && int.TryParse(line.AsSpan(4), out var seq))
                {
                    received.Add(seq);
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal)
                         && line.AsSpan(7).SequenceEqual("run.terminal"))
                {
                    sawTerminal = true;
                }
            }

            // 130 条事件必须全部送达（终态补读循环覆盖单轮上限之外的尾部事件）
            CollectionAssert.AreEqual(Enumerable.Range(0, 130).ToArray(),
                received.OrderBy(s => s).ToArray(),
                "终态 Run 的全部尾部事件必须送达后再关闭连接。");
            Assert.IsTrue(sawTerminal, "补读完成后应推送 run.terminal 事件。");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private static async Task<string> CreateRunAsync(R29KServiceFactory factory, AgentRunState state)
    {
        var runStore = factory.Services.GetRequiredService<IAgentRunStore>();
        var runId = "p1-6-run-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await runStore.CreateAsync(new AgentRun
        {
            RunId = runId,
            WorkspaceId = WorkspaceId,
            SessionId = "session-p1-6",
            Task = "P1-6 raw events pagination test",
            State = state,
            Turn = 0,
            CreatedAt = now,
            UpdatedAt = now,
            FinishedAt = state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled
                ? now
                : null
        });
        return runId;
    }

    /// <summary>通过进程内事件存储追加一批连续事件（哈希链自动链接）。</summary>
    private static async Task AppendEventsAsync(
        R29KServiceFactory factory, string runId, int startSequence, int count)
    {
        var eventStore = factory.Services.GetRequiredService<IAgentRunEventStore>();
        var batch = new List<AgentRunEvent>(count);
        string? prevHash = null;
        for (var i = 0; i < count; i++)
        {
            var seq = startSequence + i;
            var evt = AgentRunEventChain.BuildEvent(
                runId, WorkspaceId, seq,
                AgentRunEventType.ObservationAppended, AgentRunState.Created,
                JsonSerializer.Serialize(new { seq, source = "r29k-pagination-test" }),
                prevHash);
            prevHash = evt.ContentHash;
            batch.Add(evt);
        }
        await eventStore.AppendBatchAsync(
            batch, runStateUpdate: null, checkpointCursor: null, checkpointBody: null);
    }

    private static async Task<AgentRunRawEventsPage> GetPageAsync(
        HttpClient http, string runId, int? limit, int? after)
    {
        var qs = new QueryBuilder();
        if (limit is not null)
        {
            qs.Add("limit", limit.Value);
        }
        if (after is not null)
        {
            qs.Add("after", after.Value);
        }
        using var response = await http.GetAsync($"/api/agents/runs/{runId}/events/raw{qs}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"raw events 分页应返回 200 OK，实际 {response.StatusCode}。");
        var page = await response.Content.ReadFromJsonAsync<AgentRunRawEventsPage>();
        Assert.IsNotNull(page, "响应应反序列化为 AgentRunRawEventsPage。");
        return page!;
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-r29k-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>Development profile + filesystem 的 Web 工厂（与 ProductionRuntimeProfile 测试一致）。</summary>
    private sealed class R29KServiceFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;

        public R29KServiceFactory(string rootPath)
        {
            _rootPath = rootPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("ContextCoreRuntime:Profile", "Development");
            builder.UseSetting("ContextCoreRuntime:EnableAgentRunRecovery", "false");
            // filesystem 下部分服务（ICanaryLeaderLease / ILearningEventOutboxStore）仅在 Postgres provider 注册，
            // 无法从根容器解析 scoped IWorkspaceContextAccessor；本测试验证 HTTP 端点而非 DI 完整性，
            // 故关闭构建时验证。
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = false;
                options.ValidateOnBuild = false;
            });
            // 移除所有 IHostedService 注册（本测试直接通过进程内 store 准备数据，不需要后台 Worker）。
            builder.ConfigureServices(services =>
            {
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    if (services[i].ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                    {
                        services.RemoveAt(i);
                    }
                }
            });
        }
    }
}
