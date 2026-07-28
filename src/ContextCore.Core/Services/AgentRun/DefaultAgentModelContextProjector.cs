using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// P0-3：DefaultAgentModelContextProjector — Agent 模型上下文默认投影器
//
// 修复问题：
//   旧路径中 Actor 调用 IContextDecisionRuntime 后仅将 CandidateId/Type/FinalScore
//   摘要追加为 System 消息，模型拿不到材料正文。此外 ProjectForModel(tokenBudget: 0)
//   等价于关闭预算控制。
//
// 本投影器在投影阶段从 ContextDecisionExecutionResult.WorkingSet.Materials 取出候选
// 正文内容，并按 Run.ModelContextTokenBudget 截断。
//
// 投影顺序（高优先级在前，截断从最旧开始）：
//   1. System Prompt
//   2. Hard Constraints
//   3. Current Task
//   4. Working Memory（短期工作集 Messages）
//   5. Retrieved Materials（从 WorkingSet.Materials 取正文）
//   6. Tool Observations
//   7. Recent Assistant Turns
// ===========================================================================

/// <summary>
/// P0-3：Agent 模型上下文默认投影器。
/// 从 ContextDecisionExecutionResult.WorkingSet.Materials 取出候选正文内容，
/// 按 Run.ModelContextTokenBudget 截断投影为最终发送给模型的消息列表。
/// </summary>
public sealed class DefaultAgentModelContextProjector : IAgentModelContextProjector
{
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

        // 1. System Prompt（高优先级，总是保留）
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var msg = new AgentMessage { Role = AgentMessageRole.System, Content = context.SystemPrompt };
            projected.Add(msg);
            usedTokens += EstimateTokens(msg.Content);
        }

        // 2. Hard Constraints（高优先级，总是保留）
        if (!string.IsNullOrEmpty(context.Constraints))
        {
            var msg = new AgentMessage { Role = AgentMessageRole.System, Content = $"[Constraints]\n{context.Constraints}" };
            projected.Add(msg);
            usedTokens += EstimateTokens(msg.Content);
        }

        // 3. Current Task（高优先级，总是保留）
        if (!string.IsNullOrEmpty(context.CurrentTask))
        {
            var msg = new AgentMessage { Role = AgentMessageRole.User, Content = context.CurrentTask };
            projected.Add(msg);
            usedTokens += EstimateTokens(msg.Content);
        }

        // 4. Retrieved Materials（P0-3 核心：从 WorkingSet.Materials 取正文，不只是 ID/Type/Score）
        if (decisionResult is not null)
        {
            var selected = decisionResult.Decision.SelectedEnvelopes;
            var materials = decisionResult.WorkingSet.Materials;

            foreach (var env in selected)
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

                var msg = new AgentMessage
                {
                    Role = AgentMessageRole.System,
                    Content = $"[RetrievedContext:{env.Type}]\n{content}"
                };

                var tokens = EstimateTokens(msg.Content);
                if (budget > 0 && usedTokens + tokens > budget)
                {
                    truncated = true;
                    break;
                }

                projected.Add(msg);
                usedTokens += tokens;
            }
        }

        // 5. Working Memory（短期工作集 Messages；按预算从最新向最旧纳入）
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

        // 6. Tool Observations（按预算从最新向最旧纳入）
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
                    truncated = true;
                    break;
                }
                recentTools.Insert(0, obs.ToAgentMessage());
                remaining -= tokens;
            }
            projected.AddRange(recentTools);
            usedTokens += EstimateTokens(recentTools);
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
