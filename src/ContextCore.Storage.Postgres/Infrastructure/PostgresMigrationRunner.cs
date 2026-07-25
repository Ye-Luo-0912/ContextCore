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
    /// P1-5：v7 → v8，新增 relation_outbox 表与索引。
    /// R14-PG-2：v8 → v9，新增 decision_traces 表与索引。
    /// R14-PG-3：v9 → v10，新增 short-term memory / promotion / candidate review 表与索引。
    /// R14-PG-4：v10 → v11，新增 context learning / governance review 表与索引。
    /// R14-PG-5：v11 → v12，新增 vector lifecycle + artifact 表与索引。
    /// R14-PG-6：v12 → v13，新增 context_state_versions 表用于分布式版本号。
    /// R26-1：v13 → v14，新增 agent_checkpoints + agent_task_states 表与索引（Agent Runtime 持久化）。
    /// R27-1：v14 → v15，新增 pipeline_runs + pipeline_canary_assignments + pipeline_rollback_records + pipeline_baseline_comparisons 表与索引（Evolution Pipeline 持久化）。
    /// P0-7：v15 → v16，pipeline_runs 表追加 revision / lease_owner / lease_expires_at / last_transition_id 列支持 HA CAS 推进。
    /// WS-A：v16 → v17，新增 policy_bundles + policy_activations 表与索引（Postgres Policy Registry 持久化 + CAS 激活）。
    /// R28-B.6 阶段 E：v17 → v18，新增 experiment_replay_fixtures 表与索引（Postgres Experiment Recorder 持久化 replay fixture）。
    /// R28-B.8：v18 → v19，新增 stage_transitions 表与索引（Canary Gate 渐进推进审计表，独立于 pipeline_runs 的 transition audit）。
    /// R28-E：v19 → v20，新增 utility_ledger_entries + conflict_sets 表与索引（Durable Memory Governance 持久化：Utility/Conflict durable projection）。
    /// R29 WP-B-1：v20 → v21，新增 tool_dispatch_journal_entries 表与索引（持久化 Tool Dispatch Journal，支持 HA 崩溃恢复 exactly-once）。
    /// </summary>
    public const string SchemaVersion = "cc-schema-v21";

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
        "relation_outbox",
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
        "decision_traces",
        "short_term_raw_events",
        "short_term_working_items",
        "short_term_archived_raw_events",
        "short_term_archived_working_items",
        "short_term_compaction_runs",
        "short_term_promotion_candidates",
        "short_term_promotion_candidate_reviews",
        "candidate_memory_reviews",
        "stable_review_candidates",
        "stable_review_records",
        "context_learning_feedback",
        "context_learning_records",
        "context_learning_cases",
        "stable_lifecycle_reviews",
        "candidate_constraint_reviews",
        "constraint_gap_candidates",
        "constraint_gap_reviews",
        // R14-PG-5：vector lifecycle + artifact 表
        "artifacts",
        "vector_lifecycle_metadata_review_candidates",
        "vector_lifecycle_metadata_reviews",
        "vector_lifecycle_sidecar_metadata",
        "vector_reindex_reports",
        // R14-PG-6：分布式 context state 版本号表（不同于 schema_versions 用于 schema migration 跟踪）
        "context_state_versions",
        "context_schema_migrations",
        // R26-1：Agent Runtime 持久化（checkpoint + task state）
        "agent_checkpoints",
        "agent_task_states",
        // R27-1：Evolution Pipeline 持久化（run state + 3 audit tables）
        "pipeline_runs",
        "pipeline_canary_assignments",
        "pipeline_rollback_records",
        "pipeline_baseline_comparisons",
        // WS-A：Policy Registry 持久化（bundle 注册 + activation CAS 激活）
        "policy_bundles",
        "policy_activations",
        // R28-B.6 阶段 E：Experiment Recorder 持久化（replay fixture 存储）
        "experiment_replay_fixtures",
        // R28-B.8：Canary Gate 渐进推进审计表（独立于 pipeline_runs 的 transition audit，记录每次 stage 推进的完整历史）
        "stage_transitions",
        // R28-E：Durable Memory Governance 持久化（Utility Ledger + ConflictSet durable projection）
        "utility_ledger_entries",
        "conflict_sets",
        // R29 WP-B-1：Tool Dispatch Journal 持久化（HA 崩溃恢复 exactly-once）
        "tool_dispatch_journal_entries"
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
        ("relation_outbox", "state"),
        ("relation_outbox", "lease"),
        ("relation_outbox", "relation"),
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
        ("vector_index_manifests", "updated"),
        ("decision_traces", "created"),
        ("short_term_raw_events", "created"),
        ("short_term_working_items", "updated"),
        ("short_term_working_items", "expires"),
        ("short_term_archived_raw_events", "archived"),
        ("short_term_archived_working_items", "archived"),
        ("short_term_compaction_runs", "started"),
        ("short_term_promotion_candidates", "created"),
        ("short_term_promotion_candidates", "status"),
        ("short_term_promotion_candidate_reviews", "candidate"),
        ("short_term_promotion_candidate_reviews", "reviewed"),
        ("candidate_memory_reviews", "candidate"),
        ("candidate_memory_reviews", "reviewed"),
        ("stable_review_candidates", "created"),
        ("stable_review_candidates", "status"),
        ("stable_review_records", "candidate"),
        ("stable_review_records", "reviewed"),
        ("context_learning_feedback", "candidate"),
        ("context_learning_feedback", "created"),
        ("context_learning_records", "workspace"),
        ("context_learning_records", "created"),
        ("context_learning_cases", "workspace"),
        ("context_learning_cases", "created"),
        ("stable_lifecycle_reviews", "item"),
        ("stable_lifecycle_reviews", "reviewed"),
        ("candidate_constraint_reviews", "constraint"),
        ("candidate_constraint_reviews", "reviewed"),
        ("constraint_gap_candidates", "created"),
        ("constraint_gap_candidates", "status"),
        ("constraint_gap_reviews", "gap"),
        ("constraint_gap_reviews", "reviewed"),
        // R14-PG-5：vector lifecycle + artifact 索引
        ("artifacts", "kind"),
        ("artifacts", "updated"),
        ("vector_lifecycle_metadata_review_candidates", "created"),
        ("vector_lifecycle_metadata_review_candidates", "status"),
        ("vector_lifecycle_metadata_reviews", "candidate"),
        ("vector_lifecycle_metadata_reviews", "reviewed"),
        ("vector_lifecycle_sidecar_metadata", "created"),
        ("vector_reindex_reports", "created"),
        // R26-1：Agent Runtime 持久化索引
        ("agent_checkpoints", "session"),
        ("agent_checkpoints", "created"),
        ("agent_task_states", "session"),
        ("agent_task_states", "updated"),
        // R27-1：Evolution Pipeline 持久化索引
        ("pipeline_runs", "proposal"),
        ("pipeline_runs", "status"),
        ("pipeline_runs", "updated"),
        ("pipeline_canary_assignments", "run"),
        ("pipeline_canary_assignments", "assigned"),
        ("pipeline_rollback_records", "run"),
        ("pipeline_rollback_records", "triggered"),
        ("pipeline_baseline_comparisons", "proposal"),
        ("pipeline_baseline_comparisons", "compared"),
        // WS-A：Policy Registry 索引
        ("policy_bundles", "bundle"),
        ("policy_bundles", "superseded"),
        ("policy_activations", "bundle"),
        // R28-B.6 阶段 E：experiment_replay_fixtures 索引（按时间倒序 + 按 purpose 过滤）
        ("experiment_replay_fixtures", "recorded"),
        ("experiment_replay_fixtures", "purpose"),
        // R28-B.8：stage_transitions 索引（按 run_id 查历史 + 按 idempotency_key 去重）
        ("stage_transitions", "run_id"),
        ("stage_transitions", "idempotency"),
        // R28-E：Durable Memory Governance 索引（utility_ledger 按作用域/候选/决策/时间查；conflict_sets 按作用域/状态/候选查）
        ("utility_ledger_entries", "workspace"),
        ("utility_ledger_entries", "candidate"),
        ("utility_ledger_entries", "decision"),
        ("utility_ledger_entries", "materialized"),
        ("conflict_sets", "workspace"),
        ("conflict_sets", "status"),
        ("conflict_sets", "candidate"),
        // R29 WP-B-1：Tool Dispatch Journal 索引（按 state 查待恢复条目 + 按 idempotency_key 去重）
        ("tool_dispatch_journal_entries", "state"),
        ("tool_dispatch_journal_entries", "idempotency")
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
        var decisionTraces = Infrastructure.PostgresNames.Table(options, "decision_traces");
        var shortTermRawEvents = Infrastructure.PostgresNames.Table(options, "short_term_raw_events");
        var shortTermWorkingItems = Infrastructure.PostgresNames.Table(options, "short_term_working_items");
        var shortTermArchivedRawEvents = Infrastructure.PostgresNames.Table(options, "short_term_archived_raw_events");
        var shortTermArchivedWorkingItems = Infrastructure.PostgresNames.Table(options, "short_term_archived_working_items");
        var shortTermCompactionRuns = Infrastructure.PostgresNames.Table(options, "short_term_compaction_runs");
        var shortTermPromotionCandidates = Infrastructure.PostgresNames.Table(options, "short_term_promotion_candidates");
        var shortTermPromotionCandidateReviews = Infrastructure.PostgresNames.Table(options, "short_term_promotion_candidate_reviews");
        var candidateMemoryReviews = Infrastructure.PostgresNames.Table(options, "candidate_memory_reviews");
        var stableReviewCandidates = Infrastructure.PostgresNames.Table(options, "stable_review_candidates");
        var stableReviewRecords = Infrastructure.PostgresNames.Table(options, "stable_review_records");
        var contextLearningFeedback = Infrastructure.PostgresNames.Table(options, "context_learning_feedback");
        var contextLearningRecords = Infrastructure.PostgresNames.Table(options, "context_learning_records");
        var contextLearningCases = Infrastructure.PostgresNames.Table(options, "context_learning_cases");
        var stableLifecycleReviews = Infrastructure.PostgresNames.Table(options, "stable_lifecycle_reviews");
        var candidateConstraintReviews = Infrastructure.PostgresNames.Table(options, "candidate_constraint_reviews");
        var constraintGapCandidates = Infrastructure.PostgresNames.Table(options, "constraint_gap_candidates");
        var constraintGapReviews = Infrastructure.PostgresNames.Table(options, "constraint_gap_reviews");
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
        var relationOutbox = Infrastructure.PostgresNames.Table(options, "relation_outbox");
        var constraintsActive = Infrastructure.PostgresNames.Table(options, "constraints_active");
        var constraintsCandidate = Infrastructure.PostgresNames.Table(options, "constraints_candidate");
        var constraintGaps = Infrastructure.PostgresNames.Table(options, "constraint_gaps");
        var learningFeedbackEvents = Infrastructure.PostgresNames.Table(options, "learning_feedback_events");
        var learningFeedbackReviews = Infrastructure.PostgresNames.Table(options, "learning_feedback_reviews");
        var learningFeatureCandidates = Infrastructure.PostgresNames.Table(options, "learning_feature_candidates");
        var contextJobEvents = Infrastructure.PostgresNames.Table(options, "context_job_events");
        var vectorIndexEntries = Infrastructure.PostgresNames.Table(options, "vector_index_entries");
        var vectorIndexManifests = Infrastructure.PostgresNames.Table(options, "vector_index_manifests");
        // R14-PG-5：vector lifecycle + artifact 表
        var vectorReindexReports = Infrastructure.PostgresNames.Table(options, "vector_reindex_reports");
        var vectorLifecycleMetadataReviewCandidates = Infrastructure.PostgresNames.Table(options, "vector_lifecycle_metadata_review_candidates");
        var vectorLifecycleMetadataReviews = Infrastructure.PostgresNames.Table(options, "vector_lifecycle_metadata_reviews");
        var vectorLifecycleSidecarMetadata = Infrastructure.PostgresNames.Table(options, "vector_lifecycle_sidecar_metadata");
        var artifacts = Infrastructure.PostgresNames.Table(options, "artifacts");
        // R14-PG-6：分布式 context state 版本号表
        var contextStateVersions = Infrastructure.PostgresNames.Table(options, "context_state_versions");
        // R26-1：Agent Runtime 持久化表
        var agentCheckpoints = Infrastructure.PostgresNames.Table(options, "agent_checkpoints");
        var agentTaskStates = Infrastructure.PostgresNames.Table(options, "agent_task_states");
        // R27-1：Evolution Pipeline 持久化表
        var pipelineRuns = Infrastructure.PostgresNames.Table(options, "pipeline_runs");
        var pipelineCanaryAssignments = Infrastructure.PostgresNames.Table(options, "pipeline_canary_assignments");
        var pipelineRollbackRecords = Infrastructure.PostgresNames.Table(options, "pipeline_rollback_records");
        var pipelineBaselineComparisons = Infrastructure.PostgresNames.Table(options, "pipeline_baseline_comparisons");
        // WS-A：Policy Registry 持久化表
        var policyBundles = Infrastructure.PostgresNames.Table(options, "policy_bundles");
        var policyActivations = Infrastructure.PostgresNames.Table(options, "policy_activations");
        // R28-B.6 阶段 E：Experiment Recorder 持久化表
        var experimentReplayFixtures = Infrastructure.PostgresNames.Table(options, "experiment_replay_fixtures");
        // R28-B.8：Canary Gate 渐进推进审计表（独立于 pipeline_runs 的 transition audit）
        var stageTransitions = Infrastructure.PostgresNames.Table(options, "stage_transitions");
        // R28-E：Durable Memory Governance 持久化表
        var utilityLedgerEntries = Infrastructure.PostgresNames.Table(options, "utility_ledger_entries");
        var conflictSets = Infrastructure.PostgresNames.Table(options, "conflict_sets");
        // R29 WP-B-1：Tool Dispatch Journal 持久化表
        var toolDispatchJournalEntries = Infrastructure.PostgresNames.Table(options, "tool_dispatch_journal_entries");
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

ALTER TABLE {contextOperationEvents} ADD COLUMN IF NOT EXISTS entity_type text NULL;
ALTER TABLE {contextOperationEvents} ADD COLUMN IF NOT EXISTS entity_id text NULL;
ALTER TABLE {contextOperationEvents} ADD COLUMN IF NOT EXISTS operation text NULL;

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

CREATE TABLE IF NOT EXISTS {decisionTraces} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    decision_id text NOT NULL,
    source text NOT NULL DEFAULT '',
    query_text text NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, decision_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "decision_traces", "created")} ON {decisionTraces} (workspace_id, collection_id, created_at DESC);

-- R14-PG-3：short-term memory / promotion / candidate review 表与索引
CREATE TABLE IF NOT EXISTS {shortTermRawEvents} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    event_id text NOT NULL,
    kind text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, event_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_raw_events", "created")} ON {shortTermRawEvents} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {shortTermWorkingItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    item_id text NOT NULL,
    kind text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, item_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_working_items", "updated")} ON {shortTermWorkingItems} (workspace_id, collection_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_working_items", "expires")} ON {shortTermWorkingItems} (workspace_id, collection_id, expires_at);

CREATE TABLE IF NOT EXISTS {shortTermArchivedRawEvents} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    event_id text NOT NULL,
    archived_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, event_id, archived_at)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_archived_raw_events", "archived")} ON {shortTermArchivedRawEvents} (workspace_id, collection_id, archived_at DESC);

CREATE TABLE IF NOT EXISTS {shortTermArchivedWorkingItems} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    item_id text NOT NULL,
    archived_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, item_id, archived_at)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_archived_working_items", "archived")} ON {shortTermArchivedWorkingItems} (workspace_id, collection_id, archived_at DESC);

CREATE TABLE IF NOT EXISTS {shortTermCompactionRuns} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    run_id text NOT NULL,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, run_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_compaction_runs", "started")} ON {shortTermCompactionRuns} (workspace_id, collection_id, started_at DESC);

CREATE TABLE IF NOT EXISTS {shortTermPromotionCandidates} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    candidate_id text NOT NULL,
    kind text NOT NULL DEFAULT '',
    status text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, candidate_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_promotion_candidates", "created")} ON {shortTermPromotionCandidates} (workspace_id, collection_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_promotion_candidates", "status")} ON {shortTermPromotionCandidates} (workspace_id, collection_id, status);

CREATE TABLE IF NOT EXISTS {shortTermPromotionCandidateReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    review_id text NOT NULL,
    candidate_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_promotion_candidate_reviews", "candidate")} ON {shortTermPromotionCandidateReviews} (workspace_id, collection_id, candidate_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "short_term_promotion_candidate_reviews", "reviewed")} ON {shortTermPromotionCandidateReviews} (workspace_id, collection_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {candidateMemoryReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL DEFAULT '',
    review_id text NOT NULL,
    candidate_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "candidate_memory_reviews", "candidate")} ON {candidateMemoryReviews} (workspace_id, candidate_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "candidate_memory_reviews", "reviewed")} ON {candidateMemoryReviews} (workspace_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {stableReviewCandidates} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    stable_review_candidate_id text NOT NULL,
    kind text NOT NULL DEFAULT '',
    status text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, stable_review_candidate_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stable_review_candidates", "created")} ON {stableReviewCandidates} (workspace_id, collection_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stable_review_candidates", "status")} ON {stableReviewCandidates} (workspace_id, collection_id, status);

CREATE TABLE IF NOT EXISTS {stableReviewRecords} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    review_id text NOT NULL,
    stable_review_candidate_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stable_review_records", "candidate")} ON {stableReviewRecords} (workspace_id, collection_id, stable_review_candidate_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stable_review_records", "reviewed")} ON {stableReviewRecords} (workspace_id, collection_id, reviewed_at DESC);

-- R14-PG-4：context learning / governance review 表与索引
CREATE TABLE IF NOT EXISTS {contextLearningFeedback} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    feedback_id text NOT NULL,
    candidate_id text NOT NULL,
    capability_id text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, feedback_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_learning_feedback", "candidate")} ON {contextLearningFeedback} (workspace_id, collection_id, candidate_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_learning_feedback", "created")} ON {contextLearningFeedback} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {contextLearningRecords} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    record_id text NOT NULL,
    source_id text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, record_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_learning_records", "workspace")} ON {contextLearningRecords} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_learning_records", "created")} ON {contextLearningRecords} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {contextLearningCases} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    case_id text NOT NULL,
    source_record_id text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, case_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_learning_cases", "workspace")} ON {contextLearningCases} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_learning_cases", "created")} ON {contextLearningCases} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {stableLifecycleReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL DEFAULT '',
    review_id text NOT NULL,
    stable_item_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stable_lifecycle_reviews", "item")} ON {stableLifecycleReviews} (workspace_id, stable_item_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stable_lifecycle_reviews", "reviewed")} ON {stableLifecycleReviews} (workspace_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {candidateConstraintReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL DEFAULT '',
    review_id text NOT NULL,
    constraint_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "candidate_constraint_reviews", "constraint")} ON {candidateConstraintReviews} (workspace_id, constraint_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "candidate_constraint_reviews", "reviewed")} ON {candidateConstraintReviews} (workspace_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {constraintGapCandidates} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    gap_id text NOT NULL,
    status text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, gap_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraint_gap_candidates", "created")} ON {constraintGapCandidates} (workspace_id, collection_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraint_gap_candidates", "status")} ON {constraintGapCandidates} (workspace_id, collection_id, status);

CREATE TABLE IF NOT EXISTS {constraintGapReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    review_id text NOT NULL,
    gap_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraint_gap_reviews", "gap")} ON {constraintGapReviews} (workspace_id, collection_id, gap_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "constraint_gap_reviews", "reviewed")} ON {constraintGapReviews} (workspace_id, collection_id, reviewed_at DESC);

-- R14-PG-5：vector lifecycle + artifact 表与索引
CREATE TABLE IF NOT EXISTS {vectorReindexReports} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    report_id text NOT NULL,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, report_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_reindex_reports", "created")} ON {vectorReindexReports} (workspace_id, collection_id, completed_at DESC);

CREATE TABLE IF NOT EXISTS {vectorLifecycleMetadataReviewCandidates} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    candidate_id text NOT NULL,
    status text NOT NULL DEFAULT '',
    layer text NOT NULL DEFAULT '',
    item_kind text NOT NULL DEFAULT '',
    must_hit_item_id text NOT NULL DEFAULT '',
    source_eval_set text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, candidate_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_lifecycle_metadata_review_candidates", "created")} ON {vectorLifecycleMetadataReviewCandidates} (workspace_id, collection_id, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_lifecycle_metadata_review_candidates", "status")} ON {vectorLifecycleMetadataReviewCandidates} (workspace_id, collection_id, status);

CREATE TABLE IF NOT EXISTS {vectorLifecycleMetadataReviews} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    review_id text NOT NULL,
    candidate_id text NOT NULL,
    reviewed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, review_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_lifecycle_metadata_reviews", "candidate")} ON {vectorLifecycleMetadataReviews} (workspace_id, collection_id, candidate_id, reviewed_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_lifecycle_metadata_reviews", "reviewed")} ON {vectorLifecycleMetadataReviews} (workspace_id, collection_id, reviewed_at DESC);

CREATE TABLE IF NOT EXISTS {vectorLifecycleSidecarMetadata} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    item_id text NOT NULL,
    source_review_id text NOT NULL DEFAULT '',
    source_candidate_id text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, item_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "vector_lifecycle_sidecar_metadata", "created")} ON {vectorLifecycleSidecarMetadata} (workspace_id, collection_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {artifacts} (
    workspace_id text NOT NULL DEFAULT '',
    collection_id text NOT NULL DEFAULT '',
    artifact_id text NOT NULL,
    artifact_kind text NOT NULL,
    relative_path text NOT NULL DEFAULT '',
    content_type text NOT NULL DEFAULT 'application/octet-stream',
    extension text NOT NULL DEFAULT '.json',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    size_bytes bigint NOT NULL DEFAULT 0,
    content_hash text NOT NULL DEFAULT '',
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id, artifact_id)
);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "artifacts", "kind")} ON {artifacts} (workspace_id, artifact_kind);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "artifacts", "updated")} ON {artifacts} (workspace_id, collection_id, updated_at DESC);

-- R14-PG-6：分布式 context state 版本号表（不同于 schema_versions 用于 schema migration 跟踪）
CREATE TABLE IF NOT EXISTS {contextStateVersions} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    store_kind text NOT NULL,
    version bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (workspace_id, collection_id, store_kind)
);

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

-- P1-5：关系写入 outbox 表。表结构与 context_jobs 对齐（lease/retry/state + relation payload）。
CREATE TABLE IF NOT EXISTS {relationOutbox} (
    outbox_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    relation_id text NOT NULL,
    operation_kind text NOT NULL,
    provenance text NOT NULL DEFAULT '',
    payload jsonb NULL,
    state text NOT NULL DEFAULT 'Pending',
    retry_count integer NOT NULL DEFAULT 0,
    max_retry_count integer NOT NULL DEFAULT 3,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    dispatched_at timestamptz NULL,
    applied_at timestamptz NULL,
    lease_owner text NULL,
    lease_expires_at timestamptz NULL,
    last_heartbeat_at timestamptz NULL,
    last_error_message text NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (outbox_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_outbox", "state")} ON {relationOutbox} (state, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_outbox", "lease")} ON {relationOutbox} (state, lease_expires_at);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "relation_outbox", "relation")} ON {relationOutbox} (workspace_id, collection_id, relation_id);

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

-- R26-1：Agent Runtime 持久化表（checkpoint + task state）
-- 表反规范化 session 字段（session_value / runtime_kind / workspace_id / collection_id）以便按 session 索引查询；
-- 完整 AgentCheckpoint / AgentTaskState 对象保存在 data jsonb，由 store 反序列化。
CREATE TABLE IF NOT EXISTS {agentCheckpoints} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    session_value text NOT NULL,
    runtime_kind text NOT NULL DEFAULT 'Unknown',
    checkpoint_id text NOT NULL,
    turn_id text NULL,
    snapshot_id text NULL,
    created_at timestamptz NOT NULL,
    state_json text NOT NULL DEFAULT '',
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, checkpoint_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_checkpoints", "session")} ON {agentCheckpoints} (workspace_id, session_value, created_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_checkpoints", "created")} ON {agentCheckpoints} (workspace_id, created_at DESC);

CREATE TABLE IF NOT EXISTS {agentTaskStates} (
    workspace_id text NOT NULL,
    collection_id text NULL,
    session_value text NOT NULL,
    runtime_kind text NOT NULL DEFAULT 'Unknown',
    task_id text NOT NULL,
    status text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, task_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_task_states", "session")} ON {agentTaskStates} (workspace_id, session_value, updated_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_task_states", "updated")} ON {agentTaskStates} (workspace_id, updated_at DESC);

-- R27-1：Evolution Pipeline 持久化表（run state + 3 audit tables）
-- 表反规范化 proposal_id / status / run_id 字段以便按 proposal/status/run 索引查询；
-- 完整 PipelineRunSnapshot / CanaryAssignment / RollbackRecord / BaselineComparison 对象保存在 data jsonb，由 store 反序列化。
-- P0-7：新增 revision / lease_owner / lease_expires_at / last_transition_id 列支持 HA CAS 推进。
CREATE TABLE IF NOT EXISTS {pipelineRuns} (
    run_id text NOT NULL,
    proposal_id text NOT NULL,
    proposal_major integer NOT NULL,
    proposal_minor integer NOT NULL,
    target_component text NOT NULL DEFAULT 'PackagePolicy',
    current_stage text NOT NULL DEFAULT 'OfflineExperiment',
    status text NOT NULL DEFAULT 'Running',
    started_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    rollback_reason text NULL,
    revision bigint NOT NULL DEFAULT 1,
    lease_owner text NULL,
    lease_expires_at timestamptz NULL,
    last_transition_id text NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (run_id)
);

-- P0-7：v15 → v16 升级路径 — 已有 pipeline_runs 表追加新列（幂等）
ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS lease_owner text NULL;
ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz NULL;
ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS last_transition_id text NULL;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_runs", "proposal")} ON {pipelineRuns} (proposal_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_runs", "status")} ON {pipelineRuns} (status, updated_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_runs", "updated")} ON {pipelineRuns} (updated_at DESC);

CREATE TABLE IF NOT EXISTS {pipelineCanaryAssignments} (
    assignment_id text NOT NULL,
    run_id text NOT NULL,
    proposal_id text NOT NULL,
    strategy text NOT NULL DEFAULT 'Random',
    assigned_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (assignment_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_canary_assignments", "run")} ON {pipelineCanaryAssignments} (run_id, assigned_at);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_canary_assignments", "assigned")} ON {pipelineCanaryAssignments} (assigned_at DESC);

CREATE TABLE IF NOT EXISTS {pipelineRollbackRecords} (
    record_id text NOT NULL,
    run_id text NOT NULL,
    proposal_id text NOT NULL,
    reason text NOT NULL DEFAULT 'RollbackConditionTriggered',
    triggered_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (record_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_rollback_records", "run")} ON {pipelineRollbackRecords} (run_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_rollback_records", "triggered")} ON {pipelineRollbackRecords} (triggered_at DESC);

CREATE TABLE IF NOT EXISTS {pipelineBaselineComparisons} (
    comparison_id text NOT NULL,
    proposal_id text NOT NULL,
    run_id text NULL,
    compared_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (comparison_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_baseline_comparisons", "proposal")} ON {pipelineBaselineComparisons} (proposal_id, compared_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "pipeline_baseline_comparisons", "compared")} ON {pipelineBaselineComparisons} (compared_at DESC);

-- R28-B.8：Canary Gate 渐进推进审计表
-- 独立于 pipeline_runs 的 transition audit（PipelineAuditBatch），记录每次 stage 推进的完整历史
-- 用于 CanaryProgressionService 推进/回滚决策的端到端审计溯源（含 idempotency_key 端到端幂等）
CREATE TABLE IF NOT EXISTS {stageTransitions} (
    transition_id text NOT NULL,
    run_id text NOT NULL,
    from_stage text NOT NULL,
    to_stage text NOT NULL,
    transitioned_at timestamptz NOT NULL,
    idempotency_key text,
    observation_batch_id text,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (transition_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stage_transitions", "run_id")} ON {stageTransitions} (run_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "stage_transitions", "idempotency")} ON {stageTransitions} (idempotency_key) WHERE idempotency_key IS NOT NULL;

-- WS-A：Policy Registry 持久化表（bundle 注册 + activation CAS 激活）
-- policy_bundles: (bundle_id, version) 复合主键 — bundle 全局不可变；supersede 通过新建 bundle 实现
-- policy_activations: (workspace_id, collection_id) 主键 — 每个作用域仅一条 activation 记录；epoch 用于 CAS
-- 反规范化 bundle_id / bundle_version / epoch 字段以便索引查询 + CAS UPDATE；完整对象保存在 data jsonb
CREATE TABLE IF NOT EXISTS {policyBundles} (
    bundle_id text NOT NULL,
    version text NOT NULL,
    is_superseded boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (bundle_id, version)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "policy_bundles", "bundle")} ON {policyBundles} (bundle_id, is_superseded);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "policy_bundles", "superseded")} ON {policyBundles} (is_superseded);

CREATE TABLE IF NOT EXISTS {policyActivations} (
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    bundle_id text NOT NULL,
    bundle_version text NOT NULL,
    bundle_content_hash text NOT NULL,
    epoch bigint NOT NULL DEFAULT 1,
    activated_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, collection_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "policy_activations", "bundle")} ON {policyActivations} (bundle_id, bundle_version);

-- R28-B.6 阶段 E：Experiment Recorder 持久化表（replay fixture 索引 + jsonb）
-- FileSystem 存 raw fixture JSON，PostgreSQL 存索引列 + jsonb（含 WorkingSet + V2Result）。
-- fixture_id 为主键，幂等写入（ON CONFLICT DO NOTHING）；recorded_at + purpose 为查询索引。
CREATE TABLE IF NOT EXISTS {experimentReplayFixtures} (
    fixture_id text NOT NULL,
    recorded_at timestamptz NOT NULL,
    purpose text NOT NULL,
    legacy_selected_count integer NOT NULL,
    v2_selected_count integer NOT NULL,
    common_selected_count integer NOT NULL,
    only_in_legacy_count integer NOT NULL,
    only_in_v2_count integer NOT NULL,
    jaccard_index double precision NOT NULL,
    legacy_token_total integer NOT NULL,
    v2_token_total integer NOT NULL,
    working_set_candidate_count integer NOT NULL,
    parity_level text NOT NULL,
    notes text NOT NULL DEFAULT '',
    data jsonb NOT NULL,
    PRIMARY KEY (fixture_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "experiment_replay_fixtures", "recorded")} ON {experimentReplayFixtures} (recorded_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "experiment_replay_fixtures", "purpose")} ON {experimentReplayFixtures} (purpose, recorded_at DESC);

-- R28-E：Durable Memory Governance 持久化表（Utility Ledger + ConflictSet durable projection）
-- utility_ledger_entries：per-Candidate per-Expert utility 贡献账本条目（read-only 公共 API，写入由 materializer 批量插入）
-- 反规范化 workspace_id / collection_id / candidate_item_id / decision_id / materialized_at 字段以便索引查询；
-- 完整 UtilityLedgerEntry 对象保存在 data jsonb，由 store 反序列化。
CREATE TABLE IF NOT EXISTS {utilityLedgerEntries} (
    entry_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    candidate_item_id text NOT NULL,
    expert text NOT NULL,
    utility_contribution double precision NOT NULL,
    deterministic_score double precision NOT NULL,
    model_score double precision,
    final_score double precision NOT NULL,
    is_selected boolean NOT NULL,
    drop_reason_code text,
    decision_id text NOT NULL,
    policy_version text NOT NULL,
    router_id text NOT NULL,
    materialized_at timestamptz NOT NULL,
    materialization_batch_id text NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (entry_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "utility_ledger_entries", "workspace")} ON {utilityLedgerEntries} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "utility_ledger_entries", "candidate")} ON {utilityLedgerEntries} (candidate_item_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "utility_ledger_entries", "decision")} ON {utilityLedgerEntries} (decision_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "utility_ledger_entries", "materialized")} ON {utilityLedgerEntries} (materialized_at DESC);

-- conflict_sets：冲突集合（read-only 公共 API，写入由 materializer 批量插入）
-- 反规范化 workspace_id / collection_id / kind / decision_id / resolution_status 字段以便索引查询；
-- entries 列表保存在 data jsonb 的 Entries 节点（PascalCase，对齐 PostgresJsonSerializer 默认序列化），
-- 通过 GIN 索引支持 candidate 包含查询；完整 ConflictSet 对象保存在 data jsonb。
CREATE TABLE IF NOT EXISTS {conflictSets} (
    conflict_set_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    kind text NOT NULL,
    decision_id text,
    resolved_item_id text,
    resolution_status text NOT NULL DEFAULT 'Unresolved',
    chosen_authority text,
    resolved_at timestamptz,
    resolver text,
    memory_state_event_id text,
    relation_id text,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (conflict_set_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "conflict_sets", "workspace")} ON {conflictSets} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "conflict_sets", "status")} ON {conflictSets} (resolution_status);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "conflict_sets", "candidate")} ON {conflictSets} USING gin ((data->'Entries'));

-- R29 WP-B-1：Tool Dispatch Journal 持久化表（HA 崩溃恢复 exactly-once）
-- tool_dispatch_journal_entries: request_id 主键 — 每个 tool 调用一条 journal 条目
-- state 为 smallint（ToolDispatchState byte 枚举：0=Prepared, 1=Dispatched, 2=Committed, 3=ResultDelivered）
-- 前向推进由 UPDATE ... WHERE state < :target 保证原子性
CREATE TABLE IF NOT EXISTS {toolDispatchJournalEntries} (
    request_id text NOT NULL,
    tool_name text NOT NULL DEFAULT '',
    state smallint NOT NULL DEFAULT 0,
    idempotency_key text,
    external_operation_id text,
    updated_at timestamptz NOT NULL,
    diagnostic_note text,
    PRIMARY KEY (request_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_journal_entries", "state")} ON {toolDispatchJournalEntries} (state);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_journal_entries", "idempotency")} ON {toolDispatchJournalEntries} (idempotency_key) WHERE idempotency_key IS NOT NULL;
""";
    }

    public IReadOnlyList<PostgresStoreMigration> ListMigrations()
    {
        // R14-PG-8：从版本化注册表读取，避免重复维护。
        return PostgresMigrationRegistry.ToStoreMigrationList();
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
        // P0 冻结：版本已匹配时跳过完整 DDL 批次，避免重复执行 150+ CREATE TABLE IF NOT EXISTS。
        // Docker Desktop / WSL2 上即使幂等重跑也需要 3+ 分钟，会触发 socket read timeout。
        // 首次迁移成功后 schema_versions 表会记录 SchemaVersion，后续调用直接 short-circuit 返回。
        var appliedVersion = await GetAppliedVersionAsync(cancellationToken).ConfigureAwait(false);
        if (appliedVersion == SchemaVersion)
        {
            return;
        }

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

    /// <summary>
    /// R14-PG-8：查询 context_schema_migrations 表中已应用的 migration 历史，按 applied_at 升序。
    /// 表不存在时返回空列表（数据库尚未迁移）。
    /// </summary>
    public async Task<IReadOnlyList<PostgresMigrationHistoryEntry>> GetMigrationHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;
        var migrationsTable = Infrastructure.PostgresNames.Table(_connectionFactory.Options, "context_schema_migrations");
        command.CommandText = $"""
SELECT migration_id, schema_version, applied_at, checksum
FROM {migrationsTable}
ORDER BY applied_at ASC;
""";
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var entries = new List<PostgresMigrationHistoryEntry>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new PostgresMigrationHistoryEntry
                {
                    MigrationId = reader.GetString(reader.GetOrdinal("migration_id")),
                    SchemaVersion = reader.GetString(reader.GetOrdinal("schema_version")),
                    AppliedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("applied_at")),
                    Checksum = reader.IsDBNull(reader.GetOrdinal("checksum")) ? null : reader.GetString(reader.GetOrdinal("checksum"))
                });
            }
            return entries;
        }
        catch (NpgsqlException)
        {
            // context_schema_migrations 表尚不存在时返回空列表。
            return Array.Empty<PostgresMigrationHistoryEntry>();
        }
    }

    /// <summary>
    /// R14-PG-8：回滚到指定 schema 版本。
    /// 当前 baseline migration 不支持真实回滚（cumulative idempotent DDL），调用会返回 RolledBack=false
    /// 并在 Diagnostics 中说明原因。未来按版本切分的 SupportsRollback=true 的 migration 实现后，
    /// 此方法将调用其 DownAsync 并更新 context_schema_migrations。
    /// </summary>
    public async Task<PostgresMigrationRollbackResult> RollbackAsync(
        string targetSchemaVersion,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSchemaVersion);

        if (!confirm)
        {
            return new PostgresMigrationRollbackResult
            {
                RolledBack = false,
                ConfirmRequired = true,
                TargetSchemaVersion = targetSchemaVersion,
                Diagnostics = new[] { "ConfirmRequired" }
            };
        }

        var previousVersion = await GetAppliedVersionAsync(cancellationToken).ConfigureAwait(false);

        // target == current：no-op
        if (previousVersion is not null && string.Equals(previousVersion, targetSchemaVersion, StringComparison.Ordinal))
        {
            return new PostgresMigrationRollbackResult
            {
                RolledBack = true,
                ConfirmRequired = false,
                PreviousSchemaVersion = previousVersion,
                TargetSchemaVersion = targetSchemaVersion,
                ActualSchemaVersion = previousVersion,
                RolledBackMigrations = Array.Empty<string>(),
                Diagnostics = new[] { "TargetEqualsCurrent" }
            };
        }

        // 找出 target 与 current 之间需要回滚的 migration（SchemaVersion > target 的所有 migration，按版本降序）。
        var migrationsToRollback = PostgresMigrationRegistry.Migrations
            .Where(m => string.Compare(m.SchemaVersion, targetSchemaVersion, StringComparison.Ordinal) > 0)
            .OrderByDescending(m => m.SchemaVersion)
            .ToList();

        if (migrationsToRollback.Count == 0)
        {
            return new PostgresMigrationRollbackResult
            {
                RolledBack = false,
                ConfirmRequired = false,
                PreviousSchemaVersion = previousVersion,
                TargetSchemaVersion = targetSchemaVersion,
                Diagnostics = new[] { "NoMigrationsToRollback", "TargetVersionNewerOrEqualCurrent" }
            };
        }

        // 检查所有相关 migration 是否支持 rollback。
        var notSupported = migrationsToRollback.Where(m => !m.SupportsRollback).ToList();
        if (notSupported.Count > 0)
        {
            var diagnostics = new List<string> { "RollbackNotSupported" };
            foreach (var m in notSupported)
            {
                diagnostics.Add($"{m.MigrationId}: {m.RollbackNotSupportedReason}");
            }
            return new PostgresMigrationRollbackResult
            {
                RolledBack = false,
                ConfirmRequired = false,
                PreviousSchemaVersion = previousVersion,
                TargetSchemaVersion = targetSchemaVersion,
                Diagnostics = diagnostics
            };
        }

        // 当前所有 migration 都不支持真实 rollback，这里实际上不会执行到。
        // 未来 SupportsRollback=true 的 migration 实现后，这里调用其 DownAsync 并更新 context_schema_migrations。
        // 当前实现以"安全拒绝"为主，避免破坏数据。
        return new PostgresMigrationRollbackResult
        {
            RolledBack = false,
            ConfirmRequired = false,
            PreviousSchemaVersion = previousVersion,
            TargetSchemaVersion = targetSchemaVersion,
            Diagnostics = new[] { "RollbackExecutionNotImplementedForCurrentMigrations" }
        };
    }
}
