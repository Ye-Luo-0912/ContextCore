using ContextCore.Abstractions;
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

        // Learning feedback provider 仅注册 concrete 类型；默认运行时仍由 FileSystem provider 作为 source of truth。
        services.AddSingleton<PostgresLearningFeedbackStore>();
        services.AddSingleton<PostgresLearningFeedbackReviewStore>();
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

        return services;
    }
}
