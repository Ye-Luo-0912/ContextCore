using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 结果投影阶段：消费 <see cref="SelectionResult"/>，构建不可变 <see cref="PackageTemplate"/>（缓存），
/// 以及从模板投影为 <see cref="ContextPackageBuildResult"/>（每次请求重新生成 ID/时间/metadata）。
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
    /// 模板投影：从 SelectionResult 构建不可变 PackageTemplate（可安全缓存复用）。
    /// 排序 sections、排序 selected items、构建 budget 和 output。
    /// 不包含 PackageId / BuildId / CreatedAt / 响应 metadata。
    /// </summary>
    internal PackageTemplate ProjectTemplate(
        SelectionResult selection,
        ResolvedPackageOptions options)
    {
        var request = options.Request;
        var policy = options.Policy;
        var tokenBudget = options.TokenBudget;
        var modeBudgetProfile = options.ModeBudgetProfile;
        var packageModeName = options.PackageModeName;
        var packageMustHitIds = options.PackageMustHitIds;

        var orderedSections = PackageSectionBudgetResolver.OrderSections(selection.Sections, policy);

        var sortedSelected = selection.SelectedItems
            .OrderByDescending(item => PackageUncertaintyBuilder.ResolvePackageOrderScore(item, packageModeName, packageMustHitIds))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolvedUncertainties = selection.Uncertainties;

        // 构建临时 package 仅用于 budget/output 计算（PackageId/CreatedAt 不影响 budget/output）
        var tempPackage = new ContextPackage
        {
            Sections = orderedSections,
            EstimatedTokens = selection.EstimatedTokens,
        };

        var budget = PackageBudgetProjector.BuildBudgetReport(tempPackage, tokenBudget, request);
        var output = PackageBudgetProjector.BuildStandardOutput(tempPackage, selection.DroppedItems, resolvedUncertainties, budget);

        return new PackageTemplate(
            OrderedSections: orderedSections,
            SourceRefs: selection.SourceRefs.ToArray(),
            EstimatedTokens: selection.EstimatedTokens,
            TokenBudget: tokenBudget,
            SortedSelectedItems: sortedSelected,
            DroppedItems: selection.DroppedItems,
            Uncertainties: resolvedUncertainties,
            ItemReferences: selection.ItemReferences,
            Anchors: selection.Anchors,
            RetrievalPlan: selection.RetrievalPlan,
            Budget: budget,
            Output: output,
            ModeBudgetProfile: modeBudgetProfile);
    }

    /// <summary>
    /// 结果投影：从 PackageTemplate 和请求生成 ContextPackageBuildResult。
    /// 每次调用生成新的 PackageId / BuildId / CreatedAt / 响应 metadata，
    /// 缓存命中时安全复用模板数据。
    /// </summary>
    /// <param name="packageTraceWriteFailures">package trace store 累积写入失败次数（fail-open 指标）。</param>
    /// <param name="decisionTraceWriteFailures">decision trace store 累积写入失败次数（fail-open 指标）。</param>
    internal ContextPackageBuildResult ProjectResult(
        PackageTemplate template,
        ResolvedPackageOptions options,
        int packageTraceWriteFailures = 0,
        int decisionTraceWriteFailures = 0)
    {
        var request = options.Request;
        var policy = options.Policy;
        var tokenContext = options.TokenContext;
        var tokenBudget = template.TokenBudget;
        var modeBudgetProfile = template.ModeBudgetProfile;
        var workspaceId = options.WorkspaceId;
        var collectionId = options.CollectionId;

        var metadata = PackageMetadataBuilder.CreatePackageMetadata(request, tokenContext);
        if (!string.IsNullOrWhiteSpace(policy.Id))
        {
            metadata["policyId"] = policy.Id;
        }
        ContextItemRefResolver.AddAnchorMetadata(metadata, template.Anchors);
        PackageMetadataBuilder.AddModeBudgetMetadata(metadata, modeBudgetProfile);
        PackageMetadataBuilder.AddDiagnosticMetadata(metadata, tokenBudget, template.EstimatedTokens, template.DroppedItems.Count, template.Uncertainties.Count);

        var now = DateTimeOffset.UtcNow;
        var package = new ContextPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId ?? string.Empty,
            Sections = template.OrderedSections,
            EstimatedTokens = template.EstimatedTokens,
            SourceRefs = template.SourceRefs,
            Metadata = metadata,
            CreatedAt = now
        };

        // policyId 双重设置：request.Policy?.Id 覆盖 resolved policy.Id（与原实现一致）
        var resultMetadata = new Dictionary<string, string>(metadata);
        if (!string.IsNullOrWhiteSpace(request.Policy?.Id))
        {
            resultMetadata["policyId"] = request.Policy.Id;
        }
        PackageMetadataBuilder.AddTraceHealthMetadata(resultMetadata, _traceRecorder);
        PackageMetadataBuilder.AddTraceStoreHealthMetadata(resultMetadata, packageTraceWriteFailures, decisionTraceWriteFailures);

        return new ContextPackageBuildResult
        {
            BuildId = package.PackageId,
            Package = package,
            SelectedItems = template.SortedSelectedItems,
            ItemReferences = template.ItemReferences,
            DroppedItems = template.DroppedItems,
            Uncertainties = template.Uncertainties,
            Budget = template.Budget,
            Output = template.Output,
            TokenBudget = tokenBudget == int.MaxValue ? 0 : tokenBudget,
            EstimatedTokens = template.EstimatedTokens,
            Metadata = resultMetadata,
            Plan = template.RetrievalPlan,
            CreatedAt = now
        };
    }
}
