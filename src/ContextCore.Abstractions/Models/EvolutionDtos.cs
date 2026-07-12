namespace ContextCore.Abstractions.Models;

/// <summary>上下文演化目标类型。</summary>
public enum EvolutionGoalType
{
    /// <summary>将短期记忆提升为工作/稳定记忆。</summary>
    PromoteShortTerm = 0,

    /// <summary>对稳定记忆进行审核（人工或自动）。</summary>
    ReviewStable = 1,

    /// <summary>弃用过期的稳定记忆。</summary>
    DeprecateStale = 2,

    /// <summary>用新条目替换旧条目。</summary>
    Supersede = 3,

    /// <summary>填补约束缺口。</summary>
    FillConstraintGap = 4
}

/// <summary>演化步骤的执行状态。</summary>
public enum EvolutionStepStatus
{
    /// <summary>已提出，等待审批或自动执行。</summary>
    Proposed = 0,

    /// <summary>已批准，可执行。</summary>
    Approved = 1,

    /// <summary>已拒绝，不应执行。</summary>
    Rejected = 2,

    /// <summary>已成功应用。</summary>
    Applied = 3,

    /// <summary>已跳过（前置条件不满足或重复）。</summary>
    Skipped = 4,

    /// <summary>执行失败。</summary>
    Failed = 5
}

/// <summary>上下文演化目标：agent 在一个演化周期中要达成的目标。</summary>
public sealed class EvolutionGoal
{
    /// <summary>目标唯一标识。</summary>
    public string GoalId { get; init; } = string.Empty;

    /// <summary>目标类型。</summary>
    public EvolutionGoalType Type { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    /// <summary>目标条目 ID（如 promotion candidate ID、stable memory ID）；批量目标时为空。</summary>
    public string? TargetItemId { get; init; }

    /// <summary>提出该目标的原因。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>优先级（0=最低，越高越优先）。</summary>
    public int Priority { get; init; }
}

/// <summary>演化步骤：agent 提出的单个演化动作。</summary>
public sealed class EvolutionStep
{
    /// <summary>步骤唯一标识。</summary>
    public string StepId { get; init; } = string.Empty;

    /// <summary>关联的目标 ID。</summary>
    public string GoalId { get; init; } = string.Empty;

    /// <summary>步骤对应的目标类型（冗余于 Goal.Type，便于独立消费）。</summary>
    public EvolutionGoalType Action { get; init; }

    /// <summary>目标条目 ID。</summary>
    public string TargetItemId { get; init; } = string.Empty;

    /// <summary>证据引用链（trace ID、candidate ID 等）。</summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    /// <summary>执行状态。</summary>
    public EvolutionStepStatus Status { get; init; }

    /// <summary>应用时间（Status=Applied 时非空）。</summary>
    public DateTimeOffset? AppliedAt { get; init; }

    /// <summary>状态说明或错误消息。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>演化周期请求。</summary>
public sealed class EvolutionCycleRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    /// <summary>目标类型过滤器；为空时处理所有类型。</summary>
    public IReadOnlyList<EvolutionGoalType> GoalTypes { get; init; } = Array.Empty<EvolutionGoalType>();

    /// <summary>单周期最大步骤数（防止失控）；0 = 不限。</summary>
    public int MaxSteps { get; init; } = 100;

    /// <summary>是否自动应用已批准的步骤；false 时仅提出 Proposed 步骤。</summary>
    public bool AutoApply { get; init; }
}

/// <summary>演化周期结果。</summary>
public sealed class EvolutionCycleResult
{
    /// <summary>周期唯一标识。</summary>
    public string CycleId { get; init; } = string.Empty;

    /// <summary>周期开始时间。</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>周期完成时间。</summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>本周期处理的演化目标。</summary>
    public IReadOnlyList<EvolutionGoal> Goals { get; init; } = Array.Empty<EvolutionGoal>();

    /// <summary>本周期产出的演化步骤。</summary>
    public IReadOnlyList<EvolutionStep> Steps { get; init; } = Array.Empty<EvolutionStep>();

    /// <summary>已提出（Proposed）的步骤数。</summary>
    public int ProposedCount { get; init; }

    /// <summary>已应用（Applied）的步骤数。</summary>
    public int AppliedCount { get; init; }

    /// <summary>已跳过（Skipped）的步骤数。</summary>
    public int SkippedCount { get; init; }

    /// <summary>失败（Failed）的步骤数。</summary>
    public int FailedCount { get; init; }
}
