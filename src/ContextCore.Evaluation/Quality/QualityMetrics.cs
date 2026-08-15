using ContextCore.Evaluation.Quality;

namespace ContextCore.Evaluation.Quality;

/// <summary>
/// 质量指标公式与聚合（契约纯函数实现）。
/// <para>
/// 公式固定：Recall@K = |Required ∩ topK| / |Required|；
/// Recall@TokenBudget = 按排序前缀累计 token 至预算耗尽，命中 Required 数 / |Required|；
/// Precision@K = |(Required ∪ Relevant) ∩ topK| / K；MRR = 1 / 首个 Required 命中排名（无命中 0）；
/// nDCG@K = DCG / IDCG（等级：Required = 3，Relevant = 标注等级，Forbidden 与其它 = 0）。
/// 空集合语义：Required 为空 → 召回类与 MRR 为 N/A（null）；K ≤ 0 → Precision/Recall/nDCG 为 N/A；
/// 预算 ≤ 0 → Recall@TokenBudget 为 N/A；Required 与 Relevant 均为空 → 样本不可评分。
/// nDCG 的 IDCG 基于全部正相关证据（含未召回）按等级降序的理想排序，未召回证据会拉低分数。
/// 所有函数无随机性、无外部状态，同一输入重复计算逐位一致。
/// </summary>
public static class QualityMetrics
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// 计算单条样本的固定 K 与 token 预算下的质量指标。
    /// </summary>
    public static SampleQualityMetrics EvaluateSample(
        string sampleId,
        QualityEvidenceExpectation expectation,
        IReadOnlyList<RankedEvidence> ranked,
        int k,
        int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(ranked);

        var requiredSet = new HashSet<string>(expectation.RequiredEvidenceIds, IdComparer);
        var relevantGrades = new Dictionary<string, int>(IdComparer);
        foreach (var item in expectation.RelevantEvidenceIds)
        {
            if (!string.IsNullOrWhiteSpace(item.EvidenceId))
            {
                relevantGrades[item.EvidenceId] = Math.Clamp(item.Grade, 1, 3);
            }
        }

        var forbiddenSet = new HashSet<string>(expectation.ForbiddenExcludedIds, IdComparer);
        var positiveSet = new HashSet<string>(requiredSet, IdComparer);
        foreach (var id in relevantGrades.Keys)
        {
            positiveSet.Add(id);
        }

        var requiredCount = requiredSet.Count;
        var scorable = requiredCount > 0 || relevantGrades.Count > 0;
        if (k < 0)
        {
            k = 0;
        }

        var topK = ranked.Take(k).ToArray();
        var topKIds = new HashSet<string>(topK.Select(e => e.EvidenceId), IdComparer);

        // Recall@K
        double? recallAtK = null;
        if (requiredCount > 0 && k > 0)
        {
            var hits = topKIds.Count(requiredSet.Contains);
            recallAtK = (double)hits / requiredCount;
        }

        // Recall@TokenBudget：按排序顺序累计 token，直到下一条超预算为止（最后一条超预算不含入）。
        double? recallAtBudget = null;
        if (requiredCount > 0 && tokenBudget > 0)
        {
            var hits = 0;
            var used = 0;
            foreach (var evidence in ranked)
            {
                var cost = Math.Max(0, evidence.TokenCount);
                if (used + cost > tokenBudget)
                {
                    break;
                }
                used += cost;
                if (requiredSet.Contains(evidence.EvidenceId))
                {
                    hits++;
                }
            }
            recallAtBudget = (double)hits / requiredCount;
        }

        // Precision@K
        double? precisionAtK = null;
        if (k > 0)
        {
            var hits = topK.Count(e => positiveSet.Contains(e.EvidenceId));
            precisionAtK = (double)hits / k;
        }

        // MRR：首个 Required 命中的倒数排名；无命中为 0。
        double? mrr = null;
        if (requiredCount > 0)
        {
            mrr = 0.0;
            for (var i = 0; i < ranked.Count; i++)
            {
                if (requiredSet.Contains(ranked[i].EvidenceId))
                {
                    mrr = 1.0 / (i + 1);
                    break;
                }
            }
        }

        // nDCG@K：IDCG 基于全部正相关证据（含未召回证据）按等级降序的理想排序——
        // 未召回的正相关证据会拉低 nDCG（召回导向），与教科书 TREC 定义一致。
        double? ndcgAtK = null;
        if (k > 0)
        {
            var dcg = 0.0;
            for (var i = 0; i < Math.Min(k, ranked.Count); i++)
            {
                var grade = GradeOf(ranked[i].EvidenceId, requiredSet, relevantGrades);
                if (grade > 0)
                {
                    dcg += (Math.Pow(2, grade) - 1) / Math.Log2(i + 2);
                }
            }

            var idealGrades = new List<int>(requiredCount + relevantGrades.Count);
            idealGrades.AddRange(Enumerable.Repeat(3, requiredCount));
            foreach (var pair in relevantGrades)
            {
                if (!requiredSet.Contains(pair.Key))
                {
                    idealGrades.Add(pair.Value);
                }
            }
            idealGrades.Sort((a, b) => b.CompareTo(a));

            var idcg = 0.0;
            var take = Math.Min(k, idealGrades.Count);
            for (var i = 0; i < take; i++)
            {
                idcg += (Math.Pow(2, idealGrades[i]) - 1) / Math.Log2(i + 2);
            }
            if (idcg > 0)
            {
                ndcgAtK = dcg / idcg;
            }
        }

        var keyEvidenceMissed = requiredCount > 0 && requiredSet.Any(id => !topKIds.Contains(id));
        var forbiddenInResult = forbiddenSet.Count > 0 && topK.Any(e => forbiddenSet.Contains(e.EvidenceId));

        return new SampleQualityMetrics
        {
            SampleId = sampleId,
            RequiredCount = requiredCount,
            RelevantCount = relevantGrades.Count,
            ForbiddenCount = forbiddenSet.Count,
            RecallAtK = recallAtK,
            RecallAtTokenBudget = recallAtBudget,
            PrecisionAtK = precisionAtK,
            Mrr = mrr,
            NdcgAtK = ndcgAtK,
            KeyEvidenceMissed = keyEvidenceMissed,
            ForbiddenInResult = forbiddenInResult,
            Scorable = scorable
        };
    }

    /// <summary>
    /// 聚合样本集：均值类指标 macro-mean（N/A 排除并计数），比例类指标样本占比 + Wilson 95% 区间。
    /// </summary>
    public static QualityMetricAggregate Aggregate(IReadOnlyList<SampleQualityMetrics> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var scorable = samples.Where(s => s.Scorable).ToArray();
        var requiredNonEmpty = scorable.Where(s => s.RequiredCount > 0).ToArray();
        var sampleCount = scorable.Length;

        var recallValues = scorable.Select(s => s.RecallAtK).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var budgetValues = scorable.Select(s => s.RecallAtTokenBudget).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var precisionValues = scorable.Select(s => s.PrecisionAtK).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var mrrValues = scorable.Select(s => s.Mrr).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var ndcgValues = scorable.Select(s => s.NdcgAtK).Where(v => v.HasValue).Select(v => v!.Value).ToArray();

        var missedCount = requiredNonEmpty.Count(s => s.KeyEvidenceMissed);
        var forbiddenCount = scorable.Count(s => s.ForbiddenInResult);

        return new QualityMetricAggregate
        {
            SampleCount = sampleCount,
            UnscorableCount = samples.Count - sampleCount,
            RequiredNonEmptyCount = requiredNonEmpty.Length,
            RecallAtKMean = MeanOrNull(recallValues),
            RecallAtTokenBudgetMean = MeanOrNull(budgetValues),
            PrecisionAtKMean = MeanOrNull(precisionValues),
            MrrMean = MeanOrNull(mrrValues),
            NdcgAtKMean = MeanOrNull(ndcgValues),
            KeyEvidenceMissRate = requiredNonEmpty.Length == 0 ? 0.0 : (double)missedCount / requiredNonEmpty.Length,
            KeyEvidenceMissInterval = WilsonInterval(missedCount, requiredNonEmpty.Length),
            ForbiddenHitRate = sampleCount == 0 ? 0.0 : (double)forbiddenCount / sampleCount,
            ForbiddenHitInterval = WilsonInterval(forbiddenCount, sampleCount)
        };
    }

    /// <summary>
    /// 按固定四维切片聚合；输出按规范化键排序，顺序确定。
    /// </summary>
    public static QualitySliceAggregation AggregateBySlice(
        IReadOnlyList<(QualitySliceKey Key, IReadOnlyList<SampleQualityMetrics> Samples)> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);

        var results = new List<QualitySliceResult>();
        foreach (var (key, samples) in slices)
        {
            results.Add(new QualitySliceResult { Key = key, Aggregate = Aggregate(samples) });
        }
        results.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key.NormalizedKey, b.Key.NormalizedKey));

        var overall = Aggregate(slices.SelectMany(s => s.Samples).ToArray());
        return new QualitySliceAggregation { Slices = results, Overall = overall };
    }

    /// <summary>
    /// Wilson 95% 置信区间；n = 0 时为 null。比例类指标的唯一误差区间形式。
    /// </summary>
    public static WilsonInterval? WilsonInterval(int hits, int total)
    {
        if (total <= 0)
        {
            return null;
        }
        var n = (double)total;
        var p = (double)hits / n;
        var z = QualityMetricContracts.WilsonZ;
        var z2 = z * z;
        var denom = 1 + z2 / n;
        var center = (p + z2 / (2 * n)) / denom;
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / denom;
        return new WilsonInterval(
            Math.Max(0.0, center - half),
            Math.Min(1.0, center + half));
    }

    private static double? MeanOrNull(double[] values)
    {
        return values.Length == 0 ? null : values.Average();
    }

    private static int GradeOf(string evidenceId, HashSet<string> requiredSet, Dictionary<string, int> relevantGrades)
    {
        if (requiredSet.Contains(evidenceId))
        {
            return 3;
        }
        return relevantGrades.TryGetValue(evidenceId, out var grade) ? grade : 0;
    }
}
