using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// Promotion 候选项工厂，将条件评估结果转换为 Review 流程可处理的候选对象。
/// 工厂只创建内存对象，不执行持久化和实际提升。
/// </summary>
public sealed class BasicPromotionCandidateFactory : IPromotionCandidateFactory
{
    public PromotionCandidate CreateCandidate(
        PromotionEvaluationRequest request,
        PromotionEvaluationResult evaluation,
        string sourceKind = "context",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var status = ResolveInitialStatus(evaluation);

        return new PromotionCandidate
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SourceId = request.SourceId,
            SourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "context" : sourceKind,
            Content = request.Content,
            TargetLayer = evaluation.TargetLayer,
            Status = status,
            Decision = evaluation.Decision,
            Category = evaluation.Category,
            Reason = evaluation.Reason,
            Confidence = evaluation.Score,
            MatchedRules = evaluation.MatchedRules.ToArray(),
            SourceRefs = request.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(request.Metadata),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static PromotionCandidateStatus ResolveInitialStatus(PromotionEvaluationResult evaluation)
    {
        if (evaluation.Decision == PromotionEvaluationDecision.DoNotPromote)
        {
            return PromotionCandidateStatus.Rejected;
        }

        if (evaluation.RequiresReview || evaluation.Decision == PromotionEvaluationDecision.NeedsReview)
        {
            return PromotionCandidateStatus.NeedsReview;
        }

        return PromotionCandidateStatus.Candidate;
    }
}
