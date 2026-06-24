using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;
using System.Text.Json;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
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

        var backfilled = new RelationTypeNormalizer()
            .NormalizeAndBackfillFixtureRelation(relation, "test-operation");

        Assert.AreEqual(ContextRelationTypes.Replaces, backfilled.RelationType);
        Assert.AreEqual(1.0, backfilled.Confidence);
        Assert.AreEqual(StableMemoryLifecycle.Active, backfilled.Metadata["lifecycle"]);
        Assert.AreEqual(RelationReviewStatuses.Reviewed, backfilled.Metadata["reviewStatus"]);
        Assert.AreEqual(RelationTypeNormalizer.FixtureBackfillCreatedFrom, backfilled.Metadata["createdFrom"]);
        Assert.IsTrue(backfilled.SourceRefs.Contains("fixture:relation:rel:fixture"));
    }

    [TestMethod]
    public async Task ShadowEval_ShouldConsumeNormalizedRelationType()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-legacy", "seed", "target-new", "is_superseded_by", confidence: 1.0, withEvidence: true));

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1", mustHit: ["target-new"]),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        var normal = rows.Single(row => row.ProfileId == "normal-v1");

        Assert.IsTrue(normal.AcceptedRelations.Any(relation => relation.RelationId == "rel-legacy"));
        Assert.IsFalse(normal.BlockedReasons.ContainsKey(RelationExpansionValidationReasons.UnknownRelationType));
    }

    [TestMethod]
    public async Task NormalProfile_ShouldBlockReplacesTraversalFromNewItemToOldItem()
    {
        var (_, preview) = CreateRunnerWithRelations(
            Relation("rel-new-old", "new-item", "old-item", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));

        var result = await preview.PreviewAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "new-item",
            ProfileId = "normal-v1"
        });
        var blocked = result.BlockedRelations.Single(item => item.RelationId == "rel-new-old");

        Assert.IsTrue(blocked.Reasons.Contains(RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked));
        Assert.IsTrue(blocked.Reasons.Contains(RelationExpansionValidationReasons.DeprecatedTargetBlocked));
    }

    [TestMethod]
    public async Task NormalProfile_ShouldAllowSupersededByTraversalFromOldItemToActiveReplacement()
    {
        var (_, preview) = CreateRunnerWithRelations(
            Relation("rel-old-new", "old-item", "new-item", ContextRelationTypes.SupersededBy, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Active));

        var result = await preview.PreviewAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "old-item",
            ProfileId = "normal-v1"
        });

        Assert.IsTrue(result.AcceptedRelations.Any(item =>
            item.RelationId == "rel-old-new"
            && item.TraversalDirection == RelationTraversalDirections.TowardLatest));
    }

    [TestMethod]
    public async Task CurrentTaskProfile_ShouldBlockHistoricalReplacementTarget()
    {
        var (_, preview) = CreateRunnerWithRelations(
            Relation("rel-current-old", "current-item", "old-item", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Superseded));

        var result = await preview.PreviewAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "current-item",
            ProfileId = "current-task-v1"
        });
        var blocked = result.BlockedRelations.Single(item => item.RelationId == "rel-current-old");

        Assert.IsTrue(blocked.Reasons.Contains(RelationExpansionValidationReasons.HistoricalTargetBlocked));
        Assert.IsTrue(blocked.Reasons.Contains(RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked));
    }

    [TestMethod]
    public async Task AuditProfile_ShouldAllowHistoricalTargetInAuditSection()
    {
        var (_, preview) = CreateRunnerWithRelations(
            Relation("rel-audit-old", "new-item", "old-item", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));

        var result = await preview.PreviewAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "new-item",
            ProfileId = "audit-v1"
        });
        var accepted = result.AcceptedRelations.Single(item => item.RelationId == "rel-audit-old");

        Assert.AreEqual(GraphExpansionTargetSection.AuditContext, accepted.TargetSection);
        Assert.AreEqual("audit profile routes deprecated/historical target outside normal context", accepted.SectionReason);
        Assert.IsTrue(accepted.RiskIfNormalSelected);
        Assert.IsFalse(accepted.RiskAfterSectionRouting);
        Assert.IsTrue(accepted.Warnings.Contains(RelationExpansionValidationReasons.HistoricalAllowedOnlyInAudit));
    }

    [TestMethod]
    public async Task ConflictProfile_ShouldAllowReplacementBothDirectionsWithEvidence()
    {
        var (_, preview) = CreateRunnerWithRelations(
            Relation("rel-conflict-old", "new-item", "old-item", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));

        var result = await preview.PreviewAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "new-item",
            ProfileId = "conflict-v1"
        });

        Assert.IsTrue(result.AcceptedRelations.Any(item =>
            item.RelationId == "rel-conflict-old"
            && item.TargetSection == GraphExpansionTargetSection.ConflictEvidence
            && item.RiskIfNormalSelected
            && !item.RiskAfterSectionRouting));
    }

    [TestMethod]
    public async Task AuditProfile_ShouldNotCountRoutedHistoricalTargetAsNormalRisk()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-audit-old", "seed", "target-old", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));
        var lifecycle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["target-old"] = StableMemoryLifecycle.Deprecated
        };

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1", mustNotHit: ["target-old"]),
            preview,
            "workspace-test",
            "collection-test",
            lifecycle,
            CancellationToken.None);
        var audit = runner.BuildReport(1, false, rows).Profiles.Single(profile => profile.ProfileId == "audit-v1");

        Assert.AreEqual(0, audit.MustNotHitRisk);
        Assert.AreEqual(0, audit.LifecycleRisk);
        Assert.AreEqual(1, audit.RiskIfNormalSelected);
        Assert.AreEqual(0, audit.RiskAfterSectionRouting);
        Assert.AreEqual(1, audit.AcceptedToAuditContext);
        Assert.AreEqual(RelationExpansionShadowRecommendations.ReadyForAuditShadow, audit.Recommendation);
    }

    [TestMethod]
    public async Task ConflictProfile_ShouldRouteOldConflictingTargetToConflictEvidence()
    {
        var (_, preview) = CreateRunnerWithRelations(
            Relation("rel-conflict", "current-item", "old-item", "conflicts_with", confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));

        var result = await preview.PreviewAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ItemId = "current-item",
            ProfileId = "conflict-v1"
        });
        var accepted = result.AcceptedRelations.Single(item => item.RelationId == "rel-conflict");

        Assert.AreEqual(GraphExpansionTargetSection.ConflictEvidence, accepted.TargetSection);
        Assert.AreEqual("conflict profile routes accepted relation into conflict evidence", accepted.SectionReason);
        Assert.IsTrue(accepted.RiskIfNormalSelected);
        Assert.IsFalse(accepted.RiskAfterSectionRouting);
    }

    [TestMethod]
    public async Task ShadowEval_ShouldSurfaceTraversalBlockedReasonsInReport()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-new-old", "seed", "old-item", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1"),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["old-item"] = StableMemoryLifecycle.Deprecated
            },
            CancellationToken.None);
        var normal = runner.BuildReport(1, false, rows).Profiles.Single(profile => profile.ProfileId == "normal-v1");

        Assert.IsTrue(normal.BlockedByBackwardReplacementTraversal > 0);
        Assert.IsTrue(normal.BlockedByDeprecatedTarget > 0);
        Assert.AreEqual(RelationExpansionShadowRecommendations.KeepPreviewOnly, normal.Recommendation);
    }

    [TestMethod]
    public async Task ShadowEval_ShouldNotAffectFormalRetrievalOutput()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-good", "seed", "target-good", "contains", confidence: 1.0, withEvidence: true));
        var formal = FormalResult("sample-1", ["seed"]);
        var before = formal.SelectedIds.ToArray();

        var rows = await runner.EvaluateSampleAsync(
            formal,
            Sample("sample-1", mustHit: ["target-good"]),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        var report = runner.BuildReport(totalEvalSamples: 1, includeSeedBatches: false, samples: rows);

        CollectionAssert.AreEqual(before, formal.SelectedIds.ToArray());
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.AreEqual(0, report.SelectedSetChanged);
    }

    [TestMethod]
    public async Task ShadowEval_ProfileSummary_ShouldAggregateAcceptedAndBlockedRelations()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-good", "seed", "target-good", "contains", confidence: 1.0, withEvidence: true),
            Relation("rel-low", "seed", "target-low", "references", confidence: 0.1, withEvidence: true));

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1"),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        var report = runner.BuildReport(totalEvalSamples: 1, includeSeedBatches: false, rows);
        var normal = report.Profiles.Single(profile => profile.ProfileId == "normal-v1");

        Assert.IsTrue(normal.AcceptedRelations > 0);
        Assert.IsTrue(normal.BlockedRelations > 0);
        Assert.IsTrue(normal.BlockedByConfidence > 0);
    }

    [TestMethod]
    public async Task ShadowEval_ShouldReportMustNotHitRisk()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-bad", "seed", "target-bad", "contains", confidence: 1.0, withEvidence: true));

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1", mustNotHit: ["target-bad"]),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        var normal = runner.BuildReport(1, false, rows).Profiles.Single(profile => profile.ProfileId == "normal-v1");

        Assert.AreEqual(1, normal.MustNotHitRisk);
        Assert.AreEqual(RelationExpansionShadowRecommendations.BlockedByRisk, normal.Recommendation);
    }

    [TestMethod]
    public async Task ShadowEval_ShouldReportBlockedLifecycleTarget()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-old", "seed", "target-old", "contains", confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated));
        var lifecycle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["target-old"] = "Deprecated"
        };

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1"),
            preview,
            "workspace-test",
            "collection-test",
            lifecycle,
            CancellationToken.None);
        var normal = runner.BuildReport(1, false, rows).Profiles.Single(profile => profile.ProfileId == "normal-v1");

        Assert.AreEqual(0, normal.LifecycleRisk);
        Assert.AreEqual(1, normal.BlockedByDeprecatedTarget);
        Assert.AreEqual(RelationExpansionShadowRecommendations.KeepPreviewOnly, normal.Recommendation);
    }

    [TestMethod]
    public async Task ShadowEval_ShouldReportWrongSectionRisk()
    {
        var relation = Relation("rel-wrong-section", "seed", "target-old", ContextRelationTypes.Replaces, confidence: 1.0, withEvidence: true, targetLifecycle: StableMemoryLifecycle.Deprecated);
        relation.Metadata["previewTargetSectionOverride"] = GraphExpansionTargetSection.NormalContext;
        var (runner, preview) = CreateRunnerWithRelations(relation);

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1"),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target-old"] = StableMemoryLifecycle.Deprecated
            },
            CancellationToken.None);
        var audit = runner.BuildReport(1, false, rows).Profiles.Single(profile => profile.ProfileId == "audit-v1");

        Assert.AreEqual(1, audit.WrongSectionRisk);
        Assert.AreEqual(RelationExpansionShadowRecommendations.BlockedByWrongSectionRisk, audit.Recommendation);
    }

    [TestMethod]
    public async Task ShadowEval_ShouldPreserveBlockedReasons()
    {
        var (runner, preview) = CreateRunnerWithRelations(
            Relation("rel-no-evidence", "seed", "target-no-evidence", "references", confidence: 1.0, withEvidence: false));

        var rows = await runner.EvaluateSampleAsync(
            FormalResult("sample-1", ["seed"]),
            Sample("sample-1"),
            preview,
            "workspace-test",
            "collection-test",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        var normal = rows.Single(row => row.ProfileId == "normal-v1");

        Assert.IsTrue(normal.BlockedReasons.ContainsKey(RelationExpansionValidationReasons.MissingEvidence));
        Assert.IsTrue(normal.BlockedRelations.Any(relation =>
            relation.Reasons.Contains(RelationExpansionValidationReasons.MissingEvidence)));
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

    private static (RelationExpansionShadowEvalRunner Runner, RelationExpansionPreviewService Preview) CreateRunnerWithRelations(
        params ContextRelation[] relations)
    {
        var relationStore = new InMemoryRelationStore();
        foreach (var relation in relations)
        {
            relationStore.SaveAsync(relation).GetAwaiter().GetResult();
        }

        var profileRegistry = new RelationExpansionProfileRegistry();
        var typeRegistry = new RelationTypeRegistry();
        var validator = new RelationExpansionPolicyValidator(typeRegistry);
        return (
            new RelationExpansionShadowEvalRunner(profileRegistry, typeRegistry, new PlanningIntentDetector()),
            new RelationExpansionPreviewService(relationStore, profileRegistry, validator));
    }

    private static ContextEvalResult FormalResult(string sampleId, IReadOnlyList<string> selected)
    {
        return new ContextEvalResult
        {
            SampleId = sampleId,
            Query = "current task",
            Mode = "ChatMode",
            Succeeded = true,
            SelectedIds = selected
        };
    }

    private static ContextEvalSample Sample(
        string sampleId,
        IReadOnlyList<string>? mustHit = null,
        IReadOnlyList<string>? mustNotHit = null)
    {
        return new ContextEvalSample
        {
            Id = sampleId,
            Query = "current task",
            Mode = "ChatMode",
            MustHit = mustHit ?? Array.Empty<string>(),
            MustNotHit = mustNotHit ?? Array.Empty<string>()
        };
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
        var preview = new RelationExpansionPreviewService(relationStore, profileRegistry, validator);
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
