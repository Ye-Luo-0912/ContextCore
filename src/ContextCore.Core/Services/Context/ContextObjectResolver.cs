using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 解析上下文对象的统一接口。
/// 只负责查找 ContextItem / MemoryItem，不负责生命周期过滤、评分和 section 策略。
/// </summary>
public interface IContextObjectResolver
{
    Task<ContextObjectResolution> ResolveAsync(
        string workspaceId,
        string? collectionId,
        string id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextObjectResolution>> ResolveManyAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认对象解析器：按 ContextItem 优先、MemoryItem 兜底的顺序解析目标。
/// </summary>
public sealed class DefaultContextObjectResolver : IContextObjectResolver
{
    private readonly IContextStore _contextStore;
    private readonly IMemoryStore? _memoryStore;

    public DefaultContextObjectResolver(IContextStore contextStore, IMemoryStore? memoryStore)
    {
        _contextStore = contextStore;
        _memoryStore = memoryStore;
    }

    public async Task<ContextObjectResolution> ResolveAsync(
        string workspaceId,
        string? collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        var effectiveCollectionId = collectionId ?? string.Empty;
        var contextItem = await _contextStore.GetAsync(
            workspaceId,
            effectiveCollectionId,
            id,
            cancellationToken).ConfigureAwait(false);
        if (contextItem is not null)
        {
            return ContextObjectResolution.Resolved(id, ResolvedContextObject.FromContextItem(contextItem));
        }

        if (_memoryStore is not null)
        {
            var memoryItem = await _memoryStore.GetAsync(
                workspaceId,
                effectiveCollectionId,
                id,
                cancellationToken).ConfigureAwait(false);
            if (memoryItem is not null)
            {
                return ContextObjectResolution.Resolved(id, ResolvedContextObject.FromMemoryItem(memoryItem));
            }
        }

        return ContextObjectResolution.NotFound(
            id,
            "TargetNotFound",
            $"未找到 relation target：{id}");
    }

    public async Task<IReadOnlyList<ContextObjectResolution>> ResolveManyAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ContextObjectResolution>(ids.Count);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ResolveAsync(workspaceId, collectionId, id, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}

/// <summary>
/// 单个对象解析结果。未找到时返回诊断信息而不是抛异常。
/// </summary>
public sealed class ContextObjectResolution
{
    private ContextObjectResolution(
        string requestedId,
        ResolvedContextObject? resolvedObject,
        string? diagnosticCode,
        string? diagnosticMessage)
    {
        RequestedId = requestedId;
        ResolvedObject = resolvedObject;
        DiagnosticCode = diagnosticCode;
        DiagnosticMessage = diagnosticMessage;
    }

    public string RequestedId { get; }

    public ResolvedContextObject? ResolvedObject { get; }

    public string? DiagnosticCode { get; }

    public string? DiagnosticMessage { get; }

    public bool Found => ResolvedObject is not null;

    public static ContextObjectResolution Resolved(string requestedId, ResolvedContextObject resolvedObject)
        => new(requestedId, resolvedObject, diagnosticCode: null, diagnosticMessage: null);

    public static ContextObjectResolution NotFound(
        string requestedId,
        string diagnosticCode,
        string diagnosticMessage)
        => new(requestedId, resolvedObject: null, diagnosticCode, diagnosticMessage);
}

/// <summary>
/// 已解析的 context/memory 条目统一封装，供 relation expansion 后续流程消费。
/// </summary>
public sealed class ResolvedContextObject
{
    private ResolvedContextObject(
        string id,
        ContextRetrievalCandidateKind kind,
        double importance,
        ContextItem? contextItem,
        ContextMemoryItem? memoryItem)
    {
        Id = id;
        Kind = kind;
        Importance = importance;
        ContextItem = contextItem;
        MemoryItem = memoryItem;
    }

    public string Id { get; }

    public ContextRetrievalCandidateKind Kind { get; }

    public double Importance { get; }

    public ContextItem? ContextItem { get; }

    public ContextMemoryItem? MemoryItem { get; }

    public static ResolvedContextObject FromContextItem(ContextItem item)
        => new(item.Id, ContextRetrievalCandidateKind.ContextItem, item.Importance, item, memoryItem: null);

    public static ResolvedContextObject FromMemoryItem(ContextMemoryItem item)
        => new(item.Id, ContextRetrievalCandidateKind.MemoryItem, item.Importance, contextItem: null, item);

    internal RetrievalRelationTarget ToRelationTarget()
    {
        if (ContextItem is not null)
        {
            return new RetrievalRelationTarget(
                ContextItem.Id,
                ContextRetrievalCandidateKind.ContextItem,
                ContextItem.Type,
                ContextItem.Title,
                ContextItem.Content,
                ContextItem.ContentFormat,
                ContextItem.Tags.ToArray(),
                ContextItem.SourceRefs.Concat(ContextItem.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                new Dictionary<string, string>(ContextItem.Metadata),
                ContextItem.Importance);
        }

        if (MemoryItem is not null)
        {
            var metadata = new Dictionary<string, string>(MemoryItem.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycleStatus"] = MemoryItem.Status.ToString(),
                ["memoryLayer"] = MemoryItem.Layer.ToString(),
                ["candidateSourceKind"] = "memory"
            };

            return new RetrievalRelationTarget(
                MemoryItem.Id,
                ContextRetrievalCandidateKind.MemoryItem,
                MemoryItem.Type,
                Title: null,
                Content: MemoryItem.Content,
                ContentFormat: MemoryItem.ContentFormat,
                Tags: MemoryItem.Tags.ToArray(),
                SourceRefs: MemoryItem.SourceRefs.ToArray(),
                Metadata: metadata,
                Importance: MemoryItem.Importance);
        }

        throw new InvalidOperationException("ResolvedContextObject 不包含可转换的 relation target。");
    }
}
