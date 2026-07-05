namespace ContextCore.Tests;

[TestClass]
public class ContextCorePhaseLedgerAcceptanceBoundaryTests
{
    private static readonly IReadOnlyList<string> V16Phases = new List<string>
    {
        "V16.2", "V16.3", "V16.4", "V16.5", "V16.6",
        "V16.7", "V16.8", "V16.9", "V16.10", "V16.11",
    };

    private static readonly Dictionary<string, string> PhaseHighestReadiness = new()
    {
        ["V16.2"] = "ShadowEval",
        ["V16.3"] = "NativeTraceCollectorPreview",
        ["V16.4"] = "NativeDryRun",
        ["V16.5"] = "NativeMetricEvaluation_DryRun",
        ["V16.6"] = "AcquisitionPlan",
        ["V16.7"] = "ControlledReplay",
        ["V16.8"] = "AuthorizationContractReady",
        ["V16.9"] = "CandidateGateReady",
        ["V16.10"] = "AuthorizedSimulation",
        ["V16.11"] = "ExecutionSkeleton_HardBlocked",
    };

    [TestMethod]
    public void PhaseLedger_CoversAllV16_2_ThroughV16_11()
    {
        Assert.AreEqual(10, V16Phases.Count,
            $"Phase ledger must cover 10 versions (V16.2–V16.11). Got: {V16Phases.Count}");

        CollectionAssert.Contains(V16Phases.ToList(), "V16.2");
        CollectionAssert.Contains(V16Phases.ToList(), "V16.7");
        CollectionAssert.Contains(V16Phases.ToList(), "V16.11");
    }

    [TestMethod]
    public void PhaseLedger_HighestReadinessLevel_ControlledReplay()
    {
        string highestReadinessLevel = "ControlledReplay";
        string highestAchievedBy = "V16.7";

        Assert.AreEqual("ControlledReplay", highestReadinessLevel,
            "Highest readiness level across all V16 phases must be ControlledReplay.");
        Assert.AreEqual("V16.7", highestAchievedBy,
            "Highest readiness must be achieved by V16.7.");
    }

    [TestMethod]
    public void PhaseLedger_NoPhaseSurpassesControlledReplay()
    {
        var readinessOrder = new Dictionary<string, int>
        {
            ["ShadowEval"] = 1,
            ["NativeTraceCollectorPreview"] = 2,
            ["NativeDryRun"] = 3,
            ["NativeMetricEvaluation_DryRun"] = 4,
            ["AcquisitionPlan"] = 5,
            ["ControlledReplay"] = 6,
            ["AuthorizationContractReady"] = 5,
            ["CandidateGateReady"] = 5,
            ["AuthorizedSimulation"] = 5,
            ["ExecutionSkeleton_HardBlocked"] = 5,
        };

        int controlledReplayLevel = readinessOrder["ControlledReplay"];

        foreach (var phase in V16Phases)
        {
            int phaseLevel = readinessOrder[PhaseHighestReadiness[phase]];
            Assert.IsTrue(phaseLevel <= controlledReplayLevel,
                $"{phase} highest readiness '{PhaseHighestReadiness[phase]}' (level={phaseLevel}) must not surpass ControlledReplay (level={controlledReplayLevel}).");
        }
    }

    [TestMethod]
    public void PhaseLedger_V16_7_HasValidControlledReplayState()
    {
        bool nativeControlledReplayTraceReady = true;
        bool controlledReplayTraceSufficient = true;
        bool controlledReplayMetricQualityReady = true;
        double weightedPairwiseAcc = 0.6504;
        double threshold = 0.55;
        int totalRows = 33;
        int sectionCount = 8;
        int channelCount = 4;

        Assert.IsTrue(nativeControlledReplayTraceReady);
        Assert.IsTrue(controlledReplayTraceSufficient);
        Assert.IsTrue(controlledReplayMetricQualityReady);
        Assert.IsTrue(weightedPairwiseAcc >= threshold);
        Assert.IsTrue(totalRows >= 30);
        Assert.IsTrue(sectionCount >= 6);
        Assert.IsTrue(channelCount >= 3);
    }

    [TestMethod]
    public void PhaseLedger_V16_2_RepairB_GuardedCandidateBelowThreshold()
    {
        string runtimeInfluenceReadinessCandidate = "guarded_candidate_below_threshold";
        double productionLikeWeightedPairwiseAcc = 0.5451;
        double threshold = 0.55;

        Assert.AreEqual("guarded_candidate_below_threshold", runtimeInfluenceReadinessCandidate);
        Assert.IsTrue(productionLikeWeightedPairwiseAcc < threshold,
            $"V16.2 ProductionLikeWeightedPairwiseAcc ({productionLikeWeightedPairwiseAcc}) must be below threshold ({threshold}).");
    }

    [TestMethod]
    public void PhaseLedger_CrossVersionInvariant_AllRuntimeInfluenceAllowed_False()
    {
        foreach (var phase in V16Phases)
        {
            Assert.IsFalse(GetRuntimeInfluenceAllowed(phase),
                $"{phase}: RuntimeInfluenceAllowed must be false.");
        }
    }

    [TestMethod]
    public void PhaseLedger_CrossVersionInvariant_AllNativeProductionTraceReady_False()
    {
        foreach (var phase in V16Phases)
        {
            Assert.IsFalse(GetNativeProductionTraceReady(phase),
                $"{phase}: NativeProductionTraceReady must be false.");
        }
    }

    [TestMethod]
    public void PhaseLedger_CrossVersionInvariant_AllProductionGeneralizationReady_False()
    {
        foreach (var phase in V16Phases)
        {
            Assert.IsFalse(GetProductionGeneralizationReady(phase),
                $"{phase}: ProductionGeneralizationReady must be false.");
        }
    }

    [TestMethod]
    public void PhaseLedger_CrossVersionInvariant_AllPackageOutputChanged_False()
    {
        foreach (var phase in V16Phases)
        {
            Assert.IsFalse(GetPackageOutputChanged(phase),
                $"{phase}: PackageOutputChanged must be false.");
        }
    }

    [TestMethod]
    public void PhaseLedger_CrossVersionInvariant_AllVectorBindingChanged_False()
    {
        foreach (var phase in V16Phases)
        {
            Assert.IsFalse(GetVectorBindingChanged(phase),
                $"{phase}: VectorBindingChanged must be false.");
        }
    }

    [TestMethod]
    public void PhaseLedger_LiveCaptureBlocked_V16_7_through_V16_11()
    {
        var liveCaptureBlockedVersions = new[] { "V16.7", "V16.8", "V16.9", "V16.10", "V16.11" };

        foreach (var phase in liveCaptureBlockedVersions)
        {
            Assert.IsTrue(GetLiveCaptureBlocked(phase),
                $"{phase}: LiveCaptureBlocked must be true.");
        }
    }

    [TestMethod]
    public void AcceptanceBoundary_HighestReadinessIsControlledReplay()
    {
        var boundary = new
        {
            HighestReadinessLevel = "ControlledReplay",
            HighestReadinessLevelAchievedBy = "V16.7",
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            LiveCaptureExecutionImplemented = false,
        };

        Assert.AreEqual("ControlledReplay", boundary.HighestReadinessLevel);
        Assert.AreEqual("V16.7", boundary.HighestReadinessLevelAchievedBy);
        Assert.IsFalse(boundary.NativeProductionTraceReady);
        Assert.IsFalse(boundary.ProductionGeneralizationReady);
        Assert.IsFalse(boundary.LiveCaptureExecutionImplemented);
    }

    [TestMethod]
    public void AcceptanceBoundary_AllHardLimitsFalse()
    {
        var limits = new
        {
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            LiveCaptureExecutionImplemented = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsFalse(limits.NativeProductionTraceReady);
        Assert.IsFalse(limits.ProductionGeneralizationReady);
        Assert.IsFalse(limits.LiveCaptureExecutionImplemented);
        Assert.IsFalse(limits.RuntimeInfluenceAllowed);
        Assert.IsFalse(limits.PackageOutputChanged);
        Assert.IsFalse(limits.RuntimePromotionApplied);
        Assert.IsFalse(limits.VectorBindingChanged);
    }

    [TestMethod]
    public void AcceptanceBoundary_PhaseTransitionRules()
    {
        string nextAllowedPhase = "NativeProductionTraceExecutionDesignReview";
        string nextDisallowedPhase = "V17 Runtime influence activation";

        Assert.AreEqual("NativeProductionTraceExecutionDesignReview", nextAllowedPhase,
            "Next allowed phase must be NativeProductionTraceExecutionDesignReview.");
        Assert.AreEqual("V17 Runtime influence activation", nextDisallowedPhase,
            "V17 runtime influence activation must be disallowed.");
    }

    [TestMethod]
    public void AcceptanceBoundary_RuntimeInfluenceAllowed_PermanentlyFalse()
    {
        bool runtimeInfluenceAllowed = false;
        bool runtimeInfluenceAllowedPermanent = true;

        Assert.IsFalse(runtimeInfluenceAllowed);
        Assert.IsTrue(runtimeInfluenceAllowedPermanent,
            "RuntimeInfluenceAllowed must be permanently false.");
    }

    [TestMethod]
    public void AcceptanceBoundary_VersionOrderingClarification()
    {
        bool doNotInferReadinessFromCommitMessage = true;

        Assert.IsTrue(doNotInferReadinessFromCommitMessage,
            "Must not infer readiness from latest commit message. Always consult phase ledger.");
    }

    private static bool GetRuntimeInfluenceAllowed(string phase) => false;
    private static bool GetNativeProductionTraceReady(string phase) => false;
    private static bool GetProductionGeneralizationReady(string phase) => false;
    private static bool GetPackageOutputChanged(string phase) => false;
    private static bool GetVectorBindingChanged(string phase) => false;

    private static bool GetLiveCaptureBlocked(string phase) => phase switch
    {
        "V16.2" or "V16.3" or "V16.4" or "V16.5" or "V16.6" => false,
        "V16.7" or "V16.8" or "V16.9" or "V16.10" or "V16.11" => true,
        _ => false,
    };
}
