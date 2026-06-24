using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreStableMemoryGovernanceTests
{
    [TestMethod]
    public async Task Snapshot_ShouldCountStableMemories()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-memory-1", "preference", "Keep concise answers.", ["evidence-1"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-decision-1", "decision", "Use filesystem store.", ["evidence-2"]));
        await fixture.ConstraintStore.SaveAsync(CreateStableConstraint("stable-constraint-1", "Never promote repeated clarification.", ["evidence-3"]));
        await fixture.GlobalStore.SaveAsync(CreateGlobalMemory("global-memory-1", "Global preference.", ["evidence-4"]));

        var snapshot = await fixture.Service.GetSnapshotAsync("workspace-test", "collection-test");

        Assert.AreEqual(1, snapshot.StableMemoryCount);
        Assert.AreEqual(1, snapshot.DecisionRecordCount);
        Assert.AreEqual(1, snapshot.StableConstraintCount);
        Assert.AreEqual(1, snapshot.GlobalMemoryCount);
        Assert.AreEqual(4, snapshot.RecentStableItems.Count);
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectMissingProvenance()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-no-provenance",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "preference",
            Content = "Stable item without source.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.AreEqual(1, diagnostics.MissingProvenanceCount);
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MissingProvenance));
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectDuplicateStableMemory()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-dup-1", "preference", "Duplicate stable content.", ["evidence-1"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-dup-2", "preference", "Duplicate stable content.", ["evidence-2"]));

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.DuplicateStableMemoryCount >= 2);
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.DiagnosticType == StableMemoryDiagnosticTypes.DuplicateStableMemory));
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectSupersededWithoutReplacement()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory(
            "stable-superseded",
            "preference",
            "Old stable content.",
            ["evidence-1"],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Superseded,
                ["sourceStableReviewCandidateId"] = "src-superseded",
                ["evidenceRefs"] = "evidence-1"
            }));

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.AreEqual(1, diagnostics.SupersededWithoutReplacementCount);
        Assert.AreEqual(StableMemoryDiagnosticTypes.SupersededWithoutReplacement, diagnostics.Diagnostics[0].DiagnosticType);
    }

    [TestMethod]
    public async Task Explain_ShouldReturnProvenanceChain()
    {
        var fixture = CreateFixture();
        await fixture.StableReviewStore.SaveAsync(new StableReviewCandidate
        {
            StableReviewCandidateId = "src-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceCandidateId = "stpc-1",
            SourceTargetItemId = "candidate-memory-1",
            Kind = "CandidateMemory",
            Title = "Keep concise answers.",
            Summary = "Keep concise answers.",
            SuggestedStableTarget = "StableMemory",
            Reason = "Accepted by reviewer.",
            Confidence = 0.9,
            Importance = 0.8,
            EvidenceRefs = ["evidence-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            Status = StableReviewCandidateStatuses.Accepted
        });
        await fixture.MemoryStore.SaveAsync(CreateStableMemory(
            "stable:mem:src-1",
            "preference",
            "Keep concise answers.",
            ["evidence-1"],
            metadata: CreateStableMetadata("src-1", "stpc-1", "working-1", "feedback-1", "case-1", ["evidence-1"])));

        var explanation = await fixture.Service.ExplainAsync("stable:mem:src-1", "workspace-test", "collection-test");

        Assert.IsNotNull(explanation);
        Assert.IsNotNull(explanation!.Provenance);
        Assert.IsNotNull(explanation.Provenance!.StableReviewCandidate);
        Assert.AreEqual("src-1", explanation.Provenance.StableReviewCandidate!.StableReviewCandidateId);
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderStableMemoryPage()
    {
        var snapshot = new ServiceStableMemorySnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079",
            Snapshot = new StableMemorySnapshot
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                StableMemoryCount = 1,
                StableConstraintCount = 1,
                DecisionRecordCount = 1,
                GlobalMemoryCount = 1,
                MissingProvenanceCount = 1,
                RecentStableItems =
                [
                    new StableMemoryRecord
                    {
                        Id = "stable-memory-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        StableKind = StableMemoryKinds.StableMemory,
                        Type = "preference",
                        Title = "Keep concise answers.",
                        Status = ContextMemoryStatus.Stable,
                        Lifecycle = StableMemoryLifecycle.Current,
                        EvidenceRefs = ["evidence-1"]
                    }
                ]
            },
            Diagnostics = new StableMemoryDiagnosticsReport
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                DiagnosticCount = 1,
                MissingProvenanceCount = 1,
                Diagnostics =
                [
                    new StableMemoryDiagnostic
                    {
                        StableItemId = "stable-memory-1",
                        StableKind = StableMemoryKinds.StableMemory,
                        DiagnosticType = StableMemoryDiagnosticTypes.MissingProvenance,
                        Severity = "High",
                        Reason = "Stable item has no source refs."
                    }
                ]
            }
        };

        var rendered = ServiceOperationalRenderer.RenderStableMemory(snapshot);

        StringAssert.Contains(rendered, "Service Stable Memory");
        StringAssert.Contains(rendered, "StableMemoryCount");
        StringAssert.Contains(rendered, "Recent Stable Items");
        StringAssert.Contains(rendered, "Diagnostics");
        StringAssert.Contains(rendered, "stable-memory-1");
    }

    [TestMethod]
    public async Task DeprecateStableItem_ShouldRecordReview()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-deprecate", "preference", "Old stable preference.", ["evidence-1"]));

        var result = await fixture.ReviewService.DeprecateAsync("stable-deprecate", CreateLifecycleRequest("deprecated by reviewer"));
        var updated = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "stable-deprecate");
        var reviews = await fixture.ReviewStore.QueryReviewsAsync("stable-deprecate");

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, result!.ToStatus);
        Assert.AreEqual(StableMemoryLifecycle.Deprecated, result.ToLifecycle);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, updated!.Status);
        Assert.AreEqual(StableMemoryLifecycle.Deprecated, updated.Metadata["lifecycle"]);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(StableLifecycleReviewActions.Deprecate, reviews[0].Action);
    }

    [TestMethod]
    public async Task RejectStableItem_ShouldRecordReview()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-reject", "preference", "Reject stable preference.", ["evidence-1"]));

        var result = await fixture.ReviewService.RejectAsync("stable-reject", CreateLifecycleRequest("invalid stable item"));
        var updated = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "stable-reject");
        var reviews = await fixture.ReviewStore.QueryReviewsAsync("stable-reject");

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Rejected, result!.ToStatus);
        Assert.AreEqual(ContextMemoryStatus.Rejected, updated!.Status);
        Assert.AreEqual(StableMemoryLifecycle.Rejected, updated.Metadata["lifecycle"]);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(StableLifecycleReviewActions.Reject, reviews[0].Action);
    }

    [TestMethod]
    public async Task SupersedeStableItem_ShouldRequireReplacement()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-old", "preference", "Old stable preference.", ["evidence-old"]));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.ReviewService.SupersedeAsync("stable-old", CreateLifecycleRequest("missing replacement")));
    }

    [TestMethod]
    public async Task SupersedeStableItem_ShouldWriteSupersededByAndReplacesMetadata()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-old", "preference", "Old stable preference.", ["evidence-old"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-new", "preference", "New stable preference.", ["evidence-new"]));

        var result = await fixture.ReviewService.SupersedeAsync(
            "stable-old",
            CreateLifecycleRequest("newer stable item", replacementItemId: "stable-new"));
        var oldItem = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "stable-old");
        var newItem = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "stable-new");
        var reviews = await fixture.ReviewStore.QueryReviewsAsync("stable-old");

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, result!.ToStatus);
        Assert.AreEqual(StableMemoryLifecycle.Superseded, result.ToLifecycle);
        Assert.AreEqual("stable-new", oldItem!.Metadata["supersededBy"]);
        Assert.AreEqual(StableMemoryLifecycle.Superseded, oldItem.Metadata["lifecycle"]);
        Assert.AreEqual("stable-old", newItem!.Metadata["replaces"]);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("stable-new", reviews[0].ReplacementItemId);
    }

    [TestMethod]
    public async Task SupersedeStableItem_ShouldWriteReplacementRelations()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-old", "preference", "Old stable preference.", ["evidence-old"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-new", "preference", "New stable preference.", ["evidence-new"]));

        var result = await fixture.ReviewService.SupersedeAsync(
            "stable-old",
            CreateLifecycleRequest("newer stable item", replacementItemId: "stable-new"));
        var outgoing = await fixture.RelationStore.QueryBySourceAsync("workspace-test", "collection-test", "stable-old");
        var replacementOutgoing = await fixture.RelationStore.QueryBySourceAsync("workspace-test", "collection-test", "stable-new");

        Assert.IsNotNull(result);
        Assert.IsTrue(outgoing.Any(relation =>
            relation.RelationType == ContextRelationTypes.SupersededBy
            && relation.TargetId == "stable-new"
            && relation.Metadata["source"] == "stable_lifecycle_review"
            && relation.Metadata["reviewId"] == result!.ReviewId
            && relation.Metadata["reviewer"] == "tester"
            && relation.Metadata["reason"] == "newer stable item"
            && relation.Metadata["confidence"] == "1.0"
            && relation.Metadata["confidenceReason"] == "stable_lifecycle_review"
            && relation.Metadata["lifecycle"] == StableMemoryLifecycle.Active
            && relation.Metadata["reviewStatus"] == "Reviewed"
            && relation.Metadata["sourceOperationId"] == result.OperationId
            && relation.Metadata["sourceItemId"] == "stable-old"
            && relation.Metadata["createdFrom"] == "stable_lifecycle_review"
            && relation.Metadata["sourceRefs"].Contains(result.ReviewId, StringComparison.OrdinalIgnoreCase)
            && relation.Metadata["evidenceRefs"].Contains("evidence-old", StringComparison.OrdinalIgnoreCase)
            && relation.Metadata["evidenceRefs"].Contains("evidence-new", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(replacementOutgoing.Any(relation =>
            relation.RelationType == ContextRelationTypes.Replaces
            && relation.TargetId == "stable-old"
            && relation.Metadata["reviewId"] == result!.ReviewId
            && relation.Metadata["confidenceReason"] == "stable_lifecycle_review"
            && relation.Metadata["reviewStatus"] == "Reviewed"));
    }

    [TestMethod]
    public async Task ReplacementChain_ShouldReturnLatestItem()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-v1", "preference", "Old stable preference.", ["evidence-1"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-v2", "preference", "Middle stable preference.", ["evidence-2"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-v3", "preference", "Latest stable preference.", ["evidence-3"]));
        await fixture.ReviewService.SupersedeAsync("stable-v1", CreateLifecycleRequest("v2", replacementItemId: "stable-v2"));
        await fixture.ReviewService.SupersedeAsync("stable-v2", CreateLifecycleRequest("v3", replacementItemId: "stable-v3"));

        var chain = await fixture.Service.GetReplacementChainAsync("stable-v1", "workspace-test", "collection-test");

        Assert.IsNotNull(chain);
        Assert.AreEqual("stable-v1", chain!.RootItem!.Id);
        Assert.AreEqual("stable-v3", chain.LatestItem!.Id);
        Assert.IsTrue(chain.NextItems.Any(item => item.Id == "stable-v2"));
        Assert.IsTrue(chain.NextItems.Any(item => item.Id == "stable-v3"));
        Assert.IsTrue(chain.Relations.Any(relation => relation.RelationType == ContextRelationTypes.SupersededBy));
        Assert.IsTrue(chain.Relations.Any(relation => relation.RelationType == ContextRelationTypes.Replaces));
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectMetadataRelationMismatch()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory(
            "stable-old",
            "preference",
            "Old stable preference.",
            ["evidence-old"],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Superseded,
                ["supersededBy"] = "stable-new",
                ["sourceStableReviewCandidateId"] = "src-old",
                ["evidenceRefs"] = "evidence-old"
            }));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-new", "preference", "New stable preference.", ["evidence-new"]));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-other", "preference", "Other stable preference.", ["evidence-other"]));
        await fixture.RelationStore.SaveManyAsync(
        [
            CreateReplacementRelation("stable-old", "stable-other", ContextRelationTypes.SupersededBy),
            CreateReplacementRelation("stable-other", "stable-old", ContextRelationTypes.Replaces)
        ]);

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.MetadataRelationMismatchCount > 0);
        Assert.IsTrue(diagnostics.Diagnostics.Any(item =>
            item.StableItemId == "stable-old"
            && item.DiagnosticType == StableMemoryDiagnosticTypes.MetadataRelationMismatch));
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectBrokenReplacementLink()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory(
            "stable-old",
            "preference",
            "Old stable preference.",
            ["evidence-old"],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Superseded,
                ["supersededBy"] = "stable-new",
                ["sourceStableReviewCandidateId"] = "src-old",
                ["evidenceRefs"] = "evidence-old"
            }));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory("stable-new", "preference", "New stable preference.", ["evidence-new"]));
        await fixture.RelationStore.SaveAsync(CreateReplacementRelation("stable-old", "stable-new", ContextRelationTypes.SupersededBy));

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.BrokenReplacementLinkCount > 0);
        Assert.IsTrue(diagnostics.Diagnostics.Any(item =>
            item.StableItemId == "stable-old"
            && item.DiagnosticType == StableMemoryDiagnosticTypes.BrokenReplacementLink));
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectReplacementCycle()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateStableMemory(
            "stable-a",
            "preference",
            "A stable preference.",
            ["evidence-a"],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Superseded,
                ["supersededBy"] = "stable-b",
                ["sourceStableReviewCandidateId"] = "src-a",
                ["evidenceRefs"] = "evidence-a"
            }));
        await fixture.MemoryStore.SaveAsync(CreateStableMemory(
            "stable-b",
            "preference",
            "B stable preference.",
            ["evidence-b"],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Superseded,
                ["supersededBy"] = "stable-a",
                ["sourceStableReviewCandidateId"] = "src-b",
                ["evidenceRefs"] = "evidence-b"
            }));
        await fixture.RelationStore.SaveManyAsync(
        [
            CreateReplacementRelation("stable-a", "stable-b", ContextRelationTypes.SupersededBy),
            CreateReplacementRelation("stable-b", "stable-a", ContextRelationTypes.Replaces),
            CreateReplacementRelation("stable-b", "stable-a", ContextRelationTypes.SupersededBy),
            CreateReplacementRelation("stable-a", "stable-b", ContextRelationTypes.Replaces)
        ]);

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.ReplacementCycleCount > 0);
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.DiagnosticType == StableMemoryDiagnosticTypes.ReplacementCycle));
    }

    [TestMethod]
    public async Task InvalidTransition_ShouldThrowStructuredValidationExceptionSource()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-rejected",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Rejected,
            Type = "preference",
            Content = "Already rejected.",
            SourceRefs = ["evidence-1"],
            Importance = 0.8,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Rejected,
                ["status"] = ContextMemoryStatus.Rejected.ToString(),
                ["sourceStableReviewCandidateId"] = "src-stable-rejected",
                ["evidenceRefs"] = "evidence-1"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.ReviewService.RejectAsync("stable-rejected", CreateLifecycleRequest("duplicate reject")));
        StringAssert.Contains(ex.Message, "Rejected");
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderStableLifecycleReviewResult()
    {
        var result = new StableLifecycleReviewResult
        {
            OperationId = "stable-review-op-1",
            StableItemId = "stable-memory-1",
            StableKind = StableMemoryKinds.StableMemory,
            Action = StableLifecycleReviewActions.Deprecate,
            FromStatus = ContextMemoryStatus.Stable,
            ToStatus = ContextMemoryStatus.Deprecated,
            FromLifecycle = StableMemoryLifecycle.Current,
            ToLifecycle = StableMemoryLifecycle.Deprecated,
            ReviewId = "slr-1",
            Reviewer = "tester",
            Reason = "old item",
            ReviewedAt = DateTimeOffset.UtcNow
        };

        var rendered = ServiceOperationalRenderer.RenderStableLifecycleReviewResult(result);

        StringAssert.Contains(rendered, "Stable Lifecycle Review Result");
        StringAssert.Contains(rendered, "stable-memory-1");
        StringAssert.Contains(rendered, "Deprecate");
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderReplacementChain()
    {
        var chain = new StableReplacementChainResponse
        {
            ItemId = "stable-v1",
            CurrentItem = new StableMemoryRecord
            {
                Id = "stable-v1",
                StableKind = StableMemoryKinds.StableMemory,
                Status = ContextMemoryStatus.Deprecated,
                Lifecycle = StableMemoryLifecycle.Superseded
            },
            RootItem = new StableMemoryRecord { Id = "stable-v1" },
            LatestItem = new StableMemoryRecord
            {
                Id = "stable-v2",
                Status = ContextMemoryStatus.Stable,
                Lifecycle = StableMemoryLifecycle.Current
            },
            NextItems =
            [
                new StableMemoryRecord
                {
                    Id = "stable-v2",
                    StableKind = StableMemoryKinds.StableMemory,
                    Status = ContextMemoryStatus.Stable,
                    Lifecycle = StableMemoryLifecycle.Current,
                    Title = "Latest stable preference."
                }
            ],
            Relations =
            [
                CreateReplacementRelation("stable-v1", "stable-v2", ContextRelationTypes.SupersededBy)
            ],
            Warnings = ["sample warning"]
        };

        var rendered = ServiceOperationalRenderer.RenderStableReplacementChain(chain);

        StringAssert.Contains(rendered, "Stable Replacement Chain");
        StringAssert.Contains(rendered, "stable-v2");
        StringAssert.Contains(rendered, "superseded_by");
        StringAssert.Contains(rendered, "sample warning");
    }

    private static StableMemoryFixture CreateFixture()
    {
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var relationStore = new InMemoryRelationStore();
        var stableReviewStore = new InMemoryStableReviewCandidateStore();
        var promotionStore = new InMemoryShortTermPromotionCandidateStore();
        var learningStore = new InMemoryContextLearningStore();
        var shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        var provenance = new ContextProvenanceService(
            memoryStore,
            constraintStore,
            stableReviewStore,
            promotionStore,
            learningStore,
            shortTermStore);
        var service = new StableMemoryGovernanceService(
            memoryStore,
            constraintStore,
            globalStore,
            relationStore,
            provenance);
        var reviewStore = new InMemoryStableLifecycleReviewStore();
        var reviewService = new StableLifecycleReviewService(
            memoryStore,
            constraintStore,
            globalStore,
            reviewStore,
            relationStore,
            service);
        return new StableMemoryFixture(memoryStore, constraintStore, globalStore, relationStore, stableReviewStore, reviewStore, service, reviewService);
    }

    private static StableLifecycleReviewRequest CreateLifecycleRequest(
        string reason,
        string? replacementItemId = null)
    {
        return new StableLifecycleReviewRequest
        {
            OperationId = $"stable-review-{Guid.NewGuid():N}",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Reviewer = "tester",
            Reason = reason,
            ReplacementItemId = replacementItemId,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = "true"
            }
        };
    }

    private static ContextMemoryItem CreateStableMemory(
        string id,
        string type,
        string content,
        IReadOnlyList<string> evidenceRefs,
        Dictionary<string, string>? metadata = null)
    {
        metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceStableReviewCandidateId"] = $"src-{id}",
            ["sourcePromotionCandidateId"] = $"stpc-{id}",
            ["evidenceRefs"] = string.Join(",", evidenceRefs),
            ["createdFrom"] = "stable_review_accept"
        };

        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = type,
            Content = content,
            SourceRefs = evidenceRefs.ToArray(),
            Importance = 0.8,
            Confidence = 0.9,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRelation CreateReplacementRelation(
        string sourceId,
        string targetId,
        string relationType)
    {
        return new ContextRelation
        {
            Id = $"rel-{sourceId}-{relationType}-{targetId}",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = ["slr-test"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "stable_lifecycle_review",
                ["reviewId"] = "slr-test",
                ["reviewer"] = "tester",
                ["reason"] = "test",
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["confidence"] = "1.0",
                ["lifecycle"] = StableMemoryLifecycle.Active
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextConstraint CreateStableConstraint(
        string id,
        string content,
        IReadOnlyList<string> evidenceRefs)
    {
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Hard,
            Content = content,
            SourceRefs = evidenceRefs.ToArray(),
            Status = ContextMemoryStatus.Active,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceStableReviewCandidateId"] = $"src-{id}",
                ["sourcePromotionCandidateId"] = $"stpc-{id}",
                ["evidenceRefs"] = string.Join(",", evidenceRefs),
                ["createdFrom"] = "candidate_constraint_activate"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextGlobalItem CreateGlobalMemory(
        string id,
        string content,
        IReadOnlyList<string> evidenceRefs)
    {
        return new ContextGlobalItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = null,
            Scope = ContextScope.Workspace,
            Type = "preference",
            Content = content,
            SourceRefs = evidenceRefs.ToArray(),
            Importance = 0.7,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceStableReviewCandidateId"] = $"src-{id}",
                ["evidenceRefs"] = string.Join(",", evidenceRefs)
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static Dictionary<string, string> CreateStableMetadata(
        string stableReviewId,
        string promotionId,
        string workingId,
        string feedbackId,
        string learningCaseId,
        IReadOnlyList<string> evidenceRefs)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceStableReviewCandidateId"] = stableReviewId,
            ["sourcePromotionCandidateId"] = promotionId,
            ["sourceWorkingItemId"] = workingId,
            ["sourceFeedbackId"] = feedbackId,
            ["sourceLearningCaseId"] = learningCaseId,
            ["evidenceRefs"] = string.Join(",", evidenceRefs),
            ["reviewer"] = "tester",
            ["reviewReason"] = "accepted",
            ["policyVersion"] = "test",
            ["createdFrom"] = "stable_review_accept"
        };
    }

    private sealed record StableMemoryFixture(
        InMemoryMemoryStore MemoryStore,
        InMemoryConstraintStore ConstraintStore,
        InMemoryGlobalContextStore GlobalStore,
        InMemoryRelationStore RelationStore,
        InMemoryStableReviewCandidateStore StableReviewStore,
        InMemoryStableLifecycleReviewStore ReviewStore,
        StableMemoryGovernanceService Service,
        StableLifecycleReviewService ReviewService);
}
