using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// P0-3 / Perf-4：DefaultAgentModelContextProjector — Agent 模型上下文默认投影器
//
// 修复问题：
//   旧路径中 Actor 调用 IContextDecisionRuntime 后仅将 CandidateId/Type/FinalScore
//   摘要追加为 System 消息，模型拿不到材料正文。此外 ProjectForModel(tokenBudget: 0)
//   等价于关闭预算控制。
//
// 本投影器在投影阶段从 ContextDecisionExecutionResult.WorkingSet.Materials 取出候选
// 正文内容，并按 Run.ModelContextTokenBudget 截断。
//
// P0-1 修复后的投影顺序（高优先级在前）：
//   1. System Prompt（可信系统指令，System 角色）
//   2. Hard Constraints（硬约束，System 角色）
//   3. Current Task（当前任务，User 角色）
//   4. Retrieved Materials（检索材料，User 角色 + [untrusted_data] 标记；best-fit 策略）
//   5. Conversation（对话历史，按原子协议单元保序裁剪）
//
// P0-1 修复要点：
//   a. 引入 AgentContextState.Conversation 统一对话流，按时间顺序存储 Assistant + Tool 消息。
//   b. AssistantToolCallTurn（含 ToolCalls）与对应 ToolResultTurn 作为不可拆分协议单元：
//      预算不足时整体截断，不拆分 Assistant 与 Tool 消息——保持 "assistant tool_calls → tool result"
//      因果顺序（OpenAI/Anthropic function calling 协议要求）。
//   c. EstimateMessageTokens 扩展：包含 ToolCalls 的 Id/Name/Arguments 开销，
//      不再仅算 Content.Length（Content 为空但 ToolCalls 非空的消息不再算作 0 token）。
//   d. Conversation 为空时回退到 Messages + ToolObservations 分离投影（向后兼容）。
//
// Perf-4 保留要点：
//   - Retrieved Materials 不放入 System 角色（避免提示注入），改为 User 角色 + untrusted_data 标记。
//   - Retrieved Materials 使用 best-fit 策略：按 FinalScore 排序后逐个尝试纳入。
// ===========================================================================

/// <summary>
/// P0-3 / Perf-4：Agent 模型上下文默认投影器。
/// 从 ContextDecisionExecutionResult.WorkingSet.Materials 取出候选正文内容，
/// 按 Run.ModelContextTokenBudget 截断投影为最终发送给模型的消息列表。
/// </summary>
public sealed class DefaultAgentModelContextProjector : IAgentModelContextProjector
{
    /// <summary>
    /// Perf-4：Retrieved Materials 的 section 标记前缀。
    /// 标记为 untrusted_data 让模型区分可信系统指令与外部检索数据，降低提示注入风险。
    /// </summary>
    private const string UntrustedDataSectionMarker = "[untrusted_data]";

    /// <inheritdoc />
    public AgentModelContextProjection Project(
        AgentRun run,
        ContextDecisionExecutionResult? decisionResult,
        AgentContextState context,
        int modelContextTokenBudget)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);

        var projected = new List<AgentMessage>();
        var selectedMaterialIds = new HashSet<string>(StringComparer.Ordinal);
        var budget = modelContextTokenBudget > 0 ? modelContextTokenBudget : 0;
        var usedTokens = 0;
        var truncated = false;

        // 1. System Prompt（可信系统指令，高优先级，总是保留）
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var msg = new AgentMessage { Role = AgentMessageRole.System, Content = context.SystemPrompt };
            projected.Add(msg);
            usedTokens += EstimateTokens(msg.Content);
        }

        // 2. Hard Constraints（硬约束，高优先级，总是保留）
        if (!string.IsNullOrEmpty(context.Constraints))
        {
            var msg = new AgentMessage { Role = AgentMessageRole.System, Content = $"[Constraints]\n{context.Constraints}" };
            projected.Add(msg);
            usedTokens += EstimateTokens(msg.Content);
        }

        // 3. Current Task（当前任务，高优先级，总是保留）
        if (!string.IsNullOrEmpty(context.CurrentTask))
        {
            var msg = new AgentMessage { Role = AgentMessageRole.User, Content = context.CurrentTask };
            projected.Add(msg);
            usedTokens += EstimateTokens(msg.Content);
        }

        // 4. Retrieved Materials（检索材料，User 角色 + [untrusted_data] 标记）
        // P0-1：Retrieved Materials 移到 Conversation 之前——作为检索上下文注入，
        // 不破坏对话历史中 "assistant tool_calls → tool result" 的因果顺序。
        // Perf-4 修复 a：不放入 System 角色（避免提示注入），改为 User 角色 + untrusted_data 标记。
        // Perf-4 修复 c：best-fit 策略——按 FinalScore 排序后逐个尝试纳入，
        //   某个材料太大时跳过该材料继续尝试下一个（不直接 break），让更小的相关材料仍能进入上下文。
        if (decisionResult is not null)
        {
            var selected = decisionResult.Decision.SelectedEnvelopes;
            var materials = decisionResult.WorkingSet.Materials;

            // 按 FinalScore 降序排序（最相关的优先尝试）
            var ordered = new List<ContextCandidateEnvelope>(selected.Count);
            ordered.AddRange(selected);
            ordered.Sort((a, b) =>
            {
                var sa = a.Utility?.FinalScore ?? 0.0;
                var sb = b.Utility?.FinalScore ?? 0.0;
                // 降序：sb 对比 sa
                return sb.CompareTo(sa);
            });

            foreach (var env in ordered)
            {
                var content = TryGetMaterialContent(env, materials, out var materialId);
                if (content is null)
                {
                    // Material 不可用 — 降级为摘要（与旧路径兼容，但标记未取到正文）
                    var score = env.Utility?.FinalScore ?? 0;
                    content = $"- [{env.Type}] {env.CandidateId} (score={score:F3}) [content unavailable]";
                }
                else if (materialId is not null)
                {
                    selectedMaterialIds.Add(materialId);
                }

                // Perf-4：User 角色 + [untrusted_data] section 标记（替代旧的 System 角色）
                var prefix = $"{UntrustedDataSectionMarker}\n[RetrievedContext:{env.Type}]\n";
                var msg = new AgentMessage
                {
                    Role = AgentMessageRole.User,
                    Content = prefix + content
                };

                // Perf-2（精确 tokenize → Model Projection）：hydrated material 的 TokenCost 已由
                // ISelectedCandidateHydrator 用 tokenizer 精确重算（P3 Fix-5），正文 token 直接复用精确值，
                // 仅对固定包装开销（[untrusted_data]/[RetrievedContext:type]）做长度估算；
                // 无精确值（测试 stub / 降级路径 / 正文缺失）时回退到整体长度估算（与旧行为一致）。
                var tokens = TryGetExactMaterialTokens(env, materials, out var exactContentTokens)
                    ? exactContentTokens + EstimateTokens(prefix)
                    : EstimateMessageTokens(msg);
                if (budget > 0 && usedTokens + tokens > budget)
                {
                    // Perf-4 修复 c：best-fit — 当前材料太大时跳过，继续尝试下一个更小的材料（不 break）
                    truncated = true;
                    continue;
                }
                projected.Add(msg);
                usedTokens += tokens;
            }
        }

        // 5. Conversation（对话历史，按原子协议单元保序裁剪）
        // P0-1：从 context.Conversation 按时间顺序投影，保持 "assistant tool_calls → tool result" 因果顺序。
        // AssistantToolCallTurn（含 ToolCalls）与对应的 ToolResultTurn 作为不可拆分协议单元：
        // 预算不足时整体截断，不拆分 Assistant 与 Tool 消息。
        // Conversation 为空时回退到 Messages + ToolObservations 分离投影（向后兼容旧路径）。
        if (context.Conversation.Count > 0)
        {
            ProjectConversation(context.Conversation, budget, ref projected, ref usedTokens, ref truncated);
        }
        else
        {
            // 回退路径：Conversation 为空（旧路径或恢复路径未重建）——分离投影 Messages + ToolObservations
            ProjectLegacyConversation(context, budget, ref projected, ref usedTokens, ref truncated);
        }

        return new AgentModelContextProjection
        {
            Messages = projected,
            TotalTokens = usedTokens,
            SelectedMaterialIds = selectedMaterialIds,
            TruncationDiagnostics = truncated
                ? $"Projected {projected.Count} messages, {usedTokens} tokens (budget={budget}). Some content truncated."
                : null
        };
    }

    // ── P0-1：Conversation 原子协议单元投影 ──────────────────────────────

    /// <summary>
    /// P0-1：从 Conversation 按原子协议单元保序裁剪。
    /// AssistantToolCallTurn（含 ToolCalls）与紧随其后的 ToolResultTurn 作为一个不可拆分单元：
    /// 预算不足时整体截断，不拆分 Assistant 与 Tool 消息。
    /// </summary>
    private static void ProjectConversation(
        List<AgentMessage> conversation,
        int budget,
        ref List<AgentMessage> projected,
        ref int usedTokens,
        ref bool truncated)
    {
        // 预处理：识别 turn 边界
        // AssistantToolCallTurn = Assistant(含 ToolCalls) + 紧随其后的所有 Tool 消息
        // 其他消息（Assistant text / User / 孤立 Tool）各自成单独 turn
        var turns = new List<(int Start, int Count, int Tokens)>();
        var i = 0;
        while (i < conversation.Count)
        {
            var msg = conversation[i];
            if (msg.Role == AgentMessageRole.Assistant && msg.ToolCalls is { Count: > 0 })
            {
                // Assistant ToolCall turn：包含此 Assistant + 紧随其后的所有 Tool 消息
                var start = i;
                var count = 1;
                i++;
                while (i < conversation.Count
                       && conversation[i].Role == AgentMessageRole.Tool)
                {
                    count++;
                    i++;
                }
                // 计算此 turn 的总 token 数（含 ToolCalls 开销）
                var turnTokens = 0;
                for (var j = start; j < start + count; j++)
                {
                    turnTokens += EstimateMessageTokens(conversation[j]);
                }
                turns.Add((start, count, turnTokens));
            }
            else
            {
                // 单独 turn（Assistant text / User / 孤立 Tool）
                turns.Add((i, 1, EstimateMessageTokens(msg)));
                i++;
            }
        }

        if (budget <= 0)
        {
            // 无预算限制 — 全量返回（按时间顺序）
            for (var t = 0; t < turns.Count; t++)
            {
                var turn = turns[t];
                for (var j = turn.Start; j < turn.Start + turn.Count; j++)
                {
                    projected.Add(conversation[j]);
                    usedTokens += EstimateMessageTokens(conversation[j]);
                }
            }
            return;
        }

        // 有预算限制 — 从最新 turn 向最旧 turn 纳入（chronological break）
        var remaining = Math.Max(0, budget - usedTokens);
        var recentTurns = new List<AgentMessage>();
        for (var t = turns.Count - 1; t >= 0; t--)
        {
            var turn = turns[t];
            if (remaining < turn.Tokens)
            {
                // 原子单元不可拆分 — break（不纳入更旧的 turn）
                if (t > 0 || recentTurns.Count == 0)
                {
                    truncated = true;
                }
                break;
            }
            // 将此 turn 的消息插入到 recentTurns 开头（保持时间顺序）
            for (var j = turn.Start + turn.Count - 1; j >= turn.Start; j--)
            {
                recentTurns.Insert(0, conversation[j]);
            }
            remaining -= turn.Tokens;
        }
        projected.AddRange(recentTurns);
        usedTokens += budget - usedTokens - remaining; // 已使用的 token 数
    }

    /// <summary>
    /// P0-1：回退路径——Conversation 为空时从 Messages + ToolObservations 分离投影（向后兼容）。
    /// 注意：此路径不保证 "assistant tool_calls → tool result" 因果顺序，
    /// 仅用于未填充 Conversation 的旧路径或恢复路径。
    /// </summary>
    private static void ProjectLegacyConversation(
        AgentContextState context,
        int budget,
        ref List<AgentMessage> projected,
        ref int usedTokens,
        ref bool truncated)
    {
        // Tool Observations（Tool 角色，按预算从最新向最旧纳入）
        if (budget <= 0)
        {
            foreach (var obs in context.ToolObservations)
            {
                var msg = obs.ToAgentMessage();
                projected.Add(msg);
                usedTokens += EstimateMessageTokens(msg);
            }
        }
        else
        {
            var remaining = Math.Max(0, budget - usedTokens);
            var recentTools = new List<AgentMessage>();
            for (var i = context.ToolObservations.Count - 1; i >= 0; i--)
            {
                var obs = context.ToolObservations[i];
                var msg = obs.ToAgentMessage();
                var tokens = EstimateMessageTokens(msg);
                if (remaining < tokens)
                {
                    if (i > 0 || recentTools.Count == 0)
                    {
                        truncated = true;
                    }
                    break;
                }
                recentTools.Insert(0, msg);
                remaining -= tokens;
            }
            projected.AddRange(recentTools);
            usedTokens += budget - usedTokens - remaining;
        }

        // Messages（Assistant，按预算从最新向最旧纳入）
        if (budget <= 0)
        {
            foreach (var msg in context.Messages)
            {
                projected.Add(msg);
                usedTokens += EstimateMessageTokens(msg);
            }
        }
        else
        {
            var remaining = Math.Max(0, budget - usedTokens);
            var recent = new List<AgentMessage>();
            for (var i = context.Messages.Count - 1; i >= 0; i--)
            {
                var msg = context.Messages[i];
                var tokens = EstimateMessageTokens(msg);
                if (remaining < tokens)
                {
                    if (i > 0 || recent.Count == 0)
                    {
                        truncated = true;
                    }
                    break;
                }
                recent.Insert(0, msg);
                remaining -= tokens;
            }
            projected.AddRange(recent);
            usedTokens += budget - usedTokens - remaining;
        }
    }

    /// <summary>
    /// 从 WorkingSet.Materials 中按 envelope.CanonicalKey 查找候选正文。
    /// </summary>
    private static string? TryGetMaterialContent(
        ContextCandidateEnvelope envelope,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> materials,
        out string? materialId)
    {
        materialId = null;
        if (materials.TryGetValue(envelope.CanonicalKey, out var material))
        {
            materialId = envelope.CandidateId;
            return material.Content;
        }
        return null;
    }

    /// <summary>
    /// Perf-2（精确 tokenize → Model Projection）：尝试读取 material 的精确 token 数。
    /// 仅当正文非空且 TokenCost 存在（ISelectedCandidateHydrator hydrate 后用 tokenizer 精确重算，
    /// 或摄取阶段持久化的精确 cost）时返回 true；测试 stub / 降级路径返回 false，调用方回退到长度估算。
    /// </summary>
    private static bool TryGetExactMaterialTokens(
        ContextCandidateEnvelope envelope,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> materials,
        out int exactTokens)
    {
        exactTokens = 0;
        if (materials.TryGetValue(envelope.CanonicalKey, out var material)
            && !string.IsNullOrEmpty(material.Content)
            && material.TokenCost is { ContentTokens: >= 0 })
        {
            exactTokens = material.TokenCost.ContentTokens;
            return true;
        }
        return false;
    }

    /// <summary>字符估算 token 数（与 AgentContextState.EstimateTokens 对齐：Max(1, (length+1)/2)）。</summary>
    private static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }
        return Math.Max(1, (content.Length + 1) / 2);
    }

    /// <summary>
    /// P0-1：估算单条消息的 token 数，包含 Content + ToolCalls（Id/Name/Arguments）+ ToolName/ToolCallId 开销。
    /// 旧路径仅算 Content.Length，导致 Content 为空但 ToolCalls 非空的 Assistant 消息算作 0 token。
    /// </summary>
    private static int EstimateMessageTokens(AgentMessage msg)
    {
        var tokens = EstimateTokens(msg.Content);

        // ToolCalls 开销（Assistant 消息的 function calling 参数）
        if (msg.ToolCalls is { Count: > 0 })
        {
            for (var i = 0; i < msg.ToolCalls.Count; i++)
            {
                var tc = msg.ToolCalls[i];
                tokens += EstimateTokens(tc.Id);
                tokens += EstimateTokens(tc.Name);
                tokens += EstimateTokens(tc.Arguments);
            }
        }

        // Tool 消息的协议字段开销
        if (!string.IsNullOrEmpty(msg.ToolName))
        {
            tokens += EstimateTokens(msg.ToolName);
        }
        if (!string.IsNullOrEmpty(msg.ToolCallId))
        {
            tokens += EstimateTokens(msg.ToolCallId);
        }

        return tokens;
    }
}
