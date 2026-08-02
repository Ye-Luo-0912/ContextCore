using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// P1-7：关系流式查询契约，避免一次性将整张关系图载入内存。
/// <para>
/// 与 <see cref="IRelationStore"/> 分离以保持 7 方法契约稳定。实现方在已有 store 上额外实现此接口；
/// 调用方（如 RelationGraphValidationService.ValidateStreamAsync、流式诊断端点）通过
/// <c>as IRelationStreamStore</c> 探测能力——未实现时回退到 <see cref="IRelationStore.QueryAsync"/>
/// 的非流式路径。
/// </para>
/// <para>
/// 流式语义：
/// <list type="bullet">
/// <item>每条 <see cref="ContextRelation"/> 在读取后立即 yield，不缓冲到 List。</item>
/// <item>取消令牌通过 <c>[EnumeratorCancellation]</c> 透传到底层 reader。</item>
/// <item>Postgres 实现使用 <see cref="Npgsql.NpgsqlDataReader.ReadAsync"/>；
/// FileSystem 实现逐行读取 JSONL；InMemory 实现遍历内存字典。</item>
/// <item>排序语义与 <see cref="IRelationStore.QueryAsync"/> 一致（weight/confidence/createdAt desc）；
/// 但流式不应用调用方提供的 Skip/Take——返回完整候选集，由消费方按需裁剪。</item>
/// <item>P1-9：流式不得无界扫描。实现方必须应用 <see cref="GraphQueryLimits.MaxTotalEdges"/>
/// 作为 SQL LIMIT 或迭代上限，防止病态全表扫描把整张图拉入内存。在线主链不得调用本方法
/// 做候选枚举——应走 <see cref="IRelationStore.QueryNeighborsBatchAsync"/> 的 per-seed TopN 路径。</item>
/// </list>
/// </para>
/// </summary>
public interface IRelationStreamStore
{
    /// <summary>
    /// 流式枚举符合过滤条件的关系。
    /// </summary>
    /// <param name="workspaceId">工作空间 ID（必填）。</param>
    /// <param name="collectionId">可选集合过滤；为空时返回工作空间下所有集合的关系。</param>
    /// <param name="itemId">可选 item 过滤；非空时仅返回 source 或 target 等于此 ID 的关系。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步枚举的 <see cref="ContextRelation"/> 序列。</returns>
    IAsyncEnumerable<ContextRelation> StreamRelationsAsync(
        string workspaceId,
        string? collectionId = null,
        string? itemId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// P1-9：按关系 ID 批量 hydrate 完整 Relation Metadata（JSON）的契约。
/// <para>
/// 与 <see cref="IRelationStore"/> 分离以保持 7 方法契约稳定（参照 <see cref="IRelationStreamStore"/> 的拆分模式）。
/// 在线主链（<see cref="IRelationStore.QueryNeighborsBatchAsync"/>）只返回结构列，
/// 不反序列化完整 Relation JSON；当客户端 Selected 特定 edges 后，再通过本接口批量 hydrate
/// 完整的 Metadata/SourceRefs/Provenance 等字段，避免数据库为 MaxSeeds × maxScan 条候选生成完整 JSON。
/// </para>
/// <para>
/// 实现方在已有 store 上额外实现此接口；调用方通过 <c>as IRelationHydrationStore</c> 探测能力。
/// 未实现时回退到 <see cref="IRelationStore.GetAsync"/> 的逐条获取。
/// </para>
/// </summary>
public interface IRelationHydrationStore
{
    /// <summary>
    /// 按关系 ID 批量获取完整 <see cref="ContextRelation"/>（含 Metadata/SourceRefs 等 JSON 字段）。
    /// </summary>
    /// <param name="workspaceId">工作空间 ID（必填）。</param>
    /// <param name="collectionId">可选集合过滤；为空时跨集合按 ID 查找。</param>
    /// <param name="relationIds">需要 hydrate 的关系 ID 列表（必填，至少 1 个）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整反序列化的 <see cref="ContextRelation"/> 列表；未命中的 ID 不出现在结果中。</returns>
    Task<IReadOnlyList<ContextRelation>> HydrateRelationsAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<string> relationIds,
        CancellationToken cancellationToken = default);
}
