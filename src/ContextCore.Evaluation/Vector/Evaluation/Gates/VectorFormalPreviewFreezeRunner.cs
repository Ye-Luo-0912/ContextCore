using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Formal preview freeze；只冻结 preview-only 许可，不改变正式检索或 package 输出。
/// </summary>
public sealed class VectorFormalPreviewFreezeRunner
{
    public VectorFormalPreviewFreezeReport BuildGate(
        VectorV4ReadinessRecheckReport? v4Recheck,
        GuardedFormalRetrievalPreviewReport? guardedFormalPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageComparisonGate,
        ScopedFormalPreviewOptInReport? scopedFormalPreviewOptInGate,
        LimitedFormalPreviewObservationReport? limitedFormalPreviewObservationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        IReadOnlyDictionary<string, string>? sourceReports = null)
    {
        var blocked = new List<string>();
        if (v4Recheck is null
            || !v4Recheck.RecheckPassed
            || !string.Equals(v4Recheck.Recommendation, VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorV4ReadinessRecheckNotPassed");
        }

        if (guardedFormalPreviewGate is null
            || !guardedFormalPreviewGate.GatePassed
            || !string.Equals(guardedFormalPreviewGate.Recommendation, GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("GuardedFormalRetrievalPreviewGateNotPassed");
        }

        if (shadowPackageComparisonGate is null
            || !shadowPackageComparisonGate.GatePassed
            || !string.Equals(shadowPackageComparisonGate.Recommendation, VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorShadowPackageComparisonGateNotPassed");
        }

        if (scopedFormalPreviewOptInGate is null
            || !scopedFormalPreviewOptInGate.GatePassed
            || !string.Equals(scopedFormalPreviewOptInGate.Recommendation, ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ScopedFormalPreviewOptInGateNotPassed");
        }

        if (limitedFormalPreviewObservationGate is null
            || !limitedFormalPreviewObservationGate.GatePassed
            || !string.Equals(limitedFormalPreviewObservationGate.Recommendation, LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("LimitedFormalPreviewObservationGateNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        var riskAfterPolicy = Max(
            v4Recheck?.RiskAfterPolicy ?? 0,
            guardedFormalPreviewGate?.RiskAfterPolicy ?? 0,
            shadowPackageComparisonGate?.RiskAfterPolicy ?? 0,
            scopedFormalPreviewOptInGate?.RiskAfterPolicy ?? 0,
            limitedFormalPreviewObservationGate?.RiskAfterPolicy ?? 0);
        var mustNotRisk = Max(
            v4Recheck?.MustNotHitRiskAfterPolicy ?? 0,
            guardedFormalPreviewGate?.MustNotHitRiskAfterPolicy ?? 0,
            shadowPackageComparisonGate?.MustNotHitRiskAfterPolicy ?? 0,
            scopedFormalPreviewOptInGate?.MustNotHitRiskAfterPolicy ?? 0,
            limitedFormalPreviewObservationGate?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Max(
            v4Recheck?.LifecycleRiskAfterPolicy ?? 0,
            guardedFormalPreviewGate?.LifecycleRiskAfterPolicy ?? 0,
            shadowPackageComparisonGate?.LifecycleRiskAfterPolicy ?? 0,
            scopedFormalPreviewOptInGate?.LifecycleRiskAfterPolicy ?? 0,
            limitedFormalPreviewObservationGate?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = Max(
            v4Recheck?.FormalOutputChanged ?? 0,
            guardedFormalPreviewGate?.FormalOutputChanged ?? 0,
            shadowPackageComparisonGate?.FormalOutputChanged ?? 0,
            scopedFormalPreviewOptInGate?.FormalOutputChanged ?? 0,
            limitedFormalPreviewObservationGate?.FormalOutputChanged ?? 0);
        var packageOutputChanged = (guardedFormalPreviewGate?.PackageOutputChanged ?? false)
            || (shadowPackageComparisonGate?.PackageOutputChanged ?? false)
            || (scopedFormalPreviewOptInGate?.PackageOutputChanged ?? false)
            || (limitedFormalPreviewObservationGate?.PackageOutputChanged ?? false);
        var packingPolicyChanged = (guardedFormalPreviewGate?.PackingPolicyChanged ?? false)
            || (shadowPackageComparisonGate?.PackingPolicyChanged ?? false)
            || (scopedFormalPreviewOptInGate?.PackingPolicyChanged ?? false)
            || (limitedFormalPreviewObservationGate?.PackingPolicyChanged ?? false);
        var formalPackageWritten = (scopedFormalPreviewOptInGate?.FormalPackageWritten ?? false)
            || (limitedFormalPreviewObservationGate?.FormalPackageWritten ?? false);
        var runtimeMutated = (shadowPackageComparisonGate?.RuntimeMutated ?? false)
            || (scopedFormalPreviewOptInGate?.RuntimeMutated ?? false)
            || (limitedFormalPreviewObservationGate?.RuntimeMutated ?? false);
        var nonAllowlistedScopeLeakCount = Math.Max(
            scopedFormalPreviewOptInGate?.NonAllowlistedScopeLeakCount ?? 0,
            limitedFormalPreviewObservationGate?.NonAllowlistedScopeLeakCount ?? 0);

        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (formalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (packageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (packingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (formalPackageWritten)
        {
            blocked.Add("FormalPackageWritten");
        }

        if (runtimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (nonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var freezePassed = distinctBlocked.Length == 0;
        return new VectorFormalPreviewFreezeReport
        {
            OperationId = $"vector-formal-preview-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            VectorFormalPreview = freezePassed
                ? VectorFormalPreviewFreezeStatuses.ReadyForScopedOptInPreview
                : VectorFormalPreviewFreezeStatuses.KeepPreviewOnly,
            AllowedMode = "ScopedPreviewOnly",
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            RuntimeSwitchAllowed = false,
            V4ReadinessRecheckPassed = v4Recheck?.RecheckPassed ?? false,
            GuardedFormalPreviewGatePassed = guardedFormalPreviewGate?.GatePassed ?? false,
            ShadowPackageComparisonGatePassed = shadowPackageComparisonGate?.GatePassed ?? false,
            ScopedFormalPreviewOptInGatePassed = scopedFormalPreviewOptInGate?.GatePassed ?? false,
            LimitedFormalPreviewObservationGatePassed = limitedFormalPreviewObservationGate?.GatePassed ?? false,
            RuntimeChangeReadinessGatePassed = runtimeChangeGate?.Passed ?? false,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            ForbiddenChanges =
            [
                "RuntimeSwitch",
                "FormalPackageWrite",
                "PackingPolicyIntegration",
                "PackageOutputMutation",
                "NonAllowlistedScopeUse"
            ],
            Recommendation = freezePassed
                ? VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview
                : ResolveRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string BuildMarkdown(VectorFormalPreviewFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector Formal Preview Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- VectorFormalPreview: `{report.VectorFormalPreview}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- V4ReadinessRecheckPassed: `{report.V4ReadinessRecheckPassed}`");
        builder.AppendLine($"- GuardedFormalPreviewGatePassed: `{report.GuardedFormalPreviewGatePassed}`");
        builder.AppendLine($"- ShadowPackageComparisonGatePassed: `{report.ShadowPackageComparisonGatePassed}`");
        builder.AppendLine($"- ScopedFormalPreviewOptInGatePassed: `{report.ScopedFormalPreviewOptInGatePassed}`");
        builder.AppendLine($"- LimitedFormalPreviewObservationGatePassed: `{report.LimitedFormalPreviewObservationGatePassed}`");
        builder.AppendLine($"- RuntimeChangeReadinessGatePassed: `{report.RuntimeChangeReadinessGatePassed}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        AppendList(builder, "Forbidden Changes", report.ForbiddenChanges);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- V4.F freeze 通过后只允许 scoped preview opt-in preview，不允许 runtime switch。");
        builder.AppendLine("- 不写正式 package，不绑定正式 `IVectorIndexStore`，不改变 `PackingPolicy` 或 package output。");
        builder.AppendLine("- 非 allowlisted scope 不允许使用 formal preview。");
        return builder.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("RuntimeChange", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByRuntimeChangeGate;
        }

        if (blocked.Any(static reason => reason.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByFormalPackageWrite;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Any(static reason => reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Recheck", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorFormalPreviewFreezeRecommendations.BlockedByMissingGate;
        }

        return VectorFormalPreviewFreezeRecommendations.KeepPreviewOnly;
    }

    private static int Max(params int[] values) => values.Length == 0 ? 0 : values.Max();

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }
}
