using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreRelationGraphValidationTests
{
    [TestMethod]
    public void RelationTypeRegistry_ShouldReturnKnownTypes()
    {
        var registry = new RelationTypeRegistry();

        var types = registry.GetAll();

        Assert.IsTrue(types.Any(item => item.Type == "contains"));
        Assert.IsTrue(types.Any(item => item.Type == "references"));
        Assert.IsTrue(types.Any(item => item.Type == ContextRelationTypes.SupersededBy));
        Assert.IsTrue(types.Any(item => item.Type == ContextRelationTypes.Replaces));
        Assert.AreEqual(ContextRelationTypes.Replaces, registry.Find(ContextRelationTypes.SupersededBy)!.InverseType);
    }

    [TestMethod]
    public async Task Validation_ShouldReportUnknownRelationType()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-unknown", "stable-a", "made_up", "stable-b"));

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.UnknownRelationType));
    }

    [TestMethod]
    public async Task Validation_ShouldReportMissingInverse()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-old", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-new"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-new"));
        await fixture.RelationStore.SaveAsync(Relation("rel-super", "stable-old", ContextRelationTypes.SupersededBy, "stable-new", withEvidence: true));

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.MissingInverseRelation));
    }

    [TestMethod]
    public async Task Validation_ShouldReportBrokenTarget()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.RelationStore.SaveAsync(Relation("rel-broken", "stable-a", "references", "missing-target", withEvidence: true));

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.BrokenTarget));
    }

    [TestMethod]
    public async Task Validation_ShouldReportMissingEvidence()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-no-evidence", "stable-a", "references", "stable-b"));

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.MissingEvidence));
    }

    [TestMethod]
    public async Task Validation_ShouldReportLowConfidence()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(new ContextRelation
        {
            Id = "rel-low-confidence",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "stable-a",
            TargetId = "stable-b",
            RelationType = "references",
            Weight = 1.0,
            Confidence = 0.2,
            SourceRefs = ["evidence-low-confidence"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reviewStatus"] = "Reviewed",
                ["lifecycle"] = StableMemoryLifecycle.Active
            },
            CreatedAt = DateTimeOffset.UtcNow
        });

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.LowConfidence));
    }

    [TestMethod]
    public async Task Validation_ShouldReportCandidateRelationUsedInNormalPath()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(new ContextRelation
        {
            Id = "rel-candidate-normal",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "stable-a",
            TargetId = "stable-b",
            RelationType = "references",
            Weight = 1.0,
            Confidence = 0.8,
            SourceRefs = ["evidence-candidate"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reviewStatus"] = "Reviewed",
                ["lifecycle"] = ContextMemoryStatus.Candidate.ToString(),
                ["allowsNormalExpansion"] = "true"
            },
            CreatedAt = DateTimeOffset.UtcNow
        });

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.CandidateRelationUsedInNormalPath));
    }

    [TestMethod]
    public async Task Validation_ShouldReportSupersedeCycle()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-b"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-a"));
        await fixture.RelationStore.SaveManyAsync(
        [
            Relation("rel-a-b-super", "stable-a", ContextRelationTypes.SupersededBy, "stable-b", withEvidence: true),
            Relation("rel-b-a-repl", "stable-b", ContextRelationTypes.Replaces, "stable-a", withEvidence: true),
            Relation("rel-b-a-super", "stable-b", ContextRelationTypes.SupersededBy, "stable-a", withEvidence: true),
            Relation("rel-a-b-repl", "stable-a", ContextRelationTypes.Replaces, "stable-b", withEvidence: true)
        ]);

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.SupersedeCycle));
    }

    [TestMethod]
    public async Task Validation_ShouldReportDuplicateRelation()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveManyAsync(
        [
            Relation("rel-dup-1", "stable-a", "references", "stable-b", withEvidence: true),
            Relation("rel-dup-2", "stable-a", "references", "stable-b", withEvidence: true)
        ]);

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.DuplicateRelation));
    }

    [TestMethod]
    public async Task Validation_ShouldPassS3SupersededByReplacesPair()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-old", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-new"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-new", replaces: "stable-old"));
        await fixture.RelationStore.SaveManyAsync(
        [
            Relation("rel-s3-super", "stable-old", ContextRelationTypes.SupersededBy, "stable-new", withEvidence: true),
            Relation("rel-s3-replaces", "stable-new", ContextRelationTypes.Replaces, "stable-old", withEvidence: true)
        ]);

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsFalse(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.MissingInverseRelation));
        Assert.IsFalse(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.BrokenTarget));
        Assert.IsFalse(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.SupersedeCycle));
    }

    [TestMethod]
    public async Task Explain_ShouldReturnSourceTargetInverseAndEvidence()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-old", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-new"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-new", replaces: "stable-old"));
        await fixture.RelationStore.SaveManyAsync(
        [
            Relation("rel-s3-super", "stable-old", ContextRelationTypes.SupersededBy, "stable-new", withEvidence: true),
            Relation("rel-s3-replaces", "stable-new", ContextRelationTypes.Replaces, "stable-old", withEvidence: true)
        ]);

        var explain = await fixture.Service.ExplainAsync("rel-s3-super", "workspace-test", "collection-test");

        Assert.IsNotNull(explain);
        Assert.AreEqual("rel-s3-super", explain!.RelationId);
        Assert.AreEqual("StableMemory", explain.SourceItem!.Kind);
        Assert.AreEqual("StableMemory", explain.TargetItem!.Kind);
        Assert.AreEqual("rel-s3-replaces", explain.InverseRelation!.Id);
        Assert.AreEqual(1.0, explain.Confidence);
        Assert.AreEqual(StableMemoryLifecycle.Active, explain.Lifecycle);
        Assert.AreEqual("Reviewed", explain.ReviewStatus);
        Assert.IsTrue(explain.EvidenceRefs.Contains("slr-test"));
        Assert.AreEqual(1, explain.Evidence.Count);
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderRelationDiagnostics()
    {
        var snapshot = new ServiceRelationsSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079",
            ItemId = "stable-a",
            RelationTypes =
            [
                new RelationTypeDefinition
                {
                    Type = ContextRelationTypes.SupersededBy,
                    InverseType = ContextRelationTypes.Replaces,
                    DefaultWeight = 1.0,
                    RequiresEvidence = true
                }
            ],
            Relations = new ContextCoreRelationsResponse
            {
                ItemId = "stable-a",
                Outgoing =
                [
                    Relation("rel-1", "stable-a", ContextRelationTypes.SupersededBy, "stable-b", withEvidence: true)
                ]
            },
            Diagnostics = new RelationGraphDiagnosticsReport
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                RelationCount = 1,
                DiagnosticCount = 1,
                Diagnostics =
                [
                    new RelationGraphDiagnostic
                    {
                        DiagnosticType = RelationGraphDiagnosticTypes.MissingInverseRelation,
                        Severity = "High",
                        RelationId = "rel-1",
                        SourceId = "stable-a",
                        RelationType = ContextRelationTypes.SupersededBy,
                        TargetId = "stable-b",
                        Reason = "Missing inverse relation replaces."
                    }
                ]
            }
        };

        var rendered = ServiceOperationalRenderer.RenderRelations(snapshot);

        StringAssert.Contains(rendered, "Relation Types");
        StringAssert.Contains(rendered, "Global Relation Diagnostics");
        StringAssert.Contains(rendered, RelationGraphDiagnosticTypes.MissingInverseRelation);
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderRelationExplain()
    {
        var rendered = ServiceOperationalRenderer.RenderRelationExplain(new RelationExplainResponse
        {
            RelationId = "rel-explain",
            Relation = Relation("rel-explain", "stable-old", ContextRelationTypes.SupersededBy, "stable-new", withEvidence: true),
            TypeDefinition = new RelationTypeDefinition
            {
                Type = ContextRelationTypes.SupersededBy,
                InverseType = ContextRelationTypes.Replaces,
                RequiresEvidence = true
            },
            SourceItem = new RelationItemReference
            {
                ItemId = "stable-old",
                Kind = "StableMemory",
                Lifecycle = StableMemoryLifecycle.Superseded
            },
            TargetItem = new RelationItemReference
            {
                ItemId = "stable-new",
                Kind = "StableMemory",
                Lifecycle = StableMemoryLifecycle.Current
            },
            EvidenceRefs = ["slr-test"],
            SourceRefs = ["slr-test"],
            Confidence = 1.0,
            ConfidenceReason = "stable_lifecycle_review",
            Lifecycle = StableMemoryLifecycle.Active,
            ReviewStatus = "Reviewed",
            Evidence =
            [
                new RelationEvidence
                {
                    EvidenceId = "re-1",
                    RelationId = "rel-explain",
                    EvidenceKind = "stable_lifecycle_review",
                    EvidenceText = "review accepted"
                }
            ],
            Diagnostics =
            [
                new RelationGraphDiagnostic
                {
                    DiagnosticType = RelationGraphDiagnosticTypes.MissingInverseRelation,
                    Severity = "High",
                    Reason = "sample"
                }
            ]
        });

        StringAssert.Contains(rendered, "Service Relation Explain");
        StringAssert.Contains(rendered, "stable_lifecycle_review");
        StringAssert.Contains(rendered, "EvidenceRefs");
        StringAssert.Contains(rendered, RelationGraphDiagnosticTypes.MissingInverseRelation);
    }

    private static RelationGraphFixture CreateFixture()
    {
        var relationStore = new InMemoryRelationStore();
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var registry = new RelationTypeRegistry();
        var service = new RelationGraphValidationService(
            relationStore,
            null,
            memoryStore,
            constraintStore,
            globalStore,
            registry);
        return new RelationGraphFixture(relationStore, memoryStore, constraintStore, globalStore, registry, service);
    }

    private static ContextMemoryItem StableMemory(
        string id,
        string lifecycle = StableMemoryLifecycle.Current,
        string? supersededBy = null,
        string? replaces = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lifecycle"] = lifecycle,
            ["sourceStableReviewCandidateId"] = $"src-{id}",
            ["evidenceRefs"] = $"event-{id}"
        };
        if (!string.IsNullOrWhiteSpace(supersededBy))
        {
            metadata["supersededBy"] = supersededBy;
        }

        if (!string.IsNullOrWhiteSpace(replaces))
        {
            metadata["replaces"] = replaces;
        }

        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = string.Equals(lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
                ? ContextMemoryStatus.Deprecated
                : ContextMemoryStatus.Stable,
            Type = "preference",
            Content = id,
            SourceRefs = [$"event-{id}"],
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRelation Relation(
        string id,
        string sourceId,
        string relationType,
        string targetId,
        bool withEvidence = false)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = withEvidence ? ["slr-test"] : Array.Empty<string>(),
            Metadata = withEvidence
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "stable_lifecycle_review",
                    ["reviewId"] = "slr-test",
                    ["lifecycle"] = StableMemoryLifecycle.Active,
                    ["reviewStatus"] = "Reviewed",
                    ["confidenceReason"] = "stable_lifecycle_review",
                    ["evidenceRefs"] = "slr-test",
                    ["sourceRefs"] = "slr-test"
                }
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed record RelationGraphFixture(
        InMemoryRelationStore RelationStore,
        InMemoryMemoryStore MemoryStore,
        InMemoryConstraintStore ConstraintStore,
        InMemoryGlobalContextStore GlobalStore,
        RelationTypeRegistry Registry,
        RelationGraphValidationService Service);
}
