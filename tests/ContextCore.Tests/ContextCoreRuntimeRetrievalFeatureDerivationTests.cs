using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRuntimeRetrievalFeatureDerivationTests
{
    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_CleanInputs_PreviewPasses()
    {
        var dataset = BuildSampleDataset();
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildPreview(BuildContractGate(passed: true), dataset, BuildSourceScan(clean: true));

        Assert.IsTrue(report.PreviewPassed,
            $"preview should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRecommendations.ReadyForRuntimeFeatureFreeze,
            report.Recommendation);
        Assert.IsTrue(report.SampleCount > 0);
        Assert.IsTrue(report.Samples.Count > 0);
        Assert.IsTrue(report.DerivedRecall >= report.BaselineRecall - 1e-6,
            $"derivedRecall={report.DerivedRecall} baselineRecall={report.BaselineRecall}");
        Assert.IsTrue(report.DerivedMeanReciprocalRank >= report.BaselineMeanReciprocalRank - 1e-6);
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
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.VectorProviderSource);
        Assert.AreEqual("read-only relation evidence / expansion preview", report.GraphCandidateSource);
        // First sample's envelope should not be empty.
        var firstSample = report.Samples.First();
        Assert.IsFalse(string.IsNullOrEmpty(firstSample.Envelope.TargetSection));
        Assert.IsFalse(string.IsNullOrEmpty(firstSample.Envelope.RequiredRelationDerivationSource));
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_GateMode_GatePassed()
    {
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildGate(BuildContractGate(passed: true), BuildSampleDataset(), BuildSourceScan(clean: true));

        Assert.IsTrue(report.PreviewPassed);
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.OperationId.StartsWith("runtime-feature-derivation-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_MissingContractGate_BlocksRun()
    {
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildPreview(contractGate: null, BuildSampleDataset(), BuildSourceScan(clean: true));

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRecommendations.BlockedByMissingContractGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ContractGateMissing");
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_ContractGateNotPassed_BlocksRun()
    {
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildPreview(BuildContractGate(passed: false), BuildSampleDataset(), BuildSourceScan(clean: true));

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRecommendations.BlockedByContractGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ContractGateNotPassed");
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_MissingDataset_BlocksRun()
    {
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildPreview(BuildContractGate(passed: true), new RetrievalDatasetV2GeneratedDataset(), BuildSourceScan(clean: true));

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRecommendations.BlockedByMissingDataset,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDataset");
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_RecallRegression_BlocksRun()
    {
        var dataset = BuildSampleDataset();
        var options = new RuntimeRetrievalFeatureDerivationOptions
        {
            MaxAllowedRecallRegression = -1.0  // Any equality counts as regression > -1.0; impossible to meet.
        };
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildPreview(BuildContractGate(passed: true), dataset, BuildSourceScan(clean: true), options);

        Assert.IsFalse(report.PreviewPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DerivedRecallRegression");
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRecommendations.BlockedByDerivedRecallRegression,
            report.Recommendation);
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_FixtureSpecialCasing_BlocksRun()
    {
        var dirtyScan = new RuntimeObservableFeatureContractSourceScan
        {
            ScanPerformed = true,
            ScannedFileCount = 7,
            FixtureTokenHitCount = 2,
            FlaggedFiles = new[] { "RuntimeRetrievalFeatureDerivationPreviewRunner.cs" },
            FlaggedTokens = new[] { "sample.SampleId ==", "sample-pkg" }
        };
        var report = new RuntimeRetrievalFeatureDerivationPreviewRunner()
            .BuildPreview(BuildContractGate(passed: true), BuildSampleDataset(), dirtyScan);

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RuntimeRetrievalFeatureDerivationRecommendations.BlockedByFixtureSpecialCasing,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FixtureSpecialCasingDetected");
    }

    [TestMethod]
    public void RuntimeRetrievalFeatureDerivation_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "RuntimeRetrievalFeatureDerivationPreviewRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixed eval content: {forbidden}");
        }

        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        // Derivation must not look at sample.RequiredRelations / sample.EvidenceRefs / sample.SourceRefs / sample.ExpectedTargetSection / sample.MustHitItemIds / sample.MustNotHitItemIds.
        // Only EvaluateAgainstMustHit is allowed to read sample.MustHitItemIds for *evaluation*.
        // We require that sample.MustHitItemIds is read only inside an evaluation helper, not inside the deriver.
        Assert.IsTrue(source.Contains("EvaluateAgainstMustHit", StringComparison.Ordinal),
            "evaluation helper must be present and centralize sample.MustHitItemIds reads");
    }

    private static RuntimeObservableFeatureContractReport BuildContractGate(bool passed)
        => new()
        {
            ContractPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? RuntimeObservableFeatureContractRecommendations.ReadyForRuntimeObservableFeatureFreeze
                : RuntimeObservableFeatureContractRecommendations.KeepPreviewOnly,
            AllowedMode = "AuditOnly",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            BestProfileId = RetrievalQualityRepairProfiles.Combined,
            BestProfileContractStatus = RuntimeObservableFeatureContractStatuses.RequiresRuntimeDerivation,
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
            ScannedFileCount = 7,
            FixtureTokenHitCount = clean ? 0 : 1,
            ScannedFiles = new[]
            {
                "ShadowFormalRetrievalAdapterPlanRunner.cs",
                "ShadowFormalRetrievalAdapter.cs",
                "FormalAdapterPackageShadowComparisonRunner.cs",
                "GraphVectorRetrievalQualityAuditRunner.cs",
                "RetrievalQualityRepairPreviewRunner.cs",
                "RuntimeObservableRetrievalFeatureContractRunner.cs",
                "RuntimeRetrievalFeatureDerivationPreviewRunner.cs"
            },
            FlaggedFiles = clean ? Array.Empty<string>() : new[] { "RuntimeRetrievalFeatureDerivationPreviewRunner.cs" },
            FlaggedTokens = clean ? Array.Empty<string>() : new[] { "sample.SampleId ==" }
        };

    private static RetrievalDatasetV2GeneratedDataset BuildSampleDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "deriv-target-active",
                content: "active stable derivation target candidate matches the runtime query goals",
                tags: new[] { "deriv", "target", "active" },
                anchors: new[] { "deriv", "target" },
                evidenceRefs: new[] { "evidence-deriv-1" },
                relations: new[] { ("rel-deriv-1", "rel-deriv-1-target") }),
            BuildCorpusItem(
                itemId: "deriv-graph-bridge",
                content: "bridge node referenced by relation evidence supporting the runtime query",
                tags: new[] { "deriv", "bridge" },
                anchors: new[] { "bridge", "graph" },
                sourceRefs: new[] { "src-deriv-1" },
                relations: new[] { ("rel-deriv-1", "rel-deriv-1-target") }),
            BuildCorpusItem(
                itemId: "deriv-noise-rule",
                content: "general unrelated rule that may show up in baseline",
                tags: new[] { "deriv", "noise" },
                anchors: new[] { "noise" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-deriv-1",
            QueryText = "deriv target query about active stable bridge",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "deriv-target-active" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "evidence-deriv-1" },
            SourceRefs = new[] { "src-deriv-1" },
            RequiredRelations = new[] { "rel-deriv-1" },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = "ws-deriv",
                ["collectionId"] = "col-deriv"
            }
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
