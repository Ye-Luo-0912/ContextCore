using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V14_0;

public enum RuntimeCandidateTraceSource : byte { Unknown = 0, ShadowEval = 1, GraphShadow = 2, PackageTrace = 3, RetrievalTrace = 4 }
public enum RuntimeCandidateRetrievalChannel : byte { Unknown = 0, Vector = 1, Memory = 2, Graph = 3, Keyword = 4, Anchor = 5, Constraint = 6 }

/// <summary>
/// OPT-1: 候选来源类型枚举（替代原 magic byte 1/2/3/4/5/6/7）。
/// 取值与历史 byte 输出兼容（: byte），JSON 序列化仍输出数值。
/// 未匹配的 kind 不再静默落入 Raw(1)，而是显式 Unknown(0)，便于下游检测 schema 演进缺口。
/// </summary>
public enum RuntimeCandidateSourceType : byte
{
    /// <summary>未知/未匹配 kind。新增 section/kind 时如果未更新映射，将显式落此值而非静默默认 Raw。</summary>
    Unknown = 0,
    /// <summary>raw / legacy / 默认 fallback。</summary>
    Raw = 1,
    /// <summary>working_memory / stable_memory / historical_context。</summary>
    Memory = 2,
    /// <summary>hard_constraint / soft_constraint / merged_constraint。</summary>
    Constraint = 3,
    /// <summary>global_context。</summary>
    GlobalContext = 4,
    /// <summary>recent_context。</summary>
    RecentContext = 5,
    /// <summary>current_task。</summary>
    CurrentTask = 6,
    /// <summary>related_context。</summary>
    RelatedContext = 7
}

/// <summary>
/// OPT-1: 候选权威等级枚举（替代原 magic byte 1/2/3/4/5）。
/// 取值与历史 byte 输出兼容（: byte）。
/// </summary>
public enum CandidateAuthorityLevel : byte
{
    /// <summary>未知/未匹配 kind。</summary>
    Unknown = 0,
    /// <summary>硬性必含（constraint / stable_memory / global_context）。</summary>
    HardRequirement = 1,
    /// <summary>用户附着（raw / legacy / recent_context）。</summary>
    UserAttached = 2,
    /// <summary>参考背景（historical_context）。</summary>
    Reference = 3,
    /// <summary>推断来源（related_context）。</summary>
    Inferred = 4,
    /// <summary>权威来源（current_task / working_memory）。</summary>
    Authoritative = 5
}

/// <summary>
/// OPT-1: 候选策略类型枚举（替代原 magic byte 1/2/3/4/5）。
/// 取值与历史 byte 输出兼容（: byte）。
/// </summary>
public enum CandidateStrategyType : byte
{
    /// <summary>未知/未匹配 kind。</summary>
    Unknown = 0,
    /// <summary>近期策略（working_memory / recent_context / raw / legacy / 默认 fallback）。</summary>
    Recent = 1,
    /// <summary>稳定策略（stable_memory / global_context）。</summary>
    Stable = 2,
    /// <summary>约束策略（hard_constraint / soft_constraint / merged_constraint）。</summary>
    Constraint = 3,
    /// <summary>当前策略（current_task）。</summary>
    Current = 4,
    /// <summary>关联策略（related_context）。</summary>
    Related = 5
}

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
    /// <summary>OPT-1: 候选来源类型（原 byte SourceType，现 <see cref="RuntimeCandidateSourceType"/>，: byte 保证 JSON 输出兼容）。</summary>
    public RuntimeCandidateSourceType SourceType { get; init; }
    /// <summary>OPT-1: 候选权威等级（原 byte Authority，现 <see cref="CandidateAuthorityLevel"/>）。</summary>
    public CandidateAuthorityLevel Authority { get; init; }
    /// <summary>OPT-1: 候选策略类型（原 byte StrategyType，现 <see cref="CandidateStrategyType"/>）。</summary>
    public CandidateStrategyType StrategyType { get; init; }
    /// <summary>OPT-1: 检索通道（原 byte RetrievalChannel，现 <see cref="RuntimeCandidateRetrievalChannel"/>）。</summary>
    public RuntimeCandidateRetrievalChannel RetrievalChannel { get; init; }
    public RuntimeCandidateTraceSource TraceSource { get; init; }
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
        sourceId = SourceId, sourceType = (byte)SourceType, authority = (byte)Authority,
        strategyType = (byte)StrategyType, retrievalChannel = (byte)RetrievalChannel,
        traceSource = (byte)TraceSource,
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
