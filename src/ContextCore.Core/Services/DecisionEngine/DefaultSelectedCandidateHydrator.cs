using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Perf-1：DefaultSelectedCandidateHydrator — Selected 候选正文批量 hydrator 默认实现
//
// 目标：
//   补齐 Late Hydration 链路最后一环。Provider 在 Recall 阶段使用 IncludeContent=false
//   只返回 metadata（避免加载所有候选正文），Engine 选出最终 N 个 SelectedEnvelopes 后，
//   由本实现按 (EntityKind, WorkspaceId, CollectionId) 分组，复用
//   IContextStoreBatchLookup / IMemoryStoreBatchLookup 批量读取正文，
//   仅对 Selected IDs 做 I/O，避免对未选中候选做无用读取。
//
// 设计原则：
//   1. 接口可选注入：IContextStoreBatchLookup / IMemoryStoreBatchLookup 任一为 null 时，
//      对应 EntityKind 跳过 hydrate（Material.Content 保持空，Projector 降级为摘要）。
//   2. 不修改 Envelope 决策字段，仅填充 WorkingSet.Materials 中 Selected 候选的 Content。
//   3. 已 hydrate 的 Material（Content 非空）跳过，避免重复 I/O。
//   4. Constraint / 其他 EntityKind 不需要 hydrate（Constraint Provider 已在 Recall 阶段
//      从 IConstraintStore 加载完整 Content，IncludeContent=false 不适用）。
//   5. 批量读取按 (WorkspaceId, CollectionId) 分组，避免跨 collection 混查；
//      PostgresContextStore.BatchGetAsync 按 (workspaceId, collectionId, ids[]) 过滤。
//
// 链路位置：
//   Recall（IncludeContent=false）→ Merge → Score → Allocate（SelectedEnvelopes）
//   → ISelectedCandidateHydrator.HydrateAsync（本实现）
//   → Projector（消费已 hydrate 的 Material）。
// ===========================================================================

/// <summary>
/// Perf-1：<see cref="ISelectedCandidateHydrator"/> 默认实现。
/// 使用 <see cref="IContextStoreBatchLookup"/> / <see cref="IMemoryStoreBatchLookup"/>
/// 对 Engine 选中的候选批量读取正文，填充 <see cref="CandidateMaterial.Content"/>。
/// </summary>
/// <remarks>
/// 两个 batch lookup 接口都为 null 时（如测试容器未注册），本实现退化为 no-op，
/// Runtime 保持旧行为（Provider 在 Recall 阶段已加载所有正文，Material.Content 非空）。
/// </remarks>
public sealed class DefaultSelectedCandidateHydrator : ISelectedCandidateHydrator
{
    /// <summary>EntityKind = "context"，对应 IContextStoreBatchLookup。</summary>
    private const string ContextEntityKind = "context";

    /// <summary>EntityKind = "memory"，对应 IMemoryStoreBatchLookup。</summary>
    private const string MemoryEntityKind = "memory";

    private readonly IContextStoreBatchLookup? _contextBatchLookup;
    private readonly IMemoryStoreBatchLookup? _memoryBatchLookup;
    // P3 Fix-5：可选 tokenizer，用于 hydrate 后重算 TokenCost（null 时回退到 length/4 估算）。
    private readonly IContextTokenizerResolver? _tokenizerResolver;
    private readonly string? _tokenizerModelName;

    /// <summary>构造 hydrator。两个 batch lookup 接口均可为 null（对应 store 未实现批量查询能力）。</summary>
    /// <param name="contextBatchLookup">Context 批量查询能力（null 时跳过 context hydrate）。</param>
    /// <param name="memoryBatchLookup">Memory 批量查询能力（null 时跳过 memory hydrate）。</param>
    /// <param name="tokenizerResolver">P3 Fix-5：tokenizer 解析器（null 时 hydrate 后 TokenCost 用 length/4 估算）。</param>
    /// <param name="tokenizerModelName">P3 Fix-5：tokenizer 使用的模型名（可选）。</param>
    public DefaultSelectedCandidateHydrator(
        IContextStoreBatchLookup? contextBatchLookup = null,
        IMemoryStoreBatchLookup? memoryBatchLookup = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
    {
        _contextBatchLookup = contextBatchLookup;
        _memoryBatchLookup = memoryBatchLookup;
        _tokenizerResolver = tokenizerResolver;
        _tokenizerModelName = tokenizerModelName;
    }

    /// <inheritdoc />
    public async ValueTask<CandidateWorkingSet> HydrateAsync(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        CandidateWorkingSet workingSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedEnvelopes);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 无 batch lookup 能力 → 直接返回原 WorkingSet（保持旧行为，Material.Content 已由 Provider 加载）
        if (_contextBatchLookup is null && _memoryBatchLookup is null)
        {
            return workingSet;
        }

        if (selectedEnvelopes.Count == 0)
        {
            return workingSet;
        }

        // 筛出需要 hydrate 的 Selected 候选：
        // 1. EntityKind 为 context / memory（constraint 等不 hydrate）
        // 2. WorkingSet.Materials 中对应 Material 存在且 Content 为空（未 hydrate）
        // 3. CanonicalKey 字段有效（EntityId 非空，作为 store 查询的 id）
        // 注意：value tuple 键使用默认 EqualityComparer<(string, string)>.Default，
        // 内部对 string 字段使用 ordinal 比较，与 StringComparer.Ordinal 等价。
        var contextGroups = new Dictionary<(string WorkspaceId, string CollectionId), List<ContextCandidateEnvelope>>();
        var memoryGroups = new Dictionary<(string WorkspaceId, string CollectionId), List<ContextCandidateEnvelope>>();

        foreach (var envelope in selectedEnvelopes)
        {
            var key = envelope.CanonicalKey;
            if (!key.IsValid || string.IsNullOrEmpty(key.EntityId))
            {
                continue;
            }

            // 已 hydrate 的 Material 跳过（Provider 已加载正文，或前一轮已 hydrate）
            if (workingSet.Materials.TryGetValue(key, out var existingMaterial)
                && !string.IsNullOrEmpty(existingMaterial.Content))
            {
                continue;
            }

            var groupKey = (key.WorkspaceId, key.CollectionId);
            if (string.Equals(key.EntityKind, ContextEntityKind, StringComparison.Ordinal))
            {
                if (!contextGroups.TryGetValue(groupKey, out var list))
                {
                    list = new List<ContextCandidateEnvelope>();
                    contextGroups[groupKey] = list;
                }
                list.Add(envelope);
            }
            else if (string.Equals(key.EntityKind, MemoryEntityKind, StringComparison.Ordinal))
            {
                if (!memoryGroups.TryGetValue(groupKey, out var list))
                {
                    list = new List<ContextCandidateEnvelope>();
                    memoryGroups[groupKey] = list;
                }
                list.Add(envelope);
            }
            // 其他 EntityKind（constraint 等）不 hydrate
        }

        // 两个分组都为空 → 无需任何 I/O，直接返回原 WorkingSet
        if (contextGroups.Count == 0 && memoryGroups.Count == 0)
        {
            return workingSet;
        }

        // 收集 hydrate 后的 Material 更新（按 CanonicalKey 索引）
        var materialUpdates = new ConcurrentDictionary<CanonicalCandidateKey, CandidateMaterial>();

        // 并发批量读取 context + memory（两类 store 独立，无共享资源）
        var tasks = new List<Task>(4);
        if (contextGroups.Count > 0 && _contextBatchLookup is not null)
        {
            tasks.Add(HydrateContextGroupsAsync(contextGroups, materialUpdates, cancellationToken));
        }
        if (memoryGroups.Count > 0 && _memoryBatchLookup is not null)
        {
            tasks.Add(HydrateMemoryGroupsAsync(memoryGroups, materialUpdates, cancellationToken));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // 无任何更新 → 返回原 WorkingSet（避免无谓的字典复制）
        if (materialUpdates.IsEmpty)
        {
            return workingSet;
        }

        // 合并更新到新 Materials 字典（保留原有 Material 的 NativeKind / SourceRefs，
        // 用 hydrate 后的 Content 构造新 Material——避免 with 表达式导致 ContentHash 残留旧值）。
        // CandidateMaterial.Content 的 init accessor 仅在 _contentHash 为空时计算 hash，
        // 使用 with { Content = ... } 会跳过重算（_contentHash 已从原对象复制），导致 hash 与新内容不一致。
        // 因此此处显式构造新 CandidateMaterial，让 ContentHash 由 init accessor 正确计算。
        // P3 Fix-5：hydrate 后 Content 已变更，原 TokenCost（recall 阶段基于空/旧正文计算）已过期，
        // 需从 hydrate 后的正文重新计算 TokenCost，让 Projector / Allocator 看到准确 token 数。
        var mergedMaterials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(workingSet.Materials, comparer: null);
        foreach (var kvp in materialUpdates)
        {
            if (mergedMaterials.TryGetValue(kvp.Key, out var original))
            {
                // P3 Fix-5：用 hydrate 后的正文重算 TokenCost（tokenizer 可用时精确计数，否则 length/4 估算）
                var hydratedTokenCost = TokenCostHelper.ComputeTokenCost(
                    kvp.Value.Content, _tokenizerResolver, _tokenizerModelName);
                // 保留原 Material 的 NativeKind / SourceRefs，仅用 hydrate 后的 Content + 重算的 TokenCost 替换
                mergedMaterials[kvp.Key] = new CandidateMaterial
                {
                    Key = original.Key,
                    Content = kvp.Value.Content,
                    NativeKind = original.NativeKind,
                    SourceRefs = original.SourceRefs,
                    TokenCost = hydratedTokenCost
                };
            }
            else
            {
                mergedMaterials[kvp.Key] = kvp.Value;
            }
        }

        return workingSet with { Materials = mergedMaterials };
    }

    /// <summary>
    /// 按 (WorkspaceId, CollectionId) 分组批量读取 ContextItem，填充 materialUpdates。
    /// 找不到的 item 跳过（Material.Content 保持空，Projector 降级为摘要）。
    /// </summary>
    private async Task HydrateContextGroupsAsync(
        Dictionary<(string WorkspaceId, string CollectionId), List<ContextCandidateEnvelope>> groups,
        ConcurrentDictionary<CanonicalCandidateKey, CandidateMaterial> materialUpdates,
        CancellationToken cancellationToken)
    {
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (workspaceId, collectionId) = group.Key;
            var envelopes = group.Value;

            // 提取 EntityId 列表（去重，避免同 id 多候选重复查询）
            var ids = new List<string>(envelopes.Count);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var env in envelopes)
            {
                if (seenIds.Add(env.CanonicalKey.EntityId))
                {
                    ids.Add(env.CanonicalKey.EntityId);
                }
            }

            if (ids.Count == 0)
            {
                continue;
            }

            IReadOnlyList<ContextItem> items;
            try
            {
                items = await _contextBatchLookup!.BatchGetAsync(workspaceId, collectionId, ids, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Store 读取失败 → 跳过此组（Material.Content 保持空，Projector 降级为摘要）
                // 不向上抛出：hydrate 失败不应阻塞主决策流（决策已由 Engine 完成）
                continue;
            }

            if (items.Count == 0)
            {
                continue;
            }

            // 索引 by Id，便于 O(1) 查找
            var itemById = new Dictionary<string, ContextItem>(items.Count, StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Id))
                {
                    itemById[item.Id] = item;
                }
            }

            foreach (var env in envelopes)
            {
                if (itemById.TryGetValue(env.CanonicalKey.EntityId, out var item)
                    && !string.IsNullOrEmpty(item.Content))
                {
                    var material = new CandidateMaterial
                    {
                        Key = env.CanonicalKey,
                        Content = item.Content,
                        NativeKind = string.IsNullOrEmpty(item.Type) ? ContextEntityKind : item.Type,
                        SourceRefs = item.SourceRefs
                    };
                    materialUpdates[env.CanonicalKey] = material;
                }
                // 找不到或 Content 为空 → 跳过（保持原 Material 不变）
            }
        }
    }

    /// <summary>
    /// 按 (WorkspaceId, CollectionId) 分组批量读取 ContextMemoryItem，填充 materialUpdates。
    /// 找不到的 item 跳过（Material.Content 保持空，Projector 降级为摘要）。
    /// </summary>
    private async Task HydrateMemoryGroupsAsync(
        Dictionary<(string WorkspaceId, string CollectionId), List<ContextCandidateEnvelope>> groups,
        ConcurrentDictionary<CanonicalCandidateKey, CandidateMaterial> materialUpdates,
        CancellationToken cancellationToken)
    {
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (workspaceId, collectionId) = group.Key;
            var envelopes = group.Value;

            var ids = new List<string>(envelopes.Count);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var env in envelopes)
            {
                if (seenIds.Add(env.CanonicalKey.EntityId))
                {
                    ids.Add(env.CanonicalKey.EntityId);
                }
            }

            if (ids.Count == 0)
            {
                continue;
            }

            IReadOnlyList<ContextMemoryItem> items;
            try
            {
                items = await _memoryBatchLookup!.BatchGetAsync(workspaceId, collectionId, ids, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Store 读取失败 → 跳过此组（不阻塞主决策流）
                continue;
            }

            if (items.Count == 0)
            {
                continue;
            }

            var itemById = new Dictionary<string, ContextMemoryItem>(items.Count, StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Id))
                {
                    itemById[item.Id] = item;
                }
            }

            foreach (var env in envelopes)
            {
                if (itemById.TryGetValue(env.CanonicalKey.EntityId, out var item)
                    && !string.IsNullOrEmpty(item.Content))
                {
                    var material = new CandidateMaterial
                    {
                        Key = env.CanonicalKey,
                        Content = item.Content,
                        NativeKind = string.IsNullOrEmpty(item.Type) ? MemoryEntityKind : item.Type,
                        SourceRefs = item.SourceRefs
                    };
                    materialUpdates[env.CanonicalKey] = material;
                }
            }
        }
    }
}
