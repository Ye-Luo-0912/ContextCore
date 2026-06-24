using System.Text;
using System.Text.RegularExpressions;

namespace ContextCore.Core.Services;

/// <summary>
/// DTO 拆分计划：扫描 VectorIndexDtos.cs，识别每个 report/contract/DTO 类别，
/// 输出分类计数和拆分建议。不移动任何 DTO，不改 runtime。
/// </summary>
public sealed class DtoSplitPlanRunner
{
    // 扫描 VectorIndexDtos.cs 并对每条 public class 分类
    public DtoSplitPlanReport BuildPlan(DtoSplitPlanOptions? options = null)
    {
        options ??= new DtoSplitPlanOptions();
        var srcPath = Path.GetFullPath(options.SourcePath);
        if (!File.Exists(srcPath))
            return new DtoSplitPlanReport { PlanGenerated = false, ErrorDescription = $"File not found: {srcPath}" };

        var text = File.ReadAllText(srcPath);
        var lines = File.ReadAllLines(srcPath);

        // Regex: 摘取 /// <summary>...</summary> + public sealed/static class Name
        var summaryPattern = new Regex(@"///\s*<summary>\s*(.*?)\s*</summary>", RegexOptions.Compiled | RegexOptions.Singleline);
        var classPattern = new Regex(@"public\s+(sealed|static)?\s*class\s+(\w+)", RegexOptions.Compiled);

        var classes = new List<ScannedClass>();
        string? currentSummary = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("/// <summary"))
            {
                var m = summaryPattern.Match(trimmed);
                if (m.Success) currentSummary = m.Groups[1].Value.Trim();
            }
            else
            {
                var m = classPattern.Match(trimmed);
                if (m.Success)
                {
                    classes.Add(new ScannedClass(m.Groups[2].Value, currentSummary ?? "", m.Groups[1].Value == "static"));
                    currentSummary = null;
                }
            }
        }

        // 按名称/摘要关键词分类
        var runtime = new List<string>();
        var evalReport = new List<string>();
        var gateReport = new List<string>();
        var summaryDtos = new List<string>();
        var legacy = new List<string>();

        foreach (var c in classes)
        {
            var key = c.IsStatic ? c.Name : c.Name;
            if (c.Name.Contains("Report") || c.Name.Contains("Gate") || c.Name.Contains("Freeze") || c.Name.Contains("Decision"))
            {
                if (c.Name.Contains("Gate") || c.Name.Contains("Freeze")) gateReport.Add(c.Name);
                else if (c.Name.Contains("Decision") || c.Name.Contains("Plan")) gateReport.Add(c.Name);
                else if (c.Name.Contains("Summary") || c.Name.Contains("Snapshot")) summaryDtos.Add(c.Name);
                else evalReport.Add(c.Name);
            }
            else if (c.Name.Contains("Request") || c.Name.Contains("Response") || c.Name.Contains("Result") || c.Name.Contains("Query"))
            {
                if (c.Name.Contains("Diagnostic") || c.Name.Contains("Reindex") || c.Name.Contains("Coverage") || c.Name.Contains("Profile"))
                    evalReport.Add(c.Name);
                else
                    runtime.Add(c.Name);
            }
            else if (c.Name.Contains("Options") || c.Name.Contains("Config") || c.Name.Contains("Policy") || c.Name.Contains("Contract") || c.Name.Contains("Envelope"))
            {
                runtime.Add(c.Name);
            }
            else if (c.Name.Contains("Recommenda") || c.Name.Contains("Status") || c.Name.Contains("Mode") || c.Name.Contains("Metric") || c.Name.Contains("Cluster"))
            {
                if (c.Name.Contains("Status")) runtime.Add(c.Name);
                else evalReport.Add(c.Name);
            }
            else if (c.IsStatic && (c.Name.Contains("Profile") || c.Name.Contains("Modes") || c.Name.Contains("Section") || c.Name.Contains("Type")))
            {
                runtime.Add(c.Name);
            }
            else
            {
                // fallback — 看 summary 是否包含 eval/report 关键词
                var sm = c.Summary.ToLowerInvariant();
                if (sm.Contains("report") || sm.Contains("gate") || sm.Contains("freeze") || sm.Contains("eval"))
                    evalReport.Add(c.Name);
                else if (sm.Contains("request") || sm.Contains("result") || sm.Contains("option") || sm.Contains("config"))
                    runtime.Add(c.Name);
                else
                    legacy.Add(c.Name); // 无法明确分类
            }
        }

        return new DtoSplitPlanReport
        {
            PlanGenerated = true,
            SourceFile = srcPath,
            TotalClasses = classes.Count,
            RuntimeContractCount = runtime.Count,
            EvalReportCount = evalReport.Count,
            GateReportCount = gateReport.Count,
            ControlRoomSummaryCount = summaryDtos.Count,
            LegacyCount = legacy.Count,
            TargetFiles = new[]
            {
                "VectorRuntimeDtos.cs — runtime adapter request/result/contract/envelope/options",
                "VectorEvalReportDtos.cs — phase eval report DTO（不含 gate）",
                "VectorGateReportDtos.cs — gate/freeze/decision/plan report DTO",
                "VectorControlRoomSummaryDtos.cs — ControlRoom summary/snapshot 用 DTO",
                "VectorLegacyDtos.cs — 已废弃或无法明确分类的 DTO（逐步淘汰）"
            }.ToList(),
            NotMovable = new[] {
                "IContextRetrievalAdapter / IShadowRetrievalAdapter / NoOpContextRetrievalAdapter（runtime adapter contract）",
                "RetrievalAdapterRequest / RetrievalAdapterResult（runtime adapter request/result DTO）",
                "FormalAdapterInputContract（formal adapter input contract）",
                "public API client DTO（ContextCoreClient DTO）"
            }.ToList(),
            Deferred = new[] {
                "V5.1 ~ V5.3 phase reports（旧阶段报告——冻结后可归档）",
                "V4 runtime experiment reports（V4 实验报告——只读）",
                "Superseded eval policy/recommendation DTO（已被后续阶段替代）"
            }.ToList(),
            BlockedReasons = Array.Empty<string>()
        };
    }

    public static string BuildMarkdown(string title, DtoSplitPlanReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"PlanGenerated: `{r.PlanGenerated}`");
        b.AppendLine($"Source: `{r.SourceFile}`");
        b.AppendLine($"TotalClasses: `{r.TotalClasses}`"); b.AppendLine();
        b.AppendLine("## 分类统计");
        b.AppendLine($"- RuntimeContract: `{r.RuntimeContractCount}` — runtime adapter request/result/contract/envelope");
        b.AppendLine($"- EvalReport: `{r.EvalReportCount}` — phase eval report DTO（不含 gate）");
        b.AppendLine($"- GateReport: `{r.GateReportCount}` — gate/freeze/decision/plan report DTO");
        b.AppendLine($"- ControlRoomSummary: `{r.ControlRoomSummaryCount}` — ControlRoom summary/snapshot 用 DTO");
        b.AppendLine($"- Legacy: `{r.LegacyCount}` — 已废弃或无法明确分类的 DTO"); b.AppendLine();
        b.AppendLine("## 目标拆分文件");
        foreach (var f in r.TargetFiles) b.AppendLine($"- `{f}`");
        b.AppendLine(); b.AppendLine("## 不可迁移项");
        foreach (var f in r.NotMovable) b.AppendLine($"- {f}");
        b.AppendLine(); b.AppendLine("## 可延后项");
        foreach (var f in r.Deferred) b.AppendLine($"- {f}");
        AppendList(b, "Blocked", r.BlockedReasons);
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string t, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {t}");
        if (items.Count == 0) b.AppendLine("- (empty)");
        else foreach (var i in items) b.AppendLine($"- `{i}`");
    }

    private readonly record struct ScannedClass(string Name, string Summary, bool IsStatic);
}

public sealed class DtoSplitPlanReport
{
    public string OperationId { get; init; } = $"dto-split-plan-{Guid.NewGuid():N}";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanGenerated { get; init; }
    public string ErrorDescription { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public int TotalClasses { get; init; }
    public int RuntimeContractCount { get; init; }
    public int EvalReportCount { get; init; }
    public int GateReportCount { get; init; }
    public int ControlRoomSummaryCount { get; init; }
    public int LegacyCount { get; init; }
    public IReadOnlyList<string> TargetFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NotMovable { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Deferred { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class DtoSplitPlanOptions
{
    public string SourcePath { get; init; } = "src/ContextCore.Abstractions/Models/VectorIndexDtos.cs";
}