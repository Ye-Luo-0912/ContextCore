using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// Promotion 评测运行器，基于样本集计算正确提升率、错误提升率、漏提升率、长期层污染率和 needs_review 比例。
/// 运行器只调用评估器，不写入记忆层或向量层。
/// </summary>
public sealed class PromotionEvalRunner
{
    private readonly IPromotionPolicyEvaluator _evaluator;

    public PromotionEvalRunner(IPromotionPolicyEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public PromotionEvalReport Run(
        IReadOnlyList<PromotionEvalSample> samples,
        string workspaceId = "eval",
        string collectionId = "promotion")
    {
        ArgumentNullException.ThrowIfNull(samples);

        var results = samples
            .Select(sample => EvaluateSample(sample, workspaceId, collectionId))
            .ToArray();
        var expectedPromotions = results.Count(item => IsPromotion(item.ExpectedDecision));
        var expectedNoPromotions = results.Length - expectedPromotions;
        var correctPromotions = results.Count(item => item.IsCorrectPromotion);
        var erroneousPromotions = results.Count(item => item.IsErroneousPromotion);
        var missedPromotions = results.Count(item => item.IsMissedPromotion);
        var stablePollutions = results.Count(item => item.IsStableLayerPollution);
        var needsReview = results.Count(item => item.IsNeedsReview);

        return new PromotionEvalReport
        {
            TotalSamples = results.Length,
            ExpectedPromotionSamples = expectedPromotions,
            ExpectedNoPromotionSamples = expectedNoPromotions,
            CorrectPromotionCount = correctPromotions,
            ErroneousPromotionCount = erroneousPromotions,
            MissedPromotionCount = missedPromotions,
            StableLayerPollutionCount = stablePollutions,
            NeedsReviewCount = needsReview,
            CorrectPromotionRate = Ratio(correctPromotions, expectedPromotions),
            ErroneousPromotionRate = Ratio(erroneousPromotions, expectedNoPromotions),
            MissedPromotionRate = Ratio(missedPromotions, expectedPromotions),
            StableLayerPollutionRate = Ratio(stablePollutions, results.Length),
            NeedsReviewRate = Ratio(needsReview, results.Length),
            Results = results
        };
    }

    private PromotionEvalSampleResult EvaluateSample(
        PromotionEvalSample sample,
        string workspaceId,
        string collectionId)
    {
        var evaluation = _evaluator.Evaluate(new PromotionEvaluationRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceId = sample.Id,
            Type = "eval-sample",
            Content = sample.Content,
            Tags = sample.Tags.ToArray(),
            Confidence = 0.8,
            Metadata = new Dictionary<string, string>(sample.Metadata)
        });

        var expectedPromotion = IsPromotion(sample.ExpectedDecision);
        var actualPromotion = evaluation.ShouldPromote;
        var correctPromotion = expectedPromotion
            && actualPromotion
            && evaluation.Decision == sample.ExpectedDecision
            && evaluation.TargetLayer == sample.ExpectedTargetLayer;
        var erroneousPromotion = !expectedPromotion && actualPromotion;
        var missedPromotion = expectedPromotion && !actualPromotion;
        var stablePollution = evaluation.TargetLayer == ContextMemoryLayer.Stable
            && sample.ExpectedTargetLayer != ContextMemoryLayer.Stable;

        return new PromotionEvalSampleResult
        {
            SampleId = sample.Id,
            ExpectedDecision = sample.ExpectedDecision,
            ActualDecision = evaluation.Decision,
            ExpectedTargetLayer = sample.ExpectedTargetLayer,
            ActualTargetLayer = evaluation.TargetLayer,
            IsCorrectPromotion = correctPromotion,
            IsErroneousPromotion = erroneousPromotion,
            IsMissedPromotion = missedPromotion,
            IsStableLayerPollution = stablePollution,
            IsNeedsReview = evaluation.Decision == PromotionEvaluationDecision.NeedsReview,
            Reason = evaluation.Reason
        };
    }

    private static bool IsPromotion(PromotionEvaluationDecision decision)
    {
        return decision is PromotionEvaluationDecision.PromoteToWorkingMemory
            or PromotionEvaluationDecision.PromoteToStableMemory;
    }

    private static double Ratio(int numerator, int denominator)
    {
        return denominator <= 0 ? 0 : (double)numerator / denominator;
    }
}
