using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// 数据质量闸门判定结果。
/// </summary>
public enum LearningDataQualityVerdict : byte
{
    /// <summary>通过：数据质量满足闸门要求，可导出/使用。</summary>
    Passed = 0,

    /// <summary>警告：存在质量问题但不阻断（如样本缺失偏高 / 标签不平衡）。</summary>
    Warning = 1,

    /// <summary>阻断：数据不可用（空数据集等），禁止导出/使用。</summary>
    Blocked = 2
}

/// <summary>
/// Learning 数据质量报告（闸门输出，审计可解释）。
/// </summary>
public sealed record LearningDataQualityReport
{
    /// <summary>闸门判定。</summary>
    public required LearningDataQualityVerdict Verdict { get; init; }

    /// <summary>快照 ID（被检对象）。</summary>
    public string? SnapshotId { get; init; }

    /// <summary>物化样本数。</summary>
    public required int MaterializedCount { get; init; }

    /// <summary>正样本（选中）数。</summary>
    public required int PositiveCount { get; init; }

    /// <summary>负样本（丢弃）数。</summary>
    public required int NegativeCount { get; init; }

    /// <summary>缺失样本数（输入 - 物化；null = 输入不可确定）。</summary>
    public int? MissingCount { get; init; }

    /// <summary>缺失率（0-1；null = 输入不可确定）。</summary>
    public double? MissingRatio { get; init; }

    /// <summary>检测到的问题明细（中文，审计可解释）。</summary>
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    /// <summary>检查时间（UTC）。</summary>
    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>
/// Learning 数据质量闸门（WP-T）：快照导出/使用前的数据质量校验。
/// 检查项：
/// 1. 空数据集（MaterializedCount == 0）→ Blocked（不可用）；
/// 2. 样本缺失率（MissingCount / InputEvidenceCount &gt; 0.10）→ Warning（数据不完整）；
/// 3. 标签不平衡（正/负样本比例 &lt; 0.05）→ Warning（单侧标签无法有效训练）。
/// 语义：Blocked 阻断使用；Warning 允许但随报告标注；Passed 正常。
/// </summary>
public sealed class LearningDataQualityGate
{
    /// <summary>样本缺失率阈值（高于则警告）。</summary>
    public const double MaxMissingRatio = 0.10;

    /// <summary>标签不平衡阈值（正/负占比低于则警告）。</summary>
    public const double MinLabelRatio = 0.05;

    /// <summary>评估数据集快照质量。</summary>
    /// <param name="snapshot">数据集快照报告。</param>
    /// <param name="positiveCount">正样本（选中）数。</param>
    /// <param name="negativeCount">负样本（丢弃）数。</param>
    /// <returns>质量报告（判定 + 问题明细）。</returns>
    public LearningDataQualityReport Evaluate(
        DatasetSnapshotReport snapshot,
        int positiveCount,
        int negativeCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var issues = new List<string>();
        var verdict = LearningDataQualityVerdict.Passed;

        // 1. 空数据集 → Blocked。
        if (snapshot.MaterializedCount == 0)
        {
            issues.Add("数据集为空（MaterializedCount=0），无法用于训练。");
            verdict = LearningDataQualityVerdict.Blocked;
        }

        // 2. 样本缺失率。
        double? missingRatio = null;
        if (snapshot.InputEvidenceCount is > 0 && snapshot.MissingCount is not null)
        {
            missingRatio = (double)snapshot.MissingCount.Value / snapshot.InputEvidenceCount.Value;
            if (missingRatio > MaxMissingRatio)
            {
                issues.Add($"样本缺失率 {missingRatio:P1} 超过阈值 {MaxMissingRatio:P0}（数据不完整）。");
                if (verdict == LearningDataQualityVerdict.Passed)
                {
                    verdict = LearningDataQualityVerdict.Warning;
                }
            }
        }

        // 3. 标签不平衡（仅当数据集非空时检查）。
        if (snapshot.MaterializedCount > 0 && positiveCount + negativeCount > 0)
        {
            var positiveRatio = (double)positiveCount / (positiveCount + negativeCount);
            if (positiveRatio < MinLabelRatio || positiveRatio > 1.0 - MinLabelRatio)
            {
                issues.Add($"标签不平衡（正样本占比 {positiveRatio:P1}，低于 {MinLabelRatio:P0}），单侧标签无法有效训练。");
                if (verdict == LearningDataQualityVerdict.Passed)
                {
                    verdict = LearningDataQualityVerdict.Warning;
                }
            }
        }

        return new LearningDataQualityReport
        {
            Verdict = verdict,
            SnapshotId = snapshot.SnapshotId,
            MaterializedCount = snapshot.MaterializedCount,
            PositiveCount = positiveCount,
            NegativeCount = negativeCount,
            MissingCount = snapshot.MissingCount,
            MissingRatio = missingRatio,
            Issues = issues,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }
}
