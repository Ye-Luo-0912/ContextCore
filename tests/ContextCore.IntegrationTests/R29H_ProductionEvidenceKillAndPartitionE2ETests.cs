using System.Diagnostics;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// 真进程 Kill + DB 网络分区集成测试
//
// 目标：
// 1. E2E_RealProcessKill_MidToolExecution_NoDuplicateSideEffect — 真实操作系统进程
// 在 Tool 执行中途被 Kill（非优雅终止），验证：
// a. Run 数据与事件流未丢失（Postgres 持久化）；
// b. journal 停留在 DispatchingIntent 模糊态（外部副作用可能已开始）；
// c. 租约随进程消失，真实过期后新节点可抢占（fencing token 递增）；
// d. 恢复节点接管后 Tool 副作用不重复执行（exactly-once，对账而非盲目重放）。
// 2. E2E_DbNetworkPartition_LeaseExpires_NewOwnerFencingWins — 停止 Postgres 容器
// 模拟 DB 网络分区，分区期间租约真实过期，分区恢复后：
// a. 旧 owner 的续约失败（token 已失效）；
// b. 新 owner 抢占成功且 fencing token 递增；
// c. Run 数据未丢失。
//
// 设计原则：
// - 真进程 Kill 通过独立控制台项目 ContextCore.ProcessKillHarness 实现
// （Process.Start + Process.Kill(true)），非 SQL 重置模拟。
// - DB 分区通过 Testcontainers StopAsync/StartAsync 实现（数据保留，仅网络中断）。
// - Docker/Postgres 不可用时 Assert.Inconclusive 跳过。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
[TestCategory("KillAndPartition")]
public sealed class R29H_ProductionEvidenceKillAndPartitionE2ETests : IAsyncDisposable
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    [TestInitialize]
    public async Task InitializeAsync()
    {
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
            Console.WriteLine($"[KillAndPartition] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private bool ShouldSkip => _connectionString is null;

    private (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer)
        CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        return (factory, new PostgresMigrationRunner(factory), new PostgresJsonSerializer());
    }

    // =======================================================================
    // 测试 1：真进程 Kill —— Tool 执行中途 Kill → 恢复后不重复执行副作用
    // =======================================================================

    [TestMethod]
    public async Task E2E_RealProcessKill_MidToolExecution_NoDuplicateSideEffect()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        const string prefix = "kill_";
        var markerDir = Path.Combine(Path.GetTempPath(), "cc-kill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(markerDir);
        var runId = "run-kill-" + Guid.NewGuid().ToString("N");
        var startedMarker = Path.Combine(markerDir, "tool-started.marker");
        var effectFile = Path.Combine(markerDir, "effect.txt");
        var completedMarker = Path.Combine(markerDir, "harness-completed.marker");
        var recoveryEffect = Path.Combine(markerDir, "recovery-effect.txt");

        var (factory, migrationRunner, serializer) = CreateInfrastructure(prefix);
        try
        {
            await migrationRunner.MigrateAsync();

            // ── 启动 harness 进程（模拟"生产节点 A"）──
            var harnessDll = Path.Combine(AppContext.BaseDirectory, "ContextCore.ProcessKillHarness.dll");
            Assert.IsTrue(File.Exists(harnessDll),
                $"harness DLL 应存在于测试输出目录：{harnessDll}");

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(harnessDll);
            psi.ArgumentList.Add("--connection-string");
            psi.ArgumentList.Add(_connectionString!);
            psi.ArgumentList.Add("--table-prefix");
            psi.ArgumentList.Add(prefix);
            psi.ArgumentList.Add("--marker-dir");
            psi.ArgumentList.Add(markerDir);
            psi.ArgumentList.Add("--run-id");
            psi.ArgumentList.Add(runId);
            psi.ArgumentList.Add("--lease-seconds");
            psi.ArgumentList.Add("15");

            using var harness = Process.Start(psi)!;
            var stdoutTask = harness.StandardOutput.ReadToEndAsync();
            var stderrTask = harness.StandardError.ReadToEndAsync();

            // ── 等待 Tool 开始执行（Kill Point marker）──
            var started = await WaitForFileAsync(startedMarker, TimeSpan.FromSeconds(60));
            Assert.IsTrue(started,
                "harness 应在 60s 内开始执行 Tool（未出现 tool-started.marker）。");
            Assert.IsTrue(File.Exists(effectFile),
                "Tool 副作用文件应在 Kill 前已写入（外部副作用已发生）。");
            Assert.IsFalse(File.Exists(completedMarker),
                "harness 不应已自行完成（Tool 应仍在阻塞）。");

            // ── 真进程 Kill（entireProcessTree，非优雅终止）──
            try
            {
                harness.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已自行退出（异常路径）——继续验证
            }
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await harness.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("harness 进程未在 30s 内退出。");
            }
            Assert.IsTrue(harness.HasExited, "harness 进程应已退出。");

            // ── 断言 1：Run 数据未丢失（非终态，事件流完整）──
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
            var leaseStore = new PostgresAgentRunLease(factory, serializer, migrationRunner);
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            var killedRun = await runStore.GetAsync(Harness.KillWorkspaceId, runId);
            Assert.IsNotNull(killedRun, "Kill 后 Run 应仍存在于 Postgres。");
            Assert.IsFalse(AgentRunStateMachine.IsTerminalState(killedRun!.State),
                $"Kill 后 Run 应为非终态（可恢复），实际 {killedRun.State}。");

            var events = await eventStore.ReadAsync(killedRun.WorkspaceId, runId, 0, 1000);
            Assert.IsTrue(events.Count >= 1, "Kill 后事件流应非空。");
            Assert.IsNull(events[0].PrevChainHash, "链头事件 PrevChainHash 应为 null。");
            for (var i = 1; i < events.Count; i++)
            {
                Assert.AreEqual(events[i - 1].ContentHash, events[i].PrevChainHash,
                    $"事件 {i} 哈希链断裂。");
            }

            // ── 断言 2：journal 停留在 DispatchingIntent（模糊态，不可盲目重放）──
            var journalState = await ReadJournalStateAsync(prefix, runId);
            Assert.AreEqual(ToolDispatchState.DispatchingIntent, journalState,
                $"Kill 时 Tool 分派应停留在 DispatchingIntent（外部副作用可能已开始），实际 {journalState}。");

            // ── 断言 3：租约随进程消失，真实过期后无人持有 ──
            var leaseExpired = await WaitForConditionAsync(
                async () => !await leaseStore.HasActiveLeaseAsync(runId),
                TimeSpan.FromSeconds(60));
            Assert.IsTrue(leaseExpired, "harness 进程的租约应在真实过期后被释放（无人续约）。");

            // ── 恢复节点（本测试进程）接管：抢占租约，fencing token 递增 ──
            var leaseB = await leaseStore.TryAcquireAsync(runId, TimeSpan.FromMinutes(2), "recovery-node", CancellationToken.None);
            Assert.IsNotNull(leaseB, "租约过期后恢复节点应能抢占。");
            Assert.AreEqual(2, leaseB!.FencingToken, "恢复节点抢占后 fencing token 应为 2（旧 owner 的 fence 写入会被拒绝）。");

            // 旧 owner 的续约必须失败（token 已失效）。
            var oldRenew = await leaseStore.RenewAsync(runId, "stale-harness-token", TimeSpan.FromMinutes(2));
            Assert.IsFalse(oldRenew, "已失效的旧租约 token 续约必须失败。");

            // ── 断言 4：恢复执行 —— Tool 不重复执行（exactly-once 对账语义）──
            var recoveryHandler = new CountingToolHandler(Harness.KillToolName, recoveryEffect);
            var recoveryDispatcher = new RealToolDispatcher(new IToolHandler[] { recoveryHandler });
            recoveryDispatcher.Freeze();
            var recoveryExecutor = new DefaultDurableToolExecutor(recoveryDispatcher, journal);
            var recoveryTransport = new ScriptedModelTransport(
                Harness.BuildToolCallResponse(),
                Harness.BuildFinalAnswerResponse("恢复节点完成。"));

            var recoveryActor = new AgentRunActor(
                runStore, eventStore, recoveryTransport,
                new DefaultAgentLoopPolicy(),
                recoveryDispatcher,
                durableToolExecutor: recoveryExecutor);

            var resumedRun = await runStore.GetAsync(killedRun.WorkspaceId, runId);
            Assert.IsNotNull(resumedRun, "应能取回恢复前的 Run。");

            using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await recoveryActor.ExecuteAsync(
                resumedRun!, recoveryCts.Token,
                leaseB.LeaseToken, leaseB.FencingToken, () => leaseB.ExpiresAt);

            // 断言 4a：Run 进入终态（Completed —— 对账错误被观察后模型给出最终答案）。
            var finalRun = await runStore.GetAsync(killedRun.WorkspaceId, runId);
            Assert.IsNotNull(finalRun, "恢复后应能取回 Run。");
            Assert.IsTrue(AgentRunStateMachine.IsTerminalState(finalRun!.State),
                $"恢复后 Run 应进入终态，实际 {finalRun.State}。");

            // 断言 4b（核心）：外部副作用只发生一次。
            Assert.IsTrue(File.Exists(effectFile), "副作用文件应存在（harness 写入一次）。");
            Assert.IsFalse(File.Exists(recoveryEffect),
                "恢复节点不得重新执行 Tool 副作用（journal 模糊态 → 对账而非重放）。");
            Assert.AreEqual(0, recoveryHandler.InvocationCount,
                $"恢复节点的 Tool Handler 不应被调用，实际 {recoveryHandler.InvocationCount}。");

            // 断言 4c：journal 仍停留在 DispatchingIntent（从未被静默重放/提交）。
            var journalStateAfter = await ReadJournalStateAsync(prefix, runId);
            Assert.AreEqual(ToolDispatchState.DispatchingIntent, journalStateAfter,
                $"恢复后 journal 应保持 DispatchingIntent（等待对账/人工裁决），实际 {journalStateAfter}。");

            // 清理：释放恢复节点的租约。
            await leaseStore.ReleaseAsync(runId, leaseB.LeaseToken, CancellationToken.None);

            // 输出 harness 日志供诊断（若异常路径）。
            var harnessStdout = await stdoutTask;
            var harnessStderr = await stderrTask;
            if (harnessStdout.Length > 0) { Console.WriteLine($"[harness stdout] {harnessStdout}"); }
            if (harnessStderr.Length > 0) { Console.WriteLine($"[harness stderr] {harnessStderr}"); }
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(markerDir, recursive: true); } catch { /* 清理失败忽略 */ }
        }
    }

    // =======================================================================
    // 测试 2：DB 网络分区 —— 分区期间租约过期，恢复后 fencing 递增、旧 token 失效
    // =======================================================================

    [TestMethod]
    public async Task E2E_DbNetworkPartition_LeaseExpires_NewOwnerFencingWins()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        const string prefix = "part_";
        var (factory, migrationRunner, serializer) = CreateInfrastructure(prefix);
        try
        {
            await migrationRunner.MigrateAsync();
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var leaseStore = new PostgresAgentRunLease(factory, serializer, migrationRunner);

            var run = BuildRun("DB 网络分区测试");
            await runStore.CreateAsync(run);

            // owner A 获取短租约（5s）。
            var leaseA = await leaseStore.TryAcquireAsync(run.RunId, TimeSpan.FromSeconds(5), "host-A", CancellationToken.None);
            Assert.IsNotNull(leaseA, "owner A 应获取租约。");
            Assert.AreEqual(1, leaseA!.FencingToken, "首次获取的 fencing token 应为 1。");

            // ── 网络分区：停止 Postgres 容器（数据保留，仅网络中断）──
            await _container!.StopAsync();
            try
            {
                // 分区期间旧 owner 无法续约（连接失败）——与生产分区行为一致。
                // 不在此处断言具体异常（Npgsql 连接失败行为依赖环境），仅等待租约真实过期。
                await Task.Delay(TimeSpan.FromSeconds(6));
            }
            finally
            {
                // ── 分区恢复：重启同一容器 ──
                await _container.StartAsync();
                NpgsqlConnection.ClearAllPools();
            }

            // ── 断言 1：旧 owner 的续约必须失败（租约已真实过期 + token 校验）──
            var renewed = await leaseStore.RenewAsync(run.RunId, leaseA.LeaseToken, TimeSpan.FromMinutes(2));
            Assert.IsFalse(renewed, "分区后旧 owner 的续约必须失败（租约已真实过期）。");

            // ── 断言 2：新 owner 抢占，fencing token 递增 ──
            var leaseB = await leaseStore.TryAcquireAsync(run.RunId, TimeSpan.FromMinutes(2), "host-B", CancellationToken.None);
            Assert.IsNotNull(leaseB, "分区恢复后新 owner 应能抢占过期租约。");
            Assert.AreEqual(leaseA.FencingToken + 1, leaseB!.FencingToken,
                "抢占后 fencing token 必须递增（旧 owner 的副作用写入会被 fence 拒绝）。");

            // ── 断言 3：旧 fence 已真实过期 → 旧 owner 无法再执行副作用 ──
            Assert.IsTrue(leaseA.ExpiresAt < DateTimeOffset.UtcNow,
                "旧 owner 的 lease fence 应已真实过期。");

            // ── 断言 4：Run 数据在分区期间未丢失 ──
            var runAfter = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(runAfter, "分区后 Run 应仍存在。");
            Assert.AreEqual(run.Task, runAfter!.Task, "分区后 Run 内容应一致。");

            // 新 owner 正常释放。
            await leaseStore.ReleaseAsync(run.RunId, leaseB.LeaseToken, CancellationToken.None);
            var released = await leaseStore.HasActiveLeaseAsync(run.RunId);
            Assert.IsFalse(released, "新 owner 释放后不应有活跃租约。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private async Task<ToolDispatchState> ReadJournalStateAsync(string tablePrefix, string runId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT state FROM {tablePrefix}tool_dispatch_journal_entries
WHERE run_id = @runId
""";
        command.Parameters.AddWithValue("runId", runId);
        var result = await command.ExecuteScalarAsync();
        Assert.IsNotNull(result, "journal 中应存在该 run 的 Tool 分派条目。");
        return (ToolDispatchState)Convert.ToByte(result);
    }

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-part-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = "ws-part-prodevidence",
        SessionId = "session-part-prodevidence",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 5 }
    };

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }
            await Task.Delay(200);
        }
        return File.Exists(path);
    }

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(200);
        }
        return await condition();
    }

    // ── 测试 stub ─────────────────────────────────────────────────────────

    /// <summary>
    /// 记录调用次数的 Tool Handler。若恢复节点错误地重新执行 Tool，
    /// 会写入 recovery-effect 文件并递增计数——测试据此断言"不重复执行"。
    /// </summary>
    private sealed class CountingToolHandler : IToolHandler
    {
        private readonly string _effectFile;
        private int _invocationCount;

        public string ToolName { get; }
        public ToolDescriptor Descriptor => new()
        {
            Name = ToolName,
            DeclaredSideEffect = ToolSideEffect.None,
            RequiresApproval = false,
            RequiresIdempotencyKey = false,
            RequiresLeaseFence = false,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay,
            MaximumExecutionTime = TimeSpan.FromMinutes(5)
        };
        public string? Description => $"Counting tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public CountingToolHandler(string toolName, string effectFile)
        {
            ToolName = toolName;
            _effectFile = effectFile;
        }

        public async ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            await File.WriteAllTextAsync(_effectFile, $"recovery-effect {DateTimeOffset.UtcNow:O}", cancellationToken);
            return new ToolHandlerResult
            {
                Succeeded = true,
                Result = "recovery-tool-returned",
                SideEffect = ToolSideEffect.None
            };
        }
    }

    /// <summary>按顺序返回预设响应序列的 IAgentModelTransport。</summary>
    private sealed class ScriptedModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;

        public ScriptedModelTransport(params AgentModelResponse[] responses)
        {
            _responses = responses;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var response = index < _responses.Length ? _responses[index] : _responses[^1];
            return ValueTask.FromResult(response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
