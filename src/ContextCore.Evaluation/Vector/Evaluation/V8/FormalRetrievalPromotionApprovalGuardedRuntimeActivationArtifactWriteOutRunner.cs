using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    public FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport Run(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? loadedGuardedDryRunReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        Func<string, bool>? realPathExists = null,
        Func<GuardedRuntimeActivationArtifactWriteOutDecision, FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteResult>? realWriter = null,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutCase>();

        var cleanUpstream = BuildCleanGuardedRuntimeActivationDryRunReport();
        foreach (var scenario in BuildScenarios(cleanUpstream))
        {
            var decision = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutPolicy.Evaluate(
                scenario.GuardedRuntimeActivationDryRunReport,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent,
                scenario.PathExistence,
                scenario.ReferenceExistence,
                scenario.Overrides);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var passedAsExpected = statusMatched
                && blockedReasonMatched
                && !decision.RuntimeActivation
                && !decision.FormalRetrievalAllowed
                && !decision.RuntimeSwitchAllowed
                && !decision.PackageOutputChanged
                && decision.NoRuntimeMutationInvariant;

            cases.Add(new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundGrantId = decision.BoundGrantId,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                PlannedArtifactPaths = decision.PlannedArtifactPaths,
                RuntimeActivationArtifactsWritten = decision.RuntimeActivationArtifactsWritten,
                RuntimeActivation = decision.RuntimeActivation,
                FormalRetrievalAllowed = decision.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = decision.RuntimeSwitchAllowed,
                PackageOutputChanged = decision.PackageOutputChanged,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var writtenCases = cases.Count(static c => c.ActualStatus == GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten);
        var blockedCases = cases.Count(static c => c.ActualStatus == GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked);

        var matrixBlocked = new List<string>();
        if (cases.Count < 25) matrixBlocked.Add("InsufficientGuardedRuntimeActivationArtifactWriteOutCases");
        if (failedCases > 0) matrixBlocked.Add("GuardedRuntimeActivationArtifactWriteOutMatrixFailed");
        if (cases.Any(c => c.RuntimeActivation)) matrixBlocked.Add("RuntimeActivationLeaked");
        if (cases.Any(c => c.FormalRetrievalAllowed)) matrixBlocked.Add("FormalRetrievalAllowedLeaked");
        if (cases.Any(c => c.RuntimeSwitchAllowed)) matrixBlocked.Add("RuntimeSwitchAllowedLeaked");
        if (cases.Any(c => c.PackageOutputChanged)) matrixBlocked.Add("PackageOutputChangedLeaked");

        realPathExists ??= File.Exists;
        realWriter ??= decision => FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteAll(decision, now);

        var realUpstreamPresent = loadedGuardedDryRunReport is not null;
        var realUpstreamPassed = loadedGuardedDryRunReport?.GatePassed ?? false;
        var contract = loadedGuardedDryRunReport?.PlannedGuardedActivationContract ?? new GuardedRuntimeActivationWriteContract();
        var realPathExistence = BuildRealPathExistence(contract, realPathExists);
        var realReferenceExistence = BuildRealReferenceExistence(contract, realPathExists);
        var idempotentReRun = TryValidateExistingArtifacts(
            contract,
            loadedGuardedDryRunReport?.BoundGrantId ?? string.Empty,
            loadedGuardedDryRunReport?.BoundCapability ?? string.Empty,
            loadedGuardedDryRunReport?.BoundScope ?? string.Empty,
            realPathExistence);

        var realDecision = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutPolicy.Evaluate(
            loadedGuardedDryRunReport,
            rtPassed,
            p15Passed,
            mainlineEvidencePresent,
            mainlineRegistryPresent,
            idempotentReRun ? new GuardedRuntimeActivationArtifactPathExistence() : realPathExistence,
            realReferenceExistence,
            new GuardedRuntimeActivationArtifactWriteOutOverrides());

        FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteResult? writeResult = null;
        var runtimeActivationArtifactsWritten = false;
        if (idempotentReRun && realDecision.Status == GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten)
        {
            writeResult = new FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteResult
            {
                AllArtifactsWritten = true,
                WrittenPaths = realDecision.PlannedArtifactPaths.ToArray()
            };
            runtimeActivationArtifactsWritten = true;
        }
        else if (realDecision.Status == GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten)
        {
            writeResult = realWriter(realDecision);
            runtimeActivationArtifactsWritten = writeResult?.AllArtifactsWritten ?? false;
            if (!runtimeActivationArtifactsWritten)
            {
                matrixBlocked.Add("RealGuardedRuntimeActivationArtifactWriteFailed");
            }
        }
        else
        {
            foreach (var reason in realDecision.BlockedReasons)
            {
                matrixBlocked.Add($"RealGuardedRuntimeActivationArtifactWriteOut:{reason}");
            }
        }

        if (!realUpstreamPresent) matrixBlocked.Add("RealGuardedRuntimeActivationDryRunGateArtifactMissing");
        if (realUpstreamPresent && !realUpstreamPassed) matrixBlocked.Add("RealGuardedRuntimeActivationDryRunGateNotPassed");
        if (!rtPassed) matrixBlocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) matrixBlocked.Add("P15GateNotPassed");
        if (mainlineEvidencePresent) matrixBlocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) matrixBlocked.Add("MainlineTrustRegistryPresent");

        var distinctBlocked = matrixBlocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var writeOutPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && writeOutPassed;

        return new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport
        {
            OperationId = $"frp-guarded-runtime-activation-artifact-write-out-{Guid.NewGuid():N}",
            CreatedAt = now,
            GuardedRuntimeActivationArtifactWriteOutPassed = writeOutPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            WrittenCases = writtenCases,
            BlockedCases = blockedCases,
            Cases = cases,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            PlannedGuardedActivationContract = realDecision.PlannedGuardedActivationContract,
            UpstreamGuardedRuntimeActivationDryRunGatePresent = realUpstreamPresent,
            UpstreamGuardedRuntimeActivationDryRunGatePassed = realUpstreamPassed,
            WrittenArtifactPaths = writeResult?.WrittenPaths ?? Array.Empty<string>(),
            RuntimeActivationArtifactsWritten = runtimeActivationArtifactsWritten,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            Crossed = loadedGuardedDryRunReport?.Crossed ?? false,
            ArtifactOnly = true,
            CapabilityGrantWritten = loadedGuardedDryRunReport?.CapabilityGrantWritten ?? false,
            ConfigPatchWritten = loadedGuardedDryRunReport?.ConfigPatchWritten ?? false,
            RollbackSnapshotWritten = loadedGuardedDryRunReport?.RollbackSnapshotWritten ?? false,
            AuditLogWritten = loadedGuardedDryRunReport?.AuditLogWritten ?? false,
            RevocationRecordWritten = loadedGuardedDryRunReport?.RevocationRecordWritten ?? false,
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
                $"written={writtenCases}",
                $"blocked={blockedCases}",
                $"realUpstreamPresent={realUpstreamPresent}",
                $"realUpstreamPassed={realUpstreamPassed}",
                $"runtimeActivationArtifactsWritten={runtimeActivationArtifactsWritten}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                $"idempotentReRun={idempotentReRun}"
            }
        };
    }

    public static FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport BuildCleanGuardedRuntimeActivationDryRunReport() => new()
    {
        OperationId = "frp-guarded-runtime-activation-dry-run-fixture",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        GuardedRuntimeActivationDryRunPassed = true,
        GatePassed = true,
        TotalCases = 33,
        PassedCases = 33,
        FailedCases = 0,
        ReadyCases = 1,
        BlockedCases = 32,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase
            {
                CaseName = "ReadySynthetic",
                ExpectedStatus = GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady,
                ActualStatus = GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady,
                BoundGrantId = TestGrantId,
                BoundCapability = TestCapability,
                BoundScope = TestScope,
                PlannedGuardedActivationContract = BuildCleanContract(),
                DryRunOnly = true,
                RuntimeActivationWriteAllowed = false,
                RuntimeActivation = false,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                PackageOutputChanged = false,
                StatusMatched = true,
                BlockedReasonMatched = true,
                PassedAsExpected = true
            }
        },
        BoundGrantId = TestGrantId,
        BoundCapability = TestCapability,
        BoundScope = TestScope,
        PlannedGuardedActivationContract = BuildCleanContract(),
        UpstreamActivationDryRunGatePresent = true,
        UpstreamActivationDryRunGatePassed = true,
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
        Crossed = true,
        ArtifactOnly = true,
        CapabilityGrantWritten = true,
        ConfigPatchWritten = true,
        RollbackSnapshotWritten = true,
        AuditLogWritten = true,
        RevocationRecordWritten = true,
        ActivationDryRunOnly = true,
        RuntimeActivationAllowed = false,
        PackingPolicyChanged = false,
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

    private static GuardedRuntimeActivationWriteContract BuildCleanContract() => new()
    {
        PlannedRuntimeActivationMode = "GuardedScopeOnly",
        PlannedCapability = TestCapability,
        PlannedScope = TestScope,
        PlannedRuntimeSwitchArtifactPath = "vector/v8/runtime-activation/runtime-switch-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        PlannedActivationAuditArtifactPath = "vector/v8/runtime-activation/activation-audit-FormalRetrievalActivation-demo-workspace-demo-collection.jsonl",
        PlannedRuntimeGuardManifestPath = "vector/v8/runtime-activation/runtime-guard-manifest-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        PlannedScopeEnforcementManifestPath = "vector/v8/runtime-activation/scope-enforcement-manifest-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        PlannedActivationRollbackBindingPath = "vector/v8/runtime-activation/activation-rollback-binding-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ReferencedRollbackSnapshotPath = "vector/v8/dedicated-crossing/rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ReferencedRevocationRecordPath = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ReferencedConfigPatchSourcePath = "vector/v8/dedicated-crossing/runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json"
    };

    private static GuardedRuntimeActivationArtifactPathExistence BuildRealPathExistence(
        GuardedRuntimeActivationWriteContract contract,
        Func<string, bool> realPathExists) => new()
    {
        RuntimeSwitchArtifactExists = !string.IsNullOrWhiteSpace(contract.PlannedRuntimeSwitchArtifactPath) && realPathExists(contract.PlannedRuntimeSwitchArtifactPath),
        ActivationAuditArtifactExists = !string.IsNullOrWhiteSpace(contract.PlannedActivationAuditArtifactPath) && realPathExists(contract.PlannedActivationAuditArtifactPath),
        RuntimeGuardManifestExists = !string.IsNullOrWhiteSpace(contract.PlannedRuntimeGuardManifestPath) && realPathExists(contract.PlannedRuntimeGuardManifestPath),
        ScopeEnforcementManifestExists = !string.IsNullOrWhiteSpace(contract.PlannedScopeEnforcementManifestPath) && realPathExists(contract.PlannedScopeEnforcementManifestPath),
        ActivationRollbackBindingExists = !string.IsNullOrWhiteSpace(contract.PlannedActivationRollbackBindingPath) && realPathExists(contract.PlannedActivationRollbackBindingPath)
    };

    private static GuardedRuntimeActivationArtifactReferenceExistence BuildRealReferenceExistence(
        GuardedRuntimeActivationWriteContract contract,
        Func<string, bool> realPathExists) => new()
    {
        RollbackSnapshotExists = !string.IsNullOrWhiteSpace(contract.ReferencedRollbackSnapshotPath) && realPathExists(contract.ReferencedRollbackSnapshotPath),
        RevocationRecordExists = !string.IsNullOrWhiteSpace(contract.ReferencedRevocationRecordPath) && realPathExists(contract.ReferencedRevocationRecordPath),
        ConfigPatchExists = !string.IsNullOrWhiteSpace(contract.ReferencedConfigPatchSourcePath) && realPathExists(contract.ReferencedConfigPatchSourcePath)
    };

    private static bool TryValidateExistingArtifacts(
        GuardedRuntimeActivationWriteContract contract,
        string expectedGrantId,
        string expectedCapability,
        string expectedScope,
        GuardedRuntimeActivationArtifactPathExistence pathExistence)
    {
        if (!pathExistence.AllExist)
        {
            return false;
        }

        try
        {
            var runtimeSwitch = JsonSerializer.Deserialize<GuardedRuntimeActivationRuntimeSwitchArtifactContent>(File.ReadAllText(contract.PlannedRuntimeSwitchArtifactPath));
            if (runtimeSwitch is null
                || !string.Equals(runtimeSwitch.BoundGrantId, expectedGrantId, StringComparison.Ordinal)
                || !string.Equals(runtimeSwitch.Capability, expectedCapability, StringComparison.Ordinal)
                || !string.Equals(runtimeSwitch.Scope, expectedScope, StringComparison.Ordinal)
                || !string.Equals(runtimeSwitch.SwitchMode, "GuardedArtifactOnly", StringComparison.Ordinal)
                || runtimeSwitch.ApplyToRuntime
                || runtimeSwitch.RuntimeActivation
                || runtimeSwitch.FormalRetrievalAllowed
                || runtimeSwitch.RuntimeSwitchAllowed)
            {
                return false;
            }

            var auditLine = File.ReadLines(contract.PlannedActivationAuditArtifactPath).FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
            if (string.IsNullOrWhiteSpace(auditLine)) return false;
            var auditEvent = JsonSerializer.Deserialize<GuardedRuntimeActivationAuditArtifactEvent>(auditLine);
            if (auditEvent is null
                || !string.Equals(auditEvent.EventType, "GuardedRuntimeActivationArtifactWriteOut", StringComparison.Ordinal)
                || !string.Equals(auditEvent.BoundGrantId, expectedGrantId, StringComparison.Ordinal)
                || !auditEvent.RuntimeActivationArtifactsWritten
                || auditEvent.RuntimeActivation
                || auditEvent.FormalRetrievalAllowed
                || auditEvent.RuntimeSwitchAllowed)
            {
                return false;
            }

            var runtimeGuardManifest = JsonSerializer.Deserialize<GuardedRuntimeActivationRuntimeGuardManifestContent>(File.ReadAllText(contract.PlannedRuntimeGuardManifestPath));
            var scopeEnforcementManifest = JsonSerializer.Deserialize<GuardedRuntimeActivationScopeEnforcementManifestContent>(File.ReadAllText(contract.PlannedScopeEnforcementManifestPath));
            var rollbackBinding = JsonSerializer.Deserialize<GuardedRuntimeActivationRollbackBindingContent>(File.ReadAllText(contract.PlannedActivationRollbackBindingPath));

            return runtimeGuardManifest is not null
                && string.Equals(runtimeGuardManifest.BoundGrantId, expectedGrantId, StringComparison.Ordinal)
                && string.Equals(runtimeGuardManifest.Scope, expectedScope, StringComparison.Ordinal)
                && runtimeGuardManifest.KillSwitchRequired
                && runtimeGuardManifest.ScopeGuardRequired
                && runtimeGuardManifest.RollbackRequired
                && !runtimeGuardManifest.RuntimeActivationAllowed
                && scopeEnforcementManifest is not null
                && string.Equals(scopeEnforcementManifest.BoundGrantId, expectedGrantId, StringComparison.Ordinal)
                && string.Equals(scopeEnforcementManifest.AllowedScope, expectedScope, StringComparison.Ordinal)
                && !scopeEnforcementManifest.GlobalDefaultOn
                && !scopeEnforcementManifest.WildcardScopeAllowed
                && rollbackBinding is not null
                && string.Equals(rollbackBinding.BoundGrantId, expectedGrantId, StringComparison.Ordinal)
                && string.Equals(rollbackBinding.RollbackSnapshotReference, contract.ReferencedRollbackSnapshotPath, StringComparison.Ordinal)
                && string.Equals(rollbackBinding.RevocationRecordReference, contract.ReferencedRevocationRecordPath, StringComparison.Ordinal)
                && rollbackBinding.RestoreTestRequired
                && !rollbackBinding.RuntimeActivation;
        }
        catch
        {
            return false;
        }
    }
    private static IReadOnlyList<GuardedRuntimeActivationArtifactWriteOutScenario> BuildScenarios(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport cleanReport)
    {
        var cleanPaths = new GuardedRuntimeActivationArtifactPathExistence();
        var cleanReferences = new GuardedRuntimeActivationArtifactReferenceExistence
        {
            RollbackSnapshotExists = true,
            RevocationRecordExists = true,
            ConfigPatchExists = true
        };

        return
        [
            new("AllUpstreamClean", cleanReport, true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten, null),
            new("GuardedRuntimeActivationDryRunGateMissing", null, true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.GuardedRuntimeActivationDryRunGateMissing),
            new("GuardedRuntimeActivationDryRunGateNotPassed", Mutate(cleanReport, gatePassed: false), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.GuardedRuntimeActivationDryRunGateNotPassed),
            new("NoGuardedRuntimeActivationDryRunReadyCase", MutateWithoutReadyCase(cleanReport), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.NoGuardedRuntimeActivationDryRunReadyCase),
            new("BoundGrantIdEmpty", Mutate(cleanReport, boundGrantId: string.Empty), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.BoundGrantIdEmpty),
            new("BoundCapabilityMismatch", Mutate(cleanReport, boundCapability: "OtherCapability"), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.BoundCapabilityMismatch),
            new("BoundScopeMismatch", Mutate(cleanReport, boundScope: "other-workspace/other-collection"), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.BoundScopeMismatch),
            new("PlannedArtifactCountNotFive", cleanReport, true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides { ForcePlannedArtifactCountWrong = true }, GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedArtifactCountNotFive),
            new("PlannedArtifactOutsideAllowedDirectory", cleanReport, true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides { ForcePlannedArtifactOutsideDirectory = true }, GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedArtifactOutsideAllowedDirectory),
            new("PlannedArtifactAlreadyExists", cleanReport, true, true, false, false, new GuardedRuntimeActivationArtifactPathExistence { RuntimeSwitchArtifactExists = true }, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedArtifactAlreadyExists),
            new("ReferencedRollbackSnapshotMissing", cleanReport, true, true, false, false, cleanPaths, new GuardedRuntimeActivationArtifactReferenceExistence { RollbackSnapshotExists = false, RevocationRecordExists = true, ConfigPatchExists = true }, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.ReferencedRollbackSnapshotMissing),
            new("ReferencedRevocationRecordMissing", cleanReport, true, true, false, false, cleanPaths, new GuardedRuntimeActivationArtifactReferenceExistence { RollbackSnapshotExists = true, RevocationRecordExists = false, ConfigPatchExists = true }, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.ReferencedRevocationRecordMissing),
            new("ReferencedConfigPatchMissing", cleanReport, true, true, false, false, cleanPaths, new GuardedRuntimeActivationArtifactReferenceExistence { RollbackSnapshotExists = true, RevocationRecordExists = true, ConfigPatchExists = false }, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.ReferencedConfigPatchMissing),
            new("RuntimeActivationWriteAllowedTrueInUpstream", Mutate(cleanReport, runtimeActivationWriteAllowed: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeActivationWriteAllowedTrueInUpstream),
            new("RuntimeActivationTrueInUpstream", Mutate(cleanReport, runtimeActivation: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeActivationTrueInUpstream),
            new("FormalRetrievalAllowedTrueInUpstream", Mutate(cleanReport, formalRetrievalAllowed: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.FormalRetrievalAllowedTrueInUpstream),
            new("RuntimeSwitchAllowedTrueInUpstream", Mutate(cleanReport, runtimeSwitchAllowed: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeSwitchAllowedTrueInUpstream),
            new("PackageOutputChangedTrueInUpstream", Mutate(cleanReport, packageOutputChanged: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PackageOutputChangedTrueInUpstream),
            new("FormalPackageWrittenTrueInUpstream", Mutate(cleanReport, formalPackageWritten: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.FormalPackageWrittenTrueInUpstream),
            new("VectorStoreBindingChangedTrueInUpstream", Mutate(cleanReport, vectorStoreBindingChanged: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.VectorStoreBindingChangedTrueInUpstream),
            new("GlobalDefaultOnTrueInUpstream", Mutate(cleanReport, globalDefaultOn: true), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.GlobalDefaultOnTrueInUpstream),
            new("RuntimeGateNotPassed", cleanReport, false, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanReport, true, false, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", cleanReport, true, true, true, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanReport, true, true, false, true, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.MainlineTrustRegistryPresent),
            new("WriteFailureSimulated", cleanReport, true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides { SimulateWriteFailure = true }, GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.WriteFailureSimulated),
            new("NoRuntimeMutationInvariantFalseInUpstream", Mutate(cleanReport, noRuntimeMutationInvariant: false), true, true, false, false, cleanPaths, cleanReferences, new GuardedRuntimeActivationArtifactWriteOutOverrides(), GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, GuardedRuntimeActivationArtifactWriteOutBlockedReasons.NoRuntimeMutationInvariantFalseInUpstream)
        ];
    }

    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport Mutate(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport baseline,
        bool? gatePassed = null,
        string? boundGrantId = null,
        string? boundCapability = null,
        string? boundScope = null,
        bool? dryRunOnly = null,
        bool? runtimeActivationWriteAllowed = null,
        bool? runtimeActivation = null,
        bool? formalRetrievalAllowed = null,
        bool? runtimeSwitchAllowed = null,
        bool? packageOutputChanged = null,
        bool? formalPackageWritten = null,
        bool? vectorStoreBindingChanged = null,
        bool? globalDefaultOn = null,
        bool? noRuntimeMutationInvariant = null)
        => new()
        {
            OperationId = baseline.OperationId,
            CreatedAt = baseline.CreatedAt,
            GuardedRuntimeActivationDryRunPassed = gatePassed ?? baseline.GuardedRuntimeActivationDryRunPassed,
            GatePassed = gatePassed ?? baseline.GatePassed,
            TotalCases = baseline.TotalCases,
            PassedCases = baseline.PassedCases,
            FailedCases = baseline.FailedCases,
            ReadyCases = baseline.ReadyCases,
            BlockedCases = baseline.BlockedCases,
            Cases = baseline.Cases,
            BoundGrantId = boundGrantId ?? baseline.BoundGrantId,
            BoundCapability = boundCapability ?? baseline.BoundCapability,
            BoundScope = boundScope ?? baseline.BoundScope,
            PlannedGuardedActivationContract = baseline.PlannedGuardedActivationContract,
            UpstreamActivationDryRunGatePresent = baseline.UpstreamActivationDryRunGatePresent,
            UpstreamActivationDryRunGatePassed = gatePassed ?? baseline.UpstreamActivationDryRunGatePassed,
            DryRunOnly = dryRunOnly ?? baseline.DryRunOnly,
            RuntimeActivationWriteAllowed = runtimeActivationWriteAllowed ?? baseline.RuntimeActivationWriteAllowed,
            RuntimeActivation = runtimeActivation ?? baseline.RuntimeActivation,
            FormalRetrievalAllowed = formalRetrievalAllowed ?? baseline.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed ?? baseline.RuntimeSwitchAllowed,
            ConfigPatchAppliedToRuntime = baseline.ConfigPatchAppliedToRuntime,
            PackageOutputChanged = packageOutputChanged ?? baseline.PackageOutputChanged,
            FormalPackageWritten = formalPackageWritten ?? baseline.FormalPackageWritten,
            VectorStoreBindingChanged = vectorStoreBindingChanged ?? baseline.VectorStoreBindingChanged,
            GlobalDefaultOn = globalDefaultOn ?? baseline.GlobalDefaultOn,
            Crossed = baseline.Crossed,
            ArtifactOnly = baseline.ArtifactOnly,
            CapabilityGrantWritten = baseline.CapabilityGrantWritten,
            ConfigPatchWritten = baseline.ConfigPatchWritten,
            RollbackSnapshotWritten = baseline.RollbackSnapshotWritten,
            AuditLogWritten = baseline.AuditLogWritten,
            RevocationRecordWritten = baseline.RevocationRecordWritten,
            ActivationDryRunOnly = baseline.ActivationDryRunOnly,
            RuntimeActivationAllowed = baseline.RuntimeActivationAllowed,
            PackingPolicyChanged = baseline.PackingPolicyChanged,
            EvidenceCopiedToMainline = baseline.EvidenceCopiedToMainline,
            TrustRegistryCopiedToMainline = baseline.TrustRegistryCopiedToMainline,
            MainlineEvidencePresent = baseline.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = baseline.MainlineTrustRegistryPresent,
            ManualReviewRequired = baseline.ManualReviewRequired,
            ApprovalSealed = baseline.ApprovalSealed,
            GrantApplied = baseline.GrantApplied,
            ApplicationApplied = baseline.ApplicationApplied,
            RollbackActivated = baseline.RollbackActivated,
            PromotionToMainlinePerformed = baseline.PromotionToMainlinePerformed,
            NoRuntimeMutationInvariant = noRuntimeMutationInvariant ?? baseline.NoRuntimeMutationInvariant,
            BlockedReasons = baseline.BlockedReasons,
            Diagnostics = baseline.Diagnostics
        };

    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport MutateWithoutReadyCase(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport baseline)
    {
        var clone = Mutate(baseline);
        return new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport
        {
            OperationId = clone.OperationId,
            CreatedAt = clone.CreatedAt,
            GuardedRuntimeActivationDryRunPassed = clone.GuardedRuntimeActivationDryRunPassed,
            GatePassed = clone.GatePassed,
            TotalCases = clone.TotalCases,
            PassedCases = clone.PassedCases,
            FailedCases = clone.FailedCases,
            ReadyCases = 0,
            BlockedCases = clone.BlockedCases,
            Cases = new[]
            {
                new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunCase
                {
                    CaseName = "BlockedOnly",
                    ExpectedStatus = GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                    ActualStatus = GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
                    BoundGrantId = clone.BoundGrantId,
                    BoundCapability = clone.BoundCapability,
                    BoundScope = clone.BoundScope,
                    PlannedGuardedActivationContract = clone.PlannedGuardedActivationContract,
                    DryRunOnly = true,
                    RuntimeActivationWriteAllowed = false,
                    RuntimeActivation = false,
                    FormalRetrievalAllowed = false,
                    RuntimeSwitchAllowed = false,
                    PackageOutputChanged = false,
                    PassedAsExpected = true
                }
            },
            BoundGrantId = clone.BoundGrantId,
            BoundCapability = clone.BoundCapability,
            BoundScope = clone.BoundScope,
            PlannedGuardedActivationContract = clone.PlannedGuardedActivationContract,
            UpstreamActivationDryRunGatePresent = clone.UpstreamActivationDryRunGatePresent,
            UpstreamActivationDryRunGatePassed = clone.UpstreamActivationDryRunGatePassed,
            DryRunOnly = clone.DryRunOnly,
            RuntimeActivationWriteAllowed = clone.RuntimeActivationWriteAllowed,
            RuntimeActivation = clone.RuntimeActivation,
            FormalRetrievalAllowed = clone.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = clone.RuntimeSwitchAllowed,
            ConfigPatchAppliedToRuntime = clone.ConfigPatchAppliedToRuntime,
            PackageOutputChanged = clone.PackageOutputChanged,
            FormalPackageWritten = clone.FormalPackageWritten,
            VectorStoreBindingChanged = clone.VectorStoreBindingChanged,
            GlobalDefaultOn = clone.GlobalDefaultOn,
            Crossed = clone.Crossed,
            ArtifactOnly = clone.ArtifactOnly,
            CapabilityGrantWritten = clone.CapabilityGrantWritten,
            ConfigPatchWritten = clone.ConfigPatchWritten,
            RollbackSnapshotWritten = clone.RollbackSnapshotWritten,
            AuditLogWritten = clone.AuditLogWritten,
            RevocationRecordWritten = clone.RevocationRecordWritten,
            ActivationDryRunOnly = clone.ActivationDryRunOnly,
            RuntimeActivationAllowed = clone.RuntimeActivationAllowed,
            PackingPolicyChanged = clone.PackingPolicyChanged,
            EvidenceCopiedToMainline = clone.EvidenceCopiedToMainline,
            TrustRegistryCopiedToMainline = clone.TrustRegistryCopiedToMainline,
            MainlineEvidencePresent = clone.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = clone.MainlineTrustRegistryPresent,
            ManualReviewRequired = clone.ManualReviewRequired,
            ApprovalSealed = clone.ApprovalSealed,
            GrantApplied = clone.GrantApplied,
            ApplicationApplied = clone.ApplicationApplied,
            RollbackActivated = clone.RollbackActivated,
            PromotionToMainlinePerformed = clone.PromotionToMainlinePerformed,
            NoRuntimeMutationInvariant = clone.NoRuntimeMutationInvariant,
            BlockedReasons = clone.BlockedReasons,
            Diagnostics = clone.Diagnostics
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- GuardedRuntimeActivationArtifactWriteOutPassed: `{r.GuardedRuntimeActivationArtifactWriteOutPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- TotalCases: `{r.TotalCases}`");
        b.AppendLine($"- PassedCases: `{r.PassedCases}`");
        b.AppendLine($"- FailedCases: `{r.FailedCases}`");
        b.AppendLine($"- WrittenCases: `{r.WrittenCases}`");
        b.AppendLine($"- BlockedCases: `{r.BlockedCases}`");
        b.AppendLine($"- WrittenArtifactCount: `{r.WrittenArtifactPaths.Count}`");
        b.AppendLine($"- RuntimeActivationArtifactsWritten: `{r.RuntimeActivationArtifactsWritten}`");
        b.AppendLine();
        b.AppendLine("## Binding");
        b.AppendLine($"- BoundGrantId: `{r.BoundGrantId}`");
        b.AppendLine($"- BoundCapability: `{r.BoundCapability}`");
        b.AppendLine($"- BoundScope: `{r.BoundScope}`");
        b.AppendLine($"- UpstreamGuardedRuntimeActivationDryRunGatePresent: `{r.UpstreamGuardedRuntimeActivationDryRunGatePresent}`");
        b.AppendLine($"- UpstreamGuardedRuntimeActivationDryRunGatePassed: `{r.UpstreamGuardedRuntimeActivationDryRunGatePassed}`");
        b.AppendLine();
        b.AppendLine("## Safety");
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
        b.AppendLine($"- PromotionToMainlinePerformed: `{r.PromotionToMainlinePerformed}`");
        b.AppendLine($"- MainlineEvidencePresent: `{r.MainlineEvidencePresent}`");
        b.AppendLine($"- MainlineTrustRegistryPresent: `{r.MainlineTrustRegistryPresent}`");
        b.AppendLine();

        if (r.WrittenArtifactPaths.Count > 0)
        {
            b.AppendLine("## Written Artifacts");
            foreach (var path in r.WrittenArtifactPaths)
            {
                b.AppendLine($"- `{path}`");
            }

            b.AppendLine();
        }

        b.AppendLine("## Cases");
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
            foreach (var blockedReason in r.BlockedReasons)
            {
                b.AppendLine($"- `{blockedReason}`");
            }

            b.AppendLine();
        }

        b.AppendLine("V8.21 只写 runtime-activation evidence artifacts。runtime switch、formal retrieval、formal package、vector store binding、package output、PackingPolicy 都保持不变。");
        return b.ToString();
    }
}

public sealed record GuardedRuntimeActivationArtifactWriteOutScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? GuardedRuntimeActivationDryRunReport,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    GuardedRuntimeActivationArtifactPathExistence PathExistence,
    GuardedRuntimeActivationArtifactReferenceExistence ReferenceExistence,
    GuardedRuntimeActivationArtifactWriteOutOverrides Overrides,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public IReadOnlyList<string> PlannedArtifactPaths { get; init; } = Array.Empty<string>();
    public bool RuntimeActivationArtifactsWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool PackageOutputChanged { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GuardedRuntimeActivationArtifactWriteOutPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int WrittenCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutCase>();

    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public GuardedRuntimeActivationWriteContract PlannedGuardedActivationContract { get; init; } = new();
    public bool UpstreamGuardedRuntimeActivationDryRunGatePresent { get; init; }
    public bool UpstreamGuardedRuntimeActivationDryRunGatePassed { get; init; }
    public IReadOnlyList<string> WrittenArtifactPaths { get; init; } = Array.Empty<string>();
    public bool RuntimeActivationArtifactsWritten { get; init; }

    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }

    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }
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

public sealed class FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
