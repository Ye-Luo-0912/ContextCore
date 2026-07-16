using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;

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

    public MandatoryRecallChannelExecutor(
        IContextStore contextStore,
        IMemoryStore? memoryStore)
    {
        _contextStore = contextStore;
        _memoryStore = memoryStore;
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

        // 并行解析所有 RequiredId，消除 N+1 串行 await。
        // 每个 id 内部仍按 ContextStore → MemoryStore 顺序，但不同 id 之间并行。
        var resolved = await Task.WhenAll(
            requiredIds.Select(async id =>
            {
                var item = await _contextStore.GetAsync(
                    context.Request.WorkspaceId,
                    context.Request.CollectionId,
                    id,
                    cancellationToken).ConfigureAwait(false);
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
                        cancellationToken).ConfigureAwait(false);
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
            })).ConfigureAwait(false);

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
            foreach (var memory in memoryItems.Where(item => RetrievalCandidatePolicy.MatchesMemoryQuery(item, context.QueryText)))
            {
                var score = RetrievalCandidatePolicy.ScoreKeywordMemory(context.QueryText, memory, context.Plan);
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
        var results = new List<ContextMemoryItem>();
        results.AddRange(workingTask.Result);
        results.AddRange(stableTask.Result);

        var allowDeprecated = RetrievalPlanExecutionPolicy.AllowDeprecated(context.Plan);
        return results
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(item => RetrievalCandidatePolicy.CanUseMemoryItem(item, allowDeprecated))
            .Take(context.CandidateTake)
            .ToArray();
    }
}

internal sealed class VectorRecallChannelExecutor : IRetrievalChannelExecutor
{
    private readonly IContextStore _contextStore;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IMemoryStore? _memoryStore;
    private readonly IVectorStore? _vectorStore;

    public VectorRecallChannelExecutor(
        IContextStore contextStore,
        IMemoryStore? memoryStore,
        IEmbeddingProvider? embeddingProvider,
        IVectorStore? vectorStore)
    {
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
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

        // 并行 hydration 所有 hit，消除 N+1 串行 await。
        var candidates = await Task.WhenAll(
            hits.Select(hit => CreateVectorHitCandidateAsync(context, hit, cancellationToken))).ConfigureAwait(false);
        var channelCandidates = new List<RetrievalChannelCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate is not null)
            {
                channelCandidates.Add(candidate);
            }
        }

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

        var embeddingText = string.IsNullOrWhiteSpace(context.Request.QueryInstruction)
            ? context.QueryText
            : context.Request.QueryInstruction + context.QueryText;
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
                    Text = embeddingText,
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
