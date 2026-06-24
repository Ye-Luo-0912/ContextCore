using ContextCore.Client;
using ContextCore.Abstractions.Models;
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
            Console.Write("ItemId / P profiles / X expansion preview / E <relationId> / V <relationId> review / R <relationId> reject / X <relationId> deprecate / N <relationId> needs-evidence / H <relationId> history / Enter global diagnostics (B/0 back, Q quit, R refresh): ");
            var rawInput = Console.ReadLine();
            var trimmed = rawInput?.Trim() ?? string.Empty;
            if (string.Equals(trimmed, "P", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var profiles = await service.GetServiceRelationExpansionProfilesAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderRelationExpansionProfiles(profiles));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (string.Equals(trimmed, "X", StringComparison.OrdinalIgnoreCase))
            {
                await PreviewExpansionAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }

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

            if (trimmed.StartsWith("V ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    trimmed[2..].Trim(),
                    RelationReviewActions.Review,
                    (relationId, request) => service.ReviewServiceRelationAsync(relationId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (trimmed.StartsWith("R ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    trimmed[2..].Trim(),
                    RelationReviewActions.Reject,
                    (relationId, request) => service.RejectServiceRelationAsync(relationId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (trimmed.StartsWith("X ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    trimmed[2..].Trim(),
                    RelationReviewActions.Deprecate,
                    (relationId, request) => service.DeprecateServiceRelationAsync(relationId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (trimmed.StartsWith("N ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    trimmed[2..].Trim(),
                    RelationReviewActions.MarkNeedsEvidence,
                    (relationId, request) => service.MarkServiceRelationNeedsEvidenceAsync(relationId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (trimmed.StartsWith("H ", StringComparison.OrdinalIgnoreCase))
            {
                var relationId = trimmed[2..].Trim();
                if (string.IsNullOrWhiteSpace(relationId))
                {
                    Console.WriteLine("relationId is required for H history.");
                    continue;
                }

                try
                {
                    var reviews = await service.GetServiceRelationReviewsAsync(relationId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderRelationReviews(reviews));
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

    private static async Task PreviewExpansionAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        Console.Write("itemId: ");
        var itemId = NormalizeEmpty(Console.ReadLine());
        if (string.IsNullOrWhiteSpace(itemId))
        {
            Console.WriteLine("itemId is required for expansion preview.");
            return;
        }

        Console.Write("profileId (default=normal-v1): ");
        var profileId = NormalizeEmpty(Console.ReadLine()) ?? "normal-v1";
        try
        {
            var preview = await service.PreviewServiceRelationExpansionAsync(
                    itemId,
                    profileId,
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderRelationExpansionPreview(preview));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static async Task ReviewAsync(
        ControlRoomService service,
        string relationId,
        string action,
        Func<string, RelationReviewRequest, Task<RelationReviewResult>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relationId))
        {
            Console.WriteLine("relationId is required.");
            return;
        }

        RelationExplainResponse explain;
        try
        {
            explain = await service.ExplainServiceRelationAsync(relationId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderRelationExplain(explain));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for relation {relationId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Relation review action canceled.");
            return;
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(relationId, new RelationReviewRequest
            {
                OperationId = $"controlroom-relation-{action}-{Guid.NewGuid():N}",
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                Reviewer = reviewer,
                Reason = reason,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode",
                    ["explainedRelationId"] = explain.RelationId
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderRelationReviewResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
