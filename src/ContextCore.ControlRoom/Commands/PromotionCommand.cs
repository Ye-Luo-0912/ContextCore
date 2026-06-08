using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>Promotion Review 命令，用于查看和更新候选项审核状态。</summary>
public static class PromotionCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (args.Count == 0)
        {
            PrintHelp();
            return;
        }

        var action = args[0].ToLowerInvariant();
        switch (action)
        {
            case "list":
            case "candidates":
                await ListAsync(service, args, cancellationToken).ConfigureAwait(false);
                return;
            case "show":
                await ShowAsync(service, args, cancellationToken).ConfigureAwait(false);
                return;
            case "accept":
                await AcceptAndPromoteAsync(service, args, cancellationToken).ConfigureAwait(false);
                return;
            case "reject":
                await UpdateStatusAsync(service, args, PromotionCandidateStatus.Rejected, "已拒绝候选项。", cancellationToken).ConfigureAwait(false);
                return;
            case "deprecate":
            case "supersede":
                await UpdateStatusAsync(service, args, PromotionCandidateStatus.Superseded, "候选项已被后续信息覆盖。", cancellationToken).ConfigureAwait(false);
                return;
            case "explain":
            case "explain-source":
                await ExplainSourceAsync(service, args, cancellationToken).ConfigureAwait(false);
                return;
            default:
                Console.WriteLine($"未知 promotion 操作：{args[0]}");
                PrintHelp();
                return;
        }
    }

    private static async Task ListAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var status = ParseStatus(CommandHelpers.GetOption(args, "--status"));
        var take = CommandHelpers.GetIntOption(args, "--take", 20);
        var candidates = await service.ListPromotionCandidatesAsync(status, take, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"Promotion 候选项：{candidates.Count}");
        foreach (var candidate in candidates)
        {
            Console.WriteLine(
                $"{candidate.Id} | {candidate.Status} | {candidate.TargetLayer?.ToString() ?? "-"} | {candidate.Category} | {Preview(candidate.Content)}");
        }
    }

    private static async Task ShowAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var id = RequireId(args);
        if (id is null)
        {
            return;
        }

        var candidate = await service.GetPromotionCandidateAsync(id, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            Console.WriteLine($"未找到 Promotion 候选项：{id}");
            return;
        }

        PrintCandidate(candidate);
    }

    private static async Task AcceptAndPromoteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var id = RequireId(args);
        if (id is null)
        {
            return;
        }

        var reviewer = CommandHelpers.GetOption(args, "--reviewer") ?? Environment.UserName;
        var reason = CommandHelpers.GetOption(args, "--reason") ?? "已接受候选项。";
        var (candidate, promotionDetail) = await service.ExecuteAcceptAsync(id, reviewer, reason, cancellationToken)
            .ConfigureAwait(false);

        if (candidate is null)
        {
            Console.WriteLine($"未找到 Promotion 候选项：{id}");
            return;
        }

        Console.WriteLine($"Promotion 候选项已更新：{candidate.Id} -> {candidate.Status}");
        Console.WriteLine($"审核人：{candidate.Reviewer}");
        Console.WriteLine($"原因：{candidate.Reason}");

        if (!string.IsNullOrWhiteSpace(promotionDetail))
        {
            Console.WriteLine(promotionDetail);
        }
    }

    private static async Task UpdateStatusAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        PromotionCandidateStatus status,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        var id = RequireId(args);
        if (id is null)
        {
            return;
        }

        var reviewer = CommandHelpers.GetOption(args, "--reviewer") ?? Environment.UserName;
        var reason = CommandHelpers.GetOption(args, "--reason") ?? defaultReason;
        var candidate = await service.UpdatePromotionCandidateStatusAsync(
            id,
            status,
            reviewer,
            reason,
            cancellationToken).ConfigureAwait(false);

        if (candidate is null)
        {
            Console.WriteLine($"未找到 Promotion 候选项：{id}");
            return;
        }

        Console.WriteLine($"Promotion 候选项已更新：{candidate.Id} -> {candidate.Status}");
        Console.WriteLine($"审核人：{candidate.Reviewer}");
        Console.WriteLine($"原因：{candidate.Reason}");
    }

    private static async Task ExplainSourceAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var id = RequireId(args);
        if (id is null)
        {
            return;
        }

        var candidate = await service.GetPromotionCandidateAsync(id, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            Console.WriteLine($"未找到 Promotion 候选项：{id}");
            return;
        }

        Console.WriteLine($"候选项：{candidate.Id}");
        Console.WriteLine($"来源：{candidate.SourceKind}/{candidate.SourceId}");
        Console.WriteLine($"来源引用：{string.Join(", ", candidate.SourceRefs)}");
        Console.WriteLine($"命中规则：{string.Join(", ", candidate.MatchedRules)}");
        Console.WriteLine($"原因：{candidate.Reason}");

        var detail = await service.ShowAsync(candidate.SourceId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            Console.WriteLine("来源详情：未在当前集合中找到。");
            return;
        }

        Console.WriteLine($"来源标题：{detail.Title}");
        Console.WriteLine($"来源类型：{GetField(detail, "kind")}/{GetField(detail, "type")}");
        Console.WriteLine($"来源状态：{GetField(detail, "status")}");
        Console.WriteLine($"来源预览：{Preview(detail.Content)}");
    }

    private static string? RequireId(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && !args[1].StartsWith("--", StringComparison.OrdinalIgnoreCase))
        {
            return args[1];
        }

        Console.WriteLine("缺少候选项 ID。");
        PrintHelp();
        return null;
    }

    private static PromotionCandidateStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<PromotionCandidateStatus>(
            value.Replace("_", "", StringComparison.OrdinalIgnoreCase),
            ignoreCase: true,
            out var status)
            ? status
            : null;
    }

    private static void PrintCandidate(PromotionCandidate candidate)
    {
        Console.WriteLine($"ID：{candidate.Id}");
        Console.WriteLine($"状态：{candidate.Status}");
        Console.WriteLine($"来源：{candidate.SourceKind}/{candidate.SourceId}");
        Console.WriteLine($"目标层：{candidate.TargetLayer?.ToString() ?? "-"}");
        Console.WriteLine($"分类：{candidate.Category}");
        Console.WriteLine($"置信度：{candidate.Confidence:0.00}");
        Console.WriteLine($"审核人：{candidate.Reviewer ?? "-"}");
        Console.WriteLine($"创建时间：{candidate.CreatedAt:O}");
        Console.WriteLine($"更新时间：{candidate.UpdatedAt:O}");
        Console.WriteLine($"命中规则：{string.Join(", ", candidate.MatchedRules)}");
        Console.WriteLine($"原因：{candidate.Reason}");
        Console.WriteLine("内容：");
        Console.WriteLine(candidate.Content);
    }

    private static string Preview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var normalized = content.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80] + "...";
    }

    private static string GetField(ControlRoomDetail detail, string name)
    {
        return detail.Fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "-";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("promotion 支持：");
        Console.WriteLine("  promotion list [--status Candidate|Accepted|Rejected|NeedsReview|Superseded] [--take 20]");
        Console.WriteLine("  promotion show <candidateId>");
        Console.WriteLine("  promotion accept <candidateId> [--reviewer name] [--reason text]");
        Console.WriteLine("  promotion reject <candidateId> [--reviewer name] [--reason text]");
        Console.WriteLine("  promotion deprecate <candidateId> [--reviewer name] [--reason text]");
        Console.WriteLine("  promotion explain <candidateId>");
    }
}
