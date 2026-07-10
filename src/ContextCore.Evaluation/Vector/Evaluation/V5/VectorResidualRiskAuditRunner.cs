using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>向量 residual risk 离线审计 runner；只解释 shadow 风险，不接正式检索。</summary>
public sealed class VectorResidualRiskAuditRunner
{
    private const double LowMarginThreshold = 0.03;
    private const double LooseSimilarityThreshold = 0.65;

    private readonly VectorQueryPreviewService _previewService;

    public VectorResidualRiskAuditRunner(VectorQueryPreviewService previewService)
    {
        _previewService = previewService;
    }

    public async Task<VectorResidualRiskAuditReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK = 10,
        string? profileId = null,
        double? minSimilarity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var operationId = $"vector-residual-risk-audit-{Guid.NewGuid():N}";
        var sampleResults = new List<VectorQueryShadowEvalSample>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = topK,
                ProfileId = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_residual_risk_audit"
                }
            }, cancellationToken).ConfigureAwait(false);

            sampleResults.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, preview, 0.25));
        }

        return BuildReport(
            operationId,
            sampleResults,
            string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId);
    }

    public static VectorResidualRiskAuditReport BuildReport(
        string operationId,
        IReadOnlyList<VectorQueryShadowEvalSample> samples,
        string profileId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(samples);

        var details = samples
            .SelectMany(sample => BuildDetails(sample, profileId))
            .ToArray();
        var firstCandidate = samples
            .SelectMany(sample => sample.Candidates)
            .FirstOrDefault();
        var riskByType = details
            .GroupBy(item => item.RiskType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var beforeRepairRisk = samples.Sum(sample => sample.RiskBeforePolicy);
        var afterRepairRisk = samples.Sum(sample => sample.RiskAfterPolicy);
        var blockedByLifecycleGate = CountLifecycleGateBlocks(samples);
        var mustHitTotal = samples.Sum(sample => sample.MustHitCount);
        var mustHitBefore = samples.Sum(sample => sample.MustHitHitCountBeforePolicy);
        var mustHitAfter = samples.Sum(sample => sample.MustHitHitCountAfterPolicy);
        var mrr = CalculateMrr(samples);
        var marginAverage = details.Length == 0
            ? 0
            : details.Average(item => item.SimilarityMargin);

        return new VectorResidualRiskAuditReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            ProviderId = firstCandidate?.EmbeddingProvider ?? string.Empty,
            EmbeddingModel = firstCandidate?.EmbeddingModel ?? string.Empty,
            ProfileId = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId,
            ResidualRiskCount = details.Length,
            BeforeRepairRiskCount = beforeRepairRisk,
            AfterRepairRiskCount = afterRepairRisk,
            BlockedByLifecycleMetadataGate = blockedByLifecycleGate,
            RemainingRiskTypes = riskByType,
            Risks = details,
            RiskAfterPolicyByType = riskByType,
            RecallLossAfterRepair = mustHitTotal == 0 ? 0 : Math.Max(0, (double)(mustHitBefore - mustHitAfter) / mustHitTotal),
            MustHitRecallAfterPolicy = mustHitTotal == 0 ? 1.0 : (double)mustHitAfter / mustHitTotal,
            MustHitMrrAfterPolicy = mrr,
            NoCandidateCount = samples.Count(sample => sample.RawCandidateCount == 0),
            SimilarityMarginForRiskCandidates = marginAverage,
            Recommendation = Recommend(details, mustHitTotal == 0 ? 1.0 : (double)mustHitAfter / mustHitTotal, mrr),
            Warnings = BuildWarnings(details)
        };
    }

    public static string BuildMarkdownReport(
        VectorResidualRiskAuditReport a3,
        VectorResidualRiskAuditReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Residual Risk Audit Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    public static IReadOnlyDictionary<string, int> BuildRiskTypeCounts(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        return samples
            .SelectMany(sample => BuildDetails(sample, string.Empty))
            .GroupBy(item => item.RiskType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    public static double CalculateRiskSimilarityMargin(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        var details = samples.SelectMany(sample => BuildDetails(sample, string.Empty)).ToArray();
        return details.Length == 0 ? 0 : details.Average(item => item.SimilarityMargin);
    }

    private static int CountLifecycleGateBlocks(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        var lifecycleReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            VectorCandidateBlockedReason.UnknownLifecycleBlocked,
            VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked,
            VectorCandidateBlockedReason.ReplacementMetadataMissingBlocked,
            VectorCandidateBlockedReason.LegacySourceRequiresLifecycleMetadata,
            VectorCandidateBlockedReason.HistoricalSourceRequiresAuditProfile
        };
        return samples
            .SelectMany(sample => sample.Candidates)
            .Count(candidate => candidate.BlockedReasons.Any(lifecycleReasons.Contains));
    }

    private static IReadOnlyList<VectorResidualRiskDetail> BuildDetails(VectorQueryShadowEvalSample sample, string profileId)
    {
        var eligible = sample.Candidates
            .Where(IsEligible)
            .OrderBy(candidate => candidate.Rank)
            .ToArray();
        var details = new List<VectorResidualRiskDetail>();
        foreach (var candidate in eligible)
        {
            var isMustNot = sample.MustNotHitMatchedAfterPolicy.Any(expected => EvalIdMatches(expected, candidate.ItemId));
            var isLifecycleRisk = sample.LifecycleRiskItemsAfterPolicy.Any(expected => EvalIdMatches(expected, candidate.ItemId))
                                  || candidate.RiskAfterPolicy
                                  || candidate.IsLifecycleRisk
                                  || candidate.IsStale
                                  || candidate.IsOrphan;
            if (!isMustNot && !isLifecycleRisk)
            {
                continue;
            }

            var eligibleRank = Array.FindIndex(eligible, item => string.Equals(item.ItemId, candidate.ItemId, StringComparison.OrdinalIgnoreCase)) + 1;
            var margin = CalculateMargin(candidate, eligible, sample.MustNotHitMatchedAfterPolicy);
            var riskType = ClassifyRisk(candidate, isMustNot, isLifecycleRisk, margin);
            details.Add(new VectorResidualRiskDetail
            {
                SampleId = sample.SampleId,
                QueryText = sample.QueryText,
                ProfileId = profileId,
                ProviderId = candidate.EmbeddingProvider,
                EmbeddingModel = candidate.EmbeddingModel,
                CandidateItemId = candidate.ItemId,
                Similarity = candidate.Similarity,
                SimilarityMargin = margin,
                RawRank = candidate.RawRank,
                EligibleRank = eligibleRank,
                TargetSection = candidate.TargetSection,
                RiskType = riskType,
                RiskReason = BuildRiskReason(isMustNot, isLifecycleRisk),
                ItemLifecycle = ResolveLifecycle(candidate),
                ItemLayer = candidate.Layer,
                ItemKind = candidate.ItemKind,
                SourceRef = ResolveSourceRef(candidate),
                ContentHash = candidate.ContentHash,
                WhyPolicyAllowed = BuildWhyPolicyAllowed(candidate),
                ExpectedAction = BuildExpectedAction(riskType)
            });
        }

        return details;
    }

    private static string ClassifyRisk(
        VectorQueryPreviewCandidate candidate,
        bool isMustNot,
        bool isLifecycleRisk,
        double margin)
    {
        var lifecycle = ResolveLifecycle(candidate);
        if (IsDeprecated(lifecycle))
        {
            return VectorResidualRiskTypes.DeprecatedMetadataGap;
        }

        if (HasMetadataValue(candidate, "supersededBy") || HasMetadataValue(candidate, "replacedBy"))
        {
            return VectorResidualRiskTypes.SupersededItemAllowed;
        }

        if (IsHistorical(lifecycle) || IsHistoricalLayer(candidate.Layer))
        {
            return VectorResidualRiskTypes.HistoricalItemAllowed;
        }

        if (string.IsNullOrWhiteSpace(lifecycle))
        {
            return VectorResidualRiskTypes.LifecycleMetadataGap;
        }

        if (candidate.Similarity < LooseSimilarityThreshold)
        {
            return VectorResidualRiskTypes.SimilarityThresholdTooLoose;
        }

        if (Math.Abs(margin) <= LowMarginThreshold)
        {
            return VectorResidualRiskTypes.LowMarginAmbiguity;
        }

        if (isLifecycleRisk)
        {
            return VectorResidualRiskTypes.WrongVersionActiveItem;
        }

        return isMustNot
            ? VectorResidualRiskTypes.SemanticOvermatch
            : VectorResidualRiskTypes.RequiresReranker;
    }

    private static string BuildExpectedAction(string riskType)
    {
        return riskType switch
        {
            VectorResidualRiskTypes.DeprecatedMetadataGap => "补齐 deprecated lifecycle metadata 后重建 vector index。",
            VectorResidualRiskTypes.LifecycleMetadataGap => "补齐 lifecycle/status/reviewStatus metadata 后重建 vector index。",
            VectorResidualRiskTypes.SupersededItemAllowed => "使用 replacement metadata 或 relation chain 阻断旧版本候选。",
            VectorResidualRiskTypes.HistoricalItemAllowed => "将 historical/deprecated target 路由到 audit/diagnostics，禁止 normal selected。",
            VectorResidualRiskTypes.ProfileTooBroad => "收窄 profile，把诊断型 itemKind 路由到 diagnostics_only。",
            VectorResidualRiskTypes.SimilarityThresholdTooLoose => "评估提高 minSimilarity 或保留 preview-only。",
            VectorResidualRiskTypes.LowMarginAmbiguity => "保留 preview-only，并交给 reranker 或人工策略判断。",
            VectorResidualRiskTypes.SemanticOvermatch => "需要 reranker 或更细粒度意图特征，不应在 policy 中硬编码领域词。",
            _ => "保持 preview-only，等待更多 metadata、reranker 或人工策略决策。"
        };
    }

    private static string Recommend(
        IReadOnlyList<VectorResidualRiskDetail> details,
        double recallAfter,
        double mrrAfter)
    {
        if (details.Count == 0)
        {
            return recallAfter >= 0.8 && mrrAfter >= 0.5
                ? VectorQueryShadowRecommendations.ReadyForRetrievalShadow
                : VectorQueryShadowRecommendations.KeepPreviewOnly;
        }

        var metadataRepairable = details.All(item =>
            string.Equals(item.RiskType, VectorResidualRiskTypes.DeprecatedMetadataGap, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.RiskType, VectorResidualRiskTypes.LifecycleMetadataGap, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.RiskType, VectorResidualRiskTypes.SupersededItemAllowed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.RiskType, VectorResidualRiskTypes.HistoricalItemAllowed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.RiskType, VectorResidualRiskTypes.ProfileTooBroad, StringComparison.OrdinalIgnoreCase));

        if (metadataRepairable)
        {
            return VectorQueryShadowRecommendations.NeedsPolicyTuning;
        }

        return details.Any(item => string.Equals(item.RiskType, VectorResidualRiskTypes.SemanticOvermatch, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(item.RiskType, VectorResidualRiskTypes.LowMarginAmbiguity, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(item.RiskType, VectorResidualRiskTypes.RequiresReranker, StringComparison.OrdinalIgnoreCase))
            ? VectorQueryShadowRecommendations.RequiresReranker
            : VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static IReadOnlyList<string> BuildWarnings(IReadOnlyList<VectorResidualRiskDetail> details)
    {
        return details.Count == 0
            ? Array.Empty<string>()
            : ["residual risk audit 只解释 shadow 风险，不允许作为正式 retrieval scorer。"];
    }

    private static string BuildRiskReason(bool isMustNot, bool isLifecycleRisk)
    {
        return (isMustNot, isLifecycleRisk) switch
        {
            (true, true) => "候选在 policy 后仍命中 eval mustNot，并存在 lifecycle/diagnostic 风险。",
            (true, false) => "候选在 policy 后仍命中 eval mustNot。",
            (false, true) => "候选在 policy 后仍存在 lifecycle/diagnostic 风险。",
            _ => "候选被 residual audit 识别为风险。"
        };
    }

    private static string BuildWhyPolicyAllowed(VectorQueryPreviewCandidate candidate)
    {
        if (candidate.BlockedReasons.Count > 0)
        {
            return $"候选已被阻断：{string.Join(",", candidate.BlockedReasons)}。";
        }

        var lifecycle = ResolveLifecycle(candidate);
        return $"候选在运行时 metadata 中 lifecycle/status='{(string.IsNullOrWhiteSpace(lifecycle) ? "missing" : lifecycle)}'，layer='{candidate.Layer}'，itemKind='{candidate.ItemKind}'，targetSection='{candidate.TargetSection}'，未触发当前 profile 的阻断规则。";
    }

    private static string ResolveLifecycle(VectorQueryPreviewCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("lifecycle", out var lifecycle)
            && !string.IsNullOrWhiteSpace(lifecycle))
        {
            return lifecycle;
        }

        if (candidate.Metadata.TryGetValue("status", out var status)
            && !string.IsNullOrWhiteSpace(status))
        {
            return status;
        }

        if (candidate.Metadata.TryGetValue("reviewStatus", out var reviewStatus)
            && !string.IsNullOrWhiteSpace(reviewStatus))
        {
            return reviewStatus;
        }

        return string.Empty;
    }

    private static string ResolveSourceRef(VectorQueryPreviewCandidate candidate)
    {
        var parts = new[]
        {
            Get(candidate, "sourceRef"),
            Get(candidate, "sourceRefs"),
            Get(candidate, "sourceKind"),
            Get(candidate, "sourceMode"),
            Get(candidate, "corpusFile")
        }.Where(value => !string.IsNullOrWhiteSpace(value));

        return string.Join(";", parts);
    }

    private static string Get(VectorQueryPreviewCandidate candidate, string key)
    {
        return candidate.Metadata.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static bool HasMetadataValue(VectorQueryPreviewCandidate candidate, string key)
    {
        return candidate.Metadata.TryGetValue(key, out var value)
               && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsEligible(VectorQueryPreviewCandidate candidate)
    {
        return string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeprecated(string lifecycle)
    {
        return string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistorical(string lifecycle)
    {
        return string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalLayer(string layer)
    {
        return layer.Contains("historical", StringComparison.OrdinalIgnoreCase)
               || layer.Contains("deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static double CalculateMargin(
        VectorQueryPreviewCandidate riskCandidate,
        IReadOnlyList<VectorQueryPreviewCandidate> eligible,
        IReadOnlyList<string> mustNotIds)
    {
        var bestNonRisk = eligible
            .Where(candidate => !mustNotIds.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
            .Where(candidate => !candidate.RiskAfterPolicy)
            .OrderByDescending(candidate => candidate.Similarity)
            .FirstOrDefault();
        return bestNonRisk is null ? 0 : riskCandidate.Similarity - bestNonRisk.Similarity;
    }

    private static double CalculateMrr(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var sample in samples)
        {
            var eligible = sample.Candidates
                .Where(IsEligible)
                .OrderBy(candidate => candidate.Rank)
                .ToArray();
            var rank = eligible
                .Select((candidate, index) => new { candidate, index })
                .FirstOrDefault(item => sample.MustHitMatchedAfterPolicy.Any(expected => EvalIdMatches(expected, item.candidate.ItemId)))
                ?.index + 1;
            total += rank is null ? 0 : 1.0 / rank.Value;
        }

        return total / samples.Count;
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendReport(StringBuilder builder, string title, VectorResidualRiskAuditReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- ResidualRiskCount: `{report.ResidualRiskCount}`");
        builder.AppendLine($"- BeforeRepairRiskCount: `{report.BeforeRepairRiskCount}`");
        builder.AppendLine($"- AfterRepairRiskCount: `{report.AfterRepairRiskCount}`");
        builder.AppendLine($"- BlockedByLifecycleMetadataGate: `{report.BlockedByLifecycleMetadataGate}`");
        builder.AppendLine($"- MustHitRecallAfterPolicy: `{report.MustHitRecallAfterPolicy:P2}`");
        builder.AppendLine($"- MustHitMrrAfterPolicy: `{report.MustHitMrrAfterPolicy:F4}`");
        builder.AppendLine($"- RecallLossAfterRepair: `{report.RecallLossAfterRepair:P2}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| RiskType | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in report.RiskAfterPolicyByType.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.Key} | {item.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("| Sample | Candidate | Type | Similarity | Margin | Rank | Lifecycle | Layer | Kind | WhyPolicyAllowed | ExpectedAction |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---|---|---|---|---|");
        foreach (var risk in report.Risks.Take(80))
        {
            builder.AppendLine($"| {risk.SampleId} | {risk.CandidateItemId} | {risk.RiskType} | {risk.Similarity:F4} | {risk.SimilarityMargin:F4} | {risk.EligibleRank} | {risk.ItemLifecycle} | {risk.ItemLayer} | {risk.ItemKind} | {Escape(risk.WhyPolicyAllowed)} | {Escape(risk.ExpectedAction)} |");
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "/").ReplaceLineEndings(" ");
    }
}
