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
            case "migrate":
                await ExecuteMigrateAsync(service, rest, cancellationToken);
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
            Console.WriteLine("用法：relations filter <id> [--type <relationType>] [--min-confidence <0..1>] [--exclude-lifecycle <a,b,c>] [--exclude-review-status <a,b,c>] [--depth N] [--direction outgoing|incoming|both]");
            return;
        }

        var itemId = args[0];
        var typeOption = CommandHelpers.GetOption(args, "--type");
        var depth = CommandHelpers.GetIntOption(args, "--depth", 2);
        var direction = CommandHelpers.GetOption(args, "--direction") ?? "both";
        var minConfidence = CommandHelpers.GetDoubleOption(args, "--min-confidence", 0.0);
        var excludeLifecycles = ParseCommaList(CommandHelpers.GetOption(args, "--exclude-lifecycle"));
        var excludeReviewStatuses = ParseCommaList(CommandHelpers.GetOption(args, "--exclude-review-status"));

        string[]? allowedTypes = string.IsNullOrWhiteSpace(typeOption) ? null : [typeOption];
        var subgraph = await service.GetRelationSubgraphAsync(
            itemId, depth, direction, allowedTypes, cancellationToken).ConfigureAwait(false);

        if (minConfidence > 0 || excludeLifecycles.Length > 0 || excludeReviewStatuses.Length > 0)
        {
            subgraph = FilterSubgraph(subgraph, minConfidence, excludeLifecycles, excludeReviewStatuses);
        }

        RenderSubgraph(subgraph);
    }

    private static string[] ParseCommaList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>GRAPH-13: 在客户端对子图边进行后置过滤（按置信度/生命周期/审核状态）。</summary>
    private static RelationSubgraph FilterSubgraph(
        RelationSubgraph subgraph,
        double minConfidence,
        string[] excludeLifecycles,
        string[] excludeReviewStatuses)
    {
        var filteredEdges = subgraph.Edges
            .Where(e => e.Confidence >= minConfidence)
            .Where(e => !excludeLifecycles.Any(x => string.Equals(x, e.Lifecycle, StringComparison.OrdinalIgnoreCase)))
            .Where(e => !excludeReviewStatuses.Any(x => string.Equals(x, e.ReviewStatus, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var referencedNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        referencedNodeIds.Add(subgraph.RootItemId);
        foreach (var edge in filteredEdges)
        {
            referencedNodeIds.Add(edge.SourceId);
            referencedNodeIds.Add(edge.TargetId);
        }

        var filteredNodes = subgraph.Nodes
            .Where(n => referencedNodeIds.Contains(n.ItemId))
            .ToArray();

        return new RelationSubgraph
        {
            RootItemId = subgraph.RootItemId,
            Nodes = filteredNodes,
            Edges = filteredEdges,
            MaxDepthReached = subgraph.MaxDepthReached,
            Truncated = subgraph.Truncated,
            Warnings = subgraph.Warnings
        };
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

        var nodeLookup = subgraph.Nodes.ToDictionary(static n => n.ItemId, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine();
        Console.WriteLine("节点：");
        foreach (var node in subgraph.Nodes.OrderBy(static n => n.Depth).ThenBy(static n => n.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            var indent = new string(' ', node.Depth * 2);
            var kind = string.IsNullOrWhiteSpace(node.NodeKind) ? string.Empty : $" [{node.NodeKind}]";
            var lifecycle = string.IsNullOrWhiteSpace(node.Lifecycle) ? string.Empty : $" ({node.Lifecycle})";
            var title = string.IsNullOrWhiteSpace(node.Title) ? string.Empty : $" \"{node.Title}\"";
            Console.WriteLine($"{indent}depth={node.Depth}  {node.ItemId}{kind}{lifecycle}{title}");
        }

        Console.WriteLine();
        Console.WriteLine("边：");
        foreach (var edge in subgraph.Edges.OrderBy(static e => e.Depth).ThenBy(static e => e.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            var indent = new string(' ', edge.Depth * 2);
            var lifecycle = string.IsNullOrWhiteSpace(edge.Lifecycle) ? string.Empty : $" lifecycle={edge.Lifecycle}";
            var review = string.IsNullOrWhiteSpace(edge.ReviewStatus) ? string.Empty : $" review={edge.ReviewStatus}";
            var sourceTitle = nodeLookup.TryGetValue(edge.SourceId, out var src) && !string.IsNullOrWhiteSpace(src.Title) ? $" \"{src.Title}\"" : string.Empty;
            var targetTitle = nodeLookup.TryGetValue(edge.TargetId, out var tgt) && !string.IsNullOrWhiteSpace(tgt.Title) ? $" \"{tgt.Title}\"" : string.Empty;
            Console.WriteLine($"{indent}{edge.SourceId}{sourceTitle} -[{edge.RelationType}]-> {edge.TargetId}{targetTitle}  w={edge.Weight:0.##} c={edge.Confidence:0.##}{lifecycle}{review}");
        }
    }

    private static async Task ExecuteMigrateAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var collectionId = CommandHelpers.GetOption(args, "--collection");
        var apply = CommandHelpers.HasFlag(args, "--apply") || CommandHelpers.HasFlag(args, "--confirm");

        var report = await service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = collectionId,
            Apply = apply
        }, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(report.DryRun ? "关系迁移 dry-run（未写入，使用 --apply 实际落盘）：" : "关系旧数据迁移完成：");
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            Console.WriteLine($"  collection 范围：{collectionId}");
        }
        Console.WriteLine($"  扫描关系总数：{report.TotalRelations}");
        Console.WriteLine($"  待更新/已更新关系：{report.UpdatedRelations}");
        Console.WriteLine($"  已是最新跳过：{report.SkippedRelations}");
        Console.WriteLine($"  NodeKind 回填：{report.NodeKindBackfilled} 次");
        Console.WriteLine($"  Lifecycle 回填：{report.LifecycleBackfilled} 次");
        Console.WriteLine($"  ReviewStatus 回填：{report.ReviewStatusBackfilled} 次");
        Console.WriteLine($"  Provenance 回填：{report.ProvenanceBackfilled} 次");
        if (report.DryRun && report.UpdatedRelations > 0)
        {
            Console.WriteLine("  提示：本次为 dry-run，重新执行并追加 --apply 以实际写入变更。");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("relations 子命令：");
        Console.WriteLine("  show <id>                                                    显示条目的直接出入关系");
        Console.WriteLine("  expand <id> [--depth N] [--direction outgoing|incoming|both] 以指定深度展开关系子图（默认 depth=2，direction=both）");
        Console.WriteLine("  filter <id> [--type <relationType>] [--min-confidence <0..1>] [--exclude-lifecycle <a,b,c>] [--exclude-review-status <a,b,c>] [--depth N] [--direction …] 按类型/置信度/生命周期/审核状态过滤子图");
        Console.WriteLine("  chain <id> [--depth N] [--direction …]                       替换链视图（沿 SupersededBy/Replaces 遍历）");
        Console.WriteLine("  conflicts <id> [--depth N] [--direction …]                   冲突视图（沿 ConflictsWith/Contradicts 遍历）");
        Console.WriteLine("  migrate [--collection <id>] [--apply|--confirm]              P3.1-d：回填旧关系数据正式字段；默认 dry-run，--apply 实际写入；--collection 限定集合范围");
        Console.WriteLine("  help                                                         显示本帮助");
    }
}
