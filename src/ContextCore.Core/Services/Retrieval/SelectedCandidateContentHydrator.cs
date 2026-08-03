using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Core.Services.Retrieval;

/// <summary>
/// Selected 候选正文批量水合（HybridContextRetriever 路径）。
/// <para>
/// 探测语义与 <see cref="DefaultSelectedCandidateHydrator"/> 一致：对已解析的 store
/// 执行 <c>as IContextStoreBatchLookup</c> / <c>as IMemoryStoreBatchLookup</c>——
/// 实现了批量水合能力则单次存储访问批量返回；未实现时保持候选正文为空（调用方降级消费）。
/// </para>
/// <para>
/// 设计原则：
/// <list type="bullet">
/// <item>仅对 Pack 选中的候选做 I/O——向量通道在召回阶段走元数据投影（Content 为空），
/// 未选中候选不读正文 jsonb。</item>
/// <item>正文填充后按真实内容重算 token（tokenizer 可用时精确，否则 length/4），
/// 与全量召回路径的输出保持一致。</item>
/// <item>纯只读路径：不写库、不改变候选决策，仅填充 Content 并同步决策的 token 估算。</item>
/// </list>
/// </para>
/// </summary>
internal static class SelectedCandidateContentHydrator
{
    /// <summary>
    /// 对 Selected 候选批量水合正文并重算 token，返回更新后的打包结果。
    /// </summary>
    /// <param name="request">检索请求（决定工作空间/集合与是否需要正文）。</param>
    /// <param name="packingResult">Pack 后的结果（Selected 候选可能 Content 为空）。</param>
    /// <param name="contextStore">上下文存储（探测批量水合能力）。</param>
    /// <param name="memoryStore">记忆存储（可为 null，探测批量水合能力）。</param>
    /// <param name="tokenizerResolver">tokenizer 解析器（null 时用 length/4 估算）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新后的打包结果（正文已水合、token 已重算）；无需水合时原样返回。</returns>
    public static async Task<RetrievalPackingResult> HydrateAsync(
        ContextRetrievalRequest request,
        RetrievalPackingResult packingResult,
        IContextStore contextStore,
        IMemoryStore? memoryStore,
        IContextTokenizerResolver? tokenizerResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(packingResult);
        ArgumentNullException.ThrowIfNull(contextStore);

        // 输出不需要正文 → 直接返回（不执行任何水合 / token 重算）。
        if (!request.IncludeContent)
        {
            return packingResult;
        }

        var selected = packingResult.SelectedCandidates;
        if (selected.Count == 0)
        {
            return packingResult;
        }

        // 收集需要水合的候选（正文为空且为 context / memory 类型；relation-only 候选自带正文）。
        var needsHydration = selected
            .Where(candidate => string.IsNullOrEmpty(candidate.Content)
                && (candidate.Kind == ContextRetrievalCandidateKind.ContextItem
                    || candidate.Kind == ContextRetrievalCandidateKind.MemoryItem))
            .ToArray();

        // 批量水合（Context 与 Memory 独立，顺序执行保持实现简单；单次批量即一次存储访问）。
        var hydratedContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (needsHydration.Length > 0)
        {
            await HydrateContextItemsAsync(request, needsHydration, contextStore, hydratedContent, cancellationToken).ConfigureAwait(false);
            await HydrateMemoryItemsAsync(request, needsHydration, memoryStore, hydratedContent, cancellationToken).ConfigureAwait(false);
        }

        // 重建 Selected 候选：填充水合正文 + 按真实内容重算 token（tokenizer 可用时精确，否则 length/4）。
        var rebuiltCandidates = new List<ContextRetrievalCandidate>(selected.Count);
        var tokensByCandidateId = new Dictionary<string, int>(selected.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in selected)
        {
            var content = candidate.Content;
            if (string.IsNullOrEmpty(content)
                && hydratedContent.TryGetValue(candidate.SourceId, out var hydrated)
                && !string.IsNullOrEmpty(hydrated))
            {
                content = hydrated;
            }

            var tokens = ComputeTokens(content, tokenizerResolver);
            tokensByCandidateId[candidate.CandidateId] = tokens;
            rebuiltCandidates.Add(new ContextRetrievalCandidate
            {
                CandidateId = candidate.CandidateId,
                SourceId = candidate.SourceId,
                Kind = candidate.Kind,
                Type = candidate.Type,
                Title = candidate.Title,
                Content = content,
                ContentFormat = candidate.ContentFormat,
                Tags = candidate.Tags,
                SourceRefs = candidate.SourceRefs,
                Score = candidate.Score,
                EstimatedTokens = tokens,
                Reasons = candidate.Reasons,
                Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
            });
        }

        // 同步选中决策的 token 估算，保持 trace 与结果一致（决策其余字段原样保留）。
        var rebuiltDecisions = packingResult.SelectedDecisions
            .Select(decision => tokensByCandidateId.TryGetValue(decision.CandidateId, out var tokens)
                ? new ContextRetrievalDecision
                {
                    CandidateId = decision.CandidateId,
                    SourceId = decision.SourceId,
                    Kind = decision.Kind,
                    Type = decision.Type,
                    Reason = decision.Reason,
                    Score = decision.Score,
                    EstimatedTokens = tokens,
                    Metadata = new Dictionary<string, string>(decision.Metadata, StringComparer.OrdinalIgnoreCase)
                }
                : decision)
            .ToArray();

        return new RetrievalPackingResult(rebuiltCandidates, rebuiltDecisions, packingResult.DroppedDecisions);
    }

    /// <summary>批量读取 Context 候选正文；store 未命中或正文为空的候选保持原样（Content 为空）。</summary>
    private static async Task HydrateContextItemsAsync(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> needsHydration,
        IContextStore contextStore,
        Dictionary<string, string> hydratedContent,
        CancellationToken cancellationToken)
    {
        if (contextStore is not IContextStoreBatchLookup batchLookup)
        {
            return;
        }

        var ids = needsHydration
            .Where(candidate => candidate.Kind == ContextRetrievalCandidateKind.ContextItem)
            .Select(candidate => candidate.SourceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var items = await batchLookup.BatchGetAsync(
            request.WorkspaceId, request.CollectionId, ids, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Content))
            {
                hydratedContent[item.Id] = item.Content;
            }
        }
    }

    /// <summary>批量读取 Memory 候选正文；store 未命中或正文为空的候选保持原样（Content 为空）。</summary>
    private static async Task HydrateMemoryItemsAsync(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> needsHydration,
        IMemoryStore? memoryStore,
        Dictionary<string, string> hydratedContent,
        CancellationToken cancellationToken)
    {
        if (memoryStore is not IMemoryStoreBatchLookup batchLookup)
        {
            return;
        }

        var ids = needsHydration
            .Where(candidate => candidate.Kind == ContextRetrievalCandidateKind.MemoryItem)
            .Select(candidate => candidate.SourceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var items = await batchLookup.BatchGetAsync(
            request.WorkspaceId, request.CollectionId, ids, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Content))
            {
                hydratedContent[item.Id] = item.Content;
            }
        }
    }

    /// <summary>按真实内容重算 token：tokenizer 可用时精确计数，否则 length/4 估算（与召回阶段公式一致）。</summary>
    private static int ComputeTokens(string content, IContextTokenizerResolver? tokenizerResolver)
    {
        if (tokenizerResolver is not null)
        {
            return TokenCostHelper.ComputeTokenCost(content, tokenizerResolver).ContentTokens;
        }

        return string.IsNullOrWhiteSpace(content)
            ? 0
            : Math.Max(1, content.Length / 4);
    }
}
