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
/// 领域关键词配置：仅保留内容安全过滤词表（废弃内容硬/软拒绝）与夹具惩罚词表（生产为空）。
/// 模式专属加分已迁移到 <see cref="ModeReserveWeightProfile"/>（显式信号权重，替代领域词表）；
/// 长期记忆判断已迁移到 <see cref="ContextRecallSignalPolicy.IsLongTermMemoryCategory(ContextMemoryItem)"/>（Layer/Tags/Metadata 结构信号）。
/// CreateProduction() 用于生产代码（夹具惩罚关键词为空），CreateDefault() 保留夹具关键词用于迁移兼容。
/// </summary>
public sealed class DomainKeywordProfile
{
    /// <summary>夹具惩罚关键词（原 BasicContextPackageBuilder 评分路径内联）。生产环境为空。</summary>
    public IReadOnlyList<string> FixturePenaltyKeywords { get; init; } = [];

    /// <summary>废弃内容硬拒绝关键词：任何场景下都强力排除（原 ScoreWorkingMemoryForAnchors 内联）。</summary>
    public IReadOnlyList<string> DeprecatedContentHardRejectionKeywords { get; init; } = [];

    /// <summary>废弃内容软拒绝关键词：仅非审计场景下过滤（原 ScoreWorkingMemoryForAnchors 内联）。</summary>
    public IReadOnlyList<string> DeprecatedContentSoftRejectionKeywords { get; init; } = [];

    /// <summary>
    /// 创建默认配置（保留夹具惩罚关键词，用于迁移期间的向后兼容）。
    /// </summary>
    public static DomainKeywordProfile CreateDefault() => new()
    {
        FixturePenaltyKeywords =
        [
            "stress-test",
            "压力测试",
            "无用字符",
            "budget-stress"
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
            // 生产环境不含夹具惩罚关键词——fixture 名称不进入生产评分路径。
            // eval 适配层如需惩罚 stress-test 项，应通过 request.Policy 或独立 eval profile 注入。
            FixturePenaltyKeywords = [],
            DeprecatedContentHardRejectionKeywords = baseline.DeprecatedContentHardRejectionKeywords,
            DeprecatedContentSoftRejectionKeywords = baseline.DeprecatedContentSoftRejectionKeywords
        };
    }
}

/// <summary>
/// 模式保留信号权重配置：按模式（Chat/Novel/Automation）显式声明"工作记忆保留 / 稳定记忆保留 / 打包排序保留"
/// 三个阶段的信号→权重映射，替代原 DomainKeywordProfile 中的模式专属领域词表。
/// 信号来源为条目的结构化字段（Tags、Metadata["signal"]/["reserve-signal"]），不再依赖内容关键词匹配。
/// 权重值与原领域词表加分保持一致（保留 +900/+600，打包排序 +9000），仅切换判定来源。
/// </summary>
public sealed class ModeReserveWeightProfile
{
    /// <summary>各模式工作记忆保留信号权重：归一化模式名 → (信号 → 权重)。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> WorkingMemoryReserveWeights { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>各模式稳定记忆保留信号权重：归一化模式名 → (信号 → 权重)。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> StableMemoryReserveWeights { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>各模式打包排序保留信号权重：归一化模式名 → (信号 → 权重)。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> PackageOrderReserveWeights { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>创建生产配置：显式信号权重，值与原领域词表加分一致。</summary>
    public static ModeReserveWeightProfile CreateProduction()
    {
        var chatWorking = Weights(900, "preference", "scope", "active-task", "plan", "conclusion");
        var novelWorking = Weights(900, "character-state", "foreshadow", "world-rule", "item-state", "plot-hook", "ending-plan");
        var automationWorking = Weights(900, "last-error", "recovery", "retry", "dead-letter", "worker-stats");

        var chatStable = Weights(900, "preference", "language", "scope", "safety", "promotion-policy", "oneoff");
        var novelStable = Weights(600, "world-rule", "constraint", "item-state", "character-state");
        var automationStable = Weights(600, "safety", "retry", "dead-letter", "recovery");

        var chatPackage = Weights(9_000, "preference", "scope", "active-task", "plan", "conclusion", "promotion-policy", "oneoff");
        var novelPackage = Weights(9_000, "character-state", "foreshadow", "world-rule", "item-state", "plot-hook", "ending-plan");
        var automationPackage = Weights(9_000, "last-error", "recovery", "retry", "dead-letter", "worker-stats");

        return new ModeReserveWeightProfile
        {
            WorkingMemoryReserveWeights = ByMode(chatWorking, novelWorking, automationWorking),
            StableMemoryReserveWeights = ByMode(chatStable, novelStable, automationStable),
            PackageOrderReserveWeights = ByMode(chatPackage, novelPackage, automationPackage)
        };
    }

    private static IReadOnlyDictionary<string, double> Weights(double weight, params string[] signals)
    {
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in signals)
        {
            dict[signal] = weight;
        }

        return dict;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ByMode(
        IReadOnlyDictionary<string, double> chat,
        IReadOnlyDictionary<string, double> novel,
        IReadOnlyDictionary<string, double> automation)
    {
        return new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat"] = chat,
            ["chatmode"] = chat,
            ["novel"] = novel,
            ["novelmode"] = novel,
            ["automation"] = automation,
            ["automationmode"] = automation
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
