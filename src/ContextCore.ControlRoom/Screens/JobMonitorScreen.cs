using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>监控后台作业队列状态的控制台屏幕。</summary>
public static class JobMonitorScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await JobsCommand.ExecuteAsync(service, [], cancellationToken).ConfigureAwait(false);
            Console.Write("B/0 back, Q quit, R refresh: ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }
        }
    }
}
