using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-E P1-1 / R28-G P1-5：DefaultAgentCheckpointFactory
//
// 统一手动 Checkpoint 指令与自动 AutoCheckpoint 的状态格式。
// 两者均通过此工厂构建 AgentCheckpoint，序列化 KernelCheckpointState
// （CommittedResults + PendingResults + SnapshotId + Mode + LastSequence）
// 到 StateJson，确保 ResumeAsync 可靠恢复。
//
// R28-G P1-5 delta checkpoint 设计：
//   - 工厂读取 accessor 的 _lastCheckpointSequence（cursor）。
//   - cursor == 0：emit Full 模式（序列化所有 in-memory committed results）。
//   - cursor > 0：emit Delta 模式（仅序列化 Sequence > cursor 的新增条目）。
//   - StateJson 中 Mode=Delta 时附带 BaseCheckpointId 链接前一 checkpoint。
//   - Kernel.ResumeAsync 在 Delta 模式下递归加载 BaseCheckpoint 重建完整状态。
//
// 设计决策：
//   - 工厂持有 Kernel 的可变状态引用（_committedToolResults + _lastSnapshot
//     + _committedResultSequences + _pendingToolResults + _lastCheckpointSequence
//     + _lastCheckpointId），通过 KernelStateAccessor 委托读取。
//   - 序列化格式向后兼容：新增字段（Mode/BaseCheckpointId/LastSequence/
//     PendingResults/Sequence）默认值与旧 checkpoint 兼容（Mode 默认 Full）。
//   - 工厂为 sealed class，构造时注入 KernelStateAccessor。
// ===========================================================================

/// <summary>
/// R28-E P1-1 / R28-G P1-5：默认 Agent Checkpoint 工厂实现。
/// 统一所有 checkpoint 入口（手动/自动）的状态格式，支持 delta checkpoint。
/// </summary>
public sealed class DefaultAgentCheckpointFactory : IAgentCheckpointFactory
{
    private readonly KernelStateAccessor _stateAccessor;

    /// <summary>构造默认 checkpoint 工厂。</summary>
    /// <param name="stateAccessor">Kernel 状态访问器（读取已提交结果 + snapshot 引用 + delta cursor）。</param>
    public DefaultAgentCheckpointFactory(KernelStateAccessor stateAccessor)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
    }

    /// <inheritdoc />
    public ValueTask<AgentCheckpoint> CreateCheckpointAsync(
        string checkpointId,
        string sessionId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            throw new ArgumentException("checkpointId 不能为空。", nameof(checkpointId));
        }

        var snapshotId = _stateAccessor.GetLastSnapshotId();
        var committedResults = _stateAccessor.GetCommittedResults();
        var sequences = _stateAccessor.GetCommittedResultSequences();
        var pendingResults = _stateAccessor.GetPendingResults();
        var lastCheckpointSequence = _stateAccessor.GetLastCheckpointSequence();
        var lastCheckpointId = _stateAccessor.GetLastCheckpointId();

        // R28-G P1-5：根据 cursor 决定 Full / Delta 模式
        var isDelta = lastCheckpointSequence > 0;
        var mode = isDelta ? CheckpointMode.Delta : CheckpointMode.Full;

        // 计算本次 checkpoint 的 LastSequence（= 当前最大 Sequence，若无则保持 cursor）
        var currentMaxSequence = sequences.Count > 0 ? sequences.Values.Max() : lastCheckpointSequence;

        // 过滤要序列化的 committed results
        // - Full：全部
        // - Delta：仅 Sequence > cursor 的新增条目
        List<CommittedToolResultDto> committedDtos;
        if (isDelta)
        {
            committedDtos = committedResults
                .Where(kv => sequences.TryGetValue(kv.Key, out var seq) && seq > lastCheckpointSequence)
                .Select(kv => new CommittedToolResultDto
                {
                    RequestId = kv.Key,
                    Succeeded = kv.Value.Succeeded,
                    Result = kv.Value.Result,
                    Error = kv.Value.Error,
                    SideEffect = kv.Value.SideEffect,
                    Sequence = sequences.TryGetValue(kv.Key, out var s) ? s : 0
                })
                .ToList();
        }
        else
        {
            committedDtos = committedResults
                .Select(kv => new CommittedToolResultDto
                {
                    RequestId = kv.Key,
                    Succeeded = kv.Value.Succeeded,
                    Result = kv.Value.Result,
                    Error = kv.Value.Error,
                    SideEffect = kv.Value.SideEffect,
                    Sequence = sequences.TryGetValue(kv.Key, out var s) ? s : 0
                })
                .ToList();
        }

        // R28-G P1-5：始终序列化 pending results（量小，未提交，必须随 checkpoint 持久化以恢复 Unknown 副作用状态）
        var pendingDtos = pendingResults
            .Select(kv => new CommittedToolResultDto
            {
                RequestId = kv.Key,
                Succeeded = kv.Value.Succeeded,
                Result = kv.Value.Result,
                Error = kv.Value.Error,
                SideEffect = kv.Value.SideEffect,
                Sequence = 0 // pending results 不参与序号追踪
            })
            .ToList();

        var state = new KernelCheckpointStateDto
        {
            SnapshotId = snapshotId,
            Mode = mode,
            BaseCheckpointId = isDelta ? lastCheckpointId : null,
            LastSequence = currentMaxSequence,
            CommittedResults = committedDtos,
            PendingResults = pendingDtos
        };

        var stateJson = JsonSerializer.Serialize(state);

        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = checkpointId,
            Session = new AgentSessionId
            {
                Value = sessionId,
                WorkspaceId = workspaceId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = snapshotId,
            StateJson = stateJson
        };

        return ValueTask.FromResult(checkpoint);
    }

    /// <summary>R28-E P1-1 / R28-G P1-5：Kernel 状态访问器（读取已提交结果 + snapshot 引用 + delta cursor）。</summary>
    /// <remarks>
    /// 通过委托避免直接暴露 Kernel 内部字段；工厂构造时由 Kernel 注入。
    /// 新增（P1-5）的访问器委托可为 null（兼容旧调用方）；null 时退回默认值（cursor=0 → Full 模式）。
    /// </remarks>
    public sealed class KernelStateAccessor
    {
        private readonly Func<string?> _getLastSnapshotId;
        private readonly Func<IReadOnlyDictionary<string, ToolDispatchResult>> _getCommittedResults;
        private readonly Func<IReadOnlyDictionary<string, long>>? _getCommittedResultSequences;
        private readonly Func<IReadOnlyDictionary<string, ToolDispatchResult>>? _getPendingResults;
        private readonly Func<long>? _getLastCheckpointSequence;
        private readonly Func<string?>? _getLastCheckpointId;

        /// <summary>构造 Kernel 状态访问器（兼容旧签名：仅 committed + snapshot，cursor 始终 0）。</summary>
        /// <param name="getLastSnapshotId">返回上次 snapshot ID 的委托。</param>
        /// <param name="getCommittedResults">返回已提交 tool 结果字典的委托。</param>
        public KernelStateAccessor(
            Func<string?> getLastSnapshotId,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>> getCommittedResults)
            : this(getLastSnapshotId, getCommittedResults, null, null, null, null)
        {
        }

        /// <summary>构造 Kernel 状态访问器（完整签名：含 delta cursor + pending results）。</summary>
        /// <param name="getLastSnapshotId">返回上次 snapshot ID 的委托。</param>
        /// <param name="getCommittedResults">返回已提交 tool 结果字典的委托。</param>
        /// <param name="getCommittedResultSequences">返回 committed result 序号字典的委托（null 时退回 Full 模式）。</param>
        /// <param name="getPendingResults">返回 pending（Unknown 副作用）结果字典的委托（null 时退回空集）。</param>
        /// <param name="getLastCheckpointSequence">返回上次 checkpoint LastSequence 的委托（null 时退回 0 = Full 模式）。</param>
        /// <param name="getLastCheckpointId">返回上次 checkpoint ID 的委托（null 时退回 null）。</param>
        public KernelStateAccessor(
            Func<string?> getLastSnapshotId,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>> getCommittedResults,
            Func<IReadOnlyDictionary<string, long>>? getCommittedResultSequences,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>>? getPendingResults,
            Func<long>? getLastCheckpointSequence,
            Func<string?>? getLastCheckpointId)
        {
            _getLastSnapshotId = getLastSnapshotId ?? throw new ArgumentNullException(nameof(getLastSnapshotId));
            _getCommittedResults = getCommittedResults ?? throw new ArgumentNullException(nameof(getCommittedResults));
            _getCommittedResultSequences = getCommittedResultSequences;
            _getPendingResults = getPendingResults;
            _getLastCheckpointSequence = getLastCheckpointSequence;
            _getLastCheckpointId = getLastCheckpointId;
        }

        /// <summary>获取上次 snapshot ID。</summary>
        public string? GetLastSnapshotId() => _getLastSnapshotId();

        /// <summary>获取已提交 tool 结果字典。</summary>
        public IReadOnlyDictionary<string, ToolDispatchResult> GetCommittedResults() => _getCommittedResults();

        /// <summary>获取 committed result 序号字典（用于 delta 过滤）。</summary>
        public IReadOnlyDictionary<string, long> GetCommittedResultSequences()
            => _getCommittedResultSequences?.Invoke() ?? ReadOnlyDict<long>();

        /// <summary>获取 pending（Unknown 副作用）结果字典。</summary>
        public IReadOnlyDictionary<string, ToolDispatchResult> GetPendingResults()
            => _getPendingResults?.Invoke() ?? ReadOnlyDict<ToolDispatchResult>();

        /// <summary>获取上次 checkpoint 的 LastSequence（0 = 从未 checkpoint → Full 模式）。</summary>
        public long GetLastCheckpointSequence() => _getLastCheckpointSequence?.Invoke() ?? 0;

        /// <summary>获取上次 checkpoint 的 ID（用于 delta 链 BaseCheckpointId）。</summary>
        public string? GetLastCheckpointId() => _getLastCheckpointId?.Invoke();

        private static IReadOnlyDictionary<string, T> ReadOnlyDict<T>() => new Dictionary<string, T>(0, StringComparer.Ordinal);
    }

    /// <summary>R28-G P1-5：Checkpoint 模式（Full 完整快照 / Delta 增量）。</summary>
    public enum CheckpointMode
    {
        /// <summary>完整快照：序列化所有 in-memory committed results。</summary>
        Full = 0,

        /// <summary>增量：仅序列化 Sequence > 上次 checkpoint LastSequence 的新增条目。</summary>
        Delta = 1
    }

    /// <summary>R28-E P1-1 / R28-G P1-5：Checkpoint 序列化模型（公开以供 ResumeAsync 反序列化）。</summary>
    public sealed class KernelCheckpointStateDto
    {
        /// <summary>Snapshot ID（可空）。</summary>
        public string? SnapshotId { get; init; }

        /// <summary>R28-G P1-5：Checkpoint 模式（Full=完整快照，Delta=增量）。</summary>
        /// <remarks>默认 Full 保持向后兼容（旧 checkpoint 反序列化时 Mode 字段缺失 → 取默认值 Full）。</remarks>
        public CheckpointMode Mode { get; init; } = CheckpointMode.Full;

        /// <summary>R28-G P1-5：Delta 模式下的 BaseCheckpoint ID（用于 ResumeAsync 递归加载基线）。</summary>
        public string? BaseCheckpointId { get; init; }

        /// <summary>R28-G P1-5：本次 checkpoint 覆盖的最大 Sequence（用于推进下次 delta cursor）。</summary>
        public long LastSequence { get; init; }

        /// <summary>已提交 tool 结果列表（Full=全部，Delta=仅新增）。</summary>
        public List<CommittedToolResultDto> CommittedResults { get; init; } = new();

        /// <summary>R28-G P1-5：pending（Unknown 副作用）tool 结果列表。</summary>
        public List<CommittedToolResultDto> PendingResults { get; init; } = new();
    }

    /// <summary>R28-E P1-1 / R28-G P1-5：已提交 tool 结果序列化条目。</summary>
    public sealed class CommittedToolResultDto
    {
        /// <summary>Tool RequestId。</summary>
        public string RequestId { get; init; } = "";

        /// <summary>是否成功。</summary>
        public bool Succeeded { get; init; }

        /// <summary>Tool 输出。</summary>
        public string? Result { get; init; }

        /// <summary>错误信息。</summary>
        public string? Error { get; init; }

        /// <summary>副作用分类。</summary>
        public ToolSideEffect SideEffect { get; init; }

        /// <summary>R28-G P1-5：committed result 序号（用于 delta 过滤；pending 结果为 0）。</summary>
        public long Sequence { get; init; }
    }
}
