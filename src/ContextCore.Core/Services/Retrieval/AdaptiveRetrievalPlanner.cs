using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

// ===========================================================================
// AdaptiveRetrievalPlanner —— 自适应检索规划器（反馈驱动的策略调整装饰器）
// 
// 目标：
// 在确定性受控规划器（IAgentRetrievalQueryPlanner）之上叠加反馈驱动的自适应层：
// 按计划签名聚合近期检索结果（命中数 / 预算超限 / 有效性），计算自适应策略
// （Token 预算乘数 / 查询收敛乘数 / 召回增强乘数），后续规划时应用该策略。
// 
// 自适应语义（Effective 样本数 ≥ MinFeedbackSamples 才生效；加固）：
// 1. 只采用 Effective 样本（未被实际采用的结果不参与学习）。
// 2. 加权指标：权重 = 置信度 × 结果质量 × 时间衰减（0.5^(age/半衰期)），
// 单主体（Subject）贡献封顶（默认 5 条），防止单源低质量 / 恶意反馈主导策略。
// 3. 加权预算超限率 ≥ 0.5 → TokenBudgetMultiplier=0.75 + QueryConvergenceMultiplier=0.75
// （收敛预算与查询集，避免反复撞墙）。
// 4. 加权平均命中数 < 1.0 → RecallBoostMultiplier=1.25（增强查询权重扩大召回）。
// 5. 样本不足或指标未达阈值 → 中性默认（1.0 / 1.0 / 1.0）。
// 
// 运行模式（AdaptiveRetrievalOptions.Mode）：
// - Disabled（默认，fail-closed）：PlanAsync 完全透传底层计划，不读写反馈存储。
// - Shadow：计算策略但不应用，仅观察学习信号（验证无副作用后再启用）。
// - Active：计算并应用策略。
// 
// 设计原则：
// - 底层规划器保持确定性 / 幂等：自适应仅调整规划参数；给定相同输入 +
// 相同反馈状态，仍产生相同计划（可审计、可回归）。
// - 纯内存计算：除反馈存储读写外不调用任何存储 / 检索执行器。
// - 空输入仍产出受控计划（透传底层计划，绝不抛异常）。
// ===========================================================================

/// <summary>
/// 自适应检索规划器默认实现：装饰 <see cref="IAgentRetrievalQueryPlanner"/>，
/// 按计划签名应用反馈驱动的策略调整。
/// </summary>
public sealed class AdaptiveRetrievalPlanner : IAdaptiveRetrievalPlanner
{
    /// <summary>策略聚合时读取的近期反馈条数上限（默认值；可经 AdaptiveRetrievalOptions 覆盖）。</summary>
    public const int FeedbackLookbackLimit = 20;

    /// <summary>预算超限率阈值（≥ 时触发收敛策略）。</summary>
    private const double BudgetExceededRateThreshold = 0.5;

    /// <summary>预算超限时的 Token 预算乘数。</summary>
    private const double BudgetConvergeTokenMultiplier = 0.75;

    /// <summary>预算超限时的查询收敛乘数。</summary>
    private const double BudgetConvergeQueryMultiplier = 0.75;

    /// <summary>平均命中数阈值（&lt; 时触发召回增强）。</summary>
    private const double LowHitThreshold = 1.0;

    /// <summary>召回增强乘数（低命中时提升查询权重）。</summary>
    private const double RecallBoostMultiplierValue = 1.25;

    /// <summary>查询权重上限（增强后钳制，防止权重失控）。</summary>
    private const double MaxQueryWeight = 2.0;

    private readonly IAgentRetrievalQueryPlanner _inner;
    private readonly IRetrievalPlanFeedbackStore _feedbackStore;
    private readonly AdaptiveRetrievalOptions _options;

    // 签名 → 缓存策略（TTL 内复用，避免每轮规划都读取近期反馈重新聚合；
    // 记录新反馈时立即失效对应签名，下次读取即重算）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedPolicy> _policyCache =
        new(System.StringComparer.Ordinal);

    /// <summary>缓存条目：策略 + 缓存时刻（TTL 判定依据）。</summary>
    private sealed record CachedPolicy(AdaptiveRetrievalPolicy Policy, DateTimeOffset CachedAtUtc);

    public AdaptiveRetrievalPlanner(
        IAgentRetrievalQueryPlanner inner,
        IRetrievalPlanFeedbackStore feedbackStore,
        AdaptiveRetrievalOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _feedbackStore = feedbackStore ?? throw new ArgumentNullException(nameof(feedbackStore));
        _options = options ?? new AdaptiveRetrievalOptions();
    }

    /// <inheritdoc />
    public async Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var basePlan = _inner.Plan(input, ct);

        // Disabled（默认，fail-closed）：自适应层完全不读写反馈存储，透传底层计划。
        if (_options.Mode == AdaptiveRetrievalMode.Disabled)
        {
            return basePlan;
        }

        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        var policy = await GetCachedPolicyAsync(signature, NormalizeWorkspace(input.WorkspaceId), ct).ConfigureAwait(false);

        // Shadow：计算策略但不应用（观察学习信号，验证无副作用后再启用）。
        if (_options.Mode == AdaptiveRetrievalMode.Shadow)
        {
            return basePlan;
        }

        return ApplyPolicy(basePlan, policy);
    }

    /// <inheritdoc />
    public async ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ct.ThrowIfCancellationRequested();

        // Disabled 模式不写反馈存储：自适应层完全旁路，不产生任何学习信号
        // （与 PlanAsync 的 fail-closed 透传语义一致，避免"不应用策略却仍收集反馈"的隐式副作用）。
        if (_options.Mode == AdaptiveRetrievalMode.Disabled)
        {
            return;
        }

        // 清洗：保证 FeedbackId / 数值字段在合法范围内、Source 为合法枚举值，
        // 防止脏数据 / 恶意大值扭曲加权策略；WorkspaceId 归一为 trim 后的非空值
        // （隔离边界：记录必须归属到具体工作区，缺失按全局默认工作区处理）。
        var sanitized = feedback with
        {
            WorkspaceId = NormalizeWorkspace(feedback.WorkspaceId),
            FeedbackId = string.IsNullOrWhiteSpace(feedback.FeedbackId)
                ? Guid.NewGuid().ToString("N")
                : feedback.FeedbackId,
            HitsReturned = Math.Clamp(feedback.HitsReturned, 0, Math.Max(0, _options.MaxHitsClamp)),
            Confidence = Math.Clamp(feedback.Confidence, 0.0, 1.0),
            OutcomeQuality = Math.Clamp(feedback.OutcomeQuality, 0.0, 1.0),
            Source = Enum.IsDefined(feedback.Source) ? feedback.Source : RetrievalFeedbackSource.Runtime
        };

        await _feedbackStore.RecordAsync(sanitized, ct).ConfigureAwait(false);

        // 新反馈立即失效该签名的缓存策略——下次读取即反映最新学习信号（不等 TTL 过期）。
        _policyCache.TryRemove(sanitized.PlanSignature, out _);
    }

    /// <inheritdoc />
    public async ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        return await GetCachedPolicyAsync(signature, NormalizeWorkspace(input.WorkspaceId), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(string workspaceId, string planSignature, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planSignature);
        return await GetCachedPolicyAsync(planSignature, NormalizeWorkspace(workspaceId), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(
        string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default)
        => _feedbackStore.ListRecentAsync(NormalizeWorkspace(workspaceId), planSignature, limit, ct);

    /// <inheritdoc />
    public async ValueTask<int> ResetAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default)
    {
        var cleared = await _feedbackStore.ClearAsync(
            workspaceId is null ? null : NormalizeWorkspace(workspaceId), planSignature, ct).ConfigureAwait(false);
        if (planSignature is null)
        {
            _policyCache.Clear();
        }
        else
        {
            _policyCache.TryRemove(planSignature, out _);
        }
        return cleared;
    }

    // ── 策略计算 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 按签名获取策略：缓存命中且未过期 → 直接返回；否则读取近期反馈重算并写入缓存。
    /// 读取以工作区为作用域（隔离边界：跨工作区的相同签名不共享反馈）。
    /// TTL 非正时禁用缓存（每次重算，行为等价于无缓存版本）。
    /// </summary>
    private async ValueTask<AdaptiveRetrievalPolicy> GetCachedPolicyAsync(string signature, string workspaceId, CancellationToken ct)
    {
        var ttl = _options.PolicyCacheTtl;
        var now = DateTimeOffset.UtcNow;
        if (ttl > TimeSpan.Zero
            && _policyCache.TryGetValue(signature, out var cached)
            && now - cached.CachedAtUtc < ttl)
        {
            return cached.Policy;
        }

        var recent = await _feedbackStore.ListRecentAsync(workspaceId, signature, _options.FeedbackLookbackLimit, ct).ConfigureAwait(false);
        var policy = ComputePolicy(signature, recent, _options);
        SetCachedPolicy(signature, policy, ttl);
        return policy;
    }

    /// <summary>工作区 ID 归一：null/空白按全局默认工作区（空字符串）处理。</summary>
    private static string NormalizeWorkspace(string? workspaceId) => (workspaceId ?? string.Empty).Trim();

    /// <summary>写入缓存并做上限保护：超过最大条目数时先淘汰过期条目，仍超限则移除最旧条目。</summary>
    private void SetCachedPolicy(string signature, AdaptiveRetrievalPolicy policy, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        _policyCache[signature] = new CachedPolicy(policy, DateTimeOffset.UtcNow);

        if (_policyCache.Count <= _options.PolicyCacheMaxEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expired = _policyCache
            .Where(kvp => now - kvp.Value.CachedAtUtc >= ttl)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
        {
            _policyCache.TryRemove(key, out _);
        }

        if (_policyCache.Count > _options.PolicyCacheMaxEntries)
        {
            var eldest = _policyCache
                .OrderBy(kvp => kvp.Value.CachedAtUtc)
                .FirstOrDefault();
            if (eldest.Key is not null)
            {
                _policyCache.TryRemove(eldest.Key, out _);
            }
        }
    }

    private static AdaptiveRetrievalPolicy ComputePolicy(
        string signature,
        IReadOnlyList<RetrievalPlanFeedback> recent,
        AdaptiveRetrievalOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        var minSamples = Math.Max(1, options.MinFeedbackSamples);

        // 1. 只采用 Effective 样本（未被实际采用的结果不参与策略学习）。
        var effective = recent.Where(f => f.Effective).ToArray();

        // 2. 单主体贡献封顶：Subject 相同者只保留最近 MaxSamplesPerSubject 条；
        // 未归属主体（匿名）的样本各自独立，不封顶（无法归因，向后兼容匿名记录）。
        var capped = CapPerSubject(effective, options.MaxSamplesPerSubject);

        if (capped.Count < minSamples)
        {
            return new AdaptiveRetrievalPolicy
            {
                PlanSignature = signature,
                TokenBudgetMultiplier = 1.0,
                QueryConvergenceMultiplier = 1.0,
                RecallBoostMultiplier = 1.0,
                FeedbackSampleCount = capped.Count,
                ComputedAtUtc = now,
                Note = $"有效反馈样本不足（{capped.Count}/{minSamples}），策略为中性默认。"
            };
        }

        // 3. 加权指标：weight = Confidence × OutcomeQuality × 时间衰减（0.5^(age/半衰期)）。
        var halfLife = options.DecayHalfLife;
        double totalWeight = 0.0, exceededWeight = 0.0, hitsWeight = 0.0;
        foreach (var f in capped)
        {
            var age = now - f.RecordedAtUtc;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }
            var decay = halfLife > TimeSpan.Zero
                ? Math.Pow(0.5, age.TotalHours / halfLife.TotalHours)
                : 1.0;
            var w = f.Confidence * f.OutcomeQuality * decay;
            if (w <= 0.0)
            {
                continue; // 零权重样本不参与（等价于被抑制）
            }
            totalWeight += w;
            if (f.BudgetExceeded)
            {
                exceededWeight += w;
            }
            hitsWeight += w * f.HitsReturned;
        }

        if (totalWeight <= 0.0)
        {
            return new AdaptiveRetrievalPolicy
            {
                PlanSignature = signature,
                TokenBudgetMultiplier = 1.0,
                QueryConvergenceMultiplier = 1.0,
                RecallBoostMultiplier = 1.0,
                FeedbackSampleCount = capped.Count,
                ComputedAtUtc = now,
                Note = "有效反馈加权权重为零（置信度 / 质量 / 时间衰减），策略为中性默认。"
            };
        }

        var exceededRate = exceededWeight / totalWeight;
        var avgHits = hitsWeight / totalWeight;

        var tokenMultiplier = exceededRate >= BudgetExceededRateThreshold ? BudgetConvergeTokenMultiplier : 1.0;
        var queryMultiplier = exceededRate >= BudgetExceededRateThreshold ? BudgetConvergeQueryMultiplier : 1.0;
        var recallBoost = avgHits < LowHitThreshold ? RecallBoostMultiplierValue : 1.0;

        var noteParts = new System.Collections.Generic.List<string>(2);
        if (tokenMultiplier < 1.0)
        {
            noteParts.Add($"预算超限率 {exceededRate:P0} ≥ 50%，Token 预算下调至 {tokenMultiplier:P0}、查询收敛至 {queryMultiplier:P0}");
        }
        if (recallBoost > 1.0)
        {
            noteParts.Add($"平均命中 {avgHits:F1} < 1，召回权重增强至 {recallBoost:P0}");
        }
        if (noteParts.Count == 0)
        {
            noteParts.Add("近期检索指标健康，策略保持中性");
        }

        return new AdaptiveRetrievalPolicy
        {
            PlanSignature = signature,
            TokenBudgetMultiplier = tokenMultiplier,
            QueryConvergenceMultiplier = queryMultiplier,
            RecallBoostMultiplier = recallBoost,
            FeedbackSampleCount = capped.Count,
            ComputedAtUtc = now,
            Note = string.Join("；", noteParts) + "。"
        };
    }

    /// <summary>单主体贡献封顶：每个 Subject 只保留最近 maxPerSubject 条（按记录时间倒序）。</summary>
    private static List<RetrievalPlanFeedback> CapPerSubject(IReadOnlyList<RetrievalPlanFeedback> samples, int maxPerSubject)
    {
        if (maxPerSubject <= 0)
        {
            return samples.ToList(); // 未配置上限：不截断
        }

        return samples
            .Select((f, index) => (
                Feedback: f,
                // 匿名（无 Subject）样本各用唯一键，视为独立主体，不参与封顶。
                Key: string.IsNullOrWhiteSpace(f.Subject) ? "\u0000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : f.Subject))
            .OrderByDescending(t => t.Feedback.RecordedAtUtc)
            .GroupBy(t => t.Key, StringComparer.Ordinal)
            .SelectMany(g => g.Take(maxPerSubject).Select(t => t.Feedback))
            .ToList();
    }

    // ── 策略应用 ─────────────────────────────────────────────────────────────

    private static AgentRetrievalPlan ApplyPolicy(AgentRetrievalPlan basePlan, AdaptiveRetrievalPolicy policy)
    {
        var queries = basePlan.ControlledQueries;
        var note = string.Empty;

        // 1. Token 预算：乘数缩放 + 钳制到底层规划器的 [Min, Max] 区间
        var tokenBudget = (int)Math.Round(basePlan.TokenBudget * policy.TokenBudgetMultiplier, MidpointRounding.AwayFromZero);
        tokenBudget = Math.Clamp(tokenBudget, DefaultAgentRetrievalQueryPlanner.MinTokenBudget, DefaultAgentRetrievalQueryPlanner.MaxTokenBudget);
        if (tokenBudget != basePlan.TokenBudget)
        {
            note = $"[自适应] Token 预算 {basePlan.TokenBudget} → {tokenBudget}";
        }

        // 2. 查询收敛：按权重保留前 ceil(count × 乘数) 条，保持原始相对顺序
        if (policy.QueryConvergenceMultiplier < 1.0 && queries.Count > 1)
        {
            var keep = Math.Max(1, (int)Math.Ceiling(queries.Count * policy.QueryConvergenceMultiplier));
            queries = queries
                .Select((q, index) => (Query: q, Index: index))
                .OrderByDescending(t => t.Query.Weight)
                .ThenBy(t => t.Index)
                .Take(keep)
                .OrderBy(t => t.Index)
                .Select(t => t.Query)
                .ToArray();
            note = string.Concat(note, string.IsNullOrEmpty(note) ? string.Empty : "；", $"[自适应] 查询收敛 {keep}/{basePlan.ControlledQueries.Count}");
        }

        // 3. 召回增强：提升查询权重（钳制上限），扩大召回
        if (policy.RecallBoostMultiplier > 1.0 && queries.Count > 0)
        {
            queries = queries
                .Select(q => q with { Weight = Math.Min(MaxQueryWeight, q.Weight * policy.RecallBoostMultiplier) })
                .ToArray();
            note = string.Concat(note, string.IsNullOrEmpty(note) ? string.Empty : "；", "[自适应] 召回权重增强");
        }

        var reason = basePlan.Reason;
        if (!string.IsNullOrEmpty(note))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? note : $"{reason.TrimEnd()} {note}。";
        }

        return basePlan with
        {
            ControlledQueries = queries,
            TokenBudget = tokenBudget,
            Reason = reason
        };
    }
}
