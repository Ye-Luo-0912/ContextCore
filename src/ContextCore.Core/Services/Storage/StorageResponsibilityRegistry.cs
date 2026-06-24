using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Storage;

public static class StorageResponsibilityRegistry
{
    private static readonly IReadOnlyList<StorageResponsibilityEntry> Entries = BuildEntries();

    public static IReadOnlyList<StorageResponsibilityEntry> GetEntries() => Entries;

    public static StorageBoundaryReport BuildReport() => BuildReport(Entries);

    public static StorageBoundaryReport BuildReport(IReadOnlyList<StorageResponsibilityEntry> entries)
    {
        var diagnostics = ValidateCoverage(entries);
        var migrationCandidates = entries
            .Where(IsMigrationCandidate)
            .OrderByDescending(MigrationPriorityRank)
            .ThenBy(item => item.SubjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var highPriority = migrationCandidates
            .Where(item => string.Equals(item.MigrationPriority, "High", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var blockedReasons = entries
            .SelectMany(item => item.BlockedReasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StorageBoundaryReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalArtifactKinds = Enum.GetValues<ArtifactKind>().Length,
            ArtifactOnlyCount = entries.Count(item => item.Responsibility == StorageResponsibilityKind.ArtifactOnly),
            OperationalStateCount = entries.Count(item => item.Responsibility == StorageResponsibilityKind.OperationalState),
            IndexStateCount = entries.Count(item => item.Responsibility == StorageResponsibilityKind.IndexState),
            DatabaseRecommendedCount = entries.Count(item => item.PreferredProvider == StorageResponsibilityKind.DatabaseRecommended),
            FileSystemPreferredCount = entries.Count(item => item.PreferredProvider == StorageResponsibilityKind.FileSystemPreferred),
            MigrationCandidates = migrationCandidates,
            HighPriorityMigrationCandidates = highPriority,
            BlockedReasons = blockedReasons,
            RecommendedNextPhases =
            [
                "DB1: operational state provider abstraction audit",
                "DB2: relation/constraint database store design",
                "DB3: pgvector-backed vector index store design",
                "DB4: job/feedback operational store migration plan"
            ],
            Diagnostics = diagnostics,
            Entries =
            [
                .. entries
                    .OrderBy(item => item.SubjectType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.SubjectId, StringComparer.OrdinalIgnoreCase)
            ]
        };
    }

    public static string BuildMarkdownReport(StorageBoundaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Storage Boundary Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- TotalArtifactKinds: `{report.TotalArtifactKinds}`");
        builder.AppendLine($"- ArtifactOnly: `{report.ArtifactOnlyCount}`");
        builder.AppendLine($"- OperationalState: `{report.OperationalStateCount}`");
        builder.AppendLine($"- IndexState: `{report.IndexStateCount}`");
        builder.AppendLine($"- DatabaseRecommended: `{report.DatabaseRecommendedCount}`");
        builder.AppendLine($"- FileSystemPreferred: `{report.FileSystemPreferredCount}`");
        builder.AppendLine($"- MigrationCandidates: `{report.MigrationCandidates.Count}`");
        builder.AppendLine($"- HighPriorityMigrationCandidates: `{report.HighPriorityMigrationCandidates.Count}`");
        builder.AppendLine();

        builder.AppendLine("## Migration Candidates");
        builder.AppendLine();
        builder.AppendLine("| Subject | Responsibility | Current | Preferred | Priority | Risk | Notes |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var entry in report.MigrationCandidates.Take(24))
        {
            builder.AppendLine(
                $"| {Escape(entry.SubjectId)} | {entry.Responsibility} | {Escape(entry.CurrentProvider)} | {entry.PreferredProvider} | {Escape(entry.MigrationPriority)} | {Escape(entry.MigrationRisk)} | {Escape(entry.Notes)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Recommended Next Phases");
        builder.AppendLine();
        foreach (var phase in report.RecommendedNextPhases)
        {
            builder.AppendLine($"- {phase}");
        }

        if (report.Diagnostics.Count <= 0) return builder.ToString();
        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        builder.AppendLine();
        foreach (var diagnostic in report.Diagnostics)
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> ValidateCoverage(IReadOnlyList<StorageResponsibilityEntry> entries)
    {
        var covered = entries
            .Where(item => item.ArtifactKind.HasValue)
            .Select(item => item.ArtifactKind!.Value)
            .ToHashSet();

        return
        [
            .. from kind in Enum.GetValues<ArtifactKind>()
            where !covered.Contains(kind)
            select $"UnknownArtifactKindClassification:{kind}"
        ];
    }

    private static bool IsMigrationCandidate(StorageResponsibilityEntry entry)
    {
        return entry.Tags.Contains(StorageResponsibilityKind.MigrationCandidate)
            || entry.PreferredProvider == StorageResponsibilityKind.DatabaseRecommended
            || entry.Responsibility is StorageResponsibilityKind.OperationalState or StorageResponsibilityKind.IndexState;
    }

    private static int MigrationPriorityRank(StorageResponsibilityEntry entry)
    {
        return entry.MigrationPriority.ToUpperInvariant() switch
        {
            "HIGH" => 3,
            "MEDIUM" => 2,
            "LOW" => 1,
            _ => 0
        };
    }

    private static IReadOnlyList<StorageResponsibilityEntry> BuildEntries()
    {
        var entries = new List<StorageResponsibilityEntry>(96);
        AddArtifact(entries, ArtifactKind.MemoryShort, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "Medium", "Medium", "short-term memory operational state");
        AddArtifact(entries, ArtifactKind.MemoryCandidate, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "candidate memory reviewable operational state");
        AddArtifact(entries, ArtifactKind.MemoryStable, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "stable memory lifecycle state");
        AddArtifact(entries, ArtifactKind.MemoryShortTermRawEvent, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "Medium", "Medium", "raw memory event stream");
        AddArtifact(entries, ArtifactKind.MemoryShortTermWorkingItem, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "Medium", "Medium", "working memory state");
        AddArtifact(entries, ArtifactKind.MemoryShortTermArchive, StorageResponsibilityKind.SnapshotOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "archive snapshot artifact");
        AddArtifact(entries, ArtifactKind.MemoryShortTermCompactionRun, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "compaction run artifact");
        AddArtifact(entries, ArtifactKind.MemoryTemporalItem, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "Medium", "Medium", "temporal memory placeholder state");
        AddArtifact(entries, ArtifactKind.MemoryTemporalArchive, StorageResponsibilityKind.SnapshotOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "temporal archive artifact");
        AddArtifact(entries, ArtifactKind.MemoryTemporalDiagnostics, StorageResponsibilityKind.SnapshotOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "temporal diagnostics artifact");
        AddArtifact(entries, ArtifactKind.MemoryCandidateItem, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "candidate memory item state");
        AddArtifact(entries, ArtifactKind.MemoryCandidateReview, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "candidate memory review history");
        AddArtifact(entries, ArtifactKind.MemoryCandidateDiagnostics, StorageResponsibilityKind.SnapshotOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "candidate diagnostics artifact");
        AddArtifact(entries, ArtifactKind.MemoryCandidateEvidence, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "Medium", "Medium", "candidate evidence provenance");
        AddArtifact(entries, ArtifactKind.MemoryStableItem, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "stable memory item state");
        AddArtifact(entries, ArtifactKind.MemoryStableLifecycleReview, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "stable lifecycle review history");
        AddArtifact(entries, ArtifactKind.MemoryStableReplacementChain, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "stable replacement chain state");
        AddArtifact(entries, ArtifactKind.MemoryStableProvenance, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "stable provenance chain");
        AddArtifact(entries, ArtifactKind.MemoryStableDiagnostics, StorageResponsibilityKind.SnapshotOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "stable diagnostics artifact");
        AddArtifact(entries, ArtifactKind.Relation, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "relation graph operational state");
        AddArtifact(entries, ArtifactKind.Constraint, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "constraint lifecycle state");
        AddArtifact(entries, ArtifactKind.Vector, StorageResponsibilityKind.IndexState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "vector index state, pgvector candidate");
        AddArtifact(entries, ArtifactKind.VectorLifecycleMetadataReviewCandidate, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "vector lifecycle metadata review queue; no runtime effect");
        AddArtifact(entries, ArtifactKind.VectorLifecycleMetadataReviewCandidateReport, StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "vector lifecycle metadata review candidate report");
        AddArtifact(entries, ArtifactKind.LearningFeedback, StorageResponsibilityKind.ExportOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "reviewed feedback export artifact");
        AddArtifact(entries, ArtifactKind.Router, StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "router eval/report artifact");
        AddArtifact(entries, ArtifactKind.Ranker, StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "ranker eval/report artifact");
        AddArtifact(entries, ArtifactKind.Graph, StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "graph eval/report artifact");
        AddArtifact(entries, ArtifactKind.Eval, StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "eval report artifact");
        AddArtifact(entries, ArtifactKind.Trace, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "generic trace artifact");
        AddArtifact(entries, ArtifactKind.TraceRetrieval, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "retrieval trace artifact");
        AddArtifact(entries, ArtifactKind.TracePlanning, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "planning trace artifact");
        AddArtifact(entries, ArtifactKind.TraceToolCall, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "tool-call trace artifact");
        AddArtifact(entries, ArtifactKind.TraceRouterShadow, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "router shadow trace artifact");
        AddArtifact(entries, ArtifactKind.TraceRankerShadow, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "ranker shadow trace artifact");
        AddArtifact(entries, ArtifactKind.TraceVectorShadow, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "vector shadow trace artifact");
        AddArtifact(entries, ArtifactKind.TraceGraphShadow, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "graph shadow trace artifact");
        AddArtifact(entries, ArtifactKind.TraceRelationDualWrite, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "relation governance dual-write trace artifact");
        AddArtifact(entries, ArtifactKind.TraceRelationShadowRead, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "relation governance shadow-read comparison trace artifact");
        AddArtifact(entries, ArtifactKind.TraceRelationProviderSwitch, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "relation governance provider switch trace artifact");
        AddArtifact(entries, ArtifactKind.TraceLearningFeedbackDualWrite, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "learning feedback dual-write trace artifact");
        AddArtifact(entries, ArtifactKind.TraceLearningFeedbackShadowRead, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "learning feedback shadow-read trace artifact");
        AddArtifact(entries, ArtifactKind.TraceLearningFeedbackProviderSwitch, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "learning feedback provider switch trace artifact");
        AddArtifact(entries, ArtifactKind.TraceJobQueueDualWrite, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "job queue dual-write smoke trace artifact");
        AddArtifact(entries, ArtifactKind.TraceJobQueueShadowRead, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "job queue shadow-read smoke trace artifact");
        AddArtifact(entries, ArtifactKind.TraceJobQueueScopedWorkerCanary, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "job queue scoped worker canary trace artifact");
        AddArtifact(entries, ArtifactKind.TraceJobQueueLimitedWorkerScopeObservation, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "job queue limited worker scope observation trace artifact");
        AddArtifact(entries, ArtifactKind.TracePackageBuild, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "package build trace artifact");
        AddArtifact(entries, ArtifactKind.TraceModelCall, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "model-call trace artifact");
        AddArtifact(entries, ArtifactKind.TraceError, StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "error trace artifact");
        AddArtifact(entries, ArtifactKind.Job, StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "job record operational state");
        AddArtifact(entries, ArtifactKind.Report, StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "report artifact");

        AddStore(entries, "EvalReport", StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "eval reports remain artifact plane");
        AddStore(entries, "LearningReport", StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "learning reports remain artifact plane");
        AddStore(entries, "ReadinessGate", StorageResponsibilityKind.ArtifactOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "gate snapshots remain artifact plane");
        AddStore(entries, "TraceRetrieval", StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "retrieval traces remain trace plane");
        AddStore(entries, "ToolCallTrace", StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "tool call traces remain trace plane");
        AddStore(entries, "ShadowTrace", StorageResponsibilityKind.TraceOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "shadow traces remain trace plane");
        AddStore(entries, "FeedbackExport", StorageResponsibilityKind.ExportOnly, StorageResponsibilityKind.FileSystemPreferred, "Low", "Low", "feedback jsonl export remains artifact plane");
        AddStore(entries, "ContextItem", StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "context item state should move to operational store");
        AddStore(entries, "MemoryItem", StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "memory item state should move to operational store");
        AddStore(entries, "RelationItem", StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "relation graph should move to database-backed operational store");
        AddStore(entries, "ConstraintState", StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "High", "constraint lifecycle state should move to database");
        AddStore(entries, "VectorIndexEntry", StorageResponsibilityKind.IndexState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "pgvector candidate; filesystem remains preview/export only");
        AddStore(entries, "JobRecord", StorageResponsibilityKind.OperationalState, StorageResponsibilityKind.DatabaseRecommended, "High", "Medium", "job queue state should move to operational store");
        return entries;
    }

    private static void AddArtifact(
        ICollection<StorageResponsibilityEntry> entries,
        ArtifactKind artifactKind,
        StorageResponsibilityKind responsibility,
        StorageResponsibilityKind preferredProvider,
        string priority,
        string risk,
        string notes)
    {
        entries.Add(CreateEntry(
            artifactKind.ToString(),
            "ArtifactKind",
            responsibility,
            preferredProvider,
            priority,
            risk,
            notes) with
            {
                ArtifactKind = artifactKind
            });
    }

    private static void AddStore(
        ICollection<StorageResponsibilityEntry> entries,
        string storeKind,
        StorageResponsibilityKind responsibility,
        StorageResponsibilityKind preferredProvider,
        string priority,
        string risk,
        string notes)
    {
        entries.Add(CreateEntry(
            storeKind,
            "StoreKind",
            responsibility,
            preferredProvider,
            priority,
            risk,
            notes) with
            {
                StoreKind = storeKind
            });
    }

    private static StorageResponsibilityEntry CreateEntry(
        string subjectId,
        string subjectType,
        StorageResponsibilityKind responsibility,
        StorageResponsibilityKind preferredProvider,
        string priority,
        string risk,
        string notes)
    {
        var tags = preferredProvider == StorageResponsibilityKind.DatabaseRecommended
            ? new[] { StorageResponsibilityKind.MigrationCandidate }
            : Array.Empty<StorageResponsibilityKind>();
        var blockedReasons = preferredProvider == StorageResponsibilityKind.DatabaseRecommended
            ? new[] { "RequiresProviderDesign", "RequiresNonDestructiveMigrationPlan" }
            : Array.Empty<string>();

        return new StorageResponsibilityEntry
        {
            SubjectId = subjectId,
            SubjectType = subjectType,
            Responsibility = responsibility,
            PreferredProvider = preferredProvider,
            CurrentProvider = "FileSystem/InMemory",
            MigrationPriority = priority,
            MigrationRisk = risk,
            Tags = tags,
            BlockedReasons = blockedReasons,
            Notes = notes
        };
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
