using ContextCore.Abstractions;
using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// P0-3：PostgreSQL 跨 store 写入事务作用域。
/// 持有一个 <see cref="NpgsqlConnection"/> + <see cref="NpgsqlTransaction"/>，
/// 让多个 Postgres store（PostgresContextStore / PostgresRelationStore 等）共享同一连接与事务。
/// CommitAsync 一次性提交事务；RollbackAsync/DisposeAsync 回滚。
/// </summary>
/// <remarks>
/// 使用模式：
/// <code>
/// await using var scope = (PostgresWriteTransactionScope) await factory.BeginAsync(ct);
/// await txContextStore.SaveAsync(item, scope, ct);
/// await txRelationStore.BatchUpsertAsync(relations, scope, ct);
/// await scope.CommitAsync(ct);
/// </code>
/// 注意：scope 必须由调用方 DisposeAsync（推荐 await using）。未显式 Commit 时 Dispose 会触发 Rollback。
/// </remarks>
public sealed class PostgresWriteTransactionScope : IWriteTransactionScope
{
    private readonly NpgsqlConnection _connection;
    private NpgsqlTransaction? _transaction;
    private int _state; // 0=active, 1=committed, 2=rolled back
    private bool _connectionOwned;

    public PostgresWriteTransactionScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        _connection = connection;
        _transaction = transaction;
        _connectionOwned = true;
    }

    /// <summary>共享的 Npgsql 连接——事务感知 store 通过此属性访问底层连接。</summary>
    public NpgsqlConnection Connection => _connection;

    /// <summary>共享的 Npgsql 事务——事务感知 store 通过此属性将 command 绑定到事务。null 表示事务已结束。</summary>
    public NpgsqlTransaction? Transaction => _transaction;

    public bool IsActive => Interlocked.CompareExchange(ref _state, 0, 0) == 0;

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _state, 1) != 0) return; // 已结束

        var tx = _transaction;
        if (tx is not null)
        {
            try
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await DisposeTransactionAndConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _state, 2) != 0) return; // 已结束

        var tx = _transaction;
        if (tx is not null)
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Rollback 失败不掩盖原始异常——记录后继续清理连接。
            }
            finally
            {
                await DisposeTransactionAndConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 未显式 Commit 时视为 Rollback——保证异常路径不会误提交。
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            var tx = _transaction;
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* 忽略——已 Dispose */ }
            }
        }

        await DisposeTransactionAndConnectionAsync().ConfigureAwait(false);
    }

    private async Task DisposeTransactionAndConnectionAsync()
    {
        var tx = _transaction;
        _transaction = null;
        if (tx is not null)
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }

        if (_connectionOwned)
        {
            _connectionOwned = false;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// P0-3：PostgreSQL 事务作用域工厂。打开新连接并开始事务，返回 <see cref="PostgresWriteTransactionScope"/>。
/// 在 <see cref="PostgresServiceCollectionExtensions"/> 中注册为 <see cref="IWriteTransactionScopeFactory"/>。
/// </summary>
public sealed class PostgresWriteTransactionScopeFactory : IWriteTransactionScopeFactory
{
    private readonly PostgresConnectionFactory _connectionFactory;

    public PostgresWriteTransactionScopeFactory(PostgresConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IWriteTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new PostgresWriteTransactionScope(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
