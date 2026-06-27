namespace ContextCore.Core.Services;

/// <summary>V8.20 guarded runtime activation gate dry-run 状态。</summary>
public static class GuardedRuntimeActivationDryRunStatuses
{
    /// <summary>所有 binding + plan + carry 一致；write contract 已规划。仍不写。</summary>
    public const string GuardedRuntimeActivationDryRunReady = nameof(GuardedRuntimeActivationDryRunReady);

    /// <summary>至少一个 binding / plan / carry 失败。</summary>
    public const string GuardedRuntimeActivationDryRunBlocked = nameof(GuardedRuntimeActivationDryRunBlocked);
}

/// <summary>V8.20 阻塞原因常量。</summary>
public static class GuardedRuntimeActivationDryRunBlockedReasons
{
    // upstream V8.19R presence/state
    public const string ActivationDryRunGateMissing = nameof(ActivationDryRunGateMissing);
    public const string ActivationDryRunGateNotPassed = nameof(ActivationDryRunGateNotPassed);
    public const string NoRuntimeActivationDryRunReadyCase = nameof(NoRuntimeActivationDryRunReadyCase);
    public const string BoundGrantIdEmpty = nameof(BoundGrantIdEmpty);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);

    // V8.19R contract violations
    public const string ActivationDryRunOnlyFalseInUpstream = nameof(ActivationDryRunOnlyFalseInUpstream);
    public const string RuntimeActivationAllowedTrueInUpstream = nameof(RuntimeActivationAllowedTrueInUpstream);
    public const string RuntimeActivationTrueInUpstream = nameof(RuntimeActivationTrueInUpstream);
    public const string FormalRetrievalAllowedTrueInUpstream = nameof(FormalRetrievalAllowedTrueInUpstream);
    public const string RuntimeSwitchAllowedTrueInUpstream = nameof(RuntimeSwitchAllowedTrueInUpstream);
    public const string ConfigPatchAppliedToRuntimeTrueInUpstream = nameof(ConfigPatchAppliedToRuntimeTrueInUpstream);

    // V8.18 carry through V8.19R
    public const string CrossedFalseInUpstream = nameof(CrossedFalseInUpstream);
    public const string ArtifactOnlyFalseInUpstream = nameof(ArtifactOnlyFalseInUpstream);
    public const string CapabilityGrantWrittenFalseInUpstream = nameof(CapabilityGrantWrittenFalseInUpstream);
    public const string ConfigPatchWrittenFalseInUpstream = nameof(ConfigPatchWrittenFalseInUpstream);
    public const string RollbackSnapshotWrittenFalseInUpstream = nameof(RollbackSnapshotWrittenFalseInUpstream);
    public const string AuditLogWrittenFalseInUpstream = nameof(AuditLogWrittenFalseInUpstream);
    public const string RevocationRecordWrittenFalseInUpstream = nameof(RevocationRecordWrittenFalseInUpstream);

    // planned contract violations
    public const string PlannedActivationModeNotGuardedScopeOnly = nameof(PlannedActivationModeNotGuardedScopeOnly);
    public const string PlannedRuntimeSwitchPathOutsideAllowedDirectory = nameof(PlannedRuntimeSwitchPathOutsideAllowedDirectory);
    public const string PlannedActivationAuditPathOutsideAllowedDirectory = nameof(PlannedActivationAuditPathOutsideAllowedDirectory);
    public const string PlannedRollbackReferenceMissing = nameof(PlannedRollbackReferenceMissing);
    public const string PlannedRevocationReferenceMissing = nameof(PlannedRevocationReferenceMissing);

    // runtime / p15 / mainline / safety
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string PackageOutputChanged = nameof(PackageOutputChanged);
    public const string FormalPackageWritten = nameof(FormalPackageWritten);
    public const string VectorStoreBindingChanged = nameof(VectorStoreBindingChanged);
    public const string GlobalDefaultOn = nameof(GlobalDefaultOn);
}

/// <summary>V8.20 允许写出的 artifact 目录。</summary>
public static class GuardedRuntimeActivationAllowedDirectory
{
    public const string Value = "vector/v8/runtime-activation";

    public static bool IsUnder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith(Value + "/", StringComparison.Ordinal);
    }
}

/// <summary>V8.20 guarded runtime activation write contract — plan only, never written。</summary>
public sealed class GuardedRuntimeActivationWriteContract
{
    public string PlannedRuntimeActivationMode { get; init; } = "GuardedScopeOnly";
    public string PlannedCapability { get; init; } = string.Empty;
    public string PlannedScope { get; init; } = string.Empty;
    public string PlannedRuntimeSwitchArtifactPath { get; init; } = string.Empty;
    public string PlannedActivationAuditArtifactPath { get; init; } = string.Empty;
    public string PlannedRuntimeGuardManifestPath { get; init; } = string.Empty;
    public string PlannedScopeEnforcementManifestPath { get; init; } = string.Empty;
    public string PlannedActivationRollbackBindingPath { get; init; } = string.Empty;
    public string ReferencedRollbackSnapshotPath { get; init; } = string.Empty;
    public string ReferencedRevocationRecordPath { get; init; } = string.Empty;
    public string ReferencedConfigPatchSourcePath { get; init; } = string.Empty;
}

/// <summary>V8.20 dry-run decision。RuntimeActivationWriteAllowed 永远 false。</summary>
public sealed class GuardedRuntimeActivationDryRunDecision
{
    public string Status { get; init; } = GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public GuardedRuntimeActivationWriteContract PlannedGuardedActivationContract { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    public bool DryRunOnly { get; init; } = true;

    /// <summary>V8.20 核心 invariant — 不允许执行 write-out。</summary>
    public bool RuntimeActivationWriteAllowed { get; init; }

    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }

    // carry from V8.18 → V8.19R
    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }

    // carry from V8.19R
    public bool ActivationDryRunOnly { get; init; }
    public bool RuntimeActivationAllowed { get; init; }
}

/// <summary>
/// V8.20 guarded runtime activation gate dry-run policy。
/// 输入：V8.19R RuntimeActivationDryRunReport + rt/p15/mainline。
/// 输出：GuardedRuntimeActivationDryRunDecision + write contract。永远不写、不激活。
/// </summary>
public static class FormalRetrievalPromotionApprovalGuardedRuntimeActivationGatePolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static GuardedRuntimeActivationDryRunDecision Evaluate(
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? activationDryRunReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();

        if (activationDryRunReport is null)
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.ActivationDryRunGateMissing);
            return BuildBlocked(string.Empty, string.Empty, string.Empty, new GuardedRuntimeActivationWriteContract(), blocked,
                "V8.19R activation dry-run report missing; guarded runtime activation dry-run cannot proceed.",
                upstream: null);
        }

        if (!activationDryRunReport.GatePassed || !activationDryRunReport.RuntimeActivationDryRunPassed)
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.ActivationDryRunGateNotPassed);
        }

        var readyCase = activationDryRunReport.Cases?
            .FirstOrDefault(c => string.Equals(c.ActualStatus, RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady, StringComparison.Ordinal));
        if (readyCase is null)
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.NoRuntimeActivationDryRunReadyCase);
        }

        // upstream binding
        var boundGrantId = activationDryRunReport.BoundGrantId ?? string.Empty;
        var boundCapability = activationDryRunReport.BoundCapability ?? string.Empty;
        var boundScope = activationDryRunReport.BoundScope ?? string.Empty;

        if (string.IsNullOrWhiteSpace(boundGrantId))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.BoundGrantIdEmpty);
        }

        if (!string.Equals(boundCapability, AllowedCapability, StringComparison.Ordinal))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.BoundCapabilityMismatch);
        }

        if (!string.Equals(boundScope, AllowedScope, StringComparison.Ordinal))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.BoundScopeMismatch);
        }

        // V8.19R contract invariants — all must be false / dry-run-only
        if (!activationDryRunReport.ActivationDryRunOnly) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.ActivationDryRunOnlyFalseInUpstream);
        if (activationDryRunReport.RuntimeActivationAllowed) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.RuntimeActivationAllowedTrueInUpstream);
        if (activationDryRunReport.RuntimeActivation) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.RuntimeActivationTrueInUpstream);
        if (activationDryRunReport.FormalRetrievalAllowed) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream);
        if (activationDryRunReport.RuntimeSwitchAllowed) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream);
        if (activationDryRunReport.ConfigPatchAppliedToRuntime) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.ConfigPatchAppliedToRuntimeTrueInUpstream);

        // V8.18 carry — Crossed=true 必须保留，5 个 written flag 必须 true。
        if (!activationDryRunReport.Crossed) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.CrossedFalseInUpstream);
        if (!activationDryRunReport.ArtifactOnly) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.ArtifactOnlyFalseInUpstream);
        if (!activationDryRunReport.CapabilityGrantWritten) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.CapabilityGrantWrittenFalseInUpstream);
        if (!activationDryRunReport.ConfigPatchWritten) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.ConfigPatchWrittenFalseInUpstream);
        if (!activationDryRunReport.RollbackSnapshotWritten) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.RollbackSnapshotWrittenFalseInUpstream);
        if (!activationDryRunReport.AuditLogWritten) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.AuditLogWrittenFalseInUpstream);
        if (!activationDryRunReport.RevocationRecordWritten) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.RevocationRecordWrittenFalseInUpstream);

        // V8.19R planned activation contract validity
        var v819RContract = activationDryRunReport.PlannedActivationContract ?? new RuntimeActivationPlannedContract();
        if (!string.Equals(v819RContract.PlannedRuntimeActivationMode, "GuardedScopeOnly", StringComparison.Ordinal))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.PlannedActivationModeNotGuardedScopeOnly);
        }

        if (!GuardedRuntimeActivationAllowedDirectory.IsUnder(v819RContract.PlannedRuntimeSwitchPath))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.PlannedRuntimeSwitchPathOutsideAllowedDirectory);
        }

        if (!GuardedRuntimeActivationAllowedDirectory.IsUnder(v819RContract.PlannedActivationAuditPath))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.PlannedActivationAuditPathOutsideAllowedDirectory);
        }

        if (string.IsNullOrWhiteSpace(v819RContract.PlannedRollbackReference))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.PlannedRollbackReferenceMissing);
        }

        if (string.IsNullOrWhiteSpace(v819RContract.PlannedRevocationReference))
        {
            blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.PlannedRevocationReferenceMissing);
        }

        // safety/runtime/p15/mainline
        if (!rtPassed) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.MainlineTrustRegistryPresent);
        if (activationDryRunReport.PackageOutputChanged) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.PackageOutputChanged);
        if (activationDryRunReport.FormalPackageWritten) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.FormalPackageWritten);
        if (activationDryRunReport.VectorStoreBindingChanged) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.VectorStoreBindingChanged);
        if (activationDryRunReport.GlobalDefaultOn) blocked.Add(GuardedRuntimeActivationDryRunBlockedReasons.GlobalDefaultOn);

        // 构造 V8.20 自己的 planned write contract — 5 个 planned path + 3 个 referenced path。
        var safeCapability = NormalizeForPath(boundCapability);
        var safeScope = NormalizeForPath(boundScope);
        const string baseDir = "vector/v8/runtime-activation";
        var contract = new GuardedRuntimeActivationWriteContract
        {
            PlannedRuntimeActivationMode = "GuardedScopeOnly",
            PlannedCapability = boundCapability,
            PlannedScope = boundScope,
            PlannedRuntimeSwitchArtifactPath = $"{baseDir}/runtime-switch-{safeCapability}-{safeScope}.json",
            PlannedActivationAuditArtifactPath = $"{baseDir}/activation-audit-{safeCapability}-{safeScope}.jsonl",
            PlannedRuntimeGuardManifestPath = $"{baseDir}/runtime-guard-manifest-{safeCapability}-{safeScope}.json",
            PlannedScopeEnforcementManifestPath = $"{baseDir}/scope-enforcement-manifest-{safeCapability}-{safeScope}.json",
            PlannedActivationRollbackBindingPath = $"{baseDir}/activation-rollback-binding-{safeCapability}-{safeScope}.json",
            ReferencedRollbackSnapshotPath = v819RContract.PlannedRollbackReference ?? string.Empty,
            ReferencedRevocationRecordPath = v819RContract.PlannedRevocationReference ?? string.Empty,
            ReferencedConfigPatchSourcePath = v819RContract.PlannedConfigPatchSourcePath ?? string.Empty
        };

        var distinctBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = distinctBlocked.Length == 0;
        var status = ready
            ? GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunReady
            : GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked;
        var reasoning = ready
            ? $"all upstream V8.19R contracts + V8.18 carry consistent; grant '{boundGrantId}' capability '{boundCapability}' scope '{boundScope}'. 5 write paths planned under {GuardedRuntimeActivationAllowedDirectory.Value}/. RuntimeActivationWriteAllowed=false; runtime untouched."
            : $"{distinctBlocked.Length} blocked reason(s); guarded runtime activation dry-run blocked.";

        return new GuardedRuntimeActivationDryRunDecision
        {
            Status = status,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            PlannedGuardedActivationContract = contract,
            BlockedReasons = distinctBlocked,
            Reasoning = reasoning,
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
            // carry from upstream — Crossed=true 在 V8.18 后是事实
            Crossed = activationDryRunReport.Crossed,
            ArtifactOnly = activationDryRunReport.ArtifactOnly,
            CapabilityGrantWritten = activationDryRunReport.CapabilityGrantWritten,
            ConfigPatchWritten = activationDryRunReport.ConfigPatchWritten,
            RollbackSnapshotWritten = activationDryRunReport.RollbackSnapshotWritten,
            AuditLogWritten = activationDryRunReport.AuditLogWritten,
            RevocationRecordWritten = activationDryRunReport.RevocationRecordWritten,
            ActivationDryRunOnly = activationDryRunReport.ActivationDryRunOnly,
            RuntimeActivationAllowed = activationDryRunReport.RuntimeActivationAllowed
        };
    }

    private static string NormalizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "missing";
        return new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    }

    private static GuardedRuntimeActivationDryRunDecision BuildBlocked(
        string boundGrantId, string boundCapability, string boundScope,
        GuardedRuntimeActivationWriteContract contract,
        List<string> blocked,
        string reasoning,
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? upstream)
    {
        var distinct = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new GuardedRuntimeActivationDryRunDecision
        {
            Status = GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            PlannedGuardedActivationContract = contract,
            BlockedReasons = distinct,
            Reasoning = reasoning,
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
            Crossed = upstream?.Crossed ?? false,
            ArtifactOnly = upstream?.ArtifactOnly ?? true,
            CapabilityGrantWritten = upstream?.CapabilityGrantWritten ?? false,
            ConfigPatchWritten = upstream?.ConfigPatchWritten ?? false,
            RollbackSnapshotWritten = upstream?.RollbackSnapshotWritten ?? false,
            AuditLogWritten = upstream?.AuditLogWritten ?? false,
            RevocationRecordWritten = upstream?.RevocationRecordWritten ?? false,
            ActivationDryRunOnly = upstream?.ActivationDryRunOnly ?? true,
            RuntimeActivationAllowed = upstream?.RuntimeActivationAllowed ?? false
        };
    }
}
