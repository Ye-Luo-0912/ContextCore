using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V14_0;

public enum RuntimeCandidateTraceSource : byte { Unknown = 0, ShadowEval = 1, GraphShadow = 2, PackageTrace = 3, RetrievalTrace = 4 }
public enum RuntimeCandidateRetrievalChannel : byte { Unknown = 0, Vector = 1, Memory = 2, Graph = 3, Keyword = 4, Anchor = 5, Constraint = 6 }

public sealed class RuntimeCandidateTraceRow
{
    public string OperationId { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public byte SourceType { get; init; }
    public byte Authority { get; init; }
    public byte StrategyType { get; init; }
    public byte RetrievalChannel { get; init; }
    public byte TraceSource { get; init; }
    public double DeterministicScore { get; init; }
    public double StrategyScore { get; init; }
    public double FinalScore { get; init; }
    public bool SelectedByScoring { get; init; }
    public bool IncludedInPackage { get; init; }
    public string DroppedReason { get; init; } = "";
    public double TokenCost { get; init; }
    public string Section { get; init; } = "";
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ToJsonLine() => JsonSerializer.Serialize(new
    {
        operationId = OperationId, requestId = RequestId, candidateId = CandidateId,
        sourceId = SourceId, sourceType = SourceType, authority = Authority,
        strategyType = StrategyType, retrievalChannel = RetrievalChannel,
        traceSource = TraceSource,
        deterministicScore = Math.Round(DeterministicScore, 4),
        strategyScore = Math.Round(StrategyScore, 4),
        finalScore = Math.Round(FinalScore, 4),
        selectedByScoring = SelectedByScoring, includedInPackage = IncludedInPackage,
        droppedReason = DroppedReason, tokenCost = Math.Round(TokenCost, 4),
        section = Section, recordedAt = RecordedAt.ToString("O")
    });
}

public sealed class RuntimeCandidateTraceMissingFieldReport
{
    public string RowIdentifier { get; init; } = "";
    public IReadOnlyList<string> MissingCriticalFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingOptionalFields { get; init; } = Array.Empty<string>();
    public bool HasCriticalMissing => MissingCriticalFields.Count > 0;
}
