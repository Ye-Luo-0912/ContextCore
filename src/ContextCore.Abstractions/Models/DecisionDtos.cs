namespace ContextCore.Abstractions.Models;

/// <summary>
/// 统一的上下文决策记录 DTO。
/// V17.0 引入：在不改变 retrieval/package/planning/PackingPolicy/attention/constraints/vector formal runtime
/// 的前提下，把已有的 selected/dropped/context plan 信息投影为只读 decision trace artifact。
/// 该记录本身不触发任何运行时变更，所有 <see cref="ContextDecisionRisk"/> 标志位恒为 false。
/// </summary>
public sealed class ContextDecisionRecord
{
    /// <summary>决策记录唯一标识，通常复用 buildId / retrievalId。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>决策来源：Package 或 Retrieval。</summary>
    public ContextDecisionSource Source { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    /// <summary>本次决策关联的查询文本（可能为空）。</summary>
    public string? QueryText { get; init; }

    /// <summary>投影自 selected/dropped 的候选决策列表。</summary>
    public IReadOnlyList<ContextDecisionCandidate> Candidates { get; init; } = Array.Empty<ContextDecisionCandidate>();

    /// <summary>本次决策的整体产出摘要（计数、token、section）。</summary>
    public ContextDecisionOutcome Outcome { get; init; } = new();

    /// <summary>非激活契约：所有标志位恒为 false，仅用于审计断言。</summary>
    public ContextDecisionRisk Risk { get; init; } = new();

    /// <summary>策略版本，用于 trace 兼容性识别。</summary>
    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.V17_0;

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>决策记录来源类型。</summary>
public enum ContextDecisionSource
{
    Package = 0,
    Retrieval = 1
}

/// <summary>单个候选的选中/丢弃决策投影。</summary>
public sealed class ContextDecisionCandidate
{
    /// <summary>候选条目 ID（package 侧为 itemId，retrieval 侧为 candidateId 或 sourceId）。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>条目来源类型（ContextItem / MemoryItem）。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>条目业务类型。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>该候选的决策结果：Selected 或 Dropped。</summary>
    public ContextDecisionCandidateOutcome Outcome { get; init; }

    /// <summary>归属的 section 名称（package 场景）。</summary>
    public string SectionName { get; init; } = string.Empty;

    /// <summary>选中或丢弃原因。</summary>
    public string Reason { get; init; } = string.Empty;

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>候选项决策结果。</summary>
public enum ContextDecisionCandidateOutcome
{
    Selected = 0,
    Dropped = 1
}

/// <summary>本次决策的整体产出摘要。</summary>
public sealed class ContextDecisionOutcome
{
    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int EstimatedTokens { get; init; }

    public int TokenBudget { get; init; }

    /// <summary>本次决策涉及的 section 名称集合（package 场景）。</summary>
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 非激活契约风险标志位集合。
/// V17.0 阶段所有标志位恒为 false，仅用于审计断言：decision trace 不得改变任何正式运行时输出。
/// </summary>
public sealed class ContextDecisionRisk
{
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalVectorStoreBinding { get; init; }
    public bool FormalPackageWrite { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool GraphApplyFormalChanged { get; init; }
    public bool LearningPolicyApplied { get; init; }
    public bool ModelTrainingStarted { get; init; }
}

/// <summary>decision trace 策略版本常量。</summary>
public static class ContextDecisionPolicyVersions
{
    public const string V17_0 = "context-decision-foundation/v17.0";
}

/// <summary>decision-audit 审计报告。</summary>
public sealed class ContextDecisionAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TraceCount { get; init; }

    public int PackageDecisionCount { get; init; }

    public int RetrievalDecisionCount { get; init; }

    public int TotalSelectedCount { get; init; }

    public int TotalDroppedCount { get; init; }

    /// <summary>非激活契约校验：所有标志位恒为 false 时为 true。</summary>
    public bool NonActivationContractHolds { get; init; }

    /// <summary>违反非激活契约的标志位名称列表（正常应为空）。</summary>
    public IReadOnlyList<string> ContractViolations { get; init; } = Array.Empty<string>();

    /// <summary>投影保留性校验：selected/dropped 的 ItemId 是否完整保留。</summary>
    public bool ProjectionPreservesIds { get; init; }

    public IReadOnlyList<ContextDecisionAuditSample> Samples { get; init; } = Array.Empty<ContextDecisionAuditSample>();

    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.V17_0;
}

/// <summary>单条 decision trace 的审计摘要。</summary>
public sealed class ContextDecisionAuditSample
{
    public string DecisionId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int EstimatedTokens { get; init; }

    public bool NonActivationContractHolds { get; init; }

    public IReadOnlyList<string> ContractViolations { get; init; } = Array.Empty<string>();
}
