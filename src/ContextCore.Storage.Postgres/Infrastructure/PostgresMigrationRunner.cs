using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres;

/// <summary>
/// 执行 PostgreSQL 后端的轻量建表迁移。
/// 迁移只创建 ContextCore 自有表和索引，不负责数据库、用户或权限创建。
/// </summary>
public sealed class PostgresMigrationRunner : IStoreMigrationRunner
{
    /// <summary>
    /// 当前 schema 版本标识符。每次修改 DDL（新增表/列/索引）时需递增此版本。
    /// 格式：<c>cc-schema-vN</c>，N 为单调递增整数。
    /// </summary>
    public const string SchemaVersion = "cc-schema-v6";

    public const string BaselineMigrationId = "0001_operational_store_baseline";

    public static readonly IReadOnlyList<string> RequiredOperationalTableSuffixes =
    [
        "workspaces",
        "collections",
        "context_items",
        "memory_short_term_items",
        "memory_candidate_items",
        "memory_stable_items",
        "memory_temporal_items",
        "memory_reviews",
        "relations",
        "relation_reviews",
        "relation_diagnostics",
        "constraints_active",
        "constraints_candidate",
        "constraint_gaps",
        "learning_feedback_events",
        "learning_feedback_reviews",
        "learning_feature_candidates",
        "context_jobs",
        "context_job_events",
        "vector_index_entries",
        "vector_index_manifests",
        "context_schema_migrations"
    ];

    public static readonly IReadOnlyList<(string TableSuffix, string IndexSuffix)> RequiredOperationalIndexDefinitions =
    [
        ("context_operation_events", "created"),
        ("context_items", "type"),
        ("context_items", "tags"),
        ("context_items", "updated"),
        ("memory_items", "layer"),
        ("memory_items", "tags"),
        ("memory_items", "importance"),
        ("relations", "source"),
        ("relations", "target"),
        ("relations", "type"),
        ("vectors", "scope"),
        ("vectors", "tags"),
        ("vectors", "updated"),
        ("retrieval_traces", "created"),
        ("context_index", "key"),
        ("context_index", "kind"),
        ("context_index", "weight"),
        ("constraints", "coll"),
        ("constraints", "level"),
        ("global_context_items", "scope"),
        ("context_jobs", "state"),
        ("context_jobs", "scope"),
        ("context_jobs", "kind"),
        ("context_jobs", "lease"),
        ("context_jobs", "attempt"),
        ("package_build_traces", "created"),
        ("package_policies", "name"),
        ("working_memory_items", "created"),
        ("promotion_records", "source"),
        ("promotion_records", "created"),
        ("promotion_candidates", "status"),
        ("promotion_candidates", "created"),
        ("memory_short_term_items", "updated"),
        ("memory_candidate_items", "status"),
        ("memory_stable_items", "lifecycle"),
        ("memory_temporal_items", "range"),
        ("memory_reviews", "memory"),
        ("relation_reviews", "relation"),
        ("relation_diagnostics", "relation"),
        ("relation_diagnostics", "item"),
        ("relation_diagnostics", "kind"),
        ("relation_diagnostics", "severity"),
        ("constraints_active", "scope"),
        ("constraints_candidate", "status"),
        ("constraint_gaps", "status"),
        ("learning_feedback_events", "capability"),
        ("learning_feedback_reviews", "status"),
        ("learning_feature_candidates", "capability"),
        ("context_job_events", "job"),
        ("vector_index_entries", "item"),
        ("vector_index_entries", "scope"),
        ("vector_index_entries", "provider_model_dimension"),
        ("vector_index_entries", "source"),
        ("vector_index_manifests", "updated")
    ];

    private readonly PostgresConnectionFactory _connectionFactory;

    public PostgresMigrationRunner(PostgresConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>生成当前版本需要的建表 SQL，供运行时迁移和测试校验复用。</summary>
    public static string BuildMigrationSql(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workspaces = Infrastructure.PostgresNames.Table(options, "workspaces");
        var contextItems = Infrastructure.PostgresNames.Table(options, "context_items");
        var collections = Infrastructure.PostgresNames.Table(options, "collections");
        var memoryItems = Infrastructure.PostgresNames.Table(options, "memory_items");
        var relations = Infrastructure.PostgresNames.Table(options, "relations");
        var vectors = Infrastructure.PostgresNames.Table(options, "vectors");
        var retrievalTraces = Infrastructure.PostgresNames.Table(options, "retrieval_traces");
        var contextIndex = Infrastructure.PostgresNames.Table(options, "context_index");
        var constraints = Infrastructure.PostgresNames.Table(options, "constraints");
        var globalContextItems = Infrastructure.PostgresNames.Table(options, "global_context_items");
        var contextJobs = Infrastructure.PostgresNames.Table(options, "context_jobs");
        var packageBuildTraces = Infrastructure.PostgresNames.Table(options, "package_build_traces");
        var packagePolicies = Infrastructure.PostgresNames.Table(options, "package_policies");
        var workingMemoryItems = Infrastructure.PostgresNames.Table(options, "working_memory_items");
        var workingMemoryState = Infrastructure.PostgresNames.Table(options, "working_memory_state");
        var promotionRecords = Infrastructure.PostgresNames.Table(options, "promotion_records");
        var promotionCandidates = Infrastructure.PostgresNames.Table(options, "promotion_candidates");
        var schemaVersions = Infrastructure.PostgresNames.Table(options, "schema_versions");
        var contextSchemaMigrations = Infrastructure.PostgresNames.Table(options, "context_schema_migrations");
        var contextOperationEvents = Infrastructure.PostgresNames.Table(options, "context_operation_events");
        var memoryShortTermItems = Infrastructure.PostgresNames.Table(options, "memory_short_term_items");
        var memoryCandidateItems = Infrastructure.PostgresNames.Table(options, "memory_candidate_items");
        var memoryStableItems = Infrastructure.PostgresNames.Table(options, "memory_stable_items");
        var memoryTemporalItems = Infrastructure.PostgresNames.Table(options, "memory_temporal_items");
        var memoryReviews = Infrastructure.PostgresNames.Table(options, "memory_reviews");
        var relationReviews = Infrastructure.PostgresNames.Table(options, "relation_reviews");
        var relationDiagnostics = Infrastructure.PostgresNames.Table(options, "relation_diagnostics");
        var constraintsActive = Infrastructure.PostgresNames.Table(options, "constraints_active");
        var constraintsCandidate = Infrastructure.PostgresNames.Table(options, "constraints_candidate");
        var constraintGaps = Infrastructure.PostgresNames.Table(options, "constraint_gaps");
        var learningFeedbackEvents = Infrastructure.PostgresNames.Table(options, "learning_feedback_events");
        var learningFeedbackReviews = Infrastructure.PostgresNames.Table(options, "learning_feedback_reviews");
        var learningFeatureCandidates = Infrastructure.PostgresNames.Table(options, "learning_feature_candidates");
        var contextJobEvents = Infrastructure.PostgresNames.Table(options, "context_job_events");
        var vectorIndexEntries = Infrastructure.PostgresNames.Table(options, "vector_index_entries");
        var vectorIndexManifests = Infrastructure.PostgresNames.Table(options, "vector_index_manifests");
        var extensionSql = options.EnablePgVectorExtension
            ? "CREATE EXTENSION IF NOT EXISTS vector;"
            : string.Empty;
        var schemaSql = string.IsNullOrWhiteSpace(options.SchemaName)
            ? string.Empty
            : $"CREATE SCHEMA IF NOT EXISTS {options.SchemaName};";

        return $"""
{schemaSql}
{extensionSql}

CREATE TABLE IF NOT EXISTS {contextSchemaMigrations} (
    migration_id text NOT NULL,
    schema_version text NOT NULL,
    applied_at timestamptz NOT NULL,
    checksum text NULL,
    metadata jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (migration_id)
);

CREATE TABLE IF NOT EXISTS {schemaVersions} (
    version text NOT NULL,
    applied_at timestamptz NOT NULL,
    PRIMARY KEY (version)
);

CREATE TABLE IF NOT EXISTS {workspaces} (
    workspace_id text NOT NULL,
    name text NULL,
    status text NOT NULL DEFAULT 'Active',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (workspace_id)
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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_operation_events", "created")} ON {contextOperationEvents} (workspace_id, created_at DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_items", "type")} ON {contextItems} (workspace_id, collection_id, type);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_items", "tags")} ON {contextItems} USING gin (tags);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_items", "updated")} ON {contextItems} (workspace_id, collection_id, updated_at DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_items", "layer")} ON {memoryItems} (workspace_id, collection_id, layer, status);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_items", "tags")} ON {memoryItems} USING gin (tags);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_items", "importance")} ON {memoryItems} (workspace_id, collection_id, importance DESC, updated_at DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relations", "source")} ON {relations} (workspace_id, collection_id, source_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relations", "target")} ON {relations} (workspace_id, collection_id, target_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relations", "type")} ON {relations} (workspace_id, collection_id, relation_type);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vectors", "scope")} ON {vectors} (workspace_id, collection_id, source_kind);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vectors", "tags")} ON {vectors} USING gin (tags);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vectors", "updated")} ON {vectors} (workspace_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS {retrievalTraces} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    retrieval_id text NOT NULL,
    query_text text NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, retrieval_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "retrieval_traces", "created")} ON {retrievalTraces} (workspace_id, collection_id, created_at DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_index", "key")} ON {contextIndex} (workspace_id, collection_id, key text_pattern_ops);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_index", "kind")} ON {contextIndex} (workspace_id, collection_id, kind);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_index", "weight")} ON {contextIndex} (workspace_id, collection_id, weight DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraints", "coll")} ON {constraints} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraints", "level")} ON {constraints} (workspace_id, level);

CREATE TABLE IF NOT EXISTS {globalContextItems} (
    workspace_id text NOT NULL,
    id text NOT NULL,
    scope text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "global_context_items", "scope")} ON {globalContextItems} (workspace_id, scope);

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
    updated_at timestamptz NOT NULL DEFAULT now(),
    lease_owner text NULL,
    lease_expires_at timestamptz NULL,
    last_heartbeat_at timestamptz NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (job_id)
);

ALTER TABLE {contextJobs} ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE {contextJobs} ADD COLUMN IF NOT EXISTS lease_owner text NULL;
ALTER TABLE {contextJobs} ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz NULL;
ALTER TABLE {contextJobs} ADD COLUMN IF NOT EXISTS last_heartbeat_at timestamptz NULL;
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_jobs", "state")} ON {contextJobs} (state, priority DESC, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_jobs", "scope")} ON {contextJobs} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_jobs", "kind")} ON {contextJobs} (kind, state, priority DESC, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_jobs", "lease")} ON {contextJobs} (state, lease_expires_at);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_jobs", "attempt")} ON {contextJobs} (retry_count, max_retry_count, state);

CREATE TABLE IF NOT EXISTS {packageBuildTraces} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    build_id text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, build_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "package_build_traces", "created")} ON {packageBuildTraces} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {packagePolicies} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    id text NOT NULL,
    name text NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "package_policies", "name")} ON {packagePolicies} (workspace_id, name);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "working_memory_items", "created")} ON {workingMemoryItems} (workspace_id, collection_id, created_at DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "promotion_records", "source")} ON {promotionRecords} (workspace_id, collection_id, source_memory_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "promotion_records", "created")} ON {promotionRecords} (workspace_id, collection_id, created_at DESC);

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

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "promotion_candidates", "status")} ON {promotionCandidates} (workspace_id, collection_id, status);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "promotion_candidates", "created")} ON {promotionCandidates} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {memoryShortTermItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    lifecycle text NULL,
    review_status text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_short_term_items", "updated")} ON {memoryShortTermItems} (workspace_id, collection_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS {memoryCandidateItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    status text NOT NULL,
    lifecycle text NULL,
    review_status text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_candidate_items", "status")} ON {memoryCandidateItems} (workspace_id, collection_id, status, updated_at DESC);

CREATE TABLE IF NOT EXISTS {memoryStableItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    lifecycle text NOT NULL,
    review_status text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_stable_items", "lifecycle")} ON {memoryStableItems} (workspace_id, collection_id, lifecycle, updated_at DESC);

CREATE TABLE IF NOT EXISTS {memoryTemporalItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    id text NOT NULL,
    valid_from timestamptz NULL,
    valid_to timestamptz NULL,
    lifecycle text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_temporal_items", "range")} ON {memoryTemporalItems} (workspace_id, collection_id, valid_from, valid_to);

CREATE TABLE IF NOT EXISTS {memoryReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    review_id text NOT NULL,
    memory_id text NOT NULL,
    memory_layer text NOT NULL,
    review_status text NOT NULL,
    reviewer text NULL,
    reviewed_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "memory_reviews", "memory")} ON {memoryReviews} (workspace_id, collection_id, memory_layer, memory_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {relationReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    review_id text NOT NULL,
    relation_id text NOT NULL,
    review_status text NOT NULL,
    reviewer text NULL,
    reviewed_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_reviews", "relation")} ON {relationReviews} (workspace_id, collection_id, relation_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {relationDiagnostics} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    diagnostic_id text NOT NULL,
    relation_id text NULL,
    item_id text NULL,
    diagnostic_kind text NOT NULL,
    severity text NOT NULL,
    message text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, diagnostic_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_diagnostics", "relation")} ON {relationDiagnostics} (workspace_id, collection_id, relation_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_diagnostics", "item")} ON {relationDiagnostics} (workspace_id, collection_id, item_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_diagnostics", "kind")} ON {relationDiagnostics} (workspace_id, collection_id, diagnostic_kind, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_diagnostics", "severity")} ON {relationDiagnostics} (workspace_id, collection_id, severity, created_at DESC);

CREATE TABLE IF NOT EXISTS {constraintsActive} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    id text NOT NULL,
    scope text NOT NULL,
    lifecycle text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraints_active", "scope")} ON {constraintsActive} (workspace_id, collection_id, scope, lifecycle);

CREATE TABLE IF NOT EXISTS {constraintsCandidate} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    id text NOT NULL,
    status text NOT NULL,
    source_gap_id text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraints_candidate", "status")} ON {constraintsCandidate} (workspace_id, collection_id, status, updated_at DESC);

CREATE TABLE IF NOT EXISTS {constraintGaps} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    id text NOT NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraint_gaps", "status")} ON {constraintGaps} (workspace_id, collection_id, status, updated_at DESC);

CREATE TABLE IF NOT EXISTS {learningFeedbackEvents} (
    feedback_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NULL,
    capability_id text NOT NULL,
    target_id text NOT NULL,
    target_type text NOT NULL,
    feedback_kind text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (feedback_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "learning_feedback_events", "capability")} ON {learningFeedbackEvents} (workspace_id, collection_id, capability_id, feedback_kind, created_at DESC);

CREATE TABLE IF NOT EXISTS {learningFeedbackReviews} (
    feedback_id text NOT NULL,
    review_id text NOT NULL,
    review_status text NOT NULL,
    reviewer text NULL,
    reviewed_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (feedback_id, review_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "learning_feedback_reviews", "status")} ON {learningFeedbackReviews} (review_status, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {learningFeatureCandidates} (
    candidate_id text NOT NULL,
    source_feedback_id text NOT NULL,
    capability_id text NOT NULL,
    label_kind text NOT NULL,
    training_use text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (candidate_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "learning_feature_candidates", "capability")} ON {learningFeatureCandidates} (capability_id, label_kind, created_at DESC);

CREATE TABLE IF NOT EXISTS {contextJobEvents} (
    event_id text NOT NULL,
    job_id text NOT NULL,
    event_kind text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (event_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_job_events", "job")} ON {contextJobEvents} (job_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {vectorIndexEntries} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    entry_id text NOT NULL,
    item_id text NOT NULL,
    source_id text NOT NULL DEFAULT '',
    source_kind text NOT NULL DEFAULT '',
    item_kind text NOT NULL,
    layer text NOT NULL,
    embedding_provider text NOT NULL,
    provider_id text NOT NULL DEFAULT '',
    embedding_model text NOT NULL,
    model_id text NOT NULL DEFAULT '',
    dimension integer NOT NULL,
    normalized boolean NOT NULL DEFAULT true,
    content_hash text NOT NULL,
    vector vector NOT NULL,
    metadata_json jsonb NOT NULL DEFAULT jsonb_build_object(),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, entry_id)
);

ALTER TABLE {vectorIndexEntries} ADD COLUMN IF NOT EXISTS source_id text NOT NULL DEFAULT '';
ALTER TABLE {vectorIndexEntries} ADD COLUMN IF NOT EXISTS source_kind text NOT NULL DEFAULT '';
ALTER TABLE {vectorIndexEntries} ADD COLUMN IF NOT EXISTS provider_id text NOT NULL DEFAULT '';
ALTER TABLE {vectorIndexEntries} ADD COLUMN IF NOT EXISTS model_id text NOT NULL DEFAULT '';
ALTER TABLE {vectorIndexEntries} ADD COLUMN IF NOT EXISTS normalized boolean NOT NULL DEFAULT true;
ALTER TABLE {vectorIndexEntries} ADD COLUMN IF NOT EXISTS metadata_json jsonb NOT NULL DEFAULT jsonb_build_object();
UPDATE {vectorIndexEntries}
SET source_id = CASE WHEN source_id = '' THEN item_id ELSE source_id END,
    source_kind = CASE WHEN source_kind = '' THEN item_kind ELSE source_kind END,
    provider_id = CASE WHEN provider_id = '' THEN embedding_provider ELSE provider_id END,
    model_id = CASE WHEN model_id = '' THEN embedding_model ELSE model_id END,
    metadata_json = CASE WHEN metadata_json = jsonb_build_object() THEN COALESCE(data->'metadata', jsonb_build_object()) ELSE metadata_json END;
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_index_entries", "item")} ON {vectorIndexEntries} (workspace_id, collection_id, item_id, embedding_provider, embedding_model);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_index_entries", "scope")} ON {vectorIndexEntries} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_index_entries", "provider_model_dimension")} ON {vectorIndexEntries} (workspace_id, collection_id, provider_id, model_id, dimension);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_index_entries", "source")} ON {vectorIndexEntries} (workspace_id, collection_id, source_id);

CREATE TABLE IF NOT EXISTS {vectorIndexManifests} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    provider_id text NOT NULL,
    embedding_model text NOT NULL,
    dimension integer NOT NULL,
    indexed_count integer NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, provider_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_index_manifests", "updated")} ON {vectorIndexManifests} (workspace_id, collection_id, updated_at DESC);
""";
    }

    public IReadOnlyList<PostgresStoreMigration> ListMigrations()
    {
        return
        [
            new PostgresStoreMigration
            {
                MigrationId = BaselineMigrationId,
                Description = "DB1 operational store baseline schema",
                SchemaVersion = SchemaVersion,
                RequiredTables = RequiredOperationalTableSuffixes
            }
        ];
    }

    public async Task<PostgresMigrationPlan> PreviewMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = await GetAppliedVersionAsync(cancellationToken).ConfigureAwait(false);
        var missingTables = await GetMissingRequiredTablesAsync(cancellationToken).ConfigureAwait(false);
        var pending = currentVersion == SchemaVersion && missingTables.Count == 0
            ? Array.Empty<string>()
            : new[] { BaselineMigrationId };
        return new PostgresMigrationPlan
        {
            DryRun = true,
            ProviderEnabled = _connectionFactory.Options.Enabled,
            ProviderId = _connectionFactory.Options.ProviderId,
            SchemaName = _connectionFactory.Options.SchemaName,
            RedactedConnectionString = RedactConnectionString(_connectionFactory.Options.ConnectionString),
            CurrentSchemaVersion = currentVersion,
            Migrations = ListMigrations(),
            PendingMigrations = pending,
            RequiredTables = GetRequiredTableNames(_connectionFactory.Options),
            MissingRequiredTables = missingTables,
            Diagnostics = pending.Length == 0 ? Array.Empty<string>() : new[] { "PendingMigrationsDetected" }
        };
    }

    public async Task<PostgresMigrationApplyResult> ApplyMigrationsAsync(
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return new PostgresMigrationApplyResult
            {
                Applied = false,
                ConfirmRequired = true,
                Diagnostics = ["ConfirmRequired"]
            };
        }

        await MigrateAsync(cancellationToken).ConfigureAwait(false);
        return new PostgresMigrationApplyResult
        {
            Applied = true,
            ConfirmRequired = false,
            SchemaVersion = SchemaVersion,
            AppliedMigrations = [BaselineMigrationId]
        };
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
        var versionTable = Infrastructure.PostgresNames.Table(_connectionFactory.Options, "schema_versions");
        var migrationsTable = Infrastructure.PostgresNames.Table(_connectionFactory.Options, "context_schema_migrations");
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

        await using var migrationCmd = connection.CreateCommand();
        migrationCmd.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        migrationCmd.CommandText = $"""
            INSERT INTO {migrationsTable} (migration_id, schema_version, applied_at, checksum, metadata)
            VALUES (@migration_id, @schema_version, @applied_at, @checksum, jsonb_build_object())
            ON CONFLICT (migration_id) DO UPDATE
            SET schema_version = EXCLUDED.schema_version,
                applied_at = EXCLUDED.applied_at,
                checksum = EXCLUDED.checksum;
            """;
        migrationCmd.Parameters.AddWithValue("migration_id", BaselineMigrationId);
        migrationCmd.Parameters.AddWithValue("schema_version", SchemaVersion);
        migrationCmd.Parameters.AddWithValue("applied_at", DateTimeOffset.UtcNow);
        migrationCmd.Parameters.AddWithValue("checksum", "db5-0-vector-index-provider");
        await migrationCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询数据库中已记录的最高 schema 版本，未迁移时返回 null。</summary>
    public async Task<string?> GetAppliedVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;

        // 表可能还不存在（从未迁移的情况）。
        var migrationTable = Infrastructure.PostgresNames.Table(_connectionFactory.Options, "context_schema_migrations");
        command.CommandText = $"""
            SELECT schema_version FROM {migrationTable}
            ORDER BY applied_at DESC
            LIMIT 1;
            """;
        try
        {
            var migrationResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (migrationResult is string migrationVersion)
            {
                return migrationVersion;
            }
        }
        catch (NpgsqlException)
        {
            // DB1 migration table 尚不存在时继续读取旧 schema_versions。
        }

        var versionTable = Infrastructure.PostgresNames.Table(_connectionFactory.Options, "schema_versions");
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

    public static IReadOnlyList<string> GetRequiredTableNames(PostgresOptions options)
    {
        return RequiredOperationalTableSuffixes
            .Select(suffix => Infrastructure.PostgresNames.Table(options, suffix))
            .ToArray();
    }

    public static IReadOnlyList<string> GetRequiredIndexNames(PostgresOptions options)
    {
        return RequiredOperationalIndexDefinitions
            .Select(definition => Infrastructure.PostgresNames.QualifiedIndex(
                options,
                definition.TableSuffix,
                definition.IndexSuffix))
            .ToArray();
    }

    public async Task<PostgresSchemaVerificationReport> VerifySchemaAsync(CancellationToken cancellationToken = default)
    {
        var options = _connectionFactory.Options;
        var diagnostics = new List<string>();
        bool connectionAvailable;
        try
        {
            var ping = await _connectionFactory.PingAsync(cancellationToken).ConfigureAwait(false);
            connectionAvailable = ping.Success;
            if (!ping.Success)
            {
                diagnostics.Add("ConnectionTestFailed");
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            connectionAvailable = false;
            diagnostics.Add("ConnectionTestFailed");
        }

        if (!connectionAvailable)
        {
            return new PostgresSchemaVerificationReport
            {
                ProviderEnabled = options.Enabled,
                ConnectionAvailable = false,
                SchemaName = options.SchemaName,
                RequiredTableCount = RequiredOperationalTableSuffixes.Count,
                MissingRequiredTableCount = RequiredOperationalTableSuffixes.Count,
                RequiredIndexCount = RequiredOperationalIndexDefinitions.Count,
                MissingIndexCount = RequiredOperationalIndexDefinitions.Count,
                RequiredTables = GetRequiredTableNames(options),
                MissingRequiredTables = GetRequiredTableNames(options),
                RequiredIndexes = GetRequiredIndexNames(options),
                MissingIndexes = GetRequiredIndexNames(options),
                Diagnostics = diagnostics.Count == 0 ? ["BlockedByConnection"] : diagnostics,
                Recommendation = "BlockedByConnection"
            };
        }

        var currentVersion = await GetAppliedVersionAsync(cancellationToken).ConfigureAwait(false);
        var missingTables = await GetMissingRequiredTablesAsync(cancellationToken).ConfigureAwait(false);
        var missingIndexes = await GetMissingRequiredIndexesAsync(cancellationToken).ConfigureAwait(false);
        var appliedMigrationCount = await GetAppliedMigrationCountAsync(cancellationToken).ConfigureAwait(false);
        if (missingTables.Count > 0)
        {
            diagnostics.Add("MissingRequiredTables");
        }

        if (missingIndexes.Count > 0)
        {
            diagnostics.Add("MissingRequiredIndexes");
        }

        if (currentVersion != SchemaVersion)
        {
            diagnostics.Add("SchemaVersionOutOfDate");
        }

        return new PostgresSchemaVerificationReport
        {
            ProviderEnabled = options.Enabled,
            ConnectionAvailable = true,
            SchemaName = options.SchemaName,
            CurrentSchemaVersion = currentVersion,
            AppliedMigrationCount = appliedMigrationCount,
            RequiredTableCount = RequiredOperationalTableSuffixes.Count,
            MissingRequiredTableCount = missingTables.Count,
            RequiredIndexCount = RequiredOperationalIndexDefinitions.Count,
            MissingIndexCount = missingIndexes.Count,
            RequiredTables = GetRequiredTableNames(options),
            MissingRequiredTables = missingTables,
            RequiredIndexes = GetRequiredIndexNames(options),
            MissingIndexes = missingIndexes,
            Diagnostics = diagnostics,
            Recommendation = missingTables.Count == 0 && missingIndexes.Count == 0 && currentVersion == SchemaVersion
                ? "ReadyForProviderDevelopment"
                : "SchemaIncomplete"
        };
    }

    public async Task<bool> DropSchemaAsync(bool confirm, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_connectionFactory.Options.SchemaName))
        {
            throw new InvalidOperationException("清理 smoke schema 必须显式配置 SchemaName，禁止删除默认 search_path 中的对象。");
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        command.CommandText = $"DROP SCHEMA IF EXISTS {_connectionFactory.Options.SchemaName} CASCADE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static string RedactConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "***";
            }

            if (!string.IsNullOrEmpty(builder.Username))
            {
                builder.Username = "***";
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "InvalidConnectionString(redacted)";
        }
    }

    private async Task<IReadOnlyList<string>> GetMissingRequiredTablesAsync(CancellationToken cancellationToken)
    {
        var required = GetRequiredTableNames(_connectionFactory.Options);
        var missing = new List<string>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in required)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = "SELECT to_regclass(@table_name)::text;";
            command.Parameters.AddWithValue("table_name", table);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null or DBNull)
            {
                missing.Add(table);
            }
        }

        return missing;
    }

    private async Task<IReadOnlyList<string>> GetMissingRequiredIndexesAsync(CancellationToken cancellationToken)
    {
        var required = GetRequiredIndexNames(_connectionFactory.Options);
        var missing = new List<string>();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var index in required)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
            command.CommandText = "SELECT to_regclass(@index_name)::text;";
            command.Parameters.AddWithValue("index_name", index);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null or DBNull)
            {
                missing.Add(index);
            }
        }

        return missing;
    }

    private async Task<int> GetAppliedMigrationCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {Infrastructure.PostgresNames.Table(_connectionFactory.Options, "context_schema_migrations")};";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is long count ? checked((int)count) : 0;
        }
        catch (NpgsqlException)
        {
            return 0;
        }
    }
}
