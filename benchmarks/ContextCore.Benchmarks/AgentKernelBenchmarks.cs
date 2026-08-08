using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Benchmarks;

// ===========================================================================
// Agent Kernel 微基准
//
// 覆盖：
// InMemoryToolDispatchJournal 状态机（Prepared → Dispatched → Committed → ResultDelivered）
// DefaultAgentCheckpointFactory.CreateCheckpointAsync（Full + Delta 模式）
// InMemoryAgentCheckpointStore SaveAsync / GetAsync 往返
//
// 数据规模：[Params(1, 10, 100)] 覆盖单 turn / 小批次 / 大批次指令
// 指标：Mean / Median / StdDev / P95（BenchmarkDotNet 默认）+ Allocated bytes（[MemoryDiagnoser]）
//
// 依赖：
// - InMemoryToolDispatchJournal（Durable Tool Journal 进程内默认实现）
// - DefaultAgentCheckpointFactory + KernelStateAccessor（checkpoint 序列化工具）
// - InMemoryAgentCheckpointStore（生产 InMemory checkpoint store）
// ===========================================================================

/// <summary>
/// ToolDispatchJournal 状态机微基准。
/// 测量 Prepared → Dispatched → Committed → ResultDelivered 全状态机推进。
/// </summary>
[MemoryDiagnoser]
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
                WorkspaceId = "bench-ws",
                RunId = $"run-{i}",
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
            var key = new TenantRunKey(entry.WorkspaceId!, entry.RunId!);
            await journal.PrepareAsync(entry).ConfigureAwait(false);
            await journal.MarkDispatchedAsync(key, entry.RequestId, externalOperationId: $"ext-{entry.RequestId}").ConfigureAwait(false);
            await journal.MarkCommittedAsync(key, entry.RequestId).ConfigureAwait(false);
            await journal.MarkResultDeliveredAsync(key, entry.RequestId).ConfigureAwait(false);
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
        var last = _entries[^1];
        _ = await journal.GetEntryAsync(new TenantRunKey(last.WorkspaceId!, last.RunId!), last.RequestId).ConfigureAwait(false);
    }
}

/// <summary>
/// Agent Checkpoint Factory 微基准。
/// 测量 Full / Delta 两种 checkpoint 模式的创建开销。
/// </summary>
[MemoryDiagnoser]
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
/// Checkpoint Store Save + Get 往返。
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AgentCheckpointStoreBenchmarks
{
    private const string WorkspaceId = "bench-ws";

    [Params(1, 10, 50)]
    public int CheckpointCount { get; set; }

    private List<AgentCheckpoint> _checkpoints = null!;

    [GlobalSetup]
    public void Setup()
    {
        _checkpoints = new List<AgentCheckpoint>(CheckpointCount);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < CheckpointCount; i++)
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
