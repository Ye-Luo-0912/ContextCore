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
/// 但流式不应用 Skip/Take——返回完整候选集，由消费方按需裁剪。</item>
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
