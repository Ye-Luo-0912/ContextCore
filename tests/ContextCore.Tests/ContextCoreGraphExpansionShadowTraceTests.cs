using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreGraphExpansionShadowTraceTests
{
    [TestMethod]
    public void GraphExpansionShadowTraceQualityReportBuilder_EmptyTrace_ShouldRecommendNeedsMoreRealTraces()
    {
        var report = new GraphExpansionShadowTraceQualityReportBuilder().Build(
            Array.Empty<GraphExpansionShadowTraceRecord>(),
            "workspace-1",
            "collection-1");

        Assert.AreEqual(0, report.TraceCount);
        Assert.AreEqual(0, report.AcceptedRelationCount);
        Assert.AreEqual(GraphExpansionShadowTraceRecommendations.NeedsMoreRealTraces, report.Recommendation);
    }

    [TestMethod]
    public void GraphExpansionShadowTraceQualityReportBuilder_ShouldCountWrongSectionRisk()
    {
        var report = new GraphExpansionShadowTraceQualityReportBuilder().Build(
            [
                new GraphExpansionShadowTraceRecord
                {
                    RetrievalId = "retrieval-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Profiles = ["audit-v1"],
                    CreatedAt = DateTimeOffset.UtcNow,
                    AcceptedRelations =
                    [
                        PreviewRelation(
                            "rel-wrong-section",
                            ContextRelationTypes.Replaces,
                            GraphExpansionTargetSection.NormalContext,
                            riskIfNormal: true,
                            riskAfterRouting: true)
                    ],
                    RiskIfNormal = 1,
                    RiskAfterRouting = 1,
                    WrongSectionRisk = 1
                }
            ],
            "workspace-1",
            "collection-1");

        Assert.AreEqual(1, report.TraceCount);
        Assert.AreEqual(1, report.RiskAfterRoutingCount);
        Assert.AreEqual(1, report.WrongSectionRiskCount);
        Assert.AreEqual(GraphExpansionShadowTraceRecommendations.NeedsMoreRealTraces, report.Recommendation);
    }

    [TestMethod]
    public void GraphExpansionShadowTraceQualityReportBuilder_DiverseNonRiskFixture_ShouldRecommendReadyForGuardedOptIn()
    {
        var scenarios = new[]
        {
            "chat-version-conflict",
            "chat-deprecated-preference",
            "chat-audit-old-topic",
            "chat-overwritten-style-rule",
            "chat-scope-boundary-old-session",
            "chat-long-term-preference-conflict",
            "project-deprecated-design",
            "project-superseded-pool",
            "project-old-storage-choice",
            "project-migration-conflict",
            "project-retired-policy",
            "project-audit-previous-release-plan",
            "novel-old-plot",
            "novel-weapon-conflict",
            "novel-world-rule-conflict",
            "novel-character-state-retcon",
            "novel-location-rule-superseded",
            "novel-foreshadowing-conflict",
            "automation-old-backup",
            "automation-conflict-recovery",
            "automation-dead-letter-policy-conflict",
            "automation-retry-limit-superseded",
            "automation-old-credential-rotation",
            "automation-audit-failed-step-history",
            "coding-deprecated-interface",
            "coding-old-timeout",
            "coding-obsolete-api-contract",
            "coding-test-policy-conflict",
            "coding-build-script-legacy-path",
            "coding-deprecated-schema-field"
        };
        var modes = new[] { "ChatMode", "ProjectMode", "NovelMode", "AutomationMode", "CodingMode" };
        var auditTypes = new[] { ContextRelationTypes.Replaces, ContextRelationTypes.SupersededBy, "references", ContextRelationTypes.EvidenceFor };
        var conflictTypes = new[] { ContextRelationTypes.Contradicts, "conflicts_with", "blocks", "supports" };
        var records = new List<GraphExpansionShadowTraceRecord>();

        for (var index = 0; index < scenarios.Length; index++)
        {
            var scenarioId = scenarios[index];
            records.Add(new GraphExpansionShadowTraceRecord
            {
                RetrievalId = $"retrieval-{scenarioId}",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                Query = $"sample query for {scenarioId}",
                Profiles = ["audit-v1", "conflict-v1"],
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index),
                AcceptedRelations =
                [
                    PreviewRelation(
                        $"rel-audit-{scenarioId}",
                        auditTypes[index % auditTypes.Length],
                        GraphExpansionTargetSection.AuditContext,
                        riskIfNormal: true,
                        riskAfterRouting: false,
                        targetLifecycle: index % 2 == 0 ? StableMemoryLifecycle.Deprecated : StableMemoryLifecycle.Superseded),
                    PreviewRelation(
                        $"rel-conflict-{scenarioId}",
                        conflictTypes[index % conflictTypes.Length],
                        GraphExpansionTargetSection.ConflictEvidence,
                        riskIfNormal: true,
                        riskAfterRouting: false,
                        targetLifecycle: StableMemoryLifecycle.Active),
                    PreviewRelation(
                        $"rel-historical-{scenarioId}",
                        "references",
                        GraphExpansionTargetSection.HistoricalContext,
                        riskIfNormal: true,
                        riskAfterRouting: false,
                        targetLifecycle: "Historical")
                ],
                BlockedRelations =
                [
                    PreviewRelation(
                        $"rel-blocked-low-confidence-{scenarioId}",
                        "same_as",
                        GraphExpansionTargetSection.DiagnosticsOnly,
                        riskIfNormal: false,
                        riskAfterRouting: false,
                        targetLifecycle: StableMemoryLifecycle.Active,
                        reasons: [RelationExpansionValidationReasons.ConfidenceTooLow])
                ],
                TargetSections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [GraphExpansionTargetSection.AuditContext] = 1,
                    [GraphExpansionTargetSection.ConflictEvidence] = 1,
                    [GraphExpansionTargetSection.HistoricalContext] = 1,
                    [GraphExpansionTargetSection.DiagnosticsOnly] = 1
                },
                RiskIfNormal = 3,
                RiskAfterRouting = 0,
                HistoricalAuditCount = 2,
                ConflictEvidenceCount = 1,
                WrongSectionRisk = 0,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = modes[index % modes.Length],
                    ["sampleScenario"] = scenarioId
                }
            });
        }

        var report = new GraphExpansionShadowTraceQualityReportBuilder().Build(
            records,
            "workspace-1",
            "collection-1");

        Assert.AreEqual(30, report.TraceCount);
        Assert.AreEqual(90, report.AcceptedRelationCount);
        Assert.AreEqual(30, report.BlockedRelationCount);
        Assert.AreEqual(60, report.AuditContextCount);
        Assert.AreEqual(30, report.ConflictEvidenceCount);
        Assert.AreEqual(0, report.RiskAfterRoutingCount);
        Assert.AreEqual(0, report.WrongSectionRiskCount);
        Assert.AreEqual(0, report.MustNotHitRiskCount);
        Assert.AreEqual(0, report.LifecycleRiskCount);
        Assert.AreEqual(0, report.MissingEvidenceCount);
        Assert.IsTrue(report.TopRelationTypes.Count >= 6);
        Assert.AreEqual(30, report.TopBlockedReasons[RelationExpansionValidationReasons.ConfidenceTooLow]);
        Assert.AreEqual(GraphExpansionShadowTraceRecommendations.ReadyForGuardedOptIn, report.Recommendation);
    }

    [TestMethod]
    public async Task GraphExpansionShadowTraceExportService_ShouldReturnJsonLinesCompatibleRecords()
    {
        var store = new InMemoryRetrievalTraceStore();
        await store.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "retrieval-graph-shadow-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            QueryText = "audit conflict",
            CreatedAt = DateTimeOffset.UtcNow,
            GraphExpansionShadowTrace = new GraphExpansionShadowTrace
            {
                GraphExpansionShadowEnabled = true,
                GraphExpansionProfiles = ["audit-v1", "conflict-v1"],
                AcceptedRelations =
                [
                    PreviewRelation(
                        "rel-audit-old",
                        ContextRelationTypes.Replaces,
                        GraphExpansionTargetSection.AuditContext,
                        riskIfNormal: true,
                        riskAfterRouting: false)
                ],
                TargetSections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [GraphExpansionTargetSection.AuditContext] = 1
                },
                RiskIfNormal = 1,
                RiskAfterRouting = 0,
                HistoricalAuditCount = 1
            }
        });
        var export = new GraphExpansionShadowTraceExportService(store);

        var records = await export.QueryAsync("workspace-1", "collection-1", take: 10);
        var jsonl = await export.ExportJsonLinesAsync("workspace-1", "collection-1", take: 10);

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("retrieval-graph-shadow-1", records[0].RetrievalId);
        StringAssert.Contains(jsonl, "\"retrievalId\":\"retrieval-graph-shadow-1\"");
        StringAssert.Contains(jsonl, "\"relationId\":\"rel-audit-old\"");
        Assert.IsFalse(jsonl.Contains(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GraphExpansionShadowTraceExportService_ShouldDeduplicateRepeatedShadowSignature()
    {
        var store = new InMemoryRetrievalTraceStore();
        for (var index = 0; index < 2; index++)
        {
            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = $"retrieval-graph-shadow-duplicate-{index}",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                QueryText = "same audit conflict query",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index),
                GraphExpansionShadowTrace = new GraphExpansionShadowTrace
                {
                    GraphExpansionShadowEnabled = true,
                    GraphExpansionProfiles = ["audit-v1", "conflict-v1"],
                    AcceptedRelations =
                    [
                        PreviewRelation(
                            $"rel-audit-duplicate-{index}",
                            ContextRelationTypes.Replaces,
                            GraphExpansionTargetSection.AuditContext,
                            riskIfNormal: true,
                            riskAfterRouting: false)
                    ],
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["traceSignature"] = "same-shadow-signature"
                    }
                }
            });
        }

        var export = new GraphExpansionShadowTraceExportService(store);

        var records = await export.QueryAsync("workspace-1", "collection-1", take: 10);
        var jsonl = await export.ExportJsonLinesAsync("workspace-1", "collection-1", take: 10);

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("retrieval-graph-shadow-duplicate-1", records[0].RetrievalId);
        Assert.AreEqual(1, jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    public async Task EvalCommand_GraphExpansionShadowTraceQuality_ShouldWriteReportFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-graph-shadow-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var state = ControlRoomService.CreateState(
                "memory",
                tempRoot,
                "workspace-1",
                "collection-1");
            await state.RetrievalTraceStore.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "retrieval-quality-graph-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                QueryText = "audit conflict",
                CreatedAt = DateTimeOffset.UtcNow,
                GraphExpansionShadowTrace = new GraphExpansionShadowTrace
                {
                    GraphExpansionShadowEnabled = true,
                    GraphExpansionProfiles = ["audit-v1"],
                    AcceptedRelations =
                    [
                        PreviewRelation(
                            "rel-quality-audit",
                            ContextRelationTypes.Replaces,
                            GraphExpansionTargetSection.AuditContext,
                            riskIfNormal: true,
                            riskAfterRouting: false)
                    ],
                    TargetSections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        [GraphExpansionTargetSection.AuditContext] = 1
                    },
                    HistoricalAuditCount = 1,
                    RiskIfNormal = 1
                }
            });
            var service = new ControlRoomService(state);
            var jsonPath = Path.Combine(tempRoot, "quality.json");
            var markdownPath = Path.Combine(tempRoot, "quality.md");

            await EvalCommand.ExecuteAsync(
                service,
                [
                    "graph-expansion-shadow-trace-quality",
                    "--workspace", "workspace-1",
                    "--collection", "collection-1",
                    "--take", "10",
                    "--out", jsonPath,
                    "--md-out", markdownPath
                ]);

            Assert.IsTrue(File.Exists(jsonPath));
            Assert.IsTrue(File.Exists(markdownPath));
            StringAssert.Contains(await File.ReadAllTextAsync(jsonPath), "\"TraceCount\": 1");
            StringAssert.Contains(await File.ReadAllTextAsync(markdownPath), "Graph Expansion Shadow Trace Quality Report");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static RelationExpansionPreviewRelation PreviewRelation(
        string relationId,
        string relationType,
        string targetSection,
        bool riskIfNormal,
        bool riskAfterRouting,
        string targetLifecycle = StableMemoryLifecycle.Deprecated,
        IReadOnlyList<string>? reasons = null)
    {
        return new RelationExpansionPreviewRelation
        {
            RelationId = relationId,
            SourceId = "item-current",
            TargetId = "item-old",
            RelationType = relationType,
            TargetSection = targetSection,
            SectionReason = "test section routing",
            TargetLifecycle = targetLifecycle,
            Confidence = 1.0,
            RiskIfNormalSelected = riskIfNormal,
            RiskAfterSectionRouting = riskAfterRouting,
            Reasons = reasons ?? Array.Empty<string>(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["evidenceRefs"] = "evidence-fixture",
                ["reviewStatus"] = RelationReviewStatuses.Reviewed
            }
        };
    }
}
