using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

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

    /// <summary>默认 cutover 百分比（B-5: 100 = V2 only）。</summary>
    public const int DefaultCutoverPercentage = 100;

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
    private readonly List<ReplayFixture> _fixtureHistory;

    /// <summary>构造长期实验平面集成。</summary>
    public DecisionExperimentPlaneIntegration(
        DecisionExperimentPlane experimentPlane,
        ShadowGateEvaluator gateEvaluator,
        CutoverConfiguration configuration)
    {
        _experimentPlane = experimentPlane ?? throw new ArgumentNullException(nameof(experimentPlane));
        _gateEvaluator = gateEvaluator ?? throw new ArgumentNullException(nameof(gateEvaluator));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _fixtureHistory = new List<ReplayFixture>();
    }

    /// <summary>历史 replay fixture 集合（线程安全快照）。</summary>
    public IReadOnlyList<ReplayFixture> FixtureHistory
    {
        get
        {
            lock (_fixtureHistory)
            {
                return _fixtureHistory.ToList();
            }
        }
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

    /// <summary>记录 parity fixture（线程安全）。</summary>
    public void RecordFixture(ParityReport report, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(report);
        var fixture = ReplayFixture.FromReport(report, fixtureId, purpose, notes);
        lock (_fixtureHistory)
        {
            _fixtureHistory.Add(fixture);
            // 限制历史大小（保留最近 10000 条）
            if (_fixtureHistory.Count > 10000)
            {
                _fixtureHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>评估历史 fixture，产出 cutover 就绪判定（CI 验收 hook）。</summary>
    public CutoverReadinessAssessment EvaluateHistoricalFixtures()
    {
        lock (_fixtureHistory)
        {
            var reports = _fixtureHistory
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
    }

    /// <summary>清除历史 fixture（用于测试或重置）。</summary>
    public void ClearHistory()
    {
        lock (_fixtureHistory)
        {
            _fixtureHistory.Clear();
        }
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
