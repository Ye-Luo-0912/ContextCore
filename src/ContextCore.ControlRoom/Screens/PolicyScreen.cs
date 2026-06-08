using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>查看上下文包策略的控制台屏幕。</summary>
public static class PolicyScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await PolicyCommand.ExecuteAsync(service, ["list"], cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            Console.Write("输入策略 Id 查看，输入 B/0 返回，输入 Q 退出：");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (string.IsNullOrWhiteSpace(action.Value))
            {
                continue;
            }

            await PolicyCommand.ExecuteAsync(service, ["show", action.Value], cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            Console.Write("输入 B/0 返回列表，输入 Q 退出：");
            var next = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (next.Kind == ControlRoomActionKind.Quit)
            {
                return ControlRoomActionKind.Quit;
            }
        }
    }
}