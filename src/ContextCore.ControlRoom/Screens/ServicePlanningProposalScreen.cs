using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Retrieval Plan Proposal 只读预览页面。</summary>
public static class ServicePlanningProposalScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        string? currentInput = null;

        while (true)
        {
            if (string.IsNullOrWhiteSpace(currentInput))
            {
                Console.Write("Current input (B/0 back, Q quit): ");
                var input = Console.ReadLine();
                var action = ControlRoomInteraction.InterpretDetailInput(input);
                if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
                {
                    return action.Kind;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                currentInput = input.Trim();
            }

            try
            {
                var snapshot = await service.ProposeServiceRetrievalPlanAsync(
                        currentInput,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderPlanningProposal(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, R refresh, N new input: ");
            var next = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (next.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return next.Kind;
            }

            if (next.Kind == ControlRoomActionKind.Value
                && string.Equals(next.Value?.Trim(), "n", StringComparison.OrdinalIgnoreCase))
            {
                currentInput = null;
            }
        }
    }
}
