using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;

namespace ContextCore.Core.Services.Retrieval;

/// <summary>第一版混合检索器：规则召回、向量召回、关系扩展、去重和预算打包。</summary>
public sealed class HybridContextRetriever : IContextRetriever
{
    private readonly IRetrievalChannelExecutor _contextRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _mandatoryRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _memoryRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _relationRecallChannelExecutor;
    private readonly RetrievalResultAssembler _resultAssembler;
    private readonly IRetrievalTraceStore? _traceStore;
    private readonly IDecisionTraceStore? _decisionTraceStore;
    private readonly RetrievalTraceAssembler _traceAssembler;
    private readonly IRetrievalChannelExecutor _vectorRecallChannelExecutor;

    // 自动计划器（无状态，可安全静态共享）
    private static readonly RetrievalPlanner AutoPlanner = new();

    public HybridContextRetriever(
        IContextStore contextStore,
        IMemoryStore? memoryStore = null,
        IRelationStore? relationStore = null,
        IEmbeddingProvider? embeddingProvider = null,
        IVectorStore? vectorStore = null,
        IRetrievalTraceStore? traceStore = null,
        IDecisionTraceStore? decisionTraceStore = null)
    {
        _traceStore = traceStore;
        _decisionTraceStore = decisionTraceStore;
        var contextObjectResolver = new DefaultContextObjectResolver(contextStore, memoryStore);
        var relationFrontierBuilder = new RelationFrontierBuilder();
        var relationExpansionService = relationStore is null
            ? null
            : new RelationExpansionService(new RelationTraversalEngine(relationStore), contextObjectResolver);
        _mandatoryRecallChannelExecutor = new MandatoryRecallChannelExecutor(contextStore, memoryStore);
        _contextRecallChannelExecutor = new ContextRecallChannelExecutor(contextStore);
        _memoryRecallChannelExecutor = new MemoryRecallChannelExecutor(memoryStore);
        _vectorRecallChannelExecutor = new VectorRecallChannelExecutor(contextStore, memoryStore, embeddingProvider, vectorStore);
        _relationRecallChannelExecutor = new RelationRecallChannelExecutor(relationFrontierBuilder, relationExpansionService);
        _traceAssembler = new RetrievalTraceAssembler();
        _resultAssembler = new RetrievalResultAssembler();
    }

    public async Task<ContextRetrievalResult> RetrieveAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var _sw = System.Diagnostics.Stopwatch.StartNew();
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;
        var candidates = new RetrievalCandidateAccumulator();
        var relOnlyCandidates = new RetrievalCandidateAccumulator();  // 独立关系扩展通道
        var stages = new List<ContextRetrievalStageTrace>();
        var metadata = new Dictionary<string, string>();

        // 短期锚定召回计划：外部传入则直接使用，否则从请求元数据自动派生（plan 始终非 null）
        var effectivePlan = request.Plan ?? AutoPlanner.Plan(request);

        var mandatoryContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
        var mandatoryResult = await _mandatoryRecallChannelExecutor.ExecuteAsync(mandatoryContext, cancellationToken).ConfigureAwait(false);
        candidates.AddOrMerge(mandatoryResult);
        stages.Add(CreateStageTrace(mandatoryResult));
        if (request.IncludeKeywordRecall)
        {
            var keywordContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
            var keywordResult = await _contextRecallChannelExecutor.ExecuteAsync(keywordContext, cancellationToken).ConfigureAwait(false);
            candidates.AddOrMerge(keywordResult);
            stages.Add(CreateStageTrace(keywordResult));

            var memoryContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
            var memoryResult = await _memoryRecallChannelExecutor.ExecuteAsync(memoryContext, cancellationToken).ConfigureAwait(false);
            candidates.AddOrMerge(memoryResult);
            stages.Add(CreateStageTrace(memoryResult));
        }

        if (request.IncludeVectorRecall)
        {
            var vectorContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
            var vectorResult = await _vectorRecallChannelExecutor.ExecuteAsync(vectorContext, cancellationToken).ConfigureAwait(false);
            candidates.AddOrMerge(vectorResult);
            stages.Add(CreateStageTrace(vectorResult));
        }

        if (request.IncludeRelationExpansion && request.RelationExpansionDepth > 0)
        {
            var relationContext = RetrievalChannelContext.Create(
                request,
                effectivePlan,
                metadata,
                candidates.ToCandidates(includeContent: false));
            var relationResult = await _relationRecallChannelExecutor.ExecuteAsync(relationContext, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in relationResult.Candidates)
            {
                if (candidates.Contains(candidate.Kind, candidate.SourceId))
                {
                    candidates.AddOrMerge(candidate);
                }
                else
                {
                    relOnlyCandidates.AddOrMerge(candidate);
                }
            }

            stages.Add(CreateStageTrace(relationResult));
        }

        // 合并主通道与关系扩展通道：为关系独有条目预留保证槽位后，全量按分数排序
        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            request,
            candidates.ToCandidates(request.IncludeContent),
            relOnlyCandidates.ToCandidates(request.IncludeContent));

        var packed = RetrievalPackingPolicy.Pack(request, ranked);
        var effectivePacked = packed;

        var trace = _traceAssembler.Assemble(
            operationId,
            request,
            stages,
            ranked,
            effectivePacked,
            metadata);

        if (_traceStore is not null)
        {
            try
            {
                await _traceStore.SaveAsync(trace, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // P5-0.4: retrieval trace 写入失败不得影响正式检索输出。
            }
        }

        var result = _resultAssembler.Assemble(operationId, request, effectivePacked, trace, metadata);
        CoreMetrics.RetrievalDuration.Record(_sw.Elapsed.TotalMilliseconds);

        // V17.0: 投影只读 decision trace，不改变 result。
        if (_decisionTraceStore is not null)
        {
            try
            {
                var decisionRecord = ContextDecisionProjector.ProjectRetrieval(result);
                await _decisionTraceStore.SaveAsync(decisionRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // decision trace 写入失败不得影响正式检索输出。
            }
        }

        return result;
    }

    private static ContextRetrievalStageTrace CreateStageTrace(RetrievalChannelResult result)
    {
        return new ContextRetrievalStageTrace
        {
            Name = result.StageName,
            CandidateCount = result.StageCandidateCount,
            Metadata = result.Metadata
        };
    }
}
