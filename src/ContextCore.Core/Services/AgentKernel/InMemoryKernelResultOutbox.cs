using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-D P0-5：InMemoryKernelResultOutbox — 进程内结果 outbox 默认实现
//
// 目标：
//   提供 IKernelResultOutbox 的 in-memory 实现，让 FallbackToDeterministic 策略下
//   Transport 发送失败的结果不再静默丢弃，而是写入 outbox 待后续重放。
//
// 设计原则：
//   1. 使用 bounded Channel（容量由 KernelTransportOptions.MaxOutboxBacklog 决定）。
//   2. 满时 DropOldest：丢弃最早的结果并记录（避免内存耗尽，但优先保留最新结果）。
//   3. 线程安全：Channel 本身线程安全。
//   4. 生产部署应替换为持久化实现（文件/DB based outbox）。
// ===========================================================================

/// <summary>
/// R28-D P0-5：进程内 Kernel 结果 outbox（默认实现）。
/// </summary>
/// <remarks>
/// 使用 <see cref="Channel{T}"/> 作为 FIFO 缓冲区。
/// 满时丢弃最早的结果（DropOldest 语义），避免内存耗尽。
/// 生产部署应替换为持久化实现（如基于文件/DB 的 outbox）。
/// </remarks>
public sealed class InMemoryKernelResultOutbox : IKernelResultOutbox
{
    private readonly Channel<AgentKernelResult> _channel;

    /// <summary>
    /// 构造进程内 outbox。
    /// </summary>
    /// <param name="capacity">最大积压数量（默认 1024）。</param>
    public InMemoryKernelResultOutbox(int capacity = 1024)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity 必须大于 0");
        }

        _channel = Channel.CreateBounded<AgentKernelResult>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        // DropOldest 模式下 WriteAsync 不会阻塞/抛出满异常，而是丢弃最早项
        return _channel.Writer.WriteAsync(result, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<AgentKernelResult?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        if (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_channel.Reader.TryRead(out var result))
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// 当前 outbox 中待重放的结果数——<b>本进程 channel 计数</b>，仅反映 <see cref="EnqueueAsync"/>
    /// 写入但尚未被 <see cref="DequeueAsync"/> 读取的结果，<b>不是</b>全局视图。
    /// </summary>
    /// <remarks>
    /// 返回进程内 bounded Channel 的 <c>Reader.Count</c>；不反映持久化 outbox（如 <c>PostgresKernelResultOutbox</c>）
    /// 中跨实例累积的 backlog。<b>不可用于调度或安全判断</b>（如 shutdown、限流、拒绝请求）；
    /// 生产指标应使用 <see cref="GetPendingCountAsync"/> 或后台聚合服务导出的 <c>global_pending_count</c>。
    /// 仅适用于单进程部署/测试场景。
    /// </remarks>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>
    /// 异步获取 pending 数量。
    /// 对 <see cref="InMemoryKernelResultOutbox"/> 返回<b>本进程 channel 计数</b>（与 <see cref="PendingCount"/> 相同）；
    /// 持久化实现（如 <c>PostgresKernelResultOutbox</c>）返回<b>DB 精确值（全局，跨实例）</b>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 outbox 中 state='Pending' 的结果数。</returns>
    public ValueTask<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(_channel.Reader.Count);
    }
}
