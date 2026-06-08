using ContextCore.Abstractions;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 jobs 页面。</summary>
public static class ServiceJobsScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceJobsSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderJobs(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("Detail <jobId> / Requeue <jobId> / B/0 / Q / R: ");
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

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Equals("requeue", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var requeued = await service.RequeueServiceJobAsync(parts[1], cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"Requeued: {requeued.OriginalJobId} -> {requeued.NewJobId}");
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            var jobId = parts.Length == 2 && parts[0].Equals("detail", StringComparison.OrdinalIgnoreCase)
                ? parts[1]
                : input;
            try
            {
                var job = await service.GetServiceJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderJobDetail(job));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }
}
