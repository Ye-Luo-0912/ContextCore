using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 受控应用合并批准 + 范围预览。生成批准记录并基于 V6.15 决策计算范围预览 selected set。
/// 不改 formal selected set、不写 formal package、不改 PackingPolicy/package output、不切 runtime。
/// </summary>
public sealed class ControlledAppliedMergeApprovalRunner
{
    public ControlledAppliedMergeApprovalReport BuildApproval(
        ControlledAppliedMergeDryRunObservationReport? dryRunDecision,
        string approvedBy, string reason, bool confirm,
        ControlledAppliedMergeApprovalOptions? options = null)
    {
        options ??= new ControlledAppliedMergeApprovalOptions();
        var blocked = new List<string>();

        if (dryRunDecision is null) blocked.Add("DryRunDecisionMissing");
        else if (!dryRunDecision.ObservationPassed) blocked.Add("DryRunDecisionNotPassed");

        if (!confirm) blocked.Add("RequiresConfirmFlag");
        if (string.IsNullOrWhiteSpace(approvedBy)) blocked.Add("MissingApprovedBy");

        var proposalId = dryRunDecision?.OperationId ?? "unknown";
        var blk = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var passed = blk.Length == 0;

        return new ControlledAppliedMergeApprovalReport
        {
            OperationId = $"controlled-merge-approval-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ApprovalPassed = passed,
            Recommendation = passed
                ? ControlledAppliedMergeApprovalRecommendations.ReadyForScopedPreview
                : ControlledAppliedMergeApprovalRecommendations.KeepPreviewOnly,
            ProposalId = proposalId,
            ApprovedBy = approvedBy,
            Reason = reason,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(options.ExpiresInDays),
            ApprovalMode = "ControlledAppliedMergePreviewOnly",
            WouldApplyAddCount = dryRunDecision?.WouldApplyAddCount ?? 0,
            WouldApplyRemoveCount = dryRunDecision?.WouldApplyRemoveCount ?? 0,
            RiskAfterPolicy = dryRunDecision?.RiskAfterPolicy ?? 0,
            RollbackPresent = dryRunDecision?.RollbackPassed ?? false,
            KillSwitchPresent = dryRunDecision?.KillSwitchTested ?? false,
            IsRevoked = false,
            BlockedReasons = blk
        };
    }

    public ControlledAppliedMergeScopedPreviewReport BuildScopedPreview(
        ControlledAppliedMergeApprovalReport? approval,
        ControlledAppliedMergeDryRunObservationReport? dryRunDecision,
        ControlledAppliedMergeScopedPreviewOptions? options = null)
    {
        options ??= new ControlledAppliedMergeScopedPreviewOptions();
        var blocked = new List<string>();

        if (approval is null) blocked.Add("ApprovalMissing");
        else if (approval.IsRevoked || DateTimeOffset.UtcNow > approval.ExpiresAt) blocked.Add("ApprovalExpiredOrRevoked");
        if (dryRunDecision is null) blocked.Add("DryRunDecisionMissing");

        // 读取 would-apply 量作为 preview add/remove
        var previewAdd = dryRunDecision?.WouldApplyAddCount ?? 0;
        var previewRemove = dryRunDecision?.WouldApplyRemoveCount ?? 0;

        // preview selected set 发生变化 → PreviewSelectedSetChanged=true
        var previewChanged = previewAdd > 0 || previewRemove > 0;
        if (!previewChanged) blocked.Add("PreviewSelectedSetUnchanged");

        var risk = dryRunDecision?.RiskAfterPolicy ?? 0;
        if (risk > 0) blocked.Add("RiskAfterPolicy");

        var blk = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var passed = blk.Length == 0;

        return new ControlledAppliedMergeScopedPreviewReport
        {
            OperationId = $"controlled-merge-scoped-preview-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? ControlledAppliedMergeScopedPreviewRecommendations.ReadyForControlledAppliedMergeScopedPreviewGate
                : ControlledAppliedMergeScopedPreviewRecommendations.BlockedByMissingApproval,
            PreviewSelectedSetChanged = previewChanged,
            PreviewAddCount = previewAdd,
            PreviewRemoveCount = previewRemove,
            AppliedFormalAddCount = 0,
            AppliedFormalRemoveCount = 0,
            FormalSelectedSetChanged = false,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            RollbackPresent = approval?.RollbackPresent ?? false,
            KillSwitchPresent = approval?.KillSwitchPresent ?? false,
            BlockedReasons = blk
        };
    }

    public static string BuildApprovalMarkdown(string title, ControlledAppliedMergeApprovalReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"- ApprovalPassed: `{r.ApprovalPassed}`");
        b.AppendLine($"- ProposalId: `{r.ProposalId}`");
        b.AppendLine($"- ApprovedBy: `{r.ApprovedBy}`");
        b.AppendLine($"- Reason: `{r.Reason}`");
        b.AppendLine($"- ExpiresAt: `{r.ExpiresAt:O}`");
        b.AppendLine($"- Mode: `{r.ApprovalMode}`");
        b.AppendLine($"- WouldApplyAdd/Remove: `{r.WouldApplyAddCount}/{r.WouldApplyRemoveCount}`");
        b.AppendLine($"- Risk: `{r.RiskAfterPolicy}` Rollback/KillSwitch: `{r.RollbackPresent}/{r.KillSwitchPresent}`");
        AppendList(b, "Blocked", r.BlockedReasons);
        b.AppendLine(); b.AppendLine("V6.16 approval only. No formal selected set change, no formal package write, no runtime switch.");
        return b.ToString();
    }

    public static string BuildScopedPreviewMarkdown(string title, ControlledAppliedMergeScopedPreviewReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"- PreviewPassed: `{r.PreviewPassed}`");
        b.AppendLine($"- PreviewAdd/Remove: `{r.PreviewAddCount}/{r.PreviewRemoveCount}`");
        b.AppendLine($"- PreviewSelectedSetChanged: `{r.PreviewSelectedSetChanged}`");
        b.AppendLine($"- AppliedFormalAdd/Remove: `{r.AppliedFormalAddCount}/{r.AppliedFormalRemoveCount}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{r.FormalSelectedSetChanged}`");
        b.AppendLine($"- Risk: `{r.RiskAfterPolicy}`");
        b.AppendLine($"- Rollback/KillSwitch: `{r.RollbackPresent}/{r.KillSwitchPresent}`");
        AppendList(b, "Blocked", r.BlockedReasons);
        b.AppendLine(); b.AppendLine("V6.16 scoped preview only. No formal selected set change, no formal package write, no runtime switch.");
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string t, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {t}");
        if (items.Count == 0) b.AppendLine("- (empty)");
        else foreach (var i in items) b.AppendLine($"- `{i}`");
    }
}

public sealed class ControlledAppliedMergeApprovalOptions
{
    public int ExpiresInDays { get; init; } = 7;
}

public sealed class ControlledAppliedMergeScopedPreviewOptions
{
    public int PreviewTopK { get; init; } = 10;
    public int MaxPreviewAddPerSample { get; init; } = 5;
    public int MaxPreviewRemovePerSample { get; init; } = 5;
}