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

    internal static void AddGraphExpansionMetadata(
        IDictionary<string, string> metadata,
        GraphExpansionSectionContribution contribution)
    {
        metadata["graphExpansionMode"] = contribution.Mode;
        metadata["graphExpansionApplied"] = contribution.Applied ? "true" : "false";
        metadata["graphExpansionProfiles"] = string.Join(",", contribution.Profiles);
        metadata["graphExpansionAddedItems"] = string.Join(",", contribution.AddedItems
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        metadata["graphExpansionTargetSections"] = string.Join(",", contribution.TargetSections
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        metadata["graphExpansionFallbackUsed"] = contribution.FallbackUsed ? "true" : "false";
        metadata["graphExpansionFallbackReason"] = contribution.FallbackReason;
        metadata["graphExpansionRiskChecks"] =
            $"riskAfterRouting={contribution.RiskChecks.RiskAfterRoutingCount};" +
            $"wrongSection={contribution.RiskChecks.WrongSectionRiskCount};" +
            $"mustNotHit={contribution.RiskChecks.MustNotHitRiskCount};" +
            $"lifecycle={contribution.RiskChecks.LifecycleRiskCount};" +
            $"missingEvidence={contribution.RiskChecks.MissingEvidenceCount}";
        metadata["graphExpansionSource"] = contribution.Applied
            ? "graph-expansion-apply"
            : string.Empty;
        metadata["graphExpansionAddedItemCount"] = contribution.AddedItems.Count.ToString();
        metadata["graphExpansionAddedAuditContextItems"] = contribution.AddedItems
            .Count(item => string.Equals(item.TargetSection, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase))
            .ToString();
        metadata["graphExpansionAddedConflictEvidenceItems"] = contribution.AddedItems
            .Count(item => string.Equals(item.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase))
            .ToString();
        metadata["graphExpansionExpectedGraphSectionDelta"] = contribution.Applied
            && !contribution.RiskChecks.HasRisk
            && contribution.AddedItems.All(item => IsExpectedGraphExpansionSection(item.TargetSection))
            ? contribution.AddedItems.Count.ToString()
            : "0";
        metadata["graphExpansionUnexpectedWarningDelta"] = contribution.FallbackUsed || contribution.RiskChecks.HasRisk
            ? "1"
            : "0";
        metadata["graphExpansionWarnings"] = string.Join("|", contribution.Warnings);
    }

    internal static bool IsExpectedGraphExpansionSection(string section)
    {
        return string.Equals(section, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase);
    }

    internal static int ResolveGraphExpansionSectionPriority(string sectionName)
    {
        return sectionName switch
        {
            GraphExpansionTargetSection.AuditContext => 18,
            GraphExpansionTargetSection.ConflictEvidence => 18,
            GraphExpansionTargetSection.HistoricalContext => 16,
            GraphExpansionTargetSection.DiagnosticsOnly => 8,
            _ => 5
        };
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
