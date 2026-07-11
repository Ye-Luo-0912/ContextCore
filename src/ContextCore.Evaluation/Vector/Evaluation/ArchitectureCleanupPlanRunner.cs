using System.Text;
using ContextCore.Evaluation.Contracts;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 架构清理计划。动态扫描真实源码结构，输出拆分建议。
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
        var srcRoot = Path.Combine(resolvedRepositoryRoot, "src");

        var evalCommandDir = Path.Combine(srcRoot, "ContextCore.Evaluation", "Commands");
        var controlRoomServiceDir = Path.Combine(srcRoot, "ContextCore.ControlRoom", "Services");
        var vectorEvaluationDir = Path.Combine(srcRoot, "ContextCore.Evaluation", "Vector", "Evaluation");
        var rendererPath = Path.Combine(srcRoot, "ContextCore.ControlRoom", "Rendering", "ServiceOperationalRenderer.cs");
        var abstractionsModelsDir = Path.Combine(srcRoot, "ContextCore.Abstractions", "Models");
        var abstractionsContractsDir = Path.Combine(srcRoot, "ContextCore.Abstractions", "Contracts");
        var unsupportedStoresPath = Path.Combine(srcRoot, "ContextCore.Core", "Services", "UnsupportedStores.cs");

        // Vector Evaluation runner file counts (Legacy subdir already deleted — not scanned)
        var gatesCount = CountFiles(Path.Combine(vectorEvaluationDir, "Gates"), "*.cs");
        var datasetCount = CountFiles(Path.Combine(vectorEvaluationDir, "Dataset"), "*.cs");
        var v5evalCount = CountFiles(Path.Combine(vectorEvaluationDir, "V5"), "*.cs");
        var v6evalCount = CountFiles(Path.Combine(vectorEvaluationDir, "V6"), "*.cs");
        var v7evalCount = CountFiles(Path.Combine(vectorEvaluationDir, "V7"), "*.cs");
        var v8evalCount = CountFiles(Path.Combine(vectorEvaluationDir, "V8"), "*.cs");
        var vectorEvaluationRootCount = CountFiles(vectorEvaluationDir, "*.cs", SearchOption.TopDirectoryOnly);
        var totalRunnerCount = vectorEvaluationRootCount + gatesCount + datasetCount
            + v5evalCount + v6evalCount + v7evalCount + v8evalCount;

        // Historical runner count (V9-V16_2 directories — should be 0 after deletion)
        var historicalRunnerCount = CountHistoricalRunnerFiles(vectorEvaluationDir);

        // DTO type counts (Abstractions/Models — runtime + eval DTO co-located in same assembly)
        var dtoRuntimeCount = CountDtoTypes(Path.Combine(abstractionsModelsDir, "VectorIndexDtos.cs"));
        var dtoRuntimeAltCount = CountDtoTypes(Path.Combine(abstractionsModelsDir, "VectorRuntimeDtos.cs"));
        var dtoEvalCount = CountDtoTypes(Path.Combine(abstractionsModelsDir, "VectorEvalReportDtos.cs"));
        var dtoGateCount = CountDtoTypes(Path.Combine(abstractionsModelsDir, "VectorGateReportDtos.cs"));
        var dtoSummaryCount = CountDtoTypes(Path.Combine(abstractionsModelsDir, "VectorControlRoomSummaryDtos.cs"));
        var dtoLegacyCount = CountDtoTypes(Path.Combine(abstractionsModelsDir, "VectorLegacyDtos.cs"));
        var totalDtoCount = dtoRuntimeCount + dtoRuntimeAltCount + dtoEvalCount
            + dtoGateCount + dtoSummaryCount + dtoLegacyCount;

        // EvalCommand partial files (10 partials expected after split)
        var evalCommandFiles = Directory.Exists(evalCommandDir)
            ? Directory.EnumerateFiles(evalCommandDir, "EvalCommand*.cs", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();
        var evalCommandFileCount = evalCommandFiles.Count;
        var evalCommandLines = evalCommandFiles.Sum(CountLines);

        // ControlRoomService partial files (6 partials expected after split)
        var controlRoomServiceFiles = Directory.Exists(controlRoomServiceDir)
            ? Directory.EnumerateFiles(controlRoomServiceDir, "ControlRoomService*.cs", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();
        var controlRoomServiceFileCount = controlRoomServiceFiles.Count;
        var controlRoomServiceLines = controlRoomServiceFiles.Sum(CountLines);

        var rendererLines = CountLines(rendererPath);
        var subcommandCount = CountSubcommandRefs(evalCommandDir);

        // Project dependency graph + per-project production code lines
        var projectRefs = ScanProjectReferences(srcRoot);
        var projectLines = ScanProjectCodeLines(srcRoot);

        // Max class length across all src/ .cs files
        var (maxClassFile, maxClassLines) = FindMaxClassLength(srcRoot);

        // Store contract count (I*Store interfaces in Abstractions/Contracts)
        var storeContractCount = CountStoreContracts(abstractionsContractsDir);

        // Unsupported capability count (Unsupported* placeholder classes)
        var unsupportedCount = CountUnsupportedClasses(unsupportedStoresPath);

        var diag = new List<string>
        {
            $"Repository root: {PathHygiene.ToRepoRelativePath(resolvedRepositoryRoot)}",
            "Project dependency graph:",
        };
        diag.AddRange(projectRefs.OrderBy(static kv => kv.Key)
            .Select(static kv => $"  {kv.Key} -> {string.Join(", ", kv.Value)}"));
        diag.Add("Per-project production code lines (src/):");
        diag.AddRange(projectLines.OrderBy(static kv => kv.Key)
            .Select(static kv => $"  {kv.Key}: {kv.Value}"));
        diag.AddRange(new[]
        {
            $"Vector/Evaluation files (total): {totalRunnerCount}",
            $"  Root: {vectorEvaluationRootCount}",
            $"  Gates: {gatesCount}",
            $"  Dataset: {datasetCount}",
            $"  V5: {v5evalCount}",
            $"  V6: {v6evalCount}",
            $"  V7: {v7evalCount}",
            $"  V8: {v8evalCount}",
            $"  Historical (V9-V16_2, deleted): {historicalRunnerCount}",
            $"DTO types (total): {totalDtoCount}",
            $"  VectorIndexDtos: {dtoRuntimeCount}",
            $"  VectorRuntimeDtos: {dtoRuntimeAltCount}",
            $"  EvalReportDtos: {dtoEvalCount}",
            $"  GateReportDtos: {dtoGateCount}",
            $"  SummaryDtos: {dtoSummaryCount}",
            $"  LegacyDtos: {dtoLegacyCount}",
            "DTO assembly attribution: ContextCore.Abstractions (runtime + eval DTO co-located)",
            $"EvalCommand partial files: {evalCommandFileCount}, total lines: {evalCommandLines}",
            $"ControlRoomService partial files: {controlRoomServiceFileCount}, total lines: {controlRoomServiceLines}",
            $"ServiceOperationalRenderer.cs lines: {rendererLines}",
            $"Max class length: {maxClassLines} ({maxClassFile})",
            $"Store contracts (I*Store): {storeContractCount}",
            $"Unsupported stores: {unsupportedCount}",
            $"Eval subcommand refs: {subcommandCount}",
        });

        var items = new List<ArchitectureCleanupItem>
        {
            new()
            {
                Priority = "done",
                Category = "EvalCommand 拆分 (已完成)",
                CurrentState = $"EvalCommand 已拆分为 {evalCommandFileCount} 个 partial 文件 (EvalCommand.*.cs)，总计 {evalCommandLines} 行，{subcommandCount} 个 subcommand case 分支",
                Recommendation = "已完成 — 无需进一步行动",
                Risk = "n/a — 已完成"
            },
            new()
            {
                Priority = "medium",
                Category = "ControlRoomService partial 拆分",
                CurrentState = $"ControlRoomService 已拆分为 {controlRoomServiceFileCount} 个 partial 文件 (ControlRoomService.*.cs)，总计 {controlRoomServiceLines} 行",
                Recommendation = "继续按 phase/area 合并重复的 loader/snapshot 调用点；每个 phase 的双调用 (首屏+刷新) 合并为单次求值+共享",
                Risk = "low — 纯重构"
            },
            new()
            {
                Priority = "medium",
                Category = "Vector Evaluation runner 目录组织",
                CurrentState = $"Vector/Evaluation: Gates ({gatesCount}), Dataset ({datasetCount}), V5 ({v5evalCount}), V6 ({v6evalCount}), V7 ({v7evalCount}), V8 ({v8evalCount}), 根目录 ({vectorEvaluationRootCount})；历史 V9-V16_2 目录文件数: {historicalRunnerCount}",
                Recommendation = "继续将 Gates 中相关 runner 合并为统一 gate pipeline；V5 中已冻结的 runner 标记为 deprecated 或迁移到 Legacy",
                Risk = "low — 已有目录结构"
            },
            new()
            {
                Priority = "medium",
                Category = "Abstractions DTO 归属 (OPT-005 待办)",
                CurrentState = $"Vector DTO 已拆分为 6 个文件，总计 {totalDtoCount} 类型，全部位于 ContextCore.Abstractions 程序集；runtime/eval DTO 未按程序集分离",
                Recommendation = "后续按 OPT-005 将 report/gate DTO 迁移到独立 ContextCore.Eval.Models 项目",
                Risk = "low — 已拆分，namespaces 和序列化行为未变"
            },
            new()
            {
                Priority = "medium",
                Category = "Renderer 区块重复",
                CurrentState = $"ServiceOperationalRenderer.cs: {rendererLines} 行，每个 V5/V6/V7/V8 phase 的渲染块模式几乎一致",
                Recommendation = "抽象 RenderBlock(phase, snapshot, condition) 辅助方法，减少重复",
                Risk = "low — 输出格式不变"
            },
            new()
            {
                Priority = "medium",
                Category = "最大类长度监控",
                CurrentState = $"当前最大类文件: {maxClassFile}，{maxClassLines} 行",
                Recommendation = "对超过 5000 行的类按职责进一步拆分 partial；持续监控以防止回退",
                Risk = "low — 纯重构"
            },
            new()
            {
                Priority = "medium",
                Category = "Store contract / Unsupported store 同步",
                CurrentState = $"Abstractions 中 I*Store 接口: {storeContractCount} 个；Core 中 Unsupported* 占位类: {unsupportedCount} 个",
                Recommendation = "保持接口与占位实现一一对应；新增 store 合约时同步新增 Unsupported 实现，避免静默丢弃数据",
                Risk = "low — 显式失败优于静默丢弃"
            },
            new()
            {
                Priority = "medium",
                Category = "阶段编号/文档索引",
                CurrentState = "V5.1–V5.10、V6.10–V6.16、V6.F、OPT0 — 阶段编号已膨胀到 2 位数",
                Recommendation = "冻结 V5/V6 阶段编号；OPT 阶段使用三位数字（如 OPT-001）；索引文档统一到 docs/ContextCore_Phase_Index.md",
                Risk = "low — 不影响运行时"
            },
            new()
            {
                Priority = "low",
                Category = "P15 构建文件锁",
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
        b.AppendLine("## 诊断");
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

    private static int CountHistoricalRunnerFiles(string vectorEvaluationDir)
    {
        if (!Directory.Exists(vectorEvaluationDir))
        {
            return 0;
        }

        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(vectorEvaluationDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Length <= 1 || name[0] != 'V')
            {
                continue;
            }

            var numPart = new string(name.Skip(1).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(numPart, out var num) && num >= 9)
            {
                count += Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Count();
            }
        }

        return count;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ScanProjectReferences(string srcRoot)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(srcRoot))
        {
            return result;
        }

        foreach (var csproj in Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            var refs = new List<string>();
            const string marker = "<ProjectReference Include=\"";
            foreach (var line in File.ReadLines(csproj))
            {
                var idx = line.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                var start = idx + marker.Length;
                var end = line.IndexOf('"', start);
                if (end > start)
                {
                    refs.Add(Path.GetFileNameWithoutExtension(line.Substring(start, end - start)));
                }
            }

            result[projectName] = refs;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, int> ScanProjectCodeLines(string srcRoot)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(srcRoot))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(srcRoot))
        {
            var projectName = Path.GetFileName(dir);
            var count = 0;
            foreach (var file in EnumerateSourceFiles(dir))
            {
                count += CountLines(file);
            }

            result[projectName] = count;
        }

        return result;
    }

    private static (string File, int Lines) FindMaxClassLength(string srcRoot)
    {
        if (!Directory.Exists(srcRoot))
        {
            return (string.Empty, 0);
        }

        var maxFile = string.Empty;
        var maxLines = 0;
        foreach (var dir in Directory.EnumerateDirectories(srcRoot))
        {
            foreach (var file in EnumerateSourceFiles(dir))
            {
                var lines = CountLines(file);
                if (lines > maxLines)
                {
                    maxLines = lines;
                    maxFile = file;
                }
            }
        }

        return (string.IsNullOrEmpty(maxFile) ? string.Empty : PathHygiene.ToRepoRelativePath(maxFile), maxLines);
    }

    private static int CountStoreContracts(string contractsDir)
    {
        if (!Directory.Exists(contractsDir))
        {
            return 0;
        }

        return Directory.EnumerateFiles(contractsDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(static file => File.ReadLines(file))
            .Count(static line =>
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("public interface ", StringComparison.Ordinal))
                {
                    return false;
                }

                var rest = trimmed.Substring("public interface ".Length);
                var name = rest.Split(new[] { ' ', ':', '<', '(', ',', '{' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                return name.Length > 1 && name[0] == 'I' && name.EndsWith("Store", StringComparison.Ordinal);
            });
    }

    private static int CountUnsupportedClasses(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        return File.ReadLines(path)
            .Count(static line => line.AsSpan().TrimStart().StartsWith("public sealed class Unsupported", StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*.cs"))
            {
                yield return file;
            }

            foreach (var dir in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(dir);
                if (name is "bin" or "obj")
                {
                    continue;
                }

                stack.Push(dir);
            }
        }
    }
}
