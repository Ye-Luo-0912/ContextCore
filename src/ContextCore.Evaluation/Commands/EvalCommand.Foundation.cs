using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
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
}
