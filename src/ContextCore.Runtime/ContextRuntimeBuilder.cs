using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Runtime;

/// <summary>
/// 统一运行时主链组装器。消除 Service / ControlRoom / Evaluation 三套 composition 的重复组装代码。
/// 各宿主通过 <see cref="Build"/> 获取主链服务对象图，
/// 自身只负责提供 store provider、配置与 observability sinks，不再自行 new 主链服务。
/// Standard profile（ControlRoom / Evaluation）仅填充 required 项；
/// Full profile（Service）额外填充 trace sinks 以启用生产可观测性。
/// </summary>
public static class ContextRuntimeBuilder
{
    /// <summary>
    /// 从存储依赖组装运行时主链（规划/关系扩展/包构建/检索器/晋升）。
    /// Full profile sinks（trace stores）在 options 中提供时传入构造函数，否则保持 null。
    /// </summary>
    public static RuntimeServices Build(RuntimeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 关系扩展主链
        // 优先使用 options 注入的 RelationTypeRegistry（Service DI singleton），
        // 缺省时回退到本地 new，保持 ControlRoom / Evaluation 路径无需显式提供。
        var relationTypeRegistry = options.RelationTypeRegistry ?? new RelationTypeRegistry();
        var relationExpansionProfileRegistry = new RelationExpansionProfileRegistry();
        var relationExpansionValidator = new RelationExpansionPolicyValidator(relationTypeRegistry);
        var relationTraversalEngine = new RelationTraversalEngine(options.RelationStore);
        var relationExpansionPreviewService = new RelationExpansionPreviewService(
            relationTraversalEngine,
            relationExpansionProfileRegistry,
            relationExpansionValidator);

        // 晋升
        var promotionService = new BasicMemoryPromotionService(options.MemoryStore, options.PromotionRecordStore);

        // 包构建（Full profile 传入 trace stores / traversal engine）
        var packageBuilder = new BasicContextPackageBuilder(
            options.ContextStore,
            options.ConstraintStore,
            options.GlobalContextStore,
            options.MemoryStore,
            options.RelationStore,
            traceStore: options.PackageBuildTraceStore,
            tokenizerResolver: options.TokenizerResolver,
            workingMemoryService: options.WorkingMemoryService,
            decisionTraceStore: options.DecisionTraceStore,
            runtimeCandidateTraceSink: options.RuntimeCandidateTraceSink,
            traversalEngine: relationTraversalEngine,
            cacheAccessor: options.CacheAccessor);

        // 检索器（Full profile 传入 trace stores / decision trace）
        // 优先使用 capabilities 派生 fanout；为 null 时由 HybridContextRetriever 回退到 namespace 推断
        RetrievalFanoutOptions? fanoutOptions = null;
        if (options.Capabilities is { } capabilities)
        {
            fanoutOptions = RetrievalFanoutOptions.FromProfile(capabilities.Profile);
        }
        var retriever = new HybridContextRetriever(
            options.ContextStore,
            options.MemoryStore,
            options.RelationStore,
            options.EmbeddingProvider,
            options.VectorStore,
            options.RetrievalTraceStore,
            decisionTraceStore: options.DecisionTraceStore,
            fanoutOptions: fanoutOptions,
            tokenizerResolver: options.TokenizerResolver);

        return new RuntimeServices
        {
            PackageBuilder = packageBuilder,
            Retriever = retriever,
            PromotionService = promotionService,
            RelationExpansionProfileRegistry = relationExpansionProfileRegistry,
            RelationExpansionPolicyValidator = relationExpansionValidator,
            RelationTraversalEngine = relationTraversalEngine,
            RelationExpansionPreviewService = relationExpansionPreviewService
        };
    }
}
