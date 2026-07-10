using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>查询并展示上下文条目关系的命令。</summary>
public static class RelationsCommand
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

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        switch (subcommand.ToLowerInvariant())
        {
            case "show":
                await ExecuteShowAsync(service, rest, cancellationToken);
                return;
            case "expand":
                await ExecuteExpandAsync(service, rest, cancellationToken);
                return;
            case "filter":
                await ExecuteFilterAsync(service, rest, cancellationToken);
                return;
            case "chain":
                await ExecuteChainAsync(service, rest, cancellationToken);
                return;
            case "conflicts":
                await ExecuteConflictsAsync(service, rest, cancellationToken);
                return;
            case "help":
            case "-h":
            case "--help":
                PrintHelp();
                return;
            default:
                Console.WriteLine($"未知子命令：{subcommand}");
                PrintHelp();
                return;
        }
    }

    private static async Task ExecuteShowAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            Console.WriteLine("用法：relations show <id>");
            return;
        }

        var graph = await service.GetRelationGraphAsync(args[0], cancellationToken)
            .ConfigureAwait(false);
        TreeRenderer.RenderRelationGraph(graph);
    }

    private static async Task ExecuteExpandAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            Console.WriteLine("用法：relations expand <id> [--depth N] [--direction outgoing|incoming|both]");
            return;
        }

        var itemId = args[0];
        var depth = CommandHelpers.GetIntOption(args, "--depth", 2);
        var direction = CommandHelpers.GetOption(args, "--direction") ?? "both";

        var subgraph = await service.GetRelationSubgraphAsync(
            itemId, depth, direction, null, cancellationToken).ConfigureAwait(false);
        RenderSubgraph(subgraph);
    }

    private static async Task ExecuteFilterAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            Console.WriteLine("用法：relations filter <id> --type <relationType> [--depth N] [--direction outgoing|incoming|both]");
            return;
        }

        var itemId = args[0];
        var typeOption = CommandHelpers.GetOption(args, "--type");
        if (string.IsNullOrWhiteSpace(typeOption))
        {
            Console.WriteLine("filter 子命令需要 --type <relationType> 参数。");
            return;
        }

        var depth = CommandHelpers.GetIntOption(args, "--depth", 2);
        var direction = CommandHelpers.GetOption(args, "--direction") ?? "both";

        var subgraph = await service.GetRelationSubgraphAsync(
            itemId, depth, direction, [typeOption], cancellationToken).ConfigureAwait(false);
        RenderSubgraph(subgraph);
    }

    private static async Task ExecuteChainAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            Console.WriteLine("用法：relations chain <id> [--depth N] [--direction outgoing|incoming|both]");
            return;
        }

        var itemId = args[0];
        var depth = CommandHelpers.GetIntOption(args, "--depth", 5);
        var direction = CommandHelpers.GetOption(args, "--direction") ?? "both";

        var subgraph = await service.GetRelationSubgraphAsync(
            itemId,
            depth,
            direction,
            [ContextRelationTypes.SupersededBy, ContextRelationTypes.Replaces, ContextRelationTypes.ReplacedBy, ContextRelationTypes.Supersedes],
            cancellationToken).ConfigureAwait(false);
        RenderSubgraph(subgraph);
    }

    private static async Task ExecuteConflictsAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            Console.WriteLine("用法：relations conflicts <id> [--depth N] [--direction outgoing|incoming|both]");
            return;
        }

        var itemId = args[0];
        var depth = CommandHelpers.GetIntOption(args, "--depth", 3);
        var direction = CommandHelpers.GetOption(args, "--direction") ?? "both";

        var subgraph = await service.GetRelationSubgraphAsync(
            itemId,
            depth,
            direction,
            [ContextRelationTypes.ConflictsWith, ContextRelationTypes.Contradicts],
            cancellationToken).ConfigureAwait(false);
        RenderSubgraph(subgraph);
    }

    private static void RenderSubgraph(RelationSubgraph subgraph)
    {
        Console.WriteLine($"根条目：{subgraph.RootItemId}");
        Console.WriteLine($"节点数：{subgraph.Nodes.Count}，边数：{subgraph.Edges.Count}，最大深度：{subgraph.MaxDepthReached}{(subgraph.Truncated ? "（已截断）" : string.Empty)}");

        if (subgraph.Warnings.Count > 0)
        {
            Console.WriteLine("警告：");
            foreach (var warning in subgraph.Warnings)
            {
                Console.WriteLine($"  - {warning}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("节点：");
        foreach (var node in subgraph.Nodes)
        {
            var kind = string.IsNullOrWhiteSpace(node.NodeKind) ? string.Empty : $" [{node.NodeKind}]";
            Console.WriteLine($"  depth={node.Depth}  {node.ItemId}{kind}");
        }

        Console.WriteLine();
        Console.WriteLine("边：");
        foreach (var edge in subgraph.Edges)
        {
            var lifecycle = string.IsNullOrWhiteSpace(edge.Lifecycle) ? string.Empty : $" lifecycle={edge.Lifecycle}";
            var review = string.IsNullOrWhiteSpace(edge.ReviewStatus) ? string.Empty : $" review={edge.ReviewStatus}";
            Console.WriteLine($"  depth={edge.Depth}  {edge.SourceId} -[{edge.RelationType}]-> {edge.TargetId}  w={edge.Weight:0.##} c={edge.Confidence:0.##}{lifecycle}{review}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("relations 子命令：");
        Console.WriteLine("  show <id>                                                    显示条目的直接出入关系");
        Console.WriteLine("  expand <id> [--depth N] [--direction outgoing|incoming|both] 以指定深度展开关系子图（默认 depth=2，direction=both）");
        Console.WriteLine("  filter <id> --type <relationType> [--depth N] [--direction …] 按关系类型过滤展开子图");
        Console.WriteLine("  chain <id> [--depth N] [--direction …]                       替换链视图（沿 SupersededBy/Replaces 遍历）");
        Console.WriteLine("  conflicts <id> [--depth N] [--direction …]                   冲突视图（沿 ConflictsWith/Contradicts 遍历）");
        Console.WriteLine("  help                                                         显示本帮助");
    }
}
