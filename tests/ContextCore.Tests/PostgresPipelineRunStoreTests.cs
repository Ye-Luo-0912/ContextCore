using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// R27-2：PostgresPipelineRunStore 单元测试。
///
/// 不连接真实 PostgreSQL 数据库；仅验证：
///   1. 构造函数与服务注册
///   2. 参数校验（null / 空字符串在 EnsureMigrated 之前抛）
///   3. 接口实现契约（IPipelineRunStore）
///   4. DI 注册路径（PostgresServiceCollectionExtensions）
///   5. P0-7：TryTransitionAsync 参数校验 + cancellation 透传（CAS 推进路径）
///
/// 端到端持久化语义（CAS 成功 / 失败 / 幂等 / 并发）由 ContextCore.IntegrationTests 覆盖
/// （需 Testcontainers）— 与 InMemoryPipelineRunStoreTests 中对应的 10 个 TryTransitionAsync
/// 行为测试对齐。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Postgres")]
[TestCategory("R27")]
public sealed class PostgresPipelineRunStoreTests
{
    // =========================================================================
    // 1. 构造函数
    // =========================================================================

    // 注：PostgresStoreBase 基类构造函数不抛 ArgumentNullException（与既有 Postgres store 一致），
    // 所以这里不测 Constructor_NullFactory / Constructor_NullSerializer / Constructor_NullMigrationRunner。
    // 既有 PostgresDecisionTraceStore / PostgresAgentCheckpointStore 等也遵循相同约定。

    [TestMethod]
    public void Constructor_ValidArguments_CreatesInstance()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        var store = new PostgresPipelineRunStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));

        Assert.IsInstanceOfType<IPipelineRunStore>(store);
    }

    // =========================================================================
    // 2. SaveRunAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task SaveRunAsync_NullSnapshot_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveRunAsync(null!));
    }

    // =========================================================================
    // 3. GetRunAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task GetRunAsync_NullId_ThrowsArgumentNullException()
    {
        // ThrowIfNullOrWhiteSpace 在 null 时抛 ArgumentNullException（而非 ArgumentException）
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetRunAsync(null!));
    }

    [TestMethod]
    public async Task GetRunAsync_EmptyId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetRunAsync(""));
    }

    [TestMethod]
    public async Task GetRunAsync_WhitespaceId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetRunAsync("   "));
    }

    // =========================================================================
    // 4. ListRunsByProposalAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task ListRunsByProposalAsync_NullProposalId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListRunsByProposalAsync(null!));
    }

    [TestMethod]
    public async Task ListRunsByProposalAsync_NegativeTake_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => store.ListRunsByProposalAsync("prop-1", take: -1));
    }

    // =========================================================================
    // 5. DeleteRunAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task DeleteRunAsync_NullId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.DeleteRunAsync(null!));
    }

    [TestMethod]
    public async Task DeleteRunAsync_EmptyId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteRunAsync(""));
    }

    // =========================================================================
    // 6. SaveCanaryAssignmentAsync / ListCanaryAssignmentsByRunAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task SaveCanaryAssignmentAsync_NullAssignment_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveCanaryAssignmentAsync(null!));
    }

    [TestMethod]
    public async Task ListCanaryAssignmentsByRunAsync_NullRunId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListCanaryAssignmentsByRunAsync(null!));
    }

    // =========================================================================
    // 7. SaveRollbackRecordAsync / GetRollbackRecordByRunAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task SaveRollbackRecordAsync_NullRecord_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveRollbackRecordAsync(null!));
    }

    [TestMethod]
    public async Task GetRollbackRecordByRunAsync_NullRunId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetRollbackRecordByRunAsync(null!));
    }

    // =========================================================================
    // 8. SaveBaselineComparisonAsync / ListBaselineComparisonsByProposalAsync 参数校验
    // =========================================================================

    [TestMethod]
    public async Task SaveBaselineComparisonAsync_NullComparison_Throws()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveBaselineComparisonAsync(null!));
    }

    [TestMethod]
    public async Task ListBaselineComparisonsByProposalAsync_NullProposalId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListBaselineComparisonsByProposalAsync(null!));
    }

    // =========================================================================
    // 9. CancellationToken 传递（与 PostgresAgentCheckpointStoreTests 对齐）
    // =========================================================================

    [TestMethod]
    public async Task SaveRunAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        // 已取消 token 传入时，调用不应 hang；EnsureMigratedAsync 不检查 cancellation，
        // OpenConnectionAsync 在 cancellation 已取消时立即抛 OperationCanceledException（Npgsql 内部检查）。
        // 由于 Npgsql 版本/连接字符串行为差异，这里接受 Exception 基类以验证 "快速失败" 行为。
        var store = CreateStoreWithoutConnection();
        var snapshot = MakeRunSnapshot("run-1", "prop-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.SaveRunAsync(snapshot, cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    // =========================================================================
    // 10. TryTransitionAsync 参数校验（P0-7）
    // =========================================================================

    [TestMethod]
    public async Task TryTransitionAsync_NullRunId_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.TryTransitionAsync(null!, 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_EmptyRunId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.TryTransitionAsync("", 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_WhitespaceRunId_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.TryTransitionAsync("   ", 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_NullNext_ThrowsArgumentNullException()
    {
        var store = CreateStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, null!));
    }

    [TestMethod]
    public async Task TryTransitionAsync_RunIdMismatch_ThrowsArgumentException()
    {
        var store = CreateStoreWithoutConnection();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.TryTransitionAsync("different-run", 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        // 已取消 token 传入时，调用不应 hang；EnsureMigratedAsync 不检查 cancellation，
        // OpenConnectionAsync 在 cancellation 已取消时立即抛 OperationCanceledException（Npgsql 内部检查）。
        // 由于 Npgsql 版本/连接字符串行为差异，这里接受 Exception 基类以验证 "快速失败" 行为。
        var store = CreateStoreWithoutConnection();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next, null, cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    // =========================================================================
    // 11. DI 注册验证（PostgresServiceCollectionExtensions）
    // =========================================================================

    [TestMethod]
    public async Task AddContextCorePostgresStorage_RegistersPostgresPipelineRunStore()
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(PostgresPipelineRunStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IPipelineRunStore)));

        // PostgresConnectionFactory 仅实现 IAsyncDisposable，需用 await using 释放容器
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IPipelineRunStore>();
        Assert.IsInstanceOfType<PostgresPipelineRunStore>(store);
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_PostgresImplOverridesInMemory()
    {
        // R27-3：模拟完整启动顺序 — 先注册 InMemory（AddInMemoryPipelineRunStore 默认路径），
        // 再 AddContextCorePostgresStorage（postgres provider），后注册者胜出。
        var services = new ServiceCollection();
        services.AddSingleton<IPipelineRunStore, InMemoryPipelineRunStore>();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IPipelineRunStore>();
        Assert.IsInstanceOfType<PostgresPipelineRunStore>(store);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static PostgresPipelineRunStore CreateStoreWithoutConnection()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        return new PostgresPipelineRunStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));
    }

    private static PipelineRunSnapshot MakeRunSnapshot(
        string runId,
        string proposalId,
        OptimizationStage stage = OptimizationStage.OfflineExperiment,
        long revision = 1,
        string? lastTransitionId = null) => new()
    {
        RunId = runId,
        ProposalId = proposalId,
        ProposalVersion = OptimizationProposalVersion.Initial,
        Proposal = new OptimizationProposal
        {
            ProposalId = proposalId,
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.PackagePolicy,
            Status = OptimizationProposalStatus.ExperimentReady,
            RollbackConditions = new[]
            {
                new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
            }
        },
        CurrentStage = stage,
        Status = PipelineRunStatus.Running,
        StartedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        // P0-7：HA 字段（Revision 为 required，必须显式赋值）
        Revision = revision,
        LastTransitionId = lastTransitionId
    };
}
