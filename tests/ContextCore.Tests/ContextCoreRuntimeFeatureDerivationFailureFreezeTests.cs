using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRuntimeFeatureDerivationFailureFreezeTests
{
    [TestMethod]
    public void FailureFreeze_WithFailedRepairGate_FreezePasses()
    {
        var repairGate = BuildRepairGate();
        var derivationGate = BuildDerivationGate();
        var report = new RuntimeFeatureDerivationFailureFreezeRunner()
            .BuildFreeze(repairGate, derivationGate);

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(
            RuntimeFeatureDerivationFailureFreezeRecommendations.ReadyForGraphHubNoiseControlPreview,
            report.Recommendation);
        Assert.AreEqual("BlockedByHubRelationNoise", report.FrozenStatus);
        Assert.IsTrue(report.CanonicalAnchorResolverReusable);
        Assert.IsFalse(report.RuntimeRelationIntentDeriverReady);
        Assert.IsTrue(report.CombinedRepairEvalUpperBoundOnly);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsTrue(report.DisabledCapabilities.Count > 0);
        Assert.IsTrue(report.RecommendedNextPhases.Count > 0);
        Assert.IsTrue(report.FrozenArtifactPaths.Count > 0);
        Assert.AreEqual(0.5083, report.V57DerivedRecall, 0.001);
        Assert.AreEqual(0.4792, report.V58TrainDerivedRecall, 0.001);
    }

    [TestMethod]
    public void FailureFreeze_MissingRepairGate_BlocksRun()
    {
        var report = new RuntimeFeatureDerivationFailureFreezeRunner()
            .BuildFreeze(repairGate: null, BuildDerivationGate());

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            RuntimeFeatureDerivationFailureFreezeRecommendations.BlockedByMissingRepairGate,
            report.Recommendation);
    }

    [TestMethod]
    public void FailureFreeze_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src", "ContextCore.Core", "Services", "Vector",
            "RuntimeFeatureDerivationFailureFreezeRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static RuntimeRetrievalFeatureDerivationRepairReport BuildRepairGate()
    {
        var options = new RuntimeRetrievalFeatureDerivationRepairOptions();
        return new RuntimeRetrievalFeatureDerivationRepairReport
        {
            PreviewPassed = false,
            GatePassed = false,
            Recommendation = RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByDerivedRecallNotImproved,
            TrainBaselineRecall = 0.5104,
            TrainDerivedRecall = 0.4792,
            TrainBaselineMrr = 0.2299,
            TrainDerivedMrr = 0.1998,
            HoldoutBaselineRecall = 0.5000,
            HoldoutDerivedRecall = 0.4583,
            HoldoutBaselineMrr = 0.2181,
            HoldoutDerivedMrr = 0.2076,
            CanonicalRequiredRelationCoverageRate = 0.1333,
            CanonicalEvidenceAnchorCoverageRate = 0.4750,
            CanonicalSourceAnchorCoverageRate = 0.4750,
            BlockedReasons = new[] { "DerivedRecallNotImproved", "HoldoutRecallRegression", "LowRelationCoverage" }
        };
    }

    private static RuntimeRetrievalFeatureDerivationReport BuildDerivationGate()
        => new()
        {
            GatePassed = true,
            DerivedRecall = 0.5083,
            DerivedMeanReciprocalRank = 0.2275
        };

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContextCore.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray());
    }
}