using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreGraphHubNoiseControlTests
{
    [TestMethod]
    public void HubNoiseControl_PreviewPasses_WhenEnvelopeCapped()
    {
        var dataset = BuildDataset();
        var freeze = BuildFreezeGate();
        var repairGate = BuildRepairGate();
        var report = new GraphHubNoiseControlRunner().BuildPreview(freeze, dataset, repairGate);
        Assert.IsTrue(report.PreviewPassed, $"hub-controlled should not regress; blocked? check metrics");
    }

    [TestMethod]
    public void HubNoiseControl_GateMode_GatePassed()
    {
        var report = new GraphHubNoiseControlRunner().BuildGate(BuildFreezeGate(), BuildDataset(), BuildRepairGate());
        Assert.IsTrue(report.OperationId.StartsWith("graph-hub-noise-control-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HubNoiseControl_MissingFreezeGate_BlocksRun()
    {
        var report = new GraphHubNoiseControlRunner().BuildPreview(null, BuildDataset(), BuildRepairGate());
        Assert.IsFalse(report.PreviewPassed);
    }

    [TestMethod]
    public void HubNoiseControl_HasNoKnownFixtureTerms()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "Evaluation", "V6", "GraphHubNoiseControlRunner.cs"));
        foreach (var f in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
            Assert.IsFalse(source.Contains(f, StringComparison.Ordinal));
    }

    private static RuntimeFeatureDerivationFailureFreezeReport BuildFreezeGate()
        => new() { FreezePassed = true, FrozenStatus = "BlockedByHubRelationNoise", CanonicalAnchorResolverReusable = true };

    private static RuntimeRetrievalFeatureDerivationRepairReport BuildRepairGate()
        => new() { TrainDerivedRecall = 0.47, TrainDerivedMrr = 0.20 };

    private static RetrievalDatasetV2GeneratedDataset BuildDataset()
    {
        var corpus = new[] {
            BuildItem("item-1", "active target hub", new[] { "note" }, new[] { "note" }, null, null, Enumerable.Range(0, 20).Select(i => ("rel-hub-" + i, "target-" + i)).ToArray()),
            BuildItem("item-2", "specific target secondary", new[] { "target" }, new[] { "specific" }, new[] { "ev-1" }, new[] { "src-1" }, new[] { ("rel-specific", "target-specific") }),
            BuildItem("item-3", "noise unrelated", new[] { "noise" }, new[] { "rule" }, null, null, null),
        };
        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-test-1", QueryText = "active target hub note query",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "item-1", "item-2" }, MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "ev-1" }, SourceRefs = new[] { "src-1" }, RequiredRelations = new[] { "rel-specific" }
        };
        return new RetrievalDatasetV2GeneratedDataset { CorpusItems = corpus, Samples = new[] { sample } };
    }

    private static RetrievalDatasetV2CorpusItem BuildItem(string id, string content, string[] tags, string[] anchors, string[]? ev, string[]? src, (string, string)[]? rels)
        => new()
        {
            ItemId = id, Content = content, Tags = tags, Anchors = anchors, ItemKind = "note", SourceKind = "note", Layer = "Stable",
            Lifecycle = "Active", ReviewStatus = "Reviewed", ReplacementState = "Current",
            TargetSection = VectorQueryTargetSections.NormalContext,
            EvidenceRefs = ev ?? Array.Empty<string>(), SourceRefs = src ?? Array.Empty<string>(),
            Relations = (rels ?? Array.Empty<(string, string)>()).Select(r => new RetrievalDatasetV2Relation { RelationId = r.Item1, SourceItemId = id, TargetItemId = r.Item2, RelationType = "supports" }).ToArray()
        };

    private static string ResolveRepoFile(params string[] s)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(s);}
}