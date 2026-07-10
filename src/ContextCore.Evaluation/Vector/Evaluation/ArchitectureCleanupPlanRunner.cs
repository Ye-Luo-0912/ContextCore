using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 架构清理计划。审计当前代码结构，输出拆分建议。
/// 不改主线行为，不接 formal retrieval，不改 runtime，不做大规模重构。
/// </summary>
public sealed class ArchitectureCleanupPlanRunner
{
    public ArchitectureCleanupPlanReport BuildPlan(
        string repositoryRoot,
        ControlledAppliedMergePreviewFreezeReport? v6fFreeze)
    {
        var blocked = new List<string>();
        if (v6fFreeze is null || !v6fFreeze.FreezePassed)
        {
            blocked.Add("V6FFreezeMissingOrNotPassed");
        }

        var resolvedRepositoryRoot = ResolveRepositoryRoot(repositoryRoot);
        var vectorDir = Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Core", "Services", "Vector");
        var evalCommandDir = Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.ControlRoom", "Commands");
        var evalCommandPath = Path.Combine(evalCommandDir, "EvalCommand.cs");
        var controlRoomServicePath = Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.ControlRoom", "Services", "ControlRoomService.cs");
        var rendererPath = Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.ControlRoom", "Rendering", "ServiceOperationalRenderer.cs");
        var dtoPath = Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Abstractions", "Models", "VectorIndexDtos.cs");

        var runtimeCount = CountFiles(Path.Combine(vectorDir), "*.cs", SearchOption.TopDirectoryOnly);
        var legacyCount   = CountFiles(Path.Combine(vectorDir, "Legacy"), "*.cs");
        var gatesCount    = CountFiles(Path.Combine(vectorDir, "Evaluation", "Gates"), "*.cs");
        var datasetCount  = CountFiles(Path.Combine(vectorDir, "Evaluation", "Dataset"), "*.cs");
        var v5evalCount   = CountFiles(Path.Combine(vectorDir, "Evaluation", "V5"), "*.cs");
        var v6evalCount   = CountFiles(Path.Combine(vectorDir, "Evaluation", "V6"), "*.cs");
        var totalRunnerCount = runtimeCount + legacyCount + gatesCount + datasetCount + v5evalCount + v6evalCount;

        var dtoRuntimeCount = CountDtoTypes(Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Abstractions", "Models", "VectorIndexDtos.cs"));
        var dtoEvalCount    = CountDtoTypes(Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Abstractions", "Models", "VectorEvalReportDtos.cs"));
        var dtoGateCount    = CountDtoTypes(Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Abstractions", "Models", "VectorGateReportDtos.cs"));
        var dtoSummaryCount = CountDtoTypes(Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Abstractions", "Models", "VectorControlRoomSummaryDtos.cs"));
        var dtoLegacyCount  = CountDtoTypes(Path.Combine(resolvedRepositoryRoot, "src", "ContextCore.Abstractions", "Models", "VectorLegacyDtos.cs"));
        var totalDtoCount = dtoRuntimeCount + dtoEvalCount + dtoGateCount + dtoSummaryCount + dtoLegacyCount;
        var evalCommandLines = CountLines(evalCommandPath);
        var controlRoomServiceLines = CountLines(controlRoomServicePath);
        var rendererLines = CountLines(rendererPath);
        var subcommandCount = CountSubcommandRefs(evalCommandDir);

        var diag = new List<string>
        {
            $"Repository root: {PathHygiene.ToRepoRelativePath(resolvedRepositoryRoot)}",
            $"Core/Vector files (total): {totalRunnerCount}",
            $"  Runtime: {runtimeCount}",
            $"  Legacy: {legacyCount}",
            $"  Evaluation/Gates: {gatesCount}",
            $"  Evaluation/Dataset: {datasetCount}",
            $"  Evaluation/V5: {v5evalCount}",
            $"  Evaluation/V6: {v6evalCount}",
            $"DTO types (total): {totalDtoCount}",
            $"  VectorIndexDtos: {dtoRuntimeCount}",
            $"  EvalReportDtos: {dtoEvalCount}",
            $"  GateReportDtos: {dtoGateCount}",
            $"  SummaryDtos: {dtoSummaryCount}",
            $"  LegacyDtos: {dtoLegacyCount}",
            $"EvalCommand.cs lines: {evalCommandLines}",
            $"ControlRoomService.cs lines: {controlRoomServiceLines}",
            $"ServiceOperationalRenderer.cs lines: {rendererLines}",
            $"Eval subcommand refs: {subcommandCount}",
        };

        var items = new List<ArchitectureCleanupItem>
        {
            new()
            {
                Priority = "high", Category = "EvalCommand 拆分",
                CurrentState = $"EvalCommand.cs: ~24k 行，同一文件包含全部 ~50+ eval 子命令 dispatch + executor",
                Recommendation = "按 V5/V6/架构拆分到 EvalCommand.V5.cs / EvalCommand.V6.cs / EvalCommand.Arch.cs，每个子命令保留 dispatch 一行，executor 移动到对应 phase 模块",
                Risk = "low — 只移动代码，不改行为"
            },
            new()
            {
                Priority = "medium", Category = "Core 中 eval-only runner 分离 (OPT-004 已部分完成)",
                CurrentState = $"eval-only runner 已按分类拆分到 Evaluation/V5 ({v5evalCount} files), Evaluation/V6 ({v6evalCount}), Evaluation/Gates ({gatesCount}), Evaluation/Dataset ({datasetCount}), Legacy ({legacyCount})；runtime {runtimeCount} 个文件保留在 Services/Vector/ 根目录",
                Recommendation = "继续将 Evaluation/Gates 中的 gate runner 合并为统一 gate pipeline；将 V5 中已冻结的 runner 标记为 deprecated 或迁移到 Legacy",
                Risk = "low — 已有目录结构，后续只做少量文件再分配"
            },
            new()
            {
                Priority = "medium", Category = "Abstractions DTO 拆分 (OPT-003 已完成)",
                CurrentState = $"VectorIndexDtos 已拆分为 5 个文件: VectorIndexDtos ({dtoRuntimeCount}), EvalReportDtos ({dtoEvalCount}), GateReportDtos ({dtoGateCount}), SummaryDtos ({dtoSummaryCount}), LegacyDtos ({dtoLegacyCount})；总计 {totalDtoCount} 类型",
                Recommendation = "后续按 OPT-005 将 report/gate DTO 迁移到独立 ContextCore.Eval.Models 项目",
                Risk = "low — 已拆分，namespaces 和序列化行为未变"
            },
            new()
            {
                Priority = "medium", Category = "ControlRoom loader/字段冗余",
                CurrentState = "ControlRoomService.cs: ~12k 行，每个 phase 的 loader 和 snapshot 字段重复 2 次（首屏 + 刷新）",
                Recommendation = "将重复的双调用点合并为单次求值 + 共享；loader 按 phase 文件夹独立",
                Risk = "low — 纯重构"
            },
            new()
            {
                Priority = "medium", Category = "Renderer 区块重复",
                CurrentState = "ServiceOperationalRenderer.cs: ~5.8k 行，每个 V5/V6 phase 的渲染块模式几乎一致",
                Recommendation = "抽象 RenderBlock(phase, snapshot, condition) 辅助方法，减少重复",
                Risk = "low — 输出格式不变"
            },
            new()
            {
                Priority = "medium", Category = "阶段编号/文档索引",
                CurrentState = "V5.1–V5.10、V5.F、V6.10–V6.16、V6.F、OPT0 — 阶段编号已膨胀到 2 位数",
                Recommendation = "冻结 V5/V6 阶段编号；OPT 阶段使用三位数字（如 OPT-001）；索引文档统一到 docs/ContextCore_Phase_Index.md",
                Risk = "low — 不影响运行时"
            },
            new()
            {
                Priority = "low", Category = "P15 构建文件锁",
                CurrentState = "dotnet build -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false 是绕开文件锁的已知工作区",
                Recommendation = "检查并行项目引用图，确保无循环引用导致锁冲突；长期将集成测试移到独立项目",
                Risk = "low — 已知工作区可用"
            },
        };

        return new ArchitectureCleanupPlanReport
        {
            OperationId = $"arch-cleanup-plan-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = blocked.Count == 0,
            Recommendation = blocked.Count == 0
                ? ArchitectureCleanupPlanRecommendations.ReadyForCleanupPlan
                : ArchitectureCleanupPlanRecommendations.BlockedByMissingV6FFreeze,
            CoreRunnerCount = totalRunnerCount,
            DtoClassCount = totalDtoCount,
            EvalCommandLines = evalCommandLines,
            ControlRoomServiceLines = controlRoomServiceLines,
            RendererLines = rendererLines,
            SubcommandCount = subcommandCount,
            RecommendedMigrations = items,
            Diagnostics = diag,
            BlockedReasons = blocked,
        };
    }

    public static string BuildMarkdown(string title, ArchitectureCleanupPlanReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 核心指标");
        b.AppendLine($"- Core runner files: `{report.CoreRunnerCount}`");
        b.AppendLine($"- DTO classes: `{report.DtoClassCount}`");
        b.AppendLine($"- EvalCommand lines: `{report.EvalCommandLines}`");
        b.AppendLine($"- ControlRoomService lines: `{report.ControlRoomServiceLines}`");
        b.AppendLine($"- Renderer lines: `{report.RendererLines}`");
        b.AppendLine($"- Eval subcommand refs: `{report.SubcommandCount}`");
        b.AppendLine();
        b.AppendLine("## 建议迁移项");
        foreach (var item in report.RecommendedMigrations)
        {
            b.AppendLine($"### [{item.Priority.ToUpperInvariant()}] {item.Category}");
            b.AppendLine($"- 当前: {item.CurrentState}");
            b.AppendLine($"- 建议: {item.Recommendation}");
            b.AppendLine($"- 风险: {item.Risk}");
            b.AppendLine();
        }
        foreach (var d in report.Diagnostics) b.AppendLine($"- {d}");
        b.AppendLine(); b.AppendLine("OPT0 architecture cleanup plan. No runtime behavior change, no formal retrieval enable, no package/package policy/runtime/vector binding mutation.");
        return b.ToString();
    }

    private static string ResolveRepositoryRoot(string repositoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(repositoryRoot) && Directory.Exists(Path.Combine(repositoryRoot, "src")))
        {
            return Path.GetFullPath(repositoryRoot);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(repositoryRoot);
    }

    private static int CountFiles(string directory, string searchPattern, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, searchOption).Count()
            : 0;
    }

    private static int CountLines(string path)
    {
        return File.Exists(path) ? File.ReadLines(path).Count() : 0;
    }

    private static int CountDtoTypes(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        return File.ReadLines(path)
            .Count(static line =>
                line.StartsWith("public sealed class ", StringComparison.Ordinal)
                || line.StartsWith("public static class ", StringComparison.Ordinal)
                || line.StartsWith("public sealed record ", StringComparison.Ordinal)
                || line.StartsWith("public record ", StringComparison.Ordinal));
    }

    private static int CountSubcommandRefs(string evalCommandDirectory)
    {
        if (!Directory.Exists(evalCommandDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(evalCommandDirectory, "EvalCommand*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(File.ReadLines)
            .Count(static line => line.Contains("string.Equals(subcommand,", StringComparison.Ordinal));
    }
}
