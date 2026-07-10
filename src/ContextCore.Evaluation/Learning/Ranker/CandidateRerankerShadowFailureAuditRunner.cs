using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Candidate reranker shadow 回归审计；只读解释失败，不改变正式检索或打包结果。</summary>
public sealed class CandidateRerankerShadowFailureAuditRunner
{
    public const string PolicyVersion = "candidate-reranker-shadow-failure-audit-cr1.1/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string A3ReportFileName = "candidate-reranker-shadow-failure-audit-a3.json";
    public const string ExtendedReportFileName = "candidate-reranker-shadow-failure-audit-extended.json";
    public const string MarkdownReportFileName = "candidate-reranker-shadow-failure-audit.md";

    private readonly CandidateRerankerShadowEvalRunner _shadowRunner;

    public CandidateRerankerShadowFailureAuditRunner(CandidateRerankerShadowEvalRunner? shadowRunner = null)
    {
        _shadowRunner = shadowRunner ?? new CandidateRerankerShadowEvalRunner();
    }

    public CandidateRerankerShadowFailureAuditReport Build(
        ContextEvalReport evalReport,
        string datasetName,
        CandidateRerankerShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evalReport);
        var resolvedOptions = NormalizeOptions(options);
        var shadowReport = _shadowRunner.Build(evalReport, datasetName, resolvedOptions);
        var paired = evalReport.Results
            .Zip(shadowReport.SampleResults, static (evalResult, shadowSample) => new
            {
                EvalResult = evalResult,
                ShadowSample = shadowSample
            })
            .ToArray();
        var regressions = paired
            .Where(static item => item.ShadowSample.WouldRegress)
            .Select(item => BuildRegressionRecord(item.EvalResult, item.ShadowSample, resolvedOptions))
            .ToArray();
        var reasonSummary = regressions
            .GroupBy(static item => item.RegressionReason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var blockedCandidateCount = shadowReport.BlockedCandidateCount;
        var riskCandidateInShadowTopK = shadowReport.RiskCandidateInShadowTopK;
        var scoreContractStatus = shadowReport.ScoreContractStatus;

        return new CandidateRerankerShadowFailureAuditReport
        {
            OperationId = $"candidate-reranker-shadow-failure-audit-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            Samples = shadowReport.Samples,
            RegressionCount = regressions.Length,
            ScoreContractStatus = scoreContractStatus,
            RankableCandidateCount = shadowReport.RankableCandidateCount,
            BlockedCandidateCount = blockedCandidateCount,
            RiskCandidateInShadowTopK = riskCandidateInShadowTopK,
            MissingLifecycleMetadataCount = shadowReport.MissingFeatureMetadataCount,
            MissingReplacementMetadataCount = regressions.Count(static item => !item.ReplacementMetadataPresent),
            DeprecatedMetadataPresentCount = regressions.Count(static item => item.DeprecatedMetadataPresent),
            RegressionReasonSummary = reasonSummary,
            Regressions = regressions,
            RecommendedNextAction = Recommend(scoreContractStatus, regressions, riskCandidateInShadowTopK),
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(
        CandidateRerankerShadowFailureAuditReport a3,
        CandidateRerankerShadowFailureAuditReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);

        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Shadow Failure Audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private static CandidateRerankerShadowFailureAuditRecord BuildRegressionRecord(
        ContextEvalResult evalResult,
        CandidateRerankerShadowEvalSample sample,
        CandidateRerankerShadowOptions options)
    {
        var shadowTopScores = ResolveShadowTopScores(sample.Trace, options.RecordTopK);
        var shadowTop = shadowTopScores.FirstOrDefault();
        var formalTop = sample.Trace.ScoreBreakdown
            .OrderBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var scoreDirection = CandidateRerankerShadowAuditRules.ResolveScoreDirection(sample.Trace.ScoreBreakdown);
        var riskCandidatesInShadowTopK = sample.RiskCandidateInShadowTopK;
        var lifecycleMetadataPresent = sample.MissingFeatureMetadataCount == 0;
        var replacementMetadataPresent = shadowTop is not null
            && CandidateRerankerShadowAuditRules.HasReplacementMetadata(shadowTop);
        var deprecatedMetadataPresent = shadowTop is not null
            && CandidateRerankerShadowAuditRules.HasDeprecatedMetadata(shadowTop);
        var shadowTopEligibility = ResolveEligibilityStatus(sample.Trace, shadowTop);
        var draft = new CandidateRerankerShadowFailureAuditRecord
        {
            SampleId = sample.SampleId,
            Mode = sample.Mode,
            Intent = sample.Intent,
            QueryText = evalResult.Query,
            FormalTop1 = formalTop?.CandidateId ?? sample.FormalTopCandidateId,
            ShadowTop1 = shadowTop?.CandidateId ?? sample.ShadowTopCandidateId,
            ExpectedMustHit = evalResult.MustHit,
            FormalHit = sample.FormalTop1Correct,
            ShadowHit = sample.ShadowTop1Correct,
            FormalCandidateRank = ResolveBestMustHitRank(sample.Trace.FormalTopCandidates),
            ShadowCandidateRank = ResolveBestMustHitRank(sample.Trace.ShadowTopCandidates),
            CandidateCount = sample.CandidateCount,
            RiskCandidatesInShadowTopK = riskCandidatesInShadowTopK,
            ScoreDirection = scoreDirection,
            ScoreBreakdown = sample.Trace.ScoreBreakdown,
            LifecycleMetadataPresent = lifecycleMetadataPresent,
            ReplacementMetadataPresent = replacementMetadataPresent,
            DeprecatedMetadataPresent = deprecatedMetadataPresent,
            EligibilityStatus = shadowTopEligibility,
            WhyShadowPromoted = ResolveWhyShadowPromoted(formalTop, shadowTop)
        };
        var regressionReason = ResolveRegressionReason(sample, draft);

        return new CandidateRerankerShadowFailureAuditRecord
        {
            SampleId = draft.SampleId,
            Mode = draft.Mode,
            Intent = draft.Intent,
            QueryText = draft.QueryText,
            FormalTop1 = draft.FormalTop1,
            ShadowTop1 = draft.ShadowTop1,
            ExpectedMustHit = draft.ExpectedMustHit,
            FormalHit = draft.FormalHit,
            ShadowHit = draft.ShadowHit,
            FormalCandidateRank = draft.FormalCandidateRank,
            ShadowCandidateRank = draft.ShadowCandidateRank,
            CandidateCount = draft.CandidateCount,
            RiskCandidatesInShadowTopK = draft.RiskCandidatesInShadowTopK,
            ScoreDirection = draft.ScoreDirection,
            ScoreBreakdown = draft.ScoreBreakdown,
            LifecycleMetadataPresent = draft.LifecycleMetadataPresent,
            ReplacementMetadataPresent = draft.ReplacementMetadataPresent,
            DeprecatedMetadataPresent = draft.DeprecatedMetadataPresent,
            EligibilityStatus = draft.EligibilityStatus,
            WhyShadowPromoted = draft.WhyShadowPromoted,
            RegressionReason = regressionReason,
            RecommendedAction = CandidateRerankerShadowAuditRules.ResolveRecommendedAction(regressionReason)
        };
    }

    private static CandidateRerankerShadowOptions NormalizeOptions(CandidateRerankerShadowOptions? options)
    {
        return options ?? new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            MaxCandidatesPerTrace = 50,
            RecordTopK = 10,
            RecordWouldChange = true
        };
    }

    private static IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> ResolveShadowTopScores(
        CandidateRerankerShadowTrace trace,
        int topK)
    {
        return [.. trace.ScoreBreakdown
            .OrderByDescending(static item => item.LifecycleAwareScore)
            .ThenBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Take(topK > 0 ? topK : 10)];
    }

    private static int ResolveBestMustHitRank(IReadOnlyList<CandidateRerankerShadowCandidateRef> candidates)
    {
        return candidates
            .Where(static candidate => candidate.IsMustHit)
            .Select(static candidate => candidate.Rank)
            .DefaultIfEmpty(0)
            .Min();
    }

    private static string ResolveWhyShadowPromoted(
        LifecycleAwareRankerShadowCandidateScore? formalTop,
        LifecycleAwareRankerShadowCandidateScore? shadowTop)
    {
        if (shadowTop is null)
        {
            return "No shadow top candidate was available.";
        }

        if (formalTop is not null
            && string.Equals(formalTop.CandidateId, shadowTop.CandidateId, StringComparison.OrdinalIgnoreCase))
        {
            return "Shadow top1 stayed unchanged; regression comes from risk or lower must-hit rank.";
        }

        var scoreGap = formalTop is null
            ? shadowTop.LifecycleAwareScore
            : shadowTop.LifecycleAwareScore - formalTop.LifecycleAwareScore;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Shadow promoted {shadowTop.CandidateId}; reason={shadowTop.Reason}; scoreDelta={shadowTop.ScoreDelta:0.####}; lifecycleScoreGap={scoreGap:0.####}.");
    }

    private static string ResolveEligibilityStatus(
        CandidateRerankerShadowTrace trace,
        LifecycleAwareRankerShadowCandidateScore? shadowTop)
    {
        if (shadowTop is null)
        {
            return CandidateRerankerEligibilityStatuses.MetadataIncomplete;
        }

        var decision = trace.EligibilityDecisions.FirstOrDefault(item =>
            string.Equals(item.CandidateId, shadowTop.CandidateId, StringComparison.OrdinalIgnoreCase));
        return decision?.Status ?? CandidateRerankerEligibilityStatuses.MetadataIncomplete;
    }

    private static string Recommend(
        string scoreContractStatus,
        IReadOnlyList<CandidateRerankerShadowFailureAuditRecord> regressions,
        int riskCandidateInShadowTopK)
    {
        if (string.Equals(scoreContractStatus, CandidateRerankerScoreContractStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            return "Fix score contract before any ranker tuning.";
        }

        if (riskCandidateInShadowTopK > 0)
        {
            return "Keep formal ranking; add eligibility and metadata alignment before shadow opt-in.";
        }

        if (regressions.Any(static item =>
                string.Equals(
                    item.RegressionReason,
                    CandidateRerankerRegressionReasons.MissingFeatureMetadata,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return "Backfill lifecycle metadata and rerun offline audit.";
        }

        if (regressions.Count > 0)
        {
            return "Keep shadow-only and tune features offline.";
        }

        return "No failure audit action required.";
    }

    private static string ResolveRegressionReason(
        CandidateRerankerShadowEvalSample sample,
        CandidateRerankerShadowFailureAuditRecord record)
    {
        if (string.Equals(
                record.ScoreDirection,
                CandidateRerankerRegressionReasons.ScoreDirectionMismatch,
                StringComparison.OrdinalIgnoreCase))
        {
            return CandidateRerankerRegressionReasons.ScoreDirectionMismatch;
        }

        if (sample.RiskCandidateInShadowTopK > 0)
        {
            return CandidateRerankerRegressionReasons.RiskCandidateAllowed;
        }

        if (sample.MissingFeatureMetadataCount > 0)
        {
            return CandidateRerankerRegressionReasons.MissingFeatureMetadata;
        }

        if (sample.FormalTop1Correct && !sample.ShadowTop1Correct)
        {
            return CandidateRerankerRegressionReasons.PairwiseToListwiseMismatch;
        }

        if (sample.ShadowMrr + double.Epsilon < sample.FormalMrr)
        {
            return CandidateRerankerRegressionReasons.ScoreScaleMismatch;
        }

        return CandidateRerankerRegressionReasons.RequiresFeatureTuning;
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        CandidateRerankerShadowFailureAuditReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- RegressionCount: `{report.RegressionCount}`");
        builder.AppendLine($"- ScoreContractStatus: `{report.ScoreContractStatus}`");
        builder.AppendLine($"- RankableCandidateCount: `{report.RankableCandidateCount}`");
        builder.AppendLine($"- BlockedCandidateCount: `{report.BlockedCandidateCount}`");
        builder.AppendLine($"- RiskCandidateInShadowTopK: `{report.RiskCandidateInShadowTopK}`");
        builder.AppendLine($"- RecommendedNextAction: `{report.RecommendedNextAction}`");
        builder.AppendLine();
        builder.AppendLine("### Regression Reasons");
        if (report.RegressionReasonSummary.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var pair in report.RegressionReasonSummary
                         .OrderByDescending(static item => item.Value)
                         .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- `{pair.Key}`: `{pair.Value}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Intent | FormalTop1 | ShadowTop1 | FormalHit | ShadowHit | RiskTopK | Reason | Action |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---:|---|---|");
        foreach (var regression in report.Regressions.Take(40))
        {
            builder.AppendLine($"| `{regression.SampleId}` | `{regression.Mode}` | `{regression.Intent}` | `{regression.FormalTop1}` | `{regression.ShadowTop1}` | {regression.FormalHit} | {regression.ShadowHit} | {regression.RiskCandidatesInShadowTopK} | `{regression.RegressionReason}` | {regression.RecommendedAction} |");
        }
    }
}
