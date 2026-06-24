using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的最小 query 页面。</summary>
public static class ServiceQueryScreen
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

            Console.Write("Take (default 10): ");
            var takeText = Console.ReadLine();
            Console.Write("IncludeContent (true/false, default true): ");
            var includeContentText = Console.ReadLine();

            var request = new ContextQueryRequest
            {
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                QueryText = string.IsNullOrWhiteSpace(action.Value) ? null : action.Value.Trim(),
                Take = int.TryParse(takeText, out var take) && take > 0 ? take : 10,
                IncludeContent = !string.Equals(includeContentText, "false", StringComparison.OrdinalIgnoreCase)
            };

            try
            {
                var response = await service.QueryServiceAsync(request, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationRenderer.RenderQueryResult(response));
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
