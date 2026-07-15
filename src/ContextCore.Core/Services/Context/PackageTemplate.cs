using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 不可变的包构建模板：缓存命中时安全复用的构建结果。
/// 不包含 PackageId / BuildId / CreatedAt / 响应 metadata（这些在每次投影时重新生成），
/// 只包含 sections、选择结果、预算报告等确定性数据。
/// </summary>
internal sealed record PackageTemplate(
    IReadOnlyList<ContextPackageSection> OrderedSections,
    IReadOnlyList<string> SourceRefs,
    int EstimatedTokens,
    int TokenBudget,
    IReadOnlyList<ContextPackageDecision> SortedSelectedItems,
    IReadOnlyList<DroppedContextItem> DroppedItems,
    IReadOnlyList<ContextPackageUncertainty> Uncertainties,
    IReadOnlyList<ContextPackageItemReference> ItemReferences,
    IReadOnlyList<ContextAnchor> Anchors,
    RetrievalPlan? RetrievalPlan,
    ContextPackageBudgetReport Budget,
    ContextPackageStandardOutput Output,
    ModeBudgetProfile? ModeBudgetProfile);
