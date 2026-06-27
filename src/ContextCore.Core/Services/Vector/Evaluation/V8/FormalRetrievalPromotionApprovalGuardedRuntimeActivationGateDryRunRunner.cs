using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunRunner
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    public FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport Run(
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? loadedActivationDryRunReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase>();

        foreach (var scenario in BuildScenarios())
        {
            var decision = FormalRetrievalPromotionApprovalGuardedRuntimeActivationGatePolicy.Evaluate(
                scenario.ActivationDryRunReport,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var writeNotAllowed = !decision.RuntimeActivationWriteAllowed;
            var runtimeNotActivated = !decision.RuntimeActivation;
            var formalRetrievalNotAllowed = !decision.FormalRetrievalAllowed;
            var runtimeSwitchNotAllowed = !decision.RuntimeSwitchAllowed;
            var packageOutputUnchanged = !decision.PackageOutputChanged;
            var dryRunOnly = decision.DryRunOnly;

            var passedAsExpected = statusMatched && blockedReasonMatched
                && writeNotAllowed && runtimeNotActivated
                && formalRetrievalNotAllowed && runtimeSwitchNotAllowed
                && packageOutputUnchanged && dryRunOnly;

            cases.Add(new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundGrantId = decision.BoundGrantId,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                PlannedGuardedActivationContract = decision.PlannedGuardedActivationContract,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                DryRunOnly = decision.DryRunOnly,
                RuntimeActivationWriteAllowed = decision.RuntimeActivationWriteAllowed,
                RuntimeActivation = decision.RuntimeActivation,
                FormalRetrievalAllowed = decision.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = decision.RuntimeSwitchAllowed,
                PackageOutputChanged = decision.PackageOutputChanged,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var readyCases = cases.Count(static c => c.ActualStatus == GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady);
        var blockedCases = cases.Count(static c => c.ActualStatus == GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked);

        var matrixBlocked = new List<string>();
        if (cases.Count < 30) matrixBlocked.Add("InsufficientGuardedActivationDryRunCases");
        if (failedCases > 0) matrixBlocked.Add("GuardedActivationDryRunMatrixFailed");

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[] { GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady, GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked })
        {
            if (!statusesCovered.Contains(s)) matrixBlocked.Add($"StatusBranchNotCovered:{s}");
        }

        if (cases.Any(c => c.RuntimeActivationWriteAllowed)) matrixBlocked.Add("RuntimeActivationWriteAllowedLeaked");
        if (cases.Any(c => c.RuntimeActivation)) matrixBlocked.Add("RuntimeActivationLeaked");
        if (cases.Any(c => c.FormalRetrievalAllowed)) matrixBlocked.Add("FormalRetrievalAllowedLeaked");
        if (cases.Any(c => c.RuntimeSwitchAllowed)) matrixBlocked.Add("RuntimeSwitchAllowedLeaked");
        if (cases.Any(c => c.PackageOutputChanged)) matrixBlocked.Add("PackageOutputChangedLeaked");
        if (cases.Any(c => !c.DryRunOnly)) matrixBlocked.Add("DryRunOnlyViolated");

        // ===== real run =====
        var realUpstreamPresent = loadedActivationDryRunReport is not null;
        var realUpstreamPassed = loadedActivationDryRunReport?.GatePassed ?? false;
        var realDecision = FormalRetrievalPromotionApprovalGuardedRuntimeActivationGatePolicy.Evaluate(
            loadedActivationDryRunReport, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);

        if (!realUpstreamPresent) matrixBlocked.Add("RealActivationDryRunGateArtifactMissing");
        if (realUpstreamPresent && !realUpstreamPassed) matrixBlocked.Add("RealActivationDryRunGateNotPassed");
        if (realDecision.Status != GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady)
        {
            foreach (var r in realDecision.BlockedReasons)
            {
                matrixBlocked.Add($"RealGuardedActivationDryRun:{r}");
            }
        }

        if (mainlineEvidencePresent) matrixBlocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) matrixBlocked.Add("MainlineTrustRegistryPresent");
        if (!rtPassed) matrixBlocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) matrixBlocked.Add("P15GateNotPassed");

        var distinctBlocked = matrixBlocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var dryRunPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && dryRunPassed;

        return new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport
        {
            OperationId = $"frp-guarded-runtime-activation-dry-run-{Guid.NewGuid():N}",
            CreatedAt = now,
            GuardedRuntimeActivationDryRunPassed = dryRunPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            ReadyCases = readyCases,
            BlockedCases = blockedCases,
            Cases = cases,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            PlannedGuardedActivationContract = realDecision.PlannedGuardedActivationContract,
            UpstreamActivationDryRunGatePresent = realUpstreamPresent,
            UpstreamActivationDryRunGatePassed = realUpstreamPassed,
            DryRunOnly = true,
            RuntimeActivationWriteAllowed = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            // carry — Crossed=true 在 V8.18 后是事实
            Crossed = loadedActivationDryRunReport?.Crossed ?? false,
            ArtifactOnly = true,
            CapabilityGrantWritten = loadedActivationDryRunReport?.CapabilityGrantWritten ?? false,
            ConfigPatchWritten = loadedActivationDryRunReport?.ConfigPatchWritten ?? false,
            RollbackSnapshotWritten = loadedActivationDryRunReport?.RollbackSnapshotWritten ?? false,
            AuditLogWritten = loadedActivationDryRunReport?.AuditLogWritten ?? false,
            RevocationRecordWritten = loadedActivationDryRunReport?.RevocationRecordWritten ?? false,
            ActivationDryRunOnly = loadedActivationDryRunReport?.ActivationDryRunOnly ?? true,
            RuntimeActivationAllowed = loadedActivationDryRunReport?.RuntimeActivationAllowed ?? false,
            PackingPolicyChanged = false,
            EvidenceCopiedToMainline = false,
            TrustRegistryCopiedToMainline = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            ManualReviewRequired = false,
            ApprovalSealed = false,
            GrantApplied = false,
            ApplicationApplied = false,
            RollbackActivated = false,
            PromotionToMainlinePerformed = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Count}",
                $"passed={passedCases}",
                $"failed={failedCases}",
                $"ready={readyCases}",
                $"blocked={blockedCases}",
                $"realUpstreamPresent={realUpstreamPresent}",
                $"realUpstreamPassed={realUpstreamPassed}",
                $"realDecisionStatus={realDecision.Status}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                "nextStage=GuardedRuntimeActivationGateExecution (the only path that may write runtime-activation artifacts; this matrix never does)"
            }
        };
    }

    private static IReadOnlyList<GuardedRuntimeActivationGateDryRunScenario> BuildScenarios()
    {
        var cleanV819R = BuildCleanActivationDryRunReport();
        return
        [
            new("AllUpstreamClean", cleanV819R,
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady, null),
            new("ActivationDryRunGateMissing", null,
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.ActivationDryRunGateMissing),
            new("ActivationDryRunGateNotPassed", Mutate(cleanV819R, gatePassed: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.ActivationDryRunGateNotPassed),
            new("NoRuntimeActivationDryRunReadyCase", MutateWithoutReadyCase(cleanV819R),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.NoRuntimeActivationDryRunReadyCase),
            new("BoundGrantIdEmpty", Mutate(cleanV819R, boundGrantId: string.Empty),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.BoundGrantIdEmpty),
            new("BoundCapabilityMismatch", Mutate(cleanV819R, boundCapability: "OtherCapability"),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.BoundCapabilityMismatch),
            new("BoundScopeMismatch", Mutate(cleanV819R, boundScope: "other-workspace/other-collection"),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.BoundScopeMismatch),
            new("ActivationDryRunOnlyFalseInUpstream", Mutate(cleanV819R, activationDryRunOnly: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.ActivationDryRunOnlyFalseInUpstream),
            new("RuntimeActivationAllowedTrueInUpstream", Mutate(cleanV819R, runtimeActivationAllowed: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.RuntimeActivationAllowedTrueInUpstream),
            new("RuntimeActivationTrueInUpstream", Mutate(cleanV819R, runtimeActivation: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.RuntimeActivationTrueInUpstream),
            new("FormalRetrievalAllowedTrueInUpstream", Mutate(cleanV819R, formalRetrievalAllowed: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream),
            new("RuntimeSwitchAllowedTrueInUpstream", Mutate(cleanV819R, runtimeSwitchAllowed: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream),
            new("ConfigPatchAppliedToRuntimeTrueInUpstream", Mutate(cleanV819R, configPatchAppliedToRuntime: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.ConfigPatchAppliedToRuntimeTrueInUpstream),
            new("CrossedFalseInUpstream", Mutate(cleanV819R, crossed: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.CrossedFalseInUpstream),
            new("ArtifactOnlyFalseInUpstream", Mutate(cleanV819R, artifactOnly: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.ArtifactOnlyFalseInUpstream),
            new("CapabilityGrantWrittenFalseInUpstream", Mutate(cleanV819R, capabilityGrantWritten: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.CapabilityGrantWrittenFalseInUpstream),
            new("ConfigPatchWrittenFalseInUpstream", Mutate(cleanV819R, configPatchWritten: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.ConfigPatchWrittenFalseInUpstream),
            new("RollbackSnapshotWrittenFalseInUpstream", Mutate(cleanV819R, rollbackSnapshotWritten: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.RollbackSnapshotWrittenFalseInUpstream),
            new("AuditLogWrittenFalseInUpstream", Mutate(cleanV819R, auditLogWritten: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.AuditLogWrittenFalseInUpstream),
            new("RevocationRecordWrittenFalseInUpstream", Mutate(cleanV819R, revocationRecordWritten: false),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.RevocationRecordWrittenFalseInUpstream),
            new("PlannedActivationModeNotGuardedScopeOnly",
                MutateContract(cleanV819R, plannedRuntimeActivationMode: "FullActivation"),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.PlannedActivationModeNotGuardedScopeOnly),
            new("PlannedRuntimeSwitchPathOutsideAllowedDirectory",
                MutateContract(cleanV819R, plannedRuntimeSwitchPath: "vector/v8/elsewhere/runtime-switch.json"),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.PlannedRuntimeSwitchPathOutsideAllowedDirectory),
            new("PlannedActivationAuditPathOutsideAllowedDirectory",
                MutateContract(cleanV819R, plannedActivationAuditPath: "vector/v8/elsewhere/audit.jsonl"),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.PlannedActivationAuditPathOutsideAllowedDirectory),
            new("PlannedRollbackReferenceMissing",
                MutateContract(cleanV819R, plannedRollbackReference: string.Empty),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.PlannedRollbackReferenceMissing),
            new("PlannedRevocationReferenceMissing",
                MutateContract(cleanV819R, plannedRevocationReference: string.Empty),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.PlannedRevocationReferenceMissing),
            new("RuntimeGateNotPassed", cleanV819R,
                false, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanV819R,
                true, false, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", cleanV819R,
                true, true, true, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanV819R,
                true, true, false, true,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.MainlineTrustRegistryPresent),
            new("PackageOutputChanged", Mutate(cleanV819R, packageOutputChanged: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.PackageOutputChanged),
            new("FormalPackageWritten", Mutate(cleanV819R, formalPackageWritten: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.FormalPackageWritten),
            new("VectorStoreBindingChanged", Mutate(cleanV819R, vectorStoreBindingChanged: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.VectorStoreBindingChanged),
            new("GlobalDefaultOn", Mutate(cleanV819R, globalDefaultOn: true),
                true, true, false, false,
                GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                GuardedRuntimeActivationDryRunBlockedReasons.GlobalDefaultOn)
        ];
    }

    public static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport BuildCleanActivationDryRunReport() => new()
    {
        OperationId = "frp-runtime-activation-dry-run-fixture",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        RuntimeActivationDryRunPassed = true,
        GatePassed = true,
        TotalCases = 54,
        PassedCases = 54,
        FailedCases = 0,
        ReadyCases = 1,
        BlockedCases = 53,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase
            {
                CaseName = "ReadySynthetic",
                ExpectedStatus = RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady,
                ActualStatus = RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady,
                BoundGrantId = TestGrantId,
                BoundCapability = TestCapability,
                BoundScope = TestScope,
                ActivationDryRunOnly = true,
                RuntimeActivationAllowed = false,
                RuntimeActivation = false,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                ConfigPatchAppliedToRuntime = false,
                StatusMatched = true,
                BlockedReasonMatched = true,
                PassedAsExpected = true
            }
        },
        BoundGrantId = TestGrantId,
        BoundCapability = TestCapability,
        BoundScope = TestScope,
        PlannedActivationContract = new RuntimeActivationPlannedContract
        {
            PlannedRuntimeActivationMode = "GuardedScopeOnly",
            PlannedCapability = TestCapability,
            PlannedScope = TestScope,
            PlannedConfigPatchSourcePath = "vector/v8/dedicated-crossing/runtime-config-patch-fixture.json",
            PlannedRuntimeSwitchPath = "vector/v8/runtime-activation/runtime-switch-fixture.json",
            PlannedActivationAuditPath = "vector/v8/runtime-activation/activation-audit-fixture.jsonl",
            PlannedRollbackReference = "vector/v8/dedicated-crossing/rollback-snapshot-fixture.json",
            PlannedRevocationReference = "vector/v8/dedicated-crossing/revocation-record-fixture.json"
        },
        UpstreamExecutionGatePresent = true,
        UpstreamExecutionGatePassed = true,
        ActivationDryRunOnly = true,
        RuntimeActivationAllowed = false,
        RuntimeActivation = false,
        FormalRetrievalAllowed = false,
        RuntimeSwitchAllowed = false,
        ConfigPatchAppliedToRuntime = false,
        Crossed = true,
        ArtifactOnly = true,
        CapabilityGrantWritten = true,
        ConfigPatchWritten = true,
        RollbackSnapshotWritten = true,
        AuditLogWritten = true,
        RevocationRecordWritten = true,
        FormalPackageWritten = false,
        PackageOutputChanged = false,
        PackingPolicyChanged = false,
        VectorStoreBindingChanged = false,
        GlobalDefaultOn = false,
        EvidenceCopiedToMainline = false,
        TrustRegistryCopiedToMainline = false,
        MainlineEvidencePresent = false,
        MainlineTrustRegistryPresent = false,
        NoRuntimeMutationInvariant = true
    };

    private static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport Mutate(
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport b,
        bool? gatePassed = null,
        string? boundGrantId = null,
        string? boundCapability = null,
        string? boundScope = null,
        bool? activationDryRunOnly = null,
        bool? runtimeActivationAllowed = null,
        bool? runtimeActivation = null,
        bool? formalRetrievalAllowed = null,
        bool? runtimeSwitchAllowed = null,
        bool? configPatchAppliedToRuntime = null,
        bool? crossed = null,
        bool? artifactOnly = null,
        bool? capabilityGrantWritten = null,
        bool? configPatchWritten = null,
        bool? rollbackSnapshotWritten = null,
        bool? auditLogWritten = null,
        bool? revocationRecordWritten = null,
        bool? packageOutputChanged = null,
        bool? formalPackageWritten = null,
        bool? vectorStoreBindingChanged = null,
        bool? globalDefaultOn = null) => new()
    {
        OperationId = b.OperationId,
        CreatedAt = b.CreatedAt,
        RuntimeActivationDryRunPassed = gatePassed ?? b.RuntimeActivationDryRunPassed,
        GatePassed = gatePassed ?? b.GatePassed,
        TotalCases = b.TotalCases,
        PassedCases = b.PassedCases,
        FailedCases = b.FailedCases,
        ReadyCases = b.ReadyCases,
        BlockedCases = b.BlockedCases,
        Cases = b.Cases,
        BoundGrantId = boundGrantId ?? b.BoundGrantId,
        BoundCapability = boundCapability ?? b.BoundCapability,
        BoundScope = boundScope ?? b.BoundScope,
        PlannedActivationContract = b.PlannedActivationContract,
        UpstreamExecutionGatePresent = b.UpstreamExecutionGatePresent,
        UpstreamExecutionGatePassed = b.UpstreamExecutionGatePassed,
        ActivationDryRunOnly = activationDryRunOnly ?? b.ActivationDryRunOnly,
        RuntimeActivationAllowed = runtimeActivationAllowed ?? b.RuntimeActivationAllowed,
        RuntimeActivation = runtimeActivation ?? b.RuntimeActivation,
        FormalRetrievalAllowed = formalRetrievalAllowed ?? b.FormalRetrievalAllowed,
        RuntimeSwitchAllowed = runtimeSwitchAllowed ?? b.RuntimeSwitchAllowed,
        ConfigPatchAppliedToRuntime = configPatchAppliedToRuntime ?? b.ConfigPatchAppliedToRuntime,
        Crossed = crossed ?? b.Crossed,
        ArtifactOnly = artifactOnly ?? b.ArtifactOnly,
        CapabilityGrantWritten = capabilityGrantWritten ?? b.CapabilityGrantWritten,
        ConfigPatchWritten = configPatchWritten ?? b.ConfigPatchWritten,
        RollbackSnapshotWritten = rollbackSnapshotWritten ?? b.RollbackSnapshotWritten,
        AuditLogWritten = auditLogWritten ?? b.AuditLogWritten,
        RevocationRecordWritten = revocationRecordWritten ?? b.RevocationRecordWritten,
        FormalPackageWritten = formalPackageWritten ?? b.FormalPackageWritten,
        PackageOutputChanged = packageOutputChanged ?? b.PackageOutputChanged,
        PackingPolicyChanged = b.PackingPolicyChanged,
        VectorStoreBindingChanged = vectorStoreBindingChanged ?? b.VectorStoreBindingChanged,
        GlobalDefaultOn = globalDefaultOn ?? b.GlobalDefaultOn,
        EvidenceCopiedToMainline = b.EvidenceCopiedToMainline,
        TrustRegistryCopiedToMainline = b.TrustRegistryCopiedToMainline,
        MainlineEvidencePresent = b.MainlineEvidencePresent,
        MainlineTrustRegistryPresent = b.MainlineTrustRegistryPresent,
        NoRuntimeMutationInvariant = b.NoRuntimeMutationInvariant
    };

    private static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport MutateWithoutReadyCase(
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport b)
    {
        var clone = Mutate(b);
        return new FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport
        {
            OperationId = clone.OperationId,
            CreatedAt = clone.CreatedAt,
            RuntimeActivationDryRunPassed = clone.RuntimeActivationDryRunPassed,
            GatePassed = clone.GatePassed,
            TotalCases = clone.TotalCases,
            PassedCases = clone.PassedCases,
            FailedCases = clone.FailedCases,
            ReadyCases = 0,
            BlockedCases = clone.BlockedCases,
            Cases = new[]
            {
                new FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase
                {
                    CaseName = "BlockedOnly",
                    ExpectedStatus = RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                    ActualStatus = RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                    BoundGrantId = clone.BoundGrantId,
                    BoundCapability = clone.BoundCapability,
                    BoundScope = clone.BoundScope,
                    ActivationDryRunOnly = true,
                    RuntimeActivationAllowed = false,
                    RuntimeActivation = false,
                    FormalRetrievalAllowed = false,
                    RuntimeSwitchAllowed = false,
                    ConfigPatchAppliedToRuntime = false,
                    PassedAsExpected = true
                }
            },
            BoundGrantId = clone.BoundGrantId,
            BoundCapability = clone.BoundCapability,
            BoundScope = clone.BoundScope,
            PlannedActivationContract = clone.PlannedActivationContract,
            UpstreamExecutionGatePresent = clone.UpstreamExecutionGatePresent,
            UpstreamExecutionGatePassed = clone.UpstreamExecutionGatePassed,
            ActivationDryRunOnly = clone.ActivationDryRunOnly,
            RuntimeActivationAllowed = clone.RuntimeActivationAllowed,
            RuntimeActivation = clone.RuntimeActivation,
            FormalRetrievalAllowed = clone.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = clone.RuntimeSwitchAllowed,
            ConfigPatchAppliedToRuntime = clone.ConfigPatchAppliedToRuntime,
            Crossed = clone.Crossed,
            ArtifactOnly = clone.ArtifactOnly,
            CapabilityGrantWritten = clone.CapabilityGrantWritten,
            ConfigPatchWritten = clone.ConfigPatchWritten,
            RollbackSnapshotWritten = clone.RollbackSnapshotWritten,
            AuditLogWritten = clone.AuditLogWritten,
            RevocationRecordWritten = clone.RevocationRecordWritten,
            FormalPackageWritten = clone.FormalPackageWritten,
            PackageOutputChanged = clone.PackageOutputChanged,
            PackingPolicyChanged = clone.PackingPolicyChanged,
            VectorStoreBindingChanged = clone.VectorStoreBindingChanged,
            GlobalDefaultOn = clone.GlobalDefaultOn,
            EvidenceCopiedToMainline = clone.EvidenceCopiedToMainline,
            TrustRegistryCopiedToMainline = clone.TrustRegistryCopiedToMainline,
            MainlineEvidencePresent = clone.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = clone.MainlineTrustRegistryPresent,
            NoRuntimeMutationInvariant = clone.NoRuntimeMutationInvariant
        };
    }

    private static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport MutateContract(
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport b,
        string? plannedRuntimeActivationMode = null,
        string? plannedRuntimeSwitchPath = null,
        string? plannedActivationAuditPath = null,
        string? plannedRollbackReference = null,
        string? plannedRevocationReference = null)
    {
        var c = b.PlannedActivationContract;
        var newContract = new RuntimeActivationPlannedContract
        {
            PlannedRuntimeActivationMode = plannedRuntimeActivationMode ?? c.PlannedRuntimeActivationMode,
            PlannedCapability = c.PlannedCapability,
            PlannedScope = c.PlannedScope,
            PlannedConfigPatchSourcePath = c.PlannedConfigPatchSourcePath,
            PlannedRuntimeSwitchPath = plannedRuntimeSwitchPath ?? c.PlannedRuntimeSwitchPath,
            PlannedActivationAuditPath = plannedActivationAuditPath ?? c.PlannedActivationAuditPath,
            PlannedRollbackReference = plannedRollbackReference ?? c.PlannedRollbackReference,
            PlannedRevocationReference = plannedRevocationReference ?? c.PlannedRevocationReference
        };

        var clone = Mutate(b);
        return new FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport
        {
            OperationId = clone.OperationId,
            CreatedAt = clone.CreatedAt,
            RuntimeActivationDryRunPassed = clone.RuntimeActivationDryRunPassed,
            GatePassed = clone.GatePassed,
            TotalCases = clone.TotalCases,
            PassedCases = clone.PassedCases,
            FailedCases = clone.FailedCases,
            ReadyCases = clone.ReadyCases,
            BlockedCases = clone.BlockedCases,
            Cases = clone.Cases,
            BoundGrantId = clone.BoundGrantId,
            BoundCapability = clone.BoundCapability,
            BoundScope = clone.BoundScope,
            PlannedActivationContract = newContract,
            UpstreamExecutionGatePresent = clone.UpstreamExecutionGatePresent,
            UpstreamExecutionGatePassed = clone.UpstreamExecutionGatePassed,
            ActivationDryRunOnly = clone.ActivationDryRunOnly,
            RuntimeActivationAllowed = clone.RuntimeActivationAllowed,
            RuntimeActivation = clone.RuntimeActivation,
            FormalRetrievalAllowed = clone.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = clone.RuntimeSwitchAllowed,
            ConfigPatchAppliedToRuntime = clone.ConfigPatchAppliedToRuntime,
            Crossed = clone.Crossed,
            ArtifactOnly = clone.ArtifactOnly,
            CapabilityGrantWritten = clone.CapabilityGrantWritten,
            ConfigPatchWritten = clone.ConfigPatchWritten,
            RollbackSnapshotWritten = clone.RollbackSnapshotWritten,
            AuditLogWritten = clone.AuditLogWritten,
            RevocationRecordWritten = clone.RevocationRecordWritten,
            FormalPackageWritten = clone.FormalPackageWritten,
            PackageOutputChanged = clone.PackageOutputChanged,
            PackingPolicyChanged = clone.PackingPolicyChanged,
            VectorStoreBindingChanged = clone.VectorStoreBindingChanged,
            GlobalDefaultOn = clone.GlobalDefaultOn,
            EvidenceCopiedToMainline = clone.EvidenceCopiedToMainline,
            TrustRegistryCopiedToMainline = clone.TrustRegistryCopiedToMainline,
            MainlineEvidencePresent = clone.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = clone.MainlineTrustRegistryPresent,
            NoRuntimeMutationInvariant = clone.NoRuntimeMutationInvariant
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- GuardedRuntimeActivationDryRunPassed: `{r.GuardedRuntimeActivationDryRunPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — Ready: `{r.ReadyCases}` Blocked: `{r.BlockedCases}`");
        b.AppendLine();
        b.AppendLine("## Bound (Real)");
        b.AppendLine($"- BoundGrantId: `{r.BoundGrantId}`");
        b.AppendLine($"- BoundCapability: `{r.BoundCapability}`");
        b.AppendLine($"- BoundScope: `{r.BoundScope}`");
        b.AppendLine();
        b.AppendLine("## Planned Guarded Activation Write Contract");
        var c = r.PlannedGuardedActivationContract;
        b.AppendLine($"- PlannedRuntimeActivationMode: `{c.PlannedRuntimeActivationMode}`");
        b.AppendLine($"- PlannedRuntimeSwitchArtifactPath: `{c.PlannedRuntimeSwitchArtifactPath}`");
        b.AppendLine($"- PlannedActivationAuditArtifactPath: `{c.PlannedActivationAuditArtifactPath}`");
        b.AppendLine($"- PlannedRuntimeGuardManifestPath: `{c.PlannedRuntimeGuardManifestPath}`");
        b.AppendLine($"- PlannedScopeEnforcementManifestPath: `{c.PlannedScopeEnforcementManifestPath}`");
        b.AppendLine($"- PlannedActivationRollbackBindingPath: `{c.PlannedActivationRollbackBindingPath}`");
        b.AppendLine($"- ReferencedRollbackSnapshotPath: `{c.ReferencedRollbackSnapshotPath}`");
        b.AppendLine($"- ReferencedRevocationRecordPath: `{c.ReferencedRevocationRecordPath}`");
        b.AppendLine($"- ReferencedConfigPatchSourcePath: `{c.ReferencedConfigPatchSourcePath}`");
        b.AppendLine();
        b.AppendLine("## Runtime (Still Untouched)");
        b.AppendLine($"- DryRunOnly: `{r.DryRunOnly}`");
        b.AppendLine($"- RuntimeActivationWriteAllowed: `{r.RuntimeActivationWriteAllowed}` (always false from this matrix)");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- ConfigPatchAppliedToRuntime: `{r.ConfigPatchAppliedToRuntime}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("## Upstream V8.19R Carry");
        b.AppendLine($"- UpstreamActivationDryRunGatePresent: `{r.UpstreamActivationDryRunGatePresent}`");
        b.AppendLine($"- UpstreamActivationDryRunGatePassed: `{r.UpstreamActivationDryRunGatePassed}`");
        b.AppendLine($"- ActivationDryRunOnly: `{r.ActivationDryRunOnly}`");
        b.AppendLine($"- RuntimeActivationAllowed: `{r.RuntimeActivationAllowed}`");
        b.AppendLine();
        b.AppendLine("## V8.18 Carry");
        b.AppendLine($"- Crossed: `{r.Crossed}`");
        b.AppendLine($"- ArtifactOnly: `{r.ArtifactOnly}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RollbackSnapshotWritten: `{r.RollbackSnapshotWritten}`");
        b.AppendLine($"- AuditLogWritten: `{r.AuditLogWritten}`");
        b.AppendLine($"- RevocationRecordWritten: `{r.RevocationRecordWritten}`");
        b.AppendLine();
        b.AppendLine("## Guarded Activation Dry-Run Cases");
        foreach (var caseItem in r.Cases)
        {
            b.AppendLine($"- `{caseItem.CaseName}`: passedAsExpected=`{caseItem.PassedAsExpected}`");
            b.AppendLine($"  - status expected=`{caseItem.ExpectedStatus}` actual=`{caseItem.ActualStatus}` matched=`{caseItem.StatusMatched}`");
            if (!string.IsNullOrEmpty(caseItem.ExpectedBlockedReason))
            {
                b.AppendLine($"  - expectedReason=`{caseItem.ExpectedBlockedReason}` matched=`{caseItem.BlockedReasonMatched}`");
            }
            if (caseItem.ActualBlockedReasons.Count > 0)
            {
                b.AppendLine($"  - actualReasons=`{string.Join(", ", caseItem.ActualBlockedReasons)}`");
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

        b.AppendLine("V8.20 guarded runtime activation gate dry-run。读取 V8.19R + 规划 5 个 runtime-activation 写出路径（runtime-switch / activation-audit / runtime-guard-manifest / scope-enforcement-manifest / activation-rollback-binding）。RuntimeActivationWriteAllowed 永远 false。下一阶段才可能写出这些 artifact。");
        return b.ToString();
    }
}

public sealed record GuardedRuntimeActivationGateDryRunScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? ActivationDryRunReport,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public GuardedRuntimeActivationWriteContract PlannedGuardedActivationContract { get; init; } = new();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool DryRunOnly { get; init; }
    public bool RuntimeActivationWriteAllowed { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GuardedRuntimeActivationDryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase>();

    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public GuardedRuntimeActivationWriteContract PlannedGuardedActivationContract { get; init; } = new();
    public bool UpstreamActivationDryRunGatePresent { get; init; }
    public bool UpstreamActivationDryRunGatePassed { get; init; }

    public bool DryRunOnly { get; init; }
    public bool RuntimeActivationWriteAllowed { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }

    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }
    public bool ActivationDryRunOnly { get; init; }
    public bool RuntimeActivationAllowed { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool EvidenceCopiedToMainline { get; init; }
    public bool TrustRegistryCopiedToMainline { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool GrantApplied { get; init; }
    public bool ApplicationApplied { get; init; }
    public bool RollbackActivated { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
