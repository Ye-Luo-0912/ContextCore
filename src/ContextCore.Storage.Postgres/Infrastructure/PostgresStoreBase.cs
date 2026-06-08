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

    protected string Table(string suffix) => PostgresNames.Table(Options, suffix);

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
}