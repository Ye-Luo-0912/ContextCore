using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Explicit scoped runtime experiment proposal；只生成 proposal 和 config patch preview，不写 runtime 配置。
/// </summary>
public sealed class ExplicitScopedRuntimeExperimentProposalRunner
{
    public ExplicitScopedRuntimeExperimentProposalReport BuildProposal(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            "proposal",
            foundationReleaseCandidate,
            reproducibility,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            designFreeze,
            runtimeChangeGate,
            options,
            sourceReports);

    public ExplicitScopedRuntimeExperimentProposalReport BuildConfigPreview(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            "config-preview",
            foundationReleaseCandidate,
            reproducibility,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            designFreeze,
            runtimeChangeGate,
            options,
            sourceReports);

    public ExplicitScopedRuntimeExperimentProposalReport BuildGate(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            "gate",
            foundationReleaseCandidate,
            reproducibility,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            designFreeze,
            runtimeChangeGate,
            options,
            sourceReports);

    public static string BuildMarkdown(string title, ExplicitScopedRuntimeExperimentProposalReport report)
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
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ProposalPassed: `{report.ProposalPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- EvalScopeId: `{report.EvalScopeId}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- ApprovalRequired: `{report.ApprovalRequired}`");
        builder.AppendLine($"- Approved: `{report.Approved}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- WriteFormalPackage: `{report.WriteFormalPackage}`");
        builder.AppendLine($"- ConfigPatchWritten: `{report.ConfigPatchWritten}`");
        builder.AppendLine($"- DiBindingChanged: `{report.DiBindingChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- RollbackPlan: `{report.RollbackPlan}`");
        builder.AppendLine($"- KillSwitchPlan: `{report.KillSwitchPlan}`");
        AppendMap(builder, "Required Gate Summary", report.RequiredGateSummary);
        AppendMap(builder, "Proposed Config Patch Preview", report.ProposedConfigPatch);
        AppendMap(builder, "Observation Plan", report.ObservationPlan);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- V4.8 只生成 explicit scoped runtime experiment proposal 和 config patch preview。");
        builder.AppendLine("- 不写 appsettings，不改 DI binding，不绑定正式 `IVectorIndexStore`，不写正式 package。");
        builder.AppendLine("- `Approved=false`；任何 approval 必须来自后续人工 gate，不能由本 eval 自动产生。");
        return builder.ToString();
    }

    private static ExplicitScopedRuntimeExperimentProposalReport BuildReport(
        string stage,
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new ExplicitScopedRuntimeExperimentProposalOptions();
        var workspaceId = Clean(options.WorkspaceId);
        var collectionId = Clean(options.CollectionId);
        var evalScopeId = Clean(options.EvalScopeId);
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName.Trim();
        var proposalId = string.IsNullOrWhiteSpace(options.ProposalId)
            ? BuildStableProposalId(workspaceId, collectionId, evalScopeId, profileName)
            : options.ProposalId.Trim();
        var rollbackPlan = string.IsNullOrWhiteSpace(options.RollbackPlan)
            ? string.Empty
            : options.RollbackPlan.Trim();
        var killSwitchPlan = string.IsNullOrWhiteSpace(options.KillSwitchPlan)
            ? string.Empty
            : options.KillSwitchPlan.Trim();
        var nonAllowlistedScopeLeakCount = designFreeze?.NonAllowlistedScopeLeakCount ?? 0;
        var approvalRequired = options.RequireManualApproval;
        var approved = options.Approved;
        var runtimeSwitchAttempt = options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.ReadyForRuntimeSwitch
            || options.WriteFormalPackage
            || approved;
        var blocked = new List<string>();

        if (!options.Enabled)
        {
            blocked.Add("ExplicitScopedRuntimeExperimentProposalDisabled");
        }

        if (!string.Equals(options.Mode, ExplicitScopedRuntimeExperimentProposalModes.ProposalOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedProposalMode");
        }

        if (options.RequireFoundationFreeze
            && (foundationReleaseCandidate is null
                || !foundationReleaseCandidate.FreezePassed
                || !string.Equals(foundationReleaseCandidate.Recommendation, ContextCoreFoundationFreezeRecommendations.ReadyForReleaseCandidate, StringComparison.OrdinalIgnoreCase)
                || reproducibility is null
                || !reproducibility.ReproducibilityPassed))
        {
            blocked.Add("FoundationFreezeOrReproducibilityGateNotPassed");
        }

        if (options.RequireServiceFoundationFreeze
            && (serviceFoundationFreeze is null || !serviceFoundationFreeze.FreezePassed))
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

        if (options.RequireV47DesignFreeze
            && (designFreeze is null
                || !designFreeze.FreezePassed
                || !designFreeze.ReadyForRuntimeExperimentProposal
                || !string.Equals(designFreeze.Recommendation, ScopedRuntimeExperimentDesignFreezeRecommendations.ReadyForRuntimeExperimentProposal, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("ScopedRuntimeExperimentDesignFreezeGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate
            && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(collectionId)
            || string.IsNullOrWhiteSpace(evalScopeId))
        {
            blocked.Add("SelectedScopeNotConfigured");
        }

        if (nonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        if (string.IsNullOrWhiteSpace(rollbackPlan))
        {
            blocked.Add("RollbackPlanMissing");
        }

        if (string.IsNullOrWhiteSpace(killSwitchPlan))
        {
            blocked.Add("KillSwitchPlanMissing");
        }

        if (!approvalRequired)
        {
            blocked.Add("ManualApprovalNotRequired");
        }

        if (approved)
        {
            blocked.Add("AutomaticApprovalAttempt");
        }

        if (runtimeSwitchAttempt)
        {
            blocked.Add("RuntimeSwitchAttempt");
        }

        var observationPlan = BuildObservationPlan();
        if (observationPlan.Count == 0)
        {
            blocked.Add("ObservationPlanMissing");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;

        return new ExplicitScopedRuntimeExperimentProposalReport
        {
            OperationId = $"vector-scoped-runtime-experiment-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = proposalId,
            ProposalPassed = passed,
            Recommendation = passed
                ? ExplicitScopedRuntimeExperimentProposalRecommendations.ReadyForManualExperimentApproval
                : ResolveRecommendation(distinctBlocked),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EvalScopeId = evalScopeId,
            ProfileName = profileName,
            RequiredGateSummary = BuildGateSummary(
                foundationReleaseCandidate,
                reproducibility,
                serviceFoundationFreeze,
                vectorFormalPreviewFreeze,
                designFreeze,
                runtimeChangeGate),
            ProposedConfigPatch = BuildConfigPatchPreview(
                workspaceId,
                collectionId,
                evalScopeId,
                profileName,
                rollbackPlan,
                killSwitchPlan),
            RollbackPlan = rollbackPlan,
            KillSwitchPlan = killSwitchPlan,
            ObservationPlan = observationPlan,
            ApprovalRequired = approvalRequired,
            Approved = false,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            WriteFormalPackage = false,
            ConfigPatchWritten = false,
            DiBindingChanged = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            ForbiddenActions =
            [
                "ModifyAppsettingsRuntimeConfig",
                "ModifyDIBinding",
                "FormalIVectorIndexStoreBinding",
                "FormalPackageWrite",
                "PackingPolicyMutation",
                "PackageOutputMutation",
                "RuntimeSwitch",
                "GlobalDefaultOn",
                "NonAllowlistedScopeUse"
            ],
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyDictionary<string, string> BuildGateSummary(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        FoundationReproducibilityReport? reproducibility,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["foundation-release-candidate-gate"] = foundationReleaseCandidate is null
                ? "Missing"
                : $"{foundationReleaseCandidate.FreezePassed}:{foundationReleaseCandidate.Recommendation}",
            ["foundation-reproducibility-check"] = reproducibility is null
                ? "Missing"
                : $"{reproducibility.ReproducibilityPassed}:{reproducibility.Recommendation}",
            ["service-foundation-freeze-gate"] = serviceFoundationFreeze is null
                ? "Missing"
                : $"{serviceFoundationFreeze.FreezePassed}:{serviceFoundationFreeze.Recommendation}",
            ["vector-formal-preview-freeze-gate"] = vectorFormalPreviewFreeze is null
                ? "Missing"
                : $"{vectorFormalPreviewFreeze.FreezePassed}:{vectorFormalPreviewFreeze.Recommendation}",
            ["vector-scoped-runtime-experiment-design-freeze-gate"] = designFreeze is null
                ? "Missing"
                : $"{designFreeze.FreezePassed}:{designFreeze.Recommendation}",
            ["learning-runtime-change-readiness-gate"] = runtimeChangeGate is null
                ? "Missing"
                : $"{runtimeChangeGate.Passed}:{runtimeChangeGate.Recommendation}"
        };

    private static IReadOnlyDictionary<string, string> BuildConfigPatchPreview(
        string workspaceId,
        string collectionId,
        string evalScopeId,
        string profileName,
        string rollbackPlan,
        string killSwitchPlan)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["previewOnly"] = "true",
            ["writeTarget"] = "none",
            ["workspaceAllowlist"] = workspaceId,
            ["collectionAllowlist"] = collectionId,
            ["evalScopeAllowlist"] = evalScopeId,
            ["profileName"] = profileName,
            ["observationWindow"] = "manual-approval-required-before-any-runtime-change",
            ["traceOutputPath"] = "vector/v4/scoped-runtime-experiment-traces.jsonl",
            ["rollbackInstruction"] = rollbackPlan,
            ["killSwitchInstruction"] = killSwitchPlan,
            ["useForRuntime"] = "false",
            ["formalRetrievalAllowed"] = "false",
            ["readyForRuntimeSwitch"] = "false",
            ["writeFormalPackage"] = "false"
        };

    private static IReadOnlyDictionary<string, string> BuildObservationPlan()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RequestCount"] = "collect",
            ["PreviewPackageCount"] = "collect",
            ["BaselinePackageCount"] = "collect",
            ["CandidateAddCount"] = "collect",
            ["CandidateRemoveCount"] = "collect",
            ["TokenDeltaTotal"] = "collect",
            ["TokenDeltaMax"] = "collect",
            ["RiskAfterPolicy"] = "mustRemainZero",
            ["MustNotHitRiskAfterPolicy"] = "mustRemainZero",
            ["LifecycleRiskAfterPolicy"] = "mustRemainZero",
            ["FormalOutputChanged"] = "mustRemainZero",
            ["PackageOutputChanged"] = "mustRemainFalse",
            ["PackingPolicyChanged"] = "mustRemainFalse",
            ["RuntimeMutated"] = "mustRemainFalse",
            ["ScopeLeakCount"] = "mustRemainZero",
            ["LatencyP50"] = "collect",
            ["LatencyP95"] = "collect",
            ["ErrorCount"] = "collect",
            ["RollbackVerified"] = "required"
        };

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("SelectedScopeNotConfigured", StringComparer.OrdinalIgnoreCase))
        {
            return ExplicitScopedRuntimeExperimentProposalRecommendations.NeedsScopeConfiguration;
        }

        if (blocked.Any(static reason => reason.Contains("RollbackPlan", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByMissingRollbackPlan;
        }

        if (blocked.Any(static reason => reason.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByMissingKillSwitch;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeSwitch", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("AutomaticApproval", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("ManualApproval", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByRuntimeSwitchAttempt;
        }

        if (blocked.Any(static reason => reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Freeze", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Reproducibility", StringComparison.OrdinalIgnoreCase)))
        {
            return ExplicitScopedRuntimeExperimentProposalRecommendations.BlockedByMissingGate;
        }

        if (blocked.Contains("ExplicitScopedRuntimeExperimentProposalDisabled", StringComparer.OrdinalIgnoreCase))
        {
            return ExplicitScopedRuntimeExperimentProposalRecommendations.KeepPreviewOnly;
        }

        return ExplicitScopedRuntimeExperimentProposalRecommendations.KeepPreviewOnly;
    }

    private static string BuildStableProposalId(
        string workspaceId,
        string collectionId,
        string evalScopeId,
        string profileName)
    {
        var input = $"{workspaceId}|{collectionId}|{evalScopeId}|{profileName}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"vsrep-{Convert.ToHexString(bytes).ToLowerInvariant()[..16]}";
    }

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

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
