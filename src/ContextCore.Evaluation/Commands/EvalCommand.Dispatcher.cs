using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Models;
using ContextCore.Evaluation.Runners;

namespace ContextCore.Evaluation.Commands;

/// <summary>Eval 子命令分发器（partial）：注册表驱动分发，替代原先的 if-chain。</summary>
public static partial class EvalCommand
{
    private static EvalSubcommandRegistry? s_registry;

    /// <summary>构建子命令注册表（惰性初始化）。所有子命令直接注册 handler，无需 if-chain。</summary>
    private static EvalSubcommandRegistry BuildSubcommandRegistry()
    {
        if (s_registry is not null)
        {
            return s_registry;
        }

        s_registry = new EvalSubcommandRegistry();

        // === run (默认) ===
        Reg("run", "  eval run [--category <name>] [--include-batches] [--out <path>]", ExecuteRunAsync);

        // === report ===
        Reg("report", "  eval report [<path>]", ExecuteReportDispatchAsync);

        // === perf ===
        Reg("perf", "  eval perf [--out <path.json>]",
            (service, args, sub, ct) => ExecutePerfAsync(
                CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o"), ct));

        // === perf-scale ===
        Reg("perf-scale", "  eval perf-scale [--size 1000] [--fake-vectors] [--out <path.json>]",
            ExecutePerfScaleDispatchAsync);

        // === retrieval ===
        Reg("retrieval", "  eval retrieval [--out <path.json>]", ExecuteRetrievalDispatchAsync);

        // === learning ===
        Reg("export-learning-features",
            "  eval export-learning-features [--out-dir <dir>] [--workspace <id>] [--collection <id>] [--eval-reports <csv>]",
            (service, args, sub, ct) => ExecuteExportLearningFeaturesAsync(service, args, ct));
        Reg("learning-baseline",
            "  eval learning-baseline --task router|ranker [--features-dir <dir>] [--out-dir <dir>]",
            (service, args, sub, ct) => ExecuteLearningBaselineAsync(sub, args, ct));
        Reg("learning-ranker-analysis",
            "  eval learning-ranker-analysis [--features-dir <dir>] [--out-dir <dir>]",
            (service, args, sub, ct) => ExecuteLearningRankerAnalysisAsync(sub, args, ct));

        // === graph / relation ===
        Reg("relation-expansion-profile-shadow",
            "  eval relation-expansion-profile-shadow [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteRelationExpansionProfileShadowAsync(args, ct));
        Reg("relation-corpus-hygiene",
            "  eval relation-corpus-hygiene [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteRelationCorpusHygieneAsync(args, ct));
        Reg("relation-expansion-shadow-eval",
            "  eval relation-expansion-shadow-eval [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteRelationExpansionShadowEvalAsync(args, ct));

        // === vector ===
        Reg("vector-reindex-plan",
            "  eval vector-reindex-plan [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--layers <csv>] [--item-kind <kind>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorReindexPlanAsync(service, args, ct));
        Reg("vector-reindex-apply",
            "  eval vector-reindex-apply --confirm [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--force] [--batch-size <n>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorReindexApplyAsync(service, args, ct));
        Reg("vector-index-diagnostics",
            "  eval vector-index-diagnostics [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorIndexDiagnosticsAsync(service, args, ct));
        Reg("vector-index-coverage",
            "  eval vector-index-coverage [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorIndexCoverageAsync(service, args, ct));
        Reg("vector-query-preview",
            "  eval vector-query-preview --query <text> [--profile <id>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorQueryPreviewAsync(service, args, ct));
        Reg("embedding-provider-smoke",
            "  eval embedding-provider-smoke [--provider deterministic-hash|onnx-local|qwen3] [--model-path <local.onnx>] [--tokenizer-path <vocab.txt|tokenizer.json>] [--dimension <n>] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteEmbeddingProviderSmokeAsync(args, ct));
        Reg("vector-provider-comparison",
            "  eval vector-provider-comparison [--providers current,qwen3] [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorProviderComparisonV310Async(service, args, ct));
        Reg("vector-retrieval-dataset-alignment-audit",
            "  eval vector-retrieval-dataset-alignment-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteVectorRetrievalDatasetAlignmentAuditAsync(service, args, sub, ct));

        // vector-lifecycle-metadata-review-batch-* (多别名同一 handler)
        var lifecycleBatchHandler = (EvalSubcommandHandler)((service, args, sub, ct) =>
            ExecuteVectorLifecycleMetadataReviewBatchAsync(service, args, sub, ct));
        Reg("vector-lifecycle-metadata-review-batch-create",
            "  eval vector-lifecycle-metadata-review-batch-create [--workspace <id>] [--collection <id>]", lifecycleBatchHandler);
        Reg("vector-lifecycle-metadata-review-batch-export",
            "  eval vector-lifecycle-metadata-review-batch-export [--workspace <id>] [--collection <id>] [--out <path.json>]", lifecycleBatchHandler);
        Reg("vector-lifecycle-metadata-review-batch-import",
            "  eval vector-lifecycle-metadata-review-batch-import --input <path.json> [--workspace <id>] [--collection <id>]", lifecycleBatchHandler);
        Reg("vector-lifecycle-metadata-review-batch-validate",
            "  eval vector-lifecycle-metadata-review-batch-validate --input <path.json>", lifecycleBatchHandler);
        Reg("vector-lifecycle-metadata-review-batch-apply-preview",
            "  eval vector-lifecycle-metadata-review-batch-apply-preview --input <path.json> [--workspace <id>] [--collection <id>]", lifecycleBatchHandler);
        Reg("vector-lifecycle-metadata-review-batch-import-smoke",
            "  eval vector-lifecycle-metadata-review-batch-import-smoke", lifecycleBatchHandler);

        // === retrieval-dataset-v2 ===
        var datasetV2GenHandler = (EvalSubcommandHandler)((service, args, sub, ct) =>
            ExecuteRetrievalDatasetV2GenerationAsync(service, args, sub, ct));
        Reg("retrieval-dataset-v2-generate", null, datasetV2GenHandler);
        Reg("retrieval-dataset-v2-validate", null, datasetV2GenHandler);
        Reg("retrieval-dataset-v2-quality", null, datasetV2GenHandler);
        Reg("retrieval-dataset-v2-materialization-gate", null, datasetV2GenHandler);
        Reg("retrieval-dataset-v2-shadow-eval", null,
            (service, args, sub, ct) => ExecuteRetrievalDatasetV2ShadowEvalAsync(args, sub, ct));

        // === foundation ===
        var foundationHandler = (EvalSubcommandHandler)((service, args, sub, ct) =>
            ExecuteFoundationFreezeAsync(sub, ct));
        Reg("foundation-freeze-report", "  eval foundation-freeze-report", foundationHandler);
        Reg("foundation-release-candidate-gate", "  eval foundation-release-candidate-gate", foundationHandler);

        // === service ===
        Reg("service-api-contract-report", "  eval service-api-contract-report [--production]",
            (service, args, sub, ct) => ExecuteServiceApiContractAsync(sub, args, ct));
        Reg("service-foundation-freeze-gate", "  eval service-foundation-freeze-gate",
            (service, args, sub, ct) => ExecuteServiceFoundationFreezeGateAsync(ct));

        // === storage ===
        Reg("storage-check", "  eval storage-check",
            (service, args, sub, ct) => ExecuteStorageCheckAsync(service, ct));
        Reg("storage-boundary-report", "  eval storage-boundary-report [--out <path.json>] [--md-out <path.md>]",
            (service, args, sub, ct) => ExecuteStorageBoundaryReportAsync(args, ct));

        return s_registry;
    }

    /// <summary>注册帮助方法：同时设置 name、usageLine 和 handler。</summary>
    private static void Reg(string name, string? usageLine, EvalSubcommandHandler handler)
    {
        s_registry!.Register(new EvalSubcommandEntry
        {
            Name = name,
            UsageLine = usageLine,
            Handler = handler
        });
    }

    /// <summary>打印 eval 用法信息。从注册表自动生成。</summary>
    public static void PrintUsage()
    {
        var registry = BuildSubcommandRegistry();
        Console.WriteLine("eval supports:");
        foreach (var entry in registry.GetAllEntries())
        {
            var line = entry.UsageLine ?? $"  eval {entry.Name}";
            Console.WriteLine(line);
        }
    }

    // === 分发包装方法（将原 if-chain 中的内联参数解析提取为独立方法）===

    private static async Task ExecuteRunAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
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

        RenderReportToConsole(report);

        var defaultLatestPath = Path.Combine(Directory.GetCurrentDirectory(), "eval", "eval-report-latest.json");
        await ExportReportAsync(report, defaultLatestPath, cancellationToken).ConfigureAwait(false);

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

    private static async Task ExecuteReportDispatchAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        string? reportPath = null;
        if (args.Count >= 2)
        {
            reportPath = args[1];
        }
        else
        {
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
    }

    private static async Task ExecutePerfScaleDispatchAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
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
    }

    private static async Task ExecuteRetrievalDispatchAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "eval-retrieval-report.json");
        await ExecuteRetrievalAsync(outputPath, cancellationToken).ConfigureAwait(false);
    }
}
