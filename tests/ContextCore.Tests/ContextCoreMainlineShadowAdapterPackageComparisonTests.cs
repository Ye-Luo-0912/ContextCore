using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreMainlineShadowAdapterPackageComparisonTests
{
    [TestMethod]
    public void Comparison_CleanInputs_Passes()
    {
        var v61 = new ScopedShadowAdapterInvocationReport { InvocationPassed = true, AllowlistedInvocationCount = 1, NonAllowlistedInvocationCount = 1 };
        var dataset = BuildDataset();
        var report = new MainlineShadowAdapterPackageComparisonRunner().RunComparison(v61, dataset);

        Assert.IsTrue(report.ComparisonPassed, $"should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.IsTrue(report.MainlineInvocationCount > 0);
        Assert.IsTrue(report.TraceCompleteness >= 1.0);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void Comparison_MissingV61Gate_Blocks()
    {
        var report = new MainlineShadowAdapterPackageComparisonRunner().RunComparison(null, BuildDataset());
        Assert.IsFalse(report.ComparisonPassed);
        Assert.IsTrue(report.BlockedReasons.Contains("V61GateNotPassed"));
    }

    [TestMethod]
    public void Comparison_MissingDataset_Blocks()
    {
        var v61 = new ScopedShadowAdapterInvocationReport { InvocationPassed = true };
        var report = new MainlineShadowAdapterPackageComparisonRunner().RunComparison(v61, null);
        Assert.IsFalse(report.ComparisonPassed);
    }

    private static RetrievalDatasetV2GeneratedDataset BuildDataset()
    {
        var corpus = new[]
        {
            new RetrievalDatasetV2CorpusItem { ItemId = "item-a", Content = "active target query match", Tags = new[] { "target", "active" }, Anchors = new[] { "target" }, Lifecycle = "Active", TargetSection = "normal_context", ItemKind = "note", SourceKind = "note" },
            new RetrievalDatasetV2CorpusItem { ItemId = "item-b", Content = "secondary item for testing", Tags = new[] { "secondary" }, Anchors = new[] { "secondary" }, Lifecycle = "Active", TargetSection = "normal_context", ItemKind = "note", SourceKind = "note" },
        };
        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "s-test-1", QueryText = "target query test", ExpectedTargetSection = "normal_context",
            MustHitItemIds = new[] { "item-a" }, MustNotHitItemIds = Array.Empty<string>(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["workspaceId"] = "ws-test", ["collectionId"] = "col-test" }
        };
        return new RetrievalDatasetV2GeneratedDataset { CorpusItems = corpus, Samples = new[] { sample } };
    }
}