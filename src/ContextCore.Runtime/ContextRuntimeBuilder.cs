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

namespace ContextCore.Runtime;

/// <summary>
/// 统一运行时主链组装器。消除 ControlRoom RuntimeBuilder 与 Evaluation EvalStateFactory 之间的重复组装代码。
/// 各宿主（ControlRoom / Evaluation / 集成测试）通过 <see cref="Build"/> 获取主链服务对象图，
/// 自身只负责提供 store provider、配置与 observability sinks，不再自行 new 主链服务。
/// Service DI 路径目前仍由 ASP.NET DI 容器解析（参数集为 Full profile 超集），后续收敛至此 builder。
/// </summary>
public static class ContextRuntimeBuilder
{
    /// <summary>
    /// 从存储依赖组装运行时主链（规划/关系扩展/包构建/检索器/晋升）。
    /// IShortTermMemoryStore 和 IContextLearningStore 使用 InMemory 实现，与原各宿主行为一致。
    /// </summary>
    public static RuntimeServices Build(RuntimeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 规划主链
        var planningSnapshotService = new PlanningSnapshotService(
            new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            options.MemoryStore,
            options.ConstraintStore,
            new InMemoryContextLearningStore());
        var planningSafetyProfile = RetrievalPlanSafetyProfile.CreateDefault();
        var planningProposalService = new RetrievalPlanProposalService(
            planningSnapshotService,
            new PlanningIntentDetector(),
            planningSafetyProfile);
        var planningValidator = new RetrievalPlanProposalValidator(planningSafetyProfile);
        var planningShadowExecutor = new ShadowRetrievalPlanExecutor(
            options.ContextStore,
            options.MemoryStore,
            options.RelationStore,
            planningValidator,
            options.ConstraintStore);

        // 关系扩展主链
        var relationExpansionProfileRegistry = new RelationExpansionProfileRegistry();
        var relationExpansionValidator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var relationExpansionPreviewService = new RelationExpansionPreviewService(
            new RelationTraversalEngine(options.RelationStore),
            relationExpansionProfileRegistry,
            relationExpansionValidator);
        var graphExpansionApplyPolicy = new GraphExpansionApplyPolicy(
            relationExpansionPreviewService,
            options.ContextStore,
            options.MemoryStore,
            options.ConstraintStore);

        // 晋升/注意力/包构建/检索器
        var promotionService = new BasicMemoryPromotionService(options.MemoryStore, options.PromotionRecordStore);
        var attentionScorer = new RuleBasedContextAttentionScorer();
        var packageBuilder = new BasicContextPackageBuilder(
            options.ContextStore,
            options.ConstraintStore,
            options.GlobalContextStore,
            options.MemoryStore,
            options.RelationStore,
            tokenizerResolver: options.TokenizerResolver,
            workingMemoryService: options.WorkingMemoryService,
            graphExpansionApplyOptions: options.GraphExpansionApplyOptions,
            graphExpansionApplyPolicy: graphExpansionApplyPolicy);
        var retriever = new HybridContextRetriever(
            options.ContextStore,
            options.MemoryStore,
            options.RelationStore,
            options.EmbeddingProvider,
            options.VectorStore,
            options.RetrievalTraceStore,
            attentionScorer,
            attentionRerankOptions: options.AttentionRerankOptions,
            planningOptions: options.RetrievalPlanningOptions,
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
