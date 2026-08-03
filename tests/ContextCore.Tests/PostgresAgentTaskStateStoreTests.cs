using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// PostgresAgentTaskStateStore 单元测试。
///
/// 不连接真实 PostgreSQL 数据库；仅验证：
/// 1. 构造函数与服务注册
/// 2. 参数校验（null / 空字符串在 EnsureMigrated 之前抛）
/// 3. 接口实现契约（IAgentTaskStateStore）
/// 4. DI 注册路径（PostgresServiceCollectionExtensions）
/// 5. <see cref="GetAsync"/> / <see cref="DeleteAsync"/> 必须传 workspaceId，
/// workspaceId 与 taskId 的 null / empty / whitespace 校验独立
///
/// 端到端持久化语义由 ContextCore.IntegrationTests 覆盖（需 Testcontainers）。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Postgres")]
[TestCategory("R26")]
public sealed class PostgresAgentTaskStateStoreTests
{
    // =========================================================================
    // 1. 构造函数
    // =========================================================================

    // 注：PostgresStoreBase 基类构造函数不抛 ArgumentNullException（与既有 Postgres store 一致），
    // 所以这里不测 Constructor_NullFactory / Constructor_NullSerializer / Constructor_NullMigrationRunner。

    [TestMethod]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        var store = new PostgresAgentTaskStateStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));

        Assert.IsInstanceOfType<IAgentTaskStateStore>(store);
    }

    // =========================================================================
    // 2. SaveAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task SaveAsync_NullTaskState_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveAsync(null!));
    }

    // =========================================================================
    // 3. GetAsync 参数校验（workspaceId + taskId）
    // =========================================================================

    [TestMethod]
    public async Task GetAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        // ThrowIfNullOrWhiteSpace 在 null 时抛 ArgumentNullException
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetAsync(null!, "task-1"));
    }

    [TestMethod]
    public async Task GetAsync_EmptyWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("", "task-1"));
    }

    [TestMethod]
    public async Task GetAsync_WhitespaceWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("   ", "task-1"));
    }

    [TestMethod]
    public async Task GetAsync_NullTaskId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetAsync("ws-1", null!));
    }

    [TestMethod]
    public async Task GetAsync_EmptyTaskId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task GetAsync_WhitespaceTaskId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws-1", "   "));
    }

    // =========================================================================
    // 4. ListBySessionAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task ListBySessionAsync_NullSession_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListBySessionAsync(null!));
    }

    // =========================================================================
    // 5. DeleteAsync 参数校验（workspaceId + taskId）
    // =========================================================================

    [TestMethod]
    public async Task DeleteAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.DeleteAsync(null!, "task-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("", "task-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_WhitespaceWorkspaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("   ", "task-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_NullTaskId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.DeleteAsync("ws-1", null!));
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyTaskId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task DeleteAsync_WhitespaceTaskId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("ws-1", "   "));
    }

    // =========================================================================
    // 6. DI 注册验证（PostgresServiceCollectionExtensions）
    // =========================================================================

    [TestMethod]
    public async Task AddContextCorePostgresStorage_RegistersPostgresAgentTaskStateStore()
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(PostgresAgentTaskStateStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IAgentTaskStateStore)));

        // PostgresConnectionFactory 仅实现 IAsyncDisposable，需用 await using 释放容器
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IAgentTaskStateStore>();
        Assert.IsInstanceOfType<PostgresAgentTaskStateStore>(store);
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_PostgresImplOverridesInMemory()
    {
        // 模拟完整启动顺序 — 先注册 InMemory（AddContextCore 默认路径），
        // 再 AddContextCorePostgresStorage（postgres provider），后注册者胜出。
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTaskStateStore, ContextCore.Core.Services.Agent.InMemoryAgentTaskStateStore>();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IAgentTaskStateStore>();
        Assert.IsInstanceOfType<PostgresAgentTaskStateStore>(store);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static PostgresAgentTaskStateStore CreateStoreWithoutConnection()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        return new PostgresAgentTaskStateStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));
    }

    private static AgentSessionId MakeSessionId(string value) => new()
    {
        Value = value,
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = "ws-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentTaskState MakeTask(string taskId, string status = "Running") => new()
    {
        TaskId = taskId,
        Session = MakeSessionId("session-1"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Status = status
    };
}
