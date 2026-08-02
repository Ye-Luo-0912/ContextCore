using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ========================================================================================
// — Context Evolution Agent V1（离线控制面契约）
//
// 硬边界（来自 project memory）：
// - Agent 只负责离线控制面：Observe / Cluster failures / Diagnose / Form hypothesis /
//   Generate experiment / Run benchmark or eval / Compare baseline / Generate proposal
// - 明确禁止：自动改正式 Policy、自动提交生产配置、自动启用模型、绕过 shadow/canary
// - Agent 输出必须是版本化 OptimizationProposal，含证据、预期收益、风险、实验结果、回滚条件
//
// 本文件仅包含 Abstractions 层契约，不含任何实现逻辑。
// 实现层（Core/Runtime）将在后续阶段提供 DefaultContextEvolutionAgent 等。
// ========================================================================================

/// <summary>
/// OptimizationProposal 的状态生命周期。
/// Agent 只能将状态推进到 <see cref="Validated"/>/<see cref="ExperimentReady"/>，
/// 后续状态（Shadow/Canary/Promoted/RolledBack）由 R17 Guarded Optimization Pipeline 决定。
/// </summary>
public enum OptimizationProposalStatus
{
    /// <summary>草稿：Agent 刚生成，尚未验证证据完整性。</summary>
    Draft,

    /// <summary>已验证：证据/实验结果齐全，假设与基线对比已记录。</summary>
    Validated,

    /// <summary>实验就绪：可提交到 R17 pipeline 执行 shadow/canary。</summary>
    ExperimentReady,

    /// <summary>影子模式：在 R17 pipeline 中以 shadow 运行（不影响生产）。</summary>
    Shadow,

    /// <summary>范围受控 canary：在 R17 pipeline 中以小范围 canary 运行。</summary>
    ScopedCanary,

    /// <summary>已晋升：R17 pipeline 自动或人工晋升到默认路径。</summary>
    Promoted,

    /// <summary>已回滚：R17 pipeline 命中风险条件后自动回滚。</summary>
    RolledBack,

    /// <summary>已拒绝：Agent 自审或人工拒绝（证据不足/风险过高/假设被驳斥）。</summary>
    Rejected
}

/// <summary>
/// OptimizationProposal 版本号：用于追踪 proposal 的迭代演进。
/// 同一 proposalId 的版本号单调递增，每次 Agent 修订（补充证据、修正假设）都递增。
/// </summary>
public sealed record OptimizationProposalVersion(int Major, int Minor)
{
    /// <summary>初始版本（1.0），用于 Agent 首次生成的 proposal。</summary>
    public static OptimizationProposalVersion Initial => new(1, 0);

    /// <summary>递增 Minor 版本（修订证据或假设，不破坏兼容性）。</summary>
    public OptimizationProposalVersion BumpMinor() => new(Major, Minor + 1);

    /// <summary>递增 Major 版本（结构性变更，例如改变目标组件/实验设计）。</summary>
    public OptimizationProposalVersion BumpMajor() => new(Major + 1, 0);

    /// <inheritdoc />
    public override string ToString() => $"v{Major}.{Minor}";
}

/// <summary>
/// 实验证据：记录 Agent 在离线实验中采集的证据片段。
/// 证据必须可追溯（含源指标、采集时间、样本数），不允许无来源的断言。
/// </summary>
public sealed class ExperimentEvidence
{
    /// <summary>构造实验证据。</summary>
    /// <param name="source">证据源（如 "benchmark:PackageBuildCold"、"eval:golden-v3"）。</param>
    /// <param name="metricName">指标名（如 "duration_ms"、"accuracy"）。</param>
    /// <param name="baselineValue">基线值（当前生产路径的指标值）。</param>
    /// <param name="experimentValue">实验值（Agent 提议的优化路径的指标值）。</param>
    /// <param name="sampleCount">样本数（>=1）。</param>
    /// <param name="capturedAt">采集时间。</param>
    /// <param name="notes">附注（可空）。</param>
    public ExperimentEvidence(
        string source,
        string metricName,
        double baselineValue,
        double experimentValue,
        int sampleCount,
        DateTimeOffset capturedAt,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        if (sampleCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "样本数必须 >= 1");
        }
        Source = source;
        MetricName = metricName;
        BaselineValue = baselineValue;
        ExperimentValue = experimentValue;
        SampleCount = sampleCount;
        CapturedAt = capturedAt;
        Notes = notes;
    }

    /// <summary>证据源（benchmark / eval / telemetry / shadow-compare）。</summary>
    public string Source { get; }

    /// <summary>指标名。</summary>
    public string MetricName { get; }

    /// <summary>基线值（生产路径）。</summary>
    public double BaselineValue { get; }

    /// <summary>实验值（Agent 提议路径）。</summary>
    public double ExperimentValue { get; }

    /// <summary>样本数。</summary>
    public int SampleCount { get; }

    /// <summary>采集时间。</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>附注（可空）。</summary>
    public string? Notes { get; }

    /// <summary>计算 delta（experiment - baseline），正值表示实验值更高。</summary>
    public double Delta => ExperimentValue - BaselineValue;
}

/// <summary>
/// 预期收益：Agent 对 proposal 生效后预期产生的量化收益。
/// 必须含置信度（0~1）与生效前提条件；不得给出无条件的"必然收益"断言。
/// </summary>
public sealed class ExpectedGain
{
    /// <summary>构造预期收益。</summary>
    public ExpectedGain(
        string metricName,
        double estimatedDelta,
        double confidence,
        IReadOnlyList<string> preconditions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        if (confidence < 0 || confidence > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "置信度必须在 [0, 1] 区间");
        }
        ArgumentNullException.ThrowIfNull(preconditions);
        MetricName = metricName;
        EstimatedDelta = estimatedDelta;
        Confidence = confidence;
        Preconditions = preconditions;
    }

    /// <summary>指标名（与 <see cref="ExperimentEvidence.MetricName"/> 对齐）。</summary>
    public string MetricName { get; }

    /// <summary>预期 delta（与基线相比）。</summary>
    public double EstimatedDelta { get; }

    /// <summary>置信度 [0, 1]。</summary>
    public double Confidence { get; }

    /// <summary>生效前提条件（如 "TokenBudget >= 4000"、"ItemCount >= 50"）。</summary>
    public IReadOnlyList<string> Preconditions { get; }
}

/// <summary>
/// 风险评估：Agent 对 proposal 可能引入的风险进行结构化记录。
/// 风险等级 + 触发条件 + 缓解措施必须齐全，缺一项视为草稿不可晋升 Validated。
/// </summary>
public sealed class RiskAssessment
{
    /// <summary>构造风险评估。</summary>
    public RiskAssessment(
        string riskId,
        string description,
        RiskSeverity severity,
        IReadOnlyList<string> triggerConditions,
        IReadOnlyList<string> mitigations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(riskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(triggerConditions);
        ArgumentNullException.ThrowIfNull(mitigations);
        RiskId = riskId;
        Description = description;
        Severity = severity;
        TriggerConditions = triggerConditions;
        Mitigations = mitigations;
    }

    /// <summary>风险标识（用于追踪与引用）。</summary>
    public string RiskId { get; }

    /// <summary>风险描述。</summary>
    public string Description { get; }

    /// <summary>风险等级。</summary>
    public RiskSeverity Severity { get; }

    /// <summary>触发条件（命中任一即视为风险已发生）。</summary>
    public IReadOnlyList<string> TriggerConditions { get; }

    /// <summary>缓解措施（Agent 建议的预防性措施）。</summary>
    public IReadOnlyList<string> Mitigations { get; }
}

/// <summary>风险等级。</summary>
public enum RiskSeverity
{
    /// <summary>低：可观察但不影响主路径。</summary>
    Low,

    /// <summary>中：影响非关键指标，可通过配置调整。</summary>
    Medium,

    /// <summary>高：影响关键指标或用户体验，必须可回滚。</summary>
    High,

    /// <summary>严重：可能导致数据损坏或安全风险，禁止进入 canary。</summary>
    Critical
}

/// <summary>
/// 回滚条件：当实验路径在 shadow/canary 阶段命中以下任一条件时，
/// pipeline 自动回滚到基线路径。条件必须可被运行时观察器检测。
/// </summary>
public sealed class RollbackCondition
{
    /// <summary>构造回滚条件。</summary>
    public RollbackCondition(
        string metricName,
        ComparisonOperator op,
        double threshold,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        MetricName = metricName;
        Operator = op;
        Threshold = threshold;
        Description = description;
    }

    /// <summary>指标名（与 <see cref="ExperimentEvidence.MetricName"/> 对齐）。</summary>
    public string MetricName { get; }

    /// <summary>比较运算符。</summary>
    public ComparisonOperator Operator { get; }

    /// <summary>阈值。</summary>
    public double Threshold { get; }

    /// <summary>描述（人类可读）。</summary>
    public string Description { get; }

    /// <summary>判断给定指标值是否命中回滚条件。</summary>
    public bool IsTriggered(double value) => Operator switch
    {
        ComparisonOperator.GreaterThan => value > Threshold,
        ComparisonOperator.LessThan => value < Threshold,
        ComparisonOperator.GreaterThanOrEqual => value >= Threshold,
        ComparisonOperator.LessThanOrEqual => value <= Threshold,
        ComparisonOperator.Equals => Math.Abs(value - Threshold) < double.Epsilon,
        _ => false,
    };
}

/// <summary>比较运算符（用于 <see cref="RollbackCondition"/>）。</summary>
public enum ComparisonOperator
{
    /// <summary>value &gt; threshold</summary>
    GreaterThan,

    /// <summary>value &lt; threshold</summary>
    LessThan,

    /// <summary>value &gt;= threshold</summary>
    GreaterThanOrEqual,

    /// <summary>value &lt;= threshold</summary>
    LessThanOrEqual,

    /// <summary>value == threshold</summary>
    Equals
}

/// <summary>
/// 优化目标组件：标识 proposal 作用于哪个核心运行时组件。
/// 用于约束 Agent 的作用范围，避免越权修改非授权组件。
/// </summary>
public enum OptimizationTargetComponent
{
    /// <summary>Cost-aware Retrieval Router（成本感知检索路由）。</summary>
    CostAwareRetrievalRouter,

    /// <summary>Candidate Utility Reranker（候选效用重排序器）。</summary>
    CandidateUtilityReranker,

    /// <summary>Package Policy（打包策略）。</summary>
    PackagePolicy,

    /// <summary>Cache Policy（缓存策略）。</summary>
    CachePolicy,

    /// <summary>Tokenizer Selection（tokenizer 选择策略）。</summary>
    TokenizerSelection,

    /// <summary>Section Assembly（section 装配策略）。</summary>
    SectionAssembly
}

/// <summary>
/// 版本化 OptimizationProposal：Context Evolution Agent 的输出契约。
/// </summary>
/// <remarks>
/// 不可变 record；每次 Agent 修订生成新版本号。
/// Agent 不能直接修改生产 Policy 或运行时配置；它只能生成 proposal，由 R17 pipeline 决定是否晋升。
/// </remarks>
public sealed record OptimizationProposal
{
    /// <summary>proposal 唯一标识（Agent 生成，跨版本不变）。</summary>
    public required string ProposalId { get; init; }

    /// <summary>proposal 版本号（每次修订递增）。</summary>
    public required OptimizationProposalVersion Version { get; init; }

    /// <summary>标题（人类可读）。</summary>
    public required string Title { get; init; }

    /// <summary>假设描述（Agent 形成的优化假设）。</summary>
    public required string Hypothesis { get; init; }

    /// <summary>目标组件（约束 Agent 作用范围）。</summary>
    public required OptimizationTargetComponent TargetComponent { get; init; }

    /// <summary>状态（Agent 推进到 Validated/ExperimentReady，后续由 R17 pipeline 推进）。</summary>
    public OptimizationProposalStatus Status { get; init; } = OptimizationProposalStatus.Draft;

    /// <summary>证据列表（不可为空，Validated 状态至少 1 条）。</summary>
    public IReadOnlyList<ExperimentEvidence> Evidence { get; init; } = Array.Empty<ExperimentEvidence>();

    /// <summary>预期收益列表（不可为空，Validated 状态至少 1 条）。</summary>
    public IReadOnlyList<ExpectedGain> ExpectedGains { get; init; } = Array.Empty<ExpectedGain>();

    /// <summary>风险评估列表（不可为空，Validated 状态至少 1 条）。</summary>
    public IReadOnlyList<RiskAssessment> Risks { get; init; } = Array.Empty<RiskAssessment>();

    /// <summary>回滚条件列表（不可为空，ExperimentReady 状态至少 1 条）。</summary>
    public IReadOnlyList<RollbackCondition> RollbackConditions { get; init; } = Array.Empty<RollbackCondition>();

    /// <summary>实验配置 JSON（Agent 提供给 R17 pipeline 的实验参数）。</summary>
    public string? ExperimentConfigJson { get; init; }

    /// <summary>Agent 生成的回滚预案（人类可读描述）。</summary>
    public string? RollbackPlan { get; init; }

    /// <summary>生成时间。</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Agent 标识（用于审计）。</summary>
    public string? AgentIdentifier { get; init; }
}

/// <summary>
/// Agent 观察源：提供运行时指标采集能力，供 Agent 读取。
/// 实现可以是 telemetry sink、benchmark runner、eval host 等。
/// </summary>
public interface IAgentObservationSource
{
    /// <summary>观察源标识（如 "telemetry:package-build"、"benchmark:cold-path"）。</summary>
    string SourceId { get; }

    /// <summary>采集最近的指标快照（返回 metric_name → value 的字典）。</summary>
    Task<IReadOnlyDictionary<string, double>> ObserveAsync(
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent 诊断请求：触发 Agent 执行离线诊断流程。
/// Agent 内部按 Observe → Cluster failures → Diagnose → Form hypothesis →
/// Generate experiment → Run benchmark/eval → Compare baseline → Generate proposal 顺序执行。
/// </summary>
public sealed class AgentDiagnosticRequest
{
    /// <summary>构造诊断请求。</summary>
    public AgentDiagnosticRequest(
        string workspaceId,
        string? collectionId,
        OptimizationTargetComponent targetComponent,
        IReadOnlyDictionary<string, string>? hints = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        WorkspaceId = workspaceId;
        CollectionId = collectionId;
        TargetComponent = targetComponent;
        Hints = hints ?? new Dictionary<string, string>();
    }

    /// <summary>工作区 ID。</summary>
    public string WorkspaceId { get; }

    /// <summary>集合 ID（可为 null，表示全工作区诊断）。</summary>
    public string? CollectionId { get; }

    /// <summary>目标组件（约束 Agent 作用范围）。</summary>
    public OptimizationTargetComponent TargetComponent { get; }

    /// <summary>提示信息（Agent 可读取的额外上下文，例如"用户报告 cache miss 异常"）。</summary>
    public IReadOnlyDictionary<string, string> Hints { get; }
}

/// <summary>
/// Agent 诊断结果：含 Agent 生成的 proposal 与诊断摘要。
/// </summary>
public sealed class AgentDiagnosticResult
{
    /// <summary>构造诊断结果。</summary>
    public AgentDiagnosticResult(
        OptimizationProposal? proposal,
        string summary,
        IReadOnlyList<string> observations,
        IReadOnlyList<string> hypothesisTrail)
    {
        Summary = summary ?? string.Empty;
        Observations = observations ?? Array.Empty<string>();
        HypothesisTrail = hypothesisTrail ?? Array.Empty<string>();
        Proposal = proposal;
    }

    /// <summary>Agent 生成的 proposal（可为 null，表示本次诊断未形成可执行假设）。</summary>
    public OptimizationProposal? Proposal { get; }

    /// <summary>诊断摘要（人类可读）。</summary>
    public string Summary { get; }

    /// <summary>观察记录（Agent 在 Observe/Cluster 阶段记录的事实）。</summary>
    public IReadOnlyList<string> Observations { get; }

    /// <summary>假设轨迹（Agent 在 Diagnose/Form hypothesis 阶段的推理链）。</summary>
    public IReadOnlyList<string> HypothesisTrail { get; }
}

/// <summary>
/// Context Evolution Agent 主接口。
/// </summary>
/// <remarks>
/// <b>硬边界</b>：
/// <list type="bullet">
/// <item>Agent 只能调用 <see cref="DiagnoseAsync"/> 与 <see cref="RefineProposalAsync"/>。</item>
/// <item>Agent 不能直接调用任何修改生产 Policy 或运行时配置的接口。</item>
/// <item>Agent 不能调用 <see cref="IContextPackageBuilder.BuildAsync"/> 等正式构建路径</item>
/// <item>Agent 输出的 <see cref="OptimizationProposal"/> 必须通过 R17 pipeline 才能进入生产。</item>
/// </list>
/// 实现层（DefaultContextEvolutionAgent）将在后续阶段提供。
/// </remarks>
public interface IContextEvolutionAgent
{
    /// <summary>执行离线诊断，生成 OptimizationProposal（若假设成立）。</summary>
    /// <param name="request">诊断请求（含 workspace/target/hints）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>诊断结果（含 proposal 或空假设）。</returns>
    Task<AgentDiagnosticResult> DiagnoseAsync(
        AgentDiagnosticRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 基于新证据修订既有 proposal（递增版本号）。
    /// Agent 不能直接修改原 proposal；必须通过此方法生成新版本。
    /// </summary>
    /// <param name="existing">既有 proposal。</param>
    /// <param name="additionalEvidence">补充证据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新版本 proposal（版本号递增）。</returns>
    Task<OptimizationProposal> RefineProposalAsync(
        OptimizationProposal existing,
        IReadOnlyList<ExperimentEvidence> additionalEvidence,
        CancellationToken cancellationToken = default);
}

// ========================================================================================
// — Guarded Optimization Pipeline 契约
//
// 硬边界（来自 project memory）：
// - Pipeline 阶段严格按 Offline Experiment → Shadow → Scoped Canary →
//   Automatic Rollback → Manual/default Promotion 顺序推进。
// - 任何阶段命中 <see cref="RollbackCondition"/> 自动回滚到基线路径。
// - 第一项端到端学习闭环建议先用 <see cref="IPromotionJudge"/> 验证基础设施。
// - 第一项真正作用于核心运行时的 learned component 建议：
//   Cost-aware Retrieval Router 或 Candidate Utility Reranker（二选一）。
//
// 本文件仅含 Abstractions 层契约；实现层将在后续阶段提供。
// ========================================================================================

/// <summary>
/// Guarded Optimization Pipeline 阶段。
/// 阶段严格顺序推进，不允许跳跃（例如不能从 Shadow 直接跳到 Promotion）。
/// </summary>
public enum OptimizationStage
{
    /// <summary>离线实验：Agent 在 benchmark/eval 中验证假设。</summary>
    OfflineExperiment,

    /// <summary>影子模式：实验路径与生产路径并行运行，仅记录输出差异，不影响生产。</summary>
    Shadow,

    /// <summary>范围受控 canary：实验路径对小范围流量生效，生产路径作为兜底。</summary>
    ScopedCanary,

    /// <summary>自动回滚：canary 阶段命中 <see cref="RollbackCondition"/>，自动切回基线路径。</summary>
    AutomaticRollback,

    /// <summary>晋升：人工或默认规则将实验路径提升为新的基线路径。</summary>
    Promotion
}

/// <summary>
/// Canary 渐进推进决策类型。
/// </summary>
public enum CanaryProgressionDecision
{
    /// <summary>推进到下一档百分比。</summary>
    Advance,

    /// <summary>停留在当前档继续观察（观察时长不足或数据缺失）。</summary>
    Hold,

    /// <summary>触发自动回滚（指标超阈值）。</summary>
    Rollback,

    /// <summary>已晋升到 100%（V2 only），无可继续推进。</summary>
    Promoted
}

/// <summary>
/// Pipeline 运行状态。
/// </summary>
public enum PipelineRunStatus
{
    /// <summary>运行中：当前阶段正在执行。</summary>
    Running,

    /// <summary>阶段完成：可推进到下一阶段。</summary>
    StageCompleted,

    /// <summary>已回滚：命中回滚条件，自动切回基线路径。</summary>
    RolledBack,

    /// <summary>已晋升：实验路径已替代基线。</summary>
    Promoted,

    /// <summary>已拒绝：人工驳回或实验路径未达预期收益。</summary>
    Rejected,

    /// <summary>已取消：外部取消。</summary>
    Cancelled,

    /// <summary>失败：阶段执行异常（非回滚条件触发）。</summary>
    Failed
}

/// <summary>
/// Pipeline 运行结果：单次推进阶段的执行结果。
/// </summary>
public sealed class PipelineRunResult
{
    /// <summary>构造运行结果。</summary>
    public PipelineRunResult(
        string runId,
        string proposalId,
        OptimizationProposalVersion proposalVersion,
        OptimizationStage stage,
        PipelineRunStatus status,
        IReadOnlyDictionary<string, double>? stageMetrics = null,
        string? rollbackReason = null,
        DateTimeOffset? completedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        RunId = runId;
        ProposalId = proposalId;
        ProposalVersion = proposalVersion;
        Stage = stage;
        Status = status;
        StageMetrics = stageMetrics ?? new Dictionary<string, double>();
        RollbackReason = rollbackReason;
        CompletedAt = completedAt;
    }

    /// <summary>运行 ID（每次推进阶段生成）。</summary>
    public string RunId { get; }

    /// <summary>关联的 proposal ID。</summary>
    public string ProposalId { get; }

    /// <summary>关联的 proposal 版本。</summary>
    public OptimizationProposalVersion ProposalVersion { get; }

    /// <summary>本次推进的阶段。</summary>
    public OptimizationStage Stage { get; }

    /// <summary>本次推进的状态。</summary>
    public PipelineRunStatus Status { get; }

    /// <summary>阶段采集的指标（用于 <see cref="IPromotionJudge"/> 裁决）。</summary>
    public IReadOnlyDictionary<string, double> StageMetrics { get; }

    /// <summary>回滚原因（仅当 <see cref="Status"/> = RolledBack 时非空）。</summary>
    public string? RollbackReason { get; }

    /// <summary>完成时间（仅当 <see cref="Status"/> 不是 Running 时有值）。</summary>
    public DateTimeOffset? CompletedAt { get; }
}

/// <summary>
/// 晋升裁决请求：包含 proposal、阶段指标、基线指标，由 <see cref="IPromotionJudge"/> 裁决是否晋升。
/// </summary>
public sealed class PromotionJudgeRequest
{
    /// <summary>构造晋升裁决请求。</summary>
    public PromotionJudgeRequest(
        OptimizationProposal proposal,
        OptimizationStage currentStage,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        IReadOnlyDictionary<string, double>? stageMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(baselineMetrics);
        ArgumentNullException.ThrowIfNull(experimentMetrics);
        Proposal = proposal;
        CurrentStage = currentStage;
        BaselineMetrics = baselineMetrics;
        ExperimentMetrics = experimentMetrics;
        StageMetrics = stageMetrics ?? new Dictionary<string, double>();
    }

    /// <summary>被裁决的 proposal。</summary>
    public OptimizationProposal Proposal { get; }

    /// <summary>当前所在阶段。</summary>
    public OptimizationStage CurrentStage { get; }

    /// <summary>基线指标（生产路径）。</summary>
    public IReadOnlyDictionary<string, double> BaselineMetrics { get; }

    /// <summary>实验指标（proposal 路径）。</summary>
    public IReadOnlyDictionary<string, double> ExperimentMetrics { get; }

    /// <summary>当前阶段采集的指标（如 canary 期间的 P99 latency / error rate）。</summary>
    public IReadOnlyDictionary<string, double> StageMetrics { get; }
}

/// <summary>
/// 晋升裁决结果：由 <see cref="IPromotionJudge"/> 输出。
/// </summary>
public sealed class PromotionJudgeResult
{
    /// <summary>构造裁决结果。</summary>
    public PromotionJudgeResult(
        PromotionDecision decision,
        string rationale,
        OptimizationStage? nextStage = null,
        IReadOnlyList<string>? conditions = null)
    {
        Decision = decision;
        Rationale = rationale ?? string.Empty;
        NextStage = nextStage;
        Conditions = conditions ?? Array.Empty<string>();
    }

    /// <summary>裁决。</summary>
    public PromotionDecision Decision { get; }

    /// <summary>裁决理由（人类可读）。</summary>
    public string Rationale { get; }

    /// <summary>建议的下一阶段（仅当 <see cref="Decision"/> = Advance 时有值）。</summary>
    public OptimizationStage? NextStage { get; }

    /// <summary>附加条件（如"观察 24 小时后再晋升"）。</summary>
    public IReadOnlyList<string> Conditions { get; }
}

/// <summary>晋升裁决类型。</summary>
public enum PromotionDecision
{
    /// <summary>推进到下一阶段（如 OfflineExperiment → Shadow）。</summary>
    Advance,

    /// <summary>停留在当前阶段继续观察（如 canary 时间不足）。</summary>
    Hold,

    /// <summary>回滚（命中回滚条件或指标恶化）。</summary>
    Rollback,

    /// <summary>晋升为新的基线路径。</summary>
    Promote,

    /// <summary>拒绝（proposal 假设被驳斥，不再继续）。</summary>
    Reject
}

/// <summary>
/// PromotionJudge 接口：最小的端到端学习闭环裁决器。
/// </summary>
/// <remarks>
/// 建议作为第一项端到端学习闭环的实现目标，因为：
/// <list type="bullet">
/// <item>作用域最小（仅决定 proposal 是否推进/回滚/晋升）</item>
/// <item>风险容易隔离（裁决失败仅影响单个 proposal，不影响生产路径）</item>
/// <item>可独立测试（不依赖具体 learned component）</item>
/// </list>
/// 实现层（DefaultPromotionJudge）将在后续阶段提供。
/// </remarks>
public interface IPromotionJudge
{
    /// <summary>对当前阶段执行裁决。</summary>
    /// <param name="request">裁决请求（含 proposal、基线指标、实验指标）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>裁决结果（Advance/Hold/Rollback/Promote/Reject）。</returns>
    Task<PromotionJudgeResult> JudgeAsync(
        PromotionJudgeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Guarded Optimization Pipeline 主接口。
/// </summary>
/// <remarks>
/// Pipeline 严格按 OfflineExperiment → Shadow → ScopedCanary → Promotion 顺序推进，
/// 任何阶段命中 <see cref="RollbackCondition"/> 自动回滚到基线路径。
/// Pipeline 不允许跳跃推进（如从 Shadow 直接跳到 Promotion）。
/// 实现层（DefaultGuardedOptimizationPipeline）将在后续阶段提供。
/// </remarks>
public interface IGuardedOptimizationPipeline
{
    /// <summary>启动一次 pipeline 运行（从 OfflineExperiment 阶段开始）。</summary>
    /// <param name="proposal">Agent 输出的 proposal（状态必须为 ExperimentReady）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>初始运行结果（stage=OfflineExperiment, status=Running 或 StageCompleted）。</returns>
    Task<PipelineRunResult> StartAsync(
        OptimizationProposal proposal,
        CancellationToken cancellationToken = default);

    /// <summary>推进到下一阶段（由调用方触发，Pipeline 内部裁决是否允许推进）。</summary>
    /// <param name="runId">运行 ID（来自 <see cref="StartAsync"/> 返回值）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>推进结果（可能是 StageCompleted/RolledBack/Promoted/Rejected）。</returns>
    Task<PipelineRunResult> AdvanceAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>查询当前运行状态。</summary>
    /// <param name="runId">运行 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前运行结果（可能为 null，表示 runId 不存在）。</returns>
    Task<PipelineRunResult?> GetStatusAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
