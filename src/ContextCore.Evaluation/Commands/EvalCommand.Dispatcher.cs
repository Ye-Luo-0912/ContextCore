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
        s_registry.RegisterCommandOnly("run");
        s_registry.RegisterCommandOnly("report");
        s_registry.RegisterCommandOnly("perf");
        s_registry.RegisterCommandOnly("perf-scale");
        s_registry.RegisterCommandOnly("retrieval");
        s_registry.RegisterCommandOnly("attention-profile-selection");
        s_registry.RegisterCommandOnly("guarded-rerank-comparison");
        s_registry.RegisterCommandOnly("guarded-order-quality");
        s_registry.RegisterCommandOnly("guarded-profile-sweep");
        s_registry.RegisterCommandOnly("planning-shadow");
        s_registry.RegisterCommandOnly("planning-shadow-quality");
        s_registry.RegisterCommandOnly("planning-shadow-recall-loss");
        s_registry.RegisterCommandOnly("planning-optin-comparison");
        s_registry.RegisterCommandOnly("planning-optin-fallback-analysis");
        s_registry.RegisterCommandOnly("planning-optin-constraint-safety");
        s_registry.RegisterCommandOnly("extended-failure-triage");
        s_registry.RegisterCommandOnly("export-learning-features");
        s_registry.RegisterCommandOnly("learning-dataset-quality");
        s_registry.RegisterCommandOnly("router-intent-baseline");
        s_registry.RegisterCommandOnly("router-shadow-trace-quality");
        s_registry.RegisterCommandOnly("router-intent-shadow-eval");
        s_registry.RegisterCommandOnly("router-disagreement-triage");
        s_registry.RegisterCommandOnly("router-guarded-optin-readiness-gate");
        s_registry.RegisterCommandOnly("learning-readiness-freeze-report");
        s_registry.RegisterCommandOnly("learning-runtime-change-readiness-gate");
        s_registry.RegisterCommandOnly("learning-feedback-summary");
        s_registry.RegisterCommandOnly("export-learning-feedback");
        s_registry.RegisterCommandOnly("learning-feedback-review-summary");
        s_registry.RegisterCommandOnly("learning-feedback-feature-candidates");
        s_registry.RegisterCommandOnly("learning-feedback-quality");
        s_registry.RegisterCommandOnly("learning-feedback-review-smoke");
        s_registry.RegisterCommandOnly("learning-approved-feedback-dataset-gate");
        s_registry.RegisterCommandOnly("submit-learning-feedback");
        s_registry.RegisterCommandOnly("learning-feedback-smoke");
        s_registry.RegisterCommandOnly("learning-baseline");
        s_registry.RegisterCommandOnly("learning-baseline-router");
        s_registry.RegisterCommandOnly("learning-baseline-ranker");
        s_registry.RegisterCommandOnly("learning-ranker-ablation");
        s_registry.RegisterCommandOnly("learning-ranker-weight-sweep");
        s_registry.RegisterCommandOnly("learning-ranker-residual-audit");
        s_registry.RegisterCommandOnly("learning-hard-negatives");
        s_registry.RegisterCommandOnly("learning-lifecycle-aware-ranker");
        s_registry.RegisterCommandOnly("lifecycle-ranker-shadow");
        s_registry.RegisterCommandOnly("ranker-shadow-trace-quality");
        s_registry.RegisterCommandOnly("candidate-reranker-feature-completeness");
        s_registry.RegisterCommandOnly("candidate-reranker-shadow-eval");
        s_registry.RegisterCommandOnly("candidate-reranker-shadow-failure-audit");
        s_registry.RegisterCommandOnly("candidate-reranker-score-distribution");
        s_registry.RegisterCommandOnly("candidate-reranker-listwise-calibration");
        s_registry.RegisterCommandOnly("candidate-reranker-formal-priority-alignment");
        s_registry.RegisterCommandOnly("candidate-reranker-shadow-trace-quality");
        s_registry.RegisterCommandOnly("graph-expansion-shadow-trace-quality");
        s_registry.RegisterCommandOnly("graph-expansion-optin-comparison");
        s_registry.RegisterCommandOnly("graph-expansion-guarded-optin-gate");
        s_registry.RegisterCommandOnly("vector-reindex-plan");
        s_registry.RegisterCommandOnly("vector-reindex-apply");
        s_registry.RegisterCommandOnly("vector-index-diagnostics");
        s_registry.RegisterCommandOnly("vector-index-coverage");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-coverage");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-backfill-plan");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-backfill-apply");
        s_registry.RegisterCommandOnly("vector-query-preview");
        s_registry.RegisterCommandOnly("vector-query-shadow-eval");
        s_registry.RegisterCommandOnly("vector-query-profile-sweep");
        s_registry.RegisterCommandOnly("vector-residual-risk-audit");
        s_registry.RegisterCommandOnly("vector-recall-loss-audit");
        s_registry.RegisterCommandOnly("vector-safe-recall-recovery");
        s_registry.RegisterCommandOnly("vector-ranker-fusion-shadow");
        s_registry.RegisterCommandOnly("vector-representation-benchmark");
        s_registry.RegisterCommandOnly("vector-query-expansion-shadow");
        s_registry.RegisterCommandOnly("vector-retrieval-shadow-readiness-gate");
        s_registry.RegisterCommandOnly("embedding-provider-smoke");
        s_registry.RegisterCommandOnly("vector-provider-comparison");
        s_registry.RegisterCommandOnly("vector-qwen3-shadow-eval");
        s_registry.RegisterCommandOnly("vector-qwen3-readiness-gate");
        s_registry.RegisterCommandOnly("vector-provider-configuration-sanity-audit");
        s_registry.RegisterCommandOnly("vector-provider-comparison-freeze");
        s_registry.RegisterCommandOnly("vector-hybrid-preview");
        s_registry.RegisterCommandOnly("vector-hybrid-shadow-eval");
        s_registry.RegisterCommandOnly("vector-hybrid-readiness-gate");
        s_registry.RegisterCommandOnly("vector-hybrid-recall-regression-audit");
        s_registry.RegisterCommandOnly("vector-hybrid-freeze-gate");
        s_registry.RegisterCommandOnly("vector-retrieval-dataset-alignment-audit");
        s_registry.RegisterCommandOnly("vector-retrieval-dataset-alignment-audit-a3");
        s_registry.RegisterCommandOnly("vector-retrieval-dataset-alignment-audit-extended");
        s_registry.RegisterCommandOnly("vector-eligibility-recall-loss-triage");
        s_registry.RegisterCommandOnly("vector-eligibility-recall-loss-triage-a3");
        s_registry.RegisterCommandOnly("vector-eligibility-recall-loss-triage-extended");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-repair-plan");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-repair-plan-a3");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-repair-plan-extended");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-candidates-generate");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-candidates");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-summary");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-sidecar-preview");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-smoke");
        s_registry.RegisterCommandOnly("vector-sidecar-eligibility-preview");
        s_registry.RegisterCommandOnly("vector-sidecar-eligibility-recheck");
        s_registry.RegisterCommandOnly("vector-sidecar-eligibility-quality");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-batch-create");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-batch-export");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-batch-import");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-batch-validate");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-batch-apply-preview");
        s_registry.RegisterCommandOnly("vector-lifecycle-metadata-review-batch-import-smoke");
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
        s_registry.RegisterCommandOnly("vector-guarded-formal-retrieval-preview");
        s_registry.RegisterCommandOnly("vector-guarded-formal-retrieval-preview-gate");
        s_registry.RegisterCommandOnly("vector-shadow-package-comparison");
        s_registry.RegisterCommandOnly("vector-shadow-package-comparison-gate");
        s_registry.RegisterCommandOnly("vector-scoped-formal-preview-optin-plan");
        s_registry.RegisterCommandOnly("vector-scoped-formal-preview-optin-smoke");
        s_registry.RegisterCommandOnly("vector-scoped-formal-preview-optin-gate");
        s_registry.RegisterCommandOnly("vector-limited-formal-preview-observation");
        s_registry.RegisterCommandOnly("vector-limited-formal-preview-observation-gate");
        s_registry.RegisterCommandOnly("vector-formal-preview-freeze-gate");
        s_registry.RegisterCommandOnly("vector-scoped-runtime-experiment-plan");
        s_registry.RegisterCommandOnly("vector-scoped-runtime-experiment-dry-run");
        s_registry.RegisterCommandOnly("vector-scoped-runtime-experiment-gate");
        s_registry.RegisterCommandOnly("vector-scoped-runtime-experiment-proposal");
        s_registry.RegisterCommandOnly("vector-scoped-runtime-experiment-proposal-gate");
        s_registry.RegisterCommandOnly("vector-scoped-runtime-experiment-config-preview");
        s_registry.RegisterCommandOnly("vector-formal-retrieval-integration-plan");
        s_registry.RegisterCommandOnly("vector-formal-retrieval-integration-plan-gate");
        s_registry.RegisterCommandOnly("vector-shadow-formal-retrieval-adapter-plan");
        s_registry.RegisterCommandOnly("vector-shadow-formal-retrieval-adapter-plan-gate");
        s_registry.RegisterCommandOnly("vector-shadow-formal-retrieval-adapter");
        s_registry.RegisterCommandOnly("vector-shadow-formal-retrieval-adapter-gate");
        s_registry.RegisterCommandOnly("vector-formal-adapter-package-shadow-comparison");
        s_registry.RegisterCommandOnly("vector-formal-adapter-package-shadow-comparison-gate");
        s_registry.RegisterCommandOnly("vector-graph-retrieval-quality-audit");
        s_registry.RegisterCommandOnly("vector-graph-retrieval-quality-gate");
        s_registry.RegisterCommandOnly("vector-retrieval-quality-repair-preview");
        s_registry.RegisterCommandOnly("vector-retrieval-quality-repair-gate");
        s_registry.RegisterCommandOnly("vector-runtime-observable-feature-contract");
        s_registry.RegisterCommandOnly("vector-runtime-observable-feature-contract-gate");
        s_registry.RegisterCommandOnly("vector-runtime-feature-derivation-preview");
        s_registry.RegisterCommandOnly("vector-runtime-feature-derivation-gate");
        s_registry.RegisterCommandOnly("vector-runtime-feature-derivation-repair");
        s_registry.RegisterCommandOnly("vector-runtime-feature-derivation-repair-gate");
        s_registry.RegisterCommandOnly("vector-runtime-feature-derivation-failure-freeze");
        s_registry.RegisterCommandOnly("vector-graph-hub-noise-control-preview");
        s_registry.RegisterCommandOnly("vector-graph-hub-noise-control-gate");
        s_registry.RegisterCommandOnly("vector-query-driven-candidate-source-repair");
        s_registry.RegisterCommandOnly("vector-query-driven-candidate-source-repair-gate");
        s_registry.RegisterCommandOnly("vector-formal-retrieval-integration-freeze");
        s_registry.RegisterCommandOnly("vector-adapter-noop-binding-plan");
        s_registry.RegisterCommandOnly("vector-formal-retrieval-integration-freeze-gate");
        s_registry.RegisterCommandOnly("vector-adapter-noop-binding-smoke");
        s_registry.RegisterCommandOnly("vector-adapter-noop-binding-gate");
        s_registry.RegisterCommandOnly("vector-scoped-shadow-adapter-invocation");
        s_registry.RegisterCommandOnly("vector-scoped-shadow-adapter-invocation-gate");
        s_registry.RegisterCommandOnly("vector-mainline-shadow-adapter-package-comparison");
        s_registry.RegisterCommandOnly("vector-mainline-shadow-adapter-package-comparison-gate");
        s_registry.RegisterCommandOnly("architecture-cleanup-plan");
        s_registry.RegisterCommandOnly("architecture-cleanup-readiness-gate");
        s_registry.RegisterCommandOnly("architecture-cleanup-freeze");
        s_registry.RegisterCommandOnly("architecture-cleanup-freeze-gate");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-plan");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-plan-gate");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-dry-run");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-dry-run-gate");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-activation-preflight");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-activation-preflight-gate");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-observation-window");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-observation-window-gate");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-observation-hardening");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-observation-hardening-gate");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-observation-freeze");
        s_registry.RegisterCommandOnly("controlled-applied-merge-runtime-preview-observation-freeze-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-approval-plan");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-approval-plan-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-authorization");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-authorization-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-authorization-hardening");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-authorization-hardening-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-preparation");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-preparation-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-dry-run");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-dry-run-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-window-preflight");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-window-preflight-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-window-noop-execution");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-window-noop-execution-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-live-readiness-freeze");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-activation-live-readiness-freeze-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-execution-plan");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-execution-plan-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-execution");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-execution-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-observation");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-observation-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-summary-freeze");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-summary-freeze-gate");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-closeout");
        s_registry.RegisterCommandOnly("scoped-runtime-preview-live-activation-closeout-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-readiness-audit");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-readiness-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-plan");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-plan-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-evidence-seal");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-evidence-seal-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-intake");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-intake-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-submission-pack");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-submission-pack-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-dry-run");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-dry-run-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-dry-run-negative-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-dry-run-negative-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-quarantine-scan");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-quarantine-scan-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-quarantine-negative-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-quarantine-negative-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-quarantine-positive-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-external-approval-quarantine-positive-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-trust-chain-validation-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-trust-chain-validation-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-policy-authority-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-policy-authority-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-grant-application-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-grant-application-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-rollback-readiness-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-rollback-readiness-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-operator-sign-off-matrix");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-operator-sign-off-matrix-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-pre-crossing-final-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-pre-crossing-final-gate-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-dedicated-crossing-dry-run");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-dedicated-crossing-dry-run-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-dedicated-crossing-execution");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-dedicated-crossing-execution-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-runtime-activation-dry-run");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-runtime-activation-dry-run-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-runtime-activation-artifact-integrity");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-scoped-live-activation-observation");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-scoped-live-activation-observation-gate");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout");
        s_registry.RegisterCommandOnly("formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout-gate");
        s_registry.RegisterCommandOnly("v14-runtime-trace-smoke");
        s_registry.RegisterCommandOnly("v16_2-collect-production-trace");
        s_registry.RegisterCommandOnly("v16_4-native-trace-collect");
        s_registry.RegisterCommandOnly("v16_3-native-trace-readiness-gate");
        s_registry.RegisterCommandOnly("v16_6-native-production-trace-plan");
        s_registry.RegisterCommandOnly("v16_7-controlled-replay-native-trace");
        s_registry.RegisterCommandOnly("v16_9-live-capture-candidate-gate");
        s_registry.RegisterCommandOnly("v16_10-live-capture-authorized-simulation-gate");
        s_registry.RegisterCommandOnly("v16_11-live-capture-execution-skeleton");
        s_registry.RegisterCommandOnly("v16_11-phase-ledger-gate");
        s_registry.RegisterCommandOnly("v16_12-native-production-trace-execution-design-review");
        s_registry.RegisterCommandOnly("v16_13-native-production-trace-execution-plan");
        s_registry.RegisterCommandOnly("v16_14-native-production-trace-execution-authorization-contract");
        s_registry.RegisterCommandOnly("v16_15-native-production-trace-execution-endpoint-design");
        s_registry.RegisterCommandOnly("v16_16-native-production-trace-execution-endpoint-implementation-plan");
        s_registry.RegisterCommandOnly("v16_17-native-production-trace-execution-endpoint-implementation-approval");
        s_registry.RegisterCommandOnly("v16_18-native-production-trace-execution-endpoint-implementation-final-approval");
        s_registry.RegisterCommandOnly("v16_19-native-production-trace-endpoint-dossier");
        s_registry.RegisterCommandOnly("v16_20-native-production-trace-endpoint-decision-record");
        s_registry.RegisterCommandOnly("v16_21-native-production-trace-endpoint-enforcement-validation");
        s_registry.RegisterCommandOnly("v16_22-native-production-trace-endpoint-review-framework");
        s_registry.RegisterCommandOnly("v16_23-native-production-trace-endpoint-approval-validator-plan");
        s_registry.RegisterCommandOnly("v16_24-native-production-trace-endpoint-dry-run-architecture");
        s_registry.RegisterCommandOnly("v16_25-native-production-trace-endpoint-dry-run-harness-plan");
        s_registry.RegisterCommandOnly("v16_26-native-production-trace-endpoint-approval-validator-dry-run-harness");
        s_registry.RegisterCommandOnly("v16_27-native-production-trace-endpoint-approval-validator-repeated-dry-run");
        s_registry.RegisterCommandOnly("v16_28-native-production-trace-endpoint-approval-validator-failure-injection");
        s_registry.RegisterCommandOnly("dto-split-plan");
        s_registry.RegisterCommandOnly("dto-split-readiness-gate");
        s_registry.RegisterCommandOnly("vector-retrieval-eval-protocol-audit");
        s_registry.RegisterCommandOnly("vector-candidate-source-discriminability-audit");
        s_registry.RegisterCommandOnly("vector-retrieval-eval-protocol-gate");
        s_registry.RegisterCommandOnly("vector-input-metadata-enrichment-preview");
        s_registry.RegisterCommandOnly("vector-input-metadata-enrichment-gate");
        s_registry.RegisterCommandOnly("vector-enriched-candidate-source-repair-recheck");
        s_registry.RegisterCommandOnly("vector-enriched-candidate-source-repair-recheck-gate");
        s_registry.RegisterCommandOnly("vector-source-aware-ranking-repair");
        s_registry.RegisterCommandOnly("vector-source-aware-ranking-repair-gate");
        s_registry.RegisterCommandOnly("vector-output-token-priority-shadow");
        s_registry.RegisterCommandOnly("vector-output-token-priority-shadow-gate");
        s_registry.RegisterCommandOnly("vector-formal-adapter-input-contract");
        s_registry.RegisterCommandOnly("vector-formal-adapter-input-contract-gate");
        s_registry.RegisterCommandOnly("vector-formal-retrieval-integration-decision");
        s_registry.RegisterCommandOnly("vector-formal-retrieval-integration-decision-gate");
        s_registry.RegisterCommandOnly("project-state-audit");
        s_registry.RegisterCommandOnly("mainline-gap-map");
        s_registry.RegisterCommandOnly("generated-artifact-path-hygiene-audit");
        s_registry.RegisterCommandOnly("generated-artifact-path-hygiene-gate");
        s_registry.RegisterCommandOnly("foundation-freeze-report");
        s_registry.RegisterCommandOnly("foundation-release-candidate-gate");
        s_registry.RegisterCommandOnly("foundation-reproducibility-check");
        s_registry.RegisterCommandOnly("service-foundation-status-smoke");
        s_registry.RegisterCommandOnly("service-readiness-api-smoke");
        s_registry.RegisterCommandOnly("service-api-security-diagnostics");
        s_registry.RegisterCommandOnly("service-report-navigation-smoke");
        s_registry.RegisterCommandOnly("service-api-contract-report");
        s_registry.RegisterCommandOnly("service-api-contract-freeze-gate");
        s_registry.RegisterCommandOnly("service-auth-diagnostics");
        s_registry.RegisterCommandOnly("service-auth-enforcement-smoke");
        s_registry.RegisterCommandOnly("service-deployment-profile-gate");
        s_registry.RegisterCommandOnly("service-openapi-contract-export");
        s_registry.RegisterCommandOnly("service-client-contract-snapshot");
        s_registry.RegisterCommandOnly("service-api-contract-drift-gate");
        s_registry.RegisterCommandOnly("service-hosted-deployment-smoke");
        s_registry.RegisterCommandOnly("service-readonly-runtime-smoke");
        s_registry.RegisterCommandOnly("service-hosted-api-contract-smoke");
        s_registry.RegisterCommandOnly("service-foundation-freeze-gate");
        s_registry.RegisterCommandOnly("relation-expansion-profile-shadow");
        s_registry.RegisterCommandOnly("relation-corpus-hygiene");
        s_registry.RegisterCommandOnly("relation-expansion-shadow-eval");
        s_registry.RegisterCommandOnly("learning-ranker-analysis");
        s_registry.RegisterCommandOnly("storage-check");
        s_registry.RegisterCommandOnly("storage-boundary-report");
        s_registry.RegisterCommandOnly("postgres-storage-diagnostics");
        s_registry.RegisterCommandOnly("postgres-migration-preview");
        s_registry.RegisterCommandOnly("postgres-migration-apply");
        s_registry.RegisterCommandOnly("postgres-migration-smoke");
        s_registry.RegisterCommandOnly("postgres-relation-store-diagnostics");
        s_registry.RegisterCommandOnly("postgres-relation-store-parity");
        s_registry.RegisterCommandOnly("postgres-relation-review-diagnostics");
        s_registry.RegisterCommandOnly("postgres-relation-review-parity");
        s_registry.RegisterCommandOnly("postgres-relation-governance-parity");
        s_registry.RegisterCommandOnly("postgres-relation-governance-readiness-gate");
        s_registry.RegisterCommandOnly("postgres-relation-dual-write-smoke");
        s_registry.RegisterCommandOnly("postgres-relation-dual-write-quality");
        s_registry.RegisterCommandOnly("postgres-relation-shadow-read-smoke");
        s_registry.RegisterCommandOnly("postgres-relation-shadow-read-quality");
        s_registry.RegisterCommandOnly("postgres-relation-provider-switch-smoke");
        s_registry.RegisterCommandOnly("postgres-relation-provider-switch-gate");
        s_registry.RegisterCommandOnly("postgres-relation-runtime-canary");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-service-mode-smoke");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-service-mode-gate");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-extended-canary");
        s_registry.RegisterCommandOnly("postgres-relation-selected-workspace-canary");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-expansion-plan");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-expansion-smoke");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-expansion-gate");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-observation-window");
        s_registry.RegisterCommandOnly("postgres-relation-scoped-observation-quality");
        s_registry.RegisterCommandOnly("postgres-relation-selected-normal-workspace-canary");
        s_registry.RegisterCommandOnly("postgres-relation-limited-normal-scope-observation");
        s_registry.RegisterCommandOnly("postgres-relation-limited-normal-scope-quality");
        s_registry.RegisterCommandOnly("postgres-relation-multi-normal-scope-canary");
        s_registry.RegisterCommandOnly("postgres-relation-multi-normal-scope-quality");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-diagnostics");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-parity");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-readiness-gate");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-dual-write-smoke");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-shadow-read-smoke");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-provider-quality");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-scoped-service-mode-smoke");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-scoped-service-mode-gate");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-selected-normal-scope-canary");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-limited-scope-observation");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-limited-scope-quality");
        s_registry.RegisterCommandOnly("postgres-learning-feedback-freeze-gate");
        s_registry.RegisterCommandOnly("postgres-job-queue-diagnostics");
        s_registry.RegisterCommandOnly("postgres-job-queue-parity");
        s_registry.RegisterCommandOnly("postgres-job-queue-lease-smoke");
        s_registry.RegisterCommandOnly("postgres-job-queue-dual-write-smoke");
        s_registry.RegisterCommandOnly("postgres-job-queue-shadow-read-smoke");
        s_registry.RegisterCommandOnly("postgres-job-queue-provider-quality");
        s_registry.RegisterCommandOnly("postgres-job-queue-scoped-worker-canary");
        s_registry.RegisterCommandOnly("postgres-job-queue-scoped-worker-quality");
        s_registry.RegisterCommandOnly("postgres-job-queue-limited-worker-scope-observation");
        s_registry.RegisterCommandOnly("postgres-job-queue-limited-worker-scope-quality");
        s_registry.RegisterCommandOnly("postgres-job-queue-freeze-gate");
        s_registry.RegisterCommandOnly("postgres-vector-diagnostics");
        s_registry.RegisterCommandOnly("postgres-vector-compatibility");
        s_registry.RegisterCommandOnly("postgres-vector-provider-smoke");
        s_registry.RegisterCommandOnly("postgres-vector-parity");
        s_registry.RegisterCommandOnly("postgres-vector-provider-scoped-reindex-plan");
        s_registry.RegisterCommandOnly("postgres-vector-provider-scoped-reindex-apply");
        s_registry.RegisterCommandOnly("postgres-vector-provider-scoped-reindex-quality");
        s_registry.RegisterCommandOnly("postgres-vector-query-preview");
        s_registry.RegisterCommandOnly("postgres-vector-shadow-eval");
        s_registry.RegisterCommandOnly("postgres-vector-shadow-eval-a3");
        s_registry.RegisterCommandOnly("postgres-vector-shadow-eval-extended");
        s_registry.RegisterCommandOnly("postgres-vector-freeze-gate");
        s_registry.RegisterCommandOnly("chunk-ablation");
        s_registry.RegisterCommandOnly("idle-unload");
        s_registry.RegisterCommandOnly("fs-vector-perf");
        return s_registry;
    }

    /// <summary>打印 eval 用法信息。从 EvalCommand.ExecuteAsync 提取。</summary>
    private static void PrintUsage()
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
            Console.WriteLine("  eval formal-retrieval-promotion-approval [--approved-by <name>] [--approval-id <id>]");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-gate [--approved-by <name>] [--approval-id <id>]");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-evidence-seal");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-evidence-seal-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-intake");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-intake-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-submission-pack");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-submission-pack-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-dry-run");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-dry-run-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-dry-run-negative-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-dry-run-negative-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-quarantine-scan");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-quarantine-scan-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-quarantine-negative-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-quarantine-negative-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-quarantine-positive-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-external-approval-quarantine-positive-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-trust-chain-validation-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-trust-chain-validation-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-policy-authority-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-policy-authority-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-grant-application-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-grant-application-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-rollback-readiness-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-rollback-readiness-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-operator-sign-off-matrix");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-operator-sign-off-matrix-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-pre-crossing-final-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-pre-crossing-final-gate-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-dedicated-crossing-dry-run");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-dedicated-crossing-dry-run-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-dedicated-crossing-execution");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-dedicated-crossing-execution-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-runtime-activation-dry-run");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-runtime-activation-dry-run-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-runtime-activation-artifact-integrity");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-scoped-live-activation-observation");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-scoped-live-activation-observation-gate");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout");
            Console.WriteLine("  eval formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout-gate");
        Console.WriteLine("  eval v14-runtime-trace-smoke (real package build trace)");
        Console.WriteLine("  eval v16_3-native-trace-readiness-gate (native trace schema contract, privacy boundary, safety gate)");
        Console.WriteLine("  eval v16_4-native-trace-collect [--runId <runId>] (native runtime candidate-scoring trace dry run)");
        Console.WriteLine("  eval v16_6-native-production-trace-plan [--mode <PreviewOnly|ControlledReplay>] (production trace acquisition plan)");
        Console.WriteLine("  eval v16_7-controlled-replay-native-trace --workspaceId <id> --collectionId <id> [--runId <id>] (controlled replay trace from real stores)");
            Console.WriteLine("  eval v16_9-live-capture-candidate-gate (LiveCapture authorization failure dry-run gate)");
            Console.WriteLine("  eval v16_10-live-capture-authorized-simulation-gate (LiveCapture authorized simulation & no-execution proof)");
            Console.WriteLine("  eval v16_11-live-capture-execution-skeleton [--mode LiveCapture --confirm-live-capture --capture-token <tok> --workspaceId <id> --collectionId <id> --runId <id>] (execution skeleton, hard-blocked)");
            Console.WriteLine("  eval v16_11-phase-ledger-gate (phase ledger & final acceptance boundary gate)");
            Console.WriteLine("  eval v16_12-native-production-trace-execution-design-review (native production trace execution design review)");
            Console.WriteLine("  eval v16_13-native-production-trace-execution-plan (native production trace execution plan)");
            Console.WriteLine("  eval v16_14-native-production-trace-execution-authorization-contract (native production trace execution authorization contract)");
            Console.WriteLine("  eval v16_15-native-production-trace-execution-endpoint-design (native production trace execution endpoint implementation design)");
            Console.WriteLine("  eval v16_16-native-production-trace-execution-endpoint-implementation-plan (native production trace execution endpoint implementation plan)");
            Console.WriteLine("  eval v16_17-native-production-trace-execution-endpoint-implementation-approval (native production trace execution endpoint implementation approval)");
            Console.WriteLine("  eval v16_18-native-production-trace-execution-endpoint-implementation-final-approval (native production trace execution endpoint implementation final approval)");
            Console.WriteLine("  eval v16_19-native-production-trace-endpoint-dossier (native production trace endpoint authorization dossier & go/no-go protocol)");
            Console.WriteLine("  eval v16_20-native-production-trace-endpoint-decision-record (native production trace endpoint authorization decision record & no-go enforcement)");
            Console.WriteLine("  eval v16_21-native-production-trace-endpoint-enforcement-validation (native production trace endpoint no-go enforcement validation & generator parity closure)");
            Console.WriteLine("  eval v16_22-native-production-trace-endpoint-review-framework (native production trace endpoint explicit approval artifact review framework & governance)");
            Console.WriteLine("  eval v16_23-native-production-trace-endpoint-approval-validator-plan (native production trace endpoint approval validator implementation plan & verification protocol)");
            Console.WriteLine("  eval v16_24-native-production-trace-endpoint-dry-run-architecture (native production trace endpoint approval validator dry-run simulation architecture & evidence harness)");
            Console.WriteLine("  eval v16_25-native-production-trace-endpoint-dry-run-harness-plan (native production trace endpoint approval validator dry-run harness implementation plan)");
            Console.WriteLine("  eval v16_26-native-production-trace-endpoint-approval-validator-dry-run-harness (native production trace endpoint approval validator synthetic dry-run harness execution)");
            Console.WriteLine("  eval v16_27-native-production-trace-endpoint-approval-validator-repeated-dry-run (native production trace endpoint approval validator repeated dry-run determinism audit)");
            Console.WriteLine("  eval v16_28-native-production-trace-endpoint-approval-validator-failure-injection (native production trace endpoint approval validator failure injection audit)");
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

        if (string.Equals(subcommand, "vector-guarded-formal-retrieval-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-guarded-formal-retrieval-preview-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorGuardedFormalRetrievalPreviewAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-shadow-package-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-shadow-package-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorShadowPackageComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-scoped-formal-preview-optin-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-formal-preview-optin-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-formal-preview-optin-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedFormalPreviewOptInAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-limited-formal-preview-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-limited-formal-preview-observation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteLimitedFormalPreviewObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-formal-preview-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteVectorFormalPreviewFreezeGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteExplicitScopedRuntimeExperimentAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-scoped-runtime-experiment-proposal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-proposal-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-runtime-experiment-config-preview", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimeExperimentProposalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "vector-graph-hub-noise-control-preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-graph-hub-noise-control-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteGraphHubNoiseControlAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "vector-adapter-noop-binding-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalIntegrationFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-adapter-noop-binding-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-adapter-noop-binding-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAdapterNoOpBindingSmokeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedShadowAdapterInvocationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteMainlineShadowAdapterPackageComparisonAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "architecture-cleanup-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteArchitectureCleanupPlanAsync(cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewActivationPreflightAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewObservationWindowAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewObservationHardeningAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteControlledAppliedMergeRuntimePreviewObservationFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-approval-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-approval-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewApprovalPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-authorization", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-authorization-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewAuthorizationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewAuthorizationHardeningAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-preparation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-preparation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationPreparationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationWindowPreflightAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationWindowNoOpExecutionAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewActivationLiveReadinessFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationExecutionPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationExecutionAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationSummaryFreezeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "scoped-runtime-preview-live-activation-closeout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "scoped-runtime-preview-live-activation-closeout-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteScopedRuntimePreviewLiveActivationCloseoutAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-readiness-audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionReadinessAuditAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-plan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionPlanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-evidence-seal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-evidence-seal-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalEvidenceSealAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-intake", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-intake-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalIntakeAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-submission-pack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-submission-pack-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalSubmissionPackAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-dry-run-negative-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-dry-run-negative-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-scan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-scan-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalQuarantineScanAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-negative-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-negative-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-positive-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-positive-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-trust-chain-validation-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-trust-chain-validation-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalTrustChainValidationMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-policy-authority-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-policy-authority-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalPolicyAuthorityMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-grant-application-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-grant-application-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalGrantApplicationMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-rollback-readiness-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-rollback-readiness-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalRollbackReadinessMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-operator-sign-off-matrix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-operator-sign-off-matrix-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalOperatorSignOffMatrixAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-pre-crossing-final-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-pre-crossing-final-gate-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalPreCrossingFinalGateAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-dedicated-crossing-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-dedicated-crossing-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-dedicated-crossing-execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-dedicated-crossing-execution-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-runtime-activation-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-runtime-activation-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalRuntimeActivationDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }


        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-scoped-live-activation-observation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-scoped-live-activation-observation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalScopedLiveActivationObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(subcommand, "formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (string.Equals(subcommand, "v14-runtime-trace-smoke", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV14RuntimeTraceSmokeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_2-collect-production-trace", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_2CollectProductionTraceAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_3-native-trace-readiness-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_3NativeTraceReadinessGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_4-native-trace-collect", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_4NativeTraceCollectAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_6-native-production-trace-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_6NativeProductionTracePlanAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_7-controlled-replay-native-trace", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_7ControlledReplayNativeTraceAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_9-live-capture-candidate-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_9LiveCaptureCandidateGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_10-live-capture-authorized-simulation-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_10LiveCaptureAuthorizedSimulationGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_11-live-capture-execution-skeleton", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_11LiveCaptureExecutionSkeletonAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_11-phase-ledger-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_11PhaseLedgerGateAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_12-native-production-trace-execution-design-review", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_12NativeProductionTraceExecutionDesignReviewAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_13-native-production-trace-execution-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_13NativeProductionTraceExecutionPlanAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_14-native-production-trace-execution-authorization-contract", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_14NativeProductionTraceExecutionAuthorizationContractAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_15-native-production-trace-execution-endpoint-design", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_15NativeProductionTraceExecutionEndpointDesignAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_16-native-production-trace-execution-endpoint-implementation-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_16NativeProductionTraceExecutionEndpointImplementationPlanAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_17-native-production-trace-execution-endpoint-implementation-approval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_17NativeProductionTraceExecutionEndpointApprovalAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_18-native-production-trace-execution-endpoint-implementation-final-approval", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_18NativeProductionTraceExecutionEndpointFinalApprovalAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_19-native-production-trace-endpoint-dossier", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_19NativeProductionTraceEndpointDossierAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_20-native-production-trace-endpoint-decision-record", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_20NativeProductionTraceEndpointDecisionRecordAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_21-native-production-trace-endpoint-enforcement-validation", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_21NativeProductionTraceEndpointEnforcementValidationAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_22-native-production-trace-endpoint-review-framework", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_22NativeProductionTraceEndpointReviewFrameworkAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_23-native-production-trace-endpoint-approval-validator-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_23NativeProductionTraceEndpointApprovalValidatorPlanAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_24-native-production-trace-endpoint-dry-run-architecture", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_24NativeProductionTraceEndpointDryRunArchitectureAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_25-native-production-trace-endpoint-dry-run-harness-plan", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_25NativeProductionTraceEndpointDryRunHarnessPlanAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_26-native-production-trace-endpoint-approval-validator-dry-run-harness", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_26NativeProductionTraceEndpointDryRunHarnessAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_27-native-production-trace-endpoint-approval-validator-repeated-dry-run", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_27NativeProductionTraceEndpointRepeatedDryRunAsync(args, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(subcommand, "v16_28-native-production-trace-endpoint-approval-validator-failure-injection", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteV16_28NativeProductionTraceEndpointFailureInjectionAsync(args, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(subcommand, "vector-formal-adapter-input-contract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "vector-formal-adapter-input-contract-gate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteFormalAdapterInputContractAsync(args, subcommand, cancellationToken).ConfigureAwait(false);
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
