using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// Canary 紧急回滚本地状态建模
//
// 背景：
//   RollbackAsync 在 DB CAS 失败时仍无条件将本地流量切到 0%（安全优先），
//   但旧实现没有把"本地已回滚 / DB 未持久化"这一不一致状态建模出来，
//   后续 AdvanceAsync 会把本地与 DB 状态当作一致继续推进，可能掩盖 DB 真实状态。
//
// 本枚举显式建模本地 vs DB 的一致性状态，让 Progression 流水线在 DB 持久化
// 失败后能拒绝推进、要求 Operator 介入或重试持久化。
//
// 设计为 [Flags]：一次紧急回滚可能同时满足多个语义条件（已紧急回滚 + 等待持久化 +
// 需告警），用位标志组合可让查询方独立判断每个条件。
// ===========================================================================

/// <summary>
/// Canary 本地状态（相对 DB 真相源的一致性标记）。
/// </summary>
/// <remarks>
/// <para>
/// <b>语义</b>：<see cref="Consistent"/> 表示进程内 <c>_runStates</c> +
/// <see cref="CutoverController"/> 与 <c>canary_pipelines</c> 表的 DB 真相一致；
/// 其他位标记表示存在不一致，<see cref="CanaryProgressionService.AdvanceAsync"/> 应拒绝推进。
/// </para>
/// <para>
/// <b>典型组合</b>：DB CAS 失败的紧急回滚会同时设置
/// <see cref="LocalEmergencyRollback"/> | <see cref="PersistPending"/> | <see cref="OperatorAlertRequired"/>。
/// </para>
/// </remarks>
[Flags]
public enum CanaryLocalState : byte
{
    /// <summary>本地与 DB 一致（无任何未持久化变更）。</summary>
    Consistent = 0,

    /// <summary>本地已紧急回滚到 0%，但 DB CAS 失败（DB 仍记录旧百分比）。</summary>
    LocalEmergencyRollback = 1,

    /// <summary>等待 DB 持久化（本地变更尚未写入 <c>canary_pipelines</c>）。</summary>
    PersistPending = 2,

    /// <summary>进度推进被阻止（需 Operator 干预或重试 DB 持久化后才能恢复）。</summary>
    ProgressionBlocked = 4,

    /// <summary>需要 Operator 告警（应触发外部告警通道）。</summary>
    OperatorAlertRequired = 8
}

// ===========================================================================
// Production Canary Gate — 渐进推进服务
//
// 目标（对齐 R28-B.8 规格）：
//   1. 在 ScopedCanary 阶段内部按 CanaryGateOptions.PercentageLadder 渐进推进 CutoverController。
//   2. 每次推进前评估 metrics（parity/error/latency）：超阈值自动回滚；未达最小观察时长 Hold。
//   3. 100% 时不再执行 Legacy（CutoverController.CutoverPercentage=100 跳过 Legacy 路径）。
//   4. 端到端幂等：相同 TransitionId 重复调用不产生重复推进（依赖 store 的 LastTransitionId）。
//   5. 审计：每次推进记录到 stage_transitions 审计表（in-memory 实现；生产环境由
//      PostgresPipelineRunStore 同事务写入持久化审计表）。
//
// 设计边界：
//   - 本服务不替代 IPromotionJudge；仅在 ScopedCanary 阶段内部做渐进百分比推进。
//   - 终态（RolledBack/Promoted）：调用方应先检查 run status，已终态的 run 推进为 no-op。
//   - 与 CutoverController 的关系：本服务持有 CutoverController 引用，通过 SetCutoverPercentage
//     调整 V2 流量比例。多个 run 共享同一 CutoverController 时，最新推进的 run 决定全局百分比
//     （生产环境应为每个 run 隔离 CutoverController 实例，或使用 workspace 级别路由）。
// ===========================================================================

/// <summary>
/// Canary 渐进推进评估结果。
/// </summary>
public sealed record CanaryProgressionEvaluation
{
    /// <summary>决策类型。</summary>
    public required CanaryProgressionDecision Decision { get; init; }

    /// <summary>决策理由（人类可读）。</summary>
    public required string Rationale { get; init; }

    /// <summary>当前百分比档（0-100）。</summary>
    public required int CurrentPercentage { get; init; }

    /// <summary>下一档百分比（仅当 Decision=Advance 时有值）。</summary>
    public int? NextPercentage { get; init; }

    /// <summary>触发的回滚原因（仅当 Decision=Rollback 时有值）。</summary>
    public RollbackReason? RollbackReason { get; init; }

    /// <summary>触发的回滚阈值指标名（仅当 Decision=Rollback 时有值）。</summary>
    public string? RollbackMetricName { get; init; }

    /// <summary>触发的回滚阈值指标值（仅当 Decision=Rollback 时有值）。</summary>
    public double? RollbackMetricValue { get; init; }
}

/// <summary>
/// Canary 渐进推进执行结果。
/// </summary>
public sealed record CanaryProgressionResult
{
    /// <summary>决策类型。</summary>
    public required CanaryProgressionDecision Decision { get; init; }

    /// <summary>决策理由（人类可读）。</summary>
    public required string Rationale { get; init; }

    /// <summary>推进前百分比档。</summary>
    public required int PreviousPercentage { get; init; }

    /// <summary>推进后百分比档（推进成功时 = NextPercentage；否则 = PreviousPercentage）。</summary>
    public required int CurrentPercentage { get; init; }

    /// <summary>是否实际推进了百分比（true = 已调用 CutoverController.SetCutoverPercentage）。</summary>
    public bool Applied { get; init; }

    /// <summary>本次推进使用的 transitionId（用于幂等去重与审计）。</summary>
    public required string TransitionId { get; init; }

    /// <summary>本次推进使用的 idempotencyKey（用于 stage_transitions 审计表去重）。</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Canary 渐进推进服务。基于 metrics 自动推进或回滚。
/// </summary>
/// <remarks>
/// <b>使用模式</b>：
/// <code>
/// var service = new CanaryProgressionService(pipelineRunStore, cutoverController, options);
/// // 进入 ScopedCanary 时初始化为 PercentageLadder[0]
/// service.InitializeCanary(runId);
/// // 每次 observation batch 后评估 + 推进
/// var eval = await service.EvaluateAsync(runId, baselineMetrics, experimentMetrics, ct);
/// if (eval.Decision == CanaryProgressionDecision.Advance)
/// {
///     await service.AdvanceAsync(runId, transitionId, idempotencyKey, baselineMetrics, experimentMetrics, ct);
/// }
/// else if (eval.Decision == CanaryProgressionDecision.Rollback)
/// {
///     await service.RollbackAsync(runId, eval.RollbackReason!.Value, ct);
/// }
/// </code>
/// </remarks>
public sealed class CanaryProgressionService
{
    private readonly IPipelineRunStore _pipelineRunStore;
    private readonly CutoverController _cutoverController;
    private readonly CanaryGateOptions _options;
    private readonly TimeProvider _timeProvider;
    // 可选的 per-run CutoverController 注册表。
    // 非空时 InitializeCanary/AdvanceAsync/RollbackAsync 操作 registry.GetOrCreate(runId) 专用控制器；
    // 为 null 时回退到直接注入的 _cutoverController（B-8 之前行为，保持向后兼容）。
    private readonly CutoverControllerRegistry? _registry;
    // 可选的 DB 决策应用器，用于启动时从 canary_pipelines 表恢复 in-memory 状态。
    // 为 null 时（如单元测试使用 InMemoryPipelineRunStore）RecoverFromStoreAsync 为 no-op。
    private readonly ICanaryDecisionApplier? _decisionApplier;
    // per-run 本地状态标记（DB 一致性），用于在 DB CAS 失败时拒绝后续推进。
    // Consistent = 与 DB 一致；非 Consistent = 本地有未持久化变更，AdvanceAsync 应拒绝推进。
    private readonly ConcurrentDictionary<string, CanaryLocalState> _localStates
        = new(StringComparer.Ordinal);
    // 可选的日志器，用于在紧急回滚（DB CAS 失败）时记录告警。
    private readonly ILogger<CanaryProgressionService> _logger;

    // 按 runId 维度记录当前百分比档 + 进入当前档的时间戳
    private readonly ConcurrentDictionary<string, CanaryRunState> _runStates
        = new(StringComparer.Ordinal);

    // stage_transitions 审计表（in-memory 投影；生产环境由 PostgresPipelineRunStore 同事务持久化）
    private readonly ConcurrentDictionary<string, StageTransitionRecord> _stageTransitions
        = new(StringComparer.Ordinal);

    /// <summary>构造 Canary 渐进推进服务。</summary>
    /// <param name="pipelineRunStore">Pipeline run store（必填）。</param>
    /// <param name="cutoverController">Cutover 控制器（必填，由调用方注入并共享给 AuthoritativeRuntime）。</param>
    /// <param name="options">Canary Gate 配置（可选，默认 <see cref="CanaryGateOptions"/>）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    /// <param name="registry">
    /// 可选的 per-run CutoverController 注册表。非空时按 runId 隔离控制器，
    /// 避免多 run 共享 Singleton 导致百分比互相覆盖；为 null 时回退到 <paramref name="cutoverController"/>。
    /// </param>
    /// <param name="decisionApplier">
    /// 可选的 DB 决策应用器。非空时 <see cref="RecoverFromStoreAsync"/> 从
    /// <c>canary_pipelines</c> 表读取活跃 pipeline 状态并恢复 in-memory 百分比
    /// （CutoverController + <c>_runStates</c>）。为 null 时恢复为 no-op（单节点/测试场景）。
    /// </param>
    /// <param name="logger">
    /// 可选的日志器。非空时在紧急回滚（DB CAS 失败）记录告警；为 null 时使用 NullLogger。
    /// </param>
    public CanaryProgressionService(
        IPipelineRunStore pipelineRunStore,
        CutoverController cutoverController,
        CanaryGateOptions? options = null,
        TimeProvider? timeProvider = null,
        CutoverControllerRegistry? registry = null,
        ICanaryDecisionApplier? decisionApplier = null,
        ILogger<CanaryProgressionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pipelineRunStore);
        ArgumentNullException.ThrowIfNull(cutoverController);
        _pipelineRunStore = pipelineRunStore;
        _cutoverController = cutoverController;
        _options = options ?? new CanaryGateOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registry = registry;
        _decisionApplier = decisionApplier;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CanaryProgressionService>.Instance;
    }

    /// <summary>
    /// 解析指定 run 应操作的 CutoverController。
    /// registry 非空时返回该 run 的专用控制器（按需创建）；否则回退到共享的 <see cref="_cutoverController"/>。
    /// </summary>
    private CutoverController GetController(string runId)
    {
        return _registry is null ? _cutoverController : _registry.GetOrCreate(runId);
    }

    /// <summary>当前使用的 Canary Gate 配置（只读视图）。</summary>
    public CanaryGateOptions Options => _options;

    /// <summary>
    /// 初始化 run 的 canary 状态：设置 CutoverController 为 PercentageLadder[0]。
    /// </summary>
    /// <remarks>
    /// 调用时机：pipeline 进入 ScopedCanary 阶段时调用一次。
    /// 重复调用同一 runId 时：若已初始化则为 no-op（幂等）。
    /// </remarks>
    public void InitializeCanary(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_options.PercentageLadder.Count == 0)
        {
            throw new InvalidOperationException("CanaryGateOptions.PercentageLadder 不能为空");
        }

        var initialPercentage = _options.PercentageLadder[0];
        var now = _timeProvider.GetUtcNow();
        var state = new CanaryRunState(initialPercentage, now);
        if (_runStates.TryAdd(runId, state))
        {
            // 仅在首次初始化时调整 CutoverController
            // registry 非空时操作 per-run 专用控制器
            GetController(runId).SetCutoverPercentage(initialPercentage);
        }
        // 已存在 → no-op（幂等）
    }

    /// <summary>
    /// 评估当前阶段是否可推进。
    /// </summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="baselineMetrics">基线指标（Legacy 路径）。</param>
    /// <param name="experimentMetrics">实验指标（V2 路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>评估结果（Advance/Hold/Rollback/Promoted）。</returns>
    public async ValueTask<CanaryProgressionEvaluation> EvaluateAsync(
        string runId,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(baselineMetrics);
        ArgumentNullException.ThrowIfNull(experimentMetrics);
        cancellationToken.ThrowIfCancellationRequested();

        // 检查 run 是否已终态
        var snapshot = await _pipelineRunStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Hold,
                Rationale = $"Run not found: {runId}",
                CurrentPercentage = GetCurrentPercentage(runId)
            };
        }

        if (IsTerminal(snapshot.Status))
        {
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Hold,
                Rationale = $"Run 已终态：status={snapshot.Status}；不再推进。",
                CurrentPercentage = GetCurrentPercentage(runId)
            };
        }

        var currentPercentage = GetCurrentPercentage(runId);

        // 已晋升到 100%：返回 Promoted
        if (currentPercentage >= 100)
        {
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Promoted,
                Rationale = "Canary 已晋升到 100%（V2 only）；不再执行 Legacy。",
                CurrentPercentage = 100
            };
        }

        // 1. 回滚阈值检查（最高优先级）
        var rollbackCheck = CheckRollbackThresholds(baselineMetrics, experimentMetrics);
        if (rollbackCheck is not null)
        {
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Rollback,
                Rationale = rollbackCheck.Value.rationale,
                CurrentPercentage = currentPercentage,
                RollbackReason = rollbackCheck.Value.reason,
                RollbackMetricName = rollbackCheck.Value.metricName,
                RollbackMetricValue = rollbackCheck.Value.metricValue
            };
        }

        // 2. 最小观察时长检查
        if (!_runStates.TryGetValue(runId, out var state))
        {
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Hold,
                Rationale = "Canary 状态未初始化；请先调用 InitializeCanary。",
                CurrentPercentage = currentPercentage
            };
        }

        var now = _timeProvider.GetUtcNow();
        var elapsed = now - state.EnteredAt;
        if (elapsed < _options.MinObservationPeriod)
        {
            var remaining = _options.MinObservationPeriod - elapsed;
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Hold,
                Rationale = $"最小观察时长未达：已观察 {elapsed.TotalMinutes:F2} 分钟，" +
                            $"需 {_options.MinObservationPeriod.TotalMinutes:F2} 分钟（剩 {remaining.TotalMinutes:F2} 分钟）。",
                CurrentPercentage = currentPercentage
            };
        }

        // 3. 计算下一档百分比
        var nextPercentage = GetNextPercentage(currentPercentage);
        if (nextPercentage is null)
        {
            // 已是末档但仍未达 100%（理论不应发生，因 InitializeCanary 强制末位=100）
            return new CanaryProgressionEvaluation
            {
                Decision = CanaryProgressionDecision.Promoted,
                Rationale = "已到阶梯末档；不再推进。",
                CurrentPercentage = currentPercentage
            };
        }

        return new CanaryProgressionEvaluation
        {
            Decision = CanaryProgressionDecision.Advance,
            Rationale = $"观察时长已达标（{elapsed.TotalMinutes:F2} 分钟），" +
                        $"指标未超阈值；可推进 {currentPercentage}% → {nextPercentage}%。",
            CurrentPercentage = currentPercentage,
            NextPercentage = nextPercentage
        };
    }

    /// <summary>
    /// 推进到下一阶段（百分比档）。
    /// </summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="transitionId">调用方生成的 transition ID（用于幂等去重）。</param>
    /// <param name="idempotencyKey">可选的幂等键（用于 stage_transitions 审计表去重）。</param>
    /// <param name="baselineMetrics">基线指标（Legacy 路径）。</param>
    /// <param name="experimentMetrics">实验指标（V2 路径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>推进结果。</returns>
    /// <remarks>
    /// <b>幂等语义</b>：若 <paramref name="transitionId"/> 已被应用（即 stage_transitions 中已存在该 ID），
    /// 直接返回当前状态（不重复推进）。这保证调用方在响应丢失后重试时不会产生重复推进。
    /// <para>
    /// <b>推进流程</b>：
    /// <list type="number">
    /// <item>检查 transitionId 是否已应用 → 幂等返回。</item>
    /// <item>调用 <see cref="EvaluateAsync"/> 评估当前是否可推进。</item>
    /// <item>若 Decision=Advance：调用 CutoverController.SetCutoverPercentage + 更新 run state + 记录审计。</item>
    /// <item>若 Decision=Rollback：触发 <see cref="RollbackAsync"/>。</item>
    /// <item>若 Decision=Hold/Promoted：返回当前状态（不推进）。</item>
    /// </list>
    /// </para>
    /// </remarks>
    public async ValueTask<CanaryProgressionResult> AdvanceAsync(
        string runId,
        string transitionId,
        string? idempotencyKey,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionId);
        ArgumentNullException.ThrowIfNull(baselineMetrics);
        ArgumentNullException.ThrowIfNull(experimentMetrics);
        cancellationToken.ThrowIfCancellationRequested();

        var previousPercentage = GetCurrentPercentage(runId);

        // 幂等检查：相同 transitionId 已应用 → 返回当前状态
        if (_stageTransitions.ContainsKey(transitionId))
        {
            return new CanaryProgressionResult
            {
                Decision = CanaryProgressionDecision.Hold,
                Rationale = $"TransitionId={transitionId} 已应用；幂等返回当前状态。",
                PreviousPercentage = previousPercentage,
                CurrentPercentage = previousPercentage,
                Applied = false,
                TransitionId = transitionId,
                IdempotencyKey = idempotencyKey
            };
        }

        // 本地状态一致性检查。如果本地存在未持久化变更（如紧急回滚后 DB CAS 失败），
        // 拒绝推进并返回错误。调用方需先解决 PersistPending 状态（重试 RollbackAsync 持久化
        // 或 Operator 介入修复 DB 状态），让 _localStates[runId] 回到 Consistent 后才能推进。
        var localState = GetLocalState(runId);
        if (localState != CanaryLocalState.Consistent)
        {
            return new CanaryProgressionResult
            {
                Decision = CanaryProgressionDecision.Hold,
                Rationale = $"本地状态非 Consistent（{localState}）；拒绝推进。需先解决 PersistPending（重试 RollbackAsync 持久化或 Operator 介入）。",
                PreviousPercentage = previousPercentage,
                CurrentPercentage = previousPercentage,
                Applied = false,
                TransitionId = transitionId,
                IdempotencyKey = idempotencyKey
            };
        }

        var evaluation = await EvaluateAsync(runId, baselineMetrics, experimentMetrics, cancellationToken).ConfigureAwait(false);

        switch (evaluation.Decision)
        {
            case CanaryProgressionDecision.Advance:
                {
                    var nextPercentage = evaluation.NextPercentage!.Value;

                    // 当 _decisionApplier 非空时走 DB 单事务路径（ApplyCanaryDecisionLocalAsync），
                    // 统一 DB 真相源（canary_pipelines 表）。旧路径仅写进程内状态，重启后丢失。
                    if (_decisionApplier is not null)
                    {
                        var dbResult = await ApplyDecisionToStoreAsync(
                            runId, CanaryDecision.Promote, nextPercentage, previousPercentage,
                            transitionId, evaluation.Rationale, cancellationToken).ConfigureAwait(false);

                        if (dbResult.Applied)
                        {
                            UpdateInMemoryPercentage(runId, nextPercentage);
                            // DB CAS 成功 → 本地与 DB 一致，清除任何遗留的本地状态标记。
                            _localStates[runId] = CanaryLocalState.Consistent;
                        }

                        return new CanaryProgressionResult
                        {
                            Decision = dbResult.Applied ? CanaryProgressionDecision.Advance : CanaryProgressionDecision.Hold,
                            Rationale = dbResult.Applied ? evaluation.Rationale : $"DB CAS 失败（{dbResult.FailureReason}）；保持当前百分比。",
                            PreviousPercentage = dbResult.PreviousPercentage,
                            CurrentPercentage = dbResult.Applied ? nextPercentage : dbResult.PreviousPercentage,
                            Applied = dbResult.Applied,
                            TransitionId = transitionId,
                            IdempotencyKey = idempotencyKey
                        };
                    }

                    // 回退路径（_decisionApplier=null，测试场景）：仅写进程内状态
                    GetController(runId).SetCutoverPercentage(nextPercentage);
                    var now = _timeProvider.GetUtcNow();
                    _runStates[runId] = new CanaryRunState(nextPercentage, now);

                    await RecordTransitionAsync(new StageTransitionRecord
                    {
                        TransitionId = transitionId,
                        RunId = runId,
                        FromPercentage = previousPercentage,
                        ToPercentage = nextPercentage,
                        TransitionedAt = now,
                        IdempotencyKey = idempotencyKey,
                        Decision = CanaryProgressionDecision.Advance,
                        Rationale = evaluation.Rationale
                    }, cancellationToken).ConfigureAwait(false);

                    return new CanaryProgressionResult
                    {
                        Decision = CanaryProgressionDecision.Advance,
                        Rationale = evaluation.Rationale,
                        PreviousPercentage = previousPercentage,
                        CurrentPercentage = nextPercentage,
                        Applied = true,
                        TransitionId = transitionId,
                        IdempotencyKey = idempotencyKey
                    };
                }

            case CanaryProgressionDecision.Rollback:
                {
                    await RollbackAsync(runId, evaluation.RollbackReason!.Value, cancellationToken, transitionId, idempotencyKey).ConfigureAwait(false);
                    return new CanaryProgressionResult
                    {
                        Decision = CanaryProgressionDecision.Rollback,
                        Rationale = evaluation.Rationale,
                        PreviousPercentage = previousPercentage,
                        CurrentPercentage = previousPercentage,
                        Applied = false,
                        TransitionId = transitionId,
                        IdempotencyKey = idempotencyKey
                    };
                }

            case CanaryProgressionDecision.Promoted:
                {
                    var now = _timeProvider.GetUtcNow();
                    await RecordTransitionAsync(new StageTransitionRecord
                    {
                        TransitionId = transitionId,
                        RunId = runId,
                        FromPercentage = previousPercentage,
                        ToPercentage = previousPercentage,
                        TransitionedAt = now,
                        IdempotencyKey = idempotencyKey,
                        Decision = CanaryProgressionDecision.Promoted,
                        Rationale = evaluation.Rationale
                    }, cancellationToken).ConfigureAwait(false);

                    return new CanaryProgressionResult
                    {
                        Decision = CanaryProgressionDecision.Promoted,
                        Rationale = evaluation.Rationale,
                        PreviousPercentage = previousPercentage,
                        CurrentPercentage = previousPercentage,
                        Applied = false,
                        TransitionId = transitionId,
                        IdempotencyKey = idempotencyKey
                    };
                }

            case CanaryProgressionDecision.Hold:
            default:
                {
                    var now = _timeProvider.GetUtcNow();
                    await RecordTransitionAsync(new StageTransitionRecord
                    {
                        TransitionId = transitionId,
                        RunId = runId,
                        FromPercentage = previousPercentage,
                        ToPercentage = previousPercentage,
                        TransitionedAt = now,
                        IdempotencyKey = idempotencyKey,
                        Decision = CanaryProgressionDecision.Hold,
                        Rationale = evaluation.Rationale
                    }, cancellationToken).ConfigureAwait(false);

                    return new CanaryProgressionResult
                    {
                        Decision = CanaryProgressionDecision.Hold,
                        Rationale = evaluation.Rationale,
                        PreviousPercentage = previousPercentage,
                        CurrentPercentage = previousPercentage,
                        Applied = false,
                        TransitionId = transitionId,
                        IdempotencyKey = idempotencyKey
                    };
                }
        }
    }

    /// <summary>
    /// 自动回滚：将 CutoverController 重置为 0%（全部 Legacy）并记录回滚审计。
    /// </summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="reason">回滚原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="transitionId">可选的 transition ID（默认生成新 GUID）。</param>
    /// <param name="idempotencyKey">可选的幂等键。</param>
    public async ValueTask RollbackAsync(
        string runId,
        RollbackReason reason,
        CancellationToken cancellationToken = default,
        string? transitionId = null,
        string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        var tid = transitionId ?? $"rb-{runId}-{Guid.NewGuid():N}";
        var previousPercentage = GetCurrentPercentage(runId);
        var now = _timeProvider.GetUtcNow();

        // 当 _decisionApplier 非空时走 DB 单事务路径（ApplyCanaryDecisionLocalAsync），
        // 统一 DB 真相源（canary_pipelines 表）。旧路径仅写进程内状态，重启后丢失。
        if (_decisionApplier is not null)
        {
            // 捕获 DB CAS 结果，用于决定本地状态标记。
            // 项目 memory 约束：RollbackAsync 必须无条件立即更新 in-memory 百分比为 0%，
            // 无论 DB CAS 是否成功（安全优先 — 本地先回滚可以成立）。
            // 但 DB CAS 失败时，本地与 DB 状态不再一致，必须建模为 LocalEmergencyRollback。
            var dbResult = await ApplyDecisionToStoreAsync(
                runId, CanaryDecision.Rollback, 0, previousPercentage,
                tid, $"Canary 自动回滚：reason={reason}", cancellationToken).ConfigureAwait(false);

            // 无论 DB CAS 是否成功，都更新内存为 0%（确保路由立即回到 0%，全部 Legacy）
            UpdateInMemoryPercentage(runId, 0);

            if (dbResult.Applied)
            {
                // DB CAS 成功 → 本地与 DB 一致
                _localStates[runId] = CanaryLocalState.Consistent;
            }
            else
            {
                // DB CAS 失败 → 本地已紧急回滚到 0%，但 DB 仍记录旧百分比。
                // 建模为 LocalEmergencyRollback + PersistPending + OperatorAlertRequired。
                // 后续 AdvanceAsync 将拒绝推进，直到通过重试 RollbackAsync 持久化成功
                // 或 Operator 介入修复 DB 状态。
                var emergencyState = CanaryLocalState.LocalEmergencyRollback
                    | CanaryLocalState.PersistPending
                    | CanaryLocalState.OperatorAlertRequired;
                _localStates[runId] = emergencyState;
                _logger.LogWarning(
                    "P1-6：Canary run {RunId} 紧急回滚：本地已切到 0%，但 DB CAS 失败（{FailureReason}）。" +
                    "本地状态标记为 {State}；AdvanceAsync 将拒绝推进，直到持久化成功或 Operator 介入。",
                    runId, dbResult.FailureReason, emergencyState);
            }
        }
        else
        {
            // 回退路径（_decisionApplier=null，测试场景）：仅写进程内状态
            // registry 非空时操作 per-run 专用控制器
            GetController(runId).SetCutoverPercentage(0);
            _runStates[runId] = new CanaryRunState(0, now);

            await RecordTransitionAsync(new StageTransitionRecord
            {
                TransitionId = tid,
                RunId = runId,
                FromPercentage = previousPercentage,
                ToPercentage = 0,
                TransitionedAt = now,
                IdempotencyKey = idempotencyKey,
                Decision = CanaryProgressionDecision.Rollback,
                Rationale = $"Canary 自动回滚：reason={reason}"
            }, cancellationToken).ConfigureAwait(false);
        }

        // 同步持久化 RollbackRecord 到 store（供 Pipeline 的 GetRollbackRecordAsync 查询）
        var snapshot = await _pipelineRunStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && !IsTerminal(snapshot.Status))
        {
            var rollbackRecord = new RollbackRecord(
                recordId: $"rb-{runId}-{now.UtcDateTime.Ticks}",
                runId: runId,
                proposalId: snapshot.ProposalId,
                reason: reason,
                triggeredAt: now)
            {
                TriggeredAtStage = snapshot.CurrentStage
            };
            await _pipelineRunStore.SaveRollbackRecordAsync(rollbackRecord, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 获取当前 canary 百分比（未初始化的 runId 返回 0）。
    /// </summary>
    public int GetCurrentPercentage(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _runStates.TryGetValue(runId, out var state) ? state.Percentage : 0;
    }

    /// <summary>
    /// 获取指定 run 的本地状态（相对 DB 真相源的一致性标记）。
    /// </summary>
    /// <param name="runId">Run ID。</param>
    /// <returns>本地状态枚举（未初始化的 runId 返回 <see cref="CanaryLocalState.Consistent"/>）。</returns>
    /// <remarks>
    /// 用于测试断言与运维诊断。非 <see cref="CanaryLocalState.Consistent"/> 时
    /// <see cref="AdvanceAsync"/> 会拒绝推进，调用方应先解决 PersistPending 状态。
    /// </remarks>
    public CanaryLocalState GetLocalState(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _localStates.TryGetValue(runId, out var state) ? state : CanaryLocalState.Consistent;
    }

    /// <summary>
    /// 在 HA 单事务提交后同步更新 in-memory 状态（CutoverController + _runStates）。
    /// </summary>
    /// <remarks>
    /// <b>背景</b>：<see cref="CanaryLeaderHostedService"/> 在 Perf-7 后改用
    /// <see cref="ICanaryDecisionApplier.ApplyCanaryDecisionAsync"/> 单一事务完成 DB 状态变更
    /// （pipeline revision CAS + transition audit + epoch 递增）。
    /// 但 <see cref="CutoverController"/> 的进程内路由百分比与本服务的 <c>_runStates</c> 字典
    /// 仍是进程本地状态，需在事务提交后由调用方显式同步，确保后续请求路由与持久化状态一致。
    /// 本方法仅更新 in-memory 状态，不写入任何 DB 表（DB 写入已由事务完成）。
    /// </remarks>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="newPercentage">推进/回滚后的新百分比档（0-100）。</param>
    public void UpdateInMemoryPercentage(string runId, int newPercentage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (newPercentage < 0) newPercentage = 0;
        if (newPercentage > 100) newPercentage = 100;

        // registry 非空时操作 per-run 专用控制器
        GetController(runId).SetCutoverPercentage(newPercentage);
        _runStates[runId] = new CanaryRunState(newPercentage, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// 从 DB（<c>canary_pipelines</c> 表）恢复 in-memory 状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恢复的活跃 pipeline 数量（0 = 无活跃 pipeline 或 decisionApplier 未注入）。</returns>
    /// <remarks>
    /// <b>问题背景</b>：Canary 百分比有三个真值源——
    /// <list type="number">
    /// <item><c>canary_pipelines</c> 表（DB，权威，由 <see cref="ICanaryDecisionApplier"/> 维护）。</item>
    /// <item><c>_runStates</c>（进程内 ConcurrentDictionary）。</item>
    /// <item><see cref="CutoverController"/> 的 <c>_cutoverPercentage</c>（进程内 int）。</item>
    /// </list>
    /// 进程重启后 #2/#3 丢失，服务回到 0% 而 DB 仍持有真实百分比。本方法在启动时
    /// 调用 <see cref="ICanaryDecisionApplier.GetAllActivePipelineStatesAsync"/> 读取所有活跃
    /// pipeline，逐个调用 <see cref="UpdateInMemoryPercentage"/> 重建进程内路由状态
    /// （同时恢复 CutoverController 百分比与 <c>_runStates</c> 字典）。
    /// <para>
    /// <b>幂等性</b>：可安全重复调用；已存在的 run 会被最新 DB 值覆盖。
    /// <b>无 DB 写入</b>：本方法只读取 DB，不修改任何持久化状态。
    /// </para>
    /// </remarks>
    public async Task<int> RecoverFromStoreAsync(CancellationToken cancellationToken = default)
    {
        if (_decisionApplier is null)
        {
            // 单节点/测试场景（无 Postgres storage 注册）：无 DB 可恢复，跳过。
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var activeStates = await _decisionApplier.GetAllActivePipelineStatesAsync(cancellationToken).ConfigureAwait(false);
        if (activeStates.Count == 0)
        {
            return 0;
        }

        // 合并 canary_pipelines 表与 IPipelineRunStore 为单一 Pipeline Run 状态源。
        // 当前存在两个持久化聚合：
        //   1. canary_pipelines 表（由 ICanaryDecisionApplier 维护，存储 canary 百分比 + revision + epoch）
        //   2. IPipelineRunStore（存储 PipelineRunSnapshot + 审计记录，由 DefaultGuardedOptimizationPipeline 维护）
        // 两者语义重叠（都记录 run 的当前状态），但 schema 与 CAS 维度不同：
        //   - canary_pipelines 按 run_id CAS revision（canary 百分比推进专用）
        //   - IPipelineRunStore 按 run_id CAS revision + stage（覆盖整个 pipeline 生命周期）
        // 合并方向：将 canary_pipelines 的 percentage/revision/epoch 字段并入 PipelineRunSnapshot
        // （新增 CanaryPercentage / CanaryRevision / CanaryEpoch 字段），让 IPipelineRunStore
        // 成为唯一的 run 状态真相源，消除 RecoverFromStoreAsync 的需要。
        // 改造成本：需迁移 canary_pipelines 表数据、修改 ICanaryDecisionApplier 实现、
        // 更新所有 percentage 读取方（CutoverController 同步、metrics epoch 等）。
        // 短期方案：保持双真相源，但通过本方法在启动时同步，并通过 _localStates 在运行时
        // 显式建模不一致（P1-6）。
        foreach (var state in activeStates)
        {
            if (string.IsNullOrWhiteSpace(state.RunId))
            {
                continue;
            }
            // UpdateInMemoryPercentage 同时恢复 CutoverController 百分比与 _runStates[runId]。
            UpdateInMemoryPercentage(state.RunId, state.Percentage);
            // DB 是权威真相源，恢复后本地与 DB 一致，清除任何紧急回滚标记。
            _localStates[state.RunId] = CanaryLocalState.Consistent;
        }

        return activeStates.Count;
    }

    /// <summary>获取指定 run 的所有 stage transition 审计记录（按时间升序）。</summary>
    /// <remarks>R28-B.8 持久化：直接从 store 查询（权威来源），不再读取 in-memory 字典。</remarks>
    public async Task<IReadOnlyList<StageTransitionRecord>> ListStageTransitionsAsync(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return await _pipelineRunStore.ListStageTransitionsByRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>当前 stage_transitions 审计记录总数（测试与诊断用）。</summary>
    /// <remarks>
    /// 注意：此计数基于 in-memory 投影，仅反映当前进程内通过 RecordTransitionAsync 写入的 transition，
    /// 用于测试快速断言；跨进程或 HA 场景下的权威来源应为 store（<see cref="ListStageTransitionsAsync"/>）。
    /// </remarks>
    public int StageTransitionCount => _stageTransitions.Count;

    // -----------------------------------------------------------------------
    // 内部辅助方法
    // -----------------------------------------------------------------------

    private (string metricName, double metricValue, RollbackReason reason, string rationale)? CheckRollbackThresholds(
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics)
    {
        // 1. parity 差异率（divergence_rate）：experimentMetrics["divergence_rate"] 直接提供
        if (experimentMetrics.TryGetValue("divergence_rate", out var divergence))
        {
            if (divergence > _options.MaxDivergenceRate)
            {
                return ("divergence_rate", divergence, RollbackReason.ModelPerformanceRegression,
                    $"parity 差异率 {divergence:F4} > 阈值 {_options.MaxDivergenceRate:F4}（MaxDivergenceRate）；自动回滚。");
            }
        }

        // 2. 错误率差（error_rate）：experimentMetrics["error_rate"] - baselineMetrics["error_rate"]
        if (experimentMetrics.TryGetValue("error_rate", out var expError) &&
            baselineMetrics.TryGetValue("error_rate", out var baselineError))
        {
            var delta = expError - baselineError;
            if (delta > _options.MaxErrorRateDelta)
            {
                return ("error_rate", delta, RollbackReason.ModelPerformanceRegression,
                    $"错误率差 {delta:F4} > 阈值 {_options.MaxErrorRateDelta:F4}（MaxErrorRateDelta）；" +
                    $"V2={expError:F4}, Legacy={baselineError:F4}；自动回滚。");
            }
        }

        // 3. p95 延迟倍数（p95_latency_ms）：experimentMetrics / baselineMetrics
        if (experimentMetrics.TryGetValue("p95_latency_ms", out var expP95) &&
            baselineMetrics.TryGetValue("p95_latency_ms", out var baselineP95) &&
            baselineP95 > 0)
        {
            var multiplier = expP95 / baselineP95;
            if (multiplier > _options.MaxLatencyMultiplier)
            {
                return ("p95_latency_ms", multiplier, RollbackReason.ModelPerformanceRegression,
                    $"p95 延迟倍数 {multiplier:F2}x > 阈值 {_options.MaxLatencyMultiplier:F2}x（MaxLatencyMultiplier）；" +
                    $"V2 p95={expP95:F2}ms, Legacy p95={baselineP95:F2}ms；自动回滚。");
            }
        }

        // 4. 质量分下限（quality_score）：experimentMetrics["quality_score"] < MinQualityScore
        // 阈值 = 0.0 时禁用此检查（不触发回滚）。
        if (_options.MinQualityScore > 0.0 &&
            experimentMetrics.TryGetValue("quality_score", out var quality) &&
            quality < _options.MinQualityScore)
        {
            return ("quality_score", quality, RollbackReason.ModelPerformanceRegression,
                $"质量分 {quality:F4} < 阈值 {_options.MinQualityScore:F4}（MinQualityScore）；" +
                $"V2 产出质量退化（section 覆盖率 + 候选相关性综合分过低）；自动回滚。");
        }

        // 外部指标回滚检查。指标未采集（不在字典中）时跳过（优雅降级）。
        // 与 quality_score 不同，外部指标只在被 DefaultCanaryExternalMetricsSource 等采集源
        // 真正写入时才出现在 experimentMetrics 字典中（ToExperimentMetrics 仅写入非 null 字段）。

        // 5. 任务成功率下限（task_success_rate < MinTaskSuccessRate）
        if (_options.MinTaskSuccessRate > 0.0 &&
            experimentMetrics.TryGetValue("task_success_rate", out var taskSuccess) &&
            taskSuccess < _options.MinTaskSuccessRate)
        {
            return ("task_success_rate", taskSuccess, RollbackReason.ModelPerformanceRegression,
                $"任务成功率 {taskSuccess:F4} < 阈值 {_options.MinTaskSuccessRate:F4}（MinTaskSuccessRate）；" +
                $"V2 路径功能严重退化；自动回滚。");
        }

        // 6. 安全违规率上限（safety_violation_rate > MaxSafetyViolationRate）
        // 默认 MaxSafetyViolationRate=0.0（零容忍）；设为 1.0 时禁用。
        // RollbackReason 复用 ModelPerformanceRegression（safety violation 视为 V2 路径退化的一种）。
        if (_options.MaxSafetyViolationRate < 1.0 &&
            experimentMetrics.TryGetValue("safety_violation_rate", out var safetyViolation) &&
            safetyViolation > _options.MaxSafetyViolationRate)
        {
            return ("safety_violation_rate", safetyViolation, RollbackReason.ModelPerformanceRegression,
                $"安全违规率 {safetyViolation:F4} > 阈值 {_options.MaxSafetyViolationRate:F4}（MaxSafetyViolationRate）；" +
                $"V2 路径触发安全违规；自动回滚（零容忍）。");
        }

        // 7. 用户接受率下限（user_acceptance < MinUserAcceptance）
        if (_options.MinUserAcceptance > 0.0 &&
            experimentMetrics.TryGetValue("user_acceptance", out var userAcceptance) &&
            userAcceptance < _options.MinUserAcceptance)
        {
            return ("user_acceptance", userAcceptance, RollbackReason.ModelPerformanceRegression,
                $"用户接受率 {userAcceptance:F4} < 阈值 {_options.MinUserAcceptance:F4}（MinUserAcceptance）；" +
                $"V2 路径用户体验退化；自动回滚。");
        }

        return null;
    }

    private int? GetNextPercentage(int currentPercentage)
    {
        var ladder = _options.PercentageLadder;
        for (var i = 0; i < ladder.Count; i++)
        {
            if (ladder[i] > currentPercentage)
            {
                return ladder[i];
            }
        }
        return null;
    }

    /// <summary>
    /// 通过 <see cref="ICanaryDecisionApplier.ApplyCanaryDecisionLocalAsync"/> 单事务写入 DB
    /// （canary_pipelines revision CAS + transition audit + epoch 递增），统一 DB 真相源。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="decision">决策类型（Promote/Rollback）。</param>
    /// <param name="newPercentage">新百分比档（Rollback 时为 0）。</param>
    /// <param name="previousPercentage">推进前百分比（用于 transition 描述）。</param>
    /// <param name="transitionId">transition ID（审计幂等去重）。</param>
    /// <param name="rationale">决策理由（写入审计）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>决策执行结果（Applied=true 时 DB 事务已提交）。</returns>
    /// <remarks>
    /// <b>单节点模式</b>：跳过 lease/fencing 校验（FencingToken 传 "0"，被
    /// <see cref="ICanaryDecisionApplier.ApplyCanaryDecisionLocalAsync"/> 忽略），
    /// 仅执行 revision CAS + transition audit + epoch update，确保 DB 与进程内状态一致。
    /// <para>
    /// <b>Epoch 计算</b>：从 <see cref="ICanaryDecisionApplier.GetCurrentEpochAsync"/> 读取当前 epoch，
    /// 计算 <c>newEpoch = currentEpoch + 1</c>，确保重启后 epoch 单调递增（不回退）。
    /// </para>
    /// </remarks>
    private async ValueTask<CanaryDecisionResult> ApplyDecisionToStoreAsync(
        string runId,
        CanaryDecision decision,
        int newPercentage,
        int previousPercentage,
        string transitionId,
        string rationale,
        CancellationToken cancellationToken)
    {
        // 查询当前 pipeline 状态获取 CAS 预期 revision
        var pipelineState = await _decisionApplier!.GetCanaryPipelineStateAsync(
            runId, cancellationToken).ConfigureAwait(false);

        // 读取当前 epoch，计算 newEpoch = current + 1（确保重启后不回退）
        var currentEpoch = await _decisionApplier.GetCurrentEpochAsync(
            runId, cancellationToken).ConfigureAwait(false);

        var transition = decision == CanaryDecision.Rollback
            ? $"{previousPercentage}→0(rollback)"
            : $"{previousPercentage}→{newPercentage}";

        var request = new CanaryDecisionRequest
        {
            RunId = runId,
            ExpectedRevision = pipelineState.Revision,
            // 单节点模式无 Leader lease，FencingToken 传 "0"（被 ApplyCanaryDecisionLocalAsync 忽略）
            FencingToken = "0",
            Decision = decision,
            NewPercentage = newPercentage,
            Transition = transition,
            NewEpoch = currentEpoch + 1,
            TransitionId = transitionId,
            Rationale = rationale
        };

        return await _decisionApplier.ApplyCanaryDecisionLocalAsync(
            request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordTransitionAsync(StageTransitionRecord record, CancellationToken cancellationToken)
    {
        // 幂等：相同 transitionId 不覆盖（TryAdd 语义）— in-memory 快速路径，供 StageTransitionCount 与 AdvanceAsync 幂等检查使用
        _stageTransitions.TryAdd(record.TransitionId, record);
        // 持久化到 store（权威来源；同 TransitionId 覆盖）
        await _pipelineRunStore.SaveStageTransitionAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Promoted => true,
        PipelineRunStatus.RolledBack => true,
        PipelineRunStatus.Rejected => true,
        PipelineRunStatus.Cancelled => true,
        PipelineRunStatus.Failed => true,
        _ => false
    };

    /// <summary>Canary run 的运行时状态（当前百分比 + 进入当前档的时间戳）。</summary>
    private sealed record CanaryRunState(int Percentage, DateTimeOffset EnteredAt);
}
