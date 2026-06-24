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
private static async Task ExecuteFoundationFreezeAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(ContextCoreFoundationFreezeRunner.DefaultOutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var readinessOutput = Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory);
        var readinessRunner = new LearningReadinessFreezeRunner();
        await readinessRunner.RunFreezeReportAsync(readinessOutput, cancellationToken).ConfigureAwait(false);
        await readinessRunner.RunRuntimeChangeGateAsync(readinessOutput, cancellationToken).ConfigureAwait(false);

        var report = await new ContextCoreFoundationFreezeRunner()
            .BuildFromCurrentFilesAsync(Directory.GetCurrentDirectory(), cancellationToken)
            .ConfigureAwait(false);

        var fileName = string.Equals(subcommand, "foundation-release-candidate-gate", StringComparison.OrdinalIgnoreCase)
            ? "foundation-release-candidate-gate"
            : "foundation-freeze-report";
        var jsonPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{fileName}.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(ContextCoreFoundationFreezeRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] ContextCore foundation freeze report written: {jsonPath}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; foundation={report.ContextCoreFoundation}; vector={report.VectorFoundation}; runtimeSwitch={report.RuntimeSwitchAllowed}; recommendation={report.Recommendation}; missingReports={report.MissingReportCount}; missingDocs={report.MissingDocCount}");
    }

private static async Task ExecuteFoundationReproducibilityCheckAsync(CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(FoundationReproducibilityRunner.DefaultOutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var readinessOutput = Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory);
        var readinessRunner = new LearningReadinessFreezeRunner();
        await readinessRunner.RunFreezeReportAsync(readinessOutput, cancellationToken).ConfigureAwait(false);
        await readinessRunner.RunRuntimeChangeGateAsync(readinessOutput, cancellationToken).ConfigureAwait(false);

        var foundationReport = await new ContextCoreFoundationFreezeRunner()
            .BuildFromCurrentFilesAsync(Directory.GetCurrentDirectory(), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                JsonSerializer.Serialize(foundationReport, JsonOptions),
                Path.Combine(outputDirectory, "foundation-release-candidate-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                ContextCoreFoundationFreezeRunner.BuildMarkdown(foundationReport),
                Path.Combine(outputDirectory, "foundation-release-candidate-gate.md"),
                cancellationToken)
            .ConfigureAwait(false);

        var report = await new FoundationReproducibilityRunner()
            .BuildFromCurrentFilesAsync(Directory.GetCurrentDirectory(), cancellationToken)
            .ConfigureAwait(false);

        var jsonPath = Path.Combine(outputDirectory, "foundation-reproducibility-check.json");
        var markdownPath = Path.Combine(outputDirectory, "foundation-reproducibility-check.md");
        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), jsonPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(FoundationReproducibilityRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Eval] Foundation reproducibility check written: {jsonPath}");
        Console.WriteLine($"[Eval] passed={report.ReproducibilityPassed}; recommendation={report.Recommendation}; foundationGate={report.FoundationGateStatus}; runtimeGate={report.RuntimeChangeGateStatus}; p15={report.P15GateStatus}; localSecrets={report.LocalSecretPathCount}");
    }
}
