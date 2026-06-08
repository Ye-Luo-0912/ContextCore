using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>将标题、表头和行数据渲染为控制台表格的静态工具类。</summary>
public static class TableRenderer
{
    public static void Render(string title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('=', title.Length));

        if (headers.Count == 0)
        {
            return;
        }

        var widths = headers.Select(header => header.Length).ToArray();
        foreach (var row in rows)
        {
            for (var i = 0; i < headers.Count && i < row.Count; i++)
            {
                widths[i] = Math.Min(Math.Max(widths[i], row[i].Length), 48);
            }
        }

        WriteRow(headers, widths);
        Console.WriteLine(string.Join("-+-", widths.Select(width => new string('-', width))));

        if (rows.Count == 0)
        {
            Console.WriteLine("(无数据)");
            return;
        }

        foreach (var row in rows)
        {
            WriteRow(row, widths);
        }
    }

    public static void RenderStatus(ControlRoomStatus status)
    {
        Render(
            "ContextCore 控制室状态",
            ["指标", "值"],
            [
                Row("模式", status.Mode == ControlRoomMode.Service ? "Service" : "Direct"),
                Row("工作区", status.WorkspaceId),
                Row("集合", status.CollectionId),
                Row("存储", status.StorageKind),
                Row("服务地址", status.ServiceBaseUrl ?? "本地直连"),
                Row("根目录", status.RootPath),
                Row("Readiness", status.ReadinessState),
                Row("Provider 状态", status.ProviderState),
                Row("生产就绪", status.ProductionReady ? "是" : "否"),
                Row("Readiness 说明", status.ReadinessMessage),
                Row("Retrieval 基线", string.IsNullOrWhiteSpace(status.RetrievalBaseline) ? "无" : status.RetrievalBaseline),
                Row("缓存", status.RuntimeCacheTtlSeconds > 0
                    ? $"{(status.RuntimeFromCache ? "命中" : "实时")} / {status.RuntimeCacheTtlSeconds}s"
                    : "无"),
                Row("运行时告警", status.RuntimeWarningCount),
                Row("原始条目", status.RawItemCount),
                Row("工作记忆", status.WorkingMemoryCount),
                Row("候选记忆", status.CandidateMemoryCount),
                Row("稳定记忆", status.StableMemoryCount),
                Row("约束", status.ConstraintCount),
                Row("关系", status.RelationCount),
                Row("索引项", status.IndexEntryCount),
                Row("排队任务", status.QueuedJobCount),
                Row("运行任务", status.RunningJobCount),
                Row("失败任务", status.FailedJobCount),
                Row("成功任务", status.SucceededJobCount),
                Row("最近上下文包", status.LastPackage is null
                    ? "(本次会话暂无)"
                    : $"{status.LastPackage.PackageId} / {status.LastPackage.EstimatedTokens} Token")
            ]);
    }

    public static void RenderList(IReadOnlyList<ControlRoomListItem> items)
    {
        Render(
            "列表",
            ["Id", "类别", "层级", "类型", "状态", "更新时间", "预览"],
            items.Select(item => Row(
                item.Id,
                item.Kind,
                item.Layer,
                item.Type,
                item.Status,
                item.UpdatedAt == default ? "" : item.UpdatedAt.ToString("u"),
                item.Preview)).ToArray());
    }

    public static void RenderMemoryStatusBreakdown(MemoryStatusBreakdown summary)
    {
        Render(
            "记忆分层",
            ["指标", "值"],
            [
                Row("记忆总数", summary.Total),
                Row("层级：工作记忆", summary.WorkingLayer),
                Row("层级：结构化记忆", summary.StructuredLayer),
                Row("层级：稳定记忆", summary.StableLayer),
                Row("状态：候选", summary.Candidate),
                Row("状态：已验证", summary.Verified),
                Row("状态：稳定", summary.Stable),
                Row("状态：已废弃", summary.Deprecated),
                Row("状态：已拒绝", summary.Rejected)
            ]);
    }

    private static IReadOnlyList<string> Row(params object?[] values)
    {
        return values.Select(value => value?.ToString() ?? "").ToArray();
    }

    private static void WriteRow(IReadOnlyList<string> values, IReadOnlyList<int> widths)
    {
        var cells = new List<string>();
        for (var i = 0; i < widths.Count; i++)
        {
            var value = i < values.Count ? values[i] : "";
            cells.Add(Truncate(value, widths[i]).PadRight(widths[i]));
        }

        Console.WriteLine(string.Join(" | ", cells));
    }

    private static string Truncate(string value, int width)
    {
        value = value.ReplaceLineEndings(" ");
        if (value.Length <= width)
        {
            return value;
        }

        return width <= 3 ? value[..width] : value[..(width - 3)] + "...";
    }
}
