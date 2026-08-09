using System.Net;
using System.Net.Http.Json;
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
