using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// PostgresAgentCheckpointStore 单元测试。
///
/// 不连接真实 PostgreSQL 数据库；仅验证：
///   1. 构造函数与服务注册
///   2. 参数校验（null / 空字符串在 EnsureMigrated 之前抛）
///   3. 接口实现契约（IAgentCheckpointStore）
///   4. DI 注册路径（PostgresServiceCollectionExtensions）
///   5. P0-6：<see cref="GetAsync"/> / <see cref="DeleteAsync"/> 必须传 workspaceId，
///      workspaceId 与 checkpointId 的 null / empty / whitespace 校验独立
///
/// 端到端持久化语义由 ContextCore.IntegrationTests 覆盖（需 Testcontainers）。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Postgres")]
[TestCategory("R26")]
public sealed class PostgresAgentCheckpointStoreTests
{
    // =========================================================================
    // 1. 构造函数
    // =========================================================================

    // 注：PostgresStoreBase 基类构造函数不抛 ArgumentNullException（与既有 Postgres store 一致），
    // 所以这里不测 Constructor_NullFactory / Constructor_NullSerializer / Constructor_NullMigrationRunner。
    // 既有 PostgresDecisionTraceStore / PostgresContextStateVersionStore 等也遵循相同约定。

    [TestMethod]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        var store = new PostgresAgentCheckpointStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));

        Assert.IsInstanceOfType<IAgentCheckpointStore>(store);
    }

    // =========================================================================
    // 2. SaveAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task SaveAsync_NullCheckpoint_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveAsync(null!));
    }

    // =========================================================================
    // 3. GetAsync 参数校验（P0-6：workspaceId + checkpointId）
    // =========================================================================

    [TestMethod]
    public async Task GetAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        // ThrowIfNullOrWhiteSpace 在 null 时抛 ArgumentNullException
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetAsync(null!, "ckpt-1"));
    }

    [TestMethod]
    public async Task GetAsync_EmptyWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("", "ckpt-1"));
    }

    [TestMethod]
    public async Task GetAsync_WhitespaceWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("   ", "ckpt-1"));
    }

    [TestMethod]
    public async Task GetAsync_NullCheckpointId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetAsync("ws-1", null!));
    }

    [TestMethod]
    public async Task GetAsync_EmptyCheckpointId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task GetAsync_WhitespaceCheckpointId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws-1", "   "));
    }

    // =========================================================================
    // 4. ListAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task ListAsync_NullSession_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListAsync(null!));
    }

    [TestMethod]
    public async Task ListAsync_NegativeTake_Throws()
    {
        var store = CreateStoreWithoutConnection();
        var sessionId = MakeSessionId("session-1");
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => store.ListAsync(sessionId, take: -1));
    }

    // =========================================================================
    // 5. DeleteAsync 参数校验（P0-6：workspaceId + checkpointId）
    // =========================================================================

    [TestMethod]
    public async Task DeleteAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.DeleteAsync(null!, "ckpt-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("", "ckpt-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_WhitespaceWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("   ", "ckpt-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_NullCheckpointId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.DeleteAsync("ws-1", null!));
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyCheckpointId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task DeleteAsync_WhitespaceCheckpointId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("ws-1", "   "));
    }

    // =========================================================================
    // 6. CancellationToken 传递
    // =========================================================================

    [TestMethod]
    public async Task SaveAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        // 已取消 token 传入时，调用不应 hang；EnsureMigratedAsync 不检查 cancellation，
        // OpenConnectionAsync 在 cancellation 已取消时立即抛 OperationCanceledException（Npgsql 内部检查）。
        // 由于 Npgsql 版本/连接字符串行为差异，这里接受 Exception 基类以验证 "快速失败" 行为。
        var store = CreateStoreWithoutConnection();
        var cp = MakeCheckpoint("ckpt-1", "session-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.SaveAsync(cp, cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    // =========================================================================
    // 7. DI 注册验证（PostgresServiceCollectionExtensions）
    // =========================================================================

    [TestMethod]
    public async Task AddContextCorePostgresStorage_RegistersPostgresAgentCheckpointStore()
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(PostgresAgentCheckpointStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IAgentCheckpointStore)));

        // PostgresConnectionFactory 仅实现 IAsyncDisposable，需用 await using 释放容器
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IAgentCheckpointStore>();
        Assert.IsInstanceOfType<PostgresAgentCheckpointStore>(store);
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_PostgresImplOverridesInMemory()
    {
        // 模拟完整启动顺序 — 先注册 InMemory（AddContextCore 默认路径），
        // 再 AddContextCorePostgresStorage（postgres provider），后注册者胜出。
        var services = new ServiceCollection();
        services.AddSingleton<IAgentCheckpointStore, ContextCore.Core.Services.Agent.InMemoryAgentCheckpointStore>();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IAgentCheckpointStore>();
        Assert.IsInstanceOfType<PostgresAgentCheckpointStore>(store);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static PostgresAgentCheckpointStore CreateStoreWithoutConnection()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        return new PostgresAgentCheckpointStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));
    }

    private static AgentSessionId MakeSessionId(string value) => new()
    {
        Value = value,
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = "ws-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentCheckpoint MakeCheckpoint(string id, string sessionValue) => new()
    {
        CheckpointId = id,
        Session = MakeSessionId(sessionValue),
        CreatedAt = DateTimeOffset.UtcNow,
        StateJson = "{}"
    };
}
