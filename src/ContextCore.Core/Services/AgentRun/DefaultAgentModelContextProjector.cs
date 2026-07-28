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
// Perf-4 修复后的投影顺序（高优先级在前）：
//   1. System Prompt（可信系统指令，System 角色）
//   2. Hard Constraints（硬约束，System 角色）
//   3. Current Task（当前任务，User 角色）
//   4. Latest Tool Observations（最新工具观察，Tool 角色；按预算从最新向最旧纳入）
//   5. Retrieved Materials（检索材料，User 角色 + [untrusted_data] 标记；best-fit 策略）
//   6. Recent Assistant Turns（历史对话，Messages；按预算从最新向最旧纳入）
//
// Perf-4 修复要点：
//   a. Retrieved Materials 不再放入 System 角色（避免提示注入风险），改为 User 角色
//      并以 [untrusted_data] section 标记为不可信数据，让模型区分可信指令与外部数据。
//   b. Latest Tool Observations 优先级提升到 Retrieved Materials 之前（最新工具观察
//      比历史检索材料对当前决策更关键）。
//   c. Retrieved Materials 使用 best-fit 策略：按 FinalScore 排序后逐个尝试纳入，
//      某个材料太大时跳过该材料继续尝试下一个（不直接 break），让更小的相关材料仍能进入上下文。
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

        // 4. Latest Tool Observations（最新工具观察，Tool 角色）
        // Perf-4：优先级提升到 Retrieved Materials 之前——最新工具观察对当前决策比历史检索材料更关键。
        // 按预算从最新向最旧纳入（chronological break：最旧的最先被截断）。
        if (budget <= 0)
        {
            foreach (var obs in context.ToolObservations)
            {
                projected.Add(obs.ToAgentMessage());
                usedTokens += EstimateTokens(obs.Succeeded ? obs.Result : obs.Error);
            }
        }
        else
        {
            var remaining = Math.Max(0, budget - usedTokens);
            var recentTools = new List<AgentMessage>();
            for (var i = context.ToolObservations.Count - 1; i >= 0; i--)
            {
                var obs = context.ToolObservations[i];
                var content = obs.Succeeded ? obs.Result : obs.Error;
                var tokens = EstimateTokens(content);
                if (remaining < tokens)
                {
                    // 时间序列数据：旧观察对当前决策价值递减，break 而非 skip
                    //（避免跳过最新观察却纳入更旧的观察，破坏时序一致性）
                    if (i > 0 || recentTools.Count == 0)
                    {
                        truncated = true;
                    }
                    break;
                }
                recentTools.Insert(0, obs.ToAgentMessage());
                remaining -= tokens;
            }
            projected.AddRange(recentTools);
            usedTokens += EstimateTokens(recentTools);
        }

        // 5. Retrieved Materials（检索材料，User 角色 + [untrusted_data] 标记）
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
                var msg = new AgentMessage
                {
                    Role = AgentMessageRole.User,
                    Content = $"{UntrustedDataSectionMarker}\n[RetrievedContext:{env.Type}]\n{content}"
                };

                var tokens = EstimateTokens(msg.Content);
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

        // 6. Recent Assistant Turns（历史对话，Messages）
        // Perf-4：优先级最低——历史对话对当前决策的边际价值低于 Tool Observations 和 Retrieved Materials。
        // 按预算从最新向最旧纳入（chronological break：最旧的最先被截断）。
        if (budget <= 0)
        {
            // 无预算限制 — 全量返回
            foreach (var msg in context.Messages)
            {
                projected.Add(msg);
                usedTokens += EstimateTokens(msg.Content);
            }
        }
        else
        {
            var recent = new List<AgentMessage>();
            var remaining = Math.Max(0, budget - usedTokens);
            for (var i = context.Messages.Count - 1; i >= 0; i--)
            {
                var msg = context.Messages[i];
                var tokens = EstimateTokens(msg.Content);
                if (remaining < tokens)
                {
                    // 时间序列数据：旧消息对当前决策价值递减，break 而非 skip
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
            usedTokens += EstimateTokens(recent);
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

    /// <summary>字符估算 token 数（与 AgentContextState.EstimateTokens 对齐：Max(1, (length+1)/2)）。</summary>
    private static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }
        return Math.Max(1, (content.Length + 1) / 2);
    }

    /// <summary>批量估算消息列表的 token 数。</summary>
    private static int EstimateTokens(IReadOnlyList<AgentMessage> messages)
    {
        var total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            total += EstimateTokens(messages[i].Content);
        }
        return total;
    }
}
