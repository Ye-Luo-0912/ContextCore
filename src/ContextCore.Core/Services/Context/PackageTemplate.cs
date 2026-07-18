using System.Collections.Immutable;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 不可变的包构建模板：缓存命中时安全复用的构建结果。
/// 不包含 PackageId / BuildId / CreatedAt / 响应 metadata（这些在每次投影时重新生成），
/// 只包含 sections、选择结果、预算报告等确定性数据。
/// R13.0 #3: 所有集合字段使用 <see cref="ImmutableArray{T}"/>，编译期与运行期均不可变，
/// 无法通过 cast 回可变数组污染缓存。投影阶段仍做防御性拷贝以保证返回结果的独立性。
/// </summary>
internal sealed record PackageTemplate(
    ImmutableArray<ContextPackageSection> OrderedSections,
    ImmutableArray<string> SourceRefs,
    int EstimatedTokens,
    int TokenBudget,
    ImmutableArray<ContextPackageDecision> SortedSelectedItems,
    ImmutableArray<DroppedContextItem> DroppedItems,
    ImmutableArray<ContextPackageUncertainty> Uncertainties,
    ImmutableArray<ContextPackageItemReference> ItemReferences,
    ImmutableArray<ContextAnchor> Anchors,
    RetrievalPlan? RetrievalPlan,
    ContextPackageBudgetReport Budget,
    ContextPackageStandardOutput Output,
    ModeBudgetProfile? ModeBudgetProfile,
    PackageReadPlan? ReadPlan = null);
