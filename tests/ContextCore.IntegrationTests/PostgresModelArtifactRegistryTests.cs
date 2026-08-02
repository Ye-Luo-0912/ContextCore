using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// PostgresModelArtifactRegistry 端到端集成测试（Testcontainers）。
/// 验证持久化 Model Artifact Registry 的注册、查询、不可变约束与崩溃恢复。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresModelArtifactRegistryTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresModelArtifactRegistryTests] Docker 不可用，所有测试将标记为 Inconclusive。");
            return;
        }

        _container = new PostgreSqlBuilder(PgVectorImage)
            .WithDatabase("cctest")
            .WithUsername("cctest")
            .WithPassword("cctest")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
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

    private static ModelArtifactDescriptor BuildDescriptor(
        string modelArtifactId,
        string modelName,
        string modelVersion,
        string contentHash,
        DateTimeOffset? registeredAt = null,
        string? artifactPath = null,
        string? description = null) => new()
        {
            ModelArtifactId = modelArtifactId,
            ModelName = modelName,
            ModelVersion = modelVersion,
            FeatureSchemaVersion = "v1.0",
            CalibrationVersion = "v1.0",
            EngineKind = InferenceEngineKind.RealModel,
            ContentHash = contentHash,
            ArtifactPath = artifactPath,
            Description = description,
            RegisteredAt = registeredAt ?? DateTimeOffset.UtcNow
        };

    // ── 测试方法 ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Register_ThenGet_ReturnsDescriptor()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar1_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var descriptor = BuildDescriptor(
                modelArtifactId: "text-classifier-1.0.0-a1b2c3d4",
                modelName: "text-classifier",
                modelVersion: "1.0.0",
                contentHash: "sha256:abc123",
                artifactPath: "/models/text-classifier-1.0.0.onnx",
                description: "Initial production model");

            await registry.RegisterAsync(descriptor);

            var fetched = await registry.GetAsync("text-classifier-1.0.0-a1b2c3d4");
            Assert.IsNotNull(fetched, "Register 后 Get 应返回描述符。");
            Assert.AreEqual("text-classifier-1.0.0-a1b2c3d4", fetched!.ModelArtifactId);
            Assert.AreEqual("text-classifier", fetched.ModelName);
            Assert.AreEqual("1.0.0", fetched.ModelVersion);
            Assert.AreEqual("v1.0", fetched.FeatureSchemaVersion);
            Assert.AreEqual("v1.0", fetched.CalibrationVersion);
            Assert.AreEqual(InferenceEngineKind.RealModel, fetched.EngineKind);
            Assert.AreEqual("sha256:abc123", fetched.ContentHash);
            Assert.AreEqual("/models/text-classifier-1.0.0.onnx", fetched.ArtifactPath);
            Assert.AreEqual("Initial production model", fetched.Description);
            Assert.AreEqual(descriptor.RegisteredAt, fetched.RegisteredAt);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Register_DuplicateArtifactId_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar2_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var descriptor = BuildDescriptor(
                modelArtifactId: "dup-1.0.0-abc",
                modelName: "dup-model",
                modelVersion: "1.0.0",
                contentHash: "sha256:dup");

            await registry.RegisterAsync(descriptor);

            // 重复注册同一 ModelArtifactId 应抛异常（不可变语义）
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await registry.RegisterAsync(descriptor));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Get_OnUnknownId_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar3_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);

            var fetched = await registry.GetAsync("nonexistent-id");
            Assert.IsNull(fetched, "不存在的 ModelArtifactId 应返回 null。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Get_WithNullOrWhitespace_ReturnsNullWithoutQuery()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar4_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);

            Assert.IsNull(await registry.GetAsync(""));
            Assert.IsNull(await registry.GetAsync("   "));
            Assert.IsNull(await registry.GetAsync(null!));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task GetLatest_ReturnsMostRecentlyRegisteredVersion()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar5_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var baseTime = DateTimeOffset.UtcNow;

            // 注册三个版本，时间递增
            await registry.RegisterAsync(BuildDescriptor(
                "model-x-1.0.0-a", "model-x", "1.0.0", "hash-a",
                registeredAt: baseTime));
            await registry.RegisterAsync(BuildDescriptor(
                "model-x-1.1.0-b", "model-x", "1.1.0", "hash-b",
                registeredAt: baseTime.AddSeconds(10)));
            await registry.RegisterAsync(BuildDescriptor(
                "model-x-2.0.0-c", "model-x", "2.0.0", "hash-c",
                registeredAt: baseTime.AddSeconds(20)));

            var latest = await registry.GetLatestAsync("model-x");
            Assert.IsNotNull(latest, "GetLatest 应返回最新版本。");
            Assert.AreEqual("model-x-2.0.0-c", latest!.ModelArtifactId);
            Assert.AreEqual("2.0.0", latest.ModelVersion);
            Assert.AreEqual("hash-c", latest.ContentHash);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task GetLatest_OnUnknownModelName_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar6_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var latest = await registry.GetLatestAsync("nonexistent-model");
            Assert.IsNull(latest);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ListByVersion_ReturnsAllVersionsInRegisteredAtAscendingOrder()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar7_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var baseTime = DateTimeOffset.UtcNow;

            // 故意乱序注册，验证返回按 RegisteredAt 升序
            await registry.RegisterAsync(BuildDescriptor(
                "model-y-2.0.0-late", "model-y", "2.0.0", "hash-late",
                registeredAt: baseTime.AddSeconds(100)));
            await registry.RegisterAsync(BuildDescriptor(
                "model-y-1.0.0-early", "model-y", "1.0.0", "hash-early",
                registeredAt: baseTime));
            await registry.RegisterAsync(BuildDescriptor(
                "model-y-1.5.0-mid", "model-y", "1.5.0", "hash-mid",
                registeredAt: baseTime.AddSeconds(50)));

            var versions = await registry.ListByVersionAsync("model-y");
            Assert.AreEqual(3, versions.Count, "应返回该模型名的所有版本。");
            Assert.AreEqual("model-y-1.0.0-early", versions[0].ModelArtifactId);
            Assert.AreEqual("model-y-1.5.0-mid", versions[1].ModelArtifactId);
            Assert.AreEqual("model-y-2.0.0-late", versions[2].ModelArtifactId);
            Assert.IsTrue(versions[0].RegisteredAt <= versions[1].RegisteredAt);
            Assert.IsTrue(versions[1].RegisteredAt <= versions[2].RegisteredAt);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ListByVersion_OnUnknownModelName_ReturnsEmptyList()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar8_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var versions = await registry.ListByVersionAsync("nonexistent-model");
            Assert.IsNotNull(versions);
            Assert.AreEqual(0, versions.Count);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ListAll_ReturnsAllDescriptorsInRegisteredAtAscendingOrder()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar9_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var baseTime = DateTimeOffset.UtcNow;

            await registry.RegisterAsync(BuildDescriptor(
                "model-z-1.0.0", "model-z", "1.0.0", "hash-z1",
                registeredAt: baseTime));
            await registry.RegisterAsync(BuildDescriptor(
                "model-w-1.0.0", "model-w", "1.0.0", "hash-w1",
                registeredAt: baseTime.AddSeconds(10)));
            await registry.RegisterAsync(BuildDescriptor(
                "model-z-2.0.0", "model-z", "2.0.0", "hash-z2",
                registeredAt: baseTime.AddSeconds(20)));

            var all = await registry.ListAllAsync();
            Assert.AreEqual(3, all.Count, "应返回所有已注册描述符。");
            Assert.AreEqual("model-z-1.0.0", all[0].ModelArtifactId);
            Assert.AreEqual("model-w-1.0.0", all[1].ModelArtifactId);
            Assert.AreEqual("model-z-2.0.0", all[2].ModelArtifactId);
            Assert.IsTrue(all[0].RegisteredAt <= all[1].RegisteredAt);
            Assert.IsTrue(all[1].RegisteredAt <= all[2].RegisteredAt);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ListAll_OnEmptyRegistry_ReturnsEmptyList()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar10_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var all = await registry.ListAllAsync();
            Assert.IsNotNull(all);
            Assert.AreEqual(0, all.Count);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CrashRecovery_NewRegistryInstanceReadsPersistedDescriptors()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar11_");
        try
        {
            // 第一个 registry 实例（模拟崩溃前的进程）
            var registry1 = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var descriptor = BuildDescriptor(
                "crash-model-1.0.0-x", "crash-model", "1.0.0", "hash-crash",
                artifactPath: "/models/crash-1.0.0.onnx");

            await registry1.RegisterAsync(descriptor);

            // 模拟进程崩溃：丢弃 registry1，创建新实例（同一数据库）
            var registry2 = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);

            // 新实例应能读取持久化的描述符
            var fetched = await registry2.GetAsync("crash-model-1.0.0-x");
            Assert.IsNotNull(fetched, "崩溃恢复后应能读取持久化的描述符。");
            Assert.AreEqual("crash-model", fetched!.ModelName);
            Assert.AreEqual("1.0.0", fetched.ModelVersion);
            Assert.AreEqual("hash-crash", fetched.ContentHash);
            Assert.AreEqual("/models/crash-1.0.0.onnx", fetched.ArtifactPath);

            // 新实例可继续注册新版本（恢复后继续使用）
            await registry2.RegisterAsync(BuildDescriptor(
                "crash-model-1.1.0-y", "crash-model", "1.1.0", "hash-crash-v2"));
            var latest = await registry2.GetLatestAsync("crash-model");
            Assert.AreEqual("crash-model-1.1.0-y", latest!.ModelArtifactId);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Register_WithNullOptionalFields_PersistsAndReturnsNullOnGet()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar12_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var descriptor = BuildDescriptor(
                "minimal-1.0.0-z", "minimal-model", "1.0.0", "hash-min",
                artifactPath: null,
                description: null);

            await registry.RegisterAsync(descriptor);

            var fetched = await registry.GetAsync("minimal-1.0.0-z");
            Assert.IsNotNull(fetched);
            Assert.IsNull(fetched!.ArtifactPath, "ArtifactPath 为 null 应正确持久化与读取。");
            Assert.IsNull(fetched.Description, "Description 为 null 应正确持久化与读取。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Register_DeterministicReplayEngineKind_PersistsCorrectly()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mar13_");
        try
        {
            var registry = new PostgresModelArtifactRegistry(factory, serializer, migrationRunner);
            var descriptor = new ModelArtifactDescriptor
            {
                ModelArtifactId = "replay-model-1.0.0-r",
                ModelName = "replay-model",
                ModelVersion = "1.0.0",
                FeatureSchemaVersion = "v1.0",
                CalibrationVersion = "v1.0",
                EngineKind = InferenceEngineKind.DeterministicReplay,
                ContentHash = "sha256:replay",
                ArtifactPath = null,
                Description = "Deterministic replay model",
                RegisteredAt = DateTimeOffset.UtcNow
            };

            await registry.RegisterAsync(descriptor);

            var fetched = await registry.GetAsync("replay-model-1.0.0-r");
            Assert.IsNotNull(fetched);
            Assert.AreEqual(InferenceEngineKind.DeterministicReplay, fetched!.EngineKind,
                "DeterministicReplay engine_kind 应正确持久化与读取。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
