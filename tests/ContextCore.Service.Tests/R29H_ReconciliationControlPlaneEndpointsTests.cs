using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Tests;

// ===========================================================================
// Tool Reconciliation Control Plane 端点验收测试（-B1）
//
// 验证 GET /api/agents/reconciliations 双模式：
// 1. ?externalOperationId=… → 按 journal 外部操作 ID 反查（跨 Run 运维查询）；
// 2. 无 externalOperationId → ControlRoom 分页待决列表（过期高亮 + 告警计数 + OverdueOnly）。
//
// 使用文件系统存储 + 默认 InMemoryToolReconciliationStore（端点逻辑与存储解耦）；
// RBAC 未强制（RequireApiKey=false）→ 权限过滤器放行，专注验证端点契约。
// ===========================================================================

[TestClass]
[TestCategory("E2E")]
[TestCategory("ToolReconciliation-ControlPlane")]
public sealed class R29H_ReconciliationControlPlaneEndpointsTests
{
    private const string Ws = "ws-recon-endpoint";

    /// <summary>
    /// 验证：GET /api/agents/reconciliations?externalOperationId=… 按外部操作 ID 反查
    /// （跨 Run、未匹配返回空列表）。
    /// </summary>
    [TestMethod]
    public async Task GetReconciliations_ByExternalOperationId_ReturnsMatchingRecords()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = CreateFactory(rootPath);
            using var httpClient = factory.CreateClient();

            var store = factory.Services.GetRequiredService<IToolReconciliationStore>();
            const string externalOp = "ext-op-endpoint-77";
            await store.CreateAsync(BuildRecord("rec-e1", "req-e1", Ws, externalOperationId: externalOp), default);
            await store.CreateAsync(BuildRecord("rec-e2", "req-e2", Ws, externalOperationId: externalOp, runId: "run-other"), default);
            await store.CreateAsync(BuildRecord("rec-e3", "req-e3", Ws, externalOperationId: "ext-op-other"), default);

            var response = await httpClient.GetAsync($"/api/agents/reconciliations?externalOperationId={externalOp}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "按 externalOperationId 反查应返回 200。");

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var items = json.GetProperty("items").EnumerateArray().ToList();
            Assert.AreEqual(2, items.Count, "应反查到两条同 externalOperationId 的记录（跨 Run）。");
            var ids = items.Select(i => i.GetProperty("reconciliationId").GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "rec-e1", "rec-e2" }, ids, "反查应覆盖跨 Run 的所有匹配记录。");

            // 未匹配 → 空列表
            var none = await httpClient.GetAsync("/api/agents/reconciliations?externalOperationId=ext-op-missing");
            Assert.AreEqual(HttpStatusCode.OK, none.StatusCode);
            var noneJson = await none.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(0, noneJson.GetProperty("items").GetArrayLength(), "未匹配应返回空 items。");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    /// <summary>
    /// 验证：GET /api/agents/reconciliations 返回 ControlRoom 分页列表
    /// （total / overdueCount 告警计数 / 过期条目 DeadlineUtc 高亮 / OverdueOnly 过滤）。
    /// </summary>
    [TestMethod]
    public async Task GetReconciliations_ControlRoomList_PagingAndOverdueAlertCount()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = CreateFactory(rootPath);
            using var httpClient = factory.CreateClient();

            var store = factory.Services.GetRequiredService<IToolReconciliationStore>();
            var now = DateTimeOffset.UtcNow;
            await store.CreateAsync(BuildRecord("rec-old-1", "req-old-1", Ws, deadline: now - TimeSpan.FromHours(2)), default);
            await store.CreateAsync(BuildRecord("rec-old-2", "req-old-2", Ws, deadline: now - TimeSpan.FromMinutes(30)), default);
            await store.CreateAsync(BuildRecord("rec-fresh", "req-fresh", Ws, deadline: now + TimeSpan.FromHours(2)), default);
            await store.CreateAsync(BuildRecord("rec-resolved", "req-resolved", Ws, deadline: now - TimeSpan.FromHours(1)), default);
            // 先原子取得裁决权（P0-5）再提交终态：MarkRejectedAsync 需持有有效租约。
            var resolvedLease = await store.TryBeginAsync("rec-resolved", "test", TimeSpan.FromMinutes(1), default);
            Assert.IsNotNull(resolvedLease, "Pending 记录可领取裁决租约。");
            await store.MarkRejectedAsync(
                "rec-resolved", resolvedLease!.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = false, Error = "未发生" }, default);

            var response = await httpClient.GetAsync($"/api/agents/reconciliations?workspaceId={Ws}&limit=50");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "ControlRoom 列表应返回 200。");

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(4, json.GetProperty("total").GetInt32(), "列表应包含全部 4 条记录。");
            Assert.AreEqual(2, json.GetProperty("overdueCount").GetInt32(),
                "告警计数 = 过期未决（Pending 且 deadline<now）2 条。");

            var items = json.GetProperty("items").EnumerateArray().ToList();
            Assert.AreEqual(4, items.Count, "limit=50 应返回全部条目。");
            var overdueIds = items
                .Where(i => i.TryGetProperty("deadlineUtc", out var d)
                            && d.ValueKind == JsonValueKind.String
                            && DateTimeOffset.Parse(d.GetString()!) < DateTimeOffset.UtcNow
                            && i.GetProperty("status").GetInt32() == (int)ToolReconciliationStatus.Pending)
                .Select(i => i.GetProperty("reconciliationId").GetString())
                .ToList();
            CollectionAssert.AreEquivalent(new[] { "rec-old-1", "rec-old-2" }, overdueIds,
                "过期未决条目应携带过去 DeadlineUtc（高亮依据）。");

            // OverdueOnly 过滤
            var overdue = await httpClient.GetAsync($"/api/agents/reconciliations?workspaceId={Ws}&overdueOnly=true");
            Assert.AreEqual(HttpStatusCode.OK, overdue.StatusCode);
            var overdueJson = await overdue.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(2, overdueJson.GetProperty("total").GetInt32(), "OverdueOnly 应只返回过期未决记录。");
            Assert.AreEqual(2, overdueJson.GetProperty("overdueCount").GetInt32());

            // 分页：limit=2 offset=2 第二页
            var page2 = await httpClient.GetAsync($"/api/agents/reconciliations?workspaceId={Ws}&limit=2&offset=2");
            Assert.AreEqual(HttpStatusCode.OK, page2.StatusCode);
            var page2Json = await page2.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(2, page2Json.GetProperty("items").GetArrayLength(), "第二页应返回 2 条。");
            Assert.AreEqual(4, page2Json.GetProperty("total").GetInt32(), "分页结果仍应携带过滤后总数。");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── 辅助 ───────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> CreateFactory(string rootPath)
    {
        return new ServiceTestFactory(rootPath);
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-recon-endpoint-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static ToolReconciliationRecord BuildRecord(
        string reconciliationId,
        string requestId,
        string workspaceId,
        string? externalOperationId = null,
        DateTimeOffset? deadline = null,
        string runId = "run-recon-endpoint") => new()
    {
        ReconciliationId = reconciliationId,
        RunId = runId,
        WorkspaceId = workspaceId,
        RequestId = requestId,
        ToolName = "bank-transfer",
        ExternalOperationId = externalOperationId,
        ReconciliationHandler = "bank-query",
        DeadlineUtc = deadline,
        Status = ToolReconciliationStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class ServiceTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;

        public ServiceTestFactory(string rootPath)
        {
            _rootPath = rootPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("Security:RequireApiKey", "false");
        }
    }
}

