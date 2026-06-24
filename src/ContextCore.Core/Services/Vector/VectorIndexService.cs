using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Vector Index V1 的只读诊断与 reindex preview 服务，不接正式 retrieval/package。</summary>
public sealed class VectorIndexService
{
    private readonly IVectorIndexStore? _store;
    private readonly IEmbeddingGenerator? _generator;
    private readonly IContextStore? _contextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IReadOnlyList<VectorReindexSourceItem> _sourceItems;

    public VectorIndexService(
        IVectorIndexStore? store,
        IEmbeddingGenerator? generator,
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IReadOnlyList<VectorReindexSourceItem>? sourceItems = null)
    {
        _store = store;
        _generator = generator;
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _sourceItems = sourceItems ?? Array.Empty<VectorReindexSourceItem>();
    }

    public async Task<VectorIndexStatusResponse> GetStatusAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = await GetDiagnosticsAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        var warnings = new List<string>();
        if (_store is null)
        {
            warnings.Add("当前 provider 未注册 V1 vector index store。");
        }

        if (_generator is null)
        {
            warnings.Add("当前 provider 未注册 embedding generator。");
        }

        return new VectorIndexStatusResponse
        {
            Provider = _generator?.Provider ?? string.Empty,
            Model = _generator?.Model ?? string.Empty,
            Dimension = _generator?.Dimension ?? 0,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            IndexedCount = diagnostics.IndexedCount,
            StaleCount = diagnostics.StaleCount,
            MissingCount = diagnostics.MissingCount,
            DuplicateCount = diagnostics.DuplicateCount,
            OrphanCount = diagnostics.OrphanCount,
            StoreAvailable = _store is not null,
            GeneratorAvailable = _generator is not null,
            CreatedAt = DateTimeOffset.UtcNow,
            Warnings = warnings
        };
    }

    public async Task<VectorIndexDiagnosticsReport> GetDiagnosticsAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var entries = _store is null
            ? Array.Empty<VectorIndexEntry>()
            : await _store.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = 10_000,
                IncludeVector = true
            }, cancellationToken).ConfigureAwait(false);

        var diagnostics = new List<VectorIndexDiagnostic>();
        if (_store is null)
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ProviderUnavailable,
                workspaceId,
                collectionId,
                string.Empty,
                null,
                "V1 vector index store 未注册。",
                "为当前 storage provider 注册 IVectorIndexStore。"));
        }
        else
        {
            diagnostics.AddRange(await _store.GetDiagnosticsAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = 10_000,
                IncludeVector = true
            }, cancellationToken).ConfigureAwait(false));
        }

        if (_generator is null)
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ProviderUnavailable,
                workspaceId,
                collectionId,
                string.Empty,
                null,
                "embedding generator 未注册。",
                "注册 MockEmbeddingGenerator 或 DeterministicHashEmbeddingGenerator。"));
        }

        var sources = await LoadSourceItemsAsync(workspaceId, collectionId, 10_000, cancellationToken)
            .ConfigureAwait(false);
        diagnostics.AddRange(BuildSourceDiagnostics(workspaceId, collectionId, entries, sources));

        var counts = diagnostics
            .GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new VectorIndexDiagnosticsReport
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            IndexedCount = entries.Count,
            MissingCount = Count(counts, VectorIndexDiagnosticTypes.MissingEmbedding),
            StaleCount = Count(counts, VectorIndexDiagnosticTypes.StaleEmbedding)
                         + Count(counts, VectorIndexDiagnosticTypes.ContentHashMismatch)
                         + Count(counts, VectorIndexDiagnosticTypes.RequiresReindex),
            DuplicateCount = Count(counts, VectorIndexDiagnosticTypes.DuplicateVectorEntry),
            OrphanCount = Count(counts, VectorIndexDiagnosticTypes.OrphanVectorEntry),
            DimensionMismatchCount = Count(counts, VectorIndexDiagnosticTypes.DimensionMismatch),
            UnsupportedModelCount = Count(counts, VectorIndexDiagnosticTypes.UnsupportedEmbeddingModel)
                                    + Count(counts, VectorIndexDiagnosticTypes.EmbeddingModelMismatch),
            ProviderUnavailableCount = Count(counts, VectorIndexDiagnosticTypes.ProviderUnavailable),
            CountsByType = counts,
            Diagnostics = diagnostics
        };
    }

    public async Task<VectorReindexPreviewResponse> PreviewReindexAsync(
        VectorReindexPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var warnings = new List<string>();
        if (_store is null)
        {
            warnings.Add("当前 provider 未注册 V1 vector index store，preview 只能显示 source item。");
        }

        if (_generator is null)
        {
            warnings.Add("embedding generator 未注册，preview 不会执行实际向量生成。");
        }

        var sources = await LoadSourceItemsAsync(
            request.WorkspaceId,
            request.CollectionId,
            request.Take > 0 ? request.Take : 200,
            cancellationToken,
            request.IncludeContextItems,
            request.IncludeMemoryItems).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.Layer))
        {
            sources = sources
                .Where(source => string.Equals(source.Layer, request.Layer, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var entries = _store is null
            ? Array.Empty<VectorIndexEntry>()
            : await _store.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                Take = 10_000,
                IncludeVector = false
            }, cancellationToken).ConfigureAwait(false);

        var entriesByItem = entries
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        var items = new List<VectorReindexPreviewItem>();
        foreach (var source in sources)
        {
            var currentHash = VectorIndexContentHasher.Hash(source.Text);
            if (!entriesByItem.TryGetValue(source.ItemId, out var existing))
            {
                items.Add(NewPreviewItem(source, "Create", currentHash, null, "source item 尚未建立 embedding。"));
            }
            else if (!string.Equals(existing.ContentHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(NewPreviewItem(source, "Update", currentHash, existing.ContentHash, "source item 内容 hash 已变化。"));
            }
            else if (_generator is not null
                     && HasGeneratorCompatibilityChange(existing, EmbeddingGeneratorDescriptor.From(_generator)))
            {
                items.Add(NewPreviewItem(source, "Update", currentHash, existing.ContentHash, "embedding provider/model/dimension/normalize 与当前 generator 不一致。"));
            }
            else
            {
                items.Add(NewPreviewItem(source, "Current", currentHash, existing.ContentHash, "embedding 已与当前 source 内容一致。"));
            }
        }

        var sourceIds = sources.Select(source => source.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var orphan in entries.Where(entry => !sourceIds.Contains(entry.ItemId)))
        {
            items.Add(new VectorReindexPreviewItem
            {
                ItemId = orphan.ItemId,
                ItemKind = orphan.ItemKind,
                Layer = orphan.Layer,
                Action = "DeleteOrphan",
                CurrentContentHash = string.Empty,
                ExistingContentHash = orphan.ContentHash,
                Reason = "vector entry 没有对应 source item。"
            });
        }

        return new VectorReindexPreviewResponse
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SourceItemCount = sources.Length,
            WouldCreateCount = items.Count(item => item.Action == "Create"),
            WouldUpdateCount = items.Count(item => item.Action == "Update"),
            AlreadyCurrentCount = items.Count(item => item.Action == "Current"),
            WouldDeleteOrphanCount = items.Count(item => item.Action == "DeleteOrphan"),
            Items = items,
            Warnings = warnings
        };
    }

    private async Task<SourceItem[]> LoadSourceItemsAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken,
        bool includeContextItems = true,
        bool includeMemoryItems = true)
    {
        var sources = new List<SourceItem>();
        if (includeContextItems && _contextStore is not null)
        {
            var contextItems = await _contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = take,
                IncludeContent = true
            }, cancellationToken).ConfigureAwait(false);
            sources.AddRange(contextItems.Select(item => new SourceItem(
                item.Id,
                item.Type,
                "context",
                item.Content ?? string.Empty,
                item.WorkspaceId,
                item.CollectionId,
                item.UpdatedAt)));
        }

        if (includeMemoryItems && _memoryStore is not null)
        {
            var memoryItems = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = take
            }, cancellationToken).ConfigureAwait(false);
            sources.AddRange(memoryItems.Select(item => new SourceItem(
                item.Id,
                item.Type,
                item.Layer.ToString(),
                item.Content ?? string.Empty,
                item.WorkspaceId,
                item.CollectionId,
                item.UpdatedAt)));
        }

        if (_sourceItems.Count > 0)
        {
            sources.AddRange(_sourceItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Select(item => new SourceItem(
                    item.ItemId,
                    string.IsNullOrWhiteSpace(item.ItemKind) ? "unknown" : item.ItemKind,
                    string.IsNullOrWhiteSpace(item.Layer) ? "external" : item.Layer,
                    item.Text,
                    workspaceId,
                    collectionId,
                    item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt)));
        }

        return sources
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToArray();
    }

    private IEnumerable<VectorIndexDiagnostic> BuildSourceDiagnostics(
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorIndexEntry> entries,
        IReadOnlyList<SourceItem> sources)
    {
        var diagnostics = new List<VectorIndexDiagnostic>();
        var sourceById = sources.ToDictionary(source => source.ItemId, StringComparer.OrdinalIgnoreCase);
        var entriesByItem = entries.GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var descriptor = _generator is null ? null : EmbeddingGeneratorDescriptor.From(_generator);

        foreach (var source in sources)
        {
            var expectedHash = VectorIndexContentHasher.Hash(source.Text);
            if (!entriesByItem.TryGetValue(source.ItemId, out var itemEntries) || itemEntries.Length == 0)
            {
                diagnostics.Add(NewDiagnostic(
                    VectorIndexDiagnosticTypes.MissingEmbedding,
                    workspaceId,
                    collectionId,
                    source.ItemId,
                    null,
                    "source item 尚未建立 vector index entry。",
                    "运行 reindex 写入 embedding。"));
                continue;
            }

            var scopedEntries = descriptor is null
                ? itemEntries
                : itemEntries
                    .Where(entry => string.Equals(entry.EmbeddingProvider, descriptor.Provider, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(entry.EmbeddingModel, descriptor.Model, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            if (scopedEntries.Length == 0)
            {
                diagnostics.Add(NewDiagnostic(
                    VectorIndexDiagnosticTypes.MissingEmbedding,
                    workspaceId,
                    collectionId,
                    source.ItemId,
                    null,
                    "当前 provider/model 尚未为 source item 建立 vector entry。",
                    "使用当前 provider 执行受控 reindex。"));
                diagnostics.AddRange(BuildGeneratorCompatibilityDiagnostics(
                    workspaceId,
                    collectionId,
                    source.ItemId,
                    itemEntries.OrderByDescending(entry => entry.UpdatedAt).First(),
                    descriptor!));
                continue;
            }

            foreach (var entry in scopedEntries)
            {
                if (!string.Equals(entry.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(NewDiagnostic(
                        VectorIndexDiagnosticTypes.StaleEmbedding,
                        workspaceId,
                        collectionId,
                        source.ItemId,
                        entry.EntryId,
                        "source item 内容 hash 与 vector entry 不一致。",
                        "重新生成该 item 的 embedding。"));
                    diagnostics.Add(NewDiagnostic(
                        VectorIndexDiagnosticTypes.ContentHashMismatch,
                        workspaceId,
                        collectionId,
                        source.ItemId,
                        entry.EntryId,
                        "ContentHashMismatch：vector entry 的 ContentHash 已过期。",
                        "用当前 source 内容 hash 更新 entry。"));
                }

                if (descriptor is not null)
                {
                    diagnostics.AddRange(BuildGeneratorCompatibilityDiagnostics(
                        workspaceId,
                        collectionId,
                        source.ItemId,
                        entry,
                        descriptor));
                }
            }
        }

        foreach (var entry in entries.Where(entry => !sourceById.ContainsKey(entry.ItemId)))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.OrphanVectorEntry,
                workspaceId,
                collectionId,
                entry.ItemId,
                entry.EntryId,
                "vector entry 没有对应 source item。",
                "保留前确认来源；无来源时清理以减少存储噪声。"));
        }

        return diagnostics;
    }

    private static VectorReindexPreviewItem NewPreviewItem(
        SourceItem source,
        string action,
        string currentHash,
        string? existingHash,
        string reason)
    {
        return new VectorReindexPreviewItem
        {
            ItemId = source.ItemId,
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            Action = action,
            CurrentContentHash = currentHash,
            ExistingContentHash = existingHash,
            Reason = reason
        };
    }

    private static VectorIndexDiagnostic NewDiagnostic(
        string type,
        string workspaceId,
        string collectionId,
        string itemId,
        string? entryId,
        string message,
        string suggestedAction)
    {
        return new VectorIndexDiagnostic
        {
            DiagnosticId = $"{type}:{workspaceId}:{collectionId}:{entryId ?? itemId}",
            Type = type,
            Severity = "Warning",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = itemId,
            EntryId = entryId,
            Message = message,
            SuggestedAction = suggestedAction,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IEnumerable<VectorIndexDiagnostic> BuildGeneratorCompatibilityDiagnostics(
        string workspaceId,
        string collectionId,
        string itemId,
        VectorIndexEntry entry,
        EmbeddingGeneratorDescriptor descriptor)
    {
        var diagnostics = new List<VectorIndexDiagnostic>();
        if (!string.Equals(entry.EmbeddingProvider, descriptor.Provider, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.EmbeddingProviderChanged,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "vector entry 的 embedding provider 与当前 generator 不一致。",
                "重新生成该 item 的 embedding。"));
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.ProviderMismatch,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "query/index provider 不兼容。",
                "使用同一 provider 重建 index。"));
        }

        if (!string.Equals(entry.EmbeddingModel, descriptor.Model, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.EmbeddingModelChanged,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "vector entry 的 embedding model 与当前 generator 不一致。",
                "重新生成该 item 的 embedding。"));
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.EmbeddingModelMismatch,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "query/index model 不兼容。",
                "使用同一 model 重建 index。"));
        }

        if (descriptor.Dimension > 0 && entry.Dimension != descriptor.Dimension)
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.DimensionChanged,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "vector entry 维度与当前 generator 不一致。",
                "使用当前 generator 重新生成 embedding。"));
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.DimensionMismatch,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "query/index dimension 不兼容。",
                "不要混用不同维度的 index。"));
        }

        if (entry.Metadata.TryGetValue("normalize", out var normalized)
            && bool.TryParse(normalized, out var entryNormalize)
            && entryNormalize != descriptor.Normalize)
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.NormalizationMismatch,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "vector entry normalization 配置与当前 generator 不一致。",
                "按当前 normalization 配置重新生成 embedding。"));
        }

        if (diagnostics.Count > 0)
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.RequiresReindex,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "embedding provider/model/dimension/normalization 变化，需要 reindex。",
                "执行受控 vector reindex apply。"));
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.UnsupportedEmbeddingModel,
                workspaceId,
                collectionId,
                itemId,
                entry.EntryId,
                "vector entry 与当前 embedding generator 不兼容。",
                "确认 provider 配置或重新生成 embedding。"));
        }

        return diagnostics;
    }

    private static bool HasGeneratorCompatibilityChange(
        VectorIndexEntry entry,
        EmbeddingGeneratorDescriptor descriptor)
    {
        if (!string.Equals(entry.EmbeddingProvider, descriptor.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.EmbeddingModel, descriptor.Model, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (descriptor.Dimension > 0 && entry.Dimension != descriptor.Dimension)
        {
            return true;
        }

        return entry.Metadata.TryGetValue("normalize", out var normalized)
               && bool.TryParse(normalized, out var entryNormalize)
               && entryNormalize != descriptor.Normalize;
    }

    private static int Count(IReadOnlyDictionary<string, int> counts, string type)
    {
        return counts.TryGetValue(type, out var count) ? count : 0;
    }

    private sealed record SourceItem(
        string ItemId,
        string ItemKind,
        string Layer,
        string Text,
        string WorkspaceId,
        string CollectionId,
        DateTimeOffset UpdatedAt);
}
