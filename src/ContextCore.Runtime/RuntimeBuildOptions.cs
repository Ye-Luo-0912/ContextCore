using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Runtime;

/// <summary>
/// 运行时组装输入，提供存储依赖和可选配置。
/// 由各宿主（ControlRoom / Evaluation / Service / 集成测试）填充，<see cref="ContextRuntimeBuilder"/> 据此组装主链服务。
/// Standard profile（ControlRoom / Evaluation）仅填充 required 项；
/// Full profile（Service）额外填充 trace sinks 以启用生产可观测性。
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

    // --- optional: Full profile trace sinks（Service 生产路径）---

    /// <summary>包构建 trace 存储；为 null 则不记录包构建 trace。</summary>
    public IContextPackageBuildTraceStore? PackageBuildTraceStore { get; init; }

    /// <summary>决策 trace 存储；为 null 则不记录决策 trace。</summary>
    public IDecisionTraceStore? DecisionTraceStore { get; init; }

    /// <summary>运行时候选 trace sink；为 null 则使用 NullRuntimeCandidateTraceSink。</summary>
    public IRuntimeCandidateTraceSink? RuntimeCandidateTraceSink { get; init; }
}
