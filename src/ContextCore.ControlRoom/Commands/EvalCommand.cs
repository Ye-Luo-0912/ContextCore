using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;
using ContextCore.Embedding;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.ControlRoom.Commands;

/// <summary>执行上下文评测并生成报告的命令。</summary>
public static class EvalCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var subcommand = args.Count > 0 ? args[0] : string.Empty;
        if (!string.Equals(subcommand, "run", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "report", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "perf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "perf-scale", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "attention-profile-selection", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "guarded-rerank-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "guarded-order-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "guarded-profile-sweep", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "planning-shadow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "planning-shadow-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "planning-shadow-recall-loss", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "planning-optin-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "planning-optin-fallback-analysis", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "planning-optin-constraint-safety", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "extended-failure-triage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "export-learning-features", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-dataset-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-baseline", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-baseline-router", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-baseline-ranker", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-ranker-ablation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-ranker-weight-sweep", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-ranker-residual-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-hard-negatives", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-lifecycle-aware-ranker", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "lifecycle-ranker-shadow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "ranker-shadow-trace-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "storage-check", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(subcommand, "chunk-ablation", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(subcommand, "idle-unload", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(subcommand, "fs-vector-perf", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("eval supports:\n  eval run [--category <name>] [--include-batches] [--out <path>]\n  eval report [<path>]\n  eval attention-profile-selection [--baseline <path>] [--extended <path>] [--out <path.json>] [--md-out <path.md>]\n  eval guarded-rerank-comparison [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]\n  eval guarded-order-quality [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]\n  eval guarded-profile-sweep [--category <name>] [--include-batches] [--out <path.json>]\n  eval planning-shadow [--category <name>] [--include-batches] [--out <path.json>] [--triage-out <path.json>]\n  eval planning-shadow-quality [--category <name>] [--include-batches] [--out <path.json>]\n  eval planning-shadow-recall-loss [--category <name>] [--include-batches] [--out <path.json>]\n  eval planning-optin-comparison [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--out <path.json>]\n  eval planning-optin-fallback-analysis [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--candidate-intents <csv>] [--out <path.json>]\n  eval planning-optin-constraint-safety [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--candidate-intents <csv>] [--out <path.json>]\n  eval extended-failure-triage [--input <eval-report.json>] [--out <path.json>] [--md-out <path.md>]\n  eval export-learning-features [--out-dir <dir>] [--workspace <id>] [--collection <id>] [--eval-reports <csv>] [--planning-shadow-reports <csv>]\n  eval learning-dataset-quality [--features-dir <dir>] [--out <path.json>] [--md-out <path.md>]\n  eval learning-baseline --task router|ranker [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-baseline-router [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-baseline-ranker [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-ranker-ablation [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-ranker-weight-sweep [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-ranker-residual-audit [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-hard-negatives [--residual-audit <path>] [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-lifecycle-aware-ranker [--features-dir <dir>] [--out-dir <dir>]\n  eval lifecycle-ranker-shadow [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]\n  eval ranker-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--out <path.json>] [--md-out <path.md>]\n  eval learning-ranker-analysis [--features-dir <dir>] [--out-dir <dir>]\n  eval perf [--out <path.json>]\n  eval perf-scale [--size 1000] [--fake-vectors] [--out <path.json>]\n  eval retrieval [--out <path.json>]\n  eval storage-check\n  eval chunk-ablation\n  eval idle-unload\n  eval fs-vector-perf [--size 1000]");
            return;
        }

        if (string.Equals(subcommand, "chunk-ablation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteChunkAblationAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "idle-unload", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteIdleUnloadAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "fs-vector-perf", StringComparison.OrdinalIgnoreCase))
        {
            var fsSize = 1000;
            var fsSizeArg = CommandHelpers.GetOption(args, "--size") ?? CommandHelpers.GetOption(args, "-n");
            if (!string.IsNullOrEmpty(fsSizeArg) && int.TryParse(fsSizeArg, out var parsedFsSize) && parsedFsSize > 0)
                fsSize = parsedFsSize;
            await ExecuteFsVectorPerfAsync(fsSize, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "storage-check", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteStorageCheckAsync(service, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "perf", StringComparison.OrdinalIgnoreCase))
        {
            var perfOutputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o");
            await ExecutePerfAsync(perfOutputPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "perf-scale", StringComparison.OrdinalIgnoreCase))
        {
            var scaleSize = 1000;
            var sizeArg = CommandHelpers.GetOption(args, "--size") ?? CommandHelpers.GetOption(args, "-n");
            if (!string.IsNullOrEmpty(sizeArg) && int.TryParse(sizeArg, out var parsedSize) && parsedSize > 0)
            {
                scaleSize = parsedSize;
            }
            var fakeVectors = args.Contains("--fake-vectors", StringComparer.OrdinalIgnoreCase);
            var scaleOutputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o");
            await ExecutePerfScaleAsync(scaleSize, fakeVectors, scaleOutputPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval", StringComparison.OrdinalIgnoreCase))
        {
            var retrievalOutputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "eval-retrieval-report.json");
            await ExecuteRetrievalAsync(retrievalOutputPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "attention-profile-selection", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAttentionProfileSelectionAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "guarded-rerank-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedRerankComparisonAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "guarded-order-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedOrderQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "guarded-profile-sweep", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedProfileSweepAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "planning-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "planning-shadow-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "planning-shadow-recall-loss", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowRecallLossAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "planning-optin-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningOptInComparisonAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "planning-optin-fallback-analysis", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningOptInFallbackAnalysisAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "planning-optin-constraint-safety", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningOptInConstraintSafetyAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "extended-failure-triage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExtendedFailureTriageAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "export-learning-features", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExportLearningFeaturesAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-dataset-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningDatasetQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-baseline", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-baseline-router", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-baseline-ranker", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningBaselineAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-ranker-ablation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-weight-sweep", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-residual-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-hard-negatives", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-lifecycle-aware-ranker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningRankerAnalysisAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "lifecycle-ranker-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLifecycleRankerShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "ranker-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRankerShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "report", StringComparison.OrdinalIgnoreCase))
        {
            string? reportPath = null;
            if (args.Count >= 2)
            {
                reportPath = args[1];
            }
            else
            {
                // Auto-detect latest report
                var currentDir = Directory.GetCurrentDirectory();
                var candidatePaths = new List<string>
                {
                    Path.Combine(currentDir, "eval-report-latest.json"),
                    Path.Combine(currentDir, "eval", "eval-report-latest.json")
                };

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        reportPath = path;
                        break;
                    }
                }

                if (reportPath == null)
                {
                    var files = new DirectoryInfo(currentDir).GetFiles("eval-report*.json", SearchOption.AllDirectories)
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .ToList();
                    if (files.Count > 0)
                    {
                        reportPath = files[0].FullName;
                    }
                }
            }

            if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
            {
                Console.Error.WriteLine("Error: 未找到任何评测报告文件。用法: eval report [<path>]");
                return;
            }

            Console.WriteLine($"[Eval] 正在加载并显示报告: {reportPath}");
            await DisplayLocalReportAsync(reportPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // eval run
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);

        var contextsRoot = ResolveContextsRoot();
        if (!Directory.Exists(contextsRoot))
        {
            Console.Error.WriteLine($"Error: 评测数据根目录不存在: {contextsRoot}");
            return;
        }

        Console.WriteLine($"[Eval] 开始在目录 {contextsRoot} 执行评测...");
        if (categoryFilter is not null)
        {
            Console.WriteLine($"[Eval] 过滤分类: {categoryFilter}");
        }
        if (includeSeedBatches)
        {
            Console.WriteLine("[Eval] 已启用扩展批次：将读取 seed*.json 与 corpus*.json。");
        }

        var runner = new ContextEvalRunner();
        var report = await runner.RunAsync(contextsRoot, categoryFilter, includeSeedBatches).ConfigureAwait(false);

        // 渲染屏幕展示
        RenderReportToConsole(report);

        // Always save json log to latest path
        var defaultLatestPath = Path.Combine(Directory.GetCurrentDirectory(), "eval", "eval-report-latest.json");
        await ExportReportAsync(report, defaultLatestPath, cancellationToken).ConfigureAwait(false);

        // 写入输出文件
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            await ExportReportAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        }

        if (includeSeedBatches)
        {
            await ExportExtendedFailureTriageAsync(
                    report,
                    Path.Combine(Directory.GetCurrentDirectory(), "eval", "extended-failure-triage-report.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "eval", "extended-failure-triage-report.md"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string ResolveContextsRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            var target = Path.Combine(current, "eval", "contexts");
            if (Directory.Exists(target))
            {
                return target;
            }
            current = Path.GetDirectoryName(current);
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "eval", "contexts");
    }

    private static async Task ExecuteLifecycleRankerShadowAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var profile = CommandHelpers.GetOption(args, "--profile")
            ?? LifecycleAwareRankerShadowScorer.DefaultProfile;
        var defaultFileName = includeSeedBatches
            ? "lifecycle-aware-ranker-shadow-report-extended.json"
            : "lifecycle-aware-ranker-shadow-report-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "learning", "baselines", defaultFileName);

        var runner = new ContextEvalRunner();
        var evalReport = await runner.RunAsync(contextsRoot, categoryFilter, includeSeedBatches).ConfigureAwait(false);
        var report = LifecycleAwareRankerShadowReportBuilder.Build(evalReport, includeSeedBatches, profile);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Lifecycle-aware ranker shadow report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; formalChanged={report.FormalOutputChanged}; selectedSetChanged={report.SelectedSetChanged}; lifecycleViolations={report.LifecycleViolationCount}");
        Console.WriteLine($"[Eval] deprecatedDemotions={report.DeprecatedNoiseDemotedCount}; versionConflictFixes={report.VersionConflictFixedCount}; mustHitDemotions={report.MustHitDemotedCount}; mustNotHitPromotions={report.MustNotHitPromotedCount}");
        Console.WriteLine($"[Eval] potentialMrrDelta={report.PotentialMRRDelta:F4}; potentialPairwiseWinRate={report.PotentialPairwiseWinRate:P2}");
    }

    private static async Task ExecuteRankerShadowTraceQualityAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var workspaceId = CommandHelpers.GetOption(args, "--workspace")
            ?? service.State.WorkspaceId;
        var collectionId = CommandHelpers.GetOption(args, "--collection")
            ?? service.State.CollectionId;
        var take = 200;
        var takeArg = CommandHelpers.GetOption(args, "--take");
        if (!string.IsNullOrWhiteSpace(takeArg) && int.TryParse(takeArg, out var parsedTake) && parsedTake > 0)
        {
            take = parsedTake;
        }

        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, "learning", "baselines", "ranker-shadow-trace-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "learning", "baselines", "ranker-shadow-trace-quality-report.md");

        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records;
        if (service.State.IsServiceMode && service.State.ServiceClient is not null)
        {
            records = await service.State.ServiceClient
                .GetRankerShadowTracesAsync(workspaceId, collectionId, take, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            records = await new RankerShadowTraceExportService(service.State.RetrievalTraceStore)
                .QueryAsync(workspaceId, collectionId, take, cancellationToken)
                .ConfigureAwait(false);
        }

        var builder = new RankerShadowTraceQualityReportBuilder();
        var report = builder.Build(records, workspaceId, collectionId);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RankerShadowTraceQualityReportBuilder.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Ranker shadow trace quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Ranker shadow trace quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Traces={report.TraceCount}; candidates={report.CandidateScoreCount}; deprecated={report.DeprecatedDemotionCount}; historical={report.HistoricalDemotionCount}; versionFixes={report.VersionConflictFixCount}");
        Console.WriteLine($"[Eval] Risks: mustHitDemoted={report.MustHitDemotedCount}; mustNotHitPromoted={report.MustNotHitPromotedCount}; next={report.RecommendedNextStep}");
    }

    private static async Task ExecuteAttentionProfileSelectionAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var baselinePath = CommandHelpers.GetOption(args, "--baseline")
            ?? Path.Combine(current, "eval", "eval-report-attention-phase3-baseline.json");
        var extendedPath = CommandHelpers.GetOption(args, "--extended")
            ?? Path.Combine(current, "eval", "eval-report-attention-phase3-extended.json");
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", "attention-profile-selection-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "docs", "attention-profile-selection-report.md");

        var runner = new AttentionProfileSelectionRunner();
        var report = await runner.GenerateAsync(baselinePath, extendedPath, cancellationToken).ConfigureAwait(false);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(AttentionProfileSelectionRunner.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Attention profile selection report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Attention profile selection markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] RecommendedProfile={report.RecommendedProfile}; mode={report.RecommendedMode}; risk={report.RiskLevel}; blocking={string.Join(",", report.BlockingIssues)}");
    }

    private static async Task ExecuteGuardedRerankComparisonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? "old-score-anchored-v1-strong";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", "guarded-attention-rerank-comparison-report.json");

        var runner = new ContextEvalRunner(new RetrievalAttentionRerankOptions
        {
            Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
            Profile = profileId,
            PreserveSelectedSet = true,
            AllowSelectedSetMutation = false,
            EmitShadowTrace = true
        });
        var evalReport = await runner.RunAsync(contextsRoot, categoryFilter, includeSeedBatches).ConfigureAwait(false);
        var report = GuardedAttentionRerankReportBuilder.Build(
            evalReport,
            RetrievalAttentionRerankOptions.ApplyGuardedMode,
            profileId);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Guarded rerank comparison report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; applied={report.AppliedSamples}; skipped={report.SkippedSamples}; blocked={report.BlockedSamples}; selectedSetChanges={report.SelectedSetChangeCount}");
    }

    private static async Task ExecuteGuardedOrderQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? "old-score-anchored-v1-strong";
        var defaultFileName = includeSeedBatches
            ? "guarded-attention-order-quality-report-extended.json"
            : "guarded-attention-order-quality-report-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);

        var runner = new ContextEvalRunner(new RetrievalAttentionRerankOptions
        {
            Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
            Profile = profileId,
            PreserveSelectedSet = true,
            AllowSelectedSetMutation = false,
            EmitShadowTrace = true
        });
        var evalReport = await runner.RunAsync(contextsRoot, categoryFilter, includeSeedBatches).ConfigureAwait(false);
        var report = GuardedAttentionOrderQualityReportBuilder.Build(
            evalReport,
            RetrievalAttentionRerankOptions.ApplyGuardedMode,
            profileId);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Guarded order quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; applied={report.AppliedSamples}; selectedSetDiff={report.SelectedSetDiffCount}; orderMRR={report.Baseline.SelectedOrderMRR:F4}->{report.Reranked.SelectedOrderMRR:F4}; safety={report.SafetyGates.Count(gate => gate.Passed)}/{report.SafetyGates.Count}; sorting={report.SortingGates.Count(gate => gate.Passed)}/{report.SortingGates.Count}");
    }

    private static async Task ExecuteGuardedProfileSweepAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "guarded-attention-profile-sweep-extended.json"
            : "guarded-attention-profile-sweep-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);

        var entries = new List<(ContextAttentionProfile Profile, GuardedAttentionOrderQualityReport OrderReport)>();
        foreach (var profile in ContextAttentionProfile.CreateGuardedRerankSweepProfiles())
        {
            var runner = new ContextEvalRunner(new RetrievalAttentionRerankOptions
            {
                Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
                Profile = profile.ProfileId,
                PreserveSelectedSet = true,
                AllowSelectedSetMutation = false,
                EmitShadowTrace = true
            });
            var evalReport = await runner.RunAsync(contextsRoot, categoryFilter, includeSeedBatches).ConfigureAwait(false);
            var orderReport = GuardedAttentionOrderQualityReportBuilder.Build(
                evalReport,
                RetrievalAttentionRerankOptions.ApplyGuardedMode,
                profile.ProfileId);
            entries.Add((profile, orderReport));

            Console.WriteLine($"[Eval] Sweep {profile.ProfileId}: samples={orderReport.TotalSamples}; selectedSetDiff={orderReport.SelectedSetDiffCount}; added/dropped={orderReport.AddedItems}/{orderReport.DroppedItems}; orderMRR={orderReport.Reranked.SelectedOrderMRR:F4}; safety={orderReport.SafetyGates.All(gate => gate.Passed)}; sorting={orderReport.SortingGates.All(gate => gate.Passed)}");
        }

        var report = GuardedAttentionProfileSweepReportBuilder.Build(
            entries,
            RetrievalAttentionRerankOptions.ApplyGuardedMode,
            includeSeedBatches);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Guarded profile sweep report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Profiles={report.Profiles.Count}; samples={report.TotalSamples}; allSafety={report.Profiles.All(profile => profile.SafetyGatePassed)}; allSorting={report.Profiles.All(profile => profile.SortingGatePassed)}");
    }

    private static async Task ExecutePlanningShadowAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "planning-shadow-comparison-extended.json"
            : "planning-shadow-comparison-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);
        var triageDefaultFileName = includeSeedBatches
            ? "planning-shadow-diff-triage-extended.json"
            : "planning-shadow-diff-triage-a3.json";
        var triageOutputPath = CommandHelpers.GetOption(args, "--triage-out")
            ?? Path.Combine(current, "eval", triageDefaultFileName);

        var runner = new PlanningShadowEvalRunner();
        var report = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches, cancellationToken)
            .ConfigureAwait(false);
        var triageReport = PlanningShadowDiffTriageReportBuilder.Build(report);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(triageReport, triageOutputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Planning shadow comparison report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Planning shadow diff triage report: {Path.GetFullPath(triageOutputPath)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; selectedSetDiffSamples={report.SelectedSetDiffCount}; added/dropped={report.AddedItemCount}/{report.DroppedItemCount}; mustNotHitViolations={report.MustNotHitViolationCount}; lifecycleViolations={report.LifecycleViolationCount}");
        Console.WriteLine($"[Eval] Plans native/repaired/fallback={report.NativeValidPlanCount}/{report.RepairedPlanCount}/{report.FallbackPlanCount}; nativeRate={report.NativeValidRate:P1}; finalTopKClamp={report.FinalTopKClampCount}; vectorDisabled={report.VectorDisabledCount}; deprecatedBlocked={report.DeprecatedBlockedCount}");
    }

    private static async Task ExecutePlanningShadowQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "planning-shadow-quality-report-extended.json"
            : "planning-shadow-quality-report-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);

        var runner = new PlanningShadowEvalRunner();
        var comparison = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches, cancellationToken)
            .ConfigureAwait(false);
        var report = PlanningShadowQualityReportBuilder.Build(comparison);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Planning shadow quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; passDelta={report.Global.PassRateDelta:P1}; recall10Delta={report.Global.Recall10Delta:P1}; mrrDelta={report.Global.MrrDelta:F4}; mustNotHitDelta={report.Global.MustNotHitViolationDelta}; lifecycle={report.Global.LifecycleViolationCount}");
        Console.WriteLine($"[Eval] Recommendation optIn={string.Join(",", report.Recommendation.OptInCandidateIntents)}; tuning={string.Join(",", report.Recommendation.NeedsTuningIntents)}; blocked={string.Join(",", report.Recommendation.BlockedIntents)}");
    }

    private static async Task ExecutePlanningShadowRecallLossAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "planning-shadow-recall-loss-report-extended.json"
            : "planning-shadow-recall-loss-report-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);

        var runner = new PlanningShadowEvalRunner();
        var comparison = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches, cancellationToken)
            .ConfigureAwait(false);
        var report = PlanningShadowRecallLossReportBuilder.Build(comparison);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Planning shadow recall loss report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; degraded={report.DegradedSampleCount}; mustHitLost={report.MustHitLostCount}; reasons={string.Join(", ", report.SuspectedLossReasonCounts.Select(item => $"{item.Key}:{item.Value}"))}");
    }

    private static async Task ExecutePlanningOptInComparisonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "planning-optin-comparison-extended.json"
            : "planning-optin-comparison-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);
        var optInIntents = ParseCsvOption(CommandHelpers.GetOption(args, "--opt-in-intents"));

        var runner = new PlanningShadowEvalRunner();
        var report = await runner
            .RunOptInAsync(contextsRoot, optInIntents, categoryFilter, includeSeedBatches, cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        var fallbackUsedCount = report.Samples.Count(sample =>
            sample.Diagnostics.TryGetValue("planningFallbackUsed", out var fallback)
            && bool.TryParse(fallback, out var parsed)
            && parsed);
        var appliedCount = report.Samples.Count(sample =>
            sample.Diagnostics.TryGetValue("planningExecutionStatus", out var status)
            && string.Equals(status, RetrievalPlanningOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"[Eval] Planning opt-in comparison report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] OptInIntents={string.Join(",", optInIntents)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; applied={appliedCount}; fallbackUsed={fallbackUsedCount}; selectedSetDiffSamples={report.SelectedSetDiffCount}; mustNotHitViolations={report.MustNotHitViolationCount}; lifecycleViolations={report.LifecycleViolationCount}");
    }

    private static async Task ExecutePlanningOptInFallbackAnalysisAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "planning-optin-fallback-analysis-extended.json"
            : "planning-optin-fallback-analysis-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);
        var currentOptInIntents = ParseCsvOption(CommandHelpers.GetOption(args, "--opt-in-intents"));
        if (currentOptInIntents.Count == 0)
        {
            currentOptInIntents =
            [
                PlanningIntentDetector.CurrentTask,
                PlanningIntentDetector.AutomationRecovery
            ];
        }

        var candidateIntents = ParseCsvOption(CommandHelpers.GetOption(args, "--candidate-intents"));
        if (candidateIntents.Count == 0)
        {
            candidateIntents =
            [
                PlanningIntentDetector.CodingTask,
                PlanningIntentDetector.LongTermPreference
            ];
        }

        var evaluationIntents = currentOptInIntents
            .Concat(candidateIntents)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var runner = new PlanningShadowEvalRunner();
        var comparison = await runner
            .RunOptInAsync(contextsRoot, evaluationIntents, categoryFilter, includeSeedBatches, cancellationToken)
            .ConfigureAwait(false);
        var report = PlanningOptInFallbackAnalysisReportBuilder.Build(
            comparison,
            currentOptInIntents,
            candidateIntents);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Planning opt-in fallback analysis report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] CurrentOptIn={string.Join(",", currentOptInIntents)}; CandidateIntents={string.Join(",", candidateIntents)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; keep={string.Join(",", report.Recommendation.KeepOptIn)}; expand={string.Join(",", report.Recommendation.ExpandCandidate)}; tuning={string.Join(",", report.Recommendation.NeedsPolicyTuning)}; blocked={string.Join(",", report.Recommendation.Blocked)}");
    }

    private static async Task ExecutePlanningOptInConstraintSafetyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = args.Contains("--include-batches", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--all-seeds", StringComparer.OrdinalIgnoreCase);
        var defaultFileName = includeSeedBatches
            ? "planning-optin-constraint-safety-report-extended.json"
            : "planning-optin-constraint-safety-report-a3.json";
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", defaultFileName);
        var currentOptInIntents = ParseCsvOption(CommandHelpers.GetOption(args, "--opt-in-intents"));
        if (currentOptInIntents.Count == 0)
        {
            currentOptInIntents =
            [
                PlanningIntentDetector.CurrentTask,
                PlanningIntentDetector.AutomationRecovery
            ];
        }

        var candidateIntents = ParseCsvOption(CommandHelpers.GetOption(args, "--candidate-intents"));
        if (candidateIntents.Count == 0)
        {
            candidateIntents =
            [
                PlanningIntentDetector.CodingTask,
                PlanningIntentDetector.LongTermPreference
            ];
        }

        var evaluationIntents = currentOptInIntents
            .Concat(candidateIntents)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var runner = new PlanningShadowEvalRunner();
        var comparison = await runner
            .RunOptInAsync(contextsRoot, evaluationIntents, categoryFilter, includeSeedBatches, cancellationToken)
            .ConfigureAwait(false);
        var report = PlanningOptInConstraintSafetyReportBuilder.Build(comparison);

        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Planning opt-in constraint safety report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] CurrentOptIn={string.Join(",", currentOptInIntents)}; CandidateIntents={string.Join(",", candidateIntents)}");
        Console.WriteLine($"[Eval] Samples={report.TotalSamples}; affected={report.AffectedSampleCount}; fallback={report.FallbackSampleCount}; repaired={report.ConstraintRepairedCount}; repairFailed={report.ConstraintRepairFailedCount}; droppedByBudget={report.ConstraintDroppedByBudgetCount}; wrongSection={report.ConstraintWrongSectionCount}");
    }

    private static async Task ExecuteExtendedFailureTriageAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var inputPath = CommandHelpers.GetOption(args, "--input")
            ?? CommandHelpers.GetOption(args, "-i")
            ?? Path.Combine(current, "eval", "eval-report-latest.json");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "eval", "extended-failure-triage-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "eval", "extended-failure-triage-report.md");

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: eval report not found: {inputPath}");
            return;
        }

        var json = await File.ReadAllTextAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var evalReport = JsonSerializer.Deserialize<ContextEvalReport>(json, JsonOptions);
        if (evalReport is null)
        {
            Console.Error.WriteLine($"Error: eval report deserialize failed: {inputPath}");
            return;
        }

        await ExportExtendedFailureTriageAsync(evalReport, outputPath, markdownPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ExportExtendedFailureTriageAsync(
        ContextEvalReport evalReport,
        string outputPath,
        string markdownPath,
        CancellationToken cancellationToken)
    {
        var report = ExtendedFailureTriageReportBuilder.Build(evalReport);
        await WriteJsonAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ExtendedFailureTriageReportBuilder.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Extended failure triage report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Extended failure triage markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Failed={report.FailedSamples}; categories={string.Join(", ", report.CategoryCounts.Select(item => $"{item.Key}:{item.Value}"))}");
    }

    private static async Task ExecuteExportLearningFeaturesAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "learning", "features");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace")
            ?? service.State.WorkspaceId;
        var collectionId = CommandHelpers.GetOption(args, "--collection")
            ?? service.State.CollectionId;
        var sessionId = CommandHelpers.GetOption(args, "--session");
        var evalReports = ParseCsvOption(CommandHelpers.GetOption(args, "--eval-reports"));
        var planningShadowReports = ParseCsvOption(CommandHelpers.GetOption(args, "--planning-shadow-reports"));

        var policyFeedbackService = CreatePolicyFeedbackDatasetServiceForEval(service);
        var featureService = new LearningFeatureDatasetService(policyFeedbackService, new PlanningIntentDetector());
        var result = await featureService.ExportAsync(
            workspaceId,
            collectionId,
            sessionId,
            outputDirectory,
            evalReports.Count == 0 ? null : evalReports,
            planningShadowReports.Count == 0 ? null : planningShadowReports,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning feature dataset exported: {result.OutputDirectory}");
        Console.WriteLine($"[Eval] Policy feedback features: {result.FeatureCount} -> {result.PolicyFeedbackFeaturesPath}");
        Console.WriteLine($"[Eval] Ranking pairs: {result.RankingPairCount} -> {result.RankingPairsPath}");
        Console.WriteLine($"[Eval] Router intent examples: {result.RouterIntentExampleCount} -> {result.RouterIntentExamplesPath}");
    }

    private static async Task ExecuteLearningDatasetQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, "learning", "features", "dataset-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "learning", "features", "dataset-quality-report.md");

        var builder = new LearningDatasetQualityReportBuilder();
        var report = await builder.BuildAsync(featuresDirectory, cancellationToken).ConfigureAwait(false);
        await builder.WriteAsync(report, outputPath, markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning dataset quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Learning dataset quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Counts: policy={report.PolicyFeedbackFeatureCount}, rankingPairs={report.RankingPairCount}, routerIntent={report.RouterIntentExampleCount}");
        Console.WriteLine($"[Eval] Risks: {(report.DataRisks.Count == 0 ? "-" : string.Join(", ", report.DataRisks))}");
        Console.WriteLine($"[Eval] Next: {report.RecommendedNextAction}");
    }

    private static async Task ExecuteLearningBaselineAsync(
        string subcommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "learning", "baselines");
        var task = CommandHelpers.GetOption(args, "--task")
            ?? (string.Equals(subcommand, "learning-baseline-router", StringComparison.OrdinalIgnoreCase)
                ? "router"
                : string.Equals(subcommand, "learning-baseline-ranker", StringComparison.OrdinalIgnoreCase)
                    ? "ranker"
                    : "all");

        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var runner = new LearningOfflineBaselineRunner();

        if (string.Equals(task, "router", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
        {
            var inputPath = CommandHelpers.GetOption(args, "--router-input")
                ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
            var jsonPath = Path.Combine(outputDirectory, "router-intent-baseline-report.json");
            var markdownPath = Path.Combine(outputDirectory, "router-intent-baseline-report.md");
            var report = await runner.RunRouterAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Router baseline report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Router baseline markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Router status={report.Status}; samples={report.SampleCount}; best={report.BestBaseline}");
            foreach (var baseline in report.Baselines)
            {
                Console.WriteLine($"[Eval] Router {baseline.BaselineName}: accuracy={baseline.Accuracy:P2}, macroF1={baseline.MacroF1:0.####}");
            }
        }

        if (string.Equals(task, "ranker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
        {
            var inputPath = CommandHelpers.GetOption(args, "--ranker-input")
                ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName);
            var jsonPath = Path.Combine(outputDirectory, "ranker-baseline-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-baseline-report.md");
            var report = await runner.RunRankerAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Ranker baseline report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker baseline markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker status={report.Status}; pairs={report.PairCount}; best={report.BestBaseline}");
            foreach (var baseline in report.Baselines)
            {
                Console.WriteLine($"[Eval] Ranker {baseline.BaselineName}: pairwiseAccuracy={baseline.PairwiseAccuracy:P2}, fpr={baseline.FalsePositiveRate:P2}, fnr={baseline.FalseNegativeRate:P2}");
            }
        }

        if (!string.Equals(task, "router", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(task, "ranker", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: unsupported learning baseline task '{task}'. Expected router, ranker, or all.");
        }
    }

    private static async Task ExecuteLearningRankerAnalysisAsync(
        string subcommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, "learning", "baselines");
        var inputPath = CommandHelpers.GetOption(args, "--ranker-input")
            ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName);

        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var runner = new LearningOfflineBaselineRunner();

        if (string.Equals(subcommand, "learning-ranker-ablation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "ranker-ablation-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-ablation-report.md");
            var report = await runner.RunRankerAblationAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Ranker ablation report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker ablation markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker ablation status={report.Status}; pairs={report.PairCount}; baseline={report.Baseline.PairwiseAccuracy:P2}");
            foreach (var ablation in report.Ablations.OrderBy(item => item.DisabledFeature, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Eval] Ablation {ablation.DisabledFeature}: pairwiseAccuracy={ablation.PairwiseAccuracy:P2}, delta={ablation.AccuracyDelta:P2}");
            }
        }

        if (string.Equals(subcommand, "learning-ranker-weight-sweep", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "ranker-weight-sweep-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-weight-sweep-report.md");
            var report = await runner.RunRankerWeightSweepAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Ranker weight sweep report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker weight sweep markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker weight sweep status={report.Status}; pairs={report.PairCount}; baseline={report.Baseline.PairwiseAccuracy:P2}; best={report.BestResult.ConfigurationId} {report.BestResult.PairwiseAccuracy:P2}");
        }

        if (string.Equals(subcommand, "learning-ranker-residual-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "ranker-residual-audit-report.json");
            var markdownPath = Path.Combine(outputDirectory, "ranker-residual-audit-report.md");
            var report = await runner.RunRankerResidualAuditAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Ranker residual audit report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Ranker residual audit markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Ranker residual audit status={report.Status}; pairs={report.PairCount}; failures={report.Failures.Count}; clusters={(report.FailureClusters.Count == 0 ? "-" : string.Join(", ", report.FailureClusters.Select(item => $"{item.Cluster}:{item.Count}")))}");
        }

        if (string.Equals(subcommand, "learning-hard-negatives", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var residualAuditPath = CommandHelpers.GetOption(args, "--residual-audit")
                ?? Path.Combine(outputDirectory, "ranker-residual-audit-report.json");
            var jsonLinesPath = Path.Combine(featuresDirectory, "hard-negatives.jsonl");
            var jsonPath = Path.Combine(outputDirectory, "hard-negative-report.json");
            var markdownPath = Path.Combine(outputDirectory, "hard-negative-report.md");
            var report = await runner.RunHardNegativeGenerationAsync(
                    residualAuditPath,
                    jsonLinesPath,
                    jsonPath,
                    markdownPath,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Hard negative dataset: {Path.GetFullPath(jsonLinesPath)}");
            Console.WriteLine($"[Eval] Hard negative report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Hard negative markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Hard negative status={report.Status}; failures={report.SourceFailureCount}; examples={report.ExampleCount}; types={(report.TypeCounts.Count == 0 ? "-" : string.Join(", ", report.TypeCounts.Select(item => $"{item.Key}:{item.Value}")))}");
        }

        if (string.Equals(subcommand, "learning-lifecycle-aware-ranker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "lifecycle-aware-ranker-report.json");
            var markdownPath = Path.Combine(outputDirectory, "lifecycle-aware-ranker-report.md");
            var report = await runner.RunLifecycleAwareRankerAsync(inputPath, jsonPath, markdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Lifecycle-aware ranker report: {Path.GetFullPath(jsonPath)}");
            Console.WriteLine($"[Eval] Lifecycle-aware ranker markdown: {Path.GetFullPath(markdownPath)}");
            Console.WriteLine($"[Eval] Lifecycle-aware status={report.Status}; pairs={report.PairCount}; best={report.BestBaseline}; targetPassed={report.TargetPassed}");
            foreach (var baseline in report.Baselines)
            {
                Console.WriteLine($"[Eval] Lifecycle {baseline.BaselineName}: pairwiseAccuracy={baseline.PairwiseAccuracy:P2}, residual={baseline.ResidualFailures}, deprecatedNoise={baseline.DeprecatedNoiseFailures}, fpr={baseline.FalsePositiveRate:P2}, fnr={baseline.FalseNegativeRate:P2}");
            }
        }
    }

    private static PolicyFeedbackDatasetService? CreatePolicyFeedbackDatasetServiceForEval(ControlRoomService service)
    {
        if (service.State.IsServiceMode || string.IsNullOrWhiteSpace(service.State.RootPath))
        {
            return null;
        }

        if (!string.Equals(service.State.StorageKind, "filesystem", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var options = new FileStorageOptions { RootPath = service.State.RootPath };
        var paths = new FilePathResolver(options);
        var serializer = new FileFormatSerializer();
        return new PolicyFeedbackDatasetService(
            new FileShortTermPromotionCandidateStore(paths, serializer),
            new FileStableReviewCandidateStore(paths, serializer),
            new FileConstraintGapCandidateStore(paths, serializer),
            new FileCandidateConstraintReviewStore(paths, serializer),
            new FileConstraintStore(paths, serializer));
    }

    private static IReadOnlyList<string> ParseCsvOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task WriteJsonAsync(
        AttentionProfileSelectionReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        GuardedAttentionRerankEvalReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        GuardedAttentionOrderQualityReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        GuardedAttentionProfileSweepReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        ExtendedFailureTriageReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        ShadowRetrievalComparisonReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        PlanningShadowDiffTriageReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        PlanningShadowQualityReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        PlanningShadowRecallLossReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        PlanningOptInFallbackAnalysisReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        PlanningOptInConstraintSafetyReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        string text,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static void RenderReportToConsole(ContextEvalReport report)
    {
        Console.WriteLine("\n========================================================================================================================");
        Console.WriteLine("                                   🚀 ContextCore 真实中文上下文精细化评测汇总报告 🚀");
        Console.WriteLine("========================================================================================================================");
        Console.WriteLine($"总样本数: {report.TotalSamples,-5} | ✅ Passed: {report.PassedSamples,-5} | ⚠️ Warnings: {report.PassedWithWarningsSamples,-5} | ❌ Failed: {report.FailedSamples,-5} | 🚫 Invalid: {report.InvalidSamples,-5} | 综合通过率: {report.PassRate:P2}");
        Console.WriteLine($"平均 Recall@3: {report.AvgRetrievalRecall3:P2} | 平均 Recall@5: {report.AvgRetrievalRecall5:P2} | 平均 Recall@10: {report.AvgRetrievalRecall10:P2} | 平均 MRR: {report.AvgRetrievalMrr:F4}");
        Console.WriteLine($"Attention Shadow | MRR: {report.AvgAttentionMrr:F4} | Recall@3: {report.AvgAttentionRecall3:P2} | Recall@5: {report.AvgAttentionRecall5:P2} | Improved: {report.AttentionImprovedSamples} | Regressed: {report.AttentionRegressedSamples} | MustNotHitPromoted: {report.MustNotHitPromotedCount} | ChangeRatio: {report.SelectedSetChangeRatio:P2}");
        Console.WriteLine($"平均噪声违规率: {report.AvgRetrievalNoiseViolationRatio:P2} | 平均未用预算比: {report.AvgUnusedBudgetRatio:P2} | 黄金 Token 占比: {report.AvgMustHitTokenShare:P2}");
        Console.WriteLine($"约束符合率: {report.PackageConstraintHitRate:P2} | 实体符合率: {report.PackageEntityHitRate:P2} | 不确定性检测率: {report.PackageUncertaintyHitRate:P2}");
        Console.WriteLine($"平均指标计数 | 检索词数: {report.AvgRawSearchTokensCount:F1} | 语义锚点数: {report.AvgSemanticAnchorsCount:F1} | 候选数: {report.AvgCandidatesCount:F1} | 选中数: {report.AvgSelectedCount:F1} | 排除数: {report.AvgExcludedCount:F1}");
        Console.WriteLine("------------------------------------------------------------------------------------------------------------------------");

        // 使用报告中已固化的模式汇总；老 JSON 报告缺少该字段时从 Results 回退计算。
        var modeSummaries = GetModeSummaries(report);
        Console.WriteLine("\n[场景分组摘要]");
        Console.WriteLine("| 评测场景/模式 | 样本总数 | Passed | Warnings | Failed | 通过率 | Recall@3 | Recall@10 | MRR | AttnMRR | AttnR@5 | AttnChange | Noise | Waste | 黄金Token比 | 约束率 | 实体率 | 选中数 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var summary in modeSummaries)
        {
            Console.WriteLine($"| {summary.Mode,-13} | {summary.TotalSamples,8} | {summary.PassedSamples,6} | {summary.PassedWithWarningsSamples,8} | {summary.FailedSamples,6} | {summary.PassRate:P1} | {summary.AvgRetrievalRecall3:P1} | {summary.AvgRetrievalRecall10:P1} | {summary.AvgRetrievalMrr:F3} | {summary.AvgAttentionMrr:F3} | {summary.AvgAttentionRecall5:P1} | {summary.SelectedSetChangeRatio:P1} | {summary.AvgRetrievalNoiseViolationRatio:P1} | {summary.AvgPackageWasteRatio:P1} | {summary.AvgMustHitTokenShare:P1} | {summary.PackageConstraintHitRate:P1} | {summary.PackageEntityHitRate:P1} | {summary.AvgSelectedCount,6:F1} |");
        }

        var profileSummaries = GetAttentionProfileSummaries(report);
        if (profileSummaries.Count > 0)
        {
            Console.WriteLine("\n[Attention Profile Shadow Comparison]");
            Console.WriteLine("| Profile | Samples | AttnMRR | Recall@3 | Recall@5 | Improved | Regressed | MustNotHitPromoted | ChangeRatio |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var summary in profileSummaries)
            {
                Console.WriteLine($"| {summary.ProfileId} | {summary.SampleCount} | {summary.AvgAttentionMrr:F4} | {summary.AvgAttentionRecall3:P1} | {summary.AvgAttentionRecall5:P1} | {summary.ImprovedSamples} | {summary.RegressedSamples} | {summary.MustNotHitPromotedCount} | {summary.SelectedSetChangeRatio:P1} |");
            }

            RenderAttentionDiagnostics(report.AttentionDiagnostics);
        }

        Console.WriteLine("\n[详细评测结果]");
        Console.WriteLine("| 样本 ID | 评测场景/模式 | 精准状态 | Recall@3 | Recall@10 | MRR | AttnMRR | AttnR@5 | AttnChange | 黄金Token比 | 约束契合 | 实体契合 | 选中数 | 黄金金标备注 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var res in report.Results)
        {
            var stateStr = res.Status switch
            {
                "Passed" => "✅ PASSED",
                "PassedWithWarnings" => "⚠️ WARNING",
                "Failed" => "❌ FAILED",
                "InvalidSample" => "🚫 INVALID",
                _ => res.Status
            };
            var note = res.GoldenNotes.Length > 20 ? res.GoldenNotes[..17] + "..." : res.GoldenNotes;
            Console.WriteLine($"| {res.SampleId,-15} | {res.Mode,-13} | {stateStr,-10} | {res.RetrievalRecall3:P1} | {res.RetrievalRecall10:P1} | {res.RetrievalMrr:F3} | {res.AttentionMrr:F3} | {res.AttentionRecall5:P1} | {res.AttentionSelectedSetChangeRatio:P1} | {res.MustHitTokenShare:P1} | {(res.PackageHasAllConstraints ? "是" : "否"),-4} | {(res.PackageHasAllEntities ? "是" : "否"),-4} | {res.SelectedCount,6} | {note} |");
        }

        Console.WriteLine("\n[⚠️ 全局警告来源明细统计]");
        if (report.WarningSources.Count == 0)
        {
            Console.WriteLine("无任何质量警告发出，检索打包品质卓越！🎉");
        }
        else
        {
            Console.WriteLine("| 警告类型/原因 (Warning Source)          | 触发次数 | 占总样本比例 | 严重度级别 |");
            Console.WriteLine("|---|---|---|---|");
            foreach (var kv in report.WarningSources.OrderByDescending(x => x.Value))
            {
                var ratio = (double)kv.Value / report.TotalSamples;
                var severity = GetWarningSeverity(kv.Key);
                Console.WriteLine($"| {kv.Key,-39} | {kv.Value,8} | {ratio,10:P1} | {severity} |");
            }
        }

        Console.WriteLine("========================================================================================================================\n");
    }

    private static async Task DisplayLocalReportAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Error: 报告文件不存在: {path}");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<ContextEvalReport>(json, JsonOptions);
            if (report is null)
            {
                Console.Error.WriteLine("Error: 报告反序列化失败。");
                return;
            }
            Console.WriteLine(BuildMarkdownReport(report));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: 读取报告文件失败: {ex.Message}");
        }
    }

    private static async Task ExportReportAsync(
        ContextEvalReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (extension == ".json")
        {
            var json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        else if (extension == ".csv")
        {
            var csv = BuildCsvReport(report);
            await File.WriteAllTextAsync(fullPath, csv, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        else // default to markdown
        {
            var md = BuildMarkdownReport(report);
            await File.WriteAllTextAsync(fullPath, md, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] 报告已成功导出至: {fullPath}");
    }

    private static string BuildMarkdownReport(ContextEvalReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ContextCore 真实上下文质量评测报告");
        sb.AppendLine();
        sb.AppendLine($"*生成时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}*");
        sb.AppendLine();
        sb.AppendLine("## 1. 核心指标摘要");
        sb.AppendLine();
        sb.AppendLine($"| 指标名称 | 评测数值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 样本总数 | {report.TotalSamples} |");
        sb.AppendLine($"| ✅ Passed Samples | {report.PassedSamples} |");
        sb.AppendLine($"| ⚠️ Passed With Warnings | {report.PassedWithWarningsSamples} |");
        sb.AppendLine($"| ❌ Failed Samples | {report.FailedSamples} |");
        sb.AppendLine($"| 🚫 Invalid Samples | {report.InvalidSamples} |");
        sb.AppendLine($"| 综合通过率 | {report.PassRate:P2} |");
        sb.AppendLine($"| 平均 Recall@3 | {report.AvgRetrievalRecall3:P2} |");
        sb.AppendLine($"| 平均 Recall@5 | {report.AvgRetrievalRecall5:P2} |");
        sb.AppendLine($"| 平均 Recall@10 | {report.AvgRetrievalRecall10:P2} |");
        sb.AppendLine($"| 平均 MRR | {report.AvgRetrievalMrr:F4} |");
        sb.AppendLine($"| Attention 平均 MRR | {report.AvgAttentionMrr:F4} |");
        sb.AppendLine($"| Attention 平均 Recall@3 | {report.AvgAttentionRecall3:P2} |");
        sb.AppendLine($"| Attention 平均 Recall@5 | {report.AvgAttentionRecall5:P2} |");
        sb.AppendLine($"| Attention 改善样本数 | {report.AttentionImprovedSamples} |");
        sb.AppendLine($"| Attention 回退样本数 | {report.AttentionRegressedSamples} |");
        sb.AppendLine($"| MustNotHit 上推次数 | {report.MustNotHitPromotedCount} |");
        sb.AppendLine($"| Attention Selected Set Change Ratio | {report.SelectedSetChangeRatio:P2} |");
        sb.AppendLine($"| 平均噪声违规率 | {report.AvgRetrievalNoiseViolationRatio:P2} |");
        sb.AppendLine($"| 平均未用预算比 (Unused Budget) | {report.AvgUnusedBudgetRatio:P2} |");
        sb.AppendLine($"| 平均黄金 Token 占比 (MustHit Share) | {report.AvgMustHitTokenShare:P2} |");
        sb.AppendLine($"| 约束符合率 | {report.PackageConstraintHitRate:P2} |");
        sb.AppendLine($"| 实体符合率 | {report.PackageEntityHitRate:P2} |");
        sb.AppendLine($"| 不确定性检测率 | {report.PackageUncertaintyHitRate:P2} |");
        sb.AppendLine($"| 平均提取搜索词数 | {report.AvgRawSearchTokensCount:F2} |");
        sb.AppendLine($"| 平均提取语义锚点数 | {report.AvgSemanticAnchorsCount:F2} |");
        sb.AppendLine($"| 平均候选项数 | {report.AvgCandidatesCount:F2} |");
        sb.AppendLine($"| 平均打包选中数 | {report.AvgSelectedCount:F2} |");
        sb.AppendLine($"| 平均打包排除数 | {report.AvgExcludedCount:F2} |");
        sb.AppendLine();
        sb.AppendLine("## 2. 评测场景/模式统计");
        sb.AppendLine();
        sb.AppendLine("| 评测场景/模式 | 样本总数 | Passed | Warnings | Failed | 通过率 | 平均 Recall@3 | 平均 Recall@10 | 平均 MRR | AttnMRR | AttnR@5 | AttnChange | 噪声违规率 | Token 浪费率 | 黄金 Token 比 | 约束符合率 | 实体符合率 | 平均选中数 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var summary in GetModeSummaries(report))
        {
            sb.AppendLine($"| {summary.Mode} | {summary.TotalSamples} | {summary.PassedSamples} | {summary.PassedWithWarningsSamples} | {summary.FailedSamples} | {summary.PassRate:P1} | {summary.AvgRetrievalRecall3:P1} | {summary.AvgRetrievalRecall10:P1} | {summary.AvgRetrievalMrr:F4} | {summary.AvgAttentionMrr:F4} | {summary.AvgAttentionRecall5:P1} | {summary.SelectedSetChangeRatio:P1} | {summary.AvgRetrievalNoiseViolationRatio:P1} | {summary.AvgPackageWasteRatio:P1} | {summary.AvgMustHitTokenShare:P1} | {summary.PackageConstraintHitRate:P1} | {summary.PackageEntityHitRate:P1} | {summary.AvgSelectedCount:F1} |");
        }
        sb.AppendLine();
        var profileSummaries = GetAttentionProfileSummaries(report);
        if (profileSummaries.Count > 0)
        {
            sb.AppendLine("## 3. Attention Profile Shadow Comparison");
            sb.AppendLine();
            sb.AppendLine("| Profile | Samples | AttnMRR | Recall@3 | Recall@5 | Improved | Regressed | MustNotHitPromoted | ChangeRatio |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var summary in profileSummaries)
            {
                sb.AppendLine($"| {summary.ProfileId} | {summary.SampleCount} | {summary.AvgAttentionMrr:F4} | {summary.AvgAttentionRecall3:P1} | {summary.AvgAttentionRecall5:P1} | {summary.ImprovedSamples} | {summary.RegressedSamples} | {summary.MustNotHitPromotedCount} | {summary.SelectedSetChangeRatio:P1} |");
            }

            sb.AppendLine();
            sb.AppendLine("### Category Breakdown");
            sb.AppendLine();
            sb.AppendLine("| Profile | Category | Samples | AttnMRR | Recall@5 | Improved | Regressed | MustNotHitPromoted | ChangeRatio |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var summary in profileSummaries)
            {
                foreach (var category in summary.CategoryBreakdown)
                {
                    sb.AppendLine($"| {summary.ProfileId} | {category.Category} | {category.SampleCount} | {category.AvgAttentionMrr:F4} | {category.AvgAttentionRecall5:P1} | {category.ImprovedSamples} | {category.RegressedSamples} | {category.MustNotHitPromotedCount} | {category.SelectedSetChangeRatio:P1} |");
                }
            }

            AppendAttentionDiagnostics(sb, report.AttentionDiagnostics);
            sb.AppendLine();
        }

        sb.AppendLine("## 3. 详细测试清单");
        sb.AppendLine();
        sb.AppendLine("| 样本 ID | 场景模式 | 精准状态 | Recall@3 | Recall@10 | MRR | AttnMRR | AttnR@5 | AttnChange | 黄金 Token 比 | 约束率 | 实体率 | 选中数 | 黄金金标备注 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var res in report.Results)
        {
            var stateStr = res.Status switch
            {
                "Passed" => "✅ PASSED",
                "PassedWithWarnings" => "⚠️ WARNING",
                "Failed" => "❌ FAILED",
                "InvalidSample" => "🚫 INVALID",
                _ => res.Status
            };
            sb.AppendLine($"| {res.SampleId} | {res.Mode} | {stateStr} | {res.RetrievalRecall3:P1} | {res.RetrievalRecall10:P1} | {res.RetrievalMrr:F4} | {res.AttentionMrr:F4} | {res.AttentionRecall5:P1} | {res.AttentionSelectedSetChangeRatio:P1} | {res.MustHitTokenShare:P1} | {(res.PackageHasAllConstraints ? "是" : "否")} | {(res.PackageHasAllEntities ? "是" : "否")} | {res.SelectedCount} | {res.GoldenNotes} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 3. 全局警告来源汇总统计 (Warning Sources Summary)");
        sb.AppendLine();
        if (report.WarningSources.Count == 0)
        {
            sb.AppendLine("无任何质量警告发出，检索打包品质卓越！🎉");
        }
        else
        {
            sb.AppendLine("| 警告类型/原因 (Warning Source) | 触发次数 | 占总样本比例 | 严重度级别 |");
            sb.AppendLine("| :--- | :---: | :---: | :---: |");
            foreach (var kv in report.WarningSources.OrderByDescending(x => x.Value))
            {
                var ratio = (double)kv.Value / report.TotalSamples;
                var severity = GetWarningSeverity(kv.Key);
                sb.AppendLine($"| **{kv.Key}** | {kv.Value} | {ratio:P1} | {severity} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## 4. 样本输入与输出对照及过程追踪");
        sb.AppendLine();
        foreach (var res in report.Results)
        {
            sb.AppendLine($"### 🎯 样本: {res.SampleId} ({res.Mode})");
            sb.AppendLine();
            
            var stateStr = res.Status switch
            {
                "Passed" => "✅ PASSED",
                "PassedWithWarnings" => "⚠️ WARNING (Passed with quality warnings)",
                "Failed" => "❌ FAILED",
                "InvalidSample" => "🚫 INVALID",
                _ => res.Status
            };
            
            sb.AppendLine($"- **测评结论**: {stateStr}");
            if (!string.IsNullOrEmpty(res.ErrorMessage))
            {
                sb.AppendLine($"- **错误/失败诊断信息**: `{res.ErrorMessage}`");
            }
            sb.AppendLine($"- **金标备注**: {res.GoldenNotes}");
            sb.AppendLine();

            sb.AppendLine("#### 📊 输入与输出对照");
            sb.AppendLine();
            sb.AppendLine("| 输入维度 (Inputs) | 样本黄金期望设定 | 实际打包输出 (Outputs) | 状态校验结果 |");
            sb.AppendLine("|---|---|---|---|");
            sb.AppendLine($"| **用户查询 (Query)** | `{res.Query}` | - | - |");
            sb.AppendLine($"| **必须命中 (MustHit)** | `{string.Join(", ", res.MustHit)}` | `{string.Join(", ", res.SelectedIds.Where(id => res.MustHit.Contains(id)))}` | Recall@3: {res.RetrievalRecall3:P0}, Recall@10: {res.RetrievalRecall10:P0}, MRR: {res.RetrievalMrr:F3} <br> {(res.RetrievalRecall10 >= 0.99 ? "✅ 完美召回" : "❌ 召回缺失")} |");
            sb.AppendLine($"| **不得命中 (MustNotHit)** | `{string.Join(", ", res.MustNotHit)}` | `{string.Join(", ", res.SelectedIds.Where(id => res.MustNotHit.Contains(id)))}` | 噪音违规率: {res.RetrievalNoiseViolationRatio:P0} <br> {(res.MustNotHitRecalledCount == 0 ? "✅ 完美防御" : "❌ 噪音穿透")} |");
            sb.AppendLine($"| **预期约束 (ExpectedConstraints)** | `{string.Join(", ", res.ExpectedConstraints)}` | 已写入 constraints 字段中 | {(res.PackageHasAllConstraints ? "✅ 约束包含" : "❌ 约束缺失")} |");
            sb.AppendLine($"| **预期实体 (ExpectedEntities)** | `{string.Join(", ", res.ExpectedEntities)}` | 包含在打包的正文文本中 | {(res.PackageHasAllEntities ? "✅ 实体包含" : "❌ 实体缺失")} |");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(res.PackageBuildTrace))
            {
                sb.AppendLine("#### 🛠️ 组包审计过程 Trace");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(res.PackageBuildTrace);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static IReadOnlyList<ContextEvalModeSummary> GetModeSummaries(ContextEvalReport report)
    {
        if (report.ModeSummaries.Count > 0)
        {
            return report.ModeSummaries
                .OrderBy(summary => summary.Mode, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return report.Results
            .GroupBy(result => result.Mode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(BuildModeSummaryFromResults)
            .ToArray();
    }

    private static IReadOnlyList<ContextEvalAttentionProfileSummary> GetAttentionProfileSummaries(ContextEvalReport report)
    {
        if (report.AttentionProfileSummaries.Count > 0)
        {
            return report.AttentionProfileSummaries
                .OrderBy(summary => summary.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var rows = report.Results
            .SelectMany(result => result.AttentionProfiles.Select(profile => new { Result = result, Profile = profile }))
            .ToArray();
        if (rows.Length == 0)
        {
            return Array.Empty<ContextEvalAttentionProfileSummary>();
        }

        return rows
            .GroupBy(row => (row.Profile.ProfileId, row.Profile.PolicyVersion))
            .OrderBy(group => group.Key.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                return new ContextEvalAttentionProfileSummary
                {
                    ProfileId = group.Key.ProfileId,
                    PolicyVersion = group.Key.PolicyVersion,
                    SampleCount = items.Length,
                    AvgAttentionMrr = items.Average(item => item.Profile.AttentionMrr),
                    AvgAttentionRecall3 = items.Average(item => item.Profile.AttentionRecall3),
                    AvgAttentionRecall5 = items.Average(item => item.Profile.AttentionRecall5),
                    ImprovedSamples = items.Count(item => item.Profile.Improved),
                    RegressedSamples = items.Count(item => item.Profile.Regressed),
                    MustNotHitPromotedCount = items.Sum(item => item.Profile.MustNotHitPromotedCount),
                    SelectedSetChangeRatio = items.Average(item => item.Profile.SelectedSetChangeRatio),
                    CategoryBreakdown = items
                        .GroupBy(item => item.Result.Mode, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(category => category.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(category =>
                        {
                            var categoryItems = category.ToArray();
                            return new ContextEvalAttentionProfileCategorySummary
                            {
                                Category = category.Key,
                                SampleCount = categoryItems.Length,
                                AvgAttentionMrr = categoryItems.Average(item => item.Profile.AttentionMrr),
                                AvgAttentionRecall3 = categoryItems.Average(item => item.Profile.AttentionRecall3),
                                AvgAttentionRecall5 = categoryItems.Average(item => item.Profile.AttentionRecall5),
                                ImprovedSamples = categoryItems.Count(item => item.Profile.Improved),
                                RegressedSamples = categoryItems.Count(item => item.Profile.Regressed),
                                MustNotHitPromotedCount = categoryItems.Sum(item => item.Profile.MustNotHitPromotedCount),
                                SelectedSetChangeRatio = categoryItems.Average(item => item.Profile.SelectedSetChangeRatio)
                            };
                        })
                        .ToArray()
                };
            })
            .ToArray();
    }

    private static void RenderAttentionDiagnostics(ContextEvalAttentionDiagnostics diagnostics)
    {
        if (diagnostics.TopRegressedSamples.Count == 0
            && diagnostics.MustHitDemotedSamples.Count == 0
            && diagnostics.MustNotHitPromotedSamples.Count == 0
            && diagnostics.SelectedSetChangedSamples.Count == 0)
        {
            return;
        }

        Console.WriteLine("\n[Attention Regression Diagnostics]");
        Console.WriteLine($"TopRegressed={diagnostics.TopRegressedSamples.Count}, MustHitDemoted={diagnostics.MustHitDemotedSamples.Count}, MustNotHitPromoted={diagnostics.MustNotHitPromotedSamples.Count}, SelectedSetChanged={diagnostics.SelectedSetChangedSamples.Count}");
        foreach (var sample in diagnostics.TopRegressedSamples.Take(5))
        {
            Console.WriteLine($"- {sample.ProfileId}/{sample.SampleId}: delta={sample.MrrDelta:F4}, reason={sample.Reason}");
        }
    }

    private static void AppendAttentionDiagnostics(StringBuilder sb, ContextEvalAttentionDiagnostics diagnostics)
    {
        sb.AppendLine("### Regression Diagnostics");
        sb.AppendLine();
        AppendDiagnosticTable(sb, "Top Regressed Samples", diagnostics.TopRegressedSamples);
        AppendDiagnosticTable(sb, "MustHit Demoted Samples", diagnostics.MustHitDemotedSamples);
        AppendDiagnosticTable(sb, "MustNotHit Promoted Samples", diagnostics.MustNotHitPromotedSamples);
        AppendDiagnosticTable(sb, "Selected Set Changed Samples", diagnostics.SelectedSetChangedSamples);
    }

    private static void AppendDiagnosticTable(
        StringBuilder sb,
        string title,
        IReadOnlyList<ContextEvalAttentionDiagnosticSample> samples)
    {
        sb.AppendLine($"#### {title}");
        sb.AppendLine();
        if (samples.Count == 0)
        {
            sb.AppendLine("None.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Profile | Sample | Mode | CurrentMRR | AttnMRR | Delta | MustHitDemoted | MustNotHitPromoted | ChangeRatio | Reason |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var sample in samples)
        {
            sb.AppendLine($"| {sample.ProfileId} | {sample.SampleId} | {sample.Mode} | {sample.CurrentMrr:F4} | {sample.AttentionMrr:F4} | {sample.MrrDelta:F4} | {sample.MustHitDemotedCount} | {sample.MustNotHitPromotedCount} | {sample.SelectedSetChangeRatio:P1} | {sample.Reason} |");
        }

        sb.AppendLine();
    }

    private static ContextEvalModeSummary BuildModeSummaryFromResults(IGrouping<string, ContextEvalResult> group)
    {
        var items = group.ToArray();
        var total = items.Length;
        var warningSources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in items)
        {
            foreach (var reason in result.WarningReasons)
            {
                warningSources[reason] = warningSources.TryGetValue(reason, out var count) ? count + 1 : 1;
            }
        }

        return new ContextEvalModeSummary
        {
            Mode = group.Key,
            TotalSamples = total,
            PassedSamples = items.Count(result => result.Status == "Passed"),
            PassedWithWarningsSamples = items.Count(result => result.Status == "PassedWithWarnings"),
            FailedSamples = items.Count(result => result.Status == "Failed"),
            InvalidSamples = items.Count(result => result.Status == "InvalidSample"),
            PassRate = total == 0 ? 0.0 : (double)items.Count(result => result.Succeeded) / total,
            AvgRetrievalRecall3 = items.Average(result => result.RetrievalRecall3),
            AvgRetrievalRecall5 = items.Average(result => result.RetrievalRecall5),
            AvgRetrievalRecall10 = items.Average(result => result.RetrievalRecall10),
            AvgRetrievalMrrAnyMustHit = items.Average(result => result.RetrievalMrrAnyMustHit),
            AvgPrimaryMustHitMrr = items.Average(result => result.PrimaryMustHitMrr),
            AvgRetrievalNoiseViolationRatio = items.Average(result => result.RetrievalNoiseViolationRatio),
            AvgAttentionMrr = items.Average(result => result.AttentionMrr),
            AvgAttentionRecall3 = items.Average(result => result.AttentionRecall3),
            AvgAttentionRecall5 = items.Average(result => result.AttentionRecall5),
            AttentionImprovedSamples = items.Count(result => result.AttentionImproved),
            AttentionRegressedSamples = items.Count(result => result.AttentionRegressed),
            MustNotHitPromotedCount = items.Sum(result => result.MustNotHitPromotedCount),
            SelectedSetChangeRatio = items.Average(result => result.AttentionSelectedSetChangeRatio),
            AvgPackageWasteRatio = items.Average(result => result.PackageTokenWasteRatio),
            AvgUnusedBudgetRatio = items.Average(result => result.UnusedBudgetRatio),
            AvgMustHitTokenShare = items.Average(result => result.MustHitTokenShare),
            PackageConstraintHitRate = total == 0 ? 0.0 : (double)items.Count(result => result.PackageHasAllConstraints) / total,
            PackageEntityHitRate = total == 0 ? 0.0 : (double)items.Count(result => result.PackageHasAllEntities) / total,
            PackageUncertaintyHitRate = total == 0 ? 0.0 : (double)items.Count(result => result.PackageHasAllUncertainties) / total,
            AvgCandidatesCount = items.Average(result => result.CandidatesCount),
            AvgSelectedCount = items.Average(result => result.SelectedCount),
            AvgExcludedCount = items.Average(result => result.ExcludedCount),
            WarningSources = warningSources
        };
    }

    private static string BuildCsvReport(ContextEvalReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SampleId,Mode,Succeeded,RetrievalRecall5,RetrievalRecall10,RetrievalMrr,AttentionMrr,AttentionRecall3,AttentionRecall5,AttentionImproved,AttentionRegressed,AttentionWouldChangeSelectedSet,MustNotHitPromotedCount,AttentionSelectedSetChangeRatio,AttentionProfiles,RetrievalNoiseViolationRatio,PackageTokenWasteRatio,PackageHasAllConstraints,PackageHasAllEntities,PackageHasAllUncertainties,AnchorsCount,CandidatesCount,SelectedCount,ExcludedCount,PackageBuildTrace,ErrorMessage,GoldenNotes");
        foreach (var res in report.Results)
        {
            sb.AppendLine($"{EscapeCsv(res.SampleId)},{EscapeCsv(res.Mode)},{res.Succeeded},{res.RetrievalRecall5},{res.RetrievalRecall10},{res.RetrievalMrr},{res.AttentionMrr},{res.AttentionRecall3},{res.AttentionRecall5},{res.AttentionImproved},{res.AttentionRegressed},{res.AttentionWouldChangeSelectedSet},{res.MustNotHitPromotedCount},{res.AttentionSelectedSetChangeRatio},{EscapeCsv(FormatAttentionProfilesForCsv(res.AttentionProfiles))},{res.RetrievalNoiseViolationRatio},{res.PackageTokenWasteRatio},{res.PackageHasAllConstraints},{res.PackageHasAllEntities},{res.PackageHasAllUncertainties},{res.AnchorsCount},{res.CandidatesCount},{res.SelectedCount},{res.ExcludedCount},{EscapeCsv(res.PackageBuildTrace)},{EscapeCsv(res.ErrorMessage)},{EscapeCsv(res.GoldenNotes)}");
        }
        return sb.ToString();
    }

    private static string FormatAttentionProfilesForCsv(IReadOnlyList<ContextEvalAttentionProfileResult> profiles)
    {
        return string.Join("; ", profiles.Select(profile =>
            $"{profile.ProfileId}:mrr={profile.AttentionMrr:F4},r3={profile.AttentionRecall3:F4},r5={profile.AttentionRecall5:F4},change={profile.SelectedSetChangeRatio:F4},mnh={profile.MustNotHitPromotedCount}"));
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string GetWarningSeverity(string key)
    {
        return key switch
        {
            "LifecycleRiskSelectedInNormalContext" => "⚠️ Warning",
            "LifecycleItemIncludedForAudit" => "ℹ️ Info",
            "LifecycleItemExcluded" => "🔍 Diagnostics",
            _ => "⚠️ Warning"
        };
    }

    // ── A5 §7.3 性能基线 ───────────────────────────────────────────────
    private static readonly string[] PerfTexts =
    [
        "用户询问当前项目状态并请求摘要报告",
        "请记住我的偏好：输出使用中文，代码注释使用英文，避免冗余说明",
        "目前系统架构分为服务层、存储层、模型网关层三个核心模块，每个模块均支持可插拔的实现方式",
        "在向量检索中，bge-small-zh-v1.5 模型对中文语义相似度的计算在 512 token 以内表现稳定，超出后召回质量下降",
        "任务已完成：上下文包构建流程升级，新增 anchor extraction、working memory recall、graph expansion 三个阶段",
        "长期偏好已更新：用户希望在 coding 场景下优先注入最近的调试日志和测试失败信息，而非历史设计决策",
        "紧急约束：当前 sprint 内禁止修改 IContextStore 接口，所有相关变更需推迟至 B1 阶段",
        "小说进度：第三章结尾，主角发现了地图上标注的废弃矿洞实际上是秘密实验室入口",
        "自动化任务失败：步骤 4/7 超时，原因为外部 API 响应延迟超过 30s，需要重试或降级处理",
        "代码审查意见：EmbeddingContentHasher 的哈希函数需要将模型名称、输入类型和文本三者一起纳入，避免跨模型缓存命中",
        "当前系统对中文分词的支持依赖 BertTokenizer，最大序列长度为 256，超长文本需要在入库前截断或分块处理",
        "系统监控告警：向量索引构建任务已排队超过 5 分钟，当前队列深度为 23，建议检查 job worker 的处理速率",
        "用户明确要求：不要在上下文包中注入超过 6 个月前的旧决策，除非明确标注为长期约束",
        "关系图谱新增节点：ContextPackageBuilder 依赖于 HybridContextRetriever，后者依赖于 IVectorStore 和 IContextStore",
        "会话状态更新：用户已确认方案 B，方案 A 已被否决，相关 working memory 条目需标记为 rejected 并保留审计记录",
        "当前 embedding 缓存命中率为 84.3%，其中 query instruction 前缀的引入使得 query 类型命中率下降 12%",
    ];

    private static async Task ExecutePerfAsync(string? outputPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("\n========================================================");
        Console.WriteLine("          A5 §7.3  Embedding 性能基线测量");
        Console.WriteLine("========================================================");

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var memBefore = proc.WorkingSet64;

        var options = new EmbeddingOptions
        {
            ModelName = EmbeddingModelPaths.DefaultModelName,
            MaxBatchSize = 8,
            MaxSequenceLength = 256,
            OnnxIntraOpNumThreads = 1,
            OnnxInterOpNumThreads = 1,
            QueryInstruction = BgeQueryInstructions.BgeZhV15,
            EnableContentHashCache = false  // 性能测试关闭缓存，测实际 ONNX 耗时
        };
        var sessionManager = new OnnxEmbeddingSessionManager(options);
        var provider = new OnnxEmbeddingProvider(options, sessionManager);

        // 1. 首次模型加载耗时
        Console.Write("  [1/5] 首次模型加载... ");
        var swLoad = Stopwatch.StartNew();
        await sessionManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        swLoad.Stop();
        proc.Refresh();
        var memAfterLoad = proc.WorkingSet64;
        var loadMs = swLoad.ElapsedMilliseconds;
        Console.WriteLine($"{loadMs} ms  (WorkingSet +{(memAfterLoad - memBefore) / 1024 / 1024} MB)");

        // 2. 单条 embedding 延迟（Document 模式，10 次取均值）
        Console.Write("  [2/5] 单条 Document embedding（10 次）... ");
        var singleDocMs = await MeasureSingleEmbedAsync(provider, PerfTexts[0], EmbeddingInputKind.ContextItem, 10, cancellationToken);
        Console.WriteLine($"avg {singleDocMs:F1} ms");

        // 3. 单条 Query embedding（含 instruction）
        Console.Write("  [3/5] 单条 Query embedding（含 instruction，10 次）... ");
        var singleQueryMs = await MeasureSingleEmbedAsync(provider, PerfTexts[1], EmbeddingInputKind.Query, 10, cancellationToken);
        Console.WriteLine($"avg {singleQueryMs:F1} ms");

        // 4. Batch embedding 吞吐（16 条、32 条）
        Console.Write("  [4/5] Batch embedding 吞吐... ");
        var batchTexts16 = PerfTexts.Take(16).ToArray();
        var batchTexts32 = PerfTexts.Concat(PerfTexts).Take(32).ToArray();
        var batch16Ms = await MeasureBatchEmbedAsync(provider, batchTexts16, EmbeddingInputKind.ContextItem, 3, cancellationToken);
        var batch32Ms = await MeasureBatchEmbedAsync(provider, batchTexts32, EmbeddingInputKind.ContextItem, 3, cancellationToken);
        var throughput16 = 16 * 1000.0 / batch16Ms;
        var throughput32 = 32 * 1000.0 / batch32Ms;
        Console.WriteLine($"batch-16: {batch16Ms:F0} ms ({throughput16:F1} texts/s) | batch-32: {batch32Ms:F0} ms ({throughput32:F1} texts/s)");

        // 5. 内存占用
        proc.Refresh();
        var memFinal = proc.WorkingSet64;
        Console.Write("  [5/5] 内存占用... ");
        Console.WriteLine($"加载前: {memBefore / 1024 / 1024} MB | 加载后: {memAfterLoad / 1024 / 1024} MB | 测试后: {memFinal / 1024 / 1024} MB");

        // 6. A5.2 Pooling 策略验证：通过访问会话属性确认实际使用的 pooling 策略
        Console.Write("  [6/8] Pooling 策略验证... ");
        var poolingSession = await sessionManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var detectedPooling = poolingSession is OnnxRuntimeEmbeddingSession runtimeSession
            ? runtimeSession.PoolingStrategy.ToString()
            : "Unknown";
        Console.WriteLine($"{detectedPooling}（bge 模型预期：Cls）");

        // 7. A5.2 contentHash 缓存命中率：先无缓存 embed 16 条，再开缓存 embed 同 16 条，统计命中数
        Console.Write("  [7/8] contentHash 缓存命中率（16 条文本重复 embed）... ");
        var cacheOptions = new EmbeddingOptions
        {
            ModelName = options.ModelName,
            MaxBatchSize = options.MaxBatchSize,
            MaxSequenceLength = options.MaxSequenceLength,
            OnnxIntraOpNumThreads = options.OnnxIntraOpNumThreads,
            OnnxInterOpNumThreads = options.OnnxInterOpNumThreads,
            QueryInstruction = options.QueryInstruction,
            EnableContentHashCache = true   // 开启缓存，测命中率
        };
        var cacheManager = new OnnxEmbeddingSessionManager(cacheOptions);
        // 提前加载会话，避免首次加载干扰缓存测试
        await cacheManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var cacheProvider = new OnnxEmbeddingProvider(cacheOptions, cacheManager);
        var cacheTexts16 = PerfTexts.Take(16).ToList();
        var warmupReq = new EmbeddingRequest
        {
            InputKind = EmbeddingInputKind.ContextItem,
            Inputs = cacheTexts16.Select((t, i) => new EmbeddingInput { Id = $"cache-warm-{i}", Text = t }).ToList()
        };
        // 第一次：填充缓存
        await cacheProvider.EmbedAsync(warmupReq, cancellationToken).ConfigureAwait(false);
        // 第二次：相同 ID + 相同文本，验证命中缓存
        var cacheHitReq = new EmbeddingRequest
        {
            InputKind = EmbeddingInputKind.ContextItem,
            Inputs = cacheTexts16.Select((t, i) => new EmbeddingInput { Id = $"cache-hit-{i}", Text = t }).ToList()
        };
        var cacheHitResult = await cacheProvider.EmbedAsync(cacheHitReq, cancellationToken).ConfigureAwait(false);
        var cacheHitCount = cacheHitResult.Vectors.Count(v =>
            v.Metadata.TryGetValue("cacheHit", out var hit) && hit == "true");
        var cacheHitRate = cacheTexts16.Count > 0 ? (double)cacheHitCount / cacheTexts16.Count : 0;
        Console.WriteLine($"{cacheHitCount}/{cacheTexts16.Count} 命中（{cacheHitRate:P0}）");

        // 8. A5.2 序列长度消融测试：分别测试 seqlen=128/256/512 的单条 Doc embed 延迟
        Console.Write("  [8/8] 序列长度消融（seqlen 128 / 256 / 512）... ");
        var seqLenLatencies = new Dictionary<int, double>();
        foreach (var seqLen in new[] { 128, 256, 512 })
        {
            var seqOpts = new EmbeddingOptions
            {
                ModelName = options.ModelName,
                MaxBatchSize = options.MaxBatchSize,
                MaxSequenceLength = seqLen,
                OnnxIntraOpNumThreads = options.OnnxIntraOpNumThreads,
                OnnxInterOpNumThreads = options.OnnxInterOpNumThreads,
                EnableContentHashCache = false
            };
            var seqManager = new OnnxEmbeddingSessionManager(seqOpts);
            var seqProvider = new OnnxEmbeddingProvider(seqOpts, seqManager);
            // 预热：加载会话
            await seqManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            var latency = await MeasureSingleEmbedAsync(seqProvider, PerfTexts[0], EmbeddingInputKind.ContextItem, 5, cancellationToken);
            seqLenLatencies[seqLen] = latency;
        }
        Console.WriteLine($"seqlen=128: {seqLenLatencies[128]:F1} ms | seqlen=256: {seqLenLatencies[256]:F1} ms | seqlen=512: {seqLenLatencies[512]:F1} ms");

        // 汇总
        var result = new EmbeddingPerfResult
        {
            ModelName = options.ModelName,
            MeasuredAt = DateTimeOffset.UtcNow,
            ModelLoadMs = loadMs,
            WorkingSetBeforeMb = memBefore / 1024 / 1024,
            WorkingSetAfterLoadMb = memAfterLoad / 1024 / 1024,
            WorkingSetAfterPerfMb = memFinal / 1024 / 1024,
            SingleDocEmbedAvgMs = singleDocMs,
            SingleQueryEmbedAvgMs = singleQueryMs,
            Batch16AvgMs = batch16Ms,
            Batch32AvgMs = batch32Ms,
            Batch16ThroughputTextsPerSec = throughput16,
            Batch32ThroughputTextsPerSec = throughput32,
            QueryInstructionEnabled = !string.IsNullOrEmpty(options.QueryInstruction),
            MaxSequenceLength = options.MaxSequenceLength,
            MaxBatchSize = options.MaxBatchSize,
            DetectedPoolingStrategy = detectedPooling,
            CacheHitCount = cacheHitCount,
            CacheHitTotal = cacheTexts16.Count,
            CacheHitRate = cacheHitRate,
            SeqLen128AvgMs = seqLenLatencies.GetValueOrDefault(128),
            SeqLen256AvgMs = seqLenLatencies.GetValueOrDefault(256),
            SeqLen512AvgMs = seqLenLatencies.GetValueOrDefault(512)
        };

        Console.WriteLine("\n========================================================");
        Console.WriteLine("  [性能基线总结]");
        Console.WriteLine($"  模型:              {result.ModelName}");
        Console.WriteLine($"  首次加载:          {result.ModelLoadMs} ms");
        Console.WriteLine($"  单条 Doc embed:    {result.SingleDocEmbedAvgMs:F1} ms (avg 10 runs)");
        Console.WriteLine($"  单条 Query embed:  {result.SingleQueryEmbedAvgMs:F1} ms (avg 10 runs, with instruction)");
        Console.WriteLine($"  Batch-16 吞吐:     {result.Batch16ThroughputTextsPerSec:F1} texts/s");
        Console.WriteLine($"  Batch-32 吞吐:     {result.Batch32ThroughputTextsPerSec:F1} texts/s");
        Console.WriteLine($"  WorkingSet 增量:   +{result.WorkingSetAfterLoadMb - result.WorkingSetBeforeMb} MB (加载模型)");
        Console.WriteLine($"  Pooling 策略:      {result.DetectedPoolingStrategy}");
        Console.WriteLine($"  缓存命中率:        {result.CacheHitRate:P0} ({result.CacheHitCount}/{result.CacheHitTotal})");
        Console.WriteLine($"  SeqLen 消融:       128→{result.SeqLen128AvgMs:F1}ms  256→{result.SeqLen256AvgMs:F1}ms  512→{result.SeqLen512AvgMs:F1}ms");
        Console.WriteLine("========================================================\n");

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            var fullPath = Path.GetFullPath(outputPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(fullPath, json, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Perf] 结果已写入: {fullPath}");
        }
    }

    private static async Task<double> MeasureSingleEmbedAsync(
        OnnxEmbeddingProvider provider,
        string text,
        EmbeddingInputKind kind,
        int iterations,
        CancellationToken cancellationToken)
    {
        long totalMs = 0;
        for (var i = 0; i < iterations; i++)
        {
            var req = new EmbeddingRequest
            {
                InputKind = kind,
                Inputs = [new EmbeddingInput { Id = $"perf-{i}", Text = text }]
            };
            var sw = Stopwatch.StartNew();
            await provider.EmbedAsync(req, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            totalMs += sw.ElapsedMilliseconds;
        }
        return (double)totalMs / iterations;
    }

    private static async Task<double> MeasureBatchEmbedAsync(
        OnnxEmbeddingProvider provider,
        string[] texts,
        EmbeddingInputKind kind,
        int iterations,
        CancellationToken cancellationToken)
    {
        var inputs = texts.Select((t, i) => new EmbeddingInput { Id = $"batch-{i}", Text = t }).ToList();
        long totalMs = 0;
        for (var i = 0; i < iterations; i++)
        {
            var req = new EmbeddingRequest { InputKind = kind, Inputs = inputs };
            var sw = Stopwatch.StartNew();
            await provider.EmbedAsync(req, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            totalMs += sw.ElapsedMilliseconds;
        }
        return (double)totalMs / iterations;
    }

    private sealed class EmbeddingPerfResult
    {
        public string ModelName { get; init; } = string.Empty;
        public DateTimeOffset MeasuredAt { get; init; }
        public long ModelLoadMs { get; init; }
        public long WorkingSetBeforeMb { get; init; }
        public long WorkingSetAfterLoadMb { get; init; }
        public long WorkingSetAfterPerfMb { get; init; }
        public double SingleDocEmbedAvgMs { get; init; }
        public double SingleQueryEmbedAvgMs { get; init; }
        public double Batch16AvgMs { get; init; }
        public double Batch32AvgMs { get; init; }
        public double Batch16ThroughputTextsPerSec { get; init; }
        public double Batch32ThroughputTextsPerSec { get; init; }
        public bool QueryInstructionEnabled { get; init; }
        public int MaxSequenceLength { get; init; }
        public int MaxBatchSize { get; init; }
        // A5.2 新增字段
        public string DetectedPoolingStrategy { get; init; } = string.Empty;
        public int CacheHitCount { get; init; }
        public int CacheHitTotal { get; init; }
        public double CacheHitRate { get; init; }
        public double SeqLen128AvgMs { get; init; }
        public double SeqLen256AvgMs { get; init; }
        public double SeqLen512AvgMs { get; init; }
    }

    // ── A5.3 §7.3  规模查询延迟测试 ─────────────────────────────────
    /// <summary>
    /// 在内存向量存储中生成 <paramref name="size"/> 条合成上下文，
    /// 批量 embedding 后执行 20 条查询，测量 p50/p95/p99 延迟。
    /// <paramref name="fakeVectors"/> = true 时跳过语料 ONNX 嵌入，改用随机单位向量
    /// （用于 100k 规模纯存储/搜索延迟测试）。
    /// </summary>
    private static async Task ExecutePerfScaleAsync(
        int size,
        bool fakeVectors,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("\n========================================================");
        Console.WriteLine($"          A5 §7.3  规模查询延迟测试（N = {size}{(fakeVectors ? "，合成向量" : "")}）");
        Console.WriteLine("========================================================");

        // 初始化 embedding provider（关闭缓存，测真实 ONNX 耗时）
        var embOpts = new EmbeddingOptions
        {
            ModelName = EmbeddingModelPaths.DefaultModelName,
            MaxBatchSize = 32,
            MaxSequenceLength = 256,
            OnnxIntraOpNumThreads = 1,
            OnnxInterOpNumThreads = 1,
            EnableContentHashCache = false,
            QueryInstruction = BgeQueryInstructions.BgeZhV15
        };
        var embManager = new OnnxEmbeddingSessionManager(embOpts);
        // 预热：加载会话（不计入索引构建时间；--fake-vectors 时仍预热，用于 query embedding）
        Console.Write("  [1/4] 预热模型加载... ");
        var swLoad = Stopwatch.StartNew();
        await embManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        swLoad.Stop();
        Console.WriteLine($"{swLoad.ElapsedMilliseconds} ms");

        var embProvider = new OnnxEmbeddingProvider(embOpts, embManager);
        var vectorStore = new InMemoryVectorStore();
        const string workspaceId = "perf-scale";
        const string modelName = EmbeddingModelPaths.DefaultModelName;
        const int embDims = 384; // bge-small-zh-v1.5

        // 2. 构建索引
        long indexBuildMs;
        double indexThroughput;
        if (fakeVectors)
        {
            // --fake-vectors：跳过 ONNX，生成随机单位向量（测纯存储/搜索延迟）
            Console.Write($"  [2/4] 生成 {size} 条随机单位向量并写入 VectorStore... ");
            var rng = new Random(42);
            var swIndex = Stopwatch.StartNew();
            for (var i = 0; i < size; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawVec = new float[embDims];
                double norm = 0;
                for (var d = 0; d < embDims; d++)
                {
                    rawVec[d] = (float)(rng.NextDouble() * 2 - 1);
                    norm += rawVec[d] * (double)rawVec[d];
                }
                norm = Math.Sqrt(norm);
                if (norm > 0)
                    for (var d = 0; d < embDims; d++) rawVec[d] = (float)(rawVec[d] / norm);

                await vectorStore.UpsertAsync(new VectorRecord
                {
                    Id = $"scale-{i}",
                    WorkspaceId = workspaceId,
                    CollectionId = "scale",
                    SourceId = $"scale-{i}",
                    SourceKind = "context",
                    ModelName = modelName,
                    Dimensions = embDims,
                    Vector = rawVec,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
            }
            swIndex.Stop();
            indexBuildMs = swIndex.ElapsedMilliseconds;
            indexThroughput = size * 1000.0 / Math.Max(1, indexBuildMs);
            Console.WriteLine($"{indexBuildMs} ms（{indexThroughput:F1} items/s）");
        }
        else
        {
            // 生成 N 条合成文本（PerfTexts 循环 + 编号后缀）
            var syntheticTexts = Enumerable.Range(0, size)
                .Select(i => PerfTexts[i % PerfTexts.Length] + $"（条目编号：{i + 1}）")
                .ToArray();

            // 批量 embed + 写入 VectorStore（测量索引构建时间）
            Console.Write($"  [2/4] 批量 embed + 写入 VectorStore（{size} 条）... ");
            var swIndex = Stopwatch.StartNew();
            foreach (var batch in syntheticTexts.Select((t, i) => new { Text = t, Index = i })
                         .Chunk(Math.Max(1, embOpts.MaxBatchSize)))
            {
                var embedReq = new EmbeddingRequest
                {
                    InputKind = EmbeddingInputKind.ContextItem,
                    Inputs = batch.Select(item => new EmbeddingInput
                    {
                        Id = $"scale-{item.Index}",
                        Text = item.Text
                    }).ToList()
                };
                var embedResult = await embProvider.EmbedAsync(embedReq, cancellationToken).ConfigureAwait(false);
                foreach (var vec in embedResult.Vectors)
                {
                    await vectorStore.UpsertAsync(new VectorRecord
                    {
                        Id = vec.InputId,
                        WorkspaceId = workspaceId,
                        CollectionId = "scale",
                        SourceId = vec.InputId,
                        SourceKind = "context",
                        ModelName = modelName,
                        Dimensions = vec.Values.Count,
                        Vector = vec.Values.ToArray(),
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            swIndex.Stop();
            indexBuildMs = swIndex.ElapsedMilliseconds;
            indexThroughput = size * 1000.0 / Math.Max(1, indexBuildMs);
            Console.WriteLine($"{indexBuildMs} ms（{indexThroughput:F1} items/s）");
        }

        // 3. 执行 20 条查询，测量每条端到端延迟（embed query + vector search）
        Console.Write("  [3/4] 执行 20 条查询延迟测量... ");
        var queryTexts = PerfTexts.Concat(PerfTexts).Take(20).ToArray();
        var queryLatenciesMs = new List<double>(20);
        foreach (var qText in queryTexts)
        {
            var swQuery = Stopwatch.StartNew();
            var qReq = new EmbeddingRequest
            {
                InputKind = EmbeddingInputKind.Query,
                Inputs = [new EmbeddingInput { Id = "q", Text = qText }]
            };
            var qEmbed = await embProvider.EmbedAsync(qReq, cancellationToken).ConfigureAwait(false);
            if (qEmbed.Succeeded && qEmbed.Vectors.Count > 0)
            {
                var searchQuery = new VectorQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = "scale",
                    Vector = qEmbed.Vectors[0].Values,
                    TopK = 10
                };
                await vectorStore.SearchAsync(searchQuery, cancellationToken).ConfigureAwait(false);
            }
            swQuery.Stop();
            queryLatenciesMs.Add(swQuery.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine("完成");

        // 4. 计算 p50/p95/p99 延迟
        Console.Write("  [4/4] 计算延迟百分位... ");
        var sorted = queryLatenciesMs.Order().ToArray();
        var p50 = Percentile(sorted, 50);
        var p95 = Percentile(sorted, 95);
        var p99 = Percentile(sorted, 99);
        var avgLatency = queryLatenciesMs.Average();
        Console.WriteLine("完成");

        var scaleResult = new PerfScaleResult
        {
            ModelName = embOpts.ModelName,
            MeasuredAt = DateTimeOffset.UtcNow,
            IndexSize = size,
            FakeVectors = fakeVectors,
            IndexBuildMs = indexBuildMs,
            IndexBuildThroughputItemsPerSec = indexThroughput,
            QueryCount = queryTexts.Length,
            QueryAvgMs = avgLatency,
            QueryP50Ms = p50,
            QueryP95Ms = p95,
            QueryP99Ms = p99,
            TopK = 10,
            MaxSequenceLength = embOpts.MaxSequenceLength,
            BatchSize = embOpts.MaxBatchSize
        };

        Console.WriteLine("\n========================================================");
        Console.WriteLine($"  [规模测试总结]  N = {scaleResult.IndexSize} 条");
        Console.WriteLine($"  索引构建:    {scaleResult.IndexBuildMs} ms  ({scaleResult.IndexBuildThroughputItemsPerSec:F1} items/s)");
        Console.WriteLine($"  查询延迟 avg:{scaleResult.QueryAvgMs:F1} ms  p50:{scaleResult.QueryP50Ms:F1} ms  p95:{scaleResult.QueryP95Ms:F1} ms  p99:{scaleResult.QueryP99Ms:F1} ms");
        Console.WriteLine($"  TopK={scaleResult.TopK}  seqlen={scaleResult.MaxSequenceLength}  batchSize={scaleResult.BatchSize}");
        Console.WriteLine("========================================================\n");

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var json = JsonSerializer.Serialize(scaleResult, JsonOptions);
            var fullPath = Path.GetFullPath(outputPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(fullPath, json, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[PerfScale] 结果已写入: {fullPath}");
        }
    }

    /// <summary>从已排序数组中取第 <paramref name="percentile"/> 百分位值。</summary>
    private static double Percentile(double[] sorted, int percentile)
    {
        if (sorted.Length == 0) return 0;
        var idx = (percentile / 100.0) * (sorted.Length - 1);
        var lower = (int)idx;
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var frac = idx - lower;
        return sorted[lower] + frac * (sorted[upper] - sorted[lower]);
    }

    private sealed class PerfScaleResult
    {
        public string ModelName { get; init; } = string.Empty;
        public DateTimeOffset MeasuredAt { get; init; }
        public int IndexSize { get; init; }
        public bool FakeVectors { get; init; }
        public long IndexBuildMs { get; init; }
        public double IndexBuildThroughputItemsPerSec { get; init; }
        public int QueryCount { get; init; }
        public double QueryAvgMs { get; init; }
        public double QueryP50Ms { get; init; }
        public double QueryP95Ms { get; init; }
        public double QueryP99Ms { get; init; }
        public int TopK { get; init; }
        public int MaxSequenceLength { get; init; }
        public int BatchSize { get; init; }
    }

    // ── A5 §7.1 专项检索评测 ──────────────────────────────────────────
    private static async Task ExecuteRetrievalAsync(string outputPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("\n========================================================");
        Console.WriteLine("        A5 §7.1  专项 Retrieval Query 集评测");
        Console.WriteLine("========================================================");

        var contextsRoot = ResolveContextsRoot();
        if (!Directory.Exists(contextsRoot))
        {
            Console.Error.WriteLine($"Error: 评测数据根目录不存在: {contextsRoot}");
            return;
        }

        var runner = new RetrievalEvalRunner();
        var report = await runner.RunAsync(contextsRoot, cancellationToken).ConfigureAwait(false);

        RetrievalEvalRunner.RenderToConsole(report);

        if (!string.IsNullOrEmpty(report.ErrorMessage))
        {
            Console.Error.WriteLine($"Error: {report.ErrorMessage}");
            return;
        }

        await RetrievalEvalRunner.ExportAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[RetrievalEval] 报告已保存至: {Path.GetFullPath(outputPath)}");
    }

    // ── A0 §2.4 存储可读写深度检查 ─────────────────────────────────────
    private static async Task ExecuteStorageCheckAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        var state = service.State;
        const string ProbeWs = "__readiness_probe__";
        const string ProbeColl = "__probe__";
        var probeId = $"probe-{DateTimeOffset.UtcNow.Ticks}";

        Console.WriteLine("\n========================================================");
        Console.WriteLine("          A0 §2.4  存储可读写深度检查");
        Console.WriteLine("========================================================");
        Console.WriteLine($"  存储类型 : {state.StorageKind}");
        Console.WriteLine($"  探针 ID  : {probeId}");
        Console.WriteLine();

        var now = DateTimeOffset.UtcNow;
        var results = new List<StorageCheckResult>
        {
            // 1. IContextStore
            await RunStorageCheckAsync("context-store", cancellationToken, async token =>
            {
                var item = new ContextItem
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Type = "readiness-probe",
                    Content = "readiness probe — safe to delete",
                    CreatedAt = now
                };
                await state.ContextStore.SaveAsync(item, token);
                var readBack = await state.ContextStore.GetAsync(ProbeWs, ProbeColl, probeId, token);
                await state.ContextStore.DeleteAsync(ProbeWs, ProbeColl, probeId, token);
                if (readBack is null || readBack.Id != probeId)
                    throw new InvalidOperationException($"读回 ID 不匹配：expected={probeId}");
                return "写入→读取→删除 成功";
            }),

            // 2. IMemoryStore
            await RunStorageCheckAsync("memory-store", cancellationToken, async token =>
            {
                var item = new ContextMemoryItem
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Type = "readiness-probe",
                    Content = "readiness probe — safe to delete",
                    Layer = ContextMemoryLayer.Working,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await state.MemoryStore.SaveAsync(item, token);
                var readBack = await state.MemoryStore.GetAsync(ProbeWs, ProbeColl, probeId, token);
                if (readBack is null || readBack.Id != probeId)
                    throw new InvalidOperationException($"读回 ID 不匹配：expected={probeId}");
                return "写入→读取 成功（接口无 DeleteAsync）";
            }),

            // 3. IRelationStore
            await RunStorageCheckAsync("relation-store", cancellationToken, async token =>
            {
                var relation = new ContextRelation
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    SourceId = probeId,
                    TargetId = probeId,
                    RelationType = "readiness-probe",
                    CreatedAt = now
                };
                await state.RelationStore.SaveAsync(relation, token);
                var readBack = await state.RelationStore.QueryBySourceAsync(ProbeWs, ProbeColl, probeId, token);
                if (!readBack.Any(r => r.Id == probeId))
                    throw new InvalidOperationException("写入成功但 QueryBySourceAsync 找不到探针关系");
                return "写入→QueryBySource 成功（接口无 DeleteAsync）";
            }),

            // 4. IConstraintStore
            await RunStorageCheckAsync("constraint-store", cancellationToken, async token =>
            {
                var constraint = new ContextConstraint
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Content = "readiness probe — safe to delete",
                    Level = ConstraintLevel.Soft,
                    Scope = ContextScope.Collection,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await state.ConstraintStore.SaveAsync(constraint, token);
                var readBack = await state.ConstraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Take = 100
                }, token);
                if (!readBack.Any(c => c.Id == probeId))
                    throw new InvalidOperationException("写入成功但 QueryAsync 找不到探针约束");
                return "写入→QueryAsync 成功（接口无 DeleteAsync）";
            }),

            // 5. IContextJobQueue
            await RunStorageCheckAsync("job-queue", cancellationToken, async token =>
            {
                var job = new ContextJob
                {
                    JobId = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Kind = ContextJobKind.Custom,
                    PayloadJson = "{}",
                    State = ContextJobState.Queued,
                    CreatedAt = now
                };
                await state.JobQueue.EnqueueAsync(job, token);
                var queued = await state.JobQueryStore.QueryAsync(new ContextJobQuery
                {
                    WorkspaceId = ProbeWs,
                    State = ContextJobState.Queued,
                    Take = 100
                }, token);
                if (!queued.Any(j => j.JobId == probeId))
                    throw new InvalidOperationException("入队成功但 QueryAsync 找不到探针作业");
                return "入队→QueryAsync 成功（探针作业将由处理器 Nack 或手动清理）";
            }),

            // 6. IRetrievalTraceStore
            await RunStorageCheckAsync("retrieval-trace", cancellationToken, async token =>
            {
                var trace = new ContextRetrievalTrace
                {
                    RetrievalId = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    QueryText = "readiness probe",
                    CreatedAt = now
                };
                await state.RetrievalTraceStore.SaveAsync(trace, token);
                var readBack = await state.RetrievalTraceStore.QueryRecentAsync(ProbeWs, ProbeColl, 100, token);
                if (!readBack.Any(t => t.RetrievalId == probeId))
                    throw new InvalidOperationException("写入成功但 QueryRecentAsync 找不到探针 trace");
                return "写入→QueryRecent 成功（接口无 DeleteAsync）";
            })
        };

        // 打印结果表格
        int passed = 0, failed = 0;
        Console.WriteLine($"  {"存储",-22} {"状态",-8} {"耗时",7}  说明");
        Console.WriteLine($"  {new string('-', 72)}");
        foreach (var r in results)
        {
            var icon = r.Ok ? "✅" : "❌";
            Console.WriteLine($"  {icon} {r.Name,-20} {r.Status,-8} {r.ElapsedMs,5} ms  {r.Message}");
            if (r.Ok) passed++; else failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"  结论: {passed}/{results.Count} 通过 — {(failed == 0 ? "所有存储可读写 ✅" : $"{failed} 项失败 ❌")}");
        Console.WriteLine("========================================================");
    }

    private static async Task<StorageCheckResult> RunStorageCheckAsync(
        string name,
        CancellationToken ct,
        Func<CancellationToken, Task<string>> check)
    {
        var sw = Stopwatch.StartNew();
        using var perCheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCheckCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var message = await check(perCheckCts.Token);
            return StorageCheckResult.Pass(name, sw.Elapsed, message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StorageCheckResult.Fail(name, sw.Elapsed, "检查超时（>5s）");
        }
        catch (Exception ex)
        {
            return StorageCheckResult.Fail(name, sw.Elapsed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class StorageCheckResult
    {
        public required string Name { get; init; }
        public required bool Ok { get; init; }
        public required string Status { get; init; }
        public required long ElapsedMs { get; init; }
        public required string Message { get; init; }

        public static StorageCheckResult Pass(string name, TimeSpan elapsed, string message) =>
            new() { Name = name, Ok = true, Status = "ok", ElapsedMs = (long)elapsed.TotalMilliseconds, Message = message };

        public static StorageCheckResult Fail(string name, TimeSpan elapsed, string message) =>
            new() { Name = name, Ok = false, Status = "error", ElapsedMs = (long)elapsed.TotalMilliseconds, Message = message };
    }

    // ── A5 §7.2 Chunk Size 消融实验 ────────────────────────────────────
    private static async Task ExecuteChunkAblationAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("        A5 §7.2  Chunk Size 消融（召回质量对比）");
        Console.WriteLine("=======================================================");

        // 加载检索语料和样本
        var contextsRoot = ResolveContextsRoot();
        var retrievalDir = Path.Combine(contextsRoot, "retrieval");
        if (!Directory.Exists(retrievalDir))
        {
            Console.Error.WriteLine($"Error: 检索评测目录不存在: {retrievalDir}");
            return;
        }

        var corpusPath = Path.Combine(retrievalDir, "corpus.json");
        var samplesPath = Path.Combine(retrievalDir, "seed_samples.json");
        if (!File.Exists(corpusPath) || !File.Exists(samplesPath))
        {
            Console.Error.WriteLine("Error: corpus.json 或 seed_samples.json 不存在");
            return;
        }

        var jOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var corpus = JsonSerializer.Deserialize<ContextCore.Abstractions.Models.ContextEvalCorpus>(
            await File.ReadAllTextAsync(corpusPath, cancellationToken).ConfigureAwait(false), jOpts)!;
        var samples = JsonSerializer.Deserialize<List<ContextCore.Abstractions.Models.ContextEvalSample>>(
            await File.ReadAllTextAsync(samplesPath, cancellationToken).ConfigureAwait(false), jOpts)!;

        // 仅保留有 MustHit 的样本（纯向量召回测试只看向量路径和 chunk 特征）
        var evalSamples = samples.Where(s => s.MustHit.Count > 0).ToList();
        if (evalSamples.Count == 0)
        {
            Console.Error.WriteLine("Error: 无有效 MustHit 样本");
            return;
        }
        Console.WriteLine($"  语料: {corpus.Contexts.Count} 个 context item  样本: {evalSamples.Count} 条（含 MustHit）");

        // 初始化 embedding provider
        Console.Write("  初始化 OnnxEmbeddingProvider... ");
        var embOpts = new EmbeddingOptions
        {
            ModelName = EmbeddingModelPaths.DefaultModelName,
            MaxBatchSize = 16,
            MaxSequenceLength = 256,
            OnnxIntraOpNumThreads = 1,
            OnnxInterOpNumThreads = 1,
            EnableContentHashCache = true,
            QueryInstruction = BgeQueryInstructions.BgeZhV15
        };
        var embManager = new OnnxEmbeddingSessionManager(embOpts);
        await embManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);  // 预热
        var embProvider = new OnnxEmbeddingProvider(embOpts, embManager);
        Console.WriteLine("完成");

        // 预先向量化所有 query（各 chunk 大小共用，不受 chunk 影响）
        Console.Write("  向量化所有 query... ");
        var queryVectors = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        foreach (var sample in evalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var qRes = await embProvider.EmbedAsync(new EmbeddingRequest
            {
                WorkspaceId = "chunk-ablation",
                CollectionId = "eval",
                InputKind = EmbeddingInputKind.Query,
                Inputs = [new EmbeddingInput { Id = sample.Id, Text = sample.Query, SourceRef = sample.Id }]
            }, cancellationToken).ConfigureAwait(false);
            if (qRes.Succeeded && qRes.Vectors.Count > 0)
                queryVectors[sample.Id] = qRes.Vectors[0].Values;
        }
        Console.WriteLine($"完成（{queryVectors.Count} 条）");

        // 按各 chunk size 跑完整对比流程
        var chunkSizes = new[] { 64, 128, 256, 512 };
        var summaryRows = new List<ChunkAblationRow>();

        foreach (var chunkSize in chunkSizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.Write($"  [chunk={chunkSize,3}] 切分并嵌入... ");

            // 1. 切分语料
            var vectorStore = new InMemoryVectorStore();
            const string ws = "chunk-ablation";
            const string coll = "eval";

            var chunkInputs = new List<(string chunkId, string originalId, string text)>();
            foreach (var ctx in corpus.Contexts)
            {
                var content = ctx.Content ?? string.Empty;
                if (content.Length == 0)
                {
                    chunkInputs.Add(($"{ctx.Id}::c0", ctx.Id, string.Empty));
                    continue;
                }
                var ci = 0;
                for (var pos = 0; pos < content.Length; pos += chunkSize, ci++)
                {
                    var chunkText = content.Substring(pos, Math.Min(chunkSize, content.Length - pos));
                    chunkInputs.Add(($"{ctx.Id}::c{ci}", ctx.Id, chunkText));
                }
            }

            // 2. 批量 embed
            foreach (var batchItems in chunkInputs.Chunk(embOpts.MaxBatchSize))
            {
                var batchReq = new EmbeddingRequest
                {
                    WorkspaceId = ws,
                    CollectionId = coll,
                    InputKind = EmbeddingInputKind.ContextItem,
                    Inputs = batchItems.Select(x => new EmbeddingInput
                    {
                        Id = x.chunkId,
                        Text = x.text,
                        SourceRef = x.originalId   // SourceId = 原始 item ID，便于 MustHit 匹配
                    }).ToList()
                };
                var embedRes = await embProvider.EmbedAsync(batchReq, cancellationToken).ConfigureAwait(false);
                if (!embedRes.Succeeded) continue;

                var originalIdMap = batchItems.ToDictionary(x => x.chunkId, x => x.originalId, StringComparer.Ordinal);
                foreach (var vec in embedRes.Vectors)
                {
                    var sourceId = originalIdMap.TryGetValue(vec.InputId, out var oid) ? oid : vec.SourceRef ?? vec.InputId;
                    await vectorStore.UpsertAsync(new VectorRecord
                    {
                        Id = $"vec-{vec.InputId}",
                        WorkspaceId = ws,
                        CollectionId = coll,
                        SourceId = sourceId,
                        SourceKind = "context",
                        ModelName = embedRes.ModelName,
                        Dimensions = embedRes.Dimensions,
                        Vector = vec.Values,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }
            }

            var totalChunks = chunkInputs.Count;
            Console.Write($"{totalChunks} 个 chunk... ");

            // 3. 逐样本向量检索（纯向量路径，不走关键词和关系扩展）
            var recall5List = new List<double>();
            var recall10List = new List<double>();
            var mrrList = new List<double>();

            foreach (var sample in evalSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!queryVectors.TryGetValue(sample.Id, out var qVec) || qVec.Count == 0)
                    continue;

                var hits = await vectorStore.SearchAsync(new VectorQuery
                {
                    WorkspaceId = ws,
                    CollectionId = coll,
                    Vector = qVec,
                    TopK = 20
                }, cancellationToken).ConfigureAwait(false);

                // SourceId = 原始 item ID（大小写不敏感）
                var hitSourceIds = hits.Select(h => h.Record.SourceId).ToList();

                var mustHitCount = sample.MustHit.Count;
                // Recall@5: 前 5 个命中的 MustHit 比例
                var r5 = mustHitCount == 0 ? 1.0 :
                    (double)sample.MustHit.Count(id =>
                        hitSourceIds.Take(5).Any(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase))) / mustHitCount;
                // Recall@10
                var r10 = mustHitCount == 0 ? 1.0 :
                    (double)sample.MustHit.Count(id =>
                        hitSourceIds.Take(10).Any(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase))) / mustHitCount;
                // MRR
                double mrr = 0.0;
                for (var i = 0; i < hitSourceIds.Count; i++)
                {
                    if (sample.MustHit.Any(id => string.Equals(id, hitSourceIds[i], StringComparison.OrdinalIgnoreCase)))
                    {
                        mrr = 1.0 / (i + 1);
                        break;
                    }
                }

                recall5List.Add(r5);
                recall10List.Add(r10);
                mrrList.Add(mrr);
            }

            var avgR5 = recall5List.Count > 0 ? recall5List.Average() : 0;
            var avgR10 = recall10List.Count > 0 ? recall10List.Average() : 0;
            var avgMrr = mrrList.Count > 0 ? mrrList.Average() : 0;
            var avgChunksPerItem = corpus.Contexts.Count > 0 ? (double)totalChunks / corpus.Contexts.Count : 0;

            Console.WriteLine($"Recall@10={avgR10:P0}  MRR={avgMrr:F3}");
            summaryRows.Add(new ChunkAblationRow
            {
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                AvgChunksPerItem = avgChunksPerItem,
                Recall5 = avgR5,
                Recall10 = avgR10,
                Mrr = avgMrr,
                SampleCount = evalSamples.Count
            });
        }

        // 输出对比表格
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("  Chunk Size 消融结果（纯向量检索，retrieval 语料 30 条样本）");
        Console.WriteLine("=======================================================");
        Console.WriteLine($"  {"切分大小",8}  {"总 chunks",10}  {"平均 chunks/item",16}  {"Recall@5",9}  {"Recall@10",10}  {"MRR",7}");
        Console.WriteLine($"  {new string('-', 72)}");
        foreach (var row in summaryRows)
        {
            Console.WriteLine($"  {row.ChunkSize,8}  {row.TotalChunks,10}  {row.AvgChunksPerItem,16:F1}  {row.Recall5,9:P0}  {row.Recall10,10:P0}  {row.Mrr,7:F3}");
        }

        var best = summaryRows.OrderByDescending(r => r.Recall10).ThenByDescending(r => r.Mrr).FirstOrDefault();
        if (best is not null)
        {
            Console.WriteLine($"\n  最佳 chunk size: {best.ChunkSize} chars（Recall@10={best.Recall10:P0}  MRR={best.Mrr:F3}）");
        }
        Console.WriteLine("=======================================================\n");
    }

    private sealed class ChunkAblationRow
    {
        public int ChunkSize { get; init; }
        public int TotalChunks { get; init; }
        public double AvgChunksPerItem { get; init; }
        public double Recall5 { get; init; }
        public double Recall10 { get; init; }
        public double Mrr { get; init; }
        public int SampleCount { get; init; }
    }

    // ── A5 §7.3 FileSystem VectorStore 查询延迟测试 ──────────────────
    // 写入阶段：直接序列化 JSONL（O(N)），绕过 UpsertAsync 的 O(N²) 读写
    private static async Task ExecuteFsVectorPerfAsync(int size, CancellationToken cancellationToken)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine($"    A5 §7.3  FileSystem VectorStore 查询延迟（N={size}）");
        Console.WriteLine("=======================================================");

        const int embDims = 384;
        const int topK = 10;
        const int queryCount = 20;
        const string workspaceId = "fs-vector-perf";
        const string collectionId = "eval";

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cc-fs-perf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        Console.WriteLine($"  临时目录: {tmpDir}");

        try
        {
            var fsOpts = new FileStorageOptions { RootPath = tmpDir };
            var vectorStore = new FileVectorStore(fsOpts);

            // 1. 直接写 JSONL（随机单位向量，O(N)）—— 测试 SearchAsync 速度，不测 UpsertAsync
            Console.Write($"  [写入] {size} 条随机单位向量 → JSONL (O(N))... ");
            var swWrite = Stopwatch.StartNew();
            var paths = new FilePathResolver(fsOpts);
            var jsonlPath = paths.GetVectorsJsonlPath(workspaceId, collectionId);
            Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);

            var jOpts2 = new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            var rng = new Random(42);
            await using (var fs2 = new FileStream(jsonlPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var sw2 = new StreamWriter(fs2, Encoding.UTF8, 65536))
            {
                for (var i = 0; i < size; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rawVec = new float[embDims];
                    double norm = 0;
                    for (var d = 0; d < embDims; d++) { rawVec[d] = (float)(rng.NextDouble() * 2 - 1); norm += rawVec[d] * (double)rawVec[d]; }
                    norm = Math.Sqrt(norm);
                    if (norm > 0) for (var d = 0; d < embDims; d++) rawVec[d] = (float)(rawVec[d] / norm);

                    var rec = new VectorRecord
                    {
                        Id = $"fs-{i}",
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        SourceId = $"src-{i}",
                        SourceKind = "context",
                        ModelName = EmbeddingModelPaths.DefaultModelName,
                        Dimensions = embDims,
                        Vector = rawVec,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await sw2.WriteLineAsync(JsonSerializer.Serialize(rec, jOpts2).AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
            swWrite.Stop();
            var fileSizeKb = new FileInfo(jsonlPath).Length / 1024.0;
            Console.WriteLine($"{swWrite.ElapsedMilliseconds} ms  ({fileSizeKb:F0} KB)");

            // 2. ONNX 预热 + 向量化 query
            Console.Write("  [ONNX] 预热 + 向量化 query... ");
            var embOpts = new EmbeddingOptions
            {
                ModelName = EmbeddingModelPaths.DefaultModelName,
                MaxBatchSize = 16,
                MaxSequenceLength = 256,
                OnnxIntraOpNumThreads = 1,
                OnnxInterOpNumThreads = 1,
                EnableContentHashCache = false,
                QueryInstruction = BgeQueryInstructions.BgeZhV15
            };
            var embManager = new OnnxEmbeddingSessionManager(embOpts);
            await embManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            var embProvider = new OnnxEmbeddingProvider(embOpts, embManager);

            var queryVectors = new List<IReadOnlyList<float>>(queryCount);
            for (var qi = 0; qi < queryCount; qi++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var qRes = await embProvider.EmbedAsync(new EmbeddingRequest
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    InputKind = EmbeddingInputKind.Query,
                    Inputs = [new EmbeddingInput { Id = $"q{qi}", Text = PerfTexts[qi % PerfTexts.Length], SourceRef = $"q{qi}" }]
                }, cancellationToken).ConfigureAwait(false);
                if (qRes.Succeeded && qRes.Vectors.Count > 0)
                    queryVectors.Add(qRes.Vectors[0].Values);
            }
            Console.WriteLine($"完成（{queryVectors.Count} 条 query）");

            // 3. 搜索（每次读 JSONL + 线性扫描）
            Console.Write($"  [搜索] {queryVectors.Count} 条 × SearchAsync... ");
            var latenciesMs = new List<double>(queryVectors.Count);
            foreach (var qVec in queryVectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var swQ = Stopwatch.StartNew();
                await vectorStore.SearchAsync(new VectorQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Vector = qVec,
                    TopK = topK
                }, cancellationToken).ConfigureAwait(false);
                swQ.Stop();
                latenciesMs.Add(swQ.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("完成");

            var sortedLat = latenciesMs.Order().ToArray();
            var p50 = Percentile(sortedLat, 50);
            var p95 = Percentile(sortedLat, 95);
            var p99 = Percentile(sortedLat, 99);
            var avg = latenciesMs.Average();

            Console.WriteLine("\n=======================================================");
            Console.WriteLine($"  FileSystem VectorStore 查询性能  N={size}  TopK={topK}");
            Console.WriteLine($"  JSONL 文件大小:  {fileSizeKb:F0} KB");
            Console.WriteLine($"  索引写入:        {swWrite.ElapsedMilliseconds} ms (O(N) 直写)");
            Console.WriteLine($"  查询 avg:        {avg:F1} ms  (读 JSONL + 线性扫)");
            Console.WriteLine($"  查询 p50:        {p50:F1} ms");
            Console.WriteLine($"  查询 p95:        {p95:F1} ms");
            Console.WriteLine($"  查询 p99:        {p99:F1} ms");
            Console.WriteLine("=======================================================");
            Console.WriteLine();
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 清理失败忽略 */ }
        }
    }

    // ── A5 §7.2 Idle Unload 延迟影响测试 ──────────────────────────────
    private static async Task ExecuteIdleUnloadAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("    A5 §7.2  Idle Unload 策略对首次请求延迟的影响");
        Console.WriteLine("=======================================================");

        const int warmReps = 3;
        const int coldReps = 3;

        var embOpts = new EmbeddingOptions
        {
            ModelName = EmbeddingModelPaths.DefaultModelName,
            MaxBatchSize = 16,
            MaxSequenceLength = 256,
            OnnxIntraOpNumThreads = 1,
            OnnxInterOpNumThreads = 1,
            EnableContentHashCache = false,
            QueryInstruction = BgeQueryInstructions.BgeZhV15
        };

        var warmLatencies = new List<double>(warmReps);
        var coldLatencies = new List<double>(coldReps);

        // ── 热路径：模型已加载，连续嵌入
        Console.Write("  [1/3] 热路径延迟（模型常驻）... ");
        var embManager = new OnnxEmbeddingSessionManager(embOpts);
        var embProvider = new OnnxEmbeddingProvider(embOpts, embManager);
        // 预热
        await embManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        for (var r = 0; r < warmReps; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            await embProvider.EmbedAsync(new EmbeddingRequest
            {
                WorkspaceId = "idle-unload",
                CollectionId = "eval",
                InputKind = EmbeddingInputKind.Query,
                Inputs = [new EmbeddingInput { Id = $"q{r}", Text = PerfTexts[r % PerfTexts.Length], SourceRef = $"q{r}" }]
            }, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            warmLatencies.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"完成（avg={warmLatencies.Average():F1} ms）");

        // ── 冷路径：ForceUnload 后首次请求（模拟 idle timeout 后重新激活）
        Console.Write("  [2/3] 冷路径延迟（ForceUnload 后首次请求）... ");
        for (var r = 0; r < coldReps; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 卸载
            await embManager.ForceUnloadAsync(cancellationToken).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            // 重新加载 + 嵌入（端到端首次请求）
            await embProvider.EmbedAsync(new EmbeddingRequest
            {
                WorkspaceId = "idle-unload",
                CollectionId = "eval",
                InputKind = EmbeddingInputKind.Query,
                Inputs = [new EmbeddingInput { Id = $"cold{r}", Text = PerfTexts[r % PerfTexts.Length], SourceRef = $"cold{r}" }]
            }, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            coldLatencies.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"完成（avg={coldLatencies.Average():F1} ms）");

        Console.Write("  [3/3] 整理结果... ");
        var warmAvg  = warmLatencies.Average();
        var coldAvg  = coldLatencies.Average();
        var coldMin  = coldLatencies.Min();
        var coldMax  = coldLatencies.Max();
        var overhead = coldAvg - warmAvg;
        Console.WriteLine("完成");

        Console.WriteLine("\n=======================================================");
        Console.WriteLine($"  热路径 avg:          {warmAvg,8:F1} ms  （模型已加载）");
        Console.WriteLine($"  冷路径 avg:          {coldAvg,8:F1} ms  （ForceUnload 后重载 + 嵌入）");
        Console.WriteLine($"  冷路径 min/max:      {coldMin,8:F1} / {coldMax:F1} ms");
        Console.WriteLine($"  首次请求额外开销:    {overhead,8:F1} ms  （≈ 模型重加载耗时）");
        Console.WriteLine("=======================================================");
        Console.WriteLine();
        Console.WriteLine("  结论：Idle Unload 策略节省内存，但首次请求会产生约");
        Console.WriteLine($"  {overhead:F0} ms 的重加载延迟。建议 IdleUnloadAfter ≥ 10 分钟，");
        Console.WriteLine("  在低频使用场景中可节省 ~56 MB WorkingSet。");
        Console.WriteLine();
    }
}
