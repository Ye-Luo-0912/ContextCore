using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreInputMetadataEnrichmentPreviewTests
{
    [TestMethod]
    public void InputMetadataEnrichmentPreview_GatePassesAndImprovesCoverage()
    {
        var dataset = BuildDataset();
        var report = new InputMetadataEnrichmentPreviewRunner().BuildGate(
            dataset,
            BuildProtocolGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true));

        Assert.IsTrue(report.PreviewPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(
            report.IndependentNonDenseSourceCount > 0
            || string.Equals(report.Recommendation, InputMetadataEnrichmentPreviewRecommendations.NeedsSourceDiverseDataset, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(report.MetadataCoverageDelta > 0);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicy);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void InputMetadataEnrichmentPreview_MissingProtocolGateBlocks()
    {
        var report = new InputMetadataEnrichmentPreviewRunner().BuildGate(
            BuildDataset(),
            BuildProtocolGate(passed: false),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(InputMetadataEnrichmentPreviewRecommendations.BlockedByProtocolMismatch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V511ProtocolGateNotPassed");
    }

    [TestMethod]
    public void InputMetadataEnrichmentPreview_ProjectionDoesNotMutateOriginalDataset()
    {
        var dataset = BuildDataset();
        var originalItem = dataset.CorpusItems[0];
        var projected = InputMetadataEnrichmentPreviewRunner.BuildEnrichedProjection(dataset);

        Assert.IsFalse(originalItem.Metadata.ContainsKey("enrichment.generatedBy"));
        Assert.IsTrue(projected.CorpusItems[0].Metadata.ContainsKey("enrichment.generatedBy"));
        Assert.AreSame(dataset.Samples, projected.Samples);
    }

    [TestMethod]
    public void InputMetadataEnrichmentPreview_SourceDoesNotReadEvalLabelsOrSpecialCaseIds()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "InputMetadataEnrichmentPreviewRunner.cs"));
        Assert.IsFalse(source.Contains(".MustHitItemIds", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(".MustNotHitItemIds", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(".ExpectedTargetSection", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    private static RetrievalDatasetV2GeneratedDataset BuildDataset()
    {
        var corpus = new[]
        {
            BuildItem(
                "item-evidence",
                "restore guidance checkpoint stable rollback",
                ["restore"],
                ["checkpoint"],
                ["ev-shared"],
                ["src-shared"],
                [("rel-shared", "supports")]),
            BuildItem(
                "item-anchor",
                "schema migration preview rollback confirmation",
                ["schema"],
                ["migration"],
                ["ev-schema"],
                ["src-schema"],
                [("rel-schema", "depends_on")]),
            BuildItem(
                "item-negative",
                "archived distractor not selected",
                ["archive"],
                ["distractor"],
                ["ev-negative"],
                ["src-negative"],
                [])
        };
        var samples = new[]
        {
            new RetrievalDatasetV2Sample
            {
                SampleId = "sample-one",
                QueryText = "restore checkpoint stable rollback",
                Difficulty = "direct_lexical",
                Split = "train",
                ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                MustHitItemIds = ["item-evidence"],
                MustNotHitItemIds = ["item-negative"]
            },
            new RetrievalDatasetV2Sample
            {
                SampleId = "sample-two",
                QueryText = "schema migration preview rollback",
                Difficulty = "metadata_anchor",
                Split = "holdout",
                ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                MustHitItemIds = ["item-anchor"],
                MustNotHitItemIds = ["item-negative"]
            }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = samples
        };
    }

    private static RetrievalDatasetV2CorpusItem BuildItem(
        string id,
        string content,
        string[] tags,
        string[] anchors,
        string[] evidenceRefs,
        string[] sourceRefs,
        (string Id, string Type)[] relations)
        => new()
        {
            ItemId = id,
            ItemKind = "policy",
            SourceKind = "engineering-note",
            Layer = "context",
            Lifecycle = "Stable",
            ReviewStatus = "Approved",
            ReplacementState = "current",
            TargetSection = VectorQueryTargetSections.NormalContext,
            SourceRefs = sourceRefs,
            EvidenceRefs = evidenceRefs,
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = "prov-" + id,
                SourceFingerprint = "fingerprint-" + id,
                IngestionBatchId = "batch-input-metadata-test"
            },
            SourceFingerprint = "fingerprint-" + id,
            Relations = relations
                .Select(relation => new RetrievalDatasetV2Relation
                {
                    RelationId = relation.Id,
                    SourceItemId = id,
                    TargetItemId = id,
                    RelationType = relation.Type,
                    SourceRefs = sourceRefs,
                    EvidenceRefs = evidenceRefs
                })
                .ToArray(),
            Tags = tags,
            Anchors = anchors,
            Content = content,
            Split = "train"
        };

    private static RetrievalEvalProtocolGateReport BuildProtocolGate(bool passed)
        => new()
        {
            GatePassed = passed,
            Recommendation = passed
                ? RetrievalEvalProtocolRecommendations.NeedsInputMetadataEnrichment
                : RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch,
            Protocol = new RetrievalEvalProtocol(),
            RuntimeChangeGatePassed = true
        };

    private static LearningRuntimeChangeReadinessGateReport BuildRuntimeGate(bool passed)
        => new()
        {
            Passed = passed,
            Recommendation = passed ? "RuntimeChangeRulesSatisfied" : "KeepRuntimeDefaults",
            FailedConditions = passed ? Array.Empty<string>() : ["RuntimeChangeGateNotPassed"]
        };

    private static RuntimeObservableFeatureContractSourceScan BuildSourceScan(bool clean)
        => new()
        {
            ScanPerformed = true,
            ScannedFileCount = 1,
            FixtureTokenHitCount = clean ? 0 : 1,
            ScannedFiles = ["InputMetadataEnrichmentPreviewRunner.cs"],
            FlaggedFiles = clean ? Array.Empty<string>() : ["InputMetadataEnrichmentPreviewRunner.cs"],
            FlaggedTokens = clean ? Array.Empty<string>() : ["sample.SampleId =="]
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
