using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-C：DefaultAgentKernel — 极薄 .NET Agent Kernel 实现
//
// 目标（对齐 Workstream C 规格）：
//   1. 实现 IAgentKernel 的 RunAsync / SubmitAsync / GetStatus。
//   2. RunAsync 循环：从 inbox 读取指令 → 处理 → 通过 Transport 发送结果。
//   3. Execute 指令：调用 IToolDispatcher.DispatchAsync。
//   4. Checkpoint 指令：调用 IAgentCheckpointStore.SaveAsync。
//   5. Shutdown 指令：设置 Draining → 排空 inbox → 设置 Stopped。
//   6. bounded Channel（容量 256，Wait 模式）。
//
// 设计决策：
//   - Kernel 维护自身 inbox（Channel<AgentKernelInstruction>）；SubmitAsync 写入 inbox，
//     RunAsync 从 inbox 读取。Transport 主要用于发送结果（SendResultAsync）。
//   - Transport.ReceiveAsync 存在于接口中供自定义 Transport 推送远程指令，
//     但默认 Kernel 使用自身 inbox 作为输入源（简化单消费者模型，避免双源 select 复杂度）。
//   - 取消令牌传播：外部 cancellationToken 与内部 _shutdownCts 链接；
//     外部取消时抛 OperationCanceledException；Shutdown 指令时正常退出。
// ===========================================================================

/// <summary>
/// R28-C：极薄 .NET Agent Kernel 实现。
/// </summary>
/// <remarks>
/// 编排 Transport → ToolDispatcher → CheckpointStore 三者，自身不持有业务状态。
/// 线程安全：SubmitAsync 可多线程并发调用；RunAsync 单消费者执行。
/// </remarks>
public sealed class DefaultAgentKernel : IAgentKernel
{
    private readonly IAgentKernelTransport _transport;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IAgentCheckpointStore _checkpointStore;
    private readonly Channel<AgentKernelInstruction> _inbox;
    private readonly CancellationTokenSource _shutdownCts;
    private AgentKernelState _state;
    private int _processedCount;
    private DateTimeOffset? _lastProcessedAt;

    /// <summary>
    /// 构造默认 Agent Kernel。
    /// </summary>
    /// <param name="transport">Transport 抽象（用于发送结果）。</param>
    /// <param name="toolDispatcher">Tool 分派器。</param>
    /// <param name="checkpointStore">Agent 检查点存储。</param>
    public DefaultAgentKernel(
        IAgentKernelTransport transport,
        IToolDispatcher toolDispatcher,
        IAgentCheckpointStore checkpointStore)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));

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

                // Shutdown 指令：排空 inbox 后停止
                if (instruction.Kind == AgentKernelInstructionKind.Shutdown)
                {
                    _state = AgentKernelState.Draining;
                    await DrainInboxAsync(cancellationToken).ConfigureAwait(false);
                    _state = AgentKernelState.Stopped;
                    return;
                }

                // 处理 Execute / Checkpoint 指令
                var result = await ProcessInstructionAsync(instruction, ct).ConfigureAwait(false);
                await _transport.SendResultAsync(result, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
                _lastProcessedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _state = AgentKernelState.Stopped;
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
                await _transport.SendResultAsync(result, cancellationToken).ConfigureAwait(false);
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

        var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = toolName,
            Payload = payload,
            RequestId = instruction.InstructionId
        }, cancellationToken).ConfigureAwait(false);

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
            StateJson = instruction.Payload ?? "{}"
        };

        await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        return new AgentKernelResult
        {
            InstructionId = instruction.InstructionId,
            Succeeded = true,
            Output = checkpoint.CheckpointId
        };
    }
}
