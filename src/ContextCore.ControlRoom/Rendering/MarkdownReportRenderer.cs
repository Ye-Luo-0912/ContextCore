using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>将仪表盘快照渲染为 Markdown 格式报告内容的静态工具类。</summary>
public static class MarkdownReportRenderer
{
    public static string Render(
        DashboardSnapshot dashboard,
        ControlRoomStatus status,
        IReadOnlyList<ContextMemoryItem> candidateMemory,
        IReadOnlyList<ContextMemoryItem> stableMemory,
        IReadOnlyList<ContextConstraint> constraints,
        IReadOnlyList<ContextRelation> relations,
        CollectionValidationReport validation,
        IReadOnlyList<ContextJob> failedJobs,
        IReadOnlyList<ContextIndexEntry> indexEntries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ContextCore Debug Report");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{status.WorkspaceId}`");
        builder.AppendLine($"- Collection: `{status.CollectionId}`");
        builder.AppendLine($"- Storage: `{status.StorageKind}`");
        builder.AppendLine($"- Root: `{dashboard.RootPath}`");
        builder.AppendLine($"- GeneratedAt: `{DateTimeOffset.UtcNow:u}`");
        builder.AppendLine();

        AppendDashboard(builder, dashboard);

        builder.AppendLine("## System Statistics");
        builder.AppendLine();
        AppendMetric(builder, "Raw items", status.RawItemCount);
        AppendMetric(builder, "Working memory", status.WorkingMemoryCount);
        AppendMetric(builder, "Candidate memory", status.CandidateMemoryCount);
        AppendMetric(builder, "Stable memory", status.StableMemoryCount);
        AppendMetric(builder, "Constraints", status.ConstraintCount);
        AppendMetric(builder, "Relations", status.RelationCount);
        AppendMetric(builder, "Index entries", status.IndexEntryCount);
        AppendMetric(builder, "Failed jobs", status.FailedJobCount);
        builder.AppendLine();

        builder.AppendLine("## Recent Package");
        builder.AppendLine();
        if (status.LastPackage is null)
        {
            builder.AppendLine("_No package preview was built in this ControlRoom session._");
        }
        else
        {
            builder.AppendLine($"- Id: `{status.LastPackage.PackageId}`");
            builder.AppendLine($"- Estimated tokens: `{status.LastPackage.EstimatedTokens}`");
            AppendTokenEstimateMetadata(builder, status.LastPackage.Metadata);
            foreach (var section in status.LastPackage.Sections)
            {
                builder.AppendLine($"- Section `{section.Name}`: {section.EstimatedTokens} tokens");
            }
        }
        builder.AppendLine();

        AppendJobs(builder, failedJobs);
        AppendMemory(builder, "Candidate Memory", candidateMemory);
        AppendMemory(builder, "Stable Memory", stableMemory);
        AppendConstraints(builder, constraints);
        AppendRelations(builder, relations);
        AppendValidation(builder, validation);
        AppendIndex(builder, indexEntries);

        return builder.ToString();
    }

    private static void AppendDashboard(StringBuilder builder, DashboardSnapshot dashboard)
    {
        builder.AppendLine("## Dashboard Summary");
        builder.AppendLine();
        AppendMetric(builder, "Raw items", dashboard.Memory.RawItems);
        AppendMetric(builder, "Working memory", dashboard.Memory.WorkingMemory);
        AppendMetric(builder, "Candidate memory", dashboard.Memory.CandidateMemory);
        AppendMetric(builder, "Stable memory", dashboard.Memory.StableMemory);
        AppendMetric(builder, "Global items", dashboard.Memory.GlobalItems);
        AppendMetric(builder, "Constraints", dashboard.Memory.Constraints);
        AppendMetric(builder, "Relations", dashboard.Memory.Relations);
        AppendMetric(builder, "Index entries", dashboard.Memory.IndexEntries);
        AppendMetric(builder, "Packages", dashboard.Memory.Packages);
        builder.AppendLine();

        builder.AppendLine("### Recent Operations");
        builder.AppendLine();
        if (dashboard.RecentOperations.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        else
        {
            foreach (var operation in dashboard.RecentOperations)
            {
                builder.AppendLine($"- `{operation.Time:u}` {operation.OperationName} {operation.Level} {operation.Duration?.ToString() ?? ""}: {operation.Message}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("### Jobs Summary");
        builder.AppendLine();
        AppendMetric(builder, "Queued", dashboard.Jobs.Queued);
        AppendMetric(builder, "Running", dashboard.Jobs.Running);
        AppendMetric(builder, "WaitingRetry", dashboard.Jobs.WaitingRetry);
        AppendMetric(builder, "Failed", dashboard.Jobs.Failed);
        AppendMetric(builder, "Succeeded", dashboard.Jobs.Succeeded);
        AppendMetric(builder, "RequiresReview", dashboard.Jobs.RequiresReview);
        builder.AppendLine();

        builder.AppendLine("### Recent Compression Quality");
        builder.AppendLine();
        if (dashboard.RecentCompressionQuality.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        else
        {
            foreach (var report in dashboard.RecentCompressionQuality)
            {
                builder.AppendLine(
                    $"- `{report.CreatedAt:u}` `{report.GeneratedItemId}` complete `{report.CompletenessScore:0.00}` consistency `{report.ConsistencyScore:0.00}` usability `{report.UsabilityScore:0.00}` ratio `{report.CompressionRatio:0.00}` risk `{report.RiskScore:0.00}` review `{report.RequiresReview}`");
            }
        }
        builder.AppendLine();

        builder.AppendLine("### Latest Package");
        builder.AppendLine();
        if (dashboard.LatestPackage is null)
        {
            builder.AppendLine("_None._");
        }
        else
        {
            builder.AppendLine($"- Id: `{dashboard.LatestPackage.PackageId}`");
            builder.AppendLine($"- Sections: `{dashboard.LatestPackage.SectionCount}`");
            builder.AppendLine($"- Estimated tokens: `{dashboard.LatestPackage.EstimatedTokens}`");
            AppendTokenEstimateSummary(builder, dashboard.LatestPackage);
            builder.AppendLine($"- Token budget: `{dashboard.LatestPackage.TokenBudget ?? ""}`");
            builder.AppendLine($"- CreatedAt: `{dashboard.LatestPackage.CreatedAt:u}`");
        }
        builder.AppendLine();

        builder.AppendLine("### Alerts");
        builder.AppendLine();
        if (dashboard.Alerts.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        else
        {
            foreach (var alert in dashboard.Alerts)
            {
                builder.AppendLine($"- {alert}");
            }
        }
        builder.AppendLine();
    }


    private static void AppendTokenEstimateMetadata(
        StringBuilder builder,
        IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue(ContextTokenizationMetadataKeys.Source, out var source);
        metadata.TryGetValue(ContextTokenizationMetadataKeys.Model, out var model);
        metadata.TryGetValue(ContextTokenizationMetadataKeys.IsFallback, out var isFallback);

        builder.AppendLine($"- Token 估算源: `{FormatOptional(source)}`");
        builder.AppendLine($"- Token 估算模型: `{FormatOptional(model)}`");
        builder.AppendLine($"- Token 估算是否回退: `{FormatOptional(isFallback)}`");
    }

    private static void AppendTokenEstimateSummary(StringBuilder builder, PackageSummary summary)
    {
        builder.AppendLine($"- Token 估算源: `{FormatOptional(summary.TokenEstimateSource)}`");
        builder.AppendLine($"- Token 估算模型: `{FormatOptional(summary.TokenEstimateModel)}`");
        builder.AppendLine($"- Token 估算是否回退: `{summary.TokenEstimateIsFallback}`");
    }

    private static string FormatOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "无" : value;
    }
    private static void AppendMetric(StringBuilder builder, string name, int value)
    {
        builder.AppendLine($"- {name}: `{value}`");
    }

    private static void AppendJobs(StringBuilder builder, IReadOnlyList<ContextJob> jobs)
    {
        builder.AppendLine("## Failed Jobs");
        builder.AppendLine();
        if (jobs.Count == 0)
        {
            builder.AppendLine("_No failed jobs._");
        }
        else
        {
            foreach (var job in jobs)
            {
                builder.AppendLine($"- `{job.JobId}` {job.Kind} retry {job.RetryCount}/{job.MaxRetryCount}: {job.ErrorMessage}");
            }
        }
        builder.AppendLine();
    }

    private static void AppendMemory(StringBuilder builder, string title, IReadOnlyList<ContextMemoryItem> items)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (items.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        else
        {
            foreach (var item in items)
            {
                builder.AppendLine($"- `{item.Id}` {item.Type} {item.Status} importance {item.Importance:0.00}");
            }
        }
        builder.AppendLine();
    }

    private static void AppendConstraints(StringBuilder builder, IReadOnlyList<ContextConstraint> constraints)
    {
        builder.AppendLine("## Constraints");
        builder.AppendLine();
        foreach (var item in constraints)
        {
            builder.AppendLine($"- `{item.Id}` {item.Level}/{item.Status}: {item.Content}");
        }

        if (constraints.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        builder.AppendLine();
    }

    private static void AppendRelations(StringBuilder builder, IReadOnlyList<ContextRelation> relations)
    {
        builder.AppendLine("## Relations Summary");
        builder.AppendLine();
        foreach (var group in relations.GroupBy(item => item.RelationType).OrderBy(group => group.Key))
        {
            builder.AppendLine($"- `{group.Key}`: {group.Count()}");
        }

        if (relations.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        builder.AppendLine();
    }

    private static void AppendValidation(StringBuilder builder, CollectionValidationReport validation)
    {
        builder.AppendLine("## Validation Report");
        builder.AppendLine();
        AppendMetric(builder, "Items checked", validation.ItemCount);
        AppendMetric(builder, "Relations checked", validation.RelationCount);
        builder.AppendLine($"- Status: `{(validation.Succeeded ? "passed" : "failed")}`");
        builder.AppendLine($"- Issues: `{validation.Issues.Count}`");
        builder.AppendLine();

        if (validation.Issues.Count == 0)
        {
            builder.AppendLine("_No validation issues._");
        }
        else
        {
            foreach (var issue in validation.Issues)
            {
                var path = string.IsNullOrWhiteSpace(issue.Path) ? "" : $" `{issue.Path}`";
                builder.AppendLine($"- `{issue.Severity}` `{issue.Code}`{path}: {issue.Message}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendIndex(StringBuilder builder, IReadOnlyList<ContextIndexEntry> entries)
    {
        builder.AppendLine("## Index Summary");
        builder.AppendLine();
        foreach (var group in entries.GroupBy(item => item.Kind).OrderBy(group => group.Key))
        {
            builder.AppendLine($"- `{group.Key}`: {group.Count()}");
        }

        if (entries.Count == 0)
        {
            builder.AppendLine("_None._");
        }
        builder.AppendLine();
    }
}
