namespace ContextCore.ControlRoom.Commands;

/// <summary>解析命令行参数的内部工具类。</summary>
internal static class CommandHelpers
{
    public static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public static int GetIntOption(IReadOnlyList<string> args, string name, int defaultValue)
    {
        return int.TryParse(GetOption(args, name), out var value) ? value : defaultValue;
    }

    public static bool HasFlag(IReadOnlyList<string> args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }
}
