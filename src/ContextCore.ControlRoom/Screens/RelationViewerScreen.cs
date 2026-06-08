using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>浏览条目关系图谱的控制台屏幕。</summary>
public static class RelationViewerScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("Item id (B/0 back, Q quit, R refresh): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (action.Kind == ControlRoomActionKind.Refresh)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(action.Value))
            {
                await RelationsCommand.ExecuteAsync(service, ["show", action.Value], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
