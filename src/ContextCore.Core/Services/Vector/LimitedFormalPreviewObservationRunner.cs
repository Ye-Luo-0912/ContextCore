using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Limited formal preview observation；聚合 scoped preview-only 观测，不写正式 package。
/// </summary>
public sealed class LimitedFormalPreviewObservationRunner
{
    public LimitedFormalPreviewObservationReport BuildObservation(
        ScopedFormalPreviewOptInReport? scopedOptInGate,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LimitedFormalPreviewObservationOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("observation", scopedOptInGate, shadowPackageComparison, options, sourceReports);

    public LimitedFormalPreviewObservationReport BuildGate(
        ScopedFormalPreviewOptInReport? scopedOptInGate,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LimitedFormalPreviewObservationOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("gate", scopedOptInGate, shadowPackageComparison, options, sourceReports);

    public static string BuildMarkdown(string title, LimitedFormalPreviewObservationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- ObservationPassed: `{report.ObservationPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- ObservationRunCount: `{report.ObservationRunCount}`");
        builder.AppendLine($"- MinimumObservationRunCount: `{report.MinimumObservationRunCount}`");
        builder.AppendLine($"- PreviewPackageCount: `{report.PreviewPackageCount}`");
        builder.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        builder.AppendLine($"- CandidateAddCount: `{report.CandidateAddCount}`");
        builder.AppendLine($"- CandidateRemoveCount: `{report.CandidateRemoveCount}`");
        builder.AppendLine($"- CandidateUnchangedCount: `{report.CandidateUnchangedCount}`");
        builder.AppendLine($"- SectionChangedCount: `{report.SectionChangedCount}`");
        builder.AppendLine($"- TokenDeltaTotal: `{report.TokenDeltaTotal}`");
        builder.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
        builder.AppendLine($"- TokenDeltaP95: `{report.TokenDeltaP95}`");
        builder.AppendLine($"- ConstraintCoverageDelta: `{report.ConstraintCoverageDelta}`");
        builder.AppendLine($"- RelationCoverageDelta: `{report.RelationCoverageDelta}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "WorkspaceAllowlist", report.WorkspaceAllowlist);
        AppendList(builder, "CollectionAllowlist", report.CollectionAllowlist);
        AppendList(builder, "EvalScopeAllowlist", report.EvalScopeAllowlist);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- 本报告只聚合 preview-only observation，不写正式 package。");
        builder.AppendLine("- `FormalPackageWritten=false`、`RuntimeMutated=false`、`PackageOutputChanged=false`、`PackingPolicyChanged=false` 是 gate 条件。");
        builder.AppendLine("- `UseForRuntime=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`。");
        return builder.ToString();
    }

    private static LimitedFormalPreviewObservationReport BuildReport(
        string stage,
        ScopedFormalPreviewOptInReport? scopedOptInGate,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LimitedFormalPreviewObservationOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new LimitedFormalPreviewObservationOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var observationRunCount = Math.Max(0, options.ObservationWindowRuns);
        var minimumObservationRunCount = Math.Max(1, options.ObservationWindowRuns);
        var formalPackageWritten = options.WriteFormalPackage;
        var runtimeMutated = options.UseForRuntime || options.FormalRetrievalAllowed || options.ReadyForRuntimeSwitch;
        var nonAllowlistedScopeLeakCount = scopedOptInGate?.NonAllowlistedScopeLeakCount ?? 0;
        var riskAfterPolicy = Math.Max(scopedOptInGate?.RiskAfterPolicy ?? 0, shadowPackageComparison?.RiskAfterPolicy ?? 0);
        var mustNotRisk = Math.Max(scopedOptInGate?.MustNotHitRiskAfterPolicy ?? 0, shadowPackageComparison?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Math.Max(scopedOptInGate?.LifecycleRiskAfterPolicy ?? 0, shadowPackageComparison?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = Math.Max(scopedOptInGate?.FormalOutputChanged ?? 0, shadowPackageComparison?.FormalOutputChanged ?? 0);
        var packageOutputChanged = (scopedOptInGate?.PackageOutputChanged ?? false) || (shadowPackageComparison?.PackageOutputChanged ?? false);
        var packingPolicyChanged = (scopedOptInGate?.PackingPolicyChanged ?? false) || (shadowPackageComparison?.PackingPolicyChanged ?? false);
        var blocked = new List<string>();

        if (!options.Enabled || !string.Equals(options.Mode, ScopedFormalPreviewOptInModes.PreviewOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("LimitedFormalPreviewObservationDisabled");
        }

        if (options.RequireScopedFormalPreviewOptInPassed
            && (scopedOptInGate is null
                || !scopedOptInGate.GatePassed
                || !string.Equals(scopedOptInGate.Recommendation, ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("ScopedFormalPreviewOptInGateNotPassed");
        }

        if (shadowPackageComparison is null)
        {
            blocked.Add("ShadowPackageComparisonReportMissing");
        }

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ObservationProfileNotFrozenBestProfile");
        }

        if (observationRunCount < minimumObservationRunCount)
        {
            blocked.Add("InsufficientObservationRuns");
        }

        if (options.FailClosedOnRisk && (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0))
        {
            blocked.Add("RiskAfterPolicyNonZero");
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
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var multiplier = passed || shadowPackageComparison is not null ? observationRunCount : 0;
        return new LimitedFormalPreviewObservationReport
        {
            OperationId = $"vector-limited-formal-preview-observation-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = string.Equals(stage, "observation", StringComparison.OrdinalIgnoreCase) && passed,
            GatePassed = string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) && passed,
            Mode = options.Mode,
            ProfileName = profileName,
            MinimumObservationRunCount = minimumObservationRunCount,
            ObservationRunCount = observationRunCount,
            WorkspaceAllowlist = options.WorkspaceAllowlist.Count == 0
                ? scopedOptInGate?.WorkspaceAllowlist ?? Array.Empty<string>()
                : options.WorkspaceAllowlist,
            CollectionAllowlist = options.CollectionAllowlist.Count == 0
                ? scopedOptInGate?.CollectionAllowlist ?? Array.Empty<string>()
                : options.CollectionAllowlist,
            EvalScopeAllowlist = options.EvalScopeAllowlist.Count == 0
                ? scopedOptInGate?.EvalScopeAllowlist ?? Array.Empty<string>()
                : options.EvalScopeAllowlist,
            PreviewPackageCount = (shadowPackageComparison?.ShadowPackageCount ?? 0) * multiplier,
            BaselinePackageCount = (shadowPackageComparison?.BaselinePackageCount ?? 0) * multiplier,
            CandidateAddCount = (shadowPackageComparison?.CandidateAddCount ?? 0) * multiplier,
            CandidateRemoveCount = (shadowPackageComparison?.CandidateRemoveCount ?? 0) * multiplier,
            CandidateUnchangedCount = (shadowPackageComparison?.CandidateUnchangedCount ?? 0) * multiplier,
            SectionChangedCount = (shadowPackageComparison?.SectionChangedCount ?? 0) * multiplier,
            TokenDeltaTotal = (shadowPackageComparison?.TokenDeltaTotal ?? 0) * multiplier,
            TokenDeltaMax = shadowPackageComparison?.TokenDeltaMax ?? 0,
            TokenDeltaP95 = shadowPackageComparison?.TokenDeltaMax ?? 0,
            ConstraintCoverageDelta = shadowPackageComparison?.ConstraintCoverageDelta ?? 0,
            RelationCoverageDelta = shadowPackageComparison?.RelationCoverageDelta ?? 0,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            Recommendation = passed
                ? LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze
                : ResolveRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("LimitedFormalPreviewObservationDisabled", StringComparer.OrdinalIgnoreCase))
        {
            return LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly;
        }

        if (blocked.Contains("InsufficientObservationRuns", StringComparer.OrdinalIgnoreCase))
        {
            return LimitedFormalPreviewObservationRecommendations.NeedsMoreObservation;
        }

        if (blocked.Any(static item => item.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || item.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            return LimitedFormalPreviewObservationRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static item => item.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return LimitedFormalPreviewObservationRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static item => item.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return LimitedFormalPreviewObservationRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static item => item.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return LimitedFormalPreviewObservationRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static item => item.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return LimitedFormalPreviewObservationRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static item => item.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return LimitedFormalPreviewObservationRecommendations.BlockedByScopeLeak;
        }

        return LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly;
    }

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
