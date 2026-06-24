using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreAttentionScorerTests
{
    [TestMethod]
    public async Task AttentionScorer_ShouldProduceScoreBreakdown()
    {
        var scorer = new RuleBasedContextAttentionScorer();
        var scores = await scorer.ScoreAsync(Request(), [Candidate("ctx-1", score: 8.0)]);

        Assert.AreEqual(1, scores.Count);
        Assert.AreEqual("default-shadow-v1", scores[0].ProfileId);
        Assert.IsTrue(scores[0].FinalAttentionScore > 0);
        Assert.AreEqual(1, scores[0].AttentionRank);
        Assert.IsTrue(scores[0].QueryMatchScore > 0);
        Assert.IsTrue(scores[0].ImportanceScore > 0);
        Assert.IsTrue(scores[0].ChannelScore > 0);
        Assert.IsTrue(scores[0].Reasons.Count > 0);
        Assert.AreEqual("ctx-1", scores[0].FeatureVector.SourceId);
    }

    [TestMethod]
    public async Task AcceptedLearningFeedback_ShouldBoostAttentionScore()
    {
        var learningStore = new InMemoryContextLearningStore();
        await learningStore.AddRecordAsync(LearningRecord("ctx-boost", "PromotionAccepted", ContextFeedbackSignal.Positive, ContextFailureType.None));
        var scorer = new RuleBasedContextAttentionScorer(learningStore: learningStore);

        var baseline = (await new RuleBasedContextAttentionScorer().ScoreAsync(Request(), [Candidate("ctx-boost", score: 5.0)])).Single();
        var boosted = (await scorer.ScoreAsync(Request(), [Candidate("ctx-boost", score: 5.0)])).Single();

        Assert.IsTrue(boosted.FinalAttentionScore > baseline.FinalAttentionScore);
        Assert.IsTrue(boosted.LearningFeedbackScore > 0);
        CollectionAssert.Contains(boosted.Reasons.ToArray(), "positive_learning_feedback");
    }

    [TestMethod]
    public async Task RejectedLearningFeedback_ShouldPenalizeAttentionScore()
    {
        var learningStore = new InMemoryContextLearningStore();
        await learningStore.AddRecordAsync(LearningRecord("ctx-noise", "PromotionRejected", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive));
        var scorer = new RuleBasedContextAttentionScorer(learningStore: learningStore);

        var baseline = (await new RuleBasedContextAttentionScorer().ScoreAsync(Request(), [Candidate("ctx-noise", score: 5.0)])).Single();
        var penalized = (await scorer.ScoreAsync(Request(), [Candidate("ctx-noise", score: 5.0)])).Single();

        Assert.IsTrue(penalized.FinalAttentionScore < baseline.FinalAttentionScore);
        Assert.IsTrue(penalized.LearningFeedbackScore < 0);
        Assert.IsTrue(penalized.NoiseRiskScore > 0);
        CollectionAssert.Contains(penalized.Reasons.ToArray(), "negative_learning_feedback");
    }

    [TestMethod]
    public async Task DeprecatedItem_ShouldGetLifecyclePenalty()
    {
        var scorer = new RuleBasedContextAttentionScorer();
        var score = (await scorer.ScoreAsync(Request(), [Candidate(
            "mem-deprecated",
            ContextRetrievalCandidateKind.MemoryItem,
            score: 7.0,
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["channelSources"] = "memory",
                ["memoryLayer"] = "Stable",
                ["lifecycleStatus"] = "Deprecated",
                ["importance"] = "0.8"
            })])).Single();

        Assert.IsTrue(score.LifecyclePenalty > 0);
        CollectionAssert.Contains(score.Reasons.ToArray(), "deprecated_lifecycle_penalty");
    }

    [TestMethod]
    public void AttentionShadowReportBuilder_ShouldComputeRankDiffAndPromotionRisk()
    {
        var request = new ContextRetrievalRequest
        {
            OperationId = "attention-report-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 2,
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["attention.mustHit"] = "ctx-must",
                ["attention.mustNotHit"] = "ctx-noise"
            }
        };
        var candidates = new[]
        {
            Candidate("ctx-safe", score: 10.0),
            Candidate("ctx-must", score: 9.0),
            Candidate("ctx-noise", score: 1.0)
        };
        var currentPacking = RetrievalPackingPolicy.Pack(request, candidates);
        var attentionScores = new[]
        {
            Score(candidates[0], currentRank: 1, attentionRank: 3, attentionScore: 0.20),
            Score(candidates[1], currentRank: 2, attentionRank: 2, attentionScore: 0.70),
            Score(candidates[2], currentRank: 3, attentionRank: 1, attentionScore: 0.95)
        };

        var report = AttentionShadowReportBuilder.Build(
            request.OperationId,
            request,
            candidates,
            currentPacking,
            attentionScores);

        Assert.AreEqual(3, report.Ranks.Count);
        Assert.IsTrue(report.WouldChangeSelectedSet);
        Assert.AreEqual(1, report.AddedByAttention.Count);
        Assert.AreEqual("ctx-noise", report.AddedByAttention[0].SourceId);
        Assert.AreEqual(1, report.DroppedByAttention.Count);
        Assert.AreEqual("ctx-safe", report.DroppedByAttention[0].SourceId);
        Assert.AreEqual(1, report.MustNotHitPromotedCount);
        CollectionAssert.Contains(report.Warnings.ToArray(), "must_not_hit_promoted");

        var mustHit = report.Ranks.Single(rank => rank.SourceId == "ctx-must");
        Assert.IsTrue(mustHit.IsMustHit);
        Assert.AreEqual(2, mustHit.AttentionRank);

        var noise = report.Ranks.Single(rank => rank.SourceId == "ctx-noise");
        Assert.IsTrue(noise.IsMustNotHit);
        Assert.AreEqual(2, noise.RankDelta);
        Assert.IsTrue(noise.WouldBeSelectedByAttention);
    }

    [TestMethod]
    public async Task AttentionProfiles_ShouldExposeExperimentVariantsAndProduceScores()
    {
        var profiles = ContextAttentionProfile.CreateShadowExperimentProfiles();
        var profileIds = profiles.Select(profile => profile.ProfileId).ToArray();

        CollectionAssert.Contains(profileIds, "default-shadow-v1");
        CollectionAssert.Contains(profileIds, "conservative-v1");
        CollectionAssert.Contains(profileIds, "relation-balanced-v1");
        CollectionAssert.Contains(profileIds, "learning-light-v1");
        CollectionAssert.Contains(profileIds, "lifecycle-strict-v1");
        CollectionAssert.Contains(profileIds, "old-score-anchored-v1");
        CollectionAssert.Contains(profileIds, "old-score-anchored-v1-light");
        CollectionAssert.Contains(profileIds, "old-score-anchored-v1-balanced");
        CollectionAssert.Contains(profileIds, "old-score-anchored-v1-strong");
        CollectionAssert.Contains(profileIds, "delta-limited-v1");
        CollectionAssert.Contains(profileIds, "guarded-shadow-v1");

        var conservative = profiles.Single(profile => profile.ProfileId == "conservative-v1");
        var scorer = new RuleBasedContextAttentionScorer(conservative);
        var scores = await scorer.ScoreAsync(Request(), [Candidate("ctx-profile", score: 8.0)]);

        Assert.AreEqual(1, scores.Count);
        Assert.AreEqual("conservative-v1", scores[0].ProfileId);
        Assert.AreEqual("context-attention-shadow-policy/conservative-v1", scores[0].PolicyVersion);
        Assert.IsTrue(scores[0].FinalAttentionScore > 0);
        Assert.IsTrue(scores[0].Reasons.Count > 0);
    }

    [TestMethod]
    public async Task OldScoreAnchoredProfile_ShouldKeepCurrentTopCandidateAnchored()
    {
        var scorer = new RuleBasedContextAttentionScorer(ContextAttentionProfile.CreateOldScoreAnchoredV1());
        var candidates = new[]
        {
            Candidate("ctx-current-top", score: 10.0),
            Candidate(
                "ctx-attention-favored",
                score: 2.0,
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["channelSources"] = "keyword,memory,relation",
                    ["matchedTokens"] = "attention,scorer,query,match,token",
                    ["matchedAnchors"] = "attention,scorer,anchor",
                    ["relationPaths"] = "ctx-a->ctx-b|ctx-b->ctx-c",
                    ["importance"] = "1.0",
                    ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O")
                })
        };

        var scores = await scorer.ScoreAsync(Request(), candidates);

        Assert.AreEqual(1, scores.Single(score => score.SourceId == "ctx-current-top").AttentionRank);
        CollectionAssert.Contains(scores.Single(score => score.SourceId == "ctx-current-top").Reasons.ToArray(), "old_score_anchor");
    }

    [TestMethod]
    public async Task OldScoreAnchoredSweepProfiles_ShouldExposeControlsAndApplyBoostReasons()
    {
        var profile = ContextAttentionProfile.CreateOldScoreAnchoredV1Strong();
        var scorer = new RuleBasedContextAttentionScorer(profile);
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "attention scorer",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["attention.mustHit"] = "ctx-must"
            }
        };
        var candidates = new[]
        {
            Candidate("ctx-top", score: 10.0),
            Candidate(
                "ctx-must",
                score: 6.0,
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["channelSources"] = "keyword,memory,relation",
                    ["matchedTokens"] = "attention,scorer",
                    ["matchedAnchors"] = "attention",
                    ["memoryLayer"] = "working_memory",
                    ["relationPaths"] = "ctx-a->ctx-must",
                    ["importance"] = "0.8",
                    ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O")
                })
        };

        var scores = await scorer.ScoreAsync(request, candidates);
        var mustHit = scores.Single(score => score.SourceId == "ctx-must");

        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "oldScoreAnchorWeight");
        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "mustHitBoost");
        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "constraintBoost");
        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "shortTermBoost");
        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "lifecycleRiskPenalty");
        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "relationEvidenceBoost");
        CollectionAssert.Contains(profile.Controls.Keys.ToArray(), "recencyBoost");
        CollectionAssert.Contains(mustHit.Reasons.ToArray(), "old_score_anchor");
        CollectionAssert.Contains(mustHit.Reasons.ToArray(), "must_hit_boost");
        CollectionAssert.Contains(mustHit.Reasons.ToArray(), "short_term_boost");
        CollectionAssert.Contains(mustHit.Reasons.ToArray(), "relation_evidence_boost");
        CollectionAssert.Contains(mustHit.Reasons.ToArray(), "recency_boost");
    }

    [TestMethod]
    public void RetrievalAttentionRerankOptions_DefaultsShouldBeLimitedOptInOff()
    {
        var options = new RetrievalAttentionRerankOptions();

        Assert.AreEqual(RetrievalAttentionRerankOptions.OffMode, options.Mode);
        Assert.AreEqual(RetrievalAttentionRerankOptions.OffMode, options.EffectiveMode);
        Assert.AreEqual("old-score-anchored-v1-strong", options.Profile);
        Assert.AreEqual("old-score-anchored-v1-strong", options.EffectiveProfile);
        Assert.IsTrue(options.PreserveSelectedSet);
        Assert.IsFalse(options.AllowSelectedSetMutation);
        Assert.IsTrue(options.EmitShadowTrace);
        Assert.IsFalse(options.ShouldApplyGuarded);
    }

    [TestMethod]
    public async Task DeltaLimitedProfile_ShouldProtectCurrentTopThree()
    {
        var scorer = new RuleBasedContextAttentionScorer(ContextAttentionProfile.CreateDeltaLimitedV1());
        var candidates = new[]
        {
            Candidate("ctx-top-1", score: 8.0),
            Candidate("ctx-top-2", score: 7.0),
            Candidate("ctx-top-3", score: 6.0),
            Candidate(
                "ctx-attention-favored",
                score: 1.0,
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["channelSources"] = "keyword,memory,relation",
                    ["matchedTokens"] = "attention,scorer,query,match,token",
                    ["matchedAnchors"] = "attention,scorer,anchor",
                    ["relationPaths"] = "ctx-a->ctx-b|ctx-b->ctx-c",
                    ["importance"] = "1.0",
                    ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O")
                })
        };

        var scores = await scorer.ScoreAsync(Request(), candidates);

        Assert.AreEqual(1, scores.Single(score => score.SourceId == "ctx-top-1").AttentionRank);
        Assert.AreEqual(2, scores.Single(score => score.SourceId == "ctx-top-2").AttentionRank);
        Assert.AreEqual(3, scores.Single(score => score.SourceId == "ctx-top-3").AttentionRank);
        Assert.IsTrue(scores.Single(score => score.SourceId == "ctx-attention-favored").AttentionRank >= 2);
    }

    [TestMethod]
    public void GuardedShadowProfile_ShouldPreventMustNotHitWouldSelect()
    {
        var request = new ContextRetrievalRequest
        {
            OperationId = "guarded-shadow-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 2,
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["attention.mustNotHit"] = "ctx-noise"
            }
        };
        var candidates = new[]
        {
            Candidate("ctx-safe-1", score: 10.0),
            Candidate("ctx-safe-2", score: 9.0),
            Candidate("ctx-noise", score: 1.0)
        };
        var currentPacking = RetrievalPackingPolicy.Pack(request, candidates);
        var attentionScores = new[]
        {
            Score(candidates[0], currentRank: 1, attentionRank: 3, attentionScore: 0.20, profileId: "guarded-shadow-v1"),
            Score(candidates[1], currentRank: 2, attentionRank: 2, attentionScore: 0.70, profileId: "guarded-shadow-v1"),
            Score(candidates[2], currentRank: 3, attentionRank: 1, attentionScore: 0.95, profileId: "guarded-shadow-v1")
        };

        var report = AttentionShadowReportBuilder.Build(
            request.OperationId,
            request,
            candidates,
            currentPacking,
            attentionScores);

        var noise = report.Ranks.Single(rank => rank.SourceId == "ctx-noise");
        Assert.IsTrue(noise.IsMustNotHit);
        Assert.IsFalse(noise.WouldBeSelectedByAttention);
        CollectionAssert.Contains(noise.Reasons.ToArray(), "guarded_must_not_hit_filtered");
        Assert.AreEqual(0, report.Ranks.Count(rank => rank.IsMustNotHit && rank.WouldBeSelectedByAttention));
    }

    [TestMethod]
    public void GuardedAttentionRerankPolicy_ShouldReorderSelectedItemsOnly()
    {
        var request = new ContextRetrievalRequest
        {
            OperationId = "guarded-rerank-order-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 2,
            TokenBudget = 1000
        };
        var candidates = new[]
        {
            Candidate("ctx-current-top", score: 10.0),
            Candidate("ctx-attention-top", score: 9.0),
            Candidate("ctx-dropped", score: 1.0)
        };
        var packed = RetrievalPackingPolicy.Pack(request, candidates);
        var scores = new[]
        {
            Score(candidates[0], currentRank: 1, attentionRank: 2, attentionScore: 0.50, profileId: "old-score-anchored-v1"),
            Score(candidates[1], currentRank: 2, attentionRank: 1, attentionScore: 0.95, profileId: "old-score-anchored-v1"),
            Score(candidates[2], currentRank: 3, attentionRank: 3, attentionScore: 0.10, profileId: "old-score-anchored-v1")
        };

        var result = new GuardedAttentionRerankPolicy(new RetrievalAttentionRerankOptions
        {
            Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
            Profile = "old-score-anchored-v1"
        }).Apply(request.OperationId, request, packed, scores);

        Assert.IsTrue(result.Report.Applied);
        Assert.IsTrue(result.Report.AttentionApplied);
        Assert.AreEqual(RetrievalAttentionRerankOptions.ApplyGuardedMode, result.Report.AttentionRerankMode);
        Assert.AreEqual("old-score-anchored-v1", result.Report.AttentionProfile);
        Assert.IsTrue(result.Report.SelectedSetPreserved);
        Assert.AreEqual(2, result.Report.OrderChangedCount);
        Assert.AreEqual(0, result.Report.SelectedSetChangeCount);
        CollectionAssert.AreEquivalent(
            packed.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray(),
            result.PackingResult.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-attention-top", "ctx-current-top" },
            result.PackingResult.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray());
        Assert.AreEqual(2, result.Report.OrderChanges.Count);
        Assert.AreEqual(0, result.Report.AddedItems.Count);
        Assert.AreEqual(0, result.Report.DroppedItems.Count);
        CollectionAssert.AreEqual(
            new[] { "ctx-current-top", "ctx-attention-top" },
            result.Report.OldSelectedOrder.Select(item => item.SourceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-attention-top", "ctx-current-top" },
            result.Report.NewSelectedOrder.Select(item => item.SourceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-current-top", "ctx-attention-top" },
            result.Report.OldOrder.ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-attention-top", "ctx-current-top" },
            result.Report.NewOrder.ToArray());
        Assert.AreEqual(1, result.Report.MovedUpItems.Count);
        Assert.AreEqual(1, result.Report.MovedDownItems.Count);
        var movedUp = result.Report.MovedUpItems.Single();
        Assert.AreEqual("ctx-attention-top", movedUp.SourceId);
        Assert.AreEqual(2, movedUp.OldRank);
        Assert.AreEqual(1, movedUp.NewRank);
        Assert.AreEqual(9.0, movedUp.OldScore);
        Assert.AreEqual(0.95, movedUp.AttentionScore);
        Assert.AreEqual(0.95, movedUp.FinalScore);
        Assert.AreEqual("attention_rank_promoted", movedUp.MoveReason);
        Assert.IsTrue(movedUp.AttentionScoreBreakdown.ContainsKey("final"));
        CollectionAssert.Contains(movedUp.Reasons.ToArray(), "test_attention_rank");
    }

    [TestMethod]
    public void GuardedAttentionRerankPolicy_ShadowMode_ShouldCompareWithoutChangingOrder()
    {
        var request = new ContextRetrievalRequest
        {
            OperationId = "guarded-rerank-shadow-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 2,
            TokenBudget = 1000
        };
        var candidates = new[]
        {
            Candidate("ctx-current-top", score: 10.0),
            Candidate("ctx-attention-top", score: 9.0)
        };
        var packed = RetrievalPackingPolicy.Pack(request, candidates);
        var scores = new[]
        {
            Score(candidates[0], currentRank: 1, attentionRank: 2, attentionScore: 0.50, profileId: "old-score-anchored-v1-strong"),
            Score(candidates[1], currentRank: 2, attentionRank: 1, attentionScore: 0.95, profileId: "old-score-anchored-v1-strong")
        };

        var result = new GuardedAttentionRerankPolicy(new RetrievalAttentionRerankOptions
        {
            Mode = RetrievalAttentionRerankOptions.ShadowMode,
            Profile = "old-score-anchored-v1-strong"
        }).Apply(request.OperationId, request, packed, scores);

        Assert.IsFalse(result.Report.AttentionApplied);
        Assert.IsTrue(result.Report.Skipped);
        Assert.AreEqual("shadow", result.Report.SkippedReason);
        Assert.AreEqual(RetrievalAttentionRerankOptions.ShadowMode, result.Report.AttentionRerankMode);
        Assert.AreEqual(2, result.Report.OrderChangedCount);
        Assert.IsTrue(result.Report.SelectedSetPreserved);
        CollectionAssert.AreEqual(
            new[] { "ctx-current-top", "ctx-attention-top" },
            result.PackingResult.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-current-top", "ctx-attention-top" },
            result.Report.OldOrder.ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-attention-top", "ctx-current-top" },
            result.Report.NewOrder.ToArray());
    }

    [TestMethod]
    public void GuardedAttentionRerankPolicy_ShouldBlockMustNotHitPromotion()
    {
        var request = new ContextRetrievalRequest
        {
            OperationId = "guarded-rerank-must-not-hit-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 2,
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["attention.mustNotHit"] = "ctx-noise"
            }
        };
        var candidates = new[]
        {
            Candidate("ctx-safe", score: 10.0),
            Candidate("ctx-noise", score: 9.0)
        };
        var packed = RetrievalPackingPolicy.Pack(request, candidates);
        var scores = new[]
        {
            Score(candidates[0], currentRank: 1, attentionRank: 2, attentionScore: 0.50, profileId: "old-score-anchored-v1"),
            Score(candidates[1], currentRank: 2, attentionRank: 1, attentionScore: 0.99, profileId: "old-score-anchored-v1")
        };

        var result = new GuardedAttentionRerankPolicy(new RetrievalAttentionRerankOptions
        {
            Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
            Profile = "old-score-anchored-v1"
        }).Apply(request.OperationId, request, packed, scores);

        Assert.IsTrue(result.Report.Blocked);
        Assert.AreEqual("must_not_hit_promotion_blocked", result.Report.BlockedReason);
        Assert.AreEqual("must_not_hit_promotion_blocked", result.Report.GuardViolation);
        Assert.IsFalse(result.Report.AttentionApplied);
        Assert.IsTrue(result.Report.SelectedSetPreserved);
        CollectionAssert.AreEqual(
            packed.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray(),
            result.PackingResult.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "ctx-safe", "ctx-noise" },
            result.Report.NewOrder.ToArray());
        Assert.AreEqual(1, result.Report.MustNotHitRankDeltas.Count);
    }

    [TestMethod]
    public void GuardedAttentionRerankPolicy_ShouldBlockHardConstraintDemotion()
    {
        var request = new ContextRetrievalRequest
        {
            OperationId = "guarded-rerank-hard-constraint-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 2,
            TokenBudget = 1000
        };
        var hardConstraint = Candidate(
            "ctx-hard",
            score: 10.0,
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["channelSources"] = "keyword",
                ["constraintLevel"] = "hard",
                ["importance"] = "1.0"
            });
        hardConstraint = new ContextRetrievalCandidate
        {
            CandidateId = hardConstraint.CandidateId,
            SourceId = hardConstraint.SourceId,
            Kind = hardConstraint.Kind,
            Type = "hard-constraint",
            Content = hardConstraint.Content,
            ContentFormat = hardConstraint.ContentFormat,
            Tags = ["constraint"],
            SourceRefs = hardConstraint.SourceRefs,
            Score = hardConstraint.Score,
            EstimatedTokens = hardConstraint.EstimatedTokens,
            Reasons = hardConstraint.Reasons,
            Metadata = hardConstraint.Metadata
        };
        var other = Candidate("ctx-other", score: 9.0);
        var candidates = new[] { hardConstraint, other };
        var packed = RetrievalPackingPolicy.Pack(request, candidates);
        var scores = new[]
        {
            Score(candidates[0], currentRank: 1, attentionRank: 2, attentionScore: 0.20, profileId: "old-score-anchored-v1"),
            Score(candidates[1], currentRank: 2, attentionRank: 1, attentionScore: 0.95, profileId: "old-score-anchored-v1")
        };

        var result = new GuardedAttentionRerankPolicy(new RetrievalAttentionRerankOptions
        {
            Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
            Profile = "old-score-anchored-v1"
        }).Apply(request.OperationId, request, packed, scores);

        Assert.IsTrue(result.Report.Blocked);
        Assert.AreEqual("hard_constraint_demotion_blocked", result.Report.BlockedReason);
        Assert.AreEqual("hard_constraint_demotion_blocked", result.Report.GuardViolation);
        Assert.IsFalse(result.Report.AttentionApplied);
        CollectionAssert.AreEqual(
            new[] { "ctx-hard", "ctx-other" },
            result.PackingResult.SelectedCandidates.Select(candidate => candidate.SourceId).ToArray());
    }

    private static ContextRetrievalRequest Request()
    {
        return new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "attention scorer"
        };
    }

    private static ContextRetrievalCandidate Candidate(
        string sourceId,
        ContextRetrievalCandidateKind kind = ContextRetrievalCandidateKind.ContextItem,
        double score = 5.0,
        Dictionary<string, string>? metadata = null)
    {
        return new ContextRetrievalCandidate
        {
            CandidateId = $"{kind}:{sourceId}",
            SourceId = sourceId,
            Kind = kind,
            Type = "note",
            Content = "attention scorer test content",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["attention"],
            SourceRefs = [$"source:{sourceId}"],
            Score = score,
            EstimatedTokens = 4,
            Reasons = ["test"],
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["channelSources"] = "keyword,memory",
                ["matchedTokens"] = "attention,scorer",
                ["matchedAnchors"] = "attention",
                ["importance"] = "0.7",
                ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };
    }

    private static ContextLearningRecord LearningRecord(
        string sourceId,
        string eventKind,
        ContextFeedbackSignal signal,
        ContextFailureType failureType)
    {
        return new ContextLearningRecord
        {
            RecordId = $"record-{sourceId}-{signal}",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceKind = "test",
            SourceId = sourceId,
            EventKind = eventKind,
            Signal = signal,
            FailureType = failureType,
            Reason = "test feedback",
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = [$"source:{sourceId}"],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextAttentionScore Score(
        ContextRetrievalCandidate candidate,
        int currentRank,
        int attentionRank,
        double attentionScore,
        string profileId = "test")
    {
        return new ContextAttentionScore
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            CandidateKind = candidate.Kind,
            CurrentRank = currentRank,
            AttentionRank = attentionRank,
            FinalAttentionScore = attentionScore,
            QueryMatchScore = attentionScore,
            Reasons = ["test_attention_rank"],
            ProfileId = profileId,
            PolicyVersion = $"test/{profileId}"
        };
    }
}
