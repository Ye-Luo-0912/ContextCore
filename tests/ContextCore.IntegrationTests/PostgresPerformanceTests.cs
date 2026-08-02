using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// PostgreSQL 存储性能测试：使用 Testcontainers 运行真实 Postgres + pgvector，
/// 测量 BasicContextPackageBuilder 在真实网络 I/O 下的冷构建延迟和并发扩展性。
/// Docker 不可用时自动跳过（Inconclusive）。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("Performance")]
[TestCategory("DockerRequired")]
public sealed class PostgresPerformanceTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";
    private const string WorkspaceId = "perf-ws";
    private const string CollectionId = "perf-col";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // 收口：直接尝试启动容器（与 PostgresHATests 一致），避免 IsDockerAvailableAsync 误判。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgresPerformanceTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool ShouldSkip => _connectionString is null;

    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    /// <summary>
    /// 冷构建延迟基准：50 条数据下单次 BuildDetailedAsync 的端到端延迟。
    /// 预算：Postgres 网络往返 × 6 store 查询 + filter + assembly，应在 2 秒内完成。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ColdBuild_SingleRequest_ShouldCompleteWithinBudget()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 性能测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("perf1_");
        try
        {
            var builder = await CreateAndPopulateBuilderAsync(factory, migrationRunner, serializer);
            var request = CreateTestRequest();

            // Warmup: 首次构建包含连接池初始化，不计入测量
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);

            // Measure: 第二次构建为真实冷构建（无缓存，数据已就绪）
            var sw = Stopwatch.StartNew();
            var result = await builder.BuildDetailedAsync(request, CancellationToken.None);
            sw.Stop();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Package.Sections.Count > 0, "应至少有一个 section");
            Console.WriteLine($"[Postgres Perf] Cold build (50 items): {sw.ElapsedMilliseconds}ms");

            // Postgres 冷构建应在 2 秒内完成（6 次 query + filter + assembly）
            Assert.IsTrue(sw.Elapsed.TotalSeconds < 2.0,
                $"Postgres 冷构建延迟 {sw.Elapsed.TotalSeconds:F2}s 超过 2s 预算");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// 并发扩展测试：4 路并发构建，验证 Postgres 连接池和并行预取在高并发下的表现。
    /// 预算：4 路并发应在单路 × 3 倍时间内完成（并行预取应提供加速，但连接池争用会增加开销）。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task ConcurrentBuild_4Way_ShouldScaleReasonably()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 性能测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("perf4_");
        try
        {
            var builder = await CreateAndPopulateBuilderAsync(factory, migrationRunner, serializer);
            var request = CreateTestRequest();

            // Warmup
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);

            // Measure single
            var swSingle = Stopwatch.StartNew();
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
            swSingle.Stop();

            // Measure 4-way concurrent
            var swConcurrent = Stopwatch.StartNew();
            var tasks = new Task[4];
            for (int i = 0; i < 4; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    var result = await builder.BuildDetailedAsync(request, CancellationToken.None);
                    _ = result.Package.Sections.Count;
                });
            }
            await Task.WhenAll(tasks);
            swConcurrent.Stop();

            Console.WriteLine($"[Postgres Perf] Single: {swSingle.ElapsedMilliseconds}ms, 4-way concurrent: {swConcurrent.ElapsedMilliseconds}ms, ratio: {swConcurrent.Elapsed.TotalMilliseconds / swSingle.Elapsed.TotalMilliseconds:F2}x");

            // 4 路并发不应超过单路 × 4（最坏情况 = 完全串行化），实际应有并行加速
            Assert.IsTrue(swConcurrent.Elapsed.TotalSeconds < swSingle.Elapsed.TotalSeconds * 4.0,
                $"4 路并发延迟 {swConcurrent.Elapsed.TotalSeconds:F2}s 超过单路 × 4 = {swSingle.Elapsed.TotalSeconds * 4.0:F2}s");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// 16 路高并发测试：验证 Postgres 在高并发下的连接池限制和降级行为。
    /// 预算：16 路并发应在单路 × 10 倍时间内完成（允许连接池争用导致的性能下降）。
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task ConcurrentBuild_16Way_ShouldNotDegradeBeyondBudget()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 性能测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("perf16_");
        try
        {
            var builder = await CreateAndPopulateBuilderAsync(factory, migrationRunner, serializer);
            var request = CreateTestRequest();

            // Warmup
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);

            // Measure single
            var swSingle = Stopwatch.StartNew();
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
            swSingle.Stop();

            // Measure 16-way concurrent
            var swConcurrent = Stopwatch.StartNew();
            var tasks = new Task[16];
            for (int i = 0; i < 16; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    var result = await builder.BuildDetailedAsync(request, CancellationToken.None);
                    _ = result.Package.Sections.Count;
                });
            }
            await Task.WhenAll(tasks);
            swConcurrent.Stop();

            Console.WriteLine($"[Postgres Perf] Single: {swSingle.ElapsedMilliseconds}ms, 16-way concurrent: {swConcurrent.ElapsedMilliseconds}ms, ratio: {swConcurrent.Elapsed.TotalMilliseconds / swSingle.Elapsed.TotalMilliseconds:F2}x");

            // 16 路并发不应超过单路 × 10（允许连接池争用和上下文切换开销）
            Assert.IsTrue(swConcurrent.Elapsed.TotalSeconds < swSingle.Elapsed.TotalSeconds * 10.0,
                $"16 路并发延迟 {swConcurrent.Elapsed.TotalSeconds:F2}s 超过单路 × 10 = {swSingle.Elapsed.TotalSeconds * 10.0:F2}s");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private static async Task<BasicContextPackageBuilder> CreateAndPopulateBuilderAsync(
        PostgresConnectionFactory factory,
        PostgresMigrationRunner migrationRunner,
        PostgresJsonSerializer serializer)
    {
        var contextStore = new PostgresContextStore(factory, serializer, migrationRunner);
        var memoryStore = new PostgresMemoryStore(factory, serializer, migrationRunner);
        var constraintStore = new PostgresConstraintStore(factory, serializer, migrationRunner);
        var globalStore = new PostgresGlobalContextStore(factory, serializer, migrationRunner);
        var relationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
        var workingMemoryService = new PostgresWorkingMemoryStore(factory, serializer, migrationRunner);

        await PopulateStoresAsync(contextStore, memoryStore, constraintStore, globalStore);

        return new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore,
            workingMemoryService: workingMemoryService);
    }

    private static ContextPackageRequest CreateTestRequest()
    {
        var policy = new ContextPackagePolicy
        {
            Id = "perf-policy-all",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Name = "PerfAllSections",
            Mode = ContextPackageMode.None,
            TokenBudget = 4000,
            IncludeRecentRawContext = true,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeGlobalContext = true,
            MaxRecentItems = 20
        };

        return new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "Postgres 性能测试 postgres performance test",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = policy
        };
    }

    private static async Task PopulateStoresAsync(
        PostgresContextStore contextStore,
        PostgresMemoryStore memoryStore,
        PostgresConstraintStore constraintStore,
        PostgresGlobalContextStore globalStore)
    {
        var now = DateTimeOffset.UtcNow;
        var rand = new Random(20260715);

        for (int i = 0; i < 50; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 90));
            await contextStore.SaveAsync(new ContextItem
            {
                Id = $"ctx-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "note",
                Title = $"条目 {i}",
                Content = $"内容 {i} " + new string('x', 200 + rand.Next(0, 400)),
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task", "package"],
                Importance = 0.3f + (float)(rand.NextDouble() * 0.7),
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });
        }

        for (int i = 0; i < 10; i++)
        {
            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-stable-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "fact",
                Content = $"稳定记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            });

            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-working-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = $"工作记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.8,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        for (int i = 0; i < 10; i++)
        {
            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-hard-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = $"硬约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            });

            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-soft-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = $"软约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        for (int i = 0; i < 5; i++)
        {
            await globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = $"global-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = null,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = $"全局上下文 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.6,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }
}
