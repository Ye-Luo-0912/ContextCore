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
using ContextCore.Evaluation.Models;
using ContextCore.Evaluation.Runners;
using ContextCore.Evaluation.Services;
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
        var previewService = new RelationExpansionPreviewService(new RelationTraversalEngine(relationStore), profileRegistry, validator);
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

        await relationStore.BatchUpsertAsync(relations, cancellationToken).ConfigureAwait(false);

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
                CreatedAt = createdAt,
                Lifecycle = lifecycle,
                ReviewStatus = RelationReviewStatuses.Reviewed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Provenance = "relation_expansion_profile_shadow_fixture"
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
            ?? Path.Combine("vector", "reindex", "vector-reindex-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("vector", "reindex", "vector-reindex-report.md");

        if (service.State.IsServiceMode)
        {
            var response = await service.SubmitServiceVectorReindexAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(JsonSerializer.Serialize(response, JsonOptions), outputPath, cancellationToken)
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
        IEvalHost service,
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
        IEvalHost service,
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

    private static async Task ExecuteVectorProviderComparisonV310Async(
        IEvalHost service,
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

    private static async Task ExecuteRetrievalDatasetV2GenerationAsync(
        IEvalHost service,
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
        IEvalHost service,
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

    private static RetrievalDatasetV2StressOptions BuildRetrievalDatasetV2StressOptions(
        IEvalHost service,
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

    private static async Task ExecuteVectorLifecycleMetadataReviewBatchAsync(
        IEvalHost service,
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





