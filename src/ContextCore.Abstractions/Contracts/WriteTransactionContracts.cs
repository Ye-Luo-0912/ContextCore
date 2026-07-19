using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// P0-3：跨 store 写入事务作用域。表示一个开放的、可跨多个 store 的原子写入单元。
/// 实现负责持有底层连接/事务（如 NpgsqlConnection + NpgsqlTransaction），
/// 在 <see cref="CommitAsync"/> 时一次性提交所有参与 store 的写入；
/// <see cref="RollbackAsync"/> 或 <see cref="DisposeAsync"/> 时全部回滚。
/// </summary>
/// <remarks>
/// 语义：
/// <list type="bullet">
/// <item>未显式 Commit 而 Dispose 时视为 Rollback——保证异常路径不会误提交。</item>
/// <item>Commit/Rollback 多次调用幂等，仅第一次生效，之后 <see cref="IsActive"/>=false。</item>
/// </list>
/// 仅当 store 实现对应的 <see cref="ITransactionalContextStore"/> / <see cref="ITransactionalRelationStore"/>
/// 等可选能力接口时才参与事务；未实现的 store 走原有无事务路径
/// （<see cref="IWriteTransactionScopeFactory"/> 不注册即可让 pipeline 自动回退到无事务路径）。
/// </remarks>
public interface IWriteTransactionScope : IAsyncDisposable
{
    /// <summary>true 表示事务尚未提交或回滚。</summary>
    bool IsActive { get; }

    /// <summary>
    /// 提交所有参与 store 的写入。提交后 <see cref="IsActive"/>=false。
    /// 多次调用幂等（仅第一次生效）。
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 回滚所有参与 store 的写入。回滚后 <see cref="IsActive"/>=false。
    /// 多次调用幂等。<see cref="DisposeAsync"/> 未显式 Commit 时也会触发 Rollback。
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-3：跨 store 写入事务作用域工厂。注入到需要事务包裹的 pipeline 中。
/// 实现决定底层事务策略：Postgres 返回真实事务作用域，InMemory/FileSystem 返回 no-op 作用域。
/// </summary>
public interface IWriteTransactionScopeFactory
{
    /// <summary>
    /// 开始一个新的事务作用域。调用方负责 DisposeAsync（推荐使用 await using）。
    /// </summary>
    Task<IWriteTransactionScope> BeginAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-3：IContextStore 的可选事务能力接口。
/// 实现 IContextStore 的 store 若同时实现此接口，表示可在 <see cref="IWriteTransactionScope"/> 内执行写入。
/// pipeline 通过 <c>store is ITransactionalContextStore</c> 检测并切换到事务路径。
/// 未实现此接口的 store（如 InMemory/FileSystem）走原有无事务路径。
/// </summary>
public interface ITransactionalContextStore
{
    /// <summary>在指定事务作用域内保存条目。scope 必须由同一 <see cref="IWriteTransactionScopeFactory"/> 创建。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(ContextItem item, IWriteTransactionScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-3：IRelationStore 的可选事务能力接口。
/// 提供 BatchUpsert/Delete/Query 的事务感知重载，使图边写入与 ContextStore 写入共享同一事务。
/// </summary>
public interface ITransactionalRelationStore
{
    /// <summary>在事务作用域内批量 upsert 关系。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>在事务作用域内删除单条关系。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>在事务作用域内查询关系（读共享同一事务视图，避免读到未提交数据）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-3：IRelationProjectionWriter 的可选事务能力接口。
/// 让投影写入边界能在事务作用域内委托底层 BatchUpsertAsync。
/// </summary>
public interface ITransactionalRelationProjectionWriter
{
    /// <summary>在事务作用域内验证并写入关系。</summary>
    Task<RelationProjectionWriteResult> WriteAsync(
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default);
}
