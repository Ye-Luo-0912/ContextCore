using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>查询并展示记忆层条目的命令。</summary>
public static class MemoryCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (args.Count < 2)
        {
            Console.WriteLine("memory 支持：promote <id>, reject <id>, deprecate <id>");
            return;
        }

        var action = args[0];
        var id = args[1];

        if (string.Equals(action, "promote", StringComparison.OrdinalIgnoreCase))
        {
            var record = await service.PromoteAsync(id, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"已晋升 {record.SourceMemoryId}: {record.FromStatus} -> {record.ToStatus}");
            return;
        }

        if (string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase))
        {
            var record = await service.RejectAsync(id, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"已拒绝 {record.SourceMemoryId}: {record.FromStatus} -> {record.ToStatus}");
            return;
        }

        if (string.Equals(action, "deprecate", StringComparison.OrdinalIgnoreCase))
        {
            var record = await service.DeprecateAsync(id, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"已废弃 {record.SourceMemoryId}: {record.FromStatus} -> {record.ToStatus}");
            return;
        }

        Console.WriteLine($"未知 memory 操作：{action}");
    }
}
