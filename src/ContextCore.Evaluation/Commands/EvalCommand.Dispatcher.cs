using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Contracts;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Evaluation.Commands;

/// <summary>Eval 子命令分发器（partial）：从 EvalCommand.cs 提取的命令注册、帮助文本和 if-chain 分发逻辑。</summary>
public static partial class EvalCommand
{
    private static EvalSubcommandRegistry? s_registry;

    /// <summary>构建子命令注册表（惰性初始化）。替代原先的 s_knownSubcommands HashSet。</summary>
    private static EvalSubcommandRegistry BuildSubcommandRegistry()
    {
        if (s_registry is not null)
        {
            return s_registry;
        }

        s_registry = new EvalSubcommandRegistry();
        s_registry.RegisterWithUsage("run", "  eval run [--category <name>] [--include-batches] [--out <path>]");
        s_registry.RegisterWithUsage("report", "  eval report [<path>]");
        s_registry.RegisterWithUsage("perf", "  eval perf [--out <path.json>]");
        s_registry.RegisterWithUsage("perf-scale", "  eval perf-scale [--size 1000] [--fake-vectors] [--out <path.json>]");
        s_registry.RegisterWithUsage("retrieval", "  eval retrieval [--out <path.json>]");
        s_registry.RegisterWithUsage("attention-profile-selection", "  eval attention-profile-selection [--baseline <path>] [--extended <path>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("planning-shadow", "  eval planning-shadow [--category <name>] [--include-batches] [--out <path.json>] [--triage-out <path.json>]");
        s_registry.RegisterWithUsage("export-learning-features", "  eval export-learning-features [--out-dir <dir>] [--workspace <id>] [--collection <id>] [--eval-reports <csv>] [--planning-shadow-reports <csv>]");
        s_registry.RegisterWithUsage("learning-baseline", "  eval learning-baseline --task router|ranker [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("graph-expansion-optin-comparison", "  eval graph-expansion-optin-comparison [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("graph-expansion-guarded-optin-gate", "  eval graph-expansion-guarded-optin-gate [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>] [--gate-out <path.json>] [--gate-md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-reindex-plan", "  eval vector-reindex-plan [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--layers <csv>] [--item-kind <kind>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-reindex-apply", "  eval vector-reindex-apply --confirm [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--force] [--batch-size <n>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-index-diagnostics", "  eval vector-index-diagnostics [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-index-coverage", "  eval vector-index-coverage [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-query-preview", "  eval vector-query-preview --query <text> [--profile <id>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("embedding-provider-smoke", "  eval embedding-provider-smoke [--provider deterministic-hash|onnx-local|qwen3] [--model-path <local.onnx>] [--tokenizer-path <vocab.txt|tokenizer.json>] [--dimension <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-provider-comparison", "  eval vector-provider-comparison [--providers current,qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-retrieval-dataset-alignment-audit", "  eval vector-retrieval-dataset-alignment-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-create", "  eval vector-lifecycle-metadata-review-batch-create [--workspace <id>] [--collection <id>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-export", "  eval vector-lifecycle-metadata-review-batch-export [--workspace <id>] [--collection <id>] [--out <path.json>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-import", "  eval vector-lifecycle-metadata-review-batch-import --input <path.json> [--workspace <id>] [--collection <id>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-validate", "  eval vector-lifecycle-metadata-review-batch-validate --input <path.json>");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-apply-preview", "  eval vector-lifecycle-metadata-review-batch-apply-preview --input <path.json> [--workspace <id>] [--collection <id>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-import-smoke", "  eval vector-lifecycle-metadata-review-batch-import-smoke");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-generate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-validate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-quality");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-materialization-gate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-shadow-eval");
        s_registry.RegisterWithUsage("foundation-freeze-report", "  eval foundation-freeze-report");
        s_registry.RegisterWithUsage("foundation-release-candidate-gate", "  eval foundation-release-candidate-gate");
        s_registry.RegisterWithUsage("service-api-contract-report", "  eval service-api-contract-report [--production]");
        s_registry.RegisterWithUsage("service-foundation-freeze-gate", "  eval service-foundation-freeze-gate");
        s_registry.RegisterWithUsage("relation-expansion-profile-shadow", "  eval relation-expansion-profile-shadow [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("relation-corpus-hygiene", "  eval relation-corpus-hygiene [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("relation-expansion-shadow-eval", "  eval relation-expansion-shadow-eval [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("learning-ranker-analysis", "  eval learning-ranker-analysis [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("storage-check", "  eval storage-check");
        s_registry.RegisterWithUsage("storage-boundary-report", "  eval storage-boundary-report [--out <path.json>] [--md-out <path.md>]");

        return s_registry;
    }

    /// <summary>打印 eval 用法信息。从注册表自动生成。</summary>
    private static void PrintUsage()
    {
        var registry = BuildSubcommandRegistry();
        Console.WriteLine("eval supports:");
        foreach (var entry in registry.GetAllEntries())
        {
            var line = entry.UsageLine ?? $"  eval {entry.Name}";
            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// 尝试分发子命令。返回 true 表示已处理，false 表示未匹配（调用方应执行默认 eval run）。
    /// 从 EvalCommand.ExecuteAsync 提取的 if-chain 分发逻辑。
    /// </summary>
    private static async Task<bool> TryDispatchSubcommandAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        if (string.Equals(subcommand, "storage-check", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteStorageCheckAsync(service, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "storage-boundary-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteStorageBoundaryReportAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-reindex-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorReindexPlanAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-reindex-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorReindexApplyAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-index-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorIndexDiagnosticsAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-index-coverage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorIndexCoverageAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-query-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "embedding-provider-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteEmbeddingProviderSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-provider-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorProviderComparisonV310Async(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRetrievalDatasetAlignmentAuditAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-create", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-export", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-apply-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewBatchAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-quality", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-materialization-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2GenerationAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2ShadowEvalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "foundation-freeze-report", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "foundation-release-candidate-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFoundationFreezeAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-api-contract-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceApiContractAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-foundation-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceFoundationFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "perf", StringComparison.OrdinalIgnoreCase))
        {
            var perfOutputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o");
            await ExecutePerfAsync(perfOutputPath, cancellationToken).ConfigureAwait(false);
            return true;
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
            return true;
        }

        if (string.Equals(subcommand, "retrieval", StringComparison.OrdinalIgnoreCase))
        {
            var retrievalOutputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "eval-retrieval-report.json");
            await ExecuteRetrievalAsync(retrievalOutputPath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "attention-profile-selection", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAttentionProfileSelectionAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "export-learning-features", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExportLearningFeaturesAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-baseline", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningBaselineAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningRankerAnalysisAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "graph-expansion-optin-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphExpansionOptInComparisonAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "graph-expansion-guarded-optin-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphExpansionGuardedOptInGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "relation-expansion-profile-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRelationExpansionProfileShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "relation-corpus-hygiene", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRelationCorpusHygieneAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "relation-expansion-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRelationExpansionShadowEvalAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
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
                return true;
            }

            Console.WriteLine($"[Eval] 正在加载并显示报告: {reportPath}");
            await DisplayLocalReportAsync(reportPath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
