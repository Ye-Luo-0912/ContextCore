using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// Canary 紧急回滚本地状态建模
//
// 背景：
// RollbackAsync 在 DB CAS 失败时仍无条件将本地流量切到 0%（安全优先），
// 但旧实现没有把"本地已回滚 / DB 未持久化"这一不一致状态建模出来，
// 后续 AdvanceAsync 会把本地与 DB 状态当作一致继续推进，可能掩盖 DB 真实状态。
//
// P0-11：RollbackAsync 先写入持久化 Kill Switch（Emergency Override）再回滚。
// 覆盖存在期间 run 不进入 Consistent（推进被阻断，与 RecoverFromStoreAsync 语义一致），
// 直到 Operator 清除覆盖；Kill Switch 写入失败时本地持续 fail-closed。
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
/// <see cref="LocalEmergencyRollback"/> | <see cref="PersistPending"/> | <see cref="OperatorAlertRequired"/>；
/// 已持久化 Kill Switch 的自动回滚（DB 已回滚 0%）设置
/// <see cref="LocalEmergencyRollback"/> | <see cref="OperatorAlertRequired"/>（覆盖保留，等待人工确认清除）。
/// </para>
/// </remarks>
[Flags]
public enum CanaryLocalState : byte
{
    /// <summary>本地与 DB 一致（无任何未持久化变更）。</summary>
    Consistent = 0,

    /// <summary>本地已紧急回滚到 0% 且紧急状态尚未解除（活跃 Kill Switch 覆盖，或 DB CAS 失败）。</summary>
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
// 目标：
// 1. 在 ScopedCanary 阶段内部按 CanaryGateOptions.PercentageLadder 渐进推进 CutoverController。
// 2. 每次推进前评估 metrics（parity/error/latency）：超阈值自动回滚；未达最小观察时长 Hold。
// 3. 100% 时不再执行 Legacy（CutoverController.CutoverPercentage=100 跳过 Legacy 路径）。
// 4. 端到端幂等：相同 TransitionId 重复调用不产生重复推进（依赖 store 的 LastTransitionId）。
// 5. 审计：每次推进记录到 stage_transitions 审计表（in-memory 实现；生产环境由
// PostgresPipelineRunStore 同事务写入持久化审计表）。
//
// 设计边界：
// - 本服务不替代 IPromotionJudge；仅在 ScopedCanary 阶段内部做渐进百分比推进。
// - 终态（RolledBack/Promoted）：调用方应先检查 run status，已终态的 run 推进为 no-op。
// - 与 CutoverController 的关系：本服务持有 CutoverController 引用，通过 SetCutoverPercentage
// 调整 V2 流量比例。多个 run 共享同一 CutoverController 时，最新推进的 run 决定全局百分比
// （生产环境应为每个 run 隔离 CutoverController 实例，或使用 workspace 级别路由）。
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
/// await service.AdvanceAsync(runId, transitionId, idempotencyKey, baselineMetrics, experimentMetrics, ct);
/// }
/// else if (eval.Decision == CanaryProgressionDecision.Rollback)
/// {
/// await service.RollbackAsync(runId, eval.RollbackReason!.Value, ct);
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
    // 可选的集群级 Kill Switch 存储。非空时 RecoverFromStoreAsync 对存在活跃紧急覆盖的
    // run 强制恢复 0% 且不进入 Consistent；路由层在命中 V2 时也会先检查本存储。
    // P0-11：RollbackAsync 会先向本存储写入自动回滚覆盖（持久化 Kill Switch），
    // 写入失败时本地持续 fail-closed。
    private readonly ICanaryEmergencyOverrideStore? _emergencyOverrideStore;

    // 自动回滚写入 Kill Switch 时使用的固定 Operator 标识（区别于人工运维 API 的账号）。
    private const string AutomaticRollbackOperatorName = "system:automatic-rollback";
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
    /// <param name="emergencyOverrideStore">
    /// 可选的集群级 Kill Switch 存储。非空时 <see cref="RecoverFromStoreAsync"/> 对存在
    /// 活跃紧急覆盖的 run 强制恢复为 0% 且不进入 <see cref="CanaryLocalState.Consistent"/>，
    /// 直到运维显式清除覆盖。
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
        ICanaryEmergencyOverrideStore? emergencyOverrideStore = null,
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
        _emergencyOverrideStore = emergencyOverrideStore;
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
        // P0-11：活跃 Kill Switch（Emergency Override）引起的紧急状态（无 PersistPending）
        // 在 Operator 清除覆盖后自动解除（重新查询覆盖并重新同步为 Consistent）。
        var localState = GetLocalState(runId);
        if (localState != CanaryLocalState.Consistent)
        {
            if (await IsProgressionBlockedAsync(runId, localState, cancellationToken).ConfigureAwait(false))
            {
                return new CanaryProgressionResult
                {
                    Decision = CanaryProgressionDecision.Hold,
                    Rationale = $"本地状态非 Consistent（{localState}）；拒绝推进。需先解决 PersistPending（重试 RollbackAsync 持久化或 Operator 介入）或清除活跃紧急覆盖（Kill Switch）。",
                    PreviousPercentage = previousPercentage,
                    CurrentPercentage = previousPercentage,
                    Applied = false,
                    TransitionId = transitionId,
                    IdempotencyKey = idempotencyKey
                };
            }

            // 阻断已解除（Operator 清除了 Kill Switch 且无 PersistPending）：
            // 重新同步为 Consistent（DB 为权威真相源），允许本次推进继续。
            _localStates[runId] = CanaryLocalState.Consistent;
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
                            // 单真相源写入 — canary 状态并入 pipeline run snapshot
                            await PersistCanaryStateToRunAsync(
                                runId, nextPercentage, dbResult.NewEpoch,
                                _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
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

                    // 单真相源写入 — canary 状态并入 pipeline run snapshot（epoch 自增）
                    await PersistCanaryStateToRunAsync(
                        runId, nextPercentage, newEpoch: null, now, cancellationToken).ConfigureAwait(false);

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
    /// <remarks>
    /// <para>
    /// <b>P0-11 安全顺序</b>：先写入持久化 Kill Switch（<see cref="ICanaryEmergencyOverrideStore"/>），
    /// 再本地切 0%，最后尝试推进 Canary 真相源（DB CAS）。覆盖存在期间 run 不进入
    /// <see cref="CanaryLocalState.Consistent"/>（推进被阻断，直到 Operator 清除覆盖）；
    /// Kill Switch 写入失败时本地持续 fail-closed（<see cref="CanaryLocalState.LocalEmergencyRollback"/>），
    /// 不能只保存在内存——否则节点重启后旧百分比可能重新恢复。
    /// </para>
    /// </remarks>
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

        // P0-11：先建立持久化 Kill Switch（Emergency Override），再本地切 0%，
        // 最后尝试推进 Canary 真相源（DB CAS）。
        // 覆盖已存在时视为已生效（TrySetOverrideAsync 返回 false = 已有活跃覆盖，不覆盖不报错）。
        // Kill Switch 写入失败时 overridePersisted=false——下方统一按 fail-closed 处理，
        // 无论 DB CAS 结果如何都不会标记 Consistent。
        var overridePersisted = false;
        if (_emergencyOverrideStore is not null)
        {
            try
            {
                overridePersisted = await _emergencyOverrideStore.TrySetOverrideAsync(
                    runId,
                    $"Canary 自动回滚：reason={reason}",
                    AutomaticRollbackOperatorName,
                    cancellationToken).ConfigureAwait(false);
                if (!overridePersisted)
                {
                    // 已存在活跃覆盖（人工或更早的自动回滚触发）→ Kill Switch 已生效。
                    overridePersisted = await _emergencyOverrideStore.GetActiveAsync(
                        runId, cancellationToken).ConfigureAwait(false) is not null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Kill Switch 持久化失败：保持 fail-closed（本地 0% + 紧急状态 + 告警）。
                _logger.LogError(ex,
                    "P0-11：Canary run {RunId} 紧急回滚无法持久化 Kill Switch（Emergency Override 写入失败）；" +
                    "本地必须持续 fail-closed，不能只保存在内存。",
                    runId);
            }
        }

        // 本地 route = 0%（无条件，安全优先——无论 Kill Switch / DB CAS 结果如何）。
        UpdateInMemoryPercentage(runId, 0);

        // 当 _decisionApplier 非空时走 DB 单事务路径（ApplyCanaryDecisionLocalAsync），
        // 统一 DB 真相源（canary_pipelines 表）。旧路径仅写进程内状态，重启后丢失。
        if (_decisionApplier is not null)
        {
            var dbResult = await ApplyDecisionToStoreAsync(
                runId, CanaryDecision.Rollback, 0, previousPercentage,
                tid, $"Canary 自动回滚：reason={reason}", cancellationToken).ConfigureAwait(false);

            if (dbResult.Applied)
            {
                // 单真相源写入 — 回滚到 0%
                await PersistCanaryStateToRunAsync(
                    runId, 0, dbResult.NewEpoch, now, cancellationToken).ConfigureAwait(false);
            }

            _localStates[runId] = ResolveRollbackLocalState(overridePersisted, dbResult.Applied);

            if (overridePersisted && dbResult.Applied)
            {
                // DB 已持久化 0%；保留 Override 等待人工确认清除（P0-11）——
                // 与 RecoverFromStoreAsync 语义一致：活跃覆盖期间不得标记 Consistent。
                _logger.LogWarning(
                    "P0-11：Canary run {RunId} 自动回滚已持久化 Kill Switch（Emergency Override）并回滚到 0%；" +
                    "保留覆盖等待人工确认清除；本地状态标记为 {State}，推进被阻断。",
                    runId, _localStates[runId]);
            }
            else if (dbResult.Applied)
            {
                // DB 成功但 Kill Switch 未能持久化（无存储或写入失败）→ 0% 已持久化。
                // 有存储但写入失败时保持 fail-closed（不标记 Consistent）。
                if (_emergencyOverrideStore is not null)
                {
                    _logger.LogError(
                        "P0-11：Canary run {RunId} 自动回滚 DB 已持久化 0%，但 Kill Switch 未能持久化；" +
                        "本地状态标记为 {State}（fail-closed）。",
                        runId, _localStates[runId]);
                }
            }
            else
            {
                // DB CAS 失败 → 本地已紧急回滚到 0%，但 DB 仍记录旧百分比。
                // 建模为 LocalEmergencyRollback + PersistPending + OperatorAlertRequired。
                // 后续 AdvanceAsync 将拒绝推进，直到通过重试 RollbackAsync 持久化成功
                // 或 Operator 介入修复 DB 状态。
                _logger.LogWarning(
                    "P0-11：Canary run {RunId} 紧急回滚：本地已切到 0%，但 DB CAS 失败（{FailureReason}）。" +
                    "本地状态标记为 {State}；AdvanceAsync 将拒绝推进，直到持久化成功或 Operator 介入。",
                    runId, dbResult.FailureReason, _localStates[runId]);
            }
        }
        else
        {
            // 回退路径（_decisionApplier=null，测试场景）：仅写进程内状态
            // （本地 0% 已由上方 UpdateInMemoryPercentage 无条件完成）。
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

            // 单真相源写入 — 回滚到 0%（epoch 自增）
            await PersistCanaryStateToRunAsync(runId, 0, newEpoch: null, now, cancellationToken).ConfigureAwait(false);

            // 回退路径无 DB CAS：视为本地已持久化（dbPersisted=true）。
            _localStates[runId] = ResolveRollbackLocalState(overridePersisted, dbPersisted: true);
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
    /// 解析自动回滚后的本地状态（P0-11：Kill Switch 优先 + fail-closed）。
    /// </summary>
    /// <param name="overridePersisted">持久化 Kill Switch（Emergency Override）是否已生效。</param>
    /// <param name="dbPersisted">Canary 真相源（canary_pipelines / DB CAS）是否已持久化 0%。</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>无 Kill Switch 存储：无法建立持久化覆盖，维持原语义（DB 成功 → Consistent）。</item>
    /// <item>覆盖已生效：保留等待人工确认清除——即使 DB 已回滚 0% 也不得标记 Consistent
    /// （与 <see cref="RecoverFromStoreAsync"/> 语义一致），推进被阻断直到覆盖被清除。</item>
    /// <item>覆盖写入失败：持续 fail-closed，不得标记 Consistent（无论 DB CAS 结果如何）。</item>
    /// </list>
    /// DB 未持久化时叠加 <see cref="CanaryLocalState.PersistPending"/>（DB 需修复或重试持久化）。
    /// </remarks>
    private CanaryLocalState ResolveRollbackLocalState(bool overridePersisted, bool dbPersisted)
    {
        if (_emergencyOverrideStore is null)
        {
            // 无 Kill Switch 存储：无法建立持久化覆盖，维持原语义。
            return dbPersisted
                ? CanaryLocalState.Consistent
                : CanaryLocalState.LocalEmergencyRollback | CanaryLocalState.PersistPending | CanaryLocalState.OperatorAlertRequired;
        }

        var state = CanaryLocalState.LocalEmergencyRollback | CanaryLocalState.OperatorAlertRequired;
        if (!dbPersisted)
        {
            state |= CanaryLocalState.PersistPending;
        }
        return state;
    }

    /// <summary>
    /// 判断推进是否被本地一致性状态阻断（P0-11：Kill Switch 感知的阻断判定）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="localState">当前本地状态（非 Consistent 时调用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 阻断推进；false = 可放行（且调用方应重新同步本地状态为 Consistent）。</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>含 <see cref="CanaryLocalState.PersistPending"/>：DB 真相源未持久化（紧急回滚 DB CAS 失败），
    /// 必须等重试持久化或 Operator 修复——不能仅凭 Override 清除解除（DB 仍可能持有旧百分比）。</item>
    /// <item>无 PersistPending 的紧急状态由活跃 Kill Switch（Override）引起
    /// （<see cref="RollbackAsync"/> / <see cref="RecoverFromStoreAsync"/> 设置）：
    /// Operator 清除覆盖后解除阻断并重新同步为 Consistent（DB 为权威真相源）。</item>
    /// <item>Kill Switch 查询失败 → fail-closed：视为覆盖仍活跃，保持阻断并告警。</item>
    /// </list>
    /// </remarks>
    private async ValueTask<bool> IsProgressionBlockedAsync(
        string runId,
        CanaryLocalState localState,
        CancellationToken cancellationToken)
    {
        if (localState == CanaryLocalState.Consistent)
        {
            return false;
        }

        if (localState.HasFlag(CanaryLocalState.PersistPending))
        {
            return true;
        }

        // 无 PersistPending 的紧急状态：由活跃 Override 引起。
        if (_emergencyOverrideStore is null)
        {
            // 无 Kill Switch 存储：视为已解除（防御性；生产 profile 恒注册）。
            return false;
        }

        try
        {
            return await _emergencyOverrideStore.GetActiveAsync(runId, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Kill Switch 查询失败 → fail-closed：视为覆盖仍活跃，保持阻断并告警。
            _logger.LogWarning(ex,
                "P0-11：Canary run {RunId} 推进前查询 Kill Switch 失败；按覆盖活跃处理（fail-closed）。",
                runId);
            return true;
        }
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
    /// 恢复 in-memory 状态（单一真相源优先）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>恢复的活跃 pipeline 数量（0 = 无活跃 pipeline）。</returns>
    /// <remarks>
    /// <b>问题背景</b>：Canary 百分比有三个真值源——
    /// <list type="number">
    /// <item><c>canary_pipelines</c> 表（DB，legacy，由 <see cref="ICanaryDecisionApplier"/> 维护）。</item>
    /// <item><c>_runStates</c>（进程内 ConcurrentDictionary）。</item>
    /// <item><see cref="CutoverController"/> 的 <c>_cutoverPercentage</c>（进程内 int）。</item>
    /// </list>
    /// 进程重启后 / 丢失，服务回到 0% 而 DB 仍持有真实百分比。本方法在启动时重建
    /// 进程内路由状态（CutoverController 百分比与 <c>_runStates</c> 字典）。
    /// <para>
    /// <b>合并方向</b>：canary 状态已并入 <see cref="PipelineRunSnapshot"/>
    /// （CanaryPercentage / CanaryRevision / CanaryEpoch，由 <see cref="PersistCanaryStateToRunAsync"/>
    /// 经 <see cref="IPipelineRunStore.UpdateCanaryStateAsync"/> 持久化）。因此本方法
    /// <b>优先从 <see cref="IPipelineRunStore"/> 的 ScopedCanary 阶段 run snapshot 恢复</b>
    /// （snapshot.CanaryPercentage 权威，消除对 canary_pipelines 表的依赖）；
    /// 仅当 run 尚未持久化 canary 数据（CanaryRevision == 0，legacy 数据）时，回退到
    /// <see cref="ICanaryDecisionApplier.GetAllActivePipelineStatesAsync"/>。
    /// </para>
    /// <para>
    /// <b>幂等性</b>：可安全重复调用；已存在的 run 会被最新值覆盖。
    /// <b>无 DB 写入</b>：本方法只读取 store，不修改任何持久化状态。
    /// </para>
    /// </remarks>
    public async Task<int> RecoverFromStoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var restored = 0;

        // 优先从 pipeline run snapshot 恢复（单一真相源）。
        // snapshot.CanaryRevision > 0 表示 canary 状态已由 PersistCanaryStateToRunAsync 持久化；
        // == 0 表示 legacy 数据（尚未经单真相源写入），交给 canary_pipelines 回退路径。
        var snapshotRestored = new HashSet<string>(StringComparer.Ordinal);
        var snapshotRuns = await _pipelineRunStore.ListRunsByStageAsync(
            OptimizationStage.ScopedCanary, take: 100, cancellationToken).ConfigureAwait(false);
        foreach (var run in snapshotRuns)
        {
            if (IsTerminal(run.Status) || run.CanaryRevision <= 0)
            {
                continue;
            }
            snapshotRestored.Add(run.RunId);
            restored++;

            // 集群级 Kill Switch 优先：存在活跃紧急覆盖时强制 0% + 紧急本地状态，
            // 且不得标记 Consistent（运维清除覆盖前推进被拒绝，路由层已强制回退 V1）。
            // 避免重启后覆盖仍生效却按 DB 百分比恢复，导致 Kill Switch 被绕过。
            if (_emergencyOverrideStore is not null
                && await _emergencyOverrideStore.GetActiveAsync(run.RunId, cancellationToken).ConfigureAwait(false) is not null)
            {
                UpdateInMemoryPercentage(run.RunId, 0);
                _localStates[run.RunId] = CanaryLocalState.LocalEmergencyRollback | CanaryLocalState.OperatorAlertRequired;
                _logger.LogWarning(
                    "Canary run {RunId} 存在活跃紧急覆盖（Kill Switch），恢复为 0% 且标记紧急回滚，等待运维清除覆盖。",
                    run.RunId);
                continue;
            }

            // UpdateInMemoryPercentage 同时恢复 CutoverController 百分比与 _runStates[runId]。
            UpdateInMemoryPercentage(run.RunId, run.CanaryPercentage);
            _localStates[run.RunId] = CanaryLocalState.Consistent;
        }

        // legacy 回退路径（canary_pipelines 表）：仅覆盖 snapshot 未恢复的 run
        // （decisionApplier 为 null 时无 legacy 可恢复，仅 snapshot 恢复）。
        if (_decisionApplier is null)
        {
            return restored;
        }

        var activeStates = await _decisionApplier.GetAllActivePipelineStatesAsync(cancellationToken).ConfigureAwait(false);
        if (activeStates.Count == 0)
        {
            return restored;
        }

        foreach (var state in activeStates)
        {
            if (string.IsNullOrWhiteSpace(state.RunId))
            {
                continue;
            }
            if (snapshotRestored.Contains(state.RunId))
            {
                // 已由 snapshot 恢复（snapshot 是权威真相源）；跳过避免双重计数。
                continue;
            }
            restored++;

            // 集群级 Kill Switch 优先（与 snapshot 路径一致）
            if (_emergencyOverrideStore is not null
                && await _emergencyOverrideStore.GetActiveAsync(state.RunId, cancellationToken).ConfigureAwait(false) is not null)
            {
                UpdateInMemoryPercentage(state.RunId, 0);
                _localStates[state.RunId] = CanaryLocalState.LocalEmergencyRollback | CanaryLocalState.OperatorAlertRequired;
                _logger.LogWarning(
                    "Canary run {RunId} 存在活跃紧急覆盖（Kill Switch），恢复为 0% 且标记紧急回滚，等待运维清除覆盖。",
                    state.RunId);
                continue;
            }

            // UpdateInMemoryPercentage 同时恢复 CutoverController 百分比与 _runStates[runId]。
            UpdateInMemoryPercentage(state.RunId, state.Percentage);
            // DB 是权威真相源，恢复后本地与 DB 一致，清除任何紧急回滚标记。
            _localStates[state.RunId] = CanaryLocalState.Consistent;
        }

        return restored;
    }

    /// <summary>获取指定 run 的所有 stage transition 审计记录（按时间升序）。</summary>
    /// <remarks>持久化：直接从 store 查询（权威来源），不再读取 in-memory 字典。</remarks>
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

    /// <summary>
    /// 单真相源写入：将 canary 推进状态并入 pipeline run snapshot。
    /// </summary>
    /// <remarks>
    /// 合并方向：canary_pipelines 的 percentage/revision/epoch 并入
    /// <see cref="PipelineRunSnapshot"/>（CanaryPercentage / CanaryRevision / CanaryEpoch），
    /// 让 <see cref="IPipelineRunStore"/> 成为 run 状态唯一真相源，重启后可直接从 snapshot
    /// 恢复（<see cref="RecoverFromStoreAsync"/> 优先读取 snapshot）。
    /// <para>
    /// 失败语义：run 不存在或 canary revision CAS 不匹配时仅记录日志，<b>不阻断推进</b>——
    /// legacy canary_pipelines 写入已由 <see cref="ICanaryDecisionApplier"/> 完成，双真相源
    /// 过渡期内不丢状态；后续推进的 CAS 以最新 snapshot 为准自然收敛。
    /// </para>
    /// </remarks>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="newPercentage">推进后的百分比档。</param>
    /// <param name="newEpoch">推进后的 stage epoch；null 时从 snapshot.CanaryEpoch + 1 自增。</param>
    /// <param name="updatedAt">更新时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask PersistCanaryStateToRunAsync(
        string runId,
        int newPercentage,
        long? newEpoch,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _pipelineRunStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                // run 尚未创建（如直接驱动 CanaryProgressionService 的测试场景）：无可持久化的 snapshot。
                return;
            }

            var updated = await _pipelineRunStore.UpdateCanaryStateAsync(
                runId,
                snapshot.CanaryRevision,
                newPercentage,
                newEpoch ?? snapshot.CanaryEpoch + 1,
                updatedAt,
                cancellationToken).ConfigureAwait(false);
            if (updated is null)
            {
                _logger.LogWarning(
                    "Canary run {RunId} 单真相源写入失败（canary revision CAS 不匹配）；" +
                    "保留 legacy canary_pipelines 状态，下次推进自然收敛。",
                    runId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Canary run {RunId} 单真相源写入异常；不阻断推进。", runId);
        }
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
