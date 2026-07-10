namespace ContextCore.Core.Services;

/// <summary>V8.18 dedicated crossing execution 状态。</summary>
public static class CrossingExecutionStatuses
{
    /// <summary>所有 precondition 通过且 5 个 artifact 已被写出。Crossed=true，但仅 artifact 层，runtime 不动。</summary>
    public const string DedicatedCrossingExecuted = nameof(DedicatedCrossingExecuted);

    /// <summary>至少一个 precondition 失败，或写出失败。</summary>
    public const string DedicatedCrossingExecutionBlocked = nameof(DedicatedCrossingExecutionBlocked);
}

/// <summary>V8.18 阻塞原因。</summary>
public static class CrossingExecutionBlockedReasons
{
    public const string DryRunGateMissing = nameof(DryRunGateMissing);
    public const string DryRunGateNotPassed = nameof(DryRunGateNotPassed);
    public const string NoCrossingDryRunReadyCase = nameof(NoCrossingDryRunReadyCase);
    public const string DryRunOnlyFalse = nameof(DryRunOnlyFalse);
    public const string CrossingExecutionAllowedTrueInDryRun = nameof(CrossingExecutionAllowedTrueInDryRun);
    public const string PlannedArtifactCountNotFive = nameof(PlannedArtifactCountNotFive);
    public const string PlannedArtifactOutsideAllowedDirectory = nameof(PlannedArtifactOutsideAllowedDirectory);
    public const string PlannedArtifactAlreadyExists = nameof(PlannedArtifactAlreadyExists);
    public const string GlobalScopeForbidden = nameof(GlobalScopeForbidden);
    public const string EmptyScope = nameof(EmptyScope);
    public const string CapabilityMismatch = nameof(CapabilityMismatch);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string WriteFailureSimulated = nameof(WriteFailureSimulated);
}

/// <summary>V8.18 允许写出的 artifact 目录。</summary>
public static class CrossingExecutionAllowedDirectory
{
    public const string Value = "vector/v8/dedicated-crossing";

    public static bool IsUnder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith(Value + "/", StringComparison.Ordinal);
    }
}

/// <summary>V8.18 path 存在性 — runner 用真实磁盘核对；scenarios 用 in-memory mock。</summary>
public sealed class CrossingExecutionPathExistence
{
    public bool CapabilityGrantPathExists { get; init; }
    public bool RuntimeConfigPatchPathExists { get; init; }
    public bool RollbackSnapshotPathExists { get; init; }
    public bool AuditLogPathExists { get; init; }
    public bool RevocationRecordPathExists { get; init; }

    public bool AnyExists => CapabilityGrantPathExists
        || RuntimeConfigPatchPathExists
        || RollbackSnapshotPathExists
        || AuditLogPathExists
        || RevocationRecordPathExists;
}

/// <summary>V8.18 测试覆盖项。让 matrix scenarios 模拟反常输入。</summary>
public sealed class CrossingExecutionOverrides
{
    /// <summary>true → planned artifact count 被强制设为 4（缺一项）。</summary>
    public bool ForcePlannedArtifactCountWrong { get; init; }

    /// <summary>true → 其中一个 planned path 被替换为非 dedicated-crossing 目录下的路径。</summary>
    public bool ForcePlannedArtifactOutsideDirectory { get; init; }

    /// <summary>true → 写出阶段失败（runner 用，不写文件，policy 上报 WriteFailureSimulated）。</summary>
    public bool SimulateWriteFailure { get; init; }
}

/// <summary>V8.18 crossing execution decision。Status + 计划写出的 5 个 artifact 路径 + Crossed/ArtifactOnly 标志。</summary>
public sealed class CrossingExecutionDecision
{
    public string Status { get; init; } = CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string SourcePreCrossingOperationId { get; init; } = string.Empty;
    public string SourceDryRunOperationId { get; init; } = string.Empty;
    public IReadOnlyList<string> PlannedArtifactPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>Crossed=true 仅当 Status=DedicatedCrossingExecuted。</summary>
    public bool Crossed { get; init; }

    /// <summary>ArtifactOnly=true 永远在 V8.18 上下文中成立 — 只写 artifact，从不动 runtime。</summary>
    public bool ArtifactOnly { get; init; } = true;

    /// <summary>当 Crossed=true 时为 true；artifact 已写出但 runtime 未动。</summary>
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }

    /// <summary>这些永远 false — runtime 不动。</summary>
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
}

/// <summary>
/// V8.18 dedicated crossing execution policy。
/// 输入：V8.17 dry-run report + V8.16 pre-crossing report + rt/p15/mainline + path existence + overrides。
/// 输出：CrossingExecutionDecision。policy 自身不写文件；写文件由 runner 在 Status=Executed 时执行。
/// </summary>
public static class FormalRetrievalPromotionApprovalDedicatedCrossingExecutionPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;

    public static CrossingExecutionDecision Evaluate(
        FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport? dryRunReport,
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport? preCrossingReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        CrossingExecutionPathExistence pathExistence,
        CrossingExecutionOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(pathExistence);
        overrides ??= new CrossingExecutionOverrides();
        var blocked = new List<string>();

        // step 1 — V8.17 dry-run report 必须存在。
        if (dryRunReport is null)
        {
            blocked.Add(CrossingExecutionBlockedReasons.DryRunGateMissing);
            return BuildBlocked(string.Empty, string.Empty, string.Empty, string.Empty,
                Array.Empty<string>(), blocked,
                "dedicated crossing dry-run report missing; execution blocked.");
        }

        // step 2 — dry-run gate state。
        if (!dryRunReport.GatePassed || !dryRunReport.CrossingDryRunMatrixPassed)
        {
            blocked.Add(CrossingExecutionBlockedReasons.DryRunGateNotPassed);
        }

        // step 3 — V8.17 contract 不变量必须成立。
        if (!dryRunReport.DryRunOnly)
        {
            blocked.Add(CrossingExecutionBlockedReasons.DryRunOnlyFalse);
        }

        if (dryRunReport.CrossingExecutionAllowed)
        {
            blocked.Add(CrossingExecutionBlockedReasons.CrossingExecutionAllowedTrueInDryRun);
        }

        // step 4 — CrossingDryRunReady case 存在性。
        var readyCase = dryRunReport.Cases?
            .FirstOrDefault(c => string.Equals(c.ActualStatus, CrossingDryRunStatuses.CrossingDryRunReady, StringComparison.Ordinal));
        if (readyCase is null)
        {
            blocked.Add(CrossingExecutionBlockedReasons.NoCrossingDryRunReadyCase);
        }

        // step 5 — capability / scope 守护。
        var boundCapability = readyCase?.BoundCapability
            ?? (string.IsNullOrEmpty(dryRunReport.BoundCapability) ? string.Empty : dryRunReport.BoundCapability);
        var boundScope = readyCase?.BoundScope
            ?? (string.IsNullOrEmpty(dryRunReport.BoundScope) ? string.Empty : dryRunReport.BoundScope);

        if (!string.Equals(boundCapability, AllowedCapability, StringComparison.Ordinal))
        {
            blocked.Add(CrossingExecutionBlockedReasons.CapabilityMismatch);
        }

        if (string.IsNullOrWhiteSpace(boundScope))
        {
            blocked.Add(CrossingExecutionBlockedReasons.EmptyScope);
        }
        else if (CrossingForbiddenScopes.Values.Contains(boundScope.Trim()))
        {
            blocked.Add(CrossingExecutionBlockedReasons.GlobalScopeForbidden);
        }

        // step 6 — 计算计划路径（与 V8.17 一致）。
        var safeCapability = NormalizeForPath(boundCapability);
        var safeScope = NormalizeForPath(boundScope);
        var paths = new List<string>
        {
            $"{CrossingExecutionAllowedDirectory.Value}/capability-grant-{safeCapability}-{safeScope}.json",
            $"{CrossingExecutionAllowedDirectory.Value}/runtime-config-patch-{safeCapability}-{safeScope}.json",
            $"{CrossingExecutionAllowedDirectory.Value}/rollback-snapshot-{safeCapability}-{safeScope}.json",
            $"{CrossingExecutionAllowedDirectory.Value}/audit-log-{safeCapability}-{safeScope}.jsonl",
            $"{CrossingExecutionAllowedDirectory.Value}/revocation-record-{safeCapability}-{safeScope}.json"
        };

        if (overrides.ForcePlannedArtifactCountWrong)
        {
            paths.RemoveAt(0);
        }

        if (overrides.ForcePlannedArtifactOutsideDirectory && paths.Count > 0)
        {
            paths[0] = "vector/v8/elsewhere/" + System.IO.Path.GetFileName(paths[0]);
        }

        // step 7 — 路径数量与目录守护。
        if (paths.Count != 5)
        {
            blocked.Add(CrossingExecutionBlockedReasons.PlannedArtifactCountNotFive);
        }

        if (paths.Any(p => !CrossingExecutionAllowedDirectory.IsUnder(p)))
        {
            blocked.Add(CrossingExecutionBlockedReasons.PlannedArtifactOutsideAllowedDirectory);
        }

        // step 8 — path 已存在性（防覆盖）。
        if (pathExistence.AnyExists)
        {
            blocked.Add(CrossingExecutionBlockedReasons.PlannedArtifactAlreadyExists);
        }

        // step 9 — runtime / p15 / mainline 契约。
        if (!rtPassed) blocked.Add(CrossingExecutionBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(CrossingExecutionBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(CrossingExecutionBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(CrossingExecutionBlockedReasons.MainlineTrustRegistryPresent);

        // step 10 — 写出失败模拟。
        if (overrides.SimulateWriteFailure)
        {
            blocked.Add(CrossingExecutionBlockedReasons.WriteFailureSimulated);
        }

        // upstream operation id — 跨阶段绑定。
        var preCrossingOperationId = preCrossingReport?.OperationId ?? string.Empty;
        var dryRunOperationId = dryRunReport.OperationId ?? string.Empty;

        var distinctBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var executed = distinctBlocked.Length == 0;
        var status = executed
            ? CrossingExecutionStatuses.DedicatedCrossingExecuted
            : CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked;
        var reasoning = executed
            ? $"all preconditions met; 5 planned artifacts will be written under {CrossingExecutionAllowedDirectory.Value}/. Crossed=true (artifact-only). RuntimeActivation=false; FormalRetrievalAllowed=false."
            : $"{distinctBlocked.Length} blocked reason(s); crossing execution blocked. Artifacts NOT written.";

        return new CrossingExecutionDecision
        {
            Status = status,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            SourcePreCrossingOperationId = preCrossingOperationId,
            SourceDryRunOperationId = dryRunOperationId,
            PlannedArtifactPaths = paths.ToArray(),
            BlockedReasons = distinctBlocked,
            Reasoning = reasoning,
            // Crossed=true ONLY when status=Executed。
            Crossed = executed,
            ArtifactOnly = true,
            CapabilityGrantWritten = executed,
            ConfigPatchWritten = executed,
            RollbackSnapshotWritten = executed,
            AuditLogWritten = executed,
            RevocationRecordWritten = executed,
            // Runtime 永远不动。
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false
        };
    }

    private static string NormalizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "missing";
        return new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    }

    private static CrossingExecutionDecision BuildBlocked(
        string capability, string scope,
        string sourcePreCrossingOpId, string sourceDryRunOpId,
        IReadOnlyList<string> plannedPaths,
        List<string> blocked,
        string reasoning)
    {
        var distinct = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new CrossingExecutionDecision
        {
            Status = CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
            BoundCapability = capability,
            BoundScope = scope,
            SourcePreCrossingOperationId = sourcePreCrossingOpId,
            SourceDryRunOperationId = sourceDryRunOpId,
            PlannedArtifactPaths = plannedPaths,
            BlockedReasons = distinct,
            Reasoning = reasoning,
            Crossed = false,
            ArtifactOnly = true,
            CapabilityGrantWritten = false,
            ConfigPatchWritten = false,
            RollbackSnapshotWritten = false,
            AuditLogWritten = false,
            RevocationRecordWritten = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false
        };
    }
}
