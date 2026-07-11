using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    public void RetrievalDatasetV2StressGenerator_ProducesHoldoutAndDifficultyCoverage()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = new RetrievalDatasetV2StressOptions
        {
            TargetCorpusItemCount = 120,
            TargetSampleCount = 120,
            DryRun = true,
            UseForRuntime = false
        };

        var dataset = runner.Generate(options);
        var validation = runner.Validate(dataset);
        var report = runner.BuildGenerationReport(options, dataset, validation);

        Assert.IsTrue(report.CorpusItemCount >= 100);
        Assert.IsTrue(report.SampleCount >= 100);
        Assert.IsTrue(report.SplitBreakdown.GetValueOrDefault("holdout") >= 10);
        Assert.IsTrue(report.DifficultyBreakdown.Values.All(static count => count >= 10));
        Assert.AreEqual(0, report.ValidationIssueCount);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressLeakageAudit_CatchesItemIdInQuery()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = new RetrievalDatasetV2StressOptions { TargetCorpusItemCount = 100, TargetSampleCount = 100 };
        var dataset = runner.Generate(options);
        var first = dataset.Samples[0];
        var leaked = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = dataset.CorpusItems,
            Samples = dataset.Samples
                .Select((sample, index) => index == 0 ? CopySample(sample, sample.QueryText + " " + first.MustHitItemIds[0]) : sample)
                .ToArray()
        };

        var report = runner.BuildLeakageAudit(options, leaked, runner.Validate(leaked));

        Assert.IsTrue(report.ItemIdLeakageCount > 0);
        Assert.AreEqual(RetrievalDatasetV2StressRecommendations.BlockedByLeakage, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressLeakageAudit_CatchesRationaleIndexedIntoCorpus()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = new RetrievalDatasetV2StressOptions { TargetCorpusItemCount = 100, TargetSampleCount = 100 };
        var dataset = runner.Generate(options);
        var sample = dataset.Samples[0];
        var leakedCorpus = dataset.CorpusItems
            .Select((item, index) => index == 0 ? CopyCorpusItem(item, item.Content + " " + sample.Rationale) : item)
            .ToArray();
        var leaked = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = leakedCorpus,
            Samples = dataset.Samples
        };

        var report = runner.BuildLeakageAudit(options, leaked, runner.Validate(leaked));

        Assert.IsTrue(report.RationaleLeakageCount > 0);
        Assert.AreEqual(RetrievalDatasetV2StressRecommendations.BlockedByLeakage, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressLeakageAudit_CatchesUniqueTagShortcut()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = new RetrievalDatasetV2StressOptions { TargetCorpusItemCount = 100, TargetSampleCount = 100 };
        var dataset = runner.Generate(options);
        var mustHit = dataset.CorpusItems.First(item => string.Equals(item.ItemId, dataset.Samples[0].MustHitItemIds[0], StringComparison.OrdinalIgnoreCase));
        var uniqueTag = mustHit.Metadata["uniqueSourceTag"];
        var leaked = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = dataset.CorpusItems,
            Samples = dataset.Samples
                .Select((sample, index) => index == 0 ? CopySample(sample, sample.QueryText + " " + uniqueTag) : sample)
                .ToArray()
        };

        var report = runner.BuildLeakageAudit(options, leaked, runner.Validate(leaked));

        Assert.IsTrue(report.UniqueAnchorLeakageCount > 0);
        Assert.AreEqual(RetrievalDatasetV2StressRecommendations.BlockedByLeakage, report.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressValidator_CatchesSplitLeakage()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = new RetrievalDatasetV2StressOptions { TargetCorpusItemCount = 100, TargetSampleCount = 100 };
        var dataset = runner.Generate(options);
        var trainItem = dataset.CorpusItems.First(static item => string.Equals(item.Split, "train", StringComparison.OrdinalIgnoreCase));
        var holdoutSample = dataset.Samples.First(static sample => string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase));
        var leaked = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = dataset.CorpusItems,
            Samples = dataset.Samples
                .Select(sample => string.Equals(sample.SampleId, holdoutSample.SampleId, StringComparison.OrdinalIgnoreCase)
                    ? CopySample(sample, sample.QueryText, mustHit: [trainItem.ItemId])
                    : sample)
                .ToArray()
        };

        var validation = runner.Validate(leaked);

        Assert.IsTrue(validation.SplitIsolationViolationCount > 0);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressShadowEval_HoldoutSeparatedAndAnchorShuffleBounded()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = new RetrievalDatasetV2StressOptions { TargetCorpusItemCount = 120, TargetSampleCount = 120 };
        var dataset = runner.Generate(options);
        var validation = runner.Validate(dataset);
        var report = runner.BuildShadowEval(options, dataset, validation, materializationGatePassed: true);
        var holdout = dataset.Samples.Count(static sample => string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase));
        var holdoutProfile = report.Profiles.First(static profile => string.Equals(profile.ProfileName, "hybrid-on-holdout-only", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(holdout, holdoutProfile.SampleCount);
        Assert.IsTrue(report.AnchorShuffleRecallDelta >= 0);
        Assert.IsTrue(report.AnchorShuffleRecallDelta <= 1);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressReadiness_RiskBlocksGate()
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var report = new RetrievalDatasetV2StressReport
        {
            CorpusItemCount = 120,
            SampleCount = 120,
            SplitBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["holdout"] = 24 },
            DifficultyBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["direct_lexical"] = 120 },
            HybridRecall = 1,
            HoldoutHybridRecall = 1,
            RiskAfterPolicy = 1,
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var gate = runner.BuildReadinessGate(new RetrievalDatasetV2StressOptions(), report);

        Assert.AreEqual(RetrievalDatasetV2StressRecommendations.BlockedByRisk, gate.Recommendation);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFailureTriage_MissingCandidateClassified()
    {
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = [StressCorpus("item-a", "unrelated content", tags: ["alpha"])],
            Samples = [StressSample("sample-a", "zz yy", ["item-a"])]
        };

        var report = new RetrievalDatasetV2StressRecallFailureTriageRunner().BuildReport(dataset);

        Assert.AreEqual(1, report.FailureCount);
        Assert.AreEqual(RetrievalDatasetV2StressFailureReasons.MustHitMissingFromCandidateSet, report.Failures[0].FailureReason);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFailureTriage_BelowTopKClassified()
    {
        var corpus = Enumerable.Range(0, 6)
            .Select(index => StressCorpus($"wrong-{index}", "shared signal alpha", tags: ["shared"]))
            .Append(StressCorpus("z-must", "shared signal alpha", tags: ["shared"]))
            .ToArray();
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = [StressSample("sample-a", "shared signal alpha", ["z-must"])]
        };

        var report = new RetrievalDatasetV2StressRecallFailureTriageRunner().BuildReport(dataset);

        Assert.AreEqual(RetrievalDatasetV2StressFailureReasons.MustHitBelowTopK, report.Failures[0].FailureReason);
        Assert.AreEqual(1, report.MustHitBelowTopKCount);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFailureTriage_EligibilityBlockedClassified()
    {
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems =
            [
                StressCorpus(
                    "item-a",
                    "blocked lifecycle signal",
                    lifecycle: "Deprecated",
                    replacementState: "superseded",
                    tags: ["blocked"])
            ],
            Samples = [StressSample("sample-a", "blocked lifecycle signal", ["item-a"])]
        };

        var report = new RetrievalDatasetV2StressRecallFailureTriageRunner().BuildReport(dataset);

        Assert.AreEqual(RetrievalDatasetV2StressFailureReasons.MustHitBlockedByEligibility, report.Failures[0].FailureReason);
        Assert.AreEqual(1, report.EligibilityBlockedCount);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFailureTriage_AnchorRegressionClassified()
    {
        var wrong = Enumerable.Range(0, 6)
            .Select(index => StressCorpus($"wrong-{index}", "common alpha beta gamma delta", tags: ["common"]))
            .ToArray();
        var dataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = wrong.Append(StressCorpus("must", "minor", tags: ["anchorx"], anchors: ["anchorx"])).ToArray(),
            Samples = [StressSample("sample-a", "anchorx common alpha beta gamma delta", ["must"])]
        };

        var report = new RetrievalDatasetV2StressRecallFailureTriageRunner().BuildReport(dataset);

        Assert.AreEqual(RetrievalDatasetV2StressFailureReasons.AnchorRankingRegression, report.Failures[0].FailureReason);
        Assert.AreEqual(1, report.AnchorRegressionCount);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFailureTriage_HybridUnionRegressionClassified()
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

        var report = new RetrievalDatasetV2StressRecallFailureTriageRunner().BuildReport(dataset);

        Assert.AreEqual(RetrievalDatasetV2StressFailureReasons.HybridUnionRankingRegression, report.Failures[0].FailureReason);
        Assert.IsTrue(report.DenseOnlyWinCount > 0);
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
    public void HybridUnionScoringRepair_ContributionAwareRerankIsDeterministic()
    {
        var dataset = new RetrievalDatasetV2StressRunner().Generate(new RetrievalDatasetV2StressOptions
        {
            TargetCorpusItemCount = 120,
            TargetSampleCount = 120
        });
        var runner = new HybridUnionScoringRepairRunner();

        var first = runner.BuildPreview(dataset).Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.ContributionAwareRerankV1);
        var second = runner.BuildPreview(dataset).Profiles.First(static profile => profile.ProfileName == HybridUnionScoringRepairProfiles.ContributionAwareRerankV1);

        Assert.AreEqual(first.RecallAfterPolicy, second.RecallAfterPolicy);
        Assert.AreEqual(first.HoldoutRecallAfterPolicy, second.HoldoutRecallAfterPolicy);
        Assert.AreEqual(first.DenseWinnerLostCount, second.DenseWinnerLostCount);
        Assert.AreEqual(first.NegativeDistractorOutranksMustHitCount, second.NegativeDistractorOutranksMustHitCount);
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
    public void HybridScoringRiskTriage_BlockedCandidateReintroducedClassified()
    {
        var reason = HybridScoringRiskRegressionTriageRunner.ClassifyRiskReasonForDiagnostics(
            wasBlockedBeforeRepair: true,
            isMustNotCandidate: false,
            lifecycle: "Deprecated",
            replacementState: "superseded",
            targetSection: VectorQueryTargetSections.NormalContext,
            expectedTargetSection: VectorQueryTargetSections.NormalContext,
            scoreBeforeRepair: 0,
            scoreAfterRepair: 1,
            profileName: HybridUnionScoringRepairProfiles.CombinedSafeV1);

        Assert.AreEqual(HybridScoringRiskRegressionReasons.BlockedCandidateReintroduced, reason);
    }

    [TestMethod]
    public void HybridScoringRiskTriage_MustNotCandidatePromotedClassified()
    {
        var dataset = RiskTriageDataset();

        var report = new HybridScoringRiskRegressionTriageRunner().BuildReport(
            dataset,
            profileName: HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1);

        Assert.IsTrue(report.RiskCandidateCount > 0);
        Assert.IsTrue(report.MustNotCandidatePromotedCount > 0);
        Assert.IsTrue(report.RiskByType.ContainsKey("MustNotHitRisk"));
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void HybridScoringRiskTriage_RiskProjectionMismatchClassified()
    {
        var dataset = RiskTriageDataset();

        var report = new HybridScoringRiskRegressionTriageRunner().BuildReport(
            dataset,
            profileName: HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1,
            expectedRiskCount: 99);

        Assert.AreEqual(1, report.RiskProjectionMismatchCount);
        Assert.AreEqual(HybridScoringRiskRegressionRecommendations.NeedsRiskProjectionFix, report.Recommendation);
    }

    [TestMethod]
    public void HybridScoringRiskTriage_PostScoringRiskGateWouldBlockUnsafeTopK()
    {
        var dataset = RiskTriageDataset();

        var report = new HybridScoringRiskRegressionTriageRunner().BuildReport(
            dataset,
            profileName: HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1);

        Assert.AreEqual(report.RiskCandidateCount, report.RepairableByPostScoringRiskGateCount);
        Assert.AreEqual(HybridScoringRiskRegressionRecommendations.NeedsPostScoringRiskGate, report.Recommendation);
    }

    [TestMethod]
    public void HybridScoringRiskTriage_PostScoringRiskGatedProfileHasNoRisk()
    {
        var dataset = RiskTriageDataset();

        var report = new HybridScoringRiskRegressionTriageRunner().BuildReport(
            dataset,
            profileName: HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1);

        Assert.AreEqual(0, report.RiskCandidateCount);
        Assert.AreEqual(HybridScoringRiskRegressionRecommendations.ReadyForSafeScoringRepair, report.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void HybridScoringRiskTriage_ScoringPathDoesNotReadEvalLabelsOrFixtureLexicon()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Evaluation",
            "Vector",
            "Evaluation",
            "V5",
            "HybridScoringRiskRegressionTriageRunner.cs");
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
            Assert.IsFalse(scoringSource.Contains(forbidden, StringComparison.Ordinal), $"Risk triage scoring path must not read eval label: {forbidden}");
        }

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixture/domain keyword: {forbidden}");
        }
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFreeze_CleanReportsPassAsV4RecheckInputOnly()
    {
        var report = BuildStressFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeStatuses.ReadyForV4RecheckInput, report.DatasetV2Stress);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeRecommendations.ReadyForV4RecheckInput, report.Recommendation);
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.BestPreviewProfile);
        Assert.IsTrue(report.V4RecheckAllowed);
        Assert.IsFalse(report.ReadyForFormalRetrieval);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFreeze_LeakageBlocksFreeze()
    {
        var report = BuildStressFreezeReport(leakageIssueCount: 1);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeRecommendations.BlockedByLeakage, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "LeakageIssueCountNonZero");
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFreeze_RiskBlocksFreeze()
    {
        var report = BuildStressFreezeReport(riskAfterPolicy: 1);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskAfterPolicyNonZero");
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFreeze_HybridScoringRiskBlocksFreeze()
    {
        var report = BuildStressFreezeReport(hybridScoringRiskCandidateCount: 1);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeRecommendations.BlockedByHybridScoringRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "HybridScoringRiskCandidateCountNonZero");
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFreeze_FormalOutputChangedBlocksFreeze()
    {
        var report = BuildStressFreezeReport(formalOutputChanged: 1);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeRecommendations.BlockedByFormalOutputChange, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "HybridScoringRepairFormalOutputChanged");
    }

    [TestMethod]
    public void RetrievalDatasetV2StressFreeze_MissingReportBlocksFreeze()
    {
        var report = new RetrievalDatasetV2StressFreezeRunner().BuildReport(
            materializationGate: null,
            smallSetReadinessGate: CleanSmallSetReadinessGate(),
            stressReadinessGate: CleanStressReadinessGate(),
            leakageAudit: CleanStressReadinessGate(),
            anchorDominanceAudit: CleanStressReadinessGate(),
            stressFailureTriage: CleanStressFailureTriage(),
            hybridScoringRepairGate: CleanHybridRepairGate(),
            hybridScoringRiskTriage: CleanHybridScoringRiskTriage());

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(RetrievalDatasetV2StressFreezeRecommendations.BlockedByMissingReport, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingMaterializationGateReport");
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
    public void GuardedFormalRetrievalPreview_CleanReportsReadyForShadowPackageComparison()
    {
        var report = BuildGuardedFormalRetrievalPreviewReport();

        Assert.IsTrue(report.PreviewPassed);
        Assert.AreEqual(GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison, report.Recommendation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.PackageOutputChanged);
    }

    [TestMethod]
    public void GuardedFormalRetrievalPreview_V4RecheckNotPassedBlocks()
    {
        var report = BuildGuardedFormalRetrievalPreviewReport(
            v4Recheck: BuildV4ReadinessRecheckReport(stressFreeze: BuildStressFreezeReport(riskAfterPolicy: 1)));

        Assert.IsFalse(report.PreviewPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V4RecheckNotPassed");
    }

    [TestMethod]
    public void GuardedFormalRetrievalPreview_RiskBlocks()
    {
        var report = BuildGuardedFormalRetrievalPreviewReport(
            riskTriage: CleanHybridScoringRiskTriage(riskCandidateCount: 1));

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(GuardedFormalRetrievalPreviewRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "HybridScoringRiskTriageNotClean");
    }

    [TestMethod]
    public void GuardedFormalRetrievalPreview_FormalOutputChangeBlocks()
    {
        var report = BuildGuardedFormalRetrievalPreviewReport(
            repairGate: CleanHybridRepairGate(formalOutputChanged: 1));

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(GuardedFormalRetrievalPreviewRecommendations.BlockedByFormalOutputChange, report.Recommendation);
        Assert.AreEqual(1, report.FormalOutputChanged);
    }

    [TestMethod]
    public void GuardedFormalRetrievalPreview_RuntimeSwitchAttemptBlocks()
    {
        var report = BuildGuardedFormalRetrievalPreviewReport(
            options: new GuardedFormalRetrievalPreviewOptions
            {
                Enabled = true,
                ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
                UseForRuntime = true,
                FormalRetrievalAllowed = false
            });

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(GuardedFormalRetrievalPreviewRecommendations.BlockedByRuntimeSwitchAttempt, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAttempt");
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
    public void VectorShadowPackageComparison_CleanReportsReadyForScopedFormalPreviewOptIn()
    {
        var report = BuildVectorShadowPackageComparisonReport();

        Assert.IsTrue(report.ComparisonPassed);
        Assert.AreEqual(VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn, report.Recommendation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void VectorShadowPackageComparison_GuardedPreviewGateNotPassedBlocks()
    {
        var report = BuildVectorShadowPackageComparisonReport(
            guardedGate: CleanGuardedFormalPreviewGate(gatePassed: false));

        Assert.IsFalse(report.ComparisonPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "GuardedFormalRetrievalPreviewGateNotPassed");
    }

    [TestMethod]
    public void VectorShadowPackageComparison_RiskBlocks()
    {
        var report = BuildVectorShadowPackageComparisonReport(
            guardedGate: CleanGuardedFormalPreviewGate(riskAfterPolicy: 1));

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(VectorShadowPackageComparisonRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskAfterPolicyNonZero");
    }

    [TestMethod]
    public void VectorShadowPackageComparison_FormalOutputChangeBlocks()
    {
        var report = BuildVectorShadowPackageComparisonReport(
            guardedGate: CleanGuardedFormalPreviewGate(formalOutputChanged: 1));

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(VectorShadowPackageComparisonRecommendations.BlockedByFormalOutputChange, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalOutputChangedNonZero");
    }

    [TestMethod]
    public void VectorShadowPackageComparison_RuntimeMutationBlocks()
    {
        var report = BuildVectorShadowPackageComparisonReport(
            options: new VectorShadowPackageComparisonOptions
            {
                Enabled = true,
                ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
                UseForRuntime = true,
                FormalRetrievalAllowed = false
            });

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(VectorShadowPackageComparisonRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationAttempt");
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void VectorShadowPackageComparison_TokenBudgetRegressionIsReported()
    {
        var report = BuildVectorShadowPackageComparisonReport(dataset: ShadowPackageRegressionDataset());

        Assert.IsTrue(report.TokenDeltaMax > 0);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
    }

    [TestMethod]
    public void VectorShadowPackageComparison_ConstraintCoverageRegressionBlocks()
    {
        var report = BuildVectorShadowPackageComparisonReport(dataset: ShadowPackageRegressionDataset());

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(VectorShadowPackageComparisonRecommendations.BlockedByConstraintCoverageRegression, report.Recommendation);
        Assert.IsTrue(report.ConstraintCoverageDelta < 0);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ConstraintCoverageRegression");
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
    public void ScopedFormalPreviewOptIn_DefaultOffKeepsPreviewOnly()
    {
        var report = new ScopedFormalPreviewOptInRunner().BuildPlan(
            BuildV4ReadinessRecheckReport(),
            CleanGuardedFormalPreviewGate(),
            CleanVectorShadowPackageComparisonGate());

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly, report.Recommendation);
        Assert.AreEqual(ScopedFormalPreviewOptInModes.Off, report.Mode);
        Assert.AreEqual(0, report.PreviewPackageCount);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_MissingV42GateBlocks()
    {
        var report = new ScopedFormalPreviewOptInRunner().BuildGate(
            BuildV4ReadinessRecheckReport(),
            CleanGuardedFormalPreviewGate(),
            shadowPackageGate: null,
            CleanScopedFormalPreviewOptions());

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.BlockedByMissingGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ShadowPackageComparisonGateNotPassed");
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_AllowlistedScopeGeneratesPreviewOnly()
    {
        var report = BuildScopedFormalPreviewOptInReport(stage: "smoke");

        Assert.IsTrue(report.SmokePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation, report.Recommendation);
        Assert.AreEqual(1, report.AllowlistedScopeCount);
        Assert.IsTrue(report.PreviewPackageCount > 0);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_NonAllowlistedScopeRemainsBaseline()
    {
        var report = BuildScopedFormalPreviewOptInReport(stage: "smoke");

        Assert.IsTrue(report.NonAllowlistedScopeChecked);
        Assert.AreEqual(0, report.NonAllowlistedScopeLeakCount);
        Assert.IsTrue(report.BaselinePackageCount > 0);
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_FormalPackageWriteAttemptBlocks()
    {
        var report = BuildScopedFormalPreviewOptInReport(
            options: CleanScopedFormalPreviewOptions(writeFormalPackage: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWriteAttempt");
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_RuntimeMutationBlocks()
    {
        var report = BuildScopedFormalPreviewOptInReport(
            options: CleanScopedFormalPreviewOptions(useForRuntime: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationAttempt");
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_PackingPolicyChangeBlocks()
    {
        var report = BuildScopedFormalPreviewOptInReport(
            shadowPackageGate: CleanVectorShadowPackageComparisonGate(packingPolicyChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.BlockedByPackingPolicyChange, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyChanged");
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_PackageOutputChangeBlocks()
    {
        var report = BuildScopedFormalPreviewOptInReport(
            shadowPackageGate: CleanVectorShadowPackageComparisonGate(packageOutputChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.BlockedByPackageOutputChange, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOutputChanged");
    }

    [TestMethod]
    public void ScopedFormalPreviewOptIn_ScopeLeakBlocks()
    {
        var report = BuildScopedFormalPreviewOptInReport(
            options: CleanScopedFormalPreviewOptions(includeNonAllowlistedInAllowlist: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedFormalPreviewOptInRecommendations.BlockedByScopeLeak, report.Recommendation);
        Assert.AreEqual(1, report.NonAllowlistedScopeLeakCount);
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
    public void LimitedFormalPreviewObservation_CleanReportsReadyForFormalPreviewFreeze()
    {
        var report = BuildLimitedFormalPreviewObservationReport(stage: "gate");

        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze, report.Recommendation);
        Assert.AreEqual(3, report.ObservationRunCount);
        Assert.AreEqual(360, report.PreviewPackageCount);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_MissingV43GateBlocks()
    {
        var report = new LimitedFormalPreviewObservationRunner().BuildGate(
            scopedOptInGate: null,
            CleanVectorShadowPackageComparisonGate(),
            CleanLimitedFormalPreviewObservationOptions());

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ScopedFormalPreviewOptInGateNotPassed");
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_RiskBlocksGate()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            shadowPackageGate: CleanVectorShadowPackageComparisonGate(riskAfterPolicy: 1));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByRisk, report.Recommendation);
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_FormalOutputChangedBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            shadowPackageGate: CleanVectorShadowPackageComparisonGate(formalOutputChanged: 1));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByFormalOutputChange, report.Recommendation);
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_PackageOutputChangedBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            shadowPackageGate: CleanVectorShadowPackageComparisonGate(packageOutputChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByPackageOutputChange, report.Recommendation);
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_PackingPolicyChangedBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            shadowPackageGate: CleanVectorShadowPackageComparisonGate(packingPolicyChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByPackingPolicyChange, report.Recommendation);
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_FormalPackageWriteBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            options: CleanLimitedFormalPreviewObservationOptions(writeFormalPackage: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWritten");
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_RuntimeMutationBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            options: CleanLimitedFormalPreviewObservationOptions(useForRuntime: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutated");
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_ScopeLeakBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            scopedGate: BuildScopedFormalPreviewOptInReport(options: CleanScopedFormalPreviewOptions(includeNonAllowlistedInAllowlist: true)));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.BlockedByScopeLeak, report.Recommendation);
        Assert.AreEqual(1, report.NonAllowlistedScopeLeakCount);
    }

    [TestMethod]
    public void LimitedFormalPreviewObservation_InsufficientRunsBlocks()
    {
        var report = BuildLimitedFormalPreviewObservationReport(
            options: CleanLimitedFormalPreviewObservationOptions(observationRuns: 0));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(LimitedFormalPreviewObservationRecommendations.NeedsMoreObservation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "InsufficientObservationRuns");
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
    public void VectorFormalPreviewFreeze_CleanReportsReadyForScopedOptInPreview()
    {
        var report = BuildVectorFormalPreviewFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeStatuses.ReadyForScopedOptInPreview, report.VectorFormalPreview);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview, report.Recommendation);
        Assert.AreEqual("ScopedPreviewOnly", report.AllowedMode);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_MissingV44GateBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(limitedGate: null, includeLimitedGate: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByMissingGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "LimitedFormalPreviewObservationGateNotPassed");
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_RiskBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(riskAfterPolicy: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByRisk, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_FormalOutputChangedBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(formalOutputChanged: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByFormalOutputChange, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_PackageOutputChangedBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(packageOutputChanged: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByPackageOutputChange, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_PackingPolicyChangedBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(packingPolicyChanged: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByPackingPolicyChange, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_FormalPackageWriteBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(formalPackageWritten: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByFormalPackageWrite, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_RuntimeMutationBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(runtimeMutated: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByRuntimeMutation, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_ScopeLeakBlocksFreeze()
    {
        var report = BuildVectorFormalPreviewFreezeReport(
            limitedGate: CleanLimitedFormalPreviewObservationGate(scopeLeakCount: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(VectorFormalPreviewFreezeRecommendations.BlockedByScopeLeak, report.Recommendation);
    }

    [TestMethod]
    public void VectorFormalPreviewFreeze_DoesNotAllowRuntimeSwitch()
    {
        var report = BuildVectorFormalPreviewFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        CollectionAssert.Contains(report.ForbiddenChanges.ToList(), "RuntimeSwitch");
        CollectionAssert.Contains(report.ForbiddenChanges.ToList(), "FormalPackageWrite");
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
    public void FoundationReproducibility_CleanReportsPass()
    {
        var report = BuildFoundationReproducibilityReport();

        Assert.IsTrue(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.ReadyForReleaseCandidateReproduction,
            report.Recommendation);
        Assert.AreEqual("Passed", report.FoundationGateStatus);
        Assert.AreEqual("Passed", report.RuntimeChangeGateStatus);
        Assert.AreEqual("Passed", report.P15GateStatus);
        Assert.IsFalse(report.LocalSecretsDetected);
    }

    [TestMethod]
    public void FoundationReproducibility_MissingFoundationGateBlocks()
    {
        var report = BuildFoundationReproducibilityReport(
            foundationGate: null,
            includeFoundationGate: false,
            criticalReportCoverage: CleanReproducibilityCoverage("foundation/foundation-release-candidate-gate.json"));

        Assert.IsFalse(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.BlockedByMissingFoundationGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FoundationReleaseCandidateGateMissingOrFailed");
    }

    [TestMethod]
    public void FoundationReproducibility_MissingRuntimeChangeGateBlocks()
    {
        var report = BuildFoundationReproducibilityReport(
            runtimeGate: null,
            includeRuntimeGate: false,
            criticalReportCoverage: CleanReproducibilityCoverage("learning/readiness/learning-runtime-change-readiness-gate.json"));

        Assert.IsFalse(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.BlockedByMissingRuntimeChangeGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeGateMissingOrFailed");
    }

    [TestMethod]
    public void FoundationReproducibility_FormalRetrievalAllowedBlocks()
    {
        var report = BuildFoundationReproducibilityReport(
            foundationGate: BuildFoundationFreezeReport(
                vectorFormal: CleanVectorFormalPreviewFreezeReport(formalRetrievalAllowed: true)));

        Assert.IsFalse(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.BlockedByFormalRetrieval,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalRetrievalAllowed");
    }

    [TestMethod]
    public void FoundationReproducibility_RuntimeSwitchAllowedBlocks()
    {
        var report = BuildFoundationReproducibilityReport(
            foundationGate: BuildFoundationFreezeReport(
                vectorFormal: CleanVectorFormalPreviewFreezeReport(readyForRuntimeSwitch: true)));

        Assert.IsFalse(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.BlockedByRuntimeSwitch,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAllowed");
    }

    [TestMethod]
    public void FoundationReproducibility_LocalSecretsDetectedBlocks()
    {
        var categories = CleanGitStatusCategories();
        categories["local config / secrets"] = ["appsettings.Postgres.local.json", "src/secret.secrets.json"];

        var report = BuildFoundationReproducibilityReport(gitStatusCategories: categories);

        Assert.IsFalse(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.BlockedByLocalSecret,
            report.Recommendation);
        Assert.IsTrue(report.LocalSecretsDetected);
        Assert.AreEqual(2, report.LocalSecretPathCount);
    }

    [TestMethod]
    public void FoundationReproducibility_P15FailureBlocks()
    {
        var report = BuildFoundationReproducibilityReport(
            p15A3: new P15ReportStatus(false, 50, 1, 0, "Loaded"));

        Assert.IsFalse(report.ReproducibilityPassed);
        Assert.AreEqual(
            FoundationReproducibilityRecommendations.BlockedByP15Gate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "P15GateMissingOrFailed");
    }

    [TestMethod]
    public void ServiceFoundationStatusSmoke_RuntimeMutationBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var clean = CleanFoundationServiceStatusResponse();
        var mutated = CleanFoundationServiceStatusResponse(runtimeMutated: true);

        var report = service.BuildSmokeReport(clean, clean, clean, clean, clean, mutated);

        Assert.IsFalse(report.SmokePassed);
        Assert.AreEqual("BlockedByReadOnlyStatusMismatch", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutated");
    }

    [TestMethod]
    public void FoundationApiSecurityDiagnostics_ShouldDetectSecretAndAbsolutePathLeaks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());

        var report = service.BuildSecurityDiagnostics(
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
    public void ServiceReportNavigationSmoke_AbsolutePathBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var navigation = new FoundationApiResponseEnvelope<FoundationReportNavigationResponse>
        {
            Status = "Ready",
            Recommendation = "ReadyForReadOnlyReportNavigation",
            Data = new FoundationReportNavigationResponse
            {
                ReportCount = 1,
                ExistingReportCount = 1,
                Reports =
                [
                    new FoundationReportNavigationEntry
                    {
                        ReportId = "bad",
                        CapabilityId = "bad",
                        RelativePath = @"D:\\unsafe\\report.json",
                        Exists = true,
                        SafeToExpose = false
                    }
                ]
            }
        };
        var entry = new FoundationApiResponseEnvelope<FoundationReportNavigationEntry>
        {
            Status = "Ready",
            Recommendation = "ReadyForReadOnlyReportNavigation",
            Data = navigation.Data.Reports[0]
        };

        var report = service.BuildReportNavigationSmokeReport(navigation, entry);

        Assert.IsFalse(report.SmokePassed);
        Assert.IsTrue(report.AbsolutePathLeakDetected);
    }

    [TestMethod]
    public void FoundationApiContractReport_DevelopmentAuthNotConfiguredIsExplicitAndAllowed()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildContractReport(
            CleanFoundationServiceStatusResponse(),
            CleanReportNavigationEnvelope(),
            CleanMissingReportProbeEnvelope(),
            new FoundationApiSecurityDiagnosticsReport
            {
                AuthConfigured = false,
                ApiKeyConfigured = false,
                DevelopmentMode = true,
                Recommendation = "DevelopmentOnly"
            },
            productionMode: false);

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual("ReadyForServiceApiContractFreeze", report.Recommendation);
        Assert.AreEqual("DevelopmentOnly", report.AuthMode);
        Assert.AreEqual(8, report.EndpointCount);
        Assert.AreEqual(8, report.ClientMethodCount);
        Assert.AreEqual("foundation-api-envelope-v1", report.EnvelopeSchemaVersion);
        Assert.IsTrue(report.DegradedBehaviorStable);
        Assert.IsTrue(report.ForbiddenActionsExposed);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "FormalPackageWrite");
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "PackingPolicyMutation");
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "PackageOutputMutation");
    }

    [TestMethod]
    public void FoundationApiContractReport_ProductionAuthMissingBlocksFreeze()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildContractReport(
            CleanFoundationServiceStatusResponse(),
            CleanReportNavigationEnvelope(),
            CleanMissingReportProbeEnvelope(),
            new FoundationApiSecurityDiagnosticsReport
            {
                AuthConfigured = false,
                ApiKeyConfigured = false,
                DevelopmentMode = false,
                Recommendation = "NotConfigured"
            },
            productionMode: true);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByAuthNotConfigured", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ProductionAuthNotConfigured");
    }

    [TestMethod]
    public void FoundationApiContractReport_RuntimeBoundaryViolationBlocksFreeze()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildContractReport(
            CleanFoundationServiceStatusResponse(runtimeSwitchAllowed: true, formalRetrievalAllowed: true),
            CleanReportNavigationEnvelope(),
            CleanMissingReportProbeEnvelope(),
            new FoundationApiSecurityDiagnosticsReport
            {
                AuthConfigured = true,
                ApiKeyConfigured = true,
                Recommendation = "ReadyForReadOnlyServiceExposure"
            },
            productionMode: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByForbiddenActionExposure", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAllowed");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalRetrievalAllowed");
    }

    [TestMethod]
    public void FoundationApiContractReport_SecretOrAbsolutePathLeakBlocksFreeze()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildContractReport(
            CleanFoundationServiceStatusResponse(),
            CleanReportNavigationEnvelope(),
            CleanMissingReportProbeEnvelope(),
            new FoundationApiSecurityDiagnosticsReport
            {
                AuthConfigured = true,
                ApiKeyConfigured = true,
                SecretLeakDetected = true,
                AbsolutePathLeakDetected = true,
                Recommendation = "NotConfigured"
            },
            productionMode: false);

        Assert.IsFalse(report.FreezePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SecretLeakDetected");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AbsolutePathLeakDetected");
    }

    [TestMethod]
    public void FoundationServiceAuthDiagnostics_DevelopmentNoAuthAllowedButExplicit()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions
            {
                DeploymentProfile = ServiceDeploymentProfile.Development,
                RequireApiKey = false,
                AllowDevelopmentNoAuth = true
            },
            apiKeyConfigured: false,
            serializedResponses: ["{}"]);

        Assert.IsFalse(report.AuthConfigured);
        Assert.IsTrue(report.DevelopmentNoAuthAllowed);
        Assert.AreEqual("DevelopmentOnly", report.Recommendation);
        CollectionAssert.Contains(report.Diagnostics.ToList(), "DevelopmentOnlyAuthDisabled");
        Assert.AreEqual(0, report.BlockedReasons.Count);
    }

    [TestMethod]
    public void FoundationServiceAuthDiagnostics_ServiceMissingApiKeyBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions
            {
                DeploymentProfile = ServiceDeploymentProfile.Service,
                RequireApiKey = true
            },
            apiKeyConfigured: false,
            serializedResponses: ["{}"]);

        Assert.IsFalse(report.AuthConfigured);
        Assert.AreEqual("BlockedByMissingApiKey", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApiKeyRequiredButMissing");
    }

    [TestMethod]
    public void FoundationServiceAuthDiagnostics_ProductionMissingAuthBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions
            {
                DeploymentProfile = ServiceDeploymentProfile.Production,
                RequireApiKey = true
            },
            apiKeyConfigured: false,
            serializedResponses: ["{}"]);

        Assert.AreEqual("BlockedByProductionAuthMissing", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ProductionAuthNotConfigured");
    }

    [TestMethod]
    public void FoundationServiceAuthDiagnostics_SecretAndPathLeaksBlock()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions
            {
                DeploymentProfile = ServiceDeploymentProfile.Service,
                RequireApiKey = true
            },
            apiKeyConfigured: true,
            serializedResponses: [@"D:\\unsafe\\secrets.json secret-value"],
            secretProbe: "secret-value");

        Assert.IsTrue(report.SecretLeakDetected);
        Assert.IsTrue(report.AbsolutePathLeakDetected);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SecretLeakDetected");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AbsolutePathLeakDetected");
    }

    [TestMethod]
    public void FoundationServiceAuthEnforcementSmoke_AllExpectedScenariosPass()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var development = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions { DeploymentProfile = ServiceDeploymentProfile.Development, RequireApiKey = false },
            false,
            ["{}"]);
        var serviceMissing = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions { DeploymentProfile = ServiceDeploymentProfile.Service, RequireApiKey = true },
            false,
            ["{}"]);
        var serviceConfigured = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions { DeploymentProfile = ServiceDeploymentProfile.Service, RequireApiKey = true },
            true,
            ["{}"]);
        var productionMissing = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions { DeploymentProfile = ServiceDeploymentProfile.Production, RequireApiKey = true },
            false,
            ["{}"]);

        var report = service.BuildAuthEnforcementSmokeReport(
            development,
            serviceMissing,
            serviceConfigured,
            productionMissing,
            wrongApiKeyUnauthorized: true,
            correctApiKeyAvailable: true);

        Assert.IsTrue(report.SmokePassed);
        Assert.AreEqual("ReadyForDeploymentProfileGate", report.Recommendation);
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void FoundationServiceDeploymentProfileGate_UsesDiagnosticsBlockers()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var diagnostics = service.BuildAuthDiagnostics(
            new FoundationServiceAuthOptions
            {
                DeploymentProfile = ServiceDeploymentProfile.Production,
                RequireApiKey = true
            },
            apiKeyConfigured: false,
            serializedResponses: ["{}"]);

        var gate = service.BuildDeploymentProfileGateReport(diagnostics);

        Assert.IsFalse(gate.GatePassed);
        Assert.AreEqual("BlockedByProductionAuthMissing", gate.Recommendation);
        CollectionAssert.Contains(gate.BlockedReasons.ToList(), "ProductionAuthNotConfigured");
    }

    [TestMethod]
    public void FoundationOpenApiContract_ContainsAllReadOnlyEndpointsAndEnvelopeSchema()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var diagnostics = CleanFoundationServiceAuthDiagnostics();
        var openApi = service.BuildOpenApiDocument(diagnostics);
        var apiSnapshot = service.BuildApiContractSnapshot(diagnostics);
        var clientSnapshot = service.BuildClientContractSnapshot();
        var report = service.BuildOpenApiContractReport(openApi, apiSnapshot, clientSnapshot);

        Assert.AreEqual(8, report.EndpointCount);
        Assert.AreEqual("foundation-api-envelope-v1", report.EnvelopeSchemaVersion);
        Assert.AreEqual("ApiKeyAuth", report.AuthScheme);
        Assert.IsFalse(report.BreakingChangeDetected);
        Assert.AreEqual("ReadyForOpenApiContractFreeze", report.Recommendation);
        CollectionAssert.Contains(report.EndpointIds.ToList(), "GET /api/admin/foundation/status");
        CollectionAssert.Contains(report.EndpointIds.ToList(), "GET /api/admin/foundation/reports/{reportId}");
        Assert.IsTrue(openApi["components"]?["schemas"]?.AsObject().ContainsKey("FoundationApiResponseEnvelope") == true);
        Assert.IsTrue(openApi["components"]?["schemas"]?.AsObject().ContainsKey("CapabilityStatus") == true);
        Assert.IsTrue(openApi["components"]?["securitySchemes"]?.AsObject().ContainsKey("ApiKeyAuth") == true);
    }

    [TestMethod]
    public void FoundationClientContractSnapshot_ContainsPrimaryAndAliasMethods()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var snapshot = service.BuildClientContractSnapshot();

        CollectionAssert.Contains(snapshot.Methods.Select(static item => item.MethodName).ToList(), "GetFoundationStatusAsync");
        CollectionAssert.Contains(snapshot.Methods.Select(static item => item.MethodName).ToList(), "GetFoundationReportAsync");
        CollectionAssert.Contains(snapshot.AliasMethods.Select(static item => item.MethodName).ToList(), "GetFoundationReleaseCandidateStatusAsync");
        CollectionAssert.Contains(snapshot.AliasMethods.Select(static item => item.MethodName).ToList(), "GetFoundationRuntimeChangeGateStatusAsync");
        Assert.IsTrue(snapshot.Methods.All(static item => item.DeserializesEnvelope));
        Assert.IsTrue(snapshot.ReadOnly);
    }

    [TestMethod]
    public void FoundationOpenApiDriftGate_CatchesMissingEndpoint()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var diagnostics = CleanFoundationServiceAuthDiagnostics();
        var openApi = service.BuildOpenApiDocument(diagnostics);
        openApi["paths"]!.AsObject().Remove("/api/admin/foundation/status");

        var report = service.BuildOpenApiContractReport(
            openApi,
            service.BuildApiContractSnapshot(diagnostics),
            service.BuildClientContractSnapshot());

        Assert.IsTrue(report.BreakingChangeDetected);
        Assert.AreEqual("BlockedByBreakingChange", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "EndpointDeleted");
    }

    [TestMethod]
    public void FoundationOpenApiDriftGate_CatchesEnvelopeSchemaMismatch()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var diagnostics = CleanFoundationServiceAuthDiagnostics();
        var snapshot = service.BuildApiContractSnapshot(diagnostics);
        var mutatedSnapshot = new FoundationApiContractSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            GeneratedAt = snapshot.GeneratedAt,
            SchemaVersion = snapshot.SchemaVersion,
            EnvelopeSchemaFields = snapshot.EnvelopeSchemaFields.Where(static item => item != "Diagnostics").ToArray(),
            Endpoints = snapshot.Endpoints,
            CapabilityStatusSchemaFields = snapshot.CapabilityStatusSchemaFields,
            ReportNavigationSchemaFields = snapshot.ReportNavigationSchemaFields,
            ForbiddenActions = snapshot.ForbiddenActions,
            AuthScheme = snapshot.AuthScheme,
            ApiKeyHeaderName = snapshot.ApiKeyHeaderName
        };

        var report = service.BuildOpenApiContractReport(
            service.BuildOpenApiDocument(diagnostics),
            mutatedSnapshot,
            service.BuildClientContractSnapshot());

        Assert.IsTrue(report.BreakingChangeDetected);
        Assert.AreEqual("BlockedByEnvelopeSchemaMismatch", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "EnvelopeSchemaMismatch");
    }

    [TestMethod]
    public void FoundationOpenApiDriftGate_CatchesAuthDowngrade()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var diagnostics = CleanFoundationServiceAuthDiagnostics();
        var snapshot = service.BuildApiContractSnapshot(diagnostics);
        var downgraded = new FoundationApiContractSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            GeneratedAt = snapshot.GeneratedAt,
            SchemaVersion = snapshot.SchemaVersion,
            EnvelopeSchemaFields = snapshot.EnvelopeSchemaFields,
            Endpoints = snapshot.Endpoints,
            CapabilityStatusSchemaFields = snapshot.CapabilityStatusSchemaFields,
            ReportNavigationSchemaFields = snapshot.ReportNavigationSchemaFields,
            ForbiddenActions = snapshot.ForbiddenActions,
            AuthScheme = "None",
            ApiKeyHeaderName = snapshot.ApiKeyHeaderName
        };

        var report = service.BuildOpenApiContractReport(
            service.BuildOpenApiDocument(diagnostics),
            downgraded,
            service.BuildClientContractSnapshot());

        Assert.IsTrue(report.BreakingChangeDetected);
        Assert.AreEqual("BlockedByAuthDowngrade", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AuthSchemeDowngrade");
    }

    [TestMethod]
    public void FoundationOpenApiDriftGate_BlocksSecretAndAbsolutePathLeak()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var diagnostics = CleanFoundationServiceAuthDiagnostics();
        var snapshot = service.BuildApiContractSnapshot(diagnostics);
        var leakingSnapshot = new FoundationApiContractSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            GeneratedAt = snapshot.GeneratedAt,
            SchemaVersion = snapshot.SchemaVersion,
            EnvelopeSchemaFields = snapshot.EnvelopeSchemaFields,
            Endpoints = snapshot.Endpoints,
            CapabilityStatusSchemaFields = snapshot.CapabilityStatusSchemaFields,
            ReportNavigationSchemaFields = snapshot.ReportNavigationSchemaFields,
            ForbiddenActions = snapshot.ForbiddenActions,
            AuthScheme = snapshot.AuthScheme,
            ApiKeyHeaderName = @"C:\\unsafe\\.contextcore\\secrets.json"
        };

        var report = service.BuildOpenApiContractReport(
            service.BuildOpenApiDocument(diagnostics),
            leakingSnapshot,
            service.BuildClientContractSnapshot());

        Assert.IsTrue(report.BreakingChangeDetected);
        Assert.IsTrue(report.SecretLeakDetected);
        Assert.IsTrue(report.AbsolutePathLeakDetected);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SecretLeakDetected");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AbsolutePathLeakDetected");
    }

    [TestMethod]
    public void HostedServiceSmoke_NotConfiguredGivesClearStatus()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var report = service.BuildHostedServiceSmokeReport(
            new HostedServiceSmokeOptions { Enabled = false },
            Array.Empty<HostedServiceEndpointProbeResult>(),
            authPassed: false,
            unauthorizedCheckPassed: false);

        Assert.IsFalse(report.SmokePassed);
        Assert.AreEqual("NeedsHostedServiceConfig", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "HostedServiceNotConfigured");
    }

    [TestMethod]
    public void HostedServiceSmoke_CleanReadOnlyResponsesPass()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var results = service.GetFoundationEndpointContracts()
            .Select(static endpoint => CleanHostedEndpoint(endpoint))
            .ToArray();
        var report = service.BuildHostedServiceSmokeReport(
            CleanHostedOptions(),
            results,
            authPassed: true,
            unauthorizedCheckPassed: true);

        Assert.IsTrue(report.SmokePassed);
        Assert.AreEqual("ReadyForHostedReadOnlyService", report.Recommendation);
        Assert.AreEqual(8, report.SuccessfulEndpointCount);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void HostedServiceSmoke_AuthFailureBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var results = service.GetFoundationEndpointContracts()
            .Select(static endpoint => CleanHostedEndpoint(endpoint))
            .ToArray();
        var report = service.BuildHostedServiceSmokeReport(
            CleanHostedOptions(requireApiKey: true),
            results,
            authPassed: false,
            unauthorizedCheckPassed: false);

        Assert.IsFalse(report.SmokePassed);
        Assert.AreEqual("BlockedByAuthFailure", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AuthFailure");
    }

    [TestMethod]
    public void HostedServiceSmoke_EnvelopeMismatchBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var results = service.GetFoundationEndpointContracts()
            .Select(endpoint => CleanHostedEndpoint(endpoint, envelopeSchemaMatched: false))
            .ToArray();
        var report = service.BuildHostedServiceSmokeReport(
            CleanHostedOptions(),
            results,
            authPassed: true,
            unauthorizedCheckPassed: true);

        Assert.IsFalse(report.SmokePassed);
        Assert.AreEqual("BlockedByContractMismatch", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "EnvelopeSchemaMismatch");
    }

    [TestMethod]
    public void HostedServiceSmoke_RuntimeMutationBlocks()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var results = service.GetFoundationEndpointContracts()
            .Select(endpoint => CleanHostedEndpoint(endpoint, runtimeMutated: endpoint.Route.EndsWith("/status", StringComparison.Ordinal)))
            .ToArray();
        var report = service.BuildHostedServiceSmokeReport(
            CleanHostedOptions(),
            results,
            authPassed: true,
            unauthorizedCheckPassed: true);

        Assert.IsFalse(report.SmokePassed);
        Assert.AreEqual("BlockedByRuntimeMutation", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutated");
    }

    [TestMethod]
    public void HostedServiceSmoke_SecretAndAbsolutePathLeakBlock()
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        var results = service.GetFoundationEndpointContracts()
            .Select(endpoint => CleanHostedEndpoint(endpoint, secretLeakDetected: true, absolutePathLeakDetected: true))
            .ToArray();
        var report = service.BuildHostedServiceSmokeReport(
            CleanHostedOptions(),
            results,
            authPassed: true,
            unauthorizedCheckPassed: true);

        Assert.IsFalse(report.SmokePassed);
        Assert.AreEqual("BlockedBySecretLeak", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SecretLeakDetected");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AbsolutePathLeakDetected");
    }

    [TestMethod]
    public void ServiceFoundationFreeze_CleanReportsPass()
    {
        var report = BuildServiceFoundationFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual("Frozen", report.ServiceFoundation);
        Assert.AreEqual("ReadyForV45ExplicitScopedRuntimeExperimentPlanning", report.Recommendation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.RuntimeMutationAllowed);
    }

    [TestMethod]
    public void ServiceFoundationFreeze_MissingHostedSmokeBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(includeHosted: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByHostedSmoke", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingHostedDeploymentSmoke");
    }

    [TestMethod]
    public void ServiceFoundationFreeze_ContractDriftBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(drift: CleanOpenApiContractReport(breakingChangeDetected: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByContractDrift", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "Svc5OpenApiContractSnapshotNotPassed");
    }

    [TestMethod]
    public void ServiceFoundationFreeze_AuthDeploymentFailureBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(deployment: CleanDeploymentGate(gatePassed: false));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByAuthDeployment", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "Svc4AuthDeploymentProfileNotPassed");
    }

    [TestMethod]
    public void ServiceFoundationFreeze_RuntimeMutationBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(
            hosted: CleanHostedSmokeReport(runtimeMutated: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByRuntimeMutation", report.Recommendation);
        Assert.IsTrue(report.RuntimeMutationAllowed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationDetected");
    }

    [TestMethod]
    public void ServiceFoundationFreeze_FormalRetrievalAllowedBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(
            hosted: CleanHostedSmokeReport(formalRetrievalAllowed: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByFormalRetrieval", report.Recommendation);
        Assert.IsTrue(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void ServiceFoundationFreeze_RuntimeSwitchAllowedBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(
            hosted: CleanHostedSmokeReport(runtimeSwitchAllowed: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByRuntimeSwitch", report.Recommendation);
        Assert.IsTrue(report.RuntimeSwitchAllowed);
    }

    [TestMethod]
    public void ServiceFoundationFreeze_P15FailureBlocks()
    {
        var report = BuildServiceFoundationFreezeReport(p15Passed: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual("BlockedByP15", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "P15GateNotPassed");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_DefaultDisabledKeepsPreviewOnly()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(options: new ExplicitScopedRuntimeExperimentPlanOptions());

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(ExplicitScopedRuntimeExperimentRecommendations.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ExplicitScopedRuntimeExperimentPlanningDisabled");
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_CleanReportsPassDryRunGate()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(stage: "gate");

        Assert.IsTrue(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.ReadyForExplicitScopedRuntimeExperimentDryRun,
            report.Recommendation);
        Assert.AreEqual(1, report.AllowlistedScopeCount);
        Assert.IsTrue(report.NonAllowlistedScopeChecked);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.FormalPackageWritten);
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_MissingFoundationFreezeBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            foundation: null,
            includeFoundation: false);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByMissingFoundationFreeze,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FoundationFreezeOrReproducibilityGateNotPassed");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_MissingServiceFreezeBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            service: null,
            includeService: false);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByMissingServiceFreeze,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ServiceFoundationFreezeGateNotPassed");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_MissingVectorFormalPreviewFreezeBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            vectorFormal: null,
            includeVectorFormal: false);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByMissingFoundationFreeze,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorFormalPreviewFreezeGateNotPassed");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_MissingSelectedScopeBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            options: CleanExplicitScopedRuntimeExperimentOptions(includeScopes: false));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.NeedsScopeConfiguration,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SelectedScopeNotConfigured");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_RuntimeSwitchAttemptBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            options: CleanExplicitScopedRuntimeExperimentOptions(readyForRuntimeSwitch: true));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchOrMutationAttempt");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_FormalRetrievalEnableBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            options: CleanExplicitScopedRuntimeExperimentOptions(formalRetrievalAllowed: true));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchOrMutationAttempt");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_FormalPackageWriteBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            options: CleanExplicitScopedRuntimeExperimentOptions(writeFormalPackage: true));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWriteAttempt");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_PackingPolicyChangeBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            shadowGate: CleanVectorShadowPackageComparisonGate(packingPolicyChanged: true));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyChanged");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_NonAllowlistedScopeLeakBlocks()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(
            scopedGate: BuildScopedFormalPreviewOptInReport(
                options: CleanScopedFormalPreviewOptions(includeNonAllowlistedInAllowlist: true)));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentRecommendations.BlockedByScopeLeak,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NonAllowlistedScopeLeak");
    }

    [TestMethod]
    public void ExplicitScopedRuntimeExperiment_DryRunMutatesNothing()
    {
        var report = BuildExplicitScopedRuntimeExperimentReport(stage: "dry-run");

        Assert.IsTrue(report.PlanPassed);
        Assert.IsTrue(report.DryRunSupported);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.FormalOutputChanged);
    }
    [TestMethod]
    public void ScopedRuntimeExperimentProposal_CleanReportsReadyForManualApproval()
    {
        var report = BuildScopedRuntimeExperimentProposalReport();

        Assert.IsTrue(report.ProposalPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.ReadyForManualExperimentApproval,
            report.Recommendation);
        Assert.IsTrue(report.ApprovalRequired);
        Assert.IsFalse(report.Approved);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.WriteFormalPackage);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_MissingV47DesignFreezeBlocks()
    {
        var report = BuildScopedRuntimeExperimentProposalReport(includeDesignFreeze: false);

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByMissingGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ScopedRuntimeExperimentDesignFreezeGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_MissingScopeBlocks()
    {
        var report = BuildScopedRuntimeExperimentProposalReport(
            options: CleanScopedRuntimeExperimentProposalOptions(workspaceId: string.Empty));

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.NeedsScopeConfiguration,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SelectedScopeNotConfigured");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_MissingRollbackPlanBlocks()
    {
        var report = BuildScopedRuntimeExperimentProposalReport(
            options: CleanScopedRuntimeExperimentProposalOptions(rollbackPlan: string.Empty));

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByMissingRollbackPlan,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RollbackPlanMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_MissingKillSwitchBlocks()
    {
        var report = BuildScopedRuntimeExperimentProposalReport(
            options: CleanScopedRuntimeExperimentProposalOptions(killSwitchPlan: string.Empty));

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByMissingKillSwitch,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "KillSwitchPlanMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_RuntimeSwitchAttemptBlocks()
    {
        var report = BuildScopedRuntimeExperimentProposalReport(
            options: CleanScopedRuntimeExperimentProposalOptions(useForRuntime: true));

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAttempt");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_ConfigPreviewDoesNotWriteRuntimeConfig()
    {
        var report = BuildScopedRuntimeExperimentProposalReport();

        Assert.IsTrue(report.ProposedConfigPatch.Count > 0);
        Assert.AreEqual("none", report.ProposedConfigPatch["writeTarget"]);
        Assert.AreEqual("false", report.ProposedConfigPatch["useForRuntime"]);
        Assert.IsFalse(report.ConfigPatchWritten);
        Assert.IsFalse(report.DiBindingChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.PackageOutputChanged);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentProposal_CannotMarkApprovedAutomatically()
    {
        var report = BuildScopedRuntimeExperimentProposalReport(
            options: CleanScopedRuntimeExperimentProposalOptions(approved: true));

        Assert.IsFalse(report.ProposalPassed);
        Assert.IsFalse(report.Approved);
        Assert.AreEqual(
            ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AutomaticApprovalAttempt");
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
    [TestMethod]
    public void FormalRetrievalIntegrationPlan_CleanPromotionReadyForShadowAdapter()
    {
        var report = BuildFormalRetrievalIntegrationPlanReport();

        Assert.IsTrue(report.PlanPassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationPlanRecommendations.ReadyForShadowFormalRetrievalAdapter,
            report.Recommendation);
        Assert.AreEqual(FormalRetrievalIntegrationPlanModes.PlanOnly, report.AllowedMode);
        Assert.AreEqual("ShadowFormalRetrievalAdapter", report.RequiredNextPhase);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_MissingPromotionBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            null,
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationPlanRecommendations.BlockedByMissingPromotionDecision,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V416PromotionDecisionNotPassed");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_P15FailureBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(),
            CleanRuntimeChangeGate(true),
            p15GatePassed: false);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(FormalRetrievalIntegrationPlanRecommendations.BlockedByP15Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "P15GateNotPassed");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_RuntimeGateFailureBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(),
            CleanRuntimeChangeGate(false),
            p15GatePassed: true);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(FormalRetrievalIntegrationPlanRecommendations.BlockedByRuntimeChangeGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeReadinessGateNotPassed");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_FormalOutputMutationBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(formalOutputChanged: 1),
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationPlanRecommendations.BlockedByFormalOutputMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalOutputMutationDetected");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_PackageMutationBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(packageOutputChanged: true),
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationPlanRecommendations.BlockedByPackageOutputMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOutputMutationDetected");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_PackingPolicyMutationBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(packingPolicyChanged: true),
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationPlanRecommendations.BlockedByPackingPolicyMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyMutationDetected");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationPlan_VectorBindingMutationBlocks()
    {
        var report = new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(vectorStoreBindingChanged: true),
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationPlanRecommendations.BlockedByVectorBindingMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorStoreBindingMutationDetected");
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
    private static FormalRetrievalIntegrationPlanReport BuildFormalRetrievalIntegrationPlanReport()
        => new FormalRetrievalIntegrationPlanRunner().BuildPlan(
            CleanV416PromotionDecision(),
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

    private static ScopedRuntimeExperimentObservationFreezeReport CleanV416PromotionDecision(
        int formalOutputChanged = 0,
        bool packageOutputChanged = false,
        bool packingPolicyChanged = false,
        bool vectorStoreBindingChanged = false)
        => new()
        {
            OperationId = "v416-clean",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = true,
            PromotionDecision = ScopedRuntimeExperimentObservationFreezeDecisions.ReadyForFormalRetrievalIntegrationPlan,
            Recommendation = ScopedRuntimeExperimentObservationFreezeDecisions.ReadyForFormalRetrievalIntegrationPlan,
            ObservationWindowId = "vsreow-clean",
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalId = "vsrea-clean",
            V414GatePassed = true,
            V415GatePassed = true,
            RuntimeChangeGatePassed = true,
            P15GatePassed = true,
            ObservationRunCount = 3,
            RequestCount = 360,
            ExperimentRouteHitCount = 360,
            NonAllowlistedScopeLeakCount = 0,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = false,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            FormalPackageWritten = false,
            KillSwitchAvailable = true,
            KillSwitchSmokePassed = true,
            RollbackVerified = true,
            TraceCompleteness = 100,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
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

    private static GuardedFormalRetrievalPreviewReport BuildGuardedFormalRetrievalPreviewReport(
        VectorV4ReadinessRecheckReport? v4Recheck = null,
        RetrievalDatasetV2StressFreezeReport? stressFreeze = null,
        HybridUnionScoringRepairReport? repairGate = null,
        HybridScoringRiskRegressionTriageReport? riskTriage = null,
        GuardedFormalRetrievalPreviewOptions? options = null)
        => new GuardedFormalRetrievalPreviewRunner().BuildPreview(
            RiskTriageDataset(),
            v4Recheck ?? BuildV4ReadinessRecheckReport(),
            stressFreeze ?? BuildStressFreezeReport(),
            repairGate ?? CleanHybridRepairGate(),
            riskTriage ?? CleanHybridScoringRiskTriage(),
            options ?? new GuardedFormalRetrievalPreviewOptions
            {
                Enabled = true,
                ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            });

    private static VectorShadowPackageComparisonReport BuildVectorShadowPackageComparisonReport(
        RetrievalDatasetV2GeneratedDataset? dataset = null,
        GuardedFormalRetrievalPreviewReport? guardedGate = null,
        VectorShadowPackageComparisonOptions? options = null)
        => new VectorShadowPackageComparisonRunner().BuildComparison(
            dataset ?? RiskTriageDataset(),
            guardedGate ?? CleanGuardedFormalPreviewGate(),
            options ?? new VectorShadowPackageComparisonOptions
            {
                Enabled = true,
                ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            });

    private static ScopedFormalPreviewOptInReport BuildScopedFormalPreviewOptInReport(
        string stage = "gate",
        VectorV4ReadinessRecheckReport? v4Recheck = null,
        GuardedFormalRetrievalPreviewReport? guardedGate = null,
        VectorShadowPackageComparisonReport? shadowPackageGate = null,
        ScopedFormalPreviewOptInOptions? options = null)
    {
        var runner = new ScopedFormalPreviewOptInRunner();
        return stage switch
        {
            "plan" => runner.BuildPlan(
                v4Recheck ?? BuildV4ReadinessRecheckReport(),
                guardedGate ?? CleanGuardedFormalPreviewGate(),
                shadowPackageGate ?? CleanVectorShadowPackageComparisonGate(),
                options ?? CleanScopedFormalPreviewOptions()),
            "smoke" => runner.BuildSmoke(
                v4Recheck ?? BuildV4ReadinessRecheckReport(),
                guardedGate ?? CleanGuardedFormalPreviewGate(),
                shadowPackageGate ?? CleanVectorShadowPackageComparisonGate(),
                options ?? CleanScopedFormalPreviewOptions()),
            _ => runner.BuildGate(
                v4Recheck ?? BuildV4ReadinessRecheckReport(),
                guardedGate ?? CleanGuardedFormalPreviewGate(),
                shadowPackageGate ?? CleanVectorShadowPackageComparisonGate(),
                options ?? CleanScopedFormalPreviewOptions())
        };
    }

    private static LimitedFormalPreviewObservationReport BuildLimitedFormalPreviewObservationReport(
        string stage = "gate",
        ScopedFormalPreviewOptInReport? scopedGate = null,
        VectorShadowPackageComparisonReport? shadowPackageGate = null,
        LimitedFormalPreviewObservationOptions? options = null)
    {
        var runner = new LimitedFormalPreviewObservationRunner();
        return string.Equals(stage, "observation", StringComparison.OrdinalIgnoreCase)
            ? runner.BuildObservation(
                scopedGate ?? BuildScopedFormalPreviewOptInReport(),
                shadowPackageGate ?? CleanVectorShadowPackageComparisonGate(),
                options ?? CleanLimitedFormalPreviewObservationOptions())
            : runner.BuildGate(
                scopedGate ?? BuildScopedFormalPreviewOptInReport(),
                shadowPackageGate ?? CleanVectorShadowPackageComparisonGate(),
                options ?? CleanLimitedFormalPreviewObservationOptions());
    }

    private static VectorFormalPreviewFreezeReport BuildVectorFormalPreviewFreezeReport(
        VectorV4ReadinessRecheckReport? v4Recheck = null,
        GuardedFormalRetrievalPreviewReport? guardedGate = null,
        VectorShadowPackageComparisonReport? shadowGate = null,
        ScopedFormalPreviewOptInReport? scopedGate = null,
        LimitedFormalPreviewObservationReport? limitedGate = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        bool includeLimitedGate = true)
        => new VectorFormalPreviewFreezeRunner().BuildGate(
            v4Recheck ?? BuildV4ReadinessRecheckReport(),
            guardedGate ?? CleanGuardedFormalPreviewGate(),
            shadowGate ?? CleanVectorShadowPackageComparisonGate(),
            scopedGate ?? BuildScopedFormalPreviewOptInReport(),
            includeLimitedGate ? limitedGate ?? CleanLimitedFormalPreviewObservationGate() : null,
            runtimeGate ?? new LearningRuntimeChangeReadinessGateReport { Passed = true });

    private static ExplicitScopedRuntimeExperimentPlanReport BuildExplicitScopedRuntimeExperimentReport(
        string stage = "gate",
        ContextCoreFoundationFreezeReport? foundation = null,
        FoundationReproducibilityReport? reproducibility = null,
        ServiceFoundationFreezeReport? service = null,
        VectorFormalPreviewFreezeReport? vectorFormal = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        GuardedFormalRetrievalPreviewReport? guardedGate = null,
        VectorShadowPackageComparisonReport? shadowGate = null,
        ScopedFormalPreviewOptInReport? scopedGate = null,
        LimitedFormalPreviewObservationReport? limitedGate = null,
        ExplicitScopedRuntimeExperimentPlanOptions? options = null,
        bool includeFoundation = true,
        bool includeService = true,
        bool includeVectorFormal = true)
    {
        var runner = new ExplicitScopedRuntimeExperimentPlanRunner();
        var effectiveOptions = options ?? CleanExplicitScopedRuntimeExperimentOptions(
            mode: string.Equals(stage, "plan", StringComparison.OrdinalIgnoreCase)
                ? ExplicitScopedRuntimeExperimentModes.PlanOnly
                : ExplicitScopedRuntimeExperimentModes.DryRun);
        return stage.ToLowerInvariant() switch
        {
            "plan" => runner.BuildPlan(
                includeFoundation ? foundation ?? BuildFoundationFreezeReport() : null,
                reproducibility ?? BuildFoundationReproducibilityReport(),
                includeService ? service ?? BuildServiceFoundationFreezeReport() : null,
                includeVectorFormal ? vectorFormal ?? CleanVectorFormalPreviewFreezeReport() : null,
                runtimeGate ?? CleanRuntimeChangeGate(true),
                guardedGate ?? CleanGuardedFormalPreviewGate(),
                shadowGate ?? CleanVectorShadowPackageComparisonGate(),
                scopedGate ?? BuildScopedFormalPreviewOptInReport(),
                limitedGate ?? CleanLimitedFormalPreviewObservationGate(),
                effectiveOptions),
            "dry-run" => runner.BuildDryRun(
                includeFoundation ? foundation ?? BuildFoundationFreezeReport() : null,
                reproducibility ?? BuildFoundationReproducibilityReport(),
                includeService ? service ?? BuildServiceFoundationFreezeReport() : null,
                includeVectorFormal ? vectorFormal ?? CleanVectorFormalPreviewFreezeReport() : null,
                runtimeGate ?? CleanRuntimeChangeGate(true),
                guardedGate ?? CleanGuardedFormalPreviewGate(),
                shadowGate ?? CleanVectorShadowPackageComparisonGate(),
                scopedGate ?? BuildScopedFormalPreviewOptInReport(),
                limitedGate ?? CleanLimitedFormalPreviewObservationGate(),
                effectiveOptions),
            _ => runner.BuildGate(
                includeFoundation ? foundation ?? BuildFoundationFreezeReport() : null,
                reproducibility ?? BuildFoundationReproducibilityReport(),
                includeService ? service ?? BuildServiceFoundationFreezeReport() : null,
                includeVectorFormal ? vectorFormal ?? CleanVectorFormalPreviewFreezeReport() : null,
                runtimeGate ?? CleanRuntimeChangeGate(true),
                guardedGate ?? CleanGuardedFormalPreviewGate(),
                shadowGate ?? CleanVectorShadowPackageComparisonGate(),
                scopedGate ?? BuildScopedFormalPreviewOptInReport(),
                limitedGate ?? CleanLimitedFormalPreviewObservationGate(),
                effectiveOptions)
        };
    }
    private static VectorV4ReadinessRecheckReport BuildV4ReadinessRecheckReport(
        RetrievalDatasetV2StressFreezeReport? stressFreeze = null,
        bool includeStressFreeze = true,
        bool runtimeGatePassed = true,
        bool pgVectorParityPassed = true)
    {
        var stress = includeStressFreeze ? stressFreeze ?? BuildStressFreezeReport() : null;
        var blocked = new List<string>();

        if (stress is null)
        {
            blocked.Add("MissingDatasetV2StressFreezeGate");
        }
        else if (stress.RiskAfterPolicy != 0
            || stress.MustNotHitRiskAfterPolicy != 0
            || stress.LifecycleRiskAfterPolicy != 0
            || stress.HybridScoringRiskCandidateCount != 0)
        {
            blocked.Add("DatasetV2StressRiskNonZero");
        }

        if (!runtimeGatePassed)
        {
            blocked.Add("RuntimeChangeGateFailed");
        }

        if (!pgVectorParityPassed)
        {
            blocked.Add("PgVectorProviderParityNotReady");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;

        string recommendation;
        if (passed)
        {
            recommendation = VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview;
        }
        else if (distinctBlocked.Any(static r => r.Contains("RuntimeChangeGate", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = VectorV4ReadinessRecheckRecommendations.BlockedByRuntimeChangeGate;
        }
        else if (distinctBlocked.Any(static r => r.Contains("PgVector", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Parity", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = VectorV4ReadinessRecheckRecommendations.BlockedByProviderParity;
        }
        else if (distinctBlocked.Any(static r => r.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = VectorV4ReadinessRecheckRecommendations.BlockedByRisk;
        }
        else
        {
            recommendation = VectorV4ReadinessRecheckRecommendations.BlockedByDatasetV2Stress;
        }

        return new VectorV4ReadinessRecheckReport
        {
            OperationId = $"vector-v4-readiness-recheck-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            RecheckPassed = passed,
            Recommendation = recommendation,
            LegacyVectorStatus = "PreviewOnly / legacy limitations recorded",
            DatasetV2SmallStatus = "ReadyForDatasetV2RetrievalCandidate",
            DatasetV2StressStatus = stress?.DatasetV2Stress ?? "Missing",
            PgVectorProviderStatus = pgVectorParityPassed ? "ReadyForPreviewShadowStorage" : "Missing",
            Qwen3ProviderComparisonStatus = "Ready",
            HybridRetrievalStatus = "Ready",
            HybridScoringRepairStatus = HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze,
            RuntimeChangeGateStatus = runtimeGatePassed ? "Passed" : "Failed",
            BestPreviewProfile = stress?.BestPreviewProfile ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            DatasetV2StressRecall = stress?.StressRecall ?? 0,
            DatasetV2HoldoutRecall = stress?.HoldoutRecall ?? 0,
            RiskAfterPolicy = stress?.RiskAfterPolicy ?? 0,
            MustNotHitRiskAfterPolicy = stress?.MustNotHitRiskAfterPolicy ?? 0,
            LifecycleRiskAfterPolicy = stress?.LifecycleRiskAfterPolicy ?? 0,
            FormalOutputChanged = stress?.FormalOutputChanged ?? 0,
            LeakageIssueCount = stress?.LeakageIssueCount ?? 0,
            AnchorDominanceScore = stress?.AnchorDominanceScore ?? 0,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            ReadyForGuardedFormalPreview = passed,
            ReadyForRuntimeSwitch = false,
            BlockedReasons = distinctBlocked,
            SourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ScopedRuntimeExperimentDesignFreezeReport BuildScopedRuntimeExperimentDesignFreezeReport(
        ContextCoreFoundationFreezeReport? foundation = null,
        ServiceFoundationFreezeReport? service = null,
        VectorFormalPreviewFreezeReport? vectorFormal = null,
        ExplicitScopedRuntimeExperimentPlanReport? scopedRuntimeExperiment = null,
        ScopedRuntimeExperimentDryRunObservationReport? dryRunObservation = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        bool includeDryRunObservation = true,
        bool p15GatePassed = true)
    {
        var foundationActual = foundation ?? BuildFoundationFreezeReport();
        var serviceActual = service ?? BuildServiceFoundationFreezeReport();
        var vectorFormalActual = vectorFormal ?? CleanVectorFormalPreviewFreezeReport();
        var scopedActual = scopedRuntimeExperiment ?? CleanExplicitScopedRuntimeExperimentGate();
        var dryRunActual = includeDryRunObservation ? dryRunObservation ?? CleanScopedRuntimeExperimentDryRunObservationGate() : null;
        var runtimeGateActual = runtimeGate ?? CleanRuntimeChangeGate(true);

        var blocked = new List<string>();

        if (!foundationActual.FreezePassed
            || !string.Equals(foundationActual.Recommendation, ContextCoreFoundationFreezeRecommendations.ReadyForReleaseCandidate, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("FoundationReleaseCandidateGateNotPassed");
        }

        if (!serviceActual.FreezePassed)
        {
            blocked.Add("ServiceFoundationFreezeGateNotPassed");
        }

        if (!vectorFormalActual.FreezePassed
            || !string.Equals(vectorFormalActual.Recommendation, VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorFormalPreviewFreezeGateNotPassed");
        }

        if (!scopedActual.PlanPassed
            || !string.Equals(scopedActual.Recommendation, ExplicitScopedRuntimeExperimentRecommendations.ReadyForExplicitScopedRuntimeExperimentDryRun, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ScopedRuntimeExperimentGateNotPassed");
        }

        if (dryRunActual is null
            || !dryRunActual.GatePassed
            || !string.Equals(dryRunActual.Recommendation, ScopedRuntimeExperimentDryRunObservationRecommendations.ReadyForScopedRuntimeExperimentDesignFreeze, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("DryRunObservationGateNotPassed");
        }

        if (!runtimeGateActual.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!p15GatePassed)
        {
            blocked.Add("P15GateNotPassed");
        }

        var riskAfterPolicy = dryRunActual?.RiskAfterPolicy ?? 0;
        var mustNotRisk = dryRunActual?.MustNotHitRiskAfterPolicy ?? 0;
        var lifecycleRisk = dryRunActual?.LifecycleRiskAfterPolicy ?? 0;
        var formalOutputChanged = dryRunActual?.FormalOutputChanged ?? 0;
        var runtimeMutated = dryRunActual?.RuntimeMutated ?? false;
        var vectorStoreBindingChanged = dryRunActual?.VectorStoreBindingChanged ?? false;
        var packingPolicyChanged = dryRunActual?.PackingPolicyChanged ?? false;
        var packageOutputChanged = dryRunActual?.PackageOutputChanged ?? false;
        var formalPackageWritten = dryRunActual?.FormalPackageWritten ?? false;
        var nonAllowlistedScopeLeakCount = dryRunActual?.NonAllowlistedScopeLeakCount ?? 0;
        var rollbackPlanAvailable = dryRunActual?.RollbackPlanAvailable ?? false;

        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (formalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (runtimeMutated) blocked.Add("RuntimeMutated");
        if (vectorStoreBindingChanged) blocked.Add("VectorStoreBindingChanged");
        if (packingPolicyChanged) blocked.Add("PackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("PackageOutputChanged");
        if (formalPackageWritten) blocked.Add("FormalPackageWritten");
        if (nonAllowlistedScopeLeakCount != 0) blocked.Add("NonAllowlistedScopeLeak");
        if (!rollbackPlanAvailable) blocked.Add("RollbackPlanMissing");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var freezePassed = distinctBlocked.Length == 0;

        string recommendation;
        if (freezePassed)
        {
            recommendation = ScopedRuntimeExperimentDesignFreezeRecommendations.ReadyForRuntimeExperimentProposal;
        }
        else if (distinctBlocked.Any(static r => r.Contains("DryRunObservation", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByMissingDryRunObservation;
        }
        else if (distinctBlocked.Any(static r => r.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRisk;
        }
        else if (distinctBlocked.Any(static r => r.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByFormalOutputChange;
        }
        else if (distinctBlocked.Any(static r => r.Contains("RuntimeMutated", StringComparison.OrdinalIgnoreCase)
            || r.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            recommendation = ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRuntimeMutation;
        }
        else
        {
            recommendation = ScopedRuntimeExperimentDesignFreezeRecommendations.KeepPreviewOnly;
        }

        return new ScopedRuntimeExperimentDesignFreezeReport
        {
            OperationId = $"vector-scoped-runtime-experiment-design-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            Recommendation = recommendation,
            DesignStatus = freezePassed
                ? ScopedRuntimeExperimentDesignFreezeStatuses.Frozen
                : ScopedRuntimeExperimentDesignFreezeStatuses.KeepPreviewOnly,
            AllowedMode = "ExplicitScopedRuntimeExperimentOnly",
            AllowlistedScopeCount = dryRunActual?.AllowlistedScopeCount ?? scopedActual?.AllowlistedScopeCount ?? 0,
            ObservationRunCount = dryRunActual?.ObservationRunCount ?? 0,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            FormalPackageWritten = formalPackageWritten,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            RollbackPlanAvailable = rollbackPlanAvailable,
            ReadyForRuntimeExperimentProposal = freezePassed,
            ReadyForRuntimeSwitch = false,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            FormalPackageWriteAllowed = false,
            PackingPolicyIntegrationAllowed = false,
            GlobalDefaultOnAllowed = false,
            FoundationReleaseCandidateGatePassed = foundationActual.FreezePassed,
            ServiceFoundationFreezeGatePassed = serviceActual.FreezePassed,
            VectorFormalPreviewFreezeGatePassed = vectorFormalActual.FreezePassed,
            ScopedRuntimeExperimentGatePassed = scopedActual!.PlanPassed,
            DryRunObservationGatePassed = dryRunActual?.GatePassed ?? false,
            RuntimeChangeReadinessGatePassed = runtimeGateActual.Passed,
            P15GatePassed = p15GatePassed,
            AllowedActions =
            [
                "SelectedScopeExperimentPlanning",
                "SelectedScopeDryRunObservation",
                "SelectedScopeRuntimeExperimentProposal",
                "RollbackPlanValidation",
                "MetricsCollectionPlan"
            ],
            ForbiddenActions =
            [
                "GlobalRuntimeSwitch",
                "NonAllowlistedScopeUse",
                "FormalIVectorIndexStoreBinding",
                "FormalPackageWrite",
                "PackingPolicyMutation",
                "PackageOutputMutation",
                "DisablingRuntimeChangeGate",
                "FormalRetrievalWithoutExplicitLaterGate"
            ],
            BlockedReasons = distinctBlocked,
            SourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ExplicitScopedRuntimeExperimentProposalReport BuildScopedRuntimeExperimentProposalReport(
        ContextCoreFoundationFreezeReport? foundation = null,
        FoundationReproducibilityReport? reproducibility = null,
        ServiceFoundationFreezeReport? service = null,
        VectorFormalPreviewFreezeReport? vectorFormal = null,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        ExplicitScopedRuntimeExperimentProposalOptions? options = null,
        bool includeDesignFreeze = true)
        => new ExplicitScopedRuntimeExperimentProposalRunner().BuildGate(
            foundation ?? BuildFoundationFreezeReport(),
            reproducibility ?? BuildFoundationReproducibilityReport(),
            service ?? BuildServiceFoundationFreezeReport(),
            vectorFormal ?? CleanVectorFormalPreviewFreezeReport(),
            includeDesignFreeze ? designFreeze ?? BuildScopedRuntimeExperimentDesignFreezeReport() : null,
            runtimeGate ?? CleanRuntimeChangeGate(true),
            options ?? CleanScopedRuntimeExperimentProposalOptions());

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

    private static FoundationReproducibilityReport BuildFoundationReproducibilityReport(
        ContextCoreFoundationFreezeReport? foundationGate = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        bool includeFoundationGate = true,
        bool includeRuntimeGate = true,
        P15ReportStatus? p15A3 = null,
        P15ReportStatus? p15Extended = null,
        IReadOnlyDictionary<string, bool>? criticalReportCoverage = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? gitStatusCategories = null)
        => new FoundationReproducibilityRunner().BuildReport(
            includeFoundationGate ? foundationGate ?? BuildFoundationFreezeReport() : null,
            includeRuntimeGate ? runtimeGate ?? new LearningRuntimeChangeReadinessGateReport { Passed = true } : null,
            p15A3 ?? new P15ReportStatus(true, 50, 0, 0, "Loaded"),
            p15Extended ?? new P15ReportStatus(true, 113, 0, 0, "Loaded"),
            criticalReportCoverage ?? CleanReproducibilityCoverage(),
            gitStatusCategories ?? CleanGitStatusCategories());

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

    private static IReadOnlyDictionary<string, bool> CleanReproducibilityCoverage(string? missingPath = null)
    {
        var coverage = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundation/foundation-release-candidate-gate.md"] = true,
            ["foundation/foundation-release-candidate-gate.json"] = true,
            ["learning/readiness/learning-runtime-change-readiness-gate.md"] = true,
            ["learning/readiness/learning-runtime-change-readiness-gate.json"] = true,
            ["vector/v4/vector-formal-preview-freeze-gate.md"] = true,
            ["docs/ContextCore_Foundation_Freeze_Report.md"] = true,
            ["eval/eval-report-p15-a3.json"] = true,
            ["eval/eval-report-p15-extended.json"] = true
        };
        if (!string.IsNullOrWhiteSpace(missingPath))
        {
            coverage[missingPath] = false;
        }

        return coverage;
    }

    private static Dictionary<string, IReadOnlyList<string>> CleanGitStatusCategories()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["source code"] = ["src/ContextCore.Evaluation/Learning/FoundationReproducibilityRunner.cs"],
            ["tests"] = ["tests/ContextCore.Tests/ContextCoreRetrievalDatasetV2MetadataContractTests.cs"],
            ["docs"] = ["docs/ContextCore_Foundation_Freeze_Report.md"],
            ["generated reports"] = ["foundation/foundation-release-candidate-gate.json"],
            ["local config / secrets"] = Array.Empty<string>(),
            ["model files"] = Array.Empty<string>(),
            ["temporary files"] = Array.Empty<string>(),
            ["other"] = [".gitignore"]
        };

    private static FoundationServiceStatusResponse CleanFoundationServiceStatusResponse(
        bool runtimeMutated = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false)
        => new()
        {
            FoundationGateStatus = "Passed",
            RuntimeChangeGateStatus = "Passed",
            ReproducibilityStatus = "Passed",
            VectorFormalPreviewStatus = "Passed",
            PostgresFreezeStatus = "Passed",
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            Capabilities =
            [
                new CapabilityStatus
                {
                    CapabilityId = "ContextCoreFoundation",
                    Category = "foundation",
                    GatePassed = true,
                    RuntimeSwitchAllowed = false
                }
            ]
        };

    private static FoundationApiResponseEnvelope<FoundationReportNavigationResponse> CleanReportNavigationEnvelope()
        => new()
        {
            Success = true,
            CapabilityId = "foundation.report.navigation",
            Status = "Ready",
            Recommendation = "ReadyForReadOnlyReportNavigation",
            SchemaVersion = "foundation-api-envelope-v1",
            Data = new FoundationReportNavigationResponse
            {
                ReportCount = 1,
                ExistingReportCount = 1,
                DegradedReportCount = 0,
                Recommendation = "ReadyForReadOnlyReportNavigation",
                Reports =
                [
                    new FoundationReportNavigationEntry
                    {
                        ReportId = "foundation-release-candidate-gate",
                        CapabilityId = "ContextCoreFoundation",
                        RelativePath = "foundation/foundation-release-candidate-gate.json",
                        Exists = true,
                        ContentType = "application/json",
                        Summary = "Frozen; Ready",
                        SafeToExpose = true
                    }
                ]
            }
        };

    private static FoundationApiResponseEnvelope<FoundationServiceStatusResponse> CleanMissingReportProbeEnvelope()
        => new()
        {
            Success = true,
            CapabilityId = "foundation.readonly.status",
            Status = "Degraded",
            Recommendation = "RegenerateReport",
            SchemaVersion = "foundation-api-envelope-v1",
            Data = CleanFoundationServiceStatusResponse(),
            Diagnostics = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MissingReportIds"] = ["foundation-release-candidate-gate"]
            }
        };

    private static FoundationServiceAuthDiagnosticsReport CleanFoundationServiceAuthDiagnostics()
        => new()
        {
            DeploymentProfile = ServiceDeploymentProfile.Service,
            AuthConfigured = true,
            ApiKeyConfigured = true,
            RequireApiKey = true,
            ApiKeyHeaderName = "X-ContextCore-Key",
            Recommendation = "ReadyForServiceProfile"
        };

    private static HostedServiceSmokeOptions CleanHostedOptions(bool requireApiKey = false)
        => new()
        {
            Enabled = true,
            BaseUrl = "http://localhost:5088",
            DeploymentProfile = requireApiKey ? ServiceDeploymentProfile.Service : ServiceDeploymentProfile.Development,
            RequireApiKey = requireApiKey,
            ApiKeyHeaderName = "X-ContextCore-Key",
            TimeoutSeconds = 5,
            VerifyReadOnly = true,
            VerifyNoRuntimeMutation = true
        };

    private static HostedServiceEndpointProbeResult CleanHostedEndpoint(
        FoundationApiEndpointContract endpoint,
        bool envelopeSchemaMatched = true,
        bool runtimeMutated = false,
        bool secretLeakDetected = false,
        bool absolutePathLeakDetected = false)
        => new()
        {
            Method = endpoint.Method,
            Route = endpoint.Route,
            StatusCode = 200,
            Success = envelopeSchemaMatched,
            EnvelopeSchemaMatched = envelopeSchemaMatched,
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            SecretLeakDetected = secretLeakDetected,
            AbsolutePathLeakDetected = absolutePathLeakDetected
        };

    private static ServiceFoundationFreezeReport BuildServiceFoundationFreezeReport(
        ServiceFoundationStatusSmokeReport? serviceStatus = null,
        ServiceFoundationStatusSmokeReport? serviceReadiness = null,
        FoundationApiSecurityDiagnosticsReport? security = null,
        ServiceReportNavigationSmokeReport? navigation = null,
        FoundationApiContractReport? contract = null,
        FoundationServiceDeploymentProfileGateReport? deployment = null,
        FoundationOpenApiContractReport? drift = null,
        HostedServiceSmokeReport? hosted = null,
        HostedServiceSmokeReport? readonlyRuntime = null,
        HostedServiceSmokeReport? hostedContract = null,
        ContextCoreFoundationFreezeReport? foundation = null,
        FoundationReproducibilityReport? reproducibility = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        bool includeHosted = true,
        bool p15Passed = true)
    {
        var service = new FoundationStatusService(Directory.GetCurrentDirectory());
        return service.BuildServiceFoundationFreezeReport(
            serviceStatus ?? CleanServiceStatusSmokeReport(),
            serviceReadiness ?? CleanServiceStatusSmokeReport(),
            security ?? CleanSecurityDiagnostics(),
            navigation ?? CleanReportNavigationSmoke(),
            contract ?? CleanApiContractReport(),
            deployment ?? CleanDeploymentGate(),
            drift ?? CleanOpenApiContractReport(),
            includeHosted ? hosted ?? CleanHostedSmokeReport() : null,
            readonlyRuntime ?? CleanHostedSmokeReport(),
            hostedContract ?? CleanHostedSmokeReport(),
            foundation ?? BuildFoundationFreezeReport(),
            reproducibility ?? BuildFoundationReproducibilityReport(),
            runtimeGate ?? new LearningRuntimeChangeReadinessGateReport { Passed = true },
            p15Passed);
    }

    private static ServiceFoundationStatusSmokeReport CleanServiceStatusSmokeReport(
        bool runtimeMutated = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false)
        => new()
        {
            SmokePassed = true,
            Recommendation = "ReadyForReadOnlyServiceStatus",
            EndpointCount = 6,
            CapabilityCount = 8,
            FoundationStatusPassed = true,
            ReleaseCandidatePassed = true,
            ReproducibilityPassed = true,
            RuntimeChangeGatePassed = true,
            VectorFormalPreviewPassed = true,
            PostgresFreezePassed = true,
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            PackingPolicyChanged = false,
            PackageOutputChanged = false
        };

    private static FoundationApiSecurityDiagnosticsReport CleanSecurityDiagnostics()
        => new()
        {
            AuthConfigured = false,
            ApiKeyConfigured = false,
            DevelopmentMode = true,
            SecretLeakDetected = false,
            AbsolutePathLeakDetected = false,
            Recommendation = "DevelopmentOnly"
        };

    private static ServiceReportNavigationSmokeReport CleanReportNavigationSmoke()
        => new()
        {
            SmokePassed = true,
            ReportCount = 8,
            DegradedReportCount = 0,
            AbsolutePathLeakDetected = false,
            SecretLeakDetected = false,
            EnvelopeSchemaStable = true,
            Recommendation = "ReadyForReadOnlyReportNavigation"
        };

    private static FoundationApiContractReport CleanApiContractReport(
        bool freezePassed = true,
        bool runtimeMutated = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false)
        => new()
        {
            ContractPassed = freezePassed,
            FreezePassed = freezePassed,
            Recommendation = freezePassed ? "ReadyForServiceApiContractFreeze" : "BlockedByForbiddenActionExposure",
            EndpointCount = 8,
            ClientMethodCount = 8,
            EnvelopeSchemaVersion = "foundation-api-envelope-v1",
            DegradedBehaviorStable = true,
            MissingReportReturnsDegraded = true,
            ReportNavigationSchemaStable = true,
            ForbiddenActionsExposed = true,
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            ReadOnly = true
        };

    private static FoundationServiceDeploymentProfileGateReport CleanDeploymentGate(
        bool gatePassed = true,
        bool runtimeMutated = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false)
        => new()
        {
            GatePassed = gatePassed,
            DeploymentProfile = ServiceDeploymentProfile.Development,
            AuthConfigured = false,
            ApiKeyConfigured = false,
            RequireApiKey = false,
            DevelopmentNoAuthAllowed = true,
            SecretLeakDetected = false,
            AbsolutePathLeakDetected = false,
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            Recommendation = gatePassed ? "ReadyForServiceDeploymentProfile" : "BlockedByProductionAuthMissing",
            BlockedReasons = gatePassed ? Array.Empty<string>() : ["ProductionAuthNotConfigured"]
        };

    private static FoundationOpenApiContractReport CleanOpenApiContractReport(bool breakingChangeDetected = false)
        => new()
        {
            EndpointCount = 8,
            EndpointIds = ["GET /api/admin/foundation/status"],
            EnvelopeSchemaVersion = "foundation-api-envelope-v1",
            AuthScheme = "ApiKeyAuth",
            ApiKeyHeaderName = "X-ContextCore-Key",
            ClientMethodCount = 13,
            ResponseSchemaCount = 8,
            ForbiddenActionCount = 6,
            BreakingChangeDetected = breakingChangeDetected,
            SecretLeakDetected = false,
            AbsolutePathLeakDetected = false,
            ReadOnly = true,
            Recommendation = breakingChangeDetected ? "BlockedByBreakingChange" : "ReadyForOpenApiContractFreeze",
            BlockedReasons = breakingChangeDetected ? ["EndpointDeleted"] : Array.Empty<string>()
        };

    private static HostedServiceSmokeReport CleanHostedSmokeReport(
        bool smokePassed = true,
        bool runtimeMutated = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false)
        => new()
        {
            SmokePassed = smokePassed && !runtimeMutated && !formalRetrievalAllowed && !runtimeSwitchAllowed,
            BaseUrl = "http://localhost:5088",
            DeploymentProfile = ServiceDeploymentProfile.Development,
            EndpointCount = 8,
            SuccessfulEndpointCount = 8,
            FailedEndpointCount = 0,
            AuthPassed = true,
            UnauthorizedCheckPassed = true,
            EnvelopeSchemaMatched = true,
            RuntimeMutated = runtimeMutated,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            SecretLeakDetected = false,
            AbsolutePathLeakDetected = false,
            Recommendation = smokePassed ? "ReadyForHostedReadOnlyService" : "NeedsHostedServiceConfig",
            BlockedReasons = smokePassed ? Array.Empty<string>() : ["HostedEndpointFailure"]
        };

    private static LimitedFormalPreviewObservationReport CleanLimitedFormalPreviewObservationGate(
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
        return new LimitedFormalPreviewObservationReport
        {
            OperationId = "vector-limited-formal-preview-observation-gate-test",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = clean,
            GatePassed = clean,
            Mode = ScopedFormalPreviewOptInModes.PreviewOnly,
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            ObservationRunCount = 3,
            PreviewPackageCount = 360,
            BaselinePackageCount = 360,
            CandidateAddCount = 171,
            CandidateRemoveCount = 171,
            CandidateUnchangedCount = 1629,
            SectionChangedCount = 0,
            TokenDeltaTotal = 165,
            TokenDeltaMax = 10,
            TokenDeltaP95 = 10,
            ConstraintCoverageDelta = 0.0167,
            RelationCoverageDelta = 0.0569,
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
                ? LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze
                : LimitedFormalPreviewObservationRecommendations.BlockedByRisk,
            BlockedReasons = clean ? Array.Empty<string>() : ["SyntheticLimitedFormalPreviewObservationGateBlocked"]
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

    private static RetrievalDatasetV2StressFreezeReport BuildStressFreezeReport(
        int leakageIssueCount = 0,
        double anchorDominanceScore = 0,
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        int hybridScoringRiskCandidateCount = 0)
        => new RetrievalDatasetV2StressFreezeRunner().BuildReport(
            CleanMaterializationGate(),
            CleanSmallSetReadinessGate(),
            CleanStressReadinessGate(),
            CleanStressReadinessGate(leakageIssueCount: leakageIssueCount, anchorDominanceScore: anchorDominanceScore),
            CleanStressReadinessGate(leakageIssueCount: 0, anchorDominanceScore: anchorDominanceScore),
            CleanStressFailureTriage(),
            CleanHybridRepairGate(riskAfterPolicy: riskAfterPolicy, formalOutputChanged: formalOutputChanged),
            CleanHybridScoringRiskTriage(hybridScoringRiskCandidateCount));

    private static RetrievalDatasetV2ReadinessGateReport CleanSmallSetReadinessGate()
        => new()
        {
            DatasetId = "rdsv2-small",
            GatePassed = true,
            BestRecallAfterPolicy = 1,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            PgVectorParityPassed = true,
            MaterializationGatePassed = true,
            ValidationIssueCount = 0,
            MissingEvidenceCount = 0,
            MissingProvenanceCount = 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate
        };

    private static RetrievalDatasetV2StressReport CleanStressReadinessGate(
        int leakageIssueCount = 0,
        double anchorDominanceScore = 0)
        => new()
        {
            DatasetId = "rdsv2-stress",
            CorpusItemCount = 120,
            SampleCount = 120,
            LeakageIssueCount = leakageIssueCount,
            AnchorDominanceScore = anchorDominanceScore,
            DenseRecall = 0.475,
            HybridRecall = 0.43333333333333335,
            HoldoutHybridRecall = 0.625,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2StressRecommendations.BlockedByHoldoutRecall
        };

    private static RetrievalDatasetV2StressRecallFailureTriageReport CleanStressFailureTriage()
        => new()
        {
            DatasetId = "rdsv2-stress",
            SampleCount = 120,
            FailureCount = 68,
            HoldoutFailureCount = 9,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2StressFailureTriageRecommendations.NeedsHybridUnionScoringRepair
        };

    private static HybridUnionScoringRepairReport CleanHybridRepairGate(
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0)
        => new()
        {
            DatasetId = "rdsv2-stress",
            BestProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GatePassed = true,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze,
            Profiles =
            [
                new HybridUnionScoringRepairProfileReport
                {
                    ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
                    SampleCount = 120,
                    RecallAfterPolicy = 0.5083333333333333,
                    HoldoutRecallAfterPolicy = 0.75,
                    RiskAfterPolicy = riskAfterPolicy,
                    MustNotHitRiskAfterPolicy = riskAfterPolicy,
                    LifecycleRiskAfterPolicy = 0,
                    FormalOutputChanged = formalOutputChanged,
                    DenseWinnerLostCount = 0,
                    Recommendation = riskAfterPolicy == 0 && formalOutputChanged == 0
                        ? HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze
                        : HybridUnionScoringRepairRecommendations.BlockedByRisk
                }
            ]
        };

    private static HybridScoringRiskRegressionTriageReport CleanHybridScoringRiskTriage(int riskCandidateCount = 0)
        => new()
        {
            DatasetId = "rdsv2-stress",
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            SampleCount = 120,
            RiskCandidateCount = riskCandidateCount,
            RiskProjectionMismatchCount = 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = riskCandidateCount == 0
                ? HybridScoringRiskRegressionRecommendations.ReadyForSafeScoringRepair
                : HybridScoringRiskRegressionRecommendations.NeedsPostScoringRiskGate
        };

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
