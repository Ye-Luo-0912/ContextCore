using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 构建上下文包的元数据（diagnostics、trace health、graph expansion、mode budget）。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class PackageMetadataBuilder
{
    internal static ContextPackage CreatePackage(
        ContextPackageRequest request,
        string collectionId,
        IReadOnlyList<ContextPackageSection> sections,
        IEnumerable<string> sourceRefs,
        int estimatedTokens,
        TokenEstimationContext tokenContext)
    {
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        return new ContextPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Sections = sections,
            EstimatedTokens = estimatedTokens,
            SourceRefs = sourceRefs.ToArray(),
            Metadata = CreatePackageMetadata(request, tokenContext),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    internal static Dictionary<string, string> CreatePackageMetadata(
        ContextPackageRequest request,
        TokenEstimationContext tokenContext)
    {
        var metadata = new Dictionary<string, string>(request.Metadata)
        {
            [ContextTokenizationMetadataKeys.Source] = tokenContext.Source,
            [ContextTokenizationMetadataKeys.Model] = tokenContext.ModelName ?? string.Empty,
            [ContextTokenizationMetadataKeys.IsFallback] = tokenContext.IsFallback ? "true" : "false"
        };

        return metadata;
    }

    internal static void AddDiagnosticMetadata(
        IDictionary<string, string> metadata,
        int tokenBudget,
        int estimatedTokens,
        int droppedItemCount,
        int uncertaintyCount)
    {
        var normalizedBudget = LegacyPackageScorer.NormalizeTokenBudget(tokenBudget);
        metadata["diagnostics.droppedItems"] = droppedItemCount.ToString();
        metadata["diagnostics.uncertainties"] = uncertaintyCount.ToString();
        metadata["budget.tokenBudget"] = normalizedBudget.ToString();
        metadata["budget.usedTokens"] = estimatedTokens.ToString();
        metadata["budget.remainingTokens"] = normalizedBudget > 0
            ? Math.Max(0, normalizedBudget - estimatedTokens).ToString()
            : "0";
        metadata["budget.usageRatio"] = normalizedBudget > 0
            ? Math.Clamp((double)estimatedTokens / normalizedBudget, 0, 1).ToString("0.###")
            : "0";
    }

    internal static void AddTraceHealthMetadata(
        IDictionary<string, string> metadata,
        PackageTraceRecorder traceRecorder)
    {
        metadata["traceWriteFailures"] = traceRecorder.TraceWriteFailures.ToString();
        metadata["traceMapFailures"] = traceRecorder.TraceMapFailures.ToString();
        metadata["traceSinkWriteFailures"] = traceRecorder.TraceSinkWriteFailures.ToString();
    }

    internal static void AddModeBudgetMetadata(
        IDictionary<string, string> metadata,
        ModeBudgetProfile? modeBudgetProfile)
    {
        if (modeBudgetProfile is null)
        {
            return;
        }

        metadata["budget.mode"] = modeBudgetProfile.ModeName;
        metadata["budget.modeDefaultTokenBudget"] = modeBudgetProfile.DefaultTokenBudget.ToString();
    }
}
