using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextCore.Tests;

[TestClass]
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
            "ContextCore.Core",
            "Services",
            "Vector",
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
            "ContextCore.Core",
            "Services",
            "Vector",
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
            "ContextCore.Core",
            "Services",
            "Vector",
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
            "ContextCore.Core",
            "Services",
            "Vector",
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
    public void VectorV4ReadinessRecheck_CleanReportsAllowGuardedPreviewOnly()
    {
        var report = BuildV4ReadinessRecheckReport();

        Assert.IsTrue(report.RecheckPassed);
        Assert.AreEqual(VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview, report.Recommendation);
        Assert.IsTrue(report.ReadyForGuardedFormalPreview);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void VectorV4ReadinessRecheck_MissingStressFreezeBlocks()
    {
        var report = BuildV4ReadinessRecheckReport(includeStressFreeze: false);

        Assert.IsFalse(report.RecheckPassed);
        Assert.AreEqual(VectorV4ReadinessRecheckRecommendations.BlockedByDatasetV2Stress, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDatasetV2StressFreezeGate");
    }

    [TestMethod]
    public void VectorV4ReadinessRecheck_RiskBlocks()
    {
        var report = BuildV4ReadinessRecheckReport(stressFreeze: BuildStressFreezeReport(riskAfterPolicy: 1));

        Assert.IsFalse(report.RecheckPassed);
        Assert.AreEqual(VectorV4ReadinessRecheckRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DatasetV2StressRiskNonZero");
    }

    [TestMethod]
    public void VectorV4ReadinessRecheck_FormalOutputChangeBlocks()
    {
        var report = BuildV4ReadinessRecheckReport(stressFreeze: BuildStressFreezeReport(formalOutputChanged: 1));

        Assert.IsFalse(report.RecheckPassed);
        Assert.AreEqual(VectorV4ReadinessRecheckRecommendations.BlockedByFormalOutputChange, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DatasetV2StressFormalOutputChanged");
    }

    [TestMethod]
    public void VectorV4ReadinessRecheck_RuntimeChangeGateFailureBlocks()
    {
        var report = BuildV4ReadinessRecheckReport(runtimeGatePassed: false);

        Assert.IsFalse(report.RecheckPassed);
        Assert.AreEqual(VectorV4ReadinessRecheckRecommendations.BlockedByRuntimeChangeGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeGateFailed");
    }

    [TestMethod]
    public void VectorV4ReadinessRecheck_ProviderParityFailureBlocks()
    {
        var report = BuildV4ReadinessRecheckReport(pgVectorParityPassed: false);

        Assert.IsFalse(report.RecheckPassed);
        Assert.AreEqual(VectorV4ReadinessRecheckRecommendations.BlockedByProviderParity, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PgVectorProviderParityNotReady");
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
    public void ScopedRuntimeExperimentDryRunObservation_CleanReportsReadyForDesignFreeze()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(stage: "gate");

        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.ReadyForScopedRuntimeExperimentDesignFreeze,
            report.Recommendation);
        Assert.AreEqual(3, report.ObservationRunCount);
        Assert.AreEqual(360, report.DryRunPackageCount);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_MissingV45GateBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(includeV45Gate: false);

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V45ScopedRuntimeExperimentGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_InsufficientRunsBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            options: CleanScopedRuntimeExperimentDryRunObservationOptions(observationRuns: 0));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.NeedsMoreDryRunObservation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "InsufficientDryRunObservationRuns");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_RiskBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            shadowGate: CleanVectorShadowPackageComparisonGate(riskAfterPolicy: 1));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskAfterPolicyNonZero");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_FormalOutputChangedBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            shadowGate: CleanVectorShadowPackageComparisonGate(formalOutputChanged: 1));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByFormalOutputChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalOutputChangedNonZero");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_FormalPackageWriteBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            options: CleanScopedRuntimeExperimentDryRunObservationOptions(writeFormalPackage: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByFormalPackageWrite,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWritten");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_RuntimeMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            options: CleanScopedRuntimeExperimentDryRunObservationOptions(runtimeMutated: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutated");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_VectorStoreBindingMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            options: CleanScopedRuntimeExperimentDryRunObservationOptions(vectorStoreBindingChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByVectorStoreBindingMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorStoreBindingChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_PackingPolicyMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            options: CleanScopedRuntimeExperimentDryRunObservationOptions(packingPolicyChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByPackingPolicyChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_PackageOutputMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            options: CleanScopedRuntimeExperimentDryRunObservationOptions(packageOutputChanged: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByPackageOutputChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOutputChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_ScopeLeakBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            v45Gate: CleanExplicitScopedRuntimeExperimentGate(scopeLeakCount: 1));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByScopeLeak, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NonAllowlistedScopeLeak");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDryRunObservation_MissingRollbackPlanBlocks()
    {
        var report = BuildScopedRuntimeExperimentDryRunObservationReport(
            v45Gate: CleanExplicitScopedRuntimeExperimentGate(rollbackPlan: string.Empty));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RollbackPlanMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_CleanReportsReadyForRuntimeExperimentProposal()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDesignFreezeStatuses.Frozen, report.DesignStatus);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.ReadyForRuntimeExperimentProposal,
            report.Recommendation);
        Assert.IsTrue(report.ReadyForRuntimeExperimentProposal);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_MissingV46GateBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(includeDryRunObservation: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByMissingDryRunObservation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "DryRunObservationGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_RiskBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(riskAfterPolicy: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskAfterPolicyNonZero");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_FormalOutputChangedBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(formalOutputChanged: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByFormalOutputChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalOutputChangedNonZero");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_RuntimeMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(runtimeMutated: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutated");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_VectorStoreBindingMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(vectorStoreBindingChanged: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByVectorStoreBindingMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorStoreBindingChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_PackingPolicyMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(packingPolicyChanged: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByPackingPolicyChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_PackageOutputMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(packageOutputChanged: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByPackageOutputChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOutputChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_FormalPackageWriteBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(formalPackageWritten: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWritten");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_ScopeLeakBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(scopeLeakCount: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByScopeLeak, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NonAllowlistedScopeLeak");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentDesignFreeze_MissingRollbackPlanBlocks()
    {
        var report = BuildScopedRuntimeExperimentDesignFreezeReport(
            dryRunObservation: CleanScopedRuntimeExperimentDryRunObservationGate(rollbackPlanAvailable: false));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByMissingRollbackPlan,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RollbackPlanMissing");
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
    public void ScopedRuntimeExperimentApproval_MissingProposalBlocks()
    {
        var service = new ScopedRuntimeExperimentApprovalService(new InMemoryScopedRuntimeExperimentApprovalStore());
        var report = service.BuildPreview(null, CleanScopedRuntimeExperimentApprovalOptions());

        Assert.IsFalse(report.ApprovalPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingProposal, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ProposalGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentApproval_MissingApprovedByBlocks()
    {
        var service = new ScopedRuntimeExperimentApprovalService(new InMemoryScopedRuntimeExperimentApprovalStore());
        var report = service.BuildPreview(
            BuildScopedRuntimeExperimentProposalReport(),
            CleanScopedRuntimeExperimentApprovalOptions(approvedBy: string.Empty));

        Assert.IsFalse(report.ApprovalPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovedByMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentApproval_MissingReasonBlocks()
    {
        var service = new ScopedRuntimeExperimentApprovalService(new InMemoryScopedRuntimeExperimentApprovalStore());
        var report = service.BuildPreview(
            BuildScopedRuntimeExperimentProposalReport(),
            CleanScopedRuntimeExperimentApprovalOptions(reason: string.Empty));

        Assert.IsFalse(report.ApprovalPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalReasonMissing");
    }

    [TestMethod]
    public async Task ScopedRuntimeExperimentApproval_MissingConfirmWritesNothing()
    {
        var store = new InMemoryScopedRuntimeExperimentApprovalStore();
        var service = new ScopedRuntimeExperimentApprovalService(store);
        var report = await service.ApproveAsync(
            BuildScopedRuntimeExperimentProposalReport(),
            CleanScopedRuntimeExperimentApprovalOptions(),
            confirm: false);

        Assert.IsFalse(report.RecordWritten);
        Assert.AreEqual(0, (await store.ListAsync()).Count);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ExplicitConfirmMissing");
    }

    [TestMethod]
    public async Task ScopedRuntimeExperimentApproval_ConfirmWritesNoOpOnlyRecord()
    {
        var store = new InMemoryScopedRuntimeExperimentApprovalStore();
        var service = new ScopedRuntimeExperimentApprovalService(store);
        var report = await service.ApproveAsync(
            BuildScopedRuntimeExperimentProposalReport(),
            CleanScopedRuntimeExperimentApprovalOptions(),
            confirm: true);

        Assert.IsTrue(report.ApprovalPassed);
        Assert.IsTrue(report.RecordWritten);
        var records = await store.ListAsync();
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, records[0].ApprovalMode);
        Assert.AreEqual("false", records[0].Metadata["useForRuntime"]);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentApproval_UnsafeApprovalModeBlocks()
    {
        var service = new ScopedRuntimeExperimentApprovalService(new InMemoryScopedRuntimeExperimentApprovalStore());
        var report = service.BuildPreview(
            BuildScopedRuntimeExperimentProposalReport(),
            CleanScopedRuntimeExperimentApprovalOptions(approvalMode: "RuntimeSwitch"));

        Assert.IsFalse(report.ApprovalPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByUnsafeApprovalMode, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "UnsafeApprovalMode");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentNoOpHarness_ExpiredApprovalBlocks()
    {
        var report = BuildScopedRuntimeExperimentNoOpHarnessReport(
            approval: CleanScopedRuntimeExperimentApprovalRecord(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.IsFalse(report.HarnessPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByExpiredApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalExpired");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentNoOpHarness_RevokedApprovalBlocks()
    {
        var report = BuildScopedRuntimeExperimentNoOpHarnessReport(
            approval: CleanScopedRuntimeExperimentApprovalRecord(revoked: true));

        Assert.IsFalse(report.HarnessPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByRevokedApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalRevoked");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentNoOpHarness_MutatesNothing()
    {
        var report = BuildScopedRuntimeExperimentNoOpHarnessReport();

        Assert.IsTrue(report.HarnessPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze, report.Recommendation);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.DiBindingChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentNoOpHarness_FormalPackageWriteAttemptBlocks()
    {
        var report = BuildScopedRuntimeExperimentNoOpHarnessReport(
            options: CleanScopedRuntimeExperimentNoOpHarnessOptions(writeFormalPackage: true));

        Assert.IsFalse(report.HarnessPassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_CleanReportsReadyForGuardedPlanning()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentHarnessFreezeRecommendations.ReadyForGuardedRuntimeExperimentPlanning,
            report.Recommendation);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, report.ApprovalMode);
        Assert.AreEqual("Passed", report.HarnessStatus);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "RuntimeSwitch");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_MissingProposalBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(proposal: null, includeProposal: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByMissingProposal, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ProposalGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_MissingApprovalBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(approval: null, includeApproval: false);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByMissingApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalSummaryMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_ExpiredApprovalBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            approval: CleanScopedRuntimeExperimentApprovalSummary(expired: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByExpiredApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalExpired");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_RevokedApprovalBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            approval: CleanScopedRuntimeExperimentApprovalSummary(revoked: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByRevokedApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalRevoked");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_UnsafeApprovalModeBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            approval: CleanScopedRuntimeExperimentApprovalSummary(approvalMode: "RuntimeSwitch"));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByUnsafeApprovalMode, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "UnsafeApprovalMode");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_HarnessFailureBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            noOpHarness: CleanScopedRuntimeExperimentNoOpHarnessGate(harnessPassed: false));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByHarnessFailure, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NoOpHarnessGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_RuntimeMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            noOpHarness: CleanScopedRuntimeExperimentNoOpHarnessGate(runtimeMutated: true));

        Assert.IsFalse(report.FreezePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutated");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_VectorStoreBindingMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            noOpHarness: CleanScopedRuntimeExperimentNoOpHarnessGate(vectorStoreBindingChanged: true));

        Assert.IsFalse(report.FreezePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorStoreBindingChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_FormalPackageWriteBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            noOpHarness: CleanScopedRuntimeExperimentNoOpHarnessGate(formalPackageWritten: true));

        Assert.IsFalse(report.FreezePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWritten");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentHarnessFreeze_PackingPolicyPackageOutputMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentHarnessFreezeReport(
            noOpHarness: CleanScopedRuntimeExperimentNoOpHarnessGate(
                packingPolicyChanged: true,
                packageOutputChanged: true));

        Assert.IsFalse(report.FreezePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyChanged");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOutputChanged");
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
    public void GuardedScopedRuntimeExperimentPlan_CleanReportsReadyForActivationContract()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport();

        Assert.IsTrue(report.PlanPassed);
        Assert.AreEqual(
            GuardedScopedRuntimeExperimentPlanRecommendations.ReadyForScopedRuntimeExperimentActivationContract,
            report.Recommendation);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment, report.RequiredApprovalMode);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "TreatingNoOpHarnessOnlyApprovalAsRuntimeApproval");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_MissingV410HarnessFreezeBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(includeHarnessFreeze: false);

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ScopedRuntimeExperimentHarnessFreezeGateNotPassed");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_NoOpHarnessOnlyApprovalDoesNotSatisfyRuntimeApproval()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(
            options: CleanGuardedScopedRuntimeExperimentPlanOptions(
                requiredApprovalMode: ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByUnsafeApprovalMode, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NoOpHarnessOnlyApprovalCannotSatisfyRuntimeApproval");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_MissingSelectedScopeBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(
            proposal: CleanScopedRuntimeExperimentProposalGate(workspaceId: string.Empty),
            options: CleanGuardedScopedRuntimeExperimentPlanOptions(includeScopes: false));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.NeedsScopeConfiguration, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SelectedScopeNotConfigured");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_MissingKillSwitchBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(
            proposal: CleanScopedRuntimeExperimentProposalGate(killSwitchPlan: string.Empty));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingKillSwitch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "KillSwitchPlanMissing");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_MissingRollbackBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(
            proposal: CleanScopedRuntimeExperimentProposalGate(rollbackPlan: string.Empty));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingRollbackPlan, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RollbackPlanMissing");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_MissingObservationPlanBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(
            options: CleanGuardedScopedRuntimeExperimentPlanOptions(requireObservationPlan: false));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingObservationPlan, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ObservationPlanMissing");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperimentPlan_RuntimeSwitchAttemptBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentPlanReport(
            options: CleanGuardedScopedRuntimeExperimentPlanOptions(
                useForRuntime: true,
                formalRetrievalAllowed: true,
                runtimeSwitchAllowed: true,
                readyForRuntimeSwitch: true));

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByRuntimeSwitchAttempt, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAttempt");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_MissingApprovalBlocksGate()
    {
        var report = new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildGate(
            BuildGuardedScopedRuntimeExperimentPlanReport(),
            null);

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalRecordMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_NoOpHarnessOnlyBlocksGate()
    {
        var report = new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildGate(
            BuildGuardedScopedRuntimeExperimentPlanReport(),
            CleanScopedRuntimeExperimentApprovalRecord());

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByWrongApprovalMode, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "WrongApprovalMode");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_ExpiredApprovalBlocksGate()
    {
        var report = new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildGate(
            BuildGuardedScopedRuntimeExperimentPlanReport(),
            CleanScopedRuntimeExperimentApprovalRecord(
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
                approvalMode: ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByExpiredApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalExpired");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_RevokedApprovalBlocksGate()
    {
        var report = new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildGate(
            BuildGuardedScopedRuntimeExperimentPlanReport(),
            CleanScopedRuntimeExperimentApprovalRecord(
                revoked: true,
                approvalMode: ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByRevokedApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalRevoked");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_MissingAcknowledgementsBlock()
    {
        var runner = new ScopedRuntimeExperimentRuntimeApprovalRunner();
        var plan = BuildGuardedScopedRuntimeExperimentPlanReport();
        var missingRisk = runner.BuildApproval(plan, CleanScopedRuntimeExperimentRuntimeApprovalOptions(riskAcknowledgement: string.Empty), confirm: true);
        var missingRollback = runner.BuildApproval(plan, CleanScopedRuntimeExperimentRuntimeApprovalOptions(rollbackAcknowledgement: string.Empty), confirm: true);
        var missingKillSwitch = runner.BuildApproval(plan, CleanScopedRuntimeExperimentRuntimeApprovalOptions(killSwitchAcknowledgement: string.Empty), confirm: true);
        var missingScope = runner.BuildApproval(plan, CleanScopedRuntimeExperimentRuntimeApprovalOptions(scopeAcknowledgement: string.Empty), confirm: true);
        var missingObservation = runner.BuildApproval(plan, CleanScopedRuntimeExperimentRuntimeApprovalOptions(observationPlanAcknowledgement: string.Empty), confirm: true);

        foreach (var report in new[] { missingRisk, missingRollback, missingKillSwitch, missingScope, missingObservation })
        {
            Assert.IsFalse(report.ApprovalPassed);
            Assert.IsFalse(report.RecordWritten);
            Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingAcknowledgement, report.Recommendation);
        }
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_MissingConfirmWritesNothing()
    {
        var report = new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildApproval(
            BuildGuardedScopedRuntimeExperimentPlanReport(),
            CleanScopedRuntimeExperimentRuntimeApprovalOptions(),
            confirm: false);

        Assert.IsFalse(report.ApprovalPassed);
        Assert.IsFalse(report.RecordWritten);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ExplicitConfirmMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentRuntimeApproval_CleanApprovalPassesGateWithoutRuntimeMutation()
    {
        var runner = new ScopedRuntimeExperimentRuntimeApprovalRunner();
        var plan = BuildGuardedScopedRuntimeExperimentPlanReport();
        var approval = runner.BuildApproval(plan, CleanScopedRuntimeExperimentRuntimeApprovalOptions(), confirm: true);
        var gate = runner.BuildGate(plan, approval.ApprovalRecord);

        Assert.IsTrue(approval.ApprovalPassed);
        Assert.IsTrue(approval.RecordWritten);
        Assert.IsTrue(gate.GatePassed);
        Assert.AreEqual(ScopedRuntimeExperimentApprovalRecommendations.ReadyForActivationPreflight, gate.Recommendation);
        Assert.IsFalse(gate.RuntimeSwitchAllowed);
        Assert.IsFalse(gate.FormalRetrievalAllowed);
        Assert.IsFalse(gate.ReadyForRuntimeSwitch);
        Assert.IsFalse(gate.UseForRuntime);
        Assert.IsFalse(gate.FormalPackageWriteAllowed);
        Assert.IsFalse(gate.PackingPolicyIntegrationAllowed);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_CleanReportsReadyForGuardedExperiment()
    {
        var runner = new ScopedRuntimeExperimentActivationPreflightRunner();
        var preflight = BuildScopedRuntimeExperimentActivationPreflightReport("preflight");
        var route = BuildScopedRuntimeExperimentActivationPreflightReport("route");
        var gate = runner.BuildGate(
            BuildFoundationFreezeReport(),
            BuildServiceFoundationFreezeReport(),
            CleanVectorFormalPreviewFreezeReport(),
            BuildGuardedScopedRuntimeExperimentPlanReport(),
            BuildScopedRuntimeExperimentRuntimeApprovalGateReport(),
            CleanRuntimeChangeGate(true),
            preflight,
            route,
            CleanScopedRuntimeExperimentActivationPreflightOptions());

        Assert.IsTrue(preflight.PreflightPassed);
        Assert.IsTrue(route.RuntimeRouteDryRunExecuted);
        Assert.IsTrue(gate.PreflightPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentActivationPreflightRecommendations.ReadyForGuardedScopedRuntimeExperiment,
            gate.Recommendation);
        Assert.IsFalse(gate.RuntimeMutated);
        Assert.IsFalse(gate.VectorStoreBindingChanged);
        Assert.IsFalse(gate.FormalPackageWritten);
        Assert.IsFalse(gate.FormalRetrievalAllowed);
        Assert.IsFalse(gate.RuntimeSwitchAllowed);
        Assert.IsFalse(gate.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_MissingApprovalBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport("preflight", approvalGate: null, includeApproval: false);

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ScopedRuntimeExperimentApprovalGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_WrongApprovalIdBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(approvalId: "wrong-approval"));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingApproval, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalIdMismatch");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_MissingKillSwitchBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            plan: CopyGuardedScopedRuntimeExperimentPlan(BuildGuardedScopedRuntimeExperimentPlanReport(), killSwitchPlan: string.Empty));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingKillSwitch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "KillSwitchMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_MissingRollbackBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            plan: CopyGuardedScopedRuntimeExperimentPlan(BuildGuardedScopedRuntimeExperimentPlanReport(), rollbackPlan: string.Empty));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingRollbackPlan, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RollbackPlanMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_MissingTraceSinkBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(traceSinkAvailable: false));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingTraceSink, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "TraceSinkMissing");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_ScopeLeakBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(scopeLeakCount: 1));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByScopeLeak, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NonAllowlistedScopeLeakDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_RuntimeMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(mutateRuntime: true));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByRuntimeMutation, report.Recommendation);
        Assert.IsTrue(report.RuntimeMutated);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_VectorStoreBindingMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(vectorStoreBindingChanged: true));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByVectorStoreBindingMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorStoreBindingMutationDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_FormalPackageWriteBlocks()
    {
        var report = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(writeFormalPackage: true));

        Assert.IsFalse(report.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByFormalPackageWrite, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalPackageWriteDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentActivationPreflight_PackingPolicyOrPackageOutputBlocks()
    {
        var packing = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(packingPolicyChanged: true));
        var packageOutput = BuildScopedRuntimeExperimentActivationPreflightReport(
            "preflight",
            options: CleanScopedRuntimeExperimentActivationPreflightOptions(packageOutputChanged: true));

        Assert.IsFalse(packing.PreflightPassed);
        Assert.IsFalse(packageOutput.PreflightPassed);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByRuntimeMutation, packing.Recommendation);
        Assert.AreEqual(ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByRuntimeMutation, packageOutput.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_DisabledByDefaultBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport("experiment", options: new GuardedScopedRuntimeExperimentOptions());

        Assert.IsFalse(report.ExperimentPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "GuardedScopedRuntimeExperimentDisabled");
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_MissingActivationGateBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport("experiment", includeActivation: false);

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByMissingActivationGate, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_MissingRuntimeApprovalBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport("experiment", includeApproval: false);

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByMissingApproval, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_NoOpHarnessApprovalBlocks()
    {
        var approval = new ScopedRuntimeExperimentApprovalGateReport
        {
            GatePassed = true,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalId = "vsrea-clean",
            ApprovalMode = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly,
            ApprovalExists = true,
            RequiredAcknowledgementsPresent = true
        };
        var report = BuildGuardedScopedRuntimeExperimentReport("experiment", approvalGate: approval);

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByWrongApprovalMode, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_AllowlistedScopeHitsExperimentRoute()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport("experiment");

        Assert.IsTrue(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.ReadyForScopedRuntimeExperimentObservation, report.Recommendation);
        Assert.IsTrue(report.ExperimentRouteHitCount > 0);
        Assert.IsTrue(report.Traces.Any(trace => trace.ScopeMatched && trace.ExperimentRouteHit));
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_NonAllowlistedScopeStaysBaseline()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport("experiment");

        Assert.AreEqual(1, report.NonAllowlistedRequestCount);
        Assert.AreEqual(0, report.NonAllowlistedScopeLeakCount);
        Assert.IsTrue(report.Traces.Any(trace => !trace.ScopeMatched && !trace.ExperimentRouteHit));
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_FormalPackageWriteBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport(
            "experiment",
            options: CleanGuardedScopedRuntimeExperimentOptions(writeFormalPackage: true));

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByFormalPackageWrite, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_PackageOutputMutationBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport(
            "experiment",
            options: CleanGuardedScopedRuntimeExperimentOptions(packageOutputChanged: true));

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByPackageOutputChange, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_PackingPolicyMutationBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport(
            "experiment",
            options: CleanGuardedScopedRuntimeExperimentOptions(mutatePackingPolicy: true));

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByPackingPolicyChange, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_VectorBindingMutationBlocks()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport(
            "experiment",
            options: CleanGuardedScopedRuntimeExperimentOptions(vectorStoreBindingChanged: true));

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.BlockedByVectorStoreBindingMutation, report.Recommendation);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_KillSwitchDisablesExperimentRoute()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport(
            "experiment",
            options: CleanGuardedScopedRuntimeExperimentOptions(killSwitchTriggered: true));

        Assert.IsFalse(report.ExperimentPassed);
        Assert.AreEqual(0, report.ExperimentRouteHitCount);
        Assert.IsTrue(report.KillSwitchTriggered);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsTrue(report.Traces.All(trace => !trace.ExperimentRouteHit));
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_RollbackSmokeReturnsSelectedScopeToBaseline()
    {
        var report = BuildGuardedScopedRuntimeExperimentReport("rollback-smoke");

        Assert.IsTrue(report.ExperimentPassed);
        Assert.AreEqual(0, report.ExperimentRouteHitCount);
        Assert.IsTrue(report.RollbackVerified);
        Assert.IsTrue(report.KillSwitchTriggered);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
    }

    [TestMethod]
    public void GuardedScopedRuntimeExperiment_GatePassesWithObservationAndRollback()
    {
        var gate = BuildGuardedScopedRuntimeExperimentReport("gate");

        Assert.IsTrue(gate.ExperimentPassed);
        Assert.AreEqual(GuardedScopedRuntimeExperimentRecommendations.ReadyForScopedRuntimeExperimentObservation, gate.Recommendation);
        Assert.IsTrue(gate.ExperimentRouteHitCount > 0);
        Assert.IsFalse(gate.RuntimeMutated);
        Assert.IsFalse(gate.FormalPackageWritten);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_CleanReportsReadyForFreeze()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport("gate");

        Assert.IsTrue(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.ReadyForScopedRuntimeExperimentObservationFreeze,
            report.Recommendation);
        Assert.AreEqual(3, report.ObservationRunCount);
        Assert.AreEqual(360, report.RequestCount);
        Assert.AreEqual(360, report.ExperimentRouteHitCount);
        Assert.AreEqual(100, report.TraceCompleteness);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_MissingV414GateBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport("gate", includeV414Gate: false);

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V414GuardedScopedRuntimeExperimentGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_InsufficientRequestCountBlocks()
    {
        var runner = new ScopedRuntimeExperimentObservationWindowRunner();
        var v414 = BuildGuardedScopedRuntimeExperimentReport("gate");
        var existing = runner.BuildWindow(
            v414,
            CleanRuntimeChangeGate(true),
            CleanScopedRuntimeExperimentObservationWindowOptions(minRequestCount: 120));
        var report = runner.BuildGate(
            v414,
            CleanRuntimeChangeGate(true),
            existing,
            CleanScopedRuntimeExperimentObservationWindowOptions(minRequestCount: 360));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.NeedsMoreObservation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "InsufficientRequestCount");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_InsufficientObservationRunsBlocks()
    {
        var runner = new ScopedRuntimeExperimentObservationWindowRunner();
        var v414 = BuildGuardedScopedRuntimeExperimentReport("gate");
        var existing = runner.BuildWindow(
            v414,
            CleanRuntimeChangeGate(true),
            CleanScopedRuntimeExperimentObservationWindowOptions(observationRunCount: 1));
        var report = runner.BuildGate(
            v414,
            CleanRuntimeChangeGate(true),
            existing,
            CleanScopedRuntimeExperimentObservationWindowOptions(observationRunCount: 3));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.NeedsMoreObservation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "InsufficientObservationRuns");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_ScopeLeakBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(scopeLeakCount: 1));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByScopeLeak, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NonAllowlistedScopeLeakDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_RiskBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(riskAfterPolicy: 1));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_FormalOutputChangeBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(formalOutputChanged: 1));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByFormalOutputChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalOutputChangeDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_PackageOutputChangeBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(packageOutputChanged: true));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByPackageOutputChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOutputChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_PackingPolicyMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(mutatePackingPolicy: true));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByPackingPolicyChange,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackingPolicyChanged");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_RuntimeMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(runtimeMutated: true));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_VectorBindingMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(vectorStoreBindingChanged: true));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "VectorStoreBindingMutationDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_MissingTraceBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(traceCompleteness: 99));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByTraceGap,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "TraceCompletenessBelow100Percent");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_KillSwitchUnavailableBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(killSwitchAvailable: false));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "KillSwitchUnavailable");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationWindow_RollbackFailureBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationWindowReport(
            "gate",
            options: CleanScopedRuntimeExperimentObservationWindowOptions(rollbackVerified: false));

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRollbackFailure,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RollbackNotVerified");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_CleanReportsReadyForIntegrationPlan()
    {
        var report = BuildScopedRuntimeExperimentObservationFreezeReport();

        Assert.IsTrue(report.FreezePassed);
        Assert.AreEqual(
            ScopedRuntimeExperimentObservationFreezeDecisions.ReadyForFormalRetrievalIntegrationPlan,
            report.PromotionDecision);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_MissingV415Blocks()
    {
        var report = new ScopedRuntimeExperimentObservationFreezeRunner().BuildObservationFreeze(
            BuildGuardedScopedRuntimeExperimentReport("gate"),
            null,
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationFreezeDecisions.KeepPreviewOnly, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V415ObservationWindowGateNotPassed");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_RiskBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationFreezeReport(
            CleanScopedRuntimeExperimentObservationWindowOptions(riskAfterPolicy: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_OutputChangeBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationFreezeReport(
            CleanScopedRuntimeExperimentObservationWindowOptions(formalOutputChanged: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByOutputChange, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "OutputChangeDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_ScopeLeakBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationFreezeReport(
            CleanScopedRuntimeExperimentObservationWindowOptions(scopeLeakCount: 1));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByScopeLeak, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "NonAllowlistedScopeLeakDetected");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_TraceGapBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationFreezeReport(
            CleanScopedRuntimeExperimentObservationWindowOptions(traceCompleteness: 99));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByTraceGap, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "TraceCompletenessBelow100Percent");
    }

    [TestMethod]
    public void ScopedRuntimeExperimentObservationFreeze_RuntimeMutationBlocks()
    {
        var report = BuildScopedRuntimeExperimentObservationFreezeReport(
            CleanScopedRuntimeExperimentObservationWindowOptions(runtimeMutated: true));

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByRuntimeMutation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeMutationDetected");
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

    private static GuardedScopedRuntimeExperimentReport BuildGuardedScopedRuntimeExperimentReport(
        string stage,
        ScopedRuntimeExperimentActivationPreflightReport? activationGate = null,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate = null,
        GuardedScopedRuntimeExperimentOptions? options = null,
        bool includeActivation = true,
        bool includeApproval = true)
    {
        var runner = new GuardedScopedRuntimeExperimentRunner();
        var effectiveActivation = includeActivation
            ? activationGate ?? BuildScopedRuntimeExperimentActivationPreflightReport("gate")
            : null;
        var effectiveApproval = includeApproval
            ? approvalGate ?? BuildScopedRuntimeExperimentRuntimeApprovalGateReport()
            : null;
        var effectiveOptions = options ?? CleanGuardedScopedRuntimeExperimentOptions();
        return stage.ToLowerInvariant() switch
        {
            "observation" => runner.BuildObservation(
                effectiveActivation,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                effectiveOptions),
            "rollback-smoke" => runner.BuildRollbackSmoke(
                effectiveActivation,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                effectiveOptions),
            "gate" => runner.BuildGate(
                effectiveActivation,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                runner.BuildExperiment(
                    effectiveActivation,
                    effectiveApproval,
                    CleanRuntimeChangeGate(true),
                    effectiveOptions),
                runner.BuildObservation(
                    effectiveActivation,
                    effectiveApproval,
                    CleanRuntimeChangeGate(true),
                    effectiveOptions),
                runner.BuildRollbackSmoke(
                    effectiveActivation,
                    effectiveApproval,
                    CleanRuntimeChangeGate(true),
                    effectiveOptions),
                effectiveOptions),
            _ => runner.BuildExperiment(
                effectiveActivation,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                effectiveOptions)
        };
    }

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

    private static ScopedRuntimeExperimentObservationWindowReport BuildScopedRuntimeExperimentObservationWindowReport(
        string stage,
        GuardedScopedRuntimeExperimentReport? v414Gate = null,
        ScopedRuntimeExperimentObservationWindowOptions? options = null,
        bool includeV414Gate = true)
    {
        var runner = new ScopedRuntimeExperimentObservationWindowRunner();
        var effectiveV414Gate = includeV414Gate
            ? v414Gate ?? BuildGuardedScopedRuntimeExperimentReport("gate")
            : null;
        var effectiveOptions = options ?? CleanScopedRuntimeExperimentObservationWindowOptions();
        return stage.ToLowerInvariant() switch
        {
            "summary" => runner.BuildSummary(
                effectiveV414Gate,
                CleanRuntimeChangeGate(true),
                runner.BuildWindow(effectiveV414Gate, CleanRuntimeChangeGate(true), effectiveOptions),
                effectiveOptions),
            "gate" => runner.BuildGate(
                effectiveV414Gate,
                CleanRuntimeChangeGate(true),
                runner.BuildWindow(effectiveV414Gate, CleanRuntimeChangeGate(true), effectiveOptions),
                effectiveOptions),
            _ => runner.BuildWindow(
                effectiveV414Gate,
                CleanRuntimeChangeGate(true),
                effectiveOptions)
        };
    }

    private static ScopedRuntimeExperimentObservationFreezeReport BuildScopedRuntimeExperimentObservationFreezeReport(
        ScopedRuntimeExperimentObservationWindowOptions? options = null)
    {
        var v414Gate = BuildGuardedScopedRuntimeExperimentReport("gate");
        var v415Gate = BuildScopedRuntimeExperimentObservationWindowReport("gate", v414Gate, options);
        return new ScopedRuntimeExperimentObservationFreezeRunner().BuildObservationFreeze(
            v414Gate,
            v415Gate,
            CleanRuntimeChangeGate(true),
            p15GatePassed: true);
    }

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

    private static ScopedRuntimeExperimentActivationPreflightReport BuildScopedRuntimeExperimentActivationPreflightReport(
        string stage,
        GuardedScopedRuntimeExperimentPlanReport? plan = null,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate = null,
        ScopedRuntimeExperimentActivationPreflightOptions? options = null,
        bool includeApproval = true)
    {
        var runner = new ScopedRuntimeExperimentActivationPreflightRunner();
        var effectivePlan = plan ?? BuildGuardedScopedRuntimeExperimentPlanReport();
        var effectiveApproval = includeApproval
            ? approvalGate ?? BuildScopedRuntimeExperimentRuntimeApprovalGateReport()
            : null;
        var effectiveOptions = options ?? CleanScopedRuntimeExperimentActivationPreflightOptions();
        return stage.ToLowerInvariant() switch
        {
            "route" => runner.BuildDryRunRoute(
                BuildFoundationFreezeReport(),
                BuildServiceFoundationFreezeReport(),
                CleanVectorFormalPreviewFreezeReport(),
                effectivePlan,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                effectiveOptions),
            "gate" => runner.BuildGate(
                BuildFoundationFreezeReport(),
                BuildServiceFoundationFreezeReport(),
                CleanVectorFormalPreviewFreezeReport(),
                effectivePlan,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                runner.BuildPreflight(
                    BuildFoundationFreezeReport(),
                    BuildServiceFoundationFreezeReport(),
                    CleanVectorFormalPreviewFreezeReport(),
                    effectivePlan,
                    effectiveApproval,
                    CleanRuntimeChangeGate(true),
                    effectiveOptions),
                runner.BuildDryRunRoute(
                    BuildFoundationFreezeReport(),
                    BuildServiceFoundationFreezeReport(),
                    CleanVectorFormalPreviewFreezeReport(),
                    effectivePlan,
                    effectiveApproval,
                    CleanRuntimeChangeGate(true),
                    effectiveOptions),
                effectiveOptions),
            _ => runner.BuildPreflight(
                BuildFoundationFreezeReport(),
                BuildServiceFoundationFreezeReport(),
                CleanVectorFormalPreviewFreezeReport(),
                effectivePlan,
                effectiveApproval,
                CleanRuntimeChangeGate(true),
                effectiveOptions)
        };
    }

    private static ScopedRuntimeExperimentApprovalGateReport BuildScopedRuntimeExperimentRuntimeApprovalGateReport(
        GuardedScopedRuntimeExperimentPlanReport? plan = null)
        => new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildGate(
            plan ?? BuildGuardedScopedRuntimeExperimentPlanReport(),
            CleanScopedRuntimeExperimentApprovalRecord(approvalMode: ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment));

    private static GuardedScopedRuntimeExperimentPlanReport CopyGuardedScopedRuntimeExperimentPlan(
        GuardedScopedRuntimeExperimentPlanReport source,
        string? killSwitchPlan = null,
        string? rollbackPlan = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            PlanPassed = source.PlanPassed,
            Recommendation = source.Recommendation,
            ProposalId = source.ProposalId,
            RequiredApprovalMode = source.RequiredApprovalMode,
            SelectedScopes = source.SelectedScopes,
            MaxRequestCount = source.MaxRequestCount,
            MaxDurationMinutes = source.MaxDurationMinutes,
            KillSwitchPlan = killSwitchPlan ?? source.KillSwitchPlan,
            RollbackPlan = rollbackPlan ?? source.RollbackPlan,
            ObservationPlan = source.ObservationPlan,
            StopConditions = source.StopConditions,
            AllowedActions = source.AllowedActions,
            ForbiddenActions = source.ForbiddenActions,
            RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
            FormalRetrievalAllowed = source.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = source.ReadyForRuntimeSwitch,
            UseForRuntime = source.UseForRuntime,
            BlockedReasons = source.BlockedReasons
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

    private static ScopedRuntimeExperimentNoOpHarnessReport BuildScopedRuntimeExperimentNoOpHarnessReport(
        ExplicitScopedRuntimeExperimentProposalReport? proposal = null,
        ScopedRuntimeExperimentApprovalRecord? approval = null,
        ScopedRuntimeExperimentNoOpHarnessOptions? options = null)
        => new ScopedRuntimeExperimentNoOpHarnessRunner().BuildHarness(
            proposal ?? BuildScopedRuntimeExperimentProposalReport(),
            approval ?? CleanScopedRuntimeExperimentApprovalRecord(),
            options ?? CleanScopedRuntimeExperimentNoOpHarnessOptions(),
            p15GatePassed: true);

    private static ScopedRuntimeExperimentNoOpHarnessReport CleanScopedRuntimeExperimentNoOpHarnessGate(
        bool harnessPassed = true,
        bool runtimeMutated = false,
        bool vectorStoreBindingChanged = false,
        bool formalPackageWritten = false,
        bool packingPolicyChanged = false,
        bool packageOutputChanged = false,
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false,
        bool readyForRuntimeSwitch = false,
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0)
        => new()
        {
            OperationId = "noop-harness-clean",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = "vsrep-bb5402e39c0f1333",
            ApprovalId = "vsrea-clean",
            HarnessPassed = harnessPassed,
            Mode = ScopedRuntimeExperimentNoOpHarnessModes.NoOp,
            SelectedScopeChecked = true,
            NonAllowlistedScopeChecked = true,
            NoOpTraceCount = harnessPassed ? 1 : 0,
            BaselinePackageCount = harnessPassed ? 120 : 0,
            PreviewPackageCount = harnessPassed ? 120 : 0,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            DiBindingChanged = false,
            FormalPackageWritten = formalPackageWritten,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = formalOutputChanged,
            NonAllowlistedScopeLeakCount = 0,
            P15GatePassed = true,
            Recommendation = harnessPassed
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze
                : ScopedRuntimeExperimentApprovalRecommendations.BlockedByRuntimeMutation,
            BlockedReasons = harnessPassed ? Array.Empty<string>() : ["SyntheticNoOpHarnessBlocked"]
        };

    private static ScopedRuntimeExperimentHarnessFreezeReport BuildScopedRuntimeExperimentHarnessFreezeReport(
        ExplicitScopedRuntimeExperimentProposalReport? proposal = null,
        ScopedRuntimeExperimentApprovalSummaryReport? approval = null,
        ScopedRuntimeExperimentNoOpHarnessReport? noOpHarness = null,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze = null,
        ServiceFoundationFreezeReport? service = null,
        ContextCoreFoundationFreezeReport? foundation = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        bool includeProposal = true,
        bool includeApproval = true,
        bool p15Passed = true)
        => new ScopedRuntimeExperimentHarnessFreezeRunner().BuildGate(
            includeProposal ? proposal ?? BuildScopedRuntimeExperimentProposalReport() : null,
            includeApproval ? approval ?? CleanScopedRuntimeExperimentApprovalSummary() : null,
            noOpHarness ?? BuildScopedRuntimeExperimentNoOpHarnessReport(),
            designFreeze ?? BuildScopedRuntimeExperimentDesignFreezeReport(),
            service ?? BuildServiceFoundationFreezeReport(),
            foundation ?? BuildFoundationFreezeReport(),
            runtimeGate ?? CleanRuntimeChangeGate(true),
            p15Passed);

    private static GuardedScopedRuntimeExperimentPlanReport BuildGuardedScopedRuntimeExperimentPlanReport(
        ExplicitScopedRuntimeExperimentProposalReport? proposal = null,
        ScopedRuntimeExperimentHarnessFreezeReport? harnessFreeze = null,
        GuardedScopedRuntimeExperimentPlanOptions? options = null,
        bool includeHarnessFreeze = true)
        => new GuardedScopedRuntimeExperimentPlanRunner().BuildGate(
            BuildFoundationFreezeReport(),
            BuildServiceFoundationFreezeReport(),
            CleanVectorFormalPreviewFreezeReport(),
            BuildScopedRuntimeExperimentDesignFreezeReport(),
            includeHarnessFreeze ? harnessFreeze ?? BuildScopedRuntimeExperimentHarnessFreezeReport() : null,
            CleanRuntimeChangeGate(true),
            proposal ?? CleanScopedRuntimeExperimentProposalGate(),
            options ?? CleanGuardedScopedRuntimeExperimentPlanOptions());

    private static VectorV4ReadinessRecheckReport BuildV4ReadinessRecheckReport(
        RetrievalDatasetV2StressFreezeReport? stressFreeze = null,
        bool includeStressFreeze = true,
        bool runtimeGatePassed = true,
        bool pgVectorParityPassed = true)
        => new VectorV4ReadinessRecheckRunner().BuildReport(
            CleanLegacyVectorReadinessGate(),
            CleanLegacyLimitationReport(),
            CleanPgVectorFreezeGate(pgVectorParityPassed),
            CleanQwen3ProviderFreeze(),
            CleanHybridRetrievalFreeze(),
            CleanMaterializationGate(),
            CleanSmallSetReadinessGate(),
            includeStressFreeze ? stressFreeze ?? BuildStressFreezeReport() : null,
            CleanHybridRepairGate(),
            CleanHybridScoringRiskTriage(),
            CleanRuntimeChangeGate(runtimeGatePassed));

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

    private static ScopedRuntimeExperimentDryRunObservationReport BuildScopedRuntimeExperimentDryRunObservationReport(
        string stage = "gate",
        ExplicitScopedRuntimeExperimentPlanReport? v45Gate = null,
        VectorShadowPackageComparisonReport? shadowGate = null,
        LearningRuntimeChangeReadinessGateReport? runtimeGate = null,
        ScopedRuntimeExperimentDryRunObservationOptions? options = null,
        bool includeV45Gate = true)
    {
        var runner = new ScopedRuntimeExperimentDryRunObservationRunner();
        return string.Equals(stage, "observation", StringComparison.OrdinalIgnoreCase)
            ? runner.BuildObservation(
                includeV45Gate ? v45Gate ?? CleanExplicitScopedRuntimeExperimentGate() : null,
                shadowGate ?? CleanVectorShadowPackageComparisonGate(),
                runtimeGate ?? CleanRuntimeChangeGate(true),
                options ?? CleanScopedRuntimeExperimentDryRunObservationOptions())
            : runner.BuildGate(
                includeV45Gate ? v45Gate ?? CleanExplicitScopedRuntimeExperimentGate() : null,
                shadowGate ?? CleanVectorShadowPackageComparisonGate(),
                runtimeGate ?? CleanRuntimeChangeGate(true),
                options ?? CleanScopedRuntimeExperimentDryRunObservationOptions());
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
        => new ScopedRuntimeExperimentDesignFreezeRunner().BuildGate(
            foundation ?? BuildFoundationFreezeReport(),
            service ?? BuildServiceFoundationFreezeReport(),
            vectorFormal ?? CleanVectorFormalPreviewFreezeReport(),
            scopedRuntimeExperiment ?? CleanExplicitScopedRuntimeExperimentGate(),
            includeDryRunObservation ? dryRunObservation ?? CleanScopedRuntimeExperimentDryRunObservationGate() : null,
            runtimeGate ?? CleanRuntimeChangeGate(true),
            p15GatePassed);

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
            ["source code"] = ["src/ContextCore.Core/Services/Learning/FoundationReproducibilityRunner.cs"],
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
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not resolve repository file: " + Path.Combine(parts));
        return string.Empty;
    }

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
