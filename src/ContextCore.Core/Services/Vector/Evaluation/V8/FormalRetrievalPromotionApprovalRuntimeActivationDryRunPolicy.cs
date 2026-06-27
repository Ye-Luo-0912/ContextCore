namespace ContextCore.Core.Services;

/// <summary>V8.19 runtime activation dry-run 状态。</summary>
public static class RuntimeActivationDryRunStatuses
{
    /// <summary>所有 binding 一致、所有契约满足；activation plan 已规划。仍未激活 runtime。</summary>
    public const string RuntimeActivationDryRunReady = nameof(RuntimeActivationDryRunReady);

    /// <summary>至少一个 binding/contract 失败。</summary>
    public const string RuntimeActivationDryRunBlocked = nameof(RuntimeActivationDryRunBlocked);
}

/// <summary>V8.19 阻塞原因常量。</summary>
public static class RuntimeActivationDryRunBlockedReasons
{
    public const string CrossingExecutionGateMissing = nameof(CrossingExecutionGateMissing);
    public const string CrossingExecutionGateNotPassed = nameof(CrossingExecutionGateNotPassed);
    public const string CrossingNotTrueInUpstream = nameof(CrossingNotTrueInUpstream);
    public const string ArtifactOnlyFalseInUpstream = nameof(ArtifactOnlyFalseInUpstream);
    public const string CapabilityGrantWrittenFalseInUpstream = nameof(CapabilityGrantWrittenFalseInUpstream);
    public const string ConfigPatchWrittenFalseInUpstream = nameof(ConfigPatchWrittenFalseInUpstream);
    public const string RollbackSnapshotWrittenFalseInUpstream = nameof(RollbackSnapshotWrittenFalseInUpstream);
    public const string AuditLogWrittenFalseInUpstream = nameof(AuditLogWrittenFalseInUpstream);
    public const string RevocationRecordWrittenFalseInUpstream = nameof(RevocationRecordWrittenFalseInUpstream);
    public const string RuntimeActivationTrueInUpstream = nameof(RuntimeActivationTrueInUpstream);
    public const string FormalRetrievalAllowedTrueInUpstream = nameof(FormalRetrievalAllowedTrueInUpstream);
    public const string RuntimeSwitchAllowedTrueInUpstream = nameof(RuntimeSwitchAllowedTrueInUpstream);
    public const string ConfigPatchAppliedToRuntimeTrueInUpstream = nameof(ConfigPatchAppliedToRuntimeTrueInUpstream);
    public const string GrantArtifactMissing = nameof(GrantArtifactMissing);
    public const string ConfigPatchArtifactMissing = nameof(ConfigPatchArtifactMissing);
    public const string RollbackSnapshotArtifactMissing = nameof(RollbackSnapshotArtifactMissing);
    public const string AuditLogArtifactMissing = nameof(AuditLogArtifactMissing);
    public const string RevocationRecordArtifactMissing = nameof(RevocationRecordArtifactMissing);
    public const string GrantCapabilityMismatch = nameof(GrantCapabilityMismatch);
    public const string GrantScopeMismatch = nameof(GrantScopeMismatch);
    public const string GrantRuntimeActivationAllowedTrue = nameof(GrantRuntimeActivationAllowedTrue);
    public const string ConfigPatchSourceGrantIdMismatch = nameof(ConfigPatchSourceGrantIdMismatch);
    public const string RollbackSourceGrantIdMismatch = nameof(RollbackSourceGrantIdMismatch);
    public const string AuditGrantIdMismatch = nameof(AuditGrantIdMismatch);
    public const string RevocationGrantIdMismatch = nameof(RevocationGrantIdMismatch);
    public const string RevocationAlreadyRevoked = nameof(RevocationAlreadyRevoked);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);

    // V8.19R — 28 个新增 artifact-content 内容校验维度。
    public const string GrantRevocableFalse = nameof(GrantRevocableFalse);
    public const string GrantArtifactOnlyFalse = nameof(GrantArtifactOnlyFalse);
    public const string GrantCrossedFalse = nameof(GrantCrossedFalse);
    public const string GrantFormalRetrievalAllowedTrue = nameof(GrantFormalRetrievalAllowedTrue);
    public const string GrantRuntimeSwitchAllowedTrue = nameof(GrantRuntimeSwitchAllowedTrue);
    public const string GrantSourcePreCrossingMismatch = nameof(GrantSourcePreCrossingMismatch);
    public const string GrantSourceDryRunMismatch = nameof(GrantSourceDryRunMismatch);
    public const string ConfigPatchTargetCapabilityMismatch = nameof(ConfigPatchTargetCapabilityMismatch);
    public const string ConfigPatchTargetScopeMismatch = nameof(ConfigPatchTargetScopeMismatch);
    public const string ConfigPatchPatchModeNotArtifactOnly = nameof(ConfigPatchPatchModeNotArtifactOnly);
    public const string ConfigPatchApplyToRuntimeTrue = nameof(ConfigPatchApplyToRuntimeTrue);
    public const string ConfigPatchFormalRetrievalAllowedTrue = nameof(ConfigPatchFormalRetrievalAllowedTrue);
    public const string ConfigPatchSourcePreCrossingMismatch = nameof(ConfigPatchSourcePreCrossingMismatch);
    public const string ConfigPatchSourceDryRunMismatch = nameof(ConfigPatchSourceDryRunMismatch);
    public const string RollbackCapabilityMismatch = nameof(RollbackCapabilityMismatch);
    public const string RollbackScopeMismatch = nameof(RollbackScopeMismatch);
    public const string RollbackRestoreTestRequiredFalse = nameof(RollbackRestoreTestRequiredFalse);
    public const string AuditEventTypeMismatch = nameof(AuditEventTypeMismatch);
    public const string AuditCapabilityMismatch = nameof(AuditCapabilityMismatch);
    public const string AuditScopeMismatch = nameof(AuditScopeMismatch);
    public const string AuditCrossedFalse = nameof(AuditCrossedFalse);
    public const string AuditArtifactOnlyFalse = nameof(AuditArtifactOnlyFalse);
    public const string AuditRuntimeActivationTrue = nameof(AuditRuntimeActivationTrue);
    public const string AuditFormalRetrievalAllowedTrue = nameof(AuditFormalRetrievalAllowedTrue);
    public const string RevocationCapabilityMismatch = nameof(RevocationCapabilityMismatch);
    public const string RevocationScopeMismatch = nameof(RevocationScopeMismatch);
    public const string RevocationRevocableFalse = nameof(RevocationRevocableFalse);
    public const string RevocationPathPresentFalse = nameof(RevocationPathPresentFalse);
}

/// <summary>V8.18 写出的 capability grant artifact 内容（解析后）。</summary>
public sealed class CrossingCapabilityGrantContent
{
    public string GrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string SourcePreCrossingOperationId { get; init; } = string.Empty;
    public string SourceDryRunOperationId { get; init; } = string.Empty;
    public bool Revocable { get; init; }
    public bool RuntimeActivationAllowed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool Crossed { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
}

/// <summary>V8.18 写出的 runtime config patch artifact 内容。</summary>
public sealed class CrossingRuntimeConfigPatchContent
{
    public string PatchId { get; init; } = string.Empty;
    public string TargetCapability { get; init; } = string.Empty;
    public string TargetScope { get; init; } = string.Empty;
    public string PatchMode { get; init; } = string.Empty;
    public bool ApplyToRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public string SourceGrantId { get; init; } = string.Empty;
    public string SourcePreCrossingOperationId { get; init; } = string.Empty;
    public string SourceDryRunOperationId { get; init; } = string.Empty;
}

/// <summary>V8.18 写出的 rollback snapshot artifact 内容。</summary>
public sealed class CrossingRollbackSnapshotContent
{
    public string SnapshotId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string SourceGrantId { get; init; } = string.Empty;
    public bool RestoreTestRequired { get; init; }
}

/// <summary>V8.18 audit-log jsonl 文件的一条事件。</summary>
public sealed class CrossingAuditLogEvent
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string GrantId { get; init; } = string.Empty;
    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
}

/// <summary>V8.18 写出的 revocation record artifact 内容。</summary>
public sealed class CrossingRevocationRecordContent
{
    public string RevocationRecordId { get; init; } = string.Empty;
    public string GrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool Revocable { get; init; }
    public bool RevocationPathPresent { get; init; }
    public string RevocationStatus { get; init; } = string.Empty;
}

/// <summary>V8.19 activation planned contract — 仅声明 plan，不执行。</summary>
public sealed class RuntimeActivationPlannedContract
{
    public string PlannedRuntimeActivationMode { get; init; } = "GuardedScopeOnly";
    public string PlannedCapability { get; init; } = string.Empty;
    public string PlannedScope { get; init; } = string.Empty;
    public string PlannedConfigPatchSourcePath { get; init; } = string.Empty;
    public string PlannedRuntimeSwitchPath { get; init; } = string.Empty;
    public string PlannedActivationAuditPath { get; init; } = string.Empty;
    public string PlannedRollbackReference { get; init; } = string.Empty;
    public string PlannedRevocationReference { get; init; } = string.Empty;
}

/// <summary>V8.19 dry-run decision。所有 runtime activation flag 永远 false。</summary>
public sealed class RuntimeActivationDryRunDecision
{
    public string Status { get; init; } = RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public RuntimeActivationPlannedContract PlannedActivationContract { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    public bool ActivationDryRunOnly { get; init; } = true;
    public bool RuntimeActivationAllowed { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }

    // carry from V8.18 — Crossed=true 是允许的（V8.18 已 crossing），但 runtime 仍未动。
    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; } = true;
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }
}

/// <summary>
/// V8.19 runtime activation dry-run policy。
/// 输入：V8.18 execution gate report + 5 个解析后的 artifact 内容 + rt/p15/mainline。
/// 输出：RuntimeActivationDryRunDecision + PlannedActivationContract。
/// 不调用 IO，不应用 runtime config，不启用 formal retrieval。
/// </summary>
public static class FormalRetrievalPromotionApprovalRuntimeActivationDryRunPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static RuntimeActivationDryRunDecision Evaluate(
        FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport? executionReport,
        CrossingCapabilityGrantContent? grant,
        CrossingRuntimeConfigPatchContent? configPatch,
        CrossingRollbackSnapshotContent? rollbackSnapshot,
        CrossingAuditLogEvent? auditEvent,
        CrossingRevocationRecordContent? revocation,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        string? plannedConfigPatchSourcePath = null,
        string? plannedRollbackSnapshotPath = null,
        string? plannedRevocationRecordPath = null)
    {
        var blocked = new List<string>();

        // step 1 — V8.18 execution report 存在 + 通过。
        if (executionReport is null)
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.CrossingExecutionGateMissing);
            return BuildBlocked(string.Empty, string.Empty, string.Empty, blocked,
                new RuntimeActivationPlannedContract(),
                "V8.18 execution gate report missing; runtime activation dry-run cannot proceed.",
                upstream: null);
        }

        if (!executionReport.GatePassed || !executionReport.DedicatedCrossingExecutionGatePassed)
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.CrossingExecutionGateNotPassed);
        }

        // step 2 — V8.18 不变量必须一致：Crossed=true（V8.18 才允许）、ArtifactOnly=true、五写出标志=true、runtime 全 false。
        if (!executionReport.Crossed) blocked.Add(RuntimeActivationDryRunBlockedReasons.CrossingNotTrueInUpstream);
        if (!executionReport.ArtifactOnly) blocked.Add(RuntimeActivationDryRunBlockedReasons.ArtifactOnlyFalseInUpstream);
        if (!executionReport.CapabilityGrantWritten) blocked.Add(RuntimeActivationDryRunBlockedReasons.CapabilityGrantWrittenFalseInUpstream);
        if (!executionReport.ConfigPatchWritten) blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchWrittenFalseInUpstream);
        if (!executionReport.RollbackSnapshotWritten) blocked.Add(RuntimeActivationDryRunBlockedReasons.RollbackSnapshotWrittenFalseInUpstream);
        if (!executionReport.AuditLogWritten) blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditLogWrittenFalseInUpstream);
        if (!executionReport.RevocationRecordWritten) blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationRecordWrittenFalseInUpstream);
        if (executionReport.RuntimeActivation) blocked.Add(RuntimeActivationDryRunBlockedReasons.RuntimeActivationTrueInUpstream);
        if (executionReport.FormalRetrievalAllowed) blocked.Add(RuntimeActivationDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream);
        if (executionReport.RuntimeSwitchAllowed) blocked.Add(RuntimeActivationDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream);
        if (executionReport.ConfigPatchAppliedToRuntime) blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchAppliedToRuntimeTrueInUpstream);

        // step 3 — 5 个 artifact 内容存在性。
        if (grant is null) blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantArtifactMissing);
        if (configPatch is null) blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchArtifactMissing);
        if (rollbackSnapshot is null) blocked.Add(RuntimeActivationDryRunBlockedReasons.RollbackSnapshotArtifactMissing);
        if (auditEvent is null) blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditLogArtifactMissing);
        if (revocation is null) blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationRecordArtifactMissing);

        // step 4 — capability/scope/binding checks (在 grant 存在的前提下做)。
        var boundGrantId = grant?.GrantId ?? string.Empty;
        var boundCapability = grant?.Capability ?? string.Empty;
        var boundScope = grant?.Scope ?? string.Empty;

        if (grant is not null)
        {
            if (!string.Equals(grant.Capability, AllowedCapability, StringComparison.Ordinal))
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantCapabilityMismatch);
            }

            if (!string.Equals(grant.Scope, AllowedScope, StringComparison.Ordinal))
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantScopeMismatch);
            }

            if (grant.RuntimeActivationAllowed)
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantRuntimeActivationAllowedTrue);
            }

            // V8.19R — grant artifact 内容级守护。
            if (!grant.Revocable) blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantRevocableFalse);
            if (!grant.ArtifactOnly) blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantArtifactOnlyFalse);
            if (!grant.Crossed) blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantCrossedFalse);
            if (grant.FormalRetrievalAllowed) blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantFormalRetrievalAllowedTrue);
            if (grant.RuntimeSwitchAllowed) blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantRuntimeSwitchAllowedTrue);

            if (executionReport is not null)
            {
                if (!string.Equals(grant.SourcePreCrossingOperationId, executionReport.SourcePreCrossingOperationId, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantSourcePreCrossingMismatch);
                }

                if (!string.Equals(grant.SourceDryRunOperationId, executionReport.SourceDryRunOperationId, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.GrantSourceDryRunMismatch);
                }
            }
        }

        if (grant is not null && configPatch is not null
            && !string.Equals(configPatch.SourceGrantId, grant.GrantId, StringComparison.Ordinal))
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchSourceGrantIdMismatch);
        }

        // V8.19R — config patch artifact 内容级守护（独立于 SourceGrantId）。
        if (configPatch is not null && grant is not null)
        {
            if (!string.Equals(configPatch.TargetCapability, grant.Capability, StringComparison.Ordinal))
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchTargetCapabilityMismatch);
            }

            if (!string.Equals(configPatch.TargetScope, grant.Scope, StringComparison.Ordinal))
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchTargetScopeMismatch);
            }
        }

        if (configPatch is not null)
        {
            if (!string.Equals(configPatch.PatchMode, "ArtifactOnly", StringComparison.Ordinal))
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchPatchModeNotArtifactOnly);
            }

            if (configPatch.ApplyToRuntime)
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchApplyToRuntimeTrue);
            }

            if (configPatch.FormalRetrievalAllowed)
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchFormalRetrievalAllowedTrue);
            }

            if (executionReport is not null)
            {
                if (!string.Equals(configPatch.SourcePreCrossingOperationId, executionReport.SourcePreCrossingOperationId, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchSourcePreCrossingMismatch);
                }

                if (!string.Equals(configPatch.SourceDryRunOperationId, executionReport.SourceDryRunOperationId, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.ConfigPatchSourceDryRunMismatch);
                }
            }
        }

        if (grant is not null && rollbackSnapshot is not null
            && !string.Equals(rollbackSnapshot.SourceGrantId, grant.GrantId, StringComparison.Ordinal))
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.RollbackSourceGrantIdMismatch);
        }

        // V8.19R — rollback snapshot 内容级守护。
        if (rollbackSnapshot is not null)
        {
            if (grant is not null)
            {
                if (!string.Equals(rollbackSnapshot.BoundCapability, grant.Capability, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.RollbackCapabilityMismatch);
                }

                if (!string.Equals(rollbackSnapshot.BoundScope, grant.Scope, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.RollbackScopeMismatch);
                }
            }

            if (!rollbackSnapshot.RestoreTestRequired)
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.RollbackRestoreTestRequiredFalse);
            }
        }

        if (grant is not null && auditEvent is not null
            && !string.Equals(auditEvent.GrantId, grant.GrantId, StringComparison.Ordinal))
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditGrantIdMismatch);
        }

        // V8.19R — audit log event 内容级守护。
        if (auditEvent is not null)
        {
            if (!string.Equals(auditEvent.EventType, "DedicatedCrossingArtifactWriteOut", StringComparison.Ordinal))
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditEventTypeMismatch);
            }

            if (grant is not null)
            {
                if (!string.Equals(auditEvent.BoundCapability, grant.Capability, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditCapabilityMismatch);
                }

                if (!string.Equals(auditEvent.BoundScope, grant.Scope, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditScopeMismatch);
                }
            }

            if (!auditEvent.Crossed) blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditCrossedFalse);
            if (!auditEvent.ArtifactOnly) blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditArtifactOnlyFalse);
            if (auditEvent.RuntimeActivation) blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditRuntimeActivationTrue);
            if (auditEvent.FormalRetrievalAllowed) blocked.Add(RuntimeActivationDryRunBlockedReasons.AuditFormalRetrievalAllowedTrue);
        }

        if (grant is not null && revocation is not null
            && !string.Equals(revocation.GrantId, grant.GrantId, StringComparison.Ordinal))
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationGrantIdMismatch);
        }

        // V8.19R — revocation record 内容级守护。
        if (revocation is not null)
        {
            if (grant is not null)
            {
                if (!string.Equals(revocation.BoundCapability, grant.Capability, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationCapabilityMismatch);
                }

                if (!string.Equals(revocation.BoundScope, grant.Scope, StringComparison.Ordinal))
                {
                    blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationScopeMismatch);
                }
            }

            if (!revocation.Revocable)
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationRevocableFalse);
            }

            if (!revocation.RevocationPathPresent)
            {
                blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationPathPresentFalse);
            }
        }

        if (revocation is not null
            && !string.Equals(revocation.RevocationStatus, "RevocableNotYetRevoked", StringComparison.Ordinal))
        {
            blocked.Add(RuntimeActivationDryRunBlockedReasons.RevocationAlreadyRevoked);
        }

        // step 5 — baseline runtime / p15 / mainline 契约。
        if (!rtPassed) blocked.Add(RuntimeActivationDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(RuntimeActivationDryRunBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(RuntimeActivationDryRunBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(RuntimeActivationDryRunBlockedReasons.MainlineTrustRegistryPresent);

        // step 6 — 构造 planned activation contract（plan only，never executed）。
        var contract = new RuntimeActivationPlannedContract
        {
            PlannedRuntimeActivationMode = "GuardedScopeOnly",
            PlannedCapability = boundCapability,
            PlannedScope = boundScope,
            PlannedConfigPatchSourcePath = plannedConfigPatchSourcePath ?? string.Empty,
            PlannedRuntimeSwitchPath = ComputeRuntimeSwitchPath(boundCapability, boundScope),
            PlannedActivationAuditPath = ComputeActivationAuditPath(boundCapability, boundScope),
            PlannedRollbackReference = plannedRollbackSnapshotPath ?? string.Empty,
            PlannedRevocationReference = plannedRevocationRecordPath ?? string.Empty
        };

        var distinctBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = distinctBlocked.Length == 0;
        var status = ready
            ? RuntimeActivationDryRunStatuses.RuntimeActivationDryRunReady
            : RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked;
        var reasoning = ready
            ? $"all bindings consistent; grant '{boundGrantId}' for capability '{boundCapability}' in scope '{boundScope}'. Activation plan recorded. ActivationDryRunOnly=true; RuntimeActivationAllowed=false; nothing applied to runtime."
            : $"{distinctBlocked.Length} blocked reason(s); activation dry-run blocked.";

        return new RuntimeActivationDryRunDecision
        {
            Status = status,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            PlannedActivationContract = contract,
            BlockedReasons = distinctBlocked,
            Reasoning = reasoning,
            ActivationDryRunOnly = true,
            RuntimeActivationAllowed = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            // carry from V8.18 — 这些 carry 值即便 blocked 也保留 upstream 的事实。
            Crossed = executionReport.Crossed,
            ArtifactOnly = executionReport.ArtifactOnly,
            CapabilityGrantWritten = executionReport.CapabilityGrantWritten,
            ConfigPatchWritten = executionReport.ConfigPatchWritten,
            RollbackSnapshotWritten = executionReport.RollbackSnapshotWritten,
            AuditLogWritten = executionReport.AuditLogWritten,
            RevocationRecordWritten = executionReport.RevocationRecordWritten
        };
    }

    private static string ComputeRuntimeSwitchPath(string capability, string scope)
    {
        var safeCapability = NormalizeForPath(capability);
        var safeScope = NormalizeForPath(scope);
        return $"vector/v8/runtime-activation/runtime-switch-{safeCapability}-{safeScope}.json";
    }

    private static string ComputeActivationAuditPath(string capability, string scope)
    {
        var safeCapability = NormalizeForPath(capability);
        var safeScope = NormalizeForPath(scope);
        return $"vector/v8/runtime-activation/activation-audit-{safeCapability}-{safeScope}.jsonl";
    }

    private static string NormalizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "missing";
        return new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    }

    private static RuntimeActivationDryRunDecision BuildBlocked(
        string boundGrantId, string boundCapability, string boundScope,
        List<string> blocked,
        RuntimeActivationPlannedContract contract,
        string reasoning,
        FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport? upstream)
    {
        var distinct = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new RuntimeActivationDryRunDecision
        {
            Status = RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            PlannedActivationContract = contract,
            BlockedReasons = distinct,
            Reasoning = reasoning,
            ActivationDryRunOnly = true,
            RuntimeActivationAllowed = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            Crossed = upstream?.Crossed ?? false,
            ArtifactOnly = upstream?.ArtifactOnly ?? true,
            CapabilityGrantWritten = upstream?.CapabilityGrantWritten ?? false,
            ConfigPatchWritten = upstream?.ConfigPatchWritten ?? false,
            RollbackSnapshotWritten = upstream?.RollbackSnapshotWritten ?? false,
            AuditLogWritten = upstream?.AuditLogWritten ?? false,
            RevocationRecordWritten = upstream?.RevocationRecordWritten ?? false
        };
    }
}
