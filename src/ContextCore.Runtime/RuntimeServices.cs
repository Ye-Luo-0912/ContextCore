using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Runtime;

/// <summary>
/// 组装后的运行时主链服务对象图。承载规划/关系扩展/包构建/检索器/晋升的核心产出。
/// 中间服务暴露为公共属性，供 Service DI 容器注册后由 host-specific 服务消费。
/// </summary>
public sealed class RuntimeServices
{
    // --- 顶层主链产出 ---

    public required BasicContextPackageBuilder PackageBuilder { get; init; }
    public required HybridContextRetriever Retriever { get; init; }
    public required BasicMemoryPromotionService PromotionService { get; init; }

    // --- 规划子链中间服务 ---

    public required PlanningIntentDetector PlanningIntentDetector { get; init; }

    // --- 关系扩展子链中间服务 ---

    public required RelationExpansionProfileRegistry RelationExpansionProfileRegistry { get; init; }
    public required RelationExpansionPolicyValidator RelationExpansionPolicyValidator { get; init; }
    public required RelationTraversalEngine RelationTraversalEngine { get; init; }
    public required RelationExpansionPreviewService RelationExpansionPreviewService { get; init; }
    public required GraphExpansionApplyPolicy GraphExpansionApplyPolicy { get; init; }

    // --- 注意力 ---

    public required RuleBasedContextAttentionScorer AttentionScorer { get; init; }

    // --- shadow trace builders（依赖主链中间服务，由 builder 内部构造）---

    public required GraphExpansionShadowTraceBuilder GraphExpansionShadowTraceBuilder { get; init; }
}
