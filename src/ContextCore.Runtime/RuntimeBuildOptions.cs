using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Runtime;

/// <summary>
/// 运行时组装输入，提供存储依赖和可选配置。
/// 由各宿主（ControlRoom / Evaluation / 集成测试）填充，<see cref="ContextRuntimeBuilder"/> 据此组装主链服务。
/// </summary>
public sealed class RuntimeBuildOptions
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
