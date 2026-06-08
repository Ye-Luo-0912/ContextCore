using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Services;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreAttentionProfileSelectionTests
{
    [TestMethod]
    public void AttentionProfileSelectionRunner_ShouldGenerateReportWithRecommendedProfile()
    {
        var runner = new AttentionProfileSelectionRunner();
        var baseline = Report(
            totalSamples: 50,
            passRate: 1.0,
            currentRecall5: 0.95,
            currentNoise: 0,
            Profile("valid-profile", mrr: 0.82, recall3: 0.85, recall5: 0.95, improved: 8, regressed: 3, mustNotHit: 0, change: 0.05),
            Profile("risky-profile", mrr: 0.90, recall3: 0.90, recall5: 0.96, improved: 10, regressed: 2, mustNotHit: 1, change: 0.05));
        var extended = Report(
            totalSamples: 113,
            passRate: 0.86,
            currentRecall5: 0.91,
            currentNoise: 0,
            Profile("valid-profile", mrr: 0.83, recall3: 0.84, recall5: 0.91, improved: 10, regressed: 4, mustNotHit: 0, change: 0.10),
            Profile("risky-profile", mrr: 0.91, recall3: 0.90, recall5: 0.92, improved: 12, regressed: 2, mustNotHit: 1, change: 0.10));

        var report = runner.Generate(baseline, extended, "baseline.json", "extended.json");

        Assert.AreEqual("valid-profile", report.RecommendedProfile);
        Assert.AreEqual("guarded-rerank-candidate", report.RecommendedMode);
        Assert.AreEqual("low", report.RiskLevel);
        Assert.IsTrue(report.SafetyGate.Passed);
        Assert.IsTrue(report.Profiles.Count >= 2);
        Assert.IsTrue(report.Profiles.Single(profile => profile.ProfileId == "valid-profile").TopImprovedSamples.Count > 0);
        Assert.IsTrue(AttentionProfileSelectionRunner.BuildMarkdownReport(report).Contains("valid-profile", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AttentionProfileSafetyGate_ShouldFailWhenMustNotHitPromoted()
    {
        var result = AttentionProfileSelectionRunner.EvaluateSafetyGate(
            Metrics(mustNotHit: 1),
            Metrics(scope: "extended"));

        Assert.IsFalse(result.Passed);
        CollectionAssert.Contains(result.BlockingIssues.ToArray(), "must_not_hit_promoted_zero");
    }

    [TestMethod]
    public void AttentionProfileSafetyGate_ShouldFailWhenChangeRatioTooHigh()
    {
        var baselineFailure = AttentionProfileSelectionRunner.EvaluateSafetyGate(
            Metrics(change: 0.081),
            Metrics(scope: "extended"));
        var extendedFailure = AttentionProfileSelectionRunner.EvaluateSafetyGate(
            Metrics(),
            Metrics(scope: "extended", change: 0.151));

        Assert.IsFalse(baselineFailure.Passed);
        CollectionAssert.Contains(baselineFailure.BlockingIssues.ToArray(), "baseline_selected_set_change_ratio_limit");
        Assert.IsFalse(extendedFailure.Passed);
        CollectionAssert.Contains(extendedFailure.BlockingIssues.ToArray(), "extended_selected_set_change_ratio_limit");
    }

    [TestMethod]
    public void AttentionProfileSafetyGate_ShouldPassForValidMetrics()
    {
        var result = AttentionProfileSelectionRunner.EvaluateSafetyGate(
            Metrics(),
            Metrics(scope: "extended", change: 0.10));

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(0, result.BlockingIssues.Count);
    }

    [TestMethod]
    public void AttentionProfileSelectionRunner_ShouldIncludeRegressionCandidateDiagnostics()
    {
        var runner = new AttentionProfileSelectionRunner();
        var baseline = Report(
            totalSamples: 50,
            passRate: 1.0,
            currentRecall5: 0.95,
            currentNoise: 0,
            Profile("guarded-shadow-v1", mrr: 0.82, recall3: 0.85, recall5: 0.95, improved: 3, regressed: 1, mustNotHit: 0, change: 0.05));
        var extended = new ContextEvalReport
        {
            TotalSamples = 113,
            PassRate = 0.9,
            AvgRetrievalRecall5 = 0.90,
            AvgRetrievalNoiseViolationRatio = 0,
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "project-sample-009",
                    Mode = "ProjectMode",
                    AttentionProfiles =
                    [
                        new ContextEvalAttentionProfileResult
                        {
                            ProfileId = "guarded-shadow-v1",
                            PolicyVersion = "policy/guarded-shadow-v1",
                            CurrentMrr = 1.0,
                            AttentionMrr = 0.5,
                            AttentionRecall3 = 0.5,
                            AttentionRecall5 = 0.5,
                            Regressed = true,
                            MustHitDemotedCount = 1,
                            MustNotHitPromotedCount = 1,
                            SelectedSetChangeRatio = 0.10,
                            CandidateDiagnostics =
                            [
                                new ContextEvalAttentionCandidateDiagnostic
                                {
                                    CandidateId = "ContextItem:must-hit",
                                    SourceId = "must-hit",
                                    CurrentRank = 1,
                                    AttentionRank = 4,
                                    RankDelta = -3,
                                    CurrentScore = 10,
                                    AttentionScore = 0.40,
                                    SelectedByCurrentPolicy = true,
                                    WouldBeSelectedByAttention = false,
                                    IsMustHit = true,
                                    ChannelSources = ["keyword"],
                                    ScoreBreakdown = "keyword=10;total=10",
                                    AttentionScoreBreakdown = new Dictionary<string, double>
                                    {
                                        ["queryMatch"] = 0.6,
                                        ["relation"] = 0.1,
                                        ["final"] = 0.4
                                    },
                                    Reasons = ["must_hit_demoted"]
                                },
                                new ContextEvalAttentionCandidateDiagnostic
                                {
                                    CandidateId = "ContextItem:noise",
                                    SourceId = "noise",
                                    CurrentRank = 6,
                                    AttentionRank = 2,
                                    RankDelta = 4,
                                    CurrentScore = 1,
                                    AttentionScore = 0.90,
                                    WouldBeSelectedByAttention = true,
                                    IsMustNotHit = true,
                                    ChannelSources = ["relation"],
                                    ScoreBreakdown = "relation=8;total=8",
                                    AttentionScoreBreakdown = new Dictionary<string, double>
                                    {
                                        ["queryMatch"] = 0.1,
                                        ["relation"] = 1.0,
                                        ["noiseRisk"] = 0.2,
                                        ["final"] = 0.9
                                    },
                                    Reasons = ["relation_path_present"]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var report = runner.Generate(baseline, extended);
        var profile = report.Profiles.Single(item => item.ProfileId == "guarded-shadow-v1");
        var markdown = AttentionProfileSelectionRunner.BuildMarkdownReport(report);

        Assert.AreEqual(1, profile.Extended.CurrentMrrOneRegressionCount);
        Assert.IsTrue(profile.TopRegressedSamples
            .Single(sample => sample.SampleId == "project-sample-009")
            .CandidateBreakdown.Count >= 2);
        StringAssert.Contains(markdown, "Focus Regression Candidate Breakdown");
        StringAssert.Contains(markdown, "project-sample-009");
        StringAssert.Contains(markdown, "MustNotHit Promotion Diagnostics");
        StringAssert.Contains(markdown, "noise");
    }

    private static ContextEvalReport Report(
        int totalSamples,
        double passRate,
        double currentRecall5,
        double currentNoise,
        params ContextEvalAttentionProfileSummary[] profiles)
    {
        return new ContextEvalReport
        {
            TotalSamples = totalSamples,
            PassRate = passRate,
            AvgRetrievalRecall5 = currentRecall5,
            AvgRetrievalNoiseViolationRatio = currentNoise,
            AttentionProfileSummaries = profiles,
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "sample-improved",
                    Mode = "ChatMode",
                    AttentionProfiles = profiles.Select(profile => new ContextEvalAttentionProfileResult
                    {
                        ProfileId = profile.ProfileId,
                        PolicyVersion = profile.PolicyVersion,
                        CurrentMrr = 0.25,
                        AttentionMrr = Math.Min(1.0, profile.AvgAttentionMrr),
                        AttentionRecall3 = profile.AvgAttentionRecall3,
                        AttentionRecall5 = profile.AvgAttentionRecall5,
                        Improved = true,
                        MustNotHitPromotedCount = profile.MustNotHitPromotedCount,
                        SelectedSetChangeRatio = profile.SelectedSetChangeRatio
                    }).ToArray()
                },
                new ContextEvalResult
                {
                    SampleId = "sample-regressed",
                    Mode = "CodingMode",
                    AttentionProfiles = profiles.Select(profile => new ContextEvalAttentionProfileResult
                    {
                        ProfileId = profile.ProfileId,
                        PolicyVersion = profile.PolicyVersion,
                        CurrentMrr = 0.9,
                        AttentionMrr = Math.Max(0.0, profile.AvgAttentionMrr - 0.2),
                        AttentionRecall3 = profile.AvgAttentionRecall3,
                        AttentionRecall5 = profile.AvgAttentionRecall5,
                        Regressed = true,
                        MustHitDemotedCount = 1,
                        MustNotHitPromotedCount = profile.MustNotHitPromotedCount,
                        SelectedSetChangeRatio = profile.SelectedSetChangeRatio
                    }).ToArray()
                }
            ]
        };
    }

    private static ContextEvalAttentionProfileSummary Profile(
        string profileId,
        double mrr,
        double recall3,
        double recall5,
        int improved,
        int regressed,
        int mustNotHit,
        double change)
    {
        return new ContextEvalAttentionProfileSummary
        {
            ProfileId = profileId,
            PolicyVersion = $"policy/{profileId}",
            SampleCount = 10,
            AvgAttentionMrr = mrr,
            AvgAttentionRecall3 = recall3,
            AvgAttentionRecall5 = recall5,
            ImprovedSamples = improved,
            RegressedSamples = regressed,
            MustNotHitPromotedCount = mustNotHit,
            SelectedSetChangeRatio = change,
            CategoryBreakdown =
            [
                new ContextEvalAttentionProfileCategorySummary
                {
                    Category = "ChatMode",
                    SampleCount = 5,
                    AvgAttentionMrr = mrr,
                    AvgAttentionRecall3 = recall3,
                    AvgAttentionRecall5 = recall5,
                    ImprovedSamples = improved,
                    RegressedSamples = regressed,
                    MustNotHitPromotedCount = mustNotHit,
                    SelectedSetChangeRatio = change
                }
            ]
        };
    }

    private static AttentionProfileSelectionMetrics Metrics(
        string scope = "baseline",
        double passRate = 1.0,
        double currentRecall5 = 0.95,
        double currentNoise = 0,
        double attentionRecall5 = 0.94,
        int improved = 8,
        int regressed = 3,
        int mustNotHit = 0,
        double change = 0.05)
    {
        return new AttentionProfileSelectionMetrics
        {
            Scope = scope,
            TotalSamples = scope == "baseline" ? 50 : 113,
            PassRate = passRate,
            CurrentRecall5 = currentRecall5,
            CurrentNoiseRatio = currentNoise,
            AttentionMrr = 0.8,
            AttentionRecall3 = 0.8,
            AttentionRecall5 = attentionRecall5,
            ImprovedSamples = improved,
            RegressedSamples = regressed,
            MustNotHitPromotedCount = mustNotHit,
            SelectedSetChangeRatio = change
        };
    }
}
