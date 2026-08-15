namespace ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;

/// <summary>
/// 候选流结局分类。回答「候选是未生成、未召回、被 gate 丢弃、排序过低还是预算裁掉」。
/// </summary>
public enum CandidateFlowOutcome : byte
{
    /// <summary>选入最终结果。</summary>
    Selected = 0,

    /// <summary>被 gate（safety / lifecycle / duplicate / supersede / required-tag）丢弃。</summary>
    GateDropped = 1,

    /// <summary>评分低于阈值被丢弃。</summary>
    RankedTooLow = 2,

    /// <summary>预算或 TopK 配额裁掉。</summary>
    BudgetCut = 3,

    /// <summary>任何 Provider 都未产出（未召回）。</summary>
    NotRecalled = 4,

    /// <summary>应产出的 Provider 失败/超时（未生成）。</summary>
    NotGenerated = 5,

    /// <summary>必需证据同时被排除（语义破坏）。</summary>
    ExcludedContradiction = 6,

    /// <summary>种子/持有证据未选中。</summary>
    HeldNotSelected = 7,

    /// <summary>无法分类。</summary>
    Unknown = 8
}

/// <summary>单条候选的结局诊断（只含 ID/通道/结局/分数/token，不含正文）。</summary>
public sealed class CandidateOutcomeDiagnostic
{
    public string CandidateId { get; init; } = string.Empty;

    /// <summary>产出过该候选的通道（Expert 名）。</summary>
    public IReadOnlyList<string> Channels { get; init; } = Array.Empty<string>();

    public CandidateFlowOutcome Outcome { get; init; }

    /// <summary>丢弃原因码（<see cref="CandidateDecisionReasonCode"/> 名）；选中时为 null。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>最终分数（选中时）。</summary>
    public double? FinalScore { get; init; }

    /// <summary>token 成本（ContentTokens）。</summary>
    public int? TokenCost { get; init; }
}

/// <summary>单条期望证据的漏失归因。</summary>
public sealed class EvidenceAttributionDiagnostic
{
    public string EvidenceId { get; init; } = string.Empty;

    /// <summary>角色：required / relevant / forbidden。</summary>
    public string Role { get; init; } = string.Empty;

    public CandidateFlowOutcome Outcome { get; init; }

    /// <summary>产出过该证据的通道。</summary>
    public IReadOnlyList<string> Channels { get; init; } = Array.Empty<string>();

    /// <summary>丢弃原因码；选中/未召回时为 null。</summary>
    public string? ReasonCode { get; init; }
}

/// <summary>通道命中摘要：哪个通道产生了唯一有效命中。</summary>
public sealed class ChannelHitSummary
{
    /// <summary>通道名（Expert 名）。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>该通道产出的候选数。</summary>
    public int Produced { get; init; }

    /// <summary>仅该通道产出的候选数（唯一命中）。</summary>
    public int Unique { get; init; }

    /// <summary>该通道产出且最终选中的候选数。</summary>
    public int Selected { get; init; }
}

/// <summary>跨通道重复候选与分数范围。</summary>
public sealed class DuplicateCandidateDiagnostic
{
    public string CandidateId { get; init; } = string.Empty;

    /// <summary>产出该候选的通道列表。</summary>
    public IReadOnlyList<string> Channels { get; init; } = Array.Empty<string>();

    /// <summary>跨通道分数最小值（Utility.DeterministicScore）。</summary>
    public double ScoreMin { get; init; }

    /// <summary>跨通道分数最大值。</summary>
    public double ScoreMax { get; init; }
}

/// <summary>held / excluded / required / forbidden 语义破坏。</summary>
public sealed class SemanticsViolation
{
    /// <summary>violation 类型：excluded-in-candidates / required-excluded / forbidden-selected / held-dropped。</summary>
    public string Kind { get; init; } = string.Empty;

    public string EvidenceId { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}

/// <summary>selected hydration 成本（花在哪、多少 token）。</summary>
public sealed class SelectedHydrationCost
{
    public int SelectedCount { get; init; }

    /// <summary>选中候选正文 token 成本合计。</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>最终序列化总 token（含分隔符/头部）；无最终 artifact 时为 null。</summary>
    public int? FinalTotalTokens { get; init; }

    public bool WithinBudget { get; init; }

    public int? BudgetLimit { get; init; }
}

/// <summary>
/// 一次请求的候选流诊断报告。
/// 只含 ID / 通道 / 结局 / 分数 / token / 计数，绝不携带正文或敏感数据。
/// </summary>
public sealed class CandidatesFlowDiagnostics
{
    public string RequestId { get; init; } = string.Empty;

    public string? QueryText { get; init; }

    public string Purpose { get; init; } = string.Empty;

    public int TokenBudget { get; init; }

    public int TopK { get; init; }

    /// <summary>是否有 Provider degraded（失败/超时）。</summary>
    public bool IsDegraded { get; init; }

    public int CandidateCount { get; init; }

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    /// <summary>通道命中摘要。</summary>
    public IReadOnlyList<ChannelHitSummary> Channels { get; init; } = Array.Empty<ChannelHitSummary>();

    /// <summary>候选结局（合并 provider 快照与决策）。</summary>
    public IReadOnlyList<CandidateOutcomeDiagnostic> Candidates { get; init; } = Array.Empty<CandidateOutcomeDiagnostic>();

    /// <summary>期望证据漏失归因（数据集样本提供期望时）。</summary>
    public IReadOnlyList<EvidenceAttributionDiagnostic> RequiredEvidence { get; init; } = Array.Empty<EvidenceAttributionDiagnostic>();

    /// <summary>跨通道重复候选。</summary>
    public IReadOnlyList<DuplicateCandidateDiagnostic> Duplicates { get; init; } = Array.Empty<DuplicateCandidateDiagnostic>();

    /// <summary>held / excluded / required / forbidden 语义破坏。</summary>
    public IReadOnlyList<SemanticsViolation> Violations { get; init; } = Array.Empty<SemanticsViolation>();

    public SelectedHydrationCost Hydration { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}
