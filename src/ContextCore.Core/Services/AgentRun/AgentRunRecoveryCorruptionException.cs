namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// 恢复时检测到事件流损坏的异常。
/// </summary>
/// <remarks>
/// 仅在 <see cref="AgentRunActor"/> 的恢复读取路径中抛出，用于区分「事件数据损坏」（哈希链断裂 / 序列号不连续 /
/// ContentHash 重算不匹配）与「事件存储不可用」两类恢复失败：
/// 前者将 Run 标记为 <see cref="ContextCore.Abstractions.AgentRunState.RecoveryCorrupted"/>，
/// 后者标记为 <see cref="ContextCore.Abstractions.AgentRunState.RecoveryDependencyUnavailable"/>。
/// </remarks>
internal sealed class AgentRunRecoveryCorruptionException : InvalidOperationException
{
    public AgentRunRecoveryCorruptionException(string message)
        : base(message)
    {
    }
}
