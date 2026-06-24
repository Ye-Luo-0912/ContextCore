using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreOutputTokenPriorityShadowGateTests
{
    [TestMethod]
    public void OutputTokenPriorityShadow_GatePassesWithStableShadowPackage()
    {
        var report = new OutputTokenPriorityShadowGateRunner().BuildGate(
            BuildDataset(),
            BuildSourceAwareGate(passed: true),
            BuildProtocolGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new OutputTokenPriorityShadowGateOptions
            {
                TotalTokenBudget = 512,
                PerPackageTokenBudget = 64,
                SectionTokenBudget = 256,
                RequireSourceScan = true,
                UseForRuntime = false,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                ReadyForRuntimeSwitch = false
            });

        Assert.IsTrue(report.ShadowPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(OutputTokenPriorityShadowGateRecommendations.ReadyForOutputPolicyShadowFreeze, report.Recommendation);
        Assert.AreEqual(SourceAwareRankingProfileIds.CombinedSafe, report.ProfileName);
        Assert.AreEqual(0, report.TokenBudgetExceededCount);
        Assert.AreEqual(0, report.PriorityInversionCount);
        Assert.AreEqual(0, report.DroppedRequiredCandidateCount);
        Assert.AreEqual(0, report.SectionMismatchCount);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void OutputTokenPriorityShadow_MissingV514GateBlocks()
    {
        var report = new OutputTokenPriorityShadowGateRunner().BuildGate(
            BuildDataset(),
            BuildSourceAwareGate(passed: false),
            BuildProtocolGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true));

        Assert.IsFalse(report.ShadowPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(OutputTokenPriorityShadowGateRecommendations.BlockedByMissingV514Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V514SourceAwareRankingGateNotPassed");
    }

    [TestMethod]
    public void OutputTokenPriorityShadow_TokenBudgetBlocks()
    {
        var report = new OutputTokenPriorityShadowGateRunner().BuildGate(
            BuildDataset(),
            BuildSourceAwareGate(passed: true),
            BuildProtocolGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new OutputTokenPriorityShadowGateOptions
            {
                TotalTokenBudget = 1,
                PerPackageTokenBudget = 0,
                SectionTokenBudget = 1
            });

        Assert.IsFalse(report.ShadowPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(OutputTokenPriorityShadowGateRecommendations.BlockedByTokenBudget, report.Recommendation);
        Assert.IsTrue(report.TokenBudgetExceededCount > 0);
    }

    [TestMethod]
    public void OutputTokenPriorityShadow_RuntimeMutationAttemptBlocks()
    {
        var report = new OutputTokenPriorityShadowGateRunner().BuildGate(
            BuildDataset(),
            BuildSourceAwareGate(passed: true),
            BuildProtocolGate(passed: true),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true),
            new OutputTokenPriorityShadowGateOptions
            {
                UseForRuntime = true
            });

        Assert.IsFalse(report.ShadowPassed);
        Assert.AreEqual(OutputTokenPriorityShadowGateRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeOrFormalMutationAttempt");
    }

    [TestMethod]
    public void OutputTokenPriorityShadow_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "OutputTokenPriorityShadowGateRunner.cs"));
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
            BuildItem("item-a", "generic operating note source-a evidence-a priority stable", "policy", "source-a", "src-a", "ev-a"),
            BuildItem("item-b", "generic operating note source-b evidence-b priority stable", "policy", "source-b", "src-b", "ev-b"),
            BuildItem("item-c", "generic operating note source-c evidence-c priority stable", "policy", "source-c", "src-c", "ev-c"),
            BuildItem("item-d", "generic operating note source-d evidence-d priority stable", "policy", "source-d", "src-d", "ev-d")
        };
        var samples = new[]
        {
            BuildSample("sample-a", "train", "generic operating note", "src-a", "ev-a", "item-a", "item-d"),
            BuildSample("sample-b", "dev", "generic operating note", "src-b", "ev-b", "item-b", "item-d")
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
                IngestionBatchId = "batch-token-priority-test"
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
            Anchors = [evidenceRef],
            Content = content,
            Split = "train"
        };

    private static RetrievalDatasetV2Sample BuildSample(
        string id,
        string split,
        string queryText,
        string sourceRef,
        string evidenceRef,
        string mustHit,
        string mustNot)
        => new()
        {
            SampleId = id,
            QueryText = queryText,
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
                IngestionBatchId = "batch-token-priority-test"
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false"
            }
        };

    private static SourceAwareRankingRepairReport BuildSourceAwareGate(bool passed)
        => new()
        {
            ReportPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? SourceAwareRankingRepairRecommendations.ReadyForSourceAwareRankingFreeze
                : SourceAwareRankingRepairRecommendations.KeepPreviewOnly,
            SelectedProfileId = SourceAwareRankingProfileIds.CombinedSafe,
            Protocol = new RetrievalEvalProtocol
            {
                VectorTopK = 4,
                MergedTopK = 8,
                FinalTopK = 3,
                ScoreThreshold = 0
            },
            RiskAfterPolicy = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
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
            },
            RuntimeChangeGatePassed = passed
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
            ScannedFiles = ["OutputTokenPriorityShadowGateRunner.cs"],
            FlaggedFiles = clean ? Array.Empty<string>() : ["OutputTokenPriorityShadowGateRunner.cs"],
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
