using System.Reflection;
using ContextCore.Evaluation.Quality;

namespace ContextCore.Tests;

/// <summary>
/// 质量指标契约测试。
/// <para>
/// 验证目标：
/// 1. 公式：Recall@K / Recall@TokenBudget / Precision@K / MRR / nDCG@K 的具体数值
/// 2. 空集合语义：Required 为空、K ≤ 0、预算 ≤ 0、无可评分证据、Forbidden 为空
/// 3. 聚合：macro-mean 与 N/A 排除、比例指标、Wilson 95% 区间、不可评分样本计数
/// 4. 切片：固定四维、输出顺序确定、Overall 与切片一致
/// 5. 确定性：同一输入重复计算逐位一致，样本顺序无关
/// 6. 契约不变量：RankedEvidence 不含 FinalScore/结果信号字段
/// </summary>
[TestClass]
[TestCategory("LR0E")]
public sealed class QualityMetricsContractTests
{
    private const double Tolerance = 1e-6;

    // =========================================================================
    // 1. 公式
    // =========================================================================

    [TestMethod]
    public void RecallAtK_HitsInTopK_OverRequired()
    {
        var sample = QualityMetrics.EvaluateSample(
            "s1",
            Required("a", "b", "c"),
            Ranked(("a", 0, 0), ("x", 0, 0), ("b", 0, 0), ("y", 0, 0), ("c", 0, 0)),
            k: 3,
            tokenBudget: 4000);

        Assert.AreEqual(3, sample.RequiredCount);
        Assert.AreEqual(2.0 / 3.0, sample.RecallAtK!.Value, Tolerance, "top-3 应命中 a 与 b。");
    }

    [TestMethod]
    public void RecallAtK_KBeyondResult_AllRequiredRecalled()
    {
        var sample = QualityMetrics.EvaluateSample(
            "s2",
            Required("a", "b"),
            Ranked(("a", 0, 0), ("x", 0, 0), ("b", 0, 0)),
            k: 10,
            tokenBudget: 4000);

        Assert.AreEqual(1.0, sample.RecallAtK!.Value, Tolerance);
    }

    [TestMethod]
    public void RecallAtTokenBudget_AccumulatesPrefix_UntilBudgetExhausted()
    {
        // 预算 1000：a(300) + x(200) = 500，下一条 b(600) 会使 1100 > 1000，b 不含入 → 命中 1/2。
        var sample = QualityMetrics.EvaluateSample(
            "s3",
            Required("a", "b"),
            Ranked(("a", 300, 0), ("x", 200, 0), ("b", 600, 0), ("y", 100, 0)),
            k: 10,
            tokenBudget: 1000);

        Assert.AreEqual(0.5, sample.RecallAtTokenBudget!.Value, Tolerance, "超预算的最后一条不应计入前缀。");
    }

    [TestMethod]
    public void RecallAtTokenBudget_BudgetFitsExact_IncludesLastItem()
    {
        // 预算 1200：a(300)+x(200)+b(600)+y(100) = 1200 恰好耗尽，全部含入 → 命中 2/2。
        var sample = QualityMetrics.EvaluateSample(
            "s4",
            Required("a", "b"),
            Ranked(("a", 300, 0), ("x", 200, 0), ("b", 600, 0), ("y", 100, 0)),
            k: 10,
            tokenBudget: 1200);

        Assert.AreEqual(1.0, sample.RecallAtTokenBudget!.Value, Tolerance);
    }

    [TestMethod]
    public void PrecisionAtK_PositiveIsRequiredUnionRelevant_ForbiddenDoesNotCount()
    {
        // 正相关 = {a, b}；top-3 = [a, x, b] → 2/3。x 是 Forbidden，但不影响精度。
        var sample = QualityMetrics.EvaluateSample(
            "s5",
            new QualityEvidenceExpectation
            {
                RequiredEvidenceIds = ["a"],
                RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "b", Grade = 2 }],
                ForbiddenExcludedIds = ["x"]
            },
            Ranked(("a", 0, 0), ("x", 0, 0), ("b", 0, 0), ("y", 0, 0)),
            k: 3,
            tokenBudget: 4000);

        Assert.AreEqual(2.0 / 3.0, sample.PrecisionAtK!.Value, Tolerance);
        Assert.IsTrue(sample.ForbiddenInResult, "x 在 top-3 中应记为禁止命中。");
    }

    [TestMethod]
    public void Mrr_FirstRequiredRankInverse_NoneIsZero()
    {
        var hit = QualityMetrics.EvaluateSample(
            "s6",
            Required("b", "c"),
            Ranked(("a", 0, 0), ("b", 0, 0), ("c", 0, 0)),
            k: 10,
            tokenBudget: 4000);
        Assert.AreEqual(0.5, hit.Mrr!.Value, Tolerance, "首个 Required 在 rank 2。");

        var miss = QualityMetrics.EvaluateSample(
            "s7",
            Required("z"),
            Ranked(("a", 0, 0), ("b", 0, 0)),
            k: 10,
            tokenBudget: 4000);
        Assert.AreEqual(0.0, miss.Mrr!.Value, Tolerance, "无命中时 MRR 为 0。");
    }

    [TestMethod]
    public void NdcgAtK_IdealOrderIsOne_PartialOrderBelowOne()
    {
        // Required {a}=3，Relevant {b}=1；ranked=[a,b] 与理想排序一致 → 1.0。
        var ideal = QualityMetrics.EvaluateSample(
            "s8",
            new QualityEvidenceExpectation
            {
                RequiredEvidenceIds = ["a"],
                RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "b", Grade = 1 }]
            },
            Ranked(("a", 0, 0), ("b", 0, 0)),
            k: 2,
            tokenBudget: 4000);
        Assert.AreEqual(1.0, ideal.NdcgAtK!.Value, Tolerance);

        // ranked=[b,a]：DCG = 1/log2(2) + 7/log2(3)；IDCG = 7/log2(2) + 1/log2(3)。
        var partial = QualityMetrics.EvaluateSample(
            "s9",
            new QualityEvidenceExpectation
            {
                RequiredEvidenceIds = ["a"],
                RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "b", Grade = 1 }]
            },
            Ranked(("b", 0, 0), ("a", 0, 0)),
            k: 2,
            tokenBudget: 4000);
        var dcg = 1.0 / Math.Log2(2) + 7.0 / Math.Log2(3);
        var idcg = 7.0 / Math.Log2(2) + 1.0 / Math.Log2(3);
        Assert.AreEqual(dcg / idcg, partial.NdcgAtK!.Value, Tolerance);
    }

    [TestMethod]
    public void NdcgAtK_MissingRelevantEvidence_LowersScore()
    {
        // Required {a, b}，结果只召回 a：IDCG 基于全部正相关证据（含未召回的 b）——
        // DCG = 7，IDCG = 7 + 7/log2(3)，未召回证据拉低 nDCG（召回导向）。
        var sample = QualityMetrics.EvaluateSample(
            "s9b",
            Required("a", "b"),
            Ranked(("a", 0, 0), ("x", 0, 0)),
            k: 10,
            tokenBudget: 4000);

        var expected = 7.0 / (7.0 + 7.0 / Math.Log2(3));
        Assert.AreEqual(expected, sample.NdcgAtK!.Value, Tolerance);
        Assert.IsTrue(sample.NdcgAtK < 1.0, "未召回正相关证据时 nDCG 应低于 1。");
    }

    // =========================================================================
    // 2. 空集合语义
    // =========================================================================

    [TestMethod]
    public void RequiredEmpty_RecallAndMrrAreNull_PrecisionAndNdcgStillComputed()
    {
        var sample = QualityMetrics.EvaluateSample(
            "s10",
            new QualityEvidenceExpectation
            {
                RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "b", Grade = 2 }]
            },
            Ranked(("b", 0, 0)),
            k: 10,
            tokenBudget: 4000);

        Assert.IsNull(sample.RecallAtK, "Required 为空时 Recall@K 应为 N/A。");
        Assert.IsNull(sample.RecallAtTokenBudget, "Required 为空时 Recall@TokenBudget 应为 N/A。");
        Assert.IsNull(sample.Mrr, "Required 为空时 MRR 应为 N/A。");
        Assert.IsFalse(sample.KeyEvidenceMissed, "Required 为空时不应记为关键证据漏失。");
        Assert.AreEqual(0.1, sample.PrecisionAtK!.Value, Tolerance);
        Assert.AreEqual(1.0, sample.NdcgAtK!.Value, Tolerance);
        Assert.IsTrue(sample.Scorable, "仅有 Relevant 的样本仍可评分。");
    }

    [TestMethod]
    public void NoPositiveEvidence_NotScorable()
    {
        var sample = QualityMetrics.EvaluateSample(
            "s11",
            new QualityEvidenceExpectation(),
            Ranked(("x", 0, 0)),
            k: 10,
            tokenBudget: 4000);

        Assert.IsFalse(sample.Scorable, "Required 与 Relevant 均为空时样本不可评分。");
        Assert.IsNull(sample.RecallAtK);
        Assert.IsNull(sample.Mrr);
        Assert.IsNull(sample.NdcgAtK);
    }

    [TestMethod]
    public void ZeroK_And_ZeroBudget_AreNA()
    {
        var zeroK = QualityMetrics.EvaluateSample(
            "s12",
            Required("a"),
            Ranked(("a", 0, 0)),
            k: 0,
            tokenBudget: 4000);
        Assert.IsNull(zeroK.RecallAtK, "K=0 时 Recall@K 为 N/A。");
        Assert.IsNull(zeroK.PrecisionAtK, "K=0 时 Precision@K 为 N/A。");
        Assert.IsNull(zeroK.NdcgAtK, "K=0 时 nDCG@K 为 N/A。");

        var zeroBudget = QualityMetrics.EvaluateSample(
            "s13",
            Required("a"),
            Ranked(("a", 0, 0)),
            k: 10,
            tokenBudget: 0);
        Assert.IsNull(zeroBudget.RecallAtTokenBudget, "预算为 0 时 Recall@TokenBudget 为 N/A。");
    }

    [TestMethod]
    public void ForbiddenEmpty_NoViolationFlag()
    {
        var sample = QualityMetrics.EvaluateSample(
            "s14",
            Required("a"),
            Ranked(("a", 0, 0), ("x", 0, 0)),
            k: 10,
            tokenBudget: 4000);
        Assert.IsFalse(sample.ForbiddenInResult, "Forbidden 列表为空时不应有禁止命中。");
    }

    [TestMethod]
    public void DuplicateId_RequiredAndRelevant_TreatedAsRequiredGrade3()
    {
        var sample = QualityMetrics.EvaluateSample(
            "s15",
            new QualityEvidenceExpectation
            {
                RequiredEvidenceIds = ["a"],
                RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "a", Grade = 1 }]
            },
            Ranked(("a", 0, 0), ("b", 0, 0)),
            k: 2,
            tokenBudget: 4000);

        Assert.AreEqual(1, sample.RequiredCount);
        Assert.AreEqual(1, sample.RelevantCount, "同一 ID 在 Relevant 中仍计数，但等级取 Required=3。");
        Assert.AreEqual(1.0, sample.NdcgAtK!.Value, Tolerance, "等级取 3 时理想排序即 [a]，NDCG=1。");
    }

    // =========================================================================
    // 3. 聚合
    // =========================================================================

    [TestMethod]
    public void Aggregate_MacroMean_ExcludesNA_CountsUnscorable()
    {
        var s1 = QualityMetrics.EvaluateSample("a1", Required("r1", "r2"), Ranked(("r1", 0, 0), ("x", 0, 0)), 10, 4000); // recall 0.5，漏失
        var s2 = QualityMetrics.EvaluateSample("a2", Required("r3"), Ranked(("r3", 0, 0)), 10, 4000); // recall 1.0
        var s3 = QualityMetrics.EvaluateSample("a3", new QualityEvidenceExpectation { RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "b", Grade = 1 }] }, Ranked(("b", 0, 0)), 10, 4000); // recall N/A
        var s4 = QualityMetrics.EvaluateSample("a4", new QualityEvidenceExpectation(), Ranked(("x", 0, 0)), 10, 4000); // 不可评分

        var aggregate = QualityMetrics.Aggregate([s1, s2, s3, s4]);

        Assert.AreEqual(3, aggregate.SampleCount);
        Assert.AreEqual(1, aggregate.UnscorableCount);
        Assert.AreEqual(2, aggregate.RequiredNonEmptyCount);
        Assert.AreEqual(0.75, aggregate.RecallAtKMean!.Value, Tolerance, "macro-mean 只对非 N/A 样本求均值。");
        Assert.AreEqual(0.5, aggregate.KeyEvidenceMissRate, Tolerance, "Required 非空 2 个中 1 个漏失。");
        Assert.IsNotNull(aggregate.KeyEvidenceMissInterval);
        Assert.AreEqual(0.0, aggregate.ForbiddenHitRate, Tolerance);
        Assert.IsNotNull(aggregate.ForbiddenHitInterval, "样本数 ≥ 1 时区间非 null；0 命中时为 [0, upper]。");
        Assert.AreEqual(0.0, aggregate.ForbiddenHitInterval!.Value.Lower, Tolerance);
        Assert.IsTrue(aggregate.ForbiddenHitInterval!.Value.Upper > 0, "0 命中时 Wilson 上界应大于 0。");
    }

    [TestMethod]
    public void Aggregate_EmptyInput_AllDefaults()
    {
        var aggregate = QualityMetrics.Aggregate(Array.Empty<SampleQualityMetrics>());

        Assert.AreEqual(0, aggregate.SampleCount);
        Assert.AreEqual(0, aggregate.RequiredNonEmptyCount);
        Assert.IsNull(aggregate.RecallAtKMean);
        Assert.AreEqual(0.0, aggregate.KeyEvidenceMissRate, Tolerance);
        Assert.IsNull(aggregate.KeyEvidenceMissInterval);
    }

    [TestMethod]
    public void WilsonInterval_SpotCheck_And_ZeroDenominator()
    {
        // hits=1, total=2：p=0.5, n=2, z=1.96 → 区间约 [0.0945, 0.9055]。
        var interval = QualityMetrics.WilsonInterval(1, 2);
        Assert.IsNotNull(interval);
        Assert.AreEqual(0.094531, interval!.Value.Lower, 1e-4);
        Assert.AreEqual(0.905469, interval!.Value.Upper, 1e-4);

        Assert.IsNull(QualityMetrics.WilsonInterval(0, 0), "分母为 0 时区间为 null。");
    }

    // =========================================================================
    // 4. 切片
    // =========================================================================

    [TestMethod]
    public void AggregateBySlice_FixedDims_SortedOutput_OverallMatches()
    {
        var keyA = new QualitySliceKey { Dataset = "d1", Mode = "ChatMode", Provider = "InMemory", QueryCountBucket = "1" };
        var keyB = new QualitySliceKey { Dataset = "d1", Mode = "ChatMode", Provider = "FileSystem", QueryCountBucket = "4" };

        var a1 = QualityMetrics.EvaluateSample("b1", Required("r1"), Ranked(("r1", 0, 0)), 10, 4000);
        var a2 = QualityMetrics.EvaluateSample("b2", Required("r2"), Ranked(("x", 0, 0)), 10, 4000);
        var b1 = QualityMetrics.EvaluateSample("b3", Required("r3"), Ranked(("r3", 0, 0)), 10, 4000);

        var result = QualityMetrics.AggregateBySlice(
        [
            (keyB, new List<SampleQualityMetrics> { b1 }),
            (keyA, new List<SampleQualityMetrics> { a1, a2 })
        ]);

        Assert.AreEqual(2, result.Slices.Count);
        Assert.AreEqual("FileSystem", result.Slices[0].Key.Provider, "输出应按规范化键排序（FileSystem 先于 InMemory）。");
        Assert.AreEqual("InMemory", result.Slices[1].Key.Provider);
        Assert.AreEqual(0.5, result.Slices[1].Aggregate.RecallAtKMean!.Value, Tolerance, "InMemory 切片内 macro-mean。");
        Assert.AreEqual(3, result.Overall.SampleCount, "Overall 应跨切片聚合全部可评分样本。");
        Assert.AreEqual(2.0 / 3.0, result.Overall.RecallAtKMean!.Value, Tolerance);
    }

    // =========================================================================
    // 5. 确定性
    // =========================================================================

    [TestMethod]
    public void SameInput_RepeatedEvaluation_IsIdentical()
    {
        var expectation = new QualityEvidenceExpectation
        {
            RequiredEvidenceIds = ["a", "b"],
            RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "c", Grade = 2 }],
            ForbiddenExcludedIds = ["x"]
        };
        var ranked = Ranked(("b", 100, 0), ("c", 200, 0), ("x", 50, 0), ("a", 300, 0), ("y", 400, 0));

        var first = QualityMetrics.EvaluateSample("d1", expectation, ranked, 4, 800);
        var second = QualityMetrics.EvaluateSample("d1", expectation, ranked, 4, 800);

        Assert.AreEqual(first.RecallAtK, second.RecallAtK);
        Assert.AreEqual(first.RecallAtTokenBudget, second.RecallAtTokenBudget);
        Assert.AreEqual(first.PrecisionAtK, second.PrecisionAtK);
        Assert.AreEqual(first.Mrr, second.Mrr);
        Assert.AreEqual(first.NdcgAtK, second.NdcgAtK);
        Assert.AreEqual(first.KeyEvidenceMissed, second.KeyEvidenceMissed);
        Assert.AreEqual(first.ForbiddenInResult, second.ForbiddenInResult);
    }

    [TestMethod]
    public void Aggregate_SampleOrderIndependent_And_IdenticalOnRepeat()
    {
        var samples = new[]
        {
            QualityMetrics.EvaluateSample("e1", Required("r1", "r2"), Ranked(("r1", 0, 0), ("x", 0, 0)), 10, 4000),
            QualityMetrics.EvaluateSample("e2", Required("r3"), Ranked(("r3", 0, 0)), 10, 4000)
        };
        var reversed = samples.Reverse().ToArray();

        var forward = QualityMetrics.Aggregate(samples);
        var backward = QualityMetrics.Aggregate(reversed);
        var repeat = QualityMetrics.Aggregate(samples);

        Assert.AreEqual(forward.RecallAtKMean, backward.RecallAtKMean);
        Assert.AreEqual(forward.RecallAtKMean, repeat.RecallAtKMean);
        Assert.AreEqual(forward.KeyEvidenceMissRate, backward.KeyEvidenceMissRate);
        Assert.AreEqual(forward.KeyEvidenceMissInterval, backward.KeyEvidenceMissInterval);
    }

    // =========================================================================
    // 6. 契约不变量
    // =========================================================================

    [TestMethod]
    public void RankedEvidence_HasNoFinalScoreOrResultSignalFields()
    {
        var properties = typeof(RankedEvidence)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "EvidenceId", "RelevanceGrade", "TokenCount" }, properties,
            "RankedEvidence 只承载证据身份、排序相关等级与 token 成本，不得携带 FinalScore 或结果信号字段。");
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static QualityEvidenceExpectation Required(params string[] ids) =>
        new() { RequiredEvidenceIds = ids };

    private static RankedEvidence[] Ranked(params (string Id, int Tokens, int Grade)[] items) =>
        items.Select(i => new RankedEvidence { EvidenceId = i.Id, TokenCount = i.Tokens, RelevanceGrade = i.Grade }).ToArray();
}
