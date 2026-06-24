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
{    private static async Task ExecuteRouterIntentBaselineAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var featuresDirectory = CommandHelpers.GetOption(args, "--features-dir")
            ?? CommandHelpers.GetOption(args, "--in-dir")
            ?? Path.Combine(current, "learning", "features");
        var inputPath = CommandHelpers.GetOption(args, "--input")
            ?? CommandHelpers.GetOption(args, "--router-input")
            ?? Path.Combine(featuresDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(current, RouterIntentEvaluationRunner.DefaultOutputDirectory);

        var report = await new RouterIntentEvaluationRunner()
            .RunAsync(inputPath, outputDirectory, cancellationToken)
            .ConfigureAwait(false);

        var resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
        await MirrorExistingArtifactsAsync(
            cancellationToken,
            Path.Combine(resolvedOutputDirectory, RouterIntentEvaluationRunner.ReportFileName),
            Path.Combine(resolvedOutputDirectory, RouterIntentEvaluationRunner.MarkdownReportFileName),
            Path.Combine(resolvedOutputDirectory, RouterIntentEvaluationRunner.ConfusionMatrixFileName))
            .ConfigureAwait(false);
        Console.WriteLine($"[Eval] Router intent baseline report: {Path.Combine(resolvedOutputDirectory, RouterIntentEvaluationRunner.ReportFileName)}");
        Console.WriteLine($"[Eval] Router intent markdown: {Path.Combine(resolvedOutputDirectory, RouterIntentEvaluationRunner.MarkdownReportFileName)}");
        Console.WriteLine($"[Eval] Router intent confusion matrix: {Path.Combine(resolvedOutputDirectory, RouterIntentEvaluationRunner.ConfusionMatrixFileName)}");
        Console.WriteLine($"[Eval] Router R1 status={report.Status}; samples={report.SampleCount}; best={report.BestBaseline}; recommendation={report.Recommendation}");
        foreach (var baseline in report.Baselines)
        {
            Console.WriteLine($"[Eval] {baseline.BaselineName}: accuracy={baseline.Accuracy:P2}, macroF1={baseline.MacroF1:0.####}, lowConfidence={baseline.LowConfidenceCount}, abstain={baseline.AbstainCount}, recommendation={baseline.Recommendation}");
        }
    }

    private static async Task ExecuteRouterShadowTraceQualityAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, RouterIntentShadowReportBuilder.DefaultOutputDirectory);
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine(outputDirectory, RouterIntentShadowReportBuilder.TraceQualityReportFileName);
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine(outputDirectory, RouterIntentShadowReportBuilder.TraceQualityMarkdownFileName);
        var inputPath = CommandHelpers.GetOption(args, "--input");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId;
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? service.State.CollectionId;
        var take = 200;
        var takeArg = CommandHelpers.GetOption(args, "--take");
        if (!string.IsNullOrWhiteSpace(takeArg) && int.TryParse(takeArg, out var parsedTake) && parsedTake > 0)
        {
            take = parsedTake;
        }

        var builder = new RouterIntentShadowReportBuilder();
        RouterShadowTraceQualityReport report;
        if (service.State.IsServiceMode && service.State.ServiceClient is not null)
        {
            var traces = await service.State.ServiceClient
                .GetRouterShadowTracesAsync(workspaceId, collectionId, take, cancellationToken)
                .ConfigureAwait(false);
            report = builder.BuildTraceQualityReport(traces, workspaceId, collectionId);
            await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(
                    RouterIntentShadowReportBuilder.BuildTraceQualityMarkdownReport(report),
                    markdownPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            report = await builder.RunTraceQualityAsync(
                    workspaceId,
                    collectionId,
                    outputDirectory,
                    inputPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                Path.GetFullPath(outputPath),
                Path.Combine(Path.GetFullPath(outputDirectory), RouterIntentShadowReportBuilder.TraceQualityReportFileName),
                StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(
                Path.GetFullPath(markdownPath),
                Path.Combine(Path.GetFullPath(outputDirectory), RouterIntentShadowReportBuilder.TraceQualityMarkdownFileName),
                StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(
                        RouterIntentShadowReportBuilder.BuildTraceQualityMarkdownReport(report),
                        markdownPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await MirrorExistingArtifactsAsync(cancellationToken, outputPath, markdownPath).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Router shadow trace quality report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[Eval] Router shadow trace quality markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[Eval] Traces={report.TraceCount}; agreement={report.AgreementRate:P2}; disagreement={report.DisagreementRate:P2}; lowConfidence={report.LowConfidenceCount}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteRouterIntentShadowEvalAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, RouterIntentShadowReportBuilder.DefaultOutputDirectory);
        var inputPath = CommandHelpers.GetOption(args, "--input")
            ?? Path.Combine(
                current,
                LearningDatasetQualityReportBuilder.DefaultFeatureDirectory,
                LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);

        var (a3, extended) = await new RouterIntentShadowReportBuilder()
            .RunShadowEvalAsync(inputPath, outputDirectory, cancellationToken)
            .ConfigureAwait(false);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        await MirrorExistingArtifactsAsync(
            cancellationToken,
            Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalA3FileName),
            Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalExtendedFileName),
            Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalMarkdownFileName))
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Router shadow eval A3: {Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalA3FileName)}");
        Console.WriteLine($"[Eval] Router shadow eval Extended: {Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalExtendedFileName)}");
        Console.WriteLine($"[Eval] Router shadow eval markdown: {Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalMarkdownFileName)}");
        Console.WriteLine($"[Eval] A3 samples={a3.SampleCount}; agreement={a3.AgreementRate:P2}; fixes={a3.ShadowFixesRuntime}; breaks={a3.ShadowBreaksRuntime}; recommendation={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended samples={extended.SampleCount}; agreement={extended.AgreementRate:P2}; fixes={extended.ShadowFixesRuntime}; breaks={extended.ShadowBreaksRuntime}; recommendation={extended.Recommendation}");
    }

    private static async Task ExecuteRouterDisagreementTriageAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, RouterDisagreementTriageRunner.DefaultOutputDirectory);
        var inputPath = CommandHelpers.GetOption(args, "--input")
            ?? Path.Combine(
                current,
                LearningDatasetQualityReportBuilder.DefaultFeatureDirectory,
                LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);

        var (a3, extended) = await new RouterDisagreementTriageRunner()
            .RunAsync(inputPath, outputDirectory, cancellationToken)
            .ConfigureAwait(false);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        await MirrorExistingArtifactsAsync(
            cancellationToken,
            Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.A3ReportFileName),
            Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.ExtendedReportFileName),
            Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.MarkdownReportFileName),
            Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.HardNegativesFileName))
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Router disagreement triage A3: {Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.A3ReportFileName)}");
        Console.WriteLine($"[Eval] Router disagreement triage Extended: {Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.ExtendedReportFileName)}");
        Console.WriteLine($"[Eval] Router disagreement triage markdown: {Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.MarkdownReportFileName)}");
        Console.WriteLine($"[Eval] Router hard negatives: {Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.HardNegativesFileName)}");
        Console.WriteLine($"[Eval] A3 disagreements={a3.DisagreementCount}; fixes={a3.ShadowFixesRuntime}; breaks={a3.ShadowBreaksRuntime}; hardNegatives={a3.HardNegativeCount}; recommendation={a3.Recommendation}");
        Console.WriteLine($"[Eval] Extended disagreements={extended.DisagreementCount}; fixes={extended.ShadowFixesRuntime}; breaks={extended.ShadowBreaksRuntime}; hardNegatives={extended.HardNegativeCount}; recommendation={extended.Recommendation}");
    }

    private static async Task ExecuteRouterGuardedOptInReadinessGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var current = Directory.GetCurrentDirectory();
        var outputDirectory = CommandHelpers.GetOption(args, "--out-dir")
            ?? Path.Combine(current, RouterGuardedOptInReadinessGateRunner.DefaultOutputDirectory);
        var agreementThreshold = 0.85;
        var agreementArg = CommandHelpers.GetOption(args, "--agreement-threshold");
        if (!string.IsNullOrWhiteSpace(agreementArg)
            && double.TryParse(agreementArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedAgreement)
            && parsedAgreement is >= 0 and <= 1)
        {
            agreementThreshold = parsedAgreement;
        }

        var lowConfidenceMax = 0;
        var lowConfidenceArg = CommandHelpers.GetOption(args, "--low-confidence-max");
        if (!string.IsNullOrWhiteSpace(lowConfidenceArg)
            && int.TryParse(lowConfidenceArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLowConfidence)
            && parsedLowConfidence >= 0)
        {
            lowConfidenceMax = parsedLowConfidence;
        }

        var report = await new RouterGuardedOptInReadinessGateRunner()
            .RunAsync(
                outputDirectory,
                new RouterGuardedOptInReadinessGateOptions
                {
                    AgreementRateThreshold = agreementThreshold,
                    LowConfidenceMaxCount = lowConfidenceMax
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        await MirrorExistingArtifactsAsync(
            cancellationToken,
            Path.Combine(resolvedOutput, RouterGuardedOptInReadinessGateRunner.ReportFileName),
            Path.Combine(resolvedOutput, RouterGuardedOptInReadinessGateRunner.MarkdownReportFileName))
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Router guarded opt-in readiness gate: {Path.Combine(resolvedOutput, RouterGuardedOptInReadinessGateRunner.ReportFileName)}");
        Console.WriteLine($"[Eval] Router guarded opt-in readiness gate markdown: {Path.Combine(resolvedOutput, RouterGuardedOptInReadinessGateRunner.MarkdownReportFileName)}");
        Console.WriteLine($"[Eval] passed={report.Passed}; fixes={report.ShadowFixesRuntime}; breaks={report.ShadowBreaksRuntime}; netGain={report.NetGain}; agreement={report.AgreementRate:P2}; recommendation={report.Recommendation}");
        if (report.FailureReasons.Count > 0)
        {
            Console.WriteLine($"[Eval] blockedReasons={string.Join(",", report.FailureReasons)}");
        }
    }

}
