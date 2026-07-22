using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ---------------------------------------------------------------------------
// §10.0 IExperimentRecorder — P0-9：Replay fixture 持久化抽象
// ---------------------------------------------------------------------------

/// <summary>
/// P0-9：Replay fixture 持久化抽象。
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
/// P0-9：默认 in-memory 实现。进程内 List + lock，重启即丢失。
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
// R28-B B-5：Legacy Removal + DecisionExperimentPlane 长期保留
//
// 目标（B-5 阶段：V2 成为唯一权威路径，Legacy 代码保留但默认停用）：
//   1. DecisionExperimentPlaneIntegration：长期保留的实验平面集成入口。
//      提供 sampled shadow（抽样校验）、replay fixture 存储、CI 验收 hook。
//   2. CutoverConfiguration：从配置读取默认 cutover 比例（默认 100% = V2 only）。
//   3. LegacyCodeMarkedDeprecated：标记 Legacy 路径为 [Obsolete]（不物理删除，
//      保留用于回滚和 DecisionExperimentPlane 的 parity 对比基线）。
//
// 设计原则：
//   1. B-5 不物理删除 Legacy 代码（HybridContextRetriever / BasicContextPackageBuilder）。
//      原因：DecisionExperimentPlane 需要 Legacy 作为 parity 基线；
//      回滚安全需要 Legacy 代码可用。
//   2. CutoverController 默认 100%（V2 only），可通过配置降级。
//   3. DecisionExperimentPlane 作为长期基础设施：
//      - Sampled shadow：即使 V2 已权威，仍按采样率执行 Legacy + parity 对比
//      - Replay fixture：存储历史 parity 报告供回归分析
//      - CI 验收 hook：ShadowGateEvaluator 输出 CutoverReadinessAssessment
// ===========================================================================

// ---------------------------------------------------------------------------
// §10.1 CutoverConfiguration — 配置驱动默认比例
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-5：Cutover 配置。从环境变量/配置读取默认 cutover 比例。
/// </summary>
/// <remarks>
/// 默认 100%（V2 only）。可通过环境变量 CC_CUTOVER_PERCENTAGE 降级。
/// B-5 阶段 Legacy 代码保留但默认停用（CutoverPercentage=100）。
/// </remarks>
public sealed class CutoverConfiguration
{
    /// <summary>环境变量名：控制 V2 流量百分比（0-100）。</summary>
    public const string CutoverPercentageEnvVar = "CC_CUTOVER_PERCENTAGE";

    /// <summary>默认 cutover 百分比（R28-B.6 Closure Gate: 0 = Legacy only，直到 Closure Gate 通过）。</summary>
    /// <remarks>
    /// R28-B.5 曾设为 100（V2 only），但 B.6 Closure Gate 要求在验收测试通过前
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
// §10.2 DecisionExperimentPlaneIntegration — 长期实验平面集成
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-5：DecisionExperimentPlane 长期保留集成入口。
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
public sealed class DecisionExperimentPlaneIntegration
{
    private readonly DecisionExperimentPlane _experimentPlane;
    private readonly ShadowGateEvaluator _gateEvaluator;
    private readonly CutoverConfiguration _configuration;
    private readonly IExperimentRecorder _recorder;

    /// <summary>构造长期实验平面集成。</summary>
    public DecisionExperimentPlaneIntegration(
        DecisionExperimentPlane experimentPlane,
        ShadowGateEvaluator gateEvaluator,
        CutoverConfiguration configuration,
        IExperimentRecorder? recorder = null)
    {
        _experimentPlane = experimentPlane ?? throw new ArgumentNullException(nameof(experimentPlane));
        _gateEvaluator = gateEvaluator ?? throw new ArgumentNullException(nameof(gateEvaluator));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        // P0-9：持久化委托给 IExperimentRecorder；未注入时回退到 in-memory 默认实现。
        _recorder = recorder ?? new InMemoryExperimentRecorder();
    }

    /// <summary>历史 replay fixture 集合（线程安全快照）。</summary>
    public IReadOnlyList<ReplayFixture> FixtureHistory
        => _recorder.GetHistoryAsync().GetAwaiter().GetResult();

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
    public void RecordFixture(ParityReport report, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(report);
        var fixture = ReplayFixture.FromReport(report, fixtureId, purpose, notes);
        _recorder.RecordAsync(fixture).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// P0-9：从完整 Retrieval shadow 报告构建并持久化 replay fixture。
    /// 携带 WorkingSet + V2Result，使 fixture 可离线重放。
    /// </summary>
    public void RecordShadowReport(RetrievalShadowReport shadowReport, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(shadowReport);
        var fixture = ReplayFixture.FromShadowReport(
            shadowReport.Parity, shadowReport.WorkingSet, shadowReport.V2Result,
            fixtureId, purpose, notes);
        _recorder.RecordAsync(fixture).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// P0-9：从完整 Package shadow 报告构建并持久化 replay fixture。
    /// 携带 WorkingSet + V2Result，使 fixture 可离线重放。
    /// </summary>
    public void RecordShadowReport(PackageShadowReport shadowReport, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(shadowReport);
        var fixture = ReplayFixture.FromShadowReport(
            shadowReport.Parity, shadowReport.WorkingSet, shadowReport.V2Result,
            fixtureId, purpose, notes);
        _recorder.RecordAsync(fixture).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>评估历史 fixture，产出 cutover 就绪判定（CI 验收 hook）。</summary>
    public CutoverReadinessAssessment EvaluateHistoricalFixtures()
    {
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

    /// <summary>清除历史 fixture（用于测试或重置）。</summary>
    public void ClearHistory()
    {
        _recorder.ClearAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// R28-B.6 阶段 E：从历史 fixture 重放 V2 决策，验证决策可重现性。
    /// </summary>
    /// <remarks>
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
    public async ValueTask<FixtureReplayReport?> ReplayFixtureAsync(
        string fixtureId,
        IContextDecisionRuntime? v2Runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);

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
}

/// <summary>
/// R28-B.6 阶段 E：fixture 重放报告。
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
