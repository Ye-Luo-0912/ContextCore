using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>浏览上下文条目并查看详情的控制台屏幕。</summary>
public static class ContextExplorerScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var items = await service.ListAsync("raw", null, null, null, 25, cancellationToken)
                .ConfigureAwait(false);
            TableRenderer.RenderList(items);
            if (items.Count == 0)
            {
                var status = await service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine($"Root: {status.RootPath}");
                Console.WriteLine($"Workspace: {status.WorkspaceId}");
                Console.WriteLine($"Collection: {status.CollectionId}");
                Console.WriteLine("No items found. Check root/workspace/collection or run AppHost seed.");
            }

            Console.Write("Show id (B/0 back, Q quit, R refresh): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            if (action.Kind == ControlRoomActionKind.Refresh)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(action.Value))
            {
                await ShowCommand.ExecuteAsync(service, [action.Value], cancellationToken).ConfigureAwait(false);
                Console.WriteLine();
                Console.Write("B/0 to return, Q to quit, R refresh: ");
                var detailAction = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
                if (detailAction.Kind == ControlRoomActionKind.Quit)
                {
                    return ControlRoomActionKind.Quit;
                }
            }
        }
    }
}
