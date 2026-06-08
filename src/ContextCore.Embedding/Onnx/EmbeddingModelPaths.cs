namespace ContextCore.Embedding;

/// <summary>解析项目内置 embedding 模型文件路径。</summary>
public static class EmbeddingModelPaths
{
    public const string DefaultModelName = "bge-small-zh-v1.5";
    public const string EnglishModelName = "all-MiniLM-L6-v2";
    public const string DefaultModelFileName = "model_quantized.onnx";
    public const string DefaultVocabularyFileName = "vocab.txt";

    private static readonly string[] DefaultModelRelativeParts =
    [
        "src",
        "ContextCore.Embedding",
        "Models",
        DefaultModelName,
        "onnx",
        DefaultModelFileName
    ];

    private static readonly string[] DefaultVocabularyRelativeParts =
    [
        "src",
        "ContextCore.Embedding",
        "Models",
        DefaultModelName,
        DefaultVocabularyFileName
    ];

    public static string ResolveModelPath(string? configuredPath = null)
    {
        return ResolveFirstExisting(GetModelPathCandidates(configuredPath));
    }

    public static string ResolveVocabularyPath(string? configuredPath, string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        return ResolveFirstExisting(GetVocabularyPathCandidates(configuredPath, modelPath));
    }

    public static IReadOnlyList<string> GetModelPathCandidates(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return BuildCandidates(configuredPath).ToArray();
        }

        return BuildCandidates(Path.Combine(DefaultModelRelativeParts)).ToArray();
    }

    public static IReadOnlyList<string> GetVocabularyPathCandidates(
        string? configuredPath,
        string modelPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return BuildCandidates(configuredPath).ToArray();
        }

        var candidates = new List<string>();
        var modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath));
        var modelRoot = modelDirectory is null ? null : Directory.GetParent(modelDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(modelRoot))
        {
            candidates.Add(Path.Combine(modelRoot, DefaultVocabularyFileName));
        }

        candidates.AddRange(BuildCandidates(Path.Combine(DefaultVocabularyRelativeParts)));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveFirstExisting(IReadOnlyList<string> candidates)
    {
        var existing = candidates.FirstOrDefault(File.Exists);
        return existing ?? candidates.First();
    }

    private static IEnumerable<string> BuildCandidates(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        foreach (var root in GetSearchRoots())
        {
            yield return Path.GetFullPath(Path.Combine(root, path));
        }
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var roots = new List<string>
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var seed in roots.ToArray())
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                roots.Add(directory.FullName);
                directory = directory.Parent;
            }
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
