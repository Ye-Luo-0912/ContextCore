using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";

    public FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport Run(
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport? loadedPreCrossingReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        bool realConfigPatchPathAlreadyExists,
        FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase>();

        var cleanPreCrossing = BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);

        foreach (var scenario in BuildScenarios(cleanPreCrossing))
        {
            var decision = FormalRetrievalPromotionApprovalDedicatedCrossingGatePolicy.Evaluate(
                scenario.PreCrossingReport,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent,
                scenario.Overrides);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var dryRunOnly = decision.DryRunOnly;
            var executionNotAllowed = !decision.CrossingExecutionAllowed;
            var notCrossed = !decision.Crossed;
            var applicationNotApplied = !decision.ApplicationApplied;
            var rollbackNotActivated = !decision.RollbackActivated;

            var passedAsExpected = statusMatched
                && blockedReasonMatched
                && dryRunOnly
                && executionNotAllowed
                && notCrossed
                && applicationNotApplied
                && rollbackNotActivated;

            cases.Add(new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                Contract = decision.Contract,
                PlannedArtifacts = decision.PlannedArtifacts,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                DryRunOnly = dryRunOnly,
                CrossingExecutionAllowed = decision.CrossingExecutionAllowed,
                NotCrossed = notCrossed,
                ApplicationNotApplied = applicationNotApplied,
                RollbackNotActivated = rollbackNotActivated,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var readyCases = cases.Count(static c => c.ActualStatus == CrossingDryRunStatuses.CrossingDryRunReady);
        var blockedCases = cases.Count(static c => c.ActualStatus == CrossingDryRunStatuses.CrossingDryRunBlocked);

        var blocked = new List<string>();
        if (cases.Count < 15)
        {
            blocked.Add("InsufficientCrossingDryRunCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("CrossingDryRunMatrixFailed");
        }

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[] { CrossingDryRunStatuses.CrossingDryRunReady, CrossingDryRunStatuses.CrossingDryRunBlocked })
        {
            if (!statusesCovered.Contains(s))
            {
                blocked.Add($"StatusBranchNotCovered:{s}");
            }
        }

        // 五重不变量 — 每个 case 都必须保持 DryRunOnly=true + ExecutionNotAllowed + 3-tier no-action。
        if (cases.Any(c => !c.DryRunOnly)) blocked.Add("DryRunOnlyViolated");
        if (cases.Any(c => c.CrossingExecutionAllowed)) blocked.Add("CrossingExecutionWasAllowed");
        if (cases.Any(c => !c.NotCrossed)) blocked.Add("ApplicationBoundaryWasCrossed");
        if (cases.Any(c => !c.ApplicationNotApplied)) blocked.Add("ApplicationWasApplied");
        if (cases.Any(c => !c.RollbackNotActivated)) blocked.Add("RollbackPathWasActivated");

        // 真实上游 V8.16 artifact 必须就位 + 通过。
        var realUpstreamPresent = loadedPreCrossingReport is not null;
        var realUpstreamPassed = loadedPreCrossingReport?.GatePassed ?? false;
        var realUpstreamFinalGatePassed = loadedPreCrossingReport?.PreCrossingFinalGatePassed ?? false;

        if (!realUpstreamPresent) blocked.Add("RealPreCrossingGateArtifactMissing");
        if (realUpstreamPresent && !realUpstreamPassed) blocked.Add("RealPreCrossingGateNotPassed");
        if (realUpstreamPresent && !realUpstreamFinalGatePassed) blocked.Add("RealPreCrossingFinalGateNotPassed");

        // 真实磁盘核对 — planned artifact paths 都不得存在。
        var realContract = realUpstreamPresent
            ? BuildRealContractFromUpstream(loadedPreCrossingReport!)
            : new CrossingExecutionContract();
        var realPlannedPaths = new[]
        {
            realContract.PlannedCapabilityGrantPath,
            realContract.PlannedRuntimeConfigPatchPath,
            realContract.PlannedRollbackSnapshotPath,
            realContract.PlannedAuditLogPath,
            realContract.PlannedRevocationRecordPath
        }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

        var realExistingPaths = realPlannedPaths
            .Where(System.IO.File.Exists)
            .ToArray();

        // 幂等性核对 — 如果所有 5 个 planned path 都已存在且 capability-grant artifact 绑定到当前 capability/scope，
        // 视为 "V8.18 已经成功 crossed"。此时 V8.17 dry-run 不再"会覆盖" — crossing 已完成，dry-run plan 仍一致。
        var crossingAlreadyExecuted = false;
        if (realExistingPaths.Length == realPlannedPaths.Length && realPlannedPaths.Length == 5)
        {
            try
            {
                var existingGrantJson = System.IO.File.ReadAllText(realPlannedPaths[0]);
                using var doc = System.Text.Json.JsonDocument.Parse(existingGrantJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Capability", out var capProp)
                    && root.TryGetProperty("Scope", out var scopeProp)
                    && string.Equals(capProp.GetString(), realContract.PlannedCapability, StringComparison.Ordinal)
                    && string.Equals(scopeProp.GetString(), realContract.PlannedScope, StringComparison.Ordinal))
                {
                    crossingAlreadyExecuted = true;
                }
            }
            catch
            {
                // parse 失败 → 走原始 block 路径。
            }
        }

        if (!crossingAlreadyExecuted)
        {
            foreach (var existingPath in realExistingPaths)
            {
                blocked.Add($"PlannedArtifactAlreadyExists:{System.IO.Path.GetFileName(existingPath)}");
            }

            // ConfigPatch path 已存在 — 单独检测以便和 spec 中的命名对齐。
            if (realConfigPatchPathAlreadyExists)
            {
                blocked.Add("RealConfigPatchPathWouldOverwrite");
            }
        }

        if (mainlineEvidencePresent) blocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) blocked.Add("MainlineTrustRegistryPresent");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var matrixPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && matrixPassed;

        return new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport
        {
            OperationId = $"frp-dedicated-crossing-dry-run-{Guid.NewGuid():N}",
            CreatedAt = now,
            CrossingDryRunMatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            ReadyCases = readyCases,
            BlockedCases = blockedCases,
            Cases = cases,
            BoundCapability = realContract.PlannedCapability,
            BoundScope = realContract.PlannedScope,
            PlannedArtifacts = realPlannedPaths,
            Contract = realContract,
            UpstreamPreCrossingGatePresent = realUpstreamPresent,
            UpstreamPreCrossingGatePassed = realUpstreamPassed,
            UpstreamPreCrossingFinalGatePassed = realUpstreamFinalGatePassed,
            // V8.17 显式契约 — DryRun only，从不执行 crossing。
            DryRunOnly = true,
            CrossingExecutionAllowed = false,
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
                $"ready={readyCases}",
                $"blocked={blockedCases}",
                $"realUpstreamPresent={realUpstreamPresent}",
                $"realUpstreamPassed={realUpstreamPassed}",
                $"realConfigPatchPathAlreadyExists={realConfigPatchPathAlreadyExists}",
                $"realExistingPlannedPaths={realExistingPaths.Length}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=ExplicitCrossingExecutionGate (only that gate may produce Crossed=true; this matrix never does, never writes planned artifacts)"
            }
        };
    }

    private static IReadOnlyList<CrossingDryRunScenario> BuildScenarios(
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport cleanPreCrossing) =>
    [
        new(
            "AllAlignedReady",
            cleanPreCrossing,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunReady,
            ExpectedBlockedReason: null),
        new(
            "PreCrossingGateMissing",
            PreCrossingReport: null,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.PreCrossingGateMissing),
        new(
            "PreCrossingGateNotPassed",
            BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: false),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.PreCrossingGateNotPassed),
        new(
            "NoPreCrossingReadyCase",
            BuildPreCrossingReportWithoutReadyCase(TestCapability, TestScope),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.NoPreCrossingReadyCase),
        new(
            "CapabilityMismatch",
            BuildCleanPreCrossingReport(capability: "UnauthorizedCapability", TestScope, gatePassed: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.CapabilityMismatch),
        new(
            "EmptyScope",
            BuildCleanPreCrossingReport(TestCapability, scope: string.Empty, gatePassed: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.EmptyScope),
        new(
            "GlobalScope",
            BuildCleanPreCrossingReport(TestCapability, scope: "*", gatePassed: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.GlobalScopeForbidden),
        new(
            "UpstreamCrossedTrue",
            BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true, upstreamCrossed: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.UpstreamCrossedTrue),
        new(
            "UpstreamApplicationApplied",
            BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true, upstreamAppApplied: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.UpstreamApplicationApplied),
        new(
            "UpstreamRollbackActivated",
            BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true, upstreamRollbackActivated: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.UpstreamRollbackActivated),
        new(
            "RuntimeGateNotPassed",
            cleanPreCrossing,
            RtPassed: false, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.RuntimeChangeGateNotPassed),
        new(
            "P15GateNotPassed",
            cleanPreCrossing,
            RtPassed: true, P15Passed: false, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.P15GateNotPassed),
        new(
            "MainlineEvidencePresent",
            cleanPreCrossing,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: true, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.MainlineEvidencePresent),
        new(
            "MainlineTrustRegistryPresent",
            cleanPreCrossing,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: true,
            Overrides: new CrossingDryRunPlanOverrides(),
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.MainlineTrustRegistryPresent),
        new(
            "ConfigPatchPathWouldOverwrite",
            cleanPreCrossing,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides { ConfigPatchPathAlreadyExists = true },
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.PlannedConfigPatchPathWouldOverwrite),
        new(
            "RollbackSnapshotPathMissing",
            cleanPreCrossing,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            Overrides: new CrossingDryRunPlanOverrides { RollbackSnapshotPathMissing = true },
            ExpectedStatus: CrossingDryRunStatuses.CrossingDryRunBlocked,
            ExpectedBlockedReason: CrossingDryRunBlockedReasons.PlannedRollbackSnapshotPathMissing)
    ];

    public static FormalRetrievalPromotionApprovalPreCrossingFinalGateReport BuildCleanPreCrossingReport(
        string capability, string scope, bool gatePassed,
        bool upstreamCrossed = false,
        bool upstreamAppApplied = false,
        bool upstreamRollbackActivated = false) => new()
    {
        OperationId = "frp-pre-crossing-fixture-for-v8-17",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        PreCrossingFinalGatePassed = gatePassed,
        GatePassed = gatePassed,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        ReadyCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalPreCrossingFinalGateCase
            {
                CaseName = "ReadySynthetic",
                ExpectedStatus = PreCrossingStatuses.PreCrossingReady,
                ActualStatus = PreCrossingStatuses.PreCrossingReady,
                BoundCapability = capability,
                BoundScope = scope,
                CapabilityAligned = true,
                ScopeAligned = true,
                CapabilityScopeAligned = true,
                GrantApplicationReady = true,
                RollbackReady = true,
                OperatorSignOffRecorded = true,
                StatusMatched = true,
                BlockedReasonMatched = true,
                NotCrossed = true,
                ApplicationNotApplied = true,
                RollbackNotActivated = true,
                PassedAsExpected = true
            }
        },
        UpstreamGrantApplicationGatePassed = true,
        UpstreamRollbackReadinessGatePassed = true,
        UpstreamOperatorSignOffGatePassed = true,
        BoundCapability = capability,
        BoundScope = scope,
        CapabilityScopeAligned = true,
        Crossed = upstreamCrossed,
        ApplicationApplied = upstreamAppApplied,
        RollbackActivated = upstreamRollbackActivated,
        NoRuntimeMutationInvariant = true
    };

    private static FormalRetrievalPromotionApprovalPreCrossingFinalGateReport BuildPreCrossingReportWithoutReadyCase(
        string capability, string scope) => new()
    {
        OperationId = "frp-pre-crossing-fixture-no-ready",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        PreCrossingFinalGatePassed = true,
        GatePassed = true,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        ReadyCases = 0,
        BlockedCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalPreCrossingFinalGateCase
            {
                CaseName = "BlockedOnly",
                ExpectedStatus = PreCrossingStatuses.PreCrossingBlocked,
                ActualStatus = PreCrossingStatuses.PreCrossingBlocked,
                BoundCapability = capability,
                BoundScope = scope,
                NotCrossed = true,
                ApplicationNotApplied = true,
                RollbackNotActivated = true,
                PassedAsExpected = true
            }
        },
        UpstreamGrantApplicationGatePassed = true,
        UpstreamRollbackReadinessGatePassed = true,
        UpstreamOperatorSignOffGatePassed = true,
        BoundCapability = capability,
        BoundScope = scope,
        CapabilityScopeAligned = false,
        NoRuntimeMutationInvariant = true
    };

    private static CrossingExecutionContract BuildRealContractFromUpstream(
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport upstream)
    {
        var capability = string.IsNullOrEmpty(upstream.BoundCapability) ? TestCapability : upstream.BoundCapability;
        var scope = string.IsNullOrEmpty(upstream.BoundScope) ? TestScope : upstream.BoundScope;
        var decision = FormalRetrievalPromotionApprovalDedicatedCrossingGatePolicy.Evaluate(
            upstream, rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            overrides: new CrossingDryRunPlanOverrides());
        // 即便 decision Blocked，contract 永远附带；scenarios 内的 contract 路径也用作真实磁盘核对参照。
        return decision.Contract;
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- CrossingDryRunMatrixPassed: `{r.CrossingDryRunMatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — Ready: `{r.ReadyCases}` Blocked: `{r.BlockedCases}`");
        b.AppendLine();
        b.AppendLine("## Crossing Execution Contract (Planned, Not Written)");
        b.AppendLine($"- DryRunOnly: `{r.DryRunOnly}`");
        b.AppendLine($"- CrossingExecutionAllowed: `{r.CrossingExecutionAllowed}` (always false from this matrix)");
        b.AppendLine($"- PlannedCapability: `{r.Contract.PlannedCapability}`");
        b.AppendLine($"- PlannedScope: `{r.Contract.PlannedScope}`");
        b.AppendLine($"- PlannedCapabilityGrantPath: `{r.Contract.PlannedCapabilityGrantPath}`");
        b.AppendLine($"- PlannedRuntimeConfigPatchPath: `{r.Contract.PlannedRuntimeConfigPatchPath}`");
        b.AppendLine($"- PlannedRollbackSnapshotPath: `{r.Contract.PlannedRollbackSnapshotPath}`");
        b.AppendLine($"- PlannedAuditLogPath: `{r.Contract.PlannedAuditLogPath}`");
        b.AppendLine($"- PlannedRevocationRecordPath: `{r.Contract.PlannedRevocationRecordPath}`");
        b.AppendLine();
        b.AppendLine("## Upstream (Real)");
        b.AppendLine($"- UpstreamPreCrossingGatePresent: `{r.UpstreamPreCrossingGatePresent}`");
        b.AppendLine($"- UpstreamPreCrossingGatePassed: `{r.UpstreamPreCrossingGatePassed}`");
        b.AppendLine($"- UpstreamPreCrossingFinalGatePassed: `{r.UpstreamPreCrossingFinalGatePassed}`");
        b.AppendLine($"- BoundCapability: `{r.BoundCapability}`");
        b.AppendLine($"- BoundScope: `{r.BoundScope}`");
        b.AppendLine();
        b.AppendLine("## No-Crossing Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- GrantApplied: `{r.GrantApplied}`");
        b.AppendLine($"- ApplicationApplied: `{r.ApplicationApplied}`");
        b.AppendLine($"- RollbackActivated: `{r.RollbackActivated}`");
        b.AppendLine($"- Crossed: `{r.Crossed}`");
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
        b.AppendLine("## Crossing Dry-Run Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedBlockedReason))
            {
                b.AppendLine($"  - expectedReason=`{c.ExpectedBlockedReason}` matched=`{c.BlockedReasonMatched}`");
            }
            b.AppendLine($"  - bound: capability=`{c.BoundCapability}` scope=`{c.BoundScope}`");
            b.AppendLine($"  - dryRunOnly=`{c.DryRunOnly}` executionAllowed=`{c.CrossingExecutionAllowed}` notCrossed=`{c.NotCrossed}` applicationNotApplied=`{c.ApplicationNotApplied}` rollbackNotActivated=`{c.RollbackNotActivated}`");
            if (c.ActualBlockedReasons.Count > 0)
            {
                b.AppendLine($"  - actualReasons=`{string.Join(", ", c.ActualBlockedReasons)}`");
            }
            if (c.PlannedArtifacts.Count > 0)
            {
                b.AppendLine($"  - plannedArtifacts=`{string.Join(", ", c.PlannedArtifacts)}`");
            }
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

        b.AppendLine("V8.17 dedicated crossing gate dry-run matrix。读取 V8.16 PreCrossing artifact + 16 个 scenarios 验证决策逻辑 + 输出 crossing execution contract（5 个 planned artifact path）。CrossingDryRunReady ≠ Crossed — 这层 matrix 仅产出 dry-run plan；不写 planned artifact、不应用 grant、不修改 runtime config、不启用 formal retrieval。");
        return b.ToString();
    }
}

public sealed record CrossingDryRunScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalPreCrossingFinalGateReport? PreCrossingReport,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    CrossingDryRunPlanOverrides Overrides,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public CrossingExecutionContract Contract { get; init; } = new();
    public IReadOnlyList<string> PlannedArtifacts { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool DryRunOnly { get; init; }
    public bool CrossingExecutionAllowed { get; init; }
    public bool NotCrossed { get; init; }
    public bool ApplicationNotApplied { get; init; }
    public bool RollbackNotActivated { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool CrossingDryRunMatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase>();

    // 真实上游 + planned contract
    public bool UpstreamPreCrossingGatePresent { get; init; }
    public bool UpstreamPreCrossingGatePassed { get; init; }
    public bool UpstreamPreCrossingFinalGatePassed { get; init; }
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public CrossingExecutionContract Contract { get; init; } = new();
    public IReadOnlyList<string> PlannedArtifacts { get; init; } = Array.Empty<string>();

    // No-Crossing Contract
    public bool DryRunOnly { get; init; }
    public bool CrossingExecutionAllowed { get; init; }
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool GrantApplied { get; init; }
    public bool ApplicationApplied { get; init; }
    public bool RollbackActivated { get; init; }
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

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
