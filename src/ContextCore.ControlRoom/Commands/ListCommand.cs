using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>列出上下文条目并支持分页与过滤的命令。</summary>
public static class ListCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var layer = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "raw";
        var type = CommandHelpers.GetOption(args, "--type");
        var tag = CommandHelpers.GetOption(args, "--tag");
        var status = CommandHelpers.GetOption(args, "--status");
        var take = CommandHelpers.GetIntOption(args, "--take", 50);

        var items = await service.ListAsync(layer, type, tag, status, take, cancellationToken)
            .ConfigureAwait(false);
        TableRenderer.RenderList(items);
    }
}
