using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Benchmarks;

// ===========================================================================
// R29 WP-F-1：Agent Kernel 微基准
//
// 覆盖：
//   §1 InMemoryToolDispatchJournal 状态机（Prepared → Dispatched → Committed → ResultDelivered）
//   §2 DefaultAgentCheckpointFactory.CreateCheckpointAsync（Full + Delta 模式）
//   §3 DefaultAgentKernel 端到端（Submit Execute 指令 → Tool dispatch → Result outbox）
//   §4 InMemoryAgentCheckpointStore SaveAsync / GetAsync 往返
//
// 数据规模：[Params(1, 10, 100)] 覆盖单 turn / 小批次 / 大批次指令
// 指标：Mean / Median / StdDev / P95（BenchmarkDotNet 默认）+ Allocated bytes（[MemoryDiagnoser]）
//
// 依赖：
//   - EchoToolDispatcher（echo tool，SideEffect.None）作为 IToolDispatcher 桩
//   - InProcessTransport（Channel-based IAgentKernelTransport）
//   - InMemoryAgentCheckpointStore（生产 InMemory checkpoint store）
// ===========================================================================

/// <summary>
/// WP-F-1 §1：ToolDispatchJournal 状态机微基准。
/// 测量 Prepared → Dispatched → Committed → ResultDelivered 全状态机推进。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ToolDispatchJournalBenchmarks
{
    [Params(1, 10, 100)]
    public int EntryCount { get; set; }

    private List<ToolDispatchJournalEntry> _entries = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entries = new List<ToolDispatchJournalEntry>(EntryCount);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < EntryCount; i++)
        {
            _entries.Add(new ToolDispatchJournalEntry
            {
                RequestId = $"req-{i}",
                ToolName = "echo",
                State = ToolDispatchState.Prepared,
                UpdatedAt = now
            });
        }
    }

    [Benchmark]
    public async Task StateMachine_FullCycle()
    {
        var journal = new InMemoryToolDispatchJournal();
        foreach (var entry in _entries)
        {
            await journal.PrepareAsync(entry).ConfigureAwait(false);
            await journal.MarkDispatchedAsync(entry.RequestId, externalOperationId: $"ext-{entry.RequestId}").ConfigureAwait(false);
            await journal.MarkCommittedAsync(entry.RequestId).ConfigureAwait(false);
            await journal.MarkResultDeliveredAsync(entry.RequestId).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task StateMachine_PrepareAndQuery()
    {
        var journal = new InMemoryToolDispatchJournal();
        foreach (var entry in _entries)
        {
            await journal.PrepareAsync(entry).ConfigureAwait(false);
        }
        // 查询最后一个 entry
        _ = await journal.GetEntryAsync(_entries[^1].RequestId).ConfigureAwait(false);
    }
}

/// <summary>
/// WP-F-1 §2：Agent Checkpoint Factory 微基准。
/// 测量 Full / Delta 两种 checkpoint 模式的创建开销。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CheckpointFactoryBenchmarks
{
    private const string WorkspaceId = "bench-ws";

    [Params(1, 10, 100)]
    public int CommittedResultCount { get; set; }

    private DefaultAgentCheckpointFactory _fullFactory = null!;
    private DefaultAgentCheckpointFactory _deltaFactory = null!;
    private Dictionary<string, ToolDispatchResult> _committedResults = null!;
    private Dictionary<string, long> _resultSequences = null!;
    private Dictionary<string, ToolDispatchResult> _pendingResults = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 构造 committed results（模拟 Kernel 内部状态）
        _committedResults = new Dictionary<string, ToolDispatchResult>(StringComparer.Ordinal);
        _resultSequences = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < CommittedResultCount; i++)
        {
            var reqId = $"req-{i}";
            _committedResults[reqId] = new ToolDispatchResult
            {
                Succeeded = true,
                Result = $"echo payload #{i}",
                Duration = TimeSpan.FromMilliseconds(1),
                SideEffect = ToolSideEffect.None
            };
            _resultSequences[reqId] = i + 1;
        }
        _pendingResults = new Dictionary<string, ToolDispatchResult>(StringComparer.Ordinal);

        // Full 模式：使用简化构造函数（cursor 永远 0）
        _fullFactory = new DefaultAgentCheckpointFactory(new DefaultAgentCheckpointFactory.KernelStateAccessor(
            getLastSnapshotId: () => null,
            getCommittedResults: () => _committedResults));

        // Delta 模式：使用完整构造函数（带 cursor + pending）
        var lastSeq = CommittedResultCount;
        _deltaFactory = new DefaultAgentCheckpointFactory(new DefaultAgentCheckpointFactory.KernelStateAccessor(
            getLastSnapshotId: () => $"snap-{CommittedResultCount - 1}",
            getCommittedResults: () => _committedResults,
            getCommittedResultSequences: () => _resultSequences,
            getPendingResults: () => _pendingResults,
            getLastCheckpointSequence: () => lastSeq,
            getLastCheckpointId: () => $"ckpt-{CommittedResultCount - 1}"));
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Full")]
    public async Task<AgentCheckpoint> CreateCheckpoint_Full()
        => await _fullFactory.CreateCheckpointAsync(
            checkpointId: $"ckpt-full-{Guid.NewGuid():N}",
            sessionId: "bench-session",
            workspaceId: WorkspaceId).ConfigureAwait(false);

    [Benchmark]
    [BenchmarkCategory("Delta")]
    public async Task<AgentCheckpoint> CreateCheckpoint_Delta()
        => await _deltaFactory.CreateCheckpointAsync(
            checkpointId: $"ckpt-delta-{Guid.NewGuid():N}",
            sessionId: "bench-session",
            workspaceId: WorkspaceId).ConfigureAwait(false);
}

/// <summary>
/// WP-F-1 §3+§4：Agent Kernel 端到端 + Checkpoint Store 往返。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AgentKernelEndToEndBenchmarks
{
    private const string WorkspaceId = "bench-ws";

    [Params(1, 10, 50)]
    public int InstructionCount { get; set; }

    private List<AgentKernelInstruction> _executeInstructions = null!;
    private List<AgentCheckpoint> _checkpoints = null!;

    [GlobalSetup]
    public void Setup()
    {
        _executeInstructions = new List<AgentKernelInstruction>(InstructionCount);
        for (int i = 0; i < InstructionCount; i++)
        {
            _executeInstructions.Add(new AgentKernelInstruction
            {
                InstructionId = $"instr-{i}",
                Kind = AgentKernelInstructionKind.Execute,
                Payload = $"echo payload #{i}",
                Metadata = new Dictionary<string, string>
                {
                    ["tool"] = "echo",
                    ["sessionId"] = "bench-session",
                    ["workspaceId"] = WorkspaceId
                }
            });
        }

        // 预构造 checkpoints 用于 store 往返测试
        _checkpoints = new List<AgentCheckpoint>(InstructionCount);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < InstructionCount; i++)
        {
            _checkpoints.Add(new AgentCheckpoint
            {
                CheckpointId = $"ckpt-{i}",
                Session = new AgentSessionId
                {
                    Value = "bench-session",
                    WorkspaceId = WorkspaceId,
                    CreatedAt = now
                },
                CreatedAt = now,
                StateJson = """{"mode":"full","sequence":""" + i + "}"
            });
        }
    }

    // §3 Kernel 端到端：Submit Execute 指令 → Run → 等待结果
    [Benchmark]
    [BenchmarkCategory("Kernel")]
    public async Task Kernel_SubmitAndRun()
    {
        var transport = new InProcessTransport(capacity: 256);
        var kernel = new DefaultAgentKernel(
            transport,
            new EchoToolDispatcher(),
            new InMemoryAgentCheckpointStore());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = kernel.RunAsync(cts.Token).AsTask();

        foreach (var instr in _executeInstructions)
        {
            await kernel.SubmitAsync(instr, cts.Token).ConfigureAwait(false);
        }

        // 读取所有结果
        for (int i = 0; i < InstructionCount; i++)
        {
            _ = await transport.ReceiveResultAsync(cts.Token).ConfigureAwait(false);
        }

        // 提交 Shutdown 指令让 RunAsync 自然结束
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown,
            Metadata = new Dictionary<string, string>()
        }, cts.Token).ConfigureAwait(false);

        await runTask.ConfigureAwait(false);
    }

    // §4 Checkpoint Store Save + Get 往返
    [Benchmark]
    [BenchmarkCategory("Store")]
    public async Task CheckpointStore_SaveAndGet()
    {
        var store = new InMemoryAgentCheckpointStore();
        foreach (var ckpt in _checkpoints)
        {
            await store.SaveAsync(ckpt).ConfigureAwait(false);
        }
        // 读取最后一个
        _ = await store.GetAsync(WorkspaceId, _checkpoints[^1].CheckpointId).ConfigureAwait(false);
    }
}
