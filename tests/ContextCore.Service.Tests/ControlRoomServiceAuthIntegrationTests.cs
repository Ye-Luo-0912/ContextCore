using System.Net;
using System.Text;
using ContextCore.ControlRoom.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ContextCore.Service.Tests;

/// <summary>
/// 端到端测试：验证 ControlRoom Service 模式下正确传递 API Key。
/// 启动真实 Service（RequireApiKey=true），用 ControlRoomService.CreateServiceState 连接并执行只读操作。
/// </summary>
[TestClass]
[TestCategory("Auth")]
[TestCategory("E2E")]
public sealed class ControlRoomServiceAuthIntegrationTests
{
    // 测试用假密钥，使用 test-key- 前缀，确保不是真实密钥
    private const string TestApiKey = "test-key-cr01-123";

    [TestMethod]
    public async Task ControlRoom_WithCorrectApiKey_ShouldAccessService()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = CreateFactory(rootPath, TestApiKey);
            using var httpClient = factory.CreateClient();
            // 传入正确的 API Key，ControlRoomService 应能访问 Service 的写保护端点
            var state = ControlRoomService.CreateServiceState(
                "ws-test",
                "col-test",
                httpClient.BaseAddress!.ToString(),
                httpClient,
                apiKey: TestApiKey);
            var service = new ControlRoomService(state);

            // /api/status 不在 PublicPaths 中，需要 API Key；正确 key 应返回 200
            var status = await service.GetRuntimeStatusAsync();

            Assert.IsNotNull(status);
            Assert.IsNotNull(status.Storage);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoom_WithWrongApiKey_ShouldGet401()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = CreateFactory(rootPath, TestApiKey);
            using var httpClient = factory.CreateClient();
            // 传入错误的 API Key
            var state = ControlRoomService.CreateServiceState(
                "ws-test",
                "col-test",
                httpClient.BaseAddress!.ToString(),
                httpClient,
                apiKey: "wrong-key-value");
            var service = new ControlRoomService(state);

            // Service 端 ApiKeyMiddleware 会返回 401，ContextCoreClient 回退到 EnsureSuccessStatusCode 抛出 HttpRequestException
            await Assert.ThrowsExceptionAsync<HttpRequestException>(
                () => service.GetRuntimeStatusAsync());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoom_WithoutApiKey_ShouldGet401()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = CreateFactory(rootPath, TestApiKey);
            using var httpClient = factory.CreateClient();
            // 不传 apiKey：Service 启用 RequireApiKey 后所有写保护端点返回 401
            var state = ControlRoomService.CreateServiceState(
                "ws-test",
                "col-test",
                httpClient.BaseAddress!.ToString(),
                httpClient);
            var service = new ControlRoomService(state);

            await Assert.ThrowsExceptionAsync<HttpRequestException>(
                () => service.GetRuntimeStatusAsync());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoom_HttpClient_ShouldContainAuthHeader()
    {
        // 用 StubHttpMessageHandler 捕获实际发送的请求头，验证 X-ContextCore-Key 被注入
        string? capturedHeader = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Headers.TryGetValues("X-ContextCore-Key", out var values);
            capturedHeader = values?.FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5079/", UriKind.Absolute)
        };

        // CreateServiceState 会在 httpClient.DefaultRequestHeaders 上注入 API Key 认证头
        ControlRoomService.CreateServiceState(
            "ws-test",
            "col-test",
            "http://localhost:5079/",
            httpClient,
            apiKey: TestApiKey);

        // 触发一个请求，验证 header 被实际发送
        await httpClient.GetAsync("api/status");

        Assert.AreEqual(TestApiKey, capturedHeader, "HttpClient 请求必须携带 X-ContextCore-Key 认证头");
    }

    [TestMethod]
    public void ControlRoom_CreateServiceState_WithoutApiKey_ShouldNotSetAuthHeader()
    {
        // 不传 apiKey 时，HttpClient 不应携带认证头
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:5079/", UriKind.Absolute)
        };

        ControlRoomService.CreateServiceState(
            "ws-test",
            "col-test",
            "http://localhost:5079/",
            httpClient);

        Assert.IsFalse(
            httpClient.DefaultRequestHeaders.Contains("X-ContextCore-Key"),
            "未提供 apiKey 时不应设置认证头");
    }

    [TestMethod]
    public async Task ControlRoom_WithCustomHeaderName_ShouldUseConfiguredHeader()
    {
        // 验证自定义头名称生效
        const string customHeader = "X-Custom-Context-Key";
        string? capturedHeader = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Headers.TryGetValues(customHeader, out var values);
            capturedHeader = values?.FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5079/", UriKind.Absolute)
        };

        ControlRoomService.CreateServiceState(
            "ws-test",
            "col-test",
            "http://localhost:5079/",
            httpClient,
            apiKey: TestApiKey,
            apiKeyHeaderName: customHeader);

        await httpClient.GetAsync("api/status");

        Assert.AreEqual(TestApiKey, capturedHeader, "应使用自定义头名称");
    }

    private static WebApplicationFactory<Program> CreateFactory(string rootPath, string apiKey)
    {
        return new ServiceAuthTestFactory(rootPath, apiKey);
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-controlroom-auth-integration-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理失败不应影响测试结果
            }
        }
    }

    /// <summary>
    /// 启动真实 Service 的 WebApplicationFactory，参照 ServiceApiIntegrationTests.ServiceTestFactory 模式。
    /// 启用 RequireApiKey=true 并注入测试 API Key。
    /// </summary>
    private sealed class ServiceAuthTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;
        private readonly string _apiKey;

        public ServiceAuthTestFactory(string rootPath, string apiKey)
        {
            _rootPath = rootPath;
            _apiKey = apiKey;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("JobWorker:PollIntervalMilliseconds", "100");
            builder.UseSetting("Security:RequireApiKey", "true");
            builder.UseSetting("Security:ApiKey", _apiKey);
        }
    }

    /// <summary>
    /// 捕获请求并返回固定响应的 HttpMessageHandler，参照 ContextCoreControlRoomServiceModeTests.StubHttpMessageHandler 模式。
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
