using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B.6 Closure Gate §B：真实 ICandidateProvider 实现
//
// 目标：把 B-1 的空集合注册替换为真正从 Store 召回候选的 Provider 网络。
// 每个 Provider 对应一个 ExpertKind，注入对应 Store，产出 Envelope + Material 二元组。
//
// 设计原则：
//   1. Provider 只负责召回（recall），不负责评分或分配 — 评分由 Engine 的 UtilityScorer
//      执行，分配由 Engine 的 GlobalAllocator 执行。
//   2. Provider 产出 CanonicalKey + Material sidecar，正文与决策分离。
//   3. 可选 Store（IMemoryStore / IRelationStore / IVectorStore / IConstraintStore）为 null 时
//      返回空结果，不抛异常 — Enabled mask 由 Router 控制，Provider 自身不做 mask 判断。
//   4. EntityVersion 使用 stable content hash（SHA256 前 16 字符），保证同一内容跨请求
//      产出相同 CanonicalKey，使 CanonicalMerger 能正确去重。
//   5. EstimatedTokens 使用 content.Length / 4 的粗略估计（~4 chars/token）。
// ===========================================================================

/// <summary>
/// R28-B.6：Provider 共享 helper。封装 CanonicalKey 构建、content hash、token 估计等通用逻辑。
/// </summary>
internal static class CandidateProviderHelpers
{
    /// <summary>并行回退路径的默认读并发上限，避免 store 不支持批量时击穿连接池。</summary>
    internal const int DefaultReadFanout = 16;

    /// <summary>计算 stable content hash（SHA256 前 16 字符），用作 EntityVersion。</summary>
    internal static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        // 用 stackalloc + chars 减少分配：避免 ToHexString 产生的大写字符串再 ToLowerInvariant 拷贝
        Span<char> hex = stackalloc char[32];
        Convert.ToHexString(hash).AsSpan().ToLowerInvariant(hex);
        return string.Concat("sha256:", hex.Slice(0, 16));
    }

    /// <summary>粗略估算 token 数（~4 chars/token，最小 1）。</summary>
    internal static int EstimateTokens(string content)
    {
        return Math.Max(1, (content?.Length ?? 0) / 4);
    }

    /// <summary>
    /// R28-D P0-3：使用 tokenizer 精确计算候选正文 token 数，填充 envelope.TokenCost。
    /// </summary>
    /// <param name="envelope">待填充的 envelope（TokenCost 字段会被设置）。</param>
    /// <param name="material">对应的 Material（提供正文）。</param>
    /// <param name="tokenizerResolver">tokenizer 解析器（null 时 TokenCost 也基于 length/4 但 IsEstimated=true）。</param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    /// <remarks>
    /// 即使 tokenizerResolver 为 null 也填充 TokenCost（IsEstimated=true），
    /// 让 Allocator 始终消费 TokenCost 而非裸 EstimatedTokens，保证硬不变量 FinalSerializedTokenCount <= TokenBudget。
    /// </remarks>
    internal static ContextCandidateEnvelope EnrichTokenCost(
        ContextCandidateEnvelope envelope,
        CandidateMaterial material,
        Abstractions.IContextTokenizerResolver? tokenizerResolver,
        string? modelName = null)
    {
        if (envelope.TokenCost is not null)
        {
            return envelope; // 已填充，不覆盖
        }

        var content = material?.Content;
        var cost = TokenCostHelper.ComputeTokenCost(content, tokenizerResolver, modelName);
        return envelope with { TokenCost = cost };
    }

    /// <summary>构建空 ExpertExecutionResult。</summary>
    internal static ExpertExecutionResult Empty()
    {
        return new ExpertExecutionResult(
            Array.Empty<ContextCandidateEnvelope>(),
            new Dictionary<CanonicalCandidateKey, CandidateMaterial>());
    }

    /// <summary>从 Routing / Request / Snapshot 解析 Take 上限。</summary>
    internal static int ResolveTake(CandidateProviderContext context)
    {
        if (context.Routing.TopK > 0) return context.Routing.TopK;
        if (context.Request.TopK > 0) return context.Request.TopK;
        return context.Policy.Budget.DefaultTopK;
    }

    /// <summary>从 Routing / Request / Snapshot 解析 TokenBudget 上限。</summary>
    internal static int ResolveTokenBudget(CandidateProviderContext context)
    {
        if (context.Routing.TokenBudget > 0) return context.Routing.TokenBudget;
        if (context.Request.TokenBudget > 0) return context.Request.TokenBudget;
        return context.Policy.Budget.DefaultTokenBudget;
    }

    /// <summary>
    /// R28-B.7-Final：从 RetrievalInput.Plan（序列化 JSON 字符串）解析排除状态列表。
    /// </summary>
    /// <remarks>
    /// Plan 是 RetrievalPlan 的 JSON 序列化字符串，ExcludedStatuses 字段指定应从召回结果中
    /// 排除的条目状态（如 "deprecated"、"rejected"）。Provider 在召回后按此列表过滤。
    /// 解析失败或字段缺失时返回空数组（兼容旧格式 / 非 JSON Plan）。
    /// </remarks>
    internal static IReadOnlyList<string> ResolveExcludedStatusesFromPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(plan);
            if (doc.RootElement.TryGetProperty("ExcludedStatuses", out var statusesEl)
                && statusesEl.ValueKind == JsonValueKind.Array)
            {
                var result = new List<string>(statusesEl.GetArrayLength());
                foreach (var item in statusesEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                    }
                }
                return result;
            }
        }
        catch (JsonException)
        {
            // Plan 不是有效 JSON 时忽略（兼容旧格式）
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// R28-B.7-Final：应用 Plan 的 ExcludedStatuses 过滤到记忆条目列表。
    /// </summary>
    /// <remarks>
    /// 从 Plan 解析排除状态列表，过滤掉 Status 匹配的记忆条目（如 "deprecated"、"rejected"）。
    /// 无排除状态时直接返回原列表（避免无谓分配）。
    /// </remarks>
    internal static IReadOnlyList<ContextMemoryItem> ApplyExcludedStatusesFilter(
        IReadOnlyList<ContextMemoryItem> memories,
        string? plan)
    {
        var excludedStatuses = ResolveExcludedStatusesFromPlan(plan);
        if (excludedStatuses.Count == 0) return memories;

        var excludedSet = new HashSet<string>(excludedStatuses, StringComparer.OrdinalIgnoreCase);
        var filtered = new List<ContextMemoryItem>(memories.Count);
        foreach (var memory in memories)
        {
            if (!excludedSet.Contains(memory.Status.ToString()))
            {
                filtered.Add(memory);
            }
        }
        return filtered;
    }

    /// <summary>从 ContextItem 构建 Envelope + Material。</summary>
    /// <param name="includeContent">R28-B.7 P1-1：是否包含候选正文（false 时 Content 置空，尊重 RetrievalInput.IncludeContent）。</param>
    internal static (ContextCandidateEnvelope Envelope, CandidateMaterial Material) BuildFromContextItem(
        ContextItem item,
        ContextCandidateSource source,
        ExpertKind expertKind,
        double score,
        CandidateAdaptationContext adaptationContext,
        bool includeContent = true,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
    {
        var contentHash = ComputeContentHash(item.Content);
        var key = CanonicalCandidateKey.Create(
            workspaceId: item.WorkspaceId,
            collectionId: item.CollectionId,
            entityKind: "context",
            entityId: item.Id,
            entityVersion: item.Version > 0 ? item.Version.ToString() : contentHash);

        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = $"{expertKind}:{item.Id}",
            Source = source,
            Type = item.Type,
            EstimatedTokens = EstimateTokens(item.Content),
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CanonicalKey = key,
            Features = BuildFeatureVector(source, score),
            Safety = new CandidateSafetyState
            {
                IsMandatory = source == ContextCandidateSource.Mandatory,
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ModelConfidence = 0.0,
                ReasonCode = "provider-recall"
            },
            Origins = new[]
            {
                new ExpertOrigin(expertKind, score, adaptationContext.ObservedAt)
            },
            ExpertContributions = new Dictionary<ExpertKind, double> { [expertKind] = score },
            ProvenanceRefs = BuildProvenanceRefs(item.SourceRefs, adaptationContext)
        };

        var material = new CandidateMaterial
        {
            Key = key,
            // R28-B.7 P1-1：IncludeContent=false 时不输出正文（尊重 RetrievalInput.IncludeContent）
            Content = includeContent ? item.Content : string.Empty,
            NativeKind = string.IsNullOrEmpty(item.Type) ? "context" : item.Type,
            SourceRefs = item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };

        // R28-D P0-3：填充 CandidateTokenCost（使用 tokenizer 精确计算）
        envelope = EnrichTokenCost(envelope, material, tokenizerResolver, tokenizerModelName);

        return (envelope, material);
    }

    /// <summary>从 ContextMemoryItem 构建 Envelope + Material。</summary>
    /// <param name="includeContent">R28-B.7 P1-1：是否包含候选正文（false 时 Content 置空，尊重 RetrievalInput.IncludeContent）。</param>
    internal static (ContextCandidateEnvelope Envelope, CandidateMaterial Material) BuildFromMemoryItem(
        ContextMemoryItem memory,
        ContextCandidateSource source,
        ExpertKind expertKind,
        double score,
        CandidateAdaptationContext adaptationContext,
        bool includeContent = true,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
    {
        var contentHash = ComputeContentHash(memory.Content);
        var key = CanonicalCandidateKey.Create(
            workspaceId: memory.WorkspaceId,
            collectionId: memory.CollectionId,
            entityKind: "memory",
            entityId: memory.Id,
            entityVersion: contentHash);

        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = $"{expertKind}:{memory.Id}",
            Source = source,
            Type = memory.Type,
            EstimatedTokens = EstimateTokens(memory.Content),
            WorkspaceId = memory.WorkspaceId,
            CollectionId = memory.CollectionId,
            CanonicalKey = key,
            Features = BuildFeatureVector(source, score),
            Safety = new CandidateSafetyState
            {
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ModelConfidence = 0.0,
                ReasonCode = "provider-recall"
            },
            Origins = new[]
            {
                new ExpertOrigin(expertKind, score, adaptationContext.ObservedAt)
            },
            ExpertContributions = new Dictionary<ExpertKind, double> { [expertKind] = score },
            ProvenanceRefs = BuildProvenanceRefs(memory.SourceRefs, adaptationContext)
        };

        var material = new CandidateMaterial
        {
            Key = key,
            // R28-B.7 P1-1：IncludeContent=false 时不输出正文（尊重 RetrievalInput.IncludeContent）
            Content = includeContent ? memory.Content : string.Empty,
            NativeKind = string.IsNullOrEmpty(memory.Type) ? "memory" : memory.Type,
            SourceRefs = memory.SourceRefs
        };

        // R28-D P0-3：填充 CandidateTokenCost（使用 tokenizer 精确计算）
        envelope = EnrichTokenCost(envelope, material, tokenizerResolver, tokenizerModelName);

        return (envelope, material);
    }

    /// <summary>从 ContextConstraint 构建 Envelope + Material。</summary>
    internal static (ContextCandidateEnvelope Envelope, CandidateMaterial Material) BuildFromConstraint(
        ContextConstraint constraint,
        ExpertKind expertKind,
        double score,
        CandidateAdaptationContext adaptationContext,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
    {
        var contentHash = ComputeContentHash(constraint.Content);
        var collectionId = constraint.CollectionId ?? string.Empty;
        var key = CanonicalCandidateKey.Create(
            workspaceId: constraint.WorkspaceId,
            collectionId: string.IsNullOrEmpty(collectionId) ? "workspace" : collectionId,
            entityKind: "constraint",
            entityId: constraint.Id,
            entityVersion: contentHash);

        var isHard = constraint.Level == ConstraintLevel.Hard || constraint.Level == ConstraintLevel.System;

        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = $"{expertKind}:{constraint.Id}",
            Source = ContextCandidateSource.Constraint,
            Type = "constraint",
            EstimatedTokens = EstimateTokens(constraint.Content),
            WorkspaceId = constraint.WorkspaceId,
            CollectionId = collectionId,
            CanonicalKey = key,
            Features = BuildFeatureVector(ContextCandidateSource.Constraint, score, isHard),
            Safety = new CandidateSafetyState
            {
                ConstraintLevel = constraint.Level,
                IsMandatory = isHard,
                IsHardConstraint = isHard,
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ModelConfidence = 0.0,
                ReasonCode = "provider-recall"
            },
            Origins = new[]
            {
                new ExpertOrigin(expertKind, score, adaptationContext.ObservedAt)
            },
            ExpertContributions = new Dictionary<ExpertKind, double> { [expertKind] = score },
            ProvenanceRefs = BuildProvenanceRefs(constraint.SourceRefs, adaptationContext)
        };

        var material = new CandidateMaterial
        {
            Key = key,
            Content = constraint.Content,
            NativeKind = "constraint",
            SourceRefs = constraint.SourceRefs
        };

        // R28-D P0-3：填充 CandidateTokenCost（使用 tokenizer 精确计算）
        envelope = EnrichTokenCost(envelope, material, tokenizerResolver, tokenizerModelName);

        return (envelope, material);
    }

    /// <summary>
    /// R28-D P0-2：根据 source / score 构造权威原始特征向量。
    /// </summary>
    /// <remarks>
    /// 这些特征值基于现有 Provider 产出的 score 做合理映射（非真实 BM25/cosine 计算），
    /// 让模型推理输入有非零值。真实 BM25/cosine 需后续在 Store 层暴露原始信号。
    /// ScoreBreakdown 同步写入对应维度键（lexical/semantic/recency/relation/mandatory），
    /// 供 DefaultFeaturePipeline 后续提升或 trace 使用。
    /// </remarks>
    private static CandidateFeatureVector BuildFeatureVector(
        ContextCandidateSource source,
        double score,
        bool isHardConstraint = false)
    {
        double lexical = 0, semantic = 0, recency = 0, relation = 0, mandatory = 0;
        var breakdown = new Dictionary<string, double>(StringComparer.Ordinal);

        switch (source)
        {
            case ContextCandidateSource.Mandatory:
                mandatory = 1.0;
                breakdown["mandatory"] = mandatory;
                break;
            case ContextCandidateSource.Constraint:
                mandatory = isHardConstraint ? 1.0 : 0.0;
                relation = 0.1; // 约束关联性加分
                breakdown["mandatory"] = mandatory;
                breakdown["relation"] = relation;
                break;
            case ContextCandidateSource.Semantic:
                // Provider 传入 score = hit.Score * 100，归一化到 [0,1]
                semantic = score / 100.0;
                breakdown["semantic"] = semantic;
                break;
            case ContextCandidateSource.Lexical:
                // title 命中加 50，基础 10，归一化到 [0,1]
                lexical = Math.Min(1.0, score / 60.0);
                breakdown["lexical"] = lexical;
                break;
            case ContextCandidateSource.WorkingMemory:
                recency = 0.8; // 短期记忆高 recency
                breakdown["recency"] = recency;
                break;
            case ContextCandidateSource.StableMemory:
                recency = 0.3; // 长期记忆低 recency
                breakdown["recency"] = recency;
                break;
            case ContextCandidateSource.Graph:
                // Graph 基础分 30，归一化到 [0,1]
                relation = Math.Min(1.0, score / 30.0);
                breakdown["relation"] = relation;
                break;
            default:
                // Unknown / Recency / GlobalContext / RelatedContext：不填充强类型特征，
                // 由 DefaultFeaturePipeline 后续从 ScoreBreakdown 提升（保持原行为）
                break;
        }

        return new CandidateFeatureVector
        {
            LexicalScore = lexical,
            SemanticScore = semantic,
            RecencyScore = recency,
            RelationBoost = relation,
            MandatoryWeight = mandatory,
            ScoreBreakdown = breakdown
        };
    }

    private static IReadOnlyList<EvidenceRef> BuildProvenanceRefs(
        IReadOnlyList<string> sourceRefs,
        CandidateAdaptationContext context)
    {
        if (sourceRefs.Count == 0) return Array.Empty<EvidenceRef>();
        var refs = new List<EvidenceRef>(sourceRefs.Count);
        foreach (var sr in sourceRefs)
        {
            refs.Add(new EvidenceRef
            {
                RefId = sr,
                RefType = "provider-source",
                WorkspaceId = context.WorkspaceId,
                CollectionId = context.CollectionId,
                GeneratedAt = context.ObservedAt
            });
        }
        return refs;
    }
}

// ---------------------------------------------------------------------------
// MandatoryCandidateProvider — 强制注入候选（tagged mandatory）
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：Mandatory 候选 Provider。从 IContextStore 召回标记为 mandatory 的条目。
/// </summary>
/// <remarks>
/// R28-B.6 P0-1：合并两条召回路径：
///   1. Tags=["mandatory"] 查询标记为强制注入的条目（原有逻辑）。
///   2. RetrievalInput.RequiredIds（或 PackageInput.RequiredIds）强制 ID 召回 —
///      调用方显式要求强制召回的条目，按 ID 逐个 GetAsync 获取。
/// 两条路径结果合并去重。
/// 这些条目的 Safety.IsMandatory=true，Engine 在 Budget Allocator 中无条件保留。
/// </remarks>
public sealed class MandatoryCandidateProvider : ICandidateProvider
{
    private readonly IContextStore _contextStore;

    public MandatoryCandidateProvider(IContextStore contextStore)
    {
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
    }

    public ExpertKind Kind => ExpertKind.Mandatory;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var take = CandidateProviderHelpers.ResolveTake(context);
        var workspaceId = context.Request.Scope.WorkspaceId;
        var collectionId = context.Request.Scope.CollectionId;
        // R28-B.7 P1-1：尊重 RetrievalInput.IncludeContent（默认 true）
        var includeContent = context.Request.RetrievalInput?.IncludeContent ?? true;

        // 路径 1：Tags=["mandatory"] 召回
        var items = await _contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Tags = new[] { "mandatory" },
            Take = take,
            // R28-B.7 P1-1：将 IncludeContent 传递到 Store 查询
            IncludeContent = includeContent
        }, cancellationToken).ConfigureAwait(false);

        // R28-B.6 P0-1：路径 2 — RequiredIds 强制召回（RetrievalInput / PackageInput）
        // R28-B.7 P1-1：Refs 也作为额外 RequiredIds 强制召回
        var requiredIds = ResolveRequiredIds(context.Request);
        var seenIds = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        var mergedItems = items.ToList();

        if (requiredIds.Count > 0)
        {
            // 过滤空 ID 和已见 ID，保留原始顺序去重
            var idsToFetch = new List<string>();
            var fetchSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in requiredIds)
            {
                if (string.IsNullOrEmpty(id) || seenIds.Contains(id) || !fetchSeen.Add(id)) continue;
                idsToFetch.Add(id);
            }

            if (idsToFetch.Count > 0)
            {
                // 批量或并行查询，消除 N+1
                var fetchedDict = new Dictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
                if (_contextStore is IContextStoreBatchLookup batchLookup && idsToFetch.Count > 1)
                {
                    var batchItems = await batchLookup.BatchGetAsync(
                        workspaceId, collectionId, idsToFetch, cancellationToken).ConfigureAwait(false);
                    foreach (var item in batchItems)
                    {
                        if (item is not null) fetchedDict[item.Id] = item;
                    }
                }
                else
                {
                    // 回退到带节流的并行单条查询
                    var fetched = await BoundedFanout.WhenAllAsync(
                        idsToFetch,
                        (id, ct) => _contextStore.GetAsync(workspaceId, collectionId, id, ct),
                        CandidateProviderHelpers.DefaultReadFanout,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var item in fetched)
                    {
                        if (item is not null) fetchedDict[item.Id] = item;
                    }
                }

                // 按 idsToFetch 原始顺序合并（保留原行为）
                foreach (var id in idsToFetch)
                {
                    if (fetchedDict.TryGetValue(id, out var item))
                    {
                        seenIds.Add(id);
                        mergedItems.Add(item);
                    }
                }
            }
        }

        if (mergedItems.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(mergedItems.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(mergedItems.Count);

        foreach (var item in mergedItems)
        {
            var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                item, ContextCandidateSource.Mandatory, ExpertKind.Mandatory, 1000.0,
                context.AdaptationContext, includeContent);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
    }

    /// <summary>
    /// R28-B.6 P0-1：从 Request 解析 RequiredIds（RetrievalInput 优先，回退到 PackageInput）。
    /// R28-B.7 P1-1：Refs 也合并为额外 RequiredIds（强制召回）。
    /// R28-B.7-Final：AgentInput.RequiredIds 也作为回退源（AgentContext 路径 mandatory recall）。
    /// </summary>
    private static IReadOnlyList<string> ResolveRequiredIds(ContextDecisionRuntimeRequest request)
    {
        var ids = new List<string>();
        if (request.RetrievalInput?.RequiredIds is { Count: > 0 } retrievalIds)
        {
            ids.AddRange(retrievalIds);
        }
        // R28-B.7 P1-1：Refs 作为额外 RequiredIds
        if (request.RetrievalInput?.Refs is { Count: > 0 } refs)
        {
            ids.AddRange(refs);
        }
        if (ids.Count > 0) return ids;

        if (request.PackageInput?.RequiredIds is { Count: > 0 } pkgIds)
        {
            return pkgIds;
        }
        // R28-B.7-Final：AgentInput.RequiredIds 回退（AgentContext 路径）
        if (request.AgentInput?.RequiredIds is { Count: > 0 } agentIds)
        {
            return agentIds;
        }
        return Array.Empty<string>();
    }
}

// ---------------------------------------------------------------------------
// ConstraintCandidateProvider — 约束候选
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：Constraint 候选 Provider。从 IConstraintStore 召回当前作用域的有效约束。
/// </summary>
/// <remarks>
/// 查询 workspace + collection 作用域内的所有约束（不按 Level/Status 过滤），
/// SafetyGate 根据 ConstraintLevel 决定是否免预算（Hard/System 免预算，Soft/Mixed 受预算约束）。
/// </remarks>
public sealed class ConstraintCandidateProvider : ICandidateProvider
{
    private readonly IConstraintStore? _constraintStore;

    public ConstraintCandidateProvider(IConstraintStore? constraintStore = null)
    {
        _constraintStore = constraintStore;
    }

    public ExpertKind Kind => ExpertKind.Constraint;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_constraintStore is null) return CandidateProviderHelpers.Empty();

        var take = CandidateProviderHelpers.ResolveTake(context);
        var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Take = take
        }, cancellationToken).ConfigureAwait(false);

        if (constraints.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(constraints.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(constraints.Count);

        foreach (var constraint in constraints)
        {
            var score = constraint.Level == ConstraintLevel.Hard || constraint.Level == ConstraintLevel.System
                ? 1000.0
                : 100.0;
            var (envelope, material) = CandidateProviderHelpers.BuildFromConstraint(
                constraint, ExpertKind.Constraint, score, context.AdaptationContext);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
    }
}

// ---------------------------------------------------------------------------
// LexicalCandidateProvider — 关键词召回
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：Lexical 候选 Provider。从 IContextStore 按 QueryText 进行关键词召回。
/// </summary>
/// <remarks>
/// 使用 ContextQuery.QueryText 进行关键词/全文搜索。
/// 如果 QueryText 为空，返回空结果（无关键词无法做 lexical 召回）。
/// </remarks>
public sealed class LexicalCandidateProvider : ICandidateProvider
{
    private readonly IContextStore _contextStore;

    public LexicalCandidateProvider(IContextStore contextStore)
    {
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
    }

    public ExpertKind Kind => ExpertKind.Lexical;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // R28-B.7-Final：尊重 RetrievalInput.IncludeKeywordRecall（默认 true，false 时跳过关键词召回）
        if (context.Request.RetrievalInput?.IncludeKeywordRecall == false)
        {
            return CandidateProviderHelpers.Empty();
        }

        // R28-B.7 P1-1：优先使用 RewrittenQueryText（query rewriting 后的结果），回退到 QueryText
        var retrievalInput = context.Request.RetrievalInput;
        var effectiveQueryText = retrievalInput?.RewrittenQueryText ?? context.Request.QueryText;

        // 无查询文本且无 Refs 时 lexical 召回无意义
        var hasRefs = retrievalInput?.Refs is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(effectiveQueryText) && !hasRefs)
        {
            return CandidateProviderHelpers.Empty();
        }

        // R28-B.6 P0-1：读取 RetrievalInput 的 RequiredTags / RequiredTypes
        // PackageInput 也提供相同字段（Package 路径下 Lexical 不一定启用，但保留兼容）
        var packageInput = context.Request.PackageInput;
        var requiredTags = retrievalInput?.RequiredTags;
        if (requiredTags is null || requiredTags.Count == 0)
        {
            requiredTags = packageInput?.RequiredTags;
        }
        var requiredTypes = retrievalInput?.RequiredTypes;
        if (requiredTypes is null || requiredTypes.Count == 0)
        {
            requiredTypes = packageInput?.RequiredTypes;
        }

        // R28-B.7 P1-1：尊重 RetrievalInput.IncludeContent（默认 true）
        var includeContent = retrievalInput?.IncludeContent ?? true;

        // R28-B.7 P1-1：从 Metadata 解析排除类型/排除 ID（过滤用）
        var (excludedTypes, excludedIds) = ResolveExcludedFiltersFromMetadata(retrievalInput?.Metadata);

        var take = CandidateProviderHelpers.ResolveTake(context);
        var items = await _contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            QueryText = effectiveQueryText,
            // R28-B.6 P0-1：应用 RequiredTags / RequiredTypes 作为过滤条件
            // （ContextQuery.Tags/Types 默认为空数组，等价于不应用过滤）
            Tags = requiredTags ?? Array.Empty<string>(),
            Types = requiredTypes ?? Array.Empty<string>(),
            // R28-B.7 P1-1：应用 Refs（如果有 Refs，按 Refs 召回）
            Refs = retrievalInput?.Refs ?? Array.Empty<string>(),
            // R28-B.7 P1-1：应用 Metadata 解析的排除过滤
            ExcludedTypes = excludedTypes,
            ExcludedIds = excludedIds,
            Take = take,
            // R28-B.7 P1-1：将 IncludeContent 传递到 Store 查询
            IncludeContent = includeContent
        }, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(items.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(items.Count);

        foreach (var item in items)
        {
            // 简单关键词评分：查询文本出现在 Title 中加分
            var score = 10.0;
            if (!string.IsNullOrEmpty(item.Title) && !string.IsNullOrEmpty(effectiveQueryText) &&
                item.Title.Contains(effectiveQueryText, StringComparison.OrdinalIgnoreCase))
            {
                score += 50.0;
            }
            var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                item, ContextCandidateSource.Lexical, ExpertKind.Lexical, score,
                context.AdaptationContext, includeContent);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
    }

    /// <summary>
    /// R28-B.7 P1-1：从 RetrievalInput.Metadata 解析排除过滤条件。
    /// 支持 "excludedTypes"（逗号分隔）和 "excludedIds"（逗号分隔）两个键。
    /// </summary>
    private static (IReadOnlyList<string> ExcludedTypes, IReadOnlyList<string> ExcludedIds) ResolveExcludedFiltersFromMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        IReadOnlyList<string> excludedTypes = Array.Empty<string>();
        IReadOnlyList<string> excludedIds = Array.Empty<string>();

        if (metadata.TryGetValue("excludedTypes", out var typesStr) && !string.IsNullOrWhiteSpace(typesStr))
        {
            excludedTypes = typesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        if (metadata.TryGetValue("excludedIds", out var idsStr) && !string.IsNullOrWhiteSpace(idsStr))
        {
            excludedIds = idsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return (excludedTypes, excludedIds);
    }
}

// ---------------------------------------------------------------------------
// SemanticCandidateProvider — 向量召回
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：Semantic 候选 Provider。通过 IVectorStore 进行向量召回。
/// </summary>
/// <remarks>
/// 流程：
///   1. 如果 IVectorStore 未注册 → 返回空。
///   2. 从 QueryText 生成 query embedding（通过 IEmbeddingProvider）。
///   3. 向 IVectorStore 发起 SearchAsync。
///   4. 按 SourceKind 分组 hydration：context hits → IContextStore，memory hits → IMemoryStore。
///   5. 返回 Envelope + Material 二元组。
/// </remarks>
public sealed class SemanticCandidateProvider : ICandidateProvider
{
    private readonly IContextStore _contextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IVectorStore? _vectorStore;

    public SemanticCandidateProvider(
        IContextStore contextStore,
        IMemoryStore? memoryStore = null,
        IEmbeddingProvider? embeddingProvider = null,
        IVectorStore? vectorStore = null)
    {
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
        _memoryStore = memoryStore;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
    }

    public ExpertKind Kind => ExpertKind.Semantic;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_vectorStore is null) return CandidateProviderHelpers.Empty();

        // R28-B.7-Final：尊重 RetrievalInput.IncludeVectorRecall（默认 true，false 时跳过向量召回）
        if (context.Request.RetrievalInput?.IncludeVectorRecall == false)
        {
            return CandidateProviderHelpers.Empty();
        }

        // R28-B.6 P0-1：读取 RetrievalInput 中的语义召回参数
        var retrievalInput = context.Request.RetrievalInput;
        var externalQueryVector = retrievalInput?.QueryVector;
        var modelName = retrievalInput?.ModelName;
        var queryInstruction = retrievalInput?.QueryInstruction;
        var vectorTopKFromInput = retrievalInput?.VectorTopK ?? 0;
        var minVectorScore = retrievalInput?.MinVectorScore;
        // R28-B.7 P1-1：尊重 RetrievalInput.IncludeContent（默认 true）
        var includeContent = retrievalInput?.IncludeContent ?? true;

        // R28-B.6 P0-1：如果 QueryVector 非空，直接使用（不调用 EmbeddingProvider）
        IReadOnlyList<float> queryVector;
        if (externalQueryVector is { Count: > 0 } v)
        {
            queryVector = v;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(context.Request.QueryText)) return CandidateProviderHelpers.Empty();

            // R28-B.6 P0-1：未提供 QueryVector 时使用 QueryText + QueryInstruction 调用 EmbeddingProvider
            if (_embeddingProvider is null) return CandidateProviderHelpers.Empty();

            // QueryInstruction 作为 BGE 前缀拼接到 QueryText
            var effectiveQueryText = string.IsNullOrEmpty(queryInstruction)
                ? context.Request.QueryText!
                : queryInstruction + " " + context.Request.QueryText;

            var embedding = await _embeddingProvider.EmbedAsync(new EmbeddingRequest
            {
                OperationId = context.Request.RequestId,
                WorkspaceId = context.Request.Scope.WorkspaceId,
                CollectionId = context.Request.Scope.CollectionId,
                // R28-B.6 P0-1：传 ModelName 给 EmbeddingProvider
                ModelName = modelName,
                InputKind = EmbeddingInputKind.Query,
                Inputs =
                [
                    new EmbeddingInput
                    {
                        Id = "query",
                        Text = effectiveQueryText,
                        SourceRef = "query"
                    }
                ]
            }, cancellationToken).ConfigureAwait(false);

            queryVector = embedding.Succeeded && embedding.Vectors.Count > 0
                ? embedding.Vectors[0].Values
                : Array.Empty<float>();
        }

        if (queryVector.Count == 0) return CandidateProviderHelpers.Empty();

        // 2. 向量搜索
        // R28-B.6 P0-1：VectorTopK 优先于 ResolveTake
        var topK = vectorTopKFromInput > 0
            ? vectorTopKFromInput
            : CandidateProviderHelpers.ResolveTake(context);
        var hits = await _vectorStore.SearchAsync(new VectorQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Vector = queryVector,
            TopK = topK,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);

        if (hits.Count == 0) return CandidateProviderHelpers.Empty();

        // R28-B.6 P0-1：MinVectorScore 过滤（hit.Score < MinVectorScore 的结果剔除）
        if (minVectorScore.HasValue)
        {
            var threshold = minVectorScore.Value;
            hits = hits.Where(h => h.Score >= threshold).ToList();
            if (hits.Count == 0) return CandidateProviderHelpers.Empty();
        }

        // 3. Hydration：按 SourceKind 分组批量查询，消除 N+1
        var envelopes = new List<ContextCandidateEnvelope>(hits.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(hits.Count);
        var seenKeys = new HashSet<CanonicalCandidateKey>();

        var batchContextStore = _contextStore as IContextStoreBatchLookup;
        var batchMemoryStore = _memoryStore as IMemoryStoreBatchLookup;
        var workspaceId = context.Request.Scope.WorkspaceId;
        var defaultCollectionId = context.Request.Scope.CollectionId;

        // 按 CollectionId 分组收集 SourceId（context hits / memory hits 分别处理）
        var contextHitGroups = new Dictionary<string, List<VectorSearchResult>>(StringComparer.OrdinalIgnoreCase);
        var memoryHitGroups = new Dictionary<string, List<VectorSearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            var collId = hit.Record.CollectionId ?? defaultCollectionId;
            if (IsContextSourceKind(hit.Record.SourceKind))
            {
                if (!contextHitGroups.TryGetValue(collId, out var list))
                    contextHitGroups[collId] = list = new List<VectorSearchResult>();
                list.Add(hit);
            }
            else if (_memoryStore is not null && IsMemorySourceKind(hit.Record.SourceKind))
            {
                if (!memoryHitGroups.TryGetValue(collId, out var list))
                    memoryHitGroups[collId] = list = new List<VectorSearchResult>();
                list.Add(hit);
            }
        }

        // 批量查询 context items（按 collectionId 分组）
        var contextItemDict = new Dictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (collId, groupHits) in contextHitGroups)
        {
            var sourceIds = groupHits.Select(h => h.Record.SourceId).ToArray();
            if (batchContextStore is not null)
            {
                var items = await batchContextStore.BatchGetAsync(
                    workspaceId, collId, sourceIds, cancellationToken).ConfigureAwait(false);
                foreach (var item in items)
                {
                    if (item is not null) contextItemDict[item.Id] = item;
                }
            }
            else
            {
                // 回退到带节流的并行单条查询
                var fetched = await BoundedFanout.WhenAllAsync(
                    sourceIds,
                    (id, ct) => _contextStore.GetAsync(workspaceId, collId, id, ct),
                    CandidateProviderHelpers.DefaultReadFanout,
                    cancellationToken).ConfigureAwait(false);
                foreach (var item in fetched)
                {
                    if (item is not null) contextItemDict[item.Id] = item;
                }
            }
        }

        // 批量查询 memory items（按 collectionId 分组）
        var memoryItemDict = new Dictionary<string, ContextMemoryItem>(StringComparer.OrdinalIgnoreCase);
        if (_memoryStore is not null)
        {
            var ms = _memoryStore;
            foreach (var (collId, groupHits) in memoryHitGroups)
            {
                var sourceIds = groupHits.Select(h => h.Record.SourceId).ToArray();
                if (batchMemoryStore is not null)
                {
                    var memories = await batchMemoryStore.BatchGetAsync(
                        workspaceId, collId, sourceIds, cancellationToken).ConfigureAwait(false);
                    foreach (var memory in memories)
                    {
                        if (memory is not null) memoryItemDict[memory.Id] = memory;
                    }
                }
                else
                {
                    // 回退到带节流的并行单条查询
                    var fetched = await BoundedFanout.WhenAllAsync(
                        sourceIds,
                        (id, ct) => ms.GetAsync(workspaceId, collId, id, ct),
                        CandidateProviderHelpers.DefaultReadFanout,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var memory in fetched)
                    {
                        if (memory is not null) memoryItemDict[memory.Id] = memory;
                    }
                }
            }
        }

        // 按 hit 原始顺序构造 envelope（保留原 dedup 行为）
        foreach (var hit in hits)
        {
            var score = Math.Max(0.0, hit.Score) * 100.0;

            if (IsContextSourceKind(hit.Record.SourceKind))
            {
                if (!contextItemDict.TryGetValue(hit.Record.SourceId, out var item)) continue;

                // R28-B.7 P1-1：传递 includeContent 控制候选正文输出
                var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                    item, ContextCandidateSource.Semantic, ExpertKind.Semantic, score, context.AdaptationContext, includeContent);

                if (seenKeys.Add(envelope.CanonicalKey))
                {
                    envelopes.Add(envelope);
                    materials[envelope.CanonicalKey] = material;
                }
            }
            else if (_memoryStore is not null && IsMemorySourceKind(hit.Record.SourceKind))
            {
                if (!memoryItemDict.TryGetValue(hit.Record.SourceId, out var memory)) continue;

                // R28-B.7 P1-1：传递 includeContent 控制候选正文输出
                var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                    memory, ContextCandidateSource.Semantic, ExpertKind.Semantic, score, context.AdaptationContext, includeContent);

                if (seenKeys.Add(envelope.CanonicalKey))
                {
                    envelopes.Add(envelope);
                    materials[envelope.CanonicalKey] = material;
                }
            }
        }

        return new ExpertExecutionResult(envelopes, materials);
    }

    private static bool IsContextSourceKind(string sourceKind)
    {
        return string.Equals(sourceKind, "context", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceKind, "contextItem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMemorySourceKind(string sourceKind)
    {
        return string.Equals(sourceKind, "memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceKind, "memoryItem", StringComparison.OrdinalIgnoreCase);
    }
}

// ---------------------------------------------------------------------------
// WorkingMemoryCandidateProvider — 工作记忆召回
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：WorkingMemory 候选 Provider。从 IMemoryStore 召回 Layer=Working 的记忆条目。
/// </summary>
public sealed class WorkingMemoryCandidateProvider : ICandidateProvider
{
    private readonly IMemoryStore? _memoryStore;

    public WorkingMemoryCandidateProvider(IMemoryStore? memoryStore = null)
    {
        _memoryStore = memoryStore;
    }

    public ExpertKind Kind => ExpertKind.WorkingMemory;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_memoryStore is null) return CandidateProviderHelpers.Empty();

        // R28-B.7-Final：尊重 RetrievalInput.IncludeWorkingMemory（默认 true，false 时跳过工作记忆召回）
        if (context.Request.RetrievalInput?.IncludeWorkingMemory == false)
        {
            return CandidateProviderHelpers.Empty();
        }

        // R28-B.7 P1-1：尊重 RetrievalInput.IncludeContent（默认 true）
        var includeContent = context.Request.RetrievalInput?.IncludeContent ?? true;

        var take = CandidateProviderHelpers.ResolveTake(context);
        var memories = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Take = take
        }, cancellationToken).ConfigureAwait(false);

        if (memories.Count == 0) return CandidateProviderHelpers.Empty();

        // R28-B.7-Final：应用 Plan 的 ExcludedStatuses 过滤（排除废弃/拒绝等状态的记忆）
        memories = CandidateProviderHelpers.ApplyExcludedStatusesFilter(memories, context.Request.RetrievalInput?.Plan);
        if (memories.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(memories.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(memories.Count);

        foreach (var memory in memories)
        {
            // R28-B.7 P1-1：传递 includeContent 控制候选正文输出
            var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                memory, ContextCandidateSource.WorkingMemory, ExpertKind.WorkingMemory,
                50.0, context.AdaptationContext, includeContent);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
    }
}

// ---------------------------------------------------------------------------
// StableMemoryCandidateProvider — 稳定记忆召回
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：StableMemory 候选 Provider。从 IMemoryStore 召回 Layer=Stable + Status=Stable 的记忆条目。
/// </summary>
public sealed class StableMemoryCandidateProvider : ICandidateProvider
{
    private readonly IMemoryStore? _memoryStore;

    public StableMemoryCandidateProvider(IMemoryStore? memoryStore = null)
    {
        _memoryStore = memoryStore;
    }

    public ExpertKind Kind => ExpertKind.StableMemory;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_memoryStore is null) return CandidateProviderHelpers.Empty();

        // R28-B.7-Final：尊重 RetrievalInput.IncludeStableMemory（默认 true，false 时跳过稳定记忆召回）
        if (context.Request.RetrievalInput?.IncludeStableMemory == false)
        {
            return CandidateProviderHelpers.Empty();
        }

        // R28-B.7 P1-1：尊重 RetrievalInput.IncludeContent（默认 true）
        var includeContent = context.Request.RetrievalInput?.IncludeContent ?? true;

        var take = CandidateProviderHelpers.ResolveTake(context);
        var memories = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = take
        }, cancellationToken).ConfigureAwait(false);

        if (memories.Count == 0) return CandidateProviderHelpers.Empty();

        // R28-B.7-Final：应用 Plan 的 ExcludedStatuses 过滤（排除废弃/拒绝等状态的记忆）
        memories = CandidateProviderHelpers.ApplyExcludedStatusesFilter(memories, context.Request.RetrievalInput?.Plan);
        if (memories.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(memories.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(memories.Count);

        foreach (var memory in memories)
        {
            // R28-B.7 P1-1：传递 includeContent 控制候选正文输出
            var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                memory, ContextCandidateSource.StableMemory, ExpertKind.StableMemory,
                80.0, context.AdaptationContext, includeContent);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
    }
}

// ---------------------------------------------------------------------------
// GraphCandidateProvider — 关系图扩展
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6：Graph 候选 Provider。通过 IRelationStore 进行关系图扩展。
/// </summary>
/// <remarks>
/// V2 模型中 Provider 独立并行执行，无法访问其他 Provider 的输出。
/// 因此 Graph Provider 使用 Request.SeedCandidates 作为种子（而非其他 Provider 的输出），
/// 对每个种子的 ItemId 调用 QueryNeighborsAsync，然后 hydration 关联条目。
///
/// 如果 SeedCandidates 为空，返回空结果（无种子无法做图扩展）。
/// </remarks>
public sealed class GraphCandidateProvider : ICandidateProvider
{
    private readonly IRelationStore? _relationStore;
    private readonly IContextStore _contextStore;
    private readonly IMemoryStore? _memoryStore;

    public GraphCandidateProvider(
        IContextStore contextStore,
        IRelationStore? relationStore = null,
        IMemoryStore? memoryStore = null)
    {
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
        _relationStore = relationStore;
        _memoryStore = memoryStore;
    }

    public ExpertKind Kind => ExpertKind.Graph;

    public async ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_relationStore is null) return CandidateProviderHelpers.Empty();
        if (context.Request.SeedCandidates.Count == 0) return CandidateProviderHelpers.Empty();

        // R28-B.7-Final：尊重 RetrievalInput.IncludeRelationExpansion（默认 true，false 时跳过关系图扩展）
        if (context.Request.RetrievalInput?.IncludeRelationExpansion == false)
        {
            return CandidateProviderHelpers.Empty();
        }

        var take = CandidateProviderHelpers.ResolveTake(context);
        var workspaceId = context.Request.Scope.WorkspaceId;
        var collectionId = context.Request.Scope.CollectionId;

        // R28-B.7 P1-1：从 RetrievalInput 读取图扩展参数
        var retrievalInput = context.Request.RetrievalInput;
        // AllowedRelationTypes：为空表示不限制；非空时仅召回这些关系类型
        var allowedRelationTypes = retrievalInput?.AllowedRelationTypes ?? Array.Empty<string>();
        // RelationExpansionDepth：图扩展最大跳数（默认 0 → 视为 1 跳，保持向后兼容）
        var maxDepth = retrievalInput?.RelationExpansionDepth > 0
            ? retrievalInput.RelationExpansionDepth
            : 1;
        // IncludeContent：是否包含候选正文（默认 true）
        var includeContent = retrievalInput?.IncludeContent ?? true;

        // 1. 从 SeedCandidates 提取种子 ItemId
        var seedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in context.Request.SeedCandidates)
        {
            if (!string.IsNullOrEmpty(seed.CanonicalKey.EntityId))
            {
                seedItemIds.Add(seed.CanonicalKey.EntityId);
            }
        }

        if (seedItemIds.Count == 0) return CandidateProviderHelpers.Empty();

        // 2. BFS 多跳扩展：按 maxDepth 迭代，每跳批量查询 frontier 的邻居
        // R28-B.7 P1-1：应用 AllowedRelationTypes 过滤（非空时仅召回允许的关系类型）
        var neighborItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rs = _relationStore;
        var visitedItemIds = new HashSet<string>(seedItemIds, StringComparer.OrdinalIgnoreCase);
        var currentFrontier = seedItemIds.ToList();

        for (var depth = 0; depth < maxDepth && currentFrontier.Count > 0; depth++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (currentFrontier.Count > 1)
            {
                // 批量查询 frontier 的邻居
                var batchResults = await rs.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    ItemIds = currentFrontier.ToArray(),
                    Direction = RelationDirection.Both,
                    // R28-B.7 P1-1：应用 AllowedRelationTypes
                    AllowedRelationTypes = allowedRelationTypes,
                    Take = take
                }, cancellationToken).ConfigureAwait(false);

                foreach (var result in batchResults)
                {
                    var seedItemId = result.ItemId;
                    foreach (var relation in result.Relations)
                    {
                        // 提取"另一端"的 ItemId
                        var otherId = string.Equals(relation.SourceId, seedItemId, StringComparison.OrdinalIgnoreCase)
                            ? relation.TargetId
                            : relation.SourceId;

                        if (!string.IsNullOrEmpty(otherId) && visitedItemIds.Add(otherId))
                        {
                            neighborItemIds.Add(otherId);
                            nextFrontier.Add(otherId);
                        }
                    }
                }
            }
            else
            {
                // 回退到带节流的并行单条查询
                var seedArray = currentFrontier.ToArray();
                var relationsPerSeed = await BoundedFanout.WhenAllAsync(
                    seedArray,
                    (seedItemId, ct) => rs.QueryNeighborsAsync(new RelationNeighborQuery
                    {
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        ItemId = seedItemId,
                        Direction = RelationDirection.Both,
                        // R28-B.7 P1-1：应用 AllowedRelationTypes
                        AllowedRelationTypes = allowedRelationTypes,
                        Take = take
                    }, ct),
                    CandidateProviderHelpers.DefaultReadFanout,
                    cancellationToken).ConfigureAwait(false);

                for (var i = 0; i < relationsPerSeed.Length; i++)
                {
                    var seedItemId = seedArray[i];
                    foreach (var relation in relationsPerSeed[i])
                    {
                        // 提取"另一端"的 ItemId
                        var otherId = string.Equals(relation.SourceId, seedItemId, StringComparison.OrdinalIgnoreCase)
                            ? relation.TargetId
                            : relation.SourceId;

                        if (!string.IsNullOrEmpty(otherId) && visitedItemIds.Add(otherId))
                        {
                            neighborItemIds.Add(otherId);
                            nextFrontier.Add(otherId);
                        }
                    }
                }
            }

            currentFrontier = nextFrontier.ToList();
        }

        if (neighborItemIds.Count == 0) return CandidateProviderHelpers.Empty();

        // 3. Hydration：批量获取关联条目（context store 优先，未命中的回退到 memory store）
        var envelopes = new List<ContextCandidateEnvelope>(neighborItemIds.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(neighborItemIds.Count);
        var seenKeys = new HashSet<CanonicalCandidateKey>();

        var neighborIdList = neighborItemIds.ToArray();

        // 批量查询 context store
        var contextItemDict = new Dictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        var batchContextStore = _contextStore as IContextStoreBatchLookup;
        if (batchContextStore is not null && neighborIdList.Length > 1)
        {
            var items = await batchContextStore.BatchGetAsync(
                workspaceId, collectionId, neighborIdList, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (item is not null) contextItemDict[item.Id] = item;
            }
        }
        else
        {
            // 回退到带节流的并行单条查询
            var fetched = await BoundedFanout.WhenAllAsync(
                neighborIdList,
                (id, ct) => _contextStore.GetAsync(workspaceId, collectionId, id, ct),
                CandidateProviderHelpers.DefaultReadFanout,
                cancellationToken).ConfigureAwait(false);
            foreach (var item in fetched)
            {
                if (item is not null) contextItemDict[item.Id] = item;
            }
        }

        // 收集 context store 未命中的 ID，批量查询 memory store
        var memoryItemDict = new Dictionary<string, ContextMemoryItem>(StringComparer.OrdinalIgnoreCase);
        var missingIds = neighborIdList.Where(id => !contextItemDict.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0 && _memoryStore is not null)
        {
            var ms = _memoryStore;
            var batchMemoryStore = ms as IMemoryStoreBatchLookup;
            if (batchMemoryStore is not null && missingIds.Length > 1)
            {
                var memories = await batchMemoryStore.BatchGetAsync(
                    workspaceId, collectionId, missingIds, cancellationToken).ConfigureAwait(false);
                foreach (var memory in memories)
                {
                    if (memory is not null) memoryItemDict[memory.Id] = memory;
                }
            }
            else
            {
                // 回退到带节流的并行单条查询
                var fetched = await BoundedFanout.WhenAllAsync(
                    missingIds,
                    (id, ct) => ms.GetAsync(workspaceId, collectionId, id, ct),
                    CandidateProviderHelpers.DefaultReadFanout,
                    cancellationToken).ConfigureAwait(false);
                foreach (var memory in fetched)
                {
                    if (memory is not null) memoryItemDict[memory.Id] = memory;
                }
            }
        }

        // 按原始顺序构造 envelope（context 优先，memory 回退）
        foreach (var itemId in neighborIdList)
        {
            if (contextItemDict.TryGetValue(itemId, out var item))
            {
                // R28-B.7 P1-1：传递 includeContent 控制候选正文输出
                var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                    item, ContextCandidateSource.Graph, ExpertKind.Graph,
                    30.0, context.AdaptationContext, includeContent);
                if (seenKeys.Add(envelope.CanonicalKey))
                {
                    envelopes.Add(envelope);
                    materials[envelope.CanonicalKey] = material;
                }
                continue;
            }

            if (memoryItemDict.TryGetValue(itemId, out var memory))
            {
                // R28-B.7 P1-1：传递 includeContent 控制候选正文输出
                var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                    memory, ContextCandidateSource.Graph, ExpertKind.Graph,
                    30.0, context.AdaptationContext, includeContent);
                if (seenKeys.Add(envelope.CanonicalKey))
                {
                    envelopes.Add(envelope);
                    materials[envelope.CanonicalKey] = material;
                }
            }
        }

        return new ExpertExecutionResult(envelopes, materials);
    }
}
