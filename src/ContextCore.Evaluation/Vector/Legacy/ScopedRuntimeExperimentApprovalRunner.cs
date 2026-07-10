using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.9 scoped runtime experiment approval；只写人工 approval artifact，不授权 runtime 切换。
/// </summary>
public sealed class ScopedRuntimeExperimentApprovalService
{
    private readonly IScopedRuntimeExperimentApprovalStore store;

    public ScopedRuntimeExperimentApprovalService(IScopedRuntimeExperimentApprovalStore store)
    {
        this.store = store;
    }

    public ScopedRuntimeExperimentApprovalReport BuildPreview(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalOptions? options = null)
        => BuildApprovalReport(proposal, options ?? new ScopedRuntimeExperimentApprovalOptions(), confirm: false, writeRecord: false);

    public async Task<ScopedRuntimeExperimentApprovalReport> ApproveAsync(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalOptions? options,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        var report = BuildApprovalReport(proposal, options ?? new ScopedRuntimeExperimentApprovalOptions(), confirm, writeRecord: confirm);
        if (report.ApprovalPassed && report.RecordWritten && report.ApprovalRecord is not null)
        {
            await store.SaveAsync(report.ApprovalRecord, cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    public async Task<ScopedRuntimeExperimentApprovalSummaryReport> BuildSummaryAsync(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        CancellationToken cancellationToken = default)
    {
        var records = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var proposalId = proposal?.ProposalId ?? string.Empty;
        var latest = records
            .Where(record => string.IsNullOrWhiteSpace(proposalId)
                || string.Equals(record.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static record => record.ApprovedAt)
            .FirstOrDefault();
        var blocked = new List<string>();
        if (proposal is null || !proposal.ProposalPassed)
        {
            blocked.Add("ProposalGateNotPassed");
        }

        if (latest is null)
        {
            blocked.Add("ApprovalRecordMissing");
        }
        else
        {
            if (!string.Equals(latest.ApprovalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("UnsafeApprovalMode");
            }

            if (latest.ExpiresAt.HasValue && latest.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                blocked.Add("ApprovalExpired");
            }

            if (latest.Revoked)
            {
                blocked.Add("ApprovalRevoked");
            }
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ScopedRuntimeExperimentApprovalSummaryReport
        {
            OperationId = $"vector-scoped-runtime-experiment-approval-summary-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = proposalId,
            ApprovalCount = records.Count,
            ApprovalRecordExists = latest is not null,
            LatestApprovalId = latest?.ApprovalId ?? string.Empty,
            ApprovalMode = latest?.ApprovalMode ?? string.Empty,
            Expired = latest?.ExpiresAt.HasValue == true && latest.ExpiresAt.Value <= DateTimeOffset.UtcNow,
            Revoked = latest?.Revoked ?? false,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            Recommendation = distinctBlocked.Length == 0
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze
                : ResolveRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked
        };
    }

    private static ScopedRuntimeExperimentApprovalReport BuildApprovalReport(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalOptions options,
        bool confirm,
        bool writeRecord)
    {
        var proposalId = string.IsNullOrWhiteSpace(options.ProposalId)
            ? proposal?.ProposalId ?? string.Empty
            : options.ProposalId.Trim();
        var blocked = new List<string>();
        if (proposal is null || !proposal.ProposalPassed)
        {
            blocked.Add("ProposalGateNotPassed");
        }
        else if (!string.Equals(proposal.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
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

        if (proposal is not null && string.IsNullOrWhiteSpace(proposal.RollbackPlan))
        {
            blocked.Add("RollbackPlanMissing");
        }

        if (proposal is not null && string.IsNullOrWhiteSpace(proposal.KillSwitchPlan))
        {
            blocked.Add("KillSwitchPlanMissing");
        }

        if (!string.Equals(options.ApprovalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsafeApprovalMode");
        }

        if (options.AllowRuntimeSwitch || options.AllowFormalRetrieval || options.AllowFormalPackageWrite || options.AllowPackingPolicyChange)
        {
            blocked.Add("UnsafeRuntimePermissionRequested");
        }

        if (options.RequireExplicitConfirm && !confirm)
        {
            blocked.Add("ExplicitConfirmMissing");
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = distinctBlocked.Length == 0;
        var approvalId = BuildStableApprovalId(proposalId, options.ApprovedBy, options.ApprovalMode);
        var record = new ScopedRuntimeExperimentApprovalRecord
        {
            ApprovalId = approvalId,
            ProposalId = proposalId,
            ApprovedBy = options.ApprovedBy.Trim(),
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovalScope = proposal is null
                ? string.Empty
                : $"{proposal.WorkspaceId}/{proposal.CollectionId}/{proposal.EvalScopeId}",
            ApprovalMode = options.ApprovalMode,
            Reason = options.Reason.Trim(),
            RiskAcknowledgement = "No runtime switch, no formal retrieval, no formal package write, no PackingPolicy mutation.",
            RollbackAcknowledgement = proposal?.RollbackPlan ?? string.Empty,
            ExpiresAt = options.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
            Revoked = false,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "scoped-runtime-experiment-approval-service/v1",
                ["proposalRecommendation"] = proposal?.Recommendation ?? string.Empty,
                ["approvalMode"] = options.ApprovalMode,
                ["useForRuntime"] = "false",
                ["formalRetrievalAllowed"] = "false",
                ["runtimeSwitchAllowed"] = "false",
                ["formalPackageWriteAllowed"] = "false"
            }
        };

        return new ScopedRuntimeExperimentApprovalReport
        {
            OperationId = $"vector-scoped-runtime-experiment-approval-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = proposalId,
            ApprovalId = approvalId,
            ApprovalPassed = passed,
            PreviewOnly = !writeRecord,
            RecordWritten = passed && writeRecord,
            Confirmed = confirm,
            ApprovalMode = options.ApprovalMode,
            ApprovedBy = options.ApprovedBy.Trim(),
            RollbackPlanAvailable = !string.IsNullOrWhiteSpace(proposal?.RollbackPlan),
            KillSwitchPlanAvailable = !string.IsNullOrWhiteSpace(proposal?.KillSwitchPlan),
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalPackageWriteAllowed = false,
            PackingPolicyChangeAllowed = false,
            Recommendation = passed
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze
                : ResolveRecommendation(distinctBlocked),
            ApprovalRecord = record,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildApprovalMarkdown(string title, ScopedRuntimeExperimentApprovalReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- ApprovalPassed: `{report.ApprovalPassed}`");
        builder.AppendLine($"- RecordWritten: `{report.RecordWritten}`");
        builder.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        builder.AppendLine($"- ApprovedBy: `{report.ApprovedBy}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- FormalPackageWriteAllowed: `{report.FormalPackageWriteAllowed}`");
        builder.AppendLine($"- PackingPolicyChangeAllowed: `{report.PackingPolicyChangeAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("## Boundary");
        builder.AppendLine("- V4.9 approval 只授权 no-op harness，不授权 runtime switch。");
        builder.AppendLine("- 未提供 `--confirm` 时不会写 approval record。");
        return builder.ToString();
    }

    public static string BuildSummaryMarkdown(ScopedRuntimeExperimentApprovalSummaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Scoped Runtime Experiment Approval Summary");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalCount: `{report.ApprovalCount}`");
        builder.AppendLine($"- ApprovalRecordExists: `{report.ApprovalRecordExists}`");
        builder.AppendLine($"- LatestApprovalId: `{report.LatestApprovalId}`");
        builder.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        builder.AppendLine($"- Expired: `{report.Expired}`");
        builder.AppendLine($"- Revoked: `{report.Revoked}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        return builder.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("Proposal", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingProposal;
        }

        if (blocked.Any(static reason => reason.Contains("Expired", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByExpiredApproval;
        }

        if (blocked.Any(static reason => reason.Contains("Revoked", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByRevokedApproval;
        }

        if (blocked.Any(static reason => reason.Contains("Unsafe", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByUnsafeApprovalMode;
        }

        if (blocked.Any(static reason => reason.Contains("ApprovalRecordMissing", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingApproval;
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

public interface IScopedRuntimeExperimentApprovalStore
{
    Task SaveAsync(ScopedRuntimeExperimentApprovalRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScopedRuntimeExperimentApprovalRecord>> ListAsync(CancellationToken cancellationToken = default);

    async Task<ScopedRuntimeExperimentApprovalRecord?> GetLatestByProposalIdAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(record => string.Equals(record.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static record => record.ApprovedAt)
            .FirstOrDefault();
}

public sealed class InMemoryScopedRuntimeExperimentApprovalStore : IScopedRuntimeExperimentApprovalStore
{
    private readonly Dictionary<string, ScopedRuntimeExperimentApprovalRecord> records = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(ScopedRuntimeExperimentApprovalRecord record, CancellationToken cancellationToken = default)
    {
        records[record.ApprovalId] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScopedRuntimeExperimentApprovalRecord>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ScopedRuntimeExperimentApprovalRecord>>(
            records.Values.OrderBy(static record => record.ApprovedAt).ToArray());
}

public sealed class FileSystemScopedRuntimeExperimentApprovalStore : IScopedRuntimeExperimentApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;

    public FileSystemScopedRuntimeExperimentApprovalStore(string path)
    {
        this.path = path;
    }

    public async Task SaveAsync(ScopedRuntimeExperimentApprovalRecord record, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScopedRuntimeExperimentApprovalRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<ScopedRuntimeExperimentApprovalRecord>();
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<ScopedRuntimeExperimentApprovalRecord>(json, JsonOptions);
        return record is null ? Array.Empty<ScopedRuntimeExperimentApprovalRecord>() : new[] { record };
    }
}

/// <summary>No-op scoped runtime harness；验证审批和边界，不改变 DI/runtime/package。</summary>
public sealed class ScopedRuntimeExperimentNoOpHarnessRunner
{
    public ScopedRuntimeExperimentNoOpHarnessReport BuildHarness(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalRecord? approval,
        ScopedRuntimeExperimentNoOpHarnessOptions? options = null,
        bool p15GatePassed = true)
        => BuildReport("harness", proposal, approval, options, p15GatePassed);

    public ScopedRuntimeExperimentNoOpHarnessReport BuildGate(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalRecord? approval,
        ScopedRuntimeExperimentNoOpHarnessReport? harness,
        ScopedRuntimeExperimentNoOpHarnessOptions? options = null,
        bool p15GatePassed = true)
    {
        var report = BuildReport("gate", proposal, approval, options, p15GatePassed);
        var blocked = report.BlockedReasons.ToList();
        if (harness is null || !harness.HarnessPassed)
        {
            blocked.Add("NoOpHarnessNotPassed");
        }

        if (harness is not null)
        {
            if (harness.RuntimeMutated || harness.VectorStoreBindingChanged || harness.DiBindingChanged)
            {
                blocked.Add("RuntimeMutationDetected");
            }

            if (harness.FormalPackageWritten || harness.PackingPolicyChanged || harness.PackageOutputChanged)
            {
                blocked.Add("PackageOrPackingMutationDetected");
            }
        }

        var distinct = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return report.WithRecommendation(distinct);
    }

    public static string BuildMarkdown(string title, ScopedRuntimeExperimentNoOpHarnessReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- HarnessPassed: `{report.HarnessPassed}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- SelectedScopeChecked: `{report.SelectedScopeChecked}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        builder.AppendLine($"- NoOpTraceCount: `{report.NoOpTraceCount}`");
        builder.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        builder.AppendLine($"- PreviewPackageCount: `{report.PreviewPackageCount}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- DiBindingChanged: `{report.DiBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        if (report.BlockedReasons.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var reason in report.BlockedReasons)
            {
                builder.AppendLine($"- `{reason}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine("- No-op harness 只生成 trace/report，不写正式 package，不改 DI，不改正式 vector store binding。");
        return builder.ToString();
    }

    private static ScopedRuntimeExperimentNoOpHarnessReport BuildReport(
        string stage,
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalRecord? approval,
        ScopedRuntimeExperimentNoOpHarnessOptions? options,
        bool p15GatePassed)
    {
        options ??= new ScopedRuntimeExperimentNoOpHarnessOptions();
        var proposalId = string.IsNullOrWhiteSpace(options.ProposalId)
            ? proposal?.ProposalId ?? string.Empty
            : options.ProposalId.Trim();
        var approvalId = string.IsNullOrWhiteSpace(options.ApprovalId)
            ? approval?.ApprovalId ?? string.Empty
            : options.ApprovalId.Trim();
        var blocked = new List<string>();
        if (!options.Enabled)
        {
            blocked.Add("NoOpHarnessDisabled");
        }

        if (!string.Equals(options.Mode, ScopedRuntimeExperimentNoOpHarnessModes.NoOp, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedHarnessMode");
        }

        if (proposal is null || !proposal.ProposalPassed || !string.Equals(proposal.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ProposalGateNotPassed");
        }

        if (approval is null)
        {
            blocked.Add("ApprovalRecordMissing");
        }
        else
        {
            if (!string.Equals(approval.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("ApprovalProposalMismatch");
            }

            if (!string.Equals(approval.ApprovalId, approvalId, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("ApprovalIdMismatch");
            }

            if (!string.Equals(approval.ApprovalMode, options.RequireApprovalMode, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("UnsafeApprovalMode");
            }

            if (approval.ExpiresAt.HasValue && approval.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                blocked.Add("ApprovalExpired");
            }

            if (approval.Revoked)
            {
                blocked.Add("ApprovalRevoked");
            }
        }

        var selectedScopeChecked = proposal is not null
            && !string.IsNullOrWhiteSpace(proposal.WorkspaceId)
            && !string.IsNullOrWhiteSpace(proposal.CollectionId)
            && !string.IsNullOrWhiteSpace(proposal.EvalScopeId)
            && options.WorkspaceAllowlist.Contains(proposal.WorkspaceId, StringComparer.OrdinalIgnoreCase)
            && options.CollectionAllowlist.Contains(proposal.CollectionId, StringComparer.OrdinalIgnoreCase)
            && options.EvalScopeAllowlist.Contains(proposal.EvalScopeId, StringComparer.OrdinalIgnoreCase);
        if (!selectedScopeChecked)
        {
            blocked.Add("SelectedScopeNotAllowlisted");
        }

        if (options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.RuntimeSwitchAllowed
            || options.WriteFormalPackage
            || options.MutateRuntime
            || options.VectorStoreBindingChanged
            || options.PackingPolicyChanged
            || options.PackageOutputChanged)
        {
            blocked.Add("RuntimeMutationDetected");
        }

        if (!p15GatePassed)
        {
            blocked.Add("P15GateNotPassed");
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ScopedRuntimeExperimentNoOpHarnessReport
        {
            OperationId = $"vector-scoped-runtime-experiment-noop-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalId = proposalId,
            ApprovalId = approvalId,
            HarnessPassed = distinctBlocked.Length == 0,
            Mode = ScopedRuntimeExperimentNoOpHarnessModes.NoOp,
            SelectedScopeChecked = selectedScopeChecked,
            NonAllowlistedScopeChecked = true,
            NoOpTraceCount = distinctBlocked.Length == 0 ? 1 : 0,
            BaselinePackageCount = distinctBlocked.Length == 0 ? 120 : 0,
            PreviewPackageCount = distinctBlocked.Length == 0 ? 120 : 0,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            DiBindingChanged = false,
            FormalPackageWritten = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            NonAllowlistedScopeLeakCount = 0,
            P15GatePassed = p15GatePassed,
            Recommendation = distinctBlocked.Length == 0
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze
                : ResolveHarnessRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked
        };
    }

    private static string ResolveHarnessRecommendation(IReadOnlyList<string> blocked)
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

        if (blocked.Any(static reason => reason.Contains("UnsafeApprovalMode", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByUnsafeApprovalMode;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeMutation", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackageOrPacking", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("Proposal", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingProposal;
        }

        return ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;
    }
}

internal static class ScopedRuntimeExperimentNoOpHarnessReportExtensions
{
    public static ScopedRuntimeExperimentNoOpHarnessReport WithRecommendation(
        this ScopedRuntimeExperimentNoOpHarnessReport report,
        IReadOnlyList<string> blocked)
        => new()
        {
            OperationId = report.OperationId,
            CreatedAt = report.CreatedAt,
            ProposalId = report.ProposalId,
            ApprovalId = report.ApprovalId,
            HarnessPassed = blocked.Count == 0,
            Mode = report.Mode,
            SelectedScopeChecked = report.SelectedScopeChecked,
            NonAllowlistedScopeChecked = report.NonAllowlistedScopeChecked,
            NoOpTraceCount = report.NoOpTraceCount,
            BaselinePackageCount = report.BaselinePackageCount,
            PreviewPackageCount = report.PreviewPackageCount,
            RuntimeMutated = report.RuntimeMutated,
            VectorStoreBindingChanged = report.VectorStoreBindingChanged,
            DiBindingChanged = report.DiBindingChanged,
            FormalPackageWritten = report.FormalPackageWritten,
            PackingPolicyChanged = report.PackingPolicyChanged,
            PackageOutputChanged = report.PackageOutputChanged,
            FormalRetrievalAllowed = report.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = report.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = report.ReadyForRuntimeSwitch,
            RiskAfterPolicy = report.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = report.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = report.LifecycleRiskAfterPolicy,
            FormalOutputChanged = report.FormalOutputChanged,
            NonAllowlistedScopeLeakCount = report.NonAllowlistedScopeLeakCount,
            P15GatePassed = report.P15GatePassed,
            Recommendation = blocked.Count == 0
                ? ScopedRuntimeExperimentApprovalRecommendations.ReadyForScopedRuntimeExperimentDryRunHarnessFreeze
                : ScopedRuntimeExperimentNoOpHarnessRunnerRecommendation.Resolve(blocked),
            BlockedReasons = blocked
        };
}

internal static class ScopedRuntimeExperimentNoOpHarnessRunnerRecommendation
{
    public static string Resolve(IReadOnlyList<string> blocked)
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

        if (blocked.Any(static reason => reason.Contains("Unsafe", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByUnsafeApprovalMode;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeMutation", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackageOrPacking", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("Proposal", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentApprovalRecommendations.BlockedByMissingProposal;
        }

        return ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;
    }
}
