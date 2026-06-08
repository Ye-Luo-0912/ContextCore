using ContextCore.Abstractions;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的最小 ingest 页面。</summary>
public static class ServiceIngestScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Console.Write("Content (B/0 back, Q quit): ");
            var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }

            var content = action.Value ?? string.Empty;
            Console.Write("Source (default controlroom-service): ");
            var source = Console.ReadLine();
            Console.Write("InputKind (default note): ");
            var inputKind = Console.ReadLine();
            Console.Write("ContentFormat (PlainText/Markdown/Json... default PlainText): ");
            var contentFormatText = Console.ReadLine();
            Console.Write("SourceRefs comma-separated (optional): ");
            var sourceRefsText = Console.ReadLine();
            Console.Write("Metadata k=v,k2=v2 (optional): ");
            var metadataText = Console.ReadLine();

            var command = new ContextInputCommand
            {
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                Source = string.IsNullOrWhiteSpace(source) ? "controlroom-service" : source.Trim(),
                InputKind = string.IsNullOrWhiteSpace(inputKind) ? "note" : inputKind.Trim(),
                ContentFormat = ParseContentFormat(contentFormatText),
                Content = content,
                SourceRefs = SplitCommaSeparated(sourceRefsText),
                Metadata = ParseMetadata(metadataText)
            };

            try
            {
                var result = await service.IngestServiceAsync(command, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(ServiceOperationRenderer.RenderIngestResult(result));
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

    private static ContextContentFormat ParseContentFormat(string? value)
    {
        return Enum.TryParse<ContextContentFormat>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ContextContentFormat.PlainText;
    }

    internal static IReadOnlyList<string> SplitCommaSeparated(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static Dictionary<string, string> ParseMetadata(string? value)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return metadata;
        }

        foreach (var segment in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                metadata[parts[0]] = parts[1];
            }
        }

        return metadata;
    }
}
