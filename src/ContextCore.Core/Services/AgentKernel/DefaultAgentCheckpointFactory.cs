using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-E P1-1 / R28-G P1-5 / P4：DefaultAgentCheckpointFactory
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
// P4 cursor checkpoint 设计（最轻量，优先级最高）：
//   - 优势：避免每次 checkpoint 复制完整结果集合；历史 Tool 结果放在
//     IAgentRunEventStore（事件流为真相源），checkpoint 仅记录事件 cursor
//     （LastEventSequence）+ ActiveSnapshotId + BudgetCounters + PendingResults。
//   - 前提：IAgentRunEventStore 已注入且可靠（事件流持久化到 DB/WAL）。
//   - 触发条件：accessor 的 GetLastEventSequence() 返回非 null（即 Kernel 已
//     在 AutoCheckpointAsync 中读取过 EventStore 的最新 sequence）。
//   - CommittedResults 设为空列表（ResumeAsync 时从 EventStore 重建：
//     读取 sequence <= LastEventSequence 的 ToolCallCompleted 事件）。
//   - PendingResults 仍需保留（Unknown 副作用状态不能从 EventStore 重建）。
//
// 三种模式优先级：Cursor > Delta > Full（Cursor 最轻量）。
//   - Cursor：事件流为真相源，不序列化 CommittedResults（最轻量）。
//   - Delta：仅序列化新增 CommittedResults（中等）。
//   - Full：序列化所有 CommittedResults（最重，向后兼容）。
//
// 设计决策：
//   - 工厂持有 Kernel 的可变状态引用（_committedToolResults + _lastSnapshot
//     + _committedResultSequences + _pendingToolResults + _lastCheckpointSequence
//     + _lastCheckpointId + _lastEventSequenceCache），通过 KernelStateAccessor 委托读取。
//   - 序列化格式向后兼容：新增字段（Mode/BaseCheckpointId/LastSequence/
//     PendingResults/Sequence/LastEventSequence/ActiveSnapshotId/BudgetCounters）
//     默认值与旧 checkpoint 兼容（Mode 默认 Full）。
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
        var lastCheckpointContentHash = _stateAccessor.GetLastCheckpointContentHash();

        // P4：读取 EventStore cursor + 活跃 snapshot + 预算计数器（Cursor 模式专用字段）
        var lastEventSequence = _stateAccessor.GetLastEventSequence();
        var activeSnapshotId = _stateAccessor.GetActiveSnapshotId();
        var budgetCounters = _stateAccessor.GetBudgetCounters();

        // P4：模式选择——Cursor > Delta > Full（Cursor 最轻量）
        //   - Cursor：EventStore 已注入（lastEventSequence != null），不序列化 CommittedResults
        //   - Delta：上次 checkpoint cursor > 0，仅序列化新增 CommittedResults
        //   - Full：首次 checkpoint 或 cursor == 0，序列化全部 CommittedResults
        var isCursor = lastEventSequence.HasValue;
        var isDelta = !isCursor && lastCheckpointSequence > 0;
        var mode = isCursor ? CheckpointMode.Cursor
                  : isDelta ? CheckpointMode.Delta
                  : CheckpointMode.Full;

        // 计算本次 checkpoint 的 LastSequence（= 当前最大 Sequence，若无则保持 cursor）
        // P4：Cursor 模式下 LastSequence 仅用于 cursor 推进语义（与 EventStore 序号解耦）
        var currentMaxSequence = sequences.Count > 0 ? sequences.Values.Max() : lastCheckpointSequence;

        // 过滤要序列化的 committed results
        // - Cursor：空列表（ResumeAsync 时从 EventStore 重建）
        // - Full：全部
        // - Delta：仅 Sequence > cursor 的新增条目
        List<CommittedToolResultDto> committedDtos;
        if (isCursor)
        {
            // P4：Cursor 模式不序列化 CommittedResults——假设可从 AgentRunEventStore 重建
            // （读取 sequence <= LastEventSequence 的 ToolCallCompleted 事件）
            committedDtos = new List<CommittedToolResultDto>();
        }
        else if (isDelta)
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
        // P4：Cursor 模式下 PendingResults 仍需保留——Unknown 副作用状态不能从 EventStore 重建
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

        // P0-5：构建 hash chain 字段
        // - BaseLastSequence：Delta 模式下记录当前 cursor（apply 时校验 base.LastSequence == BaseLastSequence）
        // - PrevChainHash：Delta 模式下指向 base 的 ContentHash（检测 base 被篡改/替换）
        // - ChainSessionId：本次 checkpoint 所属 session（校验 base 与 delta 同 session）
        // - ContentHash：自身 StateJson（不含 ContentHash 字段）的 SHA-256（检测存储层篡改）
        // P4：Cursor 模式下 BaseCheckpointId / PrevChainHash / BaseLastSequence 均为 null/0
        // （Cursor 模式恢复时无需递归加载 base checkpoint 链——事件流是完整真相源）
        var baseLastSequence = isDelta ? lastCheckpointSequence : 0;
        var prevChainHash = isDelta ? lastCheckpointContentHash : null;
        var baseCheckpointId = isDelta ? lastCheckpointId : null;

        // 先构建不含 ContentHash 的 DTO，计算 SHA-256 后再填入 ContentHash。
        // 校验方：反序列化 → 将 ContentHash 置 null → 重新序列化 → 重算哈希 → 与存储的 ContentHash 比对。
        // P4：新字段（LastEventSequence / ActiveSnapshotId / BudgetCounters）纳入哈希计算，
        // 确保任何字段被篡改均能被检测。
        var stateForHash = new KernelCheckpointStateDto
        {
            SnapshotId = snapshotId,
            Mode = mode,
            BaseCheckpointId = baseCheckpointId,
            LastSequence = currentMaxSequence,
            CommittedResults = committedDtos,
            PendingResults = pendingDtos,
            BaseLastSequence = baseLastSequence,
            PrevChainHash = prevChainHash,
            ChainSessionId = sessionId,
            LastEventSequence = lastEventSequence,
            ActiveSnapshotId = activeSnapshotId,
            BudgetCounters = budgetCounters,
            ContentHash = null // 显式 null：哈希计算排除此字段
        };

        var jsonForHash = JsonSerializer.Serialize(stateForHash);
        var contentHash = ComputeContentHash(jsonForHash);

        var state = new KernelCheckpointStateDto
        {
            SnapshotId = snapshotId,
            Mode = mode,
            BaseCheckpointId = baseCheckpointId,
            LastSequence = currentMaxSequence,
            CommittedResults = committedDtos,
            PendingResults = pendingDtos,
            BaseLastSequence = baseLastSequence,
            PrevChainHash = prevChainHash,
            ChainSessionId = sessionId,
            LastEventSequence = lastEventSequence,
            ActiveSnapshotId = activeSnapshotId,
            BudgetCounters = budgetCounters,
            ContentHash = contentHash
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

    /// <summary>
    /// P0-5：计算 StateJson 的 SHA-256 内容哈希（小写 hex）。
    /// 用于 ContentHash / PrevChainHash 校验，检测存储层篡改。
    /// </summary>
    /// <param name="json">不含 ContentHash 字段的 StateJson。</param>
    /// <returns>小写 hex 编码的 SHA-256 哈希（64 字符）。</returns>
    internal static string ComputeContentHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// P0-5：校验 checkpoint StateJson 的 ContentHash 一致性。
    /// 反序列化 → 将 ContentHash 置 null → 重新序列化 → 重算哈希 → 与存储的 ContentHash 比对。
    /// 旧 checkpoint（无 ContentHash）跳过校验（向后兼容）。
    /// </summary>
    /// <param name="stateJson">checkpoint 的 StateJson。</param>
    /// <returns>校验通过返回 true；旧 checkpoint 无 ContentHash 返回 true（跳过）；不匹配返回 false。</returns>
    internal static bool VerifyContentHash(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return true;
        }

        KernelCheckpointStateDto state;
        try
        {
            state = JsonSerializer.Deserialize<KernelCheckpointStateDto>(stateJson)!;
            if (state is null)
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        // 旧 checkpoint 无 ContentHash → 跳过校验（向后兼容）
        if (string.IsNullOrEmpty(state.ContentHash))
        {
            return true;
        }

        // 重建不含 ContentHash 的 DTO 重新计算哈希
        // P4：新字段（LastEventSequence / ActiveSnapshotId / BudgetCounters）纳入哈希计算
        var stateForHash = new KernelCheckpointStateDto
        {
            SnapshotId = state.SnapshotId,
            Mode = state.Mode,
            BaseCheckpointId = state.BaseCheckpointId,
            LastSequence = state.LastSequence,
            CommittedResults = state.CommittedResults,
            PendingResults = state.PendingResults,
            BaseLastSequence = state.BaseLastSequence,
            PrevChainHash = state.PrevChainHash,
            ChainSessionId = state.ChainSessionId,
            LastEventSequence = state.LastEventSequence,
            ActiveSnapshotId = state.ActiveSnapshotId,
            BudgetCounters = state.BudgetCounters,
            ContentHash = null
        };

        var jsonForHash = JsonSerializer.Serialize(stateForHash);
        var computedHash = ComputeContentHash(jsonForHash);
        return string.Equals(computedHash, state.ContentHash, StringComparison.Ordinal);
    }

    /// <summary>R28-E P1-1 / R28-G P1-5 / P4：Kernel 状态访问器（读取已提交结果 + snapshot 引用 + delta cursor + event cursor）。</summary>
    /// <remarks>
    /// 通过委托避免直接暴露 Kernel 内部字段；工厂构造时由 Kernel 注入。
    /// 新增（P1-5）的访问器委托可为 null（兼容旧调用方）；null 时退回默认值（cursor=0 → Full 模式）。
    /// P4 新增委托：GetLastEventSequence / GetActiveSnapshotId / GetBudgetCounters。
    /// </remarks>
    public sealed class KernelStateAccessor
    {
        private readonly Func<string?> _getLastSnapshotId;
        private readonly Func<IReadOnlyDictionary<string, ToolDispatchResult>> _getCommittedResults;
        private readonly Func<IReadOnlyDictionary<string, long>>? _getCommittedResultSequences;
        private readonly Func<IReadOnlyDictionary<string, ToolDispatchResult>>? _getPendingResults;
        private readonly Func<long>? _getLastCheckpointSequence;
        private readonly Func<string?>? _getLastCheckpointId;
        private readonly Func<string?>? _getLastCheckpointContentHash;
        private readonly Func<int?>? _getLastEventSequence;
        private readonly Func<string?>? _getActiveSnapshotId;
        private readonly Func<BudgetCountersDto?>? _getBudgetCounters;

        /// <summary>构造 Kernel 状态访问器（兼容旧签名：仅 committed + snapshot，cursor 始终 0）。</summary>
        /// <param name="getLastSnapshotId">返回上次 snapshot ID 的委托。</param>
        /// <param name="getCommittedResults">返回已提交 tool 结果字典的委托。</param>
        public KernelStateAccessor(
            Func<string?> getLastSnapshotId,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>> getCommittedResults)
            : this(getLastSnapshotId, getCommittedResults, null, null, null, null, null, null, null, null)
        {
        }

        /// <summary>构造 Kernel 状态访问器（完整签名：含 delta cursor + pending results + P0-5 hash chain + P4 event cursor）。</summary>
        /// <param name="getLastSnapshotId">返回上次 snapshot ID 的委托。</param>
        /// <param name="getCommittedResults">返回已提交 tool 结果字典的委托。</param>
        /// <param name="getCommittedResultSequences">返回 committed result 序号字典的委托（null 时退回 Full 模式）。</param>
        /// <param name="getPendingResults">返回 pending（Unknown 副作用）结果字典的委托（null 时退回空集）。</param>
        /// <param name="getLastCheckpointSequence">返回上次 checkpoint LastSequence 的委托（null 时退回 0 = Full 模式）。</param>
        /// <param name="getLastCheckpointId">返回上次 checkpoint ID 的委托（null 时退回 null）。</param>
        /// <param name="getLastCheckpointContentHash">P0-5：返回上次 checkpoint ContentHash 的委托（null 时退回 null = 无前驱哈希）。</param>
        /// <param name="getLastEventSequence">P4：返回 AgentRunEventStore 最后事件序列号的委托（null 时退回 null = 非 Cursor 模式）。</param>
        /// <param name="getActiveSnapshotId">P4：返回当前活跃 AgentContextSnapshot ID 的委托（null 时退回 null）。</param>
        /// <param name="getBudgetCounters">P4：返回 turn/cost 预算计数器的委托（null 时退回 null）。</param>
        public KernelStateAccessor(
            Func<string?> getLastSnapshotId,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>> getCommittedResults,
            Func<IReadOnlyDictionary<string, long>>? getCommittedResultSequences,
            Func<IReadOnlyDictionary<string, ToolDispatchResult>>? getPendingResults,
            Func<long>? getLastCheckpointSequence,
            Func<string?>? getLastCheckpointId,
            Func<string?>? getLastCheckpointContentHash = null,
            Func<int?>? getLastEventSequence = null,
            Func<string?>? getActiveSnapshotId = null,
            Func<BudgetCountersDto?>? getBudgetCounters = null)
        {
            _getLastSnapshotId = getLastSnapshotId ?? throw new ArgumentNullException(nameof(getLastSnapshotId));
            _getCommittedResults = getCommittedResults ?? throw new ArgumentNullException(nameof(getCommittedResults));
            _getCommittedResultSequences = getCommittedResultSequences;
            _getPendingResults = getPendingResults;
            _getLastCheckpointSequence = getLastCheckpointSequence;
            _getLastCheckpointId = getLastCheckpointId;
            _getLastCheckpointContentHash = getLastCheckpointContentHash;
            _getLastEventSequence = getLastEventSequence;
            _getActiveSnapshotId = getActiveSnapshotId;
            _getBudgetCounters = getBudgetCounters;
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

        /// <summary>P0-5：获取上次 checkpoint 的 ContentHash（用于 delta 链 PrevChainHash）。</summary>
        public string? GetLastCheckpointContentHash() => _getLastCheckpointContentHash?.Invoke();

        /// <summary>P4：获取 AgentRunEventStore 的最后事件序列号（Cursor 模式核心）。</summary>
        /// <returns>最后事件序列号；未注入 EventStore 时返回 null（非 Cursor 模式）。</returns>
        public int? GetLastEventSequence() => _getLastEventSequence?.Invoke();

        /// <summary>P4：获取当前活跃的 AgentContextSnapshot ID（替代嵌入完整 snapshot）。</summary>
        /// <returns>当前活跃 snapshot ID；Kernel 无活跃 snapshot 时返回 null。</returns>
        public string? GetActiveSnapshotId() => _getActiveSnapshotId?.Invoke();

        /// <summary>P4：获取 turn/cost 预算计数器（替代嵌入完整结果集合用于预算追踪）。</summary>
        /// <returns>当前预算计数器快照；Kernel 不维护预算计数器时返回 null。</returns>
        public BudgetCountersDto? GetBudgetCounters() => _getBudgetCounters?.Invoke();

        private static IReadOnlyDictionary<string, T> ReadOnlyDict<T>() => new Dictionary<string, T>(0, StringComparer.Ordinal);
    }

    /// <summary>R28-G P1-5 / P4：Checkpoint 模式（Full 完整快照 / Delta 增量 / Cursor 事件游标）。</summary>
    public enum CheckpointMode
    {
        /// <summary>完整快照：序列化所有 in-memory committed results。</summary>
        Full = 0,

        /// <summary>增量：仅序列化 Sequence > 上次 checkpoint LastSequence 的新增条目。</summary>
        Delta = 1,

        /// <summary>
        /// P4：事件游标——不序列化 CommittedResults，仅记录 AgentRunEventStore 的 LastEventSequence。
        /// ResumeAsync 时从 EventStore 读取 sequence <= LastEventSequence 的 ToolCallCompleted 事件重建。
        /// 前提：IAgentRunEventStore 已注入且可靠。
        /// </summary>
        Cursor = 2
    }

    /// <summary>R28-E P1-1 / R28-G P1-5 / P0-5 / P4：Checkpoint 序列化模型（公开以供 ResumeAsync 反序列化）。</summary>
    public sealed class KernelCheckpointStateDto
    {
        /// <summary>Snapshot ID（可空）。</summary>
        public string? SnapshotId { get; init; }

        /// <summary>R28-G P1-5 / P4：Checkpoint 模式（Full=完整快照，Delta=增量，Cursor=事件游标）。</summary>
        /// <remarks>默认 Full 保持向后兼容（旧 checkpoint 反序列化时 Mode 字段缺失 → 取默认值 Full）。</remarks>
        public CheckpointMode Mode { get; init; } = CheckpointMode.Full;

        /// <summary>R28-G P1-5：Delta 模式下的 BaseCheckpoint ID（用于 ResumeAsync 递归加载基线）。</summary>
        public string? BaseCheckpointId { get; init; }

        /// <summary>R28-G P1-5：本次 checkpoint 覆盖的最大 Sequence（用于推进下次 delta cursor）。</summary>
        public long LastSequence { get; init; }

        /// <summary>已提交 tool 结果列表（Full=全部，Delta=仅新增，Cursor=空）。</summary>
        public List<CommittedToolResultDto> CommittedResults { get; init; } = new();

        /// <summary>R28-G P1-5：pending（Unknown 副作用）tool 结果列表。</summary>
        public List<CommittedToolResultDto> PendingResults { get; init; } = new();

        /// <summary>
        /// P0-5：Delta 模式下记录 base checkpoint 的 LastSequence，用于 apply 时校验
        /// <c>base.LastSequence &lt; delta.min(Sequence) &lt;= delta.LastSequence</c>。
        /// Full 模式下为 0（不校验）。
        /// </summary>
        public long BaseLastSequence { get; init; }

        /// <summary>
        /// P0-5：前驱 checkpoint 的内容哈希（StateJson 的 SHA-256）。
        /// Full 模式下为 null（无前驱）；Delta 模式下指向 base 的 ContentHash，用于检测 base 被篡改。
        /// </summary>
        public string? PrevChainHash { get; init; }

        /// <summary>
        /// P0-5：自身 StateJson（不含 ContentHash 字段本身）的 SHA-256，用于校验序列化完整性。
        /// ResumeAsync apply 前校验 ContentHash 与重新计算的哈希一致，检测存储层篡改。
        /// 旧 checkpoint（P0-5 之前）无此字段 → null，ResumeAsync 跳过校验（向后兼容）。
        /// </summary>
        public string? ContentHash { get; init; }

        /// <summary>
        /// P0-5：链所属 session 标识，用于校验 base 与 delta 属于同一 session（防跨 session 链接）。
        /// 旧 checkpoint 无此字段 → null，ResumeAsync 跳过校验。
        /// </summary>
        public string? ChainSessionId { get; init; }

        /// <summary>
        /// P4：AgentRunEventStore 的最后事件序列号（Cursor 模式的核心字段）。
        /// Cursor 模式下 ResumeAsync 从 EventStore 读取 sequence &lt;= LastEventSequence 的
        /// ToolCallCompleted 事件重建 CommittedResults（事件流为完整真相源）。
        /// 非 Cursor 模式（Full/Delta）下为 null（不使用事件流重建）。
        /// 旧 checkpoint 无此字段 → null（向后兼容）。
        /// </summary>
        public int? LastEventSequence { get; init; }

        /// <summary>
        /// P4：当前活跃的 AgentContextSnapshot ID（替代嵌入完整 snapshot）。
        /// Cursor 模式下 ResumeAsync 通过 IAgentContextSnapshotStore.GetAsync 恢复 _lastSnapshot。
        /// 非 Cursor 模式下为 null（使用 SnapshotId 字段，向后兼容）。
        /// </summary>
        public string? ActiveSnapshotId { get; init; }

        /// <summary>
        /// P4：turn budget + cost budget 的当前计数快照（替代嵌入完整结果集合用于预算追踪）。
        /// Cursor 模式下 ResumeAsync 据此恢复 Kernel 的预算计数器。
        /// 非 Cursor 模式下为 null（Kernel 不维护预算计数器时也返回 null）。
        /// </summary>
        public BudgetCountersDto? BudgetCounters { get; init; }
    }

    /// <summary>
    /// P4：预算计数器快照（turn budget + cost budget 的当前计数）。
    /// 用于 Cursor 模式 checkpoint 持久化预算状态，避免嵌入完整结果集合。
    /// </summary>
    /// <param name="TurnsUsed">已使用的循环轮次数。</param>
    /// <param name="TokensUsed">已消耗的 token 数。</param>
    /// <param name="CostUsedUsd">已产生的推理费用（美元）。</param>
    public sealed record BudgetCountersDto(int TurnsUsed, int TokensUsed, double CostUsedUsd);

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
