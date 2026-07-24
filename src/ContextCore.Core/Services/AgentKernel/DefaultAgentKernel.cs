using System.Globalization;
using System.Text.Json;
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
    private readonly Dictionary<string, ToolDispatchResult> _committedToolResults = new(StringComparer.Ordinal);

    // R28-C WP-B：跟踪最后一次 session/workspace（用于取消时自动 checkpoint）
    // 和是否为 graceful shutdown（Shutdown 指令 vs 外部取消）
    private string _lastSessionId = "kernel-default-session";
    private string _lastWorkspaceId = "kernel-default-workspace";
    private bool _gracefulShutdown;

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
    public DefaultAgentKernel(
        IAgentKernelTransport transport,
        IToolDispatcher toolDispatcher,
        IAgentCheckpointStore checkpointStore,
        IContextDecisionRuntime? decisionRuntime = null,
        IAgentContextProjector? contextProjector = null,
        KernelTransportOptions? transportOptions = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _decisionRuntime = decisionRuntime;
        _contextProjector = contextProjector;
        _transportOptions = transportOptions ?? KernelTransportOptions.Default;

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

                // Shutdown 指令：排空 inbox 后停止
                if (instruction.Kind == AgentKernelInstructionKind.Shutdown)
                {
                    _gracefulShutdown = true;
                    _state = AgentKernelState.Draining;
                    await DrainInboxAsync(cancellationToken).ConfigureAwait(false);
                    _state = AgentKernelState.Stopped;
                    return;
                }

                // 处理 Execute / Checkpoint / BuildContext 指令
                var result = await ProcessInstructionAsync(instruction, ct).ConfigureAwait(false);
                // R28-C WP-D：按 TransportFailurePolicy 发送结果（FailFast/Retry/FallbackToDeterministic）
                await SendResultWithPolicyAsync(result, ct).ConfigureAwait(false);
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
    private async ValueTask DrainInboxAsync(CancellationToken cancellationToken)
    {
        while (_inbox.Reader.TryRead(out var instruction))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 跳过重复 Shutdown 指令
            if (instruction.Kind == AgentKernelInstructionKind.Shutdown)
            {
                continue;
            }

            try
            {
                var result = await ProcessInstructionAsync(instruction, cancellationToken).ConfigureAwait(false);
                // R28-C WP-D：排空期间同样按策略发送（FailFast 下异常会跳出排空循环）
                await SendResultWithPolicyAsync(result, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
                _lastProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException)
            {
                // 排空期间取消；剩余指令丢弃
                break;
            }
        }
    }

    /// <summary>处理单条指令（Execute / Checkpoint）；Shutdown 由 RunAsync 直接处理。</summary>
    private async ValueTask<AgentKernelResult> ProcessInstructionAsync(
        AgentKernelInstruction instruction,
        CancellationToken cancellationToken)
    {
        try
        {
            return instruction.Kind switch
            {
                AgentKernelInstructionKind.Execute => await ProcessExecuteAsync(instruction, cancellationToken).ConfigureAwait(false),
                AgentKernelInstructionKind.Checkpoint => await ProcessCheckpointAsync(instruction, cancellationToken).ConfigureAwait(false),
                AgentKernelInstructionKind.BuildContext => await ProcessBuildContextAsync(instruction, cancellationToken).ConfigureAwait(false),
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentKernelResult
            {
                InstructionId = instruction.InstructionId,
                Succeeded = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>处理 Execute 指令：通过 IToolDispatcher 分派 tool。</summary>
    /// <remarks>
    /// R28-C WP-C：tool 结果按 RequestId 去重——已提交的结果不重新执行（幂等）。
    /// 副作用分类决定是否自动提交：
    ///   - None / ReadOnly / Write：自动提交到 _committedToolResults
    ///   - Unknown：不自动提交，恢复时不重放
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

        var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = toolName,
            Payload = payload,
            RequestId = instruction.InstructionId
        }, cancellationToken).ConfigureAwait(false);

        // R28-C WP-C：副作用分类决定是否自动提交
        // Unknown 不自动提交——恢复时不重放，需调用方显式确认
        if (dispatchResult.SideEffect != ToolSideEffect.Unknown)
        {
            _committedToolResults[instruction.InstructionId] = dispatchResult;
        }

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = dispatchResult.Succeeded,
            Output = dispatchResult.Result,
            Error = dispatchResult.Error
        };
    }

    /// <summary>处理 Checkpoint 指令：通过 IAgentCheckpointStore 保存检查点。</summary>
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

        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = instruction.InstructionId,
            Session = new AgentSessionId
            {
                Value = sessionId,
                WorkspaceId = workspaceId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            // R28-C WP-B：若存在上次 snapshot，将 SnapshotId 关联到 checkpoint，
            // 恢复时可据此重建 AgentContextSnapshot。
            SnapshotId = _lastSnapshot?.SnapshotId,
            StateJson = instruction.Payload ?? "{}"
        };

        await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);

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
        switch (_transportOptions.FailurePolicy)
        {
            case TransportFailurePolicy.FailFast:
                await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
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
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Transport 不可用：静默降级，不中断 Kernel 循环。
                    // 结果本地丢弃；循环继续处理后续指令。
                    // 当 transport 恢复后后续结果可正常发送。
                }
                return;

            default:
                // 未知策略值：保守按 FailFast 处理
                await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    // =======================================================================
    // R28-C WP-B：Checkpoint 恢复 + 自动 Checkpoint
    // =======================================================================

    /// <summary>
    /// R28-C WP-B：从 checkpoint 恢复 Kernel 状态。
    /// 反序列化已提交的 tool 结果 + snapshot 引用，恢复后 RunAsync 可继续处理。
    /// </summary>
    public ValueTask ResumeAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // 反序列化 checkpoint StateJson → 恢复已提交 tool 结果
        if (!string.IsNullOrWhiteSpace(checkpoint.StateJson))
        {
            try
            {
                var state = JsonSerializer.Deserialize<KernelCheckpointState>(checkpoint.StateJson);
                if (state?.CommittedResults is not null)
                {
                    foreach (var entry in state.CommittedResults)
                    {
                        _committedToolResults[entry.RequestId] = new ToolDispatchResult
                        {
                            Succeeded = entry.Succeeded,
                            Result = entry.Result,
                            Error = entry.Error,
                            Duration = TimeSpan.Zero,
                            SideEffect = entry.SideEffect
                        };
                    }
                }
            }
            catch (JsonException)
            {
                // StateJson 非预期格式（可能是旧版 checkpoint 或手动构造）；跳过恢复
            }
        }

        // 恢复 session/workspace 跟踪
        if (checkpoint.Session is not null)
        {
            _lastSessionId = checkpoint.Session.Value;
            _lastWorkspaceId = checkpoint.Session.WorkspaceId;
        }

        // 重置状态：恢复后允许再次 RunAsync
        _state = AgentKernelState.Idle;
        _gracefulShutdown = false;

        return ValueTask.CompletedTask;
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
    /// R28-C WP-B：取消时自动产出可恢复 checkpoint。
    /// 序列化已提交的 tool 结果 + snapshot 引用到 StateJson，保存到 IAgentCheckpointStore。
    /// </summary>
    private async ValueTask AutoCheckpointAsync(CancellationToken cancellationToken)
    {
        // 无已提交结果且无 snapshot → 无需 checkpoint
        if (_committedToolResults.Count == 0 && _lastSnapshot is null)
        {
            return;
        }

        var state = new KernelCheckpointState
        {
            SnapshotId = _lastSnapshot?.SnapshotId,
            CommittedResults = _committedToolResults.Select(kv => new CommittedToolResultEntry
            {
                RequestId = kv.Key,
                Succeeded = kv.Value.Succeeded,
                Result = kv.Value.Result,
                Error = kv.Value.Error,
                SideEffect = kv.Value.SideEffect
            }).ToList()
        };

        var stateJson = JsonSerializer.Serialize(state);

        var checkpointId = $"auto-{_lastSessionId}-{DateTimeOffset.UtcNow.Ticks}";

        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = checkpointId,
            Session = new AgentSessionId
            {
                Value = _lastSessionId,
                WorkspaceId = _lastWorkspaceId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = _lastSnapshot?.SnapshotId,
            StateJson = stateJson
        };

        await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>R28-C WP-B：Checkpoint 序列化模型。</summary>
    private sealed class KernelCheckpointState
    {
        public string? SnapshotId { get; init; }
        public List<CommittedToolResultEntry> CommittedResults { get; init; } = new();
    }

    /// <summary>R28-C WP-B：已提交 tool 结果的序列化条目。</summary>
    private sealed class CommittedToolResultEntry
    {
        public string RequestId { get; init; } = "";
        public bool Succeeded { get; init; }
        public string? Result { get; init; }
        public string? Error { get; init; }
        public ToolSideEffect SideEffect { get; init; }
    }
}
