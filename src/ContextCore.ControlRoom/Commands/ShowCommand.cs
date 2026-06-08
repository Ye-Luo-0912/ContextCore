using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>按 ID 查询并展示单个上下文条目详情的命令。</summary>
public static class ShowCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var id = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(id))
        {
            await Console.Error.WriteLineAsync("show requires an id.");
            return;
        }

        var detail = await service.ShowAsync(id, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            await Console.Error.WriteLineAsync($"No ContextCore object found for id '{id}'.");
            return;
        }

        DetailRenderer.Render(detail);
    }
}
