using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>查看、创建和编辑上下文包策略的命令。</summary>
public static class PolicyCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var action = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        switch (action)
        {
            case "list":
            case "ls":
                await ListAsync(service, args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
                break;
            case "show":
                await ShowAsync(service, args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
                break;
            case "save-default":
            case "init":
                await SaveDefaultAsync(service, args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
                break;
            case "edit":
            case "set":
                await EditAsync(service, args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
                break;
            default:
                Console.Error.WriteLine($"未知策略命令：{action}");
                PrintHelp();
                Environment.ExitCode = 2;
                break;
        }
    }

    private static async Task ListAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var query = CommandHelpers.GetOption(args, "--query");
        var policies = await service.ListPoliciesAsync(query, cancellationToken).ConfigureAwait(false);
        if (policies.Count == 0)
        {
            Console.WriteLine("当前集合没有已保存的上下文包策略。");
            return;
        }

        TableRenderer.Render(
            "上下文包策略",
            ["Id", "名称", "Token", "最近条目", "节顺序"],
            [.. policies.Select(item => new[]
            {
                item.Id,
                item.Name,
                item.TokenBudget.ToString(),
                item.MaxRecentItems.ToString(),
                item.SectionOrder.Count == 0 ? "默认" : string.Join(",", item.SectionOrder)
            })]);
    }

    private static async Task ShowAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var id = GetIdArgument(args);
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("请提供策略 Id：policy show <id>");
            Environment.ExitCode = 2;
            return;
        }

        var policy = await service.GetPolicyAsync(id, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            Console.Error.WriteLine($"未找到策略：{id}");
            Environment.ExitCode = 1;
            return;
        }

        RenderPolicy(policy);
    }

    private static async Task SaveDefaultAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var id = GetIdArgument(args);
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("请提供策略 Id：policy save-default <id>");
            Environment.ExitCode = 2;
            return;
        }

        var tokenBudget = CommandHelpers.GetIntOption(args, "--token-budget", 1200);
        tokenBudget = CommandHelpers.GetIntOption(args, "--budget", tokenBudget);
        var policy = new ContextPackagePolicy
        {
            Id = id,
            Name = CommandHelpers.GetOption(args, "--name") ?? id,
            Description = "ControlRoom 生成的默认上下文包策略。",
            TokenBudget = tokenBudget,
            IncludeGlobalContext = true,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeRecentRawContext = true,
            MaxRecentItems = 20,
            SectionOrder =
            [
                "hard_constraints",
                "working_memory",
                "global_context",
                "recent_context",
                "stable_memory",
                "soft_constraints",
                "related_context"
            ],
            SectionTokenBudgets = new Dictionary<string, int>
            {
                ["hard_constraints"] = Math.Max(80, tokenBudget / 8),
                ["working_memory"] = Math.Max(120, tokenBudget / 5),
                ["global_context"] = Math.Max(120, tokenBudget / 5),
                ["recent_context"] = Math.Max(160, tokenBudget / 3),
                ["stable_memory"] = Math.Max(120, tokenBudget / 5),
                ["soft_constraints"] = Math.Max(80, tokenBudget / 10)
            },
            Metadata = new Dictionary<string, string>
            {
                ["createdBy"] = "ControlRoom",
                ["mode"] = "default"
            }
        };

        await service.SavePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"已保存策略：{id}");
    }

    private static async Task EditAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var id = GetIdArgument(args);
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("请提供策略 Id：policy edit <id>");
            Environment.ExitCode = 2;
            return;
        }

        var policy = await service.GetPolicyAsync(id, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            Console.Error.WriteLine($"未找到策略：{id}");
            Environment.ExitCode = 1;
            return;
        }

        if (!TryGetBoolOption(args, "--include-global", policy.IncludeGlobalContext, out var includeGlobal, out var error)
            || !TryGetBoolOption(args, "--include-hard", policy.IncludeHardConstraints, out var includeHard, out error)
            || !TryGetBoolOption(args, "--include-soft", policy.IncludeSoftConstraints, out var includeSoft, out error)
            || !TryGetBoolOption(args, "--include-working", policy.IncludeWorkingMemory, out var includeWorking, out error)
            || !TryGetBoolOption(args, "--include-stable", policy.IncludeStableMemory, out var includeStable, out error)
            || !TryGetBoolOption(args, "--include-recent", policy.IncludeRecentRawContext, out var includeRecent, out error))
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 2;
            return;
        }

        if (!TryParseSectionBudgets(
            CommandHelpers.GetOption(args, "--section-budget"),
            policy.SectionTokenBudgets,
            out var sectionBudgets,
            out error))
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 2;
            return;
        }

        var metadata = new Dictionary<string, string>(policy.Metadata)
        {
            ["updatedBy"] = "ControlRoom",
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };

        var updated = new ContextPackagePolicy
        {
            Id = policy.Id,
            WorkspaceId = policy.WorkspaceId,
            CollectionId = policy.CollectionId,
            Name = CommandHelpers.GetOption(args, "--name") ?? policy.Name,
            Description = CommandHelpers.GetOption(args, "--description") ?? policy.Description,
            TokenBudget = CommandHelpers.GetIntOption(args, "--token-budget", policy.TokenBudget),
            IncludeGlobalContext = includeGlobal,
            IncludeHardConstraints = includeHard,
            IncludeSoftConstraints = includeSoft,
            IncludeWorkingMemory = includeWorking,
            IncludeStableMemory = includeStable,
            IncludeRecentRawContext = includeRecent,
            MaxRecentItems = CommandHelpers.GetIntOption(args, "--max-recent-items", policy.MaxRecentItems),
            SectionOrder = ParseSectionOrder(CommandHelpers.GetOption(args, "--section-order"), policy.SectionOrder),
            SectionPriorities = new Dictionary<string, int>(policy.SectionPriorities),
            SectionTokenBudgets = sectionBudgets,
            Metadata = metadata
        };

        await service.SavePolicyAsync(updated, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"已更新策略：{id}");
    }

    private static void RenderPolicy(ContextPackagePolicy policy)
    {
        Console.WriteLine();
        Console.WriteLine($"上下文包策略 {policy.Id}");
        Console.WriteLine(new string('=', 12 + policy.Id.Length));
        Console.WriteLine($"名称       : {policy.Name}");
        Console.WriteLine($"描述       : {policy.Description}");
        Console.WriteLine($"工作区     : {policy.WorkspaceId}");
        Console.WriteLine($"集合       : {policy.CollectionId}");
        Console.WriteLine($"Token 预算 : {policy.TokenBudget}");
        Console.WriteLine($"最近条目数 : {policy.MaxRecentItems}");
        Console.WriteLine($"包含全局   : {FormatBool(policy.IncludeGlobalContext)}");
        Console.WriteLine($"包含硬约束 : {FormatBool(policy.IncludeHardConstraints)}");
        Console.WriteLine($"包含软约束 : {FormatBool(policy.IncludeSoftConstraints)}");
        Console.WriteLine($"包含工作记忆: {FormatBool(policy.IncludeWorkingMemory)}");
        Console.WriteLine($"包含稳定记忆: {FormatBool(policy.IncludeStableMemory)}");
        Console.WriteLine($"包含最近上下文: {FormatBool(policy.IncludeRecentRawContext)}");

        if (policy.SectionOrder.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("节顺序");
            foreach (var section in policy.SectionOrder)
            {
                Console.WriteLine($"  - {section}");
            }
        }

        if (policy.SectionTokenBudgets.Count > 0)
        {
            TableRenderer.Render(
                "节 Token 预算",
                ["节", "Token"],
                policy.SectionTokenBudgets
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new[] { item.Key, item.Value.ToString() })
                    .ToArray());
        }
    }

    private static string FormatBool(bool value)
    {
        return value ? "是" : "否";
    }

    private static string? GetIdArgument(IReadOnlyList<string> args)
    {
        return args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : CommandHelpers.GetOption(args, "--id");
    }

    private static IReadOnlyList<string> ParseSectionOrder(string? raw, IReadOnlyList<string> current)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return current.ToArray();
        }

        if (string.Equals(raw, "clear", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static bool TryGetBoolOption(
        IReadOnlyList<string> args,
        string name,
        bool current,
        out bool value,
        out string? error)
    {
        var raw = CommandHelpers.GetOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = current;
            error = null;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
            case "是":
                value = true;
                error = null;
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
            case "否":
                value = false;
                error = null;
                return true;
            default:
                value = current;
                error = $"选项 {name} 需要布尔值：true/false、yes/no、1/0、是/否。";
                return false;
        }
    }

    private static bool TryParseSectionBudgets(
        string? raw,
        IReadOnlyDictionary<string, int> current,
        out Dictionary<string, int> budgets,
        out string? error)
    {
        budgets = new Dictionary<string, int>(current);
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (string.Equals(raw, "clear", StringComparison.OrdinalIgnoreCase))
        {
            budgets.Clear();
            return true;
        }

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[1], out var value) || value <= 0)
            {
                error = "节预算格式应为 section=token，多个配置用逗号分隔，例如：recent_context=600,working_memory=400。";
                return false;
            }

            budgets[parts[0]] = value;
        }

        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("用法：policy list | policy show <id> | policy save-default <id> --token-budget 1200 | policy edit <id> --name 名称");
    }
}