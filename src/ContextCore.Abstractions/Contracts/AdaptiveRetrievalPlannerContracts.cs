// ===========================================================================
// Adaptive Retrieval Planner —— 自适应检索规划器契约
// ===========================================================================
// 角色：在确定性受控规划器（IAgentRetrievalQueryPlanner）之上增加反馈驱动的
// 自适应层：记录每轮检索的实际结果（命中数 / 预算是否超限 / 是否有效），
// 按计划签名聚合为自适应策略（Token 预算乘数 / 查询收敛乘数 / 召回增强乘数），
// 后续规划时应用该策略调整输出，让检索策略随真实执行效果收敛，而非静态启发式。
// 
// 设计决策：
// 1. 底层规划器保持确定性 / 幂等：自适应仅调整规划参数（预算 / 查询收敛 /
// 权重），给定相同输入 + 相同反馈状态，仍产生相同计划（可审计、可回归）。
// 2. 反馈按"计划签名"聚合（加固）：签名由输入（任务 + 意图 + 未解决目标
// + 工作区 + 集合 + 用途 + 策略版本 + 检索画像 + 任务类别）经 SHA-256 确定性派生，
// 带标签字段以控制字符分隔拼接，杜绝字段边界混淆；同一租户 / 用途 / 策略形态
// 的检索任务共享自适应状态，跨 Workspace 的相同任务文本绝不共享反馈——
// 一个租户无法通过低质量 / 恶意反馈改变另一个租户的 Token Budget 与检索权重。
// 3. 反馈可信度（加固）：每条反馈携带 FeedbackId / IdempotencyKey /
// Source / Confidence / OutcomeQuality / Effective / Subject；策略计算只采用
// Effective 样本，按 置信度 × 结果质量 × 时间衰减 加权，单主体（Subject）贡献
// 封顶，防止单个低质量 / 恶意来源主导策略。
// 4. 运行模式（AdaptiveRetrievalMode）：Disabled（默认，自适应不生效，fail-closed）/
// Shadow（计算策略但不应用，仅观察）/ Active（应用策略）；由运维显式开启。
// 5. 持久化：反馈记录跨进程重启保留（Postgres 实现为 retrieval_plan_feedback 表，
// 含幂等唯一索引），进程内 InMemory 实现供单节点 / 测试使用。
// 6. 正交于决策引擎：本规划器回答"检索什么 / 多少预算"，不替代候选排序。
// ===========================================================================

namespace ContextCore.Abstractions;

/// <summary>
/// 反馈来源（Source 字段：审计反馈由谁产生，防止匿名 / 外部来源污染）。
/// </summary>
public enum RetrievalFeedbackSource
{
    /// <summary>运行时检索执行方自动记录（AgentRunActor ContextBuilding 阶段）。</summary>
    Runtime = 0,

    /// <summary>运维人工通过管理端点记录。</summary>
    Operator = 1,

    /// <summary>自动化评测（离线 / 金标数据集回放）。</summary>
    AutomatedEvaluation = 2
}

/// <summary>
/// 自适应层运行模式（保护就绪前默认关闭或仅 Shadow，由运维显式开启）。
/// </summary>
public enum AdaptiveRetrievalMode
{
    /// <summary>关闭：规划器完全透传底层计划，不读写反馈存储（默认，fail-closed）。</summary>
    Disabled = 0,

    /// <summary>影子：照常计算策略但不应用到计划（观察学习信号，验证无副作用）。</summary>
    Shadow = 1,

    /// <summary>生效：计算策略并应用到后续规划。</summary>
    Active = 2
}

/// <summary>
/// 自适应检索规划器配置（绑定 "AdaptiveRetrieval" 配置节；未配置时全部使用默认值）。
/// </summary>
public sealed class AdaptiveRetrievalOptions
{
    /// <summary>运行模式（默认 Disabled——自适应层默认不生效）。</summary>
    public AdaptiveRetrievalMode Mode { get; set; } = AdaptiveRetrievalMode.Disabled;

    /// <summary>触发自适应所需的最小 Effective 反馈样本数（低于则策略中性）。</summary>
    public int MinFeedbackSamples { get; set; } = IAdaptiveRetrievalPlanner.MinFeedbackSamples;

    /// <summary>策略聚合时读取的近期反馈条数上限。</summary>
    public int FeedbackLookbackLimit { get; set; } = 20;

    /// <summary>时间衰减半衰期（反馈权重随年龄按 0.5^(age/halfLife) 衰减）。</summary>
    public TimeSpan DecayHalfLife { get; set; } = TimeSpan.FromHours(24);

    /// <summary>单主体（Subject）在策略计算中的最大样本贡献（防单源主导 / 投毒）。</summary>
    public int MaxSamplesPerSubject { get; set; } = 5;

    /// <summary>命中数上限（记录时钳制，防异常大值扭曲加权平均）。</summary>
    public int MaxHitsClamp { get; set; } = 100;

    /// <summary>策略缓存 TTL：同一计划签名在 TTL 内复用已计算策略，
    /// 避免每轮规划都读取近期反馈重新聚合（记录新反馈时立即失效对应签名）。</summary>
    public TimeSpan PolicyCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>策略缓存最大条目数（超过时淘汰过期/最旧条目，防无界增长）。</summary>
    public int PolicyCacheMaxEntries { get; set; } = 512;
}

/// <summary>
/// 单轮检索结果反馈（自适应规划器的学习信号）。
/// 由检索执行方在每轮检索后记录（调用 <see cref="IAdaptiveRetrievalPlanner.RecordOutcomeAsync"/>）。
/// </summary>
public sealed record RetrievalPlanFeedback
{
    /// <summary>计划签名（由输入确定性派生，见 <see cref="IAdaptiveRetrievalPlanner"/>）。</summary>
    public required string PlanSignature { get; init; }

    /// <summary>
    /// 所属工作区（隔离边界）。控制面记录/查询/清除反馈必须以工作区为作用域——
    /// 服务端从请求上下文解析，客户端不得伪造其他租户的签名。
    /// </summary>
    public required string WorkspaceId { get; init; }

    /// <summary>集合 ID（签名租户维度之一，结构化审计列）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>用途（签名租户维度之一，结构化审计列）。</summary>
    public string? Purpose { get; init; }

    /// <summary>策略版本（签名租户维度之一，结构化审计列）。</summary>
    public string? PolicyVersion { get; init; }

    /// <summary>检索画像（签名租户维度之一，结构化审计列）。</summary>
    public string? RetrievalProfile { get; init; }

    /// <summary>任务类别（签名租户维度之一，结构化审计列）。</summary>
    public string? TaskClass { get; init; }

    /// <summary>本轮主导查询文本（诊断用）。</summary>
    public string QueryText { get; init; } = string.Empty;

    /// <summary>本轮返回的命中数。</summary>
    public int HitsReturned { get; init; }

    /// <summary>是否超出 Token 预算（超限应触发收敛策略）。</summary>
    public bool BudgetExceeded { get; init; }

    /// <summary>本轮检索结果是否被实际采用（有效信号；false = 结果未被使用，不计入策略）。</summary>
    public bool Effective { get; init; } = true;

    /// <summary>记录时间（UTC）。</summary>
    public required DateTimeOffset RecordedAtUtc { get; init; }

    /// <summary>反馈唯一标识（缺省由规划器生成，用于审计追溯）。</summary>
    public string? FeedbackId { get; init; }

    /// <summary>
    /// 幂等键（可选）：相同 (PlanSignature, IdempotencyKey) 只保留首条，
    /// 重放 / 重复提交不产生重复反馈。
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>反馈来源（Runtime / Operator / AutomatedEvaluation）。</summary>
    public RetrievalFeedbackSource Source { get; init; } = RetrievalFeedbackSource.Runtime;

    /// <summary>置信度（0–1，记录时钳制；默认 1.0）。</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>结果质量（0–1，记录时钳制；默认 1.0）。</summary>
    public double OutcomeQuality { get; init; } = 1.0;

    /// <summary>主体标识（可选：产生该反馈的 Workspace / 用户 / 评测用例等；策略计算按主体封顶贡献）。</summary>
    public string? Subject { get; init; }
}

/// <summary>
/// 自适应检索策略（由近期反馈聚合计算）。
/// </summary>
public sealed record AdaptiveRetrievalPolicy
{
    /// <summary>计划签名（本策略对应的签名）。</summary>
    public required string PlanSignature { get; init; }

    /// <summary>
    /// Token 预算乘数（0.5–1.0）：近期预算频繁超限时 <b>下调</b>（默认 1.0）。
    /// </summary>
    public required double TokenBudgetMultiplier { get; init; }

    /// <summary>
    /// 查询收敛乘数（0.5–1.0）：近期预算频繁超限时 <b>收敛</b>查询集
    /// （按权重保留前 ceil(count × 乘数) 条；默认 1.0 不收敛）。
    /// </summary>
    public required double QueryConvergenceMultiplier { get; init; }

    /// <summary>
    /// 召回增强乘数（1.0–1.5）：近期命中持续偏低时 <b>增强</b>查询权重
    /// 以扩大召回（默认 1.0 不增强）。
    /// </summary>
    public required double RecallBoostMultiplier { get; init; }

    /// <summary>参与聚合的反馈样本数（&lt; 阈值时策略为中性默认值）。</summary>
    public required int FeedbackSampleCount { get; init; }

    /// <summary>策略计算时间（UTC）。</summary>
    public required DateTimeOffset ComputedAtUtc { get; init; }

    /// <summary>策略说明（中文，解释为何采用当前乘数；用于审计与调试）。</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 检索计划反馈的持久化存储。
/// </summary>
public interface IRetrievalPlanFeedbackStore
{
    /// <summary>记录一条检索结果反馈（同 (PlanSignature, IdempotencyKey) 幂等去重）。
    /// 反馈必须携带 <see cref="RetrievalPlanFeedback.WorkspaceId"/>——按工作区隔离存储。</summary>
    ValueTask RecordAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default);

    /// <summary>列出指定工作区内、指定签名最近 N 条反馈（按记录时间倒序，最新在前）。
    /// 工作区为隔离边界：跨工作区的相同签名不共享反馈。</summary>
    ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListRecentAsync(string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default);

    /// <summary>清除反馈。workspaceId 为 null 时清除全部工作区（全局重置，需更高权限）；
    /// planSignature 为 null 时清除该工作区全部；否则仅清除该工作区内的该签名。返回清除条数。</summary>
    ValueTask<int> ClearAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default);
}

/// <summary>
/// 自适应检索规划器：在确定性受控规划器之上叠加反馈驱动的策略调整。
/// </summary>
/// <remarks>
/// 自适应语义（按签名聚合近期 Effective 反馈，样本数 ≥ <see cref="MinFeedbackSamples"/> 才生效；
/// 权重 = 置信度 × 结果质量 × 时间衰减，单主体贡献封顶）：
/// <list type="bullet">
/// <item>加权预算超限率 ≥ 0.5 → TokenBudgetMultiplier=0.75 + QueryConvergenceMultiplier=0.75
/// （收敛预算与查询集，避免反复撞墙）。</item>
/// <item>加权平均命中数 &lt; 1.0 → RecallBoostMultiplier=1.25（增强查询权重扩大召回）。</item>
/// <item>样本不足或指标未达阈值 → 中性默认（1.0 / 1.0 / 1.0）。</item>
/// </list>
/// 调整仅作用于规划参数，底层规划器本身保持确定性；给定相同输入 + 相同反馈状态，
/// <see cref="Plan"/> 仍产生确定输出。运行模式见 <see cref="AdaptiveRetrievalMode"/>。
/// </remarks>
public interface IAdaptiveRetrievalPlanner
{
    /// <summary>自适应规划器触发自适应所需的最小反馈样本数（低于则策略中性）。</summary>
    const int MinFeedbackSamples = 10;

    /// <summary>
    /// 规划本轮受控检索：先按输入派生计划签名，从反馈存储读取近期反馈计算策略，
    /// 再由底层规划器产出基础计划并应用策略调整。
    /// </summary>
    Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default);

    /// <summary>记录一轮检索结果反馈（携带计划签名，见 <see cref="RetrievalPlanFeedback"/>）。</summary>
    ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default);

    /// <summary>按输入派生的签名计算当前自适应策略（供诊断 / 端点查询）。</summary>
    ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default);

    /// <summary>按显式计划签名计算当前自适应策略（供诊断 / 端点查询）。
    /// 工作区为隔离边界：签名已含工作区维度，此处显式传入用于存储层作用域校验（防伪造签名越权）。</summary>
    ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(string workspaceId, string planSignature, CancellationToken ct = default);

    /// <summary>列出指定工作区内、指定计划签名最近 N 条反馈（按记录时间倒序；供诊断 / 端点查询）。</summary>
    ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default);

    /// <summary>清除反馈并重置自适应状态。workspaceId 为 null 时清除全部工作区（全局重置，需更高权限）；
    /// planSignature 为 null 时清除该工作区全部。返回清除条数。</summary>
    ValueTask<int> ResetAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default);
}

/// <summary>
/// 计划签名派生助手：从规划输入确定性派生计划签名（SHA-256 十六进制）。
/// 规划器 / 运维端点 / 测试共用同一算法，保证反馈记录与策略查询落到同一签名。
/// </summary>
/// <remarks>
/// 加固：签名覆盖租户隔离维度（Workspace / Collection / Purpose / PolicyVersion /
/// RetrievalProfile / TaskClass）+ 任务形态维度（任务 / 意图 / 未解决目标），
/// 使用带标签字段以 <c>\u001F</c> 分隔拼接后做 SHA-256，杜绝字段边界混淆与跨
/// Workspace 污染；不同租户输入相同任务产生不同签名，绝不共享反馈状态。
/// 旧版 FNV-1a 64 位签名不再产生（历史持久化签名成为孤立数据，不参与新策略）。
/// </remarks>
public static class AdaptiveRetrievalPlanSignature
{
    /// <summary>标签字段分隔符（Unit Separator 控制字符，正文几乎不可能包含）。</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// 从输入派生计划签名：对各维度字段 trim 归一后按 <c>label=value</c> 拼接
    /// （<see cref="FieldSeparator"/> 分隔），对 UTF-8 字节做 SHA-256，
    /// 输出 <c>sig:{64 位小写十六进制}</c>。相同输入永远产生相同签名；
    /// 任一租户维度不同即产生不同签名。
    /// </summary>
    public static string Compute(AgentRetrievalPlannerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var task = (input.OriginalTask ?? string.Empty).Trim();
        var intent = (input.LatestAssistantIntent ?? string.Empty).Trim();
        var goals = string.Join("\n", (input.UnresolvedGoals ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)));

        var seed = string.Join(
            FieldSeparator,
            Label("task", task),
            Label("intent", intent),
            Label("goals", goals),
            Label("ws", input.WorkspaceId),
            Label("col", input.CollectionId),
            Label("purpose", input.Purpose),
            Label("policy", input.PolicyVersion),
            Label("profile", input.RetrievalProfile),
            Label("taskClass", input.TaskClass));

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return "sig:" + Convert.ToHexStringLower(hash);
    }

    private static string Label(string name, string? value)
        => name + "=" + (value ?? string.Empty).Trim();
}
