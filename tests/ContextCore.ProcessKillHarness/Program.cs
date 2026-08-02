using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

// ===========================================================================
// WP-S6 Production Evidence：真进程 Kill 测试专用 Harness
//
// 目的：作为独立操作系统进程运行（由集成测试 Process.Start 启动），
// 模拟"生产节点 A"正在执行一个 Run：
//   1. 连接真实 Postgres（连接字符串 / table prefix 由参数注入）；
//   2. 创建 Run 并获取 Run Lease（默认 15s，owner=kill-harness-owner）；
//   3. 通过 AgentRunActor 执行 Run：模型返回 Tool 调用 → 分派到阻塞型 Tool；
//   4. Tool 副作用开始时写入 effect 文件 + tool-started marker（集成测试据此判断
//      "已执行到 Kill Point"），随后阻塞直到进程被 Kill（或 5 分钟兜底上限）。
//
// 集成测试在 marker 出现后执行 Process.Kill(true)（真进程终止，非优雅退出），
// 随后验证：Run 数据未丢失、journal 处于 DispatchingIntent 模糊态、lease 过期后
// 新节点可抢占（fencing token 递增）、Tool 副作用不重复执行。
//
// 设计说明：
//   - 不依赖 AgentKernelHost 心跳循环，直接由 Actor 携带 lease 执行——进程被 Kill 后
//     lease 无人续约，到期后新节点可抢占，行为与生产一致且完全确定。
//   - 与集成测试共享同一 table prefix，确保恢复端（测试进程）读写同一 schema 与数据。
// ===========================================================================

var options = Harness.ParseArgs(args);
if (options is null)
{
    return 1;
}

try
{
    var (factory, migrationRunner, serializer) = Harness.CreateInfrastructure(options);
    await migrationRunner.MigrateAsync().ConfigureAwait(false);

    var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
    var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
    var leaseStore = new PostgresAgentRunLease(factory, serializer, migrationRunner);
    var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

    var startedMarker = Path.Combine(options.MarkerDir, "tool-started.marker");
    var effectFile = Path.Combine(options.MarkerDir, "effect.txt");
    var completedMarker = Path.Combine(options.MarkerDir, "harness-completed.marker");

    // 阻塞型 Tool：先写副作用文件与 started marker，再阻塞直到被 Kill。
    var toolHandler = new BlockingToolHandler(Harness.KillToolName, effectFile, startedMarker);
    var dispatcher = new RealToolDispatcher(new IToolHandler[] { toolHandler });
    dispatcher.Freeze();
    var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);

    // 脚本化模型：第 1 次返回 Tool 调用，第 2 次返回最终答案。
    // Tool 参数 JSON 必须与集成测试恢复端逐字节一致——RequestId 由
    // (runId, modelTurn, toolCallId, toolName, arguments) 哈希生成，两端一致才能命中同一 journal 条目。
    var transport = new ScriptedModelTransport(
        Harness.BuildToolCallResponse(),
        Harness.BuildFinalAnswerResponse("kill-harness 完成。"));

    var run = Harness.BuildRun(options.RunId);
    await runStore.CreateAsync(run).ConfigureAwait(false);

    // 获取 Run Lease。进程被 Kill 后无续约，到期自动释放（与生产一致）。
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var lease = await leaseStore.TryAcquireAsync(
        run.RunId, TimeSpan.FromSeconds(options.LeaseSeconds), "kill-harness-owner", cts.Token).ConfigureAwait(false);
    if (lease is null)
    {
        Console.Error.WriteLine("[harness] 无法获取 Run Lease（可能已被其他实例持有）。");
        return 2;
    }

    var actor = new AgentRunActor(
        runStore, eventStore, transport,
        new DefaultAgentLoopPolicy(),
        dispatcher,
        durableToolExecutor: durableExecutor);

    // 执行 Run：Tool 阻塞时本调用不会返回，进程保持存活直至被 Kill。
    await actor.ExecuteAsync(run, cts.Token, lease.LeaseToken, lease.FencingToken, () => lease.ExpiresAt)
        .ConfigureAwait(false);

    // 未被 Kill 的兜底路径：释放 lease 并写完成 marker（测试应断言此文件不存在）。
    await leaseStore.ReleaseAsync(run.RunId, lease.LeaseToken, CancellationToken.None).ConfigureAwait(false);
    File.WriteAllText(completedMarker, DateTimeOffset.UtcNow.ToString("O"));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[harness] FATAL: {ex}");
    return 1;
}

// ── 参数与基础设施 ───────────────────────────────────────────────────────

public static class Harness
{
    public const string KillWorkspaceId = "ws-kill-prodev";
    public const string KillSessionId = "session-kill-prodev";
    public const string KillToolName = "search";
    public const string KillToolArguments = """{"query":"kill-test"}""";

    public sealed record HarnessOptions(
        string ConnectionString, string TablePrefix, string MarkerDir, string RunId, int LeaseSeconds);

    public static HarnessOptions? ParseArgs(string[] args)
    {
        string? connectionString = null;
        string? tablePrefix = null;
        string? markerDir = null;
        string? runId = null;
        var leaseSeconds = 15;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection-string" when i + 1 < args.Length:
                    connectionString = args[++i];
                    break;
                case "--table-prefix" when i + 1 < args.Length:
                    tablePrefix = args[++i];
                    break;
                case "--marker-dir" when i + 1 < args.Length:
                    markerDir = args[++i];
                    break;
                case "--run-id" when i + 1 < args.Length:
                    runId = args[++i];
                    break;
                case "--lease-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds):
                    leaseSeconds = seconds;
                    i++;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(tablePrefix) ||
            string.IsNullOrWhiteSpace(markerDir) || string.IsNullOrWhiteSpace(runId))
        {
            Console.Error.WriteLine(
                "[harness] 缺少必要参数：--connection-string / --table-prefix / --marker-dir / --run-id。");
            return null;
        }

        return new HarnessOptions(connectionString!, tablePrefix!, markerDir!, runId!, leaseSeconds);
    }

    public static (PostgresConnectionFactory, PostgresMigrationRunner, PostgresJsonSerializer) CreateInfrastructure(HarnessOptions options)
    {
        var pgOptions = new PostgresOptions
        {
            ConnectionString = options.ConnectionString,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = options.TablePrefix
        };
        var factory = new PostgresConnectionFactory(pgOptions);
        return (factory, new PostgresMigrationRunner(factory), new PostgresJsonSerializer());
    }

    public static AgentRun BuildRun(string runId) => new()
    {
        RunId = runId,
        WorkspaceId = KillWorkspaceId,
        SessionId = KillSessionId,
        Task = "kill-harness 任务",
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 5 }
    };

    public static AgentModelResponse BuildToolCallResponse() => new()
    {
        Content = "需要搜索 kill-test 数据",
        ToolCalls = new[]
        {
            new AgentToolCallRequest
            {
                ToolName = KillToolName,
                Arguments = KillToolArguments
            }
        },
        IsFinalAnswer = false,
        TokensConsumed = 10,
        Duration = TimeSpan.FromMilliseconds(5),
        InputTokens = 8,
        OutputTokens = 2,
        ModelId = "scripted-kill-harness"
    };

    public static AgentModelResponse BuildFinalAnswerResponse(string content) => new()
    {
        Content = content,
        ToolCalls = Array.Empty<AgentToolCallRequest>(),
        IsFinalAnswer = true,
        TokensConsumed = 15,
        Duration = TimeSpan.FromMilliseconds(5),
        InputTokens = 10,
        OutputTokens = 5,
        ModelId = "scripted-kill-harness"
    };
}

// ── 阻塞型 Tool ─────────────────────────────────────────────────────────

/// <summary>
/// 写入副作用文件与 started marker 后阻塞的 Tool Handler。
/// 副作用文件（effect.txt）是"外部副作用已发生"的持久证据；恢复端断言该文件只写一次。
/// </summary>
internal sealed class BlockingToolHandler : IToolHandler
{
    private readonly string _effectFile;
    private readonly string _startedMarker;

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
    public string? Description => $"Kill harness tool: {ToolName}";
    public string? ParametersJsonSchema => "{}";

    public BlockingToolHandler(string toolName, string effectFile, string startedMarker)
    {
        ToolName = toolName;
        _effectFile = effectFile;
        _startedMarker = startedMarker;
    }

    public async ValueTask<ToolHandlerResult> HandleAsync(
        ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        // 副作用：写入 effect 文件（恢复端断言此文件只写一次，不重复执行）。
        await File.WriteAllTextAsync(_effectFile, $"effect-once {DateTimeOffset.UtcNow:O}", cancellationToken)
            .ConfigureAwait(false);
        // Kill Point marker：集成测试轮询到此文件后执行 Process.Kill(true)。
        await File.WriteAllTextAsync(_startedMarker, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);
        // 阻塞：测试在此窗口内 Kill 进程。未被 Kill 时 5 分钟后返回（兜底，避免进程永久挂起）。
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 进程被 Kill —— 正常终止路径，不会走到这里。
        }
        return new ToolHandlerResult
        {
            Succeeded = true,
            Result = "blocked-tool-returned",
            SideEffect = ToolSideEffect.None
        };
    }
}

// ── 脚本化模型传输 ───────────────────────────────────────────────────────

/// <summary>
/// 按顺序返回预设响应序列的 IAgentModelTransport（与集成测试共用模式）。
/// 超出序列时返回最后一个响应。
/// </summary>
internal sealed class ScriptedModelTransport : IAgentModelTransport
{
    private readonly AgentModelResponse[] _responses;
    private int _callCount;

    public ScriptedModelTransport(params AgentModelResponse[] responses)
    {
        if (responses.Length == 0)
        {
            throw new ArgumentException("至少需要 1 个预设响应。", nameof(responses));
        }
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
