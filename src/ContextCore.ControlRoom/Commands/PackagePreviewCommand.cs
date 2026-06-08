using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>构建并预览上下文包内容的命令。</summary>
public static class PackagePreviewCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var tokenBudget = CommandHelpers.GetIntOption(args, "--token-budget", 1200);
        tokenBudget = CommandHelpers.GetIntOption(args, "--budget", tokenBudget);
        var usePolicy = !CommandHelpers.HasFlag(args, "--legacy");
        var policyId = CommandHelpers.GetOption(args, "--policy");

        var preview = await service.BuildPackagePreviewDetailsAsync(tokenBudget, usePolicy, policyId, cancellationToken)
            .ConfigureAwait(false);

        DetailRenderer.RenderPackage(preview.Package);

        if (preview.Budget.TokenBudget > 0 || preview.Budget.Sections.Count > 0)
        {
            TableRenderer.Render(
                "预算使用",
                ["Section", "Allocated", "Used", "Usage"],
                preview.Budget.Sections.Select(item => new[]
                {
                    item.SectionName,
                    item.AllocatedTokens.ToString(),
                    item.UsedTokens.ToString(),
                    item.UsageRatio.ToString("0.00")
                }).ToArray());
        }

        if (preview.Uncertainties.Count > 0)
        {
            TableRenderer.Render(
                "不确定性",
                ["Code", "Severity", "Section", "Message"],
                [.. preview.Uncertainties.Select(item => new[]
                {
                    item.Code,
                    item.Severity,
                    item.SectionName,
                    item.Message
                })]);
        }

        TableRenderer.Render(
            "Attention Rerank Status",
            ["Mode", "Profile", "Applied", "SelectedSet", "OrderChanges", "GuardViolation"],
            [
                [
                    preview.AttentionRerankComparison.AttentionRerankMode,
                    preview.AttentionRerankComparison.AttentionProfile,
                    FormatBool(preview.AttentionRerankComparison.AttentionApplied),
                    FormatBool(preview.AttentionRerankComparison.SelectedSetPreserved),
                    preview.AttentionRerankComparison.OrderChangedCount.ToString(),
                    preview.AttentionRerankComparison.GuardViolation
                ]
            ]);

        TableRenderer.Render(
            "Planning Execution Status",
            ["Mode", "Intent", "Status", "OptIn", "FallbackUsed", "FallbackReason"],
            [
                [
                    preview.PlanningMetadata.GetValueOrDefault("planningMode", "Off"),
                    preview.PlanningMetadata.GetValueOrDefault("planningIntent", ""),
                    preview.PlanningMetadata.GetValueOrDefault("planningExecutionStatus", "Legacy"),
                    preview.PlanningMetadata.GetValueOrDefault("planningOptInMatched", "false"),
                    preview.PlanningMetadata.GetValueOrDefault("planningFallbackUsed", "false"),
                    preview.PlanningMetadata.GetValueOrDefault("planningFallbackReason", "")
                ]
            ]);

        TableRenderer.Render(
            "Selected Items",
            ["Id", "Kind", "Type", "Section", "Score", "Tokens", "Reason"],
            [.. preview.SelectedItems.Select(item => new[]
            {
                item.Id,
                item.Kind,
                item.Type,
                item.SectionName,
                item.Score.ToString("0.00"),
                item.EstimatedTokens.ToString(),
                item.Reason
            })]);

        TableRenderer.Render(
            "Dropped / Not Selected Items",
            ["Id", "Kind", "Type", "Score", "Tokens", "Reason"],
            [.. preview.DroppedItems.Select(item => new[]
            {
                item.Id,
                item.Kind,
                item.Type,
                item.Score.ToString("0.00"),
                item.EstimatedTokens.ToString(),
                item.Reason
            })]);
    }

    private static string FormatBool(bool value)
    {
        return value ? "yes" : "no";
    }
}
