using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Learning;
using ContextCore.Evaluation.Models;
using ContextCore.Evaluation.Runners;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using ContextCore.Evaluation.Vector;
using ContextCore.Evaluation.Vector.Dataset;

namespace ContextCore.Evaluation.Commands;

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
        IEvalHost service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var subcommand = args.Count > 0 ? args[0] : string.Empty;
        var registry = BuildSubcommandRegistry();
        if (string.IsNullOrEmpty(subcommand) || !registry.TryGetEntry(subcommand, out var entry))
        {
            PrintUsage();
            return;
        }

        await entry!.Handler(service, args, subcommand, cancellationToken).ConfigureAwait(false);
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

    private static async Task ExecuteRelationCorpusHygieneAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var contextsRoot = ResolveContextsRoot();
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(current, "artifacts", "eval", "relation-corpus-hygiene-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(current, "artifacts", "eval", "relation-corpus-hygiene-report.md");

        var builder = new RelationCorpusHygieneReportBuilder();
        var report = await builder.BuildAsync(contextsRoot, cancellationToken).ConfigureAwait(false);

        await WriteJsonAsync(report, outputPath, cancellationToken)
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
        IEvalHost service,
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

        var policyFeedbackService = CreatePolicyFeedbackDatasetServiceForEval(service);
        var featureService = new LearningFeatureDatasetService(policyFeedbackService);
        var result = await featureService.ExportAsync(
            workspaceId,
            collectionId,
            sessionId,
            outputDirectory,
            evalReports.Count == 0 ? null : evalReports,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Learning feature dataset exported: {result.OutputDirectory}");
        Console.WriteLine($"[Eval] Policy feedback features: {result.FeatureCount} -> {result.PolicyFeedbackFeaturesPath}");
        Console.WriteLine($"[Eval] Ranking pairs: {result.RankingPairCount} -> {result.RankingPairsPath}");
        Console.WriteLine($"[Eval] Router intent examples: {result.RouterIntentExampleCount} -> {result.RouterIntentExamplesPath}");
    }

    private static async Task ExecuteVectorReindexPlanAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var request = await BuildVectorReindexRequestAsync(service, args, apply: false, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("artifacts", "vector", "vector-reindex-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("artifacts", "vector", "vector-reindex-report.md");

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

        await WriteJsonAsync(result, outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorReindexReportRenderer.ToMarkdown(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector reindex plan written: {outputPath}");
        Console.WriteLine($"[Eval] Vector reindex plan markdown written: {markdownPath}");
    }

    private static async Task ExecuteVectorReindexApplyAsync(
        IEvalHost service,
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
            ?? Path.Combine("artifacts", "vector", "vector-reindex-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("artifacts", "vector", "vector-reindex-report.md");

        if (service.State.IsServiceMode)
        {
            var response = await service.SubmitServiceVectorReindexAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await WriteJsonAsync(response, outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(EvalVectorRenderer.RenderVectorReindexSubmit(response), markdownPath, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Eval] Vector reindex job submitted: {response.Job.JobId}");
            return;
        }

        var providerOptions = BuildEmbeddingProviderOptions(args);
        var providerDiagnostics = BuildProviderDiagnostics(providerOptions);
        if (providerDiagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
        {
            var blockedResult = NewVectorReindexProviderBlockedResult(request, providerDiagnostics);
            await WriteJsonAsync(blockedResult, outputPath, cancellationToken)
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
        await WriteJsonAsync(result, outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorReindexReportRenderer.ToMarkdown(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector reindex apply written: {outputPath}");
        Console.WriteLine($"[Eval] created={result.Summary.Created}, updated={result.Summary.Updated}, failed={result.Summary.Failed}");
    }

    private static async Task ExecuteVectorIndexDiagnosticsAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("artifacts", "vector", "vector-index-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("artifacts", "vector", "vector-index-diagnostics.md");

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

        await WriteJsonAsync(report, outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildVectorIndexDiagnosticsMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector diagnostics written: {outputPath}");
    }

    private static async Task ExecuteVectorIndexCoverageAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var request = await BuildVectorCoverageReindexRequestAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("artifacts", "vector", "vector-index-coverage-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("artifacts", "vector", "vector-index-coverage-report.md");

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
        await WriteJsonAsync(report, outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorIndexCoverageReportBuilder.ToMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector index coverage written: {outputPath}");
        Console.WriteLine($"[Eval] coverage={report.CoverageRate:P2}, recommendation={report.Recommendation}");
    }

    private static async Task ExecuteVectorQueryPreviewAsync(
        IEvalHost service,
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
            ?? Path.Combine("artifacts", "vector", "vector-query-preview.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("artifacts", "vector", "vector-query-preview.md");

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

        await WriteJsonAsync(result, outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(EvalVectorRenderer.RenderVectorQueryPreview(result), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Vector query preview written: {outputPath}");
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
                : Path.Combine("artifacts", "eval", "embedding-provider-smoke-report.json"));
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? (isQwen3Provider
                ? Qwen3OutputPath("embedding-provider-smoke.md")
                : Path.Combine("artifacts", "eval", "embedding-provider-smoke-report.md"));

        var tester = new EmbeddingProviderSmokeTester();
        var report = await tester.RunAsync(providerOptions, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(report, outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(EmbeddingProviderSmokeTester.ToMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Embedding provider smoke report written: {outputPath}");
        Console.WriteLine($"[Eval] provider={report.ProviderId}; type={report.ProviderType}; succeeded={report.Succeeded}; diagnostics={report.Diagnostics.Count}");
    }

    private static string AlignmentOutputPath(string fileName)
    {
        return Path.Combine("artifacts", "vector", "alignment", fileName);
    }

    private static async Task ExecuteVectorRetrievalDatasetAlignmentAuditAsync(
        IEvalHost service,
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
            await WriteJsonAsync(a3Report, a3OutputPath, cancellationToken)
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
            await WriteJsonAsync(extendedReport, extendedOutputPath, cancellationToken)
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
            await WriteJsonAsync(summary, summaryOutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(RetrievalDatasetAlignmentAuditRunner.BuildMarkdownSummary(summary), markdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"[Eval] Vector retrieval dataset alignment audit written: {summaryOutputPath}");
        Console.WriteLine($"[Eval] recommendation={summary.Recommendation}; issues={summary.AlignmentIssueCount}");
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

    private static async Task<VectorReindexRequest> BuildVectorReindexRequestAsync(
        IEvalHost service,
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
        IEvalHost service,
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
        IEvalHost service,
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

    private static string ResolveVectorCommandWorkspaceId(IEvalHost service, IReadOnlyList<string> args)
    {
        return CommandHelpers.GetOption(args, "--workspace")
               ?? (IsVectorStoreSourceMode(args) ? service.State.WorkspaceId : VectorEvalCorpusWorkspaceId);
    }

    private static string ResolveVectorCommandCollectionId(IEvalHost service, IReadOnlyList<string> args)
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
        IEvalHost service,
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
        IEvalHost service,
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
        return Path.Combine("artifacts", "vector", "providers", "qwen3", fileName);
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

    private static async Task WriteJsonAsync<T>(
        T report,
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
        Console.WriteLine($"平均噪声违规率: {report.AvgRetrievalNoiseViolationRatio:P2} | 平均未用预算比: {report.AvgUnusedBudgetRatio:P2} | 黄金 Token 占比: {report.AvgMustHitTokenShare:P2}");
        Console.WriteLine($"约束符合率: {report.PackageConstraintHitRate:P2} | 实体符合率: {report.PackageEntityHitRate:P2} | 不确定性检测率: {report.PackageUncertaintyHitRate:P2}");
        Console.WriteLine($"平均指标计数 | 检索词数: {report.AvgRawSearchTokensCount:F1} | 语义锚点数: {report.AvgSemanticAnchorsCount:F1} | 候选数: {report.AvgCandidatesCount:F1} | 选中数: {report.AvgSelectedCount:F1} | 排除数: {report.AvgExcludedCount:F1}");
        Console.WriteLine("------------------------------------------------------------------------------------------------------------------------");

        // 使用报告中已固化的模式汇总；老 JSON 报告缺少该字段时从 Results 回退计算。
        var modeSummaries = GetModeSummaries(report);
        Console.WriteLine("\n[场景分组摘要]");
        Console.WriteLine("| 评测场景/模式 | 样本总数 | Passed | Warnings | Failed | 通过率 | Recall@3 | Recall@10 | MRR | Noise | Waste | 黄金Token比 | 约束率 | 实体率 | 选中数 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var summary in modeSummaries)
        {
            Console.WriteLine($"| {summary.Mode,-13} | {summary.TotalSamples,8} | {summary.PassedSamples,6} | {summary.PassedWithWarningsSamples,8} | {summary.FailedSamples,6} | {summary.PassRate:P1} | {summary.AvgRetrievalRecall3:P1} | {summary.AvgRetrievalRecall10:P1} | {summary.AvgRetrievalMrr:F3} | {summary.AvgRetrievalNoiseViolationRatio:P1} | {summary.AvgPackageWasteRatio:P1} | {summary.AvgMustHitTokenShare:P1} | {summary.PackageConstraintHitRate:P1} | {summary.PackageEntityHitRate:P1} | {summary.AvgSelectedCount,6:F1} |");
        }

        Console.WriteLine("\n[详细评测结果]");
        Console.WriteLine("| 样本 ID | 评测场景/模式 | 精准状态 | Recall@3 | Recall@10 | MRR | 黄金Token比 | 约束契合 | 实体契合 | 选中数 | 黄金金标备注 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
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
            Console.WriteLine($"| {res.SampleId,-15} | {res.Mode,-13} | {stateStr,-10} | {res.RetrievalRecall3:P1} | {res.RetrievalRecall10:P1} | {res.RetrievalMrr:F3} | {res.MustHitTokenShare:P1} | {(res.PackageHasAllConstraints ? "是" : "否"),-4} | {(res.PackageHasAllEntities ? "是" : "否"),-4} | {res.SelectedCount,6} | {note} |");
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
        sb.AppendLine("| 评测场景/模式 | 样本总数 | Passed | Warnings | Failed | 通过率 | 平均 Recall@3 | 平均 Recall@10 | 平均 MRR | 噪声违规率 | Token 浪费率 | 黄金 Token 比 | 约束符合率 | 实体符合率 | 平均选中数 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var summary in GetModeSummaries(report))
        {
            sb.AppendLine($"| {summary.Mode} | {summary.TotalSamples} | {summary.PassedSamples} | {summary.PassedWithWarningsSamples} | {summary.FailedSamples} | {summary.PassRate:P1} | {summary.AvgRetrievalRecall3:P1} | {summary.AvgRetrievalRecall10:P1} | {summary.AvgRetrievalMrr:F4} | {summary.AvgRetrievalNoiseViolationRatio:P1} | {summary.AvgPackageWasteRatio:P1} | {summary.AvgMustHitTokenShare:P1} | {summary.PackageConstraintHitRate:P1} | {summary.PackageEntityHitRate:P1} | {summary.AvgSelectedCount:F1} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 3. 详细测试清单");
        sb.AppendLine();
        sb.AppendLine("| 样本 ID | 场景模式 | 精准状态 | Recall@3 | Recall@10 | MRR | 黄金 Token 比 | 约束率 | 实体率 | 选中数 | 黄金金标备注 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
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
            sb.AppendLine($"| {res.SampleId} | {res.Mode} | {stateStr} | {res.RetrievalRecall3:P1} | {res.RetrievalRecall10:P1} | {res.RetrievalMrr:F4} | {res.MustHitTokenShare:P1} | {(res.PackageHasAllConstraints ? "是" : "否")} | {(res.PackageHasAllEntities ? "是" : "否")} | {res.SelectedCount} | {res.GoldenNotes} |");
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
        sb.AppendLine("SampleId,Mode,Succeeded,RetrievalRecall5,RetrievalRecall10,RetrievalMrr,RetrievalNoiseViolationRatio,PackageTokenWasteRatio,PackageHasAllConstraints,PackageHasAllEntities,PackageHasAllUncertainties,AnchorsCount,CandidatesCount,SelectedCount,ExcludedCount,PackageBuildTrace,ErrorMessage,GoldenNotes");
        foreach (var res in report.Results)
        {
            sb.AppendLine($"{EscapeCsv(res.SampleId)},{EscapeCsv(res.Mode)},{res.Succeeded},{res.RetrievalRecall5},{res.RetrievalRecall10},{res.RetrievalMrr},{res.RetrievalNoiseViolationRatio},{res.PackageTokenWasteRatio},{res.PackageHasAllConstraints},{res.PackageHasAllEntities},{res.PackageHasAllUncertainties},{res.AnchorsCount},{res.CandidatesCount},{res.SelectedCount},{res.ExcludedCount},{EscapeCsv(res.PackageBuildTrace)},{EscapeCsv(res.ErrorMessage)},{EscapeCsv(res.GoldenNotes)}");
        }
        return sb.ToString();
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

    // ── A5 性能基线 ───────────────────────────────────────────────
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

    // ── A5.3 规模查询延迟测试 ─────────────────────────────────
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

    // ── A5 专项检索评测 ──────────────────────────────────────────
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

    private static async Task<T?> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
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

}





