using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreVectorEligibilityRecallLossTriageTests
{
    [TestMethod]
    public void EligibilityRecallLossTriage_DeprecatedMustHit_ClassifiedCorrectlyBlocked()
    {
        var report = BuildReport(
            [Sample("sample-a", "deprecated context", ["item-deprecated"])],
            [Source("item-deprecated")],
            [Entry("item-deprecated", lifecycle: "Deprecated")]);
        var detail = report.Details.Single();

        Assert.AreEqual(VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedDeprecated, detail.TriageCategory);
        Assert.AreEqual(VectorQueryTargetSections.AuditContext, detail.CandidateTargetSection);
        Assert.IsTrue(detail.ShouldRemainBlocked);
        Assert.AreNotEqual(VectorQueryTargetSections.NormalContext, detail.CandidateTargetSection);
    }

    [TestMethod]
    public void EligibilityRecallLossTriage_HistoricalMustHit_RoutesOnlyToHistoricalOrAuditContext()
    {
        var report = BuildReport(
            [Sample("sample-a", "historical context", ["item-historical"])],
            [Source("item-historical")],
            [Entry("item-historical", lifecycle: "Historical")]);
        var detail = report.Details.Single();

        Assert.AreEqual(VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedHistorical, detail.TriageCategory);
        Assert.IsTrue(
            string.Equals(detail.CandidateTargetSection, VectorQueryTargetSections.HistoricalContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(detail.CandidateTargetSection, VectorQueryTargetSections.AuditContext, StringComparison.OrdinalIgnoreCase));
        Assert.AreNotEqual(VectorQueryTargetSections.NormalContext, detail.CandidateTargetSection);
    }

    [TestMethod]
    public void EligibilityRecallLossTriage_UnknownLifecycle_ClassifiedMetadataRepairNeeded()
    {
        var report = BuildReport(
            [Sample("sample-a", "active candidate", ["item-unknown"])],
            [Source("item-unknown")],
            [Entry("item-unknown", lifecycle: string.Empty)]);
        var detail = report.Details.Single();

        Assert.AreEqual(VectorEligibilityRecallLossTriageCategories.MetadataLifecycleRepairNeeded, detail.TriageCategory);
        Assert.IsTrue(detail.CanRepairMetadata);
        Assert.AreEqual(VectorQueryTargetSections.DiagnosticsOnly, detail.CandidateTargetSection);
    }

    [TestMethod]
    public void EligibilityRecallLossTriage_NormalContextRecoveryForbiddenForDeprecatedOrSuperseded()
    {
        var report = BuildReport(
            [
                Sample("sample-a", "deprecated context", ["item-deprecated"]),
                Sample("sample-b", "superseded context", ["item-superseded"])
            ],
            [
                Source("item-deprecated"),
                Source("item-superseded")
            ],
            [
                Entry("item-deprecated", lifecycle: "Deprecated"),
                Entry("item-superseded", lifecycle: "Superseded")
            ]);

        Assert.AreEqual(2, report.Details.Count);
        Assert.IsTrue(report.Details.All(detail => detail.ShouldRemainBlocked));
        Assert.IsTrue(report.Details.All(detail =>
            !string.Equals(detail.CandidateTargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void EligibilityRecallLossTriage_NoSampleIdOrItemIdSpecificShortcut()
    {
        var reportA = BuildReport(
            [Sample("sample-a", "shared context", ["item-a"])],
            [Source("item-a")],
            [Entry("item-a", lifecycle: "Deprecated")]);
        var reportB = BuildReport(
            [Sample("sample-b", "shared context", ["item-b"])],
            [Source("item-b")],
            [Entry("item-b", lifecycle: "Deprecated")]);

        Assert.AreEqual(reportA.Details.Single().TriageCategory, reportB.Details.Single().TriageCategory);
        Assert.AreEqual(reportA.Details.Single().RecommendedAction, reportB.Details.Single().RecommendedAction);
        Assert.AreEqual(reportA.Details.Single().CandidateTargetSection, reportB.Details.Single().CandidateTargetSection);
    }

    [TestMethod]
    public void EligibilityRecallLossTriage_NoFixtureDomainLexiconInProductionRunner()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "VectorEligibilityRecallLossTriageRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixture/domain keyword: {forbidden}");
        }
    }

    private static VectorEligibilityRecallLossTriageReport BuildReport(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorReindexSourceItem> sources,
        IReadOnlyList<VectorIndexEntry> entries)
    {
        return new VectorEligibilityRecallLossTriageRunner().BuildReport(
            "A3",
            samples,
            sources,
            entries,
            new EmbeddingProviderOptions
            {
                ProviderId = "test-provider",
                EmbeddingModel = "test-model",
                Dimension = 8
            });
    }

    private static ContextEvalSample Sample(string sampleId, string query, IReadOnlyList<string> mustHit)
    {
        return new ContextEvalSample
        {
            Id = sampleId,
            Mode = "TestMode",
            Query = query,
            MustHit = mustHit,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["intent"] = "TestIntent"
            }
        };
    }

    private static VectorReindexSourceItem Source(string itemId)
    {
        return new VectorReindexSourceItem
        {
            ItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            Text = "shared context",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceKind"] = "context"
            }
        };
    }

    private static VectorIndexEntry Entry(string itemId, string lifecycle)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["indexedText"] = "shared context",
            ["sourceKind"] = "context"
        };
        if (!string.IsNullOrWhiteSpace(lifecycle))
        {
            metadata["lifecycle"] = lifecycle;
        }

        return new VectorIndexEntry
        {
            EntryId = $"{itemId}:test-provider:test-model",
            ItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            WorkspaceId = "workspace",
            CollectionId = "collection",
            EmbeddingProvider = "test-provider",
            EmbeddingModel = "test-model",
            Dimension = 8,
            ContentHash = "hash",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };
    }

    private static string ResolveRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());return TestRepoFileResolver.Resolve(parts);}
}
