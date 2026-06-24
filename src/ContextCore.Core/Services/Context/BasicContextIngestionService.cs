using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 负责对 <see cref="ContextItem"/> 进行规范化并持久化到 <see cref="IContextStore"/> 的基础摄入服务。
/// </summary>
public sealed class BasicContextIngestionService
{
    private readonly IContextStore _store;

    public BasicContextIngestionService(IContextStore store)
    {
        _store = store;
    }

    /// <summary>规范化并保存一个上下文条目，若未提供 ID 则自动生成。</summary>
    public async Task<ContextItem> IngestAsync(
        ContextItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.WorkspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(item.CollectionId))
        {
            throw new ArgumentException("CollectionId is required.", nameof(item));
        }

        var now = DateTimeOffset.UtcNow;
        var normalized = new ContextItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            Refs = item.Refs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            Importance = item.Importance,
            Version = item.Version <= 0 ? 1 : item.Version,
            Checksum = string.IsNullOrWhiteSpace(item.Checksum)
                ? ComputeChecksum(item.Content)
                : item.Checksum,
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };

        await _store.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);

        return normalized;
    }

    /// <summary>计算内容的 SHA-256 十六进制校验和（小写）。</summary>
    public static string ComputeChecksum(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
