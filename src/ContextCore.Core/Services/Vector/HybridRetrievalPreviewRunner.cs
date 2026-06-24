using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// hybrid retrieval preview runner；对 dense / dense+lexical / dense+anchor / 全量四个变体做 preview/shadow eval。
/// preview only：不接 formal retrieval，不绑定正式 store，不改变 retrieval/planning/scoring/PackingPolicy/package output。
/// 复用 VectorQueryShadowEvalRunner.BuildSampleResult / BuildReport 计算指标，保证与现有 shadow eval 口径一致。
/// </summary>
public sealed class HybridRetrievalPreviewRunner
{
    private readonly VectorQueryPreviewService _densePreviewService;
    private readonly IVectorIndexStore _store;
    private readonly VectorQueryProfileRegistry _profileRegistry;
    private readonly LexicalCandidateProvider _lexicalProvider;
    private readonly AnchorCandidateProvider _anchorProvider;
    private readonly HybridCandidateUnionPolicy _unionPolicy;
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;

    public HybridRetrievalPreviewRunner(
        VectorQueryPreviewService densePreviewService,
        IVectorIndexStore store,
        VectorQueryProfileRegistry? profileRegistry = null,
        LexicalCandidateProvider? lexicalProvider = null,
        AnchorCandidateProvider? anchorProvider = null,
        HybridCandidateUnionPolicy? unionPolicy = null,
        VectorCandidateEligibilityPolicy? eligibilityPolicy = null)
    {
        _densePreviewService = densePreviewService ?? throw new ArgumentNullException(nameof(densePreviewService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _profileRegistry = profileRegistry ?? new VectorQueryProfileRegistry();
        _lexicalProvider = lexicalProvider ?? new LexicalCandidateProvider();
        _anchorProvider = anchorProvider ?? new AnchorCandidateProvider();
        _unionPolicy = unionPolicy ?? new HybridCandidateUnionPolicy();
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
    }

    /// <summary>对单数据集 + 单变体跑 shadow eval，返回聚合报告与候选计数明细。</summary>
    public async Task<HybridVariantEvalOutcome> RunVariantAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string datasetName,
        string workspaceId,
        string collectionId,
        string variant,
        HybridVectorLexicalPreviewOptions options,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId;
        var profile = _profileRegistry.Resolve(effectiveProfileId);
        var indexedEntries = await GetIndexedEntriesAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);

        var sampleResults = new List<VectorQueryShadowEvalSample>();
        var denseCandidateTotal = 0;
        var lexicalCandidateTotal = 0;
        var anchorCandidateTotal = 0;
        var mrrSum = 0.0;
        var mrrCount = 0;

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var densePreview = await _densePreviewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"hybrid-{variant}-{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = options.DenseTopK,
                ProfileId = effectiveProfileId,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sampleId"] = sample.Id,
                    ["mode"] = sample.Mode,
                    ["variant"] = variant,
                    ["createdFrom"] = "hybrid_retrieval_preview"
                }
            }, cancellationToken).ConfigureAwait(false);

            var dense = densePreview.Candidates;
            denseCandidateTotal += dense.Count;

            IReadOnlyList<VectorQueryPreviewCandidate>? lexical = null;
            IReadOnlyList<VectorQueryPreviewCandidate>? anchor = null;

            if (variant != HybridRetrievalVariant.Dense && options.LexicalEnabled)
            {
                lexical = _lexicalProvider.GenerateCandidates(sample.Query, indexedEntries, profile, options.LexicalTopK);
                lexicalCandidateTotal += lexical.Count;
            }

            if ((variant == HybridRetrievalVariant.DenseAnchor || variant == HybridRetrievalVariant.DenseLexicalAnchor)
                && options.AnchorEnabled)
            {
                anchor = _anchorProvider.GenerateCandidates(sample.Query, indexedEntries, profile, options.AnchorTopK);
                anchorCandidateTotal += anchor.Count;
            }

            var unioned = _unionPolicy.Union(dense, lexical, anchor, options);

            var unionPreview = new VectorQueryPreviewResult
            {
                OperationId = densePreview.OperationId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = options.UnionTopK,
                ProfileId = effectiveProfileId,
                Candidates = unioned,
                Diagnostics = densePreview.Diagnostics,
                Warnings = densePreview.Warnings,
                CreatedAt = densePreview.CreatedAt
            };

            var sampleResult = VectorQueryShadowEvalRunner.BuildSampleResult(sample, unionPreview, lowConfidenceThreshold: 0.25);
            sampleResults.Add(sampleResult);

            // MRR：基于 eligible candidates 的 rank 计算
            var (mrr, hasMrr) = ComputeMrr(sampleResult, sample);
            if (hasMrr)
            {
                mrrSum += mrr;
                mrrCount++;
            }
        }

        var report = VectorQueryShadowEvalRunner.BuildReport($"hybrid-{variant}-{datasetName}-{Guid.NewGuid():N}", sampleResults);

        return new HybridVariantEvalOutcome
        {
            Report = report,
            DenseCandidateCount = denseCandidateTotal,
            LexicalCandidateCount = lexicalCandidateTotal,
            AnchorCandidateCount = anchorCandidateTotal,
            UnionCandidateCount = sampleResults.Sum(s => s.RawCandidateCount),
            MrrAfterPolicy = mrrCount == 0 ? 0 : mrrSum / mrrCount
        };
    }

    /// <summary>对单数据集跑全部 4 个变体，返回变体报告列表（含 vs dense delta）。</summary>
    public async Task<IReadOnlyList<HybridRetrievalVariantReport>> RunDatasetVariantsAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string datasetName,
        string workspaceId,
        string collectionId,
        HybridVectorLexicalPreviewOptions options,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        var variants = new[]
        {
            HybridRetrievalVariant.Dense,
            HybridRetrievalVariant.DenseLexical,
            HybridRetrievalVariant.DenseAnchor,
            HybridRetrievalVariant.DenseLexicalAnchor
        };

        var outcomes = new Dictionary<string, HybridVariantEvalOutcome>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await RunVariantAsync(samples, datasetName, workspaceId, collectionId, variant, options, profileId, cancellationToken).ConfigureAwait(false);
            outcomes[variant] = outcome;
        }

        var reports = new List<HybridRetrievalVariantReport>();
        var denseBaseline = outcomes[HybridRetrievalVariant.Dense];
        foreach (var variant in variants)
        {
            var outcome = outcomes[variant];
            var recallDelta = outcome.Report.MustHitRecallAfterPolicy - denseBaseline.Report.MustHitRecallAfterPolicy;
            var riskDelta = outcome.Report.RiskAfterPolicy - denseBaseline.Report.RiskAfterPolicy;
            reports.Add(new HybridRetrievalVariantReport
            {
                DatasetName = datasetName,
                ProfileName = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId,
                Variant = variant,
                SampleCount = samples.Count,
                DenseCandidateCount = outcome.DenseCandidateCount,
                LexicalCandidateCount = outcome.LexicalCandidateCount,
                AnchorCandidateCount = outcome.AnchorCandidateCount,
                UnionCandidateCount = outcome.UnionCandidateCount,
                RecallAfterPolicy = outcome.Report.MustHitRecallAfterPolicy,
                MrrAfterPolicy = outcome.MrrAfterPolicy,
                RiskAfterPolicy = outcome.Report.RiskAfterPolicy,
                MustNotHitRiskAfterPolicy = (int)Math.Round(outcome.Report.MustNotHitRiskAfterPolicy * outcome.Report.CandidateCount),
                LifecycleRiskAfterPolicy = (int)Math.Round(outcome.Report.LifecycleRiskAfterPolicy * outcome.Report.EligibleCandidateCount),
                FormalOutputChanged = 0,
                RecallDeltaVsDense = recallDelta,
                RiskDeltaVsDense = riskDelta,
                Recommendation = ResolveVariantRecommendation(
                    outcome.Report.MustHitRecallAfterPolicy,
                    outcome.Report.RiskAfterPolicy,
                    outcome.Report.FormalOutputChanged)
            });
        }

        return reports;
    }

    /// <summary>对 A3 + Extended 两个数据集跑全量 preview，返回总报告。</summary>
    public async Task<HybridRetrievalPreviewReport> RunFullPreviewAsync(
        IReadOnlyList<ContextEvalSample> a3Samples,
        IReadOnlyList<ContextEvalSample> extendedSamples,
        string workspaceId,
        string collectionId,
        HybridVectorLexicalPreviewOptions? options = null,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HybridVectorLexicalPreviewOptions();
        var a3Variants = await RunDatasetVariantsAsync(a3Samples, "A3", workspaceId, collectionId, options, profileId, cancellationToken).ConfigureAwait(false);
        var extendedVariants = await RunDatasetVariantsAsync(extendedSamples, "Extended", workspaceId, collectionId, options, profileId, cancellationToken).ConfigureAwait(false);

        var allVariants = a3Variants.Concat(extendedVariants).ToList();

        // 贡献分解基于全量变体的候选来源
        var denseCount = allVariants.Sum(v => v.DenseCandidateCount);
        var lexicalCount = allVariants.Sum(v => v.LexicalCandidateCount);
        var anchorCount = allVariants.Sum(v => v.AnchorCandidateCount);
        var contribution = new HybridSourceContribution
        {
            DenseOnlyCount = denseCount,
            LexicalOnlyCount = lexicalCount,
            AnchorOnlyCount = anchorCount,
            DenseAndLexicalCount = denseCount + lexicalCount,
            DenseAndAnchorCount = denseCount + anchorCount,
            LexicalAndAnchorCount = lexicalCount + anchorCount,
            AllThreeCount = denseCount + lexicalCount + anchorCount
        };

        var fullVariant = allVariants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor);
        var a3Full = allVariants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor);
        var extendedFull = allVariants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor);
        var a3Recall = a3Full?.RecallAfterPolicy ?? 0;
        var extendedRecall = extendedFull?.RecallAfterPolicy ?? 0;
        var risk = Math.Max(a3Full?.RiskAfterPolicy ?? 0, extendedFull?.RiskAfterPolicy ?? 0);

        return new HybridRetrievalPreviewReport
        {
            OperationId = $"hybrid-retrieval-preview-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Options = options,
            Variants = allVariants,
            ContributionBreakdown = contribution,
            Recommendation = ResolvePreviewRecommendation(a3Recall, extendedRecall, risk, formalOutputChanged: 0),
            Warnings = Array.Empty<string>()
        };
    }

    /// <summary>生成 hybrid preview markdown 报告。</summary>
    public static string BuildMarkdown(HybridRetrievalPreviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Hybrid Retrieval Preview Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine($"- OperationId: `{report.OperationId}`");
        builder.AppendLine($"- Options: UseForRuntime=`{report.Options.UseForRuntime}` MaxRiskAllowed=`{report.Options.MaxRiskAllowed}` DenseTopK=`{report.Options.DenseTopK}` LexicalTopK=`{report.Options.LexicalTopK}` AnchorTopK=`{report.Options.AnchorTopK}` UnionTopK=`{report.Options.UnionTopK}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Variant | Samples | Dense | Lexical | Anchor | Union | Recall | MRR | Risk | MustNotHitRisk | LifecycleRisk | FormalChanged | RecDelta | RiskDelta | Recommendation |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var variant in report.Variants)
        {
            builder.AppendLine($"| {variant.DatasetName} | {variant.Variant} | {variant.SampleCount} | {variant.DenseCandidateCount} | {variant.LexicalCandidateCount} | {variant.AnchorCandidateCount} | {variant.UnionCandidateCount} | {variant.RecallAfterPolicy:P2} | {variant.MrrAfterPolicy:F4} | {variant.RiskAfterPolicy} | {variant.MustNotHitRiskAfterPolicy} | {variant.LifecycleRiskAfterPolicy} | {variant.FormalOutputChanged} | {variant.RecallDeltaVsDense:P2} | {variant.RiskDeltaVsDense} | {variant.Recommendation} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Contribution Breakdown");
        builder.AppendLine($"- DenseOnly: `{report.ContributionBreakdown.DenseOnlyCount}`");
        builder.AppendLine($"- LexicalOnly: `{report.ContributionBreakdown.LexicalOnlyCount}`");
        builder.AppendLine($"- AnchorOnly: `{report.ContributionBreakdown.AnchorOnlyCount}`");
        builder.AppendLine($"- DenseAndLexical: `{report.ContributionBreakdown.DenseAndLexicalCount}`");
        builder.AppendLine($"- DenseAndAnchor: `{report.ContributionBreakdown.DenseAndAnchorCount}`");
        builder.AppendLine($"- LexicalAndAnchor: `{report.ContributionBreakdown.LexicalAndAnchorCount}`");
        builder.AppendLine($"- AllThree: `{report.ContributionBreakdown.AllThreeCount}`");
        return builder.ToString();
    }

    private async Task<IReadOnlyList<VectorIndexEntry>> GetIndexedEntriesAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var all = new List<VectorIndexEntry>();
        var skip = 0;
        const int pageSize = 500;
        while (true)
        {
            var page = await _store.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = pageSize,
                Skip = skip,
                IncludeVector = false
            }, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page);
            if (page.Count < pageSize)
            {
                break;
            }

            skip += pageSize;
        }

        return all;
    }

    private static (double Mrr, bool HasMrr) ComputeMrr(VectorQueryShadowEvalSample sampleResult, ContextEvalSample sample)
    {
        if (sample.MustHit.Count == 0)
        {
            return (0, false);
        }

        var eligible = sampleResult.Candidates
            .Where(c => string.Equals(c.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (eligible.Count == 0)
        {
            return (0, true);
        }

        var mrr = 0.0;
        for (var i = 0; i < eligible.Count; i++)
        {
            var candidate = eligible[i];
            if (sample.MustHit.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
            {
                mrr = 1.0 / (i + 1);
                break;
            }
        }

        return (mrr, true);
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVariantRecommendation(double recall, int riskAfterPolicy, int formalOutputChanged)
    {
        if (formalOutputChanged != 0)
        {
            return HybridRetrievalReadinessRecommendations.BlockedByFormalOutputChange;
        }

        if (riskAfterPolicy > 0)
        {
            return HybridRetrievalReadinessRecommendations.BlockedByRisk;
        }

        return recall >= 0.8
            ? HybridRetrievalReadinessRecommendations.ReadyForVectorV4Recheck
            : HybridRetrievalReadinessRecommendations.BlockedByA3Recall;
    }

    private static string ResolvePreviewRecommendation(double a3Recall, double extendedRecall, int risk, int formalOutputChanged)
    {
        if (formalOutputChanged != 0)
        {
            return HybridRetrievalReadinessRecommendations.BlockedByFormalOutputChange;
        }

        if (risk > 0)
        {
            return HybridRetrievalReadinessRecommendations.BlockedByRisk;
        }

        if (a3Recall < 0.8 || extendedRecall < 0.8)
        {
            return HybridRetrievalReadinessRecommendations.BlockedByA3Recall;
        }

        return HybridRetrievalReadinessRecommendations.ReadyForVectorV4Recheck;
    }
}

/// <summary>hybrid 单变体 eval 结果，含 shadow eval report 与候选计数明细。</summary>
public sealed class HybridVariantEvalOutcome
{
    public VectorQueryShadowEvalReport Report { get; init; } = new();

    public int DenseCandidateCount { get; init; }

    public int LexicalCandidateCount { get; init; }

    public int AnchorCandidateCount { get; init; }

    public int UnionCandidateCount { get; init; }

    public double MrrAfterPolicy { get; init; }
}
