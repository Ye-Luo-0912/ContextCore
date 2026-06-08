using Npgsql;

namespace ContextCore.Storage.Postgres;

/// <summary>
/// 执行 PostgreSQL 后端的轻量建表迁移。
/// 迁移只创建 ContextCore 自有表和索引，不负责数据库、用户或权限创建。
/// </summary>
public sealed class PostgresMigrationRunner
{
    /// <summary>
    /// 当前 schema 版本标识符。每次修改 DDL（新增表/列/索引）时需递增此版本。
    /// 格式：<c>cc-schema-vN</c>，N 为单调递增整数。
    /// </summary>
    public const string SchemaVersion = "cc-schema-v2";

    private readonly PostgresConnectionFactory _connectionFactory;

    public PostgresMigrationRunner(PostgresConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>生成当前版本需要的建表 SQL，供运行时迁移和测试校验复用。</summary>
    public static string BuildMigrationSql(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var contextItems = PostgresNames.Table(options, "context_items");
        var collections = PostgresNames.Table(options, "collections");
        var memoryItems = PostgresNames.Table(options, "memory_items");
        var relations = PostgresNames.Table(options, "relations");
        var vectors = PostgresNames.Table(options, "vectors");
        var retrievalTraces = PostgresNames.Table(options, "retrieval_traces");
        var contextIndex = PostgresNames.Table(options, "context_index");
        var constraints = PostgresNames.Table(options, "constraints");
        var globalContextItems = PostgresNames.Table(options, "global_context_items");
        var contextJobs = PostgresNames.Table(options, "context_jobs");
        var packageBuildTraces = PostgresNames.Table(options, "package_build_traces");
        var packagePolicies = PostgresNames.Table(options, "package_policies");
        var workingMemoryItems = PostgresNames.Table(options, "working_memory_items");
        var workingMemoryState = PostgresNames.Table(options, "working_memory_state");
        var promotionRecords = PostgresNames.Table(options, "promotion_records");
        var promotionCandidates = PostgresNames.Table(options, "promotion_candidates");
        var schemaVersions = PostgresNames.Table(options, "schema_versions");
        var contextOperationEvents = PostgresNames.Table(options, "context_operation_events");
        var extensionSql = options.EnablePgVectorExtension
            ? "CREATE EXTENSION IF NOT EXISTS vector;"
            : string.Empty;

        return $"""
{extensionSql}

CREATE TABLE IF NOT EXISTS {schemaVersions} (
    version text NOT NULL,
    applied_at timestamptz NOT NULL,
    PRIMARY KEY (version)
);

CREATE TABLE IF NOT EXISTS {contextOperationEvents} (
    event_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NULL,
    operation_id text NOT NULL,
    operation_name text NOT NULL,
    level text NOT NULL,
    message text NOT NULL,
    duration_ms double precision NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, event_id)
);

CREATE INDEX IF NOT EXISTS ix_{contextOperationEvents}_created ON {contextOperationEvents} (workspace_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {collections} (
    workspace_id text NOT NULL,
    id text NOT NULL,
    name text NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE TABLE IF NOT EXISTS {contextItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    type text NOT NULL,
    title text NULL,
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    refs text[] NOT NULL DEFAULT ARRAY[]::text[],
    source_refs text[] NOT NULL DEFAULT ARRAY[]::text[],
    importance double precision NOT NULL,
    version bigint NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{contextItems}_type ON {contextItems} (workspace_id, collection_id, type);
CREATE INDEX IF NOT EXISTS ix_{contextItems}_tags ON {contextItems} USING gin (tags);
CREATE INDEX IF NOT EXISTS ix_{contextItems}_updated ON {contextItems} (workspace_id, collection_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS {memoryItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    layer text NOT NULL,
    status text NOT NULL,
    type text NOT NULL,
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    source_refs text[] NOT NULL DEFAULT ARRAY[]::text[],
    relation_refs text[] NOT NULL DEFAULT ARRAY[]::text[],
    importance double precision NOT NULL,
    confidence double precision NOT NULL,
    version bigint NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{memoryItems}_layer ON {memoryItems} (workspace_id, collection_id, layer, status);
CREATE INDEX IF NOT EXISTS ix_{memoryItems}_tags ON {memoryItems} USING gin (tags);
CREATE INDEX IF NOT EXISTS ix_{memoryItems}_importance ON {memoryItems} (workspace_id, collection_id, importance DESC, updated_at DESC);

CREATE TABLE IF NOT EXISTS {relations} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    source_id text NOT NULL,
    target_id text NOT NULL,
    relation_type text NOT NULL,
    weight double precision NOT NULL,
    confidence double precision NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{relations}_source ON {relations} (workspace_id, collection_id, source_id);
CREATE INDEX IF NOT EXISTS ix_{relations}_target ON {relations} (workspace_id, collection_id, target_id);
CREATE INDEX IF NOT EXISTS ix_{relations}_type ON {relations} (workspace_id, collection_id, relation_type);

CREATE TABLE IF NOT EXISTS {vectors} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    source_id text NOT NULL,
    source_kind text NOT NULL,
    model_name text NOT NULL,
    dimensions integer NOT NULL,
    content_hash text NOT NULL,
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    embedding vector NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{vectors}_scope ON {vectors} (workspace_id, collection_id, source_kind);
CREATE INDEX IF NOT EXISTS ix_{vectors}_tags ON {vectors} USING gin (tags);
CREATE INDEX IF NOT EXISTS ix_{vectors}_updated ON {vectors} (workspace_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS {retrievalTraces} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    retrieval_id text NOT NULL,
    query_text text NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, retrieval_id)
);

CREATE INDEX IF NOT EXISTS ix_{retrievalTraces}_created ON {retrievalTraces} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {contextIndex} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    key text NOT NULL,
    kind text NOT NULL,
    weight double precision NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{contextIndex}_key ON {contextIndex} (workspace_id, collection_id, key text_pattern_ops);
CREATE INDEX IF NOT EXISTS ix_{contextIndex}_kind ON {contextIndex} (workspace_id, collection_id, kind);
CREATE INDEX IF NOT EXISTS ix_{contextIndex}_weight ON {contextIndex} (workspace_id, collection_id, weight DESC);

CREATE TABLE IF NOT EXISTS {constraints} (
    workspace_id text NOT NULL,
    id text NOT NULL,
    collection_id text NULL,
    scope text NOT NULL,
    level text NOT NULL,
    status text NOT NULL,
    confidence double precision NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{constraints}_coll ON {constraints} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS ix_{constraints}_level ON {constraints} (workspace_id, level);

CREATE TABLE IF NOT EXISTS {globalContextItems} (
    workspace_id text NOT NULL,
    id text NOT NULL,
    scope text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{globalContextItems}_scope ON {globalContextItems} (workspace_id, scope);

CREATE TABLE IF NOT EXISTS {contextJobs} (
    job_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    kind text NOT NULL,
    state text NOT NULL,
    priority integer NOT NULL,
    retry_count integer NOT NULL DEFAULT 0,
    max_retry_count integer NOT NULL DEFAULT 3,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (job_id)
);

CREATE INDEX IF NOT EXISTS ix_{contextJobs}_state ON {contextJobs} (state, priority DESC, created_at ASC);
CREATE INDEX IF NOT EXISTS ix_{contextJobs}_scope ON {contextJobs} (workspace_id, collection_id);

CREATE TABLE IF NOT EXISTS {packageBuildTraces} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    build_id text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, build_id)
);

CREATE INDEX IF NOT EXISTS ix_{packageBuildTraces}_created ON {packageBuildTraces} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {packagePolicies} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    id text NOT NULL,
    name text NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{packagePolicies}_name ON {packagePolicies} (workspace_id, name);

CREATE TABLE IF NOT EXISTS {workingMemoryItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    type text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{workingMemoryItems}_created ON {workingMemoryItems} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {workingMemoryState} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    key text NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, key)
);

CREATE TABLE IF NOT EXISTS {promotionRecords} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    source_memory_id text NOT NULL,
    strategy text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{promotionRecords}_source ON {promotionRecords} (workspace_id, collection_id, source_memory_id);
CREATE INDEX IF NOT EXISTS ix_{promotionRecords}_created ON {promotionRecords} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {promotionCandidates} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS ix_{promotionCandidates}_status ON {promotionCandidates} (workspace_id, collection_id, status);
CREATE INDEX IF NOT EXISTS ix_{promotionCandidates}_created ON {promotionCandidates} (workspace_id, collection_id, created_at DESC);
""";
    }

    /// <summary>执行建表迁移。该方法幂等，可在服务启动或首次访问存储时调用。</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildMigrationSql(_connectionFactory.Options);
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // 记录本次已应用的 schema 版本，幂等（ON CONFLICT DO NOTHING）。
        var versionTable = PostgresNames.Table(_connectionFactory.Options, "schema_versions");
        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        versionCmd.CommandText = $"""
            INSERT INTO {versionTable} (version, applied_at)
            VALUES (@version, @applied_at)
            ON CONFLICT (version) DO NOTHING;
            """;
        versionCmd.Parameters.AddWithValue("version", SchemaVersion);
        versionCmd.Parameters.AddWithValue("applied_at", DateTimeOffset.UtcNow);
        await versionCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询数据库中已记录的最高 schema 版本，未迁移时返回 null。</summary>
    public async Task<string?> GetAppliedVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;

        // 表可能还不存在（从未迁移的情况）。
        var versionTable = PostgresNames.Table(_connectionFactory.Options, "schema_versions");
        command.CommandText = $"""
            SELECT version FROM {versionTable}
            ORDER BY applied_at DESC
            LIMIT 1;
            """;
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is string s ? s : null;
        }
        catch (NpgsqlException)
        {
            // schema_versions 表尚不存在时返回 null，表示数据库还未迁移。
            return null;
        }
    }
}