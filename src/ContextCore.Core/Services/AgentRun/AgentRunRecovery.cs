using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// Agent Run 崩溃恢复 / resume 协作者。
/// </summary>
/// <remarks>
/// 从事件流（或 checkpoint / 可恢复快照）重建执行状态，按
/// "Snapshot → validate anchor → replay hot delta" 或 checkpoint 快路径或全量事件重放恢复；
/// 恢复失败时进入恢复失败状态（fail-closed，不写事件流，状态直写 + 退避重试 + 人工介入告警）。
/// </remarks>
internal sealed class AgentRunRecovery
{
    private readonly IAgentRunStore _runStore;
    private readonly IAgentRunEventStore _eventStore;
    private readonly IAgentCheckpointStore? _checkpointStore;
    private readonly IAgentRunEventCompactor? _eventCompactor;
    private readonly AgentHostOptions? _hostOptions;
    private readonly IRecoveryAlertSink? _alertSink;
    private readonly AgentRunEventBuffer _eventBuffer;

    // 当前执行期的 lease token 与 fencing token（由 Actor 在调用恢复时注入；
    // 恢复失败状态直写时由 Postgres 实现在状态 CAS 的 WHERE 子句中校验 lease 仍由当前实例持有）。
    private string? _leaseToken;
    private long? _fencingToken;
    // 恢复重建的模型轮次计数（执行期模型轮次；恢复结束后由 Actor 采用）。
    private int _executionModelTurn;

    // 事件恢复 keyset pagination 页大小（基于 sequence 索引的分页读取）。
    private const int RecoveryEventPageSize = 500;

    /// <summary>
    /// 构造恢复协作者。
    /// </summary>
    /// <param name="runStore">Run 元数据存储（恢复失败状态直写用）。</param>
    /// <param name="eventStore">Run 事件流存储（哈希链）。</param>
    /// <param name="checkpointStore">Checkpoint Store（null 时跳过 checkpoint 快路径）。</param>
    /// <param name="eventCompactor">事件流压缩器（null = 非 Postgres provider，无快照/归档，走全量重放）。</param>
    /// <param name="hostOptions">Host 选项（恢复退避参数；null 时用默认值 30s base / 30min cap）。</param>
    /// <param name="alertSink">人工介入告警接收器（null = 不告警；best-effort 钩子）。</param>
    /// <param name="eventBuffer">事件缓冲协作者（本地状态推进 / 未 checkpoint 计数回填 / Turn 起始状态）。</param>
    public AgentRunRecovery(
        IAgentRunStore runStore,
        IAgentRunEventStore eventStore,
        IAgentCheckpointStore? checkpointStore,
        IAgentRunEventCompactor? eventCompactor,
        AgentHostOptions? hostOptions,
        IRecoveryAlertSink? alertSink,
        AgentRunEventBuffer eventBuffer)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore;
        _eventCompactor = eventCompactor;
        _hostOptions = hostOptions;
        _alertSink = alertSink;
        _eventBuffer = eventBuffer ?? throw new ArgumentNullException(nameof(eventBuffer));
    }

    /// <summary>
    /// 运行时能力补齐：从事件流重建 <see cref="AgentRunExecutionState"/>（崩溃恢复 / resume）。
    /// </summary>
    /// <param name="state">初始执行状态（Run + 默认 Context）。</param>
    /// <param name="leaseToken">租约 token（null = 无 lease 路径；恢复失败状态直写时透传）。</param>
    /// <param name="fencingToken">fencing token（与 lease token 同时为 null 或同时非 null）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重建后的执行状态（含 ToolObservations / EventSequence / EventChainHash）与恢复重建的模型轮次计数。</returns>
    /// <remarks>
    /// <b>重建策略</b>（优先级从高到低）：
    /// <list type="bullet">
    /// <item>可恢复快照（正式方案）：存在可解析的 Recoverable Snapshot 且覆盖范围
    /// 超过重放起点时，按 "Snapshot → validate anchor → replay hot delta" 恢复——
    /// 快照还原折叠前缀 [0..anchor] 的状态（对话流 / 工具观察 / 模型轮次 / Pending 命令），
    /// 校验热表锚点 ContentHash == 快照 ChainHeadHash，再重放锚点后的热表增量事件。</item>
    /// <item>存在 Checkpoint Cursor 且 checkpoint 本体 metadata 完整时走快路径：
    /// 从 checkpoint 还原对话流 / 工具观察 / 模型轮次，仅重放游标之后的新事件。</item>
    /// <item>否则读取 Run 的完整事件流（按 Sequence 升序），从事件重建全部状态。</item>
    /// <item>从 ToolCallCompleted 事件解析 ToolObservation（含 output / error / succeeded / toolName / toolCallId）。</item>
    /// <item>EventSequence / EventChainHash 从最后一个事件恢复（保证后续事件哈希链连续）。</item>
    /// <item>LastModelResponse 置为 null（事件流中不含完整模型响应内容），强制重新调用模型。</item>
    /// <item>本地状态规范化为 ContextBuilding（LoopPolicy 会决定 CallModel）。</item>
    /// </list>
    /// 
    /// <b>状态一致性</b>：
    /// - 本地状态（ContextBuilding）用于状态机校验（TransitionStateLocal）。
    /// - Store 状态（run.State）用于 CAS（事件缓冲 TurnStartState = run.State）。
    /// - 两者可以不同：本地状态决定状态机校验是否通过，store 状态决定 CAS 是否匹配。
    /// - 首次批量提交时 CAS 从 store 状态推进到新状态（store 不校验状态机流转）。
    /// 
    /// <b>幂等性保证</b>：
    /// 重新调用模型后若返回相同 ToolCalls，IDurableToolExecutor 通过 journal 保证
    /// 已 commit 的 Tool 不会被重复执行（返回缓存结果）。
    /// 
    /// <b>降级处理</b>：
    /// 快路径/快照路径读取失败时降级为全量事件重放；若事件流为空（崩溃发生在首次 flush 之前）
    /// 或无事件可读，回退为全新启动路径。快照缺失/不可解析（旧格式）时，压缩过的热表
    /// 会由全量重放 fail-closed 判定 RecoveryCorrupted（安全，需运维介入）。
    /// </remarks>
    public async Task<(AgentRunExecutionState State, int ExecutionModelTurn)> RebuildStateFromEventsAsync(
        AgentRunExecutionState state,
        string? leaseToken,
        long? fencingToken,
        CancellationToken cancellationToken)
    {
        _leaseToken = leaseToken;
        _fencingToken = fencingToken;

        // 获取最新 Checkpoint Cursor（失败非致命：降级为全量事件重放）
        AgentCheckpointCursor? cursor = null;
        try
        {
            cursor = await _eventStore.GetCheckpointCursorAsync(
                state.Run.WorkspaceId, state.Run.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cursor 读取失败非致命
        }

        // 尝试从 checkpoint 本体还原可恢复状态（对话流 / 工具观察 / 模型轮次）。
        // 仅当存在 Cursor、checkpoint 本体可读且 metadata 完整时才启用
        // "从游标断点续读"快路径（只重放 checkpoint 之后的新事件）；
        // 否则降级为全量事件重放（兼容旧 checkpoint 与无 checkpoint 场景）。
        var restoredContext = await TryRestoreCheckpointContextAsync(
            cursor, state.Run, cancellationToken).ConfigureAwait(false);
        var useCursorFastPath = restoredContext is not null;

        // Keyset pagination：快路径从游标之后开始读，全量路径从 0 开始
        var allEvents = new List<AgentRunEvent>();
        var fromSequence = useCursorFastPath ? cursor!.LastEventSequence + 1 : 0;

        // 不可变 Attempt（重试不删历史）：以最后一个 RunRetryScheduled 事件为 Attempt 边界，
        // 只重放当前 Attempt 的事件（Sequence > 边界）——前序 Attempt 的对话/工具观察
        // 已由全新启动清空，若连同重放会用旧 Attempt 上下文污染当前 Attempt。
        var attemptBoundaryStart = 0;
        try
        {
            var attemptBoundary = await _eventStore.GetAttemptBoundarySequenceAsync(
                state.Run.WorkspaceId, state.Run.RunId, cancellationToken).ConfigureAwait(false);
            if (attemptBoundary >= 0)
            {
                attemptBoundaryStart = attemptBoundary + 1;
                if (attemptBoundaryStart > fromSequence)
                {
                    fromSequence = attemptBoundaryStart;
                }
            }
        }
        catch
        {
            // 边界查询失败：交由后续事件流读取路径判定（存储不可用 → RecoveryDependencyUnavailable）。
        }

        string? expectedPrevChainHash = null;
        AgentRunEvent? boundaryEvent = null;
        var readFailed = false;
        // 区分恢复失败原因：事件数据损坏（哈希链/序列号/ContentHash） vs 存储不可用。
        var recoveryCorruptionDetected = false;

        // ── 可恢复快照探测（正式方案）────────────────────────────
        // 折叠前缀已归档到 agent_run_events_archive，热表只保留锚点 + 增量事件，
        // Recovery 不能依赖"从 Sequence 0 全量重放"。存在可解析的 Recoverable
        // Snapshot 且覆盖范围超过当前重放起点（fromSequence）时，启用快照路径：
        //   Snapshot → validate anchor → replay hot delta。
        // 忽略快照的场景：
        // - 快照不可解析（旧格式仅序列化锚点事件 / 损坏）→ 降级为现有恢复路径
        //   （压缩过的热表会 fail-closed 判定 RecoveryCorrupted，安全，需运维介入）；
        // - 快照覆盖范围落后于 attempt 边界 / checkpoint 游标（fromSequence 更晚）
        //   → 保留 cursor/attempt 路径，防止旧 Attempt 上下文污染当前 Attempt。
        // 快照读取失败（存储不可用）非致命：降级为现有恢复路径。
        var snapshotRestore = await TryRestoreSnapshotStateAsync(state.Run, cancellationToken).ConfigureAwait(false);
        var recoverableState = snapshotRestore?.State;
        // 快照记录链头（agent_run_event_snapshots.chain_head_hash）是锚点校验的权威基准；
        // state_json 内嵌 ChainHeadHash 与其在同一事务写入，二者一致（不一致视为快照损坏）。
        var snapshotChainHeadHash = snapshotRestore?.ChainHeadHash;
        var useSnapshotPath = recoverableState is not null && fromSequence < recoverableState.Sequence + 1;
        if (useSnapshotPath)
        {
            // 快照优先于 checkpoint 快路径：cursor 指向的事件可能已被归档（< 锚点），
            // 快照本身已覆盖折叠历史，无需 checkpoint metadata 还原。
            useCursorFastPath = false;
            restoredContext = null;
            fromSequence = recoverableState!.Sequence + 1;
            expectedPrevChainHash = snapshotChainHeadHash;
        }

        while (true)
        {
            try
            {
                // 从 fromSequence - 1 读取哈希链锚点事件（快路径 = checkpoint 游标指向的事件；
                // 不可变 Attempt 边界 = RunRetryScheduled 事件；快照路径 = 快照锚点事件）——
                // 首个重放事件的 PrevChainHash 必须等于锚点 ContentHash（跨 Attempt 哈希链连续）。
                if (fromSequence > 0)
                {
                    var boundaryPage = await _eventStore.ReadAsync(
                        state.Run.WorkspaceId, state.Run.RunId,
                        fromSequence: fromSequence - 1, take: 1, cancellationToken).ConfigureAwait(false);
                    if (boundaryPage.Count == 1)
                    {
                        boundaryEvent = boundaryPage[0];
                        expectedPrevChainHash = boundaryEvent.ContentHash;

                        // 快照锚点校验（Snapshot → validate anchor）：热表锚点事件的
                        // ContentHash 必须与快照记录链头（chain_head_hash 列）一致——
                        // 锚点被替换/篡改时立即判定 RecoveryCorrupted（快照记录与归档
                        // 同一事务写入，是权威基准）。
                        if (useSnapshotPath
                            && !string.Equals(boundaryEvent.ContentHash, snapshotChainHeadHash, StringComparison.Ordinal))
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"快照锚点哈希校验失败：热表锚点事件 sequence={boundaryEvent.Sequence} 的 " +
                                $"ContentHash 与快照 ChainHeadHash 不一致（run={state.Run.RunId}）。");
                        }
                    }
                    else
                    {
                        // 锚点事件不存在（数据异常）→ 降级为全量重放
                        throw new AgentRunRecoveryCorruptionException(
                            $"重放锚点事件不存在（sequence={fromSequence - 1}，run={state.Run.RunId}）。");
                    }
                }

                while (true)
                {
                    var page = await _eventStore.ReadAsync(
                        state.Run.WorkspaceId, state.Run.RunId,
                        fromSequence: fromSequence, take: RecoveryEventPageSize, cancellationToken)
                        .ConfigureAwait(false);

                    if (page.Count == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < page.Count; i++)
                    {
                        var evt = page[i];
                        var expectedSequence = fromSequence + i;

                        // 重算 ContentHash 校验（fail-closed）：不信任存储的 ContentHash，
                        // 每次恢复读取都按事件内容重算比对，检测篡改/损坏。
                        if (!AgentRunEventChain.VerifyContentHash(evt))
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"事件 ContentHash 校验失败：sequence={evt.Sequence} 重算哈希与存储值不匹配（run={state.Run.RunId}）。");
                        }
                        if (evt.Sequence != expectedSequence)
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"事件序列号不连续：期望 {expectedSequence}，实际 {evt.Sequence}（run={state.Run.RunId}）。");
                        }
                        var expectedHash = (i == 0) ? expectedPrevChainHash : page[i - 1].ContentHash;
                        if (!string.Equals(evt.PrevChainHash, expectedHash, StringComparison.Ordinal))
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"事件哈希链断裂：sequence={evt.Sequence} 的 PrevChainHash 与前一事件 ContentHash 不匹配（run={state.Run.RunId}）。");
                        }
                    }

                    allEvents.AddRange(page);
                    expectedPrevChainHash = page[page.Count - 1].ContentHash;
                    fromSequence += page.Count;

                    if (page.Count < RecoveryEventPageSize)
                    {
                        break;
                    }
                }
                break;
            }
            catch (Exception ex)
            {
                // 事件流读取失败（store 不可用 / 跨 workspace 不可见 / 哈希链断裂 / 内容损坏）。
                // 记录失败类型：损坏异常 → RecoveryCorrupted；其余 → RecoveryDependencyUnavailable。
                // 快路径先降级为全量重放重试一次；仍失败则进入恢复失败终态（fail-closed，
                // 不回退为全新启动——回退可能重复已执行的外部副作用）。
                recoveryCorruptionDetected = ex is AgentRunRecoveryCorruptionException;
                if (useCursorFastPath)
                {
                    useCursorFastPath = false;
                    restoredContext = null;
                    allEvents.Clear();
                    // 降级为全量重放：仍受不可变 Attempt 边界约束（跳过前序 Attempt 事件）。
                    fromSequence = attemptBoundaryStart;
                    expectedPrevChainHash = null;
                    boundaryEvent = null;
                    continue;
                }
                readFailed = true;
                break;
            }
        }

        if (readFailed)
        {
            // fail-closed：恢复读取失败不得回退为全新启动（可能重复外部副作用）。
            // 事件数据损坏 → RecoveryCorrupted；存储不可用 → RecoveryDependencyUnavailable。
            var recoveryState = recoveryCorruptionDetected
                ? AgentRunState.RecoveryCorrupted
                : AgentRunState.RecoveryDependencyUnavailable;
            return (await EnterRecoveryFailureStateAsync(state, recoveryState, cancellationToken).ConfigureAwait(false), _executionModelTurn);
        }

        // 快照路径：快照已覆盖锚点之前的折叠历史，仅需重放锚点后的热表增量事件
        // （Snapshot → validate anchor → replay hot delta）。
        if (useSnapshotPath)
        {
            return (BuildResumedStateFromSnapshot(state, recoverableState!, boundaryEvent!, allEvents), _executionModelTurn);
        }

        // 快路径：checkpoint 已覆盖游标之前的历史，仅需重放游标之后的新事件
        if (useCursorFastPath)
        {
            return (BuildResumedStateFromCheckpoint(
                state, restoredContext!.Value, boundaryEvent!, allEvents), _executionModelTurn);
        }

        if (allEvents.Count == 0)
        {
            // fail-closed：仅「Run 确实处于 Created 且从未写入任何事件」允许回退为全新启动。
            // 本方法仅在 isResume（run.State != Created）时被调用，因此零事件意味着
            // Run 已推进过状态但事件流为空（事件数据丢失）→ 不得回退为全新启动，
            // 标记 RecoveryBlocked 等待运维介入（回退可能重复已执行的外部副作用）。
            return (await EnterRecoveryFailureStateAsync(state, AgentRunState.RecoveryBlocked, cancellationToken).ConfigureAwait(false), _executionModelTurn);
        }

        // 从 Cursor 初始化事件缓冲的未 checkpoint 事件计数。
        // cursor.LastEventSequence 是已 checkpoint 的最后事件序列号（0-based），
        // 未 checkpoint 事件数 = 总事件数 - (lastCheckpointedSequence + 1)。
        // 无 Cursor 时全部事件视为未 checkpoint（首次恢复或 InMemory 无游标）。
        if (cursor is not null)
        {
            var checkpointedCount = cursor.LastEventSequence + 1;
            _eventBuffer.EventsSinceLastCheckpoint = allEvents.Count > checkpointedCount
                ? allEvents.Count - checkpointedCount
                : 0;
        }
        else
        {
            _eventBuffer.EventsSinceLastCheckpoint = allEvents.Count;
        }

        var events = allEvents;

        // 从事件流按时间顺序无损重建 Conversation（Assistant + Tool 消息）。
        // ModelCallCompleted 事件携带完整模型响应（content + toolCalls[]），重建 Assistant 消息；
        // ToolCallCompleted 事件携带 Tool 执行结果，重建 Tool 消息。
        // 按事件 Sequence 顺序遍历，保证 "assistant tool_calls → tool result" 因果顺序。
        // 旧事件缺少字段时跳过对应重建（向后兼容）；单事件解析失败不影响整体恢复。
        var toolObservations = new List<ToolObservation>();
        var rebuiltConversation = new List<AgentMessage>();
        foreach (var evt in events)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, rebuiltConversation, toolObservations);
        }

        // 从最后一个事件恢复 EventSequence / EventChainHash（保证哈希链连续）
        var lastEvent = events[events.Count - 1];

        // 从事件流统计 ModelCallCompleted 重建模型轮次计数，
        // 避免恢复后从 0 重新计数导致 RequestId 改变（Journal 无法识别原调用）。
        _executionModelTurn = AgentRunEventStateRebuilder.RebuildExecutionModelTurn(events);

        // 从 PendingToolExecution / ToolDispatching 状态恢复——不重新调用模型，
        // 而是重放原 Tool（审批路径从 ApprovalRequested 提取；Kill 中断路径从
        // ToolCallStarted 提取，原始 ModelTurnRevision 保证 RequestId 与 journal 一致）。
        // 审批 API 在裁决时将 Run 状态推进到 PendingToolExecution（批准）或 Failed（拒绝）；
        // ToolDispatching 是 Tool 执行中途被 Kill 时持久化的状态。
        // 此处仅处理 PendingToolExecution/ToolDispatching（Failed 已是终态，不会进入 resume）。
        if (state.Run.State == AgentRunState.PendingToolExecution || state.Run.State == AgentRunState.ToolDispatching)
        {
            var pendingCommands = AgentRunEventStateRebuilder.ExtractPendingToolCommands(events)
                ?? AgentRunEventStateRebuilder.ExtractPendingCommandsFromToolCallStarted(events);
            if (pendingCommands is { Count: > 0 })
            {
                // 保持 PendingToolExecution 状态（主循环检测后直接执行原 Tool）
                var pendingRun = state.Run with { State = AgentRunState.PendingToolExecution };
                return (state with
                {
                    Run = pendingRun,
                    Context = new AgentContextState
                    {
                        CurrentTask = state.Run.Task,
                        Messages = new List<AgentMessage>(),
                        ToolObservations = toolObservations,
                        Conversation = rebuiltConversation,
                        StableMemoryReferences = new List<MemoryReference>(),
                        LastModelTurn = null
                    },
                    LastModelResponse = null,
                    LastDecisionResult = null,
                    EventSequence = lastEvent.Sequence + 1,
                    EventChainHash = lastEvent.ContentHash,
                    PendingToolCommands = pendingCommands
                }, _executionModelTurn);
            }
            // PendingToolCommands 提取失败（事件 payload 损坏/缺失）→ 降级为 ContextBuilding 重新调用模型
        }

        // 本地状态规范化为 ContextBuilding：
        // - LoopPolicy 在 lastModelResponse=null 时返回 CallModel
        // - CallModelAsync 经事件缓冲的 TransitionStateLocal(ContextBuilding, ModelCalling) 合法
        // - Store 状态（run.State）仍为崩溃时的状态，首次 CAS 以此为 expected state
        var resumedRun = state.Run with { State = AgentRunState.ContextBuilding };

        return (state with
        {
            Run = resumedRun,
            Context = new AgentContextState
            {
                CurrentTask = state.Run.Task,
                Messages = new List<AgentMessage>(),
                ToolObservations = toolObservations,
                Conversation = rebuiltConversation,
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        }, _executionModelTurn);
    }

    /// <summary>
    /// 进入恢复失败状态（fail-closed）并尽力持久化。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="recoveryState">恢复失败状态（RecoveryBlocked / RecoveryCorrupted / RecoveryDependencyUnavailable）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已标记恢复失败状态的执行状态（本地为恢复失败状态，主循环不会执行）。</returns>
    /// <remarks>
    /// 恢复失败路径不向事件流追加事件：事件流本身可能已损坏或不可用，追加 StateTransition
    /// 事件需要读取真实尾事件作为序列/哈希链锚点，而依赖不可用时无法读取；且向待运维修复的
    /// 事件流写入会引入更多不确定状态。因此采用状态直写（与 RecoveryWorker / Endpoints 的
    /// "无事件状态推进"路径一致）：run store 的 state CAS（expected = 事件缓冲 TurnStartState）+
    /// 字段更新（FailureReason / RecoveryAttempt / NextRetryAtUtc）。持久化失败（典型场景：
    /// RecoveryDependencyUnavailable 时存储本身不可用）时静默降级并记录警告——Run 保持原
    /// 非终态，由 RecoveryWorker 在依赖恢复后重试恢复；不抛给 FailAsync，避免把恢复失败
    /// 误标为 Failed 而丢失 Recovery* 语义。
    /// 
    /// Recovery Integrity State：
    /// - RecoveryBlocked / RecoveryCorrupted 为终态（数据损坏，等待运维介入），每次进入均告警；
    /// - RecoveryDependencyUnavailable 可重试：按指数退避（base × 2^(attempt-1)，封顶 cap）
    /// 计算 <see cref="AgentRun.NextRetryAtUtc"/>，递增 <see cref="AgentRun.RecoveryAttempt"/>，
    /// 由 Recovery Worker 在退避门通过后重新入队；仅首次（attempt==1）告警避免告警风暴。
    /// </remarks>
    public async Task<AgentRunExecutionState> EnterRecoveryFailureStateAsync(
        AgentRunExecutionState state,
        AgentRunState recoveryState,
        CancellationToken cancellationToken,
        string? reasonOverride = null)
    {
        var failureReason = reasonOverride ?? recoveryState switch
        {
            AgentRunState.RecoveryBlocked => "RecoveryBlocked：事件流为空（事件数据丢失），无法安全重建执行状态，需运维介入。",
            AgentRunState.RecoveryCorrupted => "RecoveryCorrupted：事件流损坏（哈希链断裂 / 序列不连续 / ContentHash 重算不匹配），需运维介入。",
            _ => "RecoveryDependencyUnavailable：事件存储不可用，等待依赖恢复后由恢复 Worker 重试。"
        };

        // 退避重试：仅可重试的恢复状态（依赖暂时不可用，非数据损坏）计算退避。
        // 可重试性统一来自 AgentRunStateSemantics（RecoveryDependencyUnavailable 可重试；
        // RecoveryBlocked / RecoveryCorrupted 为终态，不计算退避、不自动重试）。
        var isRetryable = AgentRunStateSemantics.Get(recoveryState).Retryable;
        var recoveryAttempt = isRetryable ? state.Run.RecoveryAttempt + 1 : 0;
        DateTimeOffset? nextRetryAtUtc = null;
        if (isRetryable)
        {
            var backoffBase = _hostOptions?.RetryBackoffBase > TimeSpan.Zero
                ? _hostOptions.RetryBackoffBase
                : TimeSpan.FromSeconds(30);
            var backoffCap = _hostOptions?.RetryBackoffMax > TimeSpan.Zero
                ? _hostOptions.RetryBackoffMax
                : TimeSpan.FromMinutes(30);
            var baseDelay = backoffBase > backoffCap ? backoffCap : backoffBase;
            // attempt 从 1 开始：首次失败等待 1×base，与 Durable Scheduler 的重试退避语义对齐
            // （retry_count 从 0 起算时同样 base × 2^(retry_count)）。
            var delay = baseDelay * Math.Pow(2, Math.Max(0, recoveryAttempt - 1));
            if (delay > backoffCap)
            {
                delay = backoffCap;
            }
            nextRetryAtUtc = DateTimeOffset.UtcNow + delay;
        }

        // 本地推进为恢复失败状态（不缓冲事件）：主循环据此退出，不执行任何 Agent 逻辑。
        state = _eventBuffer.TransitionStateLocal(state, recoveryState, bufferEvent: false);
        try
        {
            await _runStore.TransitionStateAsync(
                state.Run.WorkspaceId, state.Run.RunId, _eventBuffer.TurnStartState, recoveryState,
                cancellationToken, _leaseToken, _fencingToken).ConfigureAwait(false);
            // RecoveryAttempt / NextRetryAtUtc 仅写入 jsonb（data 全量序列化），无独立列、无迁移。
            await _runStore.UpdateAsync(
                state.Run with
                {
                    FailureReason = failureReason,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RecoveryAttempt = isRetryable ? recoveryAttempt : state.Run.RecoveryAttempt,
                    NextRetryAtUtc = nextRetryAtUtc
                },
                cancellationToken).ConfigureAwait(false);
            _eventBuffer.TurnStartState = recoveryState;

            // 人工介入告警（best-effort，仅在持久化成功后投递）：
            // - RecoveryBlocked / RecoveryCorrupted：数据损坏级事件，每次进入均告警。
            // - RecoveryDependencyUnavailable：仅首次（RecoveryAttempt == 1）告警，
            // 持续不可用时只记日志，避免告警风暴；依赖长期不恢复仍由运维巡检发现。
            var alertKind = recoveryState switch
            {
                AgentRunState.RecoveryBlocked => AgentRunAlertKind.RecoveryBlocked,
                AgentRunState.RecoveryCorrupted => AgentRunAlertKind.RecoveryCorrupted,
                _ => AgentRunAlertKind.RecoveryDependencyUnavailable
            };
            var shouldAlert = _alertSink is not null
                && (alertKind != AgentRunAlertKind.RecoveryDependencyUnavailable || recoveryAttempt == 1);
            if (shouldAlert)
            {
                var alert = new AgentRunAlert
                {
                    RunId = state.Run.RunId,
                    WorkspaceId = state.Run.WorkspaceId,
                    SessionId = state.Run.SessionId,
                    Kind = alertKind,
                    Reason = failureReason,
                    Attempt = alertKind == AgentRunAlertKind.RecoveryDependencyUnavailable ? recoveryAttempt : 0
                };
                try
                {
                    await _alertSink!.NotifyInterventionRequiredAsync(alert, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception alertEx)
                {
                    // best-effort：告警投递失败不阻断执行（catch + log）。
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunRecovery] 投递人工介入告警 {0} 失败（run={1}，workspace={2}）：{3}。",
                        alertKind, state.Run.RunId, state.Run.WorkspaceId, alertEx.Message);
                }
            }
        }
        catch (Exception ex)
        {
            // 尽力而为：无法持久化恢复失败状态时记录警告，Run 保持原非终态等待重试。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunRecovery] 持久化恢复失败状态 {0} 失败（run={1}，workspace={2}）：{3}。" +
                "Run 保持原状态，将由 RecoveryWorker 在依赖恢复后重试恢复。",
                recoveryState, state.Run.RunId, state.Run.WorkspaceId, ex.Message);
        }
        return state;
    }

    /// <summary>
    /// 从 checkpoint 本体还原可恢复的执行状态（对话流 / 工具观察 / 模型轮次）。
    /// </summary>
    /// <returns>
    /// 还原成功返回三元组；Cursor 缺失、checkpoint 本体不可读或 metadata 不完整时返回 null
    /// （调用方降级为全量事件重放）。审批恢复路径（PendingToolExecution）不走快路径，
    /// 因为 PendingToolCommands 需从 ApprovalRequested 事件提取，事件可能早于游标。
    /// </returns>
    private async Task<(List<AgentMessage> Conversation, List<ToolObservation> ToolObservations, int ExecutionModelTurn)?> TryRestoreCheckpointContextAsync(
        AgentCheckpointCursor? cursor,
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (cursor is null || _checkpointStore is null || run.State == AgentRunState.PendingToolExecution)
        {
            return null;
        }

        AgentCheckpoint? checkpoint;
        try
        {
            checkpoint = await _checkpointStore.GetAsync(
                cursor.WorkspaceId, cursor.CheckpointId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // checkpoint 本体不可读（存储不可用 / 已被清理）→ 降级为全量重放
            return null;
        }

        if (checkpoint?.Metadata is null
            || !checkpoint.Metadata.TryGetValue("conversationJson", out var conversationJson)
            || string.IsNullOrWhiteSpace(conversationJson)
            || !checkpoint.Metadata.TryGetValue("toolObservationsJson", out var observationsJson)
            || string.IsNullOrWhiteSpace(observationsJson)
            || !checkpoint.Metadata.TryGetValue("executionModelTurn", out var turnText)
            || !int.TryParse(turnText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var executionModelTurn))
        {
            // 旧 checkpoint 未携带完整 metadata → 降级为全量重放
            return null;
        }

        try
        {
            var conversation = JsonSerializer.Deserialize<List<AgentMessage>>(conversationJson);
            var observations = JsonSerializer.Deserialize<List<ToolObservation>>(observationsJson);
            if (conversation is null || observations is null)
            {
                return null;
            }
            return (conversation, observations, executionModelTurn);
        }
        catch (JsonException)
        {
            // metadata 损坏 → 降级为全量重放
            return null;
        }
    }

    /// <summary>
    /// 从 checkpoint 还原的状态 + 游标之后的新事件构建恢复状态（快路径）。
    /// </summary>
    private AgentRunExecutionState BuildResumedStateFromCheckpoint(
        AgentRunExecutionState state,
        (List<AgentMessage> Conversation, List<ToolObservation> ToolObservations, int ExecutionModelTurn) restored,
        AgentRunEvent boundaryEvent,
        IReadOnlyList<AgentRunEvent> newEvents)
    {
        // checkpoint 覆盖游标之前的历史；在此基础上追加游标之后的新事件
        var conversation = new List<AgentMessage>(restored.Conversation);
        var toolObservations = new List<ToolObservation>(restored.ToolObservations);
        foreach (var evt in newEvents)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, conversation, toolObservations);
        }

        _executionModelTurn = restored.ExecutionModelTurn;
        // 本次读取的事件均为未 checkpoint 事件
        _eventBuffer.EventsSinceLastCheckpoint = newEvents.Count;

        // 最后事件：有新事件取最后一个；否则取游标指向的事件（无新事件时即最后事件）
        var lastEvent = newEvents.Count > 0 ? newEvents[^1] : boundaryEvent;

        // 本地状态规范化为 ContextBuilding（快路径已排除 PendingToolExecution）
        var resumedRun = state.Run with { State = AgentRunState.ContextBuilding };

        return state with
        {
            Run = resumedRun,
            Context = new AgentContextState
            {
                CurrentTask = state.Run.Task,
                Messages = new List<AgentMessage>(),
                ToolObservations = toolObservations,
                Conversation = conversation,
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            LastDecisionResult = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        };
    }

    /// <summary>
    /// 尝试从事件流压缩器读取可恢复快照（正式方案）。
    /// </summary>
    /// <returns>
    /// 快照可解析为 <see cref="AgentRunRecoverableState"/> 时返回 (状态, 快照记录链头)；
    /// 以下场景返回 null（调用方降级为现有恢复路径）：
    /// <list type="bullet">
    /// <item>压缩器未注入（非 Postgres provider，无快照/归档）；</item>
    /// <item>快照读取失败（存储不可用）——非致命，降级为现有恢复路径；</item>
    /// <item>快照不存在（从未压缩）或 state_json 为空；</item>
    /// <item>旧格式快照（仅序列化锚点事件，缺少可恢复状态成员，无法解析）或 JSON 损坏——
    /// 压缩过的热表会由后续全量重放 fail-closed 判定 RecoveryCorrupted（安全，需运维介入）。</item>
    /// </list>
    /// </returns>
    private async Task<(AgentRunRecoverableState State, string? ChainHeadHash)?> TryRestoreSnapshotStateAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (_eventCompactor is null)
        {
            return null;
        }

        AgentRunEventSnapshot? snapshot;
        try
        {
            snapshot = await _eventCompactor.GetSnapshotAsync(
                run.WorkspaceId, run.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 快照读取失败非致命：降级为现有恢复路径（存储不可用由后续事件流读取判定）。
            return null;
        }

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.StateJson))
        {
            return null;
        }

        var state = AgentRunEventStateRebuilder.TryDeserialize(snapshot.StateJson);
        if (state is null)
        {
            return null;
        }

        return (state, snapshot.ChainHeadHash);
    }

    /// <summary>
    /// 从可恢复快照还原的状态 + 锚点后的热表增量事件构建恢复状态（快照快路径）。
    /// </summary>
    /// <remarks>
    /// 正式方案（Snapshot → validate anchor → replay hot delta）的最后一步：
    /// 快照覆盖折叠前缀 [0..anchor] 的重建状态（Conversation / ToolObservations /
    /// ExecutionModelTurn / PendingToolCommands），在此之上重放锚点后的热表增量事件；
    /// 锚点一致性已由调用方（RebuildStateFromEventsAsync 边界读取）按 ChainHeadHash 校验。
    /// 审批恢复（PendingToolExecution）优先从增量事件提取更新的 ApprovalRequested，
    /// 增量无审批事件时回退到快照保存的 PendingToolCommands。
    /// </remarks>
    private AgentRunExecutionState BuildResumedStateFromSnapshot(
        AgentRunExecutionState state,
        AgentRunRecoverableState recoverable,
        AgentRunEvent boundaryEvent,
        IReadOnlyList<AgentRunEvent> deltaEvents)
    {
        // 快照覆盖锚点之前的折叠历史；在此基础上重放锚点后的热表增量事件
        var conversation = new List<AgentMessage>(recoverable.Conversation);
        var toolObservations = new List<ToolObservation>(recoverable.ToolObservations);
        foreach (var evt in deltaEvents)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, conversation, toolObservations);
        }

        // 模型轮次：快照折叠值 + 增量事件的最大值（增量内嵌更高轮次时取高，
        // 避免恢复后 RequestId 回退导致 Journal 无法识别原调用）。
        _executionModelTurn = AgentRunEventStateRebuilder.RebuildExecutionModelTurn(
            deltaEvents, recoverable.ExecutionModelTurn);
        // 本次读取的事件均为快照之后的新事件（未计入快照折叠范围）
        _eventBuffer.EventsSinceLastCheckpoint = deltaEvents.Count;

        // 最后事件：有新事件取最后一个；否则取锚点事件（无增量时即最后事件）
        var lastEvent = deltaEvents.Count > 0 ? deltaEvents[^1] : boundaryEvent;

        // 审批/执行中断恢复：PendingToolExecution/ToolDispatching 状态时优先从增量事件提取
        // （更新的 ApprovalRequested / ToolCallStarted），增量无结果时回退到快照中保存的
        // PendingToolCommands（折叠前缀已归档）。
        if (state.Run.State == AgentRunState.PendingToolExecution || state.Run.State == AgentRunState.ToolDispatching)
        {
            var pendingCommands = AgentRunEventStateRebuilder.ExtractPendingToolCommands(deltaEvents)
                ?? recoverable.PendingToolCommands
                ?? AgentRunEventStateRebuilder.ExtractPendingCommandsFromToolCallStarted(deltaEvents);
            if (pendingCommands is { Count: > 0 })
            {
                // 保持 PendingToolExecution 状态（主循环检测后直接执行原 Tool）
                var pendingRun = state.Run with { State = AgentRunState.PendingToolExecution };
                return state with
                {
                    Run = pendingRun,
                    Context = new AgentContextState
                    {
                        CurrentTask = state.Run.Task,
                        Messages = new List<AgentMessage>(),
                        ToolObservations = toolObservations,
                        Conversation = conversation,
                        StableMemoryReferences = new List<MemoryReference>(),
                        LastModelTurn = null
                    },
                    LastModelResponse = null,
                    LastDecisionResult = null,
                    EventSequence = lastEvent.Sequence + 1,
                    EventChainHash = lastEvent.ContentHash,
                    PendingToolCommands = pendingCommands
                };
            }
            // PendingToolCommands 提取失败（增量与快照均无）→ 降级为 ContextBuilding 重新调用模型
        }

        // 本地状态规范化为 ContextBuilding（与 checkpoint 快路径语义一致）
        var resumedRun = state.Run with { State = AgentRunState.ContextBuilding };

        return state with
        {
            Run = resumedRun,
            Context = new AgentContextState
            {
                CurrentTask = state.Run.Task,
                Messages = new List<AgentMessage>(),
                ToolObservations = toolObservations,
                Conversation = conversation,
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            LastDecisionResult = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        };
    }
}
