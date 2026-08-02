using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
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

    /// <summary>
    /// 可选 tokenizer 解析器与模型名。Memory/Constraint 子类在 SaveAsync 时调用
    /// <see cref="ComputeTokenizationMetadata"/> 计算 SHA-256 + token 数，持久化到专用列；
    /// 读取时 Provider 直接复用持久化值，跳过在线 SHA-256 + tokenizer 调用。
    /// null 时仅持久化 content_hash / content_length，token_count 等列保持 NULL。
    /// </summary>
    protected IContextTokenizerResolver? TokenizerResolver { get; init; }

    protected string? TokenizerModelName { get; init; }

    /// <summary>
    /// 计算内容的 tokenization metadata。SHA-256 总是计算（无外部依赖）；
    /// token_count 在 tokenizer 可用时计算，否则返回 null（Provider 回退到在线 fail-fast 路径）。
    /// </summary>
    protected TokenizationMetadata ComputeTokenizationMetadata(string content)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hashHex = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();
        var contentLength = contentBytes.Length;

        string? tokenizerId = null;
        string? tokenizerVersion = null;
        int? tokenCount = null;
        var countedAt = (DateTimeOffset?)null;

        if (TokenizerResolver is not null)
        {
            var estimate = TokenizerResolver.Estimate(content, TokenizerModelName);
            tokenCount = Math.Max(0, estimate.TokenCount);
            tokenizerId = estimate.Source;
            tokenizerVersion = string.IsNullOrWhiteSpace(TokenizerModelName)
                ? estimate.ModelName
                : TokenizerModelName;
            countedAt = DateTimeOffset.UtcNow;
        }

        return new TokenizationMetadata
        {
            ContentHash = hashHex,
            ContentLength = contentLength,
            TokenizerId = tokenizerId,
            TokenizerVersion = tokenizerVersion,
            TokenCount = tokenCount ?? 0,
            CountedAt = countedAt
        };
    }

    /// <summary>
    /// 把 tokenization metadata 注入到 Metadata 字典（供 Provider 读取复用）。
    /// 调用方应使用返回的字典替换原 Metadata（避免修改原集合）。
    /// </summary>
    protected static Dictionary<string, string> WithTokenizationMetadata(
        IReadOnlyDictionary<string, string> baseMetadata,
        TokenizationMetadata metadata)
    {
        var result = new Dictionary<string, string>(baseMetadata)
        {
            [ContentMetadataKeys.ContentHash] = metadata.ContentHash,
            [ContentMetadataKeys.ContentLength] = metadata.ContentLength.ToString(CultureInfo.InvariantCulture)
        };

        if (metadata.TokenizerId is not null)
        {
            result[ContentMetadataKeys.TokenizerId] = metadata.TokenizerId;
        }
        if (metadata.TokenizerVersion is not null)
        {
            result[ContentMetadataKeys.TokenizerVersion] = metadata.TokenizerVersion;
        }
        if (metadata.TokenCount >= 0 && metadata.CountedAt.HasValue)
        {
            result[ContentMetadataKeys.ContentTokenCost] = metadata.TokenCount.ToString(CultureInfo.InvariantCulture);
            result[ContentMetadataKeys.CountedAt] = metadata.CountedAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>
    /// 从数据库列读取 tokenization metadata，写入 Metadata 字典（供 Provider 读取复用）。
    /// 调用方应在反序列化 jsonb 后调用本方法，把专用列的值合并到 Metadata。
    /// 所有参数为 null 时直接返回原字典（不复制）。
    /// </summary>
    protected static Dictionary<string, string> MergePersistedTokenizationColumns(
        IReadOnlyDictionary<string, string> baseMetadata,
        string? contentHash,
        int? contentLength,
        string? tokenizerId,
        string? tokenizerVersion,
        int? tokenCount,
        DateTimeOffset? countedAt)
    {
        if (string.IsNullOrEmpty(contentHash)
            && !contentLength.HasValue
            && string.IsNullOrEmpty(tokenizerId)
            && string.IsNullOrEmpty(tokenizerVersion)
            && !tokenCount.HasValue
            && !countedAt.HasValue)
        {
            return baseMetadata as Dictionary<string, string> ?? new Dictionary<string, string>(baseMetadata);
        }

        var result = new Dictionary<string, string>(baseMetadata);
        if (!string.IsNullOrEmpty(contentHash))
        {
            result[ContentMetadataKeys.ContentHash] = contentHash!;
        }
        if (contentLength.HasValue)
        {
            result[ContentMetadataKeys.ContentLength] = contentLength.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrEmpty(tokenizerId))
        {
            result[ContentMetadataKeys.TokenizerId] = tokenizerId!;
        }
        if (!string.IsNullOrEmpty(tokenizerVersion))
        {
            result[ContentMetadataKeys.TokenizerVersion] = tokenizerVersion!;
        }
        if (tokenCount.HasValue)
        {
            result[ContentMetadataKeys.ContentTokenCost] = tokenCount.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (countedAt.HasValue)
        {
            result[ContentMetadataKeys.CountedAt] = countedAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }
        return result;
    }

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