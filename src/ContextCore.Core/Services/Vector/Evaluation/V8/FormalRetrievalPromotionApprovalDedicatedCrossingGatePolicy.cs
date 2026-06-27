namespace ContextCore.Core.Services;

/// <summary>V8.17 dedicated crossing dry-run 状态。</summary>
public static class CrossingDryRunStatuses
{
    /// <summary>所有 precondition 通过 + 5 个 planned artifact path 就绪 + 没有任何被禁止的迹象。仍未跨过。</summary>
    public const string CrossingDryRunReady = nameof(CrossingDryRunReady);

    /// <summary>至少一个 precondition 失败或 plan 不合规。</summary>
    public const string CrossingDryRunBlocked = nameof(CrossingDryRunBlocked);
}

/// <summary>V8.17 crossing 阻塞原因常量。</summary>
public static class CrossingDryRunBlockedReasons
{
    public const string PreCrossingGateMissing = nameof(PreCrossingGateMissing);
    public const string PreCrossingGateNotPassed = nameof(PreCrossingGateNotPassed);
    public const string NoPreCrossingReadyCase = nameof(NoPreCrossingReadyCase);
    public const string CapabilityMismatch = nameof(CapabilityMismatch);
    public const string EmptyScope = nameof(EmptyScope);
    public const string GlobalScopeForbidden = nameof(GlobalScopeForbidden);
    public const string UpstreamCrossedTrue = nameof(UpstreamCrossedTrue);
    public const string UpstreamApplicationApplied = nameof(UpstreamApplicationApplied);
    public const string UpstreamRollbackActivated = nameof(UpstreamRollbackActivated);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string PlannedConfigPatchPathWouldOverwrite = nameof(PlannedConfigPatchPathWouldOverwrite);
    public const string PlannedRollbackSnapshotPathMissing = nameof(PlannedRollbackSnapshotPathMissing);
    public const string CapabilityScopeNotAligned = nameof(CapabilityScopeNotAligned);
}

/// <summary>V8.17 crossing 唯一被授权的 capability。</summary>
public static class CrossingAuthorizedCapabilities
{
    public const string Required = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
}

/// <summary>V8.17 被禁止的 global/wildcard scope。</summary>
public static class CrossingForbiddenScopes
{
    public static readonly IReadOnlySet<string> Values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "*",
        "**",
        "all",
        "global",
        "any",
        "default",
        "*/*"
    };
}

/// <summary>V8.17 plan 覆盖项 — 让 matrix scenarios 可以模拟 path 异常。</summary>
public sealed class CrossingDryRunPlanOverrides
{
    /// <summary>true → planned rollback snapshot path 被强制设为空（模拟未配置）。</summary>
    public bool RollbackSnapshotPathMissing { get; init; }

    /// <summary>true → planned runtime config patch path 已经存在（模拟会覆盖现有 artifact）。</summary>
    public bool ConfigPatchPathAlreadyExists { get; init; }
}

/// <summary>V8.17 crossing execution contract — planned paths only, no writes。</summary>
public sealed class CrossingExecutionContract
{
    public string PlannedCapability { get; init; } = string.Empty;
    public string PlannedScope { get; init; } = string.Empty;
    public string PlannedCapabilityGrantPath { get; init; } = string.Empty;
    public string PlannedRuntimeConfigPatchPath { get; init; } = string.Empty;
    public string PlannedRollbackSnapshotPath { get; init; } = string.Empty;
    public string PlannedAuditLogPath { get; init; } = string.Empty;
    public string PlannedRevocationRecordPath { get; init; } = string.Empty;
}

/// <summary>V8.17 crossing dry-run decision。Status + planned contract。一切 action flag 永远 false。</summary>
public sealed class CrossingDryRunDecision
{
    public string Status { get; init; } = CrossingDryRunStatuses.CrossingDryRunBlocked;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public CrossingExecutionContract Contract { get; init; } = new();
    public IReadOnlyList<string> PlannedArtifacts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>核心契约 — Dry-run only，永远 true。</summary>
    public bool DryRunOnly { get; init; } = true;

    /// <summary>核心契约 — crossing 执行从不被授权（even Ready）。</summary>
    public bool CrossingExecutionAllowed { get; init; }

    /// <summary>核心契约 — 没有跨过。</summary>
    public bool Crossed { get; init; }

    /// <summary>carry — 应用未被实际应用。</summary>
    public bool ApplicationApplied { get; init; }

    /// <summary>carry — 回滚路径未被激活。</summary>
    public bool RollbackActivated { get; init; }
}

/// <summary>
/// V8.17 dedicated crossing gate policy。
/// 输入：V8.16 PreCrossingReport（可能 null）+ rt/p15/mainline 标志 + plan overrides。
/// 输出：CrossingDryRunDecision，含 planned artifact paths。从不写文件，从不跨过。
/// </summary>
public static class FormalRetrievalPromotionApprovalDedicatedCrossingGatePolicy
{
    public static CrossingDryRunDecision Evaluate(
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport? preCrossingReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        CrossingDryRunPlanOverrides? overrides = null)
    {
        overrides ??= new CrossingDryRunPlanOverrides();
        var blocked = new List<string>();

        // step 1 — upstream V8.16 报告存在性。
        if (preCrossingReport is null)
        {
            blocked.Add(CrossingDryRunBlockedReasons.PreCrossingGateMissing);
            return BuildBlocked(string.Empty, string.Empty, blocked, new CrossingExecutionContract(), Array.Empty<string>(),
                "pre-crossing final gate report missing; crossing dry-run cannot proceed.");
        }

        // step 2 — upstream gate state。
        if (!preCrossingReport.GatePassed || !preCrossingReport.PreCrossingFinalGatePassed)
        {
            blocked.Add(CrossingDryRunBlockedReasons.PreCrossingGateNotPassed);
        }

        // step 3 — PreCrossingReady case 存在性。
        var readyCase = preCrossingReport.Cases?
            .FirstOrDefault(c => string.Equals(c.ActualStatus, PreCrossingStatuses.PreCrossingReady, StringComparison.Ordinal));
        if (readyCase is null)
        {
            blocked.Add(CrossingDryRunBlockedReasons.NoPreCrossingReadyCase);
        }

        // step 4 — capability 必须是 FormalRetrievalActivation。读优先：ready case > report 顶级。
        var boundCapability = readyCase?.BoundCapability
            ?? (string.IsNullOrEmpty(preCrossingReport.BoundCapability) ? string.Empty : preCrossingReport.BoundCapability);
        if (!string.Equals(boundCapability, CrossingAuthorizedCapabilities.Required, StringComparison.Ordinal))
        {
            blocked.Add(CrossingDryRunBlockedReasons.CapabilityMismatch);
        }

        // step 5 — scope 必须非空且不能 global/wildcard。
        var boundScope = readyCase?.BoundScope
            ?? (string.IsNullOrEmpty(preCrossingReport.BoundScope) ? string.Empty : preCrossingReport.BoundScope);
        if (string.IsNullOrWhiteSpace(boundScope))
        {
            blocked.Add(CrossingDryRunBlockedReasons.EmptyScope);
        }
        else if (CrossingForbiddenScopes.Values.Contains(boundScope.Trim()))
        {
            blocked.Add(CrossingDryRunBlockedReasons.GlobalScopeForbidden);
        }

        // step 6 — capability/scope alignment（V8.16 carry）。
        if (readyCase is not null && !readyCase.CapabilityScopeAligned)
        {
            blocked.Add(CrossingDryRunBlockedReasons.CapabilityScopeNotAligned);
        }

        // step 7 — upstream invariants — 任一被破坏即 block。
        if (preCrossingReport.Crossed) blocked.Add(CrossingDryRunBlockedReasons.UpstreamCrossedTrue);
        if (preCrossingReport.ApplicationApplied) blocked.Add(CrossingDryRunBlockedReasons.UpstreamApplicationApplied);
        if (preCrossingReport.RollbackActivated) blocked.Add(CrossingDryRunBlockedReasons.UpstreamRollbackActivated);

        // step 8 — runtime + P15 + mainline 文件契约。
        if (!rtPassed) blocked.Add(CrossingDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(CrossingDryRunBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(CrossingDryRunBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(CrossingDryRunBlockedReasons.MainlineTrustRegistryPresent);

        // step 9 — 构造 planned contract（注意：path 计算与 block 状态独立；contract 永远附在输出里）。
        var contract = BuildContract(boundCapability, boundScope, overrides);

        // step 10 — plan 完整性 + 不覆盖现有文件。
        if (string.IsNullOrWhiteSpace(contract.PlannedRollbackSnapshotPath))
        {
            blocked.Add(CrossingDryRunBlockedReasons.PlannedRollbackSnapshotPathMissing);
        }

        if (overrides.ConfigPatchPathAlreadyExists)
        {
            blocked.Add(CrossingDryRunBlockedReasons.PlannedConfigPatchPathWouldOverwrite);
        }

        var distinctBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var status = distinctBlocked.Length == 0
            ? CrossingDryRunStatuses.CrossingDryRunReady
            : CrossingDryRunStatuses.CrossingDryRunBlocked;
        var reasoning = status == CrossingDryRunStatuses.CrossingDryRunReady
            ? $"all crossing dry-run preconditions met; capability='{boundCapability}' scope='{boundScope}'; 5 planned artifact paths recorded. DryRunOnly=true; CrossingExecutionAllowed=false; nothing written, nothing crossed."
            : $"{distinctBlocked.Length} blocked reason(s); crossing dry-run blocked.";

        return new CrossingDryRunDecision
        {
            Status = status,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            Contract = contract,
            PlannedArtifacts = new[]
            {
                contract.PlannedCapabilityGrantPath,
                contract.PlannedRuntimeConfigPatchPath,
                contract.PlannedRollbackSnapshotPath,
                contract.PlannedAuditLogPath,
                contract.PlannedRevocationRecordPath
            }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray(),
            BlockedReasons = distinctBlocked,
            Reasoning = reasoning,
            DryRunOnly = true,
            CrossingExecutionAllowed = false,
            Crossed = false,
            ApplicationApplied = false,
            RollbackActivated = false
        };
    }

    private static CrossingExecutionContract BuildContract(
        string capability, string scope, CrossingDryRunPlanOverrides overrides)
    {
        // 用 capability + scope-normalized 作为 grant artifact 的命名后缀，保持 plan 在不同 scope 下不会冲突。
        var safeScope = NormalizeForPath(scope);
        var safeCapability = NormalizeForPath(capability);
        const string baseDir = "vector/v8/dedicated-crossing";

        return new CrossingExecutionContract
        {
            PlannedCapability = capability,
            PlannedScope = scope,
            PlannedCapabilityGrantPath = $"{baseDir}/capability-grant-{safeCapability}-{safeScope}.json",
            PlannedRuntimeConfigPatchPath = $"{baseDir}/runtime-config-patch-{safeCapability}-{safeScope}.json",
            PlannedRollbackSnapshotPath = overrides.RollbackSnapshotPathMissing
                ? string.Empty
                : $"{baseDir}/rollback-snapshot-{safeCapability}-{safeScope}.json",
            PlannedAuditLogPath = $"{baseDir}/audit-log-{safeCapability}-{safeScope}.jsonl",
            PlannedRevocationRecordPath = $"{baseDir}/revocation-record-{safeCapability}-{safeScope}.json"
        };
    }

    private static string NormalizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "missing";
        // 把 / : \ 等替换成 -，方便文件名。
        return new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    }

    private static CrossingDryRunDecision BuildBlocked(
        string capability, string scope,
        List<string> blocked,
        CrossingExecutionContract contract,
        IReadOnlyList<string> plannedArtifacts,
        string reasoning)
    {
        var distinct = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new CrossingDryRunDecision
        {
            Status = CrossingDryRunStatuses.CrossingDryRunBlocked,
            BoundCapability = capability,
            BoundScope = scope,
            Contract = contract,
            PlannedArtifacts = plannedArtifacts,
            BlockedReasons = distinct,
            Reasoning = reasoning,
            DryRunOnly = true,
            CrossingExecutionAllowed = false,
            Crossed = false,
            ApplicationApplied = false,
            RollbackActivated = false
        };
    }
}
