using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Runners;
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
using ContextCore.Evaluation.Learning;


namespace ContextCore.Evaluation.Commands;

public static partial class EvalCommand
{
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

    private static PolicyFeedbackDatasetService? CreatePolicyFeedbackDatasetServiceForEval(IEvalHost service)
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

    private static bool ParseBoolOption(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static double ParseDoubleOption(string? value, double defaultValue)
    {
        return !string.IsNullOrWhiteSpace(value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }
}
