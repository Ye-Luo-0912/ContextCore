using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 将各类上下文条目（约束、记忆、全局项、原始上下文、近期项、丢弃项、不确定性、证据）
/// 格式化为 section 内容字符串。所有方法均为纯函数，不持有状态。
/// +B 后 section 格式化统一走 Format*Segments 方法返回 CandidateSegment。
/// </summary>
internal static class PackageSectionFormatter
{
    internal static IReadOnlyList<CandidateSegment> FormatConstraintSegments(
        IReadOnlyList<ContextConstraint> constraints, int tokenBudget = 0)
    {
        var compact = tokenBudget > 0 && tokenBudget <= 200;
        var segments = new List<CandidateSegment>(constraints.Count);
        foreach (var item in constraints)
        {
            // 携带候选级 SourceRefs/ItemRefs，供 Section refs 按接受状态聚合
            var sourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id };
            segments.Add(new CandidateSegment(
                item.Id,
                compact ? item.Content : $"- [{item.Level}] {item.Content}",
                sourceRefs,
                new[] { item.Id }));
        }
        return segments;
    }

    internal static string FormatCurrentTask(
        WorkingMemoryCurrentTask currentTask,
        ContextPackageRequest request)
    {
        if (request.TokenBudget > 0 && request.TokenBudget <= 200)
        {
            return currentTask.Title;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## 当前任务");
        builder.AppendLine($"- 任务 ID：{currentTask.TaskId}");
        builder.AppendLine($"- 标题：{currentTask.Title}");
        builder.AppendLine($"- 状态：{currentTask.Status}");
        if (currentTask.Tags.Count > 0)
        {
            builder.AppendLine($"- 标签：{string.Join(", ", currentTask.Tags)}");
        }

        if (!string.IsNullOrWhiteSpace(request.QueryText))
        {
            builder.AppendLine($"- 当前输入：{request.QueryText}");
        }

        if (!string.IsNullOrWhiteSpace(currentTask.Description))
        {
            builder.AppendLine();
            builder.AppendLine(currentTask.Description);
        }

        var metadataLines = currentTask.Metadata
            .Where(item => IsCurrentTaskMetadataKey(item.Key))
            .Take(12)
            .Select(item => $"- {item.Key}: {item.Value}")
            .ToArray();
        if (metadataLines.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 任务元数据");
            foreach (var line in metadataLines)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    internal static IReadOnlyList<CandidateSegment> FormatMergedConstraintSegments(
        IReadOnlyList<MergedContextConstraint> constraints, int tokenBudget = 0)
    {
        var compact = tokenBudget > 0 && tokenBudget <= 200;
        var segments = new List<CandidateSegment>(constraints.Count);
        foreach (var item in constraints)
        {
            // 携带候选级 SourceRefs/ItemRefs
            var sourceRefs = item.Constraint.SourceRefs.Count > 0
                ? item.Constraint.SourceRefs.ToArray()
                : new[] { item.Constraint.Id };
            segments.Add(new CandidateSegment(
                item.Constraint.Id,
                compact ? item.Constraint.Content : $"- [{item.PriorityLabel} | {item.Constraint.Level}] {item.Constraint.Content}",
                sourceRefs,
                new[] { item.Constraint.Id }));
        }
        return segments;
    }

    internal static IReadOnlyList<CandidateSegment> FormatMemorySegments(
        IReadOnlyList<ContextMemoryItem> items, int tokenBudget = 0)
    {
        var compact = tokenBudget > 0 && tokenBudget <= 200;
        var segments = new List<CandidateSegment>(items.Count);
        foreach (var item in items)
        {
            // 携带候选级 SourceRefs/ItemRefs
            var sourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id };
            segments.Add(new CandidateSegment(
                item.Id,
                compact ? item.Content : $"## {item.Type} / {item.Layer} / {item.Status}{Environment.NewLine}{item.Content}",
                sourceRefs,
                new[] { item.Id }));
        }
        return segments;
    }

    internal static IReadOnlyList<CandidateSegment> FormatGlobalSegments(IReadOnlyList<ContextGlobalItem> items)
    {
        var segments = new List<CandidateSegment>(items.Count);
        foreach (var item in items)
        {
            // 携带候选级 SourceRefs/ItemRefs
            var sourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id };
            segments.Add(new CandidateSegment(
                item.Id,
                $"## {item.Type} / {item.Scope}{Environment.NewLine}{item.Content}",
                sourceRefs,
                new[] { item.Id }));
        }
        return segments;
    }

    internal static IReadOnlyList<CandidateSegment> FormatContextItemSegments(IReadOnlyList<ContextItem> items)
    {
        var segments = new List<CandidateSegment>(items.Count);
        foreach (var item in items)
        {
            // 携带候选级 SourceRefs/ItemRefs
            segments.Add(new CandidateSegment(
                item.Id,
                $"## {(string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title)} / {item.Type}{Environment.NewLine}{item.Content}",
                ContextItemRefResolver.ResolveSourceRefs(item),
                ContextItemRefResolver.ResolveItemRefs(item)));
        }
        return segments;
    }

    internal static IReadOnlyList<CandidateSegment> FormatRecentContextSegments(
        IReadOnlyList<RecentContextItem> items, int tokenBudget = 0)
    {
        var compact = tokenBudget > 0 && tokenBudget <= 200;
        var segments = new List<CandidateSegment>(items.Count);
        foreach (var item in items)
        {
            // 携带候选级 SourceRefs/ItemRefs
            var sourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.SourceItemId };
            segments.Add(new CandidateSegment(
                item.SourceItemId,
                compact ? item.Content : $"## {item.SourceItemId} / relevance {item.Relevance:0.00} / recency {item.RecencyWeight:0.00}{Environment.NewLine}{item.Content}",
                sourceRefs,
                new[] { item.SourceItemId }));
        }
        return segments;
    }

    internal static string FormatDroppedItems(IReadOnlyList<DroppedContextItem> items)
    {
        return JoinBlocks(items.Take(50).Select(item =>
            $"- [{item.Kind}/{item.Type}] {item.ItemId}: {item.Reason}；score={item.Score:0.00}；tokens={item.EstimatedTokens}"));
    }

    internal static string FormatUncertainties(IReadOnlyList<ContextPackageUncertainty> items)
    {
        return JoinBlocks(items.Select(item =>
        {
            var refs = item.ItemRefs.Count == 0
                ? string.Empty
                : $"；refs={string.Join(',', item.ItemRefs.Take(12))}";
            return $"- [{item.Severity}] {item.Code}: {item.Message}{refs}";
        }));
    }

    internal static IReadOnlyList<ContextEvidenceEntry> BuildEvidenceEntries(
        IReadOnlyList<ContextPackageSection> sections,
        IReadOnlyList<ContextPackageDecision> selectedItems)
    {
        var sectionLookup = sections.ToDictionary(
            section => section.Name,
            section => section,
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<ContextEvidenceEntry>();

        foreach (var item in selectedItems.Take(80))
        {
            var sectionSourceRefs = sectionLookup.TryGetValue(item.SectionName, out var section)
                ? section.SourceRefs
                : Array.Empty<string>();
            entries.Add(new ContextEvidenceEntry(
                item.ItemId,
                item.SectionName,
                item.Kind,
                item.Type,
                item.SourceRefs.Concat(sectionSourceRefs)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray(),
                item.Reason));
        }

        return entries;
    }

    internal static string FormatEvidenceEntries(IReadOnlyList<ContextEvidenceEntry> entries)
    {
        return JoinBlocks(entries.Select(item =>
        {
            var refs = item.SourceRefs.Count == 0
                ? "无显式来源"
                : string.Join(", ", item.SourceRefs);
            return $"- [{item.SectionName}] {item.ItemId} ({item.Kind}/{item.Type})；来源：{refs}；原因：{item.Reason}";
        }));
    }

    internal static string JoinBlocks(IEnumerable<string> blocks)
    {
        var builder = new StringBuilder();

        foreach (var block in blocks.Where(block => !string.IsNullOrWhiteSpace(block)))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(block);
        }

        return builder.ToString();
    }

    private static bool IsCurrentTaskMetadataKey(string key)
    {
        return key.Contains("mode", StringComparison.OrdinalIgnoreCase)
            || key.Contains("task", StringComparison.OrdinalIgnoreCase)
            || key.Contains("intent", StringComparison.OrdinalIgnoreCase)
            || key.Contains("project", StringComparison.OrdinalIgnoreCase)
            || key.Contains("format", StringComparison.OrdinalIgnoreCase);
    }
}
