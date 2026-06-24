using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreVectorLifecycleMetadataRepairPlanTests
{
    [TestMethod]
    public void LifecycleMetadataRepairPlan_DeprecatedCandidateCannotBeAutoRepaired()
    {
        var report = BuildReport(
            [Detail("sample-a", "item-a", lifecycle: "Deprecated")],
            [Source("item-a", ("sourceRef", "source-a"), ("reviewStatus", "Current"))],
            [Entry("item-a")]);
        var candidate = report.Candidates.Single();

        Assert.IsFalse(candidate.CanAutoRepair);
        Assert.AreEqual("UnsafeLifecycle", candidate.ForbiddenReason);
        Assert.AreNotEqual(VectorQueryTargetSections.NormalContext, candidate.ProposedTargetSection);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_MissingProvenanceRequiresHumanReview()
    {
        var report = BuildReport(
            [Detail("sample-a", "item-a")],
            [Source("item-a", ("reviewStatus", "Current"))],
            [Entry("item-a")]);
        var candidate = report.Candidates.Single();

        Assert.IsFalse(candidate.CanAutoRepair);
        Assert.IsTrue(candidate.RequiresHumanReview);
        Assert.AreEqual("MissingProvenance", candidate.ForbiddenReason);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_ActiveProvenanceCandidateCanBeMarkedAutoRepairable()
    {
        var report = BuildReport(
            [Detail("sample-a", "item-a")],
            [Source("item-a", ("sourceRef", "source-a"), ("reviewStatus", "Stable"))],
            [Entry("item-a")]);
        var candidate = report.Candidates.Single();

        Assert.IsTrue(candidate.CanAutoRepair);
        Assert.IsFalse(candidate.RequiresHumanReview);
        Assert.AreEqual("Active", candidate.ProposedLifecycle);
        Assert.AreEqual("Current", candidate.ProposedReviewStatus);
        Assert.AreEqual(VectorQueryTargetSections.NormalContext, candidate.ProposedTargetSection);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_ConflictingReplacementRelationForbidsRepair()
    {
        var report = BuildReport(
            [Detail("sample-a", "item-a")],
            [Source("item-a", ("sourceRef", "source-a"), ("reviewStatus", "Current"), ("replacementState", "Superseded"))],
            [Entry("item-a")]);
        var candidate = report.Candidates.Single();

        Assert.IsFalse(candidate.CanAutoRepair);
        Assert.AreEqual("ConflictingReplacementRelation", candidate.ForbiddenReason);
        Assert.AreEqual(VectorQueryTargetSections.Excluded, candidate.ProposedTargetSection);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_EvalLabelAloneCannotJustifyRepair()
    {
        var report = BuildReport(
            [Detail("sample-a", "item-a")],
            [],
            []);
        var candidate = report.Candidates.Single();

        Assert.IsFalse(candidate.CanAutoRepair);
        Assert.IsTrue(candidate.RequiresHumanReview);
        Assert.AreEqual("OnlyEvalLabelSupportsRepair", candidate.ForbiddenReason);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_SkipsCorrectlyBlockedDeprecated()
    {
        var report = BuildReport(
            [
                Detail("sample-a", "item-a", category: VectorEligibilityRecallLossTriageCategories.CorrectlyBlockedDeprecated),
                Detail("sample-b", "item-b")
            ],
            [Source("item-b", ("sourceRef", "source-b"), ("reviewStatus", "Current"))],
            [Entry("item-b")]);

        Assert.AreEqual(1, report.CorrectlyBlockedSkippedCount);
        Assert.AreEqual(1, report.CandidateCount);
        Assert.AreEqual("item-b", report.Candidates.Single().MustHitItemId);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_NoSampleIdOrItemIdSpecificShortcut()
    {
        var reportA = BuildReport(
            [Detail("sample-a", "item-a")],
            [Source("item-a", ("sourceRef", "source-a"), ("reviewStatus", "Current"))],
            [Entry("item-a")]);
        var reportB = BuildReport(
            [Detail("sample-b", "item-b")],
            [Source("item-b", ("sourceRef", "source-b"), ("reviewStatus", "Current"))],
            [Entry("item-b")]);

        Assert.AreEqual(reportA.Candidates.Single().CanAutoRepair, reportB.Candidates.Single().CanAutoRepair);
        Assert.AreEqual(reportA.Candidates.Single().ProposedTargetSection, reportB.Candidates.Single().ProposedTargetSection);
        Assert.AreEqual(reportA.Candidates.Single().RepairReason, reportB.Candidates.Single().RepairReason);
    }

    [TestMethod]
    public void LifecycleMetadataRepairPlan_NoFixtureDomainLexiconInProductionRunner()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "VectorLifecycleMetadataRepairPlanRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixture/domain keyword: {forbidden}");
        }
    }

    private static VectorLifecycleMetadataRepairPlanReport BuildReport(
        IReadOnlyList<VectorEligibilityRecallLossTriageDetail> details,
        IReadOnlyList<VectorReindexSourceItem> sources,
        IReadOnlyList<VectorIndexEntry> entries)
    {
        var triage = new VectorEligibilityRecallLossTriageReport
        {
            DatasetName = "A3",
            ProviderId = "test-provider",
            EmbeddingModel = "test-model",
            Dimension = 8,
            Details = details
        };
        return new VectorLifecycleMetadataRepairPlanRunner().BuildReport(triage, sources, entries);
    }

    private static VectorEligibilityRecallLossTriageDetail Detail(
        string sampleId,
        string itemId,
        string lifecycle = "Unknown",
        string category = VectorEligibilityRecallLossTriageCategories.MetadataLifecycleRepairNeeded)
    {
        return new VectorEligibilityRecallLossTriageDetail
        {
            DatasetName = "A3",
            SampleId = sampleId,
            MustHitItemId = itemId,
            Lifecycle = lifecycle,
            ReviewStatus = string.Empty,
            CurrentTargetSection = VectorQueryTargetSections.Excluded,
            CandidateTargetSection = VectorQueryTargetSections.DiagnosticsOnly,
            TriageCategory = category
        };
    }

    private static VectorReindexSourceItem Source(string itemId, params (string Key, string Value)[] metadata)
    {
        return new VectorReindexSourceItem
        {
            ItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            Text = "shared context",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static VectorIndexEntry Entry(string itemId)
    {
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
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string ResolveRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());return TestRepoFileResolver.Resolve(parts);}
}
