using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Explicit scoped runtime experiment planning；只输出计划和 dry-run gate，不启用 runtime。
/// </summary>
public sealed class ExplicitScopedRuntimeExperimentPlanRunner
{
    public ExplicitScopedRuntimeExperimentPlanReport BuildPlan(
        ContextCoreFoundationFreezeReport? foundationFreeze,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedFormalRetrievalPreviewReport? guardedFormalPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageComparisonGate,
        ScopedFormalPreviewOptInReport? scopedFormalPreviewOptInGate,
        LimitedFormalPreviewObservationReport? limitedFormalPreviewObservationGate,
        ExplicitScopedRuntimeExperimentPlanOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            "plan",
            foundationFreeze,
            reproducibility,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            runtimeChangeGate,
            guardedFormalPreviewGate,
            shadowPackageComparisonGate,
            scopedFormalPreviewOptInGate,
            limitedFormalPreviewObservationGate,
            options,
            sourceReports);

    public ExplicitScopedRuntimeExperimentPlanReport BuildDryRun(
        ContextCoreFoundationFreezeReport? foundationFreeze,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedFormalRetrievalPreviewReport? guardedFormalPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageComparisonGate,
        ScopedFormalPreviewOptInReport? scopedFormalPreviewOptInGate,
        LimitedFormalPreviewObservationReport? limitedFormalPreviewObservationGate,
        ExplicitScopedRuntimeExperimentPlanOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            "dry-run",
            foundationFreeze,
            reproducibility,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            runtimeChangeGate,
            guardedFormalPreviewGate,
            shadowPackageComparisonGate,
            scopedFormalPreviewOptInGate,
            limitedFormalPreviewObservationGate,
            options,
            sourceReports);

    public ExplicitScopedRuntimeExperimentPlanReport BuildGate(
        ContextCoreFoundationFreezeReport? foundationFreeze,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedFormalRetrievalPreviewReport? guardedFormalPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageComparisonGate,
        ScopedFormalPreviewOptInReport? scopedFormalPreviewOptInGate,
        LimitedFormalPreviewObservationReport? limitedFormalPreviewObservationGate,
        ExplicitScopedRuntimeExperimentPlanOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            "gate",
            foundationFreeze,
            reproducibility,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            runtimeChangeGate,
            guardedFormalPreviewGate,
            shadowPackageComparisonGate,
            scopedFormalPreviewOptInGate,
            limitedFormalPreviewObservationGate,
            options,
            sourceReports);

    public static string BuildMarkdown(string title, ExplicitScopedRuntimeExperimentPlanReport report)
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
        builder.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- AllowlistedScopeCount: `{report.AllowlistedScopeCount}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        builder.AppendLine($"- DryRunSupported: `{report.DryRunSupported}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- RollbackPlan: `{report.RollbackPlan}`");
        AppendList(builder, "WorkspaceAllowlist", report.WorkspaceAllowlist);
        AppendList(builder, "CollectionAllowlist", report.CollectionAllowlist);
        AppendList(builder, "EvalScopeAllowlist", report.EvalScopeAllowlist);
        AppendList(builder, "Allowed Actions", report.AllowedActions);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Required Gate Summary", report.RequiredGateSummary);
        AppendMap(builder, "Observation Metrics", report.ObservationMetrics);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- V4.5 只允许显式 scoped runtime experiment planning 和 dry-run。");
        builder.AppendLine("- 不允许 runtime switch、正式 `IVectorIndexStore` 绑定、正式 package 写入、PackingPolicy 集成或 package output mutation。");
        builder.AppendLine("- 非 allowlisted scope 必须保持 baseline；本报告只写 shadow planning artifact。");
        return builder.ToString();
    }

    private static ExplicitScopedRuntimeExperimentPlanReport BuildReport(
        string stage,
        ContextCoreFoundationFreezeReport? foundationFreeze,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedFormalRetrievalPreviewReport? guardedFormalPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageComparisonGate,
        ScopedFormalPreviewOptInReport? scopedFormalPreviewOptInGate,
        LimitedFormalPreviewObservationReport? limitedFormalPreviewObservationGate,
        ExplicitScopedRuntimeExperimentPlanOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new ExplicitScopedRuntimeExperimentPlanOptions();
        var mode = NormalizeMode(options.Mode);
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var workspaceAllowlist = CleanList(options.WorkspaceAllowlist);
        var collectionAllowlist = CleanList(options.CollectionAllowlist);
        var evalScopeAllowlist = CleanList(options.EvalScopeAllowlist);
        var allowlistedScopeCount = CountConfiguredScopes(workspaceAllowlist, collectionAllowlist, evalScopeAllowlist);
        var nonAllowlistedScopeChecked = allowlistedScopeCount > 0;
        var nonAllowlistedScopeLeakCount = Math.Max(
            scopedFormalPreviewOptInGate?.NonAllowlistedScopeLeakCount ?? 0,
            limitedFormalPreviewObservationGate?.NonAllowlistedScopeLeakCount ?? 0);
        var runtimeSwitchAllowed = options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.ReadyForRuntimeSwitch;
        var formalPackageWritten = options.WriteFormalPackage
            || (vectorFormalPreviewFreeze?.FormalPackageWritten ?? false)
            || (limitedFormalPreviewObservationGate?.FormalPackageWritten ?? false)
            || (scopedFormalPreviewOptInGate?.FormalPackageWritten ?? false);
        var packingPolicyChanged = (guardedFormalPreviewGate?.PackingPolicyChanged ?? false)
            || (shadowPackageComparisonGate?.PackingPolicyChanged ?? false)
            || (scopedFormalPreviewOptInGate?.PackingPolicyChanged ?? false)
            || (limitedFormalPreviewObservationGate?.PackingPolicyChanged ?? false)
            || (vectorFormalPreviewFreeze?.PackingPolicyChanged ?? false);
        var packageOutputChanged = (guardedFormalPreviewGate?.PackageOutputChanged ?? false)
            || (shadowPackageComparisonGate?.PackageOutputChanged ?? false)
            || (scopedFormalPreviewOptInGate?.PackageOutputChanged ?? false)
            || (limitedFormalPreviewObservationGate?.PackageOutputChanged ?? false)
            || (vectorFormalPreviewFreeze?.PackageOutputChanged ?? false);
        var runtimeMutated = runtimeSwitchAllowed
            || (shadowPackageComparisonGate?.RuntimeMutated ?? false)
            || (scopedFormalPreviewOptInGate?.RuntimeMutated ?? false)
            || (limitedFormalPreviewObservationGate?.RuntimeMutated ?? false)
            || (vectorFormalPreviewFreeze?.RuntimeMutated ?? false);
        var riskAfterPolicy = Max(
            guardedFormalPreviewGate?.RiskAfterPolicy ?? 0,
            shadowPackageComparisonGate?.RiskAfterPolicy ?? 0,
            scopedFormalPreviewOptInGate?.RiskAfterPolicy ?? 0,
            limitedFormalPreviewObservationGate?.RiskAfterPolicy ?? 0,
            vectorFormalPreviewFreeze?.RiskAfterPolicy ?? 0);
        var mustNotRisk = Max(
            guardedFormalPreviewGate?.MustNotHitRiskAfterPolicy ?? 0,
            shadowPackageComparisonGate?.MustNotHitRiskAfterPolicy ?? 0,
            scopedFormalPreviewOptInGate?.MustNotHitRiskAfterPolicy ?? 0,
            limitedFormalPreviewObservationGate?.MustNotHitRiskAfterPolicy ?? 0,
            vectorFormalPreviewFreeze?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Max(
            guardedFormalPreviewGate?.LifecycleRiskAfterPolicy ?? 0,
            shadowPackageComparisonGate?.LifecycleRiskAfterPolicy ?? 0,
            scopedFormalPreviewOptInGate?.LifecycleRiskAfterPolicy ?? 0,
            limitedFormalPreviewObservationGate?.LifecycleRiskAfterPolicy ?? 0,
            vectorFormalPreviewFreeze?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = Max(
            guardedFormalPreviewGate?.FormalOutputChanged ?? 0,
            shadowPackageComparisonGate?.FormalOutputChanged ?? 0,
            scopedFormalPreviewOptInGate?.FormalOutputChanged ?? 0,
            limitedFormalPreviewObservationGate?.FormalOutputChanged ?? 0,
            vectorFormalPreviewFreeze?.FormalOutputChanged ?? 0);
        var blocked = new List<string>();

        if (!options.Enabled)
        {
            blocked.Add("ExplicitScopedRuntimeExperimentPlanningDisabled");
        }

        if (!string.Equals(mode, ExplicitScopedRuntimeExperimentModes.PlanOnly, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, ExplicitScopedRuntimeExperimentModes.DryRun, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedExplicitScopedRuntimeExperimentMode");
        }

        if (options.RequireFoundationFreeze
            && (foundationFreeze is null
                || !foundationFreeze.FreezePassed
                || !string.Equals(foundationFreeze.Recommendation, ContextCoreFoundationFreezeRecommendations.ReadyForReleaseCandidate, StringComparison.OrdinalIgnoreCase)
                || reproducibility is null
                || !reproducibility.ReproducibilityPassed))
        {
            blocked.Add("FoundationFreezeOrReproducibilityGateNotPassed");
        }

        if (options.RequireServiceFoundationFreeze
            && (serviceFoundationFreeze is null
                || !serviceFoundationFreeze.FreezePassed
                || !string.Equals(serviceFoundationFreeze.Recommendation, "ReadyForV45ExplicitScopedRuntimeExperimentPlanning", StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("ServiceFoundationFreezeGateNotPassed");
        }

        if (options.RequireVectorFormalPreviewFreeze
            && (vectorFormalPreviewFreeze is null
                || !vectorFormalPreviewFreeze.FreezePassed
                || !string.Equals(vectorFormalPreviewFreeze.Recommendation, VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("VectorFormalPreviewFreezeGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate
            && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
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

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("PreviewProfileNotFrozenBestProfile");
        }

        if (allowlistedScopeCount == 0)
        {
            blocked.Add("SelectedScopeNotConfigured");
        }

        if (nonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (formalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (formalPackageWritten)
        {
            blocked.Add("FormalPackageWriteAttempt");
        }

        if (packingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (packageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (runtimeMutated)
        {
            blocked.Add("RuntimeSwitchOrMutationAttempt");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;

        return new ExplicitScopedRuntimeExperimentPlanReport
        {
            OperationId = $"vector-scoped-runtime-experiment-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = passed,
            Recommendation = passed
                ? ExplicitScopedRuntimeExperimentRecommendations.ReadyForExplicitScopedRuntimeExperimentDryRun
                : ResolveRecommendation(distinctBlocked),
            Mode = mode,
            ProfileName = profileName,
            WorkspaceAllowlist = workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist,
            ScopeCount = allowlistedScopeCount + (nonAllowlistedScopeChecked ? 1 : 0),
            AllowlistedScopeCount = allowlistedScopeCount,
            NonAllowlistedScopeChecked = nonAllowlistedScopeChecked,
            RequiredGateSummary = BuildGateSummary(
                foundationFreeze,
                reproducibility,
                serviceFoundationFreeze,
                vectorFormalPreviewFreeze,
                runtimeChangeGate,
                guardedFormalPreviewGate,
                shadowPackageComparisonGate,
                scopedFormalPreviewOptInGate,
                limitedFormalPreviewObservationGate),
            AllowedActions =
            [
                "ScopeAllowlistPlanning",
                "PreviewProfileSelection",
                "RollbackPlanDefinition",
                "ObservationMetricsDefinition",
                "DryRunPackageComparisonPlanning",
                "ShadowArtifactOnlyDryRun"
            ],
            ForbiddenActions =
            [
                "RuntimeSwitch",
                "FormalIVectorIndexStoreBinding",
                "FormalPackageWrite",
                "PackingPolicyIntegration",
                "PackageOutputMutation",
                "GlobalDefaultOn",
                "NonAllowlistedScopeUse"
            ],
            RollbackPlan = "Remove scopes from allowlists, keep UseForRuntime=false, discard shadow artifacts, rerun V4.F and runtime-change gate.",
            ObservationMetrics = BuildObservationMetrics(
                riskAfterPolicy,
                mustNotRisk,
                lifecycleRisk,
                formalOutputChanged,
                packageOutputChanged,
                packingPolicyChanged,
                formalPackageWritten,
                runtimeMutated,
                nonAllowlistedScopeLeakCount),
            DryRunSupported = passed && string.Equals(mode, ExplicitScopedRuntimeExperimentModes.DryRun, StringComparison.OrdinalIgnoreCase),
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            UseForRuntime = options.UseForRuntime,
            FormalPackageWritten = formalPackageWritten,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            RuntimeMutated = runtimeMutated,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string NormalizeMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? ExplicitScopedRuntimeExperimentModes.PlanOnly : mode.Trim();

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

    private static IReadOnlyDictionary<string, string> BuildGateSummary(
        ContextCoreFoundationFreezeReport? foundationFreeze,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedFormalRetrievalPreviewReport? guardedFormalPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageComparisonGate,
        ScopedFormalPreviewOptInReport? scopedFormalPreviewOptInGate,
        LimitedFormalPreviewObservationReport? limitedFormalPreviewObservationGate)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundation-release-candidate-gate"] = foundationFreeze is null
                ? "Missing"
                : $"{foundationFreeze.FreezePassed}:{foundationFreeze.Recommendation}",
            ["foundation-reproducibility-check"] = reproducibility is null
                ? "Missing"
                : $"{reproducibility.ReproducibilityPassed}:{reproducibility.Recommendation}",
            ["service-foundation-freeze-gate"] = serviceFoundationFreeze is null
                ? "Missing"
                : $"{serviceFoundationFreeze.FreezePassed}:{serviceFoundationFreeze.Recommendation}",
            ["vector-formal-preview-freeze-gate"] = vectorFormalPreviewFreeze is null
                ? "Missing"
                : $"{vectorFormalPreviewFreeze.FreezePassed}:{vectorFormalPreviewFreeze.Recommendation}",
            ["learning-runtime-change-readiness-gate"] = runtimeChangeGate is null
                ? "Missing"
                : $"{runtimeChangeGate.Passed}:{runtimeChangeGate.Recommendation}",
            ["vector-guarded-formal-retrieval-preview-gate"] = guardedFormalPreviewGate is null
                ? "Missing"
                : $"{guardedFormalPreviewGate.GatePassed}:{guardedFormalPreviewGate.Recommendation}",
            ["vector-shadow-package-comparison-gate"] = shadowPackageComparisonGate is null
                ? "Missing"
                : $"{shadowPackageComparisonGate.GatePassed}:{shadowPackageComparisonGate.Recommendation}",
            ["vector-scoped-formal-preview-optin-gate"] = scopedFormalPreviewOptInGate is null
                ? "Missing"
                : $"{scopedFormalPreviewOptInGate.GatePassed}:{scopedFormalPreviewOptInGate.Recommendation}",
            ["vector-limited-formal-preview-observation-gate"] = limitedFormalPreviewObservationGate is null
                ? "Missing"
                : $"{limitedFormalPreviewObservationGate.GatePassed}:{limitedFormalPreviewObservationGate.Recommendation}"
        };

    private static IReadOnlyDictionary<string, string> BuildObservationMetrics(
        int riskAfterPolicy,
        int mustNotRisk,
        int lifecycleRisk,
        int formalOutputChanged,
        bool packageOutputChanged,
        bool packingPolicyChanged,
        bool formalPackageWritten,
        bool runtimeMutated,
        int nonAllowlistedScopeLeakCount)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RiskAfterPolicy"] = riskAfterPolicy.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MustNotHitRiskAfterPolicy"] = mustNotRisk.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["LifecycleRiskAfterPolicy"] = lifecycleRisk.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FormalOutputChanged"] = formalOutputChanged.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PackageOutputChanged"] = packageOutputChanged.ToString(),
            ["PackingPolicyChanged"] = packingPolicyChanged.ToString(),
            ["FormalPackageWritten"] = formalPackageWritten.ToString(),
            ["RuntimeMutated"] = runtimeMutated.ToString(),
            ["NonAllowlistedScopeLeakCount"] = nonAllowlistedScopeLeakCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("ExplicitScopedRuntimeExperimentPlanningDisabled", StringComparer.OrdinalIgnoreCase))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.KeepPreviewOnly;
        }

        if (blocked.Contains("SelectedScopeNotConfigured", StringComparer.OrdinalIgnoreCase))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.NeedsScopeConfiguration;
        }

        if (blocked.Any(static reason => reason.Contains("ServiceFoundation", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.BlockedByMissingServiceFreeze;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeChange", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.BlockedByRuntimeChangeGate;
        }

        if (blocked.Any(static reason => reason.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.BlockedByRuntimeSwitchAttempt;
        }

        if (blocked.Any(static reason => reason.Contains("Foundation", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Reproducibility", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentRecommendations.BlockedByMissingFoundationFreeze;
        }

        return ExplicitScopedRuntimeExperimentRecommendations.KeepPreviewOnly;
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
