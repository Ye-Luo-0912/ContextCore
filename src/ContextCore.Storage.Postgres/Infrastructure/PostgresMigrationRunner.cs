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
    /// R29 WP-B-2：v21 → v22，新增 kernel_result_outbox 表与索引（持久化 Kernel Result Outbox，支持崩溃恢复结果重放）。
    /// R29 WP-B-4：v22 → v23，新增 kernel_transport_inbox + kernel_transport_outbox 表与索引（Durable Transport，PostgreSQL-backed Channel 支持 HA 跨进程指令/结果传输）。
    /// R29 WP-A-1：v23 → v24，新增 model_artifacts 表与索引（Model Artifact Registry 持久化，集中管理 ModelArtifactDescriptor 的注册与查询）。
    /// R29 WP-E-3：v24 → v25，新增 vw_utility_ledger_training_data 视图（训练数据导出 SQL 接口，供 ad-hoc 查询与 BI 工具直接消费）。
    /// R29 WP-E-4：v25 → v26，新增 vw_utility_ledger_calibration_data 视图（校准数据导出 SQL 接口，predicted / observed / weight 三段式）。
    /// R29 WP-E-5：v26 → v27，新增 user_feedback_entries 表与索引（用户显式反馈接入 ledger：thumbs up/down + 评分修正 + 文本反馈）。
    /// P0-3：v27 → v28，tool_dispatch_journal_entries.idempotency_key 索引由普通 index 升级为 UNIQUE partial index，
    ///       防止不同 request_id 使用相同幂等键分别执行（外部副作用 exactly-once 的数据库层兜底）。
    /// P0-1：v28 → v29，kernel_transport_inbox / kernel_transport_outbox 追加 state / lease_owner / lease_expires_at 列
    ///       与配套索引，将破坏性 DELETE 出队改为租约模型（Pending → Leased → Acked），
    ///       支持崩溃恢复后由新实例重新租约或 RequeueExpired 回滚过期租约。
    /// P0-2：v29 → v30，kernel_result_outbox 追加 lease_owner / lease_expires_at / lease_token 列与配套索引，
    ///       将 DequeueAsync 的 Dispatched 终态改为租约模型（Pending → Leased → Acked），
    ///       避免 consumer 崩溃后 Dispatched 行永久滞留；旧 DequeueAsync 内部改用 LeaseAsync（默认租约）。
    /// 任务 F：v30 → v31，新增 agent_runs + agent_run_events 表与索引（Agent Run 状态机 + 事件流哈希链持久化）。
    /// 任务 D：v31 → v32，新增 canary_metrics_samples + canary_leader_leases 表与索引
    ///       （Canary HA 聚合：跨实例指标样本表 + Leader 租约表，支持多节点部署时全局聚合 + 单 leader 推进）。
    /// P0-3 CAS-2：v32 → v33，tool_dispatch_journal_entries 追加 payload_digest / workspace_id / run_id 列，
    ///       支持 PrepareAsync 语义等价校验，防止同一 RequestId 被复用为另一项操作时静默沿用旧 journal 记录。
    /// Learning Loop Durable Outbox：v33 → v34，新增 learning_event_outbox 表与索引。
    ///       将 Utility Ledger 物化从 fire-and-forget Task.Run 改为 Durable Outbox 模式：
    ///       Decision committed → learning_event_outbox (持久化) → bounded batch worker → MaterializeAsync → Ack/Retry/DeadLetter。
    ///       消除进程崩溃时静默丢训练数据、DB 瞬时失败不重试、无死信/可观测性等问题。
    /// Canary HA 聚合修复：v35 → v36，canary_metrics_samples 表改为最新快照模型：
    ///       - 新增 stage_epoch 列，PK 由 (sample_id) 改为 (run_id, stage_epoch, instance_id)
    ///       - 新增 canary_run_epochs 表（per-run 单调递增 epoch，Leader 推进时递增）
    ///       - 每次 UPSERT 覆盖该实例最新累计值（不再追加行），聚合时只汇总当前 epoch 的最新快照
    ///       - 修复 SUM(total_observations) 重复累计、InstanceCount=COUNT(*) 误算样本行数、
    ///         旧 Canary 阶段数据污染新阶段、表无限增长等问题。
    /// Durable Delivery 剩余修复：v36 → v37，为 kernel_transport_inbox / kernel_transport_outbox /
    ///   kernel_result_outbox 追加重试与死信列（attempt_count / max_attempts / next_attempt_at /
    ///   last_error / dead_letter_reason），新增 kernel_transport_dead_letter 表，并为
    ///   kernel_transport_outbox.instruction_id 添加 UNIQUE 约束（让 SendResultAsync 幂等，
    ///   result_id 改为基于 instruction_id 的稳定值，避免 Replayer 重发产生重复投递）。
    /// P0-6：v37 → v38，新增 model_activation_audit 表与索引（Model Control Plane 激活审计持久化，
    ///   记录 Activate/Rollback/Retire/Shadow/Warmup 等模型生命周期事件，含 previous_model_id /
    ///   operator / reason / node_id 等业务字段，支持 HA 多节点对账与 Champion/Challenger 推进追溯）。
    /// 运行时能力补齐：v38 → v39，新增 agent_run_approvals + agent_run_leases 表与索引：
    ///   - agent_run_approvals：durable approval 持久化（Pending/Approved/Rejected 状态机 + CAS 裁决），
    ///     让进程崩溃恢复后可重新加载未决审批，外部审批系统通过 approval_id 提交决策。
    ///   - agent_run_leases：HA Run Owner Lease（PostgreSQL-backed CAS 租约），
    ///     确保同一时刻仅一个 Host 实例处理同一 Run，复用 canary_leader_leases 模式。
    /// P0-8：v39 → v40，learning_event_outbox 追加 lease_token 列。AcquirePendingAsync 生成唯一 token
    ///   并写入数据库；MarkAckedAsync / MarkFailedAsync / RenewLeaseAsync 通过 lease_token CAS 校验
    ///   仅持有者可 Ack/Nack/Renew——修复旧 Worker lease 过期被抢占后越权 Ack 新 Worker lease 的问题。
    /// P0-4：v40 → v41，agent_run_leases 追加 fencing_token bigint 列（单调递增），用于 Agent Run
    ///   副作用操作（状态转换 / 事件追加 / Tool dispatch）的 lease fencing 校验，修复 HA Lease 无法
    ///   防止双执行的问题（旧 lease 被抢占后，过期持有者的 UPDATE 因 fencing_token 不匹配而影响 0 行）。
    /// P3：v41 → v42，context_items 追加 search_vector tsvector 生成列 + GIN 索引，
    ///   Lexical 检索由 (data->>'Content') ILIKE '%query%' 全表扫描改为
    ///   websearch_to_tsquery + ts_rank_cd 走 GIN 索引。
    /// P6：v41 → v42，context_items 追加 content_hash / content_token_cost 列，
    ///   摄取阶段持久化 SHA-256 与精确 token cost，Provider 直接读取不再在线重复计算。
    /// P10/P11/P12：v42 → v43，Canary 与性能真相三项修复：
    ///   - P10：canary_metrics_samples 追加 v2_latency_sketch / legacy_latency_sketch bytea 列，
    ///     各实例持久化 DDSketch 字节，Leader 聚合时 MergeFrom 合并后查询总体 P95，
    ///     替代对单实例 P95 加权平均（加权平均会低估尾延迟）。
    ///   - P11：canary_metrics_samples 追加 task_success_sum / task_success_count /
    ///     tool_success_sum / tool_success_count 列，聚合时 SUM(分子)/SUM(分母) 替代 AVG(rate)，
    ///     避免 10 样本实例与 10000 样本实例权重相同。
    ///   - P12：canary_leader_leases 追加 fencing_token bigint 列（单调递增），
    ///     每次 TryAcquireAsync 成功获取（含抢占过期）时递增；AdvanceEpochAsync 的 UPDATE
    ///     校验 WHERE fencing_token <= @fencingToken，旧 Leader（fencing token 较小）推进失败。
    /// WP-2：v43 → v44，agent_runs 追加 idempotency_key text 列与 partial UNIQUE 索引
    ///   (workspace_id, idempotency_key) WHERE idempotency_key IS NOT NULL，
    ///   让 POST /api/agents/runs 在提供 IdempotencyKey 时返回已有 Run（200 OK）而非创建重复 Run，
    ///   防止客户端重试/网络抖动导致同一业务意图被多次执行。
    /// Perf-7：v44 → v45，新增 canary_pipelines + canary_transition_audit 表与索引：
    ///   - canary_pipelines：per-run pipeline 状态表（percentage + status + revision），
    ///     ApplyCanaryDecisionAsync 在单一事务内通过 revision CAS 原子更新，替代旧路径
    ///     AdvanceAsync（修改 in-memory）+ AdvanceEpochAsync（fencing 校验）的两步模式。
    ///   - canary_transition_audit：append-only 审计表，记录每次决策的 from/to/decision/
    ///     fencing_token/new_epoch，与 canary_pipelines UPDATE 同事务写入，确保审计与状态强一致。
    ///   修复 HA 正确性：旧 Leader 在 lease 失效后无法再修改 rollout（fencing 校验在事务内首先执行）；
    ///   Rollback 路径也经过 fencing 校验（旧路径完全无校验）。
    /// Perf-2：v45 → v46，memory_items / constraints 追加 content_hash / content_length /
    ///   tokenizer_id / tokenizer_version / token_count / counted_at 列，Memory/Constraint
    ///   摄取阶段持久化完整 tokenization metadata，Provider 读取后跳过在线 SHA-256 + tokenize。
    /// Perf-5：v45 → v46，context_items 启用 pg_trgm 扩展；search_vector 改为 CJK 预分词
    ///   （regexp_replace 在每个 CJK 字符后插入空格，simple 配置即可按字符切分）；
    ///   新增 id / title 表达式的 gin_trgm_ops 索引，支持 ILIKE 中文/前缀检索。
    /// P0-3：v46 → v47，新增 tool_dispatch_results 表与索引（Durable Tool Result 缓存持久化，
    ///   让 HA 崩溃恢复时已 Committed/ResultDelivered 的 tool 结果可跨进程读取，防止外部副作用结果丢失）。
    /// P0-4：v48 → v49，修复 Durable Tool Result 主键结构：tool_dispatch_results 主键由 tool_call_id
    ///   （模型生成，不保证跨 Run/Provider 唯一、JSON fallback 可能重复）改为 request_id（稳定调用身份哈希），
    ///   追加 workspace_id / run_id / invocation_id 列与 UNIQUE(workspace_id, run_id, invocation_id) partial 约束，
    ///   防止另一 Run 覆盖已有 Tool Result；tool_call_id / idempotency_key 改为 partial index。
    /// P1-5：v50 → v51，utility_ledger_entries 追加 (decision_id, candidate_item_id, expert) UNIQUE 索引，
    ///   防御性兜底 Learning Lease 过期后旧/新 Worker 重复物化产生重复 ledger 条目（entry_id 幂等之外的数据库层约束）。
    /// </summary>
    public const string SchemaVersion = "cc-schema-v51";

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
        "tool_dispatch_journal_entries",
        // P0-3：Durable Tool Result 缓存持久化（已 Committed/ResultDelivered 的 tool 结果跨进程读取）
        "tool_dispatch_results",
        // R29 WP-B-2：Kernel Result Outbox 持久化（崩溃恢复结果重放）
        "kernel_result_outbox",
        // R29 WP-B-4：Durable Transport 持久化（PostgreSQL-backed Channel，跨进程指令/结果传输）
        "kernel_transport_inbox",
        "kernel_transport_outbox",
        // R29 WP-A-1：Model Artifact Registry 持久化（集中管理 ModelArtifactDescriptor 注册与查询）
        "model_artifacts",
        // R29 WP-E-5：User Feedback Ledger 持久化（用户显式反馈接入：thumbs up/down + 评分修正 + 文本反馈）
        "user_feedback_entries",
        // 任务 F：Agent Run 状态机 + 事件流哈希链持久化
        "agent_runs",
        "agent_run_events",
        // 任务 D：Canary HA 聚合（跨实例指标样本 + Leader 租约 + stage epoch 跟踪）
        "canary_metrics_samples",
        "canary_leader_leases",
        "canary_run_epochs",
        // Perf-7：Canary 严格 HA 单事务接口（pipeline revision CAS + transition audit）
        "canary_pipelines",
        "canary_transition_audit",
        // Learning Loop Durable Outbox：Decision 物化事件持久化（替代 fire-and-forget Task.Run）
        "learning_event_outbox",
        // Durable Delivery v37：Durable Transport Dead Letter Queue（超过 max_attempts 的指令/结果）
        "kernel_transport_dead_letter",
        // P0-6：Model Control Plane 激活审计持久化（Activate/Rollback/Retire/Shadow 等生命周期事件审计记录）
        "model_activation_audit",
        // 运行时能力补齐：durable approval + HA Run Owner Lease 持久化
        "agent_run_approvals",
        "agent_run_leases",
        // R29 WP-A-2：Desired Model State Store 持久化（HA 多节点模型期望状态同步）
        "desired_model_states",
        // P0-9: Cluster Model Slot (single champion source of truth, single-row table + CAS on revision)
        "cluster_model_slots"
    ];

    public static readonly IReadOnlyList<(string TableSuffix, string IndexSuffix)> RequiredOperationalIndexDefinitions =
    [
        ("context_operation_events", "created"),
        ("context_items", "type"),
        ("context_items", "tags"),
        ("context_items", "updated"),
        ("context_items", "search_vector"),
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
        ("utility_ledger_entries", "decision_candidate_kind"),
        ("conflict_sets", "workspace"),
        ("conflict_sets", "status"),
        ("conflict_sets", "candidate"),
        // R29 WP-B-1：Tool Dispatch Journal 索引（按 state 查待恢复条目 + 按 idempotency_key 去重）
        ("tool_dispatch_journal_entries", "state"),
        ("tool_dispatch_journal_entries", "idempotency"),
        // P0-4：Durable Tool Result 索引（request_id 为 PK 自动索引；以下为辅助索引）
        ("tool_dispatch_results", "ws_run_invocation"),
        ("tool_dispatch_results", "tool_call_id"),
        ("tool_dispatch_results", "idempotency_key"),
        // R29 WP-B-2 / P0-2：Kernel Result Outbox 索引（按 state + created_at 查 Pending + 按 instruction_id 查历史 + 租约模型 pending/expired）
        ("kernel_result_outbox", "state"),
        ("kernel_result_outbox", "instruction"),
        ("kernel_result_outbox", "pending"),
        ("kernel_result_outbox", "expired"),
        // R29 WP-B-4：Durable Transport 索引（inbox/outbox 按 created_at FIFO 取最旧 + 按 instruction_id 查历史）
        ("kernel_transport_inbox", "created"),
        ("kernel_transport_inbox", "instruction"),
        ("kernel_transport_inbox", "pending"),
        ("kernel_transport_inbox", "expired"),
        ("kernel_transport_outbox", "created"),
        ("kernel_transport_outbox", "instruction"),
        ("kernel_transport_outbox", "pending"),
        ("kernel_transport_outbox", "expired"),
        // R29 WP-A-1：Model Artifact Registry 索引（按 model_name + registered_at 查最新版本 / 列举所有版本）
        ("model_artifacts", "model_name"),
        ("model_artifacts", "registered"),
        // R29 WP-E-5：User Feedback Ledger 索引（按作用域/决策/候选/反馈者/时间查 + 按 idempotency_key 去重）
        ("user_feedback_entries", "workspace"),
        ("user_feedback_entries", "decision"),
        ("user_feedback_entries", "candidate"),
        ("user_feedback_entries", "given_by"),
        ("user_feedback_entries", "given_at"),
        ("user_feedback_entries", "idempotency"),
        // 任务 F：Agent Run 索引（按 session 列举 + 按 state 拉取待处理；events 主键已覆盖按 run 查询）
        ("agent_runs", "session"),
        ("agent_runs", "state"),
        // WP-2：idempotency_key partial UNIQUE 索引（按 workspace + idempotency_key 点查 + 去重）
        ("agent_runs", "idempotency"),
        // 任务 D：Canary HA 聚合索引
        // canary_metrics_samples：按 run + recorded_at 查最新样本（聚合器 SELECT）+ 按 run + instance 去重
        ("canary_metrics_samples", "run_recorded"),
        ("canary_metrics_samples", "run_instance"),
        // canary_leader_leases：按 lease_expires_at 扫描过期租约（ReapExpiredAsync）
        ("canary_leader_leases", "expires"),
        // Perf-7：canary_transition_audit 按 run + transitioned_at 查历史
        ("canary_transition_audit", "run"),
        ("canary_transition_audit", "transition"),
        // Learning Loop Durable Outbox 索引（按 state + created_at 拉 Pending + 按租约过期重试 + 按 workspace 查）
        ("learning_event_outbox", "state"),
        ("learning_event_outbox", "lease"),
        ("learning_event_outbox", "workspace"),
        // Durable Delivery v37：重试与死信索引
        // inbox/outbox 按 next_attempt_at 取可重试的 Pending 行（LeaseAsync WHERE next_attempt_at <= now）
        ("kernel_transport_inbox", "retry"),
        ("kernel_transport_outbox", "retry"),
        ("kernel_result_outbox", "retry"),
        // DLQ 按 source + created_at 列举（GetDeadLetterEntriesAsync）
        ("kernel_transport_dead_letter", "source"),
        ("kernel_transport_dead_letter", "created"),
        // P0-6：model_activation_audit 索引（按 model_artifact_id 查历史 + 按 operation 过滤 + 按时间倒序列举）
        ("model_activation_audit", "model"),
        ("model_activation_audit", "operation"),
        ("model_activation_audit", "timestamp"),
        // 运行时能力补齐：durable approval + HA Run Owner Lease 索引
        // agent_run_approvals：按 run + pending 状态列举未决审批 + 按 run 列举全部审批历史
        ("agent_run_approvals", "run_pending"),
        ("agent_run_approvals", "run"),
        // agent_run_leases：按 lease_expires_at 扫描过期租约（ReapExpiredAsync）
        ("agent_run_leases", "expires"),
        // R29 WP-A-2：Desired Model State Store 索引（按 updated_at 倒序列举全部状态）
        ("desired_model_states", "updated")
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
        // P0-3：Durable Tool Result 缓存持久化表
        var toolDispatchResults = Infrastructure.PostgresNames.Table(options, "tool_dispatch_results");
        // P0-4：tool_dispatch_results 主键约束名（Postgres 默认 {table}_pkey），用于 DROP CONSTRAINT
        var toolDispatchResultsPkey = $"{options.TablePrefix}tool_dispatch_results_pkey";
        // R29 WP-B-2：Kernel Result Outbox 持久化表
        var kernelResultOutbox = Infrastructure.PostgresNames.Table(options, "kernel_result_outbox");
        // R29 WP-B-4：Durable Transport 持久化表（inbox/outbox）
        var kernelTransportInbox = Infrastructure.PostgresNames.Table(options, "kernel_transport_inbox");
        var kernelTransportOutbox = Infrastructure.PostgresNames.Table(options, "kernel_transport_outbox");
        // R29 WP-A-1：Model Artifact Registry 持久化表
        var modelArtifacts = Infrastructure.PostgresNames.Table(options, "model_artifacts");
        // R29 WP-E-5：User Feedback Ledger 持久化表
        var userFeedbackEntries = Infrastructure.PostgresNames.Table(options, "user_feedback_entries");
        // 任务 F：Agent Run 状态机 + 事件流哈希链持久化表
        var agentRuns = Infrastructure.PostgresNames.Table(options, "agent_runs");
        var agentRunEvents = Infrastructure.PostgresNames.Table(options, "agent_run_events");
        // 任务 D：Canary HA 聚合表（跨实例指标样本 + Leader 租约 + stage epoch 跟踪）
        var canaryMetricsSamples = Infrastructure.PostgresNames.Table(options, "canary_metrics_samples");
        var canaryLeaderLeases = Infrastructure.PostgresNames.Table(options, "canary_leader_leases");
        var canaryRunEpochs = Infrastructure.PostgresNames.Table(options, "canary_run_epochs");
        // Perf-7：Canary 严格 HA 单事务接口（pipeline revision CAS + transition audit）
        var canaryPipelines = Infrastructure.PostgresNames.Table(options, "canary_pipelines");
        var canaryTransitionAudit = Infrastructure.PostgresNames.Table(options, "canary_transition_audit");
        var learningEventOutbox = Infrastructure.PostgresNames.Table(options, "learning_event_outbox");
        // Durable Delivery v37：Durable Transport Dead Letter Queue 表
        var kernelTransportDeadLetter = Infrastructure.PostgresNames.Table(options, "kernel_transport_dead_letter");
        // P0-6：Model Control Plane 激活审计持久化表
        var modelActivationAudit = Infrastructure.PostgresNames.Table(options, "model_activation_audit");
        // 运行时能力补齐：durable approval + HA Run Owner Lease 持久化表
        var agentRunApprovals = Infrastructure.PostgresNames.Table(options, "agent_run_approvals");
        var agentRunLeases = Infrastructure.PostgresNames.Table(options, "agent_run_leases");
        var desiredModelStates = Infrastructure.PostgresNames.Table(options, "desired_model_states");
        // P0-9: Cluster Model Slot (single champion source of truth)
        var clusterModelSlots = Infrastructure.PostgresNames.Table(options, "cluster_model_slots");
        var extensionSql = options.EnablePgVectorExtension
            ? "CREATE EXTENSION IF NOT EXISTS vector;"
            : string.Empty;
        var schemaSql = string.IsNullOrWhiteSpace(options.SchemaName)
            ? string.Empty
            : $"CREATE SCHEMA IF NOT EXISTS {options.SchemaName};";

        return $"""
{schemaSql}
{extensionSql}

-- Perf-5：pg_trgm 扩展用于 id / title 的 ILIKE 中文/前缀检索（gin_trgm_ops GIN 索引）。
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Perf-5：CJK 预分词 IMMUTABLE 函数。在每个 CJK 字符后插入空格，
-- 让 to_tsvector('simple', ...) 能按字符切分，避免中文整段被当作单一 token。
-- 覆盖 CJK Unified Ideographs 主块 (U+4E00..U+9FFF) 与扩展 A 区 (U+3400..U+4DBF)，
-- 这两块覆盖了绝大多数现代中文用字。函数标记 IMMUTABLE 以便在 GENERATED 列中使用。
CREATE OR REPLACE FUNCTION cjk_pre_tokenize(input text) RETURNS text AS $$
BEGIN
    IF input IS NULL THEN
        RETURN '';
    END IF;
    RETURN regexp_replace(regexp_replace(input, '([一-鿿㐀-䶿])', '\1 ', 'g'), '\s+', ' ', 'g');
END;
$$ LANGUAGE plpgsql IMMUTABLE;

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

-- P3：Lexical 检索 tsvector 生成列 + GIN 索引。
-- Title 加权 A（高），Content 加权 B；simple 配置避免词典依赖，对中英文/代码/JSON 均可分词。
-- STORED 生成列随 data jsonb 一起写入，无需触发器；GIN 索引让 websearch_to_tsquery 走索引而非全表扫描。
-- Perf-5：CJK 预分词——cjk_pre_tokenize 在每个 CJK 字符后插入空格，
-- simple 配置即可按字符切分，避免中文整段被当作单一 token。
-- 仅修改 search_vector 表达式时需先 DROP 旧生成列再 ADD 新列（PostgreSQL 不支持 ALTER COLUMN 表达式）。
ALTER TABLE {contextItems} DROP COLUMN IF EXISTS search_vector;
ALTER TABLE {contextItems} ADD COLUMN IF NOT EXISTS search_vector tsvector
    GENERATED ALWAYS AS (
        setweight(to_tsvector('simple', cjk_pre_tokenize(coalesce(data->>'Title', ''))), 'A') ||
        setweight(to_tsvector('simple', cjk_pre_tokenize(coalesce(data->>'Content', ''))), 'B')
    ) STORED;
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_items", "search_vector")} ON {contextItems} USING gin (search_vector);

-- Perf-5：id / title 的 trigram GIN 索引，支持 ILIKE 中文/前缀检索（gin_trgm_ops）。
-- id 列直接索引；title 是 data jsonb 字段，用表达式索引 (data->>'Title') gin_trgm_ops。
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_items", "id_trgm")} ON {contextItems} USING gin (id gin_trgm_ops);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "context_items", "title_trgm")} ON {contextItems} USING gin ((data->>'Title') gin_trgm_ops);

-- P6：摄取阶段持久化 content_hash / content_token_cost，Provider 直接读取不再在线重复计算。
-- content_hash 为 SHA-256 小写 hex（与 ContextItem.Checksum 一致），content_token_cost 为精确 token 数。
-- 两列均可 NULL（兼容历史数据与无 tokenizer 的部署），Provider 读取时 NULL 表示未持久化、回退到在线计算。
ALTER TABLE {contextItems} ADD COLUMN IF NOT EXISTS content_hash text NULL;
ALTER TABLE {contextItems} ADD COLUMN IF NOT EXISTS content_token_cost integer NULL;

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

-- Perf-2：memory_items 持久化 tokenization metadata。Memory 摄取阶段计算 content_hash /
-- content_length / tokenizer_id / tokenizer_version / token_count / counted_at 并写入专用列，
-- Provider 读取后跳过在线 SHA-256 + tokenizer 调用。所有列均可 NULL（兼容历史数据与无 tokenizer 部署）。
ALTER TABLE {memoryItems} ADD COLUMN IF NOT EXISTS content_hash text NULL;
ALTER TABLE {memoryItems} ADD COLUMN IF NOT EXISTS content_length integer NULL;
ALTER TABLE {memoryItems} ADD COLUMN IF NOT EXISTS tokenizer_id text NULL;
ALTER TABLE {memoryItems} ADD COLUMN IF NOT EXISTS tokenizer_version text NULL;
ALTER TABLE {memoryItems} ADD COLUMN IF NOT EXISTS token_count integer NULL;
ALTER TABLE {memoryItems} ADD COLUMN IF NOT EXISTS counted_at timestamptz NULL;

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

-- Perf-2：constraints 持久化 tokenization metadata。Constraint 摄取阶段计算 content_hash /
-- content_length / tokenizer_id / tokenizer_version / token_count / counted_at 并写入专用列，
-- Provider 读取后跳过在线 SHA-256 + tokenizer 调用。所有列均可 NULL（兼容历史数据与无 tokenizer 部署）。
ALTER TABLE {constraints} ADD COLUMN IF NOT EXISTS content_hash text NULL;
ALTER TABLE {constraints} ADD COLUMN IF NOT EXISTS content_length integer NULL;
ALTER TABLE {constraints} ADD COLUMN IF NOT EXISTS tokenizer_id text NULL;
ALTER TABLE {constraints} ADD COLUMN IF NOT EXISTS tokenizer_version text NULL;
ALTER TABLE {constraints} ADD COLUMN IF NOT EXISTS token_count integer NULL;
ALTER TABLE {constraints} ADD COLUMN IF NOT EXISTS counted_at timestamptz NULL;

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
-- P1-5：防御性唯一约束——同一 (decision_id, candidate_item_id, expert) 仅允许一条 ledger 条目，
-- 防止 Learning Lease 过期后旧 Worker 与新 Worker 重复物化产生重复条目（entry_id 幂等之外的数据库层兜底）。
CREATE UNIQUE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "utility_ledger_entries", "decision_candidate_kind")} ON {utilityLedgerEntries} (decision_id, candidate_item_id, expert);

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
-- P0-3 CAS-1：前向推进由 UPDATE ... WHERE state = :expected_state 保证原子性（精确前驱匹配，禁止跨级跳跃）
CREATE TABLE IF NOT EXISTS {toolDispatchJournalEntries} (
    request_id text NOT NULL,
    tool_name text NOT NULL DEFAULT '',
    state smallint NOT NULL DEFAULT 0,
    idempotency_key text,
    external_operation_id text,
    updated_at timestamptz NOT NULL,
    diagnostic_note text,
    payload_digest text,
    workspace_id text,
    run_id text,
    PRIMARY KEY (request_id)
);

-- P0-3 CAS-2：为已有数据库（v32 及更早）补加 payload_digest / workspace_id / run_id 列。
-- 新数据库由上方 CREATE TABLE 直接创建；ALTER ... ADD COLUMN IF NOT EXISTS 保证幂等。
ALTER TABLE {toolDispatchJournalEntries} ADD COLUMN IF NOT EXISTS payload_digest text;
ALTER TABLE {toolDispatchJournalEntries} ADD COLUMN IF NOT EXISTS workspace_id text;
ALTER TABLE {toolDispatchJournalEntries} ADD COLUMN IF NOT EXISTS run_id text;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_journal_entries", "state")} ON {toolDispatchJournalEntries} (state);
-- P0-3：idempotency_key 升级为 UNIQUE partial index。
--   旧版本（v21）创建的是普通 index；此处先 DROP 旧 index（若存在）再创建 UNIQUE index，
--   保证已有数据库升级后幂等键全局唯一，防止不同 request_id 复用同一幂等键分别执行。
--   partial WHERE idempotency_key IS NOT NULL：NULL 幂等键不参与唯一约束（与 "未声明幂等键" 语义一致）。
DROP INDEX IF EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_journal_entries", "idempotency")};
CREATE UNIQUE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_journal_entries", "idempotency")} ON {toolDispatchJournalEntries} (idempotency_key) WHERE idempotency_key IS NOT NULL;

-- P0-3：Durable Tool Result 缓存持久化表（HA 崩溃恢复 exactly-once 结果读取）
-- tool_dispatch_results: tool_call_id 主键 — 每个 tool 调用的结果缓存一条行
-- result jsonb 存储完整 DurableToolResult（供 GetAsync 反序列化）；
-- succeeded / side_effect / request_id 为反规范化列，供 SQL 查询/对账。
CREATE TABLE IF NOT EXISTS {toolDispatchResults} (
    tool_call_id text NOT NULL,
    request_id text NOT NULL,
    idempotency_key text,
    side_effect text NOT NULL DEFAULT 'None',
    external_operation_id text,
    result jsonb,
    succeeded boolean NOT NULL,
    error text,
    duration_ms bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tool_call_id)
);

-- P0-4：修复 Durable Tool Result 主键结构
-- 原主键为 tool_call_id（模型生成，不保证跨 Run/Provider 唯一、JSON fallback 可能重复），
-- 改为 request_id（稳定调用身份哈希），并添加 workspace_id/run_id/invocation_id 列与 UNIQUE 约束。
-- 1. 添加新列（幂等）
ALTER TABLE {toolDispatchResults} ADD COLUMN IF NOT EXISTS workspace_id text;
ALTER TABLE {toolDispatchResults} ADD COLUMN IF NOT EXISTS run_id text;
ALTER TABLE {toolDispatchResults} ADD COLUMN IF NOT EXISTS invocation_id text;

-- 2. 反向填充：request_id 为 SHA256 hash 无法反解 workspace_id/run_id，对已有数据设为空字符串
--    （新数据由应用层 DefaultDurableToolExecutor 填充）；invocation_id 设为空字符串使其不参与 UNIQUE 约束
UPDATE {toolDispatchResults} SET workspace_id = COALESCE(workspace_id, '') WHERE workspace_id IS NULL;
UPDATE {toolDispatchResults} SET run_id = COALESCE(run_id, '') WHERE run_id IS NULL;
UPDATE {toolDispatchResults} SET invocation_id = COALESCE(invocation_id, '') WHERE invocation_id IS NULL;

-- 3. 删除旧主键约束与旧 request 索引（request_id 将成为新主键，自动建索引，旧索引冗余）
ALTER TABLE {toolDispatchResults} DROP CONSTRAINT IF EXISTS {toolDispatchResultsPkey};
DROP INDEX IF EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_results", "request")};

-- 4. 设置新主键：request_id（稳定调用身份，确保跨 Run/Provider 不覆盖）
ALTER TABLE {toolDispatchResults} ADD PRIMARY KEY (request_id);

-- 5. UNIQUE 约束：(workspace_id, run_id, invocation_id) — Workspace 隔离键，防止另一 Run 覆盖已有 Tool Result
--    partial index：仅 invocation_id != '' 时参与唯一约束（兼容旧数据空字符串 + 未提供 invocation 的调用）
CREATE UNIQUE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_results", "ws_run_invocation")} ON {toolDispatchResults} (workspace_id, run_id, invocation_id) WHERE invocation_id != '';

-- 6. 辅助索引：tool_call_id（兼容旧 GetAsync 查询路径）+ idempotency_key（外部系统对账）
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_results", "tool_call_id")} ON {toolDispatchResults} (tool_call_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "tool_dispatch_results", "idempotency_key")} ON {toolDispatchResults} (idempotency_key) WHERE idempotency_key IS NOT NULL;

-- R29 WP-B-2：Kernel Result Outbox 持久化表（崩溃恢复结果重放）
-- kernel_result_outbox: outbox_id 主键 — 每个未投递的 AgentKernelResult 一条行
-- P0-2：state 为 text（'Pending' / 'Leased' / 'Dispatched'）；instruction_id 反规范化以便查询
--   - 'Pending'：待租约（LeaseAsync 取最旧行）
--   - 'Leased'：已租约未确认（持有 lease_token）；过期由 RequeueExpiredAsync 回滚为 Pending
--   - 'Dispatched'：遗留终态（旧 DequeueAsync 直接标记）；P0-2 后 DequeueAsync 改用 LeaseAsync，不再产生新 Dispatched 行
-- 完整 AgentKernelResult 保存在 data jsonb；LeaseAsync 使用 FOR UPDATE SKIP LOCKED 支持 worker 并发
CREATE TABLE IF NOT EXISTS {kernelResultOutbox} (
    outbox_id text NOT NULL,
    instruction_id text NOT NULL DEFAULT '',
    state text NOT NULL DEFAULT 'Pending',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (outbox_id)
);

-- P0-2：租约模型列追加（幂等）— 与 kernel_transport_inbox/outbox 对齐
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS lease_owner text;
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz;
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS lease_token text;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_result_outbox", "state")} ON {kernelResultOutbox} (state, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_result_outbox", "instruction")} ON {kernelResultOutbox} (instruction_id);
-- P0-2：按 created_at 查 Pending 行（LeaseAsync 取最旧 Pending，FOR UPDATE SKIP LOCKED）
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_result_outbox", "pending")} ON {kernelResultOutbox} (created_at ASC) WHERE state = 'Pending';
-- P0-2：按 lease_expires_at 查过期 Leased 行（RequeueExpiredAsync 扫描）
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_result_outbox", "expired")} ON {kernelResultOutbox} (lease_expires_at ASC) WHERE state = 'Leased';

-- R29 WP-B-4：Durable Transport 持久化表（PostgreSQL-backed Channel）
-- P0-1：租约模型 — 破坏性 DELETE 出队改为 Pending → Leased(owner,expires_at) → Acked(DELETE)。
--   state: 'Pending'（待租约）/ 'Leased'（已租约，未确认）/ 'Acked'（已确认，将被删除）
--   lease_owner: 租约持有者标识（如 worker ID）；lease_expires_at: 租约过期时间
--   崩溃恢复：过期 Leased 行由 RequeueExpiredAsync 回滚为 Pending；新实例可重新租约。
-- kernel_transport_inbox: instruction_id 主键 — 每条待处理指令一行
-- created_at 用于 FIFO 排序；LeaseAsync 使用 FOR UPDATE SKIP LOCKED 取最旧 Pending 行并标记 Leased
-- 完整 AgentKernelInstruction 保存在 data jsonb
CREATE TABLE IF NOT EXISTS {kernelTransportInbox} (
    instruction_id text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (instruction_id)
);

-- P0-1：租约模型列追加（幂等）
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS state text NOT NULL DEFAULT 'Pending';
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS lease_owner text;
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz;
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS lease_token text;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_inbox", "created")} ON {kernelTransportInbox} (created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_inbox", "instruction")} ON {kernelTransportInbox} (instruction_id);
-- P0-1：按 state + created_at 查 Pending 行（LeaseAsync 取最旧 Pending）
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_inbox", "pending")} ON {kernelTransportInbox} (created_at ASC) WHERE state = 'Pending';
-- P0-1：按 lease_expires_at 查过期 Leased 行（RequeueExpiredAsync 扫描）
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_inbox", "expired")} ON {kernelTransportInbox} (lease_expires_at ASC) WHERE state = 'Leased';

-- kernel_transport_outbox: result_id 主键 — 每条待读取结果一行
-- created_at 用于 FIFO 排序；LeaseResultAsync 使用 FOR UPDATE SKIP LOCKED 取最旧 Pending 行并标记 Leased
-- 完整 AgentKernelResult 保存在 data jsonb；instruction_id 反规范化以便查询
CREATE TABLE IF NOT EXISTS {kernelTransportOutbox} (
    result_id text NOT NULL,
    instruction_id text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (result_id)
);

-- P0-1：租约模型列追加（幂等）
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS state text NOT NULL DEFAULT 'Pending';
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS lease_owner text;
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz;
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS lease_token text;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_outbox", "created")} ON {kernelTransportOutbox} (created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_outbox", "instruction")} ON {kernelTransportOutbox} (instruction_id);
-- P0-1：按 state + created_at 查 Pending 行
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_outbox", "pending")} ON {kernelTransportOutbox} (created_at ASC) WHERE state = 'Pending';
-- P0-1：按 lease_expires_at 查过期 Leased 行
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_outbox", "expired")} ON {kernelTransportOutbox} (lease_expires_at ASC) WHERE state = 'Leased';

-- Durable Delivery v37：重试与死信支持
-- 1) kernel_transport_inbox / kernel_transport_outbox / kernel_result_outbox 追加重试列（幂等）：
--    attempt_count：累计失败次数（Nack / lease 过期回滚 +1）；max_attempts：上限，超过移入 DLQ；
--    next_attempt_at：指数退避后的下次可重试时间（NULL 表示立即可重试）；
--    last_error：最近一次失败原因（截断到合理长度）；dead_letter_reason：进入 DLQ 时的归档原因。
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0;
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 5;
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NULL;
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS last_error text NULL;
ALTER TABLE {kernelTransportInbox} ADD COLUMN IF NOT EXISTS dead_letter_reason text NULL;

ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0;
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 5;
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NULL;
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS last_error text NULL;
ALTER TABLE {kernelTransportOutbox} ADD COLUMN IF NOT EXISTS dead_letter_reason text NULL;

ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0;
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 5;
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NULL;
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS last_error text NULL;
ALTER TABLE {kernelResultOutbox} ADD COLUMN IF NOT EXISTS dead_letter_reason text NULL;

-- 2) 按 next_attempt_at 取可重试的 Pending 行（LeaseAsync WHERE next_attempt_at IS NULL OR next_attempt_at <= now）
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_inbox", "retry")} ON {kernelTransportInbox} (next_attempt_at ASC) WHERE state = 'Pending';
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_outbox", "retry")} ON {kernelTransportOutbox} (next_attempt_at ASC) WHERE state = 'Pending';
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_result_outbox", "retry")} ON {kernelResultOutbox} (next_attempt_at ASC) WHERE state = 'Pending';

-- 3) kernel_transport_outbox.instruction_id UNIQUE 约束：让 SendResultAsync 用
--    result_id = 'result-' || instruction_id + ON CONFLICT (instruction_id) DO NOTHING 实现幂等，
--    避免 Replayer 在 Ack 失败后重发产生重复投递（消费者侧也可基于 instruction_id 去重）。
--    预清理：删除同一 instruction_id 的重复旧行（保留最新一行），保证 UNIQUE 约束创建成功。
DELETE FROM {kernelTransportOutbox} o
WHERE result_id NOT IN (
    SELECT result_id FROM (
        SELECT result_id, instruction_id,
               ROW_NUMBER() OVER (PARTITION BY instruction_id ORDER BY created_at DESC, result_id DESC) AS rn
        FROM {kernelTransportOutbox}
    ) ranked WHERE rn = 1
) AND instruction_id <> '';
CREATE UNIQUE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_outbox", "instruction_unique")}
    ON {kernelTransportOutbox} (instruction_id) WHERE instruction_id <> '';

-- 4) kernel_transport_dead_letter：Durable Transport Dead Letter Queue 表
--    超过 max_attempts 的指令/结果从原表归档到此表，保留原始 data 与失败诊断供人工介入/重投。
--    source：'inbox' / 'outbox' / 'result_outbox'，标识来源表；
--    original_id：原表主键（instruction_id / result_id / outbox_id）；
--    attempt_count：归档时的累计失败次数；last_error / dead_letter_reason：失败诊断；
--    original_data：原行完整 data jsonb，便于诊断或重投回原队列。
CREATE TABLE IF NOT EXISTS {kernelTransportDeadLetter} (
    dead_letter_id text NOT NULL,
    source text NOT NULL,
    original_id text NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    last_error text NULL,
    dead_letter_reason text NULL,
    original_data jsonb NOT NULL DEFAULT jsonb_build_object(),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (dead_letter_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_dead_letter", "source")} ON {kernelTransportDeadLetter} (source, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "kernel_transport_dead_letter", "created")} ON {kernelTransportDeadLetter} (created_at ASC);

-- R29 WP-A-1：Model Artifact Registry 持久化表
-- model_artifacts: model_artifact_id 主键 — 每个已注册模型工件描述符一行
-- 反规范化 model_name / model_version / feature_schema_version / calibration_version / engine_kind /
-- content_hash / registered_at 字段以便索引查询；完整 ModelArtifactDescriptor 保存在 data jsonb。
-- 不可变语义：同一 ModelArtifactId 仅允许注册一次（ON CONFLICT DO NOTHING → 重复注册抛异常）。
CREATE TABLE IF NOT EXISTS {modelArtifacts} (
    model_artifact_id text NOT NULL,
    model_name text NOT NULL,
    model_version text NOT NULL,
    feature_schema_version text NOT NULL,
    calibration_version text NOT NULL,
    engine_kind smallint NOT NULL,
    content_hash text NOT NULL,
    artifact_path text,
    description text,
    registered_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (model_artifact_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "model_artifacts", "model_name")} ON {modelArtifacts} (model_name, registered_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "model_artifacts", "registered")} ON {modelArtifacts} (registered_at ASC);

-- R29 WP-E-3：训练数据导出视图。
-- 供 ad-hoc SQL 查询与 BI 工具直接消费，字段对齐 TrainingDataRecord（feature / label / metadata 三段式）。
-- 视图是只读的；导出工具（TrainingDataExporter）通过 IUtilityLedgerStore.QueryAsync 走应用层路径，
-- 此视图作为 SQL 接口供 DBA / 数据分析师 / BI 工具直接查询，无需经过应用层。
CREATE OR REPLACE VIEW {Infrastructure.PostgresNames.Table(options, "vw_utility_ledger_training_data")} AS
SELECT
    -- feature
    deterministic_score AS deterministic_score,
    model_score AS model_score,
    utility_contribution AS utility_contribution,
    expert AS expert,
    -- label
    is_selected AS is_selected,
    drop_reason_code AS drop_reason_code,
    -- metadata
    decision_id AS decision_id,
    candidate_item_id AS candidate_item_id,
    workspace_id AS workspace_id,
    collection_id AS collection_id,
    materialized_at AS materialized_at,
    policy_version AS policy_version
FROM {utilityLedgerEntries};

-- R29 WP-E-4：校准数据导出视图。
-- 供 ad-hoc SQL 查询与 BI 工具直接消费，字段对齐 CalibrationDataRecord（predicted / observed / weight / metadata 四段式）。
-- 仅包含 model_score 非 null 的条目（校准必须有模型预测分数）；
-- 视图是只读的；导出工具（CalibrationDataExporter）通过 IUtilityLedgerStore.QueryAsync 走应用层路径，
-- 此视图作为 SQL 接口供 DBA / 数据分析师 / BI 工具直接查询，无需经过应用层。
CREATE OR REPLACE VIEW {Infrastructure.PostgresNames.Table(options, "vw_utility_ledger_calibration_data")} AS
SELECT
    -- predicted
    model_score AS model_score,
    deterministic_score AS deterministic_score,
    final_score AS final_score,
    -- observed
    is_selected AS is_selected,
    drop_reason_code AS drop_reason_code,
    -- weight
    CASE WHEN utility_contribution > 0 THEN utility_contribution ELSE 1.0 END AS weight,
    -- metadata
    decision_id AS decision_id,
    candidate_item_id AS candidate_item_id,
    workspace_id AS workspace_id,
    collection_id AS collection_id,
    expert AS expert,
    materialized_at AS materialized_at,
    policy_version AS policy_version
FROM {utilityLedgerEntries}
WHERE model_score IS NOT NULL;

-- R29 WP-E-5：User Feedback Ledger 持久化表（用户显式反馈接入：thumbs up/down + 评分修正 + 文本反馈）。
-- user_feedback_entries：与 utility_ledger_entries 通过 (workspace_id, collection_id, decision_id, candidate_item_id) 关联。
-- 反规范化 workspace_id / collection_id / decision_id / candidate_item_id / kind / given_by / given_at 字段以便索引查询；
-- 完整 UserFeedbackEntry 对象保存在 data jsonb，由 store 反序列化。
-- 写入路径由 IUserFeedbackLedger.AppendFeedbackAsync 调用（来自 Service API 端点 POST /api/utility-ledger/feedback）。
-- 幂等：idempotency_key 重复写入由 ON CONFLICT DO UPDATE 覆盖（保留最新反馈）。
-- 关联校验：写入时通过 EXISTS 子查询验证 (decision_id, candidate_item_id, workspace_id, collection_id)
--          在 utility_ledger_entries 中存在；否则抛出 ForeignKeyViolation（强一致性保证）。
CREATE TABLE IF NOT EXISTS {userFeedbackEntries} (
    feedback_entry_id text NOT NULL,
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    decision_id text NOT NULL,
    candidate_item_id text NOT NULL,
    kind text NOT NULL,
    feedback_value double precision NOT NULL,
    feedback_text text,
    given_by text NOT NULL DEFAULT '',
    given_at timestamptz NOT NULL,
    idempotency_key text NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (feedback_entry_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "user_feedback_entries", "workspace")} ON {userFeedbackEntries} (workspace_id, collection_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "user_feedback_entries", "decision")} ON {userFeedbackEntries} (decision_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "user_feedback_entries", "candidate")} ON {userFeedbackEntries} (candidate_item_id);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "user_feedback_entries", "given_by")} ON {userFeedbackEntries} (given_by);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "user_feedback_entries", "given_at")} ON {userFeedbackEntries} (given_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "user_feedback_entries", "idempotency")} ON {userFeedbackEntries} (idempotency_key);

-- R29 WP-E-5：Utility Ledger + User Feedback JOIN 视图。
-- 供 ad-hoc SQL 查询与 BI 工具直接消费，将每次决策的 candidate 与其最新用户反馈关联起来。
-- 反馈关联策略：取每个 (workspace_id, collection_id, decision_id, candidate_item_id) 的最新反馈条目（given_at DESC LIMIT 1）。
-- 无反馈的 candidate 仍会出现在结果中（LEFT JOIN），user_feedback_kind / user_feedback_value 为 null — 与 P8 硬边界一致：
-- 保留"无反馈"样本以避免偏差。
CREATE OR REPLACE VIEW {Infrastructure.PostgresNames.Table(options, "vw_utility_ledger_with_user_feedback")} AS
SELECT
    le.entry_id AS ledger_entry_id,
    le.workspace_id AS workspace_id,
    le.collection_id AS collection_id,
    le.decision_id AS decision_id,
    le.candidate_item_id AS candidate_item_id,
    le.expert AS expert,
    le.utility_contribution AS utility_contribution,
    le.deterministic_score AS deterministic_score,
    le.model_score AS model_score,
    le.final_score AS final_score,
    le.is_selected AS is_selected,
    le.drop_reason_code AS drop_reason_code,
    le.policy_version AS policy_version,
    le.materialized_at AS materialized_at,
    uf.feedback_entry_id AS feedback_entry_id,
    uf.kind AS user_feedback_kind,
    uf.feedback_value AS user_feedback_value,
    uf.feedback_text AS user_feedback_text,
    uf.given_by AS user_feedback_given_by,
    uf.given_at AS user_feedback_given_at
FROM {utilityLedgerEntries} le
LEFT JOIN LATERAL (
    SELECT *
    FROM {userFeedbackEntries} uf_inner
    WHERE uf_inner.workspace_id = le.workspace_id
      AND uf_inner.collection_id = le.collection_id
      AND uf_inner.decision_id = le.decision_id
      AND uf_inner.candidate_item_id = le.candidate_item_id
    ORDER BY uf_inner.given_at DESC
    LIMIT 1
) uf ON true;

-- 任务 F：Agent Run 状态机持久化表（替代 InMemoryAgentRunStore，支持 HA 跨进程恢复）
-- 反规范化 workspace_id / run_id / session_id / state / turn 字段以便索引查询；
-- turn_budget_json / cost_budget_json 存预算 JSON 列；
-- 完整 AgentRun 对象保存在 data jsonb，由 store 反序列化。
-- CreateAsync 使用 ON CONFLICT (workspace_id, run_id) DO NOTHING（幂等）。
-- TransitionStateAsync 使用 expected-state CAS：UPDATE WHERE state = @expected（0 行抛异常）。
CREATE TABLE IF NOT EXISTS {agentRuns} (
    workspace_id text NOT NULL,
    run_id text NOT NULL,
    session_id text NOT NULL,
    task text NOT NULL DEFAULT '',
    state smallint NOT NULL DEFAULT 0,
    turn integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    finished_at timestamptz NULL,
    failure_reason text NULL,
    final_answer text NULL,
    turn_budget_json text NULL,
    cost_budget_json text NULL,
    last_checkpoint_id text NULL,
    last_checkpoint_sequence integer NULL,
    idempotency_key text NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (workspace_id, run_id)
);

-- G4：v34 → v35 迁移：为已有 agent_runs 表补充 last_checkpoint_id / last_checkpoint_sequence 列
-- （新表已在上方 CREATE TABLE 中包含；ALTER 仅对已存在的旧表生效）
ALTER TABLE {agentRuns} ADD COLUMN IF NOT EXISTS last_checkpoint_id text NULL;
ALTER TABLE {agentRuns} ADD COLUMN IF NOT EXISTS last_checkpoint_sequence integer NULL;

-- WP-2：v43 → v44 迁移：为已有 agent_runs 表补充 idempotency_key 列
-- （新表已在上方 CREATE TABLE 中包含；ALTER 仅对已存在的旧表生效，幂等）
ALTER TABLE {agentRuns} ADD COLUMN IF NOT EXISTS idempotency_key text NULL;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_runs", "session")} ON {agentRuns} (workspace_id, session_id, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_runs", "state")} ON {agentRuns} (state, created_at ASC);
-- WP-2：partial UNIQUE 索引让同一 workspace 内 idempotency_key 全局唯一（NULL 不参与唯一约束），
-- 防止客户端重试/网络抖动产生重复 Run； GetByIdempotencyKeyAsync 走此索引点查。
CREATE UNIQUE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_runs", "idempotency")} ON {agentRuns} (workspace_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

-- 任务 F：Agent Run 事件流哈希链持久化表
-- 主键 (workspace_id, run_id, sequence)：UNIQUE 约束防重序列号，保证事件流单调递增。
-- AppendAsync 校验 sequence 连续性 + prev_chain_hash 链接（链头为 null）。
-- 完整 AgentRunEvent 对象保存在 data jsonb，由 store 反序列化。
-- 哈希链字段：content_hash（SHA-256 of payload）、prev_chain_hash（前一事件 content_hash）。
CREATE TABLE IF NOT EXISTS {agentRunEvents} (
    event_id text NOT NULL,
    workspace_id text NOT NULL,
    run_id text NOT NULL,
    sequence integer NOT NULL,
    event_type smallint NOT NULL,
    state smallint NOT NULL,
    payload text NOT NULL DEFAULT '',
    content_hash text,
    prev_chain_hash text,
    occurred_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (workspace_id, run_id, sequence)
);

-- 任务 D：Canary HA 聚合表（跨实例指标样本 + Leader 租约 + stage epoch 跟踪）
-- canary_metrics_samples：各实例定期 UPSERT 本地 CanaryObservationMetrics 快照（含外部指标）。
--   v36 最新快照模型：PK = (run_id, stage_epoch, instance_id)，每次 UPSERT 覆盖该实例最新累计值，
--   不再追加行。聚合时只汇总 WHERE stage_epoch = current_epoch 的行，避免重复累计。
--   反规范化 recorded_at 字段以便索引查询；外部指标 nullable（未采集时为 NULL，聚合用 AVG 跳过 NULL）。
CREATE TABLE IF NOT EXISTS {canaryMetricsSamples} (
    sample_id text NOT NULL DEFAULT '',
    run_id text NOT NULL,
    instance_id text NOT NULL,
    stage_epoch bigint NOT NULL DEFAULT 0,
    recorded_at timestamptz NOT NULL,
    total_observations integer NOT NULL DEFAULT 0,
    divergent_count integer NOT NULL DEFAULT 0,
    v2_error_count integer NOT NULL DEFAULT 0,
    legacy_error_count integer NOT NULL DEFAULT 0,
    v2_p95_latency_ms double precision NOT NULL DEFAULT 0,
    legacy_p95_latency_ms double precision NOT NULL DEFAULT 0,
    average_quality_score double precision NOT NULL DEFAULT 0,
    task_success_rate double precision,
    tool_success_rate double precision,
    repair_rate double precision,
    safety_violation_rate double precision,
    context_precision double precision,
    context_recall_proxy double precision,
    user_acceptance double precision,
    answer_quality double precision,
    token_cost double precision,
    inference_cost double precision,
    external_sample_count integer NOT NULL DEFAULT 0,
    external_window_start timestamptz,
    external_window_end timestamptz,
    -- P10：DDSketch 二进制字节（各实例持久化，Leader 聚合时 MergeFrom 合并查询总体 P95）
    v2_latency_sketch bytea,
    legacy_latency_sketch bytea,
    -- P11：成功率分子/分母（聚合时 SUM(分子)/SUM(分母) 替代 AVG(rate)）
    task_success_sum double precision,
    task_success_count bigint,
    tool_success_sum double precision,
    tool_success_count bigint,
    PRIMARY KEY (run_id, stage_epoch, instance_id)
);

-- P10/P11 迁移：为已有数据库（v32-v42 创建的旧表）补加 sketch 与 success sum/count 列（幂等）。
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS v2_latency_sketch bytea;
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS legacy_latency_sketch bytea;
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS task_success_sum double precision;
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS task_success_count bigint;
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS tool_success_sum double precision;
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS tool_success_count bigint;

-- v36 迁移：为已有数据库（v32-v35 创建的旧表）补加 stage_epoch 列并迁移 PK。
-- 新数据库由上方 CREATE TABLE 直接创建新结构；ALTER 仅对已存在的旧表生效（幂等）。
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS stage_epoch bigint NOT NULL DEFAULT 0;
ALTER TABLE {canaryMetricsSamples} ADD COLUMN IF NOT EXISTS sample_id text NOT NULL DEFAULT '';
-- 旧表 PK 为 (sample_id)；新表 PK 为 (run_id, stage_epoch, instance_id)。
-- 使用 DO 块幂等切换：先去重（保留每实例最新行），再 drop 旧 PK，再加新 PK。
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = '{canaryMetricsSamples}'::regclass
          AND contype = 'p'
          AND conname = 'canary_metrics_samples_pkey'
    ) THEN
        -- 检查旧 PK 是否仅含 sample_id（v32-v35 结构）
        IF EXISTS (
            SELECT 1 FROM pg_index
            WHERE indrelid = '{canaryMetricsSamples}'::regclass
              AND indexname = 'canary_metrics_samples_pkey'
              AND array_length(indkey::smallint[], 1) = 1
        ) THEN
            -- 去重：同一 (run_id, stage_epoch, instance_id) 保留 recorded_at 最新的一行
            DELETE FROM {canaryMetricsSamples} a
            USING {canaryMetricsSamples} b
            WHERE a.run_id = b.run_id
              AND COALESCE(a.stage_epoch, 0) = COALESCE(b.stage_epoch, 0)
              AND a.instance_id = b.instance_id
              AND a.ctid < b.ctid;
            ALTER TABLE {canaryMetricsSamples} DROP CONSTRAINT canary_metrics_samples_pkey;
            ALTER TABLE {canaryMetricsSamples} ADD PRIMARY KEY (run_id, stage_epoch, instance_id);
        END IF;
    END IF;
EXCEPTION WHEN OTHERS THEN
    -- 防御性：若 PK 切换失败（如存在重复行无法去重），不阻塞迁移，下次启动重试。
    RAISE NOTICE 'canary_metrics_samples PK 迁移跳过: %', SQLERRM;
END $$;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "canary_metrics_samples", "run_recorded")} ON {canaryMetricsSamples} (run_id, stage_epoch, recorded_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "canary_metrics_samples", "run_instance")} ON {canaryMetricsSamples} (run_id, stage_epoch, instance_id, recorded_at DESC);

-- canary_leader_leases：Leader 租约表（每个 run_id 至多一条行）。
-- TryAcquireAsync 使用 INSERT ... ON CONFLICT (run_id) DO UPDATE WHERE lease_expires_at < now
--   原子获取租约：无现有行 → INSERT 成功；现有行过期 → ON CONFLICT 更新；现有行未过期 → 0 行返回 null。
-- RenewAsync / ReleaseAsync 通过 lease_token CAS 保证只有持有者能操作。
-- ReapExpiredAsync 删除 lease_expires_at < now 的行（崩溃 leader 持有的过期租约最终释放）。
-- P12 修复：新增 fencing_token bigint 列（单调递增），用于 AdvanceEpochAsync 等 Progression
--   更新的 lease 校验。每次 TryAcquireAsync 成功获取（含抢占过期）时递增；RenewAsync 不递增。
CREATE TABLE IF NOT EXISTS {canaryLeaderLeases} (
    run_id text NOT NULL,
    owner text NOT NULL,
    lease_token text NOT NULL,
    fencing_token bigint NOT NULL DEFAULT 1,
    acquired_at timestamptz NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    PRIMARY KEY (run_id)
);

-- P12：为已有 canary_leader_leases 表补充 fencing_token 列（新表已在上方 CREATE TABLE 中包含；ALTER 仅对已存在的旧表生效）
ALTER TABLE {canaryLeaderLeases} ADD COLUMN IF NOT EXISTS fencing_token bigint NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "canary_leader_leases", "expires")} ON {canaryLeaderLeases} (lease_expires_at ASC);

-- canary_run_epochs：per-run 单调递增 stage epoch 跟踪表（v36 新增）。
-- Leader 推进百分比档时调用 AdvanceEpochAsync 递增 current_epoch；
-- 所有实例在下一次轮询时读取 current_epoch，若发现变化则 Reset 本地 Collector。
-- 聚合器只汇总 WHERE stage_epoch = current_epoch 的快照行，旧 epoch 数据不参与聚合。
CREATE TABLE IF NOT EXISTS {canaryRunEpochs} (
    run_id text NOT NULL,
    current_epoch bigint NOT NULL DEFAULT 0,
    advanced_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (run_id)
);

-- Perf-7：Canary 严格 HA 单事务接口持久化表（v45 新增）。
-- canary_pipelines：per-run pipeline 状态表，revision 列用于 CAS 原子更新。
--   ApplyCanaryDecisionAsync 在单一事务内：
--     1. SELECT fencing_token FROM canary_leader_leases WHERE ...（lease 校验）
--     2. UPDATE canary_pipelines SET percentage=@new WHERE run_id=@runId AND revision=@expected（CAS）
--     3. INSERT canary_transition_audit ...（审计，同事务）
--     4. UPSERT canary_run_epochs ...（epoch 递增，同事务）
--   任一步骤失败则整个事务 ROLLBACK，确保旧 Leader 无法在 lease 失效后修改 rollout。
--   首次初始化时通过 ON CONFLICT DO UPDATE WHERE revision = 0 完成 INSERT。
CREATE TABLE IF NOT EXISTS {canaryPipelines} (
    run_id text NOT NULL,
    percentage integer NOT NULL DEFAULT 0,
    status text NOT NULL DEFAULT 'Active',
    revision bigint NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (run_id)
);

-- canary_transition_audit：append-only 审计表，记录每次 Canary 决策（Promote/Rollback/Hold）。
-- 与 canary_pipelines UPDATE 同事务写入，确保审计与状态强一致（旧路径分两步写可能丢失审计）。
-- transition_id 用于幂等去重（同 transitionId 重复调用不产生重复审计）。
CREATE TABLE IF NOT EXISTS {canaryTransitionAudit} (
    audit_id text NOT NULL,
    run_id text NOT NULL,
    transition_id text NOT NULL,
    from_percentage integer NOT NULL,
    to_percentage integer NOT NULL,
    decision text NOT NULL,
    rationale text NOT NULL DEFAULT '',
    transition text NOT NULL DEFAULT '',
    fencing_token bigint NOT NULL,
    new_epoch bigint NOT NULL,
    transitioned_at timestamptz NOT NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (audit_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "canary_transition_audit", "run")} ON {canaryTransitionAudit} (run_id, transitioned_at DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "canary_transition_audit", "transition")} ON {canaryTransitionAudit} (run_id, transition_id);

-- Learning Loop Durable Outbox：Decision 物化事件持久化表。
-- 替代 fire-and-forget Task.Run → MaterializeAsync → catch-all 模式，消除进程崩溃时静默丢训练数据。
-- 生命周期：Pending → Processing(Leased) → Acked / DeadLettered（超过 max_retry_count）。
-- AcquirePendingAsync 使用 SELECT ... FOR UPDATE SKIP LOCKED（与 relation_outbox / context_jobs 一致）。
CREATE TABLE IF NOT EXISTS {learningEventOutbox} (
    event_id text NOT NULL,
    workspace_id text NOT NULL DEFAULT '',
    collection_id text NOT NULL DEFAULT '',
    decision_id text NOT NULL,
    payload jsonb NOT NULL,
    state text NOT NULL DEFAULT 'Pending',
    retry_count integer NOT NULL DEFAULT 0,
    max_retry_count integer NOT NULL DEFAULT 5,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz NULL,
    lease_owner text NULL,
    lease_expires_at timestamptz NULL,
    last_error text NULL,
    dead_letter_reason text NULL,
    PRIMARY KEY (event_id)
);

-- P0-8：租约 token 列追加（幂等）— AcquirePendingAsync 生成唯一 token 写入 lease_token，
-- MarkAckedAsync / MarkFailedAsync / RenewLeaseAsync 通过 WHERE lease_token = @token CAS 校验
-- 仅持有者可 Ack/Nack/Renew。与 kernel_result_outbox / kernel_transport_* 对齐。
ALTER TABLE {learningEventOutbox} ADD COLUMN IF NOT EXISTS lease_token text;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "learning_event_outbox", "state")} ON {learningEventOutbox} (state, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "learning_event_outbox", "lease")} ON {learningEventOutbox} (state, lease_expires_at);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "learning_event_outbox", "workspace")} ON {learningEventOutbox} (workspace_id, collection_id);

-- P0-6：Model Control Plane 激活审计持久化表
-- model_activation_audit: append-only 审计表，记录 Activate / Rollback / Retire / Shadow / Warmup /
--   Validate / Register 等模型生命周期事件，含 previous_model_id / operator / reason / node_id 业务字段。
-- 反规范化 model_artifact_id / model_name / operation / timestamp 字段以便索引查询；
-- 完整 ModelActivationAuditEntry 对象保存在 data jsonb，由 store 反序列化。
-- 不可变语义：审计记录一旦写入不可修改（无 ON CONFLICT 子句，重复写入由调用方保证幂等）。
CREATE TABLE IF NOT EXISTS {modelActivationAudit} (
    audit_id text NOT NULL,
    model_artifact_id text NOT NULL,
    model_name text NOT NULL,
    operation smallint NOT NULL,
    succeeded boolean NOT NULL,
    timestamp timestamptz NOT NULL,
    previous_model_artifact_id text,
    operator text,
    reason text,
    error_message text,
    node_id text,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (audit_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "model_activation_audit", "model")} ON {modelActivationAudit} (model_artifact_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "model_activation_audit", "operation")} ON {modelActivationAudit} (operation, timestamp DESC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "model_activation_audit", "timestamp")} ON {modelActivationAudit} (timestamp DESC);

-- 运行时能力补齐：durable approval 持久化表
-- 反规范化 workspace_id / approval_id / run_id / tool_call_id / tool_name / status 字段以便索引查询；
-- 完整 AgentApproval 对象保存在 data jsonb，由 store 反序列化。
-- CreateAsync 使用 ON CONFLICT (workspace_id, approval_id) DO NOTHING（幂等）。
-- ResolveAsync 使用 expected-state CAS：UPDATE WHERE status = Pending（0 行抛异常）。
CREATE TABLE IF NOT EXISTS {agentRunApprovals} (
    workspace_id text NOT NULL,
    approval_id text NOT NULL,
    run_id text NOT NULL,
    tool_call_id text NOT NULL DEFAULT '',
    tool_name text NOT NULL DEFAULT '',
    status smallint NOT NULL DEFAULT 0,
    reason text NULL,
    rejection_reason text NULL,
    approver_id text NULL,
    created_at timestamptz NOT NULL,
    resolved_at timestamptz NULL,
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (workspace_id, approval_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_run_approvals", "run_pending")} ON {agentRunApprovals} (workspace_id, run_id, status, created_at ASC);
CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_run_approvals", "run")} ON {agentRunApprovals} (workspace_id, run_id, created_at ASC);

-- 运行时能力补齐：HA Run Owner Lease 持久化表
-- 复用 canary_leader_leases 模式：每个 run_id 至多一条行，
-- TryAcquireAsync 使用 INSERT ... ON CONFLICT DO UPDATE WHERE lease_expires_at < now（CAS 抢占过期租约）。
-- RenewAsync / ReleaseAsync / ReapExpiredAsync 基于 lease_token 匹配。
-- P0-4 修复双执行：新增 fencing_token bigint 列（单调递增），用于副作用 UPDATE 的 lease 校验。
-- 每次 TryAcquireAsync 成功获取（含抢占过期）时 fencing_token = 旧值 + 1；RenewAsync 不递增。
CREATE TABLE IF NOT EXISTS {agentRunLeases} (
    run_id text NOT NULL,
    owner text NOT NULL,
    lease_token text NOT NULL,
    fencing_token bigint NOT NULL DEFAULT 1,
    acquired_at timestamptz NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    PRIMARY KEY (run_id)
);

-- P0-4：为已有 agent_run_leases 表补充 fencing_token 列（新表已在上方 CREATE TABLE 中包含；ALTER 仅对已存在的旧表生效）
ALTER TABLE {agentRunLeases} ADD COLUMN IF NOT EXISTS fencing_token bigint NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "agent_run_leases", "expires")} ON {agentRunLeases} (lease_expires_at);

-- R29 WP-A-2：Desired Model State 持久化表（HA 多节点模型期望状态同步）
-- 每个模型至多一条记录，由 SetAsync 的 ON CONFLICT (model_id) DO UPDATE 维护。
-- generation 字段用于乐观并发控制：ReconcilerWorker 仅当远端 generation > 本地时应用变更。
-- content_hash 用于快速检测内容变更（避免不必要的 Activate/Deactivate 操作）。
CREATE TABLE IF NOT EXISTS {desiredModelStates} (
    model_id text NOT NULL,
    desired_state text NOT NULL,
    generation bigint NOT NULL DEFAULT 1,
    content_hash text NOT NULL DEFAULT '',
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text NOT NULL DEFAULT '',
    data jsonb NOT NULL DEFAULT jsonb_build_object(),
    PRIMARY KEY (model_id)
);

CREATE INDEX IF NOT EXISTS {Infrastructure.PostgresNames.Index(options, "desired_model_states", "updated")} ON {desiredModelStates} (updated_at DESC);

-- P0-9: Cluster Model Slot (single champion source of truth)
-- Single-row table per slot_name (e.g., "primary"). CAS on revision atomically switches ActiveModelArtifactId.
-- Replaces desired_model_states multi-row Active/Inactive model: one UPDATE invalidates old champion + activates new.
CREATE TABLE IF NOT EXISTS {clusterModelSlots} (
    slot_name text PRIMARY KEY DEFAULT 'primary',
    active_model_artifact_id text,
    content_hash text,
    revision bigint NOT NULL DEFAULT 0,
    desired_status text NOT NULL DEFAULT 'Inactive',
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text
);
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
