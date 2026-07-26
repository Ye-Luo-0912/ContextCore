using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.ControlRoom.Services;

/// <summary>ControlRoom 的运行模式：直接读本地存储，或通过 Service API 远程连接。</summary>
public enum ControlRoomMode
{
    Direct,
    Service
}

/// <summary>控制室运行时状态对象，持有当前工作区、集合及各存储层的服务引用。</summary>
public sealed class ControlRoomState
{
    public ControlRoomMode Mode { get; init; } = ControlRoomMode.Direct;

    public string WorkspaceId { get; init; } = "default";

    public string CollectionId { get; init; } = "test";

    public string StorageKind { get; init; } = "filesystem";

    /// <summary>
    /// R14-PG-10：Postgres 存储配置（仅在 StorageKind = "postgres" 时填充）。
    /// 当 ControlRoom 直接以 Postgres 模式启动时由 CreateState 设置；
    /// 由 BackupCommand pg-* 子命令消费，用于在不重复 CLI 注入连接串的情况下复用配置。
    /// </summary>
    public PostgresOptions? PostgresOptions { get; init; }

    public string RootPath { get; init; } = FileStorageOptions.DefaultRootPath;

    public string? ServiceBaseUrl { get; init; }

    public ContextCoreClient? ServiceClient { get; init; }

    // P5-4: Service Mode 不再创建本地运行时对象，这些属性在 Service Mode 下为 null。
    // Direct Mode（InMemory/FileSystem）会完整赋值；Service Mode 通过 ServiceClient 远程调用。
    public IContextStore? ContextStore { get; init; }

    public IContextIndex? Index { get; init; }

    public IMemoryStore? MemoryStore { get; init; }

    public IWorkingMemoryService? WorkingMemory { get; init; }

    public IConstraintStore? ConstraintStore { get; init; }

    public IRelationStore? RelationStore { get; init; }

    public IGlobalContextStore? GlobalContextStore { get; init; }

    public IContextJobQueue? JobQueue { get; init; }

    public IContextJobQueryStore? JobQueryStore { get; init; }

    public IMemoryPromotionService? PromotionService { get; init; }

    public IPromotionCandidateStore? PromotionCandidateStore { get; init; }

    public IContextPackageBuilder? PackageBuilder { get; init; }

    public IContextPackagePolicyStore? PackagePolicyStore { get; init; }

    public ILearningFeedbackStore? LearningFeedbackStore { get; init; }

    public ILearningFeedbackReviewStore? LearningFeedbackReviewStore { get; init; }

    public IContextTokenizerResolver TokenizerResolver { get; init; } = new DefaultContextTokenizerResolver();

    public IVectorStore? VectorStore { get; init; }

    public IEmbeddingProvider? EmbeddingProvider { get; init; }

    public IRetrievalTraceStore? RetrievalTraceStore { get; init; }

    public IContextRetriever? Retriever { get; init; }

    public ModelGatewayOptions ModelGatewayOptions { get; init; } = new();

    public IModelHealthService? ModelHealthService { get; init; }

    public IModelUsageLogStore? ModelUsageLogStore { get; init; }

    /// <summary>
    /// R29 WP-E-3：训练数据导出器（Direct 模式可用）。
    /// Service 模式下为 null（CLI 通过 Service API 远程调用，不走本地导出）。
    /// </summary>
    public ITrainingDataExporter? TrainingDataExporter { get; init; }

    /// <summary>
    /// R29 WP-E-4：校准数据导出器（Direct 模式可用）。
    /// Service 模式下为 null（CLI 通过 Service API 远程调用，不走本地导出）。
    /// </summary>
    public ICalibrationDataExporter? CalibrationDataExporter { get; init; }

    public ContextPackage? LastPackage { get; set; }

    public bool IsServiceMode => Mode == ControlRoomMode.Service;
}
