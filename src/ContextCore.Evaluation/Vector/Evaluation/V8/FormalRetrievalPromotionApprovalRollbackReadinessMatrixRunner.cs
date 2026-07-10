using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner
{
    public FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalRollbackReadinessCase>();

        foreach (var scenario in BuildScenarios())
        {
            var decision = FormalRetrievalPromotionApprovalRollbackReadinessPolicy.Evaluate(
                scenario.ApplicationDecision, scenario.Preparedness);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var missingMatched = scenario.ExpectedMissingElement is null
                || decision.RollbackElementsMissing.Contains(scenario.ExpectedMissingElement, StringComparer.Ordinal);
            var rollbackNotActivated = !decision.RollbackActivated;
            var applicationNotApplied = !decision.ApplicationApplied;

            var countShapeOk = scenario.ExpectedStatus switch
            {
                _ when scenario.ExpectedStatus == RollbackReadinessStatuses.RollbackReady =>
                    decision.RollbackElementsMissing.Count == 0 && decision.RollbackElementsMet.Count == RollbackElements.AllInOrder.Count,
                _ when scenario.ExpectedStatus == RollbackReadinessStatuses.RollbackReadinessIncomplete =>
                    decision.RollbackElementsMissing.Count >= 1,
                _ when scenario.ExpectedStatus == RollbackReadinessStatuses.RollbackReadinessNotApplicable =>
                    decision.RollbackElementsMissing.Count == 0 && decision.RollbackElementsMet.Count == 0,
                _ => false
            };

            var passedAsExpected = statusMatched
                && missingMatched
                && rollbackNotActivated
                && applicationNotApplied
                && countShapeOk;

            cases.Add(new FormalRetrievalPromotionApprovalRollbackReadinessCase
            {
                CaseName = scenario.CaseName,
                InputApplicationStatus = scenario.ApplicationDecision.Status,
                RequestedCapability = scenario.ApplicationDecision.RequestedCapability,
                RequestedScope = scenario.ApplicationDecision.RequestedScope,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedMissingElement = scenario.ExpectedMissingElement ?? string.Empty,
                ActualRollbackElementsMet = decision.RollbackElementsMet,
                ActualRollbackElementsMissing = decision.RollbackElementsMissing,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                MissingElementMatched = missingMatched,
                CountShapeOk = countShapeOk,
                RollbackNotActivated = rollbackNotActivated,
                ApplicationNotApplied = applicationNotApplied,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var notApplicableCases = cases.Count(static c => c.ActualStatus == RollbackReadinessStatuses.RollbackReadinessNotApplicable);
        var incompleteCases = cases.Count(static c => c.ActualStatus == RollbackReadinessStatuses.RollbackReadinessIncomplete);
        var readyCases = cases.Count(static c => c.ActualStatus == RollbackReadinessStatuses.RollbackReady);

        var blocked = new List<string>();
        if (cases.Count < 7)
        {
            blocked.Add("InsufficientRollbackReadinessCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("RollbackReadinessMatrixFailed");
        }

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[]
                 {
                     RollbackReadinessStatuses.RollbackReadinessNotApplicable,
                     RollbackReadinessStatuses.RollbackReadinessIncomplete,
                     RollbackReadinessStatuses.RollbackReady
                 })
        {
            if (!statusesCovered.Contains(s))
            {
                blocked.Add($"StatusBranchNotCovered:{s}");
            }
        }

        // 每个 rollback element 必须有一个孤立反例。
        var individuallyMissed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (c.ActualRollbackElementsMissing.Count == 1)
            {
                individuallyMissed.Add(c.ActualRollbackElementsMissing[0]);
            }
        }

        foreach (var e in RollbackElements.AllInOrder)
        {
            if (!individuallyMissed.Contains(e))
            {
                blocked.Add($"RollbackElementNotIsolatedlyTested:{e}");
            }
        }

        // 核心 invariant — RollbackActivated 与 ApplicationApplied 都必须保持 false。
        if (cases.Any(c => !c.RollbackNotActivated))
        {
            blocked.Add("RollbackPathWasActivated");
        }

        if (cases.Any(c => !c.ApplicationNotApplied))
        {
            blocked.Add("ApplicationWasApplied");
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

        return new FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport
        {
            OperationId = $"frp-rollback-readiness-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            RollbackReadinessMatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            NotApplicableCases = notApplicableCases,
            IncompleteCases = incompleteCases,
            ReadyCases = readyCases,
            Cases = cases,
            // V8.14 显式契约：rollback readiness 是"应用前的可撤回保证"；矩阵评估不触发回滚路径、也不触发应用。
            ManualReviewRequired = false,
            ApprovalSealed = false,
            CapabilityGrantWritten = false,
            GrantApplied = false,
            ApplicationApplied = false,
            RollbackActivated = false,
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
                $"incomplete={incompleteCases}",
                $"ready={readyCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=ExplicitOperatorSignOff (RollbackReady + ApplicationReady together still require operator sign-off; matrix never crosses)"
            }
        };
    }

    private static IReadOnlyList<RollbackReadinessScenario> BuildScenarios() =>
    [
        new(
            "NotApplicableFromBlockedApplication",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationBlocked),
            BuildAllRollbackPrep(),
            RollbackReadinessStatuses.RollbackReadinessNotApplicable,
            ExpectedMissingElement: null),
        new(
            "NotApplicableFromNotApplicableApplication",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationNotApplicable),
            BuildAllRollbackPrep(),
            RollbackReadinessStatuses.RollbackReadinessNotApplicable,
            ExpectedMissingElement: null),
        new(
            "IncompleteMissingSnapshot",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            BuildPrepWithMissing(RollbackElements.PreApplicationSnapshotPresent),
            RollbackReadinessStatuses.RollbackReadinessIncomplete,
            ExpectedMissingElement: RollbackElements.PreApplicationSnapshotPresent),
        new(
            "IncompleteMissingPlaybook",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            BuildPrepWithMissing(RollbackElements.RollbackPlaybookPresent),
            RollbackReadinessStatuses.RollbackReadinessIncomplete,
            ExpectedMissingElement: RollbackElements.RollbackPlaybookPresent),
        new(
            "IncompleteMissingDryRun",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            BuildPrepWithMissing(RollbackElements.RollbackDryRunPassed),
            RollbackReadinessStatuses.RollbackReadinessIncomplete,
            ExpectedMissingElement: RollbackElements.RollbackDryRunPassed),
        new(
            "IncompleteMissingRestorationProof",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            BuildPrepWithMissing(RollbackElements.StateRestorationProvenInTest),
            RollbackReadinessStatuses.RollbackReadinessIncomplete,
            ExpectedMissingElement: RollbackElements.StateRestorationProvenInTest),
        new(
            "IncompleteMissingOperatorAccess",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            BuildPrepWithMissing(RollbackElements.RollbackOperatorAccessPathPresent),
            RollbackReadinessStatuses.RollbackReadinessIncomplete,
            ExpectedMissingElement: RollbackElements.RollbackOperatorAccessPathPresent),
        new(
            "RollbackReadyButNothingActivated",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            BuildAllRollbackPrep(),
            RollbackReadinessStatuses.RollbackReady,
            ExpectedMissingElement: null),
        // 多元素缺失 — 应仍 Incomplete，并显式列出多个 missing。
        new(
            "IncompleteMultipleMissing",
            BuildApplicationDecision(GrantApplicationStatuses.GrantApplicationReady),
            new RollbackPreparedness
            {
                PreApplicationSnapshotPresent = false,
                RollbackPlaybookPresent = false,
                RollbackDryRunPassed = true,
                StateRestorationProvenInTest = true,
                RollbackOperatorAccessPathPresent = true
            },
            RollbackReadinessStatuses.RollbackReadinessIncomplete,
            ExpectedMissingElement: RollbackElements.PreApplicationSnapshotPresent)
    ];

    private static GrantApplicationDecision BuildApplicationDecision(string status) => new()
    {
        Status = status,
        InputPolicyEffect = status == GrantApplicationStatuses.GrantApplicationReady
            ? PolicyAuthorityEffects.Grant
            : PolicyAuthorityEffects.Deny,
        InputPolicyRule = status == GrantApplicationStatuses.GrantApplicationReady
            ? PolicyAuthorityRules.AuthorizedByPolicy
            : PolicyAuthorityRules.FixtureTrustModeCannotAuthorizeProduction,
        RequestedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
        RequestedScope = "demo-workspace/demo-collection",
        PreconditionsMet = Array.Empty<string>(),
        PreconditionsMissing = Array.Empty<string>(),
        Reasoning = $"fixture upstream application decision: status={status}",
        ApplicationApplied = false
    };

    private static RollbackPreparedness BuildAllRollbackPrep() => new()
    {
        PreApplicationSnapshotPresent = true,
        RollbackPlaybookPresent = true,
        RollbackDryRunPassed = true,
        StateRestorationProvenInTest = true,
        RollbackOperatorAccessPathPresent = true
    };

    private static RollbackPreparedness BuildPrepWithMissing(string missingName) => new()
    {
        PreApplicationSnapshotPresent = missingName != RollbackElements.PreApplicationSnapshotPresent,
        RollbackPlaybookPresent = missingName != RollbackElements.RollbackPlaybookPresent,
        RollbackDryRunPassed = missingName != RollbackElements.RollbackDryRunPassed,
        StateRestorationProvenInTest = missingName != RollbackElements.StateRestorationProvenInTest,
        RollbackOperatorAccessPathPresent = missingName != RollbackElements.RollbackOperatorAccessPathPresent
    };

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- RollbackReadinessMatrixPassed: `{r.RollbackReadinessMatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — NotApplicable: `{r.NotApplicableCases}` Incomplete: `{r.IncompleteCases}` Ready: `{r.ReadyCases}`");
        b.AppendLine();
        b.AppendLine("## No-Activation Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- GrantApplied: `{r.GrantApplied}`");
        b.AppendLine($"- ApplicationApplied: `{r.ApplicationApplied}`");
        b.AppendLine($"- RollbackActivated: `{r.RollbackActivated}`  (RollbackReady != Activated)");
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
        b.AppendLine("## Rollback Readiness Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - inputApplicationStatus=`{c.InputApplicationStatus}` capability=`{c.RequestedCapability}` scope=`{c.RequestedScope}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedMissingElement))
            {
                b.AppendLine($"  - expectedMissing=`{c.ExpectedMissingElement}` matched=`{c.MissingElementMatched}`");
            }
            if (c.ActualRollbackElementsMet.Count > 0)
            {
                b.AppendLine($"  - rollbackElementsMet=`{string.Join(", ", c.ActualRollbackElementsMet)}`");
            }
            if (c.ActualRollbackElementsMissing.Count > 0)
            {
                b.AppendLine($"  - rollbackElementsMissing=`{string.Join(", ", c.ActualRollbackElementsMissing)}`");
            }
            b.AppendLine($"  - rollbackNotActivated=`{c.RollbackNotActivated}` applicationNotApplied=`{c.ApplicationNotApplied}` countShapeOk=`{c.CountShapeOk}`");
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

        b.AppendLine("V8.14 rollback readiness matrix。上游 application Ready 才评估；5 个回滚要素（snapshot / playbook / dry-run / restoration / operator-access）任一缺失即 Incomplete；全满足即 RollbackReady。RollbackReady 仍意味着回滚 path 没被执行，应用也没被执行。");
        return b.ToString();
    }
}

public sealed record RollbackReadinessScenario(
    string CaseName,
    GrantApplicationDecision ApplicationDecision,
    RollbackPreparedness Preparedness,
    string ExpectedStatus,
    string? ExpectedMissingElement);

public sealed class FormalRetrievalPromotionApprovalRollbackReadinessCase
{
    public string CaseName { get; init; } = string.Empty;
    public string InputApplicationStatus { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedMissingElement { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualRollbackElementsMet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActualRollbackElementsMissing { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool MissingElementMatched { get; init; }
    public bool CountShapeOk { get; init; }

    /// <summary>每个 case：回滚 path 都未被激活。</summary>
    public bool RollbackNotActivated { get; init; }

    /// <summary>每个 case：上游应用也未被实际应用（carry V8.13）。</summary>
    public bool ApplicationNotApplied { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool RollbackReadinessMatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int NotApplicableCases { get; init; }
    public int IncompleteCases { get; init; }
    public int ReadyCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalRollbackReadinessCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalRollbackReadinessCase>();

    // No-Activation Contract
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool GrantApplied { get; init; }
    public bool ApplicationApplied { get; init; }

    /// <summary>matrix 级核心 invariant — 即便 ReadyCases >= 1，rollback path 也未被激活。</summary>
    public bool RollbackActivated { get; init; }
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

public sealed class FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
