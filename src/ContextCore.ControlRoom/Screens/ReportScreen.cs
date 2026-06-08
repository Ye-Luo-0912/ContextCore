using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>生成并展示 Markdown 格式报告的控制台屏幕。</summary>
public static class ReportScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("Output path (blank default, B/0 back, Q quit, R refresh): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            var args = string.IsNullOrWhiteSpace(action.Value)
                ? new[] { "export" }
                : new[] { "export", "--out", action.Value };

            await ReportCommand.ExecuteAsync(service, args, cancellationToken).ConfigureAwait(false);
        }
    }
}
