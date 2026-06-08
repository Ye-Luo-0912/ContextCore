using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的短期晋升候选项只读页面。</summary>
public static class ServicePromotionCandidatesScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        PromotionCandidateStatus? status = null;
        string? kind = null;
        string? targetLayer = null;
        double? minConfidence = null;
        double? minImportance = null;
        var limit = 20;
        var offset = 0;

        while (true)
        {
            try
            {
                var snapshot = await service.GetServicePromotionCandidatesSnapshotAsync(
                    status: status,
                    kind: kind,
                    suggestedTargetLayer: targetLayer,
                    minConfidence: minConfidence,
                    minImportance: minImportance,
                    take: limit,
                    offset: offset,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidates(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, Enter/R refresh, G generate, F filter, S <id> detail, E <id> explain, A <id> accept, R <id> reject, X <id> expire, H <id> history: ");
            var input = Console.ReadLine();
            var normalized = (input ?? string.Empty).Trim();
            if (string.Equals(normalized, "g", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var generated = await service.GenerateServiceShortTermPromotionCandidatesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"Generated candidates: {generated.Count}");
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (string.Equals(normalized, "f", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("status (Candidate/NeedsReview/Rejected, empty=all): ");
                var statusInput = Console.ReadLine();
                status = Enum.TryParse<PromotionCandidateStatus>(statusInput, true, out var parsedStatus) ? parsedStatus : null;
                Console.Write("kind (empty=all): ");
                kind = NormalizeEmpty(Console.ReadLine());
                Console.Write("target layer (empty=all): ");
                targetLayer = NormalizeEmpty(Console.ReadLine());
                Console.Write("min confidence (empty=none): ");
                minConfidence = ParseNullableDouble(Console.ReadLine());
                Console.Write("min importance (empty=none): ");
                minImportance = ParseNullableDouble(Console.ReadLine());
                Console.Write("limit (default 20): ");
                limit = ParseIntOrDefault(Console.ReadLine(), 20);
                Console.Write("offset (default 0): ");
                offset = ParseIntOrDefault(Console.ReadLine(), 0);
                continue;
            }

            if (normalized.StartsWith("s ", StringComparison.OrdinalIgnoreCase))
            {
                var candidateId = normalized[2..].Trim();
                try
                {
                    var detail = await service.GetServiceShortTermPromotionCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidateDetail(detail));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (normalized.StartsWith("e ", StringComparison.OrdinalIgnoreCase))
            {
                var candidateId = normalized[2..].Trim();
                try
                {
                    var explanation = await service.ExplainServiceShortTermPromotionCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidateExplanation(explanation));
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
                    "accept",
                    (candidateId, request) => service.AcceptServiceShortTermPromotionCandidateAsync(candidateId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("r ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    normalized[2..].Trim(),
                    "reject",
                    (candidateId, request) => service.RejectServiceShortTermPromotionCandidateAsync(candidateId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("x ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    normalized[2..].Trim(),
                    "expire",
                    (candidateId, request) => service.ExpireServiceShortTermPromotionCandidateAsync(candidateId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("h ", StringComparison.OrdinalIgnoreCase))
            {
                var candidateId = normalized[2..].Trim();
                try
                {
                    var reviews = await service.GetServiceShortTermPromotionCandidateReviewsAsync(candidateId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidateReviews(reviews));
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

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? ParseNullableDouble(string? value)
        => double.TryParse(value, out var parsed) ? parsed : null;

    private static int ParseIntOrDefault(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static async Task ReviewAsync(
        ControlRoomService service,
        string candidateId,
        string action,
        Func<string, ReviewPromotionCandidateRequest, Task<ReviewPromotionCandidateResponse>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            Console.WriteLine("candidate id is required.");
            return;
        }

        try
        {
            var detail = await service.GetServiceShortTermPromotionCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidateDetail(detail));

            var explanation = await service.ExplainServiceShortTermPromotionCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidateExplanation(explanation));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for candidate {candidateId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Review action canceled.");
            return;
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(candidateId, new ReviewPromotionCandidateRequest
            {
                OperationId = $"controlroom-short-term-promotion-{action}-{Guid.NewGuid():N}",
                Reviewer = reviewer,
                Reason = reason,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode"
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderPromotionCandidateReviewResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }
}
