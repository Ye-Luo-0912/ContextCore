namespace ContextCore.Core.Services;

/// <summary>V8.21 guarded runtime activation artifact write-out 状态。</summary>
public static class GuardedRuntimeActivationArtifactWriteOutStatuses
{
    /// <summary>5 个 runtime-activation artifact 已写出，但 live runtime 仍未切换。</summary>
    public const string GuardedRuntimeActivationArtifactsWritten = nameof(GuardedRuntimeActivationArtifactsWritten);

    /// <summary>至少一个 precondition 失败，或写出被阻断。</summary>
    public const string GuardedRuntimeActivationArtifactWriteOutBlocked = nameof(GuardedRuntimeActivationArtifactWriteOutBlocked);
}

/// <summary>V8.21 阻塞原因常量。</summary>
public static class GuardedRuntimeActivationArtifactWriteOutBlockedReasons
{
    public const string GuardedRuntimeActivationDryRunGateMissing = nameof(GuardedRuntimeActivationDryRunGateMissing);
    public const string GuardedRuntimeActivationDryRunGateNotPassed = nameof(GuardedRuntimeActivationDryRunGateNotPassed);
    public const string NoGuardedRuntimeActivationDryRunReadyCase = nameof(NoGuardedRuntimeActivationDryRunReadyCase);
    public const string BoundGrantIdEmpty = nameof(BoundGrantIdEmpty);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);
    public const string DryRunOnlyFalseInUpstream = nameof(DryRunOnlyFalseInUpstream);
    public const string RuntimeActivationWriteAllowedTrueInUpstream = nameof(RuntimeActivationWriteAllowedTrueInUpstream);
    public const string RuntimeActivationTrueInUpstream = nameof(RuntimeActivationTrueInUpstream);
    public const string FormalRetrievalAllowedTrueInUpstream = nameof(FormalRetrievalAllowedTrueInUpstream);
    public const string RuntimeSwitchAllowedTrueInUpstream = nameof(RuntimeSwitchAllowedTrueInUpstream);
    public const string PackageOutputChangedTrueInUpstream = nameof(PackageOutputChangedTrueInUpstream);
    public const string FormalPackageWrittenTrueInUpstream = nameof(FormalPackageWrittenTrueInUpstream);
    public const string VectorStoreBindingChangedTrueInUpstream = nameof(VectorStoreBindingChangedTrueInUpstream);
    public const string GlobalDefaultOnTrueInUpstream = nameof(GlobalDefaultOnTrueInUpstream);
    public const string NoRuntimeMutationInvariantFalseInUpstream = nameof(NoRuntimeMutationInvariantFalseInUpstream);
    public const string PlannedActivationModeNotGuardedScopeOnly = nameof(PlannedActivationModeNotGuardedScopeOnly);
    public const string PlannedArtifactCountNotFive = nameof(PlannedArtifactCountNotFive);
    public const string PlannedArtifactOutsideAllowedDirectory = nameof(PlannedArtifactOutsideAllowedDirectory);
    public const string PlannedArtifactAlreadyExists = nameof(PlannedArtifactAlreadyExists);
    public const string ReferencedRollbackSnapshotMissing = nameof(ReferencedRollbackSnapshotMissing);
    public const string ReferencedRevocationRecordMissing = nameof(ReferencedRevocationRecordMissing);
    public const string ReferencedConfigPatchMissing = nameof(ReferencedConfigPatchMissing);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string WriteFailureSimulated = nameof(WriteFailureSimulated);
}

/// <summary>V8.21 计划写出路径的存在性快照。</summary>
public sealed class GuardedRuntimeActivationArtifactPathExistence
{
    public bool RuntimeSwitchArtifactExists { get; init; }
    public bool ActivationAuditArtifactExists { get; init; }
    public bool RuntimeGuardManifestExists { get; init; }
    public bool ScopeEnforcementManifestExists { get; init; }
    public bool ActivationRollbackBindingExists { get; init; }

    public bool AnyExists => RuntimeSwitchArtifactExists
        || ActivationAuditArtifactExists
        || RuntimeGuardManifestExists
        || ScopeEnforcementManifestExists
        || ActivationRollbackBindingExists;

    public bool AllExist => RuntimeSwitchArtifactExists
        && ActivationAuditArtifactExists
        && RuntimeGuardManifestExists
        && ScopeEnforcementManifestExists
        && ActivationRollbackBindingExists;
}

/// <summary>V8.21 引用的 V8.18 artifact 是否存在。</summary>
public sealed class GuardedRuntimeActivationArtifactReferenceExistence
{
    public bool RollbackSnapshotExists { get; init; }
    public bool RevocationRecordExists { get; init; }
    public bool ConfigPatchExists { get; init; }
}

/// <summary>V8.21 matrix 覆盖注入项。</summary>
public sealed class GuardedRuntimeActivationArtifactWriteOutOverrides
{
    public bool ForcePlannedArtifactCountWrong { get; init; }
    public bool ForcePlannedArtifactOutsideDirectory { get; init; }
    public bool SimulateWriteFailure { get; init; }
}

/// <summary>V8.21 write-out decision。允许写 artifact，但仍不激活 runtime。</summary>
public sealed class GuardedRuntimeActivationArtifactWriteOutDecision
{
    public string Status { get; init; } = GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string SourceGuardedRuntimeActivationDryRunOperationId { get; init; } = string.Empty;
    public GuardedRuntimeActivationWriteContract PlannedGuardedActivationContract { get; init; } = new();
    public IReadOnlyList<string> PlannedArtifactPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

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
    public bool ArtifactOnly { get; init; } = true;
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; } = true;
}

/// <summary>
/// V8.21 guarded runtime activation artifact write-out policy。
/// 只允许写 runtime-activation 目录下 5 个 artifact，runtime/live switch 一律保持关闭。
/// </summary>
public static class FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static GuardedRuntimeActivationArtifactWriteOutDecision Evaluate(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? guardedDryRunReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        GuardedRuntimeActivationArtifactPathExistence pathExistence,
        GuardedRuntimeActivationArtifactReferenceExistence referenceExistence,
        GuardedRuntimeActivationArtifactWriteOutOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(pathExistence);
        ArgumentNullException.ThrowIfNull(referenceExistence);
        overrides ??= new GuardedRuntimeActivationArtifactWriteOutOverrides();

        var blocked = new List<string>();
        if (guardedDryRunReport is null)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.GuardedRuntimeActivationDryRunGateMissing);
            return BuildBlocked(string.Empty, string.Empty, string.Empty, string.Empty, new GuardedRuntimeActivationWriteContract(), Array.Empty<string>(), blocked,
                "V8.20 guarded runtime activation dry-run gate artifact missing.",
                upstream: null);
        }

        if (!guardedDryRunReport.GatePassed || !guardedDryRunReport.GuardedRuntimeActivationDryRunPassed)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.GuardedRuntimeActivationDryRunGateNotPassed);
        }

        var readyCase = guardedDryRunReport.Cases?
            .FirstOrDefault(c => string.Equals(c.ActualStatus, GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady, StringComparison.Ordinal));
        if (readyCase is null)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.NoGuardedRuntimeActivationDryRunReadyCase);
        }

        var boundGrantId = guardedDryRunReport.BoundGrantId ?? string.Empty;
        var boundCapability = guardedDryRunReport.BoundCapability ?? string.Empty;
        var boundScope = guardedDryRunReport.BoundScope ?? string.Empty;
        var contract = guardedDryRunReport.PlannedGuardedActivationContract ?? new GuardedRuntimeActivationWriteContract();
        var sourceOperationId = guardedDryRunReport.OperationId ?? string.Empty;

        if (string.IsNullOrWhiteSpace(boundGrantId))
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.BoundGrantIdEmpty);
        }

        if (!string.Equals(boundCapability, AllowedCapability, StringComparison.Ordinal))
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.BoundCapabilityMismatch);
        }

        if (!string.Equals(boundScope, AllowedScope, StringComparison.Ordinal))
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.BoundScopeMismatch);
        }

        if (!guardedDryRunReport.DryRunOnly)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.DryRunOnlyFalseInUpstream);
        }

        if (guardedDryRunReport.RuntimeActivationWriteAllowed)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeActivationWriteAllowedTrueInUpstream);
        }

        if (guardedDryRunReport.RuntimeActivation)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeActivationTrueInUpstream);
        }

        if (guardedDryRunReport.FormalRetrievalAllowed)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.FormalRetrievalAllowedTrueInUpstream);
        }

        if (guardedDryRunReport.RuntimeSwitchAllowed)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeSwitchAllowedTrueInUpstream);
        }

        if (guardedDryRunReport.PackageOutputChanged)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PackageOutputChangedTrueInUpstream);
        }

        if (guardedDryRunReport.FormalPackageWritten)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.FormalPackageWrittenTrueInUpstream);
        }

        if (guardedDryRunReport.VectorStoreBindingChanged)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.VectorStoreBindingChangedTrueInUpstream);
        }

        if (guardedDryRunReport.GlobalDefaultOn)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.GlobalDefaultOnTrueInUpstream);
        }

        if (!guardedDryRunReport.NoRuntimeMutationInvariant)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.NoRuntimeMutationInvariantFalseInUpstream);
        }

        if (!string.Equals(contract.PlannedRuntimeActivationMode, "GuardedScopeOnly", StringComparison.Ordinal))
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedActivationModeNotGuardedScopeOnly);
        }

        var plannedPaths = BuildPlannedArtifactPaths(contract, overrides);
        if (plannedPaths.Count != 5)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedArtifactCountNotFive);
        }

        if (plannedPaths.Any(static path => !GuardedRuntimeActivationAllowedDirectory.IsUnder(path)))
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedArtifactOutsideAllowedDirectory);
        }

        if (pathExistence.AnyExists)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.PlannedArtifactAlreadyExists);
        }

        if (string.IsNullOrWhiteSpace(contract.ReferencedRollbackSnapshotPath) || !referenceExistence.RollbackSnapshotExists)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.ReferencedRollbackSnapshotMissing);
        }

        if (string.IsNullOrWhiteSpace(contract.ReferencedRevocationRecordPath) || !referenceExistence.RevocationRecordExists)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.ReferencedRevocationRecordMissing);
        }

        if (string.IsNullOrWhiteSpace(contract.ReferencedConfigPatchSourcePath) || !referenceExistence.ConfigPatchExists)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.ReferencedConfigPatchMissing);
        }

        if (!rtPassed)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.RuntimeChangeGateNotPassed);
        }

        if (!p15Passed)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.P15GateNotPassed);
        }

        if (mainlineEvidencePresent)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.MainlineEvidencePresent);
        }

        if (mainlineRegistryPresent)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.MainlineTrustRegistryPresent);
        }

        if (overrides.SimulateWriteFailure)
        {
            blocked.Add(GuardedRuntimeActivationArtifactWriteOutBlockedReasons.WriteFailureSimulated);
        }

        var distinctBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var written = distinctBlocked.Length == 0;
        var status = written
            ? GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten
            : GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked;
        var reasoning = written
            ? $"all preconditions met; 5 runtime-activation artifacts can be written under {GuardedRuntimeActivationAllowedDirectory.Value}/ while RuntimeActivation remains false."
            : $"{distinctBlocked.Length} blocked reason(s); guarded runtime activation artifact write-out blocked.";

        return new GuardedRuntimeActivationArtifactWriteOutDecision
        {
            Status = status,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            SourceGuardedRuntimeActivationDryRunOperationId = sourceOperationId,
            PlannedGuardedActivationContract = contract,
            PlannedArtifactPaths = plannedPaths.ToArray(),
            BlockedReasons = distinctBlocked,
            Reasoning = reasoning,
            RuntimeActivationArtifactsWritten = written,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            Crossed = guardedDryRunReport.Crossed,
            ArtifactOnly = true,
            CapabilityGrantWritten = guardedDryRunReport.CapabilityGrantWritten,
            ConfigPatchWritten = guardedDryRunReport.ConfigPatchWritten,
            RollbackSnapshotWritten = guardedDryRunReport.RollbackSnapshotWritten,
            AuditLogWritten = guardedDryRunReport.AuditLogWritten,
            RevocationRecordWritten = guardedDryRunReport.RevocationRecordWritten,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            NoRuntimeMutationInvariant = true
        };
    }

    private static List<string> BuildPlannedArtifactPaths(
        GuardedRuntimeActivationWriteContract contract,
        GuardedRuntimeActivationArtifactWriteOutOverrides overrides)
    {
        var paths = new List<string>(5)
        {
            contract.PlannedRuntimeSwitchArtifactPath ?? string.Empty,
            contract.PlannedActivationAuditArtifactPath ?? string.Empty,
            contract.PlannedRuntimeGuardManifestPath ?? string.Empty,
            contract.PlannedScopeEnforcementManifestPath ?? string.Empty,
            contract.PlannedActivationRollbackBindingPath ?? string.Empty
        };

        if (overrides.ForcePlannedArtifactCountWrong && paths.Count > 0)
        {
            paths.RemoveAt(paths.Count - 1);
        }

        if (overrides.ForcePlannedArtifactOutsideDirectory && paths.Count > 0)
        {
            paths[0] = "vector/v8/elsewhere/" + Path.GetFileName(paths[0]);
        }

        return paths;
    }

    private static GuardedRuntimeActivationArtifactWriteOutDecision BuildBlocked(
        string boundGrantId,
        string boundCapability,
        string boundScope,
        string sourceOperationId,
        GuardedRuntimeActivationWriteContract contract,
        IReadOnlyList<string> plannedPaths,
        List<string> blocked,
        string reasoning,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? upstream)
    {
        var distinct = blocked.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        return new GuardedRuntimeActivationArtifactWriteOutDecision
        {
            Status = GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            SourceGuardedRuntimeActivationDryRunOperationId = sourceOperationId,
            PlannedGuardedActivationContract = contract,
            PlannedArtifactPaths = plannedPaths,
            BlockedReasons = distinct,
            Reasoning = reasoning,
            RuntimeActivationArtifactsWritten = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            Crossed = upstream?.Crossed ?? false,
            ArtifactOnly = true,
            CapabilityGrantWritten = upstream?.CapabilityGrantWritten ?? false,
            ConfigPatchWritten = upstream?.ConfigPatchWritten ?? false,
            RollbackSnapshotWritten = upstream?.RollbackSnapshotWritten ?? false,
            AuditLogWritten = upstream?.AuditLogWritten ?? false,
            RevocationRecordWritten = upstream?.RevocationRecordWritten ?? false,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = false,
            MainlineTrustRegistryPresent = false,
            NoRuntimeMutationInvariant = true
        };
    }
}
