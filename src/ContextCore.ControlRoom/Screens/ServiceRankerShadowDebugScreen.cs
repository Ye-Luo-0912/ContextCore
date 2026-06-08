using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 lifecycle-aware ranker shadow debug 页面。</summary>
public static class ServiceRankerShadowDebugScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        string? query = null;
        string mode = "ChatMode";

        while (true)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Console.Write("Query (B/0 back, Q quit): ");
                var input = Console.ReadLine();
                var action = ControlRoomInteraction.InterpretDetailInput(input);
                if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
                {
                    return action.Kind;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                query = input.Trim();
                Console.Write("Mode (optional, default ChatMode): ");
                var modeInput = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(modeInput))
                {
                    mode = modeInput.Trim();
                }
            }

            try
            {
                var snapshot = await service.DebugServiceLifecycleAwareRankerAsync(
                        query,
                        mode,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderRankerShadowDebug(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, R refresh, N new query: ");
            var next = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (next.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return next.Kind;
            }

            if (next.Kind == ControlRoomActionKind.Value
                && string.Equals(next.Value?.Trim(), "n", StringComparison.OrdinalIgnoreCase))
            {
                query = null;
                mode = "ChatMode";
            }
        }
    }
}
