using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R20-1：Multi-Expert Retrieval Routing 契约
//
// 目标：
//   为 HybridContextRetriever 的 5 channel（Mandatory / Keyword / Vector /
//   Memory / Relation）+ Task-State 信号建立统一的 Expert 抽象，让 Router
//   可以按 Expert 粒度开关 / 调整 TopK / 调整 TokenBudget，而不是按 channel
//   字符串或 boolean flag 操作。
//
// 设计原则：
//   1. 8 个 Expert 对齐 R18-1 ContextCandidateSource 枚举（Mandatory /
//      Lexical / Semantic / WorkingMemory / StableMemory / Graph / Recency /
//      Constraint），与 ContextCandidateEnvelope.Source 一一对应。
//   2. Mandatory / Constraint 两个 Expert "永不关闭"（用户澄清：safety gate
//      准入与 budget 限制正交）。Router 无法通过 Mask 禁用这两个 Expert。
//   3. ExpertRoutingDecision 是 per-Expert 的运行时决策，由 Router 根据
//      PolicyBundle.Routing.EnabledExperts + Request 特征计算得出。
//   4. RetrievalExpertMask 是位掩码，用于 O(1) 判断 Expert 是否启用。
//   5. 不引入存储 I/O；契约是内存中的不可变 record。
//   6. 不强制替换 HybridContextRetriever；Router 在 R20-2 阶段作为可选
//      编排路径接入，由调用方决定是否启用。
//
// 5 channel 对齐：
//   HybridContextRetriever.CurrentChannels:
//     - MandatoryRecallChannelExecutor → Expert.Mandatory
//     - ContextRecallChannelExecutor (Keyword) → Expert.Lexical
//     - VectorRecallChannelExecutor (Semantic) → Expert.Semantic
//     - MemoryRecallChannelExecutor (WorkingMemory + StableMemory)
//         → Expert.WorkingMemory + Expert.StableMemory（两个 Expert 共享 channel）
//     - RelationRecallChannelExecutor (Graph) → Expert.Graph
//   额外信号（不在 5 channel 中）：
//     - RecentContext / CurrentTask → Expert.Recency（由 WorkingMemory channel
//       副产品或独立 TaskState 信号提供）
//     - Constraint → Expert.Constraint（由 Mandatory channel 副产品或独立
//       ConstraintStore 查询提供）
//
// 子阶段进度：
//   R20-1（当前）：契约定义 + 单元测试验证可实施性。不触碰 HybridContextRetriever。
//   R20-2：DefaultRetrievalRouter 实现 + Budget-Aware TopK 模拟。
// ===========================================================================

/// <summary>
/// R20-1：检索专家（Retrieval Expert）枚举。
/// 8 个 Expert 对齐 R18-1 <see cref="ContextCandidateSource"/> 枚举，
/// 为 Router 提供 per-Expert 操作粒度。
/// </summary>
/// <remarks>
/// <b>永不关闭</b>（用户澄清：safety gate 准入与 budget 限制正交）：
///   <list type="bullet">
///   <item><see cref="Mandatory"/>：hard constraint / required tag，无条件选入。</item>
///   <item><see cref="Constraint"/>：constraint satisfaction，无条件参与。</item>
///   </list>
/// <b>可关闭</b>（Router 可通过 <see cref="RetrievalExpertMask"/> 禁用）：
///   <list type="bullet">
///   <item><see cref="Lexical"/>：keyword / context recall。</item>
///   <item><see cref="Semantic"/>：vector recall。</item>
///   <item><see cref="WorkingMemory"/>：task state / short-term signal。</item>
///   <item><see cref="StableMemory"/>：long-term verified memory。</item>
///   <item><see cref="Graph"/>：relation expansion / traversal。</item>
///   <item><see cref="Recency"/>：recent_context / current_task。</item>
///   </list>
/// </remarks>
public enum RetrievalExpert : byte
{
    /// <summary>未知 Expert（仅用于契约默认值或历史 trace 升级）。</summary>
    Unknown = 0,

    /// <summary>Mandatory Expert：hard constraint / required tag / mandatory metadata。永不关闭。</summary>
    Mandatory = 1,

    /// <summary>Constraint Expert：constraint satisfaction / hard+soft+merged constraint。永不关闭。</summary>
    Constraint = 2,

    /// <summary>Lexical Expert：keyword / context recall（ContextRecallChannelExecutor）。</summary>
    Lexical = 3,

    /// <summary>Semantic Expert：vector recall（VectorRecallChannelExecutor）。</summary>
    Semantic = 4,

    /// <summary>WorkingMemory Expert：task state / short-term signal（MemoryRecallChannelExecutor 工作记忆部分）。</summary>
    WorkingMemory = 5,

    /// <summary>StableMemory Expert：long-term verified memory（MemoryRecallChannelExecutor 稳定记忆部分）。</summary>
    StableMemory = 6,

    /// <summary>Graph Expert：relation expansion / traversal（RelationRecallChannelExecutor）。</summary>
    Graph = 7,

    /// <summary>Recency Expert：recent_context / current_task（TaskState 信号或 Working Memory 副产品）。</summary>
    Recency = 8
}

/// <summary>
/// R20-1：Expert 路由决策。Router 为每个 Expert 计算的运行时决策。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Enabled=false 时该 Expert 不参与候选生成（但 Mandatory/Constraint 永远 Enabled=true）。
///   2. TopK 是 per-Expert 的 candidate 数量上限（用户澄清：模拟各 Expert 的 Top-K 质量—成本曲线）。
///   3. TokenBudget 是 per-Expert 的 token 预算上限；总和不超过 Request.TokenBudget。
///   4. Weight 是 Expert 在最终评分中的权重（0.0-1.0）；不影响候选生成，影响排序。
///   5. ReasonCode 标识路由原因（如 "default" / "ablation-disabled" /
///      "budget-reduced" / "policy-disabled"）。
/// </remarks>
public sealed record ExpertRoutingDecision
{
    /// <summary>Expert 类型（必填）。</summary>
    public required RetrievalExpert Expert { get; init; }

    /// <summary>是否启用此 Expert（false = 该 Expert 不参与候选生成）。</summary>
    /// <remarks>
    /// Mandatory / Constraint 两个 Expert 的此字段永远为 true（Router 无法关闭）。
    /// Router 计算后强制设为 true。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>per-Expert 的 candidate 数量上限（TopK）。</summary>
    /// <remarks>
    /// 0 或负值 = 使用 Request.TopK 或 Bundle.Budget.DefaultTopK 兜底。
    /// Router 通过 Budget-Aware 模拟调整此值以平衡质量—成本曲线。
    /// </remarks>
    public int TopK { get; init; } = 50;

    /// <summary>per-Expert 的 token 预算上限。</summary>
    /// <remarks>
    /// 0 或负值 = 使用 Request.TokenBudget 或 Bundle.Budget.DefaultTokenBudget 兜底。
    /// Router 通过 Budget Allocation 算法分配总预算到各 Expert。
    /// </remarks>
    public int TokenBudget { get; init; } = 1000;

    /// <summary>Expert 在最终评分中的权重（0.0-1.0）。</summary>
    /// <remarks>
    /// 不影响候选生成（即使 Weight=0，Expert 仍可生成候选）；
    /// 仅影响最终评分的 Expert 贡献权重。R20-2 Router 可调整此值。
    /// </remarks>
    public double Weight { get; init; } = 1.0;

    /// <summary>路由原因码（如 "default" / "ablation-disabled" / "budget-reduced"）。</summary>
    public string ReasonCode { get; init; } = "default";

    /// <summary>禁用原因详情（Enabled=false 时填充，人类可读）。</summary>
    public string? DisabledReason { get; init; }

    /// <summary>路由元数据（Router 自定义键值对，用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// R20-1：Expert 路由决策集合。承载 Router 对所有 8 个 Expert 的决策结果。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 决策集合是不可变快照；Router 每次执行生成新的 ExpertRoutingDecisionSet。
///   2. 强制 Mandatory / Constraint 永远 Enabled=true（构造时校验）。
///   3. 提供 O(1) 查询：IsExpertEnabled(expert) / GetDecision(expert)。
///   4. 不直接调用 HybridContextRetriever；Router 输出决策，调用方按决策执行。
/// </remarks>
public sealed record ExpertRoutingDecisionSet
{
    /// <summary>所有 Expert 的路由决策（按 <see cref="ExpertRoutingDecision.Expert"/> 索引）。</summary>
    public required IReadOnlyList<ExpertRoutingDecision> Decisions { get; init; }

    /// <summary>Router 执行时间（UTC）。</summary>
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Router 标识（如 "default-router" / "ablation-router-v1"）。</summary>
    public string RouterId { get; init; } = "default-router";

    /// <summary>Router 版本号（用于 trace 溯源）。</summary>
    public string RouterVersion { get; init; } = "v1";

    /// <summary>总 token 预算（所有 Expert TokenBudget 之和的上限）。</summary>
    public int TotalTokenBudget { get; init; }

    /// <summary>查询指定 Expert 是否启用（Mandatory/Constraint 永远返回 true）。</summary>
    public bool IsExpertEnabled(RetrievalExpert expert)
    {
        if (expert == RetrievalExpert.Mandatory || expert == RetrievalExpert.Constraint)
        {
            return true;
        }
        var decision = Decisions.FirstOrDefault(d => d.Expert == expert);
        return decision?.Enabled ?? false;
    }

    /// <summary>查询指定 Expert 的决策（未找到时返回 null）。</summary>
    public ExpertRoutingDecision? GetDecision(RetrievalExpert expert)
    {
        return Decisions.FirstOrDefault(d => d.Expert == expert);
    }
}

// ---------------------------------------------------------------------------
// RetrievalExpertMask（位掩码）
// ---------------------------------------------------------------------------

/// <summary>
/// R20-1：Expert 位掩码。用于 O(1) 判断 Expert 是否启用。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 使用 int 类型底层（避免 byte 溢出；8 个 Expert 对应 8 bit）。
///   2. Mandatory / Constraint 两个 bit 永远为 1（构造时强制）。
///   3. 位掩码不是运行时决策的替代，仅用于快速判断；
///      完整决策需查询 <see cref="ExpertRoutingDecisionSet"/>。
/// </remarks>
public readonly record struct RetrievalExpertMask
{
    /// <summary>底层位掩码值。</summary>
    public int Mask { get; init; }

    /// <summary>构造位掩码。</summary>
    public RetrievalExpertMask(int mask)
    {
        // 强制 Mandatory / Constraint 永远启用
        Mask = mask | MandatoryBit | ConstraintBit;
    }

    /// <summary>Mandatory Expert 的 bit 位（位 1）。</summary>
    public const int MandatoryBit = 1 << (int)RetrievalExpert.Mandatory;

    /// <summary>Constraint Expert 的 bit 位（位 2）。</summary>
    public const int ConstraintBit = 1 << (int)RetrievalExpert.Constraint;

    /// <summary>Lexical Expert 的 bit 位（位 3）。</summary>
    public const int LexicalBit = 1 << (int)RetrievalExpert.Lexical;

    /// <summary>Semantic Expert 的 bit 位（位 4）。</summary>
    public const int SemanticBit = 1 << (int)RetrievalExpert.Semantic;

    /// <summary>WorkingMemory Expert 的 bit 位（位 5）。</summary>
    public const int WorkingMemoryBit = 1 << (int)RetrievalExpert.WorkingMemory;

    /// <summary>StableMemory Expert 的 bit 位（位 6）。</summary>
    public const int StableMemoryBit = 1 << (int)RetrievalExpert.StableMemory;

    /// <summary>Graph Expert 的 bit 位（位 7）。</summary>
    public const int GraphBit = 1 << (int)RetrievalExpert.Graph;

    /// <summary>Recency Expert 的 bit 位（位 8）。</summary>
    public const int RecencyBit = 1 << (int)RetrievalExpert.Recency;

    /// <summary>所有 Expert 启用的掩码（包含 Mandatory / Constraint 强制位）。</summary>
    public static RetrievalExpertMask AllEnabled => new(0x1FF); // bit 0 (Unknown) + 1-8

    /// <summary>仅 Mandatory / Constraint 启用（其他全部关闭；用于 ablation）。</summary>
    public static RetrievalExpertMask MandatoryOnly => new(0);

    /// <summary>查询指定 Expert 是否启用（Mandatory/Constraint 永远 true）。</summary>
    public bool IsEnabled(RetrievalExpert expert)
    {
        if (expert == RetrievalExpert.Unknown) return false;
        if (expert == RetrievalExpert.Mandatory || expert == RetrievalExpert.Constraint)
        {
            return true;
        }
        return (Mask & (1 << (int)expert)) != 0;
    }

    /// <summary>启用或禁用指定 Expert（Mandatory/Constraint 操作被忽略，永远启用）。</summary>
    public RetrievalExpertMask With(RetrievalExpert expert, bool enabled)
    {
        if (expert == RetrievalExpert.Unknown) return this;
        if (expert == RetrievalExpert.Mandatory || expert == RetrievalExpert.Constraint)
        {
            return this; // 强制永远启用
        }
        var bit = 1 << (int)expert;
        var newMask = enabled ? (Mask | bit) : (Mask & ~bit);
        return new RetrievalExpertMask(newMask);
    }

    /// <summary>返回当前启用的所有 Expert 列表（按枚举顺序）。</summary>
    public IReadOnlyList<RetrievalExpert> GetEnabledExperts()
    {
        var result = new List<RetrievalExpert>(8);
        for (var i = (int)RetrievalExpert.Mandatory; i <= (int)RetrievalExpert.Recency; i++)
        {
            var expert = (RetrievalExpert)i;
            if (IsEnabled(expert))
            {
                result.Add(expert);
            }
        }
        return result;
    }

    /// <summary>统计启用 Expert 数量（包含 Mandatory / Constraint）。</summary>
    public int EnabledCount => GetEnabledExperts().Count;
}

// ---------------------------------------------------------------------------
// RetrievalExpertChannels（5 channel 对齐）
// ---------------------------------------------------------------------------

/// <summary>
/// R20-1：HybridContextRetriever 5 channel 与 Expert 的映射关系。
/// </summary>
/// <remarks>
/// 当前 HybridContextRetriever 有 5 个 channel executor：
///   1. MandatoryRecallChannelExecutor → <see cref="RetrievalExpert.Mandatory"/>
///   2. ContextRecallChannelExecutor (Keyword) → <see cref="RetrievalExpert.Lexical"/>
///   3. VectorRecallChannelExecutor (Semantic) → <see cref="RetrievalExpert.Semantic"/>
///   4. MemoryRecallChannelExecutor → <see cref="RetrievalExpert.WorkingMemory"/> + <see cref="RetrievalExpert.StableMemory"/>
///   5. RelationRecallChannelExecutor (Graph) → <see cref="RetrievalExpert.Graph"/>
///
/// 额外信号（不在 5 channel 中，由 channel 副产品或独立信号源提供）：
///   - <see cref="RetrievalExpert.Recency"/>：由 WorkingMemory channel 的 Task-State 副产品或独立 TaskState 信号提供。
///   - <see cref="RetrievalExpert.Constraint"/>：由 Mandatory channel 的 constraint 副产品或独立 ConstraintStore 查询提供。
///
/// 设计原则：
///   1. 映射是静态常量；不依赖运行时状态。
///   2. 一个 channel 可对应多个 Expert（如 MemoryRecallChannel → WorkingMemory + StableMemory）。
///   3. 一个 Expert 可对应多个 channel（如 Constraint 可来自 Mandatory channel + 独立 ConstraintStore）。
///   4. Router 通过此映射决定关闭某 Expert 时需禁用哪些 channel。
/// </remarks>
public static class RetrievalExpertChannels
{
    /// <summary>MandatoryRecall channel executor 名称（对齐 HybridContextRetriever 内部命名）。</summary>
    public const string MandatoryRecallChannel = "mandatory_recall";

    /// <summary>ContextRecall (Keyword) channel executor 名称。</summary>
    public const string ContextRecallChannel = "context_recall";

    /// <summary>VectorRecall (Semantic) channel executor 名称。</summary>
    public const string VectorRecallChannel = "vector_recall";

    /// <summary>MemoryRecall channel executor 名称（包含 WorkingMemory + StableMemory）。</summary>
    public const string MemoryRecallChannel = "memory_recall";

    /// <summary>RelationRecall (Graph) channel executor 名称。</summary>
    public const string RelationRecallChannel = "relation_recall";

    /// <summary>5 个 channel 名称列表（按 HybridContextRetriever 执行顺序）。</summary>
    public static IReadOnlyList<string> AllChannels { get; } = new[]
    {
        MandatoryRecallChannel,
        ContextRecallChannel,
        VectorRecallChannel,
        MemoryRecallChannel,
        RelationRecallChannel
    };

    /// <summary>channel → Expert 映射（一个 channel 可对应多个 Expert）。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<RetrievalExpert>> ChannelToExperts { get; }
        = new Dictionary<string, IReadOnlyList<RetrievalExpert>>(StringComparer.Ordinal)
        {
            [MandatoryRecallChannel] = new[] { RetrievalExpert.Mandatory },
            [ContextRecallChannel] = new[] { RetrievalExpert.Lexical },
            [VectorRecallChannel] = new[] { RetrievalExpert.Semantic },
            [MemoryRecallChannel] = new[] { RetrievalExpert.WorkingMemory, RetrievalExpert.StableMemory },
            [RelationRecallChannel] = new[] { RetrievalExpert.Graph }
        };

    /// <summary>Expert → channel 映射（一个 Expert 可对应多个 channel；Recency/Constraint 无独立 channel）。</summary>
    public static IReadOnlyDictionary<RetrievalExpert, IReadOnlyList<string>> ExpertToChannels { get; }
        = new Dictionary<RetrievalExpert, IReadOnlyList<string>>
        {
            [RetrievalExpert.Mandatory] = new[] { MandatoryRecallChannel },
            [RetrievalExpert.Constraint] = Array.Empty<string>(),  // 无独立 channel，由 Mandatory 副产品或独立查询
            [RetrievalExpert.Lexical] = new[] { ContextRecallChannel },
            [RetrievalExpert.Semantic] = new[] { VectorRecallChannel },
            [RetrievalExpert.WorkingMemory] = new[] { MemoryRecallChannel },
            [RetrievalExpert.StableMemory] = new[] { MemoryRecallChannel },
            [RetrievalExpert.Graph] = new[] { RelationRecallChannel },
            [RetrievalExpert.Recency] = Array.Empty<string>()  // 无独立 channel，由 WorkingMemory 副产品或 TaskState 信号
        };

    /// <summary>查询指定 channel 是否需要执行（基于 Expert Mask）。</summary>
    /// <param name="channelName">channel 名称。</param>
    /// <param name="mask">当前 Expert 掩码。</param>
    /// <returns>true = channel 至少有一个 Expert 启用，需要执行；false = 全部禁用，可跳过。</returns>
    /// <remarks>
    /// MandatoryRecallChannel 永远返回 true（Mandatory Expert 永不关闭）。
    /// MemoryRecallChannel 在 WorkingMemory + StableMemory 全部禁用时返回 false。
    /// </remarks>
    public static bool ShouldExecuteChannel(string channelName, RetrievalExpertMask mask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        if (!ChannelToExperts.TryGetValue(channelName, out var experts))
        {
            return false; // 未知 channel 不执行
        }
        foreach (var expert in experts)
        {
            if (mask.IsEnabled(expert))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>查询指定 Expert 是否有独立 channel（false = 由其他 channel 副产品或独立信号提供）。</summary>
    public static bool HasDedicatedChannel(RetrievalExpert expert)
    {
        return ExpertToChannels.TryGetValue(expert, out var channels) && channels.Count > 0;
    }
}
