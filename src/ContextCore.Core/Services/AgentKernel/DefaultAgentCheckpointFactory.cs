using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-E P1-1：DefaultAgentCheckpointFactory
//
// 统一手动 Checkpoint 指令与自动 AutoCheckpoint 的状态格式。
// 两者均通过此工厂构建 AgentCheckpoint，序列化 KernelCheckpointState
// （CommittedResults + SnapshotId）到 StateJson，确保 ResumeAsync 可靠恢复。
//
// 设计决策：
//   - 工厂持有 Kernel 的可变状态引用（_committedToolResults + _lastSnapshot），
//     通过 KernelStateAccessor 委托读取，避免暴露 Kernel 内部字段。
//   - 序列化格式与旧版 AutoCheckpoint 保持一致（KernelCheckpointState JSON），
//     旧 checkpoint 可继续被 ResumeAsync 反序列化。
//   - 工厂为 sealed class，构造时注入 KernelStateAccessor。
// ===========================================================================

/// <summary>
/// R28-E P1-1：默认 Agent Checkpoint 工厂实现。
/// 统一所有 checkpoint 入口（手动/自动）的状态格式。
/// </summary>
public sealed class DefaultAgentCheckpointFactory : IAgentCheckpointFactory
{
    private readonly KernelStateAccessor _stateAccessor;

    /// <summary>构造默认 checkpoint 工厂。</summary>
    /// <param name="stateAccessor">Kernel 状态访问器（读取已提交结果 + snapshot 引用）。</param>
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

        // 序列化 KernelCheckpointState（CommittedResults + SnapshotId）
        var state = new KernelCheckpointStateDto
        {
            SnapshotId = snapshotId,
            CommittedResults = committedResults.Select(kv => new CommittedToolResultDto
            {
                RequestId = kv.Key,
                Succeeded = kv.Value.Succeeded,
                Result = kv.Value.Result,
                Error = kv.Value.Error,
                SideEffect = kv.Value.SideEffect
            }).ToList()
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

    /// <summary>R28-E P1-1：Kernel 状态访问器（读取已提交结果 + snapshot 引用）。</summary>
    /// <remarks>
    /// 通过委托避免直接暴露 Kernel 内部字段；工厂构造时由 Kernel 注入。
    /// </remarks>
    public sealed class KernelStateAccessor
    {
        private readonly Func<string?> _getLastSnapshotId;
        private readonly Func<IReadOnlyDictionary<string, ToolDispatchResult>> _getCommittedResults;

        /// <summary>构造 Kernel 状态访问器。</summary>
        /// <param name="getLastSnapshotId">返回上次 snapshot ID 的委托。</param>
        /// <param name="getCommittedResults">返回已提交 tool 结果字典的委托。</param>
        public KernelStateAccessor(
            Func<string?> getLastSnapshotId,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>> getCommittedResults)
        {
            _getLastSnapshotId = getLastSnapshotId ?? throw new ArgumentNullException(nameof(getLastSnapshotId));
            _getCommittedResults = getCommittedResults ?? throw new ArgumentNullException(nameof(getCommittedResults));
        }

        /// <summary>获取上次 snapshot ID。</summary>
        public string? GetLastSnapshotId() => _getLastSnapshotId();

        /// <summary>获取已提交 tool 结果字典。</summary>
        public IReadOnlyDictionary<string, ToolDispatchResult> GetCommittedResults() => _getCommittedResults();
    }

    /// <summary>R28-E P1-1：Checkpoint 序列化模型（公开以供 ResumeAsync 反序列化）。</summary>
    public sealed class KernelCheckpointStateDto
    {
        /// <summary>Snapshot ID（可空）。</summary>
        public string? SnapshotId { get; init; }

        /// <summary>已提交 tool 结果列表。</summary>
        public List<CommittedToolResultDto> CommittedResults { get; init; } = new();
    }

    /// <summary>R28-E P1-1：已提交 tool 结果序列化条目。</summary>
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
    }
}
