using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>对上下文索引执行搜索并展示结果的命令。</summary>
public static class IndexCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (args.Count < 2 || !string.Equals(args[0], "search", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("index supports: search <keyword>");
            return;
        }

        var keyword = args[1];
        var results = await service.SearchIndexAsync(keyword, cancellationToken).ConfigureAwait(false);

        TableRenderer.Render(
            "Index Search",
            ["EntryId", "Kind", "Key", "Weight", "Refs", "Items"],
            [.. results.Select(result => new[]
            {
                result.Entry.Id,
                result.Entry.Kind,
                result.Entry.Key,
                result.Entry.Weight.ToString("0.00"),
                string.Join(",", result.Entry.ContextRefs),
                string.Join(",", result.Items.Select(item => item.Id))
            })]);
    }
}
