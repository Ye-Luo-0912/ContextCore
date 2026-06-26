using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Rendering;
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

namespace ContextCore.ControlRoom.Commands;

/// <summary>执行上下文评测并生成报告的命令。</summary>
public static partial class EvalCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new();

    private static readonly JsonSerializerOptions EvalSampleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private const string VectorEvalCorpusSourceMode = "eval-corpus";
    private const string VectorStoreSourceMode = "store";
    private const string VectorEvalCorpusWorkspaceId = "eval-vector";
    private const string VectorEvalCorpusCollectionId = "corpus";
    private const string Qwen3ProviderAlias = "qwen3";
    private const string Qwen3ProviderId = "qwen3-embedding-0.6b-onnx";
    private const string Qwen3ModelId = "qwen3-embedding-0.6b";
    private const int Qwen3Dimension = 1024;

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
            !string.Equals(subcommand, "router-intent-baseline", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "router-shadow-trace-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "router-intent-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "router-disagreement-triage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "router-guarded-optin-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-readiness-freeze-report", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-runtime-change-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-feedback-summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "export-learning-feedback", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-feedback-review-summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-feedback-feature-candidates", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-feedback-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-feedback-review-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-approved-feedback-dataset-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "submit-learning-feedback", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-feedback-smoke", StringComparison.OrdinalIgnoreCase) &&
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
            !string.Equals(subcommand, "candidate-reranker-feature-completeness", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "candidate-reranker-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "candidate-reranker-shadow-failure-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "candidate-reranker-score-distribution", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "candidate-reranker-listwise-calibration", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "candidate-reranker-formal-priority-alignment", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "candidate-reranker-shadow-trace-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "graph-expansion-shadow-trace-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "graph-expansion-optin-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "graph-expansion-guarded-optin-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-reindex-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-reindex-apply", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-index-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-index-coverage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-coverage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-backfill-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-backfill-apply", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-query-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-query-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-query-profile-sweep", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-residual-risk-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-recall-loss-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-safe-recall-recovery", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-ranker-fusion-shadow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-representation-benchmark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-query-expansion-shadow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-shadow-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "embedding-provider-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-provider-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-qwen3-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-qwen3-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-provider-configuration-sanity-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-provider-comparison-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-hybrid-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-hybrid-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-hybrid-recall-regression-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-hybrid-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-a3", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-extended", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-eligibility-recall-loss-triage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-eligibility-recall-loss-triage-a3", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-eligibility-recall-loss-triage-extended", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-a3", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-extended", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates-generate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-sidecar-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-sidecar-eligibility-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-sidecar-eligibility-recheck", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-sidecar-eligibility-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-create", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-export", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-validate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-apply-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-dataset-v2-contract", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-dataset-v2-validator", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-legacy-dataset-limitation-report", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-generate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-validate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-materialization-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-dense-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-generate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-leakage-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-anchor-dominance-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage-holdout", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-clusters", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "retrieval-dataset-v2-stress-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-v4-readiness-recheck", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-formal-retrieval-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-formal-retrieval-preview-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-shadow-package-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-shadow-package-comparison-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-formal-preview-optin-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-formal-preview-optin-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-formal-preview-optin-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-limited-formal-preview-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-limited-formal-preview-observation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-preview-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-observation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-design-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-proposal", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-proposal-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-config-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-approve", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-request-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-approve-runtime", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-harness-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-activation-preflight", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-route", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-activation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-rollback-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-window", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-window-summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-window-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-runtime-experiment-promotion-decision", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-retrieval-integration-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-retrieval-integration-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-graph-retrieval-quality-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-graph-retrieval-quality-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-quality-repair-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-quality-repair-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-observable-feature-contract", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-observable-feature-contract-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-feature-derivation-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-feature-derivation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-feature-derivation-repair", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-feature-derivation-repair-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-runtime-feature-derivation-failure-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-graph-hub-noise-control-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-graph-hub-noise-control-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-query-driven-candidate-source-repair", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-query-driven-candidate-source-repair-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-retrieval-integration-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-adapter-noop-binding-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-retrieval-integration-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-adapter-noop-binding-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-adapter-noop-binding-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "architecture-cleanup-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "architecture-cleanup-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "architecture-cleanup-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "architecture-cleanup-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-approval-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-approval-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-authorization", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-authorization-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-preparation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-preparation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-closeout", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "scoped-runtime-preview-live-activation-closeout-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "formal-retrieval-promotion-readiness-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "formal-retrieval-promotion-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "formal-retrieval-promotion-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "formal-retrieval-promotion-plan-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "dto-split-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "dto-split-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-eval-protocol-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-candidate-source-discriminability-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-retrieval-eval-protocol-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-input-metadata-enrichment-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-input-metadata-enrichment-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-source-aware-ranking-repair", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-source-aware-ranking-repair-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-output-token-priority-shadow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-output-token-priority-shadow-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-adapter-input-contract", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-adapter-input-contract-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-retrieval-integration-decision", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "vector-formal-retrieval-integration-decision-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "project-state-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "mainline-gap-map", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "generated-artifact-path-hygiene-audit", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "generated-artifact-path-hygiene-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "foundation-freeze-report", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "foundation-release-candidate-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "foundation-reproducibility-check", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-foundation-status-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-readiness-api-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-api-security-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-report-navigation-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-api-contract-report", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-api-contract-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-auth-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-auth-enforcement-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-deployment-profile-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-openapi-contract-export", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-client-contract-snapshot", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-api-contract-drift-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-hosted-deployment-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-readonly-runtime-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-hosted-api-contract-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "service-foundation-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "relation-expansion-profile-shadow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "relation-corpus-hygiene", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "relation-expansion-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "learning-ranker-analysis", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "storage-check", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "storage-boundary-report", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-storage-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-migration-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-migration-apply", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-migration-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-store-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-store-parity", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-review-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-review-parity", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-governance-parity", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-governance-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-dual-write-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-dual-write-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-shadow-read-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-shadow-read-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-provider-switch-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-provider-switch-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-runtime-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-service-mode-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-service-mode-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-extended-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-selected-workspace-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-expansion-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-expansion-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-expansion-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-observation-window", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-scoped-observation-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-selected-normal-workspace-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-limited-normal-scope-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-limited-normal-scope-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-multi-normal-scope-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-relation-multi-normal-scope-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-parity", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-readiness-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-dual-write-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-shadow-read-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-provider-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-scoped-service-mode-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-scoped-service-mode-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-selected-normal-scope-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-limited-scope-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-limited-scope-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-learning-feedback-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-parity", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-lease-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-dual-write-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-shadow-read-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-provider-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-scoped-worker-canary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-scoped-worker-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-limited-worker-scope-observation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-limited-worker-scope-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-job-queue-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-diagnostics", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-compatibility", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-provider-smoke", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-parity", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-apply", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-quality", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-query-preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-shadow-eval", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-shadow-eval-a3", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-shadow-eval-extended", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subcommand, "postgres-vector-freeze-gate", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(subcommand, "chunk-ablation", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(subcommand, "idle-unload", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(subcommand, "fs-vector-perf", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("eval supports:\n  eval run [--category <name>] [--include-batches] [--out <path>]\n  eval report [<path>]\n  eval attention-profile-selection [--baseline <path>] [--extended <path>] [--out <path.json>] [--md-out <path.md>]\n  eval guarded-rerank-comparison [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]\n  eval guarded-order-quality [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]\n  eval guarded-profile-sweep [--category <name>] [--include-batches] [--out <path.json>]\n  eval planning-shadow [--category <name>] [--include-batches] [--out <path.json>] [--triage-out <path.json>]\n  eval planning-shadow-quality [--category <name>] [--include-batches] [--out <path.json>]\n  eval planning-shadow-recall-loss [--category <name>] [--include-batches] [--out <path.json>]\n  eval planning-optin-comparison [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--out <path.json>]\n  eval planning-optin-fallback-analysis [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--candidate-intents <csv>] [--out <path.json>]\n  eval planning-optin-constraint-safety [--category <name>] [--include-batches] [--opt-in-intents <csv>] [--candidate-intents <csv>] [--out <path.json>]\n  eval extended-failure-triage [--input <eval-report.json>] [--out <path.json>] [--md-out <path.md>]\n  eval export-learning-features [--out-dir <dir>] [--workspace <id>] [--collection <id>] [--eval-reports <csv>] [--planning-shadow-reports <csv>]\n  eval learning-dataset-quality [--features-dir <dir>] [--out <path.json>] [--md-out <path.md>]\n  eval router-intent-baseline [--features-dir <dir>] [--input <path.jsonl>] [--out-dir <dir>]\n  eval router-shadow-trace-quality [--workspace <id>] [--collection <id>] [--input <path.jsonl>] [--out <path.json>] [--md-out <path.md>]\n  eval router-intent-shadow-eval [--input <path.jsonl>] [--out-dir <dir>]\n  eval learning-baseline --task router|ranker [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-baseline-router [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-baseline-ranker [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-ranker-ablation [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-ranker-weight-sweep [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-ranker-residual-audit [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-hard-negatives [--residual-audit <path>] [--features-dir <dir>] [--out-dir <dir>]\n  eval learning-lifecycle-aware-ranker [--features-dir <dir>] [--out-dir <dir>]\n  eval lifecycle-ranker-shadow [--category <name>] [--include-batches] [--profile <id>] [--out <path.json>]\n  eval ranker-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--out <path.json>] [--md-out <path.md>]\n  eval graph-expansion-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--out <path.json>] [--md-out <path.md>]\n  eval graph-expansion-optin-comparison [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]\n  eval relation-expansion-profile-shadow [--out <path.json>] [--md-out <path.md>]\n  eval relation-corpus-hygiene [--out <path.json>] [--md-out <path.md>]\n  eval relation-expansion-shadow-eval [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]\n  eval learning-ranker-analysis [--features-dir <dir>] [--out-dir <dir>]\n  eval perf [--out <path.json>]\n  eval perf-scale [--size 1000] [--fake-vectors] [--out <path.json>]\n  eval retrieval [--out <path.json>]\n  eval storage-check\n  eval chunk-ablation\n  eval idle-unload\n  eval fs-vector-perf [--size 1000]");
            Console.WriteLine("  eval graph-expansion-guarded-optin-gate [--category <name>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>] [--gate-out <path.json>] [--gate-md-out <path.md>]");
            Console.WriteLine("  eval router-disagreement-triage [--input <path.jsonl>] [--out-dir <dir>]");
            Console.WriteLine("  eval router-guarded-optin-readiness-gate [--out-dir <dir>] [--agreement-threshold <0..1>] [--low-confidence-max <n>]");
            Console.WriteLine("  eval learning-readiness-freeze-report [--out-dir <dir>]");
            Console.WriteLine("  eval learning-runtime-change-readiness-gate [--out-dir <dir>]");
            Console.WriteLine("  eval generated-artifact-path-hygiene-audit [--scan-dir <dir>]");
            Console.WriteLine("  eval generated-artifact-path-hygiene-gate [--scan-dir <dir>]");
            Console.WriteLine("  eval foundation-freeze-report");
            Console.WriteLine("  eval foundation-release-candidate-gate");
            Console.WriteLine("  eval foundation-reproducibility-check");
            Console.WriteLine("  eval service-foundation-status-smoke");
            Console.WriteLine("  eval service-readiness-api-smoke");
            Console.WriteLine("  eval service-api-security-diagnostics");
            Console.WriteLine("  eval service-report-navigation-smoke");
            Console.WriteLine("  eval service-api-contract-report [--production]");
            Console.WriteLine("  eval service-api-contract-freeze-gate [--production]");
            Console.WriteLine("  eval service-auth-diagnostics [--profile development|service|production]");
            Console.WriteLine("  eval service-auth-enforcement-smoke");
            Console.WriteLine("  eval service-deployment-profile-gate [--profile development|service|production]");
            Console.WriteLine("  eval service-openapi-contract-export");
            Console.WriteLine("  eval service-client-contract-snapshot");
            Console.WriteLine("  eval service-api-contract-drift-gate");
            Console.WriteLine("  eval service-hosted-deployment-smoke [--base-url <url>] [--profile development|service|production]");
            Console.WriteLine("  eval service-readonly-runtime-smoke [--base-url <url>] [--profile development|service|production]");
            Console.WriteLine("  eval service-hosted-api-contract-smoke [--base-url <url>] [--profile development|service|production]");
            Console.WriteLine("  eval service-foundation-freeze-gate");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-approval-preview --proposal-id <id>");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-approve --proposal-id <id> --approved-by <name> --confirm");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-approval-summary");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-approval-request-preview");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-approve-runtime --proposal-id <id> --approved-by <name> --confirm");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-approval-gate");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-noop-harness");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-noop-harness-gate");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-harness-freeze-gate");
            Console.WriteLine("  eval vector-guarded-scoped-runtime-experiment-plan");
            Console.WriteLine("  eval vector-guarded-scoped-runtime-experiment-plan-gate");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-activation-preflight");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-dry-run-route");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-activation-gate");
            Console.WriteLine("  eval vector-guarded-scoped-runtime-experiment");
            Console.WriteLine("  eval vector-guarded-scoped-runtime-experiment-observation");
            Console.WriteLine("  eval vector-guarded-scoped-runtime-experiment-rollback-smoke");
            Console.WriteLine("  eval vector-guarded-scoped-runtime-experiment-gate");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-observation-window");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-observation-window-summary");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-observation-window-gate");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-observation-freeze");
            Console.WriteLine("  eval vector-scoped-runtime-experiment-promotion-decision");
            Console.WriteLine("  eval vector-formal-retrieval-integration-plan");
            Console.WriteLine("  eval vector-formal-retrieval-integration-plan-gate");
            Console.WriteLine("  eval vector-shadow-formal-retrieval-adapter-plan");
            Console.WriteLine("  eval vector-shadow-formal-retrieval-adapter-plan-gate");
            Console.WriteLine("  eval vector-shadow-formal-retrieval-adapter");
            Console.WriteLine("  eval vector-shadow-formal-retrieval-adapter-gate");
            Console.WriteLine("  eval vector-formal-adapter-package-shadow-comparison");
            Console.WriteLine("  eval vector-formal-adapter-package-shadow-comparison-gate");
            Console.WriteLine("  eval vector-graph-retrieval-quality-audit");
            Console.WriteLine("  eval vector-graph-retrieval-quality-gate");
            Console.WriteLine("  eval vector-retrieval-quality-repair-preview");
            Console.WriteLine("  eval vector-retrieval-quality-repair-gate");
            Console.WriteLine("  eval vector-runtime-observable-feature-contract");
            Console.WriteLine("  eval vector-runtime-observable-feature-contract-gate");
            Console.WriteLine("  eval vector-runtime-feature-derivation-preview");
            Console.WriteLine("  eval vector-runtime-feature-derivation-gate");
            Console.WriteLine("  eval vector-runtime-feature-derivation-repair");
            Console.WriteLine("  eval vector-runtime-feature-derivation-repair-gate");
            Console.WriteLine("  eval vector-runtime-feature-derivation-failure-freeze");
            Console.WriteLine("  eval vector-graph-hub-noise-control-preview");
            Console.WriteLine("  eval vector-graph-hub-noise-control-gate");
            Console.WriteLine("  eval vector-query-driven-candidate-source-repair");
            Console.WriteLine("  eval vector-query-driven-candidate-source-repair-gate");
            Console.WriteLine("  eval vector-formal-retrieval-integration-freeze");
            Console.WriteLine("  eval vector-adapter-noop-binding-plan");
            Console.WriteLine("  eval vector-formal-retrieval-integration-freeze-gate");
            Console.WriteLine("  eval vector-adapter-noop-binding-smoke");
            Console.WriteLine("  eval vector-adapter-noop-binding-gate");
            Console.WriteLine("  eval vector-scoped-shadow-adapter-invocation");
            Console.WriteLine("  eval vector-scoped-shadow-adapter-invocation-gate");
            Console.WriteLine("  eval vector-mainline-shadow-adapter-package-comparison");
            Console.WriteLine("  eval vector-mainline-shadow-adapter-package-comparison-gate");
            Console.WriteLine("  eval architecture-cleanup-plan");
            Console.WriteLine("  eval architecture-cleanup-readiness-gate");
            Console.WriteLine("  eval architecture-cleanup-freeze");
            Console.WriteLine("  eval architecture-cleanup-freeze-gate");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-plan [--max-requests <n>] [--max-duration-minutes <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-plan-gate [--max-requests <n>] [--max-duration-minutes <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-dry-run [--observation-runs <n>] [--max-token-delta-total <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-dry-run-gate [--observation-runs <n>] [--max-token-delta-total <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-activation-preflight");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-activation-preflight-gate");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-observation-window [--observation-runs <n>] [--max-requests <n>] [--max-duration-minutes <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-observation-window-gate [--observation-runs <n>] [--max-requests <n>] [--max-duration-minutes <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-observation-hardening [--min-runs <n>] [--min-requests <n>] [--max-duration-minutes <n>] [--requests-per-run <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-observation-hardening-gate [--min-runs <n>] [--min-requests <n>] [--max-duration-minutes <n>] [--requests-per-run <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-observation-freeze [--test-baseline <n>]");
            Console.WriteLine("  eval controlled-applied-merge-runtime-preview-observation-freeze-gate [--test-baseline <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-approval-plan [--validity-days <n>] [--kill-switch-seconds <n>] [--rollback-minutes <n>] [--trace-retention-days <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-approval-plan-gate [--validity-days <n>] [--kill-switch-seconds <n>] [--rollback-minutes <n>] [--trace-retention-days <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-authorization [--approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-authorization-gate [--approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-authorization-hardening [--approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-authorization-hardening-gate [--approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-preparation [--approved-by <name>] [--max-observations <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-preparation-gate [--approved-by <name>] [--max-observations <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-dry-run [--approved-by <name>] [--dry-runs <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-dry-run-gate [--approved-by <name>] [--dry-runs <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-window-preflight [--approved-by <name>] [--max-window-minutes <n>] [--max-requests <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-window-preflight-gate [--approved-by <name>] [--max-window-minutes <n>] [--max-requests <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-window-noop-execution [--approved-by <name>] [--min-windows <n>] [--requests-per-window <n>] [--min-requests <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-window-noop-execution-gate [--approved-by <name>] [--min-windows <n>] [--requests-per-window <n>] [--min-requests <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-live-readiness-freeze [--approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-activation-live-readiness-freeze-gate [--approved-by <name>] [--final-approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-execution-plan [--approved-by <name>] [--final-approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-execution-plan-gate [--approved-by <name>] [--final-approved-by <name>]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-execution [--final-approved-by <name>] [--execution-plan-id <id>] [--execute-live-activation]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-execution-gate [--final-approved-by <name>] [--execution-plan-id <id>] [--execute-live-activation]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-observation [--observation-runs <n>] [--requests-per-run <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-observation-gate [--observation-runs <n>] [--requests-per-run <n>]");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-summary-freeze");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-summary-freeze-gate");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-closeout");
            Console.WriteLine("  eval scoped-runtime-preview-live-activation-closeout-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-readiness-audit");
            Console.WriteLine("  eval formal-retrieval-promotion-readiness-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-plan");
            Console.WriteLine("  eval formal-retrieval-promotion-plan-gate");
            Console.WriteLine("  eval dto-split-plan");
            Console.WriteLine("  eval dto-split-readiness-gate");
            Console.WriteLine("  eval vector-retrieval-eval-protocol-audit");
            Console.WriteLine("  eval vector-candidate-source-discriminability-audit");
            Console.WriteLine("  eval vector-retrieval-eval-protocol-gate");
            Console.WriteLine("  eval vector-input-metadata-enrichment-preview");
            Console.WriteLine("  eval vector-input-metadata-enrichment-gate");
            Console.WriteLine("  eval vector-enriched-candidate-source-repair-recheck");
            Console.WriteLine("  eval vector-enriched-candidate-source-repair-recheck-gate");
            Console.WriteLine("  eval vector-source-aware-ranking-repair");
            Console.WriteLine("  eval vector-source-aware-ranking-repair-gate");
            Console.WriteLine("  eval vector-output-token-priority-shadow");
            Console.WriteLine("  eval vector-output-token-priority-shadow-gate");
            Console.WriteLine("  eval vector-formal-adapter-input-contract [--formal-source <path>]");
            Console.WriteLine("  eval vector-formal-adapter-input-contract-gate [--formal-source <path>]");
            Console.WriteLine("  eval vector-formal-retrieval-integration-decision");
            Console.WriteLine("  eval vector-formal-retrieval-integration-decision-gate");
            Console.WriteLine("  eval project-state-audit");
            Console.WriteLine("  eval mainline-gap-map");
            Console.WriteLine("  eval learning-feedback-summary [--workspace <id>] [--collection <id>] [--capability <id>] [--kind <kind>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval export-learning-feedback [--workspace <id>] [--collection <id>] [--capability <id>] [--kind <kind>] [--out <path.jsonl>]");
            Console.WriteLine("  eval learning-feedback-review-summary [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval learning-feedback-feature-candidates [--workspace <id>] [--collection <id>] [--capability <id>] [--kind <kind>] [--out <path.jsonl>] [--md-out <path.md>] [--report-out <path.json>]");
            Console.WriteLine("  eval learning-feedback-quality [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval learning-feedback-review-smoke [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval learning-approved-feedback-dataset-gate [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval submit-learning-feedback --capability <id> --target-type <type> --target-id <id> --kind <kind> [--source-operation-id <id>] [--reason <text>] [--metadata-only true|false]");
            Console.WriteLine("  eval learning-feedback-smoke [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval storage-boundary-report [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-storage-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-migration-preview [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-migration-apply --confirm [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-migration-smoke [--confirm] [--drop-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-store-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-store-parity [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-review-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-review-parity [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-governance-parity [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-governance-readiness-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-dual-write-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-dual-write-quality [--input <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-shadow-read-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-shadow-read-quality [--input <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-provider-switch-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-provider-switch-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-runtime-canary [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-scoped-service-mode-smoke [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-scoped-service-mode-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-scoped-extended-canary --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-relation-selected-workspace-canary [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--max-operations <n>] [--observation-window-minutes <n>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-relation-scoped-expansion-plan [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-scoped-expansion-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-relation-scoped-expansion-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-scoped-observation-window [--cleanup-confirm] [--connection-string <value>] [--schema <name>] [--observation-window-minutes <n>] [--operation-interval-seconds <n>] [--max-operations <n>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-relation-scoped-observation-quality [--p95-threshold-ms <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-selected-normal-workspace-canary [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--max-operations <n>] [--observation-window-minutes <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--cleanup-confirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-relation-limited-normal-scope-observation [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--max-operations <n>] [--observation-window-minutes <n>] [--operation-interval-seconds <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--cleanup-confirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-vector-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-compatibility [--provider <id>] [--model <id>] [--dimension <n>] [--normalized true|false] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-provider-smoke --cleanup-confirm [--provider <id>] [--model <id>] [--dimension <n>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-parity --cleanup-confirm [--provider <id>] [--model <id>] [--dimension <n>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-limited-normal-scope-quality [--fallback-rate-threshold <0..1>] [--p95-threshold-ms <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-relation-multi-normal-scope-canary [--connection-string <value>] [--schema <name>] [--max-operations-per-scope <n>] [--observation-window-minutes <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--cleanup-confirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-relation-multi-normal-scope-quality [--p95-threshold-ms <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-parity --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-readiness-gate [--connection-string <value>] [--schema <name>] [--diagnostics <path.json>] [--parity <path.json>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-dual-write-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-learning-feedback-shadow-read-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-learning-feedback-provider-quality [--dual-traces <path.jsonl>] [--shadow-traces <path.jsonl>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-scoped-service-mode-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-learning-feedback-scoped-service-mode-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-selected-normal-scope-canary [--workspace <id>] [--collection <id>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-learning-feedback-limited-scope-observation [--workspace <id>] [--collection <id>] [--observation-window-minutes <n>] [--max-operations <n>] [--cleanup-mode None|CanaryOnly|ExplicitConfirm] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-learning-feedback-limited-scope-quality [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-learning-feedback-freeze-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-diagnostics [--connection-string <value>] [--schema <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-parity --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-lease-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-dual-write-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-job-queue-shadow-read-smoke --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-job-queue-provider-quality [--dual <path.json>] [--shadow <path.json>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-scoped-worker-canary --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--quality <path.json>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-job-queue-scoped-worker-quality [--canary <path.json>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-limited-worker-scope-observation --cleanup-confirm [--connection-string <value>] [--schema <name>] [--workspace <id>] [--collection <id>] [--quality <path.json>] [--observation-window-seconds <n>] [--out <path.json>] [--md-out <path.md>] [--trace-out <path.jsonl>]");
            Console.WriteLine("  eval postgres-job-queue-limited-worker-scope-quality [--observation <path.json>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-job-queue-freeze-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-provider-scoped-reindex-plan [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--source-kind <kind>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-provider-scoped-reindex-apply --confirm [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--source-kind <kind>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-provider-scoped-reindex-quality [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--source-kind <kind>] [--apply-report <path.json>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-query-preview [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--max-queries <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-shadow-eval [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--max-queries <n>] [--out-summary <path.json>] [--summary-md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-shadow-eval-a3 [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-shadow-eval-extended [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--profile <id>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval postgres-vector-freeze-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval candidate-reranker-feature-completeness [--out-dir <dir>]");
            Console.WriteLine("  eval candidate-reranker-shadow-eval [--out-dir <dir>] [--top-k <n>]");
            Console.WriteLine("  eval candidate-reranker-shadow-failure-audit [--out-dir <dir>] [--top-k <n>]");
            Console.WriteLine("  eval candidate-reranker-score-distribution [--out-dir <dir>] [--top-k <n>]");
            Console.WriteLine("  eval candidate-reranker-listwise-calibration [--out-dir <dir>] [--top-k <n>]");
            Console.WriteLine("  eval candidate-reranker-formal-priority-alignment [--out-dir <dir>] [--top-k <n>]");
            Console.WriteLine("  eval candidate-reranker-shadow-trace-quality [--workspace <id>] [--collection <id>] [--take <n>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-reindex-plan [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--layers <csv>] [--item-kind <kind>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-reindex-apply --confirm [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--force] [--batch-size <n>] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-index-diagnostics [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-index-coverage [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--max-items <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-coverage [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-backfill-plan [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-backfill-apply --confirm [--source eval-corpus|store] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-query-preview --query <text> [--profile <id>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-query-shadow-eval [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-query-profile-sweep [--category <name>] [--source eval-corpus|store] [--contexts <dir>] [--workspace <id>] [--collection <id>] [--provider deterministic-hash|onnx-local] [--provider-type DeterministicHash|OnnxLocal] [--model-path <local.onnx>] [--tokenizer-path <vocab.txt>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-residual-risk-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-recall-loss-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-safe-recall-recovery [--category <name>] [--provider deterministic-hash|onnx-local] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-ranker-fusion-shadow [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-representation-benchmark [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--audit-out-a3 <path.json>] [--audit-out-extended <path.json>] [--md-out <path.md>] [--audit-md-out <path.md>]");
            Console.WriteLine("  eval vector-query-expansion-shadow [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--min-similarity <score>] [--out-a3 <path.json>] [--out-extended <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-retrieval-shadow-readiness-gate [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local] [--top-k <n>] [--layer <layer>] [--item-kind <kind>] [--min-similarity <score>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval embedding-provider-smoke [--provider deterministic-hash|onnx-local|qwen3] [--model-path <local.onnx>] [--tokenizer-path <vocab.txt|tokenizer.json>] [--dimension <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-provider-comparison [--providers current,qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-qwen3-shadow-eval [--category <name>] [--profile <id>] [--top-k <n>]");
            Console.WriteLine("  eval vector-qwen3-readiness-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-provider-configuration-sanity-audit [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-provider-comparison-freeze [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-hybrid-preview [--category <name>] [--profile <id>] [--top-k <n>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-hybrid-shadow-eval [--category <name>] [--profile <id>] [--top-k <n>]");
            Console.WriteLine("  eval vector-hybrid-readiness-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-hybrid-recall-regression-audit [--category <name>] [--profile <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-hybrid-freeze-gate [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-retrieval-dataset-alignment-audit [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-retrieval-dataset-alignment-audit-a3 [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-retrieval-dataset-alignment-audit-extended [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-eligibility-recall-loss-triage [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-eligibility-recall-loss-triage-a3 [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-eligibility-recall-loss-triage-extended [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-repair-plan [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out-a3 <path.json>] [--out-extended <path.json>] [--out-summary <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-repair-plan-a3 [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-repair-plan-extended [--category <name>] [--profile <id>] [--provider deterministic-hash|onnx-local|qwen3] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-candidates-generate [--workspace <id>] [--collection <id>] [--repair-plan <vector/eligibility/*.json>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-candidates [--workspace <id>] [--collection <id>] [--status <name>] [--layer <name>] [--item-kind <name>] [--must-hit <id>] [--source-eval-set <name>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-summary [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-sidecar-preview [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-smoke [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-sidecar-eligibility-preview [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-sidecar-eligibility-recheck [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-sidecar-eligibility-quality [--workspace <id>] [--collection <id>] [--out <path.json>] [--md-out <path.md>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-batch-create [--workspace <id>] [--collection <id>] [--created-by <name>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-batch-export [--batch-id <id>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-batch-import [--batch-id <id>] [--input <review-sheet.jsonl>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-batch-validate [--batch-id <id>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-batch-apply-preview [--batch-id <id>]");
            Console.WriteLine("  eval vector-lifecycle-metadata-review-batch-import-smoke");
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

        if (string.Equals(subcommand, "storage-boundary-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteStorageBoundaryReportAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-storage-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresStorageDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-migration-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresMigrationPreviewAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-migration-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresMigrationApplyAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-migration-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresMigrationSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-store-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationStoreDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-store-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationStoreParityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-review-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationReviewDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-review-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationReviewParityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-governance-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationGovernanceParityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-governance-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationGovernanceReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-dual-write-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationDualWriteSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-dual-write-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationDualWriteQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-shadow-read-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationShadowReadSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-shadow-read-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationShadowReadQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-provider-switch-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationProviderSwitchSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-provider-switch-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationProviderSwitchGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-runtime-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationRuntimeCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-service-mode-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedServiceModeSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-service-mode-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedServiceModeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-extended-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExtendedCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-selected-workspace-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationSelectedWorkspaceCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-expansion-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExpansionPlanAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-expansion-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExpansionSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-expansion-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedExpansionGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-observation-window", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedObservationWindowAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-scoped-observation-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationScopedObservationQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-selected-normal-workspace-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationSelectedNormalWorkspaceCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-limited-normal-scope-observation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationLimitedNormalScopeObservationAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-limited-normal-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationLimitedNormalScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-multi-normal-scope-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationMultiNormalScopeCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-relation-multi-normal-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresRelationMultiNormalScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackParityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-dual-write-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackDualWriteSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-shadow-read-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackShadowReadSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-provider-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackProviderQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-scoped-service-mode-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackScopedServiceModeSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-scoped-service-mode-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackScopedServiceModeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-selected-normal-scope-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackSelectedNormalScopeCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-limited-scope-observation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackLimitedScopeObservationAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-limited-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackLimitedScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-learning-feedback-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresLearningFeedbackFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueParityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-lease-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueLeaseSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-dual-write-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueDualWriteSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-shadow-read-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueShadowReadSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-provider-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueProviderQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-scoped-worker-canary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueScopedWorkerCanaryAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-scoped-worker-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueScopedWorkerQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-limited-worker-scope-observation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueLimitedWorkerScopeObservationAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-limited-worker-scope-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueLimitedWorkerScopeQualityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-job-queue-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresJobQueueFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-compatibility", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorCompatibilityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-parity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorParityAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderScopedReindexPlanAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderScopedReindexApplyAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-provider-scoped-reindex-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorProviderScopedReindexQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-query-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorQueryPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "postgres-vector-shadow-eval-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "postgres-vector-shadow-eval-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorShadowEvalAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "postgres-vector-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePostgresVectorFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-reindex-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorReindexPlanAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-reindex-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorReindexApplyAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-index-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorIndexDiagnosticsAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-index-coverage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorIndexCoverageAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-coverage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataCoverageAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-backfill-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataBackfillAsync(service, args, apply: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-backfill-apply", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataBackfillAsync(service, args, apply: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-query-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-query-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryShadowEvalAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-query-profile-sweep", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryProfileSweepAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-residual-risk-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorResidualRiskAuditAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-recall-loss-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRecallLossAuditAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-safe-recall-recovery", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorSafeRecallRecoveryAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-ranker-fusion-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRankerFusionShadowAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-representation-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRepresentationBenchmarkAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-query-expansion-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQueryExpansionShadowAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-shadow-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRetrievalShadowReadinessGateAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "embedding-provider-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteEmbeddingProviderSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-provider-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorProviderComparisonV310Async(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-qwen3-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQwen3ShadowEvalAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-qwen3-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorQwen3ReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-provider-configuration-sanity-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorProviderConfigurationSanityAuditAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-provider-comparison-freeze", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteEmbeddingProviderComparisonFreezeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-hybrid-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridPreviewAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridShadowEvalAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-hybrid-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-hybrid-recall-regression-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridRecallRegressionAuditAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-hybrid-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorHybridFreezeGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorRetrievalDatasetAlignmentAuditAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-eligibility-recall-loss-triage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-eligibility-recall-loss-triage-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-eligibility-recall-loss-triage-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorEligibilityRecallLossTriageAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-a3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-extended", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataRepairPlanAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates-generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewCandidatesAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-summary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-sidecar-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-sidecar-eligibility-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-sidecar-eligibility-recheck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-sidecar-eligibility-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorSidecarEligibilityPreviewAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-create", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-export", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-apply-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewBatchAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataEvidenceBackfillAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-dataset-v2-contract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-dataset-v2-validator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-legacy-dataset-limitation-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2MetadataContractAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-validate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-quality", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-materialization-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2GenerationAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-dense-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2ShadowEvalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-leakage-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-anchor-dominance-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2StressAsync(service, args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage-holdout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-clusters", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2StressFailureTriageAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2HybridScoringRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2HybridScoringRiskTriageAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalDatasetV2StressFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-v4-readiness-recheck", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorV4ReadinessRecheckAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-guarded-formal-retrieval-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-guarded-formal-retrieval-preview-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorGuardedFormalRetrievalPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-shadow-package-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-package-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorShadowPackageComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-formal-preview-optin-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-formal-preview-optin-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-formal-preview-optin-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedFormalPreviewOptInAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-limited-formal-preview-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-limited-formal-preview-observation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLimitedFormalPreviewObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-formal-preview-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorFormalPreviewFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExplicitScopedRuntimeExperimentAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-observation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentDryRunObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-design-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentDesignFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-proposal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-proposal-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-config-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentProposalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-approve", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-summary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentApprovalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-request-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-approve-runtime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentRuntimeApprovalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentNoOpHarnessAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-harness-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentHarnessFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedScopedRuntimeExperimentPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-activation-preflight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-route", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-activation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentActivationPreflightAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-rollback-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGuardedScopedRuntimeExperimentAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-window", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-window-summary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-window-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentObservationWindowAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-observation-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-promotion-decision", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentObservationFreezeAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-formal-retrieval-integration-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-retrieval-integration-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationPlanAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteShadowFormalRetrievalAdapterPlanAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteShadowFormalRetrievalAdapterAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalAdapterPackageShadowComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-graph-retrieval-quality-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-graph-retrieval-quality-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphVectorRetrievalQualityAuditAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-quality-repair-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-quality-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalQualityRepairPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-runtime-observable-feature-contract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-runtime-observable-feature-contract-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeObservableFeatureContractAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-runtime-feature-derivation-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-runtime-feature-derivation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeFeatureDerivationPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-runtime-feature-derivation-repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-runtime-feature-derivation-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeFeatureDerivationRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-runtime-feature-derivation-failure-freeze", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRuntimeFeatureDerivationFailureFreezeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-graph-hub-noise-control-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-graph-hub-noise-control-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphHubNoiseControlAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-query-driven-candidate-source-repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-query-driven-candidate-source-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteQueryDrivenCandidateSourceRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-formal-retrieval-integration-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-retrieval-integration-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-adapter-noop-binding-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-adapter-noop-binding-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-adapter-noop-binding-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAdapterNoOpBindingSmokeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedShadowAdapterInvocationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteMainlineShadowAdapterPackageComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "architecture-cleanup-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupPlanAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "architecture-cleanup-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupReadinessGateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "architecture-cleanup-freeze", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupFreezeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "architecture-cleanup-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewActivationPreflightAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewObservationWindowAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewObservationHardeningAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewObservationFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-approval-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-approval-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewApprovalPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-authorization", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-authorization-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewAuthorizationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewAuthorizationHardeningAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-preparation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-preparation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationPreparationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationWindowPreflightAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationWindowNoOpExecutionAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationLiveReadinessFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationExecutionPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationExecutionAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationSummaryFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-closeout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-closeout-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationCloseoutAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-readiness-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionReadinessAuditAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "dto-split-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "dto-split-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteDtoSplitPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-eval-protocol-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-candidate-source-discriminability-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-retrieval-eval-protocol-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRetrievalEvalProtocolAuditAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-input-metadata-enrichment-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-input-metadata-enrichment-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteInputMetadataEnrichmentPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteEnrichedCandidateSourceRepairRecheckAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-source-aware-ranking-repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-source-aware-ranking-repair-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSourceAwareRankingRepairAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-output-token-priority-shadow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-output-token-priority-shadow-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteOutputTokenPriorityShadowAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-formal-adapter-input-contract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-adapter-input-contract-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalAdapterInputContractAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-formal-retrieval-integration-decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-retrieval-integration-decision-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationDecisionAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "project-state-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "mainline-gap-map", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteProjectStateAuditAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "foundation-freeze-report", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "foundation-release-candidate-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFoundationFreezeAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "foundation-reproducibility-check", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFoundationReproducibilityCheckAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-foundation-status-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-readiness-api-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceFoundationStatusSmokeAsync(subcommand, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-api-security-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceApiSecurityDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-report-navigation-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceReportNavigationSmokeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-api-contract-report", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-api-contract-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceApiContractAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-auth-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceAuthDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-auth-enforcement-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceAuthEnforcementSmokeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-deployment-profile-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceDeploymentProfileGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-openapi-contract-export", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-client-contract-snapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-api-contract-drift-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceOpenApiContractAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-hosted-deployment-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-readonly-runtime-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "service-hosted-api-contract-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceHostedSmokeAsync(subcommand, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "service-foundation-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteServiceFoundationFreezeGateAsync(cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "router-intent-baseline", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterIntentBaselineAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "router-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "router-intent-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterIntentShadowEvalAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "router-disagreement-triage", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterDisagreementTriageAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "router-guarded-optin-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRouterGuardedOptInReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-readiness-freeze-report", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningReadinessFreezeReportAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-runtime-change-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningRuntimeChangeReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "generated-artifact-path-hygiene-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGeneratedArtifactPathHygieneAuditAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "generated-artifact-path-hygiene-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGeneratedArtifactPathHygieneGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-feedback-summary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackSummaryAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "export-learning-feedback", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExportLearningFeedbackAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-feedback-review-summary", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackReviewSummaryAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-feedback-feature-candidates", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackFeatureCandidatesAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-feedback-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-feedback-review-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackReviewSmokeAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-approved-feedback-dataset-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningApprovedFeedbackDatasetGateAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "submit-learning-feedback", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSubmitLearningFeedbackAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "learning-feedback-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLearningFeedbackSmokeAsync(service, args, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "candidate-reranker-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerShadowEvalAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "candidate-reranker-feature-completeness", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerFeatureCompletenessAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "candidate-reranker-shadow-failure-audit", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerShadowFailureAuditAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "candidate-reranker-score-distribution", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerScoreDistributionAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "candidate-reranker-listwise-calibration", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerListwiseCalibrationAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "candidate-reranker-formal-priority-alignment", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerFormalPriorityAlignmentAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "candidate-reranker-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteCandidateRerankerShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "graph-expansion-shadow-trace-quality", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphExpansionShadowTraceQualityAsync(service, args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "graph-expansion-optin-comparison", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphExpansionOptInComparisonAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "graph-expansion-guarded-optin-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphExpansionGuardedOptInGateAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "relation-expansion-profile-shadow", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRelationExpansionProfileShadowAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "relation-corpus-hygiene", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRelationCorpusHygieneAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "relation-expansion-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteRelationExpansionShadowEvalAsync(args, cancellationToken).ConfigureAwait(false);
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

    private static async Task ExecuteCandidateRerankerShadowEvalAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, CandidateRerankerShadowEvalRunner.DefaultOutputDirectory);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var profile = CommandHelpers.GetOption(args, "--profile")
            ?? CandidateRerankerShadowProfiles.BaselineLifecycleAware;
        var options = new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            ShadowProfile = profile,
            MaxCandidatesPerTrace = 50,
            RecordTopK = topK > 0 ? topK : 10,
            RecordWouldChange = true
        };
        var runner = new ContextEvalRunner();
        var shadowRunner = new CandidateRerankerShadowEvalRunner();
        var a3Eval = await runner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: false).ConfigureAwait(false);
        var extendedEval = await runner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: true).ConfigureAwait(false);
        var a3 = shadowRunner.Build(a3Eval, "A3", options);
        var extended = shadowRunner.Build(extendedEval, "Extended", options);
        Directory.CreateDirectory(outputDirectory);
        var a3Path = Path.Combine(outputDirectory, CandidateRerankerShadowEvalRunner.A3ReportFileName);
        var extendedPath = Path.Combine(outputDirectory, CandidateRerankerShadowEvalRunner.ExtendedReportFileName);
        var markdownPath = Path.Combine(outputDirectory, CandidateRerankerShadowEvalRunner.MarkdownReportFileName);
        await WriteTextAsync(JsonSerializer.Serialize(a3, JsonOptions), a3Path, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extended, JsonOptions), extendedPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(CandidateRerankerShadowEvalRunner.BuildMarkdownReport(a3, extended), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker shadow A3: {Path.GetFullPath(a3Path)}");
        Console.WriteLine($"[Eval] Candidate reranker shadow Extended: {Path.GetFullPath(extendedPath)}");
        Console.WriteLine($"[Eval] Candidate reranker shadow markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 samples={a3.Samples}; profile={a3.ShadowProfile}; netGain={a3.NetGain}; netAfterAbstain={a3.NetGainAfterAbstain}; apply={a3.WouldApplyCount}; abstain={a3.AbstainCount}; improve={a3.WouldImproveCount}; regress={a3.WouldRegressCount}; risk={a3.LifecycleRiskCount + a3.DeprecatedRiskCount + a3.MustNotRiskCount}; rec={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended samples={extended.Samples}; profile={extended.ShadowProfile}; netGain={extended.NetGain}; netAfterAbstain={extended.NetGainAfterAbstain}; apply={extended.WouldApplyCount}; abstain={extended.AbstainCount}; improve={extended.WouldImproveCount}; regress={extended.WouldRegressCount}; risk={extended.LifecycleRiskCount + extended.DeprecatedRiskCount + extended.MustNotRiskCount}; rec={extended.Recommendation}");
    }

    private static async Task ExecuteCandidateRerankerFeatureCompletenessAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, CandidateRerankerFeatureCompletenessRunner.DefaultOutputDirectory);
        var evalRunner = new ContextEvalRunner();
        var featureRunner = new CandidateRerankerFeatureCompletenessRunner();
        var a3Eval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: false).ConfigureAwait(false);
        var extendedEval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: true).ConfigureAwait(false);
        var a3 = featureRunner.Build(a3Eval, "A3");
        var extended = featureRunner.Build(extendedEval, "Extended");
        Directory.CreateDirectory(outputDirectory);
        var a3Path = Path.Combine(outputDirectory, CandidateRerankerFeatureCompletenessRunner.A3ReportFileName);
        var extendedPath = Path.Combine(outputDirectory, CandidateRerankerFeatureCompletenessRunner.ExtendedReportFileName);
        var markdownPath = Path.Combine(outputDirectory, CandidateRerankerFeatureCompletenessRunner.MarkdownReportFileName);
        await WriteTextAsync(JsonSerializer.Serialize(a3, JsonOptions), a3Path, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extended, JsonOptions), extendedPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
                CandidateRerankerFeatureCompletenessRunner.BuildMarkdownReport(a3, extended),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker feature completeness A3: {Path.GetFullPath(a3Path)}");
        Console.WriteLine($"[Eval] Candidate reranker feature completeness Extended: {Path.GetFullPath(extendedPath)}");
        Console.WriteLine($"[Eval] Candidate reranker feature completeness markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 raw={a3.RawCandidateCount}; rankable={a3.RankableCandidateCount}; blocked={a3.BlockedCandidateCount}; completeness={a3.FeatureCompletenessRate:P2}; guard={a3.EligibilityGuardStatus}; rec={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended raw={extended.RawCandidateCount}; rankable={extended.RankableCandidateCount}; blocked={extended.BlockedCandidateCount}; completeness={extended.FeatureCompletenessRate:P2}; guard={extended.EligibilityGuardStatus}; rec={extended.Recommendation}");
    }

    private static async Task ExecuteCandidateRerankerShadowFailureAuditAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, CandidateRerankerShadowFailureAuditRunner.DefaultOutputDirectory);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var options = new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            MaxCandidatesPerTrace = 50,
            RecordTopK = topK > 0 ? topK : 10,
            RecordWouldChange = true
        };
        var evalRunner = new ContextEvalRunner();
        var auditRunner = new CandidateRerankerShadowFailureAuditRunner();
        var a3Eval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: false).ConfigureAwait(false);
        var extendedEval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: true).ConfigureAwait(false);
        var a3 = auditRunner.Build(a3Eval, "A3", options);
        var extended = auditRunner.Build(extendedEval, "Extended", options);
        Directory.CreateDirectory(outputDirectory);
        var a3Path = Path.Combine(outputDirectory, CandidateRerankerShadowFailureAuditRunner.A3ReportFileName);
        var extendedPath = Path.Combine(outputDirectory, CandidateRerankerShadowFailureAuditRunner.ExtendedReportFileName);
        var markdownPath = Path.Combine(outputDirectory, CandidateRerankerShadowFailureAuditRunner.MarkdownReportFileName);
        await WriteTextAsync(JsonSerializer.Serialize(a3, JsonOptions), a3Path, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extended, JsonOptions), extendedPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
                CandidateRerankerShadowFailureAuditRunner.BuildMarkdownReport(a3, extended),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker failure audit A3: {Path.GetFullPath(a3Path)}");
        Console.WriteLine($"[Eval] Candidate reranker failure audit Extended: {Path.GetFullPath(extendedPath)}");
        Console.WriteLine($"[Eval] Candidate reranker failure audit markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 regressions={a3.RegressionCount}; scoreContract={a3.ScoreContractStatus}; riskTopK={a3.RiskCandidateInShadowTopK}; next={a3.RecommendedNextAction}");
        Console.WriteLine($"[Eval] Extended regressions={extended.RegressionCount}; scoreContract={extended.ScoreContractStatus}; riskTopK={extended.RiskCandidateInShadowTopK}; next={extended.RecommendedNextAction}");
    }

    private static async Task ExecuteCandidateRerankerScoreDistributionAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, CandidateRerankerScoreDistributionRunner.DefaultOutputDirectory);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var options = new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            MaxCandidatesPerTrace = 50,
            RecordTopK = topK > 0 ? topK : 10,
            RecordWouldChange = true
        };
        var evalRunner = new ContextEvalRunner();
        var distributionRunner = new CandidateRerankerScoreDistributionRunner();
        var a3Eval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: false).ConfigureAwait(false);
        var extendedEval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: true).ConfigureAwait(false);
        var a3 = distributionRunner.Build(a3Eval, "A3", options);
        var extended = distributionRunner.Build(extendedEval, "Extended", options);
        Directory.CreateDirectory(outputDirectory);
        var a3Path = Path.Combine(outputDirectory, CandidateRerankerScoreDistributionRunner.A3ReportFileName);
        var extendedPath = Path.Combine(outputDirectory, CandidateRerankerScoreDistributionRunner.ExtendedReportFileName);
        var markdownPath = Path.Combine(outputDirectory, CandidateRerankerScoreDistributionRunner.MarkdownReportFileName);
        await WriteTextAsync(JsonSerializer.Serialize(a3, JsonOptions), a3Path, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extended, JsonOptions), extendedPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
                CandidateRerankerScoreDistributionRunner.BuildMarkdownReport(a3, extended),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker score distribution A3: {Path.GetFullPath(a3Path)}");
        Console.WriteLine($"[Eval] Candidate reranker score distribution Extended: {Path.GetFullPath(extendedPath)}");
        Console.WriteLine($"[Eval] Candidate reranker score distribution markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 mean={a3.ScoreMean:0.####}; std={a3.ScoreStdDev:0.####}; lowMargin={a3.LowMarginDecisionCount}; rec={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended mean={extended.ScoreMean:0.####}; std={extended.ScoreStdDev:0.####}; lowMargin={extended.LowMarginDecisionCount}; rec={extended.Recommendation}");
    }

    private static async Task ExecuteCandidateRerankerListwiseCalibrationAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, CandidateRerankerListwiseCalibrationRunner.DefaultOutputDirectory);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var options = new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            MaxCandidatesPerTrace = 50,
            RecordTopK = topK > 0 ? topK : 10,
            RecordWouldChange = true
        };
        var evalRunner = new ContextEvalRunner();
        var calibrationRunner = new CandidateRerankerListwiseCalibrationRunner();
        var a3Eval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: false).ConfigureAwait(false);
        var extendedEval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: true).ConfigureAwait(false);
        var a3 = calibrationRunner.Build(a3Eval, "A3", options);
        var extended = calibrationRunner.Build(extendedEval, "Extended", options);
        Directory.CreateDirectory(outputDirectory);
        var a3Path = Path.Combine(outputDirectory, CandidateRerankerListwiseCalibrationRunner.A3ReportFileName);
        var extendedPath = Path.Combine(outputDirectory, CandidateRerankerListwiseCalibrationRunner.ExtendedReportFileName);
        var markdownPath = Path.Combine(outputDirectory, CandidateRerankerListwiseCalibrationRunner.MarkdownReportFileName);
        await WriteTextAsync(JsonSerializer.Serialize(a3, JsonOptions), a3Path, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extended, JsonOptions), extendedPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
                CandidateRerankerListwiseCalibrationRunner.BuildMarkdownReport(a3, extended),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker listwise calibration A3: {Path.GetFullPath(a3Path)}");
        Console.WriteLine($"[Eval] Candidate reranker listwise calibration Extended: {Path.GetFullPath(extendedPath)}");
        Console.WriteLine($"[Eval] Candidate reranker listwise calibration markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 regressions={a3.RegressionCount}; lowMargin={a3.LowMarginDecisionCount}; priorityMismatch={a3.FormalPriorityMismatchCount}; rec={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended regressions={extended.RegressionCount}; lowMargin={extended.LowMarginDecisionCount}; priorityMismatch={extended.FormalPriorityMismatchCount}; rec={extended.Recommendation}");
    }

    private static async Task ExecuteCandidateRerankerFormalPriorityAlignmentAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, CandidateRerankerFormalPriorityAlignmentRunner.DefaultOutputDirectory);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var options = new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            ShadowProfile = CandidateRerankerShadowProfiles.FormalPriorityAwareWithAbstainV1,
            MaxCandidatesPerTrace = 50,
            RecordTopK = topK > 0 ? topK : 10,
            RecordWouldChange = true
        };
        var evalRunner = new ContextEvalRunner();
        var alignmentRunner = new CandidateRerankerFormalPriorityAlignmentRunner();
        var a3Eval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: false).ConfigureAwait(false);
        var extendedEval = await evalRunner.RunAsync(contextsRoot, categoryFilter: null, includeSeedBatches: true).ConfigureAwait(false);
        var a3 = alignmentRunner.Build(a3Eval, "A3", options);
        var extended = alignmentRunner.Build(extendedEval, "Extended", options);
        Directory.CreateDirectory(outputDirectory);
        var a3Path = Path.Combine(outputDirectory, CandidateRerankerFormalPriorityAlignmentRunner.A3ReportFileName);
        var extendedPath = Path.Combine(outputDirectory, CandidateRerankerFormalPriorityAlignmentRunner.ExtendedReportFileName);
        var markdownPath = Path.Combine(outputDirectory, CandidateRerankerFormalPriorityAlignmentRunner.MarkdownReportFileName);
        await WriteTextAsync(JsonSerializer.Serialize(a3, JsonOptions), a3Path, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extended, JsonOptions), extendedPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
                CandidateRerankerFormalPriorityAlignmentRunner.BuildMarkdownReport(a3, extended),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker formal priority alignment A3: {Path.GetFullPath(a3Path)}");
        Console.WriteLine($"[Eval] Candidate reranker formal priority alignment Extended: {Path.GetFullPath(extendedPath)}");
        Console.WriteLine($"[Eval] Candidate reranker formal priority alignment markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 regressions={a3.RegressionCount}; recovered={a3.RecoveredCount}; unexplained={a3.UnexplainedMismatchCount}; abstain={a3.AbstainCount}; netAfterAbstain={a3.NetGainAfterAbstain}; rec={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended regressions={extended.RegressionCount}; recovered={extended.RecoveredCount}; unexplained={extended.UnexplainedMismatchCount}; abstain={extended.AbstainCount}; netAfterAbstain={extended.NetGainAfterAbstain}; rec={extended.Recommendation}");
    }

    private static async Task ExecuteCandidateRerankerShadowTraceQualityAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var workspaceId = CommandHelpers.GetOption(args, "--workspace")
            ?? service.State.WorkspaceId;
        var collectionId = CommandHelpers.GetOption(args, "--collection")
            ?? service.State.CollectionId;
        var take = CommandHelpers.GetIntOption(args, "--take", 200);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, CandidateRerankerShadowTraceQualityReportBuilder.DefaultOutputDirectory, CandidateRerankerShadowTraceQualityReportBuilder.ReportFileName);
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, CandidateRerankerShadowTraceQualityReportBuilder.DefaultOutputDirectory, CandidateRerankerShadowTraceQualityReportBuilder.MarkdownReportFileName);

        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records;
        if (service.State.IsServiceMode && service.State.ServiceClient is not null)
        {
            records = await service.State.ServiceClient
                .GetRankerShadowTracesAsync(workspaceId, collectionId, take > 0 ? take : 200, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            records = await new RankerShadowTraceExportService(service.State.RetrievalTraceStore)
                .QueryAsync(workspaceId, collectionId, take > 0 ? take : 200, cancellationToken)
                .ConfigureAwait(false);
        }

        var report = new CandidateRerankerShadowTraceQualityReportBuilder()
            .Build(records, workspaceId, collectionId, topK > 0 ? topK : 10);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(CandidateRerankerShadowTraceQualityReportBuilder.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Candidate reranker shadow trace quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Candidate reranker shadow trace quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] traces={report.TraceCount}; candidates={report.CandidateCount}; changeTop1={report.WouldChangeTop1Count}; risk={report.LifecycleRiskCount + report.DeprecatedRiskCount + report.MustNotRiskCount}; rec={report.Recommendation}");
    }

    private static async Task ExecuteGraphExpansionShadowTraceQualityAsync(
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
            ?? Path.Combine(current, "learning", "graph-shadow", "graph-expansion-shadow-trace-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "learning", "graph-shadow", "graph-expansion-shadow-trace-quality-report.md");

        IReadOnlyList<GraphExpansionShadowTraceRecord> records;
        if (service.State.IsServiceMode && service.State.ServiceClient is not null)
        {
            records = await service.State.ServiceClient
                .GetGraphExpansionShadowTracesAsync(workspaceId, collectionId, take, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            records = await new GraphExpansionShadowTraceExportService(service.State.RetrievalTraceStore)
                .QueryAsync(workspaceId, collectionId, take, cancellationToken)
                .ConfigureAwait(false);
        }

        var builder = new GraphExpansionShadowTraceQualityReportBuilder();
        var report = builder.Build(records, workspaceId, collectionId);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                GraphExpansionShadowTraceQualityReportBuilder.BuildMarkdownReport(report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Graph expansion shadow trace quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Graph expansion shadow trace quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Traces={report.TraceCount}; accepted={report.AcceptedRelationCount}; blocked={report.BlockedRelationCount}; audit={report.AuditContextCount}; conflict={report.ConflictEvidenceCount}");
        Console.WriteLine($"[Eval] Risks: afterRouting={report.RiskAfterRoutingCount}; wrongSection={report.WrongSectionRiskCount}; missingEvidence={report.MissingEvidenceCount}; next={report.Recommendation}");
    }

    private static async Task ExecuteRelationExpansionProfileShadowAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, "eval", "relation-expansion-profile-shadow-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "eval", "relation-expansion-profile-shadow-report.md");
        const string workspaceId = "relation-expansion-shadow";
        const string collectionId = "profile-fixture";

        var relationStore = new InMemoryRelationStore();
        await SeedRelationExpansionShadowFixtureAsync(relationStore, workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);

        var profileRegistry = new RelationExpansionProfileRegistry();
        var validator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var previewService = new RelationExpansionPreviewService(relationStore, profileRegistry, validator);
        var builder = new RelationExpansionProfileShadowReportBuilder(profileRegistry, previewService);
        var report = await builder
            .BuildAsync(workspaceId, collectionId, ["item-normal", "item-audit", "item-old", "item-depth"], cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                RelationExpansionProfileShadowReportBuilder.BuildMarkdownReport(report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Relation expansion profile shadow report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Relation expansion profile shadow markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Profiles={report.ProfileCount}; samples={report.SampleCount}; accepted={report.AcceptedRelationCount}; blocked={report.BlockedRelationCount}");
    }

    private static async Task SeedRelationExpansionShadowFixtureAsync(
        IRelationStore relationStore,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relations = new List<ContextRelation>
        {
            Relation("rel-normal-contains", "item-normal", "target-active", "contains", 0.9, 0.9, now, ["evidence:normal"]),
            Relation("rel-normal-replaces-old", "item-normal", "target-old", ContextRelationTypes.Replaces, 1.0, 1.0, now, ["review:stable-1"], targetLifecycle: StableMemoryLifecycle.Deprecated),
            Relation("rel-normal-superseded-by-new", "item-old", "target-new", ContextRelationTypes.SupersededBy, 1.0, 1.0, now, ["review:stable-2"], targetLifecycle: StableMemoryLifecycle.Active),
            Relation("rel-normal-audit-only", "item-normal", "target-replaced", "replaced_by", 1.0, 1.0, now, ["review:stable-3"]),
            Relation("rel-normal-low-confidence", "item-normal", "target-low", "references", 0.5, 0.2, now, ["evidence:low"]),
            Relation("rel-normal-missing-evidence", "item-normal", "target-no-evidence", "references", 0.5, 0.9, now, []),
            Relation("rel-audit-historical", "item-audit", "target-historical", ContextRelationTypes.Replaces, 1.0, 1.0, now, ["review:stable-4"], targetLifecycle: StableMemoryLifecycle.Deprecated),
            Relation("rel-depth-1", "item-depth", "target-depth-1", "supports", 0.8, 0.9, now, ["evidence:depth-1"]),
            Relation("rel-depth-2", "target-depth-1", "target-depth-2", "supports", 0.8, 0.9, now, ["evidence:depth-2"])
        };

        for (var i = 0; i < 10; i++)
        {
            relations.Add(Relation(
                $"rel-fanout-{i:00}",
                "item-normal",
                $"target-fanout-{i:00}",
                "contains",
                0.3,
                0.8,
                now.AddSeconds(i),
                [$"evidence:fanout-{i:00}"]));
        }

        await relationStore.SaveManyAsync(relations, cancellationToken).ConfigureAwait(false);

        ContextRelation Relation(
            string id,
            string sourceId,
            string targetId,
            string relationType,
            double weight,
            double confidence,
            DateTimeOffset createdAt,
            IReadOnlyList<string> evidenceRefs,
            string lifecycle = "Active",
            string targetLifecycle = "Active")
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = lifecycle,
                ["reviewStatus"] = RelationReviewStatuses.Reviewed,
                ["createdFrom"] = "relation_expansion_profile_shadow_fixture",
                ["targetLifecycle"] = targetLifecycle,
                ["targetExists"] = "true"
            };
            if (evidenceRefs.Count > 0)
            {
                metadata["evidenceRefs"] = string.Join(",", evidenceRefs);
            }

            return new ContextRelation
            {
                Id = id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SourceId = sourceId,
                TargetId = targetId,
                RelationType = relationType,
                Weight = weight,
                Confidence = confidence,
                SourceRefs = evidenceRefs.ToArray(),
                Metadata = metadata,
                CreatedAt = createdAt
            };
        }
    }

    private static async Task ExecuteRelationCorpusHygieneAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, "eval", "relation-corpus-hygiene-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "eval", "relation-corpus-hygiene-report.md");

        var builder = new RelationCorpusHygieneReportBuilder();
        var report = await builder.BuildAsync(contextsRoot, cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                RelationCorpusHygieneReportBuilder.BuildMarkdownReport(report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Relation corpus hygiene report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Relation corpus hygiene markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Relations={report.RelationCount}; legacy={report.LegacyRelationTypes.Values.Sum(item => item.Count)}; unknown={report.UnknownRelationTypes.Values.Sum()}; missingEvidence={report.MissingEvidenceRelations.Count}; backfill={report.BackfillCandidates.Count}");
    }

    private static async Task ExecuteRelationExpansionShadowEvalAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine(current, "eval", "relation-expansion-shadow-eval-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine(current, "eval", "relation-expansion-shadow-eval-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "eval", "relation-expansion-shadow-eval.md");

        var runner = new RelationExpansionShadowEvalRunner();
        var a3Report = await runner.RunAsync(
                contextsRoot,
                categoryFilter,
                includeSeedBatches: false,
                cancellationToken)
            .ConfigureAwait(false);
        var extendedReport = await runner.RunAsync(
                contextsRoot,
                categoryFilter,
                includeSeedBatches: true,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                RelationExpansionShadowEvalRunner.BuildMarkdownReport(a3Report, extendedReport),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Relation expansion shadow eval A3 report: {Path.GetFullPath(a3OutputPath)}");
        Console.WriteLine($"[Eval] Relation expansion shadow eval Extended report: {Path.GetFullPath(extendedOutputPath)}");
        Console.WriteLine($"[Eval] Relation expansion shadow eval markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 samples={a3Report.TotalEvalSamples}; rows={a3Report.SampleCount}; formalChanged={a3Report.FormalOutputChanged}; selectedSetChanged={a3Report.SelectedSetChanged}");
        Console.WriteLine($"[Eval] Extended samples={extendedReport.TotalEvalSamples}; rows={extendedReport.SampleCount}; formalChanged={extendedReport.FormalOutputChanged}; selectedSetChanged={extendedReport.SelectedSetChanged}");
    }

    private static async Task ExecuteGraphExpansionOptInComparisonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine(current, "eval", "graph-expansion-optin-comparison-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine(current, "eval", "graph-expansion-optin-comparison-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "eval", "graph-expansion-optin-comparison.md");

        var runner = new GraphExpansionOptInComparisonRunner();
        var a3Report = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken)
            .ConfigureAwait(false);
        var extendedReport = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                GraphExpansionOptInComparisonRunner.BuildMarkdownReport(a3Report, extendedReport),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Graph expansion opt-in comparison A3 report: {Path.GetFullPath(a3OutputPath)}");
        Console.WriteLine($"[Eval] Graph expansion opt-in comparison Extended report: {Path.GetFullPath(extendedOutputPath)}");
        Console.WriteLine($"[Eval] Graph expansion opt-in comparison markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] A3 normalChanged={a3Report.NormalSelectedSetChanged}; applied={a3Report.GraphExpansionAppliedCount}; fallback={a3Report.FallbackCount}; riskAfter={a3Report.RiskAfterRoutingCount}");
        Console.WriteLine($"[Eval] Extended normalChanged={extendedReport.NormalSelectedSetChanged}; applied={extendedReport.GraphExpansionAppliedCount}; fallback={extendedReport.FallbackCount}; riskAfter={extendedReport.RiskAfterRoutingCount}");
    }

    private static async Task ExecuteGraphExpansionGuardedOptInGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine(current, "eval", "graph-expansion-optin-comparison-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine(current, "eval", "graph-expansion-optin-comparison-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "eval", "graph-expansion-optin-comparison.md");
        var gateOutputPath = CommandHelpers.GetOption(args, "--gate-out")
            ?? Path.Combine(current, "eval", "graph-expansion-guarded-optin-gate.json");
        var gateMarkdownPath = CommandHelpers.GetOption(args, "--gate-md-out")
            ?? Path.Combine(current, "eval", "graph-expansion-guarded-optin-gate.md");

        var runner = new GraphExpansionOptInComparisonRunner();
        var a3Report = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken)
            .ConfigureAwait(false);
        var extendedReport = await runner
            .RunAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken)
            .ConfigureAwait(false);
        var gateReport = GraphExpansionOptInComparisonRunner.BuildGateReport(a3Report, extendedReport);

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                GraphExpansionOptInComparisonRunner.BuildMarkdownReport(a3Report, extendedReport),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(gateReport, JsonOptions), gateOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                GraphExpansionOptInComparisonRunner.BuildGateMarkdownReport(gateReport),
                gateMarkdownPath,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Graph expansion guarded opt-in gate: {Path.GetFullPath(gateOutputPath)}");
        Console.WriteLine($"[Eval] Graph expansion guarded opt-in gate markdown: {Path.GetFullPath(gateMarkdownPath)}");
        foreach (var scope in gateReport.Scopes)
        {
            Console.WriteLine($"[Eval] {scope.Scope} gate={scope.Passed}; normalChanged={scope.NormalSelectedSetChanged}; unexpectedWarning={scope.UnexpectedWarningDelta}; wrongSection={scope.WrongSectionRiskCount}; riskAfter={scope.RiskAfterRoutingCount}");
        }

        if (!gateReport.Passed)
        {
            throw new InvalidOperationException(
                $"Graph expansion guarded opt-in gate failed: {string.Join("; ", gateReport.FailedConditions)}");
        }
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

    private static async Task ExecuteVectorReindexPlanAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var request = await BuildVectorReindexRequestAsync(service, args, apply: false, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("vector", "reindex", "vector-reindex-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("vector", "reindex", "vector-reindex-report.md");

        VectorReindexResult result;
        if (service.State.IsServiceMode)
        {
            var plan = await service.CreateServiceVectorReindexPlanAsync(request, cancellationToken)
                .ConfigureAwait(false);
            result = NewVectorReindexDryRunResult(request, plan);
        }
        else
        {
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, request.SourceItems, providerOptions);
            result = await infrastructure.Executor.ExecuteAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteTextAsync(JsonSerializer.Serialize(result, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorReindexReportRenderer.ToMarkdown(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector reindex plan written: {outputPath}");
        Console.WriteLine($"[Eval] Vector reindex plan markdown written: {markdownPath}");
    }

    private static async Task ExecuteVectorReindexApplyAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (!CommandHelpers.HasFlag(args, "--confirm") && !CommandHelpers.HasFlag(args, "--yes"))
        {
            Console.WriteLine("[Eval] vector-reindex-apply requires --confirm. No vector index write was performed.");
            return;
        }

        var request = await BuildVectorReindexRequestAsync(service, args, apply: true, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("vector", "reindex", "vector-reindex-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("vector", "reindex", "vector-reindex-report.md");

        if (service.State.IsServiceMode)
        {
            var response = await service.SubmitServiceVectorReindexAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(JsonSerializer.Serialize(response, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(ServiceOperationalRenderer.RenderVectorReindexSubmit(response), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector reindex job submitted: {response.Job.JobId}");
            return;
        }

        var providerOptions = BuildEmbeddingProviderOptions(args);
        var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
        if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
        {
            var blockedResult = NewVectorReindexProviderBlockedResult(request, providerDiagnostics);
            await WriteTextAsync(JsonSerializer.Serialize(blockedResult, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(VectorReindexReportRenderer.ToMarkdown(blockedResult), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector reindex apply blocked by provider diagnostics: {outputPath}");
            Console.WriteLine($"[Eval] diagnostics={providerDiagnostics.Count}");
            return;
        }

        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: true, request.SourceItems, providerOptions);
        var result = await infrastructure.Executor.ExecuteAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(result, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorReindexReportRenderer.ToMarkdown(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector reindex apply written: {outputPath}");
        Console.WriteLine($"[Eval] created={result.Summary.Created}, updated={result.Summary.Updated}, failed={result.Summary.Failed}");
    }

    private static async Task ExecuteVectorIndexDiagnosticsAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("vector", "reindex", "vector-index-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("vector", "reindex", "vector-index-diagnostics.md");

        VectorIndexDiagnosticsReport report;
        if (service.State.IsServiceMode)
        {
            report = await service.State.ServiceClient!.GetVectorDiagnosticsAsync(
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
            report = await infrastructure.IndexService.GetDiagnosticsAsync(
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildVectorIndexDiagnosticsMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector diagnostics written: {outputPath}");
    }

    private static async Task ExecuteVectorIndexCoverageAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var request = await BuildVectorCoverageReindexRequestAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("vector", "reindex", "vector-index-coverage-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("vector", "reindex", "vector-index-coverage-report.md");

        VectorReindexPlan plan;
        VectorIndexDiagnosticsReport diagnostics;
        VectorIndexStatusResponse status;
        if (service.State.IsServiceMode)
        {
            plan = await service.CreateServiceVectorReindexPlanAsync(request, cancellationToken)
                .ConfigureAwait(false);
            diagnostics = await service.State.ServiceClient!.GetVectorDiagnosticsAsync(
                request.WorkspaceId,
                request.CollectionId,
                cancellationToken).ConfigureAwait(false);
            status = await service.State.ServiceClient!.GetVectorStatusAsync(
                request.WorkspaceId,
                request.CollectionId,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, request.SourceItems, providerOptions);
            plan = await infrastructure.Executor.CreatePlanOnlyAsync(request, cancellationToken)
                .ConfigureAwait(false);
            diagnostics = await infrastructure.IndexService.GetDiagnosticsAsync(
                request.WorkspaceId,
                request.CollectionId,
                cancellationToken).ConfigureAwait(false);
            status = await infrastructure.IndexService.GetStatusAsync(
                request.WorkspaceId,
                request.CollectionId,
                cancellationToken).ConfigureAwait(false);
        }

        var report = VectorIndexCoverageReportBuilder.Build(plan, diagnostics, status);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorIndexCoverageReportBuilder.ToMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector index coverage written: {outputPath}");
        Console.WriteLine($"[Eval] coverage={report.CoverageRate:P2}, recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorLifecycleMetadataCoverageAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("eval", "vector-lifecycle-metadata-coverage.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-lifecycle-metadata-coverage.md");
        var operationId = $"vector-lifecycle-metadata-coverage-{Guid.NewGuid():N}";
        var builder = new VectorLifecycleMetadataCoverageReportBuilder();

        VectorLifecycleMetadataCoverageReport report;
        if (service.State.IsServiceMode)
        {
            var request = await BuildVectorReindexRequestAsync(service, args, apply: false, cancellationToken)
                .ConfigureAwait(false);
            var plan = await service.CreateServiceVectorReindexPlanAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var diagnostics = await service.State.ServiceClient!.GetVectorDiagnosticsAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            var status = await service.State.ServiceClient!.GetVectorStatusAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            report = builder.Build(
                operationId,
                workspaceId,
                collectionId,
                PlanItemsToSourceItems(plan),
                Array.Empty<VectorIndexEntry>(),
                diagnostics,
                status);
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
            var entries = await infrastructure.Store.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                EmbeddingProvider = providerOptions.ProviderId,
                EmbeddingModel = providerOptions.EmbeddingModel,
                Take = 100_000,
                IncludeVector = false
            }, cancellationToken).ConfigureAwait(false);
            var diagnostics = await infrastructure.IndexService.GetDiagnosticsAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            var status = await BuildProviderScopedStatusAsync(infrastructure, workspaceId, collectionId, providerOptions, cancellationToken)
                .ConfigureAwait(false);
            report = builder.Build(
                operationId,
                workspaceId,
                collectionId,
                sourceItems,
                entries,
                diagnostics,
                status);
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(builder.ToMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector lifecycle metadata coverage written: {outputPath}");
        Console.WriteLine($"[Eval] known={report.KnownLifecycleCount}/{report.TotalVectorSourceItems}; unknown={report.UnknownLifecycleCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorLifecycleMetadataBackfillAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        bool apply,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("eval", apply ? "vector-lifecycle-metadata-backfill-result.json" : "vector-lifecycle-metadata-backfill-plan.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", apply ? "vector-lifecycle-metadata-backfill-result.md" : "vector-lifecycle-metadata-backfill-plan.md");
        var confirmed = CommandHelpers.HasFlag(args, "--confirm") || CommandHelpers.HasFlag(args, "--yes");
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var planner = new VectorLifecycleMetadataBackfillPlanner();

        if (service.State.IsServiceMode)
        {
            Console.WriteLine("[Eval] vector lifecycle metadata backfill 当前只支持本地 CLI vector index；Service Mode 请先导出或使用本地存储执行。");
            return;
        }

        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
        var entries = await infrastructure.Store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerOptions.ProviderId,
            EmbeddingModel = providerOptions.EmbeddingModel,
            Take = 100_000,
            IncludeVector = apply
        }, cancellationToken).ConfigureAwait(false);

        var plan = planner.CreatePlan(
            $"vector-lifecycle-metadata-backfill-{Guid.NewGuid():N}",
            workspaceId,
            collectionId,
            providerOptions,
            sourceItems,
            entries,
            dryRun: !apply || !confirmed);

        if (!apply)
        {
            await WriteTextAsync(JsonSerializer.Serialize(plan, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(planner.ToMarkdown(plan), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector lifecycle metadata backfill plan written: {outputPath}");
            Console.WriteLine($"[Eval] unknown={plan.UnknownLifecycleBefore}; auto={plan.AutoResolvableCount}; manual={plan.ManualReviewRequiredCount}; expectedCoverage={plan.ExpectedCoverageAfter:P2}");
            return;
        }

        if (!confirmed)
        {
            Console.WriteLine("[Eval] vector-lifecycle-metadata-backfill-apply requires --confirm. No vector metadata write was performed.");
        }

        var result = await planner.ApplyAsync(plan, infrastructure.Store, confirmed, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(result, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(planner.ToMarkdown(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector lifecycle metadata backfill result written: {outputPath}");
        Console.WriteLine($"[Eval] applied={result.Applied}; updated={result.UpdatedEntries}; skipped={result.SkippedEntries}; failed={result.FailedCount}");
    }

    private static IReadOnlyList<VectorReindexSourceItem> PlanItemsToSourceItems(VectorReindexPlan plan)
    {
        return plan.Items
            .Where(item => item.Action is "Create" or "Update" or "Skip")
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new VectorReindexSourceItem
            {
                ItemId = item.ItemId,
                ItemKind = item.ItemKind,
                Layer = item.Layer,
                Text = item.ItemId,
                UpdatedAt = plan.CreatedAt,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
            })
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task ExecuteVectorQueryPreviewAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var queryText = CommandHelpers.GetOption(args, "--query")
            ?? CommandHelpers.GetOption(args, "-q");
        if (string.IsNullOrWhiteSpace(queryText))
        {
            Console.WriteLine("[Eval] vector-query-preview requires --query <text>.");
            return;
        }

        var request = BuildVectorQueryPreviewRequest(service, args, queryText);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("vector", "query", "vector-query-preview.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("vector", "query", "vector-query-preview.md");

        VectorQueryPreviewResult result;
        if (service.State.IsServiceMode)
        {
            result = await service.State.ServiceClient!.PreviewVectorQueryAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                result = NewProviderBlockedVectorQueryPreviewResult(request, providerDiagnostics);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                result = await infrastructure.QueryPreviewService.PreviewAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(result, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ServiceOperationalRenderer.RenderVectorQueryPreview(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector query preview written: {outputPath}");
    }

    private static async Task ExecuteVectorQueryShadowEvalAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var layer = CommandHelpers.GetOption(args, "--layer");
        var itemKind = CommandHelpers.GetOption(args, "--item-kind");
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var lowConfidenceThreshold = GetDoubleOption(args, "--low-confidence-threshold") ?? 0.25;
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-query-shadow-eval-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-query-shadow-eval-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-query-shadow-eval.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorQueryShadowEvalReport a3Report;
        VectorQueryShadowEvalReport extendedReport;
        EmbeddingProviderOptions? providerOptionsForReport = null;
        if (service.State.IsServiceMode)
        {
            a3Report = await RunVectorQueryShadowEvalWithClientAsync(
                service.State.ServiceClient!,
                a3Samples,
                workspaceId,
                collectionId,
                topK,
                layer,
                itemKind,
                minSimilarity,
                lowConfidenceThreshold,
                profileId,
                cancellationToken).ConfigureAwait(false);
            extendedReport = await RunVectorQueryShadowEvalWithClientAsync(
                service.State.ServiceClient!,
                extendedSamples,
                workspaceId,
                collectionId,
                topK,
                layer,
                itemKind,
                minSimilarity,
                lowConfidenceThreshold,
                profileId,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            providerOptionsForReport = providerOptions;
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                a3Report = NewUnavailableShadowReport(a3Samples.Count, providerDiagnostics);
                extendedReport = NewUnavailableShadowReport(extendedSamples.Count, providerDiagnostics);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var runner = new VectorQueryShadowEvalRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    workspaceId,
                    collectionId,
                    topK,
                    layer,
                    itemKind,
                    minSimilarity,
                    lowConfidenceThreshold,
                    profileId,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    workspaceId,
                    collectionId,
                    topK,
                    layer,
                    itemKind,
                    minSimilarity,
                    lowConfidenceThreshold,
                    profileId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (providerOptionsForReport is not null)
        {
            a3Report = AttachProviderMetadata(a3Report, providerOptionsForReport);
            extendedReport = AttachProviderMetadata(extendedReport, providerOptionsForReport);
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorQueryShadowEvalRunner.BuildMarkdownReport(a3Report, extendedReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector query shadow eval A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector query shadow eval Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] Vector query shadow eval markdown written: {markdownPath}");
    }

    private static async Task ExecuteVectorQueryProfileSweepAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var isQwen3Provider = IsQwen3ProviderRequest(args);
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-query-profile-sweep-a3.json")
                : Path.Combine("eval", "vector-query-profile-sweep-a3.json"));
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-query-profile-sweep-extended.json")
                : Path.Combine("eval", "vector-query-profile-sweep-extended.json"));
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-query-profile-sweep.md")
                : Path.Combine("eval", "vector-query-profile-sweep.md"));
        var qualityJsonPath = CommandHelpers.GetOption(args, "--quality-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-embedding-quality-baseline.json")
                : Path.Combine("eval", "vector-embedding-quality-baseline.json"));
        var qualityMarkdownPath = CommandHelpers.GetOption(args, "--quality-md-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-embedding-quality-baseline.md")
                : Path.Combine("eval", "vector-embedding-quality-baseline.md"));
        var providerComparisonJsonPath = CommandHelpers.GetOption(args, "--provider-comparison-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-embedding-provider-comparison.json")
                : Path.Combine("eval", "vector-embedding-provider-comparison.json"));
        var providerComparisonMarkdownPath = CommandHelpers.GetOption(args, "--provider-comparison-md-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("vector-embedding-provider-comparison.md")
                : Path.Combine("eval", "vector-embedding-provider-comparison.md"));
        var providerDiagnostics = BuildProviderDiagnostics(providerOptions);

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
        VectorQueryProfileSweepReport a3Report;
        VectorQueryProfileSweepReport extendedReport;
        VectorEmbeddingQualityBaselineReport qualityReport;
        if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
        {
            a3Report = NewUnavailableSweepReport(a3Samples.Count, providerOptions, providerDiagnostics);
            extendedReport = NewUnavailableSweepReport(extendedSamples.Count, providerOptions, providerDiagnostics);
            qualityReport = NewUnavailableQualityReport(extendedSamples.Count, providerOptions, providerDiagnostics);
        }
        else
        {
            var runner = new VectorQueryProfileSweepRunner(infrastructure.QueryPreviewService);
            a3Report = await runner.RunAsync(
                a3Samples,
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            extendedReport = await runner.RunAsync(
                extendedSamples,
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            qualityReport = await runner.BuildEmbeddingQualityBaselineAsync(
                extendedSamples,
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
        }

        a3Report = AttachProviderMetadata(a3Report, providerOptions);
        extendedReport = AttachProviderMetadata(extendedReport, providerOptions);

        var providerComparisonResults = new List<VectorEmbeddingProviderComparisonResult>();
        foreach (var comparisonOptions in BuildProviderComparisonOptions(args))
        {
            providerComparisonResults.Add(await BuildProviderComparisonResultAsync(
                service,
                sourceItems,
                extendedSamples,
                workspaceId,
                collectionId,
                comparisonOptions,
                cancellationToken).ConfigureAwait(false));
        }

        var providerComparisonReport = VectorEmbeddingProviderComparisonReportBuilder.Build(providerComparisonResults);

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorQueryProfileSweepRunner.BuildMarkdownReport(a3Report, extendedReport, qualityReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(qualityReport, JsonOptions), qualityJsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorQueryProfileSweepRunner.BuildEmbeddingQualityMarkdown(qualityReport), qualityMarkdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(providerComparisonReport, JsonOptions), providerComparisonJsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorEmbeddingProviderComparisonReportBuilder.ToMarkdown(providerComparisonReport), providerComparisonMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector query profile sweep A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector query profile sweep Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] Vector query profile sweep markdown written: {markdownPath}");
        Console.WriteLine($"[Eval] Vector embedding quality baseline written: {qualityJsonPath}");
        Console.WriteLine($"[Eval] Vector embedding provider comparison written: {providerComparisonJsonPath}");
    }

    private static async Task ExecuteVectorResidualRiskAuditAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-residual-risk-audit-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-residual-risk-audit-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-residual-risk-audit.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorResidualRiskAuditReport a3Report;
        VectorResidualRiskAuditReport extendedReport;
        if (service.State.IsServiceMode)
        {
            a3Report = await RunVectorResidualRiskAuditWithClientAsync(
                service.State.ServiceClient!,
                a3Samples,
                workspaceId,
                collectionId,
                topK,
                profileId,
                minSimilarity,
                cancellationToken).ConfigureAwait(false);
            extendedReport = await RunVectorResidualRiskAuditWithClientAsync(
                service.State.ServiceClient!,
                extendedSamples,
                workspaceId,
                collectionId,
                topK,
                profileId,
                minSimilarity,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                a3Report = NewUnavailableResidualRiskAuditReport(a3Samples.Count, providerOptions, providerDiagnostics, profileId);
                extendedReport = NewUnavailableResidualRiskAuditReport(extendedSamples.Count, providerOptions, providerDiagnostics, profileId);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var runner = new VectorResidualRiskAuditRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorResidualRiskAuditRunner.BuildMarkdownReport(a3Report, extendedReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector residual risk audit A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector residual risk audit Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] residualRisk={extendedReport.ResidualRiskCount}; recommendation={extendedReport.Recommendation}");
    }

    private static async Task ExecuteVectorRecallLossAuditAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var layer = CommandHelpers.GetOption(args, "--layer");
        var itemKind = CommandHelpers.GetOption(args, "--item-kind");
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var lowConfidenceThreshold = GetDoubleOption(args, "--low-confidence-threshold") ?? 0.25;
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-recall-loss-audit-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-recall-loss-audit-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-recall-loss-audit.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorRecallLossAuditReport a3Report;
        VectorRecallLossAuditReport extendedReport;
        if (service.State.IsServiceMode)
        {
            a3Report = await RunVectorRecallLossAuditWithClientAsync(
                service.State.ServiceClient!,
                a3Samples,
                workspaceId,
                collectionId,
                topK,
                layer,
                itemKind,
                minSimilarity,
                lowConfidenceThreshold,
                profileId,
                cancellationToken).ConfigureAwait(false);
            extendedReport = await RunVectorRecallLossAuditWithClientAsync(
                service.State.ServiceClient!,
                extendedSamples,
                workspaceId,
                collectionId,
                topK,
                layer,
                itemKind,
                minSimilarity,
                lowConfidenceThreshold,
                profileId,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                a3Report = NewUnavailableRecallLossAuditReport(a3Samples.Count, providerOptions, providerDiagnostics, profileId, topK, minSimilarity, layer, itemKind);
                extendedReport = NewUnavailableRecallLossAuditReport(extendedSamples.Count, providerOptions, providerDiagnostics, profileId, topK, minSimilarity, layer, itemKind);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var indexEntries = await infrastructure.Store.ListAsync(new VectorIndexQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    EmbeddingProvider = providerOptions.ProviderId,
                    EmbeddingModel = providerOptions.EmbeddingModel,
                    Take = 100_000,
                    IncludeVector = false
                }, cancellationToken).ConfigureAwait(false);
                var runner = new VectorRecallLossAuditRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    indexEntries,
                    workspaceId,
                    collectionId,
                    topK,
                    layer,
                    itemKind,
                    minSimilarity,
                    profileId,
                    lowConfidenceThreshold,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    indexEntries,
                    workspaceId,
                    collectionId,
                    topK,
                    layer,
                    itemKind,
                    minSimilarity,
                    profileId,
                    lowConfidenceThreshold,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorRecallLossAuditRunner.BuildMarkdownReport(a3Report, extendedReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector recall loss audit A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector recall loss audit Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] A3 recall={a3Report.MustHitRecallAfterPolicy:P2}; recommendation={a3Report.Recommendation}");
        Console.WriteLine($"[Eval] Extended recall={extendedReport.MustHitRecallAfterPolicy:P2}; recommendation={extendedReport.Recommendation}");
    }

    private static async Task ExecuteVectorSafeRecallRecoveryAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-safe-recall-recovery-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-safe-recall-recovery-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-safe-recall-recovery.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorSafeRecallRecoveryReport a3Report;
        VectorSafeRecallRecoveryReport extendedReport;
        if (service.State.IsServiceMode)
        {
            a3Report = NewUnavailableSafeRecallRecoveryReport(a3Samples.Count, "service mode 暂不暴露全量 vector index entries，safe recall recovery 需在本地离线模式运行。");
            extendedReport = NewUnavailableSafeRecallRecoveryReport(extendedSamples.Count, "service mode 暂不暴露全量 vector index entries，safe recall recovery 需在本地离线模式运行。");
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                var message = string.Join("; ", providerDiagnostics.Select(item => item.Type).Distinct(StringComparer.OrdinalIgnoreCase));
                a3Report = NewUnavailableSafeRecallRecoveryReport(a3Samples.Count, message);
                extendedReport = NewUnavailableSafeRecallRecoveryReport(extendedSamples.Count, message);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var indexEntries = await infrastructure.Store.ListAsync(new VectorIndexQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    EmbeddingProvider = providerOptions.ProviderId,
                    EmbeddingModel = providerOptions.EmbeddingModel,
                    Take = 100_000,
                    IncludeVector = false
                }, cancellationToken).ConfigureAwait(false);
                var runner = new VectorSafeRecallRecoveryRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    indexEntries,
                    workspaceId,
                    collectionId,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    indexEntries,
                    workspaceId,
                    collectionId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorSafeRecallRecoveryRunner.BuildMarkdownReport(a3Report, extendedReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector safe recall recovery A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector safe recall recovery Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] A3 best recall={a3Report.BestSafeSweep?.MustHitRecallAfterPolicy:P2}; recommendation={a3Report.Recommendation}");
        Console.WriteLine($"[Eval] Extended best recall={extendedReport.BestSafeSweep?.MustHitRecallAfterPolicy:P2}; recommendation={extendedReport.Recommendation}");
    }

    private static async Task ExecuteVectorRankerFusionShadowAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var lowConfidenceThreshold = GetDoubleOption(args, "--low-confidence-threshold") ?? 0.25;
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-ranker-fusion-shadow-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-ranker-fusion-shadow-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-ranker-fusion-shadow.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorRankerFusionShadowReport a3Report;
        VectorRankerFusionShadowReport extendedReport;
        if (service.State.IsServiceMode)
        {
            a3Report = VectorRankerFusionShadowRunner.NewUnavailableReport(
                a3Samples.Count,
                string.Empty,
                string.Empty,
                "service mode 当前不执行离线 fusion shadow；请在本地 eval 模式运行。");
            extendedReport = VectorRankerFusionShadowRunner.NewUnavailableReport(
                extendedSamples.Count,
                string.Empty,
                string.Empty,
                "service mode 当前不执行离线 fusion shadow；请在本地 eval 模式运行。");
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                var message = string.Join("; ", providerDiagnostics.Select(item => $"{item.Type}: {item.Message}"));
                a3Report = VectorRankerFusionShadowRunner.NewUnavailableReport(
                    a3Samples.Count,
                    providerOptions.ProviderId,
                    providerOptions.EmbeddingModel,
                    message);
                extendedReport = VectorRankerFusionShadowRunner.NewUnavailableReport(
                    extendedSamples.Count,
                    providerOptions.ProviderId,
                    providerOptions.EmbeddingModel,
                    message);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var runner = new VectorRankerFusionShadowRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    lowConfidenceThreshold,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    lowConfidenceThreshold,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorRankerFusionShadowRunner.BuildMarkdownReport(a3Report, extendedReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector ranker fusion shadow A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector ranker fusion shadow Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] A3 best={a3Report.BestResult?.Strategy ?? "-"} recall={a3Report.BestResult?.MustHitRecallFusion:P2}; recommendation={a3Report.Recommendation}");
        Console.WriteLine($"[Eval] Extended best={extendedReport.BestResult?.Strategy ?? "-"} recall={extendedReport.BestResult?.MustHitRecallFusion:P2}; recommendation={extendedReport.Recommendation}");
    }

    private static async Task ExecuteVectorRepresentationBenchmarkAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-representation-benchmark-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-representation-benchmark-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-representation-benchmark.md");
        var auditA3OutputPath = CommandHelpers.GetOption(args, "--audit-out-a3")
            ?? Path.Combine("eval", "vector-missset-representation-audit-a3.json");
        var auditExtendedOutputPath = CommandHelpers.GetOption(args, "--audit-out-extended")
            ?? Path.Combine("eval", "vector-missset-representation-audit-extended.json");
        var auditMarkdownPath = CommandHelpers.GetOption(args, "--audit-md-out")
            ?? Path.Combine("eval", "vector-missset-representation-audit.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorRepresentationBenchmarkReport a3Benchmark;
        VectorRepresentationBenchmarkReport extendedBenchmark;
        VectorMissSetRepresentationAuditReport a3Audit;
        VectorMissSetRepresentationAuditReport extendedAudit;
        if (service.State.IsServiceMode)
        {
            a3Benchmark = NewUnavailableRepresentationBenchmark(a3Samples.Count, "service mode 不执行本地 representation benchmark；请在本地 eval 模式运行。");
            extendedBenchmark = NewUnavailableRepresentationBenchmark(extendedSamples.Count, "service mode 不执行本地 representation benchmark；请在本地 eval 模式运行。");
            a3Audit = NewUnavailableMissSetAudit(a3Samples.Count, "service mode 当前不暴露全量 vector source item，miss-set representation audit 需在本地离线模式运行。");
            extendedAudit = NewUnavailableMissSetAudit(extendedSamples.Count, "service mode 当前不暴露全量 vector source item，miss-set representation audit 需在本地离线模式运行。");
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                var message = string.Join("; ", providerDiagnostics.Select(item => $"{item.Type}: {item.Message}"));
                a3Benchmark = NewUnavailableRepresentationBenchmark(a3Samples.Count, message, providerOptions);
                extendedBenchmark = NewUnavailableRepresentationBenchmark(extendedSamples.Count, message, providerOptions);
                a3Audit = NewUnavailableMissSetAudit(a3Samples.Count, message, providerOptions);
                extendedAudit = NewUnavailableMissSetAudit(extendedSamples.Count, message, providerOptions);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var indexEntries = await infrastructure.Store.ListAsync(new VectorIndexQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    EmbeddingProvider = providerOptions.ProviderId,
                    EmbeddingModel = providerOptions.EmbeddingModel,
                    Take = 100_000,
                    IncludeVector = false
                }, cancellationToken).ConfigureAwait(false);
                var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
                var runner = new VectorMissSetRepresentationAuditRunner(infrastructure.QueryPreviewService, generator);
                a3Audit = await runner.RunMissSetAuditAsync(
                    a3Samples,
                    sourceItems,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken).ConfigureAwait(false);
                extendedAudit = await runner.RunMissSetAuditAsync(
                    extendedSamples,
                    sourceItems,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken).ConfigureAwait(false);
                a3Benchmark = await runner.RunBenchmarkAsync(
                    a3Samples,
                    sourceItems,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    indexEntries,
                    cancellationToken).ConfigureAwait(false);
                extendedBenchmark = await runner.RunBenchmarkAsync(
                    extendedSamples,
                    sourceItems,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    indexEntries,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Benchmark, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedBenchmark, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorMissSetRepresentationAuditRunner.BuildBenchmarkMarkdownReport(a3Benchmark, extendedBenchmark), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(a3Audit, JsonOptions), auditA3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedAudit, JsonOptions), auditExtendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorMissSetRepresentationAuditRunner.BuildMissSetMarkdownReport(a3Audit, extendedAudit), auditMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector representation benchmark A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector representation benchmark Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] Vector miss-set representation audit A3 written: {auditA3OutputPath}");
        Console.WriteLine($"[Eval] Vector miss-set representation audit Extended written: {auditExtendedOutputPath}");
        Console.WriteLine($"[Eval] A3 best doc={a3Benchmark.BestResult?.DocumentRepresentationProfile ?? "-"} query={a3Benchmark.BestResult?.QueryRepresentationProfile ?? "-"} recall={a3Benchmark.BestResult?.Recall:P2}; recommendation={a3Benchmark.Recommendation}");
        Console.WriteLine($"[Eval] Extended best doc={extendedBenchmark.BestResult?.DocumentRepresentationProfile ?? "-"} query={extendedBenchmark.BestResult?.QueryRepresentationProfile ?? "-"} recall={extendedBenchmark.BestResult?.Recall:P2}; recommendation={extendedBenchmark.Recommendation}");
    }

    private static async Task ExecuteVectorRetrievalShadowReadinessGateAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var layer = CommandHelpers.GetOption(args, "--layer");
        var itemKind = CommandHelpers.GetOption(args, "--item-kind");
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var lowConfidenceThreshold = GetDoubleOption(args, "--low-confidence-threshold") ?? 0.25;
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("eval", "vector-retrieval-shadow-readiness-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-retrieval-shadow-readiness-gate.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorQueryShadowEvalReport a3Report;
        VectorQueryShadowEvalReport extendedReport;
        if (service.State.IsServiceMode)
        {
            a3Report = await RunVectorQueryShadowEvalWithClientAsync(
                service.State.ServiceClient!,
                a3Samples,
                workspaceId,
                collectionId,
                topK,
                layer,
                itemKind,
                minSimilarity,
                lowConfidenceThreshold,
                profileId,
                cancellationToken).ConfigureAwait(false);
            extendedReport = await RunVectorQueryShadowEvalWithClientAsync(
                service.State.ServiceClient!,
                extendedSamples,
                workspaceId,
                collectionId,
                topK,
                layer,
                itemKind,
                minSimilarity,
                lowConfidenceThreshold,
                profileId,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                a3Report = NewUnavailableShadowReport(a3Samples.Count, providerDiagnostics);
                extendedReport = NewUnavailableShadowReport(extendedSamples.Count, providerDiagnostics);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var runner = new VectorQueryShadowEvalRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    workspaceId,
                    collectionId,
                    topK,
                    layer,
                    itemKind,
                    minSimilarity,
                    lowConfidenceThreshold,
                    profileId,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    workspaceId,
                    collectionId,
                    topK,
                    layer,
                    itemKind,
                    minSimilarity,
                    lowConfidenceThreshold,
                    profileId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var a3Fusion = TryLoadVectorRankerFusionShadowReport(Path.Combine("eval", "vector-ranker-fusion-shadow-a3.json"));
        var extendedFusion = TryLoadVectorRankerFusionShadowReport(Path.Combine("eval", "vector-ranker-fusion-shadow-extended.json"));
        var a3Expansion = TryLoadVectorQueryExpansionShadowReport(Path.Combine("eval", "vector-query-expansion-shadow-a3.json"));
        var extendedExpansion = TryLoadVectorQueryExpansionShadowReport(Path.Combine("eval", "vector-query-expansion-shadow-extended.json"));
        var report = VectorSafeRecallRecoveryRunner.BuildReadinessGate(
            a3Report,
            extendedReport,
            a3Fusion,
            extendedFusion,
            a3Expansion,
            extendedExpansion);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorSafeRecallRecoveryRunner.BuildGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector retrieval shadow readiness gate written: {outputPath}");
        Console.WriteLine($"[Eval] passed={report.Passed}; failReasons={string.Join(",", report.FailReasons)}");
    }

    private static async Task ExecuteVectorQueryExpansionShadowAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts")
            ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category")
            ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var profileId = CommandHelpers.GetOption(args, "--profile")
            ?? CommandHelpers.GetOption(args, "--vector-profile")
            ?? VectorQueryProfileIds.NormalV1;
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? Path.Combine("eval", "vector-query-expansion-shadow-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? Path.Combine("eval", "vector-query-expansion-shadow-extended.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("eval", "vector-query-expansion-shadow.md");

        var a3Samples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: false,
            cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches: true,
            cancellationToken).ConfigureAwait(false);

        VectorQueryExpansionShadowReport a3Report;
        VectorQueryExpansionShadowReport extendedReport;
        if (service.State.IsServiceMode)
        {
            a3Report = VectorQueryExpansionShadowRunner.NewUnavailableReport(
                a3Samples.Count,
                string.Empty,
                string.Empty,
                "service mode 当前不执行离线 query expansion shadow；请在本地 eval 模式运行。");
            extendedReport = VectorQueryExpansionShadowRunner.NewUnavailableReport(
                extendedSamples.Count,
                string.Empty,
                string.Empty,
                "service mode 当前不执行离线 query expansion shadow；请在本地 eval 模式运行。");
        }
        else
        {
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var providerOptions = BuildEmbeddingProviderOptions(args);
            var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
            if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            {
                var message = string.Join("; ", providerDiagnostics.Select(item => $"{item.Type}: {item.Message}"));
                a3Report = VectorQueryExpansionShadowRunner.NewUnavailableReport(
                    a3Samples.Count,
                    providerOptions.ProviderId,
                    providerOptions.EmbeddingModel,
                    message);
                extendedReport = VectorQueryExpansionShadowRunner.NewUnavailableReport(
                    extendedSamples.Count,
                    providerOptions.ProviderId,
                    providerOptions.EmbeddingModel,
                    message);
            }
            else
            {
                var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
                var runner = new VectorQueryExpansionShadowRunner(infrastructure.QueryPreviewService);
                a3Report = await runner.RunAsync(
                    a3Samples,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken).ConfigureAwait(false);
                extendedReport = await runner.RunAsync(
                    extendedSamples,
                    workspaceId,
                    collectionId,
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorQueryExpansionShadowRunner.BuildMarkdownReport(a3Report, extendedReport), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector query expansion shadow A3 written: {a3OutputPath}");
        Console.WriteLine($"[Eval] Vector query expansion shadow Extended written: {extendedOutputPath}");
        Console.WriteLine($"[Eval] A3 best={a3Report.BestResult?.ExpansionProfile ?? "-"} recall={a3Report.BestResult?.RecallAfterExpansion:P2}; recommendation={a3Report.Recommendation}");
        Console.WriteLine($"[Eval] Extended best={extendedReport.BestResult?.ExpansionProfile ?? "-"} recall={extendedReport.BestResult?.RecallAfterExpansion:P2}; recommendation={extendedReport.Recommendation}");
    }

    private static VectorRankerFusionShadowReport? TryLoadVectorRankerFusionShadowReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRankerFusionShadowReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VectorQueryExpansionShadowReport? TryLoadVectorQueryExpansionShadowReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorQueryExpansionShadowReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VectorRepresentationBenchmarkReport NewUnavailableRepresentationBenchmark(
        int sampleCount,
        string reason,
        EmbeddingProviderOptions? options = null)
    {
        return new VectorRepresentationBenchmarkReport
        {
            OperationId = $"vector-representation-benchmark-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            ProviderId = options?.ProviderId ?? string.Empty,
            EmbeddingModel = options?.EmbeddingModel ?? string.Empty,
            Recommendation = VectorQueryShadowRecommendations.NeedsMoreIndexedData,
            FormalOutputChanged = 0,
            Warnings = string.IsNullOrWhiteSpace(reason) ? Array.Empty<string>() : [reason]
        };
    }

    private static VectorMissSetRepresentationAuditReport NewUnavailableMissSetAudit(
        int sampleCount,
        string reason,
        EmbeddingProviderOptions? options = null)
    {
        return new VectorMissSetRepresentationAuditReport
        {
            OperationId = $"vector-missset-representation-audit-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            ProviderId = options?.ProviderId ?? string.Empty,
            EmbeddingModel = options?.EmbeddingModel ?? string.Empty,
            Recommendation = VectorQueryShadowRecommendations.NeedsMoreIndexedData,
            FormalOutputChanged = 0,
            Warnings = string.IsNullOrWhiteSpace(reason) ? Array.Empty<string>() : [reason]
        };
    }

    private static async Task ExecuteEmbeddingProviderSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var isQwen3Provider = IsQwen3ProviderRequest(args);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("embedding-provider-smoke.json")
                : Path.Combine("eval", "embedding-provider-smoke-report.json"));
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("embedding-provider-smoke.md")
                : Path.Combine("eval", "embedding-provider-smoke-report.md"));

        var tester = new EmbeddingProviderSmokeTester();
        var report = await tester.RunAsync(providerOptions, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(EmbeddingProviderSmokeTester.ToMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Embedding provider smoke report written: {outputPath}");
        Console.WriteLine($"[Eval] provider={report.ProviderId}; type={report.ProviderType}; succeeded={report.Succeeded}; diagnostics={report.Diagnostics.Count}");
    }

    private static async Task ExecuteVectorQwen3ShadowEvalAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var qwenArgs = AddOrReplaceOptions(
            args,
            ("--provider", Qwen3ProviderAlias),
            ("--out-a3", CommandHelpers.GetOption(args, "--out-a3") ?? Qwen3OutputPath("vector-qwen3-shadow-eval-a3.json")),
            ("--out-extended", CommandHelpers.GetOption(args, "--out-extended") ?? Qwen3OutputPath("vector-qwen3-shadow-eval-extended.json")),
            ("--md-out", CommandHelpers.GetOption(args, "--md-out") ?? Qwen3OutputPath("vector-qwen3-shadow-eval.md")));

        await ExecuteVectorQueryShadowEvalAsync(service, qwenArgs, cancellationToken).ConfigureAwait(false);

        var a3Path = CommandHelpers.GetOption(qwenArgs, "--out-a3")!;
        var extendedPath = CommandHelpers.GetOption(qwenArgs, "--out-extended")!;
        var a3 = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(a3Path, cancellationToken).ConfigureAwait(false);
        var extended = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(extendedPath, cancellationToken).ConfigureAwait(false);
        if (a3 is not null)
        {
            await WriteTextAsync(
                    VectorQwen3ProviderEvalRunner.BuildShadowMarkdown("A3", a3),
                    Qwen3OutputPath("vector-qwen3-shadow-eval-a3.md"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (extended is not null)
        {
            await WriteTextAsync(
                    VectorQwen3ProviderEvalRunner.BuildShadowMarkdown("Extended", extended),
                    Qwen3OutputPath("vector-qwen3-shadow-eval-extended.md"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteVectorProviderComparisonV310Async(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Qwen3OutputPath("vector-provider-comparison.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Qwen3OutputPath("vector-provider-comparison.md");
        var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
            .ConfigureAwait(false);
        var qwenSmoke = await ReadJsonFileAsync<EmbeddingProviderSmokeReport>(
            Qwen3OutputPath("embedding-provider-smoke.json"),
            cancellationToken).ConfigureAwait(false);
        var currentA3 = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(
            Path.Combine("eval", "vector-query-shadow-eval-a3.json"),
            cancellationToken).ConfigureAwait(false);
        var currentExtended = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(
            Path.Combine("eval", "vector-query-shadow-eval-extended.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenA3 = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(
            Qwen3OutputPath("vector-qwen3-shadow-eval-a3.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenExtended = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(
            Qwen3OutputPath("vector-qwen3-shadow-eval-extended.json"),
            cancellationToken).ConfigureAwait(false);

        var freezeGate = await ReadJsonFileAsync<VectorPostgresProviderFreezeGateReport>(
            Path.Combine("storage", "postgres", "postgres-vector-freeze-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenQueryPreview = await ReadJsonFileAsync<PostgresVectorQueryPreviewReport>(
            Qwen3OutputPath("postgres-vector-query-preview-report.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenShadowSummary = await ReadJsonFileAsync<PostgresVectorShadowEvalSummaryReport>(
            Qwen3OutputPath("postgres-vector-shadow-eval-summary.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenPgVectorParityPassed =
            string.Equals(qwenQueryPreview?.Recommendation, "ReadyForPgVectorShadowEval", StringComparison.OrdinalIgnoreCase)
            && string.Equals(qwenShadowSummary?.Recommendation, "ReadyForVectorPostgresFreeze", StringComparison.OrdinalIgnoreCase);
        var runner = new VectorQwen3ProviderEvalRunner();
        var report = runner.BuildComparison(
            qwenSmoke,
            currentA3,
            currentExtended,
            qwenA3,
            qwenExtended,
            sourceItems.Count,
            currentPgVectorParityPassed: freezeGate?.Passed == true,
            qwenPgVectorParityPassed);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorQwen3ProviderEvalRunner.BuildComparisonMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector provider comparison written: {outputPath}");
    }

    private static async Task ExecuteVectorQwen3ReadinessGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Qwen3OutputPath("vector-qwen3-readiness-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Qwen3OutputPath("vector-qwen3-readiness-gate.md");
        var qwenSmoke = await ReadJsonFileAsync<EmbeddingProviderSmokeReport>(
            Qwen3OutputPath("embedding-provider-smoke.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenA3 = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(
            Qwen3OutputPath("vector-qwen3-shadow-eval-a3.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenExtended = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(
            Qwen3OutputPath("vector-qwen3-shadow-eval-extended.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenQueryPreview = await ReadJsonFileAsync<PostgresVectorQueryPreviewReport>(
            Qwen3OutputPath("postgres-vector-query-preview-report.json"),
            cancellationToken).ConfigureAwait(false);
        var qwenShadowSummary = await ReadJsonFileAsync<PostgresVectorShadowEvalSummaryReport>(
            Qwen3OutputPath("postgres-vector-shadow-eval-summary.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                         && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var projectionMismatchCount = (qwenQueryPreview?.MetadataMismatchCount ?? 0)
                                      + (qwenQueryPreview?.EligibilityMetadataMismatchCount ?? 0)
                                      + (qwenQueryPreview?.RiskProjectionMismatchCount ?? 0)
                                      + (qwenShadowSummary?.Reports.Sum(report => report.MetadataMismatchCount
                                          + report.EligibilityMetadataMismatchCount
                                          + report.RiskProjectionMismatchCount) ?? 0);
        var pgVectorParityPassed =
            string.Equals(qwenQueryPreview?.Recommendation, "ReadyForPgVectorShadowEval", StringComparison.OrdinalIgnoreCase)
            && string.Equals(qwenShadowSummary?.Recommendation, "ReadyForVectorPostgresFreeze", StringComparison.OrdinalIgnoreCase);

        var runner = new VectorQwen3ProviderEvalRunner();
        var report = runner.BuildReadinessGate(
            qwenSmoke,
            qwenA3,
            qwenExtended,
            pgVectorParityPassed,
            p15Passed,
            projectionMismatchCount);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorQwen3ProviderEvalRunner.BuildReadinessGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector Qwen3 readiness gate written: {outputPath}");
        Console.WriteLine($"[Eval] passed={report.Passed}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteVectorProviderConfigurationSanityAuditAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Qwen3OutputPath("vector-provider-configuration-sanity-audit.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Qwen3OutputPath("vector-provider-configuration-sanity-audit.md");
        var report = await BuildVectorProviderConfigurationSanityAuditAsync(cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorProviderConfigurationSanityAuditRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector provider configuration sanity audit written: {outputPath}");
        Console.WriteLine($"[Eval] passed={report.Passed}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteEmbeddingProviderComparisonFreezeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Qwen3OutputPath("vector-provider-comparison-freeze.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Qwen3OutputPath("vector-provider-comparison-freeze.md");
        // 读取已生成的 Qwen3 readiness gate 与 provider comparison 报告；缺失时返回 null，由 freeze gate 给出 blocked 结论。
        var qwen3Gate = await ReadJsonFileAsync<VectorQwen3ReadinessGateReport>(
            Qwen3OutputPath("vector-qwen3-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var comparison = await ReadJsonFileAsync<VectorProviderComparisonV310Report>(
            Qwen3OutputPath("vector-provider-comparison.json"),
            cancellationToken).ConfigureAwait(false);
        var sanityPath = CommandHelpers.GetOption(args, "--sanity-out")
            ?? Qwen3OutputPath("vector-provider-configuration-sanity-audit.json");
        var sanityMarkdownPath = CommandHelpers.GetOption(args, "--sanity-md-out")
            ?? Qwen3OutputPath("vector-provider-configuration-sanity-audit.md");
        var sanity = await BuildVectorProviderConfigurationSanityAuditAsync(cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(sanity, JsonOptions), sanityPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorProviderConfigurationSanityAuditRunner.BuildMarkdown(sanity), sanityMarkdownPath, cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                         && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        var report = new EmbeddingProviderComparisonFreezeRunner().BuildFreezeReport(
            qwen3Gate,
            comparison,
            p15Passed,
            sanity,
            sanityPath);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(EmbeddingProviderComparisonFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Embedding provider comparison freeze written: {outputPath}");
            }

    private static string HybridOutputPath(string fileName)
    {
        return Path.Combine("vector", "hybrid", fileName);
    }

    private static string AlignmentOutputPath(string fileName)
    {
        return Path.Combine("vector", "alignment", fileName);
    }

    private static string EligibilityOutputPath(string fileName)
    {
        return Path.Combine("vector", "eligibility", fileName);
    }

    private static async Task ExecuteVectorRetrievalDatasetAlignmentAuditAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var providerDiagnostics = BuildProviderDiagnostics(providerOptions);

        var runA3 = !string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-extended", StringComparison.OrdinalIgnoreCase);
        var runExtended = !string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-a3", StringComparison.OrdinalIgnoreCase);
        var singleOutputPath = CommandHelpers.GetOption(args, "--out");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? (runA3 && !runExtended ? singleOutputPath : null)
            ?? AlignmentOutputPath("vector-retrieval-dataset-alignment-audit-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? (runExtended && !runA3 ? singleOutputPath : null)
            ?? AlignmentOutputPath("vector-retrieval-dataset-alignment-audit-extended.json");
        var summaryOutputPath = CommandHelpers.GetOption(args, "--out-summary")
            ?? (runA3 && runExtended ? singleOutputPath : null)
            ?? AlignmentOutputPath("vector-retrieval-dataset-alignment-audit-summary.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? (string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-a3", StringComparison.OrdinalIgnoreCase)
                ? AlignmentOutputPath("vector-retrieval-dataset-alignment-audit-a3.md")
                : string.Equals(subcommand, "vector-retrieval-dataset-alignment-audit-extended", StringComparison.OrdinalIgnoreCase)
                    ? AlignmentOutputPath("vector-retrieval-dataset-alignment-audit-extended.md")
                    : AlignmentOutputPath("vector-retrieval-dataset-alignment-audit-summary.md"));

        var a3Samples = runA3
            ? await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContextEvalSample>();
        var extendedSamples = runExtended
            ? await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContextEvalSample>();
        var a3SourceItems = runA3
            ? await LoadVectorEvalCorpusSourceItemsAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorReindexSourceItem>();
        var extendedSourceItems = runExtended
            ? await LoadVectorEvalCorpusSourceItemsAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorReindexSourceItem>();
        var sourceItemsForStore = runExtended ? extendedSourceItems : a3SourceItems;
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItemsForStore, providerOptions);
        var indexedEntries = await infrastructure.Store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 100_000,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);
        var warnings = providerDiagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray();
        var runner = new RetrievalDatasetAlignmentAuditRunner();
        var reports = new List<RetrievalDatasetAlignmentAuditReport>(capacity: 2);

        if (runA3)
        {
            var a3Report = runner.BuildReport(
                "A3",
                a3Samples,
                a3SourceItems,
                indexedEntries,
                providerOptions,
                profileId,
                warnings);
            reports.Add(a3Report);
            await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
                .ConfigureAwait(false);
            if (!runExtended)
            {
                await WriteTextAsync(RetrievalDatasetAlignmentAuditRunner.BuildMarkdownReport(a3Report), markdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (runExtended)
        {
            var extendedReport = runner.BuildReport(
                "Extended",
                extendedSamples,
                extendedSourceItems,
                indexedEntries,
                providerOptions,
                profileId,
                warnings);
            reports.Add(extendedReport);
            await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
                .ConfigureAwait(false);
            if (!runA3)
            {
                await WriteTextAsync(RetrievalDatasetAlignmentAuditRunner.BuildMarkdownReport(extendedReport), markdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var summary = RetrievalDatasetAlignmentAuditRunner.BuildSummary(reports);
        if (runA3 && runExtended)
        {
            await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryOutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetAlignmentAuditRunner.BuildMarkdownSummary(summary), markdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector retrieval dataset alignment audit written: {summaryOutputPath}");
        Console.WriteLine($"[Eval] recommendation={summary.Recommendation}; issues={summary.AlignmentIssueCount}");
    }

    private static async Task ExecuteVectorEligibilityRecallLossTriageAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var runA3 = !string.Equals(subcommand, "vector-eligibility-recall-loss-triage-extended", StringComparison.OrdinalIgnoreCase);
        var runExtended = !string.Equals(subcommand, "vector-eligibility-recall-loss-triage-a3", StringComparison.OrdinalIgnoreCase);
        var singleOutputPath = CommandHelpers.GetOption(args, "--out");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? (runA3 && !runExtended ? singleOutputPath : null)
            ?? EligibilityOutputPath("vector-eligibility-recall-loss-triage-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? (runExtended && !runA3 ? singleOutputPath : null)
            ?? EligibilityOutputPath("vector-eligibility-recall-loss-triage-extended.json");
        var summaryOutputPath = CommandHelpers.GetOption(args, "--out-summary")
            ?? (runA3 && runExtended ? singleOutputPath : null)
            ?? EligibilityOutputPath("vector-eligibility-recall-loss-triage-summary.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? (string.Equals(subcommand, "vector-eligibility-recall-loss-triage-a3", StringComparison.OrdinalIgnoreCase)
                ? EligibilityOutputPath("vector-eligibility-recall-loss-triage-a3.md")
                : string.Equals(subcommand, "vector-eligibility-recall-loss-triage-extended", StringComparison.OrdinalIgnoreCase)
                    ? EligibilityOutputPath("vector-eligibility-recall-loss-triage-extended.md")
                    : EligibilityOutputPath("vector-eligibility-recall-loss-triage-summary.md"));

        var a3Samples = runA3
            ? await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContextEvalSample>();
        var extendedSamples = runExtended
            ? await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContextEvalSample>();
        var a3SourceItems = runA3
            ? await LoadVectorEvalCorpusSourceItemsAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorReindexSourceItem>();
        var extendedSourceItems = runExtended
            ? await LoadVectorEvalCorpusSourceItemsAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorReindexSourceItem>();
        var sourceItemsForStore = runExtended ? extendedSourceItems : a3SourceItems;
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItemsForStore, providerOptions);
        var indexedEntries = await infrastructure.Store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 100_000,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);

        var runner = new VectorEligibilityRecallLossTriageRunner();
        var reports = new List<VectorEligibilityRecallLossTriageReport>(capacity: 2);
        if (runA3)
        {
            var a3Report = runner.BuildReport(
                "A3",
                a3Samples,
                a3SourceItems,
                indexedEntries,
                providerOptions,
                profileId);
            reports.Add(a3Report);
            await WriteTextAsync(JsonSerializer.Serialize(a3Report, JsonOptions), a3OutputPath, cancellationToken)
                .ConfigureAwait(false);
            if (!runExtended)
            {
                await WriteTextAsync(VectorEligibilityRecallLossTriageRunner.BuildMarkdownReport(a3Report), markdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (runExtended)
        {
            var extendedReport = runner.BuildReport(
                "Extended",
                extendedSamples,
                extendedSourceItems,
                indexedEntries,
                providerOptions,
                profileId);
            reports.Add(extendedReport);
            await WriteTextAsync(JsonSerializer.Serialize(extendedReport, JsonOptions), extendedOutputPath, cancellationToken)
                .ConfigureAwait(false);
            if (!runA3)
            {
                await WriteTextAsync(VectorEligibilityRecallLossTriageRunner.BuildMarkdownReport(extendedReport), markdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var summary = VectorEligibilityRecallLossTriageRunner.BuildSummary(reports);
        if (runA3 && runExtended)
        {
            await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryOutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(VectorEligibilityRecallLossTriageRunner.BuildMarkdownSummary(summary), markdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector eligibility recall loss triage written: {summaryOutputPath}");
        Console.WriteLine($"[Eval] recommendation={summary.Recommendation}; filtered={summary.TotalFilteredMustHit}; audit={summary.RouteToAuditCount}; historical={summary.RouteToHistoricalCount}; metadataRepair={summary.MetadataRepairNeededCount}");
    }

    private static async Task ExecuteVectorLifecycleMetadataRepairPlanAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var runA3 = !string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-extended", StringComparison.OrdinalIgnoreCase);
        var runExtended = !string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-a3", StringComparison.OrdinalIgnoreCase);
        var singleOutputPath = CommandHelpers.GetOption(args, "--out");
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? (runA3 && !runExtended ? singleOutputPath : null)
            ?? EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-a3.json");
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? (runExtended && !runA3 ? singleOutputPath : null)
            ?? EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-extended.json");
        var summaryOutputPath = CommandHelpers.GetOption(args, "--out-summary")
            ?? (runA3 && runExtended ? singleOutputPath : null)
            ?? EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-summary.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? (string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-a3", StringComparison.OrdinalIgnoreCase)
                ? EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-a3.md")
                : string.Equals(subcommand, "vector-lifecycle-metadata-repair-plan-extended", StringComparison.OrdinalIgnoreCase)
                    ? EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-extended.md")
                    : EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-summary.md"));

        var a3Samples = runA3
            ? await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContextEvalSample>();
        var extendedSamples = runExtended
            ? await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContextEvalSample>();
        var a3SourceItems = runA3
            ? await LoadVectorEvalCorpusSourceItemsAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorReindexSourceItem>();
        var extendedSourceItems = runExtended
            ? await LoadVectorEvalCorpusSourceItemsAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorReindexSourceItem>();
        var sourceItemsForStore = runExtended ? extendedSourceItems : a3SourceItems;
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItemsForStore, providerOptions);
        var indexedEntries = await infrastructure.Store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 100_000,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);

        var triageRunner = new VectorEligibilityRecallLossTriageRunner();
        var repairRunner = new VectorLifecycleMetadataRepairPlanRunner();
        var reports = new List<VectorLifecycleMetadataRepairPlanReport>(capacity: 2);

        if (runA3)
        {
            var triage = triageRunner.BuildReport(
                "A3",
                a3Samples,
                a3SourceItems,
                indexedEntries,
                providerOptions,
                profileId);
            var report = repairRunner.BuildReport(triage, a3SourceItems, indexedEntries);
            reports.Add(report);
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), a3OutputPath, cancellationToken)
                .ConfigureAwait(false);
            if (!runExtended)
            {
                await WriteTextAsync(VectorLifecycleMetadataRepairPlanRunner.BuildMarkdownReport(report), markdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (runExtended)
        {
            var triage = triageRunner.BuildReport(
                "Extended",
                extendedSamples,
                extendedSourceItems,
                indexedEntries,
                providerOptions,
                profileId);
            var report = repairRunner.BuildReport(triage, extendedSourceItems, indexedEntries);
            reports.Add(report);
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), extendedOutputPath, cancellationToken)
                .ConfigureAwait(false);
            if (!runA3)
            {
                await WriteTextAsync(VectorLifecycleMetadataRepairPlanRunner.BuildMarkdownReport(report), markdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var summary = VectorLifecycleMetadataRepairPlanRunner.BuildSummary(reports);
        if (runA3 && runExtended)
        {
            await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryOutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(VectorLifecycleMetadataRepairPlanRunner.BuildMarkdownSummary(summary), markdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector lifecycle metadata repair plan written: {summaryOutputPath}");
        Console.WriteLine($"[Eval] recommendation={summary.Recommendation}; candidates={summary.CandidateCount}; auto={summary.AutoRepairableCount}; human={summary.HumanReviewRequiredCount}; forbidden={summary.ForbiddenRepairCount}; skipped={summary.CorrectlyBlockedSkippedCount}");
    }

    private static async Task ExecuteVectorLifecycleMetadataReviewCandidatesAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-review-candidates.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-review-candidates.md");
        var repairPlanPath = ResolveSafeVectorEligibilityInputPath(
            CommandHelpers.GetOption(args, "--repair-plan")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-repair-plan-summary.json"));

        var store = new FileVectorLifecycleMetadataReviewCandidateStore(new FileStorageOptions());
        var reviewService = new VectorLifecycleMetadataReviewCandidateService(store);
        var sourceReportPath = NormalizeReportPathForOutput(repairPlanPath);
        var correctlyBlockedSkipped = 0;

        if (File.Exists(repairPlanPath))
        {
            var summary = await ReadJsonFileAsync<VectorLifecycleMetadataRepairPlanSummaryReport>(repairPlanPath, cancellationToken)
                .ConfigureAwait(false);
            correctlyBlockedSkipped = summary?.CorrectlyBlockedSkippedCount ?? 0;
            if (string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates-generate", StringComparison.OrdinalIgnoreCase))
            {
                if (summary is null)
                {
                    throw new InvalidOperationException($"Cannot read vector lifecycle metadata repair plan summary: {repairPlanPath}");
                }

                await reviewService.GenerateAsync(new VectorLifecycleMetadataReviewCandidateGenerationRequest
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    RepairPlanReportPath = sourceReportPath,
                    Limit = CommandHelpers.GetIntOption(args, "--limit", 500)
                }, summary, sourceReportPath, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (string.Equals(subcommand, "vector-lifecycle-metadata-review-candidates-generate", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Vector lifecycle metadata repair plan summary not found.", repairPlanPath);
        }

        var candidates = await reviewService.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Status = CommandHelpers.GetOption(args, "--status"),
            Layer = CommandHelpers.GetOption(args, "--layer"),
            ItemKind = CommandHelpers.GetOption(args, "--item-kind"),
            MustHitItemId = CommandHelpers.GetOption(args, "--must-hit"),
            SourceEvalSet = CommandHelpers.GetOption(args, "--source-eval-set"),
            Limit = CommandHelpers.GetIntOption(args, "--limit", 500),
            Offset = CommandHelpers.GetIntOption(args, "--offset", 0)
        }, cancellationToken).ConfigureAwait(false);
        var report = VectorLifecycleMetadataReviewCandidateService.BuildReport(
            candidates,
            sourceReportPath,
            correctlyBlockedSkipped);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataReviewCandidateService.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector lifecycle metadata review candidates written: {outputPath}");
        Console.WriteLine($"[Eval] candidates={report.CandidateCount}; pending={report.PendingCount}; skipped={report.CorrectlyBlockedSkippedCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorLifecycleMetadataReviewAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewSmokeAsync(args, cancellationToken).ConfigureAwait(false);
            return;
        }

        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var candidateStore = new FileVectorLifecycleMetadataReviewCandidateStore(new FileStorageOptions());
        var reviewStore = new FileVectorLifecycleMetadataReviewStore(new FileStorageOptions());
        var sidecarStore = new FileVectorLifecycleSidecarMetadataStore(new FileStorageOptions());
        var reviewService = new VectorLifecycleMetadataReviewService(candidateStore, reviewStore, sidecarStore);

        if (string.Equals(subcommand, "vector-lifecycle-metadata-sidecar-preview", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = CommandHelpers.GetOption(args, "--out")
                ?? EligibilityOutputPath("vector-lifecycle-metadata-sidecar-preview.json");
            var markdownPath = CommandHelpers.GetOption(args, "--md-out")
                ?? EligibilityOutputPath("vector-lifecycle-metadata-sidecar-preview.md");
            var sidecars = await reviewService.ListSidecarAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            var report = VectorLifecycleMetadataReviewService.BuildSidecarPreview(sidecars);
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(VectorLifecycleMetadataReviewService.BuildMarkdownSidecarPreview(report), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector lifecycle metadata sidecar preview written: {outputPath}");
            Console.WriteLine($"[Eval] sidecarEntries={report.SidecarEntryCount}; formalRetrievalAllowed={report.FormalRetrievalAllowed}; useForRuntime={report.UseForRuntime}");
            return;
        }

        var summaryPath = CommandHelpers.GetOption(args, "--out")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-review-summary.json");
        var summaryMarkdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-review-summary.md");
        var summary = await reviewService.BuildSummaryAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataReviewService.BuildMarkdownSummary(summary), summaryMarkdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector lifecycle metadata review summary written: {summaryPath}");
        Console.WriteLine($"[Eval] candidates={summary.CandidateCount}; pending={summary.PendingCount}; approved={summary.ApprovedForSidecarCount}; sidecar={summary.SidecarEntryCount}; unsafeBlocked={summary.UnsafeApprovalBlockedCount}; recommendation={summary.Recommendation}");
    }

    private static async Task ExecuteVectorSidecarEligibilityPreviewAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var fileName = string.Equals(subcommand, "vector-sidecar-eligibility-recheck", StringComparison.OrdinalIgnoreCase)
            ? "vector-sidecar-eligibility-recheck"
            : string.Equals(subcommand, "vector-sidecar-eligibility-quality", StringComparison.OrdinalIgnoreCase)
                ? "vector-sidecar-eligibility-quality"
                : "vector-sidecar-eligibility-preview";
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? EligibilityOutputPath($"{fileName}.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? EligibilityOutputPath($"{fileName}.md");

        var candidateStore = new FileVectorLifecycleMetadataReviewCandidateStore(new FileStorageOptions());
        var sidecarStore = new FileVectorLifecycleSidecarMetadataStore(new FileStorageOptions());
        var candidates = await candidateStore.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Limit = CommandHelpers.GetIntOption(args, "--limit", 1000)
        }, cancellationToken).ConfigureAwait(false);
        var sidecars = await sidecarStore.QueryAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        var runner = new VectorSidecarEligibilityPreviewRunner();
        var report = runner.BuildReport(candidates, sidecars, subcommand);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorSidecarEligibilityPreviewRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector sidecar eligibility report written: {outputPath}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; candidates={report.CandidateCount}; sidecar={report.SidecarEntryCount}; changed={report.EffectiveMetadataChangedCount}; unsafe={report.UnsafeSidecarBlockedCount}; conflict={report.ConflictSidecarBlockedCount}");
    }

    private static async Task ExecuteVectorLifecycleMetadataEvidenceBackfillAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var mode = string.Equals(subcommand, "vector-lifecycle-metadata-evidence-backfill-audit", StringComparison.OrdinalIgnoreCase)
            ? "audit"
            : "preview";
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? EligibilityOutputPath($"vector-lifecycle-metadata-evidence-backfill-{mode}.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? EligibilityOutputPath($"vector-lifecycle-metadata-evidence-backfill-{mode}.md");
        var batch = await LoadReviewBatchForEvidenceBackfillAsync(CommandHelpers.GetOption(args, "--batch-id"), cancellationToken)
            .ConfigureAwait(false);
        var storageOptions = BuildEvalFileStorageOptions(service);
        var candidateStore = new FileVectorLifecycleMetadataReviewCandidateStore(storageOptions);
        var candidates = await LoadReviewBatchCandidatesAsync(candidateStore, batch, cancellationToken)
            .ConfigureAwait(false);
        var snapshots = await BuildVectorLifecycleMetadataEvidenceSnapshotsAsync(
            storageOptions,
            batch.WorkspaceId,
            batch.CollectionId,
            candidates,
            cancellationToken).ConfigureAwait(false);
        var runner = new VectorLifecycleMetadataEvidenceBackfillRunner();
        var report = runner.BuildReport(
            batch,
            candidates,
            snapshots,
            Path.Combine(GetReviewBatchDirectory(batch.BatchId), "batch.json"),
            mode);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataEvidenceBackfillRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector lifecycle metadata evidence backfill {mode} written: {outputPath}");
        Console.WriteLine($"[Eval] batchId={report.BatchId}; candidates={report.CandidateCount}; evidence={report.EvidenceFoundCount}; sourceRefs={report.SourceRefFoundCount}; provenance={report.ProvenanceFoundCount}; autoRepairable={report.AutoRepairableAfterBackfillCount}; needsEvidence={report.NeedsEvidenceCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteRetrievalDatasetV2MetadataContractAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var runner = new RetrievalDatasetV2MetadataContractRunner();
        if (string.Equals(subcommand, "vector-retrieval-dataset-v2-contract", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = CommandHelpers.GetOption(args, "--out")
                ?? EligibilityOutputPath("vector-retrieval-dataset-v2-contract.json");
            var markdownPath = CommandHelpers.GetOption(args, "--md-out")
                ?? EligibilityOutputPath("vector-retrieval-dataset-v2-contract.md");
            var report = runner.BuildContractReport();
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2MetadataContractRunner.BuildContractMarkdown(report), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 contract written: {outputPath}");
            Console.WriteLine($"[Eval] recommendation={report.Recommendation}; formalRetrieval={report.FormalRetrievalAllowed}; runtime={report.UseForRuntime}");
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-dataset-v2-validator", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = CommandHelpers.GetOption(args, "--out")
                ?? EligibilityOutputPath("vector-retrieval-dataset-v2-validation-report.json");
            var markdownPath = CommandHelpers.GetOption(args, "--md-out")
                ?? EligibilityOutputPath("vector-retrieval-dataset-v2-validation-report.md");
            var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
            var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
            var samples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken)
                .ConfigureAwait(false);
            var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
                .ConfigureAwait(false);
            var relations = await LoadVectorEvalCorpusRelationsAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken)
                .ConfigureAwait(false);
            var report = runner.Validate(sourceItems, samples, relations);
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2MetadataContractRunner.BuildValidationMarkdown(report), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 validation report written: {outputPath}");
            Console.WriteLine($"[Eval] corpus={report.CorpusItemCount}; samples={report.QuerySampleCount}; issues={report.IssueCount}; missingRefs={report.MissingSourceRefsCount}/{report.MissingEvidenceRefsCount}/{report.MissingProvenanceCount}; recommendation={report.Recommendation}");
            return;
        }

        var limitationPath = CommandHelpers.GetOption(args, "--out")
            ?? EligibilityOutputPath("vector-legacy-dataset-limitation-report.json");
        var limitationMarkdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? EligibilityOutputPath("vector-legacy-dataset-limitation-report.md");
        var evidenceReport = await ReadJsonFileAsync<VectorLifecycleMetadataEvidenceBackfillReport>(
                EligibilityOutputPath("vector-lifecycle-metadata-evidence-backfill-audit.json"),
                cancellationToken).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<VectorLifecycleMetadataEvidenceBackfillReport>(
                EligibilityOutputPath("vector-lifecycle-metadata-evidence-backfill-preview.json"),
                cancellationToken).ConfigureAwait(false);
        var candidateReport = await ReadJsonFileAsync<VectorLifecycleMetadataReviewCandidateReport>(
            EligibilityOutputPath("vector-lifecycle-metadata-review-candidates.json"),
            cancellationToken).ConfigureAwait(false);
        var limitation = runner.BuildLegacyLimitationReport(evidenceReport, candidateReport);
        await WriteTextAsync(JsonSerializer.Serialize(limitation, JsonOptions), limitationPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2MetadataContractRunner.BuildLegacyLimitationMarkdown(limitation), limitationMarkdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Legacy retrieval dataset limitation report written: {limitationPath}");
        Console.WriteLine($"[Eval] candidates={limitation.ReviewCandidateCount}; missingEvidenceSourceProvenance={limitation.MissingEvidenceSourceProvenanceCandidateCount}; suitable={limitation.LegacyDatasetSuitableForPrimaryRecallRepair}; recommendation={limitation.Recommendation}");
    }

    private static async Task ExecuteRetrievalDatasetV2GenerationAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var generator = new RetrievalDatasetV2Generator();
        var options = BuildRetrievalDatasetV2GenerationOptions(service, args);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = Path.Combine(outputDirectory, "corpus.jsonl");
        var samplesPath = Path.Combine(outputDirectory, "samples.jsonl");
        var generationReportPath = Path.Combine(outputDirectory, "generation-report.json");
        var generationMarkdownPath = Path.Combine(outputDirectory, "generation-report.md");
        var validationReportPath = Path.Combine(outputDirectory, "validation-report.json");
        var validationMarkdownPath = Path.Combine(outputDirectory, "validation-report.md");
        var qualityReportPath = Path.Combine(outputDirectory, "quality-report.json");
        var qualityMarkdownPath = Path.Combine(outputDirectory, "quality-report.md");
        var manifestPath = Path.Combine(outputDirectory, "dataset-v2-manifest.json");
        var materializationReportPath = Path.Combine(outputDirectory, "materialization-report.json");
        var materializationMarkdownPath = Path.Combine(outputDirectory, "materialization-report.md");
        var materializationGatePath = Path.Combine(outputDirectory, "materialization-gate.json");
        var materializationGateMarkdownPath = Path.Combine(outputDirectory, "materialization-gate.md");

        if (string.Equals(subcommand, "retrieval-dataset-v2-generate", StringComparison.OrdinalIgnoreCase))
        {
            var dataset = generator.Generate(options);
            var validation = generator.Validate(dataset);
            var judgeWarnings = generator.Judge(dataset);
            var report = generator.BuildGenerationReport(options, dataset, validation, judgeWarnings);

            if (!options.DryRun)
            {
                if (!CommandHelpers.HasFlag(args, "--confirm"))
                {
                    throw new InvalidOperationException("retrieval-dataset-v2-generate requires --confirm when DryRun=false.");
                }

                await WriteJsonLinesAsync(dataset.CorpusItems, corpusPath, cancellationToken).ConfigureAwait(false);
                await WriteJsonLinesAsync(dataset.Samples, samplesPath, cancellationToken).ConfigureAwait(false);

                var materializationRunner = new RetrievalDatasetV2MaterializationRunner();
                var corpusHash = RetrievalDatasetV2MaterializationRunner.ComputeFileHash(corpusPath);
                var samplesHash = RetrievalDatasetV2MaterializationRunner.ComputeFileHash(samplesPath);
                var manifest = materializationRunner.BuildManifest(
                    corpusPath,
                    samplesPath,
                    dataset.CorpusItems.Count,
                    dataset.Samples.Count,
                    corpusHash,
                    samplesHash);
                var confirmQuality = generator.BuildQualityReport(dataset, validation, judgeWarnings);
                var materializationReport = materializationRunner.BuildReport(
                    manifest,
                    validation,
                    confirmQuality,
                    manifest,
                    corpusExists: true,
                    samplesExists: true,
                    requireExistingManifest: true);
                await WriteTextAsync(JsonSerializer.Serialize(manifest, JsonOptions), manifestPath, cancellationToken)
                    .ConfigureAwait(false);
                await WriteTextAsync(JsonSerializer.Serialize(materializationReport, JsonOptions), materializationReportPath, cancellationToken)
                    .ConfigureAwait(false);
                await WriteTextAsync(RetrievalDatasetV2MaterializationRunner.BuildMarkdown(materializationReport, "Retrieval Dataset V2 Materialization Report"), materializationMarkdownPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), generationReportPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2Generator.BuildGenerationMarkdown(report), generationMarkdownPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"[Eval] Retrieval Dataset V2 generation report written: {generationReportPath}");
            Console.WriteLine($"[Eval] dryRun={options.DryRun}; corpus={report.CorpusItemCount}; samples={report.SampleCount}; issues={report.ValidationIssueCount}; recommendation={report.Recommendation}");
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-materialization-gate", StringComparison.OrdinalIgnoreCase))
        {
            var materializedDataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
                .ConfigureAwait(false);
            var gateValidationReport = await ReadJsonFileAsync<RetrievalDatasetV2ValidationReport>(validationReportPath, cancellationToken)
                .ConfigureAwait(false);
            var qualityReport = await ReadJsonFileAsync<RetrievalDatasetV2QualityReport>(qualityReportPath, cancellationToken)
                .ConfigureAwait(false);
            if (gateValidationReport is null && materializedDataset.CorpusItems.Count > 0 && materializedDataset.Samples.Count > 0)
            {
                gateValidationReport = generator.Validate(materializedDataset);
            }

            if (qualityReport is null && gateValidationReport is not null && materializedDataset.CorpusItems.Count > 0 && materializedDataset.Samples.Count > 0)
            {
                qualityReport = generator.BuildQualityReport(materializedDataset, gateValidationReport, generator.Judge(materializedDataset));
            }

            var existingManifest = await ReadJsonFileAsync<RetrievalDatasetV2Manifest>(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            var corpusExists = File.Exists(corpusPath);
            var samplesExists = File.Exists(samplesPath);
            var materializationRunner = new RetrievalDatasetV2MaterializationRunner();
            var corpusHash = corpusExists ? RetrievalDatasetV2MaterializationRunner.ComputeFileHash(corpusPath) : string.Empty;
            var samplesHash = samplesExists ? RetrievalDatasetV2MaterializationRunner.ComputeFileHash(samplesPath) : string.Empty;
            var currentManifest = materializationRunner.BuildManifest(
                corpusPath,
                samplesPath,
                materializedDataset.CorpusItems.Count,
                materializedDataset.Samples.Count,
                corpusHash,
                samplesHash);
            if (existingManifest is not null)
            {
                currentManifest = new RetrievalDatasetV2Manifest
                {
                    DatasetId = existingManifest.DatasetId,
                    CorpusPath = currentManifest.CorpusPath,
                    SamplesPath = currentManifest.SamplesPath,
                    CorpusItemCount = currentManifest.CorpusItemCount,
                    SampleCount = currentManifest.SampleCount,
                    CorpusHash = currentManifest.CorpusHash,
                    SamplesHash = currentManifest.SamplesHash,
                    GeneratorVersion = existingManifest.GeneratorVersion,
                    ContractVersion = existingManifest.ContractVersion,
                    CreatedAt = existingManifest.CreatedAt,
                    UseForRuntime = false,
                    FormalRetrievalAllowed = false
                };
            }

            var gate = materializationRunner.BuildReport(
                currentManifest,
                gateValidationReport,
                qualityReport,
                existingManifest,
                corpusExists,
                samplesExists,
                requireExistingManifest: true);
            await WriteTextAsync(JsonSerializer.Serialize(gate, JsonOptions), materializationGatePath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2MaterializationRunner.BuildMarkdown(gate, "Retrieval Dataset V2 Materialization Gate"), materializationGateMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 materialization gate written: {materializationGatePath}");
            Console.WriteLine($"[Eval] datasetId={gate.DatasetId}; gatePassed={gate.GatePassed}; issues={gate.ValidationIssueCount}; recommendation={gate.Recommendation}");
            return;
        }

        var loadedDataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        if (loadedDataset.CorpusItems.Count == 0 || loadedDataset.Samples.Count == 0)
        {
            loadedDataset = generator.Generate(WithDryRun(options, true));
        }

        var validationReport = generator.Validate(loadedDataset);
        await WriteTextAsync(JsonSerializer.Serialize(validationReport, JsonOptions), validationReportPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2MetadataContractRunner.BuildValidationMarkdown(validationReport), validationMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(subcommand, "retrieval-dataset-v2-validate", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Eval] Retrieval Dataset V2 validation written: {validationReportPath}");
            Console.WriteLine($"[Eval] corpus={validationReport.CorpusItemCount}; samples={validationReport.QuerySampleCount}; issues={validationReport.IssueCount}; leakage={validationReport.QueryItemIdLeakCount}; recommendation={validationReport.Recommendation}");
            return;
        }

        var quality = generator.BuildQualityReport(loadedDataset, validationReport, generator.Judge(loadedDataset));
        await WriteTextAsync(JsonSerializer.Serialize(quality, JsonOptions), qualityReportPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2Generator.BuildQualityMarkdown(quality), qualityMarkdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Retrieval Dataset V2 quality written: {qualityReportPath}");
        Console.WriteLine($"[Eval] corpus={quality.CorpusItemCount}; samples={quality.SampleCount}; issues={quality.ValidationIssueCount}; judgeWarnings={quality.JudgeWarningCount}; recommendation={quality.Recommendation}");
    }

    private static RetrievalDatasetV2GenerationOptions BuildRetrievalDatasetV2GenerationOptions(
        ControlRoomService service,
        IReadOnlyList<string> args)
    {
        var dryRun = !CommandHelpers.HasFlag(args, "--confirm") || CommandHelpers.HasFlag(args, "--dry-run");
        return new RetrievalDatasetV2GenerationOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Provider = CommandHelpers.GetOption(args, "--provider") ?? "local-template",
            Model = CommandHelpers.GetOption(args, "--model") ?? "retrieval-dataset-v2-template-v1",
            WorkspaceId = ResolveVectorCommandWorkspaceId(service, args),
            CollectionId = ResolveVectorCommandCollectionId(service, args),
            TargetCorpusItemCount = CommandHelpers.GetIntOption(args, "--target-corpus-items", 28),
            TargetSampleCount = CommandHelpers.GetIntOption(args, "--target-samples", 21),
            DifficultyProfile = CommandHelpers.GetOption(args, "--difficulty-profile") ?? "balanced-v1",
            Seed = CommandHelpers.GetIntOption(args, "--seed", 1701),
            OutputDirectory = CommandHelpers.GetOption(args, "--output-dir")
                ?? Path.Combine("vector", "dataset-v2", "generated"),
            DryRun = dryRun,
            RequireValidation = !CommandHelpers.HasFlag(args, "--skip-validation"),
            UseForRuntime = false
        };
    }

    private static RetrievalDatasetV2GenerationOptions WithDryRun(RetrievalDatasetV2GenerationOptions options, bool dryRun)
    {
        return new RetrievalDatasetV2GenerationOptions
        {
            Enabled = options.Enabled,
            Provider = options.Provider,
            Model = options.Model,
            WorkspaceId = options.WorkspaceId,
            CollectionId = options.CollectionId,
            TargetCorpusItemCount = options.TargetCorpusItemCount,
            TargetSampleCount = options.TargetSampleCount,
            DifficultyProfile = options.DifficultyProfile,
            Seed = options.Seed,
            OutputDirectory = options.OutputDirectory,
            DryRun = dryRun,
            RequireValidation = options.RequireValidation,
            UseForRuntime = false
        };
    }

    private static async Task<RetrievalDatasetV2GeneratedDataset> LoadRetrievalDatasetV2GeneratedDatasetAsync(
        string corpusPath,
        string samplesPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(corpusPath) || !File.Exists(samplesPath))
        {
            return new RetrievalDatasetV2GeneratedDataset();
        }

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = await ReadJsonLinesAsync<RetrievalDatasetV2CorpusItem>(corpusPath, cancellationToken)
                .ConfigureAwait(false),
            Samples = await ReadJsonLinesAsync<RetrievalDatasetV2Sample>(samplesPath, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static async Task ExecuteRetrievalDatasetV2ShadowEvalAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "dataset-v2", "generated"));
        var evalDirectory = Path.GetFullPath(Path.Combine("vector", "dataset-v2", "eval"));
        Directory.CreateDirectory(evalDirectory);

        var corpusPath = Path.Combine(outputDirectory, "corpus.jsonl");
        var samplesPath = Path.Combine(outputDirectory, "samples.jsonl");
        var manifestPath = Path.Combine(outputDirectory, "dataset-v2-manifest.json");
        var validationReportPath = Path.Combine(outputDirectory, "validation-report.json");
        var qualityReportPath = Path.Combine(outputDirectory, "quality-report.json");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await ReadJsonFileAsync<RetrievalDatasetV2Manifest>(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var materializationGate = await BuildCurrentRetrievalDatasetV2MaterializationGateAsync(
            dataset,
            manifest,
            corpusPath,
            samplesPath,
            validationReportPath,
            qualityReportPath,
            cancellationToken).ConfigureAwait(false);

        var runner = new RetrievalDatasetV2ShadowEvalRunner();
        var denseReports = runner.RunDense(dataset, manifest, materializationGate);
        var hybridReports = runner.RunHybrid(dataset, manifest, materializationGate);
        var allReports = denseReports.Concat(hybridReports).ToArray();
        var summary = runner.BuildSummary(allReports);
        var recallThreshold = GetDoubleOption(args, "--recall-threshold")
            ?? RetrievalDatasetV2ShadowEvalRunner.DefaultRecallThreshold;
        var readiness = runner.BuildReadinessGate(materializationGate, summary, recallThreshold);

        var densePath = Path.Combine(evalDirectory, "dataset-v2-dense-shadow-eval.json");
        var denseMarkdownPath = Path.Combine(evalDirectory, "dataset-v2-dense-shadow-eval.md");
        var hybridPath = Path.Combine(evalDirectory, "dataset-v2-hybrid-shadow-eval.json");
        var hybridMarkdownPath = Path.Combine(evalDirectory, "dataset-v2-hybrid-shadow-eval.md");
        var summaryPath = Path.Combine(evalDirectory, "dataset-v2-shadow-eval-summary.json");
        var summaryMarkdownPath = Path.Combine(evalDirectory, "dataset-v2-shadow-eval-summary.md");
        var readinessPath = Path.Combine(evalDirectory, "dataset-v2-readiness-gate.json");
        var readinessMarkdownPath = Path.Combine(evalDirectory, "dataset-v2-readiness-gate.md");

        if (string.Equals(subcommand, "retrieval-dataset-v2-dense-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(JsonSerializer.Serialize(denseReports, JsonOptions), densePath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2ShadowEvalRunner.BuildProfilesMarkdown("Retrieval Dataset V2 Dense Shadow Eval", denseReports), denseMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-hybrid-shadow-eval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(JsonSerializer.Serialize(hybridReports, JsonOptions), hybridPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2ShadowEvalRunner.BuildProfilesMarkdown("Retrieval Dataset V2 Hybrid Shadow Eval", hybridReports), hybridMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-shadow-eval", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2ShadowEvalRunner.BuildSummaryMarkdown(summary), summaryMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 shadow eval summary written: {summaryPath}");
            Console.WriteLine($"[Eval] datasetId={summary.DatasetId}; best={summary.BestProfileName}; recall={summary.BestRecallAfterPolicy:P2}; risk={summary.BestRiskAfterPolicy}; pgParity={summary.PgVectorParityPassed}; recommendation={summary.Recommendation}");
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2ShadowEvalRunner.BuildSummaryMarkdown(summary), summaryMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(JsonSerializer.Serialize(readiness, JsonOptions), readinessPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2ShadowEvalRunner.BuildGateMarkdown(readiness), readinessMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 readiness gate written: {readinessPath}");
            Console.WriteLine($"[Eval] datasetId={readiness.DatasetId}; gatePassed={readiness.GatePassed}; recall={readiness.BestRecallAfterPolicy:P2}; recommendation={readiness.Recommendation}");
            return;
        }

        var selected = string.Equals(subcommand, "retrieval-dataset-v2-dense-shadow-eval", StringComparison.OrdinalIgnoreCase)
            ? denseReports
            : hybridReports;
        Console.WriteLine($"[Eval] Retrieval Dataset V2 {subcommand} written.");
        Console.WriteLine($"[Eval] profiles={selected.Count}; bestRecall={selected.Max(static report => report.RecallAfterPolicy):P2}; recommendation={selected.OrderByDescending(static report => report.RecallAfterPolicy).FirstOrDefault()?.Recommendation}");
    }

    private static async Task ExecuteRetrievalDatasetV2StressAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var runner = new RetrievalDatasetV2StressRunner();
        var options = BuildRetrievalDatasetV2StressOptions(service, args);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = Path.Combine(outputDirectory, "corpus.jsonl");
        var samplesPath = Path.Combine(outputDirectory, "samples.jsonl");
        var generationPath = Path.Combine(outputDirectory, "stress-generation-report.json");
        var generationMarkdownPath = Path.Combine(outputDirectory, "stress-generation-report.md");
        var validationPath = Path.Combine(outputDirectory, "validation-report.json");
        var validationMarkdownPath = Path.Combine(outputDirectory, "validation-report.md");
        var leakagePath = Path.Combine(outputDirectory, "leakage-audit.json");
        var leakageMarkdownPath = Path.Combine(outputDirectory, "leakage-audit.md");
        var anchorPath = Path.Combine(outputDirectory, "anchor-dominance-audit.json");
        var anchorMarkdownPath = Path.Combine(outputDirectory, "anchor-dominance-audit.md");
        var shadowPath = Path.Combine(outputDirectory, "stress-shadow-eval.json");
        var shadowMarkdownPath = Path.Combine(outputDirectory, "stress-shadow-eval.md");
        var readinessPath = Path.Combine(outputDirectory, "stress-readiness-gate.json");
        var readinessMarkdownPath = Path.Combine(outputDirectory, "stress-readiness-gate.md");

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        if (dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0 || string.Equals(subcommand, "retrieval-dataset-v2-stress-generate", StringComparison.OrdinalIgnoreCase))
        {
            dataset = runner.Generate(options);
        }

        var validation = runner.Validate(dataset);
        await WriteTextAsync(JsonSerializer.Serialize(validation, JsonOptions), validationPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2MetadataContractRunner.BuildValidationMarkdown(validation), validationMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-generate", StringComparison.OrdinalIgnoreCase))
        {
            var generation = runner.BuildGenerationReport(options, dataset, validation);
            if (!options.DryRun)
            {
                if (!CommandHelpers.HasFlag(args, "--confirm"))
                {
                    throw new InvalidOperationException("retrieval-dataset-v2-stress-generate requires --confirm when DryRun=false.");
                }

                await WriteJsonLinesAsync(dataset.CorpusItems, corpusPath, cancellationToken).ConfigureAwait(false);
                await WriteJsonLinesAsync(dataset.Samples, samplesPath, cancellationToken).ConfigureAwait(false);
            }

            await WriteTextAsync(JsonSerializer.Serialize(generation, JsonOptions), generationPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2StressRunner.BuildMarkdown("Retrieval Dataset V2 Stress Generation Report", generation), generationMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 stress generation written: {generationPath}");
            Console.WriteLine($"[Eval] dryRun={options.DryRun}; corpus={generation.CorpusItemCount}; samples={generation.SampleCount}; leakage={generation.LeakageIssueCount}; recommendation={generation.Recommendation}");
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-leakage-audit", StringComparison.OrdinalIgnoreCase))
        {
            var leakage = runner.BuildLeakageAudit(options, dataset, validation);
            await WriteTextAsync(JsonSerializer.Serialize(leakage, JsonOptions), leakagePath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2StressRunner.BuildMarkdown("Retrieval Dataset V2 Leakage Audit", leakage), leakageMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 leakage audit written: {leakagePath}");
            Console.WriteLine($"[Eval] leakage={leakage.LeakageIssueCount}; itemId={leakage.ItemIdLeakageCount}; rationale={leakage.RationaleLeakageCount}; recommendation={leakage.Recommendation}");
            return;
        }

        if (string.Equals(subcommand, "retrieval-dataset-v2-anchor-dominance-audit", StringComparison.OrdinalIgnoreCase))
        {
            var anchor = runner.BuildAnchorDominanceAudit(options, dataset, validation);
            await WriteTextAsync(JsonSerializer.Serialize(anchor, JsonOptions), anchorPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2StressRunner.BuildMarkdown("Retrieval Dataset V2 Anchor Dominance Audit", anchor), anchorMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 anchor dominance audit written: {anchorPath}");
            Console.WriteLine($"[Eval] anchorRecall={anchor.AnchorRecall:P2}; dominance={anchor.AnchorDominanceScore:F4}; recommendation={anchor.Recommendation}");
            return;
        }

        var materialized = File.Exists(corpusPath) && File.Exists(samplesPath);
        var shadow = runner.BuildShadowEval(options, dataset, validation, materialized);
        await WriteTextAsync(JsonSerializer.Serialize(shadow, JsonOptions), shadowPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2StressRunner.BuildMarkdown("Retrieval Dataset V2 Stress Shadow Eval", shadow), shadowMarkdownPath, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(subcommand, "retrieval-dataset-v2-stress-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            var recallThreshold = GetDoubleOption(args, "--recall-threshold")
                ?? RetrievalDatasetV2StressRunner.DefaultRecallThreshold;
            var anchorThreshold = GetDoubleOption(args, "--anchor-dominance-threshold")
                ?? RetrievalDatasetV2StressRunner.DefaultAnchorDominanceThreshold;
            var minimumHoldoutSamples = CommandHelpers.GetIntOption(args, "--min-holdout-samples", 10);
            var readiness = runner.BuildReadinessGate(options, shadow, recallThreshold, anchorThreshold, minimumHoldoutSamples);
            await WriteTextAsync(JsonSerializer.Serialize(readiness, JsonOptions), readinessPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetV2StressRunner.BuildMarkdown("Retrieval Dataset V2 Stress Readiness Gate", readiness), readinessMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval Dataset V2 stress readiness gate written: {readinessPath}");
            Console.WriteLine($"[Eval] datasetId={readiness.DatasetId}; holdoutRecall={readiness.HoldoutHybridRecall:P2}; leakage={readiness.LeakageIssueCount}; recommendation={readiness.Recommendation}");
            return;
        }

        Console.WriteLine($"[Eval] Retrieval Dataset V2 stress shadow eval written: {shadowPath}");
        Console.WriteLine($"[Eval] datasetId={shadow.DatasetId}; hybridRecall={shadow.HybridRecall:P2}; holdoutRecall={shadow.HoldoutHybridRecall:P2}; risk={shadow.RiskAfterPolicy}; recommendation={shadow.Recommendation}");
    }

    private static RetrievalDatasetV2StressOptions BuildRetrievalDatasetV2StressOptions(
        ControlRoomService service,
        IReadOnlyList<string> args)
    {
        var dryRun = !CommandHelpers.HasFlag(args, "--confirm") || CommandHelpers.HasFlag(args, "--dry-run");
        return new RetrievalDatasetV2StressOptions
        {
            TargetCorpusItemCount = CommandHelpers.GetIntOption(args, "--target-corpus-items", 120),
            TargetSampleCount = CommandHelpers.GetIntOption(args, "--target-samples", 120),
            HoldoutRatio = GetDoubleOption(args, "--holdout-ratio") ?? 0.2,
            DistractorRatio = GetDoubleOption(args, "--distractor-ratio") ?? 0.35,
            AnchorAblationEnabled = !CommandHelpers.HasFlag(args, "--no-anchor-ablation"),
            LeakageAuditEnabled = !CommandHelpers.HasFlag(args, "--no-leakage-audit"),
            WorkspaceId = ResolveVectorCommandWorkspaceId(service, args),
            CollectionId = ResolveVectorCommandCollectionId(service, args),
            Seed = CommandHelpers.GetIntOption(args, "--seed", 2701),
            OutputDirectory = CommandHelpers.GetOption(args, "--output-dir")
                ?? Path.Combine("vector", "dataset-v2", "stress"),
            DryRun = dryRun,
            UseForRuntime = false
        };
    }

    private static async Task ExecuteRetrievalDatasetV2StressFailureTriageAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "dataset-v2", "stress"));
        Directory.CreateDirectory(outputDirectory);
        var corpusPath = Path.Combine(outputDirectory, "corpus.jsonl");
        var samplesPath = Path.Combine(outputDirectory, "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        var runner = new RetrievalDatasetV2StressRecallFailureTriageRunner();
        var holdoutOnly = string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-triage-holdout", StringComparison.OrdinalIgnoreCase);
        var report = string.Equals(subcommand, "retrieval-dataset-v2-stress-failure-clusters", StringComparison.OrdinalIgnoreCase)
            ? runner.BuildClusters(dataset)
            : runner.BuildReport(dataset, holdoutOnly);
        var fileName = subcommand switch
        {
            var value when string.Equals(value, "retrieval-dataset-v2-stress-failure-triage-holdout", StringComparison.OrdinalIgnoreCase)
                => "stress-failure-triage-holdout",
            var value when string.Equals(value, "retrieval-dataset-v2-stress-failure-clusters", StringComparison.OrdinalIgnoreCase)
                => "stress-failure-clusters",
            _ => "stress-failure-triage"
        };
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2StressRecallFailureTriageRunner.BuildMarkdown($"Retrieval Dataset V2 {fileName}", report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Retrieval Dataset V2 stress failure triage written: {jsonPath}");
        Console.WriteLine($"[Eval] datasetId={report.DatasetId}; failures={report.FailureCount}; holdoutFailures={report.HoldoutFailureCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteRetrievalDatasetV2HybridScoringRepairAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "dataset-v2", "stress"));
        Directory.CreateDirectory(outputDirectory);
        var corpusPath = Path.Combine(outputDirectory, "corpus.jsonl");
        var samplesPath = Path.Combine(outputDirectory, "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        if (dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            throw new InvalidOperationException("Dataset V2 stress corpus/samples are missing. Run eval retrieval-dataset-v2-stress-generate --confirm first.");
        }

        var options = new HybridUnionScoringRepairOptions
        {
            Enabled = true,
            DensePreservationEnabled = !CommandHelpers.HasFlag(args, "--disable-dense-preservation"),
            DenseWinnerFloorEnabled = !CommandHelpers.HasFlag(args, "--disable-dense-winner-floor"),
            NegativeDistractorPenaltyEnabled = !CommandHelpers.HasFlag(args, "--disable-negative-distractor-penalty"),
            AnchorScoreCapEnabled = !CommandHelpers.HasFlag(args, "--disable-anchor-score-cap"),
            ContributionAwareRerankEnabled = !CommandHelpers.HasFlag(args, "--disable-contribution-aware-rerank"),
            MaxRiskAllowed = CommandHelpers.GetIntOption(args, "--max-risk", 0),
            UseForRuntime = false
        };
        var runner = new HybridUnionScoringRepairRunner();
        var gateMode = string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-repair-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(dataset, options)
            : runner.BuildPreview(dataset, options);
        var fileName = subcommand switch
        {
            var value when string.Equals(value, "retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval", StringComparison.OrdinalIgnoreCase)
                => "hybrid-scoring-repair-shadow-eval",
            var value when string.Equals(value, "retrieval-dataset-v2-hybrid-scoring-repair-gate", StringComparison.OrdinalIgnoreCase)
                => "hybrid-scoring-repair-gate",
            _ => "hybrid-scoring-repair-preview"
        };
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(HybridUnionScoringRepairRunner.BuildMarkdown($"Retrieval Dataset V2 {fileName}", report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        var best = report.Profiles.FirstOrDefault(profile => string.Equals(profile.ProfileName, report.BestProfileName, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[Eval] Retrieval Dataset V2 hybrid scoring repair written: {jsonPath}");
        Console.WriteLine($"[Eval] datasetId={report.DatasetId}; best={report.BestProfileName}; recall={best?.RecallAfterPolicy:P2}; holdout={best?.HoldoutRecallAfterPolicy:P2}; denseLost={best?.DenseWinnerLostCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteRetrievalDatasetV2HybridScoringRiskTriageAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "dataset-v2", "stress"));
        Directory.CreateDirectory(outputDirectory);
        var corpusPath = Path.Combine(outputDirectory, "corpus.jsonl");
        var samplesPath = Path.Combine(outputDirectory, "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        if (dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            throw new InvalidOperationException("Dataset V2 stress corpus/samples are missing. Run eval retrieval-dataset-v2-stress-generate --confirm first.");
        }

        var profileName = CommandHelpers.GetOption(args, "--profile")
            ?? HybridScoringRiskRegressionTriageRunner.DefaultProfileName;
        var holdoutOnly = string.Equals(subcommand, "retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout", StringComparison.OrdinalIgnoreCase);
        int? expectedRiskCount = holdoutOnly
            ? null
            : await TryReadHybridScoringRepairRiskCountAsync(outputDirectory, profileName, cancellationToken).ConfigureAwait(false);
        var runner = new HybridScoringRiskRegressionTriageRunner();
        var report = runner.BuildReport(dataset, holdoutOnly, profileName, expectedRiskCount);
        var fileName = holdoutOnly
            ? "hybrid-scoring-risk-triage-holdout"
            : "hybrid-scoring-risk-triage";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(HybridScoringRiskRegressionTriageRunner.BuildMarkdown($"Retrieval Dataset V2 {fileName}", report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Retrieval Dataset V2 hybrid scoring risk triage written: {jsonPath}");
        Console.WriteLine($"[Eval] datasetId={report.DatasetId}; profile={report.ProfileName}; risk={report.RiskCandidateCount}; mustNot={report.MustNotCandidatePromotedCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteRetrievalDatasetV2StressFreezeGateAsync(CancellationToken cancellationToken)
    {
        var stressDirectory = Path.GetFullPath(Path.Combine("vector", "dataset-v2", "stress"));
        Directory.CreateDirectory(stressDirectory);

        var materializationPath = Path.Combine("vector", "dataset-v2", "generated", "materialization-gate.json");
        var smallSetReadinessPath = Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-readiness-gate.json");
        var stressReadinessPath = Path.Combine("vector", "dataset-v2", "stress", "stress-readiness-gate.json");
        var leakagePath = Path.Combine("vector", "dataset-v2", "stress", "leakage-audit.json");
        var anchorPath = Path.Combine("vector", "dataset-v2", "stress", "anchor-dominance-audit.json");
        var triagePath = Path.Combine("vector", "dataset-v2", "stress", "stress-failure-triage.json");
        var repairGatePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-gate.json");
        var riskTriagePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage.json");

        var materialization = await ReadJsonFileAsync<RetrievalDatasetV2MaterializationReport>(materializationPath, cancellationToken)
            .ConfigureAwait(false);
        var smallSetReadiness = await ReadJsonFileAsync<RetrievalDatasetV2ReadinessGateReport>(smallSetReadinessPath, cancellationToken)
            .ConfigureAwait(false);
        var stressReadiness = await ReadJsonFileAsync<RetrievalDatasetV2StressReport>(stressReadinessPath, cancellationToken)
            .ConfigureAwait(false);
        var leakage = await ReadJsonFileAsync<RetrievalDatasetV2StressReport>(leakagePath, cancellationToken)
            .ConfigureAwait(false);
        var anchor = await ReadJsonFileAsync<RetrievalDatasetV2StressReport>(anchorPath, cancellationToken)
            .ConfigureAwait(false);
        var triage = await ReadJsonFileAsync<RetrievalDatasetV2StressRecallFailureTriageReport>(triagePath, cancellationToken)
            .ConfigureAwait(false);
        var repairGate = await ReadJsonFileAsync<HybridUnionScoringRepairReport>(repairGatePath, cancellationToken)
            .ConfigureAwait(false);
        var riskTriage = await ReadJsonFileAsync<HybridScoringRiskRegressionTriageReport>(riskTriagePath, cancellationToken)
            .ConfigureAwait(false);

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["materializationGate"] = materializationPath,
            ["smallSetReadinessGate"] = smallSetReadinessPath,
            ["stressReadinessGate"] = stressReadinessPath,
            ["leakageAudit"] = leakagePath,
            ["anchorDominanceAudit"] = anchorPath,
            ["stressFailureTriage"] = triagePath,
            ["hybridScoringRepairGate"] = repairGatePath,
            ["hybridScoringRiskTriage"] = riskTriagePath
        };
        var runner = new RetrievalDatasetV2StressFreezeRunner();
        var report = runner.BuildReport(
            materialization,
            smallSetReadiness,
            stressReadiness,
            leakage,
            anchor,
            triage,
            repairGate,
            riskTriage,
            sourceReports);

        var jsonPath = Path.Combine(stressDirectory, "stress-freeze-gate.json");
        var markdownPath = Path.Combine(stressDirectory, "stress-freeze-gate.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(RetrievalDatasetV2StressFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Retrieval Dataset V2 stress freeze gate written: {jsonPath}");
        Console.WriteLine($"[Eval] datasetId={report.DatasetId}; freezePassed={report.FreezePassed}; best={report.BestPreviewProfile}; stress={report.StressRecall:P2}; holdout={report.HoldoutRecall:P2}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorV4ReadinessRecheckAsync(CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var legacyReadinessPath = Path.Combine("eval", "vector-retrieval-shadow-readiness-gate.json");
        var legacyLimitationPath = Path.Combine("vector", "eligibility", "vector-legacy-dataset-limitation-report.json");
        var pgVectorFreezePath = Path.Combine("storage", "postgres", "postgres-vector-freeze-gate.json");
        var qwen3FreezePath = Path.Combine("vector", "providers", "qwen3", "vector-provider-comparison-freeze.json");
        var hybridFreezePath = Path.Combine("vector", "hybrid", "vector-hybrid-freeze-gate.json");
        var materializationPath = Path.Combine("vector", "dataset-v2", "generated", "materialization-gate.json");
        var smallSetReadinessPath = Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-readiness-gate.json");
        var stressFreezePath = Path.Combine("vector", "dataset-v2", "stress", "stress-freeze-gate.json");
        var repairGatePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-gate.json");
        var riskTriagePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");

        var legacyReadiness = await ReadJsonFileAsync<VectorRetrievalShadowReadinessGateReport>(legacyReadinessPath, cancellationToken)
            .ConfigureAwait(false);
        var legacyLimitation = await ReadJsonFileAsync<RetrievalDatasetLegacyLimitationReport>(legacyLimitationPath, cancellationToken)
            .ConfigureAwait(false);
        var pgVectorFreeze = await ReadJsonFileAsync<VectorPostgresProviderFreezeGateReport>(pgVectorFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var qwen3Freeze = await ReadJsonFileAsync<EmbeddingProviderComparisonFreezeReport>(qwen3FreezePath, cancellationToken)
            .ConfigureAwait(false);
        var hybridFreeze = await ReadJsonFileAsync<HybridRetrievalPreviewFreezeReport>(hybridFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var materialization = await ReadJsonFileAsync<RetrievalDatasetV2MaterializationReport>(materializationPath, cancellationToken)
            .ConfigureAwait(false);
        var smallSetReadiness = await ReadJsonFileAsync<RetrievalDatasetV2ReadinessGateReport>(smallSetReadinessPath, cancellationToken)
            .ConfigureAwait(false);
        var stressFreeze = await ReadJsonFileAsync<RetrievalDatasetV2StressFreezeReport>(stressFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var repairGate = await ReadJsonFileAsync<HybridUnionScoringRepairReport>(repairGatePath, cancellationToken)
            .ConfigureAwait(false);
        var riskTriage = await ReadJsonFileAsync<HybridScoringRiskRegressionTriageReport>(riskTriagePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["legacyVectorReadinessGate"] = legacyReadinessPath,
            ["legacyDatasetLimitationReport"] = legacyLimitationPath,
            ["pgVectorProviderFreezeGate"] = pgVectorFreezePath,
            ["qwen3ProviderComparisonFreeze"] = qwen3FreezePath,
            ["hybridRetrievalFreeze"] = hybridFreezePath,
            ["datasetV2MaterializationGate"] = materializationPath,
            ["datasetV2SmallReadinessGate"] = smallSetReadinessPath,
            ["datasetV2StressFreezeGate"] = stressFreezePath,
            ["hybridScoringRepairGate"] = repairGatePath,
            ["hybridScoringRiskTriage"] = riskTriagePath,
            ["runtimeChangeGate"] = runtimeGatePath
        };

        var report = new VectorV4ReadinessRecheckRunner().BuildReport(
            legacyReadiness,
            legacyLimitation,
            pgVectorFreeze,
            qwen3Freeze,
            hybridFreeze,
            materialization,
            smallSetReadiness,
            stressFreeze,
            repairGate,
            riskTriage,
            runtimeGate,
            sourceReports);

        var jsonPath = Path.Combine(outputDirectory, "vector-v4-readiness-recheck.json");
        var markdownPath = Path.Combine(outputDirectory, "vector-v4-readiness-recheck.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorV4ReadinessRecheckRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await new LearningReadinessFreezeRunner()
            .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector V4 readiness recheck written: {jsonPath}");
        Console.WriteLine($"[Eval] recheckPassed={report.RecheckPassed}; recommendation={report.Recommendation}; guardedPreview={report.ReadyForGuardedFormalPreview}; runtimeSwitch={report.ReadyForRuntimeSwitch}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteVectorGuardedFormalRetrievalPreviewAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var v4RecheckPath = Path.Combine("vector", "v4", "vector-v4-readiness-recheck.json");
        var stressFreezePath = Path.Combine("vector", "dataset-v2", "stress", "stress-freeze-gate.json");
        var repairGatePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-gate.json");
        var riskTriagePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        var v4Recheck = await ReadJsonFileAsync<VectorV4ReadinessRecheckReport>(v4RecheckPath, cancellationToken)
            .ConfigureAwait(false);
        var stressFreeze = await ReadJsonFileAsync<RetrievalDatasetV2StressFreezeReport>(stressFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var repairGate = await ReadJsonFileAsync<HybridUnionScoringRepairReport>(repairGatePath, cancellationToken)
            .ConfigureAwait(false);
        var riskTriage = await ReadJsonFileAsync<HybridScoringRiskRegressionTriageReport>(riskTriagePath, cancellationToken)
            .ConfigureAwait(false);

        var options = new GuardedFormalRetrievalPreviewOptions
        {
            Enabled = true,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireV4RecheckPassed = !CommandHelpers.HasFlag(args, "--skip-v4-recheck"),
            CompareWithCurrentFormal = !CommandHelpers.HasFlag(args, "--no-current-formal-compare"),
            FailClosedOnRisk = !CommandHelpers.HasFlag(args, "--allow-risk"),
            MaxRiskAllowed = CommandHelpers.GetIntOption(args, "--max-risk", 0),
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v4ReadinessRecheck"] = v4RecheckPath,
            ["datasetV2StressFreezeGate"] = stressFreezePath,
            ["hybridScoringRepairGate"] = repairGatePath,
            ["hybridScoringRiskTriage"] = riskTriagePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };
        var runner = new GuardedFormalRetrievalPreviewRunner();
        var gateMode = string.Equals(subcommand, "vector-guarded-formal-retrieval-preview-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(dataset, v4Recheck, stressFreeze, repairGate, riskTriage, options, sourceReports)
            : runner.BuildPreview(dataset, v4Recheck, stressFreeze, repairGate, riskTriage, options, sourceReports);
        var fileName = gateMode
            ? "vector-guarded-formal-retrieval-preview-gate"
            : "vector-guarded-formal-retrieval-preview";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                GuardedFormalRetrievalPreviewRunner.BuildMarkdown(
                    gateMode ? "Vector Guarded Formal Retrieval Preview Gate" : "Vector Guarded Formal Retrieval Preview",
                    report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (gateMode)
        {
            await new LearningReadinessFreezeRunner()
                .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector guarded formal retrieval preview written: {jsonPath}");
        Console.WriteLine($"[Eval] profile={report.ProfileName}; gatePassed={report.GatePassed}; wouldAdd={report.WouldAddCount}; wouldRemove={report.WouldRemoveCount}; risk={report.RiskAfterPolicy}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorShadowPackageComparisonAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var guardedGatePath = Path.Combine("vector", "v4", "vector-guarded-formal-retrieval-preview-gate.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);
        var guardedGate = await ReadJsonFileAsync<GuardedFormalRetrievalPreviewReport>(guardedGatePath, cancellationToken)
            .ConfigureAwait(false);
        var options = new VectorShadowPackageComparisonOptions
        {
            Enabled = true,
            RequireGuardedFormalPreviewPassed = !CommandHelpers.HasFlag(args, "--skip-guarded-preview-gate"),
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            BuildShadowPackage = !CommandHelpers.HasFlag(args, "--no-shadow-package"),
            CompareWithBaseline = !CommandHelpers.HasFlag(args, "--no-baseline-compare"),
            FailClosedOnRisk = !CommandHelpers.HasFlag(args, "--allow-risk"),
            MaxRiskAllowed = CommandHelpers.GetIntOption(args, "--max-risk", 0),
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["guardedFormalRetrievalPreviewGate"] = guardedGatePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new VectorShadowPackageComparisonRunner();
        var gateMode = string.Equals(subcommand, "vector-shadow-package-comparison-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(dataset, guardedGate, options, sourceReports)
            : runner.BuildComparison(dataset, guardedGate, options, sourceReports);
        var fileName = gateMode
            ? "vector-shadow-package-comparison-gate"
            : "vector-shadow-package-comparison";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                VectorShadowPackageComparisonRunner.BuildMarkdown(
                    gateMode ? "Vector Shadow Package Comparison Gate" : "Vector Shadow Package Comparison",
                    report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (gateMode)
        {
            await new LearningReadinessFreezeRunner()
                .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector shadow package comparison written: {jsonPath}");
        Console.WriteLine($"[Eval] profile={report.ProfileName}; gatePassed={report.GatePassed}; add/remove={report.CandidateAddCount}/{report.CandidateRemoveCount}; tokenDeltaMax={report.TokenDeltaMax}; risk={report.RiskAfterPolicy}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedFormalPreviewOptInAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var v4RecheckPath = Path.Combine("vector", "v4", "vector-v4-readiness-recheck.json");
        var guardedGatePath = Path.Combine("vector", "v4", "vector-guarded-formal-retrieval-preview-gate.json");
        var shadowGatePath = Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json");
        var v4Recheck = await ReadJsonFileAsync<VectorV4ReadinessRecheckReport>(v4RecheckPath, cancellationToken)
            .ConfigureAwait(false);
        var guardedGate = await ReadJsonFileAsync<GuardedFormalRetrievalPreviewReport>(guardedGatePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowGate = await ReadJsonFileAsync<VectorShadowPackageComparisonReport>(shadowGatePath, cancellationToken)
            .ConfigureAwait(false);

        var selectedWorkspace = CommandHelpers.GetOption(args, "--workspace")
            ?? CommandHelpers.GetOption(args, "--workspace-id")
            ?? "contextcore_eval";
        var selectedCollection = CommandHelpers.GetOption(args, "--collection")
            ?? CommandHelpers.GetOption(args, "--collection-id")
            ?? "dataset-v2-stress";
        var selectedEvalScope = CommandHelpers.GetOption(args, "--eval-scope")
            ?? "dataset-v2-stress";
        var nonAllowlistedWorkspace = CommandHelpers.GetOption(args, "--non-allowlisted-workspace")
            ?? $"{selectedWorkspace}-outside";
        var nonAllowlistedCollection = CommandHelpers.GetOption(args, "--non-allowlisted-collection")
            ?? $"{selectedCollection}-outside";
        var nonAllowlistedEvalScope = CommandHelpers.GetOption(args, "--non-allowlisted-eval-scope")
            ?? $"{selectedEvalScope}-outside";
        var workspaceAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--workspace-allowlist"));
        var collectionAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--collection-allowlist"));
        var evalScopeAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--eval-scope-allowlist"));

        var options = new ScopedFormalPreviewOptInOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Mode = CommandHelpers.GetOption(args, "--mode") ?? ScopedFormalPreviewOptInModes.PreviewOnly,
            WorkspaceAllowlist = workspaceAllowlist.Count == 0 ? [selectedWorkspace] : workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist.Count == 0 ? [selectedCollection] : collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist.Count == 0 ? [selectedEvalScope] : evalScopeAllowlist,
            SelectedWorkspaceId = selectedWorkspace,
            SelectedCollectionId = selectedCollection,
            SelectedEvalScope = selectedEvalScope,
            NonAllowlistedWorkspaceId = nonAllowlistedWorkspace,
            NonAllowlistedCollectionId = nonAllowlistedCollection,
            NonAllowlistedEvalScope = nonAllowlistedEvalScope,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireV4RecheckPassed = !CommandHelpers.HasFlag(args, "--skip-v4-recheck"),
            RequireGuardedFormalPreviewPassed = !CommandHelpers.HasFlag(args, "--skip-guarded-preview-gate"),
            RequireShadowPackageComparisonPassed = !CommandHelpers.HasFlag(args, "--skip-shadow-package-gate"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v4ReadinessRecheck"] = v4RecheckPath,
            ["guardedFormalRetrievalPreviewGate"] = guardedGatePath,
            ["shadowPackageComparisonGate"] = shadowGatePath
        };

        var runner = new ScopedFormalPreviewOptInRunner();
        var normalizedSubcommand = subcommand.ToLowerInvariant();
        var report = normalizedSubcommand switch
        {
            "vector-scoped-formal-preview-optin-gate" => runner.BuildGate(v4Recheck, guardedGate, shadowGate, options, sourceReports),
            "vector-scoped-formal-preview-optin-smoke" => runner.BuildSmoke(v4Recheck, guardedGate, shadowGate, options, sourceReports),
            _ => runner.BuildPlan(v4Recheck, guardedGate, shadowGate, options, sourceReports)
        };
        var fileName = normalizedSubcommand switch
        {
            "vector-scoped-formal-preview-optin-gate" => "vector-scoped-formal-preview-optin-gate",
            "vector-scoped-formal-preview-optin-smoke" => "vector-scoped-formal-preview-optin-smoke",
            _ => "vector-scoped-formal-preview-optin-plan"
        };
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                ScopedFormalPreviewOptInRunner.BuildMarkdown(
                    normalizedSubcommand switch
                    {
                        "vector-scoped-formal-preview-optin-gate" => "Vector Scoped Formal Preview Opt-in Gate",
                        "vector-scoped-formal-preview-optin-smoke" => "Vector Scoped Formal Preview Opt-in Smoke",
                        _ => "Vector Scoped Formal Preview Opt-in Plan"
                    },
                    report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(subcommand, "vector-scoped-formal-preview-optin-gate", StringComparison.OrdinalIgnoreCase))
        {
            await new LearningReadinessFreezeRunner()
                .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector scoped formal preview opt-in written: {jsonPath}");
        Console.WriteLine($"[Eval] mode={report.Mode}; gatePassed={report.GatePassed}; previewPackages={report.PreviewPackageCount}; leaks={report.NonAllowlistedScopeLeakCount}; risk={report.RiskAfterPolicy}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLimitedFormalPreviewObservationAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var scopedGatePath = Path.Combine("vector", "v4", "vector-scoped-formal-preview-optin-gate.json");
        var shadowGatePath = Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json");
        var scopedGate = await ReadJsonFileAsync<ScopedFormalPreviewOptInReport>(scopedGatePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowGate = await ReadJsonFileAsync<VectorShadowPackageComparisonReport>(shadowGatePath, cancellationToken)
            .ConfigureAwait(false);
        var workspaceAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--workspace-allowlist"));
        var collectionAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--collection-allowlist"));
        var evalScopeAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--eval-scope-allowlist"));
        var observationRuns = CommandHelpers.GetIntOption(args, "--observation-runs",
            CommandHelpers.GetIntOption(args, "--runs", 3));
        var options = new LimitedFormalPreviewObservationOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Mode = CommandHelpers.GetOption(args, "--mode") ?? ScopedFormalPreviewOptInModes.PreviewOnly,
            ObservationWindowRuns = observationRuns,
            WorkspaceAllowlist = workspaceAllowlist.Count == 0
                ? scopedGate?.WorkspaceAllowlist ?? Array.Empty<string>()
                : workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist.Count == 0
                ? scopedGate?.CollectionAllowlist ?? Array.Empty<string>()
                : collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist.Count == 0
                ? scopedGate?.EvalScopeAllowlist ?? Array.Empty<string>()
                : evalScopeAllowlist,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireScopedFormalPreviewOptInPassed = !CommandHelpers.HasFlag(args, "--skip-scoped-optin-gate"),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            FailClosedOnRisk = !CommandHelpers.HasFlag(args, "--allow-risk")
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scopedFormalPreviewOptInGate"] = scopedGatePath,
            ["shadowPackageComparisonGate"] = shadowGatePath
        };
        var runner = new LimitedFormalPreviewObservationRunner();
        var gateMode = string.Equals(subcommand, "vector-limited-formal-preview-observation-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(scopedGate, shadowGate, options, sourceReports)
            : runner.BuildObservation(scopedGate, shadowGate, options, sourceReports);
        var fileName = gateMode
            ? "vector-limited-formal-preview-observation-gate"
            : "vector-limited-formal-preview-observation";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                LimitedFormalPreviewObservationRunner.BuildMarkdown(
                    gateMode
                        ? "Vector Limited Formal Preview Observation Gate"
                        : "Vector Limited Formal Preview Observation",
                    report),
                markdownPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (gateMode)
        {
            await new LearningReadinessFreezeRunner()
                .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector limited formal preview observation written: {jsonPath}");
        Console.WriteLine($"[Eval] runs={report.ObservationRunCount}; gatePassed={report.GatePassed}; previewPackages={report.PreviewPackageCount}; leaks={report.NonAllowlistedScopeLeakCount}; risk={report.RiskAfterPolicy}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorFormalPreviewFreezeGateAsync(CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var v4RecheckPath = Path.Combine("vector", "v4", "vector-v4-readiness-recheck.json");
        var guardedGatePath = Path.Combine("vector", "v4", "vector-guarded-formal-retrieval-preview-gate.json");
        var shadowGatePath = Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json");
        var scopedOptInGatePath = Path.Combine("vector", "v4", "vector-scoped-formal-preview-optin-gate.json");
        var limitedObservationGatePath = Path.Combine("vector", "v4", "vector-limited-formal-preview-observation-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");

        var v4Recheck = await ReadJsonFileAsync<VectorV4ReadinessRecheckReport>(v4RecheckPath, cancellationToken)
            .ConfigureAwait(false);
        var guardedGate = await ReadJsonFileAsync<GuardedFormalRetrievalPreviewReport>(guardedGatePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowGate = await ReadJsonFileAsync<VectorShadowPackageComparisonReport>(shadowGatePath, cancellationToken)
            .ConfigureAwait(false);
        var scopedOptInGate = await ReadJsonFileAsync<ScopedFormalPreviewOptInReport>(scopedOptInGatePath, cancellationToken)
            .ConfigureAwait(false);
        var limitedObservationGate = await ReadJsonFileAsync<LimitedFormalPreviewObservationReport>(limitedObservationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vectorV4ReadinessRecheck"] = v4RecheckPath,
            ["guardedFormalRetrievalPreviewGate"] = guardedGatePath,
            ["vectorShadowPackageComparisonGate"] = shadowGatePath,
            ["scopedFormalPreviewOptInGate"] = scopedOptInGatePath,
            ["limitedFormalPreviewObservationGate"] = limitedObservationGatePath,
            ["learningRuntimeChangeReadinessGate"] = runtimeChangeGatePath
        };
        var report = new VectorFormalPreviewFreezeRunner().BuildGate(
            v4Recheck,
            guardedGate,
            shadowGate,
            scopedOptInGate,
            limitedObservationGate,
            runtimeChangeGate,
            sourceReports);

        var jsonPath = Path.Combine(outputDirectory, "vector-formal-preview-freeze-gate.json");
        var markdownPath = Path.Combine(outputDirectory, "vector-formal-preview-freeze-gate.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorFormalPreviewFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await new LearningReadinessFreezeRunner()
            .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector formal preview freeze gate written: {jsonPath}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; status={report.VectorFormalPreview}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteExplicitScopedRuntimeExperimentAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var foundationGatePath = Path.Combine("foundation", "foundation-release-candidate-gate.json");
        var reproducibilityPath = Path.Combine("foundation", "foundation-reproducibility-check.json");
        var serviceFreezePath = Path.Combine("service", "service-foundation-freeze-gate.json");
        var vectorFormalFreezePath = Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var guardedGatePath = Path.Combine("vector", "v4", "vector-guarded-formal-retrieval-preview-gate.json");
        var shadowGatePath = Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json");
        var scopedOptInGatePath = Path.Combine("vector", "v4", "vector-scoped-formal-preview-optin-gate.json");
        var limitedObservationGatePath = Path.Combine("vector", "v4", "vector-limited-formal-preview-observation-gate.json");

        var foundationGate = await ReadJsonFileAsync<ContextCoreFoundationFreezeReport>(foundationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var reproducibility = await ReadJsonFileAsync<FoundationReproducibilityReport>(reproducibilityPath, cancellationToken)
            .ConfigureAwait(false);
        var serviceFreeze = await ReadJsonFileAsync<ServiceFoundationFreezeReport>(serviceFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var vectorFormalFreeze = await ReadJsonFileAsync<VectorFormalPreviewFreezeReport>(vectorFormalFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var guardedGate = await ReadJsonFileAsync<GuardedFormalRetrievalPreviewReport>(guardedGatePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowGate = await ReadJsonFileAsync<VectorShadowPackageComparisonReport>(shadowGatePath, cancellationToken)
            .ConfigureAwait(false);
        var scopedOptInGate = await ReadJsonFileAsync<ScopedFormalPreviewOptInReport>(scopedOptInGatePath, cancellationToken)
            .ConfigureAwait(false);
        var limitedObservationGate = await ReadJsonFileAsync<LimitedFormalPreviewObservationReport>(limitedObservationGatePath, cancellationToken)
            .ConfigureAwait(false);

        var selectedWorkspace = CommandHelpers.GetOption(args, "--workspace")
            ?? CommandHelpers.GetOption(args, "--workspace-id")
            ?? "contextcore_eval";
        var selectedCollection = CommandHelpers.GetOption(args, "--collection")
            ?? CommandHelpers.GetOption(args, "--collection-id")
            ?? "dataset-v2-stress";
        var selectedEvalScope = CommandHelpers.GetOption(args, "--eval-scope")
            ?? "dataset-v2-stress";
        var workspaceAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--workspace-allowlist"));
        var collectionAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--collection-allowlist"));
        var evalScopeAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--eval-scope-allowlist"));
        var mode = string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase)
                ? ExplicitScopedRuntimeExperimentModes.DryRun
                : ExplicitScopedRuntimeExperimentModes.PlanOnly;
        mode = CommandHelpers.GetOption(args, "--mode") ?? mode;

        var options = new ExplicitScopedRuntimeExperimentPlanOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Mode = mode,
            WorkspaceAllowlist = workspaceAllowlist.Count == 0 ? [selectedWorkspace] : workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist.Count == 0 ? [selectedCollection] : collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist.Count == 0 ? [selectedEvalScope] : evalScopeAllowlist,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireFoundationFreeze = !CommandHelpers.HasFlag(args, "--skip-foundation-freeze"),
            RequireServiceFoundationFreeze = !CommandHelpers.HasFlag(args, "--skip-service-freeze"),
            RequireVectorFormalPreviewFreeze = !CommandHelpers.HasFlag(args, "--skip-vector-formal-preview-freeze"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package")
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundationReleaseCandidateGate"] = foundationGatePath,
            ["foundationReproducibilityCheck"] = reproducibilityPath,
            ["serviceFoundationFreezeGate"] = serviceFreezePath,
            ["vectorFormalPreviewFreezeGate"] = vectorFormalFreezePath,
            ["learningRuntimeChangeReadinessGate"] = runtimeChangeGatePath,
            ["guardedFormalRetrievalPreviewGate"] = guardedGatePath,
            ["shadowPackageComparisonGate"] = shadowGatePath,
            ["scopedFormalPreviewOptInGate"] = scopedOptInGatePath,
            ["limitedFormalPreviewObservationGate"] = limitedObservationGatePath
        };

        var runner = new ExplicitScopedRuntimeExperimentPlanRunner();
        var normalizedSubcommand = subcommand.ToLowerInvariant();
        var report = normalizedSubcommand switch
        {
            "vector-scoped-runtime-experiment-gate" => runner.BuildGate(
                foundationGate,
                reproducibility,
                serviceFreeze,
                vectorFormalFreeze,
                runtimeChangeGate,
                guardedGate,
                shadowGate,
                scopedOptInGate,
                limitedObservationGate,
                options,
                sourceReports),
            "vector-scoped-runtime-experiment-dry-run" => runner.BuildDryRun(
                foundationGate,
                reproducibility,
                serviceFreeze,
                vectorFormalFreeze,
                runtimeChangeGate,
                guardedGate,
                shadowGate,
                scopedOptInGate,
                limitedObservationGate,
                options,
                sourceReports),
            _ => runner.BuildPlan(
                foundationGate,
                reproducibility,
                serviceFreeze,
                vectorFormalFreeze,
                runtimeChangeGate,
                guardedGate,
                shadowGate,
                scopedOptInGate,
                limitedObservationGate,
                options,
                sourceReports)
        };
        var fileName = normalizedSubcommand switch
        {
            "vector-scoped-runtime-experiment-gate" => "vector-scoped-runtime-experiment-gate",
            "vector-scoped-runtime-experiment-dry-run" => "vector-scoped-runtime-experiment-dry-run",
            _ => "vector-scoped-runtime-experiment-plan"
        };
        var title = normalizedSubcommand switch
        {
            "vector-scoped-runtime-experiment-gate" => "Vector Scoped Runtime Experiment Gate",
            "vector-scoped-runtime-experiment-dry-run" => "Vector Scoped Runtime Experiment Dry-run",
            _ => "Vector Scoped Runtime Experiment Plan"
        };
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ExplicitScopedRuntimeExperimentPlanRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase))
        {
            await new LearningReadinessFreezeRunner()
                .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector scoped runtime experiment planning written: {jsonPath}");
        Console.WriteLine($"[Eval] mode={report.Mode}; passed={report.PlanPassed}; scopes={report.AllowlistedScopeCount}; runtimeSwitch={report.RuntimeSwitchAllowed}; formalRetrieval={report.FormalRetrievalAllowed}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentDryRunObservationAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var v45GatePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-gate.json");
        var shadowGatePath = Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");

        var v45Gate = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentPlanReport>(v45GatePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowGate = await ReadJsonFileAsync<VectorShadowPackageComparisonReport>(shadowGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);

        var selectedWorkspace = CommandHelpers.GetOption(args, "--workspace")
            ?? CommandHelpers.GetOption(args, "--workspace-id")
            ?? v45Gate?.WorkspaceAllowlist.FirstOrDefault()
            ?? "contextcore_eval";
        var selectedCollection = CommandHelpers.GetOption(args, "--collection")
            ?? CommandHelpers.GetOption(args, "--collection-id")
            ?? v45Gate?.CollectionAllowlist.FirstOrDefault()
            ?? "dataset-v2-stress";
        var selectedEvalScope = CommandHelpers.GetOption(args, "--eval-scope")
            ?? v45Gate?.EvalScopeAllowlist.FirstOrDefault()
            ?? "dataset-v2-stress";
        var workspaceAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--workspace-allowlist"));
        var collectionAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--collection-allowlist"));
        var evalScopeAllowlist = ParseCsvOption(CommandHelpers.GetOption(args, "--eval-scope-allowlist"));
        var observationRuns = CommandHelpers.GetIntOption(
            args,
            "--observation-runs",
            CommandHelpers.GetIntOption(args, "--runs", 3));

        var options = new ScopedRuntimeExperimentDryRunObservationOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Mode = CommandHelpers.GetOption(args, "--mode") ?? ScopedRuntimeExperimentDryRunObservationModes.DryRun,
            ObservationRunCount = observationRuns,
            WorkspaceAllowlist = workspaceAllowlist.Count == 0 ? [selectedWorkspace] : workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist.Count == 0 ? [selectedCollection] : collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist.Count == 0 ? [selectedEvalScope] : evalScopeAllowlist,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RequireV45PlanPassed = !CommandHelpers.HasFlag(args, "--skip-v45-gate"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            FailClosedOnRisk = !CommandHelpers.HasFlag(args, "--allow-risk"),
            RuntimeMutated = CommandHelpers.HasFlag(args, "--runtime-mutated"),
            VectorStoreBindingChanged = CommandHelpers.HasFlag(args, "--vector-store-binding-changed"),
            PackingPolicyChanged = CommandHelpers.HasFlag(args, "--packing-policy-changed"),
            PackageOutputChanged = CommandHelpers.HasFlag(args, "--package-output-changed")
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v45ScopedRuntimeExperimentGate"] = v45GatePath,
            ["shadowPackageComparisonGate"] = shadowGatePath,
            ["learningRuntimeChangeReadinessGate"] = runtimeChangeGatePath
        };

        var runner = new ScopedRuntimeExperimentDryRunObservationRunner();
        var isGate = string.Equals(
            subcommand,
            "vector-scoped-runtime-experiment-dry-run-observation-gate",
            StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.BuildGate(v45Gate, shadowGate, runtimeChangeGate, options, sourceReports)
            : runner.BuildObservation(v45Gate, shadowGate, runtimeChangeGate, options, sourceReports);
        var fileName = isGate
            ? "vector-scoped-runtime-experiment-dry-run-observation-gate"
            : "vector-scoped-runtime-experiment-dry-run-observation";
        var title = isGate
            ? "Vector Scoped Runtime Experiment Dry-run Observation Gate"
            : "Vector Scoped Runtime Experiment Dry-run Observation";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentDryRunObservationRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment dry-run observation written: {jsonPath}");
        Console.WriteLine($"[Eval] runs={report.ObservationRunCount}; gate={report.GatePassed}; packages={report.DryRunPackageCount}/{report.BaselinePackageCount}; runtimeMutated={report.RuntimeMutated}; vectorBindingChanged={report.VectorStoreBindingChanged}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentDesignFreezeGateAsync(CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var foundationGatePath = Path.Combine("foundation", "foundation-release-candidate-gate.json");
        var serviceFreezePath = Path.Combine("service", "service-foundation-freeze-gate.json");
        var vectorFormalFreezePath = Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json");
        var scopedRuntimeExperimentGatePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-gate.json");
        var dryRunObservationGatePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-dry-run-observation-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");

        var foundationGate = await ReadJsonFileAsync<ContextCoreFoundationFreezeReport>(foundationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var serviceFreeze = await ReadJsonFileAsync<ServiceFoundationFreezeReport>(serviceFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var vectorFormalFreeze = await ReadJsonFileAsync<VectorFormalPreviewFreezeReport>(vectorFormalFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var scopedRuntimeExperimentGate = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentPlanReport>(scopedRuntimeExperimentGatePath, cancellationToken)
            .ConfigureAwait(false);
        var dryRunObservationGate = await ReadJsonFileAsync<ScopedRuntimeExperimentDryRunObservationReport>(dryRunObservationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
            && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundationReleaseCandidateGate"] = foundationGatePath,
            ["serviceFoundationFreezeGate"] = serviceFreezePath,
            ["vectorFormalPreviewFreezeGate"] = vectorFormalFreezePath,
            ["scopedRuntimeExperimentGate"] = scopedRuntimeExperimentGatePath,
            ["dryRunObservationGate"] = dryRunObservationGatePath,
            ["learningRuntimeChangeReadinessGate"] = runtimeChangeGatePath,
            ["p15A3"] = Path.Combine("eval", "eval-report-p15-a3.json"),
            ["p15Extended"] = Path.Combine("eval", "eval-report-p15-extended.json")
        };
        var report = new ScopedRuntimeExperimentDesignFreezeRunner().BuildGate(
            foundationGate,
            serviceFreeze,
            vectorFormalFreeze,
            scopedRuntimeExperimentGate,
            dryRunObservationGate,
            runtimeChangeGate,
            p15Passed,
            sourceReports);

        var jsonPath = Path.Combine(outputDirectory, "vector-scoped-runtime-experiment-design-freeze-gate.json");
        var markdownPath = Path.Combine(outputDirectory, "vector-scoped-runtime-experiment-design-freeze-gate.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentDesignFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment design freeze gate written: {jsonPath}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; design={report.DesignStatus}; proposalReady={report.ReadyForRuntimeExperimentProposal}; runtimeSwitch={report.RuntimeSwitchAllowed}; formalRetrieval={report.FormalRetrievalAllowed}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentProposalAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4"));
        Directory.CreateDirectory(outputDirectory);

        var foundationGatePath = Path.Combine("foundation", "foundation-release-candidate-gate.json");
        var reproducibilityPath = Path.Combine("foundation", "foundation-reproducibility-check.json");
        var serviceFreezePath = Path.Combine("service", "service-foundation-freeze-gate.json");
        var vectorFormalFreezePath = Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json");
        var designFreezePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-design-freeze-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var scopedRuntimeExperimentGatePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-gate.json");

        var foundationGate = await ReadJsonFileAsync<ContextCoreFoundationFreezeReport>(foundationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var reproducibility = await ReadJsonFileAsync<FoundationReproducibilityReport>(reproducibilityPath, cancellationToken)
            .ConfigureAwait(false);
        var serviceFreeze = await ReadJsonFileAsync<ServiceFoundationFreezeReport>(serviceFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var vectorFormalFreeze = await ReadJsonFileAsync<VectorFormalPreviewFreezeReport>(vectorFormalFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var designFreeze = await ReadJsonFileAsync<ScopedRuntimeExperimentDesignFreezeReport>(designFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var scopedRuntimeExperimentGate = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentPlanReport>(scopedRuntimeExperimentGatePath, cancellationToken)
            .ConfigureAwait(false);

        var workspaceId = CommandHelpers.GetOption(args, "--workspace")
            ?? CommandHelpers.GetOption(args, "--workspace-id")
            ?? scopedRuntimeExperimentGate?.WorkspaceAllowlist.FirstOrDefault()
            ?? "contextcore_eval";
        var collectionId = CommandHelpers.GetOption(args, "--collection")
            ?? CommandHelpers.GetOption(args, "--collection-id")
            ?? scopedRuntimeExperimentGate?.CollectionAllowlist.FirstOrDefault()
            ?? "dataset-v2-stress";
        var evalScopeId = CommandHelpers.GetOption(args, "--eval-scope")
            ?? CommandHelpers.GetOption(args, "--eval-scope-id")
            ?? scopedRuntimeExperimentGate?.EvalScopeAllowlist.FirstOrDefault()
            ?? "dataset-v2-stress";
        var rollbackPlan = CommandHelpers.GetOption(args, "--rollback-plan")
            ?? "Remove the selected scope from the proposal allowlist, keep UseForRuntime=false, discard shadow artifacts, rerun V4.7 and runtime-change gates.";
        var killSwitchPlan = CommandHelpers.GetOption(args, "--kill-switch-plan")
            ?? "Set proposal mode to ProposalOnly, clear workspace/collection/eval scope allowlists, and rerun runtime-change gate before any new proposal.";

        var options = new ExplicitScopedRuntimeExperimentProposalOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? string.Empty,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EvalScopeId = evalScopeId,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            Mode = CommandHelpers.GetOption(args, "--mode")
                ?? ExplicitScopedRuntimeExperimentProposalModes.ProposalOnly,
            RequireV47DesignFreeze = !CommandHelpers.HasFlag(args, "--skip-v47-design-freeze"),
            RequireFoundationFreeze = !CommandHelpers.HasFlag(args, "--skip-foundation-freeze"),
            RequireServiceFoundationFreeze = !CommandHelpers.HasFlag(args, "--skip-service-freeze"),
            RequireVectorFormalPreviewFreeze = !CommandHelpers.HasFlag(args, "--skip-vector-formal-freeze"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireManualApproval = !CommandHelpers.HasFlag(args, "--no-manual-approval"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            RollbackPlan = CommandHelpers.HasFlag(args, "--missing-rollback-plan") ? string.Empty : rollbackPlan,
            KillSwitchPlan = CommandHelpers.HasFlag(args, "--missing-kill-switch-plan") ? string.Empty : killSwitchPlan,
            Approved = CommandHelpers.HasFlag(args, "--approved")
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundationReleaseCandidateGate"] = foundationGatePath,
            ["foundationReproducibilityCheck"] = reproducibilityPath,
            ["serviceFoundationFreezeGate"] = serviceFreezePath,
            ["vectorFormalPreviewFreezeGate"] = vectorFormalFreezePath,
            ["scopedRuntimeExperimentDesignFreezeGate"] = designFreezePath,
            ["learningRuntimeChangeReadinessGate"] = runtimeChangeGatePath,
            ["scopedRuntimeExperimentGate"] = scopedRuntimeExperimentGatePath
        };
        var runner = new ExplicitScopedRuntimeExperimentProposalRunner();
        var report = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-config-preview", StringComparison.OrdinalIgnoreCase)
                => runner.BuildConfigPreview(
                    foundationGate,
                    reproducibility,
                    serviceFreeze,
                    vectorFormalFreeze,
                    designFreeze,
                    runtimeChangeGate,
                    options,
                    sourceReports),
            var value when string.Equals(value, "vector-scoped-runtime-experiment-proposal-gate", StringComparison.OrdinalIgnoreCase)
                => runner.BuildGate(
                    foundationGate,
                    reproducibility,
                    serviceFreeze,
                    vectorFormalFreeze,
                    designFreeze,
                    runtimeChangeGate,
                    options,
                    sourceReports),
            _ => runner.BuildProposal(
                foundationGate,
                reproducibility,
                serviceFreeze,
                vectorFormalFreeze,
                designFreeze,
                runtimeChangeGate,
                options,
                sourceReports)
        };
        var fileName = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-config-preview", StringComparison.OrdinalIgnoreCase)
                => "vector-scoped-runtime-experiment-config-preview",
            var value when string.Equals(value, "vector-scoped-runtime-experiment-proposal-gate", StringComparison.OrdinalIgnoreCase)
                => "vector-scoped-runtime-experiment-proposal-gate",
            _ => "vector-scoped-runtime-experiment-proposal"
        };
        var title = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-config-preview", StringComparison.OrdinalIgnoreCase)
                => "Vector Scoped Runtime Experiment Config Preview",
            var value when string.Equals(value, "vector-scoped-runtime-experiment-proposal-gate", StringComparison.OrdinalIgnoreCase)
                => "Vector Scoped Runtime Experiment Proposal Gate",
            _ => "Vector Scoped Runtime Experiment Proposal"
        };
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ExplicitScopedRuntimeExperimentProposalRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment proposal written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; passed={report.ProposalPassed}; approved={report.Approved}; runtimeSwitch={report.RuntimeSwitchAllowed}; formalRetrieval={report.FormalRetrievalAllowed}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentApprovalAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var proposalPath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-proposal-gate.json");
        var proposal = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentProposalReport>(proposalPath, cancellationToken)
            .ConfigureAwait(false);
        var store = new FileSystemScopedRuntimeExperimentApprovalStore(Path.Combine(outputDirectory, "approval-record.json"));
        var service = new ScopedRuntimeExperimentApprovalService(store);
        var proposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? proposal?.ProposalId ?? string.Empty;

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-summary", StringComparison.OrdinalIgnoreCase))
        {
            var summary = await service.BuildSummaryAsync(proposal, cancellationToken).ConfigureAwait(false);
            var summaryJson = Path.Combine(outputDirectory, "approval-summary.json");
            var summaryMd = Path.Combine(outputDirectory, "approval-summary.md");
            await WriteTextAsync(JsonSerializer.Serialize(summary, JsonOptions), summaryJson, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(ScopedRuntimeExperimentApprovalService.BuildSummaryMarkdown(summary), summaryMd, cancellationToken).ConfigureAwait(false);
            var guardedPlan = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentPlanReport>(
                    Path.Combine(outputDirectory, "guarded-runtime-experiment-plan-gate.json"),
                    cancellationToken)
                .ConfigureAwait(false);
            IScopedRuntimeExperimentApprovalStore runtimeApprovalStore = new FileSystemScopedRuntimeExperimentApprovalStore(Path.Combine(outputDirectory, "runtime-approval-record.json"));
            var runtimeApproval = await runtimeApprovalStore.GetLatestByProposalIdAsync(guardedPlan?.ProposalId ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
            var runtimeSummary = new ScopedRuntimeExperimentRuntimeApprovalRunner().BuildGate(guardedPlan, runtimeApproval);
            await WriteRuntimeApprovalGateArtifactsAsync(
                    outputDirectory,
                    "runtime-approval-summary",
                    "Vector Scoped Runtime Experiment Approval Summary",
                    runtimeSummary,
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector scoped runtime experiment approval summary written: {summaryJson}");
            Console.WriteLine($"[Eval] proposalId={summary.ProposalId}; approvals={summary.ApprovalCount}; recommendation={summary.Recommendation}; runtimeApproval={runtimeSummary.Recommendation}");
            return;
        }

        var reason = CommandHelpers.HasFlag(args, "--missing-reason")
            ? string.Empty
            : CommandHelpers.GetOption(args, "--reason")
                ?? "V4.9 no-op harness approval for scoped runtime experiment proposal.";
        var options = new ScopedRuntimeExperimentApprovalOptions
        {
            ProposalId = proposalId,
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? string.Empty,
            Reason = reason,
            RequireExplicitConfirm = true,
            ApprovalMode = CommandHelpers.GetOption(args, "--approval-mode")
                ?? ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly,
            AllowRuntimeSwitch = CommandHelpers.HasFlag(args, "--allow-runtime-switch"),
            AllowFormalRetrieval = CommandHelpers.HasFlag(args, "--allow-formal-retrieval"),
            AllowFormalPackageWrite = CommandHelpers.HasFlag(args, "--allow-formal-package-write"),
            AllowPackingPolicyChange = CommandHelpers.HasFlag(args, "--allow-packing-policy-change")
        };

        var confirmed = CommandHelpers.HasFlag(args, "--confirm");
        ScopedRuntimeExperimentApprovalReport report;
        string fileName;
        string title;
        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-approve", StringComparison.OrdinalIgnoreCase))
        {
            report = await service.ApproveAsync(proposal, options, confirmed, cancellationToken).ConfigureAwait(false);
            fileName = report.RecordWritten ? "approval-record" : "approval-preview";
            title = report.RecordWritten
                ? "Vector Scoped Runtime Experiment Approval Record"
                : "Vector Scoped Runtime Experiment Approval Preview";
        }
        else
        {
            report = service.BuildPreview(proposal, options);
            fileName = "approval-preview";
            title = "Vector Scoped Runtime Experiment Approval Preview";
        }

        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        var jsonPayload = report.RecordWritten && report.ApprovalRecord is not null
            ? JsonSerializer.Serialize(report.ApprovalRecord, JsonOptions)
            : JsonSerializer.Serialize(report, JsonOptions);
        await WriteTextAsync(jsonPayload, jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentApprovalService.BuildApprovalMarkdown(title, report), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment approval written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; approvalId={report.ApprovalId}; written={report.RecordWritten}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentRuntimeApprovalAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var planPath = Path.Combine(outputDirectory, "guarded-runtime-experiment-plan-gate.json");
        var recordPath = Path.Combine(outputDirectory, "runtime-approval-record.json");
        var plan = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentPlanReport>(planPath, cancellationToken)
            .ConfigureAwait(false);
        IScopedRuntimeExperimentApprovalStore store = new FileSystemScopedRuntimeExperimentApprovalStore(recordPath);
        var existingApproval = await store.GetLatestByProposalIdAsync(plan?.ProposalId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        var runner = new ScopedRuntimeExperimentRuntimeApprovalRunner();

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-request-preview", StringComparison.OrdinalIgnoreCase))
        {
            var preview = runner.BuildRequestPreview(plan);
            var previewJsonPath = Path.Combine(outputDirectory, "runtime-approval-request-preview.json");
            var previewMarkdownPath = Path.Combine(outputDirectory, "runtime-approval-request-preview.md");
            await WriteTextAsync(JsonSerializer.Serialize(preview, JsonOptions), previewJsonPath, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(ScopedRuntimeExperimentRuntimeApprovalRunner.BuildRequestPreviewMarkdown(preview), previewMarkdownPath, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector scoped runtime experiment approval request preview written: {previewJsonPath}");
            Console.WriteLine($"[Eval] proposalId={preview.ProposalId}; requiredApproval={preview.RequiredApprovalMode}; written={preview.RecordWritten}; recommendation={preview.Recommendation}");
            return;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-approval-gate", StringComparison.OrdinalIgnoreCase))
        {
            var gate = runner.BuildGate(plan, existingApproval);
            await WriteRuntimeApprovalGateArtifactsAsync(outputDirectory, "runtime-approval-gate", "Vector Scoped Runtime Experiment Approval Gate", gate, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector scoped runtime experiment approval gate written: {Path.Combine(outputDirectory, "runtime-approval-gate.json")}");
            Console.WriteLine($"[Eval] proposalId={gate.ProposalId}; approvalId={gate.ApprovalId}; passed={gate.GatePassed}; recommendation={gate.Recommendation}");
            return;
        }

        var options = new ScopedRuntimeExperimentApprovalOptions
        {
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? plan?.ProposalId ?? string.Empty,
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? string.Empty,
            Reason = CommandHelpers.GetOption(args, "--reason") ?? string.Empty,
            RequireExplicitConfirm = true,
            ApprovalMode = CommandHelpers.GetOption(args, "--approval-mode") ?? ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
            AllowRuntimeSwitch = CommandHelpers.HasFlag(args, "--allow-runtime-switch"),
            AllowFormalRetrieval = CommandHelpers.HasFlag(args, "--allow-formal-retrieval"),
            AllowFormalPackageWrite = CommandHelpers.HasFlag(args, "--allow-formal-package-write"),
            AllowPackingPolicyChange = CommandHelpers.HasFlag(args, "--allow-packing-policy-change"),
            RiskAcknowledgement = CommandHelpers.GetOption(args, "--risk-acknowledgement") ?? string.Empty,
            RollbackAcknowledgement = CommandHelpers.GetOption(args, "--rollback-acknowledgement") ?? string.Empty,
            KillSwitchAcknowledgement = CommandHelpers.GetOption(args, "--kill-switch-acknowledgement") ?? string.Empty,
            ScopeAcknowledgement = CommandHelpers.GetOption(args, "--scope-acknowledgement") ?? string.Empty,
            ObservationPlanAcknowledgement = CommandHelpers.GetOption(args, "--observation-plan-acknowledgement") ?? string.Empty
        };
        var confirmed = CommandHelpers.HasFlag(args, "--confirm");
        var report = runner.BuildApproval(plan, options, confirmed);
        var fileName = report.RecordWritten ? "runtime-approval-record" : "runtime-approval-request-preview";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        if (report.RecordWritten && report.ApprovalRecord is not null)
        {
            await store.SaveAsync(report.ApprovalRecord, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(JsonSerializer.Serialize(report.ApprovalRecord, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        }

        await WriteTextAsync(ScopedRuntimeExperimentRuntimeApprovalRunner.BuildApprovalMarkdown(
                report.RecordWritten
                    ? "Vector Scoped Runtime Experiment Runtime Approval Record"
                    : "Vector Scoped Runtime Experiment Runtime Approval Preview",
                report),
            markdownPath,
            cancellationToken).ConfigureAwait(false);

        var latestApproval = report.RecordWritten ? report.ApprovalRecord : existingApproval;
        var summary = runner.BuildGate(plan, latestApproval);
        await WriteRuntimeApprovalGateArtifactsAsync(outputDirectory, "runtime-approval-summary", "Vector Scoped Runtime Experiment Approval Summary", summary, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment runtime approval written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; approvalId={report.ApprovalId}; written={report.RecordWritten}; recommendation={report.Recommendation}");
    }

    private static async Task WriteRuntimeApprovalGateArtifactsAsync(
        string outputDirectory,
        string fileName,
        string title,
        ScopedRuntimeExperimentApprovalGateReport report,
        CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentRuntimeApprovalRunner.BuildGateMarkdown(title, report), markdownPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteScopedRuntimeExperimentNoOpHarnessAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var proposalPath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-proposal-gate.json");
        var approvalPath = Path.Combine(outputDirectory, "approval-record.json");
        var harnessPath = Path.Combine(outputDirectory, "noop-harness-report.json");
        var proposal = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentProposalReport>(proposalPath, cancellationToken)
            .ConfigureAwait(false);
        var approval = await ReadJsonFileAsync<ScopedRuntimeExperimentApprovalRecord>(approvalPath, cancellationToken)
            .ConfigureAwait(false);
        var existingHarness = await ReadJsonFileAsync<ScopedRuntimeExperimentNoOpHarnessReport>(harnessPath, cancellationToken)
            .ConfigureAwait(false);

        var options = new ScopedRuntimeExperimentNoOpHarnessOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? proposal?.ProposalId ?? string.Empty,
            ApprovalId = CommandHelpers.GetOption(args, "--approval-id") ?? approval?.ApprovalId ?? string.Empty,
            Mode = CommandHelpers.GetOption(args, "--mode") ?? ScopedRuntimeExperimentNoOpHarnessModes.NoOp,
            WorkspaceAllowlist = SplitOption(CommandHelpers.GetOption(args, "--workspace-allowlist") ?? proposal?.WorkspaceId),
            CollectionAllowlist = SplitOption(CommandHelpers.GetOption(args, "--collection-allowlist") ?? proposal?.CollectionId),
            EvalScopeAllowlist = SplitOption(CommandHelpers.GetOption(args, "--eval-scope-allowlist") ?? proposal?.EvalScopeId),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            RuntimeSwitchAllowed = CommandHelpers.HasFlag(args, "--runtime-switch-allowed"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            MutateRuntime = CommandHelpers.HasFlag(args, "--mutate-runtime"),
            VectorStoreBindingChanged = CommandHelpers.HasFlag(args, "--vector-store-binding-changed"),
            PackingPolicyChanged = CommandHelpers.HasFlag(args, "--packing-policy-changed"),
            PackageOutputChanged = CommandHelpers.HasFlag(args, "--package-output-changed")
        };

        var runner = new ScopedRuntimeExperimentNoOpHarnessRunner();
        var report = string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness-gate", StringComparison.OrdinalIgnoreCase)
            ? runner.BuildGate(proposal, approval, existingHarness, options, p15GatePassed: true)
            : runner.BuildHarness(proposal, approval, options, p15GatePassed: true);
        var fileName = string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness-gate", StringComparison.OrdinalIgnoreCase)
            ? "noop-harness-gate"
            : "noop-harness-report";
        var title = string.Equals(subcommand, "vector-scoped-runtime-experiment-noop-harness-gate", StringComparison.OrdinalIgnoreCase)
            ? "Vector Scoped Runtime Experiment No-op Harness Gate"
            : "Vector Scoped Runtime Experiment No-op Harness Report";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentNoOpHarnessRunner.BuildMarkdown(title, report), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment no-op harness written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; approvalId={report.ApprovalId}; passed={report.HarnessPassed}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentHarnessFreezeGateAsync(CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var proposalPath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-proposal-gate.json");
        var approvalSummaryPath = Path.Combine("vector", "v4", "runtime-experiment", "approval-summary.json");
        var noOpHarnessGatePath = Path.Combine("vector", "v4", "runtime-experiment", "noop-harness-gate.json");
        var designFreezePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-design-freeze-gate.json");
        var serviceFreezePath = Path.Combine("service", "service-foundation-freeze-gate.json");
        var foundationGatePath = Path.Combine("foundation", "foundation-release-candidate-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var proposal = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentProposalReport>(proposalPath, cancellationToken)
            .ConfigureAwait(false);
        var approvalSummary = await ReadJsonFileAsync<ScopedRuntimeExperimentApprovalSummaryReport>(approvalSummaryPath, cancellationToken)
            .ConfigureAwait(false);
        var noOpHarnessGate = await ReadJsonFileAsync<ScopedRuntimeExperimentNoOpHarnessReport>(noOpHarnessGatePath, cancellationToken)
            .ConfigureAwait(false);
        var designFreeze = await ReadJsonFileAsync<ScopedRuntimeExperimentDesignFreezeReport>(designFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var serviceFreeze = await ReadJsonFileAsync<ServiceFoundationFreezeReport>(serviceFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var foundationGate = await ReadJsonFileAsync<ContextCoreFoundationFreezeReport>(foundationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
            && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        var report = new ScopedRuntimeExperimentHarnessFreezeRunner().BuildGate(
            proposal,
            approvalSummary,
            noOpHarnessGate,
            designFreeze,
            serviceFreeze,
            foundationGate,
            runtimeChangeGate,
            p15Passed);
        var jsonPath = Path.Combine(outputDirectory, "harness-freeze-gate.json");
        var markdownPath = Path.Combine(outputDirectory, "harness-freeze-gate.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentHarnessFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector scoped runtime experiment harness freeze gate written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; approvalId={report.ApprovalId}; passed={report.FreezePassed}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteGuardedScopedRuntimeExperimentPlanAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var foundationGatePath = Path.Combine("foundation", "foundation-release-candidate-gate.json");
        var serviceFreezePath = Path.Combine("service", "service-foundation-freeze-gate.json");
        var vectorFormalFreezePath = Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json");
        var designFreezePath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-design-freeze-gate.json");
        var harnessFreezePath = Path.Combine("vector", "v4", "runtime-experiment", "harness-freeze-gate.json");
        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var proposalPath = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-proposal-gate.json");

        var foundationGate = await ReadJsonFileAsync<ContextCoreFoundationFreezeReport>(foundationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var serviceFreeze = await ReadJsonFileAsync<ServiceFoundationFreezeReport>(serviceFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var vectorFormalFreeze = await ReadJsonFileAsync<VectorFormalPreviewFreezeReport>(vectorFormalFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var designFreeze = await ReadJsonFileAsync<ScopedRuntimeExperimentDesignFreezeReport>(designFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var harnessFreeze = await ReadJsonFileAsync<ScopedRuntimeExperimentHarnessFreezeReport>(harnessFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var proposal = await ReadJsonFileAsync<ExplicitScopedRuntimeExperimentProposalReport>(proposalPath, cancellationToken)
            .ConfigureAwait(false);

        var workspaceAllowlist = SplitOption(CommandHelpers.GetOption(args, "--workspace-allowlist")
            ?? CommandHelpers.GetOption(args, "--workspace")
            ?? proposal?.WorkspaceId);
        var collectionAllowlist = SplitOption(CommandHelpers.GetOption(args, "--collection-allowlist")
            ?? CommandHelpers.GetOption(args, "--collection")
            ?? proposal?.CollectionId);
        var evalScopeAllowlist = SplitOption(CommandHelpers.GetOption(args, "--eval-scope-allowlist")
            ?? CommandHelpers.GetOption(args, "--eval-scope")
            ?? proposal?.EvalScopeId);
        var options = new GuardedScopedRuntimeExperimentPlanOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Mode = CommandHelpers.GetOption(args, "--mode") ?? GuardedScopedRuntimeExperimentPlanModes.PlanOnly,
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? proposal?.ProposalId ?? string.Empty,
            RequiredApprovalMode = CommandHelpers.GetOption(args, "--required-approval-mode")
                ?? ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
            WorkspaceAllowlist = CommandHelpers.HasFlag(args, "--missing-scope") ? Array.Empty<string>() : workspaceAllowlist,
            CollectionAllowlist = CommandHelpers.HasFlag(args, "--missing-scope") ? Array.Empty<string>() : collectionAllowlist,
            EvalScopeAllowlist = CommandHelpers.HasFlag(args, "--missing-scope") ? Array.Empty<string>() : evalScopeAllowlist,
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            MaxRequestCount = CommandHelpers.GetIntOption(args, "--max-request-count", 120),
            MaxDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 30),
            MaxErrorCount = CommandHelpers.GetIntOption(args, "--max-error-count", 0),
            MaxRiskCount = CommandHelpers.GetIntOption(args, "--max-risk-count", 0),
            RequireKillSwitch = !CommandHelpers.HasFlag(args, "--missing-kill-switch"),
            RequireRollbackPlan = !CommandHelpers.HasFlag(args, "--missing-rollback"),
            RequireObservationPlan = !CommandHelpers.HasFlag(args, "--missing-observation-plan"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            RuntimeSwitchAllowed = CommandHelpers.HasFlag(args, "--runtime-switch-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch")
        };

        ExplicitScopedRuntimeExperimentProposalReport? effectiveProposal = proposal;
        if (CommandHelpers.HasFlag(args, "--missing-kill-switch") && effectiveProposal is not null)
        {
            effectiveProposal = CopyProposalWithPlans(effectiveProposal, killSwitchPlan: string.Empty);
        }

        if (CommandHelpers.HasFlag(args, "--missing-rollback") && effectiveProposal is not null)
        {
            effectiveProposal = CopyProposalWithPlans(effectiveProposal, rollbackPlan: string.Empty);
        }

        var runner = new GuardedScopedRuntimeExperimentPlanRunner();
        var report = string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan-gate", StringComparison.OrdinalIgnoreCase)
            ? runner.BuildGate(
                foundationGate,
                serviceFreeze,
                vectorFormalFreeze,
                designFreeze,
                harnessFreeze,
                runtimeChangeGate,
                effectiveProposal,
                options)
            : runner.BuildPlan(
                foundationGate,
                serviceFreeze,
                vectorFormalFreeze,
                designFreeze,
                harnessFreeze,
                runtimeChangeGate,
                effectiveProposal,
                options);
        var fileName = string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan-gate", StringComparison.OrdinalIgnoreCase)
            ? "guarded-runtime-experiment-plan-gate"
            : "guarded-runtime-experiment-plan";
        var title = string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-plan-gate", StringComparison.OrdinalIgnoreCase)
            ? "Vector Guarded Scoped Runtime Experiment Plan Gate"
            : "Vector Guarded Scoped Runtime Experiment Plan";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(GuardedScopedRuntimeExperimentPlanRunner.BuildMarkdown(title, report), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector guarded scoped runtime experiment plan written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; passed={report.PlanPassed}; requiredApproval={report.RequiredApprovalMode}; runtimeSwitch={report.RuntimeSwitchAllowed}; formalRetrieval={report.FormalRetrievalAllowed}; recommendation={report.Recommendation}");
    }

    private static ExplicitScopedRuntimeExperimentProposalReport CopyProposalWithPlans(
        ExplicitScopedRuntimeExperimentProposalReport source,
        string? killSwitchPlan = null,
        string? rollbackPlan = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            ProposalId = source.ProposalId,
            ProposalPassed = source.ProposalPassed,
            Recommendation = source.Recommendation,
            WorkspaceId = source.WorkspaceId,
            CollectionId = source.CollectionId,
            EvalScopeId = source.EvalScopeId,
            ProfileName = source.ProfileName,
            RequiredGateSummary = source.RequiredGateSummary,
            ProposedConfigPatch = source.ProposedConfigPatch,
            RollbackPlan = rollbackPlan ?? source.RollbackPlan,
            KillSwitchPlan = killSwitchPlan ?? source.KillSwitchPlan,
            ObservationPlan = source.ObservationPlan,
            ApprovalRequired = source.ApprovalRequired,
            Approved = source.Approved,
            RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
            FormalRetrievalAllowed = source.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = source.ReadyForRuntimeSwitch,
            UseForRuntime = source.UseForRuntime,
            WriteFormalPackage = source.WriteFormalPackage,
            ConfigPatchWritten = source.ConfigPatchWritten,
            DiBindingChanged = source.DiBindingChanged,
            PackingPolicyChanged = source.PackingPolicyChanged,
            PackageOutputChanged = source.PackageOutputChanged,
            NonAllowlistedScopeLeakCount = source.NonAllowlistedScopeLeakCount,
            ForbiddenActions = source.ForbiddenActions,
            BlockedReasons = source.BlockedReasons,
            SourceReports = source.SourceReports
        };

    private static async Task ExecuteScopedRuntimeExperimentActivationPreflightAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var foundationGate = await ReadJsonFileAsync<ContextCoreFoundationFreezeReport>(
                Path.Combine("foundation", "foundation-release-candidate-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var serviceFreeze = await ReadJsonFileAsync<ServiceFoundationFreezeReport>(
                Path.Combine("service", "service-foundation-freeze-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var vectorFormalFreeze = await ReadJsonFileAsync<VectorFormalPreviewFreezeReport>(
                Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var plan = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentPlanReport>(
                Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-plan-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var approvalGate = await ReadJsonFileAsync<ScopedRuntimeExperimentApprovalGateReport>(
                Path.Combine("vector", "v4", "runtime-experiment", "runtime-approval-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(
                Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var existingPreflight = await ReadJsonFileAsync<ScopedRuntimeExperimentActivationPreflightReport>(
                Path.Combine("vector", "v4", "runtime-experiment", "activation-preflight.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var existingRoute = await ReadJsonFileAsync<ScopedRuntimeExperimentActivationPreflightReport>(
                Path.Combine("vector", "v4", "runtime-experiment", "dry-run-route-report.json"),
                cancellationToken)
            .ConfigureAwait(false);

        if (CommandHelpers.HasFlag(args, "--missing-approval"))
        {
            approvalGate = null;
        }

        GuardedScopedRuntimeExperimentPlanReport? effectivePlan = plan;
        if (CommandHelpers.HasFlag(args, "--missing-kill-switch") && effectivePlan is not null)
        {
            effectivePlan = CopyGuardedScopedRuntimeExperimentPlan(effectivePlan, killSwitchPlan: string.Empty);
        }

        if (CommandHelpers.HasFlag(args, "--missing-rollback") && effectivePlan is not null)
        {
            effectivePlan = CopyGuardedScopedRuntimeExperimentPlan(effectivePlan, rollbackPlan: string.Empty);
        }

        var options = new ScopedRuntimeExperimentActivationPreflightOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? effectivePlan?.ProposalId ?? approvalGate?.ProposalId ?? string.Empty,
            ApprovalId = CommandHelpers.GetOption(args, "--approval-id") ?? approvalGate?.ApprovalId ?? string.Empty,
            Mode = CommandHelpers.GetOption(args, "--mode") ?? ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute,
            RequireV411PlanPassed = !CommandHelpers.HasFlag(args, "--skip-v411-gate"),
            RequireV412ApprovalPassed = !CommandHelpers.HasFlag(args, "--skip-v412-gate"),
            RequireFoundationFreeze = !CommandHelpers.HasFlag(args, "--skip-foundation-freeze"),
            RequireServiceFoundationFreeze = !CommandHelpers.HasFlag(args, "--skip-service-freeze"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireKillSwitch = !CommandHelpers.HasFlag(args, "--skip-kill-switch-requirement"),
            RequireRollbackPlan = !CommandHelpers.HasFlag(args, "--skip-rollback-requirement"),
            RequireTraceSink = !CommandHelpers.HasFlag(args, "--skip-trace-sink-requirement"),
            TraceSinkAvailable = !CommandHelpers.HasFlag(args, "--missing-trace-sink"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            RuntimeSwitchAllowed = CommandHelpers.HasFlag(args, "--runtime-switch-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            MutateRuntime = CommandHelpers.HasFlag(args, "--mutate-runtime"),
            VectorStoreBindingChanged = CommandHelpers.HasFlag(args, "--vector-store-binding-changed"),
            PackingPolicyChanged = CommandHelpers.HasFlag(args, "--packing-policy-changed"),
            PackageOutputChanged = CommandHelpers.HasFlag(args, "--package-output-changed"),
            NonAllowlistedScopeLeakCount = CommandHelpers.GetIntOption(args, "--scope-leak-count", 0),
            RiskAfterPolicy = CommandHelpers.GetIntOption(args, "--risk-after-policy", 0),
            FormalOutputChanged = CommandHelpers.GetIntOption(args, "--formal-output-changed", 0)
        };

        var runner = new ScopedRuntimeExperimentActivationPreflightRunner();
        var report = string.Equals(subcommand, "vector-scoped-runtime-experiment-activation-gate", StringComparison.OrdinalIgnoreCase)
            ? runner.BuildGate(
                foundationGate,
                serviceFreeze,
                vectorFormalFreeze,
                effectivePlan,
                approvalGate,
                runtimeChangeGate,
                existingPreflight,
                existingRoute,
                options)
            : string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-route", StringComparison.OrdinalIgnoreCase)
                ? runner.BuildDryRunRoute(
                    foundationGate,
                    serviceFreeze,
                    vectorFormalFreeze,
                    effectivePlan,
                    approvalGate,
                    runtimeChangeGate,
                    options)
                : runner.BuildPreflight(
                    foundationGate,
                    serviceFreeze,
                    vectorFormalFreeze,
                    effectivePlan,
                    approvalGate,
                    runtimeChangeGate,
                    options);

        var fileName = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-dry-run-route", StringComparison.OrdinalIgnoreCase)
                => "dry-run-route-report",
            var value when string.Equals(value, "vector-scoped-runtime-experiment-activation-gate", StringComparison.OrdinalIgnoreCase)
                => "activation-gate",
            _ => "activation-preflight"
        };
        var title = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-dry-run-route", StringComparison.OrdinalIgnoreCase)
                => "Vector Scoped Runtime Experiment Dry-run Route",
            var value when string.Equals(value, "vector-scoped-runtime-experiment-activation-gate", StringComparison.OrdinalIgnoreCase)
                => "Vector Scoped Runtime Experiment Activation Gate",
            _ => "Vector Scoped Runtime Experiment Activation Preflight"
        };

        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentActivationPreflightRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run-route", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(
                    ScopedRuntimeExperimentActivationPreflightRunner.BuildTraceJsonl(report),
                    Path.Combine(outputDirectory, "dry-run-route-traces.jsonl"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector scoped runtime experiment activation artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] proposalId={report.ProposalId}; approvalId={report.ApprovalId}; passed={report.PreflightPassed}; routeDryRun={report.RuntimeRouteDryRunExecuted}; runtimeMutated={report.RuntimeMutated}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteGuardedScopedRuntimeExperimentAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var activationGate = await ReadJsonFileAsync<ScopedRuntimeExperimentActivationPreflightReport>(
                Path.Combine("vector", "v4", "runtime-experiment", "activation-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var approvalGate = await ReadJsonFileAsync<ScopedRuntimeExperimentApprovalGateReport>(
                Path.Combine("vector", "v4", "runtime-experiment", "runtime-approval-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(
                Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var existingExperiment = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentReport>(
                Path.Combine(outputDirectory, "guarded-runtime-experiment-report.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var existingObservation = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentReport>(
                Path.Combine(outputDirectory, "guarded-runtime-experiment-observation.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var rollbackSmoke = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentReport>(
                Path.Combine(outputDirectory, "guarded-runtime-experiment-rollback-smoke.json"),
                cancellationToken)
            .ConfigureAwait(false);

        if (CommandHelpers.HasFlag(args, "--missing-activation-gate"))
        {
            activationGate = null;
        }

        if (CommandHelpers.HasFlag(args, "--missing-approval"))
        {
            approvalGate = null;
        }

        var selectedScope = activationGate?.SelectedScopes.FirstOrDefault() ?? "contextcore_eval/dataset-v2-stress/dataset-v2-stress";
        var selectedParts = selectedScope.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var workspaceAllowlist = ResolveAllowlist(args, "--workspace", "--workspace-allowlist", selectedParts.Length > 0 ? selectedParts[0] : "contextcore_eval");
        var collectionAllowlist = ResolveAllowlist(args, "--collection", "--collection-allowlist", selectedParts.Length > 1 ? selectedParts[1] : "dataset-v2-stress");
        var evalScopeAllowlist = ResolveAllowlist(args, "--eval-scope", "--eval-scope-allowlist", selectedParts.Length > 2 ? selectedParts[2] : "dataset-v2-stress");
        if (CommandHelpers.HasFlag(args, "--missing-scope"))
        {
            workspaceAllowlist = Array.Empty<string>();
            collectionAllowlist = Array.Empty<string>();
            evalScopeAllowlist = Array.Empty<string>();
        }

        var options = new GuardedScopedRuntimeExperimentOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            Mode = CommandHelpers.GetOption(args, "--mode") ?? GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment,
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? activationGate?.ProposalId ?? approvalGate?.ProposalId ?? string.Empty,
            ApprovalId = CommandHelpers.GetOption(args, "--approval-id") ?? activationGate?.ApprovalId ?? approvalGate?.ApprovalId ?? string.Empty,
            WorkspaceAllowlist = workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist,
            ProfileName = CommandHelpers.GetOption(args, "--profile") ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            MaxRequestCount = CommandHelpers.GetIntOption(args, "--max-request-count", 120),
            MaxDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 30),
            MaxErrorCount = CommandHelpers.GetIntOption(args, "--max-error-count", 0),
            RequireV413PreflightPassed = !CommandHelpers.HasFlag(args, "--skip-v413-gate"),
            RequireScopedRuntimeExperimentApproval = !CommandHelpers.HasFlag(args, "--skip-approval-gate"),
            RequireKillSwitch = !CommandHelpers.HasFlag(args, "--skip-kill-switch-requirement"),
            RequireRollbackPlan = !CommandHelpers.HasFlag(args, "--skip-rollback-requirement"),
            RequireTraceSink = !CommandHelpers.HasFlag(args, "--skip-trace-sink-requirement"),
            TraceSinkAvailable = !CommandHelpers.HasFlag(args, "--missing-trace-sink"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            MutateFormalOutput = CommandHelpers.HasFlag(args, "--mutate-formal-output"),
            MutatePackingPolicy = CommandHelpers.HasFlag(args, "--mutate-packing-policy") || CommandHelpers.HasFlag(args, "--packing-policy-changed"),
            GlobalDefaultOn = CommandHelpers.HasFlag(args, "--global-default-on"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            RuntimeSwitchAllowed = CommandHelpers.HasFlag(args, "--runtime-switch-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch"),
            RuntimeMutated = CommandHelpers.HasFlag(args, "--runtime-mutated") || CommandHelpers.HasFlag(args, "--mutate-runtime"),
            VectorStoreBindingChanged = CommandHelpers.HasFlag(args, "--vector-store-binding-changed"),
            PackageOutputChanged = CommandHelpers.HasFlag(args, "--package-output-changed"),
            KillSwitchTriggered = CommandHelpers.HasFlag(args, "--kill-switch-triggered"),
            RollbackVerified = !CommandHelpers.HasFlag(args, "--rollback-failed"),
            NonAllowlistedScopeLeakCount = CommandHelpers.GetIntOption(args, "--scope-leak-count", 0),
            RiskAfterPolicy = CommandHelpers.GetIntOption(args, "--risk-after-policy", 0),
            MustNotHitRiskAfterPolicy = CommandHelpers.GetIntOption(args, "--must-not-risk-after-policy", 0),
            LifecycleRiskAfterPolicy = CommandHelpers.GetIntOption(args, "--lifecycle-risk-after-policy", 0),
            FormalOutputChanged = CommandHelpers.GetIntOption(args, "--formal-output-changed", 0),
            ErrorCount = CommandHelpers.GetIntOption(args, "--error-count", 0)
        };

        var runner = new GuardedScopedRuntimeExperimentRunner();
        var report = subcommand switch
        {
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-observation", StringComparison.OrdinalIgnoreCase)
                => runner.BuildObservation(activationGate, approvalGate, runtimeChangeGate, options),
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-rollback-smoke", StringComparison.OrdinalIgnoreCase)
                => runner.BuildRollbackSmoke(activationGate, approvalGate, runtimeChangeGate, options),
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase)
                => runner.BuildGate(activationGate, approvalGate, runtimeChangeGate, existingExperiment, existingObservation, rollbackSmoke, options),
            _ => runner.BuildExperiment(activationGate, approvalGate, runtimeChangeGate, options)
        };

        var fileName = subcommand switch
        {
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-observation", StringComparison.OrdinalIgnoreCase)
                => "guarded-runtime-experiment-observation",
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-rollback-smoke", StringComparison.OrdinalIgnoreCase)
                => "guarded-runtime-experiment-rollback-smoke",
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase)
                => "guarded-runtime-experiment-gate",
            _ => "guarded-runtime-experiment-report"
        };
        var title = subcommand switch
        {
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-observation", StringComparison.OrdinalIgnoreCase)
                => "Vector Guarded Scoped Runtime Experiment Observation",
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-rollback-smoke", StringComparison.OrdinalIgnoreCase)
                => "Vector Guarded Scoped Runtime Experiment Rollback Smoke",
            var value when string.Equals(value, "vector-guarded-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase)
                => "Vector Guarded Scoped Runtime Experiment Gate",
            _ => "Vector Guarded Scoped Runtime Experiment"
        };

        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(GuardedScopedRuntimeExperimentRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(subcommand, "vector-guarded-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(
                    GuardedScopedRuntimeExperimentRunner.BuildTraceJsonl(report),
                    Path.Combine(outputDirectory, "guarded-runtime-experiment-traces.jsonl"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Guarded scoped runtime experiment artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.ExperimentPassed}; requests={report.RequestCount}; routeHits={report.ExperimentRouteHitCount}; risk={report.RiskAfterPolicy}; runtimeMutated={report.RuntimeMutated}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentObservationWindowAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var v414Gate = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentReport>(
                Path.Combine(outputDirectory, "guarded-runtime-experiment-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(
                Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var existingWindow = await ReadJsonFileAsync<ScopedRuntimeExperimentObservationWindowReport>(
                Path.Combine(outputDirectory, "observation-window.json"),
                cancellationToken)
            .ConfigureAwait(false);

        if (CommandHelpers.HasFlag(args, "--missing-v414-gate"))
        {
            v414Gate = null;
        }

        var selectedScope = v414Gate?.SelectedScopes.FirstOrDefault() ?? "contextcore_eval/dataset-v2-stress/dataset-v2-stress";
        var selectedParts = selectedScope.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var workspaceAllowlist = ResolveAllowlist(args, "--workspace", "--workspace-allowlist", selectedParts.Length > 0 ? selectedParts[0] : "contextcore_eval");
        var collectionAllowlist = ResolveAllowlist(args, "--collection", "--collection-allowlist", selectedParts.Length > 1 ? selectedParts[1] : "dataset-v2-stress");
        var evalScopeAllowlist = ResolveAllowlist(args, "--eval-scope", "--eval-scope-allowlist", selectedParts.Length > 2 ? selectedParts[2] : "dataset-v2-stress");
        if (CommandHelpers.HasFlag(args, "--missing-scope"))
        {
            workspaceAllowlist = Array.Empty<string>();
            collectionAllowlist = Array.Empty<string>();
            evalScopeAllowlist = Array.Empty<string>();
        }

        var options = new ScopedRuntimeExperimentObservationWindowOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ProposalId = CommandHelpers.GetOption(args, "--proposal-id") ?? v414Gate?.ProposalId ?? string.Empty,
            ApprovalId = CommandHelpers.GetOption(args, "--approval-id") ?? v414Gate?.ApprovalId ?? string.Empty,
            ObservationWindowId = CommandHelpers.GetOption(args, "--observation-window-id") ?? string.Empty,
            Mode = CommandHelpers.GetOption(args, "--mode") ?? ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation,
            WorkspaceAllowlist = workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist,
            MinRequestCount = CommandHelpers.GetIntOption(args, "--min-request-count", 360),
            ObservationRunCount = CommandHelpers.GetIntOption(args, "--observation-run-count", 3),
            MaxDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 30),
            MaxErrorCount = CommandHelpers.GetIntOption(args, "--max-error-count", 0),
            MaxLatencyP95Ms = CommandHelpers.GetIntOption(args, "--max-latency-p95-ms", 1_000),
            RequireV414GatePassed = !CommandHelpers.HasFlag(args, "--skip-v414-gate"),
            RequireKillSwitch = !CommandHelpers.HasFlag(args, "--skip-kill-switch-requirement"),
            RequireRollbackPlan = !CommandHelpers.HasFlag(args, "--skip-rollback-requirement"),
            RequireTraceSink = !CommandHelpers.HasFlag(args, "--skip-trace-sink-requirement"),
            TraceSinkAvailable = !CommandHelpers.HasFlag(args, "--missing-trace-sink"),
            WriteFormalPackage = CommandHelpers.HasFlag(args, "--write-formal-package"),
            MutateFormalOutput = CommandHelpers.HasFlag(args, "--mutate-formal-output"),
            MutatePackingPolicy = CommandHelpers.HasFlag(args, "--mutate-packing-policy") || CommandHelpers.HasFlag(args, "--packing-policy-changed"),
            GlobalDefaultOn = CommandHelpers.HasFlag(args, "--global-default-on"),
            UseForRuntime = CommandHelpers.HasFlag(args, "--use-for-runtime"),
            FormalRetrievalAllowed = CommandHelpers.HasFlag(args, "--formal-retrieval-allowed"),
            RuntimeSwitchAllowed = CommandHelpers.HasFlag(args, "--runtime-switch-allowed"),
            ReadyForRuntimeSwitch = CommandHelpers.HasFlag(args, "--ready-for-runtime-switch"),
            RuntimeMutated = CommandHelpers.HasFlag(args, "--runtime-mutated") || CommandHelpers.HasFlag(args, "--mutate-runtime"),
            VectorStoreBindingChanged = CommandHelpers.HasFlag(args, "--vector-store-binding-changed"),
            PackageOutputChanged = CommandHelpers.HasFlag(args, "--package-output-changed"),
            KillSwitchAvailable = !CommandHelpers.HasFlag(args, "--missing-kill-switch"),
            KillSwitchSmokePassed = !CommandHelpers.HasFlag(args, "--kill-switch-smoke-failed"),
            RollbackVerified = !CommandHelpers.HasFlag(args, "--rollback-failed"),
            NonAllowlistedScopeLeakCount = CommandHelpers.GetIntOption(args, "--scope-leak-count", 0),
            RiskAfterPolicy = CommandHelpers.GetIntOption(args, "--risk-after-policy", 0),
            MustNotHitRiskAfterPolicy = CommandHelpers.GetIntOption(args, "--must-not-risk-after-policy", 0),
            LifecycleRiskAfterPolicy = CommandHelpers.GetIntOption(args, "--lifecycle-risk-after-policy", 0),
            FormalOutputChanged = CommandHelpers.GetIntOption(args, "--formal-output-changed", 0),
            ErrorCount = CommandHelpers.GetIntOption(args, "--error-count", 0),
            LatencyP50 = CommandHelpers.GetOption(args, "--latency-p50-ms") is not null
                ? CommandHelpers.GetIntOption(args, "--latency-p50-ms", 8)
                : null,
            LatencyP95 = CommandHelpers.GetOption(args, "--latency-p95-ms") is not null
                ? CommandHelpers.GetIntOption(args, "--latency-p95-ms", 12)
                : null,
            TraceCompleteness = CommandHelpers.GetOption(args, "--trace-completeness") is not null
                ? GetDoubleOption(args, "--trace-completeness") ?? 100
                : CommandHelpers.HasFlag(args, "--missing-trace") ? 99 : 100
        };

        var runner = new ScopedRuntimeExperimentObservationWindowRunner();
        var report = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-observation-window-summary", StringComparison.OrdinalIgnoreCase)
                => runner.BuildSummary(v414Gate, runtimeChangeGate, existingWindow, options),
            var value when string.Equals(value, "vector-scoped-runtime-experiment-observation-window-gate", StringComparison.OrdinalIgnoreCase)
                => runner.BuildGate(v414Gate, runtimeChangeGate, existingWindow, options),
            _ => runner.BuildWindow(v414Gate, runtimeChangeGate, options)
        };

        var fileName = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-observation-window-summary", StringComparison.OrdinalIgnoreCase)
                => "observation-window-summary",
            var value when string.Equals(value, "vector-scoped-runtime-experiment-observation-window-gate", StringComparison.OrdinalIgnoreCase)
                => "observation-window-gate",
            _ => "observation-window"
        };
        var title = subcommand switch
        {
            var value when string.Equals(value, "vector-scoped-runtime-experiment-observation-window-summary", StringComparison.OrdinalIgnoreCase)
                => "Vector Scoped Runtime Experiment Observation Window Summary",
            var value when string.Equals(value, "vector-scoped-runtime-experiment-observation-window-gate", StringComparison.OrdinalIgnoreCase)
                => "Vector Scoped Runtime Experiment Observation Window Gate",
            _ => "Vector Scoped Runtime Experiment Observation Window"
        };

        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentObservationWindowRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                ScopedRuntimeExperimentObservationWindowRunner.BuildTraceJsonl(report),
                Path.Combine(outputDirectory, "observation-window-traces.jsonl"),
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime experiment observation window artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.ObservationPassed}; window={report.ObservationWindowId}; runs={report.ObservationRunCount}; requests={report.RequestCount}; hits={report.ExperimentRouteHitCount}; risk={report.RiskAfterPolicy}; trace={report.TraceCompleteness}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteScopedRuntimeExperimentObservationFreezeAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v4", "runtime-experiment"));
        Directory.CreateDirectory(outputDirectory);

        var v414GatePath = Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-gate.json");
        var v415GatePath = Path.Combine("vector", "v4", "runtime-experiment", "observation-window-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var p15A3Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15ExtendedPath = Path.Combine("eval", "eval-report-p15-extended.json");
        var v414Gate = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentReport>(v414GatePath, cancellationToken)
            .ConfigureAwait(false);
        var v415Gate = await ReadJsonFileAsync<ScopedRuntimeExperimentObservationWindowReport>(v415GatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(p15A3Path)
            && IsP15EvalReportPassed(p15ExtendedPath);
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v414GuardedScopedRuntimeExperimentGate"] = v414GatePath,
            ["v415ObservationWindowGate"] = v415GatePath,
            ["learningRuntimeChangeReadinessGate"] = runtimeGatePath,
            ["p15A3"] = p15A3Path,
            ["p15Extended"] = p15ExtendedPath
        };

        var runner = new ScopedRuntimeExperimentObservationFreezeRunner();
        var isPromotion = string.Equals(
            subcommand,
            "vector-scoped-runtime-experiment-promotion-decision",
            StringComparison.OrdinalIgnoreCase);
        var report = isPromotion
            ? runner.BuildPromotionDecision(v414Gate, v415Gate, runtimeGate, p15Passed, sourceReports)
            : runner.BuildObservationFreeze(v414Gate, v415Gate, runtimeGate, p15Passed, sourceReports);
        var fileName = isPromotion ? "promotion-decision" : "observation-freeze";
        var title = isPromotion
            ? "Vector Scoped Runtime Experiment Promotion Decision"
            : "Vector Scoped Runtime Experiment Observation Freeze";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimeExperimentObservationFreezeRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime experiment observation freeze artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; decision={report.PromotionDecision}; requests={report.RequestCount}; hits={report.ExperimentRouteHitCount}; risk={report.RiskAfterPolicy}; trace={report.TraceCompleteness}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}");
    }

    private static async Task ExecuteVectorLifecycleMetadataReviewBatchAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var candidateStore = new FileVectorLifecycleMetadataReviewCandidateStore(new FileStorageOptions());
        var batchService = new VectorLifecycleMetadataReviewBatchService();

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorLifecycleMetadataReviewBatchImportSmokeAsync(batchService, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-create", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = await candidateStore.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Status = VectorLifecycleMetadataReviewCandidateStatuses.PendingReview,
                Limit = CommandHelpers.GetIntOption(args, "--limit", 1000)
            }, cancellationToken).ConfigureAwait(false);
            var createdBatch = batchService.CreateBatch(
                workspaceId,
                collectionId,
                candidates,
                CommandHelpers.GetOption(args, "--created-by") ?? "local-eval",
                CommandHelpers.GetOption(args, "--instructions") ?? string.Empty);
            await WriteBatchAsync(createdBatch, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector lifecycle metadata review batch created: {GetReviewBatchDirectory(createdBatch.BatchId)}");
            Console.WriteLine($"[Eval] batchId={createdBatch.BatchId}; candidates={createdBatch.CandidateCount}; status={createdBatch.Status}; recommendation=ReadyForManualReview");
            return;
        }

        var batch = await LoadReviewBatchAsync(CommandHelpers.GetOption(args, "--batch-id"), cancellationToken)
            .ConfigureAwait(false);
        var batchDirectory = GetReviewBatchDirectory(batch.BatchId);
        var candidatesForBatch = await LoadReviewBatchCandidatesAsync(candidateStore, batch, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-export", StringComparison.OrdinalIgnoreCase))
        {
            var rows = batchService.ExportReviewSheet(batch, candidatesForBatch);
            await WriteReviewSheetAsync(batch.BatchId, rows, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(
                VectorLifecycleMetadataReviewBatchService.BuildReviewSheetMarkdown(
                    VectorLifecycleMetadataReviewBatchService.WithStatus(batch, VectorLifecycleMetadataReviewBatchStatuses.Exported),
                    rows),
                Path.Combine(batchDirectory, "review-sheet.md"),
                cancellationToken).ConfigureAwait(false);
            await WriteBatchAsync(VectorLifecycleMetadataReviewBatchService.WithStatus(batch, VectorLifecycleMetadataReviewBatchStatuses.Exported), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector lifecycle metadata review batch exported: {Path.Combine(batchDirectory, "review-sheet.jsonl")}");
            Console.WriteLine($"[Eval] batchId={batch.BatchId}; rows={rows.Count}; recommendation=ReadyForManualReview");
            return;
        }

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-import", StringComparison.OrdinalIgnoreCase))
        {
            var input = CommandHelpers.GetOption(args, "--input") ?? Path.Combine(batchDirectory, "review-sheet.jsonl");
            var rows = await ReadReviewSheetRowsAsync(input, cancellationToken).ConfigureAwait(false);
            await WriteReviewSheetAsync(batch.BatchId, rows, cancellationToken).ConfigureAwait(false);
            var result = batchService.BuildImportResult(batch.BatchId, rows);
            await WriteTextAsync(JsonSerializer.Serialize(result, JsonOptions), Path.Combine(batchDirectory, "import-result.json"), cancellationToken)
                .ConfigureAwait(false);
            await WriteBatchAsync(VectorLifecycleMetadataReviewBatchService.WithStatus(batch, VectorLifecycleMetadataReviewBatchStatuses.Imported), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector lifecycle metadata review batch imported: {Path.Combine(batchDirectory, "import-result.json")}");
            Console.WriteLine($"[Eval] batchId={batch.BatchId}; rows={result.RowCount}; decisions={result.DecisionCount}");
            return;
        }

        var reviewSheetPath = Path.Combine(batchDirectory, "review-sheet.jsonl");
        var reviewRows = File.Exists(reviewSheetPath)
            ? await ReadReviewSheetRowsAsync(reviewSheetPath, cancellationToken).ConfigureAwait(false)
            : batchService.ExportReviewSheet(batch, candidatesForBatch);
        var validation = batchService.Validate(batch, candidatesForBatch, reviewRows);

        if (string.Equals(subcommand, "vector-lifecycle-metadata-review-batch-validate", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(JsonSerializer.Serialize(validation, JsonOptions), Path.Combine(batchDirectory, "validation-report.json"), cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(VectorLifecycleMetadataReviewBatchService.BuildValidationMarkdown(validation), Path.Combine(batchDirectory, "validation-report.md"), cancellationToken)
                .ConfigureAwait(false);
            await WriteBatchAsync(VectorLifecycleMetadataReviewBatchService.WithStatus(batch, VectorLifecycleMetadataReviewBatchStatuses.Validated), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector lifecycle metadata review batch validation written: {Path.Combine(batchDirectory, "validation-report.json")}");
            Console.WriteLine($"[Eval] batchId={batch.BatchId}; decisions={validation.DecisionCount}; errors={validation.ValidationErrorCount}; recommendation={validation.Recommendation}");
            return;
        }

        var preview = batchService.BuildApplyPreview(batch, candidatesForBatch, reviewRows, validation);
        await WriteTextAsync(JsonSerializer.Serialize(preview, JsonOptions), Path.Combine(batchDirectory, "apply-preview.json"), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataReviewBatchService.BuildApplyPreviewMarkdown(preview), Path.Combine(batchDirectory, "apply-preview.md"), cancellationToken)
            .ConfigureAwait(false);
        await WriteBatchAsync(VectorLifecycleMetadataReviewBatchService.WithStatus(batch, VectorLifecycleMetadataReviewBatchStatuses.AppliedPreview), cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector lifecycle metadata review batch apply preview written: {Path.Combine(batchDirectory, "apply-preview.json")}");
        Console.WriteLine($"[Eval] batchId={batch.BatchId}; wouldWriteSidecar={preview.WouldWriteSidecarEntryCount}; unsafe={preview.UnsafeBlockedCount}; recommendation={preview.Recommendation}");
    }

    private static async Task ExecuteVectorLifecycleMetadataReviewBatchImportSmokeAsync(
        VectorLifecycleMetadataReviewBatchService batchService,
        CancellationToken cancellationToken)
    {
        const string workspaceId = "__vector_review_batch_import_smoke__";
        const string collectionId = "lifecycle-metadata-review-batch-import-smoke";
        const string batchId = "import-smoke";

        var smokeDirectory = GetReviewBatchDirectory(batchId);
        Directory.CreateDirectory(smokeDirectory);

        var candidates = new[]
        {
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-approve", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-reject", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-needs-evidence", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-supersede", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-invalid-decision", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-missing-reviewer", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-missing-reason", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-missing-evidence", "Unknown", "Active", VectorQueryTargetSections.AuditContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-unsafe-normal", "Deprecated", "Active", VectorQueryTargetSections.NormalContext),
            CreateSmokeReviewCandidate(workspaceId, collectionId, "batch-duplicate", "Unknown", "Active", VectorQueryTargetSections.AuditContext)
        };
        var candidateStore = new FileVectorLifecycleMetadataReviewCandidateStore(new FileStorageOptions());
        foreach (var candidate in candidates)
        {
            await candidateStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var batch = new VectorLifecycleMetadataReviewBatch
        {
            BatchId = batchId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CandidateIds = candidates.Select(static item => item.CandidateId).ToArray(),
            CandidateCount = candidates.Length,
            Status = VectorLifecycleMetadataReviewBatchStatuses.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "import-smoke",
            ReviewInstructions = "Synthetic import smoke batch. Do not use for real review.",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "vector-lifecycle-metadata-review-batch-import-smoke/v1",
                ["synthetic"] = bool.TrueString,
                ["realSidecarWrite"] = bool.FalseString,
                ["formalRetrievalAllowed"] = bool.FalseString
            }
        };
        var exportedBatch = VectorLifecycleMetadataReviewBatchService.WithStatus(batch, VectorLifecycleMetadataReviewBatchStatuses.Exported);
        var baseRows = batchService.ExportReviewSheet(exportedBatch, candidates);
        var rows = BuildImportSmokeRows(baseRows);
        await WriteTextAsync(JsonSerializer.Serialize(exportedBatch, JsonOptions), Path.Combine(smokeDirectory, "batch.json"), cancellationToken)
            .ConfigureAwait(false);
        await WriteReviewSheetAsync(batchId, rows, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
            VectorLifecycleMetadataReviewBatchService.BuildReviewSheetMarkdown(exportedBatch, rows),
            Path.Combine(smokeDirectory, "review-sheet.md"),
            cancellationToken).ConfigureAwait(false);

        var importedRows = await ReadReviewSheetRowsAsync(Path.Combine(smokeDirectory, "review-sheet.jsonl"), cancellationToken)
            .ConfigureAwait(false);
        var importResult = batchService.BuildImportResult(batchId, importedRows);
        await WriteTextAsync(JsonSerializer.Serialize(importResult, JsonOptions), Path.Combine(smokeDirectory, "import-result.json"), cancellationToken)
            .ConfigureAwait(false);
        var importedBatch = VectorLifecycleMetadataReviewBatchService.WithStatus(exportedBatch, VectorLifecycleMetadataReviewBatchStatuses.Imported);
        await WriteTextAsync(JsonSerializer.Serialize(importedBatch, JsonOptions), Path.Combine(smokeDirectory, "batch.json"), cancellationToken)
            .ConfigureAwait(false);

        var validation = batchService.Validate(importedBatch, candidates, importedRows);
        await WriteTextAsync(JsonSerializer.Serialize(validation, JsonOptions), Path.Combine(smokeDirectory, "validation-report.json"), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataReviewBatchService.BuildValidationMarkdown(validation), Path.Combine(smokeDirectory, "validation-report.md"), cancellationToken)
            .ConfigureAwait(false);
        var validatedBatch = VectorLifecycleMetadataReviewBatchService.WithStatus(importedBatch, VectorLifecycleMetadataReviewBatchStatuses.Validated);
        await WriteTextAsync(JsonSerializer.Serialize(validatedBatch, JsonOptions), Path.Combine(smokeDirectory, "batch.json"), cancellationToken)
            .ConfigureAwait(false);

        var preview = batchService.BuildApplyPreview(validatedBatch, candidates, importedRows, validation);
        await WriteTextAsync(JsonSerializer.Serialize(preview, JsonOptions), Path.Combine(smokeDirectory, "apply-preview.json"), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataReviewBatchService.BuildApplyPreviewMarkdown(preview), Path.Combine(smokeDirectory, "apply-preview.md"), cancellationToken)
            .ConfigureAwait(false);

        var issueCandidates = validation.Issues
            .Select(static item => item.CandidateId)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validDecisionCount = importedRows
            .Where(static item => !string.IsNullOrWhiteSpace(item.ReviewerDecision))
            .Count(item => !issueCandidates.Contains(item.CandidateId));
        var invalidDecisionCount = validation.Issues.Count;
        var duplicateCount = CountValidationIssues(validation, "DuplicateCandidateDecision");
        var unknownCount = CountValidationIssues(validation, "UnknownDecision");
        var missingReviewerCount = CountValidationIssues(validation, "MissingReviewer");
        var missingReasonCount = CountValidationIssues(validation, "MissingReviewerReason");
        var missingEvidenceCount = CountValidationIssues(validation, "MissingEvidenceOrSourceRefs");
        var unsafeCount = CountValidationIssues(validation, "UnsafeNormalContextApproval");
        var sourceItemUnchanged = true;
        var actualSidecarWriteCount = 0;
        var smokePassed = importResult.RowCount == importedRows.Count
                          && validDecisionCount == 4
                          && duplicateCount == 1
                          && unknownCount == 1
                          && missingReviewerCount == 1
                          && missingReasonCount == 1
                          && missingEvidenceCount == 1
                          && unsafeCount == 1
                          && invalidDecisionCount == 6
                          && preview.WouldWriteSidecarEntryCount == 1
                          && actualSidecarWriteCount == 0
                          && sourceItemUnchanged
                          && !preview.FormalRetrievalAllowed
                          && !preview.UseForRuntime
                          && string.Equals(exportedBatch.Status, VectorLifecycleMetadataReviewBatchStatuses.Exported, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(importedBatch.Status, VectorLifecycleMetadataReviewBatchStatuses.Imported, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(validatedBatch.Status, VectorLifecycleMetadataReviewBatchStatuses.Validated, StringComparison.OrdinalIgnoreCase);
        var report = new VectorLifecycleMetadataReviewBatchImportSmokeReport
        {
            OperationId = $"vector-lifecycle-metadata-review-batch-import-smoke-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            SmokePassed = smokePassed,
            BatchId = batchId,
            ImportedRowCount = importResult.RowCount,
            ValidDecisionCount = validDecisionCount,
            InvalidDecisionCount = invalidDecisionCount,
            DuplicateDecisionBlockedCount = duplicateCount,
            UnknownDecisionBlockedCount = unknownCount,
            MissingReviewerBlockedCount = missingReviewerCount,
            MissingReasonBlockedCount = missingReasonCount,
            MissingEvidenceBlockedCount = missingEvidenceCount,
            UnsafeNormalContextBlockedCount = unsafeCount,
            WouldWriteSidecarCount = preview.WouldWriteSidecarEntryCount,
            ActualSidecarWriteCount = actualSidecarWriteCount,
            SourceItemUnchanged = sourceItemUnchanged,
            FormalRetrievalAllowed = preview.FormalRetrievalAllowed,
            UseForRuntime = preview.UseForRuntime,
            InitialStatus = batch.Status,
            ExportedStatus = exportedBatch.Status,
            ImportedStatus = importedBatch.Status,
            ValidatedStatus = validatedBatch.Status,
            ValidationRecommendation = validation.Recommendation,
            ApplyPreviewRecommendation = preview.Recommendation,
            Recommendation = smokePassed ? "ReadyForManualReviewInput" : ResolveImportSmokeRecommendation(validation, preview),
            Diagnostics = validation.Issues.Select(static item => $"{item.CandidateId}:{item.Reason}").ToArray()
        };

        var reportPath = Path.Combine(smokeDirectory, "import-smoke-report.json");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), reportPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
            VectorLifecycleMetadataReviewBatchService.BuildImportSmokeMarkdown(report),
            Path.Combine(smokeDirectory, "import-smoke-report.md"),
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector lifecycle metadata review batch import smoke written: {reportPath}");
        Console.WriteLine($"[Eval] passed={report.SmokePassed}; rows={report.ImportedRowCount}; valid={report.ValidDecisionCount}; invalid={report.InvalidDecisionCount}; wouldWriteSidecar={report.WouldWriteSidecarCount}; recommendation={report.Recommendation}");
    }

    private static IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> BuildImportSmokeRows(
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows)
    {
        var byItem = rows.ToDictionary(static item => item.MustHitItemId, StringComparer.OrdinalIgnoreCase);
        var result = new List<VectorLifecycleMetadataReviewSheetRow>(capacity: 10)
        {
            WithReviewDecision(byItem["batch-approve"], VectorLifecycleMetadataReviewDecisions.ApproveForSidecar),
            WithReviewDecision(byItem["batch-reject"], VectorLifecycleMetadataReviewDecisions.Reject),
            WithReviewDecision(byItem["batch-needs-evidence"], VectorLifecycleMetadataReviewDecisions.NeedsEvidence),
            WithReviewDecision(byItem["batch-supersede"], VectorLifecycleMetadataReviewDecisions.Supersede),
            WithReviewDecision(byItem["batch-invalid-decision"], "NotAValidDecision"),
            WithReviewDecision(byItem["batch-missing-reviewer"], VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, reviewer: string.Empty),
            WithReviewDecision(byItem["batch-missing-reason"], VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, reason: string.Empty),
            WithReviewDecision(byItem["batch-missing-evidence"], VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, evidenceRefs: [], sourceRefs: []),
            WithReviewDecision(byItem["batch-unsafe-normal"], VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, targetSection: VectorQueryTargetSections.NormalContext),
            WithReviewDecision(byItem["batch-duplicate"], VectorLifecycleMetadataReviewDecisions.Reject),
            WithReviewDecision(byItem["batch-duplicate"], VectorLifecycleMetadataReviewDecisions.Reject, notes: "duplicate decision")
        };
        return result;
    }

    private static VectorLifecycleMetadataReviewSheetRow WithReviewDecision(
        VectorLifecycleMetadataReviewSheetRow row,
        string decision,
        string reviewer = "import-smoke-reviewer",
        string reason = "import smoke validation",
        IReadOnlyList<string>? evidenceRefs = null,
        IReadOnlyList<string>? sourceRefs = null,
        string? targetSection = null,
        string notes = "")
        => new()
        {
            CandidateId = row.CandidateId,
            MustHitItemId = row.MustHitItemId,
            CurrentLifecycle = row.CurrentLifecycle,
            ProposedLifecycle = row.ProposedLifecycle,
            CurrentTargetSection = row.CurrentTargetSection,
            ProposedTargetSection = row.ProposedTargetSection,
            EvidenceRefs = evidenceRefs?.ToArray() ?? row.EvidenceRefs.ToArray(),
            SourceRefs = sourceRefs?.ToArray() ?? row.SourceRefs.ToArray(),
            RepairReason = row.RepairReason,
            ReviewerDecision = decision,
            ReviewerReason = reason,
            Reviewer = reviewer,
            TargetSectionOverride = targetSection ?? row.TargetSectionOverride,
            Notes = notes
        };

    private static int CountValidationIssues(
        VectorLifecycleMetadataReviewBatchValidationReport validation,
        string reason)
    {
        return validation.Issues.Count(issue => string.Equals(issue.Reason, reason, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveImportSmokeRecommendation(
        VectorLifecycleMetadataReviewBatchValidationReport validation,
        VectorLifecycleMetadataReviewBatchApplyPreviewReport preview)
    {
        if (validation.UnsafeDecisionCount == 0 || preview.UnsafeBlockedCount == 0)
        {
            return "BlockedByUnsafeDecisionHandling";
        }

        return "BlockedByImportValidationBug";
    }

    private static async Task ExecuteVectorLifecycleMetadataReviewSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-review-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? EligibilityOutputPath("vector-lifecycle-metadata-review-smoke-report.md");
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var reviewStore = new InMemoryVectorLifecycleMetadataReviewStore();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var reviewService = new VectorLifecycleMetadataReviewService(candidateStore, reviewStore, sidecarStore);
        var workspaceId = "__vector_review_smoke__";
        var collectionId = "lifecycle-metadata-review-smoke";

        var approveCandidate = CreateSmokeReviewCandidate(workspaceId, collectionId, "smoke-item-approve", "Unknown", "Active", VectorQueryTargetSections.AuditContext);
        var rejectCandidate = CreateSmokeReviewCandidate(workspaceId, collectionId, "smoke-item-reject", "Unknown", "Active", VectorQueryTargetSections.AuditContext);
        var needsEvidenceCandidate = CreateSmokeReviewCandidate(workspaceId, collectionId, "smoke-item-needs-evidence", "Unknown", "Active", VectorQueryTargetSections.AuditContext);
        var supersedeCandidate = CreateSmokeReviewCandidate(workspaceId, collectionId, "smoke-item-supersede", "Unknown", "Active", VectorQueryTargetSections.AuditContext);
        var deprecatedCandidate = CreateSmokeReviewCandidate(workspaceId, collectionId, "smoke-item-deprecated", "Deprecated", "Active", VectorQueryTargetSections.NormalContext);

        foreach (var candidate in new[] { approveCandidate, rejectCandidate, needsEvidenceCandidate, supersedeCandidate, deprecatedCandidate })
        {
            await candidateStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var approve = await reviewService.ReviewAsync(CreateSmokeReviewRequest(
            approveCandidate,
            VectorLifecycleMetadataReviewDecisions.ApproveForSidecar,
            confirmed: true), cancellationToken).ConfigureAwait(false);
        var reject = await reviewService.ReviewAsync(CreateSmokeReviewRequest(
            rejectCandidate,
            VectorLifecycleMetadataReviewDecisions.Reject,
            confirmed: false), cancellationToken).ConfigureAwait(false);
        var needsEvidence = await reviewService.ReviewAsync(CreateSmokeReviewRequest(
            needsEvidenceCandidate,
            VectorLifecycleMetadataReviewDecisions.NeedsEvidence,
            confirmed: false), cancellationToken).ConfigureAwait(false);
        var supersede = await reviewService.ReviewAsync(CreateSmokeReviewRequest(
            supersedeCandidate,
            VectorLifecycleMetadataReviewDecisions.Supersede,
            confirmed: false), cancellationToken).ConfigureAwait(false);
        var blocked = await reviewService.ReviewAsync(CreateSmokeReviewRequest(
            deprecatedCandidate,
            VectorLifecycleMetadataReviewDecisions.ApproveForSidecar,
            confirmed: true,
            proposedTargetSection: VectorQueryTargetSections.NormalContext), cancellationToken).ConfigureAwait(false);

        var sidecars = await reviewService.ListSidecarAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        var report = new VectorLifecycleMetadataReviewSmokeReport
        {
            OperationId = $"vector-lifecycle-metadata-review-smoke-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ApprovedSidecarWritten = approve.SidecarWritten,
            RejectSkippedSidecar = reject.Succeeded && !reject.SidecarWritten,
            NeedsEvidenceSkippedSidecar = needsEvidence.Succeeded && !needsEvidence.SidecarWritten,
            SupersedeSkippedSidecar = supersede.Succeeded && !supersede.SidecarWritten,
            SourceItemUnchanged = approve.SourceItemUnchanged && reject.SourceItemUnchanged && needsEvidence.SourceItemUnchanged && supersede.SourceItemUnchanged && blocked.SourceItemUnchanged,
            UnsafeNormalContextApprovalBlocked = blocked.UnsafeApprovalBlocked && !blocked.SidecarWritten,
            CleanupPerformed = true,
            SidecarEntryCount = sidecars.Count,
            Recommendation = approve.SidecarWritten
                && reject.Succeeded
                && needsEvidence.Succeeded
                && supersede.Succeeded
                && blocked.UnsafeApprovalBlocked
                && sidecars.Count == 1
                    ? "ReviewSmokePassed"
                    : "ReviewSmokeFailed",
            Diagnostics = [.. new[] { approve, reject, needsEvidence, supersede, blocked }
                .SelectMany(static item => item.Diagnostics)]
        };

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorLifecycleMetadataReviewService.BuildMarkdownSmoke(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector lifecycle metadata review smoke written: {outputPath}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; sidecar={report.SidecarEntryCount}; unsafeBlocked={report.UnsafeNormalContextApprovalBlocked}; sourceUnchanged={report.SourceItemUnchanged}");
    }

    private static async Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> LoadReviewBatchCandidatesAsync(
        FileVectorLifecycleMetadataReviewCandidateStore candidateStore,
        VectorLifecycleMetadataReviewBatch batch,
        CancellationToken cancellationToken)
    {
        var candidates = await candidateStore.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
        {
            WorkspaceId = batch.WorkspaceId,
            CollectionId = batch.CollectionId,
            Limit = Math.Max(batch.CandidateCount, 1000)
        }, cancellationToken).ConfigureAwait(false);
        var allowed = batch.CandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(candidate => allowed.Contains(candidate.CandidateId))
            .OrderBy(candidate => Array.IndexOf(batch.CandidateIds.ToArray(), candidate.CandidateId))
            .ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot>> BuildVectorLifecycleMetadataEvidenceSnapshotsAsync(
        FileStorageOptions storageOptions,
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var builders = candidates
            .Select(static candidate => candidate.MustHitItemId)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static item => item, static item => new VectorLifecycleMetadataEvidenceSnapshotBuilder(item), StringComparer.OrdinalIgnoreCase);
        if (builders.Count == 0)
        {
            return new Dictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        await MergeVectorEvidenceSnapshotsAsync(storageOptions, workspaceId, collectionId, builders, cancellationToken)
            .ConfigureAwait(false);
        await MergeRelationEvidenceSnapshotsAsync(storageOptions, workspaceId, collectionId, builders, cancellationToken)
            .ConfigureAwait(false);
        MergeCandidateEvidenceSnapshots(candidates, builders);

        return builders.ToDictionary(
            static item => item.Key,
            static item => item.Value.ToSnapshot(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task MergeVectorEvidenceSnapshotsAsync(
        FileStorageOptions storageOptions,
        string workspaceId,
        string collectionId,
        Dictionary<string, VectorLifecycleMetadataEvidenceSnapshotBuilder> builders,
        CancellationToken cancellationToken)
    {
        var vectorStore = new FileVectorStore(storageOptions);
        IReadOnlyList<VectorSearchResult> results;
        try
        {
            results = await vectorStore.SearchAsync(new VectorQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TopK = Math.Max(builders.Count * 4, 1024),
                IncludeVector = false
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return;
        }

        foreach (var result in results)
        {
            var record = result.Record;
            if (string.IsNullOrWhiteSpace(record.SourceId)
                || !builders.TryGetValue(record.SourceId, out var builder))
            {
                continue;
            }

            builder.SourceKind = FirstNonEmpty(builder.SourceKind, record.SourceKind);
            builder.ItemKind = FirstNonEmpty(builder.ItemKind, GetMetadataValue(record.Metadata, "itemKind", "kind", "type"));
            builder.Lifecycle = FirstNonEmpty(builder.Lifecycle, GetMetadataValue(record.Metadata, "lifecycle", "lifecycleState"));
            builder.ReviewStatus = FirstNonEmpty(builder.ReviewStatus, GetMetadataValue(record.Metadata, "reviewStatus", "status"));
            builder.ReplacementState = FirstNonEmpty(builder.ReplacementState, GetMetadataValue(record.Metadata, "replacementState", "replacementStatus"));
            builder.ProvenanceRecordId = FirstNonEmpty(builder.ProvenanceRecordId, GetMetadataValue(record.Metadata, "provenanceRecordId", "provenanceId", "provenance"));
            builder.SourceFingerprint = FirstNonEmpty(builder.SourceFingerprint, record.ContentHash, GetMetadataValue(record.Metadata, "sourceFingerprint", "fingerprint", "contentHash"));
            AddMetadata(builder.OriginalCorpusMetadata, record.Metadata);
            AddRefs(builder.SourceRefs, GetMetadataRefs(record.Metadata, "sourceRefs", "sourceRef", "refs"));
            AddRefs(builder.EvidenceRefs, GetMetadataRefs(record.Metadata, "evidenceRefs", "evidenceRef"));
        }
    }

    private static async Task MergeRelationEvidenceSnapshotsAsync(
        FileStorageOptions storageOptions,
        string workspaceId,
        string collectionId,
        Dictionary<string, VectorLifecycleMetadataEvidenceSnapshotBuilder> builders,
        CancellationToken cancellationToken)
    {
        var paths = new FilePathResolver(storageOptions);
        var serializer = new FileFormatSerializer();
        var relationStore = new FileRelationStore(paths, serializer);
        var reviewStore = new FileRelationReviewStore(paths, serializer);

        foreach (var pair in builders)
        {
            IReadOnlyList<ContextRelation> relations;
            try
            {
                relations = await relationStore.QueryForItemAsync(workspaceId, collectionId, pair.Key, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var relation in relations)
            {
                pair.Value.RelationEvidenceRefs.Add($"relation:{relation.Id}");
                AddRefs(pair.Value.SourceRefs, relation.SourceRefs);
                AddRefs(pair.Value.RelationEvidenceRefs, GetMetadataRefs(relation.Metadata, "evidenceRefs", "evidenceRef"));
                AddRefs(pair.Value.SourceRefs, GetMetadataRefs(relation.Metadata, "sourceRefs", "sourceRef", "refs"));
                pair.Value.ReplacementState = FirstNonEmpty(
                    pair.Value.ReplacementState,
                    ResolveRelationReplacementState(relation));
                AddMetadata(pair.Value.Metadata, relation.Metadata, "relation.");

                var reviews = await reviewStore.QueryReviewsAsync(relation.Id, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var review in reviews)
                {
                    pair.Value.ReviewEvidenceRefs.Add($"relation-review:{review.ReviewId}");
                    AddRefs(pair.Value.ReviewEvidenceRefs, review.EvidenceRefs);
                    AddRefs(pair.Value.SourceRefs, review.SourceRefs);
                    pair.Value.ReviewStatus = FirstNonEmpty(pair.Value.ReviewStatus, review.ToReviewStatus, review.FromReviewStatus);
                    AddMetadata(pair.Value.Metadata, review.Metadata, "relationReview.");
                }
            }
        }
    }

    private static void MergeCandidateEvidenceSnapshots(
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        Dictionary<string, VectorLifecycleMetadataEvidenceSnapshotBuilder> builders)
    {
        foreach (var candidate in candidates)
        {
            if (!builders.TryGetValue(candidate.MustHitItemId, out var builder))
            {
                continue;
            }

            builder.ItemKind = FirstNonEmpty(builder.ItemKind, candidate.ItemKind);
            builder.SourceKind = FirstNonEmpty(builder.SourceKind, candidate.Layer);
            builder.Lifecycle = FirstNonEmpty(builder.Lifecycle, candidate.CurrentLifecycle);
            builder.ReviewStatus = FirstNonEmpty(builder.ReviewStatus, candidate.CurrentReviewStatus);
            builder.ProvenanceRecordId = FirstNonEmpty(builder.ProvenanceRecordId, GetMetadataValue(candidate.Metadata, "provenanceRecordId", "provenanceId", "provenance"));
            builder.SourceFingerprint = FirstNonEmpty(builder.SourceFingerprint, GetMetadataValue(candidate.Metadata, "sourceFingerprint", "fingerprint", "contentHash"));
            builder.ReplacementState = FirstNonEmpty(builder.ReplacementState, GetMetadataValue(candidate.Metadata, "replacementState"));
            AddRefs(builder.SourceRefs, candidate.SourceRefs);
            AddRefs(builder.EvidenceRefs, candidate.EvidenceRefs);
            AddMetadata(builder.Metadata, candidate.Metadata, "candidate.");
        }
    }

    private static async Task<VectorLifecycleMetadataReviewBatch> LoadReviewBatchForEvidenceBackfillAsync(
        string? batchId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(batchId))
        {
            return await LoadReviewBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        }

        var resolved = ResolveLatestReviewBatchId(includeSynthetic: false);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException("No real vector lifecycle metadata review batch found. Run eval vector-lifecycle-metadata-review-batch-create first or pass --batch-id.");
        }

        return await LoadReviewBatchAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBatchAsync(
        VectorLifecycleMetadataReviewBatch batch,
        CancellationToken cancellationToken)
    {
        await WriteTextAsync(
            JsonSerializer.Serialize(batch, JsonOptions),
            Path.Combine(GetReviewBatchDirectory(batch.BatchId), "batch.json"),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VectorLifecycleMetadataReviewBatch> LoadReviewBatchAsync(
        string? batchId,
        CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrWhiteSpace(batchId)
            ? ResolveLatestReviewBatchId()
            : SanitizeReviewBatchId(batchId);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException("No vector lifecycle metadata review batch found. Run eval vector-lifecycle-metadata-review-batch-create first.");
        }

        var path = Path.Combine(GetReviewBatchDirectory(resolved), "batch.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Vector lifecycle metadata review batch not found.", path);
        }

        var batch = await ReadJsonFileAsync<VectorLifecycleMetadataReviewBatch>(path, cancellationToken)
            .ConfigureAwait(false);
        return batch ?? throw new InvalidOperationException($"Cannot read review batch: {path}");
    }

    private static string ResolveLatestReviewBatchId()
        => ResolveLatestReviewBatchId(includeSynthetic: true);

    private static string ResolveLatestReviewBatchId(bool includeSynthetic)
    {
        var root = GetReviewBatchRootDirectory();
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(root, "batch.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                try
                {
                    var batch = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewBatch>(
                        File.ReadAllText(path),
                        JsonOptions);
                    return batch;
                }
                catch (JsonException)
                {
                    return null;
                }
            })
            .Where(static item => item is not null)
            .Where(item => includeSynthetic || !IsSyntheticReviewBatch(item!))
            .OrderByDescending(static item => item!.CreatedAt)
            .Select(static item => item!.BatchId)
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool IsSyntheticReviewBatch(VectorLifecycleMetadataReviewBatch batch)
    {
        return string.Equals(batch.BatchId, "import-smoke", StringComparison.OrdinalIgnoreCase)
               || (batch.Metadata.TryGetValue("synthetic", out var synthetic)
                   && bool.TryParse(synthetic, out var parsed)
                   && parsed);
    }

    private static FileStorageOptions BuildEvalFileStorageOptions(ControlRoomService service)
    {
        return !service.State.IsServiceMode
               && string.Equals(service.State.StorageKind, "filesystem", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(service.State.RootPath)
            ? new FileStorageOptions { RootPath = service.State.RootPath }
            : new FileStorageOptions();
    }

    private static string ResolveRelationReplacementState(ContextRelation relation)
    {
        var values = new[]
        {
            relation.RelationType,
            GetMetadataValue(relation.Metadata, "replacementState", "replacementStatus", "lifecycle", "status", "reviewStatus")
        };
        foreach (var value in values)
        {
            if (ContainsAny(value, "superseded", "replaces", "replaced", "deprecated", "conflict", "historical"))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetMetadataValue(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> GetMetadataRefs(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            return [.. value
                .Trim('[', ']')
                .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static item => item.Trim().Trim('"'))
                .Where(static item => !string.IsNullOrWhiteSpace(item))];
        }

        return Array.Empty<string>();
    }

    private static void AddRefs(ICollection<string> target, IEnumerable<string> refs)
    {
        foreach (var value in refs)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim();
            if (!target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(normalized);
            }
        }
    }

    private static void AddMetadata(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string> source,
        string prefix = "")
    {
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            target[$"{prefix}{pair.Key}"] = pair.Value;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return !string.IsNullOrWhiteSpace(value)
               && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class VectorLifecycleMetadataEvidenceSnapshotBuilder(string itemId)
    {
        public string ItemId { get; } = itemId;

        public List<string> SourceRefs { get; } = [];

        public List<string> EvidenceRefs { get; } = [];

        public string ProvenanceRecordId { get; set; } = string.Empty;

        public string SourceFingerprint { get; set; } = string.Empty;

        public string SourceKind { get; set; } = string.Empty;

        public string ItemKind { get; set; } = string.Empty;

        public string Lifecycle { get; set; } = string.Empty;

        public string ReviewStatus { get; set; } = string.Empty;

        public string ReplacementState { get; set; } = string.Empty;

        public List<string> RelationEvidenceRefs { get; } = [];

        public List<string> ReviewEvidenceRefs { get; } = [];

        public Dictionary<string, string> OriginalCorpusMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

        public VectorLifecycleMetadataEvidenceSourceSnapshot ToSnapshot()
        {
            return new VectorLifecycleMetadataEvidenceSourceSnapshot
            {
                ItemId = ItemId,
                SourceRefs = SourceRefs.ToArray(),
                EvidenceRefs = EvidenceRefs.ToArray(),
                ProvenanceRecordId = ProvenanceRecordId,
                SourceFingerprint = SourceFingerprint,
                SourceKind = SourceKind,
                ItemKind = ItemKind,
                Lifecycle = Lifecycle,
                ReviewStatus = ReviewStatus,
                ReplacementState = ReplacementState,
                RelationEvidenceRefs = RelationEvidenceRefs.ToArray(),
                ReviewEvidenceRefs = ReviewEvidenceRefs.ToArray(),
                OriginalCorpusMetadata = new Dictionary<string, string>(OriginalCorpusMetadata, StringComparer.OrdinalIgnoreCase),
                Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    private static string GetReviewBatchRootDirectory()
        => Path.Combine("vector", "eligibility", "review-batches");

    private static string GetReviewBatchDirectory(string batchId)
        => Path.Combine(GetReviewBatchRootDirectory(), SanitizeReviewBatchId(batchId));

    private static string SanitizeReviewBatchId(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return string.Empty;
        }

        var trimmed = batchId.Trim();
        if (trimmed.IndexOf(Path.DirectorySeparatorChar) >= 0
            || trimmed.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid review batch id.");
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.')
            {
                throw new InvalidOperationException("Invalid review batch id.");
            }
        }

        return trimmed;
    }

    private static async Task WriteReviewSheetAsync(
        string batchId,
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(JsonSerializer.Serialize(row, JsonLineOptions));
        }

        await WriteTextAsync(builder.ToString(), Path.Combine(GetReviewBatchDirectory(batchId), "review-sheet.jsonl"), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VectorLifecycleMetadataReviewSheetRow>> ReadReviewSheetRowsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Review sheet not found.", path);
        }

        var rows = new List<VectorLifecycleMetadataReviewSheetRow>();
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewSheetRow>(line, JsonOptions);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static VectorLifecycleMetadataReviewCandidate CreateSmokeReviewCandidate(
        string workspaceId,
        string collectionId,
        string itemId,
        string currentLifecycle,
        string proposedLifecycle,
        string proposedTargetSection)
        => new()
        {
            CandidateId = VectorLifecycleMetadataReviewCandidateService.BuildCandidateId(workspaceId, collectionId, itemId, proposedLifecycle, proposedTargetSection, itemId, "smoke"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceSampleId = $"sample-{itemId}",
            SourceEvalSet = "smoke",
            MustHitItemId = itemId,
            ItemKind = "memory",
            Layer = "stable",
            CurrentLifecycle = currentLifecycle,
            CurrentReviewStatus = "PendingReview",
            CurrentTargetSection = VectorQueryTargetSections.Excluded,
            ProposedLifecycle = proposedLifecycle,
            ProposedReviewStatus = "Stable",
            ProposedTargetSection = proposedTargetSection,
            RepairReason = "smoke review candidate",
            EvidenceRefs = ["evidence:smoke"],
            SourceRefs = ["source:smoke"],
            ProvenanceAvailable = true,
            RelationEvidenceAvailable = true,
            ReviewEvidenceAvailable = true,
            RiskIfApproved = ["SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval"],
            RiskIfRejected = ["RecallRemainsBlockedByLifecycleMetadata"],
            RequiresHumanReview = true,
            Status = VectorLifecycleMetadataReviewCandidateStatuses.PendingReview,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "vector-lifecycle-metadata-review-smoke/v1",
                ["reviewOnly"] = bool.TrueString,
                ["runtimeEffect"] = bool.FalseString
            }
        };

    private static VectorLifecycleMetadataReviewRequest CreateSmokeReviewRequest(
        VectorLifecycleMetadataReviewCandidate candidate,
        string decision,
        bool confirmed,
        string? proposedTargetSection = null)
        => new()
        {
            CandidateId = candidate.CandidateId,
            Decision = decision,
            Reviewer = "smoke-reviewer",
            Reason = "smoke validation",
            ProposedLifecycle = candidate.ProposedLifecycle,
            ProposedReviewStatus = candidate.ProposedReviewStatus,
            ProposedTargetSection = proposedTargetSection ?? candidate.ProposedTargetSection,
            EvidenceRefs = candidate.EvidenceRefs,
            SourceRefs = candidate.SourceRefs,
            Confirmed = confirmed,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["smoke"] = bool.TrueString,
                ["excludedFromTraining"] = bool.TrueString
            }
        };

    private static string ResolveSafeVectorEligibilityInputPath(string path)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "vector", "eligibility"));
        var full = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("vector eligibility input path must stay under vector/eligibility.", nameof(path));
        }

        return full;
    }

    private static string NormalizeReportPathForOutput(string path)
    {
        var full = Path.GetFullPath(path);
        var cwd = Path.GetFullPath(Environment.CurrentDirectory);
        return full.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(cwd, full)
            : full;
    }

    private static async Task ExecuteVectorHybridPreviewAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? HybridOutputPath("vector-hybrid-preview.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out") ?? HybridOutputPath("vector-hybrid-preview.md");
        var options = new HybridVectorLexicalPreviewOptions
        {
            DenseTopK = CommandHelpers.GetIntOption(args, "--top-k", 10),
            LexicalTopK = CommandHelpers.GetIntOption(args, "--lexical-top-k", 10),
            AnchorTopK = CommandHelpers.GetIntOption(args, "--anchor-top-k", 10),
            UnionTopK = CommandHelpers.GetIntOption(args, "--union-top-k", 10)
        };

        var a3Samples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false);

        var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken).ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
        var runner = new HybridRetrievalPreviewRunner(
            infrastructure.QueryPreviewService,
            infrastructure.Store);

        var report = await runner.RunFullPreviewAsync(a3Samples, extendedSamples, workspaceId, collectionId, options, profileId, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(HybridRetrievalPreviewRunner.BuildMarkdown(report), markdownPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Hybrid retrieval preview written: {outputPath}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; variants={report.Variants.Count}");
    }

    private static async Task ExecuteVectorHybridShadowEvalAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        // shadow eval = preview but split output by dataset; reuse the preview runner.
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? HybridOutputPath("vector-hybrid-shadow-eval.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out") ?? HybridOutputPath("vector-hybrid-shadow-eval.md");
        var options = new HybridVectorLexicalPreviewOptions
        {
            DenseTopK = CommandHelpers.GetIntOption(args, "--top-k", 10),
            LexicalTopK = CommandHelpers.GetIntOption(args, "--lexical-top-k", 10),
            AnchorTopK = CommandHelpers.GetIntOption(args, "--anchor-top-k", 10),
            UnionTopK = CommandHelpers.GetIntOption(args, "--union-top-k", 10)
        };

        var a3Samples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false);

        var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken).ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
        var runner = new HybridRetrievalPreviewRunner(infrastructure.QueryPreviewService, infrastructure.Store);

        var report = await runner.RunFullPreviewAsync(a3Samples, extendedSamples, workspaceId, collectionId, options, profileId, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(HybridRetrievalPreviewRunner.BuildMarkdown(report), markdownPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Hybrid retrieval shadow eval written: {outputPath}");
        Console.WriteLine($"[Eval] recommendation={report.Recommendation}; variants={report.Variants.Count}");
    }

    private static async Task ExecuteVectorHybridReadinessGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? HybridOutputPath("vector-hybrid-readiness-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out") ?? HybridOutputPath("vector-hybrid-readiness-gate.md");
        var preview = await ReadJsonFileAsync<HybridRetrievalPreviewReport>(HybridOutputPath("vector-hybrid-shadow-eval.json"), cancellationToken).ConfigureAwait(false)
                      ?? await ReadJsonFileAsync<HybridRetrievalPreviewReport>(HybridOutputPath("vector-hybrid-preview.json"), cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                         && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        var report = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15Passed);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(HybridRetrievalReadinessGateRunner.BuildMarkdown(report), markdownPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Hybrid retrieval readiness gate written: {outputPath}");
        Console.WriteLine($"[Eval] passed={report.Passed}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteVectorHybridRecallRegressionAuditAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? HybridOutputPath("vector-hybrid-recall-regression-audit.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out") ?? HybridOutputPath("vector-hybrid-recall-regression-audit.md");
        var options = new HybridVectorLexicalPreviewOptions
        {
            DenseTopK = CommandHelpers.GetIntOption(args, "--top-k", 10),
            LexicalTopK = CommandHelpers.GetIntOption(args, "--lexical-top-k", 10),
            AnchorTopK = CommandHelpers.GetIntOption(args, "--anchor-top-k", 10),
            UnionTopK = CommandHelpers.GetIntOption(args, "--union-top-k", 10)
        };

        var a3Samples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: false, cancellationToken).ConfigureAwait(false);
        var extendedSamples = await LoadVectorEvalSamplesAsync(contextsRoot, categoryFilter, includeSeedBatches: true, cancellationToken).ConfigureAwait(false);

        var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken).ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, providerOptions);
        var hybridRunner = new HybridRetrievalPreviewRunner(infrastructure.QueryPreviewService, infrastructure.Store);
        var auditRunner = new HybridRetrievalRecallRegressionAuditRunner(hybridRunner, infrastructure.QueryPreviewService, infrastructure.Store);

        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                         && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var report = await auditRunner.RunAuditAsync(a3Samples, extendedSamples, workspaceId, collectionId, options, profileId, p15Passed, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(HybridRetrievalRecallRegressionAuditRunner.BuildMarkdown(report), markdownPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Hybrid recall regression audit written: {outputPath}");
        Console.WriteLine($"[Eval] passed={report.Passed}; recommendation={report.Recommendation}; denseDropped={report.DenseCandidateDroppedCount}; eligibilityMismatch={report.EligibilityMismatchCount}; dedupOverwrite={report.DedupOverwriteCount}");
    }

    private static async Task ExecuteVectorHybridFreezeGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out") ?? HybridOutputPath("vector-hybrid-freeze-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out") ?? HybridOutputPath("vector-hybrid-freeze-gate.md");
        var readinessGate = await ReadJsonFileAsync<HybridRetrievalReadinessGateReport>(
                HybridOutputPath("vector-hybrid-readiness-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var audit = await ReadJsonFileAsync<HybridRetrievalRecallRegressionAuditReport>(
                HybridOutputPath("vector-hybrid-recall-regression-audit.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                         && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        var report = new HybridRetrievalPreviewFreezeRunner().BuildFreezeReport(readinessGate, audit, p15Passed);
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(HybridRetrievalPreviewFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Hybrid retrieval freeze gate written: {outputPath}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; recommendation={report.Recommendation}; v4Recheck={report.V4RecheckAllowed}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task<VectorProviderConfigurationSanityAuditReport> BuildVectorProviderConfigurationSanityAuditAsync(
        CancellationToken cancellationToken)
    {
        var runner = new VectorProviderConfigurationSanityAuditRunner();
        var checks = new List<VectorProviderConfigurationSanityAuditItem>(capacity: 7);

        var smokePath = Qwen3OutputPath("embedding-provider-smoke.json");
        var smoke = await ReadJsonFileAsync<EmbeddingProviderSmokeReport>(smokePath, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(smoke is null
            ? MissingProviderConfigurationCheck("embedding-provider-smoke", smokePath)
            : runner.Check(
                "embedding-provider-smoke",
                smokePath,
                smoke.ProviderType,
                smoke.ProviderId,
                smoke.EmbeddingModel,
                smoke.ModelPath,
                smoke.TokenizerPath,
                smoke.ExpectedDimension,
                smoke.UseForRuntime));

        var comparisonPath = Qwen3OutputPath("vector-provider-comparison.json");
        var comparison = await ReadJsonFileAsync<VectorProviderComparisonV310Report>(comparisonPath, cancellationToken)
            .ConfigureAwait(false);
        var qwenComparison = comparison?.Providers.FirstOrDefault(IsQwen3ComparisonResult);
        checks.Add(qwenComparison is null
            ? MissingProviderConfigurationCheck("vector-provider-comparison", comparisonPath)
            : runner.Check(
                "vector-provider-comparison",
                comparisonPath,
                qwenComparison.ProviderType,
                qwenComparison.ProviderId,
                qwenComparison.ModelId,
                qwenComparison.ModelPath,
                qwenComparison.TokenizerPath,
                qwenComparison.Dimension,
                qwenComparison.UseForRuntime));

        await AddShadowCheckAsync(
            checks,
            runner,
            "vector-qwen3-shadow-eval-a3",
            Qwen3OutputPath("vector-qwen3-shadow-eval-a3.json"),
            cancellationToken).ConfigureAwait(false);
        await AddShadowCheckAsync(
            checks,
            runner,
            "vector-qwen3-shadow-eval-extended",
            Qwen3OutputPath("vector-qwen3-shadow-eval-extended.json"),
            cancellationToken).ConfigureAwait(false);
        await AddProfileSweepCheckAsync(
            checks,
            runner,
            "vector-query-profile-sweep-a3",
            Qwen3OutputPath("vector-query-profile-sweep-a3.json"),
            cancellationToken).ConfigureAwait(false);
        await AddProfileSweepCheckAsync(
            checks,
            runner,
            "vector-query-profile-sweep-extended",
            Qwen3OutputPath("vector-query-profile-sweep-extended.json"),
            cancellationToken).ConfigureAwait(false);

        var readinessPath = Qwen3OutputPath("vector-qwen3-readiness-gate.json");
        var readiness = await ReadJsonFileAsync<VectorQwen3ReadinessGateReport>(readinessPath, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(readiness is null
            ? MissingProviderConfigurationCheck("vector-qwen3-readiness-gate", readinessPath)
            : runner.Check(
                "vector-qwen3-readiness-gate",
                readinessPath,
                readiness.ProviderType,
                readiness.ProviderId,
                readiness.ModelId,
                readiness.ModelPath,
                readiness.TokenizerPath,
                readiness.Dimension,
                readiness.UseForRuntime));

        var postgresQueryPreviewPath = Qwen3OutputPath("postgres-vector-query-preview-report.json");
        var postgresQueryPreview = await ReadJsonFileAsync<PostgresVectorQueryPreviewReport>(
                postgresQueryPreviewPath,
                cancellationToken)
            .ConfigureAwait(false);
        checks.Add(postgresQueryPreview is null
            ? MissingProviderConfigurationCheck("postgres-vector-query-preview", postgresQueryPreviewPath)
            : runner.Check(
                "postgres-vector-query-preview",
                postgresQueryPreviewPath,
                postgresQueryPreview.ProviderType,
                postgresQueryPreview.ProviderId,
                postgresQueryPreview.ModelId,
                postgresQueryPreview.ModelPath,
                postgresQueryPreview.TokenizerPath,
                postgresQueryPreview.Dimension,
                postgresQueryPreview.UseForRuntime));

        var postgresShadowSummaryPath = Qwen3OutputPath("postgres-vector-shadow-eval-summary.json");
        var postgresShadowSummary = await ReadJsonFileAsync<PostgresVectorShadowEvalSummaryReport>(
                postgresShadowSummaryPath,
                cancellationToken)
            .ConfigureAwait(false);
        checks.Add(postgresShadowSummary is null
            ? MissingProviderConfigurationCheck("postgres-vector-shadow-eval-summary", postgresShadowSummaryPath)
            : runner.Check(
                "postgres-vector-shadow-eval-summary",
                postgresShadowSummaryPath,
                postgresShadowSummary.ProviderType,
                postgresShadowSummary.ProviderId,
                postgresShadowSummary.ModelId,
                postgresShadowSummary.ModelPath,
                postgresShadowSummary.TokenizerPath,
                postgresShadowSummary.Dimension,
                postgresShadowSummary.UseForRuntime));

        return runner.BuildReport(checks);
    }

    private static async Task AddShadowCheckAsync(
        ICollection<VectorProviderConfigurationSanityAuditItem> checks,
        VectorProviderConfigurationSanityAuditRunner runner,
        string reportKind,
        string path,
        CancellationToken cancellationToken)
    {
        var report = await ReadJsonFileAsync<VectorQueryShadowEvalReport>(path, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(report is null
            ? MissingProviderConfigurationCheck(reportKind, path)
            : runner.Check(
                reportKind,
                path,
                report.ProviderType,
                report.ProviderId,
                report.EmbeddingModel,
                report.ModelPath,
                report.TokenizerPath,
                report.Dimension,
                report.UseForRuntime));
    }

    private static async Task AddProfileSweepCheckAsync(
        ICollection<VectorProviderConfigurationSanityAuditItem> checks,
        VectorProviderConfigurationSanityAuditRunner runner,
        string reportKind,
        string path,
        CancellationToken cancellationToken)
    {
        var report = await ReadJsonFileAsync<VectorQueryProfileSweepReport>(path, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(report is null
            ? MissingProviderConfigurationCheck(reportKind, path)
            : runner.Check(
                reportKind,
                path,
                report.ProviderType,
                report.ProviderId,
                report.EmbeddingModel,
                report.ModelPath,
                report.TokenizerPath,
                report.Dimension,
                report.UseForRuntime));
    }

    private static bool IsQwen3ComparisonResult(VectorProviderComparisonV310Result result)
    {
        return IsQwen3ProviderAlias(result.ProviderId)
               || IsQwen3ProviderAlias(result.ModelId);
    }

    private static VectorProviderConfigurationSanityAuditItem MissingProviderConfigurationCheck(
        string reportKind,
        string path)
    {
        return new VectorProviderConfigurationSanityAuditItem
        {
            ReportKind = reportKind,
            ReportPath = path,
            Passed = false,
            Mismatches = ["ReportMissing"]
        };
    }

    private static async Task<VectorEmbeddingProviderComparisonResult> BuildProviderComparisonResultAsync(
        ControlRoomService service,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        EmbeddingProviderOptions options,
        CancellationToken cancellationToken)
    {
        var diagnostics = BuildProviderDiagnostics(options);
        var infrastructure = CreateVectorReindexInfrastructure(service, saveReports: false, sourceItems, options);
        var status = await BuildProviderScopedStatusAsync(
            infrastructure,
            workspaceId,
            collectionId,
            options,
            cancellationToken).ConfigureAwait(false);

        if (diagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorEmbeddingProviderComparisonReportBuilder.BuildResult(
                options,
                status,
                NewUnavailableQualityReport(samples.Count, options, diagnostics),
                NewUnavailableShadowReport(samples.Count, diagnostics),
                diagnostics);
        }

        var runner = new VectorQueryProfileSweepRunner(infrastructure.QueryPreviewService);
        var quality = await runner.BuildEmbeddingQualityBaselineAsync(
            samples,
            workspaceId,
            collectionId,
            cancellationToken).ConfigureAwait(false);
        var shadow = await new VectorQueryShadowEvalRunner(infrastructure.QueryPreviewService).RunAsync(
            samples,
            workspaceId,
            collectionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return VectorEmbeddingProviderComparisonReportBuilder.BuildResult(
            options,
            status,
            quality,
            shadow,
            diagnostics);
    }

    private static async Task<VectorIndexStatusResponse> BuildProviderScopedStatusAsync(
        VectorReindexCliInfrastructure infrastructure,
        string workspaceId,
        string collectionId,
        EmbeddingProviderOptions options,
        CancellationToken cancellationToken)
    {
        var entries = await infrastructure.Store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = options.ProviderId,
            EmbeddingModel = options.EmbeddingModel,
            Take = 100_000,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);
        var diagnostics = await infrastructure.IndexService.GetDiagnosticsAsync(
            workspaceId,
            collectionId,
            cancellationToken).ConfigureAwait(false);
        return new VectorIndexStatusResponse
        {
            Provider = options.ProviderId,
            Model = options.EmbeddingModel,
            Dimension = options.Dimension,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            IndexedCount = entries.Count(entry => options.Dimension <= 0 || entry.Dimension == options.Dimension),
            StaleCount = diagnostics.StaleCount,
            MissingCount = diagnostics.MissingCount,
            DuplicateCount = diagnostics.DuplicateCount,
            OrphanCount = diagnostics.OrphanCount,
            StoreAvailable = true,
            GeneratorAvailable = true,
            CreatedAt = DateTimeOffset.UtcNow,
            Warnings = diagnostics.Diagnostics
                .Where(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Message)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static VectorQueryProfileSweepReport NewUnavailableSweepReport(
        int sampleCount,
        EmbeddingProviderOptions options,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics)
    {
        return new VectorQueryProfileSweepReport
        {
            OperationId = $"vector-query-profile-sweep-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            Recommendation = VectorQueryShadowRecommendations.BlockedByRisk,
            Warnings = diagnostics.Select(item => $"{options.ProviderId}: {item.Type} - {item.Message}").ToArray()
        };
    }

    private static VectorQueryProfileSweepReport AttachProviderMetadata(
        VectorQueryProfileSweepReport report,
        EmbeddingProviderOptions options)
    {
        return new VectorQueryProfileSweepReport
        {
            OperationId = report.OperationId,
            CreatedAt = report.CreatedAt,
            ProviderId = options.ProviderId,
            ProviderType = options.ProviderType,
            EmbeddingModel = options.EmbeddingModel,
            ModelPath = options.ModelPath,
            TokenizerPath = options.TokenizerPath,
            Dimension = options.Dimension,
            UseForRuntime = false,
            Samples = report.Samples,
            Results = report.Results,
            BestResult = report.BestResult,
            Recommendation = report.Recommendation,
            Warnings = report.Warnings
        };
    }

    private static VectorEmbeddingQualityBaselineReport NewUnavailableQualityReport(
        int sampleCount,
        EmbeddingProviderOptions options,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics)
    {
        return new VectorEmbeddingQualityBaselineReport
        {
            OperationId = $"vector-embedding-quality-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            EmbeddingProvider = options.ProviderId,
            EmbeddingModel = options.EmbeddingModel,
            Recommendation = VectorQueryShadowRecommendations.BlockedByRisk,
            Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray()
        };
    }

    private static VectorQueryShadowEvalReport NewUnavailableShadowReport(
        int sampleCount,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics)
    {
        return new VectorQueryShadowEvalReport
        {
            OperationId = $"vector-query-shadow-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            QueryCount = sampleCount,
            Recommendation = VectorQueryShadowRecommendations.BlockedByRisk,
            FormalOutputChanged = 0,
            Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray()
        };
    }

    private static VectorQueryShadowEvalReport AttachProviderMetadata(
        VectorQueryShadowEvalReport report,
        EmbeddingProviderOptions options)
    {
        return new VectorQueryShadowEvalReport
        {
            OperationId = report.OperationId,
            CreatedAt = report.CreatedAt,
            ProviderId = options.ProviderId,
            ProviderType = options.ProviderType,
            EmbeddingModel = options.EmbeddingModel,
            ModelPath = options.ModelPath,
            TokenizerPath = options.TokenizerPath,
            Dimension = options.Dimension,
            UseForRuntime = false,
            Samples = report.Samples,
            IndexedCoverage = report.IndexedCoverage,
            QueryCount = report.QueryCount,
            CandidateCount = report.CandidateCount,
            RawCandidateCount = report.RawCandidateCount,
            EligibleCandidateCount = report.EligibleCandidateCount,
            BlockedCandidateCount = report.BlockedCandidateCount,
            RiskBeforePolicy = report.RiskBeforePolicy,
            RiskAfterPolicy = report.RiskAfterPolicy,
            MustHitRecallBeforePolicy = report.MustHitRecallBeforePolicy,
            MustHitRecallAfterPolicy = report.MustHitRecallAfterPolicy,
            MustNotHitRiskBeforePolicy = report.MustNotHitRiskBeforePolicy,
            MustNotHitRiskAfterPolicy = report.MustNotHitRiskAfterPolicy,
            LifecycleRiskBeforePolicy = report.LifecycleRiskBeforePolicy,
            LifecycleRiskAfterPolicy = report.LifecycleRiskAfterPolicy,
            MustHitRecallAtK = report.MustHitRecallAtK,
            MustNotHitRiskAtK = report.MustNotHitRiskAtK,
            LifecycleRiskAtK = report.LifecycleRiskAtK,
            DeprecatedHitCount = report.DeprecatedHitCount,
            DuplicateHitCount = report.DuplicateHitCount,
            AverageTopSimilarity = report.AverageTopSimilarity,
            NoCandidateCount = report.NoCandidateCount,
            LowConfidenceCount = report.LowConfidenceCount,
            TopNoiseClusters = report.TopNoiseClusters,
            BlockedByReason = report.BlockedByReason,
            Recommendation = report.Recommendation,
            FormalOutputChanged = report.FormalOutputChanged,
            SampleResults = report.SampleResults,
            Warnings = report.Warnings
        };
    }

    private static PostgresVectorQueryPreviewReport AttachProviderMetadata(
        PostgresVectorQueryPreviewReport report,
        EmbeddingProviderOptions options)
    {
        return new PostgresVectorQueryPreviewReport
        {
            GeneratedAt = report.GeneratedAt,
            WorkspaceId = report.WorkspaceId,
            CollectionId = report.CollectionId,
            ProviderId = report.ProviderId,
            ProviderType = options.ProviderType,
            ModelId = report.ModelId,
            ModelPath = options.ModelPath,
            TokenizerPath = options.TokenizerPath,
            Dimension = report.Dimension,
            Normalized = report.Normalized,
            TopK = report.TopK,
            ProfileId = report.ProfileId,
            Recommendation = report.Recommendation,
            QueryCount = report.QueryCount,
            CandidateCount = report.CandidateCount,
            PgVectorCandidateCount = report.PgVectorCandidateCount,
            FileSystemCandidateCount = report.FileSystemCandidateCount,
            TopKOverlapCount = report.TopKOverlapCount,
            TopKOverlapRate = report.TopKOverlapRate,
            OrderingMismatchCount = report.OrderingMismatchCount,
            ScoreDeltaMax = report.ScoreDeltaMax,
            MetadataMismatchCount = report.MetadataMismatchCount,
            EligibilityMetadataMismatchCount = report.EligibilityMetadataMismatchCount,
            RiskProjectionMismatchCount = report.RiskProjectionMismatchCount,
            DimensionMismatchBlocked = report.DimensionMismatchBlocked,
            ProviderModelMismatchBlocked = report.ProviderModelMismatchBlocked,
            UseForRuntime = report.UseForRuntime,
            Samples = report.Samples,
            Diagnostics = report.Diagnostics
        };
    }

    private static PostgresVectorShadowEvalReport AttachProviderMetadata(
        PostgresVectorShadowEvalReport report,
        EmbeddingProviderOptions options)
    {
        return new PostgresVectorShadowEvalReport
        {
            GeneratedAt = report.GeneratedAt,
            DatasetName = report.DatasetName,
            WorkspaceId = report.WorkspaceId,
            CollectionId = report.CollectionId,
            ProviderId = report.ProviderId,
            ProviderType = options.ProviderType,
            ModelId = report.ModelId,
            ModelPath = options.ModelPath,
            TokenizerPath = options.TokenizerPath,
            Dimension = report.Dimension,
            Normalized = report.Normalized,
            ProfileId = report.ProfileId,
            TopK = report.TopK,
            Recommendation = report.Recommendation,
            SampleCount = report.SampleCount,
            QueryCount = report.QueryCount,
            PgVectorCandidateCount = report.PgVectorCandidateCount,
            FileSystemCandidateCount = report.FileSystemCandidateCount,
            RecallAfterPolicy = report.RecallAfterPolicy,
            MrrAfterPolicy = report.MrrAfterPolicy,
            FileSystemRecallAfterPolicy = report.FileSystemRecallAfterPolicy,
            RecallDelta = report.RecallDelta,
            RiskAfterPolicy = report.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = report.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = report.LifecycleRiskAfterPolicy,
            FormalOutputChanged = report.FormalOutputChanged,
            TopKOverlapRate = report.TopKOverlapRate,
            OrderingMismatchCount = report.OrderingMismatchCount,
            ScoreDeltaMax = report.ScoreDeltaMax,
            MetadataMismatchCount = report.MetadataMismatchCount,
            EligibilityMetadataMismatchCount = report.EligibilityMetadataMismatchCount,
            RiskProjectionMismatchCount = report.RiskProjectionMismatchCount,
            UseForRuntime = report.UseForRuntime,
            Samples = report.Samples,
            Diagnostics = report.Diagnostics
        };
    }

    private static PostgresVectorShadowEvalSummaryReport AttachProviderMetadata(
        PostgresVectorShadowEvalSummaryReport report,
        EmbeddingProviderOptions options)
    {
        var first = report.Reports.FirstOrDefault();
        return new PostgresVectorShadowEvalSummaryReport
        {
            GeneratedAt = report.GeneratedAt,
            Recommendation = report.Recommendation,
            ProviderType = options.ProviderType,
            ProviderId = first?.ProviderId ?? options.ProviderId,
            ModelId = first?.ModelId ?? options.EmbeddingModel,
            ModelPath = options.ModelPath,
            TokenizerPath = options.TokenizerPath,
            Dimension = first?.Dimension ?? options.Dimension,
            UseForRuntime = report.UseForRuntime,
            Reports = report.Reports,
            Diagnostics = report.Diagnostics
        };
    }

    private static VectorResidualRiskAuditReport NewUnavailableResidualRiskAuditReport(
        int sampleCount,
        EmbeddingProviderOptions options,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics,
        string profileId)
    {
        return new VectorResidualRiskAuditReport
        {
            OperationId = $"vector-residual-risk-audit-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            ProviderId = options.ProviderId,
            EmbeddingModel = options.EmbeddingModel,
            ProfileId = profileId,
            Recommendation = VectorQueryShadowRecommendations.BlockedByRisk,
            Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray()
        };
    }

    private static VectorRecallLossAuditReport NewUnavailableRecallLossAuditReport(
        int sampleCount,
        EmbeddingProviderOptions options,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics,
        string profileId,
        int topK,
        double? minSimilarity,
        string? layer,
        string? itemKind)
    {
        return new VectorRecallLossAuditReport
        {
            OperationId = $"vector-recall-loss-audit-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            ProviderId = options.ProviderId,
            EmbeddingModel = options.EmbeddingModel,
            ProfileId = profileId,
            TopK = topK,
            MinSimilarity = minSimilarity,
            LayerFilter = layer ?? string.Empty,
            ItemKindFilter = itemKind ?? string.Empty,
            Recommendation = VectorQueryShadowRecommendations.BlockedByRisk,
            FormalOutputChanged = 0,
            Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray()
        };
    }

    private static VectorSafeRecallRecoveryReport NewUnavailableSafeRecallRecoveryReport(
        int sampleCount,
        string reason)
    {
        return new VectorSafeRecallRecoveryReport
        {
            OperationId = $"vector-safe-recall-recovery-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            Recommendation = VectorQueryShadowRecommendations.BlockedByRisk,
            FormalOutputChanged = 0,
            Warnings = [reason]
        };
    }

    private static VectorQueryPreviewResult NewProviderBlockedVectorQueryPreviewResult(
        VectorQueryPreviewRequest request,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics)
    {
        return new VectorQueryPreviewResult
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"vector-query-preview-provider-blocked-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            TopK = request.TopK,
            ProfileId = request.ProfileId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            MinSimilarity = request.MinSimilarity,
            Diagnostics = new VectorQueryPreviewDiagnostics
            {
                StoreAvailable = true,
                GeneratorAvailable = false,
                ProviderUnavailableCount = diagnostics.Count(item => item.Type == VectorIndexDiagnosticTypes.ProviderUnavailable),
                Diagnostics = diagnostics
            },
            Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static IReadOnlyList<VectorIndexDiagnostic> BuildProviderDiagnostics(EmbeddingProviderOptions options)
    {
        if (options.ProviderType.Equals(EmbeddingProviderTypes.DeterministicHash, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<VectorIndexDiagnostic>();
        }

        if (options.ProviderType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase)
            || options.ProviderType.Equals(EmbeddingProviderTypes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingProviderDiagnosticsBuilder.Build(options);
        }

        return
        [
            new VectorIndexDiagnostic
            {
                DiagnosticId = $"unsupported-provider-type:{options.ProviderType}",
                Type = VectorIndexDiagnosticTypes.ProviderUnavailable,
                Severity = "Error",
                Message = $"Unsupported embedding provider type '{options.ProviderType}'. Use --provider qwen3 for the preset or --provider-type onnx-local for the implementation.",
                SuggestedAction = "修正 provider preset / provider type 配置后重新执行 eval。"
            }
        ];
    }

    private static IReadOnlyList<EmbeddingProviderOptions> BuildProviderComparisonOptions(IReadOnlyList<string> args)
    {
        var deterministic = new EmbeddingProviderOptions
        {
            ProviderId = "deterministic-hash",
            ProviderType = EmbeddingProviderTypes.DeterministicHash,
            EmbeddingModel = "deterministic-hash-v1",
            Dimension = 16,
            Normalize = true,
            PoolingStrategy = "Mean",
            Enabled = true
        };
        var onnx = BuildEmbeddingProviderOptions(args, EmbeddingProviderTypes.OnnxLocal);
        return [deterministic, onnx];
    }

    private static async Task<VectorReindexRequest> BuildVectorReindexRequestAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        bool apply,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var maxItems = CommandHelpers.GetIntOption(args, "--max-items", 200);
        var batchSize = CommandHelpers.GetIntOption(args, "--batch-size", 50);
        var layers = ParseCsvOption(CommandHelpers.GetOption(args, "--layers"));
        var layer = CommandHelpers.GetOption(args, "--layer");
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var useStoreSource = IsVectorStoreSourceMode(args);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        return new VectorReindexRequest
        {
            OperationId = $"vector-reindex-cli-{Guid.NewGuid():N}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Layer = layer,
            Layers = layers,
            ItemKind = CommandHelpers.GetOption(args, "--item-kind"),
            DryRun = !apply,
            Apply = apply,
            ConfirmApply = apply,
            Force = CommandHelpers.HasFlag(args, "--force"),
            BatchSize = batchSize > 0 ? batchSize : 50,
            MaxItems = maxItems > 0 ? maxItems : 200,
            IncludeContextItems = useStoreSource && !CommandHelpers.HasFlag(args, "--no-context"),
            IncludeMemoryItems = useStoreSource && !CommandHelpers.HasFlag(args, "--no-memory"),
            SourceItems = sourceItems,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "eval_vector_reindex_cli",
                ["sourceMode"] = ResolveVectorSourceMode(args),
                ["embeddingProvider"] = providerOptions.ProviderId,
                ["embeddingProviderType"] = providerOptions.ProviderType,
                ["embeddingModel"] = providerOptions.EmbeddingModel,
                ["embeddingDimension"] = providerOptions.Dimension.ToString(CultureInfo.InvariantCulture),
                ["normalize"] = providerOptions.Normalize ? "true" : "false"
            }
        };
    }

    private static async Task<VectorReindexRequest> BuildVectorCoverageReindexRequestAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var request = await BuildVectorReindexRequestAsync(service, args, apply: false, cancellationToken)
            .ConfigureAwait(false);
        var maxItems = CommandHelpers.GetIntOption(args, "--max-items", 100_000);
        return new VectorReindexRequest
        {
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            Layers = request.Layers,
            DryRun = true,
            Apply = false,
            ConfirmApply = false,
            Force = request.Force,
            BatchSize = request.BatchSize,
            MaxItems = maxItems > 0 ? maxItems : 100_000,
            IncludeContextItems = request.IncludeContextItems,
            IncludeMemoryItems = request.IncludeMemoryItems,
            SourceItems = request.SourceItems,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "eval_vector_index_coverage_cli"
            }
        };
    }

    private static VectorQueryPreviewRequest BuildVectorQueryPreviewRequest(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string queryText)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        return new VectorQueryPreviewRequest
        {
            OperationId = $"vector-query-cli-{Guid.NewGuid():N}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = queryText,
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 10),
            ProfileId = CommandHelpers.GetOption(args, "--profile")
                ?? CommandHelpers.GetOption(args, "--vector-profile")
                ?? VectorQueryProfileIds.NormalV1,
            Layer = CommandHelpers.GetOption(args, "--layer"),
            ItemKind = CommandHelpers.GetOption(args, "--item-kind"),
            MinSimilarity = GetDoubleOption(args, "--min-similarity"),
            IncludeVector = CommandHelpers.HasFlag(args, "--include-vector"),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "eval_vector_query_preview_cli"
            }
        };
    }

    private static async Task<VectorQueryShadowEvalReport> RunVectorQueryShadowEvalWithClientAsync(
        ContextCoreClient client,
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK,
        string? layer,
        string? itemKind,
        double? minSimilarity,
        double lowConfidenceThreshold,
        string profileId,
        CancellationToken cancellationToken)
    {
        var operationId = $"vector-query-shadow-eval-{Guid.NewGuid():N}";
        var results = new List<VectorQueryShadowEvalSample>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await client.PreviewVectorQueryAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = topK,
                ProfileId = profileId,
                Layer = layer,
                ItemKind = itemKind,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sampleId"] = sample.Id,
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "eval_vector_query_shadow_client"
                }
            }, cancellationToken).ConfigureAwait(false);

            results.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, preview, lowConfidenceThreshold));
        }

        return VectorQueryShadowEvalRunner.BuildReport(operationId, results);
    }

    private static async Task<VectorResidualRiskAuditReport> RunVectorResidualRiskAuditWithClientAsync(
        ContextCoreClient client,
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK,
        string profileId,
        double? minSimilarity,
        CancellationToken cancellationToken)
    {
        var operationId = $"vector-residual-risk-audit-{Guid.NewGuid():N}";
        var results = new List<VectorQueryShadowEvalSample>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await client.PreviewVectorQueryAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = topK,
                ProfileId = profileId,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "eval_vector_residual_risk_audit_client"
                }
            }, cancellationToken).ConfigureAwait(false);

            results.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, preview, 0.25));
        }

        return VectorResidualRiskAuditRunner.BuildReport(operationId, results, profileId);
    }

    private static async Task<VectorRecallLossAuditReport> RunVectorRecallLossAuditWithClientAsync(
        ContextCoreClient client,
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK,
        string? layer,
        string? itemKind,
        double? minSimilarity,
        double lowConfidenceThreshold,
        string profileId,
        CancellationToken cancellationToken)
    {
        var operationId = $"vector-recall-loss-audit-{Guid.NewGuid():N}";
        var configured = new List<VectorQueryShadowEvalSample>();
        var broadPreviews = new Dictionary<string, VectorQueryPreviewResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuredPreview = await client.PreviewVectorQueryAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}:configured",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = topK,
                ProfileId = profileId,
                Layer = layer,
                ItemKind = itemKind,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "eval_vector_recall_loss_audit_client"
                }
            }, cancellationToken).ConfigureAwait(false);
            configured.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, configuredPreview, lowConfidenceThreshold));

            broadPreviews[sample.Id] = await client.PreviewVectorQueryAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}:diagnostic",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = 1000,
                ProfileId = profileId,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "eval_vector_recall_loss_audit_client_diagnostic"
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        return new VectorRecallLossAuditRunner().BuildReport(
            operationId,
            samples,
            configured,
            broadPreviews,
            Array.Empty<VectorIndexEntry>(),
            profileId,
            topK,
            minSimilarity,
            layer,
            itemKind,
            ["service mode 未读取全量 vector index entries；wasIndexed 仅能由 preview 候选侧推断，建议本地模式刷新完整审计。"]);
    }

    private static async Task<IReadOnlyList<ContextEvalSample>> LoadVectorEvalSamplesAsync(
        string contextsRoot,
        string? categoryFilter,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        var categories = new[] { "chat", "project", "novel", "automation", "coding-mode" };
        var samples = new Dictionary<string, ContextEvalSample>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(categoryFilter)
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRoot, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            IReadOnlyList<ContextEvalSample> loaded;
            if (includeSeedBatches)
            {
                loaded = (await new ContextEvalSampleLoader()
                    .LoadAsync(categoryDir, cancellationToken)
                    .ConfigureAwait(false)).Samples;
            }
            else
            {
                var path = Path.Combine(categoryDir, "seed_samples.json");
                if (!File.Exists(path))
                {
                    continue;
                }

                loaded = JsonSerializer.Deserialize<IReadOnlyList<ContextEvalSample>>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                    EvalSampleJsonOptions) ?? Array.Empty<ContextEvalSample>();
            }

            foreach (var sample in loaded.Where(sample => !string.IsNullOrWhiteSpace(sample.Id)))
            {
                samples.TryAdd(sample.Id, sample);
            }
        }

        return samples.Values
            .OrderBy(sample => sample.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveVectorSourceMode(IReadOnlyList<string> args)
    {
        var source = CommandHelpers.GetOption(args, "--source");
        return string.IsNullOrWhiteSpace(source)
            ? VectorEvalCorpusSourceMode
            : source.Trim();
    }

    private static bool IsVectorStoreSourceMode(IReadOnlyList<string> args)
    {
        return string.Equals(ResolveVectorSourceMode(args), VectorStoreSourceMode, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVectorCommandWorkspaceId(ControlRoomService service, IReadOnlyList<string> args)
    {
        return CommandHelpers.GetOption(args, "--workspace")
               ?? (IsVectorStoreSourceMode(args) ? service.State.WorkspaceId : VectorEvalCorpusWorkspaceId);
    }

    private static string ResolveVectorCommandCollectionId(ControlRoomService service, IReadOnlyList<string> args)
    {
        return CommandHelpers.GetOption(args, "--collection")
               ?? (IsVectorStoreSourceMode(args) ? service.State.CollectionId : VectorEvalCorpusCollectionId);
    }

    private static async Task<IReadOnlyList<VectorReindexSourceItem>> LoadVectorReindexSourceItemsForCommandAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (IsVectorStoreSourceMode(args))
        {
            return Array.Empty<VectorReindexSourceItem>();
        }

        var sourceMode = ResolveVectorSourceMode(args);
        if (!string.Equals(sourceMode, VectorEvalCorpusSourceMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported vector source mode: {sourceMode}");
        }

        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = !CommandHelpers.HasFlag(args, "--baseline-only");
        return await LoadVectorEvalCorpusSourceItemsAsync(
            contextsRoot,
            categoryFilter,
            includeSeedBatches,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VectorReindexSourceItem>> LoadPostgresVectorProviderScopedReindexSourceItemsAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (!IsVectorStoreSourceMode(args))
        {
            return await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken).ConfigureAwait(false);
        }

        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var maxItems = CommandHelpers.GetIntOption(args, "--max-items", 200);
        var take = maxItems > 0 ? maxItems : 200;
        var items = new Dictionary<string, VectorReindexSourceItem>(StringComparer.OrdinalIgnoreCase);
        if (!CommandHelpers.HasFlag(args, "--no-context"))
        {
            var contextItems = await service.State.ContextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                IncludeContent = true,
                Take = take
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in contextItems)
            {
                AddVectorCorpusSourceItem(items, new VectorReindexSourceItem
                {
                    ItemId = item.Id,
                    ItemKind = item.Type,
                    Layer = "context",
                    Text = string.Join(' ', new[] { item.Title, item.Content }.Where(text => !string.IsNullOrWhiteSpace(text))),
                    UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt,
                    Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
                    {
                        ["sourceMode"] = VectorStoreSourceMode,
                        ["sourceKind"] = "context"
                    }
                });
            }
        }

        if (!CommandHelpers.HasFlag(args, "--no-memory"))
        {
            var memoryItems = await service.State.MemoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = take
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in memoryItems)
            {
                AddVectorCorpusSourceItem(items, new VectorReindexSourceItem
                {
                    ItemId = item.Id,
                    ItemKind = item.Type,
                    Layer = item.Layer.ToString(),
                    Text = item.Content ?? string.Empty,
                    UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt,
                    Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
                    {
                        ["sourceMode"] = VectorStoreSourceMode,
                        ["sourceKind"] = "memory",
                        ["status"] = item.Status.ToString(),
                        ["lifecycle"] = item.Status.ToString()
                    }
                });
            }
        }

        return items.Values
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToArray();
    }

    private static async Task<IReadOnlyList<VectorReindexSourceItem>> LoadVectorEvalCorpusSourceItemsAsync(
        string contextsRoot,
        string? categoryFilter,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        var categories = new[] { "chat", "project", "novel", "automation", "coding-mode" };
        var items = new Dictionary<string, VectorReindexSourceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(categoryFilter)
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRoot, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var corpusFiles = includeSeedBatches
                ? Directory.EnumerateFiles(categoryDir, "corpus*.json", SearchOption.TopDirectoryOnly)
                : File.Exists(Path.Combine(categoryDir, "corpus.json"))
                    ? [Path.Combine(categoryDir, "corpus.json")]
                    : Enumerable.Empty<string>();
            foreach (var corpusFile in corpusFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                var json = await File.ReadAllTextAsync(corpusFile, cancellationToken).ConfigureAwait(false);
                var corpus = JsonSerializer.Deserialize<ContextEvalCorpus>(json, EvalSampleJsonOptions)
                             ?? new ContextEvalCorpus();
                foreach (var contextItem in corpus.Contexts)
                {
                    AddVectorCorpusSourceItem(items, ToVectorSourceItem(contextItem, category, corpusFile));
                }

                foreach (var memoryItem in corpus.Memories)
                {
                    AddVectorCorpusSourceItem(items, ToVectorSourceItem(memoryItem, category, corpusFile));
                }
            }
        }

        return items.Values
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ContextRelation>> LoadVectorEvalCorpusRelationsAsync(
        string contextsRoot,
        string? categoryFilter,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        var categories = new[] { "chat", "project", "novel", "automation", "coding-mode" };
        var relations = new Dictionary<string, ContextRelation>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(categoryFilter)
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRoot, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var corpusFiles = includeSeedBatches
                ? Directory.EnumerateFiles(categoryDir, "corpus*.json", SearchOption.TopDirectoryOnly)
                : File.Exists(Path.Combine(categoryDir, "corpus.json"))
                    ? [Path.Combine(categoryDir, "corpus.json")]
                    : Enumerable.Empty<string>();
            foreach (var corpusFile in corpusFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                var json = await File.ReadAllTextAsync(corpusFile, cancellationToken).ConfigureAwait(false);
                var corpus = JsonSerializer.Deserialize<ContextEvalCorpus>(json, EvalSampleJsonOptions)
                             ?? new ContextEvalCorpus();
                foreach (var relation in corpus.Relations.Where(static item => !string.IsNullOrWhiteSpace(item.Id)))
                {
                    relations[relation.Id] = relation;
                }
            }
        }

        return relations.Values
            .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddVectorCorpusSourceItem(
        IDictionary<string, VectorReindexSourceItem> items,
        VectorReindexSourceItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ItemId) || string.IsNullOrWhiteSpace(item.Text))
        {
            return;
        }

        items[item.ItemId] = item;
    }

    private static VectorReindexSourceItem ToVectorSourceItem(
        ContextItem item,
        string category,
        string corpusFile)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceMode"] = VectorEvalCorpusSourceMode,
            ["evalCategory"] = category,
            ["corpusFile"] = Path.GetFileName(corpusFile),
            ["sourceKind"] = "context"
        };
        if (item.Tags.Count > 0)
        {
            metadata["sourceTags"] = string.Join(",", item.Tags);
        }

        return new VectorReindexSourceItem
        {
            ItemId = item.Id,
            ItemKind = item.Type,
            Layer = "context",
            Text = string.Join(' ', new[] { item.Title, item.Content }.Where(text => !string.IsNullOrWhiteSpace(text))),
            UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt,
            Metadata = metadata
        };
    }

    private static VectorReindexSourceItem ToVectorSourceItem(
        ContextMemoryItem item,
        string category,
        string corpusFile)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceMode"] = VectorEvalCorpusSourceMode,
            ["evalCategory"] = category,
            ["corpusFile"] = Path.GetFileName(corpusFile),
            ["sourceKind"] = "memory",
            ["status"] = item.Status.ToString(),
            ["lifecycle"] = item.Status.ToString()
        };
        if (item.Tags.Count > 0)
        {
            metadata["sourceTags"] = string.Join(",", item.Tags);
        }

        return new VectorReindexSourceItem
        {
            ItemId = item.Id,
            ItemKind = item.Type,
            Layer = item.Layer.ToString(),
            Text = item.Content ?? string.Empty,
            UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt,
            Metadata = metadata
        };
    }

    private static VectorReindexCliInfrastructure CreateVectorReindexInfrastructure(
        ControlRoomService service,
        bool saveReports,
        IReadOnlyList<VectorReindexSourceItem>? sourceItems = null,
        EmbeddingProviderOptions? providerOptions = null)
    {
        providerOptions ??= new EmbeddingProviderOptions();
        var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
        IVectorIndexStore vectorStore;
        IVectorReindexReportStore? reportStore;
        if (string.Equals(service.State.StorageKind, "filesystem", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(service.State.RootPath))
        {
            var options = new FileStorageOptions { RootPath = service.State.RootPath };
            var paths = new FilePathResolver(options);
            var serializer = new FileFormatSerializer();
            vectorStore = new FileVectorIndexStore(paths, serializer);
            reportStore = saveReports ? new FileVectorReindexReportStore(paths, serializer) : null;
        }
        else
        {
            vectorStore = new InMemoryVectorIndexStore();
            reportStore = saveReports ? new InMemoryVectorReindexReportStore() : null;
        }

        var planner = new VectorReindexPlanner(
            service.State.ContextStore,
            service.State.MemoryStore,
            vectorStore,
            generator);
        var executor = new VectorReindexExecutor(
            planner,
            generator,
            vectorStore,
            reportStore);
        var indexService = new VectorIndexService(
            vectorStore,
            generator,
            service.State.ContextStore,
            service.State.MemoryStore,
            sourceItems);
        var queryPreviewService = new VectorQueryPreviewService(
            vectorStore,
            generator,
            indexService);
        return new VectorReindexCliInfrastructure(executor, indexService, queryPreviewService, vectorStore);
    }

    private static VectorReindexResult NewVectorReindexDryRunResult(
        VectorReindexRequest request,
        VectorReindexPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        return new VectorReindexResult
        {
            ReportId = Guid.NewGuid().ToString("N"),
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Plan = plan,
            Summary = new VectorReindexSummary
            {
                TotalCandidates = plan.TotalCandidates,
                Skipped = plan.Items.Count,
                Duplicate = plan.DuplicateItems.Count,
                Orphan = plan.OrphanItems.Count,
                EstimatedEmbeddingCount = plan.EstimatedEmbeddingCount,
                DryRun = true,
                Applied = false
            },
            ProcessedItems = plan.Items,
            Warnings = plan.Warnings,
            StartedAt = now,
            CompletedAt = now
        };
    }

    private static VectorReindexResult NewVectorReindexProviderBlockedResult(
        VectorReindexRequest request,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics)
    {
        var now = DateTimeOffset.UtcNow;
        return new VectorReindexResult
        {
            ReportId = Guid.NewGuid().ToString("N"),
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Plan = new VectorReindexPlan
            {
                PlanId = request.OperationId,
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                DryRun = true,
                Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray(),
                CreatedAt = now
            },
            Summary = new VectorReindexSummary
            {
                DryRun = true,
                Applied = false,
                Failed = diagnostics.Count
            },
            Warnings = diagnostics.Select(item => $"{item.Type}: {item.Message}").ToArray(),
            Errors = diagnostics
                .Where(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                .Select(item => $"{item.Type}: {item.Message}")
                .ToArray(),
            StartedAt = now,
            CompletedAt = now
        };
    }

    private static string BuildVectorIndexDiagnosticsMarkdown(VectorIndexDiagnosticsReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Index Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{report.WorkspaceId}`");
        builder.AppendLine($"- Collection: `{report.CollectionId}`");
        builder.AppendLine($"- Diagnostics: `{report.Diagnostics.Count}`");
        builder.AppendLine($"- Missing: `{report.MissingCount}`");
        builder.AppendLine($"- Stale: `{report.StaleCount}`");
        builder.AppendLine($"- Duplicate: `{report.DuplicateCount}`");
        builder.AppendLine($"- Orphan: `{report.OrphanCount}`");
        builder.AppendLine($"- DimensionMismatch: `{report.DimensionMismatchCount}`");
        builder.AppendLine();
        builder.AppendLine("| Type | Severity | ItemId | EntryId | Message |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var item in report.Diagnostics.Take(100))
        {
            builder.AppendLine($"| {item.Type} | {item.Severity} | {item.ItemId} | {item.EntryId ?? "-"} | {item.Message.Replace("|", "/")} |");
        }

        return builder.ToString();
    }

    private sealed record VectorReindexCliInfrastructure(
        VectorReindexExecutor Executor,
        VectorIndexService IndexService,
        VectorQueryPreviewService QueryPreviewService,
        IVectorIndexStore Store);

    private static IEmbeddingGenerator CreateVectorCommandEmbeddingGenerator(EmbeddingProviderOptions options)
    {
        if (options.ProviderType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase))
        {
            return new OnnxEmbeddingGenerator(options);
        }

        if (options.ProviderType.Equals(EmbeddingProviderTypes.DeterministicHash, StringComparison.OrdinalIgnoreCase))
        {
            return new DeterministicHashEmbeddingGenerator(options.Dimension > 0 ? options.Dimension : 16);
        }

        throw new InvalidOperationException($"Unsupported embedding provider type: {options.ProviderType}");
    }

    private static bool ResolveGeneratorNormalize(IEmbeddingGenerator generator, EmbeddingProviderOptions options)
    {
        return generator is IEmbeddingGeneratorDescriptor descriptor
            ? descriptor.Normalize
            : options.Normalize;
    }

    private static EmbeddingProviderOptions BuildEmbeddingProviderOptions(
        IReadOnlyList<string> args,
        string? providerOverride = null)
    {
        var isQwen3Provider = IsQwen3ProviderRequest(args, providerOverride);
        var providerTypeOverride = CommandHelpers.GetOption(args, "--provider-type");
        var providerType = NormalizeEmbeddingProviderType(
            isQwen3Provider
                ? EmbeddingProviderTypes.OnnxLocal
                : providerTypeOverride
                  ?? providerOverride
                  ?? CommandHelpers.GetOption(args, "--provider")
                  ?? EmbeddingProviderTypes.DeterministicHash);
        var providerId = CommandHelpers.GetOption(args, "--provider-id")
            ?? (isQwen3Provider
                ? Qwen3ProviderId
                : providerType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase)
                ? "onnx-local"
                : "deterministic-hash");
        var model = CommandHelpers.GetOption(args, "--embedding-model")
            ?? CommandHelpers.GetOption(args, "--model")
            ?? (isQwen3Provider
                ? Qwen3ModelId
                : providerType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase)
                ? EmbeddingModelPaths.DefaultModelName
                : "deterministic-hash-v1");
        return new EmbeddingProviderOptions
        {
            ProviderId = providerId,
            ProviderType = providerType,
            ModelPath = CommandHelpers.GetOption(args, "--model-path")
                ?? (isQwen3Provider ? GetQwen3ModelPath() : null),
            TokenizerPath = CommandHelpers.GetOption(args, "--tokenizer-path")
                ?? (isQwen3Provider ? GetQwen3TokenizerPath() : null),
            EmbeddingModel = model,
            Dimension = CommandHelpers.GetIntOption(args, "--dimension", isQwen3Provider
                ? Qwen3Dimension
                : providerType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase) ? 512 : 16),
            Normalize = !CommandHelpers.HasFlag(args, "--no-normalize"),
            PoolingStrategy = CommandHelpers.GetOption(args, "--pooling") ?? "Mean",
            MaxTokens = CommandHelpers.GetIntOption(args, "--max-tokens", isQwen3Provider ? 8192 : 256),
            BatchSize = CommandHelpers.GetIntOption(args, "--batch-size", isQwen3Provider ? 16 : 32),
            Device = CommandHelpers.GetOption(args, "--device") ?? "cpu",
            Enabled = !providerType.Equals(EmbeddingProviderTypes.Disabled, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string NormalizeEmbeddingProviderType(string value)
    {
        var normalized = value.Trim();
        if (normalized.Equals("deterministic-hash", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("deterministic", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(EmbeddingProviderTypes.DeterministicHash, StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingProviderTypes.DeterministicHash;
        }

        if (normalized.Equals("onnx-local", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("onnx", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingProviderTypes.OnnxLocal;
        }

        if (normalized.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(EmbeddingProviderTypes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingProviderTypes.Disabled;
        }

        return normalized;
    }

    private static bool IsQwen3ProviderRequest(IReadOnlyList<string> args, string? providerOverride = null)
    {
        return IsQwen3ProviderAlias(providerOverride)
               || IsQwen3ProviderAlias(CommandHelpers.GetOption(args, "--provider"))
               || IsQwen3ProviderAlias(CommandHelpers.GetOption(args, "--provider-id"));
    }

    private static bool IsQwen3ProviderAlias(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (value.Equals(Qwen3ProviderAlias, StringComparison.OrdinalIgnoreCase)
                   || value.Equals(Qwen3ProviderId, StringComparison.OrdinalIgnoreCase)
                   || value.Equals(Qwen3ModelId, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetQwen3ModelPath()
    {
        return Path.Combine("src", "ContextCore.Embedding", "Models", Qwen3ProviderId, "model_int8.onnx");
    }

    private static string GetQwen3TokenizerPath()
    {
        return Path.Combine("src", "ContextCore.Embedding", "Models", Qwen3ProviderId, "tokenizer.json");
    }

    private static string Qwen3OutputPath(string fileName)
    {
        return Path.Combine("vector", "providers", "qwen3", fileName);
    }

    private static IReadOnlyList<string> AddOrReplaceOptions(
        IReadOnlyList<string> args,
        params (string Name, string Value)[] options)
    {
        var result = new List<string>(args);
        foreach (var (name, value) in options)
        {
            for (var index = result.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(result[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.RemoveAt(index);
                if (index < result.Count && !result[index].StartsWith("-", StringComparison.Ordinal))
                {
                    result.RemoveAt(index);
                }
            }

            result.Add(name);
            result.Add(value);
        }

        return result;
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

    private static double? GetDoubleOption(IReadOnlyList<string> args, string name)
    {
        var raw = CommandHelpers.GetOption(args, name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        await MirrorReportArtifactAsync(path, text, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonLinesAsync<T>(
        IReadOnlyList<T> rows,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var lines = rows.Select(row => JsonSerializer.Serialize(row, JsonLineOptions));
        await File.WriteAllLinesAsync(fullPath, lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }

        var rows = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = JsonSerializer.Deserialize<T>(line, JsonLineOptions);
            if (value is not null)
            {
                rows.Add(value);
            }
        }

        return rows;
    }

    private static async Task MirrorReportArtifactAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeReportPath(path);
        if (!ShouldRouteLegacyArtifact(relativePath))
        {
            return;
        }

        await new ReportArtifactMirrorWriter(new FileStorageOptions())
            .MirrorAsync(
                relativePath,
                text,
                workspaceId: "default",
                collectionId: "test",
                sourceCommand: "eval",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task MirrorExistingArtifactsAsync(
        CancellationToken cancellationToken,
        params string[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await MirrorReportArtifactAsync(path, text, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NormalizeReportPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetFullPath(Environment.CurrentDirectory);
        if (fullPath.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(current, fullPath).Replace('\\', '/');
        }

        return path.Replace('\\', '/').TrimStart('/');
    }

    private static bool ShouldRouteLegacyArtifact(string relativePath)
        => ReportArtifactRegistry.ShouldMirror(relativePath);

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
            await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
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
            await MirrorReportArtifactAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
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
            await MirrorReportArtifactAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
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

                                private static async Task<PostgresRelationStoreParityReport> RunPostgresRelationStoreParityAsync(
        FileRelationStore fileStore,
        PostgresRelationStore postgresStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relations = new[]
        {
            CreateParityRelation("rel-a", workspaceId, collectionId, "item-a", "item-b", ContextRelationTypes.References, 0.9, 0.95, now, "Active", "Reviewed"),
            CreateParityRelation("rel-b", workspaceId, collectionId, "item-b", "item-c", ContextRelationTypes.DependsOn, 0.7, 0.8, now.AddSeconds(1), "Candidate", "NeedsEvidence"),
            CreateParityRelation("rel-c", workspaceId, collectionId, "item-old", "item-new", ContextRelationTypes.SupersededBy, 1.0, 1.0, now.AddSeconds(2), "Active", "Reviewed"),
            CreateParityRelation("rel-d", workspaceId, collectionId, "item-new", "item-old", ContextRelationTypes.Replaces, 1.0, 1.0, now.AddSeconds(3), "Active", "Reviewed")
        };

        foreach (var relation in relations)
        {
            await fileStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
            await postgresStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        }

        var mismatches = new List<string>();
        var getPassed = RelationEqual(
            await fileStore.GetAsync(workspaceId, collectionId, "rel-a", cancellationToken).ConfigureAwait(false),
            await postgresStore.GetAsync(workspaceId, collectionId, "rel-a", cancellationToken).ConfigureAwait(false));
        AddMismatchIfFalse(mismatches, getPassed, "GetByIdMismatch");

        var listPassed = await CompareQueryAsync(
            fileStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = 20 }, cancellationToken),
            postgresStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = 20 }, cancellationToken),
            "ListMismatch",
            mismatches).ConfigureAwait(false);
        var sourcePassed = await CompareQueryAsync(
            fileStore.QueryBySourceAsync(workspaceId, collectionId, "item-b", cancellationToken),
            postgresStore.QueryBySourceAsync(workspaceId, collectionId, "item-b", cancellationToken),
            "SourceQueryMismatch",
            mismatches).ConfigureAwait(false);
        var targetPassed = await CompareQueryAsync(
            fileStore.QueryByTargetAsync(workspaceId, collectionId, "item-c", cancellationToken),
            postgresStore.QueryByTargetAsync(workspaceId, collectionId, "item-c", cancellationToken),
            "TargetQueryMismatch",
            mismatches).ConfigureAwait(false);
        var typePassed = await CompareQueryAsync(
            fileStore.QueryByTypeAsync(workspaceId, collectionId, ContextRelationTypes.Replaces, cancellationToken),
            postgresStore.QueryByTypeAsync(workspaceId, collectionId, ContextRelationTypes.Replaces, cancellationToken),
            "TypeQueryMismatch",
            mismatches).ConfigureAwait(false);

        var lifecyclePassed = SameIds(
            [.. relations.Where(item => string.Equals(item.Metadata.GetValueOrDefault("lifecycle"), "Active", StringComparison.OrdinalIgnoreCase))],
            await postgresStore.QueryByLifecycleAsync(workspaceId, collectionId, "Active", cancellationToken).ConfigureAwait(false));
        AddMismatchIfFalse(mismatches, lifecyclePassed, "LifecycleQueryMismatch");

        var reviewStatusPassed = SameIds(
            [.. relations.Where(item => string.Equals(item.Metadata.GetValueOrDefault("reviewStatus"), "Reviewed", StringComparison.OrdinalIgnoreCase))],
            await postgresStore.QueryByReviewStatusAsync(workspaceId, collectionId, "Reviewed", cancellationToken).ConfigureAwait(false));
        AddMismatchIfFalse(mismatches, reviewStatusPassed, "ReviewStatusQueryMismatch");

        var replacementPassed = SameIds(
            [relations[2], relations[3]],
            await postgresStore.QueryReplacementChainRelationsAsync(workspaceId, collectionId, "item-old", cancellationToken).ConfigureAwait(false));
        AddMismatchIfFalse(mismatches, replacementPassed, "ReplacementChainQueryMismatch");

        var fileDelete = await fileStore.DeleteAsync(workspaceId, collectionId, "rel-b", cancellationToken).ConfigureAwait(false);
        var postgresDelete = await postgresStore.DeleteAsync(workspaceId, collectionId, "rel-b", cancellationToken).ConfigureAwait(false);
        var deletePassed = fileDelete == postgresDelete
            && await fileStore.GetAsync(workspaceId, collectionId, "rel-b", cancellationToken).ConfigureAwait(false) is null
            && await postgresStore.GetAsync(workspaceId, collectionId, "rel-b", cancellationToken).ConfigureAwait(false) is null;
        AddMismatchIfFalse(mismatches, deletePassed, "DeleteMismatch");

        if (cleanupConfirm)
        {
            foreach (var relation in relations)
            {
                await postgresStore.DeleteAsync(workspaceId, collectionId, relation.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new PostgresRelationStoreParityReport
        {
            ProviderEnabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            FixtureRelationCount = relations.Length,
            GetPassed = getPassed,
            ListPassed = listPassed,
            SourceQueryPassed = sourcePassed,
            TargetQueryPassed = targetPassed,
            TypeQueryPassed = typePassed,
            LifecycleQueryPassed = lifecyclePassed,
            ReviewStatusQueryPassed = reviewStatusPassed,
            ReplacementChainQueryPassed = replacementPassed,
            DeletePassed = deletePassed,
            CleanupPerformed = cleanupConfirm,
            Mismatches = mismatches,
            Diagnostics = cleanupConfirm ? ["CleanupPerformed"] : ["CleanupSkipped"],
            Recommendation = mismatches.Count == 0 ? "ParityPassed" : "ParityMismatch"
        };
    }

            private static async Task<PostgresRelationReviewParityReport> RunPostgresRelationReviewParityAsync(
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relation = CreateParityRelation(
            "review-rel-a",
            workspaceId,
            collectionId,
            "review-source",
            "review-target",
            ContextRelationTypes.References,
            0.8,
            0.9,
            now,
            "Active",
            "Reviewed");
        await postgresRelationStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);

        var reviews = new[]
        {
            CreateReviewRecord("review-a", relation, RelationReviewActions.Review, RelationReviewStatuses.Reviewed, "reviewer-a", "op-a", now),
            CreateReviewRecord("review-b", relation, RelationReviewActions.MarkNeedsEvidence, RelationReviewStatuses.NeedsEvidence, "reviewer-b", "op-b", now.AddSeconds(1))
        };
        var snapshots = new[]
        {
            CreateDiagnosticSnapshot("diag-a", workspaceId, collectionId, relation.Id, relation.SourceId, "MissingEvidence", "Warning", now),
            CreateDiagnosticSnapshot("diag-b", workspaceId, collectionId, relation.Id, relation.TargetId, "LowConfidence", "Info", now.AddSeconds(1))
        };

        foreach (var review in reviews)
        {
            await fileReviewStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);
            await postgresReviewStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);
        }

        foreach (var snapshot in snapshots)
        {
            await fileDiagnosticsStore.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        var mismatches = new List<string>();
        var reviewListPassed = await CompareReviewQueryAsync(
            fileReviewStore.QueryReviewsAsync(relation.Id, cancellationToken),
            postgresReviewStore.QueryReviewsAsync(relation.Id, cancellationToken),
            "ReviewListMismatch",
            mismatches).ConfigureAwait(false);
        var latestReviewPassed = string.Equals(
            (await fileReviewStore.GetLatestReviewAsync(relation.Id, cancellationToken).ConfigureAwait(false))?.ReviewId,
            (await postgresReviewStore.GetLatestReviewAsync(relation.Id, cancellationToken).ConfigureAwait(false))?.ReviewId,
            StringComparison.Ordinal);
        AddMismatchIfFalse(mismatches, latestReviewPassed, "LatestReviewMismatch");

        var reviewStatusFilterPassed = await CompareReviewQueryAsync(
            fileReviewStore.QueryByReviewStatusAsync(workspaceId, collectionId, RelationReviewStatuses.NeedsEvidence, cancellationToken),
            postgresReviewStore.QueryByReviewStatusAsync(workspaceId, collectionId, RelationReviewStatuses.NeedsEvidence, cancellationToken),
            "ReviewStatusFilterMismatch",
            mismatches).ConfigureAwait(false);
        var reviewerFilterPassed = await CompareReviewQueryAsync(
            fileReviewStore.QueryByReviewerAsync(workspaceId, collectionId, "reviewer-a", cancellationToken),
            postgresReviewStore.QueryByReviewerAsync(workspaceId, collectionId, "reviewer-a", cancellationToken),
            "ReviewerFilterMismatch",
            mismatches).ConfigureAwait(false);
        var operationIdFilterPassed = await CompareReviewQueryAsync(
            fileReviewStore.QueryByOperationIdAsync(workspaceId, collectionId, "op-b", cancellationToken),
            postgresReviewStore.QueryByOperationIdAsync(workspaceId, collectionId, "op-b", cancellationToken),
            "OperationIdFilterMismatch",
            mismatches).ConfigureAwait(false);

        var diagnosticsByRelationPassed = await CompareDiagnosticQueryAsync(
            fileDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relation.Id, cancellationToken),
            postgresDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relation.Id, cancellationToken),
            "DiagnosticsByRelationMismatch",
            mismatches).ConfigureAwait(false);
        var diagnosticsByItemPassed = await CompareDiagnosticQueryAsync(
            fileDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, relation.TargetId, cancellationToken),
            postgresDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, relation.TargetId, cancellationToken),
            "DiagnosticsByItemMismatch",
            mismatches).ConfigureAwait(false);
        var diagnosticsKindFilterPassed = await CompareDiagnosticQueryAsync(
            fileDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, "MissingEvidence", cancellationToken),
            postgresDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, "MissingEvidence", cancellationToken),
            "DiagnosticsKindFilterMismatch",
            mismatches).ConfigureAwait(false);
        var diagnosticsSeverityFilterPassed = await CompareDiagnosticQueryAsync(
            fileDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, "Info", cancellationToken),
            postgresDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, "Info", cancellationToken),
            "DiagnosticsSeverityFilterMismatch",
            mismatches).ConfigureAwait(false);

        if (cleanupConfirm)
        {
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresRelationStore.DeleteAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        }

        return new PostgresRelationReviewParityReport
        {
            ProviderEnabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            FixtureReviewCount = reviews.Length,
            FixtureDiagnosticsCount = snapshots.Length,
            ReviewListPassed = reviewListPassed,
            LatestReviewPassed = latestReviewPassed,
            ReviewStatusFilterPassed = reviewStatusFilterPassed,
            ReviewerFilterPassed = reviewerFilterPassed,
            OperationIdFilterPassed = operationIdFilterPassed,
            DiagnosticsByRelationPassed = diagnosticsByRelationPassed,
            DiagnosticsByItemPassed = diagnosticsByItemPassed,
            DiagnosticsKindFilterPassed = diagnosticsKindFilterPassed,
            DiagnosticsSeverityFilterPassed = diagnosticsSeverityFilterPassed,
            CleanupPerformed = cleanupConfirm,
            Mismatches = mismatches,
            Diagnostics = cleanupConfirm ? ["CleanupPerformed"] : ["CleanupSkipped"],
            Recommendation = mismatches.Count == 0 ? "ParityPassed" : "ParityMismatch"
        };
    }

        private static async Task<PostgresRelationGovernanceParityReport> RunPostgresRelationGovernanceParityAsync(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relations = new[]
        {
            CreateParityRelation("gov-rel-a", workspaceId, collectionId, "gov-source-a", "gov-target-a", ContextRelationTypes.References, 0.9, 0.95, now, "Active", "Reviewed"),
            CreateParityRelation("gov-rel-b", workspaceId, collectionId, "gov-source-a", "gov-target-b", ContextRelationTypes.DependsOn, 0.8, 0.85, now.AddSeconds(1), "Candidate", "NeedsEvidence"),
            CreateParityRelation("gov-rel-c", workspaceId, collectionId, "gov-old", "gov-new", ContextRelationTypes.SupersededBy, 1.0, 1.0, now.AddSeconds(2), "Active", "Reviewed"),
            CreateParityRelation("gov-rel-d", workspaceId, collectionId, "gov-new", "gov-old", ContextRelationTypes.Replaces, 1.0, 1.0, now.AddSeconds(3), "Active", "Reviewed")
        };

        foreach (var relation in relations)
        {
            await fileRelationStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
            await postgresRelationStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        }

        var reviews = new[]
        {
            CreateReviewRecord("gov-review-a", relations[0], RelationReviewActions.Review, RelationReviewStatuses.Reviewed, "reviewer-a", "gov-op-a", now.AddSeconds(4)),
            CreateReviewRecord("gov-review-b", relations[1], RelationReviewActions.MarkNeedsEvidence, RelationReviewStatuses.NeedsEvidence, "reviewer-b", "gov-op-b", now.AddSeconds(5))
        };
        var snapshots = new[]
        {
            CreateDiagnosticSnapshot("gov-diag-a", workspaceId, collectionId, relations[0].Id, relations[0].SourceId, "MissingEvidence", "Warning", now.AddSeconds(6)),
            CreateDiagnosticSnapshot("gov-diag-b", workspaceId, collectionId, relations[1].Id, relations[1].TargetId, "LowConfidence", "Info", now.AddSeconds(7))
        };

        foreach (var review in reviews)
        {
            await fileReviewStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);
            await postgresReviewStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);
        }

        foreach (var snapshot in snapshots)
        {
            await fileDiagnosticsStore.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        var mismatches = new List<string>();
        var relationChecks = new[]
        {
            RelationEqual(
                await fileRelationStore.GetAsync(workspaceId, collectionId, relations[0].Id, cancellationToken).ConfigureAwait(false),
                await postgresRelationStore.GetAsync(workspaceId, collectionId, relations[0].Id, cancellationToken).ConfigureAwait(false)),
            await CompareQueryAsync(
                fileRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = 20 }, cancellationToken),
                postgresRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = 20 }, cancellationToken),
                "GovernanceRelationListMismatch",
                mismatches).ConfigureAwait(false),
            await CompareQueryAsync(
                fileRelationStore.QueryBySourceAsync(workspaceId, collectionId, "gov-source-a", cancellationToken),
                postgresRelationStore.QueryBySourceAsync(workspaceId, collectionId, "gov-source-a", cancellationToken),
                "GovernanceSourceQueryMismatch",
                mismatches).ConfigureAwait(false),
            await CompareQueryAsync(
                fileRelationStore.QueryByTargetAsync(workspaceId, collectionId, "gov-target-b", cancellationToken),
                postgresRelationStore.QueryByTargetAsync(workspaceId, collectionId, "gov-target-b", cancellationToken),
                "GovernanceTargetQueryMismatch",
                mismatches).ConfigureAwait(false),
            await CompareQueryAsync(
                fileRelationStore.QueryByTypeAsync(workspaceId, collectionId, ContextRelationTypes.Replaces, cancellationToken),
                postgresRelationStore.QueryByTypeAsync(workspaceId, collectionId, ContextRelationTypes.Replaces, cancellationToken),
                "GovernanceTypeQueryMismatch",
                mismatches).ConfigureAwait(false),
            SameIds(
                [.. relations.Where(item => string.Equals(item.Metadata.GetValueOrDefault("lifecycle"), "Active", StringComparison.OrdinalIgnoreCase))],
                await postgresRelationStore.QueryByLifecycleAsync(workspaceId, collectionId, "Active", cancellationToken).ConfigureAwait(false)),
            SameIds(
                [.. relations.Where(item => string.Equals(item.Metadata.GetValueOrDefault("reviewStatus"), "Reviewed", StringComparison.OrdinalIgnoreCase))],
                await postgresRelationStore.QueryByReviewStatusAsync(workspaceId, collectionId, "Reviewed", cancellationToken).ConfigureAwait(false)),
            SameIds(
                [relations[2], relations[3]],
                await postgresRelationStore.QueryReplacementChainRelationsAsync(workspaceId, collectionId, "gov-old", cancellationToken).ConfigureAwait(false))
        };
        AddMismatchIfFalse(mismatches, relationChecks[0], "GovernanceGetByIdMismatch");
        AddMismatchIfFalse(mismatches, relationChecks[5], "GovernanceLifecycleQueryMismatch");
        AddMismatchIfFalse(mismatches, relationChecks[6], "GovernanceReviewStatusQueryMismatch");
        AddMismatchIfFalse(mismatches, relationChecks[7], "GovernanceReplacementChainMismatch");

        var reviewChecks = new[]
        {
            await CompareReviewQueryAsync(
                fileReviewStore.QueryReviewsAsync(relations[0].Id, cancellationToken),
                postgresReviewStore.QueryReviewsAsync(relations[0].Id, cancellationToken),
                "GovernanceReviewListMismatch",
                mismatches).ConfigureAwait(false),
            string.Equals(
                (await fileReviewStore.GetLatestReviewAsync(relations[1].Id, cancellationToken).ConfigureAwait(false))?.ReviewId,
                (await postgresReviewStore.GetLatestReviewAsync(relations[1].Id, cancellationToken).ConfigureAwait(false))?.ReviewId,
                StringComparison.Ordinal),
            await CompareReviewQueryAsync(
                fileReviewStore.QueryByReviewStatusAsync(workspaceId, collectionId, RelationReviewStatuses.NeedsEvidence, cancellationToken),
                postgresReviewStore.QueryByReviewStatusAsync(workspaceId, collectionId, RelationReviewStatuses.NeedsEvidence, cancellationToken),
                "GovernanceReviewStatusFilterMismatch",
                mismatches).ConfigureAwait(false)
        };
        AddMismatchIfFalse(mismatches, reviewChecks[1], "GovernanceLatestReviewMismatch");

        var diagnosticsChecks = new[]
        {
            await CompareDiagnosticQueryAsync(
                fileDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relations[0].Id, cancellationToken),
                postgresDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relations[0].Id, cancellationToken),
                "GovernanceDiagnosticsByRelationMismatch",
                mismatches).ConfigureAwait(false),
            await CompareDiagnosticQueryAsync(
                fileDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, relations[1].TargetId, cancellationToken),
                postgresDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, relations[1].TargetId, cancellationToken),
                "GovernanceDiagnosticsByItemMismatch",
                mismatches).ConfigureAwait(false),
            await CompareDiagnosticQueryAsync(
                fileDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, "MissingEvidence", cancellationToken),
                postgresDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, "MissingEvidence", cancellationToken),
                "GovernanceDiagnosticsKindMismatch",
                mismatches).ConfigureAwait(false),
            await CompareDiagnosticQueryAsync(
                fileDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, "Info", cancellationToken),
                postgresDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, "Info", cancellationToken),
                "GovernanceDiagnosticsSeverityMismatch",
                mismatches).ConfigureAwait(false)
        };

        if (cleanupConfirm)
        {
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            foreach (var relation in relations)
            {
                await postgresRelationStore.DeleteAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        var relationPassed = relationChecks.All(static item => item);
        var reviewPassed = reviewChecks.All(static item => item);
        var diagnosticsPassed = diagnosticsChecks.All(static item => item);
        var governancePassed = relationPassed && reviewPassed && diagnosticsPassed && mismatches.Count == 0;
        var blockedReasons = BuildPostgresRelationGovernanceBlockedReasons(
            providerEnabled: true,
            governancePassed,
            cleanupConfirm,
            useForRuntime: false,
            mismatches);

        return new PostgresRelationGovernanceParityReport
        {
            ProviderEnabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RelationParityPassed = relationPassed,
            ReviewParityPassed = reviewPassed,
            DiagnosticsParityPassed = diagnosticsPassed,
            GovernanceParityPassed = governancePassed,
            CleanupPerformed = cleanupConfirm,
            CanDualWrite = governancePassed && cleanupConfirm,
            CanShadowRead = false,
            CanRuntimeSwitch = false,
            Mismatches = mismatches,
            BlockedReasons = blockedReasons,
            Diagnostics = cleanupConfirm ? ["CleanupPerformed", "RuntimeProviderStillFileSystem"] : ["CleanupSkipped", "RuntimeProviderStillFileSystem"],
            Recommendation = BuildPostgresRelationGovernanceRecommendation(providerEnabled: true, governancePassed, cleanupConfirm, mismatches)
        };
    }

        private static async Task<PostgresRelationGovernanceReadinessGateReport> BuildPostgresRelationGovernanceReadinessGateAsync(
        CancellationToken cancellationToken)
    {
        var storage = await ReadJsonFileAsync<PostgresOperationalStoreDiagnostics>(
            Path.Combine("storage", "postgres-storage-diagnostics.json"),
            cancellationToken).ConfigureAwait(false);
        var relation = await ReadJsonFileAsync<PostgresRelationStoreDiagnostics>(
            Path.Combine("storage", "postgres", "postgres-relation-store-diagnostics.json"),
            cancellationToken).ConfigureAwait(false);
        var relationParity = await ReadJsonFileAsync<PostgresRelationStoreParityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-store-parity-report.json"),
            cancellationToken).ConfigureAwait(false);
        var review = await ReadJsonFileAsync<PostgresRelationReviewProviderDiagnostics>(
            Path.Combine("storage", "postgres", "postgres-relation-review-diagnostics.json"),
            cancellationToken).ConfigureAwait(false);
        var reviewParity = await ReadJsonFileAsync<PostgresRelationReviewParityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-review-parity-report.json"),
            cancellationToken).ConfigureAwait(false);
        var governanceParity = await ReadJsonFileAsync<PostgresRelationGovernanceParityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-parity-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var providerEnabled = storage?.ProviderEnabled == true && relation?.ProviderEnabled == true && review?.ProviderEnabled == true;
        AddReasonIfFalse(blocked, providerEnabled, "NotConfigured");
        var storageReady = string.Equals(storage?.Status, "Ready", StringComparison.OrdinalIgnoreCase);
        AddReasonIfFalse(blocked, storageReady, "PostgresStorageNotReady");
        var schemaReady = IsPostgresSchemaAtLeast(storage?.CurrentSchemaVersion ?? relation?.SchemaVersion ?? review?.SchemaVersion, 4);
        AddReasonIfFalse(blocked, schemaReady, "SchemaNotReady");
        AddReasonIfFalse(blocked, relation?.RelationTableExists == true, "RelationTableMissing");
        AddReasonIfFalse(blocked, relation?.RelationReviewsTableExists == true || review?.RelationReviewsTableExists == true, "RelationReviewsTableMissing");
        AddReasonIfFalse(blocked, review?.RelationDiagnosticsTableExists == true, "RelationDiagnosticsTableMissing");

        var missingIndexCount = (relation?.MissingRequiredIndexes.Count ?? 0) + (review?.MissingRequiredIndexes.Count ?? 0);
        if (storage?.SchemaVerification is not null)
        {
            missingIndexCount += storage.SchemaVerification.MissingIndexCount;
        }

        AddReasonIfFalse(blocked, missingIndexCount == 0, "MissingRequiredIndexes");
        var relationStoreParityPassed = string.Equals(relationParity?.Recommendation, "ParityPassed", StringComparison.OrdinalIgnoreCase)
            && relationParity?.Mismatches.Count == 0
            && relationParity?.CleanupPerformed == true;
        AddReasonIfFalse(blocked, relationStoreParityPassed, "RelationStoreParityNotPassed");
        var relationReviewParityPassed = string.Equals(reviewParity?.Recommendation, "ParityPassed", StringComparison.OrdinalIgnoreCase)
            && reviewParity?.Mismatches.Count == 0
            && reviewParity?.CleanupPerformed == true;
        AddReasonIfFalse(blocked, relationReviewParityPassed, "RelationReviewParityNotPassed");
        var diagnosticsParityPassed = reviewParity?.DiagnosticsByRelationPassed == true
            && reviewParity?.DiagnosticsByItemPassed == true
            && reviewParity?.DiagnosticsKindFilterPassed == true
            && reviewParity?.DiagnosticsSeverityFilterPassed == true;
        AddReasonIfFalse(blocked, diagnosticsParityPassed, "DiagnosticsParityNotPassed");
        var governancePassed = governanceParity?.GovernanceParityPassed == true
            && governanceParity.Mismatches.Count == 0
            && governanceParity.CleanupPerformed;
        AddReasonIfFalse(blocked, governancePassed, "GovernanceParityNotPassed");

        var mismatchCount = (relationParity?.Mismatches.Count ?? 0)
            + (reviewParity?.Mismatches.Count ?? 0)
            + (governanceParity?.Mismatches.Count ?? 0);
        AddReasonIfFalse(blocked, mismatchCount == 0, "ParityMismatch");
        var cleanupPerformed = relationParity?.CleanupPerformed == true
            && reviewParity?.CleanupPerformed == true
            && governanceParity?.CleanupPerformed == true;
        AddReasonIfFalse(blocked, cleanupPerformed, "CleanupNotPerformed");
        var useForRuntime = relation?.UseForRuntime == true || review?.UseForRuntime == true;
        AddReasonIfFalse(blocked, !useForRuntime, "UseForRuntimeMustRemainFalse");

        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
            && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        AddReasonIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (!p15Passed)
        {
            diagnostics.Add("RunScriptsEvalGateP15First");
        }

        var passed = blocked.Count == 0;
        return new PostgresRelationGovernanceReadinessGateReport
        {
            ProviderEnabled = providerEnabled,
            Passed = passed,
            StorageReady = storageReady,
            SchemaVersion = storage?.CurrentSchemaVersion ?? relation?.SchemaVersion ?? review?.SchemaVersion,
            SchemaVersionReady = schemaReady,
            RelationTableExists = relation?.RelationTableExists == true,
            RelationReviewsTableExists = relation?.RelationReviewsTableExists == true || review?.RelationReviewsTableExists == true,
            RelationDiagnosticsTableExists = review?.RelationDiagnosticsTableExists == true,
            MissingRequiredIndexCount = missingIndexCount,
            RelationStoreParityPassed = relationStoreParityPassed,
            RelationReviewParityPassed = relationReviewParityPassed,
            DiagnosticsParityPassed = diagnosticsParityPassed,
            GovernanceParityPassed = governancePassed,
            MismatchCount = mismatchCount,
            CleanupPerformed = cleanupPerformed,
            UseForRuntime = useForRuntime,
            P15GateExpected = p15Passed,
            CanDualWrite = passed && governanceParity?.CanDualWrite == true,
            CanShadowRead = false,
            CanRuntimeSwitch = false,
            BlockedReasons = blocked,
            Diagnostics = diagnostics,
            Recommendation = passed ? "ReadyForDualWrite" : BuildReadinessGateRecommendation(blocked)
        };
    }

        private static async Task<PostgresRelationDualWriteSmokeReport> RunPostgresRelationDualWriteSmokeAsync(
        RelationGovernanceDualWriteCoordinator coordinator,
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        IReadOnlyList<RelationGovernanceDualWriteTrace> traces,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relation = CreateParityRelation("dual-rel-a", workspaceId, collectionId, "dual-source", "dual-target", ContextRelationTypes.References, 0.9, 0.95, now, "Active", "Reviewed");
        var review = CreateReviewRecord("dual-review-a", relation, RelationReviewActions.Review, RelationReviewStatuses.Reviewed, "dual-reviewer", "dual-op-review", now.AddSeconds(1));
        var diagnostics = CreateDiagnosticSnapshot("dual-diag-a", workspaceId, collectionId, relation.Id, relation.TargetId, "MissingEvidence", "Warning", now.AddSeconds(2));

        await coordinator.UpsertRelationAsync("dual-op-relation", relation, cancellationToken).ConfigureAwait(false);
        await coordinator.AppendReviewAsync("dual-op-review", review, cancellationToken).ConfigureAwait(false);
        await coordinator.WriteDiagnosticsAsync("dual-op-diagnostics", diagnostics, cancellationToken).ConfigureAwait(false);

        var mismatches = new List<string>();
        var relationPassed = RelationEqual(
            await fileRelationStore.GetAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false),
            await postgresRelationStore.GetAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false));
        AddMismatchIfFalse(mismatches, relationPassed, "DualWriteRelationMismatch");

        var reviewPassed = await CompareReviewQueryAsync(
            fileReviewStore.QueryReviewsAsync(relation.Id, cancellationToken),
            postgresReviewStore.QueryReviewsAsync(relation.Id, cancellationToken),
            "DualWriteReviewMismatch",
            mismatches).ConfigureAwait(false);
        var diagnosticsPassed = await CompareDiagnosticQueryAsync(
            fileDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relation.Id, cancellationToken),
            postgresDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relation.Id, cancellationToken),
            "DualWriteDiagnosticsMismatch",
            mismatches).ConfigureAwait(false);

        if (cleanupConfirm)
        {
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresRelationStore.DeleteAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        }

        var traceFailures = traces.Count(item => !item.PostgresWriteSucceeded || item.MismatchDetected);
        return new PostgresRelationDualWriteSmokeReport
        {
            ProviderEnabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RelationDualWritePassed = relationPassed,
            ReviewDualWritePassed = reviewPassed,
            DiagnosticsDualWritePassed = diagnosticsPassed,
            CleanupPerformed = cleanupConfirm,
            TraceCount = traces.Count,
            Mismatches = mismatches,
            Diagnostics = cleanupConfirm ? ["CleanupPerformed", "RuntimeProviderStillFileSystem"] : ["CleanupSkipped", "RuntimeProviderStillFileSystem"],
            Recommendation = mismatches.Count == 0 && traceFailures == 0 && cleanupConfirm
                ? "ReadyForShadowRead"
                : mismatches.Count > 0
                    ? "BlockedByMismatch"
                    : "NeedsMoreTraces"
        };
    }

        private static async Task<PostgresRelationDualWriteQualityReport> BuildPostgresRelationDualWriteQualityReportAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            return new PostgresRelationDualWriteQualityReport
            {
                Diagnostics = ["TraceFileMissing"],
                Recommendation = "NeedsMoreTraces"
            };
        }

        var traces = new List<RelationGovernanceDualWriteTrace>();
        foreach (var line in await File.ReadAllLinesAsync(inputPath, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trace = JsonSerializer.Deserialize<RelationGovernanceDualWriteTrace>(line, JsonLineOptions);
            if (trace is not null)
            {
                traces.Add(trace);
            }
        }

        var traceCount = traces.Count;
        var postgresFailures = traces.Count(static item => !item.PostgresWriteSucceeded);
        var mismatches = traces.Count(static item => item.MismatchDetected);
        return new PostgresRelationDualWriteQualityReport
        {
            TraceCount = traceCount,
            FileSystemWriteSuccessCount = traces.Count(static item => item.FileSystemWriteSucceeded),
            PostgresWriteSuccessCount = traces.Count(static item => item.PostgresWriteSucceeded),
            PostgresWriteFailureCount = postgresFailures,
            MismatchCount = mismatches,
            FallbackCount = traces.Count(static item => item.FallbackUsed),
            AverageDurationMs = traceCount == 0 ? 0 : traces.Average(static item => item.DurationMs),
            Recommendation = traceCount == 0
                ? "NeedsMoreTraces"
                : mismatches > 0
                    ? "BlockedByMismatch"
                    : postgresFailures > 0
                        ? "BlockedByPostgresFailure"
                        : "ReadyForShadowRead"
        };
    }

        private static async Task<PostgresRelationShadowReadSmokeReport> RunPostgresRelationShadowReadSmokeAsync(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        IReadOnlyList<RelationGovernanceShadowReadTrace> traces,
        Func<RelationGovernanceShadowReadTrace, CancellationToken, Task> traceSink,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relation = CreateParityRelation("shadow-rel-a", workspaceId, collectionId, "shadow-source", "shadow-target", ContextRelationTypes.SupersededBy, 0.9, 0.95, now, "Active", "Reviewed");
        var inverse = CreateParityRelation("shadow-rel-b", workspaceId, collectionId, "shadow-target", "shadow-source", ContextRelationTypes.Replaces, 0.8, 0.9, now.AddMilliseconds(1), "Active", "Reviewed");
        var review = CreateReviewRecord("shadow-review-a", relation, RelationReviewActions.Review, RelationReviewStatuses.Reviewed, "shadow-reviewer", "shadow-op-review", now.AddSeconds(1));
        var diagnostics = CreateDiagnosticSnapshot("shadow-diag-a", workspaceId, collectionId, relation.Id, relation.TargetId, "MissingEvidence", "Warning", now.AddSeconds(2));

        var dualCoordinator = new RelationGovernanceDualWriteCoordinator(
            fileRelationStore,
            fileReviewStore,
            fileDiagnosticsStore,
            postgresRelationStore,
            postgresReviewStore,
            postgresDiagnosticsStore,
            new RelationGovernanceDualWriteOptions
            {
                Enabled = true,
                WritePostgres = true,
                TraceEnabled = false,
                FallbackOnPostgresFailure = true
            },
            static (_, _) => Task.CompletedTask);

        await dualCoordinator.UpsertRelationAsync("shadow-seed-relation-a", relation, cancellationToken).ConfigureAwait(false);
        await dualCoordinator.UpsertRelationAsync("shadow-seed-relation-b", inverse, cancellationToken).ConfigureAwait(false);
        await dualCoordinator.AppendReviewAsync("shadow-seed-review", review, cancellationToken).ConfigureAwait(false);
        await dualCoordinator.WriteDiagnosticsAsync("shadow-seed-diagnostics", diagnostics, cancellationToken).ConfigureAwait(false);

        var shadowCoordinator = new RelationGovernanceShadowReadCoordinator(
            fileRelationStore,
            fileReviewStore,
            fileDiagnosticsStore,
            postgresRelationStore,
            postgresReviewStore,
            postgresDiagnosticsStore,
            new RelationGovernanceShadowReadOptions
            {
                Enabled = true,
                ReadPostgres = true,
                TraceEnabled = true,
                CompareResults = true,
                FailOnMismatch = false,
                MaxTraceItems = 100
            },
            traceSink);

        await shadowCoordinator.GetRelationAsync("shadow-read-get", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.ListRelationsAsync("shadow-read-list", workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryBySourceAsync("shadow-read-source", workspaceId, collectionId, relation.SourceId, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryByTargetAsync("shadow-read-target", workspaceId, collectionId, relation.TargetId, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryByTypeAsync("shadow-read-type", workspaceId, collectionId, relation.RelationType, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryByLifecycleAsync("shadow-read-lifecycle", workspaceId, collectionId, "Active", cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryByReviewStatusAsync("shadow-read-review-status", workspaceId, collectionId, "Reviewed", cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryReplacementChainAsync("shadow-read-replacement", workspaceId, collectionId, relation.SourceId, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.GetLatestReviewAsync("shadow-read-review-latest", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryReviewsAsync("shadow-read-review-list", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryReviewsByStatusAsync("shadow-read-review-filter", workspaceId, collectionId, RelationReviewStatuses.Reviewed, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryDiagnosticsByRelationAsync("shadow-read-diagnostics-relation", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryDiagnosticsByItemAsync("shadow-read-diagnostics-item", workspaceId, collectionId, relation.TargetId, cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryDiagnosticsByKindAsync("shadow-read-diagnostics-kind", workspaceId, collectionId, "MissingEvidence", cancellationToken).ConfigureAwait(false);
        await shadowCoordinator.QueryDiagnosticsBySeverityAsync("shadow-read-diagnostics-severity", workspaceId, collectionId, "Warning", cancellationToken).ConfigureAwait(false);

        if (cleanupConfirm)
        {
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresRelationStore.DeleteAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
            await postgresRelationStore.DeleteAsync(workspaceId, collectionId, inverse.Id, cancellationToken).ConfigureAwait(false);
        }

        var mismatches = traces
            .Where(static trace => trace.MismatchDetected)
            .Select(trace => $"{trace.ReadKind}:{trace.MismatchReason}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = traces.Count(static trace => !trace.PostgresReadSucceeded);
        return new PostgresRelationShadowReadSmokeReport
        {
            ProviderEnabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            TraceCount = traces.Count,
            CleanupPerformed = cleanupConfirm,
            Mismatches = mismatches,
            Diagnostics = cleanupConfirm ? ["CleanupPerformed", "RuntimeProviderStillFileSystem"] : ["CleanupSkipped", "RuntimeProviderStillFileSystem"],
            Recommendation = traces.Count == 0
                ? "NeedsMoreTraces"
                : mismatches.Length > 0
                    ? "BlockedByMismatch"
                    : failures > 0
                        ? "BlockedByPostgresFailure"
                        : "ReadyForGuardedProviderSwitch"
        };
    }

        private static async Task<PostgresRelationShadowReadQualityReport> BuildPostgresRelationShadowReadQualityReportAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            return new PostgresRelationShadowReadQualityReport
            {
                Diagnostics = ["TraceFileMissing"],
                Recommendation = "NeedsMoreTraces"
            };
        }

        var traces = new List<RelationGovernanceShadowReadTrace>();
        foreach (var line in await File.ReadAllLinesAsync(inputPath, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trace = JsonSerializer.Deserialize<RelationGovernanceShadowReadTrace>(line, JsonLineOptions);
            if (trace is not null)
            {
                traces.Add(trace);
            }
        }

        var traceCount = traces.Count;
        var postgresFailures = traces.Count(static item => !item.PostgresReadSucceeded);
        var mismatches = traces.Count(static item => item.MismatchDetected);
        return new PostgresRelationShadowReadQualityReport
        {
            TraceCount = traceCount,
            FileSystemReadSuccessCount = traces.Count(static item => item.FileSystemReadSucceeded),
            PostgresReadSuccessCount = traces.Count(static item => item.PostgresReadSucceeded),
            PostgresReadFailureCount = postgresFailures,
            MismatchCount = mismatches,
            FallbackCount = traces.Count(static item => item.FallbackUsed),
            AverageFileSystemReadMs = traceCount == 0 ? 0 : traces.Average(static item => item.FileSystemDurationMs),
            AveragePostgresReadMs = traceCount == 0 ? 0 : traces.Average(static item => item.PostgresDurationMs),
            Recommendation = traceCount == 0
                ? "NeedsMoreTraces"
                : mismatches > 0
                    ? "BlockedByMismatch"
                    : postgresFailures > 0
                        ? "BlockedByPostgresFailure"
                        : "ReadyForGuardedProviderSwitch"
        };
    }

        private static async Task<PostgresRelationProviderSwitchSmokeReport> RunPostgresRelationProviderSwitchSmokeAsync(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        bool readinessGatePassed,
        bool shadowReadQualityReady,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var relation = CreateParityRelation("switch-rel-a", workspaceId, collectionId, "switch-source", "switch-target", ContextRelationTypes.References, 0.9, 0.95, now, "Active", "Reviewed");
        var review = CreateReviewRecord("switch-review-a", relation, RelationReviewActions.Review, RelationReviewStatuses.Reviewed, "switch-reviewer", "switch-op-review", now.AddSeconds(1));
        var diagnostics = CreateDiagnosticSnapshot("switch-diag-a", workspaceId, collectionId, relation.Id, relation.TargetId, "MissingEvidence", "Warning", now.AddSeconds(2));
        var router = new RelationGovernanceProviderRouter(
            fileRelationStore,
            fileReviewStore,
            fileDiagnosticsStore,
            postgresRelationStore,
            postgresReviewStore,
            postgresDiagnosticsStore,
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                AllowedWorkspaces = [workspaceId],
                AllowedCollections = [collectionId],
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true,
                FailClosedOnMismatch = true,
                RequireReadinessGate = true
            },
            readinessGatePassed,
            shadowReadQualityReady,
            traceSink);

        var diagnosticsList = new List<string>();
        var mismatches = new List<string>();
        var writePassed = false;
        var postgresPrimaryReadPassed = false;
        var fileSystemFallbackPassed = false;
        try
        {
            await router.SaveRelationAsync("switch-write-relation", relation, cancellationToken).ConfigureAwait(false);
            await router.AppendReviewAsync("switch-write-review", review, cancellationToken).ConfigureAwait(false);
            await router.WriteDiagnosticsAsync("switch-write-diagnostics", diagnostics, cancellationToken).ConfigureAwait(false);
            writePassed = true;

            var relationRead = await router.GetRelationAsync("switch-read-relation", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
            var reviews = await router.QueryReviewsAsync("switch-read-review", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
            var diagnosticSnapshots = await router.QueryDiagnosticsByRelationAsync("switch-read-diagnostics", workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
            postgresPrimaryReadPassed = RelationEqual(relation, relationRead)
                                        && reviews.Any(item => string.Equals(item.ReviewId, review.ReviewId, StringComparison.OrdinalIgnoreCase))
                                        && diagnosticSnapshots.Any(item => string.Equals(item.DiagnosticId, diagnostics.DiagnosticId, StringComparison.OrdinalIgnoreCase));
            AddMismatchIfFalse(mismatches, postgresPrimaryReadPassed, "PostgresPrimaryReadMismatch");

            var fallbackRelation = await fileRelationStore.GetAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
            fileSystemFallbackPassed = RelationEqual(relation, fallbackRelation);
            AddMismatchIfFalse(mismatches, fileSystemFallbackPassed, "FileSystemFallbackUnavailable");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or TimeoutException or IOException)
        {
            diagnosticsList.Add($"ProviderSwitchSmokeFailed:{ex.GetType().Name}");
        }

        if (cleanupConfirm)
        {
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresRelationStore.DeleteAsync(workspaceId, collectionId, relation.Id, cancellationToken).ConfigureAwait(false);
        }

        var traceMismatchCount = traces.Count(static trace => trace.MismatchDetected);
        var postgresErrors = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
        if (traceMismatchCount > 0)
        {
            mismatches.Add("ProviderSwitchTraceMismatch");
        }

        var comparisonTraceRecorded = traces.Count > 0 && traces.Any(static trace =>
            string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase));
        var recommendation = !readinessGatePassed || !shadowReadQualityReady
            ? "GateNotReady"
            : mismatches.Count > 0 || traceMismatchCount > 0
                ? "BlockedByMismatch"
                : postgresErrors > 0
                    ? "BlockedByPostgresFailure"
                    : writePassed && postgresPrimaryReadPassed && fileSystemFallbackPassed && comparisonTraceRecorded && cleanupConfirm
                        ? "ReadyForGuardedProviderSwitch"
                        : "NeedsMoreTraces";

        return new PostgresRelationProviderSwitchSmokeReport
        {
            ProviderEnabled = true,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
            WritePassed = writePassed,
            PostgresPrimaryReadPassed = postgresPrimaryReadPassed,
            FileSystemFallbackPassed = fileSystemFallbackPassed,
            ComparisonTraceRecorded = comparisonTraceRecorded,
            CleanupPerformed = cleanupConfirm,
            TraceCount = traces.Count,
            Mismatches = mismatches,
            Diagnostics = diagnosticsList.Count == 0
                ? ["RuntimeProviderStillFileSystem", cleanupConfirm ? "CleanupPerformed" : "CleanupSkipped"]
                : diagnosticsList,
            Recommendation = recommendation
        };
    }

        private static async Task<PostgresRelationProviderSwitchGateReport> BuildPostgresRelationProviderSwitchGateAsync(
        CancellationToken cancellationToken)
    {
        var readiness = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var dualWrite = await ReadJsonFileAsync<PostgresRelationDualWriteQualityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-dual-write-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var shadowRead = await ReadJsonFileAsync<PostgresRelationShadowReadQualityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-shadow-read-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var smoke = await ReadJsonFileAsync<PostgresRelationProviderSwitchSmokeReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var governanceReady = readiness?.Passed == true;
        var dualWriteReady = string.Equals(dualWrite?.Recommendation, "ReadyForShadowRead", StringComparison.OrdinalIgnoreCase);
        var shadowReadReady = string.Equals(shadowRead?.Recommendation, "ReadyForGuardedProviderSwitch", StringComparison.OrdinalIgnoreCase);
        var mismatchCount = (dualWrite?.MismatchCount ?? 0)
                            + (shadowRead?.MismatchCount ?? 0)
                            + (smoke?.Mismatches.Count ?? 0);
        var postgresReadFailures = shadowRead?.PostgresReadFailureCount ?? 0;
        var postgresWriteFailures = dualWrite?.PostgresWriteFailureCount ?? 0;
        var fallbackTested = smoke?.FileSystemFallbackPassed == true;
        var allowlistConfigured = !string.IsNullOrWhiteSpace(smoke?.WorkspaceId)
                                  && !string.IsNullOrWhiteSpace(smoke?.CollectionId);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        AddReasonIfFalse(blocked, governanceReady, "GovernanceReadinessGateNotPassed");
        AddReasonIfFalse(blocked, dualWriteReady, "DualWriteQualityNotReady");
        AddReasonIfFalse(blocked, shadowReadReady, "ShadowReadQualityNotReady");
        AddReasonIfFalse(blocked, mismatchCount == 0, "MismatchDetected");
        AddReasonIfFalse(blocked, postgresReadFailures == 0, "PostgresReadFailureDetected");
        AddReasonIfFalse(blocked, postgresWriteFailures == 0, "PostgresWriteFailureDetected");
        AddReasonIfFalse(blocked, fallbackTested, "FallbackPathNotTested");
        AddReasonIfFalse(blocked, allowlistConfigured, "AllowlistScopeMissing");
        AddReasonIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (readiness is null)
        {
            diagnostics.Add("RunPostgresRelationGovernanceReadinessGateFirst");
        }

        if (dualWrite is null)
        {
            diagnostics.Add("RunPostgresRelationDualWriteQualityFirst");
        }

        if (shadowRead is null)
        {
            diagnostics.Add("RunPostgresRelationShadowReadQualityFirst");
        }

        if (smoke is null)
        {
            diagnostics.Add("RunPostgresRelationProviderSwitchSmokeFirst");
        }

        var passed = blocked.Count == 0;
        return new PostgresRelationProviderSwitchGateReport
        {
            Passed = passed,
            GovernanceReadinessGatePassed = governanceReady,
            DualWriteQualityReady = dualWriteReady,
            ShadowReadQualityReady = shadowReadReady,
            MismatchCount = mismatchCount,
            PostgresReadFailureCount = postgresReadFailures,
            PostgresWriteFailureCount = postgresWriteFailures,
            FallbackPathTested = fallbackTested,
            AllowlistScopeConfigured = allowlistConfigured,
            P15GatePassed = p15Passed,
            BlockedReasons = blocked,
            Diagnostics = diagnostics,
            Recommendation = passed
                ? "ReadyForGuardedProviderSwitch"
                : mismatchCount > 0
                    ? "BlockedByMismatch"
                : postgresReadFailures > 0 || postgresWriteFailures > 0
                    ? "BlockedByPostgresFailure"
                        : "GateNotReady"
        };
    }

                private static async Task<PostgresRelationScopedServiceModeGateReport> BuildPostgresRelationScopedServiceModeGateAsync(
        CancellationToken cancellationToken)
    {
        var readiness = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var switchGate = await ReadJsonFileAsync<PostgresRelationProviderSwitchGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var canary = await ReadJsonFileAsync<PostgresRelationRuntimeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var smoke = await ReadJsonFileAsync<PostgresRelationScopedServiceModeSmokeReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var readinessPassed = readiness?.Passed == true;
        var switchPassed = switchGate?.Passed == true;
        var canaryPassed = string.Equals(canary?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase);
        var allowlistConfigured = !string.IsNullOrWhiteSpace(smoke?.WorkspaceId)
                                  && !string.IsNullOrWhiteSpace(smoke?.CollectionId);
        var nonAllowlistedFileSystem = smoke?.NonAllowlistedScopeUsedFileSystem == true;
        var mismatchCount = (smoke?.MismatchCount ?? 0) + (canary?.MismatchCount ?? 0);
        var postgresFailureCount = (smoke?.PostgresFailureCount ?? 0) + (canary?.PostgresFailureCount ?? 0);
        var fallbackTested = smoke?.FallbackTested == true;
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        AddReasonIfFalse(blocked, readinessPassed, "GovernanceReadinessGateNotPassed");
        AddReasonIfFalse(blocked, switchPassed, "ProviderSwitchGateNotPassed");
        AddReasonIfFalse(blocked, canaryPassed, "RuntimeCanaryNotPassed");
        AddReasonIfFalse(blocked, allowlistConfigured, "ScopedAllowlistMissing");
        AddReasonIfFalse(blocked, nonAllowlistedFileSystem, "NonAllowlistedScopeNotFileSystem");
        AddReasonIfFalse(blocked, mismatchCount == 0, "MismatchDetected");
        AddReasonIfFalse(blocked, postgresFailureCount == 0, "PostgresFailureDetected");
        AddReasonIfFalse(blocked, fallbackTested, "FallbackPathNotTested");
        AddReasonIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (smoke is null)
        {
            diagnostics.Add("RunPostgresRelationScopedServiceModeSmokeFirst");
        }

        var passed = blocked.Count == 0;
        return new PostgresRelationScopedServiceModeGateReport
        {
            Passed = passed,
            GovernanceReadinessGatePassed = readinessPassed,
            ProviderSwitchGatePassed = switchPassed,
            RuntimeCanaryPassed = canaryPassed,
            ScopedAllowlistConfigured = allowlistConfigured,
            NonAllowlistedScopeRemainsFileSystem = nonAllowlistedFileSystem,
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            FallbackTested = fallbackTested,
            P15GatePassed = p15Passed,
            BlockedReasons = blocked,
            Diagnostics = diagnostics,
            Recommendation = passed
                ? "ReadyForScopedServiceMode"
                : mismatchCount > 0
                    ? "BlockedByMismatch"
                    : postgresFailureCount > 0
                        ? "BlockedByPostgresFailure"
                        : "GateNotReady"
        };
    }

                                    private static RelationGovernanceSelectedNormalWorkspaceCleanupMode ParseSelectedNormalCleanupMode(IReadOnlyList<string> args)
    {
        var raw = CommandHelpers.GetOption(args, "--cleanup-mode");
        if (CommandHelpers.HasFlag(args, "--cleanup-confirm"))
        {
            return RelationGovernanceSelectedNormalWorkspaceCleanupMode.ExplicitConfirm;
        }

        return Enum.TryParse<RelationGovernanceSelectedNormalWorkspaceCleanupMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : RelationGovernanceSelectedNormalWorkspaceCleanupMode.None;
    }

                    private static ContextRelation CreateParityRelation(
        string id,
        string workspaceId,
        string collectionId,
        string sourceId,
        string targetId,
        string relationType,
        double weight,
        double confidence,
        DateTimeOffset createdAt,
        string lifecycle,
        string reviewStatus)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = weight,
            Confidence = confidence,
            SourceRefs = [$"source:{id}"],
            Metadata = new Dictionary<string, string>
            {
                ["lifecycle"] = lifecycle,
                ["reviewStatus"] = reviewStatus,
                ["source"] = "postgres_relation_store_parity"
            },
            CreatedAt = createdAt
        };
    }

    private static IReadOnlyList<RelationGovernanceScopedRule> BuildDefaultScopedExpansionRules()
    {
        return
        [
            new RelationGovernanceScopedRule
            {
                ScopeName = "selected-canary-alpha",
                ScopeDescription = "First explicit DB2.10 scoped relation governance expansion canary.",
                WorkspaceId = "contextcore_scoped_expansion_alpha",
                CollectionId = "relation-governance-scoped-expansion-alpha",
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                RolloutStage = "db2.10-expansion",
                Enabled = true
            },
            new RelationGovernanceScopedRule
            {
                ScopeName = "selected-canary-beta",
                ScopeDescription = "Second explicit DB2.10 scoped relation governance expansion canary.",
                WorkspaceId = "contextcore_scoped_expansion_beta",
                CollectionId = "relation-governance-scoped-expansion-beta",
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                RolloutStage = "db2.10-expansion",
                Enabled = true
            }
        ];
    }

    private static IReadOnlyList<RelationGovernanceNormalScopeRule> BuildDefaultMultiNormalScopeRules(
        RelationGovernanceSelectedNormalWorkspaceCleanupMode cleanupMode,
        string? runShard = null)
    {
        var suffix = string.IsNullOrWhiteSpace(runShard) ? string.Empty : "-" + runShard;
        return
        [
            new RelationGovernanceNormalScopeRule
            {
                ScopeName = "multi-normal-alpha",
                WorkspaceId = "contextcore_multi_normal_alpha",
                CollectionId = "relation-governance-multi-normal-alpha" + suffix,
                RolloutStage = "db2.14-multi-normal",
                Description = "First explicit DB2.14 multi normal relation governance canary scope.",
                Enabled = true,
                CleanupMode = cleanupMode
            },
            new RelationGovernanceNormalScopeRule
            {
                ScopeName = "multi-normal-beta",
                WorkspaceId = "contextcore_multi_normal_beta",
                CollectionId = "relation-governance-multi-normal-beta" + suffix,
                RolloutStage = "db2.14-multi-normal",
                Description = "Second explicit DB2.14 multi normal relation governance canary scope.",
                Enabled = true,
                CleanupMode = cleanupMode
            }
        ];
    }

    private static IReadOnlyList<RelationGovernanceScopedExpansionPlan> BuildScopedExpansionPlans(
        IReadOnlyList<RelationGovernanceScopedRule> scopes,
        bool gatePassed)
    {
        return
        [
            .. scopes.Select(scope => new RelationGovernanceScopedExpansionPlan
            {
                ScopeName = scope.ScopeName,
                WorkspaceId = scope.WorkspaceId,
                CollectionId = scope.CollectionId,
                Mode = scope.Mode.ToString(),
                GateStatus = gatePassed ? "Passed" : "Blocked",
                LastCanaryStatus = gatePassed ? "ReadyForScopedServiceModeExpansion" : "NotReady",
                AllowedOperations =
                [
                    "relation-edge-read-write",
                    "relation-review-read-write",
                    "diagnostics-read-write",
                    "replacement-chain-lookup",
                    "graph-expansion-preview"
                ],
                FallbackEnabled = true,
                ComparisonTraceEnabled = true,
                RollbackInstruction = $"Disable scope `{scope.ScopeName}` or set RelationGovernanceProviderSwitchOptions.Enabled=false."
            })
        ];
    }

    private sealed record ScopedExpansionPreflight(
        bool Passed,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyList<string> BlockedReasons);

    private static async Task<ScopedExpansionPreflight> BuildPostgresRelationScopedExpansionPreflightAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var options = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var readinessGate = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var providerSwitchGate = await ReadJsonFileAsync<PostgresRelationProviderSwitchGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var runtimeCanary = await ReadJsonFileAsync<PostgresRelationRuntimeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<PostgresRelationScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var extendedCanary = await ReadJsonFileAsync<PostgresRelationScopedExtendedCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-extended-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var selectedCanary = await ReadJsonFileAsync<PostgresRelationSelectedWorkspaceCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-selected-workspace-canary-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, options.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, readinessGate?.Passed == true, "GovernanceReadinessGateNotPassed");
        AddReasonIfFalse(blocked, providerSwitchGate?.Passed == true, "ProviderSwitchGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(runtimeCanary?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase), "RuntimeCanaryNotPassed");
        AddReasonIfFalse(blocked, scopedGate?.Passed == true, "ScopedServiceModeGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(extendedCanary?.Recommendation, "ReadyForSelectedWorkspaceCanary", StringComparison.OrdinalIgnoreCase), "ExtendedCanaryNotPassed");
        AddReasonIfFalse(blocked, string.Equals(selectedCanary?.Recommendation, "ReadyForScopedServiceModeExpansion", StringComparison.OrdinalIgnoreCase), "SelectedWorkspaceCanaryNotPassed");

        if (readinessGate is null)
        {
            diagnostics.Add("RunPostgresRelationGovernanceReadinessGateFirst");
        }

        if (providerSwitchGate is null)
        {
            diagnostics.Add("RunPostgresRelationProviderSwitchGateFirst");
        }

        if (runtimeCanary is null)
        {
            diagnostics.Add("RunPostgresRelationRuntimeCanaryFirst");
        }

        if (scopedGate is null)
        {
            diagnostics.Add("RunPostgresRelationScopedServiceModeGateFirst");
        }

        if (extendedCanary is null)
        {
            diagnostics.Add("RunPostgresRelationScopedExtendedCanaryFirst");
        }

        if (selectedCanary is null)
        {
            diagnostics.Add("RunPostgresRelationSelectedWorkspaceCanaryFirst");
        }

        return new ScopedExpansionPreflight(
            blocked.Count == 0,
            diagnostics,
            blocked);
    }

    private static RelationReviewRecord CreateReviewRecord(
        string reviewId,
        ContextRelation relation,
        string action,
        string reviewStatus,
        string reviewer,
        string operationId,
        DateTimeOffset createdAt)
    {
        return new RelationReviewRecord
        {
            ReviewId = reviewId,
            RelationId = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            Action = action,
            FromLifecycle = "Active",
            ToLifecycle = "Active",
            FromReviewStatus = "Pending",
            ToReviewStatus = reviewStatus,
            Reviewer = reviewer,
            Reason = $"{action} parity check",
            RelationType = relation.RelationType,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            EvidenceRefs = ["evidence:relation-review"],
            SourceRefs = ["source:relation-review"],
            CreatedAt = createdAt,
            ReviewedAt = createdAt,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["source"] = "postgres_relation_review_parity"
            }
        };
    }

    private static RelationDiagnosticsSnapshot CreateDiagnosticSnapshot(
        string diagnosticId,
        string workspaceId,
        string collectionId,
        string relationId,
        string itemId,
        string kind,
        string severity,
        DateTimeOffset createdAt)
    {
        return new RelationDiagnosticsSnapshot
        {
            DiagnosticId = diagnosticId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RelationId = relationId,
            ItemId = itemId,
            DiagnosticKind = kind,
            Severity = severity,
            Message = $"{kind} parity diagnostic",
            CreatedAt = createdAt,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "postgres_relation_review_parity"
            }
        };
    }

    private static async Task<bool> CompareQueryAsync(
        Task<IReadOnlyList<ContextRelation>> expectedTask,
        Task<IReadOnlyList<ContextRelation>> actualTask,
        string mismatch,
        ICollection<string> mismatches)
    {
        var expected = await expectedTask.ConfigureAwait(false);
        var actual = await actualTask.ConfigureAwait(false);
        var passed = SameIds(expected, actual);
        AddMismatchIfFalse(mismatches, passed, mismatch);
        return passed;
    }

    private static async Task<bool> CompareReviewQueryAsync(
        Task<IReadOnlyList<RelationReviewRecord>> expectedTask,
        Task<IReadOnlyList<RelationReviewRecord>> actualTask,
        string mismatch,
        ICollection<string> mismatches)
    {
        var expected = await expectedTask.ConfigureAwait(false);
        var actual = await actualTask.ConfigureAwait(false);
        var passed = expected.Select(static item => item.ReviewId).Order(StringComparer.Ordinal)
            .SequenceEqual(actual.Select(static item => item.ReviewId).Order(StringComparer.Ordinal));
        AddMismatchIfFalse(mismatches, passed, mismatch);
        return passed;
    }

    private static async Task<bool> CompareDiagnosticQueryAsync(
        Task<IReadOnlyList<RelationDiagnosticsSnapshot>> expectedTask,
        Task<IReadOnlyList<RelationDiagnosticsSnapshot>> actualTask,
        string mismatch,
        ICollection<string> mismatches)
    {
        var expected = await expectedTask.ConfigureAwait(false);
        var actual = await actualTask.ConfigureAwait(false);
        var passed = expected.Select(static item => item.DiagnosticId).Order(StringComparer.Ordinal)
            .SequenceEqual(actual.Select(static item => item.DiagnosticId).Order(StringComparer.Ordinal));
        AddMismatchIfFalse(mismatches, passed, mismatch);
        return passed;
    }

    private static bool SameIds(IReadOnlyList<ContextRelation> expected, IReadOnlyList<ContextRelation> actual)
    {
        return expected.Select(static item => item.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(actual.Select(static item => item.Id).Order(StringComparer.Ordinal));
    }

    private static bool RelationEqual(ContextRelation? expected, ContextRelation? actual)
    {
        return expected is not null
            && actual is not null
            && string.Equals(expected.Id, actual.Id, StringComparison.Ordinal)
            && string.Equals(expected.SourceId, actual.SourceId, StringComparison.Ordinal)
            && string.Equals(expected.TargetId, actual.TargetId, StringComparison.Ordinal)
            && string.Equals(expected.RelationType, actual.RelationType, StringComparison.Ordinal)
            && expected.SourceRefs.SequenceEqual(actual.SourceRefs)
            && expected.Metadata.Count == actual.Metadata.Count
            && expected.Metadata.All(pair =>
                actual.Metadata.TryGetValue(pair.Key, out var value) &&
                string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static void AddMismatchIfFalse(ICollection<string> mismatches, bool passed, string mismatch)
    {
        if (!passed)
        {
            mismatches.Add(mismatch);
        }
    }

    private static void AddReasonIfFalse(ICollection<string> reasons, bool passed, string reason)
    {
        if (!passed && !reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add(reason);
        }
    }

    private static IReadOnlyList<string> BuildPostgresRelationGovernanceBlockedReasons(
        bool providerEnabled,
        bool governancePassed,
        bool cleanupPerformed,
        bool useForRuntime,
        IReadOnlyList<string> mismatches)
    {
        var reasons = new List<string>();
        AddReasonIfFalse(reasons, providerEnabled, "NotConfigured");
        AddReasonIfFalse(reasons, governancePassed, "GovernanceParityNotPassed");
        AddReasonIfFalse(reasons, mismatches.Count == 0, "ParityMismatch");
        AddReasonIfFalse(reasons, cleanupPerformed, "CleanupNotPerformed");
        AddReasonIfFalse(reasons, !useForRuntime, "UseForRuntimeMustRemainFalse");
        return reasons;
    }

    private static string BuildPostgresRelationGovernanceRecommendation(
        bool providerEnabled,
        bool governancePassed,
        bool cleanupPerformed,
        IReadOnlyList<string> mismatches)
    {
        if (!providerEnabled)
        {
            return "NotConfigured";
        }

        if (mismatches.Count > 0)
        {
            return "BlockedByMismatch";
        }

        if (!governancePassed || !cleanupPerformed)
        {
            return "NeedsParityFix";
        }

        return "ReadyForDualWrite";
    }

    private static string BuildReadinessGateRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Contains("NotConfigured", StringComparer.OrdinalIgnoreCase))
        {
            return "NotConfigured";
        }

        if (blockedReasons.Contains("SchemaNotReady", StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains("RelationTableMissing", StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains("RelationReviewsTableMissing", StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains("RelationDiagnosticsTableMissing", StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains("MissingRequiredIndexes", StringComparer.OrdinalIgnoreCase))
        {
            return "SchemaNotReady";
        }

        if (blockedReasons.Contains("DiagnosticsParityNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return "DiagnosticsNotReady";
        }

        if (blockedReasons.Contains("ParityMismatch", StringComparer.OrdinalIgnoreCase))
        {
            return "BlockedByMismatch";
        }

        return "NeedsParityFix";
    }

    private static bool IsPostgresSchemaAtLeast(string? schemaVersion, int minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            return false;
        }

        const string prefix = "cc-schema-v";
        return schemaVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(schemaVersion[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            && version >= minimumVersion;
    }

    private static bool IsP15EvalReportPassed(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.TryGetProperty("FailedSamples", out var failed)
                && root.TryGetProperty("InvalidSamples", out var invalid)
                && root.TryGetProperty("PassRate", out var passRate)
                && failed.GetInt32() == 0
                && invalid.GetInt32() == 0
                && passRate.GetDouble() >= 1.0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<T?> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static async Task<PostgresOperationalStoreDiagnostics> BuildCliPostgresDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var options = BuildCliPostgresOptions(args);
        if (!options.Enabled)
        {
            return PostgresOperationalStoreDiagnosticsBuilder.BuildNotConfigured(options);
        }

        await using var factory = new PostgresConnectionFactory(options);
        var runner = new PostgresMigrationRunner(factory);
        return await PostgresOperationalStoreDiagnosticsBuilder.BuildAsync(
            options,
            factory,
            runner,
            cancellationToken).ConfigureAwait(false);
    }

                                                                                                                                        private static IReadOnlyList<ContextEvalSample> LimitSamples(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<string> args)
    {
        var maxQueries = CommandHelpers.GetIntOption(args, "--max-queries", 0);
        return maxQueries > 0 ? samples.Take(maxQueries).ToArray() : samples;
    }

    private static IReadOnlyList<string> BuildPostgresVectorShadowEvalPreconditionDiagnostics(IReadOnlyList<string> args)
    {
        var diagnostics = new List<string>();
        AddPrecondition(
            diagnostics,
            "DB5.0",
            CommandHelpers.GetOption(args, "--diagnostics-report") ?? Path.Combine("storage", "postgres", "postgres-vector-diagnostics.json"),
            ReadJsonFileOrDefault<PostgresVectorDiagnosticsReport>,
            static report => string.Equals(report.Recommendation, "ReadyForVectorParityEval", StringComparison.OrdinalIgnoreCase),
            static report => report.Recommendation);
        AddPrecondition(
            diagnostics,
            "DB5.1",
            CommandHelpers.GetOption(args, "--parity-report") ?? Path.Combine("storage", "postgres", "postgres-vector-parity-report.json"),
            ReadJsonFileOrDefault<PostgresVectorIndexParityReport>,
            static report => string.Equals(report.Recommendation, "ReadyForProviderScopedReindex", StringComparison.OrdinalIgnoreCase),
            static report => report.Recommendation);
        AddPrecondition(
            diagnostics,
            "DB5.2",
            CommandHelpers.GetOption(args, "--reindex-quality-report") ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-quality-report.json"),
            ReadJsonFileOrDefault<PostgresVectorProviderScopedReindexReport>,
            static report => string.Equals(report.Recommendation, "ReadyForPgVectorQueryPreview", StringComparison.OrdinalIgnoreCase),
            static report => report.Recommendation);
        AddPrecondition(
            diagnostics,
            "DB5.3",
            CommandHelpers.GetOption(args, "--query-preview-report") ?? Path.Combine("storage", "postgres", "postgres-vector-query-preview-report.json"),
            ReadJsonFileOrDefault<PostgresVectorQueryPreviewReport>,
            static report => string.Equals(report.Recommendation, "ReadyForPgVectorShadowEval", StringComparison.OrdinalIgnoreCase),
            static report => report.Recommendation);
        return diagnostics;
    }

    private static void AddPrecondition<T>(
        ICollection<string> diagnostics,
        string phase,
        string path,
        Func<string, T?> read,
        Func<T, bool> isReady,
        Func<T, string> recommendation)
    {
        var report = read(path);
        if (report is null)
        {
            diagnostics.Add($"{phase}PreconditionMissing:{path}");
            return;
        }

        if (!isReady(report))
        {
            diagnostics.Add($"{phase}PreconditionNotReady:{recommendation(report)}");
        }
    }

    private static LearningFeedbackSelectedNormalScopeCleanupMode ParseLearningFeedbackSelectedNormalCleanupMode(IReadOnlyList<string> args)
    {
        if (CommandHelpers.HasFlag(args, "--cleanup-confirm"))
        {
            return LearningFeedbackSelectedNormalScopeCleanupMode.ExplicitConfirm;
        }

        var raw = CommandHelpers.GetOption(args, "--cleanup-mode");
        return Enum.TryParse<LearningFeedbackSelectedNormalScopeCleanupMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : LearningFeedbackSelectedNormalScopeCleanupMode.None;
    }

    private static PostgresOptions BuildCliPostgresOptions(IReadOnlyList<string> args, string defaultSchemaName = "")
    {
        var configuredPostgresOptions = ReadUserPostgresOptions();
        var cliConnectionString = CommandHelpers.GetOption(args, "--connection-string");
        var environmentConnectionString = Environment.GetEnvironmentVariable("CONTEXTCORE_POSTGRES_CONNECTION_STRING");
        var connectionString = cliConnectionString
            ?? environmentConnectionString
            ?? configuredPostgresOptions.ConnectionString
            ?? string.Empty;
        var schemaName = CommandHelpers.GetOption(args, "--schema")
            ?? configuredPostgresOptions.SchemaName
            ?? defaultSchemaName;
        var providerId = CommandHelpers.GetOption(args, "--provider-id")
            ?? configuredPostgresOptions.ProviderId
            ?? "postgres-operational-v1";
        var enabled = !string.IsNullOrWhiteSpace(cliConnectionString) ||
            !string.IsNullOrWhiteSpace(environmentConnectionString) ||
            (configuredPostgresOptions.Enabled == true && !string.IsNullOrWhiteSpace(connectionString));
        return new PostgresOptions
        {
            Enabled = enabled,
            ConnectionString = connectionString,
            SchemaName = schemaName,
            AutoMigrate = false,
            EnablePgVectorExtension = true,
            CommandTimeoutSeconds = configuredPostgresOptions.CommandTimeoutSeconds ?? 30,
            ProviderId = providerId
        };
    }

    private static UserPostgresOptions ReadUserPostgresOptions()
    {
        var directory = ResolveUserPrivateConfigurationDirectory();
        var jsonPath = Path.Combine(directory, "secrets.json");
        var envPath = Path.Combine(directory, ".env");
        LoadEnvironmentFile(envPath);
        if (!File.Exists(jsonPath))
        {
            return new UserPostgresOptions();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!document.RootElement.TryGetProperty("PostgresStore", out var postgresStore) ||
                postgresStore.ValueKind != JsonValueKind.Object)
            {
                return new UserPostgresOptions();
            }

            return new UserPostgresOptions
            {
                Enabled = TryGetBool(postgresStore, "Enabled"),
                ConnectionString = TryGetString(postgresStore, "ConnectionString"),
                SchemaName = TryGetString(postgresStore, "SchemaName"),
                AutoMigrate = TryGetBool(postgresStore, "AutoMigrate"),
                CommandTimeoutSeconds = TryGetInt(postgresStore, "CommandTimeoutSeconds"),
                ProviderId = TryGetString(postgresStore, "ProviderId")
            };
        }
        catch (JsonException)
        {
            return new UserPostgresOptions();
        }
        catch (IOException)
        {
            return new UserPostgresOptions();
        }
    }

    private static string ResolveUserPrivateConfigurationDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Path.Combine(userProfile, ".contextcore");
    }

    private static void LoadEnvironmentFile(string envPath)
    {
        if (string.IsNullOrWhiteSpace(envPath) || !File.Exists(envPath))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = UnquotePrivateConfigurationValue(line[(separatorIndex + 1)..].Trim());
            if (!string.IsNullOrWhiteSpace(key) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string UnquotePrivateConfigurationValue(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private sealed class UserPostgresOptions
    {
        public bool? Enabled { get; init; }

        public string? ConnectionString { get; init; }

        public string? SchemaName { get; init; }

        public bool? AutoMigrate { get; init; }

        public int? CommandTimeoutSeconds { get; init; }

        public string? ProviderId { get; init; }
    }

    private static PostgresMigrationPlanResponse ToCliPlanResponse(PostgresMigrationPlan plan)
    {
        return new PostgresMigrationPlanResponse
        {
            DryRun = plan.DryRun,
            ProviderEnabled = plan.ProviderEnabled,
            ProviderId = plan.ProviderId,
            CurrentSchemaVersion = plan.CurrentSchemaVersion,
            PendingMigrations = plan.PendingMigrations,
            RequiredTables = plan.RequiredTables,
            MissingRequiredTables = plan.MissingRequiredTables,
            Diagnostics = plan.Diagnostics
        };
    }

    private static string BuildPostgresDiagnosticsMarkdown(PostgresOperationalStoreDiagnostics diagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Storage Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- Enabled: `{diagnostics.ProviderEnabled}`");
        builder.AppendLine($"- Status: `{diagnostics.Status}`");
        builder.AppendLine($"- ProviderId: `{diagnostics.ProviderId}`");
        builder.AppendLine($"- ConnectionAvailable: `{diagnostics.ConnectionAvailable}`");
        builder.AppendLine($"- CurrentSchemaVersion: `{diagnostics.CurrentSchemaVersion ?? "none"}`");
        builder.AppendLine($"- PendingMigrations: `{diagnostics.PendingMigrations}`");
        builder.AppendLine($"- TableCount: `{diagnostics.TableCount}`");
        builder.AppendLine($"- RequiredTableMissingCount: `{diagnostics.RequiredTableMissingCount}`");
        builder.AppendLine($"- ProviderCapabilityStatus: `{diagnostics.ProviderCapabilityStatus}`");
        builder.AppendLine($"- RedactedConnectionString: `{diagnostics.RedactedConnectionString}`");
        builder.AppendLine();
        builder.AppendLine("## Missing Required Tables");
        foreach (var table in diagnostics.MissingRequiredTables.Take(40))
        {
            builder.AppendLine($"- `{table}`");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in diagnostics.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresMigrationPreviewMarkdown(PostgresMigrationPlanResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Migration Preview");
        builder.AppendLine();
        builder.AppendLine($"- DryRun: `{response.DryRun}`");
        builder.AppendLine($"- ProviderEnabled: `{response.ProviderEnabled}`");
        builder.AppendLine($"- ProviderId: `{response.ProviderId}`");
        builder.AppendLine($"- CurrentSchemaVersion: `{response.CurrentSchemaVersion ?? "none"}`");
        builder.AppendLine($"- PendingMigrations: `{response.PendingMigrations.Count}`");
        builder.AppendLine($"- MissingRequiredTables: `{response.MissingRequiredTables.Count}`");
        builder.AppendLine();
        builder.AppendLine("## Pending Migrations");
        foreach (var migration in response.PendingMigrations)
        {
            builder.AppendLine($"- `{migration}`");
        }

        return builder.ToString();
    }

    private static string BuildPostgresMigrationApplyMarkdown(PostgresMigrationApplyResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Migration Apply");
        builder.AppendLine();
        builder.AppendLine($"- Applied: `{response.Applied}`");
        builder.AppendLine($"- ConfirmRequired: `{response.ConfirmRequired}`");
        builder.AppendLine($"- SchemaVersion: `{response.SchemaVersion ?? "none"}`");
        builder.AppendLine($"- AppliedMigrations: `{response.AppliedMigrations.Count}`");
        if (response.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in response.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresSchemaVerificationMarkdown(PostgresSchemaVerificationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Schema Verification");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- ConnectionAvailable: `{report.ConnectionAvailable}`");
        builder.AppendLine($"- SchemaName: `{(string.IsNullOrWhiteSpace(report.SchemaName) ? "default" : report.SchemaName)}`");
        builder.AppendLine($"- CurrentSchemaVersion: `{report.CurrentSchemaVersion ?? "none"}`");
        builder.AppendLine($"- AppliedMigrationCount: `{report.AppliedMigrationCount}`");
        builder.AppendLine($"- RequiredTableCount: `{report.RequiredTableCount}`");
        builder.AppendLine($"- MissingRequiredTableCount: `{report.MissingRequiredTableCount}`");
        builder.AppendLine($"- RequiredIndexCount: `{report.RequiredIndexCount}`");
        builder.AppendLine($"- MissingIndexCount: `{report.MissingIndexCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");

        if (report.MissingRequiredTables.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Missing Required Tables");
            foreach (var table in report.MissingRequiredTables.Take(50))
            {
                builder.AppendLine($"- `{table}`");
            }
        }

        if (report.MissingIndexes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Missing Required Indexes");
            foreach (var index in report.MissingIndexes.Take(50))
            {
                builder.AppendLine($"- `{index}`");
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationStoreDiagnosticsMarkdown(PostgresRelationStoreDiagnostics diagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Store Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{diagnostics.ProviderEnabled}`");
        builder.AppendLine($"- ProviderId: `{diagnostics.ProviderId}`");
        builder.AppendLine($"- ActiveRuntimeProvider: `{diagnostics.ActiveRuntimeProvider}`");
        builder.AppendLine($"- UseForRuntime: `{diagnostics.UseForRuntime}`");
        builder.AppendLine($"- ConnectionAvailable: `{diagnostics.ConnectionAvailable}`");
        builder.AppendLine($"- SchemaVersion: `{diagnostics.SchemaVersion ?? "none"}`");
        builder.AppendLine($"- RelationTableExists: `{diagnostics.RelationTableExists}`");
        builder.AppendLine($"- RelationReviewsTableExists: `{diagnostics.RelationReviewsTableExists}`");
        builder.AppendLine($"- RelationCount: `{diagnostics.RelationCount}`");
        builder.AppendLine($"- ReviewCount: `{diagnostics.ReviewCount}`");
        builder.AppendLine($"- MissingRequiredIndexes: `{diagnostics.MissingRequiredIndexes.Count}`");
        builder.AppendLine($"- Recommendation: `{diagnostics.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Missing Indexes");
        if (diagnostics.MissingRequiredIndexes.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var index in diagnostics.MissingRequiredIndexes)
            {
                builder.AppendLine($"- `{index}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in diagnostics.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationStoreParityMarkdown(PostgresRelationStoreParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Store Parity");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- FixtureRelationCount: `{report.FixtureRelationCount}`");
        builder.AppendLine($"- GetPassed: `{report.GetPassed}`");
        builder.AppendLine($"- ListPassed: `{report.ListPassed}`");
        builder.AppendLine($"- SourceQueryPassed: `{report.SourceQueryPassed}`");
        builder.AppendLine($"- TargetQueryPassed: `{report.TargetQueryPassed}`");
        builder.AppendLine($"- TypeQueryPassed: `{report.TypeQueryPassed}`");
        builder.AppendLine($"- LifecycleQueryPassed: `{report.LifecycleQueryPassed}`");
        builder.AppendLine($"- ReviewStatusQueryPassed: `{report.ReviewStatusQueryPassed}`");
        builder.AppendLine($"- ReplacementChainQueryPassed: `{report.ReplacementChainQueryPassed}`");
        builder.AppendLine($"- DeletePassed: `{report.DeletePassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Mismatches");
        foreach (var mismatch in report.Mismatches.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {mismatch}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationReviewDiagnosticsMarkdown(PostgresRelationReviewProviderDiagnostics diagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Review Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{diagnostics.ProviderEnabled}`");
        builder.AppendLine($"- ProviderId: `{diagnostics.ProviderId}`");
        builder.AppendLine($"- ActiveRuntimeProvider: `{diagnostics.ActiveRuntimeProvider}`");
        builder.AppendLine($"- UseForRuntime: `{diagnostics.UseForRuntime}`");
        builder.AppendLine($"- ConnectionAvailable: `{diagnostics.ConnectionAvailable}`");
        builder.AppendLine($"- SchemaVersion: `{diagnostics.SchemaVersion ?? "none"}`");
        builder.AppendLine($"- RelationReviewsTableExists: `{diagnostics.RelationReviewsTableExists}`");
        builder.AppendLine($"- RelationDiagnosticsTableExists: `{diagnostics.RelationDiagnosticsTableExists}`");
        builder.AppendLine($"- ReviewCount: `{diagnostics.ReviewCount}`");
        builder.AppendLine($"- DiagnosticsCount: `{diagnostics.DiagnosticsCount}`");
        builder.AppendLine($"- MissingRequiredIndexes: `{diagnostics.MissingRequiredIndexes.Count}`");
        builder.AppendLine($"- Recommendation: `{diagnostics.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Missing Indexes");
        foreach (var index in diagnostics.MissingRequiredIndexes.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- `{index}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in diagnostics.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationReviewParityMarkdown(PostgresRelationReviewParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Review Parity");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- FixtureReviewCount: `{report.FixtureReviewCount}`");
        builder.AppendLine($"- FixtureDiagnosticsCount: `{report.FixtureDiagnosticsCount}`");
        builder.AppendLine($"- ReviewListPassed: `{report.ReviewListPassed}`");
        builder.AppendLine($"- LatestReviewPassed: `{report.LatestReviewPassed}`");
        builder.AppendLine($"- ReviewStatusFilterPassed: `{report.ReviewStatusFilterPassed}`");
        builder.AppendLine($"- ReviewerFilterPassed: `{report.ReviewerFilterPassed}`");
        builder.AppendLine($"- OperationIdFilterPassed: `{report.OperationIdFilterPassed}`");
        builder.AppendLine($"- DiagnosticsByRelationPassed: `{report.DiagnosticsByRelationPassed}`");
        builder.AppendLine($"- DiagnosticsByItemPassed: `{report.DiagnosticsByItemPassed}`");
        builder.AppendLine($"- DiagnosticsKindFilterPassed: `{report.DiagnosticsKindFilterPassed}`");
        builder.AppendLine($"- DiagnosticsSeverityFilterPassed: `{report.DiagnosticsSeverityFilterPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Mismatches");
        foreach (var mismatch in report.Mismatches.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {mismatch}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationGovernanceParityMarkdown(PostgresRelationGovernanceParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Governance Parity");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- RelationParityPassed: `{report.RelationParityPassed}`");
        builder.AppendLine($"- ReviewParityPassed: `{report.ReviewParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- GovernanceParityPassed: `{report.GovernanceParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- CanDualWrite: `{report.CanDualWrite}`");
        builder.AppendLine($"- CanShadowRead: `{report.CanShadowRead}`");
        builder.AppendLine($"- CanRuntimeSwitch: `{report.CanRuntimeSwitch}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Mismatches");
        foreach (var mismatch in report.Mismatches.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {mismatch}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationGovernanceReadinessGateMarkdown(PostgresRelationGovernanceReadinessGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Governance Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- StorageReady: `{report.StorageReady}`");
        builder.AppendLine($"- SchemaVersion: `{report.SchemaVersion ?? "none"}`");
        builder.AppendLine($"- SchemaVersionReady: `{report.SchemaVersionReady}`");
        builder.AppendLine($"- RelationTableExists: `{report.RelationTableExists}`");
        builder.AppendLine($"- RelationReviewsTableExists: `{report.RelationReviewsTableExists}`");
        builder.AppendLine($"- RelationDiagnosticsTableExists: `{report.RelationDiagnosticsTableExists}`");
        builder.AppendLine($"- MissingRequiredIndexCount: `{report.MissingRequiredIndexCount}`");
        builder.AppendLine($"- RelationStoreParityPassed: `{report.RelationStoreParityPassed}`");
        builder.AppendLine($"- RelationReviewParityPassed: `{report.RelationReviewParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- GovernanceParityPassed: `{report.GovernanceParityPassed}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- P15GateExpected: `{report.P15GateExpected}`");
        builder.AppendLine($"- CanDualWrite: `{report.CanDualWrite}`");
        builder.AppendLine($"- CanShadowRead: `{report.CanShadowRead}`");
        builder.AppendLine($"- CanRuntimeSwitch: `{report.CanRuntimeSwitch}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationDualWriteSmokeMarkdown(PostgresRelationDualWriteSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Dual-write Smoke");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- RelationDualWritePassed: `{report.RelationDualWritePassed}`");
        builder.AppendLine($"- ReviewDualWritePassed: `{report.ReviewDualWritePassed}`");
        builder.AppendLine($"- DiagnosticsDualWritePassed: `{report.DiagnosticsDualWritePassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Mismatches");
        foreach (var mismatch in report.Mismatches.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {mismatch}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationDualWriteQualityMarkdown(PostgresRelationDualWriteQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Dual-write Quality");
        builder.AppendLine();
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- FileSystemWriteSuccessCount: `{report.FileSystemWriteSuccessCount}`");
        builder.AppendLine($"- PostgresWriteSuccessCount: `{report.PostgresWriteSuccessCount}`");
        builder.AppendLine($"- PostgresWriteFailureCount: `{report.PostgresWriteFailureCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- AverageDurationMs: `{report.AverageDurationMs:0.###}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationShadowReadSmokeMarkdown(PostgresRelationShadowReadSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Shadow-read Smoke");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Mismatches");
        foreach (var mismatch in report.Mismatches.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {mismatch}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationShadowReadQualityMarkdown(PostgresRelationShadowReadQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Shadow-read Quality");
        builder.AppendLine();
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- FileSystemReadSuccessCount: `{report.FileSystemReadSuccessCount}`");
        builder.AppendLine($"- PostgresReadSuccessCount: `{report.PostgresReadSuccessCount}`");
        builder.AppendLine($"- PostgresReadFailureCount: `{report.PostgresReadFailureCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- AverageFileSystemReadMs: `{report.AverageFileSystemReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationProviderSwitchSmokeMarkdown(PostgresRelationProviderSwitchSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Provider Switch Smoke");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- WritePassed: `{report.WritePassed}`");
        builder.AppendLine($"- PostgresPrimaryReadPassed: `{report.PostgresPrimaryReadPassed}`");
        builder.AppendLine($"- FileSystemFallbackPassed: `{report.FileSystemFallbackPassed}`");
        builder.AppendLine($"- ComparisonTraceRecorded: `{report.ComparisonTraceRecorded}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Mismatches");
        foreach (var mismatch in report.Mismatches.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {mismatch}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationProviderSwitchGateMarkdown(PostgresRelationProviderSwitchGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Provider Switch Gate");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- GovernanceReadinessGatePassed: `{report.GovernanceReadinessGatePassed}`");
        builder.AppendLine($"- DualWriteQualityReady: `{report.DualWriteQualityReady}`");
        builder.AppendLine($"- ShadowReadQualityReady: `{report.ShadowReadQualityReady}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresReadFailureCount: `{report.PostgresReadFailureCount}`");
        builder.AppendLine($"- PostgresWriteFailureCount: `{report.PostgresWriteFailureCount}`");
        builder.AppendLine($"- FallbackPathTested: `{report.FallbackPathTested}`");
        builder.AppendLine($"- AllowlistScopeConfigured: `{report.AllowlistScopeConfigured}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationRuntimeCanaryMarkdown(PostgresRelationRuntimeCanaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Runtime Canary");
        builder.AppendLine();
        builder.AppendLine($"- CanaryScope: `{report.CanaryScope}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationScopedServiceModeSmokeMarkdown(PostgresRelationScopedServiceModeSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Scoped Service Mode Smoke");
        builder.AppendLine();
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- AllowlistedScopeUsedPostgresPrimary: `{report.AllowlistedScopeUsedPostgresPrimary}`");
        builder.AppendLine($"- NonAllowlistedScopeUsedFileSystem: `{report.NonAllowlistedScopeUsedFileSystem}`");
        builder.AppendLine($"- FallbackTested: `{report.FallbackTested}`");
        builder.AppendLine($"- ComparisonTraceRecorded: `{report.ComparisonTraceRecorded}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationScopedServiceModeGateMarkdown(PostgresRelationScopedServiceModeGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Scoped Service Mode Gate");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- GovernanceReadinessGatePassed: `{report.GovernanceReadinessGatePassed}`");
        builder.AppendLine($"- ProviderSwitchGatePassed: `{report.ProviderSwitchGatePassed}`");
        builder.AppendLine($"- RuntimeCanaryPassed: `{report.RuntimeCanaryPassed}`");
        builder.AppendLine($"- ScopedAllowlistConfigured: `{report.ScopedAllowlistConfigured}`");
        builder.AppendLine($"- NonAllowlistedScopeRemainsFileSystem: `{report.NonAllowlistedScopeRemainsFileSystem}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackTested: `{report.FallbackTested}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationScopedExtendedCanaryMarkdown(PostgresRelationScopedExtendedCanaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Scoped Extended Canary");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- CanaryScope: `{report.CanaryScope}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- GraphExpansionPreviewParityPassed: `{report.GraphExpansionPreviewParityPassed}`");
        builder.AppendLine($"- ReviewLifecycleParityPassed: `{report.ReviewLifecycleParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- ReplacementChainParityPassed: `{report.ReplacementChainParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationSelectedWorkspaceCanaryMarkdown(PostgresRelationSelectedWorkspaceCanaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Selected Workspace Canary");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresWriteMs: `{report.AveragePostgresWriteMs:0.###}`");
        builder.AppendLine($"- AverageFileSystemFallbackMs: `{report.AverageFileSystemFallbackMs:0.###}`");
        builder.AppendLine($"- GraphExpansionPreviewParityPassed: `{report.GraphExpansionPreviewParityPassed}`");
        builder.AppendLine($"- ReviewLifecycleParityPassed: `{report.ReviewLifecycleParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- ReplacementChainParityPassed: `{report.ReplacementChainParityPassed}`");
        builder.AppendLine($"- ControlRoomReadPathPassed: `{report.ControlRoomReadPathPassed}`");
        builder.AppendLine($"- ClientApiRoundtripPathPassed: `{report.ClientApiRoundtripPathPassed}`");
        builder.AppendLine($"- NonSelectedScopeRemainsFileSystem: `{report.NonSelectedScopeRemainsFileSystem}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationSelectedNormalWorkspaceCanaryMarkdown(PostgresRelationSelectedNormalWorkspaceCanaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Relation Selected Normal Workspace Canary");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- P95PostgresReadMs: `{report.P95PostgresReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresWriteMs: `{report.AveragePostgresWriteMs:0.###}`");
        builder.AppendLine($"- P95PostgresWriteMs: `{report.P95PostgresWriteMs:0.###}`");
        builder.AppendLine($"- GraphExpansionPreviewParityPassed: `{report.GraphExpansionPreviewParityPassed}`");
        builder.AppendLine($"- ReviewLifecycleParityPassed: `{report.ReviewLifecycleParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- ReplacementChainParityPassed: `{report.ReplacementChainParityPassed}`");
        builder.AppendLine($"- ControlRoomReadPathPassed: `{report.ControlRoomReadPathPassed}`");
        builder.AppendLine($"- ClientApiRoundtripPathPassed: `{report.ClientApiRoundtripPathPassed}`");
        builder.AppendLine($"- NonSelectedNormalScopeRemainsFileSystem: `{report.NonSelectedNormalScopeRemainsFileSystem}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationLimitedNormalScopeObservationMarkdown(
        PostgresRelationLimitedNormalScopeObservationReport report,
        string titleSuffix)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Postgres Relation Limited Normal Scope Observation {titleSuffix}");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ObservationWindowMinutes: `{report.ObservationWindowMinutes}`");
        builder.AppendLine($"- OperationIntervalSeconds: `{report.OperationIntervalSeconds}`");
        builder.AppendLine($"- MaxOperations: `{report.MaxOperations}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- P95PostgresReadMs: `{report.P95PostgresReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresWriteMs: `{report.AveragePostgresWriteMs:0.###}`");
        builder.AppendLine($"- P95PostgresWriteMs: `{report.P95PostgresWriteMs:0.###}`");
        builder.AppendLine($"- ErrorRate: `{report.ErrorRate:0.####}`");
        builder.AppendLine($"- FallbackRate: `{report.FallbackRate:0.####}`");
        builder.AppendLine($"- GraphExpansionPreviewParityPassed: `{report.GraphExpansionPreviewParityPassed}`");
        builder.AppendLine($"- ReviewLifecycleParityPassed: `{report.ReviewLifecycleParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- ReplacementChainParityPassed: `{report.ReplacementChainParityPassed}`");
        builder.AppendLine($"- ControlRoomReadPathPassed: `{report.ControlRoomReadPathPassed}`");
        builder.AppendLine($"- ClientApiRoundtripPathPassed: `{report.ClientApiRoundtripPathPassed}`");
        builder.AppendLine($"- NonSelectedNormalScopeRemainsFileSystem: `{report.NonSelectedNormalScopeRemainsFileSystem}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationMultiNormalScopeCanaryMarkdown(
        PostgresRelationMultiNormalScopeCanaryReport report,
        string titleSuffix)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Postgres Relation Multi Normal Scope Canary {titleSuffix}");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- EnabledScopeCount: `{report.EnabledScopeCount}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- P95PostgresReadMs: `{report.P95PostgresReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresWriteMs: `{report.AveragePostgresWriteMs:0.###}`");
        builder.AppendLine($"- P95PostgresWriteMs: `{report.P95PostgresWriteMs:0.###}`");
        builder.AppendLine($"- GraphExpansionPreviewParityPassed: `{report.GraphExpansionPreviewParityPassed}`");
        builder.AppendLine($"- ReviewLifecycleParityPassed: `{report.ReviewLifecycleParityPassed}`");
        builder.AppendLine($"- DiagnosticsParityPassed: `{report.DiagnosticsParityPassed}`");
        builder.AppendLine($"- ReplacementChainParityPassed: `{report.ReplacementChainParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        builder.AppendLine();
        builder.AppendLine("## Operation Count By Scope");
        foreach (var item in report.OperationCountByScope.DefaultIfEmpty())
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                builder.AppendLine("- none");
                continue;
            }

            builder.AppendLine($"- {item.Key}: `{item.Value}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Per Scope Status");
        foreach (var scope in report.PerScopeStatus.DefaultIfEmpty())
        {
            if (scope is null)
            {
                builder.AppendLine("- none");
                continue;
            }

            builder.AppendLine($"- {scope.ScopeName}: `{scope.WorkspaceId}/{scope.CollectionId}` stage=`{scope.RolloutStage}` operations=`{scope.OperationCount}` reads=`{scope.PostgresPrimaryReadCount}` writes=`{scope.PostgresPrimaryWriteCount}` mismatches=`{scope.MismatchCount}` failures=`{scope.PostgresFailureCount}` leaks=`{scope.ScopeLeakCount}` recommendation=`{scope.Recommendation}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationScopedExpansionMarkdown(
        PostgresRelationScopedExpansionReport report,
        string titleSuffix)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Postgres Relation Scoped Expansion {titleSuffix}");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- AllowlistedScopeCount: `{report.AllowlistedScopeCount}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemScopeReadCount: `{report.FileSystemScopeReadCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresWriteMs: `{report.AveragePostgresWriteMs:0.###}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Plans");
        foreach (var plan in report.Plans.DefaultIfEmpty())
        {
            if (plan is null)
            {
                builder.AppendLine("- none");
                continue;
            }

            builder.AppendLine($"- {plan.ScopeName}: `{plan.WorkspaceId}/{plan.CollectionId}` mode=`{plan.Mode}` gate=`{plan.GateStatus}` canary=`{plan.LastCanaryStatus}`");
            builder.AppendLine($"  rollback: {plan.RollbackInstruction}");
        }

        builder.AppendLine();
        builder.AppendLine("## Per Scope Status");
        foreach (var scope in report.PerScopeStatus.DefaultIfEmpty())
        {
            if (scope is null)
            {
                builder.AppendLine("- none");
                continue;
            }

            builder.AppendLine($"- {scope.ScopeName}: operations=`{scope.OperationCount}` reads=`{scope.PostgresPrimaryReadCount}` writes=`{scope.PostgresPrimaryWriteCount}` mismatches=`{scope.MismatchCount}` failures=`{scope.PostgresFailureCount}` recommendation=`{scope.Recommendation}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static string BuildPostgresRelationScopedObservationMarkdown(
        PostgresRelationScopedObservationReport report,
        string titleSuffix)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Postgres Relation Scoped Observation {titleSuffix}");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- ObservationWindowMinutes: `{report.ObservationWindowMinutes}`");
        builder.AppendLine($"- OperationIntervalSeconds: `{report.OperationIntervalSeconds}`");
        builder.AppendLine($"- MaxOperations: `{report.MaxOperations}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- AveragePostgresReadMs: `{report.AveragePostgresReadMs:0.###}`");
        builder.AppendLine($"- P95PostgresReadMs: `{report.P95PostgresReadMs:0.###}`");
        builder.AppendLine($"- AveragePostgresWriteMs: `{report.AveragePostgresWriteMs:0.###}`");
        builder.AppendLine($"- P95PostgresWriteMs: `{report.P95PostgresWriteMs:0.###}`");
        builder.AppendLine($"- FallbackPathTested: `{report.FallbackPathTested}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        builder.AppendLine();
        builder.AppendLine("## Per Scope Status");
        foreach (var scope in report.PerScopeStatus.DefaultIfEmpty())
        {
            if (scope is null)
            {
                builder.AppendLine("- none");
                continue;
            }

            builder.AppendLine($"- {scope.ScopeName}: operations=`{scope.OperationCount}` reads=`{scope.PostgresPrimaryReadCount}` writes=`{scope.PostgresPrimaryWriteCount}` mismatches=`{scope.MismatchCount}` failures=`{scope.PostgresFailureCount}` recommendation=`{scope.Recommendation}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        foreach (var reason in report.BlockedReasons.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        foreach (var diagnostic in report.Diagnostics.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {diagnostic}");
        }

        return builder.ToString();
    }

    private static async Task AppendJsonLineFileAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.AppendAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonLineOptions) + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
    }

    private static string RedactPostgresDiagnostic(string message)
    {
        return message.Replace("Password=", "Password=***;", StringComparison.OrdinalIgnoreCase)
            .Replace("Pwd=", "Pwd=***;", StringComparison.OrdinalIgnoreCase);
    }

    private static T? ReadJsonFileOrDefault<T>(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static int ParseIntOption(IReadOnlyList<string> args, string optionName, int fallback)
    {
        var value = CommandHelpers.GetOption(args, optionName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static IReadOnlyList<string> SplitOption(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void ResetTraceOutput(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // ──  存储可读写深度检查 ─────────────────────────────────────
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

    private static async Task ExecuteArchitectureCleanupReadinessGateAsync(CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("eval"));
        Directory.CreateDirectory(output);
        var planPath = Path.Combine(output, "architecture-cleanup-plan.json");
        var plan = await ReadJsonFileAsync<ArchitectureCleanupPlanReport>(planPath, ct).ConfigureAwait(false);
        var report = plan ?? new ArchitectureCleanupPlanReport();
        var jp = Path.Combine(output, "architecture-cleanup-readiness-gate.json");
        var mp = Path.Combine(output, "architecture-cleanup-readiness-gate.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ArchitectureCleanupPlanRunner.BuildMarkdown("Architecture Cleanup Readiness Gate", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Architecture cleanup readiness gate written: {jp}");
    }

    private static async Task ExecuteDtoSplitPlanAsync(IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("eval"));
        Directory.CreateDirectory(output);
        var runner = new DtoSplitPlanRunner();
        var report = runner.BuildPlan(new DtoSplitPlanOptions());
        var isGate = string.Equals(subcommand, "dto-split-readiness-gate", StringComparison.OrdinalIgnoreCase);
        var docPath = Path.GetFullPath(Path.Combine("docs", "ContextCore_DTO_Split_Plan.md"));
        if (!isGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
            await File.WriteAllTextAsync(docPath, DtoSplitPlanRunner.BuildMarkdown("ContextCore DTO Split Plan", report), Encoding.UTF8, ct).ConfigureAwait(false);
        }
        var fn = isGate ? "dto-split-readiness-gate" : "dto-split-plan";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jp, ct).ConfigureAwait(false);
        await WriteTextAsync(DtoSplitPlanRunner.BuildMarkdown(isGate ? "DTO Split Readiness Gate" : "DTO Split Plan", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] DTO split plan written: {jp}");
        Console.WriteLine($"[Eval] total={report.TotalClasses}; runtime={report.RuntimeContractCount}; eval={report.EvalReportCount}; gate={report.GateReportCount}; summary={report.ControlRoomSummaryCount}; legacy={report.LegacyCount}; blocked={report.BlockedReasons.Count}");
    }

    /// <summary>序列化报告前自动应用路径规范化，确保无本地绝对路径泄漏。</summary>
    private static async Task WriteJsonSafeAsync<T>(
        T report,
        string path,
        CancellationToken cancellationToken) where T : class
    {
        PathHygiene.NormalizeReportPaths(report);
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await MirrorReportArtifactAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteGeneratedArtifactPathHygieneAuditAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        PathHygiene.RepoRoot = current;
        var scanDir = CommandHelpers.GetOption(args, "--scan-dir") ?? current;
        var outputPath = Path.Combine(current, "eval", "generated-artifact-path-hygiene-audit.json");
        var markdownPath = Path.Combine(current, "eval", "generated-artifact-path-hygiene-audit.md");

        var report = PathHygiene.ScanGeneratedReports(scanDir);

        var md = BuildPathHygieneAuditMarkdown(report);
        await WriteJsonSafeAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(md, markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Path hygiene audit: total={report.TotalFiles}; infected={report.InfectedFiles}; passed={report.Passed}");
        if (report.InfectedFiles > 0)
        {
            Console.WriteLine($"[Eval] Found {report.InfectedFiles} files with absolute path leaks. See {outputPath} for details.");
        }
    }

    private static async Task ExecuteGeneratedArtifactPathHygieneGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        PathHygiene.RepoRoot = current;
        var scanDir = CommandHelpers.GetOption(args, "--scan-dir") ?? current;
        var auditPath = Path.Combine(current, "eval", "generated-artifact-path-hygiene-audit.json");
        var outputPath = Path.Combine(current, "eval", "generated-artifact-path-hygiene-gate.json");
        var markdownPath = Path.Combine(current, "eval", "generated-artifact-path-hygiene-gate.md");

        PathHygieneScanReport audit;
        if (File.Exists(auditPath))
        {
            var json = await File.ReadAllTextAsync(auditPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            audit = JsonSerializer.Deserialize<PathHygieneScanReport>(json, JsonOptions) ?? PathHygiene.ScanGeneratedReports(scanDir);
        }
        else
        {
            audit = PathHygiene.ScanGeneratedReports(scanDir);
        }

        var failed = new List<string>();
        if (audit.InfectedFiles > 0)
        {
            failed.Add($"Found {audit.InfectedFiles} files with absolute path leaks");
            foreach (var entry in audit.Entries)
            {
                failed.Add($"  {entry.FilePath}: {string.Join(", ", entry.LeakedPaths)}");
            }
        }

        var gate = new PathHygieneGateReport
        {
            OperationId = $"path-hygiene-gate-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = audit.Passed,
            InfectedFiles = audit.InfectedFiles,
            TotalFiles = audit.TotalFiles,
            FailedConditions = failed,
            Recommendation = audit.Passed ? "ReadyForNextPhase" : "BlockedByPathLeaks",
            AuditReportPath = "eval/generated-artifact-path-hygiene-audit.json"
        };

        var md = BuildPathHygieneGateMarkdown(gate);
        await WriteJsonSafeAsync(gate, outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(md, markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Path hygiene gate: passed={gate.Passed}; infected={gate.InfectedFiles}; total={gate.TotalFiles}");
        if (!gate.Passed)
        {
            throw new InvalidOperationException(
                $"Path hygiene gate failed: {gate.InfectedFiles} files contain absolute path leaks. Run 'eval generated-artifact-path-hygiene-audit' for details.");
        }
    }

    private static string BuildPathHygieneAuditMarkdown(PathHygieneScanReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated Artifact Path Hygiene Audit");
        sb.AppendLine();
        sb.AppendLine($"**ScanRoot:** `{report.ScanRoot}`");
        sb.AppendLine($"**GeneratedAt:** {report.GeneratedAt:O}");
        sb.AppendLine($"**TotalFiles:** {report.TotalFiles}");
        sb.AppendLine($"**InfectedFiles:** {report.InfectedFiles}");
        sb.AppendLine($"**Passed:** {report.Passed}");
        sb.AppendLine();
        if (report.Entries.Count > 0)
        {
            sb.AppendLine("## Leaked Absolute Paths");
            sb.AppendLine();
            sb.AppendLine("| File | Leak Count |");
            sb.AppendLine("|------|-----------|");
            foreach (var entry in report.Entries)
            {
                sb.AppendLine($"| `{entry.FilePath}` | {entry.LeakedPaths.Count} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"**Recommendation:** {report.Recommendation}");
        return sb.ToString();
    }

    private static string BuildPathHygieneGateMarkdown(PathHygieneGateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated Artifact Path Hygiene Gate");
        sb.AppendLine();
        sb.AppendLine($"**OperationId:** `{report.OperationId}`");
        sb.AppendLine($"**GeneratedAt:** {report.GeneratedAt:O}");
        sb.AppendLine($"**Passed:** {report.Passed}");
        sb.AppendLine($"**InfectedFiles:** {report.InfectedFiles}");
        sb.AppendLine($"**TotalFiles:** {report.TotalFiles}");
        sb.AppendLine($"**Recommendation:** {report.Recommendation}");
        sb.AppendLine();
        if (report.FailedConditions.Count > 0)
        {
            sb.AppendLine("## Failed Conditions");
            sb.AppendLine();
            foreach (var condition in report.FailedConditions)
            {
                sb.AppendLine($"- {condition}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"**AuditReport:** `{report.AuditReportPath}`");
        return sb.ToString();
    }

    private static async Task ExecuteArchitectureCleanupFreezeAsync(CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("eval"));
        Directory.CreateDirectory(output);
        var runner = new ArchitectureCleanupFreezeRunner();
        var report = runner.BuildFreeze(Directory.GetCurrentDirectory());
        var jp = Path.Combine(output, "architecture-cleanup-freeze.json");
        var mp = Path.Combine(output, "architecture-cleanup-freeze.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ArchitectureCleanupFreezeRunner.BuildMarkdown("Architecture Cleanup Freeze", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Architecture cleanup freeze written: {jp}");

        var docsDir = Path.GetFullPath("docs");
        Directory.CreateDirectory(docsDir);
        var docsMp = Path.Combine(docsDir, "ContextCore_Architecture_Cleanup_Freeze.md");
        await WriteTextAsync(ArchitectureCleanupFreezeRunner.BuildMarkdown("ContextCore Architecture Cleanup Freeze", report), docsMp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Architecture cleanup freeze docs written: {docsMp}");
    }

    private static async Task ExecuteArchitectureCleanupFreezeGateAsync(CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("eval"));
        Directory.CreateDirectory(output);
        var freezePath = Path.Combine(output, "architecture-cleanup-freeze.json");
        var freezeReport = await ReadJsonFileAsync<ArchitectureCleanupFreezeReport>(freezePath, ct).ConfigureAwait(false);
        var gateRunner = new ArchitectureCleanupFreezeGateRunner();
        var report = gateRunner.BuildGateReport(freezeReport);
        var jp = Path.Combine(output, "architecture-cleanup-freeze-gate.json");
        var mp = Path.Combine(output, "architecture-cleanup-freeze-gate.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ArchitectureCleanupFreezeGateRunner.BuildMarkdown("Architecture Cleanup Freeze Gate", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Architecture cleanup freeze gate written: {jp}");
    }
}
