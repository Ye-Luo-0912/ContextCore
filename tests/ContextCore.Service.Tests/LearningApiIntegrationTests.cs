using System.Net;
using System.Net.Http.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Service.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace ContextCore.Service.Tests;

/// <summary>
/// Learning / Diagnostics API 端到端 HTTP 验收（WP-Q）：
/// 真实 Service（WebApplicationFactory + filesystem provider）下验证
/// 认证（X-ContextCore-Key）、响应契约（learning 工件列表 / diagnostics 报告）。
/// </summary>
[TestClass]
public sealed class LearningApiIntegrationTests
{
    private const string TestApiKey = "test-api-key-123";
    private const string HeaderKeyName = "X-ContextCore-Key";
    private const string HeaderWorkspaceName = "X-ContextCore-Workspace";

    [TestMethod]
    public async Task LearningArtifacts_WithApiKey_Returns200EmptyList()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderKeyName, TestApiKey);
        client.DefaultRequestHeaders.Add(HeaderWorkspaceName, "ws-integration");

        var response = await client.GetAsync("/api/learning/artifacts");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "带有效 API Key 应 200。");
        var list = await response.Content.ReadFromJsonAsync<LearningArtifactListResponse>();
        Assert.IsNotNull(list, "响应契约：LearningArtifactListResponse。");
        Assert.AreEqual(0, list!.Entries.Count, "新工作区无工件。");
    }

    [TestMethod]
    public async Task LearningArtifacts_WithoutApiKey_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderWorkspaceName, "ws-integration");

        var response = await client.GetAsync("/api/learning/artifacts");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "缺失 API Key 应 401。");
    }

    [TestMethod]
    public async Task DiagnosticsRuntime_WithApiKey_Returns200WithReport()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderKeyName, TestApiKey);
        client.DefaultRequestHeaders.Add(HeaderWorkspaceName, "ws-integration");

        var response = await client.GetAsync("/api/diagnostics/runtime");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<RuntimeDiagnosticsReport>();
        Assert.IsNotNull(report);
        // filesystem provider：Postgres 迁移 runner 未注册 → schema null；Learning outbox 未注册 → null。
        Assert.IsNull(report!.Schema, "filesystem provider 下无 Postgres 迁移 runner。");
        Assert.IsNull(report.Learning, "filesystem provider 下无 Learning outbox。");
        Assert.IsNotNull(report.Background, "后台负载预算诊断始终存在。");
        Assert.AreEqual(8, report.Background!.DrainBudget!.MaxBatchesPerBurst);
    }

    [TestMethod]
    public async Task DiagnosticsRuntime_WithoutApiKey_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/diagnostics/runtime");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task AdaptiveMode_GetWithApiKey_Returns200WithCurrentMode()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderKeyName, TestApiKey);
        client.DefaultRequestHeaders.Add(HeaderWorkspaceName, "ws-integration");

        var response = await client.GetAsync("/api/retrieval/adaptive/mode");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<AdaptiveModeStatusResponse>();
        Assert.IsNotNull(status);
        Assert.AreEqual(AdaptiveRetrievalMode.Disabled, status!.CurrentMode, "默认 fail-closed Disabled。");
        Assert.IsTrue(status.History.Count >= 1, "审计含初始化记录。");
    }

    [TestMethod]
    public async Task AdaptiveMode_PostSwitch_WithApiKey_TransitionsAndAudits()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderKeyName, TestApiKey);
        client.DefaultRequestHeaders.Add(HeaderWorkspaceName, "ws-integration");

        // Shadow → Active（生产启用流程）。
        var shadow = await client.PostAsJsonAsync("/api/retrieval/adaptive/mode",
            new { mode = 1, reason = "观察期开始" });
        Assert.AreEqual(HttpStatusCode.OK, shadow.StatusCode);
        var shadowTransition = await shadow.Content.ReadFromJsonAsync<AdaptiveModeTransition>();
        Assert.AreEqual(AdaptiveRetrievalMode.Disabled, shadowTransition!.From);
        Assert.AreEqual(AdaptiveRetrievalMode.Shadow, shadowTransition.To);

        var active = await client.PostAsJsonAsync("/api/retrieval/adaptive/mode",
            new { mode = 2, reason = "观察期达标，启用" });
        Assert.AreEqual(HttpStatusCode.OK, active.StatusCode);
        var activeTransition = await active.Content.ReadFromJsonAsync<AdaptiveModeTransition>();
        Assert.AreEqual(AdaptiveRetrievalMode.Shadow, activeTransition!.From);
        Assert.AreEqual(AdaptiveRetrievalMode.Active, activeTransition.To);
        Assert.IsFalse(string.IsNullOrWhiteSpace(activeTransition.Actor), "审计记录操作者。");

        // 状态查询反映当前模式。
        var statusResponse = await client.GetAsync("/api/retrieval/adaptive/mode");
        var status = await statusResponse.Content.ReadFromJsonAsync<AdaptiveModeStatusResponse>();
        Assert.AreEqual(AdaptiveRetrievalMode.Active, status!.CurrentMode, "切换后模式生效。");
        Assert.IsTrue(status.History.Any(t => t.To == AdaptiveRetrievalMode.Active && t.Reason == "观察期达标，启用"),
            "审计含启用记录（原因可解释）。");

        // 一键回退 Disabled。
        var rollback = await client.PostAsJsonAsync("/api/retrieval/adaptive/mode",
            new { mode = 0, reason = "异常回退" });
        Assert.AreEqual(HttpStatusCode.OK, rollback.StatusCode);
    }

    [TestMethod]
    public async Task AdaptiveMode_WithoutApiKey_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/retrieval/adaptive/mode");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        var post = await client.PostAsJsonAsync("/api/retrieval/adaptive/mode", new { mode = 2 });
        Assert.AreEqual(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [TestMethod]
    public async Task LearningExport_EmptyDataset_QualityGateBlocks422()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderKeyName, TestApiKey);
        client.DefaultRequestHeaders.Add(HeaderWorkspaceName, "ws-integration");

        // 空 ledger（filesystem InMemory）→ 导出空数据集 → 质量闸门 Blocked → 422。
        var response = await client.PostAsJsonAsync("/api/learning/artifacts/export",
            new { outputDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-export-" + Guid.NewGuid().ToString("N")) });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, "空数据集应被质量闸门阻断（422）。");
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new ServiceApiTestFactory();
    }

    /// <summary>启动真实 Service（filesystem provider + 强制 API Key）。</summary>
    private sealed class ServiceApiTestFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-service-api-test-" + Guid.NewGuid().ToString("N")));
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("Security:RequireApiKey", "true");
            builder.UseSetting("Security:ApiKey", TestApiKey);
            builder.UseSetting("Security:ApiKeyHeaderName", HeaderKeyName);
            builder.UseSetting("Security:WorkspaceIdHeaderName", HeaderWorkspaceName);
        }
    }
}
