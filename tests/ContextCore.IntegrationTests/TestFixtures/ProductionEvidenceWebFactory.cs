using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ContextCore.Service;

namespace ContextCore.IntegrationTests.TestFixtures;

// ===========================================================================
// Production Evidence E2E WebApplicationFactory
//
// 目标：提供真实 ASP.NET Core 主机 + 真实 PostgreSQL 后端的 E2E 测试环境。
// 补齐现有测试缺口：现有 4 个 E2E 测试直接构造 Actor/Store，未通过 HTTP API 与
// 完整 DI 容器验证真实服务链路。
//
// 设计要点：
//   1. 通过 UseSetting 注入 Testcontainers PG 连接字符串，触发 Program.cs 的
//      Postgres provider 注册 + 启动期 SELECT 1 + AutoBootstrap migration。
//   2. 可选移除所有 IHostedService（默认移除），避免后台 Worker 与 HTTP 测试相互干扰。
//      需要测试 HostedService 行为时通过 keepHostedServices:true 保留。
//   3. 可选注入 StubHttpMessageHandler 到 HttpClient，用于 mock 外部 LLM/Provider。
//   4. 支持 keepExistingState:true 复用同一 PG 的已持久化状态（进程重启恢复测试）。
//
// 与现有 ProductionRuntimeFactory（R29H_ProductionRuntimeProfileTests.cs:707）的区别：
//   - ProductionRuntimeFactory 使用 filesystem 存储（无 PG）+ 关闭 ValidateOnBuild，
//     仅验证 HTTP 端点响应契约，不验证真实存储链路。
//   - 本 Factory 使用真实 PG 存储 + 完整 DI 验证，证明端到端可用性。
// ===========================================================================

/// <summary>
/// Production Evidence E2E 测试用 WebApplicationFactory。
/// 使用真实 PostgreSQL 后端，可选保留 HostedService 与注入 HTTP mock。
/// </summary>
public sealed class ProductionEvidenceWebFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _keepHostedServices;
    private readonly StubHttpMessageHandler? _httpHandler;

    /// <summary>
    /// 创建 Production Evidence Web Factory。
    /// </summary>
    /// <param name="connectionString">PostgreSQL 连接字符串（Testcontainers）。</param>
    /// <param name="keepHostedServices">是否保留 IHostedService 注册（默认 false 移除，避免后台 Worker 干扰）。</param>
    /// <param name="httpHandler">可选的 HTTP mock handler（用于外部 LLM/Provider 依赖）。</param>
    public ProductionEvidenceWebFactory(
        string connectionString,
        bool keepHostedServices = false,
        StubHttpMessageHandler? httpHandler = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("连接字符串不能为空。", nameof(connectionString));
        }
        _connectionString = connectionString;
        _keepHostedServices = keepHostedServices;
        _httpHandler = httpHandler;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 使用 Development 环境以获得详细错误信息
        builder.UseEnvironment(Environments.Development);

        // 注入真实 PostgreSQL 连接：触发 RegisterPostgres + 启动期 SELECT 1 + AutoBootstrap migration
        builder.UseSetting("Storage:Provider", "postgres");
        builder.UseSetting("Storage:PostgresConnectionString", _connectionString);
        builder.UseSetting("Storage:AutoBootstrap", "true");
        builder.UseSetting("Compression:Provider", "mock");

        // 关闭非必要的后台 Worker（测试聚焦 HTTP 端点链路，避免 HostedService 干扰）
        builder.UseSetting("JobWorker:Enabled", "false");
        builder.UseSetting("ProductionRuntime:EnableAgentKernelLoop", "false");
        builder.UseSetting("ProductionRuntime:EnableRunRecovery", "false");
        builder.UseSetting("RelationReconciliation:Enabled", "false");
        builder.UseSetting("ShortTermMaintenance:Enabled", "false");

        // 关闭 API Key 认证（E2E 测试内部网络，无需鉴权；通过 RequireWorkspacePermission 的端点仍生效）
        builder.UseSetting("Security:RequireApiKey", "false");

        // Development 默认 ValidateOnBuild=true，但部分服务（ICanaryLeaderLease 等）仅在特定条件下注册。
        // E2E 测试聚焦端到端链路，关闭构建时验证避免误判。
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = false;
            options.ValidateOnBuild = false;
        });

        builder.ConfigureServices(services =>
        {
            // 可选：移除所有 IHostedService 注册（避免后台 Worker 与 HTTP 测试相互干扰）
            if (!_keepHostedServices)
            {
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    if (services[i].ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                    {
                        services.RemoveAt(i);
                    }
                }
            }

            // 可选：注入 HTTP mock handler（替换所有 HttpClient 的主消息处理器）
            if (_httpHandler is not null)
            {
                services.RemoveAll<HttpMessageHandler>();
                services.AddTransient<HttpMessageHandler>(_ => _httpHandler);
            }
        });
    }
}
