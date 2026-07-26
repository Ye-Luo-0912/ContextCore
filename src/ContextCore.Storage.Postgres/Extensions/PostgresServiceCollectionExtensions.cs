using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;

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

        // P0-3：注册 PostgreSQL 跨 store 写入事务作用域工厂。
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
        services.AddSingleton<PostgresMemoryStore>();
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
        // P1-5：关系写入 outbox 存储。仅 Postgres provider 注册——
        // FileSystem/InMemory 不注册，OutboxAwareRelationProjectionWriter 与 RelationReconciliationWorker
        // 检测到 null 时回退到无 outbox 路径（仅走 stale-edge 周期扫描）。
        services.AddSingleton<PostgresRelationOutboxStore>();
        services.AddSingleton<IRelationOutboxStore>(sp => sp.GetRequiredService<PostgresRelationOutboxStore>());

        // R14-PG-1：Learning feedback / review 接口正式绑定 Postgres 实现。
        // 此前 Service 层在 RegisterPostgres 中用 Unsupported*Store 覆盖了接口绑定，
        // 导致运行时即便 Postgres provider 已就绪也走 Unsupported 路径。现在移除覆盖，
        // 让 PostgresLearningFeedbackStore / PostgresLearningFeedbackReviewStore 成为 source of truth。
        services.AddSingleton<PostgresLearningFeedbackStore>();
        services.AddSingleton<ILearningFeedbackStore>(sp => sp.GetRequiredService<PostgresLearningFeedbackStore>());
        services.AddSingleton<PostgresLearningFeedbackReviewStore>();
        services.AddSingleton<ILearningFeedbackReviewStore>(sp => sp.GetRequiredService<PostgresLearningFeedbackReviewStore>());
        services.AddSingleton<PostgresLearningFeatureCandidateStore>();

        // ConstraintStore
        services.AddSingleton<PostgresConstraintStore>();
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

        // R14-PG-2：DecisionTraceStore。替代 Unsupported 占位，让 HA 场景下决策审计可持久化。
        services.AddSingleton<PostgresDecisionTraceStore>();
        services.AddSingleton<IDecisionTraceStore>(sp => sp.GetRequiredService<PostgresDecisionTraceStore>());

        // R14-PG-3：Short-term memory / promotion / candidate review stores。
        // 替代 Unsupported 占位，让 HA 场景下短期记忆与晋升审核可持久化。
        services.AddSingleton<PostgresShortTermMemoryStore>();
        services.AddSingleton<IShortTermMemoryStore>(sp => sp.GetRequiredService<PostgresShortTermMemoryStore>());
        services.AddSingleton<PostgresShortTermPromotionCandidateStore>();
        services.AddSingleton<IShortTermPromotionCandidateStore>(sp => sp.GetRequiredService<PostgresShortTermPromotionCandidateStore>());
        services.AddSingleton<PostgresCandidateMemoryReviewStore>();
        services.AddSingleton<ICandidateMemoryReviewStore>(sp => sp.GetRequiredService<PostgresCandidateMemoryReviewStore>());
        services.AddSingleton<PostgresStableReviewCandidateStore>();
        services.AddSingleton<IStableReviewCandidateStore>(sp => sp.GetRequiredService<PostgresStableReviewCandidateStore>());

        // R14-PG-4：Context learning / governance review stores。
        // 替代 Unsupported 占位，让 HA 场景下学习记录与生命周期审核可持久化。
        services.AddSingleton<PostgresContextLearningStore>();
        services.AddSingleton<IContextLearningStore>(sp => sp.GetRequiredService<PostgresContextLearningStore>());
        services.AddSingleton<PostgresStableLifecycleReviewStore>();
        services.AddSingleton<IStableLifecycleReviewStore>(sp => sp.GetRequiredService<PostgresStableLifecycleReviewStore>());
        services.AddSingleton<PostgresCandidateConstraintReviewStore>();
        services.AddSingleton<ICandidateConstraintReviewStore>(sp => sp.GetRequiredService<PostgresCandidateConstraintReviewStore>());
        services.AddSingleton<PostgresConstraintGapCandidateStore>();
        services.AddSingleton<IConstraintGapCandidateStore>(sp => sp.GetRequiredService<PostgresConstraintGapCandidateStore>());

        // R14-PG-5：vector lifecycle + artifact stores。
        // 替代 Unsupported 占位，完成 R14-PG 阶段一垂直闭环，Postgres 无 Unsupported store。
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

        // R14-PG-6：分布式 context state 版本存储。覆盖 CoreExtensions 的 InMemory 默认注册。
        // 多实例 Worker 通过 Postgres 行级锁共享单调递增的版本号，支持跨实例 cache invalidation。
        services.AddSingleton<PostgresContextStateVersionStore>();
        services.AddSingleton<IContextStateVersionStore>(sp => sp.GetRequiredService<PostgresContextStateVersionStore>());

        // R26-2：Agent Runtime 持久化（checkpoint + task state）。
        // 替代 InMemory 默认注册，让 HA 场景下 agent session/task 状态可跨进程持久化与恢复。
        services.AddSingleton<PostgresAgentCheckpointStore>();
        services.AddSingleton<IAgentCheckpointStore>(sp => sp.GetRequiredService<PostgresAgentCheckpointStore>());
        // R29 WP-B-3：显式注册 IPersistentAgentCheckpointStore 标记接口以区分持久化能力。
        // delta 链路（R28-G P1-5）完全由 DefaultAgentKernel.ResumeAsync 通过标准 GetAsync 走链，
        // Store 不需感知 delta 语义 — 只持久化完整 AgentCheckpoint blob。
        services.AddSingleton<IPersistentAgentCheckpointStore>(sp => sp.GetRequiredService<PostgresAgentCheckpointStore>());
        services.AddSingleton<PostgresAgentTaskStateStore>();
        services.AddSingleton<IAgentTaskStateStore>(sp => sp.GetRequiredService<PostgresAgentTaskStateStore>());

        // R29 WP-B-1：Tool Dispatch Journal 持久化（PostgreSQL）。
        // 替代（当前未注册的）InMemory 默认实现，让 HA 场景下 tool 调用状态机可跨进程持久化与崩溃恢复。
        // 注册为 IToolDispatchJournal 让 DefaultAgentKernel 的 nullable 参数自动注入；
        // 同时注册为 IPersistentToolDispatchJournal 以便显式区分持久化能力。
        services.AddSingleton<PostgresToolDispatchJournal>();
        services.AddSingleton<IToolDispatchJournal>(sp => sp.GetRequiredService<PostgresToolDispatchJournal>());
        services.AddSingleton<IPersistentToolDispatchJournal>(sp => sp.GetRequiredService<PostgresToolDispatchJournal>());

        // R29 WP-B-2：Kernel Result Outbox 持久化（PostgreSQL）。
        // 替代（当前未注册的）InMemory 默认实现，让 HA 场景下未投递的 AgentKernelResult 可跨进程持久化与崩溃恢复重放。
        // 注册为 IKernelResultOutbox 让 DefaultAgentKernel 的 nullable 参数自动注入；
        // 同时注册为 IPersistentKernelResultOutbox 以便显式区分持久化能力。
        services.AddSingleton<PostgresKernelResultOutbox>();
        services.AddSingleton<IKernelResultOutbox>(sp => sp.GetRequiredService<PostgresKernelResultOutbox>());
        services.AddSingleton<IPersistentKernelResultOutbox>(sp => sp.GetRequiredService<PostgresKernelResultOutbox>());

        // R29 WP-B-4：Durable Transport 持久化（PostgreSQL-backed Channel）。
        // 始终注册 PostgresDurableTransport 单例 + IDurableTransport 标记接口；
        // IAgentKernelTransport 绑定默认不替换（保留 InProcessTransport），
        // 由 UsePostgresDurableTransport() 扩展方法或 KernelTransportOptions.UseDurableTransport=true 显式启用。
        services.AddSingleton<PostgresDurableTransport>();
        services.AddSingleton<IDurableTransport>(sp => sp.GetRequiredService<PostgresDurableTransport>());

        // R29 WP-A-1：Model Artifact Registry 持久化（PostgreSQL）。
        // 替代（当前未注册的）InMemory 默认实现，让 HA 场景下模型工件描述符可跨进程持久化与查询。
        // 注册为 IModelArtifactRegistry 让消费方（如 OnnxInferenceEngine 启动加载器）自动注入；
        // 同时注册为 IPersistentModelArtifactRegistry 以便显式区分持久化能力。
        services.AddSingleton<PostgresModelArtifactRegistry>();
        services.AddSingleton<IModelArtifactRegistry>(sp => sp.GetRequiredService<PostgresModelArtifactRegistry>());
        services.AddSingleton<IPersistentModelArtifactRegistry>(sp => sp.GetRequiredService<PostgresModelArtifactRegistry>());

        // R27-3：Evolution Pipeline 持久化（run state + 3 audit tables）。
        // 替代 InMemory 默认注册，让 HA 场景下 pipeline run state / canary / rollback / baseline 审计记录可跨进程持久化。
        services.AddSingleton<PostgresPipelineRunStore>();
        services.AddSingleton<IPipelineRunStore>(sp => sp.GetRequiredService<PostgresPipelineRunStore>());

        // WS-A：Postgres Policy Registry 持久化（bundle 注册 + activation CAS 激活）。
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

        // JobQueue + JobQueryStore + LeasedJobQueue (P0-4)
        services.AddSingleton<PostgresContextJobQueue>();
        services.AddSingleton<IContextJobQueue>(sp => sp.GetRequiredService<PostgresContextJobQueue>());
        services.AddSingleton<IContextJobQueryStore>(sp => sp.GetRequiredService<PostgresContextJobQueue>());
        // P0-4：注册 ILeasedJobQueue 让 worker 检测到此队列支持租约语义。
        // worker 通过 `queue is ILeasedJobQueue` 判断；InMemory/File 队列不实现此接口故走 Dequeue 路径。
        services.AddSingleton<ILeasedJobQueue>(sp => sp.GetRequiredService<PostgresContextJobQueue>());

        // PostgresContextEventSink
        services.AddSingleton<PostgresContextEventSink>();
        services.AddSingleton<IContextEventSink>(sp => sp.GetRequiredService<PostgresContextEventSink>());

        // R28-E：Durable Memory Governance 持久化（UtilityLedger + ConflictSet durable projection）。
        // read-only 公共 API；写入由 UtilityLedgerMaterializer 通过 internal BulkInsertAsync 调用。
        // 无需失效 Decorator（读路径未接入缓存，与 IDecisionTraceStore / IRetrievalTraceStore 一致）。
        services.AddSingleton<PostgresUtilityLedgerStore>();
        services.AddSingleton<IUtilityLedgerStore>(sp => sp.GetRequiredService<PostgresUtilityLedgerStore>());
        services.AddSingleton<PostgresConflictSetStore>();
        services.AddSingleton<IConflictSetStore>(sp => sp.GetRequiredService<PostgresConflictSetStore>());

        // R14-PG-10：Postgres 备份/恢复 + PITR 执行器。
        // 注册为 Transient 因为它们持有 PostgresConnectionFactory（内含 NpgsqlDataSource），
        // 需要随调用方释放；CLI 与 AdminEndpoints 通过 IAsyncDisposable 模式使用。
        services.AddTransient<PostgresBackupRunner>();
        services.AddTransient<PostgresPitrRunner>();

        return services;
    }

    /// <summary>
    /// 注册 PostgreSQL 存储实现 + 可选的 Durable Transport 配置开关。
    /// 当 <paramref name="transportOptions"/>.<see cref="KernelTransportOptions.UseDurableTransport"/> = true 时，
    /// 替换 <see cref="IAgentKernelTransport"/> 绑定为 <see cref="PostgresDurableTransport"/>；
    /// 否则保留 <see cref="ContextCore.Core.Services.AgentKernel.InProcessTransport"/>（开发环境默认）。
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="options">PostgreSQL 配置。</param>
    /// <param name="transportOptions">Kernel transport 配置；为 null 时等价于 <see cref="KernelTransportOptions.Default"/>（不启用 durable transport）。</param>
    public static IServiceCollection AddContextCorePostgresStorage(
        this IServiceCollection services,
        PostgresOptions options,
        KernelTransportOptions? transportOptions)
    {
        services.AddContextCorePostgresStorage(options);

        if (transportOptions is { UseDurableTransport: true })
        {
            services.UsePostgresDurableTransport();
        }

        return services;
    }

    /// <summary>
    /// 显式启用 PostgreSQL Durable Transport：替换 <see cref="IAgentKernelTransport"/> 绑定为
    /// <see cref="PostgresDurableTransport"/>，让指令/结果跨进程持久化以支持 HA 崩溃恢复。
    /// </summary>
    /// <remarks>
    /// 必须在 <see cref="AddContextCorePostgresStorage(PostgresOptions)"/> 之后调用，
    /// 以确保 <see cref="PostgresDurableTransport"/> 单例已注册。
    /// 此扩展会移除 CoreExtensions 注册的 InProcessTransport 默认绑定并替换为持久化实现。
    /// 开发环境保留 InMemory（不调用本方法）以避免不必要的 DB 依赖。
    /// </remarks>
    public static IServiceCollection UsePostgresDurableTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 移除 CoreExtensions 注册的 IAgentKernelTransport → InProcessTransport 默认绑定
        // （TryAddSingleton 模式下若已注册则不会被覆盖；本方法显式替换）。
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IAgentKernelTransport))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton<IAgentKernelTransport>(sp => sp.GetRequiredService<PostgresDurableTransport>());
        return services;
    }
}
