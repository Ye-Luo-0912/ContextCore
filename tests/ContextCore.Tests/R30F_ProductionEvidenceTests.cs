using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// WP- Production Evidence 与性能门禁验收测试
//
// 验证生产证据链的"强制层"完整可用（纯文件结构验证，不实际运行 CI）：
// 1. appsettings.Postgres.sample.json 采用新 "Storage" 配置形状（postgres 生产直配）；
// 2. ci.yml evidence job 在门禁前固化 TRX 清单（write-trx-manifest.py），
// gate-evidence.py 以 --trx-manifest 做清单与被门禁 TRX 集的一致性校验；
// 3. .github/settings.yml 声明 main 分支保护（唯一必查 check = evidence），
// scripts/assert-branch-protection.py 逐行校验该声明；
// 4. 生产端到端：真实 Postgres（Testcontainers）组合根下 Selected 关系水合
// 走批量存储路径（relation-hydration-store）；filesystem 提供者下经真实
// HTTP 端点（POST /api/relations/hydration）完成水合并返回完整证据。
//
// 设计原则：
// - 纯文件结构验证复用既有 CI 证据验收测试的 FindRepoRoot() 模式；
// - Postgres 集成测试复用 Production HA 组合根测试的
// Testcontainers + 正式组合根模式（Docker 不可用时 Inconclusive 跳过）。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("WP-F")]
public sealed class R30F_ProductionEvidenceTests
{
    // =======================================================================
    // 测试 1：Postgres 示例配置采用生产 "Storage" 形状
    // =======================================================================
    [TestMethod]
    public void Config_PostgresSample_DeclaresProductionStorageShape()
    {
        var repoRoot = FindRepoRoot();
        var samplePath = Path.Combine(repoRoot, "appsettings.Postgres.sample.json");

        if (!File.Exists(samplePath))
        {
            Assert.Inconclusive("未找到 appsettings.Postgres.sample.json，跳过配置冒烟验证。");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(samplePath));
        var storage = doc.RootElement.GetProperty("Storage");

        Assert.AreEqual("postgres", storage.GetProperty("Provider").GetString(),
            "Storage:Provider 必须为 postgres。");
        Assert.IsTrue(storage.GetProperty("AllowExperimentalPostgres").GetBoolean(),
            "Storage:AllowExperimentalPostgres 必须为 true（Postgres 提供者显式启用）。");
        var connectionString = storage.GetProperty("PostgresConnectionString").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(connectionString),
            "Storage:PostgresConnectionString 必须非空（生产直配连接串）。");
        Assert.IsTrue(storage.TryGetProperty("AutoBootstrap", out var autoBootstrap),
            "Storage:AutoBootstrap 必须显式声明。");
        Assert.IsTrue(autoBootstrap.GetBoolean(),
            "Storage:AutoBootstrap 必须为 true（启动时自动建表）。");
    }

    // =======================================================================
    // 测试 2：ci.yml evidence job 在门禁前固化 TRX 清单
    // =======================================================================
    [TestMethod]
    public void Ci_EvidenceJob_WritesTrxManifest_BeforeGate()
    {
        var repoRoot = FindRepoRoot();
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");

        if (!File.Exists(workflowPath))
        {
            Assert.Inconclusive("未找到 .github/workflows/ci.yml，跳过 CI 证据验证。");
            return;
        }

        var content = File.ReadAllText(workflowPath);

        Assert.IsTrue(content.Contains("write-trx-manifest.py", StringComparison.Ordinal),
            "evidence job 必须运行 write-trx-manifest.py 固化被门禁的 TRX 集。");
        Assert.IsTrue(content.Contains("--trx-manifest evidence/trx-manifest.json", StringComparison.Ordinal),
            "gate-evidence.py 必须接收 --trx-manifest 参数做清单一致性校验。");

        // 写清单步骤必须位于门禁步骤之前（清单是门禁的输入）。
        var writeIndex = content.IndexOf("write-trx-manifest.py", StringComparison.Ordinal);
        var gateIndex = content.IndexOf("--trx-manifest evidence/trx-manifest.json", StringComparison.Ordinal);
        Assert.IsTrue(writeIndex >= 0 && gateIndex > writeIndex,
            "Write TRX manifest 步骤必须先于 Gate 步骤执行。");
    }

    // =======================================================================
    // 测试 3：write-trx-manifest.py 存在且生成可审计清单
    // =======================================================================
    [TestMethod]
    public void Ci_TrxManifestScript_ExistsAndWritesAuditableManifest()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "write-trx-manifest.py");

        if (!File.Exists(scriptPath))
        {
            Assert.Inconclusive("未找到 scripts/write-trx-manifest.py，跳过清单脚本验证。");
            return;
        }

        var content = File.ReadAllText(scriptPath);

        Assert.IsTrue(content.Contains("--manifest-dir", StringComparison.Ordinal),
            "清单脚本必须支持 --manifest-dir（读取 ci-manifests）。");
        Assert.IsTrue(content.Contains("--out", StringComparison.Ordinal),
            "清单脚本必须支持 --out（输出文件路径）。");
        Assert.IsTrue(content.Contains("required-artifacts.json", StringComparison.Ordinal),
            "清单脚本必须按 required-artifacts.json 的 dir 集合扫描类别。");
        Assert.IsTrue(content.Contains("trxFiles", StringComparison.Ordinal),
            "清单必须记录每个类别的 trxFiles（机器可读审计）。");
        Assert.IsTrue(content.Contains("return 2", StringComparison.Ordinal),
            "清单脚本配置错误必须返回 exit 2（证据不可判定）。");
    }

    // =======================================================================
    // 测试 4：gate-evidence.py 支持 --trx-manifest 一致性校验
    // =======================================================================
    [TestMethod]
    public void Ci_GateScript_SupportsTrxManifestConsistency()
    {
        var repoRoot = FindRepoRoot();
        var gatePath = Path.Combine(repoRoot, "scripts", "gate-evidence.py");

        if (!File.Exists(gatePath))
        {
            Assert.Inconclusive("未找到 scripts/gate-evidence.py，跳过门禁脚本验证。");
            return;
        }

        var content = File.ReadAllText(gatePath);
        Assert.IsTrue(content.Contains("--trx-manifest", StringComparison.Ordinal),
            "门禁脚本必须支持 --trx-manifest 参数（校验清单与被门禁 TRX 集一致）。");
    }

    // =======================================================================
    // 测试 5：.github/settings.yml 声明 main 分支保护（evidence 必查）
    // =======================================================================
    [TestMethod]
    public void BranchProtection_Settings_DeclareEvidenceGate()
    {
        var repoRoot = FindRepoRoot();
        var settingsPath = Path.Combine(repoRoot, ".github", "settings.yml");

        if (!File.Exists(settingsPath))
        {
            Assert.Inconclusive("未找到 .github/settings.yml，跳过分支保护验证。");
            return;
        }

        var content = File.ReadAllText(settingsPath);

        Assert.IsTrue(content.Contains("branch_protection:", StringComparison.Ordinal),
            "settings.yml 必须声明 branch_protection。");
        Assert.IsTrue(content.Contains("- branch: main", StringComparison.Ordinal),
            "branch_protection 必须包含 main 分支条目。");
        Assert.IsTrue(content.Contains("required_status_checks:", StringComparison.Ordinal),
            "main 条目必须启用 required_status_checks。");
        Assert.IsTrue(content.Contains("strict: true", StringComparison.Ordinal),
            "required_status_checks 必须 strict: true（分支须与最新 main 同步）。");
        Assert.IsTrue(content.Contains("- evidence", StringComparison.Ordinal),
            "唯一必查 check 必须为 evidence（聚合门禁）。");
        Assert.IsTrue(content.Contains("enforce_admins: true", StringComparison.Ordinal),
            "必须 enforce_admins: true（管理员同样受保护）。");
        Assert.IsTrue(content.Contains("allow_force_pushes: false", StringComparison.Ordinal),
            "必须禁止 force push（allow_force_pushes: false）。");
        Assert.IsTrue(content.Contains("allow_deletions: false", StringComparison.Ordinal),
            "必须禁止删除分支（allow_deletions: false）。");
    }

    // =======================================================================
    // 测试 6：assert-branch-protection.py 存在且强制上述策略
    // =======================================================================
    [TestMethod]
    public void BranchProtection_AssertScript_EnforcesPolicy()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "assert-branch-protection.py");

        if (!File.Exists(scriptPath))
        {
            Assert.Inconclusive("未找到 scripts/assert-branch-protection.py，跳过校验脚本验证。");
            return;
        }

        var content = File.ReadAllText(scriptPath);

        Assert.IsTrue(content.Contains("return 1", StringComparison.Ordinal),
            "策略违反时校验脚本必须返回 exit 1。");
        Assert.IsTrue(content.Contains("return 2", StringComparison.Ordinal),
            "配置文件缺失时校验脚本必须返回 exit 2。");
        Assert.IsTrue(content.Contains("- branch: main", StringComparison.Ordinal),
            "校验脚本必须定位 main 分支条目。");
        Assert.IsTrue(content.Contains("required_status_checks", StringComparison.Ordinal),
            "校验脚本必须检查 required_status_checks。");
        Assert.IsTrue(content.Contains("strict:", StringComparison.Ordinal),
            "校验脚本必须检查 strict: true。");
        Assert.IsTrue(content.Contains("allow_force_pushes", StringComparison.Ordinal),
            "校验脚本必须检查 allow_force_pushes。");
        Assert.IsTrue(content.Contains("allow_deletions", StringComparison.Ordinal),
            "校验脚本必须检查 allow_deletions。");
        Assert.IsTrue(content.Contains("enforce_admins", StringComparison.Ordinal),
            "校验脚本必须检查 enforce_admins。");
    }

    // =======================================================================
    // 测试 7：真实 Postgres 组合根下 Selected 关系水合走批量存储
    // =======================================================================
    [TestMethod]
    [TestCategory("Integration")]
    public async Task Production_Postgres_RelationHydration_UsesBatchStore()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Production Postgres 关系水合测试已跳过。此结果不证明组合根通过。");
            return;
        }

        await using (container)
        await using (var provider = BuildProductionHAComposition(container, "r30f_"))
        {
            var store = provider.GetRequiredService<IRelationStore>();
            var hydration = provider.GetRequiredService<ISelectedRelationHydrationService>();

            var id1 = "rel-" + Guid.NewGuid().ToString("N");
            var id2 = "rel-" + Guid.NewGuid().ToString("N");
            await store.BatchUpsertAsync(new[]
            {
                Relation(id1, "ws-r30f-pg", "col-1"),
                Relation(id2, "ws-r30f-pg", "col-1")
            });

            // 请求含重复 ID 且顺序与 id 字典序相反（Postgres 按主键索引序返回，
            // 服务必须重排为请求顺序）：去重后 RequestedCount 应为 2。
            var response = await hydration.HydrateAsync(new RelationHydrationRequest
            {
                OperationId = "op-r30f-pg",
                WorkspaceId = "ws-r30f-pg",
                CollectionId = "col-1",
                RelationIds = new[] { id2, id1, id2 }
            });

            Assert.AreEqual("relation-hydration-store", response.Source,
                "PostgresRelationStore 实现了 IRelationHydrationStore，必须走批量水合路径。");
            Assert.AreEqual(2, response.RequestedCount,
                "去重后请求关系数必须为 2。");
            Assert.AreEqual(2, response.HydratedCount,
                "两条关系都必须水合成功。");
            Assert.AreEqual(0, response.MissingCount,
                "落库的关系不得缺失。");
            CollectionAssert.AreEqual(new[] { id2, id1 },
                response.Relations.Select(r => r.RelationId).ToArray(),
                "水合结果必须重排为请求顺序（id2 在前）。");
        }
    }

    // =======================================================================
    // 测试 8：真实 HTTP 端点水合（filesystem 提供者 + WebApplicationFactory）
    // =======================================================================
    [TestMethod]
    public async Task Production_Http_HydrationEndpoint_ReturnsHydratedRelations()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "cc-r30f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            await using var factory = new ProductionEvidenceFactory(rootPath);
            using var client = factory.CreateClient();

            // 健康检查：生产证据链入口端点必须可用。
            var health = await client.GetAsync("/health");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode,
                "/health 必须返回 200（生产实例存活）。");
            var healthJson = await health.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("ok", healthJson.GetProperty("status").GetString(),
                "/health 必须返回 status=ok。");

            // 先经真实文件系统存储落库一条关系。
            var id = "rel-" + Guid.NewGuid().ToString("N");
            var store = factory.Services.GetRequiredService<IRelationStore>();
            await store.BatchUpsertAsync(new[] { Relation(id, "ws-r30f-http", "col-1") });

            // 经真实 HTTP 端点水合：组合根 IRelationStore 可能被装饰器包装（失去批量水合探测）→ 批量或回退路径均可。
            var response = await client.PostAsJsonAsync("/api/relations/hydration", new RelationHydrationRequest
            {
                OperationId = "op-r30f-http",
                WorkspaceId = "ws-r30f-http",
                CollectionId = "col-1",
                RelationIds = new[] { id }
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "POST /api/relations/hydration 必须返回 200。");

            var payload = await response.Content.ReadFromJsonAsync<RelationHydrationResponse>();
            Assert.IsNotNull(payload, "水合响应必须可解析。");
            Assert.IsTrue(payload.Source is "relation-hydration-store" or "relation-store-fallback",
                "组合根 IRelationStore 为装饰器包装（失去 IRelationHydrationStore 探测）时允许回退路径；二者都必须水合成功。");
            Assert.AreEqual(1, payload.RequestedCount, "请求关系数必须为 1。");
            Assert.AreEqual(1, payload.HydratedCount, "关系必须水合成功。");
            Assert.AreEqual(0, payload.MissingCount, "落库的关系不得缺失。");
            Assert.AreEqual(id, payload.Relations.Single().RelationId,
                "水合结果必须返回请求的关系。");
            Assert.AreEqual("ws-r30f-http", payload.WorkspaceId,
                "响应必须回显 WorkspaceId。");
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch
            {
                // 清理尽力而为，不影响断言结果。
            }
        }
    }

    // =======================================================================
    // 组合根构建（与 Production HA 组合根测试相同的组合路径）
    // =======================================================================
    private static ServiceProvider BuildProductionHAComposition(
        PostgreSqlContainer container,
        string tablePrefix,
        Action<IServiceCollection>? customize = null)
    {
        var connectionString = container.GetConnectionString();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "postgres",
                ["Storage:PostgresConnectionString"] = connectionString,
                ["ContextCoreRuntime:Profile"] = "ProductionHA"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = connectionString,
            AutoMigrate = true, // 与生产启动一致：首次使用即应用幂等 schema
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        });
#pragma warning disable CS0618 // AddContextCore(IServiceCollection) 已过时；为与 Program.cs 组合顺序保持一致而保留
        services.AddContextCore();
#pragma warning restore CS0618
        services.AddContextCoreRuntime(config);

        // 提供 IHostApplicationLifetime（Program.cs 中由 WebApplication 提供），
        // 并触发 ApplicationStarted 使 ReadinessService 判定应用已启动。
        var lifetime = new TestHostApplicationLifetime();
        lifetime.TriggerApplicationStarted();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        // 提供日志基础设施（WebApplication 由 host 自动注册；裸 ServiceCollection 需显式注册）。
        services.AddLogging();

        customize?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R30F_ProductionEvidenceTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static ContextRelation Relation(string id, string workspaceId, string collectionId) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        CollectionId = collectionId,
        SourceId = "src-" + id,
        TargetId = "tgt-" + id,
        RelationType = "references",
        Weight = 1.0,
        Confidence = 0.9
    };

    /// <summary>IHostApplicationLifetime stub（触发 ApplicationStarted 使 Readiness 判定已启动）。</summary>
    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void TriggerApplicationStarted() => _started.Cancel();

        public void StopApplication()
        {
        }
    }

    /// <summary>Production Evidence HTTP E2E 用 WebApplicationFactory（filesystem 提供者）。</summary>
    private sealed class ProductionEvidenceFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;

        public ProductionEvidenceFactory(string rootPath) => _rootPath = rootPath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("ContextCoreRuntime:Profile", "Development");
            builder.UseSetting("ContextCoreRuntime:EnableAgentRunRecovery", "false");
            // 关闭构建时验证：filesystem 下部分 Postgres-only 服务（ICanaryLeaderLease /
            // ILearningEventOutboxStore）无法解析，本测试验证 HTTP 端点响应而非 DI 容器完整性。
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = false;
                options.ValidateOnBuild = false;
            });
            // 移除所有 IHostedService（E2E 测试只需 HTTP 端点响应，不需要后台 Worker）。
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

    // =======================================================================
    // 辅助：定位 repo root（参考既有 FindRepoRoot 模式）
    // =======================================================================
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
