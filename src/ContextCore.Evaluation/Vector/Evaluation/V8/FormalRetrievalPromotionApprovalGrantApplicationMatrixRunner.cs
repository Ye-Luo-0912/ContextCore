using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner
{
    public FormalRetrievalPromotionApprovalGrantApplicationMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalGrantApplicationCase>();

        foreach (var scenario in BuildScenarios())
        {
            var decision = FormalRetrievalPromotionApprovalGrantApplicationPolicy.Evaluate(
                scenario.PolicyDecision, scenario.Preconditions);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var missingMatched = scenario.ExpectedMissingPrecondition is null
                || decision.PreconditionsMissing.Contains(scenario.ExpectedMissingPrecondition, StringComparer.Ordinal);
            var applicationNotApplied = !decision.ApplicationApplied;

            // 数量约束 — Blocked 至少 1 个 missing；Ready 必须 0 个 missing；NotApplicable 两边都空。
            var countShapeOk = scenario.ExpectedStatus switch
            {
                _ when scenario.ExpectedStatus == GrantApplicationStatuses.GrantApplicationReady =>
                    decision.PreconditionsMissing.Count == 0 && decision.PreconditionsMet.Count == GrantApplicationPreconditions.AllInOrder.Count,
                _ when scenario.ExpectedStatus == GrantApplicationStatuses.GrantApplicationBlocked =>
                    decision.PreconditionsMissing.Count >= 1,
                _ when scenario.ExpectedStatus == GrantApplicationStatuses.GrantApplicationNotApplicable =>
                    decision.PreconditionsMissing.Count == 0 && decision.PreconditionsMet.Count == 0,
                _ => false
            };

            var passedAsExpected = statusMatched && missingMatched && applicationNotApplied && countShapeOk;

            cases.Add(new FormalRetrievalPromotionApprovalGrantApplicationCase
            {
                CaseName = scenario.CaseName,
                RequestedCapability = scenario.PolicyDecision.RequestedCapability,
                RequestedScope = scenario.PolicyDecision.RequestedScope,
                InputPolicyEffect = scenario.PolicyDecision.Effect,
                InputPolicyRule = scenario.PolicyDecision.RuleName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedMissingPrecondition = scenario.ExpectedMissingPrecondition ?? string.Empty,
                ActualPreconditionsMet = decision.PreconditionsMet,
                ActualPreconditionsMissing = decision.PreconditionsMissing,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                MissingPreconditionMatched = missingMatched,
                CountShapeOk = countShapeOk,
                ApplicationNotApplied = applicationNotApplied,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var notApplicableCases = cases.Count(static c => c.ActualStatus == GrantApplicationStatuses.GrantApplicationNotApplicable);
        var blockedCases = cases.Count(static c => c.ActualStatus == GrantApplicationStatuses.GrantApplicationBlocked);
        var readyCases = cases.Count(static c => c.ActualStatus == GrantApplicationStatuses.GrantApplicationReady);

        var blocked = new List<string>();
        if (cases.Count < 7)
        {
            blocked.Add("InsufficientGrantApplicationCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("GrantApplicationMatrixFailed");
        }

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[]
                 {
                     GrantApplicationStatuses.GrantApplicationNotApplicable,
                     GrantApplicationStatuses.GrantApplicationBlocked,
                     GrantApplicationStatuses.GrantApplicationReady
                 })
        {
            if (!statusesCovered.Contains(s))
            {
                blocked.Add($"StatusBranchNotCovered:{s}");
            }
        }

        // 每个 precondition 必须至少有一个 case 单独将其放在 missing 中（验证检测能力）。
        var individuallyMissed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (c.ActualPreconditionsMissing.Count == 1)
            {
                individuallyMissed.Add(c.ActualPreconditionsMissing[0]);
            }
        }

        foreach (var p in GrantApplicationPreconditions.AllInOrder)
        {
            if (!individuallyMissed.Contains(p))
            {
                blocked.Add($"PreconditionNotIsolatedlyTested:{p}");
            }
        }

        // 核心 invariant — 任何 case 的 ApplicationApplied=true 都是契约违反。
        if (cases.Any(c => !c.ApplicationNotApplied))
        {
            blocked.Add("GrantApplicationWasApplied");
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

        return new FormalRetrievalPromotionApprovalGrantApplicationMatrixReport
        {
            OperationId = $"frp-grant-application-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            GrantApplicationMatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            NotApplicableCases = notApplicableCases,
            BlockedCases = blockedCases,
            ReadyCases = readyCases,
            Cases = cases,
            // V8.13 显式契约：grant application policy 决定 "能否应用"，但绝不"实际应用"。
            ManualReviewRequired = false,
            ApprovalSealed = false,
            CapabilityGrantWritten = false,
            GrantApplied = false,
            ApplicationApplied = false,
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
                $"notApplicable={notApplicableCases}",
                $"blocked={blockedCases}",
                $"ready={readyCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=ExplicitApplicationWriteOut (Ready != Applied; application requires a dedicated write-out path with its own gate)"
            }
        };
    }

    private static IReadOnlyList<GrantApplicationScenario> BuildScenarios() =>
    [
        new(
            "DenyInputNotApplicable",
            BuildPolicyDecision(PolicyAuthorityEffects.Deny, PolicyAuthorityRules.FixtureTrustModeCannotAuthorizeProduction),
            BuildAllMet(),  // 即便前置条件齐全，Deny 输入也只是 NotApplicable。
            GrantApplicationStatuses.GrantApplicationNotApplicable,
            ExpectedMissingPrecondition: null),
        new(
            "IndeterminateInputNotApplicable",
            BuildPolicyDecision(PolicyAuthorityEffects.Indeterminate, PolicyAuthorityRules.CapabilityNotInPolicyAuthority),
            BuildAllMet(),
            GrantApplicationStatuses.GrantApplicationNotApplicable,
            ExpectedMissingPrecondition: null),
        new(
            "GrantMissingApprovalSeal",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            BuildPreconditionsWithMissing(GrantApplicationPreconditions.ApprovalSealArtifactPresent),
            GrantApplicationStatuses.GrantApplicationBlocked,
            ExpectedMissingPrecondition: GrantApplicationPreconditions.ApprovalSealArtifactPresent),
        new(
            "GrantMissingDryRunArtifact",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            BuildPreconditionsWithMissing(GrantApplicationPreconditions.DryRunCleanArtifactPresent),
            GrantApplicationStatuses.GrantApplicationBlocked,
            ExpectedMissingPrecondition: GrantApplicationPreconditions.DryRunCleanArtifactPresent),
        new(
            "GrantMissingAuditLog",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            BuildPreconditionsWithMissing(GrantApplicationPreconditions.AuditLogArtifactPresent),
            GrantApplicationStatuses.GrantApplicationBlocked,
            ExpectedMissingPrecondition: GrantApplicationPreconditions.AuditLogArtifactPresent),
        new(
            "GrantMissingRuntimeGate",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            BuildPreconditionsWithMissing(GrantApplicationPreconditions.RuntimeChangeReadinessGatePassed),
            GrantApplicationStatuses.GrantApplicationBlocked,
            ExpectedMissingPrecondition: GrantApplicationPreconditions.RuntimeChangeReadinessGatePassed),
        new(
            "GrantMissingTrustChainReverification",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            BuildPreconditionsWithMissing(GrantApplicationPreconditions.TrustChainReverificationGatePassed),
            GrantApplicationStatuses.GrantApplicationBlocked,
            ExpectedMissingPrecondition: GrantApplicationPreconditions.TrustChainReverificationGatePassed),
        new(
            "GrantAllPreconditionsMet_Ready_ButNotApplied",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            BuildAllMet(),
            GrantApplicationStatuses.GrantApplicationReady,
            ExpectedMissingPrecondition: null),
        // 多 precondition 同时缺 — 验证 missing 集合返回多项，整体仍 Blocked。
        new(
            "GrantMultiplePreconditionsMissing",
            BuildPolicyDecision(PolicyAuthorityEffects.Grant, PolicyAuthorityRules.AuthorizedByPolicy),
            new GrantApplicationPreconditionsState
            {
                ApprovalSealArtifactPresent = false,
                DryRunCleanArtifactPresent = false,
                AuditLogArtifactPresent = true,
                RuntimeChangeReadinessGatePassed = true,
                TrustChainReverificationGatePassed = true
            },
            GrantApplicationStatuses.GrantApplicationBlocked,
            // 只断言其中一个出现在 missing 中（两个都该出现，但精确断言一个就够）。
            ExpectedMissingPrecondition: GrantApplicationPreconditions.ApprovalSealArtifactPresent)
    ];

    private static PolicyAuthorityDecision BuildPolicyDecision(string effect, string rule) => new()
    {
        Effect = effect,
        Status = effect == PolicyAuthorityEffects.Deny || effect == PolicyAuthorityEffects.Grant || effect == PolicyAuthorityEffects.Indeterminate
            ? PolicyAuthorityStatuses.PolicyAuthorityResolved
            : PolicyAuthorityStatuses.PolicyAuthorityUnreachable,
        RuleName = rule,
        Reasoning = $"fixture policy decision: effect={effect}, rule={rule}",
        IsResolved = true,
        RequestedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
        RequestedScope = "demo-workspace/demo-collection",
        AppliedTrustMode = "production-signed",
        GrantApplied = false
    };

    private static GrantApplicationPreconditionsState BuildAllMet() => new()
    {
        ApprovalSealArtifactPresent = true,
        DryRunCleanArtifactPresent = true,
        AuditLogArtifactPresent = true,
        RuntimeChangeReadinessGatePassed = true,
        TrustChainReverificationGatePassed = true
    };

    private static GrantApplicationPreconditionsState BuildPreconditionsWithMissing(string missingName)
    {
        return new GrantApplicationPreconditionsState
        {
            ApprovalSealArtifactPresent = missingName != GrantApplicationPreconditions.ApprovalSealArtifactPresent,
            DryRunCleanArtifactPresent = missingName != GrantApplicationPreconditions.DryRunCleanArtifactPresent,
            AuditLogArtifactPresent = missingName != GrantApplicationPreconditions.AuditLogArtifactPresent,
            RuntimeChangeReadinessGatePassed = missingName != GrantApplicationPreconditions.RuntimeChangeReadinessGatePassed,
            TrustChainReverificationGatePassed = missingName != GrantApplicationPreconditions.TrustChainReverificationGatePassed
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalGrantApplicationMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- GrantApplicationMatrixPassed: `{r.GrantApplicationMatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — NotApplicable: `{r.NotApplicableCases}` Blocked: `{r.BlockedCases}` Ready: `{r.ReadyCases}`");
        b.AppendLine();
        b.AppendLine("## No-Application Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- GrantApplied: `{r.GrantApplied}`");
        b.AppendLine($"- ApplicationApplied: `{r.ApplicationApplied}`  (Ready != Applied)");
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
        b.AppendLine("## Grant Application Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - input: effect=`{c.InputPolicyEffect}` rule=`{c.InputPolicyRule}` capability=`{c.RequestedCapability}` scope=`{c.RequestedScope}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedMissingPrecondition))
            {
                b.AppendLine($"  - expectedMissing=`{c.ExpectedMissingPrecondition}` matched=`{c.MissingPreconditionMatched}`");
            }
            if (c.ActualPreconditionsMet.Count > 0)
            {
                b.AppendLine($"  - met=`{string.Join(", ", c.ActualPreconditionsMet)}`");
            }
            if (c.ActualPreconditionsMissing.Count > 0)
            {
                b.AppendLine($"  - missing=`{string.Join(", ", c.ActualPreconditionsMissing)}`");
            }
            b.AppendLine($"  - applicationNotApplied=`{c.ApplicationNotApplied}` countShapeOk=`{c.CountShapeOk}`");
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

        b.AppendLine("V8.13 grant application matrix。policy decision + 5 个 artifact-level precondition → NotApplicable / Blocked / Ready。Ready != Applied — ApplicationApplied 永远 false；不写 grant、不激活、不进 manual review。");
        return b.ToString();
    }
}

public sealed record GrantApplicationScenario(
    string CaseName,
    PolicyAuthorityDecision PolicyDecision,
    GrantApplicationPreconditionsState Preconditions,
    string ExpectedStatus,
    string? ExpectedMissingPrecondition);

public sealed class FormalRetrievalPromotionApprovalGrantApplicationCase
{
    public string CaseName { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public string InputPolicyEffect { get; init; } = string.Empty;
    public string InputPolicyRule { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedMissingPrecondition { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualPreconditionsMet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActualPreconditionsMissing { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool MissingPreconditionMatched { get; init; }
    public bool CountShapeOk { get; init; }

    /// <summary>核心契约 — 即便 Ready，ApplicationApplied 也是 false。</summary>
    public bool ApplicationNotApplied { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalGrantApplicationMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GrantApplicationMatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int NotApplicableCases { get; init; }
    public int BlockedCases { get; init; }
    public int ReadyCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalGrantApplicationCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalGrantApplicationCase>();

    // No-Application Contract
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool GrantApplied { get; init; }

    /// <summary>matrix 级核心 invariant — 任何 case 的 Ready 也不会被实际 Applied。</summary>
    public bool ApplicationApplied { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public bool EvidenceCopiedToMainline { get; init; }
    public bool TrustRegistryCopiedToMainline { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }

    // Safety
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

public sealed class FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
