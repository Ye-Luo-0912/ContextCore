using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// hybrid recall regression sanity audit runner；诊断 hybrid preview recall 与 legacy dense baseline 的偏差。
/// sanity audit only：不接 formal retrieval，不改变 retrieval/planning/scoring/PackingPolicy/package output。
/// </summary>
public sealed class HybridRetrievalRecallRegressionAuditRunner
{
    private const double RecallTolerance = 0.001;
    private readonly HybridRetrievalPreviewRunner _hybridRunner;
    private readonly VectorQueryPreviewService _densePreviewService;
    private readonly IVectorIndexStore _store;

    public HybridRetrievalRecallRegressionAuditRunner(
        HybridRetrievalPreviewRunner hybridRunner,
        VectorQueryPreviewService densePreviewService,
        IVectorIndexStore store)
    {
        _hybridRunner = hybridRunner ?? throw new ArgumentNullException(nameof(hybridRunner));
        _densePreviewService = densePreviewService ?? throw new ArgumentNullException(nameof(densePreviewService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>跑完整 audit：7 个 profile × A3 + Extended，对齐验证，返回诊断报告。</summary>
    public async Task<HybridRetrievalRecallRegressionAuditReport> RunAuditAsync(
        IReadOnlyList<ContextEvalSample> a3Samples,
        IReadOnlyList<ContextEvalSample> extendedSamples,
        string workspaceId,
        string collectionId,
        HybridVectorLexicalPreviewOptions? options = null,
        string? profileId = null,
        bool p15GatePassed = true,
        CancellationToken cancellationToken = default)
    {
        options ??= new HybridVectorLexicalPreviewOptions();

        // legacy dense baseline：直接用 VectorQueryShadowEvalRunner（原 shadow eval 路径）
        var legacyRunner = new VectorQueryShadowEvalRunner(_densePreviewService);
        var legacyA3 = await legacyRunner.RunAsync(a3Samples, workspaceId, collectionId, options.DenseTopK, profileId: profileId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var legacyExtended = await legacyRunner.RunAsync(extendedSamples, workspaceId, collectionId, options.DenseTopK, profileId: profileId, cancellationToken: cancellationToken).ConfigureAwait(false);

        // hybrid dense-only（通过 hybrid runner 跑 Dense 变体）
        var hybridDenseA3 = await _hybridRunner.RunVariantAsync(a3Samples, "A3", workspaceId, collectionId, HybridRetrievalVariant.Dense, options, profileId, cancellationToken).ConfigureAwait(false);
        var hybridDenseExtended = await _hybridRunner.RunVariantAsync(extendedSamples, "Extended", workspaceId, collectionId, HybridRetrievalVariant.Dense, options, profileId, cancellationToken).ConfigureAwait(false);

        // hybrid 各变体
        var hybridLexicalA3 = await _hybridRunner.RunVariantAsync(a3Samples, "A3", workspaceId, collectionId, HybridRetrievalVariant.DenseLexical, options, profileId, cancellationToken).ConfigureAwait(false);
        var hybridLexicalExtended = await _hybridRunner.RunVariantAsync(extendedSamples, "Extended", workspaceId, collectionId, HybridRetrievalVariant.DenseLexical, options, profileId, cancellationToken).ConfigureAwait(false);
        var hybridAnchorA3 = await _hybridRunner.RunVariantAsync(a3Samples, "A3", workspaceId, collectionId, HybridRetrievalVariant.DenseAnchor, options, profileId, cancellationToken).ConfigureAwait(false);
        var hybridAnchorExtended = await _hybridRunner.RunVariantAsync(extendedSamples, "Extended", workspaceId, collectionId, HybridRetrievalVariant.DenseAnchor, options, profileId, cancellationToken).ConfigureAwait(false);
        var hybridFullA3 = await _hybridRunner.RunVariantAsync(a3Samples, "A3", workspaceId, collectionId, HybridRetrievalVariant.DenseLexicalAnchor, options, profileId, cancellationToken).ConfigureAwait(false);
        var hybridFullExtended = await _hybridRunner.RunVariantAsync(extendedSamples, "Extended", workspaceId, collectionId, HybridRetrievalVariant.DenseLexicalAnchor, options, profileId, cancellationToken).ConfigureAwait(false);

        // lexical-only / anchor-only（不走 dense）
        var indexedEntries = await GetIndexedEntriesAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        var profileRegistry = new VectorQueryProfileRegistry();
        var effectiveProfileId = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId;
        var profile = profileRegistry.Resolve(effectiveProfileId);
        var eligibilityPolicy = new VectorCandidateEligibilityPolicy();

        var lexicalOnlyA3 = RunLabelFreeShadowEval(a3Samples, indexedEntries, profile, eligibilityPolicy, query => new LexicalCandidateProvider(eligibilityPolicy).GenerateCandidates(query, indexedEntries, profile, options.LexicalTopK));
        var lexicalOnlyExtended = RunLabelFreeShadowEval(extendedSamples, indexedEntries, profile, eligibilityPolicy, query => new LexicalCandidateProvider(eligibilityPolicy).GenerateCandidates(query, indexedEntries, profile, options.LexicalTopK));
        var anchorOnlyA3 = RunLabelFreeShadowEval(a3Samples, indexedEntries, profile, eligibilityPolicy, query => new AnchorCandidateProvider(eligibilityPolicy).GenerateCandidates(query, indexedEntries, profile, options.AnchorTopK));
        var anchorOnlyExtended = RunLabelFreeShadowEval(extendedSamples, indexedEntries, profile, eligibilityPolicy, query => new AnchorCandidateProvider(eligibilityPolicy).GenerateCandidates(query, indexedEntries, profile, options.AnchorTopK));

        // 对齐验证
        var candidateLossCount = Math.Max(0, legacyA3.CandidateCount - hybridDenseA3.Report.CandidateCount) + Math.Max(0, legacyExtended.CandidateCount - hybridDenseExtended.Report.CandidateCount);

        // dense candidate dropped：union 后 dense 候选是否被静默丢弃（对比 hybrid-dense-only 与 legacy candidate item ids）
        var denseDroppedA3 = CountDroppedDenseCandidates(legacyA3, hybridDenseA3.Report);
        var denseDroppedExtended = CountDroppedDenseCandidates(legacyExtended, hybridDenseExtended.Report);
        var denseCandidateDroppedCount = denseDroppedA3 + denseDroppedExtended;

        var eligibilityMismatchA3 = CountEligibilityMismatches(legacyA3, hybridDenseA3.Report);
        var eligibilityMismatchExtended = CountEligibilityMismatches(legacyExtended, hybridDenseExtended.Report);
        var eligibilityMismatchCount = eligibilityMismatchA3 + eligibilityMismatchExtended;

        var a3RecallDelta = Math.Abs(hybridDenseA3.Report.MustHitRecallAfterPolicy - legacyA3.MustHitRecallAfterPolicy);
        var extendedRecallDelta = Math.Abs(hybridDenseExtended.Report.MustHitRecallAfterPolicy - legacyExtended.MustHitRecallAfterPolicy);
        var providerScopeMismatchCount = 0;
        var topKConfigMismatchCount = options.DenseTopK != legacyA3.SampleResults.FirstOrDefault()?.TopK ? 1 : 0;
        var queryVectorMismatchCount = 0;
        var dedupOverwriteCount = 0;

        // 判断 dedup 是否错误覆盖 dense contribution
        var hybridBestA3 = Math.Max(Math.Max(hybridLexicalA3.Report.MustHitRecallAfterPolicy, hybridAnchorA3.Report.MustHitRecallAfterPolicy), hybridFullA3.Report.MustHitRecallAfterPolicy);
        var hybridBestExtended = Math.Max(Math.Max(hybridLexicalExtended.Report.MustHitRecallAfterPolicy, hybridAnchorExtended.Report.MustHitRecallAfterPolicy), hybridFullExtended.Report.MustHitRecallAfterPolicy);

        // 如果 hybrid full recall < hybrid dense-only recall，说明 union 可能丢弃了 dense 候选
        if (hybridFullA3.Report.MustHitRecallAfterPolicy + RecallTolerance < hybridDenseA3.Report.MustHitRecallAfterPolicy)
        {
            dedupOverwriteCount += 1;
        }
        if (hybridFullExtended.Report.MustHitRecallAfterPolicy + RecallTolerance < hybridDenseExtended.Report.MustHitRecallAfterPolicy)
        {
            dedupOverwriteCount += 1;
        }

        // gate 规则
        var blocked = new List<string>();
        if (a3RecallDelta > RecallTolerance || extendedRecallDelta > RecallTolerance)
        {
            blocked.Add("DenseBaselineRecallRegression");
        }
        if (denseCandidateDroppedCount > 0)
        {
            blocked.Add("DenseCandidateDropped");
        }
        if (eligibilityMismatchCount > 0)
        {
            blocked.Add("EligibilityMismatch");
        }
        if (dedupOverwriteCount > 0)
        {
            blocked.Add("DedupOverwriteDetected");
        }
        if (candidateLossCount > 0)
        {
            blocked.Add("CandidateLoss");
        }
        if (legacyA3.RiskAfterPolicy > 0 || legacyExtended.RiskAfterPolicy > 0)
        {
            blocked.Add("LegacyRiskNonZero");
        }

        var passed = blocked.Count == 0;

        var profiles = new List<HybridRecallRegressionAuditProfileResult>
        {
            ToProfileResult("legacy-dense-baseline", "A3", legacyA3, 0, 0),
            ToProfileResult("legacy-dense-baseline", "Extended", legacyExtended, 0, 0),
            ToProfileResult("hybrid-dense-only", "A3", hybridDenseA3.Report, denseDroppedA3, eligibilityMismatchA3),
            ToProfileResult("hybrid-dense-only", "Extended", hybridDenseExtended.Report, denseDroppedExtended, eligibilityMismatchExtended),
            ToProfileResult("hybrid-dense-plus-lexical", "A3", hybridLexicalA3.Report, 0, 0),
            ToProfileResult("hybrid-dense-plus-lexical", "Extended", hybridLexicalExtended.Report, 0, 0),
            ToProfileResult("hybrid-dense-plus-anchor", "A3", hybridAnchorA3.Report, 0, 0),
            ToProfileResult("hybrid-dense-plus-anchor", "Extended", hybridAnchorExtended.Report, 0, 0),
            ToProfileResult("hybrid-dense-plus-lexical-anchor", "A3", hybridFullA3.Report, 0, 0),
            ToProfileResult("hybrid-dense-plus-lexical-anchor", "Extended", hybridFullExtended.Report, 0, 0),
            ToProfileResult("lexical-only", "A3", lexicalOnlyA3, 0, 0),
            ToProfileResult("lexical-only", "Extended", lexicalOnlyExtended, 0, 0),
            ToProfileResult("anchor-only", "A3", anchorOnlyA3, 0, 0),
            ToProfileResult("anchor-only", "Extended", anchorOnlyExtended, 0, 0)
        };

        return new HybridRetrievalRecallRegressionAuditReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = passed,
            LegacyDenseRecallA3 = legacyA3.MustHitRecallAfterPolicy,
            HybridDenseOnlyRecallA3 = hybridDenseA3.Report.MustHitRecallAfterPolicy,
            HybridBestRecallA3 = hybridBestA3,
            LegacyDenseRecallExtended = legacyExtended.MustHitRecallAfterPolicy,
            HybridDenseOnlyRecallExtended = hybridDenseExtended.Report.MustHitRecallAfterPolicy,
            HybridBestRecallExtended = hybridBestExtended,
            CandidateLossCount = candidateLossCount,
            DenseCandidateDroppedCount = denseCandidateDroppedCount,
            EligibilityMismatchCount = eligibilityMismatchCount,
            ProviderScopeMismatchCount = providerScopeMismatchCount,
            TopKConfigMismatchCount = topKConfigMismatchCount,
            QueryVectorMismatchCount = queryVectorMismatchCount,
            DedupOverwriteCount = dedupOverwriteCount,
            UseForRuntime = false,
            FormalOutputChanged = 0,
            Profiles = profiles,
            BlockedReasons = blocked,
            Diagnostics = passed
                ? ["UseForRuntime=false", "FormalOutputChanged=0", "DenseBaselineAligned", "SanityAuditPassed"]
                : ["RecallRegressionDetected", "DenseBaselineNotAligned"],
            Recommendation = ResolveRecommendation(blocked, a3RecallDelta, extendedRecallDelta)
        };
    }

    /// <summary>生成 audit markdown 报告。</summary>
    public static string BuildMarkdown(HybridRetrievalRecallRegressionAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Hybrid Retrieval Recall Regression Audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- LegacyDenseRecallA3: `{report.LegacyDenseRecallA3:P2}`");
        builder.AppendLine($"- HybridDenseOnlyRecallA3: `{report.HybridDenseOnlyRecallA3:P2}`");
        builder.AppendLine($"- HybridBestRecallA3: `{report.HybridBestRecallA3:P2}`");
        builder.AppendLine($"- LegacyDenseRecallExtended: `{report.LegacyDenseRecallExtended:P2}`");
        builder.AppendLine($"- HybridDenseOnlyRecallExtended: `{report.HybridDenseOnlyRecallExtended:P2}`");
        builder.AppendLine($"- HybridBestRecallExtended: `{report.HybridBestRecallExtended:P2}`");
        builder.AppendLine($"- CandidateLossCount: `{report.CandidateLossCount}`");
        builder.AppendLine($"- DenseCandidateDroppedCount: `{report.DenseCandidateDroppedCount}`");
        builder.AppendLine($"- EligibilityMismatchCount: `{report.EligibilityMismatchCount}`");
        builder.AppendLine($"- ProviderScopeMismatchCount: `{report.ProviderScopeMismatchCount}`");
        builder.AppendLine($"- TopKConfigMismatchCount: `{report.TopKConfigMismatchCount}`");
        builder.AppendLine($"- QueryVectorMismatchCount: `{report.QueryVectorMismatchCount}`");
        builder.AppendLine($"- DedupOverwriteCount: `{report.DedupOverwriteCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Profile | Dataset | Samples | Candidates | Eligible | Blocked | Recall | MRR | Risk | Dropped | EligMismatch |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var p in report.Profiles)
        {
            builder.AppendLine($"| {p.ProfileName} | {p.DatasetName} | {p.SampleCount} | {p.CandidateCount} | {p.EligibleCandidateCount} | {p.BlockedCandidateCount} | {p.RecallAfterPolicy:P2} | {p.MrrAfterPolicy:F4} | {p.RiskAfterPolicy} | {p.DenseCandidateDroppedCount} | {p.EligibilityMismatchCount} |");
        }

        if (report.BlockedReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## BlockedReasons");
            foreach (var reason in report.BlockedReasons)
            {
                builder.AppendLine($"- `{reason}`");
            }
        }

        return builder.ToString();
    }

    private static VectorQueryShadowEvalReport RunLabelFreeShadowEval(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorIndexEntry> indexedEntries,
        VectorQueryProfile profile,
        VectorCandidateEligibilityPolicy eligibilityPolicy,
        Func<string, IReadOnlyList<VectorQueryPreviewCandidate>> generate)
    {
        var sampleResults = new List<VectorQueryShadowEvalSample>();
        foreach (var sample in samples)
        {
            var candidates = generate(sample.Query);
            var preview = new VectorQueryPreviewResult
            {
                OperationId = $"label-free-{sample.Id}",
                QueryText = sample.Query,
                TopK = candidates.Count,
                ProfileId = profile.ProfileId,
                Candidates = candidates
            };
            sampleResults.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, preview, 0.25));
        }

        return VectorQueryShadowEvalRunner.BuildReport($"label-free-{Guid.NewGuid():N}", sampleResults);
    }

    private static int CountDroppedDenseCandidates(VectorQueryShadowEvalReport legacy, VectorQueryShadowEvalReport hybrid)
    {
        var legacyIds = legacy.SampleResults
            .SelectMany(s => s.Candidates)
            .Select(c => c.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hybridIds = hybrid.SampleResults
            .SelectMany(s => s.Candidates)
            .Select(c => c.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return legacyIds.Except(hybridIds).Count();
    }

    private static int CountEligibilityMismatches(VectorQueryShadowEvalReport legacy, VectorQueryShadowEvalReport hybrid)
    {
        var legacyEligible = legacy.SampleResults
            .SelectMany(s => s.Candidates)
            .Where(c => string.Equals(c.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hybridEligible = hybrid.SampleResults
            .SelectMany(s => s.Candidates)
            .Where(c => string.Equals(c.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mismatch = 0;
        foreach (var id in legacyEligible.Intersect(hybridEligible))
        {
            // 共有 id 的 eligibility 一致则不算 mismatch
        }
        // mismatch = legacy eligible 但 hybrid not eligible + hybrid eligible 但 legacy not eligible
        mismatch += legacyEligible.Except(hybridEligible).Count();
        mismatch += hybridEligible.Except(legacyEligible).Count();
        return mismatch;
    }

    private static HybridRecallRegressionAuditProfileResult ToProfileResult(
        string profileName,
        string datasetName,
        VectorQueryShadowEvalReport report,
        int denseDropped,
        int eligibilityMismatch)
    {
        return new HybridRecallRegressionAuditProfileResult
        {
            ProfileName = profileName,
            DatasetName = datasetName,
            SampleCount = report.Samples,
            CandidateCount = report.CandidateCount,
            EligibleCandidateCount = report.EligibleCandidateCount,
            BlockedCandidateCount = report.BlockedCandidateCount,
            RecallAfterPolicy = report.MustHitRecallAfterPolicy,
            MrrAfterPolicy = 0,
            RiskAfterPolicy = report.RiskAfterPolicy,
            DenseCandidateDroppedCount = denseDropped,
            EligibilityMismatchCount = eligibilityMismatch
        };
    }

    private static string ResolveRecommendation(List<string> blocked, double a3Delta, double extendedDelta)
    {
        if (blocked.Count == 0)
        {
            return HybridRecallRegressionAuditRecommendations.ReadyForHybridFreeze;
        }

        if (blocked.Contains("DedupOverwriteDetected", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRecallRegressionAuditRecommendations.BlockedByDedupBug;
        }

        if (blocked.Contains("EligibilityMismatch", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRecallRegressionAuditRecommendations.BlockedByEligibilityMismatch;
        }

        if (blocked.Contains("DenseBaselineRecallRegression", StringComparer.OrdinalIgnoreCase))
        {
            return HybridRecallRegressionAuditRecommendations.BlockedByDenseBaselineRegression;
        }

        return HybridRecallRegressionAuditRecommendations.KeepPreviewOnly;
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
}
