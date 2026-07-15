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
    public static string RenderShortTermMemory(ServiceShortTermMemorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Short-Term Memory");
        builder.AppendLine($"RawEventCount    : {snapshot.Summary.RawEventCount}");
        builder.AppendLine($"WorkingItemCount : {snapshot.Summary.WorkingItemCount}");
        builder.AppendLine($"ActiveTasks      : {snapshot.Summary.ActiveTaskCount}");
        builder.AppendLine($"RecentDecisions  : {snapshot.Summary.RecentDecisionCount}");
        builder.AppendLine($"OpenQuestions    : {snapshot.Summary.OpenQuestionCount}");
        builder.AppendLine($"KnownIssues      : {snapshot.Summary.KnownIssueCount}");
        builder.AppendLine($"RecentWarnings   : {snapshot.Summary.RecentWarningCount}");
        AppendMaintenanceSection(builder, snapshot.Maintenance);
        AppendWorkingSection(builder, "ActiveTasks", snapshot.Summary.ActiveTasks);
        AppendWorkingSection(builder, "RecentDecisions", snapshot.Summary.RecentDecisions);
        AppendWorkingSection(builder, "OpenQuestions", snapshot.Summary.OpenQuestions);
        AppendWorkingSection(builder, "KnownIssues", snapshot.Summary.KnownIssues);
        AppendWorkingSection(builder, "RecentWarnings", snapshot.Summary.RecentWarnings);
        builder.AppendLine("LatestRawEvents");
        foreach (var item in snapshot.RawEvents)
        {
            builder.AppendLine($"- {item.EventId} [{item.EventKind}] seq={item.SequenceId} source={item.Source} tags={string.Join(',', item.Tags)}");
        }
        builder.AppendLine();
        builder.AppendLine(RenderShortTermArchiveSummary(snapshot.ArchiveSummary));
        builder.AppendLine();
        builder.AppendLine(RenderShortTermArchiveItems(snapshot.ArchiveItems));
        builder.AppendLine();
        builder.AppendLine(RenderShortTermCompactionRuns(snapshot.RecentRuns));
        return builder.ToString();
    }

    public static string RenderShortTermCompactionResult(ShortTermMemoryCompactionResult result)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Short-Term Compaction Result");
        builder.AppendLine($"Scope                  : {result.WorkspaceId}/{result.CollectionId} session={result.SessionId ?? "-"}");
        builder.AppendLine($"ActiveRawEvents        : {result.ActiveRawEventCountBefore} -> {result.ActiveRawEventCountAfter}");
        builder.AppendLine($"ActiveWorkingItems     : {result.ActiveWorkingItemCountBefore} -> {result.ActiveWorkingItemCountAfter}");
        builder.AppendLine($"MergedWorkingItems     : {result.MergedWorkingItems}");
        builder.AppendLine($"MergedByWorkingKey     : {result.MergedByWorkingKeyGroups}");
        builder.AppendLine($"MergedByTitle          : {result.MergedByTitleGroups}");
        builder.AppendLine($"ArchivedRawEvents      : {result.ArchivedRawEventCount}");
        builder.AppendLine($"ArchivedWorkingItems   : {result.ArchivedWorkingItemCount}");
        builder.AppendLine($"ArchivedResolvedItems  : {result.ArchivedResolvedWorkingItemCount}");
        builder.AppendLine($"EvidenceRefsTrimmed    : {result.EvidenceRefsTrimmed}");
        builder.AppendLine($"CompletedAt            : {result.CompletedAt:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString();
    }

    public static string RenderShortTermArchiveSummary(ShortTermArchiveSummary summary)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Short-Term Archive Summary");
        builder.AppendLine($"Scope                   : {summary.WorkspaceId}/{summary.CollectionId ?? "-"} session={summary.SessionId ?? "-"}");
        builder.AppendLine($"ArchivedRawEvents       : {summary.ArchivedRawEventCount}");
        builder.AppendLine($"ArchivedWorkingItems    : {summary.ArchivedWorkingItemCount}");
        builder.AppendLine($"ArchivedResolvedItems   : {summary.ArchivedResolvedWorkingItemCount}");
        builder.AppendLine($"ArchivedActiveTasks     : {summary.ArchivedActiveTaskCount}");
        builder.AppendLine($"ArchivedDecisions       : {summary.ArchivedRecentDecisionCount}");
        builder.AppendLine($"ArchivedOpenQuestions   : {summary.ArchivedOpenQuestionCount}");
        builder.AppendLine($"ArchivedKnownIssues     : {summary.ArchivedKnownIssueCount}");
        builder.AppendLine($"ArchivedRecentWarnings  : {summary.ArchivedRecentWarningCount}");
        builder.AppendLine($"LatestArchivedAt        : {(summary.LatestArchivedAt is null ? "-" : summary.LatestArchivedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"))}");
        return builder.ToString();
    }

    public static string RenderShortTermArchiveItems(ShortTermArchiveItemsResponse response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Short-Term Archive Items");
        builder.AppendLine($"ArchivedRawCount        : {response.RawEvents.Count}");
        foreach (var item in response.RawEvents)
        {
            builder.AppendLine($"- RAW {item.EventId} [{item.EventKind}] {item.Source}");
        }

        builder.AppendLine($"ArchivedWorkingCount    : {response.WorkingItems.Count}");
        foreach (var item in response.WorkingItems)
        {
            builder.AppendLine($"- WORK {item.ItemId} [{item.Kind}/{item.Status}] {item.Summary}");
        }

        return builder.ToString();
    }

    public static string RenderShortTermCompactionRuns(IReadOnlyList<ShortTermCompactionRun> runs)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Short-Term Compaction Runs");
        if (runs.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var run in runs)
        {
            builder.AppendLine($"- {run.RunId} [{run.Trigger}] {run.StartedAt:yyyy-MM-dd HH:mm:ss} dup={run.RemovedDuplicates} archiveRaw={run.ArchivedRawEvents} archiveWorking={run.ArchivedWorkingItems}");
        }

        return builder.ToString();
    }
}
