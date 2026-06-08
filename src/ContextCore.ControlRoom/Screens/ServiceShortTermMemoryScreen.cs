using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的短期记忆只读页面。</summary>
public static class ServiceShortTermMemoryScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceShortTermMemorySnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderShortTermMemory(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, R refresh, C compact, A archive: ");
            var input = Console.ReadLine();
            var normalized = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "c")
            {
                try
                {
                    var result = await service.CompactServiceShortTermMemoryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderShortTermCompactionResult(result));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (normalized == "a")
            {
                try
                {
                    var summary = await service.GetServiceShortTermArchiveSummaryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    var items = await service.GetServiceShortTermArchiveItemsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderShortTermArchiveSummary(summary));
                    Console.WriteLine();
                    Console.WriteLine(ServiceOperationalRenderer.RenderShortTermArchiveItems(items));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            var action = ControlRoomInteraction.InterpretDetailInput(input);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }
        }
    }
}
