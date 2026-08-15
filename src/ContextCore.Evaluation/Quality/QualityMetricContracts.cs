namespace ContextCore.Evaluation.Quality;

/// <summary>
/// 检索质量指标契约。
/// <para>
/// 先定义指标、空集合语义、聚合方式、切片维度与误差区间，再谈调参。
/// 质量指标只消费证据的存在性、排序与 token 成本；task/tool outcome、重试次数、
/// 人工纠正只作为结果信号，FinalScore 不得充当质量标签。
/// </para>
/// </summary>
public static class QualityMetricContracts
{
    /// <summary>固定交付窗口（Recall@K / Precision@K / nDCG@K 的 K）。评估者统一传入。</summary>
    public const int DefaultK = 10;

    /// <summary>固定 token 预算默认值（Recall@TokenBudget 的预算），与检索请求默认一致。</summary>
    public const int DefaultTokenBudget = 4000;

    /// <summary>Wilson 95% 置信区间使用的 z 值。</summary>
    public const double WilsonZ = 1.96;
}

/// <summary>
/// 一条已排序的检索输出证据，是质量指标的唯一输入形态。
/// 不携带 FinalScore 或结果信号字段——质量指标与结果信号严格隔离。
/// </summary>
public sealed class RankedEvidence
{
    /// <summary>证据 ID（上下文 ID / 记忆 ID / 来源引用）。</summary>
    public required string EvidenceId { get; init; }

    /// <summary>该证据打包后的 token 成本；预算类指标需要。0 表示未知（按 0 计入前缀）。</summary>
    public int TokenCount { get; init; }

    /// <summary>该证据在排序中的相关等级（由评估样本标注，非模型分数）：0 = 无关，1..3 = 相关。</summary>
    public int RelevanceGrade { get; init; }
}

/// <summary>
/// 一条带相关等级的证据期望。Grade 取值 1..3，越大越相关。
/// </summary>
public sealed class RelevantEvidenceGrade
{
    /// <summary>证据 ID。</summary>
    public required string EvidenceId { get; init; }

    /// <summary>相关等级 1..3。</summary>
    public int Grade { get; init; }
}

/// <summary>
/// 样本的证据期望：必须出现 / 有帮助（带等级）/ 不应出现。
/// 三条列表均可为空，空集合语义在指标计算中固定。
/// </summary>
public sealed class QualityEvidenceExpectation
{
    /// <summary>完成任务不可缺少的证据。Recall@K / Recall@TokenBudget / MRR / 关键证据漏失率以它为准。</summary>
    public IReadOnlyList<string> RequiredEvidenceIds { get; init; } = Array.Empty<string>();

    /// <summary>有帮助但非必需的证据，可带相关等级（nDCG 使用等级）。</summary>
    public IReadOnlyList<RelevantEvidenceGrade> RelevantEvidenceIds { get; init; } = Array.Empty<RelevantEvidenceGrade>();

    /// <summary>不应出现在结果中的证据。命中不降低精度（精度只看正相关证据），由禁止命中率单独衡量。</summary>
    public IReadOnlyList<string> ForbiddenExcludedIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 单条样本的固定切片维度。切片聚合按这四维分组，维度固定、不随数据集变化。
/// </summary>
public sealed class QualitySliceKey
{
    /// <summary>数据集名。</summary>
    public string Dataset { get; init; } = string.Empty;

    /// <summary>评测模式（ChatMode / NovelMode / AutomationMode / CodingMode / ProjectMode 等）。</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>通道或存储 Provider。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>问句数量分桶："1" / "4" / "8" / "8+"。</summary>
    public string QueryCountBucket { get; init; } = string.Empty;

    /// <summary>按固定维度生成规范化键，保证聚合输出顺序确定。</summary>
    public string NormalizedKey =>
        $"{Dataset}\u001f{Mode}\u001f{Provider}\u001f{QueryCountBucket}";
}

/// <summary>
/// 单条样本的质量指标。不可评分的指标为 null（N/A），不参与均值聚合。
/// </summary>
public sealed class SampleQualityMetrics
{
    /// <summary>样本 ID。</summary>
    public string SampleId { get; init; } = string.Empty;

    /// <summary>Required 证据数。</summary>
    public int RequiredCount { get; init; }

    /// <summary>Relevant 证据数。</summary>
    public int RelevantCount { get; init; }

    /// <summary>Forbidden 证据数。</summary>
    public int ForbiddenCount { get; init; }

    /// <summary>固定 K 下的召回率；Required 为空或 K ≤ 0 时为 null。</summary>
    public double? RecallAtK { get; init; }

    /// <summary>固定 token 预算下的召回率；Required 为空或预算 ≤ 0 时为 null。</summary>
    public double? RecallAtTokenBudget { get; init; }

    /// <summary>固定 K 下的精确率（正相关 = Required ∪ Relevant）；K ≤ 0 时为 null。</summary>
    public double? PrecisionAtK { get; init; }

    /// <summary>首个 Required 命中排名的倒数；Required 为空时为 null，无命中为 0。</summary>
    public double? Mrr { get; init; }

    /// <summary>固定 K 下的 nDCG（等级：Required = 3，Relevant = 标注等级，其余 = 0）；无正相关证据或 K ≤ 0 时为 null。</summary>
    public double? NdcgAtK { get; init; }

    /// <summary>关键证据漏失：Required 非空且任一条不在 top-K 交付窗口。</summary>
    public bool KeyEvidenceMissed { get; init; }

    /// <summary>禁止证据命中：Forbidden 与 top-K 交付窗口有交集。</summary>
    public bool ForbiddenInResult { get; init; }

    /// <summary>是否可评分：Required 或 Relevant 非空。不可评分样本从聚合中排除。</summary>
    public bool Scorable { get; init; }
}

/// <summary>Wilson 95% 置信区间（比例类指标的固定误差区间形式）。</summary>
public readonly record struct WilsonInterval(double Lower, double Upper);

/// <summary>
/// 样本集聚合结果。均值类指标为 macro-mean（样本级算术平均），
/// N/A 样本排除并计数；比例类指标为样本占比，附 Wilson 95% 区间（样本数 ≥ 1 时）。
/// 固定数据集上所有指标为精确值：同一输入重复计算逐位一致。
/// </summary>
public sealed class QualityMetricAggregate
{
    /// <summary>参与聚合的可评分样本数。</summary>
    public int SampleCount { get; init; }

    /// <summary>不可评分样本数（Required 与 Relevant 均为空）。</summary>
    public int UnscorableCount { get; init; }

    /// <summary>Required 非空的样本数（关键证据漏失率的分母）。</summary>
    public int RequiredNonEmptyCount { get; init; }

    /// <summary>Recall@K 的 macro-mean；无可评分样本时为 null。</summary>
    public double? RecallAtKMean { get; init; }

    /// <summary>Recall@TokenBudget 的 macro-mean；无可评分样本时为 null。</summary>
    public double? RecallAtTokenBudgetMean { get; init; }

    /// <summary>Precision@K 的 macro-mean；无可评分样本时为 null。</summary>
    public double? PrecisionAtKMean { get; init; }

    /// <summary>MRR 的 macro-mean；无可评分样本时为 null。</summary>
    public double? MrrMean { get; init; }

    /// <summary>nDCG@K 的 macro-mean；无可评分样本时为 null。</summary>
    public double? NdcgAtKMean { get; init; }

    /// <summary>关键证据漏失率 = 漏失样本数 / Required 非空样本数；分母为 0 时为 0。</summary>
    public double KeyEvidenceMissRate { get; init; }

    /// <summary>关键证据漏失率的 Wilson 95% 区间；分母为 0 时为 null。</summary>
    public WilsonInterval? KeyEvidenceMissInterval { get; init; }

    /// <summary>禁止命中率 = 含 Forbidden 命中的样本数 / 可评分样本数；分母为 0 时为 0。</summary>
    public double ForbiddenHitRate { get; init; }

    /// <summary>禁止命中率的 Wilson 95% 区间；分母为 0 时为 null。</summary>
    public WilsonInterval? ForbiddenHitInterval { get; init; }
}

/// <summary>
/// 切片聚合结果：固定四维切片键 → 该切片内的聚合。
/// </summary>
public sealed class QualitySliceAggregation
{
    /// <summary>按固定维度排序后的切片聚合（顺序确定，不依赖字典枚举）。</summary>
    public IReadOnlyList<QualitySliceResult> Slices { get; init; } = Array.Empty<QualitySliceResult>();

    /// <summary>全体样本（跨切片）聚合。</summary>
    public QualityMetricAggregate Overall { get; init; } = new();
}

/// <summary>单个切片的聚合结果。</summary>
public sealed class QualitySliceResult
{
    /// <summary>切片键。</summary>
    public QualitySliceKey Key { get; init; } = new();

    /// <summary>该切片内的聚合。</summary>
    public QualityMetricAggregate Aggregate { get; init; } = new();
}
