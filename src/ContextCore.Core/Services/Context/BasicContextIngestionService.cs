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
    private readonly IRelationProjector? _relationProjector;
    private readonly IRelationStore? _relationStore;
    private readonly IRelationProjectionWriter? _projectionWriter;

    public BasicContextIngestionService(
        IContextStore store,
        IRelationProjector? relationProjector = null,
        IRelationStore? relationStore = null,
        IRelationProjectionWriter? projectionWriter = null)
    {
        _store = store;
        _relationProjector = relationProjector;
        _relationStore = relationStore;
        _projectionWriter = projectionWriter;
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

        if (_relationProjector is not null && _relationStore is not null)
        {
            var ingestRelations = _relationProjector.ProjectForIngest(normalized);
            // GRAPH-09：Ingest reconcile — 新增需要的边，删除已经移除的 refs 边。
            // 4.4：通过 IRelationProjectionWriter 写入，若未注入则回退到 BatchUpsertAsync。
            await ReconcileIngestRelationsAsync(normalized, ingestRelations, cancellationToken).ConfigureAwait(false);
        }

        return normalized;
    }

    /// <summary>计算内容的 SHA-256 十六进制校验和（小写）。</summary>
    public static string ComputeChecksum(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// GRAPH-09：Ingest reconcile — 新增需要的 related_to 边，删除已经移除的 refs 边。
    /// 仅清理由 ingest 生产的 related_to 边（Provenance="ingest"），不影响其他 projector 生产的边。
    /// </summary>
    private async Task ReconcileIngestRelationsAsync(
        ContextItem item,
        IReadOnlyList<ContextRelation> newRelations,
        CancellationToken cancellationToken)
    {
        if (newRelations.Count > 0)
        {
            // 4.4：通过 IRelationProjectionWriter 统一写入边界；若未注入则回退到 BatchUpsertAsync。
            if (_projectionWriter is not null)
            {
                await _projectionWriter.WriteAsync(newRelations, "ingest", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _relationStore!.BatchUpsertAsync(newRelations, cancellationToken).ConfigureAwait(false);
            }
        }

        // 查询该条目现有的所有 related_to 出边
        var existingRelations = await _relationStore!.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.Id,
            RelationType = ContextRelationTypes.RelatedTo,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var newTargetIds = newRelations
            .Select(static r => r.TargetId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // P3-01：只删除由 ingest 投影器生产的边（Provenance="ingest"），保留人工或其他 projector 产生的边。
        // 这避免误删 compression/promotion/lifecycle-review 等流程产生的 related_to 边。
        foreach (var existing in existingRelations)
        {
            if (!newTargetIds.Contains(existing.TargetId)
                && string.Equals(existing.Provenance, "ingest", StringComparison.OrdinalIgnoreCase))
            {
                await _relationStore.DeleteAsync(
                    item.WorkspaceId,
                    item.CollectionId,
                    existing.Id,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
