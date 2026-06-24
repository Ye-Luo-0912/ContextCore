using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 model status / route resolve 页面。</summary>
public static class ServiceModelScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        ContextCoreModelRouteResolveRequest? routeRequest = null;

        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceModelSnapshotAsync(routeRequest, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderModel(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("Resolve / B/0 / Q / R: ");
            var input = Console.ReadLine()?.Trim() ?? string.Empty;
            var action = ControlRoomInteraction.InterpretDetailInput(input);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (string.IsNullOrWhiteSpace(input) || string.Equals(input, "r", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(input, "resolve", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("Role: ");
                var role = Console.ReadLine();
                Console.Write("TaskKind: ");
                var taskKind = Console.ReadLine();
                Console.Write("ThinkingMode: ");
                var thinkingMode = Console.ReadLine();
                Console.Write("RequiredCapabilities comma-separated: ");
                var capabilities = Console.ReadLine();

                routeRequest = new ContextCoreModelRouteResolveRequest
                {
                    Role = role,
                    TaskKind = taskKind,
                    ThinkingMode = thinkingMode,
                    RequiredCapabilities = ServiceIngestScreen.SplitCommaSeparated(capabilities)
                };
            }
        }
    }
}
