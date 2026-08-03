using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Model Artifact Registry。
/// 替代 InMemory 默认实现，让 HA 场景下模型工件描述符可跨进程持久化与查询，
/// 让生产环境能从 PostgreSQL 加载权威模型描述符，而非依赖代码硬编码。
/// </summary>
/// <remarks>
/// 设计要点：
/// 1. 表 <c>model_artifacts</c> 以 <c>model_artifact_id</c> 为主键。
/// 2. <see cref="RegisterAsync"/> 使用 <c>INSERT ... ON CONFLICT DO NOTHING</c> 探测插入；
/// 若 0 行受影响且行已存在 → 抛 <see cref="InvalidOperationException"/>（与不可变语义一致）。
/// 3. <see cref="GetAsync"/> 通过主键读取整行；映射反规范化列回 <see cref="ModelArtifactDescriptor"/>，
/// 避免反序列化 jsonb 以减少冷查询延迟。
/// 4. <see cref="GetLatestAsync"/> / <see cref="ListByVersionAsync"/> 通过
/// <c>(model_name, registered_at DESC)</c> 索引按模型名查询最新版本或全部版本。
/// 5. <see cref="ListAllAsync"/> 通过 <c>(registered_at ASC)</c> 索引按注册顺序列出全部描述符。
/// </remarks>
public sealed class PostgresModelArtifactRegistry : PostgresStoreBase, IPersistentModelArtifactRegistry
{
    /// <summary>初始化 Postgres 持久化 Model Artifact Registry。</summary>
    public PostgresModelArtifactRegistry(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(ModelArtifactDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // ON CONFLICT DO NOTHING：探测式插入；0 行受影响表示行已存在，需抛异常以满足"同一 ModelArtifactId 仅允许注册一次"语义。
        command.CommandText = $"""
INSERT INTO {Table("model_artifacts")} (
    model_artifact_id, model_name, model_version, feature_schema_version,
    calibration_version, engine_kind, content_hash, artifact_path,
    description, registered_at, data)
VALUES (
    @model_artifact_id, @model_name, @model_version, @feature_schema_version,
    @calibration_version, @engine_kind, @content_hash, @artifact_path,
    @description, @registered_at, @data)
ON CONFLICT (model_artifact_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("model_artifact_id", descriptor.ModelArtifactId);
        command.Parameters.AddWithValue("model_name", descriptor.ModelName);
        command.Parameters.AddWithValue("model_version", descriptor.ModelVersion);
        command.Parameters.AddWithValue("feature_schema_version", descriptor.FeatureSchemaVersion);
        command.Parameters.AddWithValue("calibration_version", descriptor.CalibrationVersion);
        command.Parameters.AddWithValue("engine_kind", (byte)descriptor.EngineKind);
        command.Parameters.AddWithValue("content_hash", descriptor.ContentHash);
        command.Parameters.AddWithValue("artifact_path", (object?)descriptor.ArtifactPath ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)descriptor.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("registered_at", descriptor.RegisteredAt);
        AddJson(command, "data", descriptor);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"ModelArtifactId '{descriptor.ModelArtifactId}' 已注册，不可重复注册。" +
                "如需发布新版本，请使用新的 ModelArtifactId（与 FeatureSchema 不可变语义一致）。");
        }
    }

    /// <inheritdoc />
    public async ValueTask<ModelArtifactDescriptor?> GetAsync(string modelArtifactId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelArtifactId))
        {
            return null;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("model_artifacts")}
WHERE model_artifact_id = @model_artifact_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("model_artifact_id", modelArtifactId);
        return await ExecuteScalarJsonAsync<ModelArtifactDescriptor?>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ModelArtifactDescriptor?> GetLatestAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("model_artifacts")}
WHERE model_name = @model_name
ORDER BY registered_at DESC
LIMIT 1;
""";
        command.Parameters.AddWithValue("model_name", modelName);
        return await ExecuteScalarJsonAsync<ModelArtifactDescriptor?>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListByVersionAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Array.Empty<ModelArtifactDescriptor>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("model_artifacts")}
WHERE model_name = @model_name
ORDER BY registered_at ASC;
""";
        command.Parameters.AddWithValue("model_name", modelName);
        return await ExecuteReaderJsonAsync<ModelArtifactDescriptor>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("model_artifacts")}
ORDER BY registered_at ASC;
""";
        return await ExecuteReaderJsonAsync<ModelArtifactDescriptor>(command, cancellationToken).ConfigureAwait(false);
    }
}
