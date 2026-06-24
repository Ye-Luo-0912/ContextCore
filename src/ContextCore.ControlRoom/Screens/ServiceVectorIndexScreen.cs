using ContextCore.Client;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Vector Index 只读页面。</summary>
public static class ServiceVectorIndexScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceVectorIndexSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderVectorIndex(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("P plan, A apply, R reports, Q query preview, D diagnostics/refresh, B/0 back: ");
            var raw = Console.ReadLine();
            if (string.Equals(raw, "p", StringComparison.OrdinalIgnoreCase))
            {
                await ShowPlanAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(raw, "a", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(raw, "r", StringComparison.OrdinalIgnoreCase))
            {
                await ShowReportsAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(raw, "q", StringComparison.OrdinalIgnoreCase))
            {
                await ShowQueryPreviewAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(raw, "d", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var action = ControlRoomInteraction.InterpretDetailInput(raw);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }
        }
    }

    private static async Task ShowPlanAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await service.CreateServiceVectorReindexPlanAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderVectorReindexPlan(plan));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static async Task ApplyAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await service.CreateServiceVectorReindexPlanAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderVectorReindexPlan(plan));
            Console.Write("Type YES to apply vector reindex job: ");
            var confirmation = Console.ReadLine();
            if (!string.Equals(confirmation, "YES", StringComparison.Ordinal))
            {
                Console.WriteLine("Apply cancelled.");
                return;
            }

            var response = await service.SubmitServiceVectorReindexAsync(new VectorReindexRequest
            {
                Apply = true,
                DryRun = false,
                ConfirmApply = true
            }, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderVectorReindexSubmit(response));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static async Task ShowReportsAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var reports = await service.GetServiceVectorReindexReportsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderVectorReindexReports(reports));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static async Task ShowQueryPreviewAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        try
        {
            Console.Write("Query text: ");
            var queryText = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(queryText))
            {
                Console.WriteLine("Query cancelled.");
                return;
            }

            Console.Write("TopK [10]: ");
            var topKText = Console.ReadLine();
            var topK = int.TryParse(topKText, out var parsedTopK) && parsedTopK > 0 ? parsedTopK : 10;

            Console.Write("Profile [normal-v1]: ");
            var profile = Console.ReadLine();

            Console.Write("Layer filter (optional): ");
            var layer = Console.ReadLine();

            Console.Write("Min similarity (optional): ");
            var minSimilarityText = Console.ReadLine();
            double? minSimilarity = double.TryParse(minSimilarityText, out var parsedMinSimilarity)
                ? parsedMinSimilarity
                : null;

            var result = await service.PreviewServiceVectorQueryAsync(new VectorQueryPreviewRequest
            {
                QueryText = queryText,
                TopK = topK,
                ProfileId = string.IsNullOrWhiteSpace(profile) ? VectorQueryProfileIds.NormalV1 : profile,
                Layer = string.IsNullOrWhiteSpace(layer) ? null : layer,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["createdFrom"] = "controlroom_vector_query_preview"
                }
            }, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderVectorQueryPreview(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }
}
