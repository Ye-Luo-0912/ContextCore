using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Models;
using ContextCore.Evaluation.Services;
using System.Text.Json;
using ContextCore.Evaluation.Learning;
using ContextCore.Evaluation.Vector.Dataset;
using ContextCore.Evaluation.Vector;
using ContextCore.Evaluation.Vector.Gates;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Retrieval")]
public class ContextCoreRetrievalDatasetV2MetadataContractTests
{
    [TestMethod]
    public void RetrievalDatasetV2Contract_DeclaresRequiredMetadataAndNoRuntimeUse()
    {
        var report = new RetrievalDatasetV2MetadataContractRunner().BuildContractReport();

        CollectionAssert.Contains(report.CorpusItemRequiredFields.ToList(), "sourceRefs");
        CollectionAssert.Contains(report.CorpusItemRequiredFields.ToList(), "evidenceRefs");
        CollectionAssert.Contains(report.CorpusItemRequiredFields.ToList(), "provenance.recordId");
        CollectionAssert.Contains(report.CorpusItemRequiredFields.ToList(), "targetSection");
        CollectionAssert.Contains(report.CorpusItemRequiredFields.ToList(), "split");
        CollectionAssert.Contains(report.QuerySampleRequiredFields.ToList(), "sourceRefs");
        CollectionAssert.Contains(report.QuerySampleRequiredFields.ToList(), "evidenceRefs");
        CollectionAssert.Contains(report.QuerySampleRequiredFields.ToList(), "provenance.recordId");
        Assert.IsFalse(report.GeneratesFormalDataset);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void RetrievalDatasetV2Validator_MissingRefsAndProvenance_AreRecognized()
    {
        var report = BuildReport(
            [Source("item-a", metadata: new Dictionary<string, string>())],
            [Sample("sample-a", "neutral query", ["item-a"], metadata: new Dictionary<string, string>())],
            []);

        Assert.IsTrue(report.MissingSourceRefsCount > 0);
        Assert.IsTrue(report.MissingEvidenceRefsCount > 0);
        Assert.IsTrue(report.MissingProvenanceCount > 0);
        Assert.AreEqual(RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill, report.Recommendation);
        Assert.IsFalse(report.GeneratesFormalDataset);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void RetrievalDatasetV2Validator_QueryItemIdLeakAndOverlap_AreRecognized()
    {
        var report = BuildReport(
            [Source("item-a")],
            [Sample("sample-a", "please retrieve item-a", ["item-a"], mustNot: ["item-a"])],
            []);

        Assert.AreEqual(1, report.QueryItemIdLeakCount);
        Assert.AreEqual(1, report.MustHitMustNotOverlapCount);
        Assert.AreEqual(RetrievalDatasetV2ValidationRecommendations.NeedsQueryLabelHygiene, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Validator_RelationEvidenceMissing_IsRecognized()
    {
        var report = BuildReport(
            [Source("item-a"), Source("item-b")],
            [Sample("sample-a", "neutral query", ["item-a"])],
            [
                new ContextRelation
                {
                    Id = "rel-a",
                    SourceId = "item-a",
                    TargetId = "item-b",
                    RelationType = "supersedes"
                }
            ]);

        Assert.AreEqual(2, report.RelationEvidenceMissingCount);
        Assert.AreEqual(RetrievalDatasetV2ValidationRecommendations.NeedsRelationEvidenceBackfill, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2LegacyLimitation_UsesEvidenceBackfillNeedsEvidence()
    {
        var report = new RetrievalDatasetV2MetadataContractRunner().BuildLegacyLimitationReport(
            new VectorLifecycleMetadataEvidenceBackfillReport
            {
                BatchId = "batch-a",
                CandidateCount = 32,
                NeedsEvidenceCount = 32,
                Recommendation = "NeedsIngestionMetadataBackfill"
            },
            null);

        Assert.AreEqual(32, report.ReviewCandidateCount);
        Assert.AreEqual(32, report.MissingEvidenceSourceProvenanceCandidateCount);
        Assert.IsFalse(report.LegacyDatasetSuitableForPrimaryRecallRepair);
        Assert.IsFalse(report.GeneratesFormalDataset);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Runner_NoFixtureDomainLexiconInProductionRunner()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Evaluation",
            "Vector",
            "Evaluation",
            "Dataset",
            "RetrievalDatasetV2MetadataContractRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixture/domain keyword: {forbidden}");
        }
    }

    [TestMethod]
    public void RetrievalDatasetV2Generator_DisabledByDefault()
    {
        var dataset = new RetrievalDatasetV2Generator().Generate(new RetrievalDatasetV2GenerationOptions());

        Assert.AreEqual(0, dataset.CorpusItems.Count);
        Assert.AreEqual(0, dataset.Samples.Count);
    }

    [TestMethod]
    public void RetrievalDatasetV2Generator_ProducesContractValidPreviewDataset()
    {
        var generator = new RetrievalDatasetV2Generator();
        var options = new RetrievalDatasetV2GenerationOptions
        {
            Enabled = true,
            TargetCorpusItemCount = 28,
            TargetSampleCount = 21,
            DryRun = true,
            UseForRuntime = false
        };

        var dataset = generator.Generate(options);
        var validation = generator.Validate(dataset);
        var quality = generator.BuildQualityReport(dataset, validation, generator.Judge(dataset));

        Assert.AreEqual(28, dataset.CorpusItems.Count);
        Assert.AreEqual(21, dataset.Samples.Count);
        Assert.AreEqual(0, validation.IssueCount);
        Assert.AreEqual(RetrievalDatasetV2GenerationRecommendations.ReadyForDatasetV2ShadowEval, quality.Recommendation);
        Assert.IsFalse(quality.FormalRetrievalAllowed);
        Assert.IsFalse(quality.UseForRuntime);
    }

    [TestMethod]
    public void RetrievalDatasetV2Generator_SameOptionsProduceStableDatasetContent()
    {
        var generator = new RetrievalDatasetV2Generator();
        var options = new RetrievalDatasetV2GenerationOptions
        {
            Enabled = true,
            TargetCorpusItemCount = 28,
            TargetSampleCount = 21,
            Seed = 1701,
            DryRun = false,
            UseForRuntime = false
        };

        var first = generator.Generate(options);
        var second = generator.Generate(options);

        Assert.AreEqual(SerializeJsonLines(first.CorpusItems), SerializeJsonLines(second.CorpusItems));
        Assert.AreEqual(SerializeJsonLines(first.Samples), SerializeJsonLines(second.Samples));
    }

    [TestMethod]
    public void RetrievalDatasetV2Materialization_DryRunDoesNotWriteDatasetArtifacts()
    {
        var directory = CreateTempDirectory();
        try
        {
            var corpusPath = Path.Combine(directory, "corpus.jsonl");
            var samplesPath = Path.Combine(directory, "samples.jsonl");
            var generator = new RetrievalDatasetV2Generator();
            var options = new RetrievalDatasetV2GenerationOptions
            {
                Enabled = true,
                TargetCorpusItemCount = 28,
                TargetSampleCount = 21,
                DryRun = true,
                UseForRuntime = false
            };

            _ = generator.Generate(options);

            Assert.IsFalse(File.Exists(corpusPath));
            Assert.IsFalse(File.Exists(samplesPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RetrievalDatasetV2Materialization_ConfirmWritesDatasetArtifactsAndStableManifest()
    {
        var directory = CreateTempDirectory();
        try
        {
            var corpusPath = Path.Combine(directory, "corpus.jsonl");
            var samplesPath = Path.Combine(directory, "samples.jsonl");
            var generator = new RetrievalDatasetV2Generator();
            var dataset = generator.Generate(new RetrievalDatasetV2GenerationOptions
            {
                Enabled = true,
                TargetCorpusItemCount = 28,
                TargetSampleCount = 21,
                DryRun = false,
                UseForRuntime = false
            });
            WriteJsonLines(corpusPath, dataset.CorpusItems);
            WriteJsonLines(samplesPath, dataset.Samples);

            var runner = new RetrievalDatasetV2MaterializationRunner();
            var corpusHash = RetrievalDatasetV2MaterializationRunner.ComputeFileHash(corpusPath);
            var samplesHash = RetrievalDatasetV2MaterializationRunner.ComputeFileHash(samplesPath);
            var manifest = runner.BuildManifest(corpusPath, samplesPath, dataset.CorpusItems.Count, dataset.Samples.Count, corpusHash, samplesHash);
            var validation = generator.Validate(dataset);
            var quality = generator.BuildQualityReport(dataset, validation, generator.Judge(dataset));
            var report = runner.BuildReport(manifest, validation, quality, manifest, corpusExists: true, samplesExists: true, requireExistingManifest: true);

            Assert.IsTrue(File.Exists(corpusPath));
            Assert.IsTrue(File.Exists(samplesPath));
            Assert.AreEqual(corpusHash, manifest.CorpusHash);
            Assert.AreEqual(samplesHash, manifest.SamplesHash);
            Assert.IsTrue(report.GatePassed);
            Assert.IsTrue(report.CorpusHashStable);
            Assert.IsTrue(report.SamplesHashStable);
            Assert.IsFalse(report.UseForRuntime);
            Assert.IsFalse(report.FormalRetrievalAllowed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RetrievalDatasetV2Materialization_MissingCorpusBlocksGate()
    {
        var directory = CreateTempDirectory();
        try
        {
            var corpusPath = Path.Combine(directory, "corpus.jsonl");
            var samplesPath = Path.Combine(directory, "samples.jsonl");
            WriteJsonLines(samplesPath, Array.Empty<RetrievalDatasetV2Sample>());

            var runner = new RetrievalDatasetV2MaterializationRunner();
            var manifest = runner.BuildManifest(corpusPath, samplesPath, 0, 0, string.Empty, RetrievalDatasetV2MaterializationRunner.ComputeFileHash(samplesPath));
            var report = runner.BuildReport(
                manifest,
                validation: null,
                quality: null,
                existingManifest: null,
                corpusExists: false,
                samplesExists: true,
                requireExistingManifest: true);

            Assert.IsFalse(report.GatePassed);
            Assert.AreEqual(RetrievalDatasetV2MaterializationRecommendations.BlockedByMissingArtifact, report.Recommendation);
            CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingMaterializedDatasetArtifact");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RetrievalDatasetV2ShadowEval_CleanMaterializedDataset_IsReadyCandidate()
    {
        var generator = new RetrievalDatasetV2Generator();
        var dataset = generator.Generate(new RetrievalDatasetV2GenerationOptions
        {
            Enabled = true,
            TargetCorpusItemCount = 28,
            TargetSampleCount = 21,
            Seed = 1701,
            UseForRuntime = false
        });
        var validation = generator.Validate(dataset);
        var quality = generator.BuildQualityReport(dataset, validation, generator.Judge(dataset));
        var materialization = new RetrievalDatasetV2MaterializationRunner();
        var manifest = materialization.BuildManifest("corpus.jsonl", "samples.jsonl", 28, 21, "corpus-hash", "samples-hash");
        var gate = materialization.BuildReport(manifest, validation, quality, manifest, corpusExists: true, samplesExists: true, requireExistingManifest: true);
        var runner = new RetrievalDatasetV2ShadowEvalRunner();

        var profiles = runner.RunDense(dataset, manifest, gate)
            .Concat(runner.RunHybrid(dataset, manifest, gate))
            .ToArray();
        var summary = runner.BuildSummary(profiles);
        var readiness = runner.BuildReadinessGate(gate, summary);

        Assert.IsTrue(readiness.GatePassed);
        Assert.AreEqual(RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate, readiness.Recommendation);
        Assert.IsTrue(summary.PgVectorParityPassed);
        Assert.AreEqual(0, readiness.RiskAfterPolicy);
        Assert.IsFalse(readiness.UseForRuntime);
        Assert.IsFalse(readiness.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void RetrievalDatasetV2ShadowEval_MissingMaterializedCorpus_BlocksEval()
    {
        var runner = new RetrievalDatasetV2ShadowEvalRunner();
        var profiles = runner.RunDense(new RetrievalDatasetV2GeneratedDataset(), manifest: null, materializationGate: null);

        Assert.IsTrue(profiles.All(static profile => profile.Recommendation == RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation));
    }

    [TestMethod]
    public void RetrievalDatasetV2Readiness_HashMismatchBlocksReadiness()
    {
        var materialization = new RetrievalDatasetV2MaterializationRunner();
        var original = materialization.BuildManifest("corpus.jsonl", "samples.jsonl", 1, 1, "old-corpus", "samples");
        var changed = materialization.BuildManifest("corpus.jsonl", "samples.jsonl", 1, 1, "new-corpus", "samples");
        var gate = materialization.BuildReport(
            changed,
            validation: null,
            quality: null,
            existingManifest: original,
            corpusExists: true,
            samplesExists: true,
            requireExistingManifest: true);
        var runner = new RetrievalDatasetV2ShadowEvalRunner();
        var readiness = runner.BuildReadinessGate(gate, null);

        Assert.IsFalse(readiness.GatePassed);
        CollectionAssert.Contains(readiness.BlockedReasons.ToArray(), "MaterializationGateNotPassed");
        Assert.AreEqual(RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation, readiness.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Readiness_PgVectorParityMismatchBlocks()
    {
        var runner = new RetrievalDatasetV2ShadowEvalRunner();
        var summary = Summary(Profile(recall: 1, risk: 0), pgVectorParityPassed: false);
        var readiness = runner.BuildReadinessGate(CleanMaterializationGate(), summary);

        Assert.IsFalse(readiness.GatePassed);
        CollectionAssert.Contains(readiness.BlockedReasons.ToArray(), "PgVectorParityMismatch");
        Assert.AreEqual(RetrievalDatasetV2ShadowEvalRecommendations.BlockedByPgVectorParityMismatch, readiness.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Readiness_RiskBlocks()
    {
        var runner = new RetrievalDatasetV2ShadowEvalRunner();
        var summary = Summary(Profile(recall: 1, risk: 1), pgVectorParityPassed: true);
        var readiness = runner.BuildReadinessGate(CleanMaterializationGate(), summary);

        Assert.IsFalse(readiness.GatePassed);
        CollectionAssert.Contains(readiness.BlockedReasons.ToArray(), "RiskAfterPolicyNonZero");
        Assert.AreEqual(RetrievalDatasetV2ShadowEvalRecommendations.BlockedByRisk, readiness.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Readiness_FormalOutputChangedBlocks()
    {
        var runner = new RetrievalDatasetV2ShadowEvalRunner();
        var summary = Summary(Profile(recall: 1, risk: 0, formalOutputChanged: 1), pgVectorParityPassed: true);
        var readiness = runner.BuildReadinessGate(CleanMaterializationGate(), summary);

        Assert.IsFalse(readiness.GatePassed);
        CollectionAssert.Contains(readiness.BlockedReasons.ToArray(), "FormalOutputChangedNonZero");
        Assert.AreEqual(RetrievalDatasetV2ShadowEvalRecommendations.BlockedByFormalOutputChange, readiness.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Validator_SplitLeakage_IsRecognized()
    {
        var trainItem = Source("item-a", metadata: ValidMetadata("Stable", VectorQueryTargetSections.NormalContext, "source-a", "evidence-a", "provenance-a"));
        trainItem.Metadata["split"] = "train";
        var testSample = Sample("sample-a", "neutral query", ["item-a"]);
        testSample.Metadata["split"] = "test";

        var report = BuildReport([trainItem], [testSample], []);

        Assert.IsTrue(report.SplitIsolationViolationCount > 0);
        Assert.AreEqual(RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2Generator_NoFixtureDomainLexiconInProductionRunner()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Evaluation",
            "Vector",
            "Evaluation",
            "Dataset",
            "RetrievalDatasetV2Generator.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Generator must not contain fixture/domain keyword: {forbidden}");
        }
    }

    [TestMethod]
    public void HybridUnionScoringRepair_DefaultOptionsRemainPreviewOnly()
    {
        var options = new HybridUnionScoringRepairOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsTrue(options.DensePreservationEnabled);
        Assert.IsTrue(options.DenseWinnerFloorEnabled);
        Assert.IsTrue(options.NegativeDistractorPenaltyEnabled);
        Assert.IsTrue(options.AnchorScoreCapEnabled);
        Assert.IsTrue(options.ContributionAwareRerankEnabled);
        Assert.IsFalse(options.UseForRuntime);
    }

    [TestMethod]
    public void HybridUnionScoringRepair_DenseWinnerFloorPreservesDenseHits()
    {
        var wrong = Enumerable.Range(0, 4)
            .Select(index => StressCorpus($"wrong-{index}", "core signal", anchors: ["boost"]))
            .Append(StressCorpus("wrong-low-dense", "boost", anchors: ["boost"]))
            .ToArray();
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = wrong.Append(StressCorpus("must", "core signal extra-one extra-two extra-three")).ToArray(),
            Samples = [StressSample("sample-a", "core signal boost", ["must"])]
        };

        var report = new HybridUnionScoringRepairRunner().BuildPreview(dataset);
        var denseFloor = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.DenseWinnerFloorV1);
        var combined = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.CombinedSafeV1);

        Assert.AreEqual(0, denseFloor.DenseWinnerLostCount);
        Assert.AreEqual(0, combined.DenseWinnerLostCount);
        Assert.IsTrue(denseFloor.RecallDeltaVsDense >= 0);
        Assert.IsTrue(combined.RecallDeltaVsDense >= 0);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void HybridUnionScoringRepair_NegativeDistractorPenaltyDoesNotIncreaseMustNotRisk()
    {
        var corpus = Enumerable.Range(0, 6)
            .Select(index => StressCorpus($"safe-{index}", $"alpha guidance safe filler-{index}"))
            .Append(StressCorpus("bad", "alpha guidance noisy", tags: ["noisy"]))
            .Append(StressCorpus("must", "alpha guidance safe authoritative"))
            .ToArray();
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = [StressSample("sample-a", "alpha guidance avoid noisy", ["must"], mustNot: ["bad"])]
        };

        var report = new HybridUnionScoringRepairRunner().BuildPreview(dataset);
        var baseline = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.BaselineHybridFull);
        var negativePenalty = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1);

        Assert.IsTrue(negativePenalty.NegativeDistractorOutranksMustHitCount <= baseline.NegativeDistractorOutranksMustHitCount);
        Assert.IsTrue(negativePenalty.MustNotHitRiskAfterPolicy <= baseline.MustNotHitRiskAfterPolicy);
    }

    [TestMethod]
    public void HybridUnionScoringRepair_AnchorScoreCapDoesNotIncreaseAnchorRegression()
    {
        var wrong = Enumerable.Range(0, 6)
            .Select(index => StressCorpus($"wrong-{index}", "common alpha beta gamma delta", tags: ["common"]))
            .ToArray();
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = wrong.Append(StressCorpus("must", "minor", tags: ["anchorx"], anchors: ["anchorx"])).ToArray(),
            Samples = [StressSample("sample-a", "anchorx common alpha beta gamma delta", ["must"])]
        };

        var report = new HybridUnionScoringRepairRunner().BuildPreview(dataset);
        var baseline = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.BaselineHybridFull);
        var capped = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.AnchorScoreCappedV1);

        Assert.IsTrue(capped.AnchorRankingRegressionCount <= baseline.AnchorRankingRegressionCount);
    }

    [TestMethod]
    public void HybridUnionScoringRepair_UngatedRiskRemainsVisibleAndRuntimeDisabled()
    {
        var corpus = Enumerable.Range(0, 2)
            .Select(index => StressCorpus($"safe-{index}", $"alpha guidance safe filler-{index}"))
            .Append(StressCorpus("bad", "alpha guidance noisy", tags: ["noisy"]))
            .Append(StressCorpus("must", "alpha guidance safe authoritative"))
            .ToArray();
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = [StressSample("sample-a", "alpha guidance", ["must"], mustNot: ["bad"])]
        };

        var report = new HybridUnionScoringRepairRunner().BuildPreview(dataset);
        var ungated = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1);

        Assert.IsTrue(ungated.MustNotHitRiskAfterPolicy > 0);
        Assert.AreEqual(HybridUnionScoringRepairRecommendations.BlockedByRisk, ungated.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void HybridUnionScoringRepair_PostScoringRiskGateRemovesMustNotCandidate()
    {
        var dataset = RiskTriageDataset();

        var report = new HybridUnionScoringRepairRunner().BuildPreview(dataset);
        var gated = report.Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1);

        Assert.AreEqual(0, gated.RiskAfterPolicy);
        Assert.AreEqual(0, gated.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, gated.DenseWinnerLostCount);
        Assert.AreEqual(HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze, gated.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void HybridUnionScoringRepair_ScoringPathDoesNotReadEvalLabelsOrFixtureLexicon()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Evaluation",
            "Vector",
            "Evaluation",
            "V5",
            "HybridUnionScoringRepairRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));
        var start = source.IndexOf("private static double ScoreForProfile", StringComparison.Ordinal);
        var end = source.IndexOf("private static double ApplyDenseFloor", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        Assert.IsTrue(end > start);
        var scoringSource = source[start..end];

        foreach (var forbidden in new[]
        {
            "MustHitItemIds",
            "MustNotHitItemIds",
            "NegativeDistractorIds",
            "RequiredRelations",
            "SampleId"
        })
        {
            Assert.IsFalse(scoringSource.Contains(forbidden, StringComparison.Ordinal), $"Scoring path must not read eval label: {forbidden}");
        }

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixture/domain keyword: {forbidden}");
        }
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksDatasetV2StressDirectRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.DatasetV2Stress,
                    CurrentPhase = "V3.24",
                    Status = RetrievalDatasetV2StressFreezeStatuses.ReadyForV4RecheckInput,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        ShadowRuntimeModes.PreviewOnly,
                        "PostScoringRiskGatedV1:Runtime"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "ReadyForFormalRetrieval",
                        "FormalIVectorIndexStoreBinding",
                        "PackingPolicyIntegration",
                        "PackageOutputIntegration"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.DatasetV2Stress}:PostScoringRiskGatedProfileRuntimeUseForbidden");
    }
    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksVectorV4RuntimeSwitch()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.VectorV4ReadinessRecheck,
                    CurrentPhase = "V4.R",
                    Status = VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "GuardedFormalPreviewOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding",
                        "PackingPolicyIntegration",
                        "PackageOutputIntegration"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.VectorV4ReadinessRecheck}:VectorV4RecheckDoesNotAllowRuntimeSwitch");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksGuardedFormalPreviewRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.GuardedFormalRetrievalPreview,
                    CurrentPhase = "V4.1",
                    Status = GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "ShadowPackageComparisonOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.GuardedFormalRetrievalPreview}:GuardedFormalPreviewDoesNotAllowRuntimeSwitch");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.GuardedFormalRetrievalPreview}:GuardedFormalPreviewPackageMutationForbidden");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksVectorShadowPackageRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.VectorShadowPackageComparison,
                    CurrentPhase = "V4.2",
                    Status = VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "ScopedFormalPreviewOptInOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.VectorShadowPackageComparison}:VectorShadowPackageComparisonDoesNotAllowRuntimeSwitch");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.VectorShadowPackageComparison}:VectorShadowPackageComparisonPackageMutationForbidden");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksScopedFormalPreviewRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.ScopedFormalPreviewOptIn,
                    CurrentPhase = "V4.3",
                    Status = ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "LimitedFormalPreviewObservationOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.ScopedFormalPreviewOptIn}:ScopedFormalPreviewOptInDoesNotAllowRuntimeSwitch");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.ScopedFormalPreviewOptIn}:ScopedFormalPreviewOptInPackageMutationForbidden");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksLimitedFormalPreviewObservationRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.LimitedFormalPreviewObservation,
                    CurrentPhase = "V4.4",
                    Status = LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "FormalPreviewFreezeOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.LimitedFormalPreviewObservation}:LimitedFormalPreviewObservationDoesNotAllowRuntimeSwitch");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.LimitedFormalPreviewObservation}:LimitedFormalPreviewObservationPackageMutationForbidden");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksVectorFormalPreviewFreezeRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.VectorFormalPreviewFreeze,
                    CurrentPhase = "V4.F",
                    Status = VectorFormalPreviewFreezeStatuses.ReadyForScopedOptInPreview,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "ScopedPreviewOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.VectorFormalPreviewFreeze}:VectorFormalPreviewFreezeDoesNotAllowRuntimeSwitch");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.VectorFormalPreviewFreeze}:VectorFormalPreviewFreezePackageMutationForbidden");
    }

    [TestMethod]
    public void FoundationFreeze_CleanReportsPassesReleaseCandidateGate()
    {
        var report = BuildFoundationFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.ReadyForReleaseCandidate, report.Recommendation);
        Assert.AreEqual("Frozen", report.ContextCoreFoundation);
        Assert.AreEqual("Frozen", report.StorageFoundation);
        Assert.AreEqual("ReadyForScopedFormalPreview", report.VectorFoundation);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void FoundationFreeze_MissingRelationFreezeBlocks()
    {
        var report = BuildFoundationFreezeReport(
            relation: null,
            includeRelation: false,
            reportCoverage: CleanFoundationCoverage(reportMissing: "storage/postgres/postgres-relation-multi-normal-scope-quality-report.json"));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.BlockedByMissingReport, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RelationGovernancePostgresFreezeNotPassed");
        Assert.AreEqual(1, report.MissingReportCount);
    }

    [TestMethod]
    public void FoundationFreeze_MissingVectorFormalPreviewFreezeBlocks()
    {
        var report = BuildFoundationFreezeReport(
            vectorFormal: null,
            includeVectorFormal: false,
            reportCoverage: CleanFoundationCoverage(reportMissing: "vector/v4/vector-formal-preview-freeze-gate.json"));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.BlockedByMissingReport, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorFormalPreviewFreezeNotPassed");
    }

    [TestMethod]
    public void FoundationFreeze_RuntimeSwitchAllowedBlocks()
    {
        var report = BuildFoundationFreezeReport(
            vectorFormal: CleanVectorFormalPreviewFreezeReport(readyForRuntimeSwitch: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.BlockedByRuntimeSwitch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAllowed");
    }

    [TestMethod]
    public void FoundationFreeze_FormalRetrievalAllowedBlocks()
    {
        var report = BuildFoundationFreezeReport(
            vectorFormal: CleanVectorFormalPreviewFreezeReport(formalRetrievalAllowed: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.BlockedByFormalRetrieval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalRetrievalAllowed");
    }

    [TestMethod]
    public void FoundationFreeze_MissingP15GateBlocks()
    {
        var report = BuildFoundationFreezeReport(
            p15A3: new P15ReportStatus(false, 0, 0, 0, "MissingReport"));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.BlockedByP15Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "P15GateNotPassed");
    }

    [TestMethod]
    public void FoundationFreeze_MissingRuntimeChangeGateBlocks()
    {
        var report = BuildFoundationFreezeReport(runtimeGate: null, includeRuntimeGate: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ContextCoreFoundationFreezeRecommendations.BlockedByRuntimeChangeGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeGateNotPassed");
    }

    [TestMethod]
    public void FoundationApiSecurityDiagnostics_ShouldDetectSecretAndAbsolutePathLeaks()
    {
        var report = FoundationReportBuilder.BuildSecurityDiagnostics(
            requireApiKey: true,
            apiKeyConfigured: true,
            developmentMode: false,
            serializedResponses:
            [
                @"path=D:\context\foundation.json token=unit-secret"
            ],
            secretProbe: "unit-secret");

        Assert.IsTrue(report.AuthConfigured);
        Assert.IsTrue(report.SecretLeakDetected);
        Assert.IsTrue(report.AbsolutePathLeakDetected);
        Assert.AreEqual("NotConfigured", report.Recommendation);
    }

    [TestMethod]
    public async Task FoundationReportNavigation_MissingReportsShouldBeDegradedWithoutAbsolutePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-foundation-navigation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FoundationStatusService(root);
            var envelope = await service.GetReportNavigationEnvelopeAsync();

            Assert.IsNotNull(envelope.Data);
            Assert.AreEqual("Degraded", envelope.Status);
            Assert.AreEqual("RegenerateReport", envelope.Recommendation);
            Assert.IsTrue(envelope.Data!.DegradedReportCount > 0);
            Assert.IsTrue(envelope.Data.Reports.All(static report => !Path.IsPathRooted(report.RelativePath)));
            Assert.IsTrue(envelope.Data.Reports.All(static report => report.SafeToExpose));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_BlocksScopedRuntimeExperimentHarnessFreezeRuntimeUse()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze,
                    CurrentPhase = "V4.10",
                    Status = "ReadyForGuardedRuntimeExperimentPlanning",
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        ShadowRuntimeModes.Off,
                        "NoOpHarnessOnly",
                        "RuntimeSwitch"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "FormalRetrievalSwitch",
                        "FormalRetrievalAllowed",
                        "FormalIVectorIndexStoreBinding",
                        "FormalPackageWrite"
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze}:ScopedRuntimeExperimentHarnessFreezeDoesNotAllowRuntimeSwitch");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze}:ScopedRuntimeExperimentHarnessFreezePackageAndBindingMutationForbidden");
        CollectionAssert.Contains(
            report.FailedConditions.ToList(),
            $"{ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze}:NoOpHarnessOnlyIsNotRuntimeApproval");
    }

    private static GuardedScopedRuntimeExperimentOptions CleanGuardedScopedRuntimeExperimentOptions(
        bool enabled = true,
        bool writeFormalPackage = false,
        bool mutatePackingPolicy = false,
        bool packageOutputChanged = false,
        bool runtimeMutated = false,
        bool vectorStoreBindingChanged = false,
        bool killSwitchTriggered = false,
        bool rollbackVerified = true,
        int riskAfterPolicy = 0,
        int scopeLeakCount = 0)
        => new()
        {
            Enabled = enabled,
            Mode = GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalId = "vsrea-clean",
            WorkspaceAllowlist = new[] { "contextcore_eval" },
            CollectionAllowlist = new[] { "dataset-v2-stress" },
            EvalScopeAllowlist = new[] { "dataset-v2-stress" },
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            MaxRequestCount = 3,
            MaxDurationMinutes = 30,
            MaxErrorCount = 0,
            RequireV413PreflightPassed = true,
            RequireScopedRuntimeExperimentApproval = true,
            RequireKillSwitch = true,
            RequireRollbackPlan = true,
            RequireTraceSink = true,
            TraceSinkAvailable = true,
            WriteFormalPackage = writeFormalPackage,
            MutateFormalOutput = false,
            MutatePackingPolicy = mutatePackingPolicy,
            GlobalDefaultOn = false,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackageOutputChanged = packageOutputChanged,
            KillSwitchTriggered = killSwitchTriggered,
            RollbackVerified = rollbackVerified,
            NonAllowlistedScopeLeakCount = scopeLeakCount,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            ErrorCount = 0
        };
    private static ScopedRuntimeExperimentObservationWindowOptions CleanScopedRuntimeExperimentObservationWindowOptions(
        bool enabled = true,
        int minRequestCount = 360,
        int observationRunCount = 3,
        bool writeFormalPackage = false,
        bool mutatePackingPolicy = false,
        bool packageOutputChanged = false,
        bool runtimeMutated = false,
        bool vectorStoreBindingChanged = false,
        bool killSwitchAvailable = true,
        bool killSwitchSmokePassed = true,
        bool rollbackVerified = true,
        int riskAfterPolicy = 0,
        int scopeLeakCount = 0,
        int formalOutputChanged = 0,
        double traceCompleteness = 100)
        => new()
        {
            Enabled = enabled,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalId = "vsrea-clean",
            ObservationWindowId = "vsreow-clean",
            Mode = ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation,
            WorkspaceAllowlist = new[] { "contextcore_eval" },
            CollectionAllowlist = new[] { "dataset-v2-stress" },
            EvalScopeAllowlist = new[] { "dataset-v2-stress" },
            MinRequestCount = minRequestCount,
            ObservationRunCount = observationRunCount,
            MaxDurationMinutes = 30,
            MaxErrorCount = 0,
            MaxLatencyP95Ms = 1_000,
            RequireV414GatePassed = true,
            RequireKillSwitch = true,
            RequireRollbackPlan = true,
            RequireTraceSink = true,
            TraceSinkAvailable = true,
            WriteFormalPackage = writeFormalPackage,
            MutateFormalOutput = false,
            MutatePackingPolicy = mutatePackingPolicy,
            GlobalDefaultOn = false,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackageOutputChanged = packageOutputChanged,
            KillSwitchAvailable = killSwitchAvailable,
            KillSwitchSmokePassed = killSwitchSmokePassed,
            RollbackVerified = rollbackVerified,
            NonAllowlistedScopeLeakCount = scopeLeakCount,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            ErrorCount = 0,
            TraceCompleteness = traceCompleteness
        };

    private static ScopedRuntimeExperimentActivationPreflightOptions CleanScopedRuntimeExperimentActivationPreflightOptions(
        string proposalId = "vsrep-bb5402e39c0f1333",
        string approvalId = "vsrea-clean",
        bool traceSinkAvailable = true,
        bool mutateRuntime = false,
        bool vectorStoreBindingChanged = false,
        bool writeFormalPackage = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false,
        int scopeLeakCount = 0)
        => new()
        {
            Enabled = true,
            ProposalId = proposalId,
            ApprovalId = approvalId,
            Mode = ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute,
            RequireV411PlanPassed = true,
            RequireV412ApprovalPassed = true,
            RequireFoundationFreeze = true,
            RequireServiceFoundationFreeze = true,
            RequireRuntimeChangeGate = true,
            RequireKillSwitch = true,
            RequireRollbackPlan = true,
            RequireTraceSink = true,
            TraceSinkAvailable = traceSinkAvailable,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            WriteFormalPackage = writeFormalPackage,
            MutateRuntime = mutateRuntime,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            NonAllowlistedScopeLeakCount = scopeLeakCount,
            RiskAfterPolicy = 0,
            FormalOutputChanged = 0
        };
    private static ScopedRuntimeExperimentApprovalOptions CleanScopedRuntimeExperimentApprovalOptions(
        string approvedBy = "codex",
        string reason = "V4.9 no-op harness approval for scoped runtime experiment proposal.",
        string approvalMode = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly,
        bool allowRuntimeSwitch = false,
        bool allowFormalRetrieval = false,
        bool allowFormalPackageWrite = false,
        bool allowPackingPolicyChange = false)
        => new()
        {
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovedBy = approvedBy,
            Reason = reason,
            ApprovalMode = approvalMode,
            AllowRuntimeSwitch = allowRuntimeSwitch,
            AllowFormalRetrieval = allowFormalRetrieval,
            AllowFormalPackageWrite = allowFormalPackageWrite,
            AllowPackingPolicyChange = allowPackingPolicyChange
        };

    private static ScopedRuntimeExperimentApprovalOptions CleanScopedRuntimeExperimentRuntimeApprovalOptions(
        string approvedBy = "codex",
        string reason = "Approve V4.12 scoped runtime experiment for activation preflight only.",
        string riskAcknowledgement = "Risk gates must remain zero.",
        string rollbackAcknowledgement = "Rollback plan acknowledged.",
        string killSwitchAcknowledgement = "Kill switch plan acknowledged.",
        string scopeAcknowledgement = "Selected scope acknowledged.",
        string observationPlanAcknowledgement = "Observation plan acknowledged.",
        bool allowRuntimeSwitch = false,
        bool allowFormalRetrieval = false,
        bool allowFormalPackageWrite = false,
        bool allowPackingPolicyChange = false)
        => new()
        {
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovedBy = approvedBy,
            Reason = reason,
            ApprovalMode = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
            AllowRuntimeSwitch = allowRuntimeSwitch,
            AllowFormalRetrieval = allowFormalRetrieval,
            AllowFormalPackageWrite = allowFormalPackageWrite,
            AllowPackingPolicyChange = allowPackingPolicyChange,
            RiskAcknowledgement = riskAcknowledgement,
            RollbackAcknowledgement = rollbackAcknowledgement,
            KillSwitchAcknowledgement = killSwitchAcknowledgement,
            ScopeAcknowledgement = scopeAcknowledgement,
            ObservationPlanAcknowledgement = observationPlanAcknowledgement
        };

    private static ScopedRuntimeExperimentApprovalRecord CleanScopedRuntimeExperimentApprovalRecord(
        DateTimeOffset? expiresAt = null,
        bool revoked = false,
        string approvalMode = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly)
        => new()
        {
            ApprovalId = "vsrea-clean",
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovedBy = "codex",
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovalScope = "contextcore_eval/dataset-v2-stress/dataset-v2-stress",
            ApprovalMode = approvalMode,
            Reason = "V4.9 no-op harness approval.",
            RiskAcknowledgement = "No runtime switch.",
            RollbackAcknowledgement = "Rollback plan acknowledged.",
            KillSwitchAcknowledgement = "Kill switch plan acknowledged.",
            ScopeAcknowledgement = "Selected scope acknowledged.",
            ObservationPlanAcknowledgement = "Observation plan acknowledged.",
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(1),
            Revoked = revoked
        };

    private static ScopedRuntimeExperimentApprovalSummaryReport CleanScopedRuntimeExperimentApprovalSummary(
        bool approvalRecordExists = true,
        string approvalMode = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly,
        bool expired = false,
        bool revoked = false)
        => new()
        {
            OperationId = "approval-summary-clean",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalCount = approvalRecordExists ? 1 : 0,
            ApprovalRecordExists = approvalRecordExists,
            LatestApprovalId = approvalRecordExists ? "vsrea-clean" : string.Empty,
            ApprovalMode = approvalMode,
            Expired = expired,
            Revoked = revoked,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            Recommendation = approvalRecordExists
                && !expired
                && !revoked
                && string.Equals(approvalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase)
                    ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze
                    : ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval,
            BlockedReasons = Array.Empty<string>()
        };

    private static ScopedRuntimeExperimentNoOpHarnessOptions CleanScopedRuntimeExperimentNoOpHarnessOptions(
        bool writeFormalPackage = false,
        bool mutateRuntime = false,
        bool vectorStoreBindingChanged = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false)
        => new()
        {
            Enabled = true,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalId = "vsrea-clean",
            WorkspaceAllowlist = ["contextcore_eval"],
            CollectionAllowlist = ["dataset-v2-stress"],
            EvalScopeAllowlist = ["dataset-v2-stress"],
            WriteFormalPackage = writeFormalPackage,
            MutateRuntime = mutateRuntime,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged
        };

    private static ContextCoreFoundationFreezeReport BuildFoundationFreezeReport(
        PostgresRelationMultiNormalScopeCanaryReport? relation = null,
        LearningFeedbackPostgresFreezeGateReport? learningFeedback = null,
        JobQueuePostgresFreezeGateReport? jobQueue = null,
        VectorPostgresProviderFreezeGateReport? vectorPostgres = null,
        VectorFormalPreviewFreezeReport? vectorFormal = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        bool includeRelation = true,
        bool includeVectorFormal = true,
        bool includeRuntimeGate = true,
        P15ReportStatus? p15A3 = null,
        P15ReportStatus? p15Extended = null,
        IReadOnlyDictionary<string, bool>? reportCoverage = null,
        IReadOnlyDictionary<string, bool>? docsCoverage = null,
        IReadOnlyDictionary<string, bool>? controlRoomCoverage = null)
        => new ContextCoreFoundationFreezeRunner().BuildReport(
            includeRelation ? relation ?? CleanRelationGovernanceFreezeReport() : null,
            learningFeedback ?? CleanLearningFeedbackFreezeReport(),
            jobQueue ?? CleanJobQueueFreezeReport(),
            vectorPostgres ?? CleanPgVectorFreezeGate(true),
            includeVectorFormal ? vectorFormal ?? CleanVectorFormalPreviewFreezeReport() : null,
            includeRuntimeGate ? runtimeGate ?? new LearningRuntimeChangeReadinessGateReport { Passed = true } : null,
            p15A3 ?? new P15ReportStatus(true, 50, 0, 0, "Loaded"),
            p15Extended ?? new P15ReportStatus(true, 113, 0, 0, "Loaded"),
            reportCoverage ?? CleanFoundationCoverage(),
            docsCoverage ?? CleanFoundationDocsCoverage(),
            controlRoomCoverage ?? CleanFoundationControlRoomCoverage());

    private static PostgresRelationMultiNormalScopeCanaryReport CleanRelationGovernanceFreezeReport()
        => new()
        {
            GatePassed = true,
            Recommendation = "ReadyForLimitedScopeExpansion",
            MismatchCount = 0,
            PostgresFailureCount = 0,
            ScopeLeakCount = 0,
            BlockedReasons = Array.Empty<string>()
        };

    private static LearningFeedbackPostgresFreezeGateReport CleanLearningFeedbackFreezeReport()
        => new()
        {
            Passed = true,
            LearningFeedbackPostgres = "ReadyForScopedServiceMode",
            MismatchCount = 0,
            PostgresFailureCount = 0,
            ScopeLeakCount = 0,
            TrainableCandidateLeakCount = 0,
            Recommendation = "ReadyForScopedServiceMode",
            BlockedReasons = Array.Empty<string>()
        };

    private static JobQueuePostgresFreezeGateReport CleanJobQueueFreezeReport()
        => new()
        {
            Passed = true,
            JobQueuePostgres = "ReadyForScopedWorkerMode",
            DuplicateExecutionCount = 0,
            LeaseViolationCount = 0,
            RetryViolationCount = 0,
            DeadLetterViolationCount = 0,
            PostgresFailureCount = 0,
            ScopeLeakCount = 0,
            RuntimeWorkerGlobalProviderUnchanged = true,
            Recommendation = "ReadyForScopedWorkerMode",
            BlockedReasons = Array.Empty<string>()
        };

    private static VectorFormalPreviewFreezeReport CleanVectorFormalPreviewFreezeReport(
        bool formalRetrievalAllowed = false,
        bool readyForRuntimeSwitch = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false)
        => new()
        {
            FreezePassed = !formalRetrievalAllowed
                && !readyForRuntimeSwitch
                && !packingPolicyChanged
                && !packageOutputChanged,
            VectorFormalPreview = VectorFormalPreviewFreezeStatuses.ReadyForScopedOptInPreview,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            RuntimeSwitchAllowed = readyForRuntimeSwitch,
            UseForRuntime = false,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            FormalPackageWritten = false,
            RuntimeMutated = false,
            NonAllowlistedScopeLeakCount = 0,
            V4ReadinessRecheckPassed = true,
            GuardedFormalPreviewGatePassed = true,
            ShadowPackageComparisonGatePassed = true,
            ScopedFormalPreviewOptInGatePassed = true,
            LimitedFormalPreviewObservationGatePassed = true,
            RuntimeChangeReadinessGatePassed = true,
            Recommendation = VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview
        };

    private static IReadOnlyDictionary<string, bool> CleanFoundationCoverage(string? reportMissing = null)
    {
        var coverage = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["storage/postgres/postgres-relation-multi-normal-scope-quality-report.json"] = true,
            ["storage/postgres/postgres-learning-feedback-freeze-gate.json"] = true,
            ["storage/postgres/postgres-job-queue-freeze-gate.json"] = true,
            ["storage/postgres/postgres-vector-freeze-gate.json"] = true,
            ["vector/v4/vector-formal-preview-freeze-gate.json"] = true,
            ["learning/readiness/learning-runtime-change-readiness-gate.json"] = true,
            ["eval/eval-report-p15-a3.json"] = true,
            ["eval/eval-report-p15-extended.json"] = true
        };
        if (!string.IsNullOrWhiteSpace(reportMissing))
        {
            coverage[reportMissing] = false;
        }

        return coverage;
    }

    private static IReadOnlyDictionary<string, bool> CleanFoundationDocsCoverage()
        => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs/relation-governance-postgres-freeze.md"] = true,
            ["docs/postgres-operational-store.md"] = true,
            ["docs/job-queue-postgres-freeze.md"] = true,
            ["docs/vector-postgres-provider-freeze.md"] = true,
            ["docs/vector-embedding-provider-comparison-freeze.md"] = true,
            ["docs/vector-hybrid-retrieval-freeze.md"] = true,
            ["docs/vector-preview-shadow-freeze.md"] = true,
            ["docs/learning-loop-foundation.md"] = true,
            ["docs/ContextCore_Foundation_Freeze_Report.md"] = true,
            ["docs/controlroom-service-mode.md"] = true
        };

    private static IReadOnlyDictionary<string, bool> CleanFoundationControlRoomCoverage()
        => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["Foundation Freeze Summary renderer"] = true,
            ["Foundation freeze report loader"] = true,
            ["Vector formal preview freeze status"] = true
        };

    private static ScopedFormalPreviewOptInReport CleanScopedFormalPreviewOptInGate(
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        bool packageOutputChanged = false,
        bool packingPolicyChanged = false,
        bool formalPackageWritten = false,
        bool runtimeMutated = false,
        int scopeLeakCount = 0,
        bool gatePassed = true)
    {
        var clean = gatePassed
            && riskAfterPolicy == 0
            && formalOutputChanged == 0
            && !packageOutputChanged
            && !packingPolicyChanged
            && !formalPackageWritten
            && !runtimeMutated
            && scopeLeakCount == 0;
        return new ScopedFormalPreviewOptInReport
        {
            OperationId = "vector-scoped-formal-preview-opt-in-gate-test",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = clean,
            SmokePassed = clean,
            GatePassed = clean,
            Mode = ScopedFormalPreviewOptInModes.PreviewOnly,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            WorkspaceAllowlist = ["contextcore_eval"],
            CollectionAllowlist = ["dataset-v2-stress"],
            EvalScopeAllowlist = ["dataset-v2-stress"],
            SelectedWorkspaceId = "contextcore_eval",
            SelectedCollectionId = "dataset-v2-stress",
            SelectedEvalScope = "dataset-v2-stress",
            NonAllowlistedWorkspaceId = "contextcore_other",
            NonAllowlistedCollectionId = "dataset-v2-other",
            NonAllowlistedEvalScope = "dataset-v2-other",
            ScopeCount = 2,
            AllowlistedScopeCount = 1,
            NonAllowlistedScopeChecked = true,
            PreviewPackageCount = 120,
            BaselinePackageCount = 120,
            CandidateAddCount = 19,
            CandidateRemoveCount = 19,
            TokenDeltaTotal = 55,
            TokenDeltaMax = 5,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            NonAllowlistedScopeLeakCount = scopeLeakCount,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            Recommendation = clean
                ? ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation
                : ScopedFormalPreviewOptInRecommendations.BlockedByRisk,
            BlockedReasons = clean ? Array.Empty<string>() : ["SyntheticScopedFormalPreviewOptInGateBlocked"]
        };
    }

    private static GuardedFormalRetrievalPreviewReport CleanGuardedFormalPreviewGate(
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        bool gatePassed = true)
    {
        var clean = gatePassed && riskAfterPolicy == 0 && formalOutputChanged == 0;
        return new GuardedFormalRetrievalPreviewReport
        {
            OperationId = "vector-guarded-formal-retrieval-preview-gate-test",
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = clean,
            GatePassed = clean,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            V4RecheckPassed = clean,
            SampleCount = 120,
            QueryCount = 120,
            BaselineCandidateCount = 600,
            PreviewVectorCandidateCount = 600,
            WouldAddCount = 57,
            WouldRemoveCount = 57,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            Recommendation = clean
                ? GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison
                : GuardedFormalRetrievalPreviewRecommendations.BlockedByRisk,
            BlockedReasons = clean ? Array.Empty<string>() : ["SyntheticGuardedPreviewGateBlocked"]
        };
    }

    private static VectorShadowPackageComparisonReport CleanVectorShadowPackageComparisonGate(
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        bool packageOutputChanged = false,
        bool packingPolicyChanged = false,
        bool gatePassed = true)
    {
        var clean = gatePassed
            && riskAfterPolicy == 0
            && formalOutputChanged == 0
            && !packageOutputChanged
            && !packingPolicyChanged;
        return new VectorShadowPackageComparisonReport
        {
            OperationId = "vector-shadow-package-comparison-gate-test",
            CreatedAt = DateTimeOffset.UtcNow,
            ComparisonPassed = clean,
            GatePassed = clean,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            SampleCount = 120,
            QueryCount = 120,
            BaselinePackageCount = 120,
            ShadowPackageCount = 120,
            CandidateAddCount = 57,
            CandidateRemoveCount = 57,
            CandidateUnchangedCount = 543,
            SectionChangedCount = 0,
            TokenDeltaTotal = 55,
            TokenDeltaMax = 10,
            ConstraintCoverageDelta = 0.0167,
            RelationCoverageDelta = 0.0569,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            ShadowPackageWritten = false,
            RuntimeMutated = false,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            Recommendation = clean
                ? VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn
                : VectorShadowPackageComparisonRecommendations.BlockedByRisk,
            BlockedReasons = clean ? Array.Empty<string>() : ["SyntheticShadowPackageComparisonGateBlocked"]
        };
    }

    private static ScopedFormalPreviewOptInOptions CleanScopedFormalPreviewOptions(
        bool writeFormalPackage = false,
        bool useForRuntime = false,
        bool includeNonAllowlistedInAllowlist = false)
    {
        const string selectedWorkspace = "contextcore_eval";
        const string selectedCollection = "dataset-v2-stress";
        const string selectedEvalScope = "dataset-v2-stress";
        const string outsideWorkspace = "contextcore_eval_outside";
        const string outsideCollection = "dataset-v2-stress-outside";
        const string outsideEvalScope = "dataset-v2-stress-outside";
        return new ScopedFormalPreviewOptInOptions
        {
            Enabled = true,
            Mode = ScopedFormalPreviewOptInModes.PreviewOnly,
            WorkspaceAllowlist = includeNonAllowlistedInAllowlist
                ? [selectedWorkspace, outsideWorkspace]
                : [selectedWorkspace],
            CollectionAllowlist = includeNonAllowlistedInAllowlist
                ? [selectedCollection, outsideCollection]
                : [selectedCollection],
            EvalScopeAllowlist = includeNonAllowlistedInAllowlist
                ? [selectedEvalScope, outsideEvalScope]
                : [selectedEvalScope],
            SelectedWorkspaceId = selectedWorkspace,
            SelectedCollectionId = selectedCollection,
            SelectedEvalScope = selectedEvalScope,
            NonAllowlistedWorkspaceId = outsideWorkspace,
            NonAllowlistedCollectionId = outsideCollection,
            NonAllowlistedEvalScope = outsideEvalScope,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            WriteFormalPackage = writeFormalPackage,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false
        };
    }

    private static LimitedFormalPreviewObservationOptions CleanLimitedFormalPreviewObservationOptions(
        int observationRuns = 3,
        bool writeFormalPackage = false,
        bool useForRuntime = false)
        => new()
        {
            Enabled = true,
            Mode = ScopedFormalPreviewOptInModes.PreviewOnly,
            ObservationWindowRuns = observationRuns,
            WorkspaceAllowlist = ["contextcore_eval"],
            CollectionAllowlist = ["dataset-v2-stress"],
            EvalScopeAllowlist = ["dataset-v2-stress"],
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            WriteFormalPackage = writeFormalPackage
        };

    private static ExplicitScopedRuntimeExperimentPlanOptions CleanExplicitScopedRuntimeExperimentOptions(
        string mode = ExplicitScopedRuntimeExperimentModes.DryRun,
        bool includeScopes = true,
        bool useForRuntime = false,
        bool formalRetrievalAllowed = false,
        bool readyForRuntimeSwitch = false,
        bool writeFormalPackage = false)
        => new()
        {
            Enabled = true,
            Mode = mode,
            WorkspaceAllowlist = includeScopes ? ["contextcore_eval"] : Array.Empty<string>(),
            CollectionAllowlist = includeScopes ? ["dataset-v2-stress"] : Array.Empty<string>(),
            EvalScopeAllowlist = includeScopes ? ["dataset-v2-stress"] : Array.Empty<string>(),
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireFoundationFreeze = true,
            RequireServiceFoundationFreeze = true,
            RequireVectorFormalPreviewFreeze = true,
            RequireRuntimeChangeGate = true,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            WriteFormalPackage = writeFormalPackage
        };

    private static ExplicitScopedRuntimeExperimentPlanReport CleanExplicitScopedRuntimeExperimentGate(
        bool planPassed = true,
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        bool formalPackageWritten = false,
        bool runtimeMutated = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false,
        int scopeLeakCount = 0,
        string rollbackPlan = "Remove scopes from allowlists, keep UseForRuntime=false, discard shadow artifacts, rerun V4.F and runtime-change gate.")
    {
        var clean = planPassed
            && riskAfterPolicy == 0
            && formalOutputChanged == 0
            && !formalPackageWritten
            && !runtimeMutated
            && !packingPolicyChanged
            && !packageOutputChanged
            && scopeLeakCount == 0
            && !string.IsNullOrWhiteSpace(rollbackPlan);
        return new ExplicitScopedRuntimeExperimentPlanReport
        {
            OperationId = "vector-scoped-runtime-experiment-gate-test",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = planPassed,
            Recommendation = planPassed
                ? ExplicitScopedRuntimeExperimentRecommendations.ReadyForExplicitScopedRuntimeExperimentDryRun
                : ExplicitScopedRuntimeExperimentRecommendations.KeepPreviewOnly,
            Mode = ExplicitScopedRuntimeExperimentModes.DryRun,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            WorkspaceAllowlist = ["contextcore_eval"],
            CollectionAllowlist = ["dataset-v2-stress"],
            EvalScopeAllowlist = ["dataset-v2-stress"],
            ScopeCount = 2,
            AllowlistedScopeCount = 1,
            NonAllowlistedScopeChecked = true,
            DryRunSupported = clean,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            NonAllowlistedScopeLeakCount = scopeLeakCount,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            RollbackPlan = rollbackPlan,
            AllowedActions = ["ShadowArtifactOnlyDryRun"],
            ForbiddenActions = ["RuntimeSwitch", "FormalIVectorIndexStoreBinding", "FormalPackageWrite"],
            BlockedReasons = clean ? Array.Empty<string>() : ["SyntheticExplicitScopedRuntimeExperimentGateBlocked"]
        };
    }

    private static ExplicitScopedRuntimeExperimentProposalOptions CleanScopedRuntimeExperimentProposalOptions(
        string workspaceId = "contextcore_eval",
        string collectionId = "dataset-v2-stress",
        string evalScopeId = "dataset-v2-stress",
        string rollbackPlan = "Remove selected scope from proposal allowlist and keep UseForRuntime=false.",
        string killSwitchPlan = "Clear proposal scope allowlists and rerun runtime-change gate.",
        bool useForRuntime = false,
        bool formalRetrievalAllowed = false,
        bool readyForRuntimeSwitch = false,
        bool writeFormalPackage = false,
        bool approved = false)
        => new()
        {
            Enabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EvalScopeId = evalScopeId,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            Mode = ExplicitScopedRuntimeExperimentProposalModes.ProposalOnly,
            RequireV47DesignFreeze = true,
            RequireFoundationFreeze = true,
            RequireServiceFoundationFreeze = true,
            RequireVectorFormalPreviewFreeze = true,
            RequireRuntimeChangeGate = true,
            RequireManualApproval = true,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            WriteFormalPackage = writeFormalPackage,
            RollbackPlan = rollbackPlan,
            KillSwitchPlan = killSwitchPlan,
            Approved = approved
        };

    private static ExplicitScopedRuntimeExperimentProposalReport CleanScopedRuntimeExperimentProposalGate(
        string workspaceId = "contextcore_eval",
        string collectionId = "dataset-v2-stress",
        string evalScopeId = "dataset-v2-stress",
        string rollbackPlan = "Remove selected scope and rerun runtime-change gate.",
        string killSwitchPlan = "Clear selected scope and keep UseForRuntime=false.")
        => new()
        {
            OperationId = "scoped-runtime-experiment-proposal-clean",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ProposalPassed = true,
            Recommendation = ExplicitScopedRuntimeExperimentProposalRecommendations.ReadyForManualExperimentApproval,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EvalScopeId = evalScopeId,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RollbackPlan = rollbackPlan,
            KillSwitchPlan = killSwitchPlan,
            ObservationPlan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RequestCount"] = "count requests",
                ["RiskAfterPolicy"] = "must remain zero"
            },
            ApprovalRequired = true,
            Approved = false,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            WriteFormalPackage = false,
            ConfigPatchWritten = false,
            DiBindingChanged = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            NonAllowlistedScopeLeakCount = 0,
            ForbiddenActions = ["RuntimeSwitch", "FormalRetrieval", "FormalPackageWrite"],
            BlockedReasons = Array.Empty<string>()
        };

    private static GuardedScopedRuntimeExperimentPlanOptions CleanGuardedScopedRuntimeExperimentPlanOptions(
        bool includeScopes = true,
        string requiredApprovalMode = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
        bool requireObservationPlan = true,
        bool useForRuntime = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false,
        bool readyForRuntimeSwitch = false)
        => new()
        {
            Enabled = true,
            Mode = GuardedScopedRuntimeExperimentPlanModes.PlanOnly,
            ProposalId = "vsrep-bb5402e39c0f1333",
            RequiredApprovalMode = requiredApprovalMode,
            WorkspaceAllowlist = includeScopes ? ["contextcore_eval"] : Array.Empty<string>(),
            CollectionAllowlist = includeScopes ? ["dataset-v2-stress"] : Array.Empty<string>(),
            EvalScopeAllowlist = includeScopes ? ["dataset-v2-stress"] : Array.Empty<string>(),
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            MaxRequestCount = 120,
            MaxDurationMinutes = 30,
            MaxErrorCount = 0,
            MaxRiskCount = 0,
            RequireKillSwitch = true,
            RequireRollbackPlan = true,
            RequireObservationPlan = requireObservationPlan,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch
        };

    private static ScopedRuntimeExperimentDryRunObservationOptions CleanScopedRuntimeExperimentDryRunObservationOptions(
        int observationRuns = 3,
        bool writeFormalPackage = false,
        bool useForRuntime = false,
        bool runtimeMutated = false,
        bool vectorStoreBindingChanged = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false)
        => new()
        {
            Enabled = true,
            Mode = ScopedRuntimeExperimentDryRunObservationModes.DryRun,
            ObservationRunCount = observationRuns,
            WorkspaceAllowlist = ["contextcore_eval"],
            CollectionAllowlist = ["dataset-v2-stress"],
            EvalScopeAllowlist = ["dataset-v2-stress"],
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireV45PlanPassed = true,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            WriteFormalPackage = writeFormalPackage,
            FailClosedOnRisk = true,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged
        };

    private static ScopedRuntimeExperimentDryRunObservationReport CleanScopedRuntimeExperimentDryRunObservationGate(
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        bool runtimeMutated = false,
        bool vectorStoreBindingChanged = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false,
        bool formalPackageWritten = false,
        int scopeLeakCount = 0,
        bool rollbackPlanAvailable = true,
        bool gatePassed = true)
    {
        return new ScopedRuntimeExperimentDryRunObservationReport
        {
            OperationId = "vector-scoped-runtime-experiment-dry-run-observation-gate-test",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = gatePassed,
            GatePassed = gatePassed,
            Mode = ScopedRuntimeExperimentDryRunObservationModes.DryRun,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            ObservationRunCount = 3,
            MinimumObservationRunCount = 3,
            WorkspaceAllowlist = ["contextcore_eval"],
            CollectionAllowlist = ["dataset-v2-stress"],
            EvalScopeAllowlist = ["dataset-v2-stress"],
            AllowlistedScopeCount = 1,
            NonAllowlistedScopeChecked = true,
            DryRunPackageCount = 360,
            BaselinePackageCount = 360,
            CandidateAddCount = 171,
            CandidateRemoveCount = 171,
            TokenDeltaTotal = 165,
            TokenDeltaMax = 10,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            NonAllowlistedScopeLeakCount = scopeLeakCount,
            RollbackPlanAvailable = rollbackPlanAvailable,
            RuntimeChangeGateConsistent = true,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            Recommendation = gatePassed
                ? ScopedRuntimeExperimentDryRunObservationRecommendations.ReadyForScopedRuntimeExperimentDesignFreeze
                : ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly,
            BlockedReasons = gatePassed ? Array.Empty<string>() : ["SyntheticScopedRuntimeExperimentDryRunObservationBlocked"]
        };
    }

    private static VectorRetrievalShadowReadinessGateReport CleanLegacyVectorReadinessGate()
        => new()
        {
            Passed = false,
            A3RecallAfterPolicy = 0.0455,
            A3RiskAfterPolicy = 0,
            A3MustNotHitRiskAfterPolicy = 0,
            A3LifecycleRiskAfterPolicy = 0,
            A3FormalOutputChanged = 0,
            ExtendedRecallAfterPolicy = 0.0313,
            ExtendedRiskAfterPolicy = 0,
            ExtendedMustNotHitRiskAfterPolicy = 0,
            ExtendedLifecycleRiskAfterPolicy = 0,
            ExtendedFormalOutputChanged = 0,
            FailReasons = ["A3RecallBelow80Percent", "ExtendedRecallBelow80Percent"]
        };

    private static RetrievalDatasetLegacyLimitationReport CleanLegacyLimitationReport()
        => new()
        {
            ReviewCandidateCount = 32,
            MissingEvidenceSourceProvenanceCandidateCount = 32,
            EvidenceBackfillRecommendation = "NeedsIngestionMetadataBackfill",
            LegacyDatasetSuitableForPrimaryRecallRepair = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = "NeedsIngestionMetadataBackfill"
        };

    private static VectorPostgresProviderFreezeGateReport CleanPgVectorFreezeGate(bool parityPassed)
        => new()
        {
            Passed = parityPassed,
            VectorPostgresProvider = parityPassed ? "ReadyForPreviewShadowStorage" : "NotReady",
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            DiagnosticsReady = true,
            CompatibilityReady = true,
            ParityPassed = parityPassed,
            ReindexQualityPassed = true,
            QueryPreviewPassed = true,
            ShadowEvalPassed = true,
            A3RecallDelta = 0,
            ExtendedRecallDelta = 0,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            ProjectionMismatchCount = 0,
            Recommendation = parityPassed ? "ReadyForPreviewShadowStorage" : "KeepPreviewOnly"
        };

    private static EmbeddingProviderComparisonFreezeReport CleanQwen3ProviderFreeze()
        => new()
        {
            Passed = false,
            ProviderComparison = "Conclusive",
            ProviderConfigurationSanityPassed = true,
            ReadinessGatePassed = false,
            A3RecallAfterPolicy = 0.04,
            ExtendedRecallAfterPolicy = 0.03,
            RiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            PromotionStatus = EmbeddingProviderPromotionStatuses.DoNotPromote,
            VectorV4RecheckAllowed = false,
            FormalRetrievalAllowed = false,
            Recommendation = "BlockedByRecall"
        };

    private static HybridRetrievalPreviewFreezeReport CleanHybridRetrievalFreeze()
        => new()
        {
            FreezePassed = true,
            HybridRetrievalStatus = HybridRetrievalReadinessRecommendations.KeepPreviewOnly,
            Recommendation = HybridRetrievalReadinessRecommendations.BlockedByA3Recall,
            HybridBestRecallA3 = 0.0455,
            HybridBestRecallExtended = 0.0313,
            RiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            V4RecheckAllowed = false
        };

    private static LearningRuntimeChangeReadinessGateReport CleanRuntimeChangeGate(bool passed)
        => new()
        {
            Passed = passed,
            Recommendation = passed ? "RuntimeChangeRulesSatisfied" : "KeepRuntimeDefaults",
            FailedConditions = passed ? Array.Empty<string>() : ["VectorRetrieval:FormalRetrievalSwitchForbidden"]
        };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"contextcore-rdsv2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteJsonLines<T>(string path, IReadOnlyList<T> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SerializeJsonLines(values));
    }

    private static string SerializeJsonLines<T>(IReadOnlyList<T> values)
    {
        return string.Join(Environment.NewLine, values.Select(static value => JsonSerializer.Serialize(value)))
            + (values.Count == 0 ? string.Empty : Environment.NewLine);
    }

    private static RetrievalDatasetV2MaterializationReport CleanMaterializationGate()
    {
        return new RetrievalDatasetV2MaterializationReport
        {
            DatasetId = "rdsv2-test",
            CorpusItemCount = 1,
            SampleCount = 1,
            CorpusExists = true,
            SamplesExists = true,
            ManifestExists = true,
            ValidatePassed = true,
            QualityRecommendation = RetrievalDatasetV2GenerationRecommendations.ReadyForDatasetV2ShadowEval,
            CorpusHashStable = true,
            SamplesHashStable = true,
            ValidationIssueCount = 0,
            MissingEvidenceCount = 0,
            MissingProvenanceCount = 0,
            ItemIdLeakageCount = 0,
            RelationInconsistencyCount = 0,
            GatePassed = true,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2MaterializationRecommendations.ReadyForDatasetV2ShadowEval
        };
    }

    private static RetrievalDatasetV2ShadowEvalProfileReport Profile(
        double recall,
        int risk,
        int formalOutputChanged = 0)
    {
        return new RetrievalDatasetV2ShadowEvalProfileReport
        {
            DatasetId = "rdsv2-test",
            ProfileName = "hybrid-dense-plus-lexical-anchor",
            RecallAfterPolicy = recall,
            MrrAfterPolicy = recall,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate
        };
    }

    private static RetrievalDatasetV2ShadowEvalSummaryReport Summary(
        RetrievalDatasetV2ShadowEvalProfileReport profile,
        bool pgVectorParityPassed)
    {
        return new RetrievalDatasetV2ShadowEvalSummaryReport
        {
            DatasetId = "rdsv2-test",
            BestProfileName = profile.ProfileName,
            BestRecallAfterPolicy = profile.RecallAfterPolicy,
            BestMrrAfterPolicy = profile.MrrAfterPolicy,
            BestRiskAfterPolicy = profile.RiskAfterPolicy,
            PgVectorParityPassed = pgVectorParityPassed,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = pgVectorParityPassed
                ? RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate
                : RetrievalDatasetV2ShadowEvalRecommendations.BlockedByPgVectorParityMismatch,
            Profiles = [profile]
        };
    }

    private static RetrievalDatasetV2ValidationReport BuildReport(
        IReadOnlyList<VectorReindexSourceItem> corpusItems,
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<ContextRelation> relations)
    {
        return new RetrievalDatasetV2MetadataContractRunner().Validate(corpusItems, samples, relations);
    }

    private static VectorReindexSourceItem Source(
        string itemId,
        string lifecycle = "Stable",
        string targetSection = VectorQueryTargetSections.NormalContext,
        Dictionary<string, string>? metadata = null)
    {
        var values = metadata is null
            ? ValidMetadata(lifecycle, targetSection, "source-a", "evidence-a", "provenance-a")
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

        return new VectorReindexSourceItem
        {
            ItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            Text = "neutral content",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = values
        };
    }

    private static ContextEvalSample Sample(
        string sampleId,
        string query,
        IReadOnlyList<string> mustHit,
        IReadOnlyList<string>? mustNot = null,
        Dictionary<string, string>? metadata = null)
    {
        var values = metadata is null
            ? ValidMetadata("Stable", VectorQueryTargetSections.NormalContext, "sample-source-a", "sample-evidence-a", "sample-provenance-a")
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

        return new ContextEvalSample
        {
            Id = sampleId,
            Query = query,
            Mode = "TestMode",
            MustHit = mustHit,
            MustNotHit = mustNot ?? Array.Empty<string>(),
            Metadata = values
        };
    }

    private static Dictionary<string, string> ValidMetadata(
        string lifecycle,
        string targetSection,
        string sourceRefs,
        string evidenceRefs,
        string provenanceRecordId)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["split"] = "test",
            ["sourceRefs"] = sourceRefs,
            ["evidenceRefs"] = evidenceRefs,
            ["provenanceRecordId"] = provenanceRecordId,
            ["lifecycle"] = lifecycle,
            ["reviewStatus"] = "Approved",
            ["replacementState"] = "current",
            ["targetSection"] = targetSection
        };
    }

    private static string ResolveRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());return TestRepoFileResolver.Resolve(parts);}

    private static RetrievalDatasetV2Sample CopySample(
        RetrievalDatasetV2Sample sample,
        string queryText,
        IReadOnlyList<string>? mustHit = null)
    {
        return new RetrievalDatasetV2Sample
        {
            SampleId = sample.SampleId,
            TaskKind = sample.TaskKind,
            Intent = sample.Intent,
            QueryText = queryText,
            Difficulty = sample.Difficulty,
            ExpectedTargetSection = sample.ExpectedTargetSection,
            MustHitItemIds = mustHit ?? sample.MustHitItemIds,
            MustNotHitItemIds = sample.MustNotHitItemIds,
            Rationale = sample.Rationale,
            NegativeDistractorIds = sample.NegativeDistractorIds,
            RequiredRelations = sample.RequiredRelations,
            ExpectedLifecycleBehavior = sample.ExpectedLifecycleBehavior,
            Split = sample.Split,
            SourceRefs = sample.SourceRefs,
            EvidenceRefs = sample.EvidenceRefs,
            Provenance = sample.Provenance,
            Metadata = sample.Metadata
        };
    }

    private static RetrievalDatasetV2CorpusItem CopyCorpusItem(RetrievalDatasetV2CorpusItem item, string content)
    {
        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = item.ItemId,
            ItemKind = item.ItemKind,
            SourceKind = item.SourceKind,
            Layer = item.Layer,
            Lifecycle = item.Lifecycle,
            ReviewStatus = item.ReviewStatus,
            ReplacementState = item.ReplacementState,
            TargetSection = item.TargetSection,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = item.EvidenceRefs,
            Provenance = item.Provenance,
            SourceFingerprint = item.SourceFingerprint,
            CreatedAt = item.CreatedAt,
            Relations = item.Relations,
            Tags = item.Tags,
            Anchors = item.Anchors,
            Content = content,
            Split = item.Split,
            Metadata = item.Metadata
        };
    }

    private static RetrievalDatasetV2CorpusItem StressCorpus(
        string itemId,
        string content,
        string lifecycle = "Stable",
        string targetSection = VectorQueryTargetSections.NormalContext,
        string replacementState = "current",
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? anchors = null)
    {
        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = itemId,
            ItemKind = "note",
            SourceKind = "test-source",
            Layer = "context",
            Lifecycle = lifecycle,
            ReviewStatus = "Approved",
            ReplacementState = replacementState,
            TargetSection = targetSection,
            SourceRefs = ["source-a"],
            EvidenceRefs = ["evidence-a"],
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = $"prov-{itemId}",
                SourceFingerprint = $"fingerprint-{itemId}",
                IngestionBatchId = "stress-test"
            },
            SourceFingerprint = $"fingerprint-{itemId}",
            CreatedAt = DateTimeOffset.UtcNow,
            Tags = tags ?? Array.Empty<string>(),
            Anchors = anchors ?? tags ?? Array.Empty<string>(),
            Content = content,
            Split = "test",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "test",
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false"
            }
        };
    }

    private static RetrievalDatasetV2Sample StressSample(
        string sampleId,
        string query,
        IReadOnlyList<string> mustHit,
        IReadOnlyList<string>? mustNot = null)
    {
        return new RetrievalDatasetV2Sample
        {
            SampleId = sampleId,
            TaskKind = "retrieval-stress",
            Intent = "ContextRetrieval",
            QueryText = query,
            Difficulty = "direct_lexical",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = mustHit,
            MustNotHitItemIds = mustNot ?? Array.Empty<string>(),
            Rationale = "test rationale",
            Split = "test",
            SourceRefs = ["sample-source-a"],
            EvidenceRefs = ["sample-evidence-a"],
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = $"prov-{sampleId}",
                SourceFingerprint = $"fingerprint-{sampleId}",
                IngestionBatchId = "stress-test"
            }
        };
    }

    private static RetrievalDatasetV2GeneratedDataset RiskTriageDataset()
    {
        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems =
            [
                StressCorpus("bad", "alpha guidance noisy", tags: ["noisy"]),
                StressCorpus("must", "alpha guidance safe authoritative", tags: ["safe"])
            ],
            Samples =
            [
                StressSample("sample-risk", "alpha guidance", ["must"], mustNot: ["bad"])
            ]
        };
    }

    private static RetrievalDatasetV2GeneratedDataset ShadowPackageRegressionDataset()
    {
        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems =
            [
                ShadowPackageCorpus(
                    "aaa-covered",
                    "alpha",
                    sourceRefs: ["sample-source-a"],
                    evidenceRefs: ["sample-evidence-a"]),
                ShadowPackageCorpus(
                    "bbb-uncovered-1",
                    "alpha",
                    anchors: ["alpha", "budget-extra"],
                    sourceRefs: ["other-source"],
                    evidenceRefs: ["other-evidence"]),
                ShadowPackageCorpus(
                    "ccc-uncovered-2",
                    "alpha",
                    anchors: ["alpha", "budget-extra"],
                    sourceRefs: ["other-source"],
                    evidenceRefs: ["other-evidence"]),
                ShadowPackageCorpus(
                    "ddd-uncovered-3",
                    "alpha",
                    anchors: ["alpha", "budget-extra"],
                    sourceRefs: ["other-source"],
                    evidenceRefs: ["other-evidence"]),
                ShadowPackageCorpus(
                    "eee-uncovered-4",
                    "alpha",
                    anchors: ["alpha", "budget-extra"],
                    sourceRefs: ["other-source"],
                    evidenceRefs: ["other-evidence"]),
                ShadowPackageCorpus(
                    "fff-uncovered-5",
                    "alpha",
                    anchors: ["alpha", "budget-extra"],
                    sourceRefs: ["other-source"],
                    evidenceRefs: ["other-evidence"]),
                ShadowPackageCorpus(
                    "ggg-uncovered-6",
                    "alpha",
                    anchors: ["alpha", "budget-extra"],
                    sourceRefs: ["other-source"],
                    evidenceRefs: ["other-evidence"])
            ],
            Samples =
            [
                new RetrievalDatasetV2Sample
                {
                    SampleId = "sample-shadow-package-regression",
                    TaskKind = "retrieval-stress",
                    Intent = "ContextRetrieval",
                    QueryText = "alpha",
                    Difficulty = "metadata_anchor",
                    ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                    MustHitItemIds = ["aaa-covered"],
                    MustNotHitItemIds = [],
                    Rationale = "test rationale",
                    Split = "test",
                    SourceRefs = ["sample-source-a"],
                    EvidenceRefs = ["sample-evidence-a"],
                    Provenance = new RetrievalDatasetV2Provenance
                    {
                        RecordId = "prov-sample-shadow-package-regression",
                        SourceFingerprint = "fingerprint-sample-shadow-package-regression",
                        IngestionBatchId = "stress-test"
                    }
                }
            ]
        };
    }

    private static RetrievalDatasetV2CorpusItem ShadowPackageCorpus(
        string itemId,
        string content,
        IReadOnlyList<string>? anchors = null,
        IReadOnlyList<string>? sourceRefs = null,
        IReadOnlyList<string>? evidenceRefs = null)
        => new()
        {
            ItemId = itemId,
            ItemKind = "note",
            SourceKind = "test-source",
            Layer = "context",
            Lifecycle = "Stable",
            ReviewStatus = "Approved",
            ReplacementState = "current",
            TargetSection = VectorQueryTargetSections.NormalContext,
            SourceRefs = sourceRefs ?? ["source-a"],
            EvidenceRefs = evidenceRefs ?? ["evidence-a"],
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = $"prov-{itemId}",
                SourceFingerprint = $"fingerprint-{itemId}",
                IngestionBatchId = "stress-test"
            },
            SourceFingerprint = $"fingerprint-{itemId}",
            CreatedAt = DateTimeOffset.UtcNow,
            Tags = [],
            Anchors = anchors ?? [],
            Content = content,
            Split = "test",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "test",
                ["useForRuntime"] = "false"
            }
        };
}
