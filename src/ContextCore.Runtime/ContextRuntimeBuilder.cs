using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Runtime;

/// <summary>
/// 统一运行时主链组装器。消除 Service / ControlRoom / Evaluation 三套 composition 的重复组装代码。
/// 各宿主通过 <see cref="Build"/> 获取主链服务对象图，
/// 自身只负责提供 store provider、配置与 observability sinks，不再自行 new 主链服务。
/// Standard profile（ControlRoom / Evaluation）仅填充 required 项；
/// Full profile（Service）额外填充 shadow/trace sinks 以启用生产可观测性。
/// </summary>
public static class ContextRuntimeBuilder
{
    /// <summary>
    /// 从存储依赖组装运行时主链（规划/关系扩展/包构建/检索器/晋升）。
    /// IShortTermMemoryStore 和 IContextLearningStore 由调用方通过 options 传入，确保与宿主 DI 容器实例一致。
    /// Full profile sinks（trace stores / shadow builders）在 options 中提供时传入构造函数，否则保持 null。
    /// </summary>
    public static RuntimeServices Build(RuntimeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 规则型意图检测（保留：被 Learning 子系统引用）
        var planningIntentDetector = new PlanningIntentDetector();

        // 关系扩展主链
        var relationTypeRegistry = new RelationTypeRegistry();
        var relationExpansionProfileRegistry = new RelationExpansionProfileRegistry();
        var relationExpansionValidator = new RelationExpansionPolicyValidator(relationTypeRegistry);
        var relationTraversalEngine = new RelationTraversalEngine(options.RelationStore);
        var relationExpansionPreviewService = new RelationExpansionPreviewService(
            relationTraversalEngine,
            relationExpansionProfileRegistry,
            relationExpansionValidator);
        var graphExpansionApplyPolicy = new GraphExpansionApplyPolicy(
            relationExpansionPreviewService,
            options.ContextStore,
            options.MemoryStore,
            options.ConstraintStore);

        // 晋升/注意力
        var promotionService = new BasicMemoryPromotionService(options.MemoryStore, options.PromotionRecordStore);
        var attentionScorer = options.AttentionProfile is null && options.AttentionLearningStore is null
            ? new RuleBasedContextAttentionScorer()
            : new RuleBasedContextAttentionScorer(
                options.AttentionProfile ?? ContextAttentionProfile.CreateDefaultShadowV1(),
                options.AttentionLearningStore);

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
            graphExpansionApplyOptions: options.GraphExpansionApplyOptions,
            graphExpansionApplyPolicy: graphExpansionApplyPolicy,
            decisionTraceStore: options.DecisionTraceStore,
            runtimeCandidateTraceSink: options.RuntimeCandidateTraceSink,
            traversalEngine: relationTraversalEngine);

        // 检索器（Full profile 传入 shadow experiments / ranker / graph shadow / decision trace）
        // GraphExpansionShadowTraceBuilder 依赖 RelationExpansionPreviewService，在此内部构造避免 DI 循环。
        var graphExpansionShadowTraceBuilder = new GraphExpansionShadowTraceBuilder(relationExpansionPreviewService);
        var retriever = new HybridContextRetriever(
            options.ContextStore,
            options.MemoryStore,
            options.RelationStore,
            options.EmbeddingProvider,
            options.VectorStore,
            options.RetrievalTraceStore,
            attentionScorer,
            attentionProfileExperiments: options.AttentionProfileExperiments,
            attentionLearningStore: options.AttentionLearningStore,
            attentionRerankOptions: options.AttentionRerankOptions,
            rankerShadowOptions: options.LifecycleAwareRankerShadowOptions,
            rankerShadowTraceBuilder: options.LifecycleAwareRankerTraceBuilder,
            graphExpansionShadowOptions: options.GraphExpansionShadowOptions,
            graphExpansionShadowTraceBuilder: graphExpansionShadowTraceBuilder,
            decisionTraceStore: options.DecisionTraceStore);

        return new RuntimeServices
        {
            PackageBuilder = packageBuilder,
            Retriever = retriever,
            PromotionService = promotionService,
            PlanningIntentDetector = planningIntentDetector,
            RelationExpansionProfileRegistry = relationExpansionProfileRegistry,
            RelationExpansionPolicyValidator = relationExpansionValidator,
            RelationTraversalEngine = relationTraversalEngine,
            RelationExpansionPreviewService = relationExpansionPreviewService,
            GraphExpansionApplyPolicy = graphExpansionApplyPolicy,
            AttentionScorer = attentionScorer,
            GraphExpansionShadowTraceBuilder = graphExpansionShadowTraceBuilder
        };
    }
}
