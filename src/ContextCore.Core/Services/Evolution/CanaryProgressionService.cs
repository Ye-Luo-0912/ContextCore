using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// R28-B.8：Production Canary Gate — 渐进推进服务
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
/// R28-B.8：Canary 渐进推进评估结果。
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
/// R28-B.8：Canary 渐进推进执行结果。
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
/// R28-B.8：Canary 渐进推进服务。基于 metrics 自动推进或回滚。
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
    // R28-B.8 工作包 B：可选的 per-run CutoverController 注册表。
    // 非空时 InitializeCanary/AdvanceAsync/RollbackAsync 操作 registry.GetOrCreate(runId) 专用控制器；
    // 为 null 时回退到直接注入的 _cutoverController（B-8 之前行为，保持向后兼容）。
    private readonly CutoverControllerRegistry? _registry;

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
    /// R28-B.8 工作包 B：可选的 per-run CutoverController 注册表。非空时按 runId 隔离控制器，
    /// 避免多 run 共享 Singleton 导致百分比互相覆盖；为 null 时回退到 <paramref name="cutoverController"/>。
    /// </param>
    public CanaryProgressionService(
        IPipelineRunStore pipelineRunStore,
        CutoverController cutoverController,
        CanaryGateOptions? options = null,
        TimeProvider? timeProvider = null,
        CutoverControllerRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(pipelineRunStore);
        ArgumentNullException.ThrowIfNull(cutoverController);
        _pipelineRunStore = pipelineRunStore;
        _cutoverController = cutoverController;
        _options = options ?? new CanaryGateOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registry = registry;
    }

    /// <summary>
    /// R28-B.8 工作包 B：解析指定 run 应操作的 CutoverController。
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
            // R28-B.8 工作包 B：registry 非空时操作 per-run 专用控制器
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

        var evaluation = await EvaluateAsync(runId, baselineMetrics, experimentMetrics, cancellationToken).ConfigureAwait(false);

        switch (evaluation.Decision)
        {
            case CanaryProgressionDecision.Advance:
                {
                    var nextPercentage = evaluation.NextPercentage!.Value;
                    // R28-B.8 工作包 B：registry 非空时操作 per-run 专用控制器
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

        // 重置 CutoverController 到 0%（全部 Legacy）
        // R28-B.8 工作包 B：registry 非空时操作 per-run 专用控制器
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

        // 4. R29 WP-C-3：质量分下限（quality_score）：experimentMetrics["quality_score"] < MinQualityScore
        // 阈值 = 0.0 时禁用此检查（不触发回滚）。
        if (_options.MinQualityScore > 0.0 &&
            experimentMetrics.TryGetValue("quality_score", out var quality) &&
            quality < _options.MinQualityScore)
        {
            return ("quality_score", quality, RollbackReason.ModelPerformanceRegression,
                $"质量分 {quality:F4} < 阈值 {_options.MinQualityScore:F4}（MinQualityScore）；" +
                $"V2 产出质量退化（section 覆盖率 + 候选相关性综合分过低）；自动回滚。");
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
