namespace ContextCore.Abstractions.Models;

/// <summary>
/// Package Quality 指标集合。第一版保持确定性，由 ContextDecisionProjector
/// 在 ProjectPackage 投影过程中一次性计算。所有指标为 [0,1] 区间的归一化分数（1.0 = 最优）。
/// </summary>
/// <remarks>
/// 8 个指标的语义：
/// <list type="bullet">
/// <item><b>AnchorCoverage</b>：被选入的候选覆盖到的 anchor 比例（1.0 = 所有 anchor 都有候选命中）。</item>
/// <item><b>HardConstraintSatisfaction</b>：active hard constraints 在选中项中的比例（1.0 = 所有 hard constraint 都被选中）。</item>
/// <item><b>RequiredItemCoverage</b>：mustHit IDs 落入选中项的比例（1.0 = 所有 mustHit 都命中）。</item>
/// <item><b>Redundancy</b>：选中项中无内容重复的比例（1.0 = 无重复，越低说明冗余越多）。</item>
/// <item><b>ProvenanceCompleteness</b>：选中项中携带 SourceRefs 的比例（1.0 = 全部有来源）。</item>
/// <item><b>LifecycleRisk</b>：选中项中无 lifecycle 风险的比例（1.0 = 全部 active，越低说明 deprecated/superseded 越多）。</item>
/// <item><b>TokenEfficiency</b>：token 预算的有效利用率（1.0 = 完全用尽；&lt;1.0 = 浪费；超支记 0）。</item>
/// <item><b>SectionBalance</b>：各 section 预算使用率的均衡度（1.0 = 完全均衡）。</item>
/// </list>
/// </remarks>
public sealed class PackageQualityReport
{
    /// <summary>Anchor 覆盖率指标。</summary>
    public PackageQualityMetric AnchorCoverage { get; init; } = new();

    /// <summary>Hard constraint 满足度指标。</summary>
    public PackageQualityMetric HardConstraintSatisfaction { get; init; } = new();

    /// <summary>MustHit / Required IDs 覆盖率指标。</summary>
    public PackageQualityMetric RequiredItemCoverage { get; init; } = new();

    /// <summary>无冗余度指标（1.0 = 完全无冗余）。</summary>
    public PackageQualityMetric Redundancy { get; init; } = new();

    /// <summary>Provenance 完整性指标。</summary>
    public PackageQualityMetric ProvenanceCompleteness { get; init; } = new();

    /// <summary>Lifecycle 风险指标（1.0 = 全部 active，无风险）。</summary>
    public PackageQualityMetric LifecycleRisk { get; init; } = new();

    /// <summary>Token 预算利用效率指标。</summary>
    public PackageQualityMetric TokenEfficiency { get; init; } = new();

    /// <summary>Section 预算均衡度指标。</summary>
    public PackageQualityMetric SectionBalance { get; init; } = new();

    /// <summary>
    /// 8 个指标的加权聚合分数 [0,1]，0.0 = 最差，1.0 = 最优。
    /// 默认权重：HardConstraintSatisfaction=0.20，AnchorCoverage/RequiredItemCoverage/LifecycleRisk=0.15，
    /// Redundancy/ProvenanceCompleteness/TokenEfficiency=0.10，SectionBalance=0.05。
    /// </summary>
    public double OverallScore { get; init; }

    /// <summary>策略版本，标识 Package Quality 计算结构（QualityContractV1_0 = "quality-contract/1.0"，按能力独立演进）。</summary>
    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.QualityContractV1_0;

    /// <summary>计算时间。</summary>
    public DateTimeOffset ComputedAt { get; init; }
}

/// <summary>
/// 单个 Package Quality 指标度量。
/// Score 为 [0,1] 区间的归一化分数（1.0 = 最优）。
/// Numerator/Denominator 为原始分子分母，便于审计与回归比对。
/// </summary>
public sealed class PackageQualityMetric
{
    /// <summary>指标名称（如 "AnchorCoverage" / "HardConstraintSatisfaction"）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>归一化分数 [0,1]，1.0 = 最优。</summary>
    public double Score { get; init; }

    /// <summary>分子（原始计数，如命中的 anchor 数量）。</summary>
    public int Numerator { get; init; }

    /// <summary>分母（原始计数，如总 anchor 数量）。</summary>
    public int Denominator { get; init; }

    /// <summary>人类可读详情（如 "covered=3/5 anchors (semantic=2/3, raw=1/2)"）。</summary>
    public string Detail { get; init; } = string.Empty;
}
