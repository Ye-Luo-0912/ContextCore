using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-C：DefaultAgentKernel — .NET Agent Kernel 实现
//
// 目标（对齐 Workstream C 规格）：
//   1. 实现 IAgentKernel 的 RunAsync / SubmitAsync / GetStatus。
//   2. RunAsync 循环：从 inbox 读取指令 → 处理 → 通过 Transport 发送结果。
//   3. Execute 指令：调用 IToolDispatcher.DispatchAsync。
//   4. Checkpoint 指令：调用 IAgentCheckpointStore.SaveAsync。
//   5. Shutdown 指令：设置 Draining → 排空 inbox → 设置 Stopped。
//   6. bounded Channel（容量 256，Wait 模式）。
//   7. R28-C WP-A：BuildContext 指令 — 调用 IContextDecisionRuntime.ExecuteWithWorkingSetAsync
//      (Purpose=AgentContext) → IAgentContextProjector.Project → 产出 AgentContextSnapshot。
//
// 设计决策：
//   - Kernel 维护自身 inbox（Channel<AgentKernelInstruction>）；SubmitAsync 写入 inbox，
//     RunAsync 从 inbox 读取。Transport 主要用于发送结果（SendResultAsync）。
//   - Transport.ReceiveAsync 存在于接口中供自定义 Transport 推送远程指令，
//     但默认 Kernel 使用自身 inbox 作为输入源（简化单消费者模型，避免双源 select 复杂度）。
//   - 取消令牌传播：外部 cancellationToken 与内部 _shutdownCts 链接；
//     外部取消时抛 OperationCanceledException；Shutdown 指令时正常退出。
//   - R28-C WP-A：IContextDecisionRuntime + IAgentContextProjector 为可选注入（null 时
//     BuildContext 指令返回明确错误而非崩溃，保持与旧测试的向后兼容）。
// ===========================================================================

/// <summary>
/// R28-C：.NET Agent Kernel 实现。
/// </summary>
/// <remarks>
/// 编排 Transport → ToolDispatcher → CheckpointStore → IContextDecisionRuntime 四者。
/// 线程安全：SubmitAsync 可多线程并发调用；RunAsync 单消费者执行。
/// </remarks>
public sealed class DefaultAgentKernel : IAgentKernel
{
    private readonly IAgentKernelTransport _transport;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IAgentCheckpointStore _checkpointStore;
    private readonly IContextDecisionRuntime? _decisionRuntime;
    private readonly IAgentContextProjector? _contextProjector;
    private readonly KernelTransportOptions _transportOptions;
    private readonly IKernelResultOutbox? _resultOutbox;
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    private readonly IAgentContextSnapshotStore? _snapshotStore;
    private readonly IAgentRunEventStore? _eventStore;
    private readonly IToolDispatchJournal? _dispatchJournal;
    private readonly IInstructionReconciliation? _reconciliation;
    private readonly Channel<AgentKernelInstruction> _inbox;
    private readonly CancellationTokenSource _shutdownCts;
    private AgentKernelState _state;
    private int _processedCount;
    private DateTimeOffset? _lastProcessedAt;

    // R28-C WP-B：记录上次 BuildContext 产出的 snapshot，供 Checkpoint 指令关联
    private AgentContextSnapshot? _lastSnapshot;

    // R28-C WP-C：已提交的 tool 结果（按 RequestId 索引）。
    // SideEffect != Unknown 的结果自动提交；Unknown 需显式 Ack 后才提交。
    // 已提交的结果在 resume 时可安全重放（返回缓存结果不重新执行）。
    // R28-G P1-5：以 FIFO 容量上限淘汰最旧条目，避免长会话无界增长。
    private readonly Dictionary<string, ToolDispatchResult> _committedToolResults = new(StringComparer.Ordinal);

    // R28-G P1-5：committed result 的序号（与 _committedToolResults 并行维护）。
    // 用于 delta checkpoint：仅序列化 Sequence > _lastCheckpointSequence 的新增条目。
    private readonly Dictionary<string, long> _committedResultSequences = new(StringComparer.Ordinal);

    // R28-G P1-5：FIFO 插入顺序队列，用于在容量超限时淘汰最旧条目。
    private readonly Queue<string> _committedResultOrder = new();

    // R28-G P1-5：committed result 容量上限（默认 1024）。超过时按 FIFO 淘汰。
    private readonly int _maxCommittedResults = DefaultMaxCommittedResults;

    // R28-G P1-5：committed result 单调递增序号；每次新增条目 +1。
    private long _committedResultSequence;

    // R28-G P1-5：上次成功 checkpoint 覆盖的最大 Sequence（0 = 从未 checkpoint）。
    // 工厂据此决定 Delta（>0）或 Full（==0）模式。
    private long _lastCheckpointSequence;

    // R28-G P1-5：上次成功 checkpoint 的 ID（用于 delta 链 BaseCheckpointId 链接）。
    private string? _lastCheckpointId;

    // P0-5：上次成功 checkpoint 的 ContentHash（用于 delta 链 PrevChainHash 链接）。
    private string? _lastCheckpointContentHash;

    // P4：AgentRunEventStore 的最后事件序列号缓存（Cursor 模式专用）。
    // 由 AutoCheckpointAsync 在调用 CreateCheckpointAsync 前从 _eventStore.GetLastSequenceAsync 读取并缓存；
    // accessor 委托 getLastEventSequence 同步读取此缓存（委托无法直接 await 异步 API）。
    // null 表示未注入 EventStore 或尚未读取过 → 工厂退回 Delta/Full 模式。
    private int? _lastEventSequenceCache;

    // P4：turn/cost 预算计数器（Cursor 模式 round-trip 持久化）。
    // 当前 Kernel 不主动维护这些计数器（仍由 AgentRunActor/IAgentLoopPolicy 层负责预算追踪），
    // 但提供字段以便 Cursor 模式 checkpoint 携带预算状态供未来扩展使用。
    private int _turnsUsed;
    private int _tokensUsed;
    private double _costUsedUsd;

    // R28-E P1-3：pending 的 Unknown 副作用 tool 结果（按 RequestId 索引）。
    // 等待 AcknowledgeToolResult 移到 _committedToolResults 或 RejectToolResult 丢弃。
    private readonly Dictionary<string, ToolDispatchResult> _pendingToolResults = new(StringComparer.Ordinal);

    // R28-C WP-B：跟踪最后一次 session/workspace（用于取消时自动 checkpoint）
    // 和是否为 graceful shutdown（Shutdown 指令 vs 外部取消）
    private string _lastSessionId = "kernel-default-session";
    private string _lastWorkspaceId = "kernel-default-workspace";
    private bool _gracefulShutdown;

    /// <summary>
    /// R28-G P1-5：committed result 默认容量上限（1024 条）。
    /// 长会话下 _committedToolResults 不会无界增长；旧条目按 FIFO 淘汰。
    /// </summary>
    public const int DefaultMaxCommittedResults = 1024;

    /// <summary>
    /// P0-5：Checkpoint delta 链最大深度（32）。
    /// ResumeAsync 递归加载 base checkpoint 时限制深度，防止损坏/恶意链导致栈溢出。
    /// 超过此深度抛 InvalidOperationException。
    /// </summary>
    public const int MaxCheckpointChainDepth = 32;

    /// <summary>
    /// 构造默认 Agent Kernel。
    /// </summary>
    /// <param name="transport">Transport 抽象（用于发送结果）。</param>
    /// <param name="toolDispatcher">Tool 分派器。</param>
    /// <param name="checkpointStore">Agent 检查点存储。</param>
    /// <param name="decisionRuntime">
    /// R28-C WP-A：Context Decision Runtime（V2 I/O 入口）。null 时 BuildContext 指令返回明确错误。
    /// </param>
    /// <param name="contextProjector">
    /// R28-C WP-A：AgentContext 投影器（DecisionResult → AgentContextSnapshot）。null 时 BuildContext 返回错误。
    /// </param>
    /// <param name="transportOptions">
    /// R28-C WP-D：Transport 失败策略（FailFast / Retry / FallbackToDeterministic）。null 时使用 Default。
    /// </param>
    /// <param name="resultOutbox">
    /// R28-D P0-5：本地结果 outbox（FallbackToDeterministic 策略下持久化失败结果）。null 时不持久化。
    /// </param>
    /// <param name="checkpointFactory">
    /// R28-E P1-1：统一 checkpoint 工厂。null 时 Kernel 内部构建默认工厂。
    /// </param>
    /// <param name="snapshotStore">
    /// R28-E P1-2：snapshot store（ResumeAsync 据此恢复 _lastSnapshot）。null 时 Resume 不恢复 snapshot。
    /// </param>
    /// <param name="dispatchJournal">
    /// R28-E P1-4：tool 分派 journal（exactly-once 语义）。null 时退回进程内去重（不保证崩溃恢复）。
    /// </param>
    /// <param name="maxCommittedResults">
    /// R28-G P1-5：_committedToolResults 容量上限。超过时按 FIFO 淘汰最旧条目。null 或 &lt;= 0 时使用默认 1024。
    /// </param>
    /// <param name="eventStore">
    /// P4：Agent Run 事件流存储（Cursor 模式 checkpoint 的真相源）。null 时退回 Delta/Full 模式。
    /// 注入后 AutoCheckpointAsync 会读取最新事件 sequence 作为 cursor，工厂产出 Cursor 模式 checkpoint
    /// （不序列化 CommittedResults，ResumeAsync 时从事件流重建）。
    /// </param>
    public DefaultAgentKernel(
        IAgentKernelTransport transport,
        IToolDispatcher toolDispatcher,
        IAgentCheckpointStore checkpointStore,
        IContextDecisionRuntime? decisionRuntime = null,
        IAgentContextProjector? contextProjector = null,
        KernelTransportOptions? transportOptions = null,
        IKernelResultOutbox? resultOutbox = null,
        IAgentCheckpointFactory? checkpointFactory = null,
        IAgentContextSnapshotStore? snapshotStore = null,
        IToolDispatchJournal? dispatchJournal = null,
        int? maxCommittedResults = null,
        IAgentRunEventStore? eventStore = null,
        IInstructionReconciliation? reconciliation = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _decisionRuntime = decisionRuntime;
        _contextProjector = contextProjector;
        _transportOptions = transportOptions ?? KernelTransportOptions.Default;
        _resultOutbox = resultOutbox;
        _snapshotStore = snapshotStore;
        _eventStore = eventStore;
        _dispatchJournal = dispatchJournal;
        _reconciliation = reconciliation;
        _maxCommittedResults = maxCommittedResults is > 0 ? maxCommittedResults.Value : DefaultMaxCommittedResults;

        // R28-E P1-1：若未注入 checkpointFactory，使用默认实现（绑定到当前 Kernel 状态访问器）
        // R28-G P1-5：accessor 暴露 delta cursor + pending results，支持 delta checkpoint
        // P4：accessor 暴露 event cursor + 活跃 snapshot + 预算计数器，支持 cursor checkpoint
        if (checkpointFactory is not null)
        {
            _checkpointFactory = checkpointFactory;
        }
        else
        {
            var accessor = new DefaultAgentCheckpointFactory.KernelStateAccessor(
                getLastSnapshotId: () => _lastSnapshot?.SnapshotId,
                getCommittedResults: () => _committedToolResults,
                getCommittedResultSequences: () => _committedResultSequences,
                getPendingResults: () => _pendingToolResults,
                getLastCheckpointSequence: () => _lastCheckpointSequence,
                getLastCheckpointId: () => _lastCheckpointId,
                getLastCheckpointContentHash: () => _lastCheckpointContentHash,
                getLastEventSequence: () => _lastEventSequenceCache,
                getActiveSnapshotId: () => _lastSnapshot?.SnapshotId,
                getBudgetCounters: () => new DefaultAgentCheckpointFactory.BudgetCountersDto(_turnsUsed, _tokensUsed, _costUsedUsd));
            _checkpointFactory = new DefaultAgentCheckpointFactory(accessor);
        }

        // 容量 256，Wait 模式（满时 SubmitAsync 阻塞等待）
        _inbox = Channel.CreateBounded<AgentKernelInstruction>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _shutdownCts = new CancellationTokenSource();
        _state = AgentKernelState.Idle;
    }

    /// <inheritdoc />
    public async ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        if (_state == AgentKernelState.Running)
        {
            throw new InvalidOperationException("Kernel 已在运行；不可重复调用 RunAsync。");
        }

        // 链接外部取消令牌与内部 shutdown 令牌
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var ct = linkedCts.Token;

        _state = AgentKernelState.Running;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                AgentKernelInstruction instruction;
                // 从 inbox 读取指令（阻塞直到有指令或取消）
                try
                {
                    if (!await _inbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        // Channel 写入端完成（不应发生，但防御性处理）
                        break;
                    }
                    if (!_inbox.Reader.TryRead(out instruction!))
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    // 内部 shutdown 信号触发；排空 inbox 后退出
                    _state = AgentKernelState.Draining;
                    await DrainInboxAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                // R28-C WP-B：从指令 Metadata 更新 session/workspace 跟踪
                TrackSessionFromInstruction(instruction);

                // P0-6-1：Shutdown 指令也走统一完成协议——先 Ack Durable lease，再排空 inbox。
                // 旧实现直接排空返回，绕过了 Ack，导致 Shutdown 指令的 lease 过期后由 reaper 回滚重投递。
                if (instruction.Kind == AgentKernelInstructionKind.Shutdown)
                {
                    _gracefulShutdown = true;
                    _state = AgentKernelState.Draining;
                    // P0-6-1：Ack Shutdown 指令自身的 lease（若来自 Durable pump）。
                    await AckDurableLeaseIfPresentAsync(instruction, InstructionProcessingOutcome.Succeeded, null, ct).ConfigureAwait(false);
                    await DrainInboxAsync(cancellationToken).ConfigureAwait(false);
                    _state = AgentKernelState.Stopped;
                    return;
                }

                // 处理 Execute / Checkpoint / BuildContext 等指令。
                // P0-6-2：统一走 ProcessLeasedInstructionAsync——含续租、outcome 决策、Ack/Nack/DeadLetter。
                await ProcessLeasedInstructionAsync(instruction, ct).ConfigureAwait(false);

                Interlocked.Increment(ref _processedCount);
                _lastProcessedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _state = AgentKernelState.Stopped;

            // R28-C WP-B：非 graceful shutdown（外部取消或异常）时自动产出可恢复 checkpoint。
            // 注意：必须使用 CancellationToken.None——传入已被取消的 cancellationToken 会导致
            // store 的 ThrowIfCancellationRequested 抛出，checkpoint 永远无法持久化（恢复机制失效）。
            // AutoCheckpoint 是清理路径，必须完成即使外部取消已触发。
            if (!_gracefulShutdown)
            {
                try
                {
                    await AutoCheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 自动 checkpoint 失败不应掩盖原始取消/异常；静默忽略
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask SubmitAsync(AgentKernelInstruction instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        if (_state == AgentKernelState.Stopped)
        {
            throw new InvalidOperationException("Kernel 已停止；无法接受新指令。");
        }
        return _inbox.Writer.WriteAsync(instruction, cancellationToken);
    }

    /// <inheritdoc />
    public AgentKernelStatus GetStatus()
    {
        return new AgentKernelStatus
        {
            State = _state,
            ProcessedCount = _processedCount,
            PendingCount = _inbox.Reader.Count,
            LastProcessedAt = _lastProcessedAt
        };
    }

    /// <summary>排空 inbox 中剩余指令（Shutdown 后调用），处理并发出结果。</summary>
    /// <remarks>
    /// P0-6-2：统一走 <see cref="ProcessLeasedInstructionAsync"/>，保证 Durable lease 的 Ack/Nack
    /// 在 drain 路径也生效。旧实现直接调用 ProcessInstructionAsync + SendResultWithPolicyAsync，
    /// 漏掉了 Ack/Nack，导致 drain 期间处理的 Durable 指令 lease 过期后被 reaper 回滚重投递。
    /// </remarks>
    private async ValueTask DrainInboxAsync(CancellationToken cancellationToken)
    {
        while (_inbox.Reader.TryRead(out var instruction))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 跳过重复 Shutdown 指令（首个 Shutdown 已在 RunAsync 处理）
            if (instruction.Kind == AgentKernelInstructionKind.Shutdown)
            {
                continue;
            }

            try
            {
                // P0-6-2：调用统一方法，含续租、outcome 决策、Ack/Nack/DeadLetter。
                await ProcessLeasedInstructionAsync(instruction, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
                _lastProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException)
            {
                // 排空期间取消；剩余指令的 lease 由 reaper 回滚为 Pending。
                break;
            }
            catch
            {
                // P0-6-2：处理/发送失败已由 ProcessLeasedInstructionAsync 内部 Nack；
                // 此处不中断 drain，继续处理下一条（FailFast 策略下 SendResult 异常会向上传播跳出循环）。
                // 注意：FailFast 策略下 SendResultWithPolicyAsync 抛异常会终止 drain（与旧行为一致）。
                throw;
            }
        }
    }

    /// <summary>处理单条指令（Execute / Checkpoint / BuildContext / Acknowledge / Reject / Query / Shutdown）。
    /// 返回结果与 outcome 分类；outcome 决定外层对 Durable lease 的 Ack/Nack/DeadLetter 行为。</summary>
    /// <remarks>
    /// P0-6-3：不再吞掉异常。业务错误（tool 不支持、参数缺失等）由各 Process*Async 返回
    /// <c>Succeeded=false</c> 结果，此处标记 <see cref="InstructionProcessingOutcome.BusinessRejected"/>，
    /// 外层 Ack 不重试。基础设施故障（DB/transport 临时不可用）由 Process*Async 抛异常，
    /// 由外层 <see cref="ProcessLeasedInstructionAsync"/> 捕获并标记
    /// <see cref="InstructionProcessingOutcome.TransientInfrastructure"/> 后 Nack 重试。
    /// <see cref="OperationCanceledException"/> 透传给外层（取消语义，不视为故障）。
    /// </remarks>
    private async ValueTask<(AgentKernelResult Result, InstructionProcessingOutcome Outcome)> ProcessInstructionAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        AgentKernelResult result = instruction.Kind switch
        {
            AgentKernelInstructionKind.Execute => await ProcessExecuteAsync(instruction, cancellationToken).ConfigureAwait(false),
            AgentKernelInstructionKind.Checkpoint => await ProcessCheckpointAsync(instruction, cancellationToken).ConfigureAwait(false),
            AgentKernelInstructionKind.BuildContext => await ProcessBuildContextAsync(instruction, cancellationToken).ConfigureAwait(false),
            AgentKernelInstructionKind.AcknowledgeToolResult => await ProcessAcknowledgeToolResultAsync(instruction, cancellationToken).ConfigureAwait(false),
            AgentKernelInstructionKind.RejectToolResult => await ProcessRejectToolResultAsync(instruction, cancellationToken).ConfigureAwait(false),
            AgentKernelInstructionKind.QueryToolDispatchState => await ProcessQueryToolDispatchStateAsync(instruction, cancellationToken).ConfigureAwait(false),
            AgentKernelInstructionKind.Shutdown => new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = true,
                Output = "shutdown"
            },
            _ => new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = $"未知指令类型: {instruction.Kind}"
            }
        };

        // 业务错误（Succeeded=false）已确定性地产出失败结果，Ack 不重试；
        // 成功（Succeeded=true）正常 Ack。
        // 抛异常的路径不会走到这里，由外层 ProcessLeasedInstructionAsync 捕获并标记 TransientInfrastructure。
        var outcome = result.Succeeded
            ? InstructionProcessingOutcome.Succeeded
            : InstructionProcessingOutcome.BusinessRejected;
        return (result, outcome);
    }

    /// <summary>
    /// P0-6-2/5/6：统一处理一条（可能来自 Durable Transport 的）指令，覆盖续租、outcome 决策、Ack/Nack/DeadLetter。
    /// 正常循环、Shutdown drain、取消 drain 共用此方法，保证 Durable lease 生命周期一致。
    /// </summary>
    /// <remarks>
    /// <b>不变量</b>（P0-6-4）：Input can be Acked IFF Result is durably persisted OR durably delivered。
    /// 因此 <see cref="SendResultWithPolicyAsync"/> 失败时必须抛异常阻止 Ack（由本方法捕获后 Nack）。
    ///
    /// <b>Outcome 决策</b>（P0-6-3）：
    /// <list type="bullet">
    ///   <item><see cref="InstructionProcessingOutcome.Succeeded"/>/<see cref="InstructionProcessingOutcome.BusinessRejected"/> → SendResult → Ack。</item>
    ///   <item><see cref="InstructionProcessingOutcome.PermanentFault"/> → SendResult（标记 PermanentFault）→ Ack（死信对账）。</item>
    ///   <item><see cref="InstructionProcessingOutcome.TransientInfrastructure"/> → Nack + 抛异常（让外层终止/重试）。</item>
    /// </list>
    ///
    /// <b>续租</b>（P0-6-5）：若指令含 Durable lease token，处理期间启动后台 Task 按
    /// <see cref="KernelTransportOptions.DurableLeaseRenewalInterval"/> 续租；处理完成或异常后取消续租。
    /// 超过 <see cref="KernelTransportOptions.DurableMaxProcessingTime"/> 未完成则视为 PermanentFault。
    /// </remarks>
    private async ValueTask ProcessLeasedInstructionAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        var durable = _transport as IDurableTransport;
        string? leaseToken = null;
        var hasLease = durable is not null
            && instruction.Metadata.TryGetValue(DurableTransportMetadataKeys.LeaseToken, out leaseToken)
            && !string.IsNullOrEmpty(leaseToken);

        // P0-6-5：续租 Task（仅 Durable lease 启动）。处理完成后通过 cts 取消。
        var renewalCts = hasLease ? new CancellationTokenSource() : null;
        var renewalTask = hasLease
            ? Task.Run(() => RenewLeaseLoopAsync(
                durable!, instruction.InstructionId, leaseToken!, renewalCts!.Token, cancellationToken),
                CancellationToken.None)
            : null;

        // P0-6-5：MaxProcessingTime 超时（仅 Durable lease 启用；非 Durable 无 lease 概念）。
        var maxProcessing = hasLease ? _transportOptions.DurableMaxProcessingTime : Timeout.InfiniteTimeSpan;
        using var timeoutCts = maxProcessing == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(maxProcessing);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var processingCt = linkedCts.Token;

        AgentKernelResult result;
        InstructionProcessingOutcome outcome;

        try
        {
            try
            {
                (result, outcome) = await ProcessInstructionAsync(instruction, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 外部取消：不视为故障，Nack 让 lease 回滚供恢复时重试。
                await NackDurableLeaseIfPresentAsync(instruction, new OperationCanceledException("外部取消"), cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // P0-6-5：处理超时 → PermanentFault（不再续租，Ack 进入死信对账）。
                // P0-6-6：在 result.Metadata 标记 PermanentFault 供下游 reconciliation。
                result = new AgentKernelResult
                {
                    InstructionId = instruction.InstructionId,
                    Succeeded = false,
                    Error = $"指令处理超时（超过 {_transportOptions.DurableMaxProcessingTime.TotalSeconds:F0}s）",
                    Metadata = WithDurableStatus(
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        DurableDeliveryStatus.PermanentFault,
                        "处理阶段超时")
                };
                outcome = InstructionProcessingOutcome.PermanentFault;
            }
            catch (Exception ex)
            {
                // P0-6-3：基础设施临时故障 → Nack 让 lease 回滚为 Pending 供重试。
                await NackDurableLeaseIfPresentAsync(instruction, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }

            // P0-6-4：发送结果。SendResultWithPolicyAsync 失败时抛异常阻止 Ack（不变量：Ack IFF Result persisted/delivered）。
            try
            {
                await SendResultWithPolicyAsync(result, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await NackDurableLeaseIfPresentAsync(instruction, new OperationCanceledException("发送时外部取消"), cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // 发送阶段超时也视为 PermanentFault，Ack 进入死信（结果可能已持久化到 outbox）。
                outcome = InstructionProcessingOutcome.PermanentFault;
                result = result with
                {
                    Metadata = WithDurableStatus(result.Metadata, DurableDeliveryStatus.PermanentFault, "发送阶段超时")
                };
            }
            catch (Exception ex)
            {
                // 发送失败：Nack 让 lease 回滚（结果可能已写入 outbox，重试时会重新处理）。
                await NackDurableLeaseIfPresentAsync(instruction, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }

            // P0-6-3：根据 outcome 决定 Ack 行为。
            // TransientInfrastructure 不应走到这里（异常路径已 return/throw），防御性 Nack。
            if (outcome == InstructionProcessingOutcome.TransientInfrastructure)
            {
                await NackDurableLeaseIfPresentAsync(instruction, new InvalidOperationException("TransientInfrastructure outcome 不应到达 Ack"), cancellationToken).ConfigureAwait(false);
                return;
            }

            // P0-6-6：Ack，并在结果 Metadata 标记 DurableDeliveryStatus。
            await AckDurableLeaseIfPresentAsync(instruction, outcome, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 停止续租 Task
            if (renewalCts is not null)
            {
                renewalCts.Cancel();
                if (renewalTask is not null)
                {
                    try { await renewalTask.ConfigureAwait(false); }
                    catch { /* 续租 Task 异常不掩盖主流程 */ }
                }
                renewalCts.Dispose();
            }
        }
    }

    /// <summary>
    /// P0-6-5：后台续租循环。按 <see cref="KernelTransportOptions.DurableLeaseRenewalInterval"/> 周期性续租。
    /// 主流程取消（renewalCts）或外部取消（externalCt）时退出。
    /// </summary>
    private async Task RenewLeaseLoopAsync(
        IDurableTransport durable,
        string instructionId,
        string leaseToken,
        CancellationToken renewalCt,
        CancellationToken externalCt)
    {
        var interval = _transportOptions.DurableLeaseRenewalInterval;
        if (interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan)
        {
            return; // 禁用续租
        }

        // 续租 extension = InstructionLeaseDuration（与 pump 租约时长一致，保持 lease 不过期）。
        // 从 KernelTransportOptions 无法直接读取 InstructionLeaseDuration（在 DurableTransportHostingOptions），
        // 此处用 DurableMaxProcessingTime 作为续租时长上限的安全余量（续租到 MaxProcessingTime 仍有余量）。
        var extension = _transportOptions.DurableMaxProcessingTime;

        try
        {
            while (!renewalCt.IsCancellationRequested && !externalCt.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, renewalCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await durable.RenewLeaseAsync(instructionId, leaseToken, extension, externalCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // 续租失败（token 不匹配/已过期）不中断主流程；lease 过期后由 reaper 回滚，dedup 保证不重复执行。
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径
        }
    }

    /// <summary>P0-6-6：在结果 Metadata 中追加 DurableDeliveryStatus 键值（返回新字典，不修改原字典）。</summary>
    private static IReadOnlyDictionary<string, string> WithDurableStatus(
        IReadOnlyDictionary<string, string> metadata,
        DurableDeliveryStatus status,
        string? diagnostic)
    {
        var dict = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
        {
            [DurableDeliveryStatusKeys.DurableDeliveryStatus] = ((byte)status).ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(diagnostic))
        {
            dict[DurableDeliveryStatusKeys.AckFailureDiagnostic] = diagnostic!;
        }
        return dict;
    }

    /// <summary>处理 Execute 指令：通过 IToolDispatcher 分派 tool。</summary>
    /// <remarks>
    /// R28-C WP-C：tool 结果按 RequestId 去重——已提交的结果不重新执行（幂等）。
    /// 副作用分类决定是否自动提交：
    ///   - None / ReadOnly / Write：自动提交到 _committedToolResults
    ///   - Unknown：不自动提交，存入 _pendingToolResults，等待 Acknowledge/Reject
    ///
    /// R28-E P1-4：若注入了 <see cref="IToolDispatchJournal"/>，按状态机推进：
    ///   Prepared（调用前）→ Dispatched（tool 返回后）→ Committed（结果提交后）→ ResultDelivered（发送后）
    /// </remarks>
    private async ValueTask<AgentKernelResult> ProcessExecuteAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        // tool 名称从 Metadata["tool"] 读取，缺省为 "echo"
        var toolName = instruction.Metadata.TryGetValue("tool", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : "echo";
        var payload = instruction.Payload ?? string.Empty;

        // 检查 tool 是否受支持
        if (!_toolDispatcher.SupportedTools.Contains(toolName))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = $"不支持的 tool: {toolName}"
            };
        }

        // R28-C WP-C：幂等去重——已提交的结果直接返回缓存，不重新执行
        if (_committedToolResults.TryGetValue(instruction.InstructionId, out var cached))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = cached.Succeeded,
                Output = cached.Result,
                Error = cached.Error
            };
        }

        // R28-E P1-4：调用 tool 前写入 Prepared journal 条目（若注入了 journal）
        var idempotencyKey = instruction.Metadata.TryGetValue("idempotencyKey", out var ik) && !string.IsNullOrWhiteSpace(ik)
            ? ik
            : null;

        if (_dispatchJournal is not null)
        {
            // P0-3 CAS-2：携带 payload 摘要与 workspace/run 作用域，供 PrepareAsync 语义等价校验。
            var runId = instruction.Metadata.TryGetValue("runId", out var rid) && !string.IsNullOrWhiteSpace(rid)
                ? rid
                : null;
            await _dispatchJournal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = instruction.InstructionId,
                ToolName = toolName,
                State = ToolDispatchState.Prepared,
                IdempotencyKey = idempotencyKey,
                UpdatedAt = DateTimeOffset.UtcNow,
                PayloadDigest = ToolDispatchJournalEntry.ComputePayloadDigest(payload),
                WorkspaceId = _lastWorkspaceId,
                RunId = runId
            }, cancellationToken).ConfigureAwait(false);
        }

        var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = toolName,
            Payload = payload,
            RequestId = instruction.InstructionId
        }, cancellationToken).ConfigureAwait(false);

        // R28-E P1-4：tool 返回后标记 Dispatched（若注入了 journal）
        if (_dispatchJournal is not null)
        {
            await _dispatchJournal.MarkDispatchedAsync(instruction.InstructionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // R28-C WP-C：副作用分类决定是否自动提交
        // Unknown 不自动提交——存入 pending，等待 Acknowledge/Reject
        if (dispatchResult.SideEffect != ToolSideEffect.Unknown)
        {
            // R28-G P1-5：通过 helper 维护 FIFO 顺序 + 容量淘汰 + 序号分配
            AddCommittedResult(instruction.InstructionId, dispatchResult);

            // R28-E P1-4：标记 Committed（若注入了 journal）
            if (_dispatchJournal is not null)
            {
                await _dispatchJournal.MarkCommittedAsync(instruction.InstructionId, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // R28-E P1-3：Unknown 副作用结果存入 pending，等待显式 Ack/Reject
            _pendingToolResults[instruction.InstructionId] = dispatchResult;
        }

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = dispatchResult.Succeeded,
            Output = dispatchResult.Result,
            Error = dispatchResult.Error
        };
    }

    /// <summary>处理 Checkpoint 指令：通过 IAgentCheckpointFactory + IAgentCheckpointStore 保存检查点。</summary>
    /// <remarks>
    /// R28-E P1-1：统一使用 <see cref="IAgentCheckpointFactory"/> 构建 checkpoint，
    /// 手动 Checkpoint 与自动 AutoCheckpoint 产出相同 KernelCheckpointState 格式。
    /// instruction.Payload 不再直接作为 StateJson（仅作为元数据可选保留在 checkpoint.Metadata）。
    /// </remarks>
    private async ValueTask<AgentKernelResult> ProcessCheckpointAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        // 从 Metadata 提取 session / workspace 信息
        var sessionId = instruction.Metadata.TryGetValue("sessionId", out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : "kernel-default-session";
        var workspaceId = instruction.Metadata.TryGetValue("workspaceId", out var w) && !string.IsNullOrWhiteSpace(w)
            ? w
            : "kernel-default-workspace";

        // R28-E P1-1：通过 factory 统一构建 checkpoint（序列化完整 Kernel 状态）
        var checkpoint = await _checkpointFactory!.CreateCheckpointAsync(
            instruction.InstructionId, sessionId, workspaceId, cancellationToken).ConfigureAwait(false);

        // 可选：将手动 Payload 作为诊断信息保留到 Metadata
        if (!string.IsNullOrEmpty(instruction.Payload))
        {
            checkpoint = checkpoint with
            {
                Metadata = new Dictionary<string, string>(checkpoint.Metadata, StringComparer.Ordinal)
                {
                    ["manualPayload"] = instruction.Payload
                }
            };
        }

        await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        // R28-G P1-5：推进 cursor，下次 Checkpoint 指令走 Delta 路径
        AdvanceCheckpointCursor(checkpoint);

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = true,
            Output = checkpoint.CheckpointId,
            LastSnapshotId = _lastSnapshot?.SnapshotId
        };
    }

    /// <summary>
    /// R28-C WP-A：处理 BuildContext 指令 — 调用 IContextDecisionRuntime（V2 路径）
    /// 构建 AgentContextSnapshot。
    /// </summary>
    /// <remarks>
    /// Metadata 约定：
    ///   - workspaceId（必填）：作用域 workspace
    ///   - collectionId（可选）：作用域 collection，缺省 = workspaceId
    ///   - sessionId（必填）：Agent session 标识
    ///   - queryText（可选）：查询文本
    ///   - tokenBudget（可选）：token 预算上限（0 = 由 policy 决定）
    ///   - requiredIds（可选）：mandatory recall ID 列表（逗号分隔）
    /// </remarks>
    private async ValueTask<AgentKernelResult> ProcessBuildContextAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (_decisionRuntime is null)
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "IContextDecisionRuntime 未注入；无法处理 BuildContext 指令。"
            };
        }
        if (_contextProjector is null)
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "IAgentContextProjector 未注入；无法处理 BuildContext 指令。"
            };
        }

        var meta = instruction.Metadata;
        if (!meta.TryGetValue("workspaceId", out var workspaceId) || string.IsNullOrWhiteSpace(workspaceId))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "BuildContext Metadata 缺少必填字段 workspaceId。"
            };
        }
        if (!meta.TryGetValue("sessionId", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "BuildContext Metadata 缺少必填字段 sessionId。"
            };
        }

        var collectionId = meta.TryGetValue("collectionId", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c
            : workspaceId;
        var queryText = meta.TryGetValue("queryText", out var q) && !string.IsNullOrWhiteSpace(q)
            ? q
            : null;
        var tokenBudget = meta.TryGetValue("tokenBudget", out var tb)
            && int.TryParse(tb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget)
            ? budget
            : 0;

        // 可选 mandatory recall IDs（逗号分隔）
        IReadOnlyList<string> requiredIds = meta.TryGetValue("requiredIds", out var rid) && !string.IsNullOrWhiteSpace(rid)
            ? rid.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        var scope = new ContextDecisionScope(workspaceId, collectionId);
        var agentSession = new AgentSessionId
        {
            Value = sessionId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = instruction.InstructionId,
            Scope = scope,
            Purpose = ContextDecisionPurpose.AgentContext,
            QueryText = queryText,
            TokenBudget = tokenBudget,
            TopK = 0,
            AgentInput = new AgentInput
            {
                Session = agentSession,
                RequiredIds = requiredIds
            }
        };

        var execution = await _decisionRuntime.ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);

        var projectionContext = new ProjectionContext
        {
            AgentSession = agentSession,
            WorkspaceId = workspaceId,
            CollectionId = collectionId
        };

        var snapshot = _contextProjector.Project(execution, projectionContext);

        // 记录上次 snapshot，供后续 Checkpoint 指令关联
        _lastSnapshot = snapshot;

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = true,
            Output = snapshot.SnapshotId,
            Snapshot = snapshot
        };
    }

    // =======================================================================
    // R28-E P1-3：AcknowledgeToolResult / RejectToolResult / QueryToolDispatchState
    // =======================================================================

    /// <summary>
    /// R28-E P1-3：处理 AcknowledgeToolResult 指令。
    /// 将 pending 的 Unknown 副作用结果移到 committed，恢复时可安全重放。
    /// </summary>
    private async ValueTask<AgentKernelResult> ProcessAcknowledgeToolResultAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (!instruction.Metadata.TryGetValue("requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "AcknowledgeToolResult Metadata 缺少必填字段 requestId。"
            };
        }

        if (!_pendingToolResults.TryGetValue(requestId, out var pending))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = $"未找到 pending 的 tool 结果: {requestId}（可能已 ack 或不存在）。",
                AffectedRequestId = requestId
            };
        }

        // 移到 committed（R28-G P1-5：通过 helper 维护 FIFO + 序号）
        _pendingToolResults.Remove(requestId);
        AddCommittedResult(requestId, pending);

        // R28-E P1-4：标记 Committed（若注入了 journal）
        if (_dispatchJournal is not null)
        {
            await _dispatchJournal.MarkCommittedAsync(requestId, cancellationToken).ConfigureAwait(false);
        }

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = true,
            Output = $"acknowledged: {requestId}",
            AffectedRequestId = requestId
        };
    }

    /// <summary>
    /// R28-E P1-3：处理 RejectToolResult 指令。
    /// 将 pending 的 Unknown 副作用结果丢弃（不提交，不重放）。
    /// </summary>
    private ValueTask<AgentKernelResult> ProcessRejectToolResultAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (!instruction.Metadata.TryGetValue("requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            return ValueTask.FromResult(new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "RejectToolResult Metadata 缺少必填字段 requestId。"
            });
        }

        if (!_pendingToolResults.Remove(requestId))
        {
            return ValueTask.FromResult(new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = $"未找到 pending 的 tool 结果: {requestId}（可能已 reject 或不存在）。",
                AffectedRequestId = requestId
            });
        }

        // rejected 结果不写入 committed，恢复时不重放
        var reason = instruction.Metadata.TryGetValue("reason", out var r) && !string.IsNullOrWhiteSpace(r)
            ? r
            : "rejected-by-caller";

        return ValueTask.FromResult(new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = true,
            Output = $"rejected: {requestId} ({reason})",
            AffectedRequestId = requestId
        });
    }

    /// <summary>
    /// R28-E P1-3：处理 QueryToolDispatchState 指令。
    /// 返回指定 RequestId 的当前分派状态。
    /// </summary>
    private async ValueTask<AgentKernelResult> ProcessQueryToolDispatchStateAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        if (!instruction.Metadata.TryGetValue("requestId", out var requestId) || string.IsNullOrWhiteSpace(requestId))
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = "QueryToolDispatchState Metadata 缺少必填字段 requestId。"
            };
        }

        ToolDispatchState state;
        string diagnostic;

        // 优先查询 journal（持久化状态）；无 journal 时回退到进程内字典推断
        if (_dispatchJournal is not null)
        {
            var entry = await _dispatchJournal.GetEntryAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                state = ToolDispatchState.Prepared;
                diagnostic = "no journal entry: tool 从未被调用";
            }
            else
            {
                state = entry.State;
                diagnostic = entry.DiagnosticNote ?? $"journal state: {entry.State}";
            }
        }
        else if (_committedToolResults.ContainsKey(requestId))
        {
            state = ToolDispatchState.Committed;
            diagnostic = "in committed results (no journal)";
        }
        else if (_pendingToolResults.ContainsKey(requestId))
        {
            state = ToolDispatchState.Dispatched;
            diagnostic = "in pending results (Unknown side effect, awaiting ack)";
        }
        else
        {
            state = ToolDispatchState.Prepared;
            diagnostic = "not found in any store";
        }

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = true,
            Output = $"{state} ({diagnostic})",
            AffectedRequestId = requestId,
            DispatchState = state
        };
    }

    // =======================================================================
    // R28-C WP-D：Transport 失败策略
    // =======================================================================

    /// <summary>
    /// R28-C WP-D：按 <see cref="_transportOptions"/> 策略发送结果到 Transport。
    /// </summary>
    /// <remarks>
    /// 策略语义：
    ///   - <see cref="TransportFailurePolicy.FailFast"/>：直接发送；失败即抛出，Kernel 循环终止。
    ///   - <see cref="TransportFailurePolicy.Retry"/>：按 MaxRetries + RetryDelay 重试；
    ///     全部失败后抛 <see cref="InvalidOperationException"/>（含最后一次异常作为 InnerException）。
    ///   - <see cref="TransportFailurePolicy.FallbackToDeterministic"/>：发送失败时静默降级——
    ///     不中断 Kernel 循环，结果被本地丢弃，循环继续处理后续指令（transport 恢复后可正常发送）。
    ///
    /// 所有策略均透传 <see cref="OperationCanceledException"/>（取消不被重试/降级掩盖）。
    /// </remarks>
    /// <param name="result">要发送的结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask SendResultWithPolicyAsync(AgentKernelResult result, CancellationToken cancellationToken)
    {
        var sent = false;
        try
        {
            switch (_transportOptions.FailurePolicy)
            {
                case TransportFailurePolicy.FailFast:
                    await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
                    sent = true;
                    return;

                case TransportFailurePolicy.Retry:
                {
                    Exception? lastEx = null;
                    // 总尝试次数 = MaxRetries + 1（1 次初始 + MaxRetries 次重试）
                    for (var attempt = 0; attempt <= _transportOptions.MaxRetries; attempt++)
                    {
                        try
                        {
                            await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
                            sent = true;
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            if (attempt < _transportOptions.MaxRetries)
                            {
                                await Task.Delay(_transportOptions.RetryDelay, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    throw new InvalidOperationException(
                        $"Transport SendResultAsync 在 { _transportOptions.MaxRetries + 1 } 次尝试后仍失败（策略: Retry）。",
                        lastEx);
                }

                case TransportFailurePolicy.FallbackToDeterministic:
                    try
                    {
                        await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
                        sent = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception transportEx)
                    {
                        // P0-6-4：Transport 不可用 → 尝试写入 outbox 持久化（若注入）。
                        // 不变量：Input can be Acked IFF Result is durably persisted OR durably delivered。
                        // Outbox 写入失败必须抛异常阻止 Ack（结果未持久化也未投递，必须 Nack 重试）。
                        if (_resultOutbox is not null && _transportOptions.EnableResultOutbox)
                        {
                            try
                            {
                                await _resultOutbox.EnqueueAsync(result, cancellationToken).ConfigureAwait(false);
                                // Outbox 写入成功：结果已持久化，可 Ack（待 outbox 重放时真正发送）。
                                // sent 保持 false，MarkResultDelivered 不调用（结果未真正送达 transport）。
                            }
                            catch (Exception outboxEx)
                            {
                                // P0-6-4：Transport + Outbox 都失败 → 抛异常阻止 Ack，指令 Nack 重试。
                                throw new InvalidOperationException(
                                    "Transport SendResultAsync 失败且 Outbox 写入失败；结果未持久化也未投递，阻止 Ack 以触发 Nack 重试。",
                                    new AggregateException(transportEx, outboxEx));
                            }
                        }
                        else
                        {
                            // P0-6-4：未注入 outbox 或禁用 outbox → Transport 失败即结果丢失，抛异常阻止 Ack。
                            throw new InvalidOperationException(
                                "Transport SendResultAsync 失败且未配置 outbox；结果未持久化也未投递，阻止 Ack 以触发 Nack 重试。",
                                transportEx);
                        }
                    }
                    return;

                default:
                    // 未知策略值：保守按 FailFast 处理
                    await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
                    sent = true;
                    return;
            }
        }
        finally
        {
            // R28-E P1-4：发送成功后标记 ResultDelivered（若注入了 journal 且 result 对应已提交的 Execute 指令）。
            // 仅当 result.InstructionId 对应已提交 tool 结果时才推进 journal；
            // 否则跳过（如 Checkpoint/BuildContext/Ack/Reject/Query 指令，或 Unknown 副作用 pending 结果）。
            // 这样保证状态机顺序：Prepared → Dispatched → Committed → ResultDelivered。
            if (sent && _dispatchJournal is not null && _committedToolResults.ContainsKey(result.InstructionId))
            {
                try
                {
                    await _dispatchJournal.MarkResultDeliveredAsync(result.InstructionId, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // journal 写入失败不影响已发送的结果；best-effort
                }
            }
        }
    }

    // =======================================================================
    // P0-4：Durable Transport lease 确认
    // =======================================================================

    /// <summary>
    /// P0-4/P0-6-6：若指令来自 Durable Transport pump（Metadata 含 lease token），在处理 + 发送成功后调用 AckAsync 确认。
    /// Ack 失败（token 不匹配/已过期回滚/已确认）不抛异常——best-effort，过期行由 reaper 回滚后 pump 重新租约。
    /// </summary>
    /// <remarks>
    /// P0-6-6：返回 <see cref="DurableDeliveryStatus"/> 供调用方记录诊断。Ack 失败时根据异常类型区分：
    /// <list type="bullet">
    ///   <item><see cref="InvalidOperationException"/>（token 不匹配/已过期回滚）→ <see cref="DurableDeliveryStatus.LeaseExpiredBeforeAck"/>。</item>
    ///   <item>其他异常 → <see cref="DurableDeliveryStatus.AckFailed"/>。</item>
    /// </list>
    /// 若注入了 <see cref="IInstructionReconciliation"/>，调用 <see cref="IInstructionReconciliation.ReconcileAsync"/>
    /// 让 Journal/Result Store 判断应返回缓存结果、继续恢复还是进入人工处理。
    /// </remarks>
    private async ValueTask<DurableDeliveryStatus> AckDurableLeaseIfPresentAsync(
        AgentKernelInstruction instruction,
        InstructionProcessingOutcome outcome,
        AgentKernelResult? result,
        CancellationToken cancellationToken)
    {
        // P0-6-6：PermanentFault outcome 优先返回（无论 Ack 成功/失败，outcome 已判定为永久故障）。
        if (outcome == InstructionProcessingOutcome.PermanentFault)
        {
            // 仍尝试 Ack 删除指令（避免 lease 过期重投递）；Ack 失败也不影响 PermanentFault 判定。
            if (_transport is IDurableTransport durable
                && instruction.Metadata.TryGetValue(DurableTransportMetadataKeys.LeaseToken, out var pToken)
                && !string.IsNullOrEmpty(pToken))
            {
                try
                {
                    await durable.AckAsync(instruction.InstructionId, pToken, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // PermanentFault 下 Ack 失败由 reaper 兜底回滚；status 仍为 PermanentFault。
                }
            }

            await InvokeReconciliationAsync(instruction.InstructionId, result?.Metadata?.GetValueOrDefault(DurableTransportMetadataKeys.LeaseToken), DurableDeliveryStatus.PermanentFault, cancellationToken).ConfigureAwait(false);
            return DurableDeliveryStatus.PermanentFault;
        }

        if (_transport is not IDurableTransport durable2) return DurableDeliveryStatus.NotDurable;
        if (!instruction.Metadata.TryGetValue(DurableTransportMetadataKeys.LeaseToken, out var token) || string.IsNullOrEmpty(token))
        {
            return DurableDeliveryStatus.NotDurable;
        }

        DurableDeliveryStatus status;
        try
        {
            await durable2.AckAsync(instruction.InstructionId, token, cancellationToken).ConfigureAwait(false);
            status = DurableDeliveryStatus.Acked;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // token 不匹配/已过期回滚/已确认 → lease 已过期，pump 会重新租约 + Submit，dedup 保证不重复执行。
            status = DurableDeliveryStatus.LeaseExpiredBeforeAck;
        }
        catch (Exception)
        {
            // 其他 Ack 失败（如 DB 临时不可用）→ AckFailed，lease 过期后由 reaper 回滚。
            status = DurableDeliveryStatus.AckFailed;
        }

        // P0-6-6：调用 reconciliation（若注入），让下游判断重复投递/恢复/人工介入。
        await InvokeReconciliationAsync(instruction.InstructionId, token, status, cancellationToken).ConfigureAwait(false);
        return status;
    }

    /// <summary>P0-6-6：调用 IInstructionReconciliation（若注入），best-effort 不抛异常。</summary>
    private async ValueTask InvokeReconciliationAsync(
        string instructionId,
        string? leaseToken,
        DurableDeliveryStatus status,
        CancellationToken cancellationToken)
    {
        if (_reconciliation is null) return;
        try
        {
            await _reconciliation.ReconcileAsync(instructionId, leaseToken, status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // reconciliation 失败不影响主流程；下游可从 result.Metadata 读取 status。
        }
    }

    /// <summary>
    /// P0-4：若指令来自 Durable Transport pump，处理或发送失败时调用 NackAsync 回滚为 Pending。
    /// 让 pump 能重新租约该指令（幂等性由 dedup / journal 保证）。
    /// Nack 失败不掩盖原始异常——best-effort，过期行由 reaper 回滚。
    /// </summary>
    private async ValueTask NackDurableLeaseIfPresentAsync(AgentKernelInstruction instruction, Exception processingException, CancellationToken cancellationToken)
    {
        if (_transport is not IDurableTransport durable) return;
        if (!instruction.Metadata.TryGetValue(DurableTransportMetadataKeys.LeaseToken, out var token) || string.IsNullOrEmpty(token)) return;

        try
        {
            await durable.NackAsync(instruction.InstructionId, token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // best-effort：Nack 失败（租约已过期/已被接管）不掩盖原始处理异常。
            // 过期 Leased 行由 reaper 回滚为 Pending，pump 重新租约。
        }
    }

    // =======================================================================
    // R28-C WP-B：Checkpoint 恢复 + 自动 Checkpoint
    // =======================================================================

    /// <summary>
    /// R28-C WP-B：从 checkpoint 恢复 Kernel 状态。
    /// 反序列化已提交的 tool 结果 + snapshot 引用，恢复后 RunAsync 可继续处理。
    /// </summary>
    /// <remarks>
    /// R28-E P1-1：反序列化使用 <see cref="DefaultAgentCheckpointFactory.KernelCheckpointStateDto"/>，
    /// 与 factory 产出的格式一致（确保手动/自动 checkpoint 均可恢复）。
    /// R28-E P1-2：若注入了 <see cref="IAgentContextSnapshotStore"/>，根据 SnapshotId 加载 snapshot。
    /// </remarks>
    public async ValueTask ResumeAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // R28-G P1-5：清空 in-memory 状态（resume 总是从 checkpoint 完整重建）。
        // 防止旧数据与新 checkpoint 混合；resume 后下次 checkpoint 为 Full 模式（cursor 重置为 0）。
        _committedToolResults.Clear();
        _committedResultSequences.Clear();
        _committedResultOrder.Clear();
        _pendingToolResults.Clear();
        _committedResultSequence = 0;
        _lastCheckpointSequence = 0;
        _lastCheckpointId = null;
        _lastCheckpointContentHash = null; // P0-5：重置 hash chain cursor

        // P0-5：递归加载 checkpoint 链（含深度限制 + 完整性校验）
        await ResumeAsyncInternal(checkpoint, depth: 0, cancellationToken).ConfigureAwait(false);

        // 恢复 session/workspace 跟踪
        if (checkpoint.Session is not null)
        {
            _lastSessionId = checkpoint.Session.Value;
            _lastWorkspaceId = checkpoint.Session.WorkspaceId;
        }

        // R28-E P1-2：若注入了 snapshotStore，根据 SnapshotId 加载 _lastSnapshot
        // P4：Cursor 模式优先使用 state.ActiveSnapshotId（与 checkpoint.SnapshotId 在工厂产出时一致，
        // 但显式优先 ActiveSnapshotId 以符合 Cursor 模式语义——ActiveSnapshotId 是 Cursor 模式的权威字段）
        _lastSnapshot = null;
        var cursorSnapshotId = TryGetCursorActiveSnapshotId(checkpoint);
        var snapshotIdToLoad = !string.IsNullOrWhiteSpace(cursorSnapshotId) ? cursorSnapshotId : checkpoint.SnapshotId;
        if (_snapshotStore is not null && !string.IsNullOrWhiteSpace(snapshotIdToLoad))
        {
            try
            {
                _lastSnapshot = await _snapshotStore.GetAsync(
                    _lastWorkspaceId, snapshotIdToLoad, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // snapshot 加载失败不阻断 resume；_lastSnapshot 保持 null
                // 调用方可通过后续 BuildContext 指令重建 snapshot
            }
        }

        // 重置状态：恢复后允许再次 RunAsync
        _state = AgentKernelState.Idle;
        _gracefulShutdown = false;
    }

    /// <summary>
    /// P4：从 Cursor 模式 checkpoint 恢复 Kernel 状态。
    /// 从 AgentRunEventStore 读取事件流，过滤 ToolCallCompleted 事件重建 _committedToolResults；
    /// 恢复 BudgetCounters 到 Kernel 计数器；恢复 PendingResults；推进 cursor。
    /// 不递归加载 base checkpoint 链（事件流是完整真相源）。
    /// </summary>
    /// <param name="checkpoint">Cursor 模式 checkpoint。</param>
    /// <param name="state">已解析的 KernelCheckpointStateDto（Mode=Cursor）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 前提：<see cref="_eventStore"/> 已注入。若未注入则抛 <see cref="InvalidOperationException"/>
    /// （Cursor 模式 checkpoint 无法在没有 EventStore 的情况下恢复）。
    /// </remarks>
    private async ValueTask ResumeFromCursorCheckpointAsync(
        AgentCheckpoint checkpoint,
        DefaultAgentCheckpointFactory.KernelCheckpointStateDto state,
        CancellationToken cancellationToken)
    {
        if (_eventStore is null)
        {
            throw new InvalidOperationException(
                $"Cursor 模式 checkpoint {checkpoint.CheckpointId} 无法恢复：Kernel 未注入 IAgentRunEventStore。" +
                "Cursor 模式要求 EventStore 作为事件流真相源以重建 CommittedResults。");
        }

        // P4：从 EventStore 读取事件流（sequence 0..LastEventSequence，含两端）
        // LastEventSequence 为 null 时退回 0（无事件可重建——CommittedResults 保持空）
        var lastSeq = state.LastEventSequence ?? -1;
        var take = lastSeq + 1; // sequence 从 0 开始；读 [0, lastSeq] 共 lastSeq+1 条
        if (take > 0)
        {
            var workspaceId = checkpoint.Session?.WorkspaceId ?? _lastWorkspaceId;
            var runId = checkpoint.Session?.Value ?? _lastSessionId;
            IReadOnlyList<AgentRunEvent> events;
            try
            {
                events = await _eventStore.ReadAsync(
                    workspaceId, runId, 0, take, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // EventStore 读取失败：CommittedResults 保持空（cursor 仍推进）
                events = Array.Empty<AgentRunEvent>();
            }

            // 过滤 ToolCallCompleted 事件，从 payload 反序列化重建 _committedToolResults
            // payload 结构（AgentRunActor 写入）：{ toolName, succeeded, output, error, durationMs }
            // 注意：payload 不含 requestId / sideEffect——requestId 用合成值（evt-{sequence}），
            // sideEffect 默认 Unknown（保守策略：未声明的 tool 不自动重放）。
            foreach (var evt in events)
            {
                if (evt.EventType != AgentRunEventType.ToolCallCompleted)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(evt.Payload))
                {
                    continue;
                }

                try
                {
                    var payload = JsonSerializer.Deserialize<ToolCallCompletedPayload>(evt.Payload);
                    if (payload is null)
                    {
                        continue;
                    }

                    var syntheticRequestId = $"evt-{evt.Sequence}";
                    AddCommittedResult(syntheticRequestId, new ToolDispatchResult
                    {
                        Succeeded = payload.Succeeded,
                        Result = payload.Output,
                        Error = payload.Error,
                        Duration = payload.DurationMs > 0
                            ? TimeSpan.FromMilliseconds(payload.DurationMs)
                            : TimeSpan.Zero,
                        SideEffect = ToolSideEffect.Unknown // 保守：事件 payload 不含 sideEffect
                    });
                }
                catch (JsonException)
                {
                    // payload 解析失败：跳过此事件（不阻断恢复）
                }
            }
        }

        // P4：恢复 BudgetCounters 到 Kernel 计数器
        if (state.BudgetCounters is not null)
        {
            _turnsUsed = state.BudgetCounters.TurnsUsed;
            _tokensUsed = state.BudgetCounters.TokensUsed;
            _costUsedUsd = state.BudgetCounters.CostUsedUsd;
        }

        // P4：恢复 pending results（Unknown 副作用，未提交——与 Full/Delta 路径一致）
        if (state.PendingResults is not null)
        {
            foreach (var entry in state.PendingResults)
            {
                _pendingToolResults[entry.RequestId] = new ToolDispatchResult
                {
                    Succeeded = entry.Succeeded,
                    Result = entry.Result,
                    Error = entry.Error,
                    Duration = TimeSpan.Zero,
                    SideEffect = entry.SideEffect
                };
            }
        }

        // P4：恢复 cursor（LastSequence / LastCheckpointId / ContentHash）
        // Cursor 模式不递归 base 链，但下次 AutoCheckpoint 仍可走 Cursor 路径（EventStore cursor 持续推进）
        _lastCheckpointSequence = state.LastSequence;
        _lastCheckpointId = checkpoint.CheckpointId;
        _lastCheckpointContentHash = state.ContentHash;

        // P4：缓存 LastEventSequence 供下次 CreateCheckpointAsync 的 accessor 委托读取
        _lastEventSequenceCache = state.LastEventSequence;
    }

    /// <summary>
    /// P4：尝试从 checkpoint StateJson 解析 Cursor 模式的 ActiveSnapshotId。
    /// 非 Cursor 模式或解析失败时返回 null（调用方退回 checkpoint.SnapshotId）。
    /// </summary>
    private static string? TryGetCursorActiveSnapshotId(AgentCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.StateJson))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(checkpoint.StateJson);
            if (state is null || state.Mode != DefaultAgentCheckpointFactory.CheckpointMode.Cursor)
            {
                return null;
            }
            return state.ActiveSnapshotId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// P0-5：递归加载 checkpoint 链（含深度限制 + hash chain 完整性校验）。
    /// </summary>
    /// <param name="checkpoint">当前要加载的 checkpoint（Full 或 Delta）。</param>
    /// <param name="depth">当前递归深度（0 = 顶层 delta；每递归到 base +1）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 校验项：
    ///   1. 链深度 ≤ <see cref="MaxCheckpointChainDepth"/>（防止损坏/恶意链导致栈溢出）。
    ///   2. ContentHash 一致性（StateJson 未被篡改）。
    ///   3. Delta 模式：PrevChainHash == base.ContentHash（base 未被篡改/替换）。
    ///   4. Delta 模式：ChainSessionId == base.ChainSessionId（防跨 session 链接）。
    ///   5. Delta 模式：BaseLastSequence == base.LastSequence（base 匹配）。
    ///   6. Delta 模式：所有 delta 条目 Sequence > base.LastSequence（无重叠/回退）。
    /// 旧 checkpoint（无 ContentHash/ChainSessionId）跳过对应校验（向后兼容）。
    /// </remarks>
    private async ValueTask ResumeAsyncInternal(AgentCheckpoint checkpoint, int depth, CancellationToken cancellationToken)
    {
        // P0-5：链深度限制
        if (depth > MaxCheckpointChainDepth)
        {
            throw new InvalidOperationException(
                $"Checkpoint delta 链深度超过上限（{MaxCheckpointChainDepth}）；可能存在循环或损坏的链。");
        }

        if (string.IsNullOrWhiteSpace(checkpoint.StateJson))
        {
            return;
        }

        DefaultAgentCheckpointFactory.KernelCheckpointStateDto state;
        try
        {
            state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(checkpoint.StateJson)!;
            if (state is null)
            {
                return;
            }
        }
        catch (JsonException)
        {
            // StateJson 非预期格式（可能是旧版 checkpoint 或手动构造）；跳过恢复
            return;
        }

        // P0-5：校验 ContentHash（旧 checkpoint 无此字段 → 跳过）
        if (!string.IsNullOrEmpty(state.ContentHash))
        {
            if (!DefaultAgentCheckpointFactory.VerifyContentHash(checkpoint.StateJson))
            {
                throw new InvalidOperationException(
                    $"Checkpoint {checkpoint.CheckpointId} 的 ContentHash 校验失败；StateJson 可能被篡改或损坏。");
            }
        }

        // P4：Cursor 模式——从 AgentRunEventStore 重建 CommittedResults，无需递归加载 base 链。
        // 事件流是完整真相源；checkpoint 仅记录 LastEventSequence cursor + ActiveSnapshotId + BudgetCounters + PendingResults。
        if (state.Mode == DefaultAgentCheckpointFactory.CheckpointMode.Cursor)
        {
            await ResumeFromCursorCheckpointAsync(checkpoint, state, cancellationToken).ConfigureAwait(false);
            return;
        }

        // R28-G P1-5 / P0-5：Delta 模式 — 加载 base checkpoint 并校验链完整性
        if (state.Mode == DefaultAgentCheckpointFactory.CheckpointMode.Delta
            && !string.IsNullOrEmpty(state.BaseCheckpointId)
            && _checkpointStore is not null)
        {
            var baseCheckpoint = await _checkpointStore.GetAsync(
                checkpoint.Session?.WorkspaceId ?? checkpoint.Session?.Value ?? string.Empty,
                state.BaseCheckpointId, cancellationToken).ConfigureAwait(false);
            if (baseCheckpoint is null)
            {
                throw new InvalidOperationException(
                    $"Delta checkpoint {checkpoint.CheckpointId} 引用的 base checkpoint {state.BaseCheckpointId} 未找到。");
            }

            // P0-5：解析 base state 用于链完整性校验
            DefaultAgentCheckpointFactory.KernelCheckpointStateDto? baseState = null;
            if (!string.IsNullOrWhiteSpace(baseCheckpoint.StateJson))
            {
                try
                {
                    baseState = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(baseCheckpoint.StateJson);
                }
                catch (JsonException)
                {
                    // base StateJson 解析失败 → 跳过链校验（base 自身的 ContentHash 校验会在递归时进行）
                }
            }

            // P0-5：校验 PrevChainHash（delta 的 PrevChainHash 必须等于 base 的 ContentHash）
            if (!string.IsNullOrEmpty(state.PrevChainHash) && !string.IsNullOrEmpty(baseState?.ContentHash))
            {
                if (!string.Equals(state.PrevChainHash, baseState!.ContentHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Delta checkpoint {checkpoint.CheckpointId} 的 PrevChainHash 与 base {state.BaseCheckpointId} 的 ContentHash 不匹配；base 可能被篡改或替换。");
                }
            }

            // P0-5：校验 ChainSessionId（delta 与 base 必须属于同一 session）
            if (!string.IsNullOrEmpty(state.ChainSessionId) && !string.IsNullOrEmpty(baseState?.ChainSessionId))
            {
                if (!string.Equals(state.ChainSessionId, baseState!.ChainSessionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Delta checkpoint {checkpoint.CheckpointId} 的 ChainSessionId 与 base {state.BaseCheckpointId} 不匹配；检测到跨 session 链接。");
                }
            }

            // P0-5：校验 BaseLastSequence（delta 记录的 base cursor 必须等于 base 实际的 LastSequence）
            if (baseState is not null && state.BaseLastSequence != 0)
            {
                if (state.BaseLastSequence != baseState.LastSequence)
                {
                    throw new InvalidOperationException(
                        $"Delta checkpoint {checkpoint.CheckpointId} 的 BaseLastSequence ({state.BaseLastSequence}) 与 base {state.BaseCheckpointId} 的 LastSequence ({baseState.LastSequence}) 不匹配。");
                }
            }

            // P0-5：校验 delta 条目 Sequence 全部 > base.LastSequence（无重叠/回退）
            if (baseState is not null && state.CommittedResults is not null && state.CommittedResults.Count > 0)
            {
                foreach (var entry in state.CommittedResults)
                {
                    if (entry.Sequence <= baseState.LastSequence)
                    {
                        throw new InvalidOperationException(
                            $"Delta checkpoint {checkpoint.CheckpointId} 包含 Sequence ({entry.Sequence}) <= base.LastSequence ({baseState.LastSequence}) 的条目；存在重叠或序号回退。");
                    }
                }
            }

            // 递归加载 base checkpoint（depth + 1），重建完整状态后再 apply delta 条目
            await ResumeAsyncInternal(baseCheckpoint, depth + 1, cancellationToken).ConfigureAwait(false);
        }

        // apply committed results（Full：完整重建；Delta：仅追加新增条目）
        if (state.CommittedResults is not null)
        {
            foreach (var entry in state.CommittedResults)
            {
                AddCommittedResult(entry.RequestId, new ToolDispatchResult
                {
                    Succeeded = entry.Succeeded,
                    Result = entry.Result,
                    Error = entry.Error,
                    Duration = TimeSpan.Zero,
                    SideEffect = entry.SideEffect
                });
            }
        }

        // R28-G P1-5：恢复 pending results（Unknown 副作用，未提交）
        if (state.PendingResults is not null)
        {
            foreach (var entry in state.PendingResults)
            {
                _pendingToolResults[entry.RequestId] = new ToolDispatchResult
                {
                    Succeeded = entry.Succeeded,
                    Result = entry.Result,
                    Error = entry.Error,
                    Duration = TimeSpan.Zero,
                    SideEffect = entry.SideEffect
                };
            }
        }

        // R28-G P1-5：恢复 delta cursor，确保下次 checkpoint 为 Delta（基于本次 LastSequence）
        // P0-5：同时恢复 ContentHash，供下次 delta checkpoint 构建 PrevChainHash
        _lastCheckpointSequence = state.LastSequence;
        _lastCheckpointId = checkpoint.CheckpointId;
        _lastCheckpointContentHash = state.ContentHash;
    }

    /// <summary>R28-C WP-B：从指令 Metadata 更新 session/workspace 跟踪。</summary>
    private void TrackSessionFromInstruction(AgentKernelInstruction instruction)
    {
        if (instruction.Metadata.TryGetValue("sessionId", out var s) && !string.IsNullOrWhiteSpace(s))
        {
            _lastSessionId = s;
        }
        if (instruction.Metadata.TryGetValue("workspaceId", out var w) && !string.IsNullOrWhiteSpace(w))
        {
            _lastWorkspaceId = w;
        }
    }

    /// <summary>
    /// R28-G P1-5：向 _committedToolResults 添加条目并维护 FIFO 容量上限 + 序号分配。
    /// </summary>
    /// <remarks>
    /// - 已存在的 requestId：仅更新 Result，保留原 Sequence（避免序号回退破坏 delta cursor 语义）。
    /// - 新增 requestId：分配下一个单调 Sequence，加入 FIFO 队列；超容量时按 FIFO 淘汰最旧条目。
    /// - 调用方不应直接写 <c>_committedToolResults[x] = y</c>，应通过本方法。
    /// </remarks>
    /// <param name="requestId">Tool RequestId（与 InstructionId 对应）。</param>
    /// <param name="result">已提交的 ToolDispatchResult。</param>
    private void AddCommittedResult(string requestId, ToolDispatchResult result)
    {
        // 已存在：仅更新 Result，保留原 Sequence（避免序号回退破坏 delta cursor 语义）
        if (_committedToolResults.TryGetValue(requestId, out _))
        {
            _committedToolResults[requestId] = result;
            return;
        }

        // 容量超限：FIFO 淘汰最旧条目（包括 dict + sequences + queue 三处同步）
        while (_committedToolResults.Count >= _maxCommittedResults && _committedResultOrder.Count > 0)
        {
            var oldest = _committedResultOrder.Dequeue();
            _committedToolResults.Remove(oldest);
            _committedResultSequences.Remove(oldest);
        }

        var seq = ++_committedResultSequence;
        _committedToolResults[requestId] = result;
        _committedResultSequences[requestId] = seq;
        _committedResultOrder.Enqueue(requestId);
    }

    /// <summary>
    /// R28-G P1-5：从 checkpoint StateJson 提取 LastSequence 用于更新 delta cursor。
    /// </summary>
    /// <remarks>
    /// checkpoint 保存成功后调用，将 _lastCheckpointSequence 推进到本次 checkpoint 的 LastSequence，
    /// 使下次 checkpoint 走 Delta 路径（仅序列化新增条目）。
    /// </remarks>
    private void AdvanceCheckpointCursor(AgentCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.StateJson))
        {
            return;
        }
        try
        {
            var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(checkpoint.StateJson);
            if (state is not null)
            {
                _lastCheckpointSequence = state.LastSequence;
                _lastCheckpointId = checkpoint.CheckpointId;
                _lastCheckpointContentHash = state.ContentHash; // P0-5：推进 hash chain cursor
            }
        }
        catch (JsonException)
        {
            // StateJson 解析失败：不推进 cursor（下次仍走 Full 路径，安全降级）
        }
    }

    /// <summary>
    /// R28-C WP-B：取消时自动产出可恢复 checkpoint。
    /// R28-E P1-1：通过 <see cref="IAgentCheckpointFactory"/> 统一构建（与手动 Checkpoint 相同格式）。
    /// R28-G P1-5：成功保存后推进 delta cursor（下次 AutoCheckpoint 走 Delta 路径）。
    /// P4：若注入了 <see cref="IAgentRunEventStore"/>，先读取最新事件 sequence 缓存到
    /// _lastEventSequenceCache，工厂据此产出 Cursor 模式 checkpoint（不序列化 CommittedResults）。
    /// </summary>
    private async ValueTask AutoCheckpointAsync(CancellationToken cancellationToken)
    {
        // 无已提交结果且无 snapshot → 无需 checkpoint
        if (_committedToolResults.Count == 0 && _lastSnapshot is null)
        {
            return;
        }

        // P4：若注入 EventStore，读取最新事件 sequence 作为 cursor 缓存。
        // accessor 委托 getLastEventSequence 同步读取此缓存；工厂据此决定 Cursor 模式。
        // 使用 _lastSessionId 作为 runId（Kernel 不区分 session/run；调用方负责对齐）。
        if (_eventStore is not null)
        {
            try
            {
                _lastEventSequenceCache = await _eventStore.GetLastSequenceAsync(
                    _lastWorkspaceId, _lastSessionId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // EventStore 读取失败：清空缓存，工厂退回 Delta/Full 模式（安全降级）
                _lastEventSequenceCache = null;
            }
        }

        var checkpointId = $"auto-{_lastSessionId}-{DateTimeOffset.UtcNow.Ticks}";

        // R28-E P1-1：通过 factory 统一构建（与手动 Checkpoint 相同格式）
        var checkpoint = await _checkpointFactory!.CreateCheckpointAsync(
            checkpointId, _lastSessionId, _lastWorkspaceId, cancellationToken).ConfigureAwait(false);

        await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        // R28-G P1-5：推进 cursor，下次 AutoCheckpoint 可走 Delta 路径
        AdvanceCheckpointCursor(checkpoint);
    }

    // R28-E P1-1：KernelCheckpointState / CommittedToolResultEntry 序列化模型已移到
    // DefaultAgentCheckpointFactory（KernelCheckpointStateDto / CommittedToolResultDto），
    // 供 Kernel 与外部反序列化共享。

    /// <summary>
    /// P4：ToolCallCompleted 事件 payload 反序列化模型。
    /// 与 <c>AgentRunActor</c> 写入的 payload 结构对齐（camelCase 字段名）：
    /// <c>{ toolName, succeeded, output, error, durationMs }</c>。
    /// </summary>
    private sealed class ToolCallCompletedPayload
    {
        /// <summary>Tool 名称（仅用于审计；重建时不使用）。</summary>
        [JsonPropertyName("toolName")]
        public string? ToolName { get; init; }

        /// <summary>是否成功。</summary>
        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }

        /// <summary>Tool 输出（成功时）。</summary>
        [JsonPropertyName("output")]
        public string? Output { get; init; }

        /// <summary>错误信息（失败时）。</summary>
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>执行耗时（毫秒）。</summary>
        [JsonPropertyName("durationMs")]
        public double DurationMs { get; init; }
    }
}
