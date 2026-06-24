using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

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
    public async Task Validation_ShouldSuggestNormalizedTypeForLegacyRelation()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-new"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-old"));
        await fixture.RelationStore.SaveAsync(Relation("rel-legacy", "stable-new", "supersedes", "stable-old", withEvidence: true));

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");
        var diagnostic = report.Diagnostics.Single(item => item.DiagnosticType == RelationGraphDiagnosticTypes.LegacyRelationType);

        Assert.AreEqual("supersedes", diagnostic.RelationType);
        Assert.AreEqual(ContextRelationTypes.Replaces, diagnostic.Metadata["normalizedType"]);
        Assert.IsFalse(report.Diagnostics.Any(item =>
            item.RelationId == "rel-legacy"
            && item.DiagnosticType == RelationGraphDiagnosticTypes.UnknownRelationType));
    }

    [TestMethod]
    public async Task Validation_ShouldSuggestEvidenceBackfillForDeterministicEvalRelation()
    {
        var fixture = CreateFixture();
        await fixture.RelationStore.SaveAsync(new ContextRelation
        {
            Id = "rel:eval-fixture",
            WorkspaceId = "eval-chat",
            CollectionId = "collection-test",
            SourceId = "source-a",
            TargetId = "target-b",
            RelationType = "references",
            Weight = 1.0,
            Confidence = 1.0,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var report = await fixture.Service.ValidateAsync("eval-chat", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item =>
            item.RelationId == "rel:eval-fixture"
            && item.DiagnosticType == RelationGraphDiagnosticTypes.EvidenceBackfillRequired));
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
    public async Task ReviewRelation_ShouldRecordHistory()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-review", "stable-a", "contains", "stable-b"));

        var result = await fixture.ReviewService.ReviewAsync("rel-review", ReviewRequest("manual verification"));

        Assert.IsNotNull(result);
        Assert.AreEqual(RelationReviewActions.Review, result!.Action);
        Assert.AreEqual(RelationReviewStatuses.Reviewed, result.ToReviewStatus);
        var reviews = await fixture.ReviewService.GetReviewsAsync("rel-review");
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(result.Review.ReviewId, reviews[0].ReviewId);
    }

    [TestMethod]
    public async Task RejectRelation_ShouldUpdateLifecycle()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-reject", "stable-a", "contains", "stable-b"));

        var result = await fixture.ReviewService.RejectAsync("rel-reject", ReviewRequest("bad relation"));
        var updated = (await fixture.RelationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = int.MaxValue
        })).Single(item => item.Id == "rel-reject");

        Assert.IsNotNull(result);
        Assert.AreEqual(StableMemoryLifecycle.Rejected, result!.ToLifecycle);
        Assert.AreEqual(RelationReviewStatuses.Rejected, updated.Metadata["reviewStatus"]);
        Assert.AreEqual(StableMemoryLifecycle.Rejected, updated.Metadata["lifecycle"]);
    }

    [TestMethod]
    public async Task DeprecateRelation_ShouldUpdateLifecycle()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-deprecate", "stable-a", "contains", "stable-b"));

        var result = await fixture.ReviewService.DeprecateAsync("rel-deprecate", ReviewRequest("old relation"));

        Assert.IsNotNull(result);
        Assert.AreEqual(StableMemoryLifecycle.Deprecated, result!.ToLifecycle);
        Assert.AreEqual(StableMemoryLifecycle.Deprecated, result.Relation.Metadata["lifecycle"]);
    }

    [TestMethod]
    public async Task MarkRelationNeedsEvidence_ShouldUpdateReviewStatus()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-needs-evidence", "stable-a", "references", "stable-b", withEvidence: true));

        var result = await fixture.ReviewService.MarkNeedsEvidenceAsync("rel-needs-evidence", ReviewRequest("needs source proof"));

        Assert.IsNotNull(result);
        Assert.AreEqual(RelationReviewStatuses.NeedsEvidence, result!.ToReviewStatus);
        Assert.AreEqual(RelationReviewStatuses.NeedsEvidence, result.Relation.Metadata["reviewStatus"]);
    }

    [TestMethod]
    public async Task HighImpactRelation_ShouldRequireReason()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-high-impact", "stable-a", "references", "stable-b", withEvidence: true));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.ReviewService.ReviewAsync("rel-high-impact", ReviewRequest(string.Empty)));
    }

    [TestMethod]
    public async Task Validation_ShouldReportRejectedRelationWithActiveInverse()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-old", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-new"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-new", replaces: "stable-old"));
        await fixture.RelationStore.SaveManyAsync(
        [
            WithMetadata(Relation("rel-rejected-super", "stable-old", ContextRelationTypes.SupersededBy, "stable-new", withEvidence: true), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = StableMemoryLifecycle.Rejected,
                ["reviewStatus"] = RelationReviewStatuses.Rejected,
                ["reviewId"] = "rrv-rejected",
                ["reviewer"] = "tester"
            }),
            Relation("rel-active-replaces", "stable-new", ContextRelationTypes.Replaces, "stable-old", withEvidence: true)
        ]);

        var report = await fixture.Service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Diagnostics.Any(item => item.DiagnosticType == RelationGraphDiagnosticTypes.RejectedRelationHasActiveInverse));
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
    public void ControlRoom_ShouldRenderRelationReviewResult()
    {
        var rendered = ServiceOperationalRenderer.RenderRelationReviewResult(new RelationReviewResult
        {
            OperationId = "op-relation-review",
            RelationId = "rel-1",
            Action = RelationReviewActions.Review,
            FromLifecycle = StableMemoryLifecycle.Active,
            ToLifecycle = StableMemoryLifecycle.Active,
            FromReviewStatus = string.Empty,
            ToReviewStatus = RelationReviewStatuses.Reviewed,
            Reviewer = "tester",
            Reason = "verified",
            ReviewedAt = DateTimeOffset.UtcNow,
            Relation = Relation("rel-1", "stable-a", "contains", "stable-b"),
            Review = new RelationReviewRecord { ReviewId = "rrv-1", RelationId = "rel-1" }
        });

        StringAssert.Contains(rendered, "Service Relation Review Result");
        StringAssert.Contains(rendered, RelationReviewActions.Review);
        StringAssert.Contains(rendered, "tester");
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

    [TestMethod]
    public async Task RelationExpansionPreview_NormalProfile_ShouldBlockBackwardReplacementTraversal()
    {
        var fixture = CreateFixture();
        await fixture.RelationStore.SaveAsync(WithMetadata(
            ExpansionRelation(
                "rel-backward-replacement",
                "item-root",
                ContextRelationTypes.Replaces,
                "item-old",
                confidence: 1.0,
                withEvidence: true),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["targetLifecycle"] = StableMemoryLifecycle.Deprecated,
                ["targetExists"] = "true"
            }));

        var preview = await fixture.PreviewService.PreviewAsync(PreviewRequest("item-root", "normal-v1"));

        Assert.AreEqual(0, preview.AcceptedCount);
        Assert.IsTrue(preview.BlockedRelations.Any(relation =>
            relation.Reasons.Contains(RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked)
            && relation.Reasons.Contains(RelationExpansionValidationReasons.DeprecatedTargetBlocked)));
    }

    [TestMethod]
    public async Task RelationExpansionPreview_AuditProfile_ShouldAllowHistoricalRelation()
    {
        var fixture = CreateFixture();
        await fixture.RelationStore.SaveAsync(ExpansionRelation(
            "rel-audit-historical",
            "item-root",
            "replaced_by",
            "item-old",
            confidence: 1.0,
            withEvidence: true,
            lifecycle: StableMemoryLifecycle.Deprecated));

        var preview = await fixture.PreviewService.PreviewAsync(PreviewRequest("item-root", "audit-v1"));

        Assert.AreEqual(1, preview.AcceptedCount);
        Assert.AreEqual("rel-audit-historical", preview.AcceptedRelations[0].RelationId);
    }

    [TestMethod]
    public async Task RelationExpansionPreview_ShouldBlockLowConfidenceRelation()
    {
        var fixture = CreateFixture();
        await fixture.RelationStore.SaveAsync(ExpansionRelation(
            "rel-low-confidence",
            "item-root",
            "references",
            "item-target",
            confidence: 0.1,
            withEvidence: true));

        var preview = await fixture.PreviewService.PreviewAsync(PreviewRequest("item-root", "normal-v1"));

        Assert.IsTrue(preview.BlockedRelations.Any(relation =>
            relation.Reasons.Contains(RelationExpansionValidationReasons.ConfidenceTooLow)));
    }

    [TestMethod]
    public async Task RelationExpansionPreview_ShouldBlockMissingEvidenceWhenRequired()
    {
        var fixture = CreateFixture();
        await fixture.RelationStore.SaveAsync(ExpansionRelation(
            "rel-missing-evidence",
            "item-root",
            "references",
            "item-target",
            confidence: 1.0,
            withEvidence: false));

        var preview = await fixture.PreviewService.PreviewAsync(PreviewRequest("item-root", "normal-v1"));

        Assert.IsTrue(preview.BlockedRelations.Any(relation =>
            relation.Reasons.Contains(RelationExpansionValidationReasons.MissingEvidence)));
    }

    [TestMethod]
    public async Task RelationExpansionPreview_ShouldApplyFanoutCap()
    {
        var fixture = CreateFixture();
        for (var i = 0; i < 7; i++)
        {
            await fixture.RelationStore.SaveAsync(ExpansionRelation(
                $"rel-fanout-{i}",
                "item-root",
                ContextRelationTypes.DependsOn,
                $"item-target-{i}",
                confidence: 1.0,
                withEvidence: true));
        }

        var preview = await fixture.PreviewService.PreviewAsync(PreviewRequest("item-root", "current-task-v1"));

        Assert.AreEqual(6, preview.AcceptedCount);
        Assert.IsTrue(preview.BlockedRelations.Any(relation =>
            relation.Reasons.Contains(RelationExpansionValidationReasons.FanoutExceeded)));
    }

    [TestMethod]
    public void RelationExpansionValidator_ShouldApplyDepthCap()
    {
        var fixture = CreateFixture();
        var profile = fixture.ProfileRegistry.Find("normal-v1")!;
        var relation = ExpansionRelation("rel-depth", "item-root", "contains", "item-target", confidence: 1.0, withEvidence: true);

        var result = fixture.ExpansionValidator.Validate(relation, profile, depth: profile.MaxDepth + 1, fanoutIndex: 1);

        Assert.IsFalse(result.Accepted);
        Assert.IsTrue(result.Reasons.Contains(RelationExpansionValidationReasons.DepthExceeded));
    }

    [TestMethod]
    public async Task RelationExpansionPreview_ShouldNotMutateRelationStore()
    {
        var fixture = CreateFixture();
        await fixture.RelationStore.SaveAsync(ExpansionRelation(
            "rel-read-only",
            "item-root",
            "contains",
            "item-target",
            confidence: 1.0,
            withEvidence: true));
        var before = await fixture.RelationStore.QueryBySourceAsync("workspace-test", "collection-test", "item-root");

        _ = await fixture.PreviewService.PreviewAsync(PreviewRequest("item-root", "normal-v1"));

        var after = await fixture.RelationStore.QueryBySourceAsync("workspace-test", "collection-test", "item-root");
        CollectionAssert.AreEquivalent(before.Select(item => item.Id).ToArray(), after.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderRelationExpansionPreview()
    {
        var renderedProfiles = ServiceOperationalRenderer.RenderRelationExpansionProfiles(new RelationExpansionProfileRegistry().GetAll());
        var renderedPreview = ServiceOperationalRenderer.RenderRelationExpansionPreview(new RelationExpansionPreviewResponse
        {
            OperationId = "op-preview",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "item-root",
            Profile = new RelationExpansionProfile { ProfileId = "normal-v1", Mode = "Normal", Intent = "Default" },
            AcceptedCount = 1,
            BlockedCount = 1,
            AcceptedRelations =
            [
                new RelationExpansionPreviewRelation
                {
                    RelationId = "rel-ok",
                    SourceId = "item-root",
                    TargetId = "item-ok",
                    RelationType = "contains",
                    Confidence = 1.0,
                    TargetSection = GraphExpansionTargetSection.NormalContext,
                    SectionReason = "default normal context route",
                    RiskIfNormalSelected = false,
                    RiskAfterSectionRouting = false
                }
            ],
            BlockedRelations =
            [
                new RelationExpansionPreviewRelation
                {
                    RelationId = "rel-blocked",
                    SourceId = "item-root",
                    TargetId = "item-old",
                    RelationType = "replaced_by",
                    TargetSection = GraphExpansionTargetSection.Excluded,
                    SectionReason = "blocked by profile",
                    RiskIfNormalSelected = true,
                    RiskAfterSectionRouting = false,
                    Reasons = [RelationExpansionValidationReasons.BlockedRelationType]
                }
            ]
        });

        StringAssert.Contains(renderedProfiles, "normal-v1");
        StringAssert.Contains(renderedPreview, "Service Relation Expansion Preview");
        StringAssert.Contains(renderedPreview, "section=normal_context");
        StringAssert.Contains(renderedPreview, "riskAfterRouting=False");
        StringAssert.Contains(renderedPreview, RelationExpansionValidationReasons.BlockedRelationType);
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderGraphExpansionShadowTraceSummary()
    {
        var rendered = ServiceOperationalRenderer.RenderRelations(new ServiceRelationsSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost",
            RelationTypes = new RelationExpansionProfileRegistry().GetAll()
                .Select(profile => new RelationTypeDefinition { Type = profile.ProfileId })
                .ToArray(),
            Diagnostics = new RelationGraphDiagnosticsReport(),
            GraphShadowTraceQualitySummary = new GraphExpansionShadowTraceQualityReport
            {
                TraceCount = 1,
                AcceptedRelationCount = 1,
                BlockedRelationCount = 0,
                AuditContextCount = 1,
                ConflictEvidenceCount = 0,
                RiskAfterRoutingCount = 0,
                WrongSectionRiskCount = 0,
                Recommendation = GraphExpansionShadowTraceRecommendations.ReadyForAuditShadowOnly
            },
            RecentGraphShadowTraces =
            [
                new GraphExpansionShadowTraceRecord
                {
                    RetrievalId = "retrieval-graph-shadow-1",
                    Query = "audit trace",
                    Profiles = ["audit-v1"],
                    CreatedAt = DateTimeOffset.UtcNow,
                    AcceptedRelations =
                    [
                        new RelationExpansionPreviewRelation
                        {
                            RelationId = "rel-audit",
                            TargetSection = GraphExpansionTargetSection.AuditContext
                        }
                    ],
                    TargetSections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        [GraphExpansionTargetSection.AuditContext] = 1
                    }
                }
            ]
        });

        StringAssert.Contains(rendered, "Graph Shadow Trace Quality Summary");
        StringAssert.Contains(rendered, "Recent Graph Shadow Traces");
        StringAssert.Contains(rendered, "ReadyForAuditShadowOnly");
        StringAssert.Contains(rendered, "audit_context=1");
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
        var reviewStore = new InMemoryRelationReviewStore();
        var reviewService = new RelationReviewService(relationStore, reviewStore, registry, service);
        var profileRegistry = new RelationExpansionProfileRegistry();
        var expansionValidator = new RelationExpansionPolicyValidator(registry);
        var previewService = new RelationExpansionPreviewService(relationStore, profileRegistry, expansionValidator);
        return new RelationGraphFixture(
            relationStore,
            memoryStore,
            constraintStore,
            globalStore,
            registry,
            service,
            reviewStore,
            reviewService,
            profileRegistry,
            expansionValidator,
            previewService);
    }

    private static RelationExpansionPreviewRequest PreviewRequest(string itemId, string profileId)
    {
        return new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = itemId,
            ProfileId = profileId
        };
    }

    private static RelationReviewRequest ReviewRequest(string reason)
    {
        return new RelationReviewRequest
        {
            OperationId = $"test-relation-review-{Guid.NewGuid():N}",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Reviewer = "tester",
            Reason = reason,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "unit-test"
            }
        };
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

    private static ContextRelation WithMetadata(
        ContextRelation relation,
        IReadOnlyDictionary<string, string> metadata)
    {
        var merged = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
        {
            merged[pair.Key] = pair.Value;
        }

        return new ContextRelation
        {
            Id = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = relation.Weight,
            Confidence = relation.Confidence,
            SourceRefs = relation.SourceRefs.ToArray(),
            Metadata = merged,
            CreatedAt = relation.CreatedAt
        };
    }

    private static ContextRelation ExpansionRelation(
        string id,
        string sourceId,
        string relationType,
        string targetId,
        double confidence,
        bool withEvidence,
        string lifecycle = StableMemoryLifecycle.Active)
    {
        var evidenceRefs = withEvidence ? [$"evidence-{id}"] : Array.Empty<string>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lifecycle"] = lifecycle,
            ["reviewStatus"] = RelationReviewStatuses.Reviewed
        };
        if (withEvidence)
        {
            metadata["evidenceRefs"] = string.Join(",", evidenceRefs);
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
            SourceRefs = evidenceRefs,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed record RelationGraphFixture(
        InMemoryRelationStore RelationStore,
        InMemoryMemoryStore MemoryStore,
        InMemoryConstraintStore ConstraintStore,
        InMemoryGlobalContextStore GlobalStore,
        RelationTypeRegistry Registry,
        RelationGraphValidationService Service,
        InMemoryRelationReviewStore ReviewStore,
        RelationReviewService ReviewService,
        RelationExpansionProfileRegistry ProfileRegistry,
        RelationExpansionPolicyValidator ExpansionValidator,
        RelationExpansionPreviewService PreviewService);
}
