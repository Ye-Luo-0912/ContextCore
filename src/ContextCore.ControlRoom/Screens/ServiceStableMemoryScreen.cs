using ContextCore.Client;
using ContextCore.Abstractions;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Stable Memory 生命周期治理页面。</summary>
public static class ServiceStableMemoryScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceStableMemoryPageSnapshotAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderStableMemory(snapshot));

                Console.Write("Detail <id> / E <id> / P <id> / C <id> / X <id> / S <id> / R <id> / H <id> / B/0 / Q / Refresh: ");
                var input = Console.ReadLine()?.Trim() ?? string.Empty;
                var action = ControlRoomInteraction.InterpretDetailInput(input);
                if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
                {
                    return action.Kind;
                }

                if (action.Kind == ControlRoomActionKind.Refresh
                    || string.IsNullOrWhiteSpace(input)
                    || string.Equals(input, "refresh", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (input.StartsWith("e ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var explanation = await service.ExplainServiceStableMemoryAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderStableMemoryExplanation(explanation));
                    }

                    continue;
                }

                if (input.StartsWith("p ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var provenance = await service.GetServiceProvenanceAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderProvenance(provenance));
                    }

                    continue;
                }

                if (input.StartsWith("c ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("chain ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("c ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[6..].Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var chain = await service.GetServiceStableReplacementChainAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderStableReplacementChain(chain));
                    }

                    continue;
                }

                if (input.StartsWith("x ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("deprecate ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("x ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[10..].Trim();
                    await ReviewAsync(
                        service,
                        id,
                        StableLifecycleReviewActions.Deprecate,
                        (itemId, request) => service.DeprecateServiceStableMemoryAsync(itemId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("s ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("supersede ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("s ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[10..].Trim();
                    await ReviewAsync(
                        service,
                        id,
                        StableLifecycleReviewActions.Supersede,
                        (itemId, request) => service.SupersedeServiceStableMemoryAsync(itemId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("r ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("reject ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("r ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[7..].Trim();
                    await ReviewAsync(
                        service,
                        id,
                        StableLifecycleReviewActions.Reject,
                        (itemId, request) => service.RejectServiceStableMemoryAsync(itemId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("h ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var reviews = await service.GetServiceStableMemoryReviewsAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderStableLifecycleReviews(reviews));
                    }

                    continue;
                }

                var detail = await service.ExplainServiceStableMemoryAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderStableMemoryDetail(detail.StableItem));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }

    private static async Task ReviewAsync(
        ControlRoomService service,
        string itemId,
        string action,
        Func<string, StableLifecycleReviewRequest, Task<StableLifecycleReviewResult>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            Console.WriteLine("stable item id is required.");
            return;
        }

        StableMemoryExplanation explanation;
        try
        {
            explanation = await service.ExplainServiceStableMemoryAsync(itemId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderStableMemoryDetail(explanation.StableItem));
            Console.WriteLine(ServiceOperationalRenderer.RenderStableMemoryExplanation(explanation));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for stable item {itemId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Stable lifecycle review action canceled.");
            return;
        }

        string? replacementItemId = null;
        if (string.Equals(action, StableLifecycleReviewActions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("replacement stable item id: ");
            replacementItemId = NormalizeEmpty(Console.ReadLine());
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(itemId, new StableLifecycleReviewRequest
            {
                OperationId = $"controlroom-stable-memory-{action}-{Guid.NewGuid():N}",
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                Reviewer = reviewer,
                Reason = reason,
                ReplacementItemId = replacementItemId,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode",
                    ["explainedStableItemId"] = explanation.StableItemId
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderStableLifecycleReviewResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
