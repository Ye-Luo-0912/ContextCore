using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner
{
    public FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalOperatorSignOffCase>();

        foreach (var scenario in BuildScenarios())
        {
            var decision = FormalRetrievalPromotionApprovalOperatorSignOffPolicy.Evaluate(
                scenario.ApplicationDecision, scenario.RollbackDecision, scenario.Credentials);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var missingMatched = scenario.ExpectedMissingElement is null
                || decision.CredentialElementsMissing.Contains(scenario.ExpectedMissingElement, StringComparer.Ordinal);
            var notCrossed = !decision.Crossed;
            var applicationNotApplied = !decision.ApplicationApplied;
            var rollbackNotActivated = !decision.RollbackActivated;

            var countShapeOk = scenario.ExpectedStatus switch
            {
                _ when scenario.ExpectedStatus == OperatorSignOffStatuses.OperatorSignOffRecorded =>
                    decision.CredentialElementsMissing.Count == 0 && decision.CredentialElementsMet.Count == OperatorSignOffElements.AllInOrder.Count,
                _ when scenario.ExpectedStatus == OperatorSignOffStatuses.OperatorSignOffInsufficient =>
                    decision.CredentialElementsMissing.Count >= 1,
                _ when scenario.ExpectedStatus == OperatorSignOffStatuses.OperatorSignOffNotApplicable =>
                    decision.CredentialElementsMissing.Count == 0 && decision.CredentialElementsMet.Count == 0,
                _ => false
            };

            var passedAsExpected = statusMatched
                && missingMatched
                && notCrossed
                && applicationNotApplied
                && rollbackNotActivated
                && countShapeOk;

            cases.Add(new FormalRetrievalPromotionApprovalOperatorSignOffCase
            {
                CaseName = scenario.CaseName,
                InputApplicationStatus = scenario.ApplicationDecision.Status,
                InputRollbackStatus = scenario.RollbackDecision.Status,
                RequestedCapability = scenario.ApplicationDecision.RequestedCapability,
                RequestedScope = scenario.ApplicationDecision.RequestedScope,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedMissingElement = scenario.ExpectedMissingElement ?? string.Empty,
                ActualCredentialElementsMet = decision.CredentialElementsMet,
                ActualCredentialElementsMissing = decision.CredentialElementsMissing,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                MissingElementMatched = missingMatched,
                CountShapeOk = countShapeOk,
                NotCrossed = notCrossed,
                ApplicationNotApplied = applicationNotApplied,
                RollbackNotActivated = rollbackNotActivated,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var notApplicableCases = cases.Count(static c => c.ActualStatus == OperatorSignOffStatuses.OperatorSignOffNotApplicable);
        var insufficientCases = cases.Count(static c => c.ActualStatus == OperatorSignOffStatuses.OperatorSignOffInsufficient);
        var recordedCases = cases.Count(static c => c.ActualStatus == OperatorSignOffStatuses.OperatorSignOffRecorded);

        var blocked = new List<string>();
        if (cases.Count < 9)
        {
            blocked.Add("InsufficientOperatorSignOffCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("OperatorSignOffMatrixFailed");
        }

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[]
                 {
                     OperatorSignOffStatuses.OperatorSignOffNotApplicable,
                     OperatorSignOffStatuses.OperatorSignOffInsufficient,
                     OperatorSignOffStatuses.OperatorSignOffRecorded
                 })
        {
            if (!statusesCovered.Contains(s))
            {
                blocked.Add($"StatusBranchNotCovered:{s}");
            }
        }

        var individuallyMissed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (c.ActualCredentialElementsMissing.Count == 1)
            {
                individuallyMissed.Add(c.ActualCredentialElementsMissing[0]);
            }
        }

        foreach (var e in OperatorSignOffElements.AllInOrder)
        {
            if (!individuallyMissed.Contains(e))
            {
                blocked.Add($"SignOffElementNotIsolatedlyTested:{e}");
            }
        }

        // 三重不变量 — Crossed / ApplicationApplied / RollbackActivated 必须全 false。
        if (cases.Any(c => !c.NotCrossed))
        {
            blocked.Add("ApplicationBoundaryWasCrossed");
        }

        if (cases.Any(c => !c.ApplicationNotApplied))
        {
            blocked.Add("ApplicationWasApplied");
        }

        if (cases.Any(c => !c.RollbackNotActivated))
        {
            blocked.Add("RollbackPathWasActivated");
        }

        // NotApplicable 触发源覆盖 — 至少有一个 case 来自 application 未就绪、一个来自 rollback 未就绪。
        var naFromAppNotReady = cases.Any(c =>
            c.ActualStatus == OperatorSignOffStatuses.OperatorSignOffNotApplicable
            && c.InputApplicationStatus != GrantApplicationStatuses.GrantApplicationReady);
        var naFromRollbackNotReady = cases.Any(c =>
            c.ActualStatus == OperatorSignOffStatuses.OperatorSignOffNotApplicable
            && c.InputApplicationStatus == GrantApplicationStatuses.GrantApplicationReady
            && c.InputRollbackStatus != RollbackReadinessStatuses.RollbackReady);

        if (!naFromAppNotReady)
        {
            blocked.Add("NotApplicableFromApplicationNotReadyMissing");
        }

        if (!naFromRollbackNotReady)
        {
            blocked.Add("NotApplicableFromRollbackNotReadyMissing");
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

        return new FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport
        {
            OperationId = $"frp-operator-sign-off-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            OperatorSignOffMatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            NotApplicableCases = notApplicableCases,
            InsufficientCases = insufficientCases,
            RecordedCases = recordedCases,
            Cases = cases,
            // V8.15 显式契约：sign-off 凭据结构验证；Recorded 不跨过应用边界。
            ManualReviewRequired = false,
            ApprovalSealed = false,
            CapabilityGrantWritten = false,
            GrantApplied = false,
            ApplicationApplied = false,
            RollbackActivated = false,
            Crossed = false,
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
                $"insufficient={insufficientCases}",
                $"recorded={recordedCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=PostCrossingObservation (only meaningful after a real Crossed=true event; matrix never produces one)"
            }
        };
    }

    private static IReadOnlyList<OperatorSignOffScenario> BuildScenarios() =>
    [
        new(
            "NotApplicableFromApplicationNotReady",
            BuildApplication(GrantApplicationStatuses.GrantApplicationBlocked),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildAllCredentials(),
            OperatorSignOffStatuses.OperatorSignOffNotApplicable,
            ExpectedMissingElement: null),
        new(
            "NotApplicableFromRollbackNotReady",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReadinessIncomplete),
            BuildAllCredentials(),
            OperatorSignOffStatuses.OperatorSignOffNotApplicable,
            ExpectedMissingElement: null),
        new(
            "InsufficientMissingIdentity",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildCredentialsWithMissing(OperatorSignOffElements.OperatorIdentityPresent),
            OperatorSignOffStatuses.OperatorSignOffInsufficient,
            ExpectedMissingElement: OperatorSignOffElements.OperatorIdentityPresent),
        new(
            "InsufficientMissingAuthority",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildCredentialsWithMissing(OperatorSignOffElements.OperatorAuthorityProofPresent),
            OperatorSignOffStatuses.OperatorSignOffInsufficient,
            ExpectedMissingElement: OperatorSignOffElements.OperatorAuthorityProofPresent),
        new(
            "InsufficientMissingIntent",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildCredentialsWithMissing(OperatorSignOffElements.SignOffIntentAffirmative),
            OperatorSignOffStatuses.OperatorSignOffInsufficient,
            ExpectedMissingElement: OperatorSignOffElements.SignOffIntentAffirmative),
        new(
            "InsufficientMissingTimestamp",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildCredentialsWithMissing(OperatorSignOffElements.SignOffTimestampWithinValidityWindow),
            OperatorSignOffStatuses.OperatorSignOffInsufficient,
            ExpectedMissingElement: OperatorSignOffElements.SignOffTimestampWithinValidityWindow),
        new(
            "InsufficientMissingSeal",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildCredentialsWithMissing(OperatorSignOffElements.SignOffCryptographicSealValid),
            OperatorSignOffStatuses.OperatorSignOffInsufficient,
            ExpectedMissingElement: OperatorSignOffElements.SignOffCryptographicSealValid),
        new(
            "RecordedButNoCrossover",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            BuildAllCredentials(),
            OperatorSignOffStatuses.OperatorSignOffRecorded,
            ExpectedMissingElement: null),
        new(
            "InsufficientMultipleMissing",
            BuildApplication(GrantApplicationStatuses.GrantApplicationReady),
            BuildRollback(RollbackReadinessStatuses.RollbackReady),
            new OperatorSignOffCredentials
            {
                OperatorIdentityPresent = false,
                OperatorAuthorityProofPresent = false,
                SignOffIntentAffirmative = true,
                SignOffTimestampWithinValidityWindow = true,
                SignOffCryptographicSealValid = true
            },
            OperatorSignOffStatuses.OperatorSignOffInsufficient,
            ExpectedMissingElement: OperatorSignOffElements.OperatorIdentityPresent)
    ];

    private static GrantApplicationDecision BuildApplication(string status) => new()
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
        Reasoning = $"fixture application decision: status={status}",
        ApplicationApplied = false
    };

    private static RollbackReadinessDecision BuildRollback(string status) => new()
    {
        Status = status,
        InputApplicationStatus = GrantApplicationStatuses.GrantApplicationReady,
        RequestedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
        RequestedScope = "demo-workspace/demo-collection",
        RollbackElementsMet = Array.Empty<string>(),
        RollbackElementsMissing = Array.Empty<string>(),
        Reasoning = $"fixture rollback decision: status={status}",
        RollbackActivated = false,
        ApplicationApplied = false
    };

    private static OperatorSignOffCredentials BuildAllCredentials() => new()
    {
        OperatorIdentityPresent = true,
        OperatorAuthorityProofPresent = true,
        SignOffIntentAffirmative = true,
        SignOffTimestampWithinValidityWindow = true,
        SignOffCryptographicSealValid = true
    };

    private static OperatorSignOffCredentials BuildCredentialsWithMissing(string missingName) => new()
    {
        OperatorIdentityPresent = missingName != OperatorSignOffElements.OperatorIdentityPresent,
        OperatorAuthorityProofPresent = missingName != OperatorSignOffElements.OperatorAuthorityProofPresent,
        SignOffIntentAffirmative = missingName != OperatorSignOffElements.SignOffIntentAffirmative,
        SignOffTimestampWithinValidityWindow = missingName != OperatorSignOffElements.SignOffTimestampWithinValidityWindow,
        SignOffCryptographicSealValid = missingName != OperatorSignOffElements.SignOffCryptographicSealValid
    };

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- OperatorSignOffMatrixPassed: `{r.OperatorSignOffMatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — NotApplicable: `{r.NotApplicableCases}` Insufficient: `{r.InsufficientCases}` Recorded: `{r.RecordedCases}`");
        b.AppendLine();
        b.AppendLine("## No-Crossover Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- GrantApplied: `{r.GrantApplied}`");
        b.AppendLine($"- ApplicationApplied: `{r.ApplicationApplied}`");
        b.AppendLine($"- RollbackActivated: `{r.RollbackActivated}`");
        b.AppendLine($"- Crossed: `{r.Crossed}`  (Recorded != Crossed — sign-off being recorded does not cross the application boundary)");
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
        b.AppendLine("## Operator Sign-Off Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - inputs: application=`{c.InputApplicationStatus}` rollback=`{c.InputRollbackStatus}` capability=`{c.RequestedCapability}` scope=`{c.RequestedScope}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedMissingElement))
            {
                b.AppendLine($"  - expectedMissing=`{c.ExpectedMissingElement}` matched=`{c.MissingElementMatched}`");
            }
            if (c.ActualCredentialElementsMet.Count > 0)
            {
                b.AppendLine($"  - elementsMet=`{string.Join(", ", c.ActualCredentialElementsMet)}`");
            }
            if (c.ActualCredentialElementsMissing.Count > 0)
            {
                b.AppendLine($"  - elementsMissing=`{string.Join(", ", c.ActualCredentialElementsMissing)}`");
            }
            b.AppendLine($"  - notCrossed=`{c.NotCrossed}` applicationNotApplied=`{c.ApplicationNotApplied}` rollbackNotActivated=`{c.RollbackNotActivated}` countShapeOk=`{c.CountShapeOk}`");
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

        b.AppendLine("V8.15 explicit operator sign-off matrix。上游必须 ApplicationReady && RollbackReady；5 个凭据结构要素（identity / authority / intent / timestamp / seal）任一缺失即 Insufficient；全满足即 Recorded。Recorded ≠ Crossed — sign-off 被记录不代表应用边界被跨过；矩阵不触发应用、不激活回滚、不跨过任何边界。");
        return b.ToString();
    }
}

public sealed record OperatorSignOffScenario(
    string CaseName,
    GrantApplicationDecision ApplicationDecision,
    RollbackReadinessDecision RollbackDecision,
    OperatorSignOffCredentials Credentials,
    string ExpectedStatus,
    string? ExpectedMissingElement);

public sealed class FormalRetrievalPromotionApprovalOperatorSignOffCase
{
    public string CaseName { get; init; } = string.Empty;
    public string InputApplicationStatus { get; init; } = string.Empty;
    public string InputRollbackStatus { get; init; } = string.Empty;
    public string RequestedCapability { get; init; } = string.Empty;
    public string RequestedScope { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedMissingElement { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualCredentialElementsMet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActualCredentialElementsMissing { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool MissingElementMatched { get; init; }
    public bool CountShapeOk { get; init; }

    /// <summary>每个 case：应用边界未被跨过。</summary>
    public bool NotCrossed { get; init; }

    /// <summary>每个 case：carry V8.13 — 应用未被实际应用。</summary>
    public bool ApplicationNotApplied { get; init; }

    /// <summary>每个 case：carry V8.14 — 回滚路径未被激活。</summary>
    public bool RollbackNotActivated { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool OperatorSignOffMatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int NotApplicableCases { get; init; }
    public int InsufficientCases { get; init; }
    public int RecordedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalOperatorSignOffCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalOperatorSignOffCase>();

    // No-Crossover Contract
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool GrantApplied { get; init; }
    public bool ApplicationApplied { get; init; }
    public bool RollbackActivated { get; init; }

    /// <summary>matrix 级 — 即便 RecordedCases >= 1，应用边界也从未被跨过。</summary>
    public bool Crossed { get; init; }
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

public sealed class FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
