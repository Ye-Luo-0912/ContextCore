using System.Text.RegularExpressions;

namespace ContextCore.Abstractions;

public static partial class PathHygiene
{
    private static string? _repoRoot;

    public static string RepoRoot
    {
        get
        {
            _repoRoot ??= ResolveRepoRoot();
            return _repoRoot;
        }
        set => _repoRoot = value;
    }

    private static string ResolveRepoRoot()
    {
        var current = Environment.CurrentDirectory;
        var dir = new DirectoryInfo(current);
        while (dir != null)
        {
            if (dir.EnumerateFiles(".git").Any() ||
                dir.EnumerateFiles("ContextCore.sln").Any())
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return current;
    }

    public static string ToRepoRelativePath(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return string.Empty;

        var normalized = NormalizePathSeparators(absolutePath);
        var repoRoot = NormalizePathSeparators(RepoRoot);

        if (normalized.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[repoRoot.Length..].TrimStart('/');
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfileNormalized = NormalizePathSeparators(userProfile);
        if (normalized.StartsWith(userProfileNormalized, StringComparison.OrdinalIgnoreCase))
        {
            var rest = normalized[userProfileNormalized.Length..].TrimStart('/');
            return $"<user-profile>/{rest}";
        }

        if (normalized.Contains("/.contextcore/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = normalized.IndexOf("/.contextcore/", StringComparison.OrdinalIgnoreCase);
            return $"<user-profile>{normalized[idx..]}";
        }

        return $"<local-path>/{Path.GetFileName(normalized)}";
    }

    public static string ToRepoRelativePathOrPlaceholder(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return string.Empty;

        var normalized = NormalizePathSeparators(absolutePath);
        var repoRoot = NormalizePathSeparators(RepoRoot);

        if (normalized.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[repoRoot.Length..].TrimStart('/');
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfileNormalized = NormalizePathSeparators(userProfile);
        if (normalized.StartsWith(userProfileNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return "<user-profile>/...";
        }

        if (normalized.Contains("AppData", StringComparison.OrdinalIgnoreCase))
        {
            return "<appdata>/...";
        }

        if (normalized.Contains("/.contextcore/", StringComparison.OrdinalIgnoreCase))
        {
            return "<user-profile>/.contextcore/...";
        }

        return "<local-path>/...";
    }

    public static string NormalizeReportPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = NormalizePathSeparators(path);
        var repoRoot = NormalizePathSeparators(RepoRoot);

        if (normalized.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[repoRoot.Length..].TrimStart('/');
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }

        var modelPaths = new[] { "model.onnx", "model_quantized.onnx", "vocab.txt", "tokenizer.json" };
        foreach (var mp in modelPaths)
        {
            if (normalized.EndsWith(mp, StringComparison.OrdinalIgnoreCase))
                return $"<local-model-path>/{mp}";
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfileNormalized = NormalizePathSeparators(userProfile);
        if (normalized.StartsWith(userProfileNormalized, StringComparison.OrdinalIgnoreCase))
            return $"<user-profile>{normalized[userProfileNormalized.Length..]}";

        return ToRepoRelativePathOrPlaceholder(path);
    }

    public static T NormalizeReportPaths<T>(T report) where T : class
    {
        ArgumentNullException.ThrowIfNull(report);
        var type = report.GetType();
        var stringProps = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite);

        foreach (var prop in stringProps)
        {
            if (!IsPathCandidate(prop.Name))
                continue;

            var value = (string?)prop.GetValue(report);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (LooksLikeAbsolutePath(value))
            {
                prop.SetValue(report, NormalizeReportPath(value));
            }
        }

        return report;
    }

    private static bool IsPathCandidate(string propertyName)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SourceFile", "ModelPath", "TokenizerPath", "ContextsRootPath",
            "BaselineReportPath", "ExtendedReportPath", "RepositoryRoot",
            "ReportPath", "ArtifactPath", "OutputPath", "InputPath",
            "SourceAuditPath", "LastEvalReportPath", "RootPath",
            "ModelFilePath", "VocabPath", "DataRoot", "TraceRoot",
            "ResolvedPath", "RelativePath"
        };

        if (candidates.Contains(propertyName))
            return true;

        if (propertyName.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
            propertyName.EndsWith("File", StringComparison.OrdinalIgnoreCase) ||
            propertyName.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) ||
            propertyName.EndsWith("Root", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool LooksLikeAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("D:\\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/home/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase))
            return true;

        // 匹配 Windows 绝对路径：驱动器+反斜杠+至少一个路径段
        if (value.Length >= 4 && value[1] == ':' && value[2] == '\\' &&
            char.IsLetter(value[0]) &&
            value[3] is not '\r' and not '\n' and not '\0')
            return true;

        return false;
    }

    public static bool IsAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            return Path.IsPathFullyQualified(value) && Path.GetFullPath(value).Equals(value, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasAbsolutePathLeak(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return AbsolutePathPattern().IsMatch(text);
    }

    public static IReadOnlyList<string> FindAbsolutePathLeaks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var matches = AbsolutePathPattern().Matches(text);
        return matches.Select(m => m.Value.Trim()).Distinct().ToArray();
    }

    private static string NormalizePathSeparators(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>匹配 Windows 驱动盘符绝对路径、Unix /home/ 和 /Users/ 路径。</summary>
    private const string AbsolutePathStrictPattern =
        @"(?:" +
        @"[A-Za-z]:\\(?:Users|Models|context-core|src|eval|learning|vector|storage|foundation|service|docs|tests|scripts|\.git)[^""'\s<>|]*|" +
        @"/home/[^""'\s<>|]+|" +
        @"/Users/[^""'\s<>|]+" +
        @")";

    public static PathHygieneScanReport ScanGeneratedReports(string scanRoot)
    {
        var entries = new List<PathHygieneScanEntry>();
        var totalFiles = 0;
        var infectedFiles = 0;

        if (!Directory.Exists(scanRoot))
            return new PathHygieneScanReport
            {
                ScanRoot = scanRoot,
                TotalFiles = 0,
                InfectedFiles = 0,
                Entries = [],
                Passed = true
            };

        var searchDirs = new[] { "eval", "learning", "vector", "storage", "foundation", "service" };
        foreach (var dir in searchDirs)
        {
            var fullDir = Path.Combine(scanRoot, dir);
            if (!Directory.Exists(fullDir))
                continue;

            foreach (var file in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("generated-artifact-path-hygiene-", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".json" && ext != ".md")
                    continue;

                totalFiles++;
                var content = File.ReadAllText(file);
                var leaks = FindAbsolutePathLeaks(content);
                if (leaks.Count > 0)
                {
                    infectedFiles++;
                    var relativePath = ToRepoRelativePath(file);
                    entries.Add(new PathHygieneScanEntry
                    {
                        FilePath = relativePath,
                        LeakedPaths = leaks.ToList()
                    });
                }
            }
        }

        return new PathHygieneScanReport
        {
            ScanRoot = scanRoot,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalFiles = totalFiles,
            InfectedFiles = infectedFiles,
            Entries = entries,
            Passed = infectedFiles == 0
        };
    }

    [GeneratedRegex(AbsolutePathStrictPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, 5000)]
    private static partial Regex AbsolutePathPattern();
}

public sealed class PathHygieneScanEntry
{
    public string FilePath { get; init; } = string.Empty;
    public IReadOnlyList<string> LeakedPaths { get; init; } = Array.Empty<string>();
}

public sealed class PathHygieneScanReport
{
    public string ScanRoot { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public int TotalFiles { get; init; }
    public int InfectedFiles { get; init; }
    public IReadOnlyList<PathHygieneScanEntry> Entries { get; init; } = Array.Empty<PathHygieneScanEntry>();
    public bool Passed { get; init; }
    public string? Recommendation => Passed ? "ReadyForNextPhase" : "BlockedByPathLeaks";
    public IReadOnlyList<string> RemediationHints { get; init; } = Array.Empty<string>();
}

public sealed class PathHygieneGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public bool Passed { get; init; }
    public int InfectedFiles { get; init; }
    public int TotalFiles { get; init; }
    public IReadOnlyList<string> FailedConditions { get; init; } = Array.Empty<string>();
    public string Recommendation { get; init; } = string.Empty;
    public string? AuditReportPath { get; init; }
}
