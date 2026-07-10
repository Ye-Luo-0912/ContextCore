using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// Shared markdown helpers extracted from the preflight runners to eliminate
/// triplicated <c>AppendList</c> implementations (GRAPH-06 governance redundancy).
/// </summary>
internal static class PreflightReportHelpers
{
    internal static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }
}
