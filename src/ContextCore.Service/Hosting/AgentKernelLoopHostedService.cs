using ContextCore.Abstractions;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// 生产 Composition Root — AgentKernel 主循环 HostedService
//
// 目标：
//   包装 <see cref="IAgentKernel.RunAsync"/> 在 BackgroundService 中执行，
//   让 Kernel 主循环随 Host 启动/停止，无需调用方手动管理生命周期。
//
// 设计边界：
//   1. 单次调用 RunAsync：DefaultAgentKernel.RunAsync 内部维护 _state 状态机，
//      Running 时重复调用会抛 InvalidOperationException。本服务仅在 ExecuteAsync
//      中调用一次，直到 Shutdown 指令或取消令牌触发。
//   2. 异常处理：RunAsync 正常完成（Shutdown 指令）时静默退出；异常时记录错误
//      并退出（不自动重启——Kernel 状态可能已不一致，需人工介入或进程重启）。
//   3. 与 DurableTransportInstructionPumpService 的关系：
//      - ProductionHA profile 下 pump 从 PG inbox 租约指令 → SubmitAsync 推入
//        Kernel inbox → 本服务调用 RunAsync 从 inbox 读取并处理。
//      - Development/SingleNode profile 下调用方直接 SubmitAsync → 本服务处理。
//      两者解耦：pump 不依赖本服务已启动（SubmitAsync 写入 Kernel inbox channel，
//      即使 Kernel 循环未启动也会排队等待）。
// ===========================================================================

/// <summary>
/// 生产 Composition Root：AgentKernel 主循环 HostedService。
/// 随 Host 启动 <see cref="IAgentKernel.RunAsync"/>，随 Host 停止取消循环。
/// </summary>
internal sealed class AgentKernelLoopHostedService : BackgroundService
{
    private readonly IAgentKernel _kernel;
    private readonly ILogger<AgentKernelLoopHostedService> _logger;

    public AgentKernelLoopHostedService(
        IAgentKernel kernel,
        ILogger<AgentKernelLoopHostedService> logger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentKernelLoopHostedService 启动：开始 Kernel 主循环。");

        try
        {
            await _kernel.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常关闭：Host 停止时取消令牌触发
        }
        catch (Exception ex)
        {
            // Kernel 循环异常退出：记录错误但不重新抛出（避免 Host 因 BackgroundService 异常崩溃）。
            // Kernel 内部已通过 AutoCheckpoint 持久化状态，重启后可通过 Recovery Worker 恢复。
            _logger.LogError(ex,
                "AgentKernelLoopHostedService 异常退出。Kernel 状态可能需要通过 AgentRunRecoveryWorker 恢复。");
        }

        _logger.LogInformation("AgentKernelLoopHostedService 已停止。");
    }
}
