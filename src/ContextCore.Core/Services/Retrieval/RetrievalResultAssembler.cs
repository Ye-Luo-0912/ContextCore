using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 负责组装最终检索结果对象，不参与 scoring、filtering 或 packing。
/// </summary>
internal sealed class RetrievalResultAssembler
{
    public ContextRetrievalResult Assemble(
        string operationId,
        ContextRetrievalRequest request,
        RetrievalPackingResult packingResult,
        ContextRetrievalTrace trace,
        Dictionary<string, string> metadata)
    {
        return new ContextRetrievalResult
        {
            OperationId = operationId,
            Succeeded = true,
            SelectedItems = packingResult.SelectedCandidates,
            DroppedItems = packingResult.DroppedDecisions,
            EstimatedTokens = packingResult.SelectedCandidates.Sum(item => item.EstimatedTokens),
            Usage = new ContextOperationUsage
            {
                InputTokens = EstimateTokens(request.QueryText),
                OutputTokens = packingResult.SelectedCandidates.Sum(item => item.EstimatedTokens),
                ModelCalls = metadata.TryGetValue("queryEmbeddingModelCalls", out var calls)
                    && int.TryParse(calls, out var parsedCalls)
                        ? parsedCalls
                        : 0
            },
            Trace = trace,
            Metadata = new Dictionary<string, string>(metadata),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static int EstimateTokens(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : Math.Max(1, text.Length / 4);
    }
}
