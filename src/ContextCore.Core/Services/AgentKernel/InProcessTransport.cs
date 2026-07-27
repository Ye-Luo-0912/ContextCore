using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-C：InProcessTransport — 进程内 Transport 实现
//
// 目标（对齐 Workstream C 规格）：
//   1. 实现 IAgentKernelTransport 的 ReceiveAsync / SendResultAsync。
//   2. 提供额外的 SubmitAsync（写入 inbox）和 ReceiveResultAsync（读取 outbox），
//      供测试和单机部署使用。
//   3. 使用 bounded Channel（默认容量 256）作为 inbox 和 outbox。
//
// 设计原则：
//   1. inbox 与 outbox 独立；inbox 接收指令，outbox 缓存结果。
//   2. 默认 Kernel 使用自身 inbox（不调用 Transport.ReceiveAsync），
//      但 Transport.ReceiveAsync 可用于自定义 Kernel 实现从远程接收指令。
//   3. 线程安全：Channel 本身线程安全；多写入者单读取者模式。
// ===========================================================================

/// <summary>
/// R28-C：进程内 Transport（默认实现，用于测试和单机部署）。
/// </summary>
/// <remarks>
/// 使用 <see cref="Channel{T}"/> 作为 inbox / outbox 的有界缓冲区。
/// inbox 接收 <see cref="AgentKernelInstruction"/>，outbox 缓存 <see cref="AgentKernelResult"/>。
/// </remarks>
public sealed class InProcessTransport : IAgentKernelTransport
{
    private readonly Channel<AgentKernelInstruction> _inbox;
    private readonly Channel<AgentKernelResult> _outbox;

    /// <summary>
    /// 构造进程内 Transport。
    /// </summary>
    /// <param name="capacity">inbox / outbox 容量（默认 256）。</param>
    public InProcessTransport(int capacity = 256)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity 必须大于 0");
        }

        _inbox = Channel.CreateBounded<AgentKernelInstruction>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _outbox = Channel.CreateBounded<AgentKernelResult>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 提交指令到 Transport 的 inbox（测试和单机部署用）。
    /// </summary>
    /// <param name="instruction">要提交的指令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// R28-D P0-4：<b>注意</b>——此方法写入 Transport 自己的 inbox。
    /// <see cref="DefaultAgentKernel"/> 维护独立 inbox，<b>不</b>读取 Transport 的 inbox，
    /// 因此调用此方法提交的指令<b>不会被默认 Kernel 处理</b>。
    /// 要驱动默认 Kernel，请使用 <see cref="IAgentKernel.SubmitAsync"/>。
    /// 此方法仅供自定义 Kernel 实现或测试需要从 Transport.ReceiveAsync 读取指令的场景使用。
    /// </remarks>
    public ValueTask SubmitAsync(AgentKernelInstruction instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return _inbox.Writer.WriteAsync(instruction, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<AgentKernelInstruction?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_inbox.Reader.TryRead(out var instruction))
            {
                return instruction;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public ValueTask SendResultAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _outbox.Writer.WriteAsync(result, cancellationToken);
    }

    /// <summary>从 outbox 读取下一条结果（测试和单机部署用，阻塞直到有结果或取消）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到的结果；outbox 关闭时返回 null。</returns>
    public async ValueTask<AgentKernelResult?> ReceiveResultAsync(CancellationToken cancellationToken = default)
    {
        if (await _outbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_outbox.Reader.TryRead(out var result))
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// 当前 inbox 中待处理指令数——<b>本进程 channel 计数</b>，仅反映 <see cref="SubmitAsync"/>
    /// 写入但尚未被 <see cref="ReceiveAsync"/> 读取的指令，<b>不是</b>全局 Transport 视图。
    /// </summary>
    /// <remarks>
    /// 返回进程内 bounded Channel 的 <c>Reader.Count</c>；不反映远程 Transport（如 <see cref="IDurableTransport"/>）
    /// 中持久化的 backlog。<b>不可用于调度或安全判断</b>（如 shutdown、限流、拒绝请求）；
    /// 生产指标应使用 <c>IDurableTransport.GetPendingInstructionCountAsync</c> 或后台聚合服务导出的
    /// <c>global_pending_count</c>。仅适用于单进程部署/测试场景。
    /// </remarks>
    public int PendingInstructionCount => _inbox.Reader.Count;

    /// <summary>
    /// 当前 outbox 中待读取结果数——<b>本进程 channel 计数</b>，仅反映 <see cref="SendResultAsync"/>
    /// 写入但尚未被 <see cref="ReceiveResultAsync"/> 读取的结果，<b>不是</b>全局 Transport 视图。
    /// </summary>
    /// <remarks>
    /// 返回进程内 bounded Channel 的 <c>Reader.Count</c>；不反映远程 Transport（如 <see cref="IDurableTransport"/>）
    /// 中持久化的 backlog。<b>不可用于调度或安全判断</b>（如 shutdown、限流、拒绝请求）；
    /// 生产指标应使用 <c>IDurableTransport.GetPendingResultCountAsync</c> 或后台聚合服务导出的
    /// <c>global_pending_count</c>。仅适用于单进程部署/测试场景。
    /// </remarks>
    public int PendingResultCount => _outbox.Reader.Count;

    /// <summary>完成 inbox / outbox 写入端（不再接受新数据）。</summary>
    public void Complete()
    {
        _inbox.Writer.TryComplete();
        _outbox.Writer.TryComplete();
    }
}
