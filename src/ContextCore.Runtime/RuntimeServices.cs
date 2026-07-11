using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Runtime;

/// <summary>
/// 组装后的运行时主链服务对象图。承载规划/关系扩展/包构建/检索器/晋升的核心产出。
/// </summary>
public sealed class RuntimeServices
{
    public required BasicContextPackageBuilder PackageBuilder { get; init; }
    public required HybridContextRetriever Retriever { get; init; }
    public required BasicMemoryPromotionService PromotionService { get; init; }
}
