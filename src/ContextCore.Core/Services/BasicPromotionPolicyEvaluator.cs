using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 轻量 Promotion 条件评估器。
/// 该评估器只做本地规则匹配，不调用 LLM、不写入存储，适合放在热路径或审核前预筛选。
/// </summary>
public sealed class BasicPromotionPolicyEvaluator : IPromotionPolicyEvaluator
{
    private static readonly PromotionRule[] DoNotPromoteRules =
    [
        new("普通寒暄", ["你好", "您好", "早上好", "晚上好", "谢谢", "辛苦了", "哈哈"]),
        new("临时情绪", ["临时情绪", "有点烦", "难受", "开心一下", "吐槽"]),
        new("重复解释", ["重复解释", "再解释一遍", "前面说过"]),
        new("无后续价值支线", ["无后续价值", "无关支线", "不用继续", "不需要后续"]),
        new("已被后续覆盖的表达修正", ["表达修正", "纠正一下", "更正前面", "覆盖前文"]),
        new("明显一次性上下文", ["一次性", "临时测试", "仅这次", "一次性日志", "日志片段"]),
        new("未验证或临时猜测", ["临时猜测", "未验证", "待确认", "可能是", "猜测"])
    ];

    private static readonly PromotionRule[] WorkingMemoryRules =
    [
        new("新的架构原则", ["架构原则", "设计原则", "服务边界", "读写分离", "分层"]),
        new("阶段性结论", ["阶段性结论", "当前结论", "本阶段结论", "结论："]),
        new("任务状态变化", ["任务状态", "已完成", "阻塞", "失败", "待持续跟踪"]),
        new("方案被否决", ["方案被否决", "不采用", "废弃方案", "否决"]),
        new("约束新增或变更", ["约束新增", "约束变更", "新增约束", "修改约束"]),
        new("当前项目路线更新", ["路线更新", "路线图更新", "TODO 更新", "计划更新"]),
        new("自动化流程状态变化", ["自动化流程", "进入完成", "进入阻塞", "进入失败"]),
        new("小说状态变化", ["剧情线", "人物状态", "伏笔", "世界观变化"])
    ];

    private static readonly PromotionRule[] StableMemoryRules =
    [
        new("用户明确长期偏好", ["用户明确长期偏好", "用户长期偏好", "长期偏好", "以后都", "始终", "默认使用", "保持中文"]),
        new("项目长期定位", ["项目长期定位", "项目定位", "长期目标", "长期路线"]),
        new("长期稳定约束", ["长期稳定约束", "稳定约束", "硬约束", "必须遵守", "不得", "禁止"]),
        new("跨场景通用规则", ["跨场景", "通用规则", "所有项目", "统一规则"]),
        new("多次重复稳定模式", ["多次重复", "稳定模式", "反复出现", "长期成立"]),
        new("已验证事实或领域知识", ["已验证事实", "领域知识"])
    ];

    public PromotionEvaluationResult Evaluate(PromotionEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var text = BuildSearchText(request);
        if (string.IsNullOrWhiteSpace(text))
        {
            return NoPromotion("空内容", "候选内容为空，不能提升。", 1.0, []);
        }

        var explicitResult = EvaluateExplicitMetadata(request);
        if (explicitResult is not null)
        {
            return explicitResult;
        }

        var blocked = MatchRules(text, DoNotPromoteRules);
        if (blocked.Count > 0)
        {
            return NoPromotion(
                blocked[0],
                $"命中禁止自动提升规则：{blocked[0]}。",
                CalculateScore(blocked.Count, request.Confidence, baseScore: 0.82),
                blocked);
        }

        var stable = MatchRules(text, StableMemoryRules);
        var working = MatchRules(text, WorkingMemoryRules);
        if (stable.Count == 0 && working.Count == 0)
        {
            return new PromotionEvaluationResult
            {
                Decision = PromotionEvaluationDecision.NeedsReview,
                Category = "规则信号不足",
                Reason = "未命中明确提升或禁止提升规则，需要后续审核判断。",
                Score = Clamp(request.Confidence * 0.5),
                RequiresReview = true
            };
        }

        if (stable.Count >= working.Count && stable.Count > 0)
        {
            return Promote(
                PromotionEvaluationDecision.PromoteToStableMemory,
                ContextMemoryLayer.Stable,
                stable[0],
                $"命中长期记忆 Promotion 条件：{stable[0]}。",
                CalculateScore(stable.Count, request.Confidence, baseScore: 0.72),
                stable);
        }

        return Promote(
            PromotionEvaluationDecision.PromoteToWorkingMemory,
            ContextMemoryLayer.Working,
            working[0],
            $"命中中期记忆 Promotion 条件：{working[0]}。",
            CalculateScore(working.Count, request.Confidence, baseScore: 0.66),
            working);
    }

    private static PromotionEvaluationResult? EvaluateExplicitMetadata(PromotionEvaluationRequest request)
    {
        if (TryGetMetadata(request.Metadata, ["promotion", "promotionDecision", "promotion.decision"], out var decision)
            && IsAny(decision, "never", "none", "deny", "reject", "do-not-promote", "不提升", "拒绝"))
        {
            return NoPromotion("显式禁止提升", "元数据显式声明不提升。", 1.0, ["显式禁止提升"]);
        }

        if (!TryGetMetadata(request.Metadata, ["promotionTarget", "promotion.target", "targetLayer"], out var target))
        {
            return null;
        }

        if (IsAny(target, "stable", "long", "long-term", "长期", "稳定"))
        {
            return Promote(
                PromotionEvaluationDecision.PromoteToStableMemory,
                ContextMemoryLayer.Stable,
                "显式长期目标层",
                "元数据显式声明提升到长期稳定记忆层。",
                0.95,
                ["显式长期目标层"]);
        }

        if (IsAny(target, "working", "middle", "mid-term", "medium", "中期", "工作记忆"))
        {
            return Promote(
                PromotionEvaluationDecision.PromoteToWorkingMemory,
                ContextMemoryLayer.Working,
                "显式中期目标层",
                "元数据显式声明提升到中期工作记忆层。",
                0.95,
                ["显式中期目标层"]);
        }

        return null;
    }

    private static PromotionEvaluationResult Promote(
        PromotionEvaluationDecision decision,
        ContextMemoryLayer targetLayer,
        string category,
        string reason,
        double score,
        IReadOnlyList<string> matchedRules)
    {
        return new PromotionEvaluationResult
        {
            Decision = decision,
            TargetLayer = targetLayer,
            Category = category,
            Reason = reason,
            Score = Clamp(score),
            MatchedRules = matchedRules.ToArray(),
            RequiresReview = false
        };
    }

    private static PromotionEvaluationResult NoPromotion(
        string category,
        string reason,
        double score,
        IReadOnlyList<string> matchedRules)
    {
        return new PromotionEvaluationResult
        {
            Decision = PromotionEvaluationDecision.DoNotPromote,
            Category = category,
            Reason = reason,
            Score = Clamp(score),
            MatchedRules = matchedRules.ToArray(),
            RequiresReview = false
        };
    }

    private static List<string> MatchRules(string text, IReadOnlyList<PromotionRule> rules)
    {
        var matches = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(rule.Name);
            }
        }

        return matches;
    }

    private static string BuildSearchText(PromotionEvaluationRequest request)
    {
        return string.Join(
            '\n',
            request.Content,
            request.Type,
            string.Join(' ', request.Tags),
            string.Join(' ', request.Metadata.Select(pair => $"{pair.Key} {pair.Value}")));
    }

    private static bool TryGetMetadata(
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<string> keys,
        out string value)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out value!)
                && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsAny(string value, params string[] expected)
    {
        return expected.Any(item => string.Equals(value, item, StringComparison.OrdinalIgnoreCase));
    }

    private static double CalculateScore(int matchCount, double confidence, double baseScore)
    {
        return baseScore + Math.Min(matchCount, 4) * 0.04 + Clamp(confidence) * 0.08;
    }

    private static double Clamp(double value)
    {
        return Math.Max(0, Math.Min(1, value));
    }

    private sealed record PromotionRule(string Name, IReadOnlyList<string> Keywords);
}
