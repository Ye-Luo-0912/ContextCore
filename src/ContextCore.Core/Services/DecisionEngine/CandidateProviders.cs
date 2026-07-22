using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

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
    /// <summary>计算 stable content hash（SHA256 前 16 字符），用作 EntityVersion。</summary>
    internal static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant().AsSpan(0, 16).ToString();
    }

    /// <summary>粗略估算 token 数（~4 chars/token，最小 1）。</summary>
    internal static int EstimateTokens(string content)
    {
        return Math.Max(1, (content?.Length ?? 0) / 4);
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

    /// <summary>从 ContextItem 构建 Envelope + Material。</summary>
    internal static (ContextCandidateEnvelope Envelope, CandidateMaterial Material) BuildFromContextItem(
        ContextItem item,
        ContextCandidateSource source,
        ExpertKind expertKind,
        double score,
        CandidateAdaptationContext adaptationContext)
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
            Content = item.Content,
            NativeKind = string.IsNullOrEmpty(item.Type) ? "context" : item.Type,
            SourceRefs = item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };

        return (envelope, material);
    }

    /// <summary>从 ContextMemoryItem 构建 Envelope + Material。</summary>
    internal static (ContextCandidateEnvelope Envelope, CandidateMaterial Material) BuildFromMemoryItem(
        ContextMemoryItem memory,
        ContextCandidateSource source,
        ExpertKind expertKind,
        double score,
        CandidateAdaptationContext adaptationContext)
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
            Content = memory.Content,
            NativeKind = string.IsNullOrEmpty(memory.Type) ? "memory" : memory.Type,
            SourceRefs = memory.SourceRefs
        };

        return (envelope, material);
    }

    /// <summary>从 ContextConstraint 构建 Envelope + Material。</summary>
    internal static (ContextCandidateEnvelope Envelope, CandidateMaterial Material) BuildFromConstraint(
        ContextConstraint constraint,
        ExpertKind expertKind,
        double score,
        CandidateAdaptationContext adaptationContext)
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

        return (envelope, material);
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
/// V2 模型不使用 RequiredIds（ContextDecisionRuntimeRequest 无此字段），
/// 而是通过 Tags=["mandatory"] 查询标记为强制注入的条目。
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
        var items = await _contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Tags = new[] { "mandatory" },
            Take = take,
            IncludeContent = true
        }, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(items.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(items.Count);

        foreach (var item in items)
        {
            var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                item, ContextCandidateSource.Mandatory, ExpertKind.Mandatory, 1000.0, context.AdaptationContext);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
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

        // 无查询文本时 lexical 召回无意义
        if (string.IsNullOrWhiteSpace(context.Request.QueryText))
        {
            return CandidateProviderHelpers.Empty();
        }

        var take = CandidateProviderHelpers.ResolveTake(context);
        var items = await _contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            QueryText = context.Request.QueryText,
            Take = take,
            IncludeContent = true
        }, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(items.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(items.Count);

        foreach (var item in items)
        {
            // 简单关键词评分：查询文本出现在 Title 中加分
            var score = 10.0;
            if (!string.IsNullOrEmpty(item.Title) &&
                item.Title.Contains(context.Request.QueryText, StringComparison.OrdinalIgnoreCase))
            {
                score += 50.0;
            }
            var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                item, ContextCandidateSource.Lexical, ExpertKind.Lexical, score, context.AdaptationContext);
            envelopes.Add(envelope);
            materials[envelope.CanonicalKey] = material;
        }

        return new ExpertExecutionResult(envelopes, materials);
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
        if (string.IsNullOrWhiteSpace(context.Request.QueryText)) return CandidateProviderHelpers.Empty();

        // 1. 生成 query embedding
        IReadOnlyList<float> queryVector;
        if (_embeddingProvider is not null)
        {
            var embedding = await _embeddingProvider.EmbedAsync(new EmbeddingRequest
            {
                OperationId = context.Request.RequestId,
                WorkspaceId = context.Request.Scope.WorkspaceId,
                CollectionId = context.Request.Scope.CollectionId,
                InputKind = EmbeddingInputKind.Query,
                Inputs =
                [
                    new EmbeddingInput
                    {
                        Id = "query",
                        Text = context.Request.QueryText,
                        SourceRef = "query"
                    }
                ]
            }, cancellationToken).ConfigureAwait(false);

            queryVector = embedding.Succeeded && embedding.Vectors.Count > 0
                ? embedding.Vectors[0].Values
                : Array.Empty<float>();
        }
        else
        {
            return CandidateProviderHelpers.Empty();
        }

        if (queryVector.Count == 0) return CandidateProviderHelpers.Empty();

        // 2. 向量搜索
        var topK = CandidateProviderHelpers.ResolveTake(context);
        var hits = await _vectorStore.SearchAsync(new VectorQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Vector = queryVector,
            TopK = topK,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);

        if (hits.Count == 0) return CandidateProviderHelpers.Empty();

        // 3. Hydration：按 SourceKind 分组
        var envelopes = new List<ContextCandidateEnvelope>(hits.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(hits.Count);
        var seenKeys = new HashSet<CanonicalCandidateKey>();

        foreach (var hit in hits)
        {
            var score = Math.Max(0.0, hit.Score) * 100.0;
            var collectionId = hit.Record.CollectionId ?? context.Request.Scope.CollectionId;

            if (IsContextSourceKind(hit.Record.SourceKind))
            {
                var item = await _contextStore.GetAsync(
                    context.Request.Scope.WorkspaceId,
                    collectionId,
                    hit.Record.SourceId,
                    cancellationToken).ConfigureAwait(false);

                if (item is null) continue;

                var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                    item, ContextCandidateSource.Semantic, ExpertKind.Semantic, score, context.AdaptationContext);

                if (seenKeys.Add(envelope.CanonicalKey))
                {
                    envelopes.Add(envelope);
                    materials[envelope.CanonicalKey] = material;
                }
            }
            else if (_memoryStore is not null && IsMemorySourceKind(hit.Record.SourceKind))
            {
                var memory = await _memoryStore.GetAsync(
                    context.Request.Scope.WorkspaceId,
                    collectionId,
                    hit.Record.SourceId,
                    cancellationToken).ConfigureAwait(false);

                if (memory is null) continue;

                var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                    memory, ContextCandidateSource.Semantic, ExpertKind.Semantic, score, context.AdaptationContext);

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

        var take = CandidateProviderHelpers.ResolveTake(context);
        var memories = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = context.Request.Scope.WorkspaceId,
            CollectionId = context.Request.Scope.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Take = take
        }, cancellationToken).ConfigureAwait(false);

        if (memories.Count == 0) return CandidateProviderHelpers.Empty();

        var envelopes = new List<ContextCandidateEnvelope>(memories.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(memories.Count);

        foreach (var memory in memories)
        {
            var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                memory, ContextCandidateSource.WorkingMemory, ExpertKind.WorkingMemory,
                50.0, context.AdaptationContext);
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

        var envelopes = new List<ContextCandidateEnvelope>(memories.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(memories.Count);

        foreach (var memory in memories)
        {
            var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                memory, ContextCandidateSource.StableMemory, ExpertKind.StableMemory,
                80.0, context.AdaptationContext);
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

        var take = CandidateProviderHelpers.ResolveTake(context);
        var workspaceId = context.Request.Scope.WorkspaceId;
        var collectionId = context.Request.Scope.CollectionId;

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

        // 2. 对每个种子查询邻居关系
        var neighborItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seedItemId in seedItemIds)
        {
            var relations = await _relationStore.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ItemId = seedItemId,
                Direction = RelationDirection.Both,
                Take = take
            }, cancellationToken).ConfigureAwait(false);

            foreach (var relation in relations)
            {
                // 提取"另一端"的 ItemId
                var otherId = string.Equals(relation.SourceId, seedItemId, StringComparison.OrdinalIgnoreCase)
                    ? relation.TargetId
                    : relation.SourceId;

                if (!string.IsNullOrEmpty(otherId) && !seedItemIds.Contains(otherId))
                {
                    neighborItemIds.Add(otherId);
                }
            }
        }

        if (neighborItemIds.Count == 0) return CandidateProviderHelpers.Empty();

        // 3. Hydration：从 IContextStore 获取关联条目（回退到 IMemoryStore）
        var envelopes = new List<ContextCandidateEnvelope>(neighborItemIds.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(neighborItemIds.Count);
        var seenKeys = new HashSet<CanonicalCandidateKey>();

        foreach (var itemId in neighborItemIds)
        {
            var item = await _contextStore.GetAsync(
                workspaceId, collectionId, itemId, cancellationToken).ConfigureAwait(false);

            if (item is not null)
            {
                var (envelope, material) = CandidateProviderHelpers.BuildFromContextItem(
                    item, ContextCandidateSource.Graph, ExpertKind.Graph,
                    30.0, context.AdaptationContext);
                if (seenKeys.Add(envelope.CanonicalKey))
                {
                    envelopes.Add(envelope);
                    materials[envelope.CanonicalKey] = material;
                }
                continue;
            }

            if (_memoryStore is not null)
            {
                var memory = await _memoryStore.GetAsync(
                    workspaceId, collectionId, itemId, cancellationToken).ConfigureAwait(false);

                if (memory is not null)
                {
                    var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
                        memory, ContextCandidateSource.Graph, ExpertKind.Graph,
                        30.0, context.AdaptationContext);
                    if (seenKeys.Add(envelope.CanonicalKey))
                    {
                        envelopes.Add(envelope);
                        materials[envelope.CanonicalKey] = material;
                    }
                }
            }
        }

        return new ExpertExecutionResult(envelopes, materials);
    }
}
