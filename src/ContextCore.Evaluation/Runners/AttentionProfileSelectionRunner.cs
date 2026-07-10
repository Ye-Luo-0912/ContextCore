using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>Builds a conservative profile selection report from attention shadow eval outputs.</summary>
public sealed class AttentionProfileSelectionRunner
{
    public const double BaselineSelectedSetChangeLimit = 0.08;
    public const double ExtendedSelectedSetChangeLimit = 0.15;
    public const double Recall5AllowedDrop = 0.02;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<AttentionProfileSelectionReport> GenerateAsync(
        string baselineReportPath,
        string extendedReportPath,
        CancellationToken cancellationToken = default)
    {
        var baseline = await ReadEvalReportAsync(baselineReportPath, cancellationToken).ConfigureAwait(false);
        var extended = await ReadEvalReportAsync(extendedReportPath, cancellationToken).ConfigureAwait(false);
        return Generate(baseline, extended, baselineReportPath, extendedReportPath);
    }

    public AttentionProfileSelectionReport Generate(
        ContextEvalReport baseline,
        ContextEvalReport extended,
        string baselineReportPath = "",
        string extendedReportPath = "")
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(extended);

        var profileIds = ResolveProfileIds(baseline, extended);
        var profiles = profileIds
            .Select(profileId => BuildProfileReport(profileId, baseline, extended))
            .OrderByDescending(profile => profile.SafetyGate.Passed)
            .ThenByDescending(profile => profile.SelectionScore)
            .ThenBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var recommended = profiles.FirstOrDefault();
        var safetyGate = recommended?.SafetyGate ?? new AttentionProfileSafetyGateResult
        {
            BlockingIssues = ["no_attention_profile_metrics"],
            Checks =
            [
                new AttentionProfileSafetyGateCheck
                {
                    Code = "profile_metrics_present",
                    Passed = false,
                    Message = "No attention profile metrics were found.",
                    Actual = 0,
                    Threshold = 1
                }
            ]
        };

        return new AttentionProfileSelectionReport
        {
            BaselineReportPath = baselineReportPath,
            ExtendedReportPath = extendedReportPath,
            RecommendedProfile = recommended?.ProfileId ?? string.Empty,
            RecommendedMode = safetyGate.Passed ? "guarded-rerank-candidate" : "shadow-only",
            RiskLevel = ResolveRiskLevel(safetyGate),
            BlockingIssues = safetyGate.BlockingIssues,
            NextAction = safetyGate.Passed
                ? "Run a guarded rerank experiment behind an explicit flag; keep package output comparison enabled."
                : "Keep attention profiles in shadow mode and reduce blocking safety issues before any rerank experiment.",
            SafetyGate = safetyGate,
            Profiles = profiles
        };
    }

    public static string BuildMarkdownReport(AttentionProfileSelectionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Attention Profile Selection Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedAt:u}");
        sb.AppendLine();
        sb.AppendLine("## Recommendation");
        sb.AppendLine();
        sb.AppendLine($"- Recommended profile: `{report.RecommendedProfile}`");
        sb.AppendLine($"- Recommended mode: `{report.RecommendedMode}`");
        sb.AppendLine($"- Risk level: `{report.RiskLevel}`");
        sb.AppendLine($"- Next action: {report.NextAction}");
        if (report.BlockingIssues.Count > 0)
        {
            sb.AppendLine($"- Blocking issues: {string.Join(", ", report.BlockingIssues.Select(issue => $"`{issue}`"))}");
        }
        sb.AppendLine();

        sb.AppendLine("## Safety Gate");
        sb.AppendLine();
        sb.AppendLine("| Check | Passed | Actual | Threshold | Message |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var check in report.SafetyGate.Checks)
        {
            sb.AppendLine($"| {check.Code} | {FormatBool(check.Passed)} | {check.Actual:0.####} | {check.Threshold:0.####} | {check.Message} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Profile Summary");
        sb.AppendLine();
        sb.AppendLine("| Profile | Gate | Score | Base MRR | Base R@3 | Base R@5 | Base Improved | Base Regressed | Base MRR1 Reg | Base MNH Promoted | Base Change | Ext MRR | Ext R@3 | Ext R@5 | Ext Improved | Ext Regressed | Ext MRR1 Reg | Ext MNH Promoted | Ext Change |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var profile in report.Profiles)
        {
            sb.AppendLine($"| {profile.ProfileId} | {FormatBool(profile.SafetyGate.Passed)} | {profile.SelectionScore:0.####} | {profile.Baseline.AttentionMrr:0.####} | {profile.Baseline.AttentionRecall3:P1} | {profile.Baseline.AttentionRecall5:P1} | {profile.Baseline.ImprovedSamples} | {profile.Baseline.RegressedSamples} | {profile.Baseline.CurrentMrrOneRegressionCount} | {profile.Baseline.MustNotHitPromotedCount} | {profile.Baseline.SelectedSetChangeRatio:P1} | {profile.Extended.AttentionMrr:0.####} | {profile.Extended.AttentionRecall3:P1} | {profile.Extended.AttentionRecall5:P1} | {profile.Extended.ImprovedSamples} | {profile.Extended.RegressedSamples} | {profile.Extended.CurrentMrrOneRegressionCount} | {profile.Extended.MustNotHitPromotedCount} | {profile.Extended.SelectedSetChangeRatio:P1} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Category Breakdown");
        sb.AppendLine();
        sb.AppendLine("| Profile | Scope | Category | Samples | MRR | R@3 | R@5 | Improved | Regressed | MRR1 Reg | MNH Promoted | Change |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var profile in report.Profiles)
        {
            AppendCategoryRows(sb, profile.ProfileId, "baseline", profile.Baseline.CategoryBreakdown);
            AppendCategoryRows(sb, profile.ProfileId, "extended", profile.Extended.CategoryBreakdown);
        }
        sb.AppendLine();

        sb.AppendLine("## Top Improved Samples");
        AppendSampleDeltaTable(sb, report.Profiles.SelectMany(profile => profile.TopImprovedSamples).OrderByDescending(sample => sample.MrrDelta).Take(20).ToArray());

        sb.AppendLine("## Top Regressed Samples");
        AppendSampleDeltaTable(sb, report.Profiles.SelectMany(profile => profile.TopRegressedSamples).OrderBy(sample => sample.MrrDelta).Take(20).ToArray());

        sb.AppendLine("## Focus Regression Candidate Breakdown");
        AppendFocusCandidateBreakdown(sb, report.Profiles);

        sb.AppendLine("## MustNotHit Promotion Diagnostics");
        AppendMustNotHitDiagnostics(sb, report.Profiles);

        return sb.ToString();
    }

    private static async Task<ContextEvalReport> ReadEvalReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Eval report was not found.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ContextEvalReport>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to parse eval report: {path}");
    }

    private static IReadOnlyList<string> ResolveProfileIds(ContextEvalReport baseline, ContextEvalReport extended)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in baseline.AttentionProfileSummaries.Concat(extended.AttentionProfileSummaries))
        {
            if (!string.IsNullOrWhiteSpace(summary.ProfileId))
            {
                ids.Add(summary.ProfileId);
            }
        }

        foreach (var result in baseline.Results.Concat(extended.Results))
        {
            foreach (var profile in result.AttentionProfiles)
            {
                if (!string.IsNullOrWhiteSpace(profile.ProfileId))
                {
                    ids.Add(profile.ProfileId);
                }
            }
        }

        return ids.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static AttentionProfileSelectionProfileReport BuildProfileReport(
        string profileId,
        ContextEvalReport baseline,
        ContextEvalReport extended)
    {
        var baselineMetrics = BuildMetrics("baseline", profileId, baseline);
        var extendedMetrics = BuildMetrics("extended", profileId, extended);
        var topImproved = BuildSampleDeltas("baseline", profileId, baseline)
            .Concat(BuildSampleDeltas("extended", profileId, extended))
            .Where(sample => sample.MrrDelta > 0.0001)
            .OrderByDescending(sample => sample.MrrDelta)
            .Take(10)
            .ToArray();
        var topRegressed = BuildSampleDeltas("baseline", profileId, baseline)
            .Concat(BuildSampleDeltas("extended", profileId, extended))
            .Where(sample => sample.MrrDelta < -0.0001)
            .OrderBy(sample => sample.MrrDelta)
            .Take(10)
            .ToArray();
        var safetyGate = EvaluateSafetyGate(baselineMetrics, extendedMetrics);

        return new AttentionProfileSelectionProfileReport
        {
            ProfileId = profileId,
            PolicyVersion = ResolvePolicyVersion(profileId, baseline, extended),
            SelectionScore = CalculateSelectionScore(baselineMetrics, extendedMetrics),
            Baseline = baselineMetrics,
            Extended = extendedMetrics,
            TopImprovedSamples = topImproved,
            TopRegressedSamples = topRegressed,
            SafetyGate = safetyGate
        };
    }

    private static AttentionProfileSelectionMetrics BuildMetrics(
        string scope,
        string profileId,
        ContextEvalReport report)
    {
        var summary = FindProfileSummary(profileId, report);
        return new AttentionProfileSelectionMetrics
        {
            Scope = scope,
            TotalSamples = report.TotalSamples,
            PassRate = report.PassRate,
            CurrentRecall5 = report.AvgRetrievalRecall5,
            CurrentNoiseRatio = report.AvgRetrievalNoiseViolationRatio,
            AttentionMrr = summary?.AvgAttentionMrr ?? 0d,
            AttentionRecall3 = summary?.AvgAttentionRecall3 ?? 0d,
            AttentionRecall5 = summary?.AvgAttentionRecall5 ?? 0d,
            ImprovedSamples = summary?.ImprovedSamples ?? 0,
            RegressedSamples = summary?.RegressedSamples ?? 0,
            CurrentMrrOneRegressionCount = summary?.CurrentMrrOneRegressionCount
                ?? report.Results.SelectMany(result => result.AttentionProfiles)
                    .Count(profile =>
                        string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                        && profile.Regressed
                        && profile.CurrentMrr >= 0.9999),
            MustNotHitPromotedCount = summary?.MustNotHitPromotedCount ?? 0,
            SelectedSetChangeRatio = summary?.SelectedSetChangeRatio ?? 0d,
            CategoryBreakdown = summary?.CategoryBreakdown ?? Array.Empty<ContextEvalAttentionProfileCategorySummary>()
        };
    }

    private static ContextEvalAttentionProfileSummary? FindProfileSummary(
        string profileId,
        ContextEvalReport report)
    {
        var summary = report.AttentionProfileSummaries.FirstOrDefault(item =>
            string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        if (summary is not null)
        {
            return summary;
        }

        var rows = report.Results
            .SelectMany(result => result.AttentionProfiles
                .Where(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                .Select(profile => new { Result = result, Profile = profile }))
            .ToArray();
        if (rows.Length == 0)
        {
            return null;
        }

        return new ContextEvalAttentionProfileSummary
        {
            ProfileId = profileId,
            PolicyVersion = rows.First().Profile.PolicyVersion,
            SampleCount = rows.Length,
            AvgAttentionMrr = rows.Average(row => row.Profile.AttentionMrr),
            AvgAttentionRecall3 = rows.Average(row => row.Profile.AttentionRecall3),
            AvgAttentionRecall5 = rows.Average(row => row.Profile.AttentionRecall5),
            ImprovedSamples = rows.Count(row => row.Profile.Improved),
            RegressedSamples = rows.Count(row => row.Profile.Regressed),
            CurrentMrrOneRegressionCount = rows.Count(row =>
                row.Profile.Regressed && row.Profile.CurrentMrr >= 0.9999),
            MustNotHitPromotedCount = rows.Sum(row => row.Profile.MustNotHitPromotedCount),
            SelectedSetChangeRatio = rows.Average(row => row.Profile.SelectedSetChangeRatio),
            CategoryBreakdown = rows
                .GroupBy(row => row.Result.Mode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new ContextEvalAttentionProfileCategorySummary
                    {
                        Category = group.Key,
                        SampleCount = items.Length,
                        AvgAttentionMrr = items.Average(row => row.Profile.AttentionMrr),
                        AvgAttentionRecall3 = items.Average(row => row.Profile.AttentionRecall3),
                        AvgAttentionRecall5 = items.Average(row => row.Profile.AttentionRecall5),
                        ImprovedSamples = items.Count(row => row.Profile.Improved),
                        RegressedSamples = items.Count(row => row.Profile.Regressed),
                        CurrentMrrOneRegressionCount = items.Count(row =>
                            row.Profile.Regressed && row.Profile.CurrentMrr >= 0.9999),
                        MustNotHitPromotedCount = items.Sum(row => row.Profile.MustNotHitPromotedCount),
                        SelectedSetChangeRatio = items.Average(row => row.Profile.SelectedSetChangeRatio)
                    };
                })
                .ToArray()
        };
    }

    private static IReadOnlyList<AttentionProfileSelectionSampleDelta> BuildSampleDeltas(
        string scope,
        string profileId,
        ContextEvalReport report)
    {
        return report.Results
            .SelectMany(result => result.AttentionProfiles
                .Where(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                .Select(profile => new AttentionProfileSelectionSampleDelta
                {
                    Scope = scope,
                    ProfileId = profile.ProfileId,
                    SampleId = result.SampleId,
                    Mode = result.Mode,
                    CurrentMrr = profile.CurrentMrr,
                    AttentionMrr = profile.AttentionMrr,
                    MrrDelta = profile.AttentionMrr - profile.CurrentMrr,
                    AttentionRecall3 = profile.AttentionRecall3,
                    AttentionRecall5 = profile.AttentionRecall5,
                    WouldChangeSelectedSet = profile.WouldChangeSelectedSet,
                    MustHitDemotedCount = profile.MustHitDemotedCount,
                    MustNotHitPromotedCount = profile.MustNotHitPromotedCount,
                    SelectedSetChangeRatio = profile.SelectedSetChangeRatio,
                    CandidateBreakdown = profile.CandidateDiagnostics
                }))
            .ToArray();
    }

    public static AttentionProfileSafetyGateResult EvaluateSafetyGate(
        AttentionProfileSelectionMetrics baseline,
        AttentionProfileSelectionMetrics extended)
    {
        var checks = new List<AttentionProfileSafetyGateCheck>
        {
            Check("baseline_pass_rate_100", baseline.PassRate >= 0.9999, baseline.PassRate, 1d,
                "A3 baseline pass rate must remain 100%."),
            Check("baseline_noise_zero", baseline.CurrentNoiseRatio <= 0.0001, baseline.CurrentNoiseRatio, 0d,
                "A3 baseline noise violation ratio must remain 0%."),
            Check("must_not_hit_promoted_zero",
                baseline.MustNotHitPromotedCount + extended.MustNotHitPromotedCount == 0,
                baseline.MustNotHitPromotedCount + extended.MustNotHitPromotedCount,
                0d,
                "Attention must not promote mustNotHit samples."),
            Check("baseline_selected_set_change_ratio_limit",
                baseline.SelectedSetChangeRatio <= BaselineSelectedSetChangeLimit,
                baseline.SelectedSetChangeRatio,
                BaselineSelectedSetChangeLimit,
                "A3 selected-set change ratio must stay <= 8%."),
            Check("extended_selected_set_change_ratio_limit",
                extended.SelectedSetChangeRatio <= ExtendedSelectedSetChangeLimit,
                extended.SelectedSetChangeRatio,
                ExtendedSelectedSetChangeLimit,
                "Extended selected-set change ratio must stay <= 15%."),
            Check("improved_samples_gt_regressed_samples",
                baseline.ImprovedSamples + extended.ImprovedSamples > baseline.RegressedSamples + extended.RegressedSamples,
                baseline.ImprovedSamples + extended.ImprovedSamples - baseline.RegressedSamples - extended.RegressedSamples,
                0d,
                "Improved samples must exceed regressed samples across baseline and extended reports."),
            Check("baseline_attention_recall5_guard",
                baseline.AttentionRecall5 >= baseline.CurrentRecall5 - Recall5AllowedDrop,
                baseline.AttentionRecall5 - baseline.CurrentRecall5,
                -Recall5AllowedDrop,
                "A3 Attention Recall@5 may not be more than 2 percentage points below current Recall@5."),
            Check("extended_attention_recall5_guard",
                extended.AttentionRecall5 >= extended.CurrentRecall5 - Recall5AllowedDrop,
                extended.AttentionRecall5 - extended.CurrentRecall5,
                -Recall5AllowedDrop,
                "Extended Attention Recall@5 may not be more than 2 percentage points below current Recall@5.")
        };
        var blocking = checks
            .Where(check => !check.Passed)
            .Select(check => check.Code)
            .ToArray();

        return new AttentionProfileSafetyGateResult
        {
            Passed = blocking.Length == 0,
            Checks = checks,
            BlockingIssues = blocking
        };
    }

    private static AttentionProfileSafetyGateCheck Check(
        string code,
        bool passed,
        double actual,
        double threshold,
        string message)
    {
        return new AttentionProfileSafetyGateCheck
        {
            Code = code,
            Passed = passed,
            Actual = actual,
            Threshold = threshold,
            Message = message
        };
    }

    private static double CalculateSelectionScore(
        AttentionProfileSelectionMetrics baseline,
        AttentionProfileSelectionMetrics extended)
    {
        var mrr = baseline.AttentionMrr * 0.35 + extended.AttentionMrr * 0.35;
        var recall = baseline.AttentionRecall5 * 0.10 + extended.AttentionRecall5 * 0.10;
        var improvement = (baseline.ImprovedSamples + extended.ImprovedSamples
            - baseline.RegressedSamples - extended.RegressedSamples) * 0.002;
        var mustNotHitPenalty = (baseline.MustNotHitPromotedCount + extended.MustNotHitPromotedCount) * 0.03;
        var changePenalty = (baseline.SelectedSetChangeRatio + extended.SelectedSetChangeRatio) * 0.20;
        var mrrOneRegressionPenalty = (baseline.CurrentMrrOneRegressionCount + extended.CurrentMrrOneRegressionCount) * 0.01;
        var recallDropPenalty = Math.Max(0d, baseline.CurrentRecall5 - baseline.AttentionRecall5)
            + Math.Max(0d, extended.CurrentRecall5 - extended.AttentionRecall5);

        return mrr + recall + improvement - mustNotHitPenalty - changePenalty - mrrOneRegressionPenalty - recallDropPenalty;
    }

    private static string ResolvePolicyVersion(
        string profileId,
        ContextEvalReport baseline,
        ContextEvalReport extended)
    {
        return baseline.AttentionProfileSummaries
                .Concat(extended.AttentionProfileSummaries)
                .FirstOrDefault(summary => string.Equals(summary.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                ?.PolicyVersion
            ?? baseline.Results
                .Concat(extended.Results)
                .SelectMany(result => result.AttentionProfiles)
                .FirstOrDefault(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                ?.PolicyVersion
            ?? string.Empty;
    }

    private static string ResolveRiskLevel(AttentionProfileSafetyGateResult safetyGate)
    {
        if (safetyGate.Passed)
        {
            return "low";
        }

        return safetyGate.BlockingIssues.Any(issue =>
            string.Equals(issue, "must_not_hit_promoted_zero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(issue, "extended_selected_set_change_ratio_limit", StringComparison.OrdinalIgnoreCase))
            ? "high"
            : "medium";
    }

    private static void AppendCategoryRows(
        StringBuilder sb,
        string profileId,
        string scope,
        IReadOnlyList<ContextEvalAttentionProfileCategorySummary> categories)
    {
        foreach (var category in categories)
        {
            sb.AppendLine($"| {profileId} | {scope} | {category.Category} | {category.SampleCount} | {category.AvgAttentionMrr:0.####} | {category.AvgAttentionRecall3:P1} | {category.AvgAttentionRecall5:P1} | {category.ImprovedSamples} | {category.RegressedSamples} | {category.CurrentMrrOneRegressionCount} | {category.MustNotHitPromotedCount} | {category.SelectedSetChangeRatio:P1} |");
        }
    }

    private static void AppendSampleDeltaTable(
        StringBuilder sb,
        IReadOnlyList<AttentionProfileSelectionSampleDelta> samples)
    {
        sb.AppendLine();
        if (samples.Count == 0)
        {
            sb.AppendLine("None.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Scope | Profile | Sample | Mode | CurrentMRR | AttentionMRR | Delta | R@3 | R@5 | Change | MNH Promoted |");
        sb.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var sample in samples)
        {
            sb.AppendLine($"| {sample.Scope} | {sample.ProfileId} | {sample.SampleId} | {sample.Mode} | {sample.CurrentMrr:0.####} | {sample.AttentionMrr:0.####} | {sample.MrrDelta:0.####} | {sample.AttentionRecall3:P1} | {sample.AttentionRecall5:P1} | {sample.SelectedSetChangeRatio:P1} | {sample.MustNotHitPromotedCount} |");
        }

        sb.AppendLine();
    }

    private static void AppendFocusCandidateBreakdown(
        StringBuilder sb,
        IReadOnlyList<AttentionProfileSelectionProfileReport> profiles)
    {
        var focusIds = new HashSet<string>([
            "project-sample-009",
            "coding-sample-009",
            "novel-sample-002"
        ], StringComparer.OrdinalIgnoreCase);
        var samples = profiles
            .SelectMany(profile => profile.TopRegressedSamples)
            .Where(sample => focusIds.Contains(sample.SampleId) && sample.CandidateBreakdown.Count > 0)
            .OrderBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(sample => sample.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (samples.Length == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No candidate-level diagnostics were present for the focus samples in the input eval reports.");
            sb.AppendLine();
            return;
        }

        AppendCandidateBreakdownTable(sb, samples, candidate => candidate.IsMustHit || candidate.SelectedByCurrentPolicy || candidate.AttentionRank <= 5);
    }

    private static void AppendMustNotHitDiagnostics(
        StringBuilder sb,
        IReadOnlyList<AttentionProfileSelectionProfileReport> profiles)
    {
        var samples = profiles
            .SelectMany(profile => profile.TopRegressedSamples.Concat(profile.TopImprovedSamples))
            .Where(sample => sample.MustNotHitPromotedCount > 0 && sample.CandidateBreakdown.Any(candidate => candidate.IsMustNotHit))
            .OrderByDescending(sample => sample.MustNotHitPromotedCount)
            .ThenBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        if (samples.Length == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No promoted mustNotHit candidate diagnostics were present in the input eval reports.");
            sb.AppendLine();
            return;
        }

        AppendCandidateBreakdownTable(sb, samples, candidate => candidate.IsMustNotHit);
    }

    private static void AppendCandidateBreakdownTable(
        StringBuilder sb,
        IReadOnlyList<AttentionProfileSelectionSampleDelta> samples,
        Func<ContextEvalAttentionCandidateDiagnostic, bool> filter)
    {
        sb.AppendLine();
        sb.AppendLine("| Scope | Profile | Sample | Candidate | Label | Current | Attention | Delta | Selected | Would | Scores | Channels | ScoreBreakdown | Reasons |");
        sb.AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---|---|---|---|");
        foreach (var sample in samples)
        {
            foreach (var candidate in sample.CandidateBreakdown.Where(filter).Take(8))
            {
                var reasons = string.Join("; ", candidate.Reasons.Take(5));
                sb.AppendLine($"| {sample.Scope} | {sample.ProfileId} | {sample.SampleId} | {candidate.SourceId} | {FormatCandidateLabel(candidate)} | {candidate.CurrentRank} | {candidate.AttentionRank} | {candidate.RankDelta:+0;-0;0} | {FormatBool(candidate.SelectedByCurrentPolicy)} | {FormatBool(candidate.WouldBeSelectedByAttention)} | {FormatCandidateScores(candidate)} | {string.Join(',', candidate.ChannelSources)} | {candidate.ScoreBreakdown} | {reasons} |");
            }
        }

        sb.AppendLine();
    }

    private static string FormatCandidateLabel(ContextEvalAttentionCandidateDiagnostic candidate)
    {
        if (candidate.IsMustHit && candidate.IsMustNotHit)
        {
            return "MustHit/MustNotHit";
        }

        if (candidate.IsMustHit)
        {
            return "MustHit";
        }

        return candidate.IsMustNotHit ? "MustNotHit" : string.Empty;
    }

    private static string FormatCandidateScores(ContextEvalAttentionCandidateDiagnostic candidate)
    {
        return string.Join("; ", candidate.AttentionScoreBreakdown
            .Where(pair => pair.Key is "queryMatch" or "relation" or "learningFeedback" or "noiseRisk" or "final")
            .Select(pair => $"{pair.Key}={pair.Value:0.###}"));
    }

    private static string FormatBool(bool value) => value ? "yes" : "no";
}
