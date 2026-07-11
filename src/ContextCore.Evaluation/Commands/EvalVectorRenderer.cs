using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Commands;

/// <summary>
/// Eval-local vector rendering helpers. Duplicated from ServiceOperationalRenderer
/// to avoid Evaluation referencing ControlRoom.
/// </summary>
internal static class EvalVectorRenderer
{
    public static string RenderVectorQueryPreview(VectorQueryPreviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Query Preview");
        builder.AppendLine("====================");
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
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
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

    public static string RenderVectorReindexSubmit(VectorReindexSubmitResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Submit");
        builder.AppendLine("=====================");
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
}
