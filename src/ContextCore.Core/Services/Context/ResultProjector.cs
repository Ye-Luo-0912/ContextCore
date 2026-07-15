using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 结果投影阶段：消费 <see cref="SelectionResult"/>，构建 metadata、排序 sections、
/// 构造 <see cref="ContextPackage"/> 与 <see cref="ContextPackageBuildResult"/>。
/// 从 <see cref="BasicContextPackageBuilder"/> 提取 metadata 装配与 CreateBuildResult 逻辑，
/// 保持 policyId 双重设置语义（resolved policy 与 request.Policy）与 selected 排序不变。
/// </summary>
internal sealed class ResultProjector
{
    private readonly PackageTraceRecorder _traceRecorder;

    internal ResultProjector(PackageTraceRecorder traceRecorder)
    {
        _traceRecorder = traceRecorder;
    }

    /// <summary>
    /// 投影阶段：组装 metadata、按 policy 排序 sections、构造 package 与 build result。
    /// </summary>
    internal ContextPackageBuildResult ProjectResult(
        SelectionResult selection,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        TokenEstimationContext tokenContext)
    {
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        var collectionId = PackagePolicyResolver.NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
        var modeBudgetProfile = PackagePolicyResolver.ResolveModeBudgetProfile(request, policy);
        var tokenBudget = PackagePolicyResolver.ResolveTokenBudget(request, policy, modeBudgetProfile);

        var metadata = PackageMetadataBuilder.CreatePackageMetadata(request, tokenContext);
        if (!string.IsNullOrWhiteSpace(policy.Id))
        {
            metadata["policyId"] = policy.Id;
        }
        ContextItemRefResolver.AddAnchorMetadata(metadata, selection.Anchors);
        PackageMetadataBuilder.AddModeBudgetMetadata(metadata, modeBudgetProfile);
        PackageMetadataBuilder.AddDiagnosticMetadata(metadata, tokenBudget, selection.EstimatedTokens, selection.DroppedItems.Count, selection.Uncertainties.Count);

        var orderedSections = PackageSectionBudgetResolver.OrderSections(selection.Sections, policy);

        var package = new ContextPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId ?? string.Empty,
            Sections = orderedSections,
            EstimatedTokens = selection.EstimatedTokens,
            SourceRefs = selection.SourceRefs.ToArray(),
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return CreateBuildResult(
            request,
            package,
            tokenBudget,
            selection.SelectedItems,
            selection.DroppedItems,
            selection.Uncertainties,
            selection.RetrievalPlan,
            _traceRecorder,
            selection.ItemReferences);
    }

    private static ContextPackageBuildResult CreateBuildResult(
        ContextPackageRequest request,
        ContextPackage package,
        int tokenBudget,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageUncertainty>? uncertainties = null,
        RetrievalPlan? plan = null,
        PackageTraceRecorder? traceRecorder = null,
        IReadOnlyList<ContextPackageItemReference>? itemReferences = null)
    {
        var metadata = new Dictionary<string, string>(package.Metadata);
        if (!string.IsNullOrWhiteSpace(request.Policy?.Id))
        {
            metadata["policyId"] = request.Policy.Id;
        }

        var packageModeName = request.Policy is null
            ? PackagePolicyResolver.ReadFirstSetting(request, new ContextPackagePolicy(), "mode", "packageMode", "contextMode", "taskMode") ?? string.Empty
            : PackagePolicyResolver.ResolvePackageModeName(request, request.Policy, PackagePolicyResolver.ResolveModeBudgetProfile(request, request.Policy));
        var packageMustHitIds = PackagePolicyResolver.ResolvePackageMustHitIds(request);
        var sortedSelected = selectedItems
            .OrderByDescending(item => PackageUncertaintyBuilder.ResolvePackageOrderScore(item, packageModeName, packageMustHitIds))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolvedUncertainties = uncertainties ?? PackageUncertaintyBuilder.BuildUncertainties(
            package.Sections,
            sortedSelected,
            droppedItems,
            Array.Empty<ContextRelation>(),
            tokenBudget,
            package.EstimatedTokens);
        var budget = PackageBudgetProjector.BuildBudgetReport(package, tokenBudget, request);
        var output = PackageBudgetProjector.BuildStandardOutput(package, droppedItems, resolvedUncertainties, budget);
        PackageMetadataBuilder.AddDiagnosticMetadata(metadata, tokenBudget, package.EstimatedTokens, droppedItems.Count, resolvedUncertainties.Count);
        if (traceRecorder is not null)
        {
            PackageMetadataBuilder.AddTraceHealthMetadata(metadata, traceRecorder);
        }

        return new ContextPackageBuildResult
        {
            BuildId = package.PackageId,
            Package = package,
            SelectedItems = sortedSelected,
            ItemReferences = itemReferences ?? Array.Empty<ContextPackageItemReference>(),
            DroppedItems = droppedItems,
            Uncertainties = resolvedUncertainties,
            Budget = budget,
            Output = output,
            TokenBudget = tokenBudget == int.MaxValue ? 0 : tokenBudget,
            EstimatedTokens = package.EstimatedTokens,
            Metadata = metadata,
            Plan = plan,
            CreatedAt = package.CreatedAt
        };
    }
}
