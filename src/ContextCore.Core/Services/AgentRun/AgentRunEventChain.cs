using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// AgentRunEventChain — Agent Run 事件哈希链工具
//
// 复用 DefaultAgentCheckpointFactory 的 ComputeContentHash / VerifyContentHash 模式：
// - ContentHash = SHA-256(序列化 payload，ContentHash=null)
// - PrevChainHash = 前一个事件的 ContentHash（链头为 null）
// - Sequence = 单调递增序列号（从 0 开始）
// 校验：
// - 读取时重算 ContentHash 比对；
// - PrevChainHash 与前一事件 ContentHash 比对；
// - Sequence 连续性校验（0,1,2,...）。
// ===========================================================================

/// <summary>
/// Agent Run 事件哈希链工具（静态方法）。
/// 复用 <see cref="ContextCore.Core.Services.AgentKernel.DefaultAgentCheckpointFactory"/> 的 SHA-256 哈希链模式。
/// </summary>
public static class AgentRunEventChain
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 与 DefaultAgentCheckpointFactory 一致：默认 PascalCase 序列化
        WriteIndented = false
    };

    /// <summary>
    /// 计算事件的 ContentHash（SHA-256）。
    /// 计算时 ContentHash 字段视为 null，排除自身参与哈希。
    /// </summary>
    /// <param name="event">待计算的事件（ContentHash 字段不参与计算）。</param>
    /// <returns>小写 hex 编码的 SHA-256 哈希（64 字符）。</returns>
    public static string ComputeContentHash(AgentRunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var dto = new EventHashDto
        {
            EventId = @event.EventId,
            RunId = @event.RunId,
            WorkspaceId = @event.WorkspaceId,
            Sequence = @event.Sequence,
            EventType = @event.EventType,
            State = @event.State,
            Payload = @event.Payload,
            PrevChainHash = @event.PrevChainHash,
            OccurredAt = @event.OccurredAt
            // ContentHash 显式不参与
        };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 校验事件 ContentHash 一致性（读取时重算哈希比对存储值）。
    /// </summary>
    /// <param name="event">待校验的事件（含存储的 ContentHash）。</param>
    /// <returns>校验通过返回 true；ContentHash 缺失或与重算值不匹配返回 false。</returns>
    public static bool VerifyContentHash(AgentRunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (string.IsNullOrEmpty(@event.ContentHash))
        {
            // 无 ContentHash（旧事件或未计算）→ 视为不通过（强制写入时计算）
            return false;
        }

        var computed = ComputeContentHash(@event);
        return string.Equals(computed, @event.ContentHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// 构建一个事件并自动计算 ContentHash。
    /// </summary>
    /// <param name="runId">所属 Run ID。</param>
    /// <param name="workspaceId">Workspace ID（隔离边界）。</param>
    /// <param name="sequence">事件序列号（同一 Run 内单调递增，从 0 开始）。</param>
    /// <param name="type">事件类型。</param>
    /// <param name="state">事件发生时的 Run 状态快照。</param>
    /// <param name="payload">事件负载（JSON 字符串）。</param>
    /// <param name="prevChainHash">前一个事件的 ContentHash（链头为 null）。</param>
    /// <returns>已计算 ContentHash 的 <see cref="AgentRunEvent"/>。</returns>
    public static AgentRunEvent BuildEvent(
        string runId,
        string workspaceId,
        int sequence,
        AgentRunEventType type,
        AgentRunState state,
        string payload,
        string? prevChainHash)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("runId 不能为空。", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("workspaceId 不能为空。", nameof(workspaceId));
        }

        payload ??= string.Empty;

        // 先构造不含 ContentHash 的临时事件，再计算 ContentHash
        var temp = new AgentRunEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            RunId = runId,
            WorkspaceId = workspaceId,
            Sequence = sequence,
            EventType = type,
            State = state,
            Payload = payload,
            ContentHash = null,
            PrevChainHash = prevChainHash,
            OccurredAt = DateTimeOffset.UtcNow
        };

        var contentHash = ComputeContentHash(temp);

        return temp with { ContentHash = contentHash };
    }

    /// <summary>
    /// 校验整条事件链的完整性：
    /// <list type="bullet">
    /// <item>Sequence 连续性（从 0 开始，无间断）。</item>
    /// <item>PrevChainHash 链接（链头为 null；其余指向前一事件 ContentHash）。</item>
    /// <item>ContentHash 完整性（重算与存储值一致）。</item>
    /// </list>
    /// </summary>
    /// <param name="events">按 Sequence 升序排列的事件列表。</param>
    /// <returns>校验通过返回 true；空列表返回 true；任一校验失败返回 false。</returns>
    public static bool VerifyChain(IReadOnlyList<AgentRunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return true;
        }

        string? previousContentHash = null;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];

            // 1. Sequence 连续性（必须从 0 开始递增）
            if (e.Sequence != i)
            {
                return false;
            }

            // 2. PrevChainHash 链接（链头为 null；其余必须等于前一事件 ContentHash）
            if (i == 0)
            {
                if (e.PrevChainHash is not null)
                {
                    return false;
                }
            }
            else
            {
                if (!string.Equals(previousContentHash, e.PrevChainHash, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // 3. ContentHash 完整性
            if (!VerifyContentHash(e))
            {
                return false;
            }

            previousContentHash = e.ContentHash;
        }

        return true;
    }

    /// <summary>
    /// 哈希计算用 DTO（不含 ContentHash 字段，确保序列化排除该字段）。
    /// 字段顺序与 <see cref="AgentRunEvent"/> 一致，仅去掉 ContentHash。
    /// </summary>
    private sealed record EventHashDto
    {
        public required string EventId { get; init; }
        public required string RunId { get; init; }
        public required string WorkspaceId { get; init; }
        public required int Sequence { get; init; }
        public required AgentRunEventType EventType { get; init; }
        public required AgentRunState State { get; init; }
        public required string Payload { get; init; }
        public string? PrevChainHash { get; init; }
        public required DateTimeOffset OccurredAt { get; init; }
    }
}
