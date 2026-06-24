using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V6 主线冻结报告。汇总 V6.10–V6.16 proposal、dry-run、approval、scoped preview
/// 链路的最终状态，停止 V6 主线扩张。只读，不改任何 runtime/formal/package 状态。
/// </summary>
public sealed class ControlledAppliedMergePreviewFreezeRunner
{
    public ControlledAppliedMergePreviewFreezeReport BuildFreeze(
        ControlledAppliedMergeScopedPreviewReport? scopedPreviewGate,
        ControlledAppliedMergeApprovalReport? approval,
        ControlledAppliedMergeDryRunObservationReport? dryRunDecision,
        ControlledAppliedMergeProposalReport? proposal)
    {
        var blocked = new List<string>();
        if (scopedPreviewGate is null) blocked.Add("ScopedPreviewGateMissing");
        else if (!scopedPreviewGate.PreviewPassed) blocked.Add("ScopedPreviewGateNotPassed");

        return new ControlledAppliedMergePreviewFreezeReport
        {
            OperationId = $"controlled-merge-preview-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = blocked.Count == 0,
            Recommendation = blocked.Count == 0
                ? ControlledAppliedMergePreviewFreezeRecommendations.ReadyForV6MainlineFreeze
                : ControlledAppliedMergePreviewFreezeRecommendations.BlockedByMissingScopedPreviewGate,
            ProposalAddCount = proposal?.StablePreviewAddCount ?? 0,
            ProposalRemoveCount = proposal?.StablePreviewRemoveCount ?? 0,
            DryRunWouldApplyAdd = dryRunDecision?.WouldApplyAddCount ?? 0,
            DryRunWouldApplyRemove = dryRunDecision?.WouldApplyRemoveCount ?? 0,
            PreviewAddCount = scopedPreviewGate?.PreviewAddCount ?? 0,
            PreviewRemoveCount = scopedPreviewGate?.PreviewRemoveCount ?? 0,
            ApprovalPresent = approval is not null && approval.ApprovalPassed,
            ApprovedBy = approval?.ApprovedBy ?? "",
            V6PhaseCount = 7,
            FrozenArtifacts = new[]
            {
                "vector/v6/controlled-shadow-merge-proposal.json/.md",
                "vector/v6/controlled-shadow-merge-proposal-gate.json/.md",
                "vector/v6/controlled-applied-merge-proposal.json/.md",
                "vector/v6/controlled-applied-merge-proposal-gate.json/.md",
                "vector/v6/controlled-applied-merge-dry-run-observation.json/.md",
                "vector/v6/controlled-applied-merge-dry-run-decision.json/.md",
                "vector/v6/controlled-applied-merge-approval-gate.json/.md",
                "vector/v6/controlled-applied-merge-scoped-preview.json/.md",
                "vector/v6/controlled-applied-merge-scoped-preview-gate.json/.md",
                "vector/v6/controlled-applied-merge-preview-freeze.json/.md",
            },
            BlockedReasons = blocked,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergePreviewFreezeReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"- FreezePassed: `{r.FreezePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- Proposal add/remove: `{r.ProposalAddCount}/{r.ProposalRemoveCount}`");
        b.AppendLine($"- Dry-run would-apply add/remove: `{r.DryRunWouldApplyAdd}/{r.DryRunWouldApplyRemove}`");
        b.AppendLine($"- Preview add/remove: `{r.PreviewAddCount}/{r.PreviewRemoveCount}`");
        b.AppendLine($"- ApprovalPresent: `{r.ApprovalPresent}` (approvedBy: `{r.ApprovedBy}`)");
        b.AppendLine($"- V6 phases frozen: `{r.V6PhaseCount}` (V6.10–V6.16)");
        b.AppendLine(); b.AppendLine("## 已冻结产物");
        foreach (var a in r.FrozenArtifacts) b.AppendLine($"- `{a}`");
        b.AppendLine(); b.AppendLine("V6 主线冻结。仅在 proposal/dry-run/approval/scoped-preview 范围内。不启用 formal retrieval，不写 formal package，不改 PakcingPolicy/package output，不切 runtime。");
        return b.ToString();
    }
}