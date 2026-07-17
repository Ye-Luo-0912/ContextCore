using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V14_0;

public enum RuntimeCandidateTraceSource : byte { Unknown = 0, ShadowEval = 1, GraphShadow = 2, PackageTrace = 3, RetrievalTrace = 4 }
public enum RuntimeCandidateRetrievalChannel : byte { Unknown = 0, Vector = 1, Memory = 2, Graph = 3, Keyword = 4, Anchor = 5, Constraint = 6 }

/// <summary>
/// P0-6.3: 单个候选在 section 装配后的精确归属结果。
/// 替代 bool IncludedInPackage，使下游诊断能区分"完整保留/部分截断/未保留/未参与评分"四种状态。
/// </summary>
public enum RuntimeCandidateOutcome : byte
{
    /// <summary>未知/未设置（仅用于兼容旧路径，正常流程不应出现）。</summary>
    Unknown = 0,
    /// <summary>候选被完整保留进 section 输出（IncludedTokens == OriginalTokens）。</summary>
    Accepted = 1,
    /// <summary>候选因 token 预算截断仅部分保留（0 &lt; IncludedTokens &lt; OriginalTokens）。</summary>
    PartiallyAccepted = 2,
    /// <summary>候选输入 section 但因预算截断未保留（IncludedTokens == 0，selectedByScoring=true）。</summary>
    Rejected = 3,
    /// <summary>候选未参与 section 评分/装配（如被 recent filter 排除、约束已废弃、审计模式外历史记忆）。</summary>
    Dropped = 4
}

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

    /// <summary>P0-6.3: 候选的精确归属结果（Accepted/PartiallyAccepted/Rejected/Dropped）。</summary>
    public RuntimeCandidateOutcome Outcome { get; init; }
    /// <summary>P0-6.3: 候选原始估算 token 数（截断前）。Accepted 路径下与 TokenCost 一致。</summary>
    public int OriginalTokens { get; init; }
    /// <summary>P0-6.3: 候选实际保留进 section 输出的 token 数（截断后）。</summary>
    public int IncludedTokens { get; init; }
    /// <summary>P0-6.3: 截断比率 = IncludedTokens / max(OriginalTokens, 1)。Accepted=1.0，Rejected/Dropped=0.0。</summary>
    public double TruncationRatio { get; init; }

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
        section = Section, recordedAt = RecordedAt.ToString("O"),
        outcome = Outcome.ToString(),
        originalTokens = OriginalTokens,
        includedTokens = IncludedTokens,
        truncationRatio = Math.Round(TruncationRatio, 4)
    });
}

public sealed class RuntimeCandidateTraceMissingFieldReport
{
    public string RowIdentifier { get; init; } = "";
    public IReadOnlyList<string> MissingCriticalFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingOptionalFields { get; init; } = Array.Empty<string>();
    public bool HasCriticalMissing => MissingCriticalFields.Count > 0;
}
