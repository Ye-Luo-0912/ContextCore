using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.IntegrationTests.TestFixtures;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// HTTP 竞态场景集成测试
//
// 目标：
// 1. E2E_Http_Sse_LastEventIdReplay_NoLostWakeup — SSE 在读取/订阅竞争窗口不丢最终事件：
// a. 连接前已提交的事件经 Last-Event-ID 补读送达（断线重连不丢历史）；
// b. 连接期间提交的事件经 notifier push 立即送达（无丢失唤醒窗口）。
// 2. E2E_Http_ConcurrentIdempotencyKey_ExactlyOneRun — N 个并发 POST 携带相同
// IdempotencyKey：恰好创建 1 个 Run，所有请求返回同一 runId。
//
// 设计原则：
// - 通过 ProductionEvidenceWebFactory 启动真实 Web 主机 + 真实 PostgreSQL。
// - SSE 事件由 factory.Services 解析出的进程内 store 追加（与端点同一 notifier 实例）。
// - Docker/Postgres 不可用时 Assert.Inconclusive 跳过。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
[TestCategory("HttpRaceE2E")]
public sealed class R29H_ProductionEvidenceHttpRaceE2ETests : IAsyncDisposable
{
    private readonly PostgresE2EFixture _pg = new();

    [TestInitialize]
    public async Task InitializeAsync() => await _pg.StartAsync();

    [TestCleanup]
    public Task CleanupAsync() => _pg.DisposeAsync().AsTask();

    // =======================================================================
    // 测试 1：SSE Last-Event-ID 补读 + 连接期间推送 —— 不丢最终事件
    // =======================================================================

    [TestMethod]
    public async Task E2E_Http_Sse_LastEventIdReplay_NoLostWakeup()
    {
        if (_pg.ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        await using var factory = new ProductionEvidenceWebFactory(_pg.ConnectionString);
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        // ── 创建 Run ──
        using var createResponse = await client.PostAsJsonAsync("/api/agents/runs", new
        {
            Task = "SSE 唤醒竞争测试",
            TimeoutSeconds = 120
        });
        Assert.IsTrue(
            createResponse.StatusCode == HttpStatusCode.Created ||
            createResponse.StatusCode == HttpStatusCode.Accepted,
            $"POST /api/agents/runs 应返回 201/202，实际 {createResponse.StatusCode}。");
        var createdRun = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdRun.GetProperty("runId").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(runId), "响应应包含 runId。");

        // ── 解析 Run 的 workspaceId（HTTP 创建时回退为 default）──
        var workspaceId = await ReadWorkspaceIdAsync(runId!);

        // ── 解析进程内 store（与 SSE 端点同一 DI 实例，同一 notifier）──
        var eventStore = factory.Services.GetRequiredService<IAgentRunEventStore>();

        // ── 预置事件 0..2（SSE 连接打开前已提交 —— 客户端断开期间"错过"的历史事件）──
        await AppendEventsAsync(eventStore, workspaceId, runId!, startSequence: 0, count: 3);

        // ── 打开 SSE 连接：Last-Event-ID=0 → 应从 sequence 1 补读 ──
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/agents/runs/{runId}/events");
        sseRequest.Headers.TryAddWithoutValidation("Last-Event-ID", "0");
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, sseResponse.StatusCode,
            $"SSE 端点应返回 200 OK，实际 {sseResponse.StatusCode}。");
        var contentType = sseResponse.Content.Headers.ContentType?.MediaType;
        Assert.AreEqual("text/event-stream", contentType,
            $"SSE Content-Type 应为 text/event-stream，实际 {contentType}。");

        using var stream = await sseResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // ── 阶段 1：读取补读事件直到收到 id=2（证明 Last-Event-ID 补读送达）──
        // 收到 id=2 时，端点已完成第一轮补读并进入 notifier 等待（订阅已注册）。
        var phase1 = await ReadUntilSequenceAsync(reader, 2, cts.Token);
        Assert.IsTrue(phase1, "SSE 应补读并送达 id=1..2（Last-Event-ID 断线重连不丢历史）。");

        // ── 阶段 2：连接期间追加事件 3..4（提交时刻晚于订阅注册 → notifier push）──
        await AppendEventsAsync(eventStore, workspaceId, runId!, startSequence: 3, count: 2);

        // 读取直到收到 id=4（最终事件必须送达 —— 无丢失唤醒窗口）。
        var phase2 = await ReadUntilSequenceAsync(reader, 4, cts.Token);
        Assert.IsTrue(phase2,
            "SSE 连接期间提交的事件必须送达（notifier push 消除读取/订阅竞争窗口，最终事件不丢失）。");
    }

    // =======================================================================
    // 测试 2：并发相同 IdempotencyKey —— 恰好创建 1 个 Run
    // =======================================================================

    [TestMethod]
    public async Task E2E_Http_ConcurrentIdempotencyKey_ExactlyOneRun()
    {
        if (_pg.ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        await using var factory = new ProductionEvidenceWebFactory(_pg.ConnectionString);
        using var client = factory.CreateClient();

        var idempotencyKey = "concurrent-idem-" + Guid.NewGuid().ToString("N");
        const int contenders = 8;
        var runIds = new ConcurrentBag<string>();
        var statusCodes = new ConcurrentBag<HttpStatusCode>();

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            using var response = await client.PostAsJsonAsync("/api/agents/runs", new
            {
                Task = "并发幂等测试",
                IdempotencyKey = idempotencyKey,
                TimeoutSeconds = 60
            });
            statusCodes.Add(response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            runIds.Add(body.GetProperty("runId").GetString()!);
        })).ToArray();

        await Task.WhenAll(tasks);

        // ── 断言 1：所有并发请求返回同一 runId ──
        Assert.AreEqual(1, runIds.Distinct().Count(),
            $"并发相同 IdempotencyKey 应返回同一 runId，实际 {runIds.Distinct().Count()} 个。");

        // ── 断言 2：恰好一个请求创建（201/202），其余幂等命中（200）──
        var created = statusCodes.Count(s => s == HttpStatusCode.Created || s == HttpStatusCode.Accepted);
        Assert.AreEqual(1, created,
            $"恰好一个并发请求应创建 Run（201/202），实际 {created} 个。");
        Assert.IsTrue(statusCodes.All(s => s == HttpStatusCode.Created || s == HttpStatusCode.Accepted || s == HttpStatusCode.OK),
            $"所有响应应为 2xx，实际 {string.Join(",", statusCodes.Select(s => (int)s))}。");

        // ── 断言 3：数据库中恰好 1 条记录 ──
        var runId = runIds.First();
        await using var connection = await _pg.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cc_agent_runs WHERE run_id = @runId";
        command.Parameters.AddWithValue("runId", runId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.AreEqual(1L, count,
            $"并发相同 IdempotencyKey 后数据库中应恰好 1 条记录，实际 {count}。");
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private async Task<string> ReadWorkspaceIdAsync(string runId)
    {
        await using var connection = await _pg.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id FROM cc_agent_runs WHERE run_id = @runId";
        command.Parameters.AddWithValue("runId", runId);
        var result = await command.ExecuteScalarAsync();
        Assert.IsNotNull(result, "应能从 cc_agent_runs 查询到 workspace_id。");
        return (string)result!;
    }

    /// <summary>
    /// 通过进程内 store 追加一批连续事件（哈希链自动链接）。
    /// 事件序列从 startSequence 起连续 count 条。
    /// </summary>
    private static async Task AppendEventsAsync(
        IAgentRunEventStore eventStore, string workspaceId, string runId, int startSequence, int count)
    {
        // 跨批次哈希链：起始 prevHash 必须链接到 store 中既有尾部事件的 ContentHash，
        // 否则 AppendBatchAsync 校验 PrevChainHash 不匹配（哈希链断裂）。
        var lastSeq = await eventStore.GetLastSequenceAsync(workspaceId, runId, CancellationToken.None);
        string? prevHash = null;
        if (lastSeq >= 0)
        {
            var tail = await eventStore.ReadAsync(workspaceId, runId, lastSeq, 1, CancellationToken.None);
            prevHash = tail.Count > 0 ? tail[0].ContentHash : null;
        }

        var batch = new List<AgentRunEvent>(count);
        for (var i = 0; i < count; i++)
        {
            var seq = startSequence + i;
            var evt = AgentRunEventChain.BuildEvent(
                runId, workspaceId, seq,
                AgentRunEventType.ObservationAppended, AgentRunState.Created,
                JsonSerializer.Serialize(new { seq, source = "sse-race-test" }),
                prevHash);
            prevHash = evt.ContentHash;
            batch.Add(evt);
        }
        await eventStore.AppendBatchAsync(batch, runStateUpdate: null, checkpointCursor: null, checkpointBody: null);
    }

    /// <summary>
    /// 逐行读取 SSE 流，直到收到 id >= target 的事件。
    /// 返回是否在取消前收到目标 sequence。
    /// </summary>
    private static async Task<bool> ReadUntilSequenceAsync(StreamReader reader, int target, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (line is null)
            {
                // 连接关闭（不应发生——Run 未进入终态，SSE 应保持打开）
                return false;
            }
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                var idText = line.AsSpan(4);
                if (int.TryParse(idText, out var id) && id >= target)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync();
    }
}
