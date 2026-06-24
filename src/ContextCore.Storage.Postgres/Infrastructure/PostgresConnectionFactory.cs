using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>集中管理 PostgreSQL 连接池。</summary>
public sealed class PostgresConnectionFactory : IPostgresConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresConnectionFactory(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL 连接字符串不能为空。");
        }

        Options = options;
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
    }

    public PostgresOptions Options { get; }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return _dataSource.OpenConnectionAsync(cancellationToken);
    }

    /// <summary>执行 <c>SELECT 1</c> 验证连接是否可用。</summary>
    public async Task<(bool Success, string? ErrorMessage)> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }
}
