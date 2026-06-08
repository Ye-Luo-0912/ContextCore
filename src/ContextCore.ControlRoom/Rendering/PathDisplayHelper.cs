namespace ContextCore.ControlRoom.Rendering;

/// <summary>对路径字符串进行截断和压缩显示的静态工具类。</summary>
public static class PathDisplayHelper
{
    public static string Compact(string path, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(path) || maxLength <= 0)
        {
            return string.Empty;
        }

        if (path.Length <= maxLength)
        {
            return path;
        }

        if (maxLength <= 8)
        {
            return "..."[..Math.Min(3, maxLength)];
        }

        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var suffix = string.Empty;

        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var candidate = string.IsNullOrEmpty(suffix)
                ? parts[i]
                : $"{parts[i]}{Path.DirectorySeparatorChar}{suffix}";
            var compact = $"...{Path.DirectorySeparatorChar}{candidate}";
            if (compact.Length > maxLength)
            {
                break;
            }

            suffix = candidate;
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            return $"...{Path.DirectorySeparatorChar}{suffix}";
        }

        return "..." + path[^Math.Max(0, maxLength - 3)..];
    }

    public static string CompactId(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 8)
        {
            return value[..maxLength];
        }

        var head = Math.Max(4, (maxLength - 3) / 2);
        var tail = Math.Max(4, maxLength - head - 3);
        return $"{value[..head]}...{value[^tail..]}";
    }
}
