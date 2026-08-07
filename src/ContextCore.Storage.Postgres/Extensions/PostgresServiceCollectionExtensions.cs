using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContextCore.Storage.Postgres.Extensions;

/// <summary>注册 ContextCore PostgreSQL 存储后端。</summary>
public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// 注册 PostgreSQL 存储实现（全量 Service-ready 契约）。
    /// 该扩展只注册服务，不主动连接数据库；是否自动建表由 <see cref="PostgresOptions.AutoMigrate"/> 控制。
    /// </summary>
    public static IServiceCollection AddContextCorePostgresStorage(
        this IServiceCollection services,
        PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<PostgresJsonSerializer>();
        services.AddSingleton<PostgresConnectionFactory>();
        services.AddSingleton<IPostgresConnectionFactory>(sp => sp.GetRequiredService<PostgresConnectionFactory>());
        services.AddSingleton<PostgresMigrationRunner>();
        services.AddSingleton<IStoreMigrationRunner>(sp => sp.GetRequiredService<PostgresMigrationRunner>());

        // 注册 PostgreSQL 跨 store 写入事务作用域工厂。
        // BasicContextIngestionService 通过 IWriteTransactionScopeFactory? 注入检测是否启用事务路径。
        // 仅 Postgres provider 注册——File/InMemory 不注册，CanUseTransactionPath 返回 false 走原有无事务路径。
        services.AddSingleton<PostgresWriteTransactionScopeFactory>();
        services.AddSingleton<IWriteTransactionScopeFactory>(sp => sp.GetRequiredService<PostgresWriteTransactionScopeFactory>());

        // ContextStore + CollectionStore
        services.AddSingleton<PostgresContextStore>();
        services.AddSingleton<IContextStore>(sp => sp.GetRequiredService<PostgresContextStore>());
        services.AddSingleton<IContextCollectionStore>(sp => sp.GetRequiredService<PostgresContextStore>());

        // ContextIndex
        services.AddSingleton<PostgresContextIndex>();
        services.AddSingleton<IContextIndex>(sp => sp.GetRequiredService<PostgresContextIndex>());

        // MemoryStore
        // 注入 IContextTokenizerResolver（可选），让 SaveAsync 在摄取阶段计算并持久化 tokenization metadata。
        // GetService 返回 null 时不影响 Store 基本读写（仅 token_count 列保持 NULL）。
        services.AddSingleton<PostgresMemoryStore>(sp => new PostgresMemoryStore(
            sp.GetRequiredService<PostgresConnectionFactory>(),
            sp.GetRequiredService<PostgresJsonSerializer>(),
            sp.GetRequiredService<PostgresMigrationRunner>(),
            sp.GetService<IContextTokenizerResolver>()));
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<PostgresMemoryStore>());

        // WorkingMemoryStore (IWorkingMemoryService + IPromotionRecordStore + IPromotionCandidateStore)
        services.AddSingleton<PostgresWorkingMemoryStore>();
        services.AddSingleton<IWorkingMemoryService>(sp => sp.GetRequiredService<PostgresWorkingMemoryStore>());
        services.AddSingleton<IPromotionRecordStore>(sp => sp.GetRequiredService<PostgresWorkingMemoryStore>());
        services.AddSingleton<IPromotionCandidateStore>(sp => sp.GetRequiredService<PostgresWorkingMemoryStore>());

        // RelationStore
        services.AddSingleton<PostgresRelationStore>();
        services.AddSingleton<IRelationStore>(sp => sp.GetRequiredService<PostgresRelationStore>());
        services.AddSingleton<PostgresRelationReviewStore>();
        services.AddSingleton<IRelationReviewStore>(sp => sp.GetRequiredService<PostgresRelationReviewStore>());
        services.AddSingleton<PostgresRelationDiagnosticsStore>();
        // 关系写入 outbox 存储。仅 Postgres provider 注册——
        // FileSystem/InMemory 不注册，OutboxAwareRelationProjectionWriter 与 RelationReconciliationWorker
        // 检测到 null 时回退到无 outbox 路径（仅走 stale-edge 周期扫描）。
        services.AddSingleton<PostgresRelationOutboxStore>();
        services.AddSingleton<IRelationOutboxStore>(sp => sp.GetRequiredService<PostgresRelationOutboxStore>());

        // Learning feedback / review 接口正式绑定 Postgres 实现。
        // 此前 Service 层在 RegisterPostgres 中用 Unsupported*Store 覆盖了接口绑定，
        // 导致运行时即便 Postgres provider 已就绪也走 Unsupported 路径。现在移除覆盖，
        // 让 PostgresLearningFeedbackStore / PostgresLearningFeedbackReviewStore 成为 source of truth。
        services.AddSingleton<PostgresLearningFeedbackStore>();
        services.AddSingleton<ILearningFeedbackStore>(sp => sp.GetRequiredService<PostgresLearningFeedbackStore>());
        services.AddSingleton<PostgresLearningFeedbackReviewStore>();
        services.AddSingleton<ILearningFeedbackReviewStore>(sp => sp.GetRequiredService<PostgresLearningFeedbackReviewStore>());
        services.AddSingleton<PostgresLearningFeatureCandidateStore>();

        // ConstraintStore
        // 注入 IContextTokenizerResolver（可选），让 SaveAsync 在摄取阶段计算并持久化 tokenization metadata。
        services.AddSingleton<PostgresConstraintStore>(sp => new PostgresConstraintStore(
            sp.GetRequiredService<PostgresConnectionFactory>(),
            sp.GetRequiredService<PostgresJsonSerializer>(),
            sp.GetRequiredService<PostgresMigrationRunner>(),
            sp.GetService<IContextTokenizerResolver>()));
        services.AddSingleton<IConstraintStore>(sp => sp.GetRequiredService<PostgresConstraintStore>());

        // GlobalContextStore
        services.AddSingleton<PostgresGlobalContextStore>();
        services.AddSingleton<IGlobalContextStore>(sp => sp.GetRequiredService<PostgresGlobalContextStore>());

        // VectorStore
        services.AddSingleton<PostgresVectorStore>();
        services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<PostgresVectorStore>());
        services.AddSingleton<PostgresVectorIndexStore>();
        services.AddSingleton<IVectorIndexStore>(sp => sp.GetRequiredService<PostgresVectorIndexStore>());

        // RetrievalTraceStore
        services.AddSingleton<PostgresRetrievalTraceStore>();
        services.AddSingleton<IRetrievalTraceStore>(sp => sp.GetRequiredService<PostgresRetrievalTraceStore>());

        // DecisionTraceStore。替代 Unsupported 占位，让 HA 场景下决策审计可持久化。
        services.AddSingleton<PostgresDecisionTraceStore>();
        services.AddSingleton<IDecisionTraceStore>(sp => sp.GetRequiredService<PostgresDecisionTraceStore>());

        // Short-term memory / promotion / candidate review stores。
        // 替代 Unsupported 占位，让 HA 场景下短期记忆与晋升审核可持久化。
        services.AddSingleton<PostgresShortTermMemoryStore>();
        services.AddSingleton<IShortTermMemoryStore>(sp => sp.GetRequiredService<PostgresShortTermMemoryStore>());
        services.AddSingleton<PostgresShortTermPromotionCandidateStore>();
        services.AddSingleton<IShortTermPromotionCandidateStore>(sp => sp.GetRequiredService<PostgresShortTermPromotionCandidateStore>());
        services.AddSingleton<PostgresCandidateMemoryReviewStore>();
        services.AddSingleton<ICandidateMemoryReviewStore>(sp => sp.GetRequiredService<PostgresCandidateMemoryReviewStore>());
        services.AddSingleton<PostgresStableReviewCandidateStore>();
        services.AddSingleton<IStableReviewCandidateStore>(sp => sp.GetRequiredService<PostgresStableReviewCandidateStore>());

        // Context learning / governance review stores。
        // 替代 Unsupported 占位，让 HA 场景下学习记录与生命周期审核可持久化。
        services.AddSingleton<PostgresContextLearningStore>();
        services.AddSingleton<IContextLearningStore>(sp => sp.GetRequiredService<PostgresContextLearningStore>());
        services.AddSingleton<PostgresStableLifecycleReviewStore>();
        services.AddSingleton<IStableLifecycleReviewStore>(sp => sp.GetRequiredService<PostgresStableLifecycleReviewStore>());
        services.AddSingleton<PostgresCandidateConstraintReviewStore>();
        services.AddSingleton<ICandidateConstraintReviewStore>(sp => sp.GetRequiredService<PostgresCandidateConstraintReviewStore>());
        services.AddSingleton<PostgresConstraintGapCandidateStore>();
        services.AddSingleton<IConstraintGapCandidateStore>(sp => sp.GetRequiredService<PostgresConstraintGapCandidateStore>());

        // vector lifecycle + artifact stores。
        // 替代 Unsupported 占位，完成 阶段一垂直闭环，Postgres 无 Unsupported store。
        services.AddSingleton<PostgresVectorReindexReportStore>();
        services.AddSingleton<IVectorReindexReportStore>(sp => sp.GetRequiredService<PostgresVectorReindexReportStore>());
        services.AddSingleton<PostgresVectorLifecycleMetadataReviewCandidateStore>();
        services.AddSingleton<IVectorLifecycleMetadataReviewCandidateStore>(sp => sp.GetRequiredService<PostgresVectorLifecycleMetadataReviewCandidateStore>());
        services.AddSingleton<PostgresVectorLifecycleMetadataReviewStore>();
        services.AddSingleton<IVectorLifecycleMetadataReviewStore>(sp => sp.GetRequiredService<PostgresVectorLifecycleMetadataReviewStore>());
        services.AddSingleton<PostgresVectorLifecycleSidecarMetadataStore>();
        services.AddSingleton<IVectorLifecycleSidecarMetadataStore>(sp => sp.GetRequiredService<PostgresVectorLifecycleSidecarMetadataStore>());
        services.AddSingleton<PostgresArtifactStore>();
        services.AddSingleton<IArtifactStore>(sp => sp.GetRequiredService<PostgresArtifactStore>());

        // 分布式 context state 版本存储。覆盖 CoreExtensions 的 InMemory 默认注册。
        // 多实例 Worker 通过 Postgres 行级锁共享单调递增的版本号，支持跨实例 cache invalidation。
        services.AddSingleton<PostgresContextStateVersionStore>();
        services.AddSingleton<IContextStateVersionStore>(sp => sp.GetRequiredService<PostgresContextStateVersionStore>());

        // Agent Runtime 持久化（checkpoint + task state）。
        // 替代 InMemory 默认注册，让 HA 场景下 agent session/task 状态可跨进程持久化与恢复。
        services.AddSingleton<PostgresAgentCheckpointStore>();
        services.AddSingleton<IAgentCheckpointStore>(sp => sp.GetRequiredService<PostgresAgentCheckpointStore>());
        // 显式注册 IPersistentAgentCheckpointStore 标记接口以区分持久化能力。
        // delta 链路完全由恢复路径（事件流 + checkpoint 链）通过标准 GetAsync 走链，
        // Store 不需感知 delta 语义 — 只持久化完整 AgentCheckpoint blob。
        services.AddSingleton<IPersistentAgentCheckpointStore>(sp => sp.GetRequiredService<PostgresAgentCheckpointStore>());
        services.AddSingleton<PostgresAgentTaskStateStore>();
        services.AddSingleton<IAgentTaskStateStore>(sp => sp.GetRequiredService<PostgresAgentTaskStateStore>());

        // Tool Dispatch Journal 持久化（PostgreSQL）。
        // 替代（当前未注册的）InMemory 默认实现，让 HA 场景下 tool 调用状态机可跨进程持久化与崩溃恢复。
        // 注册为 IToolDispatchJournal 让 DefaultDurableToolExecutor 自动注入；
        // 同时注册为 IPersistentToolDispatchJournal 以便显式区分持久化能力。
        services.AddSingleton<PostgresToolDispatchJournal>();
        services.AddSingleton<IToolDispatchJournal>(sp => sp.GetRequiredService<PostgresToolDispatchJournal>());
        services.AddSingleton<IPersistentToolDispatchJournal>(sp => sp.GetRequiredService<PostgresToolDispatchJournal>());

        // Durable Tool Result 缓存持久化（PostgreSQL）。
        // 让 DefaultDurableToolExecutor 在 Journal 已 Committed/ResultDelivered 时从持久化缓存返回结果，
        // 防止 HA 崩溃恢复时已执行的外部副作用结果丢失（被迫重新 Dispatch）。
        services.AddSingleton<PostgresDurableToolResultStore>();
        services.AddSingleton<IDurableToolResultStore>(sp => sp.GetRequiredService<PostgresDurableToolResultStore>());

        // Model Artifact Registry 持久化（PostgreSQL）。
        // 替代（当前未注册的）InMemory 默认实现，让 HA 场景下模型工件描述符可跨进程持久化与查询。
        // 注册为 IModelArtifactRegistry 让消费方（如 OnnxInferenceEngine 启动加载器）自动注入；
        // 同时注册为 IPersistentModelArtifactRegistry 以便显式区分持久化能力。
        services.AddSingleton<PostgresModelArtifactRegistry>();
        services.AddSingleton<IModelArtifactRegistry>(sp => sp.GetRequiredService<PostgresModelArtifactRegistry>());
        services.AddSingleton<IPersistentModelArtifactRegistry>(sp => sp.GetRequiredService<PostgresModelArtifactRegistry>());

        // Desired Model State Store 持久化（PostgreSQL）。
        // 存储 HA 集群中各模型的期望状态（Active/Inactive），由各节点的 ReconcilerWorker 定期拉取并应用。
        services.AddSingleton<PostgresDesiredModelStateStore>();
        services.AddSingleton<IDesiredModelStateStore>(sp => sp.GetRequiredService<PostgresDesiredModelStateStore>());

        // Cluster Model Slot Store 持久化（PostgreSQL）—— 单一 Champion 真相源。
        // 单行表 cluster_model_slots 通过 CAS（Revision）保证原子模型切换，替代 per-model DesiredModelState。
        // 控制面端点（activate/rollback/retire）通过 CAS 更新此 slot，Reconciler 从此 slot 同步期望状态。
        services.AddSingleton<PostgresClusterModelSlotStore>();
        services.AddSingleton<IClusterModelSlotStore>(sp => sp.GetRequiredService<PostgresClusterModelSlotStore>());

        // Model Node Applied State 持久化（PostgreSQL）。
        // 记录各节点最后成功应用的集群槽位 Revision 与模型内容，供 Reconciler 重启后查询
        // 本节点上次应用了什么（审计 / 漂移分析）；Upsert 通过 AppliedRevision CAS 防陈旧回写。
        services.AddSingleton<PostgresModelNodeAppliedStateStore>();
        services.AddSingleton<IModelNodeAppliedStateStore>(sp => sp.GetRequiredService<PostgresModelNodeAppliedStateStore>());

        // Model Node Membership 持久化（PostgreSQL）。
        // 节点成员资格租约：租约过期即 stale cutoff，集群 Rollout Ready 基于活跃成员
        // 而非历史 Applied State 行；serving_enabled 供 Admission/Middleware 阻断 Isolated 节点流量。
        services.AddSingleton<PostgresModelNodeMembershipStore>();
        services.AddSingleton<IModelNodeMembershipStore>(sp => sp.GetRequiredService<PostgresModelNodeMembershipStore>());

        // Model Activation Audit 持久化（PostgreSQL）。
        // 替代 FileSystem / InMemory provider 下的 InMemory 默认实现，让 HA 场景下
        // 模型生命周期审计记录（Activate / Rollback / Retire / Shadow 等）可跨进程持久化与查询。
        // Service API 端点 /api/models/{id}/audit 通过 IModelActivationAuditStore.ListByModelAsync 查询历史。
        services.AddSingleton<PostgresModelActivationAuditStore>();
        services.AddSingleton<IModelActivationAuditStore>(sp => sp.GetRequiredService<PostgresModelActivationAuditStore>());

        // Evolution Pipeline 持久化（run state + 3 audit tables）。
        // 替代 InMemory 默认注册，让 HA 场景下 pipeline run state / canary / rollback / baseline 审计记录可跨进程持久化。
        services.AddSingleton<PostgresPipelineRunStore>();
        services.AddSingleton<IPipelineRunStore>(sp => sp.GetRequiredService<PostgresPipelineRunStore>());

        // Postgres Policy Registry 持久化（bundle 注册 + activation CAS 激活）。
        // 替代 InMemory DefaultPolicyRegistry，让 HA 场景下 policy activation 可跨进程持久化 + CAS。
        services.AddSingleton<PostgresPolicyRegistry>();
        services.AddSingleton<IPolicyRegistry>(sp => sp.GetRequiredService<PostgresPolicyRegistry>());

        // PackageBuildTraceStore
        services.AddSingleton<PostgresContextPackageBuildTraceStore>();
        services.AddSingleton<IContextPackageBuildTraceStore>(sp =>
            sp.GetRequiredService<PostgresContextPackageBuildTraceStore>());

        // PackagePolicyStore
        services.AddSingleton<PostgresContextPackagePolicyStore>();
        services.AddSingleton<IContextPackagePolicyStore>(sp =>
            sp.GetRequiredService<PostgresContextPackagePolicyStore>());

        // JobQueue + JobQueryStore + LeasedJobQueue ()
        services.AddSingleton<PostgresContextJobQueue>();
        services.AddSingleton<IContextJobQueue>(sp => sp.GetRequiredService<PostgresContextJobQueue>());
        services.AddSingleton<IContextJobQueryStore>(sp => sp.GetRequiredService<PostgresContextJobQueue>());
        // 注册 ILeasedJobQueue 让 worker 检测到此队列支持租约语义。
        // worker 通过 `queue is ILeasedJobQueue` 判断；InMemory/File 队列不实现此接口故走 Dequeue 路径。
        services.AddSingleton<ILeasedJobQueue>(sp => sp.GetRequiredService<PostgresContextJobQueue>());

        // PostgresContextEventSink
        services.AddSingleton<PostgresContextEventSink>();
        services.AddSingleton<IContextEventSink>(sp => sp.GetRequiredService<PostgresContextEventSink>());

        // / Durable Memory Governance 持久化（UtilityLedger + ConflictSet durable projection）。
        // 读 API（IUtilityLedgerStore / IConflictSetStore）+ 写 API（IUtilityLedger / IConflictSetLedger）
        // 均绑定到同一 singleton；UtilityLedgerMaterializer 通过 IUtilityLedger / IConflictSetLedger
        // 异步批量写入，无需感知存储后端。无需失效 Decorator（读路径未接入缓存）。
        services.AddSingleton<PostgresUtilityLedgerStore>();
        services.AddSingleton<IUtilityLedgerStore>(sp => sp.GetRequiredService<PostgresUtilityLedgerStore>());
        services.AddSingleton<IUtilityLedger>(sp => sp.GetRequiredService<PostgresUtilityLedgerStore>());
        services.AddSingleton<PostgresConflictSetStore>();
        services.AddSingleton<IConflictSetStore>(sp => sp.GetRequiredService<PostgresConflictSetStore>());
        services.AddSingleton<IConflictSetLedger>(sp => sp.GetRequiredService<PostgresConflictSetStore>());

        // User Feedback Ledger 持久化（用户显式反馈接入：thumbs up/down + 评分修正 + 文本反馈）。
        // Postgres 实现做关联校验（EXISTS 子查询验证 (decision_id, candidate_item_id) 在 utility_ledger_entries 中存在）。
        // Service API 端点 POST /api/utility-ledger/feedback 通过 IUserFeedbackLedger.AppendFeedbackAsync 写入。
        services.AddSingleton<PostgresUserFeedbackLedgerStore>();
        services.AddSingleton<IUserFeedbackLedger>(sp => sp.GetRequiredService<PostgresUserFeedbackLedgerStore>());

        // Learning Loop Durable Outbox 持久化（PostgreSQL）。
        // 替代 fire-and-forget Task.Run 物化路径：Decision committed → learning_event_outbox 表 →
        // LearningMaterializationWorker 后台轮询 + bounded batch worker → MaterializeAsync → Ack/Retry/DeadLetter。
        // 仅 Postgres provider 注册此接口；FileSystem/InMemory 不注册——
        // LearningMaterializationDispatcher 检测到 null 时回退到 in-memory bounded Channel（非持久但消除 Task.Run）。
        services.AddSingleton<PostgresLearningEventOutboxStore>();
        services.AddSingleton<ILearningEventOutboxStore>(sp => sp.GetRequiredService<PostgresLearningEventOutboxStore>());

        // Agent Run 状态机 + 事件流哈希链持久化（PostgreSQL）。
        // 替代 InMemory 默认实现，让 HA 场景下 Agent Run 元数据 + 审计事件流可跨进程持久化与崩溃恢复。
        // - IAgentRunStore / IAgentRunEventStore：让 AgentRunActor / AgentKernelHost 自动注入持久化实现；
        // - IPersistentAgentRunStore / IPersistentAgentRunEventStore：标记接口以显式区分持久化能力。
        services.AddSingleton<PostgresAgentRunStore>();
        services.AddSingleton<IAgentRunStore>(sp => sp.GetRequiredService<PostgresAgentRunStore>());
        services.AddSingleton<IPersistentAgentRunStore>(sp => sp.GetRequiredService<PostgresAgentRunStore>());
        // 2c：注入可选 IAgentRunEventNotifier（SSE push 通道）；未注册时为 null，回退 500ms 轮询。
        services.AddSingleton<PostgresAgentRunEventStore>(sp => new PostgresAgentRunEventStore(
            sp.GetRequiredService<PostgresConnectionFactory>(),
            sp.GetRequiredService<PostgresJsonSerializer>(),
            sp.GetRequiredService<PostgresMigrationRunner>(),
            sp.GetService<IAgentRunEventNotifier>()));
        services.AddSingleton<IAgentRunEventStore>(sp => sp.GetRequiredService<PostgresAgentRunEventStore>());
        services.AddSingleton<IPersistentAgentRunEventStore>(sp => sp.GetRequiredService<PostgresAgentRunEventStore>());
        // Agent Run 事件流快照与压缩（Event Snapshot & Compaction）。
        // 将 Run 事件流前缀折叠为快照并归档折叠事件，控制长生命周期 Run 热表无界增长；
        // 仅 Postgres provider 注册——Service 端点检测 null 时返回 503（不可用）。
        services.AddSingleton<PostgresAgentRunEventCompactor>();
        services.AddSingleton<IAgentRunEventCompactor>(sp => sp.GetRequiredService<PostgresAgentRunEventCompactor>());

        // 运行时能力补齐：durable approval + HA Run Owner Lease 持久化（PostgreSQL）。
        // - IAgentApprovalStore：让 DefaultAgentApprovalGate 自动注入持久化实现，
        // 审批状态（Pending/Approved/Rejected）跨进程持久化，崩溃恢复后可重新加载未决审批。
        // - IAgentRunLease：让 AgentKernelHost 自动注入持久化租约实现，
        // 确保同一时刻仅一个 Host 实例处理同一 Run（复用 canary_leader_leases 模式）。
        services.AddSingleton<PostgresAgentApprovalStore>();
        services.AddSingleton<IAgentApprovalStore>(sp => sp.GetRequiredService<PostgresAgentApprovalStore>());
        services.AddSingleton<IPersistentAgentApprovalStore>(sp => sp.GetRequiredService<PostgresAgentApprovalStore>());
        services.AddSingleton<PostgresAgentRunLease>();
        services.AddSingleton<IAgentRunLease>(sp => sp.GetRequiredService<PostgresAgentRunLease>());

        // Tool Reconciliation Control Plane（-B1）：对账记录 PostgreSQL 持久化。
        // 替代 InMemoryToolReconciliationStore 成为 ProductionHA 组合根下的真相源：
        // 多实例 ToolReconciliationWorker / 人工 resolve 端点共享同一数据库，
        // 杜绝"对账记录只在创建它的实例内存中"导致的裁决丢失。
        // 注册顺序在 AddContextCore() 的 TryAddSingleton 之前 → Postgres 实现胜出。
        services.AddSingleton<PostgresToolReconciliationStore>();
        services.AddSingleton<IToolReconciliationStore>(sp => sp.GetRequiredService<PostgresToolReconciliationStore>());

        // Workspace 配额持久化（PostgreSQL）。
        // 替代 AddContextCoreSecurity 的 InMemoryWorkspaceQuotaService，让多实例部署下
        // 配额真相源落在数据库（ledger + reservations 跨节点共享，重启不丢失）。
        // 未配置 workspace 的默认上限：组合根（ProductionRuntimeExtensions）按配置覆盖注册；
        // 此处注册默认无限制（0 = 无限制），保证服务始终可解析。
        services.AddSingleton<PostgresWorkspaceQuotaService>(sp => new PostgresWorkspaceQuotaService(
            sp.GetRequiredService<PostgresConnectionFactory>(),
            sp.GetRequiredService<PostgresJsonSerializer>(),
            sp.GetRequiredService<PostgresMigrationRunner>()));
        services.AddSingleton<IWorkspaceQuotaService>(sp => sp.GetRequiredService<PostgresWorkspaceQuotaService>());

        // Run 终态结算 outbox（PostgreSQL）：终态结算 worker 消费（Actualize / Release）。
        services.AddSingleton<PostgresTerminalRunSettlementStore>();
        services.AddSingleton<ITerminalRunSettlementStore>(sp => sp.GetRequiredService<PostgresTerminalRunSettlementStore>());

        // 注册 ILeasedWorkStore 用于 Agent Run 租约（统一租约基础设施 — dual-registration）。
        // 与 IAgentRunLease 共享同一底层表（agent_run_leases），使用统一的 ILeasedWorkStore 接口。
        // 消费方（AgentKernelHost）暂不改用 ILeasedWorkStore，先通过 dual-registration 证明语义覆盖。
        services.AddLeasedWorkStore<string>(new LeasedWorkStoreConfiguration<string>
        {
            TableName = Infrastructure.PostgresNames.Table(options, "agent_run_leases"),
            WorkIdColumn = "run_id",
            LeaseTokenColumn = "lease_token",
            LeaseOwnerColumn = "owner",
            LeaseExpiresAtColumn = "lease_expires_at",
            FencingTokenColumn = "fencing_token",
            AcquiredAtColumn = "acquired_at",
            IsLeaderLease = true,
            SerializeWork = work => work,
            DeserializeWork = workId => workId
        });

        // Canary HA 聚合 + Leader 租约持久化（PostgreSQL）。
        // 替代单节点 InMemory 默认实现，让 HA 场景下 Canary 指标可跨实例聚合 + Leader 选举确保单 leader 推进。
        // - ICanaryLeaderLease：CanaryLeaderHostedService 通过 TryAcquireAsync/RenewAsync/ReleaseAsync
        // 竞争 per-run leader 租约（复用 / 租约模式，但状态机简化为"持有/未持有"两态）。
        // - ICanaryMetricsAggregator：各实例将本地 CanaryObservationMetrics 快照写入 canary_metrics_samples 表，
        // leader 实例通过 SQL SUM/AVG/MAX 合并跨实例视图，产出 CanaryAggregatedMetrics 供 CanaryProgressionService 评估。
        // - ICanaryDecisionApplier（Perf-7）：将 lease/fencing 校验 + pipeline revision CAS + transition audit
        // 写入 + epoch 递增合并为单一 PostgreSQL 事务，修复旧路径 AdvanceAsync → AdvanceEpochAsync 分两步
        // 导致的 HA 正确性问题。由 PostgresCanaryLeaderLease 同时实现（共享 lease 表与连接工厂）。
        // 注意：CanaryLeaderHostedService 自身在 ContextCore.Service 项目中注册（依赖方向约束），
        // Storage.Postgres 不引用 Service；调用方应在 Service 层调用
        // <c>AddCanaryLeaderHostedService()</c>（若已提供）或 <c>services.AddHostedService&lt;CanaryLeaderHostedService&gt;()</c>
        // 并配置 <see cref="CanaryLeaderOptions"/>（Enabled=true 启用 HA 模式）。
        services.AddSingleton<PostgresCanaryLeaderLease>();
        services.AddSingleton<ICanaryLeaderLease>(sp => sp.GetRequiredService<PostgresCanaryLeaderLease>());
        services.AddSingleton<ICanaryDecisionApplier>(sp => sp.GetRequiredService<PostgresCanaryLeaderLease>());
        // 集群级 Canary Kill Switch 存储（PostgreSQL 持久化）。
        // 先于 CoreExtensions 的 TryAddSingleton 默认实现注册，确保 HA 模式下路由层与
        // CanaryProgressionService 恢复逻辑读取集群共享的紧急覆盖。
        services.AddSingleton<PostgresCanaryEmergencyOverrideStore>();
        services.AddSingleton<ICanaryEmergencyOverrideStore>(sp => sp.GetRequiredService<PostgresCanaryEmergencyOverrideStore>());
        // 自适应检索规划器反馈存储（PostgreSQL 持久化）。
        // 先于 CoreExtensions 的 TryAddSingleton 默认实现注册，确保自适应策略跨实例共享反馈历史。
        services.AddSingleton<PostgresRetrievalPlanFeedbackStore>();
        services.AddSingleton<IRetrievalPlanFeedbackStore>(sp => sp.GetRequiredService<PostgresRetrievalPlanFeedbackStore>());

        // 注册 ILeasedWorkStore 用于 Canary Leader 租约（统一租约基础设施）。
        // 与 ICanaryLeaderLease 共享同一底层表（canary_leader_leases），但使用统一的 ILeasedWorkStore 接口。
        services.AddLeasedWorkStore<string>(new LeasedWorkStoreConfiguration<string>
        {
            TableName = Infrastructure.PostgresNames.Table(options, "canary_leader_leases"),
            WorkIdColumn = "run_id",
            LeaseTokenColumn = "lease_token",
            LeaseOwnerColumn = "owner",
            LeaseExpiresAtColumn = "lease_expires_at",
            FencingTokenColumn = "fencing_token",
            AcquiredAtColumn = "acquired_at",
            IsLeaderLease = true,
            SerializeWork = work => work,
            DeserializeWork = workId => workId
        });
        services.AddSingleton<PostgresCanaryMetricsAggregator>();
        services.AddSingleton<ICanaryMetricsAggregator>(sp => sp.GetRequiredService<PostgresCanaryMetricsAggregator>());

        // Postgres 备份/恢复 + PITR 执行器。
        // 注册为 Transient 因为它们持有 PostgresConnectionFactory（内含 NpgsqlDataSource），
        // 需要随调用方释放；CLI 与 AdminEndpoints 通过 IAsyncDisposable 模式使用。
        services.AddTransient<PostgresBackupRunner>();
        services.AddTransient<PostgresPitrRunner>();

        return services;
    }

    /// <summary>
    /// 注册一个通用 PostgreSQL 租约工作存储（<see cref="PostgresLeasedWorkStore{TWork}"/>）。
    /// </summary>
    /// <typeparam name="TWork">工作项类型。</typeparam>
    /// <param name="services">服务容器。</param>
    /// <param name="configuration">表/列映射配置。</param>
    /// <remarks>
    /// 注册以下绑定：
    /// <list type="bullet">
    /// <item><see cref="LeasedWorkStoreConfiguration{TWork}"/> — 单例（供 store 构造注入）。</item>
    /// <item><see cref="PostgresLeasedWorkStore{TWork}"/> — 单例。</item>
    /// <item><see cref="IPostgresLeasedWorkStore{TWork}"/> — 指向同一单例（含 <c>ExecuteFencedAsync</c>）。</item>
    /// <item><see cref="ILeasedWorkStore{TWork, LeasedWork{TWork}}"/> — 指向同一单例（provider-agnostic 接口）。</item>
    /// <item><see cref="ILeasedWorkStore"/> — 指向同一单例（非泛型标记，供 <c>IEnumerable&lt;ILeasedWorkStore&gt;</c> 枚举）。</item>
    /// </list>
    /// 可多次调用以注册不同 <typeparamref name="TWork"/> 的租约存储。
    /// </remarks>
    public static IServiceCollection AddLeasedWorkStore<TWork>(
        this IServiceCollection services,
        LeasedWorkStoreConfiguration<TWork> configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(configuration);
        services.AddSingleton<PostgresLeasedWorkStore<TWork>>();
        // 接口绑定使用 TryAdd：同一 TWork 的租约存储可注册多次（不同底层表，如 Agent Run 租约与
        // Canary Leader 租约），接口只绑定首个实例（DI 无法用同型泛型接口区分多个存储）。
        services.TryAddSingleton<IPostgresLeasedWorkStore<TWork>>(sp => sp.GetRequiredService<PostgresLeasedWorkStore<TWork>>());
        services.TryAddSingleton<ILeasedWorkStore<TWork, LeasedWork<TWork>>>(sp => sp.GetRequiredService<PostgresLeasedWorkStore<TWork>>());
        services.TryAddSingleton<ILeasedWorkStore>(sp => sp.GetRequiredService<PostgresLeasedWorkStore<TWork>>());
        return services;
    }
}
