using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>执行混合检索并展示候选、选中、丢弃和最终包的调试屏幕。</summary>
public static class RetrievalDebugScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("检索 query（B/0 返回，Q 退出，R 刷新）：");
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
                await RetrievalCommand.ExecuteAsync(service, ["debug", "--query", action.Value], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
