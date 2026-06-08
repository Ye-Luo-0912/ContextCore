using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>
/// 单次检索过程中传给通道执行器的调用上下文。
/// 只承载本次请求的运行时状态，不承载存储依赖。
/// </summary>
internal sealed record RetrievalChannelContext
{
    public required ContextRetrievalRequest Request { get; init; }

    public required RetrievalPlan Plan { get; init; }

    public required Dictionary<string, string> Metadata { get; init; }

    public IReadOnlyList<ContextRetrievalCandidate> CurrentCandidates { get; init; } = Array.Empty<ContextRetrievalCandidate>();

    public string? QueryText { get; init; }

    public int CandidateTake { get; init; }

    public static RetrievalChannelContext Create(
        ContextRetrievalRequest request,
        RetrievalPlan plan,
        Dictionary<string, string> metadata,
        IReadOnlyList<ContextRetrievalCandidate>? currentCandidates = null)
    {
        return new RetrievalChannelContext
        {
            Request = request,
            Plan = plan,
            Metadata = metadata,
            CurrentCandidates = currentCandidates ?? Array.Empty<ContextRetrievalCandidate>(),
            QueryText = string.IsNullOrWhiteSpace(request.RewrittenQueryText)
                ? request.QueryText
                : request.RewrittenQueryText,
            CandidateTake = request.CandidateTake > 0 ? request.CandidateTake : Math.Max(20, request.TopK * 4)
        };
    }
}
