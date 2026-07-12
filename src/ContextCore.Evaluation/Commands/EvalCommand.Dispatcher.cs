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
    /// <summary>鏋勫缓瀛愬懡浠ゆ敞鍐岃〃锛堟儼鎬у垵濮嬪寲锛夈€傛浛浠ｅ師鍏堢殑 s_knownSubcommands HashSet銆?/summary>
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
        s_registry.RegisterWithUsage("guarded-rerank-comparison", "  eval guarded-rerank-comparison [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]");
        s_registry.RegisterWithUsage("guarded-order-quality", "  eval guarded-order-quality [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]");
        s_registry.RegisterWithUsage("guarded-profile-sweep", "  eval guarded-profile-sweep [--category <name>] [--include-batches] [--out <path.json>]");
        s_registry.RegisterWithUsage("planning-shadow", "  eval planning-shadow [--category <name>] [--include-batches] [--out <path.json>] [--triage-out <path.json>]");
        s_registry.RegisterWithUsage("planning-shadow-quality", "  eval planning-shadow-quality [--category <name>] [--include-batches] [--out <path.json>]");
        s_registry.RegisterWithUsage("planning-shadow-recall-loss", "  eval planning-shadow-recall-loss [--category <name>] [--include-batches] [--out <path.json>]");
        s_registry.RegisterWithUsage("planning-optin-comparison", "  eval planning-optin-comparison [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--out <path.json>]");
        s_registry.RegisterWithUsage("planning-optin-fallback-analysis", "  eval planning-optin-fallback-analysis [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--candidate-intents <csv>] [--out <path.json>]");
        s_registry.RegisterWithUsage("planning-optin-constraint-safety", "  eval planning-optin-constraint-safety [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--candidate-intents <csv>] [--out <path.json>]");
        s_registry.RegisterWithUsage("extended-failure-triage", "  eval extended-failure-triage [--input <eval-report.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("export-learning-features", "  eval export-learning-features [--out-dir <dir>] [--workspace <id>] [--collection <id>] [--eval-reports <csv>] [--planning-shadow-reports <csv>]");
        s_registry.RegisterWithUsage("learning-dataset-quality", "  eval learning-dataset-quality [--features-dir <dir>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("router-intent-baseline", "  eval router-intent-baseline [--features-dir <dir>] [--input <path.jsonl>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("router-shadow-trace-quality", "  eval router-shadow-trace-quality [--workspace <id>] [--collection <id>] [--input <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("router-intent-shadow-eval", "  eval router-intent-shadow-eval [--input <path.jsonl>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("router-disagreement-triage", "  eval router-disagreement-triage [--input <path.jsonl>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("router-guarded-optin-readiness-gate", "  eval router-guarded-optin-readiness-gate [--out-dir <dir>] [--agreement-threshold <0..1>] [--low-confidence-max <n>]");
        s_registry.RegisterWithUsage("learning-readiness-freeze-report", "  eval learning-readiness-freeze-report [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-runtime-change-readiness-gate", "  eval learning-runtime-change-readiness-gate [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-feedback-summary", "  eval learning-feedback-summary [--workspace <id>] [--collection <id>] [--capability <id>] [--kind <kind>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("export-learning-feedback", "  eval export-learning-feedback [--workspace <id>] [--collection <id>] [--capability <id>] [--kind <kind>] [--out <path.jsonl>]");
        s_registry.RegisterWithUsage("learning-feedback-review-summary", "  eval learning-feedback-review-summary [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("learning-feedback-feature-candidates", "  eval learning-feedback-feature-candidates [--workspace <id>] [--collection <id>] [--capability <id>] [--kind <kind>] [--out <path.jsonl>] [--md-out <path.md>] [--report-out <path.json>]");
        s_registry.RegisterWithUsage("learning-feedback-quality", "  eval learning-feedback-quality [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("learning-feedback-review-smoke", "  eval learning-feedback-review-smoke [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("learning-approved-feedback-dataset-gate", "  eval learning-approved-feedback-dataset-gate [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("submit-learning-feedback", "  eval submit-learning-feedback --capability <id> --target-type <type> --target-id <id> --kind <kind> [--source-operation-id <id>] [--reason <text>] [--metadata-only true|false]");
        s_registry.RegisterWithUsage("learning-feedback-smoke", "  eval learning-feedback-smoke [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("learning-baseline", "  eval learning-baseline --task router|ranker [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-baseline-router", "  eval learning-baseline-router [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-baseline-ranker", "  eval learning-baseline-ranker [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-ranker-ablation", "  eval learning-ranker-ablation [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-ranker-weight-sweep", "  eval learning-ranker-weight-sweep [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-ranker-residual-audit", "  eval learning-ranker-residual-audit [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-hard-negatives", "  eval learning-hard-negatives [--residual-audit <path>] [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("learning-lifecycle-aware-ranker", "  eval learning-lifecycle-aware-ranker [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("lifecycle-ranker-shadow", "  eval lifecycle-ranker-shadow [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]");
        s_registry.RegisterWithUsage("ranker-shadow-trace-quality", "  eval ranker-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("candidate-reranker-feature-completeness", "  eval candidate-reranker-feature-completeness [--out-dir <dir>]");
        s_registry.RegisterWithUsage("candidate-reranker-shadow-eval", "  eval candidate-reranker-shadow-eval [--out-dir <dir>] [--top-k <n>]");
        s_registry.RegisterWithUsage("candidate-reranker-shadow-failure-audit", "  eval candidate-reranker-shadow-failure-audit [--out-dir <dir>] [--top-k <n>]");
        s_registry.RegisterWithUsage("candidate-reranker-score-distribution", "  eval candidate-reranker-score-distribution [--out-dir <dir>] [--top-k <n>]");
        s_registry.RegisterWithUsage("candidate-reranker-listwise-calibration", "  eval candidate-reranker-listwise-calibration [--out-dir <dir>] [--top-k <n>]");
        s_registry.RegisterWithUsage("candidate-reranker-formal-priority-alignment", "  eval candidate-reranker-formal-priority-alignment [--out-dir <dir>] [--top-k <n>]");
        s_registry.RegisterWithUsage("candidate-reranker-shadow-trace-quality", "  eval candidate-reranker-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("graph-expansion-shadow-trace-quality", "  eval graph-expansion-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("graph-expansion-optin-comparison", "  eval graph-expansion-optin-comparison [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("graph-expansion-guarded-optin-gate", "  eval graph-expansion-guarded-optin-gate [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>] [--gate-out <path.json>] [--gate-md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-reindex-plan", "  eval vector-reindex-plan [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--layers <csv>] [--item-kind <kind>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-reindex-apply", "  eval vector-reindex-apply --confirm [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--force] [--batch-size <n>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-index-diagnostics", "  eval vector-index-diagnostics [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-index-coverage", "  eval vector-index-coverage [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-coverage", "  eval vector-lifecycle-metadata-coverage [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-backfill-plan", "  eval vector-lifecycle-metadata-backfill-plan [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-backfill-apply", "  eval vector-lifecycle-metadata-backfill-apply --confirm [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-query-preview", "  eval vector-query-preview --query <text> [--profile <id>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-query-shadow-eval", "  eval vector-query-shadow-eval [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-query-profile-sweep", "  eval vector-query-profile-sweep [--category <name>] [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--provider-type DeterministicHash|OnnxLocal] [--model-path <local.onnx>] [--tokenizer-path <vocab.txt>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-residual-risk-audit", "  eval vector-residual-risk-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-recall-loss-audit", "  eval vector-recall-loss-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-safe-recall-recovery", "  eval vector-safe-recall-recovery [--category <name>] [--provider deterministic-hash|onnx-local] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-ranker-fusion-shadow", "  eval vector-ranker-fusion-shadow [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-representation-benchmark", "  eval vector-representation-benchmark [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--audit-out-a3 <path.json>] [--audit-out-extended <path.json>] [--md-out <path.md>] [--audit-md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-query-expansion-shadow", "  eval vector-query-expansion-shadow [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-retrieval-shadow-readiness-gate", "  eval vector-retrieval-shadow-readiness-gate [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("embedding-provider-smoke", "  eval embedding-provider-smoke [--provider deterministic-hash|onnx-local|qwen3] [--model-path <local.onnx>] [--tokenizer-path <vocab.txt|tokenizer.json>] [--dimension <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-provider-comparison", "  eval vector-provider-comparison [--providers current,qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-qwen3-shadow-eval", "  eval vector-qwen3-shadow-eval [--category <name>] [--profile <id>] [--top-k <n>]");
        s_registry.RegisterWithUsage("vector-qwen3-readiness-gate", "  eval vector-qwen3-readiness-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-provider-configuration-sanity-audit", "  eval vector-provider-configuration-sanity-audit [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-provider-comparison-freeze", "  eval vector-provider-comparison-freeze [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-hybrid-preview", "  eval vector-hybrid-preview [--category <name>] [--profile <id>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-hybrid-shadow-eval", "  eval vector-hybrid-shadow-eval [--category <name>] [--profile <id>] [--top-k <n>]");
        s_registry.RegisterWithUsage("vector-hybrid-readiness-gate", "  eval vector-hybrid-readiness-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-hybrid-recall-regression-audit", "  eval vector-hybrid-recall-regression-audit [--category <name>] [--profile <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-hybrid-freeze-gate", "  eval vector-hybrid-freeze-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-retrieval-dataset-alignment-audit", "  eval vector-retrieval-dataset-alignment-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-retrieval-dataset-alignment-audit-a3", "  eval vector-retrieval-dataset-alignment-audit-a3 [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-retrieval-dataset-alignment-audit-extended", "  eval vector-retrieval-dataset-alignment-audit-extended [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-eligibility-recall-loss-triage", "  eval vector-eligibility-recall-loss-triage [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-eligibility-recall-loss-triage-a3", "  eval vector-eligibility-recall-loss-triage-a3 [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-eligibility-recall-loss-triage-extended", "  eval vector-eligibility-recall-loss-triage-extended [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-repair-plan", "  eval vector-lifecycle-metadata-repair-plan [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-repair-plan-a3", "  eval vector-lifecycle-metadata-repair-plan-a3 [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-repair-plan-extended", "  eval vector-lifecycle-metadata-repair-plan-extended [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-candidates-generate", "  eval vector-lifecycle-metadata-review-candidates-generate [--workspace <id>] [--collection <id>] [--repair-plan <vector/eligibility/*.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-candidates", "  eval vector-lifecycle-metadata-review-candidates [--workspace <id>] [--collection <id>] [--status <name>] [--layer <name>] [--item-kind <name>] [--must-hit <id>] [--source-eval-set <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-summary", "  eval vector-lifecycle-metadata-review-summary [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-sidecar-preview", "  eval vector-lifecycle-metadata-sidecar-preview [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-smoke", "  eval vector-lifecycle-metadata-review-smoke [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-sidecar-eligibility-preview", "  eval vector-sidecar-eligibility-preview [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-sidecar-eligibility-recheck", "  eval vector-sidecar-eligibility-recheck [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-sidecar-eligibility-quality", "  eval vector-sidecar-eligibility-quality [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-create", "  eval vector-lifecycle-metadata-review-batch-create [--workspace <id>] [--collection <id>] [--created-by <name>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-export", "  eval vector-lifecycle-metadata-review-batch-export [--batch-id <id>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-import", "  eval vector-lifecycle-metadata-review-batch-import [--batch-id <id>] [--input <review-sheet.jsonl>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-validate", "  eval vector-lifecycle-metadata-review-batch-validate [--batch-id <id>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-apply-preview", "  eval vector-lifecycle-metadata-review-batch-apply-preview [--batch-id <id>]");
        s_registry.RegisterWithUsage("vector-lifecycle-metadata-review-batch-import-smoke", "  eval vector-lifecycle-metadata-review-batch-import-smoke");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-evidence-backfill-preview");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-evidence-backfill-audit");
        s_registry.RegisterCommandOnly("vector-retrieval-dataset-v2-contract");
        s_registry.RegisterCommandOnly("vector-retrieval-dataset-v2-validator");
        s_registry.RegisterCommandOnly("vector-legacy-dataset-limitation-report");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-generate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-validate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-quality");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-materialization-gate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-shadow-eval");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-dense-shadow-eval");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-hybrid-shadow-eval");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-readiness-gate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-generate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-leakage-audit");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-anchor-dominance-audit");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-shadow-eval");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-readiness-gate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-failure-triage");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-failure-triage-holdout");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-failure-clusters");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-hybrid-scoring-repair-preview");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-hybrid-scoring-repair-gate");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-hybrid-scoring-risk-triage");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout");
        s_registry.RegisterCommandOnly("retrieval-dataset-v2-stress-freeze-gate");
        s_registry.RegisterCommandOnly("vector-shadow-package-comparison");
        s_registry.RegisterCommandOnly("vector-shadow-package-comparison-gate");
        s_registry.RegisterCommandOnly("vector-formal-preview-freeze-gate");
        s_registry.RegisterWithUsage("vector-formal-retrieval-integration-plan", "  eval vector-formal-retrieval-integration-plan");
        s_registry.RegisterWithUsage("vector-formal-retrieval-integration-plan-gate", "  eval vector-formal-retrieval-integration-plan-gate");
        s_registry.RegisterWithUsage("vector-shadow-formal-retrieval-adapter-plan", "  eval vector-shadow-formal-retrieval-adapter-plan");
        s_registry.RegisterWithUsage("vector-shadow-formal-retrieval-adapter-plan-gate", "  eval vector-shadow-formal-retrieval-adapter-plan-gate");
        s_registry.RegisterWithUsage("vector-shadow-formal-retrieval-adapter", "  eval vector-shadow-formal-retrieval-adapter");
        s_registry.RegisterWithUsage("vector-shadow-formal-retrieval-adapter-gate", "  eval vector-shadow-formal-retrieval-adapter-gate");
        s_registry.RegisterWithUsage("vector-formal-adapter-package-shadow-comparison", "  eval vector-formal-adapter-package-shadow-comparison");
        s_registry.RegisterWithUsage("vector-formal-adapter-package-shadow-comparison-gate", "  eval vector-formal-adapter-package-shadow-comparison-gate");
        s_registry.RegisterWithUsage("vector-graph-retrieval-quality-audit", "  eval vector-graph-retrieval-quality-audit");
        s_registry.RegisterWithUsage("vector-graph-retrieval-quality-gate", "  eval vector-graph-retrieval-quality-gate");
        s_registry.RegisterWithUsage("vector-retrieval-quality-repair-preview", "  eval vector-retrieval-quality-repair-preview");
        s_registry.RegisterWithUsage("vector-retrieval-quality-repair-gate", "  eval vector-retrieval-quality-repair-gate");
        s_registry.RegisterWithUsage("vector-runtime-observable-feature-contract", "  eval vector-runtime-observable-feature-contract");
        s_registry.RegisterWithUsage("vector-runtime-observable-feature-contract-gate", "  eval vector-runtime-observable-feature-contract-gate");
        s_registry.RegisterWithUsage("vector-runtime-feature-derivation-preview", "  eval vector-runtime-feature-derivation-preview");
        s_registry.RegisterWithUsage("vector-runtime-feature-derivation-gate", "  eval vector-runtime-feature-derivation-gate");
        s_registry.RegisterWithUsage("vector-runtime-feature-derivation-repair", "  eval vector-runtime-feature-derivation-repair");
        s_registry.RegisterWithUsage("vector-runtime-feature-derivation-repair-gate", "  eval vector-runtime-feature-derivation-repair-gate");
        s_registry.RegisterWithUsage("vector-runtime-feature-derivation-failure-freeze", "  eval vector-runtime-feature-derivation-failure-freeze");
        s_registry.RegisterWithUsage("vector-query-driven-candidate-source-repair", "  eval vector-query-driven-candidate-source-repair");
        s_registry.RegisterWithUsage("vector-query-driven-candidate-source-repair-gate", "  eval vector-query-driven-candidate-source-repair-gate");
        s_registry.RegisterWithUsage("vector-formal-retrieval-integration-freeze", "  eval vector-formal-retrieval-integration-freeze");
        s_registry.RegisterWithUsage("vector-formal-retrieval-integration-freeze-gate", "  eval vector-formal-retrieval-integration-freeze-gate");
        s_registry.RegisterWithUsage("architecture-cleanup-readiness-gate", "  eval architecture-cleanup-readiness-gate");
        s_registry.RegisterWithUsage("architecture-cleanup-freeze", "  eval architecture-cleanup-freeze");
        s_registry.RegisterWithUsage("architecture-cleanup-freeze-gate", "  eval architecture-cleanup-freeze-gate");
        s_registry.RegisterWithUsage("dto-split-plan", "  eval dto-split-plan");
        s_registry.RegisterWithUsage("dto-split-readiness-gate", "  eval dto-split-readiness-gate");
        s_registry.RegisterWithUsage("vector-retrieval-eval-protocol-audit", "  eval vector-retrieval-eval-protocol-audit");
        s_registry.RegisterWithUsage("vector-candidate-source-discriminability-audit", "  eval vector-candidate-source-discriminability-audit");
        s_registry.RegisterWithUsage("vector-retrieval-eval-protocol-gate", "  eval vector-retrieval-eval-protocol-gate");
        s_registry.RegisterWithUsage("vector-input-metadata-enrichment-preview", "  eval vector-input-metadata-enrichment-preview");
        s_registry.RegisterWithUsage("vector-input-metadata-enrichment-gate", "  eval vector-input-metadata-enrichment-gate");
        s_registry.RegisterWithUsage("vector-enriched-candidate-source-repair-recheck", "  eval vector-enriched-candidate-source-repair-recheck");
        s_registry.RegisterWithUsage("vector-enriched-candidate-source-repair-recheck-gate", "  eval vector-enriched-candidate-source-repair-recheck-gate");
        s_registry.RegisterWithUsage("vector-source-aware-ranking-repair", "  eval vector-source-aware-ranking-repair");
        s_registry.RegisterWithUsage("vector-source-aware-ranking-repair-gate", "  eval vector-source-aware-ranking-repair-gate");
        s_registry.RegisterWithUsage("vector-output-token-priority-shadow", "  eval vector-output-token-priority-shadow");
        s_registry.RegisterWithUsage("vector-output-token-priority-shadow-gate", "  eval vector-output-token-priority-shadow-gate");
        s_registry.RegisterWithUsage("vector-formal-retrieval-integration-decision", "  eval vector-formal-retrieval-integration-decision");
        s_registry.RegisterWithUsage("vector-formal-retrieval-integration-decision-gate", "  eval vector-formal-retrieval-integration-decision-gate");
        s_registry.RegisterWithUsage("project-state-audit", "  eval project-state-audit");
        s_registry.RegisterWithUsage("mainline-gap-map", "  eval mainline-gap-map");
        s_registry.RegisterWithUsage("generated-artifact-path-hygiene-audit", "  eval generated-artifact-path-hygiene-audit [--scan-dir <dir>]");
        s_registry.RegisterWithUsage("generated-artifact-path-hygiene-gate", "  eval generated-artifact-path-hygiene-gate [--scan-dir <dir>]");
        s_registry.RegisterWithUsage("foundation-freeze-report", "  eval foundation-freeze-report");
        s_registry.RegisterWithUsage("foundation-release-candidate-gate", "  eval foundation-release-candidate-gate");
        s_registry.RegisterWithUsage("foundation-reproducibility-check", "  eval foundation-reproducibility-check");
        s_registry.RegisterWithUsage("service-foundation-status-smoke", "  eval service-foundation-status-smoke");
        s_registry.RegisterWithUsage("service-readiness-api-smoke", "  eval service-readiness-api-smoke");
        s_registry.RegisterWithUsage("service-api-security-diagnostics", "  eval service-api-security-diagnostics");
        s_registry.RegisterWithUsage("service-report-navigation-smoke", "  eval service-report-navigation-smoke");
        s_registry.RegisterWithUsage("service-api-contract-report", "  eval service-api-contract-report [--production]");
        s_registry.RegisterWithUsage("service-api-contract-freeze-gate", "  eval service-api-contract-freeze-gate [--production]");
        s_registry.RegisterWithUsage("service-auth-diagnostics", "  eval service-auth-diagnostics [--profile development|service|production]");
        s_registry.RegisterWithUsage("service-auth-enforcement-smoke", "  eval service-auth-enforcement-smoke");
        s_registry.RegisterWithUsage("service-deployment-profile-gate", "  eval service-deployment-profile-gate [--profile development|service|production]");
        s_registry.RegisterWithUsage("service-openapi-contract-export", "  eval service-openapi-contract-export");
        s_registry.RegisterWithUsage("service-client-contract-snapshot", "  eval service-client-contract-snapshot");
        s_registry.RegisterWithUsage("service-api-contract-drift-gate", "  eval service-api-contract-drift-gate");
        s_registry.RegisterWithUsage("service-hosted-deployment-smoke", "  eval service-hosted-deployment-smoke [--base-url <url>] [--profile development|service|production]");
        s_registry.RegisterWithUsage("service-readonly-runtime-smoke", "  eval service-readonly-runtime-smoke [--base-url <url>] [--profile development|service|production]");
        s_registry.RegisterWithUsage("service-hosted-api-contract-smoke", "  eval service-hosted-api-contract-smoke [--base-url <url>] [--profile development|service|production]");
        s_registry.RegisterWithUsage("service-foundation-freeze-gate", "  eval service-foundation-freeze-gate");
        s_registry.RegisterWithUsage("relation-expansion-profile-shadow", "  eval relation-expansion-profile-shadow [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("relation-corpus-hygiene", "  eval relation-corpus-hygiene [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("relation-expansion-shadow-eval", "  eval relation-expansion-shadow-eval [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("learning-ranker-analysis", "  eval learning-ranker-analysis [--features-dir <dir>] [--out-dir <dir>]");
        s_registry.RegisterWithUsage("storage-check", "  eval storage-check");
        s_registry.RegisterWithUsage("storage-boundary-report", "  eval storage-boundary-report [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-storage-diagnostics", "  eval postgres-storage-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-migration-preview", "  eval postgres-migration-preview [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-migration-apply", "  eval postgres-migration-apply --confirm [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-migration-smoke", "  eval postgres-migration-smoke [--confirm] [--drop-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-store-diagnostics", "  eval postgres-relation-store-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-store-parity", "  eval postgres-relation-store-parity [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-review-diagnostics", "  eval postgres-relation-review-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-review-parity", "  eval postgres-relation-review-parity [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-governance-parity", "  eval postgres-relation-governance-parity [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-governance-readiness-gate", "  eval postgres-relation-governance-readiness-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-dual-write-smoke", "  eval postgres-relation-dual-write-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-dual-write-quality", "  eval postgres-relation-dual-write-quality [--input <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-shadow-read-smoke", "  eval postgres-relation-shadow-read-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-shadow-read-quality", "  eval postgres-relation-shadow-read-quality [--input <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-provider-switch-smoke", "  eval postgres-relation-provider-switch-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-provider-switch-gate", "  eval postgres-relation-provider-switch-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-runtime-canary", "  eval postgres-relation-runtime-canary [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-service-mode-smoke", "  eval postgres-relation-scoped-service-mode-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-service-mode-gate", "  eval postgres-relation-scoped-service-mode-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-extended-canary", "  eval postgres-relation-scoped-extended-canary --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-selected-workspace-canary", "  eval postgres-relation-selected-workspace-canary [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--max-operations <n>] [--observation-window-minutes <n>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-expansion-plan", "  eval postgres-relation-scoped-expansion-plan [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-expansion-smoke", "  eval postgres-relation-scoped-expansion-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-expansion-gate", "  eval postgres-relation-scoped-expansion-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-observation-window", "  eval postgres-relation-scoped-observation-window [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--observation-window-minutes <n>] [--operation-interval-seconds <n>] [--max-operations <n>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-scoped-observation-quality", "  eval postgres-relation-scoped-observation-quality [--p95-threshold-ms <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-selected-normal-workspace-canary", "  eval postgres-relation-selected-normal-workspace-canary [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--max-operations <n>] [--observation-window-minutes <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--cleanup-confirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-limited-normal-scope-observation", "  eval postgres-relation-limited-normal-scope-observation [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--max-operations <n>] [--observation-window-minutes <n>] [--operation-interval-seconds <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--cleanup-confirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-limited-normal-scope-quality", "  eval postgres-relation-limited-normal-scope-quality [--fallback-rate-threshold <0..1>] [--p95-threshold-ms <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-relation-multi-normal-scope-canary", "  eval postgres-relation-multi-normal-scope-canary [--connection-string <value>] [--schema <name>] [--max-operations-per-scope <n>] [--observation-window-minutes <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--cleanup-confirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-relation-multi-normal-scope-quality", "  eval postgres-relation-multi-normal-scope-quality [--p95-threshold-ms <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-diagnostics", "  eval postgres-learning-feedback-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-parity", "  eval postgres-learning-feedback-parity --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-readiness-gate", "  eval postgres-learning-feedback-readiness-gate [--connection-string <value>] [--schema <name>] [--diagnostics <path.json>] [--parity <path.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-dual-write-smoke", "  eval postgres-learning-feedback-dual-write-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-shadow-read-smoke", "  eval postgres-learning-feedback-shadow-read-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-provider-quality", "  eval postgres-learning-feedback-provider-quality [--dual-traces <path.jsonl>] [--shadow-traces <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-scoped-service-mode-smoke", "  eval postgres-learning-feedback-scoped-service-mode-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-scoped-service-mode-gate", "  eval postgres-learning-feedback-scoped-service-mode-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-selected-normal-scope-canary", "  eval postgres-learning-feedback-selected-normal-scope-canary [--workspace <id>] [--collection <id>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-limited-scope-observation", "  eval postgres-learning-feedback-limited-scope-observation [--workspace <id>] [--collection <id>] [--observation-window-minutes <n>] [--max-operations <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-limited-scope-quality", "  eval postgres-learning-feedback-limited-scope-quality [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-learning-feedback-freeze-gate", "  eval postgres-learning-feedback-freeze-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-diagnostics", "  eval postgres-job-queue-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-parity", "  eval postgres-job-queue-parity --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-lease-smoke", "  eval postgres-job-queue-lease-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-dual-write-smoke", "  eval postgres-job-queue-dual-write-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-job-queue-shadow-read-smoke", "  eval postgres-job-queue-shadow-read-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-job-queue-provider-quality", "  eval postgres-job-queue-provider-quality [--dual <path.json>] [--shadow <path.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-scoped-worker-canary", "  eval postgres-job-queue-scoped-worker-canary --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--quality <path.json>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-job-queue-scoped-worker-quality", "  eval postgres-job-queue-scoped-worker-quality [--canary <path.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-limited-worker-scope-observation", "  eval postgres-job-queue-limited-worker-scope-observation --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--quality <path.json>] [--observation-window-seconds <n>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
        s_registry.RegisterWithUsage("postgres-job-queue-limited-worker-scope-quality", "  eval postgres-job-queue-limited-worker-scope-quality [--observation <path.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-job-queue-freeze-gate", "  eval postgres-job-queue-freeze-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-diagnostics", "  eval postgres-vector-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-compatibility", "  eval postgres-vector-compatibility [--provider <id>] [--model <id>] [--dimension <n>] [--normalized true|false] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-provider-smoke", "  eval postgres-vector-provider-smoke --cleanup-confirm [--provider <id>] [--model <id>] [--dimension <n>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-parity", "  eval postgres-vector-parity --cleanup-confirm [--provider <id>] [--model <id>] [--dimension <n>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-provider-scoped-reindex-plan", "  eval postgres-vector-provider-scoped-reindex-plan [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--source-kind <kind>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-provider-scoped-reindex-apply", "  eval postgres-vector-provider-scoped-reindex-apply --confirm [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--source-kind <kind>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-provider-scoped-reindex-quality", "  eval postgres-vector-provider-scoped-reindex-quality [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--source-kind <kind>] [--apply-report <path.json>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-query-preview", "  eval postgres-vector-query-preview [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--max-queries <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-shadow-eval", "  eval postgres-vector-shadow-eval [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--max-queries <n>] [--out-summary <path.json>] [--summary-md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-shadow-eval-a3", "  eval postgres-vector-shadow-eval-a3 [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-shadow-eval-extended", "  eval postgres-vector-shadow-eval-extended [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("postgres-vector-freeze-gate", "  eval postgres-vector-freeze-gate [--out <path.json>] [--md-out <path.md>]");
        s_registry.RegisterWithUsage("chunk-ablation", "  eval chunk-ablation");
        s_registry.RegisterWithUsage("idle-unload", "  eval idle-unload");
        s_registry.RegisterWithUsage("fs-vector-perf", "  eval fs-vector-perf [--size 1000]");

        return s_registry;
    }

    /// <summary>打印 eval 用法信息。从 EvalCommand.ExecuteAsync 提取。</summary>
    /// <summary>鎵撳嵃 usage 淇℃伅銆備粠娉ㄥ唽琛ㄨ嚜鍔ㄧ敓鎴愶紝涓嶅啀纭紪鐮併€?/summary>
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
        if (string.Equals(subcommand, "chunk-ablation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteChunkAblationAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "idle-unload", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteIdleUnloadAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "fs-vector-perf", StringComparison.OrdinalIgnoreCase))
        {
            var fsSize = 1000;
            var fsSizeArg = CommandHelpers.GetOption(args, "--size") ?? CommandHelpers.GetOption(args, "-n");
            if (!string.IsNullOrEmpty(fsSizeArg) && int.TryParse(fsSizeArg, out var parsedFsSize) && parsedFsSize > 0)
                fsSize = parsedFsSize;
            await ExecuteFsVectorPerfAsync(fsSize, cancellationToken).ConfigureAwait(false);
            return true;
        }

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

        if (string.Equals(subcommand, "postgres-storage-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresStorageDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-migration-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresMigrationPreviewAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-migration-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresMigrationApplyAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-migration-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresMigrationSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-store-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationStoreDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-store-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationStoreParityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-review-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationReviewDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-review-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationReviewParityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-governance-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationGovernanceParityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-governance-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationGovernanceReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-dual-write-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationDualWriteSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-dual-write-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationDualWriteQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-shadow-read-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationShadowReadSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-shadow-read-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationShadowReadQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-provider-switch-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationProviderSwitchSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-provider-switch-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationProviderSwitchGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-runtime-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationRuntimeCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-service-mode-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedServiceModeSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-service-mode-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedServiceModeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-extended-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExtendedCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-selected-workspace-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationSelectedWorkspaceCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-expansion-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExpansionPlanAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-expansion-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExpansionSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-expansion-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExpansionGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-observation-window", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedObservationWindowAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-observation-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedObservationQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-selected-normal-workspace-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationSelectedNormalWorkspaceCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-limited-normal-scope-observation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationLimitedNormalScopeObservationAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-limited-normal-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationLimitedNormalScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-multi-normal-scope-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationMultiNormalScopeCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-relation-multi-normal-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationMultiNormalScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackParityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-dual-write-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackDualWriteSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-shadow-read-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackShadowReadSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-provider-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackProviderQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-scoped-service-mode-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackScopedServiceModeSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-scoped-service-mode-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackScopedServiceModeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-selected-normal-scope-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackSelectedNormalScopeCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-limited-scope-observation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackLimitedScopeObservationAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-limited-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackLimitedScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueParityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-lease-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueLeaseSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-dual-write-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueDualWriteSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-shadow-read-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueShadowReadSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-provider-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueProviderQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-scoped-worker-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueScopedWorkerCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-scoped-worker-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueScopedWorkerQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-limited-worker-scope-observation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueLimitedWorkerScopeObservationAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-limited-worker-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueLimitedWorkerScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-job-queue-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-compatibility", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorCompatibilityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorParityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderScopedReindexPlanAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderScopedReindexApplyAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderScopedReindexQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-query-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorQueryPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "postgres-vector-shadow-eval-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "postgres-vector-shadow-eval-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorShadowEvalAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "postgres-vector-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "vector-lifecycle-metadata-coverage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataCoverageAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-backfill-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataBackfillAsync(service, args, apply: false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-backfill-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataBackfillAsync(service, args, apply: true, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-query-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-query-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryShadowEvalAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-query-profile-sweep", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryProfileSweepAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-residual-risk-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorResidualRiskAuditAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-recall-loss-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRecallLossAuditAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-safe-recall-recovery", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorSafeRecallRecoveryAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-ranker-fusion-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRankerFusionShadowAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-representation-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRepresentationBenchmarkAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-query-expansion-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryExpansionShadowAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-retrieval-shadow-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRetrievalShadowReadinessGateAsync(service, args, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "vector-qwen3-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQwen3ShadowEvalAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-qwen3-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQwen3ReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-provider-configuration-sanity-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorProviderConfigurationSanityAuditAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-provider-comparison-freeze", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteEmbeddingProviderComparisonFreezeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-hybrid-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridShadowEvalAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-hybrid-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-hybrid-recall-regression-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridRecallRegressionAuditAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-hybrid-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRetrievalDatasetAlignmentAuditAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-eligibility-recall-loss-triage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-eligibility-recall-loss-triage-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-eligibility-recall-loss-triage-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorEligibilityRecallLossTriageAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataRepairPlanAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates-generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewCandidatesAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-summary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-sidecar-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-sidecar-eligibility-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-sidecar-eligibility-recheck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-sidecar-eligibility-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorSidecarEligibilityPreviewAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataEvidenceBackfillAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-retrieval-dataset-v2-contract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-dataset-v2-validator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-legacy-dataset-limitation-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2MetadataContractAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-dense-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2ShadowEvalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-leakage-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-anchor-dominance-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2StressAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage-holdout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-clusters", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2StressFailureTriageAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2HybridScoringRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2HybridScoringRiskTriageAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2StressFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-shadow-package-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-package-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorShadowPackageComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-formal-preview-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorFormalPreviewFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-formal-retrieval-integration-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-retrieval-integration-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationPlanAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteShadowFormalRetrievalAdapterPlanAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteShadowFormalRetrievalAdapterAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalAdapterPackageShadowComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-graph-retrieval-quality-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-graph-retrieval-quality-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphVectorRetrievalQualityAuditAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-retrieval-quality-repair-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-quality-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalQualityRepairPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-runtime-observable-feature-contract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-runtime-observable-feature-contract-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeObservableFeatureContractAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-runtime-feature-derivation-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-runtime-feature-derivation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeFeatureDerivationPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-runtime-feature-derivation-repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-runtime-feature-derivation-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeFeatureDerivationRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-runtime-feature-derivation-failure-freeze", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeFeatureDerivationFailureFreezeAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-query-driven-candidate-source-repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-query-driven-candidate-source-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteQueryDrivenCandidateSourceRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-formal-retrieval-integration-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-retrieval-integration-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "architecture-cleanup-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupReadinessGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "architecture-cleanup-freeze", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupFreezeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "architecture-cleanup-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "dto-split-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "dto-split-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteDtoSplitPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-retrieval-eval-protocol-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-candidate-source-discriminability-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-eval-protocol-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalEvalProtocolAuditAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-input-metadata-enrichment-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-input-metadata-enrichment-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteInputMetadataEnrichmentPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteEnrichedCandidateSourceRepairRecheckAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-source-aware-ranking-repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-source-aware-ranking-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSourceAwareRankingRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-output-token-priority-shadow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-output-token-priority-shadow-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteOutputTokenPriorityShadowAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-formal-retrieval-integration-decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-retrieval-integration-decision-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationDecisionAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "project-state-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "mainline-gap-map", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteProjectStateAuditAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "foundation-freeze-report", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "foundation-release-candidate-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFoundationFreezeAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "foundation-reproducibility-check", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFoundationReproducibilityCheckAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-foundation-status-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-readiness-api-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceFoundationStatusSmokeAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-api-security-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceApiSecurityDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-report-navigation-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceReportNavigationSmokeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-api-contract-report", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-api-contract-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceApiContractAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-auth-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceAuthDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-auth-enforcement-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceAuthEnforcementSmokeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-deployment-profile-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceDeploymentProfileGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-openapi-contract-export", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-client-contract-snapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-api-contract-drift-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceOpenApiContractAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "service-hosted-deployment-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-readonly-runtime-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-hosted-api-contract-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceHostedSmokeAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "guarded-rerank-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedRerankComparisonAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "guarded-order-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedOrderQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "guarded-profile-sweep", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedProfileSweepAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-shadow-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-shadow-recall-loss", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningShadowRecallLossAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-optin-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningOptInComparisonAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-optin-fallback-analysis", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningOptInFallbackAnalysisAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "planning-optin-constraint-safety", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanningOptInConstraintSafetyAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "extended-failure-triage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExtendedFailureTriageAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "export-learning-features", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExportLearningFeaturesAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-dataset-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningDatasetQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "router-intent-baseline", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterIntentBaselineAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "router-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "router-intent-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterIntentShadowEvalAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "router-disagreement-triage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterDisagreementTriageAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "router-guarded-optin-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterGuardedOptInReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-readiness-freeze-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningReadinessFreezeReportAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-runtime-change-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningRuntimeChangeReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "generated-artifact-path-hygiene-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGeneratedArtifactPathHygieneAuditAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "generated-artifact-path-hygiene-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGeneratedArtifactPathHygieneGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-feedback-summary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackSummaryAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "export-learning-feedback", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExportLearningFeedbackAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-feedback-review-summary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackReviewSummaryAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-feedback-feature-candidates", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackFeatureCandidatesAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-feedback-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-feedback-review-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackReviewSmokeAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-approved-feedback-dataset-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningApprovedFeedbackDatasetGateAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "submit-learning-feedback", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSubmitLearningFeedbackAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-feedback-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackSmokeAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-baseline", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-baseline-router", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-baseline-ranker", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningBaselineAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "learning-ranker-ablation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-weight-sweep", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-residual-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-hard-negatives", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-lifecycle-aware-ranker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningRankerAnalysisAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "lifecycle-ranker-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLifecycleRankerShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "ranker-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRankerShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerShadowEvalAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-feature-completeness", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerFeatureCompletenessAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-shadow-failure-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerShadowFailureAuditAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-score-distribution", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerScoreDistributionAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-listwise-calibration", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerListwiseCalibrationAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-formal-priority-alignment", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerFormalPriorityAlignmentAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "candidate-reranker-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "graph-expansion-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphExpansionShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
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
