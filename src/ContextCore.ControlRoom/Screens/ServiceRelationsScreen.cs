using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 relations 页面。</summary>
public static class ServiceRelationsScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("ItemId / E <relationId> explain / Enter global diagnostics (B/0 back, Q quit, R refresh): ");
            var rawInput = Console.ReadLine();
            var trimmed = rawInput?.Trim() ?? string.Empty;
            if (trimmed.StartsWith("E ", StringComparison.OrdinalIgnoreCase))
            {
                var relationId = trimmed[2..].Trim();
                if (string.IsNullOrWhiteSpace(relationId))
                {
                    Console.WriteLine("relationId is required for E explain.");
                    continue;
                }

                try
                {
                    var explain = await service.ExplainServiceRelationAsync(relationId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderRelationExplain(explain));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            var action = ControlRoomInteraction.InterpretDetailInput(rawInput);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            try
            {
                var itemId = action.Kind == ControlRoomActionKind.Refresh || string.IsNullOrWhiteSpace(action.Value)
                    ? null
                    : action.Value.Trim();
                var snapshot = await service.GetServiceRelationsSnapshotAsync(itemId, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderRelations(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }
}
