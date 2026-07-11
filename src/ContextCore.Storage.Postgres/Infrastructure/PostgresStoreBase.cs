using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres;

/// <summary>PostgreSQL store 共享基类，负责迁移、连接和 jsonb 参数。</summary>
public abstract class PostgresStoreBase
{
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private bool _migrated;

    protected PostgresStoreBase(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
    {
        ConnectionFactory = connectionFactory;
        Serializer = serializer;
        MigrationRunner = migrationRunner;
    }

    protected PostgresConnectionFactory ConnectionFactory { get; }

    protected PostgresJsonSerializer Serializer { get; }

    protected PostgresMigrationRunner MigrationRunner { get; }

    protected PostgresOptions Options => ConnectionFactory.Options;

    /// <summary>首次访问时执行一次幂等迁移；关闭 AutoMigrate 时不执行。</summary>
    protected async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (!Options.AutoMigrate || _migrated)
        {
            return;
        }

        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_migrated)
            {
                await MigrationRunner.MigrateAsync(cancellationToken).ConfigureAwait(false);
                _migrated = true;
            }
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    protected string Table(string suffix) => Infrastructure.PostgresNames.Table(Options, suffix);

    protected static string CollectionKey(string? collectionId) => string.IsNullOrWhiteSpace(collectionId) ? string.Empty : collectionId;

    protected NpgsqlParameter AddJson<T>(NpgsqlCommand command, string name, T value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Jsonb);
        parameter.Value = Serializer.Serialize(value);
        return parameter;
    }

    protected static NpgsqlParameter AddTextArray(NpgsqlCommand command, string name, IReadOnlyList<string> values)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Text);
        parameter.Value = values.ToArray();
        return parameter;
    }

    protected static int TakeOrDefault(int take) => take > 0 ? take : 50;

    /// <summary>
    /// 执行命令并返回首行首列的 JSON 标量反序列化结果；当结果为空或空白时返回 null。
    /// 封装 Postgres store 中常见的 <c>ExecuteScalarAsync</c> + <c>Serializer.Deserialize</c> 模式。
    /// </summary>
    protected async Task<T?> ExecuteScalarJsonAsync<T>(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? default : Serializer.Deserialize<T>(json);
    }

    /// <summary>
    /// 执行命令并按读取器流式反序列化第 0 列的 JSON 行，返回只读列表。
    /// 封装 Postgres store 中常见的 <c>ExecuteReaderAsync</c> + <c>reader.GetString(0)</c> + <c>Serializer.Deserialize</c> 模式。
    /// </summary>
    protected async Task<IReadOnlyList<T>> ExecuteReaderJsonAsync<T>(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<T>(reader.GetString(0)));
        }

        return results;
    }
}