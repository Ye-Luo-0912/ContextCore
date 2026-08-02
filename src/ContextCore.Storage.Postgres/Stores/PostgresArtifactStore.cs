using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL artifact 存储。
/// 替代 UnsupportedArtifactStore。
/// 设计权衡：与 FileArtifactStore 不同，Postgres 版将 artifact 内容存为 jsonb 行而非文件系统文件。
/// 这是 R14-PG 边界声明中明确的"Postgres = HA 运行时（jsonb 存储），FileSystem = 本地（文件存储）"的体现。
/// WriteJsonAsync/WriteMarkdownAsync/AppendJsonLineAsync 返回合成标识符（relative_path），而非真实文件路径；
/// 外部工具需通过 ReadJsonAsync 读回内容。
/// </summary>
public sealed class PostgresArtifactStore : PostgresStoreBase, IArtifactStore
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public PostgresArtifactStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task<string> WriteJsonAsync<T>(ArtifactDescriptor descriptor, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var valueJson = Serializer.Serialize(value);
        var data = Serializer.Serialize(new { descriptor, value });
        var sizeBytes = Encoding.UTF8.GetByteCount(valueJson);
        var contentHash = ComputeSha256Hex(valueJson);
        return await InsertArtifactAsync(descriptor, ".json", "application/json", data, sizeBytes, contentHash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> WriteMarkdownAsync(ArtifactDescriptor descriptor, string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(markdown);
        var data = Serializer.Serialize(new { descriptor, markdown });
        var sizeBytes = Encoding.UTF8.GetByteCount(markdown);
        var contentHash = ComputeSha256Hex(markdown);
        return await InsertArtifactAsync(descriptor, ".md", "text/markdown", data, sizeBytes, contentHash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AppendJsonLineAsync<T>(ArtifactDescriptor descriptor, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var valueJson = Serializer.Serialize(value);
        var data = Serializer.Serialize(new { descriptor, line = value });
        var sizeBytes = Encoding.UTF8.GetByteCount(valueJson);
        var contentHash = ComputeSha256Hex(valueJson);
        return await InsertArtifactAsync(descriptor, ".jsonl", "application/jsonl", data, sizeBytes, contentHash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ReadJsonAsync<T>(ArtifactDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("artifacts")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND artifact_kind = @artifact_kind
  AND extension = '.json'
ORDER BY updated_at DESC
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", descriptor.WorkspaceId ?? string.Empty);
        command.Parameters.AddWithValue("collection_id", descriptor.CollectionId ?? string.Empty);
        command.Parameters.AddWithValue("artifact_kind", descriptor.Kind.ToString());

        var data = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(data))
        {
            return default;
        }

        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("value", out var valueElement))
        {
            return JsonSerializer.Deserialize<T>(valueElement.GetRawText(), PayloadOptions);
        }

        return default;
    }

    public async Task<IReadOnlyList<ArtifactManifestEntry>> ListAsync(ArtifactKind? kind = null, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        if (kind is null)
        {
            command.CommandText = $"""
SELECT workspace_id, collection_id, artifact_id, artifact_kind, relative_path, content_type, extension, created_at, updated_at, size_bytes, content_hash, data
FROM {Table("artifacts")}
ORDER BY updated_at DESC;
""";
        }
        else
        {
            command.CommandText = $"""
SELECT workspace_id, collection_id, artifact_id, artifact_kind, relative_path, content_type, extension, created_at, updated_at, size_bytes, content_hash, data
FROM {Table("artifacts")}
WHERE artifact_kind = @artifact_kind
ORDER BY updated_at DESC;
""";
            command.Parameters.AddWithValue("artifact_kind", kind.Value.ToString());
        }

        var results = new List<ArtifactManifestEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(BuildManifestEntry(reader));
        }

        return results;
    }

    private async Task<string> InsertArtifactAsync(
        ArtifactDescriptor descriptor,
        string extension,
        string contentType,
        string data,
        long sizeBytes,
        string contentHash,
        CancellationToken cancellationToken)
    {
        var artifactId = Guid.NewGuid().ToString("N");
        var workspaceId = descriptor.WorkspaceId ?? string.Empty;
        var collectionId = descriptor.CollectionId ?? string.Empty;
        var relativePath = BuildRelativePath(workspaceId, collectionId, artifactId, extension);
        var now = DateTimeOffset.UtcNow;
        var artifactKind = descriptor.Kind.ToString();

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("artifacts")} (workspace_id, collection_id, artifact_id, artifact_kind, relative_path, content_type, extension, created_at, updated_at, size_bytes, content_hash, data)
VALUES (@workspace_id, @collection_id, @artifact_id, @artifact_kind, @relative_path, @content_type, @extension, @created_at, @updated_at, @size_bytes, @content_hash, @data);
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("artifact_id", artifactId);
        command.Parameters.AddWithValue("artifact_kind", artifactKind);
        command.Parameters.AddWithValue("relative_path", relativePath);
        command.Parameters.AddWithValue("content_type", contentType);
        command.Parameters.AddWithValue("extension", extension);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("size_bytes", sizeBytes);
        command.Parameters.AddWithValue("content_hash", contentHash);
        var dataParam = command.Parameters.Add("data", NpgsqlDbType.Jsonb);
        dataParam.Value = data;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return relativePath;
    }

    private static string BuildRelativePath(string workspaceId, string collectionId, string artifactId, string extension)
    {
        var ws = string.IsNullOrWhiteSpace(workspaceId) ? "_default" : workspaceId;
        var col = string.IsNullOrWhiteSpace(collectionId) ? "_default" : collectionId;
        return $"{ws}/{col}/{artifactId}{extension}";
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private ArtifactManifestEntry BuildManifestEntry(NpgsqlDataReader reader)
    {
        // 列顺序与 SELECT 一致：workspace_id(0), collection_id(1), artifact_id(2), artifact_kind(3),
        // relative_path(4), content_type(5), extension(6), created_at(7), updated_at(8),
        // size_bytes(9), content_hash(10), data(11)
        var dataJson = reader.GetString(11);
        ArtifactDescriptor descriptor;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.TryGetProperty("descriptor", out var descElement))
            {
                descriptor = JsonSerializer.Deserialize<ArtifactDescriptor>(descElement.GetRawText(), PayloadOptions)
                    ?? new ArtifactDescriptor();
            }
            else
            {
                descriptor = new ArtifactDescriptor();
            }
        }
        catch (JsonException)
        {
            descriptor = new ArtifactDescriptor();
        }

        var kindStr = reader.GetString(3);
        if (!Enum.TryParse<ArtifactKind>(kindStr, out var artifactKind))
        {
            artifactKind = ArtifactKind.Report;
        }

        return new ArtifactManifestEntry
        {
            ArtifactId = reader.GetString(2),
            ArtifactKind = artifactKind,
            Descriptor = descriptor,
            WorkspaceId = reader.GetString(0),
            CollectionId = reader.GetString(1),
            RelativePath = reader.GetString(4),
            FullPath = reader.GetString(4),
            ContentType = reader.GetString(5),
            Extension = reader.GetString(6),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            SizeBytes = reader.GetInt64(9),
            ContentHash = reader.GetString(10),
            IsLatest = true
        };
    }
}
