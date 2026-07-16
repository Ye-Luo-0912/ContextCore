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
    private int _retrievalTraceWriteFailures;
    private int _decisionTraceWriteFailures;

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

        // Phase 1：独立 Channel 并行执行（mandatory / keyword / memory / vector）。
        // 每个 Channel 持有独立的 metadata 字典，消除共享可变状态的数据竞争。
        var mandatoryMetadata = new Dictionary<string, string>();
        var mandatoryContext = RetrievalChannelContext.Create(request, effectivePlan, mandatoryMetadata);
        var mandatoryChannelTask = _mandatoryRecallChannelExecutor.ExecuteAsync(mandatoryContext, cancellationToken);

        Dictionary<string, string>? keywordMetadata = null;
        Dictionary<string, string>? memoryMetadata = null;
        Task<RetrievalChannelResult>? keywordChannelTask = null;
        Task<RetrievalChannelResult>? memoryChannelTask = null;
        if (request.IncludeKeywordRecall)
        {
            keywordMetadata = new Dictionary<string, string>();
            keywordChannelTask = _contextRecallChannelExecutor.ExecuteAsync(
                RetrievalChannelContext.Create(request, effectivePlan, keywordMetadata),
                cancellationToken);
            memoryMetadata = new Dictionary<string, string>();
            memoryChannelTask = _memoryRecallChannelExecutor.ExecuteAsync(
                RetrievalChannelContext.Create(request, effectivePlan, memoryMetadata),
                cancellationToken);
        }

        Dictionary<string, string>? vectorMetadata = null;
        Task<RetrievalChannelResult>? vectorChannelTask = null;
        if (request.IncludeVectorRecall)
        {
            vectorMetadata = new Dictionary<string, string>();
            vectorChannelTask = _vectorRecallChannelExecutor.ExecuteAsync(
                RetrievalChannelContext.Create(request, effectivePlan, vectorMetadata),
                cancellationToken);
        }

        // 等待所有独立 Channel 完成
        var independentTasks = new List<Task<RetrievalChannelResult>> { mandatoryChannelTask };
        if (keywordChannelTask is not null) independentTasks.Add(keywordChannelTask);
        if (memoryChannelTask is not null) independentTasks.Add(memoryChannelTask);
        if (vectorChannelTask is not null) independentTasks.Add(vectorChannelTask);

        await Task.WhenAll(independentTasks).ConfigureAwait(false);

        // 按确定性顺序收集结果并合并 metadata（含 Channel 内部写入的 context.Metadata）
        var mandatoryResult = mandatoryChannelTask.Result;
        candidates.AddOrMerge(mandatoryResult);
        stages.Add(CreateStageTrace(mandatoryResult));
        MergeMetadata(metadata, mandatoryMetadata);
        MergeMetadata(metadata, mandatoryResult.Metadata);

        if (keywordChannelTask is not null)
        {
            var keywordResult = keywordChannelTask.Result;
            candidates.AddOrMerge(keywordResult);
            stages.Add(CreateStageTrace(keywordResult));
            MergeMetadata(metadata, keywordMetadata!);
            MergeMetadata(metadata, keywordResult.Metadata);

            var memoryResult = memoryChannelTask!.Result;
            candidates.AddOrMerge(memoryResult);
            stages.Add(CreateStageTrace(memoryResult));
            MergeMetadata(metadata, memoryMetadata!);
            MergeMetadata(metadata, memoryResult.Metadata);
        }

        if (vectorChannelTask is not null)
        {
            var vectorResult = vectorChannelTask.Result;
            candidates.AddOrMerge(vectorResult);
            stages.Add(CreateStageTrace(vectorResult));
            MergeMetadata(metadata, vectorMetadata!);
            MergeMetadata(metadata, vectorResult.Metadata);
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

        // 累积失败指标（prior calls）：写入 metadata 供 trace 与 result 消费。
        metadata["retrievalTraceWriteFailures"] = _retrievalTraceWriteFailures.ToString();
        metadata["decisionTraceWriteFailures"] = _decisionTraceWriteFailures.ToString();

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
            catch (Exception)
            {
                // P5-0.4: retrieval trace 写入失败不得影响正式检索输出，但需记录降级指标。
                Interlocked.Increment(ref _retrievalTraceWriteFailures);
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
            catch (Exception)
            {
                // decision trace 写入失败不得影响正式检索输出，但需记录降级指标。
                Interlocked.Increment(ref _decisionTraceWriteFailures);
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

    // 合并 Channel 独立 metadata 字典到全局 metadata。
    // 后写入的 key 覆盖先写入的（Channel 内部 metadata 优先于 Result.Metadata）。
    private static void MergeMetadata(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (var kvp in source)
        {
            target[kvp.Key] = kvp.Value;
        }
    }
}
