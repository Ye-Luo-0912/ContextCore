using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>规则型 planning intent detector；不调用 LLM，也不执行 retrieval。</summary>
public sealed class PlanningIntentDetector
{
    public const string CurrentTask = "CurrentTask";
    public const string AuditDeprecated = "AuditDeprecated";
    public const string ConflictCheck = "ConflictCheck";
    public const string CodingTask = "CodingTask";
    public const string NovelGeneration = "NovelGeneration";
    public const string AutomationRecovery = "AutomationRecovery";
    public const string LongTermPreference = "LongTermPreference";
    public const string FuzzyQuestion = "FuzzyQuestion";

    public PlanningIntentDetection Detect(
        ContextPlanningSnapshot snapshot,
        string? currentInput,
        string? requestedMode = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var text = Normalize(currentInput);
        var reasons = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            if (snapshot.ActiveTasks.Count > 0)
            {
                reasons.Add("empty input with active task snapshot");
                return Create(CurrentTask, 0.55, reasons, warnings);
            }

            warnings.Add("current input is empty; defaulting to fuzzy question intent");
            return Create(FuzzyQuestion, 0.35, reasons, warnings);
        }

        if (ContainsAny(text, "冲突", "矛盾", "不一致", "竞争方案", "范围不匹配", "scope mismatch", "conflict", "contradiction", "inconsistent", "competing"))
        {
            reasons.Add("matched conflict-check terms");
            return Create(ConflictCheck, 0.86, reasons, warnings);
        }

        if (ContainsAny(text, "审计", "废弃", "弃用", "过期", "历史", "旧方案", "旧版本", "deprecated", "obsolete", "legacy", "retired", "audit"))
        {
            reasons.Add("matched audit/deprecated terms");
            return Create(AuditDeprecated, 0.88, reasons, warnings);
        }

        if (ContainsAny(text, "自动化", "恢复点", "重试", "死信", "失败后", "last error", "retry", "recovery", "recover", "dead-letter", "dead letter", "automation failure"))
        {
            reasons.Add("matched automation recovery terms");
            return Create(AutomationRecovery, 0.84, reasons, warnings);
        }

        if (ContainsAny(text, "小说", "章节", "角色", "人物", "世界观", "伏笔", "剧情", "物品状态", "novel", "chapter", "character", "foreshadowing", "narrative", "world constraint"))
        {
            reasons.Add("matched novel-generation terms");
            return Create(NovelGeneration, 0.82, reasons, warnings);
        }

        if (ContainsAny(text, "代码", "编码", "实现", "修复", "测试", "编译", "仓库", "接口", "dotnet", "c#", "code", "coding", "test", "build", "bug", "fix", "implement", "repository"))
        {
            reasons.Add("matched coding-task terms");
            return Create(CodingTask, 0.82, reasons, warnings);
        }

        if (ContainsAny(text, "长期偏好", "稳定偏好", "用户偏好", "风格偏好", "preference", "long-term", "stable preference", "user preference"))
        {
            reasons.Add("matched long-term preference terms");
            return Create(LongTermPreference, 0.78, reasons, warnings);
        }

        if (ContainsAny(text, "当前任务", "下一步", "继续", "待办", "active task", "current task", "next step", "todo"))
        {
            reasons.Add("matched current-task terms");
            return Create(CurrentTask, 0.8, reasons, warnings);
        }

        if (IsModeHint(requestedMode, "Coding"))
        {
            reasons.Add("requested mode hints coding task");
            return Create(CodingTask, 0.66, reasons, warnings);
        }

        if (IsModeHint(requestedMode, "Novel"))
        {
            reasons.Add("requested mode hints novel generation");
            return Create(NovelGeneration, 0.66, reasons, warnings);
        }

        if (IsModeHint(requestedMode, "Automation"))
        {
            reasons.Add("requested mode hints automation recovery");
            return Create(AutomationRecovery, 0.66, reasons, warnings);
        }

        reasons.Add("no high-confidence rule matched");
        return Create(FuzzyQuestion, 0.52, reasons, warnings);
    }

    private static PlanningIntentDetection Create(
        string intent,
        double confidence,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> warnings)
    {
        return new PlanningIntentDetection
        {
            Intent = intent,
            Confidence = confidence,
            Reasons = reasons,
            Warnings = warnings
        };
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsModeHint(string? mode, string expected)
    {
        return !string.IsNullOrWhiteSpace(mode)
            && mode.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}

public sealed class PlanningIntentDetection
{
    public string Intent { get; init; } = PlanningIntentDetector.FuzzyQuestion;

    public double Confidence { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
