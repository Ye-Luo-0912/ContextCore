namespace ContextCore.Tests;

/// <summary>Shared test utility for resolving repository file paths, including files moved to subdirectories during OPT-004.</summary>
public static class TestRepoFileResolver
{
    /// <summary>Resolves a repository-relative file path by walking up from the working directory. Falls back to recursive subdirectory search when the exact path is not found (supports OPT-004 directory reorganization).</summary>
    public static string Resolve(params string[] parts)
    {
        // Exact path search (original behavior)
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        // Fallback: search subdirectories for files moved during OPT-004
        var repoRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (repoRoot is not null && !Directory.Exists(Path.Combine(repoRoot.FullName, "src")))
        {
            repoRoot = repoRoot.Parent;
        }
        if (repoRoot is not null)
        {
            var baseDir = repoRoot.FullName;
            for (int i = 0; i < parts.Length - 1; i++)
                baseDir = Path.Combine(baseDir, parts[i]);
            var fileName = parts[^1];
            if (Directory.Exists(baseDir))
            {
                var found = Directory.EnumerateFiles(baseDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null)
                    return found;
            }
        }

        throw new FileNotFoundException("Could not resolve repository file", Path.Combine(parts));
    }
}
