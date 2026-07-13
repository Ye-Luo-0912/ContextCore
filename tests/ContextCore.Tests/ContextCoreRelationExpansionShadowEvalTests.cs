using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using ContextCore.Evaluation.Models;
using ContextCore.Evaluation.Runners;
using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Shadow")]
public sealed class ContextCoreRelationExpansionShadowEvalTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [TestMethod]
    public void RelationTypeNormalizer_ShouldNormalizeSupersedesToReplaces()
    {
        var normalizer = new RelationTypeNormalizer();

        var normalized = normalizer.Normalize("supersedes");

        Assert.AreEqual(ContextRelationTypes.Replaces, normalized);
    }

    [TestMethod]
    public async Task HygieneReport_ShouldIncludeLegacyRelationTypesAndMissingEvidence()
    {
        var root = CreateTempCorpusRoot(new[]
        {
            new ContextRelation
            {
                Id = "rel:legacy",
                WorkspaceId = "eval-chat",
                CollectionId = "test",
                SourceId = "seed",
                TargetId = "target-old",
                RelationType = "supersedes",
                Weight = 1.0,
                Confidence = 1.0,
                CreatedAt = DateTimeOffset.UtcNow
            }
        });

        try
        {
            var report = await new RelationCorpusHygieneReportBuilder().BuildAsync(root);

            Assert.IsTrue(report.LegacyRelationTypes.ContainsKey("supersedes"));
            Assert.IsTrue(report.MigrationCandidates.Any(item =>
                item.RelationId == "rel:legacy"
                && item.NormalizedType == ContextRelationTypes.Replaces));
            Assert.IsTrue(report.MissingEvidenceRelations.Any(item => item.RelationId == "rel:legacy"));
            Assert.IsTrue(report.BackfillCandidates.Any(item =>
                item.RelationId == "rel:legacy"
                && item.CanBackfillEvidence));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DeterministicRelationBackfill_ShouldSetConfidenceLifecycleAndReviewStatus()
    {
        var relation = new ContextRelation
        {
            Id = "rel:fixture",
            WorkspaceId = "eval-chat",
            CollectionId = "test",
            SourceId = "source",
            TargetId = "target",
            RelationType = "supersedes",
            Weight = 1.0,
            Confidence = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var backfilled = new RelationEvalBackfillPolicy()
            .NormalizeAndBackfillFixtureRelation(relation, "test-operation");

        Assert.AreEqual(ContextRelationTypes.Replaces, backfilled.RelationType);
        Assert.AreEqual(1.0, backfilled.Confidence);
        Assert.AreEqual(StableMemoryLifecycle.Active, backfilled.Lifecycle);
        Assert.AreEqual(RelationReviewStatuses.Reviewed, backfilled.ReviewStatus);
        Assert.AreEqual(RelationEvalBackfillPolicy.FixtureBackfillCreatedFrom, backfilled.Metadata["createdFrom"]);
        Assert.IsTrue(backfilled.SourceRefs.Contains("fixture:relation:rel:fixture"));
    }

    private static string CreateTempCorpusRoot(IReadOnlyList<ContextRelation> relations)
    {
        var root = Path.Combine(Path.GetTempPath(), $"contextcore-relation-hygiene-{Guid.NewGuid():N}");
        var categoryDir = Path.Combine(root, "chat");
        Directory.CreateDirectory(categoryDir);
        var corpus = new ContextEvalCorpus
        {
            Relations = relations
        };
        File.WriteAllText(Path.Combine(categoryDir, "corpus.json"), JsonSerializer.Serialize(corpus, JsonOptions));
        return root;
    }
}
