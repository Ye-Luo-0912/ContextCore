using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Evaluation.Quality;

/// <summary>
/// 分层评测集构建器。
/// <para>
/// 输入声明文件（<see cref="DeclaredEvalSample"/> 数组），输出版本化数据集目录：
/// dataset.json（清单）+ train.jsonl / dev.jsonl / test.jsonl（每条含来源/期望证据/标注理由/版本）。
/// 划分确定：按 SampleId 的 SHA-256 稳定哈希落到 train/dev/test（默认 70/15/15），
/// 同一输入重复构建得到完全相同的划分。版本不可变：已存在时拒绝覆盖，除非 --force。
/// 覆盖门：全部固定维度（<see cref="EvalCoverageDimensions.All"/>）至少一条样本，否则构建失败。
/// </summary>
public static class EvalDatasetBuilder
{
    public const string Train = "train";
    public const string Dev = "dev";
    public const string Test = "test";
    public const string ManifestFile = "dataset.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>构建版本化数据集到指定目录。版本目录已存在且未 force 时抛异常。</summary>
    public static async Task<EvalDatasetManifest> BuildAsync(
        IReadOnlyList<DeclaredEvalSample> declarations,
        string version,
        string outDirectory,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);

        ValidateVersion(version);
        ValidateDeclarations(declarations);

        var versionDir = Path.Combine(outDirectory, version);
        if (Directory.Exists(versionDir) && !force)
        {
            throw new InvalidOperationException(
                $"版本目录已存在（{versionDir}），不可变数据集拒绝覆盖；如需重建请显式 --force。");
        }

        var samples = new List<VersionedEvalSample>(declarations.Count);
        foreach (var declared in declarations)
        {
            var split = AssignSplit(declared.SampleId, 70, 15, 15);
            samples.Add(new VersionedEvalSample
            {
                SampleId = declared.SampleId,
                Version = version,
                Split = split,
                Query = declared.Query,
                Source = declared.Source,
                AnnotationReason = declared.AnnotationReason,
                Evidence = declared.Evidence,
                CoverageDimensions = declared.CoverageDimensions,
                Metadata = declared.Metadata
            });
        }

        var coverageCounts = EvalCoverageDimensions.All.ToDictionary(
            dim => dim,
            dim => samples.Count(s => s.CoverageDimensions.Contains(dim, StringComparer.Ordinal)));
        var missing = EvalCoverageDimensions.All.Where(dim => coverageCounts[dim] == 0).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"覆盖门未通过，以下维度缺少样本：{string.Join(", ", missing)}。");
        }

        var manifest = new EvalDatasetManifest
        {
            Version = version,
            SchemaVersion = "1",
            HashAlgorithm = "sha256",
            TrainRatio = 70,
            DevRatio = 15,
            TestRatio = 15,
            SampleCount = samples.Count,
            SplitCounts = new Dictionary<string, int>
            {
                [Train] = samples.Count(s => s.Split == Train),
                [Dev] = samples.Count(s => s.Split == Dev),
                [Test] = samples.Count(s => s.Split == Test)
            },
            CoverageCounts = coverageCounts,
            CoverageComplete = missing.Length == 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(versionDir);
        await WriteSplitAsync(versionDir, Train, samples, cancellationToken).ConfigureAwait(false);
        await WriteSplitAsync(versionDir, Dev, samples, cancellationToken).ConfigureAwait(false);
        await WriteSplitAsync(versionDir, Test, samples, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(versionDir, ManifestFile),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>校验已构建的数据集目录：清单可读、计数一致、样本可追溯、覆盖完整。</summary>
    public static async Task<EvalDatasetVerifyResult> VerifyAsync(
        string versionDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDir);
        var errors = new List<string>();

        var manifestPath = Path.Combine(versionDir, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            return new EvalDatasetVerifyResult { Ok = false, Errors = [$"缺少清单：{manifestPath}"] };
        }

        EvalDatasetManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<EvalDatasetManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new EvalDatasetVerifyResult { Ok = false, Errors = [$"清单无法解析：{ex.Message}"] };
        }
        if (manifest is null)
        {
            return new EvalDatasetVerifyResult { Ok = false, Errors = ["清单为空。"] };
        }

        var all = new List<VersionedEvalSample>();
        foreach (var split in new[] { Train, Dev, Test })
        {
            var path = Path.Combine(versionDir, $"{split}.jsonl");
            if (!File.Exists(path))
            {
                errors.Add($"缺少划分文件：{path}");
                continue;
            }
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var sample = JsonSerializer.Deserialize<VersionedEvalSample>(line);
                if (sample is null)
                {
                    errors.Add($"{split}.jsonl 中存在无法解析的行。");
                    continue;
                }
                all.Add(sample);
                if (sample.Split != split)
                {
                    errors.Add($"样本 {sample.SampleId} 的 Split 字段与所在文件不一致。");
                }
                if (string.IsNullOrWhiteSpace(sample.Source) || string.IsNullOrWhiteSpace(sample.AnnotationReason))
                {
                    errors.Add($"样本 {sample.SampleId} 缺少来源或标注理由。");
                }
                if (string.IsNullOrWhiteSpace(sample.Version))
                {
                    errors.Add($"样本 {sample.SampleId} 缺少版本。");
                }
            }
        }

        var duplicateIds = all.GroupBy(s => s.SampleId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            errors.Add($"样本 ID 重复：{string.Join(", ", duplicateIds)}。");
        }

        var coverageCounts = EvalCoverageDimensions.All.ToDictionary(
            dim => dim,
            dim => all.Count(s => s.CoverageDimensions.Contains(dim, StringComparer.Ordinal)));
        var missing = EvalCoverageDimensions.All.Where(dim => coverageCounts[dim] == 0).ToArray();
        if (missing.Length > 0)
        {
            errors.Add($"覆盖不完整，缺少维度：{string.Join(", ", missing)}。");
        }

        if (all.Count != manifest.SampleCount)
        {
            errors.Add($"样本数不一致：清单 {manifest.SampleCount}，实际 {all.Count}。");
        }

        return new EvalDatasetVerifyResult
        {
            Ok = errors.Count == 0,
            Manifest = manifest,
            Errors = errors
        };
    }

    /// <summary>读取声明文件（JSON 数组）。</summary>
    public static async Task<IReadOnlyList<DeclaredEvalSample>> LoadDeclarationsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var samples = JsonSerializer.Deserialize<List<DeclaredEvalSample>>(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return samples ?? throw new InvalidOperationException($"声明文件为空或无法解析：{path}");
    }

    private static async Task WriteSplitAsync(
        string versionDir,
        string split,
        IReadOnlyList<VersionedEvalSample> samples,
        CancellationToken cancellationToken)
    {
        var lines = samples
            .Where(s => s.Split == split)
            .OrderBy(s => s.SampleId, StringComparer.Ordinal)
            .Select(s => JsonSerializer.Serialize(s));
        await File.WriteAllLinesAsync(
            Path.Combine(versionDir, $"{split}.jsonl"),
            lines,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateVersion(string version)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^v\d+$"))
        {
            throw new ArgumentException("版本号必须是 v 加数字（如 v1）。", nameof(version));
        }
    }

    private static void ValidateDeclarations(IReadOnlyList<DeclaredEvalSample> declarations)
    {
        if (declarations.Count == 0)
        {
            throw new ArgumentException("声明样本不能为空。", nameof(declarations));
        }
        var duplicate = declarations.GroupBy(d => d.SampleId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"样本 ID 重复：{duplicate.Key}。", nameof(declarations));
        }
        foreach (var sample in declarations)
        {
            if (string.IsNullOrWhiteSpace(sample.Query))
            {
                throw new ArgumentException($"样本 {sample.SampleId} 的 query 为空。", nameof(declarations));
            }
            if (sample.Evidence.RequiredEvidenceIds.Count == 0
                && sample.Evidence.RelevantEvidenceIds.Count == 0
                && sample.Evidence.ForbiddenExcludedIds.Count == 0)
            {
                throw new ArgumentException($"样本 {sample.SampleId} 未声明任何期望证据（不可评分）。", nameof(declarations));
            }
            if (sample.CoverageDimensions.Count == 0)
            {
                throw new ArgumentException($"样本 {sample.SampleId} 未声明覆盖维度。", nameof(declarations));
            }
            var unknown = sample.CoverageDimensions
                .Where(dim => !EvalCoverageDimensions.All.Contains(dim, StringComparer.Ordinal))
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException(
                    $"样本 {sample.SampleId} 含未知覆盖维度：{string.Join(", ", unknown)}。", nameof(declarations));
            }
        }
    }

    /// <summary>
    /// 稳定划分：SHA-256(SampleId) 前 8 字节 → uint64 → 按比例落桶。
    /// 同一 SampleId 永远落同一划分，跨版本稳定。
    /// </summary>
    internal static string AssignSplit(string sampleId, int trainRatio, int devRatio, int testRatio)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sampleId));
        var value = BitConverter.ToUInt64(hash, 0);
        var bucket = (int)(value % 100);
        if (bucket < trainRatio)
        {
            return Train;
        }
        if (bucket < trainRatio + devRatio)
        {
            return Dev;
        }
        if (bucket < trainRatio + devRatio + testRatio)
        {
            return Test;
        }
        return Train; // 比例和不为 100 时兜底，正常配置不会走到。
    }
}
