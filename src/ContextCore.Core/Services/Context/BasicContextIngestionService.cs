using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 负责对 <see cref="ContextItem"/> 进行规范化并持久化到 <see cref="IContextStore"/> 的基础摄入服务。
/// </summary>
/// <remarks>
/// P0-3：当注入的 <see cref="IContextStore"/> 实现 <see cref="ITransactionalContextStore"/>、
/// <see cref="IRelationStore"/> 实现 <see cref="ITransactionalRelationStore"/>、
/// <see cref="IRelationProjectionWriter"/> 实现 <see cref="ITransactionalRelationProjectionWriter"/>、
/// 且注入了 <see cref="IWriteTransactionScopeFactory"/> 时，IngestAsync 自动走事务路径——
/// 将 ContextStore 写入、RelationProjectionWriter 写入、RelationStore 查询与删除全部包裹在单个事务作用域中，
/// 任一步失败则整体回滚，避免出现"item 已写入但 related_to 边未写入/未删除"的脏数据。
/// 不满足任一条件时回退到原有无事务路径（行为不变）。
/// P5/P6：摄取阶段持久化 content_hash（SHA-256）与 content_token_cost（精确 token 数）到 Metadata，
/// Provider 召回时直接读取、跳过在线 SHA-256 + tokenizer 调用。
/// </remarks>
public sealed class BasicContextIngestionService
{
    private readonly IContextStore _store;
    private readonly IRelationProjector? _relationProjector;
    private readonly IRelationStore? _relationStore;
    private readonly IRelationProjectionWriter? _projectionWriter;
    private readonly IWriteTransactionScopeFactory? _transactionScopeFactory;
    private readonly IContextTokenizerResolver? _tokenizerResolver;
    private readonly string? _tokenizerModelName;

    public BasicContextIngestionService(
        IContextStore store,
        IRelationProjector? relationProjector = null,
        IRelationStore? relationStore = null,
        IRelationProjectionWriter? projectionWriter = null,
        IWriteTransactionScopeFactory? transactionScopeFactory = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
    {
        _store = store;
        _relationProjector = relationProjector;
        _relationStore = relationStore;
        _projectionWriter = projectionWriter;
        _transactionScopeFactory = transactionScopeFactory;
        _tokenizerResolver = tokenizerResolver;
        _tokenizerModelName = tokenizerModelName;
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
        // P6：摄取阶段计算 content_hash（SHA-256 小写 hex），与 ContextItem.Checksum 一致。
        // Provider 召回时从 Metadata["__content_hash"] 读取，跳过在线 SHA-256 重复计算。
        var contentHash = string.IsNullOrWhiteSpace(item.Checksum)
            ? ComputeChecksum(item.Content)
            : item.Checksum;

        // P5：摄取阶段计算精确 token cost（若注入了 tokenizer），Provider 召回时直接读取跳过在线 tokenize。
        // 未注入 tokenizer 时 content_token_cost 不写入 Metadata，Provider 回退到 fail-fast（R29 WP-D-3 不变）。
        int? contentTokenCost = null;
        if (_tokenizerResolver is not null)
        {
            var estimate = _tokenizerResolver.Estimate(item.Content, _tokenizerModelName);
            contentTokenCost = Math.Max(0, estimate.TokenCount);
        }

        var metadata = new Dictionary<string, string>(item.Metadata)
        {
            [ContentMetadataKeys.ContentHash] = contentHash
        };
        if (contentTokenCost.HasValue)
        {
            metadata[ContentMetadataKeys.ContentTokenCost] = contentTokenCost.Value.ToString(CultureInfo.InvariantCulture);
        }

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
            Metadata = metadata,
            Importance = item.Importance,
            Version = item.Version <= 0 ? 1 : item.Version,
            Checksum = contentHash,
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };

        // P0-3：若所有参与 store 都实现事务能力接口且工厂可用，走事务路径；否则走原有无事务路径。
        if (CanUseTransactionPath())
        {
            await IngestWithTransactionAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);

            if (_relationProjector is not null && _relationStore is not null)
            {
                var ingestRelations = _relationProjector.ProjectForIngest(normalized);
                // GRAPH-09：Ingest reconcile — 新增需要的边，删除已经移除的 refs 边。
                // 4.4：通过 IRelationProjectionWriter 写入，若未注入则回退到 BatchUpsertAsync。
                await ReconcileIngestRelationsAsync(normalized, ingestRelations, cancellationToken).ConfigureAwait(false);
            }
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
    /// P0-3：检测注入的 store 是否全部支持事务能力，且事务作用域工厂可用。
    /// 任一 store 未实现对应可选接口时返回 false，回退到原有无事务路径。
    /// </summary>
    private bool CanUseTransactionPath()
    {
        if (_transactionScopeFactory is null) return false;
        if (_store is not ITransactionalContextStore) return false;
        if (_relationProjector is null || _relationStore is null) return false;
        // 需要 ITransactionalRelationStore 来执行事务内的 QueryAsync 与 DeleteAsync。
        // ITransactionalRelationProjectionWriter 仅在 ProjectionWriter 注入时才需要。
        if (_relationStore is not ITransactionalRelationStore) return false;
        if (_projectionWriter is not null && _projectionWriter is not ITransactionalRelationProjectionWriter) return false;
        return true;
    }

    /// <summary>
    /// P0-3：在事务作用域内执行 Ingest。任一步失败则整体回滚；
    /// 全部成功则提交事务。事务作用域通过 await using 确保异常路径也会 Dispose（触发 Rollback）。
    /// </summary>
    private async Task IngestWithTransactionAsync(ContextItem normalized, CancellationToken cancellationToken)
    {
        var txStore = (ITransactionalContextStore)_store;
        var txRelationStore = (ITransactionalRelationStore)_relationStore!;
        var ingestRelations = _relationProjector!.ProjectForIngest(normalized);

        await using var scope = await _transactionScopeFactory!.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await txStore.SaveAsync(normalized, scope, cancellationToken).ConfigureAwait(false);

            // P1-3：与 ReconcileIngestRelationsAsync 保持一致——无 writer 时跳过整段关系 reconcile，
            // 不删除现有 ingest-provenance 边。CanUseTransactionPath 已保证 _projectionWriter 非空时
            // 必为 ITransactionalRelationProjectionWriter，因此 _projectionWriter 非空即可安全转型。
            if (ingestRelations.Count > 0 && _projectionWriter is not null)
            {
                var txProjectionWriter = (ITransactionalRelationProjectionWriter)_projectionWriter;
                await txProjectionWriter.WriteAsync(ingestRelations, "ingest", scope, cancellationToken).ConfigureAwait(false);
            }
            else if (_projectionWriter is null)
            {
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // 查询该条目现有的所有 related_to 出边——共享事务视图，避免读到其他事务未提交数据
            var existingRelations = await txRelationStore.QueryAsync(new ContextRelationQuery
            {
                WorkspaceId = normalized.WorkspaceId,
                CollectionId = normalized.CollectionId,
                SourceId = normalized.Id,
                RelationType = ContextRelationTypes.RelatedTo,
                Take = int.MaxValue
            }, scope, cancellationToken).ConfigureAwait(false);

            var newTargetIds = ingestRelations
                .Select(static r => r.TargetId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // P3-01：只删除由 ingest 投影器生产的边（Provenance="ingest"），保留其他 projector 产生的边。
            foreach (var existing in existingRelations)
            {
                if (!newTargetIds.Contains(existing.TargetId)
                    && string.Equals(existing.Provenance, "ingest", StringComparison.OrdinalIgnoreCase))
                {
                    await txRelationStore.DeleteAsync(
                        normalized.WorkspaceId,
                        normalized.CollectionId,
                        existing.Id,
                        scope,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 显式 Rollback 以确保异常路径下立即释放连接；DisposeAsync 也会再次保险地 Rollback（幂等）。
            try { await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
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
        // P1-3：无 writer 时整段跳过——既不写入新边，也不删除旧边。
        // 旧实现仅在写入条件里跳过写入，但后续删除循环仍按未写入的 newRelations 计算删除目标，
        // 会把现有 ingest-provenance 边误删，造成"item 已保存但图被破坏"的静默数据损坏。
        // newRelations 非空而 writer 为 null 属于配置错误，应在 DI 层面而非数据层面报警。
        if (_projectionWriter is null)
        {
            return;
        }

        if (newRelations.Count > 0)
        {
            // R12.4A #10: Graph Writer fallback 最终删除——production 中 writer 无条件注册，
            // 此处不再回退到 BatchUpsertAsync（会跳过 RelationProjectorOutputValidator 验证）。
            await _projectionWriter.WriteAsync(newRelations, "ingest", cancellationToken).ConfigureAwait(false);
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
