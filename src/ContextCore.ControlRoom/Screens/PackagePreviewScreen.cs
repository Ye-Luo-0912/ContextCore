using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>预览构建上下文包内容与 Token 用量的控制台屏幕。</summary>
public static class PackagePreviewScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("Token budget (default 1200, B/0 back, Q quit, R refresh): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            var args = int.TryParse(action.Value, out var parsed)
                ? new[] { "--token-budget", parsed.ToString() }
                : [];

            await PackagePreviewCommand.ExecuteAsync(service, args, cancellationToken).ConfigureAwait(false);
        }
    }
}
