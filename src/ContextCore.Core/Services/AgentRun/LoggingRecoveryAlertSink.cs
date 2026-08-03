using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// LoggingRecoveryAlertSink — 默认人工介入告警接收器（日志实现）
//
// P2-4 Recovery Integrity State 的默认 IRecoveryAlertSink：
//   - RecoveryBlocked / RecoveryCorrupted：LogError（数据损坏级，需运维介入修复）。
//   - DeadLetterExhausted：LogError（重试预算耗尽，需运维排查失败根因）。
//   - RecoveryDependencyUnavailable：LogWarning（依赖暂时不可用，自动退避重试；
//     仅首次告警，持续不可用时依赖 LogWarning 级日志由运维巡检发现）。
//
// 生产环境应替换为 PagerDuty / Slack / 邮件等真实通道（TryAddSingleton 不覆盖
// 调用方已注册的 IRecoveryAlertSink 实现）。
// ===========================================================================

/// <summary>
/// 默认人工介入告警接收器：将 <see cref="AgentRunAlert"/> 记录到 ILogger。
/// </summary>
public sealed class LoggingRecoveryAlertSink : IRecoveryAlertSink
{
    private readonly ILogger<LoggingRecoveryAlertSink> _logger;

    public LoggingRecoveryAlertSink(ILogger<LoggingRecoveryAlertSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask NotifyInterventionRequiredAsync(AgentRunAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // 数据损坏 / 死信级告警 → LogError；依赖不可用（可自动恢复）→ LogWarning。
        if (alert.Kind == AgentRunAlertKind.RecoveryDependencyUnavailable)
        {
            _logger.LogWarning(
                "人工介入告警（{Kind}）：run={RunId}，workspace={WorkspaceId}，session={SessionId}，attempt={Attempt}。{Reason}",
                alert.Kind, alert.RunId, alert.WorkspaceId, alert.SessionId, alert.Attempt, alert.Reason);
        }
        else
        {
            _logger.LogError(
                "人工介入告警（{Kind}）：run={RunId}，workspace={WorkspaceId}，session={SessionId}，attempt={Attempt}。{Reason}",
                alert.Kind, alert.RunId, alert.WorkspaceId, alert.SessionId, alert.Attempt, alert.Reason);
        }
        return ValueTask.CompletedTask;
    }
}
