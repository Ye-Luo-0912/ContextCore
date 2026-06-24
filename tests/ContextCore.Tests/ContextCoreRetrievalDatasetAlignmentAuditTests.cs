using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRetrievalDatasetAlignmentAuditTests
{
    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_MissingMustHitFromCorpus_IsRecognized()
    {
        var report = BuildReport(
            [Sample("s1", "known token", ["missing-item"])],
            [Source("source-1", "known token")],
            [Entry("source-1", "known token")]);

        Assert.AreEqual(1, report.MustHitMissingFromCorpusCount);
        Assert.AreEqual(RetrievalDatasetAlignmentRecommendations.NeedsCorpusBackfill, report.Recommendation);
        Assert.IsTrue(report.IssueBreakdown.ContainsKey(RetrievalDatasetAlignmentIssueTypes.MustHitMissingFromCorpus));
    }

    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_MustHitBlockedByEligibility_IsRecognized()
    {
        var report = BuildReport(
            [Sample("s1", "stable token", ["deprecated-item"])],
            [Source("deprecated-item", "stable token")],
            [Entry("deprecated-item", "stable token", lifecycle: "Deprecated")]);

        Assert.AreEqual(1, report.MustHitBlockedByEligibilityCount);
        Assert.IsTrue(report.IssueBreakdown.ContainsKey(RetrievalDatasetAlignmentIssueTypes.MustHitLifecycleFiltered));
    }

    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_QueryTokenSparse_IsRecognized()
    {
        var report = BuildReport(
            [Sample("s1", "!!!", ["source-1"])],
            [Source("source-1", "known token")],
            [Entry("source-1", "known token")]);

        Assert.IsTrue(report.IssueBreakdown.ContainsKey(RetrievalDatasetAlignmentIssueTypes.QueryTokenTooSparse));
    }

    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_MissingAnchorMetadata_IsRecognized()
    {
        var source = Source("source-1", "known token", itemKind: string.Empty, tags: string.Empty);
        var entry = Entry("source-1", "known token", itemKind: string.Empty, tags: string.Empty);
        var report = BuildReport(
            [Sample("s1", "known token", ["source-1"])],
            [source],
            [entry]);

        Assert.AreEqual(0.0, report.AnchorCoverageRate, 0.0001);
        Assert.IsTrue(report.IssueBreakdown.ContainsKey(RetrievalDatasetAlignmentIssueTypes.MissingAnchorMetadata));
    }

    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_ProviderScopeMismatch_IsRecognized()
    {
        var report = BuildReport(
            [Sample("s1", "known token", ["source-1"])],
            [Source("source-1", "known token")],
            [Entry("source-1", "known token", providerId: "other-provider")]);

        Assert.AreEqual(0, report.MustHitPresentInProviderScopeCount);
        Assert.IsTrue(report.IssueBreakdown.ContainsKey(RetrievalDatasetAlignmentIssueTypes.ProviderScopeMismatch));
        Assert.AreEqual(RetrievalDatasetAlignmentRecommendations.NeedsProviderScopeRepair, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_NoSampleIdOrItemIdSpecificShortcut()
    {
        var reportA = BuildReport(
            [Sample("sample-a", "shared token", ["item-a"])],
            [Source("item-a", "shared token")],
            [Entry("item-a", "shared token")]);
        var reportB = BuildReport(
            [Sample("sample-b", "shared token", ["item-b"])],
            [Source("item-b", "shared token")],
            [Entry("item-b", "shared token")]);

        Assert.AreEqual(reportA.QueryCorpusTokenOverlapAverage, reportB.QueryCorpusTokenOverlapAverage, 0.0001);
        Assert.AreEqual(reportA.AlignmentIssueCount, reportB.AlignmentIssueCount);
    }

    [TestMethod]
    public void RetrievalDatasetAlignmentAudit_NoFixtureDomainLexiconInProductionRunner()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "RetrievalDatasetAlignmentAuditRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixture/domain keyword: {forbidden}");
        }
    }

    private static RetrievalDatasetAlignmentAuditReport BuildReport(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorReindexSourceItem> sources,
        IReadOnlyList<VectorIndexEntry> entries)
    {
        return new RetrievalDatasetAlignmentAuditRunner().BuildReport(
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
            MustHit = mustHit
        };
    }

    private static VectorReindexSourceItem Source(
        string itemId,
        string text,
        string itemKind = "note",
        string tags = "known",
        string sourceKind = "context")
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceKind"] = sourceKind
        };
        if (!string.IsNullOrWhiteSpace(tags))
        {
            metadata["sourceTags"] = tags;
        }

        return new VectorReindexSourceItem
        {
            ItemId = itemId,
            ItemKind = itemKind,
            Layer = "context",
            Text = text,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };
    }

    private static VectorIndexEntry Entry(
        string itemId,
        string text,
        string providerId = "test-provider",
        string modelId = "test-model",
        string lifecycle = "Stable",
        string itemKind = "note",
        string tags = "known",
        string sourceKind = "context")
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["indexedText"] = text,
            ["lifecycle"] = lifecycle,
            ["sourceKind"] = sourceKind
        };
        if (!string.IsNullOrWhiteSpace(tags))
        {
            metadata["sourceTags"] = tags;
        }

        return new VectorIndexEntry
        {
            EntryId = $"{itemId}:{providerId}:{modelId}",
            ItemId = itemId,
            ItemKind = itemKind,
            Layer = "context",
            WorkspaceId = "workspace",
            CollectionId = "collection",
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
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
