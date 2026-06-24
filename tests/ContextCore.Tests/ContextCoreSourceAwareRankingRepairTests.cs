using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreSourceAwareRankingRepairTests
{
    [TestMethod]
    public void SourceAwareRankingRepair_GatePassesWithRuntimeObservableMetadata()
    {
        var report = new SourceAwareRankingRepairRunner().BuildGate(
            BuildDataset(),
            BuildProtocolGate(passed: true),
            BuildEnrichmentGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new SourceAwareRankingRepairOptions
            {
                BlindHoldoutSampleCount = 4,
                RequireSourceScan = true,
                UseForRuntime = false,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                ReadyForRuntimeSwitch = false
            });

        Assert.IsTrue(report.ReportPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(SourceAwareRankingRepairRecommendations.ReadyForSourceAwareRankingFreeze, report.Recommendation);
        Assert.IsTrue(report.TrainDevRecallDelta > 0);
        Assert.IsTrue(report.HoldoutRecallDelta >= 0);
        Assert.IsTrue(report.BlindHoldoutRecallDelta >= 0);
        Assert.AreEqual(0, report.DenseWinnerLostCount);
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
    public void SourceAwareRankingRepair_MissingProtocolGateBlocks()
    {
        var report = new SourceAwareRankingRepairRunner().BuildGate(
            BuildDataset(),
            BuildProtocolGate(passed: false),
            BuildEnrichmentGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new SourceAwareRankingRepairOptions { BlindHoldoutSampleCount = 4 });

        Assert.IsFalse(report.ReportPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(SourceAwareRankingRepairRecommendations.BlockedByProtocolMismatch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V511ProtocolGateNotPassed");
    }

    [TestMethod]
    public void SourceAwareRankingRepair_RuntimeSwitchAttemptBlocks()
    {
        var report = new SourceAwareRankingRepairRunner().BuildGate(
            BuildDataset(),
            BuildProtocolGate(passed: true),
            BuildEnrichmentGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new SourceAwareRankingRepairOptions
            {
                BlindHoldoutSampleCount = 4,
                UseForRuntime = true
            });

        Assert.IsFalse(report.ReportPassed);
        Assert.AreEqual(SourceAwareRankingRepairRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAttempt");
    }

    [TestMethod]
    public void SourceAwareRankingRepair_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "SourceAwareRankingRepairRunner.cs"));
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
            BuildItem("item-a", "generic operational context", "policy", "source-a", "src-a", "ev-a"),
            BuildItem("item-b", "generic operational context", "policy", "source-b", "src-b", "ev-b"),
            BuildItem("item-c", "generic operational context", "policy", "source-c", "src-c", "ev-c"),
            BuildItem("item-d", "generic operational context", "policy", "source-d", "src-d", "ev-d"),
            BuildItem("item-e", "generic operational context", "policy", "source-e", "src-e", "ev-e"),
            BuildItem("item-f", "generic operational context", "policy", "source-f", "src-f", "ev-f"),
            BuildItem("item-g", "generic operational context", "policy", "source-g", "src-g", "ev-g"),
            BuildItem("item-h", "generic operational context", "policy", "source-h", "src-h", "ev-h")
        };
        var samples = new[]
        {
            BuildSample("sample-train", "train", "src-d", "ev-d", "item-d", "item-h"),
            BuildSample("sample-dev", "dev", "src-e", "ev-e", "item-e", "item-h"),
            BuildSample("sample-test", "test", "src-f", "ev-f", "item-f", "item-h"),
            BuildSample("sample-holdout", "holdout", "src-g", "ev-g", "item-g", "item-h")
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
        string sourceRef,
        string evidenceRef)
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
            SourceRefs = [sourceRef],
            EvidenceRefs = [evidenceRef],
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = "prov-" + sourceRef,
                SourceFingerprint = "fingerprint-" + sourceRef,
                IngestionBatchId = "batch-source-aware-test"
            },
            SourceFingerprint = "fingerprint-" + sourceRef,
            Relations =
            [
                new RetrievalDatasetV2Relation
                {
                    RelationId = "rel-" + sourceRef,
                    SourceItemId = id,
                    TargetItemId = id,
                    RelationType = "supports",
                    SourceRefs = [sourceRef],
                    EvidenceRefs = [evidenceRef]
                }
            ],
            Tags = [sourceKind],
            Anchors = [],
            Content = content,
            Split = "train"
        };

    private static RetrievalDatasetV2Sample BuildSample(
        string id,
        string split,
        string sourceRef,
        string evidenceRef,
        string mustHit,
        string mustNot)
        => new()
        {
            SampleId = id,
            QueryText = "generic operational context",
            Difficulty = "metadata_anchor",
            Split = split,
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = [mustHit],
            MustNotHitItemIds = [mustNot],
            SourceRefs = [sourceRef],
            EvidenceRefs = [evidenceRef],
            RequiredRelations = ["rel-" + sourceRef],
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = "sample-prov-" + sourceRef,
                SourceFingerprint = "sample-fingerprint-" + sourceRef,
                IngestionBatchId = "batch-source-aware-test"
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false"
            }
        };

    private static RetrievalEvalProtocolGateReport BuildProtocolGate(bool passed)
        => new()
        {
            GatePassed = passed,
            Recommendation = passed
                ? RetrievalEvalProtocolRecommendations.NeedsInputMetadataEnrichment
                : RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch,
            Protocol = new RetrievalEvalProtocol
            {
                VectorTopK = 4,
                MergedTopK = 8,
                FinalTopK = 3,
                ScoreThreshold = 0
            }
        };

    private static InputMetadataEnrichmentPreviewReport BuildEnrichmentGate(bool passed)
        => new()
        {
            PreviewPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? InputMetadataEnrichmentPreviewRecommendations.ReadyForSourceRepairRecheck
                : InputMetadataEnrichmentPreviewRecommendations.BlockedByProtocolMismatch,
            MetadataCoverageDelta = passed ? 8 : 0,
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
            ScannedFiles = ["SourceAwareRankingRepairRunner.cs"],
            FlaggedFiles = clean ? Array.Empty<string>() : ["SourceAwareRankingRepairRunner.cs"],
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
