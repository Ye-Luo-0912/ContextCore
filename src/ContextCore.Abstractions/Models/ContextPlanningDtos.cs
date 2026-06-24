namespace ContextCore.Abstractions.Models;

/// <summary>供规划层读取的只读上下文快照，不参与 retrieval 或 package 选择。</summary>
public sealed class ContextPlanningSnapshot
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public IReadOnlyList<ShortTermWorkingItem> ActiveTasks { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> RecentDecisions { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> OpenQuestions { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ShortTermWorkingItem> KnownIssues { get; init; } = Array.Empty<ShortTermWorkingItem>();

    public IReadOnlyList<ContextConstraint> StableConstraints { get; init; } = Array.Empty<ContextConstraint>();

    public IReadOnlyList<ContextMemoryItem> StablePreferences { get; init; } = Array.Empty<ContextMemoryItem>();

    public IReadOnlyList<ContextMemoryItem> DecisionRecords { get; init; } = Array.Empty<ContextMemoryItem>();

    public ContextLearningSummary LearningSignalsSummary { get; init; } = new();

    public string PolicyVersion { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>规划层 retrieval plan preview 的请求体；只用于生成 proposal，不执行 retrieval。</summary>
public sealed class ContextPlanningProposalRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string CurrentInput { get; init; } = string.Empty;

    public string? Mode { get; init; }
}

/// <summary>RetrievalPlan proposal 的安全边界；只约束 preview / shadow，不改变正式 retrieval。</summary>
public sealed class RetrievalPlanSafetyProfile
{
    public int MaxFinalTopK { get; init; } = 10;

    public int MaxKeywordTopK { get; init; } = 24;

    public int MaxMemoryTopK { get; init; } = 24;

    public int MaxRelationTopK { get; init; } = 8;

    public int MaxVectorTopK { get; init; }

    public bool AllowVector { get; init; }

    public bool AllowDeprecatedInNormalMode { get; init; }

    public bool AllowSupersededInNormalMode { get; init; }

    public bool RequireLifecycleFilter { get; init; } = true;

    public static RetrievalPlanSafetyProfile CreateDefault() => new();
}

/// <summary>Retrieval planning 执行开关。默认关闭，只允许显式 opt-in 的 guarded proposal path。</summary>
public sealed class RetrievalPlanningOptions
{
    public const string OffMode = "Off";

    public const string ShadowMode = "Shadow";

    public const string ApplyGuardedMode = "ApplyGuarded";

    public const string IntentScopedApplyMode = "IntentScoped";

    public string Mode { get; init; } = OffMode;

    public string ApplyMode { get; init; } = IntentScopedApplyMode;

    public IReadOnlyList<string> OptInIntents { get; init; } = Array.Empty<string>();

    public bool FallbackToLegacyOnViolation { get; init; } = true;

    public bool EmitComparisonTrace { get; init; } = true;

    public string EffectiveMode => string.IsNullOrWhiteSpace(Mode)
        ? OffMode
        : Mode.Trim();

    public string EffectiveApplyMode => string.IsNullOrWhiteSpace(ApplyMode)
        ? IntentScopedApplyMode
        : ApplyMode.Trim();

    public bool IsShadow => string.Equals(EffectiveMode, ShadowMode, StringComparison.OrdinalIgnoreCase);

    public bool IsApplyGuarded => string.Equals(EffectiveMode, ApplyGuardedMode, StringComparison.OrdinalIgnoreCase);

    public bool IsOff => string.Equals(EffectiveMode, OffMode, StringComparison.OrdinalIgnoreCase);

    public bool IsIntentScoped => string.Equals(
        EffectiveApplyMode,
        IntentScopedApplyMode,
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>规则型 retrieval plan proposal，用于只读预览可能使用的召回通道和 TopK 参数。</summary>
public sealed class RetrievalPlanProposal
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Intent { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool UseExact { get; init; }

    public bool UseKeyword { get; init; }

    public bool UseShortTermMemory { get; init; }

    public bool UseWorkingMemory { get; init; }

    public bool UseStableMemory { get; init; }

    public bool UseRelations { get; init; }

    public bool UseVector { get; init; }

    public bool AuditMode { get; init; }

    public bool ConflictMode { get; init; }

    public int KeywordTopK { get; init; }

    public int MemoryTopK { get; init; }

    public int RelationTopK { get; init; }

    public int VectorTopK { get; init; }

    public int FinalTopK { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
