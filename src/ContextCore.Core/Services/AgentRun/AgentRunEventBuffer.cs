using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// Agent Run 执行期的事件缓冲、本地状态推进与批量提交协作者。
/// </summary>
/// <remarks>
/// 持有 Turn 内待提交事件、Turn 起始状态快照与最新 checkpoint；本地状态推进只改内存副本，
/// 持久化延后到 Turn 结束时以单事务批量提交（事件流 + 状态 CAS + checkpoint 游标），
/// mid-turn 事件数超过阈值时由 Actor 调用强制 flush。
/// </remarks>
internal sealed class AgentRunEventBuffer
{
    /// <summary>批量提交阈值（超过则 mid-turn 强制 flush）。</summary>
    public const int PendingEventsFlushThreshold = 32;

    /// <summary>强制 checkpoint 阈值 — 未 checkpoint 事件数达到此值时强制创建 checkpoint。</summary>
    public const int ForcedCheckpointEventThreshold = 1000;

    private readonly IAgentRunEventStore _eventStore;
    private readonly IPersistentAgentRunCommitter? _committer;
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    private readonly IAgentCheckpointStore? _checkpointStore;

    // Turn 内事件批量缓冲（替代每次单独 AppendAsync）
    private readonly List<AgentRunEvent> _pendingTurnEvents = new();
    // Turn 起始状态快照（用于批量提交时的 state CAS）
    private AgentRunState _turnStartState;
    // Turn 内最新 checkpoint（用于批量提交时的 checkpoint cursor）
    private AgentCheckpoint? _pendingTurnCheckpoint;
    // 自上次 checkpoint 以来已 flush 的事件数（用于强制 checkpoint 阈值判断）。
    private int _eventsSinceLastCheckpoint;
    // 当前执行期的 lease token 与 fencing token（Actor 在 ExecuteAsync 开始时注入；
    // 批量提交时由 Postgres 实现在状态 CAS 与事件追加的 WHERE 子句中校验 lease 仍由当前实例持有）。
    private string? _leaseToken;
    private long? _fencingToken;

    /// <summary>
    /// 构造事件缓冲协作者。
    /// </summary>
    /// <param name="eventStore">Run 事件流存储（哈希链）。</param>
    /// <param name="committer">统一提交入口（可选）；null 时回退 <paramref name="eventStore"/> 的批量追加。</param>
    /// <param name="checkpointFactory">检查点工厂（null 时跳过 checkpoint）。</param>
    /// <param name="checkpointStore">Checkpoint Store（null 时仅缓冲、不持久化）。</param>
    public AgentRunEventBuffer(
        IAgentRunEventStore eventStore,
        IPersistentAgentRunCommitter? committer,
        IAgentCheckpointFactory? checkpointFactory,
        IAgentCheckpointStore? checkpointStore)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _committer = committer;
        _checkpointFactory = checkpointFactory;
        _checkpointStore = checkpointStore;
    }

    /// <summary>当前缓冲的事件数（mid-turn 强制 flush 阈值判断用）。</summary>
    public int PendingEventCount => _pendingTurnEvents.Count;

    /// <summary>
    /// 自上次 checkpoint 以来已 flush 的事件数（强制 checkpoint 阈值判断用）。
    /// 恢复流程在重建执行状态时回填该计数。
    /// </summary>
    public int EventsSinceLastCheckpoint
    {
        get => _eventsSinceLastCheckpoint;
        set => _eventsSinceLastCheckpoint = value;
    }

    /// <summary>
    /// Turn 起始状态快照（批量提交时的 state CAS expected 值）。
    /// 恢复失败状态采用状态直写后由 Actor 同步更新。
    /// </summary>
    public AgentRunState TurnStartState
    {
        get => _turnStartState;
        set => _turnStartState = value;
    }

    /// <summary>
    /// 每次执行开始时的初始化：清空缓冲、记录 Turn 起始状态并注入 lease token。
    /// </summary>
    /// <param name="turnStartState">Turn 起始状态（resume 时 = store 中的当前状态）。</param>
    /// <param name="leaseToken">租约 token（null = 无 lease 路径）。</param>
    /// <param name="fencingToken">fencing token（与 lease token 同时为 null 或同时非 null）。</param>
    public void BeginExecution(AgentRunState turnStartState, string? leaseToken, long? fencingToken)
    {
        _pendingTurnEvents.Clear();
        _turnStartState = turnStartState;
        _pendingTurnCheckpoint = null;
        _eventsSinceLastCheckpoint = 0;
        _leaseToken = leaseToken;
        _fencingToken = fencingToken;
    }

    /// <summary>
    /// 本地推进 Run 状态（不直接写 DB），同时缓冲 StateTransition 事件。
    /// CAS 与字段更新延后到 Turn 结束时的 <see cref="FlushPendingEventsAsync"/> 批量提交。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="newState">目标状态。</param>
    /// <param name="bufferEvent">是否缓冲 StateTransition 事件（恢复失败路径为 false）。</param>
    /// <returns>更新后的执行状态（Run.State = newState）。</returns>
    public AgentRunExecutionState TransitionStateLocal(AgentRunExecutionState state, AgentRunState newState, bool bufferEvent = true)
    {
        // 校验状态机
        AgentRunStateMachine.ValidateTransition(state.Run.State, newState);

        var updatedRun = state.Run with
        {
            State = newState,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var newStateObj = state with { Run = updatedRun };

        // 缓冲 StateTransition 事件。恢复失败路径（bufferEvent false）不缓冲：
        // 事件流可能已损坏或不可用，且恢复失败状态采用状态直写（见 EnterRecoveryFailureStateAsync）。
        return bufferEvent
            ? BufferEvent(newStateObj, AgentRunEventType.StateTransition, JsonSerializer.Serialize(new
            {
                from = state.Run.State.ToString(),
                to = newState.ToString()
            }))
            : newStateObj;
    }

    /// <summary>
    /// 缓冲事件到 <see cref="_pendingTurnEvents"/>（不直接写 DB），
    /// 延后到 Turn 结束时由 <see cref="FlushPendingEventsAsync"/> 批量提交。
    /// </summary>
    public AgentRunExecutionState BufferEvent(AgentRunExecutionState state, AgentRunEventType type, string payload)
    {
        var @event = AgentRunEventChain.BuildEvent(
            state.Run.RunId,
            state.Run.WorkspaceId,
            state.EventSequence,
            type,
            state.Run.State,
            payload,
            state.EventChainHash);

        _pendingTurnEvents.Add(@event);

        return state with
        {
            EventSequence = state.EventSequence + 1,
            EventChainHash = @event.ContentHash
        };
    }

    /// <summary>
    /// 从缓冲事件重新同步 <paramref name="state"/> 的
    /// <see cref="AgentRunExecutionState.EventSequence"/> / <see cref="AgentRunExecutionState.EventChainHash"/>
    /// 以及 <see cref="AgentRun.State"/>。
    /// </summary>
    /// <remarks>
    /// 当 Actor 的阶段方法（调用模型 / 分派工具等）抛异常时，<c>state = await MethodAsync(state, ...)</c>
    /// 的赋值未完成，catch 块中的 <paramref name="state"/> 是阶段方法调用前的陈旧副本。但缓冲事件已被
    /// 阶段方法修改（追加了事件）。若不重新同步，Actor 的 FailAsync / TryTransitionToCancelledAsync
    /// 会用陈旧的 <see cref="AgentRunExecutionState.EventSequence"/> 生成重复 Sequence 的事件，导致
    /// <see cref="IAgentRunEventStore.AppendBatchAsync"/> 校验失败（Sequence 不连续）。
    /// </remarks>
    public AgentRunExecutionState ResyncStateFromPendingEvents(AgentRunExecutionState state)
    {
        if (_pendingTurnEvents.Count == 0)
        {
            // 无缓冲事件 → state 未被阶段方法修改（EventSequence / EventChainHash 已正确），
            // 不能重置为 0（之前 Turn 的事件可能已 flush 持久化，新事件必须从当前 EventSequence 继续）。
            return state;
        }

        var lastEvent = _pendingTurnEvents[^1];
        return state with
        {
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash,
            Run = state.Run with { State = lastEvent.State }
        };
    }

    /// <summary>执行 Checkpoint 阶段。</summary>
    /// <remarks>
    /// Create checkpoint → 缓冲 CheckpointSaved event → 本地推进到 ContextBuilding。
    /// 顺序必须是保存成功后才记录事件（不能先记录成功事件再保存）。
    /// Bug 4 修复：SaveAsync 失败时显式捕获异常，转 Failed 状态，不记录 CheckpointSaved 事件。
    /// 状态推进与事件均缓冲到 <see cref="_pendingTurnEvents"/>，CAS 延后到 Turn 结束批量提交。
    /// </remarks>
    /// <param name="state">当前执行状态。</param>
    /// <param name="currentTurn">当前 Turn（写入 checkpoint id）。</param>
    /// <param name="executionModelTurn">当前执行期模型轮次（写入 checkpoint metadata，支持 RequestId 稳定性恢复）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<AgentRunExecutionState> PersistCheckpointAsync(
        AgentRunExecutionState state,
        int currentTurn,
        int executionModelTurn,
        CancellationToken cancellationToken)
    {
        // 进入 Checkpointing（本地推进 + 缓冲 StateTransition 事件）
        state = TransitionStateLocal(state, AgentRunState.Checkpointing);

        if (_checkpointFactory is not null)
        {
            var checkpointId = $"run-{state.Run.RunId}-turn-{currentTurn}-{Guid.NewGuid():N}";
            var checkpoint = await _checkpointFactory.CreateCheckpointAsync(
                checkpointId, state.Run.SessionId, state.Run.WorkspaceId, cancellationToken).ConfigureAwait(false);

            // 将可恢复的执行状态写入 checkpoint metadata，支持恢复时从 Checkpoint Cursor
            // 直接还原（无需读取 checkpoint 之前的全部事件）：
            // - executionModelTurn：模型轮次计数（RequestId 稳定性）。
            // - conversationJson / toolObservationsJson：对话流与工具观察（模型上下文，
            // 事件流中同样存在，但 checkpoint 冗余一份使恢复可从游标断点续读）。
            // 与 RebuildStateFromEventsAsync 配合：有 Cursor 且 metadata 完整时走快路径
            // 还原，否则降级为全量事件重放（向后兼容旧 checkpoint）。
            var enrichedMetadata = new Dictionary<string, string>(
                checkpoint.Metadata ?? new Dictionary<string, string>(0),
                StringComparer.Ordinal)
            {
                ["executionModelTurn"] = executionModelTurn.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["conversationJson"] = JsonSerializer.Serialize(state.Context.Conversation),
                ["toolObservationsJson"] = JsonSerializer.Serialize(state.Context.ToolObservations)
            };
            checkpoint = checkpoint with { Metadata = enrichedMetadata };

            // 3c：checkpoint 本体不再单独 SaveAsync，而是缓冲到 _pendingTurnCheckpoint，
            // 随 Turn 结束的 AppendBatchAsync 在同一事务内持久化（Postgres：INSERT agent_checkpoints；
            // InMemory：委托注入的 IAgentCheckpointStore）。事件与 checkpoint 原子提交，顺序保证更强。
            // 
            // Bug 4 修复语义保留：若批量提交（含 checkpoint INSERT）失败，CheckpointSaved 事件
            // 也在同一批中回滚，不会出现"事件已记录但 checkpoint 未保存"的不一致。

            // 更新 state.LastCheckpoint
            state = state with { LastCheckpoint = checkpoint };

            // 记录 Turn 内最新 checkpoint，用于批量提交时的 checkpoint cursor + checkpoint 本体
            _pendingTurnCheckpoint = checkpoint;

            // 保存成功后才缓冲 CheckpointSaved 事件（顺序保证）
            state = BufferEvent(state, AgentRunEventType.CheckpointSaved, JsonSerializer.Serialize(new
            {
                checkpointId = checkpoint.CheckpointId,
                stateJsonLength = checkpoint.StateJson?.Length ?? 0,
                persisted = _checkpointStore is not null,
                sessionWorkspaceId = checkpoint.Session?.WorkspaceId,
                sessionValue = checkpoint.Session?.Value
            }));
        }

        // Checkpointing → ContextBuilding（循环继续）（本地推进）
        state = TransitionStateLocal(state, AgentRunState.ContextBuilding);

        return state;
    }

    /// <summary>
    /// 批量提交所有缓冲事件 + 可选 Run 状态 CAS + 可选 Checkpoint 游标，单事务提交。
    /// 将原本每事件一次 <see cref="IAgentRunEventStore.AppendAsync"/> 的网络往返
    /// 合并为 Turn 结束时一次 <see cref="IAgentRunEventStore.AppendBatchAsync"/>。
    /// </summary>
    public async Task FlushPendingEventsAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (_pendingTurnEvents.Count == 0)
        {
            return;
        }

        // 记录本批 flush 的事件数与是否含 checkpoint，用于成功后更新 _eventsSinceLastCheckpoint。
        var eventsBeingFlushed = _pendingTurnEvents.Count;
        var hasCheckpoint = _pendingTurnCheckpoint is not null;

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = _turnStartState,
            NewState = run.State,
            RunSnapshot = run,
            // 透传 lease token + fencing token，Postgres 实现在状态 CAS 与事件追加的
            // WHERE 子句中校验 lease 仍由当前实例持有；lease 被抢占时事务回滚并抛异常。
            LeaseToken = _leaseToken,
            FencingToken = _fencingToken
        };

        AgentCheckpointCursor? checkpointCursor = null;
        if (_pendingTurnCheckpoint is not null)
        {
            checkpointCursor = new AgentCheckpointCursor
            {
                WorkspaceId = run.WorkspaceId,
                RunId = run.RunId,
                CheckpointId = _pendingTurnCheckpoint.CheckpointId,
                LastEventSequence = _pendingTurnEvents[_pendingTurnEvents.Count - 1].Sequence
            };
        }

        try
        {
            if (_committer is not null)
            {
                // 统一提交入口：事件流 + 状态 CAS + checkpoint + 结算意图作为一次原子提交。
                // 终态语义（finished_at / 结算 outbox）由提交器从状态语义层派生，与 Run Store 一致。
                var commit = new AgentRunCommit
                {
                    Key = new TenantRunKey(run.WorkspaceId, run.RunId),
                    Events = _pendingTurnEvents,
                    ExpectedCurrentState = _turnStartState,
                    NewRunSnapshot = run,
                    Checkpoint = _pendingTurnCheckpoint,
                    UsageSnapshot = run.CostBudget,
                    LeaseToken = _leaseToken,
                    FencingToken = _fencingToken
                };
                await _committer.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _eventStore.AppendBatchAsync(
                    _pendingTurnEvents, runStateUpdate, checkpointCursor, _pendingTurnCheckpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // 3c：flush 失败时清除 checkpoint 本体，避免 Actor 的 FailAsync / TryTransitionToCancelledAsync
            // 重试时再次尝试保存已失败的 checkpoint（导致终态 flush 也失败、事件丢失）。
            // checkpoint 本体保存失败不应阻止事件流（含 RunFailed/RunCancelled）的终态持久化。
            // 同步移除 CheckpointSaved 事件并重建哈希链，避免重试时持久化声明不存在的 checkpoint 的孤立事件。
            // 事件链是 SHA-256 哈希链，RemoveEventsAndRebuildChain 全链重建 Sequence/PrevChainHash/ContentHash。
            // 其他事件（StateTransition/RunFailed/RunCancelled 等）保留，确保终态可持久化。
            _pendingTurnCheckpoint = null;
            RemoveEventsAndRebuildChain(_pendingTurnEvents, AgentRunEventType.CheckpointSaved);
            throw;
        }

        _pendingTurnEvents.Clear();
        _turnStartState = run.State;
        _pendingTurnCheckpoint = null;

        // 更新未 checkpoint 事件计数。
        // 本批含 checkpoint → 计数归零（所有事件已被 checkpoint 覆盖）；
        // 本批无 checkpoint → 累加本批事件数（mid-turn flush 的事件仍未 checkpoint）。
        if (hasCheckpoint)
        {
            _eventsSinceLastCheckpoint = 0;
        }
        else
        {
            _eventsSinceLastCheckpoint += eventsBeingFlushed;
        }
    }

    /// <summary>
    /// 从待提交事件列表中移除指定类型的事件，并重建 SHA-256 哈希链。
    /// 事件链的 Sequence 单调递增、PrevChainHash 指向前一事件 ContentHash、ContentHash 包含 Sequence。
    /// 直接删除中间事件会破坏三者一致性，必须全链重建。
    /// </summary>
    private static void RemoveEventsAndRebuildChain(List<AgentRunEvent> events, AgentRunEventType typeToRemove)
    {
        if (events.Count == 0) return;
        var hasTarget = false;
        foreach (var e in events) { if (e.EventType == typeToRemove) { hasTarget = true; break; } }
        if (!hasTarget) return;
        var startSequence = events[0].Sequence;
        var startPrevChainHash = events[0].PrevChainHash;
        var runId = events[0].RunId;
        var workspaceId = events[0].WorkspaceId;
        var filtered = new List<AgentRunEvent>(events.Count);
        foreach (var e in events) { if (e.EventType != typeToRemove) filtered.Add(e); }
        events.Clear();
        var prevChainHash = startPrevChainHash;
        var sequence = startSequence;
        foreach (var evt in filtered)
        {
            var rebuilt = AgentRunEventChain.BuildEvent(runId, workspaceId, sequence, evt.EventType, evt.State, evt.Payload, prevChainHash);
            events.Add(rebuilt);
            prevChainHash = rebuilt.ContentHash;
            sequence++;
        }
    }
}
