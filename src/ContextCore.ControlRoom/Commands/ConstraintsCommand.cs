using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>查询并展示约束条目列表的命令。</summary>
public static class ConstraintsCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var level = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "constraints";
        var items = await service.ListAsync("constraints", null, null, level, 100, cancellationToken)
            .ConfigureAwait(false);
        TableRenderer.RenderList(items);
    }
}
