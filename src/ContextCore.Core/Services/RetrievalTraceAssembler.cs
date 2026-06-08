using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 负责组装检索 trace，不参与 scoring、filtering 或 packing。
/// </summary>
internal sealed class RetrievalTraceAssembler
{
    public ContextRetrievalTrace Assemble(
        string retrievalId,
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalStageTrace> stages,
        IReadOnlyList<ContextRetrievalCandidate> candidates,
        RetrievalPackingResult packingResult,
        IReadOnlyList<ContextAttentionScore> attentionScores,
        AttentionShadowReport attentionShadowReport,
        AttentionProfileExperimentReport attentionProfileComparison,
        Dictionary<string, string> metadata,
        AttentionRerankComparisonReport? attentionRerankComparison = null,
        LifecycleAwareRankerShadowTrace? rankerShadowTrace = null)
    {
        return new ContextRetrievalTrace
        {
            RetrievalId = retrievalId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            RewrittenQueryText = request.RewrittenQueryText,
            Stages = stages,
            Candidates = candidates,
            SelectedItems = packingResult.SelectedDecisions,
            DroppedItems = packingResult.DroppedDecisions,
            AttentionScores = attentionScores,
            AttentionShadowReport = attentionShadowReport,
            AttentionProfileComparison = attentionProfileComparison,
            AttentionRerankComparison = attentionRerankComparison ?? new AttentionRerankComparisonReport(),
            RankerShadowTrace = rankerShadowTrace ?? new LifecycleAwareRankerShadowTrace(),
            Metadata = new Dictionary<string, string>(metadata),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
