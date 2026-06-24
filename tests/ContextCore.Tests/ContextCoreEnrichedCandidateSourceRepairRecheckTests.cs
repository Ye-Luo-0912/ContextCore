using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreEnrichedCandidateSourceRepairRecheckTests
{
    [TestMethod]
    public void EnrichedCandidateSourceRepairRecheck_RecordsQualityLiftButDoesNotOverPromote()
    {
        var report = new EnrichedCandidateSourceRepairRecheckRunner().BuildGate(
            BuildDerivationGate(passed: true),
            BuildEnrichmentGate(passed: true),
            BuildDataset(),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new EnrichedCandidateSourceRepairRecheckOptions
            {
                SourceRepairOptions = new QueryDrivenCandidateSourceRepairOptions
                {
                    TopK = 1,
                    RequireSourceScan = true,
                    UseForRuntime = false,
                    FormalRetrievalAllowed = false
                }
            });

        Assert.IsTrue(report.RecheckPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.QualityImproved);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(EnrichedCandidateSourceRepairRecheckRecommendations.NeedsMoreSourceRepair, report.Recommendation);
        Assert.AreEqual(0, report.RiskAfterPolicy);
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
    public void EnrichedCandidateSourceRepairRecheck_MissingV512GateBlocks()
    {
        var report = new EnrichedCandidateSourceRepairRecheckRunner().BuildGate(
            BuildDerivationGate(passed: true),
            BuildEnrichmentGate(passed: false),
            BuildDataset(),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true));

        Assert.IsFalse(report.RecheckPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByProtocolMismatch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V512InputMetadataEnrichmentGateNotPassed");
    }

    [TestMethod]
    public void EnrichedCandidateSourceRepairRecheck_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "EnrichedCandidateSourceRepairRecheckRunner.cs"));
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
                "item-policy",
                "general operational guidance",
                "policy",
                "manual",
                ["ev-policy"],
                ["src-policy"]),
            BuildItem(
                "item-runbook",
                "general operational guidance",
                "runbook",
                "playbook",
                ["ev-runbook"],
                ["src-runbook"])
        };
        var samples = new[]
        {
            new RetrievalDatasetV2Sample
            {
                SampleId = "sample-enrichment",
                QueryText = "kind-policy source-manual",
                Difficulty = "metadata_anchor",
                Split = "train",
                ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                MustHitItemIds = ["item-policy"],
                MustNotHitItemIds = ["item-runbook"]
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
        string itemKind,
        string sourceKind,
        string[] evidenceRefs,
        string[] sourceRefs)
        => new()
        {
            ItemId = id,
            ItemKind = itemKind,
            SourceKind = sourceKind,
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
                IngestionBatchId = "batch-v513-test"
            },
            SourceFingerprint = "fingerprint-" + id,
            Relations =
            [
                new RetrievalDatasetV2Relation
                {
                    RelationId = "rel-" + id,
                    SourceItemId = id,
                    TargetItemId = id,
                    RelationType = "supports",
                    SourceRefs = sourceRefs,
                    EvidenceRefs = evidenceRefs
                }
            ],
            Tags = [],
            Anchors = [],
            Content = content,
            Split = "train"
        };

    private static RuntimeRetrievalFeatureDerivationReport BuildDerivationGate(bool passed)
        => new()
        {
            PreviewPassed = passed,
            GatePassed = passed,
            Recommendation = passed ? "ReadyForFeatureDerivationRepair" : RuntimeRetrievalFeatureDerivationRecommendations.KeepPreviewOnly
        };

    private static InputMetadataEnrichmentPreviewReport BuildEnrichmentGate(bool passed)
        => new()
        {
            PreviewPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? InputMetadataEnrichmentPreviewRecommendations.ReadyForSourceRepairRecheck
                : InputMetadataEnrichmentPreviewRecommendations.BlockedByProtocolMismatch,
            MetadataCoverageDelta = passed ? 10 : 0,
            IndependentNonDenseSourceCount = passed ? 1 : 0
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
            ScannedFiles = ["EnrichedCandidateSourceRepairRecheckRunner.cs"],
            FlaggedFiles = clean ? Array.Empty<string>() : ["EnrichedCandidateSourceRepairRecheckRunner.cs"],
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
