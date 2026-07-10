using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    public FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport Run(
        FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport? loadedExecutionReport,
        CrossingCapabilityGrantContent? loadedGrant,
        CrossingRuntimeConfigPatchContent? loadedConfigPatch,
        CrossingRollbackSnapshotContent? loadedRollbackSnapshot,
        CrossingAuditLogEvent? loadedAuditEvent,
        CrossingRevocationRecordContent? loadedRevocation,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        string? configPatchSourcePath,
        string? rollbackSnapshotPath,
        string? revocationRecordPath,
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase>();

        foreach (var scenario in BuildScenarios())
        {
            var decision = FormalRetrievalPromotionApprovalRuntimeActivationDryRunPolicy.Evaluate(
                scenario.ExecutionReport,
                scenario.Grant,
                scenario.ConfigPatch,
                scenario.RollbackSnapshot,
                scenario.AuditEvent,
                scenario.Revocation,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent,
                plannedConfigPatchSourcePath: "vector/v8/dedicated-crossing/runtime-config-patch-fixture.json",
                plannedRollbackSnapshotPath: "vector/v8/dedicated-crossing/rollback-snapshot-fixture.json",
                plannedRevocationRecordPath: "vector/v8/dedicated-crossing/revocation-record-fixture.json");

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var runtimeNotActivated = !decision.RuntimeActivation;
            var runtimeActivationNotAllowed = !decision.RuntimeActivationAllowed;
            var formalRetrievalNotAllowed = !decision.FormalRetrievalAllowed;
            var runtimeSwitchNotAllowed = !decision.RuntimeSwitchAllowed;
            var configPatchNotApplied = !decision.ConfigPatchAppliedToRuntime;
            var activationDryRunOnly = decision.ActivationDryRunOnly;

            var passedAsExpected = statusMatched
                && blockedReasonMatched
                && runtimeNotActivated
                && runtimeActivationNotAllowed
                && formalRetrievalNotAllowed
                && runtimeSwitchNotAllowed
                && configPatchNotApplied
                && activationDryRunOnly;

            cases.Add(new FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundGrantId = decision.BoundGrantId,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                PlannedActivationContract = decision.PlannedActivationContract,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                ActivationDryRunOnly = decision.ActivationDryRunOnly,
                RuntimeActivationAllowed = decision.RuntimeActivationAllowed,
                RuntimeActivation = decision.RuntimeActivation,
                FormalRetrievalAllowed = decision.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = decision.RuntimeSwitchAllowed,
                ConfigPatchAppliedToRuntime = decision.ConfigPatchAppliedToRuntime,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var readyCases = cases.Count(static c => c.ActualStatus == RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady);
        var blockedCases = cases.Count(static c => c.ActualStatus == RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked);

        var matrixBlocked = new List<string>();
        if (cases.Count < 20) matrixBlocked.Add("InsufficientActivationDryRunCases");
        if (failedCases > 0) matrixBlocked.Add("ActivationDryRunMatrixFailed");

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[] { RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady, RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked })
        {
            if (!statusesCovered.Contains(s)) matrixBlocked.Add($"StatusBranchNotCovered:{s}");
        }

        // 6 重 invariant — 每个 case 都必须 false。
        if (cases.Any(c => c.RuntimeActivation)) matrixBlocked.Add("RuntimeActivationLeaked");
        if (cases.Any(c => c.RuntimeActivationAllowed)) matrixBlocked.Add("RuntimeActivationAllowedLeaked");
        if (cases.Any(c => c.FormalRetrievalAllowed)) matrixBlocked.Add("FormalRetrievalAllowedLeaked");
        if (cases.Any(c => c.RuntimeSwitchAllowed)) matrixBlocked.Add("RuntimeSwitchAllowedLeaked");
        if (cases.Any(c => c.ConfigPatchAppliedToRuntime)) matrixBlocked.Add("ConfigPatchAppliedToRuntimeLeaked");
        if (cases.Any(c => !c.ActivationDryRunOnly)) matrixBlocked.Add("ActivationDryRunOnlyViolated");

        // ===== real run =====
        var realUpstreamPresent = loadedExecutionReport is not null;
        var realUpstreamPassed = loadedExecutionReport?.GatePassed ?? false;
        var realDecision = FormalRetrievalPromotionApprovalRuntimeActivationDryRunPolicy.Evaluate(
            loadedExecutionReport,
            loadedGrant,
            loadedConfigPatch,
            loadedRollbackSnapshot,
            loadedAuditEvent,
            loadedRevocation,
            rtPassed,
            p15Passed,
            mainlineEvidencePresent,
            mainlineRegistryPresent,
            plannedConfigPatchSourcePath: configPatchSourcePath,
            plannedRollbackSnapshotPath: rollbackSnapshotPath,
            plannedRevocationRecordPath: revocationRecordPath);

        if (!realUpstreamPresent) matrixBlocked.Add("RealCrossingExecutionGateArtifactMissing");
        if (realUpstreamPresent && !realUpstreamPassed) matrixBlocked.Add("RealCrossingExecutionGateNotPassed");
        if (realDecision.Status != RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady)
        {
            foreach (var r in realDecision.BlockedReasons)
            {
                matrixBlocked.Add($"RealActivationDryRun:{r}");
            }
        }

        if (mainlineEvidencePresent) matrixBlocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) matrixBlocked.Add("MainlineTrustRegistryPresent");
        if (!rtPassed) matrixBlocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) matrixBlocked.Add("P15GateNotPassed");

        var distinctBlocked = matrixBlocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var dryRunPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && dryRunPassed;

        return new FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport
        {
            OperationId = $"frp-runtime-activation-dry-run-{Guid.NewGuid():N}",
            CreatedAt = now,
            RuntimeActivationDryRunPassed = dryRunPassed,
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
            PlannedActivationContract = realDecision.PlannedActivationContract,
            UpstreamExecutionGatePresent = realUpstreamPresent,
            UpstreamExecutionGatePassed = realUpstreamPassed,
            ActivationDryRunOnly = true,
            RuntimeActivationAllowed = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            // carry from V8.18
            Crossed = loadedExecutionReport?.Crossed ?? false,
            ArtifactOnly = true,
            CapabilityGrantWritten = loadedExecutionReport?.CapabilityGrantWritten ?? false,
            ConfigPatchWritten = loadedExecutionReport?.ConfigPatchWritten ?? false,
            RollbackSnapshotWritten = loadedExecutionReport?.RollbackSnapshotWritten ?? false,
            AuditLogWritten = loadedExecutionReport?.AuditLogWritten ?? false,
            RevocationRecordWritten = loadedExecutionReport?.RevocationRecordWritten ?? false,
            // 安全不变量
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ConfigPatchWrittenLeaked = false,
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
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=GuardedRuntimeActivationGate (the only path that may produce RuntimeActivation=true; this matrix never does)"
            }
        };
    }

    private static IReadOnlyList<RuntimeActivationDryRunScenario> BuildScenarios()
    {
        var cleanExecution = BuildCleanExecutionReport();
        var cleanGrant = BuildCleanGrant(TestGrantId, TestCapability, TestScope);
        var cleanConfigPatch = BuildCleanConfigPatch(TestGrantId, TestCapability, TestScope);
        var cleanRollback = BuildCleanRollback(TestGrantId, TestCapability, TestScope);
        var cleanAudit = BuildCleanAudit(TestGrantId, TestCapability, TestScope);
        var cleanRevocation = BuildCleanRevocation(TestGrantId, TestCapability, TestScope);

        return
        [
            new("AllUpstreamClean", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady, null),
            new("CrossingExecutionGateMissing", null, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.CrossingExecutionGateMissing),
            new("CrossingExecutionGateNotPassed", MutateExecution(cleanExecution, gatePassed: false), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.CrossingExecutionGateNotPassed),
            new("CrossingNotTrueInUpstream", MutateExecution(cleanExecution, crossed: false), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.CrossingNotTrueInUpstream),
            new("ArtifactOnlyFalseInUpstream", MutateExecution(cleanExecution, artifactOnly: false), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ArtifactOnlyFalseInUpstream),
            new("CapabilityGrantWrittenFalseInUpstream", MutateExecution(cleanExecution, capabilityGrantWritten: false), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.CapabilityGrantWrittenFalseInUpstream),
            new("ConfigPatchWrittenFalseInUpstream", MutateExecution(cleanExecution, configPatchWritten: false), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchWrittenFalseInUpstream),
            new("RuntimeActivationTrueInUpstream", MutateExecution(cleanExecution, runtimeActivation: true), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RuntimeActivationTrueInUpstream),
            new("FormalRetrievalAllowedTrueInUpstream", MutateExecution(cleanExecution, formalRetrievalAllowed: true), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream),
            new("RuntimeSwitchAllowedTrueInUpstream", MutateExecution(cleanExecution, runtimeSwitchAllowed: true), cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream),
            new("GrantArtifactMissing", cleanExecution, null, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantArtifactMissing),
            new("ConfigPatchArtifactMissing", cleanExecution, cleanGrant, null, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchArtifactMissing),
            new("RollbackSnapshotArtifactMissing", cleanExecution, cleanGrant, cleanConfigPatch, null, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RollbackSnapshotArtifactMissing),
            new("AuditLogArtifactMissing", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, null, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditLogArtifactMissing),
            new("RevocationRecordArtifactMissing", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, null,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationRecordArtifactMissing),
            new("GrantCapabilityMismatch", cleanExecution,
                BuildCleanGrant(TestGrantId, capability: "UnauthorizedCapability", TestScope),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantCapabilityMismatch),
            new("GrantScopeMismatch", cleanExecution,
                BuildCleanGrant(TestGrantId, TestCapability, scope: "other-workspace/other-collection"),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantScopeMismatch),
            new("ConfigPatchSourceGrantIdMismatch", cleanExecution, cleanGrant,
                BuildCleanConfigPatch("frp-grant-divergent", TestCapability, TestScope),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchSourceGrantIdMismatch),
            new("RollbackSourceGrantIdMismatch", cleanExecution, cleanGrant, cleanConfigPatch,
                BuildCleanRollback("frp-grant-divergent", TestCapability, TestScope),
                cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RollbackSourceGrantIdMismatch),
            new("AuditGrantIdMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                BuildCleanAudit("frp-grant-divergent", TestCapability, TestScope),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditGrantIdMismatch),
            new("RevocationGrantIdMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit,
                BuildCleanRevocation("frp-grant-divergent", TestCapability, TestScope),
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationGrantIdMismatch),
            new("RevocationAlreadyRevoked", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit,
                new CrossingRevocationRecordContent
                {
                    RevocationRecordId = "frp-revocation-record-fixture-001",
                    GrantId = TestGrantId,
                    BoundCapability = TestCapability,
                    BoundScope = TestScope,
                    Revocable = true,
                    RevocationPathPresent = true,
                    RevocationStatus = "Revoked"
                },
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationAlreadyRevoked),
            new("RuntimeGateNotPassed", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                false, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, false, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, true, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, true,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.MainlineTrustRegistryPresent),

            // ===== V8.19R — 28 个 artifact-content negative scenarios =====
            new("GrantRevocableFalse", cleanExecution,
                MutateGrant(cleanGrant, revocable: false),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantRevocableFalse),
            new("GrantArtifactOnlyFalse", cleanExecution,
                MutateGrant(cleanGrant, artifactOnly: false),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantArtifactOnlyFalse),
            new("GrantCrossedFalse", cleanExecution,
                MutateGrant(cleanGrant, crossed: false),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantCrossedFalse),
            new("GrantFormalRetrievalAllowedTrue", cleanExecution,
                MutateGrant(cleanGrant, formalRetrievalAllowed: true),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantFormalRetrievalAllowedTrue),
            new("GrantRuntimeSwitchAllowedTrue", cleanExecution,
                MutateGrant(cleanGrant, runtimeSwitchAllowed: true),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantRuntimeSwitchAllowedTrue),
            new("GrantSourcePreCrossingMismatch", cleanExecution,
                MutateGrant(cleanGrant, sourcePreCrossingOperationId: "frp-pre-crossing-divergent"),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantSourcePreCrossingMismatch),
            new("GrantSourceDryRunMismatch", cleanExecution,
                MutateGrant(cleanGrant, sourceDryRunOperationId: "frp-dry-run-divergent"),
                cleanConfigPatch, cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.GrantSourceDryRunMismatch),
            new("ConfigPatchTargetCapabilityMismatch", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, targetCapability: "DivergentCapability"),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchTargetCapabilityMismatch),
            new("ConfigPatchTargetScopeMismatch", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, targetScope: "divergent-workspace/divergent-collection"),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchTargetScopeMismatch),
            new("ConfigPatchPatchModeNotArtifactOnly", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, patchMode: "ApplyToRuntime"),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchPatchModeNotArtifactOnly),
            new("ConfigPatchApplyToRuntimeTrue", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, applyToRuntime: true),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchApplyToRuntimeTrue),
            new("ConfigPatchFormalRetrievalAllowedTrue", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, formalRetrievalAllowed: true),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchFormalRetrievalAllowedTrue),
            new("ConfigPatchSourcePreCrossingMismatch", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, sourcePreCrossingOperationId: "frp-pre-crossing-divergent"),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchSourcePreCrossingMismatch),
            new("ConfigPatchSourceDryRunMismatch", cleanExecution, cleanGrant,
                MutateConfigPatch(cleanConfigPatch, sourceDryRunOperationId: "frp-dry-run-divergent"),
                cleanRollback, cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.ConfigPatchSourceDryRunMismatch),
            new("RollbackCapabilityMismatch", cleanExecution, cleanGrant, cleanConfigPatch,
                MutateRollback(cleanRollback, boundCapability: "DivergentCapability"),
                cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RollbackCapabilityMismatch),
            new("RollbackScopeMismatch", cleanExecution, cleanGrant, cleanConfigPatch,
                MutateRollback(cleanRollback, boundScope: "divergent-workspace/divergent-collection"),
                cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RollbackScopeMismatch),
            new("RollbackRestoreTestRequiredFalse", cleanExecution, cleanGrant, cleanConfigPatch,
                MutateRollback(cleanRollback, restoreTestRequired: false),
                cleanAudit, cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RollbackRestoreTestRequiredFalse),
            new("AuditEventTypeMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, eventType: "SomeOtherEvent"),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditEventTypeMismatch),
            new("AuditCapabilityMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, boundCapability: "DivergentCapability"),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditCapabilityMismatch),
            new("AuditScopeMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, boundScope: "divergent-workspace/divergent-collection"),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditScopeMismatch),
            new("AuditCrossedFalse", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, crossed: false),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditCrossedFalse),
            new("AuditArtifactOnlyFalse", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, artifactOnly: false),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditArtifactOnlyFalse),
            new("AuditRuntimeActivationTrue", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, runtimeActivation: true),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditRuntimeActivationTrue),
            new("AuditFormalRetrievalAllowedTrue", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback,
                MutateAudit(cleanAudit, formalRetrievalAllowed: true),
                cleanRevocation,
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.AuditFormalRetrievalAllowedTrue),
            new("RevocationCapabilityMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit,
                MutateRevocation(cleanRevocation, boundCapability: "DivergentCapability"),
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationCapabilityMismatch),
            new("RevocationScopeMismatch", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit,
                MutateRevocation(cleanRevocation, boundScope: "divergent-workspace/divergent-collection"),
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationScopeMismatch),
            new("RevocationRevocableFalse", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit,
                MutateRevocation(cleanRevocation, revocable: false),
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationRevocableFalse),
            new("RevocationPathPresentFalse", cleanExecution, cleanGrant, cleanConfigPatch, cleanRollback, cleanAudit,
                MutateRevocation(cleanRevocation, revocationPathPresent: false),
                true, true, false, false,
                RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
                RuntimeActivationDryRunBlockedReasons.RevocationPathPresentFalse)
        ];
    }

    // V8.19R — artifact-content mutate helpers。每个 helper 取 baseline + override 任意字段。

    private static CrossingCapabilityGrantContent MutateGrant(
        CrossingCapabilityGrantContent b,
        string? grantId = null,
        string? capability = null,
        string? scope = null,
        string? sourcePreCrossingOperationId = null,
        string? sourceDryRunOperationId = null,
        bool? revocable = null,
        bool? runtimeActivationAllowed = null,
        bool? artifactOnly = null,
        bool? crossed = null,
        bool? formalRetrievalAllowed = null,
        bool? runtimeSwitchAllowed = null) => new()
    {
        GrantId = grantId ?? b.GrantId,
        Capability = capability ?? b.Capability,
        Scope = scope ?? b.Scope,
        SourcePreCrossingOperationId = sourcePreCrossingOperationId ?? b.SourcePreCrossingOperationId,
        SourceDryRunOperationId = sourceDryRunOperationId ?? b.SourceDryRunOperationId,
        Revocable = revocable ?? b.Revocable,
        RuntimeActivationAllowed = runtimeActivationAllowed ?? b.RuntimeActivationAllowed,
        ArtifactOnly = artifactOnly ?? b.ArtifactOnly,
        Crossed = crossed ?? b.Crossed,
        FormalRetrievalAllowed = formalRetrievalAllowed ?? b.FormalRetrievalAllowed,
        RuntimeSwitchAllowed = runtimeSwitchAllowed ?? b.RuntimeSwitchAllowed
    };

    private static CrossingRuntimeConfigPatchContent MutateConfigPatch(
        CrossingRuntimeConfigPatchContent b,
        string? patchId = null,
        string? targetCapability = null,
        string? targetScope = null,
        string? patchMode = null,
        bool? applyToRuntime = null,
        bool? formalRetrievalAllowed = null,
        string? sourceGrantId = null,
        string? sourcePreCrossingOperationId = null,
        string? sourceDryRunOperationId = null) => new()
    {
        PatchId = patchId ?? b.PatchId,
        TargetCapability = targetCapability ?? b.TargetCapability,
        TargetScope = targetScope ?? b.TargetScope,
        PatchMode = patchMode ?? b.PatchMode,
        ApplyToRuntime = applyToRuntime ?? b.ApplyToRuntime,
        FormalRetrievalAllowed = formalRetrievalAllowed ?? b.FormalRetrievalAllowed,
        SourceGrantId = sourceGrantId ?? b.SourceGrantId,
        SourcePreCrossingOperationId = sourcePreCrossingOperationId ?? b.SourcePreCrossingOperationId,
        SourceDryRunOperationId = sourceDryRunOperationId ?? b.SourceDryRunOperationId
    };

    private static CrossingRollbackSnapshotContent MutateRollback(
        CrossingRollbackSnapshotContent b,
        string? snapshotId = null,
        string? boundCapability = null,
        string? boundScope = null,
        string? sourceGrantId = null,
        bool? restoreTestRequired = null) => new()
    {
        SnapshotId = snapshotId ?? b.SnapshotId,
        BoundCapability = boundCapability ?? b.BoundCapability,
        BoundScope = boundScope ?? b.BoundScope,
        SourceGrantId = sourceGrantId ?? b.SourceGrantId,
        RestoreTestRequired = restoreTestRequired ?? b.RestoreTestRequired
    };

    private static CrossingAuditLogEvent MutateAudit(
        CrossingAuditLogEvent b,
        string? eventId = null,
        string? eventType = null,
        string? boundCapability = null,
        string? boundScope = null,
        string? grantId = null,
        bool? crossed = null,
        bool? artifactOnly = null,
        bool? runtimeActivation = null,
        bool? formalRetrievalAllowed = null) => new()
    {
        EventId = eventId ?? b.EventId,
        EventType = eventType ?? b.EventType,
        BoundCapability = boundCapability ?? b.BoundCapability,
        BoundScope = boundScope ?? b.BoundScope,
        GrantId = grantId ?? b.GrantId,
        Crossed = crossed ?? b.Crossed,
        ArtifactOnly = artifactOnly ?? b.ArtifactOnly,
        RuntimeActivation = runtimeActivation ?? b.RuntimeActivation,
        FormalRetrievalAllowed = formalRetrievalAllowed ?? b.FormalRetrievalAllowed
    };

    private static CrossingRevocationRecordContent MutateRevocation(
        CrossingRevocationRecordContent b,
        string? revocationRecordId = null,
        string? grantId = null,
        string? boundCapability = null,
        string? boundScope = null,
        bool? revocable = null,
        bool? revocationPathPresent = null,
        string? revocationStatus = null) => new()
    {
        RevocationRecordId = revocationRecordId ?? b.RevocationRecordId,
        GrantId = grantId ?? b.GrantId,
        BoundCapability = boundCapability ?? b.BoundCapability,
        BoundScope = boundScope ?? b.BoundScope,
        Revocable = revocable ?? b.Revocable,
        RevocationPathPresent = revocationPathPresent ?? b.RevocationPathPresent,
        RevocationStatus = revocationStatus ?? b.RevocationStatus
    };

    public static FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport BuildCleanExecutionReport() => new()
    {
        OperationId = "frp-dedicated-crossing-execution-fixture",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        DedicatedCrossingExecutionGatePassed = true,
        GatePassed = true,
        TotalCases = 16,
        PassedCases = 16,
        FailedCases = 0,
        ExecutedCases = 1,
        BlockedCases = 15,
        Cases = Array.Empty<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateCase>(),
        UpstreamDryRunGatePresent = true,
        UpstreamDryRunGatePassed = true,
        UpstreamDryRunOnly = true,
        UpstreamDryRunExecutionAllowed = false,
        BoundCapability = TestCapability,
        BoundScope = TestScope,
        SourcePreCrossingOperationId = "frp-pre-crossing-fixture",
        SourceDryRunOperationId = "frp-dedicated-crossing-dry-run-fixture",
        PlannedArtifactPaths = Array.Empty<string>(),
        WrittenArtifactPaths = Array.Empty<string>(),
        Crossed = true,
        ArtifactOnly = true,
        CapabilityGrantWritten = true,
        ConfigPatchWritten = true,
        RollbackSnapshotWritten = true,
        AuditLogWritten = true,
        RevocationRecordWritten = true,
        RuntimeActivation = false,
        FormalRetrievalAllowed = false,
        RuntimeSwitchAllowed = false,
        FormalPackageWritten = false,
        PackageOutputChanged = false,
        PackingPolicyChanged = false,
        VectorStoreBindingChanged = false,
        GlobalDefaultOn = false,
        ConfigPatchAppliedToRuntime = false,
        EvidenceCopiedToMainline = false,
        TrustRegistryCopiedToMainline = false,
        MainlineEvidencePresent = false,
        MainlineTrustRegistryPresent = false,
        ManualReviewRequired = false,
        ApprovalSealed = false,
        GrantApplied = false,
        ApplicationApplied = false,
        RollbackActivated = false,
        PromotionToMainlinePerformed = false,
        NoRuntimeMutationInvariant = true
    };

    public static CrossingCapabilityGrantContent BuildCleanGrant(string grantId, string capability, string scope) => new()
    {
        GrantId = grantId,
        Capability = capability,
        Scope = scope,
        SourcePreCrossingOperationId = "frp-pre-crossing-fixture",
        SourceDryRunOperationId = "frp-dedicated-crossing-dry-run-fixture",
        Revocable = true,
        RuntimeActivationAllowed = false,
        ArtifactOnly = true,
        Crossed = true,
        FormalRetrievalAllowed = false,
        RuntimeSwitchAllowed = false
    };

    public static CrossingRuntimeConfigPatchContent BuildCleanConfigPatch(string grantId, string capability, string scope) => new()
    {
        PatchId = "frp-runtime-config-patch-fixture-001",
        TargetCapability = capability,
        TargetScope = scope,
        PatchMode = "ArtifactOnly",
        ApplyToRuntime = false,
        FormalRetrievalAllowed = false,
        SourceGrantId = grantId,
        SourcePreCrossingOperationId = "frp-pre-crossing-fixture",
        SourceDryRunOperationId = "frp-dedicated-crossing-dry-run-fixture"
    };

    public static CrossingRollbackSnapshotContent BuildCleanRollback(string grantId, string capability, string scope) => new()
    {
        SnapshotId = "frp-rollback-snapshot-fixture-001",
        BoundCapability = capability,
        BoundScope = scope,
        SourceGrantId = grantId,
        RestoreTestRequired = true
    };

    public static CrossingAuditLogEvent BuildCleanAudit(string grantId, string capability, string scope) => new()
    {
        EventId = "frp-crossing-audit-fixture-001",
        EventType = "DedicatedCrossingArtifactWriteOut",
        BoundCapability = capability,
        BoundScope = scope,
        GrantId = grantId,
        Crossed = true,
        ArtifactOnly = true,
        RuntimeActivation = false,
        FormalRetrievalAllowed = false
    };

    public static CrossingRevocationRecordContent BuildCleanRevocation(string grantId, string capability, string scope) => new()
    {
        RevocationRecordId = "frp-revocation-record-fixture-001",
        GrantId = grantId,
        BoundCapability = capability,
        BoundScope = scope,
        Revocable = true,
        RevocationPathPresent = true,
        RevocationStatus = "RevocableNotYetRevoked"
    };

    private static FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport MutateExecution(
        FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport b,
        bool? gatePassed = null,
        bool? crossed = null,
        bool? artifactOnly = null,
        bool? capabilityGrantWritten = null,
        bool? configPatchWritten = null,
        bool? rollbackSnapshotWritten = null,
        bool? auditLogWritten = null,
        bool? revocationRecordWritten = null,
        bool? runtimeActivation = null,
        bool? formalRetrievalAllowed = null,
        bool? runtimeSwitchAllowed = null) => new()
    {
        OperationId = b.OperationId,
        CreatedAt = b.CreatedAt,
        DedicatedCrossingExecutionGatePassed = gatePassed ?? b.DedicatedCrossingExecutionGatePassed,
        GatePassed = gatePassed ?? b.GatePassed,
        TotalCases = b.TotalCases,
        PassedCases = b.PassedCases,
        FailedCases = b.FailedCases,
        ExecutedCases = b.ExecutedCases,
        BlockedCases = b.BlockedCases,
        Cases = b.Cases,
        UpstreamDryRunGatePresent = b.UpstreamDryRunGatePresent,
        UpstreamDryRunGatePassed = b.UpstreamDryRunGatePassed,
        UpstreamDryRunOnly = b.UpstreamDryRunOnly,
        UpstreamDryRunExecutionAllowed = b.UpstreamDryRunExecutionAllowed,
        BoundCapability = b.BoundCapability,
        BoundScope = b.BoundScope,
        SourcePreCrossingOperationId = b.SourcePreCrossingOperationId,
        SourceDryRunOperationId = b.SourceDryRunOperationId,
        PlannedArtifactPaths = b.PlannedArtifactPaths,
        WrittenArtifactPaths = b.WrittenArtifactPaths,
        Crossed = crossed ?? b.Crossed,
        ArtifactOnly = artifactOnly ?? b.ArtifactOnly,
        CapabilityGrantWritten = capabilityGrantWritten ?? b.CapabilityGrantWritten,
        ConfigPatchWritten = configPatchWritten ?? b.ConfigPatchWritten,
        RollbackSnapshotWritten = rollbackSnapshotWritten ?? b.RollbackSnapshotWritten,
        AuditLogWritten = auditLogWritten ?? b.AuditLogWritten,
        RevocationRecordWritten = revocationRecordWritten ?? b.RevocationRecordWritten,
        RuntimeActivation = runtimeActivation ?? b.RuntimeActivation,
        FormalRetrievalAllowed = formalRetrievalAllowed ?? b.FormalRetrievalAllowed,
        RuntimeSwitchAllowed = runtimeSwitchAllowed ?? b.RuntimeSwitchAllowed,
        FormalPackageWritten = b.FormalPackageWritten,
        PackageOutputChanged = b.PackageOutputChanged,
        PackingPolicyChanged = b.PackingPolicyChanged,
        VectorStoreBindingChanged = b.VectorStoreBindingChanged,
        GlobalDefaultOn = b.GlobalDefaultOn,
        ConfigPatchAppliedToRuntime = b.ConfigPatchAppliedToRuntime,
        EvidenceCopiedToMainline = b.EvidenceCopiedToMainline,
        TrustRegistryCopiedToMainline = b.TrustRegistryCopiedToMainline,
        MainlineEvidencePresent = b.MainlineEvidencePresent,
        MainlineTrustRegistryPresent = b.MainlineTrustRegistryPresent,
        ManualReviewRequired = b.ManualReviewRequired,
        ApprovalSealed = b.ApprovalSealed,
        GrantApplied = b.GrantApplied,
        ApplicationApplied = b.ApplicationApplied,
        RollbackActivated = b.RollbackActivated,
        PromotionToMainlinePerformed = b.PromotionToMainlinePerformed,
        NoRuntimeMutationInvariant = b.NoRuntimeMutationInvariant
    };

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- RuntimeActivationDryRunPassed: `{r.RuntimeActivationDryRunPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — Ready: `{r.ReadyCases}` Blocked: `{r.BlockedCases}`");
        b.AppendLine();
        b.AppendLine("## Bound (Real)");
        b.AppendLine($"- BoundGrantId: `{r.BoundGrantId}`");
        b.AppendLine($"- BoundCapability: `{r.BoundCapability}`");
        b.AppendLine($"- BoundScope: `{r.BoundScope}`");
        b.AppendLine($"- UpstreamExecutionGatePresent: `{r.UpstreamExecutionGatePresent}`");
        b.AppendLine($"- UpstreamExecutionGatePassed: `{r.UpstreamExecutionGatePassed}`");
        b.AppendLine();
        b.AppendLine("## Planned Activation Contract");
        b.AppendLine($"- PlannedRuntimeActivationMode: `{r.PlannedActivationContract.PlannedRuntimeActivationMode}`");
        b.AppendLine($"- PlannedCapability: `{r.PlannedActivationContract.PlannedCapability}`");
        b.AppendLine($"- PlannedScope: `{r.PlannedActivationContract.PlannedScope}`");
        b.AppendLine($"- PlannedConfigPatchSourcePath: `{r.PlannedActivationContract.PlannedConfigPatchSourcePath}`");
        b.AppendLine($"- PlannedRuntimeSwitchPath: `{r.PlannedActivationContract.PlannedRuntimeSwitchPath}`");
        b.AppendLine($"- PlannedActivationAuditPath: `{r.PlannedActivationContract.PlannedActivationAuditPath}`");
        b.AppendLine($"- PlannedRollbackReference: `{r.PlannedActivationContract.PlannedRollbackReference}`");
        b.AppendLine($"- PlannedRevocationReference: `{r.PlannedActivationContract.PlannedRevocationReference}`");
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
        b.AppendLine("## Runtime (Still Untouched)");
        b.AppendLine($"- ActivationDryRunOnly: `{r.ActivationDryRunOnly}`");
        b.AppendLine($"- RuntimeActivationAllowed: `{r.RuntimeActivationAllowed}` (always false from this matrix)");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- ConfigPatchAppliedToRuntime: `{r.ConfigPatchAppliedToRuntime}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("## Activation Dry-Run Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedBlockedReason))
            {
                b.AppendLine($"  - expectedReason=`{c.ExpectedBlockedReason}` matched=`{c.BlockedReasonMatched}`");
            }
            b.AppendLine($"  - bound: grantId=`{c.BoundGrantId}` capability=`{c.BoundCapability}` scope=`{c.BoundScope}`");
            b.AppendLine($"  - activationDryRunOnly=`{c.ActivationDryRunOnly}` runtimeActivation=`{c.RuntimeActivation}` formalRetrievalAllowed=`{c.FormalRetrievalAllowed}` configPatchAppliedToRuntime=`{c.ConfigPatchAppliedToRuntime}`");
            if (c.ActualBlockedReasons.Count > 0)
            {
                b.AppendLine($"  - actualReasons=`{string.Join(", ", c.ActualBlockedReasons)}`");
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

        b.AppendLine("V8.19 runtime activation dry-run matrix。读取 V8.18 5 个 artifact + 跨字段 GrantId/capability/scope 一致性核对 + 输出 planned runtime activation contract（仅 plan，不执行）。RuntimeActivationDryRunReady ≠ RuntimeActivated — runtime 仍未动。下一阶段 GuardedRuntimeActivationGate 才可能产生 RuntimeActivation=true。");
        return b.ToString();
    }
}

public sealed record RuntimeActivationDryRunScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport? ExecutionReport,
    CrossingCapabilityGrantContent? Grant,
    CrossingRuntimeConfigPatchContent? ConfigPatch,
    CrossingRollbackSnapshotContent? RollbackSnapshot,
    CrossingAuditLogEvent? AuditEvent,
    CrossingRevocationRecordContent? Revocation,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public RuntimeActivationPlannedContract PlannedActivationContract { get; init; } = new();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool ActivationDryRunOnly { get; init; }
    public bool RuntimeActivationAllowed { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool RuntimeActivationDryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalRuntimeActivationDryRunCase>();

    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public RuntimeActivationPlannedContract PlannedActivationContract { get; init; } = new();
    public bool UpstreamExecutionGatePresent { get; init; }
    public bool UpstreamExecutionGatePassed { get; init; }

    // Runtime — 永远未动
    public bool ActivationDryRunOnly { get; init; }
    public bool RuntimeActivationAllowed { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }

    // carry V8.18
    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }

    // Safety
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool ConfigPatchWrittenLeaked { get; init; }
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

public sealed class FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
