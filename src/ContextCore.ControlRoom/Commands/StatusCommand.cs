using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>展示当前工作区和集合的整体状态摘要的命令。</summary>
public static class StatusCommand
{
    public static async Task ExecuteAsync(ControlRoomService service, CancellationToken cancellationToken = default)
    {
        var status = await service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        TableRenderer.RenderStatus(status);
    }
}
