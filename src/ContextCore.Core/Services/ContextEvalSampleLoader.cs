using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 从项目内 eval/contexts 目录加载上下文评测样本。
/// 读取器只处理 seed*.json，避免把 corpus 语料误当成 query 金标样本。
/// </summary>
public sealed class ContextEvalSampleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<ContextEvalSampleLoadResult> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("评测样本根目录不能为空。", nameof(rootDirectory));
        }

        if (!Directory.Exists(rootDirectory))
        {
            return new ContextEvalSampleLoadResult();
        }

        var files = Directory.EnumerateFiles(rootDirectory, "seed*.json", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var samples = new List<ContextEvalSample>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<IReadOnlyList<ContextEvalSample>>(json, JsonOptions)
                ?? [];
            samples.AddRange(loaded.Select(sample => Normalize(sample, file)));
        }

        return new ContextEvalSampleLoadResult
        {
            Samples = samples,
            ModeCounts = samples
                .GroupBy(sample => sample.Mode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Files = files
        };
    }

    private static ContextEvalSample Normalize(ContextEvalSample sample, string file)
    {
        var metadata = new Dictionary<string, string>(sample.Metadata)
        {
            ["sourceFile"] = file
        };

        return new ContextEvalSample
        {
            Id = string.IsNullOrWhiteSpace(sample.Id)
                ? Path.GetFileNameWithoutExtension(file) + ":" + Guid.NewGuid().ToString("N")
                : sample.Id,
            Query = sample.Query,
            Mode = sample.Mode,
            MustHit = [.. sample.MustHit],
            MustNotHit = [.. sample.MustNotHit],
            ExpectedScopes = [.. sample.ExpectedScopes],
            ExpectedEntities = [.. sample.ExpectedEntities],
            ExpectedConstraints = [.. sample.ExpectedConstraints],
            ExpectedUncertainties = [.. sample.ExpectedUncertainties],
            GoldenNotes = sample.GoldenNotes,
            Metadata = metadata
        };
    }
}
