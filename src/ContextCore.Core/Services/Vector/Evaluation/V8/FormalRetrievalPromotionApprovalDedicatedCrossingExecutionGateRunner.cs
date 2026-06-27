using System.Text;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";

    public FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport Run(
        FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport? loadedDryRunReport,
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport? loadedPreCrossingReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        Func<string, bool>? realPathExists,
        Func<CrossingExecutionDecision, FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult>? realWriter,
        FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateCase>();

        var cleanDryRun = BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: true);

        // ====== matrix scenarios — pure policy evaluation, no writes ======
        foreach (var scenario in BuildScenarios(cleanDryRun))
        {
            var decision = FormalRetrievalPromotionApprovalDedicatedCrossingExecutionPolicy.Evaluate(
                scenario.DryRunReport,
                scenario.PreCrossingReport,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent,
                scenario.PathExistence,
                scenario.Overrides);

            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var runtimeNotActivated = !decision.RuntimeActivation;
            var formalRetrievalNotAllowed = !decision.FormalRetrievalAllowed;
            var artifactOnly = decision.ArtifactOnly;
            // 在 matrix 模式下，无论 scenarios Decision 怎么写，"实际"写入步骤永远没发生（matrix 不调 writer）。
            var passedAsExpected = statusMatched
                && blockedReasonMatched
                && runtimeNotActivated
                && formalRetrievalNotAllowed
                && artifactOnly;

            cases.Add(new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                PlannedArtifactPaths = decision.PlannedArtifactPaths,
                DecisionCrossed = decision.Crossed,
                ArtifactOnly = decision.ArtifactOnly,
                RuntimeActivation = decision.RuntimeActivation,
                FormalRetrievalAllowed = decision.FormalRetrievalAllowed,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                RuntimeNotActivated = runtimeNotActivated,
                FormalRetrievalNotAllowed = formalRetrievalNotAllowed,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var executedCases = cases.Count(static c => c.ActualStatus == CrossingExecutionStatuses.DedicatedCrossingExecuted);
        var blockedCases = cases.Count(static c => c.ActualStatus == CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked);

        var matrixBlocked = new List<string>();
        if (cases.Count < 15) matrixBlocked.Add("InsufficientCrossingExecutionCases");
        if (failedCases > 0) matrixBlocked.Add("CrossingExecutionMatrixFailed");

        var statusesCovered = cases.Select(c => c.ActualStatus).ToHashSet(StringComparer.Ordinal);
        foreach (var s in new[] { CrossingExecutionStatuses.DedicatedCrossingExecuted, CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked })
        {
            if (!statusesCovered.Contains(s)) matrixBlocked.Add($"StatusBranchNotCovered:{s}");
        }

        // 三重不变量 — 每个 case 都必须 RuntimeActivation=false、FormalRetrievalAllowed=false、ArtifactOnly=true。
        if (cases.Any(c => c.RuntimeActivation)) matrixBlocked.Add("RuntimeActivationLeaked");
        if (cases.Any(c => c.FormalRetrievalAllowed)) matrixBlocked.Add("FormalRetrievalAllowedLeaked");
        if (cases.Any(c => !c.ArtifactOnly)) matrixBlocked.Add("ArtifactOnlyViolated");

        // ====== real run — load 真实 upstream + 真实磁盘核对 + 写出 5 个 artifact（仅 Status=Executed 时） ======
        realPathExists ??= System.IO.File.Exists;
        realWriter ??= decision => FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteAll(decision, now);

        var realUpstreamPresent = loadedDryRunReport is not null;
        var realUpstreamPassed = loadedDryRunReport?.GatePassed ?? false;
        var realDryRunOnly = loadedDryRunReport?.DryRunOnly ?? false;
        var realDryRunExecAllowed = loadedDryRunReport?.CrossingExecutionAllowed ?? false;

        if (!realUpstreamPresent) matrixBlocked.Add("RealDryRunGateArtifactMissing");
        if (realUpstreamPresent && !realUpstreamPassed) matrixBlocked.Add("RealDryRunGateNotPassed");

        // 预估真实 path 是否会覆盖。
        var realCapability = loadedDryRunReport?.BoundCapability ?? TestCapability;
        var realScope = loadedDryRunReport?.BoundScope ?? TestScope;
        var safeCapability = NormalizeForPath(realCapability);
        var safeScope = NormalizeForPath(realScope);
        var realPlannedPaths = new[]
        {
            $"{CrossingExecutionAllowedDirectory.Value}/capability-grant-{safeCapability}-{safeScope}.json",
            $"{CrossingExecutionAllowedDirectory.Value}/runtime-config-patch-{safeCapability}-{safeScope}.json",
            $"{CrossingExecutionAllowedDirectory.Value}/rollback-snapshot-{safeCapability}-{safeScope}.json",
            $"{CrossingExecutionAllowedDirectory.Value}/audit-log-{safeCapability}-{safeScope}.jsonl",
            $"{CrossingExecutionAllowedDirectory.Value}/revocation-record-{safeCapability}-{safeScope}.json"
        };

        var realPathExistence = new CrossingExecutionPathExistence
        {
            CapabilityGrantPathExists = realPathExists(realPlannedPaths[0]),
            RuntimeConfigPatchPathExists = realPathExists(realPlannedPaths[1]),
            RollbackSnapshotPathExists = realPathExists(realPlannedPaths[2]),
            AuditLogPathExists = realPathExists(realPlannedPaths[3]),
            RevocationRecordPathExists = realPathExists(realPlannedPaths[4])
        };

        // 幂等性核对 — 5 个 path 都已存在 + capability grant 中 Capability/Scope 与当前上游一致 → 视为"先前已 crossing"，
        // 不重新写、不动 gate state，但 Crossed/Written 仍标 true（事实如此）。
        bool idempotentReRun = false;
        if (realPathExistence.AnyExists
            && realPathExistence.CapabilityGrantPathExists
            && realPathExistence.RuntimeConfigPatchPathExists
            && realPathExistence.RollbackSnapshotPathExists
            && realPathExistence.AuditLogPathExists
            && realPathExistence.RevocationRecordPathExists)
        {
            try
            {
                var existingGrantJson = System.IO.File.ReadAllText(realPlannedPaths[0]);
                using var doc = System.Text.Json.JsonDocument.Parse(existingGrantJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Capability", out var capProp)
                    && root.TryGetProperty("Scope", out var scopeProp)
                    && string.Equals(capProp.GetString(), realCapability, StringComparison.Ordinal)
                    && string.Equals(scopeProp.GetString(), realScope, StringComparison.Ordinal))
                {
                    idempotentReRun = true;
                }
            }
            catch
            {
                // parse 失败 → 走原始 block 路径，让 "RealCrossingExecution:PlannedArtifactAlreadyExists" fire。
            }
        }

        var realDecision = FormalRetrievalPromotionApprovalDedicatedCrossingExecutionPolicy.Evaluate(
            loadedDryRunReport,
            loadedPreCrossingReport,
            rtPassed,
            p15Passed,
            mainlineEvidencePresent,
            mainlineRegistryPresent,
            idempotentReRun ? new CrossingExecutionPathExistence() : realPathExistence,
            new CrossingExecutionOverrides());

        FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult? writeResult = null;
        var artifactsWritten = false;

        if (idempotentReRun && realDecision.Status == CrossingExecutionStatuses.DedicatedCrossingExecuted)
        {
            // 不重新写 — 把 existing path 直接当作 "写过"。
            writeResult = new FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult
            {
                AllArtifactsWritten = true,
                WrittenPaths = realPlannedPaths.ToArray()
            };
            artifactsWritten = true;
        }
        else if (realDecision.Status == CrossingExecutionStatuses.DedicatedCrossingExecuted)
        {
            writeResult = realWriter(realDecision);
            artifactsWritten = writeResult?.AllArtifactsWritten ?? false;
            if (!artifactsWritten)
            {
                matrixBlocked.Add("RealCrossingArtifactWriteFailed");
            }
        }
        else
        {
            // 即便 matrix scenarios 全通过，real 上游不就绪也要把 final-gate 拒了。
            foreach (var r in realDecision.BlockedReasons)
            {
                matrixBlocked.Add($"RealCrossingExecution:{r}");
            }
        }

        if (mainlineEvidencePresent) matrixBlocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) matrixBlocked.Add("MainlineTrustRegistryPresent");
        if (!rtPassed) matrixBlocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) matrixBlocked.Add("P15GateNotPassed");

        var distinctBlocked = matrixBlocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var gateActuallyPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && gateActuallyPassed;

        return new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport
        {
            OperationId = $"frp-dedicated-crossing-execution-{Guid.NewGuid():N}",
            CreatedAt = now,
            DedicatedCrossingExecutionGatePassed = gateActuallyPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            ExecutedCases = executedCases,
            BlockedCases = blockedCases,
            Cases = cases,
            // 真实上游 + 真实写出结果
            UpstreamDryRunGatePresent = realUpstreamPresent,
            UpstreamDryRunGatePassed = realUpstreamPassed,
            UpstreamDryRunOnly = realDryRunOnly,
            UpstreamDryRunExecutionAllowed = realDryRunExecAllowed,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            SourcePreCrossingOperationId = realDecision.SourcePreCrossingOperationId,
            SourceDryRunOperationId = realDecision.SourceDryRunOperationId,
            PlannedArtifactPaths = realDecision.PlannedArtifactPaths,
            WrittenArtifactPaths = writeResult?.WrittenPaths ?? Array.Empty<string>(),
            // 关键不变量 — V8.18 的 Crossed=true 是 OK 的，但仅在 Executed 状态下。
            Crossed = realDecision.Crossed && artifactsWritten,
            ArtifactOnly = true,
            CapabilityGrantWritten = artifactsWritten,
            ConfigPatchWritten = artifactsWritten,
            RollbackSnapshotWritten = artifactsWritten,
            AuditLogWritten = artifactsWritten,
            RevocationRecordWritten = artifactsWritten,
            // Runtime 永远不动。
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ConfigPatchAppliedToRuntime = false,
            // 不写 mainline
            EvidenceCopiedToMainline = false,
            TrustRegistryCopiedToMainline = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            // No-Manual-Review carry
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
                $"executed={executedCases}",
                $"blocked={blockedCases}",
                $"realUpstreamPresent={realUpstreamPresent}",
                $"realUpstreamPassed={realUpstreamPassed}",
                $"realDryRunOnly={realDryRunOnly}",
                $"artifactsWritten={artifactsWritten}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=RuntimeActivationDryRun (Crossed=true with artifacts is NOT runtime activation; that path needs its own dry-run and gate)"
            }
        };
    }

    private static string NormalizeForPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "missing";
        return new string(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    }

    private static IReadOnlyList<CrossingExecutionScenario> BuildScenarios(
        FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport cleanDryRun)
    {
        var cleanPreCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(
            TestCapability, TestScope, gatePassed: true);
        var noPathsExist = new CrossingExecutionPathExistence();
        var noOverrides = new CrossingExecutionOverrides();

        return
        [
            // 正例 — 所有 precondition 满足。matrix mode 里不写文件；real run 在 Status=Executed 时才写。
            new(
                "AllUpstreamClean",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecuted,
                ExpectedBlockedReason: null),
            new(
                "DryRunGateMissing",
                DryRunReport: null, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.DryRunGateMissing),
            new(
                "DryRunGateNotPassed",
                BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: false),
                cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.DryRunGateNotPassed),
            new(
                "NoCrossingDryRunReadyCase",
                BuildDryRunReportWithoutReadyCase(TestCapability, TestScope),
                cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.NoCrossingDryRunReadyCase),
            new(
                "DryRunOnlyFalse",
                BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: true, dryRunOnly: false),
                cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.DryRunOnlyFalse),
            new(
                "CrossingExecutionAllowedTrueInDryRun",
                BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: true, crossingExecutionAllowed: true),
                cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.CrossingExecutionAllowedTrueInDryRun),
            new(
                "PlannedArtifactCountNotFive",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist,
                Overrides: new CrossingExecutionOverrides { ForcePlannedArtifactCountWrong = true },
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.PlannedArtifactCountNotFive),
            new(
                "PlannedArtifactOutsideAllowedDirectory",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist,
                Overrides: new CrossingExecutionOverrides { ForcePlannedArtifactOutsideDirectory = true },
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.PlannedArtifactOutsideAllowedDirectory),
            new(
                "PlannedArtifactAlreadyExists",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: new CrossingExecutionPathExistence { CapabilityGrantPathExists = true },
                Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.PlannedArtifactAlreadyExists),
            new(
                "GlobalScope",
                BuildCleanDryRunReport(TestCapability, scope: "*", gatePassed: true),
                cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.GlobalScopeForbidden),
            new(
                "CapabilityMismatch",
                BuildCleanDryRunReport(capability: "UnauthorizedCapability", TestScope, gatePassed: true),
                cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.CapabilityMismatch),
            new(
                "RuntimeGateNotPassed",
                cleanDryRun, cleanPreCrossing,
                RtPassed: false, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.RuntimeChangeGateNotPassed),
            new(
                "P15GateNotPassed",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: false, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.P15GateNotPassed),
            new(
                "MainlineEvidencePresent",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: true, MainlineRegistryPresent: false,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.MainlineEvidencePresent),
            new(
                "MainlineTrustRegistryPresent",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: true,
                PathExistence: noPathsExist, Overrides: noOverrides,
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.MainlineTrustRegistryPresent),
            new(
                "WriteFailureSimulated",
                cleanDryRun, cleanPreCrossing,
                RtPassed: true, P15Passed: true, MainlineEvidencePresent: false, MainlineRegistryPresent: false,
                PathExistence: noPathsExist,
                Overrides: new CrossingExecutionOverrides { SimulateWriteFailure = true },
                ExpectedStatus: CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked,
                ExpectedBlockedReason: CrossingExecutionBlockedReasons.WriteFailureSimulated)
        ];
    }

    public static FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport BuildCleanDryRunReport(
        string capability, string scope, bool gatePassed,
        bool dryRunOnly = true,
        bool crossingExecutionAllowed = false) => new()
    {
        OperationId = "frp-dedicated-crossing-dry-run-fixture-for-v8-18",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        CrossingDryRunMatrixPassed = gatePassed,
        GatePassed = gatePassed,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        ReadyCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase
            {
                CaseName = "ReadySynthetic",
                ExpectedStatus = CrossingDryRunStatuses.CrossingDryRunReady,
                ActualStatus = CrossingDryRunStatuses.CrossingDryRunReady,
                BoundCapability = capability,
                BoundScope = scope,
                Contract = new CrossingExecutionContract
                {
                    PlannedCapability = capability,
                    PlannedScope = scope
                },
                PlannedArtifacts = new[] { "fixture-path" },
                DryRunOnly = dryRunOnly,
                CrossingExecutionAllowed = crossingExecutionAllowed,
                NotCrossed = !crossingExecutionAllowed,
                ApplicationNotApplied = true,
                RollbackNotActivated = true,
                StatusMatched = true,
                BlockedReasonMatched = true,
                PassedAsExpected = true
            }
        },
        UpstreamPreCrossingGatePresent = true,
        UpstreamPreCrossingGatePassed = true,
        UpstreamPreCrossingFinalGatePassed = true,
        BoundCapability = capability,
        BoundScope = scope,
        DryRunOnly = dryRunOnly,
        CrossingExecutionAllowed = crossingExecutionAllowed,
        Crossed = false,
        ApplicationApplied = false,
        RollbackActivated = false,
        NoRuntimeMutationInvariant = true
    };

    private static FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport BuildDryRunReportWithoutReadyCase(
        string capability, string scope) => new()
    {
        OperationId = "frp-dedicated-crossing-dry-run-fixture-no-ready",
        CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
        CrossingDryRunMatrixPassed = true,
        GatePassed = true,
        TotalCases = 1,
        PassedCases = 1,
        FailedCases = 0,
        ReadyCases = 0,
        BlockedCases = 1,
        Cases = new[]
        {
            new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunCase
            {
                CaseName = "BlockedOnly",
                ExpectedStatus = CrossingDryRunStatuses.CrossingDryRunBlocked,
                ActualStatus = CrossingDryRunStatuses.CrossingDryRunBlocked,
                BoundCapability = capability,
                BoundScope = scope,
                DryRunOnly = true,
                CrossingExecutionAllowed = false,
                NotCrossed = true,
                ApplicationNotApplied = true,
                RollbackNotActivated = true,
                PassedAsExpected = true
            }
        },
        BoundCapability = capability,
        BoundScope = scope,
        DryRunOnly = true,
        CrossingExecutionAllowed = false,
        Crossed = false,
        ApplicationApplied = false,
        RollbackActivated = false,
        NoRuntimeMutationInvariant = true
    };

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- DedicatedCrossingExecutionGatePassed: `{r.DedicatedCrossingExecutionGatePassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- Matrix Status — Executed: `{r.ExecutedCases}` Blocked: `{r.BlockedCases}`");
        b.AppendLine();
        b.AppendLine("## Crossing (Artifact-Only)");
        b.AppendLine($"- Crossed: `{r.Crossed}`");
        b.AppendLine($"- ArtifactOnly: `{r.ArtifactOnly}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}` (artifact only; ConfigPatchAppliedToRuntime=`{r.ConfigPatchAppliedToRuntime}`)");
        b.AppendLine($"- RollbackSnapshotWritten: `{r.RollbackSnapshotWritten}`");
        b.AppendLine($"- AuditLogWritten: `{r.AuditLogWritten}`");
        b.AppendLine($"- RevocationRecordWritten: `{r.RevocationRecordWritten}`");
        b.AppendLine();
        b.AppendLine("## Runtime (Untouched)");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("## Upstream (Real)");
        b.AppendLine($"- UpstreamDryRunGatePresent: `{r.UpstreamDryRunGatePresent}`");
        b.AppendLine($"- UpstreamDryRunGatePassed: `{r.UpstreamDryRunGatePassed}`");
        b.AppendLine($"- UpstreamDryRunOnly: `{r.UpstreamDryRunOnly}`");
        b.AppendLine($"- UpstreamDryRunExecutionAllowed: `{r.UpstreamDryRunExecutionAllowed}` (must remain false)");
        b.AppendLine($"- BoundCapability: `{r.BoundCapability}`");
        b.AppendLine($"- BoundScope: `{r.BoundScope}`");
        b.AppendLine($"- SourcePreCrossingOperationId: `{r.SourcePreCrossingOperationId}`");
        b.AppendLine($"- SourceDryRunOperationId: `{r.SourceDryRunOperationId}`");
        b.AppendLine();
        b.AppendLine("## Mainline Files");
        b.AppendLine($"- EvidenceCopiedToMainline: `{r.EvidenceCopiedToMainline}`");
        b.AppendLine($"- TrustRegistryCopiedToMainline: `{r.TrustRegistryCopiedToMainline}`");
        b.AppendLine($"- MainlineEvidencePresent: `{r.MainlineEvidencePresent}`");
        b.AppendLine($"- MainlineTrustRegistryPresent: `{r.MainlineTrustRegistryPresent}`");
        b.AppendLine();
        b.AppendLine("## Planned / Written Artifact Paths");
        foreach (var p in r.PlannedArtifactPaths)
        {
            var status = r.WrittenArtifactPaths.Contains(p, StringComparer.OrdinalIgnoreCase) ? "WRITTEN" : "planned";
            b.AppendLine($"- `{p}` [{status}]");
        }
        b.AppendLine();
        b.AppendLine("## Matrix Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedBlockedReason))
            {
                b.AppendLine($"  - expectedReason=`{c.ExpectedBlockedReason}` matched=`{c.BlockedReasonMatched}`");
            }
            b.AppendLine($"  - bound: capability=`{c.BoundCapability}` scope=`{c.BoundScope}`");
            b.AppendLine($"  - decisionCrossed=`{c.DecisionCrossed}` artifactOnly=`{c.ArtifactOnly}` runtimeActivation=`{c.RuntimeActivation}` formalRetrievalAllowed=`{c.FormalRetrievalAllowed}`");
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

        b.AppendLine("V8.18 dedicated crossing execution gate matrix。16 scenarios 验证 policy + 真实执行：当 real upstream 干净时写出 5 个 artifact 到 vector/v8/dedicated-crossing/。Crossed=true（artifact-only），但 RuntimeActivation 仍 false、FormalRetrievalAllowed 仍 false、ConfigPatch 仅作为 artifact 不接入 runtime。下一阶段 RuntimeActivationDryRun 才考虑把 config patch 真正应用到 runtime（仍需独立 gate + 显式 sign-off）。");
        return b.ToString();
    }
}

public sealed record CrossingExecutionScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport? DryRunReport,
    FormalRetrievalPromotionApprovalPreCrossingFinalGateReport? PreCrossingReport,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    CrossingExecutionPathExistence PathExistence,
    CrossingExecutionOverrides Overrides,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public IReadOnlyList<string> PlannedArtifactPaths { get; init; } = Array.Empty<string>();
    public bool DecisionCrossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }

    /// <summary>所有 case：runtime 未激活。</summary>
    public bool RuntimeNotActivated { get; init; }

    /// <summary>所有 case：formal retrieval 未授权。</summary>
    public bool FormalRetrievalNotAllowed { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DedicatedCrossingExecutionGatePassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ExecutedCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateCase>();

    // 真实上游 + 写出结果
    public bool UpstreamDryRunGatePresent { get; init; }
    public bool UpstreamDryRunGatePassed { get; init; }
    public bool UpstreamDryRunOnly { get; init; }
    public bool UpstreamDryRunExecutionAllowed { get; init; }
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string SourcePreCrossingOperationId { get; init; } = string.Empty;
    public string SourceDryRunOperationId { get; init; } = string.Empty;
    public IReadOnlyList<string> PlannedArtifactPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WrittenArtifactPaths { get; init; } = Array.Empty<string>();

    // Crossing (Artifact-Only) — V8.18 才允许这些为 true
    public bool Crossed { get; init; }
    public bool ArtifactOnly { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RollbackSnapshotWritten { get; init; }
    public bool AuditLogWritten { get; init; }
    public bool RevocationRecordWritten { get; init; }

    // Runtime — 永远不动
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }

    // Mainline — 不写
    public bool EvidenceCopiedToMainline { get; init; }
    public bool TrustRegistryCopiedToMainline { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }

    // No-Manual-Review carry
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

public sealed class FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
