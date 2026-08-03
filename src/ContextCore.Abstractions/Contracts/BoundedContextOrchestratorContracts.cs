using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// Bounded Context Orchestrator 契约
//
// 设计原则（对齐用户规格）：
// 1. 不使用无限循环（Build → Evaluate → Refine → Evaluate → ...）；
// 只允许一次有界修复：Plan → Decide → Build → Quality Evaluate →
// Optional Single Repair → Finalize。
// 2. 只在确定性异常出现时修复：
// - PrimaryAnchorUncovered（primary anchor 未覆盖）
// - HardConstraintMissing（hard constraint 缺失）
// - MustHitMissing（must-hit 缺失）
// - SevereRedundancy（严重冗余）
// - SectionSqueezeAnomaly（section 异常挤压）
// - TokenUtilizationTooLow（token 使用率异常低）
// - LifecycleConflictUnresolved（lifecycle conflict 未解决）
// 3. 修复预算必须显式（ContextRepairBudget 4 字段）。
// 4. 离线 ContextEvolutionAgent 继续负责 Observe → Diagnose →
// Form Hypothesis → Run Experiment → Produce Proposal，不自动修改正式 Policy。
//
// 与 PackageQualityReport 的映射：
// - AnchorCoverage.Score < 阈值 → PrimaryAnchorUncovered
// - HardConstraintSatisfaction.Score < 阈值 → HardConstraintMissing
// - RequiredItemCoverage.Score < 阈值 → MustHitMissing
// - Redundancy.Score < 阈值 → SevereRedundancy
// - SectionBalance.Score < 阈值 → SectionSqueezeAnomaly
// - TokenEfficiency.Score < 阈值 → TokenUtilizationTooLow
// - LifecycleRisk.Score < 阈值 → LifecycleConflictUnresolved
// ===========================================================================

/// <summary>
/// context repair 触发原因（7 类确定性异常）。
/// </summary>
public enum ContextRepairReason : byte
{
    /// <summary>未知原因（不应出现在正式修复请求中）。</summary>
    Unknown = 0,

    /// <summary>primary anchor 未覆盖（PackageQualityReport.AnchorCoverage.Score &lt; 阈值）。</summary>
    PrimaryAnchorUncovered = 1,

    /// <summary>hard constraint 缺失（HardConstraintSatisfaction.Score &lt; 阈值）。</summary>
    HardConstraintMissing = 2,

    /// <summary>must-hit 缺失（RequiredItemCoverage.Score &lt; 阈值）。</summary>
    MustHitMissing = 3,

    /// <summary>严重冗余（Redundancy.Score &lt; 阈值）。</summary>
    SevereRedundancy = 4,

    /// <summary>section 异常挤压（SectionBalance.Score &lt; 阈值）。</summary>
    SectionSqueezeAnomaly = 5,

    /// <summary>token 使用率异常低（TokenEfficiency.Score &lt; 阈值）。</summary>
    TokenUtilizationTooLow = 6,

    /// <summary>lifecycle conflict 未解决（LifecycleRisk.Score &lt; 阈值）。</summary>
    LifecycleConflictUnresolved = 7
}

/// <summary>
/// context repair 预算。修复操作的硬性限制，防止修复循环失控。
/// </summary>
/// <remarks>
/// 对齐用户规格：
/// public sealed record ContextRepairBudget
/// {
/// public int MaxAdditionalStoreCalls { get; init; }
/// public int MaxAdditionalCandidates { get; init; }
/// public int MaxAdditionalTokens { get; init; }
/// public TimeSpan MaxAdditionalLatency { get; init; }
/// }
///
/// 设计原则：
/// 1. 预算不可扩展：修复循环内不能再次调整预算，避免"再修复一次"递归。
/// 2. 预算必须显式：调用方在发起修复请求时必须指定预算，不提供默认值（强制显式）。
/// 3. 预算耗尽时立即终止修复，记录部分修复结果。
/// </remarks>
public sealed record ContextRepairBudget
{
    /// <summary>修复允许的额外存储调用次数（如 retrieval 补充查询、constraint 重读）。</summary>
    public int MaxAdditionalStoreCalls { get; init; }

    /// <summary>修复允许新增的候选数量上限。</summary>
    public int MaxAdditionalCandidates { get; init; }

    /// <summary>修复允许新增的 token 数量上限。</summary>
    public int MaxAdditionalTokens { get; init; }

    /// <summary>修复允许的额外延迟（wall-clock 时间）。</summary>
    public TimeSpan MaxAdditionalLatency { get; init; }
}

/// <summary>
/// context repair 触发的诊断结果。由 RepairDetector 输出，作为修复请求的输入。
/// </summary>
public sealed record ContextRepairDiagnosis
{
    /// <summary>诊断的唯一 ID（如 "diag-{guid}"）。</summary>
    public required string DiagnosisId { get; init; }

    /// <summary>关联的 DecisionResult RequestId。</summary>
    public required string DecisionRequestId { get; init; }

    /// <summary>workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域。</summary>
    public required string CollectionId { get; init; }

    /// <summary>触发的修复原因（必填；Unknown 不应出现在正式诊断中）。</summary>
    public required ContextRepairReason Reason { get; init; }

    /// <summary>触发原因详情（人类可读；如 "AnchorCoverage=0.4 < threshold 0.8, 2/5 anchors uncovered"）。</summary>
    public required string ReasonDetail { get; init; }

    /// <summary>触发的指标值（如 0.4）。</summary>
    public double TriggerMetricValue { get; init; }

    /// <summary>触发的指标阈值（如 0.8）。</summary>
    public double TriggerMetricThreshold { get; init; }

    /// <summary>关联的 PackageQualityReport（用于修复策略决策）。</summary>
    public PackageQualityReport? QualityReport { get; init; }

    /// <summary>建议的修复策略提示（如 "re-retrieve-must-hit" / "drop-redundant" /
    /// "rebalance-sections" / "inject-missing-hard-constraint"）。</summary>
    public string? SuggestedRepairStrategy { get; init; }

    /// <summary>诊断时间戳（UTC）。</summary>
    public required DateTimeOffset DiagnosedAt { get; init; }

    /// <summary>诊断元数据（用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// context repair 请求。修复循环触发后，由 orchestrator 发起的修复操作请求。
/// </summary>
public sealed record ContextRepairRequest
{
    /// <summary>修复请求的唯一 ID（如 "repair-{guid}"）。</summary>
    public required string RepairRequestId { get; init; }

    /// <summary>关联的诊断结果（必填）。</summary>
    public required ContextRepairDiagnosis Diagnosis { get; init; }

    /// <summary>修复预算（必填；显式指定，不可扩展）。</summary>
    public required ContextRepairBudget Budget { get; init; }

    /// <summary>原始 DecisionResult（作为修复起点）。</summary>
    public required ContextDecisionResult OriginalDecision { get; init; }

    /// <summary>原始 PackageQualityReport（若与 Diagnosis.QualityReport 相同则不重复）。</summary>
    public PackageQualityReport? OriginalQualityReport { get; init; }

    /// <summary>触发者（user / system / agent ID；可空 = 自动触发）。</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>修复请求时间戳（UTC）。</summary>
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// context repair 响应。修复循环的最终输出。
/// </summary>
public sealed record ContextRepairResponse
{
    /// <summary>修复响应的唯一 ID（与 RepairRequestId 对应）。</summary>
    public required string RepairRequestId { get; init; }

    /// <summary>修复是否成功（预算耗尽前完成）。</summary>
    public bool IsSuccess { get; init; }

    /// <summary>是否执行了修复（false = 无需修复或预算为 0）。</summary>
    public bool WasRepaired { get; init; }

    /// <summary>修复后的 DecisionResult（若 WasRepaired=false 则与 OriginalDecision 相同）。</summary>
    public required ContextDecisionResult RepairedDecision { get; init; }

    /// <summary>修复后的 PackageQualityReport（若 WasRepaired=false 则为 null）。</summary>
    public PackageQualityReport? RepairedQualityReport { get; init; }

    /// <summary>实际消耗的预算。</summary>
    public required ContextRepairBudget ConsumedBudget { get; init; }

    /// <summary>修复操作摘要（如 "re-retrieved 3 candidates, added 2 must-hit, dropped 1 redundant"）。</summary>
    public string RepairSummary { get; init; } = string.Empty;

    /// <summary>修复期间产生的错误列表（空 = 无错误）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>修复完成时间戳（UTC）。</summary>
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// context repair detector 接口。检查 DecisionResult + QualityReport 是否触发修复。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. Detector 是纯函数式评估：输入 DecisionResult + QualityReport，输出 ContextRepairDiagnosis 列表。
/// 2. Detector 不执行修复，只检测是否需要修复。
/// 3. 多个异常同时触发时，返回多个 Diagnosis（orchestrator 决定修复顺序）。
/// 4. 阈值参数通过实现类构造函数配置，不暴露在接口中。
/// </remarks>
public interface IContextRepairDetector
{
    /// <summary>检测 DecisionResult + QualityReport 是否触发修复。</summary>
    /// <param name="decision">原始决策结果。</param>
    /// <param name="qualityReport">原始质量报告（可空 = 无质量报告，返回空列表）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>触发的诊断列表（空 = 无需修复）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextRepairDiagnosis>> DetectAsync(
        ContextDecisionResult decision,
        PackageQualityReport? qualityReport,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// context repair executor 接口。执行单个修复请求，输出修复后的 DecisionResult。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. Executor 执行修复策略（re-retrieve / drop-redundant / rebalance / inject 等）。
/// 2. Executor 必须遵守 Budget 限制；超预算时立即终止并返回部分修复结果。
/// 3. Executor 是幂等的：相同 RepairRequest 应产生相同 RepairResponse。
/// 4. Executor 失败时（如 store 不可用）返回 IsSuccess=false + Errors，不抛异常。
/// </remarks>
public interface IContextRepairExecutor
{
    /// <summary>执行单个修复请求。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<ContextRepairResponse> ExecuteAsync(
        ContextRepairRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded Context Orchestrator 接口。编排单次有界修复循环。
/// </summary>
/// <remarks>
/// 设计原则（对齐用户规格）：
/// 1. 不允许无限循环：Plan → Decide → Build → Quality Evaluate →
/// Optional Single Repair → Finalize（最多一次修复）。
/// 2. 修复预算必须显式（由调用方传入 ContextRepairBudget）。
/// 3. 离线 ContextEvolutionAgent 继续负责 Observe → Diagnose → Hypothesis →
/// Experiment → Proposal，不自动修改正式 Policy。
/// 4. Orchestrator 是幂等的：相同输入应产生相同输出（确定性 tie-break）。
///
/// 编排流程：
/// 1. Plan：调用方传入 DecisionRequest + Budget + QualityReport。
/// 2. Decide：调用 IContextDecisionEngine.DecideAsync 得到 DecisionResult。
/// 3. Build：（若已有 DecisionResult 则跳过；由调用方传入）
/// 4. Quality Evaluate：调用 IContextRepairDetector.DetectAsync 检测异常。
/// 5. Optional Single Repair：若检测到异常且预算允许，调用
/// IContextRepairExecutor.ExecuteAsync 执行一次修复。
/// 6. Finalize：返回最终 DecisionResult + RepairResponse（可能为 null）。
/// </remarks>
public interface IBoundedContextOrchestrator
{
    /// <summary>
    /// 执行单次有界修复循环。
    /// </summary>
    /// <param name="decision">已构建的 DecisionResult（Build 阶段产物）。</param>
    /// <param name="qualityReport">已计算的 QualityReport（Quality Evaluate 阶段产物）。</param>
    /// <param name="budget">修复预算（显式指定，不可扩展）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>编排结果（最终 DecisionResult + 修复响应，可能为 null = 无需修复）。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<BoundedContextOrchestrationResult> OrchestrateAsync(
        ContextDecisionResult decision,
        PackageQualityReport qualityReport,
        ContextRepairBudget budget,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded Context Orchestrator 编排结果。
/// </summary>
public sealed record BoundedContextOrchestrationResult
{
    /// <summary>编排唯一 ID（如 "orch-{guid}"）。</summary>
    public required string OrchestrationId { get; init; }

    /// <summary>最终 DecisionResult（修复后或原始）。</summary>
    public required ContextDecisionResult FinalDecision { get; init; }

    /// <summary>最终 QualityReport（修复后或原始）。</summary>
    public required PackageQualityReport FinalQualityReport { get; init; }

    /// <summary>检测到的诊断列表（Quality Evaluate 阶段产物）。</summary>
    public required IReadOnlyList<ContextRepairDiagnosis> Diagnoses { get; init; }

    /// <summary>修复响应（若执行了修复；null = 无需修复或预算为 0）。</summary>
    public ContextRepairResponse? RepairResponse { get; init; }

    /// <summary>是否执行了修复。</summary>
    public bool WasRepaired => RepairResponse is not null && RepairResponse.WasRepaired;

    /// <summary>编排是否成功（无错误且预算未耗尽）。</summary>
    public bool IsSuccess => RepairResponse is null || RepairResponse.IsSuccess;

    /// <summary>编排开始时间（UTC）。</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>编排完成时间（UTC）。</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>编排耗时（CompletedAt - StartedAt）。</summary>
    public TimeSpan Duration => CompletedAt - StartedAt;
}
