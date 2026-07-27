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

    /// <summary>
    /// 添加一个强类型数组参数（用于 unnest 批量插入）。调用方负责选择与列类型匹配的 <paramref name="npgsqlDbType"/>。
    /// </summary>
    /// <typeparam name="T">数组元素类型（如 int / short / DateTimeOffset / string）。</typeparam>
    /// <param name="command">目标命令。</param>
    /// <param name="name">参数名（不含 @）。</param>
    /// <param name="npgsqlDbType">Npgsql 数组类型（如 <c>NpgsqlDbType.Array | NpgsqlDbType.Integer</c>）。</param>
    /// <param name="values">数组值。</param>
    protected static NpgsqlParameter AddArrayParameter<T>(NpgsqlCommand command, string name, NpgsqlDbType npgsqlDbType, T values)
        where T : notnull
    {
        var parameter = command.Parameters.Add(name, npgsqlDbType);
        parameter.Value = values;
        return parameter;
    }

    /// <summary>
    /// 添加一个可为 null 元素的 text[] 参数（用于 unnest 批量插入中允许 NULL 的列）。
    /// Npgsql 对 string?[] 中的 null 元素会发送 SQL NULL。
    /// </summary>
    protected static NpgsqlParameter AddNullableTextArray(NpgsqlCommand command, string name, IReadOnlyList<string?> values)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Text);
        // Npgsql 要求 string?[] 的 Value 类型；ToArray 保留 null 元素。
        parameter.Value = values.ToArray();
        return parameter;
    }

    /// <summary>
    /// 添加一个可为 null 元素的 double[] 参数（用于 unnest 批量插入中允许 NULL 的列）。
    /// Npgsql 对 double?[] 中的 null 元素会发送 SQL NULL。
    /// </summary>
    protected static NpgsqlParameter AddNullableDoubleArray(NpgsqlCommand command, string name, IReadOnlyList<double?> values)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Double);
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