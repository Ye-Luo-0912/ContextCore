using System.Text;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>将 <see cref="ContextCore.ControlRoom.Services.DashboardSnapshot"/> 渲染为控制台仪表盘输出的静态工具类。</summary>
public static class DashboardRenderer
{
    private const int CompactThreshold = 110;

    public static void Render(DashboardSnapshot snapshot, bool autoRefresh, int refreshSeconds)
    {
        Console.WriteLine(RenderToString(snapshot, autoRefresh, refreshSeconds));
    }

    public static string RenderToString(DashboardSnapshot snapshot, bool autoRefresh, int refreshSeconds)
    {
        return RenderToString(snapshot, autoRefresh, refreshSeconds, GetConsoleWidth());
    }

    public static string RenderToString(
        DashboardSnapshot snapshot,
        bool autoRefresh,
        int refreshSeconds,
        int width)
    {
        width = Math.Clamp(width, 72, 180);
        if (width < CompactThreshold)
        {
            // 窄终端切换到紧凑布局，避免横向面板把文字挤到不可读。
            return CompactDashboardRenderer.RenderToString(snapshot, autoRefresh, refreshSeconds, width);
        }

        var rootDisplay = PathDisplayHelper.Compact(snapshot.RootPath, Math.Min(48, Math.Max(32, width - 72)));
        var headerLines = new[]
        {
            $"标题: ContextCore 控制室   时间: {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}",
            $"刷新: {(autoRefresh ? $"自动 {refreshSeconds}s" : "手动")}   模式: {(snapshot.Mode == ControlRoomMode.Service ? "Service" : "Direct")}   存储: {snapshot.StorageKind}/{NormalizeStatus(FindHealth(snapshot, "storage")?.Status)}",
            $"工作区: {snapshot.WorkspaceId}   集合: {snapshot.CollectionId}",
            $"根目录: {rootDisplay}",
            snapshot.Mode == ControlRoomMode.Service
                ? $"服务地址: {CompactValue(snapshot.ServiceBaseUrl, Math.Min(48, Math.Max(32, width - 72)))}"
                : "服务地址: 本地直连模式"
        };

        var health = PanelBox.Render(
            "健康状态",
            HealthRows(snapshot),
            ColumnWidth(width, 3));
        var memory = PanelBox.Render(
            "记忆概览",
            MemoryRows(snapshot),
            ColumnWidth(width, 3));
        var jobs = PanelBox.Render(
            "任务概览",
            JobsRows(snapshot),
            ColumnWidth(width, 3));

        var bottomWidth = ColumnWidth(width, 3);
        var recent = PanelBox.Render(
            "最近操作",
            RecentOperationRows(snapshot, 5),
            bottomWidth);
        var package = PanelBox.Render(
            "最新上下文包",
            LatestPackageRows(snapshot),
            bottomWidth);
        var quality = PanelBox.Render(
            "压缩质量",
            CompressionQualityRows(snapshot, 3),
            width - (bottomWidth * 2) - 2);

        var builder = new StringBuilder();
        builder.AppendLines(PanelBox.Render("ContextCore 控制室", headerLines, width));
        builder.AppendLine();
        builder.AppendLines(PanelBox.JoinHorizontal([health, memory, jobs], 1));
        builder.AppendLine();
        builder.AppendLines(PanelBox.Render("告警", AlertRows(snapshot), width));
        builder.AppendLine();
        builder.AppendLines(PanelBox.JoinHorizontal([recent, package, quality], 1));
        builder.AppendLine();
        builder.AppendLines(PanelBox.Render("命令", CommandRows(compact: false, serviceMode: snapshot.Mode == ControlRoomMode.Service), width));

        return builder.ToString();
    }

    internal static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "EMPTY";
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "ok" => "正常",
            "trace" => "正常",
            "info" => "正常",
            "information" => "正常",
            "missing" => "缺失",
            "empty" => "空",
            "unavailable" => "离线",
            "offline" => "离线",
            "attention" => "警告",
            "warn" => "警告",
            "warning" => "警告",
            "error" => "错误",
            _ => status.Trim().ToUpperInvariant()
        };
    }

    internal static IReadOnlyList<string> HealthRows(DashboardSnapshot snapshot)
    {
        return
        [
            HealthLine(snapshot, "storage", "存储"),
            HealthLine(snapshot, "operation logs", "日志"),
            HealthLine(snapshot, "index", "索引"),
            HealthLine(snapshot, "job queue", "任务"),
            HealthLine(snapshot, "model gateway", "模型")
        ];
    }

    internal static IReadOnlyList<string> MemoryRows(DashboardSnapshot snapshot)
    {
        return
        [
            Metric("原始条目", snapshot.Memory.RawItems),
            Metric("工作记忆", snapshot.Memory.WorkingMemory),
            Metric("候选记忆", snapshot.Memory.CandidateMemory),
            Metric("稳定记忆", snapshot.Memory.StableMemory),
            Metric("全局上下文", snapshot.Memory.GlobalItems),
            Metric("约束", snapshot.Memory.Constraints),
            Metric("关系", snapshot.Memory.Relations),
            Metric("上下文包", snapshot.Memory.Packages)
        ];
    }

    internal static IReadOnlyList<string> JobsRows(DashboardSnapshot snapshot)
    {
        return
        [
            Metric("排队", snapshot.Jobs.Queued),
            Metric("运行中", snapshot.Jobs.Running),
            Metric("等待重试", snapshot.Jobs.WaitingRetry),
            Metric("失败", snapshot.Jobs.Failed),
            Metric("成功", snapshot.Jobs.Succeeded),
            Metric("需复核", snapshot.Jobs.RequiresReview)
        ];
    }

    internal static IReadOnlyList<string> AlertRows(DashboardSnapshot snapshot)
    {
        if (snapshot.Alerts.Count == 0)
        {
            return ["正常  当前没有活动告警"];
        }

        var rows = snapshot.Alerts
            .Take(3)
            .Select(alert => $"警告 {alert}")
            .ToList();
        if (snapshot.Alerts.Count > 3)
        {
            rows.Add($"+{snapshot.Alerts.Count - 3} 条更多");
        }

        rows.Insert(0, $"总数: {snapshot.Alerts.Count}");
        return rows;
    }

    internal static IReadOnlyList<string> RecentOperationRows(DashboardSnapshot snapshot, int take)
    {
        if (snapshot.RecentOperations.Count == 0)
        {
            return ["无"];
        }

        return snapshot.RecentOperations
            .Take(take)
            .Select(operation =>
                $"{operation.Time:HH:mm:ss}  {PathDisplayHelper.CompactId(operation.OperationName, 22),-22} {NormalizeStatus(operation.Level),-8} {FormatDuration(operation.Duration),8}")
            .ToArray();
    }

    internal static IReadOnlyList<string> LatestPackageRows(DashboardSnapshot snapshot)
    {
        if (snapshot.LatestPackage is null)
        {
            return ["无"];
        }

        return
        [
            $"ID       {PathDisplayHelper.CompactId(snapshot.LatestPackage.PackageId, 24)}",
            $"片段数   {snapshot.LatestPackage.SectionCount}",
            $"Token    {snapshot.LatestPackage.EstimatedTokens}",
            $"估算源   {CompactValue(snapshot.LatestPackage.TokenEstimateSource, 24)}",
            $"估算模型 {CompactValue(snapshot.LatestPackage.TokenEstimateModel, 24)}",
            $"是否回退 {(snapshot.LatestPackage.TokenEstimateIsFallback ? "是" : "否")}",
            $"创建时间 {snapshot.LatestPackage.CreatedAt:yyyy-MM-dd HH:mm:ss}"
        ];
    }

    internal static IReadOnlyList<string> CompressionQualityRows(DashboardSnapshot snapshot, int take)
    {
        if (snapshot.RecentCompressionQuality.Count == 0)
        {
            return ["无"];
        }

        return snapshot.RecentCompressionQuality
            .Take(take)
            .Select(report =>
                $"{report.CreatedAt:HH:mm:ss} {PathDisplayHelper.CompactId(report.GeneratedItemId, 14),-14} 完整{report.CompletenessScore:0.00} 可用{report.UsabilityScore:0.00} 风险{report.RiskScore:0.00} {(report.RequiresReview ? "需复核" : "正常")}")
            .ToArray();
    }

    internal static IReadOnlyList<string> CommandRows(bool compact, bool serviceMode = false)
    {
        if (serviceMode)
        {
            return compact
                ? 
                [
                    "[R] 刷新  [A] 自动  [S] 服务  [I] Ingest  [G] Query  [V] Package  [J] Jobs  [M] Model  [U] Admin  [Y] Memory  [K] Constraints  [C] Gaps  [E] Candidates  [L] Relations  [O] Policy  [T] ShortTerm  [N] Promotion  [Z] StableReview  [H] Learning  [32] PolicyFeedback  [33] LearningFeatures  [X] Planning  [F] Proposal  [34] RankerDebug  [35] CandidateMemory  [36] StableMemory  [Q] 退出"
                ]
                :
                [
                    "[R] 刷新  [A] 自动  [S] 服务  [I] Ingest  [G] Query  [V] Package  [J] Jobs  [M] Model  [U] Admin  [Y] Memory  [K] Constraints  [C] Gaps  [E] Candidates  [L] Relations  [O] Policy  [T] ShortTerm  [N] Promotion  [Z] StableReview  [H] Learning  [32] PolicyFeedback  [33] LearningFeatures  [X] Planning  [F] Proposal  [34] RankerDebug  [35] CandidateMemory  [36] StableMemory  [Q] 退出",
                    "Service 模式为最小观测模式；本地文件浏览、记忆层、包预览等 direct-only 功能已禁用"
                ];
        }

        return compact
            ?
            [
                "[R] 刷新  [A] 自动  [1] 浏览  [2] 记忆  [3] 上下文包  [D] 检索  [P] 策略  [Q] 退出"
            ]
            :
            [
                "[R] 刷新  [A] 自动  [W] 工作区  [C] 集合  [1] 浏览  [2] 记忆  [3] 上下文包  [D] 检索  [P] 策略  [Q] 退出",
                "[4] 任务  [5] 关系  [6] 约束  [7] 索引  [8] 检索  [9] 模型  [10] 报告  [11] 策略  [12] 评测报告"
            ];
    }


    internal static string CompactValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "无";
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return maxLength <= 3 ? value[..maxLength] : value[..(maxLength - 3)] + "...";
    }
    private static string HealthLine(DashboardSnapshot snapshot, string key, string label)
    {
        return $"{label,-8} {NormalizeStatus(FindHealth(snapshot, key)?.Status),-8}";
    }

    private static SystemHealthItem? FindHealth(DashboardSnapshot snapshot, string key)
    {
        return snapshot.Health.FirstOrDefault(item =>
            string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string Metric(string name, int value)
    {
        return $"{name,-14} {value,6}";
    }

    private static int ColumnWidth(int totalWidth, int columns)
    {
        return (totalWidth - (columns - 1)) / columns;
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        return duration is null ? "" : $"{duration.Value.TotalMilliseconds:0}ms";
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 120 : Console.WindowWidth;
        }
        catch (IOException)
        {
            return 120;
        }
    }
}

/// <summary>窄终端下使用的控制室仪表盘渲染器。</summary>
public static class CompactDashboardRenderer
{
    public static string RenderToString(
        DashboardSnapshot snapshot,
        bool autoRefresh,
        int refreshSeconds,
        int width)
    {
        width = Math.Clamp(width, 64, 108);
        var rootDisplay = PathDisplayHelper.Compact(snapshot.RootPath, Math.Min(42, Math.Max(24, width - 22)));
        var builder = new StringBuilder();

        builder.AppendLines(PanelBox.Render(
            "ContextCore 控制室",
            [
                $"时间    {snapshot.CurrentTime:HH:mm:ss}   刷新 {(autoRefresh ? $"自动 {refreshSeconds}s" : "手动")}",
                $"模式    {(snapshot.Mode == ControlRoomMode.Service ? "Service" : "Direct")}   存储 {snapshot.StorageKind}/{DashboardRenderer.NormalizeStatus(snapshot.Health.FirstOrDefault()?.Status)}",
                $"范围    {snapshot.WorkspaceId}/{snapshot.CollectionId}",
                $"根目录  {rootDisplay}",
                snapshot.Mode == ControlRoomMode.Service
                    ? $"服务    {DashboardRenderer.CompactValue(snapshot.ServiceBaseUrl, Math.Min(32, Math.Max(18, width - 18)))}"
                    : "服务    本地直连模式"
            ],
            width));
        builder.AppendLine();
        builder.AppendLines(PanelBox.Render(
            "摘要",
            [
                $"健康: {string.Join("  ", DashboardRenderer.HealthRows(snapshot).Select(CompactStatus))}",
                $"记忆: 原始 {snapshot.Memory.RawItems} | 工作 {snapshot.Memory.WorkingMemory} | 稳定 {snapshot.Memory.StableMemory} | 关系 {snapshot.Memory.Relations}",
                $"任务: 排队 {snapshot.Jobs.Queued} | 运行 {snapshot.Jobs.Running} | 失败 {snapshot.Jobs.Failed} | 复核 {snapshot.Jobs.RequiresReview}",
                "质量: " + (snapshot.RecentCompressionQuality.Count == 0
                    ? "无"
                    : $"完整 {snapshot.RecentCompressionQuality[0].CompletenessScore:0.00} | 可用 {snapshot.RecentCompressionQuality[0].UsabilityScore:0.00} | 风险 {snapshot.RecentCompressionQuality[0].RiskScore:0.00} | {(snapshot.RecentCompressionQuality[0].RequiresReview ? "需复核" : "正常")}"),
                $"告警: {snapshot.Alerts.Count}" + (snapshot.Alerts.Count > 0 ? $"  {string.Join(" | ", snapshot.Alerts.Take(2))}" : "")
            ],
            width));
        builder.AppendLine();
        builder.AppendLines(PanelBox.Render(
            "最近操作 / 上下文包",
            [
                "操作: " + (snapshot.RecentOperations.Count == 0
                    ? "无"
                    : string.Join(" | ", DashboardRenderer.RecentOperationRows(snapshot, 2))),
                "包: " + FormatLatestPackage(snapshot.LatestPackage)
            ],
            width));
        builder.AppendLine();
        builder.AppendLines(PanelBox.Render("命令", DashboardRenderer.CommandRows(compact: true, serviceMode: snapshot.Mode == ControlRoomMode.Service), width));

        return builder.ToString();
    }


    private static string FormatLatestPackage(PackageSummary? package)
    {
        if (package is null)
        {
            return "无";
        }

        var source = DashboardRenderer.CompactValue(package.TokenEstimateSource, 16);
        var fallback = package.TokenEstimateIsFallback ? " / 回退" : string.Empty;
        return $"{PathDisplayHelper.CompactId(package.PackageId, 18)} / {package.EstimatedTokens} Token / {source}{fallback}";
    }
    private static string CompactStatus(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : value;
    }
}

/// <summary>控制台面板绘制工具，负责边框、换行和横向拼接。</summary>
internal static class PanelBox
{
    public static IReadOnlyList<string> Render(string title, IReadOnlyList<string> lines, int width)
    {
        width = Math.Max(24, width);
        var innerWidth = width - 4;
        var output = new List<string>
        {
            Top(title, width)
        };

        foreach (var line in lines.DefaultIfEmpty(""))
        {
            foreach (var wrapped in Wrap(line, innerWidth))
            {
                output.Add($"║ {PadCell(wrapped, innerWidth)} ║");
            }
        }

        output.Add(Bottom(width));
        return output;
    }

    public static IReadOnlyList<string> JoinHorizontal(IReadOnlyList<IReadOnlyList<string>> panels, int gap)
    {
        var height = panels.Max(panel => panel.Count);
        var widths = panels.Select(panel => panel.Max(line => line.Length)).ToArray();
        var rows = new List<string>();

        // 每个面板先按自身最大宽度补齐，再逐行拼接，确保不同高度面板底部能对齐。
        for (var i = 0; i < height; i++)
        {
            var cells = new List<string>();
            for (var panelIndex = 0; panelIndex < panels.Count; panelIndex++)
            {
                var panel = panels[panelIndex];
                var value = i < panel.Count ? panel[i] : new string(' ', widths[panelIndex]);
                cells.Add(value.PadRight(widths[panelIndex]));
            }

            rows.Add(string.Join(new string(' ', gap), cells));
        }

        return rows;
    }

    private static string Top(string title, int width)
    {
        var label = string.IsNullOrWhiteSpace(title) ? "" : $" {title.Trim()} ";
        var left = "╔";
        var right = "╗";
        var available = width - left.Length - right.Length;
        if (label.Length > available)
        {
            label = label[..available];
        }

        return left + label + new string('═', available - label.Length) + right;
    }

    private static string Bottom(int width)
    {
        return "╚" + new string('═', width - 2) + "╝";
    }

    private static IEnumerable<string> Wrap(string value, int width)
    {
        value = value.ReplaceLineEndings(" ").TrimEnd();
        if (string.IsNullOrEmpty(value))
        {
            yield return string.Empty;
            yield break;
        }

        while (value.Length > width)
        {
            yield return value[..width];
            value = value[width..];
        }

        yield return value;
    }

    private static string PadCell(string value, int width)
    {
        return value.Length >= width ? value[..width] : value.PadRight(width);
    }
}

/// <summary>控制台渲染代码使用的少量 StringBuilder 辅助方法。</summary>
internal static class StringBuilderExtensions
{
    public static void AppendLines(this StringBuilder builder, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }
    }
}


