using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Screens;

/// <summary>展示约束条目列表并处理用户交互的控制台屏幕。</summary>
public static class ConstraintScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await ConstraintsCommand.ExecuteAsync(service, [], cancellationToken).ConfigureAwait(false);
            Console.Write("Show id or level (B/0 back, Q quit, R refresh): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (action.Kind == ControlRoomActionKind.Refresh)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(action.Value))
            {
                continue;
            }

            if (Enum.TryParse<ConstraintLevel>(action.Value, ignoreCase: true, out _))
            {
                await ConstraintsCommand.ExecuteAsync(service, [action.Value], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ShowCommand.ExecuteAsync(service, [action.Value], cancellationToken).ConfigureAwait(false);
            }

            Console.WriteLine();
            Console.Write("B/0 to return, Q to quit, R refresh: ");
            var detailAction = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (detailAction.Kind == ControlRoomActionKind.Quit)
            {
                return ControlRoomActionKind.Quit;
            }
        }
    }
}
