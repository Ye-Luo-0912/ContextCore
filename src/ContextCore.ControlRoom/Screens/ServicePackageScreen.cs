using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 package preview / build 页面。</summary>
public static class ServicePackageScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("QueryText (B/0 back, Q quit): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            Console.Write("TokenBudget (default 1200): ");
            var tokenBudgetText = Console.ReadLine();

            var request = new ContextPackageRequest
            {
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                QueryText = string.IsNullOrWhiteSpace(action.Value) ? null : action.Value.Trim(),
                TokenBudget = int.TryParse(tokenBudgetText, out var tokenBudget) && tokenBudget > 0 ? tokenBudget : 1200
            };

            try
            {
                var result = await service.BuildServicePackageAsync(request, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationRenderer.RenderPackageResult(result));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationRenderer.RenderError(ex));
            }

            Console.Write("B/0 返回, Q 退出, Enter 继续: ");
            var next = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (next.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return next.Kind;
            }
        }
    }
}
