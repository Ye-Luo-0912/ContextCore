using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 memory 页面。</summary>
public static class ServiceMemoryScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceMemorySnapshotAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderMemory(snapshot));

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

                var memory = snapshot.Working
                    .Concat(snapshot.Candidates)
                    .Concat(snapshot.Stable)
                    .FirstOrDefault(item => string.Equals(item.Id, input, StringComparison.OrdinalIgnoreCase));
                if (memory is not null)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderMemoryDetail(memory));
                    continue;
                }

                var global = snapshot.Global.FirstOrDefault(item => string.Equals(item.Id, input, StringComparison.OrdinalIgnoreCase));
                if (global is not null)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderGlobalMemoryDetail(global));
                }
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }
}
