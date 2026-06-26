namespace ContextCore.Core.Services;

/// <summary>V8.14 rollback readiness 状态。</summary>
public static class RollbackReadinessStatuses
{
    /// <summary>上游 grant application 不是 Ready — 回滚准备无须评估。</summary>
    public const string RollbackReadinessNotApplicable = nameof(RollbackReadinessNotApplicable);

    /// <summary>application Ready，但至少一个回滚要素缺失。</summary>
    public const string RollbackReadinessIncomplete = nameof(RollbackReadinessIncomplete);

    /// <summary>application Ready 且所有回滚要素就绪。RollbackReady ≠ Activated — 真正的应用 + 撤回都没发生。</summary>
    public const string RollbackReady = nameof(RollbackReady);
}

/// <summary>V8.14 回滚要素常量。每一个都是 artifact-level / plan-level 检查，不是 interactive review。</summary>
public static class RollbackElements
{
    /// <summary>应用前的系统状态快照（什么被改、改之前的值）。</summary>
    public const string PreApplicationSnapshotPresent = nameof(PreApplicationSnapshotPresent);

    /// <summary>逐步撤销脚本/手册已就位。</summary>
    public const string RollbackPlaybookPresent = nameof(RollbackPlaybookPresent);

    /// <summary>回滚路径自己跑过一次 dry-run 并通过。</summary>
    public const string RollbackDryRunPassed = nameof(RollbackDryRunPassed);

    /// <summary>从快照恢复到原状态被测试证明可达。</summary>
    public const string StateRestorationProvenInTest = nameof(StateRestorationProvenInTest);

    /// <summary>谁能触发回滚 + 凭据通道明确（access path 文档化）。</summary>
    public const string RollbackOperatorAccessPathPresent = nameof(RollbackOperatorAccessPathPresent);

    public static readonly IReadOnlyList<string> AllInOrder = new[]
    {
        PreApplicationSnapshotPresent,
        RollbackPlaybookPresent,
        RollbackDryRunPassed,
        StateRestorationProvenInTest,
        RollbackOperatorAccessPathPresent
    };
}

/// <summary>V8.14 回滚要素状态。</summary>
public sealed class RollbackPreparedness
{
    public bool PreApplicationSnapshotPresent { get; init; }
    public bool RollbackPlaybookPresent { get; init; }
    public bool RollbackDryRunPassed { get; init; }
    public bool StateRestorationProvenInTest { get; init; }
    public bool RollbackOperatorAccessPathPresent { get; init; }
}

/// <summary>V8.14 rollback readiness decision。判定"如果应用了能不能撤回"；RollbackActivated 永远 false。</summary>
public sealed class RollbackReadinessDecision
{
    public string Status { get; init; } = RollbackReadinessStatuses.RollbackReadinessNotApplicable;
    public string InputApplicationStatus { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public IReadOnlyList<string> RollbackElementsMet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackElementsMissing { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>关键不变量 — 即便 RollbackReady，回滚 path 也未被执行。</summary>
    public bool RollbackActivated { get; init; }

    /// <summary>从上游 carry — RollbackReady 也不等于应用已发生。</summary>
    public bool ApplicationApplied { get; init; }
}

/// <summary>
/// V8.14 rollback readiness policy。给定 V8.13 GrantApplicationDecision + 回滚要素状态，判定回滚就绪等级。
/// 纯函数。RollbackActivated 与 ApplicationApplied 永远 false。
/// </summary>
public static class FormalRetrievalPromotionApprovalRollbackReadinessPolicy
{
    public static RollbackReadinessDecision Evaluate(
        GrantApplicationDecision applicationDecision,
        RollbackPreparedness preparedness)
    {
        ArgumentNullException.ThrowIfNull(applicationDecision);
        ArgumentNullException.ThrowIfNull(preparedness);

        // 只有上游 application = Ready 才需要评估回滚就绪；其余直接 NotApplicable。
        if (!string.Equals(applicationDecision.Status, GrantApplicationStatuses.GrantApplicationReady, StringComparison.Ordinal))
        {
            return new RollbackReadinessDecision
            {
                Status = RollbackReadinessStatuses.RollbackReadinessNotApplicable,
                InputApplicationStatus = applicationDecision.Status,
                RequestedCapability = applicationDecision.RequestedCapability,
                RequestedScope = applicationDecision.RequestedScope,
                RollbackElementsMet = Array.Empty<string>(),
                RollbackElementsMissing = Array.Empty<string>(),
                Reasoning = $"application status '{applicationDecision.Status}' is not Ready; rollback readiness not evaluated.",
                RollbackActivated = false,
                ApplicationApplied = false
            };
        }

        var met = new List<string>();
        var missing = new List<string>();
        AddByFlag(preparedness.PreApplicationSnapshotPresent, RollbackElements.PreApplicationSnapshotPresent, met, missing);
        AddByFlag(preparedness.RollbackPlaybookPresent, RollbackElements.RollbackPlaybookPresent, met, missing);
        AddByFlag(preparedness.RollbackDryRunPassed, RollbackElements.RollbackDryRunPassed, met, missing);
        AddByFlag(preparedness.StateRestorationProvenInTest, RollbackElements.StateRestorationProvenInTest, met, missing);
        AddByFlag(preparedness.RollbackOperatorAccessPathPresent, RollbackElements.RollbackOperatorAccessPathPresent, met, missing);

        if (missing.Count > 0)
        {
            return new RollbackReadinessDecision
            {
                Status = RollbackReadinessStatuses.RollbackReadinessIncomplete,
                InputApplicationStatus = applicationDecision.Status,
                RequestedCapability = applicationDecision.RequestedCapability,
                RequestedScope = applicationDecision.RequestedScope,
                RollbackElementsMet = met,
                RollbackElementsMissing = missing,
                Reasoning = $"{missing.Count} rollback element(s) missing; rollback readiness incomplete.",
                RollbackActivated = false,
                ApplicationApplied = false
            };
        }

        return new RollbackReadinessDecision
        {
            Status = RollbackReadinessStatuses.RollbackReady,
            InputApplicationStatus = applicationDecision.Status,
            RequestedCapability = applicationDecision.RequestedCapability,
            RequestedScope = applicationDecision.RequestedScope,
            RollbackElementsMet = met,
            RollbackElementsMissing = Array.Empty<string>(),
            Reasoning = "all rollback elements present; if applied we can undo. Note: application still not executed, rollback path still not activated.",
            RollbackActivated = false,
            ApplicationApplied = false
        };
    }

    private static void AddByFlag(bool flag, string name, List<string> met, List<string> missing)
    {
        if (flag)
        {
            met.Add(name);
        }
        else
        {
            missing.Add(name);
        }
    }
}
