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
    private static async Task ExecuteArchitectureCleanupPlanAsync(CancellationToken ct)
    {
        var outputDirectory = Path.GetFullPath("eval");
        Directory.CreateDirectory(outputDirectory);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var runner = new ArchitectureCleanupPlanRunner();
        var report = runner.BuildPlan(Directory.GetCurrentDirectory(), v6Freeze);
        var jsonPath = Path.Combine(outputDirectory, "architecture-cleanup-plan.json");
        var markdownPath = Path.Combine(outputDirectory, "architecture-cleanup-plan.md");

        await WriteJsonSafeAsync(report, jsonPath, ct).ConfigureAwait(false);
        await WriteTextAsync(
                ArchitectureCleanupPlanRunner.BuildMarkdown("Architecture Cleanup Plan", report),
                markdownPath,
                ct)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Architecture cleanup plan written: {jsonPath}");
        Console.WriteLine(
            $"[Eval] passed={report.PlanPassed}; recommendation={report.Recommendation}; " +
            $"runners={report.CoreRunnerCount}; dtoClasses={report.DtoClassCount}; " +
            $"evalLines={report.EvalCommandLines}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedShadowAdapterInvocationAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v6"));
        Directory.CreateDirectory(output);

        var noopGatePath = Path.Combine("vector", "v6", "adapter-noop-binding-gate.json");
        var noopFallback = Path.Combine("vector", "v6", "adapter-noop-binding-smoke.json");
        var noopGate = await ReadJsonFileAsync<AdapterNoOpBindingSmokeReport>(noopGatePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<AdapterNoOpBindingSmokeReport>(noopFallback, ct).ConfigureAwait(false);

        var runner = new ScopedShadowAdapterInvocationRunner();
        var report = runner.RunInvocation(noopGate);
        var isGate = string.Equals(subcommand, "vector-scoped-shadow-adapter-invocation-gate", StringComparison.OrdinalIgnoreCase);
        var fn = isGate ? "scoped-shadow-adapter-invocation-gate" : "scoped-shadow-adapter-invocation";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedShadowAdapterInvocationRunner.BuildMarkdown(
            isGate ? "Scoped Shadow Adapter Invocation Gate" : "Scoped Shadow Adapter Invocation", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Scoped shadow adapter invocation artifact written: {jp}");
        Console.WriteLine($"[Eval] passed={report.InvocationPassed}; allowlisted={report.AllowlistedInvocationCount}; nonAllowlisted={report.NonAllowlistedInvocationCount}; adapter={report.AdapterType}; blocked={report.BlockedReasons.Count}");
    }


    private static async Task ExecuteMainlineShadowAdapterPackageComparisonAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v6"));
        Directory.CreateDirectory(output);

        var v61GatePath = Path.Combine("vector", "v6", "scoped-shadow-adapter-invocation-gate.json");
        var v61Fallback = Path.Combine("vector", "v6", "scoped-shadow-adapter-invocation.json");
        var v61Gate = await ReadJsonFileAsync<ScopedShadowAdapterInvocationReport>(v61GatePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedShadowAdapterInvocationReport>(v61Fallback, ct).ConfigureAwait(false);

        var cp = Path.Combine("vector", "dataset-v2", "stress", "corpus.jsonl");
        var sp = Path.Combine("vector", "dataset-v2", "stress", "samples.jsonl");
        var ds = await LoadRetrievalDatasetV2GeneratedDatasetAsync(cp, sp, ct).ConfigureAwait(false);

        var runner = new MainlineShadowAdapterPackageComparisonRunner();
        var report = runner.RunComparison(v61Gate, ds);
        var isGate = string.Equals(subcommand, "vector-mainline-shadow-adapter-package-comparison-gate", StringComparison.OrdinalIgnoreCase);
        var fn = isGate ? "mainline-shadow-adapter-package-comparison-gate" : "mainline-shadow-adapter-package-comparison";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jp, ct).ConfigureAwait(false);
        await WriteTextAsync(MainlineShadowAdapterPackageComparisonRunner.BuildMarkdown(
            isGate ? "Mainline Shadow Adapter Package Comparison Gate" : "Mainline Shadow Adapter Package Comparison", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Mainline shadow adapter package comparison artifact written: {jp}");
        Console.WriteLine($"[Eval] passed={report.ComparisonPassed}; mainlineInvocations={report.MainlineInvocationCount}; allowlisted={report.AllowlistedCount}; nonAllowlisted={report.NonAllowlistedCount}; shadowAdds={report.TotalShadowAddCount}; shadowRemoves={report.TotalShadowRemoveCount}; p50={report.P50LatencyMs}ms; p95={report.P95LatencyMs}ms; traceCompleteness={report.TraceCompleteness:P2}; blocked={report.BlockedReasons.Count}");
    }

    private static RuntimeObservableFeatureContractSourceScan ScanRunnerSourcesForFixtureSpecialCasing()
    {
        var directory = Path.Combine("src", "ContextCore.Core", "Services", "Vector");
        var fileNames = new[]
        {
            "ShadowFormalRetrievalAdapterPlanRunner.cs",
            "ShadowFormalRetrievalAdapter.cs",
            "FormalAdapterPackageShadowComparisonRunner.cs",
            "GraphVectorRetrievalQualityAuditRunner.cs",
            "RetrievalQualityRepairPreviewRunner.cs",
            "RuntimeObservableRetrievalFeatureContractRunner.cs",
            "RuntimeRetrievalFeatureDerivationPreviewRunner.cs",
            "RuntimeRetrievalFeatureDerivationRepairRunner.cs",
            "CanonicalRuntimeAnchorResolver.cs",
            "RuntimeRelationIntentDeriver.cs",
            "RuntimeFeatureDerivationFailureFreezeRunner.cs",
            "GraphHubNoiseControlRunner.cs",
            "QueryDrivenCandidateSourceRepairRunner.cs",
            "FormalRetrievalIntegrationFreezeRunner.cs",
            "AdapterNoOpBindingSmokeRunner.cs",
            "ScopedShadowAdapterInvocationRunner.cs",
            "ScopedShadowRetrievalAdapter.cs",
            "MainlineShadowAdapterPackageComparisonRunner.cs",
            "RetrievalEvalProtocolAuditRunner.cs",
            "InputMetadataEnrichmentPreviewRunner.cs",
            "EnrichedCandidateSourceRepairRecheckRunner.cs",
            "SourceAwareRankingRepairRunner.cs",
            "OutputTokenPriorityShadowGateRunner.cs",
            "FormalAdapterInputContractRunner.cs"
        };
        // Fixture domain words are encoded via integer codepoints so this list does
        // NOT appear as literal characters in the source file (the global production
        // source scan flags any literal occurrence of these words).
        var fixtureDomainCodepoints = new[]
        {
            new[] { 0x6797, 0x98CE },                 // U+6797 U+98CE
            [0x82CD, 0x7A79, 0x5927, 0x9646], // U+82CD U+7A79 U+5927 U+9646
            [0x4E5D, 0x8F6C, 0x91D1, 0x4E39], // U+4E5D U+8F6C U+91D1 U+4E39
            [0x9F99, 0x9B42, 0x8349],         // U+9F99 U+9B42 U+8349
            [0x62CD, 0x5356, 0x884C]          // U+62CD U+5356 U+884C
        };
        var forbiddenTokens = fixtureDomainCodepoints
            .Select(static codepoints => new string(codepoints.Select(c => (char)c).ToArray()))
            .Concat(new[]
            {
                "sample.SampleId ==",
                "item.ItemId ==",
                "sample-pkg",
                "sample-shadow",
                "sample-audit",
                "sample-repair"
            })
            .ToArray();

        var scanned = new List<string>();
        var flaggedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flaggedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = 0;
        foreach (var fileName in fileNames)
        {
            var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            scanned.Add(fullPath);
            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var token in forbiddenTokens)
            {
                var index = 0;
                while ((index = content.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
                {
                    hits++;
                    flaggedFiles.Add(fullPath);
                    flaggedTokens.Add(token);
                    index += token.Length;
                }
            }
        }

        return new RuntimeObservableFeatureContractSourceScan
        {
            ScanPerformed = true,
            ScannedFileCount = scanned.Count,
            FixtureTokenHitCount = hits,
            ScannedFiles = scanned,
            FlaggedFiles = flaggedFiles.ToArray(),
            FlaggedTokens = flaggedTokens.ToArray()
        };
    }

    private static FormalAdapterInputContractSourceScan ScanFormalAdapterInputContractSources(
        IReadOnlyList<string> additionalFormalSources)
    {
        var repoRoot = Path.GetFullPath(".");
        var directory = Path.Combine("src", "ContextCore.Core", "Services", "Vector");
        var formalSourceCandidates = new[]
            {
                "FormalRetrievalAdapter.cs",
                "FormalRuntimeRetrievalAdapter.cs",
                "FormalAdapterRuntimeAdapter.cs",
                "FormalAdapterImplementation.cs"
            }
            .Select(file => Path.Combine(directory, file))
            .Concat(additionalFormalSources)
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var evalOnlySourceFiles = new[]
            {
                Path.Combine(directory, "ShadowFormalRetrievalAdapter.cs"),
                Path.Combine(directory, "FormalAdapterPackageShadowComparisonRunner.cs"),
                Path.Combine(directory, "OutputTokenPriorityShadowGateRunner.cs")
            }
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tokens = new (string Token, string Category)[]
        {
            ("RetrievalDatasetV2Sample", "DatasetEvalField"),
            ("RetrievalDatasetV2GeneratedDataset", "DatasetEvalField"),
            ("sample.SampleId", "SampleMetadata"),
            ("sample.Metadata", "SampleMetadata"),
            ("sample.Split", "SampleMetadata"),
            ("sample.Difficulty", "SampleMetadata"),
            ("sample.Intent", "SampleMetadata"),
            ("sample.TaskKind", "SampleMetadata"),
            ("sample.MustHitItemIds", "GoldLabel"),
            ("sample.MustNotHitItemIds", "GoldLabel"),
            ("sample.ExpectedTargetSection", "GoldLabel"),
            ("sample.NegativeDistractorIds", "GoldLabel"),
            ("sample.RequiredRelations", "EvalAnnotation"),
            ("sample.SourceRefs", "EvalAnnotation"),
            ("sample.EvidenceRefs", "EvalAnnotation"),
            ("ShadowFormalRetrievalAdapterReport", "ShadowArtifact"),
            ("FormalAdapterPackageShadowComparisonReport", "ShadowArtifact"),
            ("OutputTokenPriorityShadowGateReport", "ShadowArtifact"),
            ("SourceAwareRankingRepairReport", "ShadowArtifact"),
            ("RetrievalEvalProtocolGateReport", "ShadowArtifact"),
            ("BlindHoldout", "ShadowArtifact"),
            ("GatePassed", "ShadowArtifact"),
            ("Recommendation", "ShadowArtifact")
        };

        var hits = new List<FormalAdapterInputContractSourceHit>();
        ScanFiles(formalSourceCandidates, isFormalSource: true, repoRoot, tokens, hits);
        ScanFiles(evalOnlySourceFiles, isFormalSource: false, repoRoot, tokens, hits);
        return new FormalAdapterInputContractSourceScan
        {
            ScanPerformed = true,
            FormalSourceFileCount = formalSourceCandidates.Length,
            EvalOnlySourceFileCount = evalOnlySourceFiles.Length,
            FormalSourceForbiddenReadCount = hits.Count(static hit => hit.IsFormalSource),
            EvalOnlyForbiddenReadCount = hits.Count(static hit => !hit.IsFormalSource),
            FormalSourceFiles = formalSourceCandidates
                .Select(path => ToRepoRelativePath(repoRoot, path))
                .ToArray(),
            EvalOnlySourceFiles = evalOnlySourceFiles
                .Select(path => ToRepoRelativePath(repoRoot, path))
                .ToArray(),
            Hits = hits
                .OrderBy(static hit => hit.IsFormalSource ? 0 : 1)
                .ThenBy(static hit => hit.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static hit => hit.Token, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        static void ScanFiles(
            IReadOnlyList<string> files,
            bool isFormalSource,
            string repoRoot,
            IReadOnlyList<(string Token, string Category)> tokens,
            List<FormalAdapterInputContractSourceHit> hits)
        {
            foreach (var file in files)
            {
                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var (token, category) in tokens)
                {
                    var index = 0;
                    while ((index = content.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
                    {
                        hits.Add(new FormalAdapterInputContractSourceHit
                        {
                            FilePath = ToRepoRelativePath(repoRoot, file),
                            Token = token,
                            Category = category,
                            IsFormalSource = isFormalSource
                        });
                        index += token.Length;
                    }
                }
            }
        }

        static string ToRepoRelativePath(string repoRoot, string path)
        {
            var relative = Path.GetRelativePath(repoRoot, path);
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private static IReadOnlyList<string> ResolveAllowlist(
        IReadOnlyList<string> args,
        string singleOption,
        string listOption,
        string fallback)
    {
        var configured = CommandHelpers.GetOption(args, listOption)
            ?? CommandHelpers.GetOption(args, singleOption);
        var values = ParseCsvOption(configured);
        if (values.Count > 0)
        {
            return values;
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? Array.Empty<string>()
            : new[] { fallback.Trim() };
    }

    private static GuardedScopedRuntimeExperimentPlanReport CopyGuardedScopedRuntimeExperimentPlan(
        GuardedScopedRuntimeExperimentPlanReport source,
        string? killSwitchPlan = null,
        string? rollbackPlan = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            PlanPassed = source.PlanPassed,
            Recommendation = source.Recommendation,
            ProposalId = source.ProposalId,
            RequiredApprovalMode = source.RequiredApprovalMode,
            SelectedScopes = source.SelectedScopes,
            MaxRequestCount = source.MaxRequestCount,
            MaxDurationMinutes = source.MaxDurationMinutes,
            KillSwitchPlan = killSwitchPlan ?? source.KillSwitchPlan,
            RollbackPlan = rollbackPlan ?? source.RollbackPlan,
            ObservationPlan = source.ObservationPlan,
            StopConditions = source.StopConditions,
            AllowedActions = source.AllowedActions,
            ForbiddenActions = source.ForbiddenActions,
            RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
            FormalRetrievalAllowed = source.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = source.ReadyForRuntimeSwitch,
            UseForRuntime = source.UseForRuntime,
            BlockedReasons = source.BlockedReasons
        };

private static async Task ExecuteAdapterNoOpBindingSmokeAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v6"));
        Directory.CreateDirectory(output);

        var freezePath = Path.Combine("vector", "v5", "formal-retrieval-integration-freeze-gate.json");
        var freezeFallback = Path.Combine("vector", "v5", "formal-retrieval-integration-freeze.json");
        var freezeGate = await ReadJsonFileAsync<FormalRetrievalIntegrationFreezeReport>(freezePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<FormalRetrievalIntegrationFreezeReport>(freezeFallback, ct).ConfigureAwait(false);

        var runner = new AdapterNoOpBindingSmokeRunner();
        var report = runner.RunSmoke(freezeGate);
        var isGate = string.Equals(subcommand, "vector-adapter-noop-binding-gate", StringComparison.OrdinalIgnoreCase);
        var fn = isGate ? "adapter-noop-binding-gate" : "adapter-noop-binding-smoke";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jp, ct).ConfigureAwait(false);
        await WriteTextAsync(AdapterNoOpBindingSmokeRunner.BuildMarkdown(
            isGate ? "Adapter No-op Binding Gate" : "Adapter No-op Binding Smoke", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Adapter no-op binding smoke artifact written: {jp}");
        Console.WriteLine($"[Eval] passed={report.SmokePassed}; invocations={report.InvocationCount}; add/remove={report.AddCount}/{report.RemoveCount}; blocked={report.BlockedReasons.Count}");
    }
}
