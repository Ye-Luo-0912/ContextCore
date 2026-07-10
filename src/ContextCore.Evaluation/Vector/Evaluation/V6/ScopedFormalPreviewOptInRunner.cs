using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Scoped formal preview opt-in policy；只判断 scope 边界，不执行 retrieval/package 写入。
/// </summary>
public sealed class ScopedFormalPreviewOptInPolicy
{
    public bool IsSelectedScopeConfigured(ScopedFormalPreviewOptInOptions options)
        => !string.IsNullOrWhiteSpace(options.SelectedWorkspaceId)
            && !string.IsNullOrWhiteSpace(options.SelectedCollectionId)
            && !string.IsNullOrWhiteSpace(options.SelectedEvalScope);

    public bool IsSelectedScopeAllowlisted(ScopedFormalPreviewOptInOptions options)
        => IsSelectedScopeConfigured(options)
            && Contains(options.WorkspaceAllowlist, options.SelectedWorkspaceId)
            && Contains(options.CollectionAllowlist, options.SelectedCollectionId)
            && Contains(options.EvalScopeAllowlist, options.SelectedEvalScope);

    public bool IsNonAllowlistedScopeConfigured(ScopedFormalPreviewOptInOptions options)
        => !string.IsNullOrWhiteSpace(options.NonAllowlistedWorkspaceId)
            && !string.IsNullOrWhiteSpace(options.NonAllowlistedCollectionId)
            && !string.IsNullOrWhiteSpace(options.NonAllowlistedEvalScope);

    public int GetNonAllowlistedScopeLeakCount(ScopedFormalPreviewOptInOptions options)
        => IsNonAllowlistedScopeConfigured(options)
            && Contains(options.WorkspaceAllowlist, options.NonAllowlistedWorkspaceId)
            && Contains(options.CollectionAllowlist, options.NonAllowlistedCollectionId)
            && Contains(options.EvalScopeAllowlist, options.NonAllowlistedEvalScope)
                ? 1
                : 0;

    private static bool Contains(IReadOnlyList<string> values, string value)
        => !string.IsNullOrWhiteSpace(value)
            && values.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Scoped formal preview opt-in；只允许显式 allowlist scope 进入 preview-only，不写正式 package。
/// </summary>
public sealed class ScopedFormalPreviewOptInRunner
{
    public ScopedFormalPreviewOptInReport BuildPlan(
        VectorV4ReadinessRecheckReport? v4Recheck,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageGate,
        ScopedFormalPreviewOptInOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("plan", v4Recheck, guardedPreviewGate, shadowPackageGate, options, sourceReports);

    public ScopedFormalPreviewOptInReport BuildSmoke(
        VectorV4ReadinessRecheckReport? v4Recheck,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageGate,
        ScopedFormalPreviewOptInOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("smoke", v4Recheck, guardedPreviewGate, shadowPackageGate, options, sourceReports);

    public ScopedFormalPreviewOptInReport BuildGate(
        VectorV4ReadinessRecheckReport? v4Recheck,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageGate,
        ScopedFormalPreviewOptInOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport("gate", v4Recheck, guardedPreviewGate, shadowPackageGate, options, sourceReports);

    public static string BuildMarkdown(string title, ScopedFormalPreviewOptInReport report)
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
        builder.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- SelectedWorkspaceId: `{report.SelectedWorkspaceId}`");
        builder.AppendLine($"- SelectedCollectionId: `{report.SelectedCollectionId}`");
        builder.AppendLine($"- SelectedEvalScope: `{report.SelectedEvalScope}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- AllowlistedScopeCount: `{report.AllowlistedScopeCount}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        builder.AppendLine($"- PreviewPackageCount: `{report.PreviewPackageCount}`");
        builder.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        builder.AppendLine($"- CandidateAddCount: `{report.CandidateAddCount}`");
        builder.AppendLine($"- CandidateRemoveCount: `{report.CandidateRemoveCount}`");
        builder.AppendLine($"- TokenDeltaTotal: `{report.TokenDeltaTotal}`");
        builder.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
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
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        AppendList(builder, "WorkspaceAllowlist", report.WorkspaceAllowlist);
        AppendList(builder, "CollectionAllowlist", report.CollectionAllowlist);
        AppendList(builder, "EvalScopeAllowlist", report.EvalScopeAllowlist);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendMap(builder, "Gate Dependency Summary", report.GateDependencySummary);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- Scoped formal preview opt-in 只允许显式 allowlist scope 的 preview-only。");
        builder.AppendLine("- 非 allowlisted scope 必须保持 current formal baseline path。");
        builder.AppendLine("- `FormalPackageWritten=false`、`RuntimeMutated=false`、`PackageOutputChanged=false`、`PackingPolicyChanged=false` 是 gate 条件。");
        builder.AppendLine("- `UseForRuntime`、`FormalRetrievalAllowed`、`ReadyForRuntimeSwitch` 均保持 `false`。");
        return builder.ToString();
    }

    private static ScopedFormalPreviewOptInReport BuildReport(
        string stage,
        VectorV4ReadinessRecheckReport? v4Recheck,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageGate,
        ScopedFormalPreviewOptInOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new ScopedFormalPreviewOptInOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var policy = new ScopedFormalPreviewOptInPolicy();
        var selectedScopeConfigured = policy.IsSelectedScopeConfigured(options);
        var selectedAllowlisted = policy.IsSelectedScopeAllowlisted(options);
        var nonAllowlistedScopeChecked = policy.IsNonAllowlistedScopeConfigured(options);
        var nonAllowlistedLeakCount = policy.GetNonAllowlistedScopeLeakCount(options);
        var formalPackageWritten = options.WriteFormalPackage;
        var runtimeMutated = options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.ReadyForRuntimeSwitch;
        var riskAfterPolicy = Math.Max(
            shadowPackageGate?.RiskAfterPolicy ?? 0,
            Math.Max(guardedPreviewGate?.RiskAfterPolicy ?? 0, v4Recheck?.RiskAfterPolicy ?? 0));
        var mustNotRisk = Math.Max(shadowPackageGate?.MustNotHitRiskAfterPolicy ?? 0, guardedPreviewGate?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Math.Max(shadowPackageGate?.LifecycleRiskAfterPolicy ?? 0, guardedPreviewGate?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = Math.Max(
            shadowPackageGate?.FormalOutputChanged ?? 0,
            Math.Max(guardedPreviewGate?.FormalOutputChanged ?? 0, v4Recheck?.FormalOutputChanged ?? 0));
        var packageOutputChanged = shadowPackageGate?.PackageOutputChanged ?? false;
        var packingPolicyChanged = shadowPackageGate?.PackingPolicyChanged ?? false;
        var blocked = new List<string>();

        if (!options.Enabled || string.Equals(options.Mode, ScopedFormalPreviewOptInModes.Off, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ScopedFormalPreviewOptInDisabled");
        }

        if (!string.Equals(options.Mode, ScopedFormalPreviewOptInModes.Off, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Mode, ScopedFormalPreviewOptInModes.PreviewOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedScopedFormalPreviewOptInMode");
        }

        if (options.RequireV4RecheckPassed
            && (v4Recheck is null
                || !v4Recheck.RecheckPassed
                || !string.Equals(v4Recheck.Recommendation, VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("V4ReadinessRecheckGateNotPassed");
        }

        if (options.RequireGuardedFormalPreviewPassed
            && (guardedPreviewGate is null
                || !guardedPreviewGate.GatePassed
                || !string.Equals(guardedPreviewGate.Recommendation, GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("GuardedFormalRetrievalPreviewGateNotPassed");
        }

        if (options.RequireShadowPackageComparisonPassed
            && (shadowPackageGate is null
                || !shadowPackageGate.GatePassed
                || !string.Equals(shadowPackageGate.Recommendation, VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("ShadowPackageComparisonGateNotPassed");
        }

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("PreviewProfileNotFrozenBestProfile");
        }

        if (options.Enabled
            && string.Equals(options.Mode, ScopedFormalPreviewOptInModes.PreviewOnly, StringComparison.OrdinalIgnoreCase)
            && !selectedAllowlisted)
        {
            blocked.Add(selectedScopeConfigured ? "SelectedScopeNotAllowlisted" : "SelectedScopeNotConfigured");
        }

        if (!nonAllowlistedScopeChecked)
        {
            blocked.Add("NonAllowlistedScopeNotChecked");
        }

        if (nonAllowlistedLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        if (formalPackageWritten)
        {
            blocked.Add("FormalPackageWriteAttempt");
        }

        if (runtimeMutated)
        {
            blocked.Add("RuntimeMutationAttempt");
        }

        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
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

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var previewCountsEnabled = selectedAllowlisted
            && options.Enabled
            && string.Equals(options.Mode, ScopedFormalPreviewOptInModes.PreviewOnly, StringComparison.OrdinalIgnoreCase);
        return new ScopedFormalPreviewOptInReport
        {
            OperationId = $"vector-scoped-formal-preview-optin-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = string.Equals(stage, "plan", StringComparison.OrdinalIgnoreCase) && passed,
            SmokePassed = string.Equals(stage, "smoke", StringComparison.OrdinalIgnoreCase) && passed,
            GatePassed = string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) && passed,
            Mode = options.Mode,
            ProfileName = profileName,
            WorkspaceAllowlist = options.WorkspaceAllowlist,
            CollectionAllowlist = options.CollectionAllowlist,
            EvalScopeAllowlist = options.EvalScopeAllowlist,
            SelectedWorkspaceId = options.SelectedWorkspaceId,
            SelectedCollectionId = options.SelectedCollectionId,
            SelectedEvalScope = options.SelectedEvalScope,
            NonAllowlistedWorkspaceId = options.NonAllowlistedWorkspaceId,
            NonAllowlistedCollectionId = options.NonAllowlistedCollectionId,
            NonAllowlistedEvalScope = options.NonAllowlistedEvalScope,
            ScopeCount = (selectedScopeConfigured ? 1 : 0) + (nonAllowlistedScopeChecked ? 1 : 0),
            AllowlistedScopeCount = selectedAllowlisted ? 1 : 0,
            NonAllowlistedScopeChecked = nonAllowlistedScopeChecked,
            PreviewPackageCount = previewCountsEnabled ? shadowPackageGate?.ShadowPackageCount ?? 0 : 0,
            BaselinePackageCount = nonAllowlistedScopeChecked ? shadowPackageGate?.BaselinePackageCount ?? 0 : 0,
            CandidateAddCount = previewCountsEnabled ? shadowPackageGate?.CandidateAddCount ?? 0 : 0,
            CandidateRemoveCount = previewCountsEnabled ? shadowPackageGate?.CandidateRemoveCount ?? 0 : 0,
            TokenDeltaTotal = previewCountsEnabled ? shadowPackageGate?.TokenDeltaTotal ?? 0 : 0,
            TokenDeltaMax = previewCountsEnabled ? shadowPackageGate?.TokenDeltaMax ?? 0 : 0,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            FormalPackageWritten = formalPackageWritten,
            RuntimeMutated = runtimeMutated,
            NonAllowlistedScopeLeakCount = nonAllowlistedLeakCount,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            RollbackInstruction = "Remove the selected workspace, collection, and eval scope from allowlists; keep Mode=Off.",
            Recommendation = passed
                ? ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation
                : ResolveRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked,
            GateDependencySummary = BuildDependencySummary(v4Recheck, guardedPreviewGate, shadowPackageGate),
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyDictionary<string, string> BuildDependencySummary(
        VectorV4ReadinessRecheckReport? v4Recheck,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonReport? shadowPackageGate)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["V4ReadinessRecheck"] = v4Recheck is null
                ? "Missing"
                : $"{v4Recheck.RecheckPassed}:{v4Recheck.Recommendation}",
            ["GuardedFormalRetrievalPreview"] = guardedPreviewGate is null
                ? "Missing"
                : $"{guardedPreviewGate.GatePassed}:{guardedPreviewGate.Recommendation}",
            ["ShadowPackageComparison"] = shadowPackageGate is null
                ? "Missing"
                : $"{shadowPackageGate.GatePassed}:{shadowPackageGate.Recommendation}"
        };

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("ScopedFormalPreviewOptInDisabled", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Any(static reason => reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Scope", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Profile", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedFormalPreviewOptInRecommendations.BlockedByMissingGate;
        }

        return ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly;
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
