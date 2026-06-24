using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Candidate Memory 治理页面。</summary>
public static class ServiceCandidateMemoryScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceCandidateMemoryPageSnapshotAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemory(snapshot));

                Console.Write("Detail <id> / E <id> / Ready <id> / N <id> / Reject <id> / Expire <id> / Supersede <id> / H <id> / B/0 / Q / R: ");
                var input = Console.ReadLine()?.Trim() ?? string.Empty;
                var action = ControlRoomInteraction.InterpretDetailInput(input);
                if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
                {
                    return action.Kind;
                }

                if (string.IsNullOrWhiteSpace(input)
                    || string.Equals(input, "r", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (input.StartsWith("e ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var explanation = await service.ExplainServiceCandidateMemoryAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemoryExplanation(explanation));
                    }

                    continue;
                }

                if (input.StartsWith("ready ", StringComparison.OrdinalIgnoreCase))
                {
                    await ReviewAsync(
                        service,
                        input[6..].Trim(),
                        CandidateMemoryReviewActions.MarkReadyForStableReview,
                        (candidateId, request) => service.MarkServiceCandidateMemoryReadyForStableReviewAsync(candidateId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("n ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("needs ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("n ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[6..].Trim();
                    await ReviewAsync(
                        service,
                        id,
                        CandidateMemoryReviewActions.NeedsMoreEvidence,
                        (candidateId, request) => service.MarkServiceCandidateMemoryNeedsMoreEvidenceAsync(candidateId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("reject ", StringComparison.OrdinalIgnoreCase))
                {
                    await ReviewAsync(
                        service,
                        input[7..].Trim(),
                        CandidateMemoryReviewActions.Reject,
                        (candidateId, request) => service.RejectServiceCandidateMemoryAsync(candidateId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("expire ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("x ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("x ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[7..].Trim();
                    await ReviewAsync(
                        service,
                        id,
                        CandidateMemoryReviewActions.Expire,
                        (candidateId, request) => service.ExpireServiceCandidateMemoryAsync(candidateId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("supersede ", StringComparison.OrdinalIgnoreCase)
                    || input.StartsWith("u ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input.StartsWith("u ", StringComparison.OrdinalIgnoreCase)
                        ? input[2..].Trim()
                        : input[10..].Trim();
                    await ReviewAsync(
                        service,
                        id,
                        CandidateMemoryReviewActions.Supersede,
                        (candidateId, request) => service.SupersedeServiceCandidateMemoryAsync(candidateId, request, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (input.StartsWith("h ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = input[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        var reviews = await service.GetServiceCandidateMemoryReviewsAsync(id, cancellationToken)
                            .ConfigureAwait(false);
                        Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemoryReviews(reviews));
                    }

                    continue;
                }

                var candidate = await service.GetServiceCandidateMemoryAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemoryDetail(candidate));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }
        }
    }

    private static async Task ReviewAsync(
        ControlRoomService service,
        string candidateId,
        string action,
        Func<string, CandidateMemoryReviewRequest, Task<CandidateMemoryReviewResult>> submit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateId))
        {
            Console.WriteLine("candidate id is required.");
            return;
        }

        CandidateMemoryExplanation explanation;
        try
        {
            var detail = await service.GetServiceCandidateMemoryAsync(candidateId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemoryDetail(detail));
            explanation = await service.ExplainServiceCandidateMemoryAsync(candidateId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemoryExplanation(explanation));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            return;
        }

        Console.Write($"Confirm {action} for candidate memory {candidateId}? Type YES to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Candidate memory review action canceled.");
            return;
        }

        string? supersedeTargetId = null;
        if (string.Equals(action, CandidateMemoryReviewActions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("supersede target candidate id: ");
            supersedeTargetId = NormalizeEmpty(Console.ReadLine());
        }

        Console.Write("reviewer (default=manual): ");
        var reviewer = NormalizeEmpty(Console.ReadLine()) ?? "manual";
        Console.Write("reason: ");
        var reason = NormalizeEmpty(Console.ReadLine()) ?? action;

        try
        {
            var result = await submit(candidateId, new CandidateMemoryReviewRequest
            {
                OperationId = $"controlroom-candidate-memory-{action}-{Guid.NewGuid():N}",
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                Reviewer = reviewer,
                Reason = reason,
                SupersedeTargetCandidateId = supersedeTargetId,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "controlroom-service-mode",
                    ["explainedCandidateId"] = explanation.CandidateId
                }
            }).ConfigureAwait(false);
            Console.WriteLine(ServiceOperationalRenderer.RenderCandidateMemoryReviewResult(result));
        }
        catch (ContextCoreApiException ex)
        {
            Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
        }
    }

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
