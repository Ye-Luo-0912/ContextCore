using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 constraints 页面。</summary>
public static class ServiceConstraintsScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceConstraintsSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderConstraints(snapshot));
                Console.Write("Detail <id> / P <id> provenance / B/0 / Q / R: ");
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

                if (input.StartsWith("p ", StringComparison.OrdinalIgnoreCase))
                {
                    var itemId = input[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(itemId))
                    {
                        var provenance = await service.GetServiceProvenanceAsync(itemId, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderProvenance(provenance));
                    }

                    continue;
                }

                var item = snapshot.Constraints.FirstOrDefault(value => string.Equals(value.Id, input, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderConstraintDetail(item));
                }
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }
}
