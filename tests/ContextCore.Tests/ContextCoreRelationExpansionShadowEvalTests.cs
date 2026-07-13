using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;
using System.Text.Json;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Evaluation.Models;

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

    [TestMethod]
    public async Task GraphExpansionApply_DefaultOff_ShouldNotAddGraphSections()
    {
        var (builder, _) = await CreateGraphApplyBuilderAsync(new GraphExpansionApplyOptions());

        var result = await builder.BuildDetailedAsync(PackageRequest());

        Assert.AreEqual(GraphExpansionApplyOptions.OffMode, result.Package.Metadata["graphExpansionMode"]);
        Assert.AreEqual("false", result.Package.Metadata["graphExpansionApplied"]);
        Assert.IsFalse(result.Package.Sections.Any(section =>
            string.Equals(section.Name, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section.Name, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task GraphExpansionApply_AuditProfile_ShouldOnlyWriteAuditContext()
    {
        var (builder, _) = await CreateGraphApplyBuilderAsync(ApplyOptions(["audit-v1"]));

        var result = await builder.BuildDetailedAsync(PackageRequest());

        Assert.AreEqual("true", result.Package.Metadata["graphExpansionApplied"]);
        Assert.AreEqual(0, ParseRisk(result.Package.Metadata["graphExpansionRiskChecks"], "riskAfterRouting"));
        Assert.IsTrue(result.Package.Sections.Any(section =>
            string.Equals(section.Name, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            && section.ItemRefs.Contains("target-audit-old")));
        Assert.IsFalse(result.Package.Sections.Any(section =>
            string.Equals(section.Name, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase)
            && section.ItemRefs.Contains("target-audit-old")));
    }

    [TestMethod]
    public async Task GraphExpansionApply_ConflictProfile_ShouldOnlyWriteConflictEvidence()
    {
        var (builder, _) = await CreateGraphApplyBuilderAsync(ApplyOptions(["conflict-v1"]));

        var result = await builder.BuildDetailedAsync(PackageRequest());

        Assert.AreEqual("true", result.Package.Metadata["graphExpansionApplied"]);
        Assert.IsTrue(result.Package.Sections.Any(section =>
            string.Equals(section.Name, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)
            && section.ItemRefs.Contains("target-conflict")));
        Assert.IsFalse(result.Package.Sections.Any(section =>
            string.Equals(section.Name, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase)
            && section.ItemRefs.Contains("target-conflict")));
    }

    [TestMethod]
    public async Task GraphExpansionApply_NormalContextInjection_ShouldFallback()
    {
        var forcedNormal = Relation(
            "rel-forced-normal",
            "seed",
            "target-audit-old",
            ContextRelationTypes.Replaces,
            confidence: 1.0,
            withEvidence: true,
            targetLifecycle: StableMemoryLifecycle.Deprecated);
        forcedNormal.Metadata["previewTargetSectionOverride"] = GraphExpansionTargetSection.NormalContext;
        var (builder, _) = await CreateGraphApplyBuilderAsync(ApplyOptions(["audit-v1"]), forcedNormal);

        var result = await builder.BuildDetailedAsync(PackageRequest());

        Assert.AreEqual("true", result.Package.Metadata["graphExpansionFallbackUsed"]);
        StringAssert.Contains(result.Package.Metadata["graphExpansionFallbackReason"], "wrongSection");
        Assert.IsFalse(result.Package.Sections.Any(section =>
            section.ItemRefs.Contains("target-audit-old")
            && (string.Equals(section.Name, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
                || string.Equals(section.Name, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)
                || string.Equals(section.Name, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public async Task GraphExpansionApply_ShouldKeepNormalSelectedSetUnchanged()
    {
        var offBuilder = (await CreateGraphApplyBuilderAsync(new GraphExpansionApplyOptions())).Builder;
        var applyBuilder = (await CreateGraphApplyBuilderAsync(ApplyOptions(["audit-v1", "conflict-v1"]))).Builder;

        var baseline = await offBuilder.BuildDetailedAsync(PackageRequest());
        var applied = await applyBuilder.BuildDetailedAsync(PackageRequest());

        CollectionAssert.AreEquivalent(
            baseline.SelectedItems.Select(item => item.ItemId).ToArray(),
            applied.SelectedItems.Select(item => item.ItemId).ToArray());
        StringAssert.Contains(applied.Package.Metadata["graphExpansionAddedItems"], "target-audit-old");
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderGraphExpansionPackageStatus()
    {
        var rendered = ServiceOperationRenderer.RenderPackageResult(new ContextPackageBuildResult
        {
            BuildId = "build-graph",
            Package = new ContextPackage
            {
                PackageId = "pkg-graph",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["graphExpansionMode"] = GraphExpansionApplyOptions.ApplyGuardedMode,
                    ["graphExpansionApplied"] = "true",
                    ["graphExpansionProfiles"] = "audit-v1",
                    ["graphExpansionAddedItems"] = "target-audit-old",
                    ["graphExpansionTargetSections"] = GraphExpansionTargetSection.AuditContext,
                    ["graphExpansionExpectedGraphSectionDelta"] = "1",
                    ["graphExpansionUnexpectedWarningDelta"] = "0",
                    ["graphExpansionFallbackUsed"] = "false",
                    ["graphExpansionRiskChecks"] = "riskAfterRouting=0;wrongSection=0;mustNotHit=0;lifecycle=0;missingEvidence=0"
                }
            }
        });

        StringAssert.Contains(rendered, "GraphExpansion");
        StringAssert.Contains(rendered, GraphExpansionApplyOptions.ApplyGuardedMode);
        StringAssert.Contains(rendered, "target-audit-old");
        StringAssert.Contains(rendered, "ExpectedDelta");
        StringAssert.Contains(rendered, "UnexpectedWarn");
    }

    [TestMethod]
    public void GraphExpansionComparison_AuditSectionAdded_ShouldBeExpectedWarningDelta()
    {
        var report = GraphExpansionOptInComparisonRunner.BuildReportFromSamples(
            "test",
            [
                ComparisonSample(
                    GraphExpansionTargetSection.AuditContext,
                    warningDelta: 2,
                    addedAudit: 1)
            ]);

        Assert.AreEqual(2, report.ExpectedWarningDelta);
        Assert.AreEqual(0, report.UnexpectedWarningDelta);
        Assert.AreEqual(GraphExpansionGuardStatus.Passed, report.GuardStatus);
        Assert.IsTrue(report.WarningDeltaByKind.ContainsKey(GraphExpansionComparisonWarningKind.ExpectedAuditContextAdded));
    }

    [TestMethod]
    public void GraphExpansionComparison_ConflictEvidenceAdded_ShouldBeExpectedWarningDelta()
    {
        var report = GraphExpansionOptInComparisonRunner.BuildReportFromSamples(
            "test",
            [
                ComparisonSample(
                    GraphExpansionTargetSection.ConflictEvidence,
                    warningDelta: 1,
                    addedConflict: 1)
            ]);

        Assert.AreEqual(1, report.ExpectedWarningDelta);
        Assert.AreEqual(0, report.UnexpectedWarningDelta);
        Assert.AreEqual(GraphExpansionGuardStatus.Passed, report.GuardStatus);
        Assert.IsTrue(report.WarningDeltaByKind.ContainsKey(GraphExpansionComparisonWarningKind.ExpectedConflictEvidenceAdded));
    }

    [TestMethod]
    public void GraphExpansionComparison_NormalContextInjection_ShouldBeUnexpectedAndFailGate()
    {
        var report = GraphExpansionOptInComparisonRunner.BuildReportFromSamples(
            "test",
            [ComparisonSample(GraphExpansionTargetSection.NormalContext)]);
        var gate = GraphExpansionOptInComparisonRunner.BuildGateReport(report, report);

        Assert.AreEqual(1, report.DisallowedNormalContextInjection);
        Assert.IsTrue(report.UnexpectedWarningDelta > 0);
        Assert.IsFalse(gate.Passed);
    }

    [TestMethod]
    public void GraphExpansionGate_SelectedSetChanged_ShouldFail()
    {
        var report = GraphExpansionOptInComparisonRunner.BuildReportFromSamples(
            "test",
            [
                ComparisonSample(
                    GraphExpansionTargetSection.AuditContext,
                    normalSelectedSetChanged: true)
            ]);
        var gate = GraphExpansionOptInComparisonRunner.BuildGateReport(report, report);

        Assert.AreEqual(1, report.NormalSelectedSetChanged);
        Assert.IsFalse(gate.Passed);
    }

    [TestMethod]
    public void GraphExpansionGate_RiskNonZero_ShouldFail()
    {
        var report = GraphExpansionOptInComparisonRunner.BuildReportFromSamples(
            "test",
            [
                ComparisonSample(
                    GraphExpansionTargetSection.AuditContext,
                    riskChecks: new GraphExpansionApplyRiskChecks { RiskAfterRoutingCount = 1 })
            ]);
        var gate = GraphExpansionOptInComparisonRunner.BuildGateReport(report, report);

        Assert.AreEqual(1, report.RiskAfterRoutingCount);
        Assert.IsFalse(gate.Passed);
    }

    private static async Task<(BasicContextPackageBuilder Builder, InMemoryContextStore Store)> CreateGraphApplyBuilderAsync(
        GraphExpansionApplyOptions options,
        params ContextRelation[] relations)
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var now = DateTimeOffset.UtcNow;
        await contextStore.SaveAsync(ContextItem("seed", "Seed", "seed content", now.AddMinutes(3)));
        await contextStore.SaveAsync(ContextItem("target-audit-old", "Audit Old", "deprecated audit context", now.AddMinutes(1)));
        await contextStore.SaveAsync(ContextItem("target-conflict", "Conflict Evidence", "conflict evidence context", now.AddMinutes(2)));

        var resolvedRelations = relations.Length > 0
            ? relations
            :
            [
                Relation("rel-audit-old", "seed", "target-audit-old", ContextRelationTypes.Replaces, 1.0, true, StableMemoryLifecycle.Deprecated),
                Relation("rel-conflict", "seed", "target-conflict", "conflicts_with", 1.0, true, StableMemoryLifecycle.Active)
            ];
        foreach (var relation in resolvedRelations)
        {
            await relationStore.SaveAsync(relation);
        }

        var profileRegistry = new RelationExpansionProfileRegistry();
        var validator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var preview = new RelationExpansionPreviewService(new RelationTraversalEngine(relationStore), profileRegistry, validator);
        var applyPolicy = new GraphExpansionApplyPolicy(preview, contextStore);
        var builder = new BasicContextPackageBuilder(
            contextStore,
            null,
            null,
            null,
            relationStore,
            tokenizerResolver: new DefaultContextTokenizerResolver(),
            graphExpansionApplyOptions: options,
            graphExpansionApplyPolicy: applyPolicy);
        return (builder, contextStore);
    }

    private static ContextItem ContextItem(
        string id,
        string title,
        string content,
        DateTimeOffset updatedAt)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "memory",
            Title = title,
            Content = content,
            ContentFormat = ContextContentFormat.Markdown,
            Importance = string.Equals(id, "seed", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.1,
            SourceRefs = [$"source:{id}"],
            CreatedAt = updatedAt.AddMinutes(-10),
            UpdatedAt = updatedAt
        };
    }

    private static ContextPackageRequest PackageRequest()
    {
        return new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "seed",
            TokenBudget = 4000,
            Policy = new ContextPackagePolicy
            {
                Id = "graph-apply-test-policy",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 1
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = "graph-apply-test"
            }
        };
    }

    private static GraphExpansionApplyOptions ApplyOptions(IReadOnlyList<string> profiles)
    {
        return new GraphExpansionApplyOptions
        {
            Mode = GraphExpansionApplyOptions.ApplyGuardedMode,
            ApplyMode = GraphExpansionApplyOptions.ProfileScopedApplyMode,
            OptInProfiles = profiles,
            AllowedTargetSections =
            [
                GraphExpansionTargetSection.AuditContext,
                GraphExpansionTargetSection.ConflictEvidence,
                GraphExpansionTargetSection.HistoricalContext,
                GraphExpansionTargetSection.DiagnosticsOnly
            ],
            DisallowNormalContextInjection = true,
            FallbackOnRisk = true,
            MaxAddedItemsPerPackage = 10,
            EmitComparisonTrace = true
        };
    }

    private static GraphExpansionOptInComparisonSample ComparisonSample(
        string targetSection,
        int warningDelta = 0,
        int addedAudit = 0,
        int addedConflict = 0,
        bool normalSelectedSetChanged = false,
        GraphExpansionApplyRiskChecks? riskChecks = null)
    {
        return new GraphExpansionOptInComparisonSample
        {
            SampleId = $"sample-{targetSection}",
            Mode = "ChatMode",
            NormalSelectedSetChanged = normalSelectedSetChanged,
            AuxiliaryGraphSectionChanged = true,
            GraphExpansionApplied = true,
            GraphExpansionMode = GraphExpansionApplyOptions.ApplyGuardedMode,
            BaselineSelected = ["seed"],
            ApplySelected = normalSelectedSetChanged ? ["seed", "unexpected"] : ["seed"],
            AddedGraphItems = ["target"],
            TargetSections = [targetSection],
            AddedAuditContextItems = addedAudit,
            AddedConflictEvidenceItems = addedConflict,
            RiskChecks = riskChecks ?? new GraphExpansionApplyRiskChecks(),
            WarningDelta = warningDelta
        };
    }

    private static int ParseRisk(string value, string key)
    {
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0 || !string.Equals(part[..index], key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(part[(index + 1)..], out var parsed) ? parsed : 0;
        }

        return 0;
    }

    private static ContextRelation Relation(
        string id,
        string sourceId,
        string targetId,
        string relationType,
        double confidence,
        bool withEvidence,
        string targetLifecycle = "Active")
    {
        var sourceRefs = withEvidence ? [$"evidence-{id}"] : Array.Empty<string>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lifecycle"] = "Active",
            ["reviewStatus"] = RelationReviewStatuses.Reviewed,
            ["targetLifecycle"] = targetLifecycle,
            ["targetExists"] = "true"
        };
        if (withEvidence)
        {
            metadata["evidenceRefs"] = string.Join(",", sourceRefs);
        }

        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = confidence,
            SourceRefs = sourceRefs,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow
        };
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
