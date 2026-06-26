namespace ContextCore.Core.Services;

/// <summary>V8.16 pre-crossing final gate 状态。</summary>
public static class PreCrossingStatuses
{
    /// <summary>三上游 gate 全 passed、各自有 Ready/Recorded case、绑定到同一 capability/scope。仍未跨过应用边界。</summary>
    public const string PreCrossingReady = nameof(PreCrossingReady);

    /// <summary>至少一个核对项失败。</summary>
    public const string PreCrossingBlocked = nameof(PreCrossingBlocked);
}

/// <summary>V8.16 final gate 阻塞原因常量。</summary>
public static class PreCrossingBlockedReasons
{
    public const string GrantApplicationGateMissing = nameof(GrantApplicationGateMissing);
    public const string RollbackReadinessGateMissing = nameof(RollbackReadinessGateMissing);
    public const string OperatorSignOffGateMissing = nameof(OperatorSignOffGateMissing);
    public const string GrantApplicationGateNotPassed = nameof(GrantApplicationGateNotPassed);
    public const string RollbackReadinessGateNotPassed = nameof(RollbackReadinessGateNotPassed);
    public const string OperatorSignOffGateNotPassed = nameof(OperatorSignOffGateNotPassed);
    public const string GrantApplicationNoReadyCase = nameof(GrantApplicationNoReadyCase);
    public const string RollbackReadinessNoReadyCase = nameof(RollbackReadinessNoReadyCase);
    public const string OperatorSignOffNoRecordedCase = nameof(OperatorSignOffNoRecordedCase);
    public const string CapabilityMismatchAcrossUpstreamGates = nameof(CapabilityMismatchAcrossUpstreamGates);
    public const string ScopeMismatchAcrossUpstreamGates = nameof(ScopeMismatchAcrossUpstreamGates);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
}

/// <summary>V8.16 final gate 决策。Status + 各上游布尔位 + 绑定的 capability/scope。Crossed 永远 false。</summary>
public sealed class PreCrossingDecision
{
    public string Status { get; init; } = PreCrossingStatuses.PreCrossingBlocked;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool GrantApplicationGatePresent { get; init; }
    public bool RollbackReadinessGatePresent { get; init; }
    public bool OperatorSignOffGatePresent { get; init; }
    public bool GrantApplicationGatePassed { get; init; }
    public bool RollbackReadinessGatePassed { get; init; }
    public bool OperatorSignOffGatePassed { get; init; }
    public bool GrantApplicationReady { get; init; }
    public bool RollbackReady { get; init; }
    public bool OperatorSignOffRecorded { get; init; }
    public bool CapabilityAligned { get; init; }
    public bool ScopeAligned { get; init; }
    public bool CapabilityScopeAligned { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>关键不变量 — PreCrossingReady ≠ Crossed。</summary>
    public bool Crossed { get; init; }

    /// <summary>carry V8.13 — 应用边界未被实际跨过。</summary>
    public bool ApplicationApplied { get; init; }

    /// <summary>carry V8.14 — 回滚路径未被激活。</summary>
    public bool RollbackActivated { get; init; }
}

/// <summary>
/// V8.16 pre-crossing final gate policy。给定三个上游 gate report + 运行时/p15/mainline 标志，
/// 验证：上游 gate 全 passed、各自有 Ready/Recorded case、绑定同一 capability/scope。
/// 纯函数；不读 FS、不动 mainline、不跨过任何边界。
/// </summary>
public static class FormalRetrievalPromotionApprovalPreCrossingFinalGatePolicy
{
    public static PreCrossingDecision Evaluate(
        FormalRetrievalPromotionApprovalGrantApplicationMatrixReport? grantApplicationReport,
        FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport? rollbackReadinessReport,
        FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport? operatorSignOffReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();

        // step 1 — upstream gate presence。
        var grantPresent = grantApplicationReport is not null;
        var rollbackPresent = rollbackReadinessReport is not null;
        var signOffPresent = operatorSignOffReport is not null;

        if (!grantPresent) blocked.Add(PreCrossingBlockedReasons.GrantApplicationGateMissing);
        if (!rollbackPresent) blocked.Add(PreCrossingBlockedReasons.RollbackReadinessGateMissing);
        if (!signOffPresent) blocked.Add(PreCrossingBlockedReasons.OperatorSignOffGateMissing);

        // step 2 — upstream gate 通过状态。
        var grantGatePassed = grantApplicationReport?.GatePassed ?? false;
        var rollbackGatePassed = rollbackReadinessReport?.GatePassed ?? false;
        var signOffGatePassed = operatorSignOffReport?.GatePassed ?? false;

        if (grantPresent && !grantGatePassed) blocked.Add(PreCrossingBlockedReasons.GrantApplicationGateNotPassed);
        if (rollbackPresent && !rollbackGatePassed) blocked.Add(PreCrossingBlockedReasons.RollbackReadinessGateNotPassed);
        if (signOffPresent && !signOffGatePassed) blocked.Add(PreCrossingBlockedReasons.OperatorSignOffGateNotPassed);

        // step 3 — readiness/recorded case 存在性。
        var grantReadyCase = grantApplicationReport?.Cases
            ?.FirstOrDefault(c => string.Equals(c.ActualStatus, GrantApplicationStatuses.GrantApplicationReady, StringComparison.Ordinal));
        var rollbackReadyCase = rollbackReadinessReport?.Cases
            ?.FirstOrDefault(c => string.Equals(c.ActualStatus, RollbackReadinessStatuses.RollbackReady, StringComparison.Ordinal));
        var signOffRecordedCase = operatorSignOffReport?.Cases
            ?.FirstOrDefault(c => string.Equals(c.ActualStatus, OperatorSignOffStatuses.OperatorSignOffRecorded, StringComparison.Ordinal));

        var grantReady = grantReadyCase is not null;
        var rollbackReady = rollbackReadyCase is not null;
        var signOffRecorded = signOffRecordedCase is not null;

        if (grantPresent && !grantReady) blocked.Add(PreCrossingBlockedReasons.GrantApplicationNoReadyCase);
        if (rollbackPresent && !rollbackReady) blocked.Add(PreCrossingBlockedReasons.RollbackReadinessNoReadyCase);
        if (signOffPresent && !signOffRecorded) blocked.Add(PreCrossingBlockedReasons.OperatorSignOffNoRecordedCase);

        // step 4 — capability/scope 绑定一致性。只有三个 ready case 都存在才能比较。
        var boundCapability = grantReadyCase?.RequestedCapability ?? string.Empty;
        var boundScope = grantReadyCase?.RequestedScope ?? string.Empty;
        var capabilityAligned = false;
        var scopeAligned = false;

        if (grantReady && rollbackReady && signOffRecorded)
        {
            capabilityAligned =
                string.Equals(grantReadyCase!.RequestedCapability, rollbackReadyCase!.RequestedCapability, StringComparison.Ordinal)
                && string.Equals(grantReadyCase.RequestedCapability, signOffRecordedCase!.RequestedCapability, StringComparison.Ordinal);
            scopeAligned =
                string.Equals(grantReadyCase.RequestedScope, rollbackReadyCase.RequestedScope, StringComparison.Ordinal)
                && string.Equals(grantReadyCase.RequestedScope, signOffRecordedCase.RequestedScope, StringComparison.Ordinal);

            if (!capabilityAligned) blocked.Add(PreCrossingBlockedReasons.CapabilityMismatchAcrossUpstreamGates);
            if (!scopeAligned) blocked.Add(PreCrossingBlockedReasons.ScopeMismatchAcrossUpstreamGates);
        }

        // step 5 — baseline runtime / p15 / mainline 文件契约。
        if (!rtPassed) blocked.Add(PreCrossingBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(PreCrossingBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(PreCrossingBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(PreCrossingBlockedReasons.MainlineTrustRegistryPresent);

        var distinctBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var status = distinctBlocked.Length == 0
            ? PreCrossingStatuses.PreCrossingReady
            : PreCrossingStatuses.PreCrossingBlocked;

        var reasoning = status == PreCrossingStatuses.PreCrossingReady
            ? $"all upstream gates present and passed; readiness cases bound to capability='{boundCapability}' scope='{boundScope}'; runtime/p15 OK; no mainline files. PreCrossingReady. Note: PreCrossingReady != Crossed; application boundary remains uncrossed."
            : $"{distinctBlocked.Length} blocked reason(s); pre-crossing final gate did not pass.";

        return new PreCrossingDecision
        {
            Status = status,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            GrantApplicationGatePresent = grantPresent,
            RollbackReadinessGatePresent = rollbackPresent,
            OperatorSignOffGatePresent = signOffPresent,
            GrantApplicationGatePassed = grantGatePassed,
            RollbackReadinessGatePassed = rollbackGatePassed,
            OperatorSignOffGatePassed = signOffGatePassed,
            GrantApplicationReady = grantReady,
            RollbackReady = rollbackReady,
            OperatorSignOffRecorded = signOffRecorded,
            CapabilityAligned = capabilityAligned,
            ScopeAligned = scopeAligned,
            CapabilityScopeAligned = capabilityAligned && scopeAligned,
            BlockedReasons = distinctBlocked,
            Reasoning = reasoning,
            Crossed = false,
            ApplicationApplied = false,
            RollbackActivated = false
        };
    }
}
