using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRuntimeRetrievalFeatureDerivationRepairTests
{
    [TestMethod]
    public void RuntimeFeatureDerivationRepair_CleanInputs_PreviewPasses()
    {
        var dataset = BuildSampleDataset();
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildPreview(BuildDerivationGate(passed: true), dataset, BuildSourceScan(clean: true), BuildOptions());

        Assert.IsTrue(report.PreviewPassed,
            $"preview should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRepairRecommendations.ReadyForRuntimeFeatureDerivationRepairFreeze,
            report.Recommendation);
        Assert.IsTrue(report.SampleCount > 0);
        Assert.IsTrue(report.TrainSampleCount > 0);
        Assert.IsTrue(report.TrainDerivedRecall > report.TrainBaselineRecall - 1e-9,
            $"trainDerivedRecall={report.TrainDerivedRecall} trainBaselineRecall={report.TrainBaselineRecall}");
        Assert.IsTrue(report.TrainDerivedMrr > report.TrainBaselineMrr - 1e-9);
        if (report.HoldoutSampleCount > 0)
        {
            Assert.IsTrue(report.HoldoutDerivedRecall >= report.HoldoutBaselineRecall - 1e-9);
            Assert.IsTrue(report.HoldoutDerivedMrr >= report.HoldoutBaselineMrr - 1e-9);
        }

        Assert.IsTrue(report.CanonicalRequiredRelationCoverageRate > 0,
            $"canonical relation coverage should be positive; actual={report.CanonicalRequiredRelationCoverageRate}");
        Assert.AreEqual(0, report.DerivedRiskAfterPolicy);
        Assert.AreEqual(0, report.DerivedMustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.DerivedLifecycleRiskAfterPolicy);
        Assert.AreEqual(0, report.DerivedSectionMismatchCount);
        Assert.AreEqual(0, report.ForbiddenSampleAnnotationReadCount);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.VectorProviderSource);
        Assert.AreEqual("read-only relation evidence / expansion preview", report.GraphCandidateSource);
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_GateMode_GatePassed()
    {
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildGate(BuildDerivationGate(passed: true), BuildSampleDataset(), BuildSourceScan(clean: true), BuildOptions());

        Assert.IsTrue(report.PreviewPassed);
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.OperationId.StartsWith("runtime-feature-derivation-repair-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_MissingDerivationGate_BlocksRun()
    {
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildPreview(derivationGate: null, BuildSampleDataset(), BuildSourceScan(clean: true), BuildOptions());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByMissingDerivationGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DerivationGateMissing");
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_DerivationGateNotPassed_BlocksRun()
    {
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildPreview(BuildDerivationGate(passed: false), BuildSampleDataset(), BuildSourceScan(clean: true), BuildOptions());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByDerivationGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DerivationGateNotPassed");
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_MissingDataset_BlocksRun()
    {
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildPreview(BuildDerivationGate(passed: true), new RetrievalDatasetV2GeneratedDataset(), BuildSourceScan(clean: true), BuildOptions());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByMissingDataset,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDataset");
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_RecallNotImproved_BlocksRun()
    {
        var saturatedDataset = BuildBaselineSaturatedDataset();
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildPreview(BuildDerivationGate(passed: true), saturatedDataset, BuildSourceScan(clean: true), BuildOptions());

        Assert.IsFalse(report.PreviewPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DerivedRecallNotImproved");
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_FixtureSpecialCasing_BlocksRun()
    {
        var dirtyScan = new RuntimeObservableFeatureContractSourceScan
        {
            ScanPerformed = true,
            ScannedFileCount = 9,
            FixtureTokenHitCount = 1,
            FlaggedFiles = new[] { "RuntimeRetrievalFeatureDerivationRepairRunner.cs" },
            FlaggedTokens = new[] { "sample.SampleId ==" }
        };
        var report = new RuntimeRetrievalFeatureDerivationRepairRunner()
            .BuildPreview(BuildDerivationGate(passed: true), BuildSampleDataset(), dirtyScan, BuildOptions());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByFixtureSpecialCasing,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FixtureSpecialCasingDetected");
    }

    [TestMethod]
    public void RuntimeFeatureDerivationRepair_HasNoKnownFixtureTerms()
    {
        var paths = new[]
        {
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "Evaluation", "V5", "RuntimeRetrievalFeatureDerivationRepairRunner.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "CanonicalRuntimeAnchorResolver.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "RuntimeRelationIntentDeriver.cs")
        };
        foreach (var path in paths)
        {
            var source = File.ReadAllText(Path.GetFullPath(path));
            foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
            {
                Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"{path} must not contain fixed eval content: {forbidden}");
            }

            Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal), $"{path} must not branch on sampleId literal");
            Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
        }

        // Repair runner: sample.MustHitItemIds must only appear inside EvaluateAgainstMustHit.
        var repairSource = File.ReadAllText(Path.GetFullPath(paths[0]));
        Assert.IsTrue(repairSource.Contains("EvaluateAgainstMustHit", StringComparison.Ordinal),
            "evaluation helper must be present and centralize sample.MustHitItemIds reads");
    }

    private static RuntimeRetrievalFeatureDerivationReport BuildDerivationGate(bool passed)
        => new()
        {
            PreviewPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? RuntimeRetrievalFeatureDerivationRecommendations.ReadyForRuntimeFeatureFreeze
                : RuntimeRetrievalFeatureDerivationRecommendations.KeepPreviewOnly,
            AllowedMode = "PreviewOnly",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            NoRuntimeMutationInvariant = true
        };

    private static RuntimeObservableFeatureContractSourceScan BuildSourceScan(bool clean)
        => new()
        {
            ScanPerformed = true,
            ScannedFileCount = 10,
            FixtureTokenHitCount = clean ? 0 : 1,
            ScannedFiles = new[]
            {
                "ShadowFormalRetrievalAdapterPlanRunner.cs",
                "ShadowFormalRetrievalAdapter.cs",
                "FormalAdapterPackageShadowComparisonRunner.cs",
                "GraphVectorRetrievalQualityAuditRunner.cs",
                "RetrievalQualityRepairPreviewRunner.cs",
                "RuntimeObservableRetrievalFeatureContractRunner.cs",
                "RuntimeRetrievalFeatureDerivationPreviewRunner.cs",
                "RuntimeRetrievalFeatureDerivationRepairRunner.cs",
                "CanonicalRuntimeAnchorResolver.cs",
                "RuntimeRelationIntentDeriver.cs"
            },
            FlaggedFiles = clean ? Array.Empty<string>() : new[] { "RuntimeRetrievalFeatureDerivationRepairRunner.cs" },
            FlaggedTokens = clean ? Array.Empty<string>() : new[] { "sample.SampleId ==" }
        };

    private static RuntimeRetrievalFeatureDerivationRepairOptions BuildOptions()
        => new()
        {
            DenseSeedTopK = 4,
            AnchorSeedTopK = 4,
            RelationTopK = 8,
            VectorTopK = 6,
            GraphTopK = 6,
            MergedTopK = 6,
            TopK = 2,                         // Tight window so baseline misses second must-hit.
            MinRelationCoverageRate = 0.0,    // Toy dataset; coverage threshold relaxed.
            HoldoutModulus = 1,               // All samples are train so the small dataset can drive the metric-improvement check.
            HoldoutRemainder = -1,            // No remainder matches → holdout split is empty.
            MaxSampleTraceCount = 5
        };

    private static RetrievalDatasetV2GeneratedDataset BuildSampleDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "r-noise-top",
                // Highest dense token overlap with the query; baseline ranks it #1.
                // Lives in Excluded so the derived risk gate filters it (envelope target = NormalContext).
                targetSection: VectorQueryTargetSections.Excluded,
                content: "active stable target query overlap content",
                tags: new[] { "active", "target" },
                anchors: new[] { "active", "target" }),
            BuildCorpusItem(
                itemId: "r-must-hit",
                content: "active stable repair target candidate referenced by the runtime query",
                tags: new[] { "target", "active" },
                anchors: new[] { "target" },
                evidenceRefs: new[] { "stress-ev-train-001" },
                relations: new[] { ("rel-shared", "rel-shared-target") }),
            BuildCorpusItem(
                itemId: "r-second-must-hit",
                content: "secondary stable item joined via shared relation evidence",
                tags: new[] { "secondary" },
                anchors: new[] { "secondary" },
                relations: new[] { ("rel-shared", "rel-shared-target") }),
            BuildCorpusItem(
                itemId: "r-noise-context",
                content: "active context unrelated to the question",
                tags: new[] { "noise", "context" },
                anchors: new[] { "context" }),
            BuildCorpusItem(
                itemId: "r-noise-rule",
                content: "stable rule that is unrelated",
                tags: new[] { "noise" },
                anchors: new[] { "rule" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-runtime-derivation-repair-1",
            QueryText = "active stable target query",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "r-must-hit", "r-second-must-hit" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "stress-sample-ev-train-001" },
            SourceRefs = Array.Empty<string>(),
            RequiredRelations = new[] { "rel-shared" },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = "ws-r",
                ["collectionId"] = "col-r"
            }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2GeneratedDataset BuildBaselineSaturatedDataset()
    {
        // Single must-hit item with overwhelming token overlap; baseline already
        // recalls at rank 1, leaving no room for derived improvement.
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "r-only-target",
                content: "only target candidate with overwhelming dense and lexical overlap",
                tags: new[] { "only", "target", "overwhelming" },
                anchors: new[] { "only", "target", "overwhelming" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-runtime-derivation-repair-saturated",
            QueryText = "only target overwhelming overlap",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "r-only-target" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = Array.Empty<string>(),
            SourceRefs = Array.Empty<string>(),
            RequiredRelations = Array.Empty<string>()
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2CorpusItem BuildCorpusItem(
        string itemId,
        string? targetSection = null,
        string? lifecycle = null,
        string? replacementState = null,
        string? content = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? anchors = null,
        IReadOnlyList<string>? evidenceRefs = null,
        IReadOnlyList<string>? sourceRefs = null,
        IReadOnlyList<(string RelationId, string TargetItemId)>? relations = null)
    {
        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = itemId,
            ItemKind = "note",
            SourceKind = "note",
            Layer = "Stable",
            Lifecycle = lifecycle ?? "Active",
            ReviewStatus = "Reviewed",
            ReplacementState = replacementState ?? "Current",
            TargetSection = targetSection ?? VectorQueryTargetSections.NormalContext,
            Content = content ?? string.Empty,
            Tags = tags ?? Array.Empty<string>(),
            Anchors = anchors ?? Array.Empty<string>(),
            EvidenceRefs = evidenceRefs ?? Array.Empty<string>(),
            SourceRefs = sourceRefs ?? Array.Empty<string>(),
            Relations = (relations ?? Array.Empty<(string, string)>())
                .Select(rel => new RetrievalDatasetV2Relation
                {
                    RelationId = rel.RelationId,
                    SourceItemId = itemId,
                    TargetItemId = rel.TargetItemId,
                    RelationType = "supports"
                })
                .ToArray()
        };
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(segments);}
}
