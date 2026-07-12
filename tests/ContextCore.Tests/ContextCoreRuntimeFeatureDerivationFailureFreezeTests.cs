using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Vector.Gates;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Unit")]
public class ContextCoreRuntimeFeatureDerivationFailureFreezeTests
{

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
            "src", "ContextCore.Evaluation", "Vector", "Evaluation", "Gates",
            "RuntimeFeatureDerivationFailureFreezeRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal));
        }
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(segments);}
}
