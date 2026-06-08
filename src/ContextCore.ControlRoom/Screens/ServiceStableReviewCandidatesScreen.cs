using ContextCore.Abstractions;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Stable Review 候选项只读页面。</summary>
public static class ServiceStableReviewCandidatesScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        string? status = null;
        string? validationStatus = null;
        string? kind = null;
        string? stableTarget = null;
        var limit = 20;
        var offset = 0;

        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceStableReviewCandidatesSnapshotAsync(
                    status: status,
                    validationStatus: validationStatus,
                    kind: kind,
                    suggestedStableTarget: stableTarget,
                    take: limit,
                    offset: offset,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewCandidates(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, Enter/R refresh, G generate, F filter, S <id> detail, E <id> explain, P <id> provenance, A <id> accept, R <id> reject, H <id> history: ");
            var input = Console.ReadLine();
            var normalized = (input ?? string.Empty).Trim();

            if (string.Equals(normalized, "g", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var generated = await service.GenerateServiceStableReviewCandidatesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"Generated stable review candidates: {generated.Count}");
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (string.Equals(normalized, "f", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("status (Candidate/NeedsMoreEvidence/Blocked/Accepted/Rejected, empty=all): ");
                status = NormalizeEmpty(Console.ReadLine());
                Console.Write("validation (ReadyForReview/NeedsMoreEvidence/DuplicateStableCandidate/ScopeMismatch, empty=all): ");
                validationStatus = NormalizeEmpty(Console.ReadLine());
                Console.Write("kind (empty=all): ");
                kind = NormalizeEmpty(Console.ReadLine());
                Console.Write("stable target (StableMemory/StableConstraint/DecisionRecord, empty=all): ");
                stableTarget = NormalizeEmpty(Console.ReadLine());
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
                    var detail = await service.GetServiceStableReviewCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewCandidateDetail(detail));
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
                    var explanation = await service.ExplainServiceStableReviewCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewCandidateExplanation(explanation));
                }
                catch (ContextCoreApiException ex)
                {
                    Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
                }

                continue;
            }

            if (normalized.StartsWith("p ", StringComparison.OrdinalIgnoreCase))
            {
                var itemId = normalized[2..].Trim();
                try
                {
                    var provenance = await service.GetServiceProvenanceAsync(itemId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderProvenance(provenance));
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
                    (candidateId, request) => service.AcceptServiceStableReviewCandidateAsync(candidateId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("r ", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewAsync(
                    service,
                    normalized[2..].Trim(),
                    "reject",
                    (candidateId, request) => service.RejectServiceStableReviewCandidateAsync(candidateId, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (normalized.StartsWith("h ", StringComparison.OrdinalIgnoreCase))
            {
                var candidateId = normalized[2..].Trim();
                try
                {
                    var reviews = await service.GetServiceStableReviewCandidateReviewsAsync(candidateId, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewCandidateReviews(reviews));
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

    private static int ParseIntOrDefault(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static async Task ReviewAsync(
        ControlRoomService service,
        string candidateId,
        string action,
        Func<string, StableReviewDecisionRequest, Task<StableReviewDecisionResult>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            Console.WriteLine("stable review candidate id is required.");
            return;
        }

        try
        {
            var detail = await service.GetServiceStableReviewCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewCandidateDetail(detail));

            var explanation = await service.ExplainServiceStableReviewCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewCandidateExplanation(explanation));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for stable review candidate {candidateId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Stable review action canceled.");
            return;
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(candidateId, new StableReviewDecisionRequest
            {
                OperationId = $"controlroom-stable-review-{action}-{Guid.NewGuid():N}",
                Reviewer = reviewer,
                Reason = reason,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode"
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderStableReviewDecisionResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }
}
