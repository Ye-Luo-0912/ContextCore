using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// RC0 可复现性检查器；只读取报告和 git 状态，不执行构建或改变 runtime。
/// </summary>
public sealed class FoundationReproducibilityRunner
{
    public const string DefaultOutputDirectory = "foundation";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] CriticalReportPaths =
    [
        Path.Combine("foundation", "foundation-release-candidate-gate.md"),
        Path.Combine("foundation", "foundation-release-candidate-gate.json"),
        Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.md"),
        Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json"),
        Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.md"),
        Path.Combine("docs", "ContextCore_Foundation_Freeze_Report.md"),
        Path.Combine("eval", "eval-report-p15-a3.json"),
        Path.Combine("eval", "eval-report-p15-extended.json")
    ];

    private static readonly string[] LocalSecretIndicators =
    [
        "secret",
        ".env",
        "appsettings.local",
        "appsettings.development.local",
        "appsettings.postgres.local",
        "connectionstrings.local"
    ];

    private static readonly string[] LargeModelExtensions =
    [
        ".onnx",
        ".safetensors",
        ".gguf",
        ".bin",
        ".pt",
        ".pth"
    ];

    public async Task<FoundationReproducibilityReport> BuildFromCurrentFilesAsync(
        string currentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var foundationGate = await ReadJsonAsync<ContextCoreFoundationFreezeReport>(
                Path.Combine(currentDirectory, "foundation", "foundation-release-candidate-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonAsync<LearningRuntimeChangeReadinessGateReport>(
                Path.Combine(currentDirectory, "learning", "readiness", "learning-runtime-change-readiness-gate.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var p15A3 = await ReadP15StatusAsync(Path.Combine(currentDirectory, "eval", "eval-report-p15-a3.json"), cancellationToken)
            .ConfigureAwait(false);
        var p15Extended = await ReadP15StatusAsync(Path.Combine(currentDirectory, "eval", "eval-report-p15-extended.json"), cancellationToken)
            .ConfigureAwait(false);

        var criticalReportCoverage = CriticalReportPaths.ToDictionary(
            static path => path,
            path => File.Exists(Path.Combine(currentDirectory, path)),
            StringComparer.OrdinalIgnoreCase);
        var gitStatusPaths = await ReadGitStatusPathsAsync(currentDirectory, cancellationToken)
            .ConfigureAwait(false);

        return BuildReport(
            foundationGate,
            runtimeGate,
            p15A3,
            p15Extended,
            criticalReportCoverage,
            CategorizeGitStatus(gitStatusPaths));
    }

    public FoundationReproducibilityReport BuildReport(
        ContextCoreFoundationFreezeReport? foundationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeGate,
        P15ReportStatus p15A3,
        P15ReportStatus p15Extended,
        IReadOnlyDictionary<string, bool> criticalReportCoverage,
        IReadOnlyDictionary<string, IReadOnlyList<string>> gitStatusCategories)
    {
        var blocked = new List<string>();

        var foundationGatePassed = foundationGate?.FreezePassed == true;
        AddBlockedIfFalse(blocked, foundationGatePassed, "FoundationReleaseCandidateGateMissingOrFailed");

        var runtimeGatePassed = runtimeGate?.Passed == true;
        AddBlockedIfFalse(blocked, runtimeGatePassed, "RuntimeChangeGateMissingOrFailed");

        var p15Passed = p15A3.Passed && p15Extended.Passed;
        AddBlockedIfFalse(blocked, p15Passed, "P15GateMissingOrFailed");

        var formalRetrievalAllowed = foundationGate?.FormalRetrievalAllowed == true;
        AddBlockedIfFalse(blocked, !formalRetrievalAllowed, "FormalRetrievalAllowed");

        var runtimeSwitchAllowed = foundationGate?.RuntimeSwitchAllowed == true
            || foundationGate?.ReadyForRuntimeSwitch == true;
        AddBlockedIfFalse(blocked, !runtimeSwitchAllowed, "RuntimeSwitchAllowed");

        var packingPolicyChanged = foundationGate?.PackingPolicyChanged == true;
        var packageOutputChanged = foundationGate?.PackageOutputChanged == true;
        AddBlockedIfFalse(blocked, !packingPolicyChanged, "PackingPolicyChanged");
        AddBlockedIfFalse(blocked, !packageOutputChanged, "PackageOutputChanged");

        var useForRuntimeFalse = IsUseForRuntimeFalse(foundationGate);
        AddBlockedIfFalse(blocked, useForRuntimeFalse, "UseForRuntimeBoundaryMissing");

        var missingCriticalReportCount = criticalReportCoverage.Count(static pair => !pair.Value);
        if (missingCriticalReportCount != 0)
        {
            blocked.Add("MissingCriticalReport");
        }

        var localSecretPaths = gitStatusCategories.TryGetValue("local config / secrets", out var secrets)
            ? secrets
            : Array.Empty<string>();
        var localSecretsDetected = localSecretPaths.Count != 0;
        AddBlockedIfFalse(blocked, !localSecretsDetected, "LocalSecretsDetected");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;

        return new FoundationReproducibilityReport
        {
            OperationId = $"foundation-reproducibility-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            ReproducibilityPassed = passed,
            Recommendation = passed
                ? FoundationReproducibilityRecommendations.ReadyForReleaseCandidateReproduction
                : ResolveRecommendation(distinctBlocked),
            ExpectedOutputSummary = "Build/test/P15/runtime-change/foundation gates pass; runtime switch and formal retrieval remain disabled; no critical report or secret leakage.",
            GitStatusCategories = gitStatusCategories,
            CriticalReportCoverage = criticalReportCoverage,
            BoundaryChecks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["FoundationReleaseCandidateGatePassed"] = foundationGatePassed,
                ["RuntimeChangeGatePassed"] = runtimeGatePassed,
                ["P15GatePassed"] = p15Passed,
                ["FormalRetrievalAllowedFalse"] = !formalRetrievalAllowed,
                ["RuntimeSwitchAllowedFalse"] = !runtimeSwitchAllowed,
                ["ReadyForRuntimeSwitchFalse"] = foundationGate?.ReadyForRuntimeSwitch != true,
                ["PackingPolicyChangedFalse"] = !packingPolicyChanged,
                ["PackageOutputChangedFalse"] = !packageOutputChanged,
                ["UseForRuntimeFalseWhereApplicable"] = useForRuntimeFalse
            },
            FoundationGateStatus = foundationGate is null
                ? "MissingReport"
                : foundationGate.FreezePassed ? "Passed" : "Failed",
            RuntimeChangeGateStatus = runtimeGate is null
                ? "MissingReport"
                : runtimeGate.Passed ? "Passed" : "Failed",
            P15GateStatus = p15Passed ? "Passed" : "Failed",
            LocalSecretsDetected = localSecretsDetected,
            LocalSecretPathCount = localSecretPaths.Count,
            LocalSecretPaths = localSecretPaths,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildMarkdown(FoundationReproducibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# ContextCore Foundation Reproducibility Check");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- ReproducibilityPassed: `{report.ReproducibilityPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FoundationGateStatus: `{report.FoundationGateStatus}`");
        builder.AppendLine($"- RuntimeChangeGateStatus: `{report.RuntimeChangeGateStatus}`");
        builder.AppendLine($"- P15GateStatus: `{report.P15GateStatus}`");
        builder.AppendLine($"- LocalSecretsDetected: `{report.LocalSecretsDetected}`");
        builder.AppendLine($"- LocalSecretPathCount: `{report.LocalSecretPathCount}`");
        builder.AppendLine();
        builder.AppendLine("## Commands");
        builder.AppendLine();
        builder.AppendLine($"- Build: `{report.BuildCommand}`");
        builder.AppendLine($"- Test: `{report.TestCommand}`");
        builder.AppendLine($"- P15: `{report.P15Command}`");
        builder.AppendLine($"- Runtime change gate: `{report.RuntimeChangeGateCommand}`");
        builder.AppendLine($"- Foundation gate: `{report.FoundationGateCommand}`");
        builder.AppendLine($"- Reproducibility check: `{report.ReproducibilityCheckCommand}`");
        builder.AppendLine();
        builder.AppendLine("## Expected Output");
        builder.AppendLine();
        builder.AppendLine(report.ExpectedOutputSummary);
        AppendBoolMap(builder, "Critical Report Coverage", report.CriticalReportCoverage);
        AppendBoolMap(builder, "Boundary Checks", report.BoundaryChecks);
        AppendCategoryMap(builder, "Git Status Categories", report.GitStatusCategories);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- 该检查不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`。");
        builder.AppendLine("- release candidate 通过也不允许 `PackingPolicy` 或 package output mutation。");
        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CategorizeGitStatus(
        IReadOnlyList<string> paths)
    {
        var categories = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["source code"] = [],
            ["tests"] = [],
            ["docs"] = [],
            ["generated reports"] = [],
            ["local config / secrets"] = [],
            ["model files"] = [],
            ["temporary files"] = [],
            ["other"] = []
        };

        foreach (var path in paths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            categories[ResolveCategory(path)].Add(path);
        }

        return categories.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveCategory(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();
        var extension = Path.GetExtension(normalized);

        if (IsSecretPath(lower))
        {
            return "local config / secrets";
        }

        if (LargeModelExtensions.Any(ext => string.Equals(extension, ext, StringComparison.OrdinalIgnoreCase)))
        {
            return "model files";
        }

        if (lower.EndsWith(".tmp", StringComparison.Ordinal)
            || lower.EndsWith(".temp", StringComparison.Ordinal)
            || lower.Contains("/tmp/", StringComparison.Ordinal)
            || lower.Contains("/temp/", StringComparison.Ordinal)
            || lower.Contains("trace", StringComparison.Ordinal) && lower.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return "temporary files";
        }

        if (lower.StartsWith("src/", StringComparison.Ordinal))
        {
            return "source code";
        }

        if (lower.StartsWith("tests/", StringComparison.Ordinal))
        {
            return "tests";
        }

        if (lower.StartsWith("docs/", StringComparison.Ordinal))
        {
            return "docs";
        }

        if (lower.StartsWith("foundation/", StringComparison.Ordinal)
            || lower.StartsWith("eval/", StringComparison.Ordinal)
            || lower.StartsWith("learning/", StringComparison.Ordinal)
            || lower.StartsWith("vector/", StringComparison.Ordinal)
            || lower.StartsWith("storage/", StringComparison.Ordinal))
        {
            return "generated reports";
        }

        return "other";
    }

    private static bool IsSecretPath(string lowerPath)
    {
        if (lowerPath.EndsWith(".sample.json", StringComparison.Ordinal)
            || lowerPath.EndsWith(".sample.env", StringComparison.Ordinal))
        {
            return false;
        }

        return LocalSecretIndicators.Any(lowerPath.Contains);
    }

    private static bool IsUseForRuntimeFalse(ContextCoreFoundationFreezeReport? foundationGate)
    {
        if (foundationGate is null)
        {
            return false;
        }

        var storageValues = foundationGate.StorageProviderReadiness.Values.Concat(foundationGate.VectorProviderReadiness.Values);
        return storageValues.All(static value =>
            !value.Contains("UseForRuntime=true", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("RuntimeSwitchAllowed=true", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<string>> ReadGitStatusPathsAsync(
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        var gitDirectory = Path.Combine(currentDirectory, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            return Array.Empty<string>();
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "status --porcelain=v1",
            WorkingDirectory = currentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParsePorcelainPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private static string ParsePorcelainPath(string line)
    {
        if (line.Length <= 3)
        {
            return string.Empty;
        }

        var path = line[3..].Trim();
        var renameMarker = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameMarker >= 0)
        {
            path = path[(renameMarker + 4)..];
        }

        return path.Trim('"');
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<P15ReportStatus> ReadP15StatusAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new P15ReportStatus(false, 0, 0, 0, "MissingReport");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var total = GetInt(root, "TotalSamples");
            var failed = GetInt(root, "FailedSamples");
            var invalid = GetInt(root, "InvalidSamples");
            return new P15ReportStatus(total > 0 && failed == 0 && invalid == 0, total, failed, invalid, "Loaded");
        }
        catch (JsonException)
        {
            return new P15ReportStatus(false, 0, 0, 0, "InvalidReport");
        }
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
    }

    private static void AddBlockedIfFalse(ICollection<string> blocked, bool condition, string reason)
    {
        if (!condition)
        {
            blocked.Add(reason);
        }
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("RuntimeSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByRuntimeSwitch;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByFormalRetrieval;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByPackageMutation;
        }

        if (blocked.Any(static reason => reason.Contains("FoundationReleaseCandidateGate", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByMissingFoundationGate;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeChangeGate", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByMissingRuntimeChangeGate;
        }

        if (blocked.Any(static reason => reason.Contains("P15", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByP15Gate;
        }

        if (blocked.Any(static reason => reason.Contains("LocalSecrets", StringComparison.OrdinalIgnoreCase)))
        {
            return FoundationReproducibilityRecommendations.BlockedByLocalSecret;
        }

        return FoundationReproducibilityRecommendations.BlockedByMissingCriticalReport;
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendBoolMap(StringBuilder builder, string title, IReadOnlyDictionary<string, bool> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }

    private static void AppendCategoryMap(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value.Count}`");
            foreach (var path in pair.Value.Take(20))
            {
                builder.AppendLine($"  - `{path}`");
            }
        }
    }
}
