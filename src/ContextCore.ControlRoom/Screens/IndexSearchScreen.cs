using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>对上下文索引执行关键词搜索并展示结果的控制台屏幕。</summary>
public static class IndexSearchScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("Keyword (B/0 back, Q quit, R refresh): ");
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
                await IndexCommand.ExecuteAsync(service, ["search", action.Value], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
