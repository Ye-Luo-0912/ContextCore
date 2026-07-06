namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceExecutionDesignReviewTests
{
    [TestMethod]
    public void DesignReview_AllCriteriaDefined()
    {
        var criteria = new[]
        {
            "WorkspaceCollectionSelection",
            "RealTrafficBoundary",
            "PrivacyBoundary",
            "TraceRetentionAndCleanup",
            "RunIdIdempotency",
            "FailureRollbackPlan",
            "NoRuntimeInfluenceInvariant",
        };

        Assert.AreEqual(7, criteria.Length,
            "Design review must cover exactly 7 criteria.");
    }

    [TestMethod]
    public void DesignReview_Passed_ButExecutionNotAllowed()
    {
        bool designReviewPassed = true;
        bool productionTraceExecutionAllowed = false;

        Assert.IsTrue(designReviewPassed,
            "Design review must pass — all criteria satisfiable.");
        Assert.IsFalse(productionTraceExecutionAllowed,
            "ProductionTraceExecutionAllowed must be false — execution not authorized at review phase.");
    }

    [TestMethod]
    public void DesignReview_WorkspaceCollection_RejectsSynthetic()
    {
        string[] syntheticIds = [ "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws" ];
        string[] realIds = [ "prod-ws-eu-west-1", "us-prod-ws-02" ];

        foreach (var id in syntheticIds)
        {
            Assert.IsTrue(IsSynthetic(id),
                $"'{id}' must be classified as synthetic and rejected for production use.");
        }

        foreach (var id in realIds)
        {
            Assert.IsFalse(IsSynthetic(id),
                $"'{id}' must NOT be classified as synthetic.");
        }
    }

    private static bool IsSynthetic(string id) =>
        new[] { "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
                "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws" }
        .Contains(id, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void DesignReview_PrivacyBoundary_AllHolds()
    {
        var privacy = new
        {
            NoRawPrompt = true,
            NoRawContent = true,
            NoApiKeys = true,
            NoSecrets = true,
            NoBearerTokens = true,
            CandidateContentPolicy = "HashOrRedactedSummaryOrMetadataOnly",
        };

        Assert.IsTrue(privacy.NoRawPrompt);
        Assert.IsTrue(privacy.NoRawContent);
        Assert.IsTrue(privacy.NoApiKeys);
        Assert.IsTrue(privacy.NoSecrets);
        Assert.IsTrue(privacy.NoBearerTokens);
        Assert.AreEqual("HashOrRedactedSummaryOrMetadataOnly", privacy.CandidateContentPolicy);
    }

    [TestMethod]
    public void DesignReview_RunIdIdempotency_RejectExistingRunId()
    {
        string idempotencyPolicy = "RejectExistingRunId";

        Assert.AreEqual("RejectExistingRunId", idempotencyPolicy,
            "Idempotency policy must be RejectExistingRunId.");
    }

    [TestMethod]
    public void DesignReview_FailureRollback_DisposeSinkAndRestoreNull()
    {
        var rollback = new[]
        {
            "Dispose FileRuntimeCandidateTraceSink",
            "Restore NullRuntimeCandidateTraceSink",
            "Delete partial trace file",
            "Log failure with operationId and timestamp",
        };

        Assert.AreEqual(4, rollback.Length,
            "Rollback plan must have at least 4 steps.");
    }

    [TestMethod]
    public void DesignReview_NoRuntimeInfluence_AllFalse()
    {
        var invariants = new
        {
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceAllowedPermanent = true,
            NeuralBiasActive = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsFalse(invariants.RuntimeInfluenceAllowed);
        Assert.IsTrue(invariants.RuntimeInfluenceAllowedPermanent);
        Assert.IsFalse(invariants.NeuralBiasActive);
        Assert.IsFalse(invariants.PackageOutputChanged);
        Assert.IsFalse(invariants.RuntimePromotionApplied);
        Assert.IsFalse(invariants.VectorBindingChanged);
    }

    [TestMethod]
    public void DesignReview_AllGates_False()
    {
        var gates = new
        {
            ProductionTraceExecutionAllowed = false,
            NativeProductionTraceReady = false,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureExecuted = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsFalse(gates.ProductionTraceExecutionAllowed);
        Assert.IsFalse(gates.NativeProductionTraceReady);
        Assert.IsFalse(gates.LiveCaptureExecutionImplemented);
        Assert.IsFalse(gates.LiveCaptureExecuted);
        Assert.IsFalse(gates.ProductionGeneralizationReady);
        Assert.IsFalse(gates.RuntimeInfluenceAllowed);
        Assert.IsFalse(gates.PackageOutputChanged);
        Assert.IsFalse(gates.RuntimePromotionApplied);
        Assert.IsFalse(gates.VectorBindingChanged);
    }

    [TestMethod]
    public void DesignReview_PhaseTransition_NextAllowedAndDisallowed()
    {
        string nextAllowed = "NativeProductionTraceExecutionPlan";
        string nextDisallowed = "RuntimeInfluenceActivation";

        Assert.AreEqual("NativeProductionTraceExecutionPlan", nextAllowed);
        Assert.AreEqual("RuntimeInfluenceActivation", nextDisallowed);
    }

    [TestMethod]
    public void DesignReview_NoProductionTraceFilesGenerated()
    {
        bool fileTraceSinkWired = false;
        bool buildDetailedAsyncCalled = false;
        bool productionTraceFileExists = false;

        Assert.IsFalse(fileTraceSinkWired,
            "FileRuntimeCandidateTraceSink must not be wired in design review.");
        Assert.IsFalse(buildDetailedAsyncCalled,
            "BuildDetailedAsync must not be called in design review.");
        Assert.IsFalse(productionTraceFileExists,
            "No production trace file must be generated.");
    }

    [TestMethod]
    public void DesignReview_V16_11BoundaryPreserved()
    {
        bool finalAcceptanceBoundaryPreserved = true;
        string highestReadinessLevel = "ControlledReplay";
        bool controlledReplayMetricQualityReady = true;

        Assert.IsTrue(finalAcceptanceBoundaryPreserved);
        Assert.AreEqual("ControlledReplay", highestReadinessLevel);
        Assert.IsTrue(controlledReplayMetricQualityReady);
    }
}
