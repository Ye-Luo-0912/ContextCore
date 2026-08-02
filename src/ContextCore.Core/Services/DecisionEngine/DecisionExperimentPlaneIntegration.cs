using System.Threading.Channels;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ---------------------------------------------------------------------------
// IExperimentRecorder — Replay fixture 持久化抽象
// ---------------------------------------------------------------------------

/// <summary>
/// Replay fixture 持久化抽象。
/// 默认实现为 in-memory；生产环境可替换为 file/Postgres 后端。
/// </summary>
public interface IExperimentRecorder
{
    /// <summary>持久化一条 replay fixture。</summary>
    ValueTask RecordAsync(ReplayFixture fixture, CancellationToken cancellationToken = default);

    /// <summary>读取全部历史 fixture（按时间升序）。</summary>
    ValueTask<IReadOnlyList<ReplayFixture>> GetHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>清除全部历史 fixture（用于测试或重置）。</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认 in-memory 实现。进程内 List + lock，重启即丢失。
/// 生产环境可通过 DI 替换为持久化实现。
/// </summary>
public sealed class InMemoryExperimentRecorder : IExperimentRecorder
{
    private readonly List<ReplayFixture> _fixtures = new();
    private readonly int _maxCapacity;
    private readonly object _lock = new();

    /// <summary>构造 in-memory recorder，默认保留最近 10000 条。</summary>
    public InMemoryExperimentRecorder(int maxCapacity = 10000)
    {
        if (maxCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(maxCapacity));
        _maxCapacity = maxCapacity;
    }

    /// <inheritdoc/>
    public ValueTask RecordAsync(ReplayFixture fixture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        lock (_lock)
        {
            _fixtures.Add(fixture);
            while (_fixtures.Count > _maxCapacity)
            {
                _fixtures.RemoveAt(0);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ReplayFixture>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return new ValueTask<IReadOnlyList<ReplayFixture>>(_fixtures.ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _fixtures.Clear();
        }
        return ValueTask.CompletedTask;
    }
}

// ===========================================================================
// Legacy Removal + DecisionExperimentPlane 长期保留
//
// 目标（B-5 阶段：V2 成为唯一权威路径，Legacy 代码保留但默认停用）：
//   1. DecisionExperimentPlaneIntegration：长期保留的实验平面集成入口。
//      提供 sampled shadow（抽样校验）、replay fixture 存储、CI 验收 hook。
//   2. CutoverConfiguration：从配置读取默认 cutover 比例（默认 0% = Legacy only（Closure Gate 通过前安全默认））。
//   3. LegacyCodeMarkedDeprecated：标记 Legacy 路径为 [Obsolete]（不物理删除，
//      保留用于回滚和 DecisionExperimentPlane 的 parity 对比基线）。
//
// 设计原则：
//   1. B-5 不物理删除 Legacy 代码（HybridContextRetriever / BasicContextPackageBuilder）。
//      原因：DecisionExperimentPlane 需要 Legacy 作为 parity 基线；
//      回滚安全需要 Legacy 代码可用。
//   2. CutoverController 默认 0%（Legacy only），可通过配置降级。
//   3. DecisionExperimentPlane 作为长期基础设施：
//      - Sampled shadow：即使 V2 已权威，仍按采样率执行 Legacy + parity 对比
//      - Replay fixture：存储历史 parity 报告供回归分析
//      - CI 验收 hook：ShadowGateEvaluator 输出 CutoverReadinessAssessment
// ===========================================================================

// ---------------------------------------------------------------------------
// CutoverConfiguration — 配置驱动默认比例
// ---------------------------------------------------------------------------

/// <summary>
/// Cutover 配置。从环境变量/配置读取默认 cutover 比例。
/// </summary>
/// <remarks>
/// 默认 0%（Legacy only）。可通过环境变量 CC_CUTOVER_PERCENTAGE 降级。
/// B-5 阶段 Legacy 代码保留但默认停用（CutoverPercentage=0）。
/// </remarks>
public sealed class CutoverConfiguration
{
    /// <summary>环境变量名：控制 V2 流量百分比（0-100）。</summary>
    public const string CutoverPercentageEnvVar = "CC_CUTOVER_PERCENTAGE";

    /// <summary>默认 cutover 百分比（R28-B.6 Closure Gate: 0 = Legacy only，直到 Closure Gate 通过）。</summary>
    /// <remarks>
    /// 曾设为 100（V2 only），但 B.6 Closure Gate 要求在验收测试通过前
    /// 默认走 Legacy，避免未完成的 Provider 网络导致空结果。
    /// 通过环境变量 CC_CUTOVER_PERCENTAGE 可覆盖（如测试环境设为 100）。
    /// </remarks>
    public const int DefaultCutoverPercentage = 0;

    /// <summary>当前配置的 cutover 百分比。</summary>
    public int CutoverPercentage { get; init; } = DefaultCutoverPercentage;

    /// <summary>是否启用 sampled shadow（即使 V2 权威，仍抽样校验）。</summary>
    public bool EnableSampledShadow { get; init; } = true;

    /// <summary>Sampled shadow 采样率（0-1，默认 0.01 = 1%）。</summary>
    public double ShadowSampleRate { get; init; } = 0.01;

    /// <summary>从环境变量构建配置。</summary>
    public static CutoverConfiguration FromEnvironment()
    {
        var percentage = DefaultCutoverPercentage;
        var envValue = Environment.GetEnvironmentVariable(CutoverPercentageEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue) && int.TryParse(envValue, out var parsed))
        {
            percentage = Math.Clamp(parsed, 0, 100);
        }

        var enableShadow = true;
        var shadowEnv = Environment.GetEnvironmentVariable("CC_SHADOW_SAMPLE_ENABLED");
        if (!string.IsNullOrWhiteSpace(shadowEnv) && bool.TryParse(shadowEnv, out var shadowParsed))
        {
            enableShadow = shadowParsed;
        }

        var sampleRate = 0.01;
        var rateEnv = Environment.GetEnvironmentVariable("CC_SHADOW_SAMPLE_RATE");
        if (!string.IsNullOrWhiteSpace(rateEnv) && double.TryParse(rateEnv, out var rateParsed))
        {
            sampleRate = Math.Clamp(rateParsed, 0.0, 1.0);
        }

        return new CutoverConfiguration
        {
            CutoverPercentage = percentage,
            EnableSampledShadow = enableShadow,
            ShadowSampleRate = sampleRate
        };
    }

    /// <summary>将配置应用到 CutoverController。</summary>
    public void ApplyTo(CutoverController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        controller.SetCutoverPercentage(CutoverPercentage);
    }
}

// ---------------------------------------------------------------------------
// DecisionExperimentPlaneIntegration — 长期实验平面集成
// ---------------------------------------------------------------------------

/// <summary>
/// DecisionExperimentPlane 长期保留集成入口。
/// </summary>
/// <remarks>
/// B-5 后 V2 已权威，但 DecisionExperimentPlane 仍作为长期基础设施保留：
///   1. Sampled shadow：按采样率执行 Legacy + parity 对比（监控 V2 漂移）
///   2. Replay fixture 存储：历史 parity 报告供回归分析
///   3. CI 验收 hook：ShadowGateEvaluator 输出 CutoverReadinessAssessment
///
/// 与 B-2~B-4 的区别：
///   - B-2~B-4：Shadow 是切换前的验收手段
///   - B-5：Shadow 是切换后的持续监控手段（detect V2 drift over time）
/// </remarks>
public sealed class DecisionExperimentPlaneIntegration : IAsyncDisposable
{
    private readonly DecisionExperimentPlane _experimentPlane;
    private readonly ShadowGateEvaluator _gateEvaluator;
    private readonly CutoverConfiguration _configuration;
    private readonly IExperimentRecorder _recorder;
    // 纯决策重放内核。可选注入；为 null 时 DecisionReplay/ExpertReplay 抛异常。
    private readonly IContextDecisionEngine? _engine;
    // 候选合并器。用于 ExpertReplay 合并 Provider 输出快照（冲突检测，非后写覆盖）。
    private readonly ICanonicalCandidateMerger _merger;

    // 异步非阻塞队列。写路径（RecordFixture / RecordShadowReport /
    // ClearHistory）只入队即返回，后台 consumer 串行消费调用 _recorder。
    // 读路径（FixtureHistory / EvaluateHistoricalFixtures）先 FlushAsync 再读取，
    // 保证调用方看到入队事件已落盘。生产环境若需纯异步读取可调用 FlushAsync 后
    // 直接访问 IExperimentRecorder。
    //
    // bounded channel（容量 1024，Wait 模式）。
    // 旧实现使用 unbounded channel，在 Postgres/磁盘持续故障导致 consumer 长期阻塞时
    // 会让内存无限增长；bounded 保住内存上限。
    // FullMode 从 DropOldest 改为 Wait。DropOldest 模式下 TryWrite 在
    // 队列满时仍返回 true（内部丢弃最旧事件），导致 DroppedCount 永远不递增，
    // 指标不可信。Wait 模式下 TryWrite 在队列满时返回 false，Enqueue 据此递增
    // _droppedCount，使 DropCount 指标准确反映因队列满而丢弃的事件数。
    private readonly Channel<ExperimentEvent> _queue;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _shutdownCts;

    // 可靠性计数器 + dead-letter。
    // _droppedCount：入队失败（writer 已完成 / bounded 满 DropOldest 时 TryWrite 仍返回 true，
    //   仅在 writer 完成后 TryWrite 返回 false 时累加）。
    // _failedWriteCount：重试 3 次后仍写入失败的 Record 事件数。
    // _processedCount：成功落盘的 Record 事件数。
    private int _droppedCount;
    private int _failedWriteCount;
    private int _processedCount;
    private readonly List<ExperimentEvent> _deadLetterQueue = new();
    private readonly object _deadLetterLock = new();

    // sequence-aware flush。
    // _sequenceCounter：单调递增的事件序号（Enqueue 时 Interlocked.Increment 分配）。
    //   让 FlushResult 能返回 AcceptedSequence（sentinel 序号）+ LastPersistedSequence（最后成功落盘序号），
    //   调用方可据此判断 flush 是否覆盖了目标序号之前所有事件。
    // _lastPersistedSequence：consumer 最后一次成功 RecordAsync 的事件序号；
    //   失败/丢弃事件不更新此值，使 LastPersistedSequence 准确反映"已落盘到哪个序号"。
    private long _sequenceCounter;
    private long _lastPersistedSequence;

    /// <summary>构造长期实验平面集成。</summary>
    /// <param name="engine">纯决策 Engine。可选；注入后启用 DecisionReplay/ExpertReplay。</param>
    /// <param name="merger">候选合并器。可选；未注入时使用 <see cref="DefaultCanonicalCandidateMerger"/>。
    /// ExpertReplay 使用此合并器合并 Provider 输出快照（冲突检测，非后写覆盖）。</param>
    public DecisionExperimentPlaneIntegration(
        DecisionExperimentPlane experimentPlane,
        ShadowGateEvaluator gateEvaluator,
        CutoverConfiguration configuration,
        IExperimentRecorder? recorder = null,
        IContextDecisionEngine? engine = null,
        ICanonicalCandidateMerger? merger = null)
    {
        _experimentPlane = experimentPlane ?? throw new ArgumentNullException(nameof(experimentPlane));
        _gateEvaluator = gateEvaluator ?? throw new ArgumentNullException(nameof(gateEvaluator));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        // 持久化委托给 IExperimentRecorder；未注入时回退到 in-memory 默认实现。
        _recorder = recorder ?? new InMemoryExperimentRecorder();
        _engine = engine;
        // 未注入合并器时回退到默认实现（含冲突检测 + SourceRefs 合并）。
        _merger = merger ?? new DefaultCanonicalCandidateMerger();

        // bounded channel。容量 1024 平衡内存上限与突发写入；
        // Wait 模式下队列满时 TryWrite 返回 false，Enqueue 据此递增 _droppedCount，
        // 使 DropCount 指标准确（DropOldest 模式下 TryWrite 永远返回 true，丢弃不可观测）；
        // SingleReader=true 由单 consumer 保证；SingleWriter=false 允许多线程入队。
        _queue = Channel.CreateBounded<ExperimentEvent>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _shutdownCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeAsync(_shutdownCts.Token));
    }

    /// <summary>当前队列深度（未消费事件数）。</summary>
    public int QueueDepth => _queue.Reader.Count;

    /// <summary>累计丢弃事件数（队列满时 TryWrite 返回 false，或 writer 完成后入队失败）。</summary>
    public int DroppedCount => _droppedCount;

    /// <summary>累计写入失败事件数（重试 3 次仍未成功，已进 dead-letter）。</summary>
    public int FailedWriteCount => _failedWriteCount;

    /// <summary>累计成功落盘 Record 事件数。</summary>
    public int ProcessedCount => _processedCount;

    /// <summary>dead-letter 队列当前长度（重试失败后保留的事件数）。</summary>
    public int DeadLetterCount
    {
        get
        {
            lock (_deadLetterLock)
            {
                return _deadLetterQueue.Count;
            }
        }
    }

    /// <summary>查询 dead-letter 队列（重试失败后保留的事件快照）。</summary>
    /// <returns>dead-letter 队列的线程安全快照（副本）。</returns>
    /// <remarks>
    /// 返回副本避免调用方持锁访问；调用方可检视失败事件的 Fixture + Sequence 用于诊断。
    /// </remarks>
    public IReadOnlyList<ExperimentEvent> GetDeadLetterQueue()
    {
        lock (_deadLetterLock)
        {
            return _deadLetterQueue.ToList();
        }
    }

    /// <summary>获取实验平面指标快照（供 ControlRoom / 诊断工具消费）。</summary>
    /// <returns>包含 QueueDepth / DroppedCount / FailedWriteCount / DeadLetterCount 等指标的不可变快照。</returns>
    /// <remarks>
    /// 一次调用捕获全部关键指标，避免 ControlRoom 多次读取时计数器不一致。
    /// ControlRoom 可定期调用此方法渲染指标页面，监控实验平面健康度。
    /// </remarks>
    public ExperimentPlaneMetricsSnapshot GetMetricsSnapshot()
    {
        return new ExperimentPlaneMetricsSnapshot
        {
            QueueDepth = _queue.Reader.Count,
            DroppedCount = _droppedCount,
            FailedWriteCount = _failedWriteCount,
            ProcessedCount = _processedCount,
            DeadLetterCount = DeadLetterCount,
            LastPersistedSequence = Interlocked.Read(ref _lastPersistedSequence),
            LastAcceptedSequence = Interlocked.Read(ref _sequenceCounter)
        };
    }

    /// <summary>历史 replay fixture 集合（线程安全快照）。</summary>
    /// <remarks>
    /// 写路径已异步化（Channel + 后台 consumer）。此 getter 为向后兼容
    /// 保留 sync-over-async：先 FlushAsync 排空队列，再同步读取 recorder 历史。
    /// 警告：sync-over-async 在高并发下可能导致线程池饥饿，生产环境应优先使用
    /// <see cref="GetFixtureHistoryAsync"/>。
    /// </remarks>
    [Obsolete("Use GetFixtureHistoryAsync instead.")]
    public IReadOnlyList<ReplayFixture> FixtureHistory
    {
        get
        {
            // 警告：保留 sync-over-async 仅为向后兼容，高并发场景请使用 GetFixtureHistoryAsync。
            FlushAsync().GetAwaiter().GetResult();
            return _recorder.GetHistoryAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 异步读取历史 fixture。先 flush 写队列，再异步读取 recorder。
    /// </summary>
    public async ValueTask<IReadOnlyList<ReplayFixture>> GetFixtureHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return await _recorder.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 记录一次 parity 对比，存为 replay fixture。
    /// 即使 V2 已权威，仍按 ShadowSampleRate 抽样执行 Legacy + parity 对比。
    /// </summary>
    public bool ShouldRunSampledShadow(string requestId)
    {
        if (!_configuration.EnableSampledShadow || _configuration.ShadowSampleRate <= 0)
            return false;
        if (_configuration.ShadowSampleRate >= 1.0)
            return true;

        // 使用与 CutoverController 相同的 FNV-1a 哈希策略
        var hash = StableHash(requestId);
        return (hash % 10000) < (_configuration.ShadowSampleRate * 10000);
    }

    /// <summary>记录 parity fixture（仅聚合标量；P0-9 前的旧入口）。</summary>
    /// <remarks>
    /// 非阻塞入队，立即返回。后台 consumer 异步调用 _recorder.RecordAsync。
    /// 调用方若需保证落盘可见，应随后调用 <see cref="FlushAsync"/> 或使用读取入口
    /// （<see cref="FixtureHistory"/> / <see cref="EvaluateHistoricalFixtures"/> 会自动 flush）。
    /// </remarks>
    public void RecordFixture(ParityReport report, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(report);
        var fixture = ReplayFixture.FromReport(report, fixtureId, purpose, notes);
        Enqueue(new ExperimentEvent.Record(fixture));
    }

    /// <summary>
    /// 从完整 Retrieval shadow 报告构建并持久化 replay fixture。
    /// 携带 WorkingSet + V2Result，使 fixture 可离线重放。
    /// </summary>
    /// <remarks>
    /// 非阻塞入队，立即返回（详见 <see cref="RecordFixture"/>）。
    /// </remarks>
    public void RecordShadowReport(RetrievalShadowReport shadowReport, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(shadowReport);
        // 优先使用 FromExecution 携带完整 Execution 数据（Policy / ProviderOutputs）；
        // Execution 为 null 时回退到 FromShadowReport（使用 shadowReport 自身的 WorkingSet + V2Result）
        var fixture = shadowReport.Execution is not null
            ? ReplayFixture.FromExecution(shadowReport.Parity, shadowReport.Execution, fixtureId, purpose, notes)
            : ReplayFixture.FromShadowReport(shadowReport.Parity, shadowReport.WorkingSet, shadowReport.V2Result, fixtureId, purpose, notes);
        Enqueue(new ExperimentEvent.Record(fixture));
    }

    /// <summary>
    /// 从完整 Package shadow 报告构建并持久化 replay fixture。
    /// 携带 WorkingSet + V2Result，使 fixture 可离线重放。
    /// </summary>
    /// <remarks>
    /// 非阻塞入队，立即返回（详见 <see cref="RecordFixture"/>）。
    /// </remarks>
    public void RecordShadowReport(PackageShadowReport shadowReport, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(shadowReport);
        // 优先使用 FromExecution 携带完整 Execution 数据（Policy / ProviderOutputs）；
        // Execution 为 null 时回退到 FromShadowReport（使用 shadowReport 自身的 WorkingSet + V2Result）
        var fixture = shadowReport.Execution is not null
            ? ReplayFixture.FromExecution(shadowReport.Parity, shadowReport.Execution, fixtureId, purpose, notes)
            : ReplayFixture.FromShadowReport(shadowReport.Parity, shadowReport.WorkingSet, shadowReport.V2Result, fixtureId, purpose, notes);
        Enqueue(new ExperimentEvent.Record(fixture));
    }

    /// <summary>评估历史 fixture，产出 cutover 就绪判定（CI 验收 hook）。</summary>
    /// <remarks>
    /// 先 FlushAsync 排空写队列，再同步读取 recorder 历史。
    /// 警告：保留 sync-over-async 仅为向后兼容 CI 入口，高并发场景请使用
    /// <see cref="EvaluateHistoricalFixturesAsync"/>。
    /// </remarks>
    [Obsolete("Use EvaluateHistoricalFixturesAsync instead.")]
    public CutoverReadinessAssessment EvaluateHistoricalFixtures()
    {
        // 警告：保留 sync-over-async 仅为向后兼容，高并发场景请使用 EvaluateHistoricalFixturesAsync。
        FlushAsync().GetAwaiter().GetResult();
        var fixtures = _recorder.GetHistoryAsync().GetAwaiter().GetResult();
        var reports = fixtures
            .Select(f => new ParityReport(
                LegacySelectedCount: f.LegacySelectedCount,
                V2SelectedCount: f.V2SelectedCount,
                CommonSelectedCount: f.CommonSelectedCount,
                OnlyInLegacyCount: f.OnlyInLegacyCount,
                OnlyInV2Count: f.OnlyInV2Count,
                JaccardIndex: f.JaccardIndex,
                ParityLevel: f.ParityLevel,
                LegacyTokenTotal: f.LegacyTokenTotal,
                V2TokenTotal: f.V2TokenTotal,
                WorkingSetCandidateCount: f.WorkingSetCandidateCount))
            .ToList();
        return _gateEvaluator.EvaluateBatch(reports);
    }

    /// <summary>
    /// 异步评估历史 fixture，产出 cutover 就绪判定。
    /// </summary>
    public async ValueTask<CutoverReadinessAssessment> EvaluateHistoricalFixturesAsync(
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var fixtures = await _recorder.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        var reports = fixtures
            .Select(f => new ParityReport(
                LegacySelectedCount: f.LegacySelectedCount,
                V2SelectedCount: f.V2SelectedCount,
                CommonSelectedCount: f.CommonSelectedCount,
                OnlyInLegacyCount: f.OnlyInLegacyCount,
                OnlyInV2Count: f.OnlyInV2Count,
                JaccardIndex: f.JaccardIndex,
                ParityLevel: f.ParityLevel,
                LegacyTokenTotal: f.LegacyTokenTotal,
                V2TokenTotal: f.V2TokenTotal,
                WorkingSetCandidateCount: f.WorkingSetCandidateCount))
            .ToList();
        return _gateEvaluator.EvaluateBatch(reports);
    }

    /// <summary>清除历史 fixture（用于测试或重置）。</summary>
    /// <remarks>
    /// 非阻塞入队 Clear 事件。调用方若需立即生效应调用
    /// <see cref="FlushAsync"/> 或随后读取 <see cref="FixtureHistory"/>（自动 flush）。
    /// </remarks>
    public void ClearHistory()
    {
        Enqueue(new ExperimentEvent.Clear());
    }

    /// <summary>
    /// 纯决策重放。不访问 Store，不调用 Provider/Router。
    /// 直接从 stored WorkingSet + stored EffectivePolicySnapshot 进入 Engine。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LiveReexecutionComparisonAsync"/> 的差异：
    ///   - DecisionReplay 不重新解析 Policy、不调用 Router、不执行 Providers、不访问 Store；
    ///   - 输入完全来自 fixture 的 StoredWorkingSet + StoredPolicySnapshot；
    ///   - 直接调用 <see cref="IContextDecisionEngine.DecideAsync"/>，验证纯决策内核的可重现性。
    /// Engine 未注入（构造时 engine=null）时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    /// <param name="fixture">要重放的 fixture（必须携带 StoredWorkingSet + StoredPolicySnapshot）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Engine 产出的决策结果。</returns>
    public async ValueTask<ContextDecisionResult> DecisionReplayAsync(
        ReplayFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (_engine is null)
            throw new InvalidOperationException(
                "DecisionReplay requires IContextDecisionEngine to be configured at construction.");
        if (fixture.StoredPolicySnapshot is null)
            throw new InvalidOperationException("DecisionReplay requires fixture.StoredPolicySnapshot.");
        if (fixture.StoredWorkingSet is null)
            throw new InvalidOperationException("DecisionReplay requires fixture.StoredWorkingSet.");

        var engineRequest = BuildReplayEngineRequest(fixture, fixture.StoredWorkingSet, "replay");
        return await _engine.DecideAsync(engineRequest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Expert 重放。使用已存 Provider 输出快照，跳过 Provider 执行。
    /// </summary>
    /// <remarks>
    /// 流程：
    ///   1. 从 fixture.StoredProviderOutputs 合并所有 Provider 的 Envelopes + Materials。
    ///      使用 <see cref="ICanonicalCandidateMerger"/> 执行正式合并逻辑
    ///      （冲突检测：相同 CanonicalCandidateKey + 不同 content hash → 抛异常；
    ///       相同 key + 相同 content hash → 合并 SourceRefs），而非后写覆盖。
    ///   2. 用合并后的候选集合 + StoredPolicySnapshot 构建 Engine 请求。
    ///   3. 调用 <see cref="IContextDecisionEngine.DecideAsync"/>，跳过 Provider/Router/Store。
    /// 与 <see cref="DecisionReplayAsync"/> 的差异：DecisionReplay 直接用 StoredWorkingSet；
    /// ExpertReplay 从多个 Provider 快照重建 WorkingSet（验证 Provider 输出快照的可重放性）。
    /// </remarks>
    /// <param name="fixture">要重放的 fixture（必须携带 StoredProviderOutputs + StoredPolicySnapshot）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Engine 产出的决策结果。</returns>
    public async ValueTask<ContextDecisionResult> ExpertReplayAsync(
        ReplayFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (_engine is null)
            throw new InvalidOperationException(
                "ExpertReplay requires IContextDecisionEngine to be configured at construction.");
        if (fixture.StoredPolicySnapshot is null)
            throw new InvalidOperationException("ExpertReplay requires fixture.StoredPolicySnapshot.");
        if (fixture.StoredProviderOutputs is null || fixture.StoredProviderOutputs.Count == 0)
            throw new InvalidOperationException("ExpertReplay requires fixture.StoredProviderOutputs (non-empty).");

        // 使用正式合并逻辑（ICanonicalCandidateMerger），而非后写覆盖。
        // 将 ProviderOutputSnapshot 转换为 ExpertExecutionResult，委托给合并器执行：
        //   - 相同 CanonicalCandidateKey + 相同 content hash → 合并 SourceRefs（union）；
        //   - 相同 key + 不同 content hash → 抛 InvalidOperationException（fail-fast，检测冲突）；
        //   - 不同 EntityVersion → CanonicalKey 自然不同，两个 Material 都保留。
        // 这与生产路径（DefaultContextDecisionRuntime 调用 _canonicalMerger.Merge）语义一致，
        // 确保 replay 合并结果与生产一致，不因后写覆盖掩盖冲突。
        var expertOutputs = fixture.StoredProviderOutputs
            .Select(snapshot => new ExpertExecutionResult(snapshot.Envelopes, snapshot.Materials))
            .ToList();
        var workingSet = _merger.Merge(expertOutputs);

        var engineRequest = BuildReplayEngineRequest(fixture, workingSet, "expert-replay");
        return await _engine.DecideAsync(engineRequest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构建 Engine 重放请求（DecisionReplay / ExpertReplay 共用）。
    /// PolicySnapshot 直接挂到请求；TokenBudget/TopK 优先取 stored V2Result，回退到 PolicySnapshot 默认值。
    /// </summary>
    private ContextDecisionRequest BuildReplayEngineRequest(
        ReplayFixture fixture,
        CandidateWorkingSet workingSet,
        string requestIdPrefix)
    {
        var snapshot = fixture.StoredPolicySnapshot!;
        var firstEnvelope = workingSet.Envelopes.FirstOrDefault();
        var scope = firstEnvelope is not null
            ? new ContextDecisionScope(firstEnvelope.WorkspaceId, firstEnvelope.CollectionId)
            : snapshot.ResolutionScope;

        var tokenBudget = fixture.V2Result?.Outcome.TokenBudget > 0
            ? fixture.V2Result.Outcome.TokenBudget
            : snapshot.Budget.DefaultTokenBudget;
        var topK = fixture.V2Result is { Outcome.SelectedCount: > 0 }
            ? fixture.V2Result.Outcome.SelectedCount
            : snapshot.Budget.DefaultTopK;
        var purpose = fixture.V2Result?.Purpose ?? ContextDecisionPurpose.Retrieval;

        return new ContextDecisionRequest
        {
            RequestId = $"{requestIdPrefix}-{fixture.FixtureId}",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = scope.WorkspaceId,
            CollectionId = scope.CollectionId,
            Candidates = workingSet.Envelopes,
            TokenBudget = tokenBudget,
            TopK = topK,
            PolicySnapshot = snapshot,
            AllocationContext = new AllocationContext
            {
                Purpose = purpose,
                Budget = snapshot.Budget,
                MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
            }
        };
    }

    /// <summary>
    /// 阶段 E / live re-execution 重放。
    /// 从历史 fixture 重新执行 V2 决策（重新解析 Policy、调用 Router、执行 Providers、访问 Store），
    /// 验证决策可重现性。
    /// </summary>
    /// <remarks>
    /// 原 <c>ReplayFixtureAsync</c> 重命名为 <c>LiveReexecutionComparisonAsync</c>，
    /// 以区分 <see cref="DecisionReplayAsync"/>（纯 Engine replay）与 <see cref="ExpertReplayAsync"/>（Provider 快照 replay）。
    /// 流程：
    ///   1. 按 fixtureId 从 recorder 加载 ReplayFixture。
    ///   2. 若 fixture 携带 WorkingSet + V2Result（P0-9 增强字段），使用 WorkingSet 作为 SeedCandidates
    ///      重新执行 V2 决策。
    ///   3. 用 DecisionExperimentPlane.Compare 对比存储的 V2Result 与重放结果，产出 ParityReport。
    ///   4. fixture 不含完整重放数据（WorkingSet/V2Result 为 null）时返回 null，调用方可降级到
    ///      EvaluateHistoricalFixtures 的标量路径。
    /// </remarks>
    /// <param name="fixtureId">要重放的 fixture ID。</param>
    /// <param name="v2Runtime">V2 pure Runtime，用于重放决策。null 时返回 null（仅离线扫描 fixture 元数据）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重放报告（含 stored V2Result vs replayed V2Result 的 ParityReport）；fixture 不存在或不完整时返回 null。</returns>
    public async ValueTask<FixtureReplayReport?> LiveReexecutionComparisonAsync(
        string fixtureId,
        IContextDecisionRuntime? v2Runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);

        // 先 flush 写队列，保证重放读到最新落盘的 fixture。
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var fixtures = await _recorder.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        var fixture = fixtures.FirstOrDefault(f => string.Equals(f.FixtureId, fixtureId, StringComparison.Ordinal));
        if (fixture is null)
        {
            return null;
        }

        // 缺少完整重放数据：无法离线 replay，调用方应降级到标量路径
        if (fixture.WorkingSet is null || fixture.V2Result is null)
        {
            return null;
        }

        // v2Runtime 为 null 时无法重放，返回 fixture 元数据但 Parity 为 null
        if (v2Runtime is null)
        {
            return new FixtureReplayReport(
                FixtureId: fixture.FixtureId,
                RecordedAt: fixture.RecordedAt,
                Purpose: fixture.Purpose,
                StoredParity: new ParityReport(
                    LegacySelectedCount: fixture.LegacySelectedCount,
                    V2SelectedCount: fixture.V2SelectedCount,
                    CommonSelectedCount: fixture.CommonSelectedCount,
                    OnlyInLegacyCount: fixture.OnlyInLegacyCount,
                    OnlyInV2Count: fixture.OnlyInV2Count,
                    JaccardIndex: fixture.JaccardIndex,
                    ParityLevel: fixture.ParityLevel,
                    LegacyTokenTotal: fixture.LegacyTokenTotal,
                    V2TokenTotal: fixture.V2TokenTotal,
                    WorkingSetCandidateCount: fixture.WorkingSetCandidateCount),
                ReplayParity: null,
                ReplaySucceeded: false,
                Notes: "v2Runtime not provided — cannot replay decision");
        }

        // 重建 V2 RuntimeRequest：用 fixture 的 WorkingSet 作为 SeedCandidates
        // 注意：ContextDecisionResult 不携带 Scope/QueryText（这些是 Request 字段），
        // replay 从 WorkingSet 的首个 envelope 推导 Scope（replay 场景仅需决策可重现，不需原始查询文本）。
        var firstEnvelope = fixture.WorkingSet.Envelopes.FirstOrDefault();
        var replayScope = new ContextDecisionScope(
            firstEnvelope.WorkspaceId,
            firstEnvelope.CollectionId);
        var replayRequest = new ContextDecisionRuntimeRequest
        {
            RequestId = $"replay:{fixture.FixtureId}",
            Purpose = fixture.V2Result.Purpose,
            Scope = replayScope,
            QueryText = null, // replay 不携带原始查询文本；决策可重现性不依赖查询文本
            TokenBudget = fixture.V2Result.Outcome.TokenBudget,
            TopK = fixture.V2Result.Outcome.SelectedCount > 0
                ? fixture.V2Result.Outcome.SelectedCount
                : 10,
            SeedCandidates = fixture.WorkingSet.Envelopes
        };

        ContextDecisionResult replayedResult;
        try
        {
            replayedResult = await v2Runtime.ExecuteAsync(replayRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FixtureReplayReport(
                FixtureId: fixture.FixtureId,
                RecordedAt: fixture.RecordedAt,
                Purpose: fixture.Purpose,
                StoredParity: new ParityReport(
                    LegacySelectedCount: fixture.LegacySelectedCount,
                    V2SelectedCount: fixture.V2SelectedCount,
                    CommonSelectedCount: fixture.CommonSelectedCount,
                    OnlyInLegacyCount: fixture.OnlyInLegacyCount,
                    OnlyInV2Count: fixture.OnlyInV2Count,
                    JaccardIndex: fixture.JaccardIndex,
                    ParityLevel: fixture.ParityLevel,
                    LegacyTokenTotal: fixture.LegacyTokenTotal,
                    V2TokenTotal: fixture.V2TokenTotal,
                    WorkingSetCandidateCount: fixture.WorkingSetCandidateCount),
                ReplayParity: null,
                ReplaySucceeded: false,
                Notes: $"Replay failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 用 DecisionExperimentPlane.Compare 对比存储 V2Result 与重放结果
        // 注意：这里比较的是 stored-V2 vs replayed-V2，验证 V2 决策的可重现性（不是 Legacy vs V2 parity）。
        // 如果 Runtime 是确定性的（无模型随机性、无时间敏感逻辑），replayParity 应为 Hard（Jaccard=1.0）。
        var replayParity = _experimentPlane.Compare(fixture.V2Result, replayedResult, fixture.WorkingSet);

        return new FixtureReplayReport(
            FixtureId: fixture.FixtureId,
            RecordedAt: fixture.RecordedAt,
            Purpose: fixture.Purpose,
            StoredParity: new ParityReport(
                LegacySelectedCount: fixture.LegacySelectedCount,
                V2SelectedCount: fixture.V2SelectedCount,
                CommonSelectedCount: fixture.CommonSelectedCount,
                OnlyInLegacyCount: fixture.OnlyInLegacyCount,
                OnlyInV2Count: fixture.OnlyInV2Count,
                JaccardIndex: fixture.JaccardIndex,
                ParityLevel: fixture.ParityLevel,
                LegacyTokenTotal: fixture.LegacyTokenTotal,
                V2TokenTotal: fixture.V2TokenTotal,
                WorkingSetCandidateCount: fixture.WorkingSetCandidateCount),
            ReplayParity: replayParity,
            ReplaySucceeded: true,
            Notes: string.Empty);
    }

    /// <summary>
    /// [Obsolete] 别名，转发到 <see cref="LiveReexecutionComparisonAsync"/>。
    /// </summary>
    /// <remarks>
    /// 旧入口保留以兼容既有调用方；新代码应按重放语义选择：
    /// <see cref="DecisionReplayAsync"/>（纯 Engine replay）、
    /// <see cref="ExpertReplayAsync"/>（Provider 快照 replay）、
    /// <see cref="LiveReexecutionComparisonAsync"/>（live re-execution，原行为）。
    /// </remarks>
    [Obsolete("Use LiveReexecutionComparisonAsync/DecisionReplayAsync/ExpertReplayAsync")]
    public ValueTask<FixtureReplayReport?> ReplayFixtureAsync(
        string fixtureId,
        IContextDecisionRuntime? v2Runtime,
        CancellationToken cancellationToken = default)
        => LiveReexecutionComparisonAsync(fixtureId, v2Runtime, cancellationToken);

    private static uint StableHash(string value)
    {
        uint hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    // -----------------------------------------------------------------------
    // 异步队列基础设施
    // -----------------------------------------------------------------------

    /// <summary>
    /// 非阻塞入队。
    /// </summary>
    /// <remarks>
    /// 入队前为事件分配单调递增的 Sequence（Interlocked.Increment），
    /// 让 FlushResult 能返回 AcceptedSequence + LastPersistedSequence，实现 sequence-aware flush。
    /// bounded channel（Wait 模式）下：TryWrite 在以下情况返回 false：
    ///   1. 队列已满（consumer 跟不上写入速率）— R28-B.7 P1-6 修复：DropOldest 模式下
    ///      TryWrite 永远返回 true（内部静默丢弃最旧事件），DroppedCount 不可信；
    ///      Wait 模式下 TryWrite 返回 false，此处据此递增 _droppedCount，使指标准确；
    ///   2. writer 已完成（DisposeAsync 后）— shutdown 后入队失败，计数并丢弃。
    /// 两种情况均累加 _droppedCount 并静默丢弃，不阻塞调用方，不抛异常
    /// （避免 shutdown 后或队列满时调用 RecordFixture 导致进程崩溃）。
    /// </remarks>
    private void Enqueue(ExperimentEvent evt)
    {
        // 分配单调递增序号，使 FlushResult 能报告 AcceptedSequence / LastPersistedSequence。
        var sequenced = evt with { Sequence = Interlocked.Increment(ref _sequenceCounter) };
        if (!_queue.Writer.TryWrite(sequenced))
        {
            // 队列满或 writer 已完成：计数并丢弃，不阻塞调用方。
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <summary>
    /// 等待队列中此前所有事件被 consumer 处理完成，
    /// 并返回累计计数快照 + sequence 信息。
    /// </summary>
    /// <remarks>
    /// 通过入队一个 sentinel（TaskCompletionSource）并在 consumer 处理到它时
    /// 完成 TCS，实现"排空到此处"的语义。<b>不</b>调用 TryComplete（保留 writer 以便后续写入）；
    /// TryComplete 仅在 <see cref="DisposeAsync"/> 中调用。
    /// FlushResult 携带 AcceptedSequence（sentinel 序号）+ LastPersistedSequence
    /// （最后成功落盘序号），调用方可据此判断 flush 是否覆盖目标序号之前的所有事件，
    /// 以及是否有事件因写入失败而未落盘（LastPersistedSequence < AcceptedSequence - 1 时存在 gap）。
    /// </remarks>
    public async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // sentinel 也分配 Sequence，AcceptedSequence 即 sentinel 序号。
        var acceptedSequence = Interlocked.Increment(ref _sequenceCounter);
        var flushEvent = new ExperimentEvent.Flush(sentinel) with { Sequence = acceptedSequence };
        if (!_queue.Writer.TryWrite(flushEvent))
        {
            // writer 已完成（shutdown 后）：sentinel 无法入队，直接返回当前计数快照。
            return new FlushResult(
                _processedCount,
                _failedWriteCount,
                _droppedCount,
                acceptedSequence,
                Interlocked.Read(ref _lastPersistedSequence));
        }
        await sentinel.Task.ConfigureAwait(false);
        return new FlushResult(
            _processedCount,
            _failedWriteCount,
            _droppedCount,
            acceptedSequence,
            Interlocked.Read(ref _lastPersistedSequence));
    }

    /// <summary>
    /// 后台 consumer。串行处理队列中的事件，调用 _recorder。
    /// </summary>
    /// <remarks>
    /// Record 事件写入失败时重试至 3 次尝试（退避 100ms/200ms），
    /// 仍失败则放入 dead-letter list 并累加 _failedWriteCount；不再静默吞掉。
    /// Clear / Flush 事件保持单次尝试（语义上 Clear 失败不影响后续 Record；Flush 仅完成 sentinel）。
    /// 重试期间若收到取消信号（shutdown）则向上抛 OperationCanceledException 退出。
    /// </remarks>
    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    switch (evt)
                    {
                        case ExperimentEvent.Record rec:
                            await ProcessRecordWithRetryAsync(rec, cancellationToken).ConfigureAwait(false);
                            break;
                        case ExperimentEvent.Clear:
                            await _recorder.ClearAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        case ExperimentEvent.Flush flush:
                            flush.Completion.TrySetResult();
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // shutdown：不再处理后续事件
                    return;
                }
                catch (Exception)
                {
                    // 重试已穷尽后的兜底：单个事件失败不终止 consumer，后续事件继续处理。
                    // （重试 + dead-letter 已在 ProcessRecordWithRetryAsync 内完成。）
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown：正常退出
        }
    }

    /// <summary>
    /// Record 事件重试 + dead-letter。
    /// 写入失败重试 3 次；仍失败则放入 dead-letter list 并累加 _failedWriteCount。
    /// 成功则累加 _processedCount 并更新 _lastPersistedSequence（sequence-aware flush）。
    /// </summary>
    private async Task ProcessRecordWithRetryAsync(ExperimentEvent.Record rec, CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (retryCount <= 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _recorder.RecordAsync(rec.Fixture, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
                // 记录最后成功落盘的事件序号，让 FlushResult.LastPersistedSequence 准确。
                // 使用 Interlocked.Max 保证并发安全（虽然当前 consumer 是单线程，但防御性写法）。
                InterlockedMax(ref _lastPersistedSequence, rec.Sequence);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                retryCount++;
                if (retryCount >= 3)
                {
                    // 重试穷尽：放入 dead-letter，累加失败计数，不再重试。
                    lock (_deadLetterLock)
                    {
                        _deadLetterQueue.Add(rec);
                    }
                    Interlocked.Increment(ref _failedWriteCount);
                    return;
                }
                // 退避：100ms / 200ms（第 3 次重试前等 200ms）。
                try
                {
                    await Task.Delay(100 * retryCount, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>线程安全的 Interlocked.Max（long）。</summary>
    private static void InterlockedMax(ref long target, long value)
    {
        long initial;
        do
        {
            initial = Interlocked.CompareExchange(ref target, 0, 0);
            if (value <= initial) return;
        }
        while (Interlocked.CompareExchange(ref target, value, initial) != initial);
    }

    /// <summary>
    /// 优雅停用。
    /// </summary>
    /// <remarks>
    /// 先 TryComplete writer（让 consumer 排空剩余事件再退出），
    /// 再以 5 秒 drain 超时等待 consumer。超时则强制取消 _shutdownCts 释放线程，
    /// 未处理事件丢弃（避免 recorder 持续故障时 DisposeAsync 永久阻塞）。
    /// 记录 drain 结果到 <see cref="DrainResult"/>，
    /// 让 ControlRoom / 诊断工具能检视 shutdown 时已 drain / 未 drain 的事件数。
    /// 调用后不应再调用 RecordFixture / RecordShadowReport / ClearHistory（TryWrite 会返回 false 并计入 DroppedCount）。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        // 记录 drain 前已成功落盘的计数，作为 DrainedCount 基线。
        var drainedCount = _processedCount;
        var undrainedCount = 0;

        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _consumerTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
            // consumer 正常退出：drainedCount 为 drain 后的累计落盘数。
            drainedCount = _processedCount;
        }
        catch (OperationCanceledException)
        {
            // drain 超时：强制取消 consumer 以释放线程，未处理事件丢弃。
            undrainedCount = _queue.Reader.Count;
            _shutdownCts.Cancel();
            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 预期退出
            }
            catch (Exception)
            {
                // 防御：consumer 退出时的异常忽略
            }
        }
        catch (Exception)
        {
            // consumer 内部已捕获所有非取消异常，此处仅为防御
        }
        _shutdownCts.Dispose();

        // 记录 drain 结果，供 ControlRoom / 诊断工具检视。
        DrainResult = new DisposeDrainResult(drainedCount, undrainedCount);
    }

    /// <summary>DisposeAsync 的 drain 结果（null 表示尚未 Dispose）。</summary>
    public DisposeDrainResult? DrainResult { get; private set; }

    /// <summary>
    /// 实验事件（写路径入队条目）。
    /// </summary>
    /// <remarks>
    /// 基类携带 Sequence 字段（init 属性），由 Enqueue 在入队前分配。
    /// 使用 init 属性而非构造参数，避免改变派生 record 的构造函数签名（向后兼容）。
    /// Final：改为 public 嵌套类型，让 <see cref="GetDeadLetterQueue"/> 能返回
    /// <c>IReadOnlyList&lt;ExperimentEvent&gt;</c>，供 ControlRoom / 诊断工具检视失败事件。
    /// </remarks>
    public abstract record ExperimentEvent
    {
        /// <summary>事件序号（Enqueue 时单调递增分配）。</summary>
        public long Sequence { get; init; }

        /// <summary>记录一条 replay fixture。</summary>
        public sealed record Record(ReplayFixture Fixture) : ExperimentEvent;

        /// <summary>清除全部历史 fixture。</summary>
        public sealed record Clear : ExperimentEvent;

        /// <summary>Flush sentinel：consumer 处理到此事件时完成 TCS，标记此前所有事件已落盘。</summary>
        public sealed record Flush(TaskCompletionSource Completion) : ExperimentEvent;
    }
}

/// <summary>
/// fixture 重放报告。
/// </summary>
/// <param name="FixtureId">重放的 fixture ID。</param>
/// <param name="RecordedAt">fixture 原始记录时间。</param>
/// <param name="Purpose">fixture 用途标签。</param>
/// <param name="StoredParity">fixture 存储时的 Legacy vs V2 parity（标量重建）。</param>
/// <param name="ReplayParity">重放结果与存储 V2Result 的 parity；null 表示重放未执行或失败。</param>
/// <param name="ReplaySucceeded">重放是否成功执行（未抛异常）。</param>
/// <param name="Notes">重放备注（失败原因等）。</param>
public sealed record FixtureReplayReport(
    string FixtureId,
    DateTimeOffset RecordedAt,
    string Purpose,
    ParityReport StoredParity,
    ParityReport? ReplayParity,
    bool ReplaySucceeded,
    string Notes);

/// <summary>
/// FlushAsync 结果快照。
/// </summary>
/// <param name="ProcessedCount">已成功落盘的 Record 事件数。</param>
/// <param name="FailedCount">重试 3 次仍失败、已进 dead-letter 的 Record 事件数。</param>
/// <param name="DroppedCount">入队失败（writer 已完成 / bounded 丢弃）的事件数。</param>
/// <param name="AcceptedSequence">sentinel 事件序号（flush 接受到的最大序号）。</param>
/// <param name="LastPersistedSequence">最后成功落盘的 Record 事件序号；小于 AcceptedSequence 表示存在未落盘 gap。</param>
public sealed record FlushResult(
    int ProcessedCount,
    int FailedCount,
    int DroppedCount,
    long AcceptedSequence,
    long LastPersistedSequence);

/// <summary>
/// DisposeAsync 的 drain 结果。
/// </summary>
/// <param name="DrainedCount">shutdown 时已成功落盘的 Record 事件数（consumer 正常退出后的累计计数）。</param>
/// <param name="UndrainedCount">shutdown 时因 drain 超时未处理的事件数（队列中剩余事件数）。</param>
public sealed record DisposeDrainResult(int DrainedCount, int UndrainedCount);

/// <summary>
/// 实验平面指标快照（供 ControlRoom / 诊断工具消费）。
/// </summary>
/// <remarks>
/// 一次调用捕获全部关键指标，避免 ControlRoom 多次读取时计数器不一致。
/// ControlRoom 可定期调用 <see cref="DecisionExperimentPlaneIntegration.GetMetricsSnapshot"/>
/// 渲染指标页面，监控实验平面健康度。
/// </remarks>
public sealed record ExperimentPlaneMetricsSnapshot
{
    /// <summary>当前队列深度（未消费事件数）。</summary>
    public int QueueDepth { get; init; }

    /// <summary>累计丢弃事件数（队列满时 TryWrite 返回 false，或 writer 完成后入队失败）。</summary>
    public int DroppedCount { get; init; }

    /// <summary>累计写入失败事件数（重试 3 次仍未成功，已进 dead-letter）。</summary>
    public int FailedWriteCount { get; init; }

    /// <summary>累计成功落盘 Record 事件数。</summary>
    public int ProcessedCount { get; init; }

    /// <summary>dead-letter 队列当前长度（重试失败后保留的事件数）。</summary>
    public int DeadLetterCount { get; init; }

    /// <summary>最后成功落盘的 Record 事件序号。</summary>
    public long LastPersistedSequence { get; init; }

    /// <summary>最后接受（入队）的事件序号。</summary>
    public long LastAcceptedSequence { get; init; }
}
