using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Core.Services;

internal interface IRetrievalChannelExecutor
{
    string StageName { get; }

    Task<RetrievalChannelResult> ExecuteAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class MandatoryRecallChannelExecutor : IRetrievalChannelExecutor
{
    private readonly IContextStore _contextStore;
    private readonly IMemoryStore? _memoryStore;
    // 回退路径（非 batch store）的并发上限
    private readonly int _maxReadFanout;

    public MandatoryRecallChannelExecutor(
        IContextStore contextStore,
        IMemoryStore? memoryStore,
        RetrievalFanoutOptions fanout)
    {
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _maxReadFanout = fanout.MaxReadFanout;
    }

    public string StageName => "强制注入";

    public async Task<RetrievalChannelResult> ExecuteAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken = default)
    {
        var requiredIds = context.Request.RequiredIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        if (requiredIds.Length == 0)
        {
            return new RetrievalChannelResult(StageName, 0, Array.Empty<RetrievalChannelCandidate>());
        }

        // 批量查询路径：provider 实现 IContextStoreBatchLookup 时优先使用单次批量查询，
        // 避免 N 次单条 GetAsync 并行导致的锁竞争（FileSystem）或连接池击穿（Postgres）。
        if (_contextStore is IContextStoreBatchLookup batchContextStore)
        {
            return await ResolveMandatoryWithBatchLookupAsync(
                context, requiredIds, batchContextStore, cancellationToken).ConfigureAwait(false);
        }

        // 回退路径：并行单条查询（消除 N+1 串行 await）。
        // 在 Batch API 落地前，用 BoundedFanout 施加 SemaphoreSlim 上限，
        // 避免 VectorTopK=100 / RequiredIds 很多时 Postgres 连接池击穿或 FileSystem 锁竞争加剧。
        var resolved = await BoundedFanout.WhenAllAsync(
            requiredIds,
            async (id, ct) =>
            {
                var item = await _contextStore.GetAsync(
                    context.Request.WorkspaceId,
                    context.Request.CollectionId,
                    id,
                    ct).ConfigureAwait(false);
                if (item is not null)
                {
                    return (Id: id, Candidate: RetrievalChannelCandidate.FromContextItem(
                        channelSource: "mandatory",
                        item,
                        score: 1000,
                        reason: "强制注入",
                        mandatory: true,
                        scoreBreakdown: new Dictionary<string, double> { ["mandatory"] = 1000 }));
                }

                if (_memoryStore is not null)
                {
                    var memory = await _memoryStore.GetAsync(
                        context.Request.WorkspaceId,
                        context.Request.CollectionId,
                        id,
                        ct).ConfigureAwait(false);
                    if (memory is not null)
                    {
                        return (Id: id, Candidate: RetrievalChannelCandidate.FromMemoryItem(
                            channelSource: "mandatory",
                            memory,
                            score: 1000,
                            reason: "强制注入",
                            mandatory: true,
                            scoreBreakdown: new Dictionary<string, double> { ["mandatory"] = 1000 }));
                    }
                }

                return (Id: id, Candidate: (RetrievalChannelCandidate?)null);
            },
            _maxReadFanout,
            cancellationToken).ConfigureAwait(false);

        var channelCandidates = new List<RetrievalChannelCandidate>();
        foreach (var entry in resolved)
        {
            if (entry.Candidate is not null)
            {
                channelCandidates.Add(entry.Candidate);
            }
        }

        return new RetrievalChannelResult(StageName, channelCandidates.Count, channelCandidates);
    }

    /// <summary>
    /// 批量查询路径：先批量查 ContextStore，对 miss 的 id 再批量查 MemoryStore（若支持）或并行单条查。
    /// </summary>
    private async Task<RetrievalChannelResult> ResolveMandatoryWithBatchLookupAsync(
        RetrievalChannelContext context,
        string[] requiredIds,
        IContextStoreBatchLookup batchContextStore,
        CancellationToken cancellationToken)
    {
        var channelCandidates = new List<RetrievalChannelCandidate>(requiredIds.Length);
        var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var items = await batchContextStore.BatchGetAsync(
            context.Request.WorkspaceId,
            context.Request.CollectionId,
            requiredIds,
            cancellationToken).ConfigureAwait(false);

        foreach (var item in items)
        {
            foundIds.Add(item.Id);
            channelCandidates.Add(RetrievalChannelCandidate.FromContextItem(
                channelSource: "mandatory",
                item,
                score: 1000,
                reason: "强制注入",
                mandatory: true,
                scoreBreakdown: new Dictionary<string, double> { ["mandatory"] = 1000 }));
        }

        // 对 ContextStore miss 的 id 查 MemoryStore
        var missedIds = requiredIds.Where(id => !foundIds.Contains(id)).ToArray();
        if (missedIds.Length > 0 && _memoryStore is not null)
        {
            if (_memoryStore is IMemoryStoreBatchLookup batchMemoryStore)
            {
                var memories = await batchMemoryStore.BatchGetAsync(
                    context.Request.WorkspaceId,
                    context.Request.CollectionId,
                    missedIds,
                    cancellationToken).ConfigureAwait(false);

                foreach (var memory in memories)
                {
                    channelCandidates.Add(RetrievalChannelCandidate.FromMemoryItem(
                        channelSource: "mandatory",
                        memory,
                        score: 1000,
                        reason: "强制注入",
                        mandatory: true,
                        scoreBreakdown: new Dictionary<string, double> { ["mandatory"] = 1000 }));
                }
            }
            else
            {
                // MemoryStore 不支持批量，回退到带节流的并行单条查询
                var memories = await BoundedFanout.WhenAllAsync(
                    missedIds,
                    (id, ct) => _memoryStore.GetAsync(
                        context.Request.WorkspaceId,
                        context.Request.CollectionId,
                        id,
                        ct),
                    _maxReadFanout,
                    cancellationToken).ConfigureAwait(false);

                foreach (var memory in memories.Where(m => m is not null))
                {
                    channelCandidates.Add(RetrievalChannelCandidate.FromMemoryItem(
                        channelSource: "mandatory",
                        memory!,
                        score: 1000,
                        reason: "强制注入",
                        mandatory: true,
                        scoreBreakdown: new Dictionary<string, double> { ["mandatory"] = 1000 }));
                }
            }
        }

        return new RetrievalChannelResult(StageName, channelCandidates.Count, channelCandidates);
    }
}

internal sealed class ContextRecallChannelExecutor : IRetrievalChannelExecutor
{
    private readonly IContextStore _contextStore;

    public ContextRecallChannelExecutor(IContextStore contextStore)
    {
        _contextStore = contextStore;
    }

    public string StageName => "关键词召回";

    public async Task<RetrievalChannelResult> ExecuteAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken = default)
    {
        var channelCandidates = new List<RetrievalChannelCandidate>();
        var rawItems = await _contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = context.Request.WorkspaceId,
            CollectionId = context.Request.CollectionId,
            QueryText = context.QueryText,
            Tags = context.Request.RequiredTags,
            Types = context.Request.RequiredTypes,
            Refs = context.Request.Refs,
            Take = context.CandidateTake,
            IncludeContent = true
        }, cancellationToken).ConfigureAwait(false);

        foreach (var item in rawItems)
        {
            if (!RetrievalCandidatePolicy.CanUseContextItem(item, context.Plan))
            {
                continue;
            }

            var score = RetrievalCandidatePolicy.ScoreKeywordContext(context.QueryText, item);
            channelCandidates.Add(RetrievalChannelCandidate.FromContextItem(
                channelSource: "keyword",
                item,
                score,
                reason: "关键词/标签/类型/引用召回",
                matchedTokens: RetrievalCandidatePolicy.ExtractMatchedTokens(
                    context.QueryText,
                    item.Title,
                    item.Content,
                    item.Type,
                    item.Tags,
                    item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                scoreBreakdown: new Dictionary<string, double> { ["keyword"] = score }));
        }

        return new RetrievalChannelResult(
            StageName,
            rawItems.Count,
            channelCandidates,
            new Dictionary<string, string>
            {
                ["rawItems"] = rawItems.Count.ToString(),
                ["memoryItems"] = "0"
            });
    }
}

internal sealed class MemoryRecallChannelExecutor : IRetrievalChannelExecutor
{
    private readonly IMemoryStore? _memoryStore;

    public MemoryRecallChannelExecutor(IMemoryStore? memoryStore)
    {
        _memoryStore = memoryStore;
    }

    public string StageName => "记忆召回";

    public async Task<RetrievalChannelResult> ExecuteAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken = default)
    {
        var channelCandidates = new List<RetrievalChannelCandidate>();
        if (_memoryStore is not null && (context.Request.IncludeWorkingMemory || context.Request.IncludeStableMemory))
        {
            var memoryItems = await QueryMemoryCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
            // Memory Recall 与 Keyword Recall 解耦。
            // 不再用 MatchesMemoryQuery 作为硬过滤——记忆条目只要通过 lifecycle 过滤（CanUseMemoryItem）
            // 即可参与评分。查询文本命中只作为加分项（ScoreMemoryCandidate 内部 CalculateTextScore），
            // 不命中时仍保留 base + importance + confidence + anchor bonus。
            // 这与 Package Build 路径（WorkingMemoryRecaller）的 anchor-based 评分语义一致。
            foreach (var memory in memoryItems)
            {
                var score = RetrievalCandidatePolicy.ScoreMemoryCandidate(context.QueryText, memory, context.Plan);
                channelCandidates.Add(RetrievalChannelCandidate.FromMemoryItem(
                    channelSource: "memory",
                    memory,
                    score,
                    reason: "记忆层召回",
                    matchedTokens: RetrievalCandidatePolicy.ExtractMatchedTokens(
                        context.QueryText,
                        title: null,
                        memory.Content,
                        memory.Type,
                        memory.Tags,
                        memory.SourceRefs),
                    matchedAnchors: RetrievalCandidatePolicy.ExtractMatchedPrimaryAnchors(context.Plan, memory),
                    scoreBreakdown: new Dictionary<string, double> { ["memory"] = score }));
            }
        }

        return new RetrievalChannelResult(
            StageName,
            channelCandidates.Count,
            channelCandidates,
            new Dictionary<string, string>
            {
                ["rawItems"] = "0",
                ["memoryItems"] = channelCandidates.Count.ToString()
            });
    }

    private async Task<IReadOnlyList<ContextMemoryItem>> QueryMemoryCandidatesAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken)
    {
        if (_memoryStore is null)
        {
            return Array.Empty<ContextMemoryItem>();
        }

        // 并行执行 Working 和 Stable 查询，消除串行 await。
        var workingTask = context.Request.IncludeWorkingMemory
            ? _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = context.Request.WorkspaceId,
                CollectionId = context.Request.CollectionId,
                Layer = ContextMemoryLayer.Working,
                Tags = context.Request.RequiredTags,
                Types = context.Request.RequiredTypes,
                SourceRefs = context.Request.Refs,
                Take = context.CandidateTake
            }, cancellationToken)
            : Task.FromResult<IReadOnlyList<ContextMemoryItem>>(Array.Empty<ContextMemoryItem>());

        var stableTask = context.Request.IncludeStableMemory && !RetrievalPlanExecutionPolicy.SuppressStableMemory(context.Plan)
            ? _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = context.Request.WorkspaceId,
                CollectionId = context.Request.CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Tags = context.Request.RequiredTags,
                Types = context.Request.RequiredTypes,
                SourceRefs = context.Request.Refs,
                Take = context.CandidateTake
            }, cancellationToken)
            : Task.FromResult<IReadOnlyList<ContextMemoryItem>>(Array.Empty<ContextMemoryItem>());

        await Task.WhenAll(workingTask, stableTask).ConfigureAwait(false);

        var allowDeprecated = RetrievalPlanExecutionPolicy.AllowDeprecated(context.Plan);
        var workingItems = workingTask.Result
            .Where(item => RetrievalCandidatePolicy.CanUseMemoryItem(item, allowDeprecated))
            .ToArray();
        var stableItems = stableTask.Result
            .Where(item => RetrievalCandidatePolicy.CanUseMemoryItem(item, allowDeprecated))
            .ToArray();

        // per-layer quota prevents Working Memory from saturating the candidate budget.
        // 旧实现按 Working → Stable → Distinct → Take(candidateTake) 顺序追加并截取，
        // 当 Working 返回数 >= candidateTake 时 Stable 会被全部截掉，违反两层共存的语义。
        // 新策略：Working 与 Stable 各保留至少一半配额，未填满层的剩余配额滚动给另一层，
        // 合并后再按 Id 去重（Working 优先）。
        var workingQuota = Math.Max(1, context.CandidateTake / 2);
        var stableQuota = context.CandidateTake - workingQuota;

        var takenWorking = workingItems.Take(workingQuota).ToArray();
        var takenStable = stableItems.Take(stableQuota).ToArray();

        // Rollover：若 Working 未填满自身配额，把剩余 slot 给 Stable；反之亦然。
        // 注意：只在另一层未被先满足时才滚动，避免双向 rollover 死循环。
        if (takenWorking.Length < workingQuota)
        {
            var rollover = workingQuota - takenWorking.Length;
            takenStable = stableItems.Take(stableQuota + rollover).ToArray();
        }
        else if (takenStable.Length < stableQuota)
        {
            var rollover = stableQuota - takenStable.Length;
            takenWorking = workingItems.Take(workingQuota + rollover).ToArray();
        }

        return takenWorking
            .Concat(takenStable)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class VectorRecallChannelExecutor : IRetrievalChannelExecutor
{
    private readonly IContextStore _contextStore;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IMemoryStore? _memoryStore;
    private readonly IVectorStore? _vectorStore;
    // hydration 回退路径的并发上限
    private readonly int _maxReadFanout;

    public VectorRecallChannelExecutor(
        IContextStore contextStore,
        IMemoryStore? memoryStore,
        IEmbeddingProvider? embeddingProvider,
        IVectorStore? vectorStore,
        RetrievalFanoutOptions fanout)
    {
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _maxReadFanout = fanout.MaxReadFanout;
    }

    public string StageName => "向量召回";

    public async Task<RetrievalChannelResult> ExecuteAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Request.IncludeVectorRecall)
        {
            return new RetrievalChannelResult(
                StageName,
                0,
                Array.Empty<RetrievalChannelCandidate>(),
                new Dictionary<string, string> { ["skipped"] = "vector recall disabled" });
        }

        if (_vectorStore is null)
        {
            return new RetrievalChannelResult(
                StageName,
                0,
                Array.Empty<RetrievalChannelCandidate>(),
                new Dictionary<string, string> { ["skipped"] = "未注册 IVectorStore" });
        }

        var queryVector = await ResolveQueryVectorAsync(context, cancellationToken).ConfigureAwait(false);
        if (queryVector.Count == 0)
        {
            return new RetrievalChannelResult(
                StageName,
                0,
                Array.Empty<RetrievalChannelCandidate>(),
                new Dictionary<string, string> { ["skipped"] = "没有查询向量，且无法生成 query embedding" });
        }

        var hits = await _vectorStore.SearchAsync(new VectorQuery
        {
            WorkspaceId = context.Request.WorkspaceId,
            CollectionId = context.Request.CollectionId,
            Vector = queryVector,
            TopK = context.Request.VectorTopK > 0 ? context.Request.VectorTopK : 20,
            MinScore = context.Request.MinVectorScore,
            SourceKinds = ["context", "contextItem", "memory", "memoryItem"],
            Tags = context.Request.RequiredTags,
            IncludeVector = false
        }, cancellationToken).ConfigureAwait(false);

        // 批量 hydration：按 SourceKind 分组，对支持 BatchGetAsync 的 store 批量查询，
        // 避免 N 次单条 GetAsync 并行导致的锁竞争（FileSystem）或连接池击穿（Postgres）。
        var channelCandidates = await HydrateVectorHitsAsync(context, hits, cancellationToken).ConfigureAwait(false);

        return new RetrievalChannelResult(
            StageName,
            channelCandidates.Count,
            channelCandidates,
            new Dictionary<string, string>
            {
                ["vectorHits"] = hits.Count.ToString()
            });
    }

    private async Task<IReadOnlyList<float>> ResolveQueryVectorAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.QueryVector.Count > 0)
        {
            context.Metadata["queryVectorSource"] = "请求传入";
            return context.Request.QueryVector;
        }

        if (_embeddingProvider is null || string.IsNullOrWhiteSpace(context.QueryText))
        {
            context.Metadata["queryVectorSource"] = "无可用查询向量";
            return Array.Empty<float>();
        }

        // 不再在此处拼接 QueryInstruction，改为通过 EmbeddingInput.Instruction 传递给 Provider，
        // 由 EmbeddingTextComposer 统一负责拼接格式，避免双重 instruction 风险。
        var embedding = await _embeddingProvider.EmbedAsync(new EmbeddingRequest
        {
            OperationId = context.Request.OperationId,
            WorkspaceId = context.Request.WorkspaceId,
            CollectionId = context.Request.CollectionId,
            ModelName = context.Request.ModelName,
            InputKind = EmbeddingInputKind.Query,
            Inputs =
            [
                new EmbeddingInput
                {
                    Id = "query",
                    Text = context.QueryText,
                    Instruction = context.Request.QueryInstruction,
                    SourceRef = "query"
                }
            ],
            Metadata = new Dictionary<string, string>
            {
                ["queryInstruction"] = context.Request.QueryInstruction
            }
        }, cancellationToken).ConfigureAwait(false);

        context.Metadata["queryVectorSource"] = embedding.Succeeded ? "EmbeddingProvider 生成" : "EmbeddingProvider 生成失败";
        context.Metadata["queryEmbeddingModelCalls"] = embedding.Usage.ModelCalls.ToString();
        return embedding.Succeeded && embedding.Vectors.Count > 0
            ? embedding.Vectors[0].Values
            : Array.Empty<float>();
    }

    /// <summary>
    /// 批量 hydration vector hits：按 SourceKind 分组，对支持 BatchGetAsync 的 store 批量查询，
    /// 对不支持的 store 回退到并行单条查询。混合模式支持（ContextStore 支持批量但 MemoryStore 不支持）。
    /// </summary>
    private async Task<List<RetrievalChannelCandidate>> HydrateVectorHitsAsync(
        RetrievalChannelContext context,
        IReadOnlyList<VectorSearchResult> hits,
        CancellationToken cancellationToken)
    {
        var batchContextStore = _contextStore as IContextStoreBatchLookup;
        var batchMemoryStore = _memoryStore as IMemoryStoreBatchLookup;
        // 元数据投影：store 实现 IContextStoreMetadataLookup 时只取元数据（Content 为空），
        // 未选中候选不读正文 jsonb；正文由 Selected 水合阶段按需批量读取（SelectedCandidateContentHydrator）。
        var metadataContextStore = _contextStore as IContextStoreMetadataLookup;
        var metadataMemoryStore = _memoryStore as IMemoryStoreMetadataLookup;

        // 若两个 store 都不支持批量（含元数据投影），回退到并行单条查询
        if (batchContextStore is null && batchMemoryStore is null
            && metadataContextStore is null && metadataMemoryStore is null)
        {
            var fallback = new List<RetrievalChannelCandidate>(hits.Count);
            await HydrateVectorHitsFallbackAsync(context, hits, fallback, cancellationToken).ConfigureAwait(false);
            return fallback;
        }

        // 按 SourceKind 分组
        var contextHits = new List<VectorSearchResult>();
        var memoryHits = new List<VectorSearchResult>();
        foreach (var hit in hits)
        {
            if (IsContextSourceKind(hit.Record.SourceKind))
            {
                contextHits.Add(hit);
            }
            else if (_memoryStore is not null && IsMemorySourceKind(hit.Record.SourceKind))
            {
                memoryHits.Add(hit);
            }
        }

        var results = new List<RetrievalChannelCandidate>(hits.Count);

        // Context hits：优先元数据投影，其次全量批量，最后带节流的并行回退
        if (contextHits.Count > 0)
        {
            if (metadataContextStore is not null)
            {
                await HydrateContextHitsBatchAsync(
                    context, contextHits,
                    (collectionId, ids, ct) => metadataContextStore.BatchGetMetadataAsync(
                        context.Request.WorkspaceId, collectionId, ids, ct),
                    results, cancellationToken).ConfigureAwait(false);
            }
            else if (batchContextStore is not null)
            {
                await HydrateContextHitsBatchAsync(
                    context, contextHits,
                    (collectionId, ids, ct) => batchContextStore.BatchGetAsync(
                        context.Request.WorkspaceId, collectionId, ids, ct),
                    results, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HydrateVectorHitsFallbackAsync(context, contextHits, results, cancellationToken).ConfigureAwait(false);
            }
        }

        // Memory hits：优先元数据投影，其次全量批量，最后带节流的并行回退
        if (memoryHits.Count > 0)
        {
            if (metadataMemoryStore is not null)
            {
                await HydrateMemoryHitsBatchAsync(
                    context, memoryHits,
                    (collectionId, ids, ct) => metadataMemoryStore.BatchGetMetadataAsync(
                        context.Request.WorkspaceId, collectionId, ids, ct),
                    results, cancellationToken).ConfigureAwait(false);
            }
            else if (batchMemoryStore is not null)
            {
                await HydrateMemoryHitsBatchAsync(
                    context, memoryHits,
                    (collectionId, ids, ct) => batchMemoryStore.BatchGetAsync(
                        context.Request.WorkspaceId, collectionId, ids, ct),
                    results, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HydrateVectorHitsFallbackAsync(context, memoryHits, results, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>带节流的并行单条查询回退（store 不支持批量查询时复用）。</summary>
    private async Task HydrateVectorHitsFallbackAsync(
        RetrievalChannelContext context,
        IReadOnlyList<VectorSearchResult> hits,
        List<RetrievalChannelCandidate> results,
        CancellationToken cancellationToken)
    {
        var candidates = await BoundedFanout.WhenAllAsync(
            hits,
            (hit, ct) => CreateVectorHitCandidateAsync(context, hit, ct),
            _maxReadFanout,
            cancellationToken).ConfigureAwait(false);
        foreach (var c in candidates)
        {
            if (c is not null) results.Add(c);
        }
    }

    /// <summary>按 CollectionId 分组批量查询 ContextStore，构造 vector 候选。</summary>
    private async Task HydrateContextHitsBatchAsync(
        RetrievalChannelContext context,
        List<VectorSearchResult> hits,
        Func<string, string[], CancellationToken, Task<IReadOnlyList<ContextItem>>> batchFetch,
        List<RetrievalChannelCandidate> results,
        CancellationToken cancellationToken)
    {
        // 按 effective CollectionId 分组
        var groups = hits.GroupBy(h => h.Record.CollectionId ?? context.Request.CollectionId);
        foreach (var group in groups)
        {
            var collectionId = group.Key;
            var sourceIds = group.Select(h => h.Record.SourceId).ToArray();
            var items = await batchFetch(collectionId, sourceIds, cancellationToken).ConfigureAwait(false);

            // 按 Id 索引命中结果
            var itemDict = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var hit in group)
            {
                if (!itemDict.TryGetValue(hit.Record.SourceId, out var item)
                    || !RetrievalCandidatePolicy.CanUseContextItem(item, context.Plan))
                {
                    continue;
                }

                var score = RetrievalCandidatePolicy.ScoreVectorHit(hit.Score);
                results.Add(RetrievalChannelCandidate.FromContextItem(
                    channelSource: "vector",
                    item,
                    score,
                    reason: $"向量召回 score={hit.Score:0.000}",
                    scoreBreakdown: new Dictionary<string, double> { ["vector"] = score }));
            }
        }
    }

    /// <summary>按 CollectionId 分组批量查询 MemoryStore，构造 vector 候选。</summary>
    private async Task HydrateMemoryHitsBatchAsync(
        RetrievalChannelContext context,
        List<VectorSearchResult> hits,
        Func<string, string[], CancellationToken, Task<IReadOnlyList<ContextMemoryItem>>> batchFetch,
        List<RetrievalChannelCandidate> results,
        CancellationToken cancellationToken)
    {
        var groups = hits.GroupBy(h => h.Record.CollectionId ?? context.Request.CollectionId);
        foreach (var group in groups)
        {
            var collectionId = group.Key;
            var sourceIds = group.Select(h => h.Record.SourceId).ToArray();
            var memories = await batchFetch(collectionId, sourceIds, cancellationToken).ConfigureAwait(false);

            var memDict = memories.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var hit in group)
            {
                if (!memDict.TryGetValue(hit.Record.SourceId, out var memory)
                    || !RetrievalCandidatePolicy.CanUseMemoryItem(memory, context.Plan))
                {
                    continue;
                }

                var score = RetrievalCandidatePolicy.ScoreVectorHit(hit.Score);
                results.Add(RetrievalChannelCandidate.FromMemoryItem(
                    channelSource: "vector",
                    memory,
                    score,
                    reason: $"向量召回 score={hit.Score:0.000}",
                    scoreBreakdown: new Dictionary<string, double> { ["vector"] = score }));
            }
        }
    }

    private async Task<RetrievalChannelCandidate?> CreateVectorHitCandidateAsync(
        RetrievalChannelContext context,
        VectorSearchResult hit,
        CancellationToken cancellationToken)
    {
        var score = RetrievalCandidatePolicy.ScoreVectorHit(hit.Score);
        var sourceKind = hit.Record.SourceKind;
        if (IsContextSourceKind(sourceKind))
        {
            var item = await _contextStore.GetAsync(
                context.Request.WorkspaceId,
                hit.Record.CollectionId ?? context.Request.CollectionId,
                hit.Record.SourceId,
                cancellationToken).ConfigureAwait(false);
            if (item is null || !RetrievalCandidatePolicy.CanUseContextItem(item, context.Plan))
            {
                return null;
            }

            return RetrievalChannelCandidate.FromContextItem(
                channelSource: "vector",
                item,
                score,
                reason: $"向量召回 score={hit.Score:0.000}",
                scoreBreakdown: new Dictionary<string, double> { ["vector"] = score });
        }

        if (_memoryStore is not null && IsMemorySourceKind(sourceKind))
        {
            var memory = await _memoryStore.GetAsync(
                context.Request.WorkspaceId,
                hit.Record.CollectionId ?? context.Request.CollectionId,
                hit.Record.SourceId,
                cancellationToken).ConfigureAwait(false);
            if (memory is null || !RetrievalCandidatePolicy.CanUseMemoryItem(memory, context.Plan))
            {
                return null;
            }

            return RetrievalChannelCandidate.FromMemoryItem(
                channelSource: "vector",
                memory,
                score,
                reason: $"向量召回 score={hit.Score:0.000}",
                scoreBreakdown: new Dictionary<string, double> { ["vector"] = score });
        }

        return null;
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

internal sealed class RelationRecallChannelExecutor : IRetrievalChannelExecutor
{
    private readonly RelationExpansionService? _relationExpansionService;
    private readonly RelationFrontierBuilder _relationFrontierBuilder;

    public RelationRecallChannelExecutor(
        RelationFrontierBuilder relationFrontierBuilder,
        RelationExpansionService? relationExpansionService)
    {
        _relationFrontierBuilder = relationFrontierBuilder;
        _relationExpansionService = relationExpansionService;
    }

    public string StageName => "关系扩展";

    public async Task<RetrievalChannelResult> ExecuteAsync(
        RetrievalChannelContext context,
        CancellationToken cancellationToken = default)
    {
        if (_relationExpansionService is null)
        {
            return new RetrievalChannelResult(
                StageName,
                0,
                Array.Empty<RetrievalChannelCandidate>(),
                new Dictionary<string, string> { ["skipped"] = "未注册 IRelationStore" });
        }

        var frontier = _relationFrontierBuilder.Build(
            context.Request,
            context.Plan,
            context.CurrentCandidates);
        return await _relationExpansionService.ExpandAsync(
            context.Request.WorkspaceId,
            context.Request.CollectionId,
            frontier,
            cancellationToken).ConfigureAwait(false);
    }
}
