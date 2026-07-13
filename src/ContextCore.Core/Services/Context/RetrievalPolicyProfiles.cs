using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 模式预算配置：定义单个模式（Chat/Novel/Automation/Coding）的默认 token 预算和 section 分配比例。
/// 从 BasicContextPackageBuilder 的私有嵌套类提取为公共类型，供 Profile/Registry 统一管理。
/// </summary>
public sealed class ModeBudgetProfile
{
    public ModeBudgetProfile(string modeName, int defaultTokenBudget, IReadOnlyDictionary<string, double> sectionRatios)
    {
        ModeName = modeName;
        DefaultTokenBudget = defaultTokenBudget;
        SectionRatios = sectionRatios;
    }

    public string ModeName { get; }

    public int DefaultTokenBudget { get; }

    public IReadOnlyDictionary<string, double> SectionRatios { get; }
}

/// <summary>
/// 模式预算配置注册表：按 ContextPackageMode 枚举或归一化字符串名解析 ModeBudgetProfile。
/// 替代 BasicContextPackageBuilder 中散落的 inline switch 与工厂方法。
/// </summary>
public sealed class ModeBudgetProfileRegistry
{
    private readonly Dictionary<ContextPackageMode, ModeBudgetProfile> _byEnum = new();
    private readonly Dictionary<string, ModeBudgetProfile> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册一个模式预算配置，并绑定枚举值与字符串别名。</summary>
    public ModeBudgetProfileRegistry Register(ModeBudgetProfile profile, ContextPackageMode mode, params string[] aliases)
    {
        _byEnum[mode] = profile;
        _byName[NormalizeModeName(profile.ModeName)] = profile;
        foreach (var alias in aliases)
        {
            _byName[NormalizeModeName(alias)] = profile;
        }

        return this;
    }

    /// <summary>按 ContextPackageMode 枚举解析；None 返回 null。</summary>
    public ModeBudgetProfile? Resolve(ContextPackageMode mode) =>
        mode == ContextPackageMode.None ? null
        : _byEnum.TryGetValue(mode, out var profile) ? profile
        : null;

    /// <summary>按归一化字符串名解析（例如 "chat"、"chatmode"）。</summary>
    public ModeBudgetProfile? Resolve(string? normalizedModeName) =>
        string.IsNullOrWhiteSpace(normalizedModeName) ? null
        : _byName.TryGetValue(normalizedModeName, out var profile) ? profile
        : null;

    /// <summary>
    /// 创建预填充 4 个默认模式预算配置的注册表，值与原 BasicContextPackageBuilder 工厂方法完全一致。
    /// </summary>
    public static ModeBudgetProfileRegistry CreateDefault()
    {
        var registry = new ModeBudgetProfileRegistry();
        registry.Register(
            new ModeBudgetProfile(
                "ChatMode",
                2_400,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["current_task"] = 0.12,
                    ["hard_constraints"] = 0.12,
                    ["constraints"] = 0.16,
                    ["recent_context"] = 0.28,
                    ["working_memory"] = 0.24,
                    ["stable_memory"] = 0.10,
                    ["global_context"] = 0.08,
                    ["soft_constraints"] = 0.08,
                    ["related_context"] = 0.10,
                    ["evidence"] = 0.08,
                    ["historical_context"] = 0.08,
                    ["conflict_evidence"] = 0.08,
                    ["deprecated_evidence"] = 0.08,
                    ["excluded"] = 0.06,
                    ["uncertainties"] = 0.06
                }),
            ContextPackageMode.Chat,
            "chat");
        registry.Register(
            new ModeBudgetProfile(
                "NovelMode",
                6_000,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["current_task"] = 0.08,
                    ["hard_constraints"] = 0.08,
                    ["constraints"] = 0.12,
                    ["recent_context"] = 0.18,
                    ["working_memory"] = 0.16,
                    ["stable_memory"] = 0.34,
                    ["global_context"] = 0.24,
                    ["soft_constraints"] = 0.12,
                    ["related_context"] = 0.22,
                    ["evidence"] = 0.16,
                    ["historical_context"] = 0.10,
                    ["conflict_evidence"] = 0.10,
                    ["deprecated_evidence"] = 0.10,
                    ["excluded"] = 0.06,
                    ["uncertainties"] = 0.06
                }),
            ContextPackageMode.Novel,
            "novel");
        registry.Register(
            new ModeBudgetProfile(
                "AutomationMode",
                4_000,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["current_task"] = 0.14,
                    ["hard_constraints"] = 0.16,
                    ["constraints"] = 0.20,
                    ["recent_context"] = 0.16,
                    ["working_memory"] = 0.26,
                    ["stable_memory"] = 0.10,
                    ["global_context"] = 0.08,
                    ["soft_constraints"] = 0.08,
                    ["related_context"] = 0.18,
                    ["evidence"] = 0.14,
                    ["historical_context"] = 0.08,
                    ["conflict_evidence"] = 0.08,
                    ["deprecated_evidence"] = 0.08,
                    ["excluded"] = 0.08,
                    ["uncertainties"] = 0.10
                }),
            ContextPackageMode.Automation,
            "automation");
        registry.Register(
            new ModeBudgetProfile(
                "CodingMode",
                5_000,
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["current_task"] = 0.12,
                    ["hard_constraints"] = 0.16,
                    ["constraints"] = 0.20,
                    ["recent_context"] = 0.20,
                    ["working_memory"] = 0.28,
                    ["stable_memory"] = 0.16,
                    ["global_context"] = 0.10,
                    ["soft_constraints"] = 0.08,
                    ["related_context"] = 0.22,
                    ["evidence"] = 0.16,
                    ["historical_context"] = 0.08,
                    ["conflict_evidence"] = 0.08,
                    ["deprecated_evidence"] = 0.08,
                    ["excluded"] = 0.08,
                    ["uncertainties"] = 0.08
                }),
            ContextPackageMode.Coding,
            "coding");
        return registry;
    }

    private static string NormalizeModeName(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return string.Empty;
        }

        return mode.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}

/// <summary>
/// 领域关键词配置：集中管理原先硬编码在 BasicContextPackageBuilder 和 ContextRecallSignalPolicy 中的关键词表。
/// CreateProduction() 用于生产代码（夹具惩罚关键词为空），CreateDefault() 保留夹具关键词用于迁移兼容。
/// </summary>
public sealed class DomainKeywordProfile
{
    /// <summary>审计模式关键词（原 BasicContextPackageBuilder 内联判定）。</summary>
    public IReadOnlyList<string> AuditModeKeywords { get; init; } = [];

    /// <summary>长期记忆关键词（原 ContextRecallSignalPolicy 硬编码数组）。</summary>
    public IReadOnlyList<string> LongTermMemoryKeywords { get; init; } = [];

    /// <summary>夹具惩罚关键词（原 BasicContextPackageBuilder 评分路径内联）。生产环境为空。</summary>
    public IReadOnlyList<string> FixturePenaltyKeywords { get; init; } = [];

    /// <summary>ChatMode 工作记忆保留加分关键词（原 ResolveWorkingMemoryReserveScore 内联）。</summary>
    public IReadOnlyList<string> ChatModeBoostKeywords { get; init; } = [];

    /// <summary>ChatMode 打包排序保留加分关键词（原 ResolvePackageOrderScore 内联）。</summary>
    public IReadOnlyList<string> ChatModeReserveBoostKeywords { get; init; } = [];

    /// <summary>NovelMode 打包排序保留加分关键词（原 ResolvePackageOrderScore 内联）。</summary>
    public IReadOnlyList<string> NovelModeReserveBoostKeywords { get; init; } = [];

    /// <summary>AutomationMode 工作记忆保留加分关键词（原 ResolveWorkingMemoryReserveScore 内联）。</summary>
    public IReadOnlyList<string> AutomationModeWorkingMemoryReserveKeywords { get; init; } = [];

    /// <summary>NovelMode 工作记忆保留加分关键词（原 ResolveWorkingMemoryReserveScore 内联）。</summary>
    public IReadOnlyList<string> NovelModeWorkingMemoryReserveKeywords { get; init; } = [];

    /// <summary>ChatMode 稳定记忆保留加分关键词（原 ResolveStableMemoryReserveScore 内联）。</summary>
    public IReadOnlyList<string> ChatModeStableMemoryReserveKeywords { get; init; } = [];

    /// <summary>NovelMode 稳定记忆保留加分关键词（原 ResolveStableMemoryReserveScore 内联）。</summary>
    public IReadOnlyList<string> NovelModeStableMemoryReserveKeywords { get; init; } = [];

    /// <summary>AutomationMode 稳定记忆保留加分关键词（原 ResolveStableMemoryReserveScore 内联）。</summary>
    public IReadOnlyList<string> AutomationModeStableMemoryReserveKeywords { get; init; } = [];

    /// <summary>AutomationMode 打包排序保留加分关键词（原 ResolvePackageOrderScore 内联）。</summary>
    public IReadOnlyList<string> AutomationModePackageOrderKeywords { get; init; } = [];

    /// <summary>废弃内容硬拒绝关键词：任何场景下都强力排除（原 ScoreWorkingMemoryForAnchors 内联）。</summary>
    public IReadOnlyList<string> DeprecatedContentHardRejectionKeywords { get; init; } = [];

    /// <summary>废弃内容软拒绝关键词：仅非审计场景下过滤（原 ScoreWorkingMemoryForAnchors 内联）。</summary>
    public IReadOnlyList<string> DeprecatedContentSoftRejectionKeywords { get; init; } = [];

    /// <summary>
    /// 创建默认配置（保留夹具惩罚关键词，用于迁移期间的向后兼容）。
    /// </summary>
    public static DomainKeywordProfile CreateDefault() => new()
    {
        AuditModeKeywords =
        [
            "废弃",
            "作废",
            "草稿",
            "草案",
            "旧版",
            "旧",
            "放弃",
            "舍弃",
            "审计",
            "legacy",
            "deprecated",
            "audit"
        ],
        LongTermMemoryKeywords =
        [
            "preference",
            "偏好",
            "project",
            "项目",
            "background",
            "背景",
            "style",
            "风格",
            "safety",
            "security",
            "安全",
            "密钥",
            "secret",
            "boundary",
            "边界",
            "principle",
            "原则",
            "constraint",
            "约束",
            "rule",
            "规则",
            "world",
            "世界观",
            "设定",
            "performance",
            "性能",
            "test",
            "测试",
            "risk",
            "风险"
        ],
        FixturePenaltyKeywords =
        [
            "stress-test",
            "压力测试",
            "无用字符",
            "budget-stress"
        ],
        ChatModeBoostKeywords =
        [
            "stable preference",
            "preference",
            "偏好",
            "scope",
            "边界",
            "作用域",
            "active task",
            "active",
            "当前",
            "计划",
            "结论"
        ],
        ChatModeReserveBoostKeywords =
        [
            "stable:preference",
            "preference-language",
            "preference",
            "scope",
            "active-task",
            "active task",
            "current-task",
            "plan",
            "conclusion",
            "promotion-policy",
            "no-promote",
            "promote",
            "提升",
            "临时情绪",
            "重复解释",
            "oneoff",
            "一次性"
        ],
        NovelModeReserveBoostKeywords =
        [
            "character-state",
            "foreshadow",
            "world-rule",
            "item-state",
            "plot-hook",
            "ending-plan"
        ],
        AutomationModeWorkingMemoryReserveKeywords =
        [
            "last-error", "last error", "错误", "失败",
            "recovery", "恢复点", "retry", "重试",
            "dead-letter", "死信队列", "worker", "stats", "统计"
        ],
        NovelModeWorkingMemoryReserveKeywords =
        [
            "character-state", "人物状态", "foreshadow", "伏笔",
            "world", "世界观", "约束", "item-state", "物品状态",
            "ending", "结局"
        ],
        ChatModeStableMemoryReserveKeywords =
        [
            "preference", "偏好", "language", "中文", "scope", "边界", "安全",
            "promotion-policy", "no-promote", "promote", "提升",
            "临时情绪", "重复解释", "oneoff", "一次性"
        ],
        NovelModeStableMemoryReserveKeywords =
        [
            "world", "世界观", "constraint", "约束", "item", "character"
        ],
        AutomationModeStableMemoryReserveKeywords =
        [
            "safety", "retry", "dead-letter", "recovery", "安全", "重试"
        ],
        AutomationModePackageOrderKeywords =
        [
            "last-error", "error-log", "recovery", "recovery-point",
            "retry", "dead-letter", "queue-state", "worker-stats"
        ],
        DeprecatedContentHardRejectionKeywords =
        [
            "绝不使用",
            "彻底舍弃不用",
            "绝不参考"
        ],
        DeprecatedContentSoftRejectionKeywords =
        [
            "不再需要参考"
        ]
    };

    /// <summary>
    /// 创建生产配置。夹具惩罚关键词为空——fixture 名称不进入生产评分路径。
    /// </summary>
    public static DomainKeywordProfile CreateProduction()
    {
        var baseline = CreateDefault();
        return new DomainKeywordProfile
        {
            AuditModeKeywords = baseline.AuditModeKeywords,
            LongTermMemoryKeywords = baseline.LongTermMemoryKeywords,
            // 生产环境不含夹具惩罚关键词——fixture 名称不进入生产评分路径。
            // eval 适配层如需惩罚 stress-test 项，应通过 request.Policy 或独立 eval profile 注入。
            FixturePenaltyKeywords = [],
            ChatModeBoostKeywords = baseline.ChatModeBoostKeywords,
            ChatModeReserveBoostKeywords = baseline.ChatModeReserveBoostKeywords,
            NovelModeReserveBoostKeywords = baseline.NovelModeReserveBoostKeywords,
            AutomationModeWorkingMemoryReserveKeywords = baseline.AutomationModeWorkingMemoryReserveKeywords,
            NovelModeWorkingMemoryReserveKeywords = baseline.NovelModeWorkingMemoryReserveKeywords,
            ChatModeStableMemoryReserveKeywords = baseline.ChatModeStableMemoryReserveKeywords,
            NovelModeStableMemoryReserveKeywords = baseline.NovelModeStableMemoryReserveKeywords,
            AutomationModeStableMemoryReserveKeywords = baseline.AutomationModeStableMemoryReserveKeywords,
            AutomationModePackageOrderKeywords = baseline.AutomationModePackageOrderKeywords,
            DeprecatedContentHardRejectionKeywords = baseline.DeprecatedContentHardRejectionKeywords,
            DeprecatedContentSoftRejectionKeywords = baseline.DeprecatedContentSoftRejectionKeywords
        };
    }
}

/// <summary>
/// 工作记忆评分权重配置：集中管理原 ScoreWorkingMemoryForAnchors 中的魔法数字。
/// 所有值与原硬编码完全一致，仅做物理抽离以便后续调参与审计。
/// </summary>
public sealed class WorkingMemoryScoringProfile
{
    /// <summary>BaseScore 上限。</summary>
    public double BaseScoreCap { get; init; } = 8.0;

    /// <summary>BaseScore 中 Importance 乘数。</summary>
    public double BaseScoreImportanceMultiplier { get; init; } = 4.0;

    /// <summary>BaseScore 中 Confidence 乘数。</summary>
    public double BaseScoreConfidenceMultiplier { get; init; } = 2.0;

    // ── LayerScore ──────────────────────────────────────────────
    public double LayerScoreWorking { get; init; } = 4.0;
    public double LayerScoreStable { get; init; } = 2.0;
    public double LayerScoreDefault { get; init; } = 1.0;

    // ── StatusScore ─────────────────────────────────────────────
    public double StatusScoreRejected { get; init; } = -30.0;
    public double StatusScoreDeprecatedAudit { get; init; } = 20.0;
    public double StatusScoreDeprecatedNormal { get; init; } = -12.0;
    public double StatusScoreActiveAudit { get; init; } = 0.5;
    public double StatusScoreActiveNormal { get; init; } = 5.0;

    // ── Anchor 匹配乘数 ─────────────────────────────────────────
    public double AnchorRawTokenActiveMultiplier { get; init; } = 9.0;
    public double AnchorRawTokenDeprecatedMultiplier { get; init; } = 7.0;
    public double AnchorSemanticActiveMultiplier { get; init; } = 18.0;
    public double AnchorSemanticDeprecatedMultiplier { get; init; } = 13.0;

    // ── AnchorMatchBonus ────────────────────────────────────────
    public double AnchorMatchBonusBoth { get; init; } = 10.0;
    public double AnchorMatchBonusBothAudit { get; init; } = 8.0;
    public double AnchorMatchBonusSemanticOnly { get; init; } = 5.0;
    public double AnchorMatchBonusRawOnly { get; init; } = 3.0;

    // ── ModeMatchScore ──────────────────────────────────────────
    public double ModeMatchScore { get; init; } = 3.0;

    // ── TaskIntentScore ─────────────────────────────────────────
    public double TaskIntentScoreCap { get; init; } = 6.0;
    public double TaskIntentScorePerHit { get; init; } = 1.5;
    public int TaskIntentMaxAnchors { get; init; } = 12;

    // ── RecencyScore ────────────────────────────────────────────
    public double RecencyScore24Hours { get; init; } = 15.0;
    public double RecencyScore7Days { get; init; } = 8.0;
    public double RecencyScore30Days { get; init; } = 3.0;

    // ── LifecyclePenalty ────────────────────────────────────────
    public double LifecyclePenaltyCap { get; init; } = 15.0;
    public double LifecyclePenaltyRatio { get; init; } = 0.70;

    // ── Stress-test 占位分 ──────────────────────────────────────
    public double StressTestPlaceholderScore { get; init; } = 1.0;

    // ── 严格相关性过滤阈值 ──────────────────────────────────────
    public double StrictRelevanceImportanceThreshold { get; init; } = 0.8;

    /// <summary>创建与原硬编码值完全一致的默认配置。</summary>
    public static WorkingMemoryScoringProfile CreateDefault() => new();
}

/// <summary>
/// 上下文包优先级配置：集中管理原 ResolvePriorityRank 和 ResolveConstraintMergePriority 中的优先级映射。
/// 值与原硬编码完全一致。
/// </summary>
public sealed class PackagePriorityProfile
{
    // ── 优先级 Rank（ResolvePriorityRank）──────────────────────
    public int PriorityRankSystem { get; init; } = 600;
    public int PriorityRankCurrent { get; init; } = 500;
    public int PriorityRankRuntime { get; init; } = 400;
    public int PriorityRankProject { get; init; } = 300;
    public int PriorityRankUser { get; init; } = 200;
    public int PriorityRankDomain { get; init; } = 100;

    // ── 按 Kind 的默认 Rank ─────────────────────────────────────
    public int PriorityRankRecentContext { get; init; } = 500;
    public int PriorityRankWorkingMemoryActive { get; init; } = 450;
    public int PriorityRankHardConstraint { get; init; } = 550;
    public int PriorityRankWorkingMemory { get; init; } = 350;
    public int PriorityRankGlobalContext { get; init; } = 250;
    public int PriorityRankStableMemory { get; init; } = 200;
    public int PriorityRankSoftConstraint { get; init; } = 100;

    // ── 约束合并优先级（ResolveConstraintMergePriority）─────────
    public int ConstraintMergeRankSystem { get; init; } = 600;
    public int ConstraintMergeRankCurrent { get; init; } = 500;
    public int ConstraintMergeRankRuntime { get; init; } = 400;
    public int ConstraintMergeRankMode { get; init; } = 350;
    public int ConstraintMergeRankProject { get; init; } = 300;
    public int ConstraintMergeRankHard { get; init; } = 450;
    public int ConstraintMergeRankUser { get; init; } = 200;
    public int ConstraintMergeRankDomain { get; init; } = 100;
    public int ConstraintMergeRankSoft { get; init; } = 100;
    public int ConstraintMergeRankUnclassified { get; init; } = 0;

    /// <summary>创建与原硬编码值完全一致的默认配置。</summary>
    public static PackagePriorityProfile CreateDefault() => new();
}
