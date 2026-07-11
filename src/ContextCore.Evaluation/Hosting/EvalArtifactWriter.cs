using System.Text.Json;

namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// P3-02：评测产物写入器。统一"写 JSON + 写 Markdown"的重复模式，
/// 替代每个 Runner 各自调用 File.WriteAllText 的散落代码。
/// </summary>
public static class EvalArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>写入 JSON 产物并返回完整路径。</summary>
    public static async Task<string> WriteJsonAsync<T>(T report, string outputDirectory, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var fullPath = Path.Combine(outputDirectory, fileName);
        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
        return fullPath;
    }

    /// <summary>写入 Markdown 产物并返回完整路径。</summary>
    public static async Task<string> WriteMarkdownAsync(string markdown, string outputDirectory, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var fullPath = Path.Combine(outputDirectory, fileName);
        await File.WriteAllTextAsync(fullPath, markdown, cancellationToken).ConfigureAwait(false);
        return fullPath;
    }

    /// <summary>写入 JSON + Markdown 产物对。</summary>
    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync<T>(
        T report,
        string markdown,
        string outputDirectory,
        string jsonFileName,
        string markdownFileName,
        CancellationToken cancellationToken)
    {
        var jsonPath = await WriteJsonAsync(report, outputDirectory, jsonFileName, cancellationToken).ConfigureAwait(false);
        var markdownPath = await WriteMarkdownAsync(markdown, outputDirectory, markdownFileName, cancellationToken).ConfigureAwait(false);
        return (jsonPath, markdownPath);
    }
}
