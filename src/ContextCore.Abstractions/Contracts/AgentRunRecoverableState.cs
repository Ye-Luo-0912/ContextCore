using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextCore.Abstractions;

// ===========================================================================
// 可恢复快照状态（正式方案：Recoverable Snapshot + Anchor + Hot Delta）
// 
// 折叠前缀 [0..anchor] 的事件在压缩时归档到 agent_run_events_archive 并从热表删除，
// 热表只保留锚点事件 + 之后的增量。Recovery 不能再依赖"从 Sequence 0 全量重放"，
// 因此压缩器把折叠前缀重建出的完整执行状态序列化为快照 state_json：
// 
//   Snapshot（本记录）→ validate anchor（热表锚点 ContentHash == ChainHeadHash）
//   → replay hot delta（重放 sequence > Sequence 的热表增量）
// 
// 覆盖范围（快照清单）说明：
// - Conversation / Tool Observations / ExecutionModelTurn / Pending Tool Commands：
//   直接存入本快照（由 AgentRunEventStateRebuilder.Rebuild 从折叠事件重建）。
// - Budget（TurnBudget / CostBudget / ModelCallsUsed）：从 Run 元数据恢复，不重复存储。
// - Pending Approval / Reconciliation：由审批存储 / 对账记录存储各自持久化，恢复时
//   由对应 Store 查询，不重复存储。
// - Last Model Turn：恢复路径统一规范化为 ContextBuilding + LastModelTurn=null
//   （强制重新调用模型），与 checkpoint 快路径语义一致，不重复存储。
// - Event Sequence / Hash：本记录的 Sequence / ChainHeadHash（= 锚点事件）。
// - Checkpoint identity：压缩与 checkpoint 正交，Checkpoint Cursor 仍从 agent_runs
//   恢复（cursor 指向已归档事件时，快照路径会覆盖 cursor 起点）。
// ===========================================================================

/// <summary>
/// Run 事件流压缩后保留的可恢复状态（Recoverable Snapshot）。
/// </summary>
/// <remarks>
/// 覆盖折叠前缀 [0..<see cref="Sequence"/>]（含锚点）重建出的完整执行状态；
/// Recovery 以它为基准，再重放 sequence &gt; <see cref="Sequence"/> 的热表增量事件。
/// 由 <see cref="AgentRunEventStateRebuilder.Rebuild"/> 从折叠事件生成，
/// 经 <see cref="AgentRunEventStateRebuilder.Serialize"/> 存入
/// <c>agent_run_event_snapshots.state_json</c>。
/// </remarks>
public sealed record AgentRunRecoverableState
{
    /// <summary>折叠覆盖的最后事件 sequence（含）；增量重放从 Sequence+1 开始。</summary>
    public required int Sequence { get; init; }

    /// <summary>
    /// 折叠覆盖最后事件的 ContentHash（链头；增量首事件的 PrevChainHash 必须等于它）。
    /// required：与 <see cref="Sequence"/> 共同拒绝旧格式快照（仅序列化锚点事件，
    /// 不含可恢复状态成员）——旧格式 JSON 即使命中 Sequence 也会因缺少本成员解析失败。
    /// </summary>
    public required string? ChainHeadHash { get; init; }

    /// <summary>折叠事件重建的完整对话流（Assistant + Tool 消息，按时间顺序）。
    /// required：拒绝旧格式/部分快照（空但合法的快照会显式序列化空数组，与缺失区分）。</summary>
    public required List<AgentMessage> Conversation { get; init; }

    /// <summary>折叠事件重建的 Tool 观察结果（按时间顺序）。required：同上。</summary>
    public required List<ToolObservation> ToolObservations { get; init; }

    /// <summary>折叠事件重建的模型轮次（_executionModelTurn 绝对计数；增量内嵌更高轮次时取高）。required：同上。</summary>
    public required int ExecutionModelTurn { get; init; }

    /// <summary>
    /// 折叠前缀中最后一个 ApprovalRequested 事件提取的 PendingToolCommands
    /// （审批恢复用；折叠前缀无审批事件时为 null）。
    /// </summary>
    public List<PendingToolCommand>? PendingToolCommands { get; init; }
}

/// <summary>
/// Agent Run 事件流状态重建器（事件 → 可恢复状态的单一事实来源）。
/// </summary>
/// <remarks>
/// 被三处复用，保证重建语义一致：
/// <list type="bullet">
/// <item><c>ContextCore.Core.Services.AgentRun.AgentRunActor</c> 崩溃恢复全量重放 / checkpoint 快路径
/// / 快照快路径（原私有静态方法，抽取到此处共享）；</item>
/// <item><c>ContextCore.Storage.Postgres.Stores.PostgresAgentRunEventCompactor</c> 压缩时
/// 从折叠前缀重建可恢复状态并序列化进快照；</item>
/// <item>测试直接验证重建/序列化往返。</item>
/// </list>
/// <see cref="Rebuild"/> 要求事件按 <see cref="AgentRunEvent.Sequence"/> 升序传入
/// （压缩器按 <c>ORDER BY sequence ASC</c> 读取；Actor 重放按 sequence 递增读取）。
/// </remarks>
public static class AgentRunEventStateRebuilder
{
    /// <summary>
    /// 快照 state_json 的序列化选项：与 <c>PostgresJsonSerializer</c> 对齐
    /// （枚举序列化为字符串 + 忽略 null），保证压缩器写入与 Actor 读取往返一致。
    /// </summary>
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 从折叠事件前缀 [0..最后事件] 重建可恢复状态（含对话流 / 工具观察 / 模型轮次 / Pending 命令）。
    /// </summary>
    /// <param name="events">按 Sequence 升序排列的事件流（非空）。</param>
    /// <returns>覆盖全部传入事件的可恢复状态。</returns>
    public static AgentRunRecoverableState Rebuild(IReadOnlyList<AgentRunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            throw new ArgumentException("事件流为空，无法构建可恢复状态。", nameof(events));
        }

        var conversation = new List<AgentMessage>();
        var toolObservations = new List<ToolObservation>();
        foreach (var evt in events)
        {
            RebuildFromEvent(evt, conversation, toolObservations);
        }

        var last = events[events.Count - 1];
        return new AgentRunRecoverableState
        {
            Sequence = last.Sequence,
            ChainHeadHash = last.ContentHash,
            Conversation = conversation,
            ToolObservations = toolObservations,
            ExecutionModelTurn = RebuildExecutionModelTurn(events),
            PendingToolCommands = ExtractPendingToolCommands(events)
        };
    }

    /// <summary>
    /// 序列化可恢复状态为快照 state_json（与 <see cref="TryDeserialize"/> 往返一致）。
    /// </summary>
    public static string Serialize(AgentRunRecoverableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(state, SnapshotJsonOptions);
    }

    /// <summary>
    /// 尝试反序列化快照 state_json 为可恢复状态。
    /// </summary>
    /// <returns>解析成功返回状态；空 / JSON 损坏 / 旧格式（仅序列化锚点事件）返回 null
    /// （调用方降级为现有恢复路径；压缩过的热表会 fail-closed 判定 RecoveryCorrupted）。</returns>
    public static AgentRunRecoverableState? TryDeserialize(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentRunRecoverableState>(stateJson, SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 将单个事件追加到重建的对话流与工具观察（全量重放 / checkpoint 快路径 / 快照快路径共用）。
    /// ModelCallCompleted → Assistant 消息；ToolCallCompleted → ToolObservation + Tool 消息。
    /// 旧事件缺少字段时跳过对应重建（向后兼容）；单事件解析失败不影响整体恢复。
    /// </summary>
    public static void RebuildFromEvent(
        AgentRunEvent evt,
        List<AgentMessage> conversation,
        List<ToolObservation> toolObservations)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(toolObservations);

        if (evt.EventType == AgentRunEventType.ModelCallCompleted)
        {
            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                var root = doc.RootElement;
                // 旧事件无 content 字段 → 跳过（向后兼容）
                if (!root.TryGetProperty("content", out var contentProp))
                {
                    return;
                }

                var content = contentProp.GetString() ?? string.Empty;
                List<AgentToolCallEntry>? toolCalls = null;
                if (root.TryGetProperty("toolCalls", out var tcArrayEl)
                    && tcArrayEl.ValueKind == JsonValueKind.Array
                    && tcArrayEl.GetArrayLength() > 0)
                {
                    toolCalls = new List<AgentToolCallEntry>(tcArrayEl.GetArrayLength());
                    foreach (var tcEl in tcArrayEl.EnumerateArray())
                    {
                        toolCalls.Add(new AgentToolCallEntry
                        {
                            Id = tcEl.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty,
                            Name = tcEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
                            Arguments = tcEl.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? string.Empty : string.Empty
                        });
                    }
                }

                // 仅当有内容或 ToolCalls 时才追加（避免空消息污染对话流）
                if (!string.IsNullOrEmpty(content) || toolCalls is { Count: > 0 })
                {
                    conversation.Add(new AgentMessage
                    {
                        Role = AgentMessageRole.Assistant,
                        Content = content,
                        ToolCalls = toolCalls
                    });
                }
            }
            catch
            {
                // 解析单个事件失败 → 跳过（不影响整体恢复）
            }
        }
        else if (evt.EventType == AgentRunEventType.ToolCallCompleted)
        {
            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("succeeded", out var succeededProp))
                {
                    return;
                }

                var succeeded = succeededProp.GetBoolean();
                var toolName = root.TryGetProperty("toolName", out var tnProp) ? tnProp.GetString() ?? string.Empty : string.Empty;
                var toolCallId = root.TryGetProperty("toolCallId", out var tcProp) ? tcProp.GetString() : null;
                var output = root.TryGetProperty("output", out var outProp) ? outProp.GetString() : null;
                var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;

                var obs = new ToolObservation
                {
                    ToolName = toolName,
                    ToolCallId = toolCallId,
                    Result = output,
                    Error = error,
                    Succeeded = succeeded
                };
                toolObservations.Add(obs);
                conversation.Add(obs.ToAgentMessage());
            }
            catch
            {
                // 解析单个事件失败 → 跳过（不影响整体恢复）
            }
        }
    }

    /// <summary>
    /// 从事件流统计 ModelCallCompleted 重建 _executionModelTurn。
    /// 优先读取事件内嵌的 executionModelTurn（新事件，绝对计数，取最大值）；
    /// 旧事件无此字段时降级为计数（相对 <paramref name="initialValue"/> 递增）。
    /// </summary>
    /// <param name="events">按 Sequence 升序的事件流。</param>
    /// <param name="initialValue">重建起点（快照/checkpoint 路径传入已折叠的模型轮次；全量重放默认 0）。</param>
    public static int RebuildExecutionModelTurn(IReadOnlyList<AgentRunEvent> events, int initialValue = 0)
    {
        var rebuiltModelTurn = initialValue;
        foreach (var evt in events)
        {
            if (evt.EventType != AgentRunEventType.ModelCallCompleted)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                if (doc.RootElement.TryGetProperty("executionModelTurn", out var emtEl)
                    && emtEl.ValueKind == JsonValueKind.Number)
                {
                    var v = emtEl.GetInt32();
                    if (v > rebuiltModelTurn) { rebuiltModelTurn = v; }
                }
                else
                {
                    // 旧事件无此字段 — 降级为计数（与原 _executionModelTurn 递增语义一致）
                    rebuiltModelTurn++;
                }
            }
            catch
            {
                // 解析失败 — 降级为计数
                rebuiltModelTurn++;
            }
        }
        return rebuiltModelTurn;
    }

    /// <summary>
    /// 从事件流中提取最后一个 ApprovalRequested 事件的 PendingToolCommands 列表。
    /// 审批通过后恢复时，Actor 据此依次执行所有 Pending Tool Call（不依赖模型重生成）。
    /// 兼容旧版单数 pendingToolCommand payload（旧版本的事件）。
    /// </summary>
    /// <param name="events">Run 的事件流（按 Sequence 升序）。</param>
    /// <returns>提取的 PendingToolCommands 列表；事件 payload 损坏/无 ApprovalRequested 事件时返回 null。</returns>
    public static List<PendingToolCommand>? ExtractPendingToolCommands(IReadOnlyList<AgentRunEvent> events)
    {
        // 从后往前找最后一个 ApprovalRequested 事件
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.EventType != AgentRunEventType.ApprovalRequested)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                var root = doc.RootElement;

                // 优先读取 pendingToolCommands（数组），兼容旧版 pendingToolCommand（单数）
                if (root.TryGetProperty("pendingToolCommands", out var ptcsProp) && ptcsProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<PendingToolCommand>();
                    foreach (var ptc in ptcsProp.EnumerateArray())
                    {
                        var cmd = ParsePendingToolCommand(ptc);
                        if (cmd is not null)
                        {
                            list.Add(cmd);
                        }
                    }
                    return list.Count > 0 ? list : null;
                }

                // 旧版事件 payload 仅有 pendingToolCommand（单数）→ 包装为单元素列表
                if (root.TryGetProperty("pendingToolCommand", out var ptcProp))
                {
                    var cmd = ParsePendingToolCommand(ptcProp);
                    return cmd is not null ? new List<PendingToolCommand> { cmd } : null;
                }

                // 旧版事件 payload 未携带 pendingToolCommand（旧版本）→ 无法恢复
                return null;
            }
            catch
            {
                // 解析失败 → 继续找更早的 ApprovalRequested 事件
            }
        }

        return null;
    }

    /// <summary>
    /// 从事件流中提取仍未完成的 Tool 调用（作为 PendingToolCommand 列表）。
    /// 非审批路径的 Tool 分派被进程 Kill 打断时（Run 停留在 ToolDispatching），
    /// 没有 ApprovalRequested 事件可提取；此时以最后一个 ToolCallCompleted 之后的
    /// 全部 ToolCallStarted 事件为准——它们携带 arguments + modelTurnRevision，
    /// 恢复节点据此重放原 Tool（原始轮次 → RequestId 与 journal 条目一致，
    /// durable 去重生效，不会重复执行外部副作用）。
    /// </summary>
    /// <param name="events">Run 的事件流（按 Sequence 升序）。</param>
    /// <returns>提取的 PendingToolCommands 列表；无可恢复的 ToolCallStarted 时返回 null。</returns>
    public static List<PendingToolCommand>? ExtractPendingCommandsFromToolCallStarted(IReadOnlyList<AgentRunEvent> events)
    {
        // 找到最后一个 ToolCallCompleted 的位置：其后所有 ToolCallStarted 都是未完成的调用。
        var lastCompletedIndex = -1;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].EventType == AgentRunEventType.ToolCallCompleted)
            {
                lastCompletedIndex = i;
                break;
            }
        }

        var result = new List<PendingToolCommand>();
        for (var i = lastCompletedIndex + 1; i < events.Count; i++)
        {
            var evt = events[i];
            if (evt.EventType != AgentRunEventType.ToolCallStarted)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                var root = doc.RootElement;
                var toolCallId = root.TryGetProperty("toolCallId", out var tciProp) ? tciProp.GetString() ?? string.Empty : string.Empty;
                var toolName = root.TryGetProperty("toolName", out var tnProp) ? tnProp.GetString() ?? string.Empty : string.Empty;
                var arguments = root.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? string.Empty : string.Empty;
                var idempotencyKey = root.TryGetProperty("idempotencyKey", out var ikProp) ? ikProp.GetString() : null;
                var requestId = root.TryGetProperty("requestId", out var ridProp) ? ridProp.GetString() : null;
                var modelTurnRevision = root.TryGetProperty("modelTurnRevision", out var mtrProp) && mtrProp.ValueKind == JsonValueKind.Number
                    ? mtrProp.GetInt32()
                    : 0;

                if (string.IsNullOrEmpty(toolCallId) && string.IsNullOrEmpty(toolName))
                {
                    continue;
                }

                result.Add(new PendingToolCommand
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    ArgumentsJson = arguments,
                    IdempotencyKey = idempotencyKey,
                    RequestId = requestId,
                    ModelTurnRevision = modelTurnRevision
                });
            }
            catch
            {
                // 单个事件解析失败不影响其他 ToolCallStarted
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 从 JSON 元素解析单个 PendingToolCommand。
    /// </summary>
    public static PendingToolCommand? ParsePendingToolCommand(JsonElement ptc)
    {
        var toolCallId = ptc.TryGetProperty("ToolCallId", out var tciProp) ? tciProp.GetString() ?? string.Empty : string.Empty;
        var toolName = ptc.TryGetProperty("ToolName", out var tnProp) ? tnProp.GetString() ?? string.Empty : string.Empty;
        var argumentsJson = ptc.TryGetProperty("ArgumentsJson", out var ajProp) ? ajProp.GetString() ?? string.Empty : string.Empty;
        var idempotencyKey = ptc.TryGetProperty("IdempotencyKey", out var ikProp) ? ikProp.GetString() : null;
        var requestId = ptc.TryGetProperty("RequestId", out var ridProp) ? ridProp.GetString() : null;
        var modelTurnRevision = ptc.TryGetProperty("ModelTurnRevision", out var mtrProp) && mtrProp.ValueKind == JsonValueKind.Number ? mtrProp.GetInt32() : 0;

        if (string.IsNullOrEmpty(toolCallId) && string.IsNullOrEmpty(toolName))
        {
            return null;
        }

        return new PendingToolCommand
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            IdempotencyKey = idempotencyKey,
            RequestId = requestId,
            ModelTurnRevision = modelTurnRevision
        };
    }
}
