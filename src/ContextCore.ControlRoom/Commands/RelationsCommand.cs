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
        if (args.Count >= 2 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
        {
            var graph = await service.GetRelationGraphAsync(args[1], cancellationToken)
                .ConfigureAwait(false);
            TreeRenderer.RenderRelationGraph(graph);
            return;
        }

        Console.WriteLine("relations supports: show <id>");
    }
}
