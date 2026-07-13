using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Runtime;

/// <summary>
/// 运行时组装输入，提供存储依赖和可选配置。
/// 由各宿主（ControlRoom / Evaluation / Service / 集成测试）填充，<see cref="ContextRuntimeBuilder"/> 据此组装主链服务。
/// Standard profile（ControlRoom / Evaluation）仅填充 required 项；
/// Full profile（Service）额外填充 shadow/trace sinks 以启用生产可观测性。
/// </summary>
public sealed class RuntimeBuildOptions
{
    // --- required: 所有 profile 共有 ---

    public required IContextStore ContextStore { get; init; }
    public required IMemoryStore MemoryStore { get; init; }
    public required IConstraintStore ConstraintStore { get; init; }
    public required IRelationStore RelationStore { get; init; }
    public required IGlobalContextStore GlobalContextStore { get; init; }
    public required IVectorStore VectorStore { get; init; }
    public IEmbeddingProvider? EmbeddingProvider { get; init; }
    public required IRetrievalTraceStore RetrievalTraceStore { get; init; }
    public required IContextTokenizerResolver TokenizerResolver { get; init; }
    public required IPromotionRecordStore PromotionRecordStore { get; init; }
    public required IWorkingMemoryService WorkingMemoryService { get; init; }
    public required IShortTermMemoryStore ShortTermMemoryStore { get; init; }
    public required IContextLearningStore LearningStore { get; init; }

    // --- optional: 配置 ---

    public GraphExpansionApplyOptions? GraphExpansionApplyOptions { get; init; }
    public RetrievalAttentionRerankOptions? AttentionRerankOptions { get; init; }

    // --- optional: Full profile shadow/trace sinks（Service 生产路径）---

    /// <summary>包构建 trace 存储；为 null 则不记录包构建 trace。</summary>
    public IContextPackageBuildTraceStore? PackageBuildTraceStore { get; init; }

    /// <summary>决策 trace 存储；为 null 则不记录决策 trace。</summary>
    public IDecisionTraceStore? DecisionTraceStore { get; init; }

    /// <summary>运行时候选 trace sink；为 null 则使用 NullRuntimeCandidateTraceSink。</summary>
    public IRuntimeCandidateTraceSink? RuntimeCandidateTraceSink { get; init; }

    /// <summary>注意力实验 profile 集合；为 null 则不启用实验。</summary>
    public IEnumerable<ContextAttentionProfile>? AttentionProfileExperiments { get; init; }

    /// <summary>注意力学习存储；为 null 则不启用注意力学习。</summary>
    public IContextLearningStore? AttentionLearningStore { get; init; }

    /// <summary>注意力 profile（单例）；为 null 则 scorer 使用内部默认。</summary>
    public ContextAttentionProfile? AttentionProfile { get; init; }

    /// <summary>Ranker shadow 选项；为 null 则使用默认。</summary>
    public LifecycleAwareRankerShadowOptions? LifecycleAwareRankerShadowOptions { get; init; }

    /// <summary>Ranker shadow trace builder；为 null 则不启用 ranker shadow trace。</summary>
    public LifecycleAwareRankerTraceBuilder? LifecycleAwareRankerTraceBuilder { get; init; }

    /// <summary>图扩展 shadow 选项；为 null 则使用默认。</summary>
    public GraphExpansionShadowOptions? GraphExpansionShadowOptions { get; init; }

    // 注：GraphExpansionShadowTraceBuilder 不作为 options 传入，因为它依赖主链中间服务
    // RelationExpansionPreviewService。由 ContextRuntimeBuilder 内部构造，通过 RuntimeServices 暴露。
}
