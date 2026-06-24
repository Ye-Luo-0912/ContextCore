using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Scoped runtime experiment dry-run observation；只聚合 dry-run 观测，不启用 runtime。
/// </summary>
public sealed class ScopedRuntimeExperimentDryRunObservationRunner
{
    public ScopedRuntimeExperimentDryRunObservationReport BuildObservation(
        ExplicitScopedRuntimeExperimentPlanReport? v45Gate,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentDryRunObservationOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("observation", v45Gate, shadowPackageComparison, runtimeChangeGate, options, sourceReports);

    public ScopedRuntimeExperimentDryRunObservationReport BuildGate(
        ExplicitScopedRuntimeExperimentPlanReport? v45Gate,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentDryRunObservationOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("gate", v45Gate, shadowPackageComparison, runtimeChangeGate, options, sourceReports);

    public static string BuildMarkdown(string title, ScopedRuntimeExperimentDryRunObservationReport report)
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
        builder.AppendLine($"- AllowlistedScopeCount: `{report.AllowlistedScopeCount}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        builder.AppendLine($"- DryRunPackageCount: `{report.DryRunPackageCount}`");
        builder.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        builder.AppendLine($"- CandidateAddCount: `{report.CandidateAddCount}`");
        builder.AppendLine($"- CandidateRemoveCount: `{report.CandidateRemoveCount}`");
        builder.AppendLine($"- TokenDeltaTotal: `{report.TokenDeltaTotal}`");
        builder.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- RollbackPlanAvailable: `{report.RollbackPlanAvailable}`");
        builder.AppendLine($"- RuntimeChangeGateConsistent: `{report.RuntimeChangeGateConsistent}`");
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
        builder.AppendLine("- V4.6 只允许 scoped runtime experiment dry-run observation。");
        builder.AppendLine("- 不允许 runtime switch、正式 `IVectorIndexStore` 绑定、正式 package 写入、PackingPolicy 集成或 package output mutation。");
        builder.AppendLine("- 本报告只写 shadow observation artifact；非 allowlisted scope 必须保持 baseline。");
        return builder.ToString();
    }

    private static ScopedRuntimeExperimentDryRunObservationReport BuildReport(
        string stage,
        ExplicitScopedRuntimeExperimentPlanReport? v45Gate,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentDryRunObservationOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new ScopedRuntimeExperimentDryRunObservationOptions();
        var mode = string.IsNullOrWhiteSpace(options.Mode)
            ? ScopedRuntimeExperimentDryRunObservationModes.DryRun
            : options.Mode.Trim();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName.Trim();
        var workspaceAllowlist = CleanList(options.WorkspaceAllowlist.Count == 0
            ? v45Gate?.WorkspaceAllowlist ?? Array.Empty<string>()
            : options.WorkspaceAllowlist);
        var collectionAllowlist = CleanList(options.CollectionAllowlist.Count == 0
            ? v45Gate?.CollectionAllowlist ?? Array.Empty<string>()
            : options.CollectionAllowlist);
        var evalScopeAllowlist = CleanList(options.EvalScopeAllowlist.Count == 0
            ? v45Gate?.EvalScopeAllowlist ?? Array.Empty<string>()
            : options.EvalScopeAllowlist);
        var allowlistedScopeCount = CountConfiguredScopes(workspaceAllowlist, collectionAllowlist, evalScopeAllowlist);
        var observationRunCount = Math.Max(0, options.ObservationRunCount);
        var minimumObservationRunCount = Math.Max(1, options.ObservationRunCount);
        var v45Passed = v45Gate is not null
            && v45Gate.PlanPassed
            && string.Equals(
                v45Gate.Recommendation,
                ExplicitScopedRuntimeExperimentRecommendations.ReadyForExplicitScopedRuntimeExperimentDryRun,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(v45Gate.Mode, ExplicitScopedRuntimeExperimentModes.DryRun, StringComparison.OrdinalIgnoreCase);
        var runtimeChangeGateConsistent = runtimeChangeGate?.Passed == true;
        var nonAllowlistedScopeChecked = (v45Gate?.NonAllowlistedScopeChecked ?? false) && allowlistedScopeCount > 0;
        var nonAllowlistedScopeLeakCount = v45Gate?.NonAllowlistedScopeLeakCount ?? 0;
        var riskAfterPolicy = Math.Max(v45Gate?.RiskAfterPolicy ?? 0, shadowPackageComparison?.RiskAfterPolicy ?? 0);
        var mustNotRisk = Math.Max(v45Gate?.MustNotHitRiskAfterPolicy ?? 0, shadowPackageComparison?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Math.Max(v45Gate?.LifecycleRiskAfterPolicy ?? 0, shadowPackageComparison?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = Math.Max(v45Gate?.FormalOutputChanged ?? 0, shadowPackageComparison?.FormalOutputChanged ?? 0);
        var formalPackageWritten = options.WriteFormalPackage || (v45Gate?.FormalPackageWritten ?? false);
        var runtimeMutated = options.RuntimeMutated
            || options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.ReadyForRuntimeSwitch
            || (v45Gate?.RuntimeMutated ?? false)
            || (shadowPackageComparison?.RuntimeMutated ?? false);
        var vectorStoreBindingChanged = options.VectorStoreBindingChanged;
        var packingPolicyChanged = options.PackingPolicyChanged
            || (v45Gate?.PackingPolicyChanged ?? false)
            || (shadowPackageComparison?.PackingPolicyChanged ?? false);
        var packageOutputChanged = options.PackageOutputChanged
            || (v45Gate?.PackageOutputChanged ?? false)
            || (shadowPackageComparison?.PackageOutputChanged ?? false);
        var rollbackPlanAvailable = !string.IsNullOrWhiteSpace(v45Gate?.RollbackPlan);
        var blocked = new List<string>();

        if (!options.Enabled)
        {
            blocked.Add("ScopedRuntimeExperimentDryRunObservationDisabled");
        }

        if (!string.Equals(mode, ScopedRuntimeExperimentDryRunObservationModes.DryRun, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedScopedRuntimeExperimentObservationMode");
        }

        if (options.RequireV45PlanPassed && !v45Passed)
        {
            blocked.Add("V45ScopedRuntimeExperimentGateNotPassed");
        }

        if (shadowPackageComparison is null)
        {
            blocked.Add("ShadowPackageComparisonReportMissing");
        }

        if (!runtimeChangeGateConsistent)
        {
            blocked.Add("RuntimeChangeGateNotConsistent");
        }

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("DryRunObservationProfileNotFrozenBestProfile");
        }

        if (allowlistedScopeCount == 0)
        {
            blocked.Add("SelectedScopeNotConfigured");
        }

        if (!nonAllowlistedScopeChecked)
        {
            blocked.Add("NonAllowlistedScopeBaselineCheckMissing");
        }

        if (observationRunCount < minimumObservationRunCount)
        {
            blocked.Add("InsufficientDryRunObservationRuns");
        }

        if (options.FailClosedOnRisk && (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0))
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (formalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (formalPackageWritten)
        {
            blocked.Add("FormalPackageWritten");
        }

        if (runtimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (vectorStoreBindingChanged)
        {
            blocked.Add("VectorStoreBindingChanged");
        }

        if (packingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (packageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (nonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        if (!rollbackPlanAvailable)
        {
            blocked.Add("RollbackPlanMissing");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var multiplier = shadowPackageComparison is null ? 0 : observationRunCount;

        return new ScopedRuntimeExperimentDryRunObservationReport
        {
            OperationId = $"vector-scoped-runtime-experiment-dry-run-observation-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = string.Equals(stage, "observation", StringComparison.OrdinalIgnoreCase) && passed,
            GatePassed = string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) && passed,
            Mode = mode,
            ProfileName = profileName,
            ObservationRunCount = observationRunCount,
            MinimumObservationRunCount = minimumObservationRunCount,
            WorkspaceAllowlist = workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist,
            AllowlistedScopeCount = allowlistedScopeCount,
            NonAllowlistedScopeChecked = nonAllowlistedScopeChecked,
            DryRunPackageCount = (shadowPackageComparison?.ShadowPackageCount ?? 0) * multiplier,
            BaselinePackageCount = (shadowPackageComparison?.BaselinePackageCount ?? 0) * multiplier,
            CandidateAddCount = (shadowPackageComparison?.CandidateAddCount ?? 0) * multiplier,
            CandidateRemoveCount = (shadowPackageComparison?.CandidateRemoveCount ?? 0) * multiplier,
            TokenDeltaTotal = (shadowPackageComparison?.TokenDeltaTotal ?? 0) * multiplier,
            TokenDeltaMax = shadowPackageComparison?.TokenDeltaMax ?? 0,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            RollbackPlanAvailable = rollbackPlanAvailable,
            RuntimeChangeGateConsistent = runtimeChangeGateConsistent,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            Recommendation = passed
                ? ScopedRuntimeExperimentDryRunObservationRecommendations.ReadyForScopedRuntimeExperimentDesignFreeze
                : ResolveRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int CountConfiguredScopes(
        IReadOnlyList<string> workspaces,
        IReadOnlyList<string> collections,
        IReadOnlyList<string> evalScopes)
        => Math.Max(workspaces.Count, Math.Max(collections.Count, evalScopes.Count));

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("InsufficientDryRunObservationRuns", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.NeedsMoreDryRunObservation;
        }

        if (blocked.Any(static reason => reason.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByFormalPackageWrite;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeMutated", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByVectorStoreBindingMutation;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDryRunObservationRecommendations.BlockedByScopeLeak;
        }

        return ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly;
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
