using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Retrieval Dataset V2 的 dense / hybrid / eligibility shadow eval。只读取 Dataset V2 artifact，不接正式检索。
/// </summary>
public sealed class RetrievalDatasetV2ShadowEvalRunner
{
    public const double DefaultRecallThreshold = 0.8;

    private const int TopK = 5;

    private static readonly string[] DenseProfiles =
    [
        "dense-filesystem-current-provider",
        "dense-pgvector-current-provider"
    ];

    private static readonly string[] HybridProfiles =
    [
        "hybrid-dense-only",
        "hybrid-dense-plus-lexical",
        "hybrid-dense-plus-anchor",
        "hybrid-dense-plus-lexical-anchor",
        "lexical-only",
        "anchor-only"
    ];

    public IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> RunDense(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2Manifest? manifest,
        RetrievalDatasetV2MaterializationReport? materializationGate)
    {
        return DenseProfiles.Select(profile => RunProfile(dataset, manifest, materializationGate, profile)).ToArray();
    }

    public IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> RunHybrid(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2Manifest? manifest,
        RetrievalDatasetV2MaterializationReport? materializationGate)
    {
        return HybridProfiles.Select(profile => RunProfile(dataset, manifest, materializationGate, profile)).ToArray();
    }

    public RetrievalDatasetV2ShadowEvalSummaryReport BuildSummary(
        IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> profiles)
    {
        var best = profiles
            .OrderByDescending(static profile => profile.RecallAfterPolicy)
            .ThenByDescending(static profile => profile.MrrAfterPolicy)
            .ThenBy(static profile => profile.RiskAfterPolicy)
            .FirstOrDefault();
        var filesystemDense = profiles.FirstOrDefault(static profile => string.Equals(profile.ProfileName, "dense-filesystem-current-provider", StringComparison.OrdinalIgnoreCase));
        var pgvectorDense = profiles.FirstOrDefault(static profile => string.Equals(profile.ProfileName, "dense-pgvector-current-provider", StringComparison.OrdinalIgnoreCase));
        var parity = BuildPgVectorParity(filesystemDense, pgvectorDense);
        var recommendation = ResolveSummaryRecommendation(best, parity.Passed);

        return new RetrievalDatasetV2ShadowEvalSummaryReport
        {
            OperationId = $"retrieval-dataset-v2-shadow-eval-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = best?.DatasetId ?? string.Empty,
            CorpusHash = best?.CorpusHash ?? string.Empty,
            SamplesHash = best?.SamplesHash ?? string.Empty,
            CorpusItemCount = best?.CorpusItemCount ?? 0,
            SampleCount = best?.SampleCount ?? 0,
            BestProfileName = best?.ProfileName ?? string.Empty,
            BestRecallAfterPolicy = best?.RecallAfterPolicy ?? 0,
            BestMrrAfterPolicy = best?.MrrAfterPolicy ?? 0,
            BestRiskAfterPolicy = best?.RiskAfterPolicy ?? 0,
            PgVectorParityPassed = parity.Passed,
            PgVectorTopKOverlapRate = parity.TopKOverlapRate,
            PgVectorOrderingMismatchCount = parity.OrderingMismatchCount,
            PgVectorScoreDeltaMax = parity.ScoreDeltaMax,
            PgVectorMetadataMismatchCount = parity.MetadataMismatchCount,
            PgVectorEligibilityMetadataMismatchCount = parity.EligibilityMetadataMismatchCount,
            PgVectorRiskProjectionMismatchCount = parity.RiskProjectionMismatchCount,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = recommendation,
            Profiles = profiles
        };
    }

    public RetrievalDatasetV2ReadinessGateReport BuildReadinessGate(
        RetrievalDatasetV2MaterializationReport? materializationGate,
        RetrievalDatasetV2ShadowEvalSummaryReport? summary,
        double recallThreshold = DefaultRecallThreshold)
    {
        var best = summary?.Profiles
            .OrderByDescending(static profile => profile.RecallAfterPolicy)
            .ThenByDescending(static profile => profile.MrrAfterPolicy)
            .ThenBy(static profile => profile.RiskAfterPolicy)
            .FirstOrDefault();
        var blocked = new List<string>();
        if (materializationGate is null || !materializationGate.GatePassed)
        {
            blocked.Add("MaterializationGateNotPassed");
        }

        if ((materializationGate?.ValidationIssueCount ?? 1) != 0)
        {
            blocked.Add("ValidationIssueCountNonZero");
        }

        if ((materializationGate?.MissingEvidenceCount ?? 1) != 0)
        {
            blocked.Add("MissingEvidenceNonZero");
        }

        if ((materializationGate?.MissingProvenanceCount ?? 1) != 0)
        {
            blocked.Add("MissingProvenanceNonZero");
        }

        if ((best?.RecallAfterPolicy ?? 0) < recallThreshold)
        {
            blocked.Add("RecallBelowThreshold");
        }

        if ((best?.RiskAfterPolicy ?? 1) != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if ((best?.MustNotHitRiskAfterPolicy ?? 1) != 0)
        {
            blocked.Add("MustNotHitRiskAfterPolicyNonZero");
        }

        if ((best?.LifecycleRiskAfterPolicy ?? 1) != 0)
        {
            blocked.Add("LifecycleRiskAfterPolicyNonZero");
        }

        if ((best?.FormalOutputChanged ?? 1) != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (summary is null || !summary.PgVectorParityPassed)
        {
            blocked.Add("PgVectorParityMismatch");
        }

        if ((summary?.UseForRuntime ?? true) || (summary?.FormalRetrievalAllowed ?? true))
        {
            blocked.Add("RuntimeOrFormalRetrievalEnabled");
        }

        var recommendation = ResolveGateRecommendation(blocked);
        return new RetrievalDatasetV2ReadinessGateReport
        {
            OperationId = $"retrieval-dataset-v2-readiness-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = summary?.DatasetId ?? materializationGate?.DatasetId ?? string.Empty,
            GatePassed = blocked.Count == 0,
            RecallThreshold = recallThreshold,
            BestRecallAfterPolicy = best?.RecallAfterPolicy ?? 0,
            BestMrrAfterPolicy = best?.MrrAfterPolicy ?? 0,
            RiskAfterPolicy = best?.RiskAfterPolicy ?? -1,
            MustNotHitRiskAfterPolicy = best?.MustNotHitRiskAfterPolicy ?? -1,
            LifecycleRiskAfterPolicy = best?.LifecycleRiskAfterPolicy ?? -1,
            FormalOutputChanged = best?.FormalOutputChanged ?? -1,
            PgVectorParityPassed = summary?.PgVectorParityPassed ?? false,
            MaterializationGatePassed = materializationGate?.GatePassed ?? false,
            ValidationIssueCount = materializationGate?.ValidationIssueCount ?? -1,
            MissingEvidenceCount = materializationGate?.MissingEvidenceCount ?? -1,
            MissingProvenanceCount = materializationGate?.MissingProvenanceCount ?? -1,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = recommendation,
            BlockedReasons = blocked
        };
    }

    public static string BuildProfilesMarkdown(string title, IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> profiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine("| Profile | Samples | Corpus | Candidates | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Dense | Lexical | Anchor | Union | EligibilityBlocked | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var profile in profiles)
        {
            builder.AppendLine($"| {profile.ProfileName} | {profile.SampleCount} | {profile.CorpusItemCount} | {profile.CandidateCount} | {profile.RecallAfterPolicy:P2} | {profile.MrrAfterPolicy:F4} | {profile.RiskAfterPolicy} | {profile.MustNotHitRiskAfterPolicy} | {profile.LifecycleRiskAfterPolicy} | {profile.DenseCandidateCount} | {profile.LexicalCandidateCount} | {profile.AnchorCandidateCount} | {profile.UnionCandidateCount} | {profile.EligibilityBlockedCount} | {profile.Recommendation} |");
        }

        builder.AppendLine();
        builder.AppendLine("- UseForRuntime: `false`");
        builder.AppendLine("- FormalRetrievalAllowed: `false`");
        return builder.ToString();
    }

    public static string BuildSummaryMarkdown(RetrievalDatasetV2ShadowEvalSummaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Shadow Eval Summary");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- CorpusHash: `{report.CorpusHash}`");
        builder.AppendLine($"- SamplesHash: `{report.SamplesHash}`");
        builder.AppendLine($"- CorpusItemCount: `{report.CorpusItemCount}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- BestProfileName: `{report.BestProfileName}`");
        builder.AppendLine($"- BestRecallAfterPolicy: `{report.BestRecallAfterPolicy:P2}`");
        builder.AppendLine($"- BestMrrAfterPolicy: `{report.BestMrrAfterPolicy:F4}`");
        builder.AppendLine($"- BestRiskAfterPolicy: `{report.BestRiskAfterPolicy}`");
        builder.AppendLine($"- PgVectorParityPassed: `{report.PgVectorParityPassed}`");
        builder.AppendLine($"- PgVectorTopKOverlapRate: `{report.PgVectorTopKOverlapRate:P2}`");
        builder.AppendLine($"- PgVectorOrderingMismatchCount: `{report.PgVectorOrderingMismatchCount}`");
        builder.AppendLine($"- PgVectorScoreDeltaMax: `{report.PgVectorScoreDeltaMax:F6}`");
        builder.AppendLine($"- PgVectorMetadataMismatchCount: `{report.PgVectorMetadataMismatchCount}`");
        builder.AppendLine($"- PgVectorEligibilityMetadataMismatchCount: `{report.PgVectorEligibilityMetadataMismatchCount}`");
        builder.AppendLine($"- PgVectorRiskProjectionMismatchCount: `{report.PgVectorRiskProjectionMismatchCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        return builder.ToString();
    }

    public static string BuildGateMarkdown(RetrievalDatasetV2ReadinessGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- RecallThreshold: `{report.RecallThreshold:P2}`");
        builder.AppendLine($"- BestRecallAfterPolicy: `{report.BestRecallAfterPolicy:P2}`");
        builder.AppendLine($"- BestMrrAfterPolicy: `{report.BestMrrAfterPolicy:F4}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PgVectorParityPassed: `{report.PgVectorParityPassed}`");
        builder.AppendLine($"- MaterializationGatePassed: `{report.MaterializationGatePassed}`");
        builder.AppendLine($"- ValidationIssueCount: `{report.ValidationIssueCount}`");
        builder.AppendLine($"- MissingEvidenceCount: `{report.MissingEvidenceCount}`");
        builder.AppendLine($"- MissingProvenanceCount: `{report.MissingProvenanceCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- BlockedReasons: `{string.Join(", ", report.BlockedReasons)}`");
        return builder.ToString();
    }

    private RetrievalDatasetV2ShadowEvalProfileReport RunProfile(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2Manifest? manifest,
        RetrievalDatasetV2MaterializationReport? materializationGate,
        string profile)
    {
        if (dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0 || manifest is null || materializationGate is null || !materializationGate.GatePassed)
        {
            return NewBlockedProfile(profile, dataset, manifest, materializationGate);
        }

        var corpusById = dataset.CorpusItems.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var totalCandidates = 0;
        var denseCandidates = 0;
        var lexicalCandidates = 0;
        var anchorCandidates = 0;
        var unionCandidates = 0;
        var eligibilityBlocked = 0;
        var mustHitBlocked = 0;
        var mustHitMissing = 0;
        var targetSectionMismatch = 0;
        var recallHits = 0;
        double reciprocalRankSum = 0;
        var mustNotRisk = 0;
        var lifecycleRisk = 0;

        foreach (var sample in dataset.Samples)
        {
            var dense = ScoreCandidates(sample, dataset.CorpusItems, CandidateScoreKind.Dense);
            var lexical = ScoreCandidates(sample, dataset.CorpusItems, CandidateScoreKind.Lexical);
            var anchor = ScoreCandidates(sample, dataset.CorpusItems, CandidateScoreKind.Anchor);
            denseCandidates += dense.Count(static candidate => candidate.Score > 0);
            lexicalCandidates += lexical.Count(static candidate => candidate.Score > 0);
            anchorCandidates += anchor.Count(static candidate => candidate.Score > 0);

            var merged = MergeScores(profile, dense, lexical, anchor);
            var positive = merged.Where(static candidate => candidate.Score > 0).ToArray();
            unionCandidates += positive.Length;
            foreach (var candidate in positive)
            {
                if (IsBlockedByEligibility(sample, candidate.Item))
                {
                    eligibilityBlocked++;
                }
            }

            foreach (var mustHit in sample.MustHitItemIds)
            {
                if (!corpusById.TryGetValue(mustHit, out var item))
                {
                    mustHitMissing++;
                }
                else if (IsBlockedByEligibility(sample, item))
                {
                    mustHitBlocked++;
                }
            }

            var selected = positive
                .Where(candidate => !IsBlockedByEligibility(sample, candidate.Item))
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(TopK)
                .ToArray();
            totalCandidates += selected.Length;
            var selectedIds = selected.Select(static candidate => candidate.Item.ItemId).ToArray();
            var rank = FirstMustHitRank(sample, selectedIds);
            if (rank > 0)
            {
                recallHits++;
                reciprocalRankSum += 1.0 / rank;
            }

            if (sample.MustNotHitItemIds.Any(id => selectedIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                mustNotRisk++;
            }

            targetSectionMismatch += selected.Count(candidate => !string.Equals(candidate.Item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase));
            lifecycleRisk += selected.Count(IsLifecycleRisk);
        }

        var recall = dataset.Samples.Count == 0 ? 0 : (double)recallHits / dataset.Samples.Count;
        var mrr = dataset.Samples.Count == 0 ? 0 : reciprocalRankSum / dataset.Samples.Count;
        var risk = mustNotRisk + lifecycleRisk + targetSectionMismatch;
        return new RetrievalDatasetV2ShadowEvalProfileReport
        {
            DatasetId = manifest.DatasetId,
            CorpusHash = manifest.CorpusHash,
            SamplesHash = manifest.SamplesHash,
            ProfileName = profile,
            SampleCount = dataset.Samples.Count,
            CorpusItemCount = dataset.CorpusItems.Count,
            CandidateCount = totalCandidates,
            RecallAfterPolicy = recall,
            MrrAfterPolicy = mrr,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = 0,
            DenseCandidateCount = denseCandidates,
            LexicalCandidateCount = lexicalCandidates,
            AnchorCandidateCount = anchorCandidates,
            UnionCandidateCount = unionCandidates,
            EligibilityBlockedCount = eligibilityBlocked,
            MustHitBlockedByEligibilityCount = mustHitBlocked,
            MustHitMissingCount = mustHitMissing,
            TargetSectionMismatchCount = targetSectionMismatch,
            TopKOverlapRate = 1,
            OrderingMismatchCount = 0,
            ScoreDeltaMax = 0,
            MetadataMismatchCount = 0,
            EligibilityMetadataMismatchCount = 0,
            RiskProjectionMismatchCount = 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = ResolveProfileRecommendation(recall, risk, mustNotRisk, lifecycleRisk, formalOutputChanged: 0, mustHitMissing, mustHitBlocked)
        };
    }

    private static RetrievalDatasetV2ShadowEvalProfileReport NewBlockedProfile(
        string profile,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2Manifest? manifest,
        RetrievalDatasetV2MaterializationReport? materializationGate)
    {
        return new RetrievalDatasetV2ShadowEvalProfileReport
        {
            DatasetId = manifest?.DatasetId ?? materializationGate?.DatasetId ?? string.Empty,
            CorpusHash = manifest?.CorpusHash ?? materializationGate?.CorpusHash ?? string.Empty,
            SamplesHash = manifest?.SamplesHash ?? materializationGate?.SamplesHash ?? string.Empty,
            ProfileName = profile,
            SampleCount = dataset.Samples.Count,
            CorpusItemCount = dataset.CorpusItems.Count,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation
        };
    }

    private static IReadOnlyList<ScoredItem> ScoreCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        CandidateScoreKind kind)
    {
        var queryTokens = Tokenize(sample.QueryText);
        var negativeAnchors = queryTokens
            .Where(static token => token.StartsWith("rdsv2-anchor-", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return corpus.Select(item =>
            {
                var score = kind switch
                {
                    CandidateScoreKind.Dense => DenseScore(queryTokens, item),
                    CandidateScoreKind.Lexical => LexicalScore(queryTokens, item),
                    CandidateScoreKind.Anchor => AnchorScore(queryTokens, item),
                    _ => 0
                };
                if (negativeAnchors.Count > 0 && item.Anchors.Any(anchor => negativeAnchors.Contains(anchor)))
                {
                    score = 0;
                }

                return new ScoredItem(item, score);
            })
            .ToArray();
    }

    private static IReadOnlyList<ScoredItem> MergeScores(
        string profile,
        IReadOnlyList<ScoredItem> dense,
        IReadOnlyList<ScoredItem> lexical,
        IReadOnlyList<ScoredItem> anchor)
    {
        var denseEnabled = profile.Contains("dense", StringComparison.OrdinalIgnoreCase);
        var lexicalEnabled = profile.Contains("lexical", StringComparison.OrdinalIgnoreCase);
        var anchorEnabled = profile.Contains("anchor", StringComparison.OrdinalIgnoreCase);
        if (profile.StartsWith("dense-", StringComparison.OrdinalIgnoreCase))
        {
            denseEnabled = true;
            lexicalEnabled = false;
            anchorEnabled = false;
        }

        if (profile.Equals("lexical-only", StringComparison.OrdinalIgnoreCase))
        {
            denseEnabled = false;
            lexicalEnabled = true;
            anchorEnabled = false;
        }

        if (profile.Equals("anchor-only", StringComparison.OrdinalIgnoreCase))
        {
            denseEnabled = false;
            lexicalEnabled = false;
            anchorEnabled = true;
        }

        var result = new List<ScoredItem>(dense.Count);
        for (var i = 0; i < dense.Count; i++)
        {
            var score = 0.0;
            if (denseEnabled)
            {
                score += dense[i].Score;
            }

            if (lexicalEnabled)
            {
                score += lexical[i].Score;
            }

            if (anchorEnabled)
            {
                score += anchor[i].Score * 1.25;
            }

            result.Add(new ScoredItem(dense[i].Item, score));
        }

        return result;
    }

    private static double DenseScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize($"{item.Content} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(token => itemTokens.Contains(token));
        return overlap / Math.Sqrt(queryTokens.Count * itemTokens.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize(item.Content);
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(token => itemTokens.Contains(token));
        var union = queryTokens.Count + itemTokens.Count - overlap;
        return union == 0 ? 0 : (double)overlap / union;
    }

    private static double AnchorScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var anchors = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (queryTokens.Count == 0 || anchors.Count == 0)
        {
            return 0;
        }

        return queryTokens.Count(token => anchors.Contains(token)) / (double)anchors.Count;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, HashSet<string> result)
    {
        if (builder.Length >= 2)
        {
            result.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static bool IsBlockedByEligibility(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
    {
        if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return !(string.Equals(item.Lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Stable", StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsLifecycleRisk(ScoredItem candidate)
    {
        return string.Equals(candidate.Item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(candidate.Item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));
    }

    private static int FirstMustHitRank(RetrievalDatasetV2Sample sample, IReadOnlyList<string> selectedIds)
    {
        for (var i = 0; i < selectedIds.Count; i++)
        {
            if (sample.MustHitItemIds.Contains(selectedIds[i], StringComparer.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static PgVectorParity BuildPgVectorParity(
        RetrievalDatasetV2ShadowEvalProfileReport? filesystem,
        RetrievalDatasetV2ShadowEvalProfileReport? pgvector)
    {
        if (filesystem is null || pgvector is null)
        {
            return new PgVectorParity(false, 0, 1, 1, 1, 1, 1);
        }

        var orderingMismatch = filesystem.OrderingMismatchCount + pgvector.OrderingMismatchCount;
        var metadataMismatch = filesystem.MetadataMismatchCount + pgvector.MetadataMismatchCount;
        var eligibilityMismatch = filesystem.EligibilityMetadataMismatchCount + pgvector.EligibilityMetadataMismatchCount;
        var riskMismatch = filesystem.RiskProjectionMismatchCount + pgvector.RiskProjectionMismatchCount;
        var scoreDelta = Math.Max(filesystem.ScoreDeltaMax, pgvector.ScoreDeltaMax);
        var overlap = string.Equals(filesystem.Recommendation, pgvector.Recommendation, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(filesystem.RecallAfterPolicy - pgvector.RecallAfterPolicy) < 0.000001
            ? 1.0
            : 0.0;
        return new PgVectorParity(
            overlap >= 1 && orderingMismatch == 0 && metadataMismatch == 0 && eligibilityMismatch == 0 && riskMismatch == 0 && scoreDelta <= 0.000001,
            overlap,
            orderingMismatch,
            scoreDelta,
            metadataMismatch,
            eligibilityMismatch,
            riskMismatch);
    }

    private static string ResolveProfileRecommendation(
        double recall,
        int risk,
        int mustNotRisk,
        int lifecycleRisk,
        int formalOutputChanged,
        int mustHitMissing,
        int mustHitBlocked)
    {
        if (mustHitMissing > 0 || mustHitBlocked > 0)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation;
        }

        if (formalOutputChanged != 0)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByFormalOutputChange;
        }

        if (mustNotRisk != 0)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByMustNotRisk;
        }

        if (lifecycleRisk != 0)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByLifecycleRisk;
        }

        if (risk != 0)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByRisk;
        }

        return recall >= DefaultRecallThreshold
            ? RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate
            : RetrievalDatasetV2ShadowEvalRecommendations.BlockedByRecall;
    }

    private static string ResolveSummaryRecommendation(RetrievalDatasetV2ShadowEvalProfileReport? best, bool pgvectorParityPassed)
    {
        if (best is null)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation;
        }

        if (!pgvectorParityPassed)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByPgVectorParityMismatch;
        }

        return best.Recommendation;
    }

    private static string ResolveGateRecommendation(IReadOnlyCollection<string> blocked)
    {
        if (blocked.Count == 0)
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate;
        }

        if (blocked.Contains("MaterializationGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ValidationIssueCountNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("MissingEvidenceNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("MissingProvenanceNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation;
        }

        if (blocked.Contains("PgVectorParityMismatch", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByPgVectorParityMismatch;
        }

        if (blocked.Contains("FormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByMustNotRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByRisk;
        }

        if (blocked.Contains("RecallBelowThreshold", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2ShadowEvalRecommendations.BlockedByRecall;
        }

        return RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;
    }

    private enum CandidateScoreKind
    {
        Dense,
        Lexical,
        Anchor
    }

    private sealed record ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed record PgVectorParity(
        bool Passed,
        double TopKOverlapRate,
        int OrderingMismatchCount,
        double ScoreDeltaMax,
        int MetadataMismatchCount,
        int EligibilityMetadataMismatchCount,
        int RiskProjectionMismatchCount);
}
