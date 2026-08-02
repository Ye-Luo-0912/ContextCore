using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// 默认 hypothesis 模板表：为每个 <see cref="OptimizationTargetComponent"/> 提供预定义的
/// 标题、假设正文、预期收益、风险评估、回滚条件。
/// </summary>
/// <remarks>
/// 内部静态表，不暴露到 Abstractions。模板内的 <see cref="ExpectedGain.EstimatedDelta"/>
/// 同时表达 metric 改进方向：正值表示 metric 增大更好（如 accuracy），
/// 负值表示 metric 减小更好（如 latency_ms）。
/// <see cref="DefaultContextEvolutionAgent"/> 用模板 + observation 指标生成 Validated proposal；
/// <see cref="DefaultContextEvolutionAgent.RefineProposalAsync"/> 用 evidence 与模板方向对比决定 Status 推进。
/// </remarks>
internal static class HypothesisTemplates
{
    /// <summary>获取指定目标组件的 hypothesis 模板；不存在时返回 null。</summary>
    internal static HypothesisTemplate? TryGet(OptimizationTargetComponent component) => component switch
    {
        OptimizationTargetComponent.CostAwareRetrievalRouter => CostAwareRetrievalRouter,
        OptimizationTargetComponent.CandidateUtilityReranker => CandidateUtilityReranker,
        OptimizationTargetComponent.PackagePolicy => PackagePolicy,
        OptimizationTargetComponent.CachePolicy => CachePolicy,
        OptimizationTargetComponent.TokenizerSelection => TokenizerSelection,
        OptimizationTargetComponent.SectionAssembly => SectionAssembly,
        _ => null
    };

    private static readonly HypothesisTemplate CostAwareRetrievalRouter = new(
        Title: "Route low-utility retrievals to cheaper channel",
        Hypothesis: "对 utility 估值低于阈值的检索候选，改走低成本 channel（如 keyword-only），可在不显著降低召回率的前提下降低端到端检索成本。",
        ExpectedGains: new[]
        {
            new ExpectedGain("retrieval_cost_ms", -120.0, 0.80, new[] { "ItemCount >= 20", "HybridRetrieval enabled" }),
            new ExpectedGain("recall", -0.01, 0.85, new[] { "UtilityThreshold = 0.35" })
        },
        Risks: new[]
        {
            new RiskAssessment(
                "R-CARR-001",
                "阈值过高导致 high-utility 候选被误判为低成本路径，召回率下降",
                RiskSeverity.Medium,
                new[] { "recall < 0.85", "high_utility_miss_rate > 0.05" },
                new[] { "动态降低阈值并触发 reindex", "对 hybrid 模式保留 fallback" })
        },
        RollbackConditions: new[]
        {
            new RollbackCondition("recall", ComparisonOperator.LessThan, 0.80, "召回率低于 80% 触发回滚"),
            new RollbackCondition("retrieval_cost_ms", ComparisonOperator.GreaterThan, 0.0, "成本未降低（与基线相同或更高）触发回滚")
        });

    private static readonly HypothesisTemplate CandidateUtilityReranker = new(
        Title: "Tighten reranker score gap for boundary candidates",
        Hypothesis: "对 reranker 输出 score 集中在边界的候选执行二次重排，可在不增加端到端延迟的前提下提升 top-K 命中率。",
        ExpectedGains: new[]
        {
            new ExpectedGain("topk_hit_rate", 0.03, 0.78, new[] { "K = 5", "BoundaryBand = 0.05" }),
            new ExpectedGain("rerank_latency_ms", 0.5, 0.90, new[] { "BoundaryBand <= 0.10" })
        },
        Risks: new[]
        {
            new RiskAssessment(
                "R-CUR-001",
                "二次重排引入的延迟在 p99 突破预算",
                RiskSeverity.High,
                new[] { "rerank_latency_ms > 5.0" },
                new[] { "对超过预算的请求跳过二次重排", "BoundaryBand 自适应收敛" })
        },
        RollbackConditions: new[]
        {
            new RollbackCondition("rerank_latency_ms", ComparisonOperator.GreaterThan, 5.0, "重排延迟突破 5ms 触发回滚"),
            new RollbackCondition("topk_hit_rate", ComparisonOperator.LessThan, 0.0, "命中率未提升（与基线相同或更低）触发回滚")
        });

    private static readonly HypothesisTemplate PackagePolicy = new(
        Title: "Reduce recent_context token budget for cold path",
        Hypothesis: "降低 recent_context section 的 token 预算可在不损失召回率的前提下减少 cold path duration。",
        ExpectedGains: new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, new[] { "ItemCount >= 50", "TokenBudget >= 4000" }),
            new ExpectedGain("allocation_kb", -200.0, 0.88, new[] { "ItemCount >= 50" })
        },
        Risks: new[]
        {
            new RiskAssessment(
                "R-PP-001",
                "section 内容不足导致关键 context 丢失",
                RiskSeverity.Medium,
                new[] { "TokenBudget < 2000", "section_completeness < 0.85" },
                new[] { "动态调整 RecentContextBudget", "对 query 包含 short-term 标记时跳过压缩" })
        },
        RollbackConditions: new[]
        {
            new RollbackCondition("cache_hit_rate", ComparisonOperator.LessThan, 0.80, "缓存命中率低于 80% 触发回滚"),
            new RollbackCondition("duration_ms", ComparisonOperator.GreaterThan, 0.0, "duration 未降低（与基线相同或更高）触发回滚")
        });

    private static readonly HypothesisTemplate CachePolicy = new(
        Title: "Extend TTL for high-utility context entries",
        Hypothesis: "对 utility >= 0.6 的 context 项延长 TTL，可在不增加内存占用上限的前提下提升缓存命中率。",
        ExpectedGains: new[]
        {
            new ExpectedGain("cache_hit_rate", 0.05, 0.82, new[] { "HighUtilityThreshold = 0.6" }),
            new ExpectedGain("eviction_rate", -0.10, 0.75, new[] { "MaxCacheEntries = 10000" })
        },
        Risks: new[]
        {
            new RiskAssessment(
                "R-CP-001",
                "高 utility 项长期占用导致低 utility 但 fresh 的项被过早驱逐",
                RiskSeverity.Medium,
                new[] { "eviction_rate > 0.30", "freshness_score < 0.5" },
                new[] { "对 TTL 上限设 2x 原值", "freshness 低于阈值时强制降级" })
        },
        RollbackConditions: new[]
        {
            new RollbackCondition("cache_hit_rate", ComparisonOperator.LessThan, 0.0, "命中率未提升触发回滚"),
            new RollbackCondition("eviction_rate", ComparisonOperator.GreaterThan, 0.30, "驱逐率超过 30% 触发回滚")
        });

    private static readonly HypothesisTemplate TokenizerSelection = new(
        Title: "Switch high-density sections to byte-level tokenizer",
        Hypothesis: "对高 token 密度的 section 切换到 byte-level tokenizer，可在不损失语义保真度的前提下降低 token 估算误差。",
        ExpectedGains: new[]
        {
            new ExpectedGain("token_estimation_error", -0.05, 0.70, new[] { "HighDensityThreshold = 0.8" }),
            new ExpectedGain("package_size_token", -50, 0.65, new[] { "ItemCount >= 30" })
        },
        Risks: new[]
        {
            new RiskAssessment(
                "R-TS-001",
                "byte-level tokenizer 引入 encoding 不一致导致下游模型截断",
                RiskSeverity.High,
                new[] { "truncation_rate > 0.05", "encoding_mismatch_count > 0" },
                new[] { "保留 sentence-level tokenizer 作为 fallback", "下游模型契约层校验 encoding" })
        },
        RollbackConditions: new[]
        {
            new RollbackCondition("truncation_rate", ComparisonOperator.GreaterThan, 0.05, "截断率超过 5% 触发回滚"),
            new RollbackCondition("token_estimation_error", ComparisonOperator.GreaterThan, 0.0, "估算误差未降低触发回滚")
        });

    private static readonly HypothesisTemplate SectionAssembly = new(
        Title: "Promote high-utility sections to head of package",
        Hypothesis: "对 utility 排名前 2 的 section 提升到 package 头部，可在不改变 token 预算的前提下提升首 token 命中率。",
        ExpectedGains: new[]
        {
            new ExpectedGain("first_token_hit_rate", 0.04, 0.72, new[] { "TopK = 2", "UtilityBand = 0.7" }),
            new ExpectedGain("section_balance", 0.02, 0.68, new[] { "MinSectionCount = 3" })
        },
        Risks: new[]
        {
            new RiskAssessment(
                "R-SA-001",
                "头部 section 占用过多预算导致尾部 section 被截断",
                RiskSeverity.Medium,
                new[] { "section_balance < 0.6", "truncated_section_count > 0" },
                new[] { "对头部 section 设上限", "尾部 section 保留 reserve" })
        },
        RollbackConditions: new[]
        {
            new RollbackCondition("first_token_hit_rate", ComparisonOperator.LessThan, 0.0, "首 token 命中率未提升触发回滚"),
            new RollbackCondition("section_balance", ComparisonOperator.LessThan, 0.5, "section balance 低于 0.5 触发回滚")
        });
}

/// <summary>hypothesis 模板：包含标题、假设正文、预期收益、风险评估、回滚条件。</summary>
internal sealed record HypothesisTemplate(
    string Title,
    string Hypothesis,
    IReadOnlyList<ExpectedGain> ExpectedGains,
    IReadOnlyList<RiskAssessment> Risks,
    IReadOnlyList<RollbackCondition> RollbackConditions);
