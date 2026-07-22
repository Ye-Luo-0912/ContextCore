using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B B-3：Shadow Gate 多维度验收
//
// 目标（B-3 阶段：Hard parity 验收 + replay fixtures）：
//   1. ShadowGate：基于 ParityReport 的验收门控。
//      - Hard parity（阻断 Authoritative cutover）：JaccardIndex ≥ 0.99 + token 偏差 ≤ 阈值
//      - Diagnostic parity（告警不阻断）：0.90 ≤ JaccardIndex < 0.99
//      - Divergent（发散）：JaccardIndex < 0.90 → 阻断切换 + 触发告警
//   2. ReplayFixture：可重放的 parity fixture（序列化为 JSON，供回归测试消费）。
//   3. ShadowGateEvaluator：批量评估多个 Shadow 报告，产出 cutover 就绪判定。
//
// 设计原则：
//   1. B-3 升级 DecisionExperimentPlane 的 Diagnostic parity 为 Hard parity（阻断切换）。
//   2. ShadowGate 不修改 ShadowDecisionRuntime（B-2 产出 ParityReport；B-3 消费之）。
//   3. Replay fixtures 可离线重放，用于回归测试和 CI 验收。
//   4. 多维度验收：selected 集合一致性（Jaccard）+ token 预算偏差 + dropped 候选数偏差。
// ===========================================================================

// ---------------------------------------------------------------------------
// §8.1 ShadowGate — Parity 验收门控
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-3：Shadow Gate 验收门控。
/// 基于 ParityReport 判定是否可安全执行 Authoritative cutover。
/// </summary>
/// <remarks>
/// 验收维度：
///   1. Selected 集合一致性（JaccardIndex ≥ threshold）
///   2. Token 预算偏差（|LegacyTokenTotal - V2TokenTotal| / max(LegacyTokenTotal, 1) ≤ tokenTolerance）
///   3. Dropped 候选数偏差（|LegacyDropped - V2Dropped| ≤ droppedTolerance）
///
/// 判定结果：
///   - Pass（Hard parity）：全部维度通过 → 可安全切换
///   - Warn（Diagnostic parity）：部分维度告警 → 可切换但需监控
///   - Fail（Divergent）：关键维度发散 → 阻断切换
/// </remarks>
public sealed class ShadowGate
{
    private readonly double _hardJaccardThreshold;
    private readonly double _diagnosticJaccardThreshold;
    private readonly double _tokenTolerance;
    private readonly int _droppedTolerance;

    /// <summary>构造 ShadowGate，使用默认验收阈值。</summary>
    public ShadowGate()
        : this(
            hardJaccardThreshold: 0.99,
            diagnosticJaccardThreshold: 0.90,
            tokenTolerance: 0.05,
            droppedTolerance: 2)
    {
    }

    /// <summary>构造 ShadowGate，使用自定义验收阈值。</summary>
    /// <param name="hardJaccardThreshold">Hard parity 的 Jaccard 阈值（默认 0.99）。</param>
    /// <param name="diagnosticJaccardThreshold">Diagnostic parity 的 Jaccard 阈值（默认 0.90）。</param>
    /// <param name="tokenTolerance">Token 预算偏差容忍度（0-1，默认 0.05 = 5%）。</param>
    /// <param name="droppedTolerance">Dropped 候选数偏差容忍度（绝对值，默认 2）。</param>
    public ShadowGate(
        double hardJaccardThreshold,
        double diagnosticJaccardThreshold,
        double tokenTolerance,
        int droppedTolerance)
    {
        if (hardJaccardThreshold < 0 || hardJaccardThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(hardJaccardThreshold));
        if (diagnosticJaccardThreshold < 0 || diagnosticJaccardThreshold > hardJaccardThreshold)
            throw new ArgumentOutOfRangeException(nameof(diagnosticJaccardThreshold));
        if (tokenTolerance < 0 || tokenTolerance > 1)
            throw new ArgumentOutOfRangeException(nameof(tokenTolerance));
        if (droppedTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(droppedTolerance));

        _hardJaccardThreshold = hardJaccardThreshold;
        _diagnosticJaccardThreshold = diagnosticJaccardThreshold;
        _tokenTolerance = tokenTolerance;
        _droppedTolerance = droppedTolerance;
    }

    /// <summary>评估单个 ParityReport，产出验收结果。</summary>
    public ShadowGateResult Evaluate(ParityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var dimensions = new List<ShadowGateDimensionResult>(3);

        // 维度 1：Jaccard 一致性
        var jaccardLevel = report.JaccardIndex switch
        {
            >= 0.99 when report.JaccardIndex >= _hardJaccardThreshold => ParityLevel.Hard,
            >= 0.90 when report.JaccardIndex >= _diagnosticJaccardThreshold => ParityLevel.Diagnostic,
            _ => ParityLevel.Divergent
        };
        dimensions.Add(new ShadowGateDimensionResult(
            Dimension: "jaccard",
            Value: report.JaccardIndex,
            Threshold: _hardJaccardThreshold,
            Level: jaccardLevel,
            Detail: $"JaccardIndex={report.JaccardIndex:F4} (hard={_hardJaccardThreshold:F2}, diag={_diagnosticJaccardThreshold:F2})"));

        // 维度 2：Token 预算偏差
        var tokenDeviation = report.LegacyTokenTotal == 0
            ? (report.V2TokenTotal == 0 ? 0.0 : 1.0)
            : Math.Abs(report.LegacyTokenTotal - report.V2TokenTotal) / (double)report.LegacyTokenTotal;
        var tokenLevel = tokenDeviation <= _tokenTolerance
            ? ParityLevel.Hard
            : tokenDeviation <= _tokenTolerance * 2
                ? ParityLevel.Diagnostic
                : ParityLevel.Divergent;
        dimensions.Add(new ShadowGateDimensionResult(
            Dimension: "token-budget",
            Value: tokenDeviation,
            Threshold: _tokenTolerance,
            Level: tokenLevel,
            Detail: $"Deviation={tokenDeviation:P2} (tolerance={_tokenTolerance:P2}), Legacy={report.LegacyTokenTotal}, V2={report.V2TokenTotal}"));

        // 维度 3：候选数偏差（selected + only-in-legacy + only-in-v2）
        var totalDeviation = report.OnlyInLegacyCount + report.OnlyInV2Count;
        var countLevel = totalDeviation <= _droppedTolerance
            ? ParityLevel.Hard
            : totalDeviation <= _droppedTolerance * 2
                ? ParityLevel.Diagnostic
                : ParityLevel.Divergent;
        dimensions.Add(new ShadowGateDimensionResult(
            Dimension: "candidate-count",
            Value: totalDeviation,
            Threshold: _droppedTolerance,
            Level: countLevel,
            Detail: $"OnlyInLegacy={report.OnlyInLegacyCount}, OnlyInV2={report.OnlyInV2Count} (tolerance={_droppedTolerance})"));

        // 综合判定：取最低维度等级
        var overallLevel = dimensions.Min(d => d.Level);
        var canCutover = overallLevel == ParityLevel.Hard;
        var hasWarnings = overallLevel == ParityLevel.Diagnostic;

        return new ShadowGateResult(
            OverallLevel: overallLevel,
            CanCutover: canCutover,
            HasWarnings: hasWarnings,
            Dimensions: dimensions,
            Summary: overallLevel switch
            {
                ParityLevel.Hard => "Hard parity achieved — safe to cutover",
                ParityLevel.Diagnostic => "Diagnostic parity — cutover allowed with monitoring",
                _ => "Divergent — cutover blocked, investigation required"
            });
    }
}

/// <summary>R28-B B-3：单个验收维度的结果。</summary>
public sealed record ShadowGateDimensionResult(
    string Dimension,
    double Value,
    double Threshold,
    ParityLevel Level,
    string Detail);

/// <summary>R28-B B-3：Shadow Gate 综合验收结果。</summary>
public sealed record ShadowGateResult(
    ParityLevel OverallLevel,
    bool CanCutover,
    bool HasWarnings,
    IReadOnlyList<ShadowGateDimensionResult> Dimensions,
    string Summary);

// ---------------------------------------------------------------------------
// §8.2 ReplayFixture — 可重放的 parity fixture
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-3：可重放的 parity fixture。
/// 序列化为 JSON 供回归测试和 CI 验收消费。
/// </summary>
public sealed record ReplayFixture(
    string FixtureId,
    DateTimeOffset RecordedAt,
    string Purpose,
    int LegacySelectedCount,
    int V2SelectedCount,
    int CommonSelectedCount,
    int OnlyInLegacyCount,
    int OnlyInV2Count,
    double JaccardIndex,
    int LegacyTokenTotal,
    int V2TokenTotal,
    int WorkingSetCandidateCount,
    ParityLevel ParityLevel,
    string Notes)
{
    /// <summary>从 ParityReport 构建 ReplayFixture。</summary>
    public static ReplayFixture FromReport(ParityReport report, string fixtureId, string purpose, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ReplayFixture(
            FixtureId: fixtureId,
            RecordedAt: DateTimeOffset.UtcNow,
            Purpose: purpose,
            LegacySelectedCount: report.LegacySelectedCount,
            V2SelectedCount: report.V2SelectedCount,
            CommonSelectedCount: report.CommonSelectedCount,
            OnlyInLegacyCount: report.OnlyInLegacyCount,
            OnlyInV2Count: report.OnlyInV2Count,
            JaccardIndex: report.JaccardIndex,
            LegacyTokenTotal: report.LegacyTokenTotal,
            V2TokenTotal: report.V2TokenTotal,
            WorkingSetCandidateCount: report.WorkingSetCandidateCount,
            ParityLevel: report.ParityLevel,
            Notes: notes);
    }
}

// ---------------------------------------------------------------------------
// §8.3 ShadowGateEvaluator — 批量评估 + cutover 就绪判定
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-3：批量评估多个 Shadow 报告，产出 cutover 就绪判定。
/// </summary>
/// <remarks>
/// 用于 CI 验收：批量运行 Shadow tee，收集所有 ParityReport，
/// 通过 ShadowGate 评估，判定是否可安全执行 Authoritative cutover。
/// </remarks>
public sealed class ShadowGateEvaluator
{
    private readonly ShadowGate _gate;

    /// <summary>构造评估器。</summary>
    public ShadowGateEvaluator() : this(new ShadowGate())
    {
    }

    /// <summary>构造评估器，使用自定义 ShadowGate。</summary>
    public ShadowGateEvaluator(ShadowGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>批量评估 ParityReport 集合，产出 cutover 就绪判定。</summary>
    public CutoverReadinessAssessment EvaluateBatch(IReadOnlyList<ParityReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        if (reports.Count == 0)
        {
            return new CutoverReadinessAssessment(
                IsReady: false,
                TotalReports: 0,
                HardCount: 0,
                DiagnosticCount: 0,
                DivergentCount: 0,
                OverallLevel: ParityLevel.Divergent,
                Results: Array.Empty<ShadowGateResult>(),
                Summary: "No reports to evaluate — cutover blocked");
        }

        var results = new List<ShadowGateResult>(reports.Count);
        foreach (var report in reports)
        {
            results.Add(_gate.Evaluate(report));
        }

        var hardCount = results.Count(r => r.OverallLevel == ParityLevel.Hard);
        var diagnosticCount = results.Count(r => r.OverallLevel == ParityLevel.Diagnostic);
        var divergentCount = results.Count(r => r.OverallLevel == ParityLevel.Divergent);

        // Cutover 就绪条件：无 Divergent + Diagnostic 比例 ≤ 20%
        var diagnosticRatio = (double)diagnosticCount / reports.Count;
        var isReady = divergentCount == 0 && diagnosticRatio <= 0.20;
        var overallLevel = divergentCount > 0
            ? ParityLevel.Divergent
            : diagnosticRatio <= 0.20
                ? ParityLevel.Hard
                : ParityLevel.Diagnostic;

        var summary = isReady
            ? $"Cutover ready: {hardCount} hard, {diagnosticCount} diagnostic, {divergentCount} divergent (n={reports.Count})"
            : $"Cutover blocked: {divergentCount} divergent, diagnostic ratio {diagnosticRatio:P1} > 20% threshold";

        return new CutoverReadinessAssessment(
            IsReady: isReady,
            TotalReports: reports.Count,
            HardCount: hardCount,
            DiagnosticCount: diagnosticCount,
            DivergentCount: divergentCount,
            OverallLevel: overallLevel,
            Results: results,
            Summary: summary);
    }
}

/// <summary>R28-B B-3：Cutover 就绪评估报告。</summary>
public sealed record CutoverReadinessAssessment(
    bool IsReady,
    int TotalReports,
    int HardCount,
    int DiagnosticCount,
    int DivergentCount,
    ParityLevel OverallLevel,
    IReadOnlyList<ShadowGateResult> Results,
    string Summary);
