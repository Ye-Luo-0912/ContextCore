using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.12 scoped runtime experiment approval gate；只处理 activation preflight 前的人工授权记录。
/// </summary>
public sealed class ScopedRuntimeExperimentRuntimeApprovalRunner
{
    public ScopedRuntimeExperimentApprovalRequestPreviewReport BuildRequestPreview(
        GuardedScopedRuntimeExperimentPlanReport? plan)
    {
        return new ScopedRuntimeExperimentApprovalRequestPreviewReport
        {
            OperationId = $"vector-scoped-runtime-experiment-runtime-approval-preview-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = plan?.ProposalId ?? string.Empty,
            RequiredApprovalMode = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
            SelectedScopes = plan?.SelectedScopes ?? Array.Empty<string>(),
            ProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            RollbackPlan = plan?.RollbackPlan ?? string.Empty,
            KillSwitchPlan = plan?.KillSwitchPlan ?? string.Empty,
            ObservationPlan = plan?.ObservationPlan ?? Array.Empty<string>(),
            StopConditions = plan?.StopConditions ?? Array.Empty<string>(),
            PreviewOnly = true,
            RecordWritten = false,
            Recommendation = plan?.PlanPassed == true
                ? ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval
                : ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly
        };
    }

    public ScopedRuntimeExperimentApprovalReport BuildApproval(
        GuardedScopedRuntimeExperimentPlanReport? plan,
        ScopedRuntimeExperimentApprovalOptions? options,
        bool confirm)
    {
        options ??= new ScopedRuntimeExperimentApprovalOptions
        {
            ApprovalMode = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment
        };
        var proposalId = string.IsNullOrWhiteSpace(options.ProposalId)
            ? plan?.ProposalId ?? string.Empty
            : options.ProposalId.Trim();
        var blocked = new List<string>();
        if (plan is null || !plan.PlanPassed)
        {
            blocked.Add("GuardedRuntimeExperimentPlanNotPassed");
        }
        else if (!string.Equals(plan.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ProposalIdMismatch");
        }

        if (string.IsNullOrWhiteSpace(proposalId))
        {
            blocked.Add("ProposalIdMissing");
        }

        if (string.IsNullOrWhiteSpace(options.ApprovedBy))
        {
            blocked.Add("ApprovedByMissing");
        }

        if (string.IsNullOrWhiteSpace(options.Reason))
        {
            blocked.Add("ApprovalReasonMissing");
        }

        if (!string.Equals(options.ApprovalMode, ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("WrongApprovalMode");
        }

        AddMissingAcknowledgement(blocked, options.RiskAcknowledgement, "RiskAcknowledgementMissing");
        AddMissingAcknowledgement(blocked, options.RollbackAcknowledgement, "RollbackAcknowledgementMissing");
        AddMissingAcknowledgement(blocked, options.KillSwitchAcknowledgement, "KillSwitchAcknowledgementMissing");
        AddMissingAcknowledgement(blocked, options.ScopeAcknowledgement, "ScopeAcknowledgementMissing");
        AddMissingAcknowledgement(blocked, options.ObservationPlanAcknowledgement, "ObservationPlanAcknowledgementMissing");

        if (plan is not null)
        {
            if (plan.SelectedScopes.Count == 0)
            {
                blocked.Add("SelectedScopeMissing");
            }

            if (string.IsNullOrWhiteSpace(plan.RollbackPlan))
            {
                blocked.Add("RollbackPlanMissing");
            }

            if (string.IsNullOrWhiteSpace(plan.KillSwitchPlan))
            {
                blocked.Add("KillSwitchPlanMissing");
            }

            if (plan.ObservationPlan.Count == 0)
            {
                blocked.Add("ObservationPlanMissing");
            }
        }

        if (options.AllowRuntimeSwitch || options.AllowFormalRetrieval || options.AllowFormalPackageWrite || options.AllowPackingPolicyChange)
        {
            blocked.Add("RuntimeSwitchAttempt");
        }

        if (options.RequireExplicitConfirm && !confirm)
        {
            blocked.Add("ExplicitConfirmMissing");
        }

        var distinctBlocked = Distinct(blocked);
        var passed = distinctBlocked.Length == 0;
        var approvedBy = Clean(options.ApprovedBy);
        var approvalId = BuildStableApprovalId(proposalId, approvedBy, ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment);
        var record = new ScopedRuntimeExperimentApprovalRecord
        {
            ApprovalId = approvalId,
            ProposalId = proposalId,
            ApprovedBy = approvedBy,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovalScope = plan?.SelectedScopes.FirstOrDefault() ?? string.Empty,
            ApprovalMode = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
            Reason = Clean(options.Reason),
            RiskAcknowledgement = Clean(options.RiskAcknowledgement),
            RollbackAcknowledgement = Clean(options.RollbackAcknowledgement),
            KillSwitchAcknowledgement = Clean(options.KillSwitchAcknowledgement),
            ScopeAcknowledgement = Clean(options.ScopeAcknowledgement),
            ObservationPlanAcknowledgement = Clean(options.ObservationPlanAcknowledgement),
            ExpiresAt = options.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(3),
            Revoked = false,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "scoped-runtime-experiment-runtime-approval-service/v1",
                ["approvalMode"] = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
                ["useForRuntime"] = "false",
                ["formalRetrievalAllowed"] = "false",
                ["runtimeSwitchAllowed"] = "false",
                ["readyForRuntimeSwitch"] = "false",
                ["formalPackageWriteAllowed"] = "false",
                ["packingPolicyIntegrationAllowed"] = "false"
            }
        };

        return new ScopedRuntimeExperimentApprovalReport
        {
            OperationId = $"vector-scoped-runtime-experiment-runtime-approval-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = proposalId,
            ApprovalId = approvalId,
            ApprovalPassed = passed,
            PreviewOnly = !passed || !confirm,
            RecordWritten = passed && confirm,
            Confirmed = confirm,
            ApprovalMode = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment,
            ApprovedBy = approvedBy,
            RollbackPlanAvailable = !string.IsNullOrWhiteSpace(plan?.RollbackPlan),
            KillSwitchPlanAvailable = !string.IsNullOrWhiteSpace(plan?.KillSwitchPlan),
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalPackageWriteAllowed = false,
            PackingPolicyChangeAllowed = false,
            Recommendation = passed
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForActivationPreflight
                : ResolveRecommendation(distinctBlocked),
            ApprovalRecord = record,
            BlockedReasons = distinctBlocked
        };
    }

    public ScopedRuntimeExperimentApprovalGateReport BuildGate(
        GuardedScopedRuntimeExperimentPlanReport? plan,
        ScopedRuntimeExperimentApprovalRecord? approval)
    {
        var blocked = new List<string>();
        if (plan is null || !plan.PlanPassed)
        {
            blocked.Add("GuardedRuntimeExperimentPlanNotPassed");
        }

        if (approval is null)
        {
            blocked.Add("ApprovalRecordMissing");
        }
        else
        {
            if (plan is not null && !string.Equals(plan.ProposalId, approval.ProposalId, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("ApprovalProposalMismatch");
            }

            if (!string.Equals(approval.ApprovalMode, ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("WrongApprovalMode");
            }

            if (approval.ExpiresAt.HasValue && approval.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                blocked.Add("ApprovalExpired");
            }

            if (approval.Revoked)
            {
                blocked.Add("ApprovalRevoked");
            }

            if (!RequiredAcknowledgementsPresent(approval))
            {
                blocked.Add("RequiredAcknowledgementMissing");
            }

            if (plan is not null && plan.SelectedScopes.Count > 0
                && !plan.SelectedScopes.Contains(approval.ApprovalScope, StringComparer.OrdinalIgnoreCase))
            {
                blocked.Add("ApprovalScopeMismatch");
            }
        }

        if (plan is not null)
        {
            if (string.IsNullOrWhiteSpace(plan.RollbackPlan))
            {
                blocked.Add("RollbackPlanMissing");
            }

            if (string.IsNullOrWhiteSpace(plan.KillSwitchPlan))
            {
                blocked.Add("KillSwitchPlanMissing");
            }

            if (plan.ObservationPlan.Count == 0)
            {
                blocked.Add("ObservationPlanMissing");
            }

            if (plan.RuntimeSwitchAllowed || plan.FormalRetrievalAllowed || plan.ReadyForRuntimeSwitch || plan.UseForRuntime)
            {
                blocked.Add("RuntimeSwitchAttempt");
            }
        }

        var distinctBlocked = Distinct(blocked);
        var passed = distinctBlocked.Length == 0;
        return new ScopedRuntimeExperimentApprovalGateReport
        {
            OperationId = $"vector-scoped-runtime-experiment-runtime-approval-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            GatePassed = passed,
            Recommendation = passed
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForActivationPreflight
                : ResolveRecommendation(distinctBlocked),
            ProposalId = plan?.ProposalId ?? approval?.ProposalId ?? string.Empty,
            ApprovalId = approval?.ApprovalId ?? string.Empty,
            ApprovalMode = approval?.ApprovalMode ?? string.Empty,
            ApprovedBy = approval?.ApprovedBy ?? string.Empty,
            ApprovalExists = approval is not null,
            ApprovalExpired = approval?.ExpiresAt.HasValue == true && approval.ExpiresAt.Value <= DateTimeOffset.UtcNow,
            ApprovalRevoked = approval?.Revoked ?? false,
            RequiredAcknowledgementsPresent = approval is not null && RequiredAcknowledgementsPresent(approval),
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalPackageWriteAllowed = false,
            PackingPolicyIntegrationAllowed = false,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildRequestPreviewMarkdown(ScopedRuntimeExperimentApprovalRequestPreviewReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Scoped Runtime Experiment Approval Request Preview");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- RequiredApprovalMode: `{report.RequiredApprovalMode}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- RecordWritten: `{report.RecordWritten}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Selected Scopes", report.SelectedScopes);
        builder.AppendLine();
        builder.AppendLine("## Rollback Plan");
        builder.AppendLine($"`{report.RollbackPlan}`");
        builder.AppendLine();
        builder.AppendLine("## Kill Switch Plan");
        builder.AppendLine($"`{report.KillSwitchPlan}`");
        AppendList(builder, "Observation Plan", report.ObservationPlan);
        AppendList(builder, "Stop Conditions", report.StopConditions);
        return builder.ToString();
    }

    public static string BuildApprovalMarkdown(string title, ScopedRuntimeExperimentApprovalReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- ApprovalPassed: `{report.ApprovalPassed}`");
        builder.AppendLine($"- RecordWritten: `{report.RecordWritten}`");
        builder.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        builder.AppendLine($"- ApprovedBy: `{report.ApprovedBy}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("V4.12 approval 只允许进入 activation preflight，不授权 runtime switch。");
        return builder.ToString();
    }

    public static string BuildGateMarkdown(string title, ScopedRuntimeExperimentApprovalGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        builder.AppendLine($"- ApprovedBy: `{report.ApprovedBy}`");
        builder.AppendLine($"- ApprovalExists: `{report.ApprovalExists}`");
        builder.AppendLine($"- ApprovalExpired: `{report.ApprovalExpired}`");
        builder.AppendLine($"- ApprovalRevoked: `{report.ApprovalRevoked}`");
        builder.AppendLine($"- RequiredAcknowledgementsPresent: `{report.RequiredAcknowledgementsPresent}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalPackageWriteAllowed: `{report.FormalPackageWriteAllowed}`");
        builder.AppendLine($"- PackingPolicyIntegrationAllowed: `{report.PackingPolicyIntegrationAllowed}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("ScopedRuntimeExperiment approval 不等于 runtime switch；下一阶段仅允许 V4.13 activation preflight。");
        return builder.ToString();
    }

    private static bool RequiredAcknowledgementsPresent(ScopedRuntimeExperimentApprovalRecord record)
        => !string.IsNullOrWhiteSpace(record.RiskAcknowledgement)
            && !string.IsNullOrWhiteSpace(record.RollbackAcknowledgement)
            && !string.IsNullOrWhiteSpace(record.KillSwitchAcknowledgement)
            && !string.IsNullOrWhiteSpace(record.ScopeAcknowledgement)
            && !string.IsNullOrWhiteSpace(record.ObservationPlanAcknowledgement);

    private static void AddMissingAcknowledgement(List<string> blocked, string value, string reason)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            blocked.Add(reason);
        }
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("ApprovalRecordMissing", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingApproval;
        }

        if (blocked.Any(static reason => reason.Contains("Expired", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByExpiredApproval;
        }

        if (blocked.Any(static reason => reason.Contains("Revoked", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByRevokedApproval;
        }

        if (blocked.Any(static reason => reason.Contains("WrongApprovalMode", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByWrongApprovalMode;
        }

        if (blocked.Any(static reason => reason.Contains("Acknowledgement", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingAcknowledgement;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeSwitchAttempt", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByRuntimeSwitchAttempt;
        }

        if (blocked.Any(static reason => reason.Contains("ApprovedBy", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Reason", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Confirm", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval;
        }

        return ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;
    }

    private static string BuildStableApprovalId(string proposalId, string approvedBy, string mode)
    {
        var input = $"{proposalId}|{approvedBy}|{mode}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"vsrea-{Convert.ToHexString(bytes).ToLowerInvariant()[..16]}";
    }

    private static string[] Distinct(IEnumerable<string> values)
        => values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();

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
}
