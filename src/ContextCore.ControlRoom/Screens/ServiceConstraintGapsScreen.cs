using ContextCore.Abstractions;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Constraint Gaps 只读页面。</summary>
public static class ServiceConstraintGapsScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceConstraintGapsSnapshotAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderConstraintGaps(snapshot));
                Console.Write("B/0 back, Q quit, Enter refresh, S <id> detail, A <id> accept, R <id> reject, H <id> history: ");
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

                if (input.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
                {
                    await ReviewAsync(
                        service,
                        input[2..].Trim(),
                        "accept",
                        (gapId, request) => service.AcceptServiceConstraintGapAsync(gapId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("r ", StringComparison.OrdinalIgnoreCase))
                {
                    await ReviewAsync(
                        service,
                        input[2..].Trim(),
                        "reject",
                        (gapId, request) => service.RejectServiceConstraintGapAsync(gapId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("h ", StringComparison.OrdinalIgnoreCase))
                {
                    var gapId = input[2..].Trim();
                    var reviews = await service.GetServiceConstraintGapReviewsAsync(gapId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderConstraintGapReviews(reviews));
                    continue;
                }

                var detailInput = input.StartsWith("s ", StringComparison.OrdinalIgnoreCase)
                    ? input[2..].Trim()
                    : input;

                var detail = snapshot.Gaps.FirstOrDefault(value => string.Equals(value.GapId, detailInput, StringComparison.OrdinalIgnoreCase))
                    ?? await service.GetServiceConstraintGapAsync(detailInput, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderConstraintGapDetail(detail));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }

    private static async Task ReviewAsync(
        ControlRoomService service,
        string gapId,
        string action,
        Func<string, ConstraintGapReviewRequest, Task<ConstraintGapReviewResult>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gapId))
        {
            Console.WriteLine("gap id is required.");
            return;
        }

        try
        {
            var detail = await service.GetServiceConstraintGapAsync(gapId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderConstraintGapDetail(detail));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for constraint gap {gapId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Constraint gap review action canceled.");
            return;
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(gapId, new ConstraintGapReviewRequest
            {
                OperationId = $"controlroom-constraint-gap-{action}-{Guid.NewGuid():N}",
                Reviewer = reviewer,
                Reason = reason,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode"
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderConstraintGapReviewResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
