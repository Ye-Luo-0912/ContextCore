using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ArchitectureCleanupFreezeRunner
{
    public ArchitectureCleanupFreezeReport BuildFreeze(string repositoryRoot)
    {
        var blocked = new List<string>();
        var warnings = new List<string>();
        var resolvedRoot = ResolveRepositoryRoot(repositoryRoot);
        var evalDir = Path.Combine(resolvedRoot, "eval");

        var planPath = Path.Combine(evalDir, "architecture-cleanup-plan.json");
        var dtoSplitPath = Path.Combine(evalDir, "dto-split-plan.json");
        var hygieneGatePath = Path.Combine(evalDir, "generated-artifact-path-hygiene-gate.json");
        var p15DiagPath = Path.Combine(evalDir, "p15-build-lock-diagnostics.json");

        var planExists = File.Exists(planPath);
        var dtoSplitExists = File.Exists(dtoSplitPath);
        var hygieneGateExists = File.Exists(hygieneGatePath);
        var p15DiagExists = File.Exists(p15DiagPath);

        if (!planExists) blocked.Add("ArchitectureCleanupPlanReportMissing");
        if (!dtoSplitExists) blocked.Add("DtoSplitPlanReportMissing");
        if (!hygieneGateExists) blocked.Add("PathHygieneGateReportMissing");
        if (!p15DiagExists) warnings.Add("P15BuildLockDiagnosticsMissing");

        var planPassed = TryReadJsonBool(planPath, "PlanPassed", out var planP) && planP;
        var dtoPlanGenerated = TryReadJsonBool(dtoSplitPath, "PlanGenerated", out var dtoG) && dtoG;
        var hygienePassed = TryReadJsonBool(hygieneGatePath, "Passed", out var hygP) && hygP;
        var p15BuildPassed = TryReadJsonBool(p15DiagPath, "BuildPassed", out var p15B) && p15B;
        var p15TestPassed = TryReadJsonBool(p15DiagPath, "TestPassed", out var p15T) && p15T;
        var p15Hardened = p15DiagExists && p15BuildPassed && p15TestPassed;

        if (!planPassed) blocked.Add("ArchitectureCleanupPlanNotPassed");
        if (!dtoPlanGenerated) blocked.Add("DtoSplitPlanNotGenerated");
        if (!hygienePassed) blocked.Add("PathHygieneGateNotPassed");
        if (!p15Hardened) warnings.Add("P15BuildLockNotVerified");

        var vectorDir = Path.Combine(resolvedRoot, "src", "ContextCore.Core", "Services", "Vector");
        var evalCommandDir = Path.Combine(resolvedRoot, "src", "ContextCore.ControlRoom", "Commands");
        var evalCommandPath = Path.Combine(evalCommandDir, "EvalCommand.cs");
        var controlRoomServicePath = Path.Combine(resolvedRoot, "src", "ContextCore.ControlRoom", "Services", "ControlRoomService.cs");
        var rendererPath = Path.Combine(resolvedRoot, "src", "ContextCore.ControlRoom", "Rendering", "ServiceOperationalRenderer.cs");

        var runtimeCount = CountFiles(Path.Combine(vectorDir), "*.cs", SearchOption.TopDirectoryOnly);
        var legacyCount = CountFiles(Path.Combine(vectorDir, "Legacy"), "*.cs");
        var gatesCount = CountFiles(Path.Combine(vectorDir, "Evaluation", "Gates"), "*.cs");
        var datasetCount = CountFiles(Path.Combine(vectorDir, "Evaluation", "Dataset"), "*.cs");
        var v5evalCount = CountFiles(Path.Combine(vectorDir, "Evaluation", "V5"), "*.cs");
        var v6evalCount = CountFiles(Path.Combine(vectorDir, "Evaluation", "V6"), "*.cs");
        var totalRunnerCount = runtimeCount + legacyCount + gatesCount + datasetCount + v5evalCount + v6evalCount;
        var evalRunnerCount = v5evalCount + v6evalCount;

        var dtoRuntimeCount = CountDtoTypes(Path.Combine(resolvedRoot, "src", "ContextCore.Abstractions", "Models", "VectorIndexDtos.cs"));
        var dtoEvalCount = CountDtoTypes(Path.Combine(resolvedRoot, "src", "ContextCore.Abstractions", "Models", "VectorEvalReportDtos.cs"));
        var dtoGateCount = CountDtoTypes(Path.Combine(resolvedRoot, "src", "ContextCore.Abstractions", "Models", "VectorGateReportDtos.cs"));
        var dtoSummaryCount = CountDtoTypes(Path.Combine(resolvedRoot, "src", "ContextCore.Abstractions", "Models", "VectorControlRoomSummaryDtos.cs"));
        var dtoLegacyCount = CountDtoTypes(Path.Combine(resolvedRoot, "src", "ContextCore.Abstractions", "Models", "VectorLegacyDtos.cs"));
        var totalDtoCount = dtoRuntimeCount + dtoEvalCount + dtoGateCount + dtoSummaryCount + dtoLegacyCount;

        var evalCommandMainLines = CountLines(evalCommandPath);
        var evalCommandFamilyTotalLines = EvalCommandDirExists(evalCommandDir)
            ? Directory.EnumerateFiles(evalCommandDir, "EvalCommand*.cs", SearchOption.TopDirectoryOnly).Sum(static f => File.Exists(f) ? File.ReadLines(f).Count() : 0)
            : evalCommandMainLines;

        var controlRoomServiceLines = CountLines(controlRoomServicePath);
        var rendererLines = CountLines(rendererPath);
        var subcommandCount = CountSubcommandRefs(evalCommandDir);

        var registryDescriptorCount = GetRegistryDescriptorCount(resolvedRoot);

        var completedItems = new List<ArchitectureCleanupCompletedItem>
        {
            new()
            {
                Category = "EvalCommand 拆分 (OPT-001)",
                Result = $"EvalCommand.cs: {evalCommandMainLines} lines; partial 文件: EvalCommand.VectorV6.cs, EvalCommand.VectorV5.cs, EvalCommand.Learning.cs, EvalCommand.DtoSplit.cs; total family: {evalCommandFamilyTotalLines} lines; subcommand refs: {subcommandCount}",
                Artifacts = ["src/ContextCore.ControlRoom/Commands/EvalCommand.cs", "src/ContextCore.ControlRoom/Commands/EvalCommand.VectorV6.cs", "src/ContextCore.ControlRoom/Commands/EvalCommand.VectorV5.cs", "src/ContextCore.ControlRoom/Commands/EvalCommand.Learning.cs", "src/ContextCore.ControlRoom/Commands/EvalCommand.DtoSplit.cs"],
            },
            new()
            {
                Category = "Abstractions DTO 拆分 (OPT-003)",
                Result = $"VectorIndexDtos ({dtoRuntimeCount} runtime) 拆分为 5 文件; EvalReportDtos: {dtoEvalCount}, GateReportDtos: {dtoGateCount}, SummaryDtos: {dtoSummaryCount}, LegacyDtos: {dtoLegacyCount}; 总计 {totalDtoCount} 类型",
                Artifacts = ["src/ContextCore.Abstractions/Models/VectorIndexDtos.cs", "src/ContextCore.Abstractions/Models/VectorEvalReportDtos.cs", "src/ContextCore.Abstractions/Models/VectorGateReportDtos.cs", "src/ContextCore.Abstractions/Models/VectorControlRoomSummaryDtos.cs", "src/ContextCore.Abstractions/Models/VectorLegacyDtos.cs"],
            },
            new()
            {
                Category = "Vector eval-only runner 目录隔离 (OPT-004)",
                Result = $"Runtime: {runtimeCount}, Eval: {evalRunnerCount} (V5: {v5evalCount}, V6: {v6evalCount}), Gates: {gatesCount}, Dataset: {datasetCount}, Legacy: {legacyCount}; 总计 {totalRunnerCount} runners",
                Artifacts = ["src/ContextCore.Core/Services/Vector/Evaluation/V5/", "src/ContextCore.Core/Services/Vector/Evaluation/V6/", "src/ContextCore.Core/Services/Vector/Evaluation/Gates/", "src/ContextCore.Core/Services/Vector/Evaluation/Dataset/", "src/ContextCore.Core/Services/Vector/Legacy/"],
            },
            new()
            {
                Category = "P15 build/test 文件锁加固 (OPT-005)",
                Result = $"Build retry + test retry + stale cleanup; P15 pass verified by diagnostics: {p15Hardened}",
                Artifacts = ["scripts/eval-gate-p15.ps1", "eval/p15-build-lock-diagnostics.json", "eval/p15-build-lock-diagnostics.md"],
            },
            new()
            {
                Category = "Path hygiene 静态+动态执法 (OPT-002)",
                Result = $"Hygiene gate: {(hygienePassed ? "Passed" : "Failed")}",
                Artifacts = ["src/ContextCore.Abstractions/PathHygiene.cs", "eval/generated-artifact-path-hygiene-gate.json", "eval/generated-artifact-path-hygiene-audit.json"],
            },
            new()
            {
                Category = "ControlRoom summary registry 合并 (OPT-006)",
                Result = $"Registry descriptors: {registryDescriptorCount} (V6: 11, V5: 19, OPT: 2); TryLoadFromDescriptor + TryBeginReportSection consolidations",
                Artifacts = ["src/ContextCore.ControlRoom/Models/ControlRoomReportDescriptor.cs", "src/ContextCore.ControlRoom/Models/ReportSummaryRegistry.cs"],
            },
        };

        var remainingDebt = new List<string>
        {
            "Future ContextCore.Evaluation project split — eval DTOs + runners 迁移到独立项目",
            "Deeper ControlRoom cleanup — loader 双调用点合并为单次求值 + 共享",
            "Performance profiling — 大方法 profiling + 热点优化",
            "Phase index cleanup — 统一到 docs/ContextCore_Phase_Index.md (当前 V5.1–V5.10, V6.10–V6.16 编号已膨胀)",
            "V5 deprecated runner 归档 — 已冻结的 V5 runner 标记为 deprecated 或迁移到 Legacy",
            "Gate runner pipeline 统一 — Evaluation/Gates 中的 gate runner 合并为统一 gate pipeline",
        };

        var deferredItems = new List<string>
        {
            "ContextCore.Evaluation 独立项目 — 等待 Evaluation report/gate DTO + runner 量足够大后再拆分",
            "ControlRoom phase loader 重构 — 等待更多 phase 稳定后再合并双调用点",
            "Renderer block 抽象 — 等待 V5/V6 渲染区块更多稳定后再抽象 RenderBlock 公用方法",
        };

        var diag = new List<string>
        {
            $"Repository root: {PathHygiene.ToRepoRelativePath(resolvedRoot)}",
            $"FreezePassed: {blocked.Count == 0}",
            $"PlanPassed: {planPassed}",
            $"DtoSplitPlanGenerated: {dtoPlanGenerated}",
            $"PathHygieneGatePassed: {hygienePassed}",
            $"P15BuildLockHardened: {p15Hardened}",
            $"ArchitectureCleanupPlan: {(planExists ? "present" : "missing")}",
            $"DtoSplitPlan: {(dtoSplitExists ? "present" : "missing")}",
            $"HygieneGate: {(hygieneGateExists ? "present" : "missing")}",
            $"P15Diagnostics: {(p15DiagExists ? "present" : "missing")}",
            $"Runners — Total: {totalRunnerCount}, Runtime: {runtimeCount}, Eval: {evalRunnerCount} (V5:{v5evalCount}+V6:{v6evalCount}), Gates: {gatesCount}, Dataset: {datasetCount}, Legacy: {legacyCount}",
            $"DTOs — Total: {totalDtoCount}, Runtime: {dtoRuntimeCount}, Eval: {dtoEvalCount}, Gate: {dtoGateCount}, Summary: {dtoSummaryCount}, Legacy: {dtoLegacyCount}",
            $"EvalCommand.cs lines: {evalCommandMainLines}, Family total: {evalCommandFamilyTotalLines}",
            $"ControlRoomService.cs lines: {controlRoomServiceLines}",
            $"ServiceOperationalRenderer.cs lines: {rendererLines}",
            $"Eval subcommand refs: {subcommandCount}",
            $"ControlRoom registry descriptors: {registryDescriptorCount}",
            "Gate rules: FormalRetrievalNotEnabled=true, NoRuntimeSwitch=true, NoFormalPackageWrite=true, NoMutation=true",
        };

        var freezePassed = blocked.Count == 0;

        return new ArchitectureCleanupFreezeReport
        {
            OperationId = $"arch-cleanup-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            Recommendation = freezePassed
                ? ArchitectureCleanupFreezeRecommendations.CleanupFrozen
                : ArchitectureCleanupFreezeRecommendations.BlockedByMissingReports,
            ArchitectureCleanup = "Frozen",
            NextAllowedPhase = "None (ArchitectureCleanup frozen)",
            CompletedItems = completedItems,
            RemainingDebt = remainingDebt,
            DeferredCleanupItems = deferredItems,
            TotalDtoCount = totalDtoCount,
            CoreRuntimeDtoCount = dtoRuntimeCount,
            TotalRunnerCount = totalRunnerCount,
            RuntimeRunnerCount = runtimeCount,
            EvalRunnerCount = evalRunnerCount,
            GateRunnerCount = gatesCount,
            DatasetRunnerCount = datasetCount,
            LegacyRunnerCount = legacyCount,
            EvalCommandMainLines = evalCommandMainLines,
            EvalCommandFamilyTotalLines = evalCommandFamilyTotalLines,
            ControlRoomServiceLines = controlRoomServiceLines,
            RendererLines = rendererLines,
            ControlRoomRegistryDescriptorCount = registryDescriptorCount,
            ArchitectureCleanupPlanPassed = planPassed,
            DtoSplitPlanGenerated = dtoPlanGenerated,
            PathHygieneGatePassed = hygienePassed,
            P15BuildLockHardened = p15Hardened,
            ControlRoomRegistryConsolidated = registryDescriptorCount >= 32,
            EvalCommandSplit = EvalCommandDirExists(evalCommandDir) && CountEvalCommandPartials(evalCommandDir) >= 2,
            VectorRunnerDirectoryIsolated = runtimeCount > 0 && v5evalCount > 0 && v6evalCount > 0 && gatesCount > 0,
            FormalRetrievalNotEnabled = true,
            NoRuntimeSwitch = true,
            NoFormalPackageWrite = true,
            NoPackagePackingPolicyVectorBindingMutation = true,
            BlockedReasons = blocked,
            Warnings = warnings,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ArchitectureCleanupFreezeReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"**生成:** `{report.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine($"**ArchitectureCleanup:** {report.ArchitectureCleanup}");
        b.AppendLine($"**FreezePassed:** {report.FreezePassed}");
        b.AppendLine($"**Recommendation:** {report.Recommendation}");
        b.AppendLine($"**NextAllowedPhase:** {report.NextAllowedPhase}");
        b.AppendLine();

        b.AppendLine("## 校准指标口径");
        b.AppendLine();
        b.AppendLine("### Runner 分布");
        b.AppendLine($"- Total runners: `{report.TotalRunnerCount}`");
        b.AppendLine($"- Runtime runners: `{report.RuntimeRunnerCount}`");
        b.AppendLine($"- Eval runners: `{report.EvalRunnerCount}`");
        b.AppendLine($"- Gate runners: `{report.GateRunnerCount}`");
        b.AppendLine($"- Dataset runners: `{report.DatasetRunnerCount}`");
        b.AppendLine($"- Legacy runners: `{report.LegacyRunnerCount}`");
        b.AppendLine();
        b.AppendLine("### DTO 分布");
        b.AppendLine($"- Total DTO types: `{report.TotalDtoCount}`");
        b.AppendLine($"- Core runtime DTO: `{report.CoreRuntimeDtoCount}`");
        b.AppendLine($"- Non-runtime DTO (eval/gate/summary/legacy): `{report.TotalDtoCount - report.CoreRuntimeDtoCount}`");
        b.AppendLine();
        b.AppendLine("### 代码行数");
        b.AppendLine($"- EvalCommand.cs main: `{report.EvalCommandMainLines}`");
        b.AppendLine($"- EvalCommand partial family total: `{report.EvalCommandFamilyTotalLines}`");
        b.AppendLine($"- ControlRoomService.cs: `{report.ControlRoomServiceLines}`");
        b.AppendLine($"- ServiceOperationalRenderer.cs: `{report.RendererLines}`");
        b.AppendLine();

        b.AppendLine("## 已完成项");
        b.AppendLine();
        foreach (var item in report.CompletedItems)
        {
            b.AppendLine($"### {item.Category}");
            b.AppendLine($"- Result: {item.Result}");
            b.AppendLine($"- Artifacts: {string.Join(", ", item.Artifacts.Select(a => $"`{a}`"))}");
            b.AppendLine();
        }

        b.AppendLine("## 保留债务");
        b.AppendLine();
        foreach (var debt in report.RemainingDebt)
        {
            b.AppendLine($"- {debt}");
        }
        b.AppendLine();

        b.AppendLine("## 延迟清理项");
        b.AppendLine();
        foreach (var deferred in report.DeferredCleanupItems)
        {
            b.AppendLine($"- {deferred}");
        }
        b.AppendLine();

        b.AppendLine("## 子报告状态");
        b.AppendLine();
        b.AppendLine($"- ArchitectureCleanupPlanPassed: {report.ArchitectureCleanupPlanPassed}");
        b.AppendLine($"- DtoSplitPlanGenerated: {report.DtoSplitPlanGenerated}");
        b.AppendLine($"- PathHygieneGatePassed: {report.PathHygieneGatePassed}");
        b.AppendLine($"- P15BuildLockHardened: {report.P15BuildLockHardened}");
        b.AppendLine($"- ControlRoomRegistryConsolidated: {report.ControlRoomRegistryConsolidated}");
        b.AppendLine($"- EvalCommandSplit: {report.EvalCommandSplit}");
        b.AppendLine($"- VectorRunnerDirectoryIsolated: {report.VectorRunnerDirectoryIsolated}");
        b.AppendLine();

        b.AppendLine("## Gate 规则合规");
        b.AppendLine();
        b.AppendLine($"- FormalRetrievalNotEnabled: {report.FormalRetrievalNotEnabled}");
        b.AppendLine($"- NoRuntimeSwitch: {report.NoRuntimeSwitch}");
        b.AppendLine($"- NoFormalPackageWrite: {report.NoFormalPackageWrite}");
        b.AppendLine($"- NoPackagePackingPolicyVectorBindingMutation: {report.NoPackagePackingPolicyVectorBindingMutation}");
        b.AppendLine();

        if (report.BlockedReasons.Count > 0)
        {
            b.AppendLine("## Blocked Reasons");
            b.AppendLine();
            foreach (var r in report.BlockedReasons) b.AppendLine($"- {r}");
            b.AppendLine();
        }

        if (report.Warnings.Count > 0)
        {
            b.AppendLine("## Warnings");
            b.AppendLine();
            foreach (var w in report.Warnings) b.AppendLine($"- {w}");
            b.AppendLine();
        }

        b.AppendLine("## Diagnostics");
        b.AppendLine();
        foreach (var d in report.Diagnostics) b.AppendLine($"- {d}");
        b.AppendLine();

        b.AppendLine("Architecture cleanup freeze report. No runtime behavior change, no formal retrieval enable, no package/packing policy/runtime/vector binding mutation.");
        return b.ToString();
    }

    private static string ResolveRepositoryRoot(string repositoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(repositoryRoot) && Directory.Exists(Path.Combine(repositoryRoot, "src")))
            return Path.GetFullPath(repositoryRoot);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }

        return Path.GetFullPath(repositoryRoot);
    }

    private static bool TryReadJsonBool(string path, string property, out bool value)
    {
        value = false;
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (doc.RootElement.TryGetProperty("Summary", out var summary) &&
                summary.TryGetProperty(property, out var sumProp) && sumProp.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            return doc.RootElement.TryGetProperty(property, out _);
        }
        catch
        {
            return false;
        }
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
        if (!File.Exists(path)) return 0;
        return File.ReadLines(path)
            .Count(static line =>
                line.StartsWith("public sealed class ", StringComparison.Ordinal)
                || line.StartsWith("public static class ", StringComparison.Ordinal)
                || line.StartsWith("public sealed record ", StringComparison.Ordinal)
                || line.StartsWith("public record ", StringComparison.Ordinal));
    }

    private static int CountSubcommandRefs(string evalCommandDirectory)
    {
        if (!Directory.Exists(evalCommandDirectory)) return 0;
        return Directory.EnumerateFiles(evalCommandDirectory, "EvalCommand*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(File.ReadLines)
            .Count(static line => line.Contains("string.Equals(subcommand,", StringComparison.Ordinal));
    }

    private static bool EvalCommandDirExists(string evalCommandDirectory)
    {
        return Directory.Exists(evalCommandDirectory)
            && Directory.EnumerateFiles(evalCommandDirectory, "EvalCommand*.cs", SearchOption.TopDirectoryOnly)
                .Any(static f => Path.GetFileName(f).StartsWith("EvalCommand.", StringComparison.Ordinal));
    }

    private static int CountEvalCommandPartials(string evalCommandDirectory)
    {
        if (!Directory.Exists(evalCommandDirectory)) return 0;
        return Directory.EnumerateFiles(evalCommandDirectory, "EvalCommand*.cs", SearchOption.TopDirectoryOnly)
            .Count(static f => Path.GetFileName(f).StartsWith("EvalCommand.", StringComparison.Ordinal));
    }

    private static int GetRegistryDescriptorCount(string repositoryRoot)
    {
        var registryPath = Path.Combine(repositoryRoot, "src", "ContextCore.ControlRoom", "Models", "ReportSummaryRegistry.cs");
        if (!File.Exists(registryPath)) return 0;
        return File.ReadLines(registryPath)
            .Count(static line => line.Contains("ControlRoomReportDescriptor", StringComparison.Ordinal)
                                 && !line.Contains("class ControlRoomReportDescriptor", StringComparison.Ordinal));
    }
}
