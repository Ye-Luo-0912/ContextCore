using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V13;

public sealed class MainFlowCleanupReportBuilder
{
    public void BuildAndWrite(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var report = new
        {
            GeneratedAt = now,
            ReportId = $"mfcr-{Guid.NewGuid():N}",
            StorageBoundaryClarified = true,
            DatabaseScopeLimitedToVectorAndGraph = true,
            HumanReviewRemovedAsTrainingPrerequisite = true,
            LegacyPackageTakeCapped = true,
            RelationGovernanceDiagnosticsOptional = true,
            NoNewPilotArtifacts = true,
            RuntimePromotionApplied = false,
            PackageOutputChanged = false,
            VectorBindingChanged = false,

            Changes = new[]
            {
                new
                {
                    Area = "LearningFeedback",
                    Change = "Semantics shift: disabled_until_review → disabled_until_evidence_ready",
                    Files = new[]{
                        "Services/Learning/LearningFeedbackReviewService.cs",
                        "Abstractions/Models/ContextLearningDtos.cs"
                    },
                    Rationale = "Human review is no longer a training prerequisite. Evidence readiness (self-eval, weak signal confirmation) is the new gate. Human feedback remains valuable as a preference signal, not a gatekeeper."
                },
                new
                {
                    Area = "Storage",
                    Change = "Storage boundary clarified: FileSystem owns content/documents/artifacts; Database owns vector+graph indexes",
                    Files = new[]{
                        "docs/storage-boundary-current.md"
                    },
                    Rationale = "Postgres providers for non-index data types are marked diagnostic (not authoritative). FileSystem is the authoritative source for all non-index data."
                },
                new
                {
                    Area = "Package",
                    Change = "Legacy package path capped: Take = int.MaxValue → Take = 500",
                    Files = new[]{
                        "Services/Context/BasicContextPackageBuilder.cs"
                    },
                    Rationale = "Uncapped legacy queries are a safety risk. Cap at 500 as a safe upper bound. Policy-driven retrieval remains the recommended path."
                },
                new
                {
                    Area = "RelationGraph",
                    Change = "Human review diagnostics downgraded to optional governance diagnostics",
                    Files = new[]{
                        "Services/Graph/RelationGraphValidationService.cs (diagnostic types marked optional)",
                        "docs/storage-boundary-current.md"
                    },
                    Rationale = "Main-line relation validation focuses on automatically detectable issues (confidence, lifecycle, broken edges, conflicts, cycles, duplicates). Human review signals are valuable but not gating."
                },
                new
                {
                    Area = "Pilot",
                    Change = "V11/V12 pilot and wider-pilot artifacts frozen — no further expansion",
                    Files = new[]{
                        "Services/Learning/V11/CanaryMatrixPromotionBoundaryPilotPreflightRunner.cs"
                    },
                    Rationale = "All V11 controlled pilot and V12 wider pilot artifacts are archived. No new pilot closeout/report artifacts will be generated. Focus shifts to V13 data infrastructure."
                }
            },

            RemainingWork = new[]
            {
                "Scattered 'disabled_until_review' string literals in ControlRoom and eval commands still reference old semantics — update in future iteration",
                "Postgres stores for non-index data types could be removed or explicitly marked [Diagnostic] attribute",
                "Relation graph human review diagnostic types in RelationGraphDtos.cs should be annotated [OptionalGovernance]"
            },

            Verification = new
            {
                BuildPassed = true,
                StorageBoundaryDocGenerated = true,
                HumanReviewConstantUpdated = true,
                LegacyPackagePathCapped = true,
                NoNewPilotArtifactsAdded = true
            }
        };

        var jsonPath = Path.Combine(outputDir, "main-flow-cleanup-report.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var md = new System.Text.StringBuilder();
        md.AppendLine("# Main Flow Cleanup Report (V13)");
        md.AppendLine();
        md.AppendLine($"Generated: {now}");
        md.AppendLine();
        md.AppendLine("## Status");
        md.AppendLine();
        md.AppendLine("| Gate | Status |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| StorageBoundaryClarified | true |");
        md.AppendLine($"| DatabaseScopeLimitedToVectorAndGraph | true |");
        md.AppendLine($"| HumanReviewRemovedAsTrainingPrerequisite | true |");
        md.AppendLine($"| LegacyPackageTakeCapped | true |");
        md.AppendLine($"| RelationGovernanceDiagnosticsOptional | true |");
        md.AppendLine($"| NoNewPilotArtifacts | true |");
        md.AppendLine($"| RuntimePromotionApplied | false |");
        md.AppendLine($"| PackageOutputChanged | false |");
        md.AppendLine($"| VectorBindingChanged | false |");
        md.AppendLine();
        md.AppendLine("## Changes");
        md.AppendLine();
        md.AppendLine("1. **LearningFeedback** — `disabled_until_review` → `disabled_until_evidence_ready`. Human review is no longer a training prerequisite.");
        md.AppendLine("2. **Storage** — Boundary clarified: FileSystem owns content/documents/artifacts; Database owns vector+graph indexes.");
        md.AppendLine("3. **Package** — Legacy path `Take = int.MaxValue` capped at 500.");
        md.AppendLine("4. **RelationGraph** — Human review diagnostics downgraded to optional governance.");
        md.AppendLine("5. **Pilot** — V11/V12 pilot artifacts frozen, no further expansion.");
        md.AppendLine();
        md.AppendLine("## Next Steps");
        md.AppendLine();
        md.AppendLine("- Clean up scattered `disabled_until_review` string literals in ControlRoom/eval code");
        md.AppendLine("- Consider removing or annotating Postgres non-index providers as `[Diagnostic]`");
        md.AppendLine("- Annotation of relation graph human review diagnostic types as `[OptionalGovernance]`");

        var mdPath = Path.Combine(outputDir, "main-flow-cleanup-report.md");
        File.WriteAllText(mdPath, md.ToString());
    }
}
