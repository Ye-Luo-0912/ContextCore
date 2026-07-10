namespace ContextCore.Core.Services;

/// <summary>V8.13 grant application policy 状态。</summary>
public static class GrantApplicationStatuses
{
    /// <summary>输入决策非 Grant — 应用路径无须评估（这是为了显式区分"Deny/Indeterminate 跳过应用"和"Grant 被阻塞"）。</summary>
    public const string GrantApplicationNotApplicable = nameof(GrantApplicationNotApplicable);

    /// <summary>Grant 决策但至少一个前置条件缺失，应用被阻塞。</summary>
    public const string GrantApplicationBlocked = nameof(GrantApplicationBlocked);

    /// <summary>Grant 决策且所有前置条件满足，应用就绪。Ready ≠ Applied — 应用是另一条独立路径。</summary>
    public const string GrantApplicationReady = nameof(GrantApplicationReady);
}

/// <summary>V8.13 grant application precondition 命名常量。每个 precondition 都是 artifact-level 检查，不是 interactive review。</summary>
public static class GrantApplicationPreconditions
{
    /// <summary>approval evidence seal artifact 已就位（写出 + 校验通过）。</summary>
    public const string ApprovalSealArtifactPresent = nameof(ApprovalSealArtifactPresent);

    /// <summary>dry-run negative matrix gate 已通过（demonstrates the change path has been exercised without harm）。</summary>
    public const string DryRunCleanArtifactPresent = nameof(DryRunCleanArtifactPresent);

    /// <summary>audit log artifact 已写入（变更被留痕）。</summary>
    public const string AuditLogArtifactPresent = nameof(AuditLogArtifactPresent);

    /// <summary>runtime change readiness gate 在应用时仍通过。</summary>
    public const string RuntimeChangeReadinessGatePassed = nameof(RuntimeChangeReadinessGatePassed);

    /// <summary>trust chain validation 在应用时被重新校验且仍 Validated。</summary>
    public const string TrustChainReverificationGatePassed = nameof(TrustChainReverificationGatePassed);

    public static readonly IReadOnlyList<string> AllInOrder = new[]
    {
        ApprovalSealArtifactPresent,
        DryRunCleanArtifactPresent,
        AuditLogArtifactPresent,
        RuntimeChangeReadinessGatePassed,
        TrustChainReverificationGatePassed
    };
}

/// <summary>V8.13 应用前置条件 — 每个字段独立表达 artifact 是否就位。</summary>
public sealed class GrantApplicationPreconditionsState
{
    public bool ApprovalSealArtifactPresent { get; init; }
    public bool DryRunCleanArtifactPresent { get; init; }
    public bool AuditLogArtifactPresent { get; init; }
    public bool RuntimeChangeReadinessGatePassed { get; init; }
    public bool TrustChainReverificationGatePassed { get; init; }
}

/// <summary>V8.13 grant application decision。机器判定能否进入"应用"路径；ApplicationApplied 永远 false。</summary>
public sealed class GrantApplicationDecision
{
    public string Status { get; init; } = GrantApplicationStatuses.GrantApplicationNotApplicable;
    public string InputPolicyEffect { get; init; } = string.Empty;
    public string InputPolicyRule { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public IReadOnlyList<string> PreconditionsMet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreconditionsMissing { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>核心契约 — Ready 不等于 Applied，应用必须走另一条显式 write-out 路径。</summary>
    public bool ApplicationApplied { get; init; }
}

/// <summary>
/// V8.13 grant application policy。给定 V8.12 PolicyAuthorityDecision + 前置条件状态，
/// 判定 capability 是否可应用。纯函数；ApplicationApplied 永远 false。
/// </summary>
public static class FormalRetrievalPromotionApprovalGrantApplicationPolicy
{
    public static GrantApplicationDecision Evaluate(
        PolicyAuthorityDecision policyDecision,
        GrantApplicationPreconditionsState preconditions)
    {
        ArgumentNullException.ThrowIfNull(policyDecision);
        ArgumentNullException.ThrowIfNull(preconditions);

        // 非 Grant 决策 — 应用路径无须评估。
        if (!string.Equals(policyDecision.Effect, PolicyAuthorityEffects.Grant, StringComparison.Ordinal))
        {
            return new GrantApplicationDecision
            {
                Status = GrantApplicationStatuses.GrantApplicationNotApplicable,
                InputPolicyEffect = policyDecision.Effect,
                InputPolicyRule = policyDecision.RuleName,
                RequestedCapability = policyDecision.RequestedCapability,
                RequestedScope = policyDecision.RequestedScope,
                PreconditionsMet = Array.Empty<string>(),
                PreconditionsMissing = Array.Empty<string>(),
                Reasoning = $"policy effect '{policyDecision.Effect}' is not Grant; application path not entered.",
                ApplicationApplied = false
            };
        }

        var met = new List<string>();
        var missing = new List<string>();
        AddByFlag(preconditions.ApprovalSealArtifactPresent, GrantApplicationPreconditions.ApprovalSealArtifactPresent, met, missing);
        AddByFlag(preconditions.DryRunCleanArtifactPresent, GrantApplicationPreconditions.DryRunCleanArtifactPresent, met, missing);
        AddByFlag(preconditions.AuditLogArtifactPresent, GrantApplicationPreconditions.AuditLogArtifactPresent, met, missing);
        AddByFlag(preconditions.RuntimeChangeReadinessGatePassed, GrantApplicationPreconditions.RuntimeChangeReadinessGatePassed, met, missing);
        AddByFlag(preconditions.TrustChainReverificationGatePassed, GrantApplicationPreconditions.TrustChainReverificationGatePassed, met, missing);

        if (missing.Count > 0)
        {
            return new GrantApplicationDecision
            {
                Status = GrantApplicationStatuses.GrantApplicationBlocked,
                InputPolicyEffect = policyDecision.Effect,
                InputPolicyRule = policyDecision.RuleName,
                RequestedCapability = policyDecision.RequestedCapability,
                RequestedScope = policyDecision.RequestedScope,
                PreconditionsMet = met,
                PreconditionsMissing = missing,
                Reasoning = $"{missing.Count} precondition(s) missing; application blocked.",
                ApplicationApplied = false
            };
        }

        return new GrantApplicationDecision
        {
            Status = GrantApplicationStatuses.GrantApplicationReady,
            InputPolicyEffect = policyDecision.Effect,
            InputPolicyRule = policyDecision.RuleName,
            RequestedCapability = policyDecision.RequestedCapability,
            RequestedScope = policyDecision.RequestedScope,
            PreconditionsMet = met,
            PreconditionsMissing = Array.Empty<string>(),
            Reasoning = "all preconditions met; application Ready. Ready != Applied — application is a separate write-out path not executed here.",
            ApplicationApplied = false  // 关键不变量 — Ready 不是 Applied。
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
