using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>路由意图数据集的加载状态。</summary>
public enum RouterIntentDatasetStatus
{
    /// <summary>文件不存在。</summary>
    NotFound,

    /// <summary>成功加载，无错误。</summary>
    Loaded,

    /// <summary>已加载但存在 JSON 解析错误行。</summary>
    LoadedWithErrors,

    /// <summary>文件访问异常（IO 错误等）。</summary>
    AccessError
}

/// <summary>路由意图数据集加载结果，包含可观测性信息。</summary>
public sealed class RouterIntentDatasetLoadResult
{
    /// <summary>解析出的训练样本列表。</summary>
    public required IReadOnlyList<ContextPolicyFeatureExample> Examples { get; init; }

    /// <summary>加载状态。</summary>
    public required RouterIntentDatasetStatus Status { get; init; }

    /// <summary>数据集文件的绝对路径。</summary>
    public string? FilePath { get; init; }

    /// <summary>文件内容的 SHA-256 哈希（小写十六进制）；文件不存在或访问失败时为 null。</summary>
    public string? ContentHash { get; init; }

    /// <summary>文件中的非空行总数。</summary>
    public int TotalLines { get; init; }

    /// <summary>成功解析的样本数。</summary>
    public int ValidLines { get; init; }

    /// <summary>JSON 解析失败或反序列化为 null 的行数。</summary>
    public int ErrorCount { get; init; }

    /// <summary>文件最后修改时间（UTC）；文件不存在时为 null。</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>数据集版本标识（内容哈希前 8 字符）；文件不存在时为 null。</summary>
    public string? Version => ContentHash is null ? null : ContentHash[..8];

    /// <summary>是否处于降级状态（文件缺失、访问失败或全部行解析失败）。</summary>
    public bool IsDegraded =>
        Status is RouterIntentDatasetStatus.NotFound or RouterIntentDatasetStatus.AccessError
        || (Status == RouterIntentDatasetStatus.LoadedWithErrors && ValidLines == 0);
}

/// <summary>
/// 路由意图数据集提供者接口，封装数据集加载逻辑并提供可观测性。
/// 替代原先 RouterIntentShadowService 中静默吞错的 private static ReadTrainingExamples 方法。
/// </summary>
public interface IRouterIntentDatasetProvider
{
    /// <summary>加载数据集并返回包含可观测性信息的结果。</summary>
    RouterIntentDatasetLoadResult Load();
}

/// <summary>
/// 基于文件系统的默认路由意图数据集提供者。
/// 从指定路径读取 JSONL 格式的训练样本，记录解析错误而非静默吞没。
/// </summary>
public sealed class FileRouterIntentDatasetProvider : IRouterIntentDatasetProvider
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <param name="filePath">
    /// 数据集文件路径。为 null 时使用默认相对路径 <c>learning/features/router-intent-examples.jsonl</c>。
    /// </param>
    public FileRouterIntentDatasetProvider(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            LearningDatasetQualityReportBuilder.DefaultFeatureDirectory,
            LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
    }

    public RouterIntentDatasetLoadResult Load()
    {
        string absolutePath;
        try
        {
            absolutePath = Path.GetFullPath(_filePath);
        }
        catch (Exception)
        {
            absolutePath = _filePath;
        }

        if (!File.Exists(absolutePath))
        {
            return new RouterIntentDatasetLoadResult
            {
                Examples = Array.Empty<ContextPolicyFeatureExample>(),
                Status = RouterIntentDatasetStatus.NotFound,
                FilePath = absolutePath,
                TotalLines = 0,
                ValidLines = 0,
                ErrorCount = 0
            };
        }

        try
        {
            return LoadFromFile(absolutePath);
        }
        catch (IOException)
        {
            return new RouterIntentDatasetLoadResult
            {
                Examples = Array.Empty<ContextPolicyFeatureExample>(),
                Status = RouterIntentDatasetStatus.AccessError,
                FilePath = absolutePath,
                TotalLines = 0,
                ValidLines = 0,
                ErrorCount = 0
            };
        }
    }

    private static RouterIntentDatasetLoadResult LoadFromFile(string absolutePath)
    {
        var content = File.ReadAllText(absolutePath);
        var hash = ComputeSha256(content);
        var lastModified = File.GetLastWriteTimeUtc(absolutePath);

        var examples = new List<ContextPolicyFeatureExample>();
        var totalLines = 0;
        var errorCount = 0;

        foreach (var line in File.ReadLines(absolutePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            totalLines++;

            try
            {
                var example = JsonSerializer.Deserialize<ContextPolicyFeatureExample>(line, JsonOptions);
                if (example is not null)
                {
                    examples.Add(example);
                }
                else
                {
                    errorCount++;
                }
            }
            catch (JsonException)
            {
                errorCount++;
            }
        }

        return new RouterIntentDatasetLoadResult
        {
            Examples = examples,
            Status = errorCount > 0 ? RouterIntentDatasetStatus.LoadedWithErrors : RouterIntentDatasetStatus.Loaded,
            FilePath = absolutePath,
            ContentHash = hash,
            TotalLines = totalLines,
            ValidLines = examples.Count,
            ErrorCount = errorCount,
            LastModified = lastModified
        };
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
