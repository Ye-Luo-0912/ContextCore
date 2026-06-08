using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>查看和操作工作记忆与稳定记忆层的控制台屏幕。</summary>
public static class MemoryLayerScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var summary = await service.GetMemoryStatusBreakdownAsync(cancellationToken).ConfigureAwait(false);
            TableRenderer.RenderMemoryStatusBreakdown(summary);
            Console.Write("Layer (working/structured/candidate/stable, B/0 back, Q quit, R refresh): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            var layer = action.Kind == ControlRoomActionKind.Refresh || string.IsNullOrWhiteSpace(action.Value)
                ? "working"
                : action.Value;

            await ListCommand.ExecuteAsync(service, [layer], cancellationToken).ConfigureAwait(false);
        }
    }
}
