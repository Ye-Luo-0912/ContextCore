using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner
{
    public FormalRetrievalPromotionApprovalPolicyAuthorityMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalPolicyAuthorityCase>();

        foreach (var scenario in BuildScenarios())
        {
            var (chain, record) = scenario.BuildInputs();
            var decision = FormalRetrievalPromotionApprovalPolicyAuthorityModel.Evaluate(
                chain, record, scenario.RequestedCapability, scenario.RequestedScope);

            var effectMatched = string.Equals(scenario.ExpectedEffect, decision.Effect, StringComparison.Ordinal);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var ruleMatched = string.Equals(scenario.ExpectedRuleName, decision.RuleName, StringComparison.Ordinal);
            var resolvedMatched = scenario.ExpectedIsResolved == decision.IsResolved;
            var grantNotApplied = !decision.GrantApplied;

            var passedAsExpected = effectMatched
                && statusMatched
                && ruleMatched
                && resolvedMatched
                && grantNotApplied;

            cases.Add(new FormalRetrievalPromotionApprovalPolicyAuthorityCase
            {
                CaseName = scenario.CaseName,
                RequestedCapability = scenario.RequestedCapability,
                RequestedScope = scenario.RequestedScope,
                ExpectedEffect = scenario.ExpectedEffect,
                ActualEffect = decision.Effect,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedRuleName = scenario.ExpectedRuleName,
                ActualRuleName = decision.RuleName,
                ExpectedIsResolved = scenario.ExpectedIsResolved,
                ActualIsResolved = decision.IsResolved,
                AppliedTrustMode = decision.AppliedTrustMode,
                Reasoning = decision.Reasoning,
                EffectMatched = effectMatched,
                StatusMatched = statusMatched,
                RuleMatched = ruleMatched,
                IsResolvedMatched = resolvedMatched,
                GrantNotApplied = grantNotApplied,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var grantCases = cases.Count(static c => string.Equals(c.ActualEffect, PolicyAuthorityEffects.Grant, StringComparison.Ordinal));
        var denyCases = cases.Count(static c => string.Equals(c.ActualEffect, PolicyAuthorityEffects.Deny, StringComparison.Ordinal));
        var indeterminateCases = cases.Count(static c => string.Equals(c.ActualEffect, PolicyAuthorityEffects.Indeterminate, StringComparison.Ordinal));

        var blocked = new List<string>();
        if (cases.Count < 5)
        {
            blocked.Add("InsufficientPolicyAuthorityCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("PolicyAuthorityMatrixFailed");
        }

        // 每个 rule branch 至少有一个 case 覆盖。
        var rulesCovered = cases.Select(c => c.ActualRuleName).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in new[]
                 {
                     PolicyAuthorityRules.NoTrustChain,
                     PolicyAuthorityRules.FixtureTrustModeCannotAuthorizeProduction,
                     PolicyAuthorityRules.ScopeOutOfAuthority,
                     PolicyAuthorityRules.CapabilityNotInPolicyAuthority,
                     PolicyAuthorityRules.AuthorizedByPolicy
                 })
        {
            if (!rulesCovered.Contains(rule))
            {
                blocked.Add($"RuleBranchNotCovered:{rule}");
            }
        }

        // 任何 case 的 GrantApplied=true 都是契约违反 — 决策不是动作。
        if (cases.Any(c => !c.GrantNotApplied))
        {
            blocked.Add("PolicyAuthorityGrantWasApplied");
        }

        if (mainlineEvidencePresent)
        {
            blocked.Add("MainlineEvidencePresent");
        }

        if (mainlineRegistryPresent)
        {
            blocked.Add("MainlineTrustRegistryPresent");
        }

        if (!rtPassed)
        {
            blocked.Add("RuntimeChangeGateNotPassed");
        }

        if (!p15Passed)
        {
            blocked.Add("P15GateNotPassed");
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var matrixPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && matrixPassed;

        return new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixReport
        {
            OperationId = $"frp-policy-authority-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            PolicyAuthorityMatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            GrantCases = grantCases,
            DenyCases = denyCases,
            IndeterminateCases = indeterminateCases,
            Cases = cases,
            // V8.12 显式契约：policy 决策不是 grant 写入、不是激活、不要求人工审查。
            ManualReviewRequired = false,
            ApprovalSealed = false,
            CapabilityGrantWritten = false,
            GrantApplied = false,
            PromotionToMainlinePerformed = false,
            EvidenceCopiedToMainline = false,
            TrustRegistryCopiedToMainline = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            // 安全不变量
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ConfigPatchWritten = false,
            RuntimeActivation = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Count}",
                $"passed={passedCases}",
                $"failed={failedCases}",
                $"grant={grantCases}",
                $"deny={denyCases}",
                $"indeterminate={indeterminateCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=GrantApplicationPolicy (decision is not action; grants must still be explicitly applied through a separate path)"
            }
        };
    }

    private static IReadOnlyList<PolicyAuthorityScenario> BuildScenarios() =>
    [
        // 规则 1 — trust chain 未完整 → policy 不可达。
        new(
            "TrustChainBrokenInput",
            RequestedCapability: PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
            RequestedScope: "demo-workspace/demo-collection",
            ExpectedEffect: PolicyAuthorityEffects.Deny,
            ExpectedStatus: PolicyAuthorityStatuses.PolicyAuthorityUnreachable,
            ExpectedRuleName: PolicyAuthorityRules.NoTrustChain,
            ExpectedIsResolved: false,
            BuildInputs: () => (BuildBrokenChain(), null)),
        // 规则 2 — fixture trust mode 永远 Deny production capability。
        new(
            "FixtureTrustModeBlocksGrant",
            RequestedCapability: PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
            RequestedScope: "demo-workspace/demo-collection",
            ExpectedEffect: PolicyAuthorityEffects.Deny,
            ExpectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            ExpectedRuleName: PolicyAuthorityRules.FixtureTrustModeCannotAuthorizeProduction,
            ExpectedIsResolved: true,
            BuildInputs: () => BuildChainAndRecord(trustMode: "fixture-dry-run")),
        // 规则 3 — 请求 scope 越界（用非 fixture trust mode 排除规则 2）。
        new(
            "ScopeOutOfAuthority",
            RequestedCapability: PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
            RequestedScope: "out-of-bounds/illegal-collection",
            ExpectedEffect: PolicyAuthorityEffects.Deny,
            ExpectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            ExpectedRuleName: PolicyAuthorityRules.ScopeOutOfAuthority,
            ExpectedIsResolved: true,
            BuildInputs: () => BuildChainAndRecord(trustMode: "production-signed")),
        // 规则 4 — capability 不在名录 → Indeterminate。
        new(
            "CapabilityNotInPolicyAuthority",
            RequestedCapability: "UnknownExperimentalCapability",
            RequestedScope: "demo-workspace/demo-collection",
            ExpectedEffect: PolicyAuthorityEffects.Indeterminate,
            ExpectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            ExpectedRuleName: PolicyAuthorityRules.CapabilityNotInPolicyAuthority,
            ExpectedIsResolved: true,
            BuildInputs: () => BuildChainAndRecord(trustMode: "production-signed")),
        // 规则 5 — 全部 precondition 满足 → Grant（决策）。
        new(
            "CleanProductionGrant",
            RequestedCapability: PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
            RequestedScope: "demo-workspace/demo-collection",
            ExpectedEffect: PolicyAuthorityEffects.Grant,
            ExpectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            ExpectedRuleName: PolicyAuthorityRules.AuthorizedByPolicy,
            ExpectedIsResolved: true,
            BuildInputs: () => BuildChainAndRecord(trustMode: "production-signed")),
        // 规则 5 — 多个已知 capability 都能授权。
        new(
            "CleanProductionGrant_MainlineEvidenceWrite",
            RequestedCapability: PolicyAuthorityKnownCapabilities.MainlineEvidenceWrite,
            RequestedScope: "demo-workspace/demo-collection",
            ExpectedEffect: PolicyAuthorityEffects.Grant,
            ExpectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            ExpectedRuleName: PolicyAuthorityRules.AuthorizedByPolicy,
            ExpectedIsResolved: true,
            BuildInputs: () => BuildChainAndRecord(trustMode: "production-signed"))
    ];

    private static TrustChainValidationResult BuildBrokenChain() => new()
    {
        ChainComplete = false,
        Status = TrustChainValidationStatuses.TrustChainBroken,
        MismatchReasons = new[] { TrustChainMismatchReasons.EvidenceProvenanceNotFoundInRegistry },
        MismatchFields = new[] { nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvenanceId) },
        MatchedProvenanceId = null,
        MatchedRecordIndex = -1
    };

    private static (TrustChainValidationResult Chain, FormalRetrievalPromotionApprovalTrustedProvenanceRecord Record) BuildChainAndRecord(string trustMode)
    {
        var record = new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
        {
            ApprovalEvidenceProvenanceId = "fixture-provenance-policy-001",
            ApprovalEvidenceSourceKind = "fixture",
            ApprovalEvidenceProvidedBy = "FixturePolicyOperator",
            ApprovalEvidenceChecksum = "fixture-checksum-policy-001",
            SourceApprovalRequestId = "frp-approval-policy-001",
            BoundPendingApprovalGateOperationId = "frp-approval-gate-policy-001",
            AllowedScopes = new[] { "demo-workspace/demo-collection" },
            TrustMode = trustMode,
            ValidUntil = DateTimeOffset.Parse("2027-12-31T00:00:00Z")
        };

        var chain = new TrustChainValidationResult
        {
            ChainComplete = true,
            Status = TrustChainValidationStatuses.TrustChainValidated,
            MismatchReasons = Array.Empty<string>(),
            MismatchFields = Array.Empty<string>(),
            MatchedProvenanceId = record.ApprovalEvidenceProvenanceId,
            MatchedRecordIndex = 0
        };

        return (chain, record);
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalPolicyAuthorityMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PolicyAuthorityMatrixPassed: `{r.PolicyAuthorityMatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Effect breakdown — Grant: `{r.GrantCases}` Deny: `{r.DenyCases}` Indeterminate: `{r.IndeterminateCases}`");
        b.AppendLine();
        b.AppendLine("## No-Manual-Review / No-Application Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- GrantApplied: `{r.GrantApplied}`  (Grant decision is not Grant application)");
        b.AppendLine($"- PromotionToMainlinePerformed: `{r.PromotionToMainlinePerformed}`");
        b.AppendLine($"- EvidenceCopiedToMainline: `{r.EvidenceCopiedToMainline}`");
        b.AppendLine($"- TrustRegistryCopiedToMainline: `{r.TrustRegistryCopiedToMainline}`");
        b.AppendLine($"- MainlineEvidencePresent: `{r.MainlineEvidencePresent}`");
        b.AppendLine($"- MainlineTrustRegistryPresent: `{r.MainlineTrustRegistryPresent}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("## Policy Authority Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - request: capability=`{c.RequestedCapability}` scope=`{c.RequestedScope}`");
            b.AppendLine($"  - effect expected=`{c.ExpectedEffect}` actual=`{c.ActualEffect}` matched=`{c.EffectMatched}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            b.AppendLine($"  - rule expected=`{c.ExpectedRuleName}` actual=`{c.ActualRuleName}` matched=`{c.RuleMatched}`");
            b.AppendLine($"  - resolved expected=`{c.ExpectedIsResolved}` actual=`{c.ActualIsResolved}` matched=`{c.IsResolvedMatched}`");
            b.AppendLine($"  - appliedTrustMode=`{c.AppliedTrustMode}` grantNotApplied=`{c.GrantNotApplied}`");
            if (!string.IsNullOrEmpty(c.Reasoning))
            {
                b.AppendLine($"  - reasoning: {c.Reasoning}");
            }
        }

        b.AppendLine();
        if (r.BlockedReasons.Count > 0)
        {
            b.AppendLine("## Blocked Reasons");
            foreach (var br in r.BlockedReasons)
            {
                b.AppendLine($"- `{br}`");
            }
            b.AppendLine();
        }

        b.AppendLine("V8.12 policy authority matrix。trust-chain-validated candidate 经规则栈（NoTrustChain / FixtureTrustModeCannotAuthorizeProduction / ScopeOutOfAuthority / CapabilityNotInPolicyAuthority / AuthorizedByPolicy）得到机器决策；Grant 决策 ≠ Grant 应用；GrantApplied 永远 false；不写 mainline、不 seal、不进 manual review。");
        return b.ToString();
    }
}

public sealed record PolicyAuthorityScenario(
    string CaseName,
    string RequestedCapability,
    string RequestedScope,
    string ExpectedEffect,
    string ExpectedStatus,
    string ExpectedRuleName,
    bool ExpectedIsResolved,
    Func<(TrustChainValidationResult Chain, FormalRetrievalPromotionApprovalTrustedProvenanceRecord? Record)> BuildInputs);

public sealed class FormalRetrievalPromotionApprovalPolicyAuthorityCase
{
    public string CaseName { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public string ExpectedEffect { get; init; } = string.Empty;
    public string ActualEffect { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedRuleName { get; init; } = string.Empty;
    public string ActualRuleName { get; init; } = string.Empty;
    public bool ExpectedIsResolved { get; init; }
    public bool ActualIsResolved { get; init; }
    public string AppliedTrustMode { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
    public bool EffectMatched { get; init; }
    public bool StatusMatched { get; init; }
    public bool RuleMatched { get; init; }
    public bool IsResolvedMatched { get; init; }

    /// <summary>核心契约 — Grant 决策从不被应用。</summary>
    public bool GrantNotApplied { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalPolicyAuthorityMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PolicyAuthorityMatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int GrantCases { get; init; }
    public int DenyCases { get; init; }
    public int IndeterminateCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalPolicyAuthorityCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalPolicyAuthorityCase>();

    // No-Manual-Review / No-Application Contract
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool CapabilityGrantWritten { get; init; }

    /// <summary>matrix 级不变量 — 任何 case 的 Grant 决策都不被应用。</summary>
    public bool GrantApplied { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public bool EvidenceCopiedToMainline { get; init; }
    public bool TrustRegistryCopiedToMainline { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }

    // Safety invariants
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
