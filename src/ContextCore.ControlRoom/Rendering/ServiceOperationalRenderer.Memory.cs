using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderMemory(ServiceMemorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Memory");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Working : {snapshot.Working.Count}");
        builder.AppendLine($"Candidate: {snapshot.Candidates.Count}");
        builder.AppendLine($"Stable  : {snapshot.Stable.Count}");
        builder.AppendLine($"Global  : {snapshot.Global.Count}");
        builder.AppendLine();
        builder.AppendLine("Memory Layout Status");
        AppendMemoryLayoutStatus(builder, snapshot.MemoryLayoutDiagnostics);
        return builder.ToString();
    }

    private static void AppendMemoryLayoutStatus(StringBuilder builder, MemoryLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"DataRoot      : {diagnostics.DataRoot}");
        builder.AppendLine($"ShortTerm     : {diagnostics.ShortTermArtifactCount}");
        builder.AppendLine($"Candidate     : {diagnostics.CandidateArtifactCount}");
        builder.AppendLine($"Stable        : {diagnostics.StableArtifactCount}");
        builder.AppendLine($"TemporalReady : {diagnostics.TemporalPlaceholderReady}");
        builder.AppendLine($"LegacyFallback: {diagnostics.LegacyFallbackCount}");
        builder.AppendLine($"MissingDirs   : {diagnostics.MissingDirectoryCount}");
        foreach (var path in diagnostics.MemoryLayerPaths.Take(6))
        {
            builder.AppendLine($"- {path.Key}: {path.Value}");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendTraceLayoutStatus(StringBuilder builder, TraceLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"TraceRoot     : {diagnostics.TraceRoot}");
        builder.AppendLine($"Retrieval     : {diagnostics.RetrievalTraceCount}");
        builder.AppendLine($"ToolCallReady : {diagnostics.ToolCallPlaceholderReady}");
        builder.AppendLine($"LegacyFallback: {diagnostics.LegacyFallbackCount}");
        foreach (var path in diagnostics.TraceCategoryPaths.Take(6))
        {
            builder.AppendLine($"- {path.Key}: {path.Value}");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendReportLayoutStatus(StringBuilder builder, ReportLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"DataRoot       : {diagnostics.DataRoot}");
        builder.AppendLine($"ManifestCount  : {diagnostics.ManifestCount}");
        builder.AppendLine($"LatestReports  : {diagnostics.LatestReportCount}");
        builder.AppendLine($"LegacyMirrored : {diagnostics.LegacyMirroredCount}");
        builder.AppendLine($"MissingStandard: {diagnostics.MissingStandardArtifactCount}");
        builder.AppendLine($"MissingLegacy  : {diagnostics.MissingLegacyArtifactCount}");
        builder.AppendLine($"DuplicateHash  : {diagnostics.DuplicateContentHashCount}");
        foreach (var count in diagnostics.ReportCountByKind.OrderBy(item => item.Key).Take(8))
        {
            builder.AppendLine($"- {count.Key}: {count.Value}");
        }

        foreach (var sample in diagnostics.ResolvedPathSamples.Take(4))
        {
            builder.AppendLine($"sample {sample.ArtifactKind}/{sample.CapabilityId}: {sample.RelativePath}");
        }

        if (diagnostics.LargestReports.Count > 0)
        {
            builder.AppendLine("LargestReports");
            foreach (var report in diagnostics.LargestReports.Take(3))
            {
                builder.AppendLine($"- {report.RelativePath} ({report.SizeBytes} bytes)");
            }
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics    : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendStorageBoundaryStatus(StringBuilder builder, StorageBoundaryReport report)
    {
        builder.AppendLine($"ArtifactKinds : {report.TotalArtifactKinds}");
        builder.AppendLine($"ArtifactOnly  : {report.ArtifactOnlyCount}");
        builder.AppendLine($"Operational   : {report.OperationalStateCount}");
        builder.AppendLine($"IndexState    : {report.IndexStateCount}");
        builder.AppendLine($"DbRecommended : {report.DatabaseRecommendedCount}");
        builder.AppendLine($"FsPreferred   : {report.FileSystemPreferredCount}");
        builder.AppendLine($"Migrations    : {report.MigrationCandidates.Count}");
        builder.AppendLine($"HighPriority  : {report.HighPriorityMigrationCandidates.Count}");
        foreach (var candidate in report.HighPriorityMigrationCandidates.Take(6))
        {
            builder.AppendLine(
                $"- {candidate.SubjectId}: {candidate.Responsibility}/{candidate.PreferredProvider}, risk={candidate.MigrationRisk}");
        }

        if (report.RecommendedNextPhases.Count > 0)
        {
            builder.AppendLine("NextPhases");
            foreach (var phase in report.RecommendedNextPhases.Take(4))
            {
                builder.AppendLine($"- {phase}");
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", report.Diagnostics)}");
        }
    }

    private static void AppendPostgresOperationalStoreStatus(
        StringBuilder builder,
        PostgresOperationalStoreDiagnostics diagnostics)
    {
        builder.AppendLine($"Enabled       : {diagnostics.ProviderEnabled}");
        builder.AppendLine($"ProviderId    : {diagnostics.ProviderId}");
        builder.AppendLine($"Status        : {diagnostics.Status}");
        builder.AppendLine($"Connection    : {diagnostics.ConnectionAvailable}");
        builder.AppendLine($"SchemaVersion : {diagnostics.CurrentSchemaVersion ?? "未应用"}");
        builder.AppendLine($"Pending       : {diagnostics.PendingMigrations}");
        builder.AppendLine($"TableCount    : {diagnostics.TableCount}");
        builder.AppendLine($"MissingTables : {diagnostics.RequiredTableMissingCount}");
        builder.AppendLine($"Capability    : {diagnostics.ProviderCapabilityStatus}");
        if (diagnostics.SchemaVerification is not null)
        {
            var verification = diagnostics.SchemaVerification;
            builder.AppendLine("Schema Verification");
            builder.AppendLine($"- SchemaName     : {(string.IsNullOrWhiteSpace(verification.SchemaName) ? "default" : verification.SchemaName)}");
            builder.AppendLine($"- SchemaVersion  : {verification.CurrentSchemaVersion ?? "未应用"}");
            builder.AppendLine($"- RequiredTables : {verification.RequiredTableCount}");
            builder.AppendLine($"- MissingTables  : {verification.MissingRequiredTableCount}");
            builder.AppendLine($"- RequiredIndexes: {verification.RequiredIndexCount}");
            builder.AppendLine($"- MissingIndexes : {verification.MissingIndexCount}");
            builder.AppendLine($"- Recommendation : {verification.Recommendation}");
        }

        if (diagnostics.MissingRequiredTables.Count > 0)
        {
            builder.AppendLine("MissingRequiredTables");
            foreach (var table in diagnostics.MissingRequiredTables.Take(6))
            {
                builder.AppendLine($"- {table}");
            }
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }
}
