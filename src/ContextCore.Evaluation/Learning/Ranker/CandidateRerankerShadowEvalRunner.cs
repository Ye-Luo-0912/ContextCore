using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Candidate reranker shadow 离线评估；只读分析，不改变正式检索输出。</summary>
public sealed class CandidateRerankerShadowEvalRunner
{
    public const string PolicyVersion = "candidate-reranker-shadow-eval-cr1/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string A3ReportFileName = "candidate-reranker-shadow-eval-a3.json";
    public const string ExtendedReportFileName = "candidate-reranker-shadow-eval-extended.json";
    public const string MarkdownReportFileName = "candidate-reranker-shadow-eval.md";

    private readonly LifecycleAwareRankerShadowScorer _scorer;
    private readonly CandidateFeatureEnvelopeBuilder _envelopeBuilder;
    private readonly RankerCandidateEligibilityGuard _eligibilityGuard;
    private readonly FormalPriorityFeatureExtractor _formalPriorityFeatureExtractor;

    public CandidateRerankerShadowEvalRunner(
        LifecycleAwareRankerShadowScorer? scorer = null,
        CandidateFeatureEnvelopeBuilder? envelopeBuilder = null,
        RankerCandidateEligibilityGuard? eligibilityGuard = null,
        FormalPriorityFeatureExtractor? formalPriorityFeatureExtractor = null)
    {
        _scorer = scorer ?? new LifecycleAwareRankerShadowScorer();
        _envelopeBuilder = envelopeBuilder ?? new CandidateFeatureEnvelopeBuilder();
        _eligibilityGuard = eligibilityGuard ?? new RankerCandidateEligibilityGuard();
        _formalPriorityFeatureExtractor = formalPriorityFeatureExtractor ?? new FormalPriorityFeatureExtractor();
    }

    public CandidateRerankerShadowEvalReport Build(
        ContextEvalReport evalReport,
        string datasetName,
        CandidateRerankerShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evalReport);
        var resolvedOptions = NormalizeOptions(options);
        var samples = evalReport.Results
            .Select(result => BuildSample(result, resolvedOptions))
            .ToArray();

        var risky = samples.Sum(static sample => sample.LifecycleRiskCount + sample.DeprecatedRiskCount + sample.MustNotRiskCount);
        var netGain = samples.Count(static sample => sample.WouldImprove) - samples.Count(static sample => sample.WouldRegress);
        var netGainAfterAbstain = samples.Count(static sample => sample.WouldImproveAfterAbstain)
            - samples.Count(static sample => sample.WouldRegressAfterAbstain);
        var riskCandidateInShadowTopK = samples.Sum(static sample => sample.RiskCandidateInShadowTopK);
        var riskCandidateInRawTopK = samples.Sum(static sample => sample.RiskCandidateInRawTopK);
        var riskCandidateBlockedBeforeRerank = samples.Sum(static sample => sample.RiskCandidateBlockedBeforeRerank);
        var blockedCandidateCount = samples.Sum(static sample => sample.BlockedCandidateCount);
        var rankableCandidateCount = samples.Sum(static sample => sample.RankableCandidateCount);
        var missingFeatureMetadataCount = samples.Sum(static sample => sample.MissingFeatureMetadataCount);
        var regressionReasonSummary = samples
            .Where(static sample => sample.WouldRegress)
            .Select(ClassifyRegressionReason)
            .GroupBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var scoreContractStatus = ResolveScoreContractStatus(regressionReasonSummary, riskCandidateInShadowTopK);
        var featureCompletenessRate = Average(samples.Select(static sample => sample.FeatureCompletenessRate));
        var eligibilityGuardStatus = ResolveEligibilityGuardStatus(samples);

        return new CandidateRerankerShadowEvalReport
        {
            OperationId = $"candidate-reranker-shadow-eval-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            ShadowProfile = _formalPriorityFeatureExtractor.ResolveProfile(resolvedOptions.ShadowProfile).ProfileId,
            Samples = samples.Length,
            CandidateCount = samples.Sum(static sample => sample.RawCandidateCount),
            RawCandidateCount = samples.Sum(static sample => sample.RawCandidateCount),
            FormalTop1Accuracy = Average(samples.Select(static sample => sample.FormalTop1Correct ? 1d : 0d)),
            ShadowTop1Accuracy = Average(samples.Select(static sample => sample.ShadowTop1Correct ? 1d : 0d)),
            FormalMRR = Average(samples.Select(static sample => sample.FormalMrr)),
            ShadowMRR = Average(samples.Select(static sample => sample.ShadowMrr)),
            WouldChangeTop1Count = samples.Count(static sample => sample.WouldChangeTop1),
            WouldImproveCount = samples.Count(static sample => sample.WouldImprove),
            WouldRegressCount = samples.Count(static sample => sample.WouldRegress),
            WouldApplyCount = samples.Count(static sample => sample.WouldApply),
            AbstainCount = samples.Count(static sample => sample.Abstained),
            NetGainAfterAbstain = netGainAfterAbstain,
            FormalPriorityRecoveredCount = samples.Count(static sample => sample.FormalPriorityRecovered),
            UnexplainedRegressionCount = samples.Count(static sample => sample.UnexplainedRegression),
            LifecycleRiskCount = samples.Sum(static sample => sample.LifecycleRiskCount),
            DeprecatedRiskCount = samples.Sum(static sample => sample.DeprecatedRiskCount),
            MustNotRiskCount = samples.Sum(static sample => sample.MustNotRiskCount),
            NetGain = netGain,
            ScoreContractStatus = scoreContractStatus,
            RankableCandidateCount = rankableCandidateCount,
            BlockedCandidateCount = blockedCandidateCount,
            RiskCandidateInShadowTopK = riskCandidateInShadowTopK,
            RiskCandidateInRawTopK = riskCandidateInRawTopK,
            RiskCandidateBlockedBeforeRerank = riskCandidateBlockedBeforeRerank,
            MissingFeatureMetadataCount = missingFeatureMetadataCount,
            FeatureCompletenessRate = featureCompletenessRate,
            EligibilityGuardStatus = eligibilityGuardStatus,
            RegressionReasonSummary = regressionReasonSummary,
            Recommendation = Recommend(samples.Length, risky, netGain, samples.Count(static sample => sample.WouldRegress)),
            SampleResults = samples,
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(
        CandidateRerankerShadowEvalReport a3,
        CandidateRerankerShadowEvalReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);

        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Shadow Eval");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private CandidateRerankerShadowEvalSample BuildSample(
        ContextEvalResult result,
        CandidateRerankerShadowOptions options)
    {
        var selected = ResolveSelectedDiagnostics(result);
        var dropped = ResolveDroppedDiagnostics(result, selected.Count);
        var raw = selected.Concat(dropped).ToArray();
        var envelopes = _envelopeBuilder.BuildMany(raw);
        var decisions = _eligibilityGuard.Evaluate(envelopes);
        var rankableIds = decisions
            .Where(RankerCandidateEligibilityGuard.IsRankable)
            .Select(static decision => decision.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rankableSelected = selected
            .Where(item => rankableIds.Contains(item.ItemId))
            .ToArray();
        var rankableDropped = dropped
            .Where(item => rankableIds.Contains(item.ItemId))
            .ToArray();
        var baselineTrace = _scorer.Score(
            rankableSelected,
            rankableDropped,
            new LifecycleAwareRankerShadowOptions
            {
                Enabled = true,
                Profile = LifecycleAwareRankerShadowScorer.DefaultProfile
            });
        var topK = ResolveTopK(options);
        var profile = _formalPriorityFeatureExtractor.ResolveProfile(options.ShadowProfile);
        var trace = _formalPriorityFeatureExtractor.ApplyProfile(baselineTrace, profile.ProfileId);
        var baselineCandidateTrace = BuildGuardedTrace(
            result.SampleId,
            result.Mode,
            ResolveIntent(result),
            result.Query,
            raw,
            baselineTrace,
            decisions,
            topK);
        var candidateTrace = BuildGuardedTrace(
            result.SampleId,
            result.Mode,
            ResolveIntent(result),
            result.Query,
            raw,
            trace,
            decisions,
            topK);
        var formalMrr = ComputeMrr(candidateTrace.FormalTopCandidates);
        var baselineShadowMrr = ComputeMrr(baselineCandidateTrace.ShadowTopCandidates);
        var shadowMrr = ComputeMrr(candidateTrace.ShadowTopCandidates);
        var riskCount = candidateTrace.LifecycleRiskCount + candidateTrace.DeprecatedCandidateCount + candidateTrace.MustNotRiskCount;
        var top1Margin = ComputeTop1Margin(trace.CandidateShadowScores);
        var wouldImprove = shadowMrr > formalMrr && riskCount == 0;
        var wouldRegress = shadowMrr + double.Epsilon < formalMrr || riskCount > 0;
        var abstained = profile.UseAbstain
            && candidateTrace.WouldChangeTop1
            && top1Margin <= profile.AbstainMarginThreshold;
        var wouldApply = candidateTrace.WouldChangeTop1 && !abstained && riskCount == 0;
        var formalPriorityRecovered = profile.UseFormalPriority
            && baselineShadowMrr + double.Epsilon < formalMrr
            && shadowMrr + double.Epsilon >= formalMrr;

        return new CandidateRerankerShadowEvalSample
        {
            ShadowProfile = profile.ProfileId,
            SampleId = result.SampleId,
            Mode = result.Mode,
            Intent = ResolveIntent(result),
            CandidateCount = raw.Length,
            RawCandidateCount = raw.Length,
            RankableCandidateCount = decisions.Count(RankerCandidateEligibilityGuard.IsRankable),
            BlockedCandidateCount = decisions.Count(static decision =>
                string.Equals(decision.Status, CandidateRerankerEligibilityStatuses.Blocked, StringComparison.OrdinalIgnoreCase)),
            FeatureCompletenessRate = Average(decisions.Select(static decision => decision.Envelope.FeatureCompleteness)),
            RiskCandidateInRawTopK = CountRawRiskTopK(raw, decisions, topK),
            RiskCandidateInShadowTopK = riskCount,
            RiskCandidateBlockedBeforeRerank = decisions.Count(RankerCandidateEligibilityGuard.IsNonRankableRisk),
            MissingFeatureMetadataCount = decisions.Count(static decision =>
                decision.BlockedReasons.Contains(CandidateRerankerBlockedReasons.MissingLifecycleMetadata, StringComparer.OrdinalIgnoreCase)
                || decision.BlockedReasons.Contains(CandidateRerankerBlockedReasons.IncompleteFeatureEnvelope, StringComparer.OrdinalIgnoreCase)),
            EligibilityGuardStatus = RankerCandidateEligibilityGuard.ResolveGuardStatus(decisions),
            FormalTopCandidateId = candidateTrace.FormalTopCandidates.FirstOrDefault()?.CandidateId ?? string.Empty,
            ShadowTopCandidateId = candidateTrace.ShadowTopCandidates.FirstOrDefault()?.CandidateId ?? string.Empty,
            FormalTop1Correct = candidateTrace.FormalTopCandidates.FirstOrDefault()?.IsMustHit == true,
            ShadowTop1Correct = candidateTrace.ShadowTopCandidates.FirstOrDefault()?.IsMustHit == true,
            FormalMrr = formalMrr,
            ShadowMrr = shadowMrr,
            WouldChangeTop1 = candidateTrace.WouldChangeTop1,
            WouldChangeTopK = candidateTrace.WouldChangeTopK,
            WouldImprove = wouldImprove,
            WouldRegress = wouldRegress,
            WouldApply = wouldApply,
            Abstained = abstained,
            WouldImproveAfterAbstain = wouldApply && wouldImprove,
            WouldRegressAfterAbstain = wouldApply && wouldRegress,
            FormalPriorityRecovered = formalPriorityRecovered,
            UnexplainedRegression = wouldApply && wouldRegress && !formalPriorityRecovered,
            Top1Margin = top1Margin,
            LifecycleRiskCount = candidateTrace.LifecycleRiskCount,
            DeprecatedRiskCount = candidateTrace.DeprecatedCandidateCount,
            MustNotRiskCount = candidateTrace.MustNotRiskCount,
            Trace = candidateTrace
        };
    }

    private static CandidateRerankerShadowOptions NormalizeOptions(CandidateRerankerShadowOptions? options)
    {
        return options ?? new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            ShadowProfile = CandidateRerankerShadowProfiles.BaselineLifecycleAware,
            MaxCandidatesPerTrace = 50,
            RecordTopK = 10,
            RecordWouldChange = true
        };
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveSelectedDiagnostics(ContextEvalResult result)
    {
        if (result.SelectedItemDiagnostics.Count > 0)
        {
            return result.SelectedItemDiagnostics
                .Select((item, index) => EnsureRank(item, index + 1))
                .ToArray();
        }

        return [.. result.SelectedIds
            .Select((id, index) => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Rank = index + 1,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })];
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveDroppedDiagnostics(
        ContextEvalResult result,
        int selectedCount)
    {
        if (result.DroppedItemDiagnostics.Count > 0)
        {
            return result.DroppedItemDiagnostics
                .Select((item, index) => EnsureRank(item, selectedCount + index + 1))
                .ToArray();
        }

        return [.. result.ExcludedIds
            .Select((id, index) => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Rank = selectedCount + index + 1,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })];
    }

    private static ContextEvalItemDiagnostic EnsureRank(ContextEvalItemDiagnostic item, int fallbackRank)
    {
        if (item.Rank > 0)
        {
            return item;
        }

        return new ContextEvalItemDiagnostic
        {
            ItemId = item.ItemId,
            Kind = item.Kind,
            Type = item.Type,
            SectionName = item.SectionName,
            Reason = item.Reason,
            Score = item.Score,
            EstimatedTokens = item.EstimatedTokens,
            Rank = fallbackRank,
            IsMustHit = item.IsMustHit,
            IsMustNotHit = item.IsMustNotHit,
            SourceRefs = item.SourceRefs
        };
    }

    private static CandidateRerankerShadowTrace BuildGuardedTrace(
        string requestId,
        string mode,
        string intent,
        string queryText,
        IReadOnlyList<ContextEvalItemDiagnostic> rawCandidates,
        LifecycleAwareRankerShadowTrace source,
        IReadOnlyList<RankerCandidateEligibilityDecision> decisions,
        int recordTopK)
    {
        var topK = recordTopK > 0 ? recordTopK : 10;
        var formalTop = rawCandidates
            .OrderBy(static item => item.Rank)
            .ThenBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select(static item => new CandidateRerankerShadowCandidateRef
            {
                CandidateId = item.ItemId,
                Rank = item.Rank,
                Score = item.Score,
                Selected = item.Rank > 0,
                IsMustHit = item.IsMustHit,
                IsMustNotHit = item.IsMustNotHit,
                SectionName = item.SectionName
            })
            .ToArray();
        var shadowTopScores = source.CandidateShadowScores
            .OrderByDescending(static item => item.LifecycleAwareScore)
            .ThenBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
        var shadowTop = shadowTopScores
            .Select((item, index) => new CandidateRerankerShadowCandidateRef
            {
                CandidateId = item.CandidateId,
                Rank = index + 1,
                Score = item.LifecycleAwareScore,
                Selected = item.Selected,
                IsMustHit = item.IsMustHit,
                IsMustNotHit = item.IsMustNotHit,
                SectionName = item.SectionName
            })
            .ToArray();

        return new CandidateRerankerShadowTrace
        {
            RequestId = requestId,
            Mode = mode,
            Intent = intent,
            QueryText = queryText,
            CandidateCount = rawCandidates.Count,
            FormalTopCandidates = formalTop,
            ShadowTopCandidates = shadowTop,
            WouldChangeTop1 = !SameCandidate(formalTop.FirstOrDefault(), shadowTop.FirstOrDefault()),
            WouldChangeTopK = !SameTopK(formalTop, shadowTop),
            LifecycleRiskCount = shadowTopScores.Count(IsLifecycleRisk),
            DeprecatedCandidateCount = shadowTopScores.Count(score => IsDeprecatedRisk(score, decisions)),
            MustNotRiskCount = shadowTop.Count(static item => item.IsMustNotHit),
            ScoreBreakdown = source.CandidateShadowScores,
            EligibilityDecisions = decisions,
            FormalOutputChanged = false
        };
    }

    private static string ResolveIntent(ContextEvalResult result)
    {
        return result.PackageMetadata.TryGetValue("planningIntent", out var intent) && !string.IsNullOrWhiteSpace(intent)
            ? intent
            : "Unknown";
    }

    private static int ResolveTopK(CandidateRerankerShadowOptions options)
    {
        return options.RecordTopK > 0 ? options.RecordTopK : 10;
    }

    private static double ComputeMrr(IReadOnlyList<CandidateRerankerShadowCandidateRef> candidates)
    {
        var rank = candidates
            .Select((candidate, index) => new { candidate.IsMustHit, Rank = index + 1 })
            .Where(static item => item.IsMustHit)
            .Select(static item => item.Rank)
            .DefaultIfEmpty(0)
            .First();
        return rank <= 0 ? 0 : 1.0 / rank;
    }

    private static double ComputeTop1Margin(IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var top = scores
            .OrderByDescending(static score => score.LifecycleAwareScore)
            .ThenBy(static score => score.LegacyRank)
            .Take(2)
            .Select(static score => score.LifecycleAwareScore)
            .ToArray();
        return top.Length < 2 ? 0 : top[0] - top[1];
    }

    private static int CountRawRiskTopK(
        IReadOnlyList<ContextEvalItemDiagnostic> rawCandidates,
        IReadOnlyList<RankerCandidateEligibilityDecision> decisions,
        int topK)
    {
        var decisionById = decisions.ToDictionary(static decision => decision.CandidateId, StringComparer.OrdinalIgnoreCase);
        return rawCandidates
            .OrderBy(static item => item.Rank)
            .ThenBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(topK > 0 ? topK : 10)
            .Count(item => item.IsMustNotHit
                || (decisionById.TryGetValue(item.ItemId, out var decision)
                    && RankerCandidateEligibilityGuard.IsNonRankableRisk(decision)));
    }

    private static bool SameTopK(
        IReadOnlyList<CandidateRerankerShadowCandidateRef> formalTop,
        IReadOnlyList<CandidateRerankerShadowCandidateRef> shadowTop)
    {
        if (formalTop.Count != shadowTop.Count)
        {
            return false;
        }

        for (var index = 0; index < formalTop.Count; index++)
        {
            if (!SameCandidate(formalTop[index], shadowTop[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameCandidate(
        CandidateRerankerShadowCandidateRef? formal,
        CandidateRerankerShadowCandidateRef? shadow)
    {
        return string.Equals(formal?.CandidateId, shadow?.CandidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleRisk(LifecycleAwareRankerShadowCandidateScore item)
    {
        return item.IsMustNotHit || item.LifecycleFeatures.IsRejected;
    }

    private static bool IsDeprecatedRisk(
        LifecycleAwareRankerShadowCandidateScore score,
        IReadOnlyList<RankerCandidateEligibilityDecision> decisions)
    {
        var decision = decisions.FirstOrDefault(item =>
            string.Equals(item.CandidateId, score.CandidateId, StringComparison.OrdinalIgnoreCase));
        if (decision is not null)
        {
            return decision.Envelope.IsDeprecated
                || decision.Envelope.IsHistorical
                || decision.Envelope.IsSuperseded;
        }

        return false;
    }

    private static string ResolveEligibilityGuardStatus(IReadOnlyList<CandidateRerankerShadowEvalSample> samples)
    {
        if (samples.Count == 0)
        {
            return CandidateRerankerEligibilityGuardStatuses.Unknown;
        }

        return samples.Any(static sample => sample.RiskCandidateBlockedBeforeRerank > 0)
            ? CandidateRerankerEligibilityGuardStatuses.Guarded
            : CandidateRerankerEligibilityGuardStatuses.Passed;
    }

    private static bool IsId(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            || actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static double Average(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }

    private static string Recommend(
        int samples,
        int riskCount,
        int netGain,
        int regressionCount)
    {
        if (samples == 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsMoreRealTraces;
        }

        if (riskCount > 0)
        {
            return CandidateRerankerShadowRecommendations.BlockedByRisk;
        }

        if (netGain > 0 && regressionCount == 0)
        {
            return CandidateRerankerShadowRecommendations.ReadyForRankerShadow;
        }

        if (netGain > 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsFeatureTuning;
        }

        return CandidateRerankerShadowRecommendations.KeepFormalRanking;
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        CandidateRerankerShadowEvalReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- ShadowProfile: `{report.ShadowProfile}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- FormalTop1Accuracy: `{report.FormalTop1Accuracy.ToString("P2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- ShadowTop1Accuracy: `{report.ShadowTop1Accuracy.ToString("P2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- FormalMRR: `{report.FormalMRR.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- ShadowMRR: `{report.ShadowMRR.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- WouldImproveCount: `{report.WouldImproveCount}`");
        builder.AppendLine($"- WouldRegressCount: `{report.WouldRegressCount}`");
        builder.AppendLine($"- WouldApplyCount: `{report.WouldApplyCount}`");
        builder.AppendLine($"- AbstainCount: `{report.AbstainCount}`");
        builder.AppendLine($"- NetGainAfterAbstain: `{report.NetGainAfterAbstain}`");
        builder.AppendLine($"- FormalPriorityRecoveredCount: `{report.FormalPriorityRecoveredCount}`");
        builder.AppendLine($"- UnexplainedRegressionCount: `{report.UnexplainedRegressionCount}`");
        builder.AppendLine($"- RiskCount: `{report.LifecycleRiskCount + report.DeprecatedRiskCount + report.MustNotRiskCount}`");
        builder.AppendLine($"- NetGain: `{report.NetGain}`");
        builder.AppendLine($"- ScoreContractStatus: `{report.ScoreContractStatus}`");
        builder.AppendLine($"- EligibilityGuardStatus: `{report.EligibilityGuardStatus}`");
        builder.AppendLine($"- FeatureCompletenessRate: `{report.FeatureCompletenessRate.ToString("P2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- MissingFeatureMetadataCount: `{report.MissingFeatureMetadataCount}`");
        builder.AppendLine($"- RiskCandidateInRawTopK: `{report.RiskCandidateInRawTopK}`");
        builder.AppendLine($"- RiskCandidateInShadowTopK: `{report.RiskCandidateInShadowTopK}`");
        builder.AppendLine($"- RiskCandidateBlockedBeforeRerank: `{report.RiskCandidateBlockedBeforeRerank}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.RegressionReasonSummary.Count > 0)
        {
            builder.AppendLine($"- RegressionReasonSummary: `{string.Join(", ", report.RegressionReasonSummary.OrderByDescending(static item => item.Value).Select(static item => item.Key + '=' + item.Value.ToString(CultureInfo.InvariantCulture)))}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Intent | FormalTop | ShadowTop | FormalMRR | ShadowMRR | Apply | Abstain | Improve | Regress | Risk |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var sample in report.SampleResults.Take(30))
        {
            var risk = sample.LifecycleRiskCount + sample.DeprecatedRiskCount + sample.MustNotRiskCount;
            builder.AppendLine($"| `{sample.SampleId}` | `{sample.Mode}` | `{sample.Intent}` | `{sample.FormalTopCandidateId}` | `{sample.ShadowTopCandidateId}` | {sample.FormalMrr:0.####} | {sample.ShadowMrr:0.####} | {sample.WouldApply} | {sample.Abstained} | {sample.WouldImprove} | {sample.WouldRegress} | {risk} |");
        }
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

    private static string ClassifyRegressionReason(CandidateRerankerShadowEvalSample sample)
    {
        var scoreDirection = CandidateRerankerShadowAuditRules.ResolveScoreDirection(sample.Trace.ScoreBreakdown);
        if (string.Equals(
                scoreDirection,
                CandidateRerankerRegressionReasons.ScoreDirectionMismatch,
                StringComparison.OrdinalIgnoreCase))
        {
            return CandidateRerankerRegressionReasons.ScoreDirectionMismatch;
        }

        var shadowTop = ResolveShadowTopScores(sample.Trace, 10).FirstOrDefault();
        if (shadowTop is not null && CandidateRerankerShadowAuditRules.IsRiskCandidate(shadowTop))
        {
            return shadowTop.ScoreDelta < 0
                ? CandidateRerankerRegressionReasons.RankerFeatureTooWeak
                : CandidateRerankerRegressionReasons.RiskCandidateAllowed;
        }

        if (shadowTop is not null && !CandidateRerankerShadowAuditRules.HasLifecycleMetadata(shadowTop))
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

    private static string ResolveScoreContractStatus(
        IReadOnlyDictionary<string, int> regressionReasonSummary,
        int riskCandidateInShadowTopK)
    {
        if (regressionReasonSummary.ContainsKey(CandidateRerankerRegressionReasons.ScoreDirectionMismatch))
        {
            return CandidateRerankerScoreContractStatuses.Failed;
        }

        return riskCandidateInShadowTopK > 0
            ? CandidateRerankerScoreContractStatuses.NeedsAudit
            : CandidateRerankerScoreContractStatuses.Passed;
    }
}
