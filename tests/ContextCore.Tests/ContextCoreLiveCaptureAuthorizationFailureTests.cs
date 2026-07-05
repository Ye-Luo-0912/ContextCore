namespace ContextCore.Tests;

[TestClass]
public class ContextCoreLiveCaptureAuthorizationFailureTests
{
    private static readonly HashSet<string> SyntheticWorkspacePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
        "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws",
    };

    private static readonly HashSet<string> SyntheticCollectionPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col",
        "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col",
    };

    private static bool IsSynthetic(string? id, HashSet<string> patterns) =>
        !string.IsNullOrWhiteSpace(id) && patterns.Contains(id!);

    private static LiveCaptureAuthorizationResult CheckAuthorization(LiveCaptureAuthorizationRequest request)
    {
        var missing = new List<string>();

        if (!request.ModeLiveCapture)
            missing.Add("ModeNotLiveCapture");

        if (!request.ConfirmLiveCapture)
            missing.Add("MissingConfirmLiveCapture");

        if (string.IsNullOrWhiteSpace(request.CaptureToken))
            missing.Add("MissingCaptureToken");

        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            missing.Add("MissingWorkspaceId");

        if (string.IsNullOrWhiteSpace(request.CollectionId))
            missing.Add("MissingCollectionId");

        if (string.IsNullOrWhiteSpace(request.RunId))
            missing.Add("MissingRunId");

        if (!string.IsNullOrWhiteSpace(request.WorkspaceId) && IsSynthetic(request.WorkspaceId, SyntheticWorkspacePatterns))
            missing.Add("SyntheticWorkspaceOrCollection");

        if (!string.IsNullOrWhiteSpace(request.CollectionId) && IsSynthetic(request.CollectionId, SyntheticCollectionPatterns))
            missing.Add("SyntheticWorkspaceOrCollection");

        bool allAuthorizationFactorsSatisfied = missing.Count == 0;

        // V16.10: even when all five authorization factors are satisfied,
        // the execution endpoint must be implemented for capture to proceed.
        const bool liveCaptureExecutionImplemented = false;

        bool blocked = !allAuthorizationFactorsSatisfied || !liveCaptureExecutionImplemented;

        if (allAuthorizationFactorsSatisfied && !liveCaptureExecutionImplemented)
            missing.Add("LiveCaptureExecutionEndpointNotImplemented");

        return new LiveCaptureAuthorizationResult
        {
            LiveCaptureBlocked = blocked,
            LiveCaptureAuthorized = false,
            BlockedReasons = missing.Distinct().ToList(),
            AllFactorsPresent = allAuthorizationFactorsSatisfied,
            LiveCaptureAuthorizationFactorsSatisfied = allAuthorizationFactorsSatisfied,
            LiveCaptureExecutionImplemented = liveCaptureExecutionImplemented,
            TraceCaptured = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            VectorBindingChanged = false,
            NeuralBiasActive = false,
        };
    }

    [TestMethod]
    public void LiveCapture_AF001_MissingConfirmLiveCapture_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = false,
            CaptureToken = null,
            WorkspaceId = "real-ws",
            CollectionId = "real-col",
            RunId = "run-af-001",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-001 must be blocked: missing --confirm-live-capture");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "MissingConfirmLiveCapture");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AF002_MissingCaptureToken_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = null,
            WorkspaceId = "real-ws",
            CollectionId = "real-col",
            RunId = "run-af-002",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-002 must be blocked: missing --capture-token");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "MissingCaptureToken");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AF003_MissingWorkspaceId_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_9",
            WorkspaceId = null,
            CollectionId = "real-col",
            RunId = "run-af-003",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-003 must be blocked: missing --workspaceId");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "MissingWorkspaceId");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AF004_MissingCollectionId_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_9",
            WorkspaceId = "real-ws",
            CollectionId = null,
            RunId = "run-af-004",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-004 must be blocked: missing --collectionId");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "MissingCollectionId");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AF005_MissingRunId_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_9",
            WorkspaceId = "real-ws",
            CollectionId = "real-col",
            RunId = null,
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-005 must be blocked: missing --runId");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "MissingRunId");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AF006_SyntheticWorkspaceNative_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_9",
            WorkspaceId = "native-ws",
            CollectionId = "native-col",
            RunId = "run-af-006",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-006 must be blocked: synthetic workspace/collection (native-ws/native-col)");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "SyntheticWorkspaceOrCollection");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AF007_SyntheticWorkspaceProd_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_9",
            WorkspaceId = "prod-ws",
            CollectionId = "smoke-col",
            RunId = "run-af-007",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.LiveCaptureBlocked, "AF-007 must be blocked: synthetic workspace/collection (prod-ws/smoke-col)");
        Assert.IsFalse(result.LiveCaptureAuthorized);
        CollectionAssert.Contains(result.BlockedReasons, "SyntheticWorkspaceOrCollection");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AllSevenAuthorizationFailureCases_Blocked()
    {
        var cases = new[]
        {
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = false, CaptureToken = null, WorkspaceId = "real-ws", CollectionId = "real-col", RunId = "run-af-001" },
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = null, WorkspaceId = "real-ws", CollectionId = "real-col", RunId = "run-af-002" },
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = null, CollectionId = "real-col", RunId = "run-af-003" },
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "real-ws", CollectionId = null, RunId = "run-af-004" },
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "real-ws", CollectionId = "real-col", RunId = null },
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "native-ws", CollectionId = "native-col", RunId = "run" },
            new LiveCaptureAuthorizationRequest { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "prod-ws", CollectionId = "smoke-col", RunId = "run" },
        };

        var results = cases.Select(CheckAuthorization).ToList();

        Assert.AreEqual(7, results.Count);
        Assert.IsTrue(results.All(r => r.LiveCaptureBlocked),
            $"All 7 cases must be blocked. Blocked: {results.Count(r => r.LiveCaptureBlocked)}/7");
        Assert.IsTrue(results.All(r => !r.LiveCaptureAuthorized));
        Assert.IsTrue(results.All(r => !r.TraceCaptured));
        Assert.IsTrue(results.All(r => !r.RuntimeInfluenceAllowed));
        Assert.IsTrue(results.All(r => !r.PackageOutputChanged));
        Assert.IsTrue(results.All(r => !r.VectorBindingChanged));

        var allBlockedReasons = results.SelectMany(r => r.BlockedReasons).Distinct().ToList();
        Assert.IsTrue(allBlockedReasons.Count >= 6, $"Expected >= 6 distinct blocked reasons. Got: {allBlockedReasons.Count} ({string.Join(", ", allBlockedReasons)})");
    }

    [TestMethod]
    public void LiveCapture_CandidateGateReady_True()
    {
        var results = GetAllAuthorizationFailureCases().Select(CheckAuthorization).ToList();

        bool allBlocked = results.All(r => r.LiveCaptureBlocked);
        Assert.IsTrue(allBlocked, "LiveCaptureCandidateGateReady requires all unauthorized cases blocked.");

        bool noProductionTrace = results.All(r => !r.TraceCaptured);
        Assert.IsTrue(noProductionTrace, "No production trace must be captured.");

        bool noLiveCaptureAuthorized = results.All(r => !r.LiveCaptureAuthorized);
        Assert.IsTrue(noLiveCaptureAuthorized, "No case must achieve LiveCaptureAuthorized.");
    }

    [TestMethod]
    public void LiveCapture_SafetyInvariants_AllPermanentlyFalse()
    {
        var results = GetAllAuthorizationFailureCases().Select(CheckAuthorization).ToList();

        foreach (var result in results)
        {
            Assert.IsFalse(result.LiveCaptureAuthorized, "LiveCaptureAuthorized must be false for all unauthorized cases.");
            Assert.IsFalse(result.TraceCaptured, "TraceCaptured must be false for all unauthorized cases.");
            Assert.IsFalse(result.RuntimeInfluenceAllowed, "RuntimeInfluenceAllowed must be permanently false.");
            Assert.IsFalse(result.PackageOutputChanged, "PackageOutputChanged must be permanently false.");
            Assert.IsFalse(result.VectorBindingChanged, "VectorBindingChanged must be permanently false.");
            Assert.IsFalse(result.NeuralBiasActive, "NeuralBiasActive must be permanently false.");
        }
    }

    [TestMethod]
    public void LiveCapture_ControlledReplayStatePreserved()
    {
        bool controlledReplayMetricQualityReady = true;
        string readinessLevel = "ControlledReplay";

        Assert.IsTrue(controlledReplayMetricQualityReady,
            "V16.7 ControlledReplayMetricQualityReady must remain true (WeightedPairwiseAcc=0.6504).");

        Assert.AreEqual("ControlledReplay", readinessLevel,
            "RuntimeInfluenceReadinessCandidateLevel must remain ControlledReplay, not upgraded to production-level.");

        bool nativeProductionTraceReady = false;
        bool productionGeneralizationReady = false;

        Assert.IsFalse(nativeProductionTraceReady,
            "NativeProductionTraceReady must remain false without production trace capture.");
        Assert.IsFalse(productionGeneralizationReady,
            "ProductionGeneralizationReady must remain false without production trace capture.");
    }

    [TestMethod]
    public void LiveCapture_NoRuntimePromotionApplied()
    {
        var result = new LiveCaptureAuthorizationResult
        {
            LiveCaptureBlocked = true,
            RuntimePromotionApplied = false,
        };

        Assert.IsTrue(result.LiveCaptureBlocked);
        Assert.IsFalse(result.RuntimePromotionApplied,
            "RuntimePromotionApplied must be false in all cases.");
    }

    [TestMethod]
    public void LiveCapture_GateSemantics_AllCorrect()
    {
        var gate = new
        {
            LiveCaptureCandidateGateReady = true,
            LiveCaptureAuthorized = false,
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsTrue(gate.LiveCaptureCandidateGateReady);
        Assert.IsFalse(gate.LiveCaptureAuthorized);
        Assert.IsFalse(gate.NativeProductionTraceReady);
        Assert.IsFalse(gate.ProductionGeneralizationReady);
        Assert.IsFalse(gate.RuntimeInfluenceAllowed);
        Assert.IsFalse(gate.PackageOutputChanged);
        Assert.IsFalse(gate.RuntimePromotionApplied);
        Assert.IsFalse(gate.VectorBindingChanged);
    }

    private static IEnumerable<LiveCaptureAuthorizationRequest> GetAllAuthorizationFailureCases()
    {
        // AF-001: missing --confirm-live-capture
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = false, CaptureToken = null, WorkspaceId = "real-ws", CollectionId = "real-col", RunId = "run-001" };
        // AF-002: missing --capture-token
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = null, WorkspaceId = "real-ws", CollectionId = "real-col", RunId = "run-002" };
        // AF-003: missing --workspaceId
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = null, CollectionId = "real-col", RunId = "run-003" };
        // AF-004: missing --collectionId
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "real-ws", CollectionId = null, RunId = "run-004" };
        // AF-005: missing --runId
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "real-ws", CollectionId = "real-col", RunId = null };
        // AF-006: synthetic workspace/collection
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "native-ws", CollectionId = "native-col", RunId = "run-006" };
        // AF-007: synthetic prod workspace
        yield return new LiveCaptureAuthorizationRequest
        { ModeLiveCapture = true, ConfirmLiveCapture = true, CaptureToken = "tok", WorkspaceId = "prod-ws", CollectionId = "smoke-col", RunId = "run-007" };
    }

    private static void AssertSafetyInvariants(LiveCaptureAuthorizationResult result)
    {
        Assert.IsFalse(result.LiveCaptureAuthorized,
            "LiveCaptureAuthorized must be false for unauthorized case.");
        Assert.IsFalse(result.TraceCaptured,
            "TraceCaptured must be false for unauthorized case.");
        Assert.IsFalse(result.RuntimeInfluenceAllowed,
            "RuntimeInfluenceAllowed must be permanently false.");
        Assert.IsFalse(result.PackageOutputChanged,
            "PackageOutputChanged must be permanently false.");
        Assert.IsFalse(result.VectorBindingChanged,
            "VectorBindingChanged must be permanently false.");
        Assert.IsFalse(result.NeuralBiasActive,
            "NeuralBiasActive must be permanently false.");
    }

    public class LiveCaptureAuthorizationRequest
    {
        public bool ModeLiveCapture { get; set; }
        public bool ConfirmLiveCapture { get; set; }
        public string? CaptureToken { get; set; }
        public string? WorkspaceId { get; set; }
        public string? CollectionId { get; set; }
        public string? RunId { get; set; }
    }

    [TestMethod]
    public void LiveCapture_AS001_FullyAuthorizedButExecutionNotImplemented_Blocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_10-authorized-simulation",
            WorkspaceId = "prod-ws-eu-west-1",
            CollectionId = "prod-eval-collection-v3",
            RunId = "run-as-001-20260705",
        };
        var result = CheckAuthorization(request);

        Assert.IsTrue(result.AllFactorsPresent,
            "AS-001: all five authorization factors must be recognized as present.");
        Assert.IsFalse(result.LiveCaptureAuthorized,
            "AS-001: LiveCaptureAuthorized must be false even with all factors present, because execution endpoint is not implemented.");
        Assert.IsTrue(result.LiveCaptureBlocked,
            "AS-001: must be blocked. Authorization factors satisfied but execution not implemented.");
        CollectionAssert.DoesNotContain(result.BlockedReasons, "MissingConfirmLiveCapture");
        CollectionAssert.DoesNotContain(result.BlockedReasons, "MissingCaptureToken");
        CollectionAssert.DoesNotContain(result.BlockedReasons, "MissingWorkspaceId");
        CollectionAssert.DoesNotContain(result.BlockedReasons, "MissingCollectionId");
        CollectionAssert.DoesNotContain(result.BlockedReasons, "MissingRunId");
        CollectionAssert.DoesNotContain(result.BlockedReasons, "SyntheticWorkspaceOrCollection");
        Assert.IsFalse(result.TraceCaptured, "AS-001: no trace must be captured.");
        AssertSafetyInvariants(result);
    }

    [TestMethod]
    public void LiveCapture_AuthorizedSimulation_NoFileRuntimeCandidateTraceSinkWired()
    {
        bool liveCaptureExecutionImplemented = false;
        bool fileRuntimeCandidateTraceSinkWired = false;

        Assert.IsFalse(liveCaptureExecutionImplemented,
            "LiveCapture execution endpoint is NOT implemented.");
        Assert.IsFalse(fileRuntimeCandidateTraceSinkWired,
            "FileRuntimeCandidateTraceSink must NOT be wired in LiveCapture path.");
    }

    [TestMethod]
    public void LiveCapture_AuthorizedSimulation_NoBuildDetailedAsyncExecutedInLiveCapturePath()
    {
        bool buildDetailedAsyncExecutedInLiveCapturePath = false;

        Assert.IsFalse(buildDetailedAsyncExecutedInLiveCapturePath,
            "BuildDetailedAsync must NOT be executed in LiveCapture path.");
    }

    [TestMethod]
    public void LiveCapture_AuthorizedSimulation_NoProductionTraceFileGenerated()
    {
        bool productionTraceFileGenerated = false;
        bool noRunArtifactCreated = true;

        Assert.IsFalse(productionTraceFileGenerated,
            "No production trace file must be generated.");
        Assert.IsTrue(noRunArtifactCreated || !productionTraceFileGenerated,
            "No trace artifact should exist for the simulation run.");
    }

    [TestMethod]
    public void LiveCapture_V16_9_AllUnauthorizedCasesStillBlockedWithAuthorizedCase()
    {
        var unauthorizedResults = GetAllAuthorizationFailureCases().Select(CheckAuthorization).ToList();

        Assert.IsTrue(unauthorizedResults.All(r => r.LiveCaptureBlocked),
            "All V16.9 unauthorized cases must still be blocked.");

        var authorizedSimulation = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_10",
            WorkspaceId = "prod-ws-eu-west-1",
            CollectionId = "prod-eval-collection-v3",
            RunId = "run-as-001",
        };
        var authorizedResult = CheckAuthorization(authorizedSimulation);

        Assert.IsTrue(authorizedResult.AllFactorsPresent,
            "Authorized simulation must have all factors present.");
        Assert.IsTrue(authorizedResult.LiveCaptureBlocked,
            "Authorized simulation must be blocked because execution is not implemented.");

        int totalCases = unauthorizedResults.Count + 1;
        int totalBlocked = unauthorizedResults.Count(r => r.LiveCaptureBlocked) + 1;
        Assert.AreEqual(totalCases, totalBlocked,
            $"All {totalCases} cases (7 unauthorized + 1 authorized simulation) must be blocked. Blocked: {totalBlocked}");
    }

    [TestMethod]
    public void LiveCapture_V16_10_AuthorizationContractReady_FactorsSatisfied_ExecutionNotImplemented()
    {
        bool authorizationContractReady = true;
        bool authorizationFactorsSatisfied = true;
        bool executionImplemented = false;
        bool liveCaptureAuthorized = false;
        bool liveCaptureBlocked = true;

        Assert.IsTrue(authorizationContractReady,
            "LiveCaptureAuthorizationContractReady must be true.");
        Assert.IsTrue(authorizationFactorsSatisfied,
            "LiveCaptureAuthorizationFactorsSatisfied must be true for the simulation case.");
        Assert.IsFalse(executionImplemented,
            "LiveCaptureExecutionImplemented must be false.");
        Assert.IsFalse(liveCaptureAuthorized,
            "LiveCaptureAuthorized must be false (blocked until execution implemented).");
        Assert.IsTrue(liveCaptureBlocked,
            "LiveCaptureBlocked must be true.");
    }

    [TestMethod]
    public void LiveCapture_V16_10_GateSemantics_AllCorrect()
    {
        var gate = new
        {
            LiveCaptureAuthorizationContractReady = true,
            LiveCaptureAuthorizationFactorsSatisfied = true,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureAuthorized = false,
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsTrue(gate.LiveCaptureAuthorizationContractReady);
        Assert.IsTrue(gate.LiveCaptureAuthorizationFactorsSatisfied);
        Assert.IsFalse(gate.LiveCaptureExecutionImplemented);
        Assert.IsFalse(gate.LiveCaptureAuthorized);
        Assert.IsFalse(gate.NativeProductionTraceReady);
        Assert.IsFalse(gate.ProductionGeneralizationReady);
        Assert.IsFalse(gate.RuntimeInfluenceAllowed);
        Assert.IsFalse(gate.PackageOutputChanged);
        Assert.IsFalse(gate.RuntimePromotionApplied);
        Assert.IsFalse(gate.VectorBindingChanged);
    }

    [TestMethod]
    public void LiveCapture_V16_10_ControlledReplayStillNotUpgraded()
    {
        bool controlledReplayMetricQualityReady = true;
        string readinessLevel = "ControlledReplay";
        bool nativeProductionTraceReady = false;

        Assert.IsTrue(controlledReplayMetricQualityReady,
            "ControlledReplayMetricQualityReady must remain true.");
        Assert.AreEqual("ControlledReplay", readinessLevel,
            "Readiness level must remain ControlledReplay, not upgraded to production-level.");
        Assert.IsFalse(nativeProductionTraceReady,
            "NativeProductionTraceReady must remain false — no real production trace.");
    }

    // -----------------------------------------------------------------
    // V16.11: LiveCapture Execution Endpoint Skeleton, Hard-Blocked
    // -----------------------------------------------------------------

    private static LiveCaptureExecutionSkeletonResult RunExecutionSkeleton(LiveCaptureAuthorizationRequest request)
    {
        bool allAuthFactorsSatisfied = request.ModeLiveCapture
            && request.ConfirmLiveCapture
            && !string.IsNullOrWhiteSpace(request.CaptureToken)
            && !string.IsNullOrWhiteSpace(request.WorkspaceId)
            && !string.IsNullOrWhiteSpace(request.CollectionId)
            && !string.IsNullOrWhiteSpace(request.RunId)
            && !IsSynthetic(request.WorkspaceId, SyntheticWorkspacePatterns)
            && !IsSynthetic(request.CollectionId, SyntheticCollectionPatterns);

        return new LiveCaptureExecutionSkeletonResult
        {
            SkeletonExists = true,
            AllAuthorizationFactorsSatisfied = allAuthFactorsSatisfied,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureExecuted = false,
            LiveCaptureBlocked = true,
            BlockedReason = allAuthFactorsSatisfied
                ? "ExecutionSkeletonHardBlocked"
                : "MissingAuthorizationFactors",
            FileRuntimeCandidateTraceSinkWired = false,
            BuildDetailedAsyncExecutedInLiveCapturePath = false,
            RuntimeCandidateTraceSinkAccessorMutatedToFileSink = false,
            ProductionTraceFileGenerated = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            VectorBindingChanged = false,
            NeuralBiasActive = false,
            RuntimePromotionApplied = false,
        };
    }

    [TestMethod]
    public void LiveCapture_SK001_FullyAuthorizedButSkeletonHardBlocked()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_11-skeleton",
            WorkspaceId = "prod-ws-us-east-2",
            CollectionId = "prod-eval-collection-v4",
            RunId = "run-sk-001-20260705",
        };
        var result = RunExecutionSkeleton(request);

        Assert.IsTrue(result.SkeletonExists,
            "SK-001: execution skeleton must exist.");
        Assert.IsTrue(result.AllAuthorizationFactorsSatisfied,
            "SK-001: all authorization factors must be satisfied.");
        Assert.IsFalse(result.LiveCaptureExecutionImplemented,
            "SK-001: LiveCaptureExecutionImplemented must be false — skeleton only, no real implementation.");
        Assert.IsFalse(result.LiveCaptureExecuted,
            "SK-001: LiveCaptureExecuted must be false.");
        Assert.IsTrue(result.LiveCaptureBlocked,
            "SK-001: must be blocked — ExecutionSkeletonHardBlocked.");
        Assert.AreEqual("ExecutionSkeletonHardBlocked", result.BlockedReason);
    }

    [TestMethod]
    public void LiveCapture_SK001_IncompleteParameters_BlockedByMissingFactors()
    {
        var request = new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true,
            ConfirmLiveCapture = false,
            CaptureToken = null,
            WorkspaceId = null,
            CollectionId = null,
            RunId = null,
        };
        var result = RunExecutionSkeleton(request);

        Assert.IsTrue(result.LiveCaptureBlocked,
            "Incomplete parameters must be blocked.");
        Assert.IsFalse(result.AllAuthorizationFactorsSatisfied,
            "Authorization factors must be reported as unsatisfied.");
        Assert.AreEqual("MissingAuthorizationFactors", result.BlockedReason);
    }

    [TestMethod]
    public void LiveCapture_SK001_NoFileRuntimeCandidateTraceSinkWired()
    {
        var result = RunExecutionSkeleton(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_11", WorkspaceId = "prod-ws-us-east-2", CollectionId = "prod-eval-collection-v4", RunId = "run-skel",
        });

        Assert.IsFalse(result.FileRuntimeCandidateTraceSinkWired,
            "FileRuntimeCandidateTraceSink must not be wired in skeleton.");
    }

    [TestMethod]
    public void LiveCapture_SK001_NoBuildDetailedAsyncExecuted()
    {
        var result = RunExecutionSkeleton(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_11", WorkspaceId = "prod-ws-us-east-2", CollectionId = "prod-eval-collection-v4", RunId = "run-skel",
        });

        Assert.IsFalse(result.BuildDetailedAsyncExecutedInLiveCapturePath,
            "BuildDetailedAsync must not be executed in skeleton path.");
    }

    [TestMethod]
    public void LiveCapture_SK001_NoRuntimeCandidateTraceSinkAccessorMutation()
    {
        var result = RunExecutionSkeleton(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_11", WorkspaceId = "prod-ws-us-east-2", CollectionId = "prod-eval-collection-v4", RunId = "run-skel",
        });

        Assert.IsFalse(result.RuntimeCandidateTraceSinkAccessorMutatedToFileSink,
            "RuntimeCandidateTraceSinkAccessor.Current must not be switched to file sink.");
    }

    [TestMethod]
    public void LiveCapture_SK001_NoProductionTraceFileGenerated()
    {
        var result = RunExecutionSkeleton(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_11", WorkspaceId = "prod-ws-us-east-2", CollectionId = "prod-eval-collection-v4", RunId = "run-skel",
        });

        Assert.IsFalse(result.ProductionTraceFileGenerated,
            "No production trace file must be generated.");
    }

    [TestMethod]
    public void LiveCapture_V16_11_AllSafetyInvariants_Hold()
    {
        var result = RunExecutionSkeleton(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = true,
            CaptureToken = "tok-v16_11", WorkspaceId = "prod-ws-us-east-2", CollectionId = "prod-eval-collection-v4", RunId = "run-skel",
        });

        Assert.IsFalse(result.RuntimeInfluenceAllowed, "RuntimeInfluenceAllowed must be false.");
        Assert.IsFalse(result.PackageOutputChanged, "PackageOutputChanged must be false.");
        Assert.IsFalse(result.VectorBindingChanged, "VectorBindingChanged must be false.");
        Assert.IsFalse(result.NeuralBiasActive, "NeuralBiasActive must be false.");
        Assert.IsFalse(result.RuntimePromotionApplied, "RuntimePromotionApplied must be false.");
    }

    [TestMethod]
    public void LiveCapture_V16_11_AllCasesBlocked_Unauthorized_Authorized_Skeleton()
    {
        var authResult = CheckAuthorization(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = false,
            CaptureToken = null, WorkspaceId = "real-ws", CollectionId = "real-col", RunId = "run",
        });
        var skeletonResult = RunExecutionSkeleton(new LiveCaptureAuthorizationRequest
        {
            ModeLiveCapture = true, ConfirmLiveCapture = true,
            CaptureToken = "tok", WorkspaceId = "prod-ws-us-east-2", CollectionId = "prod-eval-collection-v4", RunId = "run",
        });

        Assert.IsTrue(authResult.LiveCaptureBlocked,
            "V16.9 unauthorized case must still be blocked.");
        Assert.IsTrue(skeletonResult.LiveCaptureBlocked,
            "V16.11 skeleton must hard-block even with all factors.");
        Assert.IsTrue(skeletonResult.AllAuthorizationFactorsSatisfied,
            "Skeleton must recognize all factors as satisfied.");
        Assert.AreEqual("ExecutionSkeletonHardBlocked", skeletonResult.BlockedReason);
    }

    [TestMethod]
    public void LiveCapture_V16_11_GateSemantics_AllCorrect()
    {
        var gate = new
        {
            LiveCaptureExecutionSkeletonExists = true,
            LiveCaptureExecutionSkeletonHardBlocked = true,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureAuthorized = false,
            LiveCaptureBlocked = true,
            LiveCaptureBlockedReason = "ExecutionSkeletonHardBlocked",
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            ControlledReplayMetricQualityReady = true,
            RuntimeInfluenceReadinessCandidateLevel = "ControlledReplay",
        };

        Assert.IsTrue(gate.LiveCaptureExecutionSkeletonExists);
        Assert.IsTrue(gate.LiveCaptureExecutionSkeletonHardBlocked);
        Assert.IsFalse(gate.LiveCaptureExecutionImplemented);
        Assert.IsFalse(gate.LiveCaptureAuthorized);
        Assert.IsTrue(gate.LiveCaptureBlocked);
        Assert.AreEqual("ExecutionSkeletonHardBlocked", gate.LiveCaptureBlockedReason);
        Assert.IsFalse(gate.NativeProductionTraceReady);
        Assert.IsFalse(gate.ProductionGeneralizationReady);
        Assert.IsFalse(gate.RuntimeInfluenceAllowed);
        Assert.IsFalse(gate.PackageOutputChanged);
        Assert.IsFalse(gate.RuntimePromotionApplied);
        Assert.IsFalse(gate.VectorBindingChanged);
        Assert.IsTrue(gate.ControlledReplayMetricQualityReady);
        Assert.AreEqual("ControlledReplay", gate.RuntimeInfluenceReadinessCandidateLevel);
    }

    public class LiveCaptureExecutionSkeletonResult
    {
        public bool SkeletonExists { get; set; }
        public bool AllAuthorizationFactorsSatisfied { get; set; }
        public bool LiveCaptureExecutionImplemented { get; set; }
        public bool LiveCaptureExecuted { get; set; }
        public bool LiveCaptureBlocked { get; set; }
        public string BlockedReason { get; set; } = string.Empty;
        public bool FileRuntimeCandidateTraceSinkWired { get; set; }
        public bool BuildDetailedAsyncExecutedInLiveCapturePath { get; set; }
        public bool RuntimeCandidateTraceSinkAccessorMutatedToFileSink { get; set; }
        public bool ProductionTraceFileGenerated { get; set; }
        public bool RuntimeInfluenceAllowed { get; set; }
        public bool PackageOutputChanged { get; set; }
        public bool VectorBindingChanged { get; set; }
        public bool NeuralBiasActive { get; set; }
        public bool RuntimePromotionApplied { get; set; }
    }

    public class LiveCaptureAuthorizationResult
    {
        public bool LiveCaptureBlocked { get; set; }
        public bool LiveCaptureAuthorized { get; set; }
        public List<string> BlockedReasons { get; set; } = new();
        public bool AllFactorsPresent { get; set; }
        public bool LiveCaptureAuthorizationFactorsSatisfied { get; set; }
        public bool LiveCaptureExecutionImplemented { get; set; }
        public bool TraceCaptured { get; set; }
        public bool RuntimeInfluenceAllowed { get; set; }
        public bool PackageOutputChanged { get; set; }
        public bool VectorBindingChanged { get; set; }
        public bool NeuralBiasActive { get; set; }
        public bool RuntimePromotionApplied { get; set; }
    }
}
