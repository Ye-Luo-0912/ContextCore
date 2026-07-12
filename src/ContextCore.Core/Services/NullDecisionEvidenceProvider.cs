using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 默认的 <see cref="IDecisionEvidenceProvider"/> 空实现。
/// 返回空证据列表，所有候选标记为 missing，<see cref="DecisionEvidenceResult.IsComplete"/>=false。
/// 用于未接入真实证据源时的默认占位，审计应据此标记 evidence-incomplete。
/// </summary>
public sealed class NullDecisionEvidenceProvider : IDecisionEvidenceProvider
{
    public Task<DecisionEvidenceResult> ResolveEvidenceAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var missingItemIds = record.Candidates
            .Select(c => c.ItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new DecisionEvidenceResult
        {
            DecisionId = record.DecisionId,
            Evidence = Array.Empty<DecisionEvidence>(),
            IsComplete = missingItemIds.Count == 0,
            MissingItemIds = missingItemIds,
            ResolvedAt = DateTimeOffset.UtcNow
        };

        return Task.FromResult(result);
    }
}
