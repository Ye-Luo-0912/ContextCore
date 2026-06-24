using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Attention;

/// <summary>计算检索候选的 shadow attention score，不参与排序或打包。</summary>
public interface IContextAttentionScorer
{
    Task<IReadOnlyList<ContextAttentionScore>> ScoreAsync(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        CancellationToken cancellationToken = default);
}

/// <summary>规则型 attention scorer，仅用于 shadow trace。</summary>
public sealed class RuleBasedContextAttentionScorer : IContextAttentionScorer
{
    private readonly IContextLearningStore? _learningStore;
    private readonly ContextAttentionProfile _profile;
    private readonly ContextAttentionScoringPolicy _policy;

    public RuleBasedContextAttentionScorer(
        ContextAttentionProfile? profile = null,
        IContextLearningStore? learningStore = null)
    {
        _profile = profile ?? ContextAttentionProfile.CreateDefaultShadowV1();
        _policy = ContextAttentionScoringPolicy.From(_profile);
        _learningStore = learningStore;
    }

    /// <summary>
    /// 异步计算给定请求和候选上下文的注意力分数。
    /// </summary>
    /// <param name="request">上下文检索请求。</param>
    /// <param name="rankedCandidates">已排序的上下文检索候选列表。</param>
    /// <param name="cancellationToken">用于取消操作的令牌。</param>
    /// <returns>返回一个只读列表，包含每个候选的注意力评分结果。</returns>
    /// <exception cref="ArgumentNullException">当<paramref name="request"/>或<paramref name="rankedCandidates"/>为null时抛出。</exception>
    public async Task<IReadOnlyList<ContextAttentionScore>> ScoreAsync(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rankedCandidates);

        var feedbackRecords = await LoadLearningRecordsAsync(request, cancellationToken).ConfigureAwait(false);
        var scores = rankedCandidates
            .Select((candidate, index) => ScoreCandidate(request, candidate, index + 1, feedbackRecords))
            .ToArray();
        var transformedScores = ApplyProfileScoreTransforms(request, scores);
        var attentionRanks = BuildAttentionRanks(transformedScores);

        return transformedScores
            .Select(score => WithAttentionRank(score, attentionRanks.GetValueOrDefault(score.CandidateId, score.CurrentRank)))
            .ToArray();
    }

    private IReadOnlyList<ContextAttentionScore> ApplyProfileScoreTransforms(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextAttentionScore> scores)
    {
        if (!IsOldScoreAnchoredProfile(_profile.ProfileId) || scores.Count == 0)
        {
            return scores;
        }

        var mustHitLabels = BuildLabelSet(request, isMustHit: true);
        var maxCurrentScore = scores
            .Select(ResolveCurrentScoreProxy)
            .DefaultIfEmpty(0d)
            .Max();
        var denominator = Math.Max(1, scores.Count - 1);
        var oldScoreAnchorWeight = Control("oldScoreAnchorWeight", double.NaN);

        return scores
            .Select(score =>
            {
                var currentRankScore = 1d - ((score.CurrentRank - 1d) / denominator);
                var currentScore = maxCurrentScore <= 0d
                    ? 0d
                    : Math.Clamp(ResolveCurrentScoreProxy(score) / maxCurrentScore, 0d, 1d);
                if (double.IsNaN(oldScoreAnchorWeight))
                {
                    var legacyScore =
                        Control("currentRankWeight") * currentRankScore
                        + Control("currentScoreWeight") * currentScore
                        + Control("attentionWeight", 0.10) * score.FinalAttentionScore;

                    return WithFinalAttentionScore(
                        score,
                        Math.Clamp(legacyScore, 0d, 1d),
                        "old_score_anchor");
                }

                var reasons = new List<string> { "old_score_anchor" };
                var anchorScore = BuildOldScoreAnchor(currentRankScore, currentScore);
                var tunedAttentionScore = ApplyGuardedAttentionTuning(score, mustHitLabels, reasons);
                var anchorWeight = Math.Clamp(oldScoreAnchorWeight, 0d, 1d);
                var anchoredScore =
                    anchorWeight * anchorScore
                    + (1d - anchorWeight) * tunedAttentionScore;

                return WithFinalAttentionScore(
                    score,
                    Math.Clamp(anchoredScore, 0d, 1d),
                    reasons);
            })
            .ToArray();
    }

    /// <summary>
    /// 根据当前排名分数和当前分数构建旧的评分锚点。
    /// </summary>
    /// <param name="currentRankScore">基于候选位置计算出的分数。</param>
    /// <param name="currentScore">根据当前上下文计算出的分数。</param>
    /// <returns>返回一个介于0到1之间的值，表示结合了排名和当前得分的最终锚定分数。</returns>
    private double BuildOldScoreAnchor(double currentRankScore, double currentScore)
    {
        var rankWeight = Control("currentRankWeight", 0.55);
        var scoreWeight = Control("currentScoreWeight", 0.45);
        var total = Math.Max(0.0001, Math.Abs(rankWeight) + Math.Abs(scoreWeight));

        return Math.Clamp(
            (rankWeight / total) * currentRankScore
            + (scoreWeight / total) * currentScore,
            0d,
            1d);
    }

    private double ApplyGuardedAttentionTuning(
        ContextAttentionScore score,
        IReadOnlySet<string> mustHitLabels,
        List<string> reasons)
    {
        var tuned = score.FinalAttentionScore;
        if (MatchesAnyLabel(score, mustHitLabels))
        {
            tuned += Control("mustHitBoost", 0d);
            reasons.Add("must_hit_boost");
        }

        if (IsConstraint(score.FeatureVector))
        {
            tuned += Control("constraintBoost", 0d);
            reasons.Add("constraint_boost");
        }

        if (IsShortTermEvidence(score.FeatureVector))
        {
            tuned += Control("shortTermBoost", 0d);
            reasons.Add("short_term_boost");
        }

        if (score.FeatureVector.RelationPathCount > 0)
        {
            tuned += Control("relationEvidenceBoost", 0d);
            reasons.Add("relation_evidence_boost");
        }

        if (score.RecencyScore >= 0.7)
        {
            tuned += Control("recencyBoost", 0d);
            reasons.Add("recency_boost");
        }

        if (!IsLifecycleRisk(score.FeatureVector)) 
            return Math.Clamp(tuned, 0d, 1d);
        
        tuned -= Control("lifecycleRiskPenalty", 0d);
        reasons.Add("lifecycle_risk_penalty");

        return Math.Clamp(tuned, 0d, 1d);
    }

    /// <summary>
    /// 根据给定的注意力分数列表构建候选上下文的注意力排名。
    /// </summary>
    /// <param name="scores">包含每个候选上下文的注意力评分结果的只读列表。</param>
    /// <returns>返回一个字典，键为候选上下文ID，值为其在最终注意力排名中的位置。</returns>
    private Dictionary<string, int> BuildAttentionRanks(IReadOnlyList<ContextAttentionScore> scores)
    {
        var ordered = scores
            .OrderByDescending(static score => score.FinalAttentionScore)
            .ThenBy(static score => score.CurrentRank)
            .ToArray();

        if (!HasRankControls())
        {
            return ordered
                .Select((score, index) => new { score.CandidateId, Rank = index + 1 })
                .ToDictionary(static item => item.CandidateId, static item => item.Rank, StringComparer.OrdinalIgnoreCase);
        }

        return BuildConstrainedAttentionRanks(ordered);
    }

    private Dictionary<string, int> BuildConstrainedAttentionRanks(IReadOnlyList<ContextAttentionScore> orderedScores)
    {
        var count = orderedScores.Count;
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedRanks = new HashSet<int>();

        foreach (var score in orderedScores.Where(ShouldProtectCurrentRank))
        {
            if (score.CurrentRank >= 1
                && score.CurrentRank <= count
                && usedRanks.Add(score.CurrentRank))
            {
                ranks[score.CandidateId] = score.CurrentRank;
            }
        }

        foreach (var score in orderedScores)
        {
            if (ranks.ContainsKey(score.CandidateId))
            {
                continue;
            }

            var minRank = 1;
            var maxRank = count;
            var maxPromotionDelta = (int)Math.Round(Control("maxPromotionDelta", 0d));
            if (maxPromotionDelta > 0)
            {
                minRank = Math.Max(1, score.CurrentRank - maxPromotionDelta);
            }

            var maxDemotionDelta = (int)Math.Round(Control("maxDemotionDelta", 0d));
            if (maxDemotionDelta > 0)
            {
                maxRank = Math.Min(count, score.CurrentRank + maxDemotionDelta);
            }

            ranks[score.CandidateId] = ResolveAvailableRank(minRank, maxRank, count, usedRanks);
        }

        return ranks;
    }

    private bool HasRankControls()
    {
        return Control("maxPromotionDelta", 0d) > 0d
            || Control("maxDemotionDelta", 0d) > 0d
            || Control("protectCurrentTopCount", 0d) > 0d
            || Control("protectMandatory", 0d) > 0d
            || Control("protectAnchors", 0d) > 0d;
    }

    private bool ShouldProtectCurrentRank(ContextAttentionScore score)
    {
        var topCount = (int)Math.Round(Control("protectCurrentTopCount", 0d));
        if (topCount > 0 && score.CurrentRank <= topCount)
        {
            return true;
        }

        if (Control("protectMandatory", 0d) > 0d
            && score.FeatureVector.Metadata.TryGetValue("mandatory", out var mandatory)
            && string.Equals(mandatory, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Control("protectAnchors", 0d) > 0d
               && (score.FeatureVector.MatchedAnchorCount > 0 || IsHardConstraint(score.FeatureVector));
    }

    /// <summary>
    /// 判断给定的特征向量是否表示一个硬约束。硬约束通常指那些在类型或层级上明确标识为约束，并且在元数据中包含“hard”或“required”等关键词的特征。这些约束在注意力评分和排名过程中可能需要特殊保护，以确保它们不会被过度降级或提升，从而保持检索结果的相关性和准确性。
    /// </summary>
    /// <param name="feature"></param>
    /// <returns></returns>
    private static bool IsHardConstraint(ContextAttentionFeatureVector feature)
    {
        if (!feature.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase)
            && !feature.Layer.Contains("constraint", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return feature.Metadata.Any(pair =>
            pair.Value.Contains("hard", StringComparison.OrdinalIgnoreCase)
            || pair.Value.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConstraint(ContextAttentionFeatureVector feature)
    {
        return feature.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase)
            || feature.Layer.Contains("constraint", StringComparison.OrdinalIgnoreCase)
            || feature.Metadata.Any(pair =>
                pair.Key.Contains("constraint", StringComparison.OrdinalIgnoreCase)
                || pair.Value.Contains("constraint", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsShortTermEvidence(ContextAttentionFeatureVector feature)
    {
        return string.Equals(feature.Layer, "Working", StringComparison.OrdinalIgnoreCase)
            || string.Equals(feature.Layer, "WorkingMemory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(feature.Layer, "working_memory", StringComparison.OrdinalIgnoreCase)
            || feature.MatchedAnchorCount > 0;
    }

    private static bool IsLifecycleRisk(ContextAttentionFeatureVector feature)
    {
        return feature.Lifecycle.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || feature.Lifecycle.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || feature.Lifecycle.Contains("superseded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在给定的最小和最大排名范围内，找到一个尚未使用的排名。
    /// </summary>
    /// <param name="minRank">排名的最小值。</param>
    /// <param name="maxRank">排名的最大值。</param>
    /// <param name="count">候选总数。</param>
    /// <param name="usedRanks">已被占用的排名集合。</param>
    /// <returns>返回一个在指定范围内的未使用排名，如果在指定范围内找不到，则返回一个尽可能接近该范围的未使用排名。若所有排名均被占用，则返回<paramref name="count"/>。</returns>
    private static int ResolveAvailableRank(
        int minRank,
        int maxRank,
        int count,
        HashSet<int> usedRanks)
    {
        for (var rank = minRank; rank <= maxRank; rank++)
        {
            if (usedRanks.Add(rank))
            {
                return rank;
            }
        }

        for (var distance = 1; distance <= count; distance++)
        {
            var lower = minRank - distance;
            if (lower >= 1 && usedRanks.Add(lower))
            {
                return lower;
            }

            var upper = maxRank + distance;
            if (upper <= count && usedRanks.Add(upper))
            {
                return upper;
            }
        }

        return count;
    }

    private async Task<IReadOnlyList<ContextLearningRecord>> LoadLearningRecordsAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null
            || string.IsNullOrWhiteSpace(request.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.CollectionId))
        {
            return [];
        }

        return await _learningStore.QueryRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为给定的请求和候选上下文计算注意力分数。
    /// </summary>
    /// <param name="request">上下文检索请求。</param>
    /// <param name="candidate">上下文检索候选对象。</param>
    /// <param name="currentRank">当前候选对象的排名。</param>
    /// <param name="feedbackRecords">反馈记录列表，用于调整评分。</param>
    /// <returns>返回一个包含该候选对象的注意力评分结果的对象。</returns>
    /// <exception cref="ArgumentNullException">当<paramref name="request"/>、<paramref name="candidate"/>或<paramref name="feedbackRecords"/>为null时抛出。</exception>
    private ContextAttentionScore ScoreCandidate(
        ContextRetrievalRequest request,
        ContextRetrievalCandidate candidate,
        int currentRank,
        IReadOnlyList<ContextLearningRecord> feedbackRecords)
    {
        var feedback = ResolveLearningFeedback(candidate, feedbackRecords);
        var feature = BuildFeatureVector(candidate, feedback);
        var reasons = new List<string>();

        var queryMatchScore = ScoreQueryMatch(candidate, feature, reasons);
        var shortTermMatchScore = ScoreShortTermMatch(feature, reasons);
        var relationScore = ScoreRelation(feature, reasons);
        var recencyScore = ScoreRecency(feature, reasons);
        var importanceScore = Math.Clamp(feature.Importance, 0d, 1d);
        var channelScore = Math.Clamp(feature.ChannelHitCount / 3d, 0d, 1d);
        var learningFeedbackScore = Math.Clamp(feature.LearningFeedbackNetScore, -1d, 1d);
        var lifecyclePenalty = ScoreLifecyclePenalty(feature, reasons);
        var scopePenalty = ScoreScopePenalty(feature, reasons);
        var noiseRiskScore = ScoreNoiseRisk(feature, queryMatchScore, reasons);

        if (importanceScore > 0.75)
        {
            reasons.Add("high_importance");
        }

        if (channelScore > 0.66)
        {
            reasons.Add("multi_channel_hit");
        }

        if (feature.PositiveLearningFeedbackCount > 0)
        {
            reasons.Add("positive_learning_feedback");
        }

        if (feature.NegativeLearningFeedbackCount > 0)
        {
            reasons.Add("negative_learning_feedback");
        }

        if (feature.StaleLearningFeedbackCount > 0)
        {
            reasons.Add("stale_learning_feedback");
        }

        var finalScore =
            Weighted("queryMatch", queryMatchScore)
            + Weighted("shortTermMatch", shortTermMatchScore)
            + Weighted("relation", relationScore)
            + Weighted("recency", recencyScore)
            + Weighted("importance", importanceScore)
            + Weighted("channel", channelScore)
            + Weighted("learningFeedback", learningFeedbackScore)
            - lifecyclePenalty
            - scopePenalty
            - noiseRiskScore;

        return new ContextAttentionScore
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            CandidateKind = candidate.Kind,
            CurrentRank = currentRank,
            AttentionRank = currentRank,
            FinalAttentionScore = Math.Clamp(finalScore, 0d, 1d),
            QueryMatchScore = queryMatchScore,
            ShortTermMatchScore = shortTermMatchScore,
            RelationScore = relationScore,
            RecencyScore = recencyScore,
            ImportanceScore = importanceScore,
            ChannelScore = channelScore,
            LearningFeedbackScore = learningFeedbackScore,
            LifecyclePenalty = lifecyclePenalty,
            ScopePenalty = scopePenalty,
            NoiseRiskScore = noiseRiskScore,
            Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProfileId = _profile.ProfileId,
            PolicyVersion = _profile.PolicyVersion,
            FeatureVector = feature
        };
    }

    /// <summary>
    /// 构建上下文检索候选的特征向量。
    /// </summary>
    /// <param name="candidate">上下文检索候选。</param>
    /// <param name="feedback">学习反馈摘要，包含正反馈、负反馈和过时反馈的数量。</param>
    /// <returns>返回一个<see cref="ContextAttentionFeatureVector"/>实例，表示构建好的特征向量。</returns>
    /// <exception cref="ArgumentNullException">当<paramref name="candidate"/>或<paramref name="feedback"/>为null时抛出。</exception>
    private ContextAttentionFeatureVector BuildFeatureVector(
        ContextRetrievalCandidate candidate,
        LearningFeedbackSummary feedback)
    {
        var channelSources = SplitCsv(candidate.Metadata.GetValueOrDefault("channelSources"));
        var relationPaths = SplitRelationPaths(candidate.Metadata.GetValueOrDefault("relationPaths"));
        var matchedTokens = SplitCsv(candidate.Metadata.GetValueOrDefault("matchedTokens"));
        var matchedAnchors = SplitCsv(candidate.Metadata.GetValueOrDefault("matchedAnchors"));
        var layer = candidate.Metadata.GetValueOrDefault("memoryLayer");
        if (string.IsNullOrWhiteSpace(layer))
        {
            layer = candidate.Kind == ContextRetrievalCandidateKind.ContextItem ? "RawContext" : "Unknown";
        }

        var lifecycle = candidate.Metadata.GetValueOrDefault("lifecycleStatus");
        if (string.IsNullOrWhiteSpace(lifecycle))
        {
            lifecycle = candidate.Metadata.GetValueOrDefault("lifecycle");
        }

        if (string.IsNullOrWhiteSpace(lifecycle))
        {
            lifecycle = "Active";
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scoreProxy"] = candidate.Score.ToString("0.###")
        };
        foreach (var pair in candidate.Metadata)
        {
            metadata[pair.Key] = pair.Value;
        }

        return new ContextAttentionFeatureVector
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            CandidateKind = candidate.Kind,
            Type = candidate.Type,
            Layer = layer,
            Lifecycle = lifecycle,
            Importance = ResolveImportance(candidate),
            RecencyAgeHours = ResolveRecencyAgeHours(candidate),
            ChannelHitCount = channelSources.Count,
            ChannelSources = channelSources,
            RelationPathCount = relationPaths.Count,
            RelationPaths = relationPaths,
            Scope = ResolveScope(candidate),
            MatchedTokenCount = matchedTokens.Count,
            MatchedAnchorCount = matchedAnchors.Count,
            PositiveLearningFeedbackCount = feedback.Positive,
            NegativeLearningFeedbackCount = feedback.Negative,
            StaleLearningFeedbackCount = feedback.Stale,
            LearningFeedbackNetScore = feedback.Positive * 0.25 - feedback.Negative * 0.35 - feedback.Stale * 0.20,
            Metadata = metadata
        };
    }

    private static LearningFeedbackSummary ResolveLearningFeedback(
        ContextRetrievalCandidate candidate,
        IReadOnlyList<ContextLearningRecord> records)
    {
        var positive = 0;
        var negative = 0;
        var stale = 0;

        foreach (var record in records)
        {
            if (!MatchesLearningRecord(candidate, record))
            {
                continue;
            }

            switch (record.Signal)
            {
                case ContextFeedbackSignal.Positive:
                    positive++;
                    break;
                case ContextFeedbackSignal.Negative:
                    negative++;
                    break;
                case ContextFeedbackSignal.Stale:
                    stale++;
                    break;
            }
        }

        return new LearningFeedbackSummary(positive, negative, stale);
    }

    private static bool MatchesLearningRecord(ContextRetrievalCandidate candidate, ContextLearningRecord record)
    {
        if (string.Equals(record.SourceId, candidate.SourceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.SourceId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.CandidateId, candidate.SourceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (record.Metadata.TryGetValue("targetItemId", out var targetItemId)
            && (string.Equals(targetItemId, candidate.SourceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetItemId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (record.Metadata.TryGetValue("sourceCandidateId", out var sourceCandidateId)
            && (string.Equals(sourceCandidateId, candidate.SourceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceCandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return record.EvidenceRefs.Any(evidenceRef =>
            string.Equals(evidenceRef, candidate.SourceId, StringComparison.OrdinalIgnoreCase)
            || candidate.SourceRefs.Contains(evidenceRef, StringComparer.OrdinalIgnoreCase));
    }

    private static double ScoreQueryMatch(
        ContextRetrievalCandidate candidate,
        ContextAttentionFeatureVector feature,
        List<string> reasons)
    {
        var tokenScore = Math.Clamp(feature.MatchedTokenCount / 5d, 0d, 1d);
        var scoreProxy = Math.Clamp(candidate.Score / 10d, 0d, 1d) * 0.6;
        var score = Math.Max(tokenScore, scoreProxy);
        if (feature.MatchedTokenCount > 0)
        {
            reasons.Add("query_token_match");
        }

        return score;
    }

    private static double ScoreShortTermMatch(ContextAttentionFeatureVector feature, List<string> reasons)
    {
        var anchorScore = Math.Clamp(feature.MatchedAnchorCount / 3d, 0d, 1d);
        var workingLayerScore = string.Equals(feature.Layer, "Working", StringComparison.OrdinalIgnoreCase) ? 0.4 : 0d;
        var score = Math.Clamp(anchorScore + workingLayerScore, 0d, 1d);
        if (score > 0)
        {
            reasons.Add("short_term_anchor_or_working_layer");
        }

        return score;
    }

    private static double ScoreRelation(ContextAttentionFeatureVector feature, List<string> reasons)
    {
        var score = Math.Clamp(feature.RelationPathCount / 2d, 0d, 1d);
        if (score > 0)
        {
            reasons.Add("relation_path_present");
        }

        return score;
    }

    private static double ScoreRecency(ContextAttentionFeatureVector feature, List<string> reasons)
    {
        switch (feature.RecencyAgeHours)
        {
            case < 0:
                return 0.5;
            case <= 24:
                reasons.Add("recent_update");
                return 1.0;
            case <= 168:
                return 0.7;
            default:
                return feature.RecencyAgeHours <= 720 ? 0.4 : 0.15;
        }
    }

    private double ScoreLifecyclePenalty(ContextAttentionFeatureVector feature, List<string> reasons)
    {
        if (feature.Lifecycle.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("rejected_lifecycle_penalty");
            return Penalty("rejectedLifecycle");
        }

        if (!feature.Lifecycle.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            && !feature.Lifecycle.Contains("superseded", StringComparison.OrdinalIgnoreCase)) return 0d;
        
        reasons.Add("deprecated_lifecycle_penalty");
        return Penalty("deprecatedLifecycle");

    }

    private double ScoreScopePenalty(ContextAttentionFeatureVector feature, List<string> reasons)
    {
        if (feature.Scope.Contains("global", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("global_scope_penalty");
            return Penalty("globalScope");
        }

        return 0d;
    }

    /// <summary>
    /// 评估给定特征向量和查询匹配分数的噪声风险得分。
    /// </summary>
    /// <param name="feature">上下文注意力特征向量。</param>
    /// <param name="queryMatchScore">查询匹配分数。</param>
    /// <param name="reasons">用于存储影响评分原因的字符串列表。</param>
    /// <returns>返回一个介于0到1之间的双精度浮点数，表示噪声风险得分。</returns>
    private double ScoreNoiseRisk(
        ContextAttentionFeatureVector feature,
        double queryMatchScore,
        List<string> reasons)
    {
        var score = 0d;
        if (feature.NegativeLearningFeedbackCount > 0)
        {
            score += Penalty("noiseRisk") * Math.Min(1d, feature.NegativeLearningFeedbackCount);
        }

        if (feature.StaleLearningFeedbackCount > 0)
        {
            score += Penalty("staleFeedback") * Math.Min(1d, feature.StaleLearningFeedbackCount);
        }

        if (queryMatchScore < 0.15 && feature.ChannelHitCount <= 1)
        {
            score += 0.05;
        }

        if (score > 0)
        {
            reasons.Add("noise_or_stale_risk");
        }

        return Math.Clamp(score, 0d, 1d);
    }

    private static ContextAttentionScore WithAttentionRank(ContextAttentionScore score, int attentionRank)
    {
        return new ContextAttentionScore
        {
            CandidateId = score.CandidateId,
            SourceId = score.SourceId,
            CandidateKind = score.CandidateKind,
            CurrentRank = score.CurrentRank,
            AttentionRank = attentionRank,
            FinalAttentionScore = score.FinalAttentionScore,
            QueryMatchScore = score.QueryMatchScore,
            ShortTermMatchScore = score.ShortTermMatchScore,
            RelationScore = score.RelationScore,
            RecencyScore = score.RecencyScore,
            ImportanceScore = score.ImportanceScore,
            ChannelScore = score.ChannelScore,
            LearningFeedbackScore = score.LearningFeedbackScore,
            LifecyclePenalty = score.LifecyclePenalty,
            ScopePenalty = score.ScopePenalty,
            NoiseRiskScore = score.NoiseRiskScore,
            Reasons = score.Reasons,
            ProfileId = score.ProfileId,
            PolicyVersion = score.PolicyVersion,
            FeatureVector = score.FeatureVector
        };
    }

    private static ContextAttentionScore WithFinalAttentionScore(
        ContextAttentionScore score,
        double finalAttentionScore,
        params string[] reasons)
    {
        return WithFinalAttentionScore(score, finalAttentionScore, (IEnumerable<string>)reasons);
    }

    private static ContextAttentionScore WithFinalAttentionScore(
        ContextAttentionScore score,
        double finalAttentionScore,
        IEnumerable<string> reasons)
    {
        return new ContextAttentionScore
        {
            CandidateId = score.CandidateId,
            SourceId = score.SourceId,
            CandidateKind = score.CandidateKind,
            CurrentRank = score.CurrentRank,
            AttentionRank = score.AttentionRank,
            FinalAttentionScore = finalAttentionScore,
            QueryMatchScore = score.QueryMatchScore,
            ShortTermMatchScore = score.ShortTermMatchScore,
            RelationScore = score.RelationScore,
            RecencyScore = score.RecencyScore,
            ImportanceScore = score.ImportanceScore,
            ChannelScore = score.ChannelScore,
            LearningFeedbackScore = score.LearningFeedbackScore,
            LifecyclePenalty = score.LifecyclePenalty,
            ScopePenalty = score.ScopePenalty,
            NoiseRiskScore = score.NoiseRiskScore,
            Reasons = score.Reasons.Concat(reasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProfileId = score.ProfileId,
            PolicyVersion = score.PolicyVersion,
            FeatureVector = score.FeatureVector
        };
    }

    private static bool IsOldScoreAnchoredProfile(string profileId)
    {
        return profileId.StartsWith("old-score-anchored-v1", StringComparison.OrdinalIgnoreCase);
    }

    private double Weighted(string key, double value)
    {
        return _policy.Weighted(key, value);
    }

    private double Penalty(string key)
    {
        return _policy.Penalty(key);
    }

    /// <summary>
    /// 从评分策略中获取指定键的控制值，如果未找到该键或值无效，则返回默认值。该方法用于在评分过程中根据策略动态调整权重、惩罚或其他参数，以实现更灵活和可配置的评分行为。
    /// </summary>
    /// <param name="key"></param>
    /// <param name="defaultValue"></param>
    /// <returns></returns>
    private double Control(string key, double defaultValue = 0d)
    {
        return _policy.Control(key, defaultValue);
    }

    /// <summary>
    /// 解析当前分数代理值，该值用于在旧的评分锚定配置中替代当前分数。它从注意力特征向量的元数据中读取名为"scoreProxy"的值，并尝试将其解析为double类型。如果成功解析并且值大于0，则返回该值；否则返回0。
    /// </summary>
    /// <param name="score"></param>
    /// <returns></returns>
    private static double ResolveCurrentScoreProxy(ContextAttentionScore score)
    {
        return score.FeatureVector.Metadata.TryGetValue("scoreProxy", out var value)
            && double.TryParse(value, out var parsed)
                ? Math.Max(0d, parsed)
                : 0d;
    }

    /// <summary>
    /// 根据请求构建标签集合。
    /// </summary>
    /// <param name="request">上下文检索请求，包含元数据和其他信息。</param>
    /// <param name="isMustHit">布尔值，指示是否为必须匹配的标签。如果为true，则从请求中提取必须匹配的ID；否则，提取不应匹配的标签。</param>
    /// <returns>返回一个字符串哈希集合，包含根据<paramref name="request"/>和<paramref name="isMustHit"/>参数构建的标签。</returns>
    private static HashSet<string> BuildLabelSet(ContextRetrievalRequest request, bool isMustHit)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isMustHit)
        {
            foreach (var id in request.RequiredIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    labels.Add(id.Trim());
                }
            }
        }

        var keys = isMustHit
            ? new[] { "attention.mustHit", "eval.mustHit", "mustHit" }
            : new[] { "attention.mustNotHit", "eval.mustNotHit", "mustNotHit" };

        foreach (var value in request.Metadata.Where(pair => keys.Any(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase))).SelectMany(pair => SplitLabels(pair.Value)))
        {
            labels.Add(value);
        }

        return labels;
    }

    /// <summary>
    /// 将给定的字符串值分割成多个标签。
    /// </summary>
    /// <param name="value">要分割的字符串值，可以包含逗号、分号、竖线、换行符作为分隔符。</param>
    /// <returns>返回一个字符串集合，包含了从输入字符串中提取并去空格后的所有标签。</returns>
    private static IEnumerable<string> SplitLabels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var item in value.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                yield return item.Trim();
            }
        }
    }

    /// <summary>
    /// 检查给定的注意力评分是否匹配任何指定标签。
    /// </summary>
    /// <param name="score">要检查的上下文注意力评分。</param>
    /// <param name="labels">需要匹配的标签集合。</param>
    /// <returns>如果<paramref name="score"/>与<paramref name="labels"/>中的任何一个标签匹配，则返回true；否则返回false。</returns>
    private static bool MatchesAnyLabel(ContextAttentionScore score, IReadOnlySet<string> labels)
    {
        return labels.Count > 0 && labels.Any(label => MatchesLabel(score, label));
    }

    /// <summary>
    /// 检查给定的注意力评分是否与指定标签匹配。
    /// </summary>
    /// <param name="score">要检查的注意力评分。</param>
    /// <param name="label">用于比较的标签。</param>
    /// <returns>如果<paramref name="score"/>中的CandidateId、SourceId或FeatureVector元数据中特定键（如itemId, sourceId, memoryId, contextId）的值与<paramref name="label"/>相等，则返回true；否则返回false。</returns>
    private static bool MatchesLabel(ContextAttentionScore score, string label)
    {
        if (string.Equals(score.CandidateId, label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(score.SourceId, label, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return score.FeatureVector.Metadata.Any(pair =>
            (string.Equals(pair.Key, "itemId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "sourceId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "memoryId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "contextId", StringComparison.OrdinalIgnoreCase))
            && string.Equals(pair.Value, label, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析给定候选的重要性值。
    /// </summary>
    /// <param name="candidate">上下文检索候选。</param>
    /// <returns>返回解析后的标准化重要性值，范围在0到1之间。</returns>
    private static double ResolveImportance(ContextRetrievalCandidate candidate)
    {
        return TryReadDouble(candidate.Metadata, "importance", out var importance) ? Math.Clamp(importance, 0d, 1d) : Math.Clamp(candidate.Score / 10d, 0d, 1d);
    }

    /// <summary>
    /// 解析给定候选的最近更新时间，以小时为单位。
    /// </summary>
    /// <param name="candidate">上下文检索候选。</param>
    /// <returns>返回一个double值，表示候选的最近更新时间（以小时为单位）。如果无法解析时间戳，则返回-1。</returns>
    private static double ResolveRecencyAgeHours(ContextRetrievalCandidate candidate)
    {
        foreach (var key in new[] { "updatedAt", "createdAt", "lastUpdatedAt" })
        {
            if (candidate.Metadata.TryGetValue(key, out var value)
                && DateTimeOffset.TryParse(value, out var timestamp))
            {
                return Math.Max(0d, (DateTimeOffset.UtcNow - timestamp.ToUniversalTime()).TotalHours);
            }
        }

        return -1d;
    }

    /// <summary>
    /// 解析给定候选的范围。
    /// </summary>
    /// <param name="candidate">上下文检索候选。</param>
    /// <returns>返回解析后的范围字符串，如果元数据中没有指定，则默认为"Collection"。</returns>
    private static string ResolveScope(ContextRetrievalCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("scope", out var scope) && !string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        if (candidate.Metadata.TryGetValue("memoryLayer", out var layer)
            && string.Equals(layer, "Global", StringComparison.OrdinalIgnoreCase))
        {
            return "Global";
        }

        return "Collection";
    }

    private static bool TryReadDouble(IReadOnlyDictionary<string, string> metadata, string key, out double value)
    {
        if (metadata.TryGetValue(key, out var raw) && double.TryParse(raw, out value))
        {
            return true;
        }

        value = 0d;
        return false;
    }

    /// <summary>
    /// 将给定的CSV格式字符串拆分为一个只读字符串列表。
    /// </summary>
    /// <param name="value">要拆分的CSV格式字符串。</param>
    /// <returns>返回一个包含拆分后元素的只读字符串列表，如果输入为空或空白，则返回空列表。</returns>
    private static IReadOnlyList<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <summary>
    /// 将给定的关系路径字符串分割成单独的路径列表。
    /// </summary>
    /// <param name="value">要分割的关系路径字符串，路径之间使用竖线（|）分隔。</param>
    /// <returns>返回一个只读列表，包含所有非空且去重后的独立关系路径。</returns>
    private static IReadOnlyList<string> SplitRelationPaths(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private sealed record LearningFeedbackSummary(int Positive, int Negative, int Stale);
}
