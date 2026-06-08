using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Candidate Constraints review 页面。</summary>
public static class ServiceCandidateConstraintsScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate;
        var limit = 20;
        var offset = 0;

        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceCandidateConstraintsSnapshotAsync(
                    status,
                    limit,
                    offset,
                    cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderCandidateConstraints(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, Enter/R refresh, F filter, S <id> detail, A <id> activate, R <id> reject, H <id> history: ");
            var input = Console.ReadLine();
            var normalized = (input ?? string.Empty).Trim();
            var action = ControlRoomInteraction.InterpretDetailInput(input);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "r", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(normalized, "f", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("status (Candidate/Active/Rejected, empty=Candidate): ");
                status = Enum.TryParse<ContextMemoryStatus>(Console.ReadLine(), ignoreCase: true, out var parsed)
                    ? parsed
                    : ContextMemoryStatus.Candidate;
                Console.Write("limit (default 20): ");
                limit = ParseIntOrDefault(Console.ReadLine(), 20);
                Console.Write("offset (default 0): ");
                offset = ParseIntOrDefault(Console.ReadLine(), 0);
                continue;
            }

            if (normalized.StartsWith("s ", StringComparison.OrdinalIgnoreCase))
            {
                var constraintId = normalized[2..].Trim();
                try
                {
                    var detail = await service.GetServiceCandidateConstraintAsync(constraintId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderCandidateConstraintDetail(detail));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (normalized.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    normalized[2..].Trim(),
                    "activate",
                    (constraintId, request) => service.ActivateServiceCandidateConstraintAsync(constraintId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("r ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("x ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    normalized[2..].Trim(),
                    "reject",
                    (constraintId, request) => service.RejectServiceCandidateConstraintAsync(constraintId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("h ", StringComparison.OrdinalIgnoreCase))
            {
                var constraintId = normalized[2..].Trim();
                try
                {
                    var reviews = await service.GetServiceCandidateConstraintReviewsAsync(constraintId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderCandidateConstraintReviews(reviews));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }
            }
        }
    }

    private static async Task ReviewAsync(
        ControlRoomService service,
        string constraintId,
        string action,
        Func<string, CandidateConstraintReviewRequest, Task<CandidateConstraintReviewResult>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(constraintId))
        {
            Console.WriteLine("candidate constraint id is required.");
            return;
        }

        try
        {
            var detail = await service.GetServiceCandidateConstraintAsync(constraintId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderCandidateConstraintDetail(detail));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for candidate constraint {constraintId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Candidate constraint review action canceled.");
            return;
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(constraintId, new CandidateConstraintReviewRequest
            {
                OperationId = $"controlroom-candidate-constraint-{action}-{Guid.NewGuid():N}",
                Reviewer = reviewer,
                Reason = reason,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode"
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderCandidateConstraintReviewResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseIntOrDefault(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;
}
