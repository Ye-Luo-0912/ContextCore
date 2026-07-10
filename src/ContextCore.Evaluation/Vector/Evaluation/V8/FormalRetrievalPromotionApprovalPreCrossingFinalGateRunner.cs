using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner
{
    private const string BoundCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string BoundScope = "demo-workspace/demo-collection";

    /// <summary>
    /// 给定从磁盘加载的上游 gate report（可能为 null），以及运行时/p15/mainline-presence 标志，
    /// 跑 ≥10 个 scenarios（每个 case 不同的 upstream 配置），核对最终 PreCrossingReady / PreCrossingBlocked。
    /// </summary>
    public FormalRetrievalPromotionApprovalPreCrossingFinalGateReport Run(
        FormalRetrievalPromotionApprovalGrantApplicationMatrixReport? loadedGrantApplicationReport,
        FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport? loadedRollbackReadinessReport,
        FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport? loadedOperatorSignOffReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalPreCrossingFinalGateCase>();

        // 三个上游 fixture-clean 基线 report — 用于裁切/替换以模拟每个 scenario。
        var cleanGrant = BuildCleanGrantApplicationReport(BoundCapability, BoundScope, gatePassed: true);
        var cleanRollback = BuildCleanRollbackReadinessReport(BoundCapability, BoundScope, gatePassed: true);
        var cleanSignOff = BuildCleanOperatorSignOffReport(BoundCapability, BoundScope, gatePassed: true);

        foreach (var scenario in BuildScenarios(cleanGrant, cleanRollback, cleanSignOff))
        {
            var decision = FormalRetrievalPromotionApprovalPreCrossingFinalGatePolicy.Evaluate(
                scenario.GrantApplicationReport,
                scenario.RollbackReadinessReport,
                scenario.OperatorSignOffReport,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var notCrossed = !decision.Crossed;
            var applicationNotApplied = !decision.ApplicationApplied;
            var rollbackNotActivated = !decision.RollbackActivated;

            var passedAsExpected = statusMatched
                && blockedReasonMatched
                && notCrossed
                && applicationNotApplied
                && rollbackNotActivated;

            cases.Add(new FormalRetrievalPromotionApprovalPreCrossingFinalGateCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                GrantApplicationGatePresent = decision.GrantApplicationGatePresent,
                RollbackReadinessGatePresent = decision.RollbackReadinessGatePresent,
                OperatorSignOffGatePresent = decision.OperatorSignOffGatePresent,
                GrantApplicationGatePassed = decision.GrantApplicationGatePassed,
                RollbackReadinessGatePassed = decision.RollbackReadinessGatePassed,
                OperatorSignOffGatePassed = decision.OperatorSignOffGatePassed,
                GrantApplicationReady = decision.GrantApplicationReady,
                RollbackReady = decision.RollbackReady,
                OperatorSignOffRecorded = decision.OperatorSignOffRecorded,
                CapabilityAligned = decision.CapabilityAligned,
                ScopeAligned = decision.ScopeAligned,
                CapabilityScopeAligned = decision.CapabilityScopeAligned,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                NotCrossed = notCrossed,
                ApplicationNotApplied = applicationNotApplied,
                RollbackNotActivated = rollbackNotActivated,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var readyCases = cases.Count(static c => c.ActualStatus == PreCrossingStatuses.PreCrossingReady);
        var blockedCases = cases.Count(static c => c.ActualStatus == PreCrossingStatuses.PreCrossingBlocked);
        const int notApplicableCases = 0;

        var blocked = new List<string>();
        if (cases.Count < 10)
        {
            blocked.Add("InsufficientPreCrossingFinalGateCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("PreCrossingFinalGateMatrixFailed");
        }

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[] { PreCrossingStatuses.PreCrossingReady, PreCrossingStatuses.PreCrossingBlocked })
        {
            if (!statusesCovered.Contains(s))
            {
                blocked.Add($"StatusBranchNotCovered:{s}");
            }
        }

        // 关键 invariants — 四重契约必须在每个 case 都成立。
        if (cases.Any(c => !c.NotCrossed))
        {
            blocked.Add("ApplicationBoundaryWasCrossed");
        }

        if (cases.Any(c => !c.ApplicationNotApplied))
        {
            blocked.Add("ApplicationWasApplied");
        }

        if (cases.Any(c => !c.RollbackNotActivated))
        {
            blocked.Add("RollbackPathWasActivated");
        }

        // 真实加载的 upstream gate（不是 scenarios 内的合成数据）也必须 GatePassed=true，否则 matrix 不能宣称 final-gate 通过。
        var realGrantPassed = loadedGrantApplicationReport?.GatePassed ?? false;
        var realRollbackPassed = loadedRollbackReadinessReport?.GatePassed ?? false;
        var realSignOffPassed = loadedOperatorSignOffReport?.GatePassed ?? false;

        if (loadedGrantApplicationReport is null) blocked.Add("RealGrantApplicationGateArtifactMissing");
        if (loadedRollbackReadinessReport is null) blocked.Add("RealRollbackReadinessGateArtifactMissing");
        if (loadedOperatorSignOffReport is null) blocked.Add("RealOperatorSignOffGateArtifactMissing");

        if (loadedGrantApplicationReport is not null && !realGrantPassed) blocked.Add("RealGrantApplicationGateNotPassed");
        if (loadedRollbackReadinessReport is not null && !realRollbackPassed) blocked.Add("RealRollbackReadinessGateNotPassed");
        if (loadedOperatorSignOffReport is not null && !realSignOffPassed) blocked.Add("RealOperatorSignOffGateNotPassed");

        if (mainlineEvidencePresent) blocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) blocked.Add("MainlineTrustRegistryPresent");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var matrixPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && matrixPassed;

        // 在矩阵宣告 final-gate Ready 的同时，再核对一遍真实加载的 upstream 配置（不是 scenarios）。
        var realAligned = false;
        string? realBoundCapability = null;
        string? realBoundScope = null;
        if (matrixPassed
            && loadedGrantApplicationReport is not null
            && loadedRollbackReadinessReport is not null
            && loadedOperatorSignOffReport is not null)
        {
            var realGrantReady = loadedGrantApplicationReport.Cases?
                .FirstOrDefault(c => string.Equals(c.ActualStatus, GrantApplicationStatuses.GrantApplicationReady, StringComparison.Ordinal));
            var realRollbackReady = loadedRollbackReadinessReport.Cases?
                .FirstOrDefault(c => string.Equals(c.ActualStatus, RollbackReadinessStatuses.RollbackReady, StringComparison.Ordinal));
            var realSignOffRecorded = loadedOperatorSignOffReport.Cases?
                .FirstOrDefault(c => string.Equals(c.ActualStatus, OperatorSignOffStatuses.OperatorSignOffRecorded, StringComparison.Ordinal));

            if (realGrantReady is not null && realRollbackReady is not null && realSignOffRecorded is not null)
            {
                realBoundCapability = realGrantReady.RequestedCapability;
                realBoundScope = realGrantReady.RequestedScope;
                realAligned =
                    string.Equals(realBoundCapability, realRollbackReady.RequestedCapability, StringComparison.Ordinal)
                    && string.Equals(realBoundCapability, realSignOffRecorded.RequestedCapability, StringComparison.Ordinal)
                    && string.Equals(realBoundScope, realRollbackReady.RequestedScope, StringComparison.Ordinal)
                    && string.Equals(realBoundScope, realSignOffRecorded.RequestedScope, StringComparison.Ordinal);
            }
        }

        return new FormalRetrievalPromotionApprovalPreCrossingFinalGateReport
        {
            OperationId = $"frp-pre-crossing-final-gate-{Guid.NewGuid():N}",
            CreatedAt = now,
            PreCrossingFinalGatePassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            ReadyCases = readyCases,
            BlockedCases = blockedCases,
            NotApplicableCases = notApplicableCases,
            Cases = cases,
            // 真实磁盘加载的上游 gate state（独立于矩阵 scenarios）。
            UpstreamGrantApplicationGatePassed = realGrantPassed,
            UpstreamRollbackReadinessGatePassed = realRollbackPassed,
            UpstreamOperatorSignOffGatePassed = realSignOffPassed,
            BoundCapability = realBoundCapability ?? BoundCapability,
            BoundScope = realBoundScope ?? BoundScope,
            CapabilityScopeAligned = realAligned,
            // V8.16 显式契约 — final gate 不跨过、不应用、不激活回滚。
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
                $"notApplicable={notApplicableCases}",
                $"realGrantPassed={realGrantPassed}",
                $"realRollbackPassed={realRollbackPassed}",
                $"realSignOffPassed={realSignOffPassed}",
                $"realAligned={realAligned}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=DedicatedCrossingGate (the only path that may produce Crossed=true; this matrix never does)"
            }
        };
    }

    private static IReadOnlyList<PreCrossingScenario> BuildScenarios(
        FormalRetrievalPromotionApprovalGrantApplicationMatrixReport cleanGrant,
        FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport cleanRollback,
        FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport cleanSignOff) =>
    [
        new(
            "AllAlignedReady",
            cleanGrant, cleanRollback, cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingReady,
            ExpectedBlockedReason: null),
        new(
            "GrantApplicationGateMissing",
            null, cleanRollback, cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.GrantApplicationGateMissing),
        new(
            "RollbackReadinessGateMissing",
            cleanGrant, null, cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.RollbackReadinessGateMissing),
        new(
            "OperatorSignOffGateMissing",
            cleanGrant, cleanRollback, null,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.OperatorSignOffGateMissing),
        new(
            "GrantApplicationGateNotPassed",
            BuildCleanGrantApplicationReport(BoundCapability, BoundScope, gatePassed: false),
            cleanRollback, cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.GrantApplicationGateNotPassed),
        new(
            "RollbackReadinessGateNotPassed",
            cleanGrant,
            BuildCleanRollbackReadinessReport(BoundCapability, BoundScope, gatePassed: false),
            cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.RollbackReadinessGateNotPassed),
        new(
            "OperatorSignOffGateNotPassed",
            cleanGrant, cleanRollback,
            BuildCleanOperatorSignOffReport(BoundCapability, BoundScope, gatePassed: false),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.OperatorSignOffGateNotPassed),
        new(
            "GrantApplicationNoReadyCase",
            BuildGrantApplicationReportWithoutReadyCase(BoundCapability, BoundScope),
            cleanRollback, cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.GrantApplicationNoReadyCase),
        new(
            "RollbackReadinessNoReadyCase",
            cleanGrant,
            BuildRollbackReadinessReportWithoutReadyCase(BoundCapability, BoundScope),
            cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.RollbackReadinessNoReadyCase),
        new(
            "OperatorSignOffNoRecordedCase",
            cleanGrant, cleanRollback,
            BuildOperatorSignOffReportWithoutRecordedCase(BoundCapability, BoundScope),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.OperatorSignOffNoRecordedCase),
        new(
            "CapabilityMismatchAcrossGates",
            cleanGrant,
            BuildCleanRollbackReadinessReport(capability: "DivergentCapability", BoundScope, gatePassed: true),
            cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.CapabilityMismatchAcrossUpstreamGates),
        new(
            "ScopeMismatchAcrossGates",
            cleanGrant, cleanRollback,
            BuildCleanOperatorSignOffReport(BoundCapability, scope: "other-workspace/other-collection", gatePassed: true),
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.ScopeMismatchAcrossUpstreamGates),
        new(
            "RuntimeGateNotPassed",
            cleanGrant, cleanRollback, cleanSignOff,
            RtPassed: false, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.RuntimeChangeGateNotPassed),
        new(
            "P15GateNotPassed",
            cleanGrant, cleanRollback, cleanSignOff,
            RtPassed: true, P15Passed: false, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.P15GateNotPassed),
        new(
            "MainlineEvidencePresentBlocks",
            cleanGrant, cleanRollback, cleanSignOff,
            RtPassed: true, P15Passed: true, MainlineEvidencePresent: true, MainlineRegistryPresent: false,
            ExpectedStatus: PreCrossingStatuses.PreCrossingBlocked,
            ExpectedBlockedReason: PreCrossingBlockedReasons.MainlineEvidencePresent)
    ];

    public static FormalRetrievalPromotionApprovalGrantApplicationMatrixReport BuildCleanGrantApplicationReport(
        string capability, string scope, bool gatePassed) => new()
    {
        OperationId = "frp-grant-application-fixture",
        CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        GrantApplicationMatrixPassed = gatePassed,
        GatePassed = gatePassed,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        ReadyCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalGrantApplicationCase
            {
                CaseName = "ReadySynthetic",
                RequestedCapability = capability,
                RequestedScope = scope,
                ExpectedStatus = GrantApplicationStatuses.GrantApplicationReady,
                ActualStatus = GrantApplicationStatuses.GrantApplicationReady,
                StatusMatched = true,
                CountShapeOk = true,
                ApplicationNotApplied = true,
                PassedAsExpected = true
            }
        },
        ApplicationApplied = false,
        NoRuntimeMutationInvariant = true
    };

    public static FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport BuildCleanRollbackReadinessReport(
        string capability, string scope, bool gatePassed) => new()
    {
        OperationId = "frp-rollback-readiness-fixture",
        CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        RollbackReadinessMatrixPassed = gatePassed,
        GatePassed = gatePassed,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        ReadyCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalRollbackReadinessCase
            {
                CaseName = "RollbackReadySynthetic",
                RequestedCapability = capability,
                RequestedScope = scope,
                ExpectedStatus = RollbackReadinessStatuses.RollbackReady,
                ActualStatus = RollbackReadinessStatuses.RollbackReady,
                StatusMatched = true,
                CountShapeOk = true,
                RollbackNotActivated = true,
                ApplicationNotApplied = true,
                PassedAsExpected = true
            }
        },
        RollbackActivated = false,
        ApplicationApplied = false,
        NoRuntimeMutationInvariant = true
    };

    public static FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport BuildCleanOperatorSignOffReport(
        string capability, string scope, bool gatePassed) => new()
    {
        OperationId = "frp-operator-sign-off-fixture",
        CreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        OperatorSignOffMatrixPassed = gatePassed,
        GatePassed = gatePassed,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        RecordedCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalOperatorSignOffCase
            {
                CaseName = "RecordedSynthetic",
                RequestedCapability = capability,
                RequestedScope = scope,
                ExpectedStatus = OperatorSignOffStatuses.OperatorSignOffRecorded,
                ActualStatus = OperatorSignOffStatuses.OperatorSignOffRecorded,
                StatusMatched = true,
                CountShapeOk = true,
                NotCrossed = true,
                ApplicationNotApplied = true,
                RollbackNotActivated = true,
                PassedAsExpected = true
            }
        },
        Crossed = false,
        ApplicationApplied = false,
        RollbackActivated = false,
        NoRuntimeMutationInvariant = true
    };

    private static FormalRetrievalPromotionApprovalGrantApplicationMatrixReport BuildGrantApplicationReportWithoutReadyCase(
        string capability, string scope)
    {
        var clean = BuildCleanGrantApplicationReport(capability, scope, gatePassed: true);
        return new FormalRetrievalPromotionApprovalGrantApplicationMatrixReport
        {
            OperationId = clean.OperationId,
            CreatedAt = clean.CreatedAt,
            GrantApplicationMatrixPassed = clean.GrantApplicationMatrixPassed,
            GatePassed = clean.GatePassed,
            TotalCases = 1,
            PassedCases = 1,
            FailedCases = 0,
            ReadyCases = 0,
            Cases = new[]
            {
                new FormalRetrievalPromotionApprovalGrantApplicationCase
                {
                    CaseName = "BlockedOnly",
                    RequestedCapability = capability,
                    RequestedScope = scope,
                    ExpectedStatus = GrantApplicationStatuses.GrantApplicationBlocked,
                    ActualStatus = GrantApplicationStatuses.GrantApplicationBlocked,
                    ApplicationNotApplied = true,
                    PassedAsExpected = true
                }
            },
            ApplicationApplied = false,
            NoRuntimeMutationInvariant = true
        };
    }

    private static FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport BuildRollbackReadinessReportWithoutReadyCase(
        string capability, string scope)
    {
        var clean = BuildCleanRollbackReadinessReport(capability, scope, gatePassed: true);
        return new FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport
        {
            OperationId = clean.OperationId,
            CreatedAt = clean.CreatedAt,
            RollbackReadinessMatrixPassed = clean.RollbackReadinessMatrixPassed,
            GatePassed = clean.GatePassed,
            TotalCases = 1,
            PassedCases = 1,
            FailedCases = 0,
            ReadyCases = 0,
            Cases = new[]
            {
                new FormalRetrievalPromotionApprovalRollbackReadinessCase
                {
                    CaseName = "IncompleteOnly",
                    RequestedCapability = capability,
                    RequestedScope = scope,
                    ExpectedStatus = RollbackReadinessStatuses.RollbackReadinessIncomplete,
                    ActualStatus = RollbackReadinessStatuses.RollbackReadinessIncomplete,
                    RollbackNotActivated = true,
                    ApplicationNotApplied = true,
                    PassedAsExpected = true
                }
            },
            RollbackActivated = false,
            ApplicationApplied = false,
            NoRuntimeMutationInvariant = true
        };
    }

    private static FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport BuildOperatorSignOffReportWithoutRecordedCase(
        string capability, string scope)
    {
        var clean = BuildCleanOperatorSignOffReport(capability, scope, gatePassed: true);
        return new FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport
        {
            OperationId = clean.OperationId,
            CreatedAt = clean.CreatedAt,
            OperatorSignOffMatrixPassed = clean.OperatorSignOffMatrixPassed,
            GatePassed = clean.GatePassed,
            TotalCases = 1,
            PassedCases = 1,
            FailedCases = 0,
            RecordedCases = 0,
            Cases = new[]
            {
                new FormalRetrievalPromotionApprovalOperatorSignOffCase
                {
                    CaseName = "InsufficientOnly",
                    RequestedCapability = capability,
                    RequestedScope = scope,
                    ExpectedStatus = OperatorSignOffStatuses.OperatorSignOffInsufficient,
                    ActualStatus = OperatorSignOffStatuses.OperatorSignOffInsufficient,
                    NotCrossed = true,
                    ApplicationNotApplied = true,
                    RollbackNotActivated = true,
                    PassedAsExpected = true
                }
            },
            Crossed = false,
            ApplicationApplied = false,
            RollbackActivated = false,
            NoRuntimeMutationInvariant = true
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalPreCrossingFinalGateReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PreCrossingFinalGatePassed: `{r.PreCrossingFinalGatePassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Status — Ready: `{r.ReadyCases}` Blocked: `{r.BlockedCases}` NotApplicable: `{r.NotApplicableCases}`");
        b.AppendLine();
        b.AppendLine("## Upstream (Real Loaded Artifacts)");
        b.AppendLine($"- UpstreamGrantApplicationGatePassed: `{r.UpstreamGrantApplicationGatePassed}`");
        b.AppendLine($"- UpstreamRollbackReadinessGatePassed: `{r.UpstreamRollbackReadinessGatePassed}`");
        b.AppendLine($"- UpstreamOperatorSignOffGatePassed: `{r.UpstreamOperatorSignOffGatePassed}`");
        b.AppendLine($"- BoundCapability: `{r.BoundCapability}`");
        b.AppendLine($"- BoundScope: `{r.BoundScope}`");
        b.AppendLine($"- CapabilityScopeAligned: `{r.CapabilityScopeAligned}`");
        b.AppendLine();
        b.AppendLine("## No-Crossing Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- GrantApplied: `{r.GrantApplied}`");
        b.AppendLine($"- ApplicationApplied: `{r.ApplicationApplied}`");
        b.AppendLine($"- RollbackActivated: `{r.RollbackActivated}`");
        b.AppendLine($"- Crossed: `{r.Crossed}`  (PreCrossingReady != Crossed; matrix never produces a crossing event)");
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
        b.AppendLine("## Pre-Crossing Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedBlockedReason))
            {
                b.AppendLine($"  - expectedReason=`{c.ExpectedBlockedReason}` matched=`{c.BlockedReasonMatched}`");
            }
            b.AppendLine($"  - bound: capability=`{c.BoundCapability}` scope=`{c.BoundScope}` aligned=`{c.CapabilityScopeAligned}`");
            b.AppendLine($"  - upstream present: grant=`{c.GrantApplicationGatePresent}` rollback=`{c.RollbackReadinessGatePresent}` signOff=`{c.OperatorSignOffGatePresent}`");
            b.AppendLine($"  - upstream passed: grant=`{c.GrantApplicationGatePassed}` rollback=`{c.RollbackReadinessGatePassed}` signOff=`{c.OperatorSignOffGatePassed}`");
            b.AppendLine($"  - readiness: grantReady=`{c.GrantApplicationReady}` rollbackReady=`{c.RollbackReady}` signOffRecorded=`{c.OperatorSignOffRecorded}`");
            b.AppendLine($"  - notCrossed=`{c.NotCrossed}` applicationNotApplied=`{c.ApplicationNotApplied}` rollbackNotActivated=`{c.RollbackNotActivated}`");
            if (c.ActualBlockedReasons.Count > 0)
            {
                b.AppendLine($"  - actualReasons=`{string.Join(", ", c.ActualBlockedReasons)}`");
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

        b.AppendLine("V8.16 pre-crossing final gate matrix。读取真实 V8.13/V8.14/V8.15 gate artifact + 15 个合成 scenarios 核对决策逻辑。PreCrossingReady ≠ Crossed — 这层 matrix 仅产出『可进入独立 crossing gate』的最终输入包；不跨过、不应用、不激活回滚、不启用 formal retrieval、不写 mainline。");
        return b.ToString();
    }
}

public sealed record PreCrossingScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalGrantApplicationMatrixReport? GrantApplicationReport,
    FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport? RollbackReadinessReport,
    FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport? OperatorSignOffReport,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalPreCrossingFinalGateCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
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
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }

    /// <summary>每个 case：应用边界未被跨过。</summary>
    public bool NotCrossed { get; init; }

    /// <summary>carry V8.13 — 应用未被实际应用。</summary>
    public bool ApplicationNotApplied { get; init; }

    /// <summary>carry V8.14 — 回滚路径未被激活。</summary>
    public bool RollbackNotActivated { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalPreCrossingFinalGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreCrossingFinalGatePassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public int NotApplicableCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalPreCrossingFinalGateCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalPreCrossingFinalGateCase>();

    // 真实上游 gate state
    public bool UpstreamGrantApplicationGatePassed { get; init; }
    public bool UpstreamRollbackReadinessGatePassed { get; init; }
    public bool UpstreamOperatorSignOffGatePassed { get; init; }
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool CapabilityScopeAligned { get; init; }

    // No-Crossing Contract
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

public sealed class FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
