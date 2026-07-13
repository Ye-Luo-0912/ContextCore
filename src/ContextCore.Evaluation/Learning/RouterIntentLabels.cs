namespace ContextCore.Evaluation.Learning;

/// <summary>
/// 离线路由意图标签常量。原 PlanningIntentDetector 中的标签迁移至 Evaluation 本地，
/// 数据集应显式提供 Intent，缺失时使用 <see cref="Unknown"/>，不再依赖关键词自动制造标签。
/// </summary>
public static class RouterIntentLabels
{
    public const string Unknown = "Unknown";
    public const string CurrentTask = "CurrentTask";
    public const string AuditDeprecated = "AuditDeprecated";
    public const string ConflictCheck = "ConflictCheck";
    public const string CodingTask = "CodingTask";
    public const string NovelGeneration = "NovelGeneration";
    public const string AutomationRecovery = "AutomationRecovery";
    public const string LongTermPreference = "LongTermPreference";
    public const string FuzzyQuestion = "FuzzyQuestion";
}
