using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>V8.12 policy authority decision effect。</summary>
public static class PolicyAuthorityEffects
{
    public const string Grant = nameof(Grant);
    public const string Deny = nameof(Deny);
    public const string Indeterminate = nameof(Indeterminate);
}

/// <summary>V8.12 policy authority decision 状态。</summary>
public static class PolicyAuthorityStatuses
{
    /// <summary>policy 完整执行并产出有效决策（grant/deny/indeterminate）。</summary>
    public const string PolicyAuthorityResolved = nameof(PolicyAuthorityResolved);

    /// <summary>前置条件失败（如 trust chain 未验通），policy 不可达。</summary>
    public const string PolicyAuthorityUnreachable = nameof(PolicyAuthorityUnreachable);
}

/// <summary>V8.12 policy authority rule 命名常量。</summary>
public static class PolicyAuthorityRules
{
    /// <summary>trust chain 未完整（V8.11 校验失败），policy 无法评估。</summary>
    public const string NoTrustChain = nameof(NoTrustChain);

    /// <summary>fixture/preview trust mode 不允许授权 production capability。</summary>
    public const string FixtureTrustModeCannotAuthorizeProduction = nameof(FixtureTrustModeCannotAuthorizeProduction);

    /// <summary>请求 scope 不在 record.AllowedScopes 内。</summary>
    public const string ScopeOutOfAuthority = nameof(ScopeOutOfAuthority);

    /// <summary>请求 capability 不在 policy 已知名录中。</summary>
    public const string CapabilityNotInPolicyAuthority = nameof(CapabilityNotInPolicyAuthority);

    /// <summary>所有 precondition 满足；policy 按授权矩阵 grant。</summary>
    public const string AuthorizedByPolicy = nameof(AuthorizedByPolicy);
}

/// <summary>V8.12 policy 已知 capability 名录。capability 必须在此名录内，否则 indeterminate。</summary>
public static class PolicyAuthorityKnownCapabilities
{
    public const string FormalRetrievalActivation = nameof(FormalRetrievalActivation);
    public const string MainlineEvidenceWrite = nameof(MainlineEvidenceWrite);
    public const string MainlineTrustRegistryWrite = nameof(MainlineTrustRegistryWrite);
    public const string RuntimeSwitch = nameof(RuntimeSwitch);
    public const string PackagePolicyMutation = nameof(PackagePolicyMutation);
    public const string VectorStoreBindingChange = nameof(VectorStoreBindingChange);

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            FormalRetrievalActivation,
            MainlineEvidenceWrite,
            MainlineTrustRegistryWrite,
            RuntimeSwitch,
            PackagePolicyMutation,
            VectorStoreBindingChange
        };
}

/// <summary>V8.12 fixture/preview trust mode — 在 fixture 阶段，永远 Deny 任何 capability。</summary>
public static class PolicyAuthorityFixtureTrustModes
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "fixture-dry-run",
            "fixture-multi",
            "registry-preview",
            "fixture"
        };
}

/// <summary>V8.12 policy authority decision。机器判定结果；不写 grant、不激活。</summary>
public sealed class PolicyAuthorityDecision
{
    public string Effect { get; init; } = PolicyAuthorityEffects.Deny;
    public string Status { get; init; } = PolicyAuthorityStatuses.PolicyAuthorityUnreachable;
    public string RuleName { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public string AppliedTrustMode { get; init; } = string.Empty;

    /// <summary>无论 Effect 是 Grant，都不代表已经写入 grant 或激活 capability。</summary>
    public bool GrantApplied { get; init; }
}

/// <summary>
/// V8.12 policy authority model。给定一个 trust-chain-validated 记录 + 请求的 (capability, scope)，
/// 按规则优先级返回授权决策。纯函数；不写 grant、不激活、不进 manual review。
/// </summary>
public static class FormalRetrievalPromotionApprovalPolicyAuthorityModel
{
    public static PolicyAuthorityDecision Evaluate(
        TrustChainValidationResult chain,
        FormalRetrievalPromotionApprovalTrustedProvenanceRecord? matchedRecord,
        string requestedCapability,
        string requestedScope)
    {
        ArgumentNullException.ThrowIfNull(chain);
        requestedCapability ??= string.Empty;
        requestedScope ??= string.Empty;

        // rule 1 — 没有有效信任链，policy 不可达。
        if (!chain.ChainComplete || matchedRecord is null)
        {
            return new PolicyAuthorityDecision
            {
                Effect = PolicyAuthorityEffects.Deny,
                Status = PolicyAuthorityStatuses.PolicyAuthorityUnreachable,
                RuleName = PolicyAuthorityRules.NoTrustChain,
                Reasoning = "trust chain not validated; policy authority unreachable.",
                IsResolved = false,
                RequestedCapability = requestedCapability,
                RequestedScope = requestedScope,
                AppliedTrustMode = matchedRecord?.TrustMode ?? string.Empty,
                GrantApplied = false
            };
        }

        // rule 2 — fixture/preview trust mode 永远不授权 production capability。
        if (PolicyAuthorityFixtureTrustModes.All.Contains(matchedRecord.TrustMode))
        {
            return new PolicyAuthorityDecision
            {
                Effect = PolicyAuthorityEffects.Deny,
                Status = PolicyAuthorityStatuses.PolicyAuthorityResolved,
                RuleName = PolicyAuthorityRules.FixtureTrustModeCannotAuthorizeProduction,
                Reasoning = $"trust mode '{matchedRecord.TrustMode}' is fixture/preview; cannot authorize production capability.",
                IsResolved = true,
                RequestedCapability = requestedCapability,
                RequestedScope = requestedScope,
                AppliedTrustMode = matchedRecord.TrustMode,
                GrantApplied = false
            };
        }

        // rule 3 — 请求 scope 不在 record 授权范围。
        var inScope = matchedRecord.AllowedScopes.Any(s => string.Equals(s, requestedScope, StringComparison.Ordinal));
        if (!inScope)
        {
            return new PolicyAuthorityDecision
            {
                Effect = PolicyAuthorityEffects.Deny,
                Status = PolicyAuthorityStatuses.PolicyAuthorityResolved,
                RuleName = PolicyAuthorityRules.ScopeOutOfAuthority,
                Reasoning = $"requested scope '{requestedScope}' not in record allowed scopes.",
                IsResolved = true,
                RequestedCapability = requestedCapability,
                RequestedScope = requestedScope,
                AppliedTrustMode = matchedRecord.TrustMode,
                GrantApplied = false
            };
        }

        // rule 4 — capability 不在 policy 已知名录。
        if (!PolicyAuthorityKnownCapabilities.All.Contains(requestedCapability))
        {
            return new PolicyAuthorityDecision
            {
                Effect = PolicyAuthorityEffects.Indeterminate,
                Status = PolicyAuthorityStatuses.PolicyAuthorityResolved,
                RuleName = PolicyAuthorityRules.CapabilityNotInPolicyAuthority,
                Reasoning = $"requested capability '{requestedCapability}' not enumerated by policy; outcome indeterminate.",
                IsResolved = true,
                RequestedCapability = requestedCapability,
                RequestedScope = requestedScope,
                AppliedTrustMode = matchedRecord.TrustMode,
                GrantApplied = false
            };
        }

        // rule 5 — 所有 precondition 满足；policy 授予 capability（决策，不是动作）。
        return new PolicyAuthorityDecision
        {
            Effect = PolicyAuthorityEffects.Grant,
            Status = PolicyAuthorityStatuses.PolicyAuthorityResolved,
            RuleName = PolicyAuthorityRules.AuthorizedByPolicy,
            Reasoning = $"trust mode '{matchedRecord.TrustMode}' authorizes '{requestedCapability}' in scope '{requestedScope}'. Decision only; not applied.",
            IsResolved = true,
            RequestedCapability = requestedCapability,
            RequestedScope = requestedScope,
            AppliedTrustMode = matchedRecord.TrustMode,
            GrantApplied = false  // 关键不变量 — Grant 决策不等于 Grant 应用。
        };
    }
}
