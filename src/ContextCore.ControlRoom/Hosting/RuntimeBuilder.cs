using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Hosting;

/// <summary>
/// P3-03：ControlRoom Direct Mode 运行时对象图共用组装层。
/// 消除 CreateState 中 memory 与 filesystem 分支之间约 50 行重复的规划/关系/包构建/检索器组装代码。
/// Service DI 路径使用 ASP.NET DI 容器解析，构造函数参数不同，不使用此层。
/// </summary>
public static class RuntimeBuilder
{
    /// <summary>
    /// 从存储依赖组装运行时主链（规划/关系扩展/包构建/检索器/晋升）。
    /// IShortTermMemoryStore 和 IContextLearningStore 使用 InMemory 实现，与原 CreateState 行为一致。
    /// </summary>
    public static RuntimeServices BuildCoreServices(RuntimeBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 规划主链
        var planningSnapshotService = new PlanningSnapshotService(
            new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            context.MemoryStore,
            context.ConstraintStore,
            new InMemoryContextLearningStore());
        var planningSafetyProfile = RetrievalPlanSafetyProfile.CreateDefault();
        var planningProposalService = new RetrievalPlanProposalService(
            planningSnapshotService,
            new PlanningIntentDetector(),
            planningSafetyProfile);
        var planningValidator = new RetrievalPlanProposalValidator(planningSafetyProfile);
        var planningShadowExecutor = new ShadowRetrievalPlanExecutor(
            context.ContextStore,
            context.MemoryStore,
            context.RelationStore,
            planningValidator,
            context.ConstraintStore);

        // 关系扩展主链
        var relationExpansionProfileRegistry = new RelationExpansionProfileRegistry();
        var relationExpansionValidator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var relationExpansionPreviewService = new RelationExpansionPreviewService(
            new RelationTraversalEngine(context.RelationStore),
            relationExpansionProfileRegistry,
            relationExpansionValidator);
        var graphExpansionApplyPolicy = new GraphExpansionApplyPolicy(
            relationExpansionPreviewService,
            context.ContextStore,
            context.MemoryStore,
            context.ConstraintStore);

        // 晋升/注意力/包构建/检索器
        var promotionService = new BasicMemoryPromotionService(context.MemoryStore, context.PromotionRecordStore);
        var attentionScorer = new RuleBasedContextAttentionScorer();
        var packageBuilder = new BasicContextPackageBuilder(
            context.ContextStore,
            context.ConstraintStore,
            context.GlobalContextStore,
            context.MemoryStore,
            context.RelationStore,
            tokenizerResolver: context.TokenizerResolver,
            workingMemoryService: context.WorkingMemoryService,
            graphExpansionApplyOptions: context.GraphExpansionApplyOptions,
            graphExpansionApplyPolicy: graphExpansionApplyPolicy);
        var retriever = new HybridContextRetriever(
            context.ContextStore,
            context.MemoryStore,
            context.RelationStore,
            context.EmbeddingProvider,
            context.VectorStore,
            context.RetrievalTraceStore,
            attentionScorer,
            attentionRerankOptions: context.AttentionRerankOptions,
            planningOptions: context.RetrievalPlanningOptions,
            planningProposalService: planningProposalService,
            planningShadowExecutor: planningShadowExecutor);

        return new RuntimeServices
        {
            PackageBuilder = packageBuilder,
            Retriever = retriever,
            PromotionService = promotionService
        };
    }
}

/// <summary>运行时组装上下文，提供存储依赖和可选配置。</summary>
public sealed class RuntimeBuildContext
{
    public required IContextStore ContextStore { get; init; }
    public required IMemoryStore MemoryStore { get; init; }
    public required IConstraintStore ConstraintStore { get; init; }
    public required IRelationStore RelationStore { get; init; }
    public required IGlobalContextStore GlobalContextStore { get; init; }
    public required IVectorStore VectorStore { get; init; }
    public required IEmbeddingProvider EmbeddingProvider { get; init; }
    public required IRetrievalTraceStore RetrievalTraceStore { get; init; }
    public required IContextTokenizerResolver TokenizerResolver { get; init; }
    public required IPromotionRecordStore PromotionRecordStore { get; init; }
    public required IWorkingMemoryService WorkingMemoryService { get; init; }
    public GraphExpansionApplyOptions? GraphExpansionApplyOptions { get; init; }
    public RetrievalAttentionRerankOptions? AttentionRerankOptions { get; init; }
    public RetrievalPlanningOptions? RetrievalPlanningOptions { get; init; }
}

/// <summary>组装后的运行时服务对象图。</summary>
public sealed class RuntimeServices
{
    public required BasicContextPackageBuilder PackageBuilder { get; init; }
    public required HybridContextRetriever Retriever { get; init; }
    public required BasicMemoryPromotionService PromotionService { get; init; }
}
