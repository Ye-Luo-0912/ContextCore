using System.Text;
using ContextCore.Abstractions;
using ContextCore.Client;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>渲染 Service 模式下的 ingest / query / package 操作结果。</summary>
public static class ServiceOperationRenderer
{
    public static string RenderIngestResult(ContextInputIngestionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Ingest");
        builder.AppendLine("==============");
        builder.AppendLine($"OperationId : {result.OperationId}");
        builder.AppendLine($"ItemId      : {result.Item.Id}");
        builder.AppendLine($"Created     : {(result.Created ? "yes" : "no")}");
        builder.AppendLine($"Deduped     : {(result.Deduped ? "yes" : "no")}");
        builder.AppendLine($"ContentHash : {result.ContentHash}");
        builder.AppendLine($"SequenceId  : {result.SequenceId}");
        builder.AppendLine($"Type        : {result.Item.Type}");
        builder.AppendLine($"Workspace   : {result.Item.WorkspaceId}");
        builder.AppendLine($"Collection  : {result.Item.CollectionId}");
        return builder.ToString();
    }

    public static string RenderQueryResult(ContextQueryResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Query");
        builder.AppendLine("=============");
        builder.AppendLine($"Count: {response.Count}");

        foreach (var item in response.Items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Type}] {Preview(item.Content, 80)}");
        }

        if (response.Items.Count > 10)
        {
            builder.AppendLine($"... 其余 {response.Items.Count - 10} 条未展开");
        }

        return builder.ToString();
    }

    public static string RenderPackageResult(ContextPackageBuildResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Package");
        builder.AppendLine("===============");
        builder.AppendLine($"BuildId        : {result.BuildId}");
        builder.AppendLine($"PackageId      : {result.Package.PackageId}");
        builder.AppendLine($"EstimatedTokens: {result.EstimatedTokens}");
        builder.AppendLine($"TokenBudget    : {result.TokenBudget}");
        builder.AppendLine($"Sections       : {result.Package.Sections.Count}");
        builder.AppendLine($"SelectedItems  : {result.SelectedItems.Count}");
        builder.AppendLine($"DroppedItems   : {result.DroppedItems.Count}");
        builder.AppendLine($"Warnings       : {result.Uncertainties.Count}");
        builder.AppendLine($"TraceSummary   : selected={result.SelectedItems.Count}, dropped={result.DroppedItems.Count}, uncertainties={result.Uncertainties.Count}");
        builder.AppendLine();
        builder.AppendLine("Sections");
        builder.AppendLine("--------");
        foreach (var section in result.Package.Sections)
        {
            builder.AppendLine($"- {section.Name} ({section.EstimatedTokens} tokens) {Preview(section.Content, 90)}");
        }

        if (result.Uncertainties.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            builder.AppendLine("--------");
            foreach (var warning in result.Uncertainties.Take(10))
            {
                builder.AppendLine($"- [{warning.Severity}] {warning.Code}: {warning.Message}");
            }
        }

        return builder.ToString();
    }

    public static string RenderError(ContextCoreApiException exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Error");
        builder.AppendLine("=============");
        builder.AppendLine($"StatusCode : {(int)exception.StatusCode}");
        builder.AppendLine($"ErrorCode  : {exception.ErrorResponse.ErrorCode}");
        builder.AppendLine($"Target     : {exception.ErrorResponse.Target}");
        builder.AppendLine($"Message    : {exception.ErrorResponse.Message}");

        foreach (var detail in exception.ErrorResponse.Details)
        {
            builder.AppendLine($"- [{detail.Code}] {detail.Field ?? detail.Target ?? "n/a"}: {detail.Message}");
        }

        return builder.ToString();
    }

    private static string Preview(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ");
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }
}
