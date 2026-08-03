using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// DefaultSelectedCandidateHydrator — Selected 候选正文批量 hydrator 默认实现
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
//
// 本实现额外返回 HydrationRepairDecision（HydrationResult.Repair），携带 hydrate 后
//   真实的 selected / dropped 候选 ID、更新的 AllocationDecisions、精确 token 总数与失败明细，
//   让 Caller 重建整个 ContextDecisionResult（而非仅替换 WorkingSet）。
// ===========================================================================

/// <summary>
/// <see cref="ISelectedCandidateHydrator"/> 默认实现。
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
    // 可选 tokenizer，用于 hydrate 后重算 TokenCost（null 时回退到 length/4 估算）。
    private readonly IContextTokenizerResolver? _tokenizerResolver;
    private readonly string? _tokenizerModelName;

    /// <summary>构造 hydrator。两个 batch lookup 接口均可为 null（对应 store 未实现批量查询能力）。</summary>
    /// <param name="contextBatchLookup">Context 批量查询能力（null 时跳过 context hydrate）。</param>
    /// <param name="memoryBatchLookup">Memory 批量查询能力（null 时跳过 memory hydrate）。</param>
    /// <param name="tokenizerResolver">tokenizer 解析器（null 时 hydrate 后 TokenCost 用 length/4 估算）。</param>
    /// <param name="tokenizerModelName">tokenizer 使用的模型名（可选）。</param>
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
    public async ValueTask<HydrationResult> HydrateAsync(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        CandidateWorkingSet workingSet,
        int tokenBudget = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedEnvelopes);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 无 batch lookup 能力 → 直接返回原 WorkingSet（保持旧行为，Material.Content 已由 Provider 加载）
        if (_contextBatchLookup is null && _memoryBatchLookup is null)
        {
            return NoHydration(workingSet);
        }

        if (selectedEnvelopes.Count == 0)
        {
            return NoHydration(workingSet);
        }

        // 构建 CanonicalKey → CandidateId 映射，用于后续生成 HydrationRepairDecision
        // （Repair 中的 HydratedSelected / HydrationDropped / HydrationFailures 均使用 CandidateId）。
        var keyToCandidateId = new Dictionary<CanonicalCandidateKey, string>(selectedEnvelopes.Count);
        foreach (var env in selectedEnvelopes)
        {
            if (env.CanonicalKey.IsValid && !string.IsNullOrEmpty(env.CandidateId))
            {
                keyToCandidateId[env.CanonicalKey] = env.CandidateId;
            }
        }

        // 筛出需要 hydrate 的 Selected 候选：
        // 1. EntityKind 为 context / memory（constraint 等不 hydrate）
        // 2. WorkingSet.Materials 中对应 Material 存在且 Content 为空（未 hydrate）
        // 3. CanonicalKey 字段有效（EntityId 非空，作为 store 查询的 id）
        // 注意：value tuple 键使用默认 EqualityComparer<(string, string)>.Default，
        // 内部对 string 字段使用 ordinal 比较，与 StringComparer.Ordinal 等价。
        var contextGroups = new Dictionary<(string WorkspaceId, string CollectionId), List<ContextCandidateEnvelope>>();
        var memoryGroups = new Dictionary<(string WorkspaceId, string CollectionId), List<ContextCandidateEnvelope>>();
        // hydrate 候选键集合（成功/失败计数基数；仅 context/memory 且未 hydrate 的候选）
        var candidateKeys = new HashSet<CanonicalCandidateKey>();

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
                candidateKeys.Add(key);
                if (!contextGroups.TryGetValue(groupKey, out var list))
                {
                    list = new List<ContextCandidateEnvelope>();
                    contextGroups[groupKey] = list;
                }
                list.Add(envelope);
            }
            else if (string.Equals(key.EntityKind, MemoryEntityKind, StringComparison.Ordinal))
            {
                candidateKeys.Add(key);
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
            return NoHydration(workingSet);
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

        // hydrate 成功/失败计数（失败 = 候选未出现在 materialUpdates：store 未命中 / 读取异常 / 正文为空）
        var hydratedCount = 0;
        foreach (var candidateKey in candidateKeys)
        {
            if (materialUpdates.ContainsKey(candidateKey))
            {
                hydratedCount++;
            }
        }
        var failedCount = candidateKeys.Count - hydratedCount;

        // 构建 hydration 失败明细（candidateId -> 错误描述）
        var hydrationFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidateKey in candidateKeys)
        {
            if (!materialUpdates.ContainsKey(candidateKey)
                && keyToCandidateId.TryGetValue(candidateKey, out var failedCandidateId))
            {
                hydrationFailures[failedCandidateId] = "store miss or empty content";
            }
        }

        // 无任何更新 → 仍需返回 Repair 决策（所有需 hydrate 的候选均失败）
        if (materialUpdates.IsEmpty)
        {
            // 构建 Repair 决策——所有需 hydrate 的候选均失败，应被 dropped
            var repair = BuildRepairDecision(
                selectedEnvelopes,
                finalMaterials: workingSet.Materials,
                candidateKeys: candidateKeys,
                materialUpdates: materialUpdates,
                droppedKeys: null,
                keyToCandidateId: keyToCandidateId,
                hydrationFailures: hydrationFailures);
            return new HydrationResult
            {
                WorkingSet = workingSet,
                HydratedCount = 0,
                FailedCount = failedCount,
                BudgetExceeded = false,
                Repair = repair
            };
        }

        // 合并更新到新 Materials 字典（保留原有 Material 的 NativeKind / SourceRefs，
        // 用 hydrate 后的 Content 构造新 Material——避免 with 表达式导致 ContentHash 残留旧值）。
        // CandidateMaterial.Content 的 init accessor 仅在 _contentHash 为空时计算 hash，
        // 使用 with { Content = ... } 会跳过重算（_contentHash 已从原对象复制），导致 hash 与新内容不一致。
        // 因此此处显式构造新 CandidateMaterial，让 ContentHash 由 init accessor 正确计算。
        // hydrate 后 Content 已变更，原 TokenCost（recall 阶段基于空/旧正文计算）已过期，
        // 需从 hydrate 后的正文重新计算 TokenCost，让 Projector / Allocator 看到准确 token 数。
        var mergedMaterials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(workingSet.Materials, comparer: null);
        foreach (var kvp in materialUpdates)
        {
            if (mergedMaterials.TryGetValue(kvp.Key, out var original))
            {
                // 用 hydrate 后的正文重算 TokenCost（tokenizer 可用时精确计数，否则 length/4 估算）
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

        var hydratedWorkingSet = workingSet with { Materials = mergedMaterials };

        // 最终预算修复 — hydrate 后真实 TokenCost 总和可能超出 Engine 基于召回估算的预算分配，
        // 按 FinalScore 升序裁减低分 Material（mandatory / hard constraint 不裁剪）。
        var (finalWorkingSet, budgetExceeded, repairDiagnostics) = RepairBudget(
            selectedEnvelopes, hydratedWorkingSet, tokenBudget);

        // 构建 Repair 决策——比较 hydratedWorkingSet.Materials 与 finalWorkingSet.Materials
        // 得出被预算修复裁剪的候选 keys，结合 candidateKeys / materialUpdates 判断 dropped 集合。
        var droppedKeys = new HashSet<CanonicalCandidateKey>();
        foreach (var key in hydratedWorkingSet.Materials.Keys)
        {
            if (!finalWorkingSet.Materials.ContainsKey(key))
            {
                droppedKeys.Add(key);
            }
        }

        var repairDecision = BuildRepairDecision(
            selectedEnvelopes,
            finalMaterials: finalWorkingSet.Materials,
            candidateKeys: candidateKeys,
            materialUpdates: materialUpdates,
            droppedKeys: droppedKeys,
            keyToCandidateId: keyToCandidateId,
            hydrationFailures: hydrationFailures);

        return new HydrationResult
        {
            WorkingSet = finalWorkingSet,
            HydratedCount = hydratedCount,
            FailedCount = failedCount,
            BudgetExceeded = budgetExceeded,
            BudgetRepairDiagnostics = repairDiagnostics,
            Repair = repairDecision
        };
    }

    /// <summary>
    /// 无 hydrate 发生时的快捷结果（WorkingSet 原样返回，计数全 0，Repair 为 null）。
    /// </summary>
    private static HydrationResult NoHydration(CandidateWorkingSet workingSet)
    {
        return new HydrationResult
        {
            WorkingSet = workingSet,
            HydratedCount = 0,
            FailedCount = 0,
            BudgetExceeded = false
        };
    }

    /// <summary>
    /// 构建 HydrationRepairDecision。基于 hydrate 后的最终 Materials 状态、
    /// 需 hydrate 候选集合、hydrate 成功集合与被预算修复裁剪的 keys，分类 selected / dropped，
    /// 生成 UpdatedAllocationDecisions 与 ExactTokenCount。
    /// </summary>
    /// <param name="selectedEnvelopes">Engine 选中的候选 envelope 集合。</param>
    /// <param name="finalMaterials">hydrate（+ 预算修复）后的最终 Materials 字典。</param>
    /// <param name="candidateKeys">需要 hydrate 的候选 key 集合（context/memory 且 Content 原为空）。</param>
    /// <param name="materialUpdates">hydrate 成功的 Material 更新（key -> Material）。</param>
    /// <param name="droppedKeys">被预算修复裁剪的 key 集合（null 表示未发生预算修复）。</param>
    /// <param name="keyToCandidateId">CanonicalKey → CandidateId 映射。</param>
    /// <param name="hydrationFailures">hydrate 失败明细（candidateId -> error）。</param>
    /// <returns>正式修复决策。</returns>
    private static HydrationRepairDecision BuildRepairDecision(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> finalMaterials,
        HashSet<CanonicalCandidateKey> candidateKeys,
        ConcurrentDictionary<CanonicalCandidateKey, CandidateMaterial> materialUpdates,
        HashSet<CanonicalCandidateKey>? droppedKeys,
        Dictionary<CanonicalCandidateKey, string> keyToCandidateId,
        Dictionary<string, string> hydrationFailures)
    {
        var hydratedSelected = new List<string>(selectedEnvelopes.Count);
        var hydrationDropped = new List<string>(selectedEnvelopes.Count);
        var updatedDecisions = new List<CandidateAllocationDecision>(selectedEnvelopes.Count);
        var exactTokenCount = 0;

        foreach (var envelope in selectedEnvelopes)
        {
            var key = envelope.CanonicalKey;
            var cid = envelope.CandidateId;
            var section = ResolveSectionForAllocation(envelope);

            // 判断候选是否被 dropped：
            //   1. 被预算修复裁剪（key 在 droppedKeys 中）
            //   2. 需 hydrate 但失败（key 在 candidateKeys 中但不在 materialUpdates 中）
            bool isDropped = false;
            if (droppedKeys is not null && droppedKeys.Contains(key))
            {
                isDropped = true;
            }
            else if (candidateKeys.Contains(key) && !materialUpdates.ContainsKey(key))
            {
                isDropped = true;
            }

            if (isDropped)
            {
                hydrationDropped.Add(cid);
                // 与 Runtime 重建 DroppedEnvelopes 的分类一致：
                // hydration 失败（缺正文/证据）→ EvidenceMissing；预算修复裁剪 → TokenBudgetExceeded。
                var isHydrationFailure = hydrationFailures.ContainsKey(cid);
                updatedDecisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = key,
                    Section = section,
                    IncludedTokens = 0,
                    IsTruncated = false,
                    ReasonCode = isHydrationFailure
                        ? CandidateDecisionReasonCode.EvidenceMissing
                        : CandidateDecisionReasonCode.TokenBudgetExceeded
                });
            }
            else
            {
                // Retained：计算精确 token 数（hydrate 后的 Material.TokenCost 优先）
                hydratedSelected.Add(cid);
                var tokens = GetEffectiveMaterialTokens(envelope, finalMaterials);
                exactTokenCount += tokens;
                var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;
                updatedDecisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = key,
                    Section = section,
                    IncludedTokens = tokens,
                    IsTruncated = false,
                    ReasonCode = isMandatory
                        ? CandidateDecisionReasonCode.SelectedMandatory
                        : CandidateDecisionReasonCode.SelectedHighestUtility
                });
            }
        }

        return new HydrationRepairDecision
        {
            HydratedSelected = hydratedSelected,
            HydrationDropped = hydrationDropped,
            UpdatedAllocationDecisions = updatedDecisions,
            ExactTokenCount = exactTokenCount,
            HydrationFailures = hydrationFailures
        };
    }

    /// <summary>
    /// 解析候选所属 section（与 UnifiedRuntimeDefaults.ResolveSectionForAllocation 对齐）。
    /// 用于在 BuildRepairDecision 中生成 CandidateAllocationDecision.Section。
    /// </summary>
    private static string ResolveSectionForAllocation(ContextCandidateEnvelope envelope)
    {
        return envelope.Source switch
        {
            ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "mandatory",
            ContextCandidateSource.WorkingMemory or ContextCandidateSource.StableMemory => "memory",
            ContextCandidateSource.Graph => "relations",
            ContextCandidateSource.GlobalContext => "global",
            ContextCandidateSource.RelatedContext => "related",
            _ => "default"
        };
    }

    /// <summary>
    /// 最终预算修复。hydrate 后正文的真实 TokenCost 总和可能超出 Engine 基于召回估算值
    /// 做出的预算分配（Recall 阶段 IncludeContent=false 时 TokenCost 为估算或缺失）。
    /// 超限时按 FinalScore 升序裁减低分 Material（mandatory / hard constraint 不裁剪），
    /// 直到 Selected 候选的 TokenCost 总和回到预算内；全为 mandatory 时直接返回 BudgetExceeded=true。
    /// </summary>
    /// <param name="selectedEnvelopes">Engine 选中的候选 envelope 集合。</param>
    /// <param name="workingSet">hydrate 后的候选工作集。</param>
    /// <param name="tokenBudget">最终 token 预算；&lt;= 0 表示无预算约束，跳过修复。</param>
    /// <returns>修复后的 WorkingSet + 是否仍超预算 + 修复诊断（未修复时为 null）。</returns>
    private static (CandidateWorkingSet WorkingSet, bool BudgetExceeded, IReadOnlyList<string>? Diagnostics) RepairBudget(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        CandidateWorkingSet workingSet,
        int tokenBudget)
    {
        if (tokenBudget <= 0 || selectedEnvelopes.Count == 0)
        {
            return (workingSet, false, null);
        }
        var totalTokens = 0;
        foreach (var envelope in selectedEnvelopes)
        {
            totalTokens += GetEffectiveMaterialTokens(envelope, workingSet.Materials);
        }

        if (totalTokens <= tokenBudget)
        {
            return (workingSet, false, null);
        }

        // 预算超限：按 FinalScore 升序裁减低分 Material（mandatory / hard constraint 不裁剪）。
        // CandidateId 作为 tie-break 保证确定性（与 Engine 排序约定一致）。
        var trimmable = selectedEnvelopes
            .Where(e => !e.Safety.IsMandatory && !e.Safety.IsHardConstraint)
            .OrderBy(e => e.Utility.FinalScore)
            .ThenBy(e => e.CandidateId, StringComparer.Ordinal);

        var repairedMaterials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(workingSet.Materials, comparer: null);
        List<string>? diagnostics = null;
        var remaining = totalTokens;

        foreach (var envelope in trimmable)
        {
            if (remaining <= tokenBudget)
            {
                break;
            }

            // 只裁剪实际持有正文的 Material（无 Material / 空 Content 的候选本来就不占预算）
            if (!repairedMaterials.TryGetValue(envelope.CanonicalKey, out var material)
                || string.IsNullOrEmpty(material.Content))
            {
                continue;
            }
            var tokens = GetEffectiveMaterialTokens(envelope, repairedMaterials);
            repairedMaterials.Remove(envelope.CanonicalKey);
            remaining -= tokens;
            diagnostics ??= new List<string>();
            diagnostics.Add(
                "trimmed:" + envelope.CanonicalKey.EntityKind + "/" + envelope.CanonicalKey.EntityId
                + " score=" + envelope.Utility.FinalScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                + " tokens=" + tokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (diagnostics is null)
        {
            // 无可裁剪项（全部 mandatory / hard constraint 或无正文）→ 预算仍超出，WorkingSet 原样返回
            return (workingSet, true, null);
        }

        return (workingSet with { Materials = repairedMaterials }, remaining > tokenBudget, diagnostics);
    }

    /// <summary>
    /// 获取选中候选的有效 token 数。hydrate 后的 Material.TokenCost 优先（基于真实正文重算），
    /// 回退到 envelope.TokenCost（recall 阶段估算）；两者都缺失时计 0（无正文不占预算）。
    /// </summary>
    private static int GetEffectiveMaterialTokens(
        ContextCandidateEnvelope envelope,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> materials)
    {
        if (materials.TryGetValue(envelope.CanonicalKey, out var material)
            && material.TokenCost is not null
            && !string.IsNullOrEmpty(material.Content))
        {
            return material.TokenCost.ContentTokens;
        }

        return envelope.TokenCost?.ContentTokens ?? 0;
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
