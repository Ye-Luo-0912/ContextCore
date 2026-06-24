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

public static partial class EvalCommand
{
    private static async Task ExecuteFormalRetrievalIntegrationPlanAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var promotionDecisionPath = Path.Combine("vector", "v4", "runtime-experiment", "promotion-decision.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var planPath = Path.Combine("vector", "v5", "formal-retrieval-integration-plan.json");
        var p15A3Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15ExtendedPath = Path.Combine("eval", "eval-report-p15-extended.json");
        var promotionDecision = await ReadJsonFileAsync<ScopedRuntimeExperimentObservationFreezeReport>(
                promotionDecisionPath,
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(
                runtimeGatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var existingPlan = await ReadJsonFileAsync<FormalRetrievalIntegrationPlanReport>(
                planPath,
                cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(p15A3Path)
            && IsP15EvalReportPassed(p15ExtendedPath);
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v416PromotionDecision"] = promotionDecisionPath,
            ["learningRuntimeChangeReadinessGate"] = runtimeGatePath,
            ["p15A3"] = p15A3Path,
            ["p15Extended"] = p15ExtendedPath
        };

        var isGate = string.Equals(
            subcommand,
            "vector-formal-retrieval-integration-plan-gate",
            StringComparison.OrdinalIgnoreCase);
        var runner = new FormalRetrievalIntegrationPlanRunner();
        var report = isGate
            ? runner.BuildGate(promotionDecision, runtimeGate, p15Passed, existingPlan, sourceReports)
            : runner.BuildPlan(promotionDecision, runtimeGate, p15Passed, sourceReports);
        var fileName = isGate
            ? "formal-retrieval-integration-plan-gate"
            : "formal-retrieval-integration-plan";
        var title = isGate
            ? "Vector Formal Retrieval Integration Plan Gate"
            : "Vector Formal Retrieval Integration Plan";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalIntegrationPlanRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Vector formal retrieval integration plan artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.PlanPassed}; mode={report.AllowedMode}; next={report.RequiredNextPhase}; recommendation={report.Recommendation}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}");
    }


    private static async Task ExecuteProjectStateAuditAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var evalDirectory = Path.GetFullPath("eval");
        var docsDirectory = Path.GetFullPath("docs");
        Directory.CreateDirectory(evalDirectory);
        Directory.CreateDirectory(docsDirectory);

        var runner = new ProjectStateAuditRunner();
        var isGapMap = string.Equals(subcommand, "mainline-gap-map", StringComparison.OrdinalIgnoreCase);
        if (isGapMap)
        {
            var report = runner.BuildMainlineGapMap(Directory.GetCurrentDirectory());
            var jsonPath = Path.Combine(evalDirectory, "mainline-gap-map.json");
            var markdownPath = Path.Combine(evalDirectory, "mainline-gap-map.md");
            var docPath = Path.Combine(docsDirectory, "ContextCore_Mainline_Gap_Map.md");
            var markdown = ProjectStateAuditRunner.BuildMainlineGapMapMarkdown(report);
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(markdown, markdownPath, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(markdown, docPath, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[Eval] Mainline gap map written: {jsonPath}");
            Console.WriteLine($"[Eval] status={report.CurrentOverallStatus}; recommendation={report.Recommendation}; gaps={report.MainlineGaps.Count}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}");
            return;
        }

        var audit = runner.BuildProjectStateAudit(Directory.GetCurrentDirectory());
        var auditJsonPath = Path.Combine(evalDirectory, "project-state-audit.json");
        var auditMarkdownPath = Path.Combine(evalDirectory, "project-state-audit.md");
        var auditDocPath = Path.Combine(docsDirectory, "ContextCore_Project_State_Audit.md");
        var auditMarkdown = ProjectStateAuditRunner.BuildProjectStateMarkdown(audit);
        await WriteTextAsync(JsonSerializer.Serialize(audit, JsonOptions), auditJsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(auditMarkdown, auditMarkdownPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(auditMarkdown, auditDocPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Project state audit written: {auditJsonPath}");
        Console.WriteLine($"[Eval] status={audit.CurrentOverallStatus}; recommendation={audit.Recommendation}; ready={audit.ReadyCapabilities.Count}; preview={audit.PreviewOnlyCapabilities.Count}; blocked={audit.BlockedCapabilities.Count}; formalRetrieval={audit.FormalRetrievalAllowed}; runtimeSwitch={audit.RuntimeSwitchAllowed}");
    }


    private static async Task ExecuteShadowFormalRetrievalAdapterPlanAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var projectStateAuditPath = Path.Combine("eval", "project-state-audit.json");
        var formalPreviewFreezePath = Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json");
        var promotionDecisionPath = Path.Combine("vector", "v4", "runtime-experiment", "promotion-decision.json");
        var guardedRuntimeExperimentPath = Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-gate.json");
        var shadowPackageComparisonPath = Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var planPath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan.json");

        var projectStateAudit = await ReadJsonFileAsync<ProjectStateAuditReport>(projectStateAuditPath, cancellationToken)
            .ConfigureAwait(false);
        var formalPreviewFreeze = await ReadJsonFileAsync<VectorFormalPreviewFreezeReport>(formalPreviewFreezePath, cancellationToken)
            .ConfigureAwait(false);
        var promotionDecision = await ReadJsonFileAsync<ScopedRuntimeExperimentObservationFreezeReport>(promotionDecisionPath, cancellationToken)
            .ConfigureAwait(false);
        var guardedRuntimeExperiment = await ReadJsonFileAsync<GuardedScopedRuntimeExperimentReport>(guardedRuntimeExperimentPath, cancellationToken)
            .ConfigureAwait(false);
        var shadowPackageComparison = await ReadJsonFileAsync<VectorShadowPackageComparisonReport>(shadowPackageComparisonPath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var existingPlan = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterPlanReport>(planPath, cancellationToken)
            .ConfigureAwait(false);
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v50ProjectStateAudit"] = projectStateAuditPath,
            ["v4FormalPreviewFreeze"] = formalPreviewFreezePath,
            ["v416PromotionDecision"] = promotionDecisionPath,
            ["v414GuardedRuntimeExperiment"] = guardedRuntimeExperimentPath,
            ["v42ShadowPackageComparison"] = shadowPackageComparisonPath,
            ["learningRuntimeChangeReadinessGate"] = runtimeGatePath
        };

        var isGate = string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-plan-gate", StringComparison.OrdinalIgnoreCase);
        var runner = new ShadowFormalRetrievalAdapterPlanRunner();
        var report = isGate
            ? runner.BuildGate(
                projectStateAudit,
                formalPreviewFreeze,
                promotionDecision,
                guardedRuntimeExperiment,
                shadowPackageComparison,
                runtimeGate,
                existingPlan,
                sourceReports)
            : runner.BuildPlan(
                projectStateAudit,
                formalPreviewFreeze,
                promotionDecision,
                guardedRuntimeExperiment,
                shadowPackageComparison,
                runtimeGate,
                sourceReports);
        var fileName = isGate
            ? "shadow-formal-retrieval-adapter-plan-gate"
            : "shadow-formal-retrieval-adapter-plan";
        var title = isGate
            ? "Vector Shadow Formal Retrieval Adapter Plan Gate"
            : "Vector Shadow Formal Retrieval Adapter Plan";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ShadowFormalRetrievalAdapterPlanRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Shadow formal retrieval adapter plan artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.PlanPassed}; recommendation={report.Recommendation}; mode={report.AllowedMode}; vector={report.VectorProviderSource}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteShadowFormalRetrievalAdapterAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var planGatePath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan-gate.json");
        var planFallbackPath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var planGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterPlanReport>(planGatePath, cancellationToken)
            .ConfigureAwait(false);
        var planSourcePath = planGatePath;
        if (planGate is null)
        {
            planGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterPlanReport>(planFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (planGate is not null)
            {
                planSourcePath = planFallbackPath;
            }
        }

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var options = new ShadowFormalRetrievalAdapterOptions
        {
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 5),
            GraphTopK = CommandHelpers.GetIntOption(args, "--graph-top-k", 5),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 8),
            MaxSampleTraceCount = CommandHelpers.GetIntOption(args, "--max-sample-trace", 5),
            RequirePlanGatePassed = !CommandHelpers.HasFlag(args, "--skip-plan-gate-check"),
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v51PlanGate"] = planSourcePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new ShadowFormalRetrievalAdapter();
        var gateMode = string.Equals(subcommand, "vector-shadow-formal-retrieval-adapter-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(planGate, dataset, options, sourceReports)
            : runner.BuildAdapter(planGate, dataset, options, sourceReports);
        var fileName = gateMode
            ? "shadow-formal-retrieval-adapter-gate"
            : "shadow-formal-retrieval-adapter";
        var title = gateMode
            ? "Vector Shadow Formal Retrieval Adapter Gate"
            : "Vector Shadow Formal Retrieval Adapter";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(ShadowFormalRetrievalAdapter.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Shadow formal retrieval adapter artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.AdapterPassed}; gatePassed={report.GatePassed}; samples={report.SampleCount}; risk={report.RiskAfterPolicy}; mustNot={report.MustNotHitRiskAfterPolicy}; lifecycle={report.LifecycleRiskAfterPolicy}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteFormalAdapterPackageShadowComparisonAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var adapterGatePath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-gate.json");
        var adapterFallbackPath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var adapterGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterReport>(adapterGatePath, cancellationToken)
            .ConfigureAwait(false);
        var adapterSourcePath = adapterGatePath;
        if (adapterGate is null)
        {
            adapterGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterReport>(adapterFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (adapterGate is not null)
            {
                adapterSourcePath = adapterFallbackPath;
            }
        }

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var options = new FormalAdapterPackageShadowComparisonOptions
        {
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            BaselineTopK = CommandHelpers.GetIntOption(args, "--baseline-top-k", 5),
            ShadowVectorTopK = CommandHelpers.GetIntOption(args, "--shadow-vector-top-k", 5),
            ShadowGraphTopK = CommandHelpers.GetIntOption(args, "--shadow-graph-top-k", 5),
            ShadowMergedTopK = CommandHelpers.GetIntOption(args, "--shadow-merged-top-k", 8),
            PackageSectionTopK = CommandHelpers.GetIntOption(args, "--package-section-top-k", 5),
            MaxSampleTraceCount = CommandHelpers.GetIntOption(args, "--max-sample-trace", 5),
            MaxTokenDeltaTotal = CommandHelpers.GetIntOption(args, "--max-token-delta-total", 4_000),
            MaxTokenDeltaPerSample = CommandHelpers.GetIntOption(args, "--max-token-delta-per-sample", 200),
            RequireAdapterGatePassed = !CommandHelpers.HasFlag(args, "--skip-adapter-gate-check"),
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v52AdapterGate"] = adapterSourcePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new FormalAdapterPackageShadowComparisonRunner();
        var gateMode = string.Equals(subcommand, "vector-formal-adapter-package-shadow-comparison-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(adapterGate, dataset, options, sourceReports)
            : runner.BuildComparison(adapterGate, dataset, options, sourceReports);
        var fileName = gateMode
            ? "formal-adapter-package-shadow-comparison-gate"
            : "formal-adapter-package-shadow-comparison";
        var title = gateMode
            ? "Vector Formal Adapter Package Shadow Comparison Gate"
            : "Vector Formal Adapter Package Shadow Comparison";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(FormalAdapterPackageShadowComparisonRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Formal adapter package shadow comparison artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.ComparisonPassed}; gatePassed={report.GatePassed}; samples={report.SampleCount}; tokenDelta={report.TokenDeltaTotal}; tokenDeltaMax={report.TokenDeltaMax}; risk={report.RiskAfterPolicy}; mustNot={report.MustNotHitRiskAfterPolicy}; lifecycle={report.LifecycleRiskAfterPolicy}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteGraphVectorRetrievalQualityAuditAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var packageGatePath = Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison-gate.json");
        var packageFallbackPath = Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison.json");
        var adapterGatePath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-gate.json");
        var adapterFallbackPath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var packageShadowGate = await ReadJsonFileAsync<FormalAdapterPackageShadowComparisonReport>(packageGatePath, cancellationToken)
            .ConfigureAwait(false);
        var packageSourcePath = packageGatePath;
        if (packageShadowGate is null)
        {
            packageShadowGate = await ReadJsonFileAsync<FormalAdapterPackageShadowComparisonReport>(packageFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (packageShadowGate is not null)
            {
                packageSourcePath = packageFallbackPath;
            }
        }

        var adapterGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterReport>(adapterGatePath, cancellationToken)
            .ConfigureAwait(false);
        var adapterSourcePath = adapterGatePath;
        if (adapterGate is null)
        {
            adapterGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterReport>(adapterFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (adapterGate is not null)
            {
                adapterSourcePath = adapterFallbackPath;
            }
        }

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var options = new GraphVectorRetrievalQualityAuditOptions
        {
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 5),
            GraphTopK = CommandHelpers.GetIntOption(args, "--graph-top-k", 5),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 8),
            MaxSampleTraceCount = CommandHelpers.GetIntOption(args, "--max-sample-trace", 5),
            MaxFailureClusterMembers = CommandHelpers.GetIntOption(args, "--max-failure-cluster-members", 5),
            GraphNoiseThreshold = CommandHelpers.GetIntOption(args, "--graph-noise-threshold", 0),
            RankingRegressionThreshold = CommandHelpers.GetIntOption(args, "--ranking-regression-threshold", 0),
            MustHitBelowTopKThreshold = CommandHelpers.GetIntOption(args, "--must-hit-below-topk-threshold", 0),
            RequirePackageShadowGatePassed = !CommandHelpers.HasFlag(args, "--skip-package-shadow-gate-check"),
            RequireAdapterGatePassed = !CommandHelpers.HasFlag(args, "--skip-adapter-gate-check"),
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v53PackageShadowGate"] = packageSourcePath,
            ["v52AdapterGate"] = adapterSourcePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new GraphVectorRetrievalQualityAuditRunner();
        var gateMode = string.Equals(subcommand, "vector-graph-retrieval-quality-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(packageShadowGate, adapterGate, dataset, options, sourceReports)
            : runner.BuildAudit(packageShadowGate, adapterGate, dataset, options, sourceReports);
        var fileName = gateMode
            ? "graph-vector-retrieval-quality-gate"
            : "graph-vector-retrieval-quality-audit";
        var title = gateMode
            ? "Vector Graph Retrieval Quality Audit Gate"
            : "Vector Graph Retrieval Quality Audit";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(GraphVectorRetrievalQualityAuditRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Graph + vector retrieval quality audit artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.AuditPassed}; gatePassed={report.GatePassed}; samples={report.SampleCount}; recall={report.Recall:F4}; precision={report.Precision:F4}; mrr={report.MeanReciprocalRank:F4}; graphNoise={report.GraphNoiseCount}; rankingRegression={report.RankingRegressionCount}; mustHitBelowTopK={report.MustHitBelowTopKCount}; risk={report.RiskAfterPolicy}; mustNot={report.MustNotHitRiskAfterPolicy}; lifecycle={report.LifecycleRiskAfterPolicy}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteRetrievalQualityRepairPreviewAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var qualityGatePath = Path.Combine("vector", "v5", "graph-vector-retrieval-quality-gate.json");
        var qualityFallbackPath = Path.Combine("vector", "v5", "graph-vector-retrieval-quality-audit.json");
        var packageGatePath = Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison-gate.json");
        var packageFallbackPath = Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var qualityGate = await ReadJsonFileAsync<GraphVectorRetrievalQualityAuditReport>(qualityGatePath, cancellationToken)
            .ConfigureAwait(false);
        var qualitySourcePath = qualityGatePath;
        if (qualityGate is null)
        {
            qualityGate = await ReadJsonFileAsync<GraphVectorRetrievalQualityAuditReport>(qualityFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (qualityGate is not null)
            {
                qualitySourcePath = qualityFallbackPath;
            }
        }

        var packageShadowGate = await ReadJsonFileAsync<FormalAdapterPackageShadowComparisonReport>(packageGatePath, cancellationToken)
            .ConfigureAwait(false);
        var packageSourcePath = packageGatePath;
        if (packageShadowGate is null)
        {
            packageShadowGate = await ReadJsonFileAsync<FormalAdapterPackageShadowComparisonReport>(packageFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (packageShadowGate is not null)
            {
                packageSourcePath = packageFallbackPath;
            }
        }

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var options = new RetrievalQualityRepairPreviewOptions
        {
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            BaselineVectorTopK = CommandHelpers.GetIntOption(args, "--baseline-vector-top-k", 5),
            BaselineGraphTopK = CommandHelpers.GetIntOption(args, "--baseline-graph-top-k", 5),
            BaselineMergedTopK = CommandHelpers.GetIntOption(args, "--baseline-merged-top-k", 8),
            ExpansionVectorTopK = CommandHelpers.GetIntOption(args, "--expansion-vector-top-k", 10),
            ExpansionGraphTopK = CommandHelpers.GetIntOption(args, "--expansion-graph-top-k", 10),
            ExpansionMergedTopK = CommandHelpers.GetIntOption(args, "--expansion-merged-top-k", 12),
            AdjustedTopK = CommandHelpers.GetIntOption(args, "--adjusted-top-k", 8),
            SectionBoost = CommandHelpers.GetDoubleOption(args, "--section-boost", 1.5),
            MustHitEvidenceBoost = CommandHelpers.GetDoubleOption(args, "--must-hit-evidence-boost", 1.75),
            GraphRelationAnchorBoost = CommandHelpers.GetDoubleOption(args, "--graph-relation-anchor-boost", 1.6),
            LexicalFallbackBoost = CommandHelpers.GetDoubleOption(args, "--lexical-fallback-boost", 1.4),
            MaxSampleTraceCount = CommandHelpers.GetIntOption(args, "--max-sample-trace", 5),
            MaxTokenDeltaTotal = CommandHelpers.GetIntOption(args, "--max-token-delta-total", 4_000),
            MaxTokenDeltaPerSample = CommandHelpers.GetIntOption(args, "--max-token-delta-per-sample", 200),
            RequireQualityGatePassed = !CommandHelpers.HasFlag(args, "--skip-quality-gate-check"),
            RequirePackageShadowGatePassed = !CommandHelpers.HasFlag(args, "--skip-package-shadow-gate-check"),
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v54QualityGate"] = qualitySourcePath,
            ["v53PackageShadowGate"] = packageSourcePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new RetrievalQualityRepairPreviewRunner();
        var gateMode = string.Equals(subcommand, "vector-retrieval-quality-repair-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(qualityGate, packageShadowGate, dataset, options, sourceReports)
            : runner.BuildPreview(qualityGate, packageShadowGate, dataset, options, sourceReports);
        var fileName = gateMode
            ? "retrieval-quality-repair-gate"
            : "retrieval-quality-repair-preview";
        var title = gateMode
            ? "Vector Retrieval Quality Repair Preview Gate"
            : "Vector Retrieval Quality Repair Preview";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(RetrievalQualityRepairPreviewRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        var bestProfile = report.Profiles.FirstOrDefault(p => p.ProfileId == report.BestProfileId);
        var bestRecall = bestProfile?.Recall ?? 0d;
        var bestMrr = bestProfile?.MeanReciprocalRank ?? 0d;
        var deltaRecall = bestProfile?.RecallDelta ?? 0d;
        var deltaMrr = bestProfile?.MrrDelta ?? 0d;
        Console.WriteLine($"[Eval] Retrieval quality repair preview artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.PreviewPassed}; gatePassed={report.GatePassed}; bestProfile={(string.IsNullOrEmpty(report.BestProfileId) ? "-" : report.BestProfileId)}; baselineRecall={report.Baseline.Recall:F4}; baselineMrr={report.Baseline.MeanReciprocalRank:F4}; bestRecall={bestRecall:F4}; bestMrr={bestMrr:F4}; deltaRecall={deltaRecall:F4}; deltaMrr={deltaMrr:F4}; profilesEvaluated={report.Profiles.Count}; risk={report.Baseline.RiskAfterPolicy}; mustNot={report.Baseline.MustNotHitRiskAfterPolicy}; lifecycle={report.Baseline.LifecycleRiskAfterPolicy}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteRuntimeObservableFeatureContractAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var repairGatePath = Path.Combine("vector", "v5", "retrieval-quality-repair-gate.json");
        var repairFallbackPath = Path.Combine("vector", "v5", "retrieval-quality-repair-preview.json");

        var repairGate = await ReadJsonFileAsync<RetrievalQualityRepairPreviewReport>(repairGatePath, cancellationToken)
            .ConfigureAwait(false);
        var repairSourcePath = repairGatePath;
        if (repairGate is null)
        {
            repairGate = await ReadJsonFileAsync<RetrievalQualityRepairPreviewReport>(repairFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (repairGate is not null)
            {
                repairSourcePath = repairFallbackPath;
            }
        }

        var skipScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var options = new RuntimeObservableFeatureContractOptions
        {
            RequireRepairGatePassed = !CommandHelpers.HasFlag(args, "--skip-repair-gate-check"),
            RequireSourceScan = !skipScan,
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v55RepairGate"] = repairSourcePath
        };

        var runner = new RuntimeObservableRetrievalFeatureContractRunner();
        var gateMode = string.Equals(subcommand, "vector-runtime-observable-feature-contract-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(repairGate, sourceScan, options, sourceReports)
            : runner.BuildContract(repairGate, sourceScan, options, sourceReports);
        var fileName = gateMode
            ? "runtime-observable-feature-contract-gate"
            : "runtime-observable-feature-contract";
        var title = gateMode
            ? "Vector Runtime-observable Retrieval Feature Contract Gate"
            : "Vector Runtime-observable Retrieval Feature Contract";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(RuntimeObservableRetrievalFeatureContractRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        var bestProfile = report.Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, report.BestProfileId, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[Eval] Runtime-observable retrieval feature contract artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.ContractPassed}; gatePassed={report.GatePassed}; bestProfile={(string.IsNullOrEmpty(report.BestProfileId) ? "-" : report.BestProfileId)}; bestStatus={report.BestProfileContractStatus}; forbiddenInScoring={(bestProfile?.UsesForbiddenForScoring ?? false)}; evalOnlyInScoring={(bestProfile?.UsesEvalOnlyForScoring ?? false)}; runtimeDerivation={(bestProfile?.RequiresRuntimeDerivation ?? false)}; sourceScanFiles={report.SourceScan.ScannedFileCount}; fixtureHits={report.SourceScan.FixtureTokenHitCount}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteRuntimeFeatureDerivationPreviewAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var contractGatePath = Path.Combine("vector", "v5", "runtime-observable-feature-contract-gate.json");
        var contractFallbackPath = Path.Combine("vector", "v5", "runtime-observable-feature-contract.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var v55GatePath = Path.Combine("vector", "v5", "retrieval-quality-repair-gate.json");
        var v55FallbackPath = Path.Combine("vector", "v5", "retrieval-quality-repair-preview.json");

        var contractGate = await ReadJsonFileAsync<RuntimeObservableFeatureContractReport>(contractGatePath, cancellationToken)
            .ConfigureAwait(false);
        var contractSourcePath = contractGatePath;
        if (contractGate is null)
        {
            contractGate = await ReadJsonFileAsync<RuntimeObservableFeatureContractReport>(contractFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (contractGate is not null)
            {
                contractSourcePath = contractFallbackPath;
            }
        }

        var v55Gate = await ReadJsonFileAsync<RetrievalQualityRepairPreviewReport>(v55GatePath, cancellationToken)
            .ConfigureAwait(false);
        var v55SourcePath = v55GatePath;
        if (v55Gate is null)
        {
            v55Gate = await ReadJsonFileAsync<RetrievalQualityRepairPreviewReport>(v55FallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (v55Gate is not null)
            {
                v55SourcePath = v55FallbackPath;
            }
        }

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var skipScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var bestProfileResult = v55Gate?.Profiles?.FirstOrDefault(
            p => string.Equals(p.ProfileId, v55Gate.BestProfileId, StringComparison.OrdinalIgnoreCase));

        var options = new RuntimeRetrievalFeatureDerivationOptions
        {
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            SeedTopK = CommandHelpers.GetIntOption(args, "--seed-top-k", 5),
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 10),
            GraphTopK = CommandHelpers.GetIntOption(args, "--graph-top-k", 10),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 12),
            SectionBoost = CommandHelpers.GetDoubleOption(args, "--section-boost", 1.5),
            EvidenceBoost = CommandHelpers.GetDoubleOption(args, "--evidence-boost", 1.75),
            RelationBoost = CommandHelpers.GetDoubleOption(args, "--relation-boost", 1.6),
            LexicalBoost = CommandHelpers.GetDoubleOption(args, "--lexical-boost", 1.4),
            MaxSampleTraceCount = CommandHelpers.GetIntOption(args, "--max-sample-trace", 5),
            MaxAllowedRecallRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-recall-regression", 0.0),
            MaxAllowedMrrRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-mrr-regression", 0.0),
            RequireContractGatePassed = !CommandHelpers.HasFlag(args, "--skip-contract-gate-check"),
            RequireSourceScan = !skipScan,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            EvalDrivenRecall = bestProfileResult?.Recall ?? 0d,
            EvalDrivenPrecision = bestProfileResult?.Precision ?? 0d,
            EvalDrivenMeanReciprocalRank = bestProfileResult?.MeanReciprocalRank ?? 0d
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v56ContractGate"] = contractSourcePath,
            ["v55RepairGate"] = v55SourcePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new RuntimeRetrievalFeatureDerivationPreviewRunner();
        var gateMode = string.Equals(subcommand, "vector-runtime-feature-derivation-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(contractGate, dataset, sourceScan, options, sourceReports)
            : runner.BuildPreview(contractGate, dataset, sourceScan, options, sourceReports);
        var fileName = gateMode
            ? "runtime-feature-derivation-gate"
            : "runtime-feature-derivation-preview";
        var title = gateMode
            ? "Vector Runtime Retrieval Feature Derivation Preview Gate"
            : "Vector Runtime Retrieval Feature Derivation Preview";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(RuntimeRetrievalFeatureDerivationPreviewRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Runtime feature derivation preview artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.PreviewPassed}; gatePassed={report.GatePassed}; samples={report.SampleCount}; baselineRecall={report.BaselineRecall:F4}; derivedRecall={report.DerivedRecall:F4}; deltaRecall={report.DerivedRecallDelta:F4}; baselineMrr={report.BaselineMeanReciprocalRank:F4}; derivedMrr={report.DerivedMeanReciprocalRank:F4}; deltaMrr={report.DerivedMrrDelta:F4}; targetMatch={report.TargetSectionMatchRate:F4}; relationCov={report.RequiredRelationCoverageRate:F4}; evidenceCov={report.EvidenceAnchorCoverageRate:F4}; sourceCov={report.SourceAnchorCoverageRate:F4}; risk={report.DerivedRiskAfterPolicy}; mustNot={report.DerivedMustNotHitRiskAfterPolicy}; lifecycle={report.DerivedLifecycleRiskAfterPolicy}; forbiddenReads={report.ForbiddenSampleAnnotationReadCount}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteRuntimeFeatureDerivationRepairAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var derivationGatePath = Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json");
        var derivationFallbackPath = Path.Combine("vector", "v5", "runtime-feature-derivation-preview.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(derivationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var derivationSourcePath = derivationGatePath;
        if (derivationGate is null)
        {
            derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(derivationFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (derivationGate is not null)
            {
                derivationSourcePath = derivationFallbackPath;
            }
        }

        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var skipScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var options = new RuntimeRetrievalFeatureDerivationRepairOptions
        {
            ProfileName = CommandHelpers.GetOption(args, "--profile")
                ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            DenseSeedTopK = CommandHelpers.GetIntOption(args, "--dense-seed-top-k", 8),
            AnchorSeedTopK = CommandHelpers.GetIntOption(args, "--anchor-seed-top-k", 12),
            RelationTopK = CommandHelpers.GetIntOption(args, "--relation-top-k", 20),
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 10),
            GraphTopK = CommandHelpers.GetIntOption(args, "--graph-top-k", 10),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 12),
            SectionBoost = CommandHelpers.GetDoubleOption(args, "--section-boost", 1.5),
            EvidenceBoost = CommandHelpers.GetDoubleOption(args, "--evidence-boost", 1.75),
            RelationBoost = CommandHelpers.GetDoubleOption(args, "--relation-boost", 1.6),
            LexicalBoost = CommandHelpers.GetDoubleOption(args, "--lexical-boost", 1.4),
            HoldoutModulus = CommandHelpers.GetIntOption(args, "--holdout-modulus", 5),
            HoldoutRemainder = CommandHelpers.GetIntOption(args, "--holdout-remainder", 0),
            MaxSampleTraceCount = CommandHelpers.GetIntOption(args, "--max-sample-trace", 5),
            MinRelationCoverageRate = CommandHelpers.GetDoubleOption(args, "--min-relation-coverage", 0.55),
            MaxAllowedHoldoutRecallRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-holdout-recall-regression", 0.0),
            MaxAllowedHoldoutMrrRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-holdout-mrr-regression", 0.0),
            RequireDerivationGatePassed = !CommandHelpers.HasFlag(args, "--skip-derivation-gate-check"),
            RequireSourceScan = !skipScan,
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v57DerivationGate"] = derivationSourcePath,
            ["stressCorpus"] = corpusPath,
            ["stressSamples"] = samplesPath
        };

        var runner = new RuntimeRetrievalFeatureDerivationRepairRunner();
        var gateMode = string.Equals(subcommand, "vector-runtime-feature-derivation-repair-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(derivationGate, dataset, sourceScan, options, sourceReports)
            : runner.BuildPreview(derivationGate, dataset, sourceScan, options, sourceReports);
        var fileName = gateMode
            ? "runtime-feature-derivation-repair-gate"
            : "runtime-feature-derivation-repair";
        var title = gateMode
            ? "Vector Runtime Retrieval Feature Derivation Repair Gate"
            : "Vector Runtime Retrieval Feature Derivation Repair";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(RuntimeRetrievalFeatureDerivationRepairRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Runtime feature derivation repair artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.PreviewPassed}; gatePassed={report.GatePassed}; trainSamples={report.TrainSampleCount}; holdoutSamples={report.HoldoutSampleCount}; trainBaseline R/M={report.TrainBaselineRecall:F4}/{report.TrainBaselineMrr:F4}; trainDerived R/M={report.TrainDerivedRecall:F4}/{report.TrainDerivedMrr:F4}; holdoutBaseline R/M={report.HoldoutBaselineRecall:F4}/{report.HoldoutBaselineMrr:F4}; holdoutDerived R/M={report.HoldoutDerivedRecall:F4}/{report.HoldoutDerivedMrr:F4}; canonicalRel/Ev/Src={report.CanonicalRequiredRelationCoverageRate:F4}/{report.CanonicalEvidenceAnchorCoverageRate:F4}/{report.CanonicalSourceAnchorCoverageRate:F4}; forbiddenReads={report.ForbiddenSampleAnnotationReadCount}; risk={report.DerivedRiskAfterPolicy}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteRuntimeFeatureDerivationFailureFreezeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var repairGatePath = Path.Combine("vector", "v5", "runtime-feature-derivation-repair-gate.json");
        var repairFallbackPath = Path.Combine("vector", "v5", "runtime-feature-derivation-repair.json");
        var derivationGatePath = Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json");
        var derivationFallbackPath = Path.Combine("vector", "v5", "runtime-feature-derivation-preview.json");

        var repairGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationRepairReport>(repairGatePath, cancellationToken)
            .ConfigureAwait(false);
        var repairSourcePath = repairGatePath;
        if (repairGate is null)
        {
            repairGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationRepairReport>(repairFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (repairGate is not null)
            {
                repairSourcePath = repairFallbackPath;
            }
        }

        var derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(derivationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var derivationSourcePath = derivationGatePath;
        if (derivationGate is null)
        {
            derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(derivationFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (derivationGate is not null)
            {
                derivationSourcePath = derivationFallbackPath;
            }
        }

        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["repairGate"] = repairSourcePath,
            ["derivationGate"] = derivationSourcePath
        };

        var report = new RuntimeFeatureDerivationFailureFreezeRunner().BuildFreeze(
            repairGate, derivationGate,
            new RuntimeFeatureDerivationFailureFreezeOptions { RequireRepairGateFrozen = true },
            sourceReports);

        var jsonPath = Path.Combine(outputDirectory, "runtime-feature-derivation-failure-freeze.json");
        var markdownPath = Path.Combine(outputDirectory, "runtime-feature-derivation-failure-freeze.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(RuntimeFeatureDerivationFailureFreezeRunner.BuildMarkdown("Runtime Feature Derivation Failure Freeze", report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Runtime feature derivation failure freeze artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; frozenStatus={report.FrozenStatus}; canonicalResolverReusable={report.CanonicalAnchorResolverReusable}; relationDeriverReady={report.RuntimeRelationIntentDeriverReady}; recommendation={report.Recommendation}");
    }


    private static async Task ExecuteGraphHubNoiseControlAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var freezePath = Path.Combine("vector", "v5", "runtime-feature-derivation-failure-freeze.json");
        var repairGatePath = Path.Combine("vector", "v5", "runtime-feature-derivation-repair-gate.json");
        var repairFallbackPath = Path.Combine("vector", "v5", "runtime-feature-derivation-repair.json");
        var corpusPath = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");

        var freezeGate = await ReadJsonFileAsync<RuntimeFeatureDerivationFailureFreezeReport>(freezePath, cancellationToken).ConfigureAwait(false);
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken).ConfigureAwait(false);
        var repairGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationRepairReport>(repairGatePath, cancellationToken).ConfigureAwait(false);
        if (repairGate is null)
        {
            repairGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationRepairReport>(repairFallbackPath, cancellationToken).ConfigureAwait(false);
        }

        var options = new GraphHubNoiseControlOptions
        {
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            DenseSeedTopK = CommandHelpers.GetIntOption(args, "--dense-seed-top-k", 5),
            AnchorSeedTopK = CommandHelpers.GetIntOption(args, "--anchor-seed-top-k", 5),
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 10),
            GraphTopK = CommandHelpers.GetIntOption(args, "--graph-top-k", 10),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 12),
            RelationsPerSeedCap = CommandHelpers.GetIntOption(args, "--relations-per-seed-cap", 3),
            HubDegreeThreshold = CommandHelpers.GetIntOption(args, "--hub-degree-threshold", 5),
            SectionBoost = CommandHelpers.GetDoubleOption(args, "--section-boost", 1.15),
            EvidenceBoost = CommandHelpers.GetDoubleOption(args, "--evidence-boost", 1.25),
            RelationBoost = CommandHelpers.GetDoubleOption(args, "--relation-boost", 1.25),
            LexicalBoost = CommandHelpers.GetDoubleOption(args, "--lexical-boost", 1.10),
        };

        var runner = new GraphHubNoiseControlRunner();
        var gateMode = string.Equals(subcommand, "vector-graph-hub-noise-control-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(freezeGate, dataset, repairGate, options)
            : runner.BuildPreview(freezeGate, dataset, repairGate, options);
        var fileName = gateMode ? "graph-hub-noise-control-gate" : "graph-hub-noise-control-preview";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(GraphHubNoiseControlRunner.BuildMarkdown(
            gateMode ? "Graph Hub Noise Control Gate" : "Graph Hub Noise Control Preview", report),
            markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Graph hub noise control artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.PreviewPassed}; gatePassed={report.GatePassed}; hubItems={report.HubItemCount}; avgDominance={report.AvgHubDominanceRatio:F4}; baselineRecall={report.Baseline.Recall:F4}; hubCtrlRecall={report.HubControlled.Recall:F4}; recalcDelta={report.HubControlledRecallDelta:+0.0000;-0.0000;0.0000}; recommendation={report.Recommendation}");
    }


    private static async Task ExecuteQueryDrivenCandidateSourceRepairAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(output);
        var cp = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var sp = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var d = await LoadRetrievalDatasetV2GeneratedDatasetAsync(cp, sp, ct).ConfigureAwait(false);
        var derivationGatePath = Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json");
        var derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(derivationGatePath, ct).ConfigureAwait(false);
        var skipScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var opt = new QueryDrivenCandidateSourceRepairOptions
        {
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            DenseSeedTopK = CommandHelpers.GetIntOption(args, "--dense-seed-top-k", 5),
            AnchorSeedTopK = CommandHelpers.GetIntOption(args, "--anchor-seed-top-k", 5),
            RelationTopK = CommandHelpers.GetIntOption(args, "--relation-top-k", 8),
            SectionBoost = CommandHelpers.GetDoubleOption(args, "--section-boost", 1.15),
            EvidenceBoost = CommandHelpers.GetDoubleOption(args, "--evidence-boost", 1.25),
            RelationBoost = CommandHelpers.GetDoubleOption(args, "--relation-boost", 1.25),
            LexicalBoost = CommandHelpers.GetDoubleOption(args, "--lexical-boost", 1.10),
            HoldoutModulus = CommandHelpers.GetIntOption(args, "--holdout-modulus", 5),
            HoldoutRemainder = CommandHelpers.GetIntOption(args, "--holdout-remainder", 0),
            MaxAllowedHoldoutRecallRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-holdout-recall-regression", 0.0),
            MaxAllowedHoldoutMrrRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-holdout-mrr-regression", 0.0),
            RequireSourceScan = !skipScan,
            UseForRuntime = false, FormalRetrievalAllowed = false
        };
        var srcReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["derivationGate"] = derivationGatePath, ["corpus"] = cp, ["samples"] = sp
        };

        var runner = new QueryDrivenCandidateSourceRepairRunner();
        var gateMode = string.Equals(subcommand, "vector-query-driven-candidate-source-repair-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(derivationGate, d, sourceScan, opt, srcReports)
            : runner.BuildPreview(derivationGate, d, sourceScan, opt, srcReports);
        var fn = gateMode ? "query-driven-candidate-source-repair-gate" : "query-driven-candidate-source-repair";
        var jp = Path.Combine(output, $"{fn}.json"); var mp = Path.Combine(output, $"{fn}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jp, ct).ConfigureAwait(false);
        await WriteTextAsync(QueryDrivenCandidateSourceRepairRunner.BuildMarkdown(
            gateMode ? "Query-driven Candidate Source Repair Gate" : "Query-driven Candidate Source Repair Preview",
            report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Query-driven candidate source repair written: {jp}");
        Console.WriteLine($"[Eval] passed={report.ReportPassed}; gatePassed={report.GatePassed}; best={report.BestProfileId}; trainBaselineR={report.TrainBaselineRecall:F4}; trainDerivedR={report.TrainDerivedRecall:F4}; holdoutBaselineR={report.HoldoutBaselineRecall:F4}; holdoutDerivedR={report.HoldoutDerivedRecall:F4}; risk={report.RiskAfterPolicy}; recommendation={report.Recommendation}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteFormalRetrievalIntegrationDecisionAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var projectStatePath = Path.Combine("eval", "project-state-audit.json");
        var integrationPlanPath = Path.Combine("vector", "v5", "formal-retrieval-integration-plan-gate.json");
        var adapterPlanPath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan-gate.json");
        var protocolGatePath = Path.Combine("vector", "v5", "retrieval-eval-protocol-gate.json");
        var enrichmentGatePath = Path.Combine("vector", "v5", "input-metadata-enrichment-gate.json");
        var sourceRepairGatePath = Path.Combine("vector", "v5", "enriched-candidate-source-repair-recheck-gate.json");
        var rankingGatePath = Path.Combine("vector", "v5", "source-aware-ranking-repair-gate.json");
        var outputPolicyGatePath = Path.Combine("vector", "v5", "output-token-priority-shadow-gate.json");
        var inputContractGatePath = Path.Combine("vector", "v5", "formal-adapter-input-contract-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var p15A3Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15ExtendedPath = Path.Combine("eval", "eval-report-p15-extended.json");

        var projectState = await ReadJsonFileAsync<ProjectStateAuditReport>(projectStatePath, cancellationToken)
            .ConfigureAwait(false);
        var integrationPlan = await ReadJsonFileAsync<FormalRetrievalIntegrationPlanReport>(integrationPlanPath, cancellationToken)
            .ConfigureAwait(false);
        var adapterPlan = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterPlanReport>(adapterPlanPath, cancellationToken)
            .ConfigureAwait(false);
        var protocolGate = await ReadJsonFileAsync<RetrievalEvalProtocolGateReport>(protocolGatePath, cancellationToken)
            .ConfigureAwait(false);
        var enrichmentGate = await ReadJsonFileAsync<InputMetadataEnrichmentPreviewReport>(enrichmentGatePath, cancellationToken)
            .ConfigureAwait(false);
        var sourceRepairGate = await ReadJsonFileAsync<EnrichedCandidateSourceRepairRecheckReport>(sourceRepairGatePath, cancellationToken)
            .ConfigureAwait(false);
        var rankingGate = await ReadJsonFileAsync<SourceAwareRankingRepairReport>(rankingGatePath, cancellationToken)
            .ConfigureAwait(false);
        var outputPolicyGate = await ReadJsonFileAsync<OutputTokenPriorityShadowGateReport>(outputPolicyGatePath, cancellationToken)
            .ConfigureAwait(false);
        var inputContractGate = await ReadJsonFileAsync<FormalAdapterInputContractReport>(inputContractGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(p15A3Path)
            && IsP15EvalReportPassed(p15ExtendedPath);
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["V50ProjectStateAudit"] = projectStatePath,
            ["V50FormalRetrievalIntegrationPlan"] = integrationPlanPath,
            ["V51ShadowFormalRetrievalAdapterPlan"] = adapterPlanPath,
            ["V511RetrievalEvalProtocol"] = protocolGatePath,
            ["V512InputMetadataEnrichment"] = enrichmentGatePath,
            ["V513EnrichedCandidateSourceRepair"] = sourceRepairGatePath,
            ["V514SourceAwareRankingRepair"] = rankingGatePath,
            ["V515OutputTokenPriorityShadow"] = outputPolicyGatePath,
            ["V516FormalAdapterInputContract"] = inputContractGatePath,
            ["RuntimeChangeGate"] = runtimeGatePath,
            ["P15Gate"] = $"{p15A3Path};{p15ExtendedPath}"
        };

        var gateMode = string.Equals(
            subcommand,
            "vector-formal-retrieval-integration-decision-gate",
            StringComparison.OrdinalIgnoreCase);
        var runner = new FormalRetrievalIntegrationDecisionRunner();
        var report = gateMode
            ? runner.BuildGate(
                projectState,
                integrationPlan,
                adapterPlan,
                protocolGate,
                enrichmentGate,
                sourceRepairGate,
                rankingGate,
                outputPolicyGate,
                inputContractGate,
                runtimeGate,
                p15Passed,
                sourceReports)
            : runner.BuildDecision(
                projectState,
                integrationPlan,
                adapterPlan,
                protocolGate,
                enrichmentGate,
                sourceRepairGate,
                rankingGate,
                outputPolicyGate,
                inputContractGate,
                runtimeGate,
                p15Passed,
                sourceReports);
        var fileName = gateMode
            ? "formal-retrieval-integration-decision-gate"
            : "formal-retrieval-integration-decision";
        var title = gateMode
            ? "Formal Retrieval Integration Decision Gate"
            : "Formal Retrieval Integration Decision";
        var jsonPath = Path.Combine(outputDirectory, fileName + ".json");
        var markdownPath = Path.Combine(outputDirectory, fileName + ".md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalIntegrationDecisionRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Formal retrieval integration decision artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] decisionPassed={report.DecisionPassed}; gatePassed={report.GatePassed}; decision={report.IntegrationDecision}; recommendation={report.Recommendation}; next={report.NextAllowedPhase}; formalRetrieval={report.FormalRetrievalAllowed}; runtimeSwitch={report.RuntimeSwitchAllowed}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteFormalRetrievalIntegrationFreezeAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(output);

        var planGate = await ReadJsonFileAsync<FormalRetrievalIntegrationPlanReport>(
            Path.Combine("vector", "v5", "formal-retrieval-integration-plan-gate.json"), ct).ConfigureAwait(false);
        var contractGate = await ReadJsonFileAsync<RuntimeObservableFeatureContractReport>(
            Path.Combine("vector", "v5", "runtime-observable-feature-contract-gate.json"), ct).ConfigureAwait(false);
        var derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(
            Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json"), ct).ConfigureAwait(false);

        var runner = new FormalRetrievalIntegrationFreezeRunner();
        var freeze = runner.BuildFreeze(planGate, contractGate, derivationGate);

        // 空操作绑定计划（仅当 freeze passed 时有效，文档性产物）
        var isAdapterPlan = string.Equals(subcommand, "vector-adapter-noop-binding-plan", StringComparison.OrdinalIgnoreCase);
        var isFreezeGate = string.Equals(subcommand, "vector-formal-retrieval-integration-freeze-gate", StringComparison.OrdinalIgnoreCase);

        if (isAdapterPlan)
        {
            var plan = runner.BuildNoOpPlan(freeze);
            var pjp = Path.Combine(output, "adapter-noop-binding-plan.json");
            var pmp = Path.Combine(output, "adapter-noop-binding-plan.md");
            await WriteTextAsync(JsonSerializer.Serialize(plan, JsonOptions), pjp, ct).ConfigureAwait(false);
            await WriteTextAsync(FormalRetrievalIntegrationFreezeRunner.BuildPlanMarkdown("Adapter No-op Binding Plan", plan), pmp, ct).ConfigureAwait(false);
            Console.WriteLine($"[Eval] Adapter no-op binding plan artifact written: {pjp}");
            Console.WriteLine($"[Eval] planPassed={plan.PlanPassed}; recommendation={plan.Recommendation}; version={plan.PlanVersion}; phases={plan.ImplementationPhases.Count}");
            return;
        }

        var fn = isFreezeGate ? "formal-retrieval-integration-freeze-gate" : "formal-retrieval-integration-freeze";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteTextAsync(JsonSerializer.Serialize(freeze, JsonOptions), jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalIntegrationFreezeRunner.BuildFreezeMarkdown(
            isFreezeGate ? "Formal Retrieval Integration Freeze Gate" : "Formal Retrieval Integration Freeze", freeze), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Formal retrieval integration freeze artifact written: {jp}");
        Console.WriteLine($"[Eval] freezePassed={freeze.FreezePassed}; recommendation={freeze.Recommendation}; frozenArtifacts={freeze.FrozenArtifactPaths.Count}; blocked={freeze.BlockedReasons.Count}");
    }

private static async Task ExecuteRetrievalEvalProtocolAuditAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = CommandHelpers.GetOption(args, "--corpus")
            ?? Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = CommandHelpers.GetOption(args, "--samples")
            ?? Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var skipSourceScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipSourceScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var protocol = new RetrievalEvalProtocol
        {
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 5),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 8),
            FinalTopK = CommandHelpers.GetIntOption(args, "--final-top-k", 5),
            ScoreThreshold = CommandHelpers.GetDoubleOption(args, "--score-threshold", 0.0),
            TrainSplit = CommandHelpers.GetOption(args, "--train-split") ?? "train",
            HoldoutSplit = CommandHelpers.GetOption(args, "--holdout-split") ?? "holdout"
        };
        var options = new RetrievalEvalProtocolAuditOptions
        {
            Protocol = protocol,
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireSourceScan = !skipSourceScan,
            TemplateHomogeneityThreshold = CommandHelpers.GetDoubleOption(args, "--template-homogeneity-threshold", 0.35),
            MinNonDiscriminativeSourcesForDatasetIssue = CommandHelpers.GetIntOption(args, "--min-non-discriminative-sources", 3),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["corpus"] = corpusPath,
            ["samples"] = samplesPath,
            ["runtimeChangeGate"] = runtimeGatePath,
            ["v57RetrievalQualityRepairGate"] = Path.Combine("vector", "v5", "retrieval-quality-repair-gate.json"),
            ["v510CandidateSourceRepairGate"] = Path.Combine("vector", "v5", "query-driven-candidate-source-repair-gate.json")
        };

        var bundle = new RetrievalEvalProtocolAuditRunner().Build(
            dataset,
            runtimeGate,
            sourceScan,
            options,
            sourceReports);

        if (string.Equals(subcommand, "vector-candidate-source-discriminability-audit", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "candidate-source-discriminability-audit.json");
            var markdownPath = Path.Combine(outputDirectory, "candidate-source-discriminability-audit.md");
            await WriteTextAsync(JsonSerializer.Serialize(bundle.SourceDiscriminabilityAudit, JsonOptions), jsonPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(
                    RetrievalEvalProtocolAuditRunner.BuildSourceMarkdown(
                        "Candidate Source Discriminability Audit",
                        bundle.SourceDiscriminabilityAudit),
                    markdownPath,
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Candidate source discriminability audit written: {jsonPath}");
            Console.WriteLine($"[Eval] passed={bundle.SourceDiscriminabilityAudit.AuditPassed}; recommendation={bundle.SourceDiscriminabilityAudit.Recommendation}; nonDiscriminative={bundle.SourceDiscriminabilityAudit.NonDiscriminativeSourceCount}; templateHomogeneity={bundle.SourceDiscriminabilityAudit.TemplateHomogeneityScore:F4}; risk={bundle.SourceDiscriminabilityAudit.RiskAfterPolicy}");
            return;
        }

        if (string.Equals(subcommand, "vector-retrieval-eval-protocol-gate", StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = Path.Combine(outputDirectory, "retrieval-eval-protocol-gate.json");
            var markdownPath = Path.Combine(outputDirectory, "retrieval-eval-protocol-gate.md");
            await WriteTextAsync(JsonSerializer.Serialize(bundle.Gate, JsonOptions), jsonPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(
                    RetrievalEvalProtocolAuditRunner.BuildGateMarkdown("Retrieval Eval Protocol Gate", bundle.Gate),
                    markdownPath,
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Retrieval eval protocol gate written: {jsonPath}");
            Console.WriteLine($"[Eval] gatePassed={bundle.Gate.GatePassed}; recommendation={bundle.Gate.Recommendation}; hashOrderSensitivity={bundle.Gate.HashOrderSensitivityCount}; runtimeGate={bundle.Gate.RuntimeChangeGatePassed}; risk={bundle.Gate.RiskAfterPolicy}; blocked={bundle.Gate.BlockedReasons.Count}");
            return;
        }

        var protocolJsonPath = Path.Combine(outputDirectory, "retrieval-eval-protocol-audit.json");
        var protocolMarkdownPath = Path.Combine(outputDirectory, "retrieval-eval-protocol-audit.md");
        await WriteTextAsync(JsonSerializer.Serialize(bundle.ProtocolAudit, JsonOptions), protocolJsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                RetrievalEvalProtocolAuditRunner.BuildProtocolMarkdown("Retrieval Eval Protocol Audit", bundle.ProtocolAudit),
                protocolMarkdownPath,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Retrieval eval protocol audit written: {protocolJsonPath}");
        Console.WriteLine($"[Eval] passed={bundle.ProtocolAudit.ProtocolPassed}; recommendation={bundle.ProtocolAudit.Recommendation}; v57R={bundle.ProtocolAudit.V57BaselineRecall:F4}; v510R={bundle.ProtocolAudit.V510BaselineRecall:F4}; mergedR={bundle.ProtocolAudit.MergedRecall:F4}; hashOrderSensitivity={bundle.ProtocolAudit.HashOrderSensitivityCount}; blocked={bundle.ProtocolAudit.BlockedReasons.Count}");
    }

    private static async Task ExecuteInputMetadataEnrichmentPreviewAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = CommandHelpers.GetOption(args, "--corpus")
            ?? Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = CommandHelpers.GetOption(args, "--samples")
            ?? Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var protocolGatePath = Path.Combine("vector", "v5", "retrieval-eval-protocol-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var protocolGate = await ReadJsonFileAsync<RetrievalEvalProtocolGateReport>(protocolGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var skipSourceScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipSourceScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var protocol = protocolGate?.Protocol ?? new RetrievalEvalProtocol
        {
            VectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 5),
            MergedTopK = CommandHelpers.GetIntOption(args, "--merged-top-k", 8),
            FinalTopK = CommandHelpers.GetIntOption(args, "--final-top-k", 5),
            ScoreThreshold = CommandHelpers.GetDoubleOption(args, "--score-threshold", 0.0),
            TrainSplit = CommandHelpers.GetOption(args, "--train-split") ?? "train",
            HoldoutSplit = CommandHelpers.GetOption(args, "--holdout-split") ?? "holdout"
        };
        var options = new InputMetadataEnrichmentPreviewOptions
        {
            Protocol = protocol,
            RequireV511ProtocolGatePassed = !CommandHelpers.HasFlag(args, "--skip-v511-protocol-gate"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireSourceScan = !skipSourceScan,
            TemplateHomogeneityThreshold = CommandHelpers.GetDoubleOption(args, "--template-homogeneity-threshold", 0.35),
            MinNonDiscriminativeSourcesForDatasetIssue = CommandHelpers.GetIntOption(args, "--min-non-discriminative-sources", 3),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["corpus"] = corpusPath,
            ["samples"] = samplesPath,
            ["v511ProtocolGate"] = protocolGatePath,
            ["runtimeChangeGate"] = runtimeGatePath,
            ["v511SourceDiscriminability"] = Path.Combine("vector", "v5", "candidate-source-discriminability-audit.json")
        };

        var runner = new InputMetadataEnrichmentPreviewRunner();
        var gateMode = string.Equals(subcommand, "vector-input-metadata-enrichment-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(dataset, protocolGate, runtimeGate, sourceScan, options, sourceReports)
            : runner.BuildPreview(dataset, protocolGate, runtimeGate, sourceScan, options, sourceReports);
        var fileName = gateMode ? "input-metadata-enrichment-gate" : "input-metadata-enrichment-preview";
        var title = gateMode ? "Input Metadata Enrichment Gate" : "Input Metadata Enrichment Preview";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(InputMetadataEnrichmentPreviewRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Input metadata enrichment artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] previewPassed={report.PreviewPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; coverageDelta={report.MetadataCoverageDelta}; recall={report.BeforeRecall:F4}->{report.AfterRecall:F4}; independentNonDense={report.IndependentNonDenseSourceCount}; risk={report.RiskAfterPolicy}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteEnrichedCandidateSourceRepairRecheckAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = CommandHelpers.GetOption(args, "--corpus")
            ?? Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = CommandHelpers.GetOption(args, "--samples")
            ?? Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var derivationGatePath = Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json");
        var enrichmentGatePath = Path.Combine("vector", "v5", "input-metadata-enrichment-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var derivationGate = await ReadJsonFileAsync<RuntimeRetrievalFeatureDerivationReport>(derivationGatePath, cancellationToken)
            .ConfigureAwait(false);
        var enrichmentGate = await ReadJsonFileAsync<InputMetadataEnrichmentPreviewReport>(enrichmentGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);
        var skipSourceScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipSourceScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();

        var sourceRepairOptions = new QueryDrivenCandidateSourceRepairOptions
        {
            TopK = CommandHelpers.GetIntOption(args, "--top-k", 5),
            DenseSeedTopK = CommandHelpers.GetIntOption(args, "--dense-seed-top-k", 5),
            AnchorSeedTopK = CommandHelpers.GetIntOption(args, "--anchor-seed-top-k", 5),
            RelationTopK = CommandHelpers.GetIntOption(args, "--relation-top-k", 8),
            SectionBoost = CommandHelpers.GetDoubleOption(args, "--section-boost", 1.15),
            EvidenceBoost = CommandHelpers.GetDoubleOption(args, "--evidence-boost", 1.25),
            RelationBoost = CommandHelpers.GetDoubleOption(args, "--relation-boost", 1.25),
            LexicalBoost = CommandHelpers.GetDoubleOption(args, "--lexical-boost", 1.10),
            HoldoutModulus = CommandHelpers.GetIntOption(args, "--holdout-modulus", 5),
            HoldoutRemainder = CommandHelpers.GetIntOption(args, "--holdout-remainder", 0),
            MaxAllowedHoldoutRecallRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-holdout-recall-regression", 0.0),
            MaxAllowedHoldoutMrrRegression = CommandHelpers.GetDoubleOption(args, "--max-allowed-holdout-mrr-regression", 0.0),
            RequireSourceScan = !skipSourceScan,
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };
        var options = new EnrichedCandidateSourceRepairRecheckOptions
        {
            RequireV512EnrichmentGatePassed = !CommandHelpers.HasFlag(args, "--skip-v512-enrichment-gate"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireSourceScan = !skipSourceScan,
            MetricTolerance = CommandHelpers.GetDoubleOption(args, "--metric-tolerance", 1e-9),
            SourceRepairOptions = sourceRepairOptions
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["corpus"] = corpusPath,
            ["samples"] = samplesPath,
            ["derivationGate"] = derivationGatePath,
            ["v512InputMetadataEnrichmentGate"] = enrichmentGatePath,
            ["runtimeChangeGate"] = runtimeGatePath,
            ["v511ProtocolGate"] = Path.Combine("vector", "v5", "retrieval-eval-protocol-gate.json"),
            ["v510CandidateSourceRepairGate"] = Path.Combine("vector", "v5", "query-driven-candidate-source-repair-gate.json")
        };

        var runner = new EnrichedCandidateSourceRepairRecheckRunner();
        var gateMode = string.Equals(subcommand, "vector-enriched-candidate-source-repair-recheck-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(derivationGate, enrichmentGate, dataset, runtimeGate, sourceScan, options, sourceReports)
            : runner.BuildPreview(derivationGate, enrichmentGate, dataset, runtimeGate, sourceScan, options, sourceReports);
        var fileName = gateMode
            ? "enriched-candidate-source-repair-recheck-gate"
            : "enriched-candidate-source-repair-recheck";
        var title = gateMode
            ? "Enriched Candidate Source Repair Recheck Gate"
            : "Enriched Candidate Source Repair Recheck";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(EnrichedCandidateSourceRepairRecheckRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Enriched candidate source repair recheck artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] recheckPassed={report.RecheckPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; qualityImproved={report.QualityImproved}; trainRecall={report.OriginalTrainDerivedRecall:F4}->{report.EnrichedTrainDerivedRecall:F4}; holdoutRecall={report.OriginalHoldoutDerivedRecall:F4}->{report.EnrichedHoldoutDerivedRecall:F4}; belowTopK={report.OriginalMustHitBelowTopK}->{report.EnrichedMustHitBelowTopK}; risk={report.RiskAfterPolicy}; blocked={report.BlockedReasons.Count}; qualityBlocked={report.QualityBlockedReasons.Count}");
    }

    private static async Task ExecuteSourceAwareRankingRepairAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = CommandHelpers.GetOption(args, "--corpus")
            ?? Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = CommandHelpers.GetOption(args, "--samples")
            ?? Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var protocolGatePath = Path.Combine("vector", "v5", "retrieval-eval-protocol-gate.json");
        var enrichmentGatePath = Path.Combine("vector", "v5", "input-metadata-enrichment-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var protocolGate = await ReadJsonFileAsync<RetrievalEvalProtocolGateReport>(protocolGatePath, cancellationToken)
            .ConfigureAwait(false);
        var enrichmentGate = await ReadJsonFileAsync<InputMetadataEnrichmentPreviewReport>(enrichmentGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);

        var skipSourceScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipSourceScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();
        var options = new SourceAwareRankingRepairOptions
        {
            Protocol = protocolGate?.Protocol,
            RequireV511ProtocolGatePassed = !CommandHelpers.HasFlag(args, "--skip-v511-protocol-gate"),
            RequireV512EnrichmentGatePassed = !CommandHelpers.HasFlag(args, "--skip-v512-enrichment-gate"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireSourceScan = !skipSourceScan,
            BlindHoldoutSampleCount = CommandHelpers.GetIntOption(args, "--blind-holdout-count", 24),
            ContributionCap = CommandHelpers.GetDoubleOption(args, "--contribution-cap", 0.34),
            MinConfidence = CommandHelpers.GetDoubleOption(args, "--min-confidence", 0.46),
            MetricTolerance = CommandHelpers.GetDoubleOption(args, "--metric-tolerance", 1e-9),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["corpus"] = corpusPath,
            ["samples"] = samplesPath,
            ["v511ProtocolGate"] = protocolGatePath,
            ["v512InputMetadataEnrichmentGate"] = enrichmentGatePath,
            ["runtimeChangeGate"] = runtimeGatePath,
            ["v513EnrichedCandidateSourceRepairGate"] = Path.Combine("vector", "v5", "enriched-candidate-source-repair-recheck-gate.json")
        };

        var runner = new SourceAwareRankingRepairRunner();
        var gateMode = string.Equals(subcommand, "vector-source-aware-ranking-repair-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(dataset, protocolGate, enrichmentGate, runtimeGate, sourceScan, options, sourceReports)
            : runner.BuildPreview(dataset, protocolGate, enrichmentGate, runtimeGate, sourceScan, options, sourceReports);
        var fileName = gateMode ? "source-aware-ranking-repair-gate" : "source-aware-ranking-repair";
        var title = gateMode ? "Source-aware Ranking Repair Gate" : "Source-aware Ranking Repair";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        var blindCorpusPath = Path.Combine(outputDirectory, "source-aware-ranking-blind-holdout-corpus.jsonl");
        var blindSamplesPath = Path.Combine(outputDirectory, "source-aware-ranking-blind-holdout-samples.jsonl");
        var blindManifestPath = Path.Combine(outputDirectory, "source-aware-ranking-blind-holdout-manifest.json");

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(SourceAwareRankingRepairRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ToJsonLines(report.BlindHoldoutCorpusItems), blindCorpusPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ToJsonLines(report.BlindHoldoutSamples), blindSamplesPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(JsonSerializer.Serialize(report.BlindHoldoutManifest, JsonOptions), blindManifestPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Source-aware ranking repair artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] reportPassed={report.ReportPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; profile={report.SelectedProfileId}; trainDevRecallDelta={report.TrainDevRecallDelta:+0.0000;-0.0000;0.0000}; test/holdout/blind={report.TestRecallDelta:+0.0000;-0.0000;0.0000}/{report.HoldoutRecallDelta:+0.0000;-0.0000;0.0000}/{report.BlindHoldoutRecallDelta:+0.0000;-0.0000;0.0000}; denseLost={report.DenseWinnerLostCount}; risk={report.RiskAfterPolicy}; blocked={report.BlockedReasons.Count}");

        static string ToJsonLines<T>(IReadOnlyList<T> items)
            => string.Join(Environment.NewLine, items.Select(item => JsonSerializer.Serialize(item, JsonOptions))) + (items.Count == 0 ? string.Empty : Environment.NewLine);
    }

    private static async Task ExecuteOutputTokenPriorityShadowAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var corpusPath = CommandHelpers.GetOption(args, "--corpus")
            ?? Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var samplesPath = CommandHelpers.GetOption(args, "--samples")
            ?? Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var dataset = await LoadRetrievalDatasetV2GeneratedDatasetAsync(corpusPath, samplesPath, cancellationToken)
            .ConfigureAwait(false);

        var sourceAwareGatePath = Path.Combine("vector", "v5", "source-aware-ranking-repair-gate.json");
        var protocolGatePath = Path.Combine("vector", "v5", "retrieval-eval-protocol-gate.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var sourceAwareGate = await ReadJsonFileAsync<SourceAwareRankingRepairReport>(sourceAwareGatePath, cancellationToken)
            .ConfigureAwait(false);
        var protocolGate = await ReadJsonFileAsync<RetrievalEvalProtocolGateReport>(protocolGatePath, cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);

        var blindCorpusPath = CommandHelpers.GetOption(args, "--blind-corpus")
            ?? Path.Combine("vector", "v5", "source-aware-ranking-blind-holdout-corpus.jsonl");
        var blindSamplesPath = CommandHelpers.GetOption(args, "--blind-samples")
            ?? Path.Combine("vector", "v5", "source-aware-ranking-blind-holdout-samples.jsonl");
        if ((sourceAwareGate?.BlindHoldoutCorpusItems.Count ?? 0) > 0
            && (sourceAwareGate?.BlindHoldoutSamples.Count ?? 0) > 0)
        {
            dataset = new RetrievalDatasetV2GeneratedDataset
            {
                CorpusItems = dataset.CorpusItems.Concat(sourceAwareGate!.BlindHoldoutCorpusItems).ToArray(),
                Samples = dataset.Samples.Concat(sourceAwareGate.BlindHoldoutSamples).ToArray()
            };
        }
        else if (File.Exists(blindCorpusPath) && File.Exists(blindSamplesPath))
        {
            var blind = await LoadRetrievalDatasetV2GeneratedDatasetAsync(blindCorpusPath, blindSamplesPath, cancellationToken)
                .ConfigureAwait(false);
            dataset = new RetrievalDatasetV2GeneratedDataset
            {
                CorpusItems = dataset.CorpusItems.Concat(blind.CorpusItems).ToArray(),
                Samples = dataset.Samples.Concat(blind.Samples).ToArray()
            };
        }

        var skipSourceScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipSourceScan
            ? new RuntimeObservableFeatureContractSourceScan { ScanPerformed = false }
            : ScanRunnerSourcesForFixtureSpecialCasing();
        var options = new OutputTokenPriorityShadowGateOptions
        {
            Protocol = protocolGate?.Protocol,
            ProfileName = CommandHelpers.GetOption(args, "--profile") ?? SourceAwareRankingProfileIds.CombinedSafe,
            RequireV514GatePassed = !CommandHelpers.HasFlag(args, "--skip-v514-gate"),
            RequireV511ProtocolGatePassed = !CommandHelpers.HasFlag(args, "--skip-v511-protocol-gate"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireSourceScan = !skipSourceScan,
            TotalTokenBudget = CommandHelpers.GetIntOption(args, "--token-budget", 4096),
            PerPackageTokenBudget = CommandHelpers.GetIntOption(args, "--package-token-budget", 128),
            SectionTokenBudget = CommandHelpers.GetIntOption(args, "--section-token-budget", 2048),
            MaxPackageItemCount = CommandHelpers.GetIntOption(args, "--max-package-items", 16),
            MetricTolerance = CommandHelpers.GetDoubleOption(args, "--metric-tolerance", 1e-9),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            WriteFormalPackage = false,
            MutatePackingPolicy = false,
            MutatePackageOutput = false,
            MutateFormalSelectedSet = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["corpus"] = corpusPath,
            ["samples"] = samplesPath,
            ["blindCorpus"] = blindCorpusPath,
            ["blindSamples"] = blindSamplesPath,
            ["v514SourceAwareRankingGate"] = sourceAwareGatePath,
            ["v511ProtocolGate"] = protocolGatePath,
            ["runtimeChangeGate"] = runtimeGatePath
        };

        var runner = new OutputTokenPriorityShadowGateRunner();
        var gateMode = string.Equals(subcommand, "vector-output-token-priority-shadow-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(dataset, sourceAwareGate, protocolGate, runtimeGate, sourceScan, options, sourceReports)
            : runner.BuildShadow(dataset, sourceAwareGate, protocolGate, runtimeGate, sourceScan, options, sourceReports);
        var fileName = gateMode ? "output-token-priority-shadow-gate" : "output-token-priority-shadow";
        var title = gateMode ? "Output Token Priority Shadow Gate" : "Output Token Priority Shadow";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(OutputTokenPriorityShadowGateRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Output token priority shadow artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] shadowPassed={report.ShadowPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; profile={report.ProfileName}; samples={report.SampleCount}; tokenDeltaTotal={report.TokenDeltaTotal}; tokenDeltaMax={report.TokenDeltaMax}; priorityInversion={report.PriorityInversionCount}; droppedRequired={report.DroppedRequiredCandidateCount}; sectionMismatch={report.SectionMismatchCount}; risk={report.RiskAfterPolicy}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalAdapterInputContractAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(Path.Combine("vector", "v5"));
        Directory.CreateDirectory(outputDirectory);

        var planGatePath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan-gate.json");
        var planFallbackPath = Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan.json");
        var outputPolicyGatePath = Path.Combine("vector", "v5", "output-token-priority-shadow-gate.json");
        var outputPolicyFallbackPath = Path.Combine("vector", "v5", "output-token-priority-shadow.json");
        var runtimeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");

        var planGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterPlanReport>(planGatePath, cancellationToken)
            .ConfigureAwait(false);
        var planSourcePath = planGatePath;
        if (planGate is null)
        {
            planGate = await ReadJsonFileAsync<ShadowFormalRetrievalAdapterPlanReport>(planFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (planGate is not null)
            {
                planSourcePath = planFallbackPath;
            }
        }

        var outputPolicyGate = await ReadJsonFileAsync<OutputTokenPriorityShadowGateReport>(outputPolicyGatePath, cancellationToken)
            .ConfigureAwait(false);
        var outputPolicySourcePath = outputPolicyGatePath;
        if (outputPolicyGate is null)
        {
            outputPolicyGate = await ReadJsonFileAsync<OutputTokenPriorityShadowGateReport>(outputPolicyFallbackPath, cancellationToken)
                .ConfigureAwait(false);
            if (outputPolicyGate is not null)
            {
                outputPolicySourcePath = outputPolicyFallbackPath;
            }
        }

        var runtimeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeGatePath, cancellationToken)
            .ConfigureAwait(false);

        var formalSource = CommandHelpers.GetOption(args, "--formal-source") ?? string.Empty;
        var formalSources = formalSource
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var skipSourceScan = CommandHelpers.HasFlag(args, "--skip-source-scan");
        var sourceScan = skipSourceScan
            ? new FormalAdapterInputContractSourceScan { ScanPerformed = false }
            : ScanFormalAdapterInputContractSources(formalSources);

        var options = new FormalAdapterInputContractOptions
        {
            RequireV51PlanGatePassed = !CommandHelpers.HasFlag(args, "--skip-v51-plan-gate"),
            RequireV515OutputPolicyGatePassed = !CommandHelpers.HasFlag(args, "--skip-v515-output-policy-gate"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireSourceScan = !skipSourceScan,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            WriteFormalPackage = false,
            MutatePackingPolicy = false,
            MutatePackageOutput = false,
            MutateVectorStoreBinding = false
        };
        var sourceReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v51ShadowFormalRetrievalAdapterPlanGate"] = planSourcePath,
            ["v515OutputTokenPriorityShadowGate"] = outputPolicySourcePath,
            ["runtimeChangeGate"] = runtimeGatePath
        };

        var runner = new FormalAdapterInputContractRunner();
        var gateMode = string.Equals(subcommand, "vector-formal-adapter-input-contract-gate", StringComparison.OrdinalIgnoreCase);
        var report = gateMode
            ? runner.BuildGate(planGate, outputPolicyGate, runtimeGate, sourceScan, options, sourceReports)
            : runner.BuildContract(planGate, outputPolicyGate, runtimeGate, sourceScan, options, sourceReports);
        var fileName = gateMode ? "formal-adapter-input-contract-gate" : "formal-adapter-input-contract";
        var title = gateMode ? "Formal Adapter Input Contract Gate" : "Formal Adapter Input Contract";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(FormalAdapterInputContractRunner.BuildMarkdown(title, report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Formal adapter input contract artifact written: {jsonPath}");
        Console.WriteLine($"[Eval] contractPassed={report.ContractPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; version={report.ContractVersion}; runtimeFields={report.RuntimeInputFieldCount}; formalForbidden={report.FormalSourceForbiddenReadCount}; evalOnlyForbidden={report.EvalOnlyForbiddenReadCount}; blocked={report.BlockedReasons.Count}");
    }
}
