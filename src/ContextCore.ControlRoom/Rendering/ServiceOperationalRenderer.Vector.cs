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
    public static string RenderVectorIndex(ServiceVectorIndexSnapshot snapshot)
    {
        var status = snapshot.Status;
        var diagnostics = snapshot.Diagnostics;
        var preview = snapshot.ReindexPreview;
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Vector Index");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {status.WorkspaceId}");
        builder.AppendLine($"Collection : {status.CollectionId}");
        builder.AppendLine($"Provider   : {(string.IsNullOrWhiteSpace(status.Provider) ? "-" : status.Provider)}");
        builder.AppendLine($"Model      : {(string.IsNullOrWhiteSpace(status.Model) ? "-" : status.Model)}");
        builder.AppendLine($"Dimension  : {status.Dimension}");
        builder.AppendLine($"Available  : store={(status.StoreAvailable ? "yes" : "no")} generator={(status.GeneratorAvailable ? "yes" : "no")}");
        builder.AppendLine($"Counts     : indexed={status.IndexedCount} stale={status.StaleCount} missing={status.MissingCount} duplicate={status.DuplicateCount} orphan={status.OrphanCount}");
        builder.AppendLine();
        builder.AppendLine("Coverage Summary");
        builder.AppendLine($"- source items : {snapshot.Coverage.TotalSourceItems}");
        builder.AppendLine($"- indexed      : {snapshot.Coverage.IndexedItems}");
        builder.AppendLine($"- coverage     : {snapshot.Coverage.CoverageRate:P2}");
        builder.AppendLine($"- missing      : {snapshot.Coverage.MissingByLayer.Values.Sum()}");
        builder.AppendLine($"- stale        : {snapshot.Coverage.StaleByLayer.Values.Sum()}");
        builder.AppendLine($"- duplicate    : {snapshot.Coverage.DuplicateCount}");
        builder.AppendLine($"- orphan       : {snapshot.Coverage.OrphanCount}");
        builder.AppendLine($"- recommendation: {snapshot.Coverage.Recommendation}");
        builder.AppendLine();
        if (status.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", status.Warnings);
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- total          : {diagnostics.Diagnostics.Count}");
        builder.AppendLine($"- dimensionMismatch: {diagnostics.DimensionMismatchCount}");
        builder.AppendLine($"- unsupportedModel : {diagnostics.UnsupportedModelCount}");
        builder.AppendLine($"- providerUnavailable: {diagnostics.ProviderUnavailableCount}");
        if (diagnostics.CountsByType.Count > 0)
        {
            foreach (var pair in diagnostics.CountsByType.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Diagnostics");
        if (diagnostics.Diagnostics.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in diagnostics.Diagnostics.Take(20))
            {
                builder.AppendLine($"- {item.Type} [{item.Severity}] item={item.ItemId} entry={item.EntryId ?? "-"}");
                builder.AppendLine($"  message : {item.Message}");
                builder.AppendLine($"  action  : {item.SuggestedAction}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Reindex Preview");
        builder.AppendLine($"- sources : {preview.SourceItemCount}");
        builder.AppendLine($"- create  : {preview.WouldCreateCount}");
        builder.AppendLine($"- update  : {preview.WouldUpdateCount}");
        builder.AppendLine($"- current : {preview.AlreadyCurrentCount}");
        builder.AppendLine($"- orphan  : {preview.WouldDeleteOrphanCount}");
        builder.AppendLine();
        builder.AppendLine("Actions");
        builder.AppendLine("- P Reindex Plan");
        builder.AppendLine("- A Apply Reindex (requires YES)");
        builder.AppendLine("- R Reindex Reports");
        builder.AppendLine("- Q Query Preview");
        builder.AppendLine("- D Diagnostics");
        if (preview.Warnings.Count > 0)
        {
            foreach (var warning in preview.Warnings)
            {
                builder.AppendLine($"- warning : {warning}");
            }
        }

        foreach (var item in preview.Items.Take(20))
        {
            builder.AppendLine($"- {item.Action,-12} {item.ItemId} kind={item.ItemKind} layer={item.Layer}");
            builder.AppendLine($"  reason : {item.Reason}");
        }

        return builder.ToString();
    }

    public static string RenderVectorQueryPreview(VectorQueryPreviewResult result)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Vector Query Preview");
        builder.AppendLine($"Operation  : {result.OperationId}");
        builder.AppendLine($"Workspace  : {result.WorkspaceId}");
        builder.AppendLine($"Collection : {result.CollectionId}");
        builder.AppendLine($"Query      : {result.QueryText}");
        builder.AppendLine($"TopK       : {result.TopK}");
        builder.AppendLine($"Profile    : {result.ProfileId}");
        builder.AppendLine($"Layer      : {result.Layer ?? "-"}");
        builder.AppendLine($"ItemKind   : {result.ItemKind ?? "-"}");
        builder.AppendLine($"MinSim     : {result.MinSimilarity?.ToString("F3") ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- indexed={result.Diagnostics.IndexedCount} duplicate={result.Diagnostics.DuplicateCount} stale={result.Diagnostics.StaleCount} orphan={result.Diagnostics.OrphanCount}");
        builder.AppendLine($"- store={result.Diagnostics.StoreAvailable} generator={result.Diagnostics.GeneratorAvailable} indexEmpty={result.Diagnostics.IndexEmpty}");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", result.Warnings);
        }

        builder.AppendLine();
        builder.AppendLine("Candidates");
        if (result.Candidates.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return builder.ToString();
        }

        foreach (var candidate in result.Candidates.Take(30))
        {
            var flags = new List<string>();
            if (candidate.IsDuplicate) flags.Add("duplicate");
            if (candidate.IsStale) flags.Add("stale");
            if (candidate.IsOrphan) flags.Add("orphan");
            if (candidate.IsLifecycleRisk) flags.Add("lifecycle-risk");
            builder.AppendLine($"- #{candidate.Rank} raw=#{candidate.RawRank} {candidate.ItemId} sim={candidate.Similarity:F4} status={candidate.EligibilityStatus} target={candidate.TargetSection}");
            builder.AppendLine($"  kind={candidate.ItemKind} layer={candidate.Layer} riskBefore={candidate.RiskIfNormalSelected} riskAfter={candidate.RiskAfterPolicy}");
            builder.AppendLine($"  entry={candidate.EntryId} model={candidate.EmbeddingModel} provider={candidate.EmbeddingProvider}");
            if (flags.Count > 0)
            {
                builder.AppendLine($"  flags={string.Join(",", flags)}");
            }

            if (candidate.BlockedReasons.Count > 0)
            {
                builder.AppendLine($"  blocked={string.Join(",", candidate.BlockedReasons)}");
            }

            if (candidate.Diagnostics.Count > 0)
            {
                builder.AppendLine($"  diagnostics={string.Join(",", candidate.Diagnostics)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorReindexPlan(VectorReindexPlan plan)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Vector Reindex Plan");
        builder.AppendLine($"PlanId     : {plan.PlanId}");
        builder.AppendLine($"Workspace  : {plan.WorkspaceId}");
        builder.AppendLine($"Collection : {plan.CollectionId}");
        builder.AppendLine($"DryRun     : {plan.DryRun}");
        builder.AppendLine($"Candidates : total={plan.TotalCandidates} create={plan.ToCreate} update={plan.ToUpdate} skip={plan.ToSkip} orphan={plan.ToDeleteOrphan}");
        builder.AppendLine($"Signals    : stale={plan.StaleItems.Count} missing={plan.MissingItems.Count} duplicate={plan.DuplicateItems.Count} orphan={plan.OrphanItems.Count} estimatedEmbedding={plan.EstimatedEmbeddingCount}");

        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", plan.Warnings);
        }

        builder.AppendLine();
        builder.AppendLine("Plan Items");
        if (plan.Items.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in plan.Items.Take(30))
            {
                builder.AppendLine($"- {item.Action,-12} {item.ItemId} kind={item.ItemKind} layer={item.Layer}");
                builder.AppendLine($"  reason : {item.Reason}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorReindexSubmit(VectorReindexSubmitResponse response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Vector Reindex Submit");
        builder.AppendLine($"JobId      : {response.Job.JobId}");
        builder.AppendLine($"State      : {response.Job.State}");
        builder.AppendLine($"Kind       : {response.Job.Kind}");
        builder.AppendLine($"Workspace  : {response.Job.WorkspaceId}");
        builder.AppendLine($"Collection : {response.Job.CollectionId}");
        builder.AppendLine();
        builder.AppendLine($"Plan       : create={response.Plan.ToCreate} update={response.Plan.ToUpdate} skip={response.Plan.ToSkip} orphan={response.Plan.ToDeleteOrphan} duplicate={response.Plan.DuplicateItems.Count}");
        builder.AppendLine("Apply 已提交为后台 job；正式 retrieval/package 输出不会被 vector reindex 修改。");
        return builder.ToString();
    }

    public static string RenderVectorReindexReports(VectorReindexReportQueryResponse response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Vector Reindex Reports");
        builder.AppendLine($"Count: {response.Count}");
        if (response.Reports.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return builder.ToString();
        }

        foreach (var report in response.Reports.Take(20))
        {
            builder.AppendLine($"- {report.ReportId} op={report.OperationId} job={report.JobId ?? "-"} dryRun={report.Summary.DryRun} applied={report.Summary.Applied}");
            builder.AppendLine($"  summary: create={report.Summary.Created} update={report.Summary.Updated} skip={report.Summary.Skipped} failed={report.Summary.Failed} duplicate={report.Summary.Duplicate} orphan={report.Summary.Orphan}");
        }

        return builder.ToString();
    }
}
